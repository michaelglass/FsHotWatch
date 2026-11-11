/// AUTOMATION-1581 REACH: what a stop-on-fail red full run that earns a full-suite baseline
/// lets through, and what still catches it.
///
/// THESE TESTS ENCODE TODAY'S BEHAVIOUR, INCLUDING THE DEFECT. They are an investigation,
/// not a specification: the assertions marked `DEFECT` describe what happens now and are
/// expected to flip when AUTOMATION-1581 is fixed.
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
open TestPrune.AstAnalyzer
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

// ---------------------------------------------------------------------------
// The positive control, at `settleDebt` itself.
// ---------------------------------------------------------------------------

let private redA1: OutstandingFailure =
    { Project = "P"
      Class = Some "Ns.A"
      Method = Some "a1"
      File = "<tests/P>"
      Entry = FsHotWatch.ErrorLedger.ErrorEntry.error "failed Ns.A.a1" }

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
              OutstandingFailures = [ redA1 ]
              CurrentInputTree = None
              CurrentModelGeneration = Some 1L
              DebtDuringFullRun = Map.empty
              OwedTo = fun _ -> Set.empty
              NoUnrunnableCoverers = lazy true
              Debt = stubDebt }

    test <@ settled.ExecutedFullSuite @>
    // DEFECT (AUTOMATION-1581): expected false; a red that never reached Z.z1/Z.z2 is
    // accounted for because one outstanding red names its project.
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
/// for a reason no queued symbol names. The run is widened to the full suite, P's host
/// stops at A.a1, and the run earns the baseline anyway.
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
    // DEFECT: the baseline is earned by a run that never executed B.b1, Z.z1 or Z.z2.
    test <@ (baselineOf tmpDir) |> Option.map _.Projects = Some(Set.ofList [ "P" ]) @>

// ---------------------------------------------------------------------------
// Q1: is there a sequence in which Z is never selected again and `check` goes green?
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``Q1 YES - fixing only A.a1 after the stop-on-fail baseline gives a green check that never ran the red Z.z1`` () =
    withRepo "reach-q1-fix-a" false (fun tmpDir dbPath ->
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        setFailing tmpDir "Ns.A.a1" true
        setFailing tmpDir "Ns.Z.z1" true

        let host = session tmpDir dbPath
        coldRedRun tmpDir host

        // Fix A.a1 only. Z.z1 stays red.
        setFailing tmpDir "Ns.A.a1" false
        File.WriteAllText(Path.Combine(tmpDir, "src", "Lib.fs"), "module Lib\nlet foo = 2\n")

        let status = build host

        // The filtered run took exactly Ns.A: the still-owed Lib.foo selects it, and the
        // quarantined red selects it. Neither reaches Z.
        test <@ List.last (launches tmpDir) = "classes=[|Ns.A|]" @>
        test <@ executed tmpDir = [ "Ns.A.a1"; "Ns.A.a1" ] @>
        test <@ zRows tmpDir = [] @>
        test <@ outstanding tmpDir = [] @>
        test <@ PendingQueueHelpers.loadQueue tmpDir = Set.empty @>

        test
            <@
                match status with
                | Completed _ -> true
                | _ -> false
            @>

        let report, outcome = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop

        // FALSE GREEN: `check` is Clean relative to the stop-on-fail run's baseline, and
        // the graded run's evidence refuses nothing (the red run counted P as covered whole).
        test <@ isClean outcome @>
        test <@ gradedEvidenceRefusals report = Some [] @>
        test <@ zRows tmpDir = [] @>)

[<Fact(Timeout = 60000)>]
let ``Q1 NO when Z calls the owed symbol - the uncommitted Lib.foo debt selects Ns.Z and the red Z.z1 runs`` () =
    withRepo "reach-q1-debt" true (fun tmpDir dbPath ->
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        setFailing tmpDir "Ns.A.a1" true
        setFailing tmpDir "Ns.Z.z1" true

        let host = session tmpDir dbPath
        coldRedRun tmpDir host

        setFailing tmpDir "Ns.A.a1" false
        File.WriteAllText(Path.Combine(tmpDir, "src", "Lib.fs"), "module Lib\nlet foo = 2\n")

        let status = build host

        // Lib.foo stayed queued (P was red, so it could not commit) and now selects both
        // classes that call it. Not P in full: B.b1 is still never run.
        test <@ List.last (launches tmpDir) = "classes=[|Ns.A||Ns.Z|]" @>
        test <@ zRows tmpDir = [ "Ns.Z.z1" ] @>

        test
            <@
                match status with
                | Failed _ -> true
                | _ -> false
            @>

        test <@ outstanding tmpDir = [ Some "Ns.Z", Some "z1" ] @>
        let _, outcome = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop
        test <@ not (isClean outcome) @>)

/// The false green of the first Q1 test, reached in `host`.
let private falseGreen (tmpDir: string) (host: PluginHost) =
    PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
    setFailing tmpDir "Ns.A.a1" true
    setFailing tmpDir "Ns.Z.z1" true
    coldRedRun tmpDir host
    setFailing tmpDir "Ns.A.a1" false
    File.WriteAllText(Path.Combine(tmpDir, "src", "Lib.fs"), "module Lib\nlet foo = 2\n")
    build host |> ignore
    let _, outcome = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop
    test <@ isClean outcome @>

[<Fact(Timeout = 60000)>]
let ``Q1 YES - in the same daemon a later check over the unchanged tree launches nothing and stays green, never running Z``
    ()
    =
    withRepo "reach-q1-later" false (fun tmpDir dbPath ->
        let host = session tmpDir dbPath
        falseGreen tmpDir host

        // A rebuild with nothing changed launches nothing and stays green.
        let launchesBefore = launches tmpDir |> List.length
        build host |> ignore
        test <@ launches tmpDir |> List.length = launchesBefore @>
        let _, quiet = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop
        test <@ isClean quiet @>

        test <@ zRows tmpDir = [] @>)

[<Fact(Timeout = 60000)>]
let ``Q1 NO after a restart of a daemon whose model publishes no project identities - the evidence gap runs P whole and reaches Z.z1``
    ()
    =
    withRepo "reach-q1-restart" false (fun tmpDir dbPath ->
        let host = session tmpDir dbPath
        falseGreen tmpDir host

        PendingVerification.save tmpDir (Set.ofList [ "Lib.bar" ])
        File.WriteAllText(Path.Combine(tmpDir, "src", "Bar.fs"), "module Bar\nlet bar = 1\n")

        // A new daemon over the same repository: the baseline and the (empty) red ledger
        // are read from disk, but no whole-project evidence is restored because this
        // fixture model publishes no compile-input identities.
        let host2 = session tmpDir dbPath
        let status = build host2

        test <@ List.last (launches tmpDir) = "classes=[]" @>
        test <@ zRows tmpDir = [ "Ns.Z.z1" ] @>

        test
            <@
                match status with
                | Failed _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 10000)>]
let ``Q1 restart with identities - the stop-on-fail red run's whole-project evidence is restored and leaves no evidence gap for P``
    ()
    =
    // A production daemon publishes each project's compile-input identity (its options:
    // source list, defines, references, not file contents), records the whole-project
    // coverage of its evidence with them, and restores it at startup. That coverage counts
    // an unfiltered RED project (`EarnedEvidence.fromCompletion`), so a restart does not
    // force P whole while P's options are unchanged.
    let inputs = Map.ofList [ "/repo/tests/P/P.fsproj", "identity-1" ]

    let results =
        Map.ofList [ "P", TestsFailed("failed Ns.A.a1 (1ms)", false, TimeSpan.FromSeconds 1.0) ]

    let runId = Guid.NewGuid()

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

    let earned =
        EarnedEvidence.fromCompletion
            runId
            (Some 1L)
            (Some 1L)
            (Some inputs)
            (Map.ofList [ "P", "/repo/tests/P/P.fsproj" ])
            (Set.ofList [ "P" ])
            0
            None
            completed
        |> Option.get

    // DEFECT (same class, second site): the red stop-on-fail project is whole-project coverage.
    test <@ EarnedEvidence.carriedCoverage (Some 1L) (Some inputs) (Some earned) = Set.ofList [ "P" ] @>
    test <@ List.isEmpty earned.FailureReasons |> not @>

    let restored = EarnedEvidence.restored (EarnedEvidence.wholeProjectInputs earned)
    test <@ restored.IsSome @>

    // The restarted daemon (model generation 1 again, same identity): no gap, so the next
    // launch is impact-filtered and Z is not forced.
    test <@ EarnedEvidence.wholeProjectGap (Some 1L) (Some inputs) (Set.ofList [ "P" ]) restored = Set.empty @>

// ---------------------------------------------------------------------------
// Q2: does `confirm` report green from the earned baseline without running Z?
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``Q2 NO - confirm on the falsely green tree refuses the filtered scope, runs P whole and reaches the red Z.z1`` () =
    withRepo "reach-q2" false (fun tmpDir dbPath ->
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        setFailing tmpDir "Ns.A.a1" true
        setFailing tmpDir "Ns.Z.z1" true

        let host = session tmpDir dbPath
        coldRedRun tmpDir host

        setFailing tmpDir "Ns.A.a1" false
        File.WriteAllText(Path.Combine(tmpDir, "src", "Lib.fs"), "module Lib\nlet foo = 2\n")
        build host |> ignore

        let _, checkOutcome = grade host FsHotWatch.Cli.CheckVerdict.InnerLoop
        test <@ isClean checkOutcome @>

        // `confirm` reads the same state: the graded run is the filtered one, so the
        // scope is not FullSuite and `confirm` must launch a run (`confirmNeedsFullRun`).
        let report, confirmOutcome = grade host FsHotWatch.Cli.CheckVerdict.Confirmation

        test
            <@
                match confirmOutcome with
                | FsHotWatch.Cli.CheckVerdict.CheckOutcome.UnearnedScope _ -> true
                | _ -> false
            @>

        test
            <@ FsHotWatch.Cli.CheckVerdict.confirmNeedsFullRun FsHotWatch.Cli.CheckVerdict.Confirmation report.Scope @>

        // The run it launches is P whole; with A.a1 fixed, stop-on-fail now reaches Z.z1.
        let status = confirmRun host
        test <@ List.last (launches tmpDir) = "classes=[]" @>
        test <@ zRows tmpDir = [ "Ns.Z.z1" ] @>

        test
            <@
                match status with
                | Failed _ -> true
                | _ -> false
            @>

        let _, afterConfirm = grade host FsHotWatch.Cli.CheckVerdict.Confirmation
        test <@ not (isClean afterConfirm) @>)

[<Fact(Timeout = 60000)>]
let ``Q2 corollary - a confirm while A.a1 is still red stops at A again, is red, and re-earns the baseline without Z``
    ()
    =
    withRepo "reach-q2-red" false (fun tmpDir dbPath ->
        PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])
        setFailing tmpDir "Ns.A.a1" true
        setFailing tmpDir "Ns.Z.z1" true

        let host = session tmpDir dbPath
        coldRedRun tmpDir host
        let firstBaseline = baselineOf tmpDir |> Option.map _.RunId

        let status = confirmRun host

        test
            <@
                match status with
                | Failed _ -> true
                | _ -> false
            @>

        test <@ zRows tmpDir = [] @>
        // DEFECT: the confirm's own stop-on-fail run is a new baseline too.
        test <@ baselineOf tmpDir |> Option.map _.RunId <> firstBaseline @>
        test <@ (baselineOf tmpDir).IsSome @>)
