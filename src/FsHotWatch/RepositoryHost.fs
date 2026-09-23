/// The repository host: one process serving every attached worktree of a repository.
///
/// It owns the repository's control state (`RepositoryIdentity.repositoryControlPaths`):
/// a singleton lock, its pid, and the endpoint. It answers attaches, holds one
/// `WorktreeSession` per attached worktree, and routes each session call to that
/// session's daemon.
///
/// Attaching a worktree is one transaction, and it can fail at any step without
/// leaving anything behind:
///
/// 1. the handshake (`AttachHandshake.decide`);
/// 2. the client's environment against the host's (MSBuild evaluation in this process
///    reads the host's);
/// 3. the worktree's .NET SDK against the one this process loaded;
/// 4. the worktree's own `.fshw/daemon.lock`, the lock its per-worktree daemon takes,
///    so the two never serve one worktree at once;
/// 5. building the session.
///
/// The worktree then carries a `.fshw/host-session.json` naming the host and session,
/// so tools that find the lock taken can say who holds it. A dead host's record is
/// detectably stale — its pid is gone, and the OS released its lock with it — so the
/// next attach or per-worktree daemon simply takes the worktree over.
module FsHotWatch.RepositoryHost

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FsHotWatch.AttachHandshake
open FsHotWatch.DaemonIdentity
open FsHotWatch.RepositoryIdentity
open FsHotWatch.RepositoryIpc
open FsHotWatch.SessionRegistry
open FsHotWatch.SessionScope

// ---------------------------------------------------------------------------
// The worktree's record of its host session
// ---------------------------------------------------------------------------

/// What `.fshw/host-session.json` says: which host process serves the worktree, on
/// which endpoint, as which session.
type HostSessionRecord =
    { HostPid: int
      Endpoint: string
      Session: SessionId }

module HostSessionRecord =
    let path (worktreeRoot: string) =
        Path.Combine(FsHwPaths.root worktreeRoot, "host-session.json")

    let render (record: HostSessionRecord) : string =
        let node = JsonObject()
        node["hostPid"] <- JsonValue.Create record.HostPid
        node["endpoint"] <- JsonValue.Create record.Endpoint
        node["session"] <- JsonValue.Create(SessionId.render record.Session)
        node.ToJsonString()

    let tryParse (json: string) : HostSessionRecord option =
        try
            match JsonNode.Parse json with
            | :? JsonObject as node ->
                let pid = node["hostPid"].GetValue<int>()
                let endpoint = node["endpoint"].GetValue<string>()

                SessionId.tryParse (node["session"].GetValue<string>())
                |> Option.map (fun session ->
                    { HostPid = pid
                      Endpoint = endpoint
                      Session = session })
            | _ -> None
        with _ ->
            None

    /// The worktree's record, when there is one and the host process it names is
    /// still alive. A record naming a dead process is stale, and reads as absent.
    let tryReadLive (isAlive: int -> bool) (worktreeRoot: string) : HostSessionRecord option =
        let file = path worktreeRoot

        if File.Exists file then
            (try
                Some(File.ReadAllText file)
             with _ ->
                 None)
            |> Option.bind tryParse
            |> Option.filter (fun r -> isAlive r.HostPid)
        else
            None

/// Whether a process with this pid exists and has not exited. Only a positive pid
/// names one process; the OS reads zero and negative ones as process groups.
let processAlive (pid: int) : bool =
    pid > 0
    && (try
            use p = Diagnostics.Process.GetProcessById pid
            not p.HasExited
        with _ ->
            false)

// ---------------------------------------------------------------------------
// The worktree lock
// ---------------------------------------------------------------------------

/// The lock a per-worktree daemon holds for its lifetime (`fshw start`).
let worktreeLockPath (worktreeRoot: string) =
    Path.Combine(FsHwPaths.root worktreeRoot, "daemon.lock")

/// Take the worktree's lock, or `None` when another process holds it.
let tryLockWorktree (worktreeRoot: string) : IDisposable option =
    Directory.CreateDirectory(FsHwPaths.root worktreeRoot) |> ignore

    try
        Some(new FileStream(worktreeLockPath worktreeRoot, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
    with :? IOException ->
        None

/// The pid a pid file records, if it is there and holds one.
let private tryReadPid (pidFile: string) : int option =
    try
        match Int32.TryParse(File.ReadAllText(pidFile).Trim()) with
        | true, pid -> Some pid
        | _ -> None
    with _ ->
        None

/// The pid a per-worktree daemon recorded, if it recorded one.
let private recordedDaemonPid (worktreeRoot: string) : int option =
    tryReadPid (Path.Combine(FsHwPaths.root worktreeRoot, "daemon.pid"))

// ---------------------------------------------------------------------------
// The host
// ---------------------------------------------------------------------------

/// What a host is, and what it is given to start sessions with.
[<NoComparison; NoEquality>]
type HostSettings =
    {
        Identity: HostIdentity
        Control: RepositoryControlPaths
        /// This process's environment, captured in the worktree that launched it.
        Environment: SessionEnvironment
        /// The .NET SDK version this process loaded.
        Toolchain: string
        /// The .NET SDK version a worktree selects, under its client's environment.
        ToolchainOf: ResolvedWorktree -> SessionEnvironment -> string
        /// Where a session's log lines go.
        SinkFor: ResolvedWorktree -> Logging.LogSink
        /// Watch a worktree's configuration; the callback reloads its session.
        WatchConfig: ResolvedWorktree -> (unit -> unit) -> IDisposable
        /// Anything more the host reports about itself (its shared watcher, say).
        Describe: unit -> JsonObject
    }

/// Repository-level RPCs: the host's own surface, next to each session's.
type HostRpcTarget(listSessions: unit -> string, stop: unit -> unit) =
    /// Every attached session, and the host process behind them, as JSON.
    member _.ListSessions() : string = listSessions ()

    /// Stop the host, ending every session.
    member _.StopHost() : string =
        stop ()
        "stopping"

/// `servingBound`: how long a session call waits for a just-attached session to begin
/// serving before it is refused.
type RepositoryHost(settings: HostSettings, registry: SessionRegistry, stop: unit -> unit, servingBound: TimeSpan) =
    let attaching = new SemaphoreSlim(1, 1)
    let hostPid = Environment.ProcessId

    let refuse refusal = Refused(refusal, settings.Identity)

    /// Build a fresh session for `worktree`, or refuse; nothing is left behind on a
    /// refusal. `id` is the incarnation the handshake minted.
    let startSession (id: SessionId) (worktree: ResolvedWorktree) (request: AttachRequest) : AttachResponse =
        let environment = SessionEnvironment.create worktree.Root.Value request.Environment

        match SessionEnvironment.msbuildMismatches settings.Environment environment with
        | _ :: _ as mismatches -> refuse (AttachRefusal.EnvironmentMismatch mismatches)
        | [] ->
            let toolchain = settings.ToolchainOf worktree environment

            if toolchain <> settings.Toolchain then
                refuse (AttachRefusal.ToolchainMismatch(toolchain, settings.Toolchain))
            else
                match tryLockWorktree worktree.Root.Value with
                | None -> refuse (AttachRefusal.WorktreeOwnedByDaemon(recordedDaemonPid worktree.Root.Value))
                | Some lock ->
                    let record = HostSessionRecord.path worktree.Root.Value

                    FsHwPaths.atomicWriteAllText
                        record
                        (HostSessionRecord.render
                            { HostPid = hostPid
                              Endpoint = settings.Control.Endpoint
                              Session = id })

                    let forget =
                        { new IDisposable with
                            member _.Dispose() =
                                // Only this session's record: a later session may have
                                // written its own by the time this one is released.
                                match HostSessionRecord.tryReadLive (fun _ -> true) worktree.Root.Value with
                                | Some r when r.Session = id -> File.Delete record
                                | _ -> () }

                    let spec =
                        { Worktree = worktree
                          Config = request.Config
                          Environment = environment
                          Sink = settings.SinkFor worktree
                          Owned = [ forget; lock ] }

                    match registry.Start(id, spec) with
                    | Error reason -> refuse (AttachRefusal.SessionStartFailed reason)
                    | Ok session ->
                        let watch = settings.WatchConfig worktree (fun () -> session.Stop())

                        session.Ended.ContinueWith(fun (_: Task<SessionEnd>) -> watch.Dispose())
                        |> ignore

                        Attached(id, AttachDisposition.NewSession, settings.Identity)

    let mint () = SessionIncarnation.mint ()

    let freshId (worktree: ResolvedWorktree) =
        { Repository = worktree.Repository
          Worktree = worktree.Worktree
          Incarnation = mint () }

    /// Answer one attach request. Attaches are serialized: each is a transaction over
    /// the registry and a worktree's lock.
    let attach (requestJson: string) : string =
        attaching.Wait()

        try
            let response =
                match decodeRequest requestJson with
                | Error _ ->
                    // Answered with the refusal naming why it could not be read.
                    respond settings.Identity resolveWorktree registry.Live mint requestJson
                | Ok request ->
                    let derived = resolveWorktree request.ClaimedRoot

                    match decide settings.Identity derived registry.Live mint request, derived with
                    | Attached(id, AttachDisposition.NewSession, _), Ok worktree -> startSession id worktree request
                    | Attached(live, AttachDisposition.RejoinedConfigChanged, _), Ok worktree ->
                        // The configuration changed under the live session: replace it
                        // with a new incarnation, so nothing from the old one survives.
                        registry.Detach live |> ignore

                        match startSession (freshId worktree) worktree request with
                        | Attached(id, _, h) -> Attached(id, AttachDisposition.RejoinedConfigChanged, h)
                        | refused -> refused
                    | decided, _ -> decided

            match response with
            | Refused(refusal, _) ->
                Logging.warn
                    "host"
                    $"attach refused (%s{AttachRefusal.kind refusal}): %s{AttachRefusal.describe refusal}"
            | Attached(id, disposition, _) -> Logging.info "host" $"attached %s{SessionId.render id} (%A{disposition})"

            encodeResponse response
        finally
            attaching.Release() |> ignore

    /// Route a session call: the live incarnation's RPC configuration, once it serves.
    let session (id: SessionId) (invocation: InvocationId) : Task<Result<Ipc.DaemonRpcConfig, string * string>> =
        task {
            match registry.TryGet id with
            | Some live ->
                let! _ = Task.WhenAny(live.Serving, Task.Delay servingBound)

                if live.Serving.IsCompletedSuccessfully then
                    Logging.debug "host" $"invocation %s{invocation.Value} → %s{SessionId.render id}"
                    return Ok live.Serving.Result
                else
                    return
                        Error("session-not-serving", $"session %s{SessionId.render id} is not serving; attach afresh")
            | None ->
                let refusal =
                    match registry.TryGetWorktree id.Worktree with
                    | Some other -> AttachRefusal.StaleIncarnation(id, other.Id)
                    | None -> AttachRefusal.UnknownSession id

                return Error(AttachRefusal.kind refusal, AttachRefusal.describe refusal)
        }

    let listSessions () : string =
        let node = settings.Describe()
        node["hostPid"] <- JsonValue.Create hostPid
        node["repository"] <- JsonValue.Create settings.Identity.Repository.Value
        node["endpoint"] <- JsonValue.Create settings.Control.Endpoint
        let sessions = JsonArray()

        for s in registry.Sessions |> List.sortBy (fun s -> s.Worktree.Root.Value) do
            let entry = JsonObject()
            entry["session"] <- JsonValue.Create(SessionId.render s.Id)
            entry["root"] <- JsonValue.Create s.Worktree.Root.Value
            entry["config"] <- JsonValue.Create s.Config.Value
            entry["serving"] <- JsonValue.Create s.Serving.IsCompletedSuccessfully
            entry["scanGeneration"] <- JsonValue.Create(s.Daemon.GetScanGeneration())
            sessions.Add entry

        node["sessions"] <- sessions
        node.ToJsonString()

    member _.Registry = registry

    member _.Handlers: EndpointHandlers =
        { Attach = attach
          Session = session
          Repository = fun _ -> box (HostRpcTarget(listSessions, stop)) }

    member _.ListSessions() = listSessions ()

// ---------------------------------------------------------------------------
// Running a host process
// ---------------------------------------------------------------------------

/// How long a host with no sessions stays up before it exits.
let DefaultIdleGrace = TimeSpan.FromMinutes 5.0

/// What running a host came to.
[<RequireQualifiedAccess>]
type HostRun =
    /// Another process holds the repository's host lock.
    | AlreadyRunning of pid: int option
    /// The host served until it was stopped, or went idle.
    | Stopped

/// Take the repository's host lock, serve the endpoint until `cts` is cancelled or no
/// session has been attached for `idleGrace`, then end every session and release the
/// control state.
let run
    (settings: HostSettings)
    (factory: SessionFactory)
    (idleGrace: TimeSpan)
    (cts: CancellationTokenSource)
    : HostRun =
    Directory.CreateDirectory settings.Control.Directory |> ignore

    let lock =
        try
            Some(new FileStream(settings.Control.LockFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        with :? IOException ->
            None

    match lock with
    | None ->
        let pid = tryReadPid settings.Control.PidFile
        HostRun.AlreadyRunning pid
    | Some lock ->
        use _lock = lock
        File.WriteAllText(settings.Control.PidFile, string Environment.ProcessId)
        File.WriteAllText(settings.Control.IdentityFile, BinaryIdentity.render settings.Identity.Protocol.Binary)
        use registry = new SessionRegistry(factory)

        let host =
            RepositoryHost(settings, registry, (fun () -> cts.Cancel()), PreambleBound)

        Logging.info
            "host"
            $"repository host pid=%d{Environment.ProcessId} serving %s{settings.Identity.Repository.Value} on %s{settings.Control.Endpoint}"

        let serving =
            Async.StartAsTask(RepositoryIpc.serve settings.Control.Endpoint host.Handlers cts)

        // Idle exit: no session attached for the whole grace period.
        let mutable idleSince = DateTime.UtcNow

        while not cts.IsCancellationRequested do
            cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds 1.0) |> ignore

            if not (List.isEmpty registry.Sessions) then
                idleSince <- DateTime.UtcNow
            elif DateTime.UtcNow - idleSince >= idleGrace then
                Logging.info "host" $"no session for %s{string idleGrace}; exiting"
                cts.Cancel()

        serving.Wait(
            Ipc.IpcServer.ConnectionDrainBound
            + Ipc.IpcServer.ReleaseBound
            + TimeSpan.FromSeconds 1.0
        )
        |> ignore

        File.Delete settings.Control.PidFile
        HostRun.Stopped
