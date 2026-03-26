module FsHotWatch.Build.BuildPlugin

open System
open System.Text.Json
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ErrorLedger
open FsHotWatch.ProcessHelper

/// Runs a build command when source files change and emits BuildCompleted events.
/// Debounces rapid file changes — waits for 2s of quiet before building.
type BuildPlugin(?command: string, ?args: string) =
    let buildCommand = defaultArg command "dotnet"
    let buildArgs = defaultArg args "build --no-restore"
    let mutable lastResult: (bool * string) option = None
    let mutable building = false

    interface IFsHotWatchPlugin with
        /// Returns "build".
        member _.Name = "build"

        /// Subscribe to file changes and run builds; registers the "build-status" command.
        member _.Initialize(ctx) =
            let doBuild () =
                if not building then
                    building <- true

                    try
                        eprintfn "  [build] Running: %s %s" buildCommand buildArgs
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))

                        try
                            let (success, output) = runProcess buildCommand buildArgs ctx.RepoRoot []
                            Volatile.Write(&lastResult, Some(success, output))
                            eprintfn "  [build] Build %s" (if success then "succeeded" else "FAILED")

                            if success then
                                ctx.ClearErrors "<build>"
                                ctx.EmitBuildCompleted(BuildSucceeded)
                                ctx.ReportStatus(Completed(box (Volatile.Read(&lastResult)), DateTime.UtcNow))
                            else
                                ctx.ReportErrors
                                    "<build>"
                                    [ { Message = output
                                        Severity = "error"
                                        Line = 0
                                        Column = 0 } ]

                                ctx.EmitBuildCompleted(BuildFailed [ output ])
                                let lines = output.Split('\n')
                                let summary = lines |> Array.skip (max 0 (lines.Length - 5)) |> String.concat "\n"
                                ctx.ReportStatus(Failed($"Build failed: %s{summary}", DateTime.UtcNow))
                        with ex ->
                            Volatile.Write(&lastResult, Some(false, ex.Message))

                            ctx.ReportErrors
                                "<build>"
                                [ { Message = ex.Message
                                    Severity = "error"
                                    Line = 0
                                    Column = 0 } ]

                            ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                    finally
                        building <- false

            ctx.OnFileChanged.Add(fun change ->
                match change with
                | SourceChanged _
                | ProjectChanged _ -> doBuild ()
                | SolutionChanged -> ())

            ctx.RegisterCommand(
                "build-status",
                fun _args ->
                    async {
                        match Volatile.Read(&lastResult) with
                        | Some(ok, output) ->
                            let status = if ok then "passed" else "failed"
                            let lines = output.Split('\n')

                            let truncated =
                                lines |> Array.skip (max 0 (lines.Length - 200)) |> String.concat "\n"

                            let escapedOutput = JsonSerializer.Serialize(truncated)
                            return $"{{\"status\": \"%s{status}\", \"output\": %s{escapedOutput}}}"
                        | None -> return "{\"status\": \"not run\"}"
                    }
            )

        /// No resources to dispose.
        member _.Dispose() = ()
