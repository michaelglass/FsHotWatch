module FsHotWatch.Plugin

open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events

type CommandHandler = string array -> Async<string>

[<NoComparison; NoEquality>]
type PluginContext =
    { Checker: FSharpChecker
      RepoRoot: string
      OnFileChanged: IEvent<FileChangeKind>
      OnBuildCompleted: IEvent<BuildResult>
      OnFileChecked: IEvent<FileCheckResult>
      OnProjectChecked: IEvent<ProjectCheckResult>
      OnTestCompleted: IEvent<TestResults>
      ReportStatus: PluginStatus -> unit
      RegisterCommand: string * CommandHandler -> unit
      EmitBuildCompleted: BuildResult -> unit
      EmitTestCompleted: TestResults -> unit }

type IFsHotWatchPlugin =
    abstract Name: string
    abstract Initialize: PluginContext -> unit
    abstract Dispose: unit -> unit

/// A preprocessor runs before events are dispatched to plugins.
/// Use for format-on-save: the preprocessor may rewrite files,
/// and the daemon suppresses watcher events for those writes.
type IFsHotWatchPreprocessor =
    abstract Name: string
    /// Process changed files before they're dispatched. Returns the list
    /// of files that were modified (so the daemon can suppress re-triggers).
    abstract Process: changedFiles: string list -> repoRoot: string -> string list
    abstract Dispose: unit -> unit
