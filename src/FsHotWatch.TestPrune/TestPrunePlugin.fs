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
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.PluginStartup





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
    let compileInputsOf = compileInputsOfIn repoRoot

    let tracesFor = tracesForIn repoRoot traces

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

    let mayReplay = mayReplayIn indexedKey repoRoot testConfigs

    let restored = restorePersisted repoRoot
    let ledgerUnreadableReason = restored.LedgerUnreadableReason
    let debtPublicationPath = restored.DebtPublicationPath
    let loadedRuntimeObligations = restored.LoadedRuntimeObligations

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

    let owedDescription = owedDescriptionUnder baselineInvalidReason

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

    let loadedDebt = debtFromRestored repoRoot allowedRuntimeProjects restored

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

    let indexNamesOnlyRunnable =
        indexNamesOnlyRunnableIn queries runnableProjects debtScope

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

    let joinRefresh = joinRefreshIn extensionRefresh

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

    let flushAndQueryAffected =
        flushAndQueryAffectedWith flushPending joinRefresh selectAffected

    let flushAndQueryAffectedBounded =
        flushAndQueryAffectedBoundedWith flushAndQueryAffected

    let freshness = freshnessCellIn repoRoot
    let freshnessStore = freshness.Read
    let updateFreshness = freshness.Write
    let reconcileFreshness = freshness.Reconcile

    /// The clock `storedRowsExist: bool` did not have. Rows written
    /// seconds ago by this session's own scan are not a baseline; diffing the current
    /// extraction against them compares the file to itself, always reports "nothing
    /// changed", and selects zero test projects — on a diff that ADDS tests. See
    /// `FileFreshness.PriorRowLedger`, which owns the rule.
    let priorRows = FileFreshness.PriorRowLedger()

    // AUTOMATION-1482: analyses filed box-wide, beside this repository's test evidence.
    let analysisRecords =
        evidence
        |> Option.bind (fun (wiring: TestEvidenceRun.Wiring) -> wiring.Outcomes)

    let initialState =
        { initialStateOf initialMode mayReplay loadedDebt restored with
            IndexGuard =
                IndexedAnalysis.Guard(
                    // AUTOMATION-1609: the task cache answers a file only once its analysis
                    // is filed box-wide; until then the file is analysed again, and filed.
                    (fun pending result ->
                        mayReplay pending result
                        && AnalysisRecords.mayAnswerLocally analysisRecords repoRoot result),
                    fun result ->
                        analysisRecords
                        |> Option.exists (fun store -> AnalysisRecords.holds store repoRoot result)
                ) }


    let observeTestInputs = observeTestInputsOf testConfigs


    let currentOutcomeKeys = currentOutcomeKeysIn evidence repoRoot testConfigs

    let evidencePlanFor = evidencePlanForIn evidence repoRoot



    let priceSelection = pricedSelectionIn repoRoot

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
              PriceSelection = priceSelection
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


    let onlyRetriesRed = onlyRetriesRedIn repoRoot

    let logRedStands = logRedStandsWith owedDescription

    let dischargeCoveredBootScan =
        dischargeCoveredBootScanWith
            baselineInvalidReason
            indexNamesOnlyRunnable
            owedDescription
            repoRoot
            resolveExcludedProjects


    let endRun = endRunFrom initialMode

    let unavailableRun = unavailableRunWith endRun

    // Launched from the mailbox so it is serialised with every other launch site and
    // holds the "tests" key for its whole duration — see the `RunTestsRequested` case
    // for why that matters.
    //
    // Cooperative-safe: a force-run exists only for the client that asked for it, so the
    // framework may cancel it once that client is gone. Its test hosts run in its own
    // process scope (reaped), its result publishes only through the fold it returns
    // (never folded when cancelled), and its `finally` closes the run it opened.
    let requestTestRun = requestTestRunWith commandForceRun

    let impactRun =
        impactRunWith
            flushAndQueryAffectedBounded
            launchImpactRun
            logRedStands
            nothingOwed
            onlyRetriesRed
            repoRoot
            testConfigs
            tryRepairSchemaDrift

    let launchRequested =
        launchRequestedWith flushAndQueryAffectedBounded launchImpactRun testConfigs tryRepairSchemaDrift


    let launchHeld = launchHeldWith launchImpactRun testConfigs

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
          ObserveModelGeneration = observeModelGeneration
          FileBaseline =
            fileBaselineIn evidence repoRoot (currentOutcomeKeyResultsIn evidence repoRoot testConfigs) db.GetFileKeys }

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
          FindCheckoutBaseline = checkoutBaselineIn evidence dbPath
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
          OnlyRetriesRed = onlyRetriesRed
          SaveSeal = IndexSeal.save (Logging.warn "test-prune") dbPath }

    let onBatchChecked ctx state modelGeneration batch =
        onBatchCheckedWith batchCheckedEnv ctx state modelGeneration batch

    let fileCheckedEnv: FileCheckedEnv =
        { RepoRoot = repoRoot
          Db = db
          PriorRows = priorRows
          FreshnessStore = freshnessStore
          UpdateFreshness = updateFreshness
          EnqueuePending = enqueuePending
          AnalysisRecords = analysisRecords }

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
                      ProducerHash = ContentHash.ofText CheckCache.producerIdentity
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
               Decide = TraceRun.decide
               PredictFingerprint =
                 fun project args env ->
                     TraceShadow.predictFingerprint
                         settings
                         (weaveOverrides.TryFind project |> Option.defaultValue settings.WeaveTests)
                         repoRoot
                         (deriveProjectBin args repoRoot)
                         env
                         TracedLaunch.dotnetRootOfThisProcess }))
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
