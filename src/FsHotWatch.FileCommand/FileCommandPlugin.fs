module FsHotWatch.FileCommand.FileCommandPlugin

open System
open System.Diagnostics
open FsHotWatch.Events
open FsHotWatch.Plugin

/// Runs a command when files matching a filter change.
/// Register multiple instances for multiple file patterns.
type FileCommandPlugin
    (
        name: string,
        fileFilter: string -> bool,
        command: string,
        args: string
    ) =
    let mutable lastResult: (bool * string) option = None

    interface IFsHotWatchPlugin with
        member _.Name = name

        member _.Initialize(ctx) =
            ctx.OnFileChanged.Add(fun change ->
                let files =
                    match change with
                    | SourceChanged files -> files
                    | ProjectChanged files -> files
                    | SolutionChanged -> []

                let matchingFiles = files |> List.filter fileFilter

                if not matchingFiles.IsEmpty then
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                    try
                        let psi = ProcessStartInfo(command, args)
                        psi.RedirectStandardOutput <- true
                        psi.RedirectStandardError <- true
                        psi.UseShellExecute <- false
                        psi.WorkingDirectory <- ctx.RepoRoot

                        use proc = Process.Start(psi)

                        let stdout =
                            proc.StandardOutput.ReadToEndAsync()
                            |> Async.AwaitTask
                            |> Async.RunSynchronously

                        let stderr =
                            proc.StandardError.ReadToEndAsync()
                            |> Async.AwaitTask
                            |> Async.RunSynchronously

                        proc.WaitForExit()
                        let success = proc.ExitCode = 0
                        let output = $"%s{stdout}\n%s{stderr}".Trim()
                        lastResult <- Some(success, output)

                        if success then
                            ctx.ReportStatus(Completed(box lastResult, DateTime.UtcNow))
                        else
                            ctx.ReportStatus(
                                PluginStatus.Failed(
                                    $"%s{name} failed (exit %d{proc.ExitCode})",
                                    DateTime.UtcNow
                                )
                            )
                    with ex ->
                        lastResult <- Some(false, ex.Message)
                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            ctx.RegisterCommand(
                $"%s{name}-status",
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
