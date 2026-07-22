/// Core event and status types for the FsHotWatch daemon.
module FsHotWatch.Events

open System.IO
open FSharp.Compiler.CodeAnalysis

/// Absolute file path — normalized at construction time via Path.GetFullPath.
[<Struct>]
type AbsFilePath = private AbsFilePath of string

module AbsFilePath =
    let create (path: string) = AbsFilePath(Path.GetFullPath(path))
    let value (AbsFilePath p) = p

/// Absolute project path (.fsproj) — normalized at construction time via Path.GetFullPath.
[<Struct>]
type AbsProjectPath = private AbsProjectPath of string

module AbsProjectPath =
    let create (path: string) = AbsProjectPath(Path.GetFullPath(path))
    let value (AbsProjectPath p) = p

/// Opaque content hash — wraps raw hash strings to prevent mixing with other strings.
[<Struct>]
type ContentHash = private ContentHash of string

module ContentHash =
    let create (hash: string) = ContentHash hash
    let value (ContentHash h) = h

/// Identifies a check result in the cache
type CacheKey =
    {
        /// Content hash of the file being checked (from file size + mtime).
        FileHash: ContentHash
        /// Hash of project options (dependencies, compiler flags)
        ProjectOptionsHash: ContentHash
    }

/// Describes what kind of file change was detected by the watcher.
type FileChangeKind =
    /// F# source files (.fs, .fsx) changed.
    | SourceChanged of files: string list
    /// Project files (.fsproj, .props, project.assets.json) changed.
    | ProjectChanged of files: string list
    /// Solution file (.sln, .slnx) changed.
    | SolutionChanged

/// Result of a build operation.
type BuildResult =
    | BuildSucceeded
    | BuildFailed of errors: string list

/// Whether a file was fully type-checked or only parsed (check aborted).
[<NoComparison>]
type FileCheckState =
    | FullCheck of FSharpCheckFileResults
    | ParseOnly

/// Result of type-checking a single file with the warm FSharpChecker.
[<NoComparison>]
type FileCheckResult =
    {
        /// Absolute path to the checked file.
        File: AbsFilePath
        /// Source text of the file at check time.
        Source: string
        /// FCS parse results (AST).
        ParseResults: FSharpParseFileResults
        /// FCS type-check results. ParseOnly if check was aborted.
        CheckResults: FileCheckState
        /// FSharpProjectOptions used when checking this file.
        ProjectOptions: FSharpProjectOptions
        /// Monotonic version counter — higher means newer.
        Version: int64
    }

/// Result of checking all files in a project.
[<NoComparison>]
type ProjectCheckResult =
    {
        /// Project file path.
        Project: string
        /// Per-file check results keyed by absolute file path.
        FileResults: Map<string, FileCheckResult>
    }

/// The evidence EVERY terminal status carries: WHAT the run did and how long
/// it took. A guard that cannot say what it measured has not measured anything
/// — so "done, with nothing to report" is unrepresentable by construction: the
/// representation is PRIVATE, and the only way to obtain a value is
/// `RunVerdict.create`, which rejects an empty summary, so no site (daemon, cache
/// deserializer, test helper, or example) can build a hollow content-free `✓`.
[<NoComparison>]
type RunVerdict =
    private
        { summary: string
          elapsed: System.TimeSpan }

    /// Human-readable statement of what the run did — e.g.
    /// "6 passed, 0 failed in 6 projects". Rendered by `fshw status`/`check`
    /// and recorded as the run's history summary. Non-empty by construction.
    member this.Summary = this.summary

    /// The plugin's own measurement of the run's duration. Drives the run
    /// record's elapsed (the host derives startedAt from `at - Elapsed`), so a
    /// terminal that never went through `Running` still renders honest timing.
    /// `TimeSpan.Zero` is the conventional "no measurable work ran" value.
    member this.Elapsed = this.elapsed

module RunVerdict =
    /// The ONLY constructor. Throws on a null/empty/whitespace summary: a
    /// verdict that says nothing is not a verdict.
    let create (summary: string) (elapsed: System.TimeSpan) : RunVerdict =
        if System.String.IsNullOrWhiteSpace summary then
            invalidArg
                (nameof summary)
                "a RunVerdict summary must state what the run did — empty/whitespace is the content-free ✓ AUTOMATION-99 exists to kill"

        { summary = summary; elapsed = elapsed }

/// Current status of a plugin or preprocessor.
[<NoComparison>]
type PluginStatus =
    /// Plugin is registered but hasn't processed any events yet.
    | Idle
    /// Plugin is currently processing.
    | Running of since: System.DateTime
    /// Plugin finished processing successfully, carrying the verdict it earned.
    | Completed of at: System.DateTime * verdict: RunVerdict
    /// Plugin encountered an error. `error` is the diagnosis; the verdict still
    /// carries the run's one-line summary and measured duration, so a failure
    /// can never record a fabricated zero-length run.
    | Failed of error: string * at: System.DateTime * verdict: RunVerdict

module PluginStatus =
    /// Completed at the current UTC instant, carrying the verdict.
    let completedNow (summary: string) (elapsed: System.TimeSpan) : PluginStatus =
        Completed(System.DateTime.UtcNow, RunVerdict.create summary elapsed)

    /// Failed at the current UTC instant. `error` is the full diagnosis;
    /// `summary` is the one-line human verdict recorded in run history.
    let failedNow (error: string) (summary: string) (elapsed: System.TimeSpan) : PluginStatus =
        Failed(error, System.DateTime.UtcNow, RunVerdict.create summary elapsed)

    let inline isTerminal status =
        match status with
        | Idle
        | Running _ -> false
        | Completed _
        | Failed _ -> true

    // Idle counts as quiescent for status-aggregation callers that query after
    // WaitForScan: Idle there means "not triggered by this scan", not "pending".
    let inline isQuiescent status =
        match status with
        | Running _ -> false
        | Idle
        | Completed _
        | Failed _ -> true

/// A named, timestamped unit of concurrent work within a plugin run.
type Subtask =
    { Key: string
      Label: string
      StartedAt: System.DateTime }

/// Outcome of a completed plugin run.
type RunOutcome =
    | CompletedRun
    | FailedRun of error: string
    | TimedOut of reason: string

/// Record of a single completed or failed plugin run.
type RunRecord =
    { StartedAt: System.DateTime
      Elapsed: System.TimeSpan
      Outcome: RunOutcome
      Summary: string option
      ActivityTail: string list }


/// Result of a single test project execution. The `wasFiltered` flag indicates
/// whether the run was reduced by impact analysis (true) or covered the full
/// project suite (false). Downstream coverage merging uses this to decide
/// baseline vs partial output paths.
/// `elapsed` is the wall-clock time the runner ran for. Captured even on
/// failure/timeout so adaptive bounds (e.g. timeout = 2 × last-success) and
/// timing display can use it. `TimeSpan.Zero` is the conventional "no data"
/// value (e.g. for cached results from prior versions that didn't carry it).
type TestResult =
    | TestsPassed of output: string * wasFiltered: bool * elapsed: System.TimeSpan
    | TestsFailed of output: string * wasFiltered: bool * elapsed: System.TimeSpan
    /// The runner exceeded its configured `timeoutSec` and was killed. Distinct
    /// from `TestsFailed` so consumers can react to "stuck" runs (e.g. flag the
    /// whole run TimedOut) without grepping the output for a magic prefix.
    | TestsTimedOut of output: string * after: System.TimeSpan * wasFiltered: bool * elapsed: System.TimeSpan
    /// The project's tests NEVER RAN because its apphost wasn't produced yet (a
    /// build-ordering race: `dotnet run --no-build` fired before the build
    /// settled). Distinct so it can NEVER masquerade as a pass. `isPassed` is
    /// FALSE for it: a project that didn't run cannot count toward a green
    /// verdict (a CI gate
    /// must not report "safe to merge" when nothing was verified). It is ALSO
    /// not a real test failure, so the verdict surfaces it as an honest
    /// "waiting on build — tests did not run" diagnostic, not "test failed".
    /// `reason` documents why (e.g. "apphost not produced"). Carries no
    /// elapsed/wasFiltered — nothing executed — so it never lowers a coverage
    /// baseline.
    | TestsDeferred of reason: string
    /// The runner STARTED but aborted before producing a usable result: a
    /// non-zero exit with NO parseable report (the test host crashed or was
    /// killed during shutdown — e.g. the Microsoft.Testing.Platform exit-7
    /// shutdown flake — before flushing its CTRF report). Distinct from every
    /// other case so it can NEVER be surfaced as a test failure (no test was
    /// shown to fail) NOR as a pass (nothing was verified). `isPassed` is FALSE:
    /// a run that produced no evidence must never count toward a green gate.
    /// Surfaced as an honest "errored — re-run" diagnostic, NOT "tests failed".
    /// Like `TestsDeferred`, carries no elapsed/wasFiltered (nothing usable ran)
    /// so it never lowers a coverage baseline, and it is UNCACHEABLE by
    /// construction (`isPassed`=false → the cacheKey gate skips the write), so a
    /// transient abort is never replayed as a stale verdict. `reason` documents
    /// what aborted (exit code + "no report written").
    | TestsErrored of reason: string

module TestResult =
    let output =
        function
        | TestsPassed(o, _, _)
        | TestsFailed(o, _, _)
        | TestsTimedOut(o, _, _, _) -> o
        | TestsDeferred reason
        | TestsErrored reason -> reason

    let wasFiltered =
        function
        | TestsPassed(_, w, _)
        | TestsFailed(_, w, _)
        | TestsTimedOut(_, _, w, _) -> w
        // A deferred or errored project produced no usable run; treat it as
        // filtered so `ranFullSuite` can't class the run as a full suite that
        // would lower a coverage baseline.
        | TestsDeferred _
        | TestsErrored _ -> true

    let elapsed =
        function
        | TestsPassed(_, _, e)
        | TestsFailed(_, _, e)
        | TestsTimedOut(_, _, _, e) -> e
        // Nothing usable ran, so there's no wall-clock duration to report.
        | TestsDeferred _
        | TestsErrored _ -> System.TimeSpan.Zero

    let isPassed =
        function
        | TestsPassed _ -> true
        | TestsFailed _
        | TestsTimedOut _
        // A project that never ran (Deferred) or aborted before producing
        // evidence (Errored) must NEVER count as passed — otherwise a
        // build-ordering race or a host crash produces a silent false-green
        // CI verdict.
        | TestsDeferred _
        | TestsErrored _ -> false

    let isTimedOut =
        function
        | TestsTimedOut _ -> true
        | _ -> false

    /// True for the `TestsDeferred` case: the project's tests didn't run
    /// (apphost not produced). Distinct from `isPassed`/a real failure so the
    /// verdict can surface an honest "waiting on build" diagnostic.
    let isDeferred =
        function
        | TestsDeferred _ -> true
        | _ -> false

    /// True for the `TestsErrored` case: the runner aborted before producing a
    /// usable result (non-zero exit + no parseable report). Distinct from a real
    /// failure (no test was shown to fail) and from a pass (nothing verified) so
    /// the verdict surfaces an honest "errored — re-run" diagnostic.
    let isErrored =
        function
        | TestsErrored _ -> true
        | _ -> false

    /// Derive run-level `RanFullSuite` from a per-project Results map: true iff
    /// no project was run with an impact filter (i.e., the entire test suite
    /// ran). Empty map is full-suite by convention (nothing was filtered).
    let ranFullSuite (results: Map<string, TestResult>) : bool =
        results |> Map.forall (fun _ r -> not (wasFiltered r))

/// Aggregate test results snapshot. Used as a plain value type by TestPrune's
/// internals and afterRun hooks — NOT dispatched as an event. Subscribers
/// consume `TestRunCompleted` (which wraps the final Results plus Outcome).
type TestResults =
    { Results: Map<string, TestResult>
      Elapsed: System.TimeSpan }

/// Outcome of a complete test run.
type TestRunOutcome =
    /// Run executed to natural completion (inspect Results for per-project pass/fail).
    | Normal
    /// Run was cut short (cancelled, timed out, crashed). Results may be incomplete.
    | Aborted of reason: string

/// Emitted once at the start of every test run. Gives subscribers a clear
/// lifecycle boundary to reset run-scoped state (e.g. idempotency sentinels).
type TestRunStarted =
    { RunId: System.Guid
      StartedAt: System.DateTime }

/// Emitted each time a group of tests completes within a run. Pure delta —
/// carries only projects whose execution just finished. Subscribers that need
/// cumulative run-wide state fold deltas locally, keyed by RunId.
type TestProgress =
    { RunId: System.Guid
      NewResults: Map<string, TestResult> }

/// Emitted once at the end of every run (including aborts). Canonical summary
/// for subscribers that don't want to listen to TestProgress. Also the only
/// event emitted on cache-replay — cached runs skip the per-group progress
/// stream and go straight from TestRunStarted to TestRunCompleted.
type TestRunCompleted =
    {
        RunId: System.Guid
        TotalElapsed: System.TimeSpan
        Outcome: TestRunOutcome
        /// Final cumulative state. Equivalent to the fold of every TestProgress
        /// for this RunId; materialized here so late subscribers can skip
        /// progress events entirely.
        Results: Map<string, TestResult>
        /// True iff every project in this run executed without an impact filter
        /// (i.e., the entire test suite ran). False if at least one project was
        /// filtered to a subset. Consumers gate baseline refreshes/threshold
        /// tightening on this — partial runs should not lower a coverage
        /// baseline or tighten a ratchet.
        RanFullSuite: bool
    }

/// Current state of the daemon's scan operation.
type ScanState =
    /// No scan in progress or completed.
    | ScanIdle
    /// Scan is running.
    | Scanning of total: int * completed: int * startedAt: System.DateTime
    /// Scan completed and took `elapsed` wall-clock time. This is only a marker
    /// that a scan finished; completeness (registered vs. currently-checked) is
    /// always computed LIVE from the host's coverage set at read time, never
    /// frozen into this snapshot — so an incremental edit + re-check after a scan
    /// keeps `status`/`check` in agreement instead of rotting a stale count.
    | ScanComplete of elapsed: System.TimeSpan

/// Outcome of a command execution.
type CommandOutcome =
    | CommandSucceeded of output: string
    | CommandFailed of output: string

/// Result of a command execution (e.g., file command plugin completing a shell command).
type CommandCompletedResult =
    { Name: string
      Outcome: CommandOutcome }

/// What triggered a `BatchChecked` event — the boot scan or an in-session
/// debounce-batch from the watcher. Subscribers that need to distinguish
/// (e.g. for warm-up logic specific to boot scan) match on this; most just
/// treat both uniformly as "the cohort is done; flush and decide."
type BatchCheckedTrigger =
    /// The boot-scan cohort over every registered file.
    | BootScan
    /// A debounce-batch cohort from `processBatch` — typically a small set
    /// after a save. `originating` is the union of FileChangeKind values the
    /// watcher reported in this debounce window.
    | InSessionBatch of originating: FileChangeKind list

/// Emitted once after a defined cohort of `FileChecked` events has finished —
/// strictly *after* the last `FileChecked` for the cohort, and before any
/// subsequent event that depends on the cohort being complete (e.g. a
/// `BuildCompleted` derived from the same change). Subscribers consume this
/// instead of bookkeeping per-`FileChecked` state to know when a batch is done.
type BatchChecked =
    {
        Trigger: BatchCheckedTrigger
        /// Files actually dispatched into `pipeline.CheckFile` for this batch.
        /// May be smaller than the project graph (in-session batch) or equal to
        /// it (boot scan).
        Files: AbsFilePath list
        /// Monotonic generation counter — same as `Daemon.GetScanGeneration` for
        /// `BootScan`-triggered events; bumped per `InSessionBatch` as well so
        /// subscribers can identify "the latest cohort."
        Generation: int64
        /// Wall-clock start of the cohort (first `CheckFile` dispatched).
        StartedAt: System.DateTime
        /// Wall-clock end (last `FileChecked` emitted before this `BatchChecked`).
        CompletedAt: System.DateTime
    }

/// Events routed to plugins by the framework.
[<NoComparison; NoEquality>]
type PluginEvent<'Msg> =
    | FileChanged of FileChangeKind
    | FileChecked of FileCheckResult
    | BatchChecked of BatchChecked
    | BuildCompleted of BuildResult
    | TestRunStarted of TestRunStarted
    | TestProgress of TestProgress
    | TestRunCompleted of TestRunCompleted
    | CommandCompleted of CommandCompletedResult
    | Custom of 'Msg
