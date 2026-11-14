/// AUTOMATION-1581: a red unfiltered project is never counted as covered.
///
/// A red full run used to earn the full-suite baseline (and whole-project evidence) for a
/// red project, on the premise that it ran every test. A host that ends early, as xUnit v3
/// does under `--stop-on-fail on`, breaks that premise: its report lists only the tests it
/// reached, so the baseline vouched for tests that never ran, and a later filtered `check`
/// went green over a red test (ADR-021, as amended).
///
/// The shape: one test project P holding `Ns.A.a1`, `Ns.B.b1`, `Ns.Z.z1` and `Ns.Z.z2`. Its
/// host stops at the first failure, as xUnit v3 does under `--stop-on-fail on`, and writes
/// a CTRF report listing only the tests it reached. So a red run whose first test fails
/// reports `{tests:1, failed:1}` with one row, and never executes Z.
///
/// The host is a real child process (`sh`, a script written per test) driven through the
/// real BuildCompleted -> launch -> TestsFinished flow, so what was SELECTED and EXECUTED
/// is read from files the host writes, never inferred.
module FsHotWatch.Tests.TestPruneStopOnFailReachTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.CompletionStages
open FsHotWatch.TestPrune.FailureReports
open FsHotWatch.TestPrune.TestReports
open FsHotWatch.TestPrune.PluginState
open TestPrune.AstAnalyzer
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

// ---------------------------------------------------------------------------
// The positive control, at `settleDebt` itself.
// ---------------------------------------------------------------------------

let private stubDebt: CompletionDebt =
    { CommitPending = commitPending
      CoveringOf = fun _ _ -> Set.empty
      DebtScope = fun _ owed symbol -> owed symbol
      IndexNamesOnlyRunnable = fun _ -> true
      OwedElsewhere = fun _ _ -> Set.empty
      RevisionOf = fun _ _ -> 0L }

[<Fact(Timeout = 10000)>]
let ``control - settleDebt earns a full-suite baseline from a stop-on-fail red run that never reached Z`` () =
    let handler =
        create ":memory:" (isolatedRoot ()) (Some [ testConfigNamed "P" ]) None None None None []

    // P's host stopped at the first failure: unfiltered, red. Its report (not an input to
    // settleDebt at all) would be `{tests:1, failed:1}` with the single row Ns.A.a1, while
    // P holds A.a1, Z.z1 and Z.z2.
    let results =
        Map.ofList [ "P", TestsFailed("failed Ns.A.a1 (1ms)", false, TimeSpan.FromSeconds 1.0) ]

    let runId = Guid.NewGuid()

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

    // Step 1 of the defect: an unfiltered red run is `Ran FullSuite`.
    test <@ completed.Verification = Ran FullSuite @>

    let settled =
        settleDebt
            { State = handler.Init
              Completed = completed
              Launch =
                { fullSuiteLaunch [ "P" ] with
                    ModelGeneration = Some 1L }
              RunnableProjects = Set.ofList [ "P" ]
              CurrentInputTree = None
              CurrentModelGeneration = Some 1L
              DebtDuringFullRun = Map.empty
              OwedTo = fun _ -> Set.empty
              NoUnrunnableCoverers = lazy true
              Debt = stubDebt }

    test <@ settled.ExecutedFullSuite @>
    // A red that never reached Z.z1/Z.z2 is not accounted for, even though an outstanding
    // red names its project.
    test <@ not settled.EarnsBaseline @>
    test <@ settled.Debt.Baseline = None @>

let private settleWith (state: TestPruneState) (result: TestResult) =
    let results = Map.ofList [ "P", result ]

    let completed: TestRunCompleted =
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

    completed.RunId,
    settleDebt
        { State = state
          Completed = completed
          Launch =
            { fullSuiteLaunch [ "P" ] with
                ModelGeneration = Some 1L }
          RunnableProjects = Set.ofList [ "P" ]
          CurrentInputTree = None
          CurrentModelGeneration = Some 1L
          DebtDuringFullRun = Map.empty
          OwedTo = fun _ -> Set.empty
          NoUnrunnableCoverers = lazy true
          Debt = stubDebt }

[<Fact(Timeout = 10000)>]
let ``settleDebt - a red full run keeps the earlier green baseline instead of replacing it`` () =
    let handler =
        create ":memory:" (isolatedRoot ()) (Some [ testConfigNamed "P" ]) None None None None []

    let earlier: FullSuiteBaseline.Baseline =
        { RunId = Guid.NewGuid()
          EarnedAt = DateTime.UtcNow.AddHours -1.0
          Projects = Set.ofList [ "P" ]
          Tree = None
          EarlierTrees = [] }

    let state =
        { handler.Init with
            Debt =
                { handler.Init.Debt with
                    Baseline = Some earlier } }

    let _, settled =
        settleWith state (TestsFailed("failed Ns.A.a1 (1ms)", false, TimeSpan.FromSeconds 1.0))

    test <@ settled.Debt.Baseline = Some earlier @>

[<Fact(Timeout = 10000)>]
let ``settleDebt - a green unfiltered full run still earns the baseline`` () =
    let handler =
        create ":memory:" (isolatedRoot ()) (Some [ testConfigNamed "P" ]) None None None None []

    let runId, settled =
        settleWith handler.Init (TestsPassed("passed", false, TimeSpan.FromSeconds 1.0))

    test <@ settled.EarnsBaseline @>
    test <@ settled.Debt.Baseline |> Option.map _.RunId = Some runId @>

// ---------------------------------------------------------------------------
// The real host: a stop-on-fail runner, a seeded index, BuildCompleted-driven runs.
// ---------------------------------------------------------------------------

/// The fake xUnit host. Parses the filter and CTRF switches fshw appends, runs the tests
/// the filter admits in a fixed order (A.a1, B.b1, Z.z1, Z.z2), stops at the first
/// failure, appends each executed test to `executed`, and writes a coherent CTRF report
/// of exactly the rows it reached. A test fails while `fail-<Class>.<method>` exists.
let private hostScript =
    """#!/bin/sh
D="$(dirname "$0")"
classes=""
name=""
dir=""
while [ $# -gt 0 ]; do
  case "$1" in
    --filter-class) shift; classes="$classes|$1|" ;;
    --report-ctrf-filename) shift; name="$1" ;;
    --results-directory) shift; dir="$1" ;;
  esac
  shift
done
echo "classes=[$classes]" >> "$D/launches"
rows=""; total=0; failed=0; passed=0; stop=0
run() {
  [ "$stop" = 1 ] && return 0
  if [ -n "$classes" ]; then
    case "$classes" in *"|$1|"*) ;; *) return 0 ;; esac
  fi
  echo "$1.$2" >> "$D/executed"
  if [ -f "$D/fail-$1.$2" ]; then
    st=failed; failed=$((failed+1)); stop=1; echo "failed $1.$2 (1ms)"
  else
    st=passed; passed=$((passed+1)); echo "passed $1.$2 (1ms)"
  fi
  total=$((total+1))
  row="{\"name\":\"$1.$2\",\"status\":\"$st\",\"duration\":1,\"suite\":[\"h1\",\"h2\",\"$1\",\"$2\"]}"
  if [ -z "$rows" ]; then rows="$row"; else rows="$rows,$row"; fi
}
run Ns.A a1
run Ns.B b1
run Ns.Z z1
run Ns.Z z2
if [ -n "$dir" ] && [ -n "$name" ]; then
  mkdir -p "$dir"
  printf '{"results":{"summary":{"tests":%d,"passed":%d,"failed":%d,"pending":0,"skipped":0,"other":0},"tests":[%s]}}' "$total" "$passed" "$failed" "$rows" > "$dir/$name"
fi
if [ "$failed" -gt 0 ]; then exit 1; fi
exit 0
"""

let private hostConfig (tmpDir: string) : TestConfig =
    { Project = "P"
      Command = "sh"
      Args = Path.Combine(tmpDir, "host.sh")
      Group = "default"
      Environment = []
      FilterTemplate = Some "--filter-class {classes}"
      ClassJoin = " --filter-class "
      TimeoutSec = None
      ReportVerificationFormat = Ctrf
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

let private executed (tmpDir: string) : string list =
    let path = Path.Combine(tmpDir, "executed")

    if File.Exists path then
        File.ReadAllLines path |> List.ofArray
    else
        []

let private launches (tmpDir: string) : string list =
    let path = Path.Combine(tmpDir, "launches")

    if File.Exists path then
        File.ReadAllLines path |> List.ofArray
    else
        []

let private setFailing (tmpDir: string) (test: string) (failing: bool) =
    let flag = Path.Combine(tmpDir, $"fail-%s{test}")

    if failing then
        File.WriteAllText(flag, "")
    elif File.Exists flag then
        File.Delete flag

/// Index P's four tests. `Lib.foo` is called by A.a1 (and by Z.z1 when `zCallsFoo`),
/// `Lib.bar` by B.b1, `Lib.baz` by Z.z1 and Z.z2. One `RebuildProjects` call: P's rows
/// are replaced per project, so the tests must be written together.
let private seedIndex (dbPath: string) (zCallsFoo: bool) =
    let db = Database.create dbPath

    let sym name file : SymbolInfo =
        { FullName = name
          Kind = SymbolKind.Value
          Location = InSource(file, 1, 1, $"hash-%s{name}") }

    let tests = [ "Ns.A", "a1"; "Ns.B", "b1"; "Ns.Z", "z1"; "Ns.Z", "z2" ]

    let edge from target : Dependency =
        { FromSymbol = from
          ToSymbol = target
          Kind = DependencyKind.Calls
          Source = "core" }

    let edges =
        [ edge "Ns.A.a1" "Lib.foo"
          edge "Ns.B.b1" "Lib.bar"
          edge "Ns.Z.z1" "Lib.baz"
          edge "Ns.Z.z2" "Lib.baz"
          if zCallsFoo then
              edge "Ns.Z.z1" "Lib.foo" ]

    let analysis =
        AnalysisResult.Create(
            [ sym "Lib.foo" "src/Lib.fs"
              sym "Lib.bar" "src/Bar.fs"
              sym "Lib.baz" "src/Baz.fs" ]
            @ [ for cls, m in tests -> sym $"%s{cls}.%s{m}" $"tests/%s{cls}.fs" ],
            edges,
            [ for cls, m in tests ->
                  { SymbolFullName = $"%s{cls}.%s{m}"
                    TestProject = "P"
                    TestClass = cls
                    TestMethod = m } ]
        )

    db.RebuildProjects([ analysis ])
    clearSqlitePoolForDb db

/// A temp repository with the host script, a source tree the receipt can bind to, and the
/// index seeded.
let private withRepo (name: string) (zCallsFoo: bool) (body: string -> string -> unit) =
    withTempDir name (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, "host.sh"), hostScript)
        let src = Directory.CreateDirectory(Path.Combine(tmpDir, "src")).FullName
        File.WriteAllText(Path.Combine(src, "Lib.fs"), "module Lib\nlet foo = 1\n")
        let dbPath = Path.Combine(tmpDir, "tp.db")
        seedIndex dbPath zCallsFoo
        body tmpDir dbPath)

let private session (tmpDir: string) (dbPath: string) =
    let host = createModelHost (Unchecked.defaultof<_>) tmpDir

    let handler =
        create dbPath tmpDir (Some [ hostConfig tmpDir ]) None None None None []

    host.RegisterHandler(handler)
    host

let private settleAfter (host: PluginHost) (provoke: unit -> unit) : PluginStatus =
    let await = beginAwaitNextTerminal host "test-prune"
    provoke ()
    test <@ await.Wait(HangWait) @>
    waitForQuiescent host 10000

    match host.GetStatus("test-prune") with
    | Some status -> status
    | None -> failwith "test-prune reported no status"

let private build (host: PluginHost) =
    settleAfter host (fun () -> host.EmitBuildCompleted(BuildSucceeded))

/// What `fshw confirm` sends before the run it provokes.
let private confirmRun (host: PluginHost) =
    host.RunCommand("set-scope", [| "{\"scope\":\"full\"}" |])
    |> Async.RunSynchronously
    |> ignore

    build host

let private testScope (host: PluginHost) : FsHotWatch.Cli.IpcParsing.TestRunReport =
    match host.RunCommand("test-scope", [||]) |> Async.RunSynchronously with
    | Some reply -> FsHotWatch.Cli.IpcParsing.parseTestRunReport reply
    | None -> failwith "no test-scope command"

let private outstanding (tmpDir: string) : (string option * string option) list =
    match OutstandingFailure.load tmpDir with
    | OutstandingFailure.LoadedFailures.Loaded failures -> failures |> List.map (fun f -> f.Class, f.Method)
    | OutstandingFailure.LoadedFailures.Unreadable reason -> failwith $"outstanding ledger unreadable: {reason}"

let private baselineOf (tmpDir: string) : FullSuiteBaseline.Baseline option =
    match FullSuiteBaseline.load tmpDir with
    | FullSuiteBaseline.LoadedBaseline.Loaded b -> b
    | FullSuiteBaseline.LoadedBaseline.Unreadable reason -> failwith $"baseline unreadable: {reason}"

/// Grade the host's current state the way `check` (InnerLoop) or `confirm` (Confirmation)
/// does: the plugin status, the failing ledger entries, and the scope and baseline the
/// `test-scope` reply serves.
let private grade (host: PluginHost) (mode: FsHotWatch.Cli.CheckVerdict.CheckMode) =
    let report = testScope host

    let status =
        match host.GetStatus "test-prune" with
        | Some s -> s
        | None -> failwith "no status"

    let failing =
        host.GetErrorsByPlugin "test-prune"
        |> Map.toSeq
        |> Seq.sumBy (fun (_, entries) ->
            entries
            |> List.filter (fun e -> e.Severity = FsHotWatch.ErrorLedger.DiagnosticSeverity.Error)
            |> List.length)

    let inputs: FsHotWatch.Cli.CheckVerdict.CheckInputs =
        { PluginStatuses =
            Map.ofList
                [ "test-prune",
                  { Status = FsHotWatch.Cli.RunOnceOutput.StatusView.ofPluginStatus (fun _ -> None) status
                    Subtasks = []
                    ActivityTail = []
                    LastRun = None
                    Diagnostics = FsHotWatch.ErrorLedger.DiagnosticCounts.empty } ]
          FailingDiagnostics = failing
          UnattributableDiagnostics = 0
          WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
          Coverage = FsHotWatch.Cli.IpcParsing.Complete
          Scope = report.Scope
          Baseline = report.Baseline
          ProjectModel = ProjectModelFixtures.available }

    report, FsHotWatch.Cli.CheckVerdict.verdict mode inputs

let private isClean outcome =
    match outcome with
    | FsHotWatch.Cli.CheckVerdict.CheckOutcome.Clean _ -> true
    | _ -> false

/// The evidence the graded run holds, as the verdict publisher reads it: the green needs
/// one for the graded run with no refusals.
let private gradedEvidenceRefusals (report: FsHotWatch.Cli.IpcParsing.TestRunReport) : string list option =
    match report.GradedEvidence with
    | FsHotWatch.Cli.IpcParsing.GradedEvidenceReading.Reported(Some receipt) -> Some receipt.Refusals
    | _ -> None

let private zRows (tmpDir: string) =
    executed tmpDir |> List.filter (fun t -> t.StartsWith "Ns.Z.")

/// The cold first check: no baseline, `Lib.foo` queued (it breaks A.a1), and Z.z1 red
/// for a reason no queued symbol names. The run is widened to the full suite and P's host
/// stops at A.a1. That run is red, so it earns no baseline.
let private coldRedRun (tmpDir: string) (host: PluginHost) =
    test <@ (baselineOf tmpDir).IsNone @>
    let status = build host

    test
        <@
            match status with
            | Failed _ -> true
            | _ -> false
        @>

    // The widened run took P whole (no filter) and stopped at the first failure.
    test <@ launches tmpDir = [ "classes=[]" ] @>
    test <@ executed tmpDir = [ "Ns.A.a1" ] @>
    test <@ outstanding tmpDir = [ Some "Ns.A", Some "a1" ] @>
    test <@ (baselineOf tmpDir).IsNone @>

let private isFailed (status: PluginStatus) =
    match status with
    | Failed _ -> true
    | _ -> false

let private isCompleted (status: PluginStatus) =
    match status with
    | Completed _ -> true
    | _ -> false

let private redScenario (tmpDir: string) =
    PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
    setFailing tmpDir "Ns.A.a1" true
    setFailing tmpDir "Ns.Z.z1" true

// ---------------------------------------------------------------------------
// `check` after a stop-on-fail red full run
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``fixing only A.a1 after a stop-on-fail red run runs P whole again and reaches the red Z.z1`` () =
    withRepo "reach-fix-a" false (fun tmpDir dbPath ->
        redScenario tmpDir
        let host = session tmpDir dbPath
        coldRedRun tmpDir host

        // Fix A.a1 only. Z.z1 stays red.
        setFailing tmpDir "Ns.A.a1" false
        let status = build host

        // No baseline, so the run is the full suite again: P whole, and stop-on-fail now
        // reaches Z.z1. Before AUTOMATION-1581 this run was `classes=[|Ns.A|]` and green.
        test <@ List.last (launches tmpDir) = "classes=[]" @>
        test <@ zRows tmpDir = [ "Ns.Z.z1" ] @>
        test <@ isFailed status @>
        test <@ outstanding tmpDir = [ Some "Ns.Z", Some "z1" ] @>
        test <@ (baselineOf tmpDir).IsNone @>
        let _, outcome = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop
        test <@ not (isClean outcome) @>)

[<Fact(Timeout = 60000)>]
let ``once every red is fixed, one green whole-project run earns the baseline and the next check launches nothing`` () =
    withRepo "reach-recover" false (fun tmpDir dbPath ->
        redScenario tmpDir
        let host = session tmpDir dbPath
        coldRedRun tmpDir host
        setFailing tmpDir "Ns.A.a1" false
        build host |> ignore

        setFailing tmpDir "Ns.Z.z1" false
        let status = build host

        test <@ List.last (launches tmpDir) = "classes=[]" @>
        test <@ isCompleted status @>
        test <@ List.isEmpty (outstanding tmpDir) @>
        test <@ (baselineOf tmpDir) |> Option.map _.Projects = Some(Set.ofList [ "P" ]) @>
        let report, outcome = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop
        test <@ isClean outcome @>
        test <@ gradedEvidenceRefusals report = Some [] @>

        // Nothing changed: the next check launches nothing and stays green.
        let launchesBefore = launches tmpDir |> List.length
        build host |> ignore
        test <@ launches tmpDir |> List.length = launchesBefore @>
        let _, quiet = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop
        test <@ isClean quiet @>

        // Every test of P ran green in the run the baseline names.
        test
            <@ executed tmpDir |> List.rev |> List.take 4 |> List.rev = [ "Ns.A.a1"; "Ns.B.b1"; "Ns.Z.z1"; "Ns.Z.z2" ] @>)

// ---------------------------------------------------------------------------
// Whole-project evidence: a red unfiltered project is not covered
// ---------------------------------------------------------------------------

let private pInputs = Map.ofList [ "/repo/tests/P/P.fsproj", "identity-1" ]

let private completionOf (result: TestResult) : TestRunCompleted =
    let results = Map.ofList [ "P", result ]

    { RunId = Guid.NewGuid()
      TotalElapsed = TimeSpan.FromSeconds 1.0
      Outcome = Normal
      Results = results
      Verification = RunVerification.ofResults results }

let private evidenceOf (prior: EarnedEvidence option) (completed: TestRunCompleted) =
    EarnedEvidence.fromCompletion
        completed.RunId
        (Some 1L)
        (Some 1L)
        (Some pInputs)
        (Map.ofList [ "P", "/repo/tests/P/P.fsproj" ])
        (Set.ofList [ "P" ])
        0
        prior
        completed
    |> Option.get

[<Fact(Timeout = 10000)>]
let ``a red unfiltered project earns no whole-project evidence, so a restart restores none and P stays in the evidence gap``
    ()
    =
    let earned =
        evidenceOf None (completionOf (TestsFailed("failed Ns.A.a1 (1ms)", false, TimeSpan.FromSeconds 1.0)))

    test <@ EarnedEvidence.carriedCoverage (Some 1L) (Some pInputs) (Some earned) = Set.empty @>
    test <@ not (List.isEmpty earned.FailureReasons) @>

    let restored = EarnedEvidence.restored (EarnedEvidence.wholeProjectInputs earned)
    test <@ restored.IsNone @>

    test
        <@ EarnedEvidence.wholeProjectGap (Some 1L) (Some pInputs) (Set.ofList [ "P" ]) restored = Set.ofList [ "P" ] @>

[<Fact(Timeout = 10000)>]
let ``a green unfiltered project is whole-project evidence, and a later red run keeps the coverage the green one earned``
    ()
    =
    // Positive control, and the bound on the fix's cost: the coverage of a green whole
    // run is carried through a later red run of the same model, so the red does not
    // reopen the evidence gap that a whole-project run would be forced to close.
    let green =
        evidenceOf None (completionOf (TestsPassed("passed", false, TimeSpan.FromSeconds 1.0)))

    test <@ EarnedEvidence.carriedCoverage (Some 1L) (Some pInputs) (Some green) = Set.ofList [ "P" ] @>

    let red =
        evidenceOf (Some green) (completionOf (TestsFailed("failed Ns.A.a1 (1ms)", false, TimeSpan.FromSeconds 1.0)))

    test <@ EarnedEvidence.carriedCoverage (Some 1L) (Some pInputs) (Some red) = Set.ofList [ "P" ] @>

// ---------------------------------------------------------------------------
// `confirm`
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``confirm after fixing only A.a1 runs P whole and reaches the red Z.z1`` () =
    withRepo "reach-confirm" false (fun tmpDir dbPath ->
        redScenario tmpDir
        let host = session tmpDir dbPath
        coldRedRun tmpDir host
        setFailing tmpDir "Ns.A.a1" false

        let status = confirmRun host
        test <@ List.last (launches tmpDir) = "classes=[]" @>
        test <@ zRows tmpDir = [ "Ns.Z.z1" ] @>
        test <@ isFailed status @>
        let _, afterConfirm = grade host FsHotWatch.Cli.CheckVerdict.Confirmation
        test <@ not (isClean afterConfirm) @>)

[<Fact(Timeout = 60000)>]
let ``a red confirm's stop-on-fail run writes no baseline`` () =
    withRepo "reach-confirm-red" false (fun tmpDir dbPath ->
        redScenario tmpDir
        let host = session tmpDir dbPath
        coldRedRun tmpDir host

        let status = confirmRun host
        test <@ isFailed status @>
        test <@ List.isEmpty (zRows tmpDir) @>
        test <@ (baselineOf tmpDir).IsNone @>)

// ---------------------------------------------------------------------------
// Exposure routes: other ways a red full run can end before every test ran.
// ---------------------------------------------------------------------------

let private shortRedReport =
    """{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Ns.A.a1","status":"failed","duration":1,"suite":["h1","h2","Ns.A","a1"]}]}}"""

let private classify (evidence: ReportEvidence) (outcome: FsHotWatch.ProcessHelper.ProcessOutcome) =
    classifyTestOutcome evidence false (TimeSpan.FromSeconds 1.0) outcome

let private settleOne (result: TestResult) =
    let handler =
        create ":memory:" (isolatedRoot ()) (Some [ testConfigNamed "P" ]) None None None None []

    settleWith handler.Init result |> snd

[<Fact(Timeout = 10000)>]
let ``exposure (b) - a host that exits non-zero after writing a short coherent report lands on the ordinary red arm and now earns no baseline``
    ()
    =
    let result =
        classify
            (ReportRequested(FsHotWatch.Ctrf.tryVerdictReport shortRedReport))
            (FsHotWatch.ProcessHelper.ProcessOutcome.Failed(
                1,
                FsHotWatch.ProcessHelper.ProcessOutput.Drained "failed Ns.A.a1 (1ms)"
            ))

    test
        <@
            match result with
            | TestsFailed(_, false, _) -> true
            | _ -> false
        @>

    let settled = settleOne result
    test <@ settled.ExecutedFullSuite @>
    test <@ not settled.EarnsBaseline @>

[<Fact(Timeout = 10000)>]
let ``exposure (b) - a host that exits non-zero with no usable report is errored, not a full suite`` () =
    let result =
        classify
            (ReportRequested(Error "no report was written"))
            (FsHotWatch.ProcessHelper.ProcessOutcome.Failed(1, FsHotWatch.ProcessHelper.ProcessOutput.Drained "crash"))

    test
        <@
            match result with
            | TestsErrored _ -> true
            | _ -> false
        @>

    let settled = settleOne result
    test <@ not settled.ExecutedFullSuite @>
    test <@ not settled.EarnsBaseline @>

[<Fact(Timeout = 10000)>]
let ``exposure (c) - a timed-out unfiltered run files only a project-level red and now earns no baseline`` () =
    let result =
        classify
            (ReportRequested(FsHotWatch.Ctrf.tryVerdictReport shortRedReport))
            (FsHotWatch.ProcessHelper.ProcessOutcome.TimedOut(
                TimeSpan.FromSeconds 30.0,
                FsHotWatch.ProcessHelper.ProcessOutput.Drained "failed Ns.A.a1 (1ms)",
                FsHotWatch.ProcessHelper.KillOutcome.Killed
            ))

    test
        <@
            match result with
            | TestsTimedOut(_, _, false, _) -> true
            | _ -> false
        @>

    // A timeout's reds are never class-level, so quarantine re-runs P whole and only a
    // green whole run clears it: not a route to the false green even before the fix.
    let reds =
        failuresOf
            (fun _ -> FsHotWatch.RunLog.Ref.Unavailable "test")
            (fun _ -> ReportedFailures.none)
            Map.empty
            { Results = Map.ofList [ "P", result ]
              Elapsed = TimeSpan.Zero }

    test
        <@
            not reds.IsEmpty
            && reds |> List.forall (fun f -> f.Class.IsNone && f.Method.IsNone)
        @>

    let settled = settleOne result
    test <@ settled.ExecutedFullSuite @>
    test <@ not settled.EarnsBaseline @>

[<Fact(Timeout = 10000)>]
let ``exposure (d) - a host killed by a signal is errored whatever report it flushed, so it is never a full suite`` () =
    let result =
        classify
            (ReportRequested(FsHotWatch.Ctrf.tryVerdictReport shortRedReport))
            (FsHotWatch.ProcessHelper.ProcessOutcome.Failed(
                137,
                FsHotWatch.ProcessHelper.ProcessOutput.Drained "failed Ns.A.a1 (1ms)"
            ))

    test
        <@
            match result with
            | TestsErrored _ -> true
            | _ -> false
        @>

    let settled = settleOne result
    test <@ not settled.ExecutedFullSuite @>
    test <@ not settled.EarnsBaseline @>
