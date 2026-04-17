module FsHotWatch.FileCommand.FileCommandPlugin

open System
open System.Text.Json
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.ProcessHelper

type CommandResult =
    | NeverRun
    | Succeeded of output: string
    | CommandFailed of output: string

type FileCommandState = { LastResult: CommandResult }

/// Creates a framework plugin handler that runs a command when files matching a filter change.
let create
    (name: PluginName)
    (fileFilter: string -> bool)
    (command: string)
    (args: string)
    (runOnStart: bool)
    (getCommitId: (unit -> string option) option)
    : PluginHandler<FileCommandState, unit> =
    let nameStr = PluginName.value name

    { Name = name
      Init = { LastResult = NeverRun }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged change ->
                    let files =
                        match change with
                        | SourceChanged f -> f
                        | ProjectChanged f -> f
                        | SolutionChanged _ -> []

                    let matching = files |> List.filter fileFilter

                    let shouldRun =
                        (runOnStart && state.LastResult = NeverRun)
                        || (matching |> (not << List.isEmpty))

                    if not shouldRun then
                        return state
                    else
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))

                        let triggerKey =
                            match matching with
                            | [] -> $"{nameStr}:startup"
                            | first :: _ -> $"{nameStr}:{System.IO.Path.GetFileName first}"

                        return!
                            PluginCtxHelpers.withSubtask
                                ctx
                                triggerKey
                                $"running {nameStr}"
                                (async {
                                    try
                                        let (success, output) = runProcess command args ctx.RepoRoot []

                                        let result = if success then Succeeded output else CommandFailed output

                                        if success then
                                            ctx.CompleteWithSummary $"ran {nameStr}"
                                            ctx.ClearErrors $"<%s{nameStr}>"
                                            ctx.ReportStatus(Completed(DateTime.UtcNow))
                                        else
                                            ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error output ]

                                            ctx.ReportStatus(
                                                PluginStatus.Failed($"%s{nameStr} failed", DateTime.UtcNow)
                                            )

                                        ctx.EmitCommandCompleted(
                                            { Name = nameStr
                                              Outcome =
                                                if success then
                                                    FsHotWatch.Events.CommandSucceeded output
                                                else
                                                    FsHotWatch.Events.CommandFailed output }
                                        )

                                        return { LastResult = result }
                                    with ex ->
                                        ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error ex.Message ]

                                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                                        ctx.EmitCommandCompleted(
                                            { Name = nameStr
                                              Outcome = FsHotWatch.Events.CommandFailed ex.Message }
                                        )

                                        return { LastResult = CommandFailed ex.Message }
                                })
                | _ -> return state
            }
      Commands =
        [ $"%s{nameStr}-status",
          fun state _args ->
              async {
                  match state.LastResult with
                  | Succeeded _ -> return JsonSerializer.Serialize({| passed = true |})
                  | CommandFailed _ -> return JsonSerializer.Serialize({| passed = false |})
                  | NeverRun -> return JsonSerializer.Serialize({| status = "not run" |})
              } ]
      Subscriptions = Set.ofList [ SubscribeFileChanged ]
      CacheKey = FsHotWatch.TaskCache.optionalCacheKey getCommitId
      Teardown = None }
