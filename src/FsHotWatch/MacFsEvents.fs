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
let private FSEventStreamEventIdSinceNow = 0xFFFFFFFFFFFFFFFFUL

[<Literal>]
let private FSEventStreamCreateFlagFileEvents = 0x00000010u

[<Literal>]
let private FSEventStreamCreateFlagNoDefer = 0x00000002u

[<Literal>]
let private CFStringEncodingUTF8 = 0x08000100u

/// The most directory prefixes `FSEventStreamSetExclusionPaths` accepts.
[<Literal>]
let MaxExclusionPaths = 8

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

/// Every `kFSEventStreamEventFlag*` bit, by name.
let private flagNames =
    [ 0x00000001u, "MustScanSubDirs"
      0x00000002u, "UserDropped"
      0x00000004u, "KernelDropped"
      0x00000008u, "EventIdsWrapped"
      0x00000010u, "HistoryDone"
      0x00000020u, "RootChanged"
      0x00000040u, "Mount"
      0x00000080u, "Unmount"
      0x00000100u, "ItemCreated"
      0x00000200u, "ItemRemoved"
      0x00000400u, "ItemInodeMetaMod"
      0x00000800u, "ItemRenamed"
      0x00001000u, "ItemModified"
      0x00002000u, "ItemFinderInfoMod"
      0x00004000u, "ItemChangeOwner"
      0x00008000u, "ItemXattrMod"
      0x00010000u, "ItemIsFile"
      0x00020000u, "ItemIsDir"
      0x00040000u, "ItemIsSymlink"
      0x00080000u, "OwnEvent"
      0x00100000u, "ItemIsHardlink"
      0x00200000u, "ItemIsLastHardlink"
      0x00400000u, "ItemCloned" ]

/// The flag set as hex plus the names of its set bits, so a must-scan names its
/// trigger (UserDropped, KernelDropped, RootChanged, ...).
let internal describeFlags (flags: uint32) : string =
    let names =
        flagNames |> List.filter (fun (bit, _) -> flags &&& bit <> 0u) |> List.map snd

    let unknown =
        flags &&& ~~~(flagNames |> List.fold (fun all (bit, _) -> all ||| bit) 0u)

    let named =
        if unknown <> 0u then
            names @ [ $"0x%08X{unknown}" ]
        else
            names

    $"0x%08X{flags} [%s{String.Join(' ', named)}]"

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
[<DllImport(CoreFoundationLib, EntryPoint = "CFStringCreateWithCString")>]
extern nativeint private cfStringCreateWithCString(
    nativeint alloc,
    [<MarshalAs(UnmanagedType.LPUTF8Str)>] string cStr,
    uint32 encoding
)

[<DllImport(CoreFoundationLib, EntryPoint = "CFArrayCreate")>]
extern nativeint private cfArrayCreate(nativeint alloc, nativeint[] values, nativeint numValues, nativeint callBacks)

[<DllImport(CoreFoundationLib, EntryPoint = "CFRelease")>]
extern void private cfRelease(nativeint cf)

[<DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopGetCurrent")>]
extern nativeint private cfRunLoopGetCurrent()

[<DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopRun")>]
extern void private cfRunLoopRun()

[<DllImport(CoreFoundationLib, EntryPoint = "CFRunLoopStop")>]
extern void private cfRunLoopStop(nativeint runLoop)

// ─── P/Invoke: FSEvents (CoreServices) ──────────────────────────────
[<DllImport(CoreServicesLib, EntryPoint = "FSEventStreamCreate")>]
extern nativeint private fsEventStreamCreate(
    nativeint allocator,
    FSEventStreamCallback callback,
    nativeint context,
    nativeint pathsToWatch,
    uint64 sinceWhen,
    double latency,
    uint32 flags
)

/// At most `MaxExclusionPaths` directory prefixes the kernel drops before dispatch.
/// Must be called before the stream starts.
[<DllImport(CoreServicesLib, EntryPoint = "FSEventStreamSetExclusionPaths")>]
extern [<return: MarshalAs(UnmanagedType.U1)>] bool private fsEventStreamSetExclusionPaths(
    nativeint streamRef,
    nativeint pathsToExclude
)

[<DllImport(CoreServicesLib, EntryPoint = "FSEventStreamScheduleWithRunLoop")>]
extern void private fsEventStreamScheduleWithRunLoop(nativeint streamRef, nativeint runLoop, nativeint runLoopMode)

[<DllImport(CoreServicesLib, EntryPoint = "FSEventStreamStart")>]
extern [<return: MarshalAs(UnmanagedType.U1)>] bool private fsEventStreamStart(nativeint streamRef)

[<DllImport(CoreServicesLib, EntryPoint = "FSEventStreamStop")>]
extern void private fsEventStreamStop(nativeint streamRef)

[<DllImport(CoreServicesLib, EntryPoint = "FSEventStreamInvalidate")>]
extern void private fsEventStreamInvalidate(nativeint streamRef)

[<DllImport(CoreServicesLib, EntryPoint = "FSEventStreamRelease")>]
extern void private fsEventStreamRelease(nativeint streamRef)

// ─── Helpers ────────────────────────────────────────────────────────

/// Create a CFArray of CFStrings from the given paths. Caller must CFRelease the array
/// and each string.
let private createCFStringArray (paths: string list) =
    let cfStrings =
        paths
        |> List.map (fun p -> cfStringCreateWithCString (nativeint 0, p, CFStringEncodingUTF8))
        |> List.toArray

    if cfStrings |> Array.exists (fun s -> s = nativeint 0) then
        for s in cfStrings do
            if s <> nativeint 0 then
                cfRelease (s)

        failwith "CFStringCreateWithCString returned null — failed to create CFString for a watched path"

    let arr =
        cfArrayCreate (nativeint 0, cfStrings, nativeint cfStrings.Length, nativeint 0)

    arr, cfStrings

// ─── Event drain ────────────────────────────────────────────────────

/// One unit of work a stream's drain hands its handler.
[<RequireQualifiedAccess>]
type DrainItem =
    /// A file-level event for this path.
    | File of path: string
    /// A must-scan (coalesced) event: the subtree under this directory must be rescanned.
    | Rescan of directory: string

/// What a drain has taken and handed on since it started.
type DrainStats =
    {
        /// Items posted (each one an event the native callback took off fseventsd).
        Received: int64
        /// Posts folded into an identical item that was still waiting.
        Coalesced: int64
        /// Handler invocations that returned or threw.
        Delivered: int64
        /// Items waiting for the handler.
        Pending: int
    }

/// Takes events off a stream's native callback and runs the handler on a thread of its
/// own (AUTOMATION-1543).
///
/// fseventsd keeps every event it has queued for a client until that client's callback
/// returns, and its memory grows with events times slow clients. The handler behind a
/// daemon's stream reads and hashes the file and then waits, up to the store's admission
/// bound, for the daemon to accept the change — slowest exactly while a build or FCS is
/// busy. Run on the callback, that wait stopped the stream from draining. Here `Post` is
/// a constant-time enqueue, so the callback returns at once and the backlog waits
/// in-process instead.
///
/// The backlog is deduplicated: an item already waiting is not queued twice, so it is
/// bounded by the number of distinct paths, not by the number of events. An item is
/// removed from the waiting set when the handler takes it, so a change that lands while
/// the handler is reading a file is delivered again. Items are handed on in the order
/// first posted. A handler that throws is logged and the next item is delivered.
type EventDrain(handler: DrainItem -> unit, threadName: string) =
    let gate = obj ()
    let queue = System.Collections.Generic.Queue<DrainItem>()
    let waiting = System.Collections.Generic.HashSet<DrainItem>()
    let mutable closing = false
    let mutable received = 0L
    let mutable coalesced = 0L
    let mutable delivered = 0L

    let next () =
        lock gate (fun () ->
            while queue.Count = 0 && not closing do
                Monitor.Wait gate |> ignore

            if closing then
                None
            else
                let item = queue.Dequeue()
                waiting.Remove item |> ignore
                Some item)

    let run () =
        let mutable item = next ()

        while item.IsSome do
            try
                handler item.Value
            with ex ->
                debug "fsevents" $"event handler exception: %s{ex.Message}"

            lock gate (fun () -> delivered <- delivered + 1L)
            item <- next ()

    let thread = Thread(run, IsBackground = true, Name = threadName)
    do thread.Start()

    /// Queue `item` for the handler. Never waits on the handler; ignored once disposed.
    member _.Post(item: DrainItem) =
        lock gate (fun () ->
            if not closing then
                received <- received + 1L

                if waiting.Add item then
                    queue.Enqueue item
                    Monitor.Pulse gate
                else
                    coalesced <- coalesced + 1L)

    /// Counts since the drain started.
    member _.Stats() : DrainStats =
        lock gate (fun () ->
            { Received = received
              Coalesced = coalesced
              Delivered = delivered
              Pending = queue.Count })

    /// True once disposal has begun.
    member _.Closing = lock gate (fun () -> closing)

    /// Drop what is waiting, let the item under way finish, and stop the thread. From
    /// the handler itself it returns at once; the thread exits after that item.
    interface IDisposable with
        member _.Dispose() =
            let first =
                lock gate (fun () ->
                    if closing then
                        false
                    else
                        closing <- true
                        queue.Clear()
                        waiting.Clear()
                        Monitor.PulseAll gate
                        true)

            if first && not (obj.ReferenceEquals(thread, Thread.CurrentThread)) then
                if not (thread.Join 5000) then
                    warn "fsevents" $"%s{threadName}: the event handler did not return within 5s of dispose"

// ─── FsEventStream ──────────────────────────────────────────────────

/// macOS refused one of the native stream's setup calls. Refusals are the transient
/// class of native failure (`FSEventStreamStart` returning false on a
/// healthy `fseventsd`), so watcher setup retries them with bounded backoff before
/// failing over to polling; every other exception fails over at once.
type internal NativeStreamRefusedException(message: string) =
    inherit Exception(message)

/// `FSEventStreamCreate` returned a null stream.
type internal CreateFailedException() =
    inherit NativeStreamRefusedException("FSEventStreamCreate returned null — failed to create FSEvents stream")

/// The native stream was created and scheduled, but macOS refused to start it.
/// Kept distinct so watcher setup can fail over without string-matching a native error.
type internal StartFailedException() =
    inherit NativeStreamRefusedException("FSEventStreamStart returned false — failed to start FSEvents stream")

/// Serializes native teardown and keeps release behind run-loop termination.
/// A timed-out join leaks rather than releasing memory a live native thread may use.
type internal NativeCleanupCoordinator
    (
        stopStream: unit -> unit,
        invalidateStream: unit -> unit,
        stopRunLoop: unit -> unit,
        getRunLoopThread: unit -> Thread,
        releaseResources: unit -> unit
    ) =
    let gate = obj ()
    let mutable started = false
    let mutable cleanupRequested = false
    let mutable released = false

    let releaseOnce () =
        lock gate (fun () ->
            if not released then
                released <- true
                releaseResources ())

    member _.MarkStarted() = lock gate (fun () -> started <- true)

    member _.RunLoopExited() =
        let shouldRelease = lock gate (fun () -> cleanupRequested)

        if shouldRelease then
            releaseOnce ()

    member _.Cleanup() =
        let thread =
            lock gate (fun () ->
                if not cleanupRequested then
                    cleanupRequested <- true

                    if started then
                        stopStream ()
                        started <- false

                    invalidateStream ()
                    stopRunLoop ()

                getRunLoopThread ())

        if isNull thread then
            releaseOnce ()
        elif obj.ReferenceEquals(thread, Thread.CurrentThread) then
            // Joining self would deadlock. The run-loop thread calls
            // RunLoopExited after CFRunLoopRun returns and releases there.
            ()
        elif thread.ThreadState &&& ThreadState.Unstarted <> enum<ThreadState> 0 then
            releaseOnce ()
        elif thread.Join(5000) then
            releaseOnce ()
        else
            warn "fsevents" "run-loop thread did not exit within 5s; native resources retained until it exits"

/// Managed wrapper around a native FSEventStream.
/// Watches the given directories for file-level events and invokes the callback
/// with each changed file path, on its own drain thread (`EventDrain`).
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
        latencySeconds: float,
        ?exclusionPaths: string list
    ) =
    let exclusionPaths = defaultArg exclusionPaths []
    let mutable disposed = 0
    let mutable streamRef = nativeint 0
    let mutable runLoopRef = nativeint 0
    let mutable runLoopModeRef = nativeint 0
    let mutable runLoopThread: Thread = null
    let mutable callbackHandle = Unchecked.defaultof<GCHandle>
    let mutable cfStringRefs: nativeint array = [||]
    let mutable cfArrayRef = nativeint 0
    let mutable exclusionStringRefs: nativeint array = [||]
    let mutable exclusionArrayRef = nativeint 0

    // The handlers run here, never on the run loop: the native callback only posts, so
    // it returns as soon as it has copied the batch's paths and fseventsd can release
    // them however long a handler takes.
    let drain =
        new EventDrain(
            (fun item ->
                match item with
                | DrainItem.File path -> onFileEvent path
                | DrainItem.Rescan dir -> onCoalescedEvent |> Option.iter (fun handler -> handler dir)),
            "com.fshotwatch.fsevents.drain"
        )

    let callback =
        FSEventStreamCallback(fun _streamRef _clientInfo numEvents eventPaths eventFlags _eventIds ->
            if Volatile.Read(&disposed) = 0 then
                let count = int numEvents
                debug "fsevents" $"callback fired: %d{count} events"

                for i in 0 .. count - 1 do
                    let flags = uint32 (Marshal.ReadInt32(eventFlags + nativeint (i * 4)))

                    match classifyEvent flags with
                    | EventClassification.FileChange ->
                        let pathPtr = Marshal.ReadIntPtr(eventPaths, i * IntPtr.Size)
                        let path = Marshal.PtrToStringUTF8(pathPtr)

                        if not (isNull path) then
                            drain.Post(DrainItem.File path)
                    | EventClassification.CoalescedScan ->
                        let pathPtr = Marshal.ReadIntPtr(eventPaths, i * IntPtr.Size)
                        let path = Marshal.PtrToStringUTF8(pathPtr)
                        info "fsevents" $"MustScanSubDirs event for %s{path}: flags %s{describeFlags flags}"

                        if onCoalescedEvent.IsSome && not (isNull path) then
                            drain.Post(DrainItem.Rescan path)
                    | EventClassification.Ignored -> debug "fsevents" $"filtered event: flags=0x%08X{flags}")

    let releaseResources () =
        if streamRef <> nativeint 0 then
            fsEventStreamRelease (streamRef)
            streamRef <- nativeint 0

        runLoopRef <- nativeint 0
        runLoopThread <- null

        if runLoopModeRef <> nativeint 0 then
            cfRelease (runLoopModeRef)
            runLoopModeRef <- nativeint 0

        for s in cfStringRefs do
            if s <> nativeint 0 then
                cfRelease (s)

        cfStringRefs <- [||]

        for s in exclusionStringRefs do
            if s <> nativeint 0 then
                cfRelease (s)

        exclusionStringRefs <- [||]

        if exclusionArrayRef <> nativeint 0 then
            cfRelease (exclusionArrayRef)
            exclusionArrayRef <- nativeint 0

        if cfArrayRef <> nativeint 0 then
            cfRelease (cfArrayRef)
            cfArrayRef <- nativeint 0

        if callbackHandle.IsAllocated then
            callbackHandle.Free()

    let cleanupCoordinator =
        NativeCleanupCoordinator(
            (fun () ->
                if streamRef <> nativeint 0 then
                    fsEventStreamStop (streamRef)),
            (fun () ->
                if streamRef <> nativeint 0 then
                    fsEventStreamInvalidate (streamRef)),
            (fun () ->
                let runLoop = Volatile.Read(&runLoopRef)

                if runLoop <> nativeint 0 then
                    cfRunLoopStop (runLoop)),
            (fun () -> runLoopThread),
            releaseResources
        )

    // The native stream first, so nothing posts into a drain that is shutting down.
    let cleanup () =
        cleanupCoordinator.Cleanup()
        (drain :> IDisposable).Dispose()

    do
        if directories.IsEmpty then
            (drain :> IDisposable).Dispose()
            invalidArg "directories" "At least one directory is required"

        try
            // Pin the callback delegate so GC doesn't collect it while native code holds a reference
            callbackHandle <- GCHandle.Alloc(callback)

            let arr, strs = createCFStringArray directories
            cfArrayRef <- arr
            cfStringRefs <- strs

            let flags = FSEventStreamCreateFlagFileEvents ||| FSEventStreamCreateFlagNoDefer

            streamRef <-
                fsEventStreamCreate (
                    nativeint 0,
                    callback,
                    nativeint 0,
                    cfArrayRef,
                    FSEventStreamEventIdSinceNow,
                    latencySeconds,
                    flags
                )

            if streamRef = nativeint 0 then
                raise (CreateFailedException())

            if exclusionPaths.Length > MaxExclusionPaths then
                invalidArg "exclusionPaths" $"FSEvents accepts at most %d{MaxExclusionPaths} exclusion paths"

            if not exclusionPaths.IsEmpty then
                let arr, strs = createCFStringArray exclusionPaths
                exclusionArrayRef <- arr
                exclusionStringRefs <- strs

                // A refused exclusion is not fatal: the managed callback still
                // filters every event, the kernel just dispatches more of them.
                if not (fsEventStreamSetExclusionPaths (streamRef, exclusionArrayRef)) then
                    warn "fsevents" "FSEventStreamSetExclusionPaths refused; excluded paths are filtered after delivery"

            // kCFRunLoopDefaultMode is a CFString constant — create it by value
            runLoopModeRef <- cfStringCreateWithCString (nativeint 0, "kCFRunLoopDefaultMode", CFStringEncodingUTF8)

            if runLoopModeRef = nativeint 0 then
                failwith "CFStringCreateWithCString returned null — failed to create kCFRunLoopDefaultMode"

            // Start a dedicated background thread that owns the CFRunLoop for event delivery.
            // FSEventStreamScheduleWithRunLoop requires an active CFRunLoop to pump events;
            // CFRunLoopRun() on this thread provides that pump.
            let capturedStreamRef = streamRef
            let capturedModeRef = runLoopModeRef
            let runLoopReady = new ManualResetEventSlim(false)

            runLoopThread <-
                Thread(fun () ->
                    // Get this thread's run loop — must be called from the thread that will run it
                    let rl = cfRunLoopGetCurrent ()
                    Volatile.Write(&runLoopRef, rl)

                    // Schedule before signalling — the constructor starts the stream once
                    // this fires.
                    fsEventStreamScheduleWithRunLoop (capturedStreamRef, rl, capturedModeRef)
                    runLoopReady.Set()

                    // Blocks, pumping CFRunLoop events, until CFRunLoopStop() in cleanup().
                    cfRunLoopRun ()
                    cleanupCoordinator.RunLoopExited()
                    debug "fsevents" "CFRunLoopRun() returned — run loop thread exiting")

            runLoopThread.IsBackground <- true
            runLoopThread.Name <- "com.fshotwatch.fsevents.runloop"
            runLoopThread.Start()

            runLoopReady.Wait()

            // Safe from any thread once the stream is scheduled.
            if not (fsEventStreamStart (streamRef)) then
                raise (StartFailedException())

            cleanupCoordinator.MarkStarted()

            info
                "fsevents"
                $"FSEventStream started: watching %d{directories.Length} dirs, latency=%.3f{latencySeconds}s"

            for dir in directories do
                info "fsevents" $"  watching: %s{dir}"
        with ex ->
            cleanup ()
            raise ex

    /// What the native callback has taken off fseventsd, and what the handlers have run.
    member _.DrainStats() : DrainStats = drain.Stats()

    /// Returns true if the stream was successfully created and started.
    member _.IsRunning =
        Volatile.Read(&streamRef) <> nativeint 0 && Volatile.Read(&disposed) = 0

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                cleanup ()

/// Create an FsEventStream watching the given directories.
/// The callback is invoked on the stream's drain thread (`EventDrain`) with each changed
/// file path, never on the CFRunLoop thread, so a slow callback does not stop the stream
/// from draining fseventsd.
/// `latencySeconds` is the FSEvents coalescing window passed to `FSEventStreamCreate`.
let create (directories: string list) (onFileEvent: string -> unit) (latencySeconds: float) =
    new FsEventStream(directories, onFileEvent, None, latencySeconds)

/// Create an FsEventStream whose kernel-side exclusions drop `exclusionPaths`
/// (directory prefixes, at most `MaxExclusionPaths`) before dispatch.
let createExcluding
    (directories: string list)
    (exclusionPaths: string list)
    (onFileEvent: string -> unit)
    (onCoalescedEvent: string -> unit)
    (latencySeconds: float)
    =
    new FsEventStream(directories, onFileEvent, Some onCoalescedEvent, latencySeconds, exclusionPaths)
