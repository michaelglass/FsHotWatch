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

/// Fair host-wide scheduler for resources shared by otherwise independent plugins.
/// Ownership is handed directly to the oldest waiter, so a releasing plugin cannot
/// repeatedly reacquire ahead of already-owed work.
type SharedRunScheduler() =
    let gate = obj ()
    let owners = System.Collections.Generic.HashSet<string>()

    let resourceStates =
        System.Collections.Generic.Dictionary<string, SharedResourceState>()

    let waiters =
        System.Collections.Generic.Dictionary<string, System.Collections.Generic.Queue<SharedResourceState -> bool>>()

    member _.ClaimOrQueue(key: string, start: SharedResourceState -> bool) =
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
                        let created = System.Collections.Generic.Queue<SharedResourceState -> bool>()
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
                        false

                if not started then
                    handOff (Invalid "shared waiter failed to start")

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
        /// Named commands that can be invoked via IPC. Each command receives a
        /// deliberately narrow `CommandCtx` (see its doc — commands observe and
        /// `Post`, they never launch work on the IPC thread), a state snapshot,
        /// and args. `ctx` is typically `_ctx` for commands that don't need it.
        Commands: (string * (CommandCtx<'Msg> -> 'State -> string array -> Async<string>)) list
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
        /// How many dispatched events this plugin has FINISHED handling, ever.
        /// Monotonic, incremented in the same `finally` that releases the
        /// in-flight count.
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
        /// The fault that killed this plugin's message loop, if one did.
        ///
        /// A dead agent is otherwise INDISTINGUISHABLE from a busy one: the
        /// in-flight count is incremented when an event is posted and only
        /// decremented by the loop, so once the loop stops the count can only
        /// rise and `WaitForComplete` waits forever. One integer cannot say "0 and
        /// alive" apart from "n and dead", so the fault is published separately.
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
        ClaimOrQueueSharedRun: string -> (SharedResourceState -> bool) -> SharedResourceState option
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
let registerHandler (services: PluginHostServices) (handler: PluginHandler<'State, 'Msg>) : RegisteredPlugin =

    // Per-handler run-slot state for ctx.RunExclusive. Keyed by the user-supplied
    // string. `true` means a call is in flight; absent or `false` means idle.
    // While running, additional calls under the same key are dropped. Mutated
    // only inside `runSlotsLock`.
    let runSlots = System.Collections.Generic.Dictionary<string, bool>()

    let runSlotsLock = obj ()

    /// True when this plugin holds ANY exclusive run slot — i.e. real work
    /// (a test run, a build) is executing in the background right now, even
    /// though the mailbox is idle and the handler that launched it has already
    /// returned. Keyless because the framework does not know a plugin's slot
    /// names — any busy slot means "not at rest".
    let anyRunSlotBusy () =
        lock runSlotsLock (fun () -> runSlots.Values |> Seq.exists id)

    /// Serialises "decide whether a live run owns the status" + "publish it"
    /// against "claim a run slot" + "publish the `Running` that claim earns", so the
    /// ownership DECISION and the REPORT are ONE critical section. Without it the
    /// guard is a check-then-act, and a claim landing between the read and the
    /// report lets a stale terminal land ON TOP of the live run (the "✓ while tests
    /// are still running" signature: `started:` with no `elapsed:`). `PluginCtx` is
    /// a record of closures, so a plugin may legally claim a slot from a `work`
    /// async or a spawned task and reach this concurrently.
    ///
    /// Lock ordering: `statusLock` is ALWAYS acquired before `runSlotsLock` and
    /// NEVER from inside it; `runSlotsLock` is otherwise only ever taken alone
    /// (`isRunning`, `anyRunSlotBusy`, `runOne`'s release), so no cycle exists.
    /// Nothing reachable from `services.ReportStatus` re-enters this framework —
    /// `PluginHost.setStatus` is a dictionary write plus a NON-BLOCKING
    /// `MailboxProcessor.Post` — and `runSlotsLock` is released before the report at
    /// both call sites below, so a host callback reading `IsRunning` cannot
    /// self-deadlock.
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
    let mutable agentRef: MailboxProcessor<Choice<PluginEvent<'Msg>, AsyncReplyChannel<'State>>> option =
        None

    // Per-plugin inflight counter — the SINGLE source of "this plugin has work
    // in flight". Incremented (a) every time a Choice1Of2 event is posted to
    // the agent's mailbox, decremented after the agent has finished handling
    // that event; and (b) for the whole lifetime of an exclusive run: from the
    // moment `runExclusive` claims the slot until AFTER the run's completion
    // message has been posted back (see `runOne`'s finally). `WaitForComplete`
    // consults this via `RegisteredPlugin.IsBusy`.
    //
    // ONE counter on purpose (AUTOMATION-99): a composite of two atomics
    // (`inflightCount > 0 || anyRunSlotBusy()`) is read at two instants, and the
    // hand-off between them has a gap where a reader sees "slot free" AND "mailbox
    // empty" while the run's verdict is still in flight. Holding the work token
    // until after the completion post means the counter never dips to zero between
    // "run claimed" and "completion handled".
    let inflightCount = ref 0

    // Monotonic count of dispatched events this plugin has finished handling.
    // Read by the wait's stall detector to tell "draining a backlog" from
    // "stopped": see `RegisteredPlugin.CompletedDispatches`.
    let completedDispatches = ref 0L

    // Set once if the message loop dies. See `RegisteredPlugin.Fault`.
    let mutable agentFault: exn option = None

    let post (msg: 'Msg) =
        match agentRef with
        | Some a ->
            System.Threading.Interlocked.Increment(&inflightCount.contents) |> ignore
            a.Post(Choice1Of2(Custom msg))
        | None -> ()

    let runOne
        (key: string)
        (sharedRun: (string * ('Msg -> SharedResourceState)) option)
        (startedAt: DateTime)
        (w: Async<'Msg>)
        =
        async {
            let mutable completion: 'Msg voption = ValueNone

            // The work async is plugin-supplied — a third-party-extension
            // boundary that may raise anything. The broad catch keeps the
            // `completion` value assignable: without it the `finally` still runs
            // (releasing the runSlots entry) but `completion` stays unset and the
            // agent waits forever for a result. Logged as ex.ToString() so the
            // type and stack trace survive for diagnosing the offending plugin.
            try
                try
                    let! msg = w
                    completion <- ValueSome msg
                with ex ->
                    error (PluginName.value handler.Name) $"RunExclusive '%s{key}' work failed: %s{ex.ToString()}"

                    // A faulted exclusive run must never STRAND the plugin in a
                    // non-terminal status. No completion message is posted on this
                    // path (`completion` stays ValueNone below) and `runExclusive`
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
                // Release order matters:
                //   1. free the slot — the completion handler may itself launch
                //      the next run (`PendingRerun`), so the slot must be free
                //      by the time the completion message is PROCESSED;
                //   2. post the completion message — increments the mailbox leg
                //      of `inflightCount`;
                //   3. only then drop the work token taken by `runExclusive`.
                // The counter stays positive across the whole hand-off, so no
                // observer can catch the plugin "at rest" between the run
                // finishing and its verdict being handled. On the faulted path
                // (no completion) the forced `Failed` above was reported while
                // the token was still held, so the status is terminal before the
                // plugin ever reads as not-busy.
                lock runSlotsLock (fun () -> runSlots.[key] <- false)

                sharedRun
                |> Option.iter (fun (sharedKey, classify) ->
                    let resourceState =
                        match completion with
                        | ValueSome message ->
                            try
                                classify message
                            with ex ->
                                error
                                    (PluginName.value handler.Name)
                                    $"RunExclusiveShared '%s{key}' classifier failed: %s{ex.ToString()}"

                                Invalid $"%s{PluginName.value handler.Name} shared result classifier faulted"
                        | ValueNone -> Invalid $"%s{PluginName.value handler.Name} shared work faulted"

                    services.ReleaseSharedRun sharedKey resourceState)

                try
                    match completion with
                    | ValueSome m -> post m
                    | ValueNone -> ()
                finally
                    System.Threading.Interlocked.Decrement(&inflightCount.contents) |> ignore
        }

    let runExclusive (key: string) (work: Async<'Msg>) : RunClaim =
        // The claim and the `Running` it publishes are ONE critical section under
        // `statusLock`, so no terminal can slip between "no run is live" and "a run
        // is live" and land on top of the run. `runSlotsLock` is released before the
        // report; `Async.Start` happens after the lock so no plugin work ever runs
        // under it.
        let claimedAt =
            lock statusLock (fun () ->
                let shouldStart =
                    lock runSlotsLock (fun () ->
                        match runSlots.TryGetValue(key) with
                        | true, true -> false
                        | _ ->
                            runSlots.[key] <- true
                            true)

                if shouldStart then
                    // The framework — not the plugin — reports Running at the claim
                    // instant, so a launched run is never invisible. A plugin that
                    // reports it itself can miss: CoveragePlugin did, which rendered
                    // ✓ while it ran and starved `bumpGenerationIfStarting`, so the
                    // host's generation-based terminal wait could never be satisfied
                    // while coverage was registered.
                    let startedAt = DateTime.UtcNow
                    services.ReportStatus handler.Name (Running(since = startedAt))

                    // Work token: counts this exclusive run in `inflightCount` from
                    // claim until after its completion message is posted (released in
                    // `runOne`'s finally). See the counter's doc comment.
                    System.Threading.Interlocked.Increment(&inflightCount.contents) |> ignore
                    ValueSome startedAt
                else
                    ValueNone)

        match claimedAt with
        | ValueSome startedAt ->
            Async.Start(runOne key None startedAt work)
            Claimed
        | ValueNone ->
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
                let localClaimed =
                    lock runSlotsLock (fun () ->
                        match runSlots.TryGetValue(key) with
                        | true, true -> false
                        | _ ->
                            runSlots.[key] <- true
                            true)

                if localClaimed then
                    let startedAt = DateTime.UtcNow
                    services.ReportStatus handler.Name (Running(since = startedAt))
                    System.Threading.Interlocked.Increment(&inflightCount.contents) |> ignore
                    ValueSome startedAt
                else
                    ValueNone)

        match claimedAt with
        | ValueNone -> LocalSlotBusy
        | ValueSome startedAt ->
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

                    services.StartAsync(runOne key (Some(sharedKey, classify)) startedAt guardedWork)
                    true
                with ex ->
                    lock runSlotsLock (fun () -> runSlots.[key] <- false)
                    System.Threading.Interlocked.Decrement(&inflightCount.contents) |> ignore

                    error
                        (PluginName.value handler.Name)
                        $"RunExclusiveShared '%s{key}' failed to start: %s{ex.ToString()}"

                    reportBypassingGuard (
                        PluginStatus.Failed(
                            $"RunExclusiveShared '%s{key}' failed to start: %s{ex.ToString()}",
                            DateTime.UtcNow,
                            RunVerdict.create
                                $"RunExclusiveShared '%s{key}' failed to start: %s{ex.Message}"
                                (DateTime.UtcNow - startedAt)
                        )
                    )

                    post (failureMessage ex)

                    false

            match services.ClaimOrQueueSharedRun sharedKey start with
            | Some resourceState ->
                if not (start resourceState) then
                    services.ReleaseSharedRun
                        sharedKey
                        (Invalid $"%s{PluginName.value handler.Name} shared work failed to start")

                SharedClaimed
            | None -> SharedQueued

    let isRunning (key: string) =
        lock runSlotsLock (fun () ->
            match runSlots.TryGetValue(key) with
            | true, running -> running
            | _ -> false)

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
        MailboxProcessor<Choice<PluginEvent<'Msg>, AsyncReplyChannel<'State>>>
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
                            let handlerStarted = DateTime.UtcNow

                            try
                                return! handler.Update pluginCtx state event
                            with ex ->
                                reportForcedFailure "Plugin handler" handlerStarted ex
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

                                let! nextState = safeUpdate capturingCtx state event

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

                                match cachedStatus with
                                | Some status when not launchedRunInWindow ->
                                    let result: TaskCache.TaskCacheResult =
                                        { CacheKey = cacheKey
                                          Errors = capturedErrors |> Seq.toList
                                          Status = status
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
                                // EVERYTHING this event does must run inside the
                                // decrement's `finally` and under a `with`.
                                //
                                // A throw outside the `finally` leaks the increment
                                // `post` already took. A throw that escapes `loop`
                                // STOPS the MailboxProcessor — silently, so every
                                // later post increments into a mailbox nobody is
                                // reading. `IsBusy` is `inflightCount > 0`, so
                                // either leaves a dead agent indistinguishable from
                                // a busy one, permanently: both satisfaction paths
                                // in `waitForAllTerminalCore` require
                                // `not (AnyPluginBusy())`, so `check`/`confirm`
                                // can never resolve. The throwing arms are ordinary
                                // code — TestPrune's `dependsOnHash` hashes every
                                // file matched by the `dependsOn` globs, and the
                                // per-file arm calls `fcsCheckSignature` over raw
                                // FCS results: I/O and third-party data shapes, on
                                // the dispatch thread.
                                //
                                // So a fault here is ACCOUNTED FOR (the finally
                                // still decrements), VISIBLE (forced Failed, same
                                // ownership rule as `safeUpdate`), and SURVIVABLE
                                // (the loop continues, so the plugin keeps serving
                                // later events).
                                let dispatchStarted = DateTime.UtcNow

                                let! nextState =
                                    async {
                                        try
                                            try
                                                // Computed ONCE per dispatched event — see `tryReplayCache`.
                                                let cacheKeyOpt =
                                                    match handler.CacheKey with
                                                    | Some cacheKeyFn -> cacheKeyFn event
                                                    | None -> None

                                                // A `Custom` message is a cache WRITER, never a cache READER.
                                                //
                                                // Every other event is an OBSERVATION whose payload is what the
                                                // key is computed FROM, so same key ⇒ same input ⇒ the cached
                                                // result IS the result. A `Custom` message is the plugin's own
                                                // post — the delivery of work already done — and its payload is
                                                // NOT in the key: TestPrune's `cacheKeyFor` reads the
                                                // `TestRunCompleted` it carries only far enough to decide whether
                                                // the result is CACHEABLE, never far enough to IDENTIFY it, so two
                                                // different runs collide on one key. A hit here is a collision,
                                                // and serving it skips the handler — the only thing that folds the
                                                // finished run into the plugin's state.
                                                //
                                                // The WRITE below keeps the real key: a Custom window is how the
                                                // entry the next `BuildCompleted` hits gets minted at all.
                                                let replayKeyOpt =
                                                    match event with
                                                    | Custom _ -> None
                                                    | _ -> cacheKeyOpt

                                                if tryReplayCache event replayKeyOpt then
                                                    return state
                                                else
                                                    return! runAndCache event state cacheKeyOpt
                                            with ex ->
                                                // Not `safeUpdate`'s net: that one wraps
                                                // `handler.Update` alone, while this catches the
                                                // dispatch machinery AROUND it — the cache-key
                                                // thunks and the replay lookup.
                                                reportForcedFailure
                                                    "Dispatch (cache key or cache replay)"
                                                    dispatchStarted
                                                    ex

                                                return state
                                        finally
                                            // Decrement only after the handler
                                            // (or cache replay) finishes — until
                                            // then `IsBusy` must report true so
                                            // WaitForComplete doesn't return
                                            // before this event has actually been
                                            // processed.
                                            System.Threading.Interlocked.Decrement(&inflightCount.contents) |> ignore

                                            // Paired with the decrement on purpose: one
                                            // event finished is exactly one unit of
                                            // progress, so a plugin that is still
                                            // draining can never look stalled.
                                            System.Threading.Interlocked.Increment(&completedDispatches.contents)
                                            |> ignore
                                    }

                                return! loop nextState
                        }

                    loop handler.Init)
            )

    // Last resort, matching `ErrorLedger` and the scan-signal agent. The loop
    // body handles its own faults and keeps going, so this should never fire;
    // if it ever does the agent has STOPPED and `inflightCount` can only rise
    // from then on.
    //
    // RECORDING the exception is the point, not just logging it: a waiter cannot
    // otherwise tell a dead agent from a busy one, and can only infer it from a
    // plugin that never speaks again.
    agent.Error.Add(fun ex ->
        agentFault <- Some ex

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
                    let! state = agent.PostAndAsyncReply(Choice2Of2)
                    return! cmdHandler commandCtx state args
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
      // "Busy" means "this plugin has work in flight": events queued or being
      // handled, AND any exclusive run from its claim until its completion
      // message has been handled — all counted in the ONE `inflightCount` (see
      // its comment for why a single counter, not counter-plus-slots). Without
      // the run leg the host could conclude a plugin was at rest while its test
      // run was still executing, and `WaitForComplete` would hand `check` a
      // verdict the run had not yet produced. Run tokens are released in a
      // `finally`, and the verdict deadline (Ipc.resolveVerdictDeadline) still
      // bounds a genuinely wedged run.
      IsBusy = fun () -> System.Threading.Volatile.Read(&inflightCount.contents) > 0
      CompletedDispatches = fun () -> System.Threading.Volatile.Read(&completedDispatches.contents)
      Subscriptions = handler.Subscriptions
      Fault = fun () -> agentFault }

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
