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

/// `0` in a host's environment checks every worktree at its own paths instead of under
/// the repository's virtual root.
[<Literal>]
let VirtualRootEnvVar = "FSHW_VIRTUAL_ROOT"

/// `0` in a host's environment reads every in-repository reference where it is instead
/// of from the repository's import store.
[<Literal>]
let ImportStoreEnvVar = "FSHW_IMPORT_STORE"

/// True when `.fshw.json`'s text sets `"repositoryHost": true`.
let configured (configText: string) : bool =
    try
        let doc =
            JsonDocument.Parse(
                if String.IsNullOrWhiteSpace configText then
                    "{}"
                else
                    configText
            )

        try
            match doc.RootElement.TryGetProperty "repositoryHost" with
            | true, v -> v.ValueKind = JsonValueKind.True
            | _ -> false
        finally
            doc.Dispose()
    with _ ->
        false

/// Whether this worktree uses the repository host.
let enabled (configText: string) (getEnv: string -> string) : bool =
    match getEnv EnvVar with
    | null
    | "" -> configured configText
    | value -> value.Trim() = "1" || value.Trim().ToLowerInvariant() = "true"

/// The .NET SDK `root` selects under `env`: `dotnet --version` run there, as that
/// worktree's shell would run it, given at most `bound`. Anything but a clean answer is
/// reported as such, and never matches a real version.
let sdkVersionWithin (bound: TimeSpan) (root: string) (env: SessionEnvironment) : string =
    isolated (fun () ->
        use _ = SessionEnvironment.install env
        // Reaped with this probe, whoever asks: the host itself has no session scope.
        let registry = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install registry

        try
            match ProcessHelper.runProcess "dotnet" "--version" root [] (ProcessHelper.ProcessBounds.silent bound) with
            | ProcessHelper.Succeeded output -> (ProcessHelper.ProcessOutput.text output).Trim()
            | ProcessHelper.Failed(code, _) -> $"unresolved (`dotnet --version` exited %d{code})"
            | _ -> "unresolved (`dotnet --version` did not complete)"
        with ex ->
            $"unresolved (`dotnet --version` could not run: %s{ex.Message})")

/// `sdkVersionWithin` 30 seconds.
let sdkVersion (root: string) (env: SessionEnvironment) : string =
    sdkVersionWithin (TimeSpan.FromSeconds 30.0) root env

/// What attaching a worktree came to.
[<RequireQualifiedAccess>]
type Attach =
    /// The host serves this worktree as `session`, on `endpoint`.
    | Serving of endpoint: string * session: SessionId
    /// The host cannot serve this worktree; its own daemon should. Why, for the user.
    | OwnDaemon of reason: string
    /// Stop: the host refused in a way no fallback may paper over.
    | Refused of message: string

/// `attach`, with the endpoint's liveness probe and the attach exchange (endpoint,
/// request JSON → response JSON) supplied.
let internal attachWith
    (probe: string -> bool)
    (exchange: string -> string -> Async<Result<string, RepositoryIpc.ExchangeError>>)
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

        let up () = probe control.Endpoint

        /// Up, launching the host first when nothing answers.
        let upOrLaunched () =
            if not (up ()) then
                launch control
                let deadline = DateTime.UtcNow + startupBound

                while not (up ()) && DateTime.UtcNow < deadline do
                    Thread.Sleep 100

            up ()

        if not (upOrLaunched ()) then
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

            // No reply, or no connection, after the probe answered: the host exited in
            // between (its idle grace ran out), or hung up mid-frame. Not a failure of
            // this attach — relaunch if it is gone, and ask once more.
            let exchangeOnce () =
                exchange control.Endpoint (encodeRequest request) |> Async.RunSynchronously

            let answered =
                match exchangeOnce () with
                | Error _ when upOrLaunched () -> exchangeOnce ()
                | first -> first

            match answered |> Result.map decodeResponse with
            | Ok(Ok(AttachedReply(session, _, _))) -> Attach.Serving(control.Endpoint, session)
            | Ok(Ok(RefusedReply(kind, message, _))) when AttachRefusal.fallsBackToOwnDaemon kind ->
                Attach.OwnDaemon message
            | Ok(Ok(RefusedReply(_, message, _))) -> Attach.Refused message
            | Ok(Error e) -> Attach.Refused $"the repository host's reply could not be read: %A{e}"
            | Error e ->
                Attach.Refused
                    $"the repository host stopped answering during attach (%s{RepositoryIpc.ExchangeError.describe e}); see %s{control.HostLog}"

/// Attach `repoRoot`, launching the host (`launch`) when nothing serves the
/// repository's endpoint yet, and waiting at most `startupBound` for it. `stateHome`
/// is where repository hosts keep their control state (`FsHwPaths.stateHome`).
let attach (stateHome: string) (launch: RepositoryControlPaths -> unit) (startupBound: TimeSpan) =
    attachWith RepositoryIpc.isRunning RepositoryIpc.attach stateHome launch startupBound

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

/// The lines `status --repository` prints about the host itself, from its
/// `ListSessions` answer: its footprint, its lifecycle counts, and whether the host
/// before it exited without recording a stop. A field the host did not send prints
/// nothing.
let hostHealthLines (node: JsonNode) : string list =
    let int64Of (n: JsonNode) (name: string) = n[name].GetValue<int64>()
    let mb (n: JsonNode) (name: string) = int64Of n name / 1048576L

    [ match node["footprint"] with
      | null -> ()
      | f ->
          let workingSet = mb f "workingSetBytes"
          let managed = mb f "managedBytes"
          let committed = mb f "gcCommittedBytes"
          yield $"  footprint: working set %d{workingSet} MB, managed %d{managed} MB, GC committed %d{committed} MB"
      match node["lifecycle"] with
      | null -> ()
      | l ->
          let refused =
              l["refused"].AsObject()
              |> Seq.map (fun kv -> kv.Key, kv.Value.GetValue<int64>())
              |> List.ofSeq

          let refusedTotal = refused |> List.sumBy snd

          let byKind =
              match refused with
              | [] -> ""
              | kinds ->
                  let listed = kinds |> List.map (fun (k, n) -> $"%s{k} %d{n}") |> String.concat ", "
                  $" (%s{listed})"

          let ended = l["ended"]
          let stopped = int64Of ended "stopped"
          let faulted = int64Of ended "faulted"

          let attached = int64Of l "attached"
          let rejoined = int64Of l "rejoined"
          let reloaded = int64Of l "reloaded"
          let undelivered = int64Of l "undelivered"

          yield
              $"  lifecycle: %d{attached} attached, %d{rejoined} rejoined, %d{reloaded} reloaded, %d{refusedTotal} refused%s{byKind}, %d{stopped + faulted} ended (%d{faulted} faulted), %d{undelivered} undelivered"

          if l["previousHostUnclean"].GetValue<bool>() then
              let who =
                  match l["previousHostPid"] with
                  | null -> ""
                  | pid -> $" (pid %d{pid.GetValue<int>()})"

              let log = l["eventLog"].GetValue<string>()
              yield $"  the previous host%s{who} exited without recording a stop; events: %s{log}" ]
