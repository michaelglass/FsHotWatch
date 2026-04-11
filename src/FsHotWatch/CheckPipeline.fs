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

/// Manages project options and performs incremental file checking with the warm FSharpChecker.
type CheckPipeline(checker: FSharpChecker, ?cacheBackend: ICheckCacheBackend, ?cacheKeyProvider: ICacheKeyProvider) =
    let keyProvider =
        defaultArg cacheKeyProvider (TimestampCacheKeyProvider() :> ICacheKeyProvider)

    let projectOptionsByFile = ConcurrentDictionary<string, FSharpProjectOptions list>()
    let projectOptionsByProject = ConcurrentDictionary<string, FSharpProjectOptions>()
    let projectOptionsHashCache = ConcurrentDictionary<string, string>()
    let fileTokens = ConcurrentDictionary<string, CancellationTokenSource>()
    let mutable nextVersion = 0L

    let makeCacheKeyFast (filePath: string) (options: FSharpProjectOptions) : CacheKey =
        let optionsHash =
            match projectOptionsHashCache.TryGetValue(options.ProjectFileName) with
            | true, hash -> hash
            | false, _ -> getProjectOptionsHash options

        { FileHash = ContentHash.create (keyProvider.GetFileHash(filePath))
          ProjectOptionsHash = ContentHash.create optionsHash }

    member _.NextVersion() = Interlocked.Increment(&nextVersion)

    /// Clear all registered projects, file mappings, and per-file cancellation tokens.
    member _.PrepareForRediscovery() =
        for kvp in fileTokens do
            cancelAndDispose kvp.Value

        fileTokens.Clear()
        projectOptionsByFile.Clear()
        projectOptionsByProject.Clear()
        projectOptionsHashCache.Clear()
        cacheBackend |> Option.iter (fun b -> b.Clear())

    /// Invalidate the cache entry for a file so the next CheckFile call re-runs FCS.
    member _.InvalidateFile(filePath: string) =
        let absPath = System.IO.Path.GetFullPath(filePath)

        match cacheBackend with
        | Some backend ->
            match projectOptionsByFile.TryGetValue(absPath) with
            | true, optionsList ->
                for options in optionsList do
                    let key = makeCacheKeyFast absPath options
                    backend.Invalidate(key)

                Logging.debug "check" $"Cache invalidated: %s{System.IO.Path.GetFileName(absPath)}"
            | _ -> ()
        | None -> ()

    /// Register project options for a project. Maps each source file to this project's options.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        projectOptionsByProject[projectPath] <- options
        projectOptionsHashCache[projectPath] <- getProjectOptionsHash options

        for sourceFile in options.SourceFiles do
            projectOptionsByFile.AddOrUpdate(
                sourceFile,
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

    /// Get project options by project path.
    member _.GetProjectOptions(projectPath: string) : FSharpProjectOptions option =
        match projectOptionsByProject.TryGetValue(projectPath) with
        | true, opts -> Some opts
        | false, _ -> None

    /// Get all registered project paths.
    member _.GetRegisteredProjects() : string list =
        projectOptionsByProject.Keys |> Seq.toList

    /// Get all registered source files across all projects.
    member _.GetAllRegisteredFiles() : string list = projectOptionsByFile.Keys |> Seq.toList

    /// Cancel any in-flight check for the given file and return a new CancellationTokenSource.
    /// If a caller token is provided, the returned CTS is linked to it so that daemon-level
    /// cancellation also cancels the per-file check.
    member _.CancelPreviousCheck(filePath: string, ?ct: CancellationToken) : CancellationTokenSource =
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

    /// Core check logic: check a file with explicit options.
    /// The ct token is checked before and after the expensive FCS call so that
    /// CancelPreviousCheck cancellations are observed even when the async CE's
    /// implicit token differs from the per-file token.
    member private this.CheckFileCore
        (absPath: string, options: FSharpProjectOptions, ct: CancellationToken)
        : Async<FileCheckResult option> =
        async {
            ct.ThrowIfCancellationRequested()

            let cacheKey =
                cacheBackend |> Option.map (fun _ -> makeCacheKeyFast absPath options)

            let cached =
                match cacheBackend, cacheKey with
                | Some backend, Some key -> backend.TryGet(key)
                | _ -> None

            match cached with
            | Some result when
                (match result.CheckResults with
                 | FullCheck _ -> true
                 | ParseOnly -> false)
                ->
                Logging.debug "check" $"Cache hit: %s{Path.GetFileName(absPath)}"
                return Some result
            | _ ->
                let source =
                    if File.Exists(absPath) then
                        File.ReadAllText(absPath)
                    else
                        ""

                let sourceText = SourceText.ofString source
                let version = this.NextVersion()

                try
                    ct.ThrowIfCancellationRequested()
                    let sw = System.Diagnostics.Stopwatch.StartNew()

                    let! parseResults, checkAnswer = checker.ParseAndCheckFileInProject(absPath, 0, sourceText, options)

                    sw.Stop()
                    ct.ThrowIfCancellationRequested()

                    if sw.Elapsed.TotalSeconds > 2.0 then
                        Logging.debug "check" $"SLOW: %s{Path.GetFileName(absPath)} took %.1f{sw.Elapsed.TotalSeconds}s"

                    match checkAnswer with
                    | FSharpCheckFileAnswer.Succeeded checkResults ->
                        let result =
                            { File = absPath
                              Source = source
                              ParseResults = parseResults
                              CheckResults = FullCheck checkResults
                              ProjectOptions = options
                              Version = version }

                        match cacheBackend, cacheKey with
                        | Some backend, Some key -> backend.Set key result
                        | _ -> ()

                        return Some result
                    | FSharpCheckFileAnswer.Aborted ->
                        return
                            Some
                                { File = absPath
                                  Source = source
                                  ParseResults = parseResults
                                  CheckResults = ParseOnly
                                  ProjectOptions = options
                                  Version = version }
                with ex ->
                    Logging.error "check" $"Failed to check %s{absPath}: %s{ex.Message}"
                    return None
        }

    /// Check a single file using the warm checker. Returns FileCheckResult if successful.
    /// Cancels any previous in-flight check for the same file before starting.
    /// For files in multiple projects, uses the first registered project's options.
    member this.CheckFile(filePath: string, ?ct: CancellationToken) : Async<FileCheckResult option> =
        async {
            let ct = defaultArg ct CancellationToken.None
            let absPath = Path.GetFullPath(filePath)
            let fileCts = this.CancelPreviousCheck(absPath, ct)
            let fileToken = fileCts.Token

            try
                fileToken.ThrowIfCancellationRequested()

                match projectOptionsByFile.TryGetValue(absPath) with
                | false, _ ->
                    Logging.debug "check" $"No project options for: %s{absPath}"
                    return None
                | true, (options :: _) -> return! this.CheckFileCore(absPath, options, fileToken)
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
        (filePath: string, options: FSharpProjectOptions, ?ct: CancellationToken)
        : Async<FileCheckResult option> =
        async {
            let ct = defaultArg ct CancellationToken.None
            let absPath = Path.GetFullPath(filePath)
            let fileCts = this.CancelPreviousCheck(absPath, ct)
            let fileToken = fileCts.Token

            try
                fileToken.ThrowIfCancellationRequested()
                return! this.CheckFileCore(absPath, options, fileToken)
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
                let results = System.Collections.Generic.Dictionary<string, FileCheckResult>()

                for sourceFile in options.SourceFiles do
                    let! result = this.CheckFile(sourceFile, ?ct = ct)

                    match result with
                    | Some r -> results[sourceFile] <- r
                    | None -> ()

                return
                    Some
                        { Project = projectPath
                          FileResults = results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq }
        }
