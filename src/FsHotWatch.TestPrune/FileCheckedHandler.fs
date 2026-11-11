/// The FileChecked handler: one file's analysis, freshness stamp and changed symbols, over a FileCheckedEnv.
module FsHotWatch.TestPrune.FileCheckedHandler

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

/// What `onFileCheckedWith` reads from the plugin. The freshness view stays a plugin cell:
/// `FreshnessStore` reads it and `UpdateFreshness` writes it when the handler calls them.
type internal FileCheckedEnv =
    {
        RepoRoot: string
        Db: Database
        PriorRows: FileFreshness.PriorRowLedger
        FreshnessStore: unit -> FileFreshness.Store
        UpdateFreshness: FileFreshness.Store -> unit
        EnqueuePending: string list -> VerificationDebt -> VerificationDebt
        /// Where analyses are filed and read (AUTOMATION-1482); None files and reads none.
        AnalysisRecords: FsHotWatch.OutcomeStore.Store option
    }

/// The file one `FileChecked` event is about, as its handler reads it.
type internal CheckedFile =
    { Ctx: PluginCtx<TestPruneMsg>
      State: TestPruneState
      Result: FileCheckResult
      FileStr: string
      RelPath: string
      AnalysisStarted: DateTime }

/// The ONE treatment for a file whose symbol analysis
/// failed (an `analyzeSourceFromResults` Error and a handler fault are the same
/// condition). The file is REMEMBERED as unanalysable, with three
/// consequences, none silent:
///   1. a WARNING lands in the error ledger, keyed to the file, so
///      `fshw check` prints it and — under the default warn-fail
///      policy — refuses a green verdict;
///   2. `runTestsWithImpact` falls back to EVERY test project in full
///      while the set is non-empty;
///   3. the non-empty force-run set disables the zero-affected skip
///      gate, so this can never end as "0 affected — green, 0 ran".
/// The file leaves the set the moment it analyses cleanly.
///
/// The Failed stamp needs no idle guard: the framework's ReportStatus
/// funnel drops any terminal stamped while an exclusive run is in
/// flight (the run owns the status), so a mid-run analysis failure
/// cannot manufacture a terminal. The ledger entry and the
/// force-full-suite consequence persist either way.
let internal markUnanalysableIn
    (env: FileCheckedEnv)
    (file: CheckedFile)
    (reason: string)
    (detail: string)
    (logDetail: string)
    : TestPruneState =
    let repoRoot = env.RepoRoot
    let freshnessStore = env.FreshnessStore
    let updateFreshness = env.UpdateFreshness
    let ctx = file.Ctx
    let state = file.State
    let result = file.Result
    let fileStr = file.FileStr
    let relPath = file.RelPath
    let analysisStarted = file.AnalysisStarted

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
            FileFreshness.stamp FileFreshness.Gone FileFreshness.FcsErrors DateTime.UtcNow relPath (freshnessStore ())
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

/// Fold one file's successful symbol analysis into the state: persist its symbols, diff
/// them against what is stored, enqueue what changed, stamp its freshness, and report the
/// analysis when no run is due.
let internal foldAnalysedFile
    (env: FileCheckedEnv)
    (file: CheckedFile)
    (modelGeneration: int64 option)
    (projectName: string)
    (analysisRoot: string)
    (currentClean: bool)
    storedFreshness
    (analysisResult: AnalysisResult)
    : TestPruneState =
    let repoRoot = env.RepoRoot
    let db = env.Db
    let priorRows = env.PriorRows
    let freshnessStore = env.FreshnessStore
    let updateFreshness = env.UpdateFreshness
    let enqueuePending = env.EnqueuePending
    let ctx = file.Ctx
    let state = file.State
    let result = file.Result
    let fileStr = file.FileStr
    let relPath = file.RelPath
    let analysisStarted = file.AnalysisStarted

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
    // (see `BuildCompletedInThisSession`) AND a clean FCS result; dirty
    // when FCS reported errors, since this check's rows are flushed
    // and are not a baseline; otherwise `markUnverified`.
    let now = DateTime.UtcNow

    // A check of a path that has since vanished
    // forgets the record rather than re-stamping it — see
    // `FileFreshness.stamp`.
    let updatedFreshness =
        FileFreshness.stamp
            (FileFreshness.resolvePresence repoRoot relPath)
            (if not currentClean then
                 FileFreshness.FcsErrors
             elif state.BuildCompletedInThisSession then
                 FileFreshness.VerifiedClean
             else
                 FileFreshness.CleanBeforeBuild)
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


    newState

/// One file's check result under the current model: its analysis, freshness and changed symbols.
let internal onFileCheckedWith
    (env: FileCheckedEnv)
    (ctx: PluginCtx<TestPruneMsg>)
    (state: TestPruneState)
    (modelGeneration: int64 option)
    (result: FileCheckResult)
    =
    let repoRoot = env.RepoRoot
    let freshnessStore = env.FreshnessStore

    async {
        let analysisStarted = DateTime.UtcNow
        let fileStr = AbsFilePath.value result.File
        let relPath = Path.GetRelativePath(repoRoot, fileStr).Replace('\\', '/')

        let checkedFile: CheckedFile =
            { Ctx = ctx
              State = state
              Result = result
              FileStr = fileStr
              RelPath = relPath
              AnalysisStarted = analysisStarted }

        let markUnanalysable = markUnanalysableIn env checkedFile


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
                | Replayed _
                | FullCheck _ -> AnalysisRecords.projectNameOf result.ProjectOptions.ProjectFileName

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
                    let analysed =
                        analyzeSourceFromResults analysisFile result.Source parseResults checkResults projectName

                    // Filed, so another checkout's replay of the same check is answered
                    // without a live one (AUTOMATION-1482).
                    match analysed, env.AnalysisRecords, AnalysisRecords.keyFor repoRoot result with
                    | Ok analysis, Some store, Some key ->
                        AnalysisRecords.file store analysisRoot relPath key analysis |> ignore
                    | _ -> ()

                    analysed
                | Replayed _ ->
                    // Only a state that holds this analysis's record is handed a replay
                    // (`IReplayedCheckReader`); one that moved since is a fault, as before.
                    let filed =
                        env.AnalysisRecords
                        |> Option.bind (fun store ->
                            AnalysisRecords.keyFor repoRoot result
                            |> Option.bind (AnalysisRecords.lookup store analysisRoot))

                    match filed with
                    | Some analysis -> Ok analysis
                    | None -> raise (ReplayedResultException("test-prune symbol analysis", fileStr))
                | ParseOnly _ ->
                    Error "FCS produced parse-only results; full type-check results are required for impact analysis"

            match analysisResult with
            | Ok analysisResult ->
                return
                    foldAnalysedFile
                        env
                        checkedFile
                        modelGeneration
                        projectName
                        analysisRoot
                        currentClean
                        storedFreshness
                        analysisResult
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
