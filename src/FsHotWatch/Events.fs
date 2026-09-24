/// Core event and status types for the FsHotWatch daemon.
module FsHotWatch.Events

open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis

/// The process working directory was deleted while the process still stood in it, so a
/// relative path has nothing to resolve against. Names the directory the process was last
/// seen in — the cause — rather than surfacing as a missing file at every call site.
type WorkingDirectoryMissingException(lastKnownDirectory: string option, inner: exn) =
    inherit
        DirectoryNotFoundException(
            (match lastKnownDirectory with
             | Some dir -> $"The process working directory '{dir}' no longer exists"
             | None -> "The process working directory no longer exists (it was never read successfully)")
            + "; relative paths cannot be resolved until the process is restarted in a directory that exists.",
            inner
        )

    /// The working directory as last read successfully by this process, if it ever was.
    member _.LastKnownDirectory = lastKnownDirectory

/// Resolves paths to absolute form without letting a vanished working directory hide.
module private AbsolutePath =
    let mutable private lastKnownWorkingDirectory: string option = None

    let private workingDirectory () =
        try
            let dir = Directory.GetCurrentDirectory()
            Volatile.Write(&lastKnownWorkingDirectory, Some dir)
            dir
        with :? FileNotFoundException as ex ->
            raise (WorkingDirectoryMissingException(Volatile.Read(&lastKnownWorkingDirectory), ex))

    /// A fully qualified path is only normalized and never consults the working directory;
    /// a relative one resolves against it.
    let resolve (path: string) =
        if Path.IsPathFullyQualified path then
            Path.GetFullPath path
        else
            Path.GetFullPath(path, workingDirectory ())

/// Absolute file path — normalized at construction time via Path.GetFullPath.
/// Raises WorkingDirectoryMissingException for a relative path once the working directory is gone.
[<Struct>]
type AbsFilePath = private AbsFilePath of string

module AbsFilePath =
    let create (path: string) = AbsFilePath(AbsolutePath.resolve path)
    let value (AbsFilePath p) = p

/// Absolute project path (.fsproj) — normalized at construction time via Path.GetFullPath.
/// Raises WorkingDirectoryMissingException for a relative path once the working directory is gone.
[<Struct>]
type AbsProjectPath = private AbsProjectPath of string

module AbsProjectPath =
    let create (path: string) =
        AbsProjectPath(AbsolutePath.resolve path)

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
        /// The completed project-model generation this result was published against.
        /// `None` when no daemon discovery captured it (a bare pipeline check).
        ModelGeneration: int64 option
        /// The frame the file was checked under, when it was checked at a virtual root:
        /// paths inside `ParseResults` and `CheckResults` are then virtual, and this maps
        /// them to the worktree. `None`: they are the worktree's own.
        Frame: PathFrame.PathFrame option
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

/// The words a run that VERIFIED NOTHING is shown with. Pure DISPLAY: nothing reads
/// this text back to decide anything — the fact itself is `RunVerdict.NothingVerified`
/// on the verdict and `RunOutcome.VerifiedNothing` on the run record.
/// Before that it was a marked prefix on the summary that three surfaces parsed; a
/// case is what the compiler enforces at every fold, a prefix is what drifts.
module RunSummary =
    /// Leads the summary, so the fact survives truncation and so the reader meets it
    /// before any count. Upper-case because it is the headline, not a footnote.
    [<Literal>]
    let private NothingVerifiedPrefix = "NOTHING VERIFIED: "

    /// The summary line for a run that proved nothing. `detail` states what it did
    /// instead — e.g. "0 test project(s) ran, no test executed".
    let nothingVerified (detail: string) : string = NothingVerifiedPrefix + detail

/// The evidence every terminal status carries: what the run did and how long it
/// took. The representation is private and the `RunVerdict` module holds the only
/// constructors; both reject an empty summary, so no call site can build a
/// content-free `✓`.
///
/// A run can finish without failing and still prove nothing — no test project was
/// selected, so no test binary was invoked. The STATUS for that is `Completed`:
/// nothing FAILED, and reporting `Failed` would both claim a failure that did not
/// happen and turn `check`'s honest exit 3 ("NO VERDICT — NO TESTS RAN — nothing was
/// verified") into an exit 1 ("failures found"). But no surface may render it as a
/// bare `✓` either: a reader scanning plugin glyphs would see success for a run that
/// executed nothing. `NothingVerified` is that fact, as a value: the
/// host records it on the run as `RunOutcome.VerifiedNothing`, and every renderer
/// keys its glyph off the case rather than off the summary's words.
[<NoComparison>]
type RunVerdict =
    private
        { summary: string
          elapsed: System.TimeSpan
          nothingVerified: string option }

    /// Human-readable statement of what the run did — e.g.
    /// "6 passed, 0 failed in 6 projects". Rendered by `fshw status`/`check`
    /// and recorded as the run's history summary. Non-empty by construction.
    member this.Summary = this.summary

    /// The plugin's own measurement of the run's duration. Drives the run
    /// record's elapsed (the host derives startedAt from `at - Elapsed`), so a
    /// terminal that never went through `Running` still renders honest timing.
    /// `TimeSpan.Zero` is the conventional "no measurable work ran" value.
    member this.Elapsed = this.elapsed

    /// `Some detail` when the run EXECUTED NOTHING — the detail says what it did
    /// instead. `None` is a run whose summary is evidence of what it verified.
    member this.NothingVerified = this.nothingVerified

module RunVerdict =
    let private requireSummary (summary: string) =
        if System.String.IsNullOrWhiteSpace summary then
            invalidArg
                (nameof summary)
                "a RunVerdict summary must state what the run did — empty/whitespace is the content-free ✓ this type exists to kill"

    /// The verdict of a run that verified what its summary says. Throws on a
    /// null/empty/whitespace summary.
    let create (summary: string) (elapsed: System.TimeSpan) : RunVerdict =
        requireSummary summary

        { summary = summary
          elapsed = elapsed
          nothingVerified = None }

    /// The verdict of a run that VERIFIED NOTHING: no file compared, no test run, no
    /// project selected. `detail` states what it did instead and must be non-empty;
    /// the summary is `RunSummary.nothingVerified detail`, so the one sentence every
    /// surface shows is built here and nowhere else.
    let verifiedNothing (detail: string) (elapsed: System.TimeSpan) : RunVerdict =
        if System.String.IsNullOrWhiteSpace detail then
            invalidArg
                (nameof detail)
                "a verified-nothing verdict must say what the run did instead — an unexplained absence of evidence is not a verdict"

        { summary = RunSummary.nothingVerified detail
          elapsed = elapsed
          nothingVerified = Some detail }

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

    /// Completed at the current UTC instant, carrying a verdict that says the run
    /// VERIFIED NOTHING (`RunVerdict.verifiedNothing`). `Completed`, not `Failed`:
    /// nothing broke, and a failure would turn an honest "no verdict" into "failures
    /// found". The run record it produces carries `RunOutcome.VerifiedNothing`.
    let verifiedNothingNow (detail: string) (elapsed: System.TimeSpan) : PluginStatus =
        Completed(System.DateTime.UtcNow, RunVerdict.verifiedNothing detail elapsed)

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
    /// The run completed — nothing failed — having EXECUTED NOTHING: no test run, no
    /// file compared, no project selected. `detail` says what it did instead. Its
    /// status is `Completed` (so `check` keeps its honest exit 3, never an exit 1), and
    /// no surface may render it as a pass (made a case by
    | VerifiedNothing of detail: string

module RunOutcome =
    /// The outcome the host records for a `Completed` status: the verdict decides
    /// whether the run verified what it says or nothing at all. THE one mapping from
    /// verdict to run record, so the two cannot disagree.
    let ofCompletedVerdict (verdict: RunVerdict) : RunOutcome =
        match verdict.NothingVerified with
        | Some detail -> VerifiedNothing detail
        | None -> CompletedRun

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
    /// settled). `verdict` is `NothingVerified` — a project that didn't run cannot
    /// count toward a green verdict — but it is not a real test failure either, so
    /// the verdict surfaces it as "waiting on build — tests did not run". `reason`
    /// documents why (e.g. "apphost not produced"). Carries no
    /// elapsed/wasFiltered — nothing executed — so it never lowers a coverage
    /// baseline.
    | TestsDeferred of reason: string
    /// The runner STARTED but aborted before producing a usable result: a
    /// non-zero exit with NO parseable report (the test host crashed or was
    /// killed during shutdown — e.g. the Microsoft.Testing.Platform exit-7
    /// shutdown flake — before flushing its CTRF report). `verdict` is
    /// `NothingVerified` (nothing was verified) but no test was shown to fail
    /// either, so it surfaces as "errored — re-run". Carries no
    /// elapsed/wasFiltered so it never lowers a coverage baseline, and it is
    /// UNCACHEABLE by construction (the cacheKey gate admits only `Verified`
    /// and zero-match results), so a transient abort is never replayed as a
    /// stale verdict. `reason` documents what aborted (exit code + "no report
    /// written").
    | TestsErrored of reason: string
    /// The runner RAN, discovered the project's tests, and the active filter matched
    /// NONE of them — Microsoft.Testing.Platform's exit 8 / "Zero tests ran". Nothing
    /// executed, so nothing was verified.
    ///
    /// A case rather than a magic output-string prefix, so folds tell it apart
    /// structurally rather than by string comparison.
    ///
    /// Its `verdict` is `NothingVerified`, alongside Deferred and Errored: this project
    /// proved nothing. That is NOT the same as saying it failed — per PROJECT, a filter
    /// matching nothing is not that project's fault, and an impact selection naming no
    /// class in the Integration project must not turn that project red. The two
    /// statements are different questions with different answers, which is exactly why
    /// there is no single boolean here that answers both.
    | TestsNoMatch of output: string * elapsed: System.TimeSpan

/// What a single project's result ESTABLISHES — the closed set of three answers a
/// fold over `TestResult` can need.
///
/// Exists because one boolean could not honestly answer both questions folds ask:
/// "did this project verify its tests green?" and "is this project a failure to
/// report?". A zero match answers NO to both, and the predicate that used to serve
/// both (`TestResult.isPassed`) had to pick one — it said TRUE, so
/// `Map.forall isPassed` over a run where every project executed nothing
/// type-checked, was total, and folded to a green. Four aggregators had to remember
/// to re-derive the missing fact; two forgot, and a fifth was found later
///
/// A DU rather than a bool, so the compiler ENUMERATES the decision at every fold
/// instead of leaving `NothingVerified` to be silently swept into whichever side the
/// author had in mind. Adding a `TestResult` case breaks `TestResult.verdict`, and
/// adding a `ProjectVerdict` case breaks every fold — which is the list this DU
/// buys.
type ProjectVerdict =
    /// The runner executed AT LEAST ONE test and every one passed. The ONLY case
    /// that discharges anything: a cacheable green, a symbol's test debt, a green run
    /// verdict. If you are writing a fold that lets something through, this is the
    /// case you want and the other two are not it.
    | Verified
    /// The runner executed tests and at least one failed, or was killed at its
    /// timeout. The only case that is this project's OWN fault, and the only one a
    /// "which projects failed?" report should list.
    | Refuted
    /// NOTHING was verified: the filter matched no test (`TestsNoMatch`), the apphost
    /// was never built (`TestsDeferred`), or the host aborted before writing a report
    /// (`TestsErrored`). Neither a pass nor a failure — the case whose absence from
    /// the boolean was the defect.
    | NothingVerified

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

    /// THE per-project derivation. Every fold over a `TestResult` routes here, so
    /// there is exactly one place in the tree where these six cases are told apart.
    ///
    /// Deliberately exhaustive, with NO wildcard: a wildcard would silently assign the
    /// next `TestResult` case to whichever arm it fell into, where writing every case
    /// out makes it a compile error here instead.
    ///
    /// There is deliberately no `isPassed` any more. It returned TRUE for
    /// `TestsNoMatch`, which made "we ran nothing" a sub-case of "we passed" at every
    /// fold that reached for it. If you want a bool, `verifiedGreen`
    /// below is the only one offered, and its name says which of the two questions it
    /// answers.
    let verdict =
        function
        | TestsPassed _ -> Verified
        | TestsFailed _
        | TestsTimedOut _ -> Refuted
        // A project that matched nothing (NoMatch), never ran (Deferred), or aborted
        // before producing evidence (Errored) verified NOTHING. None of the three may
        // count toward a green — otherwise a mis-aimed filter, a build-ordering race
        // or a host crash produces a silent false-green CI verdict — and none of the
        // three is a test failure to report either.
        | TestsNoMatch _
        | TestsDeferred _
        | TestsErrored _ -> NothingVerified

    /// Did this project EXECUTE tests and find them all green? The one bool offered
    /// over `verdict`, and the safe direction by construction: it is TRUE only for
    /// `Verified`, so a fold that reaches for it can never let a project that ran
    /// nothing through.
    ///
    /// It is NOT the negation of "failed". `not (verifiedGreen r)` is true for a
    /// zero-match project as well as a red one — which is correct for a gate
    /// (nothing was proved) and wrong for a failure REPORT (nothing failed). A report
    /// must match on `Refuted` rather than negate this.
    let verifiedGreen (r: TestResult) : bool = verdict r = Verified

    /// True for the `TestsNoMatch` case: the project's runner ran and its filter
    /// matched no test. Distinct from the other two `NothingVerified` cases, which
    /// is why it survives `verdict` as its own question: a zero match is a mis-aimed
    /// FILTER (diagnosable, and not this project's fault), where Deferred/Errored are
    /// this project failing to produce a result it owed.
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
    /// The per-project half of "was anything verified". Broader than `verifiedGreen`
    /// in exactly one direction: a project whose tests ran and FAILED executed
    /// plenty, and a run that produced a red has still proved something.
    ///
    /// Derived from `verdict` rather than re-matching the cases, so the two can never
    /// disagree about which results count as having run.
    let executedTests (r: TestResult) : bool =
        match verdict r with
        | Verified
        | Refuted -> true
        // Nothing ran: no match under the filter, apphost never built, host aborted
        // before producing a verdict.
        | NothingVerified -> false

    let isTimedOut =
        function
        | TestsTimedOut _ -> true
        | _ -> false

    /// True for the `TestsDeferred` case: the project's tests didn't run
    /// (apphost not produced). Distinct from a real failure so the verdict can
    /// surface an honest "waiting on build" diagnostic rather than claiming a test
    /// broke.
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

/// What a completed test run earned for one project model: the run, the projects it
/// verified in full (itself or through a same-model baseline), and every reason it cannot
/// support a green. Only an owner's result fold constructs one.
type EarnedEvidence =
    private
        { Completion: TestRunCompleted
          ModelGeneration: int64
          ExpectedProjects: Set<string>
          WholeProjectCoverage: Set<string>
          Refusals: string list }

    member this.RunId = this.Completion.RunId
    member this.Generation = this.ModelGeneration
    member this.FailureReasons = this.Refusals

module internal EarnedEvidence =
    /// A launch with no model, one selected under a replaced model, or a completion that
    /// belongs to a different launch earns nothing. Everything else is evidence of what
    /// ran, carrying every reason it cannot support a green.
    let fromCompletion
        (launchRunId: System.Guid)
        (launchModelGeneration: int64 option)
        (currentModelGeneration: int64 option)
        (expectedProjects: Set<string>)
        (pendingObligationCount: int)
        (baseline: EarnedEvidence option)
        (completed: TestRunCompleted)
        : EarnedEvidence option =
        match launchModelGeneration, currentModelGeneration with
        | Some launched, Some current when
            launched = current
            && launchRunId <> System.Guid.Empty
            && launchRunId = completed.RunId
            ->
            let baselineProjects =
                baseline
                |> Option.filter (fun evidence -> evidence.Generation = current)
                |> Option.map (fun evidence -> evidence.WholeProjectCoverage)
                |> Option.defaultValue Set.empty

            let wholeProjectCoverage =
                completed.Results
                |> Map.toSeq
                |> Seq.choose (fun (project, result) ->
                    match completed.Outcome, result with
                    | Normal, TestsPassed(_, false, _)
                    | Normal, TestsFailed(_, false, _) -> Some project
                    | _ -> None)
                |> Set.ofSeq
                |> Set.union baselineProjects

            let refusals =
                [ match completed.Outcome with
                  | Normal -> ()
                  | Aborted reason -> yield $"run aborted: %s{reason}"

                  if pendingObligationCount <> 0 then
                      yield $"%d{pendingObligationCount} verification obligation(s) remain pending"

                  if expectedProjects.IsEmpty then
                      yield "no project obligations were selected"

                  for project in expectedProjects do
                      match Map.tryFind project completed.Results with
                      | None when Set.contains project baselineProjects -> ()
                      | None -> yield $"%s{project}: no result or baseline for an admitted obligation"
                      | Some result ->
                          match TestResult.verdict result with
                          | Verified when Set.contains project wholeProjectCoverage -> ()
                          | Verified -> yield $"%s{project}: filtered execution without a whole-project baseline"
                          | Refuted -> yield $"%s{project}: tests failed or timed out"
                          | NothingVerified when TestResult.isNoMatch result && Set.contains project baselineProjects ->
                              ()
                          | NothingVerified -> yield $"%s{project}: no tests verified"

                  // A passing selected project cannot hide an errored or failing sibling.
                  for KeyValue(project, result) in completed.Results do
                      match result with
                      | TestsDeferred reason
                      | TestsErrored reason -> yield $"%s{project}: %s{reason}"
                      | TestsFailed _
                      | TestsTimedOut _ when not (Set.contains project expectedProjects) ->
                          yield $"%s{project}: tests failed or timed out"
                      | _ -> ()

                  if not (TestResult.executedAnything completed.Results) then
                      yield "the completion executed no tests" ]

            Some
                { Completion = completed
                  ModelGeneration = current
                  ExpectedProjects = expectedProjects
                  WholeProjectCoverage = wholeProjectCoverage
                  Refusals = List.distinct refusals }
        | _ -> None

    /// A completion that selected nothing because everything was already verified keeps the
    /// evidence it was verified by, provided that evidence belongs to the current model.
    let retainedForZeroSelection (currentModelGeneration: int64 option) (previous: EarnedEvidence option) =
        previous
        |> Option.filter (fun evidence -> Some evidence.Generation = currentModelGeneration)

/// One file's completed analysis, kept as evidence without its compiler trees.
type AnalysisFileEvidence =
    private
        { File: AbsFilePath
          ModelGeneration: int64 option
          Refusals: string list }

module internal AnalysisFileEvidence =
    /// A result plus what the analysis of it concluded. Compiler errors and a failed
    /// symbol analysis are refusals; warnings are not.
    let fromResult (result: FileCheckResult) (symbolAnalysis: Result<unit, string>) =
        let refusals =
            [ match result.CheckResults with
              | ParseOnly -> yield "type checking did not complete"
              | FullCheck checkedResult ->
                  for diagnostic in checkedResult.Diagnostics do
                      if diagnostic.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error then
                          yield diagnostic.Message

              match symbolAnalysis with
              | Ok() -> ()
              | Error reason -> yield reason ]

        { File = result.File
          ModelGeneration = result.ModelGeneration
          Refusals = List.distinct refusals }

/// What an analysis-only daemon earned for one model: the checkable files of that model,
/// and every reason the analysis cannot support a green. It makes no test claim.
type AnalysisEvidence =
    private
        { ModelGeneration: int64
          Files: Set<AbsFilePath>
          Refusals: string list }

    member this.Generation = this.ModelGeneration
    member this.CheckedFiles = this.Files
    member this.FailureReasons = this.Refusals

module internal AnalysisEvidence =
    /// The expected files are the model's checkable membership, never the subset that
    /// happened to be observed: a file with no completed analysis is a refusal, not an
    /// absence. `None` when no model was captured, or when tests are configured — a
    /// daemon that runs tests earns test evidence instead.
    let fromCompleted
        (modelGeneration: int64 option)
        (expectedFiles: Set<AbsFilePath>)
        (configuredTestProjects: Set<string>)
        (outcomes: Map<AbsFilePath, AnalysisFileEvidence>)
        : AnalysisEvidence option =
        match modelGeneration with
        | Some generation when generation >= 0L && configuredTestProjects.IsEmpty ->
            let refusals =
                [ for file in expectedFiles do
                      match Map.tryFind file outcomes with
                      | Some outcome when outcome.File = file && outcome.ModelGeneration = Some generation ->
                          for reason in outcome.Refusals do
                              yield $"%s{AbsFilePath.value file}: %s{reason}"
                      | Some _
                      | None -> yield $"%s{AbsFilePath.value file}: no completed analysis for the current model" ]

            Some
                { ModelGeneration = generation
                  Files = expectedFiles
                  Refusals = refusals }
        | Some _
        | None -> None

/// A BUILD THAT FAILED IS AN ANSWER.
///
/// A red build mints no test receipt and no analysis receipt — the run never happened —
/// so an evidence wait over a broken tree had nothing to end on and blocked until its
/// timeout, on the one tree where the answer was already known and already printed. This
/// is that answer as evidence: the model the build failed under, and what it said.
///
/// It carries no input-tree hash, unlike a test receipt. A receipt's hash exists to bind a
/// RUN to the tree it selected against, because a run can be replayed and a selection can
/// go stale. A build failure is not replayed: the next change rebuilds and mints a new
/// answer under the model that change produced, so there is no second reading for a hash
/// to keep apart.
type CompletedBuildFailure =
    private
        { ModelGeneration: int64
          Reason: string }

    member this.Generation = this.ModelGeneration
    /// Never empty — a failure that names nothing is not minted at all.
    member this.FailureReasons = [ this.Reason ]

module internal CompletedBuildFailure =
    /// `None` when the build ran under no model, or under one this build cannot name: a
    /// failure that is not ABOUT the graded model answers nothing about it. `None` too for
    /// a blank reason — a proof that something failed and cannot say what is a status, and
    /// the whole point of evidence is that it is not one.
    let fromFailure (modelGeneration: int64 option) (reason: string) : CompletedBuildFailure option =
        match modelGeneration with
        | Some generation when generation >= 0L && not (System.String.IsNullOrWhiteSpace reason) ->
            Some
                { ModelGeneration = generation
                  Reason = reason.Trim() }
        | Some _
        | None -> None

/// Implemented by a plugin state that owns a completed build failure. Projected by the
/// work owner in the same publication as the fold that recorded it, exactly like the two
/// receipts above.
type internal ICompletedBuildFailureState =
    abstract CompletedBuildFailure: CompletedBuildFailure option

/// Implemented by a plugin state that owns analysis-only evidence. Published by the same
/// owner retirement as the file-analysis fold that minted it.
type internal IAnalysisEvidenceState =
    abstract AnalysisEvidence: AnalysisEvidence option

/// Implemented by a plugin state that owns earned test evidence. The work owner projects it
/// in the same publication as the state's work, so evidence and retirement cannot disagree.
type internal IEarnedEvidenceState =
    abstract EarnedEvidence: EarnedEvidence option

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
        /// The completed project-model generation the cohort was captured and sealed
        /// against. `None` when no daemon discovery captured it.
        ModelGeneration: int64 option
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
