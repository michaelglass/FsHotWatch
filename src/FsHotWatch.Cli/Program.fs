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

type RunMode =
    | [<Cmd("Run once and exit")>] RunOnce
    | [<Cmd("Use running daemon"); CmdDefault>] Daemon

type Command =
    | [<Cmd("Start the daemon")>] Start
    | [<Cmd("Stop the daemon")>] Stop
    | [<Cmd("Run all checks")>] Check of RunMode
    | [<Cmd("Build the project")>] Build of RunMode
    | [<Cmd("Run tests")>] Test of RunMode
    | [<Cmd("Format code")>] Format of RunMode
    | [<Cmd("Lint code")>] Lint of RunMode
    | [<Cmd("Check formatting", Name = "format-check")>] FormatCheck of RunMode
    | [<Cmd("Run analyzers")>] Analyze of RunMode
    | [<Cmd("Show current status")>] Status of plugin: string option
    | [<Cmd("Show accumulated errors")>] Errors
    | [<Cmd("Scan for file changes")>] Scan of force: bool option
    | [<Cmd("Invalidate cache for a file"); CmdFileCompletion>] InvalidateCache of filePath: string
    | [<Cmd("Generate initial config")>] Init
    | [<Cmd("Install fish completions")>] Completions

let commandTree =
    CommandReflection.fromUnion<Command> "FsHotWatch — F# file watcher daemon"

let cliName = "fs-hot-watch"

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

/// Injectable IPC operations for testability.
type IpcOps =
    { Shutdown: string -> Async<string>
      Scan: string -> bool -> Async<string>
      ScanStatus: string -> Async<string>
      GetStatus: string -> Async<string>
      GetPluginStatus: string -> string -> Async<string>
      RunCommand: string -> string -> string -> Async<string>
      GetErrors: string -> string -> Async<string>
      WaitForScan: string -> int64 -> Async<string>
      WaitForComplete: string -> Async<string>
      TriggerBuild: string -> Async<string>
      FormatAll: string -> Async<string>
      InvalidateCache: string -> string -> Async<string>
      IsRunning: string -> bool }

/// Default IPC operations using the real IpcClient.
let defaultIpcOps: IpcOps =
    { Shutdown = IpcClient.shutdown
      Scan = IpcClient.scan
      ScanStatus = IpcClient.scanStatus
      GetStatus = IpcClient.getStatus
      GetPluginStatus = IpcClient.getPluginStatus
      RunCommand = IpcClient.runCommand
      GetErrors = IpcClient.getErrors
      WaitForScan = IpcClient.waitForScan
      WaitForComplete = IpcClient.waitForComplete
      TriggerBuild = IpcClient.triggerBuild
      FormatAll = IpcClient.formatAll
      InvalidateCache = IpcClient.invalidateCache
      IsRunning = IpcClient.isRunning }

let private runIpc (action: Async<string>) : int =
    try
        let result = action |> Async.RunSynchronously
        printfn "%s" result
        0
    with ex ->
        eprintfn $"Could not connect to daemon: %s{ex.Message}"
        1

let private runIpcWithExitCode (action: Async<string>) : int =
    try
        let result = action |> Async.RunSynchronously
        printfn "%s" result

        // Determine exit code from JSON result
        try
            use doc = System.Text.Json.JsonDocument.Parse(result)
            let root = doc.RootElement

            // {"count": 0} → success
            match root.TryGetProperty("count") with
            | true, v when v.GetInt32() = 0 -> 0
            | true, _ -> 1
            | false, _ ->

                // {"error": "..."} → failure
                match root.TryGetProperty("error") with
                | true, _ -> 1
                | false, _ ->

                    // {"status": "passed"} → success, {"status": "failed"} → failure
                    match root.TryGetProperty("status") with
                    | true, v when v.GetString() = "passed" -> 0
                    | true, v when v.GetString() = "failed" -> 1
                    | _ -> 0
        with _ ->
            // Non-JSON response (e.g., plain status string) — success
            0
    with ex ->
        eprintfn "Could not connect to daemon: %s" ex.Message
        1

/// Ensure daemon, wait for scan + plugin completion, query errors for a plugin.
let private ensureAndQueryErrors
    (ensureDaemon: unit -> bool)
    (ipc: IpcOps)
    (pipeName: string)
    (pluginLabel: string)
    (pluginFilter: string)
    : int =
    if not (ensureDaemon ()) then
        eprintfn "Failed to start daemon"
        1
    else
        eprintfn "  Waiting for scan to complete..."
        let scanResult = ipc.WaitForScan pipeName -1L |> Async.RunSynchronously
        eprintfn "  Scan: %s" scanResult
        eprintfn "  Waiting for %s to complete..." pluginLabel
        ipc.WaitForComplete pipeName |> Async.RunSynchronously |> ignore
        runIpcWithExitCode (ipc.GetErrors pipeName pluginFilter)

/// Compute a hash of the config file + CLI binary for staleness detection.
let private computeConfigHash (repoRoot: string) =
    let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")
    let exePath = Environment.ProcessPath

    let configContent =
        if File.Exists configPath then
            File.ReadAllText configPath
        else
            ""

    let exeModTime =
        if File.Exists exePath then
            File.GetLastWriteTimeUtc(exePath).Ticks.ToString()
        else
            ""

    let hash =
        Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(configContent + exeModTime))

    Convert.ToHexStringLower(hash).Substring(0, 16)

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

/// Kill a stale daemon process by PID file.
let private killStaleDaemon (repoRoot: string) =
    let pidPath = Path.Combine(repoRoot, ".fs-hot-watch", "daemon.pid")

    if File.Exists pidPath then
        try
            let pid = File.ReadAllText(pidPath).Trim() |> int

            try
                let proc = System.Diagnostics.Process.GetProcessById(pid)
                eprintfn "  Killing stale daemon (PID %d)..." pid
                proc.Kill()
                proc.WaitForExit(5000) |> ignore
            with ex ->
                eprintfn "  Could not kill PID %d: %s" pid ex.Message

            File.Delete(pidPath)
        with ex ->
            eprintfn "  Could not clean up stale daemon: %s" ex.Message

let private startFreshDaemon
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (currentHash: string)
    (extraArgs: string)
    : bool =
    let stateDir = Path.Combine(repoRoot, ".fs-hot-watch")
    let logDir = Path.Combine(repoRoot, "log")
    Directory.CreateDirectory(logDir) |> ignore
    let logFile = Path.Combine(logDir, "daemon.log")
    eprintfn "Starting daemon... (log: %s)" logFile
    let exe = Environment.ProcessPath
    // Launch via nohup to fully detach from parent process group so
    // mise/task-runners don't hang waiting for the daemon subprocess.
    // The daemon writes its own PID to daemon.pid (not echo $! which gives the nohup wrapper PID).
    let psi =
        System.Diagnostics.ProcessStartInfo(
            "/bin/sh",
            $"-c \"nohup '%s{exe}' %s{extraArgs}start >> '%s{logFile}' 2>&1 &\""
        )

    psi.WorkingDirectory <- repoRoot
    psi.UseShellExecute <- false
    let proc = System.Diagnostics.Process.Start(psi)
    proc.WaitForExit()
    Directory.CreateDirectory(stateDir) |> ignore
    File.WriteAllText(Path.Combine(stateDir, "config.hash"), currentHash)
    let deadline = DateTime.UtcNow.AddSeconds(30.0)

    while not (ipc.IsRunning pipeName) && DateTime.UtcNow < deadline do
        Thread.Sleep(100)

    ipc.IsRunning pipeName

let private ensureDaemon (ipc: IpcOps) (repoRoot: string) (pipeName: string) (extraArgs: string) : bool =
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
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs
    | StartFresh ->
        killStaleDaemon repoRoot
        startFreshDaemon ipc repoRoot pipeName currentHash extraArgs

/// Execute a parsed command with injectable dependencies.
let executeCommand
    (createDaemon: string -> Daemon)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (command: Command)
    (daemonExtraArgs: string)
    (config: DaemonConfiguration)
    : int =
    let ensureDaemonFn () =
        ensureDaemon ipc repoRoot pipeName daemonExtraArgs

    let queryPlugin label filter =
        ensureAndQueryErrors ensureDaemonFn ipc pipeName label filter

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
    | Stop -> runIpc (ipc.Shutdown pipeName)
    | Scan force -> runIpc (ipc.Scan pipeName (force = Some true))
    | Status None -> runIpc (ipc.GetStatus pipeName)
    | Status(Some pluginName) -> runIpc (ipc.GetPluginStatus pipeName pluginName)
    | Build RunOnce ->
        let buildConfig = stripConfig config
        RunOnceOutput.runOnceAndReport createDaemon repoRoot buildConfig (Some "build")
    | Build Daemon ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            eprintfn "  Triggering build..."
            runIpcWithExitCode (ipc.TriggerBuild pipeName)
    | Test RunOnce ->
        let testConfig =
            { stripConfig config with
                Build = config.Build
                Tests = config.Tests }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot testConfig (Some "test-prune")
    | Test Daemon ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpcWithExitCode (ipc.RunCommand pipeName "run-tests" "{}")
    | Format RunOnce ->
        let formatConfig =
            { stripConfig config with
                Format = FormatMode.Auto }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot formatConfig (Some "format")
    | Format Daemon ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpc (ipc.FormatAll pipeName)
    | Lint RunOnce ->
        let lintConfig = { stripConfig config with Lint = true }
        RunOnceOutput.runOnceAndReport createDaemon repoRoot lintConfig (Some "lint")
    | Lint Daemon -> queryPlugin "lint" "lint"
    | Analyze RunOnce ->
        let analyzeConfig =
            { stripConfig config with
                Analyzers = config.Analyzers }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot analyzeConfig (Some "analyzers")
    | Analyze Daemon -> queryPlugin "analyzers" "analyzers"
    | FormatCheck RunOnce ->
        let formatCheckConfig =
            { stripConfig config with
                Format = FormatMode.Check }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot formatCheckConfig (Some "format")
    | FormatCheck Daemon -> queryPlugin "format check" "format"
    | Errors ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpcWithExitCode (ipc.GetErrors pipeName "")
    | InvalidateCache filePath ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpcWithExitCode (ipc.InvalidateCache pipeName filePath)
    | Init ->
        let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")
        let projects = InitConfig.discoverProjects repoRoot
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
    | Check RunOnce -> RunOnceOutput.runOnceAndReport createDaemon repoRoot config None
    | Check Daemon -> queryPlugin "all checks" ""
    | Completions ->
        FishCompletions.writeToFile commandTree cliName
        eprintfn "%s" $"%s{Color.green}✓%s{Color.reset} Fish completions installed"
        eprintfn "  Wrote ~/.config/fish/completions/%s.fish" cliName
        0

/// Execute an unknown command as a plugin command via IPC.
let executePluginCommand (ipc: IpcOps) (pipeName: string) (cmd: string) (argsStr: string) : int =
    runIpc (ipc.RunCommand pipeName cmd argsStr)

[<EntryPoint>]
let main args =
    let argList = args |> Array.toList

    let logLevelArg =
        argList
        |> List.tryFind (fun a -> a.StartsWith("--log-level="))
        |> Option.map (fun a -> a.Substring("--log-level=".Length))

    if argList |> List.exists (fun a -> a = "--verbose" || a = "-v") then
        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug

    match logLevelArg with
    | Some "error" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error
    | Some "warning" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Warning
    | Some "info" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Info
    | Some "debug" -> FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Debug
    | Some other -> eprintfn "Unknown log level: %s (using info)" other
    | None -> ()

    let noCache = argList |> List.exists (fun a -> a = "--no-cache")

    let filteredArgs =
        argList
        |> List.filter (fun a ->
            a <> "--verbose"
            && a <> "-v"
            && a <> "--no-cache"
            && not (a.StartsWith("--log-level=")))

    // Build extra args string to forward logging flags to daemon subprocess
    let daemonExtraArgs =
        let parts =
            [ match logLevelArg with
              | Some("error" | "warning" | "info" | "debug" as level) -> $"--log-level=%s{level}"
              | Some _ -> () // invalid level already warned; don't forward
              | None ->
                  if argList |> List.exists (fun a -> a = "--verbose" || a = "-v") then
                      "--verbose"
              if noCache then
                  "--no-cache" ]

        if parts.IsEmpty then
            ""
        else
            (String.concat " " parts) + " "

    let repoRoot =
        match findRepoRoot (Directory.GetCurrentDirectory()) with
        | Some root -> root
        | None ->
            eprintfn "Error: not in a jj or git repository"
            exit 1
            ""

    let pipeName = computePipeName repoRoot

    // Load config to determine cache backend, then create daemon factory with cache wired in
    let config = loadConfig repoRoot

    let cacheConfig = if noCache then DaemonConfig.NoCache else config.Cache

    let (backend, keyProvider) = DaemonConfig.createCacheComponents repoRoot cacheConfig

    let createDaemon (root: string) = Daemon.create root backend keyProvider

    let filteredArgArray = filteredArgs |> Array.ofList

    // Handle --help/-h explicitly (CommandTree doesn't support flag syntax)
    if filteredArgs |> List.exists (fun a -> a = "--help" || a = "-h" || a = "help") then
        printfn "%s" (CommandTree.helpFull commandTree cliName)
        0
    else

        match CommandTree.parse commandTree filteredArgArray with
        | Ok command -> executeCommand createDaemon defaultIpcOps repoRoot pipeName command daemonExtraArgs config
        | Error(HelpRequested path) ->
            printfn "%s" (CommandTree.helpForPath commandTree path cliName)
            0
        | Error(UnknownCommand(input, _path)) ->
            let argsStr = filteredArgs |> List.skip 1 |> String.concat " "

            executePluginCommand defaultIpcOps pipeName input argsStr
        | Error(InvalidArguments(cmd, msg)) ->
            eprintfn "Invalid arguments for '%s': %s" cmd msg
            1
        | Error(AmbiguousArgument(input, candidates)) ->
            eprintfn "Ambiguous command '%s'. Did you mean: %s" input (String.concat ", " candidates)
            1
