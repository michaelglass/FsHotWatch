/// Per-test rows of one test-project run (AUTOMATION-1744): each test's name, outcome and
/// duration, derived from the run's CTRF report when its outcome record is filed, so a
/// per-test flake rate outlives the checkout's `.fshw/test-runs`, which is reclaimed.
///
/// FILE: an index filed BESIDE its record, under the record's key, named after it
/// (`<record id>.tests.jsonl.gz`), so it lives and dies with that key under the store's own TTL
/// and byte budget. Gzipped JSON lines: a header line naming the run once (`Run`), then one line
/// per test (`Row`). A record read never reads it (records are `*.json`).
module FsHotWatch.TestRows

open System
open System.Globalization
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open FsHotWatch.Core

/// The index format. A file stating any other schema is not an index.
[<Literal>]
let Schema = "fshw-test-rows-v1"

/// What an index's file name ends with.
[<Literal>]
let Suffix = ".tests.jsonl.gz"

/// How one test came out. A test cancelled or timed out did not FAIL an assertion, so neither
/// is `Failed`: a flake rate that folded them together would blame the test for the host.
[<RequireQualifiedAccess>]
type Outcome =
    | Passed
    | Failed
    /// Stopped at its own timeout.
    | TimedOut
    /// Stopped by a cancellation.
    | Cancelled
    /// Skipped or pending: it did not run.
    | Skipped
    /// A CTRF status outside passed/failed/skipped/pending, such as an errored test.
    | Other

module Outcome =
    /// The token an index writes.
    let token (outcome: Outcome) : string =
        match outcome with
        | Outcome.Passed -> "passed"
        | Outcome.Failed -> "failed"
        | Outcome.TimedOut -> "timedOut"
        | Outcome.Cancelled -> "cancelled"
        | Outcome.Skipped -> "skipped"
        | Outcome.Other -> "other"

    /// The outcome `token` names; `None` for a token no index writes.
    let tryParse (token: string) : Outcome option =
        match token with
        | "passed" -> Some Outcome.Passed
        | "failed" -> Some Outcome.Failed
        | "timedOut" -> Some Outcome.TimedOut
        | "cancelled" -> Some Outcome.Cancelled
        | "skipped" -> Some Outcome.Skipped
        | "other" -> Some Outcome.Other
        | _ -> None

/// One test's outcome in the run.
type Row =
    { Name: string
      Outcome: Outcome
      DurationMs: int64 }

/// The run an index describes, written once per file.
type Run =
    {
        /// The key of the record the index is filed beside: the run's input set.
        Key: ActionKey.KeyValue
        /// The test project (the record's subject).
        Project: string
        /// The run whose report the rows were read from.
        RunId: string
        /// The verdict tree hash of the checkout that ran it.
        Tree: string
        /// The fshw build that ran it (the record's producer binary).
        Tool: string
        /// The host it ran on.
        Machine: string
        RecordedAt: DateTimeOffset
    }

/// One run's rows. A run with no rows files no index.
type Index = { Run: Run; Rows: NonEmpty<Row> }

/// What a writer files beside a record: what the record does not already say about the run.
/// The store takes the key, project, tool and instant from the record itself
/// (`OutcomeRecords.TestRowsOf.index`), so an index cannot disagree with its record.
type RunRows =
    { RunId: string
      Tree: string
      Machine: string
      Rows: NonEmpty<Row> }

/// The index file name of the record filed as `recordFile` (`<record id>.json`).
let fileNameFor (recordFile: string) : string =
    Path.GetFileNameWithoutExtension(Path.GetFileName recordFile) + Suffix

/// `index` as the bytes it is filed as.
let encode (index: Index) : byte[] =
    let header = JsonObject()
    header["schema"] <- Schema
    header["key"] <- index.Run.Key.Text
    header["project"] <- index.Run.Project
    header["runId"] <- index.Run.RunId
    header["tree"] <- index.Run.Tree
    header["tool"] <- index.Run.Tool
    header["machine"] <- index.Run.Machine
    header["recordedAt"] <- index.Run.RecordedAt.ToString("o", CultureInfo.InvariantCulture)

    let line (row: Row) =
        let o = JsonObject()
        o["name"] <- row.Name
        o["outcome"] <- Outcome.token row.Outcome
        o["ms"] <- row.DurationMs
        o.ToJsonString()

    let text =
        header.ToJsonString() :: (NonEmpty.toList index.Rows |> List.map line)
        |> String.concat "\n"

    use buffer = new MemoryStream()

    do
        use gzip = new GZipStream(buffer, CompressionLevel.SmallestSize)
        let bytes = Encoding.UTF8.GetBytes text
        gzip.Write(bytes, 0, bytes.Length)

    buffer.ToArray()

let private property (o: JsonElement) (name: string) : JsonElement option =
    if o.ValueKind = JsonValueKind.Object then
        match o.TryGetProperty name with
        | true, value -> Some value
        | false, _ -> None
    else
        None

let private text (value: JsonElement) : string option =
    if value.ValueKind = JsonValueKind.String then
        Some(value.GetString())
    else
        None

let private whole (value: JsonElement) : int64 option =
    if value.ValueKind = JsonValueKind.Number then
        match value.TryGetInt64() with
        | true, n -> Some n
        | false, _ -> None
    else
        None

let private instant (value: string) : DateTimeOffset option =
    match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
    | true, at -> Some at
    | false, _ -> None

/// `name` of `o`, read by `read`, or why it is absent.
let private required (o: JsonElement) (read: JsonElement -> 'T option) (name: string) : Result<'T, string> =
    match property o name |> Option.bind read with
    | Some value -> Ok value
    | None -> Error $"no {name}"

let private parseRun (line: string) : Result<Run, string> =
    use doc = JsonDocument.Parse line
    let o = doc.RootElement
    let field = required o text

    field "schema"
    |> Result.bind (fun schema ->
        if schema = Schema then
            Ok()
        else
            Error $"unknown schema {schema}")
    |> Result.bind (fun () -> required o (text >> Option.bind ActionKey.KeyValue.tryParse) "key")
    |> Result.bind (fun key ->
        field "project"
        |> Result.bind (fun project ->
            field "runId"
            |> Result.bind (fun runId ->
                field "tree"
                |> Result.bind (fun tree ->
                    field "tool"
                    |> Result.bind (fun tool ->
                        field "machine"
                        |> Result.bind (fun machine ->
                            required o (text >> Option.bind instant) "recordedAt"
                            |> Result.map (fun recordedAt ->
                                { Key = key
                                  Project = project
                                  RunId = runId
                                  Tree = tree
                                  Tool = tool
                                  Machine = machine
                                  RecordedAt = recordedAt })))))))

let private parseRow (line: string) : Result<Row, string> =
    use doc = JsonDocument.Parse line
    let o = doc.RootElement

    required o text "name"
    |> Result.bind (fun name ->
        required o (text >> Option.bind Outcome.tryParse) "outcome"
        |> Result.bind (fun outcome ->
            required o whole "ms"
            |> Result.map (fun ms ->
                { Name = name
                  Outcome = outcome
                  DurationMs = ms })))

/// Every result's value, in order, or the first error.
let private allOk (results: Result<'T, string> list) : Result<'T list, string> =
    List.foldBack
        (fun result rest -> result |> Result.bind (fun x -> rest |> Result.map (fun xs -> x :: xs)))
        results
        (Ok [])

/// The index `bytes` hold, or why they are not one: not gzip, no header, a header of another
/// schema or lacking a run field, a row that does not parse, or no rows. One bad row makes the
/// whole file torn.
let decode (bytes: byte[]) : Result<Index, string> =
    try
        let text =
            use input = new MemoryStream(bytes)
            use gzip = new GZipStream(input, CompressionMode.Decompress)
            use reader = new StreamReader(gzip, Encoding.UTF8)
            reader.ReadToEnd()

        match text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
        | [] -> Error "empty"
        | header :: rows ->
            parseRun header
            |> Result.bind (fun run ->
                rows
                |> List.map parseRow
                |> allOk
                |> Result.bind (fun rows ->
                    match NonEmpty.ofList rows with
                    | Some rows -> Ok { Run = run; Rows = rows }
                    | None -> Error "no rows"))
    with
    | :? InvalidDataException as ex -> Error ex.Message
    | :? JsonException as ex -> Error ex.Message
