/// Run scope: a run may clear only what it covered (AUTOMATION-125), the report evidence
/// that says what a run actually executed, and the stale-artifact preflight that settles
/// the ORDER in which staleness is asked about (AUTOMATION-201).
///
/// Split out of `TestPrunePluginTests`; shared harness in `TestPrunePluginTestSupport`.
module FsHotWatch.Tests.TestPruneRunScopeTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.SymbolDiff
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.TestPrune
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestPrunePluginTestSupport

// =============================================================================
// AUTOMATION-125 — a run may clear ONLY what it COVERED.
//
// Live, 2026-07-14: a full run failed one project; a queued impact-filtered re-run then
// executed a NARROWER selection, passed, and — via `ClearAllErrors` plus
// last-cycle-wins — superseded the red. The failing test never re-ran and never passed,
// yet `check` went green: "no failures reported by THIS run" read as "no failures".
//
// These drive the plugin's real `Custom(TestsFinished)` handler through a recording
// `PluginCtx` — the exact seam where the laundering happened. Both directions are pinned:
// a disjoint filtered green must NOT clear, and a COVERING filtered green MUST, or the
// fix becomes AUTOMATION-99's permanent stuck-red.
// =============================================================================

/// Recording ctx over the plugin: captures terminal statuses and models the shared error
/// ledger, where ClearAllErrors wipes the plugin's whole slice exactly as
/// `PluginFramework` does via `ClearPlugin`.
let private makeTestPruneRecordingCtx () =
    let statuses = System.Collections.Generic.List<PluginStatus>()

    let ledger =
        System.Collections.Generic.Dictionary<string, FsHotWatch.ErrorLedger.ErrorEntry list>()

    let ctx: FsHotWatch.PluginFramework.PluginCtx<TestPruneMsg> =
        { ReportStatus = fun s -> statuses.Add s
          ReportErrors = fun file entries -> ledger.[file] <- entries
          ClearErrors = fun file -> ledger.Remove(file) |> ignore
          ClearAllErrors = fun () -> ledger.Clear()
          EmitBuildCompleted = fun _ -> ()
          EmitTestRunStarted = fun _ -> ()
          EmitTestProgress = fun _ -> ()
          EmitTestRunCompleted = fun _ -> ()
          EmitCommandCompleted = fun _ -> ()
          Checker = Unchecked.defaultof<_>
          RepoRoot = ""
          Post = fun _ -> ()
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = fun _ -> ()
          Log = fun _ -> ()
          CompleteWithTimeout = fun _ -> ()
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          RunExclusiveShared = fun _ _ _ _ _ -> FsHotWatch.PluginFramework.SharedClaimed
          IsRunning = fun _ -> false
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    ctx, statuses, ledger

let private a125Config (project: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = "-c \"exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = Some "-- --filter-class {classes}"
      ClassJoin = "|"
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

/// A `TestsFinished` for a completed run: per-project results plus the SCOPE it was
/// launched against.
let private testsFinishedEvent (results: (string * TestResult) list) (launch: TestRunLaunch) =
    let runId = Guid.NewGuid()

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = Map.ofList results
          Verification = RunVerification.ofResults (Map.ofList results) }

    Custom(TestsFinished(started, completed, launch))

/// The runner's own wording for a failing test, so `parseFailedTests` attributes the
/// red to the CLASS (`ProjATests`) exactly as it does in the field.
let private failedProjA =
    TestsFailed("failed FsHotWatch.Tests.ProjATests.boom (12ms)", false, TimeSpan.FromSeconds 1.0)

/// What `executeTests` records for a project impact analysis SKIPPED: a pass, marked
/// filtered, with no output and no elapsed. It proves nothing, and is the value the
/// `ClearAllErrors` path read as "ProjA is fine now".
let private impactSkipped = TestsPassed("", true, TimeSpan.Zero)

let private passed (filtered: bool) =
    TestsPassed("ok", filtered, TimeSpan.FromSeconds 1.0)

/// No per-test report evidence: the fail-closed default `RunCoverage.ofRun` reads
/// whenever a run wrote no readable, complete CTRF report. A raw `--filter` run under
/// this claims nothing.
let private noReportEvidence: Map<string, Set<string>> = Map.empty

/// Drive the plugin through a sequence of completed runs, returning the ctx recorders and
/// the final state. Starts from the handler's own `Init`, so every invariant the real
/// plugin carries in state is carried here too.
let private driveRuns
    (handler: FsHotWatch.PluginFramework.PluginHandler<TestPruneState, TestPruneMsg>)
    (runs: PluginEvent<TestPruneMsg> list)
    =
    let ctx, statuses, ledger = makeTestPruneRecordingCtx ()

    let final =
        runs
        |> List.fold (fun state ev -> handler.Update ctx state ev |> Async.RunSynchronously) handler.Init

    ctx, statuses, ledger, final

let private lastStatus (statuses: System.Collections.Generic.List<PluginStatus>) : PluginStatus =
    test <@ statuses.Count > 0 @>
    statuses.[statuses.Count - 1]

let private ledgerFilesOf
    (ledger: System.Collections.Generic.Dictionary<string, FsHotWatch.ErrorLedger.ErrorEntry list>)
    : string list =
    ledger
    |> Seq.filter (fun kv -> not kv.Value.IsEmpty)
    |> Seq.map (fun kv -> kv.Key)
    |> Seq.toList

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a DISJOINT impact-filtered green does NOT clear a failed project's red`` () =
    // The observed sequence exactly: a full run fails ProjA, then a queued
    // impact-filtered re-run selects only ProjB while ProjA is skipped and recorded as a
    // filtered pass.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let filteredRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; filteredRun ]

    // The red survives the green that never ran it.
    test <@ final.OutstandingFailures |> List.exists (fun f -> f.Project = "ProjA") @>
    test <@ ledgerFilesOf ledger |> List.exists (fun f -> f.Contains("ProjA")) @>

    // A failing ledger entry alone would already deny `check` its exit 0, but the plugin's
    // own status must not claim a green it did not earn either.
    match lastStatus statuses with
    | PluginStatus.Failed(msg, _, _) -> test <@ msg.Contains("ProjA") @>
    | other -> Assert.Fail($"a filtered green that never ran ProjA must not produce a green terminal, got %A{other}")

// `recordRunOutcome` is the sole producer of this plugin's terminal status and decides
// purely on `TestResult.isPassed` counts. `isPassed` is TRUE for `TestsNoMatch` by design
// — per project, a filter selecting nothing is not that project's failure — so
// `failed = 0 && deferred = 0` holds and the green branch fires unless the run-level
// question is asked explicitly. It reported "N passed, 0 failed in N projects" about N
// projects that executed no test at all.
//
// A zero match in ONE project remains a pass for that project: an impact selection naming
// no class in the Integration project must not fail it. Only the run-level verdict
// changes, and only when nothing matched anywhere.
[<Fact(Timeout = 20000)>]
let ``a run where every project matched zero tests is not a green terminal status`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let zeroMatch = TestsNoMatch("Zero tests ran", TimeSpan.FromSeconds 1.0)

    let run =
        testsFinishedEvent [ "ProjA", zeroMatch; "ProjB", zeroMatch ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        Assert.Fail(
            $"every project matched zero tests, so the run verified nothing and must not be green — got %A{verdict}"
        )
    | _ -> ()

// So the guard above cannot be satisfied by refusing greens generally.
[<Fact(Timeout = 20000)>]
let ``a run where one project matched zero tests and another really ran is still green`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let zeroMatch = TestsNoMatch("Zero tests ran", TimeSpan.FromSeconds 1.0)

    let run =
        testsFinishedEvent [ "ProjA", zeroMatch; "ProjB", passed true ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Completed _ -> ()
    | other -> Assert.Fail($"ProjB executed tests and passed, so this run IS green — got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a COVERING impact-filtered green DOES clear the red (no stuck-red)`` () =
    // The over-correction guard. A filtered run that DID execute the failing class and
    // passed it is real evidence; a check that can never go green again is not a fix, it
    // is a different bug.
    withTempDir "a125-covering-receipt" (fun repoRoot ->
        let handler =
            create ":memory:" repoRoot (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

        let fullRun =
            testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

        let runId = Guid.NewGuid()
        let reportDir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
        Directory.CreateDirectory(reportDir) |> ignore

        File.WriteAllText(
            Path.Combine(reportDir, "ProjA.ctrf.json"),
            """{"results":{"summary":{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0},"tests":[{"name":"FsHotWatch.Tests.ProjATests.boom","status":"passed","duration":1}]}}"""
        )

        let started: TestRunStarted =
            { RunId = runId
              StartedAt = DateTime.UtcNow }

        let results = Map.ofList [ "ProjA", passed true; "ProjB", impactSkipped ]

        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = TimeSpan.FromSeconds 1.0
              Outcome = Normal
              Results = results
              Verification = RunVerification.ofResults results }

        let coveringRun =
            Custom(TestsFinished(started, completed, filteredLaunch [ "ProjA", [ "FsHotWatch.Tests.ProjATests" ] ]))

        let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; coveringRun ]

        test <@ List.isEmpty final.OutstandingFailures @>
        test <@ List.isEmpty (ledgerFilesOf ledger) @>

        match lastStatus statuses with
        | PluginStatus.Completed _ -> ()
        | other -> Assert.Fail($"a filtered run that executed the failing class green must clear it, got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: an unfiltered re-run (test-rerun) clears an outstanding red`` () =
    // The escape hatch the rule leans on: `test-rerun` runs every project UNFILTERED, so
    // it covers everything and may clear anything. Without it the rule is a wedge.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let rerun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; rerun ]

    test <@ List.isEmpty final.OutstandingFailures @>
    test <@ List.isEmpty (ledgerFilesOf ledger) @>

    match lastStatus statuses with
    | PluginStatus.Completed _ -> ()
    | other -> Assert.Fail($"an unfiltered all-green re-run must clear the red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a filtered green over a DIFFERENT class in the same project does not clear it`` () =
    // Project granularity is not enough: a run that selected ProjA but filtered to a class
    // OTHER than the failing one executed the project without executing the failure, so
    // "ProjA passed" is true of that run and says nothing about the red.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA ] (fullSuiteLaunch [ "ProjA" ])

    let otherClassRun =
        testsFinishedEvent [ "ProjA", passed true ] (filteredLaunch [ "ProjA", [ "FsHotWatch.Tests.SomeOtherTests" ] ])

    let _ctx, statuses, _ledger, final = driveRuns handler [ fullRun; otherClassRun ]

    test
        <@
            final.OutstandingFailures
            |> List.exists (fun f -> f.Class = Some "FsHotWatch.Tests.ProjATests")
        @>

    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"a filtered green over a different class must not clear the red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: the zero-affected skip (0 ran, green) cannot launder an outstanding red`` () =
    // The likeliest laundering path in practice: after the failing run the next build
    // changes nothing relevant, so the skip gate completes "green, 0 ran".
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA ] (fullSuiteLaunch [ "ProjA" ])

    // The degenerate skip lifecycle: empty results, empty selection, Normal outcome.
    let skipRun = testsFinishedEvent [] emptyLaunch

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; skipRun ]

    test <@ final.OutstandingFailures |> List.exists (fun f -> f.Project = "ProjA") @>
    test <@ ledgerFilesOf ledger |> List.exists (fun f -> f.Contains("ProjA")) @>

    // `Failed`, specifically: the OUTSTANDING RED is what makes this one a red. A run
    // that executes nothing with NO red outstanding is non-green for a different reason
    // and in a different way — `Completed` carrying a verified-nothing verdict, which the
    // AUTOMATION-198 test below pins. Neither may be a green.
    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"a run that executed nothing must not launder an outstanding red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-198: a run that executed NO project records a verdict that says nothing was verified`` () =
    // The same degenerate skip lifecycle as the AUTOMATION-125 test above, minus the
    // outstanding red: empty results, empty selection, `Normal` outcome, nothing owed.
    // This is the state that put `✓ test-prune — 0 passed, 0 failed in 0 projects` on a
    // check that verified nothing and then (correctly) refused to certify it.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let _ctx, statuses, _ledger, _final =
        driveRuns handler [ testsFinishedEvent [] emptyLaunch ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        // The STATUS stays `Completed` — nothing failed, and a `Failed` would turn
        // `check`'s honest exit 3 (NO VERDICT) into an exit 1 (failures found). The
        // VERDICT is what has to stop reading as a pass; every renderer glyphs off it.
        test <@ RunSummary.saysNothingVerified verdict.Summary @>
        // Never the counts line: "0 passed, 0 failed" is a pass report, and `in 0
        // projects` is the only part of it that says otherwise.
        test <@ not (verdict.Summary.Contains "0 passed") @>
    | other -> Assert.Fail($"a run that executed nothing must complete, not fail, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-198: a run that DID execute keeps its counts verdict`` () =
    // Positive control for the test above: the same path, one project that actually ran.
    // Without this, "the verdict says nothing was verified" would pass just as well if
    // every run started claiming it.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let _ctx, statuses, _ledger, _final =
        driveRuns handler [ testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ]) ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        test <@ not (RunSummary.saysNothingVerified verdict.Summary) @>
        test <@ verdict.Summary.Contains "1 passed, 0 failed" @>
        test <@ verdict.Summary.Contains "in 1 projects" @>
    | other -> Assert.Fail($"a run that executed and passed must complete green, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-294: a run whose test HOST DIED completes as an ABORT, never as "N failed"`` () =
    // The RUN-LEVEL console half. `recordRunOutcome` used to fold an errored project into
    // `failedList` (`not isDeferred`), so a killed host produced `1 failed: ProjB` on the
    // status line and `PluginStatus.Failed` underneath it — a definite negative about a
    // project whose tests never finished running.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let aborted =
        TestsErrored "test host was KILLED by SIGKILL (exit 137) — it never reached its own exit"

    let run =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", aborted ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        // `Completed`, exactly like the pure-defer case: nothing FAILED, and a `Failed`
        // here would turn the honest exit 2 back into the exit 1 this ticket is about.
        // The SUMMARY is what carries the fact, and it says NOTHING VERIFIED — so no
        // renderer can glyph this run with a bare tick either.
        test <@ RunSummary.saysNothingVerified verdict.Summary @>
        test <@ verdict.Summary.Contains "ABORTED" @>
        test <@ verdict.Summary.Contains "ProjB" @>
        test <@ verdict.Summary.Contains "NOT a test failure" @>
        // The words that used to be there, and must not be.
        test <@ not (verdict.Summary.Contains "1 failed:") @>
    | other ->
        Assert.Fail(
            $"a run whose host was killed must complete as an ABORT, not fail as though a test broke — got %A{other}"
        )

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-294: THE OTHER DIRECTION — a run with a REAL failure still fails, and names the abort apart`` () =
    // The guard against inverting the lie. A genuine red alongside a killed host stays a
    // red: `PluginStatus.Failed`, "1 failed: ProjA". The abort is still NAMED — it proved
    // nothing and a reader must not add it to the failure count — but it neither
    // launders the failure nor is counted as one.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let aborted = TestsErrored "test host was KILLED by SIGKILL (exit 137)"

    let run =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", aborted ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Failed(msg, _, verdict) ->
        test <@ msg.Contains "1 failed: ProjA" @>
        // Named, and named as an abort — never rolled into the "1".
        test <@ msg.Contains "ABORTED" @>
        test <@ msg.Contains "ProjB" @>
        test <@ not (msg.Contains "2 failed") @>
        // The counts line agrees with the status line: one failure, one abort.
        test <@ verdict.Summary.Contains "1 failed" @>
        test <@ verdict.Summary.Contains "1 ABORTED" @>
    | other -> Assert.Fail($"a real failure beside an abort must stay a red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a TIMED-OUT project's red needs a WHOLE-project pass, not a class-filtered one`` () =
    // A project killed for being stuck is a fact about the PROJECT (`Class = None`), so
    // only a run that executed the project in full can clear it.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let timedOut =
        testsFinishedEvent
            [ "ProjA",
              TestsTimedOut(
                  "failed FsHotWatch.Tests.ProjATests.slow (1ms)",
                  TimeSpan.FromSeconds 60.0,
                  false,
                  TimeSpan.FromSeconds 60.0
              ) ]
            (fullSuiteLaunch [ "ProjA" ])

    let classFilteredGreen =
        testsFinishedEvent [ "ProjA", passed true ] (filteredLaunch [ "ProjA", [ "ProjATests" ] ])

    let _ctx, _statuses, _ledger, afterFiltered =
        driveRuns handler [ timedOut; classFilteredGreen ]

    // Even a green over the very class named in the timeout output does not clear it.
    test
        <@
            afterFiltered.OutstandingFailures
            |> List.exists (fun f -> f.Project = "ProjA" && f.Class = None)
        @>

    // A whole-project pass does.
    let wholeProjectGreen =
        testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ])

    let _ctx2, statuses2, _ledger2, afterFull =
        driveRuns handler [ timedOut; classFilteredGreen; wholeProjectGreen ]

    test <@ List.isEmpty afterFull.OutstandingFailures @>

    match lastStatus statuses2 with
    | PluginStatus.Completed _ -> ()
    | other -> Assert.Fail($"a whole-project pass must clear a timeout red, got %A{other}")

// --- RunCoverage: what a run is ENTITLED to clear (unit) ---

[<Fact(Timeout = 10000)>]
let ``RunCoverage: an impact-SKIPPED project (filtered pass, absent from the selection) covers nothing`` () =
    // The laundering vector in one assertion: the skip sentinel is a PASS, and reading it
    // as evidence is the bug.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjB", ProjectClasses(Set.ofList [ "ProjBTests" ]) ])
            (Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ])
            noReportEvidence

    test <@ not (RunCoverage.covers "ProjA" (Some "ProjATests") coverage) @>
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>
    test <@ RunCoverage.covers "ProjB" (Some "ProjBTests") coverage @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: an UNFILTERED result covers the whole project whatever the selection asked for`` () =
    // A project with selected classes but no `filterTemplate` runs in FULL: the RESULT is
    // the receipt, not the request.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectClasses(Set.ofList [ "OneClass" ]) ])
            (Map.ofList [ "ProjA", passed false ])
            noReportEvidence

    test <@ RunCoverage.covers "ProjA" (Some "AnyOtherClass") coverage @>
    test <@ RunCoverage.covers "ProjA" None coverage @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: a class-filtered pass covers ONLY the classes it ran`` () =
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectClasses(Set.ofList [ "Alpha" ]) ])
            (Map.ofList [ "ProjA", passed true ])
            noReportEvidence

    test <@ RunCoverage.covers "ProjA" (Some "Alpha") coverage @>
    test <@ not (RunCoverage.covers "ProjA" (Some "Beta") coverage) @>
    // Never a project-level red — an unparseable failure, or a timeout.
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: deferred, errored and zero-match results cover nothing — they ran no tests`` () =
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull; "ProjC", ProjectInFull ])
            (Map.ofList
                [ "ProjA", TestsDeferred "apphost not produced"
                  "ProjB", TestsErrored "no parseable report"
                  "ProjC", TestsNoMatch("no tests matched", TimeSpan.Zero) ])
            noReportEvidence

    test <@ not (RunCoverage.covers "ProjA" None coverage) @>
    test <@ not (RunCoverage.covers "ProjB" None coverage) @>
    test <@ not (RunCoverage.covers "ProjC" None coverage) @>
    test <@ coverage = Map.empty @>

[<Fact(Timeout = 10000)>]
let ``failuresOf: a TestsDeferred result is a Deferred-severity 'waiting on build' entry, never a failing Error`` () =
    // A deferred project's build artifact was not produced, so its tests did not run: it
    // must surface as a non-failing `Deferred` the verdict routes to Incomplete/exit 2.
    // As `errorWithDetail` (Error severity) it made the deploy preflight read a
    // build-ordering defer as a test failure.
    let deferred: TestResults =
        { Results = Map.ofList [ "ProjA", TestsDeferred "apphost not produced" ]
          Elapsed = TimeSpan.Zero }

    let entry = (failuresOf Map.empty deferred |> List.exactlyOne).Entry

    test <@ entry.Severity = FsHotWatch.ErrorLedger.Deferred @>
    test <@ FsHotWatch.ErrorLedger.ErrorEntry.isWaitingOnBuild entry @>
    // Never counted as a failure — in either warn-fail policy.
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing true entry) @>
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing false entry) @>
    test <@ entry.Message.ToLowerInvariant().Contains "waiting on build" @>

    // The reclassification is surgical to the defer case, not a blanket downgrade.
    let failed: TestResults =
        { Results = Map.ofList [ "ProjB", TestsFailed("Some.Test FAILED", false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let realFailures = failuresOf Map.empty failed
    test <@ not realFailures.IsEmpty @>

    test
        <@
            realFailures
            |> List.forall (fun f -> f.Entry.Severity = FsHotWatch.ErrorLedger.Error)
        @>

    test
        <@
            realFailures
            |> List.forall (fun f -> FsHotWatch.ErrorLedger.ErrorEntry.isFailing true f.Entry)
        @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: a raw --filter passthrough with NO report evidence claims no coverage`` () =
    // `run-tests --filter <raw>` launches every project in full but hands the runner an
    // arbitrary filter string: `wasFiltered` is true and the selection names no classes,
    // so the LAUNCH REQUEST can say nothing about its reach. With no report to ask either,
    // claim nothing. This is the floor AUTOMATION-225 builds on, not a replacement for it.
    let coverage =
        RunCoverage.ofRun (Map.ofList [ "ProjA", ProjectInFull ]) (Map.ofList [ "ProjA", passed true ]) noReportEvidence

    test <@ coverage = Map.empty @>

// --- AUTOMATION-225: a green re-run retires the red it just disproved ---

/// A CTRF report in the shape fshw's runners really emit: summary counts and the per-test
/// array, both nested under `results`. The summary is DERIVED from the entries unless
/// `declaredTotal` overrides it, so a fixture cannot accidentally disagree with itself —
/// only deliberately, to pin the truncation guard.
let private ctrfReportWithTotal (declaredTotal: int option) (tests: (string * string) list) : string =
    let count status =
        tests |> List.filter (fun (_, s) -> s = status) |> List.length

    let entries =
        tests
        |> List.map (fun (name, status) ->
            sprintf """{"name":%s,"status":"%s","duration":1}""" (JsonSerializer.Serialize<string>(name)) status)
        |> String.concat ","

    sprintf
        """{"results":{"summary":{"tests":%d,"passed":%d,"failed":%d,"pending":0,"skipped":%d,"other":%d},"tests":[%s]}}"""
        (declaredTotal |> Option.defaultValue tests.Length)
        (count "passed")
        (count "failed")
        (count "skipped")
        (count "other")
        entries

let private ctrfReport (tests: (string * string) list) : string = ctrfReportWithTotal None tests

/// A completed run whose CTRF reports sit exactly where the real runner writes them:
/// `<repoRoot>/.fshw/test-runs/<runId>/<Project>.ctrf.json`. That directory is the only
/// route by which a run's per-test evidence reaches the ledger, so these tests drive the
/// whole path rather than handing `ofRun` a hand-made map.
let private testsFinishedEventWithReports
    (repoRoot: string)
    (reports: (string * string) list)
    (results: (string * TestResult) list)
    (launch: TestRunLaunch)
    =
    let runId = Guid.NewGuid()
    let dir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    for project, json in reports do
        File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), json)

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = Map.ofList results
          Verification = RunVerification.ofResults (Map.ofList results) }

    Custom(TestsFinished(started, completed, launch))

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-67 d223: a quarantined method passing in the exact CTRF receipt retires its red`` () =
    withTempDir "a67-d223-reconcile" (fun repoRoot ->
        let project = "Intelligence.Tests.Integration"

        let className =
            "Intelligence.Tests.Integration.AuthThrottleAcceptanceTests+AuthThrottleAcceptanceTests"

        let methodName =
            "NEWS-804 an unthrottled request answers the same for a real address and an unknown one"

        let fullName = $"%s{className}.%s{methodName}"

        let handler =
            create ":memory:" repoRoot (Some [ a125Config project ]) None None None None []

        let failedRun =
            testsFinishedEvent
                [ project, TestsFailed($"failed %s{fullName} (496ms)", false, TimeSpan.FromMilliseconds 496.0) ]
                (fullSuiteLaunch [ project ])

        let passingRun =
            testsFinishedEventWithReports
                repoRoot
                [ project, ctrfReport [ fullName, "passed" ] ]
                [ project, TestsPassed("passed", true, TimeSpan.FromMilliseconds 496.0) ]
                (filteredLaunch [ project, [ className ] ])

        let _ctx, statuses, ledger, final = driveRuns handler [ failedRun; passingRun ]

        test <@ List.isEmpty final.OutstandingFailures @>
        test <@ List.isEmpty (ledgerFilesOf ledger) @>

        match lastStatus statuses with
        | PluginStatus.Completed _ -> ()
        | other -> Assert.Fail($"the exact previously failing method passed, so its red must retire; got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-67: a different passing method in the quarantined class cannot erase the red`` () =
    withTempDir "a67-method-receipt" (fun repoRoot ->
        let project = "Intelligence.Tests.Integration"
        let className = "Intelligence.Tests.Integration.UserAuthTests+UserAuthTests"
        let failedMethod = "POST /api/user/login/request with valid email returns 200"
        let failedName = $"%s{className}.%s{failedMethod}"
        let siblingName = $"%s{className}.another method passed"

        let handler =
            create ":memory:" repoRoot (Some [ a125Config project ]) None None None None []

        let failedRun =
            testsFinishedEvent
                [ project, TestsFailed($"failed %s{failedName} (178ms)", false, TimeSpan.FromMilliseconds 178.0) ]
                (fullSuiteLaunch [ project ])

        let siblingOnlyRun =
            testsFinishedEventWithReports
                repoRoot
                [ project, ctrfReport [ siblingName, "passed" ] ]
                [ project, TestsPassed("passed", true, TimeSpan.FromMilliseconds 20.0) ]
                (filteredLaunch [ project, [ className ] ])

        let _ctx, statuses, _ledger, final = driveRuns handler [ failedRun; siblingOnlyRun ]

        test
            <@
                final.OutstandingFailures
                |> List.exists (fun red -> red.Method = Some failedMethod)
            @>

        match lastStatus statuses with
        | PluginStatus.Failed _ -> ()
        | other -> Assert.Fail($"a sibling pass did not contradict the failed method and must stay red; got %A{other}"))

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a report's PASSED classes are the receipt a raw-filter run is credited with`` () =
    let report =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.signs in", "passed" ]

    test <@ passedClassesOfReport report = Set.ofList [ "Acme.Tests.BrowserIntegrationTests" ] @>

    test
        <@
            passedTestsOfReport report = Set.ofList
                [ "Acme.Tests.BrowserIntegrationTests", "loads the dashboard"
                  "Acme.Tests.BrowserIntegrationTests", "signs in" ]
        @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-67 recall denominator requires every CTRF failed row promised by the summary`` () =
    let complete =
        ctrfReport
            [ "Acme.Tests.OneTests.first failure", "failed"
              "Acme.Tests.TwoTests.second failure", "failed" ]

    test
        <@
            failedTestsOfReport complete = Some
                [ "Acme.Tests.OneTests", "first failure"
                  "Acme.Tests.TwoTests", "second failure" ]
        @>

    let omittedRow =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},
             "tests":[{"name":"Acme.Tests.OneTests.first failure","status":"failed","duration":1}]}}"""

    test <@ failedTestsOfReport omittedRow = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 non-executing and shared-infrastructure runs cannot produce exact recall evidence`` () =
    withTempDir "a111-typed-run-evidence" (fun repoRoot ->
        let runId = Guid.NewGuid()
        let project = "Database.Tests"

        let errored =
            { Results = Map.ofList [ project, TestsErrored "host exited 139" ]
              Elapsed = TimeSpan.Zero }

        match failedTestsOfRun repoRoot runId errored with
        | Error reason -> test <@ reason.Contains "did not produce an executable test result" @>
        | Ok evidence -> failwithf "errored host produced exact evidence: %A" evidence

        let dir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
        Directory.CreateDirectory dir |> ignore

        let report =
            """{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Database.Tests.Query.loads","status":"failed","duration":1,"message":"Npgsql.NpgsqlException: connection failed","trace":"System.Net.Sockets.SocketException"}]}}"""

        File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), report)

        let failed =
            { Results = Map.ofList [ project, TestsFailed("failed", false, TimeSpan.Zero) ]
              Elapsed = TimeSpan.Zero }

        match failedTestsOfRun repoRoot runId failed with
        | Error reason -> test <@ reason.Contains "shared infrastructure" @>
        | Ok evidence -> failwithf "shared-infrastructure failures produced selector evidence: %A" evidence)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 one typed infrastructure exception invalidates a mixed failure sample`` () =
    let mixed =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.Contract.asserts","status":"failed","message":"expected true"},{"name":"Database.Tests.Query.loads","status":"failed","trace":"Npgsql.NpgsqlException: connection failed"}]}}"""

    test <@ sharedInfrastructureFailureOfReport mixed = Some "NpgsqlException" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 infrastructure tokens in names and assertion messages are not exception evidence`` () =
    let assertionOnly =
        """{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.NpgsqlExceptionExamples.render","status":"failed","message":"expected the text SocketException"}]}}"""

    test <@ sharedInfrastructureFailureOfReport assertionOnly = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 structured exception objects and stack arrays carry infrastructure evidence`` () =
    let structured =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Database.Tests.One.loads","status":"failed","exception":{"type":"Npgsql.NpgsqlException","message":"connection failed"}},{"name":"Database.Tests.Two.loads","status":"failed","stack":["at connector","System.Net.Sockets.SocketException: refused"]}]}}"""

    test <@ sharedInfrastructureFailureOfReport structured = Some "NpgsqlException/SocketException" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 an assertion trace naming a SocketException test class is not infrastructure evidence`` () =
    let ordinaryTrace =
        """{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.SocketExceptionExamples.renders","status":"failed","trace":"at Api.Tests.SocketExceptionExamples.renders()"}]}}"""

    test <@ sharedInfrastructureFailureOfReport ordinaryTrace = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 longer identifiers containing exception type names are not infrastructure evidence`` () =
    let longerIdentifiers =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.One.asserts","status":"failed","trace":"at Npgsql.NpgsqlExceptionExamples.renders()"},{"name":"Api.Tests.Two.asserts","status":"failed","trace":"at System.Net.Sockets.SocketExceptionExamples.renders()"}]}}"""

    test <@ sharedInfrastructureFailureOfReport longerIdentifiers = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 malformed parseable CTRF structure becomes unknown evidence`` () =
    withTempDir "a111-malformed-structure" (fun repoRoot ->
        let runId = Guid.NewGuid()
        let project = "Database.Tests"
        let dir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), """{"results":{"tests":{}}}""")

        let failed =
            { Results = Map.ofList [ project, TestsFailed("failed", false, TimeSpan.Zero) ]
              Elapsed = TimeSpan.Zero }

        match failedTestsOfRun repoRoot runId failed with
        | Error _ -> ()
        | Ok evidence -> failwithf "malformed CTRF produced exact evidence: %A" evidence)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-67 completion publishes real CTRF recall through check-reach IPC`` () =
    withTempDir "a67-recall-ipc" (fun repoRoot ->
        let project = "Acme.Tests"
        let reachedClass = "Acme.Tests.ReachedTests"
        let missedClass = "Acme.Tests.MissedTests"

        let handler =
            create ":memory:" repoRoot (Some [ a125Config project ]) None None None None []

        let report =
            ctrfReport [ $"%s{reachedClass}.fails", "failed"; $"%s{missedClass}.also fails", "failed" ]

        let launch =
            { fullSuiteLaunch [ project ] with
                WouldHaveRun = Some(Map.ofList [ project, ProjectClasses(Set.ofList [ reachedClass ]) ]) }

        let completion =
            testsFinishedEventWithReports
                repoRoot
                [ project, report ]
                [ project,
                  TestsFailed(
                      $"failed %s{reachedClass}.fails (1ms)\nfailed %s{missedClass}.also fails (1ms)",
                      false,
                      TimeSpan.FromMilliseconds 2.0
                  ) ]
                launch

        let pluginCtx, _, _ = makeTestPruneRecordingCtx ()

        let state =
            handler.Update pluginCtx handler.Init completion |> Async.RunSynchronously

        let command = handler.Commands |> List.find (fst >> (=) "check-reach") |> snd

        let commandCtx: FsHotWatch.PluginFramework.CommandCtx<TestPruneMsg> =
            { RepoRoot = repoRoot
              Log = ignore
              Post = ignore
              IsRunning = fun _ -> false
              ProjectGraph = pluginCtx.ProjectGraph }

        let json = command commandCtx state [||] |> Async.RunSynchronously

        match FsHotWatch.Cli.IpcParsing.parseCheckReach json with
        | FsHotWatch.Cli.IpcParsing.ReachRecorded reading ->
            test <@ reading.Recall = FsHotWatch.Cli.IpcParsing.FailureRecallMeasured(1, 2, 1.0, false) @>
        | other -> Assert.Fail($"the completed run must publish a measured recall sample, got %A{other}"))

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a class that RAN AND FAILED is claimed by nothing, not even its own passes`` () =
    // Per-class judgement on that class's own evidence: a class with a failure is not
    // vindicated by its siblings' greens, and the sibling IS vindicated by its own.
    let report =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.signs in", "failed"
              "Acme.Tests.SmokeTests.pings", "passed" ]

    test <@ passedClassesOfReport report = Set.ofList [ "Acme.Tests.SmokeTests" ] @>

    // An `other` status is an individually-ERRORED test — not a pass, so not a receipt.
    let errored =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.explodes", "other" ]

    test <@ passedClassesOfReport errored = Set.empty @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a class with nothing but skips proves nothing; a skip beside a pass is neutral`` () =
    let allSkipped =
        ctrfReport [ "Acme.Tests.BrowserIntegrationTests.disabled for now", "skipped" ]

    test <@ passedClassesOfReport allSkipped = Set.empty @>

    // Neutral, not disqualifying: the unfiltered arm this refines returns
    // `CoveredWholeProject` for a full run whose report contains skips, so blocking a
    // class on a skip here would be stricter than the path it refines.
    let mixed =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.disabled for now", "skipped" ]

    test <@ passedClassesOfReport mixed = Set.ofList [ "Acme.Tests.BrowserIntegrationTests" ] @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: report evidence that is missing, unparseable or INCOMPLETE claims nothing`` () =
    // Fail CLOSED, four ways: a bug here must degrade to "the red stays red".
    test <@ passedClassesOfReport "" = Set.empty @>
    test <@ passedClassesOfReport "not json at all" = Set.empty @>

    // A per-test array with NO summary block was truncated or never flushed, so
    // completeness cannot be checked and it is not evidence.
    test <@ passedClassesOfReport """{"results":{"tests":[{"name":"A.BTests.c","status":"passed"}]}}""" = Set.empty @>

    // The dangerous case: a real report OMITS per-test entries for tests that threw a raw,
    // non-assertion exception while still counting them in the summary. Counting is the
    // only way to see the omission — a class could otherwise look all-green while one of
    // its tests exploded.
    let truncated =
        ctrfReportWithTotal
            (Some 3)
            [ "Acme.Tests.BrowserIntegrationTests.one", "passed"
              "Acme.Tests.BrowserIntegrationTests.two", "passed" ]

    test <@ passedClassesOfReport truncated = Set.empty @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a raw --filter run is credited with the classes its own report shows passing`` () =
    let evidence = Map.ofList [ "ProjA", Set.ofList [ "BrowserIntegrationTests" ] ]

    let coverage =
        RunCoverage.ofRun (Map.ofList [ "ProjA", ProjectInFull ]) (Map.ofList [ "ProjA", passed true ]) evidence

    test <@ RunCoverage.covers "ProjA" (Some "BrowserIntegrationTests") coverage @>

    // A class the report never mentions is untouched, and a PROJECT-level red (timeout,
    // errored, unparseable failure) still needs a full run.
    test <@ not (RunCoverage.covers "ProjA" (Some "SomeOtherTests") coverage) @>
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>

    // Still a FILTERED scope, never a whole-suite claim.
    test <@ not (RunCoverage.coversWholeSuite [ "ProjA" ] coverage) @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: report evidence never speaks for a project the run did not LAUNCH`` () =
    // The AUTOMATION-125 laundering vector re-checked against report evidence: ProjA was
    // impact-SKIPPED, and a report bearing its name must not resurrect it. The selection
    // is consulted BEFORE any evidence, and absence from it is final.
    let evidence = Map.ofList [ "ProjA", Set.ofList [ "ProjATests" ] ]

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjB", ProjectInFull ])
            (Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ])
            evidence

    test <@ not (RunCoverage.covers "ProjA" (Some "ProjATests") coverage) @>
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a TIMED-OUT project is credited with nothing, report or no report`` () =
    // Whatever a killed process managed to flush is not a receipt for anything.
    let evidence = Map.ofList [ "ProjA", Set.ofList [ "ProjATests" ] ]

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull ])
            (Map.ofList [ "ProjA", TestsTimedOut("killed", TimeSpan.FromSeconds 60.0, true, TimeSpan.FromSeconds 60.0) ])
            evidence

    test <@ coverage = Map.empty @>

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-225: a filtered re-run that PASSES retires the red it re-ran — and only that one`` () =
    // The deadlock, end to end. An environmental failure (`ERR_NETWORK_IO_SUSPENDED` after
    // a machine suspend) reds a class; `test-rerun --filter-class '*BrowserIntegrationTests'`
    // re-runs it and it passes — and the red STAYS, because the launch records every
    // project as `ProjectInFull` and the raw filter string's reach is unknowable. Sticky
    // forever; it blocked a production deploy three times.
    let repoRoot =
        Path.Combine(Path.GetTempPath(), "fshw-a225-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(repoRoot) |> ignore

    try
        let handler =
            create ":memory:" repoRoot (Some [ a125Config "ProjA" ]) None None None None []

        // A full run reds TWO classes: the environmental one, and an unrelated one.
        let fullRun =
            testsFinishedEvent
                [ "ProjA",
                  TestsFailed(
                      "failed Acme.Tests.BrowserIntegrationTests.loads the dashboard (12ms)\nfailed Acme.Tests.LedgerTests.balances (3ms)",
                      false,
                      TimeSpan.FromSeconds 1.0
                  ) ]
                (fullSuiteLaunch [ "ProjA" ])

        let _c1, _s1, _l1, afterFull = driveRuns handler [ fullRun ]

        test
            <@
                afterFull.OutstandingFailures |> List.map (fun f -> f.Class) |> List.sort = [ Some
                                                                                                  "Acme.Tests.BrowserIntegrationTests"
                                                                                              Some
                                                                                                  "Acme.Tests.LedgerTests" ]
            @>

        // A `ProjectInFull` selection plus an opaque filter string, exactly as
        // `commandForceRun` builds it: only the run's own report knows what executed.
        let filteredRerun =
            testsFinishedEventWithReports
                repoRoot
                [ "ProjA", ctrfReport [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed" ] ]
                [ "ProjA", passed true ]
                (fullSuiteLaunch [ "ProjA" ])

        let _c2, _s2, ledger, afterRerun = driveRuns handler [ fullRun; filteredRerun ]

        // The class that re-ran and passed is retired ...
        test
            <@
                not (
                    afterRerun.OutstandingFailures
                    |> List.exists (fun f -> f.Class = Some "Acme.Tests.BrowserIntegrationTests")
                )
            @>

        // ... and the red the re-run never touched is still standing, in the state AND in
        // the ledger the verdict reads.
        test <@ afterRerun.OutstandingFailures |> List.map (fun f -> f.Class) = [ Some "Acme.Tests.LedgerTests" ] @>

        let ledgerMessages =
            ledger
            |> Seq.collect (fun kv -> kv.Value)
            |> Seq.map (fun e -> e.Message)
            |> Seq.toList

        test <@ ledgerMessages |> List.exists (fun m -> m.Contains "LedgerTests") @>
        test <@ not (ledgerMessages |> List.exists (fun m -> m.Contains "BrowserIntegrationTests")) @>
    finally
        try
            Directory.Delete(repoRoot, true)
        with _ ->
            ()

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-225: a filtered re-run whose report is missing or garbage retires NOTHING`` () =
    // Fail closed at the top of the stack too: a defect here must cost a stuck red, never
    // a false green.
    let repoRoot =
        Path.Combine(Path.GetTempPath(), "fshw-a225-closed-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(repoRoot) |> ignore

    try
        let handler =
            create ":memory:" repoRoot (Some [ a125Config "ProjA" ]) None None None None []

        let fullRun =
            testsFinishedEvent
                [ "ProjA",
                  TestsFailed(
                      "failed Acme.Tests.BrowserIntegrationTests.loads the dashboard (12ms)",
                      false,
                      TimeSpan.FromSeconds 1.0
                  ) ]
                (fullSuiteLaunch [ "ProjA" ])

        // No report at all.
        let noReport =
            testsFinishedEventWithReports repoRoot [] [ "ProjA", passed true ] (fullSuiteLaunch [ "ProjA" ])

        let _c1, _s1, _l1, afterNoReport = driveRuns handler [ fullRun; noReport ]

        test
            <@
                afterNoReport.OutstandingFailures
                |> List.exists (fun f -> f.Class = Some "Acme.Tests.BrowserIntegrationTests")
            @>

        // An unparseable one.
        let garbage =
            testsFinishedEventWithReports
                repoRoot
                [ "ProjA", "{ this is not a ctrf report" ]
                [ "ProjA", passed true ]
                (fullSuiteLaunch [ "ProjA" ])

        let _c2, _s2, _l2, afterGarbage = driveRuns handler [ fullRun; garbage ]

        test
            <@
                afterGarbage.OutstandingFailures
                |> List.exists (fun f -> f.Class = Some "Acme.Tests.BrowserIntegrationTests")
            @>
    finally
        try
            Directory.Delete(repoRoot, true)
        with _ ->
            ()

// --- OutstandingFailure.carry: the ledger algebra (unit) ---

let private redIn (project: string) (cls: string option) =
    { Project = project
      Class = cls
      Method = cls |> Option.map (fun _ -> "failed method")
      File = $"<tests/%s{project}>"
      Entry = FsHotWatch.ErrorLedger.ErrorEntry.errorWithDetail $"%s{project} failed" "output" }

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.quarantine automatically adds prior red classes to the next impact selection`` () =
    let affected = Map.ofList [ "ProjA", [ "FreshTests" ]; "ProjB", [ "OtherTests" ] ]

    let quarantined =
        OutstandingFailure.quarantine
            (Set.ofList [ "ProjA"; "ProjB"; "ProjC" ])
            [ redIn "ProjA" (Some "PriorRedTests")
              redIn "ProjC" (Some "OnlyPriorRedTests") ]
            affected

    test <@ quarantined.["ProjA"] |> Set.ofList = Set.ofList [ "FreshTests"; "PriorRedTests" ] @>
    test <@ quarantined.["ProjB"] = [ "OtherTests" ] @>
    test <@ quarantined.["ProjC"] = [ "OnlyPriorRedTests" ] @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.quarantine promotes an unknown-scope prior red to the whole project`` () =
    let affected = Map.ofList [ "ProjA", [ "FreshTests" ]; "ProjB", [ "OtherTests" ] ]

    let quarantined =
        OutstandingFailure.quarantine
            (Set.ofList [ "ProjA"; "ProjB" ])
            [ redIn "ProjA" (Some "PriorRedTests"); redIn "ProjA" None ]
            affected

    test <@ List.isEmpty quarantined.["ProjA"] @>
    test <@ quarantined.["ProjB"] = [ "OtherTests" ] @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-87 browser red survives a following edit whose graph selects zero tests`` () =
    let project = "Intelligence.Tests.Integration"

    let browserClass =
        "Intelligence.Tests.Integration.Web.BrowserIntegrationTests+BrowserIntegrationTests"

    // The historical AUTOMATION-87 edit changed only browser-test waits. Its graph
    // selection was empty; that must not let an already-red browser class disappear.
    let graphSelectionAfterBrowserEdit: Map<string, string list> = Map.empty

    let selected =
        OutstandingFailure.quarantine
            (Set.singleton project)
            [ redIn project (Some browserClass) ]
            graphSelectionAfterBrowserEdit

    test <@ selected = Map.ofList [ project, [ browserClass ] ] @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: keeps an uncovered red, drops a covered-and-passed one`` () =
    let prior = [ redIn "ProjA" (Some "ProjATests"); redIn "ProjB" None ]

    // Covered ONLY ProjB, in full, and found nothing.
    let coverage: RunCoverage = Map.ofList [ "ProjB", CoveredWholeProject ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA"; "ProjB" ]) coverage Map.empty [] prior

    test <@ carried |> List.map (fun f -> f.Project) = [ "ProjA" ] @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: a covered project that failed AGAIN keeps exactly one red`` () =
    let prior = [ redIn "ProjA" (Some "ProjATests") ]
    let coverage: RunCoverage = Map.ofList [ "ProjA", CoveredWholeProject ]
    let found = [ redIn "ProjA" (Some "ProjATests") ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA" ]) coverage Map.empty found prior

    // Superseded by this run's own evidence: one entry, not two. Reds must not accumulate.
    test <@ carried.Length = 1 @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: a red for a project no longer configured is pruned, never wedged`` () =
    // A project dropped from `tests.projects` can never be covered again, so retaining its
    // red is a permanent stuck-red no command can clear.
    let prior = [ redIn "Removed" None; redIn "ProjA" None ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA" ]) RunCoverage.none Map.empty [] prior

    test <@ carried |> List.map (fun f -> f.Project) = [ "ProjA" ] @>

// --- The task cache must not launder it either ---

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125: no cache participation while a red is outstanding`` () =
    // Two roads to the same laundered green. A BuildCompleted that HITS a cached green
    // skips the handler, so no run happens and the red is replayed away. And a run that
    // passed everything it ran while carrying an uncovered red reports a FAILED terminal,
    // which written under a content merkle replays on a tree that has since been fixed.
    let started: TestRunStarted =
        { RunId = Guid.NewGuid()
          StartedAt = DateTime.UtcNow }

    let allPassed =
        Custom(
            TestsFinished(
                started,
                { RunId = started.RunId
                  TotalElapsed = TimeSpan.Zero
                  Outcome = Normal
                  Results = Map.ofList [ "ProjA", TestsPassed("ok", false, TimeSpan.Zero) ]
                  Verification = Ran RunScope.FullSuite },
                fullSuiteLaunch [ "ProjA" ]
            )
        )

    let keyFor (hasOutstanding: bool) (event: PluginEvent<TestPruneMsg>) =
        cacheKeyFor
            (fun () -> "symbols")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> None)
            (fun () -> hasOutstanding)
            (fun () -> true)
            event

    // A clean plugin keeps the green fast-path ...
    test <@ (keyFor false (BuildCompleted BuildSucceeded)).IsSome @>
    test <@ (keyFor false allPassed).IsSome @>

    // ... and an outstanding red means no replay and no write, on either arm.
    test <@ (keyFor true (BuildCompleted BuildSucceeded)).IsNone @>
    test <@ (keyFor true allPassed).IsNone @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125: confirm still rejects a filtered green as UnearnedScope`` () =
    // The fix must not weaken the gate it stands beside: the filtered re-run above still
    // classifies as a SUBSET, and a merge verdict built on a subset is `UnearnedScope`.
    let configs = [ a125Config "ProjA"; a125Config "ProjB" ]

    let filteredResults: TestResults =
        { Results = Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ]
          Elapsed = TimeSpan.FromSeconds 1.0 }

    // ProjA never ran and ProjB was launched under a CLASS FILTER, so the run's honest
    // reach is "some of ProjB" and nothing else.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectClasses(Set.ofList [ "SomeTests" ]) ])
            filteredResults.Results
            noReportEvidence

    match scopeOf (configs |> List.map (fun c -> c.Project)) coverage with
    | ScopeFiltered(ran, total) ->
        test <@ ran = 1 && total = 2 @>

        let outcome =
            FsHotWatch.Cli.CheckVerdict.verdict
                FsHotWatch.Cli.CheckVerdict.Confirmation
                { PluginStatuses = Map.empty
                  FailingDiagnostics = 0
                  UnattributableDiagnostics = 0
                  WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
                  RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
                  Coverage = FsHotWatch.Cli.IpcParsing.Complete
                  Scope = FsHotWatch.Cli.IpcParsing.ImpactFiltered(ran, total) }

        test <@ FsHotWatch.Cli.CheckVerdict.exitCode outcome = 3 @>
    | other -> Assert.Fail($"a run with a filtered project is not a full-suite scope, got %A{other}")

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125 x 129: a RAW-filter run with no report evidence claims NO coverage, so the gate sees no scope at all``
    ()
    =
    // Sharper than the case above: a raw filter's reach the LAUNCH REQUEST cannot express,
    // with no report to ask either, credits the run with NOTHING. That projects to
    // `ScopeNone`, not `ScopeFiltered`, and the CLI refuses it in either mode — strictly
    // safer than `classifyRunScope`, which called this a SUBSET (still exit 3, weaker
    // reason).
    let configs = [ a125Config "ProjA"; a125Config "ProjB" ]

    let rawFiltered: TestResults =
        { Results = Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ]
          Elapsed = TimeSpan.FromSeconds 1.0 }

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ])
            rawFiltered.Results
            noReportEvidence

    test <@ scopeOf (configs |> List.map (fun c -> c.Project)) coverage = ScopeNone 2 @>

    // NoTestsRun is UnearnedScope in the inner loop as well as in `confirm`.
    let noTestsRan: FsHotWatch.Cli.CheckVerdict.CheckInputs =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
          Coverage = FsHotWatch.Cli.IpcParsing.Complete
          Scope = FsHotWatch.Cli.IpcParsing.NoTestsRun FsHotWatch.Cli.IpcParsing.NoTestsReason.Unstated }

    let confirmed =
        FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.Confirmation noTestsRan

    let inner =
        FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.InnerLoop noTestsRan

    test <@ FsHotWatch.Cli.CheckVerdict.exitCode confirmed = 3 @>
    test <@ FsHotWatch.Cli.CheckVerdict.exitCode inner = 3 @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225 x 112: a raw-filter run WITH evidence is a FILTERED scope, and confirm still refuses it`` () =
    // Crediting a raw-filter run with what its report proves makes the scope projection
    // say what actually ran instead of "nothing ran". That is MORE evidence, not a weaker
    // gate: `confirm` still demands a full-suite scope and still exits 3. And `scopeOf`
    // stays a pure projection of `RunCoverage`, so the ledger and the verdict read the
    // same coverage.
    let configs = [ a125Config "ProjA"; a125Config "ProjB" ]

    let evidence = Map.ofList [ "ProjB", Set.ofList [ "BrowserIntegrationTests" ] ]

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ])
            (Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ])
            evidence

    test <@ scopeOf (configs |> List.map (fun c -> c.Project)) coverage = ScopeFiltered(1, 2) @>

    let filtered: FsHotWatch.Cli.CheckVerdict.CheckInputs =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
          Coverage = FsHotWatch.Cli.IpcParsing.Complete
          Scope = FsHotWatch.Cli.IpcParsing.ImpactFiltered(1, 2) }

    let confirmed =
        FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.Confirmation filtered

    test <@ FsHotWatch.Cli.CheckVerdict.exitCode confirmed = 3 @>

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a test run does not erase the unanalysable-file warning (AUTOMATION-113)`` () =
    // The same defect on a different diagnostic. The `TestsFinished` ledger rewrite
    // cleared this plugin's whole slice, so the FIRST test run after an analysis failure
    // dropped the warning that is supposed to deny the check its green verdict: the file
    // kept forcing full-suite runs (state) but nothing told anyone (ledger). The warning
    // leaves only when the CONDITION clears — the file analyses cleanly.
    //
    // The broken file is written to DISK (AUTOMATION-303 case 4). A path that does not
    // exist has no condition left to discharge and is pruned, so a fixture naming an
    // imaginary file would assert this invariant over an entry that is now correctly
    // dropped — and pass or fail for the wrong reason.
    withTempDir "tp-a125-unanalysable" (fun tmpDir ->
        let handler =
            create ":memory:" tmpDir (Some [ a125Config "ProjA" ]) None None None None []

        let brokenFile = Path.Combine(tmpDir, "src", "Broken.fs")
        Directory.CreateDirectory(Path.GetDirectoryName brokenFile) |> ignore
        File.WriteAllText(brokenFile, "module Broken\n")

        let broken =
            { RelPath = "src/Broken.fs"
              File = brokenFile
              Reason = "FS3520: unexpected doc comment" }

        let stateWithUnanalysable =
            { handler.Init with
                UnanalyzableFiles = Map.ofList [ broken.RelPath, broken ] }

        let ctx, _statuses, ledger = makeTestPruneRecordingCtx ()

        // The strongest green there is: a full-suite run in which everything passes.
        let greenRun =
            testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ])

        handler.Update ctx stateWithUnanalysable greenRun
        |> Async.RunSynchronously
        |> ignore

        let warnings =
            ledger
            |> Seq.collect (fun kv -> kv.Value)
            |> Seq.filter (fun e -> e.Severity = FsHotWatch.ErrorLedger.Warning)
            |> Seq.toList

        test <@ ledger.ContainsKey broken.File @>
        test <@ warnings |> List.exists (fun e -> e.Message.Contains("src/Broken.fs")) @>

        // ... and it DOES go once the file analyses cleanly and the state drops it.
        let ctx2, _statuses2, ledger2 = makeTestPruneRecordingCtx ()

        handler.Update ctx2 handler.Init greenRun |> Async.RunSynchronously |> ignore

        test <@ ledger2.Count = 0 @>)

[<Fact(Timeout = 10000)>]
let ``RunCoverage.coversWholeSuite: only every project, each in FULL, is a whole-suite claim`` () =
    // Answered from what the run EXECUTED, so there is one notion of scope in the system —
    // the same one the ledger clears by — rather than a parallel one that can drift.
    let projects = [ "ProjA"; "ProjB" ]

    let everything: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredWholeProject ]

    let oneFiltered: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredClasses(Set.ofList [ "X" ]) ]

    let oneMissing: RunCoverage = Map.ofList [ "ProjA", CoveredWholeProject ]

    test <@ RunCoverage.coversWholeSuite projects everything @>
    // A filtered project covered LESS than the suite, whatever its result said; so did a
    // skipped one; and a run of nothing is never evidence of everything.
    test <@ not (RunCoverage.coversWholeSuite projects oneFiltered) @>
    test <@ not (RunCoverage.coversWholeSuite projects oneMissing) @>
    test <@ not (RunCoverage.coversWholeSuite projects RunCoverage.none) @>
    test <@ not (RunCoverage.coversWholeSuite [] everything) @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage.coveredProjects: names exactly what the run executed`` () =
    let coverage: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredClasses(Set.ofList [ "X" ]) ]

    test <@ RunCoverage.coveredProjects coverage = Set.ofList [ "ProjA"; "ProjB" ] @>
    test <@ Set.isEmpty (RunCoverage.coveredProjects RunCoverage.none) @>

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: the last run's coverage is readable from state (a verdict writer's receipt)`` () =
    // `LastResults` says what the run FOUND; `LastCoverage` says what it COVERED. A
    // consumer outside the handler must be able to read the second, or it invents its own
    // answer to "what did this run cover?" and the two drift.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let filteredRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let _ctx, _statuses, _ledger, final = driveRuns handler [ filteredRun ]

    // Every project produced a "passed" result, and yet the run covered only ProjB's one
    // class. That gap is the whole ticket, and it must be legible from state.
    test <@ final.LastResults.IsSome @>
    test <@ RunCoverage.coveredProjects final.LastCoverage = Set.ofList [ "ProjB" ] @>
    test <@ not (RunCoverage.coversWholeSuite [ "ProjA"; "ProjB" ] final.LastCoverage) @>

[<Fact(Timeout = 20000)>]
let ``a queued narrow drain cannot replace the full-suite receipt exposed to the verdict writer`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let fullRunId =
        match fullRun with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    let _ctx, statuses, _ledger, final = driveRuns handler [ fullRun; narrowRun ]

    let receipt = final.EvidenceReceipt.Value
    test <@ receipt.RunId = fullRunId @>
    test <@ RunCoverage.coversWholeSuite [ "ProjA"; "ProjB" ] receipt.Coverage @>

    let scopeCommand = handler.Commands |> List.find (fst >> (=) "test-scope") |> snd

    let commandCtx: FsHotWatch.PluginFramework.CommandCtx<TestPruneMsg> =
        { RepoRoot = "/tmp"
          Log = ignore
          Post = ignore
          IsRunning = fun _ -> false
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    let report =
        scopeCommand commandCtx final [||]
        |> Async.RunSynchronously
        |> FsHotWatch.Cli.IpcParsing.parseTestRunReport

    test <@ report.RunId = Some fullRunId @>
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.FullSuite 2 @>

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) -> test <@ verdict.Summary.Contains("2 projects") @>
    | other -> Assert.Fail($"latest run status must remain independently visible, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``test-scope declares EVERY run the session completed, not only the one the receipt names`` () =
    // AUTOMATION-533. The receipt deliberately holds ONE run — the full-suite one, which
    // a later narrow drain may not downgrade (the test above). That is right for grading
    // and wrong for reporting: both runs wrote a directory, both hold reports, and a
    // reader looking for their own tests in the receipt's directory alone finds only
    // what the last batch happened to cover.
    //
    // So the reply carries both questions. `runId` is what this verdict was graded from;
    // `runIds` is everything this session ran, and it is what lets a check name every
    // batch it produced instead of only the last.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let runIdOf event =
        match event with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    let fullRunId = runIdOf fullRun
    let narrowRunId = runIdOf narrowRun

    let _ctx, _statuses, _ledger, final = driveRuns handler [ fullRun; narrowRun ]

    let scopeCommand = handler.Commands |> List.find (fst >> (=) "test-scope") |> snd

    let commandCtx: FsHotWatch.PluginFramework.CommandCtx<TestPruneMsg> =
        { RepoRoot = "/tmp"
          Log = ignore
          Post = ignore
          IsRunning = fun _ -> false
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    let report =
        scopeCommand commandCtx final [||]
        |> Async.RunSynchronously
        |> FsHotWatch.Cli.IpcParsing.parseTestRunReport

    // Graded from the full-suite run, as before — this must not have moved.
    test <@ report.RunId = Some fullRunId @>

    // ...and the narrow drain, whose directory holds the only reports that batch wrote,
    // is no longer invisible. Newest first.
    test <@ report.SessionRuns = [ narrowRunId; fullRunId ] @>

[<Fact(Timeout = 20000)>]
let ``a queued manual filtered force-run clears the prior full receipt when its FIFO drain launches`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let mutable claims = [ LocalSlotBusy; SharedClaimed ]
    let recordingCtx, _, _ = makeTestPruneRecordingCtx ()

    let ctx =
        { recordingCtx with
            RunExclusiveShared =
                fun _ _ _ _ _ ->
                    match claims with
                    | claim :: later ->
                        claims <- later
                        claim
                    | [] -> failwith "unexpected extra test-slot claim" }

    let fullState = handler.Update ctx handler.Init fullRun |> Async.RunSynchronously
    test <@ fullState.EvidenceReceipt.IsSome @>

    let queuedReply = System.Threading.Tasks.TaskCompletionSource<string>()

    let queuedState =
        handler.Update
            ctx
            fullState
            (Custom(RunTestsRequested([ a125Config "ProjB" ], Some "FullyQualifiedName~ProjBTests", queuedReply)))
        |> Async.RunSynchronously

    test <@ queuedState.EvidenceReceipt.IsSome @>
    test <@ queuedState.QueuedCommandRuns.Length = 1 @>

    // `narrowRun` completes the pre-existing in-flight run. Its terminal handler must
    // dequeue and LAUNCH the explicit manual filter as a new top-level receipt boundary.
    let drainedState =
        handler.Update ctx queuedState narrowRun |> Async.RunSynchronously

    test <@ drainedState.EvidenceReceipt.IsNone @>
    test <@ drainedState.QueuedCommandRuns.IsEmpty @>
    test <@ claims.IsEmpty @>

[<Fact(Timeout = 15000)>]
let ``manual run reply terminates when its shared test host cannot start`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let reply = System.Threading.Tasks.TaskCompletionSource<string>()
    let mutable posted: TestPruneMsg option = None
    let recordingCtx, _, _ = makeTestPruneRecordingCtx ()

    let ctx =
        { recordingCtx with
            Post = fun message -> posted <- Some message
            RunExclusiveShared =
                fun _ _ _ _ failureMessage ->
                    posted <- Some(failureMessage (InvalidOperationException("host start fault")))
                    SharedClaimed }

    let claimedState =
        handler.Update ctx handler.Init (Custom(RunTestsRequested([ a125Config "ProjA" ], None, reply)))
        |> Async.RunSynchronously

    test <@ posted.IsSome @>

    let finalState =
        handler.Update ctx claimedState (Custom posted.Value) |> Async.RunSynchronously

    test <@ reply.Task.Wait 5000 @>
    test <@ reply.Task.Result.Contains("test host could not start") @>
    test <@ reply.Task.Result.Contains("host start fault") @>
    test <@ finalState.PendingRerun @>

[<Fact(Timeout = 20000)>]
let ``a run receipt keeps its launch seeds when a later cohort flushes while it runs`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let seedsA = [ "Lib.A.changed" ]
    let seedsB = [ "Lib.B.changed" ]

    let launch =
        { fullSuiteLaunch [ "ProjA" ] with
            Seeds = seedsA }

    let run = testsFinishedEvent [ "ProjA", passed false ] launch

    // This is the state at completion after BatchChecked has flushed cohort B while
    // run A held the test slot. Every other receipt input already comes from `launch`.
    let stateAtCompletion = { handler.Init with LastSeeds = seedsB }
    let ctx, _, _ = makeTestPruneRecordingCtx ()
    let final = handler.Update ctx stateAtCompletion run |> Async.RunSynchronously
    let receipt = final.EvidenceReceipt.Value

    let runId =
        match run with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    test <@ receipt.RunId = runId @>
    test <@ RunCoverage.coversWholeSuite [ "ProjA" ] receipt.Coverage @>
    test <@ receipt.Seeds = seedsA @>

[<Fact(Timeout = 20000)>]
let ``a zero-selection receipt carries the previous seeds captured at launch`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let previousSeeds = [ "Lib.PreviouslyVerified.changed" ]

    let zeroLaunch =
        { emptyLaunch with
            Seeds = previousSeeds
            ZeroSelection = ZeroSelection.AlreadyVerified }

    let run = testsFinishedEvent [] zeroLaunch

    let stateAtCompletion =
        { handler.Init with
            LastSeeds = [ "Lib.Later.changed" ] }

    let ctx, _, _ = makeTestPruneRecordingCtx ()
    let final = handler.Update ctx stateAtCompletion run |> Async.RunSynchronously
    let receipt = final.EvidenceReceipt.Value

    test <@ receipt.Seeds = previousSeeds @>
    test <@ receipt.ZeroSelection = ZeroSelection.AlreadyVerified @>

[<Fact(Timeout = 20000)>]
let ``a queued narrow failure remains red while the earlier full-suite receipt is retained`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowFailure =
        testsFinishedEvent
            [ "ProjA", impactSkipped
              "ProjB", TestsFailed("boom", true, TimeSpan.FromSeconds 1.0) ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let fullRunId =
        match fullRun with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    let _ctx, statuses, _ledger, final = driveRuns handler [ fullRun; narrowFailure ]

    test <@ final.EvidenceReceipt |> Option.map (fun receipt -> receipt.RunId) = Some fullRunId @>

    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"the later narrow failure must remain red, got %A{other}")

// `verificationOf` replaces a boolean that could not tell "no project was selected" from
// "every project matched nothing": for an empty result set it answered `false` to both
// "did every project match nothing?" and "did anything run?", so an empty run reached the
// CLI looking like a real one and was reported as `Tests passed`, exit 0.

[<Fact>]
let ``verificationOf tells an EMPTY run apart from one that matched nothing — the bool could not`` () =
    let zero = TestsNoMatch("Zero tests ran", TimeSpan.Zero)
    let realPass = TestsPassed("Passed! total: 4", true, TimeSpan.Zero)

    let emptyRun =
        { Results = Map.empty
          Elapsed = TimeSpan.Zero }

    let allZero =
        { Results = Map.ofList [ "A", zero; "B", zero ]
          Elapsed = TimeSpan.Zero }

    let ran =
        { Results = Map.ofList [ "A", zero; "B", realPass ]
          Elapsed = TimeSpan.Zero }

    test <@ verificationOf emptyRun.Results = NoProjectsSelected @>
    test <@ verificationOf allZero.Results = AllZeroMatch 2 @>
    // `Ran`, and PARTIAL: project A matched nothing, which `wasFiltered` reports as
    // filtered, so this run cannot claim the whole suite. Under the bool the scope rode
    // alongside as a separate field with nothing tying it to having run.
    test <@ verificationOf ran.Results = Ran RunScope.Partial @>

    // The bool collapses the first two cases in a way that loses the empty one entirely.
    test <@ allZeroMatch emptyRun = false @>
    test <@ allZeroMatch allZero = true @>

// `RunVerification.verifiedNothing` and the wire tokens are core API, pinned in
// EventTests.fs. Duplicating them here meant editing both files in lockstep.

// ── ProjectReference Include with an MSBuild property ────────────────────────
//
// `directReferences` resolved an `Include` by string-concatenating it onto the project
// directory, with no MSBuild property expansion, so a computed reference —
//
//   <SomeProject>$(MSBuildThisFileDirectory)../../Other/Other.fsproj</SomeProject>
//   <ProjectReference Include="$(SomeProject)" Condition="..." />
//
// — became a search for a file literally named `$(SomeProject)`. The gate then answered
// `InputsUndeterminable` and deferred the whole test run as "waiting on build", forever,
// on a perfectly fresh tree. Observed against TestPrune's own test project, which
// computes its optional Falco.UnionRoutes reference exactly this way.
//
// The fail-CLOSED behaviour must survive: an `Include` that genuinely cannot be resolved
// still has to be an error, or this reintroduces the fail-open hole the module exists to
// close. Both directions are asserted below.

let private writeProj (path: string) (contents: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path: string) |> ignore
    File.WriteAllText(path, contents)

[<Fact(Timeout = 15000)>]
let ``ProjectReference Include is resolved through MSBuild properties`` () =
    withTempDir "af-msbuild-prop" (fun tmpDir ->
        let other = Path.Combine(tmpDir, "Other", "Other.fsproj")
        writeProj other "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

        // The shape that broke: the Include is a property, defined in the same file in
        // terms of another well-known MSBuild property.
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <PropertyGroup>\n\
             \    <OtherProject>$(MSBuildThisFileDirectory)../Other/Other.fsproj</OtherProject>\n\
             \  </PropertyGroup>\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(OtherProject)\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs ->
            // The real file, not a literal `$(OtherProject)`.
            test <@ refs |> List.exists (fun r -> Path.GetFileName r = "Other.fsproj") @>
        | Error e -> Assert.Fail($"a property-computed ProjectReference must resolve, got: %s{e}"))

[<Fact(Timeout = 15000)>]
let ``an unresolvable ProjectReference still fails closed`` () =
    // The positive control for the test above: if expansion made every reference
    // resolvable, the gate would answer "fresh" for a project whose inputs it cannot
    // determine.
    withTempDir "af-msbuild-missing" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"../Nope/Nope.fsproj\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs -> Assert.Fail($"a missing ProjectReference must NOT resolve, got: %A{refs}")
        | Error _ -> ())

[<Fact(Timeout = 15000)>]
let ``a CONDITIONAL ProjectReference to a missing project is optional, not undeterminable`` () =
    // The one trade-off this fix makes, so it gets its own test. A project guards an
    // optional sibling checkout with a Condition, and on a machine that has not cloned it
    // the file is absent — deferring the whole run over a reference the build correctly
    // ignores is the thing being fixed.
    withTempDir "af-cond-missing" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <PropertyGroup>\n\
             \    <Optional>$(MSBuildThisFileDirectory)../Absent/Absent.fsproj</Optional>\n\
             \    <HaveIt Condition=\"Exists('$(Optional)')\">true</HaveIt>\n\
             \  </PropertyGroup>\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(Optional)\" Condition=\"'$(HaveIt)' == 'true'\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs ->
            // Skipped, not resolved, and not a phantom entry either.
            test <@ refs |> List.forall (fun r -> Path.GetFileName r <> "Absent.fsproj") @>
        | Error e -> Assert.Fail($"a conditional reference to an absent project must not be an error, got: %s{e}"))

[<Fact(Timeout = 15000)>]
let ``an UNCONDITIONAL reference to an unexpandable property still fails closed`` () =
    // The other side of that trade-off. Nothing defines `$(Nope)`, so it stays unexpanded
    // and the path cannot exist — and with no Condition marking it optional, the gate must
    // still refuse.
    withTempDir "af-unexpandable" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(Nope)\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs -> Assert.Fail($"an unexpandable unconditional reference must fail closed, got: %A{refs}")
        | Error _ -> ())

[<Fact(Timeout = 15000)>]
let ``a self-referential property does not spin`` () =
    // A property defined in terms of itself never reaches a fixed point, so the expansion
    // loop is bounded; the bound turns it into an ordinary unresolvable path, which then
    // fails closed like any other.
    withTempDir "af-self-ref" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <PropertyGroup>\n\
             \    <Loop>$(Loop)/x.fsproj</Loop>\n\
             \  </PropertyGroup>\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(Loop)\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs -> Assert.Fail($"a self-referential property must not resolve, got: %A{refs}")
        | Error _ -> ())

// ---------------------------------------------------------------------------
// AUTOMATION-303 case 1 — a STRUCTURAL change must MISS the BuildCompleted cache
// ---------------------------------------------------------------------------
//
// 2026-08-12: a test file plus its `<Compile Include=…>` was added, `fshw check`
// exited 0 with `outcome: green` and `scope: {kind: full, ranProjects: 6}`, and the 21
// new tests never executed. The BuildCompleted key is a merkle over the CHANGED
// SYMBOLS, and on a scan `BuildCompleted` is dispatched BEFORE the FCS pass — so that
// term is empty whatever the tree holds, the tree WITH the new file computes the same
// key as the tree without it, and the entry a previous green wrote replays. A replay
// skips the handler, so no run happens and the plugin's `LastCoverage` still describes
// the EARLIER run: the verdict reports that run's full-suite green over a tree it
// never saw.
//
// Driven through the plugin's REAL cache-key closure and a REAL project file on disk,
// not through `cacheKeyFor`'s thunks: the defect was that production never fed this
// input in, which a test supplying it by hand cannot see.

/// A minimal, well-formed `.fsproj` declaring `compiles` in order.
let private fsprojWithCompiles (compiles: string list) : string =
    let items =
        compiles
        |> List.map (fun f -> $"    <Compile Include=\"%s{f}\" />")
        |> String.concat "\n"

    $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n%s{items}\n  </ItemGroup>\n</Project>"

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: adding a compile item moves the BuildCompleted cache key`` () =
    withTempDir "tp-structure-key" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory projDir |> ignore
        let proj = Path.Combine(projDir, "Lib.fsproj")
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs" ])

        // Analysis-only (no test configs): with no runnable projects
        // `sessionHasTestEvidence` is vacuously true, so BuildCompleted HAS a key —
        // which is the value under test. With test configs a cold handler refuses the
        // cache outright (AUTOMATION-161) and there would be nothing to compare.
        let handler =
            create (Path.Combine(tmpDir, "tp.db")) tmpDir None None None None None []

        let keyOf () =
            handler.CacheKey.Value(BuildCompleted BuildSucceeded)

        let before = keyOf ()
        test <@ before.IsSome @>

        // POSITIVE CONTROL. "The key changed" is worth nothing from a key that changes
        // on every call — that would be a cache that never hits, not a cache that is
        // sound. On an untouched tree the key must be STABLE.
        test <@ keyOf () = before @>

        // The structural change: one new compile item. Nothing else moves — no symbol
        // has been checked, no build outcome differs, the queue is still empty.
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "NewTests.fs" ])

        test <@ keyOf () <> before @>

        // ... and removing one moves it too: a DELETED test file must not replay the
        // green that ran it.
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "NewTests.fs"; "More.fs" ])
        let three = keyOf ()
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "NewTests.fs" ])
        test <@ keyOf () <> three @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: the structure hash sees a compile item, not a source edit`` () =
    // The scope of the fix, pinned. Source EDITS are already covered by the symbol-diff
    // pipeline that runs after BuildCompleted and supersedes the entry; what that
    // pipeline cannot rescue is a file it has never seen. So the hash tracks the files
    // that DECLARE what is compiled and deliberately not their contents — otherwise
    // every keystroke would invalidate the whole test cache.
    withTempDir "tp-structure-scope" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory projDir |> ignore
        let proj = Path.Combine(projDir, "Lib.fsproj")
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs" ])
        File.WriteAllText(Path.Combine(projDir, "A.fs"), "module A\nlet x = 1\n")

        let before = projectStructureHash tmpDir

        // A source edit: not a structural change.
        File.WriteAllText(Path.Combine(projDir, "A.fs"), "module A\nlet x = 2\n")
        test <@ projectStructureHash tmpDir = before @>

        // POSITIVE CONTROL — the same hash over the same tree DOES move for the input
        // it exists for, so the equality above is a scope statement and not a hash that
        // never changes.
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "B.fs" ])
        test <@ projectStructureHash tmpDir <> before @>

        // Build output is excluded: a restore/rebuild dropping generated project files
        // under `obj/` must not invalidate every cached verdict in the repo.
        let objDir = Path.Combine(projDir, "obj")
        Directory.CreateDirectory objDir |> ignore
        let withItems = projectStructureHash tmpDir
        File.WriteAllText(Path.Combine(objDir, "Generated.fsproj"), fsprojWithCompiles [ "Z.fs" ])
        test <@ projectStructureHash tmpDir = withItems @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: the structure hash sees EVERY MSBuild implicit import, not just Directory.Build.props`` () =
    // The list this hash walks was a private copy that knew about `Directory.Build.props`
    // and not about `Directory.Build.targets` or `Directory.Packages.props` — all three
    // are implicit imports on identical terms, and each can carry a `<Compile Include=…>`
    // that adds a file to every project in the repo. Two of the three doors were open.
    //
    // RED before the fix for the two new names: the hash was byte-identical across the
    // edit, so a tree that had just gained a repo-wide compile item computed the key of
    // the tree without it and replayed a green that never ran the new tests.
    withTempDir "tp-structure-imports" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory projDir |> ignore
        File.WriteAllText(Path.Combine(projDir, "Lib.fsproj"), fsprojWithCompiles [ "A.fs" ])

        for name in FsHotWatch.StructureFiles.implicitImportNames do
            let path = Path.Combine(tmpDir, name)
            File.WriteAllText(path, "<Project />")
            let before = projectStructureHash tmpDir

            // POSITIVE CONTROL, before the claim: stable on an untouched tree.
            test <@ projectStructureHash tmpDir = before @>

            File.WriteAllText(path, "<Project><ItemGroup><Compile Include=\"Generated.fs\" /></ItemGroup></Project>")
            test <@ projectStructureHash tmpDir <> before @>)

// ---------------------------------------------------------------------------
// AUTOMATION-303 case 4 — a DELETED file must not keep blocking the verdict
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: an unanalysable file that was DELETED stops blocking the verdict`` () =
    // `UnanalyzableFiles` entries leave only when the file analyses CLEANLY
    // (AUTOMATION-113/125), and a deleted file never analyses again — no `FileChecked`
    // will ever arrive for a path that is gone. So its warning was re-reported after
    // every test run for the rest of the daemon's life, and under the default
    // warn-fail policy it denied every check its green while ALSO widening every run to
    // the full suite. Deleting a file is not a defect in the tree.
    withTempDir "tp-deleted-unanalysable" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory srcDir |> ignore

        let stillHere = Path.Combine(srcDir, "StillBroken.fs")
        File.WriteAllText(stillHere, "module StillBroken\n")

        let deleted = Path.Combine(srcDir, "Gone.fs")

        let handler =
            create ":memory:" tmpDir (Some [ a125Config "ProjA" ]) None None None None []

        let entry (relPath: string) (file: string) =
            relPath,
            { RelPath = relPath
              File = file
              Reason = "FS3520: unexpected doc comment" }

        let stateWithBoth =
            { handler.Init with
                UnanalyzableFiles = Map.ofList [ entry "src/StillBroken.fs" stillHere; entry "src/Gone.fs" deleted ] }

        let ctx, _statuses, ledger = makeTestPruneRecordingCtx ()

        let greenRun =
            testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ])

        let after = handler.Update ctx stateWithBoth greenRun |> Async.RunSynchronously

        // POSITIVE CONTROL FIRST. The warning for a file that IS still on disk is still
        // reported — so "the deleted one is absent" is a fact about deletion and not
        // about a detector that stopped firing. Without this the assertion below would
        // pass just as well if the ledger were empty for every reason.
        test <@ ledger.ContainsKey stillHere @>
        test <@ after.UnanalyzableFiles.ContainsKey "src/StillBroken.fs" @>

        // The finding: a path that no longer exists is neither a warning nor a reason to
        // widen the next run.
        test <@ not (ledger.ContainsKey deleted) @>
        test <@ not (after.UnanalyzableFiles.ContainsKey "src/Gone.fs") @>)

// =============================================================================
// AUTOMATION-201 — the stale-artifact PREFLIGHT.
//
// `ArtifactFreshness.stale` was always right about WHAT was stale; it was asked too
// late. Its single call site sat inside the per-config body of the PARALLEL run loop,
// so a group-A project wrote its report before group B's staleness had been looked at
// — a three-minute partial-execution red that reads like progress.
//
// These tests are about ORDER, and they settle it with a file on disk rather than an
// assertion about intent: each runner is a real `sh` process that `touch`es a marker.
// A marker that exists is a suite that ran. Nothing here trusts a log line.
// =============================================================================

/// A synthetic two-project repo in the real MSBuild output layout, with runners that
/// leave evidence. `Common` is referenced by both test projects and its DLL is copied
/// into each of their output dirs — the copy that goes stale in the field.
type private PreflightSynth =
    { Root: string
      P1Proj: string
      P2Proj: string
      P1Marker: string
      P2Marker: string
      P2Src: string
      P2Dll: string
      CommonDll: string
      CommonCopyInP2: string
      BuiltAt: DateTime }

let private preflightSynth (root: string) : PreflightSynth =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)

    let commonDir = Path.Combine(root, "Common")
    let commonOut = p [ commonDir; "bin"; "Debug"; "net10.0" ]

    writeAt (Path.Combine(commonDir, "Common.fsproj")) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (Path.Combine(commonDir, "Common.fs")) "module Common" sourcedAt
    writeAt (Path.Combine(commonOut, "Common.dll")) "COMMON-V2" builtAt

    let mkTestProject (name: string) =
        let dir = Path.Combine(root, name)
        let out = p [ dir; "bin"; "Debug"; "net10.0" ]

        writeAt
            (Path.Combine(dir, $"{name}.fsproj"))
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Common/Common.fsproj\" />\n  </ItemGroup>\n</Project>"
            sourcedAt

        writeAt (Path.Combine(dir, $"{name}.fs")) $"module {name}" sourcedAt
        writeAt (Path.Combine(out, name)) "" builtAt // apphost — the presence probe's target
        writeAt (Path.Combine(out, $"{name}.dll")) "" builtAt
        writeAt (Path.Combine(out, "Common.dll")) "COMMON-V2" builtAt // a CURRENT copy
        dir, out

    let p1Dir, _ = mkTestProject "P1"
    let p2Dir, p2Out = mkTestProject "P2"

    { Root = root
      P1Proj = Path.Combine(p1Dir, "P1.fsproj")
      P2Proj = Path.Combine(p2Dir, "P2.fsproj")
      P1Marker = Path.Combine(root, "p1-ran")
      P2Marker = Path.Combine(root, "p2-ran")
      P2Src = Path.Combine(p2Dir, "P2.fs")
      P2Dll = Path.Combine(p2Out, "P2.dll")
      CommonDll = Path.Combine(commonOut, "Common.dll")
      CommonCopyInP2 = Path.Combine(p2Out, "Common.dll")
      BuiltAt = builtAt }

/// A runner that leaves proof it ran, and carries the `--project` the freshness gate
/// derives its target from. `sh` ignores the trailing args (they become positional
/// params of the `-c` script), so the process is a plain `touch` either way.
let private markerRunner (project: string) (marker: string) (projFile: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = $"-c \"touch {marker}; exit 0\" run --project {projFile} --no-build"
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = Disabled }

/// Drive one real run of both projects and return the repo root's marker facts.
/// Seeds a covering symbol per project and a pending queue naming both, so a
/// `BuildCompleted` runs BOTH — the two-suite shape the ordering bug needs.
let private drivePreflightRunWithHost (tmpDir: string) (s: PreflightSynth) =
    let dbPath = Path.Combine(tmpDir, "tp.db")
    let db = Database.create dbPath
    // DISTINCT source files per symbol: two symbols sharing one file collapse into a
    // single affected test, which would select only ONE project and quietly turn the
    // "the fresh project did not run" assertion into "the fresh project was never
    // selected" — a test that passes for the wrong reason.
    PendingQueueHelpers.seedCoveredSymbol db "Lib.a" "A.fs" "P1" "P1Tests" "aTest"
    PendingQueueHelpers.seedCoveredSymbol db "Lib.b" "B.fs" "P2" "P2Tests" "bTest"
    FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.a"; "Lib.b" ])

    let configs =
        [ markerRunner "P1" s.P1Marker s.P1Proj; markerRunner "P2" s.P2Marker s.P2Proj ]

    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    let handler = create dbPath tmpDir (Some configs) None None None None []
    host.RegisterHandler(handler)

    let await = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    await.Wait(TimeSpan.FromSeconds 25.0) |> ignore
    waitForQuiescent host 5000
    host

/// Drive one real run and discard the host — for the tests whose evidence is on disk.
let private drivePreflightRun (tmpDir: string) (s: PreflightSynth) =
    drivePreflightRunWithHost tmpDir s |> ignore

/// POSITIVE CONTROL, and it comes first on purpose. Every assertion below about a
/// suite NOT running is worthless without it: a preflight that refused everything —
/// or a harness that never launched anything — would satisfy those tests trivially.
/// This pins that the very same path DOES run both suites when the tree is fresh.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: POSITIVE CONTROL — a fresh tree runs every suite`` () =
    withTempDir "tp-preflight-fresh" (fun tmpDir ->
        let s = preflightSynth tmpDir
        drivePreflightRun tmpDir s

        // Both processes really ran. This is the control the refusal tests lean on.
        test <@ File.Exists s.P1Marker @>
        test <@ File.Exists s.P2Marker @>)

/// THE ORDERING TEST. P2's source is edited after its assembly was compiled — the
/// unhealable `AssemblyOlderThanSource` case, which only a real build can clear.
///
/// RED BEFORE THE FIX: with the freshness gate living inside the parallel per-config
/// body, P1 was examined, found fresh and LAUNCHED, and only then was P2 examined and
/// deferred — so `p1-ran` existed and this test failed on its first assertion. That is
/// the three-minute partial-execution red, reproduced in miniature.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: a stale project refuses the run BEFORE any suite executes`` () =
    withTempDir "tp-preflight-stale" (fun tmpDir ->
        let s = preflightSynth tmpDir

        // The trigger from the field: a source edit under a live daemon. P2's compile
        // has not run since, so its assembly cannot be trusted.
        File.SetLastWriteTimeUtc(s.P2Src, s.BuiltAt.AddMinutes 30.0)

        drivePreflightRun tmpDir s

        // NOTHING launched — not the stale project, and not the fresh one either.
        // A run whose tree is provably not built cannot reach a green verdict, so
        // spending minutes on the projects that happen to be fresh buys nothing but
        // the appearance of progress.
        test <@ not (File.Exists s.P2Marker) @>
        test <@ not (File.Exists s.P1Marker) @>)

/// The heal, end to end. P2's copy of `Common.dll` holds bytes no current build output
/// holds — the same-timestamp/different-bytes inversion MSBuild's incremental copy
/// leaves behind. The repair is provably complete (write the origin's bytes across),
/// so the preflight fixes it, re-reads the bytes, and lets the run proceed.
///
/// RED BEFORE THE FIX: P2 was deferred as "waiting on build" and never ran, so
/// `p2-ran` was absent. Nothing repaired anything.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: a stale build-output COPY is repaired and the run proceeds`` () =
    withTempDir "tp-preflight-heal" (fun tmpDir ->
        let s = preflightSynth tmpDir

        // The inversion: different bytes, and the mtime MSBuild would compare as equal,
        // so no plain incremental build re-copies it.
        File.WriteAllText(s.CommonCopyInP2, "COMMON-V1-STALE")
        File.SetLastWriteTimeUtc(s.CommonCopyInP2, s.BuiltAt)

        drivePreflightRun tmpDir s

        // Repaired, so BOTH suites ran ...
        test <@ File.Exists s.P1Marker @>
        test <@ File.Exists s.P2Marker @>

        // ... the bytes are the origin's ...
        test <@ File.ReadAllText s.CommonCopyInP2 = File.ReadAllText s.CommonDll @>

        // ... and the repair was RECORDED, not just done. A heal that fires every run
        // is itself the finding, and nobody greps history for it.
        let ledger = FsHotWatch.TestPrune.StaleArtifactPreflight.loadLedger tmpDir
        test <@ ledger |> List.exists (fun r -> r.File = s.CommonCopyInP2) @>)

/// END TO END, through the ledger the CLI actually reads. AC2's "states the concrete
/// remedy" is about the message an operator ACTS on, and for a `check` that is the
/// top-level verdict line — which, before this rework, said "a test project's build
/// artifact was not produced … re-run once the build settles" and pointed at `fshw
/// confirm`. Every clause of that is wrong here: the artifact WAS produced, re-running
/// returns the identical refusal, and `fshw confirm` spends a full gate cycle to reach
/// it. So this asserts the whole chain — real run → real ledger entry →
/// `BuildWait.classify` — rather than any one link.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: an unrepairable stale tree reaches the CLI as StaleOutput, not as a build-ordering defer`` () =
    withTempDir "tp-preflight-classify" (fun tmpDir ->
        let s = preflightSynth tmpDir
        // The unhealable case: P2's source was edited after its assembly was compiled.
        File.SetLastWriteTimeUtc(s.P2Src, s.BuiltAt.AddMinutes 30.0)

        let host = drivePreflightRunWithHost tmpDir s

        test <@ not (File.Exists s.P1Marker) @>
        test <@ not (File.Exists s.P2Marker) @>

        let deferrals =
            host.GetErrors()
            |> Map.toList
            |> List.collect snd
            |> List.filter (fun (_, e) -> FsHotWatch.ErrorLedger.ErrorEntry.isWaitingOnBuild e)

        test <@ not (List.isEmpty deferrals) @>

        // The message the CLI classifies on — from the run, not hand-written.
        let messages = deferrals |> List.map (fun (_, e) -> e.Message)

        // THE OPERATOR-FACING MESSAGE, derived the way the CLI derives it: classify the
        // ledger, take the verdict, read the reason. Asserting on the reason rather than
        // on the classifier is deliberate — the defect was never "the enum is wrong", it
        // was "the person reading this is sent to run the wrong command".
        let outcome =
            FsHotWatch.Cli.CheckVerdict.CheckOutcome.WaitingOnBuild(
                FsHotWatch.Cli.CheckVerdict.BuildWait.staleDeferrals (
                    FsHotWatch.Cli.CheckVerdict.BuildWait.classify messages
                )
            )

        match FsHotWatch.Cli.Verdict.outcomeOfCheck outcome with
        | FsHotWatch.Cli.Verdict.Incomplete reason ->
            // Names the affected project, and the command that actually repairs it.
            test <@ reason.Contains "P2" @>
            test <@ reason.Contains "dotnet build" @>
            test <@ reason.Contains "--no-incremental" @>

            // And names the projects to ACT on, not every project the refusal deferred.
            // P1 is fresh and was deferred only because the run was refused as a whole —
            // listing it would be the over-listing that made the headline unreadable in
            // the first place. Its own deferral still names P2 as the cause.
            test <@ not (reason.Contains "P1") @>

            // And no longer asserts the cause that is FALSE here — the artifact WAS
            // produced — nor prescribes the two remedies that cannot clear it.
            test <@ not (reason.Contains "was not produced") @>
            test <@ not (reason.Contains "re-run once the build settles") @>
        | other -> failwith $"a stale build-output refusal must stay incomplete, got %A{other}"

        // The DETAIL stops asserting the wrong cause too. It used to read "This is a
        // build-ordering issue, not a test failure" for every defer — advice that costs
        // a gate cycle when the tree is stale rather than merely early.
        let staleDetails =
            deferrals
            |> List.filter (fun (_, e) -> FsHotWatch.TestPrune.StaleArtifactPreflight.isStaleOutputDeferral e.Message)
            |> List.choose (fun (_, e) -> e.Detail)

        test <@ not (List.isEmpty staleDetails) @>
        test <@ staleDetails |> List.forall (fun d -> not (d.Contains "build-ordering issue")) @>
        test <@ staleDetails |> List.forall (fun d -> d.Contains "build OUTPUT is stale") @>)

/// POSITIVE CONTROL for the classification: the OTHER defer — a build artifact that
/// genuinely was not produced — must keep classifying as `ArtifactNotProduced`, because
/// for it "re-run once the build settles" is correct advice and replacing it would be a
/// regression wearing a fix's clothes. Without this, a classifier that answered
/// `StaleOutput` to everything would pass the test above.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: an apphost-missing defer still classifies as a build-ordering wait`` () =
    let deferred: TestResults =
        { Results = Map.ofList [ "P2", TestsDeferred "apphost not produced; tests did not run" ]
          Elapsed = TimeSpan.Zero }

    let messages = failuresOf Map.empty deferred |> List.map (fun f -> f.Entry.Message)
    test <@ not (List.isEmpty messages) @>

    test
        <@
            FsHotWatch.Cli.CheckVerdict.BuildWait.classify messages = FsHotWatch.Cli.CheckVerdict.BuildWait.ArtifactNotProduced
        @>

// ---------------------------------------------------------------------------
// AUTOMATION-343 — TEST-PRUNE's own cold-vs-cached ledger parity
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-343: a cached test-prune replay clears the whole ledger, exactly as its real run does`` () =
    // THIS PLUGIN IS THE OPPOSITE CASE, and asserting the same thing here as for
    // build / file-command would paper over the difference.
    //
    // `reportOutstanding` is test-prune's ONLY path to the ledger, and it clears the
    // slate wholesale (`ClearAllErrors`) before re-reporting the outstanding set. So a
    // finding about a file outside the run is not "preserved" here — a real run
    // deletes it, on purpose, and a replay that preserved it would be the FALSE RED
    // the fix's risk inverts towards: findings accumulating across replays because
    // the replay stopped doing what the run did.
    //
    // The framework captures that wholesale clear as a `("*", [])` marker, which is
    // exactly why deleting the blanket pre-replay `ClearPlugin` was safe: the run that
    // really cleared everything already says so in its own captured errors. This test
    // is what proves that claim about the plugin that actually relies on it.
    withTempDir "a343-tp-parity" (fun tmpDir ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)

        // Exit 0 with no parseable report is a PASS (`decideTestOutcome`'s clean-exit
        // arm), so the run leaves nothing outstanding — which is what lets the entry
        // be cacheable at all.
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "tests passed"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler =
            create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        // COLD.
        seedOutOfBatch host "test-prune"
        test <@ ledgerHasOutOfBatch host "test-prune" @>

        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 30000
        let settled = waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 30000
        test <@ settled @>
        let coldSummary = terminalSummaryOf host "test-prune"
        test <@ not (coldSummary.Contains "(cached)") @>

        let cold = ledgerSlice host "test-prune"

        // The real run really did wipe it. Asserted, not assumed — if it did not, the
        // cached assertion below would be pinning the wrong behaviour.
        test <@ not (ledgerHasOutOfBatch host "test-prune") @>

        // WARM. Re-seed so the replay has something to destroy: without this the
        // "sentinel is gone" assertion below would hold trivially, since the cold run
        // already removed it.
        seedOutOfBatch host "test-prune"
        test <@ ledgerHasOutOfBatch host "test-prune" @>

        host.EmitBuildCompleted(BuildSucceeded)
        let replayed = waitForCachedReplay host "test-prune" 30000
        test <@ replayed @>

        let cached = ledgerSlice host "test-prune"

        // The replay honoured the captured `("*", [])` and cleared it too ...
        test <@ not (ledgerHasOutOfBatch host "test-prune") @>
        // ... landing the ledger in exactly the state the real run landed it in.
        test <@ cached = cold @>)

// ---------------------------------------------------------------------------
// AUTOMATION-259 (rework) — retaining the selection `confirm` widens past, and asking
// whether it would have reached a failure.
//
// `confirm` sends `set-scope full` BEFORE the scan that provokes the run, so the impact
// selection is computed at the launch chokepoint and thrown away in the same breath.
// Retaining it is what turns each confirm into a same-tree sample instead of a positive
// assertion that nothing was compared.
// ---------------------------------------------------------------------------

let private a259Projects =
    [ testConfigNamed "Alpha.Tests"
      testConfigNamed "Beta.Tests"
      testConfigNamed "Gamma.Tests" ]

let private a259Failure (project: string) (cls: string option) : OutstandingFailure =
    { Project = project
      Class = cls
      Method = cls |> Option.map (fun _ -> "failed method")
      File = "tests/X.fs"
      Entry = FsHotWatch.ErrorLedger.ErrorEntry.error $"%s{project}: 1 test(s) failed" }

[<Fact(Timeout = 15000)>]
let ``the would-have-run selection drops the FULL-SUITE widening and keeps every other one`` () =
    // The load-bearing line. Remove too much and the projection models a `check` narrower
    // than the one that runs, and reports misses the selector never made; remove too
    // little and every project is selected, the projection agrees with everything, and
    // the sample is worthless.
    let symbolAffected = Map.ofList [ "Alpha.Tests", [ "Alpha.OneTests" ] ]

    let selection =
        wouldHaveRunSelection a259Projects symbolAffected Set.empty Set.empty false

    test <@ selection = Map.ofList [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ]) ] @>

    // The coarse fallback for unanalysable files (AUTOMATION-113) fires in the inner loop
    // too, so it SURVIVES the projection — as a whole-project run, which is what it is.
    let withCoarse =
        wouldHaveRunSelection a259Projects symbolAffected (Set.ofList [ "Beta.Tests" ]) Set.empty false

    test <@ Map.tryFind "Beta.Tests" withCoarse = Some ProjectInFull @>
    test <@ Map.tryFind "Alpha.Tests" withCoarse = Some(ProjectClasses(Set.ofList [ "Alpha.OneTests" ])) @>
    test <@ Map.containsKey "Gamma.Tests" withCoarse |> not @>

    // An UNREADABLE pending-verification ledger (AUTOMATION-150) widens `check` to the
    // whole suite as well, and must therefore widen the projection.
    let withUnreadableLedger =
        wouldHaveRunSelection a259Projects symbolAffected Set.empty Set.empty true

    test <@ withUnreadableLedger |> Map.forall (fun _ sel -> sel = ProjectInFull) @>
    test <@ withUnreadableLedger.Count = 3 @>

[<Fact(Timeout = 15000)>]
let ``the would-have-run selection retains a runtime-only project's reach`` () =
    // There is deliberately no AST-selected class for Beta. Runtime coverage is
    // the only reason check would launch it, and it must launch the whole project.
    // Dropping this input makes the same-tree confirm comparison falsely report
    // Beta's red as a selector miss.
    let selection =
        wouldHaveRunSelection
            a259Projects
            (Map.ofList [ "Alpha.Tests", [ "Alpha.OneTests" ] ])
            Set.empty
            (Set.ofList [ "Beta.Tests" ])
            false

    test <@ Map.tryFind "Beta.Tests" selection = Some ProjectInFull @>

    let reach =
        CheckReach.classify (Some selection) [ a259Failure "Beta.Tests" (Some "Beta.RuntimeOnlyTests") ]

    test <@ reach = ReachedAFailure [ "Beta.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``scopeOfSelection describes what check would have covered, and never rounds up`` () =
    let projects = a259Projects |> List.map (fun c -> c.Project)

    test <@ scopeOfSelection projects Map.empty = ScopeNone 3 @>

    test
        <@
            scopeOfSelection projects (a259Projects |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList) = ScopeFull
                3
        @>

    // Every project selected, but one of them under a class filter — NOT a full suite.
    let allButFiltered =
        [ "Alpha.Tests", ProjectClasses(Set.ofList [ "X" ])
          "Beta.Tests", ProjectInFull
          "Gamma.Tests", ProjectInFull ]
        |> Map.ofList

    test <@ scopeOfSelection projects allButFiltered = ScopeFiltered(3, 3) @>
    test <@ scopeOfSelection projects (Map.ofList [ "Alpha.Tests", ProjectInFull ]) = ScopeFiltered(1, 3) @>

[<Fact(Timeout = 15000)>]
let ``CheckReach.classify decides reach per failure, and refuses what it cannot decide`` () =
    let inFull = Map.ofList [ "Alpha.Tests", ProjectInFull ]

    let filtered =
        Map.ofList [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ]) ]

    // Nothing failed: there was no failure for a selection to miss.
    test <@ CheckReach.classify (Some inFull) [] = NoFailuresToReach @>

    // A project the selection never launches — the ordinary miss.
    let projectMiss =
        CheckReach.classify (Some inFull) [ a259Failure "Beta.Tests" (Some "Beta.OneTests") ]

    test
        <@
            projectMiss = ReachedNoFailure
                [ { Project = "Beta.Tests"
                    Class = "Beta.OneTests"
                    Cause = MissCause.ProjectNotSelected } ]
        @>

    // A project it launches in full reaches every failure in it, named class or not.
    test
        <@
            CheckReach.classify (Some inFull) [ a259Failure "Alpha.Tests" (Some "Alpha.TwoTests") ] = ReachedAFailure
                [ "Alpha.Tests" ]
        @>

    test <@ CheckReach.classify (Some inFull) [ a259Failure "Alpha.Tests" None ] = ReachedAFailure [ "Alpha.Tests" ] @>

    // A class-filtered project reaches the failure only when the class is in the filter.
    test
        <@
            CheckReach.classify (Some filtered) [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests") ] = ReachedAFailure
                [ "Alpha.Tests" ]
        @>

    let classMiss =
        CheckReach.classify (Some filtered) [ a259Failure "Alpha.Tests" (Some "Alpha.TwoTests") ]

    test
        <@
            classMiss = ReachedNoFailure
                [ { Project = "Alpha.Tests"
                    Class = "Alpha.TwoTests"
                    Cause = MissCause.ClassNotInFilter } ]
        @>

    // A PROJECT-LEVEL red (a timeout, an errored host, unparseable output) names no
    // class, so a class-filtered selection cannot be asked whether it reaches it. Refused
    // rather than guessed — guessing "not reached" invents a missed failure, guessing
    // "reached" invents an agreement.
    match CheckReach.classify (Some filtered) [ a259Failure "Alpha.Tests" None ] with
    | ReachUnknown reason -> test <@ reason.Contains "names no test class" @>
    | other -> failwithf "a project-level red under a class filter must be UNKNOWN, got %A" other

    // ONE undecidable failure poisons the whole reading, even beside a decided one: the
    // question is about the RUN, and a run we cannot fully account for is not a sample.
    match
        CheckReach.classify
            (Some filtered)
            [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests")
              a259Failure "Alpha.Tests" None ]
    with
    | ReachUnknown _ -> ()
    | other -> failwithf "an undecidable failure beside a decided one must be UNKNOWN, got %A" other

    // No retained selection at all (a forced re-run, an aborted run, a skip): there is
    // nothing to project through, and `Selection` must NOT be used as a stand-in — under
    // full-suite scope it names every project in full and would agree forever.
    match CheckReach.classify None [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests") ] with
    | ReachUnknown reason -> test <@ reason.Contains "no retained impact selection" @>
    | other -> failwithf "a run with no retained selection must be UNKNOWN, got %A" other

[<Theory(Timeout = 15000)>]
[<InlineData("Database.Tests failed but wrote no current-run CTRF report")>]
[<InlineData("Database.Tests's CTRF failed rows do not reconcile to its summary")>]
[<InlineData("Database.Tests's CTRF report could not be read")>]
let ``AUTOMATION-111 incomplete failure evidence cannot become a selection alarm`` reason =
    let selection = Some(Map.ofList [ "Unit.Tests", ProjectInFull ])
    let reach, recall = CheckReach.classifyEvidence selection (Error reason)

    test <@ reach = ReachUnknown reason @>
    test <@ recall = RecallNotMeasurable reason @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 complete evidence preserves genuine mass selection misses`` () =
    let selection = Some(Map.ofList [ "Unit.Tests", ProjectInFull ])

    let failures =
        [ 1..600 ]
        |> List.map (fun index -> a259Failure "Database.Tests" (Some $"Database.Case%d{index}"))

    let reach, _ = CheckReach.classifyEvidence selection (Ok failures)

    match reach with
    | ReachedNoFailure missed -> test <@ missed.Length = 600 @>
    | other -> failwithf "complete mass misses remain actionable, got %A" other

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-67 failure recall is an exact numerator over full-run failures and never vacuous`` () =
    let selection =
        Map.ofList
            [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ])
              "Beta.Tests", ProjectClasses(Set.ofList [ "Beta.OtherTests" ]) ]

    let failures =
        [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests")
          a259Failure "Beta.Tests" (Some "Beta.MissedTests") ]

    test <@ CheckReach.measure (Some selection) failures = RecallMeasured(1, 2, 1.0, false) @>

    let interventionSelection =
        Map.ofList
            [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ])
              "Beta.Tests", ProjectClasses(Set.ofList [ "Beta.MissedTests" ]) ]

    test <@ CheckReach.measure (Some interventionSelection) failures = RecallMeasured(2, 2, 1.0, true) @>

    match CheckReach.measure (Some selection) [] with
    | RecallNotMeasurable reason -> test <@ reason.Contains("denominator is zero") @>
    | measured -> Assert.Fail($"a zero-denominator sample cannot claim recall, got %A{measured}")

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-67 failure recall requires every observed failure to be decidable`` () =
    let inFull = Map.ofList [ "Alpha.Tests", ProjectInFull ]

    match CheckReach.measure (Some inFull) [ a259Failure "Alpha.Tests" None ] with
    | RecallNotMeasurable reason -> test <@ reason.Contains("exact failing-test denominator") @>
    | measured -> Assert.Fail($"an undecidable failure cannot enter a recall denominator, got %A{measured}")

// ---------------------------------------------------------------------------
// AUTOMATION-747 — a red project's ledger slice is a SUM, not a PRODUCT.
//
// `failuresOf` used to attach `output` — the whole captured project run — to every
// parsed per-test failure. In the incident this ticket records that made one project's
// ledger slice 753 × 48 MB: ~36 GB, from a plugin holding a single string. It is
// invisible in the daemon's own heap (every entry is the same reference) and fatal the
// moment any mirror of the ledger writes each entry's copy out — which is exactly where
// seven finished merge gates died.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``failuresOf does not attach the whole project output to every parsed failure`` () =
    let failures = 200
    let noise = String.replicate 40_000 "n"

    let output =
        [ yield noise
          for i in 1..failures do
              yield $"  failed Some.Suite%d{i}.the test (0ms)" ]
        |> String.concat "\n"

    let results: TestResults =
        { Results = Map.ofList [ "ProjA", TestsFailed(output, false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let entries = failuresOf Map.empty results

    // Every failure is still FILED — this bound may not be bought by losing reds.
    test <@ entries.Length = failures @>

    let totalChars =
        entries
        |> List.sumBy (fun f ->
            f.Entry.Message.Length
            + (f.Entry.Detail |> Option.map String.length |> Option.defaultValue 0))

    // The law: the slice is bounded by the failures PLUS the output, never their
    // product. Before this fix the same input produced 200 × 40,000 = 8,000,000 chars.
    test <@ totalChars < output.Length @>

[<Fact(Timeout = 15000)>]
let ``failuresOf still carries the whole output when NO test could be named`` () =
    // The one arm where that text is the entry's own subject: nothing named a test, so
    // the run itself is all the reader has. ONE entry, so it is carried once.
    let output = "the host said something unparseable\n" + String.replicate 5_000 "z"

    let results: TestResults =
        { Results = Map.ofList [ "ProjA", TestsFailed(output, false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let entry = (failuresOf Map.empty results |> List.exactlyOne).Entry
    test <@ entry.Detail = Some output @>
