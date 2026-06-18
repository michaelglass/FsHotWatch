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

// A captured real Microsoft.Testing.Platform / xUnit.v3 CTRF report: tests and
// summary are nested under `results`, and a test that threw a RAW (non-assertion)
// exception is OMITTED from `results.tests` while still counted in the summary
// (`tests:3` but only 2 entries; the throw shows as `failed:2` + `other:1`). This
// is captured output, not hand-authored — it is the shape the verdict logic must
// handle (see the comment on `parseCtrfTests`/`tryParseReport`).
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
    // The summary says 3 tests but the array holds only 2 (the raw-throw is
    // omitted). The report's totals must come from the summary, never the array.
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
    // A test that errored individually (CTRF `other`) is NOT a clean pass.
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

        let r =
            { Name = "A.B.C"
              Outcome = Passed
              DurationMs = 100
              RunStartedAt = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) }

        appendRecords path 20 [ r ]
        let history = loadHistory path
        test <@ Map.find "A.B.C" history |> List.length = 1 @>
        test <@ (Map.find "A.B.C" history).[0].DurationMs = 100 @>)

[<Fact(Timeout = 15000)>]
let ``appendRecords trims per-test history to the last N`` () =
    withTempDir "fshw-flake-trim" (fun tmp ->
        let path = Path.Combine(tmp, "test-history.json")

        let mkRecord i =
            { Name = "A.B.C"
              Outcome = Passed
              DurationMs = i
              RunStartedAt = DateTime(2026, 4, 1, 0, 0, i, DateTimeKind.Utc) }

        // Write 30 records one at a time, retain last 5
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

        // FlakyA: alternates P/F (flakiness 1.0)
        // StableB: always P (flakiness 0.0)
        let alternating =
            [ for i in 1..6 ->
                  { Name = "Mod.FlakyA"
                    Outcome = (if i % 2 = 0 then Passed else Failed)
                    DurationMs = i
                    RunStartedAt = DateTime(2026, 4, 1, 0, 0, i, DateTimeKind.Utc) } ]

        let stable =
            [ for i in 1..6 ->
                  { Name = "Mod.StableB"
                    Outcome = Passed
                    DurationMs = i
                    RunStartedAt = DateTime(2026, 4, 1, 0, 0, i, DateTimeKind.Utc) } ]

        appendRecords path 20 alternating
        appendRecords path 20 stable

        let history = loadHistory path
        let top = topFlaky 10 history

        // FlakyA should be first (score 1.0); StableB excluded (score 0.0).
        test <@ top.Length = 1 @>
        test <@ (fst top.[0]) = "Mod.FlakyA" @>
        test <@ (snd top.[0]) = 1.0 @>)
