module FsHotWatch.Daemon

open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Ipc
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.ProjectGraph
open FsHotWatch.Watcher

/// Discover .fsproj files and register them with the graph and pipeline.
let private discoverAndRegisterProjects
    (repoRoot: string)
    (checker: FSharpChecker)
    (graph: ProjectGraph)
    (pipeline: CheckPipeline)
    =
    async {
        let searchDirs =
            [ Path.Combine(repoRoot, "src")
              Path.Combine(repoRoot, "tests") ]
            |> List.filter Directory.Exists

        let fsprojFiles =
            searchDirs
            |> List.collect (fun dir ->
                Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories)
                |> Array.toList)
            |> List.filter (fun f ->
                let n = f.Replace('\\', '/')
                not (n.Contains("/obj/")) && not (n.Contains("/bin/")))

        graph.PrepareForRediscovery()
        pipeline.PrepareForRediscovery()

        for fsproj in fsprojFiles do
            try
                graph.RegisterFromFsproj(fsproj) |> ignore
            with ex ->
                eprintfn "  [discover] Failed to parse %s: %s" (Path.GetFileName fsproj) ex.Message

        for fsproj in graph.GetTopologicalOrder() do
            try
                let srcFiles = graph.GetSourceFiles(fsproj) |> List.toArray

                let anchorFile = srcFiles |> Array.tryFind File.Exists

                match anchorFile with
                | Some anchor ->
                    let source = File.ReadAllText(anchor)
                    let sourceText = SourceText.ofString source

                    let! projOptions, _ =
                        checker.GetProjectOptionsFromScript(anchor, sourceText, assumeDotNetFramework = false)

                    pipeline.RegisterProject(fsproj, { projOptions with SourceFiles = srcFiles })
                    eprintfn "  [discover] Registered %s (%d files)" (Path.GetFileName fsproj) srcFiles.Length
                | None ->
                    eprintfn "  [discover] Skipping %s — no source files exist on disk" (Path.GetFileName fsproj)
            with ex ->
                eprintfn "  [discover] Failed to load %s: %s" (Path.GetFileName fsproj) ex.Message
    }

/// The daemon ties together a warm FSharpChecker, file watcher, check pipeline, and plugin host.
/// It runs until the provided CancellationToken is cancelled.
[<NoComparison; NoEquality>]
type Daemon =
    { /// The plugin host that manages plugin lifecycle and event dispatch.
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
      /// Current scan progress state.
      mutable ScanState: ScanState
      /// Semaphore ensuring only one scan runs at a time.
      ScanSemaphore: SemaphoreSlim
      /// Disposes the debounce timer used for coalescing file change events.
      DisposeDebounceTimer: unit -> unit }

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
                        | None ->
                            skippedCount <- skippedCount + 1

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

            try
                let tcs =
                    System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg =
                    cancellationToken.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask
            finally
                this.DisposeDebounceTimer()
        }

    /// Discover .fsproj files in src/ and tests/ and register them with the pipeline.
    member this.DiscoverAndRegisterProjects() =
        discoverAndRegisterProjects this.RepoRoot this.Checker this.Graph this.Pipeline

    /// Format scan state as a human-readable string.
    member this.FormatScanStatus() =
        match this.ScanState with
        | ScanIdle -> "idle"
        | Scanning(total, completed, _) ->
            let pct = if total > 0 then completed * 100 / total else 0
            $"scanning: %d{completed}/%d{total} files (%d{pct}%%)"
        | ScanComplete(total, elapsed) ->
            $"complete: %d{total} files checked in %.1f{elapsed.TotalSeconds}s"

    /// Run the daemon with IPC server on the given pipe name.
    /// Discovers projects, performs initial scan, then watches for changes.
    member this.RunWithIpc(pipeName: string, cts: CancellationTokenSource) =
        async {
            use _ = this.Watcher :> System.IDisposable

            try
                let onScan () =
                    Async.StartAsTask(this.ScanAll()) |> ignore

                let ipcTask =
                    Async.StartAsTask(
                        IpcServer.start pipeName this.Host cts onScan this.FormatScanStatus
                    )

                // Discover projects and perform initial full scan
                do! this.DiscoverAndRegisterProjects()
                do! this.ScanAll()

                let tcs =
                    System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg =
                    cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask

                try
                    ipcTask.Wait(System.TimeSpan.FromSeconds(1.0)) |> ignore
                with
                | _ -> ()
            finally
                this.DisposeDebounceTimer()
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

        let pendingChanges = System.Collections.Concurrent.ConcurrentBag<FileChangeKind>()
        let mutable debounceTimer: System.Threading.Timer option = None
        let debounceLock = obj ()
        let suppressedFiles = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()
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

                    discoverAndRegisterProjects repoRoot checker graph pipeline |> Async.RunSynchronously
                    eprintfn "  [daemon] Re-discovery complete: %d projects, %d files" (graph.GetAllProjects().Length) (pipeline.GetAllRegisteredFiles().Length)

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

                    let allFilesToCheck =
                        (allSourceFiles @ dependentProjectFiles) |> List.distinct

                    host.EmitFileChanged(SourceChanged allFilesToCheck)

                    for file in allFilesToCheck do
                        let result = pipeline.CheckFile(file) |> Async.RunSynchronously

                        match result with
                        | Some checkResult -> host.EmitFileChecked(checkResult)
                        | None -> ()

            finally
                Volatile.Write(&processingChanges, 0)

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
                | Some timer ->
                    timer.Change(pendingDelayMs, System.Threading.Timeout.Infinite) |> ignore
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
          ScanState = ScanIdle
          ScanSemaphore = new SemaphoreSlim(1, 1)
          DisposeDebounceTimer = disposeDebounceTimer }

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
