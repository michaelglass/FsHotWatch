module FsHotWatch.Cli.RunOnceOutput

open System
open System.Text
open FsHotWatch.Events
open FsHotWatch.ErrorLedger

/// Format a single-line progress status for all plugins.
let formatProgressLine (statuses: Map<string, PluginStatus>) : string =
    statuses
    |> Map.toList
    |> List.map (fun (name, status) ->
        match status with
        | Completed _ -> $"%s{name} \u2713"
        | Failed _ -> $"%s{name} \u2717"
        | Running since ->
            let elapsed = DateTime.UtcNow - since
            $"%s{name} %d{int elapsed.TotalSeconds}s"
        | Idle -> $"%s{name} ...")
    |> String.concat "  "

/// Format the plugin summary section.
/// Takes a map of plugin name -> status, returns lines for stderr.
let formatSummary (statuses: Map<string, PluginStatus>) : string =
    if statuses.IsEmpty then
        ""
    else
        let maxNameLen =
            statuses |> Map.toSeq |> Seq.map (fun (name, _) -> name.Length) |> Seq.max

        let sb = StringBuilder()

        for KeyValue(name, status) in statuses do
            let paddedName = name.PadRight(maxNameLen)

            match status with
            | Completed _ -> sb.AppendLine($"  \u2713 %s{paddedName}") |> ignore
            | Failed(error, _) -> sb.AppendLine($"  \u2717 %s{paddedName} \u2014 %s{error}") |> ignore
            | Running _ -> sb.AppendLine($"  \u2026 %s{paddedName}") |> ignore
            | Idle -> sb.AppendLine($"  \u2014 %s{paddedName}") |> ignore

        sb.ToString().TrimEnd('\n', '\r')

/// Format the errors section.
/// Takes errors grouped by file (from GetErrors), returns lines for stderr.
let formatErrors (errors: Map<string, (string * ErrorEntry) list>) : string =
    if errors.IsEmpty then
        "No errors"
    else
        let sb = StringBuilder()
        let mutable totalCount = 0
        let fileCount = errors.Count

        for KeyValue(file, entries) in errors do
            sb.AppendLine() |> ignore
            sb.AppendLine(file) |> ignore
            totalCount <- totalCount + entries.Length

            for (pluginName, entry) in entries do
                let severityLabel =
                    match entry.Severity with
                    | Error -> "error: "
                    | Warning -> "warning: "
                    | Info -> ""
                    | Hint -> ""

                sb.AppendLine($"  [%s{pluginName}] L%d{entry.Line}: %s{severityLabel}%s{entry.Message}")
                |> ignore

        sb.AppendLine() |> ignore
        sb.Append($"%d{totalCount} error(s) in %d{fileCount} file(s)") |> ignore
        sb.ToString().TrimEnd('\n', '\r')

/// Format the complete run-once output for stderr.
let formatRunOnceOutput
    (statuses: Map<string, PluginStatus>)
    (errors: Map<string, (string * ErrorEntry) list>)
    : string =
    let summary = formatSummary statuses
    let errorsOutput = formatErrors errors

    if summary = "" then
        errorsOutput
    else
        summary + "\n" + errorsOutput

/// Run a daemon's RunOnce with live progress display to stderr.
/// Returns the final statuses.
let runOnceWithProgress (daemon: FsHotWatch.Daemon.Daemon) : Map<string, PluginStatus> =
    let mutable currentStatuses = daemon.Host.GetAllStatuses()
    let progressLock = obj ()

    let sub =
        daemon.Host.OnStatusChanged.Subscribe(fun (name, status) ->
            lock progressLock (fun () ->
                currentStatuses <- Map.add name status currentStatuses
                let line = formatProgressLine currentStatuses
                eprintf "\r\033[2K%s" line))

    let statuses = daemon.RunOnce() |> Async.RunSynchronously
    sub.Dispose()

    // Clear the progress line and move to next line
    eprintf "\r\033[2K"
    statuses

/// Run a daemon in run-once mode and report results.
/// pluginName = None queries all errors; Some name queries one plugin.
/// Returns exit code (0 = clean, 1 = errors).
let runOnceAndReport
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

    let errorCount =
        allErrors |> Map.toList |> List.sumBy (fun (_, entries) -> entries.Length)

    eprintfn "%s" (formatRunOnceOutput statuses allErrors)

    let json =
        System.Text.Json.JsonSerializer.Serialize(
            {| count = errorCount
               statuses = statuses |> Map.map (fun _ s -> sprintf "%A" s) |}
        )

    printfn "%s" json
    if errorCount > 0 then 1 else 0
