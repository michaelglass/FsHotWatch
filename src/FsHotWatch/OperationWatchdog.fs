module FsHotWatch.OperationWatchdog

open System
open System.Collections.Generic
open System.Threading

/// AUTOMATION-15 — daemon operation watchdog + wedge-aware diagnostics.
///
/// The daemon's RPC request loop had no timeout/health path of its own: a
/// single in-flight op (a wedged FCS/format unit, a deadlocked re-discovery)
/// would block the socket, and BOTH `status` and `check` then blocked on the
/// same dead pipe ("could not connect to daemon: operation timed out") with
/// zero insight into WHAT was stuck. This module makes a wedge:
///   - PREVENTED elsewhere (bounded + cancellable ops — see ProcessHelper and
///     the deadline enforced at Ipc's `trackedTask` seam),
///   - DIAGNOSABLE here: it tracks the operations in flight (name + start time),
///     a watchdog timer logs a structured "operation exceeded Ns" record once an
///     op overruns the threshold, and `wedgeReport` lets `status` report the
///     wedge + stuck op + inline recovery directly instead of the consumer
///     blindly timing out on the socket.
///
/// AUTOMATION-98 — it tracks operationS, plural. It used to hold ONE
/// `InFlightOp`, which the IPC server's THREE concurrent acceptors corrupted by
/// design: a second `Begin` overwrote the first's record and the first `End`
/// erased the second's. Two parallel `fshw check` clients — routine — were
/// enough. The consequence was the exact inverse of this module's purpose: a
/// genuinely wedged op heartbeat as `idle` and `WedgeReport()` returned `None`,
/// so the diagnostic built for wedges went blind precisely under the concurrency
/// it was built for. Ops are now keyed by an `OpToken` minted at `Begin` and
/// retired at `End`, so they cannot clobber one another.
///
/// The decision logic (is-wedged, the log/heartbeat text) is pure and
/// unit-tested; the live timer + clock are injected.
/// A single in-flight operation: its name and when it started (UTC).
[<NoComparison>]
type InFlightOp = { Name: string; StartedAt: DateTime }

/// Identifies ONE tracked operation. Minted by `Begin`, retired by `End`, so an
/// `End` can only ever retire the op its own `Begin` started — the property the
/// old parameterless `End()` could not have.
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

/// The inline recovery action printed in every wedge report, so a consumer who
/// reads a wedged `status` knows exactly what to do without looking it up.
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
/// in-flight op + its elapsed time so a stuck daemon is diagnosable from a single
/// log line, or "idle" when nothing is running. When several ops are in flight the
/// count is carried too — a `status` poll landing on a free acceptor while a check
/// is wedged is the normal case, and hiding it would misrepresent the daemon.
let heartbeatLine (now: DateTime) (state: WatchdogState) : string =
    match oldestInFlight state.InFlight with
    | None -> "heartbeat: idle"
    | Some op ->
        let elapsed = now - op.StartedAt
        let running = $"%s{op.Name} running %d{int elapsed.TotalSeconds}s"

        match state.InFlight.Length with
        | 1 -> $"heartbeat: in-flight %s{running}"
        | n -> $"heartbeat: %d{n} in-flight, oldest %s{running}"

/// Live watchdog over the daemon's in-flight RPC operations.
///
/// `Begin` mints an `OpToken` and records the op; `End token` retires exactly that
/// op. A background timer fires every `tick`; on each tick, every in-flight op that
/// has overrun the threshold logs the structured overrun record ONCE (not every
/// tick, and not once globally — once PER OP, so a second wedged op is not masked
/// by the first), plus a heartbeat at a coarser cadence. Thread-safe: mutations and
/// timer reads share one lock.
///
/// Injected deps keep it testable: `now` (clock) and `log` (sink). Production
/// passes `DateTime.UtcNow` and `Logging.info "watchdog"`.
type Watchdog
    (threshold: TimeSpan, heartbeatEvery: TimeSpan, now: unit -> DateTime, log: string -> unit, ?tick: TimeSpan) =
    let gate = obj ()
    let inFlight = Dictionary<int64, InFlightOp>()
    // Ops whose overrun record has already been emitted, so a long op logs its
    // overrun once. Keyed per-op (not a single flag) so N concurrent wedges
    // produce N records rather than one.
    let overrunLogged = HashSet<int64>()
    let mutable nextId = 0L
    let mutable lastHeartbeat = now ()

    let snapshotOps () = inFlight.Values |> List.ofSeq

    let snapshot () =
        lock gate (fun () ->
            { InFlight = snapshotOps ()
              Threshold = threshold })

    let onTick () =
        let n = now ()
        // Read + decide under the lock so Begin/End can't mutate mid-read.
        let toLog =
            lock gate (fun () ->
                let logs = List<string>()

                for KeyValue(id, op) in inFlight do
                    if isWedgedAt n threshold op && overrunLogged.Add id then
                        logs.Add(overrunLogRecord n threshold op)

                // Heartbeat at its own cadence regardless of wedge state.
                if n - lastHeartbeat >= heartbeatEvery then
                    lastHeartbeat <- n

                    logs.Add(
                        heartbeatLine
                            n
                            { InFlight = snapshotOps ()
                              Threshold = threshold }
                    )

                logs |> List.ofSeq)

        for line in toLog do
            log line

    let timer =
        let interval = defaultArg tick (TimeSpan.FromSeconds(5.0))
        new Timer((fun _ -> onTick ()), null, interval, interval)

    /// Record `name` as in flight (capturing its start time) and return the token
    /// that retires it.
    member _.Begin(name: string) : OpToken =
        lock gate (fun () ->
            nextId <- nextId + 1L
            let id = nextId
            inFlight[id] <- { Name = name; StartedAt = now () }
            OpToken id)

    /// Retire the operation `token` identified (it completed or faulted). Retiring
    /// an already-retired token is a no-op, so a double-`End` cannot erase a
    /// SIBLING op's record the way the old parameterless `End()` did.
    member _.End(OpToken id) =
        lock gate (fun () ->
            inFlight.Remove id |> ignore
            overrunLogged.Remove id |> ignore)

    /// Current state snapshot — for `status`/tests.
    member _.State = snapshot ()

    /// The wedge report for `status`, or `None` when not wedged.
    member this.WedgeReport() = wedgeReport (now ()) this.State

    interface IDisposable with
        member _.Dispose() = timer.Dispose()
