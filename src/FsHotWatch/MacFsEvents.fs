/// macOS FSEvents P/Invoke bindings and managed wrapper.
/// Only loaded/used on macOS — guarded by RuntimeInformation.IsOSPlatform checks in Watcher.fs.
module FsHotWatch.MacFsEvents

open System
open System.Runtime.InteropServices
open System.Threading

// ─── Native library paths ───────────────────────────────────────────
[<Literal>]
let private CoreServicesLib =
    "/System/Library/Frameworks/CoreServices.framework/CoreServices"

[<Literal>]
let private CoreFoundationLib =
    "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"

[<Literal>]
let private LibSystem = "/usr/lib/libSystem.B.dylib"

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

// ─── Callback delegate ──────────────────────────────────────────────
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
extern void private FSEventStreamSetDispatchQueue(nativeint streamRef, nativeint queue)

[<DllImport(CoreServicesLib)>]
extern [<return: MarshalAs(UnmanagedType.U1)>] bool private FSEventStreamStart(nativeint streamRef)

[<DllImport(CoreServicesLib)>]
extern void private FSEventStreamStop(nativeint streamRef)

[<DllImport(CoreServicesLib)>]
extern void private FSEventStreamInvalidate(nativeint streamRef)

[<DllImport(CoreServicesLib)>]
extern void private FSEventStreamRelease(nativeint streamRef)

// ─── P/Invoke: libdispatch ──────────────────────────────────────────
[<DllImport(LibSystem)>]
extern nativeint private dispatch_queue_create([<MarshalAs(UnmanagedType.LPUTF8Str)>] string label, nativeint attr)

[<DllImport(LibSystem)>]
extern void private dispatch_release(nativeint queue)

// ─── Helpers ────────────────────────────────────────────────────────

/// Create a CFArray of CFStrings from the given paths. Caller must CFRelease the array
/// and each string.
let private createCFStringArray (paths: string list) =
    let cfStrings =
        paths
        |> List.map (fun p -> CFStringCreateWithCString(nativeint 0, p, kCFStringEncodingUTF8))
        |> List.toArray

    let arr =
        CFArrayCreate(nativeint 0, cfStrings, nativeint cfStrings.Length, nativeint 0)

    arr, cfStrings

/// Check if an FSEvent flag set indicates a file event we care about.
let private isFileChangeEvent (flags: uint32) =
    (flags &&& ItemIsFile <> 0u)
    && (flags &&& (ItemCreated ||| ItemRemoved ||| ItemRenamed ||| ItemModified) <> 0u)

// ─── FsEventStream ──────────────────────────────────────────────────

/// Managed wrapper around a native FSEventStream.
/// Watches the given directories for file-level events and invokes the callback
/// with each changed file path. Uses a GCD dispatch queue for event delivery.
type FsEventStream(directories: string list, onFileChanged: string -> unit, latencySeconds: float) =
    let mutable disposed = false
    let mutable streamRef = nativeint 0
    let mutable queueRef = nativeint 0
    let mutable callbackHandle = Unchecked.defaultof<GCHandle>
    let mutable cfStringRefs: nativeint array = [||]
    let mutable cfArrayRef = nativeint 0

    let callback =
        FSEventStreamCallback(fun _streamRef _clientInfo numEvents eventPaths eventFlags _eventIds ->
            let count = int numEvents

            for i in 0 .. count - 1 do
                let pathPtr = Marshal.ReadIntPtr(eventPaths, i * IntPtr.Size)
                let path = Marshal.PtrToStringUTF8(pathPtr)
                let flags = uint32 (Marshal.ReadInt32(eventFlags + nativeint (i * 4)))

                if not (isNull path) && isFileChangeEvent flags then
                    onFileChanged path)

    do
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

        queueRef <- dispatch_queue_create ("com.fshotwatch.fsevents", nativeint 0)
        FSEventStreamSetDispatchQueue(streamRef, queueRef)

        if not (FSEventStreamStart(streamRef)) then
            failwith "FSEventStreamStart returned false — failed to start FSEvents stream"

    /// Returns true if the stream was successfully created and started.
    member _.IsRunning = streamRef <> nativeint 0 && not disposed

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true

                if streamRef <> nativeint 0 then
                    FSEventStreamStop(streamRef)
                    FSEventStreamInvalidate(streamRef)
                    FSEventStreamRelease(streamRef)
                    streamRef <- nativeint 0

                if queueRef <> nativeint 0 then
                    dispatch_release (queueRef)
                    queueRef <- nativeint 0

                // Release CF objects
                for s in cfStringRefs do
                    if s <> nativeint 0 then
                        CFRelease(s)

                cfStringRefs <- [||]

                if cfArrayRef <> nativeint 0 then
                    CFRelease(cfArrayRef)
                    cfArrayRef <- nativeint 0

                if callbackHandle.IsAllocated then
                    callbackHandle.Free()

/// Create an FsEventStream watching the given directories.
/// The callback is invoked on a GCD background thread with each changed file path.
let create (directories: string list) (onFileChanged: string -> unit) =
    new FsEventStream(directories, onFileChanged, 0.05)
