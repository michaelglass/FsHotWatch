/// CTRF (Common Test Report Format) — the machine-readable test report.
///
/// fshw's runners (xUnit.v3 via Microsoft.Testing.Platform) emit CTRF when asked with
/// `--report-ctrf`, and fshw reads it back as the AUTHORITATIVE pass/fail verdict (the
/// process exit code is only a tie-break — see `TestPrunePlugin.classifyTestOutcome`).
///
/// `trySummary` is the one summary reader: the verdict layer, the flakiness recorder
/// and the verdict file all read the same block through it, so they cannot disagree
/// about what a report says. Reports are retained on disk (bounded, newest-per-run) so
/// a consumer can be pointed at them.
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

let private trySummaryNode (root: JsonNode) : Summary option =
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

let private requiredCounter (summary: JsonNode) (name: string) : Result<int, string> =
    match summary.[name] with
    | null -> Error $"CTRF summary is missing required '{name}' counter"
    | value ->
        try
            let count = value.GetValue<decimal>()

            if count < 0M || Decimal.Truncate(count) <> count || count > decimal Int32.MaxValue then
                Error $"CTRF summary '{name}' counter must be a nonnegative integer"
            else
                Ok(int count)
        with
        | :? InvalidOperationException
        | :? FormatException
        | :? OverflowException -> Error $"CTRF summary '{name}' counter must be a nonnegative integer"

/// A row can support a clean summary only when it explicitly says that its test was
/// passed or skip-like. Red reports intentionally do not use rows as evidence because
/// MTP can omit raw-exception rows, but a clean row claiming failure or no status is a
/// direct contradiction that must not authorize a pass.
let private hasCleanStatus (node: JsonNode) =
    match node with
    | :? JsonObject as entry ->
        match entry.["status"] with
        | null -> false
        | status ->
            try
                match status.GetValue<string>() with
                | "passed"
                | "pending"
                | "skipped" -> true
                | _ -> false
            with _ ->
                false
    | _ -> false

let private cleanStatusCounts (tests: JsonArray) : int * int * int =
    tests
    |> Seq.fold
        (fun (passed, pending, skipped) row ->
            match row with
            | :? JsonObject as entry ->
                match entry.["status"] with
                | null -> passed, pending, skipped
                | status ->
                    try
                        match status.GetValue<string>() with
                        | "passed" -> passed + 1, pending, skipped
                        | "pending" -> passed, pending + 1, skipped
                        | "skipped" -> passed, pending, skipped + 1
                        | _ -> passed, pending, skipped
                    with _ ->
                        passed, pending, skipped
            | _ -> passed, pending, skipped)
        (0, 0, 0)

let private tryVerdictSummaryNode (root: JsonNode) : Result<Summary * int, string> =
    let summary =
        match root.["results"] with
        | null -> root.["summary"]
        | results -> results.["summary"]

    match summary with
    | null -> Error "CTRF report has no usable summary"
    | summary ->
        requiredCounter summary "tests"
        |> Result.bind (fun total ->
            requiredCounter summary "passed"
            |> Result.bind (fun passed ->
                requiredCounter summary "failed"
                |> Result.bind (fun failed ->
                    requiredCounter summary "pending"
                    |> Result.bind (fun pending ->
                        requiredCounter summary "skipped"
                        |> Result.bind (fun skipped ->
                            requiredCounter summary "other"
                            |> Result.map (fun other ->
                                { Total = total
                                  Passed = passed
                                  Failed = failed
                                  Skipped = skipped
                                  Other = other },
                                pending))))))

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
        | root -> trySummaryNode root
    with
    | :? JsonException
    | :? InvalidOperationException -> None

/// Parse the CTRF evidence used to decide a test run's verdict. The summary remains
/// authoritative for red reports because MTP can omit a raw-exception row. A clean
/// summary has no such explanation, however, so its declared total must reconcile
/// with `results.tests`; otherwise a partial flush could falsely turn a run green.
let tryVerdictSummary (json: string) : Result<Summary, string> =
    try
        match JsonNode.Parse(json) with
        | null -> Error "CTRF report is empty"
        | root ->
            match tryVerdictSummaryNode root with
            | Error reason -> Error reason
            | Ok(summary, summaryPending) ->
                let entries =
                    match root.["results"] with
                    | null -> root.["tests"]
                    | results -> results.["tests"]

                match entries with
                | :? JsonArray as tests when summary.Failed = 0 && summary.Other = 0 && tests.Count <> summary.Total ->
                    Error $"CTRF summary says {summary.Total} test(s), but results.tests lists {tests.Count}"
                | :? JsonArray as tests when
                    summary.Failed = 0
                    && summary.Other = 0
                    && (tests |> Seq.exists (fun row -> not (hasCleanStatus row)))
                    ->
                    Error "CTRF clean summary has a test row without a clean status"
                | :? JsonArray as tests when summary.Failed = 0 && summary.Other = 0 ->
                    let passed, pending, skipped = cleanStatusCounts tests

                    // `Summary` intentionally omits a public Pending field, but verdict
                    // parsing retains the declared counter long enough to reconcile all
                    // three clean statuses exactly against their rows.
                    if
                        passed <> summary.Passed
                        || pending <> summaryPending
                        || skipped <> summary.Skipped
                    then
                        Error
                            $"CTRF clean summary counters do not match results.tests (summary passed/pending/skipped: {summary.Passed}/{summaryPending}/{summary.Skipped}; rows: {passed}/{pending}/{skipped})"
                    else
                        Ok summary
                | :? JsonArray -> Ok summary
                | _ -> Error "CTRF report has no results.tests array"
    with
    | :? JsonException
    | :? FormatException
    | :? InvalidOperationException -> Error "CTRF report is not valid JSON"

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
let tryReadReport (runId: string) (path: string) : Report option = tryReadReportWith trySummary runId path

/// Read one report only when it is coherent enough to be verdict evidence. This is
/// deliberately narrower than `tryReadReport`: red reports may have a raw-exception
/// row omitted by MTP, but a clean report must reconcile its summary with its rows.
let tryReadVerdictReport (runId: string) (path: string) : Report option =
    tryReadReportWith (fun contents -> tryVerdictSummary contents |> Result.toOption) runId path

/// Did this run happen at all? The run-dir is created before anything executes, so its
/// existence records that a run took place, whether or not it produced a report.
let runExists (repoRoot: string) (runId: Guid) : bool = Directory.Exists(runDir repoRoot runId)

/// The reports THIS RUN produced — the files in the run's own directory. An empty list
/// from an existing run-dir means the run executed no tests.
let reportsForRun (repoRoot: string) (runId: Guid) : Report list =
    let dir = runDir repoRoot runId

    if not (Directory.Exists dir) then
        []
    else
        try
            Directory.GetFiles(dir, "*" + ReportSuffix)
            |> Array.toList
            |> List.choose (tryReadReport (runId.ToString("N")))
            |> List.sortBy (fun r -> r.Project)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> []

/// The reports from this run that can support an emitted pass verdict. Invalid clean
/// reports remain on disk for diagnostics, but do not contribute counts to a response.
let verdictReportsForRun (repoRoot: string) (runId: Guid) : Report list =
    let dir = runDir repoRoot runId

    if not (Directory.Exists dir) then
        []
    else
        try
            Directory.GetFiles(dir, "*" + ReportSuffix)
            |> Array.toList
            |> List.choose (tryReadVerdictReport (runId.ToString("N")))
            |> List.sortBy (fun r -> r.Project)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> []

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
