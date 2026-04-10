/// macOS FSEvents P/Invoke bindings and managed wrapper.
/// Only loaded/used on macOS — guarded by RuntimeInformation.IsOSPlatform checks in Watcher.fs.
module FsHotWatch.MacFsEvents

open System
open System.Runtime.InteropServices
open System.Threading
open FsHotWatch.Logging

// ─── Native library paths ───────────────────────────────────────────
[<Literal>]
let private CoreServicesLib =
    "/System/Library/Frameworks/CoreServices.framework/CoreServices"

[<Literal>]
let private CoreFoundationLib =
    "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"

// ─── Constants ──────────────────────────────────────────────────────
[<Literal>]
let private kFSEventStreamEventIdSinceNow = 0xFFFFFFFFFFFFFFFFUL

[<Literal>]
let private kFSEventStreamCreateFlagFileEvents = 0x00000010u

[<Literal>]
let private kFSEventStreamCreateFlagNoDefer = 0x00000002u

[<Literal>]
let private kCFStringEncodingUTF8 = 0x08000100u

// Event flags we care about (kFSEventStreamEventFlagItem*)
[<Literal>]
let private ItemCreated = 0x00000100u

[<Literal>]
let private ItemRemoved = 0x00000200u

[<Literal>]
let private ItemRenamed = 0x00000800u

[<Literal>]
let private ItemModified = 0x00001000u

[<Literal>]
let private ItemIsFile = 0x00010000u

[<Literal>]
let private MustScanSubDirs = 0x00000001u

// ─── Public pure functions for flag interpretation ─────────────────

/// FSEvent flag constants exposed for testing and external use.
[<RequireQualifiedAccess>]
module EventFlags =
    /// Item is a file (not a directory).
    [<Literal>]
    let ItemIsFile = 0x00010000u

    /// Item was created.
    [<Literal>]
    let ItemCreated = 0x00000100u

    /// Item was removed.
    [<Literal>]
    let ItemRemoved = 0x00000200u

    /// Item was renamed.
    [<Literal>]
    let ItemRenamed = 0x00000800u

    /// Item was modified.
    [<Literal>]
    let ItemModified = 0x00001000u

    /// Kernel coalesced events — must re-scan subdirectories.
    [<Literal>]
    let MustScanSubDirs = 0x00000001u

/// Result of classifying an FSEvent flag set.
[<RequireQualifiedAccess>]
type EventClassification =
    /// A file-level change event (create, remove, rename, or modify).
    | FileChange
    /// Kernel coalesced events — directory must be re-scanned.
    | CoalescedScan
    /// Event we don't care about (directory events, metadata-only, etc.).
    | Ignored

/// Classify an FSEvent flag set into the kind of event we should handle.
let classifyEvent (flags: uint32) =
    if
        (flags &&& EventFlags.ItemIsFile <> 0u)
        && (flags
            &&& (EventFlags.ItemCreated
                 ||| EventFlags.ItemRemoved
                 ||| EventFlags.ItemRenamed
                 ||| EventFlags.ItemModified)
            <> 0u)
    then
        EventClassification.FileChange
    elif flags &&& EventFlags.MustScanSubDirs <> 0u then
        EventClassification.CoalescedScan
    else
        EventClassification.Ignored

/// Check if an FSEvent flag set indicates a file event we care about.
let isFileChangeEvent (flags: uint32) =
    classifyEvent flags = EventClassification.FileChange

/// Check if an FSEvent flag set indicates a coalesced/must-scan event.
let isMustScanEvent (flags: uint32) =
    classifyEvent flags = EventClassification.CoalescedScan

// ─── Callback delegate ──────────────────────────────────────────────
[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private FSEventStreamCallback =
    delegate of
        streamRef: nativeint *
        clientInfo: nativeint *
        numEvents: nativeint *
        eventPaths: nativeint *
        eventFlags: nativeint *
        eventIds: nativeint ->
            unit

// ─── P/Invoke: CoreFoundation ───────────────────────────────────────
[<DllImport(CoreFoundationLib)>]
extern nativeint private CFStringCreateWithCString(
    nativeint alloc,
    [<MarshalAs(UnmanagedType.LPUTF8Str)>] string cStr,
    uint32 encoding
)

[<DllImport(CoreFoundationLib)>]
extern nativeint private CFArrayCreate(nativeint alloc, nativeint[] values, nativeint numValues, nativeint callBacks)

[<DllImport(CoreFoundationLib)>]
extern void private CFRelease(nativeint cf)

[<DllImport(CoreFoundationLib)>]
extern nativeint private CFRunLoopGetCurrent()

[<DllImport(CoreFoundationLib)>]
extern void private CFRunLoopRun()

[<DllImport(CoreFoundationLib)>]
extern void private CFRunLoopStop(nativeint runLoop)

// ─── P/Invoke: FSEvents (CoreServices) ──────────────────────────────
[<DllImport(CoreServicesLib)>]
extern nativeint private FSEventStreamCreate(
    nativeint allocator,
    FSEventStreamCallback callback,
    nativeint context,
    nativeint pathsToWatch,
    uint64 sinceWhen,
    double latency,
    uint32 flags
)

[<DllImport(CoreServicesLib)>]
extern void private FSEventStreamScheduleWithRunLoop(nativeint streamRef, nativeint runLoop, nativeint runLoopMode)

[<DllImport(CoreServicesLib)>]
extern [<return: MarshalAs(UnmanagedType.U1)>] bool private FSEventStreamStart(nativeint streamRef)

[<DllImport(CoreServicesLib)>]
extern void private FSEventStreamStop(nativeint streamRef)

[<DllImport(CoreServicesLib)>]
extern void private FSEventStreamInvalidate(nativeint streamRef)

[<DllImport(CoreServicesLib)>]
extern void private FSEventStreamRelease(nativeint streamRef)

// ─── Helpers ────────────────────────────────────────────────────────

/// Create a CFArray of CFStrings from the given paths. Caller must CFRelease the array
/// and each string.
let private createCFStringArray (paths: string list) =
    let cfStrings =
        paths
        |> List.map (fun p -> CFStringCreateWithCString(nativeint 0, p, kCFStringEncodingUTF8))
        |> List.toArray

    if cfStrings |> Array.exists (fun s -> s = nativeint 0) then
        // Release any successfully created strings before failing
        for s in cfStrings do
            if s <> nativeint 0 then
                CFRelease(s)

        failwith "CFStringCreateWithCString returned null — failed to create CFString for a watched path"

    let arr =
        CFArrayCreate(nativeint 0, cfStrings, nativeint cfStrings.Length, nativeint 0)

    arr, cfStrings

// ─── FsEventStream ──────────────────────────────────────────────────

/// Managed wrapper around a native FSEventStream.
/// Watches the given directories for file-level events and invokes the callback
/// with each changed file path.
///
/// Uses FSEventStreamScheduleWithRunLoop on a dedicated background thread that
/// runs CFRunLoopRun(). This is required for event delivery in .NET CLI processes,
/// which have no CoreFoundation run loop running by default. FSEventStreamSetDispatchQueue
/// (GCD-based) fails silently on macOS 15 without an active CFRunLoop in the process,
/// because FSEvents' internal Mach port sources require a run loop consumer.
type FsEventStream
    (
        directories: string list,
        onFileEvent: string -> unit,
        onCoalescedEvent: (string -> unit) option,
        latencySeconds: float
    ) =
    let mutable disposed = false
    let mutable streamRef = nativeint 0
    let mutable runLoopRef = nativeint 0
    let mutable runLoopModeRef = nativeint 0
    let mutable callbackHandle = Unchecked.defaultof<GCHandle>
    let mutable cfStringRefs: nativeint array = [||]
    let mutable cfArrayRef = nativeint 0

    let callback =
        FSEventStreamCallback(fun _streamRef _clientInfo numEvents eventPaths eventFlags _eventIds ->
            if not (Volatile.Read(&disposed)) then
                let count = int numEvents
                debug "fsevents" $"callback fired: %d{count} events"

                for i in 0 .. count - 1 do
                    let flags = uint32 (Marshal.ReadInt32(eventFlags + nativeint (i * 4)))

                    match classifyEvent flags with
                    | EventClassification.FileChange ->
                        let pathPtr = Marshal.ReadIntPtr(eventPaths, i * IntPtr.Size)
                        let path = Marshal.PtrToStringUTF8(pathPtr)

                        if not (isNull path) then
                            try
                                onFileEvent path
                            with ex ->
                                debug "fsevents" $"callback exception: %s{ex.Message}"
                    | EventClassification.CoalescedScan ->
                        let pathPtr = Marshal.ReadIntPtr(eventPaths, i * IntPtr.Size)
                        let path = Marshal.PtrToStringUTF8(pathPtr)
                        debug "fsevents" $"MustScanSubDirs event for: %s{path}"

                        match onCoalescedEvent with
                        | Some handler ->
                            try
                                handler path
                            with ex ->
                                debug "fsevents" $"coalesced event handler exception: %s{ex.Message}"
                        | None -> ()
                    | EventClassification.Ignored -> debug "fsevents" $"filtered event: flags=0x%08X{flags}")

    let cleanup () =
        if streamRef <> nativeint 0 then
            FSEventStreamStop(streamRef)
            FSEventStreamInvalidate(streamRef)

            // Stop the dedicated run loop so its thread exits CFRunLoopRun()
            let rl = Volatile.Read(&runLoopRef)

            if rl <> nativeint 0 then
                CFRunLoopStop(rl)

            FSEventStreamRelease(streamRef)
            streamRef <- nativeint 0

        runLoopRef <- nativeint 0

        if runLoopModeRef <> nativeint 0 then
            CFRelease(runLoopModeRef)
            runLoopModeRef <- nativeint 0

        for s in cfStringRefs do
            if s <> nativeint 0 then
                CFRelease(s)

        cfStringRefs <- [||]

        if cfArrayRef <> nativeint 0 then
            CFRelease(cfArrayRef)
            cfArrayRef <- nativeint 0

        if callbackHandle.IsAllocated then
            callbackHandle.Free()

    do
        if directories.IsEmpty then
            invalidArg "directories" "At least one directory is required"

        try
            // Pin the callback delegate so GC doesn't collect it while native code holds a reference
            callbackHandle <- GCHandle.Alloc(callback)

            let arr, strs = createCFStringArray directories
            cfArrayRef <- arr
            cfStringRefs <- strs

            let flags = kFSEventStreamCreateFlagFileEvents ||| kFSEventStreamCreateFlagNoDefer

            streamRef <-
                FSEventStreamCreate(
                    nativeint 0,
                    callback,
                    nativeint 0,
                    cfArrayRef,
                    kFSEventStreamEventIdSinceNow,
                    latencySeconds,
                    flags
                )

            if streamRef = nativeint 0 then
                failwith "FSEventStreamCreate returned null — failed to create FSEvents stream"

            // kCFRunLoopDefaultMode is a CFString constant — create it by value
            runLoopModeRef <- CFStringCreateWithCString(nativeint 0, "kCFRunLoopDefaultMode", kCFStringEncodingUTF8)

            if runLoopModeRef = nativeint 0 then
                failwith "CFStringCreateWithCString returned null — failed to create kCFRunLoopDefaultMode"

            // Start a dedicated background thread that owns the CFRunLoop for event delivery.
            // FSEventStreamScheduleWithRunLoop requires an active CFRunLoop to pump events;
            // CFRunLoopRun() on this thread provides that pump.
            let capturedStreamRef = streamRef
            let capturedModeRef = runLoopModeRef
            let runLoopReady = new ManualResetEventSlim(false)

            let runLoopThread =
                Thread(fun () ->
                    // Get this thread's run loop — must be called from the thread that will run it
                    let rl = CFRunLoopGetCurrent()
                    Volatile.Write(&runLoopRef, rl)

                    // Schedule the stream on this thread's run loop before signalling
                    FSEventStreamScheduleWithRunLoop(capturedStreamRef, rl, capturedModeRef)

                    // Signal the constructor that scheduling is complete
                    runLoopReady.Set()

                    // Block here, pumping CFRunLoop events until CFRunLoopStop() is called in cleanup()
                    CFRunLoopRun()
                    debug "fsevents" "CFRunLoopRun() returned — run loop thread exiting")

            runLoopThread.IsBackground <- true
            runLoopThread.Name <- "com.fshotwatch.fsevents.runloop"
            runLoopThread.Start()

            // Wait until the run loop thread has scheduled the stream
            runLoopReady.Wait()

            // Start the stream — safe to call from any thread once the stream is scheduled
            if not (FSEventStreamStart(streamRef)) then
                failwith "FSEventStreamStart returned false — failed to start FSEvents stream"

            info
                "fsevents"
                $"FSEventStream started: watching %d{directories.Length} dirs, latency=%.3f{latencySeconds}s"

            for dir in directories do
                info "fsevents" $"  watching: %s{dir}"
        with ex ->
            cleanup ()
            raise ex

    /// Returns true if the stream was successfully created and started.
    member _.IsRunning =
        Volatile.Read(&streamRef) <> nativeint 0 && not (Volatile.Read(&disposed))

    interface IDisposable with
        member _.Dispose() =
            if not (Volatile.Read(&disposed)) then
                Volatile.Write(&disposed, true)
                cleanup ()

/// Create an FsEventStream watching the given directories.
/// The callback is invoked on the dedicated CFRunLoop thread with each changed file path.
let create (directories: string list) (onFileEvent: string -> unit) =
    new FsEventStream(directories, onFileEvent, None, 0.05)

/// Create an FsEventStream with a handler for coalesced (MustScanSubDirs) events.
let createWithCoalesced (directories: string list) (onFileEvent: string -> unit) (onCoalescedEvent: string -> unit) =
    new FsEventStream(directories, onFileEvent, Some onCoalescedEvent, 0.05)
