/// The daily test-row log (AUTOMATION-1744): every run's per-test rows, in a file of its own under
/// the store root, `test-rows/<yyyy-mm-dd>.<run>.<project>.jsonl.gz`, named by its recorded UTC day.
///
/// The rows filed beside a record (`TestRows`, `<record id>.tests.jsonl.gz`) live and die with
/// its key, and the store evicts keys by size within days, so a red and the green that shows it
/// flaky rarely survive together. The log lives outside the evictable keys, under its own TTL and
/// byte cap (`Retention`, `testRowsLog` in `.fshw.json`), so flake rates can span months.
///
/// FILE: one per run and project, written once (`TestRows.encodeLog`), each line naming its run.
/// A file is never rewritten; retention deletes whole files by the day in their name.
module FsHotWatch.TestRowsLog

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// The log's directory under the store root.
[<Literal>]
let DirectoryName = "test-rows"

/// What every day file's name ends with.
[<Literal>]
let Extension = ".jsonl.gz"

/// How long day files are kept, and how many bytes they may take together.
type Retention = { Ttl: TimeSpan; MaxBytes: int64 }

module Retention =
    /// The `.fshw.json` key that configures the log.
    [<Literal>]
    let ConfigKey = "testRowsLog"

    /// Days a day file is kept when `testRowsLog.ttlDays` is absent: long enough for a flake
    /// rate over a quarter.
    [<Literal>]
    let DefaultTtlDays = 90

    /// The cap when `testRowsLog.maxMegabytes` is absent. Measured on 2026-11-16: a row costs
    /// about 33 bytes gzipped, about 0.7 MB for a full intelligence run of ~21,000 tests. 512 MiB
    /// holds about 730 full-run equivalents, eight a day for the default 90 days, against the one
    /// full run a day plus smaller impact runs the server makes; and it is a quarter of the outcome
    /// store's own 2 GiB, so the log cannot crowd out the records it outlives.
    [<Literal>]
    let DefaultMaxMegabytes = 512

    let private megabyte = 1024L * 1024L

    let defaults: Retention =
        { Ttl = TimeSpan.FromDays(float DefaultTtlDays)
          MaxBytes = int64 DefaultMaxMegabytes * megabyte }

    /// `name` of `o` as a positive whole number, or `fallback` when it is absent.
    let private positive (o: JsonElement) (name: string) (fallback: int) : Result<int, string> =
        let refuse (v: JsonElement) =
            Error(
                ConfigKey
                + "."
                + name
                + ": "
                + v.GetRawText()
                + " is not a positive whole number"
            )

        match o.TryGetProperty name with
        | false, _ -> Ok fallback
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, n when n > 0 -> Ok n
            | _ -> refuse v
        | true, v -> refuse v

    /// The retention `json`, a `.fshw.json`'s text, configures: `testRowsLog.ttlDays` and
    /// `testRowsLog.maxMegabytes`, each a positive whole number, each its default when absent.
    /// Raises `JsonException` for text that is not JSON.
    let parse (json: string) : Result<Retention, string> =
        use doc = JsonDocument.Parse json

        match Json.tryProp doc.RootElement ConfigKey with
        | None -> Ok defaults
        | Some v when v.ValueKind = JsonValueKind.Object ->
            let unknown =
                v.EnumerateObject()
                |> Seq.map _.Name
                |> Seq.tryFind (fun name ->
                    not (
                        name.StartsWith("_", StringComparison.Ordinal)
                        || name = "ttlDays"
                        || name = "maxMegabytes"
                    ))

            match unknown with
            | Some name ->
                Error(
                    ConfigKey
                    + ": unknown key '"
                    + name
                    + "' — expected ttlDays or maxMegabytes (a key starting with _ is a comment)"
                )
            | None ->
                positive v "ttlDays" DefaultTtlDays
                |> Result.bind (fun days ->
                    positive v "maxMegabytes" DefaultMaxMegabytes
                    |> Result.map (fun mb ->
                        { Ttl = TimeSpan.FromDays(float days)
                          MaxBytes = int64 mb * megabyte }))
        | Some v -> Error(ConfigKey + ": " + v.GetRawText() + " is not an object")

    /// The retention `repoRoot`'s `.fshw.json` configures; the defaults when it has none, and why
    /// not when it cannot be read or is not JSON.
    let ofRepo (repoRoot: string) : Result<Retention, string> =
        let path = FsHwPaths.configFile repoRoot

        if File.Exists path then
            try
                parse (File.ReadAllText path)
            with
            | :? JsonException
            | :? IOException
            | :? UnauthorizedAccessException as ex -> Error(FsHwPaths.ConfigFileName + ": " + ex.Message)
        else
            Ok defaults

/// A run id as a file name keeps it when it is letters and digits only (fshw's run ids are a
/// Guid's 32 hex digits), and is otherwise `sha-` and 12 hex digits of its hash, which no such
/// run id can be.
let private plainRunId = Regex("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant)

/// The name of `index`'s file: `<yyyy-mm-dd>.<run>.<project>.jsonl.gz`, by the run's recorded
/// UTC day, its run id and 12 hex digits of its project's hash. One run id covers every project a
/// launch ran, so the project keeps two of its runs apart.
let fileNameOf (index: TestRows.Index) : string =
    let token (text: string) =
        (ContentHash.ofText text).Substring(0, 12)

    let day =
        index.Run.RecordedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let run =
        if plainRunId.IsMatch index.Run.RunId then
            index.Run.RunId
        else
            "sha-" + token index.Run.RunId

    day + "." + run + "." + token index.Run.Project + Extension

/// A log file's name, as `fileNameOf` writes it.
let private logFileName =
    Regex(
        @"^(\d{4}-\d{2}-\d{2})\.([A-Za-z0-9]+|sha-[0-9a-f]{12})\.[0-9a-f]{12}\.jsonl\.gz$",
        RegexOptions.CultureInvariant
    )

/// The UTC day `name`, a file in the log's directory, holds a run of; `None` for a file that is
/// not named as a run's: not the log's.
let private dayOf (name: string) : DateOnly option =
    let m = logFileName.Match name

    match
        m.Success,
        DateOnly.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None)
    with
    | true, (true, day) -> Some day
    | _ -> None

/// The path of `index`'s file in the log under `root`.
let pathOf (root: string) (index: TestRows.Index) : string =
    Path.Combine(root, DirectoryName, fileNameOf index)

/// The log's files under `root`, oldest day first (by name within a day), with their day.
let private dayFiles (root: string) : (string * DateOnly) list =
    let dir = Path.Combine(root, DirectoryName)

    if Directory.Exists dir then
        Directory.GetFiles dir
        |> Array.choose (fun path -> dayOf (Path.GetFileName path) |> Option.map (fun day -> path, day))
        |> Array.sortBy (fun (path, day) -> day, Path.GetFileName path)
        |> List.ofArray
    else
        []

/// What a read of the log found.
type LogRead =
    {
        /// Every run read, files in day order.
        Runs: TestRows.Index list
        /// Files that could not be read, or are torn or not gzip: each loses only its own run.
        UnreadableFiles: int
        /// Lines, in files that were read, that are not a log line.
        UnreadableLines: int
    }

/// Every run the log under `root` holds, read without changing it. Each file is read on its own: a
/// file that cannot be read, or is torn or not gzip, loses only its own run, and is counted.
let read (root: string) : LogRead =
    let reads =
        dayFiles root
        |> List.map (fun (path, _) ->
            try
                TestRows.decodeLog (File.ReadAllBytes path)
            with
            | :? IOException
            | :? UnauthorizedAccessException as ex -> Error ex.Message)

    let files = reads |> List.choose Result.toOption

    { Runs = files |> List.collect _.Runs
      UnreadableFiles = reads |> List.filter Result.isError |> List.length
      UnreadableLines = files |> List.sumBy _.UnreadableLines }

/// What `collect` deleted, by file name, and what it could not delete, with why.
type Collected =
    {
        /// Day files older than the TTL, oldest first.
        Expired: string list
        /// Day files deleted, oldest first, to bring the log within its cap.
        OverCap: string list
        NotDeleted: (string * string) list
    }

/// Apply `retention` to the log under `root` at `now`, by whole day files: first every file of a
/// day before the TTL's cutoff (the UTC day `now - Ttl` falls on), then the oldest files until the
/// rest fit `MaxBytes`. A file that is not a day file is never deleted, nor counted.
let collect (root: string) (now: DateTimeOffset) (retention: Retention) : Collected =
    let cutoff = DateOnly.FromDateTime((now - retention.Ttl).UtcDateTime)
    let expired, kept = dayFiles root |> List.partition (fun (_, day) -> day < cutoff)
    let sized = kept |> List.map (fun (path, _) -> path, FileInfo(path).Length)

    // Oldest first: each file with the bytes the log holds before it is deleted. Those holding
    // more than the cap go.
    let overCap =
        sized
        |> List.scan (fun (_, left) (path, size) -> Some(path, left), left - size) (None, List.sumBy snd sized)
        |> List.choose fst
        |> List.filter (fun (_, before) -> before > retention.MaxBytes)
        |> List.map fst

    let delete (path: string) =
        try
            File.Delete path
            Ok(Path.GetFileName path)
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex -> Error(Path.GetFileName path, ex.Message)

    let expiredResults = expired |> List.map (fst >> delete)
    let overCapResults = overCap |> List.map delete

    { Expired = expiredResults |> List.choose Result.toOption
      OverCap = overCapResults |> List.choose Result.toOption
      NotDeleted =
        expiredResults @ overCapResults
        |> List.choose (fun result ->
            match result with
            | Error failure -> Some failure
            | Ok _ -> None) }
