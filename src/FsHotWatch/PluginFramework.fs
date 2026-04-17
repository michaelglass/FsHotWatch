/// Declarative plugin framework — define plugins as pure update functions,
/// the framework manages agents, error recovery, and event dispatch.
module FsHotWatch.PluginFramework

open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Logging
open FsHotWatch.Plugin

/// Opaque plugin name — prevents accidental mixing with other strings.
[<Struct>]
type PluginName = private PluginName of string

module PluginName =
    let create (name: string) = PluginName name
    let value (PluginName n) = n

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
        /// Start a named concurrent subtask. Duplicate keys are no-ops.
        StartSubtask: string -> string -> unit
        /// End a named subtask. No-op if not started.
        EndSubtask: string -> unit
        /// Append an activity log line. Also routes to Logging.info.
        Log: string -> unit
        /// Override the auto-derived summary captured on the next terminal transition.
        CompleteWithSummary: string -> unit
    }

/// Tags for events a plugin can subscribe to.
type SubscribedEvent =
    | SubscribeFileChanged
    | SubscribeFileChecked
    | SubscribeBuildCompleted
    | SubscribeTestCompleted
    | SubscribeCommandCompleted

/// Which events the plugin subscribes to.
type PluginSubscriptions = Set<SubscribedEvent>

/// Helper functions for PluginSubscriptions.
module PluginSubscriptions =
    /// No subscriptions — the plugin only handles Custom messages.
    let none: PluginSubscriptions = Set.empty

/// Declarative plugin definition.
[<NoComparison; NoEquality>]
type PluginHandler<'State, 'Msg> =
    {
        /// The display name of this plugin.
        Name: PluginName
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
        CacheKey: (PluginEvent<'Msg> -> ContentHash option) option
        /// Optional teardown function called when the plugin host is disposed.
        Teardown: (unit -> unit) option
    }

/// Type-erased event for host → plugin dispatch (no generic Custom variant).
[<NoComparison; NoEquality>]
type PluginDispatchEvent =
    | DispatchFileChanged of FileChangeKind
    | DispatchFileChecked of FileCheckResult
    | DispatchBuildCompleted of BuildResult
    | DispatchTestCompleted of TestResults
    | DispatchCommandCompleted of CommandCompletedResult

/// Type-erased plugin registration stored by PluginHost.
[<NoComparison; NoEquality>]
type RegisteredPlugin =
    {
        /// The display name of this plugin.
        Name: PluginName
        /// Dispatch an event to this plugin. Filtering by subscription is built in.
        Dispatch: PluginDispatchEvent -> unit
        /// Optional teardown function for releasing resources.
        Teardown: (unit -> unit) option
    }

/// Host-provided services bundled into a record to avoid fragile positional params.
[<NoComparison; NoEquality>]
type PluginHostServices =
    { Checker: FSharp.Compiler.CodeAnalysis.FSharpChecker
      RepoRoot: string
      ReportStatus: PluginName -> PluginStatus -> unit
      ReportErrors: PluginName -> string -> ErrorEntry list -> unit
      ClearErrors: PluginName -> string -> unit
      ClearPlugin: PluginName -> unit
      EmitBuildCompleted: BuildResult -> unit
      EmitTestCompleted: TestResults -> unit
      EmitCommandCompleted: CommandCompletedResult -> unit
      RegisterCommand: string * CommandHandler -> unit
      TaskCache: TaskCache.ITaskCache option
      StartSubtask: PluginName -> string -> string -> unit
      EndSubtask: PluginName -> string -> unit
      Log: PluginName -> string -> unit
      SetSummary: PluginName -> string -> unit }

/// Register a declarative plugin handler, returning a type-erased RegisteredPlugin.
/// Creates a MailboxProcessor with error recovery and wires up event dispatch.
let registerHandler (services: PluginHostServices) (handler: PluginHandler<'State, 'Msg>) : RegisteredPlugin =

    let agent =
        MailboxProcessor<Choice<PluginEvent<'Msg>, AsyncReplyChannel<'State>>>
            .Start(
                (fun inbox ->
                    let ctx =
                        { ReportStatus = fun s -> services.ReportStatus handler.Name s
                          ReportErrors = fun file entries -> services.ReportErrors handler.Name file entries
                          ClearErrors = fun file -> services.ClearErrors handler.Name file
                          ClearAllErrors = fun () -> services.ClearPlugin handler.Name
                          EmitBuildCompleted = services.EmitBuildCompleted
                          EmitTestCompleted = services.EmitTestCompleted
                          EmitCommandCompleted = services.EmitCommandCompleted
                          Checker = services.Checker
                          RepoRoot = services.RepoRoot
                          Post = fun msg -> inbox.Post(Choice1Of2(Custom msg))
                          StartSubtask = fun key label -> services.StartSubtask handler.Name key label
                          EndSubtask = fun key -> services.EndSubtask handler.Name key
                          Log = fun msg -> services.Log handler.Name msg
                          CompleteWithSummary = fun s -> services.SetSummary handler.Name s }

                    /// Compute the composite key for a given event.
                    let compositeKey (event: PluginEvent<'Msg>) : TaskCache.CompositeKey =
                        let nameStr = PluginName.value handler.Name

                        match event with
                        | FileChecked r -> { Plugin = nameStr; File = Some r.File }
                        | _ -> { Plugin = nameStr; File = None }

                    /// Try to replay a cached result. Returns true if cache hit.
                    let tryReplayCache (event: PluginEvent<'Msg>) =
                        match services.TaskCache, handler.CacheKey with
                        | Some cache, Some cacheKeyFn ->
                            match cacheKeyFn event with
                            | Some cacheKey ->
                                let compKey = compositeKey event

                                match cache.TryGet compKey cacheKey with
                                | Some result ->
                                    // Clear stale errors before replay
                                    match event with
                                    | FileChecked r -> services.ClearErrors handler.Name r.File
                                    | _ -> services.ClearPlugin handler.Name

                                    // Replay errors
                                    for (file, entries) in result.Errors do
                                        if file = "*" then
                                            services.ClearPlugin handler.Name
                                        elif entries.IsEmpty then
                                            services.ClearErrors handler.Name file
                                        else
                                            services.ReportErrors handler.Name file entries

                                    // Replay status
                                    services.ReportStatus handler.Name result.Status

                                    // Replay emitted events
                                    for emitted in result.EmittedEvents do
                                        match emitted with
                                        | TaskCache.CachedBuildCompleted r -> services.EmitBuildCompleted r
                                        | TaskCache.CachedTestCompleted r -> services.EmitTestCompleted r
                                        | TaskCache.CachedCommandCompleted r -> services.EmitCommandCompleted r

                                    true
                                | None -> false
                            | None -> false
                        | _ -> false

                    let safeUpdate pluginCtx state event =
                        async {
                            try
                                return! handler.Update pluginCtx state event
                            with ex ->
                                error (PluginName.value handler.Name) $"Plugin handler failed: %s{ex.ToString()}"
                                return state
                        }

                    /// Run Update with a capturing context that records side effects, then store in cache if terminal.
                    let runAndCache (event: PluginEvent<'Msg>) (state: 'State) =
                        async {
                            match services.TaskCache, handler.CacheKey with
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
                                                services.ReportStatus handler.Name s
                                          ReportErrors =
                                            fun file entries ->
                                                capturedErrors.Add(file, entries)
                                                services.ReportErrors handler.Name file entries
                                          ClearErrors =
                                            fun file ->
                                                capturedErrors.Add(file, [])
                                                services.ClearErrors handler.Name file
                                          ClearAllErrors =
                                            fun () ->
                                                capturedErrors.Add("*", [])
                                                services.ClearPlugin handler.Name
                                          EmitBuildCompleted =
                                            fun r ->
                                                capturedEvents.Add(TaskCache.CachedBuildCompleted r)
                                                services.EmitBuildCompleted r
                                          EmitTestCompleted =
                                            fun r ->
                                                capturedEvents.Add(TaskCache.CachedTestCompleted r)
                                                services.EmitTestCompleted r
                                          EmitCommandCompleted =
                                            fun r ->
                                                capturedEvents.Add(TaskCache.CachedCommandCompleted r)
                                                services.EmitCommandCompleted r
                                          Checker = services.Checker
                                          RepoRoot = services.RepoRoot
                                          Post = fun msg -> inbox.Post(Choice1Of2(Custom msg))
                                          StartSubtask = fun key label -> services.StartSubtask handler.Name key label
                                          EndSubtask = fun key -> services.EndSubtask handler.Name key
                                          Log = fun msg -> services.Log handler.Name msg
                                          CompleteWithSummary = fun s -> services.SetSummary handler.Name s }

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
            )

    // Register commands
    for (cmdName, cmdHandler) in handler.Commands do
        services.RegisterCommand(
            cmdName,
            fun args ->
                async {
                    let! state = agent.PostAndAsyncReply(Choice2Of2)
                    return! cmdHandler state args
                }
        )

    // Build type-erased registration with subscription-filtered dispatch
    let post event = agent.Post(Choice1Of2 event)
    let has e = handler.Subscriptions.Contains(e)

    let dispatch event =
        match event with
        | DispatchFileChanged c when has SubscribeFileChanged -> post (FileChanged c)
        | DispatchFileChecked r when has SubscribeFileChecked -> post (FileChecked r)
        | DispatchBuildCompleted r when has SubscribeBuildCompleted -> post (BuildCompleted r)
        | DispatchTestCompleted r when has SubscribeTestCompleted -> post (TestCompleted r)
        | DispatchCommandCompleted r when has SubscribeCommandCompleted -> post (CommandCompleted r)
        | _ -> ()

    { Name = handler.Name
      Dispatch = dispatch
      Teardown = handler.Teardown }

/// Ergonomic helpers over PluginCtx that every plugin tends to want.
module PluginCtxHelpers =

    /// Wrap `work` with matched StartSubtask / EndSubtask calls. `EndSubtask`
    /// fires even if `work` throws, via try/finally.
    let withSubtask (ctx: PluginCtx<'Msg>) (key: string) (label: string) (work: Async<'a>) : Async<'a> =
        async {
            ctx.StartSubtask key label

            try
                return! work
            finally
                ctx.EndSubtask key
        }

    /// Set the run summary and transition status to Completed at the current UTC time.
    let completeWith (ctx: PluginCtx<'Msg>) (summary: string) : unit =
        ctx.CompleteWithSummary summary
        ctx.ReportStatus(Completed System.DateTime.UtcNow)
