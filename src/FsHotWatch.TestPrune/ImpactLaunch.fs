/// An impact run's environment and the stages it launches through: the widenings, the selection, the launch it assembles, the zero-affected skip and the selection log.
module FsHotWatch.TestPrune.ImpactLaunch

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

/// The fixed configuration a run reads.
type internal RunConfig =
    { RepoRoot: string
      ConfiguredTestProjects: TestConfig list
      RunnableProjects: Set<string>
      LaunchDeadline: TimeSpan
      MaxParallelProjects: int option
      BeforeRun: (Guid -> HookStep.Tracker -> unit) option
      AfterRun: (TestResults -> unit) option
      CoveragePaths: (string -> CoveragePaths option) option
      Traces: TraceWiring option }

/// How a run classifies the debt it launches against.
type internal RunDebt =
    { CoveringOf: string seq -> string -> Set<string>
      DebtScope: (string -> Set<string>) -> string -> Set<string>
      NothingOwed: VerificationDebt -> bool
      RevisionOf: VerificationDebt -> string -> int64
      BaselineInvalidReason: VerificationDebt -> string option }

/// The test-evidence plan of a run about to launch `configs` (`evidencePlanFor`): the projects
/// `launching` admits, whether lookups are allowed, and the outstanding failures.
type internal EvidencePlanner =
    PluginCtx<TestPruneMsg>
        -> TestConfig list
        -> (string -> bool)
        -> bool
        -> OutstandingFailure list
        -> TestEvidenceRun.Plan option

/// What a run asks the plugin and its host at launch.
type internal RunLaunchSupport =
    { AwaitDiscovery: PluginCtx<TestPruneMsg> -> Async<unit>
      CompileInputsOf: PluginCtx<TestPruneMsg> -> unit -> string -> bool
      CoverageIngestFailed: PluginCtx<TestPruneMsg> -> CoverageIngestFailure -> unit
      EvidencePlanFor: EvidencePlanner
      LogLaunch: string -> TestMode -> TestRunLaunch -> unit
      PriceSelection: TestRunLaunch -> TestRunLaunch
      ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option
      ObserveTestInputs: PluginCtx<TestPruneMsg> -> Map<string, string * string> option
      TracesFor: PluginCtx<TestPruneMsg> -> TestMode -> TraceRunHost option }

/// What an impact run reads from the plugin that launches it, grouped: its configuration,
/// its index handles, how it classifies debt, what it asks at launch, and the poison-seed
/// ages it advances, as they stand when it advances them.
type internal RunEnv =
    {
        Config: RunConfig
        Db: Database
        Queries: ImpactQueries
        Debt: RunDebt
        Launch: RunLaunchSupport
        /// Advance the poison-seed ages for the symbols a run launches (`pendingAgeRef`).
        BumpSeedAges: string list -> unit
    }

/// What an impact run widens to before it selects anything (`widenLaunch`).
type internal LaunchWidenings =
    { ScopeIsFullSuite: bool
      LedgerUnreadable: bool
      BaselineInvalid: string option
      CoarseGaps: Set<string>
      LaunchedRuntimeObligations: RuntimeCoverageObligations
      RuntimeForceProjects: Set<string>
      EvidenceGap: Set<string>
      FullSuiteCause: FullSuiteCause
      ForceRunProjects: Set<string>
      LaunchedSymbols: Set<string>
      LaunchedRevisions: Map<string, int64> }

/// The widenings every impact launch applies, logged, and the symbols and revisions it launches against.
/// The fanout left once projects that hold evidence at these inputs are taken out. A
/// fanout force-runs a project, and the launch then reuses that evidence anyway, so such
/// a project gives a run nothing to do. Taking it out here lets a cycle with nothing else
/// owed end at the zero-affected skip rather than pay for a launch (AUTOMATION-1534).
let internal undischargedFanout
    (env: RunEnv)
    ctx
    (configs: TestConfig list)
    (inputs: TestRunInputs)
    (fanout: Set<string>)
    =
    if
        Set.isEmpty fanout
        || TestMode.requestsFullSuite inputs.Mode
        || inputs.Debt.RecoveryOutstanding
    then
        fanout
    else
        match env.Launch.EvidencePlanFor ctx configs fanout.Contains true inputs.OutstandingFailures with
        | None -> fanout
        | Some plan ->
            let held = fanout |> Set.filter plan.Reused.ContainsKey

            if not held.IsEmpty then
                let names = String.concat "; " held

                Logging.info
                    "test-prune"
                    $"Dependency fanout: %d{held.Count} project(s) already hold evidence at these inputs, not force-run: %s{names}"

            Set.difference fanout held

let internal widenLaunch
    (env: RunEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (configs: TestConfig list)
    (inputs: TestRunInputs)
    (fanoutProjects: Set<string>)
    : LaunchWidenings =
    let baselineInvalidReason = env.Debt.BaselineInvalidReason
    let revisionOf = env.Debt.RevisionOf
    let observeModelGeneration = env.Launch.ObserveModelGeneration

    // The single chokepoint every launch path funnels through (BatchChecked
    // drain, BuildCompleted, deferred rerun). Both widenings live here rather
    // than at each call site, so no future launch path can forget one and
    // quietly reintroduce a filtered run where an unfiltered one was owed.
    //
    //  * full-suite scope: run EVERY project, unfiltered.
    //  * unanalysable files: run every project, because a
    //    selection made without them cannot be trusted.
    //  * an UNREADABLE pending-verification ledger: run every
    //    project, because the ledger names what is still owed, and a selection
    //    made without it cannot be trusted either. Same shape as 113: the
    //    missing input is a SAFETY input, so its absence widens rather than
    //    narrows.
    //  * NO VALID FULL-SUITE BASELINE: run every project,
    //    because the tests a filtered run skips have nothing to be equivalent
    //    to. A cold repository earns its baseline here; a repository whose
    //    `tests.projects` grew re-earns it. Same shape as 150.
    let scopeIsFullSuite = TestMode.requestsFullSuite inputs.Mode
    let ledgerUnreadable = inputs.Debt.RecoveryOutstanding
    let baselineInvalid = baselineInvalidReason inputs.Debt

    // The coarse fallback only needs to know WHICH files are unanalysable; the
    // map's values exist so the ledger projection can re-report their
    // diagnostics.
    let unanalyzablePaths = inputs.UnanalyzableFiles |> Map.keys |> Set.ofSeq

    // A failed extension is a hole in the graph exactly as an unanalysable file
    // is: its stored edges describe an older tree. Same coarse fallback.
    let coarseGaps =
        inputs.FailedExtensions
        |> Map.keys
        |> Seq.map extensionLedgerKey
        |> Set.ofSeq
        |> Set.union unanalyzablePaths
        |> Set.union inputs.Unindexed

    let launchedRuntimeObligations = inputs.Debt.RuntimeObligations

    // A project whose runtime coverage did not ingest runs in full too: what its tests
    // reach is not known, so no runtime selection of it can be trusted.
    let runtimeForceProjects =
        launchedRuntimeObligations
        |> Map.values
        |> Seq.collect Map.keys
        |> Set.ofSeq
        |> Set.union (
            inputs.Debt.CoverageNotIngested
            |> Map.keys
            |> Set.ofSeq
            |> Set.intersect (fullSuiteProjects configs)
        )

    // A project with no whole-project run under the current model can only be
    // vouched for by running it in full: filtered or skipped, it is a refusal in
    // the evidence this run earns, and a later run that selects nothing keeps that
    // refusal. After a model change or a restart, this is every project the
    // current model has not yet seen run whole.
    let evidenceGap =
        EarnedEvidence.wholeProjectGap
            (observeModelGeneration ctx)
            (ObservedModel.inputs ctx.ProjectGraph)
            (fullSuiteProjects configs)
            inputs.Earned

    let fullSuiteCause =
        fullSuiteCauseOf
            scopeIsFullSuite
            ledgerUnreadable
            (Option.isSome baselineInvalid)
            (not (Set.isEmpty coarseGaps))
            (Set.isSubset (fullSuiteProjects configs) evidenceGap)

    let forceRunProjects =
        let widened = coarseFallbackProjects configs coarseGaps fanoutProjects
        let widened = Set.union widened runtimeForceProjects
        let widened = Set.union widened evidenceGap

        if scopeIsFullSuite || ledgerUnreadable || Option.isSome baselineInvalid then
            Set.union widened (fullSuiteProjects configs)
        else
            widened

    if scopeIsFullSuite then
        Logging.info
            "test-prune"
            "Scope: FULL SUITE — impact filtering is disabled for this run; every configured test project runs in full"

    if not (Set.isEmpty evidenceGap) then
        let names = evidenceGap |> Set.toList |> String.concat ", "

        Logging.info
            "test-prune"
            $"Evidence gap: %d{Set.count evidenceGap} project(s) have no whole-project run under the current project model (%s{names}) — running them in full, so this run's evidence can support a green"

    match baselineInvalid with
    | Some reason when not ledgerUnreadable ->
        Logging.info
            "test-prune"
            $"Scope: FULL SUITE (no valid full-suite baseline) — %s{reason}. Every configured test project runs in full; impact filtering resumes once this run has accounted for every project."
    | _ -> ()

    if ledgerUnreadable then
        Logging.warn
            "test-prune"
            $"Scope: FULL SUITE (verification debt unknown: %s{describeUnknownDebt inputs.Debt.UnknownDebt}) — what still needs testing is not known, so this run cannot know what it owes. It runs EVERY configured test project in full rather than trust an impact selection made without that record. Impact filtering resumes once a full suite passes."

    if not (Map.isEmpty inputs.FailedExtensions) then
        let names = inputs.FailedExtensions |> Map.keys |> String.concat ", "

        Logging.warn
            "test-prune"
            $"%d{inputs.FailedExtensions.Count} test-impact extension(s) failed their last refresh (%s{names}) — their edges describe an older tree, so this run falls back to EVERY test project in full rather than trusting a selection made over them"

    if not (Set.isEmpty inputs.Unindexed) then
        Logging.warn
            "test-prune"
            $"%d{Set.count inputs.Unindexed} file(s) whose analysis a project-model change retired are not in the impact index yet (%s{describeAll (Set.toList inputs.Unindexed)}) — this run falls back to EVERY test project in full until they are analysed again"

    if not (Set.isEmpty unanalyzablePaths) then
        let names = unanalyzablePaths |> Set.toList |> String.concat ", "

        Logging.warn
            "test-prune"
            $"%d{Set.count unanalyzablePaths} file(s) could not be analysed (%s{names}) — their symbols are missing from the impact graph, so this run falls back to EVERY test project in full rather than trusting a selection made without them"

    // The queue snapshot this run is LAUNCHED against — the durable
    // queue UNION the in-memory hot view. Captured here (not read from
    // state at completion time) because mid-run BatchChecked flushes
    // mutate both; the synchronous TestsFinished handler commits ONLY
    // these symbols and leaves mid-run arrivals queued for the rerun.
    let launchedSymbols =
        Set.union inputs.Debt.PendingQueue (Set.ofList inputs.ChangedSymbols)

    let launchedRevisions =
        launchedSymbols
        |> Set.toList
        |> List.map (fun symbol -> symbol, revisionOf inputs.Debt symbol)
        |> Map.ofList

    { ScopeIsFullSuite = scopeIsFullSuite
      LedgerUnreadable = ledgerUnreadable
      BaselineInvalid = baselineInvalid
      CoarseGaps = coarseGaps
      LaunchedRuntimeObligations = launchedRuntimeObligations
      RuntimeForceProjects = runtimeForceProjects
      EvidenceGap = evidenceGap
      FullSuiteCause = fullSuiteCause
      ForceRunProjects = forceRunProjects
      LaunchedSymbols = launchedSymbols
      LaunchedRevisions = launchedRevisions }

/// The impact selection an impact run launches from (`selectForLaunch`).
type internal LaunchSelection =
    {
        CoveringProjectsBySymbol: Map<string, Set<string>>
        SymbolAffectedByProject: Map<string, string list>
        /// The projects the composition-root fail-safe restored among those.
        RestoredProjects: Set<string>
        TraceShadow: ShadowReading list
        QuarantinedAffectedByProject: Map<string, string list>
        AffectedByProject: Map<string, string list>
        TotalClasses: int
        InputTree: ReceiptTree option
        SymbolFreeEdit: SymbolFreeEdit option
        NarrowedSeeds: string list option
        AlwaysOnly: bool
    }

/// Select what an impact run launches: the projects covering each launched symbol, the
/// classes the symbols reach, the shadow trace reading, quarantined failures, the forced
/// projects, and a symbol-free edit's covering classes.
let internal selectForLaunch
    (env: RunEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (configs: TestConfig list)
    (inputs: TestRunInputs)
    (hasCachedResults: bool)
    (widenings: LaunchWidenings)
    : LaunchSelection =
    let repoRoot = env.Config.RepoRoot
    let runnableProjects = env.Config.RunnableProjects
    let traces = env.Config.Traces
    let db = env.Db
    let queries = env.Queries
    let coveringOf = env.Debt.CoveringOf
    let lazyDebtScope = env.Debt.DebtScope
    let nothingOwed = env.Debt.NothingOwed
    let scopeIsFullSuite = widenings.ScopeIsFullSuite
    let forceRunProjects = widenings.ForceRunProjects
    let launchedSymbols = widenings.LaunchedSymbols

    // For each launched symbol, the set of test PROJECTS whose tests
    // cover it. An empty set ⇒ no covering test. Queried per-symbol so
    // a symbol commits ONLY when every project covering IT passed (a
    // run-wide union would over-couple unrelated symbols). Empty queue ⇒
    // no queries. Kept INSIDE the try: these are DB reads that can throw
    // transiently on a cold/contended box (SQLITE_BUSY, schema drift). A
    // throw here must produce the Aborted lifecycle below (an honest,
    // re-runnable "tests did not run" verdict) rather than escaping to
    // the framework's `runOne`, which would only log-and-strand the run.
    // The projects gating a symbol's commit are `debtScope`'s — the same
    // rule `flushAndQueryAffected` uses to drop symbols, so the two cannot
    // disagree. An unconfigured, undeclared coverer blocks the commit on
    // purpose: its tests never ran, so they verified nothing.
    //
    // A pass-through run asks nothing: it runs every project, and its
    // completion discharges by the full run's green (see `TestsFinished`).
    let coveringProjectsBySymbol =
        if TestMode.skips LaunchCoveringCapture inputs.Mode then
            Map.empty
        else
            let owedTo = lazyDebtScope (coveringOf launchedSymbols)

            launchedSymbols |> Set.toList |> List.map (fun s -> s, owedTo s) |> Map.ofList

    // Extension-contributed edges were already written to the DB by
    // flushAndQueryAffected, so `inputs.AffectedTests` already includes tests
    // reachable through extension edges (sql, sql-hydra, falco, etc.).
    //
    // A pass-through flush selects nothing, so its launch asks the one grouped
    // question the check-reach sample needs: what `check` would have selected.
    let affectedSelection =
        if TestMode.skips FlushSelection inputs.Mode then
            if Set.isEmpty launchedSymbols then
                AffectedSelection.none
            else
                queries.AffectedTests(Set.toList launchedSymbols)
                |> AffectedSelection.within (fun project ->
                    Set.isEmpty runnableProjects || Set.contains project runnableProjects)
        else
            match inputs.AffectedTests with
            | Analyzed selection -> selection
            | NotYetAnalyzed -> AffectedSelection.none

    let affectedTestsList = affectedSelection.Tests

    let symbolAffectedByProject =
        affectedTestsList
        |> List.groupBy (fun t -> t.TestProject)
        |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)
        |> Map.ofList

    // `tests.traces.select: "shadow"`: what the recorded traces would select,
    // beside the static symbol selection above, from the same seeds and index at
    // the same instant. Logged now, stored at completion; it selects nothing.
    let traceShadow =
        match traces with
        | Some wiring when wiring.Policy.Select = SelectShadow ->
            let symbols = TestPrune.Ports.toSymbolStore db

            TraceShadow.atLaunch
                wiring.Policy
                repoRoot
                (configs |> List.map (fun c -> c.Project) |> List.distinct)
                (fun project -> symbols.GetTestMethodsInProjects [ project ])
                (lazy (TestPrune.Trace.TraceIngest.versionHashes symbols))
                affectedTestsList
                // R6 walks a seed traces cannot verify with the static selection's own
                // query; R7 applies the static file rules to changed files no trace claims.
                { Seeds = Set.toList launchedSymbols
                  ChangedFiles = inputs.ChangedFiles
                  StaticWalk = fun seeds -> (queries.AffectedTests seeds).Tests
                  FileFallback =
                    fun files ->
                        match selectTests symbols files Map.empty |> fst with
                        | RunAll _ -> None
                        | RunSubset tests -> Some tests
                  IsIndexed = fun file -> (symbols.GetFileKey file).IsSome
                  AttributesOf = fun symbol -> symbols.GetAttributesForSymbol symbol |> List.map fst }
                // Only the traces of the environment the run is about to record are read.
                (fun project ->
                    match configs |> List.tryFind (fun c -> c.Project = project) with
                    | None -> Error "no-test-config"
                    | Some config -> wiring.PredictFingerprint project config.Args config.Environment)
                (Logging.info "test-prune")
        | _ -> []

    // A prior red is mandatory verification debt. The
    // graph for today's edit may not reach yesterday's failing class, but
    // the next ordinary run must still execute it. Unknown class scope is
    // conservatively the whole project.
    let quarantinedAffectedByProject =
        OutstandingFailure.quarantine (fullSuiteProjects configs) inputs.OutstandingFailures symbolAffectedByProject

    // UNION the dependency-fanout: each force-run project enters the
    // map with an EMPTY class list, which `buildFilterArgs` treats as
    // "no filter → run ALL tests in this project" (a project ABSENT
    // from a non-empty map is skipped; present-with-[] runs in full).
    // We don't overwrite a project that already has symbol-affected
    // classes — its precise selection plus a forced full run are the
    // same "run this project"; [] (full) is the safe superset, so a
    // fanout hit promotes a partially-selected project to full.
    let affectedByProject =
        forceRunProjects
        |> Set.fold (fun acc proj -> Map.add proj [] acc) quarantinedAffectedByProject

    // The skip gate counts symbol-affected classes only. A pure
    // dependency-fanout (force-run projects, zero symbol classes) must
    // NOT be counted as "0 affected" and skipped — so the gate below
    // also checks `forceRunProjects` is empty.
    let totalClasses =
        quarantinedAffectedByProject |> Map.values |> Seq.sumBy List.length

    let inputTree = ReceiptInputTree.readTree repoRoot

    // The tree moved since the evidence was earned.
    let unverified =
        not (ReceiptInputTree.matches inputs.EvidenceTree (inputTree |> Option.map _.Identity))

    // Whether the current model compiles a repo-relative path; `None` when no model
    // publishes the files it compiles.
    let compiledFiles () =
        ctx.ProjectGraph.ObserveCheckableFiles()
        |> Option.map (fun (_, files) ->
            fun (rel: string) -> files.Contains(AbsFilePath.create (Path.Combine(repoRoot, rel))))

    // Nothing is owed and nothing was selected, but the tree moved since the
    // evidence was earned, by an edit the symbol diff saw as changing no
    // symbol. Running every project in full is what a non-source edit costs;
    // an edit confined to compiled F# source files costs the classes covering
    // their symbols (`SymbolFreeEdit`). `None` when this is not that launch.
    let symbolFreeEdit =
        let nothingSelected =
            totalClasses = 0
            && Set.isEmpty forceRunProjects
            && List.isEmpty inputs.OutstandingFailures
            && not (UncoveredChanges.isAll inputs.ChangedSymbolsAllUncovered)

        if
            unverified
            && hasCachedResults
            && not scopeIsFullSuite
            && nothingOwed inputs.Debt
            && nothingSelected
        then
            let compiled = compiledFiles ()

            let index: SymbolFreeEdit.Index =
                { SymbolsIn = fun rel -> db.GetSymbolsInFile rel |> List.map _.FullName |> List.distinct
                  TestsIn = db.GetTestMethodsInFile
                  Affected = queries.AffectedTests }

            match
                SymbolFreeEdit.select
                    inputs.EvidenceManifest
                    inputTree
                    inputs.SealedTree
                    compiled
                    (fun project -> Set.isEmpty runnableProjects || Set.contains project runnableProjects)
                    index
            with
            // No test covers the edited files, and no always-class stands in
            // for them: only running can earn evidence for the edited tree.
            | SymbolFreeEdit.Covering(files, _, classes, _) when
                Map.isEmpty classes && Map.isEmpty (AlwaysClasses.only configs)
                ->
                SymbolFreeEdit.Refused
                    $"no runnable test covers a symbol of %s{describeAll files}, and no project declares alwaysClasses"
            | narrowing -> narrowing
            |> Some
        else
            None

    let affectedByProject, narrowedSeeds =
        match symbolFreeEdit with
        | Some(SymbolFreeEdit.Covering(files, seeds, classes, _)) ->
            let classCount = classes |> Map.values |> Seq.sumBy List.length

            let running =
                if classCount = 0 then
                    $"no runnable test covers their %d{List.length seeds} symbol(s), so running only the declared alwaysClasses"
                else
                    $"running the %d{classCount} class(es) covering their %d{List.length seeds} symbol(s)"

            Logging.info
                "test-prune"
                $"No affected classes, and the tree moved since the evidence was earned only by an edit to %d{List.length files} compiled F# source file(s) the symbol diff saw as changing no symbol (%s{describeAll files}); %s{running} instead of every project in full"

            classes, Some seeds
        | Some(SymbolFreeEdit.Refused _)
        | None -> affectedByProject, None

    // A NON-SYMBOL EDIT BESIDE A SELECTION. The narrowing above reads the tree delta
    // only when nothing was selected. When a symbol edit (or a prior red) selected
    // classes, a fixture, content file or config that moved with it was answered by
    // nobody: the run executed the symbol's classes, and the evidence it earned spoke
    // for the whole new tree, including the tests that read what moved. Such a move
    // costs what it costs alone, every project in full.
    let affectedByProject =
        let moved =
            if
                Option.isNone symbolFreeEdit
                && unverified
                && hasCachedResults
                && not scopeIsFullSuite
            then
                SymbolFreeEdit.movedBesideSymbols
                    inputs.EvidenceManifest
                    inputTree
                    (compiledFiles ())
                    (db.GetSymbolsInFile >> List.isEmpty >> not)
            else
                []

        match moved with
        | [] -> affectedByProject
        | moved ->
            Logging.info
                "test-prune"
                $"The tree also moved by %d{List.length moved} path(s) no symbol selection answers for (%s{describeAll moved}); running every project in full"

            fullSuiteProjects configs |> Seq.map (fun project -> project, []) |> Map.ofSeq

    // ALWAYS-CLASSES. Every filtered launch carries each project's declared
    // `alwaysClasses`: tests that read the source tree as data, which no
    // symbol edge leads to. An edit whose every changed symbol has no
    // covering test would otherwise take the nothing-to-verify skip below,
    // yet the tree changed under classes that read all of it, so that edit
    // runs exactly those classes instead. So does an edit no symbol diff sees
    // to compiled files no test covers (`SymbolFreeEdit.Covering` with no
    // class). An empty selection is otherwise left alone: it already runs
    // every project in full.
    let uncoveredSymbolFreeEdit =
        match symbolFreeEdit with
        | Some(SymbolFreeEdit.Covering(_, _, classes, _)) -> Map.isEmpty classes
        | _ -> false

    let alwaysOnly =
        totalClasses = 0
        && Set.isEmpty forceRunProjects
        && List.isEmpty inputs.OutstandingFailures
        && Map.isEmpty affectedByProject
        && (UncoveredChanges.isAll inputs.ChangedSymbolsAllUncovered
            || uncoveredSymbolFreeEdit)
        && not (Map.isEmpty (AlwaysClasses.only configs))

    let affectedByProject =
        if alwaysOnly then
            if not uncoveredSymbolFreeEdit then
                Logging.info
                    "test-prune"
                    "Every changed symbol has no covering test, but the tree changed under the declared alwaysClasses; running only those"

            AlwaysClasses.only configs
        else
            AlwaysClasses.widen configs affectedByProject

    { CoveringProjectsBySymbol = coveringProjectsBySymbol
      SymbolAffectedByProject = symbolAffectedByProject
      RestoredProjects = affectedSelection.RestoredProjects
      TraceShadow = traceShadow
      QuarantinedAffectedByProject = quarantinedAffectedByProject
      AffectedByProject = affectedByProject
      TotalClasses = totalClasses
      InputTree = inputTree
      SymbolFreeEdit = symbolFreeEdit
      NarrowedSeeds = narrowedSeeds
      AlwaysOnly = alwaysOnly }

/// The run an impact launch assembled (`assembleLaunch`).
type internal LaunchAssembly =
    {
        /// The run's test-evidence plan, made when `executeTests` calls it: once `beforeRun` has
        /// settled the tree, so every key names the tree the tests run against.
        EvidencePlan: unit -> TestEvidenceRun.Plan option
        ExcludedByProject: Map<string, string list>
        Launch: TestRunLaunch
    }

/// Assemble the run an impact launch starts: the test-evidence plan, the check-skip
/// decisions, the selection and its attribution, what a full run would have run, and the
/// launch record.
let internal assembleLaunch
    (env: RunEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (configs: TestConfig list)
    (inputs: TestRunInputs)
    (hasCachedResults: bool)
    (fanoutProjects: Set<string>)
    (widenings: LaunchWidenings)
    (selected: LaunchSelection)
    : LaunchAssembly =
    let evidencePlanFor = env.Launch.EvidencePlanFor
    let observeModelGeneration = env.Launch.ObserveModelGeneration
    let observeTestInputs = env.Launch.ObserveTestInputs
    let scopeIsFullSuite = widenings.ScopeIsFullSuite
    let ledgerUnreadable = widenings.LedgerUnreadable
    let baselineInvalid = widenings.BaselineInvalid
    let coarseGaps = widenings.CoarseGaps
    let launchedRuntimeObligations = widenings.LaunchedRuntimeObligations
    let runtimeForceProjects = widenings.RuntimeForceProjects
    let evidenceGap = widenings.EvidenceGap
    let fullSuiteCause = widenings.FullSuiteCause
    let launchedSymbols = widenings.LaunchedSymbols
    let launchedRevisions = widenings.LaunchedRevisions
    let coveringProjectsBySymbol = selected.CoveringProjectsBySymbol
    let symbolAffectedByProject = selected.SymbolAffectedByProject
    let selectionRestored = selected.RestoredProjects
    let traceShadow = selected.TraceShadow
    let quarantinedAffectedByProject = selected.QuarantinedAffectedByProject
    let affectedByProject = selected.AffectedByProject
    let inputTree = selected.InputTree
    let symbolFreeEdit = selected.SymbolFreeEdit
    let narrowedSeeds = selected.NarrowedSeeds

    // TEST EVIDENCE, at launch: AFTER selection, so it can only take away
    // launches the selector chose, never add a green it did not. Keyed when
    // `executeTests` asks, once `beforeRun` has run and before any test has: the
    // tree a key names is the tree the tests run against, so the output of a
    // generating setup step is not an edit made mid-run (AUTOMATION-1510).
    // Every run keys (so its greens can be published); only a run that owes
    // nothing a reuse cannot pay looks anything up. `confirm` owes a run of
    // everything. An unreadable ledger is owed a full suite that passed. A run
    // owed a full-suite baseline looks up too: a project whose inputs are
    // byte-identical to another checkout's green had every test run at exactly
    // these inputs, and a run whose every project ran or held such a reuse earns
    // the baseline (`CompletionStages`). So a fresh checkout of an already-tested
    // tree launches only what misses (AUTOMATION-1508).
    let evidenceLookups = not scopeIsFullSuite && not ledgerUnreadable

    let evidencePlan () =
        evidencePlanFor
            ctx
            configs
            (fun project -> Map.isEmpty affectedByProject || Map.containsKey project affectedByProject)
            evidenceLookups
            inputs.OutstandingFailures

    /// The run's SCOPE, in the same shape `executeTests`
    /// will actually honour, captured on the launch so the completion
    /// handler knows what this run is entitled to clear.
    ///
    /// Mirrors `executeTests`' own reading of `affectedClassesByProject`
    /// EXACTLY, which is why it is derived from that map and not
    /// re-decided: an EMPTY map means "no selection" → every project runs
    /// in full; a NON-empty map means a project present with `[]` runs in
    /// full, a project present with classes runs filtered, and a project
    /// ABSENT is skipped entirely (and recorded as a filtered pass that
    /// proves nothing — the laundering vector).
    let wholeSelection: Map<string, ProjectSelection> =
        selectionOf configs affectedByProject

    // CHECK-SKIP CLASSES. A project this launch takes whole only for widenings
    // its `checkSkipOn` lists runs without its `checkSkipClasses`, under its
    // exclude filter; any other reason runs them. `confirm` is such a reason, so
    // it never skips. The classes the run selected by name always run.
    let checkSkipWidenings: CheckSkipLaunch.Widenings =
        { FullSuiteRequested = scopeIsFullSuite
          LedgerUnreadable = ledgerUnreadable
          NoFullSuiteBaseline = Option.isSome baselineInvalid
          CoarseGaps = not (Set.isEmpty coarseGaps)
          EvidenceGap = evidenceGap
          DependencyFanout = fanoutProjects
          RuntimeCoverage = runtimeForceProjects
          ProjectLevelReds =
            quarantinedAffectedByProject
            |> Map.filter (fun _ classes -> List.isEmpty classes)
            |> Map.keys
            |> Set.ofSeq
          EmptySelection =
            if Map.isEmpty affectedByProject then
                Some(
                    zeroAffectedWidening
                        hasCachedResults
                        inputs.Debt.UnknownDebt
                        (Set.count inputs.Debt.PendingQueue)
                        inputs.Debt.RuntimeObligations
                        (inputs.Debt.CoverageNotIngested |> Map.keys |> Set.ofSeq)
                        (List.length inputs.OutstandingFailures)
                        baselineInvalid
                        evidenceGap
                        (not (ReceiptInputTree.matches inputs.EvidenceTree (inputTree |> Option.map _.Identity)))
                )
            else
                None }

    let namedClasses: Map<string, Set<string>> =
        let symbolFreeClasses =
            match symbolFreeEdit with
            | Some(SymbolFreeEdit.Covering(_, _, classes, _)) -> classes
            | _ -> Map.empty

        [ quarantinedAffectedByProject; AlwaysClasses.only configs; symbolFreeClasses ]
        |> List.collect Map.toList
        |> List.groupBy fst
        |> List.map (fun (project, entries) -> project, entries |> List.collect snd |> Set.ofList)
        |> Map.ofList

    let checkSkipDecisions =
        CheckSkipLaunch.decide configs checkSkipWidenings namedClasses wholeSelection

    let excludedByProject = CheckSkipLaunch.excludedOf checkSkipDecisions

    for line in CheckSkipLaunch.describe checkSkipDecisions do
        Logging.info "test-prune" line

    let selection: Map<string, ProjectSelection> =
        CheckSkipLaunch.narrow excludedByProject wholeSelection


    // Which cause put each project in this run. Every source is kept apart
    // up to here; unioned, a wide selection cannot be told from an honest one.
    let selectedBy =
        let everyProject = fullSuiteProjects configs

        let sources: SelectionSources =
            { FullSuite =
                if scopeIsFullSuite || ledgerUnreadable || Option.isSome baselineInvalid then
                    everyProject
                else
                    Set.empty
              CoarseFallback = if Set.isEmpty coarseGaps then Set.empty else everyProject
              EvidenceGap = evidenceGap
              DependencyFanout = fanoutProjects
              RuntimeCoverage = runtimeForceProjects
              OutstandingFailure =
                quarantinedAffectedByProject
                |> Map.filter (fun project classes -> Map.tryFind project symbolAffectedByProject <> Some classes)
                |> Map.keys
                |> Set.ofSeq
              CompositionRootRestored =
                match symbolFreeEdit with
                | Some(SymbolFreeEdit.Covering(_, _, _, restored)) -> Set.union selectionRestored restored
                | _ -> selectionRestored
              ChangedSymbols = symbolAffectedByProject |> Map.keys |> Set.ofSeq
              SymbolFreeEdit =
                match symbolFreeEdit with
                | Some(SymbolFreeEdit.Covering(_, _, classes, _)) -> classes |> Map.keys |> Set.ofSeq
                | _ -> Set.empty
              AlwaysClasses = AlwaysClasses.only configs |> Map.keys |> Set.ofSeq }

        SelectionAttribution.attribute sources affectedByProject selection
        |> CheckSkipLaunch.attribute excludedByProject

    // The SAME derivation with the full-suite widening taken
    // back out — what `check` would have launched over this tree, this
    // instant. Every OTHER widening stays: the coarse fallback
    // for unanalysable files and the unreadable-ledger fallback
    // apply to the inner loop too, so removing them would project a selection
    // narrower than the one `check` actually uses and manufacture misses the
    // selector never made.
    //
    // Computed here, at the chokepoint, and NOT re-derived later: by
    // completion the symbol queue has moved on, which is exactly why the
    // reading was previously unrecoverable.
    let wouldHaveRun: Map<string, ProjectSelection> option =
        if not scopeIsFullSuite then
            // Nothing was widened past — the run IS the impact selection, and
            // there is no second reading to project.
            None
        else
            wouldHaveRunSelection
                configs
                quarantinedAffectedByProject
                (Set.union (coarseFallbackProjects configs coarseGaps fanoutProjects) evidenceGap)
                runtimeForceProjects
                ledgerUnreadable
            // What `check` would have left out of the projects it took whole:
            // the same decision, without the full-suite widening.
            |> fun projected ->
                let checkWidenings =
                    { checkSkipWidenings with
                        FullSuiteRequested = false
                        NoFullSuiteBaseline = false
                        EmptySelection = None }

                CheckSkipLaunch.narrow
                    (CheckSkipLaunch.excludedOf (CheckSkipLaunch.decide configs checkWidenings namedClasses projected))
                    projected
            |> Some

    let launch =
        { InputTree = inputTree
          ModelGeneration = observeModelGeneration ctx
          ProjectInputs = observeTestInputs ctx
          Symbols = launchedSymbols
          SymbolRevisions = launchedRevisions
          ChangedFiles = inputs.ChangedFiles
          TestHostStartedAt = None
          CoveringProjectsBySymbol = coveringProjectsBySymbol
          RuntimeProjectsByFile = launchedRuntimeObligations
          Selection = selection
          WouldHaveRun = wouldHaveRun
          Reused = Map.empty
          Seeds = narrowedSeeds |> Option.defaultValue inputs.Seeds
          ZeroSelection = ZeroSelection.NotAZero
          FullSuiteCause = fullSuiteCause
          SelectedBy = selectedBy
          SeedFanout = inputs.SeedFanout
          Price = None
          TraceShadow = traceShadow }

    { EvidencePlan = evidencePlan
      ExcludedByProject = excludedByProject
      Launch = launch }

/// The zero-affected skip: an impact run with nothing to verify finishes green with a
/// degenerate lifecycle that executed nothing, so it covers and clears nothing.
let internal zeroAffectedSkip
    (emitStarted: TestRunStarted -> unit)
    (inputs: TestRunInputs)
    (launch: TestRunLaunch)
    (nothingToVerify: bool)
    : Async<TestPruneMsg> =
    async {
        if nothingToVerify then
            Logging.info
                "test-prune"
                "Every changed symbol has no covering test — nothing to verify, skipping tests (green, 0 ran)"
        else
            Logging.info
                "test-prune"
                "No affected classes, no dependency fanout, empty pending queue, baseline exists — skipping tests"

        // Build a degenerate lifecycle (Started → Completed with empty
        // Results). Start fires immediately; the synchronous Custom handler
        // emits completion inside the cache-write capture window.
        let runId = Guid.NewGuid()

        let started: TestRunStarted =
            { RunId = runId
              StartedAt = DateTime.UtcNow }

        emitStarted started

        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = TimeSpan.Zero
              Outcome = Normal
              Results = Map.empty
              // Impact analysis selected no project, so none was invoked.
              // Stated rather than inferred, and the
              // outcome is `Normal`, so it reaches consumers that filter
              // on `Outcome`.
              Verification = NoProjectsSelected }

        // The skip EXECUTES NOTHING, so it covers nothing and may clear
        // nothing. Empty results already yield an empty
        // `RunCoverage`; the empty selection says so at the source too, so
        // the "0 affected, green, 0 ran" path can never be mistaken for
        // evidence about a project.
        return
            TestsFinished(
                started,
                completed,
                { launch with
                    Selection = Map.empty
                    SelectedBy = []
                    SeedFanout = SeedFanoutReading.NoSelection
                    Price = None
                    // Executed nothing, so it neither covers nor projects
                    // anything.
                    WouldHaveRun = None
                    Reused = Map.empty
                    ZeroSelection =
                        (match inputs.ChangedSymbolsAllUncovered with
                         | UncoveredChanges.AllUncovered(symbols, unrunnable) ->
                             ZeroSelection.ChangesUncovered(symbols, unrunnable)
                         | UncoveredChanges.No -> ZeroSelection.AlreadyVerified) }
            )
    }

/// Log why an impact run that launches selected what it did: a zero-affected run that
/// still launches says why its skip was refused, and every launch logs its selectivity.
let internal logLaunchSelection
    (inputs: TestRunInputs)
    (hasCachedResults: bool)
    (widenings: LaunchWidenings)
    (selected: LaunchSelection)
    (excludedByProject: Map<string, string list>)
    (treeUnverified: bool)
    =
    let baselineInvalid = widenings.BaselineInvalid
    let evidenceGap = widenings.EvidenceGap
    let affectedByProject = selected.AffectedByProject
    let totalClasses = selected.TotalClasses
    let symbolFreeEdit = selected.SymbolFreeEdit
    let narrowedSeeds = selected.NarrowedSeeds
    let alwaysOnly = selected.AlwaysOnly

    if totalClasses = 0 && narrowedSeeds.IsNone && not alwaysOnly then
        // NAME the debt that refused the zero-affected
        // skip, and say what will actually run. Both halves were wrong
        // before: the cause was hard-coded to two of five possibilities,
        // and "running all tests" was printed over runs that were a
        // couple of force-run projects.
        let widenings =
            zeroAffectedWidening
                hasCachedResults
                inputs.Debt.UnknownDebt
                (Set.count inputs.Debt.PendingQueue)
                inputs.Debt.RuntimeObligations
                (inputs.Debt.CoverageNotIngested |> Map.keys |> Set.ofSeq)
                (List.length inputs.OutstandingFailures)
                baselineInvalid
                evidenceGap
                treeUnverified

        // An EMPTY selection is not "a few projects": `selectionOf` reads
        // it as no selection at all and every configured project runs in
        // full. That is the expensive outcome, so it is the one named.
        let willRun =
            if Map.isEmpty affectedByProject then
                "EVERY configured project, in full"
            else
                $"%d{affectedByProject.Count} force-run project(s)"

        match widenings with
        | [] ->
            // Nothing is owed and a baseline exists, so the zero-affected
            // skip should have discharged this cycle for free — and did
            // not. That is a defect in whichever arm consumed the signal,
            // and it costs `willRun`. The phantom
            // runtime-coverage obligation was exactly this shape and left
            // no attributable line at all; this one is the alarm that a
            // DIFFERENT arm has re-opened the same hole.
            Logging.warn
                "test-prune"
                $"No affected classes, and NOTHING is owed — the zero-affected skip should have discharged \
                  this cycle without running anything, yet it is running %s{willRun}. Some verification \
                  debt is being reported as outstanding while naming nothing that can select or discharge \
                  it."
        | causes ->
            let notNarrowed =
                match symbolFreeEdit with
                | Some(SymbolFreeEdit.Refused reason) ->
                    $" (not narrowed to the edited files' covering classes: %s{reason})"
                | _ -> ""

            Logging.info
                "test-prune"
                $"No affected classes, but the zero-affected skip is refused — \
                  %s{ZeroAffectedWidening.describeMany causes}; running %s{willRun}%s{notNarrowed}"
    else
        for (proj, classes) in affectedByProject |> Map.toList do
            // Never `%A` here: it caps the list at 100, so a
            // 1,500-class blowout reads like a 100-class one, and it
            // renders an EMPTY list as `[]` — which here means the
            // project runs UNFILTERED, the exact opposite of "nothing
            // selected". `describeMany` leads with the exact count.
            let rendered =
                match classes, Map.tryFind proj excludedByProject with
                | [], Some skipped ->
                    $"ALL but %d{List.length skipped} checkSkipClasses (whole-project, exclude filter)"
                | [], None -> "ALL (unfiltered — force-run)"
                | classes, _ -> describeMany classes

            Logging.info "test-prune" $"Affected classes for %s{proj}: %s{rendered}"

    // Run-level selectivity in ONE line, since the per-project lines
    // above can be interleaved or truncated. Separates the two ways a
    // run can be wide: many classes named, versus projects unfiltered.
    let unfilteredProjects =
        affectedByProject |> Map.filter (fun _ cs -> List.isEmpty cs) |> Map.count

    Logging.info
        "test-prune"
        $"Selectivity: %d{affectedByProject.Count} project(s) selected, \
          %d{unfilteredProjects} of them UNFILTERED (whole-project), \
          %d{affectedByProject |> Map.values |> Seq.sumBy List.length} class(es) named in total"
