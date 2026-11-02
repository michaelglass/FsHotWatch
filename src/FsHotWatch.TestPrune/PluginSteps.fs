/// The plugin's steps over its state: debt accounting, the run lifecycle, refresh and launch helpers, each over what it captured.
module FsHotWatch.TestPrune.PluginSteps

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunResults
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.TestFilters
open FsHotWatch.TestPrune.FailureReports
open FsHotWatch.TestPrune.RunnerArgs
open FsHotWatch.TestPrune.TestReports
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.TestPrune.PluginCommands
open FsHotWatch.TestPrune.IndexMaintenance
open FsHotWatch.TestPrune.RunSupport
open FsHotWatch.TestPrune.ProjectLaunch
open FsHotWatch.TestPrune.RunGroups
open FsHotWatch.TestPrune.RunExecution
open FsHotWatch.TestPrune.AffectedSelection
open FsHotWatch.TestPrune.ImpactLaunch
open FsHotWatch.TestPrune.ImpactRun
open FsHotWatch.TestPrune.FileCheckedHandler
open FsHotWatch.TestPrune.CheckHandlers
open FsHotWatch.TestPrune.RunOutcomeReport
open FsHotWatch.TestPrune.CompletionStages
open FsHotWatch.TestPrune.RunFinished
open FsHotWatch.TestPrune.PluginRuns
open FsHotWatch.TestPrune.PluginFold

/// Hold a build's launch for its tree's check results (`DiffWait`), and post its
/// expiry. The mailbox is not blocked: the `BatchChecked` it waits for is delivered
/// through it.
let internal holdForDiff (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
    let held =
        { Id = Guid.NewGuid()
          Since = DateTime.UtcNow }

    Logging.info
        "test-prune"
        $"BuildSucceeded: the check results for the tree this build compiled are not folded yet, so its symbol diff is unknown — holding the launch for the next BatchChecked (at most %.0f{DiffWait.bound.TotalSeconds}s)"

    Tasks.Task
        .Delay(DiffWait.bound)
        .ContinueWith(fun (_: Tasks.Task) ->
            try
                ctx.Post(DiffWaitExpired held.Id)
            with ex ->
                Logging.debug "test-prune" $"held launch expiry not delivered: %s{ex.Message}")
    |> ignore

    { state with AwaitingDiff = Some held }

/// Add `symbols` to the queue at one new revision, including a re-edit of a symbol
/// already queued: a run captured before it cannot have built it.
let internal enqueuePending (symbols: string list) (debt: VerificationDebt) =
    if symbols.IsEmpty then
        debt
    else
        let revision = debt.Revision + 1L

        { debt with
            PendingQueue = (debt.PendingQueue, symbols) ||> List.fold (fun q s -> Set.add s q)
            Revision = revision
            SymbolRevisions =
                (debt.SymbolRevisions, symbols)
                ||> List.fold (fun revisions s -> Map.add s revision revisions) }

/// Hold a launch while the host discovers its project model (`ObservedModel.awaitDiscovery`),
/// so the run is launched under the model it completes under. Called inside the launch's
/// exclusive work, before anything about the model is read.
let internal awaitDiscoveryBeforeLaunch (ctx: PluginCtx<TestPruneMsg>) =
    async {
        match ctx.ProjectGraph.ObserveModel() with
        | FsHotWatch.ProjectModel.Observation.Rediscovering generation ->
            Logging.info
                "test-prune"
                $"project discovery (generation %d{generation}) is in flight; waiting for it before launching"
        | _ -> ()

        let! wait = ObservedModel.awaitDiscovery ctx.ProjectGraph
        ObservedModel.reportDiscoveryWait (Logging.warn "test-prune") (Logging.info "test-prune") wait
    }

/// The evidence candidates of `configs`: each project the model knows a `.fsproj` for, with
/// its reference closure and keyed configuration.
let internal evidenceCandidates
    (graph: ProjectGraphAccessor)
    (configs: TestConfig list)
    : TestEvidenceRun.Candidate list =
    let files = ObservedModel.projectFiles graph

    configs
    |> List.choose (fun config ->
        Map.tryFind config.Project files
        |> Option.map (fun fsproj ->
            { TestEvidenceRun.Candidate.Project = config.Project
              ProjectFile = fsproj
              Closure = TestEvidenceRun.closureOf graph.GetProjectReferences fsproj
              TestConfig = canonicalTestConfig config }))

/// A launch, named by the model it was selected under and what it selected, so the
/// completion line (`describeCompletionEvidence`) can be read against it.
let internal logLaunch (kind: string) (mode: TestMode) (launch: TestRunLaunch) =
    let scope =
        if TestMode.requestsFullSuite mode then
            "confirm mode"
        else
            "check mode"

    Logging.info
        "test-prune"
        $"Launching %s{kind} run under project model generation %s{ObservedModel.describe launch.ModelGeneration} (%s{scope}, %d{Map.count launch.Selection} project(s) selected)"

/// The failures `outcomes` leave, from those before them: a refreshed extension
/// leaves the map, a failed one enters it.
let internal failuresAfter (outcomes: ExtensionRefresh list) (failed: Map<string, string>) =
    outcomes
    |> List.fold
        (fun failed outcome ->
            match outcome with
            | ExtensionRefresh.Refreshed(name, _) -> Map.remove name failed
            | ExtensionRefresh.Failed(name, ex) -> Map.add name ex.Message failed)
        failed

/// Remove `symbols` from the queue: a covering run passed them, or they have no
/// covering test. Durable once the state carrying it is prepared.
let internal commitPending (symbols: Set<string>) (debt: VerificationDebt) =
    if symbols.IsEmpty then
        debt
    else
        { debt with
            PendingQueue = Set.difference debt.PendingQueue symbols
            SymbolRevisions = debt.SymbolRevisions |> Map.filter (fun s _ -> not (Set.contains s symbols)) }

/// Whether debt found while the key is held joins the run holding it instead of
/// queueing another. Only a full-suite run can take it. Under pass-through it takes
/// every arrival, since no run may follow it. Under impact selection it takes a
/// BootScan cohort, which scanned the tree that run is testing; an in-session cohort
/// is an edit the run never built, and queues its own run.
let internal joinsFullRun (state: TestPruneState) (bootScan: bool) =
    (state.InFlight |> Option.exists (fun run -> run.Scope = LaunchedFullSuite))
    && (TestMode.skips RerunIntents state.Mode || bootScan)

/// The debt revision of `symbol`. A symbol loaded from the durable queue and never
/// re-enqueued is at revision 0.
let internal revisionOf (debt: VerificationDebt) (symbol: string) : int64 =
    debt.SymbolRevisions |> Map.tryFind symbol |> Option.defaultValue 0L

/// The generation of the model the host currently publishes, when it is available.
let internal observeModelGeneration (ctx: PluginCtx<TestPruneMsg>) =
    ObservedModel.generation ctx.ProjectGraph

/// Attach what is owed to the full run in flight. The FIRST captured revision stands:
/// a later attach must not advance a symbol past an edit the held run never built.
let internal attachToFullRun (state: TestPruneState) =
    { state with
        DebtDuringFullRun =
            (state.DebtDuringFullRun, state.Debt.PendingQueue)
            ||> Set.fold (fun captured symbol ->
                if Map.containsKey symbol captured then
                    captured
                else
                    Map.add symbol (revisionOf state.Debt symbol) captured) }

/// Fold a refresh's outcomes into `state`: the log, the ledger, `FailedExtensions`.
let internal applyRefresh
    (ctx: PluginCtx<TestPruneMsg>)
    (outcomes: ExtensionRefresh list, refreshMs: int64)
    (state: TestPruneState)
    =
    for outcome in outcomes do
        match outcome with
        | ExtensionRefresh.Refreshed(name, edgeCount) ->
            ctx.ClearErrors(extensionLedgerKey name)
            Logging.info "test-prune" (extensionStoredLine name edgeCount refreshMs)
        | ExtensionRefresh.Failed(name, ex) ->
            Logging.error
                "test-prune"
                $"Extension '%s{name}' failed: %s{ex.ToString()}; its previously stored edges are kept and every test project runs in full until it answers"

            ctx.ReportErrors (extensionLedgerKey name) [ extensionFailedDiagnostic name ex.Message ]

    { state with
        FailedExtensions = failuresAfter outcomes state.FailedExtensions }

/// Owe one impact run after whatever holds the "tests" key. Coalesced: however many
/// triggers arrive while the key is held, one run is queued, and it decides against
/// the state it is delivered into (`impactRun`).
let internal enqueueImpactRun (ctx: PluginCtx<TestPruneMsg>) =
    ctx.EnqueueExclusiveIntent "tests" (Some "impact") ImpactRunRequested |> ignore

/// Each configured test project's `.fsproj` and compile-input identity now.
let internal observeTestInputsOf (testConfigs: TestConfig list option) (ctx: PluginCtx<TestPruneMsg>) =
    ObservedModel.testInputs ctx.ProjectGraph (defaultArg testConfigs [] |> List.map _.Project)

/// Why the full-suite baseline cannot vouch for what a filtered run
/// over the configured projects would skip — `None` when it can. Analysis-only
/// daemons make no test claim and so owe no baseline.
///
/// Unknown debt is folded in: the baseline composes with the queue (the queue names
/// what changed since the baseline), so a baseline beside a ledger that cannot be
/// read vouches for nothing until a full suite re-earns both.
let internal baselineInvalidReasonIn (runnableProjects: Set<string>) (debt: VerificationDebt) : string option =
    if Set.isEmpty runnableProjects then
        None
    elif debt.RecoveryOutstanding then
        Some "what is owed is unknown, so the full-suite baseline cannot say what changed since it was earned"
    else
        match debt.Baseline with
        | None -> Some FullSuiteBaseline.absentReason
        | Some baseline -> FullSuiteBaseline.staleness runnableProjects baseline

/// The unconfigured projects `owedTo` still waits on for `symbols`, for a message
/// that has to name them.
let internal owedElsewhereIn
    (runnableProjects: Set<string>)
    (owedTo: string -> Set<string>)
    (symbols: string seq)
    : Set<string> =
    symbols
    |> Seq.map (fun s -> Set.difference (owedTo s) runnableProjects)
    |> Set.unionMany

/// The covering test projects a symbol's debt waits on, under ONE resolution of the
/// declared exclusions. Empty ⇒ nothing is owed for it and it may leave the queue.
///
/// A covering project counts unless it is unconfigured AND declared excluded with a
/// written reason. That is the whole policy, applied at every place debt is
/// classified — the flush that drops uncovered symbols, the launch that captures each
/// symbol's coverers, the completion that retires them (launch and BootScan cohorts
/// alike), and the residual diagnostics that name what is still owed:
///   * a CONFIGURED project always counts, whatever an exclusion says;
///   * an unconfigured project with no declaration counts. Nothing can run it here,
///     so the symbol stays owed and the verdict stays red, naming the project. The
///     earlier rule dropped such a symbol, which let a configured-suite green
///     discharge tests that never ran;
///   * a blank reason declares nothing.
/// Excluding a project is never evidence that its tests passed: the declaration only
/// removes that project from THIS gate's claim.
///
/// `excluded` is lazy so a classification that never meets an unconfigured coverer
/// never resolves the declarations. Analysis-only daemons make no test claim, so every
/// covering project counts and the declarations are never consulted. `covering` is a
/// pass's `coveringOf`.
let internal debtScopeIn
    (runnableProjects: Set<string>)
    (excluded: Lazy<Map<string, string>>)
    (covering: string -> Set<string>)
    : string -> Set<string> =
    let declaredExcluded (project: string) =
        match Map.tryFind project excluded.Value with
        | Some reason -> not (String.IsNullOrWhiteSpace reason)
        | None -> false

    fun symbol ->
        let projects = covering symbol

        if Set.isEmpty runnableProjects then
            projects
        else
            projects
            |> Set.filter (fun project -> Set.contains project runnableProjects || not (declaredExcluded project))

/// The test projects covering each symbol a classification pass is about: what its
/// single-seed `QueryAffectedTests` would select from. One grouped query answers all
/// of `symbols`, run the first time any symbol is asked about and never when none is.
/// A symbol outside `symbols` is answered by a grouped query of its own.
let internal coveringOfIn (queries: ImpactQueries) (symbols: string seq) : string -> Set<string> =
    let known = symbols |> Seq.distinct |> List.ofSeq
    let grouped = lazy (queries.CoveringProjectsBySeed known)

    fun symbol ->
        match Map.tryFind symbol grouped.Value with
        | Some projects -> projects
        | None ->
            queries.CoveringProjectsBySeed [ symbol ]
            |> Map.tryFind symbol
            |> Option.defaultValue Set.empty

/// Whether a `FileChecked` cache hit may stand in for analysing `result`
/// (`IndexedAnalysis`): the index holds exactly this analysis, and no other is pending
/// for the file. Analysis-only daemons keep the plain key.
/// The analysis identity the index holds for a file (`IndexedAnalysis`). An index that
/// cannot be read holds nothing it can vouch for.
let internal indexedKeyIn (db: Database) (relPath: string) : string option =
    try
        db.GetFileKey relPath
    with ex ->
        Logging.debug "test-prune" $"index identity unreadable for %s{relPath}: %s{ex.Message}"
        None

/// Run every extension and time the whole refresh: it runs them in one call.
let internal refreshTimedIn (db: Database) (repoRoot: string) (exts: ITestPruneExtension list) () =
    let refreshClock = Diagnostics.Stopwatch.StartNew()
    let outcomes = refreshExtensionEdges db repoRoot exts
    outcomes, refreshClock.ElapsedMilliseconds

/// Write the queue before the analysis flush advances the durable symbol snapshot:
/// once the snapshot advances, a symbol missing from the durable queue can no longer
/// be re-detected after a crash. Only ever ADDS to what is on disk — the state it
/// writes is the published queue plus this event's enqueues — so writing it before
/// publication can over-test, never under-test.
///
/// Unknown debt is not written: the unreadable file is the honest record until the
/// recovering full suite publishes a new one.
let internal persistQueueAdditionsIn (repoRoot: string) (debt: VerificationDebt) =
    if not debt.RecoveryOutstanding then
        try
            PendingVerification.save repoRoot debt.PendingQueue
        with ex ->
            Logging.warn
                "test-prune"
                $"failed to persist pending-verification queue before the analysis flush: %s{ex.Message}; the queue is published with this event's state"

/// Records, durably and before the run returns, that a project's runtime coverage did
/// not ingest, then tells the owner through a message. A record that cannot be written
/// is logged; the message still makes this session owe the project.
let internal coverageIngestFailedIn (repoRoot: string) (ctx: PluginCtx<TestPruneMsg>) (failure: CoverageIngestFailure) =
    try
        armCoverageNotIngested
            (fun () -> ctx.Post(RuntimeCoverageFailed(failure.Project, failure.Reason)))
            (CoverageNotIngested.sidecarPath repoRoot)
            failure
    with ex ->
        ctx.Post(RuntimeCoverageFailed(failure.Project, failure.Reason))

        Logging.error
            "test-prune"
            $"failed to durably record that the runtime coverage of %s{failure.Project} did not ingest (%s{CoverageNotIngested.sidecarPath repoRoot}): %s{ex.Message}"

/// Which cause selected each project of a run that is about to execute. Logged once
/// the run is past the zero-affected skip, which executes nothing and selects nothing.
let internal logSelectedByIn (repoRoot: string) (launch: TestRunLaunch) =
    Logging.info "test-prune" $"Selected by: %s{ProjectAttribution.describe launch.SelectedBy}"

    // Priced before it runs, from the class durations the newest runs recorded
    // (`SelectionPrice`). A diagnostic: a price that cannot be read never stops a run.
    try
        let book =
            SelectionPrice.Book.ofRunsDir SelectionPrice.DefaultWindow (FsHotWatch.Ctrf.reportsDir repoRoot)

        Logging.info
            "test-prune"
            $"Priced: %s{SelectionPrice.describe (SelectionPrice.price book (askedOf launch.Selection))}"
    with ex ->
        Logging.warn "test-prune" $"Priced: not priced — %s{ex.Message}"

/// The run in flight has concluded: forget it, and end the pass-through it ran for
/// when it executed at least one project to a verdict (`TestMode.afterRun`).
let internal endRunFrom (initialMode: TestMode) (executedAny: bool) (state: TestPruneState) =
    let mode =
        match state.InFlight with
        | Some run -> TestMode.afterRunIn initialMode run.Mode executedAny state.Mode
        | None -> state.Mode

    // Said out loud: a later launch in the same session is impact-selected again, and
    // its status line (`selected: yes`) reads as if the confirm had lost its scope.
    if TestMode.requestsFullSuite state.Mode && not (TestMode.requestsFullSuite mode) then
        Logging.info
            "test-prune"
            "Scope back to IMPACT-FILTERED: the full-suite run the scope was set for has concluded; later launches in this session are impact-selected"
    elif TestMode.launchesOwedRuns state.Mode && not (TestMode.launchesOwedRuns mode) then
        Logging.info
            "test-prune"
            "Holding test runs again: the run a command asked for has concluded; later builds launch none until a command (check, confirm, test-rerun) asks"
    elif
        not executedAny
        && state.InFlight |> Option.exists (fun run -> TestMode.requestsFullSuite run.Mode)
    then
        Logging.info
            "test-prune"
            "Scope stays FULL SUITE: the run launched for it executed no project to a verdict, so the next launch runs every project in full"

    { state with
        InFlight = None
        Mode = mode }

/// The one question every skip in this plugin is really asking: is the
/// needs-testing queue PROVABLY empty? Unknown debt is never `true` here — an empty
/// queue we could not read is not an empty queue.
///
/// An absent or stale full-suite baseline is owed work too — the
/// tests a filtered run skips have nothing to be equivalent TO until one exists —
/// so it is folded in here rather than checked beside this at each skip site.
let internal nothingOwedUnder (baselineInvalidReason: VerificationDebt -> string option) (debt: VerificationDebt) =
    Set.isEmpty debt.PendingQueue
    && Map.isEmpty debt.RuntimeObligations
    && Map.isEmpty debt.CoverageNotIngested
    && not debt.RecoveryOutstanding
    && Option.isNone (baselineInvalidReason debt)

/// What a launch from `inputs` will execute: the widenings `runTestsWithImpact`
/// applies to every project.
let internal launchScopeUnder (baselineInvalidReason: VerificationDebt -> string option) (inputs: TestRunInputs) =
    if
        TestMode.requestsFullSuite inputs.Mode
        || inputs.Debt.RecoveryOutstanding
        || Option.isSome (baselineInvalidReason inputs.Debt)
    then
        LaunchedFullSuite
    else
        LaunchedSelection

/// What a drain is FOR, in words. Unknown debt cannot be counted, so it is named.
let internal owedDescriptionUnder (baselineInvalidReason: VerificationDebt -> string option) (debt: VerificationDebt) =
    let queued = Set.count debt.PendingQueue

    if debt.RecoveryOutstanding then
        $"UNKNOWN verification debt (%s{describeUnknownDebt debt.UnknownDebt}), which only a full suite can prove, + %d{queued} newly-queued symbol(s)"
    else
        // A drain owed only to the baseline says so, or a reader sees
        // "0 symbol(s) awaiting verification — draining now" and goes looking for the
        // bug in the queue.
        let notIngested =
            if Map.isEmpty debt.CoverageNotIngested then
                ""
            else
                let projects = debt.CoverageNotIngested |> Map.keys |> String.concat ", "
                $" + a whole-project run of %s{projects}, whose runtime coverage did not ingest"

        match baselineInvalidReason debt with
        | Some reason ->
            $"%d{queued} symbol(s) awaiting verification + a full suite to earn the baseline (%s{reason})%s{notIngested}"
        | None -> $"%d{queued} symbol(s) awaiting verification%s{notIngested}"

/// Say why an owed launch was not made (`onlyRetriesRed`).
let internal logRedStandsWith (owedDescription: VerificationDebt -> string) (trigger: string) (state: TestPruneState) =
    Logging.info
        "test-prune"
        $"%s{trigger}: %s{owedDescription state.Debt} — all of it was in the run that concluded red over this same tree, so a launch would only retry it; that result stands until the tree or what is owed moves (AUTOMATION-1373)"

/// Whether an owed launch now would only retry the red run that concluded last
/// (AUTOMATION-1373). That run launched against every symbol owed now, each at the
/// revision it is at now, over the tree there is now, and no dependency fanout has
/// arrived since: the same selection over the same bytes. Its red is the answer for
/// those inputs. Under CPU starvation a retry costs as long as the run did, and every
/// cohort that folded while it ran had queued one, so a gate never converged.
///
/// Not launching can never make a green: the debt stays owed, the red stays standing.
/// An edit, a newly owed symbol, a fanout, or a full suite requested moves one of these
/// and launches as before. A run that executed nothing is no conclusion and is retried.
let internal onlyRetriesRedIn (repoRoot: string) (state: TestPruneState) =
    match state.LastRed with
    | Some red ->
        not (TestMode.requestsFullSuite state.Mode)
        && Set.isEmpty state.PendingForceRunProjects
        && Set.union state.Debt.PendingQueue (Set.ofList state.ChangedSymbols)
           |> Set.forall (fun symbol -> Map.tryFind symbol red.SymbolRevisions = Some(revisionOf state.Debt symbol))
        && ReceiptInputTree.matches (Some red.Tree) (ReceiptInputTree.read repoRoot)
    | None -> false

/// Take the answer of the refresh running beside the fold, if one is, into `state`.
let internal joinRefreshIn
    (extensionRefresh: ExtensionRefreshPhase ref)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    =
    match extensionRefresh.Value with
    | ExtensionRefreshPhase.NeverRefreshed
    | ExtensionRefreshPhase.Refreshed -> state
    | ExtensionRefreshPhase.RunningBesideFold refresh ->
        extensionRefresh.Value <- ExtensionRefreshPhase.Refreshed

        let answer =
            use _ = ctx.DeclareBoundedWork "extension refresh" ImpactSelectionDeadline
            refresh.GetAwaiter().GetResult()

        applyRefresh ctx answer state

let internal mayReplayIn
    (indexedKey: string -> string option)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (pending: Map<string, string>)
    (result: FileCheckResult)
    : bool =
    testConfigs |> Option.forall List.isEmpty
    || (let relPath =
            Path.GetRelativePath(repoRoot, AbsFilePath.value result.File).Replace('\\', '/')

        IndexedAnalysis.identityOf repoRoot result
        |> Option.exists (IndexedAnalysis.mayReplay (indexedKey relPath) (Map.tryFind relPath pending)))

/// The outcome-store key of each configured project's full run over the tree as it is now
/// (ADR-059 step 7b), for the `test-scope` reply. Empty without a store or without the
/// model's compile-input identities: nothing can be keyed then.
let internal currentOutcomeKeysIn
    (evidence: TestEvidenceRun.Wiring option)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (graph: ProjectGraphAccessor)
    : Map<string, string> =
    match evidence, ObservedModel.inputs graph with
    | Some wiring, Some identities ->
        TestEvidenceRun.currentOutcomeKeys
            wiring
            repoRoot
            (fun fsproj -> Map.tryFind fsproj identities)
            (evidenceCandidates graph (defaultArg testConfigs []))
    | _ -> Map.empty

/// The traces of a run launched under `mode`, when `tests.traces` is configured, with
/// the plugin's activity log and subtasks: a force run's `executeTests` has no `ctx`.
let internal tracesForIn
    (repoRoot: string)
    (traces: TraceWiring option)
    (ctx: PluginCtx<TestPruneMsg>)
    (mode: TestMode)
    : TraceRunHost option =
    traces
    |> Option.map (fun wiring ->
        { TraceRuntime =
            { Settings = wiring.Policy
              RepoRoot = repoRoot
              ExcludedProjects = wiring.OptedOut
              Mode = mode
              // Known once the run's selection is: set by `executeTests`.
              RunsEveryProjectInFull = false }
          TraceWiring = wiring
          TraceLog = ctx.Log
          HoldSubtask =
            fun key label ->
                ctx.StartSubtask key label

                { new IDisposable with
                    member _.Dispose() = ctx.EndSubtask key }
          IndexFold =
            { Folded =
                fun () ->
                    let folded =
                        Tasks.TaskCompletionSource(Tasks.TaskCreationOptions.RunContinuationsAsynchronously)

                    ctx.Post(IndexFoldProbe folded)
                    folded.Task
              Bound = TraceRun.indexFoldBound } })

/// The test-evidence plan of a run about to launch `configs`: every project the model
/// can key, and a lookup for each one `launching` admits that has no outstanding
/// failure, when `lookups` allows any. `None` without a store, or without the
/// model's compile-input identities (nothing can be keyed without them).
let internal evidencePlanForIn
    (evidence: TestEvidenceRun.Wiring option)
    (repoRoot: string)
    (ctx: PluginCtx<TestPruneMsg>)
    (configs: TestConfig list)
    (launching: string -> bool)
    (lookups: bool)
    (outstanding: OutstandingFailure list)
    : TestEvidenceRun.Plan option =
    evidence
    |> Option.bind (fun wiring ->
        match ObservedModel.inputs ctx.ProjectGraph with
        | None ->
            Logging.info "test-prune" "test evidence: the project model publishes no identities — nothing keyed"
            None
        | Some identities ->
            let failing = outstanding |> List.map _.Project |> Set.ofList
            let candidates = evidenceCandidates ctx.ProjectGraph configs

            let plan =
                TestEvidenceRun.plan
                    wiring
                    repoRoot
                    (fun fsproj -> Map.tryFind fsproj identities)
                    candidates
                    (fun project -> lookups && launching project && not (failing.Contains project))

            for KeyValue(project, why) in plan.Misses do
                Logging.info "test-prune" $"%s{project}: launching — %s{why}"

            Some plan)

/// A launch that found its artifacts or test host unavailable ran nothing: it revokes
/// the receipt, hands back the fanout it consumed, and fails.
let internal unavailableRunWith
    (endRun: bool -> TestPruneState -> TestPruneState)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (message: string)
    (owed: Set<string>)
    (reply: Tasks.TaskCompletionSource<string> option)
    =
    ctx.ReportStatus(PluginStatus.failedNow message message TimeSpan.Zero)

    { endRun false state with
        Receipt = HeldReceipt.Revoked $"the test run could not start: %s{message}"
        PendingForceRunProjects = Set.union state.PendingForceRunProjects owed
        Replies =
            reply
            |> Option.map (fun target -> target, JsonSerializer.Serialize {| error = message |})
            |> Option.toList }

/// Which repo-relative paths a build consumed, under the model the host publishes when
/// asked — so `executeTests` observes it after `beforeRun`, not at launch.
let internal compileInputsOfIn (repoRoot: string) (ctx: PluginCtx<TestPruneMsg>) () : string -> bool =
    ReceiptInputTree.feedsCompile repoRoot (ctx.ProjectGraph.ObserveCheckableFiles() |> Option.map snd)

/// Flush, then select, unless the mode skips `FlushSelection`: pass-through records
/// the debt without selecting or classifying it, and only a full run's green
/// discharges it.
let internal flushAndQueryAffectedWith
    (flushPending: PluginCtx<TestPruneMsg> -> TestPruneState -> TestPruneState * RuntimeCoverageObligations)
    (joinRefresh: StateStep)
    (selectAffected: TestPruneState -> RuntimeCoverageObligations -> TestPruneState)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    =
    let flushedState, runtimeObligations = flushPending ctx state

    // A selection that queries the index follows the extensions' edges.
    let flushedState =
        if
            Set.isEmpty flushedState.Debt.PendingQueue
            && List.isEmpty flushedState.ChangedSymbols
        then
            flushedState
        else
            joinRefresh ctx flushedState

    if TestMode.skips FlushSelection flushedState.Mode then
        { flushedState with
            Debt =
                { flushedState.Debt with
                    RuntimeObligations = runtimeObligations }
            AffectedTests = Analyzed []
            ChangedSymbolsAllUncovered = UncoveredChanges.No }
    else
        selectAffected flushedState runtimeObligations

/// `flushAndQueryAffected` under the bound it declares over itself
/// (`ImpactSelectionDeadline`). Every caller goes through this: the fold is the same
/// work whichever event drove it, and a caller that forgot the declaration would be
/// the one the stall detector failed.
let internal flushAndQueryAffectedBoundedWith
    (flushAndQueryAffected: StateStep)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    =
    use _declaration = ctx.DeclareBoundedWork "impact selection" ImpactSelectionDeadline
    flushAndQueryAffected ctx state

/// Launch the run a build held back (`holdForDiff`), selected from the state it is
/// delivered into, where the diff has now folded, or the wait expired without it.
let internal launchHeldWith
    (launchImpactRun: ImpactLauncher)
    (testConfigs: TestConfig list option)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (why: string)
    =
    let waited =
        state.AwaitingDiff
        |> Option.map (fun held -> (DateTime.UtcNow - held.Since).TotalSeconds)
        |> Option.defaultValue 0.0

    let state = { state with AwaitingDiff = None }

    match testConfigs with
    | Some configs when not configs.IsEmpty ->
        Logging.info "test-prune" $"Launching the build's held run after %.1f{waited}s: %s{why}"
        let fanout = state.PendingForceRunProjects

        let launchState =
            { state with
                PendingForceRunProjects = Set.empty }

        match launchImpactRun ctx configs launchState state.LastResults.IsSome fanout with
        | Some launched -> launched
        | None when joinsFullRun state false -> attachToFullRun state
        | None ->
            // A run that claimed the key after the build describes the built tree.
            Logging.info "test-prune" "  ↳ tests slot already held — queueing re-run"
            enqueueImpactRun ctx
            state
    | _ -> state

/// Whether the index names no test project beyond those a run here executes or
/// declares excluded, so that a run of every runnable project leaves nothing owed
/// elsewhere. An index that cannot be scanned may name one.
let internal indexNamesOnlyRunnableIn
    (queries: ImpactQueries)
    (runnableProjects: Set<string>)
    (debtScope: Lazy<Map<string, string>> -> (string -> Set<string>) -> string -> Set<string>)
    (excluded: Lazy<Map<string, string>>)
    =
    match queries.IndexedTestProjects() with
    | Some indexed -> Set.isSubset (debtScope excluded (fun _ -> indexed) "") runnableProjects
    | None -> false

let internal requestTestRunWith
    (commandForceRun:
        PluginCtx<TestPruneMsg>
            -> TestMode
            -> TestConfig list
            -> string option
            -> Tasks.TaskCompletionSource<string>
            -> Async<TestPruneMsg>)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    configs
    filter
    reply
    =
    let workOf runCtx =
        PluginWork.cooperativeSafe (commandForceRun runCtx state.Mode configs filter reply)

    match runTestHostExclusive ctx Set.empty (Some reply) workOf with
    | Claimed ->
        { state with
            Receipt = HeldReceipt.Revoked "a manual test run replaced it"
            InFlight =
                Some
                    { Scope = LaunchedSelection
                      Mode = state.Mode
                      WatcherReported = Set.empty } }
    | SlotBusy ->
        // A busy key QUEUES the run, never refuses it: a refusal that reads as
        // success is a vacuous green. The intent waits behind the holder, owned, and
        // the IPC command bounds its own wait on `reply`.
        ctx.Log "  ↳ queued run-tests force-run (tests already running)"

        ctx.EnqueueExclusiveIntent "tests" None (RunTestsRequested(configs, filter, reply))
        |> ignore

        state

/// Launch the run a session that launches only requested runs held back, now that a
/// command has asked for it. `state` is already armed (`TestMode.armOwedRun`). Flushed
/// first, like a build's launch, so changes folded since the hold are selected; the
/// launch then applies every widening a build's would — the missing baseline, the
/// evidence gap, quarantined reds.
let internal launchRequestedWith
    (flushAndQueryAffectedBounded: StateStep)
    (launchImpactRun: ImpactLauncher)
    (testConfigs: TestConfig list option)
    (tryRepairSchemaDrift: exn -> unit)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    =
    match testConfigs with
    | Some configs when not configs.IsEmpty ->
        match
            (try
                Ok(flushAndQueryAffectedBounded ctx state)
             with ex ->
                 Error ex)
        with
        | Error ex ->
            Logging.error "test-prune" $"flushAndQueryAffected (requested run) failed: %s{ex.Message}"
            tryRepairSchemaDrift ex

            ctx.ReportStatus(
                PluginStatus.failedNow ex.Message $"requested run flush failed: %s{ex.Message}" TimeSpan.Zero
            )

            state
        | Ok flushed ->
            Logging.info "test-prune" "A command asked for the owed test run: launching the run this session held"
            let fanout = flushed.PendingForceRunProjects

            let launchState =
                { flushed with
                    PendingForceRunProjects = Set.empty
                    OwedRunHeld = false }

            match launchImpactRun ctx configs launchState flushed.LastResults.IsSome fanout with
            | Some launched -> launched
            | None when joinsFullRun flushed false -> attachToFullRun { flushed with OwedRunHeld = false }
            | None ->
                enqueueImpactRun ctx
                { flushed with OwedRunHeld = false }
    | _ -> state

/// The owed run an `ImpactRunRequested` intent stands for, decided against the state it
/// is delivered into. Flush first, so changes that landed while the previous run held
/// the key are selected; then launch only if something is still owed. An intent is
/// queued while a run is in flight, and that run may have discharged exactly the debt
/// that queued it: launching blindly would produce a zero-project run whose result
/// erases the passing evidence.
///
/// Stale needs the evidence to cover the tree as well. A run that executed nothing
/// (every client abandoned it, its artifacts or test host were unavailable) discharged
/// nothing, and the debt alone cannot show that: a tree moved only by an edit the
/// symbol diff sees as changing no symbol owes no symbol, yet has no evidence. The
/// rerun queued behind such a run is what verifies it.
let internal impactRunWith
    (flushAndQueryAffectedBounded: StateStep)
    (launchImpactRun: ImpactLauncher)
    (logRedStands: string -> TestPruneState -> unit)
    (nothingOwed: VerificationDebt -> bool)
    (onlyRetriesRed: TestPruneState -> bool)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (tryRepairSchemaDrift: exn -> unit)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    =
    match testConfigs with
    | Some configs when not configs.IsEmpty ->
        match
            (try
                Ok(flushAndQueryAffectedBounded ctx state)
             with ex ->
                 Error ex)
        with
        | Error ex ->
            Logging.error "test-prune" $"flushAndQueryAffected (rerun) failed: %s{ex.Message}"
            tryRepairSchemaDrift ex
            ctx.ReportStatus(PluginStatus.failedNow ex.Message $"rerun flush failed: %s{ex.Message}" TimeSpan.Zero)
            state
        | Ok rerunState when
            nothingOwed rerunState.Debt
            && Set.isEmpty rerunState.PendingForceRunProjects
            && ReceiptInputTree.matches
                (rerunState.EvidenceReceipt |> Option.bind (fun receipt -> receipt.InputTreeHash))
                (ReceiptInputTree.read repoRoot)
            ->
            Logging.info
                "test-prune"
                "Queued impact rerun is stale — nothing is owed, no dependency fanout remains, and the evidence covers the current tree"

            rerunState
        | Ok rerunState when onlyRetriesRed rerunState ->
            logRedStands "Queued impact rerun" rerunState
            rerunState
        | Ok rerunState ->
            Logging.info "test-prune" "Re-running tests (queued during previous run)"
            let fanout = rerunState.PendingForceRunProjects

            let launchState =
                { rerunState with
                    PendingForceRunProjects = Set.empty }

            match launchImpactRun ctx configs launchState rerunState.LastResults.IsSome fanout with
            | Some launched -> launched
            | None when joinsFullRun rerunState false -> attachToFullRun rerunState
            | None ->
                enqueueImpactRun ctx
                rerunState
    | _ -> state

/// Discharge what a BootScan cohort owes when the last completed run already covered
/// it: that run passed every runnable project in full over the tree and model the
/// cohort sealed on, every file the cohort checked is part of that tree, and the
/// index names no coverer it did not run. A cold daemon's build launches that run
/// before the scan has checked the tree, so the cohort usually seals after the run's
/// result has folded, with no run in flight to attach to (`joinsFullRun`). Launching
/// for it would run the tree's impact a second time.
/// An in-session cohort is an edit, and debt the covering run cannot speak for (an
/// unreadable ledger, a missing baseline, a dependency force-run) keeps its launch.
let internal dischargeCoveredBootScanWith
    (baselineInvalidReason: VerificationDebt -> string option)
    (indexNamesOnlyRunnable: Lazy<Map<string, string>> -> bool)
    (owedDescription: VerificationDebt -> string)
    (repoRoot: string)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (ctx: PluginCtx<TestPruneMsg>)
    (batch: BatchChecked)
    (state: TestPruneState)
    =
    // A file outside the hashed tree can change without moving its identity.
    let inTree (tree: ReceiptTree) =
        let hashed = tree.Entries |> List.map fst |> Set.ofList

        batch.Files
        |> List.forall (fun file ->
            hashed.Contains(Path.GetRelativePath(repoRoot, AbsFilePath.value file).Replace('\\', '/')))

    // An idle boot scan that owes debt the covering run can speak for.
    let bootScanOwesCoverableDebt () =
        batch.Trigger = BootScan
        && state.InFlight.IsNone
        && not (Set.isEmpty state.Debt.PendingQueue && Map.isEmpty state.Debt.RuntimeObligations)
        && not state.Debt.RecoveryOutstanding
        && Option.isNone (baselineInvalidReason state.Debt)
        && Set.isEmpty state.PendingForceRunProjects

    // The covering run passed this very tree, under the current model.
    let coveredBy (tree: ReceiptTree) generation =
        ReceiptInputTree.matches (Some tree.Identity) state.SealedTree
        && inTree tree
        && observeModelGeneration ctx = generation
        && indexNamesOnlyRunnable (lazy (resolveExcludedProjects ()))

    match state.CoveredFullSuite with
    | Some(tree, generation) when bootScanOwesCoverableDebt () && coveredBy tree generation ->
        Logging.info
            "test-prune"
            $"BatchChecked: %s{owedDescription state.Debt} discovered by BootScan over the tree the last full-suite run passed — covered by that run, no re-run"

        // Shed what a covering completion sheds: the queue, and the in-memory view
        // the next selection reads from, so a later edit selects only itself.
        { state with
            Debt =
                { commitPending state.Debt.PendingQueue state.Debt with
                    RuntimeObligations = Map.empty }
            ChangedFiles = []
            ChangedSymbols = []
            AffectedTests = Analyzed [] }
    | _ -> state
