// This module REDIRECTS `Console.Error` (see `captureStderr`). That writer is
// process-wide, so a redirect here and a redirect in another class interleave: whichever
// restores last hands back a writer the other class captured, and from then on this
// class's output goes into that class's buffer while that class's output arrives in this
// one. Observed as a capture full of another suite's temp directories, on a test that
// asserts about its own sentence. Hence the serialized collection every other
// `Console.Error` class in this project already joins.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.IpcOutputTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Tests.TestHelpers

let private evidenceTree hash =
    VerifiedTree
        { FsHotWatch.TreeHash.Hash = hash
          FileCount = 1
          SkippedCount = 0
          DeclaredCount = 0
          AbsentDeclarationCount = 0 }

let private evidenceReport scope runId =
    { BaselineFixtures.reportOf scope with
        RunId = runId
        Seeds = [ "src/Changed.fs" ]
        SeedCount = 1
        Baseline = BaselineFixtures.reading }

let private executedA =
    evidenceReport (ImpactFiltered(2, 4)) (Some(System.Guid.Parse "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))

let private executedB =
    evidenceReport (FullSuite 4) (Some(System.Guid.Parse "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))

[<Fact>]
let ``same-tree already-verified retains the executed report atomically`` () =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None

    let effective, retainedAfterQuiet =
        TestRunEvidence.reconcile tree (BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified)) retained

    test <@ effective = executedA @>
    test <@ retainedAfterQuiet = retained @>

[<Theory>]
[<InlineData("already-verified")>]
[<InlineData("unstated")>]
let ``an executed report survives a SEQUENCE of quiet same-tree reads`` (quiet: string) =
    // Two CLI drives used to script exactly this — an executed run followed by two or
    // three quiet reads — by riding the convergence loop's re-reads. Those reads are gone
    // with the loop, and the guarantee is not: it is a property of this store, which every
    // reading passes through (`observeTestRun`), not of how many times anything was read.
    // Kept here, where it can be stated without a loop to produce the sequence.
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None

    let quietRead () =
        match quiet with
        | "unstated" -> BaselineFixtures.reportOf (NoTestsRun NoTestsReason.Unstated)
        | _ -> BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified)

    let afterFirst, retainedAfterFirst =
        TestRunEvidence.reconcile tree (quietRead ()) retained

    let afterSecond, retainedAfterSecond =
        TestRunEvidence.reconcile tree (quietRead ()) retainedAfterFirst

    // Every quiet read is graded on what actually ran, and the retention does not decay
    // with the number of reads.
    test <@ afterFirst = executedA @>
    test <@ afterSecond = executedA @>
    test <@ retainedAfterSecond = retained @>

[<Fact>]
let ``an UNREADABLE scope is not a quiet read, and borrows nothing`` () =
    // The distinction the port above turned up, worth stating rather than smoothing over:
    // `NoTestsRun` says "nothing needed running on this tree", which retained evidence can
    // legitimately answer for. `ScopeUnknown` says "I could not read what the run was" —
    // that is not a quiet read, it is an absent one, and it borrows nothing.
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None
    let unreadable = BaselineFixtures.reportOf ScopeUnknown
    let effective, _ = TestRunEvidence.reconcile tree unreadable retained

    test <@ effective = unreadable @>
    test <@ effective.RunId <> executedA.RunId @>

[<Fact>]
let ``a genuinely zero-test command remains no evidence`` () =
    let current = BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified)

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
    | Some evidence -> test <@ QualifyingEvidence.report evidence = executedA @>
    | None -> failwith "valid filtered evidence was not retained"

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``an executed-looking scope without a run id is not retainable evidence`` (fullSuite: bool) =
    let scope = if fullSuite then FullSuite 4 else ImpactFiltered(2, 4)

    let scopeOnly = BaselineFixtures.reportOf scope

    let effective, retained =
        TestRunEvidence.reconcile (evidenceTree "sha256:no-run") scopeOnly None

    test <@ effective = scopeOnly @>
    test <@ retained = None @>

[<Fact>]
let ``already-verified on a changed tree cannot reuse executed evidence`` () =
    let _, retained = TestRunEvidence.reconcile (evidenceTree "sha256:a") executedA None
    let current = BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified)

    let effective, retainedAfterMove =
        TestRunEvidence.reconcile (evidenceTree "sha256:b") current retained

    test <@ effective = current @>
    test <@ retainedAfterMove = None @>

[<Theory>]
[<InlineData("changes-uncovered")>]
[<InlineData("unknown-reason")>]
[<InlineData("unreadable")>]
let ``a disqualifying same-tree read drops prior evidence`` (kind: string) =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None

    let scope =
        match kind with
        | "changes-uncovered" -> NoTestsRun(NoTestsReason.ChangesUncovered([ "M.f" ], 1, UnrunnableCoverage.none))
        | "unknown-reason" -> NoTestsRun(NoTestsReason.UnknownReason "future")
        | _ -> ScopeUnreadable "broken reply"

    let current = BaselineFixtures.reportOf scope

    let effective, retainedAfterRefusal =
        TestRunEvidence.reconcile tree current retained

    test <@ ReadKind.Disqualifying scope = TestRunEvidence.classify tree current retained @>
    test <@ effective = current @>
    test <@ retainedAfterRefusal = None @>

// The theory above once also listed `unstated`, and the
// fold's wildcard arm dropped the evidence for it. A same-tree read that ran nothing and
// gave no reason is a QUIET read, exactly as `already-verified` is: it neither adds to
// nor contradicts what an earlier read of the same check executed on the same tree.
[<Fact>]
let ``same-tree unstated keeps prior executed evidence`` () =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None
    let current = BaselineFixtures.reportOf (NoTestsRun NoTestsReason.Unstated)

    test <@ TestRunEvidence.classify tree current retained = ReadKind.QuietSameTree retained.Value @>

    let effective, retainedAfterQuiet = TestRunEvidence.reconcile tree current retained

    test <@ effective = executedA @>
    test <@ retainedAfterQuiet = retained @>

[<Fact>]
let ``unstated on a moved tree cannot reuse executed evidence`` () =
    let _, retained = TestRunEvidence.reconcile (evidenceTree "sha256:a") executedA None
    let current = BaselineFixtures.reportOf (NoTestsRun NoTestsReason.Unstated)

    let effective, retainedAfterMove =
        TestRunEvidence.reconcile (evidenceTree "sha256:b") current retained

    test <@ effective = current @>
    test <@ retainedAfterMove = None @>

// `scope: "running"` is what a check reads while the daemon is mid-run.
// It says nothing about what ran and nothing about the tree, so it is neither composed
// nor allowed to wipe: the settled read that follows decides.
[<Fact>]
let ``an in-flight read neither composes nor wipes retained evidence`` () =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None
    let running = BaselineFixtures.reportOf ScopeUnknown

    test <@ TestRunEvidence.classify tree running retained = ReadKind.InFlight @>

    let effective, retainedAfterRunning =
        TestRunEvidence.reconcile tree running retained

    test <@ effective = running @>
    test <@ retainedAfterRunning = retained @>

    // The quiet read that follows the run serves the evidence the in-flight read kept.
    let afterSettle, _ =
        TestRunEvidence.reconcile tree (BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified)) retained

    test <@ afterSettle = executedA @>

[<Fact>]
let ``a quiet read with no retained evidence is disqualifying, not quiet`` () =
    let tree = evidenceTree "sha256:same"
    let current = BaselineFixtures.reportOf (NoTestsRun NoTestsReason.Unstated)
    test <@ TestRunEvidence.classify tree current None = ReadKind.Disqualifying current.Scope @>
    test <@ TestRunEvidence.reconcile tree current None = (current, None) @>

// The arms the fold names that the driver reaches only on its bad
// paths, pinned here so they are exercised as the pure functions they are.
[<Fact>]
let ``an executed read on a check that never settled qualifies nothing`` () =
    test <@ TestRunEvidence.ofExecuted NeverSettled executedA = None @>
    test <@ TestRunEvidence.classify NeverSettled executedA None = ReadKind.Disqualifying executedA.Scope @>
    test <@ TestRunEvidence.reconcile NeverSettled executedA None = (executedA, None) @>

[<Fact>]
let ``a quiet read after the settled tree is lost cannot reuse retained evidence`` () =
    let _, retained =
        TestRunEvidence.reconcile (evidenceTree "sha256:same") executedA None

    let quiet = BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified)
    test <@ TestRunEvidence.classify NeverSettled quiet retained = ReadKind.Disqualifying quiet.Scope @>
    test <@ TestRunEvidence.reconcile NeverSettled quiet retained = (quiet, None) @>

[<Fact>]
let ``a later filtered run on a moved tree replaces full-suite evidence from the old tree`` () =
    // The full-beats-filtered rule holds only on the SAME tree: `--run-once` re-captures
    // the settled tree after a forced run, and evidence keyed to the old one is no
    // longer a claim about this one.
    let _, retainedFull =
        TestRunEvidence.reconcile (evidenceTree "sha256:a") executedB None

    let effective, retainedAfterMove =
        TestRunEvidence.reconcile (evidenceTree "sha256:b") executedA retainedFull

    test <@ effective = executedA @>

    match retainedAfterMove with
    | Some evidence ->
        test <@ QualifyingEvidence.report evidence = executedA @>
        test <@ QualifyingEvidence.treeHash evidence = "sha256:b" @>
    | None -> failwith "the executed read on the new tree must be retained"

[<Fact>]
let ``a later executed report wholly replaces the prior report`` () =
    let tree = evidenceTree "sha256:same"
    let _, retainedA = TestRunEvidence.reconcile tree executedA None
    let effective, retainedB = TestRunEvidence.reconcile tree executedB retainedA
    test <@ effective = executedB @>

    match retainedB with
    | Some evidence -> test <@ QualifyingEvidence.report evidence = executedB @>
    | None -> failwith "the later executed report must itself be retained"

[<Fact>]
let ``a later filtered run cannot downgrade same-tree full-suite evidence`` () =
    let tree = evidenceTree "sha256:same"
    let _, retainedFull = TestRunEvidence.reconcile tree executedB None

    let effective, retainedAfterFiltered =
        TestRunEvidence.reconcile tree executedA retainedFull

    test <@ effective = executedB @>
    test <@ retainedAfterFiltered = retainedFull @>

[<Fact>]
let ``a later full-suite run upgrades same-tree filtered evidence`` () =
    let tree = evidenceTree "sha256:same"
    let _, retainedFiltered = TestRunEvidence.reconcile tree executedA None

    let effective, retainedFull =
        TestRunEvidence.reconcile tree executedB retainedFiltered

    test <@ effective = executedB @>

    match retainedFull with
    | Some evidence -> test <@ QualifyingEvidence.report evidence = executedB @>
    | None -> failwith "full-suite evidence must replace filtered evidence"

[<Fact>]
let ``retained test evidence cannot hide a later plugin failure`` () =
    let tree = evidenceTree "sha256:same"
    let _, retained = TestRunEvidence.reconcile tree executedA None

    let effective, _ =
        TestRunEvidence.reconcile tree (BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified)) retained

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
          Scope = effective.Scope
          Baseline = BaselineFixtures.reading
          ProjectModel = ProjectModelFixtures.available }

    let outcome = CheckVerdict.verdict CheckVerdict.InnerLoop inputs
    test <@ outcome = CheckVerdict.CheckOutcome.FailuresFound @>
    test <@ CheckVerdict.exitCode outcome = 1 @>

let private writeEvidenceSuite (repoRoot: string) (runId: System.Guid) =
    let runDir = FsHotWatch.Ctrf.runDir repoRoot runId
    System.IO.Directory.CreateDirectory(runDir) |> ignore

    System.IO.File.WriteAllText(
        System.IO.Path.Combine(runDir, "A.Tests" + FsHotWatch.Ctrf.ReportSuffix),
        """{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"a","results":{"tool":{"name":"xUnit.net v3"},"summary":{"tests":3,"passed":3,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1,"start":1,"stop":2},"tests":[{"name":"A.Tests.T.passes1","status":"passed"},{"name":"A.Tests.T.passes2","status":"passed"},{"name":"A.Tests.T.passes3","status":"passed"}]}}"""
    )

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
    // This silently defeated all of the wedge detection: the classifier only
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
let ``agent output puts each diagnostic line before the trailing next hint`` () =
    // An agent reads the hint as the last thing to do, so the findings go above it.
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":3,"detail":null}]},"statuses":{}}"""

    let output =
        formatDiagnosticsResponse
            ProgressRenderer.Agent
            (fun _ -> [ "banner"; "lint: 1 warning"; "next: fshw check" ])
            (parseDiagnosticsResponse json)

    test
        <@
            output.Split '\n' = [| "banner"
                                   "lint: 1 warning"
                                   "lint:src/Foo.fs:17:3: warning bad name"
                                   "next: fshw check" |]
        @>

[<Fact(Timeout = 15000)>]
let ``a busy rerun with no message still refuses a green, naming a default reason`` () =
    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"status":"busy"}""" = 1 @>

[<Fact(Timeout = 15000)>]
let ``a rerun whose project entry carries no name or status is never a green`` () =
    // An entry the CLI cannot read is rendered as "(unnamed)" / "unknown" and is not
    // counted as having executed anything, so the reply fails closed.
    test
        <@
            renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"projects":[{}]}"""
            <> 0
        @>

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
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

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
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

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
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

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
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

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
    // `test-rerun` is the "prove it ran" verb, so a reply carrying NO run
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
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

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
// so it has not transitioned Idle->Running). A status-map predicate treats Idle as quiescent
// and never consults the host's inflight/busy state, so `check` concluded "settled", read
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
        """{"count":1,"files":{"tests/Foo.fs":[{"plugin":"test-prune","message":"1 test failed","severity":"error","line":0,"column":0,"detail":null}]},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}"""
    else
        """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}"""

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
                (fun () -> BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                // No projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore) // forceFullRun: never fires — the scope is already full-suite

    // The authoritative settle MUST have been consulted...
    test <@ waitForCompleteCalls >= 1 @>
    // ...and exit 1 only happens if the check waited for the verdict before reading
    // diagnostics. Settling on the status map reads the clean ledger during the Idle
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
        """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}"""

    // The verdict the daemon transport publishes is stamped with the
    // invocation that drove it, so the wrapping CLI can attach its hook timing to THIS
    // file and to no other.
    let invocationId = "daemon-transport-invocation"

    let exitCode, recordedInvocation =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            let code =
                pollAndRenderForInvocation
                    (Verdict.Invocation.startAs invocationId)
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
                    (fun () -> BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                    // No projection on offer. `InnerLoop` never asks, and a
                    // `Confirmation` that gets this records "no sample", never an agreement.
                    (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                    ignore // forceFullRun: never fires — the scope is already full-suite

            let recorded =
                match Verdict.read repoRoot with
                | Verdict.Reading.Found verdict -> verdict.InvocationId
                | other -> failwith $"expected daemon verdict, got %A{other}"

            code, recorded)

    test <@ exitCode = 0 @>
    test <@ recordedInvocation = Some invocationId @>

// --- the project model reaches the verdict file ---

/// A clean `GetDiagnostics` reply carrying `projectModel` exactly as the daemon serves it
/// (`ProjectModelWire.payload`), or no `projectModel` at all for `None` — an older daemon.
let private diagnosticsWithModel (model: FsHotWatch.ProjectModel.Observation option) : string =
    let modelField =
        match model with
        | Some observation ->
            let payload =
                System.Text.Json.JsonSerializer.Serialize(FsHotWatch.ProjectModelWire.payload observation)

            $""","projectModel":%s{payload}"""
        | None -> ""

    $"""{{"count":0,"files":{{}},"statuses":{{}},"unchecked":0%s{modelField}}}"""

/// The fields of the written `.fshw/verdict.json` these tests read, as plain values.
type private SeenFile =
    { Schema: string
      OutcomeKind: string
      ExitCode: int
      ModelStatus: string
      ModelSchema: string option
      ModelObservation: FsHotWatch.ProjectModel.Observation option }

let private seenFileOf (raw: string) : SeenFile =
    use doc = System.Text.Json.JsonDocument.Parse raw
    let root = doc.RootElement
    let model = root.GetProperty("projectModel")

    { Schema = root.GetProperty("schema").GetString()
      OutcomeKind = root.GetProperty("outcome").GetProperty("kind").GetString()
      ExitCode = root.GetProperty("exitCode").GetInt32()
      ModelStatus = model.GetProperty("status").GetString()
      ModelSchema =
        match model.TryGetProperty("schema") with
        | true, schema -> Some(schema.GetString())
        | false, _ -> None
      ModelObservation = FsHotWatch.ProjectModelWire.tryRead model }

/// Drive the daemon transport to a published verdict over a fully passing test run, and
/// return the exit code, the verdict read back, and the written file.
let private driveWithModel
    (mode: CheckVerdict.CheckMode)
    (run: TestRunReport)
    (diagnostics: string)
    : int * Verdict.Reading * SeenFile =
    TestHelpers.withTempDir "ipcoutput" (fun repoRoot ->
        let code =
            pollAndRenderForInvocation
                (Verdict.Invocation.startAs " ")
                ProgressRenderer.Agent
                mode
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle")
                (fun () -> statusJsonFor true)
                (fun () -> statusJsonFor true)
                (fun () -> diagnostics)
                (fun () -> run)
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore

        let raw = System.IO.File.ReadAllText(Verdict.path repoRoot)
        code, Verdict.read repoRoot, seenFileOf raw)

[<Theory(Timeout = 15000)>]
[<InlineData("rediscovering", "rediscovering")>]
[<InlineData("loading-failed", "unavailable")>]
[<InlineData("unobserved", "unobserved")>]
[<InlineData("older-daemon", "not-reported")>]
let ``a check over a passing FULL-SUITE run is NOT green when the daemon's project model was unavailable``
    (model: string, status: string)
    =
    // Every other input is the one `pollAndRender surfaces a clean verdict` goes green on.
    // Only the project model differs — so the refusal can only have come from it.
    let observation =
        match model with
        | "rediscovering" -> Some(FsHotWatch.ProjectModel.Observation.Rediscovering 12L)
        | "loading-failed" ->
            Some(
                FsHotWatch.ProjectModel.ofCompleted
                    11L
                    { Discovered = 21
                      Loaded = 0
                      OptionsMapped = 0
                      Registered = 0 }
            )
        | "unobserved" -> Some FsHotWatch.ProjectModel.Observation.Unobserved
        | _ -> None

    let exitCode, reading, file =
        driveWithModel
            CheckVerdict.InnerLoop
            (BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
            (diagnosticsWithModel observation)

    test <@ exitCode = 2 @>

    // The machine-readable half: its own outcome kind, and the model state beside it.
    test <@ file.Schema = "fshw-verdict-v2" @>
    test <@ file.OutcomeKind = "model-unavailable" @>
    test <@ file.ModelStatus = status @>
    test <@ file.ExitCode = 2 @>

    match reading with
    | Verdict.Reading.Found v ->
        match v.Outcome with
        | Verdict.ModelUnavailable _ -> ()
        | other -> failwith $"expected model-unavailable, got %A{other}"

        test <@ IpcParsing.ProjectModelReading.available v.ProjectModel |> Option.isNone @>

        match observation with
        | Some o -> test <@ v.ProjectModel = IpcParsing.ProjectModelReading.Observed o @>
        | None -> ()
    | other -> failwith $"the refusal must still be a READABLE verdict, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``POSITIVE CONTROL: a healthy model that selected nothing records its own answer and the available model`` () =
    let exitCode, reading, file =
        driveWithModel
            CheckVerdict.InnerLoop
            (BaselineFixtures.reportOf (IpcParsing.NoTestsRun IpcParsing.NoTestsReason.AlreadyVerified))
            (diagnosticsWithModel (Some ProjectModelFixtures.observation))

    // The pre-existing answer for "nothing needed re-verifying" — not the model alarm.
    test <@ exitCode = 3 @>
    test <@ file.OutcomeKind = "incomplete" @>
    test <@ file.ModelStatus = "available" @>
    test <@ file.ModelSchema = Some "fshw-project-model-v1" @>

    match reading with
    | Verdict.Reading.Found v ->
        match v.Outcome with
        | Verdict.Incomplete reason ->
            test <@ reason.Contains "nothing needed re-verifying" @>
            test <@ not (reason.Contains "PROJECT MODEL") @>
        | other -> failwith $"a healthy empty selection must keep its own reading, got %A{other}"

        test <@ v.ProjectModel = ProjectModelFixtures.available @>
    | other -> failwith $"expected a readable verdict, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``a green verdict file records the available project model it was graded against`` () =
    let exitCode, _, file =
        driveWithModel
            CheckVerdict.InnerLoop
            (BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
            (diagnosticsWithModel (Some ProjectModelFixtures.observation))

    test <@ exitCode = 0 @>
    test <@ file.OutcomeKind = "green" @>
    test <@ file.ModelObservation = Some ProjectModelFixtures.observation @>

// --- one check, several run directories ---

/// One CTRF report, in the run directory that OWNS it.
let private writeRunReport (repoRoot: string) (runId: System.Guid) (project: string) (tests: int) : unit =
    let runDir = FsHotWatch.Ctrf.runDir repoRoot runId
    System.IO.Directory.CreateDirectory(runDir) |> ignore

    let rows =
        List.init tests (fun i -> $"""{{"name":"%s{project}.T.passes%d{i}","status":"passed"}}""")
        |> String.concat ","

    let json =
        """{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"batch","results":{"tool":{"name":"xUnit.net v3"},"summary":{"tests":N,"passed":N,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1,"start":1,"stop":2},"tests":[ROWS]}}"""
            .Replace("N", string<int> tests)
            .Replace("ROWS", rows)

    System.IO.File.WriteAllText(System.IO.Path.Combine(runDir, project + FsHotWatch.Ctrf.ReportSuffix), json)

[<Fact(Timeout = 15000)>]
let ``a check whose daemon ran the tests TWICE publishes a verdict covering BOTH run directories`` () =
    // The reported case, driven through the real check loop.
    //
    // The daemon here is a check away from a fresh start: it has already completed a run
    // for a PREVIOUS check (`earlier`), and during this check it completes two more —
    // the impact-selected batch that holds the change's acceptance tests, then a small
    // build-tooling batch thirty seconds later. `test-scope` names the LAST one, which
    // is how a reader following the documented procedure landed on 566 tests out of
    // 10,979 and concluded their own tests had never run.
    //
    // Both directions are asserted, because a verdict that simply listed every run
    // directory it could find would pass the first half and be wrong: `earlier` belongs
    // to another check and must NOT be adopted by this one.
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
        """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}"""

    let earlier = System.Guid.NewGuid()
    let firstBatch = System.Guid.NewGuid()
    let secondBatch = System.Guid.NewGuid()

    // The daemon's own ledger, newest first, exactly as `test-scope` declares it. The
    // FIRST reading is the baseline the driver takes before its scan — at that point
    // only the previous check's run has happened.
    let mutable readings = 0

    let getTestRun () : TestRunReport =
        readings <- readings + 1

        if readings = 1 then
            { BaselineFixtures.reportOf (FullSuite 2) with
                RunId = Some earlier
                SessionRuns = [ earlier ] }
        else
            { BaselineFixtures.reportOf (FullSuite 2) with
                RunId = Some secondBatch
                SessionRuns = [ secondBatch; firstBatch; earlier ] }

    let v =
        TestHelpers.withTempDir "ipcoutput-533-two-batches" (fun repoRoot ->
            writeRunReport repoRoot earlier "PreviousCheck.Tests" 42
            writeRunReport repoRoot firstBatch "Acceptance.Tests" 23
            writeRunReport repoRoot secondBatch "BuildTooling.Tests" 566

            pollAndRenderForInvocation
                (Verdict.Invocation.startAs "two-batch-check")
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
                getTestRun
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore
            |> ignore

            match Verdict.read repoRoot with
            | Verdict.Reading.Found v -> v
            | other -> failwith $"expected a verdict, got %A{other}")

    // Graded from the run the daemon's receipt names — unchanged, and still the answer
    // to "which run was this verdict computed from".
    test <@ v.RunId = Some secondBatch @>

    // ...and accountable for BOTH of this check's batches. `Acceptance.Tests` ran only
    // in the first one: under the old verdict it was absent, and its absence was read
    // as "it never ran".
    test <@ v.Runs |> List.map (fun r -> r.RunId) = [ Some secondBatch; Some firstBatch ] @>

    let projects = v.Suites |> List.map (fun s -> s.Project) |> List.sort
    test <@ projects = [ "Acceptance.Tests"; "BuildTooling.Tests" ] @>

    // The negative half: the previous check's run is still on disk, and is not this
    // check's evidence.
    test <@ not (projects |> List.contains "PreviousCheck.Tests") @>

[<Fact(Timeout = 15000)>]
let ``run attribution takes every run the daemon completed after the baseline, oldest first, once each`` () =
    // The fold on its own. `attribute` is what makes the batches a check never watched
    // go by — the rerun a mid-run change queues, the drain of a queued force-run —
    // countable at all: they are in the daemon's ledger without ever having been the
    // answer to a `test-scope`.
    let baselineRun = System.Guid.NewGuid()
    let a = System.Guid.NewGuid()
    let b = System.Guid.NewGuid()
    let c = System.Guid.NewGuid()

    let reading (runId: System.Guid) (session: System.Guid list) =
        { BaselineFixtures.reportOf (FullSuite 1) with
            RunId = Some runId
            SessionRuns = session }

    let baseline = Some(Set.ofList [ baselineRun ])

    // First reading: two runs the driver never saw individually.
    let afterFirst =
        IpcOutput.TestRunEvidence.attribute baseline [] (reading b [ b; a; baselineRun ])

    test <@ afterFirst = [ a; b ] @>

    // Second reading: one more run, and the same ones again. Re-reading the daemon
    // cannot double-count, and the order stays the order they ran in.
    let afterSecond =
        IpcOutput.TestRunEvidence.attribute baseline afterFirst (reading c [ c; b; a; baselineRun ])

    test <@ afterSecond = [ a; b; c ] @>

    // A daemon too old to send a ledger says nothing, and then the only run this can
    // name is the one it observed — today's behaviour, never worse.
    test <@ IpcOutput.TestRunEvidence.attribute (Some Set.empty) [] (reading a []) = [ a ] @>

    // And a baseline that could not be TAKEN is not an empty one. With no baseline the
    // ledger cannot be diffed, so only the observed run is claimed — under-reporting,
    // never adopting an earlier check's runs.
    test <@ IpcOutput.TestRunEvidence.attribute None [] (reading b [ b; a; baselineRun ]) = [ b ] @>

// --- isDaemonShutdownDuringWait (mid-wait teardown classification) ---
// A WaitForComplete that faults because the daemon shut down or the pipe
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
                (fun () ->
                    """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}""") // getErrors
                (fun () -> BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                // No projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore) // forceFullRun: never fires — the scope is already full-suite

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
                (fun () ->
                    """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}""") // getErrors
                (fun () -> BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                // No projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore) // forceFullRun: never fires — the scope is already full-suite

    test <@ exitCode = 2 @>

// ---------------------------------------------------------------------------
// `confirm` EARNS its evidence.
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
            BaselineFixtures.reportOf (FullSuite 1)
        else
            BaselineFixtures.reportOf (ImpactFiltered(1, 1))

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
                (fun () ->
                    """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}""") // getErrors
                getTestRun
                // No projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                (fun () -> forceCalls <- forceCalls + 1)) // forceFullRun

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
                (fun () ->
                    """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}""")
                (fun () -> BaselineFixtures.reportOf (FullSuite 1))
                // No projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                (fun () -> forceCalls <- forceCalls + 1))

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
/// The check-vs-confirm sample lives or dies in the wiring: `publishVerdict` will
/// happily classify a `None` it was handed, so a transport that captured nothing at the escalation would
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
            { BaselineFixtures.reportOf (FullSuite 1) with
                RunId = Some driveRunId }
        else
            { BaselineFixtures.reportOf firstScope with
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
            (fun () ->
                """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}""")
            getTestRun
            getCheckReach
            (fun () -> forceCalls <- forceCalls + 1)
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
        driveConfirmForVerdict CheckVerdict.Confirmation (ImpactFiltered(1, 1)) offersNothing

    // Both runs were clean, so this is the ordinary sample the feature exists to collect.
    test <@ v.Divergence = Verdict.Divergence.Agreed @>

    match v.ImpactScopedRun with
    // The scope the daemon reported BEFORE the force — not the full suite the verdict
    // itself rests on.
    | Some pre ->
        test <@ pre.Scope = ImpactFiltered(1, 1) @>
        // It RAN. The projection is a different measurement and must not be able to
        // masquerade as this one.
        test <@ pre.Basis = Verdict.SampleBasis.Executed @>
    | None -> failwith "an escalating confirm must record the reading it escalated away from"

[<Fact(Timeout = 15000)>]
let ``a confirm that did NOT escalate records the PROJECTED sample, not a bare "nothing compared"`` () =
    // Rework of the check-vs-confirm sample. `confirm` widens the scope BEFORE the scan,
    // so the run its own scan provokes is unfiltered and this branch — not the escalating
    // one — is what CI takes every single time. It used to record `no-impact-scoped-run`:
    // a true statement that produced, in ten days and seventeen confirms, zero samples.
    let projected =
        driveConfirmForVerdict
            CheckVerdict.Confirmation
            (FullSuite 1)
            (offering IpcParsing.NoFailuresToReach (ImpactFiltered(1, 1)))

    test <@ projected.Divergence = Verdict.Divergence.Agreed @>

    match projected.ImpactScopedRun with
    | Some pre ->
        test <@ pre.Basis = Verdict.SampleBasis.ProjectedFromFullRun @>
        // The scope `check` WOULD have covered — not the full suite this verdict rests on.
        test <@ pre.Scope = ImpactFiltered(1, 1) @>
    | None -> failwith "a non-escalating confirm must record the projected reading"

[<Fact(Timeout = 15000)>]
let ``a confirm with no projection on offer says nothing was compared, and a check records no comparison at all`` () =
    // The controls, one on each side. Without them, a transport that recorded a reading
    // unconditionally, or one that recorded nothing ever, would still satisfy the two
    // tests above.
    let noSample =
        driveConfirmForVerdict CheckVerdict.Confirmation (FullSuite 1) offersNothing

    // An unavailable projection is a REFUSAL, never an agreement: this is the whole
    // fail-closed direction, asserted end to end through the transport.
    match noSample.Divergence with
    | Verdict.Divergence.Incomparable reason -> test <@ reason.Contains "offers no projection" @>
    | other -> failwithf "an unavailable projection must be INCOMPARABLE, got %A" other

    // `check` never escalates, so it never has a comparison to make — confirm-only, and
    // `Verdict.create` refuses a check that claims otherwise.
    let inner =
        driveConfirmForVerdict CheckVerdict.InnerLoop (ImpactFiltered(1, 1)) offersNothing

    test <@ inner.Command = Verdict.Check @>
    test <@ inner.Divergence = Verdict.Divergence.NotRecorded @>

// ---------------------------------------------------------------------------
// NOTE: these payloads carried a `verifiedNothing` boolean that NO producer writes and
// no consumer reads — `formatTestResultsJson` emits `elapsed`/`filter`/`noTestsMatched`/
// `coverage`/`projects`, and the run-level "verified nothing" fact is already carried by
// the `coverage` token (`RunVerification.verifiedNothing` derives it). It was removed
// rather than emitted: a fixture asserting a field the wire does not have vouches for a
// payload that never occurs, and a second serialized source of the same fact is the drift
// this whole block guards against.
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
    // executed and passed is still a pass.
    for token in [ "ran-full-suite"; "ran-partial" ] do
        let json =
            $"""{{"elapsed":"1.0s","coverage":"{token}","projects":[{{"project":"P","status":"passed","output":"Passed! total: 3"}}]}}"""

        test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 0 @>

[<Fact(Timeout = 15000)>]
let ``the retired bare "ran" token is refused, not read as a pass`` () =
    // Older daemons sent `"ran"`: tests executed, breadth unstated. The missing half used
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
    // The residual vacuous green in the older-daemon fallback. This
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

// --- the verdict's causes come from the SAME entries as its count ---

/// Where a cause with nothing of its own to say sends the reader. Production resolves it
/// from the repo's configured `logDir`; these tests are about which entries become causes.
let private daemonLog = DaemonConfig.DaemonLog.under DaemonConfig.DefaultLogDir

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
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

    let causes = redCausesOf daemonLog false resp

    // The count that decides the exit code and the causes the verdict records come from
    // ONE traversal, so they agree by construction — asserted, not assumed.
    test <@ List.length causes = exitCodeFromResponse false resp * 2 @>
    test <@ causes |> List.exists (fun c -> c.Source = "fcs" && c.File = "src/Lib/Thing.fs") @>

    test
        <@
            causes
            |> List.exists (fun c -> (Verdict.RedCauseMessage.value c.Message).Contains "internal error")
        @>

    // `--no-warn-fail` drops the warning from BOTH — the causes may never name something
    // the exit code did not count, or the file would explain a red it does not have.
    let errorsOnly = redCausesOf daemonLog true resp
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
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

    test <@ List.isEmpty (redCausesOf daemonLog false clean) @>

// ---------------------------------------------------------------------------
// A tree that MOVES under the check.
//
// The double tree-hash exists to catch exactly one condition: the working tree changing
// while a verdict is being produced. It catches it only if ONE of the two hashes is
// taken where the daemon stopped verifying — so the transport captures it at its settle
// boundary (`SettledTree`) and hands it to `publishVerdict`, which compares it with a
// hash taken at the write. A publisher that takes both hashes itself takes them on the
// same side of the move and sees a tree that never budged.
//
// Both halves are asserted TOGETHER, which is the whole point: the process exit and the
// verdict FILE are two renderings of one decision, and a deploy preflight authorises on
// the FILE. A run that exits 0 while the file records `incomplete` tells its two
// consumers opposite things about the same tree.
// ---------------------------------------------------------------------------

/// Drive `pollAndRender` over a repo with one tracked file, rewriting that file from
/// inside `getErrors` when `moveTree` — i.e. after `waitForComplete` has returned and
/// before the verdict is published, which is precisely the window at issue.
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

            """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}"""

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
                (fun () -> BaselineFixtures.reportOf (FullSuite 1))
                // No projection on offer. `InnerLoop` never asks, and a
                // `Confirmation` that gets this records "no sample", never an agreement.
                (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                ignore // forceFullRun: never fires — the scope is already full-suite

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

/// Run `f`, capturing everything it writes to stderr, and return (stderr, result).
///
/// `Console.SetError` is PROCESS-WIDE. No lock in this file can exclude a writer or a
/// redirector in another one, so the exclusion that matters is the module's
/// `LogGlobalCollectionName` collection: it serializes this class against every other
/// class that redirects `Console.Error`, which is what stops a foreign restore from
/// handing our writer away mid-capture.
///
/// The rest is belt: a bare `StringWriter` is not thread-safe, so its own `ToString()`
/// threw `ArgumentOutOfRangeException (chunkLength)` mid-append under a loaded box. The
/// synchronized wrapper keeps that safe against a class that merely WRITES to stderr
/// while running in parallel, and such a line is harmless to the assertions here, since
/// every caller asserts on `Contains`, never on the whole buffer.
///
/// Captures are serialized against each other so two of them cannot interleave their
/// redirections and restore the wrong original.
let private stderrCaptureGate = obj ()

let private captureStderr (f: unit -> 'a) : string * 'a =
    lock stderrCaptureGate (fun () ->
        let original = System.Console.Error
        use buffer = new System.IO.StringWriter()
        let writer = System.IO.TextWriter.Synchronized buffer
        System.Console.SetError(writer)

        try
            let result = f ()
            writer.Flush()
            // Read under the same lock the synchronized writer uses, so a concurrent
            // write cannot be mid-append while the buffer is materialized.
            lock writer (fun () -> buffer.ToString()), result
        finally
            System.Console.SetError(original))

[<Fact(Timeout = 15000)>]
let ``a tree that moves mid-check says so at the terminal, not only in the verdict file`` () =
    // The file was already right and silent at the terminal: an operator saw exit 2 with
    // no sentence at all, because the caller explains the outcome it handed in (clean).
    let stderr, (exitCode, _) =
        captureStderr (fun () -> driveWithTreeMovedMidCheck true)

    test <@ exitCode = 2 @>
    test <@ stderr.Contains "working tree changed" @>

    // The control: a tree that held still says nothing of the kind.
    let quiet, (greenExit, _) =
        captureStderr (fun () -> driveWithTreeMovedMidCheck false)

    test <@ greenExit = 0 @>
    test <@ not (quiet.Contains "working tree changed") @>

[<Fact(Timeout = 15000)>]
let ``the same drive over a tree that HOLDS STILL is green — 0 in both renderings`` () =
    // The control. Without it, a publisher that answered `incomplete`/2 unconditionally
    // would satisfy the test above, and `check` would never go green again.
    let exitCode, v = driveWithTreeMovedMidCheck false

    test <@ exitCode = 0 @>
    test <@ v.ExitCode = 0 @>
    test <@ BaselineFixtures.isGreen (v.Outcome) @>

/// The seven-suite shape the consuming repo's `confirm` leaves behind: one CTRF report
/// per test project under the run directory, which is where `Verdict.suiteVerdicts`
/// reads `suites[]` from. Returns the project names, sorted as the verdict lists them.
let private writeSevenSuiteRun (repoRoot: string) (runId: System.Guid) : string list =
    let runDir = FsHotWatch.Ctrf.runDir repoRoot runId
    System.IO.Directory.CreateDirectory(runDir) |> ignore

    let projects =
        [ "Intelligence.Tests.Unit"
          "Intelligence.Tests.Integration"
          "Intelligence.Tests.Pipeline"
          "Intelligence.Tests.Web"
          "Intelligence.Tests.Legal"
          "Intelligence.Tests.Playwright"
          "Intelligence.Tests.Build" ]

    for project in projects do
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(runDir, project + FsHotWatch.Ctrf.ReportSuffix),
            """{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"seven","results":{"tool":{"name":"xUnit.net v3"},"summary":{"tests":3,"passed":3,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1,"start":1,"stop":2},"tests":[{"name":"A.Tests.T.passes1","status":"passed"},{"name":"A.Tests.T.passes2","status":"passed"},{"name":"A.Tests.T.passes3","status":"passed"}]}}"""
        )

    List.sort projects

[<Fact(Timeout = 15000)>]
let ``a zero-test convergence result preserves a prior applicable full-suite green`` () =
    // A re-scan can settle successfully while TestPrune has no test work to do. That
    // result is correctly refused as NEW evidence, but it must not erase a full-suite
    // green the same binary already earned over this unchanged tree. Otherwise the
    // next `confirm` loses the only evidence it is entitled to reuse.
    //
    // The incident as REPORTED: the erased verdict carried a run id, seven suite
    // entries and a tree hash, and what the reader lost was exactly those. So the prior
    // here is that verdict — a real run with seven CTRF reports and a green plugin
    // record — and the proof is the FILE read back after the zero-test publish, entry
    // by entry, not merely that `priorConfirmation` still says the green applies.
    TestHelpers.withTempDir "ipcoutput-643-preserve-prior-green" (fun repoRoot ->
        let runId = System.Guid.NewGuid()
        let projects = writeSevenSuiteRun repoRoot runId

        let fullRun =
            { BaselineFixtures.reportOf (FullSuite 7) with
                RunId = Some runId
                Seeds = [ "src/Changed.fs" ]
                SeedCount = 1
                Baseline = BaselineFixtures.reading }

        let greenTestPrune =
            { Status = StatusView.Completed System.DateTime.UtcNow
              Subtasks = []
              ActivityTail = []
              LastRun =
                Some
                    { StartedAt = System.DateTime.UtcNow.AddSeconds(-30.0)
                      Elapsed = System.TimeSpan.FromSeconds 25.0
                      Outcome = CompletedRun
                      Summary = Some "21 passed, 0 failed in 7 projects"
                      ActivityTail = [] }
              Diagnostics = DiagnosticCounts.empty }

        let initialExitCode =
            publishVerdict
                repoRoot
                []
                CheckVerdict.Confirmation
                false
                fullRun
                Verdict.NoReading
                (Map.ofList [ "test-prune", greenTestPrune ])
                []
                ProjectModelFixtures.available
                (SettledTree.capture repoRoot [])
                (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

        test <@ initialExitCode = 0 @>

        // POSITIVE CONTROL on the fixture: the prior really is a seven-suite full green
        // with a run id and a tree hash, so an intact read-back below proves something.
        let priorText = System.IO.File.ReadAllText(Verdict.path repoRoot)

        let prior =
            match Verdict.read repoRoot with
            | Verdict.Reading.Found v -> v
            | other -> failwithf "the prior full-suite green must be readable, got %A" other

        test <@ Verdict.isFullSuiteGreen prior @>
        test <@ prior.RunId = Some runId @>
        test <@ prior.Suites |> List.map _.Project |> List.sort = projects @>

        test
            <@
                prior.Suites
                |> List.forall (fun s -> s.Total = 3 && s.Passed = 3 && s.Failed = 0)
            @>

        test <@ prior.Plugins |> List.map _.Name = [ "test-prune" ] @>

        let initialInputs: CheckVerdict.CheckInputs =
            { PluginStatuses = Map.empty
              FailingDiagnostics = 0
              UnattributableDiagnostics = 0
              WaitingOnBuild = CheckVerdict.BuildWait.NotWaiting
              RunnerAborted = CheckVerdict.RunnerAbort.NoAbort
              Coverage = Incomplete 1
              Scope = FullSuite 7
              Baseline = BaselineFixtures.reading
              ProjectModel = ProjectModelFixtures.available }

        let zeroTestInputs =
            { initialInputs with
                Coverage = Complete
                Scope = NoTestsRun NoTestsReason.AlreadyVerified
                Baseline = BaselineFixtures.reading }

        // The reading this test is about is the zero-test one, and it is now read once:
        // the incomplete reading above is a DIFFERENT reading with its own answer, not an
        // earlier attempt this one improves on.
        let outcome = CheckVerdict.verdict CheckVerdict.InnerLoop zeroTestInputs

        test <@ outcome = CheckVerdict.CheckOutcome.UnearnedScope(NoTestsRun NoTestsReason.AlreadyVerified) @>
        test <@ CheckVerdict.verdict CheckVerdict.InnerLoop initialInputs = CheckVerdict.CheckOutcome.Incomplete 1 @>

        let zeroTestExitCode =
            publishVerdict
                repoRoot
                []
                CheckVerdict.InnerLoop
                false
                (BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified))
                Verdict.NoReading
                Map.empty
                []
                ProjectModelFixtures.available
                (SettledTree.capture repoRoot [])
                outcome

        // The current invocation remains an unearned, exit-3 refusal; preservation is
        // about the durable evidence, not laundering this no-test run into a pass.
        test <@ zeroTestExitCode = 3 @>

        // THE claim: the file on disk is still the prior — its run id, its seven suite
        // entries, its tree hash, its plugin record, its green — not a no-test verdict
        // wearing the prior's applicability.
        let preserved =
            match Verdict.read repoRoot with
            | Verdict.Reading.Found v -> v
            | other -> failwithf "the preserved full-suite green must still be readable, got %A" other

        test <@ preserved.TreeHash = prior.TreeHash @>
        test <@ preserved.RunId = Some runId @>
        test <@ preserved.Suites = prior.Suites @>
        test <@ preserved.Suites |> List.map _.Project |> List.sort = projects @>
        test <@ preserved.Scope = FullSuite 7 @>
        test <@ BaselineFixtures.isGreen (preserved.Outcome) @>
        test <@ preserved.ExitCode = 0 @>
        test <@ preserved.Plugins = prior.Plugins @>
        test <@ Verdict.isFullSuiteGreen preserved @>
        // Byte for byte: preservation is the ABSENCE of a write, not a rewrite.
        test <@ System.IO.File.ReadAllText(Verdict.path repoRoot) = priorText @>

        match Verdict.priorConfirmation repoRoot [] with
        | Verdict.PriorConfirmation.StillApplies _ -> ()
        | Verdict.PriorConfirmation.MustEarn ->
            failwith "the zero-test re-scan erased a full-suite green that still applies")

[<Fact(Timeout = 15000)>]
let ``a zero-test convergence never preserves a full-suite green from a different tree`` () =
    // Preservation is evidence reuse, not a way for a no-test run to hide an edit.
    // Drive the publisher (rather than `priorConfirmation` alone) so this asserts the
    // no-write branch is unavailable when the prior full green has become stale.
    TestHelpers.withTempDir "ipcoutput-643-stale-prior-green" (fun repoRoot ->
        let src = System.IO.Path.Combine(repoRoot, "src")
        System.IO.Directory.CreateDirectory(src) |> ignore
        let tracked = System.IO.Path.Combine(src, "Tracked.fs")
        System.IO.File.WriteAllText(tracked, "module Tracked\nlet answer = 42\n")

        publishVerdict
            repoRoot
            []
            CheckVerdict.Confirmation
            false
            (BaselineFixtures.reportOf (FullSuite 1))
            Verdict.NoReading
            Map.empty
            []
            ProjectModelFixtures.available
            (SettledTree.capture repoRoot [])
            (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)
        |> ignore

        System.IO.File.WriteAllText(tracked, "module Tracked\nlet answer = 43\n")

        let exitCode =
            publishVerdict
                repoRoot
                []
                CheckVerdict.InnerLoop
                false
                (BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified))
                Verdict.NoReading
                Map.empty
                []
                ProjectModelFixtures.available
                (SettledTree.capture repoRoot [])
                (CheckVerdict.CheckOutcome.UnearnedScope(NoTestsRun NoTestsReason.AlreadyVerified))

        test <@ exitCode = 3 @>
        test <@ Verdict.priorConfirmation repoRoot [] = Verdict.PriorConfirmation.MustEarn @>

        match Verdict.read repoRoot with
        | Verdict.Reading.Found v ->
            test <@ v.Scope = NoTestsRun NoTestsReason.AlreadyVerified @>
            test <@ v.ExitCode = 3 @>
        | other -> failwithf "the stale full-suite green must be replaced, got %A" other)

let private publishPrior (repoRoot: string) (kind: string) =
    let publish scope outcome statuses =
        publishVerdict
            repoRoot
            []
            CheckVerdict.InnerLoop
            false
            (BaselineFixtures.reportOf scope)
            Verdict.NoReading
            statuses
            []
            ProjectModelFixtures.available
            (SettledTree.capture repoRoot [])
            outcome
        |> ignore

    match kind with
    | "missing" -> ()
    | "unreadable" ->
        let verdictPath = Verdict.path repoRoot

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName verdictPath)
        |> ignore

        System.IO.File.WriteAllText(verdictPath, "not a verdict")
    | "filtered" -> publish (ImpactFiltered(1, 2)) (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline) Map.empty
    | "red" ->
        let failedStatus =
            { Status = StatusView.Failed("prior failure", System.DateTime.UtcNow)
              Subtasks = []
              ActivityTail = []
              LastRun = None
              Diagnostics = DiagnosticCounts.empty }

        publish (FullSuite 1) CheckVerdict.CheckOutcome.FailuresFound (Map.ofList [ "build", failedStatus ])
    | "incomplete" -> publish (FullSuite 1) (CheckVerdict.CheckOutcome.Incomplete 1) Map.empty
    | "different-producer" ->
        publish (FullSuite 1) (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline) Map.empty
        let verdictPath = Verdict.path repoRoot
        let json = System.IO.File.ReadAllText verdictPath

        let changed =
            let pattern =
                System.Text.RegularExpressions.Regex("(\\\"contentHash\\\"\\s*:\\s*\\\")[^\\\"]+")

            pattern.Replace(
                json,
                System.Text.RegularExpressions.MatchEvaluator(fun m -> m.Groups[1].Value + String.replicate 64 "0"),
                1
            )

        test <@ changed <> json @>
        System.IO.File.WriteAllText(verdictPath, changed)
    | other -> failwithf "unknown prior kind %s" other

[<Theory(Timeout = 15000)>]
[<InlineData("different-producer")>]
[<InlineData("filtered")>]
[<InlineData("red")>]
[<InlineData("incomplete")>]
[<InlineData("unreadable")>]
[<InlineData("missing")>]
let ``a zero-test convergence replaces every prior that is not an applicable full-suite green`` (kind: string) =
    TestHelpers.withTempDir $"ipcoutput-643-replace-{kind}" (fun repoRoot ->
        publishPrior repoRoot kind

        let noTests = NoTestsRun NoTestsReason.AlreadyVerified

        let exitCode =
            publishVerdict
                repoRoot
                []
                CheckVerdict.InnerLoop
                false
                (BaselineFixtures.reportOf noTests)
                Verdict.NoReading
                Map.empty
                []
                ProjectModelFixtures.available
                (SettledTree.capture repoRoot [])
                (CheckVerdict.CheckOutcome.UnearnedScope noTests)

        test <@ exitCode = 3 @>

        match Verdict.read repoRoot with
        | Verdict.Reading.Found v ->
            test <@ v.Scope = noTests @>
            test <@ v.ExitCode = 3 @>
            test <@ not (Verdict.isFullSuiteGreen v) @>
        | other -> failwithf "the ineligible prior must be replaced by the zero-test verdict, got %A" other)

[<Fact>]
let ``ordinary outcomes do not inspect prior confirmation evidence`` () =
    let ordinaryOutcome = (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

    let preserved =
        priorVerdictToPreserve ordinaryOutcome "sha256:current-tree" FsHotWatch.TreeHash.Algorithm (fun () ->
            failwith "ordinary publication must not read and hash the prior verdict")

    test <@ preserved = None @>

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
            (BaselineFixtures.reportOf (FullSuite 1))
            Verdict.NoReading
            Map.empty
            []
            ProjectModelFixtures.available
            (SettledTree.capture repoRoot [])
            (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)
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
                    """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}""")
                (fun () -> BaselineFixtures.reportOf (ImpactFiltered(1, 3)))
                (fun () -> IpcParsing.ReachUnavailable "must not be read")
                (fun () -> forcedRuns <- forcedRuns + 1)

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

// ---------------------------------------------------------------------------
// A run that FINISHED and could not be received is its own answer.
//
// The shape of the incident, exactly: `WaitForComplete` returns (the daemon has built,
// run the suite and committed its evidence), and the very next call — the diagnostics
// read — dies for want of memory. That used to reach the caller as a bare exit 2 with
// no verdict written, so the file on disk stayed whatever the previous run left; seven
// completed runs in a row read back as an earlier refusal stub for a different tree.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``isMemoryExhaustion recognises memory exhaustion on EITHER side of the wire`` () =
    test <@ isMemoryExhaustion (System.OutOfMemoryException("Insufficient memory")) @>
    // The transcoder's refusal of an over-large string token is the same fact.
    test <@ isMemoryExhaustion (System.OverflowException("too large")) @>
    // Through the inner chain, which is how it arrives once a Task has wrapped it.
    test <@ isMemoryExhaustion (exn ("outer", System.OutOfMemoryException("inner"))) @>

    // A daemon-side fault crosses as RemoteInvocationException and is recognised by the
    // remote payload's TYPE — never by its message, which is a localized framework
    // resource string.
    let remote =
        StreamJsonRpc.RemoteInvocationException(
            "Insufficient memory to continue the execution of the program.",
            0,
            null,
            StreamJsonRpc.Protocol.CommonErrorData(
                TypeName = "System.OutOfMemoryException",
                Message = "Insufficient memory to continue the execution of the program."
            )
        )

    test <@ isMemoryExhaustion remote @>

[<Fact(Timeout = 15000)>]
let ``isMemoryExhaustion ignores unrelated faults`` () =
    test <@ not (isMemoryExhaustion (exn "boom")) @>
    test <@ not (isMemoryExhaustion (System.IO.IOException("pipe is broken"))) @>
    test <@ not (isMemoryExhaustion (System.TimeoutException("WaitForComplete timed out"))) @>

[<Fact(Timeout = 20000)>]
let ``pollAndRender returns exit 7 and PUBLISHES when the result is lost after the run settled`` () =
    // Settle succeeds — the daemon finished the run — and the diagnostics read then dies.
    let getErrors () : string =
        raise (System.OutOfMemoryException("Insufficient memory to continue the execution of the program."))

    let exitCode, published =
        TestHelpers.withTempDir "ipcoutput" (fun repoRoot ->
            let code =
                pollAndRender
                    ProgressRenderer.Agent
                    CheckVerdict.InnerLoop
                    repoRoot
                    []
                    (fun _ -> [])
                    false
                    (fun () -> "idle")
                    (fun () -> "{}") // waitForComplete SUCCEEDS: the run is done
                    (fun () -> "{}")
                    getErrors
                    (fun () -> BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                    (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                    ignore

            code, Verdict.read repoRoot)

    // NOT 2. The work was done; a caller that re-runs on this is paying twice.
    test <@ exitCode = 7 @>

    // And the finished run leaves a verdict behind rather than the previous run's.
    match published with
    | Verdict.Reading.Found verdict ->
        test <@ verdict.ExitCode = 7 @>

        match verdict.Outcome with
        | Verdict.Incomplete reason ->
            // The words point at the evidence, not at another twenty minutes.
            test <@ reason.Contains ".fshw/test-runs/" @>
            test <@ reason.Contains "FINISHED" @>
        | other -> failwithf "expected an incomplete outcome, got %A" other
    | other -> failwithf "expected a published verdict, got %A" other

[<Fact(Timeout = 20000)>]
let ``a memory fault BEFORE the run settles is NOT claimed as a lost result`` () =
    // The distinction the new exit code exists to carry, in the other direction. Dying
    // on the way IN is a run that never happened, and calling that "the result was lost"
    // would be the same lie pointed the other way. So this arm does not fire: the fault
    // propagates to the caller's un-completable exit-2 path exactly as it always did.
    let waitForComplete () : string =
        raise (System.OutOfMemoryException("Insufficient memory to continue the execution of the program."))

    TestHelpers.withTempDir "ipcoutput-early" (fun repoRoot ->
        raises<System.OutOfMemoryException>
            <@
                pollAndRender
                    ProgressRenderer.Agent
                    CheckVerdict.InnerLoop
                    repoRoot
                    []
                    (fun _ -> [])
                    false
                    (fun () -> "idle")
                    waitForComplete
                    (fun () -> "{}")
                    (fun () ->
                        """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}""")
                    (fun () -> BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                    (fun () -> IpcParsing.ReachUnavailable "this drive offers no projection")
                    ignore
            @>

        // And nothing was published: there is no finished run to record.
        match Verdict.read repoRoot with
        | Verdict.Reading.Found v -> failwithf "expected no verdict, got %A" v.Outcome
        | _ -> ())

// A moved tree refuses retained evidence — including a move that touches ONLY a declared
// verdict input. This rode the convergence re-scan, which was the thing that moved the
// tree mid-drive; with the loop gone there is no second reading to move it under. The
// property is the store's, and it is stated here directly: evidence is keyed to the tree
// it was earned on, so a quiet read taken over a DIFFERENT tree earns nothing.
[<Theory(Timeout = 15000)>]
[<InlineData(false, "already-verified")>]
[<InlineData(true, "already-verified")>]
[<InlineData(false, "unstated")>]
[<InlineData(true, "unstated")>]
let ``a moved tree refuses retained evidence, even when only a declared input moved``
    (declaredInput: bool, quietRead: string)
    =
    TestHelpers.withTempDir "exact-tree-refusal" (fun repoRoot ->
        let quietReason =
            match quietRead with
            | "unstated" -> NoTestsReason.Unstated
            | _ -> NoTestsReason.AlreadyVerified

        let source = System.IO.Path.Combine(repoRoot, "src", "Candidate.fs")

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName source)
        |> ignore

        System.IO.File.WriteAllText(source, "module Candidate\nlet value = 1\n")
        let policy = System.IO.Path.Combine(repoRoot, "coverage-policy.txt")
        System.IO.File.WriteAllText(policy, "floor=80\n")

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(repoRoot, ".fshw.json"),
            """{"verdictInputs":{"hashed":[{"path":"coverage-policy.txt","why":"test coverage policy affects the gate"}]}}"""
        )

        // The tree the executed run was earned on.
        let before = FsHotWatch.TreeHash.compute repoRoot []

        let earnedOn =
            VerifiedTree
                { FsHotWatch.TreeHash.Hash = before.Hash
                  FileCount = before.FileCount
                  SkippedCount = before.SkippedCount
                  DeclaredCount = before.DeclaredCount
                  AbsentDeclarationCount = before.AbsentDeclarationCount }

        let _, retained = TestRunEvidence.reconcile earnedOn executedA None
        test <@ retained.IsSome @>

        // Now move the tree. `declaredInput` moves ONLY the declared policy file, which
        // changes no symbol and no source byte — the case that used to slip through.
        if declaredInput then
            System.IO.File.WriteAllText(policy, "floor=90\n")
        else
            System.IO.File.AppendAllText(source, "// exact tree changed without changing symbol bodies\n")

        let after = FsHotWatch.TreeHash.compute repoRoot []
        test <@ before.Hash <> after.Hash @>

        let movedTo =
            VerifiedTree
                { FsHotWatch.TreeHash.Hash = after.Hash
                  FileCount = after.FileCount
                  SkippedCount = after.SkippedCount
                  DeclaredCount = after.DeclaredCount
                  AbsentDeclarationCount = after.AbsentDeclarationCount }

        let quiet = BaselineFixtures.reportOf (NoTestsRun quietReason)
        let effective, retainedAfter = TestRunEvidence.reconcile movedTo quiet retained

        // The quiet read is graded as itself — nothing ran on THIS tree — and the earlier
        // run is no longer retained, so no later read can borrow it either.
        test <@ effective = quiet @>
        test <@ effective.RunId <> executedA.RunId @>
        test <@ retainedAfter = None @>

        // And the verdict such a reading earns is the explicit non-evidence one: exit 3,
        // naming the quiet reason rather than the run it could not use.
        let reading: CheckVerdict.CheckInputs =
            { PluginStatuses = Map.empty
              FailingDiagnostics = 0
              UnattributableDiagnostics = 0
              WaitingOnBuild = CheckVerdict.BuildWait.NotWaiting
              RunnerAborted = CheckVerdict.RunnerAbort.NoAbort
              Coverage = Complete
              Scope = effective.Scope
              Baseline = BaselineFixtures.reading
              ProjectModel = ProjectModelFixtures.available }

        let outcome = CheckVerdict.verdict CheckVerdict.InnerLoop reading
        test <@ outcome = CheckVerdict.CheckOutcome.UnearnedScope(NoTestsRun quietReason) @>
        test <@ CheckVerdict.exitCode outcome = 3 @>)

// ---------------------------------------------------------------------------
// A green needs the graded run's receipt for the current project model.
// ---------------------------------------------------------------------------

/// Publish an otherwise-clean reading with exactly `receipts` served beside it.
let private publishWithReceipts
    (repoRoot: string)
    (receipts: IpcParsing.ModelReceipt list)
    (runReport: IpcParsing.TestRunReport)
    =
    publishVerdictForInvocation
        (Verdict.Invocation.start ())
        repoRoot
        []
        CheckVerdict.InnerLoop
        false
        runReport
        Verdict.NoReading
        Map.empty
        (IpcParsing.DaemonEvidence.Served([], IpcParsing.ReceiptLedger.Offered receipts))
        []
        ProjectModelFixtures.available
        (SettledTree.capture repoRoot [])
        (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

/// The generation `ProjectModelFixtures.available` reports.
let private currentGeneration = 7L

[<Theory(Timeout = 20000)>]
[<InlineData("matching", 0)>]
[<InlineData("missing", 2)>]
[<InlineData("different-run", 2)>]
[<InlineData("analysis-only", 2)>]
[<InlineData("different-model", 2)>]
[<InlineData("refused", 2)>]
let ``green publication requires the graded run's current model receipt`` (kind: string) (expectedExit: int) =
    withTempDir "ipcoutput-model-receipt" (fun repoRoot ->
        let gradedRun =
            { BaselineFixtures.reportOf (FullSuite 1) with
                RunId = Some BaselineFixtures.runId }

        let receipts =
            if kind = "missing" then
                []
            else
                [ { RunId =
                      match kind with
                      | "analysis-only" -> None
                      | "different-run" -> Some(System.Guid.NewGuid())
                      | _ -> Some BaselineFixtures.runId
                    Generation =
                      if kind = "different-model" then
                          currentGeneration + 1L
                      else
                          currentGeneration
                    Refusals =
                      if kind = "refused" then
                          [ "1 verification obligation(s) remain pending" ]
                      else
                          [] } ]

        test <@ publishWithReceipts repoRoot receipts gradedRun = expectedExit @>)

[<Theory(Timeout = 20000)>]
[<InlineData("matching", 0)>]
[<InlineData("missing", 2)>]
[<InlineData("test-run", 2)>]
[<InlineData("different-model", 2)>]
[<InlineData("refused", 2)>]
let ``analysis-only green requires its own completed model receipt`` (kind: string) (expectedExit: int) =
    withTempDir "ipcoutput-analysis-receipt" (fun repoRoot ->
        // No run is named: this daemon runs no tests, so only its own receipt can vouch.
        let analysisOnly = BaselineFixtures.reportOf (FullSuite 1)

        let receipts =
            if kind = "missing" then
                []
            else
                [ { RunId =
                      if kind = "test-run" then
                          Some BaselineFixtures.runId
                      else
                          None
                    Generation =
                      if kind = "different-model" then
                          currentGeneration + 1L
                      else
                          currentGeneration
                    Refusals =
                      if kind = "refused" then
                          [ "Lib.fs: no completed analysis for the current model" ]
                      else
                          [] } ]

        test <@ publishWithReceipts repoRoot receipts analysisOnly = expectedExit @>)

[<Fact(Timeout = 20000)>]
let ``a daemon that serves no receipts keeps its verdict, so the analysis-only path cannot silently ungate`` () =
    withTempDir "ipcoutput-receipts-not-served" (fun repoRoot ->
        // A daemon older than the receipt rule says nothing about evidence. This gate
        // cannot speak about what it was never sent, so the reading keeps its outcome —
        // and this test is what stops that skew path from being widened by accident.
        let gradedRun =
            { BaselineFixtures.reportOf (FullSuite 1) with
                RunId = Some BaselineFixtures.runId }

        let exitCode =
            publishVerdictForInvocation
                (Verdict.Invocation.start ())
                repoRoot
                []
                CheckVerdict.InnerLoop
                false
                gradedRun
                Verdict.NoReading
                Map.empty
                IpcParsing.DaemonEvidence.NotServed
                []
                ProjectModelFixtures.available
                (SettledTree.capture repoRoot [])
                (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

        test <@ exitCode = 0 @>

        // A daemon that DOES serve receipts, and serves none for this run, is refused —
        // the pair is the point: the skew path is narrow and deliberate.
        test <@ publishWithReceipts repoRoot [] gradedRun = 2 @>)

[<Fact(Timeout = 20000)>]
let ``a refused receipt says why at the terminal, not only in the verdict file`` () =
    withTempDir "ipcoutput-receipt-terminal" (fun repoRoot ->
        let gradedRun =
            { BaselineFixtures.reportOf (FullSuite 1) with
                RunId = Some BaselineFixtures.runId }

        let stderr, exitCode =
            captureStderr (fun () -> publishWithReceipts repoRoot [] gradedRun)

        test <@ exitCode = 2 @>
        test <@ stderr.Contains "no evidence receipt" @>

        // The control: a receipt that earns the green says nothing.
        let quiet, greenExit =
            captureStderr (fun () ->
                publishWithReceipts
                    repoRoot
                    [ { RunId = Some BaselineFixtures.runId
                        Generation = currentGeneration
                        Refusals = [] } ]
                    gradedRun)

        test <@ greenExit = 0 @>
        test <@ not (quiet.Contains "no evidence receipt") @>)

[<Theory(Timeout = 20000)>]
[<InlineData("no-receipt")>]
[<InlineData("refusing-receipt")>]
let ``a receipt refusal is a recorded cause, so the summary names it instead of UNEXPLAINED`` (kind: string) =
    withTempDir "ipcoutput-receipt-cause" (fun repoRoot ->
        // The cold-tree RED on main: the refusal was printed, the exit was 2, and the
        // WHAT FAILED block said "UNEXPLAINED exit 2 with no failing plugin, no failing
        // suite and no failing diagnostic — do NOT read this as a pass". The block
        // collects its causes from the verdict, and nothing put the refusal there.
        let gradedRun =
            { BaselineFixtures.reportOf (FullSuite 1) with
                RunId = Some BaselineFixtures.runId }

        let receipts =
            if kind = "no-receipt" then
                []
            else
                [ { RunId = Some BaselineFixtures.runId
                    Generation = currentGeneration
                    Refusals = [ "3 verification obligation(s) remain pending" ] } ]

        test <@ publishWithReceipts repoRoot receipts gradedRun = 2 @>

        match Verdict.read repoRoot with
        | Verdict.Reading.Found verdict ->
            let causes =
                verdict.RedCauses
                |> List.map (fun c -> Verdict.RedCauseMessage.value c.Message)
                |> String.concat "\n"

            let summary =
                ProgressRenderer.AgentHints.forVerdict (FsHotWatch.Ctrf.runExists repoRoot) None verdict
                |> String.concat "\n"

            test <@ not (List.isEmpty verdict.RedCauses) @>
            test <@ causes.Contains "receipt" @>
            test <@ not (summary.Contains "UNEXPLAINED") @>
            test <@ summary.Contains "receipt" @>
        | other -> failwithf "expected a published verdict, got %A" other)

[<Fact(Timeout = 20000)>]
let ``a clean publication records no cause, so the refusal cause is not furniture`` () =
    withTempDir "ipcoutput-receipt-cause-control" (fun repoRoot ->
        // Negative control for the theory above: without it, recording a cause on every
        // publication would satisfy it while telling every green run something failed.
        let gradedRun =
            { BaselineFixtures.reportOf (FullSuite 1) with
                RunId = Some BaselineFixtures.runId }

        let exitCode =
            publishWithReceipts
                repoRoot
                [ { RunId = Some BaselineFixtures.runId
                    Generation = currentGeneration
                    Refusals = [] } ]
                gradedRun

        test <@ exitCode = 0 @>

        match Verdict.read repoRoot with
        | Verdict.Reading.Found verdict -> test <@ List.isEmpty verdict.RedCauses @>
        | other -> failwithf "expected a published verdict, got %A" other)

[<Fact(Timeout = 30000)>]
let ``a verdict wait that RESOLVES on a no-model host still exits 2 with a named reason`` () =
    // The guarantee the deleted `requireVerdict` used to hold, re-pinned where a user can
    // see it. That guard made the daemon-side wait BLOCK on a host that had verified
    // nothing, so a cold daemon could not render a vacuous exit-0 "No errors". The wait no
    // longer blocks on a host that observes no project model — nothing can ever earn
    // evidence for a model that does not exist, so blocking would spend the client's whole
    // deadline on an answer already determined.
    //
    // Resolving fast must therefore never become GREEN fast, and this is the test that
    // stops a refactor from turning one into the other: the wait resolves, AND the check
    // over that same no-model reading still exits 2 carrying a sentence that names the
    // cause. Both halves in one test, deliberately — apart, either could keep passing
    // while the pair stopped being true.
    withTempDir "no-model-resolves-then-refuses" (fun repoRoot ->
        let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value repoRoot

        host.RegisterHandler
            { Name = FsHotWatch.PluginFramework.PluginName.create "never-runs"
              Init = ()
              Update = fun _ state _ -> async { return state }
              Commands = []
              Subscriptions = Set.singleton FsHotWatch.PluginFramework.SubscribeFileChanged
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        // Half one: no model was ever observed, and the wait resolves rather than blocking.
        test <@ host.WorkSnapshot.ProjectModel = FsHotWatch.ProjectModel.Observation.Unobserved @>

        FsHotWatch.Daemon.waitForVerdict host (System.TimeSpan.FromSeconds 5.0) System.Threading.CancellationToken.None
        |> fun waiting -> waiting.GetAwaiter().GetResult()

        // Half two: grading that same reading is exit 2 with the cause in words, not a
        // green and not a bare code.
        let exitCode, reading, file =
            driveWithModel
                CheckVerdict.InnerLoop
                (BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                (diagnosticsWithModel (Some FsHotWatch.ProjectModel.Observation.Unobserved))

        test <@ exitCode = 2 @>
        test <@ file.OutcomeKind = "model-unavailable" @>

        match reading with
        | Verdict.Reading.Found v ->
            match v.Outcome with
            | Verdict.ModelUnavailable reason ->
                test <@ reason.Contains "NO VERDICT" @>
                // The sentence that does the work: this is not "nothing needed checking".
                test <@ reason.Contains "not an empty test selection" @>
            | other -> failwithf "a no-model reading must refuse in words, got %A" other
        | other -> failwithf "the refusal must still be a readable verdict, got %A" other)

// ---------------------------------------------------------------------------
// ONE SETTLED READ IS THE ANSWER.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``an incomplete read is the answer: one read, no re-scan, even where a later read would differ`` () =
    withTempDir "ipcoutput-one-settled-read" (fun repoRoot ->
        // The convergence loop re-scanned an incomplete-but-clean reading up to three
        // times and took whichever later read looked better. It held no second opinion
        // about what a read MEANS — every attempt went through the same `verdict` — so all
        // it did was hope the next answer differed.
        //
        // The scripted reads below are the shape that made it look useful: the first says
        // one file is unchecked, and a second would say none. Only one of those is a
        // settled reading — `settle()` returns when the host owns no work AND holds
        // evidence for the current model, so the read taken after it is the answer. A
        // later read that differs is a DIFFERENT tree's answer, not a better one about
        // this tree, and taking it is how a check reported green about a state it never
        // verified.
        //
        // This test fails if a re-read can still change the verdict: it scripts a second
        // read that would convert exit 2 into exit 0, and requires that it is never taken.
        let mutable errorReads = 0
        let mutable rescans = 0

        let complete =
            """{"count":0,"files":{},"statuses":{},"unchecked":0, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}"""

        let incomplete =
            """{"count":0,"files":{},"statuses":{},"unchecked":1, "projectModel":{"schema":"fshw-project-model-v1","status":"available","generation":7,"counts":{"discovered":3,"loaded":3,"optionsMapped":3,"registered":3},"reasonCode":null}}"""

        let getErrors () =
            errorReads <- errorReads + 1
            if errorReads = 1 then incomplete else complete

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
                (fun () -> BaselineFixtures.reportOf (IpcParsing.FullSuite 1))
                (fun () -> IpcParsing.ReachUnavailable "not used")
                (fun () -> failwith "an inner-loop check must not force a full run")

        // Incomplete coverage is the answer, and it is exit 2 — "could not complete, retry".
        test <@ exitCode = 2 @>
        // Nothing was re-scanned, and the read that would have flipped it was never taken.
        test <@ rescans = 0 @>
        test <@ errorReads = 1 @>)
