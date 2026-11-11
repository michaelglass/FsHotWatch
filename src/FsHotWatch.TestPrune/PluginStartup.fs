/// What a plugin starts from: its restored sidecars, its starting debt and state, and its freshness cell.
module FsHotWatch.TestPrune.PluginStartup

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
open FsHotWatch.TestPrune.PluginRuns
open FsHotWatch.TestPrune.PluginFold
open FsHotWatch.TestPrune.PluginSteps

/// What a plugin restores from its sidecars at start (`restorePersisted`), each with the
/// reason it could not be read, when it could not.
type internal RestoredState =
    { LoadedQueue: PendingVerification.LoadedQueue
      LedgerUnreadableReason: string option
      DebtPublicationPath: string
      InterruptedPublication: bool
      LoadedFailures: OutstandingFailure list
      FailuresUnreadableReason: string option
      LoadedBaseline: FullSuiteBaseline.Baseline option
      RestoredEvidence: EarnedEvidence option
      LoadedRuntimeObligations: RuntimeCoverageObligations
      RuntimeObligationsUnreadableReason: string option
      LoadedNotIngested: CoverageNotIngested.Record
      NotIngestedUnreadableReason: string option }

/// Restore the plugin's sidecars: the pending queue, the publication marker, outstanding
/// failures, the full-suite baseline, whole-project evidence, runtime obligations and
/// coverage that was not ingested.
let internal restorePersisted (repoRoot: string) : RestoredState =
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
    /// relative to. `None` until a full-suite run has passed every configured
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

    { LoadedQueue = loadedQueue
      LedgerUnreadableReason = ledgerUnreadableReason
      DebtPublicationPath = debtPublicationPath
      InterruptedPublication = interruptedPublication
      LoadedFailures = loadedFailures
      FailuresUnreadableReason = failuresUnreadableReason
      LoadedBaseline = loadedBaseline
      RestoredEvidence = restoredEvidence
      LoadedRuntimeObligations = loadedRuntimeObligations
      RuntimeObligationsUnreadableReason = runtimeObligationsUnreadableReason
      LoadedNotIngested = loadedNotIngested
      NotIngestedUnreadableReason = notIngestedUnreadableReason }

/// The verification debt a plugin starts with, from its restored sidecars.
let internal debtFromRestored
    (repoRoot: string)
    (allowedRuntimeProjects: Set<string>)
    (restored: RestoredState)
    : VerificationDebt =
    let loadedQueue = restored.LoadedQueue
    let ledgerUnreadableReason = restored.LedgerUnreadableReason
    let debtPublicationPath = restored.DebtPublicationPath
    let interruptedPublication = restored.InterruptedPublication
    let failuresUnreadableReason = restored.FailuresUnreadableReason
    let loadedBaseline = restored.LoadedBaseline
    let loadedRuntimeObligations = restored.LoadedRuntimeObligations
    let runtimeObligationsUnreadableReason = restored.RuntimeObligationsUnreadableReason
    let loadedNotIngested = restored.LoadedNotIngested
    let notIngestedUnreadableReason = restored.NotIngestedUnreadableReason

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

/// Seed the in-memory hot view from the durable queue so a restart with a non-empty
/// queue re-flags those symbols. Without this, a restart diffs current symbols against
/// the already-advanced analysis snapshot → "nothing changed" → zero tests run → false
/// green.
let internal initialStateOf
    (initialMode: TestMode)
    (mayReplay: Map<string, string> -> FileCheckResult -> bool)
    (loadedDebt: VerificationDebt)
    (restored: RestoredState)
    : TestPruneState =
    let loadedFailures = restored.LoadedFailures
    let restoredEvidence = restored.RestoredEvidence

    { ColdScan = ColdScan.NotStarted
      Debt = loadedDebt
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

/// The plugin's file-freshness view (`freshnessCellIn`): one cell, read and written only
/// through these functions.
type internal FreshnessCell =
    { Read: unit -> FileFreshness.Store
      Write: FileFreshness.Store -> unit
      Reconcile: int64 option -> unit }

/// Per-file FCS freshness sidecar, loaded once at plugin construction from
/// `.fshw/test-prune/file-freshness.json` and updated incrementally on each
/// FileChecked. Survives daemon restarts so a cross-restart replay can decide which
/// files' stored symbols are trustworthy enough to run detectChanges against. Held with
/// the project model generation it was last reconciled against, as one value.
///
/// Closure-local mutable cell + Volatile: only the Update handler reads and writes it.
let internal freshnessCellIn (repoRoot: string) : FreshnessCell =
    let loaded = FileFreshness.load repoRoot

    if loaded.ContainsKey FileFreshness.UnreadableKey then
        Logging.warn
            "test-prune"
            $"%s{FileFreshness.sidecarPath repoRoot} could not be read in full; every file without a record of its own is treated as FCS-dirty, so its next clean check widens its selection instead of diffing"

    let mutable freshness: FreshnessView = { Store = loaded; ReconciledAt = None }

    /// The sidecar as it stands.
    let freshnessStore () = (Volatile.Read(&freshness)).Store


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

    { Read = freshnessStore
      Write = updateFreshness
      Reconcile = reconcileFreshness }
