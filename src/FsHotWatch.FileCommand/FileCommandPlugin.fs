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

type FileCommandState =
    {
        LastResult: CommandResult
        /// Identity of the most recent TestResults snapshot the afterTests
        /// branch has already fired for. Progressive emission can send
        /// multiple TestCompleted events that all satisfy the filter (e.g.
        /// cumulative emissions after the condition first becomes true); we
        /// only want to run the command once per batch. A batch is identified
        /// by the set of project names seen so far — once that set grows to
        /// include every listed project, subsequent emissions that still
        /// satisfy the filter share a superset of that key and are skipped.
        LastAfterTestsKey: Set<string> option
    }

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
        // "all listed projects completed" — since TestPrune emits TestCompleted
        // progressively (a cumulative prefix-chain per group), this condition
        // goes from false → true exactly once per batch, on the first emission
        // that carries every listed project. Plugin-side idempotency (LastResult
        // check in Update) ensures the command runs once even though subsequent
        // emissions still satisfy the predicate.
        | TestProjects names -> names |> Set.forall (fun n -> Map.containsKey n results.Results)

/// Creates a framework plugin handler that runs a command in response to the configured trigger(s).
let create
    (name: PluginName)
    (trigger: CommandTrigger)
    (command: string)
    (args: string)
    (getCommitId: (unit -> string option) option)
    : PluginHandler<FileCommandState, unit> =
    let nameStr = PluginName.value name

    /// Run the command and return the resulting CommandResult. Callers merge
    /// this into the full plugin state so runCommand stays agnostic of
    /// trigger-specific fields like LastAfterTestsKey.
    let runCommand (ctx: PluginCtx<unit>) (triggerKey: string) : Async<CommandResult> =
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

                            return result
                        with ex ->
                            ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error ex.Message ]
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                            ctx.EmitCommandCompleted(
                                { Name = nameStr
                                  Outcome = FsHotWatch.Events.CommandFailed ex.Message }
                            )

                            return CommandFailed ex.Message
                    })
        }

    { Name = name
      Init =
        { LastResult = NeverRun
          LastAfterTestsKey = None }
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

                            let! result = runCommand ctx triggerKey
                            return { state with LastResult = result }
                | TestCompleted results ->
                    match trigger.AfterTests with
                    | None -> return state
                    | Some filter ->
                        let currentKey = results.Results |> Map.toSeq |> Seq.map fst |> Set.ofSeq

                        // Progressive emission can deliver multiple TestCompleted
                        // events within one batch (each a cumulative prefix-chain
                        // snapshot) and repeat those batches run after run. We
                        // want to fire at most once per batch — specifically, on
                        // the first emission whose project set satisfies the
                        // filter — and fire again the next time a fresh batch
                        // reaches the filter-satisfying set.
                        //
                        // Batch boundary detection: if the incoming snapshot is
                        // NOT a superset of the last project set we fired for,
                        // a new batch has started (its cumulative emissions
                        // restart with a smaller prefix). Clear the sentinel so
                        // the upcoming full-satisfying emission can fire.
                        let freshBatch =
                            match state.LastAfterTestsKey with
                            | Some prev when not (Set.isSubset prev currentKey) -> true
                            | _ -> false

                        let carriedKey = if freshBatch then None else state.LastAfterTestsKey

                        if not (CommandTrigger.matchesTestResults filter results) then
                            return
                                { state with
                                    LastAfterTestsKey = carriedKey }
                        else
                            match carriedKey with
                            | Some _ ->
                                // Same batch, already fired — skip.
                                return
                                    { state with
                                        LastAfterTestsKey = carriedKey }
                            | None ->
                                let triggerKey = $"{nameStr}:tests-completed"
                                let! result = runCommand ctx triggerKey

                                return
                                    { state with
                                        LastResult = result
                                        LastAfterTestsKey = Some currentKey }
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
