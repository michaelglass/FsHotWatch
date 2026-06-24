module FsHotWatch.OperationWatchdog

open System
open System.Threading

/// AUTOMATION-15 — daemon operation watchdog + wedge-aware diagnostics.
///
/// The daemon's RPC request loop had no timeout/health path of its own: a
/// single in-flight op (a wedged FCS/format unit, a deadlocked re-discovery)
/// would block the socket, and BOTH `status` and `check` then blocked on the
/// same dead pipe ("could not connect to daemon: operation timed out") with
/// zero insight into WHAT was stuck. This module makes a wedge:
///   - PREVENTED elsewhere (bounded + cancellable ops — see ProcessHelper),
///   - DIAGNOSABLE here: it tracks the single in-flight daemon operation (name
///     + start time), a watchdog timer logs a structured "operation exceeded
///     Ns" record once an op overruns the threshold, and `wedgeReport` lets
///     `status` report the wedge + stuck op + inline recovery directly instead
///     of the consumer blindly timing out on the socket.
///
/// The decision logic (is-wedged, the log/heartbeat text) is pure and
/// unit-tested; the live timer + clock are injected.
/// A single in-flight operation: its name and when it started (UTC).
[<NoComparison>]
type InFlightOp = { Name: string; StartedAt: DateTime }

/// A snapshot of the watchdog's current view, used to render diagnostics.
[<NoComparison>]
type WatchdogState =
    {
        /// The op currently executing, if any.
        InFlight: InFlightOp option
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

/// The structured one-line record the watchdog emits when an op overruns the
/// threshold. Stable, greppable shape: `operation exceeded Ns: <op> running Ms`.
/// (`Ns` = threshold seconds, `Ms` = actual elapsed seconds.)
let overrunLogRecord (now: DateTime) (threshold: TimeSpan) (op: InFlightOp) : string =
    let elapsed = now - op.StartedAt
    $"operation exceeded %d{int threshold.TotalSeconds}s: %s{op.Name} running %d{int elapsed.TotalSeconds}s"

/// The inline recovery action printed in every wedge report, so a consumer who
/// reads a wedged `status` knows exactly what to do without looking it up.
[<Literal>]
let RecoveryAction =
    "recover with `fshw stop` then re-run (the next command auto-restarts the daemon); check logs/daemon.log for the stuck op"

/// Wedge-aware `status` line. `None` when no op is in flight or it is still
/// within threshold (the caller renders normal status). `Some message` when the
/// in-flight op has exceeded the threshold — naming the stuck op, its elapsed
/// time, and the inline recovery action. Stable `WEDGED:` prefix so consumers
/// can detect it with a cheap `StartsWith`.
let wedgeReport (now: DateTime) (state: WatchdogState) : string option =
    match state.InFlight with
    | Some op when isWedgedAt now state.Threshold op ->
        let elapsed = now - op.StartedAt

        Some
            $"WEDGED: %s{op.Name} running %d{int elapsed.TotalSeconds}s, exceeded %d{int state.Threshold.TotalSeconds}s threshold — %s{RecoveryAction}"
    | _ -> None

/// Heartbeat line: the default-on periodic diagnostic. Names the in-flight op +
/// its elapsed time so a stuck daemon is diagnosable from a single log line,
/// or "idle" when nothing is running.
let heartbeatLine (now: DateTime) (state: WatchdogState) : string =
    match state.InFlight with
    | Some op ->
        let elapsed = now - op.StartedAt
        $"heartbeat: in-flight %s{op.Name} running %d{int elapsed.TotalSeconds}s"
    | None -> "heartbeat: idle"

/// Live watchdog over the daemon's single in-flight RPC operation.
///
/// `Begin`/`End` bracket each RPC; the most recent un-ended `Begin` is the
/// in-flight op. A background timer fires every `tick`; on each tick, if the
/// in-flight op has overrun the threshold, it logs the structured overrun
/// record (once per overrun episode, not every tick) and a heartbeat at a
/// coarser cadence. Thread-safe: `Begin`/`End` mutate under a lock and the
/// timer reads under the same lock.
///
/// Injected deps keep it testable: `now` (clock) and `log` (sink). Production
/// passes `DateTime.UtcNow` and `Logging.info "watchdog"`.
type Watchdog
    (threshold: TimeSpan, heartbeatEvery: TimeSpan, now: unit -> DateTime, log: string -> unit, ?tick: TimeSpan) =
    let gate = obj ()
    let mutable inFlight: InFlightOp option = None
    // Whether we've already emitted the overrun record for the CURRENT op, so a
    // long op logs its overrun once (not on every tick).
    let mutable overrunLogged = false
    let mutable lastHeartbeat = now ()

    let snapshot () =
        lock gate (fun () ->
            { InFlight = inFlight
              Threshold = threshold })

    let onTick () =
        let n = now ()
        // Read + decide under the lock so Begin/End can't swap the op mid-read.
        let toLog =
            lock gate (fun () ->
                let logs = System.Collections.Generic.List<string>()

                match inFlight with
                | Some op when isWedgedAt n threshold op ->
                    if not overrunLogged then
                        overrunLogged <- true
                        logs.Add(overrunLogRecord n threshold op)
                | _ -> ()

                // Heartbeat at its own cadence regardless of wedge state.
                if n - lastHeartbeat >= heartbeatEvery then
                    lastHeartbeat <- n

                    logs.Add(
                        heartbeatLine
                            n
                            { InFlight = inFlight
                              Threshold = threshold }
                    )

                logs |> List.ofSeq)

        for line in toLog do
            log line

    let timer =
        let interval = defaultArg tick (TimeSpan.FromSeconds(5.0))
        new Timer((fun _ -> onTick ()), null, interval, interval)

    /// Mark `name` as the in-flight operation (records its start time).
    member _.Begin(name: string) =
        lock gate (fun () ->
            inFlight <- Some { Name = name; StartedAt = now () }
            overrunLogged <- false)

    /// Clear the in-flight operation (the op completed or faulted).
    member _.End() =
        lock gate (fun () ->
            inFlight <- None
            overrunLogged <- false)

    /// Current state snapshot — for `status`/tests.
    member _.State = snapshot ()

    /// The wedge report for `status`, or `None` when not wedged.
    member this.WedgeReport() = wedgeReport (now ()) this.State

    interface IDisposable with
        member _.Dispose() = timer.Dispose()
