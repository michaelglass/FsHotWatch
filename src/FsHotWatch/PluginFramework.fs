/// Declarative plugin framework — define plugins as pure update functions,
/// the framework manages agents, error recovery, and event dispatch.
module FsHotWatch.PluginFramework

open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Logging
open FsHotWatch.Plugin

/// Side-effect context provided to plugin handlers.
[<NoComparison; NoEquality>]
type PluginCtx<'Msg> =
    {
        /// Report the plugin's current status to the host.
        ReportStatus: PluginStatus -> unit
        /// Report per-file errors to the shared error ledger.
        ReportErrors: string -> ErrorEntry list -> unit
        /// Clear this plugin's errors for a file.
        ClearErrors: string -> unit
        /// Clear all of this plugin's errors across all files.
        ClearAllErrors: unit -> unit
        /// Emit a build completed event to other plugins.
        EmitBuildCompleted: BuildResult -> unit
        /// Emit a test completed event to other plugins.
        EmitTestCompleted: TestResults -> unit
        /// Emit a command completed event to other plugins.
        EmitCommandCompleted: CommandCompletedResult -> unit
        /// The warm FSharpChecker instance shared across all plugins.
        Checker: FSharp.Compiler.CodeAnalysis.FSharpChecker
        /// The repository root directory.
        RepoRoot: string
        /// Post a custom message back to this plugin's agent.
        Post: 'Msg -> unit
    }

/// Which events the plugin subscribes to.
type PluginSubscriptions =
    {
        /// Subscribe to file change events.
        FileChanged: bool
        /// Subscribe to file check events.
        FileChecked: bool
        /// Subscribe to build completed events.
        BuildCompleted: bool
        /// Subscribe to test completed events.
        TestCompleted: bool
        /// Subscribe to command completed events.
        CommandCompleted: bool
    }

/// Helper functions for PluginSubscriptions.
module PluginSubscriptions =
    /// No subscriptions — the plugin only handles Custom messages.
    let none =
        { FileChanged = false
          FileChecked = false
          BuildCompleted = false
          TestCompleted = false
          CommandCompleted = false }

/// Declarative plugin definition.
[<NoComparison; NoEquality>]
type PluginHandler<'State, 'Msg> =
    {
        /// The display name of this plugin.
        Name: string
        /// Initial state.
        Init: 'State
        /// Pure-ish update function: given context, current state, and event, produce next state.
        Update: PluginCtx<'Msg> -> 'State -> PluginEvent<'Msg> -> Async<'State>
        /// Named commands that can be invoked via IPC. Each command receives current state and args.
        Commands: (string * ('State -> string array -> Async<string>)) list
        /// Which events the plugin subscribes to.
        Subscriptions: PluginSubscriptions
        /// Optional cache key function. When provided, the framework checks the task cache
        /// before calling Update. Returns Some(key) for cacheable events, None to skip cache.
        CacheKey: (PluginEvent<'Msg> -> string option) option
    }

/// Type-erased plugin registration stored by PluginHost.
[<NoComparison; NoEquality>]
type RegisteredPlugin =
    {
        /// The display name of this plugin.
        Name: string
        /// Handler for file change events, if subscribed.
        OnFileChanged: (FileChangeKind -> unit) option
        /// Handler for file check events, if subscribed.
        OnFileChecked: (FileCheckResult -> unit) option
        /// Handler for build completed events, if subscribed.
        OnBuildCompleted: (BuildResult -> unit) option
        /// Handler for test completed events, if subscribed.
        OnTestCompleted: (TestResults -> unit) option
        /// Handler for command completed events, if subscribed.
        OnCommandCompleted: (CommandCompletedResult -> unit) option
    }

/// Register a declarative plugin handler, returning a type-erased RegisteredPlugin.
/// Creates a MailboxProcessor with error recovery and wires up event dispatch.
let registerHandler
    (checker: FSharp.Compiler.CodeAnalysis.FSharpChecker)
    (repoRoot: string)
    (reportStatus: string -> PluginStatus -> unit)
    (reportErrors: string -> string -> ErrorEntry list -> unit)
    (clearErrors: string -> string -> unit)
    (clearPlugin: string -> unit)
    (emitBuildCompleted: BuildResult -> unit)
    (emitTestCompleted: TestResults -> unit)
    (emitCommandCompleted: CommandCompletedResult -> unit)
    (registerCommand: string * CommandHandler -> unit)
    (taskCache: TaskCache.ITaskCache option)
    (handler: PluginHandler<'State, 'Msg>)
    : RegisteredPlugin =

    let agent =
        MailboxProcessor<Choice<PluginEvent<'Msg>, AsyncReplyChannel<'State>>>.Start(fun inbox ->
            let ctx =
                { ReportStatus = fun s -> reportStatus handler.Name s
                  ReportErrors = fun file entries -> reportErrors handler.Name file entries
                  ClearErrors = fun file -> clearErrors handler.Name file
                  ClearAllErrors = fun () -> clearPlugin handler.Name
                  EmitBuildCompleted = emitBuildCompleted
                  EmitTestCompleted = emitTestCompleted
                  EmitCommandCompleted = emitCommandCompleted
                  Checker = checker
                  RepoRoot = repoRoot
                  Post = fun msg -> inbox.Post(Choice1Of2(Custom msg)) }

            /// Compute the composite key for a given event.
            let compositeKey (event: PluginEvent<'Msg>) =
                match event with
                | FileChecked r -> $"%s{handler.Name}--%s{r.File}"
                | FileChanged _ -> handler.Name
                | BuildCompleted _ -> handler.Name
                | TestCompleted _ -> handler.Name
                | CommandCompleted _ -> handler.Name
                | Custom _ -> handler.Name

            /// Try to replay a cached result. Returns true if cache hit.
            let tryReplayCache (event: PluginEvent<'Msg>) =
                match taskCache, handler.CacheKey with
                | Some cache, Some cacheKeyFn ->
                    match cacheKeyFn event with
                    | Some cacheKey ->
                        let compKey = compositeKey event

                        match cache.TryGet compKey cacheKey with
                        | Some result ->
                            // Clear stale errors before replay
                            match event with
                            | FileChecked r -> clearErrors handler.Name r.File
                            | _ -> clearPlugin handler.Name

                            // Replay errors
                            for (file, entries) in result.Errors do
                                if file = "*" then clearPlugin handler.Name
                                elif entries.IsEmpty then clearErrors handler.Name file
                                else reportErrors handler.Name file entries

                            // Replay status
                            reportStatus handler.Name result.Status

                            // Replay emitted events
                            for emitted in result.EmittedEvents do
                                match emitted with
                                | TaskCache.CachedBuildCompleted r -> emitBuildCompleted r
                                | TaskCache.CachedTestCompleted r -> emitTestCompleted r
                                | TaskCache.CachedCommandCompleted r -> emitCommandCompleted r

                            true
                        | None -> false
                    | None -> false
                | _ -> false

            let safeUpdate pluginCtx state event =
                async {
                    try
                        return! handler.Update pluginCtx state event
                    with ex ->
                        error handler.Name $"Plugin handler failed: %s{ex.ToString()}"
                        return state
                }

            /// Run Update with a capturing context that records side effects, then store in cache if terminal.
            let runAndCache (event: PluginEvent<'Msg>) (state: 'State) =
                async {
                    match taskCache, handler.CacheKey with
                    | Some cache, Some cacheKeyFn ->
                        match cacheKeyFn event with
                        | Some cacheKey ->
                            let capturedErrors = ResizeArray<string * ErrorEntry list>()
                            let capturedEvents = ResizeArray<TaskCache.CachedEvent>()
                            let mutable capturedStatus: PluginStatus option = None

                            let capturingCtx =
                                { ReportStatus =
                                    fun s ->
                                        capturedStatus <- Some s
                                        reportStatus handler.Name s
                                  ReportErrors =
                                    fun file entries ->
                                        capturedErrors.Add(file, entries)
                                        reportErrors handler.Name file entries
                                  ClearErrors =
                                    fun file ->
                                        capturedErrors.Add(file, [])
                                        clearErrors handler.Name file
                                  ClearAllErrors =
                                    fun () ->
                                        capturedErrors.Add("*", [])
                                        clearPlugin handler.Name
                                  EmitBuildCompleted =
                                    fun r ->
                                        capturedEvents.Add(TaskCache.CachedBuildCompleted r)
                                        emitBuildCompleted r
                                  EmitTestCompleted =
                                    fun r ->
                                        capturedEvents.Add(TaskCache.CachedTestCompleted r)
                                        emitTestCompleted r
                                  EmitCommandCompleted =
                                    fun r ->
                                        capturedEvents.Add(TaskCache.CachedCommandCompleted r)
                                        emitCommandCompleted r
                                  Checker = checker
                                  RepoRoot = repoRoot
                                  Post = fun msg -> inbox.Post(Choice1Of2(Custom msg)) }

                            let! nextState = safeUpdate capturingCtx state event

                            // Only cache when status reached a terminal state
                            match capturedStatus with
                            | Some(Completed _ as s)
                            | Some(Failed _ as s) ->
                                let compKey = compositeKey event

                                let result: TaskCache.TaskCacheResult =
                                    { CacheKey = cacheKey
                                      Errors = capturedErrors |> Seq.toList
                                      Status = s
                                      EmittedEvents = capturedEvents |> Seq.toList }

                                cache.Set compKey cacheKey result
                            | _ -> ()

                            return nextState
                        | None -> return! safeUpdate ctx state event
                    | _ -> return! safeUpdate ctx state event
                }

            let rec loop state =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Choice2Of2 ch ->
                        ch.Reply(state)
                        return! loop state
                    | Choice1Of2 event ->
                        if tryReplayCache event then
                            return! loop state
                        else
                            let! nextState = runAndCache event state
                            return! loop nextState
                }

            loop handler.Init)

    // Register commands
    for (cmdName, cmdHandler) in handler.Commands do
        registerCommand (
            cmdName,
            fun args ->
                async {
                    let! state = agent.PostAndAsyncReply(Choice2Of2)
                    return! cmdHandler state args
                }
        )

    // Build type-erased registration
    let post event = agent.Post(Choice1Of2 event)

    { Name = handler.Name
      OnFileChanged =
        if handler.Subscriptions.FileChanged then
            Some(fun c -> post (FileChanged c))
        else
            None
      OnFileChecked =
        if handler.Subscriptions.FileChecked then
            Some(fun r -> post (FileChecked r))
        else
            None
      OnBuildCompleted =
        if handler.Subscriptions.BuildCompleted then
            Some(fun r -> post (BuildCompleted r))
        else
            None
      OnTestCompleted =
        if handler.Subscriptions.TestCompleted then
            Some(fun r -> post (TestCompleted r))
        else
            None
      OnCommandCompleted =
        if handler.Subscriptions.CommandCompleted then
            Some(fun r -> post (CommandCompleted r))
        else
            None }
