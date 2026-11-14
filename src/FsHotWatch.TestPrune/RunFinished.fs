/// A test run's completion folded into state, over a RunFinishedEnv.
module FsHotWatch.TestPrune.RunFinished

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

/// What `onRunFinishedWith` reads from the plugin. The declared exclusions are resolved
/// when the handler calls `ResolveExcludedProjects`, as before.
type internal RunFinishedEnv =
    {
        RepoRoot: string
        RunnableProjects: Set<string>
        Traces: TraceWiring option
        ResolveExcludedProjects: unit -> Map<string, string>
        Debt: CompletionDebt
        EndRun: bool -> TestPruneState -> TestPruneState
        ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option
        /// File the shadow `test-baseline` record this completion may earn, and log the
        /// outcome either way (AUTOMATION-1587 step 2). Nothing reads what it files.
        FileBaseline: PluginCtx<TestPruneMsg> -> TestBaseline.Facts -> unit
        /// Publish what a restarted daemon would select, from the ledger this completion
        /// leaves (AUTOMATION-1623, ADR-069).
        PublishRestartSelection: FullSuiteBaseline.Baseline option -> unit
    }

/// A run's completion (`TestsFinished`, or a requested run's `CommandTestsFinished`), folded into state.
let internal onRunFinishedWith
    (env: RunFinishedEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (message: TestPruneMsg)
    (started: TestRunStarted)
    (launchedCompletion: TestRunCompleted)
    (launchedAs: TestRunLaunch)
    =
    let repoRoot = env.RepoRoot
    let runnableProjects = env.RunnableProjects
    let traces = env.Traces
    let resolveExcludedProjects = env.ResolveExcludedProjects
    let coveringOf = env.Debt.CoveringOf
    let debtScope = env.Debt.DebtScope
    let indexNamesOnlyRunnable = env.Debt.IndexNamesOnlyRunnable
    let owedElsewhere = env.Debt.OwedElsewhere
    let endRun = env.EndRun
    let observeModelGeneration = env.ObserveModelGeneration

    async {
        // A completion that lands while the model is being re-discovered has
        // nothing to be compared against yet: wait for the re-discovery to
        // settle, so it is carried or revoked per project instead of whole.
        let! waited = ObservedModel.awaitSettled ctx.ProjectGraph launchedAs.ModelGeneration

        if waited > TimeSpan.Zero then
            let waitedRun = launchedCompletion.RunId.ToString("N")

            Logging.info
                "test-prune"
                $"Run %s{waitedRun} completed while the project model was being re-discovered; waited %.1f{waited.TotalSeconds}s for it to settle before folding"

        // A model replaced while the run was in flight takes back only the
        // results of the projects whose compile inputs it changed.
        let modelCarry, completed, launch =
            acrossModelChange
                (observeModelGeneration ctx)
                (ObservedModel.inputs ctx.ProjectGraph)
                launchedCompletion
                launchedAs

        let runText = launchedCompletion.RunId.ToString("N")
        let launchedText = ObservedModel.describe launchedAs.ModelGeneration
        let nowText = ObservedModel.describe (observeModelGeneration ctx)

        match modelCarry with
        | ModelCarry.SameModel -> ()
        | ModelCarry.Carried ->
            Logging.info
                "test-prune"
                $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran, but no test project's compile inputs changed — its results stand"
        | ModelCarry.Revoked changed ->
            Logging.info
                "test-prune"
                $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran and changed the compile inputs of %s{describeAll (Set.toList changed)} — their results are revoked; the others stand"
        | ModelCarry.Unprovable ->
            Logging.info
                "test-prune"
                $"Run %s{runText}: the project model moved from generation %s{launchedText} to %s{nowText} while it ran, and no per-project compile-input identities can show what changed — the whole run is revoked"

        // The declarations this completion retires debt under, resolved ONCE and
        // BEFORE any side effect. A resolution that throws (an ambiguous or
        // unobserved excluded project) fails this handler before it has emitted,
        // recorded or committed anything, so the framework settles the work as a
        // failure and every symbol stays owed.
        // Every symbol this completion can ask about: what the run launched,
        // what was attached to it, and what is queued. One grouped query,
        // and only if something asks.
        let covering =
            coveringOf (
                Seq.concat
                    [ Set.toSeq launch.Symbols
                      Map.keys state.DebtDuringFullRun
                      Set.toSeq state.Debt.PendingQueue ]
            )

        let excluded =
            if Set.isEmpty runnableProjects then
                lazy (resolveExcludedProjects ())
            else
                Lazy<_>.CreateFromValue(resolveExcludedProjects ())

        let owedTo = debtScope excluded covering

        // Whether the index names a test project no run here can discharge:
        // neither configured nor declared excluded. If not, no symbol can be
        // owed elsewhere, so nothing needs asking per symbol. One scan of the
        // index, not a covering query, and only if something asks. An index
        // that cannot be scanned may name one.
        let noUnrunnableCoverers = lazy (indexNamesOnlyRunnable excluded)

        // Emit the lifecycle events synchronously here, inside the framework's
        // per-event capture window, so they land in the cached EmittedEvents
        // and re-fire on cache replay — subscribers that key off
        // TestRunCompleted (FileCommandPlugin) must see it on a hit.
        ctx.EmitTestRunCompleted completed

        // AUTOMATION-1660: each step of the fold logs its start and end, under the run's id.
        let postRun = completed.RunId.ToString("N")

        let assembled =
            PostRunStep.timed postRun "fold: assemble results" (fun () ->
                assembleRunResults
                    { State = state
                      Completed = completed
                      Launch = launch
                      RepoRoot = repoRoot
                      RunnableProjects = runnableProjects
                      Traces = traces })

        let testResults = assembled.TestResults
        let coverage = assembled.Coverage
        let carriedFailures = assembled.CarriedFailures
        let outstandingFailures = assembled.OutstandingFailures

        // Discharged HERE, immediately before the ledger
        // rewrite, because this is the one place the outstanding set is
        // recomputed — pruning anywhere else would leave the ledger and the
        // state disagreeing about what is still owed.
        let unanalyzable =
            PostRunStep.timed postRun "fold: prune unanalyzable" (fun () ->
                pruneDeletedUnanalyzable File.Exists state.UnanalyzableFiles)

        // The ONLY path to the ledger: clear the slate, re-report the whole
        // outstanding set. There is no wholesale clear a filtered run can
        // reach for.
        PostRunStep.timed postRun "fold: report outstanding" (fun () ->
            reportOutstanding ctx unanalyzable state.FailedExtensions outstandingFailures)

        // `LastCoverage` is the moment the process acquires test evidence — a
        // run completed and we know what it covered. Until a state carrying it
        // is supplied, the cache key refuses to let a cached BuildCompleted
        // assert a result this process never ran.
        let debtDuringFullRun = state.DebtDuringFullRun

        let receipt =
            PostRunStep.timed postRun "fold: decide receipt" (fun () ->
                decideReceipt
                    ctx
                    { State = state
                      Completed = completed
                      Launch = launch
                      Coverage = coverage
                      RepoRoot = repoRoot
                      RunnableProjects = runnableProjects
                      ObserveModelGeneration = observeModelGeneration })

        let currentInputTree = receipt.CurrentInputTree
        let currentModelGeneration = receipt.CurrentModelGeneration
        let currentInputs = receipt.CurrentInputs
        let standingEvidence = receipt.StandingEvidence
        let receiptTransition = receipt.ReceiptTransition
        let evidenceReceipt = receipt.EvidenceReceipt

        let state =
            PostRunStep.timed postRun "fold: record completion" (fun () ->
                recordCompletion endRun state completed launch assembled receipt unanalyzable)


        let settled =
            PostRunStep.timed postRun "fold: settle debt" (fun () ->
                settleDebt
                    { State = state
                      Completed = completed
                      Launch = launch
                      RunnableProjects = runnableProjects
                      CurrentInputTree = currentInputTree
                      CurrentModelGeneration = currentModelGeneration
                      DebtDuringFullRun = debtDuringFullRun
                      OwedTo = owedTo
                      NoUnrunnableCoverers = noUnrunnableCoverers
                      Debt = env.Debt })

        let aborted = settled.Aborted
        let projectPassed = settled.ProjectPassed
        let coveredByConstruction = settled.CoveredByConstruction
        let debt = settled.Debt
        let absorbedProjects = settled.AbsorbedProjects
        let executedFullSuite = settled.ExecutedFullSuite
        let earnsBaseline = settled.EarnsBaseline

        // The tree the baseline was earned on, kept only when the index was sealed at exactly
        // that tree: a restarted daemon diffs its cold scan from it (AUTOMATION-1587). The
        // trees earlier full passes earned are kept beside it, so a revert that re-seals the
        // index at one of them still leaves a restart a tree to diff from (AUTOMATION-1682).
        let debt =
            match debt.Baseline with
            | Some baseline when earnsBaseline ->
                let sealedReceiptTree =
                    receipt.CurrentTree
                    |> Option.map _.Identity
                    |> Option.filter (fun tree -> state.SealedTree = Some tree)

                { debt with
                    Baseline =
                        Some(
                            FullSuiteBaseline.succeed
                                state.Debt.Baseline
                                { baseline with
                                    Tree = sealedReceiptTree }
                        ) }
            | _ -> debt

        let earned =
            PostRunStep.timed postRun "fold: earned evidence" (fun () ->
                earnedEvidenceOf
                    ctx
                    { State = state
                      Started = started
                      Completed = completed
                      Launch = launch
                      Debt = debt
                      RunnableProjects = runnableProjects
                      Aborted = aborted
                      ExecutedFullSuite = executedFullSuite
                      ProjectPassed = projectPassed
                      Unanalyzable = unanalyzable
                      OutstandingFailures = outstandingFailures
                      CurrentInputTree = currentInputTree
                      CurrentInputs = currentInputs
                      CurrentModelGeneration = currentModelGeneration
                      ReceiptTransition = receiptTransition
                      EvidenceReceipt = evidenceReceipt
                      StandingEvidence = standingEvidence })

        let state =
            { state with
                Debt = debt
                Earned = earned
                PendingForceRunProjects = Set.difference state.PendingForceRunProjects absorbedProjects
                // A completed run is this session's baseline from now on.
                CheckoutBaseline = None
                CoveredFullSuite =
                    match launch.InputTree with
                    | Some tree when coveredByConstruction -> Some(tree, currentModelGeneration)
                    | _ -> None }

        // A run that earns the baseline passed every configured project unfiltered, so
        // it also discharged any unknown debt (`settleDebt`), and `PrepareCommit` writes
        // the baseline logged here. A red full run earns none (AUTOMATION-1581), so a
        // baseline can no longer be earned while what is owed is still unknown.
        if earnsBaseline then
            let runId = completed.RunId.ToString("N")

            Logging.info
                "test-prune"
                $"Full-suite baseline recorded: run %s{runId} passed every configured project (%d{Set.count runnableProjects}); impact-filtered greens are now relative to it"

        // AUTOMATION-1587 step 2: the box-wide, per-TREE record of the same fact, in shadow.
        // Asked on EVERY completion, so the log counts both the baselines written and, for
        // each completion that wrote none, the condition that refused it.
        PostRunStep.timed postRun "fold: file baseline" (fun () ->
            env.FileBaseline
                ctx
                { RunId = completed.RunId.ToString("N")
                  EarnsBaseline = earnsBaseline
                  RunnableProjects = runnableProjects
                  SealedTree = state.SealedTree
                  ReceiptTree = receipt.CurrentTree |> Option.map (fun tree -> tree.Identity, tree.Entries) })

        // AUTOMATION-1623 (ADR-069): this completion may have moved the ledger's full-suite
        // tree or filed T's record, so what a restart would select is published again now.
        PostRunStep.timed postRun "fold: publish restart selection" (fun () ->
            env.PublishRestartSelection debt.Baseline)

        // The in-memory hot view must shed ONLY the committed symbols,
        // never the whole list — symbols left in the queue (mid-run
        // arrivals, projects that failed/aborted) must keep selecting
        // tests until a covering run passes. `queueAfterCommit` is the
        // post-commit queue; it drives the cleared ChangedSymbols.
        let queueAfterCommit = debt.PendingQueue
        let remainingChangedSymbols = queueAfterCommit |> Set.toList

        // Why the queue is still non-empty, in words. Symbols owed to projects
        // this daemon cannot run are not "waiting on build": no build will
        // discharge them, so the message names the projects and the way out.
        // A function: it queries each queued symbol, and only non-green
        // branches ask.
        let pendingDescription () =
            let elsewhere =
                if noUnrunnableCoverers.Value then
                    Set.empty
                else
                    owedElsewhere owedTo queueAfterCommit

            match Set.toList elsewhere with
            | [] -> $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)"
            | projects ->
                let names = projects |> String.concat ", "

                $"%d{Set.count queueAfterCommit} symbol(s) still owed to tests in %s{names}, which this \
                  daemon does not run: list them in `tests.projects`, or declare them in \
                  `tests.excluded` with a reason"


        // Whatever this run left owed is queued behind it as an intent by
        // the trigger that owed it (`enqueueImpactRun`, a queued `run-tests`),
        // and is delivered only after this fold is committed. Nothing is
        // launched from here.
        PostRunStep.timed postRun "fold: report outcome" (fun () ->
            reportRunOutcome
                ctx
                { Completed = completed
                  CarriedFailures = carriedFailures
                  QueueAfterCommit = queueAfterCommit
                  PendingDescription = pendingDescription
                  ReceiptTransition = receiptTransition
                  EvidenceReceipt = evidenceReceipt }
                testResults)

        let replies =
            match message with
            | CommandTestsFinished(_, _, _, reply, response) ->
                // Built from the results this fold kept, not the ones the run returned.
                [ reply, response (revokedProjects modelCarry launchedCompletion) ]
            | _ -> []

        return
            { state with
                LastResults = Some testResults
                // Only the files this run launched against are consumed; a
                // file that changed during the run selects the next one.
                ChangedFiles =
                    if coveredByConstruction then
                        []
                    else
                        state.ChangedFiles
                        |> List.filter (fun file -> not (List.contains file launch.ChangedFiles))
                ChangedSymbols = remainingChangedSymbols
                AffectedTests = Analyzed AffectedSelection.none
                Replies = replies }

    // An unavailable launch ran nothing. Its receipt is revoked and the fanout
    // it consumed is owed again. No run is queued: the next build or cohort
    // seal is what can make the artifacts or the host available, and it
    // launches with this debt.
    }
