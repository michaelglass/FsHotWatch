module FsHotWatch.Build.BuildPlugin

open System
open System.Diagnostics
open FsHotWatch.Events
open FsHotWatch.Plugin

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
                        let psi = ProcessStartInfo(buildCommand, buildArgs)
                        psi.RedirectStandardOutput <- true
                        psi.RedirectStandardError <- true
                        psi.UseShellExecute <- false
                        psi.WorkingDirectory <- ctx.RepoRoot

                        use proc = Process.Start(psi)

                        let stdout =
                            proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask |> Async.RunSynchronously

                        let stderr =
                            proc.StandardError.ReadToEndAsync() |> Async.AwaitTask |> Async.RunSynchronously

                        proc.WaitForExit()
                        let success = proc.ExitCode = 0
                        let output = $"%s{stdout}\n%s{stderr}".Trim()
                        lastResult <- Some(success, output)

                        if success then
                            ctx.EmitBuildCompleted(BuildSucceeded)
                            ctx.ReportStatus(Completed(box lastResult, DateTime.UtcNow))
                        else
                            ctx.EmitBuildCompleted(BuildFailed [ output ])
                            ctx.ReportStatus(Failed($"Build failed (exit %d{proc.ExitCode})", DateTime.UtcNow))
                    with ex ->
                        lastResult <- Some(false, ex.Message)
                        ctx.EmitBuildCompleted(BuildFailed [ ex.Message ])
                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                | SolutionChanged -> ())

            ctx.RegisterCommand(
                "build-status",
                fun _args ->
                    async {
                        match lastResult with
                        | Some(ok, _) ->
                            let passed = if ok then "true" else "false"
                            return $"{{\"passed\": %s{passed}}}"
                        | None -> return "{\"status\": \"not run\"}"
                    }
            )

        member _.Dispose() = ()
