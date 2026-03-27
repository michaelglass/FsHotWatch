module FsHotWatch.CheckPipeline

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.Events
open FsHotWatch.Logging

/// Manages project options and performs incremental file checking with the warm FSharpChecker.
type CheckPipeline(checker: FSharpChecker) =
    let projectOptionsByFile = ConcurrentDictionary<string, FSharpProjectOptions>()
    let projectOptionsByProject = ConcurrentDictionary<string, FSharpProjectOptions>()
    let fileTokens = ConcurrentDictionary<string, CancellationTokenSource>()

    /// Clear all registered projects and file mappings. Call before re-discovery
    /// to ensure deleted projects and removed files don't leave stale options.
    /// Also cancels all outstanding per-file cancellation tokens.
    member _.PrepareForRediscovery() =
        for kvp in fileTokens do
            try
                kvp.Value.Cancel()
                kvp.Value.Dispose()
            with :? ObjectDisposedException ->
                ()

        fileTokens.Clear()
        projectOptionsByFile.Clear()
        projectOptionsByProject.Clear()

    /// Register project options for a project. Maps each source file to this project's options.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        projectOptionsByProject[projectPath] <- options

        for sourceFile in options.SourceFiles do
            projectOptionsByFile[sourceFile] <- options

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
                try
                    existing.Cancel()
                    existing.Dispose()
                with :? ObjectDisposedException ->
                    ()

                newCts
        )
        |> ignore

        newCts

    /// Check a single file using the warm checker. Returns FileCheckResult if successful.
    /// Cancels any previous in-flight check for the same file before starting.
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
                | true, options ->
                    let source =
                        if File.Exists(absPath) then
                            File.ReadAllText(absPath)
                        else
                            ""

                    let sourceText = SourceText.ofString source

                    try
                        let sw = System.Diagnostics.Stopwatch.StartNew()

                        let! parseResults, checkAnswer =
                            checker.ParseAndCheckFileInProject(absPath, 0, sourceText, options)

                        sw.Stop()

                        if sw.Elapsed.TotalSeconds > 2.0 then
                            Logging.debug
                                "check"
                                $"SLOW: %s{Path.GetFileName(absPath)} took %.1f{sw.Elapsed.TotalSeconds}s"

                        match checkAnswer with
                        | FSharpCheckFileAnswer.Succeeded checkResults ->
                            return
                                Some
                                    { File = absPath
                                      Source = source
                                      ParseResults = parseResults
                                      CheckResults = checkResults
                                      ProjectOptions = options }
                        | FSharpCheckFileAnswer.Aborted ->
                            // Still emit with parse results — lint can use the AST even without type info
                            return
                                Some
                                    { File = absPath
                                      Source = source
                                      ParseResults = parseResults
                                      CheckResults = Unchecked.defaultof<_>
                                      ProjectOptions = options }
                    with ex ->
                        Logging.error "check" $"Failed to check %s{absPath}: %s{ex.Message}"
                        return None
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
                let mutable fileResults = Map.empty

                for sourceFile in options.SourceFiles do
                    let! result = this.CheckFile(sourceFile, ?ct = ct)

                    match result with
                    | Some r -> fileResults <- fileResults |> Map.add sourceFile r
                    | None -> ()

                return
                    Some
                        { Project = projectPath
                          FileResults = fileResults }
        }
