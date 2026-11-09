/// Executing one test run: launching each configured project, collecting what it reports, and ingesting coverage, flakiness and traces once every project has finished.
module FsHotWatch.TestPrune.RunExecution

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

/// A finished run's test evidence, reconciled (`reconcileEvidence`).
type internal ReconciledEvidence =
    { ReusedNow: Map<string, TestEvidenceStore.Entry>
      Reconciled: (TestEvidenceRun.Plan * TestEvidenceRun.Reconciled) option
      EvidenceHeld: Map<string, TestEvidenceRun.Provenance> }

/// Re-key every reused project at completion: a reuse whose key moved is withdrawn and
/// deferred, and a held one reports its provenance as a pass.
let internal reconcileEvidence
    (collected: RunCollector)
    (logToCtx: string -> unit)
    (evidence: TestEvidenceRun.Plan option)
    (repoRoot: string)
    (runDir: string)
    : ReconciledEvidence =
    // TEST EVIDENCE, at completion. One fresh walk keys every candidate again. A reuse
    // whose key moved while the run was in flight no longer describes this tree: its
    // filed report and coverage are withdrawn and it reports a deferral, so this run
    // cannot go green on it and the next one launches it. A project that really ran
    // publishes its green only when it qualifies (`TestEvidenceRun.qualifies`).
    let reusedNow = collected.Read _.Materialized

    let reconciled =
        evidence
        |> Option.map (fun plan -> plan, TestEvidenceRun.reconcile plan repoRoot reusedNow)

    // A withdrawn reuse takes back everything it filed and reports a deferral; a held one
    // reports its green now, and only now.
    reconciled
    |> Option.iter (fun (_, r) ->
        for (project, reason) in r.Withdrawn do
            Logging.warn "test-prune" $"%s{project}: %s{reason}"
            logToCtx $"{project}: {reason}"
            TestEvidenceRun.deleteQuietly (Path.Combine(runDir, project + Ctrf.ReportSuffix))

            collected.Update(fun c ->
                { c with
                    CoverageInputs = c.CoverageInputs |> List.filter (fun i -> i.Project <> project) })

        let reported =
            (r.Withdrawn |> List.map (fun (project, reason) -> project, TestsDeferred reason))
            @ (r.Held
               |> Map.toList
               |> List.map (fun (project, entry) ->
                   project,
                   TestsPassed(
                       TestEvidenceRun.Provenance.describe (TestEvidenceRun.Provenance.ofEntry entry),
                       false,
                       TimeSpan.Zero
                   )))

        collected.Update(fun c ->
            { c with
                Results =
                    reported
                    |> List.fold (fun acc (project, result) -> Map.add project result acc) c.Results }))

    let evidenceHeld: Map<string, TestEvidenceRun.Provenance> =
        reconciled
        |> Option.map (fun (_, r) -> r.Held |> Map.map (fun _ entry -> TestEvidenceRun.Provenance.ofEntry entry))
        |> Option.defaultValue Map.empty

    { ReusedNow = reusedNow
      Reconciled = reconciled
      EvidenceHeld = evidenceHeld }

/// A finished run's coverage, ingested: invalidate what failed its receipt, ingest and emit
/// the cobertura, and report every failure as that project's debt.
let internal ingestRunCoverage
    (collected: RunCollector)
    (logToCtx: string -> unit)
    (runId: Guid)
    db
    repoRoot
    runDir
    enabledRatchetProjects
    configuredCoverageOutput
    coverageIngestFailed
    (coveragePathsByProject: Map<string, CoveragePaths>)
    (launchTree: ReceiptTree option)
    (reconciled: (TestEvidenceRun.Plan * TestEvidenceRun.Reconciled) option)
    (rerunProjects: Set<string>)
    reusedNow
    =
    // Coverage: now that ALL projects have finished, serially ingest each
    // project's raw runner cobertura into the TestPrune DB (max-merged,
    // symbol-relative) and emit the FULL DB ONCE to the single shared
    // cobertura file. Done here, outside Async.Parallel, so there is no
    // DB-write contention and no file-write race on the shared output.
    let collectedInputs, receiptFailures =
        collected.Read(fun c -> List.rev c.CoverageInputs, List.rev c.CoverageFailures)

    let receiptFailures =
        receiptFailures
        |> List.map (fun failure ->
            try
                invalidateCoverageReceiptFailure db failure
                failure
            with ex ->
                { failure with
                    Reason =
                        $"%s{failure.Reason}; prior coverage invalidation failed (%s{ex.GetType().Name}: %s{ex.Message})" })

    let coverageOutcome =
        ingestAndEmitCoverageForProjects
            db
            repoRoot
            (runId.ToString("N"))
            enabledRatchetProjects
            configuredCoverageOutput
            collectedInputs

    // Coverage missing (AUTOMATION-1371): the prior rows were kept; the run says so.
    for missing in coverageOutcome.CoverageMissing do
        logToCtx $"%s{missing.Project}: %s{missing.Reason}; its prior runtime coverage is kept"

    let coverageFailures = receiptFailures @ coverageIngestFailures coverageOutcome

    if not coverageFailures.IsEmpty then
        for failure in coverageFailures do
            coverageIngestFailed failure

        collected.Update(fun c ->
            { c with
                Results = applyCoverageIngestFailures coverageFailures c.Results })

    // TEST EVIDENCE: publish. Only now are this run's own results final — a project
    // whose coverage did not ingest is no longer green — so only now may a green leave
    // this checkout.
    reconciled
    |> Option.iter (fun (plan, r) ->
        let finalNow = collected.Read _.Results

        // A project re-run under starvation is not published: its green was earned on a
        // second attempt, which the stored evidence could not say.
        let runOf (candidate: TestEvidenceRun.Candidate) =
            TestEvidenceRun.producedRunOf
                (Map.tryFind candidate.Project finalNow)
                (rerunProjects.Contains candidate.Project)
                (fun result ->
                    let coveragePath = runCoveragePath runDir candidate.Project

                    { Project = candidate.Project
                      Result = result
                      Report =
                        TestEvidenceRun.readIfPresent (Path.Combine(runDir, candidate.Project + Ctrf.ReportSuffix))
                      CoverageRequired = coveragePathsByProject.ContainsKey candidate.Project
                      Coverage =
                        TestEvidenceRun.readIfPresent coveragePath
                        |> Option.map (fun text -> coveragePath, text) })

        let producer: TestEvidenceStore.Producer =
            { Worktree = Path.GetFullPath repoRoot
              RunId = runId.ToString("N")
              TreeHash = launchTree |> Option.map _.Identity |> Option.defaultValue "unbound"
              ProducerHash = plan.Wiring.ProducerHash
              EarnedAt = DateTimeOffset.UtcNow }

        let published = TestEvidenceRun.publish plan repoRoot r reusedNow runOf producer

        // Why each launched project filed nothing, one line apiece, at the default level: a
        // project that never gets evidence is otherwise indistinguishable from a defect.
        for line in TestEvidenceRun.withheldLines published do
            Logging.info "test-prune" line

        let tally = TestEvidenceRun.tally plan r published
        Logging.info "test-prune" tally
        logToCtx tally)

    ()

/// A run's coverage plan: each configured project's coverage paths, the projects in the
/// ratchet, and the cobertura output.
let internal coveragePlanOf
    (coveragePaths: (string -> CoveragePaths option) option)
    (configuredCoverageProjects: TestConfig list)
    =
    let coveragePathsByProject =
        configuredCoverageProjects
        |> List.choose (fun config ->
            coveragePaths
            |> Option.bind (fun pathsForProject -> pathsForProject config.Project)
            |> Option.map (fun paths -> config.Project, paths))
        |> Map.ofList

    let enabledRatchetProjects =
        coveragePathsByProject
        |> Map.toSeq
        |> Seq.choose (fun (project, paths) -> if paths.IncludeInRatchet then Some project else None)
        |> Set.ofSeq

    let configuredCoverageOutput =
        coveragePathsByProject
        |> Map.toSeq
        |> Seq.tryHead
        |> Option.map (fun (_, paths) -> paths.Cobertura)

    coveragePathsByProject, enabledRatchetProjects, configuredCoverageOutput

/// Begin a run: settle how its traces see it, say it once when traces record nothing, run
/// `beforeRun`, emit its start, and bind the tree it launches against.
let internal beginRun
    (ctx: PluginCtx<'msg> option)
    (traces: TraceRunHost option)
    (configs: TestConfig list)
    (configuredCoverageProjects: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    (excludedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    (runId: Guid)
    repoRoot
    beforeRun
    emitStarted
    feedsCompile
    launchTree
    trackHookStep
    =
    let skipsClasses =
        excludedClassesByProject
        |> Map.exists (fun _ classes -> not (List.isEmpty classes))

    let isFilteredRun =
        not affectedClassesByProject.IsEmpty || Option.isSome rawFilter || skipsClasses

    // Every configured project launched, none of them filtered: a full run whatever
    // mode launched it.
    let runsEveryProjectInFull =
        Option.isNone rawFilter
        && not skipsClasses
        && configuredCoverageProjects
           |> List.forall (fun configured ->
               configs |> List.exists (fun c -> c.Project = configured.Project)
               && (affectedClassesByProject.IsEmpty
                   || affectedClassesByProject
                      |> Map.tryFind configured.Project
                      |> Option.exists List.isEmpty))

    let traces =
        traces
        |> Option.map (fun host ->
            { host with
                TraceRuntime =
                    { host.TraceRuntime with
                        RunsEveryProjectInFull = runsEveryProjectInFull } })

    // Said once per run, loudly: every project below then stores and logs the same
    // reason as its refusal.
    traces
    |> Option.bind (fun host -> TraceRun.unrecordedReason host.TraceRuntime)
    |> Option.iter (fun reason -> Logging.warn "test-prune" $"traces: this run records none — %s{reason}")

    let primaryLabel =
        if isFilteredRun then
            $"running %d{configs.Length} selected test projects"
        else
            $"running full suite (%d{configs.Length} projects)"

    let startedAt = DateTime.UtcNow

    ctx |> Option.iter (fun c -> c.StartSubtask PrimarySubtaskKey primaryLabel)
    // `TestRunStarted` is emitted by the CALLER (which receives `started` in the
    // returned tuple), so the synchronous `TestsFinished` handler can fire it inside
    // the cache-write capture window.
    let started: TestRunStarted = { RunId = runId; StartedAt = startedAt }
    emitStarted started

    // The tree this run's receipt binds to: re-read once `beforeRun` has done its
    // generating, so its own output does not count as an edit made mid-run. `settled`
    // says whether that is the tree the tests run against: no `beforeRun`, or one that
    // moved nothing a build compiles. Test evidence is keyed only over a settled tree.
    let boundTree, settled =
        match beforeRun with
        | Some setup ->
            Logging.info "test-prune" "Running beforeRun setup..."
            setup runId trackHookStep
            Logging.info "test-prune" "beforeRun complete"

            let afterSetup = ReceiptInputTree.readTree repoRoot

            let binding, why =
                ReceiptInputTree.afterBeforeRun (feedsCompile ()) launchTree afterSetup

            why |> Option.iter (Logging.info "test-prune")

            let settled =
                match binding, afterSetup with
                | Some bound, Some after -> bound.Identity = after.Identity
                | _ -> false

            binding, settled
        | None -> launchTree, true

    traces, started, boundTree, settled

/// Conclude a run whose groups have all finished: record flakiness, ingest its traces,
/// tidy the run directories, stop the clock and close the primary subtask, and build the
/// results and the completion the caller emits.
let internal concludeRun
    (ctx: PluginCtx<'msg> option)
    (collected: RunCollector)
    (traceIngestion: TraceIngestion option)
    (repoRoot: string)
    (runId: Guid)
    (sw: Stopwatch)
    (configuredCoverageProjects: TestConfig list)
    : Async<TestResults * TestRunCompleted> =
    async {
        // Flakiness: ONE parse + ONE rewrite of the history file for the whole run,
        // with every project's records — not one per project, and not racing itself
        // from inside Async.Parallel. Best-effort: the history is a diagnostic, so a
        // failure to record it must never fail the run that produced it.
        let collectedFlakiness = collected.Read _.Flakiness

        if not collectedFlakiness.IsEmpty then
            try
                Flakiness.appendRecords (Flakiness.historyPath repoRoot) 20 collectedFlakiness
            with
            | :? IOException
            | :? UnauthorizedAccessException
            | :? JsonException as ex -> Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"

        // Traces: every project still being stored, and any that never settled, before
        // `tidyRunsDir` can rotate the run directory holding the dumps. Each ingestion first
        // waits, bounded, for this plugin's mailbox to fold the analysis events admitted
        // before it began, so the traces join against the index those events write. The
        // folds never wait on this run, whose result is posted only after it returns. It
        // never throws, and nothing it does reaches `cumulative`: a trace failure is a log
        // line and a stored row, never a changed result.
        match traceIngestion with
        | Some ingestion -> do! ingestion.Finish()
        | None -> ()

        // Bound what `.fshw/test-runs/` retains, and purge the DEAD `.log` format.
        // Runs AFTER this run's reports were written, so the
        // evidence the verdict is about to point at is always among the survivors.
        Ctrf.tidyRunsDir repoRoot Ctrf.RetainedRuns

        sw.Stop()

        let finalResults = collected.Read _.Results

        let testResults =
            { Results = finalResults
              Elapsed = sw.Elapsed }

        ctx |> Option.iter (fun c -> c.EndSubtask PrimarySubtaskKey)

        // `TestRunCompleted` is emitted by the CALLER (the synchronous Custom handler) so
        // it lands in EmittedEvents for cache replay; returned in the tuple instead. The
        // matching start was emitted before any host could mutate its input artifacts.
        // `Outcome = Normal` means the run completed naturally — per-project pass/fail
        // lives in Results, and Aborted is reserved for cancellation/timeout/crash.
        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = sw.Elapsed
              Outcome = Normal
              Results = finalResults
              Verification =
                verificationWithin
                    (configuredCoverageProjects |> List.map (fun c -> c.Project) |> Set.ofList)
                    finalResults }

        return testResults, completed
    }

/// Execute test configs with optional affected classes for filtering. Handles beforeRun,
/// coveragePaths, process execution, result storage. `rawFilter` is a passthrough filter
/// string (from the run-tests command) that bypasses the template.
///
/// Emission contract (when `ctx` is Some):
///   1. `TestRunStarted` once, before any group begins.
///   2. `TestProgress` once per group as it completes, carrying only that
///      group's projects as a delta.
///   3. `TestRunCompleted` once, after all groups finish, carrying the full
///      cumulative Results plus an Outcome.
/// All three share a single RunId generated at the start of the run.
///
/// `ctx = None` (a one-off command) fires no lifecycle events — the caller just gets the
/// final TestResults — and also disables the skip-on-stale shortcut, so a manual run is
/// never deadlocked by a stuck dirty bit. See the staleness branch below.
let internal executeTests
    (db: Database)
    (ctx: PluginCtx<'msg> option)
    (emitStarted: TestRunStarted -> unit)
    (repoRoot: string)
    (launchDeadline: TimeSpan)
    // `tests.maxParallelProjects`: at most this many groups in flight, longest-expected
    // first; `None` launches every group at once.
    (maxParallelProjects: int option)
    // This session's lane of its repository host's process scheduler: each group holds
    // one of its slots while it runs (AUTOMATION-682). `None` for a per-worktree daemon.
    (processes: FsHotWatch.WorkScheduler.Lane option)
    // `tests.traces`, resolved for this run's mode, with the plugin's activity log (which
    // a force run's `ctx = None` would otherwise lose); `None` when not configured, which
    // leaves every launch exactly as it is without traces.
    (traces: TraceRunHost option)
    // The input tree the run was launched against, for the traces it records and the
    // receipt's re-binding once `beforeRun` is done.
    (launchTree: ReceiptTree option)
    // Which repo-relative paths a build consumed, observed once `beforeRun` finishes:
    // decides whether the receipt may re-bind to the tree the tests actually run against.
    (feedsCompile: unit -> string -> bool)
    // Receives the run id so the hook's own timings can be filed
    // under this run, beside the CTRF reports the verdict already reads.
    (beforeRun: (Guid -> HookStep.Tracker -> unit) option)
    // Where each step `beforeRun` runs is held while it runs: the plugin's subtasks, on
    // every path, so a wait or wedge inside the setup names the step it is on.
    (trackHookStep: HookStep.Tracker)
    (coveragePaths: (string -> CoveragePaths option) option)
    (coverageIngestFailed: CoverageIngestFailure -> unit)
    (afterRun: (TestResults -> unit) option)
    (configuredCoverageProjects: TestConfig list)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    // `checkSkipClasses` a project run whole leaves out, through its exclude template
    // (`buildExcludeArgs`). Empty for every run that skips nothing.
    (excludedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    // Makes the test-evidence plan (`TestEvidenceRun.plan`): the projects this run reuses
    // instead of launching, and every key it publishes against. Called once `beforeRun` has
    // settled the tree, so a key names the tree the tests run against (AUTOMATION-1510).
    // `None`, or a plan of `None`, runs every selected project exactly as it would without
    // a store.
    (evidencePlan: (unit -> TestEvidenceRun.Plan option) option)
    =
    async {
        Logging.info "test-prune" $"executeTests starting with %d{configs.Length} configs"
        let sw = Stopwatch.StartNew()
        let runId = Guid.NewGuid()

        // The run's own directory. Created NOW — before anything runs — so that a run
        // which executes but reports nothing is distinguishable from a run that never
        // happened: the first leaves an empty directory, the second leaves none.
        let runDir = Ctrf.runDir repoRoot runId

        try
            Directory.CreateDirectory(runDir) |> ignore
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex ->
            Logging.warn "test-prune" $"could not create the run directory %s{runDir}: %s{ex.Message}"

        let traces, started, boundTree, settled =
            beginRun
                ctx
                traces
                configs
                configuredCoverageProjects
                affectedClassesByProject
                excludedClassesByProject
                rawFilter
                runId
                repoRoot
                beforeRun
                emitStarted
                feedsCompile
                launchTree
                trackHookStep

        // TEST EVIDENCE: keyed now, over the tree the tests are about to run against. A
        // `beforeRun` that moved a compile input left binaries older than that tree, so no
        // key names what they run: such a run neither reuses nor publishes.
        let evidence =
            match evidencePlan with
            | None -> None
            | Some planOf when settled -> planOf ()
            | Some _ ->
                Logging.info
                    "test-prune"
                    "test evidence: none reused or published — beforeRun moved a compile input, so the tests run against a tree they were not built from"

                None

        let testHostStartedAt = DateTime.UtcNow

        let groups = configs |> List.groupBy (fun c -> c.Group)

        let coveragePathsByProject, enabledRatchetProjects, configuredCoverageOutput =
            coveragePlanOf coveragePaths configuredCoverageProjects

        // Impact analysis may have decided a project has no affected classes. Such a
        // project never launches, so it is neither preflighted (no artifacts worth
        // walking) nor deferred (nothing was going to run).
        let skipProjectOf (config: TestConfig) =
            not affectedClassesByProject.IsEmpty
            && not (affectedClassesByProject |> Map.containsKey config.Project)

        /// What an impact-skipped project reports. Skipped-due-to-impact-analysis is the
        /// strongest form of filtering — hence `wasFiltered = true`, so no caller mistakes
        /// it for a full run — and its coverage contribution is "nothing new". Elapsed is
        /// Zero because the test runner never started.
        ///
        /// Beside `skipProjectOf` rather than at either call site: the refusal path and the
        /// run loop both answer for skipped projects, and they have to answer the same way.
        let skippedResultOf (config: TestConfig) =
            config.Project, TestsPassed("", true, TimeSpan.Zero)

        // What the parallel project launches collect, under one lock (`RunCollector`): the
        // run-wide results, and the coverage, flakiness, trace, evidence and starvation
        // records that are used SERIALLY once the parallel section ends.
        let collected = RunCollector()

        // TRACES: each project's traces are stored as soon as its result is final, while the
        // rest of the run goes on (`TraceIngestion`); the run waits at its end only for what
        // is left. Joined against the index admitted before each project's ingestion.
        let! runCancellation = Async.CancellationToken

        let traceIngestion =
            traces
            |> Option.map (fun (host: TraceRunHost) ->
                TraceIngestion(
                    collected,
                    (fun runs ->
                        TraceRun.ingestAll
                            host.TraceRuntime
                            (TestPrune.Ports.toSymbolStore db)
                            host.IndexFold
                            (runId.ToString("N"))
                            (boundTree |> Option.map _.Identity)
                            (fun () -> ReceiptInputTree.read repoRoot)
                            runs
                            host.TraceLog),
                    runCancellation
                ))

        /// Write a line to the plugin's activity log when there is a host to write to.
        /// One binding for the whole run: the preflight, the refusal path and the per-
        /// config runner all report through it, and it is allocated once rather than
        /// per config.
        let logToCtx msg = ctx |> Option.iter (fun c -> c.Log msg)

        let foldAndEmit = foldAndEmitInto ctx collected runId

        // STALE-ARTIFACT PREFLIGHT. The freshness question is pure
        // file I/O and is answerable in seconds, so it is asked about EVERY config
        // HERE — before a single suite launches — rather than inside the parallel
        // per-config body, where a group-A project wrote its CTRF before group B was
        // examined and the refusal surfaced three minutes into a partial run that
        // read like progress.
        //
        // The preflight also REPAIRS what is provably repairable (a build-output copy
        // whose origin exists on disk), re-verifies the bytes afterwards, and records
        // every repair to a durable ledger that trips a circuit breaker on repetition.
        // See `StaleArtifactPreflight` for why exactly one stale case is healed.
        // TEST EVIDENCE. A project the plan reuses is not launched: its stored CTRF report
        // is filed in this run's directory, its stored coverage where the run would have
        // written it, and it reports a green that names where it was earned. A project
        // whose coverage the gate grades is reused only when the entry carries it.
        let reusable: Map<string, TestEvidenceStore.Entry> =
            match evidence with
            | None -> Map.empty
            | Some plan ->
                plan.Reused
                |> Map.filter (fun project entry ->
                    configs |> List.exists (fun c -> c.Project = project && not (skipProjectOf c))
                    && (not (coveragePathsByProject.ContainsKey project) || Option.isSome entry.Coverage))


        let reuseEnv: ReuseEnv =
            { RepoRoot = repoRoot
              RunDir = runDir
              Collected = collected
              LogToCtx = logToCtx
              Reusable = reusable
              CoveragePathsByProject = coveragePathsByProject }

        let tryReuse config = tryReuseWith reuseEnv config

        let preflight = preflightLaunch repoRoot configs skipProjectOf logToCtx

        // ALL-OR-NOTHING on a refusal, as two named alternatives. A run whose tree is
        // provably not built cannot reach a green verdict, so launching the projects that
        // happen to be fresh buys minutes of partial execution for signal the verdict
        // cannot use — which is the "reads like progress" half of the defect.
        //
        // ADR-016 kept this and changed the layer below it: the refusal
        // is still run-wide, but the preflight now repairs every copy its breaker did not
        // name, so a refused run leaves a better tree than it found and the refusal set
        // shrinks run over run. `preflight.Healed` above can therefore be non-empty on
        // this path — repairs happened, the launch did not.
        //
        // Both arms are bound as functions rather than inlined into the `if`, so the
        // 390-line group loop keeps the indentation it has always had. A run-level
        // guard should cost one line here, not re-flow every line it guards.

        let refusalEnv: RefusalEnv =
            { Configs = configs
              Preflight = preflight
              LogToCtx = logToCtx
              SkipProjectOf = skipProjectOf
              SkippedResultOf = skippedResultOf
              FoldAndEmit = foldAndEmit }

        let refuseWholeRun () = refuseWholeRunWith refusalEnv ()

        // `tests.maxParallelProjects`: with a cap, groups launch longest expected wall first
        // (from the retained CTRF reports), so the long ones are not left for last. Without
        // one every group launches at once and the order is moot, so no history is read.
        let scheduledGroups =
            match maxParallelProjects with
            | None -> groups
            | Some cap ->
                let ordered =
                    GroupSchedule.longestFirst
                        (fun (_, groupConfigs: TestConfig list) -> groupConfigs |> List.map (fun c -> c.Project))
                        (GroupSchedule.expectedWalls repoRoot)
                        groups

                let order = ordered |> List.map fst |> String.concat ", "
                Logging.info "test-prune" $"launching at most %d{cap} test group(s) at once, in order: %s{order}"
                ordered

        let projectLaunchEnv: ProjectLaunchEnv<'msg> =
            { Ctx = ctx
              RepoRoot = repoRoot
              RunDir = runDir
              LaunchDeadline = launchDeadline
              Traces = traces
              Collected = collected
              LogToCtx = logToCtx
              AffectedClassesByProject = affectedClassesByProject
              ExcludedClassesByProject = excludedClassesByProject
              RawFilter = rawFilter
              CoveragePathsByProject = coveragePathsByProject }

        let launchProjectOrRaise runToken config =
            launchProjectWith projectLaunchEnv runToken config

        // STARVATION. A project that declared `starvation` is sampled while it runs; a
        // timeout on a starved box is held back from the group's results and re-run alone
        // once every group has finished (`rerunStarved`). See `Starvation`.

        let launchProject = launchProjectGuarded launchProjectOrRaise logToCtx

        let selectionOf = selectionIn rawFilter affectedClassesByProject

        let launchSampled = launchSampledWith launchProject

        let watchEnv: WatchEnv =
            { Collected = collected
              Settled = fun project -> traceIngestion |> Option.iter (fun t -> t.Settled project)
              LogToCtx = logToCtx
              RunDir = runDir
              LaunchProject = launchProject
              LaunchSampled = launchSampled
              SelectionOf = selectionOf }

        let launchWatched runToken config =
            launchWatchedWith watchEnv runToken config

        let rerunEnv: RerunEnv =
            { RepoRoot = repoRoot
              RunDir = runDir
              Collected = collected
              LogToCtx = logToCtx
              LaunchSampled = launchSampled }

        let rerunStarved = rerunStarvedWith rerunEnv

        let groupsEnv: GroupsEnv =
            { ScheduledGroups = scheduledGroups
              Processes = processes
              MaxParallelProjects = maxParallelProjects
              SkipProjectOf = skipProjectOf
              SkippedResultOf = skippedResultOf
              TryReuse = tryReuse
              LaunchWatched = launchWatched
              FoldAndEmit = foldAndEmit }

        let runAllGroups () = runAllGroupsWith groupsEnv ()

        let! groupResults =
            if List.isEmpty preflight.Refusals then
                runAllGroups ()
            else
                refuseWholeRun ()

        // groupResults is the per-group return values; we ignore it because
        // `collected.Results` (populated under the lock inside foldAndEmit) is the
        // canonical run-wide aggregate.
        groupResults |> ignore

        // STARVATION: the held-back projects, re-run alone now that every group is done.
        let! rerunResults = rerunStarved ()

        if not rerunResults.IsEmpty then
            foldAndEmit rerunResults

        let rerunProjects = rerunResults |> List.map fst |> Set.ofList

        let reconciledEvidence =
            reconcileEvidence collected logToCtx evidence repoRoot runDir

        let reusedNow = reconciledEvidence.ReusedNow
        let reconciled = reconciledEvidence.Reconciled
        let evidenceHeld = reconciledEvidence.EvidenceHeld

        ingestRunCoverage
            collected
            logToCtx
            runId
            db
            repoRoot
            runDir
            enabledRatchetProjects
            configuredCoverageOutput
            coverageIngestFailed
            coveragePathsByProject
            launchTree
            reconciled
            rerunProjects
            reusedNow

        let! testResults, completed =
            concludeRun ctx collected traceIngestion repoRoot runId sw configuredCoverageProjects

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        Logging.info
            "test-prune"
            $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

        return testResults, started, completed, boundTree, testHostStartedAt, evidenceHeld
    }
