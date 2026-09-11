/// CTRF (Common Test Report Format) — the machine-readable test report.
///
/// fshw's runners (xUnit.v3 via Microsoft.Testing.Platform) emit CTRF when asked with
/// `--report-ctrf`, and fshw reads it back as the AUTHORITATIVE pass/fail verdict (the
/// process exit code is only a tie-break — see `TestPrunePlugin.classifyTestOutcome`).
///
/// `trySummary` reads diagnostic counts; `tryVerdictReport` additionally validates
/// the evidence before a caller may treat a clean summary as a verdict. Reports are
/// retained on disk (bounded, newest-per-run) so a consumer can be pointed at them.
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

/// Evidence that a report has valid counters and that a clean summary agrees
/// with its actual rows. Only the parser can construct this proof. It does not
/// establish run ownership, scope, or that a nonempty suite executed.
type VerdictReport = private VerdictReport of Summary

module VerdictReport =
    /// Read the validated summary without granting callers a proof constructor.
    let summary (VerdictReport value) = value

let private requiredCounter (summary: JsonNode) (name: string) : Result<int, string> =
    match summary.[name] with
    | null -> Error $"CTRF summary is missing required '{name}' counter"
    | value ->
        try
            let count = value.GetValue<decimal>()

            if count < 0M || Decimal.Truncate count <> count || count > decimal Int32.MaxValue then
                Error $"CTRF summary '{name}' counter must be a nonnegative integer"
            else
                Ok(int count)
        with
        | :? InvalidOperationException
        | :? FormatException
        | :? OverflowException -> Error $"CTRF summary '{name}' counter must be a nonnegative integer"

let private verdictCounters (summary: JsonNode) =
    let counter name next = requiredCounter summary name |> Result.bind next

    counter "tests" (fun total ->
        counter "passed" (fun passed ->
            counter "failed" (fun failed ->
                counter "pending" (fun pending ->
                    counter "skipped" (fun skipped ->
                        requiredCounter summary "other"
                        |> Result.map (fun other ->
                            { Total = total
                              Passed = passed
                              Failed = failed
                              Skipped = skipped
                              Other = other }, pending))))))

let private cleanStatus (row: JsonNode) =
    match row with
    | :? JsonObject as entry ->
        match entry.["status"] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, ("passed" | "pending" | "skipped" as status) -> Some status
            | _ -> None
        | _ -> None
    | _ -> None

let private reconcileRows (summary: Summary) pending (tests: JsonArray) =
    // MTP's raw-exception reports omit rows and count `other` inside `failed`.
    // Their red summary remains authoritative; clean reports have no such excuse.
    if summary.Failed > 0 || summary.Other > 0 then
        Ok(VerdictReport summary)
    elif tests.Count <> summary.Total then
        Error $"CTRF summary says {summary.Total} test(s), but results.tests lists {tests.Count}"
    else
        let statuses = tests |> Seq.map cleanStatus |> Seq.toList

        if statuses |> List.exists Option.isNone then
            Error "CTRF clean summary has a test row without a clean status"
        else
            let count status = statuses |> List.filter ((=) (Some status)) |> List.length

            if count "passed" <> summary.Passed || count "pending" <> pending || count "skipped" <> summary.Skipped then
                Error "CTRF clean summary counters do not match results.tests"
            else
                Ok(VerdictReport summary)

/// Validate requested report evidence before allowing a clean summary to override
/// a runner's exit code. Diagnostic summary readers deliberately remain permissive.
let tryVerdictReport (json: string) : Result<VerdictReport, string> =
    try
        match JsonNode.Parse json with
        | null -> Error "CTRF report is empty"
        | root ->
            let results =
                match root.["results"] with
                | null -> root
                | nested -> nested

            match results.["summary"], results.["tests"] with
            | (:? JsonObject as summary), (:? JsonArray as tests) ->
                verdictCounters summary
                |> Result.bind (fun (counts, pending) -> reconcileRows counts pending tests)
            | _ -> Error "CTRF report requires a summary object and tests array"
    with
    | :? JsonException
    | :? InvalidOperationException -> Error "CTRF report is not valid JSON report structure"

/// Read one report file. `None` for anything that is not a well-formed fshw CTRF
/// report — unreadable, unparseable, or carrying no summary. A report we cannot
/// read is not evidence, and is never counted as a zero-failure pass.
let private tryReadReportWith parse (runId: string) (path: string) : Report option =
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

/// Read retained diagnostic counts, without treating them as verdict evidence.
let tryReadReport runId path = tryReadReportWith trySummary runId path

/// Read a requested report only when its summary and rows can support a verdict.
let tryReadVerdictReport runId path =
    tryReadReportWith (tryVerdictReport >> Result.toOption >> Option.map VerdictReport.summary) runId path

/// Did this run happen at all? The run-dir is created before anything executes, so its
/// existence records that a run took place, whether or not it produced a report.
let runExists (repoRoot: string) (runId: Guid) : bool = Directory.Exists(runDir repoRoot runId)

/// The reports THIS RUN produced — the files in the run's own directory. An empty list
/// from an existing run-dir means the run executed no tests.
let private reportsForRunWith readReport (repoRoot: string) (runId: Guid) : Report list =
    let dir = runDir repoRoot runId

    if not (Directory.Exists dir) then
        []
    else
        try
            Directory.GetFiles(dir, "*" + ReportSuffix)
            |> Array.toList
            |> List.choose (readReport (runId.ToString("N")))
            |> List.sortBy (fun r -> r.Project)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> []

/// Retained reports for diagnostics, including summaries that are not verdict proof.
let reportsForRun repoRoot runId = reportsForRunWith tryReadReport repoRoot runId

/// Only coherent reports may contribute counts to a durable suite verdict.
let verdictReportsForRun repoRoot runId = reportsForRunWith tryReadVerdictReport repoRoot runId

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
