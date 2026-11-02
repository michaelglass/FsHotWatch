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
open FsHotWatch.TestPrune.RunExecution
open FsHotWatch.TestPrune.AffectedSelection
open FsHotWatch.TestPrune.ImpactRun










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





/// What `onFileCheckedWith` reads from the plugin. The freshness view stays a plugin cell:
/// `FreshnessStore` reads it and `UpdateFreshness` writes it when the handler calls them.
type internal FileCheckedEnv =
    { RepoRoot: string
      Db: Database
      PriorRows: FileFreshness.PriorRowLedger
      FreshnessStore: unit -> FileFreshness.Store
      UpdateFreshness: FileFreshness.Store -> unit
      EnqueuePending: string list -> VerificationDebt -> VerificationDebt }

/// One file's check result under the current model: its analysis, freshness and changed symbols.
let internal onFileCheckedWith
    (env: FileCheckedEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (modelGeneration: int64 option)
    (result: FileCheckResult)
    =
    let repoRoot = env.RepoRoot
    let db = env.Db
    let priorRows = env.PriorRows
    let freshnessStore = env.FreshnessStore
    let updateFreshness = env.UpdateFreshness
    let enqueuePending = env.EnqueuePending

    async {
        let analysisStarted = DateTime.UtcNow
        let fileStr = AbsFilePath.value result.File
        let relPath = Path.GetRelativePath(repoRoot, fileStr).Replace('\\', '/')

        // The ONE treatment for a file whose symbol analysis
        // failed (an `analyzeSourceFromResults` Error and a handler fault are the same
        // condition). The file is REMEMBERED as unanalysable, with three
        // consequences, none silent:
        //   1. a WARNING lands in the error ledger, keyed to the file, so
        //      `fshw check` prints it and — under the default warn-fail
        //      policy — refuses a green verdict;
        //   2. `runTestsWithImpact` falls back to EVERY test project in full
        //      while the set is non-empty;
        //   3. the non-empty force-run set disables the zero-affected skip
        //      gate, so this can never end as "0 affected — green, 0 ran".
        // The file leaves the set the moment it analyses cleanly.
        //
        // The Failed stamp needs no idle guard: the framework's ReportStatus
        // funnel drops any terminal stamped while an exclusive run is in
        // flight (the run owns the status), so a mid-run analysis failure
        // cannot manufacture a terminal. The ledger entry and the
        // force-full-suite consequence persist either way.
        let markUnanalysable (reason: string) (detail: string) (logDetail: string) : TestPruneState =
            match FileFreshness.resolvePresence repoRoot relPath with
            | FileFreshness.Gone ->
                // A path that no longer exists is GONE, not a
                // file that failed analysis: there is no source to be invisible
                // and no parse error to fix. Warning here named files a merge had
                // deleted, and the warning denied the gate its green. Forget the
                // file everywhere this plugin remembers it instead.
                Logging.info
                    "test-prune"
                    $"%s{relPath} no longer exists; dropping it rather than reporting '%s{reason}' (%s{detail})"

                updateFreshness (
                    FileFreshness.stamp FileFreshness.Gone false DateTime.UtcNow relPath (freshnessStore ())
                )

                { state with
                    AnalysisFiles = Map.remove result.File state.AnalysisFiles
                    UnanalyzableFiles = Map.remove relPath state.UnanalyzableFiles
                    Unindexed = Set.remove relPath state.Unindexed }
            | FileFreshness.Present ->
                Logging.error
                    "test-prune"
                    $"%s{reason} for %s{relPath}: %s{logDetail} — this file is INVISIBLE to the impact graph (no symbols), so every test project will be run in full until it analyses cleanly"

                ctx.ReportErrors fileStr [ unanalyzableFileDiagnostic relPath detail ]

                ctx.ReportStatus(
                    PluginStatus.Failed(
                        $"%s{reason}: %s{detail}",
                        DateTime.UtcNow,
                        RunVerdict.create $"%s{reason}: %s{detail}" (DateTime.UtcNow - analysisStarted)
                    )
                )

                { state with
                    AnalysisFiles =
                        Map.add
                            result.File
                            (AnalysisFileEvidence.fromResult ctx.FcsSuppressedCodes result (Error detail))
                            state.AnalysisFiles
                    UnanalyzableFiles =
                        Map.add
                            relPath
                            { RelPath = relPath
                              File = fileStr
                              Reason = detail }
                            state.UnanalyzableFiles }

        try
            // Canonical project identity. For real .fsproj files, FCS
            // gives "MyProject.fsproj" → "MyProject". For .fsx scripts
            // FCS synthesizes "Lib.fsx.fsproj" → "Lib.fsx" after one
            // strip; drop the trailing ".fsx" so config that specifies
            // `"Lib"` matches both cases.
            let projectName =
                match result.CheckResults with
                | ParseOnly _ ->
                    // Analysis below fails explicitly before this identity is
                    // consumed. Do not inspect ProjectOptions: an aborted FCS
                    // check may not carry usable options either.
                    ""
                | Replayed _ -> raise (ReplayedResultException("test-prune symbol analysis", fileStr))
                | FullCheck _ ->
                    let raw = result.ProjectOptions.ProjectFileName |> Path.GetFileNameWithoutExtension

                    if raw.EndsWith(".fsx") then
                        raw.Substring(0, raw.Length - 4)
                    else
                        raw

            // The per-file freshness sidecar gates the `detectChanges` call
            // site, not the symbol-DB write: dirty FCS results are still
            // persisted (cold-scan rows must go in), and the sidecar records
            // `fcsClean = false` so a cross-restart replay treats those rows
            // as untrusted-for-diff. The next clean recheck overwrites the
            // rows and flips the sidecar back.
            let currentClean =
                not (hasFcsErrors (FileCheckResult.suppression ctx.FcsSuppressedCodes result) result.CheckResults)

            let storedFreshness =
                let store = freshnessStore ()
                FileFreshness.classify relPath store

            // FileChecked is the completion payload from the daemon's warm FCS
            // pass. Re-entering FSharpChecker here duplicated that expensive
            // work and could observe a different compiler state than the event
            // being handled. TestPrune 8.1 accepts the existing parse/check
            // results directly. ParseOnly is not analysable: spelling that as
            // Error keeps the established fail-closed full-suite fallback.
            // Checked under a virtual root, the results name the file and
            // every symbol by virtual path: analysis asks for the file by that
            // name, and repository-relative paths come from the virtual root,
            // the same in every worktree.
            let analysisRoot =
                result.Frame
                |> Option.fold (fun _ (frame: PathFrame.PathFrame) -> frame.Virtual) repoRoot

            let analysisFile = PathFrame.nameIn result.Frame fileStr

            let analysisResult =
                match result.CheckResults with
                | FullCheck(parseResults, checkResults) ->
                    analyzeSourceFromResults analysisFile result.Source parseResults checkResults projectName
                | Replayed _ -> raise (ReplayedResultException("test-prune symbol analysis", fileStr))
                | ParseOnly _ ->
                    Error "FCS produced parse-only results; full type-check results are required for impact analysis"

            match analysisResult with
            | Ok analysisResult ->
                // Said only once the analysis has produced symbols to persist:
                // a syntax error refuses the analysis below, and that file is
                // reported as unanalysable instead (AUTOMATION-1391).
                if not currentClean then
                    let errCount =
                        fcsErrorCount (FileCheckResult.suppression ctx.FcsSuppressedCodes result) result.CheckResults

                    Logging.warn
                        "test-prune"
                        $"FCS reported %d{errCount} error(s) for %s{relPath}; persisting symbols but marking file dirty in freshness sidecar (Phase B detectChanges will fall back for this file)"

                let normalizedSymbols = normalizeSymbolPaths analysisRoot analysisResult.Symbols

                let fileAnalysis =
                    { Symbols = normalizedSymbols
                      Dependencies = analysisResult.Dependencies
                      TestMethods =
                        analysisResult.TestMethods
                        |> List.map (fun t -> { t with TestProject = projectName })
                      Attributes = analysisResult.Attributes
                      ParentLinks = analysisResult.ParentLinks
                      Diagnostics = analysisResult.Diagnostics }

                // Read stored symbols from the in-memory snapshot (populated after
                // each flush). Falls back to DB for warm starts where the snapshot
                // hasn't been populated yet.
                let storedSymbols =
                    match Map.tryFind relPath state.SymbolSnapshot with
                    | Some symbols -> symbols
                    | None -> db.GetSymbolsInFile(relPath)

                // WHOSE rows are these? `storedSymbols` is
                // non-empty both when a prior run indexed this file and when
                // THIS run did, seconds ago — and those mean opposite things.
                // The ledger remembers the first look, which is necessarily
                // taken before this session wrote anything for the file, so
                // it survives the snapshot short-circuit above.
                let storedRows = priorRows.Classify(relPath, not storedSymbols.IsEmpty)

                // Accumulate per-project; flush on BuildCompleted.
                // Replace any prior analysis for this file to avoid double-counting
                // when a file is checked more than once before the flush (e.g. initial
                // scan followed by a file-change recheck).
                let existingForProject =
                    state.PendingAnalysis |> Map.tryFind projectName |> Option.defaultValue []

                let filteredExisting =
                    existingForProject
                    |> List.filter (fun a -> not (a.Symbols |> List.exists (fun s -> s.SourceFile = relPath)))

                let newPending =
                    state.PendingAnalysis
                    |> Map.add projectName (filteredExisting @ [ fileAnalysis ])

                // Can the stored rows be diffed against? The CURRENT
                // extraction must be FCS-clean (a dirty current result means
                // the just-extracted symbols are themselves suspect); given
                // that, `FileFreshness.trustStoredRows` decides, from the
                // sidecar's verdict plus what the index HOLDS for this file
                // and SINCE WHEN. Every arm of that pair is load-bearing and
                // all are documented there —
                // `EverySymbolIsNew`, which is what a `Clean` stamp means once
                // a schema recreate has emptied the index underneath it, and
                // `RowsFromThisRun`, which is what rows mean
                // when this run is the one that wrote them.
                let storedTrust = FileFreshness.trustStoredRows storedFreshness storedRows

                // `planLook` is the whole decision. Its only way of
                // contributing nothing is the one that HIDES a file's changes,
                // and it goes out at warn. It replaced a `(names, bool)` pair
                // whose bool this call site computed and then `ignore`d.
                let changedNames, notSelected =
                    match FileFreshness.planLook currentClean storedTrust with
                    | FileFreshness.Diffable baseline ->
                        // WHAT to diff against is the plan's decision, not
                        // `storedSymbols`'s. This call site used to pass the
                        // stored rows for BOTH diffable arms, which made
                        // `EverySymbolIsNew` behave exactly like
                        // `DiffAgainstStored` — and whenever the rows were
                        // this run's own, that is the self-comparison
                        // the `RowsFromThisRun` clock was introduced to replace.
                        // A self-comparison reports zero changes every time.
                        let priorSymbols = FileFreshness.baselineRows baseline storedSymbols

                        let (changes, _events) = detectChanges normalizedSymbols priorSymbols

                        // A trustworthy extraction has now
                        // been taken AND consumed, so the rows it leaves
                        // behind are a real "before" for the next look —
                        // which is what keeps a missing baseline costing ONE
                        // widening per file rather than one on every save.
                        // Marked only here: the bypass arms below discard
                        // their extraction, and a baseline they never
                        // established must not be claimed.
                        priorRows.MarkBaselineEstablished relPath

                        Logging.info
                            "test-prune"
                            $"detectChanges for %s{relPath} (stored=%A{storedFreshness}, rows=%A{storedRows}, trust=%A{storedTrust}, baseline=%A{baseline}): %d{changes.Length} changes, %d{priorSymbols.Length} diffed against, %d{normalizedSymbols.Length} current"

                        changedSymbolNames changes, Map.remove relPath state.NotSelected
                    | FileFreshness.FileUnverified ->
                        // The one arm that DROPS a file's changes. At warn,
                        // and stating the consequence rather than the
                        // mechanism, because "detectChanges bypassed" at info
                        // is what this defect looked like for three runs.
                        Logging.warn
                            "test-prune"
                            $"NOT SELECTED: %s{relPath} changed but FCS reported errors for it on this check, so its symbols may be partial and no tests were selected from it (stored=%A{storedFreshness}, rows=%A{storedRows}, storedRowCount=%d{storedSymbols.Length}). The next FCS-clean check of this file widens it back in."

                        [], Map.add relPath NotSelectedReason.FcsErrors state.NotSelected

                let newChangedSymbols, newDebt =
                    if not changedNames.IsEmpty then
                        // These feed straight into `enqueuePending`, so this
                        // line is the primary evidence when diagnosing over-
                        // or under-selection. `describeMany`, not `%A` —
                        // the count must be exact and uncapped.
                        Logging.info "test-prune" $"Changed symbols: %s{describeMany changedNames}"

                        // Queued at the SAME point the in-memory hot view
                        // accumulates, and written durably before the BatchChecked
                        // analysis flush advances the symbol snapshot. They leave
                        // the queue only when a covering test run passes
                        // (TestsFinished) or they prove to have no covering test
                        // (flushAndQueryAffected).
                        (state.ChangedSymbols @ changedNames) |> List.distinct, enqueuePending changedNames state.Debt
                    else
                        state.ChangedSymbols, state.Debt

                // Only track file as changed if its AST actually changed.
                // Comment-only changes produce the same symbol hashes, so they
                // should not trigger extension-based tests (e.g. Falco routes).
                let newChangedFiles =
                    let runtimeRelevantChange = not changedNames.IsEmpty || fileAnalysis.Symbols.IsEmpty

                    if runtimeRelevantChange && not (state.ChangedFiles |> List.contains relPath) then
                        relPath :: state.ChangedFiles
                    else
                        state.ChangedFiles

                // Update class→file mapping for test methods found in this file
                let newClassFiles =
                    fileAnalysis.TestMethods
                    |> List.fold (fun acc t -> Map.add t.TestClass fileStr acc) state.TestClassFiles

                // `AffectedTests` is set exclusively by `flushAndQueryAffected`
                // on BuildCompleted and consumed by `runTestsWithImpact`; the
                // `affected-tests` IPC command computes its own answer on
                // demand from `ChangedSymbols` against the current DB.
                let newState =
                    { state with
                        Debt = newDebt
                        NotSelected = notSelected
                        ChangedFiles = newChangedFiles
                        PendingAnalysis = newPending
                        PendingIdentities =
                            Map.add
                                relPath
                                (IndexedAnalysis.identityOf repoRoot result
                                 |> Option.defaultWith IndexedAnalysis.undescribed)
                                state.PendingIdentities
                        AnalysisModelGeneration = modelGeneration
                        AnalysisFiles =
                            Map.add
                                result.File
                                (AnalysisFileEvidence.fromResult ctx.FcsSuppressedCodes result (Ok()))
                                state.AnalysisFiles
                        ChangedSymbols = newChangedSymbols
                        TestClassFiles = newClassFiles
                        // The file analysed cleanly, so it is back in the impact
                        // graph and no longer owes the coarse fallback. The
                        // framework already cleared this plugin's ledger entries
                        // for the file when the FileChecked arrived, and dropping
                        // it here means the next test run's ledger rewrite stops
                        // re-reporting the warning: the entry
                        // leaves the ledger because the CONDITION cleared, which
                        // is the only reason any entry may leave it.
                        UnanalyzableFiles = Map.remove relPath state.UnanalyzableFiles }

                // Stamp the freshness sidecar with the result of THIS check.
                // After analysis, not at the top, so a failed `analyzeSourceFromResults`
                // cannot lock in a clean stamp for a file we have no symbols
                // for. `markClean` only with a BuildCompleted this session
                // (see `BuildCompletedInThisSession`) AND a clean FCS result;
                // otherwise `markUnverified`, which will not downgrade a
                // previously-clean entry to dirty.
                let now = DateTime.UtcNow

                // A check of a path that has since vanished
                // forgets the record rather than re-stamping it — see
                // `FileFreshness.stamp`.
                let updatedFreshness =
                    FileFreshness.stamp
                        (FileFreshness.resolvePresence repoRoot relPath)
                        (currentClean && state.BuildCompletedInThisSession)
                        now
                        relPath
                        (freshnessStore ())

                updateFreshness updatedFreshness

                // A helper analysis is not new evidence about a test red. Keep
                // the fresh run's terminal provenance until a covering run
                // clears the outstanding failure.
                let analysisFinished = DateTime.UtcNow

                if List.isEmpty state.OutstandingFailures then
                    ctx.ReportStatus(
                        Completed(
                            analysisFinished,
                            RunVerdict.create
                                $"symbol analysis: %s{relPath}, %d{List.length changedNames} changed symbol(s); no run due"
                                (analysisFinished - analysisStarted)
                        )
                    )

                return newState
            | Error msg ->
                // On analysis failure the file must NOT be dropped: a
                // dropped file contributes no symbols, a change to it diffs
                // against nothing and selects NO tests, and the check reports
                // green having run nothing relevant. Silent under-selection:
                // the one failure mode a test-impact tool must not have. See
                // `markUnanalysable` for the treatment.
                return markUnanalysable "Analysis failed" msg msg

        with ex ->
            // A fault ANYWHERE in this handler — not just an `analyzeSourceFromResults`
            // Error — leaves the file unanalysed, the same condition and so
            // the same treatment.
            return markUnanalysable "FileChecked handler failed" ex.Message (ex.ToString())
    }

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
    { RepoRoot: string
      TestConfigs: TestConfig list option
      RunnableProjects: Set<string>
      Launch: LaunchSteps
      DischargeCoveredBootScan: PluginCtx<TestPruneMsg> -> BatchChecked -> TestPruneState -> TestPruneState
      LaunchHeld: PluginCtx<TestPruneMsg> -> TestPruneState -> string -> TestPruneState
      LogRedStands: string -> TestPruneState -> unit
      OwedDescription: VerificationDebt -> string
      OnlyRetriesRed: TestPruneState -> bool }

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
            // The tree these check results describe, for `DiffWait`: read after
            // the fold, so bytes written since read as a moved tree.
            let flushedState =
                if flushedState.LastResults.IsSome then
                    { flushedState with
                        SealedTree = ReceiptInputTree.read repoRoot }
                else
                    flushedState

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
                            AwaitingDiff = None }
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
                    let hasCachedResults = flushedState.LastResults.IsSome
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
    { RepoRoot: string
      TestConfigs: TestConfig list option
      Launch: LaunchSteps
      HoldForDiff: StateStep
      JoinRefresh: StateStep
      LaunchScopeOf: TestRunInputs -> LaunchScope
      ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option }

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

                        // Only a launch with a session baseline can wait for the
                        // diff, and whether it does reads the extensions'
                        // failures. Without one, the run takes them itself.
                        let stateWithAffected =
                            if hasCachedResults then
                                joinRefresh ctx stateWithAffected
                            else
                                stateWithAffected

                        // Union this build's fanout with any pending
                        // fanout deferred from a prior mid-run build,
                        // then clear the pending set (it's being run).
                        let forceRunProjects = Set.union fanoutNow stateWithAffected.PendingForceRunProjects

                        let launchState =
                            { stateWithAffected with
                                PendingForceRunProjects = Set.empty }

                        let launchInputs = TestRunInputs.ofState launchState

                        let awaitsDiff =
                            DiffWait.awaits
                                { HasSessionBaseline = hasCachedResults
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
                                  CurrentTree = ReceiptInputTree.read repoRoot
                                  SealedTree = launchState.SealedTree }

                        if awaitsDiff then
                            return holdForDiff ctx launchState
                        else
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

/// The debt accounting a run's completion reads (`commitPending`, `debtScope` and the rest).
type internal CompletionDebt =
    { CommitPending: Set<string> -> VerificationDebt -> VerificationDebt
      CoveringOf: string seq -> string -> Set<string>
      DebtScope: Lazy<Map<string, string>> -> (string -> Set<string>) -> string -> Set<string>
      IndexNamesOnlyRunnable: Lazy<Map<string, string>> -> bool
      OwedElsewhere: (string -> Set<string>) -> string seq -> Set<string>
      RevisionOf: VerificationDebt -> string -> int64 }

/// What `onRunFinishedWith` reads from the plugin. The declared exclusions are resolved
/// when the handler calls `ResolveExcludedProjects`, as before.
type internal RunFinishedEnv =
    { RepoRoot: string
      RunnableProjects: Set<string>
      Traces: TraceWiring option
      ResolveExcludedProjects: unit -> Map<string, string>
      Debt: CompletionDebt
      EndRun: bool -> TestPruneState -> TestPruneState
      ObserveModelGeneration: PluginCtx<TestPruneMsg> -> int64 option }

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
    let commitPending = env.Debt.CommitPending
    let coveringOf = env.Debt.CoveringOf
    let debtScope = env.Debt.DebtScope
    let indexNamesOnlyRunnable = env.Debt.IndexNamesOnlyRunnable
    let owedElsewhere = env.Debt.OwedElsewhere
    let revisionOf = env.Debt.RevisionOf
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
            let failedKeys =
                match failedTests with
                | Ok failures ->
                    failures
                    |> List.choose (fun f ->
                        match f.Class, f.Method with
                        | Some cls, Some meth -> Some(TraceShadow.failureKey f.Project cls meth)
                        | _ -> None)
                | Error _ -> []

            TraceShadow.atCompletion
                wiring.Policy
                repoRoot
                launch.TraceShadow
                (completed.RunId.ToString "N")
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
            OutstandingFailure.carriedOver runnableProjects coverage passedTests priorFailures

        let outstandingFailures =
            OutstandingFailure.carry runnableProjects coverage passedTests foundFailures priorFailures

        if not carriedFailures.IsEmpty then
            Logging.info
                "test-prune"
                $"%d{carriedFailures.Length} failure(s) from an earlier run were NOT covered by this one (%s{OutstandingFailure.summarize carriedFailures}) — they stay RED until a run that executes them passes"

        // Discharged HERE, immediately before the ledger
        // rewrite, because this is the one place the outstanding set is
        // recomputed — pruning anywhere else would leave the ledger and the
        // state disagreeing about what is still owed.
        let unanalyzable = pruneDeletedUnanalyzable File.Exists state.UnanalyzableFiles

        // The ONLY path to the ledger: clear the slate, re-report the whole
        // outstanding set. There is no wholesale clear a filtered run can
        // reach for.
        reportOutstanding ctx unanalyzable state.FailedExtensions outstandingFailures

        // `LastCoverage` is the moment the process acquires test evidence — a
        // run completed and we know what it covered. Until a state carrying it
        // is supplied, the cache key refuses to let a cached BuildCompleted
        // assert a result this process never ran.
        let debtDuringFullRun = state.DebtDuringFullRun

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
                        revisionOf state.Debt symbol = (Map.tryFind symbol launch.SymbolRevisions
                                                        |> Option.defaultValue 0L))

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

        // The full-suite WATERMARK. Written when a full-suite
        // run has ACCOUNTED for every configured project: passed, or red with
        // the red recorded in the durable outstanding list. Not only for a
        // green run — a red full suite still proves what every other test did,
        // and its reds are quarantined until they pass — so the inner loop can
        // resume impact filtering (plus quarantine) after one full run rather
        // than running the whole suite until the last red is fixed. A project
        // that produced no accountable outcome (deferred, errored) is neither,
        // and `Ran FullSuite` is already false for it.
        //
        // The same `executedFullSuite` the unreadable-ledger discharge reads: two
        // readings of what a full-suite run is would let one recover a ledger
        // the other refused to baseline.
        let accountedFor (proj: string) =
            projectPassed proj
            || outstandingFailures |> List.exists (fun f -> f.Project = proj)

        // A run that reused evidence ran less than the suite here; its greens
        // stand, but a full-suite baseline is only ever earned by running.
        let earnsBaseline =
            executedFullSuite
            && Map.isEmpty launch.Reused
            && runnableProjects |> Set.forall accountedFor

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

        let state =
            { state with
                Debt = debt
                Earned = earned
                PendingForceRunProjects = Set.difference state.PendingForceRunProjects absorbedProjects
                CoveredFullSuite =
                    match launch.InputTree with
                    | Some tree when coveredByConstruction -> Some(tree, currentModelGeneration)
                    | _ -> None }

        // `PrepareCommit` writes no sidecar while what is owed is unknown, so a
        // baseline earned then lives only in this daemon's memory. Saying it was
        // recorded would claim a write that never happens.
        if earnsBaseline then
            let runId = completed.RunId.ToString("N")

            if debt.RecoveryOutstanding then
                Logging.info
                    "test-prune"
                    $"Full-suite baseline earned by run %s{runId} but not written: what is owed is still UNKNOWN (%s{describeUnknownDebt debt.UnknownDebt}), and nothing is written until a full suite passes every configured project. Until then every run, in this daemon and the next, is the full suite."
            else
                Logging.info
                    "test-prune"
                    $"Full-suite baseline recorded: run %s{runId} accounted for every configured project (%d{Set.count runnableProjects}); impact-filtered greens are now relative to it"

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

        // Pushing a terminal Completed/Failed status is what appends the
        // run to history; both rerun and final-idle branches must call this.
        let recordRunOutcome (results: TestResults) =
            let total = results.Results.Count

            // Reds this run did not cover are still RED —
            // they must deny it a green verdict exactly as its own failures
            // would, or `check` exits 0 with a failing test outstanding
            // (the whole defect). Named in every non-green message below so
            // the reason a passing run is not green is never a mystery.
            let carriedCount = carriedFailures.Length

            let carriedNote =
                if carriedCount = 0 then
                    ""
                else
                    $" (+%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures})"

            // Consult `completed.Outcome` FIRST. An Aborted run (beforeRun
            // threw, runner crashed, run cancelled) must be non-green
            // regardless of result counts — empty results trivially satisfy
            // "failed = 0 && deferred = 0" and would otherwise false-green.
            // Likewise a run that executed ZERO projects while the pending
            // queue still holds symbols verified nothing, so it takes the
            // honest "waiting on build (tests did not run)" path.
            let abortMessage =
                match completed.Outcome with
                | Aborted reason -> Some reason
                | Normal -> None

            match abortMessage with
            | Some reason ->
                ctx.ReportStatus(
                    PluginStatus.failedNow
                        $"test run aborted (tests did not run): %s{reason}%s{carriedNote}"
                        $"test run aborted: %s{reason}%s{carriedNote}"
                        results.Elapsed
                )
            | None when total = 0 && not (Set.isEmpty queueAfterCommit) ->
                // Zero projects executed but symbols still await
                // verification — honest non-green, same wording/path as a
                // deferred (never-ran) project.
                ctx.ReportStatus(
                    PluginStatus.failedNow
                        $"%s{pendingDescription ()}%s{carriedNote}"
                        $"0 projects ran; symbols still awaiting verification%s{carriedNote}"
                        results.Elapsed
                )
            // Projects RAN and every one matched zero tests, so
            // nothing executed and nothing was verified — not a green. The
            // ladder below counts zero matches OUT of both `passed` and
            // `failed`, so without this check `failed = 0 && deferred = 0`
            // holds, the green branch fires, and it reports "0 passed, 0
            // failed in N projects" about N projects that ran no test at all.
            //
            // Scoped to ALL projects deliberately: a zero match in ONE project
            // is a correct pass for it (an impact selection naming no class in
            // the Integration project must not fail that project). Only the
            // run-level verdict changes, and only when nothing matched
            // anywhere — a mis-aimed filter, never a verified pass. An
            // empty-results run stays on its existing "nothing to verify" path
            // because `Map.forall` is vacuously true for an empty map.
            | None when allZeroMatchOf results.Results ->
                ctx.ReportStatus(
                    PluginStatus.failedNow
                        $"%d{total} project(s) ran and matched ZERO tests — nothing was verified (not a pass)%s{carriedNote}"
                        $"%d{total} project(s) discovered their tests; the active filter matched none of them, so no test executed%s{carriedNote}"
                        results.Elapsed
                )
            | None ->

                // Non-green = anything this run did not verify green, MINUS
                // the zero matches (counted separately below). Split further
                // into genuine failures vs deferred (never-ran) so the verdict
                // can be honest: deferred is non-green but is "could not run /
                // waiting on build", NOT "failed".
                //
                // Written out per case, at the site. `not verifiedGreen`
                // would sweep the zero matches in here and report them as
                // failures; matching only `Refuted` would drop the
                // Deferred/Errored projects, which ARE non-green — they owed
                // a result and produced none.
                //
                // Per result, this match is the exact negation of
                // `noProjectRefutedOrUnrun` in `cacheKeyFor`, kept in agreement
                // BY HAND — see that site for why a shared helper would not
                // enforce it (the conditions around the two differ).
                let nonGreen =
                    results.Results
                    |> Map.toList
                    |> List.filter (fun (_, r) ->
                        match TestResult.verdict r with
                        | Verified -> false
                        | Refuted -> true
                        | NothingVerified -> not (TestResult.isNoMatch r))

                let deferredList = nonGreen |> List.filter (fun (_, r) -> TestResult.isDeferred r)

                // An ABORTED project — its test host died
                // mid-run — is counted OUT of `failed`, which used to sweep
                // it in (`not isDeferred`). Nothing failed there: the runner
                // never finished asking. Reporting it as "N failed: X" is a
                // non-result rendered as a definite negative, and it is the
                // exact shape a reader mistakes for a mass regression.
                let abortedList = nonGreen |> List.filter (fun (_, r) -> TestResult.isErrored r)

                let failedList =
                    nonGreen
                    |> List.filter (fun (_, r) -> not (TestResult.isDeferred r) && not (TestResult.isErrored r))

                let failed = failedList.Length
                let deferred = deferredList.Length
                let aborted = abortedList.Length

                // Zero-match projects are counted OUT of `passed`. `passed` is
                // derived by exclusion, and the `nonGreen` fold above
                // deliberately excludes them (a zero match is not a failure to
                // report), so without this subtraction a project that executed
                // no test is reported as one that passed — and the status line
                // then disagrees with the CLI, which counts them separately.
                let noMatch =
                    results.Results |> Map.filter (fun _ r -> TestResult.isNoMatch r) |> Map.count

                let passed = total - failed - deferred - aborted - noMatch

                let noMatchSuffix = if noMatch = 0 then "" else $", %d{noMatch} matched nothing"

                let anyFiltered =
                    results.Results |> Map.exists (fun _ r -> TestResult.wasFiltered r)

                let selectedSuffix = if anyFiltered then "yes" else "no"

                let timedOutProjects =
                    failedList
                    |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then Some name else None)

                // When 2+ projects ran and at least one has recorded elapsed,
                // surface the slowest in the summary so a bottlenecked project
                // is visible without having to query test-results JSON.
                let slowestSuffix =
                    if total < 2 then
                        ""
                    else
                        let withElapsed =
                            results.Results
                            |> Map.toList
                            |> List.choose (fun (name, r) ->
                                let e = TestResult.elapsed r
                                if e > TimeSpan.Zero then Some(name, e) else None)

                        match withElapsed with
                        | [] -> ""
                        | _ ->
                            let (n, e) = withElapsed |> List.maxBy snd
                            $", slowest: %s{n} %.1f{e.TotalSeconds}s"

                let deferredSuffix =
                    if deferred > 0 then
                        $", %d{deferred} waiting on build"
                    else
                        ""

                // Never folded into the `failed` number it sits beside: the
                // summary is the one line most readers see, so it is the one
                // line that must not call a dead host a failure.
                let abortedSuffix =
                    if aborted > 0 then
                        $", %d{aborted} ABORTED (host died, nothing verified)"
                    else
                        ""

                let abortedNames = abortedList |> List.map fst |> String.concat ", "

                if not timedOutProjects.IsEmpty then
                    let names = timedOutProjects |> String.concat ", "
                    // Flip the recorded outcome to TimedOut; the verdict on
                    // the Failed below carries the summary (one channel).
                    ctx.CompleteWithTimeout $"test project(s): {names}"

                    ctx.ReportStatus(
                        PluginStatus.failedNow
                            $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                            $"%d{timedOutProjects.Length} timed out: %s{names}%s{carriedNote}"
                            results.Elapsed
                    )
                else
                    let runSummary =
                        $"%d{passed} passed, %d{failed} failed%s{abortedSuffix}%s{deferredSuffix}%s{noMatchSuffix} in %d{total} projects (selected: %s{selectedSuffix}%s{slowestSuffix})"

                    // EVERY terminal below CARRIES the run's evidence —
                    // `runSummary` + measured duration — on the status
                    // itself. There is no separate summary channel left to
                    // forget or contradict.
                    if
                        failed = 0
                        && aborted = 0
                        && deferred = 0
                        && Set.isEmpty queueAfterCommit
                        && carriedCount = 0
                    then
                        // Nothing failed, nothing is owed — and on a
                        // run that EXECUTED NOTHING that is not a pass, it is an
                        // absence of evidence. `runSummary` would report it as
                        // "0 passed, 0 failed in 0 projects", which is how a plugin
                        // line goes `✓` for a check that verified nothing while the
                        // verdict layer is (correctly) refusing it exit 0.
                        //
                        // The STATUS stays `Completed`: nothing failed, and a
                        // `Failed` here would claim one — turning `check`'s honest
                        // exit 3 "NO VERDICT" into an exit 1 "failures found". It is
                        // the VERDICT that carries the fact — the run record becomes
                        // `RunOutcome.VerifiedNothing` — and every renderer keys its
                        // glyph off that case (`ParsedPluginStatus.verifiedNothing`).
                        //
                        // Asked of `RunVerification`, THE derivation, so this is a
                        // question about the RUN ("did anything execute?") and not
                        // about one selection bug: any future path that lands an
                        // executed-nothing run here is covered without a new arm.
                        let verdict =
                            match receiptTransition, evidenceReceipt with
                            | ReceiptTransition.Noop, Some retained when
                                RunVerification.verifiedNothing (verificationOf results.Results)
                                ->
                                // Nothing needed running because an earlier run
                                // already verified this tree and model, and its
                                // receipt stands: that run is the evidence, so
                                // the line names it rather than the empty skip.
                                let retainedRun = retained.RunId.ToString("N")

                                RunVerdict.create
                                    $"no test needed: nothing changed since run %s{retainedRun}, which verified this tree"
                                    results.Elapsed
                            | _ when RunVerification.verifiedNothing (verificationOf results.Results) ->
                                RunVerdict.verifiedNothing
                                    $"%d{total} test project(s) ran, no test executed"
                                    results.Elapsed
                            | _ -> RunVerdict.create runSummary results.Elapsed

                        ctx.ReportStatus(Completed(DateTime.UtcNow, verdict))
                    elif failed = 0 && aborted = 0 && deferred = 0 && Set.isEmpty queueAfterCommit then
                        // Everything this run RAN passed, the
                        // queue is drained — and yet an earlier failure it did
                        // not execute is still outstanding. A narrower run
                        // cannot vindicate a test it never ran, so this is
                        // NON-green: the red survives until a run that COVERS
                        // it passes (`dotnet fshw test-rerun` runs every
                        // project unfiltered and will clear it if it is fixed).
                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"%d{carriedCount} still red from an earlier run, not covered by this one: %s{OutstandingFailure.summarize carriedFailures}"
                                $"%s{runSummary}%s{carriedNote}"
                                results.Elapsed
                        )
                    elif failed = 0 && aborted = 0 && deferred = 0 then
                        // Everything that RAN passed, but the pending queue
                        // still holds symbols this (e.g. filtered) run did not
                        // cover green — NOT test-equivalent to a green run yet.
                        // Non-green with the honest "waiting on build" wording;
                        // the next BuildCompleted re-selects and runs them.
                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"%s{pendingDescription ()}%s{carriedNote}"
                                $"%s{runSummary}%s{carriedNote}"
                                results.Elapsed
                        )
                    elif failed = 0 && aborted = 0 then
                        // Only deferred projects — nothing FAILED, but
                        // nothing was verified either. Non-green, honest
                        // "waiting on build" (never "failed").
                        let names = deferredList |> List.map fst |> String.concat ", "

                        if carriedCount = 0 then
                            // PURE defer: no red this run or carried. A
                            // NON-failing terminal, so the verdict reads the
                            // `Deferred`-severity ledger entry and routes it to
                            // `Incomplete`/exit 2, not the exit 1 a red earns —
                            // a build-ordering race left one project unrun.
                            // A defer's `verdict` is `NothingVerified`, so its
                            // symbols never commit (the next build re-runs them)
                            // and the result is uncacheable.
                            ctx.ReportStatus(
                                PluginStatus.completedNow
                                    $"%s{runSummary} — %d{deferred} waiting on build (tests did not run): %s{names}"
                                    results.Elapsed
                            )
                        else
                            // A carried RED from an earlier run is still
                            // outstanding — that dominates a defer. Stay
                            // Failed/red (exit 1); the ledger's carried Error
                            // entry independently keeps the verdict red.
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"%d{deferred} waiting on build (tests did not run): %s{names}%s{carriedNote}"
                                    $"%s{runSummary}%s{carriedNote}"
                                    results.Elapsed
                            )
                    elif failed = 0 then
                        // Nothing failed — a test HOST DIED.
                        // Killed by a signal under load, or gone before it
                        // could write a report. No test was shown to fail and
                        // no test was shown to pass, so this is the third
                        // answer and it must LOOK like the third answer: a
                        // dead host reported as "N failed: X" sends the reader
                        // hunting a regression that is not there, which is the
                        // hours this arm exists to stop burning.
                        //
                        // A NON-failing terminal, exactly like the pure-defer
                        // arm above: the `HostAborted`-severity ledger entries
                        // route the verdict to `RunnerAborted`/exit 2 rather
                        // than the exit 1 a red earns. The verdict is a
                        // verified-nothing one (`RunOutcome.VerifiedNothing` on
                        // the run record), so no renderer can give this run a
                        // bare `✓` either.
                        let deferredNote =
                            if deferred > 0 then
                                let dn = deferredList |> List.map fst |> String.concat ", "
                                $" (+%d{deferred} waiting on build: %s{dn})"
                            else
                                ""

                        let abortLine =
                            $"%d{aborted} test host(s) ABORTED — killed mid-run, nothing verified (NOT a test failure): %s{abortedNames}%s{deferredNote}"

                        if carriedCount = 0 then
                            ctx.ReportStatus(PluginStatus.verifiedNothingNow abortLine results.Elapsed)
                        else
                            // A carried RED from an earlier run outranks an
                            // abort — same rule the defer arm applies, and for
                            // the same reason: an aborted run cannot vindicate
                            // a test it never ran. Stay red (exit 1); the
                            // carried Error entry keeps the ledger red anyway.
                            ctx.ReportStatus(
                                PluginStatus.failedNow
                                    $"%s{abortLine}%s{carriedNote}"
                                    $"%s{runSummary}%s{carriedNote}"
                                    results.Elapsed
                            )
                    else
                        let names = failedList |> List.map fst |> String.concat ", "

                        let deferredNote =
                            if deferred > 0 then
                                let dn = deferredList |> List.map fst |> String.concat ", "
                                $" (%d{deferred} waiting on build: %s{dn})"
                            else
                                ""

                        // A real red DOMINATES an abort beside it — but the
                        // abort is still NAMED, and named as an abort: those
                        // projects proved nothing, and a reader must not add
                        // them to the failure count.
                        let abortNote =
                            if aborted > 0 then
                                $" (+%d{aborted} ABORTED, nothing verified: %s{abortedNames})"
                            else
                                ""

                        ctx.ReportStatus(
                            PluginStatus.failedNow
                                $"%d{failed} failed: %s{names}%s{abortNote}%s{deferredNote}%s{carriedNote}"
                                $"%s{runSummary}%s{carriedNote}"
                                results.Elapsed
                        )

        // Whatever this run left owed is queued behind it as an intent by
        // the trigger that owed it (`enqueueImpactRun`, a queued `run-tests`),
        // and is delivered only after this fold is committed. Nothing is
        // launched from here.
        recordRunOutcome testResults

        let replies =
            match message with
            | CommandTestsFinished(_, _, _, reply, response) -> [ reply, response ]
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
                AffectedTests = Analyzed []
                Replies = replies }

    // An unavailable launch ran nothing. Its receipt is revoked and the fanout
    // it consumed is owed again. No run is queued: the next build or cohort
    // seal is what can make the artifacts or the host available, and it
    // launches with this debt.
    }

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
    let runTestHostExclusive
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

    /// Whether a `FileChecked` cache hit may stand in for analysing `result`
    /// (`IndexedAnalysis`): the index holds exactly this analysis, and no other is pending
    /// for the file. Analysis-only daemons keep the plain key.
    /// The analysis identity the index holds for a file (`IndexedAnalysis`). An index that
    /// cannot be read holds nothing it can vouch for.
    let indexedKey (relPath: string) : string option =
        try
            db.GetFileKey relPath
        with ex ->
            Logging.debug "test-prune" $"index identity unreadable for %s{relPath}: %s{ex.Message}"
            None

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

    /// Records, durably and before the run returns, that a project's runtime coverage did
    /// not ingest, then tells the owner through a message. A record that cannot be written
    /// is logged; the message still makes this session owe the project.
    let coverageIngestFailed (ctx: PluginCtx<TestPruneMsg>) (failure: CoverageIngestFailure) =
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


    /// Why the full-suite baseline cannot vouch for what a filtered run
    /// over the configured projects would skip — `None` when it can. Analysis-only
    /// daemons make no test claim and so owe no baseline.
    ///
    /// Unknown debt is folded in: the baseline composes with the queue (the queue names
    /// what changed since the baseline), so a baseline beside a ledger that cannot be
    /// read vouches for nothing until a full suite re-earns both.
    let baselineInvalidReason (debt: VerificationDebt) : string option =
        if Set.isEmpty runnableProjects then
            None
        elif debt.RecoveryOutstanding then
            Some "what is owed is unknown, so the full-suite baseline cannot say what changed since it was earned"
        else
            match debt.Baseline with
            | None -> Some FullSuiteBaseline.absentReason
            | Some baseline -> FullSuiteBaseline.staleness runnableProjects baseline

    /// The one question every skip in this plugin is really asking: is the
    /// needs-testing queue PROVABLY empty? Unknown debt is never `true` here — an empty
    /// queue we could not read is not an empty queue.
    ///
    /// An absent or stale full-suite baseline is owed work too — the
    /// tests a filtered run skips have nothing to be equivalent TO until one exists —
    /// so it is folded in here rather than checked beside this at each skip site.
    let nothingOwed (debt: VerificationDebt) =
        Set.isEmpty debt.PendingQueue
        && Map.isEmpty debt.RuntimeObligations
        && Map.isEmpty debt.CoverageNotIngested
        && not debt.RecoveryOutstanding
        && Option.isNone (baselineInvalidReason debt)

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

    /// Write the queue before the analysis flush advances the durable symbol snapshot:
    /// once the snapshot advances, a symbol missing from the durable queue can no longer
    /// be re-detected after a crash. Only ever ADDS to what is on disk — the state it
    /// writes is the published queue plus this event's enqueues — so writing it before
    /// publication can over-test, never under-test.
    ///
    /// Unknown debt is not written: the unreadable file is the honest record until the
    /// recovering full suite publishes a new one.
    let persistQueueAdditions (debt: VerificationDebt) =
        if not debt.RecoveryOutstanding then
            try
                PendingVerification.save repoRoot debt.PendingQueue
            with ex ->
                Logging.warn
                    "test-prune"
                    $"failed to persist pending-verification queue before the analysis flush: %s{ex.Message}; the queue is published with this event's state"

    /// The debt revision of `symbol`. A symbol loaded from the durable queue and never
    /// re-enqueued is at revision 0.
    let revisionOf (debt: VerificationDebt) (symbol: string) : int64 =
        debt.SymbolRevisions |> Map.tryFind symbol |> Option.defaultValue 0L

    /// Add `symbols` to the queue at one new revision, including a re-edit of a symbol
    /// already queued: a run captured before it cannot have built it.
    let enqueuePending (symbols: string list) (debt: VerificationDebt) =
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

    /// Remove `symbols` from the queue: a covering run passed them, or they have no
    /// covering test. Durable once the state carrying it is prepared.
    let commitPending (symbols: Set<string>) (debt: VerificationDebt) =
        if symbols.IsEmpty then
            debt
        else
            { debt with
                PendingQueue = Set.difference debt.PendingQueue symbols
                SymbolRevisions = debt.SymbolRevisions |> Map.filter (fun s _ -> not (Set.contains s symbols)) }

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
    let debtScope (excluded: Lazy<Map<string, string>>) (covering: string -> Set<string>) : string -> Set<string> =
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

    /// The test projects covering each symbol a classification pass is about: what its
    /// single-seed `QueryAffectedTests` would select from. One grouped query answers all
    /// of `symbols`, run the first time any symbol is asked about and never when none is.
    /// A symbol outside `symbols` is answered by a grouped query of its own.
    let coveringOf (symbols: string seq) : string -> Set<string> =
        let known = symbols |> Seq.distinct |> List.ofSeq
        let grouped = lazy (queries.CoveringProjectsBySeed known)

        fun symbol ->
            match Map.tryFind symbol grouped.Value with
            | Some projects -> projects
            | None ->
                queries.CoveringProjectsBySeed [ symbol ]
                |> Map.tryFind symbol
                |> Option.defaultValue Set.empty

    /// The unconfigured projects `owedTo` still waits on for `symbols`, for a message
    /// that has to name them.
    let owedElsewhere (owedTo: string -> Set<string>) (symbols: string seq) : Set<string> =
        symbols
        |> Seq.map (fun s -> Set.difference (owedTo s) runnableProjects)
        |> Set.unionMany

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

    /// The failures `outcomes` leave, from those before them: a refreshed extension
    /// leaves the map, a failed one enters it.
    let failuresAfter (outcomes: ExtensionRefresh list) (failed: Map<string, string>) =
        outcomes
        |> List.fold
            (fun failed outcome ->
                match outcome with
                | ExtensionRefresh.Refreshed(name, _) -> Map.remove name failed
                | ExtensionRefresh.Failed(name, ex) -> Map.add name ex.Message failed)
            failed

    /// Fold a refresh's outcomes into `state`: the log, the ledger, `FailedExtensions`.
    let applyRefresh
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

    /// Run every extension and time the whole refresh: it runs them in one call.
    let refreshTimed (exts: ITestPruneExtension list) () =
        let refreshClock = Diagnostics.Stopwatch.StartNew()
        let outcomes = refreshExtensionEdges db repoRoot exts
        outcomes, refreshClock.ElapsedMilliseconds

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
    let flushPending (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
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

                    Logging.info
                        "test-prune"
                        $"runtime coverage selected %s{file} -> %s{projectNames} (project-in-full)"

        reportRuntimeCoverageWidenings (Logging.warn "test-prune") runtimeSelection

        flushedState, runtimeObligations

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

    /// The generation of the model the host currently publishes, when it is available.
    let observeModelGeneration (ctx: PluginCtx<TestPruneMsg>) =
        ObservedModel.generation ctx.ProjectGraph

    /// Each configured test project's `.fsproj` and compile-input identity now.
    let observeTestInputs (ctx: PluginCtx<TestPruneMsg>) =
        ObservedModel.testInputs ctx.ProjectGraph (defaultArg testConfigs [] |> List.map _.Project)

    /// The evidence candidates of `configs`: each project the model knows a `.fsproj` for, with
    /// its reference closure and keyed configuration.
    let evidenceCandidates (graph: ProjectGraphAccessor) (configs: TestConfig list) : TestEvidenceRun.Candidate list =
        let files = ObservedModel.projectFiles graph

        configs
        |> List.choose (fun config ->
            Map.tryFind config.Project files
            |> Option.map (fun fsproj ->
                { TestEvidenceRun.Candidate.Project = config.Project
                  ProjectFile = fsproj
                  Closure = TestEvidenceRun.closureOf graph.GetProjectReferences fsproj
                  TestConfig = canonicalTestConfig config }))

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

    /// Hold a launch while the host discovers its project model (`ObservedModel.awaitDiscovery`),
    /// so the run is launched under the model it completes under. Called inside the launch's
    /// exclusive work, before anything about the model is read.
    let awaitDiscoveryBeforeLaunch (ctx: PluginCtx<TestPruneMsg>) =
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

    /// A launch, named by the model it was selected under and what it selected, so the
    /// completion line (`describeCompletionEvidence`) can be read against it.
    let logLaunch (kind: string) (mode: TestMode) (launch: TestRunLaunch) =
        let scope =
            if TestMode.requestsFullSuite mode then
                "confirm mode"
            else
                "check mode"

        Logging.info
            "test-prune"
            $"Launching %s{kind} run under project model generation %s{ObservedModel.describe launch.ModelGeneration} (%s{scope}, %d{Map.count launch.Selection} project(s) selected)"

    /// Which cause selected each project of a run that is about to execute. Logged once
    /// the run is past the zero-affected skip, which executes nothing and selects nothing.
    let logSelectedBy (launch: TestRunLaunch) =
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
    let commandForceRun
        (ctx: PluginCtx<TestPruneMsg>)
        // The mode the force run launches under: it decides whether it records traces.
        (mode: TestMode)
        (configs: TestConfig list)
        (filter: string option)
        (reply: Tasks.TaskCompletionSource<string>)
        : Async<TestPruneMsg> =
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

    let allCommands =
        pluginCommands
            { RepoRoot = repoRoot
              TestConfigs = testConfigs
              RunnableProjects = runnableProjects
              CurrentOutcomeKeys = currentOutcomeKeys
              BaselineInvalidReason = baselineInvalidReason
              Queries = queries }


    /// Owe one impact run after whatever holds the "tests" key. Coalesced: however many
    /// triggers arrive while the key is held, one run is queued, and it decides against
    /// the state it is delivered into (`impactRun`).
    let enqueueImpactRun (ctx: PluginCtx<TestPruneMsg>) =
        ctx.EnqueueExclusiveIntent "tests" (Some "impact") ImpactRunRequested |> ignore

    /// What a launch from `inputs` will execute: the widenings `runTestsWithImpact`
    /// applies to every project.
    let launchScopeOf (inputs: TestRunInputs) =
        if
            TestMode.requestsFullSuite inputs.Mode
            || inputs.Debt.RecoveryOutstanding
            || Option.isSome (baselineInvalidReason inputs.Debt)
        then
            LaunchedFullSuite
        else
            LaunchedSelection

    /// Claim the "tests" key for an impact launch from `launchState`. `Some` carries the
    /// state recording what the claimed run will execute; `None` means the key is held.
    ///
    /// A session that launches only requested runs (`TestMode.launchesOwedRuns`) claims
    /// nothing: the debt stays owed, the fanout is kept, and the status says no run was
    /// launched, so a settle does not wait on a run that will never start. A requested run
    /// holding the key keeps its own status.
    let launchImpactRun
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        (launchState: TestPruneState)
        (hasCachedResults: bool)
        (fanout: Set<string>)
        =
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

    /// Whether debt found while the key is held joins the run holding it instead of
    /// queueing another. Only a full-suite run can take it. Under pass-through it takes
    /// every arrival, since no run may follow it. Under impact selection it takes a
    /// BootScan cohort, which scanned the tree that run is testing; an in-session cohort
    /// is an edit the run never built, and queues its own run.
    let joinsFullRun (state: TestPruneState) (bootScan: bool) =
        (state.InFlight |> Option.exists (fun run -> run.Scope = LaunchedFullSuite))
        && (TestMode.skips RerunIntents state.Mode || bootScan)

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

    /// Attach what is owed to the full run in flight. The FIRST captured revision stands:
    /// a later attach must not advance a symbol past an edit the held run never built.
    let attachToFullRun (state: TestPruneState) =
        { state with
            DebtDuringFullRun =
                (state.DebtDuringFullRun, state.Debt.PendingQueue)
                ||> Set.fold (fun captured symbol ->
                    if Map.containsKey symbol captured then
                        captured
                    else
                        Map.add symbol (revisionOf state.Debt symbol) captured) }

    /// The run in flight has concluded: forget it, and end the pass-through it ran for
    /// when it executed at least one project to a verdict (`TestMode.afterRun`).
    let endRun (executedAny: bool) (state: TestPruneState) =
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

    /// Hold a build's launch for its tree's check results (`DiffWait`), and post its
    /// expiry. The mailbox is not blocked: the `BatchChecked` it waits for is delivered
    /// through it.
    let holdForDiff (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) =
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
    let prepareCommit (prior: TestPruneState) (candidate: TestPruneState) =
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

    { Name = PluginName.create FsHotWatch.PluginActivity.TestPrunePluginName
      Init = initialState
      Update =
        fun ctx state event ->
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
                | PluginEvent.FileChecked result -> return! onFileChecked ctx state modelGeneration result

                | PluginEvent.BatchChecked batch -> return! onBatchChecked ctx state modelGeneration batch

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
                    |> Option.iter (fun target ->
                        target.TrySetResult(JsonSerializer.Serialize {| error = reason |}) |> ignore)

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

                | Custom(RunTestsRequested(configs, filter, reply)) ->
                    return requestTestRun ctx state configs filter reply

                | Custom(IndexFoldProbe folded) ->
                    folded.TrySetResult() |> ignore
                    return state

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
        // Pure-content cache key, built by the lifted `cacheKeyFor` so the per-arm input
        // dependencies are structural rather than a convention. The thunks are this
        // closure's live state; `cacheKeyFor` decides which arm forces which — and
        // `FileChecked`, the per-file probe, forces none.
        let cacheKey (state: TestPruneState) (event: PluginEvent<TestPruneMsg>) : ContentHash option =
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

        Some cacheKey
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
