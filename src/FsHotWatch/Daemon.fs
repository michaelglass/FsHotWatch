module FsHotWatch.Daemon

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open System.Text.Json
open FsHotWatch.CheckCache
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
/// Reports all severity levels (Error, Warning, Info, Hidden) with configurable
/// suppressed diagnostic codes.
let private reportFcsDiagnostics (suppressedCodes: Set<int>) (host: PluginHost) (checkResult: Events.FileCheckResult) =
    match checkResult.CheckResults with
    | None -> ()
    | Some checkResults ->
        let diagnostics =
            checkResults.Diagnostics
            |> Array.choose (fun d ->
                if suppressedCodes.Contains(d.ErrorNumber) then
                    None
                else
                    match d.Severity with
                    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error ->
                        Some
                            { Message = d.Message
                              Severity = DiagnosticSeverity.Error
                              Line = d.StartLine
                              Column = d.StartColumn
                              Detail = None }
                    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning ->
                        Some
                            { Message = d.Message
                              Severity = DiagnosticSeverity.Warning
                              Line = d.StartLine
                              Column = d.StartColumn
                              Detail = None }
                    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Info ->
                        Some
                            { Message = d.Message
                              Severity = DiagnosticSeverity.Info
                              Line = d.StartLine
                              Column = d.StartColumn
                              Detail = None }
                    | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Hidden ->
                        Some
                            { Message = d.Message
                              Severity = DiagnosticSeverity.Hint
                              Line = d.StartLine
                              Column = d.StartColumn
                              Detail = None })
            |> Array.toList

        if diagnostics.IsEmpty then
            host.ClearErrors("fcs", checkResult.File, version = checkResult.Version)
        else
            host.ReportErrors("fcs", checkResult.File, diagnostics, version = checkResult.Version)

/// Fingerprint fsproj files by path + last-write-time. Used by ScanAll to skip
/// expensive MSBuild re-evaluation when no project files have changed.
let private fingerprintFsprojFiles (repoRoot: string) =
    let searchDirs =
        [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]
        |> List.filter Directory.Exists

    searchDirs
    |> List.collect (fun dir -> Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories) |> Array.toList)
    |> List.filter (fun f ->
        let n = f.Replace('\\', '/')
        not (n.Contains("/obj/")) && not (n.Contains("/bin/")))
    |> List.map (fun f -> f, File.GetLastWriteTimeUtc(f).Ticks)
    |> Set.ofList

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

/// Re-discover projects and clear FCS errors for any files that were removed.
/// Returns the set of removed files.
let private rediscoverAndClearRemoved
    (repoRoot: string)
    (loader: IWorkspaceLoader)
    (graph: ProjectGraph)
    (pipeline: CheckPipeline)
    (host: PluginHost)
    (logTag: string)
    =
    async {
        let oldFiles = graph.GetAllFiles() |> Set.ofList
        do! discoverAndRegisterProjects repoRoot loader graph pipeline
        let newFiles = graph.GetAllFiles() |> Set.ofList
        let removedFiles = Set.difference oldFiles newFiles

        for file in removedFiles do
            host.ClearErrors("fcs", file)

        if not removedFiles.IsEmpty then
            Logging.info logTag $"Cleared errors for %d{removedFiles.Count} removed files"

        return removedFiles
    }

/// Manages TaskCompletionSource instances for signal-based WaitForScan.
[<NoComparison; NoEquality>]
type private ScanSignalMsg =
    | WaitFor of afterGen: int64 * TaskCompletionSource<unit>
    | Signal of newGen: int64

type ScanSignal() =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (waiters: (int64 * TaskCompletionSource<unit>) list) =
                async {
                    let! msg = inbox.Receive()

                    try
                        match msg with
                        | WaitFor(afterGeneration, tcs) ->
                            Logging.debug "scan-signal" $"WaitFor(%d{afterGeneration}) — registering waiter"

                            return! loop ((afterGeneration, tcs) :: waiters)

                        | Signal newGeneration ->
                            let toSignal, remaining =
                                waiters
                                |> List.partition (fun (afterGen, _) -> afterGen < 0L || newGeneration > afterGen)

                            Logging.debug
                                "scan-signal"
                                $"SignalGeneration(%d{newGeneration}) — resolving %d{toSignal.Length} waiters, %d{remaining.Length} remaining"

                            for _, tcs in toSignal do
                                tcs.TrySetResult(()) |> ignore

                            return! loop remaining
                    with ex ->
                        Logging.error "scan-signal" $"Agent failed: %s{ex.ToString()}"
                        return! loop waiters
                }

            loop [])

    /// Register a waiter that resolves when generation exceeds afterGeneration.
    /// If afterGeneration < 0, resolves on the next generation increment.
    member _.WaitForGeneration(afterGeneration: int64, currentGeneration: int64) : Task<unit> =
        let alreadySatisfied =
            if afterGeneration >= 0L then
                currentGeneration > afterGeneration
            else
                currentGeneration > 0L

        if alreadySatisfied then
            Logging.debug
                "scan-signal"
                $"WaitForGeneration(%d{afterGeneration}, %d{currentGeneration}) — already satisfied, returning immediately"

            Task.FromResult(())
        else
            let tcs =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            agent.Post(WaitFor(afterGeneration, tcs))
            tcs.Task

    /// Signal all waiters whose afterGeneration is now satisfied.
    member _.SignalGeneration(newGeneration: int64) = agent.Post(Signal newGeneration)

/// Messages handled by the scan agent.
[<NoComparison; NoEquality>]
type private ScanMsg = RequestScan of force: bool * CancellationToken * AsyncReplyChannel<unit>

/// Internal state managed by the scan agent.
type private ScanAgentState =
    { ScanState: ScanState
      Generation: int64
      LastFingerprint: Set<string * int64> }

/// Opaque handle to the scan MailboxProcessor.
/// Read-only state (ScanState, Generation) is stored in mutable fields updated
/// via Volatile.Write by the agent handler, readable without mailbox round-trip.
[<NoComparison; NoEquality>]
type ScanAgent =
    private
        { Agent: MailboxProcessor<ScanMsg>
          mutable CurrentState: ScanState
          mutable CurrentGeneration: int64 }

    member this.GetScanState() = Volatile.Read(&this.CurrentState)
    member this.GetGeneration() = Volatile.Read(&this.CurrentGeneration)

let private createScanAgent agent =
    { Agent = agent
      CurrentState = ScanIdle
      CurrentGeneration = 0L }

let private requestScan (sa: ScanAgent) force ct =
    sa.Agent.PostAndAsyncReply(fun ch -> RequestScan(force, ct, ch))

let private getScanGeneration (sa: ScanAgent) = sa.GetGeneration()

let private getScanStatus (sa: ScanAgent) = sa.GetScanState()

let private setScanStatus (sa: ScanAgent) state = Volatile.Write(&sa.CurrentState, state)

let private isTerminal (s: PluginStatus) =
    match s with
    | Running _ -> false
    | _ -> true

let private allTerminal (statuses: Map<string, PluginStatus>) =
    not statuses.IsEmpty && statuses |> Map.forall (fun _ s -> isTerminal s)

/// Wait for all plugins to reach a terminal state with 1-second stability confirmation.
/// Times out with TimeoutException after the specified timeout.
let internal waitForAllTerminal (host: PluginHost) (timeout: System.TimeSpan) () : Task<unit> =
    let tcs =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable timerCts: CancellationTokenSource option = None
    let mutable timeoutCts: CancellationTokenSource option = None
    let mutable subscription: System.IDisposable option = None
    let mutable resolved = false
    let lockObj = obj ()

    let cleanup () =
        timerCts
        |> Option.iter (fun c ->
            c.Cancel()
            c.Dispose())

        timerCts <- None

        timeoutCts
        |> Option.iter (fun c ->
            c.Cancel()
            c.Dispose())

        timeoutCts <- None

        subscription |> Option.iter (fun s -> s.Dispose())
        subscription <- None

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
                                            cleanup ()
                                            tcs.TrySetResult(()) |> ignore))
                    |> ignore)

    // Subscribe before initial check to avoid TOCTOU gap
    subscription <- Some(host.OnStatusChanged.Subscribe(fun _ -> checkAndSchedule ()))
    checkAndSchedule ()

    // Periodic status logging so operators can see what's blocking completion
    let logCts = new CancellationTokenSource()

    let rec logLoop () =
        Task
            .Delay(10_000, logCts.Token)
            .ContinueWith(fun (t: Task) ->
                if not t.IsCanceled then
                    lock lockObj (fun () ->
                        if not resolved then
                            let statuses = host.GetAllStatuses()

                            let running =
                                statuses
                                |> Map.toList
                                |> List.choose (fun (name, s) ->
                                    match s with
                                    | Running since -> Some $"%s{name} (since %O{since})"
                                    | _ -> None)

                            match running with
                            | [] -> Logging.info "wait" "All plugins terminal, waiting for stability confirmation..."
                            | plugins ->
                                let joined = plugins |> String.concat ", "

                                Logging.info "wait" $"Waiting for plugins: %s{joined}")

                    logLoop ())
        |> ignore

    logLoop ()

    // Clean up log timer when resolved
    tcs.Task.ContinueWith(fun (_: Task<unit>) ->
        logCts.Cancel()
        logCts.Dispose())
    |> ignore

    // Set up timeout — cancellable so the delay task doesn't hold closure refs for the full 30 min on normal completion
    if timeout <> System.TimeSpan.MaxValue then
        let cts = new CancellationTokenSource()
        timeoutCts <- Some cts

        Task
            .Delay(timeout, cts.Token)
            .ContinueWith(fun (t: Task) ->
                if not t.IsCanceled then
                    lock lockObj (fun () ->
                        if not resolved then
                            resolved <- true
                            cleanup ()

                            let statuses = host.GetAllStatuses()

                            let running =
                                statuses
                                |> Map.toList
                                |> List.choose (fun (name, s) ->
                                    match s with
                                    | Running since -> Some $"%s{name} (since %O{since})"
                                    | _ -> None)

                            let detail =
                                if running.IsEmpty then
                                    "all terminal but stability check failed"
                                else
                                    let joined = running |> String.concat ", "
                                    $"still running: %s{joined}"

                            tcs.TrySetException(
                                System.TimeoutException($"WaitForComplete timed out after %O{timeout} — %s{detail}")
                            )
                            |> ignore))
        |> ignore

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
        /// MailboxProcessor that serializes scan requests (replaces SemaphoreSlim).
        ScanAgent: ScanAgent
        /// Shared cancellation token ref for processChanges (set by RunWithIpc).
        CancellationTokenRef: CancellationToken ref
        /// Signalled when the daemon is ready to accept file change events.
        Ready: ManualResetEventSlim
        /// Signal-based notification for WaitForScan clients.
        ScanSignal: ScanSignal
        /// Optional jj scan guard for content-addressed cache optimization.
        JjGuard: JjHelper.JjScanGuard option
        /// FCS diagnostic codes to suppress (default: [1182] SqlHydra CE "value unused").
        FcsSuppressedCodes: Set<int>
    }

    /// Register a declarative framework-managed plugin handler.
    member this.RegisterHandler<'State, 'Msg>(handler: PluginFramework.PluginHandler<'State, 'Msg>) =
        this.Host.RegisterHandler(handler)

    /// Register a preprocessor (e.g., formatter) that runs before events are dispatched.
    member this.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) =
        this.Host.RegisterPreprocessor(preprocessor)

    /// Register a project's options so its files can be checked incrementally.
    member this.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        this.Pipeline.RegisterProject(projectPath, options)

    /// Get current scan state.
    member this.GetScanState() = getScanStatus this.ScanAgent

    /// Get current scan generation (incremented after each completed scan).
    member this.GetScanGeneration() = getScanGeneration this.ScanAgent

    /// Set scan state (internal, for testing).
    member internal this.SetScanState(state: ScanState) = setScanStatus this.ScanAgent state

    /// Invalidate cache for a file, re-check it, and emit results to plugins.
    member this.InvalidateAndRecheck(filePath: string) =
        async {
            this.Pipeline.InvalidateFile(filePath)
            let! result = this.Pipeline.CheckFile(filePath)

            match result with
            | Some checkResult ->
                this.Host.EmitFileChecked(checkResult)
                reportFcsDiagnostics this.FcsSuppressedCodes this.Host checkResult

                return
                    JsonSerializer.Serialize(
                        {| status = "rechecked"
                           file = checkResult.File |}
                    )
            | None ->
                return
                    JsonSerializer.Serialize(
                        {| status = "failed"
                           file = Path.GetFullPath(filePath) |}
                    )
        }

    /// Scan all registered files — check each one and emit events to plugins.
    /// Blocks until complete. If a scan is already running, waits for it to finish.
    member this.ScanAll(?force: bool) =
        async {
            let force = defaultArg force false
            let! ct = Async.CancellationToken

            do! requestScan this.ScanAgent force ct
        }

    /// Run a single full scan in-process without watcher or IPC.
    /// Discovers projects, scans all files, waits for plugins to complete, returns statuses.
    member this.RunOnce() =
        async {
            do! this.ScanAll(force = true)

            do!
                waitForAllTerminal this.Host (System.TimeSpan.FromMinutes(30.0)) ()
                |> Async.AwaitTask

            return this.Host.GetAllStatuses()
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
                this.Ready.Dispose()
        }

    /// Discover .fsproj files in src/ and tests/ and register them with the pipeline.
    member this.DiscoverAndRegisterProjects() =
        discoverAndRegisterProjects this.RepoRoot this.WorkspaceLoader this.Graph this.Pipeline

    /// Format scan state as a human-readable string.
    member this.FormatScanStatus() =
        let scanState = getScanStatus this.ScanAgent

        match scanState with
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
                let onScan (force: bool) =
                    Async.StartAsTask(this.ScanAll(force = force)) |> ignore

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
                      WaitForAllTerminal = waitForAllTerminal this.Host (System.TimeSpan.FromMinutes(30.0))
                      InvalidateAndRecheck = fun filePath -> this.InvalidateAndRecheck(filePath) }

                let ipcTask = Async.StartAsTask(IpcServer.start pipeName rpcConfig cts)

                this.CancellationTokenRef.Value <- cts.Token
                this.Ready.Set()

                // Initial full scan — performScan handles discovery when LastFingerprint
                // differs from current (always true on first run since it starts empty).
                // force bypasses jj guard since plugins have no state from previous daemon runs.
                do! this.ScanAll(force = true)

                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask

                try
                    ipcTask.Wait(System.TimeSpan.FromSeconds(1.0)) |> ignore
                with ex ->
                    Logging.debug "daemon" $"IPC shutdown: %s{ex.Message}"
            finally
                this.Ready.Dispose()
        }

/// Execute the full scan logic, returning the updated agent state.
let private performScan
    (host: PluginHost)
    (pipeline: CheckPipeline)
    (graph: ProjectGraph)
    (repoRoot: string)
    (loader: IWorkspaceLoader)
    (jjGuard: JjHelper.JjScanGuard option)
    (scanSignal: ScanSignal)
    (fcsSuppressedCodes: Set<int>)
    (state: ScanAgentState)
    (force: bool)
    (ct: CancellationToken)
    =
    async {
        // Re-discover projects before scanning so that removed files/projects
        // are cleared before results are returned to the client. Without this,
        // a concurrent processChanges re-discovery (triggered by the file watcher
        // with debounce delay) may complete AFTER the scan signals waiters,
        // leaving stale FCS errors visible for one cycle.
        // Guarded by fsproj fingerprint to skip expensive MSBuild evaluation
        // when no project files have changed.
        let currentFingerprint = fingerprintFsprojFiles repoRoot
        let mutable lastFingerprint = state.LastFingerprint

        if currentFingerprint <> state.LastFingerprint then
            let! _ = rediscoverAndClearRemoved repoRoot loader graph pipeline host "scan"

            lastFingerprint <- currentFingerprint

        let registeredProjects = pipeline.GetRegisteredProjects()
        let files = pipeline.GetAllRegisteredFiles()
        let total = files.Length
        Logging.info "scan" $"%d{registeredProjects.Length} projects, %d{total} files registered"
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let mutable scanState: ScanState = Scanning(total, 0, System.DateTime.UtcNow)

        if not files.IsEmpty then
            // jj guard: determine which files actually need checking (bypassed when force=true)
            let scanDecision =
                match jjGuard with
                | Some guard ->
                    // Always call BeginScan to snapshot currentCommitId for CommitScanSuccess
                    let decision = guard.BeginScan()

                    if force then
                        Logging.info "scan" "force scan: bypassing jj guard"
                        JjHelper.CheckAll
                    else
                        decision
                | None -> JjHelper.CheckAll

            match scanDecision with
            | JjHelper.SkipAll -> Logging.info "scan" "jj guard: skipping all checks (commit unchanged)"
            | _ ->

                let filesToCheck =
                    match scanDecision with
                    | JjHelper.CheckSubset changedFiles ->
                        let directlyChanged = files |> List.filter (fun f -> changedFiles.Contains(f))

                        let dependentFiles =
                            directlyChanged
                            |> List.choose (fun f -> graph.GetProjectForFile(f))
                            |> List.distinct
                            |> List.collect (fun p -> graph.GetTransitiveDependents(p))
                            |> List.distinct
                            |> List.collect (fun proj -> graph.GetSourceFiles(proj))

                        (directlyChanged @ dependentFiles) |> List.distinct
                    | _ -> files

                // Run preprocessors (e.g., formatter) before dispatching
                let modified = host.RunPreprocessors(files)

                if modified.Length > 0 then
                    Logging.info "scan" $"Preprocessors modified %d{modified.Length} files (watcher may re-trigger)"

                host.EmitFileChanged(SourceChanged files)
                let mutable completed = 0

                let mutable checkedCount = 0
                let mutable skippedCount = 0

                let filesToCheckSet = Set.ofList filesToCheck

                // Check files in parallel tiers based on project dependency graph
                let tiers = graph.GetParallelTiers()

                for tier in tiers do
                    let tierFiles = tier |> List.collect (fun proj -> graph.GetSourceFiles(proj))

                    let! results =
                        tierFiles
                        |> List.map (fun file ->
                            if filesToCheckSet.Contains(file) then
                                pipeline.CheckFile(file, ct)
                            else
                                async { return None })
                        |> Async.Parallel

                    for result in results do
                        match result with
                        | Some checkResult ->
                            checkedCount <- checkedCount + 1
                            host.EmitFileChecked(checkResult)
                            reportFcsDiagnostics fcsSuppressedCodes host checkResult
                        | None -> skippedCount <- skippedCount + 1

                        completed <- completed + 1
                        scanState <- Scanning(total, completed, System.DateTime.UtcNow)

                Logging.info "scan" $"Checked %d{checkedCount} files (%d{tiers.Length} tiers), skipped %d{skippedCount}"

            // Commit jj guard after successful scan
            match jjGuard with
            | Some guard -> guard.CommitScanSuccess()
            | None -> ()

        sw.Stop()
        let finalScanState = ScanComplete(total, sw.Elapsed)
        let newGeneration = state.Generation + 1L
        scanSignal.SignalGeneration(newGeneration)

        return
            { ScanState = finalScanState
              Generation = newGeneration
              LastFingerprint = lastFingerprint }
    }

/// Functions for creating and managing daemons.
module Daemon =
    let private sourceDebounceMs = 500
    let private projectDebounceMs = 200

    /// Create a daemon with the given checker (internal, for testing).
    /// Pass None for both cache params to disable caching entirely.
    let internal createWith
        (checker: FSharpChecker)
        (repoRoot: string)
        (cacheBackend: ICheckCacheBackend option)
        (cacheKeyProvider: ICacheKeyProvider option)
        (fcsSuppressedCodes: Set<int>)
        =
        let errorDir = Path.Combine(repoRoot, ".fshw", "errors")

        let fileReporter: IErrorReporter =
            FsHotWatch.FileErrorReporter.FileErrorReporter(errorDir)

        fileReporter.ClearAll()
        let taskCacheDir = Path.Combine(repoRoot, ".fshw", "cache", "tasks")
        let taskCache = FsHotWatch.FileTaskCache.FileTaskCache(taskCacheDir)

        let host =
            PluginHost(checker, repoRoot, reporters = [ fileReporter ], taskCache = taskCache)

        let pipeline =
            match cacheBackend, cacheKeyProvider with
            | Some b, Some kp -> CheckPipeline(checker, cacheBackend = b, cacheKeyProvider = kp)
            | Some b, None -> CheckPipeline(checker, cacheBackend = b)
            | _ -> CheckPipeline(checker)

        let graph = ProjectGraph()
        let toolsPath = Init.init (DirectoryInfo(repoRoot)) None
        let loader = WorkspaceLoader.Create(toolsPath, [])

        let daemonCtRef = ref CancellationToken.None

        let delayForChange change =
            match change with
            | ProjectChanged _
            | SolutionChanged _ -> projectDebounceMs
            | SourceChanged _ -> sourceDebounceMs

        let processBatch (changes: FileChangeKind list) (suppressed: Set<string>) =
            async {
                let mutable sourceFiles = []
                let mutable projFiles = []
                let mutable solutionFile = None

                for c in changes do
                    match c with
                    | SourceChanged files -> sourceFiles <- files @ sourceFiles
                    | ProjectChanged files -> projFiles <- files @ projFiles
                    | SolutionChanged f -> solutionFile <- Some f

                let hasSolution = solutionFile.IsSome

                Logging.debug
                    "daemon"
                    $"processChanges: %d{sourceFiles.Length} source, %d{projFiles.Length} project, solution=%b{hasSolution}"

                for f in sourceFiles do
                    Logging.debug "daemon" $"source: %s{f}"

                for f in projFiles do
                    Logging.debug "daemon" $"project: %s{f}"

                // Filter out files written by preprocessors (suppress re-trigger)
                let filteredSourceFiles, remainingSuppressed =
                    sourceFiles
                    |> List.distinct
                    |> List.fold
                        (fun (accepted, sup) f ->
                            if Set.contains f sup then
                                Logging.debug "daemon" $"suppressed: %s{f}"
                                (accepted, Set.remove f sup)
                            else
                                (f :: accepted, sup))
                        ([], suppressed)

                let allSourceFiles =
                    filteredSourceFiles
                    |> List.rev
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
                    host.EmitFileChanged(SolutionChanged solutionFile.Value)

                if not projFilesChanged.IsEmpty || hasSolution then
                    Logging.info "daemon" "Project/solution change detected — re-discovering projects"

                    // Guard: tests may pass Unchecked.defaultof for checker
                    if not (isNull (box checker)) then
                        checker.InvalidateAll()
                        checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()

                    let! _ = rediscoverAndClearRemoved repoRoot loader graph pipeline host "daemon"

                    Logging.info
                        "daemon"
                        $"Re-discovery complete: %d{graph.GetAllProjects().Length} projects, %d{pipeline.GetAllRegisteredFiles().Length} files"

                    if not projFilesChanged.IsEmpty then
                        host.EmitFileChanged(ProjectChanged projFilesChanged)

                if not allSourceFiles.IsEmpty then
                    let modifiedByPreprocessors = host.RunPreprocessors(allSourceFiles)

                    let newSuppressed =
                        modifiedByPreprocessors
                        |> List.fold (fun s f -> Set.add f s) remainingSuppressed

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
                            host.EmitFileChecked(checkResult)
                            reportFcsDiagnostics fcsSuppressedCodes host checkResult

                        | None -> ()

                    return newSuppressed
                else
                    return remainingSuppressed
            }

        let changeAgent =
            MailboxProcessor.Start(fun inbox ->
                let rec idle (suppressed: Set<string>) =
                    async {
                        let! msg = inbox.Receive()
                        let delayMs = delayForChange msg
                        return! debouncing [ msg ] delayMs suppressed
                    }

                and debouncing (pending: FileChangeKind list) (delayMs: int) (suppressed: Set<string>) =
                    async {
                        let! msg = inbox.TryReceive(delayMs)

                        match msg with
                        | Some change ->
                            let newDelay = max delayMs (delayForChange change)
                            return! debouncing (change :: pending) newDelay suppressed
                        | None ->
                            // Debounce expired — process batch
                            try
                                let! newSuppressed = processBatch (List.rev pending) suppressed
                                return! idle newSuppressed
                            with ex ->
                                Logging.error "daemon" $"processChanges failed: %s{ex.ToString()}"
                                return! idle suppressed
                    }

                idle Set.empty)

        let onChange change =
            Logging.debug "watcher" $"%O{change}"
            changeAgent.Post(change)

        let watcher = FileWatcher.create repoRoot onChange

        let jjGuard =
            match cacheKeyProvider with
            | Some(:? JjCacheKeyProvider) -> Some(JjHelper.JjScanGuard(repoRoot))
            | _ -> None

        let scanSignal = ScanSignal()

        // Mutable ref allows the agent loop (which starts immediately) to
        // update volatile fields on the wrapper created after MailboxProcessor.Start.
        let scanAgentRef: ScanAgent option ref = ref None

        let scanMailbox =
            MailboxProcessor.Start(fun inbox ->
                let rec loop (state: ScanAgentState) =
                    async {
                        let! msg = inbox.Receive()

                        match msg with
                        | RequestScan(force, ct, reply) ->
                            try
                                let! newState =
                                    performScan
                                        host
                                        pipeline
                                        graph
                                        repoRoot
                                        loader
                                        jjGuard
                                        scanSignal
                                        fcsSuppressedCodes
                                        state
                                        force
                                        ct

                                match scanAgentRef.Value with
                                | Some sa ->
                                    Volatile.Write(&sa.CurrentState, newState.ScanState)
                                    Volatile.Write(&sa.CurrentGeneration, newState.Generation)
                                | None -> ()

                                reply.Reply(())
                                return! loop newState
                            with ex ->
                                Logging.error "scan" $"performScan failed: %s{ex.ToString()}"
                                reply.Reply(())
                                return! loop state
                    }

                loop
                    { ScanState = ScanIdle
                      Generation = 0L
                      LastFingerprint = Set.empty })

        let scanAgentWrapper = createScanAgent scanMailbox
        scanAgentRef.Value <- Some scanAgentWrapper

        { Host = host
          Watcher = watcher
          Checker = checker
          Pipeline = pipeline
          Graph = graph
          RepoRoot = repoRoot
          WorkspaceLoader = loader
          ScanAgent = scanAgentWrapper
          CancellationTokenRef = daemonCtRef
          Ready = new ManualResetEventSlim(false)
          ScanSignal = scanSignal
          JjGuard = jjGuard
          FcsSuppressedCodes = fcsSuppressedCodes }

    /// Create a new daemon for the given repository root with a warm FSharpChecker.
    /// When cacheBackend or cacheKeyProvider are None, defaults to FileCheckCache + TimestampCacheKeyProvider.
    let create
        (repoRoot: string)
        (cacheBackend: ICheckCacheBackend option)
        (cacheKeyProvider: ICacheKeyProvider option)
        (fcsSuppressedCodes: int list option)
        =
        let suppressedCodes =
            fcsSuppressedCodes |> Option.defaultValue [ 1182 ] |> Set.ofList

        let checker =
            FSharpChecker.Create(
                projectCacheSize = 200,
                keepAssemblyContents = true,
                keepAllBackgroundResolutions = true,
                parallelReferenceResolution = true
            )

        createWith checker repoRoot cacheBackend cacheKeyProvider suppressedCodes
