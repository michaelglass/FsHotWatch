/// AUTOMATION-1472: the shadow's TREE-keyed test records across two publications of one
/// tree. Check N's full-suite run fails a test (today red); check N+1 on the same tree runs
/// it again and passes it (today green, as the outstanding-failure ledger clears it). The
/// fail N filed at the tree's `full` key must not keep N+1's lookup red, and nothing else
/// may clear it. The cross-check sibling of AUTOMATION-1444 (`ShadowKpSupersessionTests`).
/// Every time is a value the test chose.
module FsHotWatch.Tests.ShadowCrossCheckSupersessionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 3, 4, 0, 0, TimeSpan.Zero)

let private projects = [ "A.Tests"; "B.Tests" ]

let private named run project (failed: string list) (passed: string list) : Suite =
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

/// Check N: a full-suite run r1 over T1, graded; A.x failed in it.
let private checkN (aFailed: string list) : Observation =
    { Command = Verdict.Check
      Tree = "sha256:t1"
      Producer = "fshw 1"
      Worktree = "/w"
      Today = Class.Red
      TodayOutcome = "red"
      ExitCode = 1
      Plugins = [ "build", Some OutcomeStore.Outcome.Pass ]
      DiagnosticsFailed = false
      GradedRunId = Some "r1"
      Scope = GradedScope.FullSuite
      Suites =
        [ named "r1" "A.Tests" aFailed [ "A.y"; "A.z" ]
          named "r1" "B.Tests" [] [ "B.x" ] ]
      RunOrder = [ "r1" ]
      RecordedAt = t0
      OutcomeKeys = Map.empty }

/// Check N+1 on the same tree: impact run r2, graded, with `suites`.
let private checkN1 (suites: Suite list) : Observation =
    { checkN [] with
        Today = Class.Green
        TodayOutcome = "green"
        ExitCode = 0
        GradedRunId = Some "r2"
        Scope = GradedScope.ImpactFiltered
        Suites = suites
        RunOrder = [ "r2" ]
        RecordedAt = t0.AddMinutes 5.0 }

let private withStore (body: string -> OutcomeStore.Store -> unit) =
    withTempDir "cross-check" (fun repo ->
        body
            repo
            (OutcomeStore.Store(
                { Root = storeRoot repo
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )))

[<Fact>]
let ``a full fail of check N whose named failure check N+1 passed again on the same tree is green under check`` () =
    withStore (fun repo store ->
        observeWith repo store None projects (checkN [ "A.x" ]) |> ignore

        test
            <@
                observeWith repo store None projects (checkN1 [ named "r2" "A.Tests" [] [ "A.x" ] ]) = LookupVerdict.Answer.Green
            @>)

// ---------------------------------------------------------------------------
// Never looser: each of these must stay red.
// ---------------------------------------------------------------------------

let private afterCheckN repo store (aFailed: string list) (n1: Observation) =
    observeWith repo store None projects (checkN aFailed) |> ignore
    observeWith repo store None projects n1

[<Fact>]
let ``a failure check N+1 failed again stays red`` () =
    withStore (fun repo store ->
        let n1 =
            { checkN1 [ named "r2" "A.Tests" [ "A.x" ] [] ] with
                Today = Class.Red
                TodayOutcome = "red"
                ExitCode = 1 }

        test <@ afterCheckN repo store [ "A.x" ] n1 = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``a passing check N+1 that did not run the failed test leaves it red`` () =
    withStore (fun repo store ->
        test
            <@
                afterCheckN repo store [ "A.x" ] (checkN1 [ named "r2" "A.Tests" [] [ "A.y" ] ]) = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``a check N+1 that passed only some of the failures leaves it red`` () =
    withStore (fun repo store ->
        test
            <@
                afterCheckN repo store [ "A.x"; "A.w" ] (checkN1 [ named "r2" "A.Tests" [] [ "A.x" ] ]) = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``a failure check N's report did not name is never cleared`` () =
    withStore (fun repo store ->
        let n =
            { checkN [ "A.x" ] with
                Suites =
                    [ { named "r1" "A.Tests" [ "A.x" ] [ "A.y" ] with
                          FailedTests = None }
                      named "r1" "B.Tests" [] [ "B.x" ] ] }

        observeWith repo store None projects n |> ignore

        test
            <@
                observeWith repo store None projects (checkN1 [ named "r2" "A.Tests" [] [ "A.x" ] ]) = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``a pass published at the same instant as the fail never clears it`` () =
    withStore (fun repo store ->
        let n1 =
            { checkN1 [ named "r2" "A.Tests" [] [ "A.x" ] ] with
                RecordedAt = t0 }

        test <@ afterCheckN repo store [ "A.x" ] n1 = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``a newer full fail on the same tree is red again, though check N's was cleared`` () =
    withStore (fun repo store ->
        afterCheckN repo store [ "A.x" ] (checkN1 [ named "r2" "A.Tests" [] [ "A.x" ] ])
        |> ignore

        let n2 =
            { checkN [ "A.x" ] with
                GradedRunId = Some "r3"
                Suites = [ named "r3" "A.Tests" [ "A.x" ] [ "A.y" ]; named "r3" "B.Tests" [] [ "B.x" ] ]
                RunOrder = [ "r3" ]
                RecordedAt = t0.AddMinutes 9.0 }

        test <@ observeWith repo store None projects n2 = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``confirm on the tree never reads check N's cleared fail as a pass`` () =
    withStore (fun repo store ->
        afterCheckN repo store [ "A.x" ] (checkN1 [ named "r2" "A.Tests" [] [ "A.x" ] ])
        |> ignore

        let read =
            { checkN1 [] with
                Command = Verdict.Confirm
                Today = Class.Missing
                TodayOutcome = "incomplete"
                ExitCode = 3
                Suites = []
                GradedRunId = None
                Scope = GradedScope.Unscoped
                RecordedAt = t0.AddMinutes 6.0 }

        test <@ observeReadWith repo store None projects read = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``check N+1's passed names are read from its report when it failed nothing itself`` () =
    // ofVerdict reads names only for a project that failed in that verdict, so a passing
    // project's suite arrives with none; its report says what it passed.
    withStore (fun repo store ->
        let report = Path.Combine(repo, "A.Tests.ctrf.json")

        File.WriteAllText(report, """{ "results": { "tests": [ { "name": "A.x", "status": "passed" } ] } }""")

        let reported =
            { named "r2" "A.Tests" [] [] with
                Report = Some report }

        test <@ afterCheckN repo store [ "A.x" ] (checkN1 [ reported ]) = LookupVerdict.Answer.Green @>)

[<Fact>]
let ``check N+1 with no report to read clears nothing`` () =
    withStore (fun repo store ->
        let unread =
            { named "r2" "A.Tests" [] [] with
                Report = Some(Path.Combine(repo, "absent.ctrf.json")) }

        test <@ afterCheckN repo store [ "A.x" ] (checkN1 [ unread ]) = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``a suite names its failures only when it counted nothing else, and never when it timed out`` () =
    let counts total passed failed skipped : OutcomeStore.Counts =
        { Total = total
          Passed = passed
          Failed = failed
          Skipped = skipped }

    test <@ namedFailures false (counts 3 2 1 0) (Some [ "A.x" ]) = Some [ "A.x" ] @>
    // A timeout is about the project, whatever its report names.
    test <@ namedFailures true (counts 3 2 1 0) (Some [ "A.x" ]) = None @>
    // An `other` row (an errored test) is a failure nobody named.
    test <@ namedFailures false (counts 4 2 1 0) (Some [ "A.x" ]) = None @>
    // More failures counted than named, or a report that names none.
    test <@ namedFailures false (counts 3 1 2 0) (Some [ "A.x" ]) = None @>
    test <@ namedFailures false (counts 3 2 1 0) None = None @>
