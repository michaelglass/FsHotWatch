module FsHotWatch.FileCommand.FileCommandPlugin

open System
open System.Text.Json
open System.Threading
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.ProcessHelper

/// Env var name set on every afterTests-triggered child process.
/// Carries a `FullSuiteClaim` token — `"true"` / `"false"` / `"unknown"`.
/// See README for downstream usage.
[<Literal>]
let RanFullSuiteEnvVar = "FSHW_RAN_FULL_SUITE"

/// What an `afterTests` command can HONESTLY be told about the breadth of the
/// run that fired it.
///
/// This was a plain `bool`, and a bool cannot carry the question. Hooks are
/// ARBITRARY USER CODE — the documented use is "gate baseline refreshes or
/// threshold tightening on it" — so the one thing that must never happen is a
/// `"true"` on a run that did not, in fact, run the whole suite. Both boolean
/// values lie in one direction: `"true"` overstates a run that verified nothing
/// or is still in flight, and `"false"` asserts a filtered run that never
/// happened. The third value says "cannot tell you" instead of picking a lie,
/// and a hook gated on `= "true"` (the documented idiom) is correct under it.
type FullSuiteClaim =
    /// The run is COMPLETE, executed at least one test, and no project was
    /// impact-filtered. The ONLY value that licenses a baseline refresh.
    | FullSuite
    /// At least one project in view was impact-filtered. Provable from a
    /// partial view too — a filtered project stays filtered for the whole run.
    | PartialSuite
    /// Unprovable either way: the fire is MID-RUN (a prefix of the run cannot
    /// prove that nothing LATER is filtered), or the run executed nothing at all.
    | BreadthUnknown

[<RequireQualifiedAccess>]
module FullSuiteClaim =

    /// Wire value for `FSHW_RAN_FULL_SUITE`. The ONLY place these strings are written.
    let token (claim: FullSuiteClaim) : string =
        match claim with
        | FullSuite -> "true"
        | PartialSuite -> "false"
        | BreadthUnknown -> "unknown"

    /// Derive the claim from the results view a fire is about.
    ///
    /// `isFinal` is true ONLY for `TestRunCompleted`, whose `Results` is the whole
    /// run. A `TestProgress` accumulator is a strict PREFIX: the plugin fires as
    /// soon as its filter is satisfied, which for `afterTests: true` is the first
    /// group of a multi-`group` run. Deriving "full suite" from a prefix said the
    /// whole suite ran while later, impact-filtered groups had yet to report — and
    /// RunId dedupe means the truthful `TestRunCompleted` never corrects it.
    ///
    /// `ranFullSuite` is the caller's authoritative filtered/unfiltered signal
    /// (`TestRunCompleted.RanFullSuite` for a final view, `TestResult.ranFullSuite`
    /// over the accumulator for a partial one). It is NOT re-derived here, because
    /// the event's own field is the contract for the final view.
    let derive (isFinal: bool) (results: Map<string, TestResult>) (ranFullSuite: bool) : FullSuiteClaim =
        if not (TestResult.executedAnything results) then
            // NOTHING EXECUTED — so no claim about breadth is available, and a hook
            // must not be handed a licence to refresh a coverage baseline.
            //
            // Deriving it here rather than trusting `ranFullSuite` is still load
            // bearing after AUTOMATION-281 made the producers honest: `false` means
            // "filtered OR nothing ran" (see `TestRunCompleted.RanFullSuite`), and
            // `PartialSuite` is a different claim from "unknown". A replayed cache
            // entry or an external producer can also arrive here.
            BreadthUnknown
        elif not ranFullSuite then
            PartialSuite
        elif isFinal then
            FullSuite
        else
            BreadthUnknown

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
    // `read` is a file-IO call, so transient IO failures (file locked by editor,
    // missing, perms) legitimately drop this entry from the merkle and we tolerate
    // the resulting cache miss. Anything else (NullReferenceException, programming
    // bugs) must surface — bare `with _` would silently mask real defects.
    try
        let bytes = read path
        let hash = System.Security.Cryptography.SHA256.HashData(bytes)
        Some(System.Convert.ToHexString(hash).ToLowerInvariant())
    with
    | :? System.IO.IOException -> None
    | :? System.UnauthorizedAccessException -> None

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

    // A fileCommand is an arbitrary user command — it may print nothing at all
    // (a linter that only speaks on failure), so its output cannot prove
    // liveness and `cmdTimeout` is the bound. It still gets the polled-exit and
    // bounded post-exit drain every spawn gets: a fileCommand whose grandchild
    // (an MSBuild node) holds the inherited stdout pipe no longer wedges the run.
    let cmdBounds = ProcessBounds.silent cmdTimeout

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
            let runStarted = DateTime.UtcNow
            ctx.ReportStatus(Running(since = runStarted))

            return!
                PluginCtxHelpers.withSubtask
                    ctx
                    triggerKey
                    $"running {nameStr}"
                    (async {
                        try
                            let processResult = runProcess command args ctx.RepoRoot extraEnv cmdBounds

                            let output = outputOf processResult

                            let cmdResult =
                                match processResult with
                                // `output` (rendered) rather than the raw capture: an
                                // incomplete drain is named in the text a human reads.
                                | ProcessOutcome.Succeeded _ -> Succeeded output
                                | _ -> CommandFailed output

                            let finishedAt = DateTime.UtcNow
                            let elapsed = finishedAt - runStarted

                            match processResult with
                            | ProcessOutcome.Succeeded _ ->
                                ctx.ClearErrors $"<%s{nameStr}>"

                                ctx.ReportStatus(
                                    Completed(finishedAt, RunVerdict.create $"%s{nameStr}: succeeded" elapsed)
                                )
                            | ProcessOutcome.TimedOut(after, _, kill) ->
                                // `output` is `outputOf processResult`, so a failed kill is
                                // already spelled out in full in the error entry; the verdict
                                // and summary are one-liners, so they carry the short marker.
                                let killNote = renderKillBrief kill

                                ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error output ]
                                // Flip the recorded outcome to TimedOut; the verdict
                                // below carries the summary (one channel).
                                ctx.CompleteWithTimeout $"%d{int after.TotalSeconds}s%s{killNote}"

                                ctx.ReportStatus(
                                    PluginStatus.Failed(
                                        $"%s{nameStr} timed out",
                                        finishedAt,
                                        RunVerdict.create
                                            $"%s{nameStr}: timed out after %d{int after.TotalSeconds}s%s{killNote}"
                                            elapsed
                                    )
                                )
                            | ProcessOutcome.Failed _ ->
                                ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error output ]

                                ctx.ReportStatus(
                                    PluginStatus.Failed(
                                        $"%s{nameStr} failed",
                                        finishedAt,
                                        RunVerdict.create $"%s{nameStr}: failed" elapsed
                                    )
                                )

                            ctx.EmitCommandCompleted(
                                { Name = nameStr
                                  Outcome =
                                    match processResult with
                                    | ProcessOutcome.Succeeded _ -> FsHotWatch.Events.CommandSucceeded output
                                    | _ -> FsHotWatch.Events.CommandFailed output }
                            )

                            return cmdResult
                        with ex ->
                            ctx.ReportErrors $"<%s{nameStr}>" [ ErrorEntry.error ex.Message ]

                            ctx.ReportStatus(
                                PluginStatus.failedNow ex.Message $"%s{nameStr}: crashed" (DateTime.UtcNow - runStarted)
                            )

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
        (claim: FullSuiteClaim)
        : Async<FileCommandState> =
        async {
            match trigger.AfterTests with
            | Some filter when state.LastFiredRunId <> Some runId && CommandTrigger.matches filter results ->
                let env = [ RanFullSuiteEnvVar, FullSuiteClaim.token claim ]

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
                            | SolutionChanged -> []

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

                    // Mid-run view. The accumulator is a strict PREFIX of the run:
                    // it holds only the projects that have reported so far, and
                    // the fire happens as soon as the trigger's filter is
                    // satisfied — for `afterTests: true` that is the FIRST group
                    // of a multi-`group` run. `wasFiltered` is authoritative for
                    // the projects present (a filtered one stays filtered), so a
                    // prefix can prove PARTIAL; it can never prove FULL, because
                    // a group that has not reported yet may be filtered. Hence
                    // `isFinal = false` — see `FullSuiteClaim.derive`.
                    let claim =
                        FullSuiteClaim.derive false accumulated (TestResult.ranFullSuite accumulated)

                    let! state' = tryFire ctx state progress.RunId accumulated claim

                    return
                        { state' with
                            RunAccumulator = Some(progress.RunId, accumulated) }

                | TestRunCompleted completed ->
                    // TestRunCompleted always carries the full cumulative Results,
                    // so cache-hit replays (which skip TestProgress) still fire the
                    // command correctly. Same dedupe semantics. This is the only
                    // view that can license a `"true"` claim (`isFinal = true`) —
                    // and even here only if something actually executed.
                    let claim = FullSuiteClaim.derive true completed.Results completed.RanFullSuite

                    return! tryFire ctx state completed.RunId completed.Results claim

                | _ -> return state
            }
      Commands =
        [ $"%s{nameStr}-status",
          fun _ctx state _args ->
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
        //
        // Always `Some` for a trigger event, so the first trigger's result is
        // stored and an identical trigger replays instead of re-running: a `None`
        // key is uncacheable (neither replays nor stores), which would
        // double-execute this side-effecting command.
        let cacheKey (event: PluginEvent<unit>) : ContentHash option =
            match event with
            | Custom _ -> None
            | _ -> Some(ContentHash.create (computeArgsSalt repoRoot command args))

        Some cacheKey
      Teardown = None }
