module FsHotWatch.PluginHost

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin

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
    // Registration order is run order: a generator registered before the formatter runs
    // before it, and the formatter is offered what the generator wrote.
    let preprocessors = ResizeArray<IFsHotWatchPreprocessor>()

    let preprocessorsSnapshot () =
        lock preprocessors (fun () -> List.ofSeq preprocessors)

    let fileCommandPatterns = ConcurrentDictionary<string, Watcher.FilePattern>()
    let activity = PluginActivity.State()
    // Wall-time attribution rework. Every phase the daemon spends wall time in — its own
    // (startup, discovery, scans, change batches) and every plugin's `Running` →
    // terminal interval, superseded runs included — so a verdict can cover what the
    // check observed instead of only each plugin's surviving `lastRun`.
    let phases = DaemonPhases.Ledger()
    let sharedRunScheduler = PluginFramework.SharedRunScheduler()

    // Read-only project-graph accessor threaded into every plugin's
    // `PluginCtx.ProjectGraph`. Starts as the no-op accessor; the daemon installs
    // the live graph via `SetProjectGraph` before plugins are registered (so the
    // closure captured into each plugin's `services` sees the live one). Volatile
    // because it's set from the daemon construction thread and read from plugin
    // agent threads.
    let mutable projectGraphAccessor = PluginFramework.ProjectGraphAccessor.none

    // The one publication of owned work: every registered plugin's owner row, plus the
    // host's own operations (dispatch fan-out, preprocessor passes). Busy, progress and
    // fault answers are read from one pinned snapshot of it.
    let workStore = PluginWorkOwner.Store()

    // UTC ticks of the most recent host-level activity (event dispatch, plugin status
    // change, preprocessor run). The quiescence window in `WaitForComplete` and
    // idle-exit read it; it is not an ownership record.
    let mutable lastActivityAtTicks = System.DateTime.UtcNow.Ticks

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

    // statusChanged.Trigger dispatch is owned by its own agent: `setStatus` posts
    // the (name, status) pair here AFTER publishing the mutation, and this loop
    // fires the trigger serially, in mutation order, OUTSIDE the status write
    // lock. Two invariants hang off that:
    //   1. A subscriber that writes a status from inside the trigger callback
    //      cannot deadlock — no status write is in progress inside the trigger.
    //   2. By the time a trigger fires, the mutation is already published, so a
    //      re-entrant read observes the new value (or newer).
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

    // Plugin statuses are one immutable map, replaced wholesale under `statusGate`
    // and published with a volatile write. Readers take no lock and post no message:
    // they read the last published map. The scan's build wait, `WaitForComplete`,
    // the RPC status and the wedge monitor all read it on the gate's path, so no read
    // depends on a thread-pool thread being scheduled for it: under a saturated pool
    // that dependence turns a healthy daemon's reads into timeouts.
    //
    // Writers apply the mutation on their own thread inside the lock, so a status is
    // visible to the next read on any thread as soon as `setStatus` returns. The lock
    // guards a map insert and two leaf-locked records; nothing inside it waits on
    // another thread.
    let statusGate = obj ()
    let mutable statuses: Map<string, PluginStatus> = Map.empty

    let readStatuses () =
        System.Threading.Volatile.Read(&statuses)

    let applyStatus (name: string) (status: PluginStatus) =
        let prev = Map.tryFind name statuses

        touchActivity ()

        // Every terminal status carries its verdict, so the run record
        // derives startedAt from it rather than guessing.
        match status with
        | Completed(at, verdict) ->
            let outcome = RunOutcome.ofCompletedVerdict verdict
            activity.RecordTerminal(name, outcome, at - verdict.Elapsed, at)
        | Failed(err, at, verdict) -> activity.RecordTerminal(name, FailedRun err, at - verdict.Elapsed, at)
        | Idle
        | Running _ -> ()

        // The plugin's WHOLE `Running` interval, not the run it measured itself:
        // test-prune is `Running` through symbol analysis and selection long before
        // `executeTests` starts its stopwatch, and a check blocked on
        // `WaitForComplete` waits for all of it. Recorded for EVERY terminal
        // transition, so a run a later re-run supersedes is still on the ledger when
        // the verdict asks.
        match prev, status with
        | Some(Running since), Completed(at, verdict)
        | Some(Running since), Failed(_, at, verdict) ->
            phases.Record(DaemonPhases.Phase.PluginRun name, since, at - since, Some verdict.Summary)
        | _, Completed(at, verdict)
        | _, Failed(_, at, verdict) ->
            phases.Record(
                DaemonPhases.Phase.PluginRun name,
                at - verdict.Elapsed,
                verdict.Elapsed,
                Some verdict.Summary
            )
        | _, Idle
        | _, Running _ -> ()

        System.Threading.Volatile.Write(&statuses, Map.add name status statuses)

        // Posted inside the lock so notifications keep mutation order.
        triggerAgent.Post(name, status)

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

        // Called from plugin agent threads (via ReportStatus inside handler Update
        // bodies). The write never waits for another thread to be scheduled: it is
        // applied here, under a lock whose holders only update memory.
        lock statusGate (fun () -> applyStatus name status)

    let setPluginStatus (name: PluginFramework.PluginName) status =
        setStatus (PluginFramework.PluginName.value name) status

    let registeredPlugins = ResizeArray<PluginFramework.RegisteredPlugin>()

    /// Dispatch an event to all registered plugins (filtering is built into each plugin's Dispatch).
    ///
    /// The fan-out is a host operation of its own until every recipient has admitted the
    /// event, so no reader of the publication can see rest between two admissions.
    let dispatchToAll (event: PluginFramework.PluginDispatchEvent) =
        touchActivity ()
        let fanOut = workStore.BeginOperation "dispatch"

        try
            // `Dispatch` never throws: a plugin whose executor has stopped drops the event,
            // and the remaining recipients still receive it.
            for p in registeredPlugins do
                p.Dispatch event
        finally
            workStore.EndOperation fanOut

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
                // Plugins observe the model this host publishes, whoever supplied the graph.
                { ObserveModel = fun () -> workStore.Snapshot.ProjectModel
                  ObserveCheckableFiles = fun () -> workStore.Snapshot.ProjectModelFiles
                  GetAllProjects = fun () -> projectGraphAccessor.GetAllProjects()
                  GetTransitiveDependentProjects = fun p -> projectGraphAccessor.GetTransitiveDependentProjects p
                  GetProjectReferences = fun p -> projectGraphAccessor.GetProjectReferences p
                  GetCanonicalDllPath = fun p -> projectGraphAccessor.GetCanonicalDllPath p }
              StartAsync = Async.Start
              ClaimOrQueueSharedRun = fun key start -> sharedRunScheduler.ClaimOrQueue(key, start)
              ReleaseSharedRun = fun key state -> sharedRunScheduler.Release(key, state) }

        let plugin = PluginFramework.registerHandlerWithOwner workStore services handler

        if registeredPlugins |> Seq.exists (fun p -> p.Name = plugin.Name) then
            Logging.warn
                "plugin-host"
                $"Plugin name '%s{PluginFramework.PluginName.value plugin.Name}' is already registered — commands and status may be overwritten"

        setPluginStatus plugin.Name Idle
        registeredPlugins.Add(plugin)

    /// Register a preprocessor (runs before events are dispatched).
    member _.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) =
        setStatus preprocessor.Name Idle
        lock preprocessors (fun () -> preprocessors.Add(preprocessor))

    /// The registered preprocessors' names, in run order.
    member _.PreprocessorNames() : string list =
        preprocessorsSnapshot () |> List.map (fun p -> p.Name)

    /// Run all preprocessors on the given files, in registration order. Each pass is
    /// offered the files plus every file an earlier pass rewrote, so a formatter
    /// registered last formats what a generator produced.
    ///
    /// Each preprocessor's status line is its own evidence — `formatted 0 of 12 files —
    /// dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)` — and a pass that
    /// could not run (`Error`) or threw is a FAILED status carrying the reason, so it can
    /// never be read as "nothing needed rewriting".
    member _.RunPreprocessors(files: string list) : PreprocessorsRun =
        // The pass, and each preprocessor inside it, is owned host work until its outcome
        // is published: the host is not at rest while a formatter is rewriting files.
        let pass = workStore.BeginOperation "preprocessors"

        try
            let mutable modifiedFiles = []
            let mutable lines = []
            let mutable evidence = []
            let mutable refused = []

            for preprocessor in preprocessorsSnapshot () do
                let operation = workStore.BeginOperation preprocessor.Name

                try
                    let startedAt = System.DateTime.UtcNow
                    setStatus preprocessor.Name (Running(since = startedAt))
                    let offered = (files @ modifiedFiles) |> List.distinct

                    // MGA-ERROR-REPORT-001:ok — a throwing preprocessor becomes a Failed status and a `Refused` entry
                    try
                        match preprocessor.Process offered repoRoot with
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

                            workStore.FailOperation(operation, System.InvalidOperationException(summary))
                            |> ignore

                            setStatus
                                preprocessor.Name
                                (Failed(reason, finishedAt, RunVerdict.create summary (finishedAt - startedAt)))
                    with ex ->
                        let finishedAt = System.DateTime.UtcNow
                        refused <- (preprocessor.Name, ex.Message) :: refused
                        workStore.FailOperation(operation, ex) |> ignore

                        setStatus
                            preprocessor.Name
                            (Failed(
                                ex.ToString(),
                                finishedAt,
                                RunVerdict.create $"preprocessor failed: %s{ex.Message}" (finishedAt - startedAt)
                            ))
                finally
                    workStore.EndOperation operation

            { Modified = modifiedFiles |> List.distinct
              Lines = List.rev lines
              Evidence = List.rev evidence
              Refused = List.rev refused }
        finally
            workStore.EndOperation pass

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

    /// clear a file's findings from EVERY plugin, not one.
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
        readStatuses () |> Map.tryFind pluginName

    /// Get all plugin statuses as an immutable map.
    member _.GetAllStatuses() : Map<string, PluginStatus> = readStatuses ()

    /// Test seam: hold the status write lock on another thread until `release` is set,
    /// and return once it is held — a writer that cannot finish, for proving that reads
    /// do not wait for one.
    member internal _.HoldStatusWritesForTest(release: System.Threading.ManualResetEventSlim) =
        // `try/finally`, not `use`: this seam must not add a null-check branch to the
        // file's coverage that no test can take.
        let held = new System.Threading.ManualResetEventSlim(false)

        try
            let holder =
                System.Threading.Thread(
                    (fun () ->
                        lock statusGate (fun () ->
                            held.Set()
                            release.Wait())),
                    IsBackground = true
                )

            holder.Start()
            held.Wait()
        finally
            held.Dispose()

    /// UTC timestamp of the most recent host activity: an event dispatch or a
    /// plugin status transition. Used by `WaitForComplete` to enforce a
    /// quiescence window so a plugin that's about to start a new cycle isn't
    /// missed when its predecessor's event has been emitted but not yet
    /// processed from the plugin's mailbox.
    member _.LastActivityAt() : System.DateTime =
        System.DateTime(System.Threading.Volatile.Read(&lastActivityAtTicks), System.DateTimeKind.Utc)

    /// The one immutable publication of owned work. Pin it once to answer several
    /// questions about the same instant.
    member internal _.WorkSnapshot: PluginWorkOwner.HostSnapshot = workStore.Snapshot

    /// The store behind `WorkSnapshot`. The daemon's scan and change-batch supervisors
    /// publish their rows into it, so one publication answers for them and the plugins.
    member internal _.WorkStore: PluginWorkOwner.Store = workStore

    /// True if the publication holds any owned work: an admitted event not yet
    /// committed, a queued command, an exclusive run from claim until its result is
    /// committed, or a host operation (dispatch fan-out, preprocessor pass).
    member _.AnyPluginBusy() : bool = workStore.Snapshot.IsBusy

    /// WHICH plugins and host operations own work, from the same publication as
    /// `AnyPluginBusy`, so a `WaitForComplete` that times out can say what blocked it.
    member _.BusyPluginNames() : string list = workStore.Snapshot.BusyNames

    /// Total events every plugin has committed. The stall detector compares this
    /// across polls: if it moved, work is being done, whatever the busy set looks like.
    member _.CompletedDispatches() : int64 = workStore.Snapshot.CompletedEvents

    /// Plugins whose executor has stopped, with the fault that stopped it. The fault
    /// stays after the plugin's last live worker settles: no busy work is not evidence
    /// of a healthy executor. Empty in every healthy daemon.
    member _.FaultedPlugins() : (string * exn) list = workStore.Snapshot.ExecutorFaults

    /// Every failure the publication records: failed events, commits and exclusive
    /// runs, executor faults, and failed host operations.
    member _.FailedWork() : (string * exn) list = workStore.Snapshot.Faults

    /// Failed host operations, such as a refused preprocessor pass.
    member _.FailedOperations() : (string * exn) list = workStore.Snapshot.OperationFaults

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

    /// The daemon's phase ledger, kept for verdict wall-time attribution — see `DaemonPhases`.
    member _.Phases: DaemonPhases.Ledger = phases

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
    /// miss. Any other plugin subscribed to `FileChanged` (format-check)
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
            this.EmitFileChanged(SourceChanged [ Watcher.FilePattern.syntheticPath normalizedRepoRoot pattern ])
            Result.Ok()
        | None ->
            let registered =
                registeredPlugins
                |> Seq.tryFind (fun p -> PluginFramework.PluginName.value p.Name = name)

            let isPreprocessor =
                preprocessorsSnapshot () |> List.exists (fun p -> p.Name = name)

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
