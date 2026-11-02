/// An impact run, launched and executed over the stages in ImpactLaunch, with the aborted lifecycle when it throws.
module FsHotWatch.TestPrune.ImpactRun

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
open FsHotWatch.TestPrune.RunExecution
open FsHotWatch.TestPrune.AffectedSelection
open FsHotWatch.TestPrune.ImpactLaunch

let internal runTestsWithImpactWith
    (env: RunEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (configs: TestConfig list)
    // The four fields a run reads — NOT the state record. The async this
    // returns outlives the state generation it was launched from; see
    // `TestRunInputs`.
    (inputs: TestRunInputs)
    // `hasCachedResults` (`state.LastResults.IsSome`, computed by the caller)
    // means a run already completed THIS session — i.e. a green baseline exists
    // to be "test-equivalent" to. The zero-affected skip needs this AS WELL AS an
    // empty queue: a cold daemon with an empty queue but no prior run has no
    // baseline yet and must run the full suite once to establish one. See
    // the skip gate below.
    (hasCachedResults: bool)
    // Dependency-fanout (DependencyFanout): test projects whose dependency
    // fingerprint changed since the last build. Their tests run in FULL
    // (project-coarse), UNIONED with the symbol-precise selection — a binary
    // change the symbol diff can't see still re-runs the dependent tests.
    // Empty in the common case (no dependency change), so ordinary
    // source-symbol edits keep their precise, minimal selection.
    (fanoutProjects: Set<string>)
    : Async<TestPruneMsg> =
    let repoRoot = env.Config.RepoRoot
    let configuredTestProjects = env.Config.ConfiguredTestProjects
    let launchDeadline = env.Config.LaunchDeadline
    let maxParallelProjects = env.Config.MaxParallelProjects
    let beforeRun = env.Config.BeforeRun
    let afterRun = env.Config.AfterRun
    let coveragePaths = env.Config.CoveragePaths
    let db = env.Db
    let nothingOwed = env.Debt.NothingOwed
    let awaitDiscoveryBeforeLaunch = env.Launch.AwaitDiscovery
    let compileInputsOf = env.Launch.CompileInputsOf
    let coverageIngestFailed = env.Launch.CoverageIngestFailed
    let logLaunch = env.Launch.LogLaunch
    let logSelectedBy = env.Launch.LogSelectedBy
    let observeModelGeneration = env.Launch.ObserveModelGeneration
    let observeTestInputs = env.Launch.ObserveTestInputs
    let tracesFor = env.Launch.TracesFor

    async {
        do! awaitDiscoveryBeforeLaunch ctx

        let mutable emittedStart: TestRunStarted option = None

        let emitStarted started =
            emittedStart <- Some started
            ctx.EmitTestRunStarted started

        let widenings = widenLaunch env ctx configs inputs fanoutProjects
        let launchedRuntimeObligations = widenings.LaunchedRuntimeObligations
        let fullSuiteCause = widenings.FullSuiteCause
        let forceRunProjects = widenings.ForceRunProjects
        let launchedSymbols = widenings.LaunchedSymbols
        let launchedRevisions = widenings.LaunchedRevisions

        // Advance the poisoned-seed counters HERE, at the launch of
        // a test RUN, so the count means what `PoisonSeedRuns` and the warning text
        // claim. `flushAndQueryAffected` runs several times per edit-save cycle.
        if not (TestMode.skips SeedAgeing inputs.Mode) then
            env.BumpSeedAges(Set.toList launchedSymbols)

        try
            let selected = selectForLaunch env ctx configs inputs hasCachedResults widenings
            let affectedByProject = selected.AffectedByProject
            let totalClasses = selected.TotalClasses
            let alwaysOnly = selected.AlwaysOnly

            let assembled =
                assembleLaunch env ctx configs inputs hasCachedResults fanoutProjects widenings selected

            let evidencePlan = assembled.EvidencePlan
            let excludedByProject = assembled.ExcludedByProject
            let launch = assembled.Launch

            logLaunch "impact" inputs.Mode launch

            let hasQuarantinedFailures = not (List.isEmpty inputs.OutstandingFailures)

            // Two independent routes to the degenerate zero-affected skip. Both
            // terminate as a clean green via the same lifecycle, differing from
            // "tests exist and all passed" only in that zero ran:
            //
            //  (1) Baseline-equivalent. Queue PROVABLY empty AND a session baseline
            //      exists. An empty queue means "test-equivalent to the last green
            //      run", so "0 affected tests" is a sound green. Both halves are
            //      load-bearing: a NON-empty queue with 0 affected classes (covered
            //      symbols whose tests aren't indexed yet) must run the suite rather
            //      than silent-green, and the first run of a session has no baseline
            //      to be equivalent TO, so it must run the full suite to establish
            //      one. Reads `nothingOwed`, not `Set.isEmpty` — an unreadable ledger
            //      owes an unknown debt and can never be baseline-equivalent
            //
            //  (2) Nothing-to-verify. This cycle HAD changed/queued symbols and every
            //      one proved to have no covering test (`ChangedSymbolsAllUncovered`,
            //      set by `flushAndQueryAffected` as it dropped them), so even a
            //      cold-start full suite would verify nothing about them. Sound
            //      WITHOUT a session baseline, unlike route 1, because it is gated on
            //      symbols having existed and provably lacking any test. Without it
            //      an all-uncovered cold run falls through to the full suite and
            //      hangs, never resolving WaitForComplete. A genuine cold start with
            //      NO pending symbols leaves the flag false, so the baseline runs.
            //
            //  Route 1 also needs evidence bound to THIS tree. Test-equivalence is a
            //  claim about symbols; a receipt is a claim about bytes, and an edit the
            //  symbol diff does not see (a comment) moves the bytes. Skipping there
            //  earns nothing, so `check` could only answer "no tests ran".
            let treeUnverified =
                not (ReceiptInputTree.matches inputs.EvidenceTree launch.InputTreeHash)

            let baselineEquivalent =
                nothingOwed inputs.Debt && hasCachedResults && not treeUnverified

            let nothingToVerify = UncoveredChanges.isAll inputs.ChangedSymbolsAllUncovered

            let nothingForced =
                totalClasses = 0 && Set.isEmpty forceRunProjects && not hasQuarantinedFailures

            if nothingForced && not alwaysOnly && (baselineEquivalent || nothingToVerify) then
                return! zeroAffectedSkip emitStarted inputs launch nothingToVerify
            else
                logSelectedBy launch

                logLaunchSelection inputs hasCachedResults widenings selected excludedByProject treeUnverified

                let! results, started, completed, boundTree, testHostStartedAt, evidenceHeld =
                    executeTests
                        db
                        (Some ctx)
                        emitStarted
                        repoRoot
                        launchDeadline
                        maxParallelProjects
                        ctx.Processes
                        (tracesFor ctx inputs.Mode)
                        launch.InputTree
                        (compileInputsOf ctx)
                        beforeRun
                        (HookStep.asSubtasks ctx.StartSubtask ctx.EndSubtask)
                        coveragePaths
                        (coverageIngestFailed ctx)
                        afterRun
                        configuredTestProjects
                        configs
                        affectedByProject
                        excludedByProject
                        None
                        evidencePlan

                // `executeTests` emits Started before launching the host and still
                // emits per-group TestProgress live; the synchronous handler captures
                // Completed for cache replay.
                ignore results

                return
                    TestsFinished(
                        started,
                        completed,
                        { launch with
                            InputTree = boundTree
                            TestHostStartedAt = Some testHostStartedAt
                            Reused = evidenceHeld }
                    )
        with ex ->
            Logging.error "test-prune" $"runTests failed: %s{ex.Message}"

            // Build an Aborted lifecycle so subscribers see a coherent end
            // to this run rather than hanging at TestRunStarted.
            let started, completed = abortedRunLifecycle emittedStart ex.Message

            if emittedStart.IsNone then
                emitStarted started

            // launch carries the queue snapshot this aborted run was
            // launched against; the TestsFinished handler commits NOTHING
            // for an Aborted outcome, so those symbols stay queued. Rebuilt
            // from `launchedSymbols` here (rather than the in-try `launch`,
            // which may not exist if the per-symbol coverage query itself
            // threw) with an empty covering map — an Aborted run commits
            // nothing, so the covering map is never consulted on this path.
            // The empty SELECTION says the same thing about the ledger: an
            // aborted run executed nothing, so it clears nothing.
            let launch =
                { InputTree = None
                  ModelGeneration = observeModelGeneration ctx
                  ProjectInputs = observeTestInputs ctx
                  Symbols = launchedSymbols
                  SymbolRevisions = launchedRevisions
                  ChangedFiles = inputs.ChangedFiles
                  TestHostStartedAt = None
                  CoveringProjectsBySymbol = Map.empty
                  RuntimeProjectsByFile = launchedRuntimeObligations
                  Selection = Map.empty
                  WouldHaveRun = None
                  Reused = Map.empty
                  Seeds = inputs.Seeds
                  ZeroSelection = ZeroSelection.NotAZero
                  FullSuiteCause = fullSuiteCause
                  SelectedBy = []
                  SeedFanout = SeedFanoutReading.NoSelection
                  TraceShadow = [] }

            return TestsFinished(started, completed, launch)
    }
