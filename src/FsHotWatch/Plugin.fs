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
      ReportStatus: PluginStatus -> unit
      RegisterCommand: string * CommandHandler -> unit }

type IFsHotWatchPlugin =
    abstract Name: string
    abstract Initialize: PluginContext -> unit
    abstract Dispose: unit -> unit
