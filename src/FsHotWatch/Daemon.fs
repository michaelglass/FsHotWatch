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

/// Parse #nowarn directives from F# source text, returning the set of suppressed warning codes.
/// Workaround for https://github.com/dotnet/fsharp/issues/9796 — FCS TransparentCompiler
/// ignores #nowarn directives for warnaserror codes. When that issue is resolved, this
/// function and its callers can be removed.
let parseNowarnCodes (source: string) : Set<int> =
    source.Split('\n')
    |> Array.filter (fun line -> line.TrimStart().StartsWith("#nowarn"))
    |> Array.collect (fun line -> line.TrimStart().Split('"'))
    |> Array.choose (fun part ->
        match System.Int32.TryParse(part) with
        | true, code -> Some code
        | _ -> None)
    |> Set.ofArray

/// Extract FCS diagnostics from check results and report to the error ledger.
/// Reports all severity levels (Error, Warning, Info, Hidden) with configurable
/// suppressed diagnostic codes.
let private reportFcsDiagnostics (suppressedCodes: Set<int>) (host: PluginHost) (checkResult: Events.FileCheckResult) =
    match checkResult.CheckResults with
    | ParseOnly -> ()
    | FullCheck checkResults ->
        let mapSeverity =
            function
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error -> DiagnosticSeverity.Error
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning -> DiagnosticSeverity.Warning
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Info -> DiagnosticSeverity.Info
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Hidden -> DiagnosticSeverity.Hint

        // Merge global suppressed codes with per-file #nowarn directives.
        // Workaround for https://github.com/dotnet/fsharp/issues/9796
        let nowarnCodes = parseNowarnCodes checkResult.Source
        let allSuppressed = Set.union suppressedCodes nowarnCodes

        let diagnostics =
            checkResults.Diagnostics
            |> Array.choose (fun d ->
                if allSuppressed.Contains(d.ErrorNumber) then
                    None
                else
                    Some
                        { Message = d.Message
                          Severity = mapSeverity d.Severity
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
                let absProject = AbsProjectPath.create proj.ProjectFileName
                let sourceFiles = proj.SourceFiles |> List.map AbsFilePath.create

                let references =
                    proj.ReferencedProjects
                    |> List.map (fun r -> AbsProjectPath.create r.ProjectFileName)

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
            host.ClearErrors("fcs", AbsFilePath.value file)

        if not removedFiles.IsEmpty then
            Logging.info logTag $"Cleared errors for %d{removedFiles.Count} removed files"

        return removedFiles
    }

/// Manages TaskCompletionSource instances for signal-based WaitForScan.
[<NoComparison; NoEquality>]
type private ScanSignalMsg =
    | WaitFor of afterGen: int64 * TaskCompletionSource<unit>
    | Signal of newGen: int64

type ScanSignal(?cancellationToken: CancellationToken) =
    let agent =
        MailboxProcessor.Start(
            (fun inbox ->
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

                loop []),
            ?cancellationToken = cancellationToken
        )

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

/// Dependencies for processBatch, bundled to avoid a long closure capture list.
[<NoComparison; NoEquality>]
type internal BatchContext =
    { Host: PluginHost
      InvalidateFcs: (unit -> unit) option
      RepoRoot: string
      Loader: IWorkspaceLoader
      Graph: ProjectGraph.ProjectGraph
      Pipeline: CheckPipeline
      DaemonCt: CancellationToken ref
      FcsSuppressedCodes: Set<int> }

/// Process a batch of debounced file changes: filter, re-discover projects if needed,
/// run preprocessors, emit events, and check files.
let internal processBatch (ctx: BatchContext) (changes: FileChangeKind list) (suppressed: Set<string>) =
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
                let changed = Watcher.hasContentChanged f

                if not changed then
                    Logging.debug "daemon" $"content unchanged: %s{f}"

                changed)

        let projFilesChanged =
            projFiles
            |> List.distinct
            |> List.filter (fun f ->
                let changed = Watcher.hasContentChanged f

                if not changed then
                    Logging.debug "daemon" $"content unchanged: %s{f}"

                changed)

        if hasSolution then
            ctx.Host.EmitFileChanged(SolutionChanged solutionFile.Value)

        if not projFilesChanged.IsEmpty || hasSolution then
            Logging.info "daemon" "Project/solution change detected — re-discovering projects"

            ctx.InvalidateFcs |> Option.iter (fun invalidate -> invalidate ())

            let! _ = rediscoverAndClearRemoved ctx.RepoRoot ctx.Loader ctx.Graph ctx.Pipeline ctx.Host "daemon"

            Logging.info
                "daemon"
                $"Re-discovery complete: %d{ctx.Graph.GetAllProjects().Length} projects, %d{ctx.Pipeline.GetAllRegisteredFiles().Length} files"

            if not projFilesChanged.IsEmpty then
                ctx.Host.EmitFileChanged(ProjectChanged projFilesChanged)

        if not allSourceFiles.IsEmpty then
            let modifiedByPreprocessors = ctx.Host.RunPreprocessors(allSourceFiles)

            let newSuppressed =
                Set.union remainingSuppressed (Set.ofList modifiedByPreprocessors)

            let absSourceFiles = allSourceFiles |> List.map AbsFilePath.create

            let changedProjects =
                absSourceFiles
                |> List.collect (fun f -> ctx.Graph.GetProjectsForFile(f))
                |> List.distinct

            let changedProjectSet = Set.ofList changedProjects

            let dependentProjectFiles =
                changedProjects
                |> List.collect (fun p -> ctx.Graph.GetTransitiveDependents(p))
                |> List.distinct
                |> List.filter (fun p -> not (Set.contains p changedProjectSet))
                |> List.collect (fun proj -> ctx.Graph.GetSourceFiles(proj))
                |> List.map AbsFilePath.value

            let allFilesToCheck = (allSourceFiles @ dependentProjectFiles) |> List.distinct

            ctx.Host.EmitFileChanged(SourceChanged allFilesToCheck)

            Logging.debug "daemon" $"Checking %d{allFilesToCheck.Length} files after change"
            let mutable checkedFiles = Set.empty
            let filesToCheckSet = allFilesToCheck |> Set.ofList
            let tiers = ctx.Graph.GetParallelTiers()

            let emitResults (results: FileCheckResult option array) =
                for result in results do
                    match result with
                    | Some checkResult ->
                        Logging.debug "daemon" $"EmitFileChecked: %s{Path.GetFileName(checkResult.File)}"
                        ctx.Host.EmitFileChecked(checkResult)
                        reportFcsDiagnostics ctx.FcsSuppressedCodes ctx.Host checkResult
                    | None -> ()

            for tier in tiers do
                let tierChecks = ResizeArray<Async<FileCheckResult option>>()

                for proj in tier do
                    let projPath = AbsProjectPath.value proj

                    let projFiles =
                        ctx.Graph.GetSourceFiles(proj)
                        |> List.map AbsFilePath.value
                        |> List.filter filesToCheckSet.Contains

                    checkedFiles <- Set.union checkedFiles (Set.ofList projFiles)

                    match ctx.Pipeline.GetProjectOptions(projPath) with
                    | Some options ->
                        for file in projFiles do
                            tierChecks.Add(ctx.Pipeline.CheckFileWithOptions(file, options, ctx.DaemonCt.Value))
                    | None ->
                        for file in projFiles do
                            tierChecks.Add(ctx.Pipeline.CheckFile(file, ctx.DaemonCt.Value))

                let! results = tierChecks |> Seq.toList |> Async.Parallel
                emitResults results

            // Check files not belonging to any project (e.g. standalone .fsx files)
            let uncovered = Set.difference filesToCheckSet checkedFiles |> Set.toList

            if not uncovered.IsEmpty then
                let! results =
                    uncovered
                    |> List.map (fun file -> ctx.Pipeline.CheckFile(file, ctx.DaemonCt.Value))
                    |> Async.Parallel

                emitResults results

            return newSuppressed
        else
            return remainingSuppressed
    }

/// Wait for all plugins to reach a terminal state with 1-second stability confirmation.
/// Times out with TimeoutException after the specified timeout.
let internal waitForAllTerminal (host: PluginHost) (timeout: System.TimeSpan) () : Task<unit> =
    let deadline = System.DateTime.UtcNow + timeout
    let mutable lastLogTime = System.DateTime.UtcNow

    let getRunningPlugins () =
        host.GetAllStatuses()
        |> Map.toList
        |> List.choose (fun (name, s) ->
            match s with
            | Running since -> Some $"%s{name} (since %O{since})"
            | _ -> None)

    let logRunningPlugins () =
        let now = System.DateTime.UtcNow

        if (now - lastLogTime).TotalSeconds >= 10.0 then
            lastLogTime <- now

            match getRunningPlugins () with
            | [] -> Logging.info "wait" "All plugins terminal, waiting for stability confirmation..."
            | plugins ->
                let joined = plugins |> String.concat ", "
                Logging.info "wait" $"Waiting for plugins: %s{joined}"

    let formatTimeoutDetail () =
        match getRunningPlugins () with
        | [] -> "all terminal but stability check failed"
        | running ->
            let joined = running |> String.concat ", "
            $"still running: %s{joined}"

    let rec loop () =
        async {
            if timeout <> System.TimeSpan.MaxValue && System.DateTime.UtcNow >= deadline then
                let detail = formatTimeoutDetail ()

                raise (System.TimeoutException($"WaitForComplete timed out after %O{timeout} — %s{detail}"))

            let statuses = host.GetAllStatuses()

            if allTerminal statuses then
                // Stability check: wait 1 second, then confirm still terminal
                do! Async.Sleep 1000
                let final = host.GetAllStatuses()

                if allTerminal final then return () else return! loop ()
            else
                logRunningPlugins ()
                do! Async.Sleep 100
                return! loop ()
        }

    loop () |> Async.StartAsTask

/// The daemon ties together a warm FSharpChecker, file watcher, check pipeline, and plugin host.
/// It runs until the provided CancellationToken is cancelled.
type Daemon
    internal
    (
        host: PluginHost,
        watcher: FileWatcher,
        pipeline: CheckPipeline,
        graph: ProjectGraph,
        repoRoot: string,
        workspaceLoader: IWorkspaceLoader,
        scanAgent: ScanAgent,
        cancellationTokenRef: CancellationToken ref,
        ready: ManualResetEventSlim,
        scanSignal: ScanSignal,
        _jjGuard: JjHelper.JjScanGuard option,
        fcsSuppressedCodes: Set<int>,
        lifetime: CancellationTokenSource,
        formatAllFn: (unit -> Async<string>) option
    ) =

    let mutable disposed = false

    /// The plugin host that manages plugin lifecycle and event dispatch.
    member _.Host = host

    /// The check pipeline that performs incremental file checking.
    member _.Pipeline = pipeline

    /// The project dependency graph.
    member _.Graph = graph

    /// The repository root directory.
    member _.RepoRoot = repoRoot

    /// Signalled when the daemon is ready to accept file change events.
    member _.Ready = ready

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                lifetime.Cancel()
                lifetime.Dispose()
                (watcher :> IDisposable).Dispose()

    /// Register a declarative framework-managed plugin handler.
    member _.RegisterHandler<'State, 'Msg>(handler: PluginFramework.PluginHandler<'State, 'Msg>) =
        host.RegisterHandler(handler)

    /// Register a preprocessor (e.g., formatter) that runs before events are dispatched.
    member _.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) = host.RegisterPreprocessor(preprocessor)

    /// Register a project's options so its files can be checked incrementally.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        pipeline.RegisterProject(projectPath, options)

    /// Get current scan state.
    member _.GetScanState() = getScanStatus scanAgent

    /// Get current scan generation (incremented after each completed scan).
    member _.GetScanGeneration() = getScanGeneration scanAgent

    /// Set scan state (internal, for testing).
    member internal _.SetScanState(state: ScanState) = setScanStatus scanAgent state

    /// Invalidate cache for a file, re-check it, and emit results to plugins.
    member _.InvalidateAndRecheck(filePath: string) =
        async {
            pipeline.InvalidateFile(filePath)
            let! result = pipeline.CheckFile(filePath)

            match result with
            | Some checkResult ->
                host.EmitFileChecked(checkResult)
                reportFcsDiagnostics fcsSuppressedCodes host checkResult

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
    member _.ScanAll(?force: bool) =
        async {
            let force = defaultArg force false
            let! ct = Async.CancellationToken

            do! requestScan scanAgent force ct
        }

    /// Run a single full scan in-process without watcher or IPC.
    /// Discovers projects, scans all files, waits for plugins to complete, returns statuses.
    member this.RunOnce() =
        async {
            do! this.ScanAll(force = true)

            do!
                waitForAllTerminal host (System.TimeSpan.FromMinutes(30.0)) ()
                |> Async.AwaitTask

            return host.GetAllStatuses()
        }

    /// Run the daemon until cancellation is requested.
    member this.Run(cancellationToken: CancellationToken) =
        async {
            ready.Set()

            try
                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg = cancellationToken.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask
            finally
                ready.Dispose()
                (this :> IDisposable).Dispose()
        }

    /// Discover .fsproj files in src/ and tests/ and register them with the pipeline.
    member _.DiscoverAndRegisterProjects() =
        discoverAndRegisterProjects repoRoot workspaceLoader graph pipeline

    /// Format scan state as a human-readable string.
    member _.FormatScanStatus() =
        let scanState = getScanStatus scanAgent

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
            try
                let onScan (force: bool) =
                    Async.StartAsTask(this.ScanAll(force = force)) |> ignore

                let triggerBuild () =
                    async {
                        let files = pipeline.GetAllRegisteredFiles()

                        if not files.IsEmpty then
                            host.EmitFileChanged(SourceChanged files)
                    }

                let formatAll () =
                    match formatAllFn with
                    | Some fn -> fn ()
                    | None ->
                        async {
                            let files = pipeline.GetAllRegisteredFiles()
                            let modified = host.RunPreprocessors(files)
                            return $"formatted %d{modified.Length} files"
                        }

                let rpcConfig: DaemonRpcConfig =
                    { Host = host
                      RequestShutdown = fun () -> cts.Cancel()
                      RequestScan = onScan
                      GetScanStatus = this.FormatScanStatus
                      GetScanGeneration = this.GetScanGeneration
                      TriggerBuild = triggerBuild
                      FormatAll = formatAll
                      WaitForScanGeneration =
                        fun afterGen -> scanSignal.WaitForGeneration(afterGen, this.GetScanGeneration())
                      WaitForAllTerminal = waitForAllTerminal host (System.TimeSpan.FromMinutes(30.0))
                      InvalidateAndRecheck = fun filePath -> this.InvalidateAndRecheck(filePath) }

                let ipcTask = Async.StartAsTask(IpcServer.start pipeName rpcConfig cts)

                cancellationTokenRef.Value <- cts.Token
                ready.Set()

                // Register cancellation before starting the scan so that cancellation during
                // the initial scan unblocks RunWithIpc immediately rather than waiting for the
                // scan to complete. This prevents test-process hangs when cts is cancelled while
                // the scan is still running (e.g. under thread-pool contention in test suites).
                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()
                use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)

                // force bypasses jj guard since plugins have no state from previous daemon runs.
                // Race against cancellation so a slow scan doesn't block shutdown.
                let scanTask = Async.StartAsTask(this.ScanAll(force = true))

                do!
                    [| scanTask :> System.Threading.Tasks.Task
                       tcs.Task :> System.Threading.Tasks.Task |]
                    |> System.Threading.Tasks.Task.WhenAny
                    |> Async.AwaitTask
                    |> Async.Ignore

                do! tcs.Task |> Async.AwaitTask

                try
                    ipcTask.Wait(System.TimeSpan.FromSeconds(1.0)) |> ignore
                with ex ->
                    Logging.debug "daemon" $"IPC shutdown: %s{ex.Message}"
            finally
                ready.Dispose()
                (this :> IDisposable).Dispose()
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
                            |> List.map AbsFilePath.create
                            |> List.collect (fun f -> graph.GetProjectsForFile(f))
                            |> List.distinct
                            |> List.collect (fun p -> graph.GetTransitiveDependents(p))
                            |> List.distinct
                            |> List.collect (fun proj -> graph.GetSourceFiles(proj))
                            |> List.map AbsFilePath.value

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
                    let tierChecks = ResizeArray<Async<FileCheckResult option>>()

                    for proj in tier do
                        let projPath = AbsProjectPath.value proj

                        let projFiles =
                            graph.GetSourceFiles(proj)
                            |> List.map AbsFilePath.value
                            |> List.filter filesToCheckSet.Contains

                        skippedCount <- skippedCount + ((graph.GetSourceFiles(proj) |> List.length) - projFiles.Length)

                        match pipeline.GetProjectOptions(projPath) with
                        | Some options ->
                            for file in projFiles do
                                tierChecks.Add(pipeline.CheckFileWithOptions(file, options, ct))
                        | None ->
                            for file in projFiles do
                                tierChecks.Add(pipeline.CheckFile(file, ct))

                    let! results = tierChecks |> Seq.toList |> Async.Parallel

                    for result in results do
                        match result with
                        | Some checkResult ->
                            checkedCount <- checkedCount + 1
                            host.EmitFileChecked(checkResult)
                            reportFcsDiagnostics fcsSuppressedCodes host checkResult
                        | None -> ()

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
        let lifetime = new CancellationTokenSource()

        try
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

            let batchCtx: BatchContext =
                { Host = host
                  InvalidateFcs =
                    if isNull (box checker) then
                        None
                    else
                        Some(fun () ->
                            checker.InvalidateAll()
                            checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients())
                  RepoRoot = repoRoot
                  Loader = loader
                  Graph = graph
                  Pipeline = pipeline
                  DaemonCt = daemonCtRef
                  FcsSuppressedCodes = fcsSuppressedCodes }

            let formatAllAndSuppress (suppressed: Set<string>) (replyChannel: AsyncReplyChannel<string>) =
                let files = pipeline.GetAllRegisteredFiles()
                let modified = host.RunPreprocessors(files)
                let newSuppressed = Set.union suppressed (Set.ofList modified)
                replyChannel.Reply($"formatted %d{modified.Length} files")
                newSuppressed

            let changeAgent =
                MailboxProcessor<Choice<FileChangeKind, AsyncReplyChannel<string>>>
                    .Start(
                        (fun inbox ->
                            let rec idle (suppressed: Set<string>) =
                                async {
                                    let! msg = inbox.Receive()

                                    match msg with
                                    | Choice2Of2 replyChannel ->
                                        let newSuppressed = formatAllAndSuppress suppressed replyChannel
                                        return! idle newSuppressed
                                    | Choice1Of2 change ->
                                        let delayMs = delayForChange change
                                        return! debouncing [ change ] delayMs suppressed
                                }

                            and debouncing (pending: FileChangeKind list) (delayMs: int) (suppressed: Set<string>) =
                                async {
                                    let! msg = inbox.TryReceive(delayMs)

                                    match msg with
                                    | Some(Choice1Of2 change) ->
                                        let newDelay = max delayMs (delayForChange change)
                                        return! debouncing (change :: pending) newDelay suppressed
                                    | Some(Choice2Of2 replyChannel) ->
                                        try
                                            let! newSuppressed = processBatch batchCtx (List.rev pending) suppressed
                                            let finalSuppressed = formatAllAndSuppress newSuppressed replyChannel
                                            return! idle finalSuppressed
                                        with ex ->
                                            Logging.error "daemon" $"processChanges failed: %s{ex.ToString()}"
                                            replyChannel.Reply("format failed")
                                            return! idle suppressed
                                    | None ->
                                        // Debounce expired — process batch
                                        try
                                            let! newSuppressed = processBatch batchCtx (List.rev pending) suppressed
                                            return! idle newSuppressed
                                        with ex ->
                                            Logging.error "daemon" $"processChanges failed: %s{ex.ToString()}"
                                            return! idle suppressed
                                }

                            idle Set.empty),
                        cancellationToken = lifetime.Token
                    )

            let onChange change =
                Logging.debug "watcher" $"%O{change}"
                changeAgent.Post(Choice1Of2 change)

            let watcher = FileWatcher.create repoRoot onChange None

            let jjGuard =
                match cacheKeyProvider with
                | Some(:? JjCacheKeyProvider) -> Some(JjHelper.JjScanGuard(repoRoot))
                | _ -> None

            let scanSignal = ScanSignal(cancellationToken = lifetime.Token)

            // Mutable ref allows the agent loop (which starts immediately) to
            // update volatile fields on the wrapper created after MailboxProcessor.Start.
            let scanAgentRef: ScanAgent option ref = ref None

            let scanMailbox =
                MailboxProcessor.Start(
                    (fun inbox ->
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
                              LastFingerprint = Set.empty }),
                    cancellationToken = lifetime.Token
                )

            let scanAgentWrapper = createScanAgent scanMailbox
            scanAgentRef.Value <- Some scanAgentWrapper

            let formatAllViaAgent () =
                changeAgent.PostAndAsyncReply(Choice2Of2)

            new Daemon(
                host,
                watcher,
                pipeline,
                graph,
                repoRoot,
                loader,
                scanAgentWrapper,
                daemonCtRef,
                new ManualResetEventSlim(false),
                scanSignal,
                jjGuard,
                fcsSuppressedCodes,
                lifetime,
                Some formatAllViaAgent
            )
        with _ ->
            lifetime.Dispose()
            reraise ()

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
                parallelReferenceResolution = true,
                useTransparentCompiler = true
            )

        createWith checker repoRoot cacheBackend cacheKeyProvider suppressedCodes
