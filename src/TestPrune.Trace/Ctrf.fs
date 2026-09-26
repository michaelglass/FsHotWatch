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

/// The per-test rows of a CTRF report (`results.tests`, or a top-level `tests`). A real
/// MTP report omits rows for tests that threw a raw exception; such a test simply gets
/// no outcome here, which ingestion records as `NoOutcome`, never as passed. An
/// unreadable report has no rows.
let parse (json: string) : TestOutcome list =
    try
        let root = JsonNode.Parse json

        let rows =
            match root.["results"] with
            | null -> root.["tests"]
            | results ->
                match results.["tests"] with
                | null -> root.["tests"]
                | tests -> tests

        match rows with
        | :? JsonArray as a -> a |> Seq.choose rowOf |> List.ofSeq
        | _ -> []
    with _ ->
        []

/// Run `exe` with a CTRF report into `resultsDir` and return the report's outcomes.
/// `Error` names the run (`where`), its exit code and its last output lines when it wrote
/// no report; a report an earlier run left in `resultsDir` is removed first.
let run
    (where: string)
    (exe: string)
    (appArgs: string list)
    (env: (string * string) list)
    (workDir: string)
    (resultsDir: string)
    (timeout: TimeSpan)
    : Result<TestOutcome list, string> =
    let args =
        appArgs
        @ [ "--report-xunit-ctrf"
            "--report-xunit-ctrf-filename"
            "run.ctrf.json"
            "--results-directory"
            resultsDir ]

    let report = Path.Combine(resultsDir, "run.ctrf.json")

    // A report left by an earlier run in the same directory is never this run's.
    if File.Exists report then
        File.Delete report

    let code, output = Launch.run exe args env workDir timeout

    if File.Exists report then
        Ok(parse (File.ReadAllText report))
    else
        // The last lines usually say why: a test that ended the process, a crash at startup.
        let tail =
            output.Split '\n'
            |> Seq.filter (String.IsNullOrWhiteSpace >> not)
            |> Seq.toList
            |> List.rev
            |> List.truncate 5
            |> List.rev
            |> String.concat "\n"

        Error $"no CTRF report from the run %s{where} (exit %d{code}); its output ends:\n%s{tail}"
