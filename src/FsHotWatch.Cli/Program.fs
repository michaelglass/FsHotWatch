module FsHotWatch.Cli.Program

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
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
      RunCommand: string -> string -> string -> Async<string> }

/// Default IPC operations using the real IpcClient.
let defaultIpcOps: IpcOps =
    { Shutdown = IpcClient.shutdown
      Scan = IpcClient.scan
      ScanStatus = IpcClient.scanStatus
      GetStatus = IpcClient.getStatus
      GetPluginStatus = IpcClient.getPluginStatus
      RunCommand = IpcClient.runCommand }

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
    printfn "  <command> [args]   Run a plugin-registered command"

let private runIpc (action: Async<string>) : int =
    try
        let result = action |> Async.RunSynchronously
        printfn "%s" result
        0
    with ex ->
        eprintfn $"Could not connect to daemon: %s{ex.Message}"
        1

/// Execute a parsed command with injectable dependencies.
let executeCommand
    (createDaemon: string -> Daemon)
    (ipc: IpcOps)
    (repoRoot: string)
    (pipeName: string)
    (command: Command)
    : int =
    match command with
    | Help ->
        showHelp ()
        0
    | Start ->
        eprintfn $"Starting FsHotWatch daemon for %s{repoRoot}"
        eprintfn $"Pipe: %s{pipeName}"
        let daemon = createDaemon repoRoot
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

[<EntryPoint>]
let main args =
    let repoRoot =
        match findRepoRoot (Directory.GetCurrentDirectory()) with
        | Some root -> root
        | None ->
            eprintfn "Error: not in a jj or git repository"
            exit 1
            ""

    let pipeName = computePipeName repoRoot
    let command = parseCommand (args |> Array.toList)
    executeCommand Daemon.create defaultIpcOps repoRoot pipeName command
