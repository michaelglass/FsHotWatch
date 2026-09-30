module FsHotWatch.Coverage.Judgement

open FsHotWatch.Events
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

/// Where a check reads a run's Cobertura reports.
[<RequireQualifiedAccess>]
type CoverageReports =
    /// Every `coverage.cobertura.xml` under the directory, whatever the run's scope.
    | SearchUnder of dir: string
    /// Exactly these files: a full-suite run's own runner output. Only a full-suite run
    /// writes them, so no other run is judged by them.
    | Named of paths: string list

/// The floors a run's coverage fell below, as the `coverageratchet check` CLI reads
/// them: per-file percentage floors and absolute count floors.
[<NoComparison; NoEquality>]
type FloorFailures =
    {
        /// Files below a line or branch percentage floor.
        Files: FileResult list
        /// Files below a covered-lines or covered-branches count floor.
        Counts: CountResult list
    }

/// The gated coverage verdict for a cycle, after applying the impact-filter
/// guard. Distinct from the raw floor check so the message that drives the
/// plugin's pass/fail status carries the already-gated decision.
[<NoComparison; NoEquality>]
type CoverageVerdict =
    /// Every evaluated file met its floors (or no coverage XML was produced).
    | Passed
    /// A full-suite run found real shortfalls — these GATE (exit non-zero).
    | Failed of FloorFailures
    /// An impact-filtered run produced shortfalls, but it did NOT run every
    /// project's tests this cycle, so an un-run source file reads `0.0%`
    /// indistinguishably from a genuine zero. We do NOT gate on a filtered
    /// run (raise-only); instead we surface a loud notice naming the count so
    /// the shortfall is visible without a false red.
    | NotGatedFiltered of belowFloorCount: int
    /// The floor file's reader options could not be read (e.g. an invalid
    /// `includedExtensions` list), or a named report was not written by the run it is
    /// judged for, so nothing was judged. Gates whatever the run's scope: a check that
    /// cannot read what it judges has verified nothing.
    | Unreadable of reason: string

/// What a completed run's coverage comes to.
[<NoComparison; NoEquality>]
type CoverageJudgement =
    /// The run's reports were judged against the floors.
    | Judged of CoverageVerdict
    /// The run is not one the configured reports describe, so nothing was judged.
    | NotJudged of reason: string

/// Per-file rendering of floor failures.
[<RequireQualifiedAccess>]
module FloorFailures =

    /// The ratchet's per-file error text for a file below its percentage floors.
    let private percentDetail (r: FileResult) =
        [ if not (FileResult.linePassed r) then
              $"line=%.1f{r.File.LinePct}%% < min %.1f{r.LineThreshold}%%"
          if not (FileResult.branchPassed r) then
              $"branch=%.1f{r.File.BranchPct}%% < min %.1f{r.BranchThreshold}%%" ]

    /// The ratchet's per-file error text for a file below its count floors.
    let private countDetail (r: CountResult) =
        [ if not (CountResult.linesPassed r) then
              $"covered lines %d{r.File.LinesCovered} < %d{r.Floor.CoveredLines}"
          if not (CountResult.branchesPassed r) then
              $"covered branches %d{r.File.BranchesCovered} < %d{r.Floor.CoveredBranches}" ]

    /// Each failing file once, with every floor it fell below joined into one detail:
    /// percentage floors first, then count floors, as the ratchet reports them.
    let details (failures: FloorFailures) : (string * string) list =
        (failures.Files |> List.map (fun r -> r.File.FileName, percentDetail r))
        @ (failures.Counts |> List.map (fun r -> r.File.FileName, countDetail r))
        |> List.groupBy fst
        |> List.map (fun (file, parts) -> file, parts |> List.collect snd |> String.concat ", ")

    /// How many files `details` names, and the first few of them, for a status line that
    /// says which files failed rather than only how many.
    let summary (details: (string * string) list) : string =
        let shown = 5
        let names = details |> List.truncate shown |> List.map fst |> String.concat ", "

        let more =
            if details.Length > shown then
                $", +%d{details.Length - shown} more"
            else
                ""

        $"%d{details.Length} file(s) below threshold: %s{names}%s{more}"

/// The floor check the `coverageratchet check` CLI makes: percentage floors, then count
/// floors. `None` when every floor holds.
let internal floorCheck (config: Config) (coverage: FileCoverage list) : FloorFailures option =
    let files =
        match check config coverage with
        | AllPassed -> []
        | SomeFailed results -> results

    let counts =
        match checkCounts config coverage with
        | CountsAllPassed -> []
        | CountsFailed results -> results

    if List.isEmpty files && List.isEmpty counts then
        None
    else
        Some { Files = files; Counts = counts }

/// Decide the gated verdict from a raw floor check and what the run established. Pure,
/// so the gating policy is unit-testable without spinning a daemon.
///
/// Takes the scope a run PROVED, not a bool. `FullSuite` gates normally; `Partial`
/// downgrades a shortfall to a non-gating notice, because un-run files cannot be
/// distinguished from genuine zeros. A run that executed nothing has no `RunScope`
/// to pass, so it cannot reach here at all and cannot be mistaken for a filtered
/// one — the caller has already had to decide what to do about it.
let internal gateVerdict (scope: RunScope) (failures: FloorFailures option) : CoverageVerdict =
    match failures, scope with
    | None, _ -> Passed
    | Some failures, FullSuite -> Failed failures
    | Some failures, Partial ->
        let files =
            (failures.Files |> List.map _.File.FileName)
            @ (failures.Counts |> List.map _.File.FileName)
            |> List.distinct

        NotGatedFiltered files.Length

/// Read the run's reports the way the `coverageratchet` CLI does: with the reader
/// options the floor file asks for (`loadReaderOptions`), which root the directory
/// rules at the floor file's directory. The parameterless `parseFiles` matches rules
/// against the whole absolute path, so a checkout under a directory named `tests`,
/// `test` or `obj` would read nothing, and an `includedExtensions` list in the floor
/// file would be ignored; either way the daemon's verdict would disagree with the
/// CLI's on the same reports.
let internal readCoverage (configPath: string) (xmlPaths: string list) : Result<FileCoverage list, string> =
    loadReaderOptions configPath
    |> Result.map (fun options ->
        (readReports options (xmlPaths |> List.map System.IO.File.ReadAllText)).Lines
        |> buildCoverage)

/// Judge `xmlPaths` against the floor file and gate the result by `scope`.
let private verdictOf (configPath: string) (scope: RunScope) (xmlPaths: string list) : CoverageVerdict =
    match readCoverage configPath xmlPaths with
    | Ok coverage -> gateVerdict scope (floorCheck (loadConfig configPath) coverage)
    | Result.Error reason -> Unreadable reason

/// The reports of `reports` that exist now.
let internal existingReports (reports: CoverageReports) : string list =
    match reports with
    | CoverageReports.SearchUnder dir -> findCoverageFiles dir
    | CoverageReports.Named paths -> paths |> List.filter System.IO.File.Exists

/// Why a run of `scope` is not judged by `reports`, or `None` when it is. Decided
/// from the run's kind alone, before anything is read.
let internal notJudgedReason (reports: CoverageReports) (scope: RunScope) : string option =
    match reports, scope with
    | CoverageReports.Named _, Partial ->
        Some "an impact-filtered run does not write the named coverage reports, which a full-suite run does"
    | CoverageReports.Named _, FullSuite
    | CoverageReports.SearchUnder _, _ -> None

/// Whether a run's coverage is judged, and against what, before any report is read.
/// `Named` reports are a full-suite run's own output: a run that did not run the full
/// suite did not write them, so it is not judged by them; a full-suite run is, and a
/// named report it did not write (missing, or older than `runStartedAt`) is
/// `Unreadable`, never a verdict on an earlier run's output. `found` is what a
/// `SearchUnder` check found on disk.
let internal judge
    (configPath: string)
    (reports: CoverageReports)
    (scope: RunScope)
    (runStartedAt: System.DateTime)
    (found: string list)
    : CoverageJudgement =
    match notJudgedReason reports scope, reports with
    | Some reason, _ -> NotJudged reason
    | None, CoverageReports.Named paths ->
        let notWritten =
            paths
            |> List.filter (fun path ->
                not (System.IO.File.Exists path)
                || System.IO.File.GetLastWriteTimeUtc path < runStartedAt)

        if List.isEmpty notWritten then
            Judged(verdictOf configPath scope paths)
        else
            let listed = String.concat ", " notWritten

            Judged(
                Unreadable $"coverage report(s) not written by this run (missing, or older than its start): %s{listed}"
            )
    | None, CoverageReports.SearchUnder _ ->
        if List.isEmpty found then
            Judged Passed
        else
            Judged(verdictOf configPath scope found)
