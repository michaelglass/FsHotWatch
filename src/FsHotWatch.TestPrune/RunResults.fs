/// What a finished run's results say: whether it verified, whether every project matched nothing, the reply a run-tests request gets, and what a run covered.
module FsHotWatch.TestPrune.RunResults

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs

/// What a run actually VERIFIED.
///
/// Three cases rather than one case with a flag, because they carry different EVIDENCE.
/// `AllZeroMatch` knows a filter ran against discovered tests and how many projects it
/// was applied to, so remediation can be specific. `NoProjectsSelected` has no
/// discovered names at all — nothing ran to discover them — so the only honest report is
/// that the scope was empty.
///
/// Takes the RESULT MAP, not `TestResults`, so the cache key (which holds a
/// `TestRunCompleted`) can call it instead of open-coding the fold. The derivation lives
/// in core (`RunVerification.ofResults`) because the CLI needs the same tokens and a
/// second copy is how the two ends drift; this alias just reads better at TestPrune's
/// call sites and is what the analyzer allow-list names.
let internal verificationOf (results: Map<string, TestResult>) : RunVerification = RunVerification.ofResults results

/// Refine the result-shaped receipt with the configured-suite boundary. An unfiltered
/// result map can only mean FullSuite when it names every configured project; manual
/// `--project` runs are unfiltered inside the selected projects but partial overall.
let internal verificationWithin (configuredProjects: Set<string>) (results: Map<string, TestResult>) =
    match verificationOf results with
    | Ran FullSuite when results |> Map.keys |> Set.ofSeq = configuredProjects -> Ran FullSuite
    | Ran FullSuite -> Ran RunScope.Partial
    | other -> other

/// "Projects ran, and every one of them matched nothing." The predicate the aggregators
/// want; `NoProjectsSelected` is deliberately NOT this, because no project is not the
/// same claim as every project matching nothing.
let internal allZeroMatchOf (results: Map<string, TestResult>) : bool =
    match verificationOf results with
    | AllZeroMatch _ -> true
    | NoProjectsSelected
    | NothingExecuted
    | Ran _ -> false

/// Retained for the existing wire field, which older CLIs still read. Prefer
/// `verificationOf`: this cannot distinguish an empty run from a real one.
let internal allZeroMatch (results: TestResults) : bool = allZeroMatchOf results.Results

/// `activeFilter` is the raw `--filter-*` passthrough the run was launched with, when
/// there was one. It rides on the wire so a refusal can ECHO IT: "no tests matched the
/// filter" without saying WHICH filter leaves the reader to guess between a typo, a
/// renamed class, and a filter aimed at a project that does not contain it — the exact
/// three-way ambiguity that cost one investigation a wrong conclusion.
/// `None` for an unfiltered run, and for the `test-results`
/// command, which reads a stored result set that does not remember what launched it.
///
/// `runReports` is the per-project CTRF SUMMARY this run produced, keyed by project.
/// Its whole job is criterion 3: the CLI must state total /
/// succeeded / failed for each project it ran, because a missing summary line is the
/// tell that separates a real pass from a vacuous one, and it used to live only in
/// `daemon.log`. A project ABSENT from this map produced no readable report — an
/// unknown runner we never asked one from, or a host that aborted before flushing —
/// and the consumer says so rather than printing zeros, which would read as a suite
/// that ran cleanly.
let internal formatTestResultsJson
    (activeFilter: string option)
    (runReports: Map<string, FsHotWatch.Ctrf.Summary>)
    (results: TestResults)
    =
    let projects =
        results.Results
        |> Map.toList
        |> List.map (fun (name, result) ->
            let (status, output) =
                match result with
                // A zero-match-under-filter result gets a DISTINCT status so a consumer
                // can tell "ran, all green" from "matched nothing". The CLI's `coverage`
                // fallback for older daemons parses these per-project statuses, so
                // renaming any of these wire strings breaks it.
                | TestsNoMatch(o, _) -> ("no-tests-matched", o)
                | TestsPassed(o, _, _) -> ("passed", o)
                | TestsFailed(o, _, _) -> ("failed", o)
                | TestsTimedOut(o, _, _, _) -> ("timed-out", o)
                | TestsDeferred reason -> ("deferred", reason)
                | TestsErrored reason -> ("errored", reason)

            // `null`, not zeros. `total: 0, failed: 0` reads as "this suite ran
            // cleanly", so manufacturing counts from an absent report is the very
            // vacuous green this `null` avoids (the same rule `Verdict.parseSuites`
            // holds for the verdict file).
            let counts: obj =
                match Map.tryFind name runReports with
                | Some(summary: FsHotWatch.Ctrf.Summary) ->
                    box
                        {| total = summary.Total
                           succeeded = summary.Passed
                           failed = summary.Failed
                           skipped = summary.Skipped
                           other = summary.Other |}
                | None -> null

            {| project = name
               status = status
               output = truncateOutput 200 output
               elapsedMs = (TestResult.elapsed result).TotalMilliseconds
               counts = counts |})

    let verification = verificationOf results.Results

    JsonSerializer.Serialize(
        {| elapsed = $"%.1f{results.Elapsed.TotalSeconds}s"
           // The filter this run was launched with, or `null` when there was none.
           // Written so a "matched nothing" refusal can quote it back; a consumer that
           // does not know the field simply says less.
           filter = Option.toObj activeFilter
           // `noTestsMatched` is true iff EVERY project matched zero tests under
           // the active filter. RETAINED for CLIs older than `coverage`; it
           // cannot express "no project was selected", which is why the field
           // below exists.
           noTestsMatched = allZeroMatch results
           // The run-level answer to "did this verify anything?", stated by the
           // producer rather than reconstructed by the consumer from array
           // lengths. A consumer that does not know this field falls back to the
           // counts — an ABSENT field must never be read as "ran".
           coverage = RunVerification.token verification
           projects = projects |}
    )

/// Default slot-wait budget (ms) for the manual `run-tests` command when the
/// payload carries no `waitSec` — generous so a long `tests.beforeRun` chain
/// (90 s+) held by a prior in-flight run can't make an explicit `test-rerun` give
/// up and report `busy` before the slot frees. The
/// CLI always sends `waitSec` (default `DefaultTestRerunWaitSec`); this fallback
/// covers a missing/malformed field (an older CLI or hand-crafted payload).
[<Literal>]
let internal DefaultRunTestsWaitMs = 600_000

/// Read the `waitSec` slot-wait budget (seconds) from a `run-tests` argument
/// JSON object and convert it to milliseconds, falling back to `fallbackMs` when
/// the argument is absent, unparseable, or lacks a numeric `waitSec` field. Pure
/// so the wait budget is unit-testable without round-tripping the IPC command.
let internal parseRunTestsWaitMs (argStr: string) (fallbackMs: int) : int =
    try
        use doc = JsonDocument.Parse(argStr)

        match doc.RootElement.TryGetProperty("waitSec") with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, secs when secs > 0 -> secs * 1000
            | _ -> fallbackMs
        | _ -> fallbackMs
    with _ ->
        fallbackMs

/// Every configured test project, each with an EMPTY affected-class list — which
/// `buildFilterArgs` reads as "no filter, run this project in full". This is the
/// unfiltered scope: the whole suite, no selection, nothing chosen.
///
/// Used by the two callers that may not trust a selection: `fshw confirm`
/// (impact filtering is a latency optimization for the inner loop,
/// never the basis of a correctness claim) and the unanalysable-file fallback
/// (a file the analyser cannot read has no symbols to select by).
let internal fullSuiteProjects (configs: TestConfig list) : Set<string> =
    configs |> List.map (fun c -> c.Project) |> Set.ofList

/// Why a launch will cover every configured project, if it does: the first widening, in
/// `FullSuiteCause` precedence order, that applies to it.
///
/// `coarseGaps` and `evidenceGapIsWhole` are the two widenings that run every project only
/// when they apply: a coarse gap always widens to every project
/// (`coarseFallbackProjects`), while the evidence gap widens to the projects it names.
let internal fullSuiteCauseOf
    (requested: bool)
    (ledgerUnreadable: bool)
    (baselineInvalid: bool)
    (coarseGaps: bool)
    (evidenceGapIsWhole: bool)
    : FullSuiteCause =
    if requested then FullSuiteCause.Requested
    elif ledgerUnreadable then FullSuiteCause.LedgerUnreadable
    elif baselineInvalid then FullSuiteCause.NoFullSuiteBaseline
    elif coarseGaps then FullSuiteCause.CoarseFallback
    elif evidenceGapIsWhole then FullSuiteCause.EvidenceGap
    else FullSuiteCause.SelectionReachedEveryProject

/// The coarse fallback for files the symbol analyser could not read.
///
/// A file whose analysis FAILED contributes no symbols, so the symbol diff finds nothing
/// changed in it and the selection it earns is EMPTY — an edit to it would select zero
/// tests and the check would go green having run nothing relevant. An unanalysable file
/// means "I cannot tell you what is affected", not "nothing is affected", so the answer
/// is to run EVERY test project: a superset is safe where a gap is not. Same rule
/// `EdgeEmission.resolveTargets` follows for an unresolvable seed.
///
/// Returned as force-run projects: a project present with an empty class list runs IN
/// FULL, and a non-empty force-run set also disables the zero-affected skip gate, so an
/// unanalysable file cannot reach the "0 affected, green, 0 ran" verdict either.
let internal coarseFallbackProjects
    (configs: TestConfig list)
    (unanalyzableFiles: Set<string>)
    (fanout: Set<string>)
    : Set<string> =
    if Set.isEmpty unanalyzableFiles then
        fanout
    else
        Set.union fanout (fullSuiteProjects configs)


/// The single answer to "what did this run actually cover?", per project and per suite.
/// Public so the verdict writer asks it rather than keeping a parallel notion of scope.
module RunCoverage =

    /// Nothing was executed, so nothing may be cleared. The verdict of an aborted
    /// run, and of the zero-affected skip (which runs no tests at all).
    let none: RunCoverage = Map.empty

    /// The projects this run executed at all (in full, or a class subset of).
    let coveredProjects (coverage: RunCoverage) : Set<string> = coverage |> Map.keys |> Set.ofSeq

    /// Did this run execute EVERY configured project, each in FULL? The only scope
    /// from which a whole-suite claim can be made — and the question `fshw confirm` is
    /// really asking. A run that filtered ANY project, or skipped one, covered less
    /// than the suite, whatever its result counts say. An empty project list is not a
    /// covered suite (there is no evidence in a run of nothing).
    let coversWholeSuite (projects: string list) (coverage: RunCoverage) : bool =
        not projects.IsEmpty
        && projects
           |> List.forall (fun p ->
               match Map.tryFind p coverage with
               | Some CoveredWholeProject -> true
               | Some(CoveredClasses _)
               | None -> false)

    /// Does this run's evidence reach the given red? `cls = None` is a
    /// PROJECT-level red (unparseable failure output, a timeout, an errored or
    /// deferred project): no class-filtered run can speak for it — only a run that
    /// executed the whole project can.
    let covers (project: string) (cls: string option) (coverage: RunCoverage) : bool =
        match Map.tryFind project coverage, cls with
        | None, _ -> false
        | Some CoveredWholeProject, _ -> true
        | Some(CoveredClasses _), None -> false
        | Some(CoveredClasses classes), Some c -> Set.contains c classes

    /// Derive the coverage of a completed run: what it was LAUNCHED against
    /// (`selection`), intersected with what it actually EXECUTED — the results, plus
    /// the run's own per-test report (`passedClasses`, see `passedClassesOfRun`).
    ///
    /// Per project, in order:
    ///   * no result / deferred / errored / zero-match-under-filter → NO coverage.
    ///     Nothing ran, so nothing is vindicated (a deferred project's `wasFiltered`
    ///     is `true` by convention and its output is empty — neither is evidence).
    ///   * `wasFiltered = false` → the runner executed the project with no filter,
    ///     whatever the selection asked for (a project with selected classes but no
    ///     `filterTemplate` runs in FULL) → `CoveredWholeProject`. The RESULT, not
    ///     the request, is the receipt.
    ///   * absent from the selection → NO coverage, unconditionally. The project was
    ///     never LAUNCHED (impact-skipped, recorded as a filtered pass), so no file on
    ///     disk may speak for it — this is the laundering guard and it is
    ///     checked BEFORE any report evidence.
    ///   * `wasFiltered = true` and the selection named classes → `CoveredClasses`.
    ///   * `wasFiltered = true` otherwise → the `run-tests --filter <raw>` passthrough:
    ///     an arbitrary filter string whose reach the LAUNCH REQUEST cannot express
    ///     (every project goes down as `ProjectInFull`). Ask the run's own evidence
    ///     instead — the classes its CTRF report shows actually RAN AND PASSED.
    ///     Otherwise `test-rerun --filter-class X` could re-run X,
    ///     pass, and still leave X's red standing forever. Fail-closed wherever the
    ///     report is absent, unreadable or incomplete (`passedClassesOfReport`).
    ///   * a TIMED-OUT project is excluded from the evidence path: a report flushed by
    ///     a process we killed is not a receipt for anything.
    let ofRun
        (selection: Map<string, ProjectSelection>)
        (results: Map<string, TestResult>)
        (passedClasses: Map<string, Set<string>>)
        : RunCoverage =
        results
        |> Map.toList
        |> List.choose (fun (project, result) ->
            // Exhaustive by construction — see `TestResult.executedTests`. Do not
            // reintroduce a wildcard here: a new non-executing case would fall through
            // and be counted as having run.
            let ran = TestResult.executedTests result

            let fromEvidence () =
                if TestResult.isTimedOut result then
                    None
                else
                    match Map.tryFind project passedClasses with
                    | Some classes when not (Set.isEmpty classes) -> Some(project, CoveredClasses classes)
                    | _ -> None

            if not ran then
                None
            elif not (TestResult.wasFiltered result) then
                Some(project, CoveredWholeProject)
            else
                match Map.tryFind project selection with
                | None -> None
                | Some(ProjectClasses classes) when not (Set.isEmpty classes) -> Some(project, CoveredClasses classes)
                // An exclude filter (`checkSkipClasses`) names what did NOT run, so the
                // run's own report says what did.
                | Some(ProjectClasses _)
                | Some(ProjectExcept _)
                | Some ProjectInFull -> fromEvidence ())
        |> Map.ofList

/// The SCOPE `fshw confirm` reads, as a pure PROJECTION of `RunCoverage`.
///
/// Deliberately not an independent derivation: a second answer to "what did this run
/// cover?" can disagree with the one the ledger clears by, and `confirm` would then go
/// green on a scope the ledger never granted.
type internal ScopeReport =
    /// Every configured project executed, each in FULL. The only scope a whole-suite
    /// claim can be made from.
    | ScopeFull of projects: int
    /// Some project ran, but not the whole suite in full.
    | ScopeFiltered of ran: int * total: int
    /// NOTHING executed. Not a scope — an absence of evidence, which the CLI reads as
    /// `NoTestsRun` and refuses to call green in either mode.
    | ScopeNone of total: int

let internal scopeOf (projects: string list) (coverage: RunCoverage) : ScopeReport =
    let covered = RunCoverage.coveredProjects coverage
    let total = List.length projects

    if RunCoverage.coversWholeSuite projects coverage then
        ScopeFull total
    elif Set.isEmpty covered then
        ScopeNone total
    else
        ScopeFiltered(Set.count covered, total)
