module FsHotWatch.OperationWatchdog

open System
open System.Collections.Generic
open System.Threading

/// Diagnostic-only watchdog over the daemon's in-flight RPC operations: tracks
/// ops in flight and reports a wedge via `status`; it kills nothing — wedges are
/// prevented by bounded, cancellable ops (ProcessHelper) and the deadline at
/// Ipc's `trackedTask` seam. Ops are keyed by `OpToken` so concurrent `Begin`s
/// can't clobber one another (the IPC server runs several acceptors at once).
/// Decision logic (is-wedged, log/heartbeat text) is pure and unit-tested; the
/// live timer + clock are injected.
///
/// A single in-flight operation: its name and when it started (UTC).
[<NoComparison>]
type InFlightOp = { Name: string; StartedAt: DateTime }

/// Identifies ONE tracked operation. Minted by `Begin`, retired by `End`, so an
/// `End` retires only the op its own `Begin` started.
[<Struct>]
type OpToken = internal OpToken of id: int64

/// A snapshot of the watchdog's current view, used to render diagnostics.
[<NoComparison>]
type WatchdogState =
    {
        /// Every operation currently executing. Routinely more than one: the IPC
        /// server keeps three acceptors live so a `status` poll can be served
        /// while another op is stuck.
        InFlight: InFlightOp list
        /// Wall-clock threshold past which an in-flight op is treated as wedged.
        Threshold: TimeSpan
    }

/// Default threshold past which an in-flight daemon op is treated as wedged and
/// the watchdog emits its structured overrun record. Chosen to sit comfortably
/// above any legitimately long single RPC (a cold `WaitForComplete` blocks on
/// the daemon's own bounded plugin waits, which already log their own progress
/// every 10s) so the watchdog only fires on a genuine stall.
let DefaultThreshold = TimeSpan.FromSeconds(120.0)

/// True when `op` has been in flight for at least `threshold` as of `now`.
let isWedgedAt (now: DateTime) (threshold: TimeSpan) (op: InFlightOp) : bool = now - op.StartedAt >= threshold

/// The LONGEST-RUNNING op that has overrun the threshold, if any. The oldest is
/// the right one to report: it is the likeliest to actually BE the wedge (the
/// others may simply be queued behind it), and it stays stable across ticks while
/// shorter ops come and go.
let oldestOverrun (now: DateTime) (threshold: TimeSpan) (ops: InFlightOp list) : InFlightOp option =
    match ops |> List.filter (isWedgedAt now threshold) with
    | [] -> None
    | wedged -> Some(wedged |> List.minBy (fun op -> op.StartedAt))

/// The longest-running op in flight, wedged or not — what the heartbeat names.
let private oldestInFlight (ops: InFlightOp list) : InFlightOp option =
    match ops with
    | [] -> None
    | ops -> Some(ops |> List.minBy (fun op -> op.StartedAt))

/// The structured one-line record the watchdog emits when an op overruns the
/// threshold. Stable, greppable shape: `operation exceeded Ns: <op> running Ms`.
/// (`Ns` = threshold seconds, `Ms` = actual elapsed seconds.)
let overrunLogRecord (now: DateTime) (threshold: TimeSpan) (op: InFlightOp) : string =
    let elapsed = now - op.StartedAt
    $"operation exceeded %d{int threshold.TotalSeconds}s: %s{op.Name} running %d{int elapsed.TotalSeconds}s"

/// The inline recovery action printed in every wedge report.
[<Literal>]
let RecoveryAction =
    "recover with `fshw stop` then re-run (the next command auto-restarts the daemon); check logs/daemon.log for the stuck op"

/// Wedge-aware `status` line. `None` when nothing is in flight or every in-flight
/// op is still within threshold (the caller renders normal status). `Some message`
/// when an op has exceeded the threshold — naming the OLDEST such op, its elapsed
/// time, and the inline recovery action. Stable `WEDGED:` prefix so consumers can
/// detect it with a cheap `StartsWith`.
let wedgeReport (now: DateTime) (state: WatchdogState) : string option =
    match oldestOverrun now state.Threshold state.InFlight with
    | Some op ->
        let elapsed = now - op.StartedAt

        Some
            $"WEDGED: %s{op.Name} running %d{int elapsed.TotalSeconds}s, exceeded %d{int state.Threshold.TotalSeconds}s threshold — %s{RecoveryAction}"
    | None -> None

/// Heartbeat line: the default-on periodic diagnostic. Names the longest-running
/// in-flight op + its elapsed time so a stuck daemon is diagnosable from a single log
/// line, or "idle" when nothing is running. The count is carried when several ops are
/// in flight — a `status` poll landing on a free acceptor while a check is wedged is
/// the normal case.
let heartbeatLine (now: DateTime) (state: WatchdogState) : string =
    match oldestInFlight state.InFlight with
    | None -> "heartbeat: idle"
    | Some op ->
        let elapsed = now - op.StartedAt
        let running = $"%s{op.Name} running %d{int elapsed.TotalSeconds}s"

        match state.InFlight.Length with
        | 1 -> $"heartbeat: in-flight %s{running}"
        | n -> $"heartbeat: %d{n} in-flight, oldest %s{running}"

/// The heartbeat's GC cost: the share of the heartbeat `window` the runtime spent with
/// threads paused for garbage collection (`GC.GetTotalPauseDuration` over the window),
/// as a suffix for `heartbeatLine`. Empty for a window with no length.
let gcPauseSuffix (paused: TimeSpan) (window: TimeSpan) : string =
    if window <= TimeSpan.Zero then
        ""
    else
        let percent = 100.0 * paused.TotalMilliseconds / window.TotalMilliseconds

        $"; gc-pause %.2f{percent}%% (%d{int64 paused.TotalMilliseconds}ms of %d{int64 window.TotalSeconds}s)"

/// What the heartbeat reads about the machine and the managed heap: the 1-minute load
/// average (`None` where the platform has none), the GC heap size, and the cumulative
/// gen0/gen1/gen2 collection counts.
[<NoComparison>]
type ResourceReading =
    {
        LoadAverage: float option
        HeapBytes: int64
        Gen0: int
        Gen1: int
        Gen2: int
        /// The process's thread count: a pool grown by starvation shows here first.
        Threads: int
    }

module private Native =
    [<Runtime.InteropServices.DllImport("libc", SetLastError = false)>]
    extern int getloadavg(double[] loadavg, int nelem)

/// `getloadavg` for one sample: how many samples it returned, and the 1-minute value.
let private nativeLoad () : int * float =
    let values = Array.zeroCreate<double> 1
    let returned = Native.getloadavg (values, 1)
    returned, values[0]

/// The load average `read` reports, or `None` on Windows (no such figure) or when the
/// read returned no sample. `read` is injected so every outcome is testable here.
let internal loadAverageOf (isWindows: bool) (read: unit -> int * float) : float option =
    if isWindows then
        None
    else
        match read () with
        | 1, value -> Some value
        | _ -> None

/// The 1-minute load average, or `None` on Windows or when the call fails.
let loadAverage () : float option =
    loadAverageOf (OperatingSystem.IsWindows()) nativeLoad

/// How many threads this process has.
let private threadCount () : int =
    let self = Diagnostics.Process.GetCurrentProcess()

    try
        self.Threads.Count
    finally
        self.Dispose()

/// The process's current `ResourceReading`.
let readResources () : ResourceReading =
    { LoadAverage = loadAverage ()
      HeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes
      Gen0 = GC.CollectionCount 0
      Gen1 = GC.CollectionCount 1
      Gen2 = GC.CollectionCount 2
      Threads = threadCount () }

/// The heartbeat's resource suffix: load average, GC heap size, and how many gen0/1/2
/// collections ran since the previous heartbeat. A GC share near 100% with gen2 counts
/// climbing and the heap flat says the live set is at the heap's limit — the reading the
/// gc-pause figure alone could not distinguish from a burst of short-lived garbage.
let resourceSuffix (previous: ResourceReading) (current: ResourceReading) : string =
    let load =
        match current.LoadAverage with
        | Some value -> $"load %.2f{value}"
        | None -> "load n/a"

    let heapGb = float current.HeapBytes / 1073741824.0

    $"; %s{load}; heap %.2f{heapGb} GB; collections gen0 +%d{current.Gen0 - previous.Gen0}, gen1 +%d{current.Gen1 - previous.Gen1}, gen2 +%d{current.Gen2 - previous.Gen2}; threads %d{current.Threads}"

/// When the heap valve (see `Watchdog`) forces a collection: the heap is above
/// `Threshold` and no gen2 collection has run for `Quiet`.
[<NoComparison>]
type HeapValvePolicy = { Threshold: int64; Quiet: TimeSpan }

/// The default valve: a quarter of the memory the GC may use, and two minutes without a
/// gen2. A daemon whose live set is a few GB never comes near it; the incident it backs
/// up was a 32 GB heap of garbage that no gen2 had touched for minutes.
let defaultHeapValvePolicy () : HeapValvePolicy =
    { Threshold = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4L
      Quiet = TimeSpan.FromMinutes 2.0 }

/// What the valve has seen: the gen2 count at the last reading and when it last moved,
/// and when the valve last fired.
[<NoComparison>]
type HeapValveState =
    { Gen2: int
      Gen2MovedAt: DateTime
      LastFired: DateTime option }

/// `state` updated with `reading`, taken at `now`.
let observeGen2 (state: HeapValveState) (now: DateTime) (reading: ResourceReading) : HeapValveState =
    if reading.Gen2 <> state.Gen2 then
        { state with
            Gen2 = reading.Gen2
            Gen2MovedAt = now }
    else
        state

/// Whether the valve fires on `reading` at `now`, given `state` already updated with it:
/// the heap is above the threshold, no gen2 has run for the quiet interval, and the valve
/// itself has not fired within it — so it can never collect in a loop.
let heapValveFires (policy: HeapValvePolicy) (state: HeapValveState) (now: DateTime) (reading: ResourceReading) : bool =
    reading.HeapBytes > policy.Threshold
    && now - state.Gen2MovedAt >= policy.Quiet
    && (match state.LastFired with
        | Some fired -> now - fired >= policy.Quiet
        | None -> true)

/// One firing of the valve, for the log and `status`.
[<NoComparison>]
type HeapValveFiring =
    { At: DateTime
      HeapBefore: int64
      HeapAfter: int64
      QuietFor: TimeSpan
      Count: int }

let private gb (bytes: int64) = float bytes / 1073741824.0

/// The loud line the valve logs when it fires.
let heapValveLogLine (policy: HeapValvePolicy) (firing: HeapValveFiring) : string =
    $"HEAP VALVE FIRED (#%d{firing.Count}): heap %.2f{gb firing.HeapBefore} GB above %.2f{gb policy.Threshold} GB with no gen2 collection for %d{int firing.QuietFor.TotalSeconds}s; forced one compacting gen2, heap now %.2f{gb firing.HeapAfter} GB. This is a backstop: the check bound, the shared snapshots or the GC configuration has regressed"

/// The `status` line for the valve, once it has fired.
let heapValveReport (firing: HeapValveFiring) : string =
    let at = firing.At.ToString("yyyy-MM-ddTHH:mm:ssZ")

    $"HEAP VALVE: fired %d{firing.Count} time(s), last at %s{at} (heap %.2f{gb firing.HeapBefore} GB -> %.2f{gb firing.HeapAfter} GB) — the daemon's allocation has regressed; see logs/daemon.log"

/// One blocking, compacting collection of every generation, the large-object heap included.
let compactingCollect () : unit =
    Runtime.GCSettings.LargeObjectHeapCompactionMode <- Runtime.GCLargeObjectHeapCompactionMode.CompactOnce
    GC.Collect(2, GCCollectionMode.Forced, true, true)

/// Live watchdog over the daemon's in-flight RPC operations.
///
/// `Begin` mints an `OpToken` and records the op; `End token` retires exactly that
/// op. A background timer fires every `tick`; on each tick, every in-flight op that
/// has overrun the threshold logs the structured overrun record ONCE (not every
/// tick, and not once globally — once PER OP, so a second wedged op is not masked
/// by the first), plus a heartbeat at a coarser cadence. Thread-safe: mutations and
/// timer reads share one lock.
///
/// Each heartbeat carries `gcPauseSuffix` for the time since the previous one.
///
/// Injected deps keep it testable: `now` (clock), `log` (sink) and `gcPauseTotal`
/// (the process's cumulative GC pause). Production passes `DateTime.UtcNow`,
/// `Logging.info "watchdog"` and the default, `GC.GetTotalPauseDuration`.
type Watchdog
    (
        threshold: TimeSpan,
        heartbeatEvery: TimeSpan,
        now: unit -> DateTime,
        log: string -> unit,
        ?tick: TimeSpan,
        ?gcPauseTotal: unit -> TimeSpan,
        ?resources: unit -> ResourceReading,
        ?heapValve: HeapValvePolicy,
        ?collect: unit -> unit
    ) =
    let gcPauseTotal = defaultArg gcPauseTotal GC.GetTotalPauseDuration
    let resources = defaultArg resources readResources
    let heapValve = defaultArg heapValve (defaultHeapValvePolicy ())
    let collect = defaultArg collect compactingCollect
    let gate = Lock()
    let inFlight = Dictionary<int64, InFlightOp>()
    // Ops whose overrun record has already been emitted, so a long op logs its overrun
    // once.
    let overrunLogged = HashSet<int64>()
    let mutable nextId = 0L
    let mutable lastHeartbeat = now ()
    let mutable lastGcPause = gcPauseTotal ()
    let mutable lastResources = resources ()

    let mutable valveState =
        { Gen2 = lastResources.Gen2
          Gen2MovedAt = now ()
          LastFired = None }

    let mutable lastFiring: HeapValveFiring option = None

    let snapshotOps () = inFlight.Values |> List.ofSeq

    let snapshot () =
        Locking.locked gate (fun () ->
            { InFlight = snapshotOps ()
              Threshold = threshold })

    let onTick () =
        let n = now ()
        // Read + decide under the lock so Begin/End can't mutate mid-read.
        let valve = ref None

        let toLog =
            Locking.locked gate (fun () ->
                let logs = List<string>()

                for KeyValue(id, op) in inFlight do
                    if isWedgedAt n threshold op && overrunLogged.Add id then
                        logs.Add(overrunLogRecord n threshold op)

                // Heartbeat at its own cadence regardless of wedge state.
                if n - lastHeartbeat >= heartbeatEvery then
                    let gcPause = gcPauseTotal ()
                    let pauseSuffix = gcPauseSuffix (gcPause - lastGcPause) (n - lastHeartbeat)
                    let reading = resources ()
                    let resourcePart = resourceSuffix lastResources reading
                    lastHeartbeat <- n
                    lastGcPause <- gcPause
                    lastResources <- reading
                    valveState <- observeGen2 valveState n reading

                    if heapValveFires heapValve valveState n reading then
                        valveState <- { valveState with LastFired = Some n }
                        valve.Value <- Some(reading.HeapBytes, n - valveState.Gen2MovedAt)

                    logs.Add(
                        heartbeatLine
                            n
                            { InFlight = snapshotOps ()
                              Threshold = threshold }
                        + pauseSuffix
                        + resourcePart
                    )

                logs |> List.ofSeq)

        for line in toLog do
            log line

        // Outside the lock: a blocking full collection takes seconds, and `Begin`/`End`
        // must not wait on it.
        match valve.Value with
        | Some(heapBefore, quietFor) ->
            collect ()

            let firing =
                { At = n
                  HeapBefore = heapBefore
                  HeapAfter = (resources ()).HeapBytes
                  QuietFor = quietFor
                  Count =
                    (match Volatile.Read(&lastFiring) with
                     | Some previous -> previous.Count + 1
                     | None -> 1) }

            Volatile.Write(&lastFiring, Some firing)
            log (heapValveLogLine heapValve firing)
        | None -> ()

    let timer =
        let interval = defaultArg tick (TimeSpan.FromSeconds(5.0))
        new Timer((fun _ -> onTick ()), null, interval, interval)

    /// Record `name` as in flight (capturing its start time) and return the token
    /// that retires it.
    member _.Begin(name: string) : OpToken =
        Locking.locked gate (fun () ->
            nextId <- nextId + 1L
            let id = nextId
            inFlight[id] <- { Name = name; StartedAt = now () }
            OpToken id)

    /// Retire the operation `token` identified (it completed or faulted). Retiring
    /// an already-retired token is a no-op, so a double-`End` cannot erase a
    /// sibling op's record.
    member _.End(OpToken id) =
        Locking.locked gate (fun () ->
            inFlight.Remove id |> ignore
            overrunLogged.Remove id |> ignore)

    /// Current state snapshot — for `status`/tests.
    member _.State = snapshot ()

    /// The wedge report for `status`, or `None` when not wedged.
    member this.WedgeReport() = wedgeReport (now ()) this.State

    /// The heap valve's report for `status`, or `None` when it has never fired.
    member _.HeapValveReport() : string option =
        Volatile.Read(&lastFiring) |> Option.map heapValveReport

    interface IDisposable with
        member _.Dispose() = timer.Dispose()
