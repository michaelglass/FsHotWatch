module FsHotWatch.FileCommand.FileCommandPlugin

open System
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper

/// Runs a command when files matching a filter change.
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
                        let (success, output) = runProcess command args ctx.RepoRoot []
                        Volatile.Write(&lastResult, Some(success, output))

                        if success then
                            ctx.ReportStatus(Completed(box (Volatile.Read(&lastResult)), DateTime.UtcNow))
                        else
                            ctx.ReportStatus(
                                PluginStatus.Failed($"%s{name} failed", DateTime.UtcNow)
                            )
                    with ex ->
                        Volatile.Write(&lastResult, Some(false, ex.Message))
                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            ctx.RegisterCommand(
                $"%s{name}-status",
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
