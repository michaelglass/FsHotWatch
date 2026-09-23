/// The worktree sessions a repository host holds.
///
/// Each session wraps today's `Daemon` unchanged: its own checker, plugin host and
/// `.fshw` state. It is built with `SessionScope.isolated`, so its log sink, its
/// client's environment and the process registry `Daemon` installs belong to it alone
/// and flow with everything it starts. A session ends when its run ends for any
/// reason — a stop, its own `Shutdown`, idle-exit, a wedge restart, a fault — and is
/// removed at that moment. Its incarnation is then void, and what it owned (the
/// worktree's lock, its watchers) is released. Nothing a session does on the way in or
/// out reaches a sibling.
module FsHotWatch.SessionRegistry

open System
open System.Threading
open System.Threading.Tasks
open FsHotWatch.AttachHandshake
open FsHotWatch.Ipc
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionScope

/// Everything a session is started from.
[<NoComparison; NoEquality>]
type SessionSpec =
    {
        Worktree: ResolvedWorktree
        /// The configuration the attaching client loaded.
        Config: ConfigDigest
        /// The attaching client's environment; the session's children start from it.
        Environment: SessionEnvironment
        /// Where the session's log lines go.
        Sink: Logging.LogSink
        /// Released when the session ends, or when it fails to start.
        Owned: IDisposable list
    }

/// Builds a session's daemon. Runs inside the session's own scope.
type SessionFactory = SessionSpec -> Daemon.Daemon

/// Runs a built daemon: serve through the given function, from the given start
/// instant, until the token source is cancelled.
type SessionRun =
    Daemon.Daemon
        -> (DaemonRpcConfig -> CancellationTokenSource -> Async<unit>)
        -> DateTime
        -> CancellationTokenSource
        -> Async<unit>

/// How long a stopped session's daemon waits for its serving to wind down.
let ServeBound = TimeSpan.FromSeconds 5.0

/// How long `Detach` waits for a session to end.
let DetachBound = TimeSpan.FromSeconds 60.0

/// Run the daemon as today's code does, through the host's serving.
let defaultRun: SessionRun =
    fun daemon serve startedAt cts -> daemon.RunWith(serve, ServeBound, startedAt, cts)

/// How a session's run ended.
[<RequireQualifiedAccess>]
type SessionEnd =
    | Stopped
    | Faulted of message: string

/// One attached worktree.
[<Sealed>]
type WorktreeSession
    internal
    (
        id: SessionId,
        spec: SessionSpec,
        daemon: Daemon.Daemon,
        cts: CancellationTokenSource,
        serving: Task<DaemonRpcConfig>,
        ended: Task<SessionEnd>
    ) =
    member _.Id = id
    member _.Worktree = spec.Worktree
    member _.Config = spec.Config
    member _.Daemon = daemon

    /// The daemon's RPC configuration, once it serves. Canceled when the session ends
    /// before serving.
    member _.Serving = serving

    /// Completes, never faults, when the session has ended and released what it owned.
    member _.Ended = ended

    /// Ask the session to end. Returns at once; `Ended` says when it has.
    ///
    /// The token source is never disposed (it holds no timer), so a stop after the end
    /// is a no-op rather than a fault.
    member _.Stop() = cts.Cancel()

/// Release each of `owned`, logging (never raising) a release that fails.
let private release (owned: IDisposable list) =
    for d in owned do
        try
            d.Dispose()
        with ex ->
            Logging.warn "session" $"releasing a session resource failed: %s{ex.Message}"

/// Serve by handing the configuration over, then hold until the daemon is stopped.
let private serveInto (serving: TaskCompletionSource<DaemonRpcConfig>) =
    fun (config: DaemonRpcConfig) (serveCts: CancellationTokenSource) ->
        async {
            serving.TrySetResult config |> ignore
            let stopped = TaskCompletionSource()
            use _ = serveCts.Token.Register(fun () -> stopped.TrySetResult() |> ignore)
            do! stopped.Task |> Async.AwaitTask
        }

type SessionRegistry internal (factory: SessionFactory, run: SessionRun) =
    let gate = obj ()
    let mutable sessions: Map<WorktreeId, WorktreeSession> = Map.empty
    let mutable starting: Set<WorktreeId> = Set.empty

    let current () = Volatile.Read(&sessions)

    let remove (session: WorktreeSession) =
        lock gate (fun () ->
            sessions <- sessions |> Map.filter (fun _ live -> not (obj.ReferenceEquals(live, session))))

    /// Build and run the session, inside its own scope. The worktree is reserved.
    let launch (id: SessionId) (spec: SessionSpec) : WorktreeSession =
        isolated (fun () ->
            // Installed for the session's whole life: everything started below, the
            // daemon's agents and the run included, captures this context.
            Logging.installSink spec.Sink |> ignore
            SessionEnvironment.install spec.Environment |> ignore
            let startedAt = DateTime.UtcNow
            let daemon = factory spec

            // One line naming the process and the session, after the configuration lines
            // the build wrote: the session's log says who is behind it.
            Logging.info
                "host"
                $"Attached to repository host pid=%d{Environment.ProcessId} session=%s{SessionId.render id}"

            let cts = new CancellationTokenSource()

            let serving =
                TaskCompletionSource<DaemonRpcConfig>(TaskCreationOptions.RunContinuationsAsynchronously)

            let ended =
                TaskCompletionSource<SessionEnd>(TaskCreationOptions.RunContinuationsAsynchronously)

            let session = WorktreeSession(id, spec, daemon, cts, serving.Task, ended.Task)
            lock gate (fun () -> sessions <- sessions.Add(spec.Worktree.Worktree, session))

            let body =
                async {
                    let! outcome = run daemon (serveInto serving) startedAt cts |> Async.Catch

                    let how =
                        match outcome with
                        | Choice1Of2() -> SessionEnd.Stopped
                        | Choice2Of2 ex ->
                            Logging.error "session" $"session %s{SessionId.render id} ended by a fault: %s{ex.Message}"

                            SessionEnd.Faulted ex.Message

                    remove session
                    serving.TrySetCanceled() |> ignore
                    // Idempotent: a run that ended normally has disposed it already.
                    release [ daemon :> IDisposable ]
                    release spec.Owned
                    ended.SetResult how
                }

            Async.Start body
            session)

    new(factory: SessionFactory) = new SessionRegistry(factory, defaultRun)

    /// Start a session `id` for `spec.Worktree`. Refused when the worktree already has
    /// a session (or one starting), or when building it fails; either way everything
    /// in `spec.Owned` is released.
    member _.Start(id: SessionId, spec: SessionSpec) : Result<WorktreeSession, string> =
        let worktree = spec.Worktree.Worktree

        let reserved =
            lock gate (fun () ->
                if sessions.ContainsKey worktree || starting.Contains worktree then
                    false
                else
                    starting <- starting.Add worktree
                    true)

        if not reserved then
            release spec.Owned
            Error $"worktree %s{spec.Worktree.Root.Value} already has a session in this host"
        else
            try
                try
                    Ok(launch id spec)
                with ex ->
                    release spec.Owned
                    Error $"the session for %s{spec.Worktree.Root.Value} could not start: %s{ex.Message}"
            finally
                lock gate (fun () -> starting <- starting.Remove worktree)

    /// The live session `id` — exactly that incarnation.
    member _.TryGet(id: SessionId) : WorktreeSession option =
        current () |> Map.tryFind id.Worktree |> Option.filter (fun s -> s.Id = id)

    /// The worktree's live session, whatever its incarnation.
    member _.TryGetWorktree(worktree: WorktreeId) : WorktreeSession option = current () |> Map.tryFind worktree

    /// The worktree's live session as the attach handshake sees it.
    member this.Live(worktree: WorktreeId) : LiveSession option =
        this.TryGetWorktree worktree
        |> Option.map (fun s -> { Session = s.Id; Config = s.Config })

    member _.Sessions: WorktreeSession list = current () |> Map.values |> List.ofSeq

    /// Stop session `id` and wait (bounded) for it to end. False when no such
    /// incarnation is live.
    member this.Detach(id: SessionId) : bool =
        match this.TryGet id with
        | Some session ->
            session.Stop()
            session.Ended.Wait DetachBound
        | None -> false

    interface IDisposable with
        member this.Dispose() =
            let all = this.Sessions

            for session in all do
                session.Stop()

            for session in all do
                session.Ended.Wait DetachBound |> ignore
