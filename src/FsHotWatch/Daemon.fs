module FsHotWatch.Daemon

open System
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open FsHotWatch.CheckPipeline
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Ipc
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.ProjectGraph
open FsHotWatch.Watcher

/// Extract FCS diagnostics from check results and report to the error ledger.
let private reportFcsDiagnostics (host: PluginHost) (checkResult: Events.FileCheckResult) =
    if not (isNull (box checkResult.CheckResults)) then
        let diagnostics =
            checkResult.CheckResults.Diagnostics
            |> Array.choose (fun d ->
                match d.Severity with
                | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error ->
                    Some
                        { Message = d.Message
                          Severity = "error"
                          Line = d.StartLine
                          Column = d.StartColumn }
                | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning ->
                    Some
                        { Message = d.Message
                          Severity = "warning"
                          Line = d.StartLine
                          Column = d.StartColumn }
                | _ -> None)
            |> Array.toList

        if diagnostics.IsEmpty then
            host.ClearErrors("fcs", checkResult.File)
        else
            host.ReportErrors("fcs", checkResult.File, diagnostics)

/// Discover .fsproj files and register them with the graph and pipeline.
/// Uses Ionide.ProjInfo for MSBuild design-time evaluation to get real
/// assembly references, NuGet packages, and compiler flags.
let private discoverAndRegisterProjects
    (repoRoot: string)
    (loader: IWorkspaceLoader)
    (graph: ProjectGraph)
    (pipeline: CheckPipeline)
    =
    async {
        let searchDirs =
            [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]
            |> List.filter Directory.Exists

        let fsprojFiles =
            searchDirs
            |> List.collect (fun dir ->
                Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories) |> Array.toList)
            |> List.filter (fun f ->
                let n = f.Replace('\\', '/')
                not (n.Contains("/obj/")) && not (n.Contains("/bin/")))

        graph.PrepareForRediscovery()
        pipeline.PrepareForRediscovery()

        let sw = System.Diagnostics.Stopwatch.StartNew()
        eprintfn "  [discover] Loading project options via MSBuild evaluation..."

        try
            let loaded = loader.LoadProjects(fsprojFiles) |> Seq.toList

            // Register projects in the graph using Ionide-derived data (not XML parse)
            // so source file lists match what FCS sees (handles globs, conditionals, generated files)
            for proj in loaded do
                let absProject = Path.GetFullPath(proj.ProjectFileName)
                let sourceFiles = proj.SourceFiles |> List.map Path.GetFullPath

                let references =
                    proj.ReferencedProjects
                    |> List.map (fun r -> Path.GetFullPath(r.ProjectFileName))

                graph.RegisterProject(absProject, sourceFiles, references)

            let fcsOptionsList = Ionide.ProjInfo.FCS.mapManyOptions loaded |> Seq.toList
            sw.Stop()

            eprintfn
                "  [discover] MSBuild evaluation complete: %d projects in %.1fs"
                fcsOptionsList.Length
                sw.Elapsed.TotalSeconds

            for fcsOptions in fcsOptionsList do
                try
                    let absProject = Path.GetFullPath(fcsOptions.ProjectFileName)
                    pipeline.RegisterProject(absProject, fcsOptions)

                    eprintfn
                        "  [discover] Registered %s (%d files, %d opts)"
                        (Path.GetFileName fcsOptions.ProjectFileName)
                        fcsOptions.SourceFiles.Length
                        fcsOptions.OtherOptions.Length
                with ex ->
                    eprintfn
                        "  [discover] Failed to register %s: %s"
                        (Path.GetFileName fcsOptions.ProjectFileName)
                        ex.Message
        with ex ->
            sw.Stop()
            eprintfn "  [discover] MSBuild evaluation failed (%.1fs): %s" sw.Elapsed.TotalSeconds ex.Message
    }

/// The daemon ties together a warm FSharpChecker, file watcher, check pipeline, and plugin host.
/// It runs until the provided CancellationToken is cancelled.
[<NoComparison; NoEquality>]
type Daemon =
    {
        /// The plugin host that manages plugin lifecycle and event dispatch.
        Host: PluginHost
        /// The file watcher monitoring the repository for changes.
        Watcher: FileWatcher
        /// The warm FSharpChecker instance used for incremental checking.
        Checker: FSharpChecker
        /// The check pipeline that performs incremental file checking.
        Pipeline: CheckPipeline
        /// The project dependency graph.
        Graph: ProjectGraph
        /// The repository root directory.
        RepoRoot: string
        /// Cached MSBuild workspace loader (created once at startup to avoid
        /// accumulating MSBuild BuildManager instances on each re-discovery).
        WorkspaceLoader: IWorkspaceLoader
        /// Current scan progress state.
        mutable ScanState: ScanState
        /// Semaphore ensuring only one scan runs at a time.
        ScanSemaphore: SemaphoreSlim
        /// Disposes the debounce timer used for coalescing file change events.
        DisposeDebounceTimer: unit -> unit
        /// Signalled when the daemon is ready to accept file change events.
        Ready: ManualResetEventSlim
    }

    /// Register a plugin with the daemon's plugin host.
    member this.Register(plugin: IFsHotWatchPlugin) = this.Host.Register(plugin)

    /// Register a preprocessor (e.g., formatter) that runs before events are dispatched.
    member this.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) =
        this.Host.RegisterPreprocessor(preprocessor)

    /// Register a project's options so its files can be checked incrementally.
    member this.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        this.Pipeline.RegisterProject(projectPath, options)

    /// Get current scan state.
    member this.GetScanState() = this.ScanState

    /// Scan all registered files — check each one and emit events to plugins.
    /// Blocks until complete. If a scan is already running, waits for it to finish.
    member this.ScanAll() =
        async {
            let! ct = Async.CancellationToken
            do! this.ScanSemaphore.WaitAsync(ct) |> Async.AwaitTask

            try
                let registeredProjects = this.Pipeline.GetRegisteredProjects()
                let files = this.Pipeline.GetAllRegisteredFiles()
                let total = files.Length
                eprintfn "  [scan] %d projects, %d files registered" registeredProjects.Length total
                let sw = System.Diagnostics.Stopwatch.StartNew()
                this.ScanState <- Scanning(total, 0, System.DateTime.UtcNow)

                if not files.IsEmpty then
                    // Run preprocessors (e.g., formatter) before dispatching
                    let _modified = this.Host.RunPreprocessors(files)
                    this.Host.EmitFileChanged(SourceChanged files)
                    let mutable completed = 0

                    let mutable checkedCount = 0
                    let mutable skippedCount = 0

                    for file in files do
                        let! result = this.Pipeline.CheckFile(file)

                        match result with
                        | Some checkResult ->
                            checkedCount <- checkedCount + 1
                            this.Host.EmitFileChecked(checkResult)
                            reportFcsDiagnostics this.Host checkResult

                        | None -> skippedCount <- skippedCount + 1

                        completed <- completed + 1
                        this.ScanState <- Scanning(total, completed, System.DateTime.UtcNow)

                    eprintfn "  [scan] Checked %d files, skipped %d" checkedCount skippedCount

                sw.Stop()
                this.ScanState <- ScanComplete(total, sw.Elapsed)
            finally
                this.ScanSemaphore.Release() |> ignore
        }

    /// Run the daemon until cancellation is requested.
    member this.Run(cancellationToken: CancellationToken) =
        async {
            use _ = this.Watcher :> System.IDisposable
            this.Ready.Set()

            try
                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg = cancellationToken.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask
            finally
                this.DisposeDebounceTimer()
                this.Ready.Dispose()
        }

    /// Discover .fsproj files in src/ and tests/ and register them with the pipeline.
    member this.DiscoverAndRegisterProjects() =
        discoverAndRegisterProjects this.RepoRoot this.WorkspaceLoader this.Graph this.Pipeline

    /// Format scan state as a human-readable string.
    member this.FormatScanStatus() =
        match this.ScanState with
        | ScanIdle -> "idle"
        | Scanning(total, completed, _) ->
            let pct = if total > 0 then completed * 100 / total else 0
            $"scanning: %d{completed}/%d{total} files (%d{pct}%%)"
        | ScanComplete(total, elapsed) -> $"complete: %d{total} files checked in %.1f{elapsed.TotalSeconds}s"

    /// Run the daemon with IPC server on the given pipe name.
    /// Discovers projects, performs initial scan, then watches for changes.
    member this.RunWithIpc(pipeName: string, cts: CancellationTokenSource) =
        async {
            use _ = this.Watcher :> System.IDisposable

            try
                let onScan () =
                    Async.StartAsTask(this.ScanAll()) |> ignore

                let triggerBuild () =
                    async {
                        let files = this.Pipeline.GetAllRegisteredFiles()

                        if not files.IsEmpty then
                            this.Host.EmitFileChanged(SourceChanged files)
                    }

                let formatAll () =
                    async {
                        let files = this.Pipeline.GetAllRegisteredFiles()
                        let modified = this.Host.RunPreprocessors(files)
                        return $"formatted %d{modified.Length} files"
                    }

                let ipcTask =
                    Async.StartAsTask(
                        IpcServer.start pipeName this.Host cts onScan this.FormatScanStatus triggerBuild formatAll
                    )

                this.Ready.Set()

                // Discover projects and perform initial full scan
                do! this.DiscoverAndRegisterProjects()
                do! this.ScanAll()

                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask

                try
                    ipcTask.Wait(System.TimeSpan.FromSeconds(1.0)) |> ignore
                with _ ->
                    ()
            finally
                this.DisposeDebounceTimer()
                this.Ready.Dispose()
        }

/// Functions for creating and managing daemons.
module Daemon =
    let private sourceDebounceMs = 500
    let private projectDebounceMs = 200

    /// Create a daemon with the given checker (internal, for testing).
    let internal createWith (checker: FSharpChecker) (repoRoot: string) =
        let host = PluginHost.create checker repoRoot
        let pipeline = CheckPipeline(checker)
        let graph = ProjectGraph()
        let toolsPath = Init.init (DirectoryInfo(repoRoot)) None
        let loader = WorkspaceLoader.Create(toolsPath, [])

        let pendingChanges = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
        let mutable debounceTimer: System.Threading.Timer option = None
        let debounceLock = obj ()

        let suppressedFiles =
            System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

        let mutable processingChanges = 0

        let processChanges (_state: obj) =
            if Interlocked.CompareExchange(&processingChanges, 1, 0) = 0 then
                try
                    let changes = System.Collections.Generic.List<FileChangeKind>()
                    let mutable item = Unchecked.defaultof<_>

                    while pendingChanges.TryTake(&item) do
                        changes.Add(item)

                    if changes.Count > 0 then
                        let mutable sourceFiles = []
                        let mutable projFiles = []
                        let mutable hasSolution = false

                        for c in changes do
                            match c with
                            | SourceChanged files -> sourceFiles <- files @ sourceFiles
                            | ProjectChanged files -> projFiles <- files @ projFiles
                            | SolutionChanged -> hasSolution <- true

                        // Filter out files written by preprocessors (suppress re-trigger)
                        let allSourceFiles =
                            sourceFiles
                            |> List.distinct
                            |> List.filter (fun f ->
                                match suppressedFiles.TryRemove(f) with
                                | true, _ -> false
                                | false, _ -> true)

                        if hasSolution then
                            host.EmitFileChanged(SolutionChanged)

                        if not projFiles.IsEmpty || hasSolution then
                            eprintfn "  [daemon] Project/solution change detected — re-discovering projects"

                            if not (isNull (box checker)) then
                                checker.InvalidateAll()
                                checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()

                            discoverAndRegisterProjects repoRoot loader graph pipeline
                            |> Async.RunSynchronously

                            eprintfn
                                "  [daemon] Re-discovery complete: %d projects, %d files"
                                (graph.GetAllProjects().Length)
                                (pipeline.GetAllRegisteredFiles().Length)

                            if not projFiles.IsEmpty then
                                host.EmitFileChanged(ProjectChanged(projFiles |> List.distinct))

                        if not allSourceFiles.IsEmpty then
                            let modifiedByPreprocessors = host.RunPreprocessors(allSourceFiles)

                            for file in modifiedByPreprocessors do
                                suppressedFiles.TryAdd(file, true) |> ignore

                            let changedProjects =
                                allSourceFiles
                                |> List.choose (fun f -> graph.GetProjectForFile(f))
                                |> List.distinct

                            // Files in dependent projects (not the changed project itself)
                            let dependentProjectFiles =
                                changedProjects
                                |> List.collect (fun p -> graph.GetTransitiveDependents(p))
                                |> List.distinct
                                |> List.filter (fun p -> not (changedProjects |> List.contains p))
                                |> List.collect (fun proj -> graph.GetSourceFiles(proj))

                            let allFilesToCheck = (allSourceFiles @ dependentProjectFiles) |> List.distinct

                            host.EmitFileChanged(SourceChanged allFilesToCheck)

                            for file in allFilesToCheck do
                                let result = pipeline.CheckFile(file) |> Async.RunSynchronously

                                match result with
                                | Some checkResult ->
                                    host.EmitFileChecked(checkResult)
                                    reportFcsDiagnostics host checkResult

                                | None -> ()
                finally
                    Interlocked.Exchange(&processingChanges, 0) |> ignore

                    // If items arrived during processing, re-arm the debounce timer
                    if not pendingChanges.IsEmpty then
                        lock debounceLock (fun () ->
                            match debounceTimer with
                            | Some timer -> timer.Change(sourceDebounceMs, System.Threading.Timeout.Infinite) |> ignore
                            | None -> ())

        let mutable pendingDelayMs = 0

        let onChange change =
            pendingChanges.Add(change)

            let delayMs =
                match change with
                | ProjectChanged _
                | SolutionChanged -> projectDebounceMs
                | SourceChanged _ -> sourceDebounceMs

            lock debounceLock (fun () ->
                pendingDelayMs <- max pendingDelayMs delayMs

                match debounceTimer with
                | Some timer -> timer.Change(pendingDelayMs, System.Threading.Timeout.Infinite) |> ignore
                | None ->
                    debounceTimer <-
                        Some(
                            new System.Threading.Timer(
                                System.Threading.TimerCallback(fun state ->
                                    lock debounceLock (fun () -> pendingDelayMs <- 0)
                                    processChanges state),
                                null,
                                pendingDelayMs,
                                System.Threading.Timeout.Infinite
                            )
                        ))

        let watcher = FileWatcher.create repoRoot onChange

        let disposeDebounceTimer () =
            lock debounceLock (fun () ->
                match debounceTimer with
                | Some timer ->
                    timer.Dispose()
                    debounceTimer <- None
                | None -> ())

        { Host = host
          Watcher = watcher
          Checker = checker
          Pipeline = pipeline
          Graph = graph
          RepoRoot = repoRoot
          WorkspaceLoader = loader
          ScanState = ScanIdle
          ScanSemaphore = new SemaphoreSlim(1, 1)
          DisposeDebounceTimer = disposeDebounceTimer
          Ready = new ManualResetEventSlim(false) }

    /// Create a new daemon for the given repository root with a warm FSharpChecker.
    let create (repoRoot: string) =
        let checker =
            FSharpChecker.Create(
                projectCacheSize = 200,
                keepAssemblyContents = true,
                keepAllBackgroundResolutions = true,
                parallelReferenceResolution = true
            )

        createWith checker repoRoot
