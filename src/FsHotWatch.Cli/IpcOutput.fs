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
/// Verbose/Compact: per-plugin progress block, then the colored by-file error block.
/// Agent: the banner + per-plugin lines are split so plain diagnostic lines slot in
/// *before* the trailing `next:` hint, giving line-by-line output with no ANSI.
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
/// `noWarnFail`), each paired with the file it was reported against.
///
/// ONE traversal, so the COUNT that decides the exit code and the CAUSES the verdict
/// file records are the same entries by construction (AUTOMATION-303). Two predicates
/// would be two chances to disagree, and "exit 1 with nothing named" is exactly what
/// disagreement looks like from outside.
let internal failingDiagnosticEntries (noWarnFail: bool) (resp: DiagnosticsResponse) : (string * DiagnosticEntry) list =
    let isFailure (e: DiagnosticEntry) =
        match e.Severity with
        | Error -> true
        | Warning -> not noWarnFail
        | Info
        | Hint
        // `Deferred` ("waiting on build") is never a failure — it routes the
        // verdict to `Incomplete`/exit 2 via `WaitingOnBuild`, not to a red.
        | Deferred
        // Nor is `HostAborted` ("the test host died"): it routes to `RunnerAborted`/exit 2
        // via `RunnerAbort`. Counting it here is precisely how a killed host used to
        // report as a red (AUTOMATION-294).
        | HostAborted -> false

    resp.Files
    |> Map.toList
    |> List.collect (fun (file, entries) -> entries |> List.filter isFailure |> List.map (fun e -> file, e))

/// HALF of "did check find real problems" — the other half is
/// `CheckInputs.anyPluginFailed`. Never used alone; see `checkInputs`.
let private failingDiagnosticCount (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    failingDiagnosticEntries noWarnFail resp |> List.length

/// The failing ledger entries as the verdict records them. `Plugin` is the LEDGER KEY,
/// so an FCS diagnostic — which belongs to no plugin — names `fcs` and stops being
/// invisible.
let internal redCausesOf (noWarnFail: bool) (resp: DiagnosticsResponse) : Verdict.RedCause list =
    failingDiagnosticEntries noWarnFail resp
    |> List.map (fun (file, e) ->
        { Verdict.Source = e.Plugin
          Verdict.File = file
          Verdict.Severity = DiagnosticSeverity.toString e.Severity
          Verdict.Message = e.Message
          Verdict.Kind = Verdict.RedCause.classify e.Plugin file e.Message })

/// How many of the failing entries are NOT claims about the tree on disk
/// (AUTOMATION-303). From `redCausesOf` — the very list the verdict records — so the
/// number that can turn a red into NO VERDICT and the reasons printed beside it are the
/// same entries by construction, BEFORE `MaxRedCauses` truncation.
let internal unattributableCountOf (noWarnFail: bool) (resp: DiagnosticsResponse) : int =
    redCausesOf noWarnFail resp |> Verdict.RedCause.unattributable |> List.length

/// Any "waiting on build" deferral in the ledger — and WHY? A `Deferred`-severity entry
/// means a test project's tests DID NOT run: non-green (nothing verified) but not a
/// failure. The verdict routes this to `Incomplete`/exit 2. Reading it off the parsed
/// ledger keeps it fail-closed — a severity that doesn't round-trip defaults to `Error`
/// (counted as failing), so the worst case of a wire bug is the OLD exit 1, never a
/// false green.
///
/// The messages are handed to `BuildWait.classify` rather than reduced to a bool here:
/// the two causes need opposite remedies, and this transport is not the place that
/// decides which — `RunOnceCheck` asks the same question of the same classifier.
let private waitingOnBuild (resp: DiagnosticsResponse) : CheckVerdict.BuildWait =
    resp.Files
    |> Map.toSeq
    |> Seq.collect snd
    |> Seq.filter (fun (e: DiagnosticEntry) -> e.Severity = Deferred)
    |> Seq.map (fun e -> e.Message)
    |> List.ofSeq
    |> CheckVerdict.BuildWait.classify

/// Did any test HOST DIE mid-run — and with what diagnosis? A `HostAborted`-severity
/// entry means a runner did not finish: non-green (nothing verified) but not a failure.
/// The verdict routes this to `RunnerAborted`/exit 2.
///
/// The in-process twin is `RunOnceCheck.runnerAborted`; both read the SAME condition and
/// hand it to the SAME classifier. Fail-closed like `waitingOnBuild`: a severity that
/// does not round-trip the wire defaults to `Error` and is counted as failing, so the
/// worst case of a wire bug is the OLD exit 1, never a false green.
let private runnerAborted (resp: DiagnosticsResponse) : CheckVerdict.RunnerAbort =
    resp.Files
    |> Map.toSeq
    |> Seq.collect snd
    |> Seq.filter (fun (e: DiagnosticEntry) -> e.Severity = HostAborted)
    |> Seq.map (fun e -> e.Message)
    |> List.ofSeq
    |> CheckVerdict.RunnerAbort.classify

/// The daemon transport's observations, as `CheckVerdict.verdict` consumes them.
///
/// This is the daemon's half of "one verdict, two transports"; the in-process half is
/// `RunOnceCheck.reread`. Neither computes a verdict; both hand over the same record,
/// and `CheckVerdict` decides. Adding a term to `CheckInputs` breaks both.
let internal checkInputs (noWarnFail: bool) (scope: TestScope) (resp: DiagnosticsResponse) : CheckVerdict.CheckInputs =
    { PluginStatuses = resp.Statuses
      FailingDiagnostics = failingDiagnosticCount noWarnFail resp
      UnattributableDiagnostics = unattributableCountOf noWarnFail resp
      WaitingOnBuild = waitingOnBuild resp
      RunnerAborted = runnerAborted resp
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
    /// An older daemon sent no `coverage` field, and the reported COUNTS establish
    /// that projects executed tests. That is all they establish: counts cannot say
    /// whether the run was impact-filtered, so this deliberately carries no
    /// `RunScope` rather than inventing one (AUTOMATION-282). The CLI's exit code
    /// only asks "did anything run", so nothing here needs the breadth — and a
    /// consumer that DID need it must not be handed a guess.
    | RanPerCounts
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
                        // Name and status of every project the run consulted. Kept as a
                        // list (not just a count) because a refusal has to say WHAT it
                        // searched: "no tests matched the filter" over an unnamed set of
                        // projects is indistinguishable from a typo, and reading the
                        // names off `fshw status test-prune` afterwards is a step people
                        // skip (AUTOMATION-227/272).
                        let projectStatuses =
                            projects.EnumerateArray()
                            |> Seq.map (fun p ->
                                let name =
                                    match p.TryGetProperty("project") with
                                    | true, n when n.ValueKind = JsonValueKind.String -> n.GetString()
                                    | _ -> "(unnamed)"

                                let status =
                                    match p.TryGetProperty("status") with
                                    | true, s when s.ValueKind = JsonValueKind.String -> s.GetString()
                                    | _ -> "unknown"

                                name, status)
                            |> List.ofSeq

                        // AUTOMATION-272, criterion 3. The per-project TEST counts, from
                        // the CTRF report each project wrote. Rendered for every project on
                        // every outcome, green included: "a missing summary line is the tell
                        // that separates a real pass from a vacuous one", and until now the
                        // counts existed only in `daemon.log`, so noticing required going to
                        // look. Now the ✓ has to carry them.
                        //
                        // ABSENT counts print as an explicit "no test report", never as
                        // zeros: `total: 0, failed: 0` reads as a suite that ran cleanly,
                        // which is the same vacuous green one level down.
                        let countsLine (p: JsonElement) : string =
                            let field (name: string) =
                                match p.TryGetProperty("counts") with
                                | true, c when c.ValueKind = JsonValueKind.Object ->
                                    match c.TryGetProperty(name) with
                                    | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
                                    | _ -> None
                                | _ -> None

                            match field "total", field "succeeded", field "failed" with
                            | Some total, Some succeeded, Some failed ->
                                let skipped =
                                    match field "skipped" with
                                    | Some n when n > 0 -> $", %d{n} skipped"
                                    | _ -> ""

                                $"total %d{total}, %d{succeeded} succeeded, %d{failed} failed%s{skipped}"
                            | _ -> "no test report — counts unknown (not zero)"

                        let perProjectLines =
                            Seq.zip projectStatuses (projects.EnumerateArray())
                            |> Seq.map (fun ((name, status), p) -> $"  · %s{name} [%s{status}] — %s{countsLine p}")
                            |> List.ofSeq

                        // NO PROJECT RAN AT ALL. `noTestsMatched` cannot cover this:
                        // `allZeroMatch` is deliberately false for an empty result set
                        // (no project is not the same claim as every project matched
                        // nothing), so without this branch an empty `projects` array
                        // reads as "Tests passed" — a green for a run that executed
                        // nothing.
                        let projectCount = projectStatuses.Length

                        let statusCounts = projectStatuses |> List.countBy snd |> Map.ofList

                        let countOf key =
                            statusCounts |> Map.tryFind key |> Option.defaultValue 0

                        let passedCount = countOf "passed"
                        let failedCount = countOf "failed"
                        let timedOutCount = countOf "timed-out"
                        let noMatchCount = countOf "no-tests-matched"

                        let otherCount =
                            projectCount - passedCount - failedCount - timedOutCount - noMatchCount

                        // A project killed at its timeout is a FAILURE, and was not being
                        // counted as one: `hasFailed` matched only `"failed"`, so a run
                        // whose sole project was killed at its timeout printed
                        // "✓ Tests passed" and exited 0 while the daemon's own terminal
                        // status for the same run was `failedNow` + `CompleteWithTimeout`.
                        // The CLI and the daemon may not disagree about a red.
                        let hasFailed = failedCount > 0 || timedOutCount > 0

                        // Which wire statuses mean "a test actually executed". Listed
                        // POSITIVELY and closed: an unrecognised status (a daemon newer
                        // than this CLI) is NOT counted as having executed, so the
                        // fallback below fails closed rather than greening on a word it
                        // does not know.
                        let executedCount = passedCount + failedCount + timedOutCount

                        // Run-level "matched nothing": every project was a
                        // zero-match-under-filter pass. Reported DISTINCTLY so a
                        // `test-rerun --filter-*` that selected no test never looks
                        // like a real green run that exercised code.
                        let noTestsMatched =
                            match root.TryGetProperty("noTestsMatched") with
                            | true, n -> n.ValueKind = JsonValueKind.True
                            | false, _ -> false

                        // The producer STATES what the run verified instead of leaving
                        // the consumer to infer it from array lengths.
                        //
                        // PARSED, not string-compared. The tokens are written in exactly
                        // one place (`RunVerification.token`, core) and read back through
                        // `tryParse`, so a rename cannot drift between the two ends —
                        // and, unlike a literal comparison with an `else`, a token this
                        // build does not know cannot fall through to exit 0.
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
                                // field must never be read as "ran", so derive what the
                                // counts actually establish — and no more than that.
                                if projectCount = 0 then
                                    Understood NoProjectsSelected
                                elif noTestsMatched then
                                    Understood(AllZeroMatch projectCount)
                                elif executedCount = 0 then
                                    // AUTOMATION-227. The MIXED case, and the one this
                                    // fallback used to hand to `RanPerCounts` → exit 0:
                                    // `{"projects":[{"status":"no-tests-matched"},
                                    // {"status":"deferred"}]}` is not all-zero-match, so
                                    // `noTestsMatched` is false, yet NOT ONE project
                                    // executed a test. The per-project statuses that
                                    // establish it were already in hand and went unused.
                                    Understood NothingExecuted
                                else
                                    RanPerCounts

                        // Say what the run actually did, on every outcome including the
                        // green: "1 project: 1 matched nothing" and "1 project: 12
                        // passed" are the same `✓` without this line, and the counts are
                        // otherwise only in `daemon.log`. Printed before the verdict so
                        // it is present whichever branch below fires.
                        let summaryParts =
                            [ if passedCount > 0 then
                                  $"%d{passedCount} passed"
                              if failedCount > 0 then
                                  $"%d{failedCount} failed"
                              // Named separately from "failed" and from "did not run":
                              // a killed run is a red, but a red whose cause is the clock
                              // rather than an assertion.
                              if timedOutCount > 0 then
                                  $"%d{timedOutCount} timed out"
                              if noMatchCount > 0 then
                                  $"%d{noMatchCount} matched nothing"
                              // Deferred / errored projects. Named as a group rather than
                              // silently omitted, so the parts always add up to the
                              // project count.
                              if otherCount > 0 then
                                  $"%d{otherCount} did not run" ]

                        if projectCount > 0 then
                            let detail = summaryParts |> String.concat ", "
                            UI.info $"  %d{projectCount} project(s): %s{detail}"

                            for line in perProjectLines do
                                UI.info line

                        // The filter the run was launched with, as the daemon reported
                        // it. `None` is NOT rendered as "(none)": an absent field means
                        // this daemon predates the field, which is a different fact from
                        // "the run was unfiltered", and a refusal that guesses between
                        // them is how a wrong conclusion gets drawn.
                        let activeFilter =
                            match root.TryGetProperty("filter") with
                            | true, f when f.ValueKind = JsonValueKind.String && f.GetString() <> "" ->
                                Some(f.GetString())
                            | _ -> None

                        /// The refusal's two evidence lines: WHAT was searched for and
                        /// WHERE. Printed by every "nothing was verified" branch, so no
                        /// refusal can be the count-only message that sent one
                        /// investigation after a class that was never misspelled.
                        let reportSearch () =
                            match activeFilter with
                            | Some f -> UI.info $"  Filter:   %s{f}"
                            | None ->
                                UI.info
                                    "  Filter:   not reported (this daemon predates the field) — restart it with `fshw stop` to see it here"

                            // Capped, with the remainder COUNTED rather than dropped: a
                            // truncated list that does not say it was truncated reads as
                            // the whole selection.
                            let shown = projectStatuses |> List.truncate 10

                            let rendered =
                                shown |> List.map (fun (n, s) -> $"%s{n} (%s{s})") |> String.concat ", "

                            let more = projectCount - shown.Length

                            let suffix = if more > 0 then $", +%d{more} more" else ""

                            if projectCount > 0 then
                                UI.info $"  Searched: %s{rendered}%s{suffix}"

                        // Both no-op outcomes exit 3, matching `confirm`'s established
                        // contract: REFUSE TO GREEN WITHOUT EVIDENCE rather than
                        // report a pass nothing earned. Exit 0 here would sail through
                        // any `&&` chain and any CI gate.
                        if hasFailed then
                            if failedCount > 0 then
                                UI.fail "Tests failed"
                            else
                                // Reached only via `timedOutCount`. Worded so the cause is
                                // not mistaken for an assertion failure — a killed run
                                // usually means a wedged host or a too-tight budget.
                                UI.fail $"%d{timedOutCount} test project(s) were KILLED at their timeout — not a pass"

                            1
                        elif parsedCoverage = Understood NoProjectsSelected then
                            UI.fail "No test project ran — nothing was verified (not a pass)"
                            reportSearch ()
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
                            reportSearch ()

                            UI.info
                                $"  Why: %d{projectCount} project(s) ran and discovered their tests; the filter matched none of them."

                            // Two causes, and the ordering matters. The filter is fanned
                            // out across EVERY configured project, so a class that really
                            // exists still reports "matched nothing" in each project that
                            // does not contain it — indistinguishable from a typo. Naming
                            // only the typo has already sent one investigation after a
                            // class that was never misspelled, so aim is offered first
                            // when the run was narrow enough for it to be the likely story.
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
                            // Scope is irrelevant to the exit code — this asks only
                            // whether anything was verified, so both breadths pass.
                            | Understood(Ran _)
                            | RanPerCounts ->
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
                            | Understood NothingExecuted ->
                                // Projects reported and not one ran a test. Reachable —
                                // unlike the two below — because the zero-match branch
                                // above only catches results that MATCHED nothing, not
                                // ones that deferred or errored.
                                UI.fail "Nothing was verified — every project failed to execute a test (not a pass)"
                                // The per-project statuses ARE the diagnosis here, so
                                // they are printed rather than pointed at.
                                reportSearch ()
                                UI.info "  Run `fshw status test-prune` for the full output of each."
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
/// SOUNDNESS: the loop termination is `isSettled`, NOT a status-map predicate like
/// `isAllTerminal`. `isAllTerminal` treats `Idle` as quiescent and never consults the
/// host's inflight/busy state, so it concludes "settled" while a downstream plugin
/// (test-prune) still has a `BuildCompleted` event queued in its mailbox (status
/// observably `Idle`, handler not yet run) or while it is mid-run with a non-empty
/// pending-verification queue — which exits 0 having computed N affected tests BEFORE
/// the test-prune run's verdict was captured. The authoritative settle is the daemon's
/// `WaitForComplete` RPC (`waitForVerdict` → `requireVerdict=true`, gating on
/// `AnyPluginBusy` + generation advancement + quiescence), so `isSettled` is wired to
/// that RPC's completion. The status reads here are for RENDERING ONLY and never
/// decide the verdict.
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
        // `isSettled` and graded from `getErrors`, both of which fail closed on their own.
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

/// True when `ex` (walking its inner-exception chain) indicates the daemon shut down or
/// the IPC pipe dropped WHILE a `WaitForComplete` verdict wait was in flight — as opposed
/// to a genuine plugin verdict, which comes back as a normal status payload, never a
/// fault. Matches StreamJsonRpc's connection-loss exception (by type-name substring, so
/// no compile-time dependency on the transport assembly), a raw pipe teardown, and the
/// daemon's graceful-shutdown sentinel propagated back as a remote invocation error.
///
/// Used to turn a mid-wait teardown into a diagnostic exit 2 ("no verdict was produced")
/// instead of an opaque crash: the waiting client must never see a silent drop.
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

/// True when `ex` (walking its inner-exception chain) is memory exhaustion — in THIS
/// process or in the daemon. AUTOMATION-747.
///
/// Matched by TYPE, never by message text: `OutOfMemoryException`'s message is a
/// localized framework resource string, and a predicate that decides a merge gate's
/// exit code on a substring of it answers differently in another locale. A fault raised
/// on the daemon crosses the wire as `RemoteInvocationException` — never as the
/// daemon's own exception type — so the remote error payload's `TypeName` is the
/// evidence on that side, exactly as `Program.remoteFaultDetails` reads it.
///
/// `OverflowException` counts: System.Text.Json's UTF-8 transcoder raises it, not an
/// OOM, when one string token is too large to encode. Both are the same fact — a
/// payload nobody can build — and both used to reach a caller as a bare exit 2.
let rec isMemoryExhaustion (ex: exn) : bool =
    match ex with
    | null -> false
    | :? OutOfMemoryException
    | :? OverflowException -> true
    | :? StreamJsonRpc.RemoteInvocationException as remote ->
        let rec namesMemoryFault (data: StreamJsonRpc.Protocol.CommonErrorData) =
            not (isNull data)
            && (data.TypeName = "System.OutOfMemoryException"
                || data.TypeName = "System.OverflowException"
                || namesMemoryFault data.Inner)

        (match remote.DeserializedErrorData with
         | :? StreamJsonRpc.Protocol.CommonErrorData as data -> namesMemoryFault data
         | _ -> false)
        || (not (isNull ex.InnerException) && isMemoryExhaustion ex.InnerException)
    | _ -> not (isNull ex.InnerException) && isMemoryExhaustion ex.InnerException

/// The tree a check had just finished verifying, captured by the TRANSPORT at the
/// instant it settled.
///
/// AUTOMATION-167. Deliberately NOT computed inside `publishVerdict`. Everything
/// between settling and publishing — reading diagnostics, reading the test scope,
/// rendering the summary, converging — runs against a LIVE working tree, so a
/// publisher that hashes on entry takes BOTH of its snapshots after that window and
/// sees one consistent post-move tree. The move it exists to catch is invisible to it,
/// and it publishes green over a tree nobody checked. The comparison is only sound if
/// one side of it was taken where the verifying stopped.
type internal SettledTree =
    /// The content address of the tree at the moment the check settled — the tree this
    /// verdict is about to make a claim about.
    | VerifiedTree of FsHotWatch.TreeHash.Tree
    /// The check aborted before it ever settled (a wedged plugin, a daemon that went
    /// away mid-wait). There is no verified tree, so there is nothing for a
    /// publication-time tree to be compared WITH. Stated, rather than faked with a hash
    /// taken now — which would make the comparison vacuously pass and read as "the tree
    /// held still" over a check that never ran.
    | NeverSettled

module internal SettledTree =
    /// Capture the tree the check has just finished verifying. Call this AT the settle
    /// boundary — where `WaitForComplete` returns on the daemon path, where the
    /// in-process scan returns on the `--run-once` one — and nowhere later.
    let capture (repoRoot: string) (excludePatterns: string list) : SettledTree =
        VerifiedTree(FsHotWatch.TreeHash.compute repoRoot excludePatterns)

let internal priorVerdictToPreserve
    (outcome: CheckVerdict.CheckOutcome)
    (currentTreeHash: string)
    (currentTreeHashAlgorithm: string)
    (priorConfirmation: unit -> Verdict.PriorConfirmation)
    : Verdict.Verdict option =
    match outcome with
    | CheckVerdict.CheckOutcome.UnearnedScope(NoTestsRun _) ->
        match priorConfirmation () with
        | Verdict.PriorConfirmation.StillApplies prior when
            prior.TreeHash = currentTreeHash
            && prior.TreeHashAlgorithm = currentTreeHashAlgorithm
            ->
            Some prior
        | Verdict.PriorConfirmation.StillApplies _
        | Verdict.PriorConfirmation.MustEarn -> None
    | _ -> None

type internal RetainedTestRun =
    { Report: TestRunReport
      TreeHash: string }

module internal TestRunEvidence =
    let private executed (report: TestRunReport) =
        match report.RunId, report.Scope with
        | None, _ -> false
        | Some _, FullSuite _ -> true
        | Some _, ImpactFiltered(ran, total) -> ran > 0 && total > 0 && ran <= total
        | Some _, NoTestsRun _
        | Some _, ScopeUnknown
        | Some _, ScopeUnreadable _ -> false

    /// AUTOMATION-533. The runs THIS CHECK is accountable for, folded from one reading
    /// of the daemon.
    ///
    /// Both ends are DECLARED. `baseline` is the session ledger the driver read from the
    /// daemon BEFORE its scan, so a run belonging to an earlier check cannot be adopted
    /// by this one; `report.SessionRuns` is the same ledger as it stands now. Everything
    /// the daemon has completed since the baseline was taken belongs to this check —
    /// including the batches nobody watched go by, which is the whole difficulty: a run
    /// the CLI never happened to observe is exactly the one a reader later fails to find
    /// their tests in.
    ///
    /// Oldest first, so the list reads in the order the batches ran, and `distinct`, so
    /// re-reading the daemon cannot double-count. A daemon that predates the ledger
    /// sends nothing, and then the only run this can name is the one it observed — which
    /// is today's behaviour exactly, never worse.
    ///
    /// `None` is NO BASELINE — the driver could not take one — and is NOT an empty
    /// baseline. An empty one is the positive claim "the daemon had run nothing", which a
    /// fresh in-process host can make; treating a failed read as that claim would hand
    /// this check every run an earlier one left in the ledger. Unknown therefore falls
    /// back to attributing only what this check OBSERVED, which under-reports rather
    /// than over-claims.
    let attribute (baseline: Set<Guid> option) (soFar: Guid list) (report: TestRunReport) : Guid list =
        let newlyRun =
            match baseline with
            | None -> Option.toList report.RunId
            | Some known ->
                (List.rev report.SessionRuns) @ Option.toList report.RunId
                |> List.filter (fun id -> not (known.Contains id))

        soFar @ newlyRun |> List.distinct

    let reconcile
        (settledTree: SettledTree)
        (current: TestRunReport)
        (retained: RetainedTestRun option)
        : TestRunReport * RetainedTestRun option =
        match settledTree, current.Scope with
        | VerifiedTree tree, ImpactFiltered _ ->
            match retained with
            | Some evidence when
                String.Equals(evidence.TreeHash, tree.Hash, StringComparison.Ordinal)
                && (match evidence.Report.Scope with
                    | FullSuite _ -> true
                    | _ -> false)
                ->
                evidence.Report, retained
            | _ when executed current ->
                current,
                Some
                    { Report = current
                      TreeHash = tree.Hash }
            | _ -> current, None
        | VerifiedTree tree, _ when executed current ->
            current,
            Some
                { Report = current
                  TreeHash = tree.Hash }
        | VerifiedTree tree, NoTestsRun NoTestsReason.AlreadyVerified ->
            match retained with
            | Some evidence when String.Equals(evidence.TreeHash, tree.Hash, StringComparison.Ordinal) ->
                evidence.Report, retained
            | _ -> current, None
        | _ -> current, None

/// Publish the run's verdict as `.fshw/verdict.json` and — when a MACHINE is reading
/// (stdout not a TTY) — print the steering block that names it. The file and the exit
/// code are two renderings of ONE `CheckOutcome`, never a second computation.
///
/// TREE HASH: compared against `settledTree`, the tree the caller captured when its
/// check settled, using a second hash taken immediately before the write. If the two
/// differ, the working tree moved underneath the verdict while it was being produced,
/// and the honest answer is `incomplete`, not a green over a tree nobody checked.
///
/// Best-effort by design: a repo whose `.fshw/` cannot be written must still get
/// its exit code. The verdict is an additional surface, never a new way to fail.
let private publishVerdictWithReason
    // AUTOMATION-555. The invocation this verdict belongs to: the id it is stamped
    // with (so the wrapping CLI can attach ITS evidence to THIS file and no other) and
    // the origin every interval below is measured from.
    (invocation: Verdict.Invocation)
    (repoRoot: string)
    (excludePatterns: string list)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (runReport: TestRunReport)
    // AUTOMATION-259. The check-vs-confirm sample this run can offer: an EXECUTED reading
    // when `confirm` escalated away from an impact-scoped run, a PROJECTION through the
    // retained selection when it did not have to (the common case in CI), or nothing at
    // all — which is a FACT the verdict states, not a silence.
    (checkScoped: Verdict.CheckScopedEvidence)
    (statuses: Map<string, ParsedPluginStatus>)
    // AUTOMATION-555 (rework). The daemon's phase ledger — where ITS wall time went,
    // including the plugin runs this check waited on and a later re-run superseded.
    (daemonEvidence: IpcParsing.DaemonEvidence)
    // AUTOMATION-303. The failing ledger diagnostics the exit code was computed from —
    // including the ones no plugin owns (`fcs`), which is the whole point: a `confirm`
    // returned exit 1 with every plugin `ok` and 9,064 tests passed, and the file it
    // wrote named nothing at all.
    (redCauses: Verdict.RedCause list)
    // AUTOMATION-167. The tree the CALLER was verifying, captured at its settle
    // boundary. Required, not derived: see `SettledTree` — a tree hashed here is
    // hashed too late to catch a move, because the move happens between settling and
    // publishing and both of a publisher's own snapshots fall on the far side of it.
    (settledTree: SettledTree)
    (outcome: CheckVerdict.CheckOutcome)
    (terminalIncompleteReason: string option)
    // AUTOMATION-167. RETURNS the exit code it wrote, so the caller cannot compute a
    // second one. The tree-moved downgrade below is decided HERE — it needs the two
    // hashes taken around the write — so a caller re-deriving the code from `outcome`
    // misses exactly the case the double-hash exists to catch: the file said
    // `incomplete`/2 and the process returned 0, which is what CI reads.
    : int =
    try
        // AUTOMATION-533. EVERY batch this check has evidence from, not just the one the
        // daemon's receipt names — see `Verdict.runSuites`.
        let runs = Verdict.runSuites repoRoot runReport
        let plugins = Verdict.pluginVerdicts (not noWarnFail) (DateTime.UtcNow) statuses
        let atWrite = FsHotWatch.TreeHash.compute repoRoot excludePatterns

        let verdictOutcome, exitCode =
            match terminalIncompleteReason, settledTree with
            // A terminal infrastructure failure is already the answer. Preserve its
            // exact diagnosis in the machine-readable verdict instead of rounding it
            // down to the generic "coverage could not be confirmed" sentence that
            // sent AUTOMATION-290 away from the project-loader failure.
            | _, VerifiedTree settled when settled.Hash <> atWrite.Hash ->
                Verdict.Incomplete
                    "the working tree changed while the verdict was being produced — nothing is claimed about it",
                CheckVerdict.exitCode (CheckVerdict.CheckOutcome.Incomplete -1)
            | Some reason, _ ->
                Verdict.Incomplete reason, CheckVerdict.exitCode (CheckVerdict.CheckOutcome.Incomplete -1)
            // A tree that held still — and, on the abort paths, a check that never
            // settled at all. `NeverSettled` cannot be what makes a verdict incomplete:
            // the abort already did, and both callers that reach here hand in an
            // `Incomplete`. Re-deciding it from a comparison with no left-hand side
            // would invent an answer rather than read one.
            | None, VerifiedTree _
            | None, NeverSettled -> Verdict.outcomeOfCheck outcome, CheckVerdict.exitCode outcome

        let command = Verdict.Command.ofCheckMode checkMode

        // AUTOMATION-258. `confirm` escalates a filtered scope to a forced full run and
        // never accepts the filtered one — but when that forced run does not COMPLETE (a
        // build that failed, so its tests never ran), the daemon's `test-scope` still
        // answers with the PRE-escalation coverage: the scope is a projection of
        // `LastCoverage` (`TestPrunePlugin`) and no later run finished to move it.
        // Recorded verbatim that produced `command: confirm, scope: {kind: "filtered",
        // 5/6}`, which reads cold as "confirm settled for 5 of 6 projects" — the opposite
        // of what confirm did.
        //
        // Only the RECORD is corrected. `verdictOutcome` and `exitCode` are untouched: a
        // confirm that hit compile errors is a RED and must stay one, or a deploy preflight
        // would read "retry" over a tree that is broken and stop nothing.
        let runReport =
            { runReport with
                Scope = Verdict.scopeToRecord command runReport.Scope }

        // AUTOMATION-259. Classified against `verdictOutcome` — the outcome this verdict
        // RECORDS, including the tree-moved-underneath downgrade above, because that is the
        // answer `confirm` actually earned and therefore the only one worth comparing with.
        //
        // `check` gets `notRecorded`, not a classification of `None`: it never escalates,
        // so it never had a second reading, and saying "nothing to compare" of a command
        // that never compares would be a fact about the verb, not about this run.
        let comparison =
            match command with
            | Verdict.Check -> Verdict.CheckComparison.notRecorded
            | Verdict.Confirm -> Verdict.comparisonOf checkScoped runReport verdictOutcome statuses redCauses

        // `create` REFUSES a green carrying a failing plugin. It cannot fire from here —
        // `outcome` is computed by `CheckVerdict.verdict` from the very statuses
        // `pluginVerdicts` renders — but if the two ever drift apart, fshw stops rather
        // than stamping the contradiction on disk.
        // AUTOMATION-158. The declared gaps in this run's scope, re-read from
        // `.fshw.json` HERE rather than plumbed down from the config load.
        //
        // Deliberate, and safe for the same reason the tree hash is taken twice
        // around this write: `.fshw.json` is part of the hashed tree, so a config
        // edit between the load and this read moves `atWrite` and downgrades the
        // verdict to `incomplete` — the file cannot record exclusions from a
        // config the run was not governed by and still claim a green.
        //
        // Failing to read them is NOT an empty list: `[]` is the positive claim
        // "nothing was excluded", and a config we could not parse has not
        // established that. `readExclusions` returns `None` there, and the verdict
        // says it does not know.
        let excluded = SolutionScope.readExclusions repoRoot

        // AUTOMATION-555. Where the wall time went. Every interval is placed against the
        // ONE origin the invocation captured before any hook ran, and refused — by name —
        // when it falls outside the invocation: a plugin run or a `tests.beforeRun` step
        // from an EARLIER run (a warm daemon that had nothing to re-run) is not work this
        // invocation did, and a cache replay did no work at all. What cannot be placed
        // is reported as an incompleteness reason, separately from the percentage the
        // placed spans explain.
        let observedSoFar = Verdict.Invocation.elapsedMs invocation

        let hooks, hookSpans, hookReasons =
            DaemonConfig.HookTimings.read repoRoot runReport.RunId
            |> List.fold
                (fun (hooks, spans, reasons) timing ->
                    let scope = "tests.beforeRun"

                    match
                        Verdict.TimingSpan.ofWallClock
                            invocation
                            observedSoFar
                            scope
                            timing.StartedAtUtc
                            (TimeSpan.FromMilliseconds(float timing.ElapsedMs))
                            (Some timing.Command)
                    with
                    // `Result.` qualified: `ErrorLedger` is open here and its `Error`
                    // severity case shadows the result constructor.
                    | Result.Ok span ->
                        let hook: Verdict.HookVerdict =
                            { Scope = scope
                              StepIndex = timing.StepIndex
                              StepCount = timing.StepCount
                              Command = timing.Command
                              ElapsedMs = timing.ElapsedMs
                              Outcome = timing.Outcome }

                        hook :: hooks, span :: spans, reasons
                    | Result.Error reason -> hooks, spans, reason :: reasons)
                ([], [], [])
            |> fun (hooks, spans, reasons) -> List.rev hooks, List.rev spans, List.rev reasons

        // (rework) The daemon's own phases — startup, discovery, the scan `WaitForScan`
        // blocked on, change batches — and EVERY plugin `Running` interval, clipped to
        // this invocation. A served ledger already carries each plugin's runs, the
        // superseded ones included, so the `lastRun` records are the fallback for a
        // daemon that serves none. Nothing here is refused by name: an interval that
        // fell outside the invocation is history, and what the placed intervals fail
        // to explain surfaces as the derived coverage gap, not as a silence.
        let daemonSpans =
            match daemonEvidence with
            | IpcParsing.DaemonEvidence.Served phases ->
                phases
                |> List.choose (Verdict.TimingSpan.ofDaemonPhase invocation observedSoFar)
            | IpcParsing.DaemonEvidence.NotServed ->
                statuses
                |> Map.toList
                |> List.choose (fun (name, parsed) ->
                    parsed.LastRun
                    |> Option.bind (Verdict.TimingSpan.ofPluginRun invocation observedSoFar name))

        let attribution: Verdict.Attribution =
            { Hooks = hooks
              TimingSpans = hookSpans @ daemonSpans
              // A terminal failure ended the run before its timing could be complete,
              // and says so here as well as in `outcome` — the two questions are read
              // by different consumers. Completeness itself is DERIVED from the spans
              // (`Attribution.incompleteReasons`); this list holds only what was refused.
              RefusedEvidence = hookReasons @ Option.toList terminalIncompleteReason
              ObservedElapsedMs = Some observedSoFar
              InvocationId = Some invocation.Id }

        let v =
            Verdict.create command runReport atWrite excluded verdictOutcome exitCode plugins runs comparison redCauses
            |> Verdict.withAttribution attribution

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

        // A completed re-scan that ran no tests has NO new evidence. It remains an
        // exit-3 refusal for this invocation, but it must not overwrite a full-suite
        // green that this binary already earned over this exact tree: doing so turns a
        // successful no-op convergence pass into evidence destruction.
        //
        // `priorConfirmation` is the one cross-process reuse gate. It checks the tree
        // hash, tree-hash algorithm and producer identity before it returns
        // `StillApplies`, so preservation cannot promote a green from another tree or
        // binary. Comparing the prior tree with `v` additionally binds it to the tree
        // this publisher is about to describe.
        let preservedPrior =
            priorVerdictToPreserve outcome v.TreeHash v.TreeHashAlgorithm (fun () ->
                Verdict.priorConfirmation repoRoot excludePatterns)

        match preservedPrior with
        | Some _ -> ()
        | None -> Verdict.write repoRoot v

        if not UI.isInteractive then
            eprintfn ""

            for line in ProgressRenderer.AgentHints.forVerdict priorVerdict v do
                eprintfn "%s" line

        exitCode
    with
    // Nothing was written, so there is no code "it wrote" to hand back. Fall back to
    // the caller's own outcome — today's behaviour exactly — rather than inventing a
    // red: a verdict file we could not save is a reporting failure, not a verdict.
    | :? System.IO.IOException as ex ->
        FsHotWatch.Logging.warn "verdict" $"could not publish %s{Verdict.RelativePath}: %s{ex.Message}"
        CheckVerdict.exitCode outcome
    | :? System.UnauthorizedAccessException as ex ->
        FsHotWatch.Logging.warn "verdict" $"could not publish %s{Verdict.RelativePath}: %s{ex.Message}"
        CheckVerdict.exitCode outcome

/// Publish the ordinary check outcome, owned by one explicit CLI invocation. This
/// stable wrapper keeps the many normal terminal paths unable to accidentally invent
/// an infrastructure diagnosis.
let internal publishVerdictForInvocation
    (invocation: Verdict.Invocation)
    (repoRoot: string)
    (excludePatterns: string list)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (runReport: TestRunReport)
    (checkScoped: Verdict.CheckScopedEvidence)
    (statuses: Map<string, ParsedPluginStatus>)
    (daemonEvidence: IpcParsing.DaemonEvidence)
    (redCauses: Verdict.RedCause list)
    (settledTree: SettledTree)
    (outcome: CheckVerdict.CheckOutcome)
    : int =
    publishVerdictWithReason
        invocation
        repoRoot
        excludePatterns
        checkMode
        noWarnFail
        runReport
        checkScoped
        statuses
        daemonEvidence
        redCauses
        settledTree
        outcome
        None

/// `publishVerdictForInvocation` for a publish that no CLI bracket wraps — tests and
/// embedders. Production check/confirm paths always pass their invocation through.
let internal publishVerdict
    (repoRoot: string)
    (excludePatterns: string list)
    (checkMode: CheckVerdict.CheckMode)
    (noWarnFail: bool)
    (runReport: TestRunReport)
    (checkScoped: Verdict.CheckScopedEvidence)
    (statuses: Map<string, ParsedPluginStatus>)
    (redCauses: Verdict.RedCause list)
    (settledTree: SettledTree)
    (outcome: CheckVerdict.CheckOutcome)
    : int =
    publishVerdictForInvocation
        (Verdict.Invocation.start ())
        repoRoot
        excludePatterns
        checkMode
        noWarnFail
        runReport
        checkScoped
        statuses
        IpcParsing.DaemonEvidence.NotServed
        redCauses
        settledTree
        outcome

/// Publish an infrastructure failure that made the run un-completable before
/// plugin/test verdict inputs existed. Exit 2, never red, and the exact reason is
/// retained in `verdict.json` so an older green cannot survive or misdirect.
let internal publishTerminalIncompleteForInvocation
    (invocation: Verdict.Invocation)
    (repoRoot: string)
    (excludePatterns: string list)
    (checkMode: CheckVerdict.CheckMode)
    (reason: string)
    (settledTree: SettledTree)
    : int =
    publishVerdictWithReason
        invocation
        repoRoot
        excludePatterns
        checkMode
        false
        (TestRunReport.ofScopeOnly (ScopeUnreadable reason))
        Verdict.NoReading
        Map.empty
        IpcParsing.DaemonEvidence.NotServed
        []
        settledTree
        (CheckVerdict.CheckOutcome.Incomplete -1)
        (Some reason)

/// `publishTerminalIncompleteForInvocation` for a publish that no CLI bracket wraps.
let internal publishTerminalIncomplete
    (repoRoot: string)
    (excludePatterns: string list)
    (checkMode: CheckVerdict.CheckMode)
    (reason: string)
    (settledTree: SettledTree)
    : int =
    publishTerminalIncompleteForInvocation
        (Verdict.Invocation.start ())
        repoRoot
        excludePatterns
        checkMode
        reason
        settledTree

/// Poll daemon status, render live progress, then decide a converge-then-verdict
/// outcome and return its exit code (0 = complete & clean, 1 = failures found,
/// 2 = completeness unachievable, 3 = `confirm` with an unearned scope).
/// `renderStatuses` is injected so callers choose the progress renderer
/// (compact/verbose). `triggerScan` forces a fresh scan and is invoked only on
/// the convergence path (incomplete coverage, no failures).
///
/// Every terminal path — clean, red, incomplete, wedged plugin, daemon teardown —
/// publishes a verdict file, so the machine-readable answer exists on the failures
/// too, not only the greens. The sole exception is an unearned no-test convergence
/// over a tree already covered by an applicable full-suite green: preserving that
/// existing evidence is more honest than replacing it with an absence of new evidence.
let pollAndRenderForInvocation
    // AUTOMATION-555. The invocation every verdict this drive publishes belongs to.
    (invocation: Verdict.Invocation)
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
    // AUTOMATION-259. What `check`'s impact selection WOULD have reached in the run this
    // `confirm` did not have to escalate. Read ONCE, at publish time, so a convergence
    // re-scan that provokes another run cannot leave the verdict holding an earlier run's
    // projection — and checked against the run id either way.
    (getCheckReach: unit -> IpcParsing.CheckReachReading)
    // Run EVERY configured test project, now, and don't come back until it is done
    // (`run-tests` with no filter). Invoked ONLY in `Confirmation`, and only when the
    // settled scope is not already full-suite — see the "CONFIRM EARNS ITS EVIDENCE"
    // block below.
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

    // AUTOMATION-167. The tree as it was when the daemon last said it had finished.
    // Re-captured at EVERY settle (the first one, the forced-full-suite one, and each
    // convergence re-settle), so it always names the tree the verdict below actually
    // rests on. `NeverSettled` until the first one returns — which is the state the
    // abort handlers publish from, and it is a fact about them, not a missing value.
    let settledTree = ref NeverSettled

    // Settle the host through its AUTHORITATIVE verdict (`WaitForComplete` →
    // `waitForVerdict`), rendering live status while it blocks. `WaitForComplete` runs on
    // a background task; the render loop terminates only when THAT task finishes, never
    // on the Idle-tolerant `isAllTerminal` predicate — see `pollUntilSettled`.
    let settle () : unit =
        let completeTask =
            System.Threading.Tasks.Task.Run(fun () -> waitForComplete () |> ignore)

        pollUntilSettled renderStatuses getStatus (fun () -> completeTask.IsCompleted)
        // Surface a fault (daemon shutdown / IPC error) rather than swallowing it
        // behind a vacuous clean — re-raises the original RPC exception.
        completeTask.GetAwaiter().GetResult()
        // HERE, and not one line later: `getErrors`, `getTestRun` and the render below
        // all run against a live tree, so a hash taken after them is a hash of whatever
        // the tree became, not of what the daemon just verified.
        settledTree.Value <- SettledTree.capture repoRoot excludePatterns

    // The LAST state the verdict was computed from. Captured at every read (the first
    // one and each convergence re-read) so the file records what the final verdict was
    // actually based on — never an earlier snapshot, and never a second query that could
    // see a different daemon.
    let finalStatuses = ref Map.empty

    // AUTOMATION-555 (rework). The daemon's phase ledger, captured from the SAME
    // response as the statuses at every read, so the verdict places the daemon's
    // phases from the reading it was computed from.
    let finalEvidence = ref IpcParsing.DaemonEvidence.NotServed

    // AUTOMATION-303. Captured with the statuses, at every read, from the SAME response
    // the failing count comes from — so the verdict names the entries its own exit code
    // was computed from rather than a second query's.
    let finalCauses: Verdict.RedCause list ref = ref []

    // The placeholder before anything has been ASKED. It reaches `publishVerdict` only
    // on the abort paths below (a wedged plugin, a daemon that shut down mid-wait), and
    // on those paths it must not say "the daemon reported no scope" — nobody asked it.
    // A placeholder that reads as an answer is the same mistake one layer down.
    let finalRun =
        ref (TestRunReport.ofScopeOnly (ScopeUnreadable "the check aborted before the test scope could be read"))

    let retainedTestRun: RetainedTestRun option ref = ref None

    // AUTOMATION-533. What the daemon had ALREADY completed before this check began.
    // Read HERE, before the scan provokes anything, because it is the only moment at
    // which "not this check's" is knowable: afterwards the ledger holds both. One
    // read-only round trip, on a transport that answers even mid-run.
    let baselineRuns =
        try
            Some(getTestRun().SessionRuns |> Set.ofList)
        with _ ->
            // A baseline that could not be taken must not read as an empty one — see
            // `attribute`. And it may not fail the check: this is a reporting input, and
            // the whole point of the verdict file is that it exists on the bad paths too.
            None

    // Every run this check has provoked so far, oldest first. Folded at every reading
    // rather than derived at the end: a check that aborts still publishes a verdict, and
    // it must name the batches it had already run by then.
    let checkRuns: Guid list ref = ref []

    let observeTestRun (run: TestRunReport) : TestRunReport =
        checkRuns.Value <- TestRunEvidence.attribute baselineRuns checkRuns.Value run

        let effective, retained =
            TestRunEvidence.reconcile settledTree.Value run retainedTestRun.Value

        retainedTestRun.Value <- retained
        // Attribution is a fact about the CHECK, so it rides on whichever report the
        // reconciliation kept — including a retained earlier full-suite run, whose own
        // batch the verdict names through `runId`.
        let effective =
            { effective with
                CheckRuns = checkRuns.Value }

        finalRun.Value <- effective
        effective

    // AUTOMATION-259. Set once, at the escalation below, and read again on every publish
    // path INCLUDING the abort handlers: a `confirm` that escalated and then lost its
    // daemon is precisely the "the escalated run never completed" sample, and dropping the
    // record there would make it indistinguishable from a confirm that never escalated.
    let impactScoped: Verdict.ImpactScopedRun option ref = ref None

    try
        withProgress "Scanning" "Scanning..." (fun () -> waitForScan () |> ignore)

        settle ()

        // First read: diagnostics + coverage after the daemon has settled.
        let firstRaw = getErrors ()
        let firstResp = parseDiagnosticsResponse firstRaw
        let firstOutput = formatDiagnosticsResponse mode renderStatuses firstResp
        eprintfn "%s" firstOutput
        finalStatuses.Value <- firstResp.Statuses
        finalEvidence.Value <- IpcParsing.DaemonEvidence.parse firstRaw
        finalCauses.Value <- redCausesOf noWarnFail firstResp

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
            let raw = getErrors ()
            let resp = parseDiagnosticsResponse raw
            let output = formatDiagnosticsResponse mode renderStatuses resp
            eprintfn "%s" output
            let run = getTestRun () |> observeTestRun
            finalStatuses.Value <- resp.Statuses
            finalEvidence.Value <- IpcParsing.DaemonEvidence.parse raw
            finalCauses.Value <- redCausesOf noWarnFail resp
            checkInputs noWarnFail run.Scope resp

        let firstRun = getTestRun () |> observeTestRun

        // CONFIRM EARNS ITS EVIDENCE.
        //
        // `set-scope full` was already sent (before the scan), so any run the scan
        // provoked is unfiltered — and on a cold daemon that is the whole story: the
        // scope reads `FullSuite` here and nothing more is run. But a WARM daemon whose
        // impact DB says nothing changed provokes NO run at all, and the scope we just
        // read is the last (filtered) run's. Refusing there is correct — there is no
        // whole-suite evidence — but refusing and STOPPING makes `confirm` unsatisfiable.
        //
        // So: no full-suite evidence ⇒ go and produce some. Then re-settle and re-read,
        // because a forced run can fail, and its failures are the answer.
        // The reading `confirm` is about to throw away. Hoisted out of the `else` branch
        // below because BOTH branches need it now: one grades it, the other escalates past
        // it — and AUTOMATION-259 records what it said either way.
        let preEscalation = checkInputs noWarnFail firstRun.Scope firstResp

        let initialRead =
            if CheckVerdict.confirmNeedsFullRun checkMode firstRun.Scope then
                // AUTOMATION-259. Captured BEFORE the forced run, from state that already
                // exists: same tree, same daemon, same scan generation, same instant as the
                // verdict below. Two separate `check`/`confirm` invocations cannot produce
                // that pair — the tree moves in between.
                impactScoped.Value <- Some(Verdict.impactScopedRun repoRoot firstRun preEscalation)

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
                preEscalation

        let outcome =
            CheckVerdict.converge checkMode MaxConvergeAttempts rescan reread initialRead

        // AUTOMATION-259. The sample this run can offer. An escalation produced an
        // EXECUTED reading; the non-escalating `confirm` — the common case in CI, and the
        // one that produced ten days of `no-impact-scoped-run` and not one comparison —
        // offers the PROJECTION instead. `check` offers nothing: it never escalates and
        // never compares, so there is nothing to ask the daemon for.
        let checkScoped =
            match impactScoped.Value, checkMode with
            | Some reading, _ -> Verdict.ExecutedReading(reading, getCheckReach ())
            | None, CheckVerdict.Confirmation -> Verdict.ProjectedThrough(getCheckReach ())
            | None, CheckVerdict.InnerLoop -> Verdict.NoReading

        // AUTOMATION-167. The exit code is the one `publishVerdict` WROTE, not a second
        // computation from `outcome`. They differ exactly when the tree moved during
        // the check: the file recorded `incomplete`/2 while this returned 0, so CI —
        // the only consumer that gates on the exit code — read that as a pass.
        let publishedExitCode =
            publishVerdictForInvocation
                invocation
                repoRoot
                excludePatterns
                checkMode
                noWarnFail
                finalRun.Value
                checkScoped
                finalStatuses.Value
                finalEvidence.Value
                finalCauses.Value
                settledTree.Value
                outcome

        // `Verdict.CheckProse.explainOutcome`, not a local match: the daemon-less path
        // (`RunOnceCheck`) prints the very same call, so whether a daemon served the check
        // is not something the explanation can vary on. `Some` only where there is
        // something to say — `Clean` and `FailuresFound` are already explained by the
        // plugin lines and the red causes above. `MaxConvergeAttempts` is what this path
        // has and `--run-once` does not: it converges, so its "incomplete" can say how
        // many re-scans it spent.
        match Verdict.CheckProse.explainOutcome (Some MaxConvergeAttempts) outcome with
        | Some explanation -> UI.fail explanation
        | None -> ()

        publishedExitCode
    with
    | ex when FsHotWatch.Daemon.isTotalDiscoveryFailureMessage ex.Message ->
        // StreamJsonRpc preserves the message but not the concrete ConfigError
        // type. This is the daemon-backed twin of RunOnceCheck's early terminal:
        // no convergence, no forced suite, and no stale green left on disk.
        let reason = ex.Message

        let exitCode =
            publishTerminalIncompleteForInvocation
                invocation
                repoRoot
                excludePatterns
                checkMode
                reason
                settledTree.Value

        UI.fail reason
        exitCode
    // AUTOMATION-747. Memory exhaustion — here or in the daemon — AFTER the run
    // settled.
    //
    // The guard is the settle, not the fault: `settledTree` is `VerifiedTree` only once
    // `WaitForComplete` has returned, which is the daemon saying it built, ran and
    // committed. So this arm is reachable only when the work is DONE and the answer was
    // lost carrying it back, which is the one thing that distinguishes this from a
    // refusal. The same fault BEFORE the settle keeps its old exit 2: nothing had
    // completed, and claiming otherwise would be the same lie in the other direction.
    //
    // It publishes, like every other terminal here. Without a publish the verdict on
    // disk stays whatever the LAST run left — in the incident that produced this ticket,
    // a refusal stub for a different tree — and seven finished runs in a row read back
    // as that stub.
    | ex when
        isMemoryExhaustion ex
        && (match settledTree.Value with
            | VerifiedTree _ -> true
            | NeverSettled -> false)
        ->
        let reason = ex.Message

        let abortExitCode =
            publishVerdictForInvocation
                invocation
                repoRoot
                excludePatterns
                checkMode
                noWarnFail
                finalRun.Value
                // Same reasoning as the two aborts below: an escalation's EXECUTED
                // reading is already in hand; asking the daemon for a fresh projection
                // on a path that just failed for want of memory is the last thing to do.
                (match impactScoped.Value with
                 | Some reading ->
                     Verdict.ExecutedReading(
                         reading,
                         IpcParsing.ReachUnavailable "the check ran out of memory before recall could be read"
                     )
                 | None -> Verdict.NoReading)
                finalStatuses.Value
                finalEvidence.Value
                finalCauses.Value
                settledTree.Value
                (CheckVerdict.CheckOutcome.ResultUnreceived reason)

        UI.fail (Verdict.CheckProse.resultUnreceived reason)
        abortExitCode
    | ex when isVerdictWaitTimeout ex ->
        // The daemon's hard verdict deadline fired: a plugin overran the bound and is
        // most likely wedged. The remote message names the plugin and its elapsed time
        // (e.g. "still running: test-prune (1h 0m)"), so it is surfaced verbatim plus
        // the recovery path.
        // AUTOMATION-167: return the code the verdict FILE records, not a literal.
        let abortExitCode =
            publishVerdictForInvocation
                invocation
                repoRoot
                excludePatterns
                checkMode
                noWarnFail
                finalRun.Value
                // The daemon is gone or wedged — asking it for a projection would hang or
                // throw. An escalation's EXECUTED reading is already in hand and is
                // exactly the "the escalated run never completed" sample; without one
                // there is nothing to offer, and the verdict says so.
                (match impactScoped.Value with
                 | Some reading ->
                     Verdict.ExecutedReading(
                         reading,
                         IpcParsing.ReachUnavailable "the escalated confirm aborted before recall could be read"
                     )
                 | None -> Verdict.NoReading)
                finalStatuses.Value
                finalEvidence.Value
                finalCauses.Value
                settledTree.Value
                (CheckVerdict.CheckOutcome.Incomplete -1)

        UI.fail
            $"Check aborted: %s{ex.Message}\nA plugin overran the verdict deadline and is likely wedged — inspect logs/daemon.log, then `fshw stop` to reclaim the daemon. If the suite legitimately needs longer, raise FSHW_VERDICT_DEADLINE_SEC."

        abortExitCode
    | ex when isDaemonShutdownDuringWait ex ->
        // AUTOMATION-167: return the code the verdict FILE records, not a literal.
        let abortExitCode =
            publishVerdictForInvocation
                invocation
                repoRoot
                excludePatterns
                checkMode
                noWarnFail
                finalRun.Value
                // The daemon is gone or wedged — asking it for a projection would hang or
                // throw. An escalation's EXECUTED reading is already in hand and is
                // exactly the "the escalated run never completed" sample; without one
                // there is nothing to offer, and the verdict says so.
                (match impactScoped.Value with
                 | Some reading ->
                     Verdict.ExecutedReading(
                         reading,
                         IpcParsing.ReachUnavailable "the escalated confirm aborted before recall could be read"
                     )
                 | None -> Verdict.NoReading)
                finalStatuses.Value
                finalEvidence.Value
                finalCauses.Value
                settledTree.Value
                (CheckVerdict.CheckOutcome.Incomplete -1)

        UI.fail
            "Check aborted: the daemon shut down before producing a verdict — nothing was verified. Re-run `fshw check` (the next command auto-restarts the daemon)."

        abortExitCode

/// `pollAndRenderForInvocation` for a drive that no CLI bracket wraps — tests and
/// embedders. Production check/confirm paths always pass their invocation through.
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
    (getCheckReach: unit -> IpcParsing.CheckReachReading)
    (forceFullRun: unit -> unit)
    (triggerScan: unit -> string)
    : int =
    pollAndRenderForInvocation
        (Verdict.Invocation.start ())
        mode
        checkMode
        repoRoot
        excludePatterns
        renderStatuses
        noWarnFail
        waitForScan
        waitForComplete
        getStatus
        getErrors
        getTestRun
        getCheckReach
        forceFullRun
        triggerScan
