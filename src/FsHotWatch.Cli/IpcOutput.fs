module FsHotWatch.Cli.IpcOutput

open System
open System.Text.Json
open System.Threading
open CommandTree
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.IpcParsing

// Re-export types and pure parsers for existing callers that open IpcOutput.
type DiagnosticEntry = IpcParsing.DiagnosticEntry
type DiagnosticsResponse = IpcParsing.DiagnosticsResponse
type ParsedPluginStatus = IpcParsing.ParsedPluginStatus

let parseStatus = IpcParsing.parseStatus
let parseStatusMap = IpcParsing.parseStatusMap
let parsePluginStatusElement = IpcParsing.parsePluginStatusElement
let parsePluginStatuses = IpcParsing.parsePluginStatuses
let parseDiagnosticsResponse = IpcParsing.parseDiagnosticsResponse
let statusOnly = IpcParsing.statusOnly
let isAllTerminal = IpcParsing.isAllTerminal

/// Render all plugin statuses as a multi-line progress display.
let renderProgress (statuses: Map<string, PluginStatus>) : string =
    statuses
    |> Map.toList
    |> List.map (fun (name, status) -> RunOnceOutput.formatStepResult name status)
    |> String.concat "\n"

/// Format the full errors response with colored status lines and error details.
let formatDiagnosticsResponse (resp: DiagnosticsResponse) : string =
    let sb = System.Text.StringBuilder()

    let parsedStatuses = statusOnly resp.Statuses
    let summary = RunOnceOutput.formatSummary parsedStatuses

    if summary <> "" then
        sb.AppendLine(summary) |> ignore
        sb.AppendLine() |> ignore

    let errorMap =
        resp.Files
        |> Map.map (fun _ entries ->
            entries
            |> List.map (fun d ->
                d.Plugin,
                { Message = d.Message
                  Severity = d.Severity
                  Line = d.Line
                  Column = d.Column
                  Detail = d.Detail }))

    sb.Append(RunOnceOutput.formatErrors errorMap) |> ignore
    sb.ToString().TrimEnd('\n', '\r')

/// Determine exit code from a DiagnosticsResponse.
/// When noWarnFail is true, only errors (not warnings) cause a non-zero exit code.
let exitCodeFromResponse (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    let isFailure (e: DiagnosticEntry) =
        match e.Severity with
        | Error -> true
        | Warning -> not noWarnFail
        | Info
        | Hint -> false

    let failCount =
        resp.Files |> Map.toSeq |> Seq.collect snd |> Seq.filter isFailure |> Seq.length

    if failCount > 0 then 1 else 0

/// Render a generic IPC result (status JSON or plain text).
let renderIpcResult
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (noWarnFail: bool)
    (result: string)
    : int =
    let doc =
        try
            Some(JsonDocument.Parse(result))
        with _ ->
            None

    match doc with
    | None ->
        eprintfn "%s" result
        0
    | Some doc ->
        use doc = doc
        let root = doc.RootElement

        match root.TryGetProperty("count") with
        | true, _ ->
            let resp = parseDiagnosticsResponse result
            let output = formatDiagnosticsResponse resp
            eprintfn "%s" output
            exitCodeFromResponse noWarnFail resp
        | false, _ ->

            match root.TryGetProperty("error") with
            | true, e ->
                UI.fail (e.GetString())
                1
            | false, _ ->

                match root.TryGetProperty("status") with
                | true, v when v.ValueKind = JsonValueKind.String && v.GetString() = "failed" ->
                    UI.fail "Failed"
                    1
                | true, v when v.ValueKind = JsonValueKind.String && v.GetString() = "passed" ->
                    UI.success "Passed"
                    0
                | _ ->

                    match root.TryGetProperty("projects") with
                    | true, projects when projects.ValueKind = JsonValueKind.Array ->
                        let hasFailed =
                            projects.EnumerateArray()
                            |> Seq.exists (fun p ->
                                match p.TryGetProperty("status") with
                                | true, s -> s.GetString() = "failed"
                                | false, _ -> false)

                        if hasFailed then
                            UI.fail "Tests failed"
                            1
                        else
                            UI.success "Tests passed"
                            0
                    | _ ->

                        let parsed = parsePluginStatuses result
                        let plain = statusOnly parsed
                        let lines = renderStatuses parsed
                        let output = String.concat "\n" lines
                        eprintfn "%s" output

                        let hasFailed =
                            plain
                            |> Map.exists (fun _ s ->
                                match s with
                                | Failed _ -> true
                                | _ -> false)

                        if hasFailed then 1 else 0

/// Poll daemon status, render live progress, then format final errors.
/// Returns exit code (0 = no errors, 1 = errors).
/// `renderStatuses` is injected so callers choose the progress renderer (compact/verbose).
let pollAndRender
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (noWarnFail: bool)
    (waitForScan: unit -> string)
    (getStatus: unit -> string)
    (getErrors: unit -> string)
    : int =
    if UI.isInteractive then
        UI.withSpinnerQuiet "Scanning" (fun () -> waitForScan () |> ignore)
    else
        eprintfn "  Scanning..."
        waitForScan () |> ignore

    let mutable prevLineCount = 0
    let mutable prevRendered = ""
    let mutable allDone = false

    while not allDone do
        let statusJson = getStatus ()
        let parsed = parsePluginStatuses statusJson
        let plain = statusOnly parsed

        if UI.isInteractive then
            let lines = renderStatuses parsed
            let progress = String.concat "\n" lines

            if progress <> prevRendered then
                if prevLineCount > 0 then
                    for _ in 1..prevLineCount do
                        Console.Error.Write("\x1b[A\x1b[2K")

                eprintfn "%s" progress
                prevLineCount <- List.length lines
                prevRendered <- progress

        allDone <- isAllTerminal plain

        if not allDone then
            Thread.Sleep(200)

    if UI.isInteractive && prevLineCount > 0 then
        for _ in 1..prevLineCount do
            Console.Error.Write("\x1b[A\x1b[2K")

    let errorsJson = getErrors ()
    let resp = parseDiagnosticsResponse errorsJson
    let output = formatDiagnosticsResponse resp
    eprintfn "%s" output
    exitCodeFromResponse noWarnFail resp
