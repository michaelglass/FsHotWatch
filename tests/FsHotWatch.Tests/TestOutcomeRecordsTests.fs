/// ADR-059 §11 step 7: the test runner files an outcome record for every project it ran,
/// under the K_P key of the inputs it ran over, taken at LAUNCH and still the key at
/// completion. A run whose inputs moved under it files nothing: its reports describe no
/// tree (the second half of AUTOMATION-641 and 699). Every time is a value the test chose.
module FsHotWatch.Tests.TestOutcomeRecordsTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestEvidenceRun

let private t0 = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

let private candidate: Candidate =
    { Project = "A.Tests"
      ProjectFile = "/w/tests/A.Tests/A.Tests.fsproj"
      Closure = []
      TestConfig = "{}" }

let private keyOf (closure: string) =
    TestEvidenceKey.ofLabels [ "toolchain", "t"; "closure", closure; "scope", "verdict-tree" ]

let private producer: TestEvidenceStore.Producer =
    { Worktree = "/w"
      RunId = "run-1"
      TreeHash = "sha256:tree"
      ProducerHash = "fshw-1"
      EarnedAt = t0 }

let private report (passed: int) (failed: int) =
    let rows =
        List.init passed (fun i -> $"""{{"name":"T.p%d{i}","status":"passed"}}""")
        @ List.init failed (fun i -> $"""{{"name":"T.f%d{i}","status":"failed"}}""")
        |> String.concat ","

    $"""{{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"r","results":{{"tool":{{"name":"xUnit.net v3"}},"summary":{{"tests":%d{passed + failed},"passed":%d{passed},"failed":%d{failed},"pending":0,"skipped":0,"other":0,"suites":1,"start":0,"stop":1}},"tests":[%s{rows}]}}}}"""

let private run (result: TestResult) (ctrf: string option) : ProducedRun =
    { Project = "A.Tests"
      Result = result
      Report = ctrf
      CoverageRequired = false
      Coverage = None }

let private passedFull = TestsPassed("", false, TimeSpan.FromSeconds 1.0)

let private recordFor launch completion produced =
    outcomeRecordOf candidate (Some launch) (Some completion) produced producer

[<Fact>]
let ``a full green over a key that held still is a pass under that key, scoped full`` () =
    let key = keyOf "c1"

    let record =
        recordFor (Ok key) (Ok key) (run passedFull (Some(report 3 0))) |> Option.get

    test <@ record.Outcome = OutcomeStore.Outcome.Pass @>
    test <@ record.Key.Kind = "test" @>
    test <@ record.Key.Value = (testActionKey key "full").Value @>
    test <@ record.Subject = "A.Tests" @>
    test <@ record.RecordedAt = t0 @>
    test <@ record.Producer.RunId = Some "run-1" @>
    test <@ record.Counts |> Option.map _.Total = Some 3 @>

[<Fact>]
let ``a red is recorded too, under the same key a green would use`` () =
    let key = keyOf "c1"

    let record =
        recordFor (Ok key) (Ok key) (run (TestsFailed("", false, TimeSpan.Zero)) (Some(report 2 1)))
        |> Option.get

    test <@ record.Outcome = OutcomeStore.Outcome.Fail @>
    test <@ record.Key.Value = (testActionKey key "full").Value @>
    test <@ record.Counts |> Option.map _.Failed = Some 1 @>

[<Fact>]
let ``a filtered run is scoped impact and never stands in for full`` () =
    let key = keyOf "c1"

    let record =
        recordFor (Ok key) (Ok key) (run (TestsPassed("", true, TimeSpan.Zero)) None)
        |> Option.get

    test <@ record.Key.Value = (testActionKey key "impact").Value @>
    test <@ record.Key.Value <> (testActionKey key "full").Value @>
    test <@ record.Counts = None @>

[<Fact>]
let ``a timed-out run is a fail`` () =
    let key = keyOf "c1"

    let record =
        recordFor (Ok key) (Ok key) (run (TestsTimedOut("", TimeSpan.FromSeconds 1.0, false, TimeSpan.Zero)) None)
        |> Option.get

    test <@ record.Outcome = OutcomeStore.Outcome.Fail @>

[<Fact>]
let ``a run whose inputs moved under it files nothing (641, 699)`` () =
    test <@ recordFor (Ok(keyOf "c1")) (Ok(keyOf "c2")) (run passedFull (Some(report 3 0))) = None @>

[<Fact>]
let ``a project with no key, or a run that verified nothing, files nothing`` () =
    let key = keyOf "c1"
    test <@ recordFor (Error "no model") (Ok key) (run passedFull None) = None @>
    test <@ outcomeRecordOf candidate None (Some(Ok key)) (run passedFull None) producer = None @>
    test <@ recordFor (Ok key) (Ok key) (run (TestsNoMatch("", TimeSpan.Zero)) None) = None @>
    test <@ recordFor (Ok key) (Ok key) (run (TestsDeferred "later") None) = None @>
    test <@ recordFor (Ok key) (Ok key) (run (TestsErrored "boom") None) = None @>

[<Fact>]
let ``the action key moves exactly when K_P or the scope moves`` () =
    test <@ (testActionKey (keyOf "c1") "full").Value = (testActionKey (keyOf "c1") "full").Value @>

    test
        <@
            (testActionKey (keyOf "c1") "full").Value
            <> (testActionKey (keyOf "c2") "full").Value
        @>
    // K_P's labels travel into the action key, so a miss can name the K_P input that moved.
    test
        <@
            ActionKey.moved (testActionKey (keyOf "c1") "full").Inputs (testActionKey (keyOf "c2") "full").Inputs = [ "kp"
                                                                                                                      "kp:closure" ]
        @>
