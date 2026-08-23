module FsHotWatch.ProcessRegistry

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading

/// Exception classes treated as benign when observing or killing a tracked
/// Process. HasExited and Kill both throw InvalidOperationException (no process
/// associated / already exited) and Win32Exception (access denied). Both are
/// tolerated here. NullReferenceException and other CLR-level bugs propagate.
let isExpectedProcessException (ex: exn) : bool =
    match ex with
    | :? InvalidOperationException -> true
    | :? System.ComponentModel.Win32Exception -> true
    | _ -> false

/// A process tree we asked the OS to tear down and could NOT establish is dead —
/// either the kill was refused, or the kill CALL never returned inside its teardown
/// budget. Recorded as DATA, not only as a log line, so shutdown can name what it is
/// walking away from even when nobody was reading stderr at the moment it happened.
///
/// It holds the PID rather than the `Process`: the handle is disposed the instant the
/// spawn's scope unwinds, and a disposed handle can neither be observed nor killed.
///
/// We deliberately do NOT re-resolve that pid and kill it at shutdown. A pid the OS
/// has since recycled belongs to somebody else, and killing a stranger to tidy up is
/// worse than the leak. This record exists to be ACTED ON by a human.
[<NoComparison>]
type LeakedTree =
    {
        /// The pid we could not account for. May since have exited, or been reused.
        Pid: int
        /// What we spawned, as an operator would recognise it (command + args + pid).
        Description: string
        /// Why termination could not be established — a refusal, or a kill call that
        /// never returned.
        Reason: string
        /// When we gave up on it (UTC).
        At: DateTime
    }

/// Per-scope process tracker. Scoped via AsyncLocal so a daemon's spawned children
/// register against that daemon's registry, not a process-wide global. This keeps
/// `killAll` from clobbering unrelated work in parallel test runs.
type Registry() =
    let live = ConcurrentDictionary<int, Process>()
    // Track pids alongside Process so Untrack can clean up even if the Process
    // handle has been disposed and `proc.Id` would throw.
    let pidByProc = ConcurrentDictionary<Process, int>(HashIdentity.Reference)
    // Append-only: a tree we could not account for is never un-leaked.
    let leaks = ConcurrentQueue<LeakedTree>()

    member _.Track(p: Process) =
        pidByProc.TryAdd(p, p.Id) |> ignore
        live.TryAdd(p.Id, p) |> ignore

    member _.Untrack(p: Process) =
        match pidByProc.TryRemove(p) with
        | true, pid -> live.TryRemove(pid) |> ignore
        | false, _ -> ()

    member _.Snapshot() : Process list =
        [ for kv in live do
              let p = kv.Value

              // A tolerated exception means "can't observe; treat as not alive".
              let alive =
                  try
                      not p.HasExited
                  with ex when isExpectedProcessException ex ->
                      false

              if alive then
                  yield p ]

    /// Record a process tree whose termination we could NOT establish. Append-only,
    /// and never cleared by `KillAll` — the point of the record is to outlive the
    /// live set and be readable at shutdown.
    member _.ReportLeak(leak: LeakedTree) = leaks.Enqueue leak

    /// Every tree we failed to account for, oldest first.
    member _.Leaks: LeakedTree list = List.ofSeq leaks

    /// KillAll is a shutdown-only operation. Tracks added concurrently with
    /// iteration may be missed and silently dropped from `live` by the final
    /// Clear — accept that for daemon shutdown; do not call from steady-state.
    member _.KillAll() : unit =
        // Tolerating the expected classes (see `isExpectedProcessException`) lets
        // shutdown proceed across the whole live set.
        for kv in live do
            try
                let p = kv.Value

                if not p.HasExited then
                    p.Kill(entireProcessTree = true)
            with ex when isExpectedProcessException ex ->
                ()

        live.Clear()
        pidByProc.Clear()

        // Shutdown is the LAST moment anyone looks. A tree we could not account for
        // is exactly what it must not swallow, so it is named here even though we
        // will not chase the pid (see `LeakedTree`).
        for leak in leaks do
            let at = leak.At.ToString("HH:mm:ss")

            Logging.error
                "process-registry"
                $"LEAKED process tree: pid %d{leak.Pid} — %s{leak.Description}. %s{leak.Reason} at %s{at}Z, so we \
                  never established it is dead. This shutdown is NOT reaping it (the pid may since have been \
                  recycled, and killing a stranger is worse than the leak). Check it and kill it by hand."

let private currentRegistry = AsyncLocal<Registry>()

/// Install `r` as the current scope's tracker. Returns an IDisposable that
/// restores the prior registry, so callers can `use _ = install r` and have
/// the scope unwind cleanly when the work completes.
let install (r: Registry) : IDisposable =
    let prior = currentRegistry.Value
    currentRegistry.Value <- r

    { new IDisposable with
        member _.Dispose() = currentRegistry.Value <- prior }

let private currentOpt () =
    let r = currentRegistry.Value
    if isNull (box r) then None else Some r

/// Register `p` with the current scope's registry so daemon shutdown can tear it down.
/// A child spawned with NO registry in scope can never be reaped — it outlives the
/// daemon as an init-reparented orphan — so the miss is warned, never swallowed.
let track (p: Process) =
    match currentOpt () with
    | Some r -> r.Track p
    | None ->
        Logging.warn
            "process-registry"
            $"spawned pid %d{p.Id} with no registry in scope — it cannot be reaped on shutdown and will be orphaned"

let untrack (p: Process) =
    match currentOpt () with
    | Some r -> r.Untrack p
    | None -> ()

/// Register a process tree whose termination we could NOT establish, so shutdown can
/// name it. `reason` says WHY we cannot vouch for it — a refusal, or a kill call that
/// never returned inside its budget.
///
/// A leak with no registry in scope has nowhere to be recorded, so it is warned
/// rather than dropped: the caller has already logged the failure itself, but a
/// leaked tree that is also unrecordable is a second fact worth saying out loud.
let reportLeak (pid: int) (description: string) (reason: string) =
    let leak =
        { Pid = pid
          Description = description
          Reason = reason
          At = DateTime.UtcNow }

    match currentOpt () with
    | Some r -> r.ReportLeak leak
    | None ->
        Logging.warn
            "process-registry"
            $"leaked pid %d{pid} (%s{description}) with no registry in scope — it cannot be named at shutdown either"

/// Every tree the current scope failed to account for, oldest first.
let leaked () : LeakedTree list =
    match currentOpt () with
    | Some r -> r.Leaks
    | None -> []

let killAll () =
    match currentOpt () with
    | Some r -> r.KillAll()
    | None -> ()

let snapshot () : Process list =
    match currentOpt () with
    | Some r -> r.Snapshot()
    | None -> []
