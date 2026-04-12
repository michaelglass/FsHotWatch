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

type Command =
    | [<Cmd("Start the daemon")>] Start
    | [<Cmd("Stop the daemon")>] Stop
    | [<Cmd("Run all checks")>] Check of RunFlag list
    | [<Cmd("Build the project")>] Build of RunFlag list
    | [<Cmd("Run tests")>] Test of RunFlag list
    | [<Cmd("Format code")>] Format of RunFlag list
    | [<Cmd("Lint code")>] Lint of RunFlag list
    | [<Cmd("Check formatting", Name = "format-check")>] FormatCheck of RunFlag list
    | [<Cmd("Run analyzers")>] Analyze of RunFlag list
    | [<Cmd("Show current status")>] Status of plugin: string option
    | [<Cmd("Show accumulated errors")>] Errors
    | [<Cmd("Scan for file changes")>] Scan of ScanFlag list
    | [<Cmd("Invalidate cache for a file"); CmdFileCompletion>] InvalidateCache of filePath: string
    | [<Cmd("Generate initial config")>] Init
    | [<Cmd("Install fish completions")>] Completions

type GlobalFlag =
    | [<CmdFlag(Short = "v")>] Verbose
    | [<CmdFlag(Name = "log-level")>] LogLevel of string
    | [<CmdFlag(Name = "no-cache")>] NoCache
    | [<CmdFlag(Name = "no-warn-fail")>] NoWarnFail

let globalSpec =
    CommandReflection.fromUnionWithGlobalsAndEnv<Command, GlobalFlag>
        "FsHotWatch — F# file watcher daemon"
        "FS_HOT_WATCH"

let commandTree = globalSpec.Tree

let cliName = "fs-hot-watch"

let private isRunOnce = List.contains RunOnce

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
      WaitForComplete: string -> Async<string>
      TriggerBuild: string -> Async<string>
      FormatAll: string -> Async<string>
      InvalidateCache: string -> string -> Async<string>
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
      InvalidateCache = IpcClient.invalidateCache
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

/// Wrap an IPC call with connection error handling.
let private withIpc (action: unit -> int) : int =
    try
        action ()
    with ex ->
        eprintfn "Could not connect to daemon: %s" ex.Message
        1

/// Ensure daemon, poll for progress, render colored output.
let private ensureAndQueryErrors
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
        IpcOutput.pollAndRender
            noWarnFail
            (fun () -> ipc.WaitForScan pipeName -1L |> Async.RunSynchronously)
            (fun () -> ipc.GetStatus pipeName |> Async.RunSynchronously)
            (fun () -> ipc.GetDiagnostics pipeName pluginFilter |> Async.RunSynchronously)

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
    (startupTimeoutSeconds: float)
    : bool =
    let stateDir = Path.Combine(repoRoot, ".fs-hot-watch")
    let logDir = Path.Combine(repoRoot, "log")
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
    (startupTimeoutSeconds: float)
    : bool =
    startFreshDaemonWith defaultFileOps ipc repoRoot pipeName currentHash extraArgs startupTimeoutSeconds

let private ensureDaemon
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (extraArgs: string)
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
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs startupTimeoutSeconds
    | StartFresh ->
        killStaleDaemon repoRoot
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs startupTimeoutSeconds

/// Execute a parsed command with injectable dependencies.
let executeCommand
    (createDaemon: string -> Daemon)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (command: Command)
    (daemonExtraArgs: string)
    (noWarnFail: bool)
    (config: DaemonConfiguration)
    (startupTimeoutSeconds: float)
    : int =
    let ensureDaemonFn () =
        ensureDaemon ipc repoRoot pipeName daemonExtraArgs startupTimeoutSeconds

    let queryPlugin filter =
        ensureAndQueryErrors noWarnFail ensureDaemonFn ipc pipeName filter

    let withDaemon (action: unit -> int) : int =
        if not (ensureDaemonFn ()) then
            eprintfn "Failed to start daemon"
            1
        else
            action ()

    match command with
    | Start ->
        eprintfn $"Starting FsHotWatch daemon for %s{repoRoot}"
        eprintfn $"Pipe: %s{pipeName}"
        // Write our own PID so killStaleDaemon can find the actual daemon process,
        // not the nohup wrapper that launched us.
        let stateDir = Path.Combine(repoRoot, ".fs-hot-watch")
        Directory.CreateDirectory(stateDir) |> ignore

        File.WriteAllText(
            Path.Combine(stateDir, "daemon.pid"),
            string (System.Diagnostics.Process.GetCurrentProcess().Id)
        )

        let daemon = createDaemon repoRoot
        registerPlugins daemon repoRoot config
        let cts = new CancellationTokenSource()

        Console.CancelKeyPress.Add(fun e ->
            e.Cancel <- true
            cts.Cancel())

        try
            Async.RunSynchronously(daemon.RunWithIpc(pipeName, cts))
        with :? OperationCanceledException ->
            ()

        eprintfn "Daemon stopped."
        0
    | Stop ->
        withIpc (fun () ->
            ipc.Shutdown pipeName |> Async.RunSynchronously |> ignore
            UI.success "Daemon stopped"
            0)
    | Scan flags ->
        let force = flags |> List.contains Force

        withIpc (fun () ->
            let result = ipc.Scan pipeName force |> Async.RunSynchronously
            UI.success $"Scan: %s{result}"
            0)
    | Status None ->
        withIpc (fun () ->
            let json = ipc.GetStatus pipeName |> Async.RunSynchronously
            let parsed = IpcOutput.parseStatusMap (IpcOutput.parseStatusJson json)
            eprintfn "%s" (IpcOutput.renderProgress parsed)
            0)
    | Status(Some pluginName) ->
        withIpc (fun () ->
            let result = ipc.GetPluginStatus pipeName pluginName |> Async.RunSynchronously
            let parsed = IpcOutput.parseStatusMap (Map.ofList [ pluginName, result ])
            eprintfn "%s" (IpcOutput.renderProgress parsed)
            0)
    | Build flags when isRunOnce flags ->
        let buildConfig =
            { stripConfig config with
                Build = config.Build }

        RunOnceOutput.runOnceAndReport noWarnFail createDaemon repoRoot buildConfig (Some "build")
    | Build _ ->
        withDaemon (fun () ->
            let result =
                if UI.isInteractive then
                    UI.withSpinner "Building" (fun () -> ipc.TriggerBuild pipeName |> Async.RunSynchronously)
                else
                    eprintfn "  Building..."
                    ipc.TriggerBuild pipeName |> Async.RunSynchronously

            IpcOutput.renderIpcResult noWarnFail result)
    | Test flags when isRunOnce flags ->
        let testConfig =
            { stripConfig config with
                Build = config.Build
                Tests = config.Tests }

        RunOnceOutput.runOnceAndReport noWarnFail createDaemon repoRoot testConfig (Some "test-prune")
    | Test _ ->
        withDaemon (fun () ->
            let result =
                if UI.isInteractive then
                    UI.withSpinner "Running tests" (fun () ->
                        ipc.RunCommand pipeName "run-tests" "{}" |> Async.RunSynchronously)
                else
                    eprintfn "  Running tests..."
                    ipc.RunCommand pipeName "run-tests" "{}" |> Async.RunSynchronously

            IpcOutput.renderIpcResult noWarnFail result)
    | Format flags when isRunOnce flags ->
        let formatConfig =
            { stripConfig config with
                Format = FormatMode.Auto }

        RunOnceOutput.runOnceAndReport noWarnFail createDaemon repoRoot formatConfig (Some "format")
    | Format _ ->
        withDaemon (fun () ->
            let result =
                if UI.isInteractive then
                    UI.withSpinner "Formatting" (fun () -> ipc.FormatAll pipeName |> Async.RunSynchronously)
                else
                    eprintfn "  Formatting..."
                    ipc.FormatAll pipeName |> Async.RunSynchronously

            IpcOutput.renderIpcResult noWarnFail result)
    | Lint flags when isRunOnce flags ->
        let lintConfig = { stripConfig config with Lint = true }
        RunOnceOutput.runOnceAndReport noWarnFail createDaemon repoRoot lintConfig (Some "lint")
    | Lint _ -> queryPlugin "lint"
    | Analyze flags when isRunOnce flags ->
        let analyzeConfig =
            { stripConfig config with
                Analyzers = config.Analyzers }

        RunOnceOutput.runOnceAndReport noWarnFail createDaemon repoRoot analyzeConfig (Some "analyzers")
    | Analyze _ -> queryPlugin "analyzers"
    | FormatCheck flags when isRunOnce flags ->
        let formatCheckConfig =
            { stripConfig config with
                Format = FormatMode.Check }

        RunOnceOutput.runOnceAndReport noWarnFail createDaemon repoRoot formatCheckConfig (Some "format-check")
    | FormatCheck _ -> queryPlugin "format-check"
    | Errors ->
        withDaemon (fun () ->
            withIpc (fun () ->
                let errorsJson = ipc.GetDiagnostics pipeName "" |> Async.RunSynchronously
                let resp = IpcOutput.parseDiagnosticsResponse errorsJson
                eprintfn "%s" (IpcOutput.formatDiagnosticsResponse resp)
                IpcOutput.exitCodeFromResponse noWarnFail resp))
    | InvalidateCache filePath ->
        withDaemon (fun () ->
            withIpc (fun () ->
                let result = ipc.InvalidateCache pipeName filePath |> Async.RunSynchronously
                IpcOutput.renderIpcResult noWarnFail result))
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
    | Check flags when isRunOnce flags -> RunOnceOutput.runOnceAndReport noWarnFail createDaemon repoRoot config None
    | Check _ -> queryPlugin ""
    | Completions ->
        FishCompletions.writeToFile commandTree cliName
        eprintfn "%s" $"%s{Color.green}✓%s{Color.reset} Fish completions installed"
        eprintfn "  Wrote ~/.config/fish/completions/%s.fish" cliName
        0

/// Execute an unknown command as a plugin command via IPC.
let executePluginCommand (ipc: IpcOps) (pipeName: string) (cmd: string) (argsStr: string) : int =
    withIpc (fun () ->
        let result = ipc.RunCommand pipeName cmd argsStr |> Async.RunSynchronously
        IpcOutput.renderIpcResult false result)

/// Apply parsed global flags: configure logging and return (noCache, noWarnFail, daemonExtraArgs).
let applyGlobalFlags (globals: GlobalFlag list) : bool * bool * string =
    let (noCache, noWarnFail, parts) =
        globals
        |> List.fold
            (fun (nc, nwf, acc) flag ->
                match flag with
                | Verbose ->
                    FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
                    (nc, nwf, "--verbose" :: acc)
                | LogLevel level ->
                    match level with
                    | "error" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error
                    | "warning" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Warning
                    | "info" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Info
                    | "debug" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
                    | other ->
                        eprintfn "Unknown log level: %s (using info)" other
                        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Info

                    (nc, nwf, $"--log-level %s{level}" :: acc)
                | NoCache -> (true, nwf, "--no-cache" :: acc)
                | NoWarnFail -> (nc, true, acc))
            (false, false, [])

    let extraArgs =
        match parts with
        | [] -> ""
        | _ -> (parts |> List.rev |> String.concat " ") + " "

    (noCache, noWarnFail, extraArgs)

[<EntryPoint>]
let main args =
    let argList = args |> Array.toList

    // Handle --help/-h as global flags (CommandTree handles subcommand help via HelpRequested)
    if argList |> List.exists (fun a -> a = "--help" || a = "-h" || a = "help") then
        printfn "%s" (CommandTree.helpWithGlobals commandTree globalSpec.GlobalFlags cliName)
        0
    else

        let repoRoot =
            match findRepoRoot (Directory.GetCurrentDirectory()) with
            | Some root -> root
            | None ->
                eprintfn "Error: not in a jj or git repository"
                exit 1
                ""

        let pipeName = computePipeName repoRoot

        match globalSpec.Parse args with
        | Ok(globals, command) ->
            let (noCache, noWarnFail, daemonExtraArgs) = applyGlobalFlags globals
            let config = loadConfig repoRoot
            let cacheConfig = if noCache then DaemonConfig.NoCache else config.Cache
            let (backend, keyProvider) = DaemonConfig.createCacheComponents repoRoot cacheConfig

            let createDaemon (root: string) =
                Daemon.create root backend keyProvider None config.Exclude

            executeCommand createDaemon defaultIpcOps repoRoot pipeName command daemonExtraArgs noWarnFail config 30.0
        | Error(HelpRequested path) ->
            printfn "%s" (CommandTree.helpForPath commandTree path cliName)
            0
        | Error VersionRequested ->
            let version =
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version |> string

            printfn "%s %s" cliName version
            0
        | Error(UnknownCommand(input, _path)) ->
            let argsStr =
                argList
                |> List.skipWhile (fun a -> a <> input)
                |> List.skip 1
                |> String.concat " "

            executePluginCommand defaultIpcOps pipeName input argsStr
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
