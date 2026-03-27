module FsHotWatch.Daemon

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open FsHotWatch.CheckPipeline
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Ipc
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.Watcher
open FsHotWatch.PluginHost
open FsHotWatch.ProjectGraph
open FsHotWatch.Watcher

/// Extract FCS diagnostics from check results and report to the error ledger.
let private reportFcsDiagnostics (host: PluginHost) (checkResult: Events.FileCheckResult) =
    if not (isNull (box checkResult.CheckResults)) then
        // FCS per-file diagnostics are noisy: TreatWarningsAsErrors promotes CE-related
        // warnings (FS1182 from SqlHydra, etc.) to errors that the real build handles
        // via #nowarn. The build plugin provides authoritative compilation diagnostics.
        // Only report genuine FCS errors that aren't the well-known CE false positives.
        let suppressedCodes = set [ 1182 ] // SqlHydra CE "value unused"

        let diagnostics =
            checkResult.CheckResults.Diagnostics
            |> Array.choose (fun d ->
                if suppressedCodes.Contains(d.ErrorNumber) then
                    None
                else
                    match d.Severity with
                    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error ->
                        Some
                            { Message = d.Message
                              Severity = "error"
                              Line = d.StartLine
                              Column = d.StartColumn }
                    | _ -> None)
            |> Array.toList

        if diagnostics.IsEmpty then
            host.ClearErrors("fcs", checkResult.File, version = checkResult.Version)
        else
            host.ReportErrors("fcs", checkResult.File, diagnostics, version = checkResult.Version)

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
        Logging.info "discover" "Loading project options via MSBuild evaluation..."

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

            Logging.info
                "discover"
                $"MSBuild evaluation complete: %d{fcsOptionsList.Length} projects in %.1f{sw.Elapsed.TotalSeconds}s"

            for fcsOptions in fcsOptionsList do
                try
                    let absProject = Path.GetFullPath(fcsOptions.ProjectFileName)
                    pipeline.RegisterProject(absProject, fcsOptions)

                    Logging.info
                        "discover"
                        $"Registered %s{Path.GetFileName fcsOptions.ProjectFileName} (%d{fcsOptions.SourceFiles.Length} files, %d{fcsOptions.OtherOptions.Length} opts)"
                with ex ->
                    Logging.error
                        "discover"
                        $"Failed to register %s{Path.GetFileName fcsOptions.ProjectFileName}: %s{ex.Message}"
        with ex ->
            sw.Stop()
            Logging.error "discover" $"MSBuild evaluation failed (%.1f{sw.Elapsed.TotalSeconds}s): %s{ex.Message}"
    }

/// Manages TaskCompletionSource instances for signal-based WaitForScan.
type ScanSignal() =
    let mutable waiters: (int64 * TaskCompletionSource<unit>) list = []
    let lockObj = obj ()

    /// Register a waiter that resolves when generation exceeds afterGeneration.
    /// If afterGeneration < 0, resolves on the next generation increment.
    member _.WaitForGeneration(afterGeneration: int64, currentGeneration: int64) : Task<unit> =
        let alreadySatisfied =
            if afterGeneration >= 0L then
                currentGeneration > afterGeneration
            else
                // Legacy path (afterGeneration < 0): resolve immediately if any scan has completed
                currentGeneration > 0L

        if alreadySatisfied then
            Logging.debug
                "scan-signal"
                $"WaitForGeneration(%d{afterGeneration}, %d{currentGeneration}) — already satisfied, returning immediately"

            Task.FromResult(())
        else
            Logging.debug
                "scan-signal"
                $"WaitForGeneration(%d{afterGeneration}, %d{currentGeneration}) — registering waiter"

            let tcs =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            lock lockObj (fun () -> waiters <- (afterGeneration, tcs) :: waiters)
            tcs.Task

    /// Signal all waiters whose afterGeneration is now satisfied.
    member _.SignalGeneration(newGeneration: int64) =
        let toSignal =
            lock lockObj (fun () ->
                let s, r =
                    waiters
                    |> List.partition (fun (afterGen, _) -> afterGen < 0L || newGeneration > afterGen)

                Logging.debug
                    "scan-signal"
                    $"SignalGeneration(%d{newGeneration}) — resolving %d{s.Length} waiters, %d{r.Length} remaining"

                waiters <- r
                s)

        for _, tcs in toSignal do
            tcs.TrySetResult(()) |> ignore

let private isTerminal (s: PluginStatus) =
    match s with
    | Running _ -> false
    | _ -> true

let private allTerminal (statuses: Map<string, PluginStatus>) =
    not statuses.IsEmpty && statuses |> Map.forall (fun _ s -> isTerminal s)

/// Wait for all plugins to reach a terminal state with 1-second stability confirmation.
let private waitForAllTerminal (host: PluginHost) () : Task<unit> =
    let tcs =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable timerCts: CancellationTokenSource option = None
    let mutable subscription: System.IDisposable option = None
    let mutable resolved = false
    let lockObj = obj ()

    let checkAndSchedule () =
        lock lockObj (fun () ->
            if resolved then
                ()
            else

                let statuses = host.GetAllStatuses()

                timerCts
                |> Option.iter (fun c ->
                    c.Cancel()
                    c.Dispose())

                timerCts <- None

                if allTerminal statuses then
                    let newCts = new CancellationTokenSource()
                    timerCts <- Some newCts

                    Task
                        .Delay(1000, newCts.Token)
                        .ContinueWith(fun (t: Task) ->
                            if not t.IsCanceled then
                                lock lockObj (fun () ->
                                    if not resolved then
                                        let final = host.GetAllStatuses()

                                        if allTerminal final then
                                            resolved <- true

                                            timerCts |> Option.iter (fun c -> c.Dispose())
                                            timerCts <- None

                                            subscription |> Option.iter (fun s -> s.Dispose())
                                            subscription <- None

                                            tcs.TrySetResult(()) |> ignore))
                    |> ignore)

    // Subscribe before initial check to avoid TOCTOU gap
    subscription <- Some(host.OnStatusChanged.Subscribe(fun _ -> checkAndSchedule ()))
    checkAndSchedule ()

    tcs.Task

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
        /// Monotonically increasing scan generation counter.
        mutable ScanGeneration: int64
        /// Semaphore ensuring only one scan runs at a time.
        ScanSemaphore: SemaphoreSlim
        /// Disposes the debounce timer used for coalescing file change events.
        DisposeDebounceTimer: unit -> unit
        /// Shared cancellation token ref for processChanges (set by RunWithIpc).
        CancellationTokenRef: CancellationToken ref
        /// Signalled when the daemon is ready to accept file change events.
        Ready: ManualResetEventSlim
        /// Signal-based notification for WaitForScan clients.
        ScanSignal: ScanSignal
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

    /// Get current scan generation (incremented after each completed scan).
    member this.GetScanGeneration() = Volatile.Read(&this.ScanGeneration)

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
                Logging.info "scan" $"%d{registeredProjects.Length} projects, %d{total} files registered"
                let sw = System.Diagnostics.Stopwatch.StartNew()
                this.ScanState <- Scanning(total, 0, System.DateTime.UtcNow)

                if not files.IsEmpty then
                    // Run preprocessors (e.g., formatter) before dispatching
                    let modified = this.Host.RunPreprocessors(files)

                    if modified.Length > 0 then
                        Logging.info "scan" $"Preprocessors modified %d{modified.Length} files (watcher may re-trigger)"

                    this.Host.EmitFileChanged(SourceChanged files)
                    let mutable completed = 0

                    let mutable checkedCount = 0
                    let mutable skippedCount = 0

                    // Check files in parallel tiers based on project dependency graph
                    let tiers = this.Graph.GetParallelTiers()

                    for tier in tiers do
                        let tierFiles = tier |> List.collect (fun proj -> this.Graph.GetSourceFiles(proj))

                        let! results =
                            tierFiles
                            |> List.map (fun file -> this.Pipeline.CheckFile(file, ct))
                            |> Async.Parallel

                        for result in results do
                            match result with
                            | Some checkResult ->
                                checkedCount <- checkedCount + 1
                                do! this.Host.EmitFileCheckedParallel(checkResult)
                                reportFcsDiagnostics this.Host checkResult
                            | None -> skippedCount <- skippedCount + 1

                            completed <- completed + 1
                            this.ScanState <- Scanning(total, completed, System.DateTime.UtcNow)

                    Logging.info
                        "scan"
                        $"Checked %d{checkedCount} files (%d{tiers.Length} tiers), skipped %d{skippedCount}"

                sw.Stop()
                this.ScanState <- ScanComplete(total, sw.Elapsed)
                Interlocked.Increment(&this.ScanGeneration) |> ignore
                this.ScanSignal.SignalGeneration(Volatile.Read(&this.ScanGeneration))
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

                let rpcConfig: DaemonRpcConfig =
                    { Host = this.Host
                      RequestShutdown = fun () -> cts.Cancel()
                      RequestScan = onScan
                      GetScanStatus = this.FormatScanStatus
                      GetScanGeneration = this.GetScanGeneration
                      TriggerBuild = triggerBuild
                      FormatAll = formatAll
                      WaitForScanGeneration =
                        fun afterGen -> this.ScanSignal.WaitForGeneration(afterGen, this.GetScanGeneration())
                      WaitForAllTerminal = waitForAllTerminal this.Host }

                let ipcTask = Async.StartAsTask(IpcServer.start pipeName rpcConfig cts)

                this.CancellationTokenRef.Value <- cts.Token
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
        let daemonCtRef = ref CancellationToken.None

        let processChanges (_state: obj) =
            if Interlocked.CompareExchange(&processingChanges, 1, 0) = 0 then
                Async.Start(
                    async {
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

                                Logging.debug
                                    "daemon"
                                    $"processChanges: %d{sourceFiles.Length} source, %d{projFiles.Length} project, solution=%b{hasSolution}"

                                for f in sourceFiles do
                                    Logging.debug "daemon" $"source: %s{f}"

                                for f in projFiles do
                                    Logging.debug "daemon" $"project: %s{f}"

                                // Filter out files written by preprocessors (suppress re-trigger)
                                let allSourceFiles =
                                    sourceFiles
                                    |> List.distinct
                                    |> List.filter (fun f ->
                                        let suppressed =
                                            match suppressedFiles.TryRemove(f) with
                                            | true, _ -> true
                                            | false, _ -> false

                                        if suppressed then
                                            Logging.debug "daemon" $"suppressed: %s{f}"

                                        not suppressed)
                                    |> List.filter (fun f ->
                                        let changed = hasContentChanged f

                                        if not changed then
                                            Logging.debug "daemon" $"content unchanged: %s{f}"

                                        changed)

                                let projFilesChanged =
                                    projFiles
                                    |> List.distinct
                                    |> List.filter (fun f ->
                                        let changed = hasContentChanged f

                                        if not changed then
                                            Logging.debug "daemon" $"content unchanged: %s{f}"

                                        changed)

                                if hasSolution then
                                    host.EmitFileChanged(SolutionChanged)

                                if not projFilesChanged.IsEmpty || hasSolution then
                                    Logging.info "daemon" "Project/solution change detected — re-discovering projects"

                                    if not (isNull (box checker)) then
                                        checker.InvalidateAll()
                                        checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()

                                    do! discoverAndRegisterProjects repoRoot loader graph pipeline

                                    Logging.info
                                        "daemon"
                                        $"Re-discovery complete: %d{graph.GetAllProjects().Length} projects, %d{pipeline.GetAllRegisteredFiles().Length} files"

                                    if not projFilesChanged.IsEmpty then
                                        host.EmitFileChanged(ProjectChanged projFilesChanged)

                                if not allSourceFiles.IsEmpty then
                                    let modifiedByPreprocessors = host.RunPreprocessors(allSourceFiles)

                                    for file in modifiedByPreprocessors do
                                        suppressedFiles.TryAdd(file, true) |> ignore

                                    let changedProjects =
                                        allSourceFiles
                                        |> List.choose (fun f -> graph.GetProjectForFile(f))
                                        |> List.distinct

                                    let dependentProjectFiles =
                                        changedProjects
                                        |> List.collect (fun p -> graph.GetTransitiveDependents(p))
                                        |> List.distinct
                                        |> List.filter (fun p -> not (changedProjects |> List.contains p))
                                        |> List.collect (fun proj -> graph.GetSourceFiles(proj))

                                    let allFilesToCheck = (allSourceFiles @ dependentProjectFiles) |> List.distinct

                                    host.EmitFileChanged(SourceChanged allFilesToCheck)

                                    Logging.debug "daemon" $"Checking %d{allFilesToCheck.Length} files after change"

                                    for file in allFilesToCheck do
                                        let! result = pipeline.CheckFile(file, daemonCtRef.Value)

                                        match result with
                                        | Some checkResult ->
                                            Logging.debug "daemon" $"EmitFileChecked: %s{Path.GetFileName(file)}"
                                            do! host.EmitFileCheckedParallel(checkResult)
                                            reportFcsDiagnostics host checkResult

                                        | None -> ()
                        finally
                            Interlocked.Exchange(&processingChanges, 0) |> ignore

                            // If items arrived during processing, re-arm the debounce timer
                            if not pendingChanges.IsEmpty then
                                lock debounceLock (fun () ->
                                    match debounceTimer with
                                    | Some timer ->
                                        timer.Change(sourceDebounceMs, System.Threading.Timeout.Infinite) |> ignore
                                    | None -> ())
                    },
                    daemonCtRef.Value
                )

        let mutable pendingDelayMs = 0

        let onChange change =
            Logging.debug "watcher" $"%O{change}"

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
          ScanGeneration = 0L
          ScanSemaphore = new SemaphoreSlim(1, 1)
          DisposeDebounceTimer = disposeDebounceTimer
          CancellationTokenRef = daemonCtRef
          Ready = new ManualResetEventSlim(false)
          ScanSignal = ScanSignal() }

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
