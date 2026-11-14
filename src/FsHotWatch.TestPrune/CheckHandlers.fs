/// The BatchChecked and BuildCompleted handlers, over the run-launch helpers they share (LaunchSteps).
module FsHotWatch.TestPrune.CheckHandlers

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

/// A fold step over the plugin state that may post to the plugin.
type internal StateStep = PluginCtx<TestPruneMsg> -> TestPruneState -> TestPruneState

/// `launchImpactRun`: the launched state, or `None` when the tests slot is already held.
type internal ImpactLauncher =
    PluginCtx<TestPruneMsg> -> TestConfig list -> TestPruneState -> bool -> Set<string> -> TestPruneState option

/// The plugin's run-launch helpers the check and build handlers share.
type internal LaunchSteps =
    { AttachToFullRun: TestPruneState -> TestPruneState
      EnqueueImpactRun: PluginCtx<TestPruneMsg> -> unit
      FlushAndQueryAffectedBounded: StateStep
      JoinsFullRun: TestPruneState -> bool -> bool
      LaunchImpactRun: ImpactLauncher
      NothingOwed: VerificationDebt -> bool
      TryRepairSchemaDrift: exn -> unit }

/// What `onBatchCheckedWith` reads from the plugin.
type internal BatchCheckedEnv =
    {
        RepoRoot: string
        TestConfigs: TestConfig list option
        RunnableProjects: Set<string>
        Launch: LaunchSteps
        DischargeCoveredBootScan: PluginCtx<TestPruneMsg> -> BatchChecked -> TestPruneState -> TestPruneState
        LaunchHeld: PluginCtx<TestPruneMsg> -> TestPruneState -> string -> TestPruneState
        LogRedStands: string -> TestPruneState -> unit
        OwedDescription: VerificationDebt -> string
        OnlyRetriesRed: TestPruneState -> bool
        /// Record the tree the index is now sealed at beside it (`IndexSeal`), or clear it.
        SaveSeal: string option -> unit
        /// Publish what a restarted daemon would select, once the seal is saved
        /// (AUTOMATION-1623, ADR-069): a re-seal at another tree can end a usable baseline.
        PublishRestartSelection: FullSuiteBaseline.Baseline option -> unit
    }

/// A sealed batch of check results under the current model.
let internal onBatchCheckedWith
    (env: BatchCheckedEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (modelGeneration: int64 option)
    (batch: BatchChecked)
    =
    let repoRoot = env.RepoRoot
    let testConfigs = env.TestConfigs
    let runnableProjects = env.RunnableProjects
    let attachToFullRun = env.Launch.AttachToFullRun
    let enqueueImpactRun = env.Launch.EnqueueImpactRun
    let flushAndQueryAffectedBounded = env.Launch.FlushAndQueryAffectedBounded
    let joinsFullRun = env.Launch.JoinsFullRun
    let launchImpactRun = env.Launch.LaunchImpactRun
    let nothingOwed = env.Launch.NothingOwed
    let tryRepairSchemaDrift = env.Launch.TryRepairSchemaDrift
    let dischargeCoveredBootScan = env.DischargeCoveredBootScan
    let launchHeld = env.LaunchHeld
    let logRedStands = env.LogRedStands
    let owedDescription = env.OwedDescription
    let onlyRetriesRed = env.OnlyRetriesRed

    async {
        // Cohort-complete flush. The mailbox is FIFO and the daemon emits
        // BatchChecked strictly after the last FileChecked, so every
        // FileChecked from this cohort is already folded into
        // `ChangedSymbols`/`PendingAnalysis` by now.
        //
        // This is the canonical DB persistence point, NOT BuildCompleted. On a
        // cold scan `Daemon.performScan` awaits BuildPlugin terminal BEFORE
        // the FCS tier checks, so BuildCompleted reaches this mailbox before
        // any FileChecked — a BuildCompleted-only flush would always fire
        // against an empty `PendingAnalysis` and leave the symbol DB
        // permanently empty. BuildCompleted's flush stays as an idempotent
        // re-run plus the test-trigger.
        let flushed =
            try
                Ok(flushAndQueryAffectedBounded ctx state)
            with ex ->
                Error ex

        match flushed with
        | Error ex ->
            Logging.error "test-prune" $"BatchChecked flushAndQueryAffected failed: %s{ex.Message}"
            tryRepairSchemaDrift ex
            return state
        | Ok flushedState ->
            // The seal is the moment the scan has an answer: every checkable
            // file of this model either analysed or did not. An analysis-only
            // daemon publishes that answer as its receipt. A daemon that runs
            // tests grades its test evidence through it, so a file the scan
            // never finished refuses the run selected without its symbols.
            let flushedState =
                // A seal that re-discovered the model on the scoped path names
                // the files it did not re-check because their inputs did not
                // change. Their outcomes stand for this model.
                let analysisFiles =
                    match batch.Retained, modelGeneration with
                    | Some carried, Some generation ->
                        AnalysisFileEvidence.carryInto generation carried flushedState.AnalysisFiles
                    | _ -> flushedState.AnalysisFiles

                let membership =
                    match ctx.ProjectGraph.ObserveCheckableFiles() with
                    | Some(generation, files) when Some generation = modelGeneration -> Some files
                    | Some _
                    | None -> None

                let seal =
                    membership
                    |> Option.bind (fun files -> AnalysisEvidence.fromCompleted modelGeneration files analysisFiles)

                { flushedState with
                    AnalysisFiles = analysisFiles
                    CohortSeal = seal
                    AnalysisReceipt = if runnableProjects.IsEmpty then seal else None }

            // ── DRAIN THE PENDING QUEUE ────────────────
            // The cohort seal is the first moment this scan's symbols are
            // known. `BuildCompleted` cannot be the only test trigger: on a
            // scan it fires BEFORE the FCS pass, so scan-discovered symbols
            // would never be verified by the run it launched and would
            // accumulate silently while `check` reported a stale terminal
            // status. If symbols remain unverified here, RUN the tests that
            // verify them — a verdict is only ever earned by a run.
            //
            // The skip asks `nothingOwed`, not `Set.isEmpty`: an UNREADABLE
            // ledger leaves the in-memory queue empty because we cannot name
            // what it held, and reading that as "nothing to drain" lets a
            // corrupt sidecar run ZERO tests and still go green.
            // The tree these check results describe: read after the fold, so bytes
            // written since read as a moved tree. Read on every seal, including those
            // before this session's first run: a cold daemon's first full run is the run
            // a `test-baseline` record most often describes, and it files one only when
            // the index is sealed at its receipt tree (AUTOMATION-1587 step 2). Every
            // DECISION that reads it is still guarded by a baseline of its own: a session
            // baseline (the symbol-free-edit narrowing, the covered boot scan's
            // `CoveredFullSuite`), or for `DiffWait.awaits` a session baseline or the
            // checkout baseline the tree moved from (AUTOMATION-1587).
            let flushedState =
                { flushedState with
                    SealedTree = ReceiptInputTree.readTree repoRoot }

            // The index now describes exactly this tree, until a flush writes rows again: a
            // restarted daemon reads it to know its cold scan diffs from this tree.
            env.SaveSeal(flushedState.SealedTree |> Option.map _.Identity)
            env.PublishRestartSelection flushedState.Debt.Baseline

            // A held launch takes its inputs from this fold, whichever branch launches it.
            match flushedState.AwaitingDiff with
            | Some awaited -> Logging.info "test-prune" (DiffWait.released awaited false)
            | None -> ()

            let flushedState = dischargeCoveredBootScan ctx batch flushedState

            if nothingOwed flushedState.Debt then
                if flushedState.AwaitingDiff.IsSome then
                    return launchHeld ctx flushedState "the check results it waited for are folded and owe nothing"
                else
                    return flushedState
            else
                match testConfigs with
                | Some configs when not configs.IsEmpty && onlyRetriesRed flushedState ->
                    logRedStands "BatchChecked" flushedState

                    return
                        { flushedState with
                            AwaitingDiff = None
                            CheckoutBaseline = None }
                | Some configs when not configs.IsEmpty ->
                    // Drain UNCONDITIONALLY when the slot is free. Never defer
                    // to "the BuildCompleted that is surely coming": several
                    // `FileChanged` shapes never produce one at all
                    // (BuildPlugin ignores `SolutionChanged`, and DROPS a
                    // FileChanged arriving while a build is running), so the
                    // deferral can wait forever. CI caught exactly that — 3
                    // symbols deferred to a BuildCompleted that never came,
                    // zero tests run, exit 0.
                    //
                    // Draining on a half-built tree is safe: the
                    // apphost-freshness gate in `executeTests` refuses
                    // `--no-build` against an artifact older than its sources
                    // and defers that project WITHOUT spawning a test process,
                    // so the stale case costs a status flip, not a run.
                    //
                    // The claim is ATTEMPTED, not pre-checked: `RunExclusive`
                    // returns the outcome, so there is no TOCTOU between an
                    // `IsRunning` read and the launch.
                    // A launch held on a checkout baseline selects against it (AUTOMATION-1587).
                    let hasCachedResults = flushedState.HasBaselineInHand
                    let forceRunProjects = flushedState.PendingForceRunProjects

                    let drainedState =
                        { flushedState with
                            PendingForceRunProjects = Set.empty
                            AwaitingDiff = None }

                    match launchImpactRun ctx configs drainedState hasCachedResults forceRunProjects with
                    | Some launched ->
                        Logging.info "test-prune" $"BatchChecked: %s{owedDescription flushedState.Debt} — draining now"

                        return launched
                    | None when joinsFullRun flushedState (batch.Trigger = BootScan) ->
                        // The full-suite run in flight covers the built tree
                        // this cohort describes, so it takes the debt instead of
                        // a duplicate run. TestsFinished may discharge it only
                        // from actual green full-suite evidence over an
                        // unchanged input tree; failure, partial scope, or a
                        // moved tree keeps the durable queue outstanding.
                        Logging.info
                            "test-prune"
                            $"BatchChecked: %s{owedDescription flushedState.Debt} discovered during a full-suite run — attaching debt to that run"

                        return
                            attachToFullRun
                                { flushedState with
                                    AwaitingDiff = None }
                    | None ->
                        // A run is in flight but was launched against an older
                        // queue snapshot, so it cannot clear these symbols.
                        // Queue the rerun behind it as an intent. The pending
                        // fanout is retained (the work was NOT consumed).
                        Logging.info
                            "test-prune"
                            $"BatchChecked: %s{owedDescription flushedState.Debt} still outstanding while a run is in flight — queueing re-run"

                        enqueueImpactRun ctx

                        return
                            { flushedState with
                                AwaitingDiff = None }
                | _ ->
                    // Analysis-only (no test configs): nothing can verify
                    // these symbols, so there is nothing to drain.
                    return flushedState
    }

/// What `onBuildCompletedWith` reads from the plugin.
type internal BuildCompletedEnv =
    {
        RepoRoot: string
        TestConfigs: TestConfig list option
        Launch: LaunchSteps
        HoldForDiff: WaitBasis -> StateStep
        /// This checkout's own last full pass, given its ledger's baseline and the current
        /// tree, when the tree moved from it and the index is still sealed there; otherwise
        /// why not (`checkoutBaselineIn`).
        FindCheckoutBaseline: FullSuiteBaseline.Baseline option -> string -> Result<CheckoutBaseline, string>
        JoinRefresh: StateStep
        LaunchScopeOf: TestRunInputs -> LaunchScope
        ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option
    }

/// A completed build: flush, select, and launch or hold the impact run it owes.
let internal onBuildCompletedWith
    (env: BuildCompletedEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (buildResult: BuildResult)
    =
    let repoRoot = env.RepoRoot
    let testConfigs = env.TestConfigs
    let attachToFullRun = env.Launch.AttachToFullRun
    let enqueueImpactRun = env.Launch.EnqueueImpactRun
    let flushAndQueryAffectedBounded = env.Launch.FlushAndQueryAffectedBounded
    let joinsFullRun = env.Launch.JoinsFullRun
    let launchImpactRun = env.Launch.LaunchImpactRun
    let nothingOwed = env.Launch.NothingOwed
    let tryRepairSchemaDrift = env.Launch.TryRepairSchemaDrift
    let holdForDiff = env.HoldForDiff
    let findCheckoutBaseline = env.FindCheckoutBaseline
    let joinRefresh = env.JoinRefresh
    let launchScopeOf = env.LaunchScopeOf
    let observeModelGeneration = env.ObserveModelGeneration

    async {
        match buildResult with
        | BuildSucceeded ->
            // Record that BuildCompleted has fired this session, so subsequent
            // FileChecked events may promote the freshness sidecar to clean.
            // Set unconditionally across both the queued-rerun and run-now
            // branches: the gate asks whether the build has realized the
            // reference graph, not whether tests are queued.
            let state =
                { state with
                    BuildCompletedInThisSession = true }

            // ── Dependency-fanout fingerprint (computed for EVERY
            // build, before the running/idle split, so the prior
            // fingerprint always advances and a mid-run dependency
            // change is never lost). Fingerprint each test project from
            // its referenced projects' non-symbol inputs + own package
            // versions (DependencyFanout). A project whose fingerprint moved
            // since the last build had a dependency/binary change the
            // symbol diff can't see → force-run it. An empty graph
            // (tests / no graph wired) yields no fanout, so the
            // symbol-precise path is unchanged.
            let fsprojByName =
                ctx.ProjectGraph.GetAllProjects()
                |> List.map (fun p -> Path.GetFileNameWithoutExtension p, p)
                |> Map.ofList

            let currentFingerprints =
                match testConfigs with
                | Some configs ->
                    let fingerprint = DependencyFanout.fingerprinter ctx.ProjectGraph

                    configs
                    |> List.choose (fun c ->
                        Map.tryFind c.Project fsprojByName
                        |> Option.map (fun fsproj -> c.Project, fsproj))
                    |> List.map (fun (name, fsproj) -> name, fingerprint fsproj)
                    |> Map.ofList
                | None -> Map.empty

            let fanoutNow =
                DependencyFanout.changedProjects state.PriorProjectFingerprints currentFingerprints

            if not (Set.isEmpty fanoutNow) then
                Logging.info
                    "test-prune"
                    $"Dependency fanout: %d{Set.count fanoutNow} test project(s) had a \
                      dependency-fingerprint change — force-running: \
                      %s{describeAll (Set.toList fanoutNow)}"
            else
                // Say that nothing fanned out, and say what was examined to
                // reach that conclusion. "No dependency changed" and
                // "fingerprinting never ran" otherwise produce identical
                // logs, so an inert `computeProjectFingerprint` — the safety
                // net against binary-only changes silently OFF — reads as a
                // healthy quiet run. Zero fingerprints computed, or zero
                // graph projects, means inert rather than clean.
                Logging.info
                    "test-prune"
                    $"Dependency fanout: none — \
                      %d{currentFingerprints.Count} project fingerprint(s) computed, \
                      %d{fsprojByName.Count} project(s) in the graph, \
                      %d{state.PriorProjectFingerprints.Count} prior fingerprint(s) to compare against"

            // Advance the baseline on every build; carry any not-yet-run
            // fanout in the pending set (consumed by the queued rerun).
            let state =
                { state with
                    PriorProjectFingerprints =
                        DependencyFanout.advance state.PriorProjectFingerprints currentFingerprints }

            // Asked BEFORE selection: a held key refuses the claim, and
            // selection is the expensive step (a flush and an impact query
            // over the whole index), so selecting only to be refused is
            // wasted work. A fold of a finished run holds the key as surely
            // as a live run does. The claim below still handles `SlotBusy`
            // for a key taken after this read.
            let heldBy =
                match ctx.SlotHolder "tests" with
                | SlotHolder.Free -> None
                | SlotHolder.LiveRun -> Some "tests already running"
                | SlotHolder.Fold -> Some "tests key held by an uncommitted fold"

            match heldBy with
            | Some reason when joinsFullRun state false ->
                // A pass-through full run takes what is owed: no run follows
                // it. The fanout is kept for whatever launches next.
                ctx.Log $"  ↳ attached to the full run (%s{reason})"

                Logging.info
                    "test-prune"
                    $"BuildSucceeded received during a full-suite run (%s{reason}) — attaching debt to that run"

                return
                    { attachToFullRun state with
                        PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
            | Some reason ->
                // The leading two spaces nest this under the in-flight test
                // run in the activity-fold `recent:` view (the renderer
                // already indents every tail entry by 8), so it does not read
                // as a sibling of the test-result lines.
                ctx.Log $"  ↳ queued re-run (%s{reason})"

                Logging.info
                    "test-prune"
                    $"BuildSucceeded received but %s{reason} — will re-run after, without selecting now"

                // Stash the fanout so the rerun runs it (don't lose a
                // mid-run dependency change).
                enqueueImpactRun ctx

                return
                    { state with
                        PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
            | None ->
                Logging.info "test-prune" "BuildSucceeded: starting test run"

                // Flush/query before announcing Running so the reported status never
                // lies: announcing Running before the flush would flash Running even
                // on a schema-drifted DB.
                // The framework catches uncaught throws and forces Failed as a
                // defense-in-depth net; we still trap locally here so we can run
                // the schema-drift self-heal and preserve the idle transition.
                match
                    (try
                        Ok(flushAndQueryAffectedBounded ctx state)
                     with ex ->
                         Error ex)
                with
                | Error ex ->
                    Logging.error "test-prune" $"flushAndQueryAffected failed: %s{ex.Message}"
                    tryRepairSchemaDrift ex

                    ctx.ReportStatus(PluginStatus.failedNow ex.Message $"flush failed: %s{ex.Message}" TimeSpan.Zero)

                    return state
                | Ok stateWithAffected ->
                    match testConfigs with
                    | Some configs when not configs.IsEmpty ->
                        let hasCachedResults = state.LastResults.IsSome
                        let currentTree = ReceiptInputTree.read repoRoot

                        // The baseline in hand, which is what a wait for the diff needs: a
                        // session baseline, or else this checkout's own last full pass at a
                        // tree T the current tree MOVED from, with the index still sealed at
                        // T (AUTOMATION-1587). An unchanged tree is no checkout baseline:
                        // the skip on records answers it at once, without a diff.
                        let basis =
                            if hasCachedResults then
                                WaitBasis.SessionBaseline
                            else
                                match currentTree with
                                | None -> WaitBasis.NoBaseline
                                | Some tree ->
                                    match findCheckoutBaseline state.Debt.Baseline tree with
                                    | Ok baseline -> WaitBasis.CheckoutBaseline baseline
                                    | Error why ->
                                        Logging.info "test-prune" $"diff-wait: no checkout baseline: %s{why}"
                                        WaitBasis.NoBaseline

                        // Only a launch with a baseline in hand can wait for the diff,
                        // and whether it does reads the extensions' failures. Without
                        // one, the run takes them itself.
                        let stateWithAffected =
                            if basis <> WaitBasis.NoBaseline then
                                joinRefresh ctx stateWithAffected
                            else
                                stateWithAffected

                        // Union this build's fanout with any pending
                        // fanout deferred from a prior mid-run build,
                        // then clear the pending set (it's being run).
                        let forceRunProjects = Set.union fanoutNow stateWithAffected.PendingForceRunProjects

                        // A checkout baseline an earlier hold left is this build's to decide
                        // again: only a launch held on one selects against it.
                        let launchState =
                            { stateWithAffected with
                                PendingForceRunProjects = Set.empty
                                CheckoutBaseline = None }

                        let launchInputs = TestRunInputs.ofState launchState

                        let awaitsDiff =
                            DiffWait.awaits
                                { Basis = basis
                                  HasModel = (observeModelGeneration ctx).IsSome
                                  OwesNothing = nothingOwed launchInputs.Debt
                                  RunsInFullAnyway =
                                    Set.unionMany
                                        [ forceRunProjects
                                          EarnedEvidence.wholeProjectGap
                                              (observeModelGeneration ctx)
                                              (ObservedModel.inputs ctx.ProjectGraph)
                                              (fullSuiteProjects configs)
                                              launchInputs.Earned ]
                                  RunsSomethingAnyway =
                                    not (
                                        List.isEmpty launchInputs.OutstandingFailures
                                        && Map.isEmpty launchInputs.UnanalyzableFiles
                                        && Set.isEmpty launchInputs.Unindexed
                                        && Map.isEmpty launchInputs.FailedExtensions
                                    )
                                  FoldedChanges = UncoveredChanges.isAll launchInputs.ChangedSymbolsAllUncovered
                                  Scope = launchScopeOf launchInputs
                                  EvidenceTree = launchInputs.EvidenceTree
                                  CurrentTree = currentTree
                                  SealedTree = launchState.SealedTree |> Option.map _.Identity }

                        if awaitsDiff then
                            let launchState =
                                match basis with
                                | WaitBasis.CheckoutBaseline baseline ->
                                    { launchState with
                                        CheckoutBaseline = Some baseline }
                                | WaitBasis.NoBaseline
                                | WaitBasis.SessionBaseline -> launchState

                            return holdForDiff basis ctx launchState
                        else
                            Logging.info "test-prune" (DiffWait.describe false basis 0L false)

                            match launchImpactRun ctx configs launchState hasCachedResults forceRunProjects with
                            | Some launched -> return launched
                            | None when joinsFullRun stateWithAffected false ->
                                // The key is held by a pass-through full run whose
                                // result is not folded yet: it takes the debt.
                                Logging.info
                                    "test-prune"
                                    "BuildSucceeded: tests slot held by a full-suite run — attaching debt to that run"

                                return
                                    { attachToFullRun stateWithAffected with
                                        PendingForceRunProjects = forceRunProjects }
                            | None ->
                                // The key is held without a live run: a result fold
                                // or an intent. Same treatment: queue the rerun behind
                                // it, retain the un-consumed fanout.
                                Logging.info "test-prune" "BuildSucceeded: tests slot already held — queueing re-run"

                                enqueueImpactRun ctx

                                return
                                    { stateWithAffected with
                                        PendingForceRunProjects = forceRunProjects }
                    | _ ->
                        // No test configs — flush only; nothing to run.
                        return stateWithAffected
        | BuildFailed _ -> return state
    }
