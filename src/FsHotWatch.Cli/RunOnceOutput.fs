module FsHotWatch.Cli.RunOnceOutput

open System
open System.Text
open CommandTree
open FsHotWatch.Events
open FsHotWatch.ErrorLedger

type ParsedPluginStatus =
    { Status: PluginStatus
      Subtasks: Subtask list
      ActivityTail: string list
      LastRun: RunRecord option
      Diagnostics: DiagnosticCounts }

/// Format the errors section with colored severity labels.
/// Groups errors by file with colored severity.
let formatErrors (errors: Map<string, (string * ErrorEntry) list>) : string =
    let actionable =
        errors
        |> Map.map (fun _ entries ->
            entries
            |> List.filter (fun (_, e) ->
                match e.Severity with
                | Error
                | Warning -> true
                | Info
                | Hint -> false))
        |> Map.filter (fun _ entries -> not entries.IsEmpty)

    if actionable.IsEmpty then
        $"%s{Color.green}No errors%s{Color.reset}"
    else
        let sb = StringBuilder()
        let mutable errorCount = 0
        let mutable warnCount = 0

        for KeyValue(file, entries) in actionable do
            sb.AppendLine() |> ignore
            sb.AppendLine($"%s{Color.bold}%s{file}%s{Color.reset}") |> ignore

            for (pluginName, entry) in entries do
                match entry.Severity with
                | Error -> errorCount <- errorCount + 1
                | Warning -> warnCount <- warnCount + 1
                | _ -> ()

                let severityLabel =
                    match entry.Severity with
                    | Error -> $"%s{Color.red}error%s{Color.reset}: "
                    | Warning -> $"%s{Color.yellow}warning%s{Color.reset}: "
                    | Info
                    | Hint -> ""

                sb.AppendLine(
                    $"  %s{Color.dim}[%s{pluginName}]%s{Color.reset} L%d{entry.Line}: %s{severityLabel}%s{entry.Message}"
                )
                |> ignore

        sb.AppendLine() |> ignore
        let fileCount = actionable.Count

        let summary =
            match
                DiagnosticCounts.summary
                    { Errors = errorCount
                      Warnings = warnCount }
            with
            | "" -> "No errors"
            | s -> s

        sb.Append($"%s{summary} in %d{fileCount} file(s)") |> ignore
        sb.ToString().TrimEnd('\n', '\r')

/// Run a daemon's RunOnce with live progress display to stderr.
let runOnceWithProgress (daemon: FsHotWatch.Daemon.Daemon) : Map<string, PluginStatus> =
    if UI.isInteractive then
        UI.withSpinnerQuiet "Running checks" (fun () -> daemon.RunOnce() |> Async.RunSynchronously)
    else
        daemon.RunOnce() |> Async.RunSynchronously

/// Build a parsed-status map from the daemon's host, for use by the progress renderer.
let snapshotHost (host: FsHotWatch.PluginHost.PluginHost) (statuses: Map<string, PluginStatus>) =
    let counts = host.GetDiagnosticCountsByPlugin()

    statuses
    |> Map.map (fun name status ->
        let snap = host.GetActivitySnapshot(name)

        { Status = status
          Subtasks = snap.Subtasks
          ActivityTail = snap.ActivityTail
          LastRun = snap.LastRun
          Diagnostics = Map.tryFind name counts |> Option.defaultValue DiagnosticCounts.empty })

/// Run a daemon in run-once mode and report results.
let runOnceAndReport
    (renderSummary: Map<string, ParsedPluginStatus> -> string)
    (noWarnFail: bool)
    (createDaemon: string -> FsHotWatch.Daemon.Daemon)
    (repoRoot: string)
    (config: DaemonConfig.DaemonConfiguration)
    (pluginName: string option)
    : int =
    let daemon = createDaemon repoRoot
    DaemonConfig.registerPlugins daemon repoRoot config
    let statuses = runOnceWithProgress daemon

    let allErrors =
        match pluginName with
        | Some name ->
            daemon.Host.GetErrorsByPlugin(name)
            |> Map.map (fun _ entries -> entries |> List.map (fun e -> name, e))
        | None -> daemon.Host.GetErrors()

    let failCount =
        allErrors
        |> Map.toList
        |> List.collect snd
        |> List.filter (fun (_, e) -> ErrorEntry.isFailing (not noWarnFail) e)
        |> List.length

    let parsed = snapshotHost daemon.Host statuses
    let summary = renderSummary parsed

    if summary <> "" then
        eprintfn "%s" summary

    eprintfn "%s" (formatErrors allErrors)
    if failCount > 0 then 1 else 0
