module FsHotWatch.Events

open FSharp.Compiler.CodeAnalysis

type FileChangeKind =
    | SourceChanged of files: string list
    | ProjectChanged of files: string list
    | SolutionChanged

type BuildResult =
    | BuildSucceeded
    | BuildFailed of errors: string list

[<NoComparison>]
type FileCheckResult =
    { File: string
      Source: string
      ParseResults: FSharpParseFileResults
      CheckResults: FSharpCheckFileResults }

[<NoComparison>]
type ProjectCheckResult =
    { Project: string
      FileResults: Map<string, FileCheckResult> }

[<NoComparison>]
type PluginStatus =
    | Idle
    | Running of since: System.DateTime
    | Completed of result: obj * at: System.DateTime
    | Failed of error: string * at: System.DateTime

[<NoComparison>]
type PluginResult =
    { PluginName: string
      Status: PluginStatus }
