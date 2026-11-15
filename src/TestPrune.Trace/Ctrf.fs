/// Test outcomes from a CTRF report, the per-test JSON report xUnit v3 writes under MTP.
module TestPrune.Trace.Ctrf

open System
open System.IO
open System.Text.Json.Nodes
open TestPrune.Trace.Model

let private outcomeOf (status: string) =
    match status.ToLowerInvariant() with
    | "passed" -> Passed
    | "failed" -> Failed
    | "skipped"
    | "pending" -> Skipped
    | _ -> OtherOutcome

let private rowOf (node: JsonNode) =
    match node with
    | null -> None
    | n ->
        match n.["name"], n.["status"] with
        | null, _
        | _, null -> None
        | name, status ->
            Some
                { Name = name.GetValue<string>()
                  Outcome = outcomeOf (status.GetValue<string>()) }

/// Escape raw control characters inside string literals (AUTOMATION-1073). xUnit v3's JSON
/// writer escapes only `\b \f \n \r \t` below U+0020, so an ANSI-coloured failure message
/// leaves a raw ESC that makes the whole report invalid JSON. Inside a literal such a
/// character can only mean itself; outside one nothing is touched. The same repair as
/// `FsHotWatch.Ctrf.repairControlChars`, which this package does not reference.
let private repairControlChars (json: string) : string =
    let out = Text.StringBuilder(json.Length)
    let mutable inString = false
    let mutable escaped = false

    for c in json do
        if inString && not escaped && c < ' ' then
            out.Append(sprintf "\\u%04x" (int c)) |> ignore
        else
            out.Append c |> ignore

        if not inString then
            inString <- c = '"'
        elif escaped then
            escaped <- false
        elif c = '\\' then
            escaped <- true
        elif c = '"' then
            inString <- false

    out.ToString()

/// The row array of a CTRF report (`results.tests`, else a top-level `tests`); empty when
/// there is none. Throws on text that is not JSON.
let private rowsIn (json: string) : JsonNode list =
    let root = JsonNode.Parse(repairControlChars json)

    let rows =
        match root.["results"] with
        | null -> root.["tests"]
        | results ->
            match results.["tests"] with
            | null -> root.["tests"]
            | tests -> tests

    match rows with
    | :? JsonArray as a -> List.ofSeq a
    | _ -> []

/// The per-test rows of a CTRF report (`results.tests`, or a top-level `tests`). A real
/// MTP report omits rows for tests that threw a raw exception; such a test simply gets
/// no outcome here, which ingestion records as `NoOutcome`, never as passed. An
/// unreadable report has no rows.
let parse (json: string) : TestOutcome list =
    try
        rowsIn json |> List.choose rowOf
    with _ ->
        []

/// The test class of each row, by its name: xUnit v3's `extra.type`, else the class in its
/// `suite` path (`[assembly, collection, class, method]`). A row naming neither has no class.
/// The class is xUnit's, as `--filter-class` takes it (a nested module is `Outer+Inner`).
/// An unreadable report has no classes.
let classes (json: string) : Map<string, string> =
    let text (node: JsonNode) =
        match node with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s when not (String.IsNullOrEmpty s) -> Some s
            | _ -> None
        | _ -> None

    let classOf (row: JsonNode) =
        let fromExtra =
            match row.["extra"] with
            | null -> None
            | extra -> text extra.["type"]

        fromExtra
        |> Option.orElse (
            match row.["suite"] with
            | :? JsonArray as suite when suite.Count >= 4 -> text suite.[2]
            | _ -> None
        )

    try
        rowsIn json
        |> List.choose (fun row ->
            match row with
            | null -> None
            | row -> Option.map2 (fun name cls -> name, cls) (text row.["name"]) (classOf row))
        |> Map.ofList
    with _ ->
        Map.empty

/// What one run's CTRF report says: each test's outcome (`parse`) and class (`classes`).
type Report =
    { Outcomes: TestOutcome list
      Classes: Map<string, string> }

/// The report a CTRF document holds.
let report (json: string) : Report =
    { Outcomes = parse json
      Classes = classes json }

/// Run `exe` with a CTRF report into `resultsDir` and return the report (`report`).
/// `Error` names the run (`where`), its exit code and its last output lines when it wrote
/// no report; a report an earlier run left in `resultsDir` is removed first.
let runReport
    (where: string)
    (exe: string)
    (appArgs: string list)
    (env: (string * string) list)
    (workDir: string)
    (resultsDir: string)
    (timeout: TimeSpan)
    : Result<Report, string> =
    let args =
        appArgs
        @ [ "--report-xunit-ctrf"
            "--report-xunit-ctrf-filename"
            "run.ctrf.json"
            "--results-directory"
            resultsDir ]

    let written = Path.Combine(resultsDir, "run.ctrf.json")

    // A report left by an earlier run in the same directory is never this run's.
    if File.Exists written then
        File.Delete written

    let code, output = Launch.run exe args env workDir timeout

    if File.Exists written then
        Ok(report (File.ReadAllText written))
    else
        // The last lines usually say why: a test that ended the process, a crash at startup.
        let tail =
            Launch.outputLines output
            |> List.rev
            |> List.truncate 5
            |> List.rev
            |> String.concat "\n"

        Error $"no CTRF report from the run %s{where} (exit %d{code}); its output ends:\n%s{tail}"

/// `runReport`'s outcomes alone.
let run where exe appArgs env workDir resultsDir timeout : Result<TestOutcome list, string> =
    runReport where exe appArgs env workDir resultsDir timeout
    |> Result.map _.Outcomes
