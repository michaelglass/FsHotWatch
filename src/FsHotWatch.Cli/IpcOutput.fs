module FsHotWatch.Cli.IpcOutput

open System
open System.Text.Json
open System.Threading
open CommandTree
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.IpcParsing

/// Format one diagnostic entry as a plain agent-mode line:
///   `<plugin>:<file>:<line>:<col>: <severity> <message>`
/// No ANSI, no indentation. Message is single-line (collapses newlines).
let private agentDiagnosticLine (file: string) (d: DiagnosticEntry) : string =
    let msg = d.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()
    $"%s{d.Plugin}:%s{file}:%d{d.Line}:%d{d.Column}: %s{DiagnosticSeverity.toString d.Severity} %s{msg}"

/// Format the full errors response.
///
/// In Verbose/Compact modes: per-plugin progress block followed by the colored
/// by-file error block (via `RunOnceOutput.formatErrors`).
///
/// In Agent mode: banner + per-plugin lines from `renderStatuses` (which ends
/// with `next: ...`) are split so plain diagnostic lines slot in *before* the
/// trailing `next:` hint. Agents can read the output line by line without
/// stripping ANSI.
let formatDiagnosticsResponse
    (mode: ProgressRenderer.RenderMode)
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (resp: DiagnosticsResponse)
    : string =
    match mode with
    | ProgressRenderer.Agent ->
        // renderStatuses for Agent produces [banner; plugin lines...; next: ...].
        // Insert diag lines between plugin lines and the next: footer.
        let lines = renderStatuses resp.Statuses

        let header, footer =
            match List.rev lines with
            | last :: rest when last.StartsWith("next:") -> List.rev rest, [ last ]
            | _ -> lines, []

        let diagLines =
            [ for KeyValue(file, entries) in resp.Files do
                  for d in entries do
                      agentDiagnosticLine file d ]

        header @ diagLines @ footer |> String.concat "\n"
    | ProgressRenderer.Compact
    | ProgressRenderer.Verbose ->
        let sb = System.Text.StringBuilder()

        let summary = renderStatuses resp.Statuses |> String.concat "\n"

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

/// True if a DiagnosticsResponse contains failures: any plugin Failed, or any
/// error/warning-severity diagnostic (warnings respecting noWarnFail). This is
/// the single source of truth for "did check find real problems"; both
/// `exitCodeFromResponse` and the converge-then-verdict path reuse it so the two
/// can't drift.
let hasFailures (noWarnFail: bool) (resp: DiagnosticsResponse) : bool =
    let anyPluginFailed =
        resp.Statuses
        |> Map.exists (fun _ parsed ->
            match parsed.Status with
            | Failed _ -> true
            | _ -> false)

    let isFailure (e: DiagnosticEntry) =
        match e.Severity with
        | Error -> true
        | Warning -> not noWarnFail
        | Info
        | Hint -> false

    let failCount =
        resp.Files |> Map.toSeq |> Seq.collect snd |> Seq.filter isFailure |> Seq.length

    anyPluginFailed || failCount > 0

/// Determine exit code from a DiagnosticsResponse.
/// Returns non-zero if any plugin is Failed, or if the ledger has failing entries.
/// When noWarnFail is true, only errors (not warnings) in the ledger trigger a non-zero exit code.
let exitCodeFromResponse (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    if hasFailures noWarnFail resp then 1 else 0

/// Render a generic IPC result (status JSON or plain text).
let renderIpcResult
    (mode: ProgressRenderer.RenderMode)
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (noWarnFail: bool)
    (result: string)
    : int =
    // F8 (audit 2026-05-02): renderIpcResult tolerates non-JSON output (the
    // daemon emits plain text for some commands; the None branch falls through
    // to eprintfn). Narrow to :? JsonException so a real programming bug
    // (e.g. null arg) propagates instead of silently rendering raw text.
    let doc =
        try
            Some(JsonDocument.Parse(result))
        with :? JsonException ->
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
            let output = formatDiagnosticsResponse mode renderStatuses resp
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

/// Maximum convergence attempts for an incomplete-but-clean check. Each attempt
/// forces a re-scan and re-reads coverage; the loop stops early on failures,
/// completion, or no-progress. 3 is enough to clear a transient cancellation
/// race while staying bounded for a genuinely un-completable check.
[<Literal>]
let MaxConvergeAttempts = 3

/// Poll plugin statuses until all terminal, rendering live progress. Pure of the
/// scan trigger — the caller decides whether/when to wait for a scan first.
let private pollUntilTerminal
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (getStatus: unit -> string)
    : unit =
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

/// Poll daemon status, render live progress, then decide a converge-then-verdict
/// outcome and return its exit code (0 = complete & clean, 1 = failures found,
/// 2 = completeness unachievable). `renderStatuses` is injected so callers choose
/// the progress renderer (compact/verbose). `triggerScan` forces a fresh scan
/// and is invoked only on the convergence path (incomplete coverage, no failures).
let pollAndRender
    (mode: ProgressRenderer.RenderMode)
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (noWarnFail: bool)
    (waitForScan: unit -> string)
    (getStatus: unit -> string)
    (getErrors: unit -> string)
    (triggerScan: unit -> string)
    : int =
    if UI.isInteractive then
        UI.withSpinnerQuiet "Scanning" (fun () -> waitForScan () |> ignore)
    else
        eprintfn "  Scanning..."
        waitForScan () |> ignore

    pollUntilTerminal renderStatuses getStatus

    // First read: diagnostics + coverage after the daemon has settled.
    let firstResp = parseDiagnosticsResponse (getErrors ())
    let firstOutput = formatDiagnosticsResponse mode renderStatuses firstResp
    eprintfn "%s" firstOutput

    // Force a fresh scan and re-settle (the convergence loop's "try to FIX, not
    // just report" step). Invoked only when the first read is incomplete-but-clean.
    let rescan () : unit =
        if UI.isInteractive then
            UI.withSpinnerQuiet "Re-scanning (incomplete)" (fun () -> triggerScan () |> ignore)
        else
            eprintfn "  Re-scanning (incomplete check)..."
            triggerScan () |> ignore

        pollUntilTerminal renderStatuses getStatus

    // Re-read diagnostics + coverage and render. Called after each rescan.
    let reread () : bool * Coverage =
        let resp = parseDiagnosticsResponse (getErrors ())
        let output = formatDiagnosticsResponse mode renderStatuses resp
        eprintfn "%s" output
        (hasFailures noWarnFail resp, resp.Coverage)

    let outcome =
        CheckVerdict.converge MaxConvergeAttempts rescan reread (hasFailures noWarnFail firstResp, firstResp.Coverage)

    match outcome with
    | CheckVerdict.CheckOutcome.Incomplete n when n > 0 ->
        UI.fail $"Check incomplete: %d{n} file(s) could not be checked after %d{MaxConvergeAttempts} re-scan attempt(s)"
    | CheckVerdict.CheckOutcome.Incomplete _ ->
        UI.fail $"Check incomplete: coverage could not be confirmed after %d{MaxConvergeAttempts} re-scan attempt(s)"
    | CheckVerdict.CheckOutcome.Clean
    | CheckVerdict.CheckOutcome.FailuresFound -> ()

    CheckVerdict.exitCode outcome
