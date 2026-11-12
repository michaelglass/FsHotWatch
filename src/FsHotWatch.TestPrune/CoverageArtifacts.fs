/// Runtime coverage artifacts a test run writes: their paths, the arguments that ask a runner for them, whether a run wrote them, and how their ingestion failed.
module FsHotWatch.TestPrune.CoverageArtifacts

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

/// Per-project raw cobertura written by a FULL (unfiltered) test run.
[<Literal>]
let BaselineName = "coverage.baseline.cobertura.xml"

/// Per-project raw cobertura written by an impact-FILTERED test run.
[<Literal>]
let PartialName = "coverage.partial.cobertura.xml"

/// The single shared cobertura emitted from the full TestPrune DB and consumed
/// by downstream gating (coverageratchet).
[<Literal>]
let CoberturaName = "coverage.cobertura.xml"

/// `tests.projects[].runtimeSelection` (AUTOMATION-1645): whether what a project's runtime
/// coverage attributes to a changed file decides what runs.
[<RequireQualifiedAccess>]
type RuntimeSelectionMode =
    /// The project runs in full for every changed file its runtime coverage reached, owed
    /// until a run of it passes. The default.
    | Decide
    /// Coverage is still collected for impact, and what it would add is computed and
    /// logged per launch (`RuntimeShadowLine`). It never enters the selection, so it is
    /// never owed.
    | Shadow

/// Per-project raw-coverage artifact paths + the command-line template used to
/// produce them. The runner writes Cobertura XML to `Baseline` (full run) or
/// `Partial` (impact-filtered run); the plugin ingests whichever this run wrote
/// into the TestPrune DB and emits the whole DB once to `Cobertura`. Callers
/// (DaemonConfig) decide the directory layout and the arg template; the plugin
/// treats the paths as opaque absolute paths and substitutes `{output}` in
/// `ArgsTemplate`.
///
/// The file format is Cobertura regardless of `ArgsTemplate` — the template
/// is responsible for telling its runner to write Cobertura to `{output}`.
/// For Microsoft Testing Platform, use `DefaultCoverageArgsTemplate`; for
/// other runners (coverlet.collector, AltCover, OpenCover) supply your own.
type CoveragePaths =
    {
        Baseline: string
        Partial: string
        /// The SHARED cobertura the plugin emits the whole DB to — set identically
        /// for every project by DaemonConfig (the DB unions coverage across them),
        /// so the daemon writes one run-wide artifact, not one per project.
        Cobertura: string
        /// Whether this project's measurements are eligible for the consumer
        /// coverage ratchet. Impact collection is independent: a project may
        /// populate TestPrune while remaining outside the consumer's gate.
        IncludeInRatchet: bool
        ArgsTemplate: string
        /// Whether this project's runtime coverage decides what runs, or is only logged.
        RuntimeSelection: RuntimeSelectionMode
    }

/// One raw coverage artifact produced by one test project. Keeping the project
/// beside the path prevents the collection boundary from erasing provenance
/// before TestPrune gains a project-aware coverage ingest API.
type CoverageInput =
    { Project: string
      RawPath: string
      IncludeInRatchet: bool
      Scope: CoverageRunScope }

let coverageInput (project: string) (includeInRatchet: bool) (rawPath: string) : CoverageInput =
    { Project = project
      RawPath = rawPath
      IncludeInRatchet = includeInRatchet
      Scope =
        if Path.GetFileName(rawPath) = PartialName then
            CoverageRunScope.Partial
        else
            CoverageRunScope.Full }

type CoverageArtifactFingerprint =
    { Length: int64
      LastWriteUtc: DateTime
      Sha256: string }

type CoverageArtifactState =
    | Missing
    | Fingerprinted of CoverageArtifactFingerprint
    | Unreadable of reason: string

type CoverageArtifactLaunch =
    { Project: string
      IncludeInRatchet: bool
      RawPath: string
      Scope: CoverageRunScope
      Before: CoverageArtifactState
      DeletionProven: bool }

type CoverageIngestFailure = { Project: string; Reason: string }

type CoverageReceiptOutcome =
    | CoverageReceiptAccepted of CoverageInput
    | CoverageReceiptAbsent
    | CoverageReceiptFailed of CoverageIngestFailure

let private coverageArtifactState (path: string) : CoverageArtifactState =
    try
        File.GetAttributes path |> ignore
        use stream = File.OpenRead path

        Fingerprinted
            { Length = stream.Length
              LastWriteUtc = File.GetLastWriteTimeUtc path
              Sha256 = Convert.ToHexString(SHA256.HashData stream) }
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> Missing
    | :? IOException as ex -> Unreadable ex.Message
    | :? UnauthorizedAccessException as ex -> Unreadable ex.Message

/// Remove the prior runner artifact before launch. If removal is refused, retain
/// a content fingerprint so a stable stale file can never masquerade as this run's
/// receipt merely because it still exists afterward.
let internal prepareCoverageArtifact
    (project: string)
    (includeInRatchet: bool)
    (wasFiltered: bool)
    (rawPath: string)
    : CoverageArtifactLaunch =
    let before = coverageArtifactState rawPath

    try
        File.Delete rawPath
    with
    | :? IOException
    | :? UnauthorizedAccessException -> ()

    let afterDelete = coverageArtifactState rawPath

    { Project = project
      IncludeInRatchet = includeInRatchet
      RawPath = rawPath
      Scope =
        if wasFiltered then
            CoverageRunScope.Partial
        else
            CoverageRunScope.Full
      Before = before
      DeletionProven = afterDelete = Missing }

/// Whether the artifact now at the launch's path is one the run wrote, rather than one it
/// found there: absent before, changed, or proven deleted at launch.
let internal writtenByRun (launch: CoverageArtifactLaunch) (after: CoverageArtifactState) =
    match launch.Before, after with
    | _, Missing
    | _, Unreadable _ -> false
    | Unreadable _, Fingerprinted _ -> launch.DeletionProven
    | Missing, Fingerprinted _ -> true
    | Fingerprinted before, Fingerprinted after -> launch.DeletionProven || before <> after

/// How a test host that overran its `timeoutSec` is asked to stop before its tree is
/// killed. A coverage run is `dotnet run` → the test platform's controller → the test
/// host; the controller restores the assemblies it instrumented (`X.dll.orig` back over
/// `X.dll`) once the host below it has exited — whether that host stopped or was killed
/// — but not if it is itself signalled first. So SIGTERM goes to the deepest processes
/// only, and the controller and `dotnet run` are left to finish and exit on their own.
/// The grace bounds the whole tree: a host that honours the signal is gone, restored,
/// in well under a second.
let internal TestHostStop: TreeStop =
    { Signal = StopSignal.Terminate
      Order = StopOrder.DeepestFirst
      Grace = TimeSpan.FromSeconds 15.0 }

/// Where a run keeps its own copy of the raw coverage `project` wrote, beside its CTRF
/// report and output log. The per-project baseline and partial files are overwritten by
/// every later run; this copy is not.
let internal runCoveragePath (runDir: string) (project: string) = Ctrf.coverageReportPath runDir project

/// Copy the raw coverage the run wrote for `launch.Project` into the run's directory.
/// Nothing is copied when the run wrote nothing; a failed copy is logged, never fatal.
let internal keepRunCoverage (runDir: string) (launch: CoverageArtifactLaunch) =
    if writtenByRun launch (coverageArtifactState launch.RawPath) then
        try
            File.Copy(launch.RawPath, runCoveragePath runDir launch.Project, true)
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex ->
            Logging.warn
                "test-prune"
                $"%s{launch.Project}: could not keep this run's coverage in %s{runDir}: %s{ex.Message}"

let internal coverageInputFromObservedState
    (verifiedGreen: bool)
    (launch: CoverageArtifactLaunch)
    (after: CoverageArtifactState)
    : CoverageInput option =
    if verifiedGreen && writtenByRun launch after then
        Some
            { Project = launch.Project
              RawPath = launch.RawPath
              IncludeInRatchet = launch.IncludeInRatchet
              Scope = launch.Scope }
    else
        None

let internal coverageReceiptFromObservedState
    (verifiedGreen: bool)
    (launch: CoverageArtifactLaunch)
    (after: CoverageArtifactState)
    : CoverageReceiptOutcome =
    match coverageInputFromObservedState verifiedGreen launch after with
    | Some input -> CoverageReceiptAccepted input
    | None when verifiedGreen && launch.Scope = CoverageRunScope.Full ->
        let reason =
            match after with
            | Missing ->
                $"successful full test run produced no fresh runtime coverage receipt: %s{launch.RawPath} is missing"
            | Unreadable detail ->
                $"successful full test run produced no fresh readable runtime coverage receipt at %s{launch.RawPath}: %s{detail}"
            | Fingerprinted _ ->
                $"successful full test run left an unchanged stale runtime coverage receipt at %s{launch.RawPath}"

        CoverageReceiptFailed
            { Project = launch.Project
              Reason = reason }
    | None -> CoverageReceiptAbsent

/// Turn a launch into an ingest receipt only when the runner finished green and
/// wrote a new artifact. Full receipts are therefore trustworthy replacement
/// baselines; failed, aborted, timed-out and stable-artifact launches contribute
/// no runtime evidence.
let internal coverageInputFromReceipt (verifiedGreen: bool) (launch: CoverageArtifactLaunch) : CoverageInput option =
    coverageInputFromObservedState verifiedGreen launch (coverageArtifactState launch.RawPath)

let internal coverageReceiptFromReceipt
    (verifiedGreen: bool)
    (launch: CoverageArtifactLaunch)
    : CoverageReceiptOutcome =
    coverageReceiptFromObservedState verifiedGreen launch (coverageArtifactState launch.RawPath)

/// Default coverage args template for Microsoft Testing Platform hosts
/// (xUnit v3, MSTest v3 — anything invoked as `dotnet run --project <test>
/// --no-build -- ...`). `{output}` is replaced with the target file path.
[<Literal>]
let DefaultCoverageArgsTemplate =
    "--coverage --coverage-output-format cobertura --coverage-output \"{output}\""

[<Literal>]
let private OutputPlaceholder = "{output}"

/// Substitute `{output}` in `paths.ArgsTemplate` with either Baseline or
/// Partial depending on `wasFiltered`. Creates the output dir if missing.
/// Raises if the template is missing the placeholder, rather than silently
/// emitting args the runner will ignore.
let buildCoverageArgs (paths: CoveragePaths) (wasFiltered: bool) : string =
    let target = if wasFiltered then paths.Partial else paths.Baseline

    let dir = Path.GetDirectoryName(target)

    if not (String.IsNullOrEmpty dir) then
        Directory.CreateDirectory(dir) |> ignore

    if not (paths.ArgsTemplate.Contains(OutputPlaceholder)) then
        invalidArg
            "ArgsTemplate"
            (sprintf "coverage args template must contain %s placeholder; got %A" OutputPlaceholder paths.ArgsTemplate)

    paths.ArgsTemplate.Replace(OutputPlaceholder, target)

/// True when most of a run's coverage lines failed to attribute to a symbol — a sign the
/// symbol graph is still being indexed (e.g. the first run after a schema bump recreated the
/// TestPrune DB, before the daemon's scan reached the covered files). A healthy run maps the
/// vast majority of lines (the only misses are rare inter-symbol lines), so requiring at
/// least half to map cleanly separates "still indexing" from a real run.
let internal symbolGraphLooksIncomplete (ingested: int) (skipped: int) : bool =
    ingested + skipped > 0 && ingested < skipped

/// Serially ingest each project's raw runner cobertura into the TestPrune DB
/// for runtime selection and into the plugin-owned project-attributed ratchet
/// table, then emit the eligible projects' aggregate ONCE to the shared
/// cobertura file that downstream gating reads.
///
/// `inputs` retains the producing test project and whether that project belongs
/// to the consumer ratchet. TestPrune.Core's line ratchet stores coverage without
/// project identity, so this local table is the compatibility layer that lets a later
/// configuration transition subtract one project's historical contribution.
///
/// Invariants:
/// - An empty / aborted raw cobertura parses to zero rows → ingests nothing →
///   cannot clobber the DB or the emitted file. A FULL one for a project that holds
///   runtime coverage is not ingested at all (`coverageMissingFromFullReceipt`).
/// - If NO raw inputs exist on disk, the shared cobertura is NOT written, so a
///   prior good emission is never overwritten with nothing.
type CoverageIngestOutcome =
    {
        /// Receipts that could not be ingested: each project is reported errored and owed.
        Failures: CoverageIngestFailure list
        /// Full receipts that measured nothing for a project that holds runtime coverage:
        /// not ingested, the prior rows kept, and the run flagged. The tests' result stands.
        CoverageMissing: CoverageIngestFailure list
    }

let internal coverageIngestFailures outcome = outcome.Failures

let internal applyCoverageIngestFailures
    (failures: CoverageIngestFailure list)
    (results: Map<string, TestResult>)
    : Map<string, TestResult> =
    failures
    |> List.fold
        (fun current failure ->
            Map.add
                failure.Project
                (TestsErrored $"runtime coverage receipt could not be ingested: %s{failure.Reason}")
                current)
        results

/// Record that `failure.Project`'s runtime coverage did not ingest (`CoverageNotIngested`, at
/// `path`), then `notify` the owner: that project is owed a whole-project run that ingests.
let internal armCoverageNotIngested (notify: unit -> unit) (path: string) (failure: CoverageIngestFailure) =
    CoverageNotIngested.arm notify path failure.Project failure.Reason
