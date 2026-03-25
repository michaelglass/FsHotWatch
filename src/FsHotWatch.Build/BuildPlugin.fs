module FsHotWatch.Build.BuildPlugin

open System
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper

/// Runs a build command when source files change and emits BuildCompleted events.
type BuildPlugin(?command: string, ?args: string) =
    let buildCommand = defaultArg command "dotnet"
    let buildArgs = defaultArg args "build --no-restore"
    let mutable lastResult: (bool * string) option = None

    interface IFsHotWatchPlugin with
        member _.Name = "build"

        member _.Initialize(ctx) =
            ctx.OnFileChanged.Add(fun change ->
                match change with
                | SourceChanged _
                | ProjectChanged _ ->
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                    try
                        let (success, output) = runProcess buildCommand buildArgs ctx.RepoRoot []
                        Volatile.Write(&lastResult, Some(success, output))

                        if success then
                            ctx.EmitBuildCompleted(BuildSucceeded)
                            ctx.ReportStatus(Completed(box (Volatile.Read(&lastResult)), DateTime.UtcNow))
                        else
                            ctx.EmitBuildCompleted(BuildFailed [ output ])
                            ctx.ReportStatus(Failed($"Build failed", DateTime.UtcNow))
                    with ex ->
                        Volatile.Write(&lastResult, Some(false, ex.Message))
                        ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                | SolutionChanged -> ())

            ctx.RegisterCommand(
                "build-status",
                fun _args ->
                    async {
                        match Volatile.Read(&lastResult) with
                        | Some(ok, _) ->
                            let passed = if ok then "true" else "false"
                            return $"{{\"passed\": %s{passed}}}"
                        | None -> return "{\"status\": \"not run\"}"
                    }
            )

        member _.Dispose() = ()
