module FsHotWatch.CheckPipeline

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.CheckCache

let private cancelAndDispose (cts: CancellationTokenSource) =
    try
        cts.Cancel()
        cts.Dispose()
    with :? ObjectDisposedException ->
        ()

let private noopSink =
    { new PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()
        member _.Log _ = ()
        member _.SetSummary _ = () }

/// Pure cache-lookup decision: returns Some only when the cached entry is a
/// FullCheck. ParseOnly entries are treated as misses so the file is re-checked.
/// Exposed so cache-invalidation logic can be tested without disk or FCS.
let tryGetCachedFullCheck (backend: ICheckCacheBackend option) (key: CacheKey option) : FileCheckResult option =
    match backend, key with
    | Some b, Some k ->
        match b.TryGet k with
        | Some r ->
            match r.CheckResults with
            | FullCheck _ -> Some r
            | ParseOnly -> None
        | None -> None
    | _ -> None

/// The diagnostic messages a parse+check answer carries. `Aborted` carries none:
/// a check that did not finish said nothing about any type.
let internal answerMessages (answer: FSharpCheckFileAnswer) : string seq =
    match answer with
    | FSharpCheckFileAnswer.Succeeded r -> r.Diagnostics |> Seq.map (fun d -> d.Message)
    | FSharpCheckFileAnswer.Aborted -> Seq.empty

let private readSourceOrEmpty (absPath: string) : string =
    try
        File.ReadAllText(absPath)
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> ""

/// Manages project options and performs incremental file checking with the warm FSharpChecker.
type CheckPipeline
    (
        checker: FSharpChecker,
        ?cacheBackend: ICheckCacheBackend,
        ?cacheKeyProvider: ICacheKeyProvider,
        ?activity: PluginActivity.IActivitySink,
        ?repoRoot: string,
        ?recheckCooldown: TimeSpan
    ) =
    let activity = defaultArg activity noopSink

    /// See `FcsDiagnosticFilter.shouldRecheckProject`. Injectable so the
    /// cooldown can be collapsed in tests without waiting five minutes.
    let recheckCooldown =
        defaultArg recheckCooldown FcsDiagnosticFilter.defaultRecheckCooldown

    /// Bounds the cure for stale checker state to one project re-typecheck per
    /// project per cooldown.
    let recheckBudget = FcsDiagnosticFilter.RecheckBudget(recheckCooldown)


    /// Present, the project-options hash names in-repo paths relatively, so the hash
    /// is identical across two checkouts of one repository. See
    /// `CheckCache.getProjectOptionsHashRelativeTo`.
    let repoRoot = repoRoot

    let keyProvider =
        defaultArg cacheKeyProvider (TimestampCacheKeyProvider() :> ICacheKeyProvider)

    let projectOptionsByFile =
        ConcurrentDictionary<AbsFilePath, FSharpProjectOptions list>()

    let projectOptionsByProject = ConcurrentDictionary<string, FSharpProjectOptions>()
    let projectOptionsHashCache = ConcurrentDictionary<string, string>()
    let fileTokens = ConcurrentDictionary<AbsFilePath, CancellationTokenSource>()
    let upstreamFingerprints = UpstreamFingerprints(repoRoot)
    let mutable nextVersion = 0L
    let mutable lastFitWarning: string option = None

    let optionsHashOf (options: FSharpProjectOptions) =
        match projectOptionsHashCache.TryGetValue(options.ProjectFileName) with
        | true, hash -> hash
        | false, _ -> getProjectOptionsHashRelativeTo repoRoot options

    // GetFileHash returns None when the file is unreadable. That None propagates so
    // the cache lookup is bypassed and the next call (after the transient lock
    // clears) produces a fresh read instead of poisoning the cache with a
    // synthesized key.

    /// The lookup key: upstream fingerprint from this generation's per-project table.
    let makeCacheKeyFast (filePath: AbsFilePath) (options: FSharpProjectOptions) : CacheKey option =
        let path = AbsFilePath.value filePath
        let optionsHash = optionsHashOf options

        let upstream = upstreamFingerprints.For(path, options, optionsHash)

        makeCacheKeyWith keyProvider optionsHash upstream path

    /// The key as the disk stands NOW, bypassing the generation memo. A result is
    /// stored only under a key this still produces after the check, so a check that
    /// ran against an upstream edited mid-generation is never written.
    let makeCacheKeyFresh (filePath: AbsFilePath) (options: FSharpProjectOptions) : CacheKey option =
        let path = AbsFilePath.value filePath

        let upstream = upstreamFingerprints.Fresh(path, options)

        makeCacheKeyWith keyProvider (optionsHashOf options) upstream path

    /// Report the registered working set to a scoped backend.
    let ensureCacheCoversWorkingSet () =
        match cacheBackend with
        | Some(:? IScopedCheckCache as scoped) ->
            projectOptionsByProject
            |> Seq.map (fun kv -> kv.Value.ProjectFileName, kv.Value.SourceFiles.Length)
            |> Seq.toList
            |> scoped.ObserveWorkingSet
        | _ -> ()

    /// The backend to use for `options`' project: None when it does not admit it.
    let backendFor (options: FSharpProjectOptions) =
        match cacheBackend with
        | Some(:? IScopedCheckCache as scoped) when not (scoped.Admits options.ProjectFileName) -> None
        | other -> other

    member _.NextVersion() = Interlocked.Increment(&nextVersion)

    /// Start a generation — one scan or one change batch. Upstream fingerprints are
    /// then computed once per project for the generation instead of once per file;
    /// see `CheckCache.UpstreamFingerprints`. Call it after preprocessors have
    /// rewritten files and before dispatching the generation's checks.
    ///
    /// Also where a cache too small for its working set says so: projects register
    /// one at a time, so only once a generation starts is the count it names final.
    /// Logged once per distinct warning.
    member _.BeginGeneration() =
        upstreamFingerprints.BeginGeneration()

        match cacheBackend with
        | Some(:? IScopedCheckCache as scoped) ->
            match scoped.FitWarning with
            | Some warning when Some warning <> Volatile.Read(&lastFitWarning) ->
                Volatile.Write(&lastFitWarning, Some warning)
                Logging.warn "cache" warning
            | _ -> ()
        | _ -> ()

    /// The fit warning last logged, if any.
    member _.LastFitWarning = Volatile.Read(&lastFitWarning)

    /// Time spent computing upstream fingerprints since construction — the key's own
    /// cost, to weigh against the checks the cache saves. Excludes waits.
    member _.FingerprintTime = upstreamFingerprints.ComputeTime

    /// Per-project fingerprint tables built since construction.
    member _.FingerprintTablesBuilt = upstreamFingerprints.TablesBuilt

    /// Clear all registered projects, file mappings, and per-file cancellation tokens.
    ///
    /// `clearCheckCache` (default `true`) controls whether the check-result cache is
    /// also dropped. The project→options maps are always rebuilt — they're cheap and
    /// membership may have changed. Retaining the check-result cache is safe because it
    /// is keyed by `(file content hash, project-options hash)`: changed options get a
    /// new key (miss → recompute), unchanged ones keep their warm entry. Pass `false`
    /// from the scoped project-change path (which separately invalidates the affected
    /// project + its dependents); leave `true` for repo-wide re-discovery, where
    /// per-project staleness cannot be reasoned about.
    member _.PrepareForRediscovery(?clearCheckCache: bool) =
        let clearCheckCache = defaultArg clearCheckCache true

        for kvp in fileTokens do
            cancelAndDispose kvp.Value

        fileTokens.Clear()
        projectOptionsByFile.Clear()
        projectOptionsByProject.Clear()
        projectOptionsHashCache.Clear()

        if clearCheckCache then
            cacheBackend |> Option.iter (fun b -> b.Clear())

    /// Invalidate the cache entry for a file so the next CheckFile call re-runs FCS.
    member _.InvalidateFile(filePath: AbsFilePath) =
        match cacheBackend with
        | Some backend ->
            match projectOptionsByFile.TryGetValue(filePath) with
            | true, optionsList ->
                for options in optionsList do
                    match makeCacheKeyFast filePath options with
                    | Some key -> backend.Invalidate(key)
                    | None ->
                        // File unreadable; nothing to invalidate (no key was
                        // ever produced). The next CheckFile will re-try on
                        // disk read.
                        ()

                Logging.debug "check" $"Cache invalidated: %s{System.IO.Path.GetFileName(AbsFilePath.value filePath)}"
            | _ -> ()
        | None -> ()

    /// Register project options for a project. Maps each source file to this project's options.
    /// Filters out generated files in obj/ and bin/ directories that should not be checked.
    ///
    /// Re-registering a project REPLACES its compile-item set: a file the prior options
    /// listed and these do not stops mapping to this project, and stops being registered at
    /// all when no other project lists it. Without that, a file removed
    /// from the project survived in the per-file map with the project's OLD options, and
    /// every later check and scan of the registered set kept reaching for it.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        let filteredOptions =
            { options with
                SourceFiles =
                    options.SourceFiles
                    |> Array.filter (fun f -> not (PathFilter.isGeneratedPath f)) }

        let removedFiles =
            match projectOptionsByProject.TryGetValue(projectPath) with
            | true, prior -> Set.difference (Set.ofArray prior.SourceFiles) (Set.ofArray filteredOptions.SourceFiles)
            | false, _ -> Set.empty

        for removed in removedFiles do
            let key = AbsFilePath.create removed

            match projectOptionsByFile.TryGetValue(key) with
            | true, existing ->
                match
                    existing
                    |> List.filter (fun o -> o.ProjectFileName <> filteredOptions.ProjectFileName)
                with
                | [] -> projectOptionsByFile.TryRemove(key) |> ignore
                | remaining -> projectOptionsByFile[key] <- remaining
            | false, _ -> ()

        projectOptionsByProject[projectPath] <- filteredOptions
        projectOptionsHashCache[projectPath] <- getProjectOptionsHashRelativeTo repoRoot filteredOptions

        for sourceFile in filteredOptions.SourceFiles do
            projectOptionsByFile.AddOrUpdate(
                AbsFilePath.create sourceFile,
                [ filteredOptions ],
                fun _ existing ->
                    if
                        existing
                        |> List.exists (fun o -> o.ProjectFileName = filteredOptions.ProjectFileName)
                    then
                        existing
                        |> List.map (fun o ->
                            if o.ProjectFileName = filteredOptions.ProjectFileName then
                                filteredOptions
                            else
                                o)
                    else
                        filteredOptions :: existing
            )
            |> ignore

        ensureCacheCoversWorkingSet ()

    /// Get project options by project path.
    member _.GetProjectOptions(projectPath: string) : FSharpProjectOptions option =
        match projectOptionsByProject.TryGetValue(projectPath) with
        | true, opts -> Some opts
        | false, _ -> None

    /// Get all registered project paths.
    member _.GetRegisteredProjects() : string list =
        projectOptionsByProject.Keys |> Seq.toList

    /// Get all registered source files across all projects.
    member _.GetAllRegisteredFiles() : AbsFilePath list = projectOptionsByFile.Keys |> Seq.toList

    /// Cancel any in-flight check for the given file and return a new CancellationTokenSource.
    /// If a caller token is provided, the returned CTS is linked to it so that daemon-level
    /// cancellation also cancels the per-file check.
    ///
    /// Required for correctness, not a hot-path optimization: the scan and change-batch
    /// supervisors in Daemon.fs can issue concurrent CheckFile calls for the same file.
    /// Without cancellation a slow scan-side check can emit a stale FileChecked AFTER
    /// the batch-side check emitted the fresh one, and plugins would observe
    /// newer-then-older ordering and re-publish stale errors.
    member _.CancelPreviousCheck(filePath: AbsFilePath, ?ct: CancellationToken) : CancellationTokenSource =
        let ct = defaultArg ct CancellationToken.None

        let newCts =
            if ct = CancellationToken.None then
                new CancellationTokenSource()
            else
                CancellationTokenSource.CreateLinkedTokenSource(ct)

        fileTokens.AddOrUpdate(
            filePath,
            newCts,
            fun _ existing ->
                cancelAndDispose existing
                newCts
        )
        |> ignore

        newCts

    /// FCS-only check: takes already-read source text, performs parse+check,
    /// returns the result. Pure of disk I/O and cache concerns so it can be
    /// composed by callers that manage caching separately.
    /// The ct token is checked before and after the expensive FCS call so that
    /// CancelPreviousCheck cancellations are observed even when the async CE's
    /// implicit token differs from the per-file token.
    member private this.CheckFileCore
        (absPath: string, source: string, options: FSharpProjectOptions, ct: CancellationToken)
        : Async<FileCheckResult option> =
        async {
            ct.ThrowIfCancellationRequested()
            let sourceText = SourceText.ofString source
            let version = this.NextVersion()

            try
                ct.ThrowIfCancellationRequested()
                let sw = System.Diagnostics.Stopwatch.StartNew()

                let! firstParse, firstAnswer = checker.ParseAndCheckFileInProject(absPath, 0, sourceText, options)

                // A diagnostic that declares a type incompatible with ITSELF is not
                // code feedback — the compiler renders two types so they can be told
                // apart, so an identical render means it found no difference to tell.
                // What produces it is not known (see `FcsDiagnosticFilter`), so
                // dropping this project's checker state and asking again is a guess
                // at the class of thing that might clear it: cheap, bounded to ONCE
                // per project per cooldown so a pathological tree cannot turn every
                // file into a project re-typecheck, and never trusted to have worked
                // — a survivor is reported as our fault, not swallowed.
                let project = options.ProjectFileName

                // Asked on every check rather than behind the staleness test: it
                // is a dictionary probe and a subtraction, and asking it
                // unconditionally keeps the budget rule on the path every check
                // takes instead of only the one nothing can provoke on demand.
                let budgetAllows = recheckBudget.Allows(project, DateTime.UtcNow)

                let retryLog =
                    FcsDiagnosticFilter.recheckLogLine (Path.GetFileName project) (Path.GetFileName absPath)

                let onRecheck () =
                    recheckBudget.Spend(project, DateTime.UtcNow)
                    Logging.warn "check" retryLog
                    checker.InvalidateConfiguration(options)

                let! parseResults, checkAnswer =
                    FcsDiagnosticFilter.recheckIfSelfIncompatible
                        (snd >> answerMessages)
                        FcsDiagnosticFilter.isSelfIncompatibleTypeMessage
                        budgetAllows
                        onRecheck
                        (fun () -> checker.ParseAndCheckFileInProject(absPath, 0, sourceText, options))
                        (firstParse, firstAnswer)

                sw.Stop()
                ct.ThrowIfCancellationRequested()

                if sw.Elapsed.TotalSeconds > 2.0 then
                    Logging.debug "check" $"SLOW: %s{Path.GetFileName(absPath)} took %.1f{sw.Elapsed.TotalSeconds}s"

                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded checkResults ->
                    activity.Log($"checked {Path.GetFileName absPath}")

                    return
                        Some
                            { File = AbsFilePath.create absPath
                              Source = source
                              ParseResults = parseResults
                              CheckResults = FullCheck checkResults
                              ProjectOptions = options
                              Version = version
                              ModelGeneration = None }
                | FSharpCheckFileAnswer.Aborted ->
                    return
                        Some
                            { File = AbsFilePath.create absPath
                              Source = source
                              ParseResults = parseResults
                              CheckResults = ParseOnly
                              ProjectOptions = options
                              Version = version
                              ModelGeneration = None }
            with ex ->
                Logging.error "check" $"Failed to check %s{absPath}: %s{ex.Message}"
                return None
        }

    /// Cache-aware wrapper: looks up the cache, on miss reads disk and calls
    /// CheckFileCore, then stores FullCheck results back into the cache.
    member private this.CheckFileCached
        (absPath: string, options: FSharpProjectOptions, ct: CancellationToken)
        : Async<FileCheckResult option> =
        async {
            ct.ThrowIfCancellationRequested()

            let cacheBackend = backendFor options

            let cacheKey =
                cacheBackend
                |> Option.bind (fun _ -> makeCacheKeyFast (AbsFilePath.create absPath) options)

            match tryGetCachedFullCheck cacheBackend cacheKey with
            | Some cached ->
                Logging.debug "check" $"Cache hit: %s{Path.GetFileName(absPath)}"
                return Some cached
            | None ->
                let source = readSourceOrEmpty absPath
                let! result = this.CheckFileCore(absPath, source, options, ct)

                match result, cacheBackend, cacheKey with
                | Some r, Some backend, Some key ->
                    match r.CheckResults with
                    | FullCheck _ when makeCacheKeyFresh (AbsFilePath.create absPath) options = Some key ->
                        backend.Set key r
                    | FullCheck _ ->
                        Logging.debug
                            "check"
                            $"Not caching %s{Path.GetFileName absPath}: its inputs moved during the check"
                    | ParseOnly -> ()
                | _ -> ()

                return result
        }

    /// Check a single file using the warm checker. Returns FileCheckResult if successful.
    /// Cancels any previous in-flight check for the same file before starting.
    /// For files in multiple projects, uses the first registered project's options.
    member this.CheckFile(filePath: AbsFilePath, ?ct: CancellationToken) : Async<FileCheckResult option> =
        async {
            let ct = defaultArg ct CancellationToken.None
            let absPath = AbsFilePath.value filePath
            let fileCts = this.CancelPreviousCheck(filePath, ct)
            let fileToken = fileCts.Token

            try
                fileToken.ThrowIfCancellationRequested()

                match projectOptionsByFile.TryGetValue(filePath) with
                | false, _ ->
                    Logging.debug "check" $"No project options for: %s{absPath}"
                    return None
                | true, (options :: _) -> return! this.CheckFileCached(absPath, options, fileToken)
                | true, [] ->
                    Logging.debug "check" $"No project options for: %s{absPath}"
                    return None
            with :? OperationCanceledException ->
                Logging.debug "check" $"Cancelled: %s{Path.GetFileName(absPath)}"
                return None
        }

    /// Check a file with explicit project options.
    /// Use this for shared files that need checking in multiple project contexts.
    member this.CheckFileWithOptions
        (filePath: AbsFilePath, options: FSharpProjectOptions, ?ct: CancellationToken)
        : Async<FileCheckResult option> =
        async {
            let ct = defaultArg ct CancellationToken.None
            let absPath = AbsFilePath.value filePath
            let fileCts = this.CancelPreviousCheck(filePath, ct)
            let fileToken = fileCts.Token

            try
                fileToken.ThrowIfCancellationRequested()
                return! this.CheckFileCached(absPath, options, fileToken)
            with :? OperationCanceledException ->
                Logging.debug "check" $"Cancelled: %s{Path.GetFileName(absPath)}"
                return None
        }

    /// Check all registered files for a project. Returns results keyed by file path.
    member this.CheckProject(projectPath: string, ?ct: CancellationToken) : Async<ProjectCheckResult option> =
        async {
            match projectOptionsByProject.TryGetValue(projectPath) with
            | false, _ -> return None
            | true, options ->
                let projectKey = Path.GetFileName projectPath
                activity.StartSubtask(projectKey, $"checking {projectKey}")
                let results = System.Collections.Generic.Dictionary<string, FileCheckResult>()

                try
                    for sourceFile in options.SourceFiles do
                        let! result = this.CheckFile(AbsFilePath.create sourceFile, ?ct = ct)

                        match result with
                        | Some r -> results[sourceFile] <- r
                        | None -> ()
                finally
                    activity.EndSubtask(projectKey)

                activity.SetSummary($"checked {results.Count} files in {projectKey}")

                return
                    Some
                        { Project = projectPath
                          FileResults = results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq }
        }
