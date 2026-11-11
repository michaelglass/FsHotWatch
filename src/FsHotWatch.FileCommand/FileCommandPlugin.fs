module FsHotWatch.FileCommand.FileCommandPlugin

open System
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
/// Three values, not a bool, because a bool cannot carry the question. Hooks are
/// ARBITRARY USER CODE — the documented use is "gate baseline refreshes or
/// threshold tightening on it" — so a `"true"` on a run that did not in fact run
/// the whole suite must never happen. Both boolean values lie in one direction:
/// `"true"` overstates a run that verified nothing or is still in flight, and
/// `"false"` asserts a filtered run that never happened. The third value says
/// "cannot tell you", and a hook gated on `= "true"` (the documented idiom) is
/// correct under it.
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

    /// Turn what a run VERIFIED into what a hook may be told.
    ///
    /// The only thing this adds to `RunVerification` is FINALITY: `isFinal` is true
    /// ONLY for `TestRunCompleted`, whose Results are the whole run. A `TestProgress`
    /// accumulator is a strict PREFIX — the plugin fires as soon as its filter is
    /// satisfied, which for `afterTests: true` is the first group of a multi-`group`
    /// run. A prefix can prove PARTIAL (a filtered project stays filtered) but can
    /// never prove FULL, because a group that has not reported may yet be filtered —
    /// and RunId dedupe means the truthful `TestRunCompleted` never corrects a
    /// premature claim.
    let derive (isFinal: bool) (verification: RunVerification) : FullSuiteClaim =
        match RunVerification.scope verification with
        // No scope established — the run verified nothing, so there is no breadth to
        // claim and a hook must not be handed a licence to refresh a baseline.
        | None -> BreadthUnknown
        | Some Partial -> PartialSuite
        | Some RunScope.FullSuite when isFinal -> FullSuite
        | Some RunScope.FullSuite -> BreadthUnknown

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

/// Hash file content via an injectable reader — injectable so unit tests can
/// substitute a reader that throws (the `None` branch) or returns canned bytes
/// (the hex-formatting branch), with a separate integration test confirming the
/// production reader really does throw on e.g. chmod-000.
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

/// Salt computation with an injectable hash function — injectable so a test can
/// deterministically exercise the `None` branch: a path that passes `File.Exists`
/// during `collectArgFiles` but whose subsequent read fails (deleted in between,
/// or permissions changed).
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

/// `change` with every path it names made absolute under `repoRoot`. A relative path is never
/// read against the process's working directory, which other code may move: two events
/// naming one file must key identically however they spell it.
let internal resolveChange (repoRoot: string) (change: FileChangeKind) : FileChangeKind =
    match change with
    | SourceChanged paths -> SourceChanged(paths |> List.map (resolveArgPath repoRoot))
    | ProjectChanged paths -> ProjectChanged(paths |> List.map (resolveArgPath repoRoot))
    | SolutionChanged -> SolutionChanged

/// The files of a change that `filter` matched: the inputs a FileChanged run fires for.
let internal matchingFiles (filter: string -> bool) (change: FileChangeKind) : string list =
    match change with
    | SourceChanged files
    | ProjectChanged files -> files |> List.filter filter
    | SolutionChanged -> []

/// Every file under `dir` that is not build output or tooling state; nothing for a
/// directory outside `repoRoot` (an absolute `/` must never walk the machine), and nothing
/// for one that does not exist (the walk yields nothing for a missing root).
let private filesUnder (repoRoot: string) (dir: string) : string list =
    if not (FsHotWatch.PathFilter.isOutsideRepo repoRoot dir) then
        FsHotWatch.SafeWalk.bestEffortFilePaths FsHotWatch.SafeWalk.SourceExcludedDirs "*" dir
        |> List.ofSeq
    else
        []

/// The directories a command's args reach beyond single files: a directory argument
/// (`lint migrations/`), and for a glob argument (`docs/*.txt`) its literal directory
/// prefix, so every file under it is an input: a superset of what the glob matches, which
/// costs a cache miss at worst and never a replay.
let internal argInputDirs (repoRoot: string) (args: string) : string list =
    tokenizeArgs args
    |> Array.toList
    |> List.choose (fun token ->
        match token.IndexOfAny([| '*'; '?' |]) with
        | -1 ->
            let resolved = resolveArgPath repoRoot token

            if System.IO.Directory.Exists resolved then
                Some resolved
            else
                None
        | wildcard ->
            let prefix = System.IO.Path.GetDirectoryName(token.Substring(0, wildcard))

            if String.IsNullOrEmpty prefix then
                Some repoRoot
            else
                Some(resolveArgPath repoRoot prefix))

/// The files under a set of scopes (a directory and which files in it count), walked ONCE
/// and then kept current from the paths each event names: a named file in a scope joins
/// the listing when it exists and leaves it when it does not. Walking the repository for
/// pattern files on every FileChanged event cost a median 534 ms on a 3,776-file
/// repository (AUTOMATION-1557); with the listing each event pays only for hashing.
/// A solution change drops the listing, and the next read walks again.
///
/// What the listing cannot see: a file added in a scope that no event names (the watcher
/// reports F# inputs and FileCommand patterns only) joins at the next walk, not before.
///
/// Read and folded only from the plugin's own dispatch loop (the key is computed there, as
/// Update runs there), so no two calls overlap; Volatile keeps the convention for plugin state.
/// `onWalked` hears how many files each walk listed (`largeListingWarning`).
type internal InputListing(scopes: (string * (string -> bool)) list, walk: string -> string list, onWalked: int -> unit)
    =
    let mutable listed: Set<string> list option = None

    let excluded (dir: string) (path: string) =
        System.IO.Path.GetRelativePath(dir, path).Replace('\\', '/').Split('/')
        |> Array.exists FsHotWatch.SafeWalk.SourceExcludedDirs.Contains

    let inScope (dir: string) (path: string) =
        let prefix =
            System.IO.Path.TrimEndingDirectorySeparator dir
            + string System.IO.Path.DirectorySeparatorChar

        path.StartsWith(prefix, StringComparison.Ordinal) && not (excluded dir path)

    new(scopes, walk) = InputListing(scopes, walk, ignore)

    /// Every file currently listed, walking each scope the first time.
    member _.Files() : string list =
        let sets =
            match Volatile.Read(&listed) with
            | Some sets -> sets
            | None ->
                let sets =
                    scopes
                    |> List.map (fun (dir, keep) -> walk dir |> List.filter keep |> Set.ofList)

                Volatile.Write(&listed, Some sets)
                onWalked (sets |> List.sumBy Set.count)
                sets

        sets |> Seq.collect id |> Seq.distinct |> List.ofSeq

    /// Fold the paths `change` names into the listing.
    member _.Observe(change: FileChangeKind) : unit =
        match Volatile.Read(&listed), change with
        | None, _ -> ()
        | Some _, SolutionChanged -> Volatile.Write(&listed, None)
        | Some sets, (SourceChanged paths | ProjectChanged paths) ->
            let fold (dir: string, keep: string -> bool) (set: Set<string>) =
                paths
                |> List.fold
                    (fun (acc: Set<string>) path ->
                        if inScope dir path && keep path then
                            if System.IO.File.Exists path then
                                acc.Add path
                            else
                                acc.Remove path
                        else
                            acc)
                    set

            Volatile.Write(&listed, Some(List.map2 fold scopes sets))

/// A listing past this many files is named in a warning: every event stats each one.
[<Literal>]
let LargeListingThreshold = 10_000

/// The warning for a file command whose inputs list `count` files, or `None` within the bound.
let internal largeListingWarning (name: string) (count: int) : string option =
    if count > LargeListingThreshold then
        Some(
            $"file command %s{name}: its pattern and arguments list %d{count} files (more than %d{LargeListingThreshold}); every change event checks each one's size and modification time, and re-hashes those that moved. Narrow the pattern if events feel slow."
        )
    else
        None

/// Content digests of input files (AUTOMATION-1557). Every listed file used to be read on
/// every event, so a broad pattern over a large repository read the whole tree per event.
///
/// A file the event NAMES is always read again. Every other listed file goes to the core's
/// `FileContentHasher`, which re-reads a file only when its `FileStamp` (change time
/// included) moves, and never trusts a stamp younger than its racy window. A cache keyed on
/// size and last-write time, which this replaced, kept a stale digest when a same-size
/// rewrite restored the mtime (`cp -p`, `rsync -t`, a checkout): ADR-008, mtime is not a
/// content oracle. Its "missing" and "unreadable" answers go to `hashFile`, which answers
/// for those files in the key's own terms. Used from the plugin's dispatch loop only.
type internal DigestCache(hashFile: string -> string option, contentHash: string -> string) =
    new(hashFile) = DigestCache(hashFile, FsHotWatch.CheckCache.FileContentHasher().Hash)

    member _.Digest(path: string, named: bool) : string option =
        if named then
            hashFile path
        else
            match contentHash path with
            | "missing"
            | "unreadable" -> hashFile path
            | digest -> Some digest

/// The salt for a FileChanged run: `computeArgsSaltWith` plus the content of every input
/// file (`InputListing`, and the files the change itself matched). Keying only the
/// event's own files let a second, breaking edit replay a success, and let `lint
/// migrations/` replay a pass after a different migration broke.
///
/// Keyed by REPO-RELATIVE path, so two checkouts of the same content agree. A deleted
/// file is keyed as absent. A matched file that exists but cannot be read gives
/// `None`: no key, so the command runs instead of replaying a result for content
/// nobody hashed.
let internal computeFileChangedSaltWith
    (hashFile: string -> string option)
    (repoRoot: string)
    (command: string)
    (args: string)
    (matched: string list)
    : string option =
    let matchedHashes =
        matched
        |> List.distinct
        |> List.sort
        |> List.map (fun path ->
            let label = "matched:" + System.IO.Path.GetRelativePath(repoRoot, path)

            if System.IO.File.Exists path then
                hashFile path |> Option.map (fun hash -> label, hash)
            else
                Some(label, "absent"))

    if matchedHashes |> List.forall Option.isSome then
        let inputs =
            ("args-salt", computeArgsSaltWith hashFile repoRoot command args)
            :: List.choose id matchedHashes

        Some(
            FsHotWatch.TaskCache.merkleCacheKey inputs
            |> FsHotWatch.Events.ContentHash.value
        )
    else
        None

/// How the plugin starts its command: `runProcess`'s shape, so a test can stand in for it.
type internal Spawn = string -> string -> string -> (string * string) list -> ProcessBounds -> ProcessOutcome

/// `ETXTBSY`: the OS refused to execute a file that is open for writing. Linux and macOS
/// both number it 26.
[<Literal>]
let private TextFileBusy = 26

/// Whether a spawn failed because the command's file was open for writing.
let internal isTextFileBusy (failure: exn) =
    match failure with
    | :? System.ComponentModel.Win32Exception as refused -> refused.NativeErrorCode = TextFileBusy
    | _ -> false

/// The pauses, in ms, before each new start of a command refused as busy. A user's script
/// may be regenerated just before it runs, and a child the daemon forked meanwhile keeps
/// a copy of the write handle until that child execs, which takes microseconds, or a few
/// milliseconds on a loaded box. Under a second in all, then the refusal is the answer.
let internal BusyRetryPauses = [ 25; 50; 100; 200; 400 ]

/// Run `spawn`, starting it again after each of `BusyRetryPauses` while the OS refuses it
/// as busy. Any other failure, and a refusal after the last pause, is raised as it came.
let internal spawnRetryingBusy (pause: int -> unit) (spawn: unit -> 'T) : 'T =
    let rec attempt (pauses: int list) =
        match pauses with
        | [] -> spawn ()
        | next :: rest ->
            match
                (try
                    Result.Ok(spawn ())
                 with failure when isTextFileBusy failure ->
                     Result.Error failure)
            with
            | Result.Ok outcome -> outcome
            | Result.Error _ ->
                pause next
                attempt rest

    attempt BusyRetryPauses

/// `create`, with the spawn and the pause between refused spawns injected.
let internal createWith
    (spawn: Spawn)
    (pause: int -> unit)
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
    // liveness and `cmdTimeout` is the bound.
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
            let runElapsed = PluginCtxHelpers.beginRun ctx

            return!
                PluginCtxHelpers.withSubtask
                    ctx
                    triggerKey
                    $"running {nameStr}"
                    (async {
                        try
                            let processResult =
                                spawnRetryingBusy pause (fun () -> spawn command args ctx.RepoRoot extraEnv cmdBounds)

                            let output = outputOf processResult

                            let cmdResult =
                                match processResult with
                                // `output` (rendered) rather than the raw capture: an
                                // incomplete drain is named in the text a human reads.
                                | ProcessOutcome.Succeeded _ -> Succeeded output
                                | _ -> CommandFailed output

                            let finishedAt = DateTime.UtcNow
                            let elapsed = runElapsed ()

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

                            ctx.ReportStatus(PluginStatus.failedNow ex.Message $"%s{nameStr}: crashed" (runElapsed ()))

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

    // The FileChanged key's inputs beyond the event's own files: every file the pattern
    // matches, and every file under a directory or glob argument, walked once and kept
    // current from the events (`InputListing`).
    let inputListing =
        InputListing(
            (match trigger.FilePattern with
             | Some fileFilter -> [ repoRoot, fileFilter ]
             | None -> [])
            @ (argInputDirs repoRoot args
               |> List.map (fun dir -> dir, (fun (_: string) -> true))),
            filesUnder repoRoot,
            fun count ->
                largeListingWarning nameStr count
                |> Option.iter (FsHotWatch.Logging.warn "file-command")
        )

    // The digests the key hashes with: a listed file is read again only when it moved or the
    // event names it (`DigestCache`).
    let digests = DigestCache tryHashFile

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
                    inputListing.Observe(resolveChange repoRoot change)

                    match trigger.FilePattern with
                    | None -> return state
                    | Some fileFilter ->
                        match matchingFiles fileFilter change with
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

                    // Mid-run view: the accumulator is a strict PREFIX of the run,
                    // hence `isFinal = false` — see `FullSuiteClaim.derive`.
                    let claim = FullSuiteClaim.derive false (RunVerification.ofResults accumulated)

                    let! state' = tryFire ctx state progress.RunId accumulated claim

                    return
                        { state' with
                            RunAccumulator = Some(progress.RunId, accumulated) }

                | TestRunCompleted completed ->
                    // Always carries the full cumulative Results, so cache-hit replays
                    // (which skip TestProgress) still fire the command correctly. The
                    // only view that can license a `"true"` claim — and even here only
                    // if something actually executed.
                    let claim = FullSuiteClaim.derive true completed.Verification

                    return! tryFire ctx state completed.RunId completed.Results claim

                | _ -> return state
            }
      PrepareCommit = None
      Commands =
        [ $"%s{nameStr}-status",
          PluginCommand.Observe(fun _ctx state _args ->
              async {
                  let status =
                      match state.LastResult with
                      | Succeeded _ -> FileCommandReplies.CommandStatus.Passed
                      | CommandFailed _ -> FileCommandReplies.CommandStatus.Failed
                      | NeverRun -> FileCommandReplies.CommandStatus.NotRun

                  return FileCommandReplies.CommandStatus.serialize status
              })
          |> PluginCommand.typed FileCommandReplies.StatusSchema ]
      Subscriptions = CommandTrigger.subscriptions trigger
      CacheKey =
        // Pure-content cache key: merkle of (command, args, content of every
        // arg-file that exists on disk, content of every file the change matched).
        // No jj commit_id — two daemons on the
        // same inputs hash the same regardless of working-copy state. Recomputed
        // per event so mid-session edits to a referenced config invalidate it.
        //
        // File triggers are content-cacheable. afterTests triggers are deliberately
        // NOT: each run publishes new external evidence (Cobertura, and for a
        // filtered run a newer coverage.partial marker) which need not be named in
        // the command's args. Replaying a prior full run's cached RED skips the
        // command that would observe the newer partial marker, so the obsolete RED
        // survives a later check. Per-RunId state dedupe, not cross-run
        // task caching, provides the side-effect-at-most-once rule for afterTests.
        let cacheKey (event: PluginEvent<unit>) : ContentHash option =
            match event with
            | FileChanged change ->
                let change = resolveChange repoRoot change
                inputListing.Observe change

                let matched =
                    (trigger.FilePattern
                     |> Option.map (fun fileFilter -> matchingFiles fileFilter change)
                     |> Option.defaultValue [])
                    @ inputListing.Files()

                let named = matchingFiles (fun _ -> true) change |> Set.ofList

                computeFileChangedSaltWith
                    (fun path -> digests.Digest(path, named.Contains path))
                    repoRoot
                    command
                    args
                    matched
                |> Option.map ContentHash.create
            | _ -> None

        Some(fun _state event -> cacheKey event)
      Teardown = None }

/// Creates a framework plugin handler that runs a command in response to the configured trigger(s).
let create
    (name: PluginName)
    (trigger: CommandTrigger)
    (command: string)
    (args: string)
    (repoRoot: string)
    (timeoutSec: int option)
    : PluginHandler<FileCommandState, unit> =
    createWith runProcess Thread.Sleep name trigger command args repoRoot timeoutSec
