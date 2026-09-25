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

/// Pure cache-lookup decision: returns Some only when the cached entry is a FullCheck
/// whose recorded project outputs all still hold the bytes it was typed against
/// (`outputsHold`). ParseOnly entries are misses so the file is re-checked. Exposed so
/// cache-invalidation logic can be tested without disk or FCS.
let tryGetCachedFullCheck
    (outputsHold: (string * string) list -> bool)
    (backend: ICheckCacheBackend option)
    (key: CacheKey option)
    : FileCheckResult option =
    match backend, key with
    | Some b, Some k ->
        match b.TryGet k with
        | Some { Result = { CheckResults = FullCheck _ } as r
                 ProjectOutputs = outputs } when outputsHold outputs -> Some r
        | Some _
        | None -> None
    | _ -> None

/// The diagnostic messages a parse+check answer carries. `Aborted` carries none:
/// a check that did not finish said nothing about any type.
let internal answerMessages (answer: FSharpCheckFileAnswer) : string seq =
    match answer with
    | FSharpCheckFileAnswer.Succeeded r -> r.Diagnostics |> Seq.map (fun d -> d.Message)
    | FSharpCheckFileAnswer.Aborted -> Seq.empty

/// The line logged when a file's FCS check begins, so the log shows whether a slow
/// result was slow to start or slow to finish.
let internal checkStartLine (fileName: string) : string = $"check start %s{fileName}"

/// The line logged when a file's FCS check succeeds: its total time, split between
/// building the project snapshot and the checker's parse and type-check.
let internal checkedLine (fileName: string) (total: TimeSpan) (snapshot: TimeSpan) (fcs: TimeSpan) : string =
    let ms (span: TimeSpan) = int64 span.TotalMilliseconds
    $"checked %s{fileName} in %d{ms total}ms (snapshot %d{ms snapshot}ms, fcs %d{ms fcs}ms)"

/// Manages project options and performs incremental file checking with the warm FSharpChecker.
type CheckPipeline
    (
        checker: FSharpChecker,
        ?cacheBackend: ICheckCacheBackend,
        ?cacheKeyProvider: ICacheKeyProvider,
        ?activity: PluginActivity.IActivitySink,
        ?repoRoot: string,
        ?recheckCooldown: TimeSpan,
        ?frames: PathFrame.FrameChoice
    ) =
    let activity = defaultArg activity noopSink
    // Which frame each project is checked under: its own paths unless a repository host
    // checks its worktrees under one virtual root.
    let frames = defaultArg frames PathFrame.realPaths

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

    /// Content hashes shared by the upstream fingerprints and the snapshot versions,
    /// so a file is read and hashed once per change for both.
    let hashFile = upstreamFingerprints.HashFile
    let mutable nextVersion = 0L

    /// A check canceled outside CheckFileCore's guarded body: logged at debug level,
    /// and the check returns no result. Cancellation is observed through
    /// `IsCancellationRequested` rather than thrown, so a superseded check costs no
    /// exception unwind.
    let logCanceled (absPath: string) =
        Logging.debug "check" $"Cancelled: %s{Path.GetFileName absPath}"

    /// A check canceled inside CheckFileCore's guarded body is reported like any other
    /// failure there: an error naming the file and the cancellation.
    let logCanceledAsFailure (absPath: string) (ct: CancellationToken) =
        Logging.error "check" $"Failed to check %s{absPath}: %s{OperationCanceledException(ct).Message}"

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

    /// A project output as an entry records it: repository-relative, so a sibling
    /// worktree's identical output verifies it too.
    let outputName (path: string) = relativizeOption repoRoot path

    /// The path an entry's recorded output names in this worktree.
    let outputPath (name: string) =
        repoRoot
        |> Option.fold (fun (path: string) root -> path.Replace(RepoRootPlaceholder, Path.GetFullPath root)) name

    /// Whether every output an entry recorded still holds the bytes it was typed against.
    let outputsHold (outputs: (string * string) list) =
        outputs |> List.forall (fun (name, hash) -> hashFile (outputPath name) = hash)

    /// Report the registered working set to a scoped backend.
    let ensureCacheCoversWorkingSet () =
        match cacheBackend with
        | Some(:? IScopedCheckCache as scoped) ->
            projectOptionsByProject
            |> Seq.map (fun kv ->
                kv.Value.ProjectFileName,
                kv.Value.SourceFiles
                |> Array.filter (not << PathFilter.isGeneratedPath)
                |> Array.length)
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

    /// Register project options for a project. Maps each of its checkable source files
    /// to these options; the generated files in obj/ and bin/ are compiled but never
    /// checked on their own, so they are not mapped.
    ///
    /// The options themselves are kept whole. The checker sees a project with the same
    /// source files whether one of its own files is being checked or a project
    /// downstream of it is (which reaches it through `ReferencedProjects`, unfiltered).
    /// Two source lists for one project would be two versions of every type-check the
    /// checker caches for it, and computing either demotes the other.
    ///
    /// Re-registering a project REPLACES its compile-item set: a file the prior options
    /// listed and these do not stops mapping to this project, and stops being registered at
    /// all when no other project lists it. Without that, a file removed
    /// from the project survived in the per-file map with the project's OLD options, and
    /// every later check and scan of the registered set kept reaching for it.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        let checkable (o: FSharpProjectOptions) =
            o.SourceFiles
            |> Array.filter (fun f -> not (PathFilter.isGeneratedPath f))
            |> Set.ofArray

        let removedFiles =
            match projectOptionsByProject.TryGetValue(projectPath) with
            | true, prior -> Set.difference (checkable prior) (checkable options)
            | false, _ -> Set.empty

        for removed in removedFiles do
            let key = AbsFilePath.create removed

            match projectOptionsByFile.TryGetValue(key) with
            | true, existing ->
                match existing |> List.filter (fun o -> o.ProjectFileName <> options.ProjectFileName) with
                | [] -> projectOptionsByFile.TryRemove(key) |> ignore
                | remaining -> projectOptionsByFile[key] <- remaining
            | false, _ -> ()

        projectOptionsByProject[projectPath] <- options
        projectOptionsHashCache[projectPath] <- getProjectOptionsHashRelativeTo repoRoot options

        for sourceFile in checkable options do
            projectOptionsByFile.AddOrUpdate(
                AbsFilePath.create sourceFile,
                [ options ],
                fun _ existing ->
                    if existing |> List.exists (fun o -> o.ProjectFileName = options.ProjectFileName) then
                        existing
                        |> List.map (fun o ->
                            if o.ProjectFileName = options.ProjectFileName then
                                options
                            else
                                o)
                    else
                        options :: existing
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
        (openFile: ProjectSnapshots.OpenFile, options: FSharpProjectOptions, ct: CancellationToken)
        : Async<(FileCheckResult * (string * string) list) option> =
        async {
            if ct.IsCancellationRequested then
                logCanceled openFile.Path
                return None
            else
                let absPath = openFile.Path
                let source = openFile.Text
                let version = this.NextVersion()

                try
                    if ct.IsCancellationRequested then
                        logCanceledAsFailure absPath ct
                        return None
                    else
                        let fileName = Path.GetFileName absPath
                        activity.Log(checkStartLine fileName)
                        let sw = System.Diagnostics.Stopwatch.StartNew()

                        // Built per check, so a check after `ProjectSnapshots.invalidate` is in the
                        // project's new generation. A generation never changes the frame.
                        let framedNow () =
                            ProjectSnapshots.buildFramed
                                (ProjectSnapshots.generationOf checker)
                                hashFile
                                repoRoot
                                frames
                                openFile
                                options

                        let framed = framedNow ()
                        let snapshotTime = sw.Elapsed

                        // What FCS may type against beyond the snapshot's versions, read before it does.
                        let outputs =
                            framed.RealProjectOutputs |> List.map (fun output -> output, hashFile output)

                        // The name FCS knows the file by: under the virtual root when its project is.
                        let checkedPath =
                            match framed.Frame with
                            | Some frame ->
                                ProjectSnapshots.recordFrame
                                    checker
                                    options.ProjectFileName
                                    (PathFrame.toVirtual frame options.ProjectFileName)

                                PathFrame.toVirtual frame absPath
                            | None -> absPath

                        let! firstParse, firstAnswer =
                            ProjectSnapshots.parseAndCheck checker checkedPath framed.Snapshot

                        // A diagnostic that declares a type incompatible with ITSELF is not
                        // code feedback — the compiler renders two types so they can be told
                        // apart, so an identical render means it found no difference to tell.
                        // What produces it is not known (see `FcsDiagnosticFilter`), so
                        // asking again in a new generation of this project's checker state
                        // (`ProjectSnapshots.invalidateShared`, since a suspect entry under a
                        // virtual root is every sharing session's) is a guess at the class of thing
                        // that might clear it: cheap, bounded to ONCE
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
                            ProjectSnapshots.invalidateShared checker options

                        let! parseResults, checkAnswer =
                            FcsDiagnosticFilter.recheckIfSelfIncompatible
                                (snd >> answerMessages)
                                FcsDiagnosticFilter.isSelfIncompatibleTypeMessage
                                budgetAllows
                                onRecheck
                                (fun () -> ProjectSnapshots.parseAndCheck checker checkedPath (framedNow ()).Snapshot)
                                (firstParse, firstAnswer)

                        sw.Stop()

                        if ct.IsCancellationRequested then
                            logCanceledAsFailure absPath ct
                            return None
                        else

                            if sw.Elapsed.TotalSeconds > 2.0 then
                                Logging.debug "check" $"SLOW: %s{fileName} took %.1f{sw.Elapsed.TotalSeconds}s"

                            match checkAnswer with
                            | FSharpCheckFileAnswer.Succeeded checkResults ->
                                activity.Log(checkedLine fileName sw.Elapsed snapshotTime (sw.Elapsed - snapshotTime))

                                return
                                    Some(
                                        { File = AbsFilePath.create absPath
                                          Source = source
                                          ParseResults = parseResults
                                          CheckResults = FullCheck checkResults
                                          ProjectOptions = options
                                          Version = version
                                          ModelGeneration = None
                                          Frame = framed.Frame },
                                        outputs
                                    )
                            | FSharpCheckFileAnswer.Aborted ->
                                return
                                    Some(
                                        { File = AbsFilePath.create absPath
                                          Source = source
                                          ParseResults = parseResults
                                          CheckResults = ParseOnly
                                          ProjectOptions = options
                                          Version = version
                                          ModelGeneration = None
                                          Frame = framed.Frame },
                                        outputs
                                    )
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
            if ct.IsCancellationRequested then
                logCanceled absPath
                return None
            else

                let cacheBackend = backendFor options

                let cacheKey =
                    cacheBackend
                    |> Option.bind (fun _ -> makeCacheKeyFast (AbsFilePath.create absPath) options)

                match tryGetCachedFullCheck outputsHold cacheBackend cacheKey with
                | Some cached ->
                    Logging.debug "check" $"Cache hit: %s{Path.GetFileName(absPath)}"
                    return Some cached
                | None ->
                    let openFile = ProjectSnapshots.readOpenFile hashFile absPath
                    let! result = this.CheckFileCore(openFile, options, ct)

                    match result, cacheBackend, cacheKey with
                    | Some(r, outputs), Some backend, Some key ->
                        let inputsHeld =
                            makeCacheKeyFresh (AbsFilePath.create absPath) options = Some key
                            && outputs |> List.forall (fun (output, hash) -> hashFile output = hash)

                        match r.CheckResults with
                        | FullCheck _ when inputsHeld ->
                            backend.Set
                                key
                                { Result = r
                                  ProjectOutputs = [ for output, hash in outputs -> outputName output, hash ] }
                        | FullCheck _ ->
                            Logging.debug
                                "check"
                                $"Not caching %s{Path.GetFileName absPath}: its inputs moved during the check"
                        | ParseOnly -> ()
                    | _ -> ()

                    return result |> Option.map fst
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
                if fileToken.IsCancellationRequested then
                    logCanceled absPath
                    return None
                else

                    match projectOptionsByFile.TryGetValue(filePath) with
                    | false, _ ->
                        Logging.debug "check" $"No project options for: %s{absPath}"
                        return None
                    | true, (options :: _) -> return! this.CheckFileCached(absPath, options, fileToken)
                    | true, [] ->
                        Logging.debug "check" $"No project options for: %s{absPath}"
                        return None
            with :? OperationCanceledException ->
                logCanceled absPath
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
                if fileToken.IsCancellationRequested then
                    logCanceled absPath
                    return None
                else
                    return! this.CheckFileCached(absPath, options, fileToken)
            with :? OperationCanceledException ->
                logCanceled absPath
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
                    for sourceFile in options.SourceFiles |> Array.filter (not << PathFilter.isGeneratedPath) do
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
