module FsHotWatch.PluginHost

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin

[<NoComparison; NoEquality>]
type private StatusMsg =
    | SetStatus of name: string * PluginStatus
    | GetStatus of name: string * AsyncReplyChannel<PluginStatus option>
    | GetAll of AsyncReplyChannel<Map<string, PluginStatus>>

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
    let preprocessors = ConcurrentBag<IFsHotWatchPreprocessor>()

    let statusAgent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (statuses: Map<string, PluginStatus>) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | SetStatus(name, status) ->
                        statusChanged.Trigger(name, status)
                        return! loop (Map.add name status statuses)
                    | GetStatus(name, ch) ->
                        ch.Reply(Map.tryFind name statuses)
                        return! loop statuses
                    | GetAll ch ->
                        ch.Reply(statuses)
                        return! loop statuses
                }

            loop Map.empty)

    let registeredPlugins = ResizeArray<PluginFramework.RegisteredPlugin>()

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

                    statusAgent.Post(SetStatus(plugin.Name, status))
              RegisterCommand = fun (name, handler) -> commands[name] <- handler
              EmitBuildCompleted = fun result -> buildCompleted.Trigger(result)
              EmitTestCompleted = fun results -> testCompleted.Trigger(results)
              ReportErrors = fun file errors -> ledger.Report(plugin.Name, file, errors)
              ClearErrors = fun file -> ledger.Clear(plugin.Name, file) }

        statusAgent.Post(SetStatus(plugin.Name, Idle))

        try
            plugin.Initialize(ctx)
        with ex ->
            Logging.error "plugin-host" $"Failed to initialize plugin '%s{plugin.Name}': %s{ex.Message}"
            statusAgent.Post(SetStatus(plugin.Name, Failed(ex.Message, System.DateTime.UtcNow)))

    /// Register a declarative framework-managed plugin handler.
    member this.RegisterHandler<'State, 'Msg>(handler: PluginFramework.PluginHandler<'State, 'Msg>) =
        let plugin =
            PluginFramework.registerHandler
                checker
                repoRoot
                (fun name status ->
                    statusAgent.Post(SetStatus(name, status))

                    Logging.debug
                        name
                        (match status with
                         | Idle -> "Idle"
                         | Running _ -> "Running"
                         | Completed _ -> "Completed"
                         | Failed(e, _) -> $"Failed: %s{e.Substring(0, min 80 e.Length)}"))
                (fun name file entries -> ledger.Report(name, file, entries))
                (fun name file -> ledger.Clear(name, file))
                (fun result -> buildCompleted.Trigger(result))
                (fun results -> testCompleted.Trigger(results))
                (fun cmd -> commands[fst cmd] <- snd cmd)
                handler

        statusAgent.Post(SetStatus(plugin.Name, Idle))
        registeredPlugins.Add(plugin)

    /// Register a preprocessor (runs before events are dispatched).
    member _.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) =
        statusAgent.Post(SetStatus(preprocessor.Name, Idle))
        preprocessors.Add(preprocessor)

    /// Run all preprocessors on the given files. Returns files that were modified.
    member _.RunPreprocessors(files: string list) : string list =
        let mutable modifiedFiles = []

        for preprocessor in preprocessors do
            statusAgent.Post(SetStatus(preprocessor.Name, Running(since = System.DateTime.UtcNow)))

            try
                let modified = preprocessor.Process files repoRoot
                modifiedFiles <- modified @ modifiedFiles
                statusAgent.Post(SetStatus(preprocessor.Name, Completed(System.DateTime.UtcNow)))
            with ex ->
                statusAgent.Post(SetStatus(preprocessor.Name, Failed(ex.Message, System.DateTime.UtcNow)))

        modifiedFiles |> List.distinct

    /// Emit a file change event to all registered plugins.
    member _.EmitFileChanged(change: FileChangeKind) =
        fileChanged.Trigger(change)

        for p in registeredPlugins do
            match p.OnFileChanged with
            | Some f -> f change
            | None -> ()

    /// Emit a build completed event to all registered plugins.
    member _.EmitBuildCompleted(result: BuildResult) =
        buildCompleted.Trigger(result)

        for p in registeredPlugins do
            match p.OnBuildCompleted with
            | Some f -> f result
            | None -> ()

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

        for p in registeredPlugins do
            match p.OnFileChecked with
            | Some f -> f result
            | None -> ()

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

            for p in registeredPlugins do
                match p.OnFileChecked with
                | Some f -> f result
                | None -> ()
        }

    /// Emit a project checked event to all registered plugins.
    member _.EmitProjectChecked(result: ProjectCheckResult) = projectChecked.Trigger(result)

    /// Emit a test completed event to all registered plugins.
    member _.EmitTestCompleted(results: TestResults) =
        testCompleted.Trigger(results)

        for p in registeredPlugins do
            match p.OnTestCompleted with
            | Some f -> f results
            | None -> ()

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
        statusAgent.PostAndReply(fun ch -> GetAll ch)

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
