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

let private tryGetString (o: JsonNode) (key: string) : string option =
    match o.[key] with
    | null -> None
    | n ->
        try
            Some(n.GetValue<string>())
        with _ ->
            None

let private tryGetNumber (o: JsonNode) (key: string) : float option =
    match o.[key] with
    | null -> None
    | n ->
        try
            Some(n.GetValue<float>())
        with _ ->
            None

/// The per-test array in a real Microsoft.Testing.Platform / xUnit.v3 CTRF
/// report is nested under `results.tests` (confirmed against captured output;
/// `specVersion` "0.0.0"). Older / hand-authored documents put it at the top
/// level (`tests`). Try the spec-nested location first, then fall back, so a
/// runner-variant or a flattened fixture both parse. Returns null when neither
/// holds a JSON array.
let private asJsonArray (node: JsonNode) : JsonArray =
    match node with
    | :? JsonArray as arr -> arr
    | _ -> null

let private ctrfTestsArray (root: JsonNode) : JsonArray =
    let nested =
        match root.["results"] with
        | null -> null
        | results -> asJsonArray results.["tests"]

    match nested with
    | null -> asJsonArray root.["tests"]
    | arr -> arr

/// Parse a CTRF (Common Test Report Format) JSON document and extract per-
/// test records. Returns [] when the document has no `tests` array or is
/// unparseable — silent failure is the right behaviour for an opportunistic
/// post-test step that shouldn't crash the test runner if the report is
/// missing or malformed. Entries missing `name` or `status` are dropped.
///
/// NOTE: a real report OMITS per-test entries for tests that threw a raw
/// (non-assertion) exception while still counting them in the summary — so
/// this array is NOT a reliable run total. Use `tryParseReport` (summary
/// counts) for the pass/fail verdict; this array is only for flakiness history.
let internal parseCtrfTests (json: string) : TestRunRecord list =
    try
        match JsonNode.Parse(json) with
        | null -> []
        | root ->
            match ctrfTestsArray root with
            | null -> []
            | arr ->
                arr
                |> Seq.choose (fun node ->
                    if isNull node then
                        None
                    else
                        match tryGetString node "name", tryGetString node "status" with
                        | Some name, Some status ->
                            Some
                                { Name = name
                                  Outcome = parseOutcome status
                                  DurationMs = tryGetNumber node "duration" |> Option.map int |> Option.defaultValue 0
                                  RunStartedAt = DateTime.UtcNow }
                        | _ -> None)
                |> Seq.toList
    with _ ->
        []

/// Aggregate, runner-agnostic view of a test run, read from the report's SUMMARY
/// block. THE canonical shape lives in `FsHotWatch.Ctrf` — this is an ABBREVIATION
/// of it, not a second copy: the pass/fail verdict, the flakiness recorder and the
/// `suites` in `.fshw/verdict.json` all read one report through one parser, so they
/// cannot disagree about what it says.
type TestReport = FsHotWatch.Ctrf.Summary

module TestReport =
    /// "All clear" — the run produced NOTHING that is not a clean pass-or-skip.
    /// `Failed` and `Other` are both treated as problems (a test that threw an
    /// unrecognised/raw exception is NOT a pass); `Pending` is folded into
    /// skip-like and does not block green. The `Total > 0` guard is intentionally
    /// NOT here — an unfiltered zero-test run is a real problem that the verdict
    /// layer (which knows `wasFiltered`) decides, so this stays a pure "no bad
    /// results" predicate.
    let allClear (r: TestReport) : bool = r.Failed = 0 && r.Other = 0

/// Parse a CTRF report's SUMMARY counts. `None` when the JSON is unparseable or
/// carries no summary object — the signal the verdict logic reads as "no usable
/// report", so a truncated or never-flushed report is never mistaken for a clean
/// run.
let internal tryParseReport (json: string) : TestReport option = FsHotWatch.Ctrf.trySummary json

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
    match tryGetString o "name", tryGetString o "outcome", tryGetString o "runStartedAt" with
    | Some name, Some outcome, Some startedAt ->
        try
            Some
                { Name = name
                  Outcome = parseOutcome outcome
                  DurationMs = tryGetNumber o "durationMs" |> Option.map int |> Option.defaultValue 0
                  RunStartedAt = DateTime.Parse(startedAt).ToUniversalTime() }
        with _ ->
            None
    | _ -> None

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

/// How long a test's history survives after its LAST recorded run.
///
/// `keepN` bounds each test's record list, but nothing bounded the set of test
/// NAMES: a renamed, deleted, or one-off-parameterised test kept its entry
/// forever, so the file only ever grew (5.5 MB observed live) and every write
/// re-parsed and re-serialised all of it. A test that has not run in 30 days
/// cannot inform a flakiness score anyone cares about; it is history of a test
/// that no longer exists.
let DefaultHistoryRetention = TimeSpan.FromDays 30.0

/// Drop tests whose NEWEST run is older than `retention` as of `now`, and drop
/// tests left with no records at all. Pure, so the retention rule is testable
/// without touching disk.
let internal expireHistory
    (now: DateTime)
    (retention: TimeSpan)
    (history: Map<string, TestRunRecord list>)
    : Map<string, TestRunRecord list> =
    let cutoff = now - retention

    history
    |> Map.filter (fun _ recs ->
        match recs with
        | [] -> false
        | recs -> recs |> List.exists (fun r -> r.RunStartedAt >= cutoff))

/// Merge `records` into `history`, keeping each test's `keepN` most-recent
/// entries (most-recent-first), then expire tests whose newest run predates
/// `retention`. Pure — `appendRecords` is this plus the file I/O.
let internal mergeRecords
    (now: DateTime)
    (retention: TimeSpan)
    (keepN: int)
    (records: TestRunRecord list)
    (history: Map<string, TestRunRecord list>)
    : Map<string, TestRunRecord list> =
    (history, records)
    ||> List.fold (fun acc r ->
        let prior = Map.tryFind r.Name acc |> Option.defaultValue []
        let trimmed = (r :: prior) |> List.truncate (max 1 keepN)
        Map.add r.Name trimmed acc)
    |> expireHistory now retention

/// Append the given records to the history file, trim each per-test list to
/// `keepN` most-recent entries, and expire tests that have not run in
/// `DefaultHistoryRetention`. Atomic via temp + rename so a daemon crash
/// mid-write can't corrupt the on-disk file.
///
/// Call this ONCE PER RUN, with every project's records. It is a full `loadHistory`
/// + full rewrite of the whole file, so calling it per test CONFIG would mean one
/// parse+rewrite cycle per project; and since those configs run under
/// `Async.Parallel`, a per-config read-modify-write would race itself (two projects
/// finishing together each load the same `existing`, and the second write drops the
/// first's records). One call, after the parallel section, avoids both.
let internal appendRecords (path: string) (keepN: int) (records: TestRunRecord list) : unit =
    let merged =
        loadHistory path
        |> mergeRecords DateTime.UtcNow DefaultHistoryRetention keepN records

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
