module FsHotWatch.Cli.IpcOutput

open System
open System.Text.Json
open System.Threading
open CommandTree
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
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
            | StatusView.Failed _ -> true
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
                    // The force-rerun did not produce a result within its
                    // budget (a prior run holds the slot, or the queued run
                    // didn't finish in time). Distinct from "Tests failed" —
                    // nothing is known to be broken — but it must NEVER exit 0:
                    // `test-rerun` is the explicit "prove it ran" verb, and an
                    // exit 0 without a run is a vacuous green (AUTOMATION-99).
                    let msg =
                        match root.TryGetProperty("message") with
                        | true, m when m.ValueKind = JsonValueKind.String -> m.GetString()
                        | _ -> "the test run did not produce a result in time; retry (or raise --wait-sec)"

                    UI.fail msg
                    // Non-zero: no run was proven. The caller retries; it must
                    // not read this as a green verdict.
                    1
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
                                | StatusView.Failed _ -> true
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

/// Publish the run's verdict as `.fshw/verdict.json` and — when a MACHINE is
/// reading (stdout not a TTY) — print the steering block that names it.
///
/// The verdict written here and the exit code returned to the shell are two
/// renderings of ONE `CheckOutcome`. There is no second computation, so there is
/// no way for the file to say green while the process says red.
///
/// TREE HASH: taken TWICE — once after the daemon has settled (that is the tree
/// it just finished verifying) and once again immediately before the write. If
/// the two differ, the working tree moved underneath the verdict while it was
/// being produced, and the honest answer is `incomplete`, not a green over a tree
/// nobody checked. The failure direction is the safe one: it is a refusal, never
/// a claim.
///
/// Best-effort by design: a repo whose `.fshw/` cannot be written must still get
/// its exit code. The verdict is an additional surface, never a new way to fail.
let internal publishVerdict
    (repoRoot: string)
    (excludePatterns: string list)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (runReport: TestRunReport)
    (statuses: Map<string, ParsedPluginStatus>)
    (outcome: CheckVerdict.CheckOutcome)
    : unit =
    try
        let settled = FsHotWatch.TreeHash.compute repoRoot excludePatterns
        let suites = Verdict.suiteVerdicts repoRoot runReport.RunId
        let plugins = Verdict.pluginVerdicts (not noWarnFail) (DateTime.UtcNow) statuses
        let atWrite = FsHotWatch.TreeHash.compute repoRoot excludePatterns

        let verdictOutcome, exitCode =
            if settled.Hash <> atWrite.Hash then
                Verdict.Incomplete
                    "the working tree changed while the verdict was being produced — nothing is claimed about it",
                CheckVerdict.exitCode (CheckVerdict.CheckOutcome.Incomplete -1)
            else
                Verdict.outcomeOfCheck outcome, CheckVerdict.exitCode outcome

        let v: Verdict.Verdict =
            { ProducedAt = DateTime.UtcNow
              Command = Verdict.Command.ofCheckMode checkMode
              // WHO made this claim. `treeHash` says what it is about; without this a
              // stale binary's green over an unchanged tree reads as current.
              Producer = Verdict.Producer.current ()
              RunId = runReport.RunId
              TreeHash = atWrite.Hash
              TreeHashAlgorithm = FsHotWatch.TreeHash.Algorithm
              TreeFileCount = atWrite.FileCount
              Scope = runReport.Scope
              Outcome = verdictOutcome
              ExitCode = exitCode
              Plugins = plugins
              Suites = suites }

        Verdict.write repoRoot v

        if not UI.isInteractive then
            eprintfn ""

            for line in ProgressRenderer.AgentHints.forVerdict v do
                eprintfn "%s" line
    with
    | :? System.IO.IOException as ex ->
        FsHotWatch.Logging.warn "verdict" $"could not publish %s{Verdict.RelativePath}: %s{ex.Message}"
    | :? System.UnauthorizedAccessException as ex ->
        FsHotWatch.Logging.warn "verdict" $"could not publish %s{Verdict.RelativePath}: %s{ex.Message}"

/// Poll daemon status, render live progress, then decide a converge-then-verdict
/// outcome and return its exit code (0 = complete & clean, 1 = failures found,
/// 2 = completeness unachievable, 3 = merge gate with an unearned scope).
/// `renderStatuses` is injected so callers choose the progress renderer
/// (compact/verbose). `triggerScan` forces a fresh scan and is invoked only on
/// the convergence path (incomplete coverage, no failures).
///
/// Every terminal path — clean, red, incomplete, wedged plugin, daemon teardown —
/// publishes a verdict file. The moments a consumer is MOST tempted to start
/// grepping are the failures, so those are exactly the moments the machine-readable
/// answer must exist and be pointed at.
let pollAndRender
    (mode: ProgressRenderer.RenderMode)
    (checkMode: CheckVerdict.CheckMode)
    (repoRoot: string)
    (excludePatterns: string list)
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (noWarnFail: bool)
    (waitForScan: unit -> string)
    (waitForComplete: unit -> string)
    (getStatus: unit -> string)
    (getErrors: unit -> string)
    (getTestRun: unit -> TestRunReport)
    // Run EVERY configured test project, now, and don't come back until it is done
    // (`run-tests` with no filter). This is the gate's teeth: `set-scope full` only
    // makes the next run unfiltered, and on a warm daemon whose impact DB says nothing
    // changed there IS no next run — so the gate would refuse for want of evidence it
    // was never willing to go and get. Invoked ONLY in `MergeGate`, and only when the
    // settled scope is not already full-suite, so the common case (a cold daemon whose
    // scan already provoked the unfiltered run) pays for exactly one suite.
    (forceFullRun: unit -> unit)
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
    // The LAST state the verdict was computed from. Captured at every read (the
    // first one and each convergence re-read) so the file records what the final
    // verdict was actually based on — never an earlier snapshot, and never a
    // second query that could see a different daemon.
    let finalStatuses = ref Map.empty

    let finalRun = ref { Scope = ScopeUnknown; RunId = None }

    try
        withProgress "Scanning" "Scanning..." (fun () -> waitForScan () |> ignore)

        settle ()

        // First read: diagnostics + coverage after the daemon has settled.
        let firstResp = parseDiagnosticsResponse (getErrors ())
        let firstOutput = formatDiagnosticsResponse mode renderStatuses firstResp
        eprintfn "%s" firstOutput
        finalStatuses.Value <- firstResp.Statuses

        // Force a fresh scan and re-settle (the convergence loop's "try to FIX,
        // not just report" step). Invoked only when the first read is
        // incomplete-but-clean.
        let rescan () : unit =
            withProgress "Re-scanning (incomplete)" "Re-scanning (incomplete check)..." (fun () ->
                triggerScan () |> ignore)

            settle ()

        // Re-read diagnostics + coverage + test scope and render. Called after each
        // rescan. The scope is read from the daemon EVERY time alongside the
        // diagnostics — never carried over from an earlier read — so the verdict is
        // always computed against what the latest run actually covered.
        let reread () : bool * Coverage * TestScope =
            let resp = parseDiagnosticsResponse (getErrors ())
            let output = formatDiagnosticsResponse mode renderStatuses resp
            eprintfn "%s" output
            let run = getTestRun ()
            finalStatuses.Value <- resp.Statuses
            finalRun.Value <- run
            (hasFailures noWarnFail resp, resp.Coverage, run.Scope)

        let firstRun = getTestRun ()
        finalRun.Value <- firstRun

        // THE GATE EARNS ITS EVIDENCE (AUTOMATION-117).
        //
        // `set-scope full` was already sent (before the scan), so any run the scan
        // provoked is unfiltered — and on a cold daemon that is the whole story: the
        // scope reads `FullSuite` here and nothing more is run. But a WARM daemon whose
        // impact DB says nothing changed provokes NO run at all, and the scope we just
        // read is the last (filtered) run's. Refusing there is correct — there is no
        // whole-suite evidence — but refusing and STOPPING makes the gate unsatisfiable,
        // and an unsatisfiable gate is one people route around with a shell script.
        //
        // So: no full-suite evidence ⇒ go and produce some. Then re-settle and re-read,
        // because a forced run can fail, and its failures are the answer.
        let initialRead =
            if CheckVerdict.gateNeedsFullRun checkMode firstRun.Scope then
                eprintfn
                    "  Merge gate: the tests that ran were %s — running the FULL suite to earn a verdict..."
                    (TestScope.describe firstRun.Scope)

                withProgress "Running the full suite (merge gate)" "Running the full suite (merge gate)..." (fun () ->
                    forceFullRun ())

                settle ()
                // `reread` refreshes finalStatuses/finalRun too, so the verdict is
                // computed — and PUBLISHED — from what the forced run actually did.
                reread ()
            else
                (hasFailures noWarnFail firstResp, firstResp.Coverage, firstRun.Scope)

        let outcome =
            CheckVerdict.converge checkMode MaxConvergeAttempts rescan reread initialRead

        publishVerdict repoRoot excludePatterns checkMode noWarnFail finalRun.Value finalStatuses.Value outcome

        match outcome with
        | CheckVerdict.CheckOutcome.Incomplete n ->
            let detail =
                if n > 0 then
                    $"%d{n} file(s) could not be checked"
                else
                    "coverage could not be confirmed"

            UI.fail $"Check incomplete: {detail} after %d{MaxConvergeAttempts} re-scan attempt(s)"
        | CheckVerdict.CheckOutcome.UnearnedScope scope ->
            // Nothing failed — and that is precisely the point. The gate was asked for
            // a claim about the whole suite and the tests that ran do not support one,
            // so it has no verdict to give. Say so; never launder it into a green.
            UI.fail
                $"Merge gate: NO VERDICT — the tests that ran were %s{TestScope.describe scope}, \
                   not the full suite.\nAn impact-filtered green means \"your change didn't break anything I chose to \
                   look at\", which is not the claim a merge needs. Nothing is reported broken, but nothing is \
                   reported sound either."
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
        publishVerdict
            repoRoot
            excludePatterns
            checkMode
            noWarnFail
            finalRun.Value
            finalStatuses.Value
            (CheckVerdict.CheckOutcome.Incomplete -1)

        UI.fail
            $"Check aborted: %s{ex.Message}\nA plugin overran the verdict deadline and is likely wedged — inspect logs/daemon.log, then `fshw stop` to reclaim the daemon. If the suite legitimately needs longer, raise FSHW_VERDICT_DEADLINE_SEC."

        2
    | ex when isDaemonShutdownDuringWait ex ->
        publishVerdict
            repoRoot
            excludePatterns
            checkMode
            noWarnFail
            finalRun.Value
            finalStatuses.Value
            (CheckVerdict.CheckOutcome.Incomplete -1)

        UI.fail
            "Check aborted: the daemon shut down before producing a verdict — nothing was verified. Re-run `fshw check` (the next command auto-restarts the daemon)."

        2
