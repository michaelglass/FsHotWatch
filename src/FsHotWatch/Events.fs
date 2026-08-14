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

/// The evidence every terminal status carries: what the run did and how long it
/// took. The representation is private and `RunVerdict.create` is the only
/// constructor; it rejects an empty summary, so no call site can build a
/// content-free `✓`.
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
    /// The ONLY constructor. Throws on a null/empty/whitespace summary.
    let create (summary: string) (elapsed: System.TimeSpan) : RunVerdict =
        if System.String.IsNullOrWhiteSpace summary then
            invalidArg
                (nameof summary)
                "a RunVerdict summary must state what the run did — empty/whitespace is the content-free ✓ AUTOMATION-99 exists to kill"

        { summary = summary; elapsed = elapsed }

/// The vocabulary a TERMINAL status uses to say "this run VERIFIED NOTHING".
///
/// A run can finish without failing and still prove nothing — no test project was
/// selected, so no test binary was invoked. The STATUS for that is `Completed`:
/// nothing FAILED, and reporting `Failed` would both claim a failure that did not
/// happen and turn `check`'s honest exit 3 ("NO VERDICT — the tests that ran were no
/// tests ran") into an exit 1 ("failures found"). But no surface may render it as a
/// bare `✓` either: a reader scanning plugin glyphs would see success for a run that
/// executed nothing (AUTOMATION-198).
///
/// The renderer's only channel to a plugin's terminal is the run summary, so the
/// marker is WRITTEN here by one constructor and READ here by one predicate — the
/// same single-writer/single-reader discipline as `RunVerification.token`/`tryParse`.
/// A producer that hand-wrote the sentence and a consumer that hand-matched it are
/// how the two ends drift, and the drift fails OPEN (back to a ✓).
module RunSummary =
    /// Leads the summary, so the fact survives truncation and so the reader meets it
    /// before any count. Upper-case because it is the headline, not a footnote.
    [<Literal>]
    let private NothingVerifiedPrefix = "NOTHING VERIFIED: "

    /// The summary for a run that proved nothing. `detail` states what it did instead
    /// — e.g. "0 test project(s) ran, no test executed".
    let nothingVerified (detail: string) : string = NothingVerifiedPrefix + detail

    /// Does this summary state that the run verified nothing?
    ///
    /// `StartsWith`, not equality: the cache-replay path APPENDS " (cached)" to the
    /// summary it replays (`PluginFramework.markCached`), and a replayed nothing-run
    /// verified exactly as much as the original did.
    let saysNothingVerified (summary: string) : bool =
        summary.StartsWith(NothingVerifiedPrefix, System.StringComparison.Ordinal)

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
    /// settled). `isPassed` is FALSE — a project that didn't run cannot count
    /// toward a green verdict — but it is not a real test failure either, so the
    /// verdict surfaces it as "waiting on build — tests did not run". `reason`
    /// documents why (e.g. "apphost not produced"). Carries no
    /// elapsed/wasFiltered — nothing executed — so it never lowers a coverage
    /// baseline.
    | TestsDeferred of reason: string
    /// The runner STARTED but aborted before producing a usable result: a
    /// non-zero exit with NO parseable report (the test host crashed or was
    /// killed during shutdown — e.g. the Microsoft.Testing.Platform exit-7
    /// shutdown flake — before flushing its CTRF report). `isPassed` is FALSE
    /// (nothing was verified) but no test was shown to fail either, so it
    /// surfaces as "errored — re-run". Carries no elapsed/wasFiltered so it never
    /// lowers a coverage baseline, and it is UNCACHEABLE by construction
    /// (`isPassed`=false → the cacheKey gate skips the write), so a transient
    /// abort is never replayed as a stale verdict. `reason` documents what
    /// aborted (exit code + "no report written").
    | TestsErrored of reason: string
    /// The runner RAN, discovered the project's tests, and the active filter matched
    /// NONE of them — Microsoft.Testing.Platform's exit 8 / "Zero tests ran". Nothing
    /// executed, so nothing was verified.
    ///
    /// A case rather than a magic output-string prefix, so folds tell it apart
    /// structurally rather than by string comparison (AUTOMATION-272).
    ///
    /// `isPassed` is TRUE for it, deliberately: per PROJECT, a filter matching nothing
    /// is not that project's failure — an impact selection naming no class in the
    /// Integration project must leave it passing, or every filtered run goes red. The
    /// RUN-level question ("did anything get verified?") belongs to `RunVerification`.
    | TestsNoMatch of output: string * elapsed: System.TimeSpan

module TestResult =
    /// The magic output prefix a zero-match result used to be encoded with, before
    /// `TestsNoMatch` existed.
    ///
    /// Retained ONLY because entries already written to `.fshw`'s task cache carry it,
    /// stored as `"passed"`: reading one back as a plain `TestsPassed` would replay a
    /// run that executed nothing as a genuine green. `FileTaskCache` reconstructs the
    /// case from this prefix on read. Nothing constructs it any more — delete once no
    /// cache in the wild can predate the change.
    [<Literal>]
    let LegacyZeroMatchMarker = "[fshw:no-tests-matched] "

    let output =
        function
        | TestsPassed(o, _, _)
        | TestsFailed(o, _, _)
        | TestsTimedOut(o, _, _, _) -> o
        | TestsNoMatch(o, _) -> o
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
        // A zero match arises ONLY under a filter, and reporting it as filtered is
        // the safe direction: `ranFullSuite` must never class a run that executed
        // nothing as a full suite entitled to overwrite a coverage baseline.
        | TestsNoMatch _ -> true

    let elapsed =
        function
        | TestsPassed(_, _, e)
        | TestsFailed(_, _, e)
        | TestsTimedOut(_, _, _, e) -> e
        // The runner really did start and discover this project's tests, so unlike
        // the two cases below there IS a genuine wall-clock duration to report.
        | TestsNoMatch(_, e) -> e
        // Nothing usable ran, so there's no wall-clock duration to report.
        | TestsDeferred _
        | TestsErrored _ -> System.TimeSpan.Zero

    let isPassed =
        function
        | TestsPassed _ -> true
        // TRUE, deliberately. `isPassed` answers a PER-PROJECT question, and per
        // project a filter that matched nothing is not that project's failure: an
        // impact selection naming no class in the Integration project must leave it
        // passing, or every filtered run goes red.
        //
        // A RUN must not sum these into a green — that question is `RunVerification`,
        // and it is the aggregators' job to ask it. If you are writing a new fold over
        // results, `isPassed` is not the predicate you want for a run-level verdict.
        | TestsNoMatch _ -> true
        | TestsFailed _
        | TestsTimedOut _
        // A project that never ran (Deferred) or aborted before producing
        // evidence (Errored) must NEVER count as passed — otherwise a
        // build-ordering race or a host crash produces a silent false-green
        // CI verdict.
        | TestsDeferred _
        | TestsErrored _ -> false

    /// True for the `TestsNoMatch` case: the project's runner ran and its filter
    /// matched no test, so this project verified nothing. The predicate a RUN-level
    /// fold wants, and the one `isPassed` deliberately cannot be.
    let isNoMatch =
        function
        | TestsNoMatch _ -> true
        | TestsPassed _
        | TestsFailed _
        | TestsTimedOut _
        | TestsDeferred _
        | TestsErrored _ -> false

    /// Did this project actually EXECUTE at least one test?
    ///
    /// The per-project half of "was anything verified". Distinct from `isPassed` in
    /// both directions: a zero-match project PASSES but executed nothing, and a
    /// failing project executed plenty.
    ///
    /// Deliberately exhaustive, with NO wildcard: a wildcard would silently count the
    /// next `TestResult` case as having run, where writing every case out makes it a
    /// compile error here instead.
    let executedTests =
        function
        | TestsPassed _
        | TestsFailed _
        | TestsTimedOut _ -> true
        // Nothing ran: no match under the filter, apphost never built, host aborted
        // before producing a verdict.
        | TestsNoMatch _
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

    /// Did this run EXECUTE anything? The run-level half of `executedTests`.
    /// `RunVerification.ofResults` is the answer callers should reach for; this is
    /// its building block, and is here for the rare caller that only needs the yes/no.
    let executedAnything (results: Map<string, TestResult>) : bool =
        results |> Map.exists (fun _ r -> executedTests r)

    /// True iff no project was reduced by impact analysis. Says NOTHING about
    /// whether anything ran, which is why it is not the scope answer on its own —
    /// `Map.forall` is vacuously true for an empty map. `RunVerification.ofResults`
    /// is the total answer; this is the conjunct it uses once everything else has
    /// been ruled out.
    let noneFiltered (results: Map<string, TestResult>) : bool =
        results |> Map.forall (fun _ r -> not (wasFiltered r))

/// Aggregate test results snapshot. Used as a plain value type by TestPrune's
/// internals and afterRun hooks — NOT dispatched as an event. Subscribers
/// consume `TestRunCompleted` (which wraps the final Results plus Outcome).
type TestResults =
    { Results: Map<string, TestResult>
      Elapsed: System.TimeSpan }

/// How much of the suite a run that DID execute covered.
///
/// Only reachable from `RunVerification.Ran`, which is the point: asking about scope
/// requires first establishing that something ran. A free-standing `RanFullSuite:
/// bool` had no honest value for a run that executed nothing.
type RunScope =
    /// At least one project was reduced by impact analysis. Un-run files cannot be
    /// told apart from genuine zeros, so a shortfall must not gate on this.
    | Partial
    /// Every project executed, none impact-filtered — the entire suite ran. The only
    /// state that may lower a coverage baseline or tighten a ratchet.
    | FullSuite

/// What a run actually VERIFIED — the run-level answer to "did this prove anything?".
///
/// Lives here, in core, rather than inside TestPrune, because BOTH ends of the wire
/// need it: TestPrune produces the token and the CLI consumes it. With the type on
/// only one side the other hand-wrote the string literals, so a rename on the producer
/// was silent on the consumer, whose `else` branch mapped every unrecognized token to
/// `Tests passed`, exit 0. The token is parsed, never compared.
type RunVerification =
    /// No project was selected — nothing was invoked.
    | NoProjectsSelected
    /// Projects ran; every one matched zero tests under the active filter.
    | AllZeroMatch of projectCount: int
    /// Projects reported, but not one executed a test — every result was deferred,
    /// errored, or a zero match mixed among them.
    | NothingExecuted
    /// At least one project executed at least one test. Carries how much of the
    /// suite it covered — the only place scope is representable.
    | Ran of scope: RunScope

[<RequireQualifiedAccess>]
module RunVerification =

    /// Stable wire token. The ONLY place these strings are written.
    ///
    /// `Ran` serialises per scope rather than as a bare "ran" plus a second field,
    /// so a reader cannot obtain a scope without having read that the run ran.
    let token (c: RunVerification) : string =
        match c with
        | NoProjectsSelected -> "no-projects-selected"
        | AllZeroMatch _ -> "all-zero-match"
        | NothingExecuted -> "nothing-executed"
        | Ran Partial -> "ran-partial"
        | Ran FullSuite -> "ran-full-suite"

    /// Read a token off the wire. `None` means "this build cannot interpret it" —
    /// which a caller must treat as NO VERDICT, never as a pass. Deliberately not a
    /// total function with a default: a default here is precisely how an unknown
    /// reading becomes a green one.
    ///
    /// The bare "ran" of the older wire format is deliberately NOT accepted: it
    /// asserted that tests executed while saying nothing about scope. Refusing it
    /// costs one no-verdict on a daemon/CLI version skew, which fails closed.
    let tryParse (s: string) : RunVerification option =
        match s with
        | "no-projects-selected" -> Some NoProjectsSelected
        // The count is carried separately on the wire; the parsed case exists to say
        // WHICH shape this is, and callers that need the number read it alongside.
        | "all-zero-match" -> Some(AllZeroMatch 0)
        | "nothing-executed" -> Some NothingExecuted
        | "ran-partial" -> Some(Ran Partial)
        | "ran-full-suite" -> Some(Ran FullSuite)
        | _ -> None

    /// True when the run verified NOTHING — the property every gate cares about.
    let verifiedNothing (c: RunVerification) : bool =
        match c with
        | NoProjectsSelected
        | AllZeroMatch _
        | NothingExecuted -> true
        | Ran _ -> false

    /// The scope a run established, if it established one. `None` is not "partial":
    /// it is "this run proved nothing about breadth", and a caller that needs to act
    /// must decide what to do with that rather than receive a fabricated boolean.
    let scope (c: RunVerification) : RunScope option =
        match c with
        | Ran s -> Some s
        | NoProjectsSelected
        | AllZeroMatch _
        | NothingExecuted -> None

    /// True only for a run that executed AND covered the whole suite. The single
    /// question the coverage ratchet and the baseline refresh are asking.
    let ranFullSuite (c: RunVerification) : bool = scope c = Some FullSuite

    /// The verification a per-project result map establishes. TOTAL, and the ONLY
    /// derivation in the tree — TestPrune, the plugins and the cache all route here,
    /// so there is one place where these five cases are told apart.
    ///
    /// Order matters and is the honest one: emptiness first (nothing was invoked),
    /// then all-zero-match (projects ran and discovered tests, the filter matched
    /// none — diagnosable, so it keeps its count), then nothing-executed (they
    /// reported but not one ran a test), and only then may scope be asked at all.
    let ofResults (results: Map<string, TestResult>) : RunVerification =
        if results.IsEmpty then
            NoProjectsSelected
        elif results |> Map.forall (fun _ r -> TestResult.isNoMatch r) then
            AllZeroMatch results.Count
        elif not (TestResult.executedAnything results) then
            NothingExecuted
        elif TestResult.noneFiltered results then
            Ran FullSuite
        else
            Ran Partial

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
        /// What this run VERIFIED, and — only if it verified something — how much of
        /// the suite it covered.
        ///
        /// Gate baseline refreshes and ratchet tightening on `Ran FullSuite` — via
        /// `RunVerification.ranFullSuite` — never on "not Partial".
        Verification: RunVerification
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
