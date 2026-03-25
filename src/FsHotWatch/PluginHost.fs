module FsHotWatch.PluginHost

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events
open FsHotWatch.Plugin

/// Manages plugin lifecycle, event dispatch, command registration, and status tracking.
type PluginHost(checker: FSharpChecker, repoRoot: string) =
    let fileChanged = Event<FileChangeKind>()
    let buildCompleted = Event<BuildResult>()
    let fileChecked = Event<FileCheckResult>()
    let projectChecked = Event<ProjectCheckResult>()
    let testCompleted = Event<TestResults>()

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
              OnFileChecked = fileChecked.Publish
              OnProjectChecked = projectChecked.Publish
              OnTestCompleted = testCompleted.Publish
              ReportStatus = fun status -> statuses[plugin.Name] <- status
              RegisterCommand = fun (name, handler) -> commands[name] <- handler
              EmitBuildCompleted = fun result -> buildCompleted.Trigger(result)
              EmitTestCompleted = fun results -> testCompleted.Trigger(results) }

        statuses[plugin.Name] <- Idle
        plugin.Initialize(ctx)

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

    /// Emit a file checked event to all registered plugins.
    member _.EmitFileChecked(result: FileCheckResult) = fileChecked.Trigger(result)

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

    /// Create a new PluginHost.
    static member create (checker: FSharpChecker) (repoRoot: string) = PluginHost(checker, repoRoot)
