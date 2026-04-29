/// Per-test flakiness tracking. Captures individual test pass/fail/duration
/// records from CTRF reports emitted by Microsoft Testing Platform runners
/// (xUnit v3, etc.), persists rolling history per test, and computes a
/// flakiness score over the recent N runs.
module FsHotWatch.TestPrune.Flakiness

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// Outcome of a single test invocation. `Other` covers CTRF statuses we don't
/// recognise (the spec is open-ended) so a runner update can't crash parsing.
type TestOutcome =
    | Passed
    | Failed
    | Skipped
    | Other

/// Single test execution captured from a CTRF report. `Name` is the fully-
/// qualified test identifier (e.g. `Mod.Type.Method`); we key history by it.
type TestRunRecord =
    { Name: string
      Outcome: TestOutcome
      DurationMs: int
      RunStartedAt: DateTime }

let private parseOutcome =
    function
    | "passed" -> Passed
    | "failed" -> Failed
    | "skipped"
    | "pending" -> Skipped
    | _ -> Other

let private outcomeToString =
    function
    | Passed -> "passed"
    | Failed -> "failed"
    | Skipped -> "skipped"
    | Other -> "other"

/// Parse a CTRF (Common Test Report Format) JSON document and extract per-
/// test records. Returns [] when the document has no `tests` array or is
/// unparseable — silent failure is the right behaviour for an opportunistic
/// post-test step that shouldn't crash the test runner if the report is
/// missing or malformed.
let internal parseCtrfTests (json: string) : TestRunRecord list =
    try
        let doc = JsonNode.Parse(json)

        match doc with
        | null -> []
        | root ->
            match root.["tests"] with
            | null -> []
            | tests ->
                match tests.AsArray() with
                | null -> []
                | arr ->
                    arr
                    |> Seq.choose (fun node ->
                        if isNull node then
                            None
                        else
                            try
                                let nameNode = node.["name"]
                                let statusNode = node.["status"]

                                if isNull nameNode || isNull statusNode then
                                    None
                                else
                                    let name = nameNode.GetValue<string>()
                                    let status = statusNode.GetValue<string>()

                                    let duration =
                                        match node.["duration"] with
                                        | null -> 0
                                        | n ->
                                            try
                                                n.GetValue<int>()
                                            with _ ->
                                                int (n.GetValue<float>())

                                    Some
                                        { Name = name
                                          Outcome = parseOutcome status
                                          DurationMs = duration
                                          RunStartedAt = DateTime.UtcNow }
                            with _ ->
                                None)
                    |> Seq.toList
    with _ ->
        []

/// Compute a flakiness score in [0.0, 1.0] over a sequence of outcomes ordered
/// most-recent-first. Skipped runs are filtered out before counting (a skip
/// isn't a real outcome flip — collapse to the surrounding outcomes). The
/// formula is `transitions / (n - 1)` over the remaining Pass/Fail/Other
/// outcomes — alternating P/F scores 1.0; all-pass (or all-fail) scores 0.0.
/// Returns 0.0 when fewer than 2 effective outcomes are available.
let internal computeFlakiness (history: TestOutcome list) : float =
    let effective = history |> List.filter (fun o -> o <> Skipped)

    if effective.Length < 2 then
        0.0
    else
        let transitions =
            effective |> List.pairwise |> List.sumBy (fun (a, b) -> if a = b then 0 else 1)

        float transitions / float (effective.Length - 1)

// Persistence: a single JSON file keyed by test name → records list, with
// most-recent-first ordering inside each list.

let private serializeRecord (r: TestRunRecord) : JsonObject =
    let o = JsonObject()
    o.["name"] <- JsonValue.Create(r.Name)
    o.["outcome"] <- JsonValue.Create(outcomeToString r.Outcome)
    o.["durationMs"] <- JsonValue.Create(r.DurationMs)
    o.["runStartedAt"] <- JsonValue.Create(r.RunStartedAt.ToString("o"))
    o

let private deserializeRecord (o: JsonObject) : TestRunRecord option =
    try
        Some
            { Name = o.["name"].GetValue<string>()
              Outcome = parseOutcome (o.["outcome"].GetValue<string>())
              DurationMs = o.["durationMs"].GetValue<int>()
              RunStartedAt = DateTime.Parse(o.["runStartedAt"].GetValue<string>()).ToUniversalTime() }
    with _ ->
        None

/// Read the history file. Returns Map.empty when the file is missing or
/// unparseable; per-test entries that fail to deserialize individually are
/// dropped silently so a single corrupted record can't shadow the rest.
let internal loadHistory (path: string) : Map<string, TestRunRecord list> =
    try
        if not (File.Exists path) then
            Map.empty
        else
            let json = File.ReadAllText path

            match JsonNode.Parse(json) with
            | :? JsonObject as root ->
                root
                |> Seq.choose (fun kvp ->
                    match kvp.Value with
                    | :? JsonArray as arr ->
                        let records =
                            arr
                            |> Seq.choose (fun node ->
                                match node with
                                | :? JsonObject as o -> deserializeRecord o
                                | _ -> None)
                            |> Seq.toList

                        Some(kvp.Key, records)
                    | _ -> None)
                |> Map.ofSeq
            | _ -> Map.empty
    with _ ->
        Map.empty

/// Append the given records to the history file, then trim each per-test
/// list to `keepN` most-recent entries. Atomic via temp + rename so a daemon
/// crash mid-write can't corrupt the on-disk file.
let internal appendRecords (path: string) (keepN: int) (records: TestRunRecord list) : unit =
    let existing = loadHistory path

    let merged =
        (existing, records)
        ||> List.fold (fun acc r ->
            let prior = Map.tryFind r.Name acc |> Option.defaultValue []
            let trimmed = (r :: prior) |> List.truncate (max 1 keepN)
            Map.add r.Name trimmed acc)

    let root = JsonObject()

    for KeyValue(name, recs) in merged do
        let arr = JsonArray()

        for r in recs do
            arr.Add(serializeRecord r)

        root.[name] <- arr

    FsHotWatch.FsHwPaths.atomicWriteAllText path (root.ToJsonString())

/// Top-K flakiest tests by score, descending. Tests with score 0.0 are
/// excluded — a zero-flakiness test is by definition not interesting here.
let internal topFlaky (k: int) (history: Map<string, TestRunRecord list>) : (string * float) list =
    history
    |> Map.toList
    |> List.map (fun (name, recs) ->
        let outcomes = recs |> List.map (fun r -> r.Outcome)
        name, computeFlakiness outcomes)
    |> List.filter (fun (_, score) -> score > 0.0)
    |> List.sortByDescending snd
    |> List.truncate (max 0 k)
