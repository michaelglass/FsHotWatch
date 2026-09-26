/// Test outcomes from a CTRF report, the per-test JSON report xUnit v3 writes under MTP.
module TestPrune.Trace.Ctrf

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
