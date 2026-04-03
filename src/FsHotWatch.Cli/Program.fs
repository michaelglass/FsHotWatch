module FsHotWatch.Cli.Program

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Daemon
open FsHotWatch.Ipc

/// CLI command types.
type Command =
    | Start of runOnce: bool
    | Stop
    | Scan of force: bool
    | ScanStatus
    | Status of pluginName: string option
    | PluginCommand of name: string * args: string
    | Build of runOnce: bool
    | Test of args: string * runOnce: bool
    | Format of runOnce: bool
    | Lint of runOnce: bool
    | Errors
    | Check
    | AnalyzeCheck of runOnce: bool
    | FormatCheck of runOnce: bool
    | InvalidateCache of filePath: string
    | Init
    | Help

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

/// Parse CLI arguments into a Command.
let parseCommand (args: string list) : Command =
    match args with
    | []
    | [ "help" ]
    | [ "--help" ]
    | [ "-h" ] -> Help
    | [ "start" ] -> Start false
    | [ "start"; "--run-once" ] -> Start true
    | [ "stop" ] -> Stop
    | [ "scan" ] -> Scan false
    | [ "scan"; "--force" ] -> Scan true
    | [ "scan-status" ] -> ScanStatus
    | [ "status" ] -> Status None
    | [ "status"; pluginName ] -> Status(Some pluginName)
    | [ "build" ] -> Build false
    | [ "build"; "--run-once" ] -> Build true
    | [ "test" ] -> Test("{}", false)
    | "test" :: rest ->
        // Convert CLI flags to JSON for run-tests command
        let mutable projects = []
        let mutable filter = None
        let mutable onlyFailed = false
        let mutable runOnce = false
        let mutable i = 0
        let args = rest |> Array.ofList

        while i < args.Length do
            match args.[i] with
            | "--project"
            | "-p" when i + 1 < args.Length ->
                projects <- args.[i + 1] :: projects
                i <- i + 2
            | "--filter"
            | "-f" when i + 1 < args.Length ->
                filter <- Some args.[i + 1]
                i <- i + 2
            | "--only-failed" ->
                onlyFailed <- true
                i <- i + 1
            | "--run-once" ->
                runOnce <- true
                i <- i + 1
            | _ -> i <- i + 1

        let json = System.Text.StringBuilder("{")

        if onlyFailed then
            json.Append("\"only-failed\": true") |> ignore

        if not projects.IsEmpty then
            let ps =
                projects |> List.rev |> List.map (fun p -> $"\"%s{p}\"") |> String.concat ", "

            if json.Length > 1 then
                json.Append(", ") |> ignore

            json.Append($"\"projects\": [%s{ps}]") |> ignore

        match filter with
        | Some f ->
            if json.Length > 1 then
                json.Append(", ") |> ignore

            json.Append($"\"filter\": \"%s{f}\"") |> ignore
        | None -> ()

        json.Append("}") |> ignore
        Test(json.ToString(), runOnce)
    | [ "analyze" ] -> AnalyzeCheck false
    | [ "analyze"; "--run-once" ] -> AnalyzeCheck true
    | [ "format"; "--check" ] -> FormatCheck false
    | [ "format"; "--check"; "--run-once" ] -> FormatCheck true
    | [ "format" ] -> Format false
    | [ "format"; "--run-once" ] -> Format true
    | [ "lint" ] -> Lint false
    | [ "lint"; "--run-once" ] -> Lint true
    | [ "errors" ] -> Errors
    | [ "check" ] -> Check
    | [ "invalidate-cache"; path ] -> InvalidateCache path
    | [ "init" ] -> Init
    | cmd :: rest ->
        let argsStr = if rest.IsEmpty then "" else String.concat " " rest
        PluginCommand(cmd, argsStr)

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

let private showHelp () =
    printfn "FsHotWatch — F# file watcher daemon"
    printfn ""
    printfn "Usage: fs-hot-watch <command>"
    printfn ""
    printfn "Commands:"
    printfn "  start [--run-once]  Start daemon (--run-once: single pass, exit)"
    printfn "  stop               Stop running daemon"
    printfn "  scan [--force]      Re-scan all files (--force bypasses jj guard)"
    printfn "  scan-status        Check scan progress without blocking"
    printfn "  status [plugin]    Show plugin statuses"
    printfn "  build [--run-once]  Trigger a build and wait for completion"
    printfn "  test [opts]        Run tests (-p project, -f filter, --only-failed, --run-once)"
    printfn "  format [--run-once]  Run formatter on all files"
    printfn "  lint [--run-once]  Run linter on all files"
    printfn "  analyze [--run-once] Run analyzers and report errors"
    printfn "  format --check [--run-once] Check formatting without modifying files"
    printfn "  errors             Show current errors from all plugins"
    printfn "  check              Full check: scan, build, lint, then report errors"
    printfn "  invalidate-cache <file>    Invalidate cache for a file and re-check it"
    printfn "  init               Generate .fs-hot-watch.json from discovered projects"
    printfn "  <command> [args]   Run a plugin-registered command"
    printfn ""
    printfn "Options:"
    printfn "  -v, --verbose              Show per-file status transitions (same as --log-level=debug)"
    printfn "  --log-level=<level>        Set log level: error, warning, info, debug (default: info)"
    printfn "  --no-cache                 Disable check result cache"

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
    // Launch via shell with nohup to fully detach from parent process group.
    // Without this, mise (and similar task runners) wait for all child processes
    // to exit, causing them to hang even after the CLI client completes.
    // Launch with nohup so mise/task-runners don't wait for us.
    // The daemon writes its own PID to daemon.pid on startup (not echo $! which gives the nohup wrapper PID).
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
    match command with
    | Help ->
        showHelp ()
        0
    | Start true -> RunOnceOutput.runOnceAndReport createDaemon repoRoot config None
    | Start false ->
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
    | Scan force -> runIpc (ipc.Scan pipeName force)
    | ScanStatus -> runIpc (ipc.ScanStatus pipeName)
    | Status None -> runIpc (ipc.GetStatus pipeName)
    | Status(Some pluginName) -> runIpc (ipc.GetPluginStatus pipeName pluginName)
    | PluginCommand(cmd, argsJson) -> runIpc (ipc.RunCommand pipeName cmd argsJson)
    | Build true ->
        let buildConfig = stripConfig config
        RunOnceOutput.runOnceAndReport createDaemon repoRoot buildConfig (Some "build")
    | Build false ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            eprintfn "  Triggering build..."
            runIpcWithExitCode (ipc.TriggerBuild pipeName)
    | Test(_, true) ->
        let testConfig =
            { stripConfig config with
                Build = config.Build
                Tests = config.Tests }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot testConfig (Some "test-prune")
    | Test(argsJson, false) ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpcWithExitCode (ipc.RunCommand pipeName "run-tests" argsJson)
    | Format true ->
        let formatConfig =
            { stripConfig config with
                Format = FormatMode.Auto }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot formatConfig (Some "format")
    | Format false ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpc (ipc.FormatAll pipeName)
    | Lint true ->
        let lintConfig = { stripConfig config with Lint = true }
        RunOnceOutput.runOnceAndReport createDaemon repoRoot lintConfig (Some "lint")
    | Lint false ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            eprintfn "  Waiting for scan to complete..."
            let scanResult = ipc.WaitForScan pipeName -1L |> Async.RunSynchronously
            eprintfn "  Scan: %s" scanResult
            eprintfn "  Waiting for lint to complete..."
            ipc.WaitForComplete pipeName |> Async.RunSynchronously |> ignore
            runIpcWithExitCode (ipc.GetErrors pipeName "lint")
    | AnalyzeCheck true ->
        let analyzeConfig =
            { stripConfig config with
                Analyzers = config.Analyzers }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot analyzeConfig (Some "analyzers")
    | AnalyzeCheck false ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            eprintfn "  Waiting for scan to complete..."
            let scanResult = ipc.WaitForScan pipeName -1L |> Async.RunSynchronously
            eprintfn "  Scan: %s" scanResult
            eprintfn "  Waiting for analyzers to complete..."
            ipc.WaitForComplete pipeName |> Async.RunSynchronously |> ignore
            runIpcWithExitCode (ipc.GetErrors pipeName "analyzers")
    | FormatCheck true ->
        let formatCheckConfig =
            { stripConfig config with
                Format = FormatMode.Check }

        RunOnceOutput.runOnceAndReport createDaemon repoRoot formatCheckConfig (Some "format")
    | FormatCheck false ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            eprintfn "  Waiting for scan to complete..."
            let scanResult = ipc.WaitForScan pipeName -1L |> Async.RunSynchronously
            eprintfn "  Scan: %s" scanResult
            eprintfn "  Waiting for format check to complete..."
            ipc.WaitForComplete pipeName |> Async.RunSynchronously |> ignore
            runIpcWithExitCode (ipc.GetErrors pipeName "format")
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
    | Check ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            // Wait for the daemon's initial scan (triggered by RunWithIpc on startup)
            eprintfn "  Waiting for scan to complete..."
            let scanResult = ipc.WaitForScan pipeName -1L |> Async.RunSynchronously
            eprintfn "  Scan: %s" scanResult
            eprintfn "  Waiting for all plugins to complete..."
            ipc.WaitForComplete pipeName |> Async.RunSynchronously |> ignore
            eprintfn "  Collecting errors..."
            runIpcWithExitCode (ipc.GetErrors pipeName "")

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

    let command = parseCommand filteredArgs
    executeCommand createDaemon defaultIpcOps repoRoot pipeName command daemonExtraArgs config
