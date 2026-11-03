/// AUTOMATION-1444: what a full fail's clearance is made of, and what a check reads from it.
/// The end-to-end shape (runner filing, then the shadow lookup) is in
/// `ShadowKpSupersessionTests`. Every time is a value the test chose.
module FsHotWatch.Tests.TestFailureClearanceTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero)

let private full = ActionKey.create "test" [ "kp", "A"; "run-scope", "full" ]

let private record (outcome: OutcomeStore.Outcome) (run: string option) (minutes: float) : OutcomeStore.Record =
    { Key = full
      Subject = "A.Tests"
      Outcome = outcome
      Counts = None
      DurationMs = None
      Producer =
        { Binary = "fshw 1"
          Worktree = "/w"
          RunId = run }
      RecordedAt = t0.AddMinutes minutes }

let private fail = record OutcomeStore.Outcome.Fail (Some "r1") 5.0
let private pass = record OutcomeStore.Outcome.Pass (Some "r2") 7.5
let private named = Some(TestFailureClearance.outputsOf [ "A.x" ])

[<Fact>]
let ``a fail's failed tests read back from its outputs, and nothing else names any`` () =
    test
        <@
            TestFailureClearance.failedTestsOf (Some(TestFailureClearance.outputsOf [ "A.x"; "A.y" ])) = Some
                [ "A.x"; "A.y" ]
        @>

    test <@ TestFailureClearance.failedTestsOf None = None @>
    test <@ TestFailureClearance.failedTestsOf (Some "not json") = None @>
    test <@ TestFailureClearance.failedTestsOf (Some """{"failedTests":[]}""") = None @>
    test <@ TestFailureClearance.failedTestsOf (Some """{"failedTests":["A.x",1]}""") = None @>
    test <@ TestFailureClearance.failedTestsOf (Some """{"failedTests":"A.x"}""") = None @>
    test <@ TestFailureClearance.failedTestsOf (Some """{"other":1}""") = None @>

[<Fact>]
let ``a passing later run that passed every named failure clears that fail, keyed on its run`` () =
    let c =
        TestFailureClearance.clearanceOf full.Value (Some(fail, named)) (set [ "A.x"; "A.y" ]) pass
        |> Option.get

    test <@ c.Key.Value = (TestFailureClearance.keyOf full.Value "r1").Value @>
    test <@ c.Key.Kind = TestFailureClearance.Kind @>
    test <@ c.Outcome = OutcomeStore.Outcome.Pass @>
    test <@ c.Producer.RunId = Some "r2" @>
    test <@ c.RecordedAt = pass.RecordedAt @>

    test
        <@
            (TestFailureClearance.keyOf full.Value "r1").Value
            <> (TestFailureClearance.keyOf full.Value "r3").Value
        @>

[<Fact>]
let ``nothing else earns a clearance`` () =
    let clear newest passed p =
        TestFailureClearance.clearanceOf full.Value newest passed p

    let both = set [ "A.x" ]
    // No record, or the newest is a pass: there is nothing to clear.
    test <@ clear None both pass = None @>
    test <@ clear (Some(pass, None)) both pass = None @>
    // The later run failed, or did not pass the named failure.
    test <@ clear (Some(fail, named)) both (record OutcomeStore.Outcome.Fail (Some "r2") 7.5) = None @>
    test <@ clear (Some(fail, named)) (set [ "A.y" ]) pass = None @>
    // The fail names nothing, or names no run.
    test <@ clear (Some(fail, None)) both pass = None @>
    test <@ clear (Some(record OutcomeStore.Outcome.Fail None 5.0, named)) both pass = None @>
    // The same run, or a pass filed before the fail or at the same instant (a tie goes to fail).
    test <@ clear (Some(fail, named)) both (record OutcomeStore.Outcome.Pass (Some "r1") 7.5) = None @>
    test <@ clear (Some(fail, named)) both (record OutcomeStore.Outcome.Pass (Some "r2") 4.0) = None @>
    test <@ clear (Some(fail, named)) both (record OutcomeStore.Outcome.Pass (Some "r2") 5.0) = None @>

let private withStore (body: OutcomeStore.Store -> unit) =
    withTempDir "clearance" (fun dir ->
        body (
            OutcomeStore.Store(
                { Root = Path.Combine(dir, "outcomes")
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )
        ))

[<Fact>]
let ``a check reads a cleared fail as a pass, and every other answer as the store gives it`` () =
    withStore (fun store ->
        let read () =
            TestFailureClearance.lookupForCheck store full.Value

        test <@ read () = None @>
        store.RecordWithOutputs(fail, named) |> ignore
        test <@ read () = Some OutcomeStore.Outcome.Fail @>

        let c =
            TestFailureClearance.clearanceOf full.Value (store.LookupWithOutputs full) (set [ "A.x" ]) pass
            |> Option.get

        store.Record c |> ignore
        test <@ read () = Some OutcomeStore.Outcome.Pass @>
        // The full key's own newest record is still the fail: only a check reads through it.
        test <@ (store.Lookup full).Value.Outcome = OutcomeStore.Outcome.Fail @>

        store.Record(record OutcomeStore.Outcome.Pass (Some "r4") 12.0) |> ignore
        test <@ read () = Some OutcomeStore.Outcome.Pass @>)

[<Fact>]
let ``a fail with no run, or a clearance not after the fail, is read as the fail`` () =
    withStore (fun store ->
        store.Record(record OutcomeStore.Outcome.Fail None 5.0) |> ignore
        test <@ TestFailureClearance.lookupForCheck store full.Value = Some OutcomeStore.Outcome.Fail @>)
    // A clearance older than the fail, or of the same instant, is not after it; one that is not a pass clears nothing.
    for minutes in [ 1.0; 5.0 ] do
        withStore (fun store ->
            store.RecordWithOutputs(fail, named) |> ignore

            store.Record
                { pass with
                    Key = TestFailureClearance.keyOf full.Value "r1"
                    RecordedAt = t0.AddMinutes minutes }
            |> ignore

            test <@ TestFailureClearance.lookupForCheck store full.Value = Some OutcomeStore.Outcome.Fail @>)

    withStore (fun store ->
        store.RecordWithOutputs(fail, named) |> ignore

        store.Record
            { pass with
                Key = TestFailureClearance.keyOf full.Value "r1"
                Outcome = OutcomeStore.Outcome.Fail }
        |> ignore

        test <@ TestFailureClearance.lookupForCheck store full.Value = Some OutcomeStore.Outcome.Fail @>)
