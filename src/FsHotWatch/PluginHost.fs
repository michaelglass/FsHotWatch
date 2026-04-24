module FsHotWatch.PluginHost

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin

/// Manages plugin lifecycle, event dispatch, command registration, and status tracking.
type PluginHost
    (checker: FSharpChecker, repoRoot: string, ?reporters: IErrorReporter list, ?taskCache: TaskCache.ITaskCache) =
    let statusChanged = Event<string * PluginStatus>()

    let ledger = ErrorLedger(?reporters = reporters)
    let commands = ConcurrentDictionary<string, CommandHandler>()
    let preprocessors = ConcurrentBag<IFsHotWatchPreprocessor>()
    let fileCommandPatterns = ConcurrentDictionary<string, Watcher.FilePattern>()

    // Status tracking uses a lock + mutable map instead of a MailboxProcessor.
    // The event fires OUTSIDE the lock so subscribers can safely call GetAllStatuses
    // without deadlocking (a MailboxProcessor fires events inside its loop,
    // causing re-entrant PostAndReply deadlocks).
    let mutable statuses: Map<string, PluginStatus> = Map.empty
    let mutable runStartedAt: Map<string, System.DateTime> = Map.empty
    let statusLock = obj ()
    let activity = PluginActivity.State()

    let setStatus (name: string) status =
        lock statusLock (fun () ->
            let prev = Map.tryFind name statuses
            statuses <- Map.add name status statuses

            match prev, status with
            | _, Running since -> runStartedAt <- Map.add name since runStartedAt
            | _, Completed at ->
                let startedAt =
                    match Map.tryFind name runStartedAt with
                    | Some s -> s
                    | None -> at

                activity.RecordTerminal(name, CompletedRun, startedAt, at)
                runStartedAt <- Map.remove name runStartedAt
            | _, Failed(err, at) ->
                let startedAt =
                    match Map.tryFind name runStartedAt with
                    | Some s -> s
                    | None -> at

                activity.RecordTerminal(name, FailedRun err, startedAt, at)
                runStartedAt <- Map.remove name runStartedAt
            | _ -> ())

        statusChanged.Trigger(name, status)

    let setPluginStatus (name: PluginFramework.PluginName) status =
        setStatus (PluginFramework.PluginName.value name) status

    let registeredPlugins = ResizeArray<PluginFramework.RegisteredPlugin>()

    /// Dispatch an event to all registered plugins (filtering is built into each plugin's Dispatch).
    let dispatchToAll (event: PluginFramework.PluginDispatchEvent) =
        for p in registeredPlugins do
            p.Dispatch event

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
              SetSummary = fun name s -> activity.SetSummary(PluginFramework.PluginName.value name, s) }

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
                setStatus preprocessor.Name (Failed(ex.Message, System.DateTime.UtcNow))

        modifiedFiles |> List.distinct

    /// Emit a file change event to all registered plugins.
    member _.EmitFileChanged(change: FileChangeKind) =
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
    member _.EmitFileChecked(result: FileCheckResult) =
        dispatchToAll (PluginFramework.DispatchFileChecked result)

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
        lock statusLock (fun () -> Map.tryFind pluginName statuses)

    /// Get all plugin statuses as an immutable map.
    member _.GetAllStatuses() : Map<string, PluginStatus> = lock statusLock (fun () -> statuses)

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
                try
                    teardown ()
                with ex ->
                    Logging.error (PluginFramework.PluginName.value p.Name) $"Teardown failed: %s{ex.Message}"
            | None -> ()

        fileCommandPatterns.Clear()

    static member create (checker: FSharpChecker) (repoRoot: string) = PluginHost(checker, repoRoot)
