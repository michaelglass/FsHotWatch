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

/// Filter for afterTests trigger — either fire on any TestCompleted event,
/// or only when the supplied project names appear in the results.
type TestFilter =
    | AnyTest
    | TestProjects of Set<string>

/// Describes what causes a FileCommandPlugin to run its command.
/// At least one of `FilePattern` / `AfterTests` must be set (validated at config parse time).
[<NoComparison; NoEquality>]
type CommandTrigger =
    { FilePattern: (string -> bool) option
      AfterTests: TestFilter option }

module CommandTrigger =
    let subscriptions (t: CommandTrigger) : Set<SubscribedEvent> =
        [ if t.FilePattern.IsSome then
              SubscribeFileChanged
          if t.AfterTests.IsSome then
              SubscribeTestCompleted ]
        |> Set.ofList

    let matchesTestResults (filter: TestFilter) (results: TestResults) : bool =
        match filter with
        | AnyTest -> not results.Results.IsEmpty
        | TestProjects names -> results.Results |> Map.exists (fun k _ -> Set.contains k names)

/// Creates a framework plugin handler that runs a command in response to the configured trigger(s).
let create
    (name: PluginName)
    (trigger: CommandTrigger)
    (command: string)
    (args: string)
    (getCommitId: (unit -> string option) option)
    : PluginHandler<FileCommandState, unit> =
    let nameStr = PluginName.value name

    let runCommand (ctx: PluginCtx<unit>) (triggerKey: string) : Async<FileCommandState> =
        async {
            ctx.ReportStatus(Running(since = DateTime.UtcNow))

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
                                ctx.ReportStatus(PluginStatus.Failed($"%s{nameStr} failed", DateTime.UtcNow))

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
        }

    { Name = name
      Init = { LastResult = NeverRun }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged change ->
                    match trigger.FilePattern with
                    | None -> return state
                    | Some fileFilter ->
                        let files =
                            match change with
                            | SourceChanged f -> f
                            | ProjectChanged f -> f
                            | SolutionChanged _ -> []

                        let matching = files |> List.filter fileFilter

                        if List.isEmpty matching then
                            return state
                        else
                            let triggerKey =
                                match matching with
                                | [] -> $"{nameStr}:startup"
                                | first :: _ -> $"{nameStr}:{System.IO.Path.GetFileName first}"

                            return! runCommand ctx triggerKey
                | TestCompleted results ->
                    match trigger.AfterTests with
                    | Some filter when CommandTrigger.matchesTestResults filter results ->
                        let triggerKey = $"{nameStr}:tests-completed"
                        return! runCommand ctx triggerKey
                    | _ -> return state
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
      Subscriptions = CommandTrigger.subscriptions trigger
      CacheKey = FsHotWatch.TaskCache.optionalCacheKey getCommitId
      Teardown = None }
