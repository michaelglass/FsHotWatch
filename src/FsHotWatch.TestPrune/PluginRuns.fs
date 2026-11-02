/// The plugin's run steps: the run-tests force run, the exclusive test-host run, a flush of pending analysis, an impact launch and a candidate state's commit.
module FsHotWatch.TestPrune.PluginRuns

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
open FsHotWatch.TestPrune.PluginFold

/// The `run-tests` force-run work async, launched under the "tests" key by the
/// `RunTestsRequested` fold. Every exit that returns yields a `CommandTestsFinished`,
/// whose fold delivers the earned terminal status and resolves `reply` once that is
/// published; cancellation resolves `reply` itself (the IPC command is awaiting it,
/// bounded).
///
/// Empty launch set: `run-tests` is a manual FORCE run (optionally
/// filtered to a subset / only-failed). It is NOT the impact-analysis
/// queue-draining path, and a filtered force-run may not cover every
/// queued symbol — so it commits NOTHING from the pending-verification
/// queue (over-testing is the safe direction). The queue drains through
/// the normal BuildCompleted impact flow.
let internal commandForceRunWith
    (env: RunEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    // The mode the force run launches under: it decides whether it records traces.
    (mode: TestMode)
    (configs: TestConfig list)
    (filter: string option)
    (reply: Tasks.TaskCompletionSource<string>)
    : Async<TestPruneMsg> =
    let repoRoot = env.Config.RepoRoot
    let configuredTestProjects = env.Config.ConfiguredTestProjects
    let launchDeadline = env.Config.LaunchDeadline
    let maxParallelProjects = env.Config.MaxParallelProjects
    let beforeRun = env.Config.BeforeRun
    let afterRun = env.Config.AfterRun
    let coveragePaths = env.Config.CoveragePaths
    let db = env.Db
    let awaitDiscoveryBeforeLaunch = env.Launch.AwaitDiscovery
    let compileInputsOf = env.Launch.CompileInputsOf
    let coverageIngestFailed = env.Launch.CoverageIngestFailed
    let logLaunch = env.Launch.LogLaunch
    let logSelectedBy = env.Launch.LogSelectedBy
    let observeModelGeneration = env.Launch.ObserveModelGeneration
    let observeTestInputs = env.Launch.ObserveTestInputs
    let tracesFor = env.Launch.TracesFor

    // A force-run launches exactly `configs`, each with NO class selection (it
    // passes `Map.empty` to `executeTests`), so each runs IN FULL — a plain
    // `dotnet fshw test-rerun` is therefore the unfiltered run that can clear ANY
    // outstanding red (the escape hatch that keeps the clear-only-what-it-covered rule
    // from wedging into a permanent stuck-red).
    //
    // A `--filter` passthrough is a different matter: `RunCoverage.ofRun` sees
    // `wasFiltered = true` on the results and declines to claim coverage for a
    // filter string whose reach it cannot compute. Projects NOT in `configs`
    // (`--only-failed`, `--projects`) are absent from the selection and so are
    // covered by nothing — exactly right, they did not run.
    let commandLaunch: TestRunLaunch =
        { InputTree = None
          ModelGeneration = observeModelGeneration ctx
          ProjectInputs = observeTestInputs ctx
          Symbols = Set.empty
          SymbolRevisions = Map.empty
          // A force-run is not launched from the changed files, so it consumes none.
          ChangedFiles = []
          TestHostStartedAt = None
          CoveringProjectsBySymbol = Map.empty
          RuntimeProjectsByFile = Map.empty
          Selection = configs |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList
          // A FORCE run has no impact selection behind it — nothing was widened past,
          // so there is no `check` reading to project. This is the
          // ESCALATING `confirm`'s path, and it already carries an EXECUTED
          // impact-scoped reading taken before the escalation.
          WouldHaveRun = None
          Reused = Map.empty
          Seeds = []
          ZeroSelection = ZeroSelection.NotAZero
          FullSuiteCause = FullSuiteCause.ForceRun
          SeedFanout = SeedFanoutReading.NoSelection
          TraceShadow = []
          SelectedBy =
            configs
            |> List.map (fun c ->
                { Project = c.Project
                  Causes = [ SelectionCause.ForceRun ]
                  SkippedClasses = [] }) }

    async {
        do! awaitDiscoveryBeforeLaunch ctx

        // The model is read after the wait, not when the run was requested.
        let commandLaunch =
            { commandLaunch with
                InputTree = ReceiptInputTree.readTree repoRoot
                ModelGeneration = observeModelGeneration ctx
                ProjectInputs = observeTestInputs ctx }

        logLaunch "forced" mode commandLaunch
        logSelectedBy commandLaunch

        let mutable progress = RequestedRunProgress.NotStarted

        let emittedStart () =
            match progress with
            | RequestedRunProgress.Started started -> Some started
            | RequestedRunProgress.NotStarted
            | RequestedRunProgress.Returned -> None

        let emitStarted started =
            progress <- RequestedRunProgress.Started started
            ctx.EmitTestRunStarted started

        try
            try
                let! results, started, completed, boundTree, testHostStartedAt, _ =
                    executeTests
                        db
                        None
                        emitStarted
                        repoRoot
                        launchDeadline
                        maxParallelProjects
                        ctx.Processes
                        (tracesFor ctx mode)
                        commandLaunch.InputTree
                        (compileInputsOf ctx)
                        beforeRun
                        (HookStep.asSubtasks ctx.StartSubtask ctx.EndSubtask)
                        coveragePaths
                        (coverageIngestFailed ctx)
                        afterRun
                        configuredTestProjects
                        configs
                        Map.empty
                        Map.empty
                        filter
                        // A `run-tests` force run is an explicit request to run: it
                        // neither reuses nor publishes evidence.
                        None

                // The counts come from the CTRF reports THIS RUN wrote, located by
                // the run id the daemon just handed back — declared membership, never
                // an mtime scan of a shared pile. An empty list from an existing
                // run-dir means the run executed no tests, and that is exactly the
                // fact the CLI has to be able to state. Only verdict evidence is
                // read, so the counts printed beside a status are the ones that
                // decided it.
                let runReports =
                    FsHotWatch.Ctrf.verdictReportsForRun repoRoot started.RunId
                    |> List.map (fun r -> r.Project, r.Summary)
                    |> Map.ofList

                // Returned (not Posted) so the framework's completion path
                // delivers it: the synchronous handler does the error reporting and
                // status updates a bare emit would skip, and the reply is resolved
                // only once that completion is published.
                progress <- RequestedRunProgress.Returned

                return
                    CommandTestsFinished(
                        started,
                        completed,
                        { commandLaunch with
                            InputTree = boundTree
                            TestHostStartedAt = Some testHostStartedAt },
                        reply,
                        formatTestResultsJson filter runReports results
                    )
            with ex ->
                // A `beforeRun` throw / `executeTests` fault
                // means the suite it guards NEVER RAN — that must surface
                // as a failure, never a stale prior green. The Aborted
                // lifecycle drives the TestsFinished handler to a Failed
                // status.
                Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                let started, completed = abortedRunLifecycle (emittedStart ()) ex.Message

                if (emittedStart ()).IsNone then
                    emitStarted started

                progress <- RequestedRunProgress.Returned

                return
                    CommandTestsFinished(
                        started,
                        completed,
                        commandLaunch,
                        reply,
                        JsonSerializer.Serialize({| error = ex.Message |})
                    )
        finally
            // Cancellation — daemon teardown, or every client that asked for this run
            // has gone — skips `with` but runs `finally`, and no completion will fold.
            let reason = "the run was cancelled before it completed"

            // Close the run this work opened. Subscribers track every started run
            // until its completion (Build defers every build while one is live),
            // so a started run must end. `Aborted` with no results is evidence of
            // nothing: it is never a pass.
            match progress with
            | RequestedRunProgress.Returned -> ()
            | RequestedRunProgress.Started started ->
                let _, completed = abortedRunLifecycle (Some started) reason
                ctx.EmitTestRunCompleted completed
                // Never leave a client that is still waiting on a reply that cannot come.
                reply.TrySetResult(JsonSerializer.Serialize({| error = reason |})) |> ignore
            | RequestedRunProgress.NotStarted ->
                reply.TrySetResult(JsonSerializer.Serialize({| error = reason |})) |> ignore
    }

/// Make a candidate's durable debt the record a restart reads, then publish it.
///
/// The marker goes first and comes off only in `Finalize`, after the candidate is
/// published: a restart that finds it cannot know which sidecars describe a published
/// state, and treats what is owed as unknown. Only what changed is written, unless an
/// earlier publication left the marker, in which case every sidecar is rewritten from
/// this candidate. While the debt is unknown nothing is written, the marker included: the unreadable
/// record is the honest one until a full suite recovers it.
///
/// Replies owed by the candidate are resolved last, so a command never acknowledges an
/// outcome that is not published.
let internal prepareCommitIn
    (repoRoot: string)
    (debtPublicationPath: string)
    (prior: TestPruneState)
    (candidate: TestPruneState)
    =
    async {
        let changed (read: TestPruneState -> 'T) =
            not (obj.ReferenceEquals(read prior, read candidate))
            && read prior <> read candidate

        let queueChanged = changed (fun state -> state.Debt.PendingQueue)
        let failuresChanged = changed (fun state -> state.OutstandingFailures)
        let obligationsChanged = changed (fun state -> state.Debt.RuntimeObligations)
        let baselineChanged = changed (fun state -> state.Debt.Baseline)
        let recoveryChanged = changed (fun state -> state.Debt.RecoveryOutstanding)
        let notIngestedChanged = changed (fun state -> state.Debt.CoverageNotIngested)
        let interrupted = File.Exists debtPublicationPath

        let publishes =
            interrupted
            || queueChanged
            || failuresChanged
            || obligationsChanged
            || baselineChanged
            || recoveryChanged
            || notIngestedChanged

        // While the debt is unknown nothing is written: the unreadable record is the
        // honest one until a full suite recovers it. No publication begins then, so
        // no marker is written either. One written then outlived the session, since
        // only a recovered state removes it, and the next daemon read an unfinished
        // publication that never happened. A marker an earlier publication left
        // stays, because the sidecars on disk are still that publication's.
        if publishes && not candidate.Debt.RecoveryOutstanding then
            FsHwPaths.atomicWriteAllText debtPublicationPath "verification debt publication in progress"
            let rewrite = interrupted || recoveryChanged

            if rewrite || queueChanged then
                PendingVerification.save repoRoot candidate.Debt.PendingQueue

            if rewrite || failuresChanged then
                OutstandingFailure.save repoRoot candidate.OutstandingFailures

            if rewrite || obligationsChanged then
                saveRuntimeCoverageObligations repoRoot candidate.Debt.RuntimeObligations

            if rewrite || notIngestedChanged then
                CoverageNotIngested.save repoRoot candidate.Debt.CoverageNotIngested

            match candidate.Debt.Baseline with
            | Some baseline when rewrite || baselineChanged -> FullSuiteBaseline.save repoRoot baseline
            | _ -> ()

        // Evidence, not debt: it records which projects ran whole over which compile
        // inputs, which stays true whatever is owed. A state that earned none leaves
        // the last record standing, since every entry is checked against the compile
        // inputs of the daemon that reads it.
        if changed (fun state -> state.Earned) then
            match candidate.Earned |> Option.map EarnedEvidence.wholeProjectInputs with
            | Some record when not (Map.isEmpty record) -> WholeProjectEvidence.save repoRoot record
            | _ -> ()

        return
            { Finalize =
                async {
                    if not candidate.Debt.RecoveryOutstanding then
                        // The runtime marker is written by a run before its failure
                        // reaches this owner, so only the state that recovered from
                        // unknown debt may remove it.
                        let markers =
                            if recoveryChanged then
                                [ runtimeCoverageRecoveryPath repoRoot; debtPublicationPath ]
                            else
                                [ debtPublicationPath ]

                        // `File.Delete` of an absent file is a no-op, but not in an
                        // absent directory: a repository that never wrote debt has none.
                        for marker in markers do
                            if File.Exists marker then
                                File.Delete marker

                    for reply, response in candidate.Replies do
                        reply.TrySetResult response |> ignore
                } }
    }

/// Claim the "tests" key and the shared artifact lease for `work`. `owed` is the
/// dependency fanout this launch consumes; an unavailable launch hands it back.
///
/// Result-first: the completion fold sheds only the symbols the run launched with, so
/// a file check or build dispatched during the run stays owed whether it folds before
/// the result or after it. Folding after can only cost a run: a BootScan cohort that
/// would have joined a full run in flight (`joinsFullRun`) finds none and queues its
/// own. It never lets less evidence discharge anything. The run emits test lifecycle
/// events this plugin does not subscribe to. Without this, a result stuck behind a
/// storm of checks holds the "tests" key, and its green, for as long as those folds
/// take.
let internal runTestHostExclusive
    (ctx: PluginCtx<TestPruneMsg>)
    (owed: Set<string>)
    (reply: Tasks.TaskCompletionSource<string> option)
    (workOf: PluginCtx<TestPruneMsg> -> Async<TestPruneMsg>)
    =
    // The run this launch started and has not closed. Its result's fold closes it; a
    // launch whose result never folds — its work returned, but its test host's
    // termination could not be established — closes it from its failure instead.
    // Subscribers track every started run until its completion (Build defers every
    // build while one is open), so a run left open wedges them for good.
    let opened = ref None

    let tracked =
        { ctx with
            EmitTestRunStarted =
                fun started ->
                    opened.Value <- Some started
                    ctx.EmitTestRunStarted started
            EmitTestRunCompleted =
                fun completed ->
                    match opened.Value with
                    | Some started when started.RunId = completed.RunId -> opened.Value <- None
                    | _ -> ()

                    ctx.EmitTestRunCompleted completed }

    let workFor =
        function
        | Ready ->
            // Cancelled once every client that wanted it has gone (when `workOf` is
            // cooperative-safe), it folds as withdrawn: it closes the run it opened and
            // hands back what it consumed, so nothing of it is left in flight.
            workOf tracked
            |> PluginWork.onAbandon (fun () -> TestRunWithdrawn(owed, reply, opened.Value))
            |> PluginWork.resultFirst
        | Invalid reason -> PluginWork.resultFirst (async { return ArtifactsUnavailable(reason, owed, reply) })

    // What this launch releases into the shared "build-artifacts" lease, which the
    // next claimant is handed. Only a verdict ABOUT the artifacts may change it.
    // A refusal that found them unusable carries that invalidity forward, so a
    // failed build stays sticky until one succeeds. A test host that would not
    // start says nothing about the build output: it releases the lease as valid,
    // so the next launch on an unchanged tree runs instead of being refused — with
    // the build named as the culprit — until something forces a real rebuild.
    let classify =
        function
        | ArtifactsUnavailable(reason, _, _) -> Invalid reason
        | TestHostUnavailable _ -> Ready
        | _ -> Ready

    let failureMessage (ex: exn) =
        TestHostUnavailable(ex.Message, owed, reply, opened.Value)

    match ctx.RunExclusiveShared "tests" "build-artifacts" workFor classify failureMessage with
    | SharedClaimed
    | SharedQueued -> Claimed
    | LocalSlotBusy -> SlotBusy

/// Take an extension refresh's answer, and how long it took, into the state.
type internal RefreshApplier =
    PluginCtx<TestPruneMsg> -> ExtensionRefresh list * int64 -> TestPruneState -> TestPruneState

/// The extensions' edge refresh, timed.
type internal TimedRefresh = ITestPruneExtension list -> unit -> ExtensionRefresh list * int64

/// The extension refresh a flush runs or joins: the cell that says whether one runs beside
/// the fold (read and written when the flush runs), and the steps around it.
type internal ExtensionRefreshSteps =
    { Phase: ExtensionRefreshPhase ref
      Extensions: ITestPruneExtension list option
      Apply: RefreshApplier
      Timed: TimedRefresh
      Join: StateStep }

/// What a flush of pending analysis reads from the plugin (`flushPendingWith`).
type internal FlushEnv =
    { Db: Database
      DbPath: string
      BusyTimeout: TimeSpan
      EnqueuePending: string list -> VerificationDebt -> VerificationDebt
      PersistQueueAdditions: VerificationDebt -> unit
      RuntimeCoverageSelection: string list -> RuntimeCoverageSelection
      Refresh: ExtensionRefreshSteps }

let internal flushPendingWith (env: FlushEnv) (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
    let db = env.Db
    let dbPath = env.DbPath
    let busyTimeout = env.BusyTimeout
    let enqueuePending = env.EnqueuePending
    let persistQueueAdditions = env.PersistQueueAdditions
    let runtimeCoverageSelection = env.RuntimeCoverageSelection
    let extensionRefresh = env.Refresh.Phase
    let extensions = env.Refresh.Extensions
    let applyRefresh = env.Refresh.Apply
    let refreshTimed = env.Refresh.Timed
    let joinRefresh = env.Refresh.Join

    // Capture OLD literal coupling before `RebuildProjects`
    // replaces a changed producer's outgoing graph. The unchanged test still
    // points at that old literal, but after the rebuild the producer does not;
    // querying only the changed producer would therefore select nothing.
    //
    // Queue the literal NODE, not a stale producer edge. It then follows the
    // ordinary pending-verification lifecycle and disappears after its covering
    // tests pass, while the rebuilt graph remains an exact description of current
    // source. This is bounded evidence carried across one destructive boundary,
    // not permanent false-positive coupling.
    let preRebuildSymbols =
        Set.union state.Debt.PendingQueue (Set.ofList state.ChangedSymbols)
        |> Set.toList

    let priorLiteralSeeds = db.GetPriorSharedLiteralSeeds preRebuildSymbols

    // Only a seed not already owed is new debt. Re-enqueuing an owed one would issue
    // it a newer revision and deny a covering run in flight its discharge.
    let newlyOwedLiterals =
        priorLiteralSeeds
        |> List.filter (fun symbol -> not (Set.contains symbol state.Debt.PendingQueue))

    let state =
        { state with
            Debt = enqueuePending newlyOwedLiterals state.Debt
            ChangedSymbols = (priorLiteralSeeds @ state.ChangedSymbols) |> List.distinct }

    if not priorLiteralSeeds.IsEmpty then
        Logging.info
            "test-prune"
            $"Preserved %d{priorLiteralSeeds.Length} pre-rebuild literal coupling seed(s) for impact selection"

    // Persist the pending queue BEFORE flushPendingAnalysis advances the
    // durable analysis snapshot. One write per flush (vs per FileChecked) — same
    // crash-safety, batch-size fewer disk writes. See `persistQueueAdditions`.
    persistQueueAdditions state.Debt

    let indexedSomething = not state.PendingAnalysis.IsEmpty

    // A refresh still running beside the fold reads the index this flush would write.
    let state = joinRefresh ctx state

    let flushedState =
        flushPendingAnalysis
            db
            dbPath
            busyTimeout
            (fun label -> ctx.DeclareBoundedWork label ImpactSelectionDeadline)
            state

    // The flush wrote these analyses and their identities in one transaction.
    let flushedState =
        let written = state.PendingIdentities |> Map.keys |> Set.ofSeq

        { flushedState with
            PendingIdentities = Map.empty
            Unindexed = Set.difference flushedState.Unindexed written }

    let flushedState =
        match extensions with
        | Some exts when
            not exts.IsEmpty
            && (indexedSomething
                || extensionRefresh.Value = ExtensionRefreshPhase.NeverRefreshed
                || not flushedState.FailedExtensions.IsEmpty)
            ->
            if not indexedSomething && flushedState.FailedExtensions.IsEmpty then
                // Owed only because this process has not refreshed: nothing it indexed
                // is in the answer, so it runs beside the fold.
                extensionRefresh.Value <- ExtensionRefreshPhase.RunningBesideFold(Tasks.Task.Run(refreshTimed exts))

                flushedState
            else
                extensionRefresh.Value <- ExtensionRefreshPhase.Refreshed

                // At info, because a refresh that holds the fold for minutes is
                // otherwise invisible.
                applyRefresh ctx (refreshTimed exts ()) flushedState
        | _ -> flushedState

    let runtimeSelection = runtimeCoverageSelection flushedState.ChangedFiles

    let runtimeObligations =
        if Map.isEmpty runtimeSelection.ProjectsByFile then
            flushedState.Debt.RuntimeObligations
        else
            mergeRuntimeCoverageObligations flushedState.Debt.RuntimeObligations runtimeSelection.ProjectsByFile

    if not (Map.isEmpty runtimeSelection.ProjectsByFile) then
        for KeyValue(file, projects) in runtimeSelection.ProjectsByFile do
            if not (Set.isEmpty projects) then
                let projectNames = projects |> Set.toList |> String.concat ", "

                Logging.info "test-prune" $"runtime coverage selected %s{file} -> %s{projectNames} (project-in-full)"

    reportRuntimeCoverageWidenings (Logging.warn "test-prune") runtimeSelection

    flushedState, runtimeObligations

/// An impact run, launched over `RunEnv` (`runTestsWithImpactWith`, partially applied).
type internal ImpactRunner =
    PluginCtx<TestPruneMsg> -> TestConfig list -> TestRunInputs -> bool -> Set<string> -> Async<TestPruneMsg>

/// What launching an impact run reads from the plugin (`launchImpactRunWith`). The refresh
/// phase is the plugin's cell, read when the launch runs.
type internal ImpactLaunchEnv =
    { RefreshPhase: ExtensionRefreshPhase ref
      FailuresAfter: ExtensionRefresh list -> Map<string, string> -> Map<string, string>
      LaunchScopeOf: TestRunInputs -> LaunchScope
      RunTestsWithImpact: ImpactRunner }

/// Claim the "tests" key for an impact launch from `launchState`. `Some` carries the
/// state recording what the claimed run will execute; `None` means the key is held.
///
/// A session that launches only requested runs (`TestMode.launchesOwedRuns`) claims
/// nothing: the debt stays owed, the fanout is kept, and the status says no run was
/// launched, so a settle does not wait on a run that will never start. A requested run
/// holding the key keeps its own status.
let internal launchImpactRunWith
    (env: ImpactLaunchEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (configs: TestConfig list)
    (launchState: TestPruneState)
    (hasCachedResults: bool)
    (fanout: Set<string>)
    =
    let extensionRefresh = env.RefreshPhase
    let failuresAfter = env.FailuresAfter
    let launchScopeOf = env.LaunchScopeOf
    let runTestsWithImpact = env.RunTestsWithImpact

    let inputs = TestRunInputs.ofState launchState
    // A refresh still running beside the fold: the run takes its failures before it
    // reads anything, off the fold (AUTOMATION-1441).
    let refreshing =
        match extensionRefresh.Value with
        | ExtensionRefreshPhase.RunningBesideFold refresh -> Some refresh
        | ExtensionRefreshPhase.NeverRefreshed
        | ExtensionRefreshPhase.Refreshed -> None

    let withRefreshed (inputs: TestRunInputs) =
        match refreshing with
        | None -> async.Return inputs
        | Some refresh ->
            async {
                let! outcomes, _ = Async.AwaitTask refresh

                return
                    { inputs with
                        FailedExtensions = failuresAfter outcomes inputs.FailedExtensions }
            }

    if not (TestMode.launchesOwedRuns inputs.Mode) then
        let detail =
            "no owed test run launched: this session runs only the tests a command requests"

        Logging.info "test-prune" detail

        if ctx.SlotHolder "tests" = SlotHolder.Free then
            ctx.ReportStatus(PluginStatus.verifiedNothingNow detail TimeSpan.Zero)

        Some
            { launchState with
                AwaitingDiff = None
                OwedRunHeld = true
                PendingForceRunProjects = Set.union launchState.PendingForceRunProjects fanout }
    else
        match
            // Cooperative-safe: a run held only by clients is cancelled once they have
            // all gone. Its test hosts run in its own process scope (reaped), its
            // result publishes only through the fold it returns, and the run it opened
            // is closed by its withdrawal fold. A run the daemon owes is never cancelled.
            runTestHostExclusive ctx fanout None (fun runCtx ->
                PluginWork.cooperativeSafe (
                    async {
                        let! inputs = withRefreshed inputs
                        return! runTestsWithImpact runCtx configs inputs hasCachedResults fanout
                    }
                ))
        with
        | Claimed ->
            Some
                { launchState with
                    AwaitingDiff = None
                    InFlight =
                        Some
                            { Scope = launchScopeOf inputs
                              Mode = inputs.Mode
                              WatcherReported = Set.empty } }
        | SlotBusy -> None
