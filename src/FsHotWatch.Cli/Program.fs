module FsHotWatch.Cli.Program

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open CommandTree
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Daemon
open FsHotWatch.Ipc

type RunFlag = | [<Cmd("Run once without daemon")>] RunOnce

type ScanFlag = | [<Cmd("Force rescan even if no changes detected")>] Force

type ErrorsFlag =
    | [<CmdFlag(Short = "w"); Cmd("Block until every plugin reaches a terminal state")>] Wait
    | [<CmdFlag(Name = "timeout"); Cmd("Timeout in seconds for --wait (default 600)")>] Timeout of int

/// Normalized wait policy parsed from ErrorsFlag list. Prevents the impossible
/// states allowed by the raw flag list (e.g. --timeout without --wait, negative timeout).
[<RequireQualifiedAccess>]
type WaitMode =
    | NoWait
    | WaitFor of TimeSpan

module WaitMode =
    let defaultTimeout = TimeSpan.FromSeconds(600.0)

    /// Parse raw flags to a normalized WaitMode. Returns an error message on invalid combinations.
    let fromFlags (flags: ErrorsFlag list) : Result<WaitMode, string> =
        let wait = flags |> List.contains Wait

        let timeout =
            flags
            |> List.tryPick (function
                | Timeout s -> Some s
                | _ -> None)

        match wait, timeout with
        | false, Some _ -> Error "--timeout requires --wait"
        | false, None -> Ok WaitMode.NoWait
        | true, None -> Ok(WaitMode.WaitFor defaultTimeout)
        | true, Some s when s <= 0 -> Error "--timeout must be a positive number of seconds"
        | true, Some s -> Ok(WaitMode.WaitFor(TimeSpan.FromSeconds(float s)))

type ConfigCommand = | [<Cmd("Validate .fs-hot-watch.json without starting the daemon")>] Check

type CoverageCommand =
    | [<Cmd("Delete coverage baseline + partial JSON so the next full run rebuilds from scratch",
            Name = "refresh-baseline")>] RefreshBaseline

type Command =
    | [<Cmd("Start the daemon")>] Start
    | [<Cmd("Stop the daemon")>] Stop
    | [<Cmd("Run all checks")>] Check of RunFlag list
    | [<Cmd("Build the project")>] Build of RunFlag list
    | [<Cmd("Run tests")>] Test of RunFlag list
    | [<Cmd("Format code")>] Format of RunFlag list
    | [<Cmd("Show lint results from daemon")>] Lint of RunFlag list
    | [<Cmd("Show format-check results from daemon", Name = "format-check")>] FormatCheck of RunFlag list
    | [<Cmd("Show analyzer results from daemon")>] Analyze of RunFlag list
    | [<Cmd("Show current status")>] Status of plugin: string option
    | [<Cmd("Show accumulated errors")>] Errors of ErrorsFlag list
    | [<Cmd("Scan for file changes")>] Scan of ScanFlag list
    | [<Cmd("Force a plugin to re-run, clearing its cached state")>] Rerun of pluginName: string
    | [<Cmd("Generate initial config")>] Init
    | [<Cmd("Configuration commands")>] Config of ConfigCommand
    | [<Cmd("Coverage commands")>] Coverage of CoverageCommand
    | [<Cmd("Install fish completions")>] Completions

type GlobalFlag =
    | [<CmdFlag(Short = "v"); Cmd("Enable debug-level logging")>] Verbose
    | [<CmdFlag(Name = "log-level"); Cmd("Set log level: error|warning|info|debug")>] LogLevel of string
    | [<CmdFlag(Name = "no-cache"); Cmd("Disable on-disk task result cache")>] NoCache
    | [<CmdFlag(Name = "no-warn-fail"); Cmd("Treat warnings as non-fatal (errors still fail)")>] NoWarnFail
    | [<CmdFlag(Short = "q"); Cmd("Compact one-line-per-plugin output")>] Compact
    | [<CmdFlag(Short = "a"); Cmd("Agent-friendly parseable output with next-step hint")>] Agent

let globalSpec =
    CommandReflection.fromUnionWithGlobalsAndEnv<Command, GlobalFlag>
        "FsHotWatch — F# file watcher daemon"
        "FS_HOT_WATCH"

let commandTree = globalSpec.Tree

let cliName = "fs-hot-watch"

let private isRunOnce = List.contains RunOnce

/// Pick a render mode from the global `--agent` / `--compact` flags. `--agent`
/// wins when both are set.
let private pickMode (agentMode: bool) (compactMode: bool) : ProgressRenderer.RenderMode =
    if agentMode then ProgressRenderer.Agent
    elif compactMode then ProgressRenderer.Compact
    else ProgressRenderer.Verbose

let private renderLines mode warningsAreFailures statuses =
    ProgressRenderer.renderAll mode warningsAreFailures System.DateTime.UtcNow statuses

let private renderBlock mode warningsAreFailures statuses =
    renderLines mode warningsAreFailures statuses |> String.concat "\n"

/// Compute the launch command for re-starting the daemon.
/// Returns (exe, argPrefix) where argPrefix is prepended to "start" when launching.
/// When running as a dotnet tool, Environment.ProcessPath is the dotnet binary itself,
/// so we need to reconstruct "dotnet tool run fs-hot-watch" as the command.
let computeLaunchCommand (processPath: string) : string * string =
    let lowerPath = processPath.ToLowerInvariant()

    if lowerPath.EndsWith("dotnet") || lowerPath.EndsWith("dotnet.exe") then
        (processPath, $"tool run %s{cliName} ")
    else
        (processPath, "")

/// Walk up from startDir looking for .jj or .git directory.
let findRepoRoot (startDir: string) =
    let rec walk (dir: string) =
        if
            Directory.Exists(Path.Combine(dir, ".jj"))
            || Directory.Exists(Path.Combine(dir, ".git"))
        then
            Some dir
        else
            let parent = Directory.GetParent(dir)
            if isNull parent then None else walk parent.FullName

    walk startDir

/// Compute a deterministic pipe name from repo root path.
let computePipeName (repoRoot: string) =
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot))
    let short = Convert.ToHexStringLower(hash).Substring(0, 12)
    $"fs-hot-watch-{short}"

/// Injectable file system operations for testability.
type FileOps =
    { FileExists: string -> bool
      ReadAllText: string -> string
      WriteAllText: string -> string -> unit
      DeleteFile: string -> unit
      GetLastWriteTimeUtc: string -> DateTime
      CreateDirectory: string -> unit }

/// Default file system operations.
let defaultFileOps: FileOps =
    { FileExists = File.Exists
      ReadAllText = File.ReadAllText
      WriteAllText = fun path content -> File.WriteAllText(path, content)
      DeleteFile = File.Delete
      GetLastWriteTimeUtc = fun path -> File.GetLastWriteTimeUtc(path)
      CreateDirectory = fun path -> Directory.CreateDirectory(path) |> ignore }

/// Injectable process operations for testability.
type ProcessOps =
    { GetProcessById: int -> System.Diagnostics.Process
      KillProcess: System.Diagnostics.Process -> unit
      WaitForExit: System.Diagnostics.Process -> int -> bool }

/// Default process operations.
let defaultProcessOps: ProcessOps =
    { GetProcessById = System.Diagnostics.Process.GetProcessById
      KillProcess = fun proc -> proc.Kill()
      WaitForExit = fun proc timeout -> proc.WaitForExit(timeout) }

/// Injectable IPC operations for testability.
type IpcOps =
    { Shutdown: string -> Async<string>
      Scan: string -> bool -> Async<string>
      ScanStatus: string -> Async<string>
      GetStatus: string -> Async<string>
      GetPluginStatus: string -> string -> Async<string>
      RunCommand: string -> string -> string -> Async<string>
      GetDiagnostics: string -> string -> Async<string>
      WaitForScan: string -> int64 -> Async<string>
      WaitForComplete: string -> int -> Async<string>
      TriggerBuild: string -> Async<string>
      FormatAll: string -> Async<string>
      RerunPlugin: string -> string -> Async<string>
      IsRunning: string -> bool
      LaunchDaemon: string -> string -> string -> unit }

/// Default IPC operations using the real IpcClient.
let defaultIpcOps: IpcOps =
    { Shutdown = IpcClient.shutdown
      Scan = IpcClient.scan
      ScanStatus = IpcClient.scanStatus
      GetStatus = IpcClient.getStatus
      GetPluginStatus = IpcClient.getPluginStatus
      RunCommand = IpcClient.runCommand
      GetDiagnostics = IpcClient.getDiagnostics
      WaitForScan = IpcClient.waitForScan
      WaitForComplete = IpcClient.waitForComplete
      TriggerBuild = IpcClient.triggerBuild
      FormatAll = IpcClient.formatAll
      RerunPlugin = IpcClient.rerunPlugin
      IsRunning = IpcClient.isRunning
      LaunchDaemon =
        fun repoRoot extraArgs logFile ->
            let (exe, toolPrefix) = computeLaunchCommand Environment.ProcessPath

            let psi =
                System.Diagnostics.ProcessStartInfo(
                    "/bin/sh",
                    $"-c \"nohup '%s{exe}' %s{toolPrefix}%s{extraArgs}start >> '%s{logFile}' 2>&1 &\""
                )

            psi.WorkingDirectory <- repoRoot
            psi.UseShellExecute <- false
            let proc = System.Diagnostics.Process.Start(psi)
            proc.WaitForExit() }

/// Unwrap nested AggregateException down to the most informative inner exception
/// so we don't print "One or more errors occurred. (...)" wrapping the real message.
let rec unwrapIpcException (ex: exn) : exn =
    match ex with
    | :? AggregateException as agg when agg.InnerExceptions.Count = 1 -> unwrapIpcException agg.InnerExceptions.[0]
    | :? AggregateException as agg when agg.InnerException <> null -> unwrapIpcException agg.InnerException
    | _ -> ex

/// Wrap an IPC call with connection error handling.
let private withIpc (action: unit -> int) : int =
    try
        action ()
    with ex ->
        let inner = unwrapIpcException ex

        let hint =
            match inner with
            // StreamJsonRpc reads a Content-Length header then allocates a buffer of
            // that size. A corrupted/garbage header (commonly: two daemons sharing the
            // same pipe, or a leftover stale daemon from an older version) makes the
            // length nonsensical, and the buffer alloc throws OutOfMemoryException —
            // which is misleading because the machine isn't actually out of memory.
            | :? OutOfMemoryException ->
                Some
                    "The IPC pipe returned a corrupted message — usually caused by another \
                     daemon (possibly an older version) writing to the same pipe. Try: \
                     `pkill -f FsHotWatch.Cli.dll` then `dotnet fs-hot-watch start`."
            | :? TimeoutException ->
                Some "Daemon did not respond in time. It may be busy or hung — check `logs/daemon.log`."
            | _ -> None

        eprintfn "Could not connect to daemon: %s" inner.Message

        match hint with
        | Some h -> eprintfn "  hint: %s" h
        | None -> ()

        1

/// Ensure daemon, poll for progress, render colored output.
let private ensureAndQueryErrors
    (mode: ProgressRenderer.RenderMode)
    (noWarnFail: bool)
    (ensureDaemon: unit -> bool)
    (ipc: IpcOps)
    (pipeName: string)
    (pluginFilter: string)
    : int =
    if not (ensureDaemon ()) then
        eprintfn "Failed to start daemon"
        1
    else
        withIpc (fun () ->
            IpcOutput.pollAndRender
                mode
                (renderLines mode (not noWarnFail))
                noWarnFail
                (fun () -> ipc.WaitForScan pipeName -1L |> Async.RunSynchronously)
                (fun () -> ipc.GetStatus pipeName |> Async.RunSynchronously)
                (fun () -> ipc.GetDiagnostics pipeName pluginFilter |> Async.RunSynchronously))

/// Compute a hash of the config file + CLI binary for staleness detection (injectable).
let computeConfigHashWith (fileOps: FileOps) (repoRoot: string) (exePath: string) =
    let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")

    let configContent =
        if fileOps.FileExists configPath then
            fileOps.ReadAllText configPath
        else
            ""

    let exeModTime =
        if fileOps.FileExists exePath then
            fileOps.GetLastWriteTimeUtc(exePath).Ticks.ToString()
        else
            ""

    let hash =
        Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(configContent + exeModTime))

    Convert.ToHexStringLower(hash).Substring(0, 16)

/// Compute a hash of the config file + CLI binary for staleness detection.
let private computeConfigHash (repoRoot: string) =
    computeConfigHashWith defaultFileOps repoRoot Environment.ProcessPath

/// What action ensureDaemon should take.
type DaemonAction =
    | Reuse
    | Restart
    | StartFresh

/// Determine what daemon action is needed based on current state.
let decideDaemonAction (isRunning: bool) (storedHash: string) (currentHash: string) : DaemonAction =
    if isRunning then
        if storedHash = currentHash then Reuse else Restart
    else
        StartFresh

/// Kill a stale daemon process by PID file (injectable).
let killStaleDaemonWith (fileOps: FileOps) (processOps: ProcessOps) (repoRoot: string) =
    let pidPath = Path.Combine(repoRoot, ".fs-hot-watch", "daemon.pid")

    if fileOps.FileExists pidPath then
        try
            let pid = (fileOps.ReadAllText pidPath).Trim() |> int

            try
                let proc = processOps.GetProcessById pid
                eprintfn "  Killing stale daemon (PID %d)..." pid
                processOps.KillProcess proc
                processOps.WaitForExit proc 5000 |> ignore
            with ex ->
                eprintfn "  Could not kill PID %d: %s" pid ex.Message

            fileOps.DeleteFile pidPath
        with ex ->
            eprintfn "  Could not clean up stale daemon: %s" ex.Message

/// Kill a stale daemon process by PID file.
let private killStaleDaemon (repoRoot: string) =
    killStaleDaemonWith defaultFileOps defaultProcessOps repoRoot

/// Start a fresh daemon process (injectable for testing).
let startFreshDaemonWith
    (fileOps: FileOps)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (currentHash: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    let stateDir = Path.Combine(repoRoot, ".fs-hot-watch")

    let logDir =
        if Path.IsPathRooted(logDirName) then
            logDirName
        else
            Path.Combine(repoRoot, logDirName)

    fileOps.CreateDirectory logDir
    let logFile = Path.Combine(logDir, "daemon.log")
    eprintfn "Starting daemon... (log: %s)" logFile
    ipc.LaunchDaemon repoRoot extraArgs logFile
    fileOps.CreateDirectory stateDir
    fileOps.WriteAllText (Path.Combine(stateDir, "config.hash")) currentHash
    let deadline = DateTime.UtcNow.AddSeconds(startupTimeoutSeconds)
    let mutable isUp = ipc.IsRunning pipeName

    while not isUp && DateTime.UtcNow < deadline do
        Thread.Sleep(100)
        isUp <- ipc.IsRunning pipeName

    isUp

let private startFreshDaemon
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (currentHash: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    startFreshDaemonWith defaultFileOps ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds

let private ensureDaemon
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (extraArgs: string)
    (logDirName: string)
    (startupTimeoutSeconds: float)
    : bool =
    let stateDir = Path.Combine(repoRoot, ".fs-hot-watch")
    let hashPath = Path.Combine(stateDir, "config.hash")
    let currentHash = computeConfigHash repoRoot
    let isRunning = ipc.IsRunning pipeName

    let storedHash =
        if File.Exists hashPath then
            File.ReadAllText(hashPath).Trim()
        else
            ""

    match decideDaemonAction isRunning storedHash currentHash with
    | Reuse -> true
    | Restart ->
        eprintfn "  Daemon config changed — restarting..."

        try
            ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
            Thread.Sleep(1000)
        with ex ->
            eprintfn "  Shutdown request failed: %s" ex.Message

        killStaleDaemon repoRoot
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds
    | StartFresh ->
        killStaleDaemon repoRoot
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs logDirName startupTimeoutSeconds

/// Options assembled from parsed global flags.
type GlobalOptions =
    { NoCache: bool
      NoWarnFail: bool
      AgentMode: bool
      CompactMode: bool
      DaemonExtraArgs: string }

let defaultGlobalOptions =
    { NoCache = false
      NoWarnFail = false
      AgentMode = false
      CompactMode = false
      DaemonExtraArgs = "" }

/// Delete coverage baseline + partial JSON for every configured test project
/// (skipping those with coverage opted out). Returns the list of paths that
/// were actually removed — empty when nothing was present. Pure wrt. the
/// filesystem inputs; safe to call when the coverage directory doesn't exist.
let refreshCoverageBaseline (repoRoot: string) (config: DaemonConfiguration) : string list =
    match config.Tests with
    | None -> []
    | Some t ->
        t.Projects
        |> List.filter (fun p -> p.Coverage)
        |> List.collect (fun p ->
            let dir = Path.Combine(repoRoot, t.CoverageDir, p.Project)

            [ FsHotWatch.TestPrune.CoverageMerge.BaselineName
              FsHotWatch.TestPrune.CoverageMerge.PartialName ]
            |> List.map (fun name -> Path.Combine(dir, name))
            |> List.filter File.Exists
            |> List.choose (fun path ->
                try
                    File.Delete(path)
                    Some path
                with _ ->
                    None))

/// Execute a parsed command with injectable dependencies.
let executeCommand
    (createDaemon: string -> Daemon)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (command: Command)
    (opts: GlobalOptions)
    (config: DaemonConfiguration)
    (startupTimeoutSeconds: float)
    : int =
    let mode = pickMode opts.AgentMode opts.CompactMode
    let noWarnFail = opts.NoWarnFail

    let ensureDaemonFn () =
        ensureDaemon ipc repoRoot pipeName opts.DaemonExtraArgs config.LogDir startupTimeoutSeconds

    let queryPluginWith (mode: ProgressRenderer.RenderMode) (filter: string) : int =
        ensureAndQueryErrors mode noWarnFail ensureDaemonFn ipc pipeName filter

    let queryPlugin filter =
        queryPluginWith ProgressRenderer.Verbose filter

    let withDaemon (action: unit -> int) : int =
        if not (ensureDaemonFn ()) then
            eprintfn "Failed to start daemon"
            1
        else
            action ()

    let withDaemonAndIpc (action: unit -> int) : int = withDaemon (fun () -> withIpc action)

    match command with
    | Start ->
        let stateDir = Path.Combine(repoRoot, ".fs-hot-watch")
        let pidFile = Path.Combine(stateDir, "daemon.pid")
        let lockFile = Path.Combine(stateDir, "daemon.lock")
        Directory.CreateDirectory(stateDir) |> ignore

        // OS-enforced singleton: hold an exclusive lock on daemon.lock for the
        // daemon's lifetime. Two concurrent `start` invocations cannot both
        // acquire it; the second exits cleanly. Replaces the earlier probe-based
        // guard which had a TOCTOU window between IsRunning check and pipe claim.
        let acquired =
            try
                Some(new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            with :? IOException ->
                None

        match acquired with
        | None ->
            let pidInfo =
                if File.Exists pidFile then
                    $" (pid %s{File.ReadAllText(pidFile).Trim()})"
                else
                    ""

            eprintfn $"Daemon already running at pipe %s{pipeName}%s{pidInfo}"
            0
        | Some lockStream ->
            use _lock = lockStream
            eprintfn $"Starting FsHotWatch daemon for %s{repoRoot}"
            eprintfn $"Pipe: %s{pipeName}"

            // Write our own PID so killStaleDaemon can find the actual daemon process,
            // not the nohup wrapper that launched us.
            File.WriteAllText(pidFile, string (System.Diagnostics.Process.GetCurrentProcess().Id))

            let daemon = createDaemon repoRoot
            registerPlugins daemon repoRoot config
            let cts = new CancellationTokenSource()

            Console.CancelKeyPress.Add(fun e ->
                e.Cancel <- true
                cts.Cancel())

            // Stop the daemon cleanly if `.fs-hot-watch.json` is edited. The
            // user then runs the daemon again to pick up the new config (or
            // sees the error if the edit was invalid). No hot-reload.
            use _configWatcher =
                watchRepoConfigFile repoRoot (fun reason ->
                    FsHotWatch.Logging.info "config" reason
                    cts.Cancel())

            try
                Async.RunSynchronously(daemon.RunWithIpc(pipeName, cts))
            with :? OperationCanceledException ->
                ()

            eprintfn "Daemon stopped."
            0
    | Stop ->
        withIpc (fun () ->
            // Multiple daemons may be listening on the same pipe (historically the
            // start command spawned duplicates); iterate Shutdown until the pipe
            // has been quiet for two consecutive probes so we don't leave orphans
            // behind and don't misreport "No daemon running" while the OS is still
            // tearing down the last pipe endpoint.
            let overallTimeout = TimeSpan.FromSeconds(30.0)
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let mutable stopped = 0
            let mutable consecutiveQuiet = 0

            while consecutiveQuiet < 2 && sw.Elapsed < overallTimeout do
                if ipc.IsRunning pipeName then
                    consecutiveQuiet <- 0

                    try
                        ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
                        stopped <- stopped + 1
                    with _ ->
                        ()
                else
                    consecutiveQuiet <- consecutiveQuiet + 1

                Thread.Sleep(100)

            match stopped with
            | 0 -> UI.info "No daemon running"
            | 1 -> UI.success "Daemon stopped"
            | n -> UI.success $"{n} daemons stopped"

            0)
    | Scan flags ->
        let force = flags |> List.contains Force

        withIpc (fun () ->
            let result = ipc.Scan pipeName force |> Async.RunSynchronously
            UI.success $"Scan: %s{result}"
            0)
    | Status pluginName ->


        withIpc (fun () ->
            let filter = pluginName |> Option.defaultValue ""
            let json = ipc.GetDiagnostics pipeName filter |> Async.RunSynchronously
            let resp = IpcParsing.parseDiagnosticsResponse json

            // GetDiagnostics filters files by plugin but returns all plugin statuses.
            // Narrow Statuses client-side when a specific plugin was requested.
            let scoped =
                match pluginName with
                | None -> resp
                | Some name ->
                    { resp with
                        Statuses = resp.Statuses |> Map.filter (fun k _ -> k = name) }

            match pluginName with
            | Some name when Map.isEmpty scoped.Statuses ->
                eprintfn "not found: %s" name
                1
            | _ ->
                let output =
                    IpcOutput.formatDiagnosticsResponse mode (renderLines mode (not noWarnFail)) scoped

                eprintfn "%s" output
                IpcOutput.exitCodeFromResponse noWarnFail scoped)
    | Build flags when isRunOnce flags ->
        let buildConfig =
            { stripConfig config with
                Build = config.Build }



        RunOnceOutput.runOnceAndReport
            (renderBlock mode (not noWarnFail))
            noWarnFail
            createDaemon
            repoRoot
            buildConfig
            (Some "build")
    | Build flags ->


        withDaemon (fun () ->
            let result =
                if UI.isInteractive then
                    UI.withSpinner "Building" (fun () -> ipc.TriggerBuild pipeName |> Async.RunSynchronously)
                else
                    eprintfn "  Building..."
                    ipc.TriggerBuild pipeName |> Async.RunSynchronously

            IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)
    | Test flags when isRunOnce flags ->
        let testConfig =
            { stripConfig config with
                Build = config.Build
                Tests = config.Tests }



        RunOnceOutput.runOnceAndReport
            (renderBlock mode (not noWarnFail))
            noWarnFail
            createDaemon
            repoRoot
            testConfig
            (Some "test-prune")
    | Test flags ->


        withDaemon (fun () ->
            let result =
                if UI.isInteractive then
                    UI.withSpinner "Running tests" (fun () ->
                        ipc.RunCommand pipeName "run-tests" "{}" |> Async.RunSynchronously)
                else
                    eprintfn "  Running tests..."
                    ipc.RunCommand pipeName "run-tests" "{}" |> Async.RunSynchronously

            IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)
    | Format flags when isRunOnce flags ->
        let formatConfig =
            { stripConfig config with
                Format = FormatMode.Auto }



        RunOnceOutput.runOnceAndReport
            (renderBlock mode (not noWarnFail))
            noWarnFail
            createDaemon
            repoRoot
            formatConfig
            (Some "format")
    | Format flags ->


        withDaemon (fun () ->
            let result =
                if UI.isInteractive then
                    UI.withSpinner "Formatting" (fun () -> ipc.FormatAll pipeName |> Async.RunSynchronously)
                else
                    eprintfn "  Formatting..."
                    ipc.FormatAll pipeName |> Async.RunSynchronously

            IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)
    | Lint flags when isRunOnce flags ->
        let lintConfig = { stripConfig config with Lint = true }


        RunOnceOutput.runOnceAndReport
            (renderBlock mode (not noWarnFail))
            noWarnFail
            createDaemon
            repoRoot
            lintConfig
            (Some "lint")
    | Lint flags -> queryPluginWith (mode) "lint"
    | Analyze flags when isRunOnce flags ->
        let analyzeConfig =
            { stripConfig config with
                Analyzers = config.Analyzers }



        RunOnceOutput.runOnceAndReport
            (renderBlock mode (not noWarnFail))
            noWarnFail
            createDaemon
            repoRoot
            analyzeConfig
            (Some "analyzers")
    | Analyze flags -> queryPluginWith (mode) "analyzers"
    | FormatCheck flags when isRunOnce flags ->
        let formatCheckConfig =
            { stripConfig config with
                Format = FormatMode.Check }



        RunOnceOutput.runOnceAndReport
            (renderBlock mode (not noWarnFail))
            noWarnFail
            createDaemon
            repoRoot
            formatCheckConfig
            (Some "format-check")
    | FormatCheck flags -> queryPluginWith (mode) "format-check"
    | Errors flags ->


        match WaitMode.fromFlags flags with
        | Error msg ->
            eprintfn "fs-hot-watch errors: %s" msg
            2
        | Ok waitMode ->
            withDaemonAndIpc (fun () ->
                let waitResult =
                    match waitMode with
                    | WaitMode.NoWait -> Ok()
                    | WaitMode.WaitFor timeout ->
                        try
                            let timeoutMs = int timeout.TotalMilliseconds
                            ipc.WaitForComplete pipeName timeoutMs |> Async.RunSynchronously |> ignore
                            Ok()
                        with
                        | :? TimeoutException as ex -> Error ex.Message
                        | ex ->
                            let inner = unwrapIpcException ex
                            Error $"daemon stopped or died before wait completed: %s{inner.Message}"

                match waitResult with
                | Error msg ->
                    eprintfn "fs-hot-watch errors --wait: %s" msg
                    2
                | Ok() ->
                    let errorsJson = ipc.GetDiagnostics pipeName "" |> Async.RunSynchronously
                    let resp = IpcParsing.parseDiagnosticsResponse errorsJson

                    eprintfn "%s" (IpcOutput.formatDiagnosticsResponse mode (renderLines mode (not noWarnFail)) resp)

                    IpcOutput.exitCodeFromResponse noWarnFail resp)
    | Rerun pluginName ->
        withDaemonAndIpc (fun () ->
            let result =
                if UI.isInteractive then
                    UI.withSpinner $"Running %s{pluginName}" (fun () ->
                        ipc.RerunPlugin pipeName pluginName |> Async.RunSynchronously)
                else
                    eprintfn "  Running %s..." pluginName
                    ipc.RerunPlugin pipeName pluginName |> Async.RunSynchronously

            IpcOutput.renderIpcResult mode (renderLines mode (not noWarnFail)) noWarnFail result)
    | Init ->
        let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")
        let projects = InitConfig.discoverProjects repoRoot None
        let hasJj = detectDefaultCacheBackend repoRoot = JjFileBackend
        let config = InitConfig.generateConfig projects hasJj
        let json = InitConfig.serializeConfig config

        try
            use fs = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write)
            use sw = new StreamWriter(fs)
            sw.Write(json + "\n")
            printfn "%s" json
            eprintfn "Wrote %s" configPath
            0
        with :? IOException ->
            eprintfn "%s already exists" configPath
            1
    | Check flags when isRunOnce flags ->

        RunOnceOutput.runOnceAndReport (renderBlock mode (not noWarnFail)) noWarnFail createDaemon repoRoot config None
    | Check flags -> queryPluginWith (mode) ""
    | Config ConfigCommand.Check ->
        // Config has already been parsed by main; reaching here means it's valid.
        printfn "config: OK (%d plugins configured)" (countPlugins config)
        0
    | Coverage CoverageCommand.RefreshBaseline ->
        let deleted = refreshCoverageBaseline repoRoot config

        if deleted.IsEmpty then
            printfn "No coverage baseline/partial JSON files found to remove."
        else
            printfn "Removed:"

            for p in deleted do
                printfn "  %s" p

        0
    | Completions ->
        FishCompletions.writeToFile commandTree cliName
        eprintfn "%s" $"%s{Color.green}✓%s{Color.reset} Fish completions installed"
        eprintfn "  Wrote ~/.config/fish/completions/%s.fish" cliName
        0

/// Execute an unknown command as a plugin command via IPC.
let executePluginCommand (ipc: IpcOps) (pipeName: string) (opts: GlobalOptions) (cmd: string) (argsStr: string) : int =
    withIpc (fun () ->
        let mode = pickMode opts.AgentMode opts.CompactMode
        let result = ipc.RunCommand pipeName cmd argsStr |> Async.RunSynchronously
        IpcOutput.renderIpcResult mode (renderLines mode true) false result)

/// Apply parsed global flags: configure logging and return the resolved options.
let applyGlobalFlags (globals: GlobalFlag list) : GlobalOptions =
    let folder (opts: GlobalOptions, parts) flag =
        match flag with
        | Verbose ->
            FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
            opts, "--verbose" :: parts
        | LogLevel level ->
            match level with
            | "error" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error
            | "warning" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Warning
            | "info" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Info
            | "debug" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
            | other ->
                eprintfn "Unknown log level: %s (using info)" other
                FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Info

            opts, $"--log-level %s{level}" :: parts
        | NoCache -> { opts with NoCache = true }, "--no-cache" :: parts
        | NoWarnFail -> { opts with NoWarnFail = true }, parts
        // --agent and --compact are client-side render selectors; don't forward to the daemon.
        | Agent -> { opts with AgentMode = true }, parts
        | Compact -> { opts with CompactMode = true }, parts

    let opts, parts = globals |> List.fold folder (defaultGlobalOptions, [])

    let extraArgs =
        match parts with
        | [] -> ""
        | _ -> (parts |> List.rev |> String.concat " ") + " "

    { opts with
        DaemonExtraArgs = extraArgs }

[<EntryPoint>]
let main args =
    let argList = args |> Array.toList

    // Bare `--help` / `-h` / `help` (no subcommand) prints global help with global flags.
    // Subcommand help (e.g. `errors --help`) is handled by Parse via HelpRequested below
    // so per-command flags like --wait and --timeout actually appear in the output.
    let isHelpToken (a: string) = a = "--help" || a = "-h" || a = "help"

    let onlyHelpRequested =
        match argList with
        | [] -> true
        | args when args |> List.forall isHelpToken -> true
        | _ -> false

    if onlyHelpRequested then
        printfn "%s" (CommandTree.helpWithGlobals commandTree globalSpec.GlobalFlags cliName)
        0
    else

        // Parse before locating the repo root so `<cmd> --help` and `--version`
        // work outside a jj/git checkout.
        let parsed = globalSpec.Parse args

        match parsed with
        | Error(HelpRequested path) ->
            printfn "%s" (CommandTree.helpForPath commandTree path cliName)
            0
        | Error VersionRequested ->
            let version =
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version |> string

            printfn "%s %s" cliName version
            0
        | _ ->

            let repoRoot =
                match findRepoRoot (Directory.GetCurrentDirectory()) with
                | Some root -> root
                | None ->
                    eprintfn "Error: not in a jj or git repository"
                    exit 1
                    ""

            let pipeName = computePipeName repoRoot

            match parsed with
            | Ok(globals, command) ->
                let opts = applyGlobalFlags globals

                let config =
                    try
                        loadConfig repoRoot
                    with ConfigError msg ->
                        eprintfn $"fs-hot-watch: config error: %s{msg}"
                        exit 2

                let cacheConfig = if opts.NoCache then DaemonConfig.NoCache else config.Cache
                let (backend, keyProvider) = DaemonConfig.createCacheComponents repoRoot cacheConfig

                let fileCommandPatterns =
                    config.FileCommands
                    |> List.choose (fun fc -> fc.Pattern)
                    |> List.map FsHotWatch.Watcher.FilePattern.parse

                let createDaemon (root: string) =
                    Daemon.create
                        root
                        { Daemon.DaemonOptions.defaults with
                            CacheBackend = backend
                            CacheKeyProvider = keyProvider
                            ExcludePatterns = config.Exclude
                            ExtraWatchPatterns = fileCommandPatterns }

                executeCommand createDaemon defaultIpcOps repoRoot pipeName command opts config 30.0
            | Error(HelpRequested _)
            | Error VersionRequested ->
                // Already handled above before repo-root lookup.
                0
            | Error(UnknownCommand(input, _path)) ->
                let argsStr =
                    argList
                    |> List.skipWhile (fun a -> a <> input)
                    |> List.skip 1
                    |> String.concat " "

                let opts =
                    { defaultGlobalOptions with
                        AgentMode = argList |> List.exists (fun a -> a = "--agent" || a = "-a")
                        CompactMode = argList |> List.exists (fun a -> a = "--compact" || a = "-q") }

                executePluginCommand defaultIpcOps pipeName opts input argsStr
            | Error(InvalidArguments(cmd, msg)) ->
                eprintfn "Invalid arguments for '%s': %s" cmd msg
                1
            | Error(AmbiguousArgument(input, candidates)) ->
                eprintfn "Ambiguous command '%s'. Did you mean: %s" input (String.concat ", " candidates)
                1
            | Error(UnknownFlag(flag, cmd, validFlags)) ->
                eprintfn "Unknown flag '%s' for '%s'. Valid flags: %s" flag cmd (String.concat ", " validFlags)
                1
            | Error(DuplicateFlag(flag, cmd)) ->
                eprintfn "Flag '%s' provided more than once for '%s'" flag cmd
                1
