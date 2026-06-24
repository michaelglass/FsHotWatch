/// Declarative plugin framework — define plugins as pure update functions,
/// the framework manages agents, error recovery, and event dispatch.
module FsHotWatch.PluginFramework

open System
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
        /// Emit the start of a test run. Fires exactly once per run before any progress.
        EmitTestRunStarted: TestRunStarted -> unit
        /// Emit progress for a running test run (one or more groups just completed).
        /// Carries only newly-completed projects as a delta; subscribers that need
        /// cumulative state fold locally keyed by RunId.
        EmitTestProgress: TestProgress -> unit
        /// Emit the end of a test run. Fires exactly once per run. Carries the full
        /// cumulative Results so subscribers that don't listen to TestProgress can
        /// still see the final state.
        EmitTestRunCompleted: TestRunCompleted -> unit
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
        /// Update an existing subtask's label in-place. No-op if not started.
        UpdateSubtask: string -> string -> unit
        /// End a named subtask. No-op if not started.
        EndSubtask: string -> unit
        /// Append an activity log line. Also routes to Logging.info.
        Log: string -> unit
        /// Override the auto-derived summary captured on the next terminal transition.
        CompleteWithSummary: string -> unit
        /// Mark the next terminal transition as TimedOut with `reason`. Also sets
        /// the summary. The plugin is still responsible for calling
        /// `ReportStatus(Failed(..))` (or raising) so the state machine advances
        /// to a terminal state — the override is consumed when that fires.
        CompleteWithTimeout: string -> unit
        /// Run `work` exclusively under `key`. While running, additional calls
        /// with the same key are dropped (ignored). On completion, the
        /// framework posts the returned `'Msg` back to the agent's mailbox as
        /// a `Custom` event (mirroring the existing self-post pattern).
        ///
        /// If `work` throws, the exception is logged and no completion message
        /// is posted (the slot is freed). Plugins that need failure to flow
        /// back to Update should `try/with` inside `work` and return a
        /// sentinel `'Msg`.
        RunExclusive: string -> Async<'Msg> -> unit
        /// Whether `key` is currently running under `RunExclusive`. Plugins
        /// use this for IPC-facing status without maintaining their own
        /// "is running" bit.
        IsRunning: string -> bool
        /// Caller-configured FCS warning codes the host has been told to
        /// treat as noise. Plugins must merge this with per-file `#nowarn`
        /// directives (`FcsDiagnosticFilter.allSuppressedCodes`) before any
        /// gate or report decision so the user-visible error stream and any
        /// cache-poisoning gates agree on what counts as an error.
        FcsSuppressedCodes: Set<int>
        /// Read-only project-graph accessor for dependency-aware test selection.
        /// The daemon wires this from its live `ProjectGraph`; tests and the
        /// null-checker daemon leave it at the no-op default (every accessor
        /// returns empty/None), so a plugin that consults it simply sees "no
        /// graph" and falls back to its symbol-precise behaviour. All paths are
        /// absolute `.fsproj` paths.
        ProjectGraph: ProjectGraphAccessor
    }

/// Minimal read-only view of the project graph exposed to plugins via
/// `PluginCtx`. Closures (not the concrete `ProjectGraph`) so `PluginFramework`
/// stays free of a forward dependency on `ProjectGraph.fs` (which compiles
/// after it). All inputs/outputs are absolute `.fsproj` path strings; the DLL
/// path is the canonical `bin/Debug/<TFM>/<name>.dll`.
and [<NoComparison; NoEquality>] ProjectGraphAccessor =
    {
        /// Every registered project, as absolute `.fsproj` paths.
        GetAllProjects: unit -> string list
        /// Projects that directly or transitively ProjectReference the given
        /// project (excludes the project itself), as absolute `.fsproj` paths.
        GetTransitiveDependentProjects: string -> string list
        /// The given project's direct ProjectReferences, as absolute `.fsproj` paths.
        GetProjectReferences: string -> string list
        /// The given project's canonical compiled-DLL path, or None when the
        /// target framework couldn't be resolved.
        GetCanonicalDllPath: string -> string option
    }

module ProjectGraphAccessor =
    /// No-op accessor: no graph wired (tests, null-checker daemon). Every query
    /// returns empty/None, so dependency-fanout consumers fall back cleanly.
    let none: ProjectGraphAccessor =
        { GetAllProjects = fun () -> []
          GetTransitiveDependentProjects = fun _ -> []
          GetProjectReferences = fun _ -> []
          GetCanonicalDllPath = fun _ -> None }

/// Tags for events a plugin can subscribe to.
type SubscribedEvent =
    | SubscribeFileChanged
    | SubscribeFileChecked
    | SubscribeBatchChecked
    | SubscribeBuildCompleted
    | SubscribeTestRunStarted
    | SubscribeTestProgress
    | SubscribeTestRunCompleted
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
        /// Named commands that can be invoked via IPC. Each command receives the
        /// plugin context (for `IsRunning`, `Log`, etc.), current state, and args.
        /// `ctx` is typically `_ctx` for commands that don't need it.
        Commands: (string * (PluginCtx<'Msg> -> 'State -> string array -> Async<string>)) list
        /// Which events the plugin subscribes to.
        Subscriptions: PluginSubscriptions
        /// Optional cache key function. `Some hash` → look up the cache and replay on hit.
        /// `None` → skip cache and run Update — overloaded across "uncacheable event",
        /// "cold-start bypass", and "outputs missing"; plugins document which at the call site.
        CacheKey: (PluginEvent<'Msg> -> ContentHash option) option
        /// Optional teardown function called when the plugin host is disposed.
        Teardown: (unit -> unit) option
    }

/// Type-erased event for host → plugin dispatch (no generic Custom variant).
[<NoComparison; NoEquality>]
type PluginDispatchEvent =
    | DispatchFileChanged of FileChangeKind
    | DispatchFileChecked of FileCheckResult
    | DispatchBatchChecked of BatchChecked
    | DispatchBuildCompleted of BuildResult
    | DispatchTestRunStarted of TestRunStarted
    | DispatchTestProgress of TestProgress
    | DispatchTestRunCompleted of TestRunCompleted
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
        /// True iff this plugin has at least one event still pending in its
        /// mailbox or actively being processed by its handler. Used by
        /// `WaitForComplete` to avoid the race where a plugin's status is
        /// observably Idle but an event has been posted to its mailbox and
        /// will trigger work as soon as the handler runs.
        IsBusy: unit -> bool
    }

/// Host-provided services bundled into a record to avoid fragile positional params.
[<NoComparison; NoEquality>]
type PluginHostServices =
    {
        Checker: FSharp.Compiler.CodeAnalysis.FSharpChecker
        RepoRoot: string
        ReportStatus: PluginName -> PluginStatus -> unit
        ReportErrors: PluginName -> string -> ErrorEntry list -> unit
        ClearErrors: PluginName -> string -> unit
        ClearPlugin: PluginName -> unit
        EmitBuildCompleted: BuildResult -> unit
        EmitTestRunStarted: TestRunStarted -> unit
        EmitTestProgress: TestProgress -> unit
        EmitTestRunCompleted: TestRunCompleted -> unit
        EmitCommandCompleted: CommandCompletedResult -> unit
        RegisterCommand: string * CommandHandler -> unit
        TaskCache: TaskCache.ITaskCache option
        StartSubtask: PluginName -> string -> string -> unit
        UpdateSubtask: PluginName -> string -> string -> unit
        EndSubtask: PluginName -> string -> unit
        Log: PluginName -> string -> unit
        SetSummary: PluginName -> string -> unit
        /// Set the outcome recorded on the next terminal transition. Lets a plugin
        /// flip the run's stored outcome (e.g. to TimedOut) without introducing a
        /// new PluginStatus variant.
        SetNextTerminalOutcome: PluginName -> RunOutcome -> unit
        /// Caller-configured FCS warning codes treated as noise. Threaded into
        /// every plugin's `PluginCtx.FcsSuppressedCodes` so plugin-level gates
        /// stay in sync with the user-visible diagnostic filter.
        FcsSuppressedCodes: Set<int>
        /// Read-only project-graph accessor wired into every plugin's
        /// `PluginCtx.ProjectGraph`. The host supplies `ProjectGraphAccessor.none`
        /// until the daemon installs the live graph.
        ProjectGraph: ProjectGraphAccessor
    }

/// Register a declarative plugin handler, returning a type-erased RegisteredPlugin.
/// Creates a MailboxProcessor with error recovery and wires up event dispatch.
let registerHandler (services: PluginHostServices) (handler: PluginHandler<'State, 'Msg>) : RegisteredPlugin =

    // Per-handler run-slot state for ctx.RunExclusive. Keyed by the user-supplied
    // string. `true` means a call is in flight; absent or `false` means idle.
    // While running, additional calls under the same key are dropped. Mutated
    // only inside `runSlotsLock`.
    let runSlots = System.Collections.Generic.Dictionary<string, bool>()

    let runSlotsLock = obj ()

    // Forward reference to the agent so `post` and `runOne` can route completion
    // messages back without an inbox closure. Set immediately after Start returns;
    // any access before then is impossible by construction (no caller can invoke
    // ctx until registerHandler returns the RegisteredPlugin).
    let mutable agentRef: MailboxProcessor<Choice<PluginEvent<'Msg>, AsyncReplyChannel<'State>>> option =
        None

    // Per-plugin inflight counter: incremented every time a Choice1Of2 event
    // is posted to the agent's mailbox, decremented after the agent has
    // finished handling that event. `WaitForComplete` consults this via
    // `RegisteredPlugin.IsBusy` to avoid declaring quiescence while events are
    // still queued or being processed but the plugin's status is observably
    // Idle (e.g. handler hasn't yet called ReportStatus(Running)).
    let inflightCount = ref 0

    let post (msg: 'Msg) =
        match agentRef with
        | Some a ->
            System.Threading.Interlocked.Increment(&inflightCount.contents) |> ignore
            a.Post(Choice1Of2(Custom msg))
        | None -> ()

    let runOne (key: string) (w: Async<'Msg>) =
        async {
            let mutable completion: 'Msg voption = ValueNone

            // F15 (audit 2026-05-02): the work async is plugin-supplied — a
            // third-party-extension boundary that may raise anything. The
            // broad catch is what guarantees the surrounding `finally` runs
            // (releasing the runSlots entry); without it, an unhandled
            // exception would skip the `with` and still hit the `finally`
            // via async-exception propagation, but the `completion` post
            // path ahead of `finally` would never receive a value, leaving
            // the agent hanging waiting for the result. We log ex.ToString()
            // so the type and stack trace are preserved for diagnosing the
            // offending plugin.
            try
                try
                    let! msg = w
                    completion <- ValueSome msg
                with ex ->
                    error (PluginName.value handler.Name) $"RunExclusive '%s{key}' work failed: %s{ex.ToString()}"
            finally
                lock runSlotsLock (fun () -> runSlots.[key] <- false)

                match completion with
                | ValueSome m -> post m
                | ValueNone -> ()
        }

    let runExclusive (key: string) (work: Async<'Msg>) =
        let shouldStart =
            lock runSlotsLock (fun () ->
                match runSlots.TryGetValue(key) with
                | true, true -> false
                | _ ->
                    runSlots.[key] <- true
                    true)

        if shouldStart then
            Async.Start(runOne key work)
        else
            // AUTOMATION-15 (item 5): exclusion-slot contention. A new run was
            // requested while `key` is still busy — the framework drops it
            // rather than stacking. Surfacing it as a debug diagnostic makes the
            // "why didn't my edit re-run this plugin?" / "is it wedged on a
            // long run?" question answerable from the log instead of guessing.
            debug
                (PluginName.value handler.Name)
                $"exclusion-slot busy: '%s{key}' run skipped (a previous run is still in flight)"

    let isRunning (key: string) =
        lock runSlotsLock (fun () ->
            match runSlots.TryGetValue(key) with
            | true, running -> running
            | _ -> false)

    // Standard ctx — used both inside the agent loop (via Update) and from
    // IPC command handlers (via the wrapper in `services.RegisterCommand`).
    let ctx: PluginCtx<'Msg> =
        { ReportStatus = fun s -> services.ReportStatus handler.Name s
          ReportErrors = fun file entries -> services.ReportErrors handler.Name file entries
          ClearErrors = fun file -> services.ClearErrors handler.Name file
          ClearAllErrors = fun () -> services.ClearPlugin handler.Name
          EmitBuildCompleted = services.EmitBuildCompleted
          EmitTestRunStarted = services.EmitTestRunStarted
          EmitTestProgress = services.EmitTestProgress
          EmitTestRunCompleted = services.EmitTestRunCompleted
          EmitCommandCompleted = services.EmitCommandCompleted
          Checker = services.Checker
          RepoRoot = services.RepoRoot
          Post = post
          StartSubtask = fun key label -> services.StartSubtask handler.Name key label
          UpdateSubtask = fun key label -> services.UpdateSubtask handler.Name key label
          EndSubtask = fun key -> services.EndSubtask handler.Name key
          Log = fun msg -> services.Log handler.Name msg
          CompleteWithSummary = fun s -> services.SetSummary handler.Name s
          CompleteWithTimeout =
            fun reason ->
                services.SetSummary handler.Name $"timed out after {reason}"
                services.SetNextTerminalOutcome handler.Name (TimedOut reason)
          RunExclusive = runExclusive
          IsRunning = isRunning
          FcsSuppressedCodes = services.FcsSuppressedCodes
          ProjectGraph = services.ProjectGraph }

    let agent =
        MailboxProcessor<Choice<PluginEvent<'Msg>, AsyncReplyChannel<'State>>>
            .Start(
                (fun inbox ->

                    /// Compute the composite key for a given event.
                    let compositeKey (event: PluginEvent<'Msg>) : TaskCache.CompositeKey =
                        let nameStr = PluginName.value handler.Name

                        match event with
                        | FileChecked r ->
                            { Plugin = nameStr
                              File = Some(AbsFilePath.value r.File) }
                        | _ -> { Plugin = nameStr; File = None }

                    /// Try to replay a cached result. Returns true if cache hit.
                    /// The pre-BatchChecked design used a `RequireWarmStart`
                    /// gate here to suppress replay until the plugin reached a
                    /// terminal state once per session — needed because the
                    /// per-`FileChecked` accumulation that fed plugin cache
                    /// keys (TestPrune's `changedSymbolsRef`, BuildPlugin's
                    /// `BuildInputsHasher`) wasn't fully populated when the
                    /// very first dispatch hit. Now that cohort completion is
                    /// signalled by `BatchChecked`, every subsequent
                    /// cacheable event (`BuildCompleted`, etc.) sees a fully
                    /// populated key and the gate is gone.
                    /// `cacheKeyOpt` is the key for this event, computed ONCE by
                    /// the dispatch loop and threaded here so the lookup and the
                    /// later store share the exact same value (computing a
                    /// BuildPlugin key is a full content-hash of the project
                    /// graph — recomputing per call doubled that cost per
                    /// trigger). Threading the single value is also strictly
                    /// safer than recomputing: lookup key ≡ store key by
                    /// construction.
                    let tryReplayCache (event: PluginEvent<'Msg>) (cacheKeyOpt: ContentHash option) =
                        match services.TaskCache, cacheKeyOpt with
                        | Some cache, Some cacheKey ->
                            let compKey = compositeKey event
                            let lookupResult = cache.TryGet compKey cacheKey
                            // §2a measurement A: per-plugin hit/miss counts. Filter post-hoc.
                            let pluginName = PluginName.value handler.Name

                            FsHotWatch.Logging.debug "task-cache" $"plugin=%s{pluginName} hit=%b{lookupResult.IsSome}"

                            match lookupResult with
                            | Some result ->
                                // Clear stale errors before replay
                                match event with
                                | FileChecked r -> services.ClearErrors handler.Name (AbsFilePath.value r.File)
                                | _ -> services.ClearPlugin handler.Name

                                // Replay errors
                                for (file, entries) in result.Errors do
                                    if file = "*" then
                                        services.ClearPlugin handler.Name
                                    elif entries.IsEmpty then
                                        services.ClearErrors handler.Name file
                                    else
                                        services.ReportErrors handler.Name file entries

                                // Replay status. Rewrite the timestamp to now: the cached
                                // status carries the ORIGINAL run's terminal time (often a
                                // prior session). If a `Running since=now` had been set in
                                // this session, the activity log's RecordTerminal would
                                // compute `elapsed = cached_at - now` and produce nonsense
                                // (negative) elapsed. From this session's POV, the work
                                // "completed" instantly via cache replay.
                                let nowAt = System.DateTime.UtcNow

                                let replayStatus =
                                    match result.Status with
                                    | Completed _ -> Completed nowAt
                                    | Failed(err, _) -> Failed(err, nowAt)
                                    | s -> s

                                services.ReportStatus handler.Name replayStatus

                                // Replay emitted events. Cached test-lifecycle events carry the
                                // ORIGINAL run's RunId, which would cause RunId-based dedup (e.g.
                                // FileCommand) to skip the replay as if it were the same run. Swap
                                // in a single fresh RunId shared across the three test events so
                                // the cache hit looks like a distinct run.
                                let freshRunId = System.Lazy<System.Guid>(System.Guid.NewGuid)

                                for emitted in result.EmittedEvents do
                                    match emitted with
                                    | TaskCache.CachedBuildCompleted r -> services.EmitBuildCompleted r
                                    | TaskCache.CachedTestRunStarted r ->
                                        services.EmitTestRunStarted { r with RunId = freshRunId.Value }
                                    | TaskCache.CachedTestProgress r ->
                                        services.EmitTestProgress { r with RunId = freshRunId.Value }
                                    | TaskCache.CachedTestRunCompleted r ->
                                        services.EmitTestRunCompleted { r with RunId = freshRunId.Value }
                                    | TaskCache.CachedCommandCompleted r -> services.EmitCommandCompleted r

                                true
                            | None -> false
                        | _ -> false

                    /// Invariant: a handler that throws out of `Update` must never leave the
                    /// plugin stuck in whatever transient status it reported before the throw
                    /// (classic case: plugin reports Running, hits a DB error, never reports
                    /// terminal status, UI shows "running" forever). We surface the exception
                    /// as PluginStatus.Failed *after* catching so the observable status always
                    /// reaches a terminal state, regardless of what the handler did beforehand.
                    let safeUpdate pluginCtx state event =
                        async {
                            try
                                return! handler.Update pluginCtx state event
                            with ex ->
                                error (PluginName.value handler.Name) $"Plugin handler failed: %s{ex.ToString()}"
                                services.ReportStatus handler.Name (Failed(ex.ToString(), DateTime.UtcNow))
                                return state
                        }

                    /// Run Update with a capturing context that records side effects, then store in cache if terminal.
                    /// `cacheKeyOpt` is the same key the preceding `tryReplayCache`
                    /// lookup used (computed once per event in the dispatch loop)
                    /// — never recompute it here.
                    let runAndCache (event: PluginEvent<'Msg>) (state: 'State) (cacheKeyOpt: ContentHash option) =
                        async {
                            match services.TaskCache, cacheKeyOpt with
                            | Some cache, Some cacheKey ->
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
                                      EmitTestRunStarted =
                                        fun r ->
                                            capturedEvents.Add(TaskCache.CachedTestRunStarted r)
                                            services.EmitTestRunStarted r
                                      EmitTestProgress =
                                        fun r ->
                                            capturedEvents.Add(TaskCache.CachedTestProgress r)
                                            services.EmitTestProgress r
                                      EmitTestRunCompleted =
                                        fun r ->
                                            capturedEvents.Add(TaskCache.CachedTestRunCompleted r)
                                            services.EmitTestRunCompleted r
                                      EmitCommandCompleted =
                                        fun r ->
                                            capturedEvents.Add(TaskCache.CachedCommandCompleted r)
                                            services.EmitCommandCompleted r
                                      Checker = services.Checker
                                      RepoRoot = services.RepoRoot
                                      Post = post
                                      StartSubtask = fun key label -> services.StartSubtask handler.Name key label
                                      UpdateSubtask = fun key label -> services.UpdateSubtask handler.Name key label
                                      EndSubtask = fun key -> services.EndSubtask handler.Name key
                                      Log = fun msg -> services.Log handler.Name msg
                                      CompleteWithSummary = fun s -> services.SetSummary handler.Name s
                                      CompleteWithTimeout =
                                        fun reason ->
                                            services.SetSummary handler.Name $"timed out after {reason}"
                                            services.SetNextTerminalOutcome handler.Name (TimedOut reason)
                                      RunExclusive = runExclusive
                                      IsRunning = isRunning
                                      FcsSuppressedCodes = services.FcsSuppressedCodes
                                      ProjectGraph = services.ProjectGraph }

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
                                // Compute the cache key ONCE per dispatched event and thread the
                                // single value to both the lookup (tryReplayCache) and the store
                                // (runAndCache). The framework used to call `cacheKeyFn event`
                                // twice per dispatch; for BuildPlugin that key is a full
                                // content-hash of the project graph, so a miss paid two SHA-256
                                // passes per trigger. Threading one value also guarantees the
                                // lookup key equals the store key by construction.
                                let cacheKeyOpt =
                                    match handler.CacheKey with
                                    | Some cacheKeyFn -> cacheKeyFn event
                                    | None -> None

                                let! nextState =
                                    async {
                                        try
                                            if tryReplayCache event cacheKeyOpt then
                                                return state
                                            else
                                                return! runAndCache event state cacheKeyOpt
                                        finally
                                            // Decrement only after the handler
                                            // (or cache replay) finishes — until
                                            // then `IsBusy` must report true so
                                            // WaitForComplete doesn't return
                                            // before this event has actually been
                                            // processed.
                                            System.Threading.Interlocked.Decrement(&inflightCount.contents) |> ignore
                                    }

                                return! loop nextState
                        }

                    loop handler.Init)
            )

    agentRef <- Some agent

    // Register commands
    for (cmdName, cmdHandler) in handler.Commands do
        services.RegisterCommand(
            cmdName,
            fun args ->
                async {
                    let! state = agent.PostAndAsyncReply(Choice2Of2)
                    return! cmdHandler ctx state args
                }
        )

    // Build type-erased registration with subscription-filtered dispatch
    let post event =
        System.Threading.Interlocked.Increment(&inflightCount.contents) |> ignore
        agent.Post(Choice1Of2 event)

    let has e = handler.Subscriptions.Contains(e)

    let dispatch event =
        match event with
        | DispatchFileChanged c when has SubscribeFileChanged -> post (FileChanged c)
        | DispatchFileChecked r when has SubscribeFileChecked -> post (FileChecked r)
        | DispatchBatchChecked r when has SubscribeBatchChecked -> post (BatchChecked r)
        | DispatchBuildCompleted r when has SubscribeBuildCompleted -> post (BuildCompleted r)
        | DispatchTestRunStarted r when has SubscribeTestRunStarted -> post (TestRunStarted r)
        | DispatchTestProgress r when has SubscribeTestProgress -> post (TestProgress r)
        | DispatchTestRunCompleted r when has SubscribeTestRunCompleted -> post (TestRunCompleted r)
        | DispatchCommandCompleted r when has SubscribeCommandCompleted -> post (CommandCompleted r)
        | _ -> ()

    { Name = handler.Name
      Dispatch = dispatch
      Teardown = handler.Teardown
      IsBusy = fun () -> System.Threading.Volatile.Read(&inflightCount.contents) > 0 }

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

    /// Report or clear the per-file error scope based on whether any entries exist.
    /// Used by per-file analyzers (Lint, Analyzers, FormatCheck) so that a file
    /// transitions cleanly between "has findings" and "clean" without leaking
    /// stale entries.
    let reportOrClearFile (ctx: PluginCtx<'Msg>) (file: string) (entries: ErrorEntry list) : unit =
        if entries.IsEmpty then
            ctx.ClearErrors file
        else
            ctx.ReportErrors file entries
