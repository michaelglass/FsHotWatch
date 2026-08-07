module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsDiagnosticFilter
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

/// Above this many selected tests, an impact query stops looking like impact
/// analysis and starts looking like a full run wearing its clothes — so the
/// per-seed attribution below is worth paying for. Under it, the breakdown is
/// noise and is never computed.
[<Literal>]
let WideSelectionTests = 500

/// Attribution re-queries each seed ALONE, so its cost is linear in seed count.
/// Past this many seeds the breakdown is skipped — and said to be skipped, so an
/// absent breakdown is never misread as "no single seed dominated".
[<Literal>]
let MaxSeedsToAttribute = 200

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

/// Stable first-line sentinel prefixed onto the recorded output of a project
/// whose FILTERED run matched ZERO tests. `test-rerun --filter-*` fans a raw
/// filter out to every test project; a project with no matching test runs zero
/// tests and is recorded as a (filtered) pass so it can't masquerade as a
/// failure. But a pass is indistinguishable from a REAL pass, which hides the
/// important "your filter selected nothing" case — making `test-rerun` look
/// like it force-ran when it ran nothing. This marker lets the `run-tests`
/// command detect a zero-match project STRUCTURALLY (no fragile re-grep of
/// runner output) and report `no-tests-matched` distinctly when EVERY project
/// in a filtered run matched nothing.
[<Literal>]
let ZeroMatchMarker = "[fshw:no-tests-matched] "

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
/// For Microsoft Testing Platform, use `defaultCoverageArgsTemplate`; for
/// other runners (coverlet.collector, AltCover, OpenCover) supply your own.
type CoveragePaths =
    {
        Baseline: string
        Partial: string
        /// The SHARED cobertura the plugin emits the whole DB to — set identically
        /// for every project by DaemonConfig (the DB unions coverage across them),
        /// so the daemon writes one run-wide artifact, not one per project.
        Cobertura: string
        ArgsTemplate: string
    }

/// Default coverage args template for Microsoft Testing Platform hosts
/// (xUnit v3, MSTest v3 — anything invoked as `dotnet run --project <test>
/// --no-build -- ...`). `{output}` is replaced with the target file path.
[<Literal>]
let defaultCoverageArgsTemplate =
    "--coverage --coverage-output-format cobertura --coverage-output \"{output}\""

[<Literal>]
let private OutputPlaceholder = "{output}"

/// Substitute `{output}` in `paths.ArgsTemplate` with either Baseline or
/// Partial depending on `wasFiltered`. Creates the output dir if missing.
/// Raises with a clear message if the template is missing the placeholder —
/// silent emission of broken args is the bug we just fixed.
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
/// (symbol-relative, max-merged across all test projects), then emit the FULL
/// DB ONCE to a single shared cobertura file that downstream gating reads.
///
/// `inputs` is the list of `(rawCoberturaPath, sharedCoberturaOutputPath)`
/// tuples collected from each project that ran with coverage; the output path
/// is identical for every project (DaemonConfig points every project at one
/// shared file), so emitting once at the end is the single source of truth.
///
/// Invariants:
/// - An empty / aborted raw cobertura parses to zero rows → ingests nothing →
///   cannot clobber the DB or the emitted file.
/// - If NO raw inputs exist on disk, the shared cobertura is NOT written, so a
///   prior good emission is never overwritten with nothing.
let internal ingestAndEmitCoverage
    (db: Database)
    (repoRoot: string)
    (coverageOutput: string option)
    (rawPaths: string list)
    : unit =
    try
        let existing = rawPaths |> List.filter File.Exists

        let results =
            existing
            |> List.map (fun rawPath -> File.ReadAllText rawPath |> ingestCobertura db (Some repoRoot))

        let totalIngested = results |> List.sumBy (fun r -> r.Ingested)
        let totalSkipped = results |> List.sumBy (fun r -> r.Skipped)

        // Cold-start guard. If most coverage lines found NO containing symbol, the symbol
        // graph is still being indexed — e.g. the FIRST run after a schema bump recreated the
        // TestPrune DB, before the daemon's scan reached the covered files. Emitting now would
        // write a partial cobertura that DROPS every not-yet-indexed file's coverage, clobbering
        // a prior good emission and failing the ratchet. So skip the emit; the DB persists and
        // max-merges, so a later warm run emits in full.
        match existing, coverageOutput with
        | [], _
        | _, None -> ()
        | _, Some _ when symbolGraphLooksIncomplete totalIngested totalSkipped ->
            Logging.warn
                "test-prune"
                $"coverage: only %d{totalIngested} of %d{totalIngested + totalSkipped} lines mapped to a symbol — symbol graph still indexing; skipping emit to avoid a partial snapshot (will emit once warm)."
        | _, Some out ->
            let dir = Path.GetDirectoryName(out)

            if not (String.IsNullOrEmpty dir) then
                Directory.CreateDirectory(dir) |> ignore

            File.WriteAllText(out, emitCobertura db)
    with ex ->
        Logging.error "test-prune" $"coverage post-processing failed: %s{ex.Message}"

/// How fshw obtains the structured pass/fail report a test verdict is derived
/// from. The report (CTRF) — not the process exit code — is authoritative, but
/// only a runner that actually emits a parseable report can be trusted that
/// way, and an UNSUPPORTED `--report-*` flag is fatal (the runner exits
/// "invalid command line" and runs nothing). So injection of the report flag is
/// scoped by this setting.
type ReportVerificationFormat =
    /// Inject `--report-ctrf` iff the runner is detected as CTRF-capable
    /// (xUnit.v3, from the test project's package references), else fall back to
    /// the broad "is a dotnet command" heuristic. The default.
    | AutoDetect
    /// Always inject `--report-ctrf` (force-on for a capable runner the detector
    /// misses).
    | Ctrf
    /// Never inject a report flag — the process exit code is authoritative
    /// (force-off; e.g. a custom runner that would error on `--report-ctrf`).
    | Disabled

// ─────────────────────────────────────────────────────────────────────────────
// AUTOMATION-125 — a run may clear ONLY what it covered.
//
// The disease (same as AUTOMATION-95/99/112, arrived at by another road): "no
// failures reported by THIS run" was being read as "no failures". A full run failed
// project X; a queued impact-filtered re-run then executed a NARROWER selection,
// passed, and — via `ClearAllErrors` + last-cycle-wins — superseded X's red. X never
// re-ran and never passed, yet the check went green.
//
// The cure is structural, not a rule to remember: a run carries the SELECTION it was
// launched against, a completed run's COVERAGE is derived from that selection
// intersected with what actually executed, and clearing is a total function OVER that
// coverage. "Clear everything" is not a thing a filtered run can express — there is
// no wholesale clear left to reach for. A red therefore survives every run that did
// not execute it, and dies the moment one that did executes it green.
//
// (Types here; the functions over them live below `isZeroMatchResult`, which
// `RunCoverage.ofRun` reads.)
// ─────────────────────────────────────────────────────────────────────────────

/// What a run was LAUNCHED against, per test project — captured at dispatch
/// (`TestRunLaunch.Selection`), never re-derived at completion, because by then the
/// selection inputs have moved on. A project ABSENT from the selection map was not
/// launched at all: impact analysis skipped it (and `executeTests` records the skip
/// as `TestsPassed("", filtered, 0)`, a pass that proves precisely nothing).
type ProjectSelection =
    /// Launched with no class filter — every test in the project was asked to run.
    | ProjectInFull
    /// Launched under a class filter — only these classes were asked to run.
    | ProjectClasses of Set<string>

/// What a completed run may VINDICATE in one project — the honest reach of the
/// evidence it produced. Absent from `RunCoverage` ⇒ this run says nothing at all
/// about the project (it was skipped, it never ran, or it ran under a filter whose
/// reach we cannot know).
type ProjectCoverage =
    /// The project executed with NO filter: its green speaks for every test in it,
    /// so it may clear any red the project holds.
    | CoveredWholeProject
    /// The project executed only these classes: its green speaks for them and
    /// nothing else.
    | CoveredClasses of Set<string>

/// What a completed run is entitled to clear, keyed by test project.
type RunCoverage = Map<string, ProjectCoverage>

/// A file the symbol analyser could not read (AUTOMATION-113), retained until it
/// analyses cleanly. Carries everything its ledger entry needs, because the entry has
/// to be RE-REPORTED after every test run: the run's ledger rewrite clears this
/// plugin's whole slice, and a warning erased by a cycle that never addressed it is
/// the same defect as a red erased by a run that never executed it (AUTOMATION-125).
/// Before this, the first test run after an analysis failure silently dropped the
/// warning — the file kept forcing full-suite runs, but nothing told anyone, and
/// AUTOMATION-113's "this must deny the check a green verdict" quietly stopped holding.
type UnanalyzableFile =
    {
        /// Repo-relative path — what the diagnostic names.
        RelPath: string
        /// Absolute path — the ledger key.
        File: string
        /// Why analysis failed (the FCS/parse error), carried into the diagnostic.
        Reason: string
    }

/// A red this plugin still OWES the user: a test failure (or a project that produced
/// no verdict at all) that no run COVERING it has passed since.
///
/// The shared `ErrorLedger` is a pure projection of the outstanding list — the
/// `TestsFinished` handler rewrites it wholesale each cycle (`ClearAllErrors` +
/// re-report the whole set) — so `fshw errors` shows exactly what is outstanding:
/// never a superseded red (the AUTOMATION-95 "Issue 2" accumulation), and never a
/// laundered one (AUTOMATION-125).
type OutstandingFailure =
    {
        /// The test project the red belongs to.
        Project: string
        /// The failing test CLASS when the runner named one; `None` for a
        /// project-level red (unparseable failure output, timeout, errored,
        /// deferred). A project-level red is only clearable by a run that executed
        /// the project in FULL.
        Class: string option
        /// The ledger key: the class's source file, or the synthetic
        /// `<tests/Project>` bucket when no source file is known.
        File: string
        Entry: ErrorLedger.ErrorEntry
    }

/// Configuration for a test project to run.
type TestConfig =
    {
        Project: string
        Command: string
        Args: string
        Group: string
        Environment: (string * string) list
        /// Template for class-based test filtering. {classes} is replaced with
        /// the joined class names. Example: "-- --filter-class {classes}"
        FilterTemplate: string option
        /// Separator for joining class names in the filter. Default: " "
        /// Example: "|ClassName=" for dotnet test --filter "ClassName=A|ClassName=B"
        ClassJoin: string
        /// Per-project timeout in seconds. None → use top-level default.
        TimeoutSec: int option
        /// How to obtain the structured test report for the verdict. Default
        /// `AutoDetect`. Override via `.fshw.json` `reportVerificationFormat`.
        ReportVerificationFormat: ReportVerificationFormat
    }

type AffectedTestsState =
    | NotYetAnalyzed
    | Analyzed of TestMethodInfo list

type TestPruneState =
    {
        PendingAnalysis: Map<string, AnalysisResult list>
        SymbolSnapshot: Map<string, SymbolInfo list>
        AffectedTests: AffectedTestsState
        ChangedSymbols: string list
        ChangedFiles: string list
        /// Last completed test run's results, if any. Replaces the prior
        /// `Lifecycle.value`-encoded last results — `ctx.IsRunning "tests"`
        /// is the source of truth for "currently running", so we no longer
        /// need a phantom-typed Idle/Running phase to wrap this value.
        LastResults: TestResults option
        /// The id of the run that produced `LastResults` — i.e. the directory its
        /// CTRF reports live in (`.fshw/test-runs/<runId>/`). Reported to the CLI by
        /// `test-scope` so the verdict can DECLARE which reports are this run's,
        /// instead of inferring membership from mtimes. `None` until a run completes.
        LastRunId: Guid option
        /// True if a BuildCompleted arrived while a test run was in flight.
        /// The synchronous `Custom(TestsFinished)` handler reads this AFTER
        /// the run completes — at which point `state.ChangedSymbols` reflects
        /// every FileChecked that landed during the run, including ones that
        /// arrived between the queueing BuildCompleted and TestsFinished.
        /// Cleared when the rerun is dispatched.
        PendingRerun: bool
        /// Maps test class name → absolute source file path (built during FileChecked analysis).
        TestClassFiles: Map<string, string>
        /// Item 3 (BuildCompleted-gated stamping). True after the plugin has
        /// observed at least one `BuildCompleted BuildSucceeded` event in this
        /// daemon session. The `FileChecked` handler uses this to decide
        /// whether a clean FCS check is allowed to promote the freshness
        /// sidecar to `fcsClean = true`.
        ///
        /// fshw's cold-scan pipeline guarantees BuildCompleted reaches the
        /// TestPrune mailbox before any FileChecked (see Daemon.fs's
        /// performScan: `BuildPlugin terminal awaited before FCS tier
        /// checks`). So in normal operation this flag is set before any
        /// FileChecked is processed; the gate is therefore effective on the
        /// very first cold start of a fresh daemon, no two-session warm-up
        /// required.
        ///
        /// Resets on plugin restart — that's by design. A daemon restart
        /// clears the in-process "I've seen warm FCS this session"
        /// assertion.
        BuildCompletedInThisSession: bool
        /// Per-test-project dependency fingerprint observed at the last
        /// `BuildCompleted`. A project whose fingerprint moves between builds had
        /// a dependency/binary change the symbol diff can't see, so its tests are
        /// force-run (dependency-fanout). Empty until the first build establishes
        /// the baseline. See `DependencyFanout`.
        PriorProjectFingerprints: Map<string, string>
        /// Test projects whose dependency fingerprint changed but whose force-run
        /// is deferred because a run was already in flight when the build landed.
        /// The queued rerun consumes (and clears) this so a dependency change that
        /// arrives mid-run is not lost. Unioned with the rerun's own fanout.
        PendingForceRunProjects: Set<string>
        /// True when the most recent `flushAndQueryAffected` had changed/queued
        /// symbols but EVERY one proved to have NO covering test (all dropped as
        /// uncovered), leaving an empty affected set. A definitive "nothing to
        /// verify" green: there WAS pending work and none of it is testable, so a
        /// run would verify nothing. Lets the zero-affected skip in
        /// `runTestsWithImpact` complete green immediately — even on a cold daemon
        /// with no session baseline — instead of pointlessly running (and, on a
        /// memory-pressured box, potentially wedging in) the full suite. Distinct
        /// from a genuine cold start with NO pending symbols, which must still run
        /// the full-suite baseline to establish one (guarded by `hasCachedResults`).
        /// Recomputed on every flush; only read right after one, by the run-trigger.
        ChangedSymbolsAllUncovered: bool
        /// Repo-relative paths of files whose symbol analysis FAILED and has not
        /// since succeeded. These files contribute NO symbols, so they are invisible
        /// to the impact graph — an edit to one has nothing to diff and selects
        /// nothing on its own (AUTOMATION-113). While this set is non-empty the run
        /// falls back to the coarse selection (`coarseFallbackProjects`: every test
        /// project, in full), because "I cannot analyse this file" means the SELECTOR
        /// cannot know what to select, and a superset is safe where a gap is not.
        ///
        /// A file leaves the map as soon as it analyses cleanly, so the fallback is
        /// self-clearing and costs nothing on a healthy tree. NOT persisted: every
        /// file is re-checked on a cold scan, which repopulates the map from scratch.
        ///
        /// Keyed by repo-relative path; the value carries what the ledger entry needs,
        /// so every `TestsFinished` can RE-REPORT the diagnostic after clearing the
        /// plugin's slice.
        UnanalyzableFiles: Map<string, UnanalyzableFile>
        /// `run-tests` force-runs that arrived while another run held the
        /// "tests" slot. A force-run is OWED work — `test-rerun`
        /// is the explicit "prove it ran" verb, so a busy slot must QUEUE the
        /// run, never refuse it (a refusal that exits 0 is a vacuous green).
        /// Drained FIFO by the `TestsFinished` handler, one per completed run
        /// (each queued run's own TestsFinished drains the next). Each entry
        /// carries the reply TCS the IPC command is awaiting — the command
        /// bounds that wait (`waitSec`), so an entry stranded by daemon
        /// teardown cannot hang the client.
        QueuedCommandRuns: (TestConfig list * string option * Tasks.TaskCompletionSource<string>) list
        /// The reds no COVERING run has passed since (AUTOMATION-125). Rewritten on
        /// every `TestsFinished`: a red leaves ONLY when a run that actually executed
        /// it passes. The shared error ledger is a projection of this list, so a
        /// narrower run can no longer erase a failure it never ran.
        ///
        /// Session-scoped by design (not persisted). A daemon restart has no baseline
        /// (`LastResults = None`), so its first run is the FULL suite — which re-runs
        /// the failing test and re-establishes (or genuinely clears) the red from
        /// evidence. Persisting it would buy nothing and could only wedge a red that
        /// no longer exists.
        OutstandingFailures: OutstandingFailure list
        /// What the last completed run actually COVERED — the receipt that goes with
        /// `LastResults` (which says what it FOUND). The two are read together: a green
        /// result means "nothing failed IN WHAT THIS COVERED", never "nothing failed".
        ///
        /// Kept in state, not just in the `TestsFinished` closure, so a consumer OUTSIDE
        /// the handler — an IPC command, a verdict writer — can ask what the last run
        /// covered instead of inventing a parallel notion of scope that could drift from
        /// the one the ledger clears by. Empty until the first run completes.
        LastCoverage: RunCoverage
    }

/// The slice of `TestPruneState` a test RUN reads — and nothing else.
///
/// The run is an `Async` handed to `RunExclusive`, and it lives for as long as the
/// suite does: minutes, on a full run. Whatever that async closes over, it PINS for
/// that whole time. Closing over the state RECORD pinned the entire generation —
/// including `SymbolSnapshot` (the repo-wide symbol table) and `PendingAnalysis`,
/// neither of which a run touches. Meanwhile the agent loop keeps folding incoming
/// `FileChecked` events into NEW generations, and the pinned one holds down every
/// node those newer generations replaced. The peak lands exactly when the suite is
/// running and FCS is at its own peak — and FsHotWatch is ~85% native FCS memory.
///
/// So the launch copies the four references the run actually reads, and the rest of
/// each generation dies on schedule. The type is the enforcement: a run cannot reach
/// a field that is not in here, because it does not have the state to reach it with.
///
/// (`LastResults` is NOT one of them. The run's interest in it is exactly one bit —
/// "does a baseline exist" — which the caller computes and passes as
/// `hasCachedResults`.)
type TestRunInputs =
    {
        /// The impact selection: which test classes the changed symbols reach.
        AffectedTests: AffectedTestsState
        /// The in-memory hot view of the pending-verification queue, unioned with the
        /// durable queue to form the snapshot this run is launched against.
        ChangedSymbols: string list
        /// Every changed symbol proved to have no covering test — the "nothing to
        /// verify" green.
        ChangedSymbolsAllUncovered: bool
        /// Files whose symbol analysis failed: while non-empty, the run widens to
        /// every test project (AUTOMATION-113).
        UnanalyzableFiles: Map<string, UnanalyzableFile>
    }

module TestRunInputs =
    /// Project the state down to what a run reads, at LAUNCH time — the point of
    /// the copy is that the state record itself is not carried into the run.
    let ofState (state: TestPruneState) : TestRunInputs =
        { AffectedTests = state.AffectedTests
          ChangedSymbols = state.ChangedSymbols
          ChangedSymbolsAllUncovered = state.ChangedSymbolsAllUncovered
          UnanalyzableFiles = state.UnanalyzableFiles }

/// Custom message posted from the async test runner back to the synchronous
/// Custom handler. Carries the lifecycle events (Started + Completed) so the
/// handler can emit them inside the framework's per-event capture window —
/// required for the §2a cache to record EmittedEvents on terminal status,
/// which `tryReplayCache` re-fires to downstream subscribers (FileCommandPlugin
/// keys off TestRunCompleted) when the cache hits.
///
/// Live `TestProgress` events still fire from the async (per-group, streaming)
/// because they're not part of cache replay (cache replay skips per-group
/// progress and goes straight from Started to Completed by design).
///
/// `launch` carries the set the run was LAUNCHED against — the queue snapshot
/// (`Symbols`) and, per launched symbol, the set of test PROJECTS whose tests
/// cover it (`CoveringProjectsBySymbol`) — both captured at dispatch time. The
/// synchronous `Custom(TestsFinished)` handler uses this, NOT the live
/// `state.AffectedTests`/`state.ChangedSymbols` (which mid-run `BatchChecked`
/// flushes overwrite), to decide per-symbol green-commit: a symbol leaves the
/// queue only when EVERY project covering it passed. A symbol with NO covering
/// projects (empty set) is committed unconditionally at flush time — there's
/// nothing to wait for. `Symbols` empty for the degenerate zero-affected skip.
///
/// `Selection` is the run's SCOPE (AUTOMATION-125): what it was launched against,
/// per project. It is the input to `RunCoverage.ofRun`, and therefore decides what
/// this run's green is allowed to CLEAR. A project absent from it was never
/// launched — no matter what its result says (an impact-skip is recorded as a
/// filtered PASS), it vindicates nothing. Empty for the zero-affected skip and the
/// aborted-run lifecycle: they executed nothing, so they clear nothing.
type TestRunLaunch =
    { Symbols: Set<string>
      CoveringProjectsBySymbol: Map<string, Set<string>>
      Selection: Map<string, ProjectSelection> }

[<NoComparison; NoEquality>]
type TestPruneMsg =
    | TestsFinished of started: TestRunStarted * completed: TestRunCompleted * launch: TestRunLaunch
    /// A `run-tests` IPC command asking the MAILBOX to launch its force-run
    /// under the `RunExclusive "tests"` slot (AUTOMATION-99). The command must
    /// never execute tests on the IPC thread itself: a run outside the slot is
    /// invisible to the daemon's whole runtime model — `IsRunning "tests"`
    /// reads false (so a concurrent FileChecked stamps a terminal status over
    /// it), the plugin never reports Running, and `AnyPluginBusy()` reads
    /// false, letting a concurrent `fshw check` resolve its verdict wait and
    /// exit 0 while the test process is literally alive. Routing the launch
    /// through the mailbox serialises it with every other launch site and
    /// makes the run held-and-visible for its whole duration. `reply` carries
    /// the results JSON back to the awaiting IPC command; every completion
    /// path must resolve it.
    | RunTestsRequested of configs: TestConfig list * filter: string option * reply: Tasks.TaskCompletionSource<string>

/// Build the degenerate Started→Aborted lifecycle a faulted run posts back so
/// the synchronous `TestsFinished` handler drives the plugin to a NON-green
/// terminal status. A `beforeRun` throw / `executeTests` fault means the suite
/// it guards NEVER RAN — that must surface as a failure, never a stale prior
/// green (AUTOMATION-68). Both the impact path (`runTestsWithImpact`) and the
/// manual `run-tests` command build this identically, so it lives here to keep
/// the two in lockstep. `Results = Map.empty` ⇒ the handler commits nothing from
/// the pending queue; `reason` carries the hook's failure output so `fshw check`
/// / `fshw errors` shows WHY the preflight failed.
let private abortedRunLifecycle (reason: string) : TestRunStarted * TestRunCompleted =
    let runId = Guid.NewGuid()

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.Zero
          Outcome = Aborted reason
          Results = Map.empty
          RanFullSuite = true }

    started, completed

/// Translate a repo-root-relative glob (`*`, `?`, `**`, `/`) into a regex
/// anchored against a repo-relative path. `**` matches across directory
/// separators (including none); a single `*`/`?` does NOT cross `/`. A trailing
/// `dir/**` also matches `dir` itself (zero segments). Paths are normalised to
/// `/` before matching so the same config works on every OS.
let internal dependsOnGlobToRegex (glob: string) : System.Text.RegularExpressions.Regex =
    // Collapse a trailing `/**` to a sentinel so the leading `/` becomes
    // optional (`dir/**` matches `dir`, `dir/x`, `dir/x/y`). Done before the
    // char scan so the `/` isn't emitted as a mandatory literal.
    let normalized = glob.Replace('\\', '/').TrimStart('/')

    let normalized, trailingDoubleStar =
        if normalized.EndsWith("/**") then
            normalized.Substring(0, normalized.Length - 3), true
        else
            normalized, false

    let sb = System.Text.StringBuilder()
    sb.Append('^') |> ignore
    let mutable i = 0

    while i < normalized.Length do
        let c = normalized.[i]

        if c = '*' then
            if i + 1 < normalized.Length && normalized.[i + 1] = '*' then
                // `**` — match any chars including `/`. A `**/ ` (cross-dir,
                // zero-or-more leading segments) makes the following separator
                // optional so `a/**/b` matches `a/b` too.
                if i + 2 < normalized.Length && normalized.[i + 2] = '/' then
                    sb.Append("(?:.*/)?") |> ignore
                    i <- i + 3
                else
                    sb.Append(".*") |> ignore
                    i <- i + 2
            else
                // single `*` — any run of non-separator chars
                sb.Append("[^/]*") |> ignore
                i <- i + 1
        elif c = '?' then
            sb.Append("[^/]") |> ignore
            i <- i + 1
        else
            sb.Append(System.Text.RegularExpressions.Regex.Escape(string c)) |> ignore
            i <- i + 1

    // Re-attach the trailing `/**`: an OPTIONAL `/<anything>` so the bare dir
    // and any descendant both match.
    if trailingDoubleStar then
        sb.Append("(?:/.*)?") |> ignore

    sb.Append('$') |> ignore

    System.Text.RegularExpressions.Regex(
        sb.ToString(),
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ||| System.Text.RegularExpressions.RegexOptions.CultureInvariant
    )

/// Resolve the on-disk files under `repoRoot` whose repo-relative path matches
/// any of the `dependsOn` globs. Deterministic: returns sorted, distinct
/// absolute paths. Globs that match nothing contribute nothing; a glob that is
/// a plain existing file path resolves to that one file. Directory enumeration
/// errors are swallowed (best-effort) so a transient IO hiccup can't crash the
/// cache-key computation.
let internal resolveDependsOnFiles (repoRoot: string) (dependsOn: string list) : string list =
    if dependsOn.IsEmpty then
        []
    else
        let rootFull = Path.GetFullPath(repoRoot)

        let toRel (abs: string) =
            Path.GetRelativePath(rootFull, abs).Replace('\\', '/')

        // A plain glob with no wildcard meta is a direct file reference — resolve
        // it without walking the whole tree (cheap + handles files outside any
        // enumerable subdir uniformly).
        let isLiteral (g: string) =
            not (g.Contains('*') || g.Contains('?'))

        let literalHits =
            dependsOn
            |> List.filter isLiteral
            |> List.choose (fun g ->
                let abs =
                    Path.GetFullPath(Path.Combine(rootFull, g.Replace('\\', '/').TrimStart('/')))

                if File.Exists abs then Some abs else None)

        let globPatterns =
            dependsOn |> List.filter (isLiteral >> not) |> List.map dependsOnGlobToRegex

        let globHits =
            if globPatterns.IsEmpty then
                []
            elif not (Directory.Exists rootFull) then
                []
            else
                // Repo-root-rooted walk of EVERY file. `SearchOption.AllDirectories`
                // here would follow `.devenv/profile` into the /nix/store symlink
                // cycle, so SafeWalk owns the recursion (no symlinked-dir descent,
                // depth-capped), and its per-subtree IO errors are already swallowed
                // internally.
                SafeWalk.enumerateFilePaths SafeWalk.ToolingExcludedDirs "*" rootFull
                |> Seq.filter (fun abs ->
                    let rel = toRel abs
                    globPatterns |> List.exists (fun rx -> rx.IsMatch(rel)))
                |> Seq.toList

        (literalHits @ globHits) |> List.distinct |> List.sort

/// Deterministic content hash of the files matched by the `dependsOn` globs.
/// Editing, adding, or deleting a matched file changes this hash, which salts
/// the test cache key so an external input (a DB migration, a generated file,
/// a schema) that test-prune's symbol diff can't see still invalidates a stale
/// cached test verdict. Empty `dependsOn` → empty string (NO salt: the key is
/// byte-identical to the pre-feature key, so existing caches keep hitting).
/// Missing files are skipped; a glob matching nothing contributes nothing.
let internal externalDependencyHash (repoRoot: string) (dependsOn: string list) : string =
    let files = resolveDependsOnFiles repoRoot dependsOn

    if files.IsEmpty then
        ""
    else
        let sb = System.Text.StringBuilder()

        for path in files do
            let rel = Path.GetRelativePath(Path.GetFullPath(repoRoot), path).Replace('\\', '/')

            let h =
                try
                    FsHotWatch.CheckCache.sha256Hex (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes path))
                with
                | :? IOException
                | :? UnauthorizedAccessException -> "unreadable"

            sb.Append(rel.Length) |> ignore
            sb.Append(':') |> ignore
            sb.Append(rel) |> ignore
            sb.Append('@') |> ignore
            sb.Append(h) |> ignore
            sb.Append('\n') |> ignore

        FsHotWatch.CheckCache.sha256Hex (sb.ToString())

/// True when this result is a zero-match-under-filter pass (the runner found no
/// test matching the active `--filter-*`). Detected structurally via the
/// `ZeroMatchMarker` prefix `executeTests` stamps on such results.
let internal isZeroMatchResult (result: TestResult) : bool =
    match result with
    | TestsPassed(o, _, _) -> o.StartsWith(ZeroMatchMarker, StringComparison.Ordinal)
    | _ -> false

/// What a run actually VERIFIED.
///
/// This replaces a boolean that could not express the difference between "no
/// project was selected" and "every selected project matched nothing". The bool
/// was `false` for an empty result set — defensible in isolation ("no project"
/// is not the claim "every project matched nothing") and wrong in effect: an
/// empty run became indistinguishable from one that executed tests, so it fell
/// through to `Tests passed` and exit 0. A run that verified nothing wore the
/// same clothes as a run that verified everything.
///
/// Three cases rather than one case with a flag, because they carry different
/// EVIDENCE. `AllZeroMatch` knows a filter ran against discovered tests and how
/// many projects it was applied to, so remediation can be specific.
/// `NoProjectsSelected` has no discovered names at all — nothing ran to discover
/// them — so the only honest report is that the scope was empty. Offering a
/// "did you mean…" there would be guessing dressed as diagnosis.
type internal RunVerification =
    /// No project was selected — nothing was invoked.
    | NoProjectsSelected
    /// Projects ran; every one matched zero tests under the active filter.
    | AllZeroMatch of projectCount: int
    /// At least one project executed at least one test.
    | Ran

/// Stable wire token. Consumers match on this; adding a case is a deliberate
/// contract change.
let internal verificationToken (c: RunVerification) : string =
    match c with
    | NoProjectsSelected -> "no-projects-selected"
    | AllZeroMatch _ -> "all-zero-match"
    | Ran -> "ran"

/// True when the run verified NOTHING — the property every gate cares about,
/// and the one the old boolean could not answer.
let internal verifiedNothing (c: RunVerification) : bool =
    match c with
    | NoProjectsSelected
    | AllZeroMatch _ -> true
    | Ran -> false

let internal verificationOf (results: TestResults) : RunVerification =
    if results.Results.IsEmpty then
        NoProjectsSelected
    elif results.Results |> Map.forall (fun _ r -> isZeroMatchResult r) then
        AllZeroMatch results.Results.Count
    else
        Ran

/// Retained for the existing wire field, which older CLIs still read. Prefer
/// `verificationOf`: this cannot distinguish an empty run from a real one, which
/// is precisely the defect it caused.
let internal allZeroMatch (results: TestResults) : bool =
    match verificationOf results with
    | AllZeroMatch _ -> true
    | NoProjectsSelected
    | Ran -> false

let private formatTestResultsJson (results: TestResults) =
    let projects =
        results.Results
        |> Map.toList
        |> List.map (fun (name, result) ->
            let (status, output) =
                match result with
                // A zero-match-under-filter pass gets a DISTINCT status so a
                // consumer can tell "ran, all green" from "matched nothing".
                // Strip the internal marker from the surfaced output.
                | TestsPassed(o, _, _) when o.StartsWith(ZeroMatchMarker, StringComparison.Ordinal) ->
                    ("no-tests-matched", o.Substring(ZeroMatchMarker.Length))
                | TestsPassed(o, _, _) -> ("passed", o)
                | TestsFailed(o, _, _) -> ("failed", o)
                | TestsTimedOut(o, _, _, _) -> ("timed-out", o)
                | TestsDeferred reason -> ("deferred", reason)
                | TestsErrored reason -> ("errored", reason)

            {| project = name
               status = status
               output = truncateOutput 200 output
               elapsedMs = (TestResult.elapsed result).TotalMilliseconds |})

    JsonSerializer.Serialize(
        {| elapsed = $"%.1f{results.Elapsed.TotalSeconds}s"
           // `noTestsMatched` is true iff EVERY project matched zero tests under
           // the active filter. RETAINED for CLIs older than `coverage`; it
           // cannot express "no project was selected", which is why the field
           // below exists.
           noTestsMatched = allZeroMatch results
           // The run-level answer to "did this verify anything?", stated by the
           // producer rather than reconstructed by the consumer from array
           // lengths. A consumer that does not know this field falls back to the
           // counts — an ABSENT field must never be read as "ran".
           coverage = verificationToken (verificationOf results)
           verifiedNothing = verifiedNothing (verificationOf results)
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
/// (AUTOMATION-112 — impact filtering is a latency optimization for the inner loop,
/// never the basis of a correctness claim) and the unanalysable-file fallback
/// (AUTOMATION-113 — a file the analyser cannot read has no symbols to select by).
let internal fullSuiteProjects (configs: TestConfig list) : Set<string> =
    configs |> List.map (fun c -> c.Project) |> Set.ofList

/// The coarse fallback for files the symbol analyser could not read.
///
/// A file whose analysis FAILED contributes no symbols. The impact graph therefore
/// cannot see it, the symbol diff finds nothing changed in it, and the selection it
/// deserves is EMPTY — so an edit to such a file would select zero tests and the
/// check would go green having run nothing relevant. That is silent UNDER-selection: "the
/// one failure mode a test-impact tool must not have" (`TestPrune.EdgeEmission`).
///
/// The honest reading of an unanalysable file is not "nothing is affected" — it is
/// "I cannot tell you what is affected". The only sound answer to that is the coarse
/// one: run EVERY test project, because any of them could cover the file. A superset
/// is safe where a gap is not — exactly the rule `EdgeEmission.resolveTargets` follows
/// when a seed names a symbol it can no longer resolve.
///
/// Returned as force-run projects, which `runTestsWithImpact` already knows how to
/// honour: a project present with an empty class list runs IN FULL (unfiltered), and a
/// non-empty force-run set also disables the zero-affected skip gate — so an
/// unanalysable file can never reach the "0 affected, green, 0 ran" verdict either.
let internal coarseFallbackProjects
    (configs: TestConfig list)
    (unanalyzableFiles: Set<string>)
    (fanout: Set<string>)
    : Set<string> =
    if Set.isEmpty unanalyzableFiles then
        fanout
    else
        Set.union fanout (fullSuiteProjects configs)


/// The single answer to "what did this run actually cover?" — PUBLIC because it is
/// load-bearing outside this handler too: a verdict writer must be able to ask it, per
/// project and per suite, what a run executed, rather than maintain a parallel notion
/// of scope that can drift from the one the ledger clears by.
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
    ///     disk may speak for it — this is AUTOMATION-125's laundering guard and it is
    ///     checked BEFORE any report evidence.
    ///   * `wasFiltered = true` and the selection named classes → `CoveredClasses`.
    ///   * `wasFiltered = true` otherwise → the `run-tests --filter <raw>` passthrough:
    ///     an arbitrary filter string whose reach the LAUNCH REQUEST cannot express
    ///     (every project goes down as `ProjectInFull`). Ask the run's own evidence
    ///     instead — the classes its CTRF report shows actually RAN AND PASSED
    ///     (AUTOMATION-225). Without that the request claimed nothing, so
    ///     `test-rerun --filter-class X` could re-run X, pass, and STILL leave X's red
    ///     standing forever — an environmental failure became permanently sticky.
    ///     Evidence is a RECEIPT, not a guess: it names only classes this run executed
    ///     and saw pass. Fail-closed everywhere it is absent, unreadable or incomplete
    ///     (`passedClassesOfReport`), which degrades to the old "claims nothing".
    ///   * a TIMED-OUT project is excluded from the evidence path: a project killed
    ///     for being stuck is a fact about the PROJECT, and a report flushed by a
    ///     process we shot is not a receipt for anything.
    let ofRun
        (selection: Map<string, ProjectSelection>)
        (results: Map<string, TestResult>)
        (passedClasses: Map<string, Set<string>>)
        : RunCoverage =
        results
        |> Map.toList
        |> List.choose (fun (project, result) ->
            let ran =
                match result with
                | TestsDeferred _
                | TestsErrored _ -> false
                | _ -> not (isZeroMatchResult result)

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
                | Some(ProjectClasses _)
                | Some ProjectInFull -> fromEvidence ())
        |> Map.ofList

/// The SCOPE `fshw confirm` reads, as a pure PROJECTION of `RunCoverage`.
///
/// It is deliberately not an independent derivation. Until AUTOMATION-129 the caller's
/// scope came from `classifyRunScope` (over `LastResults`) while the ledger decided
/// what a run could CLEAR from `RunCoverage` — two answers to one question, with
/// nothing making them agree. `confirm` could therefore go green on a scope the ledger
/// would never have granted. There is one source of truth now, and this is a view of
/// it.
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

module internal OutstandingFailure =

    /// Identity for de-duplication: the same class failing the same way twice is one
    /// red, not two. (`Entry` is compared by message only — the detail is the full
    /// runner output, which differs by timing/ordering between otherwise identical
    /// failures.)
    let private identity (f: OutstandingFailure) =
        f.Project, f.Class, f.File, f.Entry.Message

    /// The reds a run CARRIES: prior failures it did not cover, and so cannot speak
    /// for. These are what deny an otherwise-passing run its green verdict.
    ///
    /// `configured` prunes reds for projects the daemon no longer runs (a project
    /// removed from `tests.projects` could otherwise never be covered again, and its
    /// red would wedge the verdict forever — the AUTOMATION-99 stuck-red, rebuilt). Empty
    /// ⇒ analysis-only, nothing to prune by.
    let carriedOver
        (configured: Set<string>)
        (coverage: RunCoverage)
        (prior: OutstandingFailure list)
        : OutstandingFailure list =
        let stillConfigured (f: OutstandingFailure) =
            Set.isEmpty configured || Set.contains f.Project configured

        prior
        |> List.filter (fun f -> stillConfigured f && not (RunCoverage.covers f.Project f.Class coverage))

    /// The outstanding set after a run: what it carried, plus what it found. A red the
    /// run COVERED is not carried — if it failed again, `found` re-adds it from THIS
    /// run's evidence; if it passed, it is gone, which is the whole point (no permanent
    /// stuck-red). Defined in terms of `carriedOver` so the status verdict and the
    /// ledger can never disagree about what is still red.
    let carry
        (configured: Set<string>)
        (coverage: RunCoverage)
        (found: OutstandingFailure list)
        (prior: OutstandingFailure list)
        : OutstandingFailure list =
        carriedOver configured coverage prior @ found |> List.distinctBy identity

    /// Human-readable "what is still red that this run did not look at", for the
    /// status verdict. Names the projects, not every class — the ledger has the detail.
    let summarize (failures: OutstandingFailure list) : string =
        failures
        |> List.map (fun f -> f.Project)
        |> List.distinct
        |> List.sort
        |> String.concat ", "

/// The ledger diagnostic for a file the symbol analyser could not read. LOUD by
/// construction: it is a WARNING (so it surfaces in `fshw check` output and, under
/// the default warn-fail policy, denies the check a green verdict) keyed to the file
/// itself, naming the file and the reason. The previous behaviour reported nothing a
/// consumer could see — the plugin logged, set a status that the very next file's
/// `Completed` overwrote, and dropped the file. Nothing in the system would ever have
/// told anyone.
let internal unanalyzableFileDiagnostic (relPath: string) (reason: string) : ErrorLedger.ErrorEntry =
    ErrorLedger.ErrorEntry.warningWithDetail
        $"%s{relPath}: symbol analysis failed — %s{reason}"
        $"TestPrune could not extract symbols from this file, so it is INVISIBLE to the impact graph: a change to it \
           has no symbols to diff and would select no tests on its own. Every test project is being run in full for \
           this cycle (safe over-selection) until the file analyses cleanly. Fix the reported parse/check error — a \
           misplaced `///` doc comment (FS3520) is the usual cause."

/// Build the filter arg string for a config given affected classes.
let internal buildFilterArgs (config: TestConfig) (classesByProject: Map<string, string list>) : string option =
    let classes =
        classesByProject |> Map.tryFind config.Project |> Option.defaultValue []

    match classes, config.FilterTemplate with
    | [], _ -> None
    | _, None ->
        Logging.debug "test-prune" $"No filterTemplate configured — running all tests for %s{config.Project}"
        None
    | classes, Some template ->
        let joined = classes |> String.concat config.ClassJoin
        let result = template.Replace("{classes}", joined)
        Logging.info "test-prune" $"Filter: %s{result}"
        Some result

/// Microsoft.Testing.Platform exit code for "no tests matched / zero tests ran".
[<Literal>]
let internal zeroTestsExitCode = 8

/// True when a *filtered* run matched no tests in this project. An explicit
/// `--filter-*` passthrough (run-tests / test-rerun) is fanned out to EVERY test
/// project; a project that has no test matching the filter runs zero tests and
/// the runner exits non-zero (MTP uses exit code 8, `ZeroTests`). That is NOT a
/// test failure — the tests simply don't exist for this filter — so it must be
/// treated like an impact-skip (passed/filtered, contributing no coverage),
/// exactly as a template-filtered project with no affected classes already is.
///
/// Gated on `wasFiltered`: an UNFILTERED project that runs zero tests is a real
/// problem (misconfigured runner, empty suite) and must still surface, so this
/// returns false for it. Detection is structural (the canonical exit code) with
/// a text fallback for runners that exit non-zero without emitting code 8 but
/// still print MTP's zero-tests summary line.
let internal isZeroTestsUnderFilter (wasFiltered: bool) (outcome: ProcessOutcome) : bool =
    wasFiltered
    && match outcome with
       | ProcessOutcome.Failed(code, _) when code = zeroTestsExitCode -> true
       | ProcessOutcome.Failed(_, output) ->
           // A text SEARCH for a marker: a capture cut short by an unfinished drain
           // can only cost us the hit (falling back to the exit code above), never
           // invent one. Sound to search the untagged text.
           (ProcessOutput.text output).Contains("Zero tests ran", StringComparison.OrdinalIgnoreCase)
       | _ -> false

/// Build the human-readable error lines for a FAILED test project run, parsed
/// from the runner's captured `output`. The header line plus the per-test
/// `failed ...` lines plus the MTP summary lines (`total:`/`failed:`/
/// `succeeded:`).
///
/// CRITICAL for CI observability: a red run that only reports "failed: 1" with
/// NO test name is undiagnosable when the on-disk `.fshw/test-runs` log isn't
/// uploaded as an artifact. MTP prints a failing test as a line whose TRIMMED
/// form starts with `failed ` — INCLUDING `failed (canceled) <name> (Nms)` for a
/// test killed by its `[<Fact(Timeout=...)>]` under CI load (the documented
/// daemon-load flake class). We match the trimmed prefix so leading indentation
/// (which varies by MTP version / capture path) never hides the name. As a
/// backstop, when the run failed but NO `failed ` line parsed (a crash, an
/// OOM-kill, or an output shape the matcher doesn't yet recognise), the tail of
/// the captured output is echoed so the failure is ALWAYS visible from the CI
/// console alone — never silently swallowed into "0 test(s) failed".
let internal formatFailureReport (projectName: string) (output: string) : string list =
    let lines = output.Split('\n')

    let isFailedLine (l: string) = l.TrimStart().StartsWith("failed ")

    let failedTests = lines |> Array.filter isFailedLine |> Array.toList

    let summaryLines =
        lines
        |> Array.filter (fun l ->
            let t = l.TrimStart()

            t.StartsWith("Test run summary:")
            || t.Contains("total:")
            || t.Contains("failed:")
            || t.Contains("succeeded:"))
        |> Array.filter (isFailedLine >> not)
        |> Array.toList

    [ $"%s{projectName}: %d{failedTests.Length} test(s) failed:"
      yield! failedTests |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      yield! summaryLines |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      if List.isEmpty failedTests then
          $"%s{projectName}: run failed but no per-test 'failed' line was parsed — dumping last output lines so the failure is visible without the saved log:"

          let tail =
              lines
              |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
              |> (fun ls -> ls.[max 0 (ls.Length - 40) ..])

          yield! tail |> Array.map (fun l -> $"  | %s{l.TrimEnd()}") |> Array.toList ]

/// What is known about the structured test report for a run — the verdict
/// input. Modelled as a DU rather than a `bool * report option` so the
/// meaningless "no report was requested yet somehow a parsed report exists"
/// state cannot be constructed.
type ReportEvidence =
    /// No report was requested (an unknown / unsupported runner) — the process
    /// exit code is the only pass/fail signal available.
    | NoReportRequested
    /// A report WAS requested from a capable runner. `Some` carries the parsed
    /// summary; `None` means the file was absent / unreadable / unparseable —
    /// the host aborted before flushing, or wrote a truncated report.
    | ReportRequested of report: Flakiness.TestReport option

/// Decide a single project's verdict. The structured test report (when present
/// and parseable) is AUTHORITATIVE for pass/fail; the process exit code is only
/// a tie-break when there is no usable report. This inverts the prior logic
/// (exit-code-only), which produced false REDs: a test host that exits non-zero
/// during a dirty shutdown (e.g. the Microsoft.Testing.Platform exit-7 flake)
/// after flushing a clean report was reported as "Tests failed" with zero named
/// tests, while `test-rerun` came back green.
///
/// Precedence (apphost-missing / zero-match-under-filter are handled by the
/// caller BEFORE this — they are not test outcomes):
///   1. report has any failed/other result → `TestsFailed` (red). Exit irrelevant.
///   2. report is all-clear (no failed/other) AND ran ≥1 test → `TestsPassed`
///      (green) EVEN IF the process exited non-zero — the flake case.
///   3. no usable report (absent / unparseable / no summary) AND exit ≠ 0:
///        - report WAS requested from a capable runner → `TestsErrored`: the host
///          aborted before writing results; nothing was verified. Never green,
///          never the misleading "tests failed".
///        - report NOT requested (unknown runner) → exit code is the only signal
///          we have → `TestsFailed` (unchanged behaviour, no regression).
///   4. no usable report AND exit = 0 → trust the clean exit → `TestsPassed`.
///   A `summary.tests == 0` report that reaches here is an UNFILTERED zero-test
///   run (the filtered case was handled upstream) — a real misconfiguration, so
///   it falls to the exit-code tie-break rather than going green.
///
/// TODO(approach C, deferred): once the set of benign shutdown exit codes is
/// confirmed per runner/version, outcome 2 could additionally require the exit
/// code to be 0 or a whitelisted shutdown code (e.g. MTP's 7), treating other
/// non-zero exits with a clean report as errored. Left out for now — the exact
/// benign code is runner/version-specific and a report that positively shows
/// zero failures is stronger evidence than the exit number.
let internal classifyTestOutcome
    (evidence: ReportEvidence)
    (wasFiltered: bool)
    (elapsed: System.TimeSpan)
    (outcome: ProcessOutcome)
    : TestResult =
    match outcome with
    | ProcessOutcome.TimedOut(after, output, kill) ->
        // A timeout KILL is a real "stuck" signal; a partial report it may have
        // flushed must not override it. Keep it distinct (unchanged).
        //
        // This arm renders the tail itself rather than going through `outputOf`, so it
        // must append `renderKill` explicitly — otherwise a test-runner tree we FAILED
        // to kill would be reported as a plain "stuck" timeout while it kept running,
        // holding the test DB / the port / the lock that makes the NEXT run fail too.
        TestsTimedOut(renderOutput output + renderKill kill, after, wasFiltered, elapsed)
    | _ ->
        let output = outputOf outcome
        let succeeded = isSucceeded outcome

        match evidence with
        | ReportRequested(Some r) when r.Failed > 0 || r.Other > 0 ->
            // Outcome 1: the report names failing/errored tests — authoritative red.
            TestsFailed(output, wasFiltered, elapsed)
        | ReportRequested(Some r) when Flakiness.TestReport.allClear r && r.Total > 0 ->
            // Outcome 2: report parsed, zero non-pass results, ≥1 test ran →
            // GREEN even if the process exited non-zero (the dirty-shutdown flake).
            TestsPassed(output, wasFiltered, elapsed)
        | ReportRequested(Some _) ->
            // Report parsed but Total == 0 — an unfiltered zero-test run. Real
            // problem; defer to the exit code (preserves "empty suite is red").
            if succeeded then
                TestsPassed(output, wasFiltered, elapsed)
            else
                TestsFailed(output, wasFiltered, elapsed)
        | _ when succeeded ->
            // Outcome 4: clean exit and no usable report (none requested, or
            // requested-but-absent) → trust the pass.
            TestsPassed(output, wasFiltered, elapsed)
        | ReportRequested None ->
            // Outcome 3: we asked a capable runner for a report, the process
            // exited non-zero, and none is parseable → the host aborted before
            // writing results. Errored — never green, never "tests failed".
            TestsErrored "test host exited non-zero but wrote no parseable report — nothing verified"
        | NoReportRequested ->
            // Unknown runner we never asked for a report: exit code is the only
            // signal. Preserve today's behaviour (no false-errored regression).
            TestsFailed(output, wasFiltered, elapsed)

/// Split runner args on whitespace into tokens (empty entries removed).
let private argTokens (args: string) : string[] =
    if String.IsNullOrWhiteSpace args then
        [||]
    else
        args.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

/// The value following `--project`/`-p` in the tokenized args, quote-trimmed.
/// Shared by `tryApphostPresent` and `detectCtrfCapable` (both derive a project
/// from the runner command line the same way).
let private projectFlagValue (tokens: string[]) : string option =
    tokens
    |> Array.tryFindIndex (fun t -> t = "--project" || t = "-p")
    |> Option.bind (fun i -> if i + 1 < tokens.Length then Some tokens.[i + 1] else None)
    |> Option.map (fun raw -> raw.Trim('"'))

/// Derive the runner's build-output target (project file, dir, assembly name,
/// `bin/Debug`) from its `--project` arg, or `None` when no `--project`/`-p`
/// token is present (a custom, non-`dotnet run` command). The `--project` value
/// may point at a `.fsproj`/`.csproj` file OR a directory; the assembly name
/// defaults to the project/dir leaf — matching `ProjectGraph.GetCanonicalDllPath`,
/// which uses the project file's base name. Shared by `tryApphostPresent`
/// (presence) and `ArtifactFreshness.stale` (freshness) so this
/// fsproj-or-directory derivation has ONE definition.
let internal deriveProjectBin (args: string) (repoRoot: string) : ArtifactFreshness.RunnerTarget option =
    projectFlagValue (argTokens args)
    |> Option.map (fun proj ->
        // Resolve to an absolute path (relative paths are repoRoot-relative).
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        let projectFile, projDir, assemblyName =
            if
                File.Exists abs
                && (abs.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || abs.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            then
                Some abs, Path.GetDirectoryName(abs), Path.GetFileNameWithoutExtension(abs)
            else
                // Treat as a directory. The assembly name conventionally matches
                // the directory leaf; if a single project file lives there, prefer
                // that file's base name.
                let dir = abs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                let projFile =
                    if Directory.Exists dir then
                        Directory.GetFiles(dir, "*.fsproj")
                        |> Array.append (Directory.GetFiles(dir, "*.csproj"))
                        |> Array.tryHead
                    else
                        None

                projFile,
                dir,
                (projFile
                 |> Option.map Path.GetFileNameWithoutExtension
                 |> Option.defaultValue (Path.GetFileName dir))

        { ArtifactFreshness.ProjectFile = projectFile
          ArtifactFreshness.ProjectDir = projDir
          ArtifactFreshness.AssemblyName = assemblyName
          ArtifactFreshness.BinDir = Path.Combine(projDir, "bin", "Debug") })

/// Issue 2 — STRUCTURAL apphost-missing detection. On a cold daemon a
/// `dotnet run --project <proj> --no-build` can be launched before the build
/// plugin produced that project's apphost binary; `dotnet run` then fails to
/// spawn it and exits non-zero. That is an ORDERING bug, never a test failure.
///
/// Rather than sniff localized OS error text out of the runner output (fragile
/// to locale and SDK phrasing — see `looksLikeApphostMissing`, kept only as a
/// defensive fallback), we derive the apphost binary path from the runner's
/// `--project` arg and check `File.Exists` BEFORE/around the launch. The
/// apphost is the extension-less sibling of the canonical
/// `<projDir>/bin/Debug/<tfm>/<assemblyName>.dll` (`.exe` on Windows). We don't
/// know the TFM without the project graph here, so we glob every
/// `bin/Debug/*/` TFM dir for the assembly. If NO apphost is found for a
/// derivable project, that absence IS the "apphost not yet produced" signal.
/// Presence only — `ArtifactFreshness.stale` is the freshness complement (a
/// PRESENT but out-of-date artifact).
///
/// Returns:
///   Some true  — project derivable AND apphost present
///   Some false — project derivable AND apphost absent (the deferred signal)
///   None       — could not derive a project from args (e.g. a non-`dotnet run`
///                custom command); caller falls back to the output sniff.
let internal tryApphostPresent (args: string) (repoRoot: string) : bool option =
    deriveProjectBin args repoRoot
    |> Option.map (fun target ->
        // The apphost lives at bin/Debug/<tfm>/<assemblyName>(.exe). We don't
        // know the TFM, so scan every TFM output dir for the extension-less
        // binary (Unix) or the `.exe` (Windows); no build output yet ⇒ no TFM
        // dirs ⇒ apphost definitionally absent.
        ArtifactFreshness.tfmOutputDirs target.BinDir
        |> Array.exists (fun tfmDir ->
            File.Exists(Path.Combine(tfmDir, target.AssemblyName))
            || File.Exists(Path.Combine(tfmDir, target.AssemblyName + ".exe"))))


/// Detect whether a test runner emits a CTRF report we can parse. The
/// `--report-ctrf` family is provided by xUnit.v3's runner (NOT the MTP core),
/// and an UNSUPPORTED `--report-*` flag is fatal — the runner exits "invalid
/// command line" and runs zero tests. So we positively identify xUnit.v3 from
/// the test project file's package references before injecting the flag.
///
/// Returns `Some true`/`Some false` when a project file is located and read
/// (mentions xunit or not), and `None` when no project file can be derived from
/// the args (a custom runner, or a `--project`-less command) — the caller treats
/// `None` as "fall back to the broad dotnet heuristic", preserving existing
/// behaviour without risking a fatal flag on a positively-non-xunit runner.
let internal detectCtrfCapable (args: string) (repoRoot: string) : bool option =
    let tokens = argTokens args

    let looksLikeProjectFile (t: string) =
        t.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)

    // The project hint: the value after --project/-p, else any token that is
    // itself a project file path (e.g. `dotnet test path/to/Proj.fsproj`).
    let projArg =
        projectFlagValue tokens
        |> Option.orElse (
            tokens
            |> Array.tryFind looksLikeProjectFile
            |> Option.map (fun raw -> raw.Trim('"'))
        )

    let resolveProjectFile (proj: string) : string option =
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        if File.Exists abs && looksLikeProjectFile abs then
            Some abs
        elif Directory.Exists abs then
            Directory.GetFiles(abs, "*.fsproj")
            |> Array.append (Directory.GetFiles(abs, "*.csproj"))
            |> Array.tryHead
        else
            None

    projArg
    |> Option.bind resolveProjectFile
    |> Option.bind (fun projFile ->
        try
            // xUnit is a DIRECT package reference in a test project, so a text
            // probe of the project file is sufficient and build-independent.
            Some((File.ReadAllText projFile).Contains("xunit", StringComparison.OrdinalIgnoreCase))
        with _ ->
            None)

/// Issue 1/2 — defensive fallback apphost-missing classifier, used ONLY when
/// `tryApphostPresent` can't derive a project from the runner args (custom,
/// non-`dotnet run` commands). On a cold daemon a `dotnet run --project <proj>
/// --no-build` launched before the build plugin produced the apphost fails to
/// spawn it and surfaces the .NET host's start-process error, exiting non-zero.
/// That non-zero exit is an ORDERING bug, never a test failure.
///
/// This classifier distinguishes that launch failure from a genuine non-zero
/// test exit. A real xUnit/MTP failure carries `failed <name>` lines and a
/// `failed:`/`Test run summary` block; the apphost-launch failure carries the
/// host's "An error occurred trying to start process …" / "No such file or
/// directory" signature and NO test-summary block. The match is deliberately
/// conservative: when in doubt we treat output as a real failure (never
/// silence a red). Pure + internal so both branches are unit-testable without
/// a live daemon.
let internal looksLikeApphostMissing (output: string) : bool =
    if String.IsNullOrWhiteSpace output then
        false
    else
        let lower = output.ToLowerInvariant()

        // Signatures the .NET host emits when it cannot launch the apphost the
        // build was supposed to produce.
        let hasStartProcessFailure =
            lower.Contains("an error occurred trying to start process")
            || (lower.Contains("no such file or directory")
                && (lower.Contains("apphost")
                    || lower.Contains("/bin/")
                    || lower.Contains("\\bin\\")))
            || lower.Contains("apphost_version not found")

        // A genuine test run always emits a summary / per-test `failed ` lines.
        // Their PRESENCE means the runner actually executed tests, so this is a
        // real failure, not a launch race — don't misclassify it.
        let looksLikeRealTestFailure =
            lower.Contains("test run summary")
            || lower.Contains("failed:")
            || (output.Split('\n')
                |> Array.exists (fun l -> l.TrimStart().StartsWith("failed ")))

        hasStartProcessFailure && not looksLikeRealTestFailure

/// Split a fully-qualified test name into (class, method): the LAST dotted segment is
/// the method, the one before it the class. A name with no dot is its own class.
///
/// ONE derivation, used by BOTH sides of the ledger: the class a red is FILED under
/// (`parseFailedTests`, off the runner's `failed <name>` console lines) and the class a
/// run's report VINDICATES (`passedClassesOfReport`, off the CTRF `name` field). The two
/// read the same runner's rendering of the same fully-qualified name, so sharing the
/// split is what makes retirement match filing — an xUnit display name that defeats the
/// heuristic defeats it identically on both sides, and a key that fails to match simply
/// leaves the red standing.
let internal splitTestName (name: string) : string * string =
    let parts = name.Split('.')

    if parts.Length >= 2 then
        parts.[parts.Length - 2], parts.[parts.Length - 1]
    else
        name, name

/// Parse "failed Namespace.Class.Method (Xms)" lines from test output.
/// Returns (className, methodName, fullLine) tuples.
let parseFailedTests (output: string) : (string * string * string) list =
    output.Split('\n')
    |> Array.choose (fun line ->
        let trimmed = line.Trim()

        if trimmed.StartsWith("failed ") then
            // Strip "failed " prefix and optional trailing timing "(Xms)"
            let rest = trimmed.Substring(7).Trim()

            let name =
                match rest.LastIndexOf(" (") with
                | -1 -> rest
                | i -> rest.Substring(0, i)

            let className, methodName = splitTestName name
            Some(className, methodName, trimmed)
        else
            None)
    |> Array.toList

/// The classes ONE project's CTRF report proves RAN AND PASSED in this run
/// (AUTOMATION-225) — the receipt `RunCoverage.ofRun` reads for a raw `--filter`
/// passthrough, whose launch REQUEST claims nothing.
///
/// A class is claimed only when the report holds at least one PASSED test for it and
/// NO failed/other test for it. Everything else fails CLOSED — an empty set, which
/// leaves every red exactly as it was:
///
///   * no parseable SUMMARY block → the report was truncated or never flushed;
///   * per-test array shorter (or longer) than the summary's total → the array is
///     INCOMPLETE. A real report omits per-test entries for tests that threw a raw
///     (non-assertion) exception while still counting them in the summary, so a class
///     could look all-green here while one of its tests exploded. Counting is the only
///     way to see the omission, so an array that does not account for every test in the
///     summary is not evidence about ANY class in it;
///   * a class with a failed/other entry, or with no passed entry at all (all skipped)
///     → not claimed.
///
/// Skips are neutral rather than disqualifying, because the unfiltered arm they must
/// agree with is: a full run whose report contains skips still returns
/// `CoveredWholeProject` and clears everything. A rule that let a skip block a class
/// here would be stricter than the whole-project path it is a refinement of.
let internal passedClassesOfReport (json: string) : Set<string> =
    match Ctrf.trySummary json with
    | None -> Set.empty
    | Some summary ->
        let records = Flakiness.parseCtrfTests json

        if records.IsEmpty || records.Length <> summary.Total then
            Set.empty
        else
            records
            |> List.groupBy (fun r -> fst (splitTestName r.Name))
            |> List.choose (fun (cls, forClass) ->
                let anyPassed = forClass |> List.exists (fun r -> r.Outcome = Flakiness.Passed)

                let anyUnvindicated =
                    forClass
                    |> List.exists (fun r ->
                        match r.Outcome with
                        | Flakiness.Failed
                        | Flakiness.Other -> true
                        | Flakiness.Passed
                        | Flakiness.Skipped -> false)

                if anyPassed && not anyUnvindicated then Some cls else None)
            |> Set.ofList

/// The per-project passed-class evidence for a completed run, read back from the CTRF
/// reports THAT RUN wrote (`.fshw/test-runs/<runId>/<Project>.ctrf.json`).
///
/// Scoped by RUN DIRECTORY, so a previous run's report can never be mistaken for this
/// one's: the directory IS the run. A project with no readable report contributes no
/// entry, and a project absent from the map claims nothing — the absence of evidence is
/// never evidence of a pass.
let internal passedClassesOfRun (repoRoot: string) (runId: Guid) : Map<string, Set<string>> =
    Ctrf.reportsForRun repoRoot runId
    |> List.choose (fun report ->
        let json =
            try
                Some(File.ReadAllText report.Path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match json |> Option.map passedClassesOfReport with
        | Some classes when not (Set.isEmpty classes) -> Some(report.Project, classes)
        | _ -> None)
    |> Map.ofList

/// The reds a completed run FOUND, as outstanding failures (AUTOMATION-125). Pure:
/// the same run always yields the same set, so the ledger projection is a function
/// of the evidence and nothing else.
///
/// A red is attributed to a CLASS only when the runner named one AND the result is a
/// genuine test failure. A TIMEOUT is deliberately project-level (`Class = None`)
/// even when its output names tests: a project killed for being stuck is a fact
/// about the PROJECT, and a later class-filtered green must not be allowed to
/// vindicate it. Deferred/errored projects are project-level for the same reason —
/// nothing about them was verified.
let internal failuresOf (classFiles: Map<string, string>) (results: TestResults) : OutstandingFailure list =
    let synthetic (project: string) = $"<tests/%s{project}>"

    results.Results
    |> Map.toList
    |> List.collect (fun (project, result) ->
        let projectLevel (entry: ErrorLedger.ErrorEntry) =
            { Project = project
              Class = None
              File = synthetic project
              Entry = entry }

        match result with
        | TestsFailed(output, _, _)
        | TestsTimedOut(output, _, _, _) ->
            // A timeout is never attributable to one class (see above).
            let isTimeout = TestResult.isTimedOut result
            let parsed = parseFailedTests output

            if parsed.IsEmpty then
                [ projectLevel (ErrorLedger.ErrorEntry.errorWithDetail $"Tests failed in %s{project}" output) ]
            else
                parsed
                |> List.map (fun (className, _methodName, line) ->
                    let file =
                        classFiles |> Map.tryFind className |> Option.defaultValue (synthetic project)

                    { Project = project
                      Class = (if isTimeout then None else Some className)
                      File = file
                      Entry = ErrorLedger.ErrorEntry.errorWithDetail line output })
        | TestsDeferred reason ->
            // NOT a test failure — surface an honest "waiting on build / did not
            // run" diagnostic at `Deferred` severity so the verdict is NON-green
            // (nothing was verified) yet NOT a red: the CLI routes any
            // `Deferred`-severity entry to `Incomplete`/exit 2, distinct from the
            // exit 1 a real failure earns. This entry still joins the Outstanding
            // failure list, so cache participation stays refused (a deferred run is
            // never replayed as a green) — the severity governs the VERDICT, the
            // outstanding LIST governs the CACHE, and the two are decoupled.
            [ projectLevel (
                  ErrorLedger.ErrorEntry.deferredWithDetail
                      $"%s{project}: waiting on build — %s{reason}"
                      $"The %s{project} test project did not run because its build artifact (apphost) was not produced. Tests were NOT executed, so this cycle cannot be reported as passing. This is a build-ordering issue, not a test failure."
              ) ]
        | TestsErrored reason ->
            // NOT a test failure (no test was shown to fail) and NOT a pass
            // (nothing was verified) — an honest "errored" diagnostic so the
            // verdict is non-green without the misleading "Tests failed in X".
            [ projectLevel (
                  ErrorLedger.ErrorEntry.errorWithDetail
                      $"%s{project}: errored — %s{reason}"
                      $"The %s{project} test host exited non-zero but wrote no parseable test report, so NO pass/fail verdict could be derived — nothing was verified. This is NOT a reported test failure and NOT a pass; re-run (e.g. `dotnet fshw test-rerun`). A run that only goes green on retry is itself a real failure, so this stays non-green."
              ) ]
        | TestsPassed _ -> [])

/// Rewrite this plugin's whole slice of the error ledger to be exactly what is still
/// OUTSTANDING (AUTOMATION-125).
///
/// `ClearAllErrors` (== `ClearPlugin "test-prune"`) is still how the slate is wiped —
/// but it is now always paired with a re-report of EVERYTHING the plugin still owes:
///
///   * every outstanding red, including the ones carried from earlier runs that this
///     one did not cover. The previous code cleared the slate and re-reported only
///     THIS run's failures, so a narrower run that passed erased reds it had never
///     executed;
///   * every unanalysable-file warning (AUTOMATION-113). The clear wiped those too, so
///     the first test run after an analysis failure silently dropped the warning that
///     is supposed to deny the check its green verdict — the same defect, on a
///     different diagnostic.
///
/// Clearing-and-re-reporting is the ONLY path to the ledger, so an entry can disappear
/// only by leaving the outstanding set: a red needs a run that COVERED it, a warning
/// needs the file to analyse cleanly.
let private reportOutstanding
    (ctx: PluginCtx<TestPruneMsg>)
    (unanalyzable: Map<string, UnanalyzableFile>)
    (outstanding: OutstandingFailure list)
    =
    ctx.ClearAllErrors()

    let failureEntries = outstanding |> List.map (fun f -> f.File, f.Entry)

    let unanalyzableEntries =
        unanalyzable
        |> Map.toList
        |> List.map (fun (_, u) -> u.File, unanalyzableFileDiagnostic u.RelPath u.Reason)

    failureEntries @ unanalyzableEntries
    |> List.groupBy fst
    |> List.iter (fun (file, entries) -> ctx.ReportErrors file (entries |> List.map snd))

/// Execute test configs with optional affected classes for filtering.
/// Handles beforeRun, coveragePaths, process execution, result storage.
/// rawFilter is a passthrough filter string (from run-tests command), bypassing the template.
///
/// Emission contract (when `ctx` is Some):
///   1. `TestRunStarted` once, before any group begins.
///   2. `TestProgress` once per group as it completes, carrying only that
///      group's projects as a delta.
///   3. `TestRunCompleted` once, after all groups finish, carrying the full
///      cumulative Results plus an Outcome.
/// All three share a single RunId generated at the start of the run.
/// When `ctx` is None (e.g. invoked from a one-off command), no lifecycle
/// events fire; the caller just gets back the final TestResults. ctx=None
/// also disables the skip-on-stale shortcut so manual runs aren't deadlocked
/// by a stuck dirty bit — see the staleness branch below.
let private flakinessHistoryPath (repoRoot: string) =
    Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-history.json")

let private executeTests
    (db: Database)
    (ctx: PluginCtx<'msg> option)
    (repoRoot: string)
    (beforeRun: (unit -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (afterRun: (TestResults -> unit) option)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    =
    async {
        Logging.info "test-prune" $"executeTests starting with %d{configs.Length} configs"
        let sw = Stopwatch.StartNew()
        let runId = Guid.NewGuid()

        // The run's own directory. Created NOW — before anything runs — so that a run
        // which executes but reports nothing is distinguishable from a run that never
        // happened: the first leaves an empty directory, the second leaves none.
        let runDir = Ctrf.runDir repoRoot runId

        try
            Directory.CreateDirectory(runDir) |> ignore
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex ->
            Logging.warn "test-prune" $"could not create the run directory %s{runDir}: %s{ex.Message}"

        // Launch-liveness deadline (AUTOMATION-65 QA finding). Between a test
        // config's spawn and its first sign of life, `ProcessBounds.streaming`
        // bounds the wait so an overloaded box / a sleep-killed child can never
        // wedge the plugin at `Running` forever. Default 5 min, overridable with
        // `FSHW_LAUNCH_DEADLINE_SEC` (read once per run).
        let launchDeadline =
            Environment.GetEnvironmentVariable "FSHW_LAUNCH_DEADLINE_SEC"
            |> Option.ofObj
            |> resolveLaunchDeadline

        let isFilteredRun = not affectedClassesByProject.IsEmpty || Option.isSome rawFilter

        let primaryLabel =
            if isFilteredRun then
                $"running %d{configs.Length} selected test projects"
            else
                $"running full suite (%d{configs.Length} projects)"

        let startedAt = DateTime.UtcNow

        ctx |> Option.iter (fun c -> c.StartSubtask PrimarySubtaskKey primaryLabel)
        // EmitTestRunStarted moved to caller (so the synchronous Custom
        // TestsFinished handler can emit it inside the cache-write capture
        // window). Caller receives `started` in the returned tuple.
        let started: TestRunStarted = { RunId = runId; StartedAt = startedAt }

        match beforeRun with
        | Some setup ->
            Logging.info "test-prune" "Running beforeRun setup..."
            setup ()
            Logging.info "test-prune" "beforeRun complete"
        | None -> ()

        let groups = configs |> List.groupBy (fun c -> c.Group)

        // Memo for the freshness gate, shared across every config of the run:
        // each project in a closure is walked and parsed at most ONCE, even when
        // several test projects depend on it (their closures overlap heavily).
        // Thread-safe — the groups below run in parallel.
        let freshnessCache = ArtifactFreshness.Cache()

        // Cumulative results built up as groups complete. Mutable under a lock
        // so concurrent group completions see a consistent prefix-chain. Per-
        // group deltas are emitted via TestProgress; the final cumulative is
        // carried by TestRunCompleted (and returned to non-daemon callers).
        let mutable cumulative: Map<string, TestResult> = Map.empty
        let accumulatorLock = obj ()

        // Raw-cobertura ingest inputs collected across the parallel per-project
        // runs. Each entry is (rawCoberturaPathThisProjectWrote,
        // sharedCoberturaOutputPath). Ingest+emit runs SERIALLY after
        // Async.Parallel completes so concurrent group completions never race on
        // the DB write or the single shared output file.
        let mutable coverageRawPaths: string list = []
        // Every project's CoveragePaths.Cobertura is the same run-wide shared path;
        // captured once so the DB is emitted to a single file after the run.
        let mutable coverageOutput: string option = None
        let coverageRawPathsLock = obj ()

        // Per-test flakiness records, COLLECTED here and written ONCE after the
        // parallel section — exactly like `coverageRawPaths` above, and for both of
        // the same reasons. `Flakiness.appendRecords` is a full parse + full rewrite
        // of the whole history file, so a per-config call from inside the parallel
        // group body would mean one parse+rewrite cycle per project, AND a
        // read-modify-write racing itself across parallel groups (two projects
        // finishing together each load the same history, and the second writer drops
        // the first's records).
        let mutable flakinessRecords: Flakiness.TestRunRecord list = []
        let flakinessLock = obj ()

        let foldAndEmit (groupOutput: (string * TestResult) list) =
            lock accumulatorLock (fun () ->
                for (k, v) in groupOutput do
                    cumulative <- Map.add k v cumulative

                ctx
                |> Option.iter (fun c ->
                    c.EmitTestProgress
                        { RunId = runId
                          NewResults = Map.ofList groupOutput }))

        let! groupResults =
            groups
            |> List.map (fun (_, groupConfigs) ->
                async {
                    let mutable results = []

                    for config in groupConfigs do
                        // Collect extra args (filter + coverage) to append
                        let extraArgs = ResizeArray<string>()

                        // Template-based class filter (from impact analysis).
                        // When the map is non-empty but has no classes for this project,
                        // skip the project entirely (impact analysis found no relevant tests).
                        let skipProject =
                            not affectedClassesByProject.IsEmpty
                            && not (affectedClassesByProject |> Map.containsKey config.Project)

                        // FRESHNESS GATE (AUTOMATION-122; mirrors BuildPlugin.verifyArtifactsFresh,
                        // ADR-008). Would `--no-build` execute bits that don't match the sources?
                        // Judged over THIS project's own transitive closure — its sources, its
                        // dependencies' assemblies, and the content/fixture items copied into its
                        // output dir. Its predecessor compared against the newest source ANYWHERE
                        // in the repo, so any edit to any leaf project condemned every project
                        // outside that edit's closure — and no plain build could clear it, because
                        // MSBuild correctly declines to relink an unaffected project. Every finding
                        // here names a file pair a plain `dotnet build` will re-emit.
                        let staleInput =
                            if skipProject then
                                None // not running it anyway — don't pay for the walk
                            else
                                deriveProjectBin config.Args repoRoot
                                |> Option.bind (ArtifactFreshness.stale freshnessCache)

                        match skipProject, staleInput with
                        | true, _ ->
                            Logging.info "test-prune" $"Skipping %s{config.Project} — no affected classes"

                            // Skipped-due-to-impact-analysis is the strongest form of filtering;
                            // its coverage contribution is "nothing new", so mark as filtered.
                            // Elapsed=Zero since we didn't actually run the test runner.
                            results <- (config.Project, TestsPassed("", true, TimeSpan.Zero)) :: results
                        | false, Some stale ->
                            // `dotnet run --no-build` would execute STALE bits and report a verdict
                            // (pass OR fail) that doesn't match the sources — the false-green this
                            // exists to prevent. Runs PRE-launch, independent of exit code: the old
                            // apphost check only fired on a FAILED launch (`detectApphostMissing`
                            // short-circuits on a clean exit), so a stale apphost that exited 0
                            // sailed through as `TestsPassed`. DEFER without launching — exactly the
                            // "waiting on build" signal a MISSING apphost yields — so a stale
                            // artifact can never produce a passing verdict. (A MISSING apphost stays
                            // on the launch+retry+defer path below: a build in flight may still land
                            // it; a stale one won't refresh without a real build.)
                            let reason = ArtifactFreshness.describe stale

                            Logging.warn
                                "test-prune"
                                $"%s{config.Project}: build output is STALE — %s{reason}. Deferring as 'waiting on build', NOT running --no-build on stale code (mirrors BuildPlugin.verifyArtifactsFresh; ADR-008)"

                            ctx
                            |> Option.iter (fun c -> c.Log $"{config.Project}: waiting on build ({reason})")

                            results <-
                                (config.Project,
                                 TestsDeferred
                                     $"build output is stale — %s{reason}; would run --no-build on stale code")
                                :: results
                        | false, None ->
                            let filterArgs = buildFilterArgs config affectedClassesByProject

                            match filterArgs with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            // Raw passthrough filter (from run-tests command)
                            match rawFilter with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            let wasFiltered = Option.isSome filterArgs || Option.isSome rawFilter

                            // Resolve per-project coverage paths (if coverage is configured for
                            // this project). wasFiltered determines which file coverlet writes
                            // to; the post-test step reads those files back to produce cobertura.
                            let projectCoveragePaths =
                                coveragePaths |> Option.bind (fun fn -> fn config.Project)

                            match projectCoveragePaths with
                            | Some paths -> extraArgs.Add(buildCoverageArgs paths wasFiltered)
                            | None -> ()

                            // xUnit.v3's runner supports `--report-ctrf`, which fshw
                            // reads back as the AUTHORITATIVE pass/fail verdict (and for
                            // flakiness history). An UNSUPPORTED `--report-*` flag is
                            // FATAL (the runner exits "invalid command line" and runs
                            // zero tests), so injection is scoped: `Disabled` never
                            // injects, `Ctrf` always does, `AutoDetect` injects iff the
                            // runner is detected as xUnit (from the project's package
                            // refs) and otherwise falls back to the broad "is a dotnet
                            // command" heuristic — non-dotnet test fixtures (sleep, echo)
                            // are thereby never given the flag.
                            let isDotnetCommand (cmd: string) =
                                let leaf = Path.GetFileNameWithoutExtension(cmd)
                                leaf = "dotnet"

                            let shouldRequestCtrf =
                                match config.ReportVerificationFormat with
                                | Disabled -> false
                                | Ctrf -> true
                                | AutoDetect ->
                                    match detectCtrfCapable config.Args repoRoot with
                                    | Some capable -> capable
                                    | None -> isDotnetCommand config.Command

                            // ONE DIRECTORY PER RUN (AUTOMATION-129). The reports of a run
                            // live in `.fshw/test-runs/<runId>/`, and NOTHING ELSE does — so
                            // membership is a fact about where a file IS, not an inference
                            // from when it was written. A shared pile with no manifest is
                            // unreadable in BOTH directions: you cannot tell which files are
                            // yours (presence), and an empty listing could mean "nothing ran"
                            // or "cleaned up" or "wrong glob" (absence). Two capable readers
                            // misread this directory within an hour of each other.
                            //
                            // The run-dir is created whether or not any project reports, so an
                            // executed run that produced nothing leaves an EMPTY DIRECTORY —
                            // a stated fact — rather than a silence to be decoded.
                            let ctrfPath =
                                if shouldRequestCtrf then
                                    Directory.CreateDirectory(runDir) |> ignore
                                    // The dir already names the run, so the file need only name
                                    // the project. No guid to guess at, nothing to parse.
                                    let ctrfName = $"{config.Project}{Ctrf.ReportSuffix}"

                                    extraArgs.Add(
                                        $"--report-ctrf --report-ctrf-filename {ctrfName} --results-directory \"{runDir}\""
                                    )

                                    Some(Path.Combine(runDir, ctrfName))
                                else
                                    None

                            let finalArgs =
                                if extraArgs.Count > 0 then
                                    let extra = String.concat " " extraArgs
                                    $"%s{config.Args} %s{extra}"
                                else
                                    config.Args

                            Logging.info "test-prune" $"Running: %s{config.Command} %s{finalArgs}"

                            let logToCtx msg = ctx |> Option.iter (fun c -> c.Log msg)

                            let timeoutSpan =
                                match config.TimeoutSec with
                                | Some s -> TimeSpan.FromSeconds(float s)
                                | None -> System.Threading.Timeout.InfiniteTimeSpan

                            let projectSw = Stopwatch.StartNew()

                            // A test runner STREAMS (discovery banner, progress, per-test
                            // lines), so its first byte is a sound liveness proof and the
                            // launch deadline can bound the spawn even when the config sets
                            // no TimeoutSec at all.
                            let runOnce =
                                async {
                                    return
                                        runProcess
                                            config.Command
                                            finalArgs
                                            repoRoot
                                            config.Environment
                                            (ProcessBounds.streaming timeoutSpan launchDeadline)
                                }

                            // Issue 2: STRUCTURAL apphost-missing detection. Prefer
                            // a `File.Exists` check on the derived apphost binary
                            // over sniffing localized OS error text. When the
                            // project isn't derivable from the runner args (custom,
                            // non-`dotnet run` command), fall back to the output
                            // sniff. `outcome` is the process result whose output
                            // the fallback inspects.
                            let detectApphostMissing (outcome: ProcessOutcome) : bool =
                                // A clean exit means the apphost ran — never a
                                // launch-ordering problem, regardless of artifacts.
                                if isSucceeded outcome then
                                    false
                                else
                                    match tryApphostPresent config.Args repoRoot with
                                    | Some present -> not present
                                    | None ->
                                        // Not derivable — fall back to the text sniff.
                                        match outcome with
                                        | ProcessOutcome.Failed(_, out) ->
                                            looksLikeApphostMissing (ProcessOutput.text out)
                                        | _ -> false

                            // Issue 1/2: cold-start apphost-missing retry. The
                            // BuildCompleted→TestPrune ordering already gates the
                            // launch on a successful build, but a narrow race can
                            // still fire `--no-build` before the apphost lands. If
                            // the FIRST run looks like an apphost-missing launch
                            // (structural check, or text sniff fallback), wait
                            // briefly for the build to settle and retry ONCE. A
                            // still-missing apphost after the retry is surfaced as
                            // DEFERRED ("waiting on build"), never FAILED.
                            let runTestWithRetry =
                                async {
                                    let! first = runOnce

                                    if detectApphostMissing first then
                                        Logging.warn
                                            "test-prune"
                                            $"%s{config.Project}: apphost missing at launch (build not settled yet); retrying once after a short wait"

                                        do! Async.Sleep 750
                                        let! second = runOnce
                                        return second
                                    else
                                        return first
                                }

                            let! processResult =
                                async {
                                    try
                                        return!
                                            match ctx with
                                            | Some c ->
                                                PluginCtxHelpers.withSubtask
                                                    c
                                                    config.Project
                                                    $"testing {config.Project}"
                                                    runTestWithRetry
                                            | None -> runTestWithRetry
                                    with LaunchStalledException reason ->
                                        // AUTOMATION-65 QA finding — the launch gap. The
                                        // watchdog killed a child that never showed a sign of
                                        // life within the launch deadline (an overloaded spawn
                                        // that went nowhere). Re-raise NAMING the config and
                                        // elapsed so the run's Aborted lifecycle (built by the
                                        // caller's `with ex ->`) carries a legible diagnostic;
                                        // a launch stall means this project NEVER RAN, so the
                                        // whole run must abort → PluginStatus.Failed → `check`
                                        // exits non-green rather than wedging at Running. (A
                                        // child that EXITS — even a sleep-killed one — is NOT a
                                        // stall: the poll observes its exit and it's classified
                                        // normally, non-green, without wedging.)
                                        return
                                            raise (
                                                LaunchStalledException
                                                    $"%s{config.Project}: %s{reason} (after %.0f{projectSw.Elapsed.TotalSeconds}s)"
                                            )
                                }

                            projectSw.Stop()
                            let projectElapsed = projectSw.Elapsed

                            // Issue 1/2: distinguish a still-missing apphost (an
                            // ordering bug) from a genuine non-zero test exit,
                            // via the same structural-with-fallback check.
                            let apphostMissing = detectApphostMissing processResult

                            // A filtered run that matched zero tests in this project is
                            // not a failure (see `isZeroTestsUnderFilter`) — treat it
                            // like an impact-skip.
                            let zeroTestsUnderFilter = isZeroTestsUnderFilter wasFiltered processResult

                            let output = outputOf processResult

                            // Read the structured report ONCE (when one was requested
                            // from a capable runner). It is BOTH the authoritative
                            // pass/fail signal (summary counts) AND the flakiness
                            // source (per-test records). Read BEFORE the verdict so
                            // the REPORT — not the exit code — decides green/red.
                            let reportJson =
                                match ctrfPath with
                                | Some p ->
                                    try
                                        Some(File.ReadAllText p)
                                    with
                                    | :? IOException
                                    | :? UnauthorizedAccessException -> None
                                | None -> None

                            let reportEvidence =
                                match ctrfPath with
                                | None -> NoReportRequested
                                | Some _ -> ReportRequested(reportJson |> Option.bind Flakiness.tryParseReport)

                            let result =
                                if apphostMissing then
                                    // Tests NEVER RAN — the apphost wasn't produced.
                                    // A dedicated `TestsDeferred`, NOT a pass
                                    // (`isPassed`=false → never a silent false-green)
                                    // and NOT a real failure: surfaced as an honest
                                    // "waiting on build" diagnostic. Carries no
                                    // elapsed/wasFiltered, so it never lowers a
                                    // coverage baseline.
                                    TestsDeferred "apphost not produced; tests did not run"
                                elif zeroTestsUnderFilter then
                                    // A filtered run matched no tests here — record
                                    // like an impact-skip (passed + filtered), NOT a
                                    // failure. The ZeroMatchMarker lets `run-tests`
                                    // report `no-tests-matched` distinctly.
                                    TestsPassed(ZeroMatchMarker + output, true, projectElapsed)
                                else
                                    // Report-authoritative verdict (exit code only a
                                    // tie-break). Fixes the false-RED: a dirty-shutdown
                                    // non-zero exit with a clean report is GREEN; a
                                    // non-zero exit with NO parseable report is ERRORED
                                    // (never a misleading "tests failed").
                                    classifyTestOutcome reportEvidence wasFiltered projectElapsed processResult

                            // Log driven off the AUTHORITATIVE verdict (not the raw
                            // exit code) so the console line can never disagree with
                            // the recorded result.
                            match result with
                            | TestsDeferred _ ->
                                logToCtx $"{config.Project}: waiting on build (apphost not yet produced)"

                                Logging.warn
                                    "test-prune"
                                    $"%s{config.Project}: apphost still missing after retry — surfacing as 'waiting on build', not FAILED (a build-ordering issue, never a test failure)"
                            | TestsPassed(o, _, _) when o.StartsWith(ZeroMatchMarker, StringComparison.Ordinal) ->
                                logToCtx $"{config.Project}: no tests matched the filter — skipped"

                                Logging.info
                                    "test-prune"
                                    $"%s{config.Project}: no tests matched the active filter — skipped, not FAILED (a filtered run that selects nothing here is not a test failure)"
                            | TestsPassed _ ->
                                logToCtx $"{config.Project}: passed"
                                Logging.info "test-prune" $"%s{config.Project}: PASSED"
                            | TestsErrored reason ->
                                logToCtx $"{config.Project}: errored — {reason}"

                                Logging.error
                                    "test-prune"
                                    $"%s{config.Project}: ERRORED — %s{reason}. Nothing was verified; this is NOT a test failure and NOT a pass — re-run (e.g. `dotnet fshw test-rerun`)."
                            | TestsFailed _
                            | TestsTimedOut _ ->
                                logToCtx $"{config.Project}: failed"
                                Logging.error "test-prune" $"%s{config.Project}: FAILED"

                            // Report the failure in full: the failing tests, with
                            // messages and traces, are in the RETAINED CTRF report the
                            // verdict points at, and the failure report is logged here
                            // in full.
                            match result with
                            | TestsFailed _
                            | TestsTimedOut _
                            | TestsErrored _ ->
                                for line in formatFailureReport config.Project output do
                                    Logging.error "test-prune" line
                            | _ -> ()

                            // Post-test coverage step: collect this project's raw
                            // runner cobertura for SERIAL ingest after Async.Parallel
                            // (a parallel DB write + shared-file write would race).
                            // Issue 3: a run that never executed (apphost missing)
                            // contributes NO input — a partial/empty file must not
                            // lower coverage. An empty/aborted raw cobertura that IS
                            // collected ingests nothing (parse → [] → no-op), so it
                            // also can't clobber the emitted file.
                            match projectCoveragePaths with
                            | Some paths when not apphostMissing ->
                                let rawPath = if wasFiltered then paths.Partial else paths.Baseline

                                lock coverageRawPathsLock (fun () ->
                                    coverageRawPaths <- rawPath :: coverageRawPaths
                                    coverageOutput <- Some paths.Cobertura)
                            | _ -> ()

                            // Per-test flakiness tracking: reuse the report content
                            // already read for the verdict; COLLECT this project's
                            // per-test records for the single post-parallel write (see
                            // `flakinessRecords`). Best-effort — exceptions never fail
                            // the run.
                            //
                            // The report is RETAINED — the verdict file POINTS at these
                            // reports rather than deleting them once their records are
                            // folded into the flakiness history. `Ctrf.tidyRunsDir`
                            // (post-run) keeps the newest few per project, so retention
                            // stays bounded.
                            match ctrfPath, reportJson with
                            | Some _, Some json ->
                                try
                                    let records = Flakiness.parseCtrfTests json

                                    if not records.IsEmpty then
                                        lock flakinessLock (fun () -> flakinessRecords <- flakinessRecords @ records)
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException
                                | :? JsonException as ex ->
                                    Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"
                            | Some p, None ->
                                // Report requested but unreadable (missing — the host
                                // aborted before flushing — or locked). Nothing to retain;
                                // its ABSENCE already drove the Errored verdict.
                                try
                                    File.Delete p
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException -> ()
                            | None, _ -> ()

                            results <- (config.Project, result) :: results

                    // Atomically fold this group's results into the shared
                    // accumulator and emit a cumulative snapshot. Groups that
                    // complete later will extend (never contradict) this one.
                    foldAndEmit results
                    return results
                })
            |> Async.Parallel

        // groupResults is the per-group return values; we ignore it because
        // `cumulative` (populated under the lock inside foldAndEmit) is the
        // canonical run-wide aggregate.
        groupResults |> ignore

        // Coverage: now that ALL projects have finished, serially ingest each
        // project's raw runner cobertura into the TestPrune DB (max-merged,
        // symbol-relative) and emit the FULL DB ONCE to the single shared
        // cobertura file. Done here, outside Async.Parallel, so there is no
        // DB-write contention and no file-write race on the shared output.
        let collectedRawPaths, sharedOutput =
            lock coverageRawPathsLock (fun () -> List.rev coverageRawPaths, coverageOutput)

        ingestAndEmitCoverage db repoRoot sharedOutput collectedRawPaths

        // Flakiness: ONE parse + ONE rewrite of the history file for the whole run,
        // with every project's records — not one per project, and not racing itself
        // from inside Async.Parallel. Best-effort: the history is a diagnostic, so a
        // failure to record it must never fail the run that produced it.
        let collectedFlakiness = lock flakinessLock (fun () -> flakinessRecords)

        if not collectedFlakiness.IsEmpty then
            try
                Flakiness.appendRecords (flakinessHistoryPath repoRoot) 20 collectedFlakiness
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? JsonException as ex -> Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"

        // Bound what `.fshw/test-runs/` retains, and purge the DEAD `.log` format
        // (AUTOMATION-129). Runs AFTER this run's reports were written, so the
        // evidence the verdict is about to point at is always among the survivors.
        Ctrf.tidyRunsDir repoRoot Ctrf.RetainedRuns

        sw.Stop()

        let finalResults = lock accumulatorLock (fun () -> cumulative)

        let testResults =
            { Results = finalResults
              Elapsed = sw.Elapsed }

        // Outcome = Normal means the run completed naturally. Per-project
        // pass/fail lives in Results; Aborted is reserved for cancellation,
        // timeouts, or crashes (none wired through this path today).
        ctx |> Option.iter (fun c -> c.EndSubtask PrimarySubtaskKey)

        // EmitTestRunCompleted moved to caller (synchronous Custom handler)
        // so it's captured in EmittedEvents for cache replay. Returned as part
        // of the tuple instead.
        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = sw.Elapsed
              Outcome = Normal
              Results = finalResults
              RanFullSuite = TestResult.ranFullSuite finalResults }

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        Logging.info
            "test-prune"
            $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

        return testResults, started, completed
    }

/// FCS cache-poisoning gate. A `FileChecked` whose underlying FCS result
/// reports any Error-severity diagnostic is treated as untrustworthy: cold-
/// start FCS sometimes returns "expected type X but here has type X" for
/// files that compile cleanly once warm, and flushing those poisoned
/// symbols would overwrite the prior good DB snapshot. Gating by severity
/// (not message text) handles both the cold-start race and the user-broke-
/// their-code case identically: in both, we hold the prior DB row instead
/// of replacing it. `ParseOnly` (check aborted) is treated as "no observable
/// errors" so the existing fall-through behaviour is preserved.
///
/// `suppressedCodes` (caller-configured) is merged with per-file `#nowarn`
/// directives via `FcsDiagnosticFilter.allSuppressedCodes` so the gate sees
/// the same filter the user-visible error stream applies in
/// `Daemon.reportFcsDiagnostics`. Without this symmetry the gate trips on
/// codes the user has already silenced (e.g. FS1182 promoted to Error by
/// `<TreatWarningsAsErrors>` but suppressed via `#nowarn "1182"`), killing
/// cache-replay across daemon restarts on every cold scan.
let internal hasFcsErrors (suppressedCodes: Set<int>) (source: string) (state: FileCheckState) : bool =
    match state with
    | FullCheck cr ->
        let allSuppressed = allSuppressedCodes suppressedCodes source

        cr.Diagnostics
        |> Array.exists (fun d ->
            d.Severity = FSharpDiagnosticSeverity.Error
            && not (allSuppressed.Contains d.ErrorNumber))
    | ParseOnly -> false

/// Count of Error-severity diagnostics not in the effective suppression set —
/// used only for the skip-log message so operators have a number to
/// correlate against the FCS-error stream. Must apply the same filter as
/// `hasFcsErrors` so the count matches what the gate decided on.
let internal fcsErrorCount (suppressedCodes: Set<int>) (source: string) (state: FileCheckState) : int =
    match state with
    | FullCheck cr ->
        let allSuppressed = allSuppressedCodes suppressedCodes source

        cr.Diagnostics
        |> Array.filter (fun d ->
            d.Severity = FSharpDiagnosticSeverity.Error
            && not (allSuppressed.Contains d.ErrorNumber))
        |> Array.length
    | ParseOnly -> 0

/// Flush accumulated per-file analysis results to the DB in a single RebuildProjects
/// call. Pure function: takes state, returns updated state.
let private flushPendingAnalysis (db: Database) (state: TestPruneState) =
    let allResults = ResizeArray<AnalysisResult>()

    let mutable newPending = state.PendingAnalysis

    for projectName in state.PendingAnalysis |> Map.toList |> List.map fst do
        match Map.tryFind projectName newPending with
        | Some items ->
            newPending <- Map.remove projectName newPending

            // Use a full record literal (not AnalysisResult.Create) so per-file
            // Attributes and ParentLinks survive the per-project merge.
            // Create defaults both to []; the per-file results above carry them
            // and we'd silently drop them on every flush. Single fold over
            // items to avoid 5 separate passes.
            let syms, deps, tms, attrs, pls =
                (([], [], [], [], []), items)
                ||> List.fold (fun (s, d, t, a, p) r ->
                    (r.Symbols :: s, r.Dependencies :: d, r.TestMethods :: t, r.Attributes :: a, r.ParentLinks :: p))

            let combined =
                { Symbols = syms |> List.rev |> List.concat
                  Dependencies = deps |> List.rev |> List.concat
                  TestMethods = tms |> List.rev |> List.concat
                  Attributes = attrs |> List.rev |> List.concat
                  ParentLinks = pls |> List.rev |> List.concat
                  Diagnostics = AnalysisDiagnostics.Zero }

            Logging.info "test-prune" $"Flushing %d{items.Length} files for %s{projectName} to DB"
            allResults.Add(combined)
        | None -> ()

    if allResults.Count > 0 then
        db.RebuildProjects(Seq.toList allResults)

    // Update in-memory snapshot so subsequent FileChecked reads see the
    // new symbols instead of hitting the DB mid-rebuild.
    let mutable newSnapshot = state.SymbolSnapshot

    for result in allResults do
        for (file, symbols) in result.Symbols |> List.groupBy (fun s -> s.SourceFile) do
            newSnapshot <- Map.add file symbols newSnapshot

    { state with
        PendingAnalysis = newPending
        SymbolSnapshot = newSnapshot }

/// Detect schema-drift errors (stale cache DB lacking a column the current
/// `TestPrune.Core` requires). These surface as SQLite "no such column" /
/// "no column named" messages. Deliberately pure / internal so the caller
/// can unit-test both branches without needing a corrupt DB on disk.
let internal looksLikeSchemaDrift (ex: exn) =
    let msg = ex.Message.ToLowerInvariant()
    msg.Contains("no such column") || msg.Contains("no column named")

/// If `ex` looks like schema drift, delete the cache DB at `dbPath` so the
/// next run rebuilds from scratch. The cache is derivative and safe to
/// regenerate; requiring a user to know which file to delete was the trap
/// this routine exists to close.
let internal tryRepairSchemaDrift (dbPath: string) (ex: exn) =
    if looksLikeSchemaDrift ex && File.Exists dbPath then
        try
            // Delegate to TestPrune.Core — it owns the SQLite-sidecar
            // invariant. Deleting only the main file leaves stale `-wal` /
            // `-shm` sidecars that SQLite may try to "recover" against a
            // freshly created empty DB, producing a 0-byte main DB with no
            // tables — every subsequent INSERT then hits "no such column:
            // <name>".
            TestPrune.Database.deleteCacheFiles dbPath

            Logging.warn
                "test-prune"
                $"Deleted stale cache DB %s{dbPath} after schema-drift error: %s{ex.Message}. Next run will rebuild from scratch."
        with deleteEx ->
            Logging.error
                "test-prune"
                $"Could not delete stale cache DB %s{dbPath}: %s{deleteEx.Message}. Delete it manually and restart the daemon."

/// Delete the FCS check cache (`.fshw/cache/*.json`) for `repoRoot`, returning the number
/// of entries removed. Called when the TestPrune symbol DB was recreated (a schema bump):
/// the persisted FCS cache would otherwise let unchanged files hit the cache and SKIP
/// re-checking, so their symbols never re-flush into the freshly-emptied DB — leaving the
/// symbol graph (and therefore coverage + impact analysis) permanently partial. Clearing it
/// forces the next scan to re-check, and thus re-index, every file. Pure path logic so it
/// is unit-testable without a daemon.
let internal clearFcsCheckCache (repoRoot: string) : int =
    let cacheDir = Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "cache")

    if Directory.Exists cacheDir then
        let files = Directory.GetFiles(cacheDir, "*.json")

        for f in files do
            File.Delete f

        files.Length
    else
        0

/// Build the TestPrune task-cache key for one event, from its three state inputs.
///
/// Every input is a THUNK, and that is load-bearing rather than stylistic.
/// `FileChecked` is the per-FILE, highest-frequency probe — ONE EVENT PER FILE on
/// every scan — and it splices NONE of the three. Taking them by value is exactly
/// how the `dependsOn` hash (a full-repo `SafeWalk` plus a SHA256 of every matched
/// file) came to be computed once per checked file for a value that arm then threw
/// away (AUTOMATION-98; the comment defending it claimed "cacheKey runs once per
/// event, not per file", which is true of BuildCompleted and false of FileChecked).
///
/// Lifted out of the `create` closure so the property is STRUCTURAL: an arm cannot
/// pay for an input it does not name, and a test can prove it by counting calls
/// rather than by reading the code and trusting it.
///
/// `pendingQueueHash`/`dependsOnHash` return `None` for "nothing to contribute",
/// which keeps the corresponding merkle entry OMITTED — the empty-queue,
/// no-dependsOn key stays byte-identical to the pre-feature key, so existing
/// on-disk caches keep hitting.
let internal cacheKeyFor
    (changedSymbolsHash: unit -> string)
    (pendingQueueHash: unit -> string option)
    (dependsOnHash: unit -> string option)
    // AUTOMATION-112. `Some "full"` while the caller has asked for the whole
    // suite; `None` for the impact-filtered inner loop.
    //
    // Load-bearing: without it, the FIRST thing `confirm` does on an unchanged tree is
    // HIT the cache entry written by an earlier impact-filtered run and replay its
    // verdict — a filtered green, laundered into a merge verdict, with no test
    // process ever starting. That is the whole bug, arrived at by a different road.
    //
    // Keeping it `None` (rather than "impact") for the inner loop leaves the ordinary
    // key byte-identical to the pre-feature one, so existing on-disk caches keep
    // hitting; only a full-suite request's key differs. A second such run over the same
    // tree still hits its OWN entry and replays a run that genuinely was full-suite —
    // sound and fast.
    (fullSuiteScopeHash: unit -> string option)
    // AUTOMATION-125. True while a failure no covering run has passed is outstanding.
    // While it is, this plugin does not participate in the task cache AT ALL:
    //   * no REPLAY — a cached green served on a BuildCompleted would skip the handler,
    //     skip the run, and hand back exactly the laundered verdict this ticket is
    //     about (the same reasoning that makes a non-empty pending queue refuse);
    //   * no WRITE — the terminal status of such a run carries a red the run itself
    //     did not produce, and pinning that to a content merkle would let it replay on
    //     a tree that has since been fixed (AUTOMATION-5, in reverse).
    // Read at DISPATCH time, so on a `TestsFinished` it is the PRIOR outstanding set —
    // which is the sound one to gate on: `allPassed` with an empty prior set implies an
    // empty post-run set, so a genuinely green run is still cacheable, and a run that
    // CLEARS a red merely forgoes one cache write.
    (hasOutstandingFailures: unit -> bool)
    // AUTOMATION-161. Has a run in THIS PROCESS produced test evidence — i.e. is there
    // any project this session's `RunCoverage` actually covers?
    //
    // A cached BuildCompleted entry ASSERTS a test result. Serving it skips the handler,
    // so no run happens and no `TestsFinished` lands — which leaves `LastCoverage` empty.
    // The plugin then answers every consumer that reads its STATE (`test-scope`, and
    // through it the verdict file) with "NO TESTS RAN", while its status line
    // simultaneously reports "1 passed (cached)". One run, two surfaces, opposite
    // answers — and both `check` and `confirm` exit 3 on a tree they were just told is
    // green.
    //
    // The key cannot rescue this, because the key does not pin the TREE. On a cold scan
    // `BuildCompleted` is dispatched BEFORE the FCS pass, so `changed-symbols` is empty
    // whatever the tree contains; two different trees that both build clean with an empty
    // queue compute the SAME key. What makes the cache sound in a warm daemon is not the
    // key but the symbol-diff pipeline that runs AFTER it — the entry is a status stamp
    // that a real run supersedes. Across a PROCESS boundary there is no such run to
    // supersede it, and minting a full-suite receipt from a key that cannot see the tree
    // would manufacture merge-grade evidence for a tree nobody tested: the vacuous green,
    // rebuilt inside the fix for it.
    //
    // So fail closed — no replay AND no write — exactly as a non-empty pending queue and
    // an outstanding failure already do. This also RESTORES an invariant the plugin
    // already believed and the cache was quietly defeating: `hasCachedResults` says a
    // cold start with no session baseline must run the full suite to establish one. A
    // cache hit skipped the handler that rule lives in, so the baseline was never run.
    //
    // The warm-daemon inner loop is untouched: once this session's first run lands, there
    // IS coverage, and every later BuildCompleted replays as before.
    (sessionHasTestEvidence: unit -> bool)
    (event: PluginEvent<TestPruneMsg>)
    : ContentHash option =
    let optionalEntry (name: string) (value: string option) =
        match value with
        | Some v -> [ name, v ]
        | None -> []

    // Reuses the same merkle for BuildCompleted and Custom TestsFinished so the
    // cache writes on TestsFinished (synchronous handler — captures EmittedEvents)
    // and the next BuildCompleted hits via the matching key. TestsFinished only
    // fires after BuildSucceeded (BuildFailed short-circuits earlier), so
    // outcome="succeeded" is correct for the Custom path.
    //
    // AUTOMATION-5: salt bumped v1→v2 so any entry written by the prior code (which
    // cached FAILED test verdicts and could replay them on a now-green tree) can
    // never match a key computed here. Orphans legacy poison on disk without needing
    // a manual cache wipe.
    let outcomeKey (buildOutcome: string) =
        FsHotWatch.TaskCache.merkleCacheKey (
            [ "plugin-version", "test-prune-merkle-v2"
              "event", "BuildCompleted"
              "changed-symbols", changedSymbolsHash ()
              "build-outcome", buildOutcome ]
            @ optionalEntry "pending-queue" (pendingQueueHash ())
            @ optionalEntry "depends-on" (dependsOnHash ())
            @ optionalEntry "full-suite-scope" (fullSuiteScopeHash ())
        )

    match event with
    | BuildCompleted BuildSucceeded ->
        // AUTOMATION-95/99. A cache HIT makes the framework replay the cached
        // terminal status and SKIP the handler entirely (PluginFramework
        // `tryReplayCache`) — but this handler is a drain trigger for the
        // pending-verification queue. Replaying a cached verdict while symbols are
        // still unverified is precisely the "verdict nobody earned" bug: observed
        // live as `[task-cache] plugin=test-prune hit=true` on a BuildCompleted, no
        // test run, and `check` green with symbols pending.
        //
        // The key already folds in a queue hash, but that is read at DISPATCH time —
        // and on a scan the queue is mutated afterwards, by the FCS pass that runs
        // after the build. So the key cannot be trusted to notice outstanding work.
        // Refuse to participate in the cache at all while the queue is non-empty:
        // `None` means no replay AND no write, so the handler always runs and always
        // gets its chance to drain.
        //
        // The empty-queue green fast-path (the case the cache exists for) is untouched.
        //
        // An outstanding failure (AUTOMATION-125) refuses participation for the same
        // reason: replaying a cached green while a failing test has never re-passed IS
        // the laundered verdict, arrived at through the cache instead of through a
        // filtered run.
        //
        // And a process with NO test evidence of its own (AUTOMATION-161) refuses for the
        // sharpest version of the same reason: a replay here would have this process
        // ASSERT a test result while its own state records no run — which is precisely
        // the split-brain that made `confirm` (and `check`) exit 3 on a warm tree.
        match pendingQueueHash (), hasOutstandingFailures (), sessionHasTestEvidence () with
        | Some _, _, _
        | _, true, _
        | _, _, false -> None
        | None, false, true -> Some(outcomeKey "succeeded")
    | Custom(TestsFinished(_, completed, _)) ->
        // A FAILED test outcome must never be served from cache as a current
        // verdict. Unlike BuildPlugin — whose result is a pure
        // function of its content-merkle inputs, so replaying a cached failure on an
        // identical tree is correct — a test outcome is NOT pinned by the
        // changed-symbols merkle: the same key recurs after the tree is fixed (or for
        // a flaky test), and a cached `Failed` would then replay as a stale red on a
        // green tree ("green tree read as red"). Field evidence: an 08:35 failure
        // replayed at 10:19/10:49 and through four deploy-preflights on a `failed: 0`
        // tree. Returning None here makes a non-passing run UNCACHEABLE, so
        // `runAndCache` skips the write and the next matching BuildCompleted finds no
        // poisoned entry and re-runs. A fully-passing run still caches (key matches
        // BuildSucceeded) and replays cleanly — the desired green fast-path.
        //
        // §3d also requires the queue to be EMPTY for a green to be cacheable: a green
        // that left symbols queued is not a sound "safe to skip" verdict, so it must
        // re-run rather than replay. The Aborted-outcome / abort short-circuit is
        // covered because an aborted run has empty Results that the all-passed check
        // treats as trivially passing — so we ALSO gate on a non-Aborted outcome here.
        let allPassed = completed.Results |> Map.forall (fun _ r -> TestResult.isPassed r)

        let notAborted =
            match completed.Outcome with
            | Aborted _ -> false
            | Normal -> true

        // AUTOMATION-125 adds the third condition: a run that passed everything IT ran
        // while an earlier, uncovered failure is still outstanding is NOT green (its
        // terminal status is a Failed carrying the carried-over red), so it is not a
        // "safe to skip" verdict and must never be replayed as one.
        //
        // AUTOMATION-161: this arm is deliberately NOT gated on `sessionHasTestEvidence`.
        // It is the WRITE — the only thing that mints the entry the next `BuildCompleted`
        // hits — and it is read at DISPATCH time, when the run this message carries has
        // not yet been folded into state and there is therefore no evidence to see. The
        // key it returns is never used for a LOOKUP: the framework does not replay over a
        // `Custom` message at all (a Custom's payload is not in its key, so a hit there is
        // a collision, not a proof — see `PluginFramework`'s dispatch loop).
        if
            allPassed
            && notAborted
            && (pendingQueueHash ()).IsNone
            && not (hasOutstandingFailures ())
        then
            Some(outcomeKey "succeeded")
        else
            None
    | BuildCompleted(BuildFailed errs) ->
        // Salt bumped v1→v2 in lockstep with the BuildSucceeded key so the two never
        // split across versions; the pending-queue and dependsOn salts mirror it too
        // (the same external-input invalidation applies to a failed build).
        Some(outcomeKey ("failed:" + String.concat "|" (List.sort errs)))
    | FileChecked r ->
        // §1: fcs-signature captures cross-file FCS state so symbol changes upstream
        // invalidate this file's cached symbol-diff.
        //
        // Note what this arm does NOT read: not the changed symbols, not the pending
        // queue, not the dependsOn globs. It is a pure function of THIS file. That is
        // why all three are thunks.
        let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature r.CheckResults

        Some(
            FsHotWatch.TaskCache.merkleCacheKey
                [ "plugin-version", "test-prune-merkle-v2"
                  "event", "FileChecked"
                  "file", AbsFilePath.value r.File
                  "source", r.Source
                  "fcs-signature", fcsSignature ]
        )
    | _ -> None

/// Create a TestPrune plugin handler using the declarative plugin framework.
/// `buildExtensions` receives the plugin's own `Database` so extensions that
/// need a `RouteStore`/`SymbolStore` derive it from the same DB the plugin
/// queries against — structurally prevents the caller from wiring an extension
/// to a different DB than the plugin's.
let create
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (unit -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    // `dependsOn`: repo-root-relative globs naming EXTERNAL inputs (DB
    // migrations, generated files, schemas) that the symbol-diff cache key can't
    // see. Their content hash salts the BuildCompleted cache key so editing one
    // forces a genuine re-run instead of replaying a stale verdict. `[]` → no
    // salt (key byte-identical to the pre-feature key).
    (dependsOn: string list)
    =
    let db = Database.create dbPath

    // The symbol DB was just recreated (schema bump deleted the old one). The FCS check
    // cache is now stale: cache-hit files would skip re-checking and never re-flush their
    // symbols into the empty DB, leaving the graph (and coverage/impact analysis) partial.
    // Clear it so the next scan re-checks — and re-indexes — every file.
    if db.WasRecreated then
        try
            let cleared = clearFcsCheckCache repoRoot

            Logging.warn
                "test-prune"
                $"TestPrune DB was recreated (schema change) — cleared %d{cleared} FCS check-cache entries so every file re-indexes on this scan."
        with ex ->
            Logging.error "test-prune" $"failed to clear the FCS check cache after a DB recreate: %s{ex.Message}"

    let extensions = buildExtensions |> Option.map (fun f -> f db)

    let tryRepairSchemaDrift ex = tryRepairSchemaDrift dbPath ex

    // Durable "needs-testing" queue (plugin-owned sidecar). The set of changed
    // symbols not yet proven test-equivalent to the last green run. Loaded once
    // at construction so a restart with a non-empty queue re-flags those
    // symbols; updated write-through (in-memory ChangedSymbols stays the hot
    // view, this is the durable copy). A symbol leaves ONLY when a covering test
    // run passed (or it has no covering test). See PendingVerification.fs.
    //
    // Held in a closure-local mutable cell + Volatile for the same reason
    // changedSymbolsRef/freshnessRef are — read/written from multiple threads
    // (mailbox + cache intercept).
    let loadedQueue = PendingVerification.load repoRoot

    /// AUTOMATION-150. `Some reason` when the sidecar EXISTS but could not be read
    /// (a torn write, corrupt JSON, a non-string entry). What was owed is then
    /// UNKNOWN — which is NOT the same fact as "nothing is owed", and must never
    /// again be spelled with the same value. A MISSING file is not this: it is a
    /// provable `Loaded empty` (fresh clone), and stays a fast no-op.
    let ledgerUnreadableReason =
        match loadedQueue with
        | PendingVerification.LoadedQueue.Loaded _ -> None
        | PendingVerification.LoadedQueue.Unreadable reason -> Some reason

    let mutable pendingQueueRef: PendingVerification.Queue =
        match loadedQueue with
        | PendingVerification.LoadedQueue.Loaded queue -> queue
        | PendingVerification.LoadedQueue.Unreadable _ ->
            // We cannot NAME the symbols that were owed, so we cannot seed them. The
            // debt rides on `ledgerRecoveryOutstandingRef` instead, which widens every
            // run to the full suite until one proves the whole tree green. Seeding
            // `empty` here is safe ONLY because that flag exists — on its own it is
            // exactly the bug.
            PendingVerification.empty

    /// AUTOMATION-150. True while an UNREADABLE ledger's unknown debt is still
    /// outstanding — i.e. no full-suite green has yet re-verified the tree it
    /// described. While it is set:
    ///   * every run WIDENS to every configured project, in full (`runTestsWithImpact`);
    ///   * no skip may conclude "nothing owed" (`nothingOwed`);
    ///   * the plugin does not participate in the task cache at all (`cacheKeyFor`),
    ///     or a cached green from a genuinely-clean tree would replay over the debt;
    ///   * the corrupt file is NOT overwritten (`persistQueue`), so a crash mid-recovery
    ///     leaves the next session the same honest "unknown", not a clean empty ledger.
    /// Cleared only by a full-suite run that passed EVERY runnable project.
    let mutable ledgerRecoveryOutstandingRef = ledgerUnreadableReason.IsSome

    // Say it out loud. A silent recovery here is how this stayed invisible: the old
    // code swallowed the corrupt file and reported a green.
    match ledgerUnreadableReason with
    | Some reason ->
        Logging.warn
            "test-prune"
            $"the pending-verification ledger (%s{PendingVerification.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. It records every symbol still awaiting a green test run, so what is owed is now UNKNOWN — which is NOT the same as nothing owed. Until a FULL-SUITE run passes, every test run is widened to every configured project in full and no cached verdict may be replayed."
    | None -> ()

    /// The one question every skip in this plugin is really asking: is the
    /// needs-testing queue PROVABLY empty? An unreadable ledger is never `true` here
    /// — an empty queue we could not read is not an empty queue (AUTOMATION-150).
    let nothingOwed () =
        Set.isEmpty pendingQueueRef
        && not (Volatile.Read(&ledgerRecoveryOutstandingRef))

    /// What a drain is FOR, in words. An unreadable ledger owes a debt whose size
    /// cannot be printed, so it is named rather than counted.
    let owedDescription () =
        let queued = Set.count pendingQueueRef

        if Volatile.Read(&ledgerRecoveryOutstandingRef) then
            $"an UNREADABLE pending-verification ledger (what is owed is UNKNOWN, so only a full suite can prove it) + %d{queued} newly-queued symbol(s)"
        else
            $"%d{queued} symbol(s) awaiting verification"

    /// Persist the durable queue — UNLESS an unreadable ledger's debt is still
    /// outstanding (AUTOMATION-150).
    ///
    /// While it is, the corrupt file on disk IS the record, and it says the honest
    /// thing: "what is owed here is unknown". Overwriting it with our necessarily
    /// incomplete in-memory queue would launder that uncertainty into a clean, EMPTY
    /// ledger — and a crash before the recovering full-suite run finished would then
    /// hand the next session a ledger claiming nothing is owed. That is the very hole
    /// this ticket closes, re-opened through the write path. So we leave the corrupt
    /// bytes exactly where they are until a full-suite green has verified the tree,
    /// and rewrite the ledger only then (see the discharge in `TestsFinished`).
    let persistQueue (context: string) =
        if not (Volatile.Read(&ledgerRecoveryOutstandingRef)) then
            try
                PendingVerification.save repoRoot pendingQueueRef
            with ex ->
                Logging.warn
                    "test-prune"
                    $"failed to persist pending-verification queue%s{context}: %s{ex.Message}; in-memory queue still updated"

    /// AUTOMATION-112. When set, every test run this plugin launches is UNFILTERED —
    /// every configured project, in full. Requested by `fshw confirm` through the
    /// `set-scope` command BEFORE it triggers the scan, so the run the scan provokes
    /// is already full-suite and `confirm` never pays for two runs.
    ///
    /// Deliberately one-way within a daemon session in the safe direction: `fshw
    /// check` does not reset it. A `confirm` followed by an inner-loop check is merely
    /// slower; the reverse — a filtered run silently satisfying a `confirm` — is the whole
    /// bug. To go back to impact filtering, ask for it explicitly (`set-scope impact`)
    /// or restart the daemon.
    ///
    /// Note this only makes the run unfiltered. It does NOT let the CLI *claim* a
    /// full-suite verdict: `confirm` reads back what the run actually covered
    /// (`test-scope` → a projection of `RunCoverage`) and refuses anything less.
    /// The flag is a request; the scope report is the evidence.
    let mutable fullSuiteScopeRef = false

    /// Add `symbols` to the in-memory queue. Called at the FileChecked
    /// accumulation point; the durable persist is batched to the flush
    /// chokepoint (`flushAndQueryAffected` saves BEFORE the analysis flush
    /// advances the durable snapshot). Crash-safety holds without a per-file
    /// write: losing un-flushed enqueues also means the analysis snapshot was
    /// not advanced, so a restart re-DETECTS the same changes and re-enqueues
    /// them (over-testing is the safe direction).
    let enqueuePending (symbols: string list) =
        if not symbols.IsEmpty then
            let updated = (pendingQueueRef, symbols) ||> List.fold (fun q s -> Set.add s q)
            Volatile.Write(&pendingQueueRef, updated)

    /// Remove `symbols` from the persisted queue and flush to disk. Called only
    /// when a covering test run for those symbols completed green (or a symbol
    /// has no covering test).
    let commitPending (symbols: Set<string>) =
        if not symbols.IsEmpty then
            let updated = Set.difference pendingQueueRef symbols
            Volatile.Write(&pendingQueueRef, updated)
            persistQueue " after commit"

    /// The reds no covering run has passed since (AUTOMATION-125), mirrored out of
    /// the mailbox state for the CACHE-KEY intercept — which runs BEFORE `Update`, on
    /// another thread, and so cannot read the state. Same closure-local + `Volatile`
    /// shape as `pendingQueueRef`/`changedSymbolsRef`, for the same reason.
    ///
    /// While it is non-empty the plugin does not participate in the task cache AT ALL
    /// (no replay, no write — see `cacheKeyFor`): a cached green must never be served
    /// while a failing test is outstanding, and a red carried on a status must never be
    /// written to disk where a later, genuinely-fixed tree could replay it
    /// (AUTOMATION-5's stale-red-on-a-green-tree, in reverse).
    let mutable outstandingFailuresRef: OutstandingFailure list = []

    /// What the runs in THIS PROCESS have actually covered (AUTOMATION-161), mirrored out
    /// of the mailbox state for the CACHE-KEY intercept — same closure-local + `Volatile`
    /// shape as `outstandingFailuresRef`, and for the same reason: the key is computed
    /// before `Update`, on another thread, so it cannot read the state.
    ///
    /// EMPTY means this process holds NO test evidence, and while that is so the plugin
    /// does not participate in the task cache on `BuildCompleted` at all (no replay, no
    /// write — see `cacheKeyFor`). A replay would have this process assert a test result
    /// that its own state has no record of, and every consumer that reads the state —
    /// `test-scope`, and through it `.fshw/verdict.json` — would go on correctly
    /// reporting that nothing ran.
    ///
    /// An ABORTED run leaves it empty (its launch selection is empty, so it covers
    /// nothing), which is right: a run that never executed establishes nothing.
    let mutable sessionCoverageRef: RunCoverage = RunCoverage.none

    /// The test projects this daemon can actually RUN — i.e. the ones in
    /// `testConfigs`. Empty when the plugin is analysis-only.
    ///
    /// AUTOMATION-99. The symbol DB indexes test methods from EVERY test project it
    /// analyzed, which is not the same set as the projects fshw is configured to run.
    /// A symbol covered ONLY by an unconfigured project (e.g. FsHotWatch's own
    /// IntegrationTests, which the daemon never runs) can never be proven green: its
    /// covering project never executes, so it never appears in a run's results, so the
    /// symbol never commits — and it sits in the pending queue FOREVER, re-selecting
    /// tests that pass while the verdict stays red. That is a permanently stuck red.
    ///
    /// Observed live: two full suites ran and PASSED back-to-back, yet the queue kept
    /// 2 symbols and `check` exited 1 — because those symbols were covered by
    /// FsHotWatch.IntegrationTests, which is not in `tests.projects`.
    ///
    /// So "covered" must mean "covered by a test we can actually run". A symbol whose
    /// only covering tests are unrunnable is, for this daemon, indistinguishable from a
    /// symbol with no covering test at all — and is dropped by the same rule.
    let runnableProjects: Set<string> =
        match testConfigs with
        | Some configs -> configs |> List.map (fun c -> c.Project) |> Set.ofList
        | None -> Set.empty

    /// Tests covering `symbol` that this daemon can actually run. Empty ⇒ nothing
    /// runnable can ever verify it. When analysis-only (no test configs at all), the
    /// runnable filter is not applied — that mode produces no test verdict, so the old
    /// "any covering test" semantics are preserved.
    let runnableCoveringTests (symbol: string) =
        let covering = db.QueryAffectedTests [ symbol ]

        if Set.isEmpty runnableProjects then
            covering
        else
            covering |> List.filter (fun t -> Set.contains t.TestProject runnableProjects)

    // Flush pending analysis to DB and query affected tests from changed symbols.
    // Extensions (if any) contribute dependency edges via AnalyzeEdges, written
    // to the DB before QueryAffectedTests so they participate in impact traversal.
    let flushAndQueryAffected (state: TestPruneState) =
        // Persist the pending queue BEFORE flushPendingAnalysis advances the
        // durable analysis snapshot: once the snapshot advances, un-persisted
        // queue entries would no longer be re-detectable after a crash. One
        // write per flush (vs per FileChecked) — same crash-safety, batch-size
        // fewer disk writes.
        persistQueue ""

        let flushedState = flushPendingAnalysis db state

        match extensions with
        | Some exts when not exts.IsEmpty ->
            let store = TestPrune.Ports.toSymbolStore db

            let extensionDeps =
                exts
                |> List.collect (fun ext ->
                    try
                        ext.AnalyzeEdges store flushedState.ChangedFiles repoRoot
                    with ex ->
                        Logging.error "test-prune" $"Extension '%s{ext.Name}' failed: %s{ex.Message}"
                        [])

            if not extensionDeps.IsEmpty then
                let edgeResult =
                    { Symbols = []
                      Dependencies = extensionDeps
                      TestMethods = []
                      Attributes = []
                      ParentLinks = []
                      Diagnostics = AnalysisDiagnostics.Zero }

                db.RebuildProjects([ edgeResult ])
        | _ -> ()

        // Affected tests must be computed from the WHOLE needs-testing queue —
        // the in-memory hot view UNION the durable sidecar — not just the latest
        // diff. The persisted queue holds symbols a green run hasn't yet cleared
        // (e.g. carried across a restart, or left behind by an Aborted/failed
        // run); they must keep selecting tests until a covering run passes.
        let symbols =
            Set.union pendingQueueRef (Set.ofList flushedState.ChangedSymbols) |> Set.toList

        let affectedTests =
            if symbols.IsEmpty then
                []
            else
                // Scoped to projects this daemon actually runs. Selecting tests in an
                // unconfigured project would put classes in the run map that never
                // execute — and make `allChangesUncovered` (and so the zero-affected
                // skip) disagree with the commit rule. See `runnableCoveringTests`.
                let queryRunnable (seeds: string list) =
                    db.QueryAffectedTests(seeds)
                    |> fun ts ->
                        if Set.isEmpty runnableProjects then
                            ts
                        else
                            ts |> List.filter (fun t -> Set.contains t.TestProject runnableProjects)

                let affected = queryRunnable symbols
                let sortedSeeds = List.sort symbols

                // Count the INPUT explicitly, not just the output. `symbols` is the
                // union of the durable pending-verification queue and the in-memory
                // hot view, so it grows monotonically across aborted runs until a
                // green run clears it — "the queue is wedged and growing" and "a
                // small, precise selection" look identical without this number, and
                // `%A` truncated it away at 100.
                //
                // The seed list is logged in FULL and sorted (`describeAll`, not
                // `%A`): when a handful of junk seeds drags in thousands of tests,
                // the offending seed is the whole diagnosis, and a sample that
                // happens to omit it costs hours.
                Logging.info
                    "test-prune"
                    $"QueryAffectedTests: %d{symbols.Length} seed(s) → %d{affected.Length} affected tests"

                Logging.info "test-prune" $"  seeds: %s{describeAll sortedSeeds}"

                // Per-seed attribution, but ONLY when the selection is already wide.
                // A single seed accounting for most of a run is either a genuine
                // graph hub or a mis-qualified symbol, and is the only thing worth
                // looking at; below the threshold this costs nothing because the
                // re-query loop never runs.
                if affected.Length > WideSelectionTests then
                    if sortedSeeds.Length > MaxSeedsToAttribute then
                        // Never cap silently: say the attribution was skipped and why,
                        // so an absent breakdown is not read as "no seed dominated".
                        Logging.warn
                            "test-prune"
                            $"%d{affected.Length} tests selected, but %d{sortedSeeds.Length} seeds exceeds the \
                              %d{MaxSeedsToAttribute}-seed attribution budget — per-seed breakdown SKIPPED"
                    else
                        let dominantShare = affected.Length / 4

                        for seed in sortedSeeds do
                            let alone = (queryRunnable [ seed ]).Length

                            if alone > dominantShare then
                                let pct = alone * 100 / affected.Length

                                Logging.warn
                                    "test-prune"
                                    $"seed '%s{seed}' alone selects %d{alone} of %d{affected.Length} tests \
                                      (%d{pct}%%) — a dependency-graph hub, or a mis-qualified symbol"

                affected

        // Drop queued symbols that have no RUNNABLE covering test from the durable
        // queue immediately: there is nothing for them to wait on, and retaining them
        // would wedge the queue forever (every future run would re-select zero
        // runnable tests yet the queue would never empty → permanent non-green). A
        // symbol is "covered" iff it has at least one covering test IN A PROJECT THIS
        // DAEMON RUNS (see `runnableCoveringTests` — AUTOMATION-99: a symbol covered
        // only by an unconfigured project is unverifiable here and wedged the verdict).
        // Only ever REMOVES from the queue, so it cannot under-test.
        let uncovered =
            symbols
            |> List.filter (fun s -> (runnableCoveringTests s).IsEmpty)
            |> Set.ofList

        if not (Set.isEmpty uncovered) then
            Logging.info
                "test-prune"
                $"Dropping %d{Set.count uncovered} queued symbol(s) with no runnable covering test from pending-verification queue"

            commitPending uncovered

        // Keep the in-memory hot view aligned with the durable queue so the
        // ChangedSymbols carried in state (and the cache-key snapshot) don't
        // re-select the uncovered symbols on the next event.
        let remainingSymbols =
            flushedState.ChangedSymbols
            |> List.filter (fun s -> not (Set.contains s uncovered))

        // There WERE symbols to consider this cycle, yet the affected set is
        // empty — so every one of them was just dropped as uncovered (a union
        // query returning zero tests means every per-symbol query did too). That
        // is a definitive "nothing to verify" green: nothing that changed is
        // testable, so a run would prove nothing. The run-trigger reads this to
        // complete green immediately instead of running the full suite (see
        // runTestsWithImpact's zero-affected skip). An EMPTY `symbols` (genuine
        // cold start, nothing pending) leaves this false so the baseline still runs.
        let allChangesUncovered = not symbols.IsEmpty && List.isEmpty affectedTests

        { flushedState with
            ChangedSymbols = remainingSymbols
            AffectedTests = Analyzed affectedTests
            ChangedSymbolsAllUncovered = allChangesUncovered }

    // Mutable snapshot of ChangedSymbols for the cache key function.
    // Updated from the Update handler so the cache intercept (which runs
    // before Update) sees the symbols accumulated from prior FileChecked events.
    let mutable changedSymbolsRef: string list = []

    // Per-file FCS freshness sidecar (Path D in the 0.10 fix-forward design).
    // Loaded once at plugin construction from `.fshw/test-prune/file-freshness.json`
    // and updated incrementally on each FileChecked. Survives daemon restarts
    // so cross-restart Phase B replay can decide which files' stored symbols
    // are trustworthy enough to run detectChanges against.
    //
    // Held in a closure-local mutable cell + Volatile for the same reason
    // changedSymbolsRef is — the plugin Update handler reads/writes from
    // multiple threads (mailbox + cache intercept).
    let mutable freshnessRef: FileFreshness.Store = FileFreshness.load repoRoot

    let updateFreshness (newStore: FileFreshness.Store) =
        Volatile.Write(&freshnessRef, newStore)

        try
            FileFreshness.save repoRoot newStore
        with ex ->
            Logging.warn
                "test-prune"
                $"failed to persist file-freshness sidecar: %s{ex.Message}; in-memory state still updated"

    let hasTestConfigs =
        testConfigs |> Option.map (List.isEmpty >> not) |> Option.defaultValue false

    // Seed the in-memory hot view from the durable queue so a restart with a
    // non-empty queue re-flags those symbols: the first flushAndQueryAffected
    // (on cold-scan BatchChecked) then queries them and the next run re-tests
    // anything not yet proven green. Without this, a restart would diff current
    // symbols against the already-advanced analysis snapshot → "nothing changed"
    // → zero tests run → false green (hole #3).
    let initialState =
        { PendingAnalysis = Map.empty
          SymbolSnapshot = Map.empty
          AffectedTests = NotYetAnalyzed
          ChangedSymbols = pendingQueueRef |> Set.toList
          ChangedFiles = []
          LastResults = None
          LastRunId = None
          PendingRerun = false
          TestClassFiles = Map.empty
          BuildCompletedInThisSession = false
          PriorProjectFingerprints = Map.empty
          PendingForceRunProjects = Set.empty
          ChangedSymbolsAllUncovered = false
          UnanalyzableFiles = Map.empty
          QueuedCommandRuns = []
          OutstandingFailures = []
          LastCoverage = RunCoverage.none }

    // Keep the cache-key snapshot consistent with the seeded queue from the
    // very first event (the cache intercept runs before any Update handler).
    Volatile.Write(&changedSymbolsRef, initialState.ChangedSymbols)

    /// Returns the `TestsFinished` message that the framework's RunExclusive
    /// will post back to the agent. The synchronous `Custom(TestsFinished)`
    /// handler emits the `TestRunStarted`/`TestRunCompleted` events inside
    /// the §2a cache-write capture window. Catches its own exceptions to
    /// produce an `Aborted` lifecycle rather than letting RunExclusive eat
    /// the message (which would leave the slot freed but no completion
    /// posted, stranding `LastResults`/`PendingRerun` in a Schrödinger state).
    let runTestsWithImpact
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        // The four fields a run reads — NOT the state record. The async this
        // returns outlives the state generation it was launched from; see
        // `TestRunInputs`.
        (inputs: TestRunInputs)
        // `hasCachedResults` (`state.LastResults.IsSome`, computed by the caller)
        // means a run already completed THIS session — i.e. a green baseline exists
        // to be "test-equivalent" to. The zero-affected skip needs this AS WELL AS an
        // empty queue: a cold daemon with an empty queue but no prior run has no
        // baseline yet and must run the full suite once to establish one. See
        // the skip gate below.
        (hasCachedResults: bool)
        // Dependency-fanout (DependencyFanout): test projects whose dependency
        // fingerprint changed since the last build. Their tests run in FULL
        // (project-coarse), UNIONED with the symbol-precise selection — a binary
        // change the symbol diff can't see still re-runs the dependent tests.
        // Empty in the common case (no dependency change), so ordinary
        // source-symbol edits keep their precise, minimal selection.
        (fanoutProjects: Set<string>)
        : Async<TestPruneMsg> =
        async {
            // The single chokepoint every launch path funnels through (BatchChecked
            // drain, BuildCompleted, deferred rerun). Both widenings live here rather
            // than at each call site, so no future launch path can forget one and
            // quietly reintroduce a filtered run where an unfiltered one was owed.
            //
            //  * AUTOMATION-112 — full-suite scope: run EVERY project, unfiltered.
            //  * AUTOMATION-113 — unanalysable files: run every project, because a
            //    selection made without them cannot be trusted.
            //  * AUTOMATION-150 — an UNREADABLE pending-verification ledger: run every
            //    project, because the ledger names what is still owed, and a selection
            //    made without it cannot be trusted either. Same shape as 113: the
            //    missing input is a SAFETY input, so its absence widens rather than
            //    narrows.
            let scopeIsFullSuite = Volatile.Read(&fullSuiteScopeRef)
            let ledgerUnreadable = Volatile.Read(&ledgerRecoveryOutstandingRef)

            // The coarse fallback only needs to know WHICH files are unanalysable; the
            // map's values exist so the ledger projection can re-report their
            // diagnostics (AUTOMATION-125).
            let unanalyzablePaths = inputs.UnanalyzableFiles |> Map.keys |> Set.ofSeq

            let forceRunProjects =
                let widened = coarseFallbackProjects configs unanalyzablePaths fanoutProjects

                if scopeIsFullSuite || ledgerUnreadable then
                    Set.union widened (fullSuiteProjects configs)
                else
                    widened

            if scopeIsFullSuite then
                Logging.info
                    "test-prune"
                    "Scope: FULL SUITE — impact filtering is disabled for this run; every configured test project runs in full"

            if ledgerUnreadable then
                Logging.warn
                    "test-prune"
                    "Scope: FULL SUITE (unreadable pending-verification ledger) — the record of what still needs testing could not be read, so this run cannot know what it owes. It runs EVERY configured test project in full rather than trust an impact selection made without the ledger. Impact filtering resumes once a full suite passes."

            if not (Set.isEmpty unanalyzablePaths) then
                let names = unanalyzablePaths |> Set.toList |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"%d{Set.count unanalyzablePaths} file(s) could not be analysed (%s{names}) — their symbols are missing from the impact graph, so this run falls back to EVERY test project in full rather than trusting a selection made without them"

            // The queue snapshot this run is LAUNCHED against — the durable
            // queue UNION the in-memory hot view. Captured here (not read from
            // state at completion time) because mid-run BatchChecked flushes
            // mutate both; the synchronous TestsFinished handler commits ONLY
            // these symbols and leaves mid-run arrivals queued for the rerun.
            let launchedSymbols = Set.union pendingQueueRef (Set.ofList inputs.ChangedSymbols)

            try
                // For each launched symbol, the set of test PROJECTS whose tests
                // cover it. An empty set ⇒ no covering test. Queried per-symbol so
                // a symbol commits ONLY when every project covering IT passed (a
                // run-wide union would over-couple unrelated symbols). Empty queue ⇒
                // no queries. Kept INSIDE the try: these are DB reads that can throw
                // transiently on a cold/contended box (SQLITE_BUSY, schema drift). A
                // throw here must produce the Aborted lifecycle below (an honest,
                // re-runnable "tests did not run" verdict) rather than escaping to
                // the framework's `runOne`, which would only log-and-strand the run.
                // Only RUNNABLE covering projects gate a symbol's commit — the same
                // rule `flushAndQueryAffected` uses to drop unverifiable symbols, so
                // the two cannot disagree. Gating on a project this daemon never runs
                // would block the commit forever (AUTOMATION-99's permanent red).
                let coveringProjectsBySymbol =
                    launchedSymbols
                    |> Set.toList
                    |> List.map (fun s ->
                        let projs =
                            runnableCoveringTests s |> List.map (fun t -> t.TestProject) |> Set.ofList

                        s, projs)
                    |> Map.ofList

                // Extension-contributed edges were already written to the DB by
                // flushAndQueryAffected, so `inputs.AffectedTests` already includes tests
                // reachable through extension edges (sql, sql-hydra, falco, etc.).
                let affectedTestsList =
                    match inputs.AffectedTests with
                    | Analyzed tests -> tests
                    | NotYetAnalyzed -> []

                let symbolAffectedByProject =
                    affectedTestsList
                    |> List.groupBy (fun t -> t.TestProject)
                    |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)
                    |> Map.ofList

                // UNION the dependency-fanout: each force-run project enters the
                // map with an EMPTY class list, which `buildFilterArgs` treats as
                // "no filter → run ALL tests in this project" (a project ABSENT
                // from a non-empty map is skipped; present-with-[] runs in full).
                // We don't overwrite a project that already has symbol-affected
                // classes — its precise selection plus a forced full run are the
                // same "run this project"; [] (full) is the safe superset, so a
                // fanout hit promotes a partially-selected project to full.
                let affectedByProject =
                    forceRunProjects
                    |> Set.fold (fun acc proj -> Map.add proj [] acc) symbolAffectedByProject

                /// AUTOMATION-125 — the run's SCOPE, in the same shape `executeTests`
                /// will actually honour, captured on the launch so the completion
                /// handler knows what this run is entitled to clear.
                ///
                /// Mirrors `executeTests`' own reading of `affectedClassesByProject`
                /// EXACTLY, which is why it is derived from that map and not
                /// re-decided: an EMPTY map means "no selection" → every project runs
                /// in full; a NON-empty map means a project present with `[]` runs in
                /// full, a project present with classes runs filtered, and a project
                /// ABSENT is skipped entirely (and recorded as a filtered pass that
                /// proves nothing — the laundering vector).
                let selection: Map<string, ProjectSelection> =
                    if Map.isEmpty affectedByProject then
                        configs |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList
                    else
                        configs
                        |> List.choose (fun c ->
                            match Map.tryFind c.Project affectedByProject with
                            | None -> None
                            | Some [] -> Some(c.Project, ProjectInFull)
                            | Some classes -> Some(c.Project, ProjectClasses(Set.ofList classes)))
                        |> Map.ofList

                let launch =
                    { Symbols = launchedSymbols
                      CoveringProjectsBySymbol = coveringProjectsBySymbol
                      Selection = selection }

                // The skip gate counts symbol-affected classes only. A pure
                // dependency-fanout (force-run projects, zero symbol classes) must
                // NOT be counted as "0 affected" and skipped — so the gate below
                // also checks `forceRunProjects` is empty.
                let totalClasses = symbolAffectedByProject |> Map.values |> Seq.sumBy List.length

                // Two independent routes to the degenerate zero-affected skip —
                // BOTH terminate as a clean green (0 ran) via the same lifecycle,
                // distinguishable from "tests exist and all passed" only in that
                // zero ran:
                //
                //  (1) Baseline-equivalent (§3c). PERSISTED queue empty AND a
                //      session baseline exists (`hasCachedResults`). Empty queue =
                //      "test-equivalent to the last green run", so "0 affected
                //      tests" is a sound green. The queue-empty check is
                //      load-bearing: a NON-empty queue with 0 affected classes
                //      (covered symbols whose tests aren't indexed yet, etc.) must
                //      run the suite rather than silent-green. `hasCachedResults`
                //      is the cold-start guard: the very first run of a session
                //      (no baseline yet, e.g. a genuine cold check with nothing
                //      pending) must run the full suite to ESTABLISH the green
                //      baseline the empty queue is then equivalent to.
                //
                //  (2) Nothing-to-verify (AUTOMATION-65 QA). This cycle HAD
                //      changed/queued symbols and EVERY one proved to have no
                //      covering test (`ChangedSymbolsAllUncovered`, set by
                //      flushAndQueryAffected as it dropped them). No test covers
                //      any of them, so a run — even the cold-start full suite —
                //      would verify nothing about them: the honest outcome is a
                //      "nothing to verify" green NOW. This is sound WITHOUT a
                //      session baseline (unlike route 1) precisely because it is
                //      gated on symbols having existed and provably lacking any
                //      test, not on an absent-pending inference. It rescues the
                //      wedge where an all-uncovered cold run would otherwise fall
                //      through to the full suite and hang, never resolving
                //      WaitForComplete. A genuine cold start with NO pending
                //      symbols leaves the flag false, so the baseline still runs.
                //      AUTOMATION-150: "empty queue" must mean PROVABLY empty, so this
                //      reads `nothingOwed` rather than `Set.isEmpty` — an unreadable
                //      ledger owes an unknown debt and can never be baseline-equivalent.
                //      (The `nothingToVerify` route cannot fire under an unreadable
                //      ledger either: it requires `Set.isEmpty forceRunProjects`, and
                //      the widening above has just forced every configured project.)
                let baselineEquivalent = nothingOwed () && hasCachedResults

                let nothingToVerify = inputs.ChangedSymbolsAllUncovered

                if
                    totalClasses = 0
                    && Set.isEmpty forceRunProjects
                    && (baselineEquivalent || nothingToVerify)
                then
                    if nothingToVerify then
                        Logging.info
                            "test-prune"
                            "Every changed symbol has no covering test — nothing to verify, skipping tests (green, 0 ran)"
                    else
                        Logging.info
                            "test-prune"
                            "No affected classes, no dependency fanout, empty pending queue, baseline exists — skipping tests"

                    // Build a degenerate lifecycle (Started → Completed with empty
                    // Results). The synchronous Custom handler emits both events
                    // inside the cache-write capture window so they replay
                    // correctly on cache hit.
                    let runId = Guid.NewGuid()

                    let started: TestRunStarted =
                        { RunId = runId
                          StartedAt = DateTime.UtcNow }

                    let completed: TestRunCompleted =
                        { RunId = runId
                          TotalElapsed = TimeSpan.Zero
                          Outcome = Normal
                          Results = Map.empty
                          RanFullSuite = true }

                    // The skip EXECUTES NOTHING, so it covers nothing and may clear
                    // nothing (AUTOMATION-125). Empty results already yield an empty
                    // `RunCoverage`; the empty selection says so at the source too, so
                    // the "0 affected, green, 0 ran" path can never be mistaken for
                    // evidence about a project.
                    return TestsFinished(started, completed, { launch with Selection = Map.empty })
                else
                    if totalClasses = 0 then
                        Logging.info "test-prune" "No affected classes (cold start / pending queue) — running all tests"
                    else
                        for (proj, classes) in affectedByProject |> Map.toList do
                            // Two defects, one line. (1) BREADTH is the measurement
                            // this line exists to provide, and `%A` capped it at 100 —
                            // a 1,500-class blowout read identically to a 100-class
                            // one. `describeMany` leads with the exact count.
                            // (2) An EMPTY list here does not mean "nothing selected";
                            // it means the project runs UNFILTERED — every class,
                            // force-run. Printing that as `[]` said the precise
                            // opposite of what it means, so the widest possible run
                            // rendered as the narrowest. Say it in words instead.
                            let rendered =
                                if List.isEmpty classes then
                                    "ALL (unfiltered — force-run)"
                                else
                                    describeMany classes

                            Logging.info "test-prune" $"Affected classes for %s{proj}: %s{rendered}"

                    // Run-level selectivity, in ONE line. The per-project lines above
                    // can be scrolled past, interleaved, or truncated by whatever is
                    // reading the log; this is the single line that answers "did
                    // impact analysis actually narrow anything this run?" — and it
                    // separates the two ways a run can be wide: many classes named,
                    // versus projects running unfiltered.
                    let unfilteredProjects =
                        affectedByProject |> Map.filter (fun _ cs -> List.isEmpty cs) |> Map.count

                    Logging.info
                        "test-prune"
                        $"Selectivity: %d{affectedByProject.Count} project(s) selected, \
                          %d{unfilteredProjects} of them UNFILTERED (whole-project), \
                          %d{totalClasses} class(es) named in total"

                    let! results, started, completed =
                        executeTests
                            db
                            (Some ctx)
                            repoRoot
                            beforeRun
                            coveragePaths
                            afterRun
                            configs
                            affectedByProject
                            None

                    // executeTests still emits per-group TestProgress live; the
                    // synchronous handler emits Started + Completed for the
                    // §2a cache-write capture window.
                    ignore results
                    return TestsFinished(started, completed, launch)
            with ex ->
                Logging.error "test-prune" $"runTests failed: %s{ex.Message}"

                // Build an Aborted lifecycle so subscribers see a coherent end
                // to this run rather than hanging at TestRunStarted.
                let started, completed = abortedRunLifecycle ex.Message

                // launch carries the queue snapshot this aborted run was
                // launched against; the TestsFinished handler commits NOTHING
                // for an Aborted outcome, so those symbols stay queued. Rebuilt
                // from `launchedSymbols` here (rather than the in-try `launch`,
                // which may not exist if the per-symbol coverage query itself
                // threw) with an empty covering map — an Aborted run commits
                // nothing, so the covering map is never consulted on this path.
                // The empty SELECTION says the same thing about the ledger: an
                // aborted run executed nothing, so it clears nothing.
                let launch =
                    { Symbols = launchedSymbols
                      CoveringProjectsBySymbol = Map.empty
                      Selection = Map.empty }

                return TestsFinished(started, completed, launch)
        }

    /// The `run-tests` force-run work async, launched under the "tests" slot.
    /// Shared by the immediate-claim path (`RunTestsRequested` with a free
    /// slot) and the queued-drain path (`TestsFinished` popping
    /// `QueuedCommandRuns`) so FORCE semantics stay in lockstep. Every exit —
    /// success, fault, cancellation — resolves `reply` (the IPC command is
    /// awaiting it, bounded) and returns a `TestsFinished` so the synchronous
    /// handler delivers the earned terminal status.
    ///
    /// Empty launch set: `run-tests` is a manual FORCE run (optionally
    /// filtered to a subset / only-failed). It is NOT the impact-analysis
    /// queue-draining path, and a filtered force-run may not cover every
    /// queued symbol — so it commits NOTHING from the pending-verification
    /// queue (over-testing is the safe direction). The queue drains through
    /// the normal BuildCompleted impact flow.
    let commandForceRun
        (configs: TestConfig list)
        (filter: string option)
        (reply: Tasks.TaskCompletionSource<string>)
        : Async<TestPruneMsg> =
        // A force-run launches exactly `configs`, each with NO class selection (it
        // passes `Map.empty` to `executeTests`), so each runs IN FULL — a plain
        // `dotnet fshw test-rerun` is therefore the unfiltered run that can clear ANY
        // outstanding red (AUTOMATION-125's escape hatch, and the reason the rule
        // cannot wedge into a permanent stuck-red).
        //
        // A `--filter` passthrough is a different matter: `RunCoverage.ofRun` sees
        // `wasFiltered = true` on the results and declines to claim coverage for a
        // filter string whose reach it cannot compute. Projects NOT in `configs`
        // (`--only-failed`, `--projects`) are absent from the selection and so are
        // covered by nothing — exactly right, they did not run.
        let commandLaunch: TestRunLaunch =
            { Symbols = Set.empty
              CoveringProjectsBySymbol = Map.empty
              Selection = configs |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList }

        async {
            try
                try
                    let! results, started, completed =
                        executeTests db None repoRoot beforeRun coveragePaths afterRun configs Map.empty filter

                    reply.TrySetResult(formatTestResultsJson results) |> ignore

                    // Returned (not Posted) so the framework's completion path
                    // delivers it: the synchronous TestsFinished handler does
                    // the error reporting and status updates a bare emit would
                    // skip.
                    return TestsFinished(started, completed, commandLaunch)
                with ex ->
                    // AUTOMATION-68: a `beforeRun` throw / `executeTests` fault
                    // means the suite it guards NEVER RAN — that must surface
                    // as a failure, never a stale prior green. The Aborted
                    // lifecycle drives the TestsFinished handler to a Failed
                    // status.
                    Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                    let started, completed = abortedRunLifecycle ex.Message

                    reply.TrySetResult(JsonSerializer.Serialize({| error = ex.Message |})) |> ignore

                    return TestsFinished(started, completed, commandLaunch)
            finally
                // Cancellation (daemon teardown) skips `with` but runs
                // `finally`: never leave the IPC client awaiting a reply that
                // cannot come. No-op when a result was already set.
                reply.TrySetResult(JsonSerializer.Serialize({| error = "daemon shut down before the run completed" |}))
                |> ignore
        }

    let commands =
        [ "affected-tests",
          fun (_ctx: CommandCtx<TestPruneMsg>) (state: TestPruneState) (_args: string array) ->
              async {
                  // Compute on demand from state.ChangedSymbols against current DB
                  // state. ChangedSymbols accumulates across FileChecked events and
                  // is reset by flushAndQueryAffected on BuildCompleted.
                  let symbols = state.ChangedSymbols |> List.distinct

                  let tests =
                      if symbols.IsEmpty then
                          []
                      else
                          db.QueryAffectedTests(symbols)

                  let testsData =
                      tests
                      |> List.map (fun t ->
                          {| project = t.TestProject
                             ``class`` = t.TestClass
                             ``method`` = t.TestMethod |})

                  return JsonSerializer.Serialize(testsData)
              }

          "changed-files",
          fun (_ctx: CommandCtx<TestPruneMsg>) (state: TestPruneState) (_args: string array) ->
              async { return JsonSerializer.Serialize(state.ChangedFiles) }

          "test-results",
          fun (ctx: CommandCtx<TestPruneMsg>) (state: TestPruneState) (_args: string array) ->
              async {
                  if ctx.IsRunning "tests" then
                      return JsonSerializer.Serialize({| status = "running" |})
                  else
                      match state.LastResults with
                      | Some results -> return formatTestResultsJson results
                      | None -> return JsonSerializer.Serialize({| status = "not run" |})
              }

          "flaky-tests",
          fun (_ctx: CommandCtx<TestPruneMsg>) (_state: TestPruneState) (_args: string array) ->
              async {
                  let history = Flakiness.loadHistory (flakinessHistoryPath repoRoot)
                  let top = Flakiness.topFlaky 10 history

                  let payload =
                      top
                      |> List.map (fun (name, score) ->
                          let runs =
                              history |> Map.tryFind name |> Option.map List.length |> Option.defaultValue 0

                          {| name = name
                             flakiness = score
                             runs = runs |})

                  return JsonSerializer.Serialize({| tests = payload |})
              } ]

    // run-tests / scope commands (only if testConfigs are provided). The scope verbs
    // live behind the same condition on purpose: a repo with no test projects has no suite
    // to run in full, so `fshw confirm` finds no `set-scope` command, cannot establish
    // the full-suite scope, and refuses to produce a merge verdict — rather than
    // silently issuing a green one it never earned.
    let allCommands =
        match testConfigs with
        | Some allConfigs when not allConfigs.IsEmpty ->
            commands
            @ [ "set-scope",
                fun (_ctx: CommandCtx<TestPruneMsg>) (_state: TestPruneState) (args: string array) ->
                    async {
                        // AUTOMATION-112. `fshw confirm` calls this BEFORE triggering its
                        // scan, so the test run the scan provokes is already unfiltered.
                        let requested =
                            let argStr = if args.Length > 0 then args.[0].Trim() else "{}"

                            try
                                use doc = JsonDocument.Parse(argStr)

                                match doc.RootElement.TryGetProperty("scope") with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> "impact"
                            with _ ->
                                "impact"

                        match requested with
                        | "full" ->
                            Volatile.Write(&fullSuiteScopeRef, true)

                            Logging.info
                                "test-prune"
                                "Scope set to FULL SUITE — impact filtering disabled for subsequent runs in this daemon session"

                            return JsonSerializer.Serialize({| scope = "full" |})
                        | "impact" ->
                            Volatile.Write(&fullSuiteScopeRef, false)
                            Logging.info "test-prune" "Scope set to IMPACT-FILTERED (inner-loop default)"
                            return JsonSerializer.Serialize({| scope = "impact" |})
                        | other ->
                            return
                                JsonSerializer.Serialize(
                                    {| error = $"unknown scope '%s{other}' (expected 'full' or 'impact')" |}
                                )
                    }

                "test-scope",
                fun (ctx: CommandCtx<TestPruneMsg>) (state: TestPruneState) (_args: string array) ->
                    async {
                        // What the last completed run ACTUALLY covered — the evidence a
                        // merge verdict is computed from. Never a restatement of what was
                        // requested: `set-scope full` is a request, this is the receipt.
                        // A run still in flight reports `running`, which `confirm` treats
                        // as "no verdict yet" rather than as a scope.
                        // The reply carries the RUN ID as well as the scope, so the CLI
                        // can DECLARE which CTRF reports belong to this run
                        // (`.fshw/test-runs/<runId>/`) rather than inferring membership
                        // from mtimes. A pile you have to date-sort is a pile you have to
                        // do forensics on.
                        let runId =
                            match state.LastRunId with
                            | Some id -> box (id.ToString("N"))
                            | None -> null

                        // The scope is a PROJECTION of `LastCoverage` — the very value the
                        // ledger uses to decide what a run is entitled to CLEAR
                        // (AUTOMATION-125). See `scopeOf`.
                        let projects = allConfigs |> List.map (fun c -> c.Project)

                        if ctx.IsRunning "tests" then
                            return JsonSerializer.Serialize({| scope = "running"; runId = runId |})
                        else
                            match scopeOf projects state.LastCoverage with
                            | ScopeFull n ->
                                return
                                    JsonSerializer.Serialize(
                                        {| scope = "full"
                                           ranProjects = n
                                           totalProjects = n
                                           runId = runId |}
                                    )
                            | ScopeFiltered(ran, total) ->
                                return
                                    JsonSerializer.Serialize(
                                        {| scope = "filtered"
                                           ranProjects = ran
                                           totalProjects = total
                                           runId = runId |}
                                    )
                            | ScopeNone total ->
                                return
                                    JsonSerializer.Serialize(
                                        {| scope = "none"
                                           ranProjects = 0
                                           totalProjects = total
                                           runId = runId |}
                                    )
                    }

                "run-tests",
                fun (ctx: CommandCtx<TestPruneMsg>) (state: TestPruneState) (args: string array) ->
                    async {
                        // FORCE semantics: `test-rerun` is the explicit "prove it
                        // ran" verb. The run NEVER executes here (AUTOMATION-99):
                        // executing on the IPC thread bypassed the `RunExclusive
                        // "tests"` slot entirely, so for the whole duration of a
                        // force-run the daemon model read "at rest" — no Running
                        // status, `IsRunning "tests"` false (a concurrent
                        // FileChecked stamped a terminal over the live run),
                        // `AnyPluginBusy()` false — and a concurrent `fshw check`
                        // exited 0 while the test process was literally alive.
                        // `CommandCtx` has since made that unwritable: a command
                        // can only `Post`. The mailbox handler claims the slot
                        // (the framework reports Running at the claim) or QUEUES
                        // the run behind the one in flight — a force-run is owed
                        // work, never refused. The only thing bounded here is the
                        // WAIT: `waitSec` (CLI `--wait-sec`) caps queue time plus
                        // run time, and on expiry the command reports a DISTINCT
                        // `busy` status — the CLI exits non-zero, never a verdict
                        // the run didn't produce (AUTOMATION-98: bound every seam).
                        try
                            let argStr = if args.Length > 0 then args.[0].Trim() else "{}"
                            let waitForResultMs = parseRunTestsWaitMs argStr DefaultRunTestsWaitMs

                            let parseResult =
                                try
                                    Ok(JsonDocument.Parse(argStr))
                                with ex ->
                                    Error ex.Message

                            match parseResult with
                            | Error msg -> return JsonSerializer.Serialize({| error = $"invalid JSON: %s{msg}" |})
                            | Ok doc ->

                                use doc = doc
                                let root = doc.RootElement

                                let filter =
                                    match root.TryGetProperty("filter") with
                                    | true, v -> Some(v.GetString())
                                    | false, _ -> None

                                let onlyFailed =
                                    match root.TryGetProperty("only-failed") with
                                    | true, v -> v.GetBoolean()
                                    | false, _ -> false

                                let projectFilter =
                                    match root.TryGetProperty("projects") with
                                    | true, v ->
                                        v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq |> Some
                                    | false, _ -> None

                                // Resolve configs or produce an error
                                let lastResults = state.LastResults

                                // AUTOMATION-125. "Failed" is the OUTSTANDING set, not
                                // merely the last run's results: after an impact-filtered
                                // run the failing project is not in `LastResults` at all
                                // (it wasn't selected), and `--only-failed` would have
                                // re-run nothing — "no matching test projects" — while a
                                // red sat there. The outstanding ledger is what still owes
                                // a re-run, so it is what this verb must re-run.
                                let outstandingProjects =
                                    state.OutstandingFailures |> List.map (fun f -> f.Project) |> Set.ofList

                                let configsResult =
                                    if onlyFailed then
                                        let lastRunFailed =
                                            match lastResults with
                                            | Some prev ->
                                                prev.Results
                                                |> Map.toList
                                                |> List.choose (fun (name, r) ->
                                                    match r with
                                                    | TestsFailed _
                                                    | TestsTimedOut _
                                                    // A deferred project never ran, and an errored one
                                                    // aborted without a verdict — both are non-green, so
                                                    // `--only-failed` (rerun non-green projects) must pick
                                                    // them up.
                                                    | TestsDeferred _
                                                    | TestsErrored _ -> Some name
                                                    | _ -> None)
                                                |> Set.ofList
                                            | None -> Set.empty

                                        let failedNames = Set.union lastRunFailed outstandingProjects

                                        if lastResults.IsNone && Set.isEmpty failedNames then
                                            Error "no previous results — cannot determine failed projects"
                                        else
                                            Ok(allConfigs |> List.filter (fun c -> failedNames.Contains(c.Project)))
                                    else
                                        match projectFilter with
                                        | Some names ->
                                            Ok(allConfigs |> List.filter (fun c -> names.Contains(c.Project)))
                                        | None -> Ok allConfigs

                                match configsResult with
                                | Error msg -> return JsonSerializer.Serialize({| error = msg |})
                                | Ok configs when configs.IsEmpty ->
                                    return JsonSerializer.Serialize({| error = "no matching test projects" |})
                                | Ok configs ->
                                    let reply =
                                        Tasks.TaskCompletionSource<string>(
                                            Tasks.TaskCreationOptions.RunContinuationsAsynchronously
                                        )

                                    ctx.Post(RunTestsRequested(configs, filter, reply))

                                    // Bounded await (AUTOMATION-98): the reply resolves
                                    // when the run finishes — behind the test-prune
                                    // mailbox and possibly behind a run already in
                                    // flight — so an unbounded wait here could pin the
                                    // IPC caller for as long as the daemon is wedged.
                                    let! winner =
                                        Tasks.Task.WhenAny(reply.Task, Tasks.Task.Delay(waitForResultMs))
                                        |> Async.AwaitTask

                                    if winner = (reply.Task :> Tasks.Task) then
                                        return reply.Task.Result
                                    else
                                        return
                                            JsonSerializer.Serialize(
                                                {| status = "busy"
                                                   message =
                                                    $"the test run did not produce a result within %d{waitForResultMs / 1000}s (still queued or running); retry, or raise --wait-sec" |}
                                            )
                        with ex ->
                            // Command-local faults only (the run itself executes in
                            // the mailbox-launched work, which owns run faults per
                            // AUTOMATION-68 and always resolves the reply + posts
                            // the Aborted lifecycle). Nothing to post here: no run
                            // was launched.
                            Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                            return JsonSerializer.Serialize({| error = ex.Message |})
                    } ]
        | _ -> commands

    { Name = PluginName.create "test-prune"
      Init = initialState
      Update =
        fun ctx state event ->
            async {
                match event with
                | PluginEvent.FileChecked result ->
                    let analysisStarted = DateTime.UtcNow
                    let fileStr = AbsFilePath.value result.File
                    let relPath = Path.GetRelativePath(repoRoot, fileStr).Replace('\\', '/')

                    // AUTOMATION-113: the ONE treatment for a file whose symbol
                    // analysis failed (an `analyzeSource` Error and a handler
                    // fault are the same condition arrived at differently). The
                    // file is REMEMBERED as unanalysable — three consequences,
                    // none silent:
                    //   1. a WARNING lands in the error ledger, keyed to the
                    //      file, so `fshw check` prints it and (under the
                    //      default warn-fail policy) refuses a green verdict;
                    //   2. `runTestsWithImpact` falls back to EVERY test project
                    //      in full while the set is non-empty — safe
                    //      over-selection instead of a selection made without
                    //      the file;
                    //   3. the non-empty force-run set also disables the
                    //      zero-affected skip gate, so this can never terminate
                    //      as "0 affected — green, 0 ran".
                    // The file leaves the set the moment it analyses cleanly.
                    // The Failed stamp needs no idle guard here: the framework's
                    // ReportStatus funnel drops any terminal stamped while an
                    // exclusive run is in flight (the run owns the status), so a
                    // mid-run analysis failure can never manufacture a terminal
                    // — the ledger entry and the force-full-suite consequence
                    // persist either way.
                    let markUnanalysable (reason: string) (detail: string) (logDetail: string) : TestPruneState =
                        Logging.error
                            "test-prune"
                            $"%s{reason} for %s{relPath}: %s{logDetail} — this file is INVISIBLE to the impact graph (no symbols), so every test project will be run in full until it analyses cleanly"

                        ctx.ReportErrors fileStr [ unanalyzableFileDiagnostic relPath detail ]

                        ctx.ReportStatus(
                            PluginStatus.Failed(
                                $"%s{reason}: %s{detail}",
                                DateTime.UtcNow,
                                RunVerdict.create $"%s{reason}: %s{detail}" (DateTime.UtcNow - analysisStarted)
                            )
                        )

                        { state with
                            UnanalyzableFiles =
                                Map.add
                                    relPath
                                    { RelPath = relPath
                                      File = fileStr
                                      Reason = detail }
                                    state.UnanalyzableFiles }

                    try
                        // Canonical project identity. For real .fsproj files, FCS
                        // gives "MyProject.fsproj" → "MyProject". For .fsx scripts
                        // FCS synthesizes "Lib.fsx.fsproj" → "Lib.fsx" after one
                        // strip; drop the trailing ".fsx" so config that specifies
                        // `"Lib"` matches both cases.
                        let projectName =
                            let raw = result.ProjectOptions.ProjectFileName |> Path.GetFileNameWithoutExtension

                            if raw.EndsWith(".fsx") then
                                raw.Substring(0, raw.Length - 4)
                            else
                                raw

                        // Path D — fshw-owned per-file freshness sidecar gates the
                        // detectChanges call site. The F38 "withhold the symbol-DB
                        // write entirely" branch is gone: dirty FCS results no
                        // longer block persistence (cold-scan rows still go in).
                        // Instead the sidecar records `fcsClean = false` for the
                        // file so cross-restart Phase B replay treats those rows
                        // as untrusted-for-diff. The next clean recheck will both
                        // overwrite the rows with good extractions and flip the
                        // sidecar back to clean.
                        let currentClean =
                            not (hasFcsErrors ctx.FcsSuppressedCodes result.Source result.CheckResults)

                        let storedFreshness =
                            let store = Volatile.Read(&freshnessRef)
                            FileFreshness.classify relPath store

                        if not currentClean then
                            let errCount =
                                fcsErrorCount ctx.FcsSuppressedCodes result.Source result.CheckResults

                            Logging.warn
                                "test-prune"
                                $"FCS reported %d{errCount} error(s) for %s{relPath}; persisting symbols but marking file dirty in freshness sidecar (Phase B detectChanges will fall back for this file)"

                        let! analysisResult =
                            analyzeSource ctx.Checker fileStr result.Source result.ProjectOptions projectName

                        match analysisResult with
                        | Ok analysisResult ->
                            let normalizedSymbols = normalizeSymbolPaths repoRoot analysisResult.Symbols

                            let fileAnalysis =
                                { Symbols = normalizedSymbols
                                  Dependencies = analysisResult.Dependencies
                                  TestMethods =
                                    analysisResult.TestMethods
                                    |> List.map (fun t -> { t with TestProject = projectName })
                                  Attributes = analysisResult.Attributes
                                  ParentLinks = analysisResult.ParentLinks
                                  Diagnostics = analysisResult.Diagnostics }

                            // Read stored symbols from the in-memory snapshot (populated after
                            // each flush). Falls back to DB for warm starts where the snapshot
                            // hasn't been populated yet.
                            let storedSymbols =
                                match Map.tryFind relPath state.SymbolSnapshot with
                                | Some symbols -> symbols
                                | None -> db.GetSymbolsInFile(relPath)

                            // Accumulate per-project; flush on BuildCompleted.
                            // Replace any prior analysis for this file to avoid double-counting
                            // when a file is checked more than once before the flush (e.g. initial
                            // scan followed by a file-change recheck).
                            let existingForProject =
                                state.PendingAnalysis |> Map.tryFind projectName |> Option.defaultValue []

                            let filteredExisting =
                                existingForProject
                                |> List.filter (fun a ->
                                    not (a.Symbols |> List.exists (fun s -> s.SourceFile = relPath)))

                            let newPending =
                                state.PendingAnalysis
                                |> Map.add projectName (filteredExisting @ [ fileAnalysis ])

                            // Path D gate: decide whether the stored rows can be
                            // diffed against. Requires the CURRENT extraction to be
                            // FCS-clean (a dirty current result means the
                            // just-extracted symbols are themselves suspect). Given
                            // that, the STORED side is trusted per FileFreshness.classify:
                            //
                            //   Clean   — explicit clean stamp → diff.
                            //   Unknown — NO sidecar record. Over a NON-EMPTY stored
                            //             row set this is a seeded test-impact.db
                            //             (ADR-010) whose freshness sidecar didn't
                            //             travel into this fresh workspace. ADR-010
                            //             guarantees a seeded DB over-indexes but
                            //             never serves a stale verdict, so DIFF the
                            //             seeded rows: unchanged files find no
                            //             changes, edited files find the real delta
                            //             and select their covering tests. Bypassing
                            //             here (the pre-AUTOMATION-67 behaviour)
                            //             silently UNDER-selected — a real edit in a
                            //             seeded workspace ran zero tests → vacuous
                            //             green. Gated on stored rows existing: an
                            //             EMPTY stored set is a genuine cold scan
                            //             (empty DB) with no baseline to diff, which
                            //             stays no-diff so it doesn't select the
                            //             whole suite.
                            //   Dirty   — explicit `fcsClean = false` stamp: the
                            //             stored rows were written during an FCS-error
                            //             extraction and may be PARTIAL. Diffing
                            //             against them yields a phantom "all symbols
                            //             changed" delta (the 4921-affected-tests
                            //             Phase B regression). Bypass.
                            let storedTrustedForDiff =
                                match storedFreshness with
                                | FileFreshness.Clean -> true
                                | FileFreshness.Unknown -> not storedSymbols.IsEmpty
                                | FileFreshness.Dirty -> false

                            let (changedNames, suppressedDiff) =
                                if currentClean && storedTrustedForDiff then
                                    // detectChanges filters externs internally; no pre-filter needed here.
                                    let (changes, _events) = detectChanges normalizedSymbols storedSymbols

                                    Logging.info
                                        "test-prune"
                                        $"detectChanges for %s{relPath} (stored=%A{storedFreshness}): %d{changes.Length} changes, %d{storedSymbols.Length} stored, %d{normalizedSymbols.Length} current"

                                    changedSymbolNames changes, false
                                else
                                    Logging.info
                                        "test-prune"
                                        $"detectChanges bypassed for %s{relPath} (currentClean=%b{currentClean}, stored=%A{storedFreshness}, storedRows=%d{storedSymbols.Length}); falling back to no-diff for this file"

                                    [], true

                            ignore suppressedDiff

                            let newChangedSymbols =
                                if not changedNames.IsEmpty then
                                    // These are the symbols fed straight into
                                    // `enqueuePending`, so this line is the primary
                                    // evidence when diagnosing over- or
                                    // under-selection (see the phantom
                                    // all-symbols-changed regression noted above).
                                    // It previously carried NO count at all and
                                    // capped the list at 100.
                                    Logging.info "test-prune" $"Changed symbols: %s{describeMany changedNames}"

                                    // Write-through to the durable needs-testing queue at the
                                    // SAME point the in-memory hot view accumulates. Persisted
                                    // here (before the BatchChecked analysis flush) so a crash
                                    // between this and the DB rebuild leaves the symbols QUEUED
                                    // — over-testing is the safe direction. They leave the queue
                                    // only when a covering test run passes (TestsFinished) or
                                    // they prove to have no covering test (flushAndQueryAffected).
                                    enqueuePending changedNames

                                    (state.ChangedSymbols @ changedNames) |> List.distinct
                                else
                                    state.ChangedSymbols

                            // Only track file as changed if its AST actually changed.
                            // Comment-only changes produce the same symbol hashes, so they
                            // should not trigger extension-based tests (e.g. Falco routes).
                            let newChangedFiles =
                                if not changedNames.IsEmpty && not (state.ChangedFiles |> List.contains relPath) then
                                    relPath :: state.ChangedFiles
                                else
                                    state.ChangedFiles

                            // Update class→file mapping for test methods found in this file
                            let newClassFiles =
                                fileAnalysis.TestMethods
                                |> List.fold (fun acc t -> Map.add t.TestClass fileStr acc) state.TestClassFiles

                            // AffectedTests is no longer eagerly populated here. The
                            // `affected-tests` IPC command computes it on demand from
                            // state.ChangedSymbols against the current DB. AffectedTests
                            // is set exclusively by flushAndQueryAffected on BuildCompleted
                            // and consumed by runTestsWithImpact.
                            let newState =
                                { state with
                                    ChangedFiles = newChangedFiles
                                    PendingAnalysis = newPending
                                    ChangedSymbols = newChangedSymbols
                                    TestClassFiles = newClassFiles
                                    // The file analysed cleanly, so it is back in the impact
                                    // graph and no longer owes the coarse fallback. The
                                    // framework already cleared this plugin's ledger entries
                                    // for the file when the FileChecked arrived, and dropping
                                    // it here means the next test run's ledger rewrite stops
                                    // re-reporting the warning (AUTOMATION-125): the entry
                                    // leaves the ledger because the CONDITION cleared, which
                                    // is the only reason any entry may leave it.
                                    UnanalyzableFiles = Map.remove relPath state.UnanalyzableFiles }

                            // Keep the mutable snapshot in sync for the cache key function
                            Volatile.Write(&changedSymbolsRef, newState.ChangedSymbols)

                            // Stamp the freshness sidecar with the result of THIS check.
                            // Done after analysis (rather than at the very top) so a
                            // failed `analyzeSource` doesn't lock in a clean stamp for
                            // a file we have no symbols for.
                            //
                            // Item 3 gate: only `markClean` if we've observed a
                            // BuildCompleted in this session AND the current FCS
                            // result is clean. fshw's pipeline guarantees
                            // BuildCompleted reaches the mailbox before any
                            // FileChecked on a cold scan, so post-build clean
                            // stamps fire on the very first session — no
                            // two-session warm-up required. Otherwise
                            // `markUnverified` (won't downgrade a previously-clean
                            // entry to dirty — see FileFreshness.markUnverified).
                            let now = DateTime.UtcNow

                            let updatedFreshness =
                                let prior = Volatile.Read(&freshnessRef)

                                if currentClean && state.BuildCompletedInThisSession then
                                    FileFreshness.markClean now relPath prior
                                else
                                    FileFreshness.markUnverified relPath prior

                            updateFreshness updatedFreshness

                            // AUTOMATION-99: no per-plugin idle guard — the framework's
                            // ReportStatus funnel drops any terminal stamped while an
                            // exclusive run is in flight (the run OWNS the status;
                            // TestsFinished delivers the earned verdict). Universal
                            // property, not a convention this handler must remember.
                            let analysisFinished = DateTime.UtcNow

                            ctx.ReportStatus(
                                Completed(
                                    analysisFinished,
                                    RunVerdict.create
                                        $"symbol analysis: %s{relPath}, %d{List.length changedNames} changed symbol(s); no run due"
                                        (analysisFinished - analysisStarted)
                                )
                            )

                            return newState
                        | Error msg ->
                            // On analysis failure the file must NOT be dropped: a
                            // dropped file contributes no symbols, a change to it diffs
                            // against nothing and selects NO tests, and the check reports
                            // green having run nothing relevant. Silent under-selection:
                            // the one failure mode a test-impact tool must not have. See
                            // `markUnanalysable` for the treatment.
                            return markUnanalysable "Analysis failed" msg msg

                    with ex ->
                        // F10 ∩ AUTOMATION-99/113: a fault ANYWHERE in this handler
                        // (not just an `analyzeSource` Error) leaves the file
                        // unanalysed — the SAME condition as an analysis failure,
                        // so it gets the SAME treatment (`markUnanalysable`).
                        return markUnanalysable "FileChecked handler failed" ex.Message (ex.ToString())

                | PluginEvent.BatchChecked _ ->
                    // Cohort-complete flush. Per-file accumulation already
                    // happened in the FileChecked handler; by the time we get
                    // here every FileChecked from this cohort has been folded
                    // into state.ChangedSymbols / state.PendingAnalysis
                    // (mailbox is FIFO and the daemon emits BatchChecked
                    // strictly after the last FileChecked).
                    //
                    // We persist PendingAnalysis to the DB here — this is the
                    // canonical persistence point, NOT BuildCompleted. On a
                    // cold scan, performScan awaits BuildPlugin terminal
                    // BEFORE running FCS tier checks (Daemon.fs:1195) so
                    // BuildCompleted reaches the TestPrune mailbox BEFORE any
                    // FileChecked. If the flush only ran on BuildCompleted, it
                    // would always fire against an empty PendingAnalysis on
                    // cold scans, leaving the symbol DB permanently empty.
                    // BatchChecked owning the flush makes the persistence
                    // independent of event ordering.
                    //
                    // BuildCompleted's flush is retained as an idempotent
                    // re-run (PendingAnalysis is already empty by then,
                    // RebuildProjects is skipped) plus the test-trigger.
                    let flushed =
                        try
                            Ok(flushAndQueryAffected state)
                        with ex ->
                            Error ex

                    match flushed with
                    | Error ex ->
                        Logging.error "test-prune" $"BatchChecked flushAndQueryAffected failed: %s{ex.Message}"
                        tryRepairSchemaDrift ex
                        return state
                    | Ok flushedState ->
                        Volatile.Write(&changedSymbolsRef, flushedState.ChangedSymbols)

                        // ── AUTOMATION-95/99: DRAIN THE PENDING QUEUE ────────────────
                        // The cohort seal is the first moment the symbols this scan
                        // discovered are known. Before this, `BuildCompleted` was the
                        // ONLY trigger that ran tests — and on a scan it fires BEFORE
                        // the FCS pass (Daemon.fs `performScan` awaits the build, then
                        // dispatches FCS tiers), so scan-discovered symbols could never
                        // be verified by the run that BuildCompleted launched. They
                        // silently accumulated in the queue while `check` reported the
                        // plugin's stale terminal status — green (95) or red (99).
                        //
                        // Now: if symbols remain unverified here, we RUN the tests that
                        // verify them. Convergence, not reporting. A verdict is only
                        // ever earned by a run.
                        //
                        // The skip asks whether anything is owed, so it must ask
                        // `nothingOwed`, not `Set.isEmpty`. An UNREADABLE ledger leaves
                        // the in-memory queue empty (we cannot name what it held) — and
                        // reading that empty set as "nothing to drain" would let a
                        // corrupt sidecar run ZERO tests and still go green.
                        if nothingOwed () then
                            return flushedState
                        else
                            match testConfigs with
                            | Some configs when not configs.IsEmpty ->
                                // Drain UNCONDITIONALLY when the slot is free. An earlier
                                // revision of this fix tried to be clever and defer to "the
                                // BuildCompleted that is surely coming" whenever a build
                                // looked to be in flight. That is unsound: several
                                // `FileChanged` shapes never produce a BuildCompleted at all
                                // (BuildPlugin ignores `SolutionChanged`, and DROPS a
                                // FileChanged that arrives while a build is already
                                // running), so the deferral could wait forever. Caught by CI
                                // on this very branch: the gate deferred 3 symbols to a
                                // BuildCompleted that never came, ran ZERO tests, and still
                                // exited 0 — the exact false green this fix exists to kill.
                                // Never predict a future event as a reason to skip work you
                                // already owe.
                                //
                                // Draining here is safe even on a half-built tree: the
                                // apphost-freshness gate in `executeTests` already refuses
                                // to run `--no-build` against a compiled artifact older
                                // than its sources. It defers that project as an honest
                                // "waiting on build" WITHOUT spawning a test process, so
                                // the stale case costs a status flip, not a run — and the
                                // BuildCompleted that follows re-runs it for real.
                                //
                                // The claim is ATTEMPTED, not pre-checked: `RunExclusive`
                                // returns the claim outcome, so there is no TOCTOU between
                                // an `IsRunning` read and the launch.
                                let hasCachedResults = flushedState.LastResults.IsSome
                                let forceRunProjects = flushedState.PendingForceRunProjects

                                let drainedState =
                                    { flushedState with
                                        PendingForceRunProjects = Set.empty }

                                match
                                    ctx.RunExclusive
                                        "tests"
                                        (runTestsWithImpact
                                            ctx
                                            configs
                                            (TestRunInputs.ofState drainedState)
                                            hasCachedResults
                                            forceRunProjects)
                                with
                                | Claimed ->
                                    Logging.info "test-prune" $"BatchChecked: %s{owedDescription ()} — draining now"

                                    return drainedState
                                | SlotBusy ->
                                    // A run is in flight but was launched against an older
                                    // queue snapshot, so it cannot clear these symbols.
                                    // Queue the rerun — TestsFinished drains it. The pending
                                    // fanout is retained (the work was NOT consumed).
                                    Logging.info
                                        "test-prune"
                                        $"BatchChecked: %s{owedDescription ()} still outstanding while a run is in flight — queueing re-run"

                                    return
                                        { flushedState with
                                            PendingRerun = true }
                            | _ ->
                                // Analysis-only (no test configs): nothing can verify
                                // these symbols, so there is nothing to drain.
                                return flushedState

                | PluginEvent.BuildCompleted buildResult ->
                    match buildResult with
                    | BuildSucceeded ->
                        // Item 3: record that BuildCompleted has fired this
                        // session. Subsequent FileChecked events are now
                        // allowed to promote the freshness sidecar to clean.
                        // Set unconditionally for both the queued-rerun and
                        // run-now branches — the gate is about "has the build
                        // process realized the reference graph yet", not
                        // about whether tests are queued.
                        let state =
                            { state with
                                BuildCompletedInThisSession = true }

                        // ── Dependency-fanout fingerprint (computed for EVERY
                        // build, before the running/idle split, so the prior
                        // fingerprint always advances and a mid-run dependency
                        // change is never lost). Fingerprint each test project from
                        // its referenced-project DLL hashes + own package versions
                        // (DependencyFanout). A project whose fingerprint moved
                        // since the last build had a dependency/binary change the
                        // symbol diff can't see → force-run it. An empty graph
                        // (tests / no graph wired) yields no fanout, so the
                        // symbol-precise path is unchanged.
                        let fsprojByName =
                            ctx.ProjectGraph.GetAllProjects()
                            |> List.map (fun p -> Path.GetFileNameWithoutExtension p, p)
                            |> Map.ofList

                        let currentFingerprints =
                            match testConfigs with
                            | Some configs ->
                                configs
                                |> List.choose (fun c ->
                                    Map.tryFind c.Project fsprojByName
                                    |> Option.map (fun fsproj -> c.Project, fsproj))
                                |> List.map (fun (name, fsproj) ->
                                    name, DependencyFanout.computeProjectFingerprint ctx.ProjectGraph fsproj)
                                |> Map.ofList
                            | None -> Map.empty

                        let fanoutNow =
                            DependencyFanout.changedProjects state.PriorProjectFingerprints currentFingerprints

                        if not (Set.isEmpty fanoutNow) then
                            Logging.info
                                "test-prune"
                                $"Dependency fanout: %d{Set.count fanoutNow} test project(s) had a \
                                  dependency-fingerprint change — force-running: \
                                  %s{describeAll (Set.toList fanoutNow)}"
                        else
                            // Say that NOTHING fanned out, and say what was examined to
                            // reach that conclusion. Silence here is ambiguous in the
                            // worst possible way: "no dependency changed" and
                            // "fingerprinting never ran at all" produce byte-identical
                            // logs, so an inert `computeProjectFingerprint` — which
                            // would mean the safety net against binary-only dependency
                            // changes is quietly OFF, an under-selection risk — is
                            // indistinguishable from a healthy quiet run. The two
                            // counts below settle it: zero fingerprints computed, or
                            // zero graph projects, means inert rather than clean.
                            Logging.info
                                "test-prune"
                                $"Dependency fanout: none — \
                                  %d{currentFingerprints.Count} project fingerprint(s) computed, \
                                  %d{fsprojByName.Count} project(s) in the graph, \
                                  %d{state.PriorProjectFingerprints.Count} prior fingerprint(s) to compare against"

                        // Advance the baseline on every build; carry any not-yet-run
                        // fanout in the pending set (consumed by the queued rerun).
                        let state =
                            { state with
                                PriorProjectFingerprints = currentFingerprints }

                        if ctx.IsRunning "tests" then
                            // Leading "  ↳ " (↳) indents this entry one
                            // level beyond test-result lines in the activity-fold
                            // `recent:` view. The renderer adds 8 spaces to every
                            // tail entry; we add 2 more here so it visually nests
                            // under the in-flight test run rather than reading as
                            // a sibling of the test-result lines.
                            ctx.Log "  ↳ queued re-run (tests already running)"

                            Logging.info
                                "test-prune"
                                "BuildSucceeded received but tests already running — will re-run after"

                            // Stash the fanout so the rerun runs it (don't lose a
                            // mid-run dependency change).
                            return
                                { state with
                                    PendingRerun = true
                                    PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
                        else
                            Logging.info "test-prune" "BuildSucceeded: starting test run"

                            // Flush/query before announcing Running so the reported status never
                            // lies: announcing Running before the flush would flash Running even
                            // on a schema-drifted DB.
                            // The framework catches uncaught throws and forces Failed as a
                            // defense-in-depth net; we still trap locally here so we can run
                            // the schema-drift self-heal and preserve the idle transition.
                            match
                                (try
                                    Ok(flushAndQueryAffected state)
                                 with ex ->
                                     Error ex)
                            with
                            | Error ex ->
                                Logging.error "test-prune" $"flushAndQueryAffected failed: %s{ex.Message}"
                                tryRepairSchemaDrift ex

                                ctx.ReportStatus(
                                    PluginStatus.failedNow ex.Message $"flush failed: %s{ex.Message}" TimeSpan.Zero
                                )

                                return state
                            | Ok stateWithAffected ->
                                match testConfigs with
                                | Some configs when not configs.IsEmpty ->
                                    let hasCachedResults = state.LastResults.IsSome

                                    // Union this build's fanout with any pending
                                    // fanout deferred from a prior mid-run build,
                                    // then clear the pending set (it's being run).
                                    let forceRunProjects = Set.union fanoutNow stateWithAffected.PendingForceRunProjects

                                    let launchState =
                                        { stateWithAffected with
                                            PendingForceRunProjects = Set.empty }

                                    match
                                        ctx.RunExclusive
                                            "tests"
                                            (runTestsWithImpact
                                                ctx
                                                configs
                                                (TestRunInputs.ofState launchState)
                                                hasCachedResults
                                                forceRunProjects)
                                    with
                                    | Claimed -> return launchState
                                    | SlotBusy ->
                                        // Raced by another launch between the IsRunning
                                        // fast-path above and this claim. Same treatment:
                                        // queue the rerun, retain the un-consumed fanout.
                                        Logging.info
                                            "test-prune"
                                            "BuildSucceeded: tests slot already held — queueing re-run"

                                        return
                                            { stateWithAffected with
                                                PendingRerun = true
                                                PendingForceRunProjects = forceRunProjects }
                                | _ ->
                                    // No test configs — flush only; nothing to run.
                                    return stateWithAffected
                    | BuildFailed _ -> return state

                | Custom(TestsFinished(started, completed, launch)) ->
                    // §2a: emit lifecycle events synchronously here (inside the framework's
                    // per-event capture window) so they're recorded in the cached
                    // EmittedEvents and re-fired on cache replay. Live per-group
                    // TestProgress already fired from the async; subscribers that key off
                    // TestRunCompleted (e.g. FileCommandPlugin) must see it on cache hit.
                    ctx.EmitTestRunStarted started
                    ctx.EmitTestRunCompleted completed

                    // Apply error reporting synchronously here too — live emission from
                    // the async wouldn't be captured for cache replay.
                    let testResults: TestResults =
                        { Results = completed.Results
                          Elapsed = completed.TotalElapsed }

                    // AUTOMATION-125 — a run may clear ONLY what it COVERED.
                    //
                    // Issue 2 (AUTOMATION-95) established that the ledger must be
                    // REWRITTEN each cycle rather than appended to, so a superseded red
                    // can't linger. It rewrote it from THIS run's failures alone — which
                    // meant a narrower run's green erased reds it had never executed
                    // (observed live: full run fails project X → queued impact-filtered
                    // re-run passes 2 symbols → X's red gone, X never re-ran).
                    //
                    // Both properties hold once the rewrite is from the OUTSTANDING set:
                    // what this run found, PLUS every earlier red it did not cover. The
                    // coverage is derived from the run's own launch selection — so a red
                    // dies only to evidence that actually executed it, and dies the
                    // moment such evidence exists (no stuck-red: cf. AUTOMATION-99).
                    //
                    // AUTOMATION-225: the launch selection cannot express the reach of a
                    // raw `--filter` passthrough (it records every project as
                    // `ProjectInFull`), so `test-rerun --filter-class X` used to re-run X,
                    // pass, and leave X's red standing — for good. The run's OWN report
                    // knows what it executed, so hand `ofRun` the classes it shows passing.
                    // Read from THIS run's directory only, and empty whenever the report is
                    // missing or incomplete.
                    let coverage =
                        RunCoverage.ofRun
                            launch.Selection
                            completed.Results
                            (passedClassesOfRun repoRoot completed.RunId)

                    let foundFailures = failuresOf state.TestClassFiles testResults

                    let carriedFailures =
                        OutstandingFailure.carriedOver runnableProjects coverage state.OutstandingFailures

                    let outstandingFailures =
                        OutstandingFailure.carry runnableProjects coverage foundFailures state.OutstandingFailures

                    if not carriedFailures.IsEmpty then
                        Logging.info
                            "test-prune"
                            $"%d{carriedFailures.Length} failure(s) from an earlier run were NOT covered by this one (%s{OutstandingFailure.summarize carriedFailures}) — they stay RED until a run that executes them passes"

                    // The ONLY path to the ledger: clear the slate, re-report the whole
                    // outstanding set. There is no wholesale clear a filtered run can
                    // reach for.
                    reportOutstanding ctx state.UnanalyzableFiles outstandingFailures
                    Volatile.Write(&outstandingFailuresRef, outstandingFailures)

                    // AUTOMATION-161. THIS is the moment the process acquires test
                    // evidence — a run completed and we know what it covered. Until it
                    // happens, the cache key intercept refuses to let a cached
                    // BuildCompleted assert a result this process never ran.
                    Volatile.Write(&sessionCoverageRef, coverage)

                    // Carried into EVERY return branch below (rerun-drain, queued
                    // force-run, idle) by rebinding here — a branch that forgot would
                    // silently resurrect the laundering bug. `LastCoverage` rides along
                    // as the receipt of what this run covered, for consumers outside the
                    // handler.
                    let state =
                        { state with
                            OutstandingFailures = outstandingFailures
                            LastCoverage = coverage }

                    // Outcome-conditional, per-project green-commit. A launched
                    // symbol leaves the needs-testing queue ONLY when the run
                    // genuinely covered it green:
                    //   - the run did NOT abort (a beforeRun throw / crash gives
                    //     Outcome = Aborted, Results = Map.empty → commit nothing), AND
                    //   - EVERY project covering the symbol produced a PASSED result.
                    // A project counts as passed only if it appears in completed.Results
                    // with TestResult.isPassed — a covering project ABSENT from the
                    // results (didn't run this cycle) blocks the commit (we can't claim
                    // it green). A symbol with NO covering project was already dropped at
                    // flush time, but if one slipped through it commits here (nothing to
                    // wait on). Mid-run arrivals are NOT in launch.Symbols, so they stay
                    // queued and the PendingRerun flow re-runs them.
                    let aborted =
                        match completed.Outcome with
                        | Aborted _ -> true
                        | Normal -> false

                    let projectPassed (proj: string) =
                        match Map.tryFind proj completed.Results with
                        | Some r -> TestResult.isPassed r
                        | None -> false

                    let committedSymbols =
                        if aborted then
                            Set.empty
                        else
                            launch.Symbols
                            |> Set.filter (fun s ->
                                match Map.tryFind s launch.CoveringProjectsBySymbol with
                                | Some projs when not (Set.isEmpty projs) -> projs |> Set.forall projectPassed
                                | _ -> true)

                    if not (Set.isEmpty committedSymbols) then
                        Logging.info
                            "test-prune"
                            $"Committing %d{Set.count committedSymbols} verified symbol(s) — removing from pending-verification queue"

                        commitPending committedSymbols

                    // AUTOMATION-150 — discharge an UNREADABLE ledger's debt.
                    //
                    // The debt is owed in FULL, because its membership is unknown: the only
                    // run that can retire it is one that executed EVERY runnable project,
                    // unfiltered, and passed. At that point every symbol the lost ledger
                    // could possibly have held has been verified by an actual test run, so
                    // there is nothing left for it to owe.
                    //
                    // Each conjunct is load-bearing:
                    //  * `not aborted` — an aborted run has empty Results and verified nothing.
                    //  * every RUNNABLE project passed — `projectPassed` demands the project
                    //    be PRESENT in the results AND green, so a project that never ran
                    //    cannot be counted. This also rules out the degenerate zero-ran
                    //    lifecycle (empty Results ⇒ no project passes).
                    //  * `RanFullSuite` — none of those projects was impact-FILTERED. It is
                    //    vacuously true for an empty Results map, which is why it is an
                    //    addition to the check above and never a substitute for it.
                    //  * a non-empty `runnableProjects` — an analysis-only daemon runs no
                    //    tests, so it can never prove anything and must not discharge.
                    //
                    // Only now may the ledger be rewritten: `persistQueue` has deliberately
                    // left the corrupt file untouched until this moment, so that a crash
                    // mid-recovery leaves the next session the same honest "unknown" rather
                    // than a clean, empty, WRONG ledger.
                    if
                        Volatile.Read(&ledgerRecoveryOutstandingRef)
                        && not aborted
                        && not (Set.isEmpty runnableProjects)
                        && completed.RanFullSuite
                        && runnableProjects |> Set.forall projectPassed
                    then
                        Volatile.Write(&ledgerRecoveryOutstandingRef, false)
                        persistQueue " after recovering an unreadable ledger"

                        Logging.info
                            "test-prune"
                            "A full suite passed every configured project — the unreadable pending-verification ledger has been rewritten and its unknown debt discharged. Impact filtering resumes."

                    // The in-memory hot view must shed ONLY the committed symbols,
                    // never the whole list — symbols left in the queue (mid-run
                    // arrivals, projects that failed/aborted) must keep selecting
                    // tests until a covering run passes. `queueAfterCommit` is the
                    // post-commit durable queue; it drives the cleared ChangedSymbols
                    // and the cache-key snapshot in every return branch below.
                    let queueAfterCommit = pendingQueueRef
                    let remainingChangedSymbols = queueAfterCommit |> Set.toList

                    // Pushing a terminal Completed/Failed status is what appends the
                    // run to history; both rerun and final-idle branches must call this.
                    let recordRunOutcome (results: TestResults) =
                        let total = results.Results.Count

                        // AUTOMATION-125. Reds this run did not cover are still RED —
                        // they must deny it a green verdict exactly as its own failures
                        // would, or `check` exits 0 with a failing test outstanding
                        // (the whole defect). Named in every non-green message below so
                        // the reason a passing run is not green is never a mystery.
                        let carriedCount = carriedFailures.Length

                        let carriedNote =
                            if carriedCount = 0 then
                                ""
                            else
                                $" (+%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures})"

                        // §3a — verdict hardening: consult completed.Outcome FIRST.
                        // An Aborted run (beforeRun threw, runner crashed, run
                        // cancelled) MUST be non-green regardless of result counts —
                        // empty results trivially satisfy "failed = 0 && deferred = 0"
                        // and would otherwise false-green. Surface the abort message.
                        // §3b — a run that executed ZERO projects while the pending
                        // queue still holds symbols verified nothing: reuse the honest
                        // "waiting on build (tests did not run)" deferred path rather
                        // than reporting a green that tested nothing.
                        let abortMessage =
                            match completed.Outcome with
                            | Aborted reason -> Some reason
                            | Normal -> None

                        match abortMessage with
                        | Some reason ->
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"test run aborted (tests did not run): %s{reason}%s{carriedNote}"
                                    $"test run aborted: %s{reason}%s{carriedNote}"
                                    results.Elapsed
                            )
                        | None when total = 0 && not (Set.isEmpty queueAfterCommit) ->
                            // Zero projects executed but symbols still await
                            // verification — honest non-green, same wording/path as a
                            // deferred (never-ran) project.
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)%s{carriedNote}"
                                    $"0 projects ran; symbols still awaiting verification%s{carriedNote}"
                                    results.Elapsed
                            )
                        | None ->

                            // Non-green = anything not passed. Split into genuine
                            // failures vs deferred (never-ran) so the verdict can be
                            // honest: deferred is non-green but is "could not run /
                            // waiting on build", NOT "failed".
                            let nonGreen =
                                results.Results
                                |> Map.toList
                                |> List.filter (fun (_, r) -> not (TestResult.isPassed r))

                            let deferredList = nonGreen |> List.filter (fun (_, r) -> TestResult.isDeferred r)

                            let failedList =
                                nonGreen |> List.filter (fun (_, r) -> not (TestResult.isDeferred r))

                            let failed = failedList.Length
                            let deferred = deferredList.Length
                            let passed = total - failed - deferred

                            let anyFiltered =
                                results.Results |> Map.exists (fun _ r -> TestResult.wasFiltered r)

                            let selectedSuffix = if anyFiltered then "yes" else "no"

                            let timedOutProjects =
                                failedList
                                |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then Some name else None)

                            // When 2+ projects ran and at least one has recorded elapsed,
                            // surface the slowest in the summary so a bottlenecked project
                            // is visible without having to query test-results JSON.
                            let slowestSuffix =
                                if total < 2 then
                                    ""
                                else
                                    let withElapsed =
                                        results.Results
                                        |> Map.toList
                                        |> List.choose (fun (name, r) ->
                                            let e = TestResult.elapsed r
                                            if e > TimeSpan.Zero then Some(name, e) else None)

                                    match withElapsed with
                                    | [] -> ""
                                    | _ ->
                                        let (n, e) = withElapsed |> List.maxBy snd
                                        $", slowest: %s{n} %.1f{e.TotalSeconds}s"

                            let deferredSuffix =
                                if deferred > 0 then
                                    $", %d{deferred} waiting on build"
                                else
                                    ""

                            if not timedOutProjects.IsEmpty then
                                let names = timedOutProjects |> String.concat ", "
                                // Flip the recorded outcome to TimedOut; the verdict on
                                // the Failed below carries the summary (one channel).
                                ctx.CompleteWithTimeout $"test project(s): {names}"

                                ctx.ReportStatus(
                                    PluginStatus.failedNow
                                        $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                                        $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                                        results.Elapsed
                                )
                            else
                                let runSummary =
                                    $"%d{passed} passed, %d{failed} failed%s{deferredSuffix} in %d{total} projects (selected: %s{selectedSuffix}%s{slowestSuffix})"

                                // EVERY terminal below CARRIES the run's evidence —
                                // `runSummary` + measured duration — on the status
                                // itself. There is no separate summary channel left to
                                // forget or contradict (AUTOMATION-99).
                                if failed = 0 && deferred = 0 && Set.isEmpty queueAfterCommit && carriedCount = 0 then
                                    ctx.ReportStatus(
                                        Completed(DateTime.UtcNow, RunVerdict.create runSummary results.Elapsed)
                                    )
                                elif failed = 0 && deferred = 0 && Set.isEmpty queueAfterCommit then
                                    // AUTOMATION-125. Everything this run RAN passed, the
                                    // queue is drained — and yet an earlier failure it did
                                    // not execute is still outstanding. A narrower run
                                    // cannot vindicate a test it never ran, so this is
                                    // NON-green: the red survives until a run that COVERS
                                    // it passes (`dotnet fshw test-rerun` runs every
                                    // project unfiltered and will clear it if it is fixed).
                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )
                                elif failed = 0 && deferred = 0 then
                                    // Everything that RAN passed, but the pending queue
                                    // still holds symbols this (e.g. filtered) run did not
                                    // cover green — NOT test-equivalent to a green run yet.
                                    // Non-green with the honest "waiting on build" wording;
                                    // the next BuildCompleted re-selects and runs them.
                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)%s{carriedNote}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )
                                elif failed = 0 then
                                    // Only deferred projects — nothing FAILED, but
                                    // nothing was verified either. Non-green, honest
                                    // "waiting on build" (never "failed").
                                    let names = deferredList |> List.map fst |> String.concat ", "

                                    if carriedCount = 0 then
                                        // PURE defer: no red at all this run OR carried.
                                        // Report a NON-failing terminal so the verdict
                                        // reads the `Deferred`-severity ledger entry (from
                                        // `failuresOf`) and routes it to
                                        // `Incomplete`/exit 2 ("waiting on build"), NOT the
                                        // exit 1 a red earns. The status is not "failed" —
                                        // the run did what it could; a build-ordering race
                                        // left one project unrun. The deferred projects
                                        // stay in the pending-verification queue
                                        // (`isPassed`=false, so their symbols never commit),
                                        // so the next build re-runs them, and the result is
                                        // UNCACHEABLE (`isPassed`=false), so no green is ever
                                        // pinned. The `OutstandingFailure` list still carries
                                        // the defer, so cache participation stays refused.
                                        ctx.ReportStatus(
                                            PluginStatus.completedNow
                                                $"%s{runSummary} — %d{deferred} waiting on build (tests did not run): %s{names}"
                                                results.Elapsed
                                        )
                                    else
                                        // A carried RED from an earlier run is still
                                        // outstanding — that dominates a defer. Stay
                                        // Failed/red (exit 1); the ledger's carried Error
                                        // entry independently keeps the verdict red.
                                        ctx.ReportStatus(
                                            PluginStatus.failedNow
                                                $"%d{deferred} waiting on build (tests did not run): %s{names}%s{carriedNote}"
                                                $"%s{runSummary}%s{carriedNote}"
                                                results.Elapsed
                                        )
                                else
                                    let names = failedList |> List.map fst |> String.concat ", "

                                    let deferredNote =
                                        if deferred > 0 then
                                            let dn = deferredList |> List.map fst |> String.concat ", "
                                            $" (%d{deferred} waiting on build: %s{dn})"
                                        else
                                            ""

                                    ctx.ReportStatus(
                                        PluginStatus.failedNow
                                            $"%d{failed} failed: %s{names}%s{deferredNote}%s{carriedNote}"
                                            $"%s{runSummary}%s{carriedNote}"
                                            results.Elapsed
                                    )

                    // Drain order after a completed run:
                    //   1. a queued `run-tests` force-run — an IPC caller is WAITING
                    //      on its reply (bounded, but waiting), so it goes first;
                    //      FIFO, one per completed run (each queued run's own
                    //      TestsFinished drains the next);
                    //   2. the impact rerun (`PendingRerun`) — no waiter; it survives
                    //      across queued command runs and drains when the queue is
                    //      empty;
                    //   3. idle.
                    match state.QueuedCommandRuns with
                    | (queuedConfigs, queuedFilter, queuedReply) :: laterRuns ->
                        Volatile.Write(&changedSymbolsRef, remainingChangedSymbols)
                        recordRunOutcome testResults

                        let dequeuedState =
                            { state with
                                LastResults = Some testResults
                                LastRunId = Some completed.RunId
                                ChangedFiles = []
                                ChangedSymbols = remainingChangedSymbols
                                AffectedTests = Analyzed []
                                QueuedCommandRuns = laterRuns }

                        match ctx.RunExclusive "tests" (commandForceRun queuedConfigs queuedFilter queuedReply) with
                        | Claimed ->
                            Logging.info "test-prune" "Launching queued run-tests force-run"
                            return dequeuedState
                        | SlotBusy ->
                            // Unreachable in practice — every "tests" claim happens on
                            // this mailbox thread, and the slot was freed before this
                            // TestsFinished was posted — but typed anyway: keep the
                            // run QUEUED rather than dropping owed work.
                            return
                                { dequeuedState with
                                    QueuedCommandRuns = state.QueuedCommandRuns }
                    | [] when state.PendingRerun ->
                        Logging.info "test-prune" "Re-running tests (queued during previous run)"

                        // Flush any new pending analysis against CURRENT state — picking up any
                        // FileChecked symbols that landed between the queueing BuildCompleted
                        // and now. ChangedSymbols is reset to the POST-COMMIT queue
                        // (committed symbols removed, still-pending + mid-run arrivals
                        // retained) so the rerun re-selects exactly what hasn't been
                        // proven green. flushAndQueryAffected unions this with the durable
                        // queue, so the rerun keeps testing the unverified symbols. If the
                        // DB errors out here the rerun never happens, so we must bail back
                        // to idle (capturing testResults) instead of leaving PendingRerun
                        // stuck and the slot already freed.
                        match
                            (try
                                Ok(
                                    flushAndQueryAffected
                                        { state with
                                            PendingRerun = false
                                            ChangedSymbols = remainingChangedSymbols }
                                )
                             with ex ->
                                 Error ex)
                        with
                        | Error ex ->
                            Logging.error "test-prune" $"flushAndQueryAffected (rerun) failed: %s{ex.Message}"
                            tryRepairSchemaDrift ex

                            ctx.ReportStatus(
                                PluginStatus.failedNow ex.Message $"rerun flush failed: %s{ex.Message}" TimeSpan.Zero
                            )

                            return
                                { state with
                                    LastResults = Some testResults
                                    LastRunId = Some completed.RunId
                                    PendingRerun = false
                                    ChangedFiles = []
                                    ChangedSymbols = remainingChangedSymbols
                                    AffectedTests = Analyzed [] }
                        | Ok rerunState ->
                            recordRunOutcome testResults
                            Volatile.Write(&changedSymbolsRef, rerunState.ChangedSymbols)

                            // Consume the deferred dependency-fanout: a build that
                            // landed mid-run stashed its changed test projects here
                            // (it couldn't run them then). The rerun runs them now,
                            // alongside the queued symbols. Clear so a later rerun
                            // doesn't re-run them.
                            let deferredFanout = rerunState.PendingForceRunProjects

                            let rerunState =
                                { rerunState with
                                    LastResults = Some testResults
                                    LastRunId = Some completed.RunId
                                    PendingRerun = false
                                    PendingForceRunProjects = Set.empty }

                            match testConfigs with
                            | Some configs when not configs.IsEmpty ->
                                // A run just completed (LastResults set above), so the
                                // baseline exists — hasCachedResults = true. The
                                // deferred fanout force-runs any test project whose
                                // dependency fingerprint changed during the prior run.
                                match
                                    ctx.RunExclusive
                                        "tests"
                                        (runTestsWithImpact
                                            ctx
                                            configs
                                            (TestRunInputs.ofState rerunState)
                                            true
                                            deferredFanout)
                                with
                                | Claimed -> return rerunState
                                | SlotBusy ->
                                    // Another launch site won the slot; ITS
                                    // TestsFinished will drain this rerun — keep it
                                    // queued and the fanout un-consumed.
                                    return
                                        { rerunState with
                                            PendingRerun = true
                                            PendingForceRunProjects = deferredFanout }
                            | _ -> return rerunState
                    | [] ->
                        // Clear ONLY the committed symbols from the hot view; the
                        // durable queue (post-commit) is the source of truth and is
                        // mirrored into the cache-key snapshot so a non-empty queue
                        // keeps a cached green from replaying (see CacheKey below).
                        Volatile.Write(&changedSymbolsRef, remainingChangedSymbols)
                        recordRunOutcome testResults

                        return
                            { state with
                                LastResults = Some testResults
                                LastRunId = Some completed.RunId
                                ChangedFiles = []
                                ChangedSymbols = remainingChangedSymbols
                                AffectedTests = Analyzed [] }

                | Custom(RunTestsRequested(configs, filter, reply)) ->
                    // AUTOMATION-99: the `run-tests` force-run, launched from the
                    // mailbox so it is serialised with every other launch site and
                    // holds the `RunExclusive "tests"` slot for its whole duration —
                    // visible to `IsRunning`, to `AnyPluginBusy()`, and (via the
                    // framework's Running report at the claim) to `fshw status`.
                    match ctx.RunExclusive "tests" (commandForceRun configs filter reply) with
                    | Claimed -> return state
                    | SlotBusy ->
                        // FORCE semantics: a busy slot QUEUES the run, never refuses
                        // it — `test-rerun` is the explicit "prove it ran" verb, and
                        // a refusal that reads as success is the vacuous green this
                        // ticket exists to kill. TestsFinished drains the queue FIFO.
                        // The IPC command bounds its wait on `reply`, so queueing can
                        // never hang the caller.
                        ctx.Log "  ↳ queued run-tests force-run (tests already running)"

                        return
                            { state with
                                QueuedCommandRuns = state.QueuedCommandRuns @ [ (configs, filter, reply) ] }

                | _ -> return state
            }
      Commands = allCommands
      Subscriptions =
        Set.ofList (
            // FileChecked: per-file analysis fold into PendingAnalysis /
            // changedSymbolsRef (unchanged from the pre-BatchChecked design).
            // BatchChecked: cohort-complete flush signal — fires after the
            // last FileChecked of a batch, before any subsequent BuildCompleted
            // racing the same change. By the time the agent processes
            // BatchChecked, every FileChecked update has been folded in, so
            // changedSymbolsRef is consistent with state.ChangedSymbols and
            // any cache key derived from it is well-formed.
            // BuildCompleted is subscribed unconditionally so the
            // Item 3 freshness-stamp gate works even when the plugin is
            // configured analysis-only (no testConfigs). When testConfigs is
            // None / empty, the BuildCompleted handler still runs
            // `flushAndQueryAffected` (idempotent on empty PendingAnalysis)
            // and skips the test-run path — see the
            // `match testConfigs with | Some configs when not configs.IsEmpty`
            // branch in the handler.
            [ SubscribeFileChecked; SubscribeBatchChecked; SubscribeBuildCompleted ]
        )
      CacheKey =
        // §2a: pure-content cache key, built by the lifted `cacheKeyFor` so the
        // per-arm input dependencies are structural rather than a convention. The
        // three thunks are this closure's live state; `cacheKeyFor` decides which
        // arm forces which — and `FileChecked`, the per-file probe, forces none.
        let cacheKey (event: PluginEvent<TestPruneMsg>) : ContentHash option =
            let changedSymbolsHash () =
                Volatile.Read(&changedSymbolsRef)
                |> List.distinct
                |> List.sort
                |> String.concat "|"
                |> FsHotWatch.CheckCache.sha256Hex

            // §3d — the persisted needs-testing queue. `None` = empty, which both
            // omits the merkle entry (keeping the empty-queue green fast-path key
            // byte-stable) and, on BuildCompleted, is what makes the event cacheable
            // at all: a green that left symbols queued must re-run, never replay.
            let pendingQueueHash () =
                if Volatile.Read(&ledgerRecoveryOutstandingRef) then
                    // AUTOMATION-150. An unreadable ledger is outstanding debt whose
                    // membership is unknown. `None` here would assert "provably nothing
                    // owed" and make BuildCompleted cacheable — so a cached green,
                    // written by an earlier run over this very same content merkle, would
                    // REPLAY: handler skipped, drain skipped, no test process, green. The
                    // hole, re-opened through the cache door. A `Some` refuses cache
                    // participation outright (no replay, no write), exactly as a non-empty
                    // queue does. The value is a constant, not a hash: there is nothing to
                    // hash — that is the whole point.
                    Some "unreadable-ledger"
                elif Set.isEmpty pendingQueueRef then
                    None
                else
                    Some(PendingVerification.hash pendingQueueRef)

            // External-dependency salt: a content hash of the files matched by the
            // configured `dependsOn` globs. Editing a matched file (a DB migration
            // that changes the TEST database schema but no test SOURCE) changes this
            // hash → cache miss → genuine re-run. `None` when unconfigured or when
            // the globs match nothing, so the entry is omitted and existing on-disk
            // caches keep hitting.
            let dependsOnHash () =
                match externalDependencyHash repoRoot dependsOn with
                | "" -> None
                | h -> Some h

            // AUTOMATION-112: a full-suite run must never REPLAY an impact-filtered run's
            // cached verdict. Salting the key with the requested scope makes that
            // impossible rather than merely unlikely.
            let fullSuiteScopeHash () =
                if Volatile.Read(&fullSuiteScopeRef) then
                    Some "full"
                else
                    None

            // AUTOMATION-125: no cache participation while a red no covering run has
            // passed is outstanding.
            let hasOutstandingFailures () =
                not (List.isEmpty (Volatile.Read(&outstandingFailuresRef)))

            // AUTOMATION-161: no cache participation on BuildCompleted until a run in
            // THIS process has covered something. A cached result is an assertion about
            // tests, and a process may not make one it has no record of earning.
            //
            // ANALYSIS-ONLY IS EXEMPT, and the exemption is the rule seen from the other
            // side. With no runnable test projects this plugin makes no test CLAIM at all
            // — its terminal status is a symbol-analysis summary, and `runnableProjects`
            // is empty precisely because "that mode produces no test verdict". There is
            // no verdict to launder, so there is nothing to fail closed about; gating it
            // would delete a working cache to guard against an assertion it never makes.
            let sessionHasTestEvidence () =
                Set.isEmpty runnableProjects
                || not (Set.isEmpty (RunCoverage.coveredProjects (Volatile.Read(&sessionCoverageRef))))

            cacheKeyFor
                changedSymbolsHash
                pendingQueueHash
                dependsOnHash
                fullSuiteScopeHash
                hasOutstandingFailures
                sessionHasTestEvidence
                event

        Some cacheKey
      Teardown = None }
