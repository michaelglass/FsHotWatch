module FsHotWatch.PluginHost

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.Plugin

/// Manages plugin lifecycle, event dispatch, command registration, and status tracking.
type PluginHost(checker: FSharpChecker, repoRoot: string) =
    let statusChanged = Event<string * PluginStatus>()

    let ledger = ErrorLedger()
    let commands = ConcurrentDictionary<string, CommandHandler>()
    let preprocessors = ConcurrentBag<IFsHotWatchPreprocessor>()

    // Status tracking uses a lock + mutable map instead of a MailboxProcessor.
    // The event fires OUTSIDE the lock so subscribers can safely call GetAllStatuses
    // without deadlocking (a MailboxProcessor fires events inside its loop,
    // causing re-entrant PostAndReply deadlocks).
    let mutable statuses: Map<string, PluginStatus> = Map.empty
    let statusLock = obj ()

    let setStatus name status =
        lock statusLock (fun () -> statuses <- Map.add name status statuses)
        statusChanged.Trigger(name, status)

    let registeredPlugins = ResizeArray<PluginFramework.RegisteredPlugin>()

    /// Dispatch a payload to all registered plugins that have a matching handler.
    let dispatchToRegistered (selector: PluginFramework.RegisteredPlugin -> ('a -> unit) option) (payload: 'a) =
        for p in registeredPlugins do
            match selector p with
            | Some f -> f payload
            | None -> ()

    /// Register a declarative framework-managed plugin handler.
    member this.RegisterHandler<'State, 'Msg>(handler: PluginFramework.PluginHandler<'State, 'Msg>) =
        let plugin =
            PluginFramework.registerHandler
                checker
                repoRoot
                (fun name status ->
                    setStatus name status

                    Logging.debug
                        name
                        (match status with
                         | Idle -> "Idle"
                         | Running _ -> "Running"
                         | Completed _ -> "Completed"
                         | Failed(e, _) -> $"Failed: %s{e.Substring(0, min 80 e.Length)}"))
                (fun name file entries -> ledger.Report(name, file, entries))
                (fun name file -> ledger.Clear(name, file))
                (fun result -> dispatchToRegistered (fun p -> p.OnBuildCompleted) result)
                (fun results -> dispatchToRegistered (fun p -> p.OnTestCompleted) results)
                (fun cmd -> commands[fst cmd] <- snd cmd)
                handler

        setStatus plugin.Name Idle
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
        dispatchToRegistered (fun p -> p.OnFileChanged) change

    /// Emit a build completed event to all registered plugins.
    member _.EmitBuildCompleted(result: BuildResult) =
        dispatchToRegistered (fun p -> p.OnBuildCompleted) result

    /// Report errors to the ledger on behalf of a named source (e.g., "fcs").
    member _.ReportErrors(pluginName: string, filePath: string, entries: ErrorEntry list, ?version: int64) =
        ledger.Report(pluginName, filePath, entries, ?version = version)

    /// Clear errors in the ledger for a named source + file.
    member _.ClearErrors(pluginName: string, filePath: string, ?version: int64) =
        ledger.Clear(pluginName, filePath, ?version = version)

    /// Emit a file checked event to all registered plugins.
    member _.EmitFileChecked(result: FileCheckResult) =
        dispatchToRegistered (fun p -> p.OnFileChecked) result

    /// Emit a test completed event to all registered plugins.
    member _.EmitTestCompleted(results: TestResults) =
        dispatchToRegistered (fun p -> p.OnTestCompleted) results

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
