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
        /// RunId of the most recent test run whose afterTests filter triggered this
        /// plugin. Compared against incoming events' RunId to dedupe — at most one
        /// fire per run. Naturally resets when a new run with a different RunId
        /// arrives; no superset heuristics or batch-boundary detection required.
        LastFiredRunId: Guid option
        /// Per-run local accumulation of project results, keyed by the RunId it
        /// belongs to. Reset implicitly when a new RunId's first progress arrives.
        /// Used to evaluate `TestProjects` filters against the cumulative view
        /// without depending on the event carrying cumulative state itself.
        RunAccumulator: (Guid * Map<string, TestResult>) option
    }

/// Filter for afterTests trigger — either fire on any completed test run,
/// or only when all supplied project names have completed.
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
              SubscribeTestProgress
              SubscribeTestRunCompleted ]
        |> Set.ofList

    let matches (filter: TestFilter) (results: Map<string, TestResult>) : bool =
        match filter with
        | AnyTest -> not results.IsEmpty
        | TestProjects names -> names |> Set.forall (fun n -> Map.containsKey n results)

/// Why a FileCommandPlugin invocation was triggered. Used as a structured
/// source for the subtask key passed to the daemon's activity log so that
/// concurrent invocations of the same plugin (e.g. two rapid file changes,
/// or a file change immediately followed by a test run) don't collide.
type private TriggerReason =
    | FileMatched of firstFile: string
    | TestsCompleted

let private subtaskKey (nameStr: string) (reason: TriggerReason) : string =
    match reason with
    | FileMatched file -> $"{nameStr}:{System.IO.Path.GetFileName file}"
    | TestsCompleted -> $"{nameStr}:tests-completed"

/// Creates a framework plugin handler that runs a command in response to the configured trigger(s).
let create
    (name: PluginName)
    (trigger: CommandTrigger)
    (command: string)
    (args: string)
    (getCommitId: (unit -> string option) option)
    (timeoutSec: int option)
    : PluginHandler<FileCommandState, unit> =
    let nameStr = PluginName.value name

    let cmdTimeout =
        match timeoutSec with
        | Some s -> TimeSpan.FromSeconds(float s)
        | None -> System.Threading.Timeout.InfiniteTimeSpan

    /// Run the command and return the resulting CommandResult. Callers merge
    /// this into the full plugin state so runCommand stays agnostic of
    /// trigger-specific fields.
    let runCommand (ctx: PluginCtx<unit>) (reason: TriggerReason) : Async<CommandResult> =
        let triggerKey = subtaskKey nameStr reason

        async {
            ctx.ReportStatus(Running(since = DateTime.UtcNow))

            return!
                PluginCtxHelpers.withSubtask
                    ctx
                    triggerKey
                    $"running {nameStr}"
                    (async {
                        try
                            let (success, output) =
                                runProcessWithTimeout command args ctx.RepoRoot [] cmdTimeout

                            let result = if success then Succeeded output else CommandFailed output

                            if success then
                                ctx.CompleteWithSummary $"%s{nameStr}: succeeded"
                                ctx.ClearErrors $"<%s{nameStr}>"
                                ctx.ReportStatus(Completed(DateTime.UtcNow))
                            elif output.StartsWith(TimedOutPrefix) then
                                ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error output ]
                                ctx.CompleteWithTimeout(output.Split('\n').[0])
                                ctx.ReportStatus(PluginStatus.Failed($"%s{nameStr} timed out", DateTime.UtcNow))
                            else
                                ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error output ]
                                ctx.CompleteWithSummary $"%s{nameStr}: failed"
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
                            ctx.CompleteWithSummary $"%s{nameStr}: crashed"
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                            ctx.EmitCommandCompleted(
                                { Name = nameStr
                                  Outcome = FsHotWatch.Events.CommandFailed ex.Message }
                            )

                            return CommandFailed ex.Message
                    })
        }

    /// Try to fire the command against a run-wide results view. Dedups on RunId
    /// so at most one fire per run; the caller supplies whichever results view
    /// is relevant (cumulative for progress, final for completion).
    let tryFire
        (ctx: PluginCtx<unit>)
        (state: FileCommandState)
        (runId: Guid)
        (results: Map<string, TestResult>)
        : Async<FileCommandState> =
        async {
            match trigger.AfterTests with
            | Some filter when state.LastFiredRunId <> Some runId && CommandTrigger.matches filter results ->
                let! result = runCommand ctx TestsCompleted

                return
                    { state with
                        LastResult = result
                        LastFiredRunId = Some runId }
            | _ -> return state
        }

    { Name = name
      Init =
        { LastResult = NeverRun
          LastFiredRunId = None
          RunAccumulator = None }
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

                        match matching with
                        | [] -> return state
                        | first :: _ ->
                            let! result = runCommand ctx (FileMatched first)
                            return { state with LastResult = result }

                | TestProgress progress ->
                    let accumulated =
                        match state.RunAccumulator with
                        | Some(prevRunId, acc) when prevRunId = progress.RunId ->
                            progress.NewResults |> Map.fold (fun a k v -> Map.add k v a) acc
                        | _ -> progress.NewResults

                    let! state' = tryFire ctx state progress.RunId accumulated

                    return
                        { state' with
                            RunAccumulator = Some(progress.RunId, accumulated) }

                | TestRunCompleted completed ->
                    // TestRunCompleted always carries the full cumulative Results,
                    // so cache-hit replays (which skip TestProgress) still fire the
                    // command correctly. Same dedupe semantics.
                    return! tryFire ctx state completed.RunId completed.Results

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
