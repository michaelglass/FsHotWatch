/// The plugin's fold over its events and its content cache key, over explicit envs.
module FsHotWatch.TestPrune.PluginFold

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







/// Where this process's extension refresh stands. One value, so a refresh can never be
/// running beside the fold while the process still reads as never having refreshed.
[<RequireQualifiedAccess>]
type internal ExtensionRefreshPhase =
    /// No refresh yet: the stored edges were written by an earlier process, over a tree
    /// that may differ, so the first flush owes one.
    | NeverRefreshed
    /// The restart's first refresh, running beside the fold (AUTOMATION-1441): its answer,
    /// and how long it took, once it finishes.
    | RunningBesideFold of Tasks.Task<ExtensionRefresh list * int64>
    /// This process has refreshed. A later flush refreshes again only when it indexed
    /// something or an extension failed (`FailedExtensions`).
    | Refreshed

/// How far a requested run's work has got, for the path that closes it when it is
/// cancelled: a run it started must be completed, and a run it returned is closed by the
/// fold of what it returned.
[<RequireQualifiedAccess>]
type internal RequestedRunProgress =
    /// No run started yet.
    | NotStarted
    /// This run started and has not returned.
    | Started of TestRunStarted
    /// The work returned its result; the fold closes the run.
    | Returned

/// The file-freshness sidecar a plugin holds, with the project model generation it was
/// last reconciled against (`None` before the first). Read and replaced as one value.
type internal FreshnessView =
    { Store: FileFreshness.Store
      ReconciledAt: int64 option }








/// A handler for one checked file under the current model.
type internal FileCheckedFold =
    PluginCtx<TestPruneMsg> -> TestPruneState -> int64 option -> FileCheckResult -> Async<TestPruneState>

/// A handler for a sealed batch under the current model.
type internal BatchCheckedFold =
    PluginCtx<TestPruneMsg> -> TestPruneState -> int64 option -> BatchChecked -> Async<TestPruneState>

/// A handler for a run's completion.
type internal RunFinishedFold =
    PluginCtx<TestPruneMsg>
        -> TestPruneState
        -> TestPruneMsg
        -> TestRunStarted
        -> TestRunCompleted
        -> TestRunLaunch
        -> Async<TestPruneState>

/// A requested run's launch.
type internal RunRequester =
    PluginCtx<TestPruneMsg>
        -> TestPruneState
        -> TestConfig list
        -> string option
        -> Tasks.TaskCompletionSource<string>
        -> TestPruneState

/// A requested run that cannot launch: its status, and what it owes back.
type internal UnavailableRun =
    PluginCtx<TestPruneMsg>
        -> TestPruneState
        -> string
        -> Set<string>
        -> Tasks.TaskCompletionSource<string> option
        -> TestPruneState

/// The event handlers the fold dispatches to (`updateWith`).
type internal EventHandlers =
    { OnFileChecked: FileCheckedFold
      OnBatchChecked: BatchCheckedFold
      OnBuildCompleted: PluginCtx<TestPruneMsg> -> TestPruneState -> BuildResult -> Async<TestPruneState>
      OnRunFinished: RunFinishedFold }

/// The run lifecycle steps the fold takes directly (`updateWith`).
type internal FoldSteps =
    { EndRun: bool -> TestPruneState -> TestPruneState
      ImpactRun: StateStep
      JoinRefresh: StateStep
      LaunchHeld: PluginCtx<TestPruneMsg> -> TestPruneState -> string -> TestPruneState
      LaunchRequested: StateStep
      RequestTestRun: RunRequester
      UnavailableRun: UnavailableRun }

/// What the plugin's fold reads (`updateWith`). The freshness view is reconciled through
/// `ReconcileFreshness` when the fold calls it.
type internal UpdateEnv =
    { RepoRoot: string
      TestConfigs: TestConfig list option
      RunnableProjects: Set<string>
      IndexedKey: string -> string option
      ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option
      ReconcileFreshness: int64 option -> unit
      Handlers: EventHandlers
      Steps: FoldSteps }

/// The fold: each event, after the refresh it may need, to the handler for its kind.
let internal updateWith
    (env: UpdateEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (event: PluginEvent<TestPruneMsg>)
    =
    let repoRoot = env.RepoRoot
    let testConfigs = env.TestConfigs
    let runnableProjects = env.RunnableProjects
    let indexedKey = env.IndexedKey
    let observeModelGeneration = env.ObserveModelGeneration
    let reconcileFreshness = env.ReconcileFreshness
    let onFileChecked = env.Handlers.OnFileChecked
    let onBatchChecked = env.Handlers.OnBatchChecked
    let onBuildCompleted = env.Handlers.OnBuildCompleted
    let onRunFinished = env.Handlers.OnRunFinished
    let endRun = env.Steps.EndRun
    let impactRun = env.Steps.ImpactRun
    let joinRefresh = env.Steps.JoinRefresh
    let launchHeld = env.Steps.LaunchHeld
    let launchRequested = env.Steps.LaunchRequested
    let requestTestRun = env.Steps.RequestTestRun
    let unavailableRun = env.Steps.UnavailableRun

    async {
        // Replies belong to the event that owes them; the previous event's were
        // resolved when its state was published.
        let state = { state with Replies = [] }

        // Only a check result folds while a refresh runs beside the fold: every
        // other event may read the extensions' edges or failures (AUTOMATION-1441).
        let state =
            match event with
            | PluginEvent.FileChecked _ -> state
            | _ -> joinRefresh ctx state

        let modelGeneration = observeModelGeneration ctx
        reconcileFreshness modelGeneration

        let state =
            if
                state.AnalysisModelGeneration <> modelGeneration
                && not state.PendingAnalysis.IsEmpty
            then
                // Pending compiler results belong to the model they were accepted
                // under. Retire them before a flush can write them into another
                // model's symbol index. Their changed symbols stay owed, and the
                // index can no longer vouch for what covers them, so every runnable
                // project owes a run.
                // Outcomes already recorded for the current model stay: a replayed
                // `FileChecked` records one without passing through this fold, and
                // dropping it here left the seal refusing a file no later check
                // re-analysed (AUTOMATION-1415).
                let currentOutcomes =
                    AnalysisFileEvidence.forModel modelGeneration state.AnalysisFiles

                Logging.info
                    "test-prune"
                    $"project model changed (%A{state.AnalysisModelGeneration} -> %A{modelGeneration}); retiring %d{state.PendingAnalysis.Count} project(s) of pending analysis, keeping %d{currentOutcomes.Count} file outcome(s) already recorded for the current model"

                // The retired analyses never reached the index. Where it does not
                // already hold the same analysis, it cannot vouch for the file
                // until the file is analysed and flushed again, and nothing this
                // process cached for it may replay.
                let retired =
                    state.PendingIdentities
                    |> Map.filter (fun relPath identity -> indexedKey relPath <> Some identity)
                    |> Map.keys
                    |> Set.ofSeq

                { state with
                    PendingAnalysis = Map.empty
                    PendingIdentities = Map.empty
                    Unindexed = Set.union state.Unindexed retired
                    AnalysisModelGeneration = modelGeneration
                    AnalysisFiles = currentOutcomes
                    CohortSeal = None
                    AnalysisReceipt = None
                    PendingForceRunProjects = Set.union state.PendingForceRunProjects runnableProjects }
            else
                state

        // Analysis folds only a result or seal published against the model this host
        // publishes now. One from a replaced model was admitted before the
        // replacement, and its cohort is retried against the current model. One
        // with no model was captured against none, so it cannot describe this one.
        let notCurrent (published: int64 option) =
            published.IsNone || published <> modelGeneration

        // Recorded before any arm can return early: whether a moved path is an
        // edit or the run's own write depends only on the watcher having seen it.
        let state =
            match event, state.InFlight with
            | PluginEvent.FileChecked result, Some run ->
                let relPath =
                    Path.GetRelativePath(repoRoot, AbsFilePath.value result.File).Replace('\\', '/')

                Logging.debug "test-prune" $"watcher reported %s{relPath} during the in-flight run"

                { state with
                    InFlight =
                        Some
                            { run with
                                WatcherReported = Set.add relPath run.WatcherReported } }
            | _ -> state

        match event with
        | PluginEvent.FileChecked result when notCurrent result.ModelGeneration ->
            Logging.info
                "test-prune"
                $"ignoring FileChecked for %s{AbsFilePath.value result.File} from model %A{result.ModelGeneration}; current %A{modelGeneration}"

            return state
        | PluginEvent.BatchChecked batch when notCurrent batch.ModelGeneration ->
            Logging.info
                "test-prune"
                $"ignoring BatchChecked from model %A{batch.ModelGeneration}; current %A{modelGeneration}"

            return state
        | PluginEvent.FileChecked result ->
            let! folded = onFileChecked ctx state modelGeneration result

            return
                { folded with
                    ColdScan = ColdScan.onFileChecked folded.ColdScan }

        | PluginEvent.BatchChecked batch ->
            let! folded = onBatchChecked ctx state modelGeneration batch

            match batch.Trigger with
            | BootScan ->
                return
                    { folded with
                        ColdScan = ColdScan.onBootScanFlushed folded.ColdScan }
            | InSessionBatch _ -> return folded

        | PluginEvent.BuildCompleted buildResult -> return! onBuildCompleted ctx state buildResult

        | Custom(TestsFinished(started, launchedCompletion, launchedAs) as message)
        | Custom(CommandTestsFinished(started, launchedCompletion, launchedAs, _, _) as message) ->
            return! onRunFinished ctx state message started launchedCompletion launchedAs
        | Custom(ArtifactsUnavailable(reason, owed, reply)) ->
            let message =
                $"Tests did not run because the preceding build left invalid artifacts: %s{reason}"

            return unavailableRun ctx state message owed reply

        | Custom(TestHostUnavailable(reason, owed, reply, None)) ->
            let message = $"Tests did not run because the test host could not start: %s{reason}"
            return unavailableRun ctx state message owed reply

        // The run started and its result is gone, so it proves nothing: it ends
        // Aborted, and what it would have discharged stays owed.
        | Custom(TestHostUnavailable(reason, owed, reply, Some started)) ->
            let _, completed = abortedRunLifecycle (Some started) reason
            ctx.EmitTestRunCompleted completed
            let message = $"The test run's result was discarded: %s{reason}"
            return unavailableRun ctx state message owed reply

        // Nobody is left to read this run, so it reports nothing: the status its
        // `Running` displaced is already back. It closes what it opened, ends the
        // run in flight, and hands back the fanout it consumed. The rest of its debt
        // was never discharged, so whatever next wants a run launches with it.
        | Custom(TestRunWithdrawn(owed, reply, opened)) ->
            let reason = "every client that wanted the run has gone"

            opened
            |> Option.iter (fun started ->
                let _, completed = abortedRunLifecycle (Some started) reason
                ctx.EmitTestRunCompleted completed)

            reply
            |> Option.iter (fun target -> target.TrySetResult(JsonSerializer.Serialize {| error = reason |}) |> ignore)

            return
                { endRun false state with
                    PendingForceRunProjects = Set.union state.PendingForceRunProjects owed
                    DebtDuringFullRun = Map.empty }

        | Custom OwedRunRequested ->
            if TestMode.launchesOwedRuns state.Mode then
                return state
            else
                let armed =
                    { state with
                        Mode = TestMode.armOwedRun state.Mode }

                if state.OwedRunHeld then
                    return launchRequested ctx armed
                else
                    Logging.info
                        "test-prune"
                        "A command asked for the owed test run: none is held yet, so the next build's run launches"

                    return armed

        | Custom(ScopeRequested mode) ->
            if TestMode.requestsFullSuite mode then
                Logging.info
                    "test-prune"
                    "Scope set to FULL SUITE — impact filtering disabled for subsequent runs in this daemon session"
            else
                Logging.info "test-prune" "Scope set to IMPACT-FILTERED (inner-loop default)"

            return { state with Mode = mode }

        | Custom(RuntimeCoverageFailed(project, reason)) ->
            // What is unknown is what THIS project's tests reach on the tree they ran
            // on, so it is this project's debt, not the suite's.
            Logging.warn
                "test-prune"
                $"runtime coverage for %s{project} could not be ingested: %s{reason}; %s{project} runs in full until a run of it ingests"

            return
                { state with
                    Debt =
                        { state.Debt with
                            CoverageNotIngested = Map.add project reason state.Debt.CoverageNotIngested } }

        | Custom ImpactRunRequested -> return impactRun ctx state
        | Custom(DiffWaitExpired id) ->
            match state.AwaitingDiff with
            | Some held when held.Id = id ->
                Logging.warn
                    "test-prune"
                    $"No check results were folded within %.0f{DiffWait.bound.TotalSeconds}s of the build that held its launch for them"

                return launchHeld ctx state "its wait expired, so it selects without the diff"
            | _ -> return state

        | Custom(RunTestsRequested(configs, filter, reply)) -> return requestTestRun ctx state configs filter reply

        | Custom(IndexFoldProbe folded) ->
            return
                { state with
                    ColdScan = ColdScan.onProbe folded state.ColdScan }

        | Custom(RunFailedTestsRequested(filter, reply)) ->
            match failedTestConfigs (defaultArg testConfigs []) state.LastResults state.OutstandingFailures with
            | Error msg ->
                reply.TrySetResult(JsonSerializer.Serialize({| error = msg |})) |> ignore
                return state
            | Ok [] ->
                reply.TrySetResult(JsonSerializer.Serialize({| error = "no matching test projects" |}))
                |> ignore

                return state
            | Ok configs -> return requestTestRun ctx state configs filter reply

        | _ -> return state
    }

/// What the plugin's cache key reads (`cacheKeyWith`).
type internal CacheKeyEnv =
    { RepoRoot: string
      RunnableProjects: Set<string>
      DependsOn: string list
      BaselineInvalidReason: VerificationDebt -> string option }

/// Pure-content cache key, built by the lifted `cacheKeyFor` so the per-arm input
/// dependencies are structural rather than a convention. The thunks are this
/// closure's live state; `cacheKeyFor` decides which arm forces which — and
/// `FileChecked`, the per-file probe, forces none.
let internal cacheKeyWith
    (env: CacheKeyEnv)
    (state: TestPruneState)
    (event: PluginEvent<TestPruneMsg>)
    : ContentHash option =
    let repoRoot = env.RepoRoot
    let runnableProjects = env.RunnableProjects
    let dependsOn = env.DependsOn
    let baselineInvalidReason = env.BaselineInvalidReason

    let changedSymbolsHash () =
        state.ChangedSymbols
        |> List.distinct
        |> List.sort
        |> String.concat "|"
        |> FsHotWatch.CheckCache.sha256Hex

    // The persisted needs-testing queue. `None` = empty, which both omits the
    // merkle entry (keeping the empty-queue green fast-path key byte-stable) and,
    // on BuildCompleted, is what makes the event cacheable at all: a green that
    // left symbols queued must re-run, never replay.
    let pendingQueueHash () =
        if state.Debt.RecoveryOutstanding then
            // An unreadable ledger is outstanding debt whose
            // membership is unknown. `None` would assert "provably nothing owed"
            // and make BuildCompleted cacheable, so a cached green written over
            // this same content merkle would replay with no test process running.
            // `Some` refuses cache participation outright, as a non-empty queue
            // does. A constant rather than a hash — there is nothing to hash.
            Some "unreadable-ledger"
        elif not (Map.isEmpty state.Debt.CoverageNotIngested) then
            // A project owes a whole-project run that ingests: a cached green
            // cannot pay it. Refused as a non-empty queue is.
            let owed = state.Debt.CoverageNotIngested |> Map.keys |> String.concat ","

            Some $"coverage-not-ingested:%s{owed}|%s{PendingVerification.hash state.Debt.PendingQueue}"
        elif Set.isEmpty state.Debt.PendingQueue then
            None
        else
            Some(PendingVerification.hash state.Debt.PendingQueue)

    // External-dependency salt: a content hash of the files matched by the
    // configured `dependsOn` globs. Editing a matched file (a DB migration
    // that changes the TEST database schema but no test SOURCE) changes this
    // hash → cache miss → genuine re-run. `None` when unconfigured or when
    // the globs match nothing, so the entry is omitted and existing on-disk
    // caches keep hitting.
    let dependsOnHash () =
        match externalDependencyHash repoRoot dependsOn with
        | "" -> None
        | h -> Some h

    // A full-suite run must never REPLAY an impact-filtered run's
    // cached verdict. Salting the key with the requested scope makes that
    // impossible rather than merely unlikely.
    let fullSuiteScopeHash () =
        // A run widened by a missing baseline is a full-suite
        // run too, and must not replay a filtered run's cached verdict.
        if
            TestMode.requestsFullSuite state.Mode
            || Option.isSome (baselineInvalidReason state.Debt)
        then
            Some "full"
        else
            None

    // No cache participation while a red no covering run has
    // passed is outstanding.
    let hasOutstandingFailures () =
        not (List.isEmpty state.OutstandingFailures)

    // No cache participation on BuildCompleted until a run in
    // THIS process has covered something.
    //
    // ANALYSIS-ONLY IS EXEMPT. With no runnable test projects this plugin makes no
    // test claim at all — its terminal status is a symbol-analysis summary — so
    // there is no verdict to launder, and gating it would throw away a working
    // cache to guard an assertion it never makes.
    let sessionHasTestEvidence () =
        Set.isEmpty runnableProjects
        || not (Set.isEmpty (RunCoverage.coveredProjects state.LastCoverage))

    // A full-repo walk of the project files, so it is a thunk
    // like the rest: `FileChecked` fires once per file on every scan and must
    // not pay for an input it never splices.
    let structureHash () = projectStructureHash repoRoot

    match event with
    // Dependency fanout still owed: the launch that runs it is this handler's, so
    // no replay AND no write, as for a non-empty pending queue. A replayed green
    // would skip the only event that launches it, and the fanout is not a key
    // input, so a later lookup could not tell the entry from one that ran it.
    | BuildCompleted _
    | Custom(TestsFinished _) when not (Set.isEmpty state.PendingForceRunProjects) -> None
    // A full-suite request earns its evidence from a real run, never a replay. The
    // run's own entry is still written, from its `TestsFinished` window.
    | BuildCompleted _ when TestMode.requestsFullSuite state.Mode -> None
    // A session that launches no owed run has no build outcome to replay or record.
    | BuildCompleted _ when not (TestMode.launchesOwedRuns state.Mode) -> None
    // No receipt binds the tree as it is now. The key names the changed symbols
    // and the project files, not the bytes, so an edit that changes neither (a
    // comment) computes the key of the green it follows. A replay would skip the
    // handler, and with it the only run that can earn evidence for this tree.
    | BuildCompleted BuildSucceeded when
        not (Set.isEmpty runnableProjects)
        && not (
            state.EvidenceReceipt
            |> Option.exists (fun evidence ->
                ReceiptInputTree.matches evidence.InputTreeHash (ReceiptInputTree.read repoRoot))
        )
        ->
        None
    | _ ->
        cacheKeyFor
            changedSymbolsHash
            pendingQueueHash
            dependsOnHash
            structureHash
            fullSuiteScopeHash
            (fun () -> ReceiptInputTree.read repoRoot)
            hasOutstandingFailures
            sessionHasTestEvidence
            event
