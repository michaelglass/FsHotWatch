/// A run's completion in stages: its results and failures, receipt, recorded state, settled debt, earned evidence and reported outcome.
module FsHotWatch.TestPrune.CompletionStages

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
open FsHotWatch.TestPrune.ImpactRun
open FsHotWatch.TestPrune.FileCheckedHandler
open FsHotWatch.TestPrune.CheckHandlers
open FsHotWatch.TestPrune.RunOutcomeReport

/// The debt accounting a run's completion reads (`commitPending`, `debtScope` and the rest).
type internal CompletionDebt =
    { CommitPending: Set<string> -> VerificationDebt -> VerificationDebt
      CoveringOf: string seq -> string -> Set<string>
      DebtScope: Lazy<Map<string, string>> -> (string -> Set<string>) -> string -> Set<string>
      IndexNamesOnlyRunnable: Lazy<Map<string, string>> -> bool
      OwedElsewhere: (string -> Set<string>) -> string seq -> Set<string>
      RevisionOf: VerificationDebt -> string -> int64 }

/// What a completion's earned evidence is decided from (`earnedEvidenceOf`).
type internal CompletionEvidenceFacts =
    { State: TestPruneState
      Started: TestRunStarted
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      Debt: VerificationDebt
      RunnableProjects: Set<string>
      Aborted: bool
      ExecutedFullSuite: bool
      ProjectPassed: string -> bool
      Unanalyzable: Map<string, UnanalyzableFile>
      OutstandingFailures: OutstandingFailure list
      CurrentInputTree: string option
      CurrentInputs: Map<string, string> option
      CurrentModelGeneration: int64 option
      ReceiptTransition: ReceiptTransition
      EvidenceReceipt: TestEvidenceReceipt option
      StandingEvidence: EarnedEvidence option }

/// The evidence a completion earns, or keeps from the standing receipt, and the log line
/// that describes it.
let internal earnedEvidenceOf (ctx: PluginCtx<TestPruneMsg>) (facts: CompletionEvidenceFacts) : EarnedEvidence option =
    let state = facts.State
    let started = facts.Started
    let completed = facts.Completed
    let launch = facts.Launch
    let debt = facts.Debt
    let runnableProjects = facts.RunnableProjects
    let aborted = facts.Aborted
    let executedFullSuite = facts.ExecutedFullSuite
    let projectPassed = facts.ProjectPassed
    let unanalyzable = facts.Unanalyzable
    let outstandingFailures = facts.OutstandingFailures
    let currentInputTree = facts.CurrentInputTree
    let currentInputs = facts.CurrentInputs
    let currentModelGeneration = facts.CurrentModelGeneration
    let receiptTransition = facts.ReceiptTransition
    let evidenceReceipt = facts.EvidenceReceipt
    let standingEvidence = facts.StandingEvidence

    // What this completion EARNED. What it left OWED is a refusal it
    // carries: a proof that records a red is still a proof of what ran.
    //
    // Owed is what THIS run did not cover, not everything the queue holds
    // at this instant. A cold tree queues obligations while the run is
    // already executing — the build fires the full suite first and the
    // scan's file events land during it — so they are absent from the
    // launch snapshot and survive the launch-scoped retirement above. A run
    // that executed every runnable project IN FULL and passed, over the
    // tree it launched against, covered those files whenever they were
    // queued; refusing them turned a green check into exit 2 with nothing
    // failing anywhere. A run that did not (a project absent, failed, or
    // filtered) covers nothing extra, and every one of them still refuses.
    let coveredEverythingRunnable =
        executedFullSuite
        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
        && runnableProjects |> Set.forall projectPassed

    let owedSymbols =
        if coveredEverythingRunnable then
            Set.empty
        else
            debt.PendingQueue

    let owedRuntimeObligations =
        if coveredEverythingRunnable then
            0
        else
            debt.RuntimeObligations |> Map.values |> Seq.sumBy Map.count

    let pendingObligations =
        Set.count owedSymbols
        + owedRuntimeObligations
        + unanalyzable.Count
        + outstandingFailures.Length
        + Map.count debt.CoverageNotIngested
        + (if debt.RecoveryOutstanding then 1 else 0)

    // A completion that leaves the receipt standing — a narrower run, or a
    // quiet one that found the tree already verified — leaves the evidence
    // THAT receipt's run earned standing with it: the verdict grades the
    // receipt's run and looks its evidence up by run id. Under a model,
    // `classify` keeps a receipt only when this evidence is held.
    //
    // Including a quiet completion that folds while an obligation is owed
    // (symbols a scan delivered while it was in flight). It used to publish
    // an evidence of its own, refusing for the obligation, beside the
    // receipt it kept: the two named different runs, and the narrower run
    // that then discharged the obligation found no evidence for the
    // receipt's run to keep and published its own, so `check` graded the
    // full suite and found nothing vouching for it.
    //
    // A receipt that covers the whole suite on the tree it is bound to has
    // run every test that tree has, so an obligation owed on that same tree
    // is one it already verified, as it is after a narrower run. A
    // narrower receipt cannot say so: its evidence refuses for what is owed.
    let keptWithReceipt =
        match receiptTransition, evidenceReceipt with
        | ReceiptTransition.Noop, Some receipt when
            pendingObligations > 0
            && not (RunCoverage.coversWholeSuite (Set.toList runnableProjects) receipt.Coverage)
            ->
            standingEvidence
            |> Option.map (
                EarnedEvidence.withRefusals [ $"%d{pendingObligations} verification obligation(s) remain pending" ]
            )
        | ReceiptTransition.Noop, _
        | ReceiptTransition.Narrower, _ -> standingEvidence
        | ReceiptTransition.Earned _, _
        | ReceiptTransition.Revoked _, _ -> None

    let earned =
        match keptWithReceipt, completed.Verification with
        | Some kept, _ -> Some kept
        | None, NoProjectsSelected when
            pendingObligations = 0
            && not aborted
            && launch.ZeroSelection <> ZeroSelection.NotAZero
            && launch.ModelGeneration = currentModelGeneration
            ->
            // Nothing needed running, so this completion proves nothing new.
            // The evidence it was already verified by stands, while it belongs
            // to the current model.
            EarnedEvidence.retainedForZeroSelection currentModelGeneration currentInputs state.Earned
        | None, NoProjectsSelected
        | None, AllZeroMatch _
        | None, NothingExecuted
        | None, Ran _ ->
            EarnedEvidence.fromCompletion
                started.RunId
                launch.ModelGeneration
                currentModelGeneration
                currentInputs
                (ObservedModel.projectFiles ctx.ProjectGraph)
                runnableProjects
                pendingObligations
                (TestPruneState.coverageEvidence state)
                completed

    Logging.info
        "test-prune"
        (describeCompletionEvidence
            completed.RunId
            launch.ModelGeneration
            currentModelGeneration
            receiptTransition
            evidenceReceipt
            earned)

    match receiptEvidenceMismatch currentModelGeneration evidenceReceipt earned with
    | Some mismatch -> Logging.warn "test-prune" mismatch
    | None -> ()

    earned

/// What a completion's results and failures are assembled from.
type internal CompletionRunFacts =
    { State: TestPruneState
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      RepoRoot: string
      RunnableProjects: Set<string>
      Traces: TraceWiring option }

/// A completed run's results and the failures it leaves outstanding (`assembleRunResults`).
type internal AssembledRunResults =
    { CompletedRuns: Guid list
      TestResults: TestResults
      Coverage: RunCoverage
      CheckReach: CheckReach
      ConditionalFailureRecall: FailureRecall
      CarriedFailures: OutstandingFailure list
      OutstandingFailures: OutstandingFailure list }

/// What a shadow completion reads of a run. A project ran every test when it launched
/// unfiltered, ran, and was not answered from the evidence store: only then are all of its
/// failures in this report. The failed tests' trace keys are `None` without a complete
/// report, so no miss is judged and no recall measured.
let internal shadowFacts
    (selection: Map<string, ProjectSelection>)
    (ran: Set<string>)
    (reused: Set<string>)
    (failedTests: Result<OutstandingFailure list, string>)
    : (string -> bool) * string list option =
    let ranInFull (project: string) =
        selection.TryFind project = Some ProjectInFull
        && ran.Contains project
        && not (reused.Contains project)

    let failedKeys =
        failedTests
        |> Result.toOption
        |> Option.map (
            List.choose (fun f ->
                match f.Class, f.Method with
                | Some cls, Some meth -> Some(TraceShadow.failureKey f.Project cls meth)
                | _ -> None)
        )

    ranInFull, failedKeys

/// The first stage of a run's completion: its results, coverage and check reach, the
/// shadow trace comparison, and the failures it found, retired and carried.
let internal assembleRunResults (facts: CompletionRunFacts) : AssembledRunResults =
    let state = facts.State
    let completed = facts.Completed
    let launch = facts.Launch
    let repoRoot = facts.RepoRoot
    let runnableProjects = facts.RunnableProjects
    let traces = facts.Traces

    // This run joins the session ledger HERE, so every state this fold
    // returns carries it. A run that completed is a run whose directory a
    // reader may need, whatever the handler goes on to decide about its
    // results.
    //
    // Except a skip: a launch that selected nothing and ran nothing wrote
    // no directory, and counting it tells a reader the tests ran twice.
    let skipped =
        launch.ZeroSelection <> ZeroSelection.NotAZero && Map.isEmpty completed.Results

    let completedRuns =
        if skipped then
            state.CompletedRuns
        else
            completed.RunId
            :: (state.CompletedRuns
                |> List.filter (fun id -> id <> completed.RunId)
                |> List.truncate (SessionRunLedger - 1))

    // Apply error reporting synchronously here too — live emission from
    // the async wouldn't be captured for cache replay.
    let testResults: TestResults =
        { Results = completed.Results
          Elapsed = completed.TotalElapsed }

    // A run may clear ONLY what it COVERED.
    //
    // Two properties must hold together: the ledger is REWRITTEN each
    // cycle (so a superseded red cannot linger), and it is
    // rewritten from the OUTSTANDING set — what this run found PLUS every
    // earlier red it did not cover. Rewriting from this run's failures
    // alone lets a narrower run's green erase reds it never executed.
    // Coverage comes from the run's own launch selection, so a red dies
    // only to evidence that executed it, and dies the moment that evidence
    // exists (no stuck-red).
    //
    // The launch selection cannot express the reach of a raw `--filter`
    // passthrough — it records every project as `ProjectInFull` — so hand
    // `ofRun` the classes the run's OWN report shows passing.
    // Read from THIS run's directory only, and empty
    // whenever the report is missing or incomplete.
    let coverage =
        RunCoverage.ofRun launch.Selection completed.Results (passedClassesOfRun repoRoot completed.RunId)

    let passedTests = passedTestsOfRun repoRoot completed.RunId

    // A red unfiltered project clears a prior red only for a test this run's report
    // shows passing; `coverage` itself, which `confirm` reads, is unchanged.
    let redInFull = RunCoverage.redInFull completed.Results

    // The run log a project-level cause points at. Named
    // only when it is on disk: `Written` is otherwise reserved for the
    // code holding the open handle, and a message must never point at
    // a log nobody wrote.
    let runLogOf (project: string) : RunLog.Ref =
        let path = RunLog.pathIn (Ctrf.runDir repoRoot completed.RunId) project

        if File.Exists path then
            RunLog.Ref.Written path
        else
            RunLog.Ref.Unavailable "no output log is on disk for this run"

    // The failed rows and declared classes of the CTRF report each
    // project wrote in this run's directory, for naming a red the console
    // line did not and for splitting every red's name.
    let reportOf =
        let reports =
            Ctrf.reportsForRun repoRoot completed.RunId
            |> List.map (fun report -> report.Project, report.Path)
            |> Map.ofList

        fun (project: string) ->
            match Map.tryFind project reports with
            | None -> ReportedFailures.none
            | Some path ->
                try
                    ReportedFailures.ofReport (File.ReadAllText path)
                with
                | :? IOException
                | :? UnauthorizedAccessException -> ReportedFailures.none

    let foundFailures = failuresOf runLogOf reportOf state.TestClassFiles testResults

    let failedTests = failedTestsOfRun repoRoot completed.RunId testResults

    let checkReach, conditionalFailureRecall =
        failedTests |> CheckReach.classifyEvidence launch.WouldHaveRun

    // Shadow trace selection: store the launch's comparison with this
    // run's failed tests. A run with no complete report judges no miss.
    match traces with
    | Some wiring when not launch.TraceShadow.IsEmpty ->
        let ranInFull, failedKeys =
            shadowFacts launch.Selection (Set.ofSeq completed.Results.Keys) (Set.ofSeq launch.Reused.Keys) failedTests

        TraceShadow.atCompletion
            wiring.Policy
            repoRoot
            launch.TraceShadow
            (completed.RunId.ToString "N")
            ranInFull
            failedKeys
            (Logging.info "test-prune")
    | _ -> ()

    // Classified HERE, against THIS run's failures and the
    // selection retained at its launch. Nothing extra runs and nothing is
    // re-read: both inputs are already in hand.

    // A prior red naming a class this run selected, in a project that then
    // passed with a complete report holding no row of that class, names no
    // test the project has. No pass can ever retire it, so it goes now.
    let absentFailures, priorFailures =
        OutstandingFailure.partitionAbsent
            coverage
            (classesRunOfPassedProjects repoRoot completed.RunId completed.Results)
            state.OutstandingFailures

    if not absentFailures.IsEmpty then
        Logging.info
            "test-prune"
            $"%d{absentFailures.Length} failure(s) from an earlier run name a test class this run selected and its project does not have (%s{OutstandingFailure.summarize absentFailures}) — retired, since no run can ever execute them"

    let carriedFailures =
        OutstandingFailure.carriedOver runnableProjects coverage redInFull passedTests priorFailures

    let outstandingFailures =
        OutstandingFailure.carry runnableProjects coverage redInFull passedTests foundFailures priorFailures

    if not carriedFailures.IsEmpty then
        Logging.info
            "test-prune"
            $"%d{carriedFailures.Length} failure(s) from an earlier run were NOT covered by this one (%s{OutstandingFailure.summarize carriedFailures}) — they stay RED until a run that executes them passes"

    { CompletedRuns = completedRuns
      TestResults = testResults
      Coverage = coverage
      CheckReach = checkReach
      ConditionalFailureRecall = conditionalFailureRecall
      CarriedFailures = carriedFailures
      OutstandingFailures = outstandingFailures }

/// What a completion's receipt transition is decided from.
type internal ReceiptFacts =
    { State: TestPruneState
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      Coverage: RunCoverage
      RepoRoot: string
      RunnableProjects: Set<string>
      ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option }

/// The tree and model a completion is judged against, and what it does to the receipt.
type internal ReceiptOutcome =
    { CurrentTree: ReceiptTree option
      CurrentInputTree: string option
      CurrentModelGeneration: int64 option
      CurrentInputs: Map<string, string> option
      StandingEvidence: EarnedEvidence option
      ReceiptTransition: ReceiptTransition
      EvidenceReceipt: TestEvidenceReceipt option }

/// Read the tree and model as they stand now, classify what the completion does to the
/// test-evidence receipt, log a revocation and the paths that moved, and apply it.
let internal decideReceipt (ctx: PluginCtx<TestPruneMsg>) (facts: ReceiptFacts) : ReceiptOutcome =
    let state = facts.State
    let completed = facts.Completed
    let launch = facts.Launch
    let coverage = facts.Coverage
    let repoRoot = facts.RepoRoot
    let runnableProjects = facts.RunnableProjects
    let observeModelGeneration = facts.ObserveModelGeneration

    let currentTree = ReceiptInputTree.readTree repoRoot
    let currentInputTree = currentTree |> Option.map _.Identity
    let currentModelGeneration = observeModelGeneration ctx
    let currentInputs = ObservedModel.inputs ctx.ProjectGraph

    // A typed transition, not a candidate-then-guard: see
    // `ReceiptTransition`. `Noop` and `Narrower` keep whatever was earned,
    // `Revoked` clears it, and only `Earned` can write.
    let writtenByTestRun =
        ReceiptInputTree.writtenByTestRun
            repoRoot
            (ReceiptInputTree.watcherObserves repoRoot (ctx.ProjectGraph.ObserveCheckableFiles() |> Option.map snd))
            (state.InFlight |> Option.map (fun run -> run.WatcherReported))
            launch.TestHostStartedAt
            launch.InputTree
            currentTree

    // The evidence the standing receipt's own run earned, under the
    // current model. A completion that keeps the receipt (`Noop`,
    // `Narrower`) publishes exactly this, so the run `test-scope` grades
    // and the run the published evidence names are one run.
    let standingEvidence =
        state.EvidenceReceipt
        |> Option.bind (fun receipt ->
            EarnedEvidence.retainedForRun currentModelGeneration currentInputs receipt.RunId state.Earned)

    let receiptTransition =
        ReceiptTransition.classify
            (Set.toList runnableProjects)
            state.EvidenceReceipt
            standingEvidence
            currentInputTree
            currentModelGeneration
            currentInputs
            writtenByTestRun
            launch
            completed
            coverage

    match receiptTransition with
    | ReceiptTransition.Revoked reason ->
        ctx.Log $"  ↳ test evidence receipt revoked: %s{reason}"

        // WHICH paths, so a revocation with no watcher event behind it
        // (a gitignored build output under a discovery root, say) is
        // answered by this line rather than by an afternoon of ctimes.
        match ReceiptInputTree.movedPaths launch.InputTree currentTree with
        | Some paths -> ctx.Log $"    moved paths: %s{paths}"
        | None -> ()
    | ReceiptTransition.Earned _
    | ReceiptTransition.Noop
    | ReceiptTransition.Narrower -> ()

    let evidenceReceipt =
        ReceiptTransition.apply state.EvidenceReceipt receiptTransition

    { CurrentTree = currentTree
      CurrentInputTree = currentInputTree
      CurrentModelGeneration = currentModelGeneration
      CurrentInputs = currentInputs
      StandingEvidence = standingEvidence
      ReceiptTransition = receiptTransition
      EvidenceReceipt = evidenceReceipt }

/// The state a completion leaves before its debt is settled: the session ledgers, the
/// failures and coverage it assembled, the receipt it holds, the red it stands on, and the
/// mode `endRun` leaves.
let internal recordCompletion
    (endRun: bool -> TestPruneState -> TestPruneState)
    (state: TestPruneState)
    (completed: TestRunCompleted)
    (launch: TestRunLaunch)
    (assembled: AssembledRunResults)
    (receipt: ReceiptOutcome)
    (unanalyzable: Map<string, UnanalyzableFile>)
    : TestPruneState =
    let completedRuns = assembled.CompletedRuns
    let checkReach = assembled.CheckReach
    let conditionalFailureRecall = assembled.ConditionalFailureRecall
    let outstandingFailures = assembled.OutstandingFailures
    let coverage = assembled.Coverage
    let currentTree = receipt.CurrentTree
    let currentInputTree = receipt.CurrentInputTree
    let currentModelGeneration = receipt.CurrentModelGeneration
    let receiptTransition = receipt.ReceiptTransition

    let state =
        { state with
            CompletedRuns = completedRuns
            CheckReaches =
                (completed.RunId, launch.WouldHaveRun, checkReach, conditionalFailureRecall)
                :: (state.CheckReaches
                    |> List.filter (fun (id, _, _, _) -> id <> completed.RunId)
                    |> List.truncate (SessionRunLedger - 1))
            OutstandingFailures = outstandingFailures
            LastCoverage = coverage
            LastZeroSelection = launch.ZeroSelection
            Receipt = ReceiptTransition.hold state.Receipt receiptTransition currentTree
            // Debt is scoped to exactly the run that was active when the
            // BootScan cohort sealed. Failure keeps it durable, but must not
            // let a later unrelated run claim it implicitly.
            DebtDuringFullRun = Map.empty
            InFlight = None
            // A conclusion only when the run executed to a red over the
            // tree and model it launched under (`onlyRetriesRed`).
            LastRed =
                match completed.Outcome, launch.InputTreeHash, currentInputTree with
                | Normal, Some launched, Some current when
                    launched = current
                    && launch.ModelGeneration = currentModelGeneration
                    && completed.Results
                       |> Map.exists (fun _ result -> TestResult.verdict result = Refuted)
                    ->
                    Some
                        { SymbolRevisions = launch.SymbolRevisions
                          Tree = current }
                | _ -> None
            Mode =
                (endRun
                    (completed.Results
                     |> Map.exists (fun _ result -> TestResult.executedTests result))
                    state)
                    .Mode
            // Carried with them: the pruned map is what the ledger was
            // just written from, so the next run's coarse-fallback
            // widening reads the same set the user was shown.
            UnanalyzableFiles = unanalyzable }

    state

/// What a completion's debt is settled from.
type internal DebtSettlementFacts =
    { State: TestPruneState
      Completed: TestRunCompleted
      Launch: TestRunLaunch
      RunnableProjects: Set<string>
      CurrentInputTree: string option
      CurrentModelGeneration: int64 option
      DebtDuringFullRun: Map<string, int64>
      OwedTo: string -> Set<string>
      NoUnrunnableCoverers: Lazy<bool>
      Debt: CompletionDebt }

/// What a completion settled: the debt it leaves, and the facts about the run the rest of
/// the fold reads.
type internal DebtSettlement =
    { Aborted: bool
      ProjectPassed: string -> bool
      CoveredByConstruction: bool
      Debt: VerificationDebt
      AbsorbedProjects: Set<string>
      ExecutedFullSuite: bool
      EarnsBaseline: bool }

/// Settle what a completion owes: commit the symbols it covered green, retire and absorb
/// runtime obligations and fanout, record ingested coverage, recover unknown debt, and
/// decide whether it earns a full-suite baseline.
let internal settleDebt (facts: DebtSettlementFacts) : DebtSettlement =
    let state = facts.State
    let completed = facts.Completed
    let launch = facts.Launch
    let runnableProjects = facts.RunnableProjects
    let currentInputTree = facts.CurrentInputTree
    let currentModelGeneration = facts.CurrentModelGeneration
    let debtDuringFullRun = facts.DebtDuringFullRun
    let owedTo = facts.OwedTo
    let noUnrunnableCoverers = facts.NoUnrunnableCoverers
    let commitPending = facts.Debt.CommitPending
    let revisionOf = facts.Debt.RevisionOf

    // Outcome-conditional, per-project green-commit. A launched
    // symbol leaves the needs-testing queue ONLY when the run
    // genuinely covered it green:
    //   - the run did NOT abort (a beforeRun throw / crash gives
    //     Outcome = Aborted, Results = Map.empty → commit nothing), AND
    //   - EVERY project covering the symbol produced a PASSED result.
    // A project counts as passed only if it appears in completed.Results
    // with `TestResult.verifiedGreen` — a covering project ABSENT from the
    // results (didn't run this cycle) blocks the commit (we can't claim
    // it green). A symbol with NO covering project was already dropped at
    // flush time, but if one slipped through it commits here (nothing to
    // wait on). Genuine in-session mid-run arrivals are NOT in
    // launch.Symbols, and a launched symbol edited again during the run is
    // at a newer revision than the launch captured: both stay queued and
    // the queued impact run re-runs them. BootScan debt may join the candidate set only when
    // this completion proves the run was actually full-suite.
    //
    // This fold read `TestResult.isPassed`, which was TRUE
    // for `TestsNoMatch` — so a symbol whose covering project ran under an
    // impact-derived class filter that matched ZERO tests had its test debt
    // DISCHARGED by a project that executed nothing, and left
    // pending-verification.json unverified. `verifiedGreen` is `Verified`
    // only, so a project that ran nothing can no longer retire anything.
    // A run selected under a model this host has since replaced verified
    // that model, not the current one: it discharges nothing, like an abort.
    let aborted =
        launch.ModelGeneration <> currentModelGeneration
        || match completed.Outcome with
           | Aborted _ -> true
           | Normal -> false

    let projectPassed (proj: string) =
        match Map.tryFind proj completed.Results with
        | Some r -> TestResult.verifiedGreen r
        | None -> false

    // A run that executed every runnable project in full, over the input tree
    // it launched against, and passed all of them, covered every symbol that
    // tree holds: there is nothing left to ask per symbol, unless the index
    // names a coverer this daemon neither runs nor declares excluded.
    let coveredByConstruction =
        not aborted
        && not (Set.isEmpty runnableProjects)
        && completed.Verification = Ran FullSuite
        && ReceiptInputTree.matches launch.InputTreeHash currentInputTree
        && runnableProjects |> Set.forall projectPassed
        && noUnrunnableCoverers.Value

    let committedSymbols =
        if aborted then
            Set.empty
        else
            // An attached symbol may borrow this run only when the run was
            // actually full, over the input tree it launched against, and the
            // symbol is still at the revision captured when it attached.
            let attachedCandidates =
                match completed.Verification with
                | Ran FullSuite when ReceiptInputTree.matches launch.InputTreeHash currentInputTree ->
                    debtDuringFullRun
                    |> Map.filter (fun symbol captured -> revisionOf state.Debt symbol = captured)
                    |> Map.keys
                    |> Set.ofSeq
                | _ -> Set.empty

            let launchedCurrent =
                launch.Symbols
                |> Set.filter (fun symbol ->
                    revisionOf state.Debt symbol = (Map.tryFind symbol launch.SymbolRevisions |> Option.defaultValue 0L))

            let candidates = Set.union launchedCurrent attachedCandidates

            if coveredByConstruction then
                candidates
            else
                candidates
                |> Set.filter (fun s ->
                    match Map.tryFind s launch.CoveringProjectsBySymbol with
                    | Some projs when not (Set.isEmpty projs) -> projs |> Set.forall projectPassed
                    | Some _ -> true
                    | None -> owedTo s |> Set.forall projectPassed)

    if not (Set.isEmpty committedSymbols) then
        Logging.info
            "test-prune"
            $"Committing %d{Set.count committedSymbols} verified symbol(s) — removing from pending-verification queue"

    let debt = commitPending committedSymbols state.Debt

    if not aborted && not (Map.isEmpty launch.RuntimeProjectsByFile) then
        for KeyValue(file, launchedProjects) in launch.RuntimeProjectsByFile do
            let passed = launchedProjects |> Map.keys |> Seq.filter projectPassed |> Set.ofSeq

            if not (Set.isEmpty passed) then
                let projectNames = passed |> Set.toList |> String.concat ", "

                Logging.info "test-prune" $"runtime coverage verified %s{file} by %s{projectNames}"

    // A run covered by construction discharges every obligation owed now,
    // not only those it launched with: what attached to it arrived over the
    // tree it ran.
    let retiredRuntime =
        if coveredByConstruction then
            debt.RuntimeObligations
        else
            launch.RuntimeProjectsByFile

    let debt =
        if aborted || Map.isEmpty retiredRuntime then
            debt
        else
            { debt with
                RuntimeObligations =
                    retireRuntimeCoverageObligations debt.RuntimeObligations retiredRuntime projectPassed }

    // What runtime coverage would have added to this run, for a shadow project, is spent
    // by the run it was logged beside: it was never selected, so nothing here may treat it
    // as covered or owed. Only a project that came back red keeps it, for the run that
    // retries it, as a deciding project's obligation would stay.
    let debt =
        if aborted || Map.isEmpty launch.RuntimeShadow then
            debt
        else
            // Per project, not a run-level verdict: a project this run did not launch was
            // not red, so what was logged beside the run is spent for it too.
            let notRed project =
                match Map.tryFind project completed.Results with
                | Some result -> TestResult.verifiedGreen result
                | None -> true

            { debt with
                ShadowRuntime = retireRuntimeCoverageObligations debt.ShadowRuntime launch.RuntimeShadow notRed }

    // A project this run executed UNFILTERED and passed, over the input
    // tree it launched against, ran every test a trigger raised for it
    // during the run could ask for: a scan re-deriving its runtime
    // obligation (a newer generation over the same bytes) or a dependency
    // fanout from a build of that tree. Those triggers are absorbed here,
    // per project, as `coveredByConstruction` absorbs them for the whole
    // suite; otherwise the queued re-run executes the project in full
    // again. A filtered or reused project, a red, an abort, or a tree that
    // moved since launch absorbs nothing.
    let absorbedProjects =
        if aborted || not (ReceiptInputTree.matches launch.InputTreeHash currentInputTree) then
            Set.empty
        else
            launch.Selection
            |> Map.filter (fun project selection ->
                selection = ProjectInFull
                && not (Map.containsKey project launch.Reused)
                && projectPassed project)
            |> Map.keys
            |> Set.ofSeq

    let debt =
        if Set.isEmpty absorbedProjects then
            debt
        else
            { debt with
                RuntimeObligations =
                    debt.RuntimeObligations
                    |> Map.map (fun _ projects ->
                        projects
                        |> Map.filter (fun project _ -> not (Set.contains project absorbedProjects)))
                    |> Map.filter (fun _ projects -> not (Map.isEmpty projects)) }

    // A project whose runtime coverage did not ingest is paid by a run that executed
    // it in full and passed: a pass means its coverage ingested this time (a run
    // that could not ingest it reports the project errored, never passed). A
    // filtered or reused result, a red or an abort pays nothing.
    let ingested =
        if aborted then
            Set.empty
        else
            debt.CoverageNotIngested
            |> Map.keys
            |> Seq.filter (fun project ->
                Map.tryFind project launch.Selection = Some ProjectInFull
                && not (Map.containsKey project launch.Reused)
                && projectPassed project)
            |> Set.ofSeq

    if not (Set.isEmpty ingested) then
        let names = ingested |> Set.toList |> String.concat ", "

        Logging.info
            "test-prune"
            $"Runtime coverage ingested again for %s{names} after a whole-project run passed; no longer owed"

    let debt =
        if Set.isEmpty ingested then
            debt
        else
            { debt with
                CoverageNotIngested =
                    debt.CoverageNotIngested
                    |> Map.filter (fun project _ -> not (Set.contains project ingested)) }

    // Discharge an UNREADABLE ledger's debt.
    //
    // The debt is owed in FULL, because its membership is unknown: the only
    // run that can retire it is one that executed EVERY runnable project,
    // unfiltered, and passed. At that point every symbol the lost ledger
    // could possibly have held has been verified by an actual test run, so
    // there is nothing left for it to owe.
    //
    // Each conjunct is load-bearing:
    //  * `not aborted` — an aborted run has empty Results and verified nothing.
    //  * every RUNNABLE project passed — `projectPassed` demands the project
    //    be PRESENT in the results AND green, so a project that never ran
    //    cannot be counted.
    //  * `Ran FullSuite` — the run EXECUTED and none of it was
    //    impact-FILTERED. A case, not a bool: scope is unreachable unless
    //    something ran, so emptiness needs no separate check.
    //  * a non-empty `runnableProjects` — an analysis-only daemon runs no
    //    tests, so it can never prove anything and must not discharge. It
    //    asks about the SELECTION, not the results.
    //
    // Only a state that has discharged it rewrites the ledger: `PrepareCommit`
    // leaves the corrupt file untouched while the debt is unknown, so that a
    // crash mid-recovery leaves the next session the same honest "unknown"
    // rather than a clean, empty, WRONG ledger.
    let executedFullSuite =
        not aborted
        && not (Set.isEmpty runnableProjects)
        && completed.Verification = Ran FullSuite

    let recovers =
        debt.RecoveryOutstanding
        && executedFullSuite
        && runnableProjects |> Set.forall projectPassed

    // Obligations this run launched were retired above; any left are newer
    // than the run and stay owed.
    if recovers then
        let causes = describeUnknownDebt debt.UnknownDebt

        Logging.info
            "test-prune"
            $"A full suite passed every configured project — the unknown verification debt is discharged (%s{causes}), and the ledgers are rewritten from this run. Impact filtering resumes."

    let debt = if recovers then { debt with UnknownDebt = [] } else debt

    // The full-suite WATERMARK. Written only when a full-suite run PASSED
    // every configured project unfiltered. A red project is never counted as
    // covered (AUTOMATION-1581, ADR-021 as amended): a red run proves nothing
    // about tests it did not report as passed. A host that ends early, as
    // xUnit v3 does under `--stop-on-fail on`, exits like any other red and
    // reports only the tests it reached, so "red with the red recorded" used
    // to vouch for tests that never ran. A red full run leaves the earlier
    // baseline, if any, standing: every test it skipped last ran in that one.
    // A project that produced no accountable outcome (deferred, errored) has
    // `Ran FullSuite` false already.
    //
    // The same `executedFullSuite` the unreadable-ledger discharge reads: two
    // readings of what a full-suite run is would let one recover a ledger
    // the other refused to baseline.
    //
    // A project this run reused passed here only if its reuse held at
    // completion: its key was re-computed over the tree as it is now and
    // still matched a full-project green earned at exactly those inputs
    // (AUTOMATION-1508). Every test of it ran at these inputs, so it counts
    // as executed. A reuse whose key moved reports a deferral, which is
    // not a pass, so that run earns nothing.
    let earnsBaseline =
        executedFullSuite && runnableProjects |> Set.forall projectPassed

    let debt =
        if earnsBaseline then
            { debt with
                Baseline =
                    Some
                        { RunId = completed.RunId
                          EarnedAt = DateTime.UtcNow
                          Projects = runnableProjects } }
        else
            debt

    { Aborted = aborted
      ProjectPassed = projectPassed
      CoveredByConstruction = coveredByConstruction
      Debt = debt
      AbsorbedProjects = absorbedProjects
      ExecutedFullSuite = executedFullSuite
      EarnsBaseline = earnsBaseline }
