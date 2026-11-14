/// A test failure re-run green at the same inputs (ADR-059 §4 ledger #10, AUTOMATION-1444).
///
/// The test runner files a project's full-suite run under its K_P, a fail as well as a pass
/// (ADR-059 §16). A check that runs the project again, re-selecting the failed tests, files
/// that later run under the `impact` key beside it, so nothing at the full key ever said the
/// failure was run again. A check-time lookup of the full key stayed red on a check today
/// graded green: today's outstanding-failure ledger clears a failure the moment a later run
/// passes it.
///
/// The store keeps both facts instead. A full fail whose report names every failure it
/// counts is filed with those names. A later PASSING run at the same K_P that passed each of
/// them files a CLEARANCE, keyed on the fail it clears (its key and its run). Under `check`,
/// a full fail with a clearance reads as a pass: the full suite ran at these inputs, and
/// every test it failed has since passed at these inputs, which is exactly the evidence
/// today's check grades green on. Nothing else is cleared:
///   * a failure nobody can name (a raw throw, a crashed host, a timeout) has no names;
///   * a later run that did not pass every named failure files no clearance;
///   * a clearance names one fail's run, so a NEWER fail at the key is red again;
///   * `confirm` never reads clearances: it grades the full suite itself, as today does.
module FsHotWatch.TestFailureClearance

open System.Text.Json
open System.Text.Json.Nodes

/// The record kind a clearance is filed under.
[<Literal>]
let Kind = "test-cleared"

/// The outputs (ADR-059 §14) of a fail record whose report names its failed tests.
let outputsOf (failedTests: string list) : string =
    let names = JsonArray()

    for name in failedTests do
        names.Add(JsonValue.Create name)

    let o = JsonObject()
    o["failedTests"] <- names
    o.ToJsonString()

/// The failed tests a fail record's outputs name; `None` when they name none.
let failedTestsOf (outputs: string option) : string list option =
    outputs
    |> Option.bind (fun text ->
        try
            use doc = JsonDocument.Parse text

            match Json.tryProp doc.RootElement "failedTests" with
            | Some names when names.ValueKind = JsonValueKind.Array ->
                let read =
                    [ for n in names.EnumerateArray() ->
                          if n.ValueKind = JsonValueKind.String then
                              Some(n.GetString())
                          else
                              None ]

                if read.IsEmpty || read |> List.exists Option.isNone then
                    None
                else
                    Some(List.choose id read)
            | _ -> None
        with :? JsonException ->
            None)

/// The key of a clearance of the fail run `failRun` filed under `failed`.
let keyOf (failed: ActionKey.KeyValue) (failRun: string) : ActionKey.ActionKey =
    ActionKey.create Kind [ "failed", failed.Text; "fail-run", failRun ]

/// The clearance `pass` earns, when it is a passing run that passed every failure `newest`
/// (the newest record at the full key `full`, with its outputs) names, filed strictly after
/// that fail by another run (a tie goes to the fail, as in `OutcomeStore`). `passed` is every test `pass`'s report names as passed.
let clearanceOf
    (full: ActionKey.KeyValue)
    (newest: (OutcomeStore.Record * string option) option)
    (passed: Set<string>)
    (pass: OutcomeStore.Record)
    : OutcomeStore.Record option =
    match newest with
    | Some(fail, outputs) when
        pass.Outcome = OutcomeStore.Outcome.Pass
        && fail.Outcome = OutcomeStore.Outcome.Fail
        ->
        match fail.Producer.RunId, failedTestsOf outputs with
        | Some failRun, Some failed when
            pass.Producer.RunId <> Some failRun
            && fail.RecordedAt < pass.RecordedAt
            && failed |> List.forall passed.Contains
            ->
            Some
                { pass with
                    Key = keyOf full failRun
                    Counts = None }
        | _ -> None
    | _ -> None

/// The answer a CHECK reads for `key` in `store`: its newest record, except that a fail a
/// clearance names reads as a pass. Through any `IOutcomeStore` (AUTOMATION-1695): the disk store
/// or the simulation's in-memory one.
let lookupForCheckIn (store: OutcomeRecords.IOutcomeStore) (key: ActionKey.KeyValue) : OutcomeStore.Outcome option =
    match OutcomeStore.Through.lookup store key with
    | Some fail when fail.Outcome = OutcomeStore.Outcome.Fail ->
        let cleared =
            fail.Producer.RunId
            |> Option.bind (fun run -> OutcomeStore.Through.lookup store (keyOf key run).Value)
            |> Option.exists (fun c -> c.Outcome = OutcomeStore.Outcome.Pass && c.RecordedAt > fail.RecordedAt)

        Some(
            if cleared then
                OutcomeStore.Outcome.Pass
            else
                OutcomeStore.Outcome.Fail
        )
    | other -> other |> Option.map _.Outcome

/// `lookupForCheckIn` over the disk store.
let lookupForCheck (store: OutcomeStore.Store) (key: ActionKey.KeyValue) : OutcomeStore.Outcome option =
    lookupForCheckIn store key
