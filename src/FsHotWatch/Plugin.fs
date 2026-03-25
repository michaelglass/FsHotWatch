module FsHotWatch.Plugin

open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events

/// A function that handles a named command with string arguments and returns a result.
type CommandHandler = string array -> Async<string>

/// Context provided to each plugin during initialization.
[<NoComparison; NoEquality>]
type PluginContext =
    { /// The warm FSharpChecker instance shared across all plugins.
      Checker: FSharpChecker
      /// The repository root directory.
      RepoRoot: string
      /// Event fired when source, project, or solution files change.
      OnFileChanged: IEvent<FileChangeKind>
      /// Event fired when a build completes (success or failure).
      OnBuildCompleted: IEvent<BuildResult>
      /// Event fired when a single file is type-checked.
      OnFileChecked: IEvent<FileCheckResult>
      /// Event fired when all files in a project are checked.
      OnProjectChecked: IEvent<ProjectCheckResult>
      /// Event fired when test execution completes.
      OnTestCompleted: IEvent<TestResults>
      /// Report the plugin's current status to the host.
      ReportStatus: PluginStatus -> unit
      /// Register a named command that can be invoked via IPC.
      RegisterCommand: string * CommandHandler -> unit
      /// Emit a build completed event to other plugins.
      EmitBuildCompleted: BuildResult -> unit
      /// Emit a test completed event to other plugins.
      EmitTestCompleted: TestResults -> unit }

/// Interface that all FsHotWatch plugins must implement.
type IFsHotWatchPlugin =
    /// The display name of this plugin.
    abstract Name: string
    /// Initialize the plugin with the given context, subscribing to events.
    abstract Initialize: PluginContext -> unit
    /// Dispose plugin resources.
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
