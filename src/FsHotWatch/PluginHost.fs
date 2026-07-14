module FsHotWatch.PluginHost

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin

/// Internal messages for the status agent.
[<NoComparison; NoEquality>]
type private StatusMsg =
    | SetStatus of string * PluginStatus
    | GetStatus of string * AsyncReplyChannel<PluginStatus option>
    | GetAllStatuses of AsyncReplyChannel<Map<string, PluginStatus>>

/// Internal state owned by the status agent's loop.
[<NoComparison; NoEquality>]
type private StatusAgentState =
    { Statuses: Map<string, PluginStatus>
      RunStartedAt: Map<string, System.DateTime> }

/// Manages plugin lifecycle, event dispatch, command registration, and status tracking.
type PluginHost
    (
        checker: FSharpChecker,
        repoRoot: string,
        ?reporters: IErrorReporter list,
        ?taskCache: TaskCache.ITaskCache,
        ?fcsSuppressedCodes: Set<int>
    ) =
    let fcsSuppressedCodes = defaultArg fcsSuppressedCodes Set.empty
    let statusChanged = Event<string * PluginStatus>()

    let ledger = ErrorLedger(?reporters = reporters)
    let commands = ConcurrentDictionary<string, CommandHandler>()
    let preprocessors = ConcurrentBag<IFsHotWatchPreprocessor>()
    let fileCommandPatterns = ConcurrentDictionary<string, Watcher.FilePattern>()
    let activity = PluginActivity.State()

    // Read-only project-graph accessor threaded into every plugin's
    // `PluginCtx.ProjectGraph`. Starts as the no-op accessor; the daemon installs
    // the live graph via `SetProjectGraph` before plugins are registered (so the
    // closure captured into each plugin's `services` sees the live one). Volatile
    // because it's set from the daemon construction thread and read from plugin
    // agent threads.
    let mutable projectGraphAccessor = PluginFramework.ProjectGraphAccessor.none

    // Quiescence tracking for `WaitForComplete`. `lastActivityAtTicks` is the
    // UTC ticks of the most recent host-level activity (event dispatch, plugin
    // status change, preprocessor run). `pluginGenerations` is a per-plugin
    // counter that increments on every Idle->Running transition; this lets a
    // waiter detect the "transitioned through Idle between cycles" race in
    // which a plugin happens to be Idle at the snapshot moment but is about to
    // start a new cycle. See `waitForAllTerminal` in Daemon.fs.
    let mutable lastActivityAtTicks = System.DateTime.UtcNow.Ticks
    let pluginGenerations = ConcurrentDictionary<string, int64>()

    // Live "checked files" coverage set: the set of files that currently hold a
    // valid FULL type-check result. Used as a request-time completeness signal
    // (NOT the stale ScanComplete snapshot — incremental checks update it too).
    //
    // Both the cold scan and the incremental batch path flow through
    // `EmitFileChecked`, so adding on a FULL check there covers both. A file
    // change / invalidation flows through `EmitFileChanged`, so removing the
    // changed files there means an edited-but-not-yet-rechecked file correctly
    // counts as unchecked until its next successful FULL check re-adds it.
    //
    // `unit`-valued so it acts as a thread-safe set; membership ops are O(1) —
    // this is the hot check path. The daemon computes the unchecked count as
    // `registered minus checked` via `IsFileChecked`.
    let checkedFiles = ConcurrentDictionary<AbsFilePath, unit>()

    let touchActivity () =
        System.Threading.Volatile.Write(&lastActivityAtTicks, System.DateTime.UtcNow.Ticks)

    let bumpGenerationIfStarting (name: string) (prev: PluginStatus option) (next: PluginStatus) =
        match next with
        | Running _ ->
            let wasNotRunning =
                match prev with
                | Some(Running _) -> false
                | _ -> true

            if wasNotRunning then
                pluginGenerations.AddOrUpdate(name, 1L, (fun _ g -> g + 1L)) |> ignore
        | _ -> ()

    // statusChanged.Trigger dispatch is owned by its own agent: the status
    // agent posts the (name, status) pair here AFTER applying the mutation, and
    // this loop fires the trigger serially, in mutation order, OUTSIDE the
    // status agent's serialization boundary. Two invariants hang off that:
    //   1. A subscriber doing GetAllStatuses (PostAndReply to the status agent)
    //      from inside the trigger callback cannot deadlock — the status agent
    //      is not blocked inside the trigger.
    //   2. By the time a trigger fires, the status agent has already applied
    //      the mutation, so a re-entrant read observes the new value (or newer).
    // A subscriber that throws must not kill this loop (it would silently stop
    // ALL future status notifications), so the exception is logged and the loop
    // continues.
    let triggerAgent =
        MailboxProcessor<string * PluginStatus>.Start(fun inbox ->
            let rec loop () =
                async {
                    let! (name, status) = inbox.Receive()

                    try
                        statusChanged.Trigger(name, status)
                    with ex ->
                        Logging.error "plugin-host" $"OnStatusChanged subscriber failed: %s{ex.ToString()}"

                    return! loop ()
                }

            loop ())

    // Status tracking is owned by a MailboxProcessor: the loop's recursion holds
    // (Statuses, RunStartedAt) and serializes mutations. statusChanged.Trigger
    // fires OUTSIDE this agent's loop (handed to triggerAgent after the
    // mutation is applied) so subscribers can safely call GetAllStatuses
    // re-entrantly without deadlocking - this agent isn't blocked inside the
    // trigger callback.
    let statusAgent =
        MailboxProcessor<StatusMsg>.Start(fun inbox ->
            let rec loop (state: StatusAgentState) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | SetStatus(name, status) ->
                        let prev = Map.tryFind name state.Statuses
                        bumpGenerationIfStarting name prev status
                        touchActivity ()
                        let statuses' = Map.add name status state.Statuses

                        let runStartedAt' =
                            match prev, status with
                            | _, Running since -> Map.add name since state.RunStartedAt
                            | _, Completed at ->
                                let startedAt =
                                    match Map.tryFind name state.RunStartedAt with
                                    | Some s -> s
                                    | None -> at

                                activity.RecordTerminal(name, CompletedRun, startedAt, at)
                                Map.remove name state.RunStartedAt
                            | _, Failed(err, at) ->
                                let startedAt =
                                    match Map.tryFind name state.RunStartedAt with
                                    | Some s -> s
                                    | None -> at

                                activity.RecordTerminal(name, FailedRun err, startedAt, at)
                                Map.remove name state.RunStartedAt
                            | _ -> state.RunStartedAt

                        // Mutation applied — hand the notification to the
                        // trigger agent (fires outside this loop, in order).
                        triggerAgent.Post(name, status)

                        return!
                            loop
                                { Statuses = statuses'
                                  RunStartedAt = runStartedAt' }
                    | GetStatus(name, reply) ->
                        reply.Reply(Map.tryFind name state.Statuses)
                        return! loop state
                    | GetAllStatuses reply ->
                        reply.Reply(state.Statuses)
                        return! loop state
                }

            loop
                { Statuses = Map.empty
                  RunStartedAt = Map.empty })

    let setStatus (name: string) status =
        // Non-blocking by design: setStatus is called from plugin
        // MailboxProcessor agent threads (via ReportStatus inside handler
        // Update bodies). The previous implementation posted a
        // TaskCompletionSource and synchronously blocked on it
        // (`tcs.Task.GetAwaiter().GetResult()`) until the status agent applied
        // the mutation — pinning an agent (thread-pool) thread per concurrent
        // caller, a latent thread-pool-starvation deadlock: enough simultaneous
        // blocked reporters can starve the status agent of the very pool thread
        // it needs to call SetResult. A plain Post keeps every agent thread
        // free; ordering and visibility survive because (a) the status agent's
        // mailbox is FIFO, so any GetStatus/GetAllStatuses posted after this
        // SetStatus observes it, and (b) the statusChanged trigger is fired by
        // the trigger agent only after the mutation is applied.
        statusAgent.Post(SetStatus(name, status))

    let setPluginStatus (name: PluginFramework.PluginName) status =
        setStatus (PluginFramework.PluginName.value name) status

    let registeredPlugins = ResizeArray<PluginFramework.RegisteredPlugin>()

    /// Dispatch an event to all registered plugins (filtering is built into each plugin's Dispatch).
    let dispatchToAll (event: PluginFramework.PluginDispatchEvent) =
        // Mark host activity so quiescence-based waiters don't return prematurely
        // between an event being emitted and its downstream plugin handlers
        // actually processing the event from their mailboxes.
        touchActivity ()

        for p in registeredPlugins do
            p.Dispatch event

    /// Install the read-only project-graph accessor exposed to every plugin via
    /// `PluginCtx.ProjectGraph`. The daemon calls this once, with closures over
    /// its live `ProjectGraph`, before registering plugins. Plugins registered
    /// before or after both see the live accessor because `services.ProjectGraph`
    /// reads through the mutable holder on every call.
    member _.SetProjectGraph(accessor: PluginFramework.ProjectGraphAccessor) = projectGraphAccessor <- accessor

    /// Register a declarative framework-managed plugin handler.
    member this.RegisterHandler<'State, 'Msg>(handler: PluginFramework.PluginHandler<'State, 'Msg>) =
        let services: PluginFramework.PluginHostServices =
            { Checker = checker
              RepoRoot = repoRoot
              ReportStatus =
                fun name status ->
                    setPluginStatus name status
                    let nameStr = PluginFramework.PluginName.value name

                    Logging.debug
                        nameStr
                        (match status with
                         | Idle -> "Idle"
                         | Running _ -> "Running"
                         | Completed _ -> "Completed"
                         | Failed(e, _) -> $"Failed: %s{e.Substring(0, min 80 e.Length)}")
              ReportErrors =
                fun name file entries -> ledger.Report(PluginFramework.PluginName.value name, file, entries)
              ClearErrors = fun name file -> ledger.Clear(PluginFramework.PluginName.value name, file)
              ClearPlugin = fun name -> ledger.ClearPlugin(PluginFramework.PluginName.value name)
              EmitBuildCompleted = fun result -> dispatchToAll (PluginFramework.DispatchBuildCompleted result)
              EmitTestRunStarted = fun started -> dispatchToAll (PluginFramework.DispatchTestRunStarted started)
              EmitTestProgress = fun progress -> dispatchToAll (PluginFramework.DispatchTestProgress progress)
              EmitTestRunCompleted = fun completed -> dispatchToAll (PluginFramework.DispatchTestRunCompleted completed)
              EmitCommandCompleted = fun result -> dispatchToAll (PluginFramework.DispatchCommandCompleted result)
              RegisterCommand = fun cmd -> commands[fst cmd] <- snd cmd
              TaskCache = taskCache
              StartSubtask =
                fun name key label -> activity.StartSubtask(PluginFramework.PluginName.value name, key, label)
              UpdateSubtask =
                fun name key label -> activity.UpdateSubtask(PluginFramework.PluginName.value name, key, label)
              EndSubtask = fun name key -> activity.EndSubtask(PluginFramework.PluginName.value name, key)
              Log =
                fun name msg ->
                    let nameStr = PluginFramework.PluginName.value name
                    activity.Log(nameStr, msg)
                    Logging.info nameStr msg
              SetSummary = fun name s -> activity.SetSummary(PluginFramework.PluginName.value name, s)
              SetNextTerminalOutcome =
                fun name outcome -> activity.SetNextTerminalOutcome(PluginFramework.PluginName.value name, outcome)
              FcsSuppressedCodes = fcsSuppressedCodes
              // Read through the mutable holder so a plugin registered before the
              // daemon installed the live graph still sees it (the accessor record
              // is captured at registration, but its three closures all re-read
              // `projectGraphAccessor` each call below).
              ProjectGraph =
                { GetAllProjects = fun () -> projectGraphAccessor.GetAllProjects()
                  GetTransitiveDependentProjects = fun p -> projectGraphAccessor.GetTransitiveDependentProjects p
                  GetProjectReferences = fun p -> projectGraphAccessor.GetProjectReferences p
                  GetCanonicalDllPath = fun p -> projectGraphAccessor.GetCanonicalDllPath p } }

        let plugin = PluginFramework.registerHandler services handler

        if registeredPlugins |> Seq.exists (fun p -> p.Name = plugin.Name) then
            Logging.warn
                "plugin-host"
                $"Plugin name '%s{PluginFramework.PluginName.value plugin.Name}' is already registered — commands and status may be overwritten"

        setPluginStatus plugin.Name Idle
        registeredPlugins.Add(plugin)

    /// Register a preprocessor (runs before events are dispatched).
    member _.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) =
        setStatus preprocessor.Name Idle
        preprocessors.Add(preprocessor)

    /// Run all preprocessors on the given files. Returns files that were modified.
    member _.RunPreprocessors(files: string list) : string list =
        let mutable modifiedFiles = []

        for preprocessor in preprocessors do
            setStatus preprocessor.Name (Running(since = System.DateTime.UtcNow))

            try
                let modified = preprocessor.Process files repoRoot
                modifiedFiles <- modified @ modifiedFiles
                setStatus preprocessor.Name (Completed(System.DateTime.UtcNow))
            with ex ->
                setStatus preprocessor.Name (Failed(ex.ToString(), System.DateTime.UtcNow))

        modifiedFiles |> List.distinct

    /// Emit a file change event to all registered plugins.
    ///
    /// Side effect on the live coverage set: each changed source/project file is
    /// removed from `checkedFiles` so it counts as unchecked until its next
    /// successful FULL check re-adds it (via `EmitFileChecked`). A `SolutionChanged`
    /// can add, remove, or retarget projects — invalidating every file's options —
    /// so nothing is treated as known-checked until the following re-scan re-adds
    /// each file. Clearing the whole set also prevents removed files from lingering.
    member _.EmitFileChanged(change: FileChangeKind) =
        match change with
        | SourceChanged files
        | ProjectChanged files ->
            for f in files do
                checkedFiles.TryRemove(AbsFilePath.create f) |> ignore
        | SolutionChanged -> checkedFiles.Clear()

        dispatchToAll (PluginFramework.DispatchFileChanged change)

    /// Emit a build completed event to all registered plugins.
    member _.EmitBuildCompleted(result: BuildResult) =
        dispatchToAll (PluginFramework.DispatchBuildCompleted result)

    /// Report errors to the ledger on behalf of a named source (e.g., "fcs").
    member _.ReportErrors(pluginName: string, filePath: string, entries: ErrorEntry list, ?version: int64) =
        ledger.Report(pluginName, filePath, entries, ?version = version)

    /// Clear errors in the ledger for a named source + file.
    member _.ClearErrors(pluginName: string, filePath: string, ?version: int64) =
        ledger.Clear(pluginName, filePath, ?version = version)

    /// Emit a file checked event to all registered plugins.
    ///
    /// Side effect on the live coverage set: a FULL check result adds the file to
    /// `checkedFiles`. A ParseOnly/aborted check does NOT count as checked — the
    /// file stays unchecked until a full check succeeds.
    member _.EmitFileChecked(result: FileCheckResult) =
        match result.CheckResults with
        | FullCheck _ -> checkedFiles[result.File] <- ()
        | ParseOnly -> ()

        dispatchToAll (PluginFramework.DispatchFileChecked result)

    /// True if `file` currently holds a valid FULL type-check result (i.e. a
    /// `FullCheck` was emitted for it via `EmitFileChecked` and it hasn't been
    /// invalidated by a subsequent `EmitFileChanged`). The daemon's
    /// `GetUncheckedCount` is `registered files minus those for which this is
    /// true`.
    member _.IsFileChecked(file: AbsFilePath) : bool = checkedFiles.ContainsKey(file)

    /// Count of files that currently hold a valid FULL type-check result.
    /// Diagnostic counterpart to `IsFileChecked`.
    member _.CheckedFileCount() : int = checkedFiles.Count

    /// Emit a batch-checked event to all registered plugins. Fired by the
    /// daemon once after a defined cohort of `FileChecked` events has finished
    /// (boot scan or in-session debounce batch).
    member _.EmitBatchChecked(batch: BatchChecked) =
        dispatchToAll (PluginFramework.DispatchBatchChecked batch)

    /// Emit the start of a test run to all registered plugins.
    member _.EmitTestRunStarted(started: TestRunStarted) =
        dispatchToAll (PluginFramework.DispatchTestRunStarted started)

    /// Emit progress for a running test run (one or more groups just completed).
    member _.EmitTestProgress(progress: TestProgress) =
        dispatchToAll (PluginFramework.DispatchTestProgress progress)

    /// Emit the end of a test run.
    member _.EmitTestRunCompleted(completed: TestRunCompleted) =
        dispatchToAll (PluginFramework.DispatchTestRunCompleted completed)

    /// Emit a command completed event to all registered plugins.
    member _.EmitCommandCompleted(result: CommandCompletedResult) =
        dispatchToAll (PluginFramework.DispatchCommandCompleted result)

    /// Run a registered command by name. Returns None if the command is unknown.
    member _.RunCommand(name: string, args: string array) : Async<string option> =
        async {
            match commands.TryGetValue(name) with
            | true, handler ->
                let! result = handler args
                return Some result
            | false, _ -> return None
        }

    /// Get the status of a specific plugin by name.
    member _.GetStatus(pluginName: string) : PluginStatus option =
        statusAgent.PostAndReply(fun ch -> GetStatus(pluginName, ch))

    /// Get all plugin statuses as an immutable map.
    member _.GetAllStatuses() : Map<string, PluginStatus> =
        statusAgent.PostAndReply(fun ch -> GetAllStatuses ch)

    /// UTC timestamp of the most recent host activity: an event dispatch or a
    /// plugin status transition. Used by `WaitForComplete` to enforce a
    /// quiescence window so a plugin that's about to start a new cycle isn't
    /// missed when its predecessor's event has been emitted but not yet
    /// processed from the plugin's mailbox.
    member _.LastActivityAt() : System.DateTime =
        System.DateTime(System.Threading.Volatile.Read(&lastActivityAtTicks), System.DateTimeKind.Utc)

    /// Per-plugin work-cycle generation counter. Incremented every time a
    /// plugin transitions from a non-Running status (Idle / Completed / Failed)
    /// into Running. A waiter can snapshot the generations at call time and
    /// detect "the plugin started a new cycle since I started waiting".
    /// Plugins that have never run report generation 0.
    member _.WorkCycleGenerations() : Map<string, int64> =
        pluginGenerations |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// True if any registered plugin has work in flight: events queued in its
    /// mailbox, an event being processed, or an exclusive background run
    /// (`RunExclusive`) anywhere between claim and completion-handled. The
    /// status agent reflects only what handlers have explicitly reported; this
    /// catches both the "event posted but handler hasn't reported Running yet"
    /// gap and the "handler returned but its background run is still
    /// executing" gap (AUTOMATION-95/99).
    member _.AnyPluginBusy() : bool =
        registeredPlugins |> Seq.exists (fun p -> p.IsBusy())

    member _.StartSubtask(pluginName: string, key: string, label: string) =
        activity.StartSubtask(pluginName, key, label)

    member _.UpdateSubtask(pluginName: string, key: string, label: string) =
        activity.UpdateSubtask(pluginName, key, label)

    member _.EndSubtask(pluginName: string, key: string) = activity.EndSubtask(pluginName, key)

    /// Append an activity log line and route to Logging.info.
    member _.LogActivity(pluginName: string, message: string) =
        activity.Log(pluginName, message)
        Logging.info pluginName message

    member _.SetSummary(pluginName: string, summary: string) =
        activity.SetSummary(pluginName, summary)

    member _.GetActivitySnapshot(pluginName: string) : PluginActivity.Snapshot = activity.GetSnapshot(pluginName)

    /// Build an IActivitySink bound to a plugin name. Used by the check pipeline.
    member this.ActivitySinkFor(pluginName: string) : PluginActivity.IActivitySink =
        { new PluginActivity.IActivitySink with
            member _.StartSubtask(key, label) =
                this.StartSubtask(pluginName, key, label)

            member _.UpdateSubtask(key, label) =
                this.UpdateSubtask(pluginName, key, label)

            member _.EndSubtask(key) = this.EndSubtask(pluginName, key)
            member _.Log(msg) = this.LogActivity(pluginName, msg)
            member _.SetSummary(s) = this.SetSummary(pluginName, s) }

    member _.GetSubtasks(pluginName: string) : Subtask list = activity.GetSubtasks(pluginName)

    member _.GetActivityTail(pluginName: string) : string list = activity.GetActivityTail(pluginName)

    member _.GetHistory(pluginName: string) : RunRecord list = activity.GetHistory(pluginName)

    /// Get all errors grouped by file path.
    member _.GetErrors() = ledger.GetAll()

    /// Get errors for a specific plugin only.
    member _.GetErrorsByPlugin(name) = ledger.GetByPlugin(name)

    /// Per-plugin error/warning counts from the ledger, in a single roundtrip.
    member _.GetDiagnosticCountsByPlugin() = ledger.GetCountsByPlugin()

    /// True if any failing entries exist (Error, or Warning when warningsAreFailures=true).
    member _.HasFailingReasons(warningsAreFailures: bool) =
        ledger.HasFailingReasons(warningsAreFailures)

    /// Get all failing entries grouped by file path, filtered by severity.
    member _.FailingReasons(warningsAreFailures: bool) =
        ledger.FailingReasons(warningsAreFailures)

    /// Event fired when any plugin's status changes.
    member _.OnStatusChanged = statusChanged.Publish

    /// Clear all task cache entries.
    member _.ClearTaskCache() =
        match taskCache with
        | Some c -> c.Clear()
        | None -> ()

    /// Clear task cache entries for a specific plugin.
    member _.ClearTaskCachePlugin(plugin: string) =
        match taskCache with
        | Some c -> c.ClearPlugin(plugin)
        | None -> ()

    /// Clear task cache entries for a specific file.
    member _.ClearTaskCacheFile(file: string) =
        match taskCache with
        | Some c -> c.ClearFile(file)
        | None -> ()

    /// Clear a specific plugin+file task cache entry.
    member _.ClearTaskCachePluginFile(plugin: string, file: string) =
        match taskCache with
        | Some c -> c.ClearPluginFile plugin file
        | None -> ()

    /// Register a FileCommandPlugin's parsed file pattern by plugin name.
    /// Used by `RerunFileCommandPlugin` to synthesize a fake file event whose
    /// path matches the plugin's filter.
    member _.RegisterFileCommandPattern(name: string, pattern: Watcher.FilePattern) =
        fileCommandPatterns[name] <- pattern

    /// Look up a registered FileCommandPlugin pattern by plugin name.
    member _.GetFileCommandPattern(name: string) : Watcher.FilePattern option =
        match fileCommandPatterns.TryGetValue(name) with
        | true, p -> Some p
        | false, _ -> None

    /// Force a specific FileCommandPlugin to re-run. Clears the plugin's task
    /// cache and emits a synthetic FileChanged event whose path matches the
    /// plugin's registered pattern — other plugins cache-hit (commit unchanged),
    /// only the target plugin sees a cache miss.
    ///
    /// Returns `Error` if the plugin has no registered pattern (which is the
    /// case for non-FileCommand plugins and for FileCommand plugins configured
    /// only with `afterTests`). The caller is responsible for waiting until
    /// plugins settle before inspecting status.
    member this.RerunFileCommandPlugin(name: string) : Result<unit, string> =
        match this.GetFileCommandPattern(name) with
        | None ->
            Result.Error
                $"Plugin '%s{name}' has no registered file pattern (only FileCommand plugins with a pattern support rerun)"
        | Some pattern ->
            this.ClearTaskCachePlugin(name)
            this.EmitFileChanged(SourceChanged [ Watcher.FilePattern.syntheticPath pattern ])
            Result.Ok()

    /// Create a new PluginHost.
    /// Tear down all plugins that have a Teardown function.
    member _.Teardown() =
        for p in registeredPlugins do
            match p.Teardown with
            | Some teardown ->
                // F14 (audit 2026-05-02): plugin Teardown is a third-party-
                // extension boundary; the broad catch is what keeps one
                // misbehaving plugin from preventing the rest from cleaning
                // up. Log ex.ToString() so the type and stack trace are
                // preserved for diagnosing the offending plugin.
                try
                    teardown ()
                with ex ->
                    Logging.error (PluginFramework.PluginName.value p.Name) $"Teardown failed: %s{ex.ToString()}"
            | None -> ()

        fileCommandPatterns.Clear()

    static member create (checker: FSharpChecker) (repoRoot: string) = PluginHost(checker, repoRoot)
