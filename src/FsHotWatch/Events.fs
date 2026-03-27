/// Core event and status types for the FsHotWatch daemon.
module FsHotWatch.Events

open FSharp.Compiler.CodeAnalysis

/// Identifies a check result in the cache
type CacheKey =
    {
        /// Content hash of the file being checked (from jj or file metadata)
        FileHash: string
        /// Hash of project options (dependencies, compiler flags)
        ProjectOptionsHash: string
    }

/// Information about a cache operation.
/// Designed for the two-tier cache model (in-memory + file-based) that will be implemented
/// in Tasks 3-5. The FromMemory field tracks which tier served the cache hit, enabling
/// statistics and future optimization of the cache hierarchy.
type CacheOperationInfo =
    { Key: CacheKey
      File: string
      HitCache: bool
      FromMemory: bool }

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
        /// FCS type-check results (symbols, diagnostics).
        CheckResults: FSharpCheckFileResults
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
    | Completed of result: obj * at: System.DateTime
    /// Plugin encountered an error.
    | Failed of error: string * at: System.DateTime

/// Named plugin status for serialization.
[<NoComparison>]
type PluginResult =
    { PluginName: string
      Status: PluginStatus }

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
