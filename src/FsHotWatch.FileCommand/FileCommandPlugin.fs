module FsHotWatch.FileCommand.FileCommandPlugin

open System
open FsHotWatch.AgentHost
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper

type private FileCommandState = { LastResult: (bool * string) option }

type private FileCommandMsg = FileChanged of files: string list

/// Runs a command when files matching a filter change.
type FileCommandPlugin(name: string, fileFilter: string -> bool, command: string, args: string) =
    let mutable agentRef: Agent<FileCommandState, FileCommandMsg> option = None

    interface IFsHotWatchPlugin with
        /// Returns the configured plugin name.
        member _.Name = name

        /// Subscribe to file changes and run the command on matching files; registers a status command.
        member _.Initialize(ctx) =
            let agent =
                createAgent $"FileCommand-%s{name}" { LastResult = None } (fun state msg ->
                    async {
                        match msg with
                        | FileChanged files ->
                            let matchingFiles = files |> List.filter fileFilter

                            if matchingFiles.IsEmpty then
                                return state
                            else
                                ctx.ReportStatus(Running(since = DateTime.UtcNow))

                                try
                                    let (success, output) = runProcess command args ctx.RepoRoot []
                                    let newState = { LastResult = Some(success, output) }

                                    if success then
                                        ctx.ReportStatus(Completed(box newState.LastResult, DateTime.UtcNow))
                                    else
                                        ctx.ReportStatus(PluginStatus.Failed($"%s{name} failed", DateTime.UtcNow))

                                    return newState
                                with ex ->
                                    let newState = { LastResult = Some(false, ex.Message) }

                                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                                    return newState
                    })

            agentRef <- Some agent

            ctx.OnFileChanged.Add(fun change ->
                let files =
                    match change with
                    | SourceChanged files -> files
                    | ProjectChanged files -> files
                    | SolutionChanged -> []

                if not files.IsEmpty then
                    agent.Post(FileChanged files)
                    // Wait for the agent to finish processing to preserve synchronous behavior.
                    agent.GetState() |> Async.RunSynchronously |> ignore)

            ctx.RegisterCommand(
                $"%s{name}-status",
                fun _args ->
                    async {
                        let! state = agent.GetState()

                        match state.LastResult with
                        | Some(ok, _) ->
                            let passed = if ok then "true" else "false"
                            return $"{{\"passed\": %s{passed}}}"
                        | None -> return "{\"status\": \"not run\"}"
                    }
            )

        /// No resources to dispose.
        member _.Dispose() = ()
