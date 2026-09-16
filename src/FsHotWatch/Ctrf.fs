/// CTRF (Common Test Report Format) — the machine-readable test report.
///
/// fshw's runners (xUnit.v3 via Microsoft.Testing.Platform) emit CTRF when asked with
/// `--report-ctrf`, and fshw reads it back as the AUTHORITATIVE pass/fail verdict (the
/// process exit code is only a tie-break — see `TestPrunePlugin.classifyTestOutcome`).
///
/// `trySummary` is the one DIAGNOSTIC summary reader: the flakiness recorder and the
/// status views read the same block through it, so they cannot disagree about what a
/// report says. A verdict reads `tryVerdictReport`, which also refuses a report whose
/// counters are malformed or whose clean summary its rows do not account for. Reports
/// are retained on disk (bounded, newest-per-run) so a consumer can be pointed at them.
module FsHotWatch.Ctrf

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// Aggregate, runner-agnostic counts read from a CTRF report's SUMMARY block
/// (`results.summary`).
///
/// This — NOT the per-test array — is the authoritative source for a pass/fail
/// verdict: the per-test array omits raw-throw/errored tests while the summary
/// still counts them. `Other` captures CTRF statuses outside
/// passed/failed/skipped/pending (an individually-errored test).
type Summary =
    { Total: int
      Passed: int
      Failed: int
      Skipped: int
      Other: int }

/// A CTRF report belonging to a run.
///
/// `Project` comes from the FILE NAME and `RunId` from the DIRECTORY it sits in.
/// Neither is inferred from a timestamp — membership of a run is decided by where the
/// file is. See `runDir`.
type Report =
    {
        Project: string
        RunId: string
        /// Absolute path to the report.
        Path: string
        Summary: Summary
    }

/// The suffix every CTRF report fshw requests is named with.
[<Literal>]
let ReportSuffix = ".ctrf.json"

/// How many run directories to retain. Old runs are rotated, never wiped on start.
[<Literal>]
let RetainedRuns = 10

/// The root under which every run's directory lives.
let reportsDir (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "test-runs")

/// The directory IS the run: `.fshw/test-runs/<runId>/<Project>.ctrf.json`, and nothing
/// else lives there. The run-dir exists and is empty when a run executed nothing, and
/// does not exist at all when no run happened, so absence never has to be decoded from
/// mtimes.
let runDir (repoRoot: string) (runId: Guid) : string =
    Path.Combine(reportsDir repoRoot, runId.ToString("N"))

let private tryGetNumber (node: JsonNode) (key: string) : float option =
    match node.[key] with
    | null -> None
    | v ->
        try
            Some(v.GetValue<float>())
        with _ ->
            None

/// Parse a CTRF report's SUMMARY counts. `None` when the JSON is unparseable or
/// carries no summary object — the signal the verdict logic reads as "no usable
/// report", so a truncated or never-flushed report is never mistaken for a clean
/// run. Reads ONLY the summary; the per-test array is unreliable for totals.
/// The summary is nested under `results.summary` in real MTP/xUnit.v3 output,
/// with a top-level fallback for flattened variants.
let trySummary (json: string) : Summary option =
    try
        match JsonNode.Parse(json) with
        | null -> None
        | root ->
            let summary =
                match root.["results"] with
                | null -> root.["summary"]
                | results -> results.["summary"]

            match summary with
            | null -> None
            | s ->
                let getInt key =
                    tryGetNumber s key |> Option.map int |> Option.defaultValue 0

                Some
                    { Total = getInt "tests"
                      Passed = getInt "passed"
                      Failed = getInt "failed"
                      Skipped = getInt "skipped"
                      Other = getInt "other" }
    with
    | :? JsonException
    | :? InvalidOperationException -> None

/// A report that may decide a verdict: every counter is a nonnegative integer, and a
/// CLEAN summary is accounted for by its rows. Opaque, so the only way to hold one is to
/// have passed `tryVerdictReport`.
///
/// `trySummary` is not enough for a verdict. It defaults an absent counter to zero and
/// reads only the summary, so a clean summary claiming seven tests beside one row reads
/// as seven passes. A clean report has no reason to omit a row. A RED one does: a test
/// that threw a raw exception is counted in `failed` and `other` and gets no row, so a
/// red summary stays authoritative without reconciliation. Coherence proves nothing about
/// scope or ownership, and a coherent report of zero tests does not prove a test ran.
type VerdictReport = private VerdictReport of Summary

module VerdictReport =
    /// The validated counts. Reading them grants no way to construct a report.
    let summary (VerdictReport summary) : Summary = summary

let private objectOf (node: JsonNode) : JsonObject option =
    match node with
    | :? JsonObject as o -> Some o
    | _ -> None

let private counter (summary: JsonObject) (key: string) : Result<int, string> =
    match summary.[key] with
    | :? JsonValue as value ->
        match value.TryGetValue<int>() with
        | true, count when count >= 0 -> Ok count
        | _ -> Error $"summary.%s{key} is not a nonnegative integer"
    | _ -> Error $"summary.%s{key} is not a nonnegative integer"

/// The status a clean report's row may carry, or `None` for any row that is not an
/// object with one of them.
let private cleanStatus (row: JsonNode) : string option =
    objectOf row
    |> Option.bind (fun o ->
        match o.["status"] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, ("passed" | "pending" | "skipped" as status) -> Some status
            | _ -> None
        | _ -> None)

/// All six CTRF summary counters, or the first one that is absent or malformed.
let private counters (summary: JsonObject) : Result<Map<string, int>, string> =
    [ "tests"; "passed"; "failed"; "pending"; "skipped"; "other" ]
    |> List.fold
        (fun read key ->
            read
            |> Result.bind (fun (counts: Map<string, int>) ->
                counter summary key |> Result.map (fun n -> counts.Add(key, n))))
        (Ok Map.empty)

let private reconcile (counts: Map<string, int>) (rows: JsonNode) : Result<VerdictReport, string> =
    let verdict =
        VerdictReport
            { Total = counts["tests"]
              Passed = counts["passed"]
              Failed = counts["failed"]
              Skipped = counts["skipped"]
              Other = counts["other"] }

    if counts["failed"] > 0 || counts["other"] > 0 then
        Ok verdict
    else
        match rows with
        | :? JsonArray as rows ->
            let statuses = rows |> Seq.map cleanStatus |> List.ofSeq

            let disagrees status =
                (statuses |> List.filter ((=) (Some status)) |> List.length) <> counts[status]

            let total = counts["tests"]

            if rows.Count <> total then
                Error $"a clean summary counts %d{total} test(s) but the report lists %d{rows.Count} row(s)"
            elif statuses |> List.exists Option.isNone then
                Error "a clean summary lists a row that is not passed, pending or skipped"
            elif [ "passed"; "pending"; "skipped" ] |> List.exists disagrees then
                Error "a clean summary's counters disagree with its rows"
            else
                Ok verdict
        | _ -> Error "a clean summary has no tests array to account for it"

/// Validate a report as verdict evidence. `Error` names why it cannot be one: not JSON,
/// no summary object, a counter that is absent or not a nonnegative integer, or a clean
/// summary its rows do not account for. The summary is nested under `results` in real
/// output, with a top-level fallback for flattened variants, as in `trySummary`.
let tryVerdictReport (json: string) : Result<VerdictReport, string> =
    try
        match JsonNode.Parse json |> objectOf with
        | None -> Error "the report is not a JSON object"
        | Some top ->
            let body =
                match top.["results"] with
                | null -> Some top
                | nested -> objectOf nested

            match
                body
                |> Option.bind (fun b -> objectOf b.["summary"] |> Option.map (fun s -> b, s))
            with
            | None -> Error "the report carries no summary object"
            | Some(body, summary) -> counters summary |> Result.bind (fun counts -> reconcile counts body.["tests"])
    with
    // A duplicate property surfaces as ArgumentException when the object is read: which
    // of two `passed` counters is the real one is not a question evidence can leave open.
    | :? JsonException
    | :? ArgumentException -> Error "the report is not valid JSON"

let private tryReadReportWith (parse: string -> Summary option) (runId: string) (path: string) : Report option =
    let fileName = Path.GetFileName(path)

    if not (fileName.EndsWith(ReportSuffix, StringComparison.Ordinal)) then
        None
    else
        let json =
            try
                Some(File.ReadAllText(path))
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        json
        |> Option.bind parse
        |> Option.map (fun summary ->
            { Project = fileName.Substring(0, fileName.Length - ReportSuffix.Length)
              RunId = runId
              Path = path
              Summary = summary })

/// Read one report file. `None` for anything that is not a well-formed fshw CTRF
/// report — unreadable, unparseable, or carrying no summary. A report we cannot
/// read is not evidence, and is never counted as a zero-failure pass.
///
/// DIAGNOSTIC: the counts are read as `trySummary` reads them. A verdict reads
/// `tryReadVerdictReport` instead.
let tryReadReport (runId: string) (path: string) : Report option = tryReadReportWith trySummary runId path

/// Read one report file only when it is verdict evidence (`tryVerdictReport`).
let tryReadVerdictReport (runId: string) (path: string) : Report option =
    tryReadReportWith (tryVerdictReport >> Result.toOption >> Option.map VerdictReport.summary) runId path

/// Did this run happen at all? The run-dir is created before anything executes, so its
/// existence records that a run took place, whether or not it produced a report.
let runExists (repoRoot: string) (runId: Guid) : bool = Directory.Exists(runDir repoRoot runId)

let private reportsForRunWith (read: string -> string -> Report option) (repoRoot: string) (runId: Guid) : Report list =
    let dir = runDir repoRoot runId

    if not (Directory.Exists dir) then
        []
    else
        try
            Directory.GetFiles(dir, "*" + ReportSuffix)
            |> Array.toList
            |> List.choose (read (runId.ToString("N")))
            |> List.sortBy (fun r -> r.Project)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> []

/// The reports THIS RUN produced — the files in the run's own directory. An empty list
/// from an existing run-dir means the run executed no tests. DIAGNOSTIC, like
/// `tryReadReport`.
let reportsForRun (repoRoot: string) (runId: Guid) : Report list =
    reportsForRunWith tryReadReport repoRoot runId

/// The reports THIS RUN produced that are verdict evidence. A report that fails
/// `tryVerdictReport` is absent here, so a count copied from this list is a count its
/// rows account for.
let verdictReportsForRun (repoRoot: string) (runId: Guid) : Report list =
    reportsForRunWith tryReadVerdictReport repoRoot runId

/// Every retained run directory, newest first (by write time of the directory).
let private runDirs (repoRoot: string) : DirectoryInfo list =
    let root = reportsDir repoRoot

    if not (Directory.Exists root) then
        []
    else
        try
            DirectoryInfo(root).GetDirectories()
            |> Array.toList
            |> List.sortByDescending (fun d -> d.LastWriteTimeUtc)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> []

/// The most recent run's reports — for `fshw status`, which triggers nothing and so
/// has no run of its own to point at.
let latestRunReports (repoRoot: string) : Report list =
    match runDirs repoRoot with
    | [] -> []
    | newest :: _ ->
        try
            newest.GetFiles("*" + ReportSuffix)
            |> Array.toList
            |> List.choose (fun f -> tryReadReport newest.Name f.FullName)
            |> List.sortBy (fun r -> r.Project)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> []

/// Bound what `.fshw/test-runs/` accumulates. Run after every test run:
///
///   * keep the newest `keepRuns` run directories, delete the rest;
///   * delete loose files at the top level — the old flat layout, which nothing could
///     attribute to a run;
///   * best-effort throughout, so the catch is widened to all exceptions: tidying must
///     never fail the run that produced the evidence. One enumeration of the run dirs
///     is used, so a run-dir appearing mid-sweep cannot push `List.skip` past the
///     list length.
let tidyRunsDir (repoRoot: string) (keepRuns: int) : unit =
    let root = reportsDir repoRoot

    if Directory.Exists root then
        try
            // Loose files at the top level are always the old layout — the current
            // one puts every report inside a run directory.
            for stale in Directory.GetFiles(root) do
                try
                    File.Delete(stale)
                with _ ->
                    ()

            let dirs = runDirs repoRoot

            dirs
            |> List.skip (min keepRuns (List.length dirs))
            |> List.iter (fun d ->
                try
                    d.Delete(true)
                with _ ->
                    ())
        with _ ->
            ()
