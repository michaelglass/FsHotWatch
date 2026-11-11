/// AUTOMATION-1444: the K_P records the test runner files (ADR-059 §16 step 7a) and the
/// check-time lookup that reads them (step 7b), when a full-suite run fails a test and a
/// later run of the same check passes it again at the same K_P.
///
/// Shape of intelligence's replay step 34 (fshw 0.14.0-alpha.133): full run 6ef651bd failed
/// exactly one named Build.Dev test; the graded impact run eb571426 ran it again and passed;
/// today graded green, and the check's shadow line said `stricter`, failing that project.
/// Every record is filed through the runner's own filing function, and every time is a
/// value the test chose.
module FsHotWatch.Tests.ShadowKpSupersessionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestEvidenceRun
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero)

let private projects = [ "A.Tests"; "B.Tests" ]

/// What the configuration requires: the build the observation reports, and both projects.
let private configured: Configured =
    { RequiredPlugins = [ "build" ]
      TestProjects = projects }

let private candidate (project: string) : Candidate =
    { Project = project
      ProjectFile = $"/w/tests/%s{project}/%s{project}.fsproj"
      Closure = []
      TestConfig = "{}" }

/// The project's K_P over the tree both runs saw.
let private kpOf (project: string) =
    TestEvidenceKey.ofLabels [ "toolchain", "t"; "closure", project; "scope", "verdict-tree" ]

let private fullKeyOf project = testActionKey (kpOf project) "full"

/// A CTRF report naming each row; `unnamedFailures` failed rows carry no name.
let private reportOf (failed: string list) (passed: string list) (unnamedFailures: int) =
    let row (name: string) status =
        $"""{{"name":"%s{name}","status":"%s{status}"}}"""

    let rows =
        (passed |> List.map (fun n -> row n "passed"))
        @ (failed |> List.map (fun n -> row n "failed"))
        @ List.replicate unnamedFailures """{"status":"failed"}"""
        |> String.concat ","

    let f = failed.Length + unnamedFailures

    $"""{{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"r","results":{{"tool":{{"name":"xUnit.net v3"}},"summary":{{"tests":%d{f + passed.Length},"passed":%d{passed.Length},"failed":%d{f},"pending":0,"skipped":0,"other":0,"suites":1,"start":0,"stop":1}},"tests":[%s{rows}]}}}}"""

/// One project run, `filtered` for an impact run, filed as the runner files it after
/// `minutes` past t0 by run `runId`.
let private file
    (kp: OutcomeStore.Store)
    (project: string)
    (runId: string)
    (minutes: float)
    (filtered: bool)
    (failed: string list)
    (passed: string list)
    (unnamedFailures: int)
    =
    let result =
        if failed.IsEmpty && unnamedFailures = 0 then
            Events.TestsPassed("", filtered, TimeSpan.FromSeconds 1.0)
        else
            Events.TestsFailed("", filtered, TimeSpan.FromSeconds 1.0)

    let run: ProducedRun =
        { Project = project
          Result = result
          Report = Some(reportOf failed passed unnamedFailures)
          CoverageRequired = false
          Coverage = None }

    let producer: TestEvidenceStore.Producer =
        { Worktree = "/w"
          RunId = runId
          TreeHash = "sha256:t1"
          ProducerHash = "fshw 1"
          EarnedAt = t0.AddMinutes minutes }

    let key = Ok(kpOf project)

    outcomeRecordOf (candidate project) (Some key) (Some key) run producer
    |> Option.map (fileOutcome kp (fullKeyOf project) run)
    |> ignore

let private suite (run: string) (project: string) (failed: string list) (passed: string list) : Suite =
    { RunId = Some run
      Project = project
      Counts =
        { Total = failed.Length + passed.Length
          Passed = passed.Length
          Failed = failed.Length
          Skipped = 0 }
      Passed = List.isEmpty failed
      FailedTests = Some failed
      PassedTests = Set.ofList passed
      Report = None }

/// The check publication of step 34: graded from impact run r2, after full run r1 the same
/// check produced; the daemon names each project's full-run K_P key.
let private check (suites: Suite list) : Observation =
    { Command = Verdict.Check
      Tree = "sha256:t1"
      Producer = "fshw 1"
      Worktree = "/w"
      Today = Class.Green
      TodayOutcome = "green"
      ExitCode = 0
      Plugins = [ "build", Some OutcomeStore.Outcome.Pass ]
      DiagnosticsFailed = false
      GradedRunId = Some "r2"
      BaselineRunId = None
      Scope = GradedScope.ImpactFiltered
      Suites = suites
      RunOrder = [ "r1"; "r2" ]
      RecordedAt = t0.AddMinutes 10.0
      OutcomeKeys = projects |> List.map (fun p -> p, (fullKeyOf p).Value) |> Map.ofList }

let private withStores (body: string -> OutcomeStore.Store -> OutcomeStore.Store -> unit) =
    withTempDir "kp-supersede" (fun repo ->
        let open' (root: string) =
            OutcomeStore.Store(
                { Root = root
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        body repo (open' (storeRoot repo)) (open' (Path.Combine(repo, "kp-store"))))

/// B.Tests passed in full in r1 and was not selected in r2.
let private bPassedInFull (kp: OutcomeStore.Store) =
    file kp "B.Tests" "r1" 5.0 false [] [ "B.x" ] 0

let private bSuite = suite "r1" "B.Tests" [] [ "B.x" ]

[<Fact>]
let ``a full-suite K_P fail whose one named failure a later impact run passed at the same K_P is green under check``
    ()
    =
    withStores (fun repo store kp ->
        file kp "A.Tests" "r1" 5.0 false [ "A.x" ] [ "A.y"; "A.z" ] 0
        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [] [ "A.x" ] 0

        let o =
            check
                [ suite "r2" "A.Tests" [] [ "A.x" ]
                  suite "r1" "A.Tests" [ "A.x" ] [ "A.y"; "A.z" ]
                  bSuite ]

        test <@ observeWith repo store (Some kp) configured o = LookupVerdict.Answer.Green @>)

// ---------------------------------------------------------------------------
// Never looser: each of these must stay red, as today's verdict is or would be.
// ---------------------------------------------------------------------------

let private stepThirtyFour =
    [ suite "r2" "A.Tests" [] [ "A.x" ]
      suite "r1" "A.Tests" [ "A.x" ] [ "A.y"; "A.z" ]
      bSuite ]

[<Fact>]
let ``a failure the later run failed again stays red`` () =
    withStores (fun repo store kp ->
        file kp "A.Tests" "r1" 5.0 false [ "A.x" ] [ "A.y" ] 0
        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [ "A.x" ] [] 0

        let o =
            { check
                  [ suite "r2" "A.Tests" [ "A.x" ] []
                    suite "r1" "A.Tests" [ "A.x" ] [ "A.y" ]
                    bSuite ] with
                Today = Class.Red
                TodayOutcome = "red"
                ExitCode = 1 }

        test <@ observeWith repo store (Some kp) configured o = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``a later passing run that did not run the failed test leaves the K_P fail red`` () =
    withStores (fun repo store kp ->
        file kp "A.Tests" "r1" 5.0 false [ "A.x" ] [ "A.y" ] 0
        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [] [ "A.y" ] 0

        let o =
            check
                [ suite "r2" "A.Tests" [] [ "A.y" ]
                  suite "r1" "A.Tests" [ "A.x" ] [ "A.y" ]
                  bSuite ]

        test <@ observeWith repo store (Some kp) configured o = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``a later run that passed only some of the failures leaves the K_P fail red`` () =
    withStores (fun repo store kp ->
        file kp "A.Tests" "r1" 5.0 false [ "A.x"; "A.w" ] [ "A.y" ] 0
        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [] [ "A.x" ] 0

        let o =
            check
                [ suite "r2" "A.Tests" [] [ "A.x" ]
                  suite "r1" "A.Tests" [ "A.x"; "A.w" ] [ "A.y" ]
                  bSuite ]

        test <@ observeWith repo store (Some kp) configured o = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``a full fail with a failure its report does not name is never cleared`` () =
    withStores (fun repo store kp ->
        file kp "A.Tests" "r1" 5.0 false [ "A.x" ] [ "A.y" ] 1
        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [] [ "A.x" ] 0

        test
            <@
                observeWith repo store (Some kp) configured (check stepThirtyFour) = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``a timed-out full run is never cleared, whatever its report names`` () =
    withStores (fun repo store kp ->
        let timedOut: ProducedRun =
            { Project = "A.Tests"
              Result = Events.TestsTimedOut("", TimeSpan.FromSeconds 30.0, false, TimeSpan.FromSeconds 30.0)
              Report = Some(reportOf [ "A.x" ] [ "A.y" ] 0)
              CoverageRequired = false
              Coverage = None }

        let producer: TestEvidenceStore.Producer =
            { Worktree = "/w"
              RunId = "r1"
              TreeHash = "sha256:t1"
              ProducerHash = "fshw 1"
              EarnedAt = t0.AddMinutes 5.0 }

        let key = Ok(kpOf "A.Tests")

        outcomeRecordOf (candidate "A.Tests") (Some key) (Some key) timedOut producer
        |> Option.map (fileOutcome kp (fullKeyOf "A.Tests") timedOut)
        |> ignore

        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [] [ "A.x" ] 0

        test
            <@
                observeWith repo store (Some kp) configured (check stepThirtyFour) = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``a pass filed before the full fail never clears it`` () =
    withStores (fun repo store kp ->
        file kp "A.Tests" "r0" 4.0 true [] [ "A.x" ] 0
        file kp "A.Tests" "r1" 5.0 false [ "A.x" ] [ "A.y"; "A.z" ] 0
        bPassedInFull kp

        test
            <@
                observeWith repo store (Some kp) configured (check stepThirtyFour) = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``a newer full fail at the same K_P is red again, though an older one was cleared`` () =
    withStores (fun repo store kp ->
        file kp "A.Tests" "r1" 5.0 false [ "A.x" ] [ "A.y"; "A.z" ] 0
        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [] [ "A.x" ] 0
        file kp "A.Tests" "r3" 9.0 false [ "A.x" ] [ "A.y"; "A.z" ] 0

        test
            <@
                observeWith repo store (Some kp) configured (check stepThirtyFour) = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``confirm never accepts a cleared full fail: it needs the full suite itself to pass`` () =
    // Today's confirm grades the full-suite run, and a failure in it is red whatever ran after.
    withStores (fun repo store kp ->
        file kp "A.Tests" "r1" 5.0 false [ "A.x" ] [ "A.y"; "A.z" ] 0
        bPassedInFull kp
        file kp "A.Tests" "r2" 7.5 true [] [ "A.x" ] 0

        let o =
            { check stepThirtyFour with
                Command = Verdict.Confirm
                Today = Class.Red
                TodayOutcome = "red"
                ExitCode = 1
                GradedRunId = Some "r1"
                Scope = GradedScope.FullSuite }

        test <@ observeWith repo store (Some kp) configured o = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)


/// The failed tests the fail record of a full run reporting `report` names, as filed.
let private namedBy (report: string) =
    withTempDir "kp-named" (fun dir ->
        let kp =
            OutcomeStore.Store(
                { Root = dir
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        let run: ProducedRun =
            { Project = "A.Tests"
              Result = Events.TestsFailed("", false, TimeSpan.FromSeconds 1.0)
              Report = Some report
              CoverageRequired = false
              Coverage = None }

        let producer: TestEvidenceStore.Producer =
            { Worktree = "/w"
              RunId = "r1"
              TreeHash = "sha256:t1"
              ProducerHash = "fshw 1"
              EarnedAt = t0 }

        let key = Ok(kpOf "A.Tests")

        outcomeRecordOf (candidate "A.Tests") (Some key) (Some key) run producer
        |> Option.map (fileOutcome kp (fullKeyOf "A.Tests") run)
        |> ignore

        kp.LookupWithOutputs(fullKeyOf "A.Tests")
        |> Option.bind snd
        |> TestFailureClearance.failedTestsOf)

[<Fact>]
let ``a fail record names its failures only when its summary counts no other failure`` () =
    let summaryWith (failed: int) (other: int) =
        $"""{{"results":{{"summary":{{"tests":3,"passed":1,"failed":%d{failed},"pending":0,"skipped":0,"other":%d{other}}},"tests":[{{"name":"A.y","status":"passed"}},{{"name":"A.x","status":"failed"}}]}}}}"""

    test <@ namedBy (summaryWith 1 0) = Some [ "A.x" ] @>
    // A failure the rows do not name, and an errored row, leave the fail unnamed.
    test <@ namedBy (summaryWith 2 0) = None @>
    test <@ namedBy (summaryWith 1 1) = None @>
