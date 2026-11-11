/// AUTOMATION-1443: a `fshw verdict` read has no daemon, so it has no K_P keys, and a check that
/// only the test runner's K_P records made green read back as missing on the same tree and
/// binary. ADR-059 §19 predicted this; the AUTOMATION-1440 replay's step 0 showed it. Every
/// time is a value the test chose.
module FsHotWatch.Tests.ShadowReadKpTests

open System
open System.IO
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
    { RequiredPlugins = [ "build" ]
      TestProjects = projects }

let private kpA = ActionKey.create "test" [ "kp", "A"; "run-scope", "full" ]
let private kpB = ActionKey.create "test" [ "kp", "B"; "run-scope", "full" ]

let private kpRecord (key: ActionKey.ActionKey) (outcome: OutcomeStore.Outcome) (run: string) : OutcomeStore.Record =
    { Key = key
      Subject = "kp"
      Outcome = outcome
      Counts = None
      DurationMs = None
      Producer =
        { Binary = "fshw 1"
          Worktree = "/w"
          RunId = Some run }
      RecordedAt = t0 }

/// An impact check over a cold checkout: nothing the shadow files under the tree answers for
/// either project (no baseline is on file), and the daemon names each project's K_P key.
let private coldCheck: Observation =
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
      Suites = []
      RunOrder = [ "r2" ]
      RecordedAt = t0.AddMinutes 15.0
      OutcomeKeys = Map.ofList [ "A.Tests", kpA.Value; "B.Tests", kpB.Value ] }

/// The read `./build.fsx check` makes right after: the same tree and binary, no keys.
let private readOf (o: Observation) =
    { o with
        TodayOutcome = "applies:green"
        Suites = []
        RecordedAt = o.RecordedAt.AddSeconds 3.0
        OutcomeKeys = Map.empty }

let private withStores (body: string -> OutcomeStore.Store -> OutcomeStore.Store -> unit) =
    withTempDir "read-kp" (fun repo ->
        let open' (root: string) =
            OutcomeStore.Store(
                { Root = root
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        body repo (open' (storeRoot repo)) (open' (Path.Combine(repo, "kp-store"))))

[<Fact>]
let ``a read right after a check that K_P records alone made green agrees green`` () =
    withStores (fun repo store kp ->
        kp.Record(kpRecord kpA OutcomeStore.Outcome.Pass "r1") |> ignore
        kp.Record(kpRecord kpB OutcomeStore.Outcome.Pass "r1") |> ignore

        test <@ observeWith repo store (Some kp) configured coldCheck = LookupVerdict.Answer.Green @>
        test <@ observeReadWith repo store (Some kp) configured (readOf coldCheck) = LookupVerdict.Answer.Green @>)

// ---------------------------------------------------------------------------
// Never looser: the read finds only the keys a publication named for ITS tree and binary,
// and judges what it finds by the same rules as the check.
// ---------------------------------------------------------------------------

let private published repo store (kp: OutcomeStore.Store) (outcome: OutcomeStore.Outcome) =
    kp.Record(kpRecord kpA outcome "r1") |> ignore
    kp.Record(kpRecord kpB OutcomeStore.Outcome.Pass "r1") |> ignore
    observeWith repo store (Some kp) configured coldCheck |> ignore

[<Fact>]
let ``a read of another tree finds no key the check named`` () =
    withStores (fun repo store kp ->
        published repo store kp OutcomeStore.Outcome.Pass

        let elsewhere =
            { readOf coldCheck with
                Tree = "sha256:t2" }

        test
            <@
                observeReadWith repo store (Some kp) configured elsewhere = LookupVerdict.Answer.Missing
                    [ "plugin:build"; "diagnostics"; "test:A.Tests"; "test:B.Tests" ]
            @>)

[<Fact>]
let ``a read under another fshw binary finds no key the check named`` () =
    withStores (fun repo store kp ->
        published repo store kp OutcomeStore.Outcome.Pass

        let upgraded =
            { readOf coldCheck with
                Producer = "fshw 2" }

        test
            <@
                observeReadWith repo store (Some kp) configured upgraded = LookupVerdict.Answer.Missing
                    [ "plugin:build"; "diagnostics"; "test:A.Tests"; "test:B.Tests" ]
            @>)

[<Fact>]
let ``a read finds a K_P fail as red`` () =
    withStores (fun repo store kp ->
        published repo store kp OutcomeStore.Outcome.Fail

        let read =
            { readOf coldCheck with
                Today = Class.Red
                TodayOutcome = "applies:red" }

        test <@ observeReadWith repo store (Some kp) configured read = LookupVerdict.Answer.Red [ "test:A.Tests" ] @>)

[<Fact>]
let ``a confirm read never reads a cleared K_P fail as a pass, and a check read does`` () =
    withStores (fun repo store kp ->
        kp.RecordWithOutputs(
            kpRecord kpA OutcomeStore.Outcome.Fail "r1",
            Some(TestFailureClearance.outputsOf [ "A.x" ])
        )
        |> ignore

        let pass =
            { kpRecord kpA OutcomeStore.Outcome.Pass "r2" with
                RecordedAt = t0.AddMinutes 5.0 }

        TestFailureClearance.clearanceOf kpA.Value (kp.LookupWithOutputs kpA) (set [ "A.x" ]) pass
        |> Option.iter (kp.Record >> ignore)

        kp.Record(kpRecord kpB OutcomeStore.Outcome.Pass "r1") |> ignore
        test <@ observeWith repo store (Some kp) configured coldCheck = LookupVerdict.Answer.Green @>
        test <@ observeReadWith repo store (Some kp) configured (readOf coldCheck) = LookupVerdict.Answer.Green @>

        let confirmRead =
            { readOf coldCheck with
                Command = Verdict.Confirm }

        test
            <@
                observeReadWith repo store (Some kp) configured confirmRead = LookupVerdict.Answer.Red
                    [ "test:A.Tests" ]
            @>)

[<Fact>]
let ``a read with no K_P store, or a named key it cannot read back, decides nothing from K_P`` () =
    withStores (fun repo store kp ->
        published repo store kp OutcomeStore.Outcome.Pass

        test
            <@
                observeReadWith repo store None configured (readOf coldCheck) = LookupVerdict.Answer.Missing
                    [ "test:A.Tests"; "test:B.Tests" ]
            @>

        // A record under A's name that does not hold a key reads as no key.
        store.RecordWithOutputs(
            { OutcomeStore.Record.ofAction
                  (outcomeKeyKey coldCheck "A.Tests")
                  "A.Tests"
                  OutcomeStore.Outcome.Pass
                  "fshw 1"
                  "/w"
                  (t0.AddMinutes 20.0) with
                Subject = "A.Tests" },
            Some """{"outcomeKey":"not a key"}"""
        )
        |> ignore

        test <@ recordedOutcomeKeys store projects (readOf coldCheck) |> Map.keys |> List.ofSeq = [ "B.Tests" ] @>)

[<Fact>]
let ``naming the keys is not counted as recording an outcome, and a read files nothing`` () =
    withStores (fun repo store kp ->
        published repo store kp OutcomeStore.Outcome.Pass

        let line =
            File.ReadAllLines(logPath repo)
            |> Array.last
            |> fun l -> Text.Json.Nodes.JsonNode.Parse(l)

        // One plugin, diagnostics, and an impact pass of zero tests for each unselected project.
        test <@ line["recorded"].GetValue<int>() = 4 @>

        let before = allFilesUnder "*.json" (storeRoot repo) |> List.length
        observeReadWith repo store (Some kp) configured (readOf coldCheck) |> ignore
        test <@ allFilesUnder "*.json" (storeRoot repo) |> List.length = before @>)
