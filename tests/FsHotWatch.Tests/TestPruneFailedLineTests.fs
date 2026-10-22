/// What the runner's console may file as a failing test, and what happens to a red that
/// names no test the project has.
///
/// A test project's captured output carries more than the runner's result lines: a
/// project's own preflight, a gate or a test that prints prose shares the stream. A
/// console line is a failing test only in the runner's result shape —
/// `failed [(qualifier)] <name> (<duration>)` — and prose that happens to open with the
/// word `failed` is not one. Field case: a package-pin gate printed
/// "failed twice. Pin the version obj/project.assets.json resolves today, then run"
/// beside an unrelated red, the ledger filed a test named "twice. Pin the version…",
/// and every later run carried a red no run could ever execute.
module FsHotWatch.Tests.TestPruneFailedLineTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

let private project = "Build.Tests"
let private realClass = "Build.Tests.PinGateTests"
let private realMethod = "a floating version is red"
let private otherClass = "Build.Tests.OtherTests"

/// The field output, as the gate wrapped its report.
let private gateProse =
    [ "(when 5.3.0 shipped): 16 client tests could not load Fable.Core 5.3.0.0 and the release confirm"
      "failed twice. Pin the version obj/project.assets.json resolves today, then run"
      "`dotnet restore --force-evaluate`. A deliberate range belongs in"
      "the gate's allow-list with its reason." ]

let private realFailureLine = $"failed %s{realClass}.%s{realMethod} (27ms)"

let private summary failed total =
    [ "Test run summary: Failed!"
      $"  total: %d{total}"
      $"  failed: %d{failed}"
      $"  succeeded: %d{total - failed}" ]

let private failedRun (lines: string list) =
    TestsFailed(String.concat "\n" lines, false, TimeSpan.FromSeconds 1.0)

let private results (result: TestResult) : TestResults =
    { Results = Map.ofList [ project, result ]
      Elapsed = TimeSpan.Zero }

let private noRunLog (_: string) : FsHotWatch.RunLog.Ref =
    FsHotWatch.RunLog.Ref.Unavailable "no run log in this test"

/// The ledger entry the field daemon stored, field for field.
let private phantom: OutstandingFailure =
    { Project = project
      Class = Some "twice. Pin the version obj/project.assets"
      Method = Some "json resolves today, then run"
      File = $"<tests/%s{project}>"
      Entry =
        FsHotWatch.ErrorLedger.ErrorEntry.error
            "failed twice. Pin the version obj/project.assets.json resolves today, then run" }

let private ctrf (rows: (string * string) list) =
    let count status =
        rows |> List.filter (fun (_, s) -> s = status) |> List.length

    let entries =
        rows
        |> List.map (fun (name, status) -> $"""{{"name":"%s{name}","status":"%s{status}","duration":1}}""")
        |> String.concat ","

    $"""{{"results":{{"summary":{{"tests":%d{rows.Length},"passed":%d{count "passed"},"failed":%d{count "failed"},"pending":0,"skipped":%d{count "skipped"},"other":0}},"tests":[%s{entries}]}}}}"""

let private greenReport =
    ctrf
        [ $"%s{realClass}.%s{realMethod}", "passed"
          $"%s{otherClass}.still green", "passed" ]

let private projConfig: TestConfig =
    { Project = project
      Command = "sh"
      Args = "-c \"exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = Some "-- --filter-class {classes}"
      ClassJoin = "|"
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect
      Starvation = None
      AlwaysClasses = [] }

/// A completed run whose CTRF report sits where the runner writes it.
let private finished (repoRoot: string) (report: string option) (result: TestResult) (launch: TestRunLaunch) =
    let runId = Guid.NewGuid()
    let dir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    report
    |> Option.iter (fun json -> File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), json))

    let runResults = Map.ofList [ project, result ]

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = runResults
          Verification = RunVerification.ofResults runResults }

    Custom(TestsFinished(started, completed, launch))

let private drive (repoRoot: string) (runs: PluginEvent<TestPruneMsg> list) =
    let handler =
        create ":memory:" repoRoot (Some [ projConfig ]) None None None None []

    let ctx, _statuses, ledger = makeTestPruneRecordingCtx ()

    let final =
        runs
        |> List.fold (fun state ev -> handler.Update ctx state ev |> Async.RunSynchronously) handler.Init

    final,
    ledger
    |> Seq.filter (fun kv -> not kv.Value.IsEmpty)
    |> Seq.map _.Key
    |> List.ofSeq

let private passedFiltered = TestsPassed("ok", true, TimeSpan.FromSeconds 1.0)

// --- the result-line shape ---

[<Fact(Timeout = 10000)>]
let ``prose opening with 'failed' is not a failed-test line; the runner's result lines are`` () =
    test <@ not (isFailedTestLine "failed twice. Pin the version obj/project.assets.json resolves today, then run") @>
    test <@ not (isFailedTestLine "failed to connect to 127.0.0.1 (exit 1)") @>
    test <@ not (isFailedTestLine "failed: 2") @>
    test <@ isFailedTestLine realFailureLine @>
    test <@ isFailedTestLine "  failed (canceled) A.B.e (2m 3s 4ms)" @>
    test <@ isFailedTestLine "failed A.B.c (10s 112ms)" @>
    test <@ isFailedTestLine "failed A.B.theory(x: 1.5) (0.4ms)" @>

[<Fact(Timeout = 10000)>]
let ``a gate's prose beside a real red files only the real red`` () =
    let output = gateProse @ [ realFailureLine ] @ summary 1 2

    let reds = failuresOf noRunLog noReport Map.empty (results (failedRun output))

    test <@ reds |> List.map (fun f -> f.Class, f.Method) = [ Some realClass, Some realMethod ] @>

    let report =
        formatFailureReport RedRunEnding.Exited [] project savedLog (String.concat "\n" output)
        |> String.concat "\n"

    test <@ report.Contains $"%s{project}: 1 test(s) failed:" @>
    test <@ not (report.Contains "  failed twice") @>

[<Fact(Timeout = 10000)>]
let ``a red run whose only 'failed' line is prose is a project-level red a full run can clear`` () =
    let output = gateProse @ summary 1 2

    let red =
        failuresOf noRunLog noReport Map.empty (results (failedRun output))
        |> List.exactlyOne

    test <@ red.Class = None @>
    test <@ red.Method = None @>

// --- the reproducer, through the real handler ---

[<Fact(Timeout = 20000)>]
let ``a gate's prose beside an unrelated red leaves the ledger clean once the red re-runs green`` () =
    withTempDir "failed-line-prose" (fun repoRoot ->
        let red =
            finished
                repoRoot
                None
                (failedRun (gateProse @ [ realFailureLine ] @ summary 1 2))
                (fullSuiteLaunch [ project ])

        let afterRed, _ = drive repoRoot [ red ]

        // What the next run is launched against: the ledger's own quarantine.
        let quarantined =
            OutstandingFailure.quarantine (Set.singleton project) afterRed.OutstandingFailures Map.empty

        test <@ quarantined = Map.ofList [ project, [ realClass ] ] @>

        let green =
            finished repoRoot (Some greenReport) passedFiltered (filteredLaunch [ project, quarantined.[project] ])

        let final, ledgerFiles = drive repoRoot [ red; green ]

        test <@ List.isEmpty final.OutstandingFailures @>
        test <@ List.isEmpty ledgerFiles @>)

[<Fact(Timeout = 20000)>]
let ``a stored red naming a class the project does not have clears on the project's next green run`` () =
    withTempDir "failed-line-phantom" (fun repoRoot ->
        // A workspace that already holds the field entry from an older daemon.
        OutstandingFailure.save repoRoot [ phantom ]

        let quarantined =
            OutstandingFailure.quarantine (Set.singleton project) [ phantom ] (Map.ofList [ project, [ realClass ] ])

        let green =
            finished repoRoot (Some greenReport) passedFiltered (filteredLaunch [ project, quarantined.[project] ])

        let final, ledgerFiles = drive repoRoot [ green ]

        test <@ List.isEmpty final.OutstandingFailures @>
        test <@ List.isEmpty ledgerFiles @>)

// --- what retiring an absent class may NOT do ---

let private selected =
    Map.ofList [ project, CoveredClasses(Set.ofList [ realClass; phantom.Class.Value ]) ]

[<Fact(Timeout = 10000)>]
let ``an absent class is retired only when the run selected it and the project's report is complete`` () =
    let complete = Map.ofList [ project, Set.ofList [ realClass; otherClass ] ]
    let retired, kept = OutstandingFailure.partitionAbsent selected complete [ phantom ]
    test <@ retired = [ phantom ] @>
    test <@ List.isEmpty kept @>

    // No complete green report for the project: nothing is known about its classes.
    test <@ OutstandingFailure.partitionAbsent selected Map.empty [ phantom ] = ([], [ phantom ]) @>

    // Not selected by this run: the run cannot speak for it.
    let other = Map.ofList [ project, CoveredClasses(Set.ofList [ realClass ]) ]
    test <@ OutstandingFailure.partitionAbsent other complete [ phantom ] = ([], [ phantom ]) @>

    // The class has rows — skipped ones included: it exists, so its red waits for its pass.
    let withPhantomRows =
        Map.ofList [ project, Set.ofList [ realClass; phantom.Class.Value ] ]

    test <@ OutstandingFailure.partitionAbsent selected withPhantomRows [ phantom ] = ([], [ phantom ]) @>

    // A project-level red names no class and is the whole-project run's to clear.
    let projectLevel =
        { phantom with
            Class = None
            Method = None }

    test <@ OutstandingFailure.partitionAbsent selected complete [ projectLevel ] = ([], [ projectLevel ]) @>

[<Fact(Timeout = 10000)>]
let ``the classes a report ran are read only from a complete report, skipped rows included`` () =
    let report =
        ctrf
            [ $"%s{realClass}.%s{realMethod}", "passed"
              $"%s{otherClass}.skipped one", "skipped" ]

    test <@ classesRunOfReport report = Some(Set.ofList [ realClass; otherClass ]) @>

    let truncated = report.Replace("\"tests\":2", "\"tests\":3")
    test <@ classesRunOfReport truncated = None @>
    test <@ classesRunOfReport (ctrf []) = None @>
