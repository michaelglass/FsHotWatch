/// AUTOMATION-1476: a check's own full-suite run earns today's watermark (the verdict's
/// baseline) without being the run the check graded, which is an impact run after it. The
/// shadow filed baselines only from a graded full-suite run, so it filed none, and the lookup
/// knew less than today: replay step 0 (intelligence 6552ffddc769, run 9089c471). Every time
/// is a value the test chose.
module FsHotWatch.Tests.ShadowBaselineRunTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 11, 1, 2, 40, 0, TimeSpan.Zero)

let private projects = [ "A.Tests"; "B.Tests" ]

/// What the configuration requires: the build the observation reports, and both projects.
let private configured: Configured =
    { RequiredPlugins = [ PluginInputs.RequiredPlugin.wholeTree "build" "build reads the whole tree" ]
      TestProjects = projects }

let private suite run project (failed: string list) (passed: string list) : Suite =
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

/// The first check of a cold checkout: full-suite run r1 earned the watermark, then impact run
/// r2, which the check graded, ran A again.
let private coldCheck (r1: Suite list) : Observation =
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
      BaselineRunId = Some "r1"
      Scope = GradedScope.ImpactFiltered
      Suites = suite "r2" "A.Tests" [] [ "A.x" ] :: r1
      RunOrder = [ "r1"; "r2" ]
      RecordedAt = t0
      OutcomeKeys = Map.empty }

let private bothPassed =
    [ suite "r1" "A.Tests" [] [ "A.x"; "A.y" ]; suite "r1" "B.Tests" [] [ "B.x" ] ]

let private withStore (body: string -> OutcomeStore.Store -> unit) =
    withTempDir "baseline-run" (fun repo ->
        body
            repo
            (OutcomeStore.Store(
                { Root = storeRoot repo
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )))

[<Fact>]
let ``the check's own full-suite run that earned the watermark files each passing project's baseline`` () =
    withStore (fun repo store ->
        test <@ observeWith repo store None configured (coldCheck bothPassed) = LookupVerdict.Answer.Green @>)

// ---------------------------------------------------------------------------
// Never looser.
// ---------------------------------------------------------------------------

let private baselinesIn (records: OutcomeStore.Record list) =
    records
    |> List.filter (fun r -> r.Key.Kind = "test-baseline")
    |> List.map _.Subject
    |> List.sort

[<Fact>]
let ``a project the watermark run failed earns no baseline, and its fail stays red`` () =
    withStore (fun repo store ->
        let r1 = [ suite "r1" "A.Tests" [] [ "A.x" ]; suite "r1" "B.Tests" [ "B.x" ] [] ]
        test <@ baselinesIn (recordsOf (coldCheck r1) projects) = [ "A.Tests" ] @>
        test <@ observeWith repo store None configured (coldCheck r1) = LookupVerdict.Answer.Red [ "test:B.Tests" ] @>)

[<Fact>]
let ``a watermark run the publication does not hold, or no watermark at all, files no baseline`` () =
    let elsewhere =
        { coldCheck bothPassed with
            BaselineRunId = Some "r0" }

    test <@ List.isEmpty (baselinesIn (recordsOf elsewhere projects)) @>

    let none =
        { coldCheck bothPassed with
            BaselineRunId = None }

    test <@ List.isEmpty (baselinesIn (recordsOf none projects)) @>

[<Fact>]
let ``the graded run is never filed as the watermark's full run as well`` () =
    // Its suites are impact records; only the watermark run earns baselines, never a `full`
    // record of the tree, which confirm would accept.
    let records = recordsOf (coldCheck bothPassed) projects

    test
        <@
            records
            |> List.filter (fun r -> r.Key.Kind = "test")
            |> List.forall (fun r -> r.Key.Value <> (testKey (coldCheck bothPassed) r.Subject FullScope).Value)
        @>

[<Fact>]
let ``confirm, another tree and another binary find nothing the watermark run filed for this tree`` () =
    withStore (fun repo store ->
        observeWith repo store None configured (coldCheck bothPassed) |> ignore

        let readOf (o: Observation) =
            { o with
                Today = Class.Missing
                TodayOutcome = "stale"
                Suites = []
                GradedRunId = None
                BaselineRunId = None
                Scope = GradedScope.Unscoped
                RecordedAt = t0.AddMinutes 1.0 }

        let confirmRead =
            { readOf (coldCheck bothPassed) with
                Command = Verdict.Confirm }

        test
            <@
                observeReadWith repo store None configured confirmRead = LookupVerdict.Answer.Missing
                    [ "test:A.Tests"; "test:B.Tests" ]
            @>

        let otherTree =
            { readOf (coldCheck bothPassed) with
                Tree = "sha256:t2" }

        test
            <@
                observeReadWith repo store None configured otherTree = LookupVerdict.Answer.Missing
                    [ "plugin:build"; "diagnostics"; "test:A.Tests"; "test:B.Tests" ]
            @>

        let otherBinary =
            { readOf (coldCheck bothPassed) with
                Producer = "fshw 2" }

        test
            <@
                observeReadWith repo store None configured otherBinary = LookupVerdict.Answer.Missing
                    [ "plugin:build"; "diagnostics"; "test:A.Tests"; "test:B.Tests" ]
            @>)
