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
    | Start
    | Stop
    | Scan
    | ScanStatus
    | Status of pluginName: string option
    | PluginCommand of name: string * args: string
    | Build
    | Test of args: string
    | Format
    | Lint
    | Errors
    | Check
    | InvalidateCache of filePath: string
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
    | [ "start" ] -> Start
    | [ "stop" ] -> Stop
    | [ "scan" ] -> Scan
    | [ "scan-status" ] -> ScanStatus
    | [ "status" ] -> Status None
    | [ "status"; pluginName ] -> Status(Some pluginName)
    | [ "build" ] -> Build
    | [ "test" ] -> Test "{}"
    | "test" :: rest ->
        // Convert CLI flags to JSON for run-tests command
        let mutable projects = []
        let mutable filter = None
        let mutable onlyFailed = false
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
        Test(json.ToString())
    | [ "format" ] -> Format
    | [ "lint" ] -> Lint
    | [ "errors" ] -> Errors
    | [ "check" ] -> Check
    | [ "invalidate-cache"; path ] -> InvalidateCache path
    | cmd :: rest ->
        let argsStr = if rest.IsEmpty then "" else String.concat " " rest
        PluginCommand(cmd, argsStr)

/// Injectable IPC operations for testability.
type IpcOps =
    { Shutdown: string -> Async<string>
      Scan: string -> Async<string>
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
    printfn "  start              Start daemon in foreground (auto-scans on boot)"
    printfn "  stop               Stop running daemon"
    printfn "  scan               Re-scan all files"
    printfn "  scan-status        Check scan progress without blocking"
    printfn "  status [plugin]    Show plugin statuses"
    printfn "  build              Trigger a build and wait for completion"
    printfn "  test [opts]        Run tests (-p project, -f filter, --only-failed)"
    printfn "  format             Run formatter on all files"
    printfn "  lint               Run linter on all files"
    printfn "  errors             Show current errors from all plugins"
    printfn "  check              Full check: scan, build, lint, then report errors"
    printfn "  invalidate-cache <file>    Invalidate cache for a file and re-check it"
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
            with _ ->
                ()

            File.Delete(pidPath)
        with _ ->
            ()

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
        with _ ->
            ()

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
    | Scan -> runIpc (ipc.Scan pipeName)
    | ScanStatus -> runIpc (ipc.ScanStatus pipeName)
    | Status None -> runIpc (ipc.GetStatus pipeName)
    | Status(Some pluginName) -> runIpc (ipc.GetPluginStatus pipeName pluginName)
    | PluginCommand(cmd, argsJson) -> runIpc (ipc.RunCommand pipeName cmd argsJson)
    | Build ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            eprintfn "  Triggering build..."
            runIpcWithExitCode (ipc.TriggerBuild pipeName)
    | Test argsJson ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpcWithExitCode (ipc.RunCommand pipeName "run-tests" argsJson)
    | Format ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpc (ipc.FormatAll pipeName)
    | Lint ->
        if not (ensureDaemon ipc repoRoot pipeName daemonExtraArgs) then
            eprintfn "Failed to start daemon"
            1
        else
            runIpcWithExitCode (ipc.RunCommand pipeName "lint" "")
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
