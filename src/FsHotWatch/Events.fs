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
        /// Content hash of the file being checked (from jj or file metadata)
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
    | SolutionChanged of file: string

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
        File: string
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

/// Current status of a plugin or preprocessor.
[<NoComparison>]
type PluginStatus =
    /// Plugin is registered but hasn't processed any events yet.
    | Idle
    /// Plugin is currently processing.
    | Running of since: System.DateTime
    /// Plugin finished processing successfully.
    | Completed of at: System.DateTime
    /// Plugin encountered an error.
    | Failed of error: string * at: System.DateTime

module PluginStatus =
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
type TestResult =
    | TestsPassed of output: string * wasFiltered: bool
    | TestsFailed of output: string * wasFiltered: bool
    /// The runner exceeded its configured `timeoutSec` and was killed. Distinct
    /// from `TestsFailed` so consumers can react to "stuck" runs (e.g. flag the
    /// whole run TimedOut) without grepping the output for a magic prefix.
    | TestsTimedOut of output: string * after: System.TimeSpan * wasFiltered: bool

module TestResult =
    let output =
        function
        | TestsPassed(o, _)
        | TestsFailed(o, _)
        | TestsTimedOut(o, _, _) -> o

    let wasFiltered =
        function
        | TestsPassed(_, w)
        | TestsFailed(_, w)
        | TestsTimedOut(_, _, w) -> w

    let isPassed =
        function
        | TestsPassed _ -> true
        | TestsFailed _
        | TestsTimedOut _ -> false

    let isTimedOut =
        function
        | TestsTimedOut _ -> true
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
    /// Scan completed.
    | ScanComplete of total: int * elapsed: System.TimeSpan

/// Outcome of a command execution.
type CommandOutcome =
    | CommandSucceeded of output: string
    | CommandFailed of output: string

/// Result of a command execution (e.g., file command plugin completing a shell command).
type CommandCompletedResult =
    { Name: string
      Outcome: CommandOutcome }

/// Events routed to plugins by the framework.
[<NoComparison; NoEquality>]
type PluginEvent<'Msg> =
    | FileChanged of FileChangeKind
    | FileChecked of FileCheckResult
    | BuildCompleted of BuildResult
    | TestRunStarted of TestRunStarted
    | TestProgress of TestProgress
    | TestRunCompleted of TestRunCompleted
    | CommandCompleted of CommandCompletedResult
    | Custom of 'Msg
