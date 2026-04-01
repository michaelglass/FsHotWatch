module FsHotWatch.FileCommand.FileCommandPlugin

open System
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.ProcessHelper

type CommandResult =
    | NotRun
    | Succeeded of output: string
    | CommandFailed of output: string

type FileCommandState = { LastResult: CommandResult }

/// Creates a framework plugin handler that runs a command when files matching a filter change.
let create
    (name: string)
    (fileFilter: string -> bool)
    (command: string)
    (args: string)
    : PluginHandler<FileCommandState, unit> =
    { Name = name
      Init = { LastResult = NotRun }
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

                    if matching.IsEmpty then
                        return state
                    else
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))

                        try
                            let (success, output) = runProcess command args ctx.RepoRoot []

                            let result = if success then Succeeded output else CommandFailed output

                            if success then
                                ctx.ClearErrors $"<%s{name}>"
                                ctx.ReportStatus(Completed(DateTime.UtcNow))
                            else
                                ctx.ReportErrors
                                    $"<%s{name}>"
                                    [ { Message = output
                                        Severity = DiagnosticSeverity.Error
                                        Line = 0
                                        Column = 0 } ]

                                ctx.ReportStatus(PluginStatus.Failed($"%s{name} failed", DateTime.UtcNow))

                            return { LastResult = result }
                        with ex ->
                            ctx.ReportErrors
                                $"<%s{name}>"
                                [ { Message = ex.Message
                                    Severity = DiagnosticSeverity.Error
                                    Line = 0
                                    Column = 0 } ]

                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                            return { LastResult = CommandFailed ex.Message }
                | _ -> return state
            }
      Commands =
        [ $"%s{name}-status",
          fun state _args ->
              async {
                  match state.LastResult with
                  | Succeeded _ -> return "{\"passed\": true}"
                  | CommandFailed _ -> return "{\"passed\": false}"
                  | NotRun -> return "{\"status\": \"not run\"}"
              } ]
      Subscriptions =
        { PluginSubscriptions.none with
            FileChanged = true } }
