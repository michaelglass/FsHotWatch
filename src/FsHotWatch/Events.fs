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

/// Record of a single completed or failed plugin run.
type RunRecord =
    { StartedAt: System.DateTime
      Elapsed: System.TimeSpan
      Outcome: RunOutcome
      Summary: string option
      ActivityTail: string list }


/// Result of a single test project execution.
type TestResult =
    | TestsPassed of output: string
    | TestsFailed of output: string

/// Aggregate test results across all test projects.
type TestResults =
    {
        /// Per-project test results.
        Results: Map<string, TestResult>
        /// Total time for all test execution.
        Elapsed: System.TimeSpan
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
    | TestCompleted of TestResults
    | CommandCompleted of CommandCompletedResult
    | Custom of 'Msg
