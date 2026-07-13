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
                | true, v when v.ValueKind = JsonValueKind.String && v.GetString() = "busy" ->
                    // A test run is still in progress (the force-rerun waited and
                    // gave up). Distinct, non-failure signal — never "Tests failed".
                    let msg =
                        match root.TryGetProperty("message") with
                        | true, m when m.ValueKind = JsonValueKind.String -> m.GetString()
                        | _ -> "a test run is still in progress; retry once it finishes"

                    UI.warn msg
                    // exit 0: nothing failed; the caller should retry, not treat
                    // this as a red verdict.
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

                        // Run-level "matched nothing": every project was a
                        // zero-match-under-filter pass. Reported DISTINCTLY so a
                        // `test-rerun --filter-*` that selected no test never looks
                        // like a real green run that exercised code.
                        let noTestsMatched =
                            match root.TryGetProperty("noTestsMatched") with
                            | true, n -> n.ValueKind = JsonValueKind.True
                            | false, _ -> false

                        if hasFailed then
                            UI.fail "Tests failed"
                            1
                        elif noTestsMatched then
                            UI.skip "No tests matched the filter"
                            0
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

/// Render live plugin-status progress until `isSettled` reports the host has
/// reached its authoritative verdict. Pure of the scan trigger — the caller
/// decides whether/when to wait for a scan first.
///
/// SOUNDNESS: the loop termination is `isSettled`, NOT a status-map predicate
/// like `isAllTerminal`. `isAllTerminal` treats `Idle` as quiescent and never
/// consults the host's inflight/busy state, so it concludes "settled" while a
/// downstream plugin (test-prune) still has a `BuildCompleted` event queued in
/// its mailbox (status observably `Idle`, handler not yet run) or while it is
/// mid-run with a non-empty pending-verification queue. That produced false
/// greens: `check` exited 0 having computed N affected tests BEFORE the
/// test-prune run's verdict was captured. The authoritative settle is the
/// daemon's `WaitForComplete` RPC (`waitForVerdict` → `requireVerdict=true`,
/// which gates on `AnyPluginBusy` + generation advancement + quiescence), so
/// `isSettled` is wired to that RPC's completion. The status reads here are for
/// RENDERING ONLY and never decide the gate.
let private pollUntilSettled
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (getStatus: unit -> string)
    (isSettled: unit -> bool)
    : unit =
    let mutable prevLineCount = 0
    let mutable prevRendered = ""
    let mutable allDone = false

    while not allDone do
        let statusJson = getStatus ()
        let parsed = parsePluginStatuses statusJson

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

        // Authoritative termination: the daemon's sound verdict, NOT the
        // Idle-tolerant status map. See the soundness note above.
        allDone <- isSettled ()

        if not allDone then
            Thread.Sleep(200)

    if UI.isInteractive && prevLineCount > 0 then
        for _ in 1..prevLineCount do
            Console.Error.Write("\x1b[A\x1b[2K")

/// True when `ex` (walking its inner-exception chain) indicates the daemon shut
/// down or the IPC pipe dropped WHILE a `WaitForComplete` verdict wait was in
/// flight — as opposed to a genuine plugin verdict (which comes back as a normal
/// status payload, never a fault). Matches three shapes: StreamJsonRpc's
/// connection-loss exception (by type-name substring, so no compile-time
/// dependency on the transport assembly is needed here), a raw pipe teardown
/// (`IOException`/`ObjectDisposedException`/`EndOfStreamException`), and the
/// daemon's own graceful-shutdown sentinel message propagated back as a remote
/// invocation error. Used to turn a mid-wait teardown into a diagnostic exit-2
/// ("no verdict was produced") instead of an opaque crash — the waiting client
/// must never see a silent connection drop.
let rec isDaemonShutdownDuringWait (ex: exn) : bool =
    match ex with
    | null -> false
    | _ ->
        let typeName = ex.GetType().FullName
        let msg = ex.Message

        typeName.Contains("ConnectionLost", StringComparison.Ordinal)
        || (ex :? System.IO.EndOfStreamException)
        || (ex :? System.IO.IOException)
        || (ex :? System.ObjectDisposedException)
        || (not (isNull msg)
            && msg.Contains("daemon shutting down", StringComparison.Ordinal))
        || (not (isNull ex.InnerException) && isDaemonShutdownDuringWait ex.InnerException)

/// True when `ex` (walking its inner-exception chain) is the daemon's
/// verdict-wait deadline breach — the `TimeoutException` raised by
/// `waitForAllTerminalCore` once `WaitForComplete` overruns its hard bound
/// (`FSHW_VERDICT_DEADLINE_SEC`, default 60 min). Matched by the stable
/// "WaitForComplete timed out" message substring because the exception crosses
/// the RPC boundary as a transport-level remote-invocation error, not as a
/// typed `TimeoutException`. Distinct from `isDaemonShutdownDuringWait`: this
/// is the daemon SAYING a plugin is wedged, not the daemon going away.
let rec isVerdictWaitTimeout (ex: exn) : bool =
    match ex with
    | null -> false
    | _ ->
        (not (isNull ex.Message)
         && ex.Message.Contains("WaitForComplete timed out", StringComparison.Ordinal))
        || (not (isNull ex.InnerException) && isVerdictWaitTimeout ex.InnerException)

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
    (waitForComplete: unit -> string)
    (getStatus: unit -> string)
    (getErrors: unit -> string)
    (triggerScan: unit -> string)
    : int =
    // Run `fn` under a spinner when interactive, else announce it with a plain
    // console line first. Centralizes the interactive/non-interactive split so
    // the scan and re-scan steps don't each repeat the branch.
    let withProgress (spinnerLabel: string) (consoleLabel: string) (fn: unit -> unit) =
        if UI.isInteractive then
            UI.withSpinnerQuiet spinnerLabel fn
        else
            eprintfn "  %s" consoleLabel
            fn ()

    // Settle the host through its AUTHORITATIVE verdict (`WaitForComplete` →
    // `waitForVerdict`), rendering live status while it blocks. `WaitForComplete`
    // runs on a background task; the render loop terminates only when THAT task
    // finishes — never on the Idle-tolerant `isAllTerminal` status predicate
    // that let `check` exit green while test-prune still had a build event queued
    // or a run in flight. See `pollUntilSettled`.
    let settle () : unit =
        let completeTask =
            System.Threading.Tasks.Task.Run(fun () -> waitForComplete () |> ignore)

        pollUntilSettled renderStatuses getStatus (fun () -> completeTask.IsCompleted)
        // Surface a fault (daemon shutdown / IPC error) rather than swallowing it
        // behind a vacuous clean — re-raises the original RPC exception.
        completeTask.GetAwaiter().GetResult()

    // A mid-wait daemon teardown (an explicit `fshw stop`, or any crash while a
    // settle is in flight) surfaces the RPC as a transport fault, NOT a verdict.
    // Translate it into a loud diagnostic + exit 2 ("completeness unachievable")
    // so the waiting client always gets an actionable verdict rather than an
    // opaque connection-drop stack trace. See `isDaemonShutdownDuringWait`.
    try
        withProgress "Scanning" "Scanning..." (fun () -> waitForScan () |> ignore)

        settle ()

        // First read: diagnostics + coverage after the daemon has settled.
        let firstResp = parseDiagnosticsResponse (getErrors ())
        let firstOutput = formatDiagnosticsResponse mode renderStatuses firstResp
        eprintfn "%s" firstOutput

        // Force a fresh scan and re-settle (the convergence loop's "try to FIX,
        // not just report" step). Invoked only when the first read is
        // incomplete-but-clean.
        let rescan () : unit =
            withProgress "Re-scanning (incomplete)" "Re-scanning (incomplete check)..." (fun () ->
                triggerScan () |> ignore)

            settle ()

        // Re-read diagnostics + coverage and render. Called after each rescan.
        let reread () : bool * Coverage =
            let resp = parseDiagnosticsResponse (getErrors ())
            let output = formatDiagnosticsResponse mode renderStatuses resp
            eprintfn "%s" output
            (hasFailures noWarnFail resp, resp.Coverage)

        let outcome =
            CheckVerdict.converge
                MaxConvergeAttempts
                rescan
                reread
                (hasFailures noWarnFail firstResp, firstResp.Coverage)

        match outcome with
        | CheckVerdict.CheckOutcome.Incomplete n ->
            let detail =
                if n > 0 then
                    $"%d{n} file(s) could not be checked"
                else
                    "coverage could not be confirmed"

            UI.fail $"Check incomplete: {detail} after %d{MaxConvergeAttempts} re-scan attempt(s)"
        | CheckVerdict.CheckOutcome.Clean
        | CheckVerdict.CheckOutcome.FailuresFound -> ()

        CheckVerdict.exitCode outcome
    with
    | ex when isVerdictWaitTimeout ex ->
        // The daemon's hard verdict deadline fired: a plugin overran the bound
        // and is most likely wedged. The remote message names the plugin and
        // its elapsed time (e.g. "still running: test-prune (1h 0m)"). Surface
        // it verbatim plus the recovery path — bounded and legible, never the
        // old heartbeat-forever silence.
        UI.fail
            $"Check aborted: %s{ex.Message}\nA plugin overran the verdict deadline and is likely wedged — inspect logs/daemon.log, then `fshw stop` to reclaim the daemon. If the suite legitimately needs longer, raise FSHW_VERDICT_DEADLINE_SEC."

        2
    | ex when isDaemonShutdownDuringWait ex ->
        UI.fail
            "Check aborted: the daemon shut down before producing a verdict — nothing was verified. Re-run `fshw check` (the next command auto-restarts the daemon)."

        2
