module FsHotWatch.TestPrune.TestPrunePlugin

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

/// Create a TestPrune plugin handler using the declarative plugin framework.
/// `buildExtensions` receives the plugin's own `Database` so extensions that
/// need a `RouteStore`/`SymbolStore` derive it from the same DB the plugin
/// queries against — structurally prevents the caller from wiring an extension
/// to a different DB than the plugin's. `queriesOf` builds the impact queries over that
/// same DB.
let internal createWithEvidenceAndQueries
    // The box-wide test-evidence store this plugin reuses from and publishes to;
    // `None` (the kill switch, or no store) runs exactly as without one.
    (evidence: TestEvidenceRun.Wiring option)
    (queriesOf: Database -> ImpactQueries)
    (launchDeadline: TimeSpan)
    // `tests.maxParallelProjects`: at most this many test groups in flight; `None` for all.
    (maxParallelProjects: int option)
    // The declared exclusions: indexed test-project name -> written reason. Called once
    // per debt classification (flush, launch, completion), so a caller can re-resolve
    // them against the current project inventory. A throw is a refusal to classify, and
    // leaves the debt owed.
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    // The edges the impact walk follows (`tests.selection`). `WalkPolicy.FollowAll`
    // follows every edge.
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    // `dependsOn`: repo-root-relative globs naming EXTERNAL inputs (DB
    // migrations, generated files, schemas) that the symbol-diff cache key can't
    // see. Their content hash salts the BuildCompleted cache key so editing one
    // forces a genuine re-run instead of replaying a stale verdict. `[]` → no
    // salt (key byte-identical to the pre-feature key).
    (dependsOn: string list)
    // `tests.traces`: `None` records nothing and launches every project as configured.
    (traces: TraceWiring option)
    // The mode the session starts in: `TestMode.initial` for a daemon,
    // `TestMode.startingWith false` for a host that runs only what a command requests.
    (initialMode: TestMode)
    =
    /// Which repo-relative paths a build consumed, under the model the host publishes when
    /// asked — so `executeTests` observes it after `beforeRun`, not at launch.
    let compileInputsOf (ctx: PluginCtx<TestPruneMsg>) () : string -> bool =
        ReceiptInputTree.feedsCompile repoRoot (ctx.ProjectGraph.ObserveCheckableFiles() |> Option.map snd)

    /// The traces of a run launched under `mode`, when `tests.traces` is configured, with
    /// the plugin's activity log and subtasks: a force run's `executeTests` has no `ctx`.
    let tracesFor (ctx: PluginCtx<TestPruneMsg>) (mode: TestMode) : TraceRunHost option =
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

    let db = openOwnIndex dbPath walkPolicy
    let busyTimeout = dbBusyTimeout db
    let queries = queriesOf db
    let configuredTestProjects = testConfigs |> Option.defaultValue []



    // A recreated DB (schema bump) leaves the FCS check cache stale — see
    // `clearFcsCheckCache`.
    if db.WasRecreated then
        try
            let cleared = clearFcsCheckCache repoRoot

            Logging.warn
                "test-prune"
                $"TestPrune DB was recreated (schema change) — cleared %d{cleared} FCS check-cache entries so every file re-indexes on this scan."
        with ex ->
            Logging.error "test-prune" $"failed to clear the FCS check cache after a DB recreate: %s{ex.Message}"

    let extensions = buildExtensions |> Option.map (fun f -> f db)

    let tryRepairSchemaDrift ex = tryRepairSchemaDrift dbPath ex

    let indexedKey = indexedKeyIn db

    let mayReplay (pending: Map<string, string>) (result: FileCheckResult) : bool =
        testConfigs |> Option.forall List.isEmpty
        || (let relPath =
                Path.GetRelativePath(repoRoot, AbsFilePath.value result.File).Replace('\\', '/')

            IndexedAnalysis.identityOf repoRoot result
            |> Option.exists (IndexedAnalysis.mayReplay (indexedKey relPath) (Map.tryFind relPath pending)))

    // Durable "needs-testing" queue (plugin-owned sidecar). The set of changed
    // symbols not yet proven test-equivalent to the last green run. Loaded once
    // at construction so a restart with a non-empty queue re-flags those
    // symbols. The live queue is `state.Debt.PendingQueue`; this file is its durable
    // copy, written by `PrepareCommit`. A symbol leaves ONLY when a covering test
    // run passed (or it has no covering test). See PendingVerification.fs.
    let loadedQueue = PendingVerification.load repoRoot

    /// `Some reason` when the sidecar EXISTS but could not be read
    /// (a torn write, corrupt JSON, a non-string entry). What was owed is then
    /// UNKNOWN — which is NOT the same fact as "nothing is owed", and must never
    /// again be spelled with the same value. A MISSING file is not this: it is a
    /// provable `Loaded empty` (fresh clone), and stays a fast no-op.
    let ledgerUnreadableReason =
        match loadedQueue with
        | PendingVerification.LoadedQueue.Loaded _ -> None
        | PendingVerification.LoadedQueue.Unreadable reason -> Some reason

    /// Present while a durable debt publication is between its first write and its
    /// publication. A restart that finds it cannot tell which sidecars describe the
    /// published state, so what is owed is unknown. Only `Finalize` removes it, and only
    /// for a state that is no longer unknown: a session that ends with the debt still
    /// unknown has written nothing, so the marker it found still describes the sidecars.
    let debtPublicationPath =
        Path.Combine(FsHwPaths.root repoRoot, "test-prune", "owner-publication-pending")

    let interruptedPublication = File.Exists debtPublicationPath

    if interruptedPublication then
        Logging.warn
            "test-prune"
            $"a verification-debt publication did not finish (%s{debtPublicationPath}), and no full suite has passed every configured project since. The sidecars may describe a state that was never published, so what is owed is UNKNOWN. The marker stays, and every test run, in this daemon and the next, is widened to every configured project in full until a full suite passes every configured project."

    /// The reds carried in from the previous session — quarantined into
    /// the first run exactly as in-session reds are. An UNREADABLE file is debt of unknown
    /// membership and takes the unreadable-ledger road: widen to the full suite, which
    /// re-executes every test and rebuilds the list from evidence.
    let loadedFailures, failuresUnreadableReason =
        match OutstandingFailure.load repoRoot with
        | OutstandingFailure.LoadedFailures.Loaded failures -> failures, None
        | OutstandingFailure.LoadedFailures.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the outstanding-failures ledger (%s{OutstandingFailure.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. It records every test still red from earlier runs, so which tests are owed is now UNKNOWN. Every test run is widened to every configured project in full until a full suite has re-executed them all."

            [], Some reason

    /// The full-suite watermark this ledger's impact-filtered greens are
    /// relative to. `None` until a full-suite run has accounted for every configured
    /// project; an unreadable file is the same recovery, said out loud.
    let loadedBaseline =
        match FullSuiteBaseline.load repoRoot with
        | FullSuiteBaseline.LoadedBaseline.Loaded baseline -> baseline
        | FullSuiteBaseline.LoadedBaseline.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the full-suite baseline (%s{FullSuiteBaseline.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. The next test run is widened to the full suite to earn a new one."

            None

    /// Whole-project coverage the previous daemon recorded. Unreadable is no record: the
    /// projects it might have named run whole again, which costs time and nothing else.
    let restoredEvidence =
        match WholeProjectEvidence.load repoRoot with
        | WholeProjectEvidence.LoadedRecord.Loaded record ->
            if not (Map.isEmpty record) then
                let names = record |> Map.keys |> String.concat ", "

                Logging.info
                    "test-prune"
                    $"Whole-project evidence recorded by an earlier daemon for %d{Map.count record} project(s) (%s{names}): each stands while its compile inputs are the ones recorded"

            EarnedEvidence.restored record
        | WholeProjectEvidence.LoadedRecord.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the whole-project evidence record (%s{WholeProjectEvidence.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. Every test project runs in full until this daemon has seen it run whole."

            None

    let loadedRuntimeObligations, runtimeObligationsUnreadableReason =
        match loadRuntimeCoverageObligations repoRoot with
        | Ok obligations -> obligations, None
        | Error reason ->
            Logging.warn
                "test-prune"
                $"the runtime coverage obligation ledger could not be read: %s{reason}. Widening to the full suite until a verified full run recovers the unknown debt."

            Map.empty, Some reason

    /// Projects an earlier daemon owed a whole-project run because their runtime coverage did
    /// not ingest. A record that cannot be read leaves which projects are owed unknown.
    let loadedNotIngested, notIngestedUnreadableReason =
        match CoverageNotIngested.load repoRoot with
        | CoverageNotIngested.LoadedRecord.Loaded record ->
            if not (Map.isEmpty record) then
                let owed =
                    record
                    |> Map.toList
                    |> List.map (fun (project, reason) -> $"%s{project} (%s{reason})")
                    |> String.concat ", "

                Logging.warn
                    "test-prune"
                    $"runtime coverage did not ingest for %d{Map.count record} project(s): %s{owed}. Each runs in full until a run of it ingests."

            record, None
        | CoverageNotIngested.LoadedRecord.Unreadable reason ->
            Logging.warn
                "test-prune"
                $"the record of runtime coverage that did not ingest (%s{CoverageNotIngested.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. Which projects are owed is UNKNOWN; widening to the full suite until a full suite passes."

            Map.empty, Some reason

    let coverageIngestFailed = coverageIngestFailedIn repoRoot

    /// How many CONSECUTIVE flush cycles each currently-queued symbol
    /// has seeded, so a symbol that is pinned AND selecting wide can be named out loud
    /// (see `isPoisonSuspect`).
    ///
    /// In-memory and per-session on purpose. Persisting it needs either a second sidecar
    /// or a shape change to `pending-verification.json` — and that file's reader treats
    /// ANY unparseable content as unknown debt that widens every run to the full suite,
    /// so a format change hands every existing checkout one gratuitous
    /// full-suite recovery. Forgetting on restart costs three cycles of re-arming.
    ///
    /// A diagnostic only: no selection, cache or debt decision reads it.
    let mutable pendingAgeRef: Map<string, int> = Map.empty

    // Say it out loud — a silent recovery here reads as a green.
    match ledgerUnreadableReason with
    | Some reason ->
        Logging.warn
            "test-prune"
            $"the pending-verification ledger (%s{PendingVerification.sidecarPath repoRoot}) EXISTS but could not be read: %s{reason}. It records every symbol still awaiting a green test run, so what is owed is now UNKNOWN — which is NOT the same as nothing owed. Until a FULL-SUITE run passes, every test run is widened to every configured project in full and no cached verdict may be replayed."
    | None -> ()

    /// The test projects this daemon can actually RUN — i.e. the ones in
    /// `testConfigs`. Empty when the plugin is analysis-only.
    ///
    /// The symbol DB indexes test methods from EVERY test project it analyzed, which is
    /// not the same set. A symbol covered by a project outside this set is NOT covered by
    /// a test we can run, and it is not thereby verified either: see `debtScope` for the
    /// only way such a project stops owing.
    let runnableProjects: Set<string> =
        match testConfigs with
        | Some configs -> configs |> List.map (fun c -> c.Project) |> Set.ofList
        | None -> Set.empty


    let baselineInvalidReason = baselineInvalidReasonIn runnableProjects

    let nothingOwed = nothingOwedUnder baselineInvalidReason

    /// What a drain is FOR, in words. Unknown debt cannot be counted, so it is named.
    let owedDescription (debt: VerificationDebt) =
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

    let persistQueueAdditions = persistQueueAdditionsIn repoRoot




    let expectedRuntimeCoverageProjects =
        match testConfigs, coveragePaths with
        | Some configs, Some pathsForProject ->
            configs
            |> List.choose (fun config ->
                if pathsForProject config.Project |> Option.isSome then
                    Some config.Project
                else
                    None)
        | _ -> []

    let allowedRuntimeProjects = Set.ofList expectedRuntimeCoverageProjects

    let loadedDebt =
        { PendingQueue =
            match loadedQueue with
            | PendingVerification.LoadedQueue.Loaded queue -> queue
            // The owed symbols cannot be NAMED, so none are seeded; `UnknownDebt` carries
            // the debt instead and widens every run until a full suite proves the tree.
            // Seeding `empty` is safe ONLY because that cause is recorded.
            | PendingVerification.LoadedQueue.Unreadable _ -> PendingVerification.empty
          SymbolRevisions = Map.empty
          Revision = 0L
          UnknownDebt =
            [ match ledgerUnreadableReason with
              | Some reason ->
                  $"the pending-verification ledger (%s{PendingVerification.sidecarPath repoRoot}) could not be read: %s{reason}"
              | None -> ()
              match failuresUnreadableReason with
              | Some reason ->
                  $"the outstanding-failures ledger (%s{OutstandingFailure.sidecarPath repoRoot}) could not be read: %s{reason}"
              | None -> ()
              match runtimeObligationsUnreadableReason with
              | Some reason ->
                  $"the runtime coverage obligation ledger (%s{runtimeCoverageObligationsPath repoRoot}) could not be trusted: %s{reason}"
              | None -> ()
              match notIngestedUnreadableReason with
              | Some reason ->
                  $"the record of runtime coverage that did not ingest (%s{CoverageNotIngested.sidecarPath repoRoot}) could not be read: %s{reason}"
              | None -> ()
              if interruptedPublication then
                  $"a verification-debt publication did not finish (%s{debtPublicationPath}), and no full suite has passed since to recover it" ]
          Baseline = loadedBaseline
          RuntimeObligations = pruneRuntimeCoverageObligations allowedRuntimeProjects loadedRuntimeObligations
          CoverageNotIngested = loadedNotIngested }

    // A project that no longer produces runtime coverage cannot retire an obligation, so
    // its obligations are pruned at load. Written here, before any event, because no
    // publication will otherwise carry an unchanged debt to disk.
    if
        not loadedDebt.RecoveryOutstanding
        && loadedDebt.RuntimeObligations <> loadedRuntimeObligations
    then
        match
            persistRuntimeCoverageObligationsWith
                (fun () ->
                    FsHwPaths.atomicWriteAllText
                        (runtimeCoverageRecoveryPath repoRoot)
                        "runtime obligation write in progress")
                (fun () -> saveRuntimeCoverageObligations repoRoot loadedDebt.RuntimeObligations)
                (fun () -> File.Delete(runtimeCoverageRecoveryPath repoRoot))
        with
        | Ok() -> ()
        | Error ex ->
            Logging.warn
                "test-prune"
                $"failed to persist pruned runtime coverage obligations: %s{ex.Message}; the next publication writes them"

    let runtimeCoverageSelection changedFiles =
        selectByRuntimeCoverage
            db
            expectedRuntimeCoverageProjects
            changedFiles
            (DateTimeOffset.UtcNow - RuntimeCoverageMaxAge)

    let debtScope = debtScopeIn runnableProjects

    /// Whether the index names no test project beyond those a run here executes or
    /// declares excluded, so that a run of every runnable project leaves nothing owed
    /// elsewhere. An index that cannot be scanned may name one.
    let indexNamesOnlyRunnable (excluded: Lazy<Map<string, string>>) =
        match queries.IndexedTestProjects() with
        | Some indexed -> Set.isSubset (debtScope excluded (fun _ -> indexed) "") runnableProjects
        | None -> false

    /// `debtScope` resolving the declarations only if a classification needs them.
    let lazyDebtScope (covering: string -> Set<string>) =
        debtScope (lazy (resolveExcludedProjects ())) covering

    let coveringOf = coveringOfIn queries

    let owedElsewhere = owedElsewhereIn runnableProjects

    // This process's extension refresh (`ExtensionRefreshPhase`). The refresh a restart owes
    // before anything this process indexed (AUTOMATION-1441) runs beside the fold: its
    // answer, and how long it took. A restart's first flush is
    // the `BuildCompleted` its scan dispatches before the checks; on a 2,674-file
    // repository the refresh took 6 s, and every check result queued behind the build
    // waited for it. A `FileChecked` reads neither the extensions' edges nor their failures,
    // so it folds while the refresh runs. Every other event, a selection that queries the
    // index, and a run launched in the meantime take its answer first (`joinRefresh`). It
    // reads what the fold's own refresh would have: no flush writes the index until the
    // answer is taken.
    //
    // A ref rather than state the fold returns: a flush that throws returns the state from
    // before it, and a refresh it already started must still be joined, once.
    let extensionRefresh = ref ExtensionRefreshPhase.NeverRefreshed



    let refreshTimed = refreshTimedIn db repoRoot

    /// Take the answer of the refresh running beside the fold, if one is, into `state`.
    let joinRefresh (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        match extensionRefresh.Value with
        | ExtensionRefreshPhase.NeverRefreshed
        | ExtensionRefreshPhase.Refreshed -> state
        | ExtensionRefreshPhase.RunningBesideFold refresh ->
            extensionRefresh.Value <- ExtensionRefreshPhase.Refreshed

            let answer =
                use _ = ctx.DeclareBoundedWork "extension refresh" ImpactSelectionDeadline
                refresh.GetAwaiter().GetResult()

            applyRefresh ctx answer state

    // Flush pending analysis to DB and merge the runtime obligations it names.
    // Extensions (if any) then replace their COMPLETE edge sets via
    // `refreshExtensionEdges` — after the AST results are written, so they resolve
    // against current symbols, and before QueryAffectedTests, so their edges
    // participate in impact traversal. They re-run only when this flush indexed
    // something or a refresh is owed: their answer is a function of the tree.
    let flushEnv: FlushEnv =
        { Db = db
          DbPath = dbPath
          BusyTimeout = busyTimeout
          EnqueuePending = enqueuePending
          PersistQueueAdditions = persistQueueAdditions
          RuntimeCoverageSelection = runtimeCoverageSelection
          Refresh =
            { Phase = extensionRefresh
              Extensions = extensions
              Apply = applyRefresh
              Timed = refreshTimed
              Join = joinRefresh } }

    let flushPending ctx state = flushPendingWith flushEnv ctx state

    /// The impact selection over a flushed state, and the classification of every queued
    /// symbol by the projects covering it.
    let selectionEnv: SelectionEnv =
        { Db = db
          Queries = queries
          RunnableProjects = runnableProjects
          CoveringOf = coveringOf
          DebtScope = lazyDebtScope
          CommitPending = commitPending
          SeedAges = fun () -> Volatile.Read(&pendingAgeRef) }

    let selectAffected (flushedState: TestPruneState) (runtimeObligations: RuntimeCoverageObligations) =
        selectAffectedWith selectionEnv flushedState runtimeObligations

    /// Flush, then select, unless the mode skips `FlushSelection`: pass-through records
    /// the debt without selecting or classifying it, and only a full run's green
    /// discharges it.
    let flushAndQueryAffected (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
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
    let flushAndQueryAffectedBounded (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
        use _declaration = ctx.DeclareBoundedWork "impact selection" ImpactSelectionDeadline
        flushAndQueryAffected ctx state

    // Per-file FCS freshness sidecar, loaded once at plugin construction from
    // `.fshw/test-prune/file-freshness.json` and updated incrementally on each
    // FileChecked. Survives daemon restarts so a cross-restart replay can decide which
    // files' stored symbols are trustworthy enough to run detectChanges against. Held with
    // the project model generation it was last reconciled against, as one value.
    //
    // Closure-local mutable cell + Volatile: only the Update handler reads and writes it.
    let mutable freshness: FreshnessView =
        { Store = FileFreshness.load repoRoot
          ReconciledAt = None }

    /// The sidecar as it stands.
    let freshnessStore () = (Volatile.Read(&freshness)).Store

    /// The clock `storedRowsExist: bool` did not have. Rows written
    /// seconds ago by this session's own scan are not a baseline; diffing the current
    /// extraction against them compares the file to itself, always reports "nothing
    /// changed", and selects zero test projects — on a diff that ADDS tests. See
    /// `FileFreshness.PriorRowLedger`, which owns the rule.
    let priorRows = FileFreshness.PriorRowLedger()

    let updateFreshness (newStore: FileFreshness.Store) =
        Volatile.Write(
            &freshness,
            { Volatile.Read(&freshness) with
                Store = newStore }
        )

        try
            FileFreshness.save repoRoot newStore
        with ex ->
            Logging.warn
                "test-prune"
                $"failed to persist file-freshness sidecar: %s{ex.Message}; in-memory state still updated"

    // A file deleted together with its compile entry is unregistered by the re-evaluation
    // that publishes the next model, so it gets no FileChecked and no `stamp` forgets it;
    // the model moving is the event that says such a file may be gone.
    let reconcileFreshness (modelGeneration: int64 option) =
        let current = Volatile.Read(&freshness)

        match modelGeneration with
        | Some generation when modelGeneration <> current.ReconciledAt ->
            Volatile.Write(
                &freshness,
                { current with
                    ReconciledAt = modelGeneration }
            )

            let kept, dropped = FileFreshness.dropGone repoRoot current.Store

            if not dropped.IsEmpty then
                for relPath in dropped do
                    Logging.info
                        "test-prune"
                        $"%s{relPath} no longer exists; dropping its freshness record (project model generation %d{generation})"

                updateFreshness kept
        | _ -> ()

    // Seed the in-memory hot view from the durable queue so a restart with a non-empty
    // queue re-flags those symbols. Without this, a restart diffs current symbols against
    // the already-advanced analysis snapshot → "nothing changed" → zero tests run → false
    // green.
    let initialState =
        { Debt = loadedDebt
          Mode = initialMode
          InFlight = None
          LastRed = None
          SealedTree = None
          AwaitingDiff = None
          OwedRunHeld = false
          CompletedRuns = []
          CheckReaches = []
          Replies = []
          PendingAnalysis = Map.empty
          AnalysisModelGeneration = None
          AnalysisFiles = Map.empty
          CohortSeal = None
          AnalysisReceipt = None
          SymbolSnapshot = Map.empty
          AffectedTests = NotYetAnalyzed
          ChangedSymbols = loadedDebt.PendingQueue |> Set.toList
          ChangedFiles = []
          LastResults = None
          LastSeeds = []
          LastSeedFanout = SeedFanoutReading.NoSelection
          DebtDuringFullRun = Map.empty
          CoveredFullSuite = None
          TestClassFiles = Map.empty
          BuildCompletedInThisSession = false
          PriorProjectFingerprints = Map.empty
          PendingForceRunProjects = Set.empty
          ChangedSymbolsAllUncovered = UncoveredChanges.No
          UnanalyzableFiles = Map.empty
          NotSelected = Map.empty
          PendingIdentities = Map.empty
          Unindexed = Set.empty
          IndexGuard = IndexedAnalysis.Guard mayReplay
          FailedExtensions = Map.empty
          // The previous session's reds, quarantined into the first run.
          OutstandingFailures = loadedFailures
          LastCoverage = RunCoverage.none
          LastZeroSelection = ZeroSelection.NotAZero
          Receipt = HeldReceipt.NoneYet
          Earned = None
          RestoredEvidence = restoredEvidence }


    let observeTestInputs = observeTestInputsOf testConfigs


    /// The outcome-store key of each configured project's full run over the tree as it is now
    /// (ADR-059 step 7b), for the `test-scope` reply. Empty without a store or without the
    /// model's compile-input identities: nothing can be keyed then.
    let currentOutcomeKeys (graph: ProjectGraphAccessor) : Map<string, string> =
        match evidence, ObservedModel.inputs graph with
        | Some wiring, Some identities ->
            TestEvidenceRun.currentOutcomeKeys
                wiring
                repoRoot
                (fun fsproj -> Map.tryFind fsproj identities)
                (evidenceCandidates graph (defaultArg testConfigs []))
        | _ -> Map.empty

    /// The test-evidence plan of a run about to launch `configs`: every project the model
    /// can key, and a lookup for each one `launching` admits that has no outstanding
    /// failure, when `lookups` allows any. `None` without a store, or without the
    /// model's compile-input identities (nothing can be keyed without them).
    let evidencePlanFor
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



    let logSelectedBy = logSelectedByIn repoRoot

    /// Returns the `TestsFinished` message the framework's RunExclusive posts back to the
    /// agent; the synchronous `Custom(TestsFinished)` handler emits `TestRunCompleted`
    /// inside the cache-write capture window. `TestRunStarted` fires before the host starts.
    /// Catches its own exceptions to produce an `Aborted` lifecycle — letting RunExclusive
    /// eat the message would free the slot with no completion posted, stranding
    /// `LastResults` and the debt the run launched against.
    let runEnv: RunEnv =
        { Config =
            { RepoRoot = repoRoot
              ConfiguredTestProjects = configuredTestProjects
              RunnableProjects = runnableProjects
              LaunchDeadline = launchDeadline
              MaxParallelProjects = maxParallelProjects
              BeforeRun = beforeRun
              AfterRun = afterRun
              CoveragePaths = coveragePaths
              Traces = traces }
          Db = db
          Queries = queries
          Debt =
            { CoveringOf = coveringOf
              DebtScope = lazyDebtScope
              NothingOwed = nothingOwed
              RevisionOf = revisionOf
              BaselineInvalidReason = baselineInvalidReason }
          Launch =
            { AwaitDiscovery = awaitDiscoveryBeforeLaunch
              CompileInputsOf = compileInputsOf
              CoverageIngestFailed = coverageIngestFailed
              EvidencePlanFor = evidencePlanFor
              LogLaunch = logLaunch
              LogSelectedBy = logSelectedBy
              ObserveModelGeneration = observeModelGeneration
              ObserveTestInputs = observeTestInputs
              TracesFor = tracesFor }
          BumpSeedAges =
            fun symbols -> Volatile.Write(&pendingAgeRef, bumpSeedAges (Volatile.Read(&pendingAgeRef)) symbols) }

    let runTestsWithImpact ctx configs inputs hasCachedResults fanoutProjects =
        runTestsWithImpactWith runEnv ctx configs inputs hasCachedResults fanoutProjects


    let commandForceRun ctx mode configs filter reply =
        commandForceRunWith runEnv ctx mode configs filter reply

    let allCommands =
        pluginCommands
            { RepoRoot = repoRoot
              TestConfigs = testConfigs
              RunnableProjects = runnableProjects
              CurrentOutcomeKeys = currentOutcomeKeys
              BaselineInvalidReason = baselineInvalidReason
              Queries = queries }



    let launchScopeOf = launchScopeUnder baselineInvalidReason

    let impactLaunchEnv: ImpactLaunchEnv =
        { RefreshPhase = extensionRefresh
          FailuresAfter = failuresAfter
          LaunchScopeOf = launchScopeOf
          RunTestsWithImpact = runTestsWithImpact }

    let launchImpactRun ctx configs launchState hasCachedResults fanout =
        launchImpactRunWith impactLaunchEnv ctx configs launchState hasCachedResults fanout


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
    let onlyRetriesRed (state: TestPruneState) =
        match state.LastRed with
        | Some red ->
            not (TestMode.requestsFullSuite state.Mode)
            && Set.isEmpty state.PendingForceRunProjects
            && Set.union state.Debt.PendingQueue (Set.ofList state.ChangedSymbols)
               |> Set.forall (fun symbol -> Map.tryFind symbol red.SymbolRevisions = Some(revisionOf state.Debt symbol))
            && ReceiptInputTree.matches (Some red.Tree) (ReceiptInputTree.read repoRoot)
        | None -> false

    /// Say why an owed launch was not made (`onlyRetriesRed`).
    let logRedStands (trigger: string) (state: TestPruneState) =
        Logging.info
            "test-prune"
            $"%s{trigger}: %s{owedDescription state.Debt} — all of it was in the run that concluded red over this same tree, so a launch would only retry it; that result stands until the tree or what is owed moves (AUTOMATION-1373)"

    /// Discharge what a BootScan cohort owes when the last completed run already covered
    /// it: that run passed every runnable project in full over the tree and model the
    /// cohort sealed on, every file the cohort checked is part of that tree, and the
    /// index names no coverer it did not run. A cold daemon's build launches that run
    /// before the scan has checked the tree, so the cohort usually seals after the run's
    /// result has folded, with no run in flight to attach to (`joinsFullRun`). Launching
    /// for it would run the tree's impact a second time.
    /// An in-session cohort is an edit, and debt the covering run cannot speak for (an
    /// unreadable ledger, a missing baseline, a dependency force-run) keeps its launch.
    let dischargeCoveredBootScan (ctx: PluginCtx<TestPruneMsg>) (batch: BatchChecked) (state: TestPruneState) =
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


    let endRun = endRunFrom initialMode

    /// A launch that found its artifacts or test host unavailable ran nothing: it revokes
    /// the receipt, hands back the fanout it consumed, and fails.
    let unavailableRun
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

    // Launched from the mailbox so it is serialised with every other launch site and
    // holds the "tests" key for its whole duration — see the `RunTestsRequested` case
    // for why that matters.
    //
    // Cooperative-safe: a force-run exists only for the client that asked for it, so the
    // framework may cancel it once that client is gone. Its test hosts run in its own
    // process scope (reaped), its result publishes only through the fold it returns
    // (never folded when cancelled), and its `finally` closes the run it opened.
    let requestTestRun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) configs filter reply =
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
    let impactRun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
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

    /// Launch the run a session that launches only requested runs held back, now that a
    /// command has asked for it. `state` is already armed (`TestMode.armOwedRun`). Flushed
    /// first, like a build's launch, so changes folded since the hold are selected; the
    /// launch then applies every widening a build's would — the missing baseline, the
    /// evidence gap, quarantined reds.
    let launchRequested (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
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


    /// Launch the run a build held back (`holdForDiff`), selected from the state it is
    /// delivered into, where the diff has now folded, or the wait expired without it.
    let launchHeld (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) (why: string) =
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

    let prepareCommit = prepareCommitIn repoRoot debtPublicationPath

    let runFinishedEnv: RunFinishedEnv =
        { RepoRoot = repoRoot
          RunnableProjects = runnableProjects
          Traces = traces
          ResolveExcludedProjects = resolveExcludedProjects
          Debt =
            { CommitPending = commitPending
              CoveringOf = coveringOf
              DebtScope = debtScope
              IndexNamesOnlyRunnable = indexNamesOnlyRunnable
              OwedElsewhere = owedElsewhere
              RevisionOf = revisionOf }
          EndRun = endRun
          ObserveModelGeneration = observeModelGeneration }

    let onRunFinished ctx state message started launchedCompletion launchedAs =
        onRunFinishedWith runFinishedEnv ctx state message started launchedCompletion launchedAs

    let launchSteps: LaunchSteps =
        { AttachToFullRun = attachToFullRun
          EnqueueImpactRun = enqueueImpactRun
          FlushAndQueryAffectedBounded = flushAndQueryAffectedBounded
          JoinsFullRun = joinsFullRun
          LaunchImpactRun = launchImpactRun
          NothingOwed = nothingOwed
          TryRepairSchemaDrift = tryRepairSchemaDrift }

    let buildCompletedEnv: BuildCompletedEnv =
        { RepoRoot = repoRoot
          TestConfigs = testConfigs
          Launch = launchSteps
          HoldForDiff = holdForDiff
          JoinRefresh = joinRefresh
          LaunchScopeOf = launchScopeOf
          ObserveModelGeneration = observeModelGeneration }

    let onBuildCompleted ctx state buildResult =
        onBuildCompletedWith buildCompletedEnv ctx state buildResult

    let batchCheckedEnv: BatchCheckedEnv =
        { RepoRoot = repoRoot
          TestConfigs = testConfigs
          RunnableProjects = runnableProjects
          Launch = launchSteps
          DischargeCoveredBootScan = dischargeCoveredBootScan
          LaunchHeld = launchHeld
          LogRedStands = logRedStands
          OwedDescription = owedDescription
          OnlyRetriesRed = onlyRetriesRed }

    let onBatchChecked ctx state modelGeneration batch =
        onBatchCheckedWith batchCheckedEnv ctx state modelGeneration batch

    let fileCheckedEnv: FileCheckedEnv =
        { RepoRoot = repoRoot
          Db = db
          PriorRows = priorRows
          FreshnessStore = freshnessStore
          UpdateFreshness = updateFreshness
          EnqueuePending = enqueuePending }

    let onFileChecked ctx state modelGeneration result =
        onFileCheckedWith fileCheckedEnv ctx state modelGeneration result

    let updateEnv: UpdateEnv =
        { RepoRoot = repoRoot
          TestConfigs = testConfigs
          RunnableProjects = runnableProjects
          IndexedKey = indexedKey
          ObserveModelGeneration = observeModelGeneration
          ReconcileFreshness = reconcileFreshness
          Handlers =
            { OnFileChecked = onFileChecked
              OnBatchChecked = onBatchChecked
              OnBuildCompleted = onBuildCompleted
              OnRunFinished = onRunFinished }
          Steps =
            { EndRun = endRun
              ImpactRun = impactRun
              JoinRefresh = joinRefresh
              LaunchHeld = launchHeld
              LaunchRequested = launchRequested
              RequestTestRun = requestTestRun
              UnavailableRun = unavailableRun } }

    let update ctx state event = updateWith updateEnv ctx state event

    { Name = PluginName.create FsHotWatch.PluginActivity.TestPrunePluginName
      Init = initialState
      Update = update
      PrepareCommit = Some prepareCommit
      Commands = allCommands
      Subscriptions =
        Set.ofList (
            // BatchChecked is the cohort-complete flush signal: it fires after the last
            // FileChecked of a batch and before any subsequent BuildCompleted racing the
            // same change, so by the time the agent processes it every FileChecked update
            // has been folded in to `state.ChangedSymbols`.
            //
            // BuildCompleted is subscribed UNCONDITIONALLY so the freshness-stamp gate
            // works even when the plugin is analysis-only: with no testConfigs the handler
            // still runs `flushAndQueryAffected` (idempotent on empty PendingAnalysis) and
            // skips the test-run path.
            [ SubscribeFileChecked; SubscribeBatchChecked; SubscribeBuildCompleted ]
        )
      CacheKey =
        Some(
            cacheKeyWith
                { RepoRoot = repoRoot
                  RunnableProjects = runnableProjects
                  DependsOn = dependsOn
                  BaselineInvalidReason = baselineInvalidReason }
        )
      Teardown = None }

/// `createWithQueries` over the plugin's own index.
/// The test-evidence wiring a daemon gets for `repoRoot`: the repository's box-wide store,
/// its projects' `evidence` blocks, this process's toolchain and the daemon's `exclude`.
/// `None` when `FSHW_TEST_EVIDENCE=0`, or when the config cannot be read (loudly).
let internal evidenceWiringWhen
    (enabled: bool)
    (excludePatterns: string list)
    (repoRoot: string)
    : TestEvidenceRun.Wiring option =
    if not enabled then
        Logging.info "test-prune" $"test evidence: off (%s{TestEvidenceStore.KillSwitchEnvVar}=0)"
        None
    else
        match TestEvidenceKey.configsOf repoRoot with
        | Error reason ->
            Logging.warn "test-prune" $"test evidence: off — %s{reason}"
            None
        | Ok configs ->
            // Eager: the environment and env files as THIS daemon loaded them, not as a
            // later keying finds them on disk.
            let loaded = TestEvidenceKey.LoadedEnvironment.ofProcess repoRoot configs.Values
            Logging.info "test-prune" $"test evidence: keyed %s{TestEvidenceKey.LoadedEnvironment.describe loaded}"

            try
                Some
                    { Store = TestEvidenceStore.openFor repoRoot (Logging.info "test-prune")
                      Configs = configs
                      Toolchain = lazy (TestEvidenceKey.toolchainOf repoRoot)
                      Loaded = loaded
                      ProducerHash = ContentHash.ofText CheckCache.toolchainIdentity
                      ExcludePatterns = excludePatterns
                      // ADR-059 step 7: every completed project run is recorded, box-wide,
                      // under the K_P it ran over. The shadow verdict reads these next.
                      Outcomes = Some(OutcomeStore.openFor repoRoot (Logging.info "test-prune")) }
            with ex ->
                Logging.warn "test-prune" $"test evidence: off — the store could not be opened: %s{ex.Message}"
                None

/// The test-evidence wiring a daemon gets for `repoRoot`, unless `FSHW_TEST_EVIDENCE=0`.
let internal defaultEvidenceWiring (excludePatterns: string list) (repoRoot: string) =
    evidenceWiringWhen (TestEvidenceStore.enabled ()) excludePatterns repoRoot

/// `createWithEvidenceAndQueries` with the daemon's default test-evidence wiring.
let internal createWithQueries
    (queriesOf: Database -> ImpactQueries)
    (launchDeadline: TimeSpan)
    (maxParallelProjects: int option)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    =
    createWithEvidenceAndQueries
        (defaultEvidenceWiring [] repoRoot)
        queriesOf
        launchDeadline
        maxParallelProjects
        resolveExcludedProjects
        dbPath
        walkPolicy
        repoRoot

let internal createWithLaunchDeadline
    (launchDeadline: TimeSpan)
    (maxParallelProjects: int option)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    // The edges the impact walk follows (`tests.selection`). `WalkPolicy.FollowAll`
    // follows every edge.
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    (traces: TraceWiring option)
    (initialMode: TestMode)
    =
    createWithQueries
        ImpactQueries.ofDatabase
        launchDeadline
        maxParallelProjects
        resolveExcludedProjects
        dbPath
        walkPolicy
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
        traces
        initialMode

/// Create a TestPrune handler that honors declared test-scope exclusions and records
/// per-test traces as `tests.traces` asks.
///
/// `traces` is the parsed `tests.traces` block; `None` records nothing and launches every
/// project exactly as `createWithScope` does. `untracedProjects` names the projects that
/// opted out with `"traces": false`; `weaveOverrides` maps a project to its own
/// `traces.weaveTests`, which it is woven with instead of `traces.WeaveTests`. A project tracing refuses (no build output, a refused
/// weave, a failed JIT verification, an unknown `dotnet run` option) runs untraced, and
/// the refusal is stored in the trace database and logged with its reason. No trace
/// failure changes a test result or the verdict.
///
/// `initialMode` is the mode the session starts in (`TestMode.initial` for a daemon). See
/// `createWithScope` for `resolveExcludedProjects` and the launch policy.
///
/// `excludePatterns` is the daemon's `exclude`, the patterns its verdict tree hash is
/// computed with. The test-evidence key leaves out exactly those files, so two checkouts
/// whose tree hashes agree also agree on every key.
let createWithTracesIn
    (excludePatterns: string list)
    (initialMode: TestMode)
    // `tests.maxParallelProjects`: at most this many test groups run at once, longest
    // expected wall first; `None` launches every group at once.
    (maxParallelProjects: int option)
    (traces: TraceSettings option)
    (untracedProjects: Set<string>)
    (weaveOverrides: Map<string, TraceWeaveTests>)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    // The edges the impact walk follows (`tests.selection`). `WalkPolicy.FollowAll`
    // follows every edge.
    (walkPolicy: TestPrune.Domain.WalkPolicy)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    let launchDeadline =
        Environment.GetEnvironmentVariable "FSHW_LAUNCH_DEADLINE_SEC"
        |> Option.ofObj
        |> resolveLaunchDeadline

    createWithEvidenceAndQueries
        (defaultEvidenceWiring excludePatterns repoRoot)
        ImpactQueries.ofDatabase
        launchDeadline
        maxParallelProjects
        resolveExcludedProjects
        dbPath
        walkPolicy
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
        (traces
         |> Option.map (fun settings ->
             { Policy = settings
               OptedOut = untracedProjects
               WeaveOverrides = weaveOverrides
               Decide = TraceRun.decide }))
        initialMode

/// `createWithTracesIn` for a daemon session, which starts in `TestMode.initial`.
let createWithTraces
    (traces: TraceSettings option)
    (untracedProjects: Set<string>)
    (weaveOverrides: Map<string, TraceWeaveTests>)
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithTracesIn
        []
        TestMode.initial
        None
        traces
        untracedProjects
        weaveOverrides
        resolveExcludedProjects
        dbPath
        TestPrune.Domain.WalkPolicy.FollowAll
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn

/// Create a TestPrune handler that honors declared test-scope exclusions.
///
/// `resolveExcludedProjects` returns indexed test-project name (the project file's name
/// without extension) -> written reason. A covering project that is not configured and
/// is declared here with a non-blank reason no longer holds a symbol's verification
/// debt; every other covering project does, configured or not. It is called at each
/// debt classification; a throw refuses that classification and leaves the debt owed.
///
/// The launch policy is captured at construction. Environment configuration is
/// process-global, so reading it lazily at run time can retroactively change
/// already-created handlers (and made a short-deadline regression kill unrelated
/// in-flight test children under parallel CI). A handler's policy is immutable for its
/// lifetime; tests inject it through `createWithLaunchDeadline` without mutating
/// process-global state.
let createWithScope
    (resolveExcludedProjects: unit -> Map<string, string>)
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithTraces
        None
        Set.empty
        Map.empty
        resolveExcludedProjects
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn

/// Create a TestPrune handler with no declared exclusions: every covering test project
/// holds its symbols' verification debt. See `createWithScope`.
let create
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (dependsOn: string list)
    =
    createWithScope
        (fun () -> Map.empty)
        dbPath
        repoRoot
        testConfigs
        buildExtensions
        beforeRun
        afterRun
        coveragePaths
        dependsOn
