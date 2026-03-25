module FsHotWatch.Cli.Program

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open FsHotWatch.Daemon
open FsHotWatch.Ipc

type Command =
    | Start
    | Stop
    | Scan
    | Status of pluginName: string option
    | PluginCommand of name: string * args: string
    | Help

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

let computePipeName (repoRoot: string) =
    let hash = SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot))
    let short = Convert.ToHexStringLower(hash).Substring(0, 12)
    $"fs-hot-watch-{short}"

let parseCommand (args: string list) : Command =
    match args with
    | []
    | [ "help" ]
    | [ "--help" ]
    | [ "-h" ] -> Help
    | [ "start" ] -> Start
    | [ "stop" ] -> Stop
    | [ "scan" ] -> Scan
    | [ "status" ] -> Status None
    | [ "status"; pluginName ] -> Status(Some pluginName)
    | cmd :: rest ->
        let argsStr = if rest.IsEmpty then "" else String.concat " " rest
        PluginCommand(cmd, argsStr)

let private showHelp () =
    printfn "FsHotWatch — F# file watcher daemon"
    printfn ""
    printfn "Usage: fs-hot-watch <command>"
    printfn ""
    printfn "Commands:"
    printfn "  start              Start daemon in foreground (auto-scans on boot)"
    printfn "  stop               Stop running daemon"
    printfn "  scan               Re-scan all registered files"
    printfn "  status [plugin]    Show plugin statuses"
    printfn "  <command> [args]   Run a plugin-registered command"

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

    match parseCommand (args |> Array.toList) with
    | Help ->
        showHelp ()
        0
    | Start ->
        eprintfn $"Starting FsHotWatch daemon for %s{repoRoot}"
        eprintfn $"Pipe: %s{pipeName}"
        let daemon = Daemon.create repoRoot
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
        try
            let result = IpcClient.shutdown pipeName |> Async.RunSynchronously
            printfn "%s" result
            0
        with ex ->
            eprintfn $"Could not connect to daemon: %s{ex.Message}"
            1
    | Scan ->
        try
            let result = IpcClient.scan pipeName |> Async.RunSynchronously
            printfn "%s" result
            0
        with ex ->
            eprintfn $"Could not connect to daemon: %s{ex.Message}"
            1
    | Status None ->
        try
            let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
            printfn "%s" result
            0
        with ex ->
            eprintfn $"Could not connect to daemon: %s{ex.Message}"
            1
    | Status(Some pluginName) ->
        try
            let result = IpcClient.getPluginStatus pipeName pluginName |> Async.RunSynchronously

            printfn "%s" result
            0
        with ex ->
            eprintfn $"Could not connect to daemon: %s{ex.Message}"
            1
    | PluginCommand(cmd, argsJson) ->
        try
            let result = IpcClient.runCommand pipeName cmd argsJson |> Async.RunSynchronously

            printfn "%s" result
            0
        with ex ->
            eprintfn $"Could not connect to daemon: %s{ex.Message}"
            1
