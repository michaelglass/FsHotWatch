module FsHotWatch.FileCommand.FileCommandPlugin

open System
open System.Text.Json
open System.Threading
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.ProcessHelper

/// Env var name set on every afterTests-triggered child process.
/// Value is `"true"` iff every project in the triggering run executed without
/// an impact filter. See README for downstream usage.
[<Literal>]
let RanFullSuiteEnvVar = "FSHW_RAN_FULL_SUITE"

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

/// Hash file content via an injectable reader. Exposed so unit tests can
/// substitute a reader that throws (covers the None branch deterministically)
/// or returns canned bytes (covers the hex-formatting branch). A separate
/// integration test confirms the production reader's failure mode (e.g.
/// chmod-000) really does throw.
let internal hashFileWith (read: string -> byte[]) (path: string) : string option =
    try
        let bytes = read path
        let hash = System.Security.Cryptography.SHA256.HashData(bytes)
        Some(System.Convert.ToHexString(hash).ToLowerInvariant())
    with _ ->
        None

let private tryHashFile = hashFileWith System.IO.File.ReadAllBytes

let private resolveArgPath (repoRoot: string) (token: string) : string =
    if System.IO.Path.IsPathRooted token then
        token
    else
        System.IO.Path.Combine(repoRoot, token)

let private tokenizeArgs (args: string) : string array =
    args.Split(
        [| ' '; '\t' |],
        System.StringSplitOptions.RemoveEmptyEntries
        ||| System.StringSplitOptions.TrimEntries
    )

/// Returns the absolute paths of arg tokens that resolve to an existing file
/// (relative to repoRoot or absolute). Used by reporters to detect when a
/// plugin's input has been edited after its last successful run.
let collectArgFiles (repoRoot: string) (args: string) : string list =
    tokenizeArgs args
    |> Array.choose (fun tok ->
        let resolved = resolveArgPath repoRoot tok

        if System.IO.File.Exists(resolved) then
            Some resolved
        else
            None)
    |> Array.toList

/// Returns the absolute paths of arg-file tokens whose mtime exceeds
/// `referenceTime`. A non-empty result hints that a cached plugin run from
/// before `referenceTime` may not reflect current input.
let argsStalerThan (repoRoot: string) (args: string) (referenceTime: System.DateTime) : string list =
    let ref = referenceTime.ToUniversalTime()

    tokenizeArgs args
    |> Array.choose (fun tok ->
        let path = resolveArgPath repoRoot tok

        try
            if System.IO.File.GetLastWriteTimeUtc(path) > ref then
                Some path
            else
                None
        with _ ->
            None)
    |> Array.toList

/// Salt computation with an injectable hash function. Exposed so unit tests
/// can deterministically exercise the None branch — the case where a path
/// passes File.Exists during collectArgFiles but the subsequent read fails
/// (e.g. file deleted in between, or permissions changed). An integration
/// test confirms the production reader's failure mode is realistic.
let internal computeArgsSaltWith
    (hashFile: string -> string option)
    (repoRoot: string)
    (command: string)
    (args: string)
    : string =
    let fileHashes =
        collectArgFiles repoRoot args
        |> List.choose (fun path -> hashFile path |> Option.map (fun h -> $"file:%s{path}", h))

    let inputs = [ "command", command; "args", args ] @ fileHashes

    FsHotWatch.TaskCache.merkleCacheKey inputs
    |> FsHotWatch.Events.ContentHash.value

/// Build the salt for this plugin's cache key. Includes the command, the args
/// string, and a content hash of every whitespace-separated token in args
/// that resolves to an existing file (relative to repoRoot or absolute).
/// This means editing a config file referenced in args invalidates the cache
/// even when commit_id hasn't changed.
let internal computeArgsSalt (repoRoot: string) (command: string) (args: string) : string =
    computeArgsSaltWith tryHashFile repoRoot command args

/// Creates a framework plugin handler that runs a command in response to the configured trigger(s).
let create
    (name: PluginName)
    (trigger: CommandTrigger)
    (command: string)
    (args: string)
    (repoRoot: string)
    (timeoutSec: int option)
    : PluginHandler<FileCommandState, unit> =
    let nameStr = PluginName.value name

    let cmdTimeout =
        match timeoutSec with
        | Some s -> TimeSpan.FromSeconds(float s)
        | None -> System.Threading.Timeout.InfiniteTimeSpan

    // Cold-start guard: until the command has actually executed once in this
    // daemon session, CacheKey returns None so a stale on-disk cache entry from
    // a prior session can't pre-empt the first fire. Mirrors TestPrunePlugin's
    // hadPriorResultsRef pattern. Flipped to true inside runCommand below.
    let mutable hasFiredInSessionRef = false

    /// Run the command and return the resulting CommandResult. Callers merge
    /// this into the full plugin state so runCommand stays agnostic of
    /// trigger-specific fields.
    let runCommand
        (ctx: PluginCtx<unit>)
        (reason: TriggerReason)
        (extraEnv: (string * string) list)
        : Async<CommandResult> =
        let triggerKey = subtaskKey nameStr reason

        async {
            ctx.ReportStatus(Running(since = DateTime.UtcNow))
            // Mark before the subtask so CacheKey starts returning Some as soon as
            // the command begins; in-session re-triggers at the same commit cache-hit
            // and skip re-running. Cold-start (false) bypasses any prior-session entry.
            Volatile.Write(&hasFiredInSessionRef, true)

            return!
                PluginCtxHelpers.withSubtask
                    ctx
                    triggerKey
                    $"running {nameStr}"
                    (async {
                        try
                            let processResult =
                                runProcessWithTimeout command args ctx.RepoRoot extraEnv cmdTimeout

                            let output = outputOf processResult

                            let cmdResult =
                                match processResult with
                                | ProcessOutcome.Succeeded out -> Succeeded out
                                | _ -> CommandFailed output

                            match processResult with
                            | ProcessOutcome.Succeeded _ ->
                                ctx.CompleteWithSummary $"%s{nameStr}: succeeded"
                                ctx.ClearErrors $"<%s{nameStr}>"
                                ctx.ReportStatus(Completed(DateTime.UtcNow))
                            | ProcessOutcome.TimedOut(after, _) ->
                                ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error output ]
                                ctx.CompleteWithTimeout $"timed out after %d{int after.TotalSeconds}s"
                                ctx.ReportStatus(PluginStatus.Failed($"%s{nameStr} timed out", DateTime.UtcNow))
                            | ProcessOutcome.Failed _ ->
                                ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error output ]
                                ctx.CompleteWithSummary $"%s{nameStr}: failed"
                                ctx.ReportStatus(PluginStatus.Failed($"%s{nameStr} failed", DateTime.UtcNow))

                            ctx.EmitCommandCompleted(
                                { Name = nameStr
                                  Outcome =
                                    match processResult with
                                    | ProcessOutcome.Succeeded out -> FsHotWatch.Events.CommandSucceeded out
                                    | _ -> FsHotWatch.Events.CommandFailed output }
                            )

                            return cmdResult
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
        (ranFullSuite: bool)
        : Async<FileCommandState> =
        async {
            match trigger.AfterTests with
            | Some filter when state.LastFiredRunId <> Some runId && CommandTrigger.matches filter results ->
                let env = [ RanFullSuiteEnvVar, (if ranFullSuite then "true" else "false") ]

                let! result = runCommand ctx TestsCompleted env

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
                            let! result = runCommand ctx (FileMatched first) []
                            return { state with LastResult = result }

                | TestProgress progress ->
                    let accumulated =
                        match state.RunAccumulator with
                        | Some(prevRunId, acc) when prevRunId = progress.RunId ->
                            progress.NewResults |> Map.fold (fun a k v -> Map.add k v a) acc
                        | _ -> progress.NewResults

                    // Mid-run view: derive RanFullSuite from the accumulator.
                    // An afterTests fire during progress only happens once all
                    // projects its TestProjects filter names have reported, so
                    // their wasFiltered values are authoritative by then.
                    let ranFullSuite = TestResult.ranFullSuite accumulated
                    let! state' = tryFire ctx state progress.RunId accumulated ranFullSuite

                    return
                        { state' with
                            RunAccumulator = Some(progress.RunId, accumulated) }

                | TestRunCompleted completed ->
                    // TestRunCompleted always carries the full cumulative Results,
                    // so cache-hit replays (which skip TestProgress) still fire the
                    // command correctly. Same dedupe semantics.
                    return! tryFire ctx state completed.RunId completed.Results completed.RanFullSuite

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
      CacheKey =
        // Pure-content cache key: merkle of (command, args, content of every
        // arg-file that exists on disk). No jj commit_id — two daemons on the
        // same inputs hash the same regardless of working-copy state.
        // Recomputed per event so mid-session edits to a referenced config
        // file invalidate the cache.
        let cacheKey (event: PluginEvent<unit>) : ContentHash option =
            match event with
            | Custom _ -> None
            | _ ->
                if Volatile.Read(&hasFiredInSessionRef) then
                    // Cold-start bypass: return None until the command has actually
                    // run once in this daemon session. Without this, an on-disk entry
                    // from a prior session pre-empts the first replay-emitted event
                    // and the command never executes despite its trigger firing.
                    Some(ContentHash.create (computeArgsSalt repoRoot command args))
                else
                    None

        Some cacheKey
      Teardown = None }
