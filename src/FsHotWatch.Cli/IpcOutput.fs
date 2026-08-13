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

/// The failing entries in a DiagnosticsResponse's ledger (warnings respecting
/// `noWarnFail`). HALF of "did check find real problems" — the other half is
/// `CheckInputs.anyPluginFailed`, and a caller that folds in only this one is the bug
/// this release exists to eliminate. Never used alone; see `checkInputs`.
let private failingDiagnosticCount (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    let isFailure (e: DiagnosticEntry) =
        match e.Severity with
        | Error -> true
        | Warning -> not noWarnFail
        | Info
        | Hint
        // `Deferred` ("waiting on build") is never a failure — it routes the
        // verdict to `Incomplete`/exit 2 via `WaitingOnBuild`, not to a red.
        | Deferred -> false

    resp.Files |> Map.toSeq |> Seq.collect snd |> Seq.filter isFailure |> Seq.length

/// Any "waiting on build" deferral in the ledger? A `Deferred`-severity entry
/// means a test project's build artifact wasn't ready, so its tests DID NOT run:
/// non-green (nothing verified) but not a failure. The verdict routes this to
/// `Incomplete`/exit 2. Reading it off the parsed ledger keeps it fail-closed —
/// a severity that doesn't round-trip defaults to `Error` (counted as failing),
/// so the worst case of a wire bug is the OLD exit 1, never a false green.
let private waitingOnBuild (resp: DiagnosticsResponse) : bool =
    resp.Files
    |> Map.toSeq
    |> Seq.collect snd
    |> Seq.exists (fun (e: DiagnosticEntry) -> e.Severity = Deferred)

/// The daemon transport's observations, as `CheckVerdict.verdict` consumes them.
///
/// This is the daemon's HALF of the "one verdict, two transports" contract; the
/// in-process half is `RunOnceCheck.checkInputs`. Neither computes a verdict; both hand
/// over the same record, and `CheckVerdict` decides. Adding a term to `CheckInputs`
/// breaks BOTH here and there — which is the point of it being a record.
let internal checkInputs (noWarnFail: bool) (scope: TestScope) (resp: DiagnosticsResponse) : CheckVerdict.CheckInputs =
    { PluginStatuses = resp.Statuses
      FailingDiagnostics = failingDiagnosticCount noWarnFail resp
      WaitingOnBuild = waitingOnBuild resp
      Coverage = resp.Coverage
      Scope = scope }

/// True if a DiagnosticsResponse contains failures: any plugin Failed (or in a status
/// this build cannot read), or any error/warning-severity diagnostic (warnings
/// respecting noWarnFail). Both terms come from `CheckInputs.foundProblems` — THE
/// definition — so this and the converge-then-verdict path cannot drift.
let hasFailures (noWarnFail: bool) (resp: DiagnosticsResponse) : bool =
    CheckVerdict.CheckInputs.foundProblems resp.Statuses (failingDiagnosticCount noWarnFail resp)

/// Determine exit code from a DiagnosticsResponse.
/// Returns non-zero if any plugin is Failed, or if the ledger has failing entries.
/// When noWarnFail is true, only errors (not warnings) in the ledger trigger a non-zero exit code.
let exitCodeFromResponse (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    if hasFailures noWarnFail resp then 1 else 0

/// What this build could make of the run's `coverage` field.
///
/// Deliberately NOT `Result`: `Error` collides with `DiagnosticSeverity.Error`
/// here, and naming the cases says the thing anyway — `Unrecognized` is a fact
/// about THIS BUILD's vocabulary, not about the run, and must never be folded
/// into a pass.
type private CoverageRead =
    | Understood of RunVerification
    | Unrecognized of token: string

/// Render a generic IPC result (status JSON or plain text).
let renderIpcResult
    (mode: ProgressRenderer.RenderMode)
    (renderStatuses: Map<string, ParsedPluginStatus> -> string list)
    (noWarnFail: bool)
    (result: string)
    : int =
    // renderIpcResult tolerates non-JSON output (the daemon emits plain text for
    // some commands; the None branch falls through to eprintfn). Narrow to
    // :? JsonException so a real programming bug (e.g. null arg) propagates instead
    // of silently rendering raw text.
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
                    // exit 0 without a run is a vacuous green.
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

                        // NO PROJECT RAN AT ALL. `noTestsMatched` cannot cover this:
                        // `allZeroMatch` is deliberately false for an empty result set
                        // (no project is not the same claim as every project matched
                        // nothing), so without this branch an empty `projects` array
                        // fell through to "Tests passed" — a green for a run that
                        // executed nothing. That is the worst case wearing the best
                        // outcome's clothes, and it is why this branch exists.
                        let projectCount = projects.EnumerateArray() |> Seq.length

                        // The producer STATES what the run verified instead of leaving
                        // the consumer to infer it from array lengths.
                        //
                        // PARSED, not string-compared. The tokens are written in exactly
                        // one place (`RunVerification.token`, core) and read back through
                        // `tryParse`, so a rename cannot drift between the two ends. The
                        // previous form compared literals here and fell through to
                        // `Tests passed`, exit 0, for ANY token this build did not know —
                        // a newer daemon, a typo, a future case. That is the one outcome
                        // this whole area exists to prevent, and it was reachable by an
                        // `else`. `IpcParsing.fs`'s `ScopeUnreadable` gets the same
                        // decision right; this now matches it.
                        //
                        // `None` means BOTH "absent" and "unreadable", and both are
                        // handled below as no-verdict. Absent is the older-daemon case:
                        // reconstruct from the counts rather than assume anything.
                        let parsedCoverage =
                            match root.TryGetProperty("coverage") with
                            | true, c when c.ValueKind = JsonValueKind.String ->
                                match RunVerification.tryParse (c.GetString()) with
                                | Some v -> Understood v
                                | None -> Unrecognized(c.GetString())
                            | _ ->
                                // Older daemon: no `coverage` field at all. An ABSENT
                                // field must never be read as "ran", so derive it.
                                Understood(
                                    if projectCount = 0 then NoProjectsSelected
                                    elif noTestsMatched then AllZeroMatch projectCount
                                    else Ran
                                )

                        // AUTOMATION-272 — say what the run actually did, on every
                        // outcome including the green.
                        //
                        // These counts existed only in `daemon.log`, so telling a real
                        // pass from a vacuous one meant going and reading a log. The
                        // missing summary line IS the tell: "1 project: 1 matched
                        // nothing" and "1 project: 12 passed" are the same `✓` without
                        // it. Printed before the verdict so it is present whichever
                        // branch below fires.
                        let statusCounts =
                            projects.EnumerateArray()
                            |> Seq.map (fun p ->
                                match p.TryGetProperty("status") with
                                | true, s -> s.GetString()
                                | false, _ -> "unknown")
                            |> Seq.countBy id
                            |> Map.ofSeq

                        let countOf key =
                            statusCounts |> Map.tryFind key |> Option.defaultValue 0

                        let passedCount = countOf "passed"
                        let failedCount = countOf "failed"
                        let noMatchCount = countOf "no-tests-matched"
                        let otherCount = projectCount - passedCount - failedCount - noMatchCount

                        let summaryParts =
                            [ if passedCount > 0 then
                                  $"%d{passedCount} passed"
                              if failedCount > 0 then
                                  $"%d{failedCount} failed"
                              if noMatchCount > 0 then
                                  $"%d{noMatchCount} matched nothing"
                              // Deferred / timed-out / errored projects. Named as a
                              // group rather than silently omitted, so the parts always
                              // add up to the project count.
                              if otherCount > 0 then
                                  $"%d{otherCount} did not run" ]

                        if projectCount > 0 then
                            let detail = summaryParts |> String.concat ", "
                            UI.info $"  %d{projectCount} project(s): %s{detail}"

                        // Both no-op outcomes exit 3, matching `confirm`'s established
                        // contract: REFUSE TO GREEN WITHOUT EVIDENCE rather than
                        // report a pass nothing earned. Exit 0 here would sail through
                        // any `&&` chain and any CI gate.
                        if hasFailed then
                            UI.fail "Tests failed"
                            1
                        elif parsedCoverage = Understood NoProjectsSelected then
                            UI.fail "No test project ran — nothing was verified (not a pass)"
                            UI.info "  Why: no project was selected, so no test binary was invoked."

                            UI.info "  Nothing was discovered, so there are no test names to suggest here."

                            UI.info "  Wanted the whole suite?   fshw confirm   (unfiltered)"
                            UI.info "  Expected your diff to select something?   fshw status test-prune"
                            3
                        elif
                            (match parsedCoverage with
                             | Understood(AllZeroMatch _) -> true
                             | _ -> false)
                        then
                            UI.fail "No tests matched the filter — nothing was verified (not a pass)"

                            UI.info
                                $"  Why: %d{projectCount} project(s) ran and discovered their tests; the filter matched none of them."

                            // Two causes, and the ordering matters. The filter is fanned
                            // out across EVERY configured project, so a class that
                            // really exists still reports "matched nothing" in each
                            // project that does not contain it — and if the one that
                            // does was not among those invoked, the run says the same
                            // thing as a typo does. Naming only the typo sent the last
                            // investigation after a class that was never misspelled
                            // (AUTOMATION-272), so aim is offered first when the run was
                            // narrow enough for it to be the likely story.
                            if projectCount = 1 then
                                UI.info
                                    "  Either the class lives in a DIFFERENT project than the one that ran, or the pattern is a typo / renamed class."

                                UI.info
                                    "  Aim it:                    fshw test-rerun --project <name> --filter-class <pattern>"
                            else
                                UI.info "  A filter that matches nothing is usually a typo or a renamed class."

                                UI.info
                                    "  Or aim it at one project:  fshw test-rerun --project <name> --filter-class <pattern>"

                            UI.info "  See the discovered names:  fshw status test-prune"
                            UI.info "  Wanted the whole suite?    fshw confirm   (unfiltered)"
                            3
                        else
                            match parsedCoverage with
                            | Understood Ran ->
                                UI.success "Tests passed"
                                0
                            | Unrecognized unknown ->
                                // A token this build cannot interpret is NOT a pass. Same
                                // decision as `ScopeUnreadable` in IpcParsing: a reading
                                // you could not interpret must never read as a good one.
                                UI.fail "Cannot interpret what this run verified — no verdict (not a pass)"

                                UI.info
                                    $"  The daemon reported coverage '%s{unknown}', which this build does not recognize."

                                UI.info "  Most likely the daemon is NEWER than this CLI. Check both versions:"
                                UI.info "    ps ax | rg fshotwatch      # the running daemon's package path"
                                UI.info "  Then restart the daemon so it matches:  fshw stop"
                                3
                            | Understood NoProjectsSelected
                            | Understood(AllZeroMatch _) ->
                                // Unreachable: both are handled above. Present so that
                                // adding a case to RunVerification breaks THIS match
                                // rather than silently landing on a green.
                                UI.fail "internal: unhandled coverage case — no verdict"
                                3
                    | _ ->

                        match parsePluginStatuses result with
                        // The daemon answered and we could not read the answer. That is
                        // not "no plugins are failing" — it is not an answer. Exit
                        // non-zero: a status nobody could read must never be the basis
                        // of a zero exit code.
                        | Result.Error reason ->
                            UI.fail $"could not read the daemon's status: %s{reason}"
                            1
                        | Result.Ok parsed ->
                            let lines = renderStatuses parsed
                            let output = String.concat "\n" lines
                            eprintfn "%s" output

                            // The SAME predicate the verdict decides on, so `fshw status`
                            // and `fshw check` cannot disagree about whether a plugin failed.
                            if CheckVerdict.CheckInputs.anyPluginFailed parsed then
                                1
                            else
                                0

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
/// RENDERING ONLY and never decide the verdict.
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

        // RENDERING ONLY (see the soundness note above): an unreadable status payload
        // renders as nothing here and decides nothing. The verdict is settled by
        // `isSettled` and graded from `getErrors`, both of which fail closed on their
        // own — this loop is a progress display and must not become a second opinion.
        let parsed = parsePluginStatuses statusJson |> Result.defaultValue Map.empty

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

        // `create` REFUSES a green carrying a failing plugin. It cannot fire from here —
        // `outcome` is computed by `CheckVerdict.verdict` from the very statuses
        // `pluginVerdicts` renders, so a failing plugin has already made it red — and
        // that is exactly the property worth having a guard for: the day the two drift
        // apart again, fshw stops rather than stamping the contradiction on disk.
        let v =
            Verdict.create
                (Verdict.Command.ofCheckMode checkMode)
                runReport
                atWrite
                verdictOutcome
                exitCode
                plugins
                suites

        // Capture what is on disk BEFORE overwriting it. When this run executed no
        // tests, the prior verdict is the only thing that can answer the reader's
        // next question — "was this tree already verified, and by what?" — and the
        // write below destroys it. Read first, or the answer is gone.
        //
        // `Unreadable` is deliberately folded into `None`: a verdict this build
        // cannot parse must not be paraphrased into a reassuring sentence.
        let priorVerdict =
            match Verdict.read repoRoot with
            | Verdict.Reading.Found p -> Some p
            | Verdict.Reading.Missing
            | Verdict.Reading.Unreadable _ -> None

        Verdict.write repoRoot v

        if not UI.isInteractive then
            eprintfn ""

            for line in ProgressRenderer.AgentHints.forVerdict priorVerdict v do
                eprintfn "%s" line
    with
    | :? System.IO.IOException as ex ->
        FsHotWatch.Logging.warn "verdict" $"could not publish %s{Verdict.RelativePath}: %s{ex.Message}"
    | :? System.UnauthorizedAccessException as ex ->
        FsHotWatch.Logging.warn "verdict" $"could not publish %s{Verdict.RelativePath}: %s{ex.Message}"

/// Poll daemon status, render live progress, then decide a converge-then-verdict
/// outcome and return its exit code (0 = complete & clean, 1 = failures found,
/// 2 = completeness unachievable, 3 = `confirm` with an unearned scope).
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
    // (`run-tests` with no filter). This is `confirm`'s teeth: `set-scope full` only
    // makes the next run unfiltered, and on a warm daemon whose impact DB says nothing
    // changed there IS no next run — so `confirm` would refuse for want of evidence it
    // was never willing to go and get. Invoked ONLY in `Confirmation`, and only when the
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

    // The placeholder before anything has been ASKED. It reaches `publishVerdict` only
    // on the abort paths below (a wedged plugin, a daemon that shut down mid-wait), and
    // on those paths it must not say "the daemon reported no scope" — nobody asked it.
    // A placeholder that reads as an answer is the same mistake one layer down.
    let finalRun =
        ref
            { Scope = ScopeUnreadable "the check aborted before the test scope could be read"
              RunId = None }

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
        let reread () : CheckVerdict.CheckInputs =
            let resp = parseDiagnosticsResponse (getErrors ())
            let output = formatDiagnosticsResponse mode renderStatuses resp
            eprintfn "%s" output
            let run = getTestRun ()
            finalStatuses.Value <- resp.Statuses
            finalRun.Value <- run
            checkInputs noWarnFail run.Scope resp

        let firstRun = getTestRun ()
        finalRun.Value <- firstRun

        // CONFIRM EARNS ITS EVIDENCE.
        //
        // `set-scope full` was already sent (before the scan), so any run the scan
        // provoked is unfiltered — and on a cold daemon that is the whole story: the
        // scope reads `FullSuite` here and nothing more is run. But a WARM daemon whose
        // impact DB says nothing changed provokes NO run at all, and the scope we just
        // read is the last (filtered) run's. Refusing there is correct — there is no
        // whole-suite evidence — but refusing and STOPPING makes `confirm` unsatisfiable,
        // and an unsatisfiable check is one people route around with a shell script.
        //
        // So: no full-suite evidence ⇒ go and produce some. Then re-settle and re-read,
        // because a forced run can fail, and its failures are the answer.
        let initialRead =
            if CheckVerdict.confirmNeedsFullRun checkMode firstRun.Scope then
                eprintfn
                    "  Confirm: the tests that ran were %s — running the FULL suite to earn a verdict..."
                    (TestScope.describe firstRun.Scope)

                withProgress "Running the full suite (confirm)" "Running the full suite (confirm)..." (fun () ->
                    forceFullRun ())

                settle ()
                // `reread` refreshes finalStatuses/finalRun too, so the verdict is
                // computed — and PUBLISHED — from what the forced run actually did.
                reread ()
            else
                checkInputs noWarnFail firstRun.Scope firstResp

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
        | CheckVerdict.CheckOutcome.WaitingOnBuild ->
            // Non-green, but "could not complete", never a red. Distinct exit 2 so
            // an autonomous loop / deploy preflight retries rather than treating it
            // as a test failure.
            UI.fail
                "Check incomplete: waiting on build — a test project's build artifact was not produced, so its \
                 tests did not run. Nothing was verified (not a pass) and nothing failed (not a red); re-run once \
                 the build settles."
        | CheckVerdict.CheckOutcome.UnearnedScope(ScopeUnreadable reason) ->
            // Refused in BOTH modes, so it must not borrow `confirm`'s words: this is not
            // "the run was too narrow", it is "we could not see what the run was".
            UI.fail
                $"NO VERDICT — the test scope could not be read (%s{reason}).\nThat is not the same as \"no tests were \
                   needed\": a read that faulted cannot rule out that ZERO tests ran, which is the one thing a green \
                   may never mean. Nothing is reported broken, and nothing is reported sound either."
        | CheckVerdict.CheckOutcome.UnearnedScope scope ->
            // Nothing failed — and that is precisely the point. `confirm` was asked for
            // a claim about the whole suite and the tests that ran do not support one,
            // so it has no verdict to give. Say so; never launder it into a green.
            UI.fail
                $"Confirm: NO VERDICT — the tests that ran were %s{TestScope.describe scope}, \
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
