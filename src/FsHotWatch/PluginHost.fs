module FsHotWatch.PluginHost

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin

/// Manages plugin lifecycle, event dispatch, command registration, and status tracking.
type PluginHost(checker: FSharpChecker, repoRoot: string) =
    let fileChanged = Event<FileChangeKind>()
    let buildCompleted = Event<BuildResult>()
    let projectChecked = Event<ProjectCheckResult>()
    let testCompleted = Event<TestResults>()
    let statusChanged = Event<string * PluginStatus>()

    let fileCheckedHandlers = ConcurrentBag<FileCheckResult -> unit>()

    /// IEvent wrapper that captures handlers into a bag for parallel dispatch.
    let fileCheckedCapture =
        { new IEvent<Handler<FileCheckResult>, FileCheckResult> with
            member _.AddHandler(handler) =
                fileCheckedHandlers.Add(fun r -> handler.Invoke(null, r))

            member _.RemoveHandler(_handler) = ()

            member _.Subscribe(observer) =
                fileCheckedHandlers.Add(fun r -> observer.OnNext(r))

                { new System.IDisposable with
                    member _.Dispose() = () } }

    let ledger = ErrorLedger()
    let commands = ConcurrentDictionary<string, CommandHandler>()
    let statuses = ConcurrentDictionary<string, PluginStatus>()
    let preprocessors = ConcurrentBag<IFsHotWatchPreprocessor>()

    /// Register a plugin, wiring up its context and calling Initialize.
    member _.Register(plugin: IFsHotWatchPlugin) =
        let ctx =
            { Checker = checker
              RepoRoot = repoRoot
              OnFileChanged = fileChanged.Publish
              OnBuildCompleted = buildCompleted.Publish
              OnFileChecked = fileCheckedCapture
              OnProjectChecked = projectChecked.Publish
              OnTestCompleted = testCompleted.Publish
              ReportStatus =
                fun status ->
                    let statusName =
                        match status with
                        | Idle -> "Idle"
                        | Running _ -> "Running"
                        | Completed _ -> "Completed"
                        | Failed(e, _) -> $"Failed: %s{e.Substring(0, min 80 e.Length)}"

                    Logging.debug plugin.Name $"→ %s{statusName}"

                    statuses[plugin.Name] <- status
                    statusChanged.Trigger(plugin.Name, status)
              RegisterCommand = fun (name, handler) -> commands[name] <- handler
              EmitBuildCompleted = fun result -> buildCompleted.Trigger(result)
              EmitTestCompleted = fun results -> testCompleted.Trigger(results)
              ReportErrors = fun file errors -> ledger.Report(plugin.Name, file, errors)
              ClearErrors = fun file -> ledger.Clear(plugin.Name, file) }

        statuses[plugin.Name] <- Idle

        try
            plugin.Initialize(ctx)
        with ex ->
            Logging.error "plugin-host" $"Failed to initialize plugin '%s{plugin.Name}': %s{ex.Message}"
            statuses[plugin.Name] <- Failed(ex.Message, System.DateTime.UtcNow)

    /// Register a preprocessor (runs before events are dispatched).
    member _.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) =
        statuses[preprocessor.Name] <- Idle
        preprocessors.Add(preprocessor)

    /// Run all preprocessors on the given files. Returns files that were modified.
    member _.RunPreprocessors(files: string list) : string list =
        let mutable modifiedFiles = []

        for preprocessor in preprocessors do
            statuses[preprocessor.Name] <- Running(since = System.DateTime.UtcNow)

            try
                let modified = preprocessor.Process files repoRoot
                modifiedFiles <- modified @ modifiedFiles
                statuses[preprocessor.Name] <- Completed(box modified, System.DateTime.UtcNow)
            with ex ->
                statuses[preprocessor.Name] <- Failed(ex.Message, System.DateTime.UtcNow)

        modifiedFiles |> List.distinct

    /// Emit a file change event to all registered plugins.
    member _.EmitFileChanged(change: FileChangeKind) = fileChanged.Trigger(change)

    /// Emit a build completed event to all registered plugins.
    member _.EmitBuildCompleted(result: BuildResult) = buildCompleted.Trigger(result)

    /// Report errors to the ledger on behalf of a named source (e.g., "fcs").
    member _.ReportErrors(pluginName: string, filePath: string, entries: ErrorEntry list, ?version: int64) =
        ledger.Report(pluginName, filePath, entries, ?version = version)

    /// Clear errors in the ledger for a named source + file.
    member _.ClearErrors(pluginName: string, filePath: string, ?version: int64) =
        ledger.Clear(pluginName, filePath, ?version = version)

    /// Emit a file checked event to all registered plugins (synchronous, sequential).
    member _.EmitFileChecked(result: FileCheckResult) =
        for handler in fileCheckedHandlers.ToArray() do
            handler result

    /// Emit a file checked event to all registered plugins in parallel.
    /// Each handler runs as an independent async computation.
    member _.EmitFileCheckedParallel(result: FileCheckResult) : Async<unit> =
        async {
            let handlers = fileCheckedHandlers.ToArray()

            if handlers.Length > 0 then
                do!
                    handlers
                    |> Array.map (fun handler -> async { handler result })
                    |> Async.Parallel
                    |> Async.Ignore
        }

    /// Emit a project checked event to all registered plugins.
    member _.EmitProjectChecked(result: ProjectCheckResult) = projectChecked.Trigger(result)

    /// Emit a test completed event to all registered plugins.
    member _.EmitTestCompleted(results: TestResults) = testCompleted.Trigger(results)

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
        match statuses.TryGetValue(pluginName) with
        | true, status -> Some status
        | false, _ -> None

    /// Get all plugin statuses as an immutable map.
    member _.GetAllStatuses() : Map<string, PluginStatus> =
        statuses |> Seq.map (fun kvp -> kvp.Key, kvp.Value) |> Map.ofSeq

    /// Get all errors grouped by file path.
    member _.GetErrors() = ledger.GetAll()

    /// Get errors for a specific plugin only.
    member _.GetErrorsByPlugin(name) = ledger.GetByPlugin(name)

    /// True if any errors exist in the ledger.
    member _.HasErrors() = ledger.HasErrors()

    /// Total error count across all plugins and files.
    member _.ErrorCount() = ledger.Count()

    /// Event fired when any plugin's status changes.
    member _.OnStatusChanged = statusChanged.Publish

    /// Create a new PluginHost.
    static member create (checker: FSharpChecker) (repoRoot: string) = PluginHost(checker, repoRoot)
