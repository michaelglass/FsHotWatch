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

/// What one pass of every registered preprocessor did, for the caller that needs more
/// than the suppression set: `fshw format` renders `Lines` as its reply and `Refused`
/// as its failure, so the reply names the formatter that ran — or the reason none did.
type PreprocessorsRun =
    {
        /// Union of every preprocessor's `Modified` — the daemon's watcher-suppression set.
        Modified: string list
        /// One status line per preprocessor that ran (`format: rewrote 0 of 12 file(s) — …`).
        Lines: string list
        /// What each preprocessor that ran actually ran — its `PreprocessResult.Evidence`.
        Evidence: string list
        /// `(name, reason)` for every preprocessor that could not run at all.
        Refused: (string * string) list
    }

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
    let normalizedRepoRoot = System.IO.Path.GetFullPath(repoRoot)
    let commands = ConcurrentDictionary<string, CommandHandler>()
    let preprocessors = ConcurrentBag<IFsHotWatchPreprocessor>()
    let fileCommandPatterns = ConcurrentDictionary<string, Watcher.FilePattern>()
    let activity = PluginActivity.State()
    let sharedRunScheduler = PluginFramework.SharedRunScheduler()

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

    // Live "checked files" coverage set: the files that currently hold a valid FULL
    // type-check result. A request-time completeness signal, NOT the stale
    // ScanComplete snapshot — incremental checks update it too.
    //
    // Both the cold scan and the incremental batch path flow through
    // `EmitFileChecked`, and invalidation flows through `EmitFileChanged`, so an
    // edited-but-not-yet-rechecked file counts as unchecked until its next
    // successful FULL check re-adds it.
    //
    // `unit`-valued so it acts as a thread-safe set; membership ops are O(1) —
    // this is the hot check path.
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
    // the statuses and serializes mutations. statusChanged.Trigger fires OUTSIDE
    // this loop, via `triggerAgent` — see its two invariants above.
    let statusAgent =
        MailboxProcessor<StatusMsg>.Start(fun inbox ->
            let rec loop (statuses: Map<string, PluginStatus>) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | SetStatus(name, status) ->
                        let prev = Map.tryFind name statuses
                        bumpGenerationIfStarting name prev status

                        touchActivity ()

                        // Every terminal status carries its verdict, so the run record
                        // derives startedAt from it rather than guessing.
                        match status with
                        | Completed(at, verdict) ->
                            activity.RecordTerminal(name, CompletedRun, at - verdict.Elapsed, at)
                        | Failed(err, at, verdict) ->
                            activity.RecordTerminal(name, FailedRun err, at - verdict.Elapsed, at)
                        | Idle
                        | Running _ -> ()

                        // Mutation applied — hand the notification to the
                        // trigger agent (fires outside this loop, in order).
                        triggerAgent.Post(name, status)

                        return! loop (Map.add name status statuses)
                    | GetStatus(name, reply) ->
                        reply.Reply(Map.tryFind name statuses)
                        return! loop statuses
                    | GetAllStatuses reply ->
                        reply.Reply(statuses)
                        return! loop statuses
                }

            loop Map.empty)

    let setStatus (name: string) status =
        // Route the verdict's summary into the activity log here, at the one
        // choke point every status flows through, so the run record's summary and
        // the reported verdict can never disagree. Set BEFORE the status post:
        // RecordTerminal (fired by the status agent when it applies the mutation)
        // consumes the pending summary.
        match status with
        | Completed(_, verdict)
        | Failed(_, _, verdict) -> activity.SetSummary(name, verdict.Summary)
        | Idle
        | Running _ -> ()

        // Non-blocking by design: setStatus is called from plugin
        // MailboxProcessor agent threads (via ReportStatus inside handler Update
        // bodies). Blocking on the status agent's reply would pin a pool thread
        // per concurrent caller, and enough simultaneous blocked reporters starve
        // the status agent of the very pool thread it needs to reply. A plain Post
        // keeps every agent thread free; ordering and visibility survive because
        // (a) the status agent's mailbox is FIFO, so any GetStatus/GetAllStatuses
        // posted after this SetStatus observes it, and (b) the statusChanged
        // trigger is fired by the trigger agent only after the mutation is applied.
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
    /// `PluginCtx.ProjectGraph`. The daemon calls this once, with closures over its
    /// live `ProjectGraph`, before registering plugins.
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
                         | Failed(e, _, _) -> $"Failed: %s{e.Substring(0, min 80 e.Length)}")
              ReportErrors =
                fun name file entries -> ledger.Report(PluginFramework.PluginName.value name, file, entries)
              ClearErrors = fun name file -> ledger.Clear(PluginFramework.PluginName.value name, file)
              ClearPlugin = fun name -> ledger.ClearPlugin(PluginFramework.PluginName.value name)
              GetPluginDiagnostics = fun name -> ledger.GetByPlugin(PluginFramework.PluginName.value name)
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
              SetNextTerminalOutcome =
                fun name outcome -> activity.SetNextTerminalOutcome(PluginFramework.PluginName.value name, outcome)
              FcsSuppressedCodes = fcsSuppressedCodes
              // Each closure re-reads the mutable holder per call, so a plugin
              // registered before the daemon installed the live graph still sees it.
              ProjectGraph =
                { GetAllProjects = fun () -> projectGraphAccessor.GetAllProjects()
                  GetTransitiveDependentProjects = fun p -> projectGraphAccessor.GetTransitiveDependentProjects p
                  GetProjectReferences = fun p -> projectGraphAccessor.GetProjectReferences p
                  GetCanonicalDllPath = fun p -> projectGraphAccessor.GetCanonicalDllPath p }
              StartAsync = Async.Start
              ClaimOrQueueSharedRun = fun key start -> sharedRunScheduler.ClaimOrQueue(key, start)
              ReleaseSharedRun = fun key state -> sharedRunScheduler.Release(key, state) }

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

    /// Run all preprocessors on the given files.
    ///
    /// Each preprocessor's status line is its own evidence — `formatted 0 of 12 files —
    /// dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)` — and a pass that
    /// could not run (`Error`) or threw is a FAILED status carrying the reason, so it can
    /// never be read as "nothing needed rewriting" (AUTOMATION-447).
    member _.RunPreprocessors(files: string list) : PreprocessorsRun =
        let mutable modifiedFiles = []
        let mutable lines = []
        let mutable evidence = []
        let mutable refused = []

        for preprocessor in preprocessors do
            let startedAt = System.DateTime.UtcNow
            setStatus preprocessor.Name (Running(since = startedAt))

            // MGA-ERROR-REPORT-001:ok — a throwing preprocessor becomes a Failed status and a `Refused` entry
            try
                match preprocessor.Process files repoRoot with
                | Result.Ok result ->
                    modifiedFiles <- result.Modified @ modifiedFiles
                    let finishedAt = System.DateTime.UtcNow

                    let summary =
                        $"%s{preprocessor.Name}: rewrote %d{result.Modified.Length} of %d{result.Considered} file(s) — %s{result.Evidence}"

                    lines <- summary :: lines
                    evidence <- result.Evidence :: evidence

                    setStatus
                        preprocessor.Name
                        (Completed(finishedAt, RunVerdict.create summary (finishedAt - startedAt)))
                | Result.Error reason ->
                    let finishedAt = System.DateTime.UtcNow
                    let summary = $"%s{preprocessor.Name} refused: %s{reason}"
                    refused <- (preprocessor.Name, reason) :: refused
                    Logging.error preprocessor.Name summary

                    setStatus
                        preprocessor.Name
                        (Failed(reason, finishedAt, RunVerdict.create summary (finishedAt - startedAt)))
            with ex ->
                let finishedAt = System.DateTime.UtcNow
                refused <- (preprocessor.Name, ex.Message) :: refused

                setStatus
                    preprocessor.Name
                    (Failed(
                        ex.ToString(),
                        finishedAt,
                        RunVerdict.create $"preprocessor failed: %s{ex.Message}" (finishedAt - startedAt)
                    ))

        { Modified = modifiedFiles |> List.distinct
          Lines = List.rev lines
          Evidence = List.rev evidence
          Refused = List.rev refused }

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

    /// AUTOMATION-300 — clear a file's findings from EVERY plugin, not one.
    ///
    /// For a file that no longer exists there is no such thing as a per-plugin
    /// answer: the path is gone, so every finding keyed to it is about nothing.
    /// Clearing only one plugin's view (which is what the removed-file path used
    /// to do, for `fcs` alone) leaves the others reporting a parse failure in a
    /// file that cannot be opened — a permanently red gate about code that does
    /// not exist, recoverable only by stopping the daemon.
    ///
    /// Reads the plugin names OUT OF THE LEDGER rather than from the registered
    /// plugin list. The ledger is keyed by whatever name reported, so asking the
    /// registry instead would clear only what happens to be registered right now
    /// — and would clear nothing at all in any context where findings outlive
    /// their reporter. The findings themselves are the authority on who reported
    /// them.
    member _.ClearFilesEverywhere(filePaths: string list) =
        if not filePaths.IsEmpty then
            let wanted = Set.ofList filePaths
            let all = ledger.GetAll()

            for KeyValue(file, entries) in all do
                if wanted.Contains file then
                    for (pluginName, _) in entries do
                        ledger.Clear(pluginName, file)

    /// Remove diagnostics keyed to repository files that no longer exist.
    ///
    /// Keys outside the repository and angle-bracket pseudo-keys (for example
    /// `<build>`) are opaque plugin identities, not files owned by this host.
    /// Relative keys are repository-relative file paths. The ledger revision in
    /// the snapshot makes deletion conditional: a report that arrives while the
    /// filesystem is inspected survives the atomic compare-and-remove.
    member internal _.PruneVanishedErrors(fileExists: string -> bool) =
        let tryRepoFile (key: string) =
            if
                key.StartsWith("<", System.StringComparison.Ordinal)
                && key.EndsWith(">", System.StringComparison.Ordinal)
            then
                None
            else
                try
                    let fullPath =
                        if System.IO.Path.IsPathRooted(key) then
                            System.IO.Path.GetFullPath(key)
                        else
                            System.IO.Path.GetFullPath(key, normalizedRepoRoot)

                    let relative = System.IO.Path.GetRelativePath(normalizedRepoRoot, fullPath)

                    if
                        relative = ".."
                        || relative.StartsWith(
                            $"..%c{System.IO.Path.DirectorySeparatorChar}",
                            System.StringComparison.Ordinal
                        )
                        || System.IO.Path.IsPathRooted(relative)
                    then
                        None
                    else
                        Some fullPath
                with
                | :? System.ArgumentException
                | :? System.NotSupportedException
                | :? System.IO.PathTooLongException -> None

        let snapshots = ledger.SnapshotKeys()

        let existence =
            snapshots
            |> Seq.map (fun snapshot -> snapshot.File)
            |> Seq.distinct
            |> Seq.choose (fun key -> tryRepoFile key |> Option.map (fun fullPath -> key, fileExists fullPath))
            |> Map.ofSeq

        let vanished =
            snapshots
            |> List.filter (fun snapshot -> Map.tryFind snapshot.File existence = Some false)

        ledger.PruneIfCurrent(vanished)

    /// Single-file convenience over `ClearFilesEverywhere`.
    member this.ClearFileEverywhere(filePath: string) = this.ClearFilesEverywhere [ filePath ]

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

    /// WHICH plugins report work in flight. Same predicate as `AnyPluginBusy`,
    /// but naming the offenders, so a `WaitForComplete` that times out on the busy
    /// leg can say which plugin blocked it rather than lumping three unrelated
    /// failures into one sentence. Kept separate rather than folded into one
    /// accessor because `AnyPluginBusy` is polled every 50ms and must not
    /// allocate a list to answer a boolean.
    member _.BusyPluginNames() : string list =
        registeredPlugins
        |> Seq.filter (fun p -> p.IsBusy())
        |> Seq.map (fun p -> PluginFramework.PluginName.value p.Name)
        |> List.ofSeq

    /// Total events every plugin has FINISHED handling. The stall detector
    /// compares this across polls: if it moved, work is being done, whatever the
    /// busy set looks like. Busy-set identity cannot answer that — one plugin
    /// draining a long backlog keeps the very same set for the whole drain.
    member _.CompletedDispatches() : int64 =
        registeredPlugins |> Seq.sumBy (fun p -> p.CompletedDispatches())

    /// Plugins whose message loop has died, with the fault that killed it.
    ///
    /// Such a plugin reports busy forever — the in-flight count is incremented
    /// at post time and only the loop decrements it — so without this the wait
    /// can only infer death from silence. Empty in every healthy daemon.
    member _.FaultedPlugins() : (string * exn) list =
        registeredPlugins
        |> Seq.choose (fun p -> p.Fault() |> Option.map (fun ex -> PluginFramework.PluginName.value p.Name, ex))
        |> List.ofSeq

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

    /// The spelling the task cache knows a file by. Entries are keyed by the
    /// repo-relative identity (so they are portable between checkouts), so a caller
    /// naming a file by its absolute path must be translated before it can clear
    /// anything — otherwise `fshw cache clear --file=...` silently matches nothing.
    member private _.TaskCacheFileKey(file: string) =
        CachePathIdentity.ofPath repoRoot file |> CachePathIdentity.toKey

    /// Clear task cache entries for a specific file.
    member this.ClearTaskCacheFile(file: string) =
        match taskCache with
        | Some c -> c.ClearFile(this.TaskCacheFileKey file)
        | None -> ()

    /// Clear a specific plugin+file task cache entry.
    member this.ClearTaskCachePluginFile(plugin: string, file: string) =
        match taskCache with
        | Some c -> c.ClearPluginFile plugin (this.TaskCacheFileKey file)
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

    /// Force a plugin to re-run from a cleared task cache.
    ///
    /// A FileCommand plugin with a registered pattern gets a synthetic `FileChanged`
    /// whose path matches only it — other plugins cache-hit, only the target sees a
    /// miss. Any other plugin subscribed to `FileChanged` (format-check, AUTOMATION-447)
    /// gets a `SourceChanged` over `sourceFiles ()` — the daemon's registered source
    /// set, the same files a cold scan would deliver — with its cache cleared first,
    /// so the run it produces is a fresh one over the real tree, not a replay.
    ///
    /// Returns `Error` with the reason when nothing can be re-fired: no such plugin, a
    /// plugin that does not consume file changes (a FileCommand configured only with
    /// `afterTests`, a build-triggered plugin), a preprocessor (which has no cached
    /// state to refresh — `fshw format` re-runs it over every registered file), or a
    /// registered source set that is still empty. The caller waits for plugins to
    /// settle before inspecting status.
    member this.RerunPlugin(name: string, sourceFiles: unit -> string list) : Result<unit, string> =
        match this.GetFileCommandPattern(name) with
        | Some pattern ->
            this.ClearTaskCachePlugin(name)
            this.EmitFileChanged(SourceChanged [ Watcher.FilePattern.syntheticPath pattern ])
            Result.Ok()
        | None ->
            let registered =
                registeredPlugins
                |> Seq.tryFind (fun p -> PluginFramework.PluginName.value p.Name = name)

            let isPreprocessor = preprocessors |> Seq.exists (fun p -> p.Name = name)

            match registered with
            | Some p when p.Subscriptions.Contains PluginFramework.SubscribeFileChanged ->
                match sourceFiles () with
                | [] ->
                    Result.Error
                        $"Plugin '%s{name}' consumes file changes, but no source files are registered yet — run `fshw scan` first"
                | files ->
                    this.ClearTaskCachePlugin(name)
                    this.EmitFileChanged(SourceChanged files)
                    Result.Ok()
            | Some _ ->
                Result.Error
                    $"Plugin '%s{name}' has no registered file pattern and does not consume file changes, so there is no event to re-fire (only FileCommand plugins with a pattern and FileChanged subscribers support rerun)"
            | None when isPreprocessor ->
                Result.Error
                    $"'%s{name}' is a preprocessor, not a plugin: it keeps no cached state to refresh — `fshw format` re-runs it over every registered file and reports what ran"
            | None -> Result.Error $"Plugin '%s{name}' has no registered file pattern and is not a registered plugin"

    /// Tear down all plugins that have a Teardown function.
    member _.Teardown() =
        for p in registeredPlugins do
            match p.Teardown with
            | Some teardown ->
                // Plugin Teardown is a third-party-extension boundary; the broad
                // catch keeps one misbehaving plugin from preventing the rest from
                // cleaning up. Logged as ex.ToString() so the type and stack trace
                // survive for diagnosing the offending plugin.
                try
                    teardown ()
                with ex ->
                    Logging.error (PluginFramework.PluginName.value p.Name) $"Teardown failed: %s{ex.ToString()}"
            | None -> ()

        fileCommandPatterns.Clear()

    static member create (checker: FSharpChecker) (repoRoot: string) = PluginHost(checker, repoRoot)
