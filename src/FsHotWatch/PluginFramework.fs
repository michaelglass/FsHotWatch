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
/// caller forever and lets `test-rerun` exit 0 having run nothing.
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

/// What holds an exclusive key (`PluginCtx.SlotHolder`). A claim on a key held by
/// either `LiveRun` or `Fold` returns `SlotBusy`.
[<RequireQualifiedAccess>]
type SlotHolder =
    /// Nothing holds the key.
    | Free
    /// A live worker runs under the key.
    | LiveRun
    /// No worker is live, but a fold holds the key: a finished run's result or a
    /// delivered intent, whose `Update` has not committed.
    | Fold

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

/// What a shared starter reports. A starter that failed keeps its local claim until the
/// scheduler has handed the resource on; `afterRelease` is then told whether that
/// handoff itself failed.
[<NoComparison; NoEquality>]
type SharedRunStart =
    | SharedStarted
    | SharedStartFailed of afterRelease: (Result<unit, exn> -> unit)

let private finishSharedStartFailure (release: unit -> unit) (afterRelease: Result<unit, exn> -> unit) =
    let outcome =
        try
            release ()
            Result.Ok()
        with failure ->
            Result.Error failure

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
                        SharedStartFailed ignore

                match started with
                | SharedStarted -> ()
                | SharedStartFailed afterRelease ->
                    finishSharedStartFailure (fun () -> handOff (Invalid "shared waiter failed to start")) afterRelease

        handOff resourceState

/// Declarations about exclusive work handed to `RunExclusive`/`RunExclusiveShared`.
module PluginWork =
    let private safe = System.Runtime.CompilerServices.ConditionalWeakTable<obj, obj>()

    /// Declare `work` cooperative-safe: cancelling it part-way leaves nothing behind that
    /// anyone can observe. Its processes run in the run's own child scope (reaped on
    /// cancellation), it publishes only through the result message it returns (which a
    /// cancelled run never folds), and any lifecycle it opens it also closes on
    /// cancellation. Only such work is cancelled when every consumer that asked for it
    /// has gone; undeclared work always runs to completion.
    let cooperativeSafe (work: Async<'Msg>) : Async<'Msg> =
        safe.AddOrUpdate(work, null)
        work

    /// Whether `work` was declared with `cooperativeSafe`.
    let isCooperativeSafe (work: Async<'Msg>) : bool =
        let mutable marker = null
        safe.TryGetValue(work, &marker)

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
        /// Queue a message behind whatever holds the exclusive key. It is delivered once
        /// the key is free, and holds the key until its `Update` commits, so it can claim
        /// the next run under that key. `Some coalescingKey` replaces the payload of a
        /// queued message with the same key, keeping its place. The task completes once
        /// this exact message's state is committed, and fails if it is not.
        EnqueueExclusiveIntent: string -> string option -> 'Msg -> System.Threading.Tasks.Task<unit>
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
        /// What holds `key` under `RunExclusive`. Outside the fold that holds the
        /// key, `SlotHolder.Free` is the only case a claim can succeed in: a plugin
        /// that would do expensive work only to launch it can ask first. It must
        /// still handle `SlotBusy`, since the key may be taken between the read and
        /// the claim.
        SlotHolder: string -> SlotHolder
        /// Caller-configured FCS warning codes the host has been told to
        /// treat as noise. Plugins must merge this with per-file `#nowarn`
        /// directives (`FcsDiagnosticFilter.allSuppressedCodes`) before any
        /// gate or report decision so the user-visible error stream and any
        /// cache-poisoning gates agree on what counts as an error.
        FcsSuppressedCodes: Set<int>
        /// Declare that this fold is entering ONE long unit of work that will finish no
        /// plugin event while it runs — a first-run impact attribution over a cold
        /// database — and bound it. Dispose the handle to end the declaration.
        ///
        /// The stall detector reads owned work, and a slow fold and a stuck one look
        /// identical to it: work owned, nothing `Running`, the host's completed-event
        /// counter still. Only the plugin knows which it is, so it says so and pays for
        /// saying it with a deadline. Inside the deadline the work counts as progress;
        /// past it the declaration's own failure is recorded and the detector names the
        /// plugin exactly as it names an undeclared stall.
        ///
        /// Declaring nothing is the safe default: an undeclared fold is still caught at
        /// the detector's own threshold. Never wrap a fold whose duration you cannot
        /// bound — that is the shape the detector exists to catch.
        DeclareBoundedWork: string -> System.TimeSpan -> System.IDisposable
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
        /// The project model of the host's current publication. A result stamped with
        /// a different `ModelGeneration` belongs to a model this one replaced.
        ObserveModel: unit -> FsHotWatch.ProjectModel.Observation
        /// The available model's checkable files, with the generation they belong to.
        /// `None` whenever no model is available: membership never outlives its model.
        /// This is what an analysis-only completion must account for, so a file with no
        /// completed analysis is a refusal rather than an absence.
        ObserveCheckableFiles: unit -> (int64 * Set<FsHotWatch.Events.AbsFilePath>) option
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

/// The subtask a plugin shows from the moment a finished exclusive run's result is
/// handed to its mailbox until the plugin picks that result up to fold it. While it is
/// live, the plugin is still `Running` — the result is not committed — but nothing is
/// executing any more: the result waits only for the plugin's own folds of the events
/// admitted ahead of it. A wait line renders subtasks by key, so a client blocked on
/// the plugin reads `test-prune (17m) [tests result queued 16m]` instead of a bare
/// elapsed time, and the wedge monitor reads the same subtask to name the mailbox
/// rather than the run.
module QueuedResult =
    [<Literal>]
    let private Suffix = " result queued"

    /// The subtask key for a result of the exclusive run under `exclusiveKey`.
    let subtaskKey (exclusiveKey: string) = exclusiveKey + Suffix

    /// Whether `key` is a queued-result subtask key.
    let isSubtaskKey (key: string) =
        key.EndsWith(Suffix, StringComparison.Ordinal)

    /// The subtask's label: what a status reader should understand from it.
    let label (exclusiveKey: string) =
        $"the '%s{exclusiveKey}' run has finished; its result is queued behind the plugin's own events"

/// A fold that took longer than this is logged when it commits, with the event it
/// folded and the backlog that waited behind it. Always on: a plugin whose folds are
/// minutes long is the one whose queued result nobody can see from the outside.
let SlowFoldThreshold = TimeSpan.FromSeconds 30.0

/// The name of an event as a slow-fold line reports it: its union case's name, so a
/// new case is named without a change here.
let eventKind (event: PluginEvent<'Msg>) : string =
    (fst (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(event, typeof<PluginEvent<'Msg>>))).Name

/// The slow-fold line: what was folded, how long it took, and what waited behind it —
/// the queued events, and the finished runs' results among them.
let slowFoldLine (kind: string) (elapsed: TimeSpan) (queuedBehind: int) (resultsQueued: string list) : string =
    let took =
        if elapsed.TotalMinutes >= 1.0 then
            $"%d{int elapsed.TotalMinutes}m %d{elapsed.Seconds}s"
        else
            $"%d{int elapsed.TotalSeconds}s"

    let behind =
        match queuedBehind, resultsQueued with
        | 0, [] -> "nothing queued behind it"
        | n, [] -> $"%d{n} event(s) queued behind it"
        | n, results ->
            let named = String.concat ", " results
            $"%d{n} event(s) queued behind it, among them %s{named}"

    $"%s{kind} fold took %s{took}; %s{behind}"

module BoundedWork =
    /// The declaration made by a context with no host behind it — test fixtures, and any
    /// embedder wiring a bare `PluginCtx`. Declaring nothing is the SAFE default, never a
    /// loophole: an undeclared fold is still caught by the stall detector at its own
    /// threshold, so a fixture that forgets to supervise loses no detection.
    let undeclared: string -> System.TimeSpan -> System.IDisposable =
        fun _ _ ->
            { new System.IDisposable with
                member _.Dispose() = () }

module ProjectGraphAccessor =
    /// No-op accessor: no graph wired (tests, null-checker daemon). Every query
    /// returns empty/None, so dependency-fanout consumers fall back cleanly.
    let none: ProjectGraphAccessor =
        { ObserveModel = fun () -> FsHotWatch.ProjectModel.Observation.Unobserved
          ObserveCheckableFiles = fun () -> None
          GetAllProjects = fun () -> []
          GetTransitiveDependentProjects = fun _ -> []
          GetProjectReferences = fun _ -> []
          GetCanonicalDllPath = fun _ -> None }

/// The DELIBERATELY narrow context handed to IPC command handlers
/// (`PluginHandler.Commands`). Commands run on the IPC thread, outside the
/// plugin's mailbox and outside its inflight accounting — so work started there
/// would be invisible to `IsRunning`/`AnyPluginBusy`/the status model.
/// Hence no `ReportStatus`, no `RunExclusive`, no `Emit*`:
/// posting is the ONLY way a command can cause work, and the work then runs on
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
        /// Queue a message behind an exclusive key and await its committed state. See
        /// `PluginCtx.EnqueueExclusiveIntent`.
        EnqueueExclusiveIntent: string -> string option -> 'Msg -> System.Threading.Tasks.Task<unit>
        /// Whether `key` is currently running under `RunExclusive`.
        IsRunning: string -> bool
        /// Read-only project-graph accessor.
        ProjectGraph: ProjectGraphAccessor
    }

/// The context an observing command receives. It has no route for posting work: an
/// observation reads committed state and nothing else.
[<NoComparison; NoEquality>]
type CommandReadCtx =
    {
        /// The repository root directory.
        RepoRoot: string
        /// Append an activity log line. Also routes to Logging.info.
        Log: string -> unit
        /// Whether `key` is currently running under `RunExclusive`.
        IsRunning: string -> bool
        /// Read-only project-graph accessor.
        ProjectGraph: ProjectGraphAccessor
    }

/// A named IPC command. The two cases are two different contracts.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type PluginCommand<'State, 'Msg> =
    /// Read committed state. An observation cannot post work, so it never has to wait
    /// behind work that is running.
    | Observe of (CommandReadCtx -> 'State -> string array -> Async<string>)
    /// Ask the plugin to do something. A request never sees state: any choice that
    /// depends on state belongs to the plugin's own `Update`, which folds the message
    /// against the state it actually commits.
    | Request of (CommandCtx<'Msg> -> string array -> Async<string>)

module PluginCommand =
    /// The observing half of a command context.
    let readContext (ctx: CommandCtx<'Msg>) : CommandReadCtx =
        { RepoRoot = ctx.RepoRoot
          Log = ctx.Log
          IsRunning = ctx.IsRunning
          ProjectGraph = ctx.ProjectGraph }

    /// Run a command against an explicit state. Plugin tests use this to drive a
    /// command without a host.
    let invoke (command: PluginCommand<'State, 'Msg>) (ctx: CommandCtx<'Msg>) (state: 'State) (args: string array) =
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

/// What a plugin's `PrepareCommit` hands back once its durable preparation succeeded.
/// `Finalize` runs after the candidate state is published and before the event is
/// acknowledged.
[<NoComparison; NoEquality>]
type PreparedCommit = { Finalize: Async<unit> }

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
        /// Optional durable preparation for a successful update, given the committed
        /// state and the candidate `Update` returned. Nothing is published if it fails.
        /// A failing `Finalize` leaves the candidate published and the event failed.
        PrepareCommit: ('State -> 'State -> Async<PreparedCommit>) option
        /// Named commands that can be invoked via IPC. An `Observe` reads committed
        /// state; a `Request` posts work and never sees state (see `PluginCommand`).
        Commands: (string * PluginCommand<'State, 'Msg>) list
        /// Which events the plugin subscribes to.
        Subscriptions: PluginSubscriptions
        /// Optional cache key function, given the committed state `Update` will receive.
        /// `Some hash` → look up the cache and replay on hit.
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

/// The completion witness for one dispatched event: it completes once that event's
/// state is committed, and fails with the event's own failure. No other event's
/// progress can satisfy it.
[<NoComparison; NoEquality>]
type DispatchReceipt =
    private
    | DispatchReceipt of System.Threading.Tasks.Task<unit>

    /// Wait at most `timeout` for the event's commit.
    member this.Wait(timeout: TimeSpan) =
        let (DispatchReceipt completion) = this
        completion.WaitAsync(timeout)

/// Type-erased plugin registration stored by PluginHost.
[<NoComparison; NoEquality>]
type RegisteredPlugin =
    {
        /// The display name of this plugin.
        Name: PluginName
        /// Dispatch an event to this plugin. Filtering by subscription is built in. Never
        /// throws: a plugin whose executor has stopped logs and drops the event.
        Dispatch: PluginDispatchEvent -> unit
        /// Dispatch and return the event's receipt; `None` when the plugin does not
        /// subscribe to the event. A plugin whose executor has failed refuses the event
        /// by raising the failure that stopped it.
        DispatchTracked: PluginDispatchEvent -> DispatchReceipt option
        /// Optional teardown function for releasing resources.
        Teardown: (unit -> unit) option
        /// True while this plugin owns work: an admitted event not yet committed, a
        /// queued command, or an exclusive run from its claim until its result is
        /// committed. Read from the plugin's owner snapshot.
        IsBusy: unit -> bool
        /// How many events this plugin has committed, ever. Monotonic.
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
        /// The failure this plugin's owner currently records: a failed update, commit
        /// or exclusive run, or the fault that stopped its executor. It is kept after
        /// the work that failed has retired, until later work disproves it.
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
        /// cache entry carries no summary of its own.
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

/// Register a declarative plugin handler against an existing owner, returning a
/// type-erased RegisteredPlugin.
///
/// The owner is the only record of this plugin's work and state. Events are admitted
/// to it before they are posted to the executor, exclusive runs are claimed from it,
/// and an event's state is published through it before its receipt settles. Readers,
/// commands included, take the owner's published snapshot.
let internal registerHandlerForOwner
    (owner: PluginWorkOwner.Owner<'State>)
    (services: PluginHostServices)
    (handler: PluginHandler<'State, 'Msg>)
    : RegisteredPlugin =

    let pluginName = PluginName.value handler.Name

    // Dispatch may arrive from a short-lived scan or batch scope, or from whoever holds
    // the host. The plugin's work belongs to this registered plugin, not to that trigger,
    // so it runs in the context the plugin was registered in: its process scope, its log
    // sink. That covers both what the mailbox loop runs inline (a posted message resumes
    // the loop in the poster's context) and an exclusive worker, which can also outlive
    // the trigger. `Capture` is null when flow was suppressed at registration; the work
    // then runs in the context it was started from.
    let ownerContext = System.Threading.ExecutionContext.Capture()

    let inOwnerContext (start: unit -> unit) =
        if isNull ownerContext then
            start ()
        else
            System.Threading.ExecutionContext.Run(
                ownerContext.CreateCopy(),
                System.Threading.ContextCallback(fun _ -> start ()),
                null
            )

    let startOwned (work: Async<unit>) =
        inOwnerContext (fun () -> services.StartAsync work)

    // Orders status reports against the claims that publish `Running`. Whether a live
    // run owns the status is read from the owner snapshot; the lock only makes that
    // decision and the report it leads to one step, so a terminal decided before a claim
    // cannot land after the claim's `Running`. `services.ReportStatus` never calls back
    // into this plugin, and no owner transition waits on this lock.
    let statusLock = obj ()

    // Per-file results this registration produced by running `Update` in the current run,
    // and per-file results it served from cache instead. A per-file replay's summary is
    // derived from the ledger, which reads the same whether every file was examined or
    // every file was replayed; the tally is what tells those apart. Both move only on the
    // plugin's own loop, one event at a time.
    let mutable perFileExamined = 0
    let mutable perFileReplayed = 0

    // A run is the cohort of per-file events one `BatchChecked` closes. `runEpoch` counts
    // the closes, advanced where events are dispatched whether or not this plugin
    // subscribes to `BatchChecked`. Each admitted `FileChecked` is stamped with the epoch
    // it was dispatched in, and the loop starts a fresh tally at the first event of a
    // newer epoch. Stamping at dispatch keeps a close from resetting the tally while an
    // earlier run's events are still queued behind it.
    let mutable runEpoch = 0L
    let mutable tallyEpoch = 0L

    let dispatchedInRun =
        System.Collections.Concurrent.ConcurrentDictionary<PluginWorkOwner.WorkId, int64>()

    let beginRunOf (identity: PluginWorkOwner.WorkId) =
        match dispatchedInRun.TryRemove identity with
        | true, epoch when epoch > tallyEpoch ->
            tallyEpoch <- epoch
            System.Threading.Volatile.Write(&perFileExamined, 0)
            System.Threading.Volatile.Write(&perFileReplayed, 0)
        | _ -> ()

    // The last status this plugin reported to the host, written under `statusLock`. A
    // claim remembers the one its `Running` displaced, so a run cancelled because nobody
    // needs it can put that back: it produced no verdict of its own.
    let mutable lastReported = Idle

    let reportToHost (status: PluginStatus) =
        services.ReportStatus handler.Name status
        lastReported <- status

    /// The one funnel every status a plugin reports, a cache replay reports, or a fault
    /// forces passes through. A terminal is dropped while an exclusive run owes its
    /// verdict: from its claim until its result fold commits, the run owns the status and
    /// reports its own terminal. A finished worker whose result fold is still queued owes
    /// it too, so an unrelated fold that happens to run first cannot replace the run's
    /// `Running`. `reporter` is the event reporting: a result fold's own report is the
    /// verdict. `status` is built only when it is reported, so a summary derived from the
    /// ledger is read at the instant it lands. Returns whether the status was reported.
    let reportStatus
        (reporter: PluginWorkOwner.WorkId option)
        (isTerminal: bool)
        (status: unit -> PluginStatus)
        (source: string)
        : bool =
        lock statusLock (fun () ->
            if isTerminal && owner.Snapshot.OwesRunVerdict reporter then
                debug
                    pluginName
                    $"%s{source}: terminal status dropped — an exclusive run owes its verdict and owns the status"

                false
            else
                reportToHost (status ())
                true)

    let reportPluginStatus (reporter: PluginWorkOwner.WorkId option) (status: PluginStatus) =
        reportStatus reporter (PluginStatus.isTerminal status) (fun () -> status) "status report"
        |> ignore

    /// A run's own failure. Its key is still held, so the funnel would drop it; it is the
    /// verdict the run owes, reported before the run retires.
    let reportRunFailure (key: string) (startedAt: DateTime) (stage: string) (failure: exn) =
        try
            error pluginName $"RunExclusive '%s{key}' %s{stage}: %s{failure.ToString()}"

            lock statusLock (fun () ->
                reportToHost (
                    PluginStatus.Failed(
                        $"RunExclusive '%s{key}' %s{stage}: %s{failure.ToString()}",
                        DateTime.UtcNow,
                        RunVerdict.create
                            $"RunExclusive '%s{key}' %s{stage}: %s{failure.Message}"
                            (DateTime.UtcNow - startedAt)
                    )
                ))
        with reportingFailure ->
            error pluginName $"Reporting the failure of '%s{key}' also failed: %s{reportingFailure.ToString()}"

    /// A stopped executor admits nothing. Raise the failure that stopped it. An executor
    /// that stops after this check still refuses, with that failure as the refusal's
    /// inner exception.
    let admit (admission: unit -> 'T) : 'T =
        match owner.Snapshot.ExecutorFault with
        | Some failure -> raise failure
        | None -> admission ()

    // Bound to the executor's mailbox once it exists. No closure that admits work is
    // reachable before `registerHandlerForOwner` returns.
    let mutable deliver: PluginEvent<'Msg> * PluginWorkOwner.WorkId -> unit = ignore

    // Results handed to the mailbox and not yet picked up, by the fold's identity, each
    // with the subtask that shows it waiting. `finishRun` adds one as it delivers the
    // fold; the receive loop removes it as it takes the fold off the mailbox.
    let queuedResults =
        System.Collections.Concurrent.ConcurrentDictionary<PluginWorkOwner.WorkId, string>()

    // The consumers of each delivered intent that clients alone hold, from its delivery
    // until its fold has been processed. A claim that fold makes, and an intent it
    // enqueues, is held by the same consumers. Every other event is held by the daemon.
    let clientHeld =
        System.Collections.Concurrent.ConcurrentDictionary<PluginWorkOwner.WorkId, PluginWorkOwner.ConsumerLeases>()

    let leasesOf (event: PluginWorkOwner.WorkId) =
        match clientHeld.TryGetValue event with
        | true, leases -> leases
        | _ -> PluginWorkOwner.HeldByDaemon

    let post (message: 'Msg) =
        let identity = admit owner.AdmitEvent
        deliver (Custom message, identity)

    /// Enqueue an intent on behalf of `leases`. When clients alone hold it, it is
    /// withdrawn the moment the last of them goes while it is still queued.
    let enqueueExclusiveIntentFor
        (leases: PluginWorkOwner.ConsumerLeases)
        (key: string)
        (coalescingKey: string option)
        (message: 'Msg)
        =
        let carrier, receipt =
            admit (fun () ->
                owner.EnqueueLeasedIntent(
                    key,
                    coalescingKey,
                    leases,
                    fun identity held ->
                        match held with
                        | PluginWorkOwner.HeldByClients _ -> clientHeld[identity] <- held
                        | PluginWorkOwner.HeldByDaemon -> ()

                        deliver (Custom message, identity)
                ))

        match leases with
        | PluginWorkOwner.HeldByClients tokens ->
            for token in tokens do
                // A token that has already fired runs this at once, outside any store
                // change. Withdrawal is total: it refuses what it may not withdraw.
                token.Register(fun () ->
                    try
                        if owner.WithdrawReleased carrier then
                            info pluginName $"'%s{key}' intent withdrawn: every client that wanted it has gone"
                    with failure ->
                        error pluginName $"withdrawing '%s{key}' intent failed: %s{failure.ToString()}")
                |> ignore
        | PluginWorkOwner.HeldByDaemon -> ()

        receipt

    let enqueueExclusiveIntent = enqueueExclusiveIntentFor PluginWorkOwner.HeldByDaemon

    /// Retire a finished run. A successful result becomes the run's result fold, which
    /// keeps the key until the result's `Update` commits. Shared-resource classification
    /// and release happen first, while the run still holds its key; either failing turns
    /// the run into a failure, so no success is folded.
    let finishRun
        (key: string)
        (identity: PluginWorkOwner.WorkId)
        (sharedRun: (string * ('Msg -> SharedResourceState) * SharedResourceState) option)
        (startedAt: DateTime)
        (outcome: Result<'Msg, exn>)
        =
        let completion =
            match sharedRun with
            | None -> outcome
            | Some(sharedKey, classify, _) ->
                let classified, resourceState =
                    match outcome with
                    | Result.Ok message ->
                        try
                            outcome, classify message
                        with failure ->
                            error pluginName $"RunExclusiveShared '%s{key}' classifier failed: %s{failure.ToString()}"
                            Result.Error failure, Invalid $"%s{pluginName} shared result classifier faulted"
                    | Result.Error _ -> outcome, Invalid $"%s{pluginName} shared work faulted"

                try
                    services.ReleaseSharedRun sharedKey resourceState
                    classified
                with failure ->
                    Result.Error failure

        match completion with
        | Result.Ok message ->
            // `None`: the executor has stopped, so there is nothing to fold into. The
            // worker retires and its result is not published.
            match owner.CompleteRun identity with
            | Some fold ->
                let subtask = QueuedResult.subtaskKey key
                queuedResults[fold] <- subtask
                services.StartSubtask handler.Name subtask (QueuedResult.label key)
                deliver (Custom message, fold)
            | None -> ()
        | Result.Error failure ->
            try
                reportRunFailure key startedAt "work failed" failure
            finally
                owner.FailRun(identity, failure)

    /// Retire a run every consumer abandoned. Nothing is published: no result folds, no
    /// failure is recorded, the shared resource goes back in the state the run was handed
    /// (a cancelled run learned nothing about it), and the status the run's `Running`
    /// displaced is reported again.
    let retireAbandoned
        (key: string)
        (identity: PluginWorkOwner.WorkId)
        (sharedRun: (string * ('Msg -> SharedResourceState) * SharedResourceState) option)
        (startedAt: DateTime)
        (displaced: PluginStatus)
        =
        info pluginName $"RunExclusive '%s{key}' cancelled: every client that wanted it has gone; nothing is published"

        let released =
            try
                sharedRun
                |> Option.iter (fun (sharedKey, _, handed) -> services.ReleaseSharedRun sharedKey handed)

                Result.Ok()
            with failure ->
                Result.Error failure

        match released with
        | Result.Ok() ->
            try
                lock statusLock (fun () -> reportToHost displaced)
            with failure ->
                error pluginName $"Reporting the cancellation of '%s{key}' failed: %s{failure.ToString()}"

            owner.AbandonRun identity
        | Result.Error failure ->
            try
                reportRunFailure key startedAt "shared release after cancellation failed" failure
            finally
                owner.FailRun(identity, failure)

    /// The worker body. Children the work spawns belong to its own process scope and are
    /// torn down before the run can retire.
    ///
    /// `abandoned` fires when every consumer of cooperative-safe work has gone. The work
    /// is then cancelled, its process scope reaped, and it retires with nothing
    /// published — even if it managed to return a result, since that result is only what
    /// was left once its processes were killed.
    let runOne
        (key: string)
        (identity: PluginWorkOwner.WorkId)
        (sharedRun: (string * ('Msg -> SharedResourceState) * SharedResourceState) option)
        (startedAt: DateTime)
        (displaced: PluginStatus)
        (abandoned: (System.Threading.CancellationTokenSource * IDisposable) option)
        (work: Async<'Msg>)
        =
        async {
            match abandoned with
            | None ->
                let! outcome =
                    async {
                        try
                            let! message =
                                ProcessRegistry.withChildScopeAsync System.Threading.CancellationToken.None work

                            return Result.Ok message
                        with failure ->
                            return Result.Error failure
                    }

                // Total: every plugin callback inside is guarded, and only this run retires
                // its own worker.
                finishRun key identity sharedRun startedAt outcome
            | Some(source, watch) ->
                let token = source.Token

                // Run as a task and read its outcome from a continuation: awaiting a
                // cancelled task directly would cancel THIS async too, and then nothing
                // would retire the worker.
                let running =
                    Async.StartAsTask(ProcessRegistry.withChildScopeAsync token work, cancellationToken = token)

                let! settled =
                    running.ContinueWith(fun (finished: System.Threading.Tasks.Task<'Msg>) -> finished)
                    |> Async.AwaitTask

                watch.Dispose()
                let cancelled = token.IsCancellationRequested
                source.Dispose()

                if cancelled then
                    retireAbandoned key identity sharedRun startedAt displaced
                elif settled.IsFaulted then
                    finishRun key identity sharedRun startedAt (Result.Error(settled.Exception.GetBaseException()))
                elif settled.IsCanceled then
                    finishRun key identity sharedRun startedAt (Result.Error(OperationCanceledException()))
                else
                    finishRun key identity sharedRun startedAt (Result.Ok settled.Result)
        }

    /// Claim `key` and report the `Running` the claim earns as one step against the
    /// status funnel. `after` names the event making the claim, so a result fold can
    /// launch its successor before it commits. Answers the status `Running` displaced.
    let claim (after: PluginWorkOwner.WorkId) (key: string) =
        lock statusLock (fun () ->
            match admit (fun () -> owner.TryClaim(key, after = after)) with
            | None -> None
            | Some identity ->
                let startedAt = DateTime.UtcNow
                let displaced = lastReported

                try
                    reportToHost (Running(since = startedAt))
                with failure ->
                    owner.FailRun(identity, failure)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()

                Some(identity, startedAt, displaced))

    /// What cancels `work` once its consumers have gone: nothing unless it is
    /// cooperative-safe and clients alone hold the event that launched it. `leases` are
    /// read when the claim is made: a shared run can start after its fold is done.
    let abandonmentOf (leases: PluginWorkOwner.ConsumerLeases) (work: Async<'Msg>) =
        if PluginWork.isCooperativeSafe work then
            PluginWorkOwner.ConsumerLeases.whenAllReleased leases
        else
            None

    let runExclusive (after: PluginWorkOwner.WorkId) (key: string) (work: Async<'Msg>) : RunClaim =
        match claim after key with
        | Some(identity, startedAt, displaced) ->
            try
                startOwned (runOne key identity None startedAt displaced (abandonmentOf (leasesOf after) work) work)
            with failure ->
                try
                    reportRunFailure key startedAt "failed to start" failure
                finally
                    owner.FailRun(identity, failure)

            Claimed
        | None ->
            // Exclusion-slot contention: the run is NOT started and the caller must
            // decide (skip or queue). The debug line keeps "why didn't my edit
            // re-run this plugin?" answerable from the log.
            debug pluginName $"exclusion-slot busy: '%s{key}' run not started (a previous run is still in flight)"
            SlotBusy

    let runExclusiveShared
        (after: PluginWorkOwner.WorkId)
        (key: string)
        (sharedKey: string)
        (workFor: SharedResourceState -> Async<'Msg>)
        (classify: 'Msg -> SharedResourceState)
        (failureMessage: exn -> 'Msg)
        : SharedRunClaim =
        match claim after key with
        | None -> LocalSlotBusy
        | Some(identity, startedAt, displaced) ->
            let leases = leasesOf after

            let start resourceState =
                // The plugin's factory runs inside the worker, so a synchronous throw
                // while building the work is a work failure like any other.
                try
                    let produced =
                        try
                            Result.Ok(workFor resourceState)
                        with failure ->
                            Result.Error failure

                    let guardedWork =
                        match produced with
                        | Result.Ok work ->
                            async {
                                try
                                    return! work
                                with failure ->
                                    return failureMessage failure
                            }
                        | Result.Error failure -> async { return failureMessage failure }

                    let abandonment =
                        match produced with
                        | Result.Ok work -> abandonmentOf leases work
                        | Result.Error _ -> None

                    startOwned (
                        runOne
                            key
                            identity
                            (Some(sharedKey, classify, resourceState))
                            startedAt
                            displaced
                            abandonment
                            guardedWork
                    )

                    SharedStarted
                with launchFailure ->
                    // The scheduler hands the resource on before this runs, and the run
                    // keeps its key until then.
                    SharedStartFailed(fun released ->
                        try
                            match released with
                            | Result.Error cleanupFailure -> raise cleanupFailure
                            | Result.Ok() -> ()

                            reportRunFailure key startedAt "failed to start" launchFailure
                            let message = failureMessage launchFailure

                            match owner.CompleteRun identity with
                            | Some fold -> deliver (Custom message, fold)
                            | None -> ()
                        with failure ->
                            try
                                reportRunFailure key startedAt "startup cleanup failed" failure
                            finally
                                owner.FailRun(identity, failure))

            match services.ClaimOrQueueSharedRun sharedKey start with
            | Some resourceState ->
                match start resourceState with
                | SharedStarted -> ()
                | SharedStartFailed afterRelease ->
                    finishSharedStartFailure
                        (fun () ->
                            services.ReleaseSharedRun sharedKey (Invalid $"%s{pluginName} shared work failed to start"))
                        afterRelease

                SharedClaimed
            | None -> SharedQueued

    let isRunning (key: string) = owner.Snapshot.IsRunning key

    let slotHolder (key: string) =
        let snapshot = owner.Snapshot

        if snapshot.IsRunning key then SlotHolder.LiveRun
        elif snapshot.IsHeld key then SlotHolder.Fold
        else SlotHolder.Free

    /// The plugin's name is part of the operation name, so the wedge message that names
    /// an overrun declaration names the plugin that made it.
    let declareBoundedWork (label: string) (deadline: System.TimeSpan) =
        SupervisedWork.declare
            owner.Store
            SupervisedWork.defaultScheduler
            $"%s{PluginName.value handler.Name}: %s{label}"
            deadline

    /// The context `Update` receives for one event. `event` names that event, so a claim
    /// it makes can follow the key's result fold.
    let contextFor (event: PluginWorkOwner.WorkId) : PluginCtx<'Msg> =
        { ReportStatus = reportPluginStatus (Some event)
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
          EnqueueExclusiveIntent = enqueueExclusiveIntentFor (leasesOf event)
          StartSubtask = fun key label -> services.StartSubtask handler.Name key label
          UpdateSubtask = fun key label -> services.UpdateSubtask handler.Name key label
          EndSubtask = fun key -> services.EndSubtask handler.Name key
          Log = fun msg -> services.Log handler.Name msg
          CompleteWithTimeout = fun reason -> services.SetNextTerminalOutcome handler.Name (TimedOut reason)
          RunExclusive = runExclusive event
          RunExclusiveShared = runExclusiveShared event
          SlotHolder = slotHolder
          DeclareBoundedWork = declareBoundedWork
          FcsSuppressedCodes = services.FcsSuppressedCodes
          ProjectGraph = services.ProjectGraph }

    // The narrow context handed to IPC command handlers — see `CommandCtx`.
    let commandCtx: CommandCtx<'Msg> =
        { RepoRoot = services.RepoRoot
          Log = fun msg -> services.Log handler.Name msg
          Post = post
          EnqueueExclusiveIntent = enqueueExclusiveIntent
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
                            let fileOfKey = compKey.File |> Option.defaultValue "-"

                            // The typed miss reason is the whole point of `Lookup` over
                            // `TryGet`: with content-addressed keys a cold start is
                            // indistinguishable from a key accidentally salted with
                            // something machine-local unless the miss NAMES the input
                            // that moved.
                            let lookupResult =
                                match cache.Lookup compKey cacheKey with
                                | TaskCache.CacheHit result ->
                                    FsHotWatch.Logging.debug
                                        "task-cache"
                                        $"plugin=%s{pluginName} file=%s{fileOfKey} hit=true"

                                    Some result
                                | TaskCache.CacheMiss reason ->
                                    FsHotWatch.Logging.debug
                                        "task-cache"
                                        $"plugin=%s{pluginName} file=%s{fileOfKey} hit=false miss=%s{TaskCache.CacheMissReason.describe reason}"

                                    None

                            match lookupResult with
                            | Some result ->
                                if compKey.File.IsSome then
                                    System.Threading.Interlocked.Increment(&perFileReplayed) |> ignore

                                // Clear ONLY what the cached run itself
                                // cleared. A replay must be observationally
                                // indistinguishable from running the handler (the
                                // invariant already stated for the build cache),
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
                                // entry's scope:
                                //
                                // • Whole-run entries (`CachedRun*`) store a verdict
                                //   that is a pure function of the key, so the
                                //   ORIGINAL run's summary + true duration replay
                                //   verbatim.
                                // • Per-file entries (`CachedFile*`) carry no summary
                                //   BY CONSTRUCTION. Theirs is derived from the
                                //   plugin's live ledger set AFTER the error replay
                                //   above has landed this entry's findings. The
                                //   derivation runs INSIDE the status funnel below
                                //   (never here), so the ledger snapshot the summary
                                //   reflects is exactly the one the report lands on.
                                //   Otherwise: "analyzed 1044 files, 5 findings
                                //   (cached)" over an empty ledger and a green
                                //   verdict.
                                //
                                //   The ledger alone reads the same whether this plugin
                                //   examined every file or replayed every file, so the
                                //   summary carries the tally of both.
                                let derivedVerdict elapsed =
                                    let examined = System.Threading.Volatile.Read(&perFileExamined)
                                    let replayed = System.Threading.Volatile.Read(&perFileReplayed)

                                    RunVerdict.create
                                        $"%s{ledgerSummary (services.GetPluginDiagnostics handler.Name)}; %d{examined} files examined, %d{replayed} replayed from cache"
                                        elapsed

                                // Built lazily: the status funnel evaluates this only when
                                // it reports, so the per-file ledger read never happens
                                // for a replay whose terminal is dropped.
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
                                // re-reporting the cached `Completed` would stomp the
                                // `Running` an in-flight test run set. The replay goes
                                // through the same funnel as every other report, which
                                // drops a terminal while a run owes its verdict. A
                                // replayed event is never a run's result fold, so it
                                // reports as no one's. Errors and emitted events still
                                // replay.
                                reportStatus None true mkReplayTerminal "cache replay" |> ignore

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
                    /// It goes through the status funnel: while an exclusive run owes its
                    /// verdict, that run reports the terminal. The fault is logged either
                    /// way. `identity` is the faulted event: a run's result fold that faults
                    /// is the run's verdict.
                    ///
                    /// `what` names the layer that faulted ("handler", "dispatch"), and
                    /// `startedAt` is when that layer began, so the verdict carries a
                    /// MEASURED elapsed rather than a fabricated zero-length run.
                    let reportForcedFailure
                        (identity: PluginWorkOwner.WorkId)
                        (what: string)
                        (startedAt: DateTime)
                        (ex: exn)
                        =
                        error pluginName $"%s{what} failed: %s{ex.ToString()}"

                        reportStatus
                            (Some identity)
                            true
                            (fun () ->
                                Failed(
                                    ex.ToString(),
                                    DateTime.UtcNow,
                                    RunVerdict.create $"%s{what} failed: %s{ex.Message}" (DateTime.UtcNow - startedAt)
                                ))
                            $"%s{what} fault"
                        |> ignore

                    let safeUpdate pluginCtx state event =
                        async {
                            try
                                let! candidate = handler.Update pluginCtx state event
                                return Result.Ok candidate
                            with failure ->
                                return Result.Error("Plugin handler", failure)
                        }

                    /// Run Update with a capturing context that records side effects. A
                    /// cacheable terminal becomes a deferred cache write, run only after the
                    /// candidate state is published. `cacheKeyOpt` is the same key the
                    /// preceding `tryReplayCache` lookup used — never recompute it here.
                    let runAndCache
                        (identity: PluginWorkOwner.WorkId)
                        (event: PluginEvent<'Msg>)
                        (state: 'State)
                        (cacheKeyOpt: ContentHash option)
                        =
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

                                // A per-file entry stores no summary and no timestamp, only
                                // that the file's work finished (see the mint below), and a
                                // replay reports it through the same funnel. So the funnel
                                // dropping this report while a run owns the status withholds
                                // its PUBLICATION, not the file's cached result: work done
                                // while a run is in flight is replayed like any other.
                                let perFile = (compositeKey event).File.IsSome

                                // The event's own context, with every report and run it
                                // makes also captured for the cache entry.
                                let capturingCtx =
                                    { contextFor identity with
                                        ReportStatus =
                                            fun status ->
                                                // A whole-run entry replays its verdict, so it captures
                                                // only a report that landed: a terminal the funnel
                                                // dropped was never observable, and must not become a
                                                // cached verdict either.
                                                let landed =
                                                    reportStatus
                                                        (Some identity)
                                                        (PluginStatus.isTerminal status)
                                                        (fun () -> status)
                                                        "status report"

                                                if landed || perFile then
                                                    capturedStatus <- Some status
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
                                        RunExclusive =
                                            fun key work ->
                                                match runExclusive identity key work with
                                                | Claimed ->
                                                    launchedRunInWindow <- true
                                                    Claimed
                                                | SlotBusy -> SlotBusy
                                        RunExclusiveShared =
                                            fun key sharedKey workFor classify failureMessage ->
                                                match
                                                    runExclusiveShared
                                                        identity
                                                        key
                                                        sharedKey
                                                        workFor
                                                        classify
                                                        failureMessage
                                                with
                                                | SharedClaimed ->
                                                    launchedRunInWindow <- true
                                                    SharedClaimed
                                                | SharedQueued ->
                                                    launchedRunInWindow <- true
                                                    SharedQueued
                                                | LocalSlotBusy -> LocalSlotBusy }

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

                                if cachedStatus.IsSome && compKey.File.IsSome then
                                    System.Threading.Interlocked.Increment(&perFileExamined) |> ignore

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
                                let! attempted = safeUpdate (contextFor identity) state event
                                return attempted |> Result.map (fun candidate -> candidate, None)
                        }

                    /// Publish the candidate, finalize, write the cache, then settle the
                    /// event. A preparation failure publishes nothing. Once published, a
                    /// failure keeps the candidate and fails the event.
                    let commit
                        (identity: PluginWorkOwner.WorkId)
                        (state: 'State)
                        (candidate: 'State)
                        (cacheWrite: (unit -> unit) option)
                        (updated: bool)
                        =
                        async {
                            match handler.PrepareCommit with
                            | Some prepare when updated ->
                                let! prepared =
                                    async {
                                        try
                                            let! prepared = prepare state candidate
                                            return Result.Ok prepared
                                        with failure ->
                                            return Result.Error failure
                                    }

                                match prepared with
                                | Result.Error failure ->
                                    return Result.Error("Plugin commit", PluginWorkOwner.CommitFailed failure)
                                | Result.Ok prepared ->
                                    try
                                        owner.PublishEventState(identity, candidate)
                                        do! prepared.Finalize
                                        cacheWrite |> Option.iter (fun write -> write ())
                                        owner.SettleEvent(identity, preparedCommit = true)
                                        return Result.Ok()
                                    with failure ->
                                        return Result.Error("Plugin commit", PluginWorkOwner.CommitFailed failure)
                            | _ ->
                                try
                                    owner.PublishEventState(identity, candidate)
                                    cacheWrite |> Option.iter (fun write -> write ())
                                    owner.SettleEvent identity
                                    return Result.Ok()
                                with failure ->
                                    return Result.Error("Plugin commit", PluginWorkOwner.UpdateFailed failure)
                        }

                    let rec loop () =
                        async {
                            let! event, identity = inbox.Receive()
                            beginRunOf identity

                            match queuedResults.TryRemove identity with
                            | true, subtask -> services.EndSubtask handler.Name subtask
                            | _ -> ()

                            let dispatchStarted = DateTime.UtcNow
                            // Only this loop publishes domain state, so the snapshot holds
                            // exactly the state the previous event left.
                            let state = owner.Snapshot.State

                            let! attempted =
                                async {
                                    try
                                        // Computed ONCE per dispatched event — see `tryReplayCache`.
                                        let cacheKeyOpt = handler.CacheKey |> Option.bind (fun key -> key state event)

                                        // A `Custom` message is a cache WRITER, never a cache READER.
                                        //
                                        // Every other event is an OBSERVATION whose payload is what the
                                        // key is computed FROM, so same key ⇒ same input ⇒ the cached
                                        // result IS the result. A `Custom` message is the plugin's own
                                        // post — the delivery of work already done — and its payload is
                                        // NOT in the key, so two different runs can collide on one key.
                                        // A hit here is a collision, and serving it skips the handler —
                                        // the only thing that folds the finished run into the state.
                                        //
                                        // The WRITE keeps the real key: a Custom window is how the entry
                                        // the next `BuildCompleted` hits gets minted at all.
                                        let replayKeyOpt =
                                            match event with
                                            | Custom _ -> None
                                            | _ -> cacheKeyOpt

                                        if tryReplayCache event replayKeyOpt then
                                            return Result.Ok(state, None, false)
                                        else
                                            let! result = runAndCache identity event state cacheKeyOpt

                                            return
                                                result |> Result.map (fun (candidate, write) -> candidate, write, true)
                                    with failure ->
                                        // The dispatch machinery around `Update`: the cache-key
                                        // thunks and the replay itself.
                                        return Result.Error("Dispatch (cache key or cache replay)", failure)
                                }

                            let! committed =
                                match attempted with
                                | Result.Error(what, failure) ->
                                    async.Return(Result.Error(what, PluginWorkOwner.UpdateFailed failure))
                                | Result.Ok(candidate, cacheWrite, updated) ->
                                    commit identity state candidate cacheWrite updated

                            // The fold has made every claim and intent it will: its
                            // consumers now live on in those.
                            clientHeld.TryRemove identity |> ignore

                            let foldTook = DateTime.UtcNow - dispatchStarted

                            if foldTook >= SlowFoldThreshold then
                                info
                                    pluginName
                                    (slowFoldLine
                                        (eventKind event)
                                        foldTook
                                        inbox.CurrentQueueLength
                                        (queuedResults.Values |> List.ofSeq))

                            match committed with
                            | Result.Ok() -> ()
                            | Result.Error(what, failure) ->
                                let cause =
                                    match failure with
                                    | PluginWorkOwner.UpdateFailed cause
                                    | PluginWorkOwner.CommitFailed cause -> cause

                                match owner.Snapshot.ExecutorFault with
                                | Some _ ->
                                    // The executor stopped while this event was in flight, and
                                    // stopping it already failed this event's receipt. Retiring
                                    // the event again would be refused; end the loop instead.
                                    raise cause
                                | None ->
                                    // Reporting is part of the event. Its attempt comes before
                                    // the one failed settlement, and its own failure is logged,
                                    // never allowed to retire the event a second time.
                                    try
                                        reportForcedFailure identity what dispatchStarted cause
                                    with reportingFailure ->
                                        error
                                            pluginName
                                            $"Failure reporting also failed: %s{reportingFailure.ToString()}"

                                    owner.FailEvent(identity, failure)

                            return! loop ()
                        }

                    loop ())
            )

    // The loop handles its own faults, so this should never fire. If it does, the
    // executor has stopped: the owner fails every admitted event's receipt, keeps live
    // workers owned until they finish, and refuses new work, all in one publication.
    agent.Error.Add(fun ex ->
        try
            owner.FaultExecutor ex
        finally
            error pluginName $"Mailbox loop crashed (programming bug, agent stopped): %s{ex.ToString()}")

    deliver <- fun message -> inOwnerContext (fun () -> agent.Post message)

    // Register commands. An observation reads one published snapshot and never waits
    // behind running work; a request is the plugin's own code with a posting context.
    for (commandName, command) in handler.Commands do
        services.RegisterCommand(
            commandName,
            fun args ->
                match command with
                | PluginCommand.Request request ->
                    // A request runs under its requester's token — the IPC server's
                    // per-connection token for a client — so the intents it enqueues are
                    // held by that client, and released when it goes away.
                    async {
                        let! requester = Async.CancellationToken
                        let leases = PluginWorkOwner.ConsumerLeases.ofClient requester

                        return!
                            request
                                { commandCtx with
                                    EnqueueExclusiveIntent = enqueueExclusiveIntentFor leases }
                                args
                    }
                | PluginCommand.Observe read ->
                    async {
                        let snapshot = owner.Snapshot

                        let readCtx =
                            { PluginCommand.readContext commandCtx with
                                IsRunning = snapshot.IsRunning }

                        return! read readCtx snapshot.State args
                    }
        )

    let dispatchTracked (dispatched: PluginDispatchEvent) =
        match dispatched with
        | DispatchBatchChecked _ -> System.Threading.Interlocked.Increment(&runEpoch) |> ignore
        | _ -> ()

        let admitted event =
            let identity, completion = admit owner.AdmitTrackedEvent

            match event with
            | FileChecked _ -> dispatchedInRun[identity] <- System.Threading.Volatile.Read(&runEpoch)
            | _ -> ()

            deliver (event, identity)
            Some(DispatchReceipt completion)

        let has subscription =
            handler.Subscriptions.Contains subscription

        match dispatched with
        | DispatchFileChanged c when has SubscribeFileChanged -> admitted (FileChanged c)
        | DispatchFileChecked r when has SubscribeFileChecked -> admitted (FileChecked r)
        | DispatchBatchChecked r when has SubscribeBatchChecked -> admitted (BatchChecked r)
        | DispatchBuildCompleted r when has SubscribeBuildCompleted -> admitted (BuildCompleted r)
        | DispatchTestRunStarted r when has SubscribeTestRunStarted -> admitted (TestRunStarted r)
        | DispatchTestProgress r when has SubscribeTestProgress -> admitted (TestProgress r)
        | DispatchTestRunCompleted r when has SubscribeTestRunCompleted -> admitted (TestRunCompleted r)
        | DispatchCommandCompleted r when has SubscribeCommandCompleted -> admitted (CommandCompleted r)
        | _ -> None

    // Untracked dispatch cannot surface a refusal to anyone: a stopped executor's fault is
    // already in the owner snapshot, so the event is logged and dropped.
    let dispatch event =
        try
            dispatchTracked event |> ignore
        with failure ->
            error pluginName $"refused a dispatched event: %s{failure.Message}"

    { Name = handler.Name
      Dispatch = dispatch
      DispatchTracked = dispatchTracked
      Teardown = handler.Teardown
      IsBusy = fun () -> owner.Snapshot.IsBusy
      CompletedDispatches = fun () -> owner.Snapshot.CompletedEvents
      Subscriptions = handler.Subscriptions
      Fault = fun () -> owner.Snapshot.Fault }

/// Register a plugin as one row of a host's shared publication.
let internal registerHandlerWithOwner
    (store: PluginWorkOwner.Store)
    (services: PluginHostServices)
    (handler: PluginHandler<'State, 'Msg>)
    : RegisteredPlugin =
    registerHandlerForOwner (PluginWorkOwner.Owner(handler.Init, store, PluginName.value handler.Name)) services handler

/// Register a declarative plugin handler with a publication of its own, returning a
/// type-erased RegisteredPlugin. A host registers every plugin into one shared
/// publication instead (`PluginHost.RegisterHandler`).
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
