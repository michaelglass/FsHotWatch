module FsHotWatch.ProcessRegistry

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

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

/// How long a teardown waits for one killed child to exit. The kills run side by
/// side, so ten children cost one budget, not ten.
let internal TerminationBudget = TimeSpan.FromSeconds 5.0

/// Extra time the whole teardown allows beyond `TerminationBudget`, so a child that
/// used its full budget can still report what it established.
let private TeardownGrace = TimeSpan.FromSeconds 1.0

/// What a handle can tell us about its child.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type internal ExitObservation =
    | Exited
    | Running
    /// The handle cannot answer: most often it was disposed while still tracked.
    | Unobservable of reason: exn

/// What a teardown established about one child.
[<RequireQualifiedAccess>]
type internal Termination =
    | Established
    | Uncertain of reason: string

/// Only a handle that positively reports exit is evidence that its child is gone. A
/// disposed handle is NOT: disposing a `Process` releases our view of the child, not
/// the child, so treating "cannot observe" as "not alive" would let a caller that
/// disposed a live child claim it was reaped.
let internal classifyTermination (observation: ExitObservation) (killFailure: exn option) : Termination =
    match observation, killFailure with
    | ExitObservation.Exited, _ -> Termination.Established
    | ExitObservation.Running, Some failure ->
        Termination.Uncertain
            $"the kill failed (%s{failure.GetType().Name}: %s{failure.Message}) and the child is still running"
    | ExitObservation.Running, None ->
        Termination.Uncertain $"the child was still running %s{string TerminationBudget} after the kill"
    | ExitObservation.Unobservable reason, _ ->
        Termination.Uncertain
            $"its handle can no longer be observed (%s{reason.GetType().Name}: %s{reason.Message}), so nothing \
              establishes that the child exited; it was probably disposed while still tracked"

let private observe (p: Process) : ExitObservation =
    try
        if p.HasExited then
            ExitObservation.Exited
        else
            ExitObservation.Running
    with ex when isExpectedProcessException ex ->
        ExitObservation.Unobservable ex

/// What a registry needs from a child it owns: to see whether it is still running, to
/// kill its tree, and to wait for it. A child this process started is a `Process`
/// (`ownedProcess`); a child a spawn helper started for us is reached through the helper
/// and has no `Process` here.
type internal IOwnedChild =
    /// The child's pid, read once while it was certainly live.
    abstract Pid: int
    abstract Observe: unit -> ExitObservation
    /// Kill the child and its descendants. Throws what `Process.Kill` would.
    abstract KillTree: unit -> unit
    /// Wait up to `milliseconds` for the child to exit.
    abstract WaitForExit: milliseconds: int -> unit

/// A `Process` as an owned child. The pid is read now, while the caller holds a live
/// handle, so a later disposal cannot erase it.
let internal ownedProcess (p: Process) : IOwnedChild =
    let pid = p.Id

    { new IOwnedChild with
        member _.Pid = pid
        member _.Observe() = observe p
        member _.KillTree() = p.Kill(entireProcessTree = true)

        member _.WaitForExit milliseconds =
            p.WaitForExit(milliseconds: int) |> ignore }

/// Kill one child's tree and wait, within the budget, for positive evidence of exit.
let private terminateHandle (child: IOwnedChild) : Termination =
    match child.Observe() with
    | ExitObservation.Running ->
        let killFailure =
            try
                child.KillTree()
                None
            with ex when isExpectedProcessException ex ->
                Some ex

        try
            child.WaitForExit(int TerminationBudget.TotalMilliseconds)
        with ex when isExpectedProcessException ex ->
            ()

        classifyTermination (child.Observe()) killFailure
    | settled -> classifyTermination settled None

/// Tear every child down side by side, all bounded by ONE wait. A kill call that has
/// not come back by then is reported as uncertain rather than waited on.
let private terminateAll (children: IOwnedChild array) : Termination array =
    let attempts =
        children
        |> Array.map (fun child ->
            Task.Factory.StartNew(
                (fun () ->
                    try
                        terminateHandle child
                    with ex ->
                        Termination.Uncertain $"the teardown threw %s{ex.GetType().Name}: %s{ex.Message}"),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            ))

    let allowed = TerminationBudget + TeardownGrace

    if not (Array.isEmpty attempts) then
        Task.WaitAll(attempts |> Array.map (fun attempt -> attempt :> Task), allowed)
        |> ignore

    attempts
    |> Array.map (fun attempt ->
        if attempt.IsCompletedSuccessfully then
            attempt.Result
        else
            Termination.Uncertain $"the teardown did not return within %s{string allowed}")

/// Per-scope process tracker. Scoped via AsyncLocal so a daemon's spawned children
/// register against that daemon's registry, not a process-wide global. This keeps
/// `killAll` from clobbering unrelated work in parallel test runs.
///
/// A registry may have a PARENT: an operation's own scope (see `withChildScope`). It
/// forwards every admission, untrack and leak to its parent, so daemon shutdown still
/// sees an operation's children, while tearing the operation down reaches only its own.
type Registry internal (parent: Registry option) =
    // One lock for admission, untrack and the shutdown snapshot, so "closed" and "the
    // set of children" are never observed out of step. OS calls stay outside it.
    let gate = obj ()
    // Keyed by the identity of the caller's handle: the `Process` for a child this
    // process started, the helper's child object for one a spawn helper started.
    let live = Dictionary<obj, IOwnedChild>(HashIdentity.Reference)
    let mutable closed = false
    // Append-only: a tree we could not account for is never un-leaked.
    let leaks = ConcurrentQueue<LeakedTree>()

    let uncertain (pid: int) (reason: string) =
        { Pid = pid
          Description = $"tracked child pid %d{pid}"
          Reason = reason
          At = DateTime.UtcNow }

    new() = Registry(None)

    /// True once this registry, or any registry it forwards to, has shut down.
    member internal _.IsClosed: bool =
        lock gate (fun () -> closed)
        || (parent |> Option.exists (fun owner -> owner.IsClosed))

    /// Admit `child` under `key`, or refuse it: a registry that has begun shutting down
    /// reaps a child arriving afterwards instead of letting it escape the snapshot it
    /// already took. The parent is asked first; a refusing parent has already reaped it.
    member internal this.AdmitChild(key: obj, child: IOwnedChild) : bool =
        let parentAdmitted =
            match parent with
            | Some owner -> owner.AdmitChild(key, child)
            | None -> true

        if not parentAdmitted then
            false
        else
            let admitted =
                lock gate (fun () ->
                    if not closed then
                        live[key] <- child

                    not closed)

            if not admitted then
                match terminateAll [| child |] with
                | [| Termination.Uncertain reason |] -> this.ReportLeak(uncertain child.Pid reason)
                | _ -> ()

                parent |> Option.iter (fun owner -> owner.UntrackChild key)

            admitted

    member internal this.Admit(p: Process) : bool = this.AdmitChild(p, ownedProcess p)

    member this.Track(p: Process) = this.Admit p |> ignore

    member internal _.UntrackChild(key: obj) =
        lock gate (fun () -> live.Remove key) |> ignore
        parent |> Option.iter (fun owner -> owner.UntrackChild key)

    member this.Untrack(p: Process) = this.UntrackChild p

    /// The observably live tracked children this process started itself. A handle that
    /// cannot be observed is not listed; whether its child is GONE is `KillAll`'s
    /// question, not this view's. Children a spawn helper started have no `Process`
    /// here: `LivePids` lists every owned child.
    member _.Snapshot() : Process list =
        lock gate (fun () -> List.ofSeq live.Keys)
        |> List.choose (fun key ->
            match key with
            | :? Process as p ->
                match observe p with
                | ExitObservation.Running -> Some p
                | _ -> None
            | _ -> None)

    /// The pids of every observably live owned child, however it was started.
    member internal _.LivePids() : int list =
        lock gate (fun () -> List.ofSeq live.Values)
        |> List.choose (fun child ->
            match child.Observe() with
            | ExitObservation.Running -> Some child.Pid
            | _ -> None)

    /// Record a process tree whose termination we could NOT establish. Append-only,
    /// and never cleared by `KillAll` — the point of the record is to outlive the
    /// live set and be readable at shutdown. Forwarded, so the parent can name it at
    /// ITS shutdown too.
    member _.ReportLeak(leak: LeakedTree) =
        leaks.Enqueue leak
        parent |> Option.iter (fun owner -> owner.ReportLeak leak)

    /// Every tree we failed to account for, oldest first.
    member _.Leaks: LeakedTree list = List.ofSeq leaks

    /// Shut down: close admission and take the snapshot in ONE step, so a concurrent
    /// `Track` either belongs to the snapshot or observes the closure and reaps its own
    /// child. Then tear the snapshot down, bounded (see `TerminationBudget`).
    ///
    /// A child whose termination cannot be established is recorded as a leak, unless
    /// its owner untracked it while the teardown ran: accounting for it is then the
    /// owner's job, and `ProcessHelper` reports its own failed kills.
    member this.KillAll() : unit =
        let children =
            lock gate (fun () ->
                closed <- true
                live |> Seq.map (fun kv -> kv.Key, kv.Value) |> Array.ofSeq)

        let outcomes = terminateAll (Array.map snd children)

        for (key, child), outcome in Array.zip children outcomes do
            let stillOwned = lock gate (fun () -> live.Remove key)
            parent |> Option.iter (fun owner -> owner.UntrackChild key)

            match outcome with
            | Termination.Uncertain reason when stillOwned -> this.ReportLeak(uncertain child.Pid reason)
            | _ -> ()

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

/// Run `work` with `r` current, in a context of its own: the caller's is left as it
/// was, and what follows `work` in the caller runs in the caller's context again.
///
/// `use _ = install r` inside an `async` is not enough. Started on the caller's thread
/// (`Async.StartImmediate`, `Async.RunSynchronously`), the async sets `r` in the
/// caller's context and hands the thread back at its first wait, still set; its
/// restore runs later, in a continuation's context, never the caller's. So the caller
/// would go on spawning into `r`, and be refused once `r` has shut down.
let internal withRegistryAsync (r: Registry) (work: Async<'T>) : Async<'T> =
    async {
        let! ct = Async.CancellationToken

        return!
            Async.FromContinuations(fun (ok, error, cancelled) ->
                match ExecutionContext.Capture() with
                | null ->
                    // Flow is suppressed, so nothing `work` waits on carries a context:
                    // `r` is current until its first wait, and the caller's is restored.
                    let prior = currentRegistry.Value
                    currentRegistry.Value <- r

                    try
                        Async.StartWithContinuations(work, ok, error, cancelled, ct)
                    finally
                        currentRegistry.Value <- prior
                | caller ->
                    let inCaller (k: 'a -> unit) =
                        fun (value: 'a) -> ExecutionContext.Run(caller, ContextCallback(fun _ -> k value), null)

                    // `Run` gives the callback its own copy of the caller's context and
                    // restores the caller's when the work first waits; the work's
                    // continuations carry the copy.
                    ExecutionContext.Run(
                        caller,
                        ContextCallback(fun _ ->
                            currentRegistry.Value <- r
                            Async.StartWithContinuations(work, inCaller ok, inCaller error, inCaller cancelled, ct)),
                        null
                    ))
    }

/// Refuse a launch BEFORE it has side effects when the current scope has shut down.
/// Admission checks again after the spawn, which closes the race with a shutdown
/// landing between this check and the spawn.
let internal ensureAdmitting (what: string) =
    match currentOpt () with
    | Some r when r.IsClosed ->
        raise (OperationCanceledException($"refusing to launch %s{what}: its process scope has shut down"))
    | _ -> ()

/// Register `p` with the current scope's registry so shutdown can tear it down.
/// Returns false when the scope refused it, in which case it has already been reaped.
///
/// A child spawned with NO registry in scope can never be reaped — it outlives the
/// daemon as an init-reparented orphan — so the miss is warned, never swallowed.
let internal admitChild (key: obj) (child: IOwnedChild) : bool =
    match currentOpt () with
    | Some r -> r.AdmitChild(key, child)
    | None ->
        Logging.warn
            "process-registry"
            $"spawned pid %d{child.Pid} with no registry in scope — it cannot be reaped on shutdown and will be orphaned"

        true

let internal admit (p: Process) : bool = admitChild p (ownedProcess p)

/// Register `p` with the current scope's registry so daemon shutdown can tear it down.
let track (p: Process) = admit p |> ignore

/// Admit a child that was just launched under `key`, or raise
/// `OperationCanceledException`. A scope that shut down while the child was starting
/// has already reaped it, so whatever it would exit with is not the target's outcome
/// and must not be reported as one.
let internal admitChildOrRefuse (key: obj) (child: IOwnedChild) (what: string) =
    if not (admitChild key child) then
        raise (
            OperationCanceledException(
                $"%s{what} was terminated at launch: its process scope shut down while it started"
            )
        )

/// `admitChildOrRefuse` for a child this process started itself.
let internal admitOrRefuse (p: Process) (what: string) =
    admitChildOrRefuse p (ownedProcess p) what

let internal untrackChild (key: obj) =
    match currentOpt () with
    | Some r -> r.UntrackChild key
    | None -> ()

let untrack (p: Process) = untrackChild p

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

/// The refusal a scope raises when its work would otherwise succeed but a child's
/// termination could not be established. Pure, so the message is tested directly.
let internal uncertainRetirement (leaks: LeakedTree list) : exn option =
    if List.isEmpty leaks then
        None
    else
        let reasons =
            leaks
            |> List.map (fun leak -> $"pid %d{leak.Pid}: %s{leak.Reason}")
            |> String.concat "; "

        Some(
            InvalidOperationException(
                $"the operation finished, but it could not establish termination of its child processes (%s{reasons})"
            )
        )

/// Run one operation with a process scope of its own, nested in the current one.
///
/// - Children the work spawns register with the scope AND its parent, so daemon
///   shutdown still reaches them.
/// - Cancelling `ct` tears down this scope's children only, never a sibling's, and
///   refuses any launch the work attempts afterwards. Cancellation alone cannot stop
///   a child: a callback that ignores its token would otherwise keep one running.
/// - When the work ends, whatever it left tracked is torn down before it may retire.
///   If any termination in the scope could not be established, a result that would
///   otherwise succeed is refused. An exception the work raised itself is preserved
///   unchanged; the uncertainty is still in the parent's ledger.
///
/// Disposing the cancellation registration waits for a teardown the cancellation is
/// already running, so the ledger is read only after that teardown has finished.
let internal withChildScope (ct: CancellationToken) (work: unit -> 'T) : 'T =
    let scope = Registry(currentOpt ())
    use _ = install scope

    let result =
        let cancellation = ct.Register(fun () -> scope.KillAll())

        try
            work ()
        finally
            try
                scope.KillAll()
            finally
                cancellation.Dispose()

    match uncertainRetirement scope.Leaks with
    | None -> result
    | Some refusal -> raise refusal

/// `withChildScope` for asynchronous work. The scope stays current across the work's
/// continuations, and whatever the work left tracked is torn down before it may retire.
let internal withChildScopeAsync (ct: CancellationToken) (work: Async<'T>) : Async<'T> =
    async {
        let scope = Registry(currentOpt ())
        let cancellation = ct.Register(fun () -> scope.KillAll())

        let! result =
            withRegistryAsync
                scope
                (async {
                    try
                        return! work
                    finally
                        try
                            scope.KillAll()
                        finally
                            cancellation.Dispose()
                })

        match uncertainRetirement scope.Leaks with
        | None -> return result
        | Some refusal -> return raise refusal
    }
