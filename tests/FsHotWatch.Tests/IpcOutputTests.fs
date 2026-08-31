module FsHotWatch.Tests.IpcOutputTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcOutput

let private evidenceTree hash =
    VerifiedTree
        { FsHotWatch.TreeHash.Hash = hash
          FileCount = 1
          SkippedCount = 0
          DeclaredCount = 0
          AbsentDeclarationCount = 0 }

let private evidenceReport scope runId =
    { TestRunReport.ofScopeOnly scope with
        RunId = runId
        Seeds = [ "src/Changed.fs" ]
        SeedCount = 1 }

let private executedA =
    evidenceReport (ImpactFiltered(2, 4)) (Some(System.Guid.Parse "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))

let private executedB =
    evidenceReport (FullSuite 4) (Some(System.Guid.Parse "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))

[<Fact>]
let ``same-tree already-verified retains the executed report atomically`` () =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None

    let effective, retainedAfterQuiet =
        TestRunEvidence.reconcile tree (TestRunReport.ofScopeOnly (NoTestsRun NoTestsReason.AlreadyVerified)) retained

    test <@ effective = executedA @>
    test <@ retainedAfterQuiet = retained @>

[<Fact>]
let ``a genuinely zero-test command remains no evidence`` () =
    let current = TestRunReport.ofScopeOnly (NoTestsRun NoTestsReason.AlreadyVerified)

    let effective, retained =
        TestRunEvidence.reconcile (evidenceTree "sha256:zero") current None

    test <@ effective = current @>
    test <@ retained = None @>

[<Theory>]
[<InlineData(0, 3)>]
[<InlineData(-1, 3)>]
[<InlineData(4, 3)>]
let ``impossible filtered project counts are not retainable executed evidence`` ran total =
    let report =
        evidenceReport (ImpactFiltered(ran, total)) (Some(System.Guid.Parse "cccccccc-cccc-cccc-cccc-cccccccccccc"))

    let effective, retained =
        TestRunEvidence.reconcile (evidenceTree "sha256:invalid-filtered") report None

    test <@ effective = report @>
    test <@ retained = None @>

[<Fact>]
let ``positive bounded filtered project counts are retainable executed evidence`` () =
    let effective, retained =
        TestRunEvidence.reconcile (evidenceTree "sha256:valid-filtered") executedA None

    test <@ effective = executedA @>

    match retained with
    | Some evidence -> test <@ evidence.Report = executedA @>
    | None -> failwith "valid filtered evidence was not retained"

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``an executed-looking scope without a run id is not retainable evidence`` (fullSuite: bool) =
    let scope = if fullSuite then FullSuite 4 else ImpactFiltered(2, 4)

    let scopeOnly = TestRunReport.ofScopeOnly scope

    let effective, retained =
        TestRunEvidence.reconcile (evidenceTree "sha256:no-run") scopeOnly None

    test <@ effective = scopeOnly @>
    test <@ retained = None @>

[<Fact>]
let ``already-verified on a changed tree cannot reuse executed evidence`` () =
    let _, retained = TestRunEvidence.reconcile (evidenceTree "sha256:a") executedA None
    let current = TestRunReport.ofScopeOnly (NoTestsRun NoTestsReason.AlreadyVerified)

    let effective, retainedAfterMove =
        TestRunEvidence.reconcile (evidenceTree "sha256:b") current retained

    test <@ effective = current @>
    test <@ retainedAfterMove = None @>

[<Theory>]
[<InlineData("changes-uncovered")>]
[<InlineData("unstated")>]
[<InlineData("unknown-reason")>]
[<InlineData("unreadable")>]
let ``only already-verified can reuse prior evidence`` (kind: string) =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None

    let scope =
        match kind with
        | "changes-uncovered" -> NoTestsRun(NoTestsReason.ChangesUncovered([ "M.f" ], 1))
        | "unstated" -> NoTestsRun NoTestsReason.Unstated
        | "unknown-reason" -> NoTestsRun(NoTestsReason.UnknownReason "future")
        | _ -> ScopeUnreadable "broken reply"

    let current = TestRunReport.ofScopeOnly scope

    let effective, retainedAfterRefusal =
        TestRunEvidence.reconcile tree current retained

    test <@ effective = current @>
    test <@ retainedAfterRefusal = None @>

[<Fact>]
let ``a later executed report wholly replaces the prior report`` () =
    let tree = evidenceTree "sha256:same"
    let _, retainedA = TestRunEvidence.reconcile tree executedA None
    let effective, retainedB = TestRunEvidence.reconcile tree executedB retainedA
    test <@ effective = executedB @>

    match retainedB with
    | Some evidence -> test <@ evidence.Report = executedB @>
    | None -> failwith "the later executed report must itself be retained"

[<Fact>]
let ``retained test evidence cannot hide a later plugin failure`` () =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None

    let effective, _ =
        TestRunEvidence.reconcile tree (TestRunReport.ofScopeOnly (NoTestsRun NoTestsReason.AlreadyVerified)) retained

    let inputs: CheckVerdict.CheckInputs =
        { PluginStatuses =
            Map.ofList
                [ "lint",
                  { Status = StatusView.Failed("late failure", System.DateTime.UtcNow)
                    Subtasks = []
                    ActivityTail = []
                    LastRun = None
                    Diagnostics = DiagnosticCounts.empty } ]
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = CheckVerdict.RunnerAbort.NoAbort
          Coverage = Complete
          Scope = effective.Scope }

    let outcome = CheckVerdict.verdict CheckVerdict.InnerLoop inputs
    test <@ outcome = CheckVerdict.CheckOutcome.FailuresFound @>
    test <@ CheckVerdict.exitCode outcome = 1 @>

let private writeEvidenceSuite (repoRoot: string) (runId: System.Guid) =
    let runDir = FsHotWatch.Ctrf.runDir repoRoot runId
    System.IO.Directory.CreateDirectory(runDir) |> ignore

    System.IO.File.WriteAllText(
        System.IO.Path.Combine(runDir, "A.Tests" + FsHotWatch.Ctrf.ReportSuffix),
        """{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"a","results":{"tool":{"name":"xUnit.net v3"},"summary":{"tests":3,"passed":3,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1,"start":1,"stop":2},"tests":[]}}"""
    )

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``daemon command retains executed evidence across a same-tree quiet convergence read`` (failSecondRead: bool) =
    TestHelpers.withTempDir "ipcoutput-retained-command" (fun repoRoot ->
        let runId = executedA.RunId.Value
        writeEvidenceSuite repoRoot runId
        let mutable errorReads = 0
        let mutable scopeReads = 0

        let getErrors () =
            errorReads <- errorReads + 1

            if errorReads = 1 then
                """{"count":0,"files":{},"statuses":{},"unchecked":1}"""
            elif failSecondRead then
                """{"count":0,"files":{},"statuses":{"lint":{"status":{"tag":"failed","error":"late failure","at":"2026-08-31T12:00:00Z"},"subtasks":[],"activityTail":[],"lastRun":null}},"unchecked":0}"""
            else
                """{"count":0,"files":{},"statuses":{},"unchecked":0}"""

        let getTestRun () =
            scopeReads <- scopeReads + 1

            if scopeReads = 1 then
                executedA
            else
                TestRunReport.ofScopeOnly (NoTestsRun NoTestsReason.AlreadyVerified)

        let exitCode =
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle")
                (fun () -> "idle")
                (fun () -> "{}")
                getErrors
                getTestRun
                (fun () -> IpcParsing.ReachUnavailable "not used")
                ignore
                (fun () -> "idle")

        test <@ exitCode = (if failSecondRead then 1 else 0) @>

        match Verdict.read repoRoot with
        | Verdict.Reading.Found verdict ->
            test <@ verdict.RunId = Some runId @>
            test <@ verdict.Scope = executedA.Scope @>
            test <@ verdict.Suites |> List.map (fun suite -> suite.Project) = [ "A.Tests" ] @>
        | other -> failwithf "expected a published command verdict, got %A" other)

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse extracts count`` () =
    let json = """{"count":2,"files":{},"statuses":{}}"""
    let result = parseDiagnosticsResponse json
    test <@ result.Count = 2 @>

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse extracts files with entries`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    test <@ result.Files.ContainsKey("src/Foo.fs") @>
    let entries = result.Files["src/Foo.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries[0].Plugin = "lint" @>
    test <@ entries[0].Message = "bad name" @>
    test <@ entries[0].Severity = Warning @>
    test <@ entries[0].Line = 17 @>

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse extracts statuses`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null},"lint":{"status":{"tag":"idle"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let result = parseDiagnosticsResponse json
    test <@ result.Statuses.ContainsKey("build") @>

    match result.Statuses["build"].Status with
    | StatusView.Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

// ---------------------------------------------------------------------------
// AN UNREADABLE PLUGIN STATUS IS NOT A PASSING ONE.
//
// `Verdict.read` states the policy one file away: an outcome token this build does not
// recognize becomes `Fail`, never `Ok`. The IPC parser did the opposite — four places
// rounded an unparseable status DOWN to `Idle`: quiescent, no failure, omitted from the
// verdict entirely. A live cross-version hazard, since `PluginOutcome` gained `Wedged`.
//
// Asserted through `hasFailures` — the one predicate both `check` and `confirm` decide on —
// so these pin the CONSEQUENCE, not the representation.
// ---------------------------------------------------------------------------

let private responseWithStatus (statusJson: string) =
    parseDiagnosticsResponse (
        """{"count":0,"files":{},"unchecked":0,"statuses":{"mystery":{"status":"""
        + statusJson
        + ""","subtasks":[],"activityTail":[],"lastRun":null}}}"""
    )

[<Fact(Timeout = 15000)>]
let ``a status tag this build does not recognize is a FAILURE, not idle`` () =
    // Rounding a newer daemon's unknown state to `Idle` makes the plugin quiescent, clean,
    // and INVISIBLE in the verdict's `plugins[]`.
    let resp = responseWithStatus """{"tag":"quantum-superposition"}"""
    test <@ hasFailures false resp @>
    test <@ exitCodeFromResponse false resp = 1 @>

[<Fact(Timeout = 15000)>]
let ``a running status whose since cannot be parsed is a FAILURE, not idle`` () =
    // This silently defeated all of AUTOMATION-147's wedge detection: the classifier only
    // fires on `StatusView.Running since`, so an unparseable `since` turned a WEDGED plugin
    // into an idle one.
    let resp = responseWithStatus """{"tag":"running","since":"not-a-timestamp"}"""
    test <@ hasFailures false resp @>

[<Fact(Timeout = 15000)>]
let ``a status that is not even an object is a FAILURE, not idle`` () =
    let resp = responseWithStatus "\"running\""
    test <@ hasFailures false resp @>

[<Fact(Timeout = 15000)>]
let ``a plugin element with NO status field is a FAILURE, not idle`` () =
    let resp =
        parseDiagnosticsResponse
            """{"count":0,"files":{},"unchecked":0,"statuses":{"mystery":{"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    test <@ hasFailures false resp @>

[<Fact(Timeout = 15000)>]
let ``a recognized idle status is still idle — the fail-closed rule does not swallow the healthy case`` () =
    let resp = responseWithStatus """{"tag":"idle"}"""
    test <@ not (hasFailures false resp) @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse with no errors shows clean message`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse with errors shows file and message`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":0,"detail":null}]},"statuses":{"lint":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("src/Foo.fs") @>
    test <@ output.Contains("[lint]") @>
    test <@ output.Contains("L17") @>
    test <@ output.Contains("bad name") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse with errors shows count summary`` () =
    let json =
        """{"count":2,"files":{"src/A.fs":[{"plugin":"lint","message":"x","severity":"warning","line":1,"column":0,"detail":null}],"src/B.fs":[{"plugin":"build","message":"y","severity":"error","line":2,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("1 error(s), 1 warning(s) in 2 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse returns 0 for count 0`` () =
    let resp =
        { Count = 0
          Files = Map.empty
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 0 @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse returns 1 for errors`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "fcs"
                      Message = "bad"
                      Severity = DiagnosticSeverity.Error
                      Line = 1
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 1 @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse with noWarnFail ignores warnings`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "lint"
                      Message = "bad"
                      Severity = DiagnosticSeverity.Warning
                      Line = 1
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse true resp = 0 @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse without noWarnFail fails on warnings`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "lint"
                      Message = "bad"
                      Severity = DiagnosticSeverity.Warning
                      Line = 1
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 1 @>

// --- renderIpcResult tests ---

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with GetDiagnostics format count 0 returns 0`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"count":0,"files":{},"statuses":{}}"""

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with GetDiagnostics format count > 0 returns 1`` () =
    let result =
        renderIpcResult
            ProgressRenderer.Verbose
            (fun _ -> [])
            false
            """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with status passed returns 0`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"status":"passed"}"""

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with status failed returns 1`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"status":"failed"}"""

    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with error field returns 1`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"error":"something went wrong"}"""

    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with plain text returns 0`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false "build completed successfully"

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with test results JSON containing arrays does not crash`` () =
    let json =
        """{"elapsed":"1.5s","projects":[{"project":"TestProject","status":"passed","output":"ok"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with test results JSON with failed project returns 1`` () =
    let json =
        """{"elapsed":"2.0s","projects":[{"project":"FailProject","status":"failed","output":"FAIL: test1"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with noTestsMatched run returns 3 — refuses to green without evidence`` () =
    // Exit 3 is this tool's "refuse to green without evidence" code. The trap: this
    // assertion once demanded 0, so a filter matching zero tests printed "✓ Tests passed"
    // and exited 0 — an exit code that sails through `&&` is not a distinct outcome no
    // matter what the text beside it says.
    let json =
        """{"elapsed":"0.1s","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with an EMPTY projects array returns 3, never "Tests passed"`` () =
    // The case `noTestsMatched` cannot express: `allZeroMatch` is deliberately false for an
    // empty result set ("no project ran" ≠ "every project matched nothing"), so this JSON
    // sets neither flag and used to fall through to `UI.success "Tests passed"`, exit 0.
    let json = """{"elapsed":"0.0s","projects":[]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult still returns 0 for a genuine pass — the guard is not a blanket`` () =
    // Positive control for the two assertions above: without it, both would still pass if
    // `renderIpcResult` simply never returned 0.
    let json =
        """{"elapsed":"1.2s","projects":[{"project":"RealProject","status":"passed","output":"Passed! total: 7"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with busy status NEVER returns 0 — no run, no green`` () =
    // AUTOMATION-99: `test-rerun` is the "prove it ran" verb, so a reply carrying NO run
    // result must never exit 0. The message still distinguishes it from "Tests failed"
    // (nothing is known to be broken), but the exit code is non-zero either way.
    let json =
        """{"status":"busy","message":"the test run did not produce a result within 600s (still queued or running); retry, or raise --wait-sec"}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse hides info-severity entries`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"fcs","message":"XML comment is not placed on a valid language element.","severity":"info","line":3,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ not (output.Contains("XML comment")) @>
    test <@ output.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse shows warnings but hides info in same file`` () =
    let json =
        """{"count":2,"files":{"src/Foo.fs":[{"plugin":"fcs","message":"XML comment","severity":"info","line":3,"column":0,"detail":null},{"plugin":"format-check","message":"File is not formatted","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("File is not formatted") @>
    test <@ not (output.Contains("XML comment")) @>
    test <@ output.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse excludes info-only files from count`` () =
    let json =
        """{"count":2,"files":{"src/A.fs":[{"plugin":"fcs","message":"XML comment","severity":"info","line":3,"column":0,"detail":null}],"src/B.fs":[{"plugin":"lint","message":"bad","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse ignores info-severity entries`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "fcs"
                      Message = "XML comment"
                      Severity = DiagnosticSeverity.Info
                      Line = 3
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 0 @>

// --- Regression: parsePluginStatuses format drift ---
//
// If parsePluginStatuses rejects the GetStatus JSON shape, the parse silently yields an
// empty map — which once hung a status-polling consumer for 40+ minutes before being
// caught. These pin the accepted vs rejected wire shapes.

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses rejects bare-string values and returns empty`` () =
    let json = """{"plugin": "Completed at 2026-01-01T00:00:00Z"}"""
    test <@ parsePluginStatuses json = Ok Map.empty @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses accepts object-valued entries with status field`` () =
    let json =
        """{"plugin": {"status": {"tag": "completed", "at": "2026-01-01T00:00:00Z"}, "subtasks": [], "activityTail": [], "lastRun": null}}"""

    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json
    test <@ Map.containsKey "plugin" parsed @>

// --- Regression: check soundness (false green before the test-prune verdict) ---
//
// The scan signals its generation as soon as FCS check + BatchChecked finish; at that
// instant test-prune can still be Idle (its queued `BuildCompleted` has not been handled,
// so it has not transitioned Idle->Running). `isAllTerminal` treats Idle as quiescent and
// never consults the host's inflight/busy state, so `check` concluded "settled", read
// diagnostics during that Idle window, and exited 0 while real test failures were still
// pending. `pollAndRender` now blocks on `WaitForComplete` before reading diagnostics;
// status polling is rendering-only.
//
// The seams below reproduce the exact ordering deterministically — no real daemon, no
// sleep race — so `exit = 1` is red-before / green-after.

/// GetStatus JSON: fcs Completed; test-prune Idle until the run has finished, then
/// Completed. The Idle window is the false-green trap.
let private statusJsonFor (testRunFinished: bool) : string =
    let testPruneStatus =
        if testRunFinished then
            """{"tag":"completed","at":"2026-01-01T00:00:00.0000000Z"}"""
        else
            """{"tag":"idle"}"""

    $"""{{"fcs":{{"status":{{"tag":"completed","at":"2026-01-01T00:00:00.0000000Z"}},"subtasks":[],"activityTail":[],"lastRun":null}},"test-prune":{{"status":%s{testPruneStatus},"subtasks":[],"activityTail":[],"lastRun":null}}}}"""

/// GetDiagnostics JSON: complete coverage (unchecked 0). The one test-prune failure becomes
/// visible ONLY after the run finished — before that the ledger is deceptively clean,
/// exactly as during the Idle race window.
let private diagnosticsJsonFor (testRunFinished: bool) : string =
    if testRunFinished then
        """{"count":1,"files":{"tests/Foo.fs":[{"plugin":"test-prune","message":"1 test failed","severity":"error","line":0,"column":0,"detail":null}]},"statuses":{},"unchecked":0}"""
    else
        """{"count":0,"files":{},"statuses":{},"unchecked":0}"""

[<Fact(Timeout = 15000)>]
let ``pollAndRender waits for the test-prune verdict before deciding (no false green while test-prune is Idle)`` () =
    // The test-prune run's terminal state, flipped true ONLY by the authoritative wait —
    // mirroring `waitForVerdict` blocking until the BuildCompleted -> run reaches terminal.
    let mutable testRunFinished = false
    let mutable waitForCompleteCalls = 0

    // Determinism gate: `pollUntilSettled` runs `waitForComplete` on a `Task.Run` and exits
    // as soon as it observes `IsCompleted`. On an idle machine that can happen before the
    // FIRST `isSettled` poll, so the `if not allDone then Thread.Sleep(200)` arm flips
    // covered/uncovered run-to-run. Releasing this gate on the SECOND `getStatus` (not the
    // first) keeps the task un-finished through the first `isSettled` check, so the loop
    // always takes the wait-and-retry branch once, with no wall-clock race.
    use releaseComplete = new System.Threading.ManualResetEventSlim(false)
    let mutable pollCount = 0

    let waitForScan () : string =
        // Generation signalled; test-prune has NOT yet processed its queued
        // BuildCompleted. This is the false-green window.
        "idle"

    let waitForComplete () : string =
        // The sound verdict wait: returns only once the run has reached terminal. Blocks
        // until the render loop has done one full un-settled iteration.
        releaseComplete.Wait()
        waitForCompleteCalls <- waitForCompleteCalls + 1
        testRunFinished <- true
        statusJsonFor true

    let getStatus () : string =
        // Runs at the top of each iteration, before the `isSettled` check: the first poll
        // leaves the gate closed so the loop sleeps; the second releases it.
        pollCount <- pollCount + 1

        if pollCount >= 2 then
            releaseComplete.Set()

        statusJsonFor testRunFinished

    let getErrors () : string = diagnosticsJsonFor testRunFinished
    let triggerScan () : string = "idle"

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                waitForScan
                waitForComplete
                getStatus
                getErrors
                (fun () -> IpcParsing.TestRunReport.ofScopeOnly (IpcParsing.FullSuite 1))
                // AUTOMATION-259: no projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore // forceFullRun: never fires — the scope is already full-suite
                triggerScan)

    // The authoritative settle MUST have been consulted...
    test <@ waitForCompleteCalls >= 1 @>
    // ...and exit 1 only happens if the check waited for the verdict before reading
    // diagnostics. Settling on `isAllTerminal` reads the clean ledger during the Idle
    // window and returns 0 — the false green.
    test <@ exitCode = 1 @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender surfaces a clean verdict once the test-prune run passes`` () =
    // The legitimate "nothing failed" case — guards against the fix over-blocking or
    // mis-reporting a genuinely green run.
    //
    // Determinism gate: this pins the OTHER arm of `pollUntilSettled`'s
    // `if not allDone then Thread.Sleep(200)` branch — the SKIP arm, taken when the verdict
    // task has already completed by the first `isSettled` poll. Blocking the first
    // `getStatus` on `completed` guarantees that ordering instead of racing the `Task.Run`.
    use completed = new System.Threading.ManualResetEventSlim(false)
    let mutable firstStatusPoll = true

    let waitForComplete () : string =
        let r = statusJsonFor true
        completed.Set()
        r

    let getStatus () : string =
        if firstStatusPoll then
            firstStatusPoll <- false
            completed.Wait()

        statusJsonFor true

    let cleanDiagnostics () : string =
        """{"count":0,"files":{},"statuses":{},"unchecked":0}"""

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle")
                waitForComplete
                getStatus
                cleanDiagnostics
                (fun () -> IpcParsing.TestRunReport.ofScopeOnly (IpcParsing.FullSuite 1))
                // AUTOMATION-259: no projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore // forceFullRun: never fires — the scope is already full-suite
                (fun () -> "idle"))

    test <@ exitCode = 0 @>

// --- isDaemonShutdownDuringWait (mid-wait teardown classification) ---
// AUTOMATION-65: a WaitForComplete that faults because the daemon shut down or the pipe
// dropped mid-wait must be recognised, so the check yields a diagnostic verdict (exit 2)
// rather than an opaque crash or a silent connection drop.

/// A stand-in whose type NAME contains "ConnectionLost" — exercises the StreamJsonRpc
/// connection-loss detection without a compile-time dependency on the transport assembly
/// (the production match is by type-name substring).
type private FakeConnectionLostException() =
    inherit exn("connection lost")

[<Fact(Timeout = 15000)>]
let ``isDaemonShutdownDuringWait recognises transport + shutdown faults`` () =
    test <@ isDaemonShutdownDuringWait (System.IO.IOException("pipe is broken")) @>
    test <@ isDaemonShutdownDuringWait (System.IO.EndOfStreamException()) @>
    test <@ isDaemonShutdownDuringWait (System.ObjectDisposedException("pipe")) @>
    test <@ isDaemonShutdownDuringWait (FakeConnectionLostException()) @>
    // The daemon's graceful-shutdown sentinel, propagated back as a remote error.
    test <@ isDaemonShutdownDuringWait (exn ("Error: daemon shutting down")) @>
    // ...and detected through the inner-exception chain.
    test <@ isDaemonShutdownDuringWait (exn ("outer", System.IO.IOException("inner drop"))) @>

[<Fact(Timeout = 15000)>]
let ``isDaemonShutdownDuringWait ignores unrelated faults`` () =
    // A real timeout or an arbitrary bug is NOT a mid-wait teardown.
    test <@ not (isDaemonShutdownDuringWait (System.TimeoutException("WaitForComplete timed out after 00:30:00"))) @>
    test <@ not (isDaemonShutdownDuringWait (exn "boom")) @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender returns exit 2 when the daemon drops mid-wait`` () =
    // End-to-end: a faulting WaitForComplete reaches the diagnostic exit-2 path rather than
    // propagating a crash.
    let waitForComplete () : string =
        raise (System.IO.IOException("pipe is broken"))

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle") // waitForScan
                waitForComplete
                (fun () -> "{}") // getStatus
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""") // getErrors
                (fun () -> IpcParsing.TestRunReport.ofScopeOnly (IpcParsing.FullSuite 1))
                // AUTOMATION-259: no projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore // forceFullRun: never fires — the scope is already full-suite
                (fun () -> "idle")) // triggerScan

    test <@ exitCode = 2 @>

// --- isVerdictWaitTimeout (verdict-deadline breach classification) ---
// The daemon bounds a client-unbounded WaitForComplete (`resolveVerdictDeadline`), and its
// TimeoutException crosses the RPC boundary as a remote error recognisable ONLY by its
// message. The check must turn that into a diagnostic exit 2 naming the wedged plugin —
// never an opaque crash, never an endless wait.

[<Fact(Timeout = 15000)>]
let ``isVerdictWaitTimeout recognises the daemon's deadline breach`` () =
    test
        <@
            isVerdictWaitTimeout (
                System.TimeoutException("WaitForComplete timed out after 01:00:00 — still running: test-prune (1h 0m)")
            )
        @>

    // ...as a remote-invocation error (arbitrary exception type, message-borne) ...
    test <@ isVerdictWaitTimeout (exn "Error: WaitForComplete timed out after 01:00:00 — still running: x") @>
    // ...and through the inner-exception chain.
    test <@ isVerdictWaitTimeout (exn ("outer", exn "WaitForComplete timed out after 00:30:00 — y")) @>

[<Fact(Timeout = 15000)>]
let ``isVerdictWaitTimeout ignores unrelated faults`` () =
    test <@ not (isVerdictWaitTimeout (exn "boom")) @>
    test <@ not (isVerdictWaitTimeout (System.IO.IOException("pipe is broken"))) @>
    test <@ not (isVerdictWaitTimeout (System.TimeoutException("some other timeout"))) @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender returns exit 2 when the verdict deadline is breached`` () =
    // End-to-end: the daemon's deadline TimeoutException reaches the diagnostic exit-2 path
    // instead of crashing.
    let waitForComplete () : string =
        raise (System.TimeoutException("WaitForComplete timed out after 01:00:00 — still running: test-prune (1h 0m)"))

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle") // waitForScan
                waitForComplete
                (fun () -> "{}") // getStatus
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""") // getErrors
                (fun () -> IpcParsing.TestRunReport.ofScopeOnly (IpcParsing.FullSuite 1))
                // AUTOMATION-259: no projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore // forceFullRun: never fires — the scope is already full-suite
                (fun () -> "idle")) // triggerScan

    test <@ exitCode = 2 @>

// ---------------------------------------------------------------------------
// AUTOMATION-117 — `confirm` EARNS its evidence.
//
// `set-scope full` makes the next test run unfiltered; it does not make a run HAPPEN. On a
// warm daemon whose impact DB says nothing changed, the scan provokes no run at all, so
// `confirm` read the LAST (filtered) run's coverage and refused — correctly, but with no
// way for the caller to ever produce a satisfying answer. `confirm` now RUNS the suite it
// demands, and only then judges it.
// ---------------------------------------------------------------------------

/// A `pollAndRender` drive whose test scope starts impact-filtered and becomes full-suite
/// only once `forceFullRun` has been invoked — i.e. a warm daemon with nothing to do,
/// reporting the last filtered run until something forces a new one.
let private driveConfirm (checkMode: CheckVerdict.CheckMode) : int * int =
    let mutable forceCalls = 0

    let getTestRun () : TestRunReport =
        if forceCalls > 0 then
            TestRunReport.ofScopeOnly (FullSuite 1)
        else
            TestRunReport.ofScopeOnly (ImpactFiltered(0, 1))

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-confirm-force" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                checkMode
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle") // waitForScan
                (fun () -> "idle") // waitForComplete
                (fun () -> "{}") // getStatus
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""") // getErrors
                getTestRun
                // AUTOMATION-259: no projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                (fun () -> forceCalls <- forceCalls + 1) // forceFullRun
                (fun () -> "idle")) // triggerScan

    exitCode, forceCalls

[<Fact(Timeout = 15000)>]
let ``a confirm with no full-suite evidence FORCES the run and then goes green`` () =
    // Without the force, `confirm` reads the stale ImpactFiltered scope and returns exit 3
    // (UnearnedScope) with no way for the caller to get a different answer.
    let exitCode, forceCalls = driveConfirm CheckVerdict.Confirmation

    test <@ forceCalls = 1 @>
    test <@ exitCode = 0 @>

[<Fact(Timeout = 15000)>]
let ``a confirm that already has full-suite evidence does NOT run the suite twice`` () =
    // The force is a BACKSTOP, not the mechanism. On a cold daemon the scan already provoked
    // the unfiltered run (`set-scope full` was sent first), so the scope reads full-suite
    // here and `confirm` must pay for exactly ONE suite.
    let mutable forceCalls = 0

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-confirm-noforce" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.Confirmation
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle")
                (fun () -> "idle")
                (fun () -> "{}")
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""")
                (fun () -> TestRunReport.ofScopeOnly (FullSuite 1))
                // AUTOMATION-259: no projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                (fun () -> forceCalls <- forceCalls + 1)
                (fun () -> "idle"))

    test <@ forceCalls = 0 @>
    test <@ exitCode = 0 @>

[<Fact(Timeout = 15000)>]
let ``the inner loop NEVER forces a full suite`` () =
    // An impact-filtered green is precisely the answer the inner loop wants.
    let exitCode, forceCalls = driveConfirm CheckVerdict.InnerLoop

    test <@ forceCalls = 0 @>
    test <@ exitCode = 0 @>

/// The same drive, but the VERDICT FILE is read before the temp dir goes away.
///
/// AUTOMATION-259 lives or dies in the wiring: `publishVerdict` will happily classify a
/// `None` it was handed, so a transport that captured nothing at the escalation would
/// record `no-impact-scoped-run` on a confirm that plainly escalated — and every
/// producer-level test would still pass.
/// The run every `driveConfirmForVerdict` report and projection names.
let private driveRunId = System.Guid.Parse("11111111-1111-1111-1111-111111111111")

let private driveConfirmForVerdict
    (checkMode: CheckVerdict.CheckMode)
    (firstScope: TestScope)
    (getCheckReach: unit -> IpcParsing.CheckReachReading)
    : Verdict.Verdict =
    let mutable forceCalls = 0

    // A RUN ID on both sides, because the projection refuses to attach a selection to a
    // run it does not belong to — and a drive whose reports name no run would exercise
    // only that refusal.
    let getTestRun () : TestRunReport =
        if forceCalls > 0 then
            { TestRunReport.ofScopeOnly (FullSuite 1) with
                RunId = Some driveRunId }
        else
            { TestRunReport.ofScopeOnly firstScope with
                RunId = Some driveRunId }

    TestHelpers.withTempDir "ipcoutput-confirm-259" (fun repoRoot ->
        pollAndRender
            ProgressRenderer.Agent
            checkMode
            repoRoot
            []
            (fun _ -> [])
            false
            (fun () -> "idle")
            (fun () -> "idle")
            (fun () -> "{}")
            (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""")
            getTestRun
            getCheckReach
            (fun () -> forceCalls <- forceCalls + 1)
            (fun () -> "idle")
        |> ignore

        match Verdict.read repoRoot with
        | Verdict.Reading.Found v -> v
        | other -> failwithf "the drive must leave a readable verdict, got %A" other)

/// A daemon that has a projection to offer, for the run this drive grades.
let private offering (reach: IpcParsing.CheckReach) (scope: TestScope) () : IpcParsing.CheckReachReading =
    IpcParsing.ReachRecorded
        { RunId = Some driveRunId
          Scope = scope
          Reach = reach
          Recall = IpcParsing.FailureRecallNotMeasurable "test fixture" }

let private offersNothing () : IpcParsing.CheckReachReading =
    IpcParsing.ReachUnavailable "this drive offers no projection"

[<Fact(Timeout = 15000)>]
let ``an escalating confirm records the impact-scoped reading it escalated away from`` () =
    let v =
        driveConfirmForVerdict CheckVerdict.Confirmation (ImpactFiltered(0, 1)) offersNothing

    // Both runs were clean, so this is the ordinary sample the feature exists to collect.
    test <@ v.Divergence = Verdict.Divergence.Agreed @>

    match v.ImpactScopedRun with
    // The scope the daemon reported BEFORE the force — not the full suite the verdict
    // itself rests on.
    | Some pre ->
        test <@ pre.Scope = ImpactFiltered(0, 1) @>
        // It RAN. The projection is a different measurement and must not be able to
        // masquerade as this one.
        test <@ pre.Basis = Verdict.SampleBasis.Executed @>
    | None -> failwith "an escalating confirm must record the reading it escalated away from"

[<Fact(Timeout = 15000)>]
let ``a confirm that did NOT escalate records the PROJECTED sample, not a bare "nothing compared"`` () =
    // AUTOMATION-259's rework. `confirm` widens the scope BEFORE the scan, so the run its
    // own scan provokes is unfiltered and this branch — not the escalating one — is what
    // CI takes every single time. It used to record `no-impact-scoped-run`: a true
    // statement that produced, in ten days and seventeen confirms, zero samples.
    let projected =
        driveConfirmForVerdict
            CheckVerdict.Confirmation
            (FullSuite 1)
            (offering IpcParsing.NoFailuresToReach (ImpactFiltered(0, 1)))

    test <@ projected.Divergence = Verdict.Divergence.Agreed @>

    match projected.ImpactScopedRun with
    | Some pre ->
        test <@ pre.Basis = Verdict.SampleBasis.ProjectedFromFullRun @>
        // The scope `check` WOULD have covered — not the full suite this verdict rests on.
        test <@ pre.Scope = ImpactFiltered(0, 1) @>
    | None -> failwith "a non-escalating confirm must record the projected reading"

[<Fact(Timeout = 15000)>]
let ``a confirm with no projection on offer says nothing was compared, and a check records no comparison at all`` () =
    // The controls, one on each side. Without them, a transport that recorded a reading
    // unconditionally, or one that recorded nothing ever, would still satisfy the two
    // tests above.
    let noSample =
        driveConfirmForVerdict CheckVerdict.Confirmation (FullSuite 1) offersNothing

    // An unavailable projection is a REFUSAL, never an agreement: this is the whole
    // fail-closed direction of the ticket, asserted end to end through the transport.
    match noSample.Divergence with
    | Verdict.Divergence.Incomparable reason -> test <@ reason.Contains "offers no projection" @>
    | other -> failwithf "an unavailable projection must be INCOMPARABLE, got %A" other

    // `check` never escalates, so it never has a comparison to make — confirm-only, and
    // `Verdict.create` refuses a check that claims otherwise.
    let inner =
        driveConfirmForVerdict CheckVerdict.InnerLoop (ImpactFiltered(0, 1)) offersNothing

    test <@ inner.Command = Verdict.Check @>
    test <@ inner.Divergence = Verdict.Divergence.NotRecorded @>

// ---------------------------------------------------------------------------
// NOTE: these payloads carried a `verifiedNothing` boolean that NO producer writes and
// no consumer reads — `formatTestResultsJson` emits `elapsed`/`filter`/`noTestsMatched`/
// `coverage`/`projects`, and the run-level "verified nothing" fact is already carried by
// the `coverage` token (`RunVerification.verifiedNothing` derives it). It was removed
// rather than emitted: a fixture asserting a field the wire does not have vouches for a
// payload that never occurs, and a second serialized source of the same fact is the drift
// this whole ticket family is about (AUTOMATION-278).
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// The `coverage` token is what a CURRENT daemon sends. These cover the other side of the
// contract: a CLI talking to an OLDER daemon that omits it. The rule the fallback holds is
// that an ABSENT field must never read as "ran", or upgrading the CLI ahead of the daemon
// silently reintroduces green-for-nothing.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``renderIpcResult reads the coverage token when the daemon sends one`` () =
    let json = """{"elapsed":"0.0s","coverage":"no-projects-selected","projects":[]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult reads coverage=all-zero-match as a refusal, not a pass`` () =
    let json =
        """{"elapsed":"0.1s","coverage":"all-zero-match","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult trusts a coverage token that ran, over an empty-looking payload`` () =
    // Positive control on the token path: without it, a fallback that ignored the token
    // entirely would still satisfy the two tests above.
    //
    // Both breadths, because the exit code must not depend on scope — a partial run that
    // executed and passed is still a pass (AUTOMATION-282).
    for token in [ "ran-full-suite"; "ran-partial" ] do
        let json =
            $"""{{"elapsed":"1.0s","coverage":"{token}","projects":[{{"project":"P","status":"passed","output":"Passed! total: 3"}}]}}"""

        test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 0 @>

[<Fact(Timeout = 15000)>]
let ``the retired bare "ran" token is refused, not read as a pass`` () =
    // Pre-282 daemons sent `"ran"`: tests executed, breadth unstated. The missing half used
    // to arrive as a separate bool that could claim a full suite for a run that executed
    // nothing, so the token is refused rather than having a scope invented for it — a CLI
    // newer than its daemon gets one exit 3 and instructions to restart it.
    let json =
        """{"elapsed":"1.0s","coverage":"ran","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 3 @>

[<Fact(Timeout = 15000)>]
let ``an OLDER daemon sending no coverage field still cannot report a no-op as a pass`` () =
    // The upgrade-skew case: with no `coverage` and no `noTestsMatched`, the fallback must
    // reconstruct "no project ran" from the counts rather than defaulting to "ran".
    let noCoverageEmpty = """{"elapsed":"0.0s","projects":[]}"""

    let noCoverageZeroMatch =
        """{"elapsed":"0.1s","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let noCoverageReal =
        """{"elapsed":"1.0s","projects":[{"project":"P","status":"passed","output":"Passed! total: 9"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageEmpty = 3 @>
    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageZeroMatch = 3 @>
    // …and the fallback is still not a blanket refusal.
    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageReal = 0 @>

[<Fact(Timeout = 15000)>]
let ``an OLDER daemon reporting a MIXED no-op run is refused, not read as a pass`` () =
    // AUTOMATION-227. The residual vacuous green in the older-daemon fallback. This
    // payload is neither empty nor all-zero-match, so neither guard above catches it —
    // yet NOT ONE project executed a test. It used to fall to `RanPerCounts` → exit 0,
    // printing "2 project(s): 1 matched nothing, 1 did not run" directly above a
    // "✓ Tests passed". The per-project statuses that establish it were already in hand.
    let mixedNoOp =
        """{"elapsed":"0.2s","projects":[{"project":"A","status":"no-tests-matched","output":""},{"project":"B","status":"deferred","output":"apphost not produced"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false mixedNoOp = 3 @>

    // Every all-non-executing combination, not just the one that was reported.
    let deferredAndErrored =
        """{"elapsed":"0.2s","projects":[{"project":"A","status":"errored","output":""},{"project":"B","status":"deferred","output":""}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false deferredAndErrored = 3 @>

    // POSITIVE CONTROL: the refusal is derived from "nothing executed", not from "some
    // project was not `passed`". One project that really ran keeps the run a pass.
    let oneRealPass =
        """{"elapsed":"0.2s","projects":[{"project":"A","status":"passed","output":"Passed! total: 3"},{"project":"B","status":"no-tests-matched","output":""}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false oneRealPass = 0 @>

[<Fact(Timeout = 15000)>]
let ``an UNKNOWN per-project status does not count as having executed`` () =
    // Fail-closed on the daemon-newer-than-CLI direction of the same skew: a status word
    // this build does not know must not be summed into "something ran".
    let unknownStatus =
        """{"elapsed":"0.2s","projects":[{"project":"A","status":"quarantined","output":""}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false unknownStatus = 3 @>

[<Fact(Timeout = 15000)>]
let ``a project KILLED at its timeout is a failure, not a pass`` () =
    // `hasFailed` matched only `"failed"`, so a timed-out project exited 0 with
    // "✓ Tests passed" — while the daemon's own terminal status for the same run was
    // `failedNow` + `CompleteWithTimeout`. Both wire shapes, because the token path and
    // the older-daemon fallback reach the verdict by different routes.
    let withToken =
        """{"elapsed":"9.0s","coverage":"ran-partial","projects":[{"project":"P","status":"timed-out","output":"killed after 600s"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false withToken = 1 @>

    let noToken =
        """{"elapsed":"9.0s","projects":[{"project":"P","status":"timed-out","output":"killed after 600s"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noToken = 1 @>

    // POSITIVE CONTROL: a run with no timeout in it is still a pass, so the assertions
    // above are about the timeout and not about a blanket refusal.
    let clean =
        """{"elapsed":"1.0s","coverage":"ran-partial","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false clean = 0 @>

// ---------------------------------------------------------------------------
// The token was string-COMPARED, and anything unrecognized fell through an `else` to
// `Tests passed`, exit 0 — so a newer daemon, a typo, or a future case such as
// "all-deferred" each reported a green, reachable by the mere absence of a match arm.
// These pin the CLI to `IpcParsing`'s rule: a reading you cannot interpret is never good.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``an UNRECOGNIZED coverage token is refused, not read as a pass`` () =
    // The version-skew direction the fallback does NOT cover: daemon newer than CLI.
    let json =
        """{"elapsed":"1.0s","coverage":"all-deferred","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``a garbage coverage token is refused too — it is not a whitelist of known-bad values`` () =
    let json =
        """{"elapsed":"1.0s","coverage":"","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 3 @>

    let typo =
        """{"elapsed":"1.0s","coverage":"rann","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false typo = 3 @>

[<Fact(Timeout = 15000)>]
let ``refusing unknown tokens is NOT a blanket — a known ran token is still a pass`` () =
    // Positive control: without it, a change that refused every token would satisfy both
    // tests above while making every real green a failure.
    let json =
        """{"elapsed":"1.0s","coverage":"ran-full-suite","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 0 @>

// --- AUTOMATION-303: the verdict's causes come from the SAME entries as its count ---

[<Fact(Timeout = 15000)>]
let ``redCausesOf names the ledger SOURCE, so an fcs diagnostic stops being invisible`` () =
    // FCS is not a plugin: the daemon reports its diagnostics under the pseudo-source
    // `fcs`, which has no PluginStatus and so no line in `plugins[]`. That is the class
    // of red that produced exit 1 beside four `ok` plugins and 9,064 passing tests.
    let resp =
        { Count = 2
          Files =
            Map.ofList
                [ "src/Lib/Thing.fs",
                  [ { Plugin = "fcs"
                      Message = "internal error: Object reference not set to an instance of an object."
                      Severity = DiagnosticSeverity.Error
                      Line = 12
                      Column = 4
                      Detail = None } ]
                  "src/Lib/Other.fs",
                  [ { Plugin = "analyzers"
                      Message = "MGA-001: wildcard on a DU"
                      Severity = DiagnosticSeverity.Warning
                      Line = 3
                      Column = 1
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    let causes = redCausesOf false resp

    // The count that decides the exit code and the causes the verdict records come from
    // ONE traversal, so they agree by construction — asserted, not assumed.
    test <@ List.length causes = exitCodeFromResponse false resp * 2 @>
    test <@ causes |> List.exists (fun c -> c.Source = "fcs" && c.File = "src/Lib/Thing.fs") @>
    test <@ causes |> List.exists (fun c -> c.Message.Contains "internal error") @>

    // `--no-warn-fail` drops the warning from BOTH — the causes may never name something
    // the exit code did not count, or the file would explain a red it does not have.
    let errorsOnly = redCausesOf true resp
    test <@ List.length errorsOnly = 1 @>
    test <@ errorsOnly |> List.forall (fun c -> c.Severity = "error") @>

[<Fact(Timeout = 15000)>]
let ``redCausesOf reports NOTHING on a clean ledger`` () =
    // The positive control for the assertions above is that they found entries at all;
    // this is the other direction — a clean run may not accumulate phantom causes.
    let clean =
        { Count = 0
          Files = Map.empty
          Statuses = Map.empty
          Coverage = Complete }

    test <@ List.isEmpty (redCausesOf false clean) @>

// ---------------------------------------------------------------------------
// AUTOMATION-167 — a tree that MOVES under the check.
//
// The double tree-hash exists to catch exactly one condition: the working tree changing
// while a verdict is being produced. It catches it only if ONE of the two hashes is
// taken where the daemon stopped verifying — so the transport captures it at its settle
// boundary (`SettledTree`) and hands it to `publishVerdict`, which compares it with a
// hash taken at the write. A publisher that takes both hashes itself takes them on the
// same side of the move and sees a tree that never budged.
//
// Both halves are asserted TOGETHER, which is the whole ticket: the process exit and the
// verdict FILE are two renderings of one decision, and a deploy preflight authorises on
// the FILE. A run that exits 0 while the file records `incomplete` tells its two
// consumers opposite things about the same tree.
// ---------------------------------------------------------------------------

/// Drive `pollAndRender` over a repo with one tracked file, rewriting that file from
/// inside `getErrors` when `moveTree` — i.e. after `waitForComplete` has returned and
/// before the verdict is published, which is precisely the window the ticket describes.
///
/// `getErrors` is the seam production already reads its diagnostics through (`Program.fs`
/// passes the IPC call); nothing was added to `IpcOutput` to make this window reachable
/// from a test.
let private driveWithTreeMovedMidCheck (moveTree: bool) : int * Verdict.Verdict =
    TestHelpers.withTempDir "ipcoutput-167-tree" (fun repoRoot ->
        // The file has to sit under a DISCOVERY ROOT. Outside one, `TreeHash` never walks
        // it: both hashes are then the hash of an EMPTY tree, they agree no matter what
        // the test does to the file, and the control below would pass against a
        // publisher that compares nothing at all.
        let srcDir = System.IO.Path.Combine(repoRoot, "src")
        System.IO.Directory.CreateDirectory(srcDir) |> ignore
        let tracked = System.IO.Path.Combine(srcDir, "Tracked.txt")
        System.IO.File.WriteAllText(tracked, "the content the check was run against")

        let mutable moved = false

        let getErrors () : string =
            if moveTree && not moved then
                moved <- true
                System.IO.File.WriteAllText(tracked, "an edit that landed while the check was finishing")

            """{"count":0,"files":{},"statuses":{},"unchecked":0}"""

        let exitCode =
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle") // waitForScan
                (fun () -> "idle") // waitForComplete
                (fun () -> "{}") // getStatus
                getErrors
                (fun () -> TestRunReport.ofScopeOnly (FullSuite 1))
                // AUTOMATION-259: no projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore // forceFullRun: never fires — the scope is already full-suite
                (fun () -> "idle") // triggerScan

        // Read inside the temp dir's lifetime: the verdict dies with it.
        match Verdict.read repoRoot with
        | Verdict.Reading.Found v -> exitCode, v
        | other -> failwithf "the drive must leave a readable verdict, got %A" other)

[<Fact(Timeout = 15000)>]
let ``a tree that moves mid-check exits 2 AND records incomplete — the file and the process agree`` () =
    let exitCode, v = driveWithTreeMovedMidCheck true

    // What CI reads...
    test <@ exitCode = 2 @>
    // ...and what the deploy preflight reads. Asserting only one of these is what let
    // the defect ship: the file was already right and the process was already wrong.
    test <@ v.ExitCode = 2 @>

    match v.Outcome with
    | Verdict.Incomplete reason -> test <@ reason.Contains "working tree changed" @>
    | other -> failwithf "a tree that moved under the check must record INCOMPLETE, got %A" other

[<Fact(Timeout = 15000)>]
let ``the same drive over a tree that HOLDS STILL is green — 0 in both renderings`` () =
    // The control. Without it, a publisher that answered `incomplete`/2 unconditionally
    // would satisfy the test above, and `check` would never go green again.
    let exitCode, v = driveWithTreeMovedMidCheck false

    test <@ exitCode = 0 @>
    test <@ v.ExitCode = 0 @>
    test <@ v.Outcome = Verdict.Green @>

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``daemon check and confirm overwrite green on discovery failure before diagnostics or convergence``
    (confirmation: bool)
    =
    TestHelpers.withTempDir "ipcoutput-290-discovery" (fun repoRoot ->
        let reason =
            "PROJECT LOADING FAILED: MSBuild evaluation loaded 0 of 18 discovered project(s). Read LoadProject FAILED."

        let mode =
            if confirmation then
                CheckVerdict.Confirmation
            else
                CheckVerdict.InnerLoop

        publishVerdict
            repoRoot
            []
            mode
            false
            (TestRunReport.ofScopeOnly (FullSuite 1))
            Verdict.NoReading
            Map.empty
            []
            (SettledTree.capture repoRoot [])
            CheckVerdict.CheckOutcome.Clean
        |> ignore

        let mutable diagnosticsReads = 0
        let mutable forcedRuns = 0
        let mutable rescans = 0

        let exitCode =
            pollAndRender
                ProgressRenderer.Agent
                mode
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "complete: 0 files checked")
                (fun () -> raise (System.InvalidOperationException reason))
                (fun () -> "{}")
                (fun () ->
                    diagnosticsReads <- diagnosticsReads + 1
                    """{"count":0,"files":{},"statuses":{},"unchecked":0}""")
                (fun () -> TestRunReport.ofScopeOnly (ImpactFiltered(1, 3)))
                (fun () -> IpcParsing.ReachUnavailable "must not be read")
                (fun () -> forcedRuns <- forcedRuns + 1)
                (fun () ->
                    rescans <- rescans + 1
                    "complete: 0 files checked")

        test <@ exitCode = 2 @>
        test <@ diagnosticsReads = 0 @>
        test <@ forcedRuns = 0 @>
        test <@ rescans = 0 @>

        match Verdict.read repoRoot with
        | Verdict.Reading.Found v ->
            test <@ v.ExitCode = 2 @>

            match v.Outcome with
            | Verdict.Incomplete persisted -> test <@ persisted.Contains("PROJECT LOADING FAILED") @>
            | other -> failwithf "expected incomplete discovery verdict, got %A" other
        | other -> failwithf "expected a published discovery verdict, got %A" other)
