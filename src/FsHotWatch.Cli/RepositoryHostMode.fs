/// The CLI side of the repository host: whether a worktree uses it, how the host is
/// launched, and how a CLI attaches its worktree.
///
/// Opt-in. A worktree uses the host when its `.fshw.json` says `"repositoryHost":
/// true`, or when `FSHW_REPOSITORY_HOST=1` says so for this shell (`0` turns it off).
/// Everything else keeps its own per-worktree daemon, which stays the default and the
/// fallback.
///
/// One host process serves every attached worktree of the repository. A crash or an
/// out-of-memory in that process ends every attached session at once, where separate
/// daemons would have lost only one; each worktree then reattaches (or starts its own
/// daemon) on its next command.
module FsHotWatch.Cli.RepositoryHostMode

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.RepositoryIdentity
open FsHotWatch.SessionScope

/// Overrides the worktree's `repositoryHost` key for this shell: `1` on, `0` off.
[<Literal>]
let EnvVar = "FSHW_REPOSITORY_HOST"

/// True when `.fshw.json`'s text sets `"repositoryHost": true`.
let configured (configText: string) : bool =
    try
        use doc =
            JsonDocument.Parse(
                if String.IsNullOrWhiteSpace configText then
                    "{}"
                else
                    configText
            )

        match doc.RootElement.TryGetProperty "repositoryHost" with
        | true, v -> v.ValueKind = JsonValueKind.True
        | _ -> false
    with _ ->
        false

/// Whether this worktree uses the repository host.
let enabled (configText: string) (getEnv: string -> string) : bool =
    match getEnv EnvVar with
    | null
    | "" -> configured configText
    | value -> value.Trim() = "1" || value.Trim().ToLowerInvariant() = "true"

/// The .NET SDK `root` selects under `env`: `dotnet --version` run there, as that
/// worktree's shell would run it. Anything but a clean answer is reported as such, and
/// never matches a real version.
let sdkVersion (root: string) (env: SessionEnvironment) : string =
    isolated (fun () ->
        use _ = SessionEnvironment.install env
        // Reaped with this probe, whoever asks: the host itself has no session scope.
        let registry = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install registry

        try
            match
                ProcessHelper.runProcess
                    "dotnet"
                    "--version"
                    root
                    []
                    (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 30.0))
            with
            | ProcessHelper.Succeeded output -> (ProcessHelper.ProcessOutput.text output).Trim()
            | ProcessHelper.Failed(code, _) -> $"unresolved (`dotnet --version` exited %d{code})"
            | _ -> "unresolved (`dotnet --version` did not complete)"
        with ex ->
            $"unresolved (`dotnet --version` could not run: %s{ex.Message})")

/// What attaching a worktree came to.
[<RequireQualifiedAccess>]
type Attach =
    /// The host serves this worktree as `session`, on `endpoint`.
    | Serving of endpoint: string * session: SessionId
    /// The host cannot serve this worktree; its own daemon should. Why, for the user.
    | OwnDaemon of reason: string
    /// Stop: the host refused in a way no fallback may paper over.
    | Refused of message: string

/// Attach `repoRoot`, launching the host (`launch`) when nothing serves the
/// repository's endpoint yet, and waiting at most `startupBound` for it. `stateHome`
/// is where repository hosts keep their control state (`FsHwPaths.stateHome`).
let attach
    (stateHome: string)
    (launch: RepositoryControlPaths -> unit)
    (startupBound: TimeSpan)
    (repoRoot: string)
    (configText: string)
    : Attach =
    match resolveWorktree repoRoot with
    | Error e -> Attach.Refused $"this checkout has no repository identity: %s{IdentityError.describe e}"
    | Ok worktree ->
        let control = repositoryControlPaths stateHome worktree.Repository

        let up () =
            RepositoryIpc.isRunning control.Endpoint

        if not (up ()) then
            launch control
            let deadline = DateTime.UtcNow + startupBound

            while not (up ()) && DateTime.UtcNow < deadline do
                Thread.Sleep 100

        if not (up ()) then
            Attach.Refused
                $"the repository host did not start within %.0f{startupBound.TotalSeconds}s; see %s{control.HostLog}"
        else
            let request =
                { requestFor
                      (ProtocolIdentity.current ())
                      worktree
                      IncarnationExpectation.Fresh
                      (ConfigDigest.ofText configText) with
                    Environment = SessionEnvironment.variables (SessionEnvironment.ofProcess repoRoot) }

            let reply =
                RepositoryIpc.attach control.Endpoint (encodeRequest request)
                |> Async.RunSynchronously
                |> decodeResponse

            match reply with
            | Ok(AttachedReply(session, _, _)) -> Attach.Serving(control.Endpoint, session)
            | Ok(RefusedReply(kind, message, _)) when AttachRefusal.fallsBackToOwnDaemon kind ->
                Attach.OwnDaemon message
            | Ok(RefusedReply(_, message, _)) -> Attach.Refused message
            | Error e -> Attach.Refused $"the repository host's reply could not be read: %A{e}"

/// The shell line that backgrounds the repository host for the worktree at `root`,
/// appending its output to the host log.
let hostShellCommand (exe: string) (toolPrefix: string) (root: string) (logFile: string) : string =
    let q = DetachedLaunch.shellQuote

    $"nohup %s{q exe} %s{toolPrefix}host %s{q root} >> %s{q logFile} 2>&1 < /dev/null &"

/// The settings a host launched from the worktree at `launchRoot` runs with.
let hostSettings
    (launchRoot: ResolvedWorktree)
    (sinkFor: ResolvedWorktree -> Logging.LogSink)
    (watchConfig: ResolvedWorktree -> (unit -> unit) -> IDisposable)
    (sessionResources: ResolvedWorktree -> IDisposable list)
    (describe: unit -> JsonObject)
    : RepositoryHost.HostSettings =
    let environment = SessionEnvironment.ofProcess launchRoot.Root.Value

    { Identity =
        { Protocol = ProtocolIdentity.current ()
          Repository = launchRoot.Repository }
      Control = repositoryControlPaths (FsHwPaths.stateHome ()) launchRoot.Repository
      Environment = environment
      Toolchain = sdkVersion launchRoot.Root.Value environment
      ToolchainOf = fun worktree env -> sdkVersion worktree.Root.Value env
      SinkFor = sinkFor
      WatchConfig = watchConfig
      SessionResources = sessionResources
      Describe = describe }
