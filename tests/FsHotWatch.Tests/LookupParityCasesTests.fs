/// The tickets folded into AUTOMATION-1362 (ADR-059 §13), each pinned as a case the
/// ActionKey and the lookup verdict must get right. Today's machinery got each of them
/// wrong at least once; the lookup's answer is a function of recorded outcomes under
/// content keys, so each case is a statement about keys and records, never about a daemon.
module FsHotWatch.Tests.LookupParityCasesTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli.ShadowVerdict
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)

let private withStore (body: string -> OutcomeStore.Store -> unit) =
    withTempDir "parity" (fun repo ->
        body
            repo
            (OutcomeStore.Store(
                { Root = storeRoot repo
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )))

let private pass (store: OutcomeStore.Store) (key: ActionKey.ActionKey) =
    store.Record
        { Key = key
          Subject = key.Kind
          Outcome = OutcomeStore.Outcome.Pass
          Counts = None
          DurationMs = None
          Producer =
            { Binary = "fshw 1"
              Worktree = "/w"
              RunId = Some "r1" }
          RecordedAt = t0 }
    |> ignore

let private lookupIn (store: OutcomeStore.Store) =
    fun (k: string) -> store.Lookup k |> Option.map _.Outcome

/// The key of a test project's run over the files it reads: here, one fixture.
let private testOver (fixtureDigest: string) =
    ActionKey.create "test" [ "project", "A.Tests"; "reads:coverage-counts.json", fixtureDigest ]

[<Fact>]
let ``641: a pass earned before a file the tests read changed does not answer for the new bytes`` () =
    withStore (fun _ store ->
        pass store (testOver "sha256:746-floors")

        let required =
            [ LookupVerdict.Requirement.single "test:A.Tests" (testOver "sha256:751-floors") ]

        test <@ LookupVerdict.decide required (lookupIn store) = LookupVerdict.Answer.Missing [ "test:A.Tests" ] @>)

[<Fact>]
let ``698: a file outside the tree hash that a plugin reads still keys that plugin's claim`` () =
    withStore (fun _ store ->
        // The changelog gate reads CHANGELOG.md, which the verdict tree does not hash. Its
        // claim is keyed on what IT reads, so an edit there leaves no green standing.
        let gate (changelog: string) =
            ActionKey.create "plugin" [ "plugin", "changelog-gate"; "reads:CHANGELOG.md", changelog ]

        pass store (gate "sha256:well-formed")

        let required =
            [ LookupVerdict.Requirement.single "plugin:changelog-gate" (gate "sha256:malformed") ]

        test
            <@
                LookupVerdict.decide required (lookupIn store) = LookupVerdict.Answer.Missing
                    [ "plugin:changelog-gate" ]
            @>)

[<Fact>]
let ``699: a verdict whose own suites report a failure is never looked up green`` () =
    withStore (fun repo store ->
        let contradictory: Observation =
            { Command = "check"
              Tree = "sha256:t1"
              Producer = "fshw 1"
              Worktree = repo
              Today = Class.Green
              TodayOutcome = "green"
              ExitCode = 0
              Concluded = true
              Plugins = []
              DiagnosticsFailed = false
              GradedRunId = Some "r1"
              FullSuite = true
              ImpactFiltered = false
              Suites =
                [ { RunId = Some "r1"
                    Project = "Database.Tests"
                    Counts =
                      { Total = 1806
                        Passed = 1805
                        Failed = 1
                        Skipped = 0 }
                    Passed = false } ]
              RecordedAt = t0
              OutcomeKeys = Map.empty }

        test
            <@
                observeWith repo store None [ "Database.Tests" ] contradictory = LookupVerdict.Answer.Red
                    [ "test:Database.Tests" ]
            @>)

[<Fact>]
let ``1027: an analyzer pass that did not cover every file of the model is not green`` () =
    withStore (fun _ store ->
        let analyze (file: string) =
            ActionKey.create "analyze" [ "file", file; "content", "sha256:" + file ]

        pass store (analyze "src/A.fs")
        // src/B.fs was neither examined nor replayed: no record.
        let required =
            [ "src/A.fs"; "src/B.fs" ]
            |> List.map (fun f -> LookupVerdict.Requirement.single ("analyze:" + f) (analyze f))

        test <@ LookupVerdict.decide required (lookupIn store) = LookupVerdict.Answer.Missing [ "analyze:src/B.fs" ] @>)

[<Fact>]
let ``1242: a configured test project the model never discovered is still required`` () =
    withStore (fun repo store ->
        // FsHotWatch.Bench.Tests lives under bench/, outside the discovery roots. The
        // requirements come from the configuration, so a run that never launched it is
        // missing it rather than silently green without it.
        let o: Observation =
            { Command = "confirm"
              Tree = "sha256:t1"
              Producer = "fshw 1"
              Worktree = repo
              Today = Class.Green
              TodayOutcome = "green"
              ExitCode = 0
              Concluded = true
              Plugins = []
              DiagnosticsFailed = false
              GradedRunId = Some "r1"
              FullSuite = true
              ImpactFiltered = false
              Suites =
                [ { RunId = Some "r1"
                    Project = "FsHotWatch.Tests"
                    Counts =
                      { Total = 3
                        Passed = 3
                        Failed = 0
                        Skipped = 0 }
                    Passed = true } ]
              RecordedAt = t0
              OutcomeKeys = Map.empty }

        test
            <@
                observeWith repo store None [ "FsHotWatch.Tests"; "FsHotWatch.Bench.Tests" ] o = LookupVerdict.Answer.Missing
                    [ "test:FsHotWatch.Bench.Tests" ]
            @>)

[<Fact>]
let ``109: decide is a pure function of the recorded outcomes, whatever store instance answers`` () =
    withStore (fun repo store ->
        let k = ActionKey.create "plugin" [ "plugin", "lint"; "tree", "sha256:t" ]
        pass store k
        let required = [ LookupVerdict.Requirement.single "plugin:lint" k ]
        let first = LookupVerdict.decide required (lookupIn store)

        let afresh =
            OutcomeStore.Store(
                { Root = storeRoot repo
                  Ttl = TimeSpan.FromDays 14.0
                  MaxBytes = 100_000_000L
                  Log = ignore }
            )

        test <@ first = LookupVerdict.Answer.Green @>
        test <@ LookupVerdict.decide required (lookupIn afresh) = first @>
        test <@ LookupVerdict.decide required (lookupIn afresh) = first @>)
