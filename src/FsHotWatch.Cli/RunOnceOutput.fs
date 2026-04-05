module FsHotWatch.Cli.RunOnceOutput

open System
open System.Text
open CommandTree
open FsHotWatch.Events
open FsHotWatch.ErrorLedger

/// Format a single plugin result line with ANSI colors and timing.
/// Example: "  ✓ Build                        3.2s"
/// Example: "  ✗ Lint                         6.4s"
let formatStepResult (name: string) (status: PluginStatus) : string =
    let paddedName = name.PadRight(24)

    match status with
    | Completed _ -> $"  %s{Color.green}\u2713%s{Color.reset} %s{paddedName}"
    | Failed(error, _) ->
        $"  %s{Color.red}\u2717%s{Color.reset} %s{paddedName} %s{Color.dim}\u2014 %s{error}%s{Color.reset}"
    | Running since ->
        let elapsed = DateTime.UtcNow - since
        let timingStr = UI.timing elapsed
        $"  %s{Color.yellow}\u2026%s{Color.reset} %s{paddedName} %s{timingStr}"
    | Idle -> $"  %s{Color.dim}\u2014 %s{paddedName}%s{Color.reset}"

/// Format the plugin summary section with colored status and timing.
let formatSummary (statuses: Map<string, PluginStatus>) : string =
    if statuses.IsEmpty then
        ""
    else
        let sb = StringBuilder()

        for KeyValue(name, status) in statuses do
            sb.AppendLine(formatStepResult name status) |> ignore

        sb.ToString().TrimEnd('\n', '\r')

/// Format the errors section with colored severity labels.
/// Groups errors by file with colored severity.
let formatErrors (errors: Map<string, (string * ErrorEntry) list>) : string =
    if errors.IsEmpty then
        $"%s{Color.green}No errors%s{Color.reset}"
    else
        let sb = StringBuilder()
        let mutable totalCount = 0
        let fileCount = errors.Count

        for KeyValue(file, entries) in errors do
            sb.AppendLine() |> ignore
            sb.AppendLine($"%s{Color.bold}%s{file}%s{Color.reset}") |> ignore
            totalCount <- totalCount + entries.Length

            for (pluginName, entry) in entries do
                let severityLabel =
                    match entry.Severity with
                    | Error -> $"%s{Color.red}error%s{Color.reset}: "
                    | Warning -> $"%s{Color.yellow}warning%s{Color.reset}: "
                    | Info -> ""
                    | Hint -> ""

                sb.AppendLine(
                    $"  %s{Color.dim}[%s{pluginName}]%s{Color.reset} L%d{entry.Line}: %s{severityLabel}%s{entry.Message}"
                )
                |> ignore

        sb.AppendLine() |> ignore
        sb.Append($"%d{totalCount} error(s) in %d{fileCount} file(s)") |> ignore
        sb.ToString().TrimEnd('\n', '\r')

/// Run a daemon's RunOnce with live progress display to stderr.
/// Uses spinners when interactive, plain output otherwise.
/// Returns the final statuses.
let runOnceWithProgress (daemon: FsHotWatch.Daemon.Daemon) : Map<string, PluginStatus> =
    if UI.isInteractive then
        UI.withSpinnerQuiet "Running checks" (fun () -> daemon.RunOnce() |> Async.RunSynchronously)
    else
        daemon.RunOnce() |> Async.RunSynchronously

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

    let summary = formatSummary statuses

    if summary <> "" then
        eprintfn "%s" summary

    eprintfn "%s" (formatErrors allErrors)
    if errorCount > 0 then 1 else 0
