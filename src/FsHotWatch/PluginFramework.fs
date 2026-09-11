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

/// The outcome of claiming an exclusive run slot (`PluginCtx.RunExclusive`).
/// BOTH cases must be handled — silently dropping a refused claim hangs an IPC
/// caller forever and lets `test-rerun` exit 0 having run nothing (AUTOMATION-99).
/// `TreatWarningsAsErrors` + FS0020 make an unhandled result a compile error;
/// discarding one deliberately requires a greppable `ignore` (which the
/// FSHW-CLAIM-001 analyzer flags).
type RunClaim =
    /// The slot was claimed and the work is running; its completion message
    /// will be posted back to the plugin's mailbox.
    | Claimed
    /// Another run holds the slot — the work was NOT started and no completion
    /// message will ever arrive. The caller must decide: skip (when a live run
    /// already covers the need) or queue (when the work is owed).
    | SlotBusy

/// Outcome of atomically claiming a plugin-local slot and a host-wide lease.
type SharedRunClaim =
    /// The shared resource was idle and work started immediately.
    | SharedClaimed
    /// An older owner exists; work is accounted now and will start by fair handoff.
    | SharedQueued
    /// This plugin's local slot is already occupied; the caller still owns this debt.
    | LocalSlotBusy

type SharedResourceState =
    | Ready
    | Invalid of reason: string

type SharedRunStarter<'Msg> =
    string
        -> string
        -> (SharedResourceState -> Async<'Msg>)
        -> ('Msg -> SharedResourceState)
        -> (exn -> 'Msg)
        -> SharedRunClaim

/// A failed starter retains its local obligation until the scheduler has
/// completed resource handoff. The acknowledgement carries cleanup failure too.
[<NoComparison; NoEquality>]
type SharedRunStart =
    | SharedStarted
    | SharedStartFailed of afterRelease: (Result<unit, exn> -> unit)

let private finishSharedStartFailure release afterRelease =
    let outcome =
        try
            release ()
            Ok()
        with ex ->
            Result.Error ex

    afterRelease outcome

/// Fair host-wide scheduler for resources shared by otherwise independent plugins.
/// Ownership is handed directly to the oldest waiter, so a releasing plugin cannot
/// repeatedly reacquire ahead of already-owed work.
type SharedRunScheduler() =
    let gate = obj ()
    let owners = System.Collections.Generic.HashSet<string>()

    let resourceStates =
        System.Collections.Generic.Dictionary<string, SharedResourceState>()

    let waiters =
        System.Collections.Generic.Dictionary<
            string,
            System.Collections.Generic.Queue<SharedResourceState -> SharedRunStart>
         >()

    member _.ClaimOrQueue(key: string, start: SharedResourceState -> SharedRunStart) =
        lock gate (fun () ->
            if owners.Add key then
                match resourceStates.TryGetValue key with
                | true, state -> Some state
                | _ -> Some Ready
            else
                let queue =
                    match waiters.TryGetValue key with
                    | true, existing -> existing
                    | _ ->
                        let created =
                            System.Collections.Generic.Queue<SharedResourceState -> SharedRunStart>()

                        waiters[key] <- created
                        created

                queue.Enqueue start
                None)

    member _.Release(key: string, resourceState: SharedResourceState) =
        let rec handOff state =
            let next =
                lock gate (fun () ->
                    resourceStates[key] <- state

                    match waiters.TryGetValue key with
                    | true, queue when queue.Count > 0 -> Some(queue.Dequeue())
                    | _ ->
                        owners.Remove key |> ignore
                        waiters.Remove key |> ignore
                        None)

            match next with
            | None -> ()
            | Some start ->
                let started =
                    try
                        start state
                    with _ ->
                        SharedStartFailed(fun _ -> ())

                match started with
                | SharedStarted -> ()
                | SharedStartFailed afterRelease ->
                    finishSharedStartFailure (fun () -> handOff (Invalid "shared waiter failed to start")) afterRelease

        handOff resourceState

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
        /// Mark the next terminal transition as TimedOut with `reason`. The
        /// plugin is still responsible for reporting a terminal `Failed`
        /// (whose verdict carries the summary) so the state machine advances —
        /// the override is consumed when that fires.
        CompleteWithTimeout: string -> unit
        /// Try to run `work` exclusively under `key`, returning whether the
        /// slot was `Claimed` or is held by a prior run (`SlotBusy`). On a
        /// claim the framework itself reports `Running`, and on completion
        /// posts the returned `'Msg` back to the agent's mailbox as a `Custom`
        /// event.
        ///
        /// If `work` throws, the exception is logged, no completion message is
        /// posted (the slot is freed), and the framework forces a terminal
        /// `Failed` so the plugin never strands in `Running`. Plugins that
        /// need failure to flow back to Update should `try/with` inside `work`
        /// and return a sentinel `'Msg`.
        ///
        /// The result must be handled: a dropped `SlotBusy` is dropped WORK.
        /// Match it and either skip-with-reason or queue.
        RunExclusive: string -> Async<'Msg> -> RunClaim
        /// Atomically claim a local slot and enter a fair host-wide resource queue.
        /// Both SharedClaimed and SharedQueued mean the framework owns the work;
        /// only LocalSlotBusy requires the caller to retain or merge the debt.
        RunExclusiveShared: SharedRunStarter<'Msg>
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
        /// Completed discovery identity. Unavailable or changing models cannot earn test proof.
        ObserveModel: unit -> ProjectModel.Observation
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
        { ObserveModel = fun () -> ProjectModel.Observation.Unobserved
          GetAllProjects = fun () -> []
          GetTransitiveDependentProjects = fun _ -> []
          GetProjectReferences = fun _ -> []
          GetCanonicalDllPath = fun _ -> None }

/// The DELIBERATELY narrow context handed to IPC command handlers
/// (`PluginHandler.Commands`). Commands run on the IPC thread, outside the
/// plugin's mailbox and outside its inflight accounting — so work started there
/// would be invisible to `IsRunning`/`AnyPluginBusy`/the status model
/// (AUTOMATION-99). Hence no `ReportStatus`, no `RunExclusive`, no `Emit*`:
/// `Post` is the ONLY way a command can cause work, and the work then runs on
/// the mailbox, accounted like every other launch.
[<NoComparison; NoEquality>]
type CommandCtx<'Msg> =
    {
        /// The repository root directory.
        RepoRoot: string
        /// Append an activity log line. Also routes to Logging.info.
        Log: string -> unit
        /// Post a message to the plugin's agent — the only way a command may
        /// cause work to happen.
        Post: 'Msg -> unit
        /// Whether `key` is currently running under `RunExclusive`.
        IsRunning: string -> bool
        /// Read-only project-graph accessor.
        ProjectGraph: ProjectGraphAccessor
    }

/// Observation capabilities contain no route for posting work.
[<NoComparison; NoEquality>]
type CommandReadCtx =
    { RepoRoot: string
      Log: string -> unit
      IsRunning: string -> bool
      ProjectGraph: ProjectGraphAccessor }

/// Reads receive committed state; requests carry intent without inspecting state.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type PluginCommand<'State, 'Msg> =
    | Observe of (CommandReadCtx -> 'State -> string array -> Async<string>)
    | Request of (CommandCtx<'Msg> -> string array -> Async<string>)

module PluginCommand =
    let readContext (ctx: CommandCtx<'Msg>) : CommandReadCtx =
        { RepoRoot = ctx.RepoRoot
          Log = ctx.Log
          IsRunning = ctx.IsRunning
          ProjectGraph = ctx.ProjectGraph }

    /// Interpret a command against an explicit state, also used by isolated handler tests.
    let invoke command ctx state args =
        match command with
        | PluginCommand.Observe read -> read (readContext ctx) state args
        | PluginCommand.Request request -> request ctx args

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
type PreparedCommit = { Finalize: Async<unit> }

/// A handler's proposal is published only after preparation succeeds; its event
/// remains owned until finalization and any cache write have completed.
[<NoComparison; NoEquality>]
type PluginHandler<'State, 'Msg> =
    {
        /// The display name of this plugin.
        Name: PluginName
        /// Initial state.
        Init: 'State
        /// Pure-ish update function: given context, current state, and event, produce next state.
        Update: PluginCtx<'Msg> -> 'State -> PluginEvent<'Msg> -> Async<'State>
        PrepareCommit: ('State -> 'State -> Async<PreparedCommit>) option
        /// Named IPC commands. Observations cannot post work; requests cannot
        /// inspect plugin state and must post state-dependent intent to its owner.
        Commands: (string * PluginCommand<'State, 'Msg>) list
        /// Which events the plugin subscribes to.
        Subscriptions: PluginSubscriptions
        /// Cache decisions consume the same committed state as Update; no mirrored state is required.
        /// Optional cache key function. `Some hash` → look up the cache and replay on hit.
        /// `None` → skip cache and run Update — overloaded across "uncacheable event",
        /// "cold-start bypass", and "outputs missing"; plugins document which at the call site.
        CacheKey: ('State -> PluginEvent<'Msg> -> ContentHash option) option
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

/// Identity-bearing completion witness for one dispatched event. Waiting is always
/// bounded and never inferred from an unrelated event's progress counter.
[<NoComparison; NoEquality>]
type DispatchReceipt =
    private
    | DispatchReceipt of System.Threading.Tasks.Task<unit>

    member this.Wait(timeout: TimeSpan) =
        let (DispatchReceipt completion) = this
        completion.WaitAsync(timeout)

/// Type-erased plugin registration stored by PluginHost.
[<NoComparison; NoEquality>]
type RegisteredPlugin =
    {
        /// The display name of this plugin.
        Name: PluginName
        /// Dispatch an event to this plugin. Filtering by subscription is built in.
        Dispatch: PluginDispatchEvent -> unit
        /// None means the event was not subscribed. Success acknowledges this
        /// event's state publication; executor failure faults accepted receipts.
        /// Admission to an already-faulted executor raises its recorded failure.
        DispatchTracked: PluginDispatchEvent -> DispatchReceipt option
        /// Optional teardown function for releasing resources.
        Teardown: (unit -> unit) option
        /// True iff this plugin owns a pending/active event or exclusive worker,
        /// including that worker's completion processing and cleanup. Used by
        /// `WaitForComplete` to avoid the race where a plugin's status is
        /// observably Idle but an event has been posted to its mailbox and
        /// will trigger work as soon as the handler runs.
        IsBusy: unit -> bool
        /// How many dispatched events this plugin has FINISHED handling, ever.
        /// Monotonic, published with the domain state when the event obligation
        /// is retired by the owner.
        ///
        /// The difference between "busy" and "making progress". A plugin draining
        /// a long `FileChecked` backlog is busy continuously, with nothing
        /// `Running`, for as long as the drain takes — and the set of busy plugins
        /// does not change either. Only this counter tells that apart from a stuck
        /// plugin.
        CompletedDispatches: unit -> int64
        /// What the handler subscribed to — so the host can tell which plugins a
        /// re-fired `FileChanged` would reach (`PluginHost.RerunPlugin`).
        Subscriptions: PluginSubscriptions
        /// The latest owned event, persistence, or executor failure, if one exists.
        ///
        /// Published in the same owner snapshot as work obligations. A fault
        /// fails accepted event receipts but preserves live exclusive workers
        /// until cleanup. It remains observable after those workers finish;
        /// empty work is not evidence that the executor is healthy.
        Fault: unit -> exn option
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
        /// Read this plugin's CURRENT ledger set (file -> entries) — the same
        /// set `fshw status` lists and the verdict gates on. The cache-replay
        /// path derives per-file entries' summaries from it, since a per-file
        /// cache entry carries no summary of its own (AUTOMATION-186).
        GetPluginDiagnostics: PluginName -> Map<string, ErrorEntry list>
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
        /// Starts plugin work. Supplied by the host so the synchronous failure
        /// boundary is deterministic in framework tests.
        StartAsync: Async<unit> -> unit
        /// Enter the FIFO for a host-wide resource. True means start now; false
        /// means `start` is retained and invoked on direct ownership handoff.
        ClaimOrQueueSharedRun: string -> (SharedResourceState -> SharedRunStart) -> SharedResourceState option
        /// Release ownership and wake exactly the oldest waiter, if present.
        ReleaseSharedRun: string -> SharedResourceState -> unit
    }

/// The replay summary for a per-file cache entry, derived from the plugin's LIVE
/// ledger set — the same findings the verdict is computed from, read AFTER the
/// entry's own error replay has landed. Never taken from the stored entry: a
/// per-file key cannot testify to a whole-session claim (the scope rule on
/// `TaskCache.CachedStatus`). Non-empty by construction (counts always render), so
/// `RunVerdict.create` can never throw on it.
let internal ledgerSummary (diagnosticsByFile: Map<string, ErrorEntry list>) : string =
    let allEntries = diagnosticsByFile |> Map.toList |> List.collect snd
    let counts = DiagnosticCounts.ofEntries allEntries
    let findings = List.length allEntries

    $"%d{findings} findings (%d{counts.Errors} errors, %d{counts.Warnings} warnings)"

/// Register a declarative plugin handler, returning a type-erased RegisteredPlugin.
/// Creates a MailboxProcessor with error recovery and wires up event dispatch.
let internal registerHandlerWithOwner
    (store: PluginWorkOwner.Store)
    (services: PluginHostServices)
    (handler: PluginHandler<'State, 'Msg>)
    : RegisteredPlugin =

    let workOwner =
        PluginWorkOwner.Owner(handler.Init, store, PluginName.value handler.Name)

    // Dispatch may arrive from a short-lived scan/batch scope. An exclusive
    // worker belongs to this registered plugin and can outlive that trigger.
    let ownerContext = System.Threading.ExecutionContext.Capture()

    let startOwned operation =
        if isNull ownerContext then
            services.StartAsync operation
        else
            System.Threading.ExecutionContext.Run(
                ownerContext.CreateCopy(),
                System.Threading.ContextCallback(fun _ -> services.StartAsync operation),
                null
            )

    let anyRunSlotBusy () = workOwner.Snapshot.HasExclusiveRun

    /// Serialises "decide whether a live run owns the status" + "publish it"
    /// against "claim a run slot" + "publish the `Running` that claim earns", so the
    /// ownership DECISION and the REPORT are ONE critical section. Without it the
    /// guard is a check-then-act, and a claim landing between the read and the
    /// report lets a stale terminal land ON TOP of the live run (the "✓ while tests
    /// are still running" signature: `started:` with no `elapsed:`). `PluginCtx` is
    /// a record of closures, so a plugin may legally claim a slot from a `work`
    /// async or a spawned task and reach this concurrently.
    ///
    /// This remaining UI-report lock orders reports against claims. Work and
    /// domain state are owned separately by the single immutable owner snapshot;
    /// the final host evidence migration removes UI status as gate authority.
    let statusLock = obj ()

    /// Publish `s` unless a live exclusive run owns this plugin's status; returns
    /// whether it was published. ATOMIC against `runExclusive`'s claim.
    ///
    /// While an exclusive run is in flight the run OWNS this plugin's status: it
    /// was reported `Running` at the claim and its completion path is guaranteed
    /// to deliver the earned terminal (the completion handler on success,
    /// `runOne`'s forced `Failed` on a faulted work async), so any OTHER terminal
    /// stamped mid-run is a verdict nobody earned. Applied at the ONE funnel every
    /// plugin-originated status passes through, plus the cache-replay and
    /// `safeUpdate` crash-net paths.
    let reportUnlessRunOwns (onSuppressed: unit -> unit) (s: PluginStatus) : bool =
        lock statusLock (fun () ->
            if PluginStatus.isTerminal s && anyRunSlotBusy () then
                onSuppressed ()
                false
            else
                services.ReportStatus handler.Name s
                true)

    /// A replayed terminal whose summary is DERIVED from the live ledger (a
    /// per-file cache entry) must build that summary at the same instant the report
    /// decision is made. Unlike `reportUnlessRunOwns`, the ownership gate runs FIRST
    /// and `mkTerminal` is evaluated only on the report path, so the derive and the
    /// report are one atomic step under `statusLock` and summary and verdict cannot
    /// diverge across the read. `mkTerminal` is TERMINAL by construction (the replay
    /// only ever builds `Completed`/`Failed`), so no `isTerminal` re-check is needed.
    let reportDerivedTerminalUnlessRunOwns (onSuppressed: unit -> unit) (mkTerminal: unit -> PluginStatus) : bool =
        lock statusLock (fun () ->
            if anyRunSlotBusy () then
                onSuppressed ()
                false
            else
                services.ReportStatus handler.Name (mkTerminal ())
                true)

    /// Publish `s` WITHOUT the ownership check, still serialised against claims
    /// and guarded reports. The framework's own forced terminal in `runOne` is the
    /// live run's OWN verdict — its slot is still held — so the ownership rule
    /// must not suppress it; but it must not interleave with a concurrent claim or
    /// guarded report either.
    let reportBypassingGuard (s: PluginStatus) =
        lock statusLock (fun () -> services.ReportStatus handler.Name s)

    // Forward reference to the agent so `post` and `runOne` can route completion
    // messages back without an inbox closure. Set immediately after Start returns;
    // any access before then is impossible by construction (no caller can invoke
    // ctx until registerHandler returns the RegisteredPlugin).
    let mutable agentRef: MailboxProcessor<PluginEvent<'Msg> * PluginWorkOwner.WorkId> option =
        None

    let post (msg: 'Msg) =
        match agentRef with
        | Some a ->
            let identity = workOwner.AdmitEvent()
            a.Post(Custom msg, identity)
        | None -> ()

    let reportRunFailure key startedAt stage (ex: exn) =
        let summary = $"RunExclusive '%s{key}' %s{stage}: %s{ex.ToString()}"
        error (PluginName.value handler.Name) summary

        reportBypassingGuard (
            PluginStatus.Failed(
                summary,
                DateTime.UtcNow,
                RunVerdict.create $"RunExclusive '%s{key}' %s{stage}: %s{ex.Message}" (DateTime.UtcNow - startedAt)
            )
        )

    let runOne
        (key: string)
        (identity: PluginWorkOwner.WorkId)
        (sharedRun: (string * ('Msg -> SharedResourceState)) option)
        (startedAt: DateTime)
        (w: Async<'Msg>)
        =
        ProcessRegistry.withChildScopeAsync System.Threading.CancellationToken.None (fun settleChildren ->
            async {
                let mutable completion: Result<'Msg, exn> option = None

                // The work async is plugin-supplied — a third-party-extension
                // boundary that may raise anything. The broad catch keeps the
                // `completion` value assignable: without it the `finally` still runs
                // (resolving the exclusive obligation) but `completion` stays unset and the
                // agent waits forever for a result. Logged as ex.ToString() so the
                // type and stack trace survive for diagnosing the offending plugin.
                try
                    try
                        let! msg =
                            async {
                                try
                                    return! w
                                finally
                                    settleChildren ()
                            }

                        completion <- Some(Result.Ok msg)
                    with ex ->
                        completion <- Some(Result.Error ex)
                        error (PluginName.value handler.Name) $"RunExclusive '%s{key}' work failed: %s{ex.ToString()}"

                        // A faulted exclusive run must never STRAND the plugin in a
                        // non-terminal status. No completion message is posted on this
                        // path (`completion` retains the failure below) and `runExclusive`
                        // reported Running at the claim, so without a forced terminal
                        // the plugin sits Running forever while `IsBusy`/`AnyPluginBusy`
                        // report false — `WaitForComplete` then blocks on a plugin that
                        // will never complete, and idle-exit fires mid-wait. The
                        // framework knows when this run started (it claimed the slot),
                        // so the verdict carries a measured elapsed.
                        reportBypassingGuard (
                            PluginStatus.Failed(
                                $"RunExclusive '%s{key}' work failed: %s{ex.ToString()}",
                                DateTime.UtcNow,
                                RunVerdict.create
                                    $"RunExclusive '%s{key}' work failed: %s{ex.Message}"
                                    (DateTime.UtcNow - startedAt)
                            )
                        )
                finally
                    try
                        try
                            // The exclusive obligation survives classification and handoff.
                            // Only the atomic transfer below frees its slot.
                            sharedRun
                            |> Option.iter (fun (sharedKey, classify) ->
                                let resourceState =
                                    match completion with
                                    | Some(Result.Ok message) ->
                                        try
                                            classify message
                                        with ex ->
                                            error
                                                (PluginName.value handler.Name)
                                                $"RunExclusiveShared '%s{key}' classifier failed: %s{ex.ToString()}"

                                            Invalid
                                                $"%s{PluginName.value handler.Name} shared result classifier faulted"
                                    | Some(Result.Error _)
                                    | None -> Invalid $"%s{PluginName.value handler.Name} shared work faulted"

                                services.ReleaseSharedRun sharedKey resourceState)
                        with ex ->
                            // Cleanup is part of the admitted operation. Its failure
                            // invalidates the result, so no successful fold is posted.
                            completion <- Some(Result.Error ex)
                            reportRunFailure key startedAt "cleanup failed" ex
                    finally
                        match completion with
                        | Some(Result.Ok message) ->
                            match workOwner.CompleteRun identity with
                            | None -> ()
                            | Some eventIdentity ->
                                match agentRef with
                                | Some agent -> agent.Post(Custom message, eventIdentity)
                                | None -> invalidOp "Plugin executor is unavailable after work admission"
                        | Some(Result.Error failure) -> workOwner.FailRun(identity, failure)
                        | None ->
                            workOwner.FailRun(
                                identity,
                                InvalidOperationException("Exclusive work ended without an outcome")
                            )
            })

    let runExclusive (key: string) (work: Async<'Msg>) : RunClaim =
        // The owner admits the exclusive obligation before its UI report.
        let claimedAt =
            lock statusLock (fun () ->
                let claim = workOwner.TryClaim key

                claim
                |> Option.iter (fun (_, startedAt) -> services.ReportStatus handler.Name (Running startedAt))

                claim)

        match claimedAt with
        | Some(identity, startedAt) ->
            try
                startOwned (runOne key identity None startedAt work)
            with ex ->
                try
                    reportRunFailure key startedAt "failed to start" ex
                finally
                    workOwner.FailRun(identity, ex)

            Claimed
        | None ->
            // Exclusion-slot contention: the run is NOT started and the caller must
            // decide (skip or queue). The debug line keeps "why didn't my edit
            // re-run this plugin?" answerable from the log.
            debug
                (PluginName.value handler.Name)
                $"exclusion-slot busy: '%s{key}' run not started (a previous run is still in flight)"

            SlotBusy

    let runExclusiveShared
        (key: string)
        (sharedKey: string)
        (workFor: SharedResourceState -> Async<'Msg>)
        (classify: 'Msg -> SharedResourceState)
        (failureMessage: exn -> 'Msg)
        : SharedRunClaim =
        let claimedAt =
            lock statusLock (fun () ->
                let claim = workOwner.TryClaim key

                claim
                |> Option.iter (fun (_, startedAt) -> services.ReportStatus handler.Name (Running startedAt))

                claim)

        match claimedAt with
        | None -> LocalSlotBusy
        | Some(identity, startedAt) ->
            let start resourceState =
                // Defer the plugin factory invocation into runOne's guarded async
                // boundary. A synchronous exception while constructing the work
                // must release both the local slot and the host-wide lease.
                try
                    let guardedWork =
                        async {
                            try
                                return! workFor resourceState
                            with ex ->
                                return failureMessage ex
                        }

                    startOwned (runOne key identity (Some(sharedKey, classify)) startedAt guardedWork)
                    SharedStarted
                with ex ->
                    SharedStartFailed(fun released ->
                        // The scheduler owns handoff before this acknowledgement.
                        // Failure mapping cannot retire the run ahead of cleanup.
                        try
                            match released with
                            | Result.Error cleanupFailure -> raise cleanupFailure
                            | Ok() -> ()

                            reportRunFailure key startedAt "failed to start" ex
                            let message = failureMessage ex

                            match workOwner.CompleteRun identity with
                            | None -> ()
                            | Some eventIdentity ->
                                match agentRef with
                                | Some agent -> agent.Post(Custom message, eventIdentity)
                                | None -> invalidOp "Plugin executor is unavailable after shared start failure"
                        with failure ->
                            try
                                reportRunFailure key startedAt "startup completion failed" failure
                            finally
                                workOwner.FailRun(identity, failure))

            match services.ClaimOrQueueSharedRun sharedKey start with
            | Some resourceState ->
                match start resourceState with
                | SharedStarted -> ()
                | SharedStartFailed afterRelease ->
                    finishSharedStartFailure
                        (fun () ->
                            services.ReleaseSharedRun
                                sharedKey
                                (Invalid $"%s{PluginName.value handler.Name} shared work failed to start"))
                        afterRelease

                SharedClaimed
            | None -> SharedQueued

    let isRunning (key: string) = workOwner.Snapshot.IsRunning key

    /// The plugin-facing status reporter: drops a terminal stamped while a live
    /// run owns the status (see `reportUnlessRunOwns`), forwards everything else.
    let reportStatusGuarded (s: PluginStatus) =
        reportUnlessRunOwns
            (fun () ->
                debug
                    (PluginName.value handler.Name)
                    "suppressing terminal status — an exclusive run is in flight and owns the status")
            s
        |> ignore

    // Standard ctx — used inside the agent loop (via Update). IPC command
    // handlers get the far narrower CommandCtx instead (see its doc).
    let ctx: PluginCtx<'Msg> =
        { ReportStatus = reportStatusGuarded
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
          CompleteWithTimeout = fun reason -> services.SetNextTerminalOutcome handler.Name (TimedOut reason)
          RunExclusive = runExclusive
          RunExclusiveShared = runExclusiveShared
          IsRunning = isRunning
          FcsSuppressedCodes = services.FcsSuppressedCodes
          ProjectGraph = services.ProjectGraph }

    // The narrow context handed to IPC command handlers — see `CommandCtx`.
    let commandCtx: CommandCtx<'Msg> =
        { RepoRoot = services.RepoRoot
          Log = fun msg -> services.Log handler.Name msg
          Post = post
          IsRunning = isRunning
          ProjectGraph = services.ProjectGraph }

    let agent =
        MailboxProcessor<PluginEvent<'Msg> * PluginWorkOwner.WorkId>
            .Start(
                (fun inbox ->

                    /// Compute the composite key for a given event.
                    ///
                    /// The file half is REPO-RELATIVE, not the absolute path. It ends up
                    /// in the on-disk entry's file NAME, so an absolute path there gave
                    /// two checkouts of one repository entirely different names for
                    /// byte-identical work — the cache could not be shared even when
                    /// every key input agreed. `CachePathIdentity` keeps a path outside
                    /// the repo explicitly machine-local, so nothing is silently made
                    /// portable that is not.
                    let compositeKey (event: PluginEvent<'Msg>) : TaskCache.CompositeKey =
                        let nameStr = PluginName.value handler.Name

                        match event with
                        | FileChecked r ->
                            { Plugin = nameStr
                              File =
                                Some(
                                    CachePathIdentity.ofPath services.RepoRoot (AbsFilePath.value r.File)
                                    |> CachePathIdentity.toKey
                                ) }
                        | _ -> { Plugin = nameStr; File = None }

                    /// Try to replay a cached result. Returns true if cache hit.
                    ///
                    /// `cacheKeyOpt` is the key for this event, computed ONCE by
                    /// the dispatch loop and threaded here so the lookup and the
                    /// later store share the exact same value (computing a
                    /// BuildPlugin key is a full content-hash of the project
                    /// graph — recomputing per call doubles that cost per
                    /// trigger). Threading one value also makes lookup key ≡
                    /// store key by construction.
                    let tryReplayCache (event: PluginEvent<'Msg>) (cacheKeyOpt: ContentHash option) =
                        match services.TaskCache, cacheKeyOpt with
                        | Some cache, Some cacheKey ->
                            let compKey = compositeKey event
                            let pluginName = PluginName.value handler.Name

                            // The typed miss reason is the whole point of `Lookup` over
                            // `TryGet`: with content-addressed keys a cold start is
                            // indistinguishable from a key accidentally salted with
                            // something machine-local unless the miss NAMES the input
                            // that moved.
                            let lookupResult =
                                match cache.Lookup compKey cacheKey with
                                | TaskCache.CacheHit result ->
                                    FsHotWatch.Logging.debug "task-cache" $"plugin=%s{pluginName} hit=true"
                                    Some result
                                | TaskCache.CacheMiss reason ->
                                    FsHotWatch.Logging.debug
                                        "task-cache"
                                        $"plugin=%s{pluginName} hit=false miss=%s{TaskCache.CacheMissReason.describe reason}"

                                    None

                            match lookupResult with
                            | Some result ->
                                // AUTOMATION-343 — clear ONLY what the cached run itself
                                // cleared. A replay must be observationally
                                // indistinguishable from running the handler (the
                                // invariant AUTOMATION-245 stated for the build cache),
                                // and it was not:
                                //
                                // this used to call `ClearPlugin` for every non-FileChecked
                                // event — the plugin's ENTIRE ledger — and then replay only
                                // the errors captured in that one batch. Any finding for a
                                // file OUTSIDE the batch was silently destroyed. A real run
                                // never does that: `reportOrClearFile` touches one file at a
                                // time, so earlier batches' findings stand. A cache HIT
                                // therefore erased findings a cache MISS keeps, and the
                                // verdict gates on the ledger — a false green.
                                //
                                // The blanket is also redundant: a run that genuinely
                                // cleared everything captures a `("*", [])` marker, replayed
                                // by the loop below. Replaying exactly what the run did, and
                                // nothing more, is what makes hit ≡ run true by construction
                                // rather than by coincidence.
                                //
                                // The per-file clear on `FileChecked` stays: it is scoped to
                                // the one file the event is about, which is precisely what
                                // the real handler does for that file.
                                match event with
                                | FileChecked r -> services.ClearErrors handler.Name (AbsFilePath.value r.File)
                                | _ -> ()

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

                                // Mark every replayed verdict as served from cache so
                                // the rendering never passes a replay off as a fresh
                                // run. Idempotent: a re-cached replay doesn't stack
                                // suffixes.
                                let cachedSuffix = " (cached)"

                                let markCached (v: RunVerdict) =
                                    if v.Summary.EndsWith(cachedSuffix, System.StringComparison.Ordinal) then
                                        v
                                    else
                                        RunVerdict.create (v.Summary + cachedSuffix) v.Elapsed

                                // What the replayed verdict may say is bounded by the
                                // entry's scope (AUTOMATION-186):
                                //
                                // • Whole-run entries (`CachedRun*`) store a verdict
                                //   that is a pure function of the key, so the
                                //   ORIGINAL run's summary + true duration replay
                                //   verbatim.
                                // • Per-file entries (`CachedFile*`) carry no summary
                                //   BY CONSTRUCTION. Theirs is derived from the
                                //   plugin's live ledger set AFTER the error replay
                                //   above has landed this entry's findings. The
                                //   derivation runs INSIDE the ownership guard below
                                //   (never here), so the ledger snapshot the summary
                                //   reflects is exactly the one the report lands on.
                                //   Otherwise: "analyzed 1044 files, 5 findings
                                //   (cached)" over an empty ledger and a green
                                //   verdict.
                                let derivedVerdict elapsed =
                                    RunVerdict.create
                                        (ledgerSummary (services.GetPluginDiagnostics handler.Name))
                                        elapsed

                                // Built lazily: `reportDerivedTerminalUnlessRunOwns`
                                // evaluates this only on the report path, so the
                                // per-file ledger read never happens when the replay
                                // is suppressed (an exclusive run owns the status).
                                let mkReplayTerminal () =
                                    match result.Status with
                                    | TaskCache.CachedRunCompleted v -> Completed(nowAt, markCached v)
                                    | TaskCache.CachedRunFailed(err, v) -> Failed(err, nowAt, markCached v)
                                    | TaskCache.CachedFileCompleted elapsed ->
                                        Completed(nowAt, markCached (derivedVerdict elapsed))
                                    | TaskCache.CachedFileFailed(err, elapsed) ->
                                        Failed(err, nowAt, markCached (derivedVerdict elapsed))

                                // A cached TERMINAL status must never claim the plugin is
                                // at rest while it is mid-exclusive-run. On a warm scan
                                // every `FileChecked` is a cache hit, and each hit
                                // re-reporting the cached `Completed` stomps the `Running`
                                // an in-flight test run set: `allPluginsAtRest` then sees
                                // "no plugin Running" and `WaitForComplete` resolves while
                                // the run is still executing (AUTOMATION-95/99).
                                //
                                // The live run owns this plugin's status and reports the
                                // real terminal when it finishes. Errors and emitted events
                                // still replay — only the status claim is suppressed. Same
                                // ownership rule as `reportStatusGuarded`, kept explicit
                                // here for the replay-specific diagnostic.
                                reportDerivedTerminalUnlessRunOwns
                                    (fun () ->
                                        FsHotWatch.Logging.debug
                                            (PluginName.value handler.Name)
                                            "cache replay: suppressing cached terminal status — an exclusive run is in flight")
                                    mkReplayTerminal
                                |> ignore

                                // Replay emitted events. Cached test-lifecycle events carry the
                                // ORIGINAL run's RunId, which would cause RunId-based dedup (e.g.
                                // FileCommand) to skip the replay as if it were the same run. Swap
                                // in a single fresh RunId shared across the three test events so
                                // the cache hit looks like a distinct run.
                                let freshRunId = System.Lazy<System.Guid>(System.Guid.NewGuid)

                                // Live TestRunStarted is emitted before the test host launches,
                                // outside the later TestsFinished cache-write window. New cache
                                // entries therefore carry only progress/completion; synthesize the
                                // matching start on replay. Older entries already contain it.
                                if
                                    result.EmittedEvents
                                    |> List.exists (function
                                        | TaskCache.CachedTestRunCompleted _ -> true
                                        | _ -> false)
                                    && not (
                                        result.EmittedEvents
                                        |> List.exists (function
                                            | TaskCache.CachedTestRunStarted _ -> true
                                            | _ -> false)
                                    )
                                then
                                    let completed =
                                        result.EmittedEvents
                                        |> List.pick (function
                                            | TaskCache.CachedTestRunCompleted value -> Some value
                                            | _ -> None)

                                    services.EmitTestRunStarted
                                        { RunId = freshRunId.Value
                                          StartedAt = DateTime.UtcNow - completed.TotalElapsed }

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

                    /// Force a terminal `Failed` for a fault the plugin could not report
                    /// itself, so a handler that throws out of `Update` cannot leave the
                    /// plugin stuck in whatever transient status it last reported (reports
                    /// Running, hits an error, never reports terminal, UI shows "running"
                    /// forever).
                    ///
                    /// Subject to the ownership rule: while an exclusive run is in flight
                    /// it already published `Running` and its completion path delivers a
                    /// terminal, so a forced status there would stomp a run still
                    /// executing. The crash is logged either way.
                    ///
                    /// `what` names the layer that faulted ("handler", "dispatch"), and
                    /// `startedAt` is when that layer began, so the verdict carries a
                    /// MEASURED elapsed rather than a fabricated zero-length run.
                    let reportForcedFailure (what: string) (startedAt: DateTime) (ex: exn) =
                        error (PluginName.value handler.Name) $"%s{what} failed: %s{ex.ToString()}"

                        reportUnlessRunOwns
                            (fun () ->
                                FsHotWatch.Logging.debug
                                    (PluginName.value handler.Name)
                                    $"%s{what} fault: suppressing forced Failed status — an exclusive run is in flight and owns the status")
                            (Failed(
                                ex.ToString(),
                                DateTime.UtcNow,
                                RunVerdict.create $"%s{what} failed: %s{ex.Message}" (DateTime.UtcNow - startedAt)
                            ))
                        |> ignore

                    let safeUpdate pluginCtx state event =
                        async {
                            try
                                let! candidate = handler.Update pluginCtx state event
                                return Result.Ok candidate
                            with ex ->
                                return Result.Error ex
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

                                // True once this capture window launched a NEW exclusive
                                // run. A handler that reports a terminal and then launches
                                // a run (TestPrune's queued-rerun drain) has NOT produced
                                // a replayable result — the terminal it reported is about
                                // to be superseded by the run it just started, and caching
                                // it would replay a verdict the rerun exists to overturn.
                                let mutable launchedRunInWindow = false

                                let capturingCtx =
                                    { ReportStatus =
                                        fun s ->
                                            // Guard BEFORE capture: a terminal the live run
                                            // suppressed was never observable, so it must
                                            // not become a cached result either. `capturedStatus`
                                            // is set only when the report actually landed, and
                                            // is read after this Update completes on the same
                                            // mailbox thread — so it stays outside the lock.
                                            if
                                                reportUnlessRunOwns
                                                    (fun () ->
                                                        FsHotWatch.Logging.debug
                                                            (PluginName.value handler.Name)
                                                            "suppressing terminal status — an exclusive run is in flight and owns the status")
                                                    s
                                            then
                                                capturedStatus <- Some s
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
                                      CompleteWithTimeout =
                                        fun reason -> services.SetNextTerminalOutcome handler.Name (TimedOut reason)
                                      RunExclusive =
                                        fun key work ->
                                            match runExclusive key work with
                                            | Claimed ->
                                                launchedRunInWindow <- true
                                                Claimed
                                            | SlotBusy -> SlotBusy
                                      RunExclusiveShared =
                                        fun key sharedKey workFor classify failureMessage ->
                                            match runExclusiveShared key sharedKey workFor classify failureMessage with
                                            | SharedClaimed ->
                                                launchedRunInWindow <- true
                                                SharedClaimed
                                            | SharedQueued ->
                                                launchedRunInWindow <- true
                                                SharedQueued
                                            | LocalSlotBusy -> LocalSlotBusy
                                      IsRunning = isRunning
                                      FcsSuppressedCodes = services.FcsSuppressedCodes
                                      ProjectGraph = services.ProjectGraph }

                                let! attempted = safeUpdate capturingCtx state event

                                // Only cache when the status reached a terminal state AND
                                // the handler did not launch a new run in the same window
                                // (see `launchedRunInWindow`).
                                //
                                // The mint site enforces the scope rule: a per-file entry
                                // (`File = Some`) may not store the status summary — a
                                // whole-session claim a per-file key cannot back — nor the
                                // timestamp (replay re-stamps `now`). Only a whole-run
                                // entry keeps its verdict, which IS a pure function of its
                                // key.
                                let compKey = compositeKey event

                                let cachedStatus =
                                    match capturedStatus, compKey.File with
                                    | Some(Completed(_, v)), Some _ -> Some(TaskCache.CachedFileCompleted v.Elapsed)
                                    | Some(Failed(err, _, v)), Some _ ->
                                        Some(TaskCache.CachedFileFailed(err, v.Elapsed))
                                    | Some(Completed(_, v)), None -> Some(TaskCache.CachedRunCompleted v)
                                    | Some(Failed(err, _, v)), None -> Some(TaskCache.CachedRunFailed(err, v))
                                    | (Some(Idle | Running _) | None), _ -> None

                                let cacheWrite =
                                    match attempted, cachedStatus with
                                    | Result.Ok _, Some status when not launchedRunInWindow ->
                                        let result: TaskCache.TaskCacheResult =
                                            { CacheKey = cacheKey
                                              Errors = capturedErrors |> Seq.toList
                                              Status = status
                                              EmittedEvents = capturedEvents |> Seq.toList }

                                        Some(fun () -> cache.Set compKey cacheKey result)
                                    | _ -> None

                                return attempted |> Result.map (fun candidate -> candidate, cacheWrite)
                            | _ ->
                                let! attempted = safeUpdate ctx state event
                                return attempted |> Result.map (fun candidate -> candidate, None)
                        }

                    let rec loop state =
                        async {
                            let! (event, identity) = inbox.Receive()
                            let dispatchStarted = DateTime.UtcNow

                            let! attempted =
                                async {
                                    try
                                        let cacheKeyOpt = handler.CacheKey |> Option.bind (fun key -> key state event)
                                        // Custom messages deliver actual work and cannot replay an older fold.
                                        let replayKeyOpt =
                                            match event with
                                            | Custom _ -> None
                                            | _ -> cacheKeyOpt

                                        if tryReplayCache event replayKeyOpt then
                                            return Result.Ok(state, None, false)
                                        else
                                            let! result = runAndCache event state cacheKeyOpt

                                            return
                                                result |> Result.map (fun (candidate, write) -> candidate, write, true)
                                    with ex ->
                                        return Result.Error ex
                                }

                            let! committed =
                                async {
                                    match attempted with
                                    | Result.Error failure ->
                                        return Result.Error(state, PluginWorkOwner.UpdateFailure failure)
                                    | Result.Ok(candidate, cacheWrite, updated) ->
                                        match handler.PrepareCommit with
                                        | Some prepare when updated ->
                                            try
                                                // External preparation never runs inside the Store writer.
                                                let! prepared = prepare state candidate
                                                workOwner.PublishEventState(identity, candidate)

                                                try
                                                    do! prepared.Finalize
                                                    cacheWrite |> Option.iter (fun write -> write ())
                                                    workOwner.SettleEvent(identity, preparedCommit = true)
                                                    return Result.Ok candidate
                                                with ex ->
                                                    return Result.Error(candidate, PluginWorkOwner.CommitFailure ex)
                                            with ex ->
                                                return Result.Error(state, PluginWorkOwner.CommitFailure ex)
                                        | _ ->
                                            try
                                                workOwner.PublishEventState(identity, candidate)

                                                try
                                                    cacheWrite |> Option.iter (fun write -> write ())
                                                    workOwner.SettleEvent(identity)
                                                    return Result.Ok candidate
                                                with ex ->
                                                    return Result.Error(candidate, PluginWorkOwner.UpdateFailure ex)
                                            with ex ->
                                                return Result.Error(state, PluginWorkOwner.UpdateFailure ex)
                                }

                            // Settle failure once, outside the effect catches. Diagnostic failure
                            // cannot retry retirement of an identity whose receipt already failed.
                            let nextState =
                                match committed with
                                | Result.Ok candidate -> candidate
                                | Result.Error(retained, failure) ->
                                    try
                                        try
                                            reportForcedFailure "Plugin event" dispatchStarted failure.Exception
                                        with reportingFailure ->
                                            error
                                                (PluginName.value handler.Name)
                                                $"Failure reporting also failed: %s{reportingFailure.ToString()}"
                                    finally
                                        // Reporting is part of the original event. Publish
                                        // its failure only after that attempt, exactly once.
                                        workOwner.FailEvent(identity, failure)

                                    retained

                            return! loop nextState
                        }

                    loop handler.Init)
            )

    // Fail accepted event receipts, but preserve exclusive workers until their
    // actual cleanup completes. The same snapshot publishes fault and ownership.
    agent.Error.Add(fun ex ->
        workOwner.FaultExecutor ex

        error
            (PluginName.value handler.Name)
            $"Mailbox loop crashed (programming bug, agent stopped): %s{ex.ToString()}")

    agentRef <- Some agent

    // Register commands
    for (cmdName, cmdHandler) in handler.Commands do
        services.RegisterCommand(
            cmdName,
            fun args ->
                async {
                    match cmdHandler with
                    | PluginCommand.Request request -> return! request commandCtx args
                    | PluginCommand.Observe read ->
                        let snapshot = workOwner.Snapshot

                        let readContext =
                            { PluginCommand.readContext commandCtx with
                                IsRunning = snapshot.IsRunning }

                        return! read readContext snapshot.State args
                }
        )

    // Build type-erased registration with subscription-filtered dispatch
    let post event =
        let identity, completion = workOwner.AdmitTrackedEvent()
        agent.Post(event, identity)
        Some(DispatchReceipt completion)

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
        | _ -> None

    { Name = handler.Name
      Dispatch = fun event -> dispatch event |> ignore
      DispatchTracked = dispatch
      Teardown = handler.Teardown
      // Events and exclusive runs share one identity-bearing work ledger.
      IsBusy = fun () -> workOwner.Snapshot.IsBusy
      CompletedDispatches = fun () -> workOwner.Snapshot.CompletedEvents
      Subscriptions = handler.Subscriptions
      Fault = fun () -> workOwner.Snapshot.Fault }

/// Standalone registration with a private owner publication.
let registerHandler (services: PluginHostServices) (handler: PluginHandler<'State, 'Msg>) : RegisteredPlugin =
    registerHandlerWithOwner (PluginWorkOwner.Store()) services handler

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

    /// Transition status to Completed at the current UTC time, carrying the
    /// verdict (summary + the plugin's own duration measurement). The host
    /// routes the verdict's summary into the run record — there is no separate
    /// summary channel to forget or contradict.
    let completeWith (ctx: PluginCtx<'Msg>) (summary: string) (elapsed: System.TimeSpan) : unit =
        ctx.ReportStatus(PluginStatus.completedNow summary elapsed)

    /// Transition status to Completed at the current UTC time with a verdict that says
    /// the run VERIFIED NOTHING — `detail` is what it did instead. The run record then
    /// carries `RunOutcome.VerifiedNothing`, and every surface refuses it a green.
    let completeVerifyingNothing (ctx: PluginCtx<'Msg>) (detail: string) (elapsed: System.TimeSpan) : unit =
        ctx.ReportStatus(PluginStatus.verifiedNothingNow detail elapsed)

    /// Transition status to Failed at the current UTC time. `error` is the
    /// full diagnosis; the verdict carries the one-line `summary` and the
    /// measured duration — same single channel as `completeWith`.
    let failedWith (ctx: PluginCtx<'Msg>) (error: string) (summary: string) (elapsed: System.TimeSpan) : unit =
        ctx.ReportStatus(PluginStatus.failedNow error summary elapsed)

    /// Report or clear the per-file error scope based on whether any entries exist.
    /// Used by per-file analyzers (Lint, Analyzers, FormatCheck) so that a file
    /// transitions cleanly between "has findings" and "clean" without leaking
    /// stale entries.
    let reportOrClearFile (ctx: PluginCtx<'Msg>) (file: string) (entries: ErrorEntry list) : unit =
        if entries.IsEmpty then
            ctx.ClearErrors file
        else
            ctx.ReportErrors file entries
