module FsHotWatch.Tests.FlakinessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune.Flakiness
open FsHotWatch.Tests.TestHelpers

// --- TestOutcome / parseCtrfTests ---

[<Fact(Timeout = 5000)>]
let ``parseCtrfTests extracts name, status, and duration from CTRF JSON`` () =
    let json =
        """{"results":{"tool":{"name":"xUnit.net v3"}},"tests":[
            {"name":"Mod.Type.Method","status":"passed","duration":42,"extra":{}},
            {"name":"Mod.Type.OtherMethod","status":"failed","duration":17,"extra":{}}]}"""

    let records = parseCtrfTests json

    test <@ records.Length = 2 @>
    test <@ records.[0].Name = "Mod.Type.Method" @>
    test <@ records.[0].Outcome = TestOutcome.Passed @>
    test <@ records.[0].DurationMs = 42 @>
    test <@ records.[1].Name = "Mod.Type.OtherMethod" @>
    test <@ records.[1].Outcome = TestOutcome.Failed @>

[<Fact(Timeout = 5000)>]
let ``parseCtrfTests treats unknown statuses as Other`` () =
    let json = """{"tests":[{"name":"X","status":"weird","duration":0,"extra":{}}]}"""

    let records = parseCtrfTests json
    test <@ records.[0].Outcome = TestOutcome.Other @>

[<Fact(Timeout = 5000)>]
let ``parseCtrfTests returns empty when no tests array present`` () =
    test <@ List.isEmpty (parseCtrfTests "{}") @>

[<Fact(Timeout = 5000)>]
let ``parseCtrfTests returns empty on unparseable JSON`` () =
    test <@ List.isEmpty (parseCtrfTests "not json") @>

[<Fact(Timeout = 5000)>]
let ``parseCtrfTests recognises skipped status`` () =
    let json = """{"tests":[{"name":"X","status":"skipped","duration":0,"extra":{}}]}"""

    let records = parseCtrfTests json
    test <@ records.[0].Outcome = TestOutcome.Skipped @>

// CAPTURED (not hand-authored) Microsoft.Testing.Platform / xUnit.v3 output: tests
// and summary nest under `results`, and a test that threw a RAW (non-assertion)
// exception is OMITTED from `results.tests` while still counted in the summary —
// `tests:3` with only 2 entries, the throw showing as `failed:2` + `other:1`.
let private realCtrf =
    """{"reportFormat":"CTRF","specVersion":"0.0.0",
        "results":{
          "tool":{"name":"xUnit.net v3"},
          "summary":{"tests":3,"passed":1,"failed":2,"pending":0,"skipped":0,"other":1,"suites":1},
          "tests":[
            {"name":"Mod.passes","status":"passed","duration":5},
            {"name":"Mod.assertFails","status":"failed","duration":3}]}}"""

[<Fact(Timeout = 5000)>]
let ``parseCtrfTests reads the real nested results.tests shape`` () =
    // Regression: the parser previously read top-level `tests` (absent in real
    // output) and silently returned [] on every real run — flakiness was dead.
    let records = parseCtrfTests realCtrf
    test <@ records.Length = 2 @>

    test
        <@
            records
            |> List.exists (fun r -> r.Name = "Mod.passes" && r.Outcome = TestOutcome.Passed)
        @>

    test
        <@
            records
            |> List.exists (fun r -> r.Name = "Mod.assertFails" && r.Outcome = TestOutcome.Failed)
        @>

// --- tryParseReport (summary-authoritative verdict source) ---

[<Fact(Timeout = 5000)>]
let ``tryParseReport reads summary counts, not the per-test array length`` () =
    // The summary says 3 tests, the array holds 2 (the raw-throw is omitted): the
    // totals must come from the summary, never the array.
    match tryParseReport realCtrf with
    | None -> failwith "expected Some report"
    | Some r ->
        test <@ r.Total = 3 @>
        test <@ r.Passed = 1 @>
        test <@ r.Failed = 2 @>
        test <@ r.Other = 1 @>
        test <@ not (TestReport.allClear r) @>

[<Fact(Timeout = 5000)>]
let ``tryParseReport reports allClear for a clean run`` () =
    let json =
        """{"results":{"summary":{"tests":15,"passed":15,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1}}}"""

    match tryParseReport json with
    | None -> failwith "expected Some report"
    | Some r ->
        test <@ r.Total = 15 @>
        test <@ TestReport.allClear r @>

[<Fact(Timeout = 5000)>]
let ``tryParseReport treats an other-only run as not allClear`` () =
    let json =
        """{"results":{"summary":{"tests":1,"passed":0,"failed":0,"pending":0,"skipped":0,"other":1,"suites":1}}}"""

    match tryParseReport json with
    | Some r -> test <@ not (TestReport.allClear r) @>
    | None -> failwith "expected Some report"

[<Fact(Timeout = 5000)>]
let ``tryParseReport returns None when no summary present`` () =
    // A truncated / never-flushed report (host aborted) has no summary — the
    // signal the verdict reads as "no usable report" → errored, not green.
    test <@ (tryParseReport """{"results":{"tool":{"name":"x"}}}""").IsNone @>
    test <@ (tryParseReport "{}").IsNone @>

[<Fact(Timeout = 5000)>]
let ``tryParseReport returns None on unparseable JSON`` () =
    test <@ (tryParseReport "not json at all").IsNone @>

// --- computeFlakiness ---

[<Fact(Timeout = 5000)>]
let ``computeFlakiness is 0 for all-passing history`` () =
    let outcomes = [ Passed; Passed; Passed; Passed ]
    test <@ computeFlakiness outcomes = 0.0 @>

[<Fact(Timeout = 5000)>]
let ``computeFlakiness is 0 for all-failing history`` () =
    let outcomes = [ Failed; Failed; Failed ]
    test <@ computeFlakiness outcomes = 0.0 @>

[<Fact(Timeout = 5000)>]
let ``computeFlakiness is 1 for alternating pass-fail`` () =
    let outcomes = [ Passed; Failed; Passed; Failed; Passed ]
    test <@ computeFlakiness outcomes = 1.0 @>

[<Fact(Timeout = 5000)>]
let ``computeFlakiness counts only outcome transitions`` () =
    // P P F P → 2 transitions over 3 gaps → 2/3
    let outcomes = [ Passed; Passed; Failed; Passed ]
    let score = computeFlakiness outcomes
    test <@ abs (score - (2.0 / 3.0)) < 0.0001 @>

[<Fact(Timeout = 5000)>]
let ``computeFlakiness ignores Skipped outcomes`` () =
    // Skipped runs aren't an outcome flip — collapse to neighboring outcome.
    let outcomes = [ Passed; Skipped; Failed ]
    // After dropping Skipped: P F → 1 transition / 1 gap = 1.0
    test <@ computeFlakiness outcomes = 1.0 @>

[<Fact(Timeout = 5000)>]
let ``computeFlakiness is 0 when history has fewer than 2 effective runs`` () =
    test <@ computeFlakiness [] = 0.0 @>
    test <@ computeFlakiness [ Passed ] = 0.0 @>
    test <@ computeFlakiness [ Skipped; Skipped ] = 0.0 @>

// --- persistence: appendRecords / loadHistory / trim ---

[<Fact(Timeout = 15000)>]
let ``appendRecords + loadHistory round-trips a record`` () =
    withTempDir "fshw-flake-rt" (fun tmp ->
        let path = Path.Combine(tmp, "test-history.json")

        // Anchored to "now": history older than `DefaultHistoryRetention` is
        // expired on write, so a persistence test must record a RECENT run.
        let r =
            { Name = "A.B.C"
              Outcome = Passed
              DurationMs = 100
              RunStartedAt = DateTime.UtcNow }

        appendRecords path 20 [ r ]
        let history = loadHistory path
        test <@ Map.find "A.B.C" history |> List.length = 1 @>
        test <@ (Map.find "A.B.C" history).[0].DurationMs = 100 @>)

[<Fact(Timeout = 15000)>]
let ``appendRecords trims per-test history to the last N`` () =
    withTempDir "fshw-flake-trim" (fun tmp ->
        let path = Path.Combine(tmp, "test-history.json")

        let t0 = DateTime.UtcNow

        let mkRecord i =
            { Name = "A.B.C"
              Outcome = Passed
              DurationMs = i
              RunStartedAt = t0.AddSeconds(float i) }

        for i in 1..30 do
            appendRecords path 5 [ mkRecord i ]

        let history = loadHistory path
        let records = Map.find "A.B.C" history
        test <@ records.Length = 5 @>
        // Most recent first (DurationMs 30, 29, 28, 27, 26)
        test <@ records.[0].DurationMs = 30 @>
        test <@ records.[4].DurationMs = 26 @>)

[<Fact(Timeout = 15000)>]
let ``loadHistory returns empty Map when file is missing`` () =
    withTempDir "fshw-flake-missing" (fun tmp ->
        let path = Path.Combine(tmp, "missing.json")
        test <@ loadHistory path = Map.empty @>)

[<Fact(Timeout = 15000)>]
let ``topFlaky returns tests sorted by flakiness descending`` () =
    withTempDir "fshw-flake-top" (fun tmp ->
        let path = Path.Combine(tmp, "test-history.json")

        let t0 = DateTime.UtcNow

        // FlakyA: alternates P/F (flakiness 1.0)
        // StableB: always P (flakiness 0.0)
        let alternating =
            [ for i in 1..6 ->
                  { Name = "Mod.FlakyA"
                    Outcome = (if i % 2 = 0 then Passed else Failed)
                    DurationMs = i
                    RunStartedAt = t0.AddSeconds(float i) } ]

        let stable =
            [ for i in 1..6 ->
                  { Name = "Mod.StableB"
                    Outcome = Passed
                    DurationMs = i
                    RunStartedAt = t0.AddSeconds(float i) } ]

        appendRecords path 20 alternating
        appendRecords path 20 stable

        let history = loadHistory path
        let top = topFlaky 10 history

        // StableB scores 0.0 and is excluded entirely, so only FlakyA is listed.
        test <@ top.Length = 1 @>
        test <@ (fst top.[0]) = "Mod.FlakyA" @>
        test <@ (snd top.[0]) = 1.0 @>)

// ---------------------------------------------------------------------------
// AUTOMATION-98 finding 7 — the history file only ever grew, and racing writes
// could lose records.
// ---------------------------------------------------------------------------
//
// `keepN` bounds each test's record LIST; nothing bounded the set of test NAMES, so
// a renamed or deleted test kept its entry for good and the live file reached
// 5.5 MB. `appendRecords` parses and rewrites ALL of it, and was called once per
// test CONFIG — under `Async.Parallel`, so the read-modify-write also raced itself
// and the second writer dropped the first's records. Collecting every project's
// records and writing ONCE per run fixes the cost and the race together.

let private t0 = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)

let private record name (at: DateTime) =
    { Name = name
      Outcome = Passed
      DurationMs = 1
      RunStartedAt = at }

[<Fact(Timeout = 5000)>]
let ``expireHistory drops tests whose NEWEST run predates the retention window`` () =
    let history =
        Map.ofList
            [ "Stale.Renamed", [ record "Stale.Renamed" (t0.AddDays -40.0) ]
              "Fresh.Test", [ record "Fresh.Test" (t0.AddDays -1.0) ]
              // Mostly-old, but ran recently — the NEWEST run is what counts, so it stays.
              "Old.ButStillRunning",
              [ record "Old.ButStillRunning" (t0.AddDays -2.0)
                record "Old.ButStillRunning" (t0.AddDays -300.0) ] ]

    let kept = expireHistory t0 (TimeSpan.FromDays 30.0) history

    test <@ kept |> Map.containsKey "Fresh.Test" @>
    test <@ kept |> Map.containsKey "Old.ButStillRunning" @>
    test <@ not (kept |> Map.containsKey "Stale.Renamed") @>

[<Fact(Timeout = 5000)>]
let ``expireHistory drops an entry with no records at all`` () =
    let history = Map.ofList [ "Empty.Test", [] ]
    test <@ expireHistory t0 (TimeSpan.FromDays 30.0) history = Map.empty @>

[<Fact(Timeout = 5000)>]
let ``mergeRecords keeps the N most-recent per test and expires the rest`` () =
    let existing =
        Map.ofList
            [ "Stale.Gone", [ record "Stale.Gone" (t0.AddDays -90.0) ]
              "Kept", [ record "Kept" (t0.AddDays -1.0) ] ]

    let merged =
        existing
        |> mergeRecords t0 (TimeSpan.FromDays 30.0) 2 [ record "Kept" t0; record "New" t0 ]

    // The stale test is collected, even though this run said nothing about it.
    test <@ not (merged |> Map.containsKey "Stale.Gone") @>
    // The touched test keeps at most N, most-recent-first.
    test <@ (Map.find "Kept" merged).Length = 2 @>
    test <@ (Map.find "Kept" merged).Head.RunStartedAt = t0 @>
    test <@ (Map.find "New" merged).Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``appendRecords records EVERY project's tests in one write (no lost update)`` () =
    // The shape of the race the per-config call created: several projects' records
    // arriving together. One call with all of them retains all of them, where N
    // load-and-rewrite calls could drop each other's.
    withTempDir "fshw-flake-multi" (fun tmp ->
        let path = Path.Combine(tmp, "test-history.json")
        let now = DateTime.UtcNow

        let allProjects =
            [ record "ProjA.Test1" now
              record "ProjB.Test1" now
              record "ProjC.Test1" now
              record "ProjD.Test1" now ]

        appendRecords path 20 allProjects

        let history = loadHistory path
        test <@ history.Count = 4 @>

        for r in allProjects do
            test <@ history |> Map.containsKey r.Name @>)

[<Fact(Timeout = 15000)>]
let ``appendRecords expires a test that has not run inside the retention window`` () =
    withTempDir "fshw-flake-expire" (fun tmp ->
        let path = Path.Combine(tmp, "test-history.json")
        let now = DateTime.UtcNow

        // A test that last ran 45 days ago — renamed, deleted, or parameterised away.
        appendRecords path 20 [ record "Ancient.Test" (now.AddDays -45.0) ]
        // ...is collected by the NEXT run, which touches a different test entirely.
        appendRecords path 20 [ record "Current.Test" now ]

        let history = loadHistory path
        test <@ history |> Map.containsKey "Current.Test" @>
        test <@ not (history |> Map.containsKey "Ancient.Test") @>)
