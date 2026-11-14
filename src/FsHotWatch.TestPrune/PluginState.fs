/// The plugin's state: the verification it owes, the run in flight, and what a run reads at launch.
module FsHotWatch.TestPrune.PluginState

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

/// What is still owed before a green is test-equivalent to the last full suite. It is
/// published with the run results it depends on, so a reader of one snapshot reads one
/// consistent answer, and a completion whose fold failed leaves it unchanged.
type VerificationDebt =
    {
        /// Changed symbols not yet verified by a covering run. Durable
        /// (`PendingVerification`).
        PendingQueue: PendingVerification.Queue
        /// The revision at which each queued symbol was last enqueued in this session. A
        /// symbol loaded from disk and never re-enqueued is at revision 0.
        SymbolRevisions: Map<string, int64>
        /// The last revision issued. Every enqueue takes the next one, so an edit to a
        /// symbol already queued is still a newer revision.
        Revision: int64
        /// Why what is owed is unknown, one sentence per cause naming the record and why it
        /// could not be trusted: a ledger that could not be read, a durable publication that
        /// did not finish, runtime coverage that did not ingest. While any is held, every run
        /// widens to the full suite and the cache is refused, until a full suite passes every
        /// configured project. Empty when the debt is known.
        UnknownDebt: string list
        /// The full-suite watermark impact-filtered greens are relative to.
        Baseline: FullSuiteBaseline.Baseline option
        /// File-granular runtime coverage obligations, per project generation. Durable.
        RuntimeObligations: RuntimeCoverageObligations
        /// What runtime coverage would have obligated for the projects whose
        /// `runtimeSelection` is shadow. Logged per launch, never selected, never owed: no
        /// verdict, skip or baseline reads it. In memory only.
        ShadowRuntime: RuntimeCoverageObligations
        /// Projects whose runtime coverage did not ingest, each with why. Each runs in full
        /// until a run of it passes (and so ingests). Durable (`CoverageNotIngested`).
        CoverageNotIngested: Map<string, string>
    }

    /// What is owed is unknown (`UnknownDebt`).
    member this.RecoveryOutstanding = not (List.isEmpty this.UnknownDebt)

/// What a launched run was going to execute, known when it claims the "tests" key.
type LaunchScope =
    /// Every configured project, unfiltered: requested, or widened to earn a baseline.
    | LaunchedFullSuite
    | LaunchedSelection

/// A full pass of this checkout's own, as a restarted daemon may diff from it
/// (AUTOMATION-1587): the tree T the impact index is sealed at, which the durable full-suite
/// ledger records this checkout passing in full (its tree or an earlier one it keeps,
/// AUTOMATION-1682), and a passing `test-baseline` record for exactly T. The cold scan's diff
/// against the index's rows is then the diff T to T'.
type CheckoutBaseline =
    {
        Tree: string
        /// The `test-baseline` record's key.
        Key: string
        /// The run that passed T in full.
        RunId: string
        /// T's manifest, the evidence a moved tree is compared with: a path that moved
        /// beside the symbols still forces the wide run (AUTOMATION-1559, ADR-039).
        Manifest: ReceiptTree
    }

/// The baseline a build's launch has in hand, which is what makes its diff worth waiting
/// for (`DiffWait`).
[<RequireQualifiedAccess>]
type WaitBasis =
    /// Neither a run completed this session nor a checkout baseline the tree moved from.
    | NoBaseline
    /// A run has completed this session.
    | SessionBaseline
    /// No run has completed this session, and the tree MOVED from this checkout's own last
    /// full pass (`CheckoutBaseline`). An unchanged tree is not one: the skip on records
    /// answers it without a diff (ADR-067), so waiting would only cost time.
    | CheckoutBaseline of baseline: CheckoutBaseline

module WaitBasis =
    /// The `reason=` token of a `diff-wait:` line.
    let describe (basis: WaitBasis) =
        match basis with
        | WaitBasis.NoBaseline -> "none"
        | WaitBasis.SessionBaseline -> "session-baseline"
        | WaitBasis.CheckoutBaseline b -> $"checkout-baseline tree=%s{b.Tree} record=%s{b.Key} run=%s{b.RunId}"

/// A launch `BuildSucceeded` held back because the symbol diff of the tree it built was
/// not folded yet (`DiffWait`), and the baseline that made it wait.
type AwaitedDiff =
    { Id: Guid
      Since: DateTime
      Basis: WaitBasis }

/// Holding a build's launch until the check results of the tree it built are folded.
///
/// A scan waits for the build before it type-checks, so `BuildSucceeded` reaches this
/// plugin before the `FileChecked` results for the files the build compiled. Launching
/// then selects from a diff that does not have them yet: with nothing owed, zero classes
/// are affected, the tree has moved since the last evidence, and the run widens to every
/// project in full, for an edit the diff would have narrowed to one.
///
/// A restarted daemon's first launch is the same race over its cold scan: it took its
/// inputs before the diff folded, and the edit ran only in a queued re-run behind a full
/// suite. When the tree MOVED from this checkout's own last full pass (`CheckoutBaseline`),
/// that launch waits too, and then selects from the diff T to T' as a warm session would
/// (AUTOMATION-1587). On expiry it launches what it would have launched without waiting:
/// the full suite, and a later diff queues a re-run.
module internal DiffWait =
    /// How long a held launch waits for the next `BatchChecked` before it launches
    /// without it. Longer than a warm scan of a large repository.
    let bound = TimeSpan.FromMinutes 5.0

    /// What a build's launch would run regardless of the symbol diff, and the trees
    /// that decide whether the diff is in hand.
    type Launch =
        {
            /// The baseline in hand: a run completed this session, or the checkout baseline
            /// the tree moved from. With neither, the launch is the baseline run.
            Basis: WaitBasis
            /// The host publishes a project model. Without one every seal is ignored, so
            /// none could release the wait.
            HasModel: bool
            /// No symbol, runtime obligation or unknown ledger is owed.
            OwesNothing: bool
            /// Projects the launch runs in full whatever the diff says: dependency
            /// fanout, and those with no whole-project run under the current model.
            RunsInFullAnyway: Set<string>
            /// Outstanding failures, or files and extensions the index could not analyse.
            RunsSomethingAnyway: bool
            /// The flush this build ran found changed symbols: the diff is in hand.
            FoldedChanges: bool
            Scope: LaunchScope
            /// The tree the last run's evidence is bound to.
            EvidenceTree: string option
            CurrentTree: string option
            /// The tree when the last `BatchChecked` folded its check results.
            SealedTree: string option
        }

    /// Whether a build's launch waits for its tree's check results. Only a launch the
    /// diff alone would select can gain from it; one that runs something regardless
    /// launches now, and so does one over the tree the last run's evidence is bound to,
    /// which the zero-affected skip already answers. A tree that cannot be read is never
    /// waited on.
    let awaits (launch: Launch) : bool =
        launch.Basis <> WaitBasis.NoBaseline
        && launch.HasModel
        && launch.OwesNothing
        && Set.isEmpty launch.RunsInFullAnyway
        && not launch.RunsSomethingAnyway
        && not launch.FoldedChanges
        && launch.Scope = LaunchedSelection
        && (match launch.CurrentTree with
            | Some current ->
                launch.SealedTree <> Some current
                && not (ReceiptInputTree.matches launch.EvidenceTree launch.CurrentTree)
            | None -> false)

    /// The one line saying when a build's launch took its inputs: `waitedMs` after the
    /// build (0 when it did not wait), on what baseline, and whether the bound ran out.
    /// Every line starts `diff-wait:`, so a land's log shows the added wait by grep.
    let describe (held: bool) (basis: WaitBasis) (waitedMs: int64) (timedOut: bool) =
        let heldText = if held then "yes" else "no"
        let timedOutText = if timedOut then "yes" else "no"

        $"diff-wait: held=%s{heldText} waited-ms=%d{waitedMs} reason=%s{WaitBasis.describe basis} timed-out=%s{timedOutText}"

    /// The line for a held launch that takes its inputs now.
    let released (awaited: AwaitedDiff) (timedOut: bool) =
        let waited = int64 (DateTime.UtcNow - awaited.Since).TotalMilliseconds
        describe true awaited.Basis waited timedOut

/// The run holding the "tests" key, as its launch here claimed it.
type InFlightRun =
    {
        Scope: LaunchScope
        /// The mode it was launched under. Its fold ends a pass-through it ran for.
        Mode: TestMode
        /// Repo-relative paths the watcher reported (as a `FileChecked`) while this run
        /// was in flight. A moved path in here was edited mid-run, not written by the run.
        WatcherReported: Set<string>
    }

/// A run that concluded red over the tree it launched on: what it launched against, so a
/// later launch can tell a retry of it from new work (AUTOMATION-1373).
type RedConclusion =
    {
        /// Each symbol the run launched against, at its debt revision then.
        SymbolRevisions: Map<string, int64>
        /// The input tree it launched and concluded over.
        Tree: string
    }

/// How far the boot scan has got, as the symbol index sees it. A run on a cold daemon can
/// finish while the scan is still checking files, and the analyses are written to the
/// index only when the boot scan's cohort flushes. Until then the index lacks most of the
/// tree, so a join against it would report those symbols as not indexed.
[<RequireQualifiedAccess>]
type ColdScan =
    /// No file has been checked yet: nothing suggests a scan, so nothing is waited for.
    | NotStarted
    /// Files are being checked; each probe here answers when the boot scan flushes.
    | InProgress of waiting: System.Threading.Tasks.TaskCompletionSource list
    /// The boot scan's cohort has been flushed to the index.
    | Flushed

module ColdScan =
    let onFileChecked (scan: ColdScan) : ColdScan =
        match scan with
        | ColdScan.NotStarted -> ColdScan.InProgress []
        | ColdScan.InProgress _
        | ColdScan.Flushed -> scan

    /// Answer `probe` now, or hold it until the boot scan flushes.
    let onProbe (probe: System.Threading.Tasks.TaskCompletionSource) (scan: ColdScan) : ColdScan =
        match scan with
        | ColdScan.InProgress waiting -> ColdScan.InProgress(probe :: waiting)
        | ColdScan.NotStarted
        | ColdScan.Flushed ->
            probe.TrySetResult() |> ignore
            scan

    let onBootScanFlushed (scan: ColdScan) : ColdScan =
        match scan with
        | ColdScan.InProgress waiting -> waiting |> List.iter (fun probe -> probe.TrySetResult() |> ignore)
        | ColdScan.NotStarted
        | ColdScan.Flushed -> ()

        ColdScan.Flushed

type TestPruneState =
    {
        /// How far the boot scan has got, for trace ingestion's wait on the index.
        ColdScan: ColdScan
        Debt: VerificationDebt
        /// Set by `set-scope`. Pass-through lasts for the run launched under it: that
        /// run's fold returns the daemon to impact selection once it executed a project
        /// to a verdict; a run that executed nothing, or a launch that could not start,
        /// leaves it (`TestMode.afterRun`). A request, not evidence; `test-scope` reports
        /// what actually ran.
        Mode: TestMode
        /// The run holding the "tests" key whose result is not folded yet, if a launch
        /// here claimed it. Cleared by that run's completion fold.
        InFlight: InFlightRun option
        /// The last completed run, when it executed and came back red over an unmoved
        /// tree; `None` after any other completion. An owed launch over the same tree with
        /// nothing owed that this run did not launch against would only retry it, so it is
        /// not made (`onlyRetriesRed`). In memory only: a restart may retry once.
        LastRed: RedConclusion option
        /// The receipt identity of the input tree when the last `BatchChecked` folded its
        /// check results, on every seal. `DiffWait` asks for it only once a session baseline
        /// exists; a `test-baseline` record asks whether it is the receipt tree.
        SealedTree: string option
        /// A build's launch held back for its tree's check results (`DiffWait`). Cleared
        /// by the next `BatchChecked`, by any launch, or by its expiry.
        AwaitingDiff: AwaitedDiff option
        /// The checkout baseline a held launch waits on (`WaitBasis.CheckoutBaseline`), until
        /// this session's first run completes. While held, the launch the fold releases
        /// selects as with a session baseline, with T's manifest as its evidence. Cleared on
        /// expiry (that launch is today's full suite) and by any completion.
        CheckoutBaseline: CheckoutBaseline option
        /// A launch this session's mode held back since a command last asked for one: the
        /// session launches only requested runs (`TestMode.launchesOwedRuns`), and a build
        /// left a run owed. `OwedRunRequested` launches it.
        OwedRunHeld: bool
        /// Every run this session completed, newest first, bounded at
        /// `SessionRunLedger`. `test-scope` declares them so a check can name each batch
        /// it ran.
        CompletedRuns: Guid list
        /// Each completion's check-vs-confirm projection, NEWEST FIRST and keyed by its run:
        /// the selection `check` would have used, and whether that reached a failure the run
        /// saw. Bounded at `SessionRunLedger`. Empty until a run completes.
        ///
        /// Keyed by run, not the newest alone: `confirm` grades the run its RECEIPT names,
        /// and a later completion that leaves the receipt standing — the zero-selection
        /// "already verified" skip its own scan provokes — replaced the graded run's
        /// projection with one for a run nothing grades, so every recall read "names run X,
        /// but the graded run is Y". `check-reach` is asked for the graded run by id.
        CheckReaches: (Guid * Map<string, ProjectSelection> option * CheckReach * FailureRecall) list
        /// Command replies this state owes, resolved only after the state is published
        /// (`PrepareCommit`'s `Finalize`). Each event starts with none.
        Replies: (Tasks.TaskCompletionSource<string> * string) list
        PendingAnalysis: Map<string, AnalysisResult list>
        /// The project-model generation `PendingAnalysis` was accepted under. Analysis
        /// from a replaced model is retired before any flush can persist it.
        AnalysisModelGeneration: int64 option
        /// What each file's completed analysis concluded, for the model in
        /// `AnalysisModelGeneration`. The cohort seal is minted from these.
        AnalysisFiles: Map<AbsFilePath, AnalysisFileEvidence>
        /// What the last sealed cohort earned: every checkable file of its model, and each
        /// one without a completed analysis as a refusal. `None` until a cohort seals.
        CohortSeal: AnalysisEvidence option
        /// The analysis-only evidence the last sealed cohort earned: the cohort seal, when
        /// no tests are configured. A daemon that runs tests publishes none; its test
        /// evidence refuses through the seal instead.
        AnalysisReceipt: AnalysisEvidence option
        SymbolSnapshot: Map<string, SymbolInfo list>
        AffectedTests: AffectedTestsState
        ChangedSymbols: string list
        ChangedFiles: string list
        /// Last completed test run's results, if any. The owner is the source of truth
        /// for the run's phase (`ctx.IsRunning "tests"` while its worker is live,
        /// `ctx.IsSettling "tests"` until its result fold commits), so this carries none.
        LastResults: TestResults option
        /// The seed symbols that SELECTED the last completed run — i.e. the change
        /// that caused those tests to run. Empty for an unfiltered run (nothing
        /// selected it; everything ran) and until a run completes.
        ///
        /// Retained because a later check that selects NOTHING has to be able to
        /// answer "then what was the last change that did trigger tests?". The
        /// seeds are computed per flush and were previously only logged, so that
        /// question had no answer outside a daemon log — and a reader who cannot
        /// tell "nothing needed running" from "nothing ran" goes looking for a bug
        /// in the selector.
        LastSeeds: string list
        /// The fanout reading of `LastSeeds`: which of them alone selected the most tests,
        /// or why that was not measured. Moves with `LastSeeds`.
        LastSeedFanout: SeedFanoutReading
        /// Symbols that became owed while a full-suite run was already in flight and were
        /// attached to it rather than queueing another run, each with the debt revision
        /// captured when it attached. Under `ImpactSelection` only a BootScan cohort
        /// attaches; under `PassThrough` every arrival does. The run covers the built tree
        /// being baselined, but these symbols are absent from its immutable launch
        /// snapshot. They may be committed only after that run produces genuinely green
        /// full-suite evidence over the input tree it launched against, and only while the
        /// symbol is still at its captured revision: a later edit is new debt the held run
        /// never built, even if the edit restores the sealed bytes.
        DebtDuringFullRun: Map<string, int64>
        /// The input tree, and the project model, over which the last completed run passed
        /// every runnable project in full (`coveredByConstruction`). A BootScan cohort that
        /// seals after that run's result has folded, over the same tree and model, and
        /// whose files are all entries of it, describes what the run already tested: its
        /// debt is discharged without a second run. The next completed run replaces it.
        CoveredFullSuite: (ReceiptTree * int64 option) option
        /// Maps test class name → absolute source file path (built during FileChecked analysis).
        TestClassFiles: Map<string, string>
        /// True after the plugin has observed at least one `BuildCompleted
        /// BuildSucceeded` in this daemon session. The `FileChecked` handler uses this
        /// to decide whether a clean FCS check may promote the freshness sidecar to
        /// `fcsClean = true`.
        ///
        /// The cold-scan pipeline guarantees BuildCompleted reaches the TestPrune
        /// mailbox before any FileChecked (Daemon.fs `performScan` awaits BuildPlugin
        /// terminal before the FCS tier), so the gate is effective on the very first
        /// cold start — no two-session warm-up. Resets on plugin restart by design: a
        /// restart clears the in-process "I've seen warm FCS this session" assertion.
        BuildCompletedInThisSession: bool
        /// Per-test-project dependency fingerprint observed at the last
        /// `BuildCompleted`. A project whose fingerprint moves between builds had
        /// a dependency/binary change the symbol diff can't see, so its tests are
        /// force-run (dependency-fanout). Empty until the first build establishes
        /// the baseline. See `DependencyFanout`.
        PriorProjectFingerprints: Map<string, string>
        /// Test projects whose dependency fingerprint changed but whose force-run
        /// has not launched: a run was already in flight when the build landed, or the
        /// launch found its artifacts or test host unavailable. The next launch consumes
        /// (and clears) this, so a dependency change is never lost.
        PendingForceRunProjects: Set<string>
        /// True when the most recent `flushAndQueryAffected` had changed/queued symbols
        /// but EVERY one proved to have NO covering test, leaving an empty affected set.
        /// A definitive "nothing to verify" green, so the zero-affected skip in
        /// `runTestsWithImpact` completes immediately even on a cold daemon with no
        /// session baseline. Distinct from a genuine cold start with NO pending symbols,
        /// which must still run the full-suite baseline to establish one (guarded by
        /// `hasCachedResults`). Recomputed on every flush; only read right after one.
        ChangedSymbolsAllUncovered: UncoveredChanges
        /// Repo-relative paths of files whose symbol analysis FAILED and has not
        /// since succeeded. These files contribute NO symbols, so they are invisible
        /// to the impact graph — an edit to one has nothing to diff and selects
        /// nothing on its own. While this set is non-empty the run
        /// falls back to the coarse selection (`coarseFallbackProjects`: every test
        /// project, in full), because "I cannot analyse this file" means the SELECTOR
        /// cannot know what to select, and a superset is safe where a gap is not.
        ///
        /// A file leaves the map as soon as it analyses cleanly, so the fallback is
        /// self-clearing. NOT persisted: a cold scan re-checks every file and
        /// repopulates the map from scratch.
        UnanalyzableFiles: Map<string, UnanalyzableFile>
        /// Repo-relative paths of changed files whose changes selected NO tests, and why
        /// (`FileFreshness.FileUnverified`). Unlike `UnanalyzableFiles` these do not widen
        /// the run: the file has symbols, but its latest ones could not be diffed. A file
        /// leaves the map when a later check of it is diffed. `test-scope` reports the map
        /// as `notSelected`. NOT persisted, for the same reason as `UnanalyzableFiles`.
        NotSelected: Map<string, NotSelectedReason>
        /// The identity (`IndexedAnalysis.identityOf`) of each analysis in
        /// `PendingAnalysis`, by repo-relative path. The flush that writes them records
        /// them as what the index holds; a retirement drops them with the analyses.
        PendingIdentities: Map<string, string>
        /// Whether a `FileChecked` task-cache entry may stand in for analysing a check now
        /// (`IndexedAnalysis.mayReplay` over the index and `PendingIdentities`). The entry is
        /// written whenever a file is analysed; this decides only whether it may be READ.
        IndexGuard: IndexedAnalysis.Guard
        /// Files whose analysis a project-model change retired before any flush wrote
        /// it. The index lacks them, so it cannot vouch for what covers anything while
        /// one remains: the run falls back as for `UnanalyzableFiles`, and no change
        /// may read as covered by nothing. A file leaves when its analysis is flushed.
        Unindexed: Set<string>
        /// Extensions whose last refresh THREW, by name, with the reason. Their stored
        /// edges describe an older tree, so while this is non-empty the run takes the same
        /// coarse fallback as `UnanalyzableFiles`, and the ledger carries an error for each.
        /// An extension leaves the map when a refresh succeeds; a failed one is retried on
        /// every flush. NOT persisted: a new process refreshes every extension on its
        /// first flush.
        FailedExtensions: Map<string, string>
        /// The reds no COVERING run has passed since. Rewritten on
        /// every `TestsFinished`: a red leaves ONLY when a run that actually executed
        /// it passes. The shared error ledger is a projection of this list.
        ///
        /// DURABLE: persisted beside the pending-verification queue
        /// and loaded at construction, so a red survives a daemon restart and is
        /// quarantined into the next run exactly as it would have been in the session
        /// that found it. The earlier "a restart runs the full suite anyway" argument
        /// was false whenever the durable queue was non-empty at restart — see
        /// `OutstandingFailure.load`.
        OutstandingFailures: OutstandingFailure list
        /// What the last completed run actually COVERED — the receipt that goes with
        /// `LastResults` (which says what it FOUND). Read together: a green result means
        /// "nothing failed IN WHAT THIS COVERED", never "nothing failed".
        ///
        /// Kept in state rather than only in the `TestsFinished` closure so consumers
        /// outside the handler (IPC commands, the verdict writer) ask this instead of
        /// inventing a parallel notion of scope that could drift from the one the ledger
        /// clears by. Empty until the first run completes.
        LastCoverage: RunCoverage
        LastZeroSelection: ZeroSelection
        /// Atomic receipt exposed by `test-scope`. A queued narrower drain may update
        /// status and failure state, but cannot split or downgrade a full-suite receipt
        /// earned earlier in the same top-level verification episode.
        Receipt: HeldReceipt
        /// What the last completion EARNED for the current project model: the run, what it
        /// covered in full, and every reason it cannot support a green. Minted only by the
        /// completion fold, and published with this state (`IEarnedEvidenceState`).
        Earned: EarnedEvidence option
        /// Whole-project coverage an earlier daemon recorded for this checkout
        /// (`WholeProjectEvidence`), restored at start. It vouches for a project only while
        /// that project's compile inputs are the ones recorded, and only where nothing this
        /// daemon earned speaks: it closes the evidence gap a restart used to open, and is
        /// never published as this daemon's evidence.
        RestoredEvidence: EarnedEvidence option
    }

    /// A baseline a launch may select against: a run completed this session, or the
    /// checkout baseline a held launch waited on.
    member this.HasBaselineInHand = this.LastResults.IsSome || this.CheckoutBaseline.IsSome

    /// The standing receipt, if one stands.
    member this.EvidenceReceipt =
        match this.Receipt with
        | HeldReceipt.Standing(receipt, _) -> Some receipt
        | HeldReceipt.NoneYet
        | HeldReceipt.Revoked _ -> None

    /// Why no receipt stands, when one was taken away. `test-scope` says it when it has
    /// no evidence to serve.
    member this.ReceiptRevoked =
        match this.Receipt with
        | HeldReceipt.Revoked reason -> Some reason
        | HeldReceipt.NoneYet
        | HeldReceipt.Standing _ -> None

    interface IEarnedEvidenceState with
        member this.EarnedEvidence =
            this.Earned |> Option.map (AnalysisEvidence.refuseThrough this.CohortSeal)

    interface IAnalysisEvidenceState with
        member this.AnalysisEvidence = this.AnalysisReceipt

    // ADR-059 §17: an analysis is cached when it runs, so a restart finds it; it replays only
    // once the index holds it.
    interface IFileCacheReadGuard with
        member this.MayReadFileCache result =
            this.IndexGuard.MayReplay(this.PendingIdentities, result)

    // AUTOMATION-1482: a replayed check whose analysis is filed is answered from the record.
    interface IReplayedCheckReader with
        member this.CanAnswer result = this.IndexGuard.AnswersReplay result

    // A replayed `FileChecked` is recorded by the rule a live fold applies: only a result
    // published against the model published now describes it (see `notCurrent`).
    //
    // A replayed analysis that completed and left no findings for the file is the file
    // analysing cleanly, so it leaves `UnanalyzableFiles` as a live clean fold makes it
    // leave (AUTOMATION-1469). Keeping the entry made every later run re-report a parse
    // error the file no longer has. A failed replay, or one that left findings, keeps it.
    interface IFileReplayState<TestPruneState> with
        member this.Replayed suppressedCodes currentModel result analysis =
            if result.ModelGeneration.IsNone || result.ModelGeneration <> currentModel then
                this
            else
                let file = AbsFilePath.value result.File

                let unanalysable =
                    match analysis with
                    | Ok [] -> this.UnanalyzableFiles |> Map.filter (fun _ entry -> entry.File <> file)
                    | Ok _
                    | Error _ -> this.UnanalyzableFiles

                { this with
                    AnalysisFiles =
                        Map.add
                            result.File
                            (AnalysisFileEvidence.fromResult suppressedCodes result (Result.map ignore analysis))
                            this.AnalysisFiles
                    UnanalyzableFiles = unanalysable }

module TestPruneState =
    /// The evidence that decides which projects still need a whole-project run: what this
    /// daemon earned, or — until it has earned any — what an earlier one recorded. Only
    /// coverage is read from it; the evidence a verdict grades is always `Earned`.
    let coverageEvidence (state: TestPruneState) : EarnedEvidence option =
        state.Earned |> Option.orElse state.RestoredEvidence

/// The slice of `TestPruneState` a test RUN reads — and nothing else.
///
/// The run is an `Async` handed to `RunExclusive` and lives as long as the suite does:
/// minutes, on a full run. Whatever it closes over it PINS for that whole time, so
/// closing over the state RECORD pins the entire generation — including `SymbolSnapshot`
/// (the repo-wide symbol table), which no run touches — while the agent loop keeps
/// folding new `FileChecked` events into fresh generations. The peak lands exactly when
/// the suite is running and FCS is at its own peak, and FsHotWatch is ~85% native FCS
/// memory. Copying only what the run reads lets the rest of each generation die on
/// schedule; the type is the enforcement.
///
/// `LastResults` is deliberately absent: the run's interest in it is one bit — "does a
/// baseline exist" — which the caller computes and passes as `hasCachedResults`.
type TestRunInputs =
    {
        /// The impact selection: which test classes the changed symbols reach.
        AffectedTests: AffectedTestsState
        /// The in-memory hot view of the pending-verification queue, unioned with the
        /// durable queue to form the snapshot this run is launched against.
        ChangedSymbols: string list
        /// Every changed symbol proved to have no covering test — the "nothing to
        /// verify" green.
        ChangedSymbolsAllUncovered: UncoveredChanges
        /// Files whose symbol analysis failed: while non-empty, the run widens to
        /// every test project.
        UnanalyzableFiles: Map<string, UnanalyzableFile>
        /// Files whose analysis the index lacks (`TestPruneState.Unindexed`): while
        /// non-empty, the run widens to every test project.
        Unindexed: Set<string>
        /// Extensions whose last refresh failed: while non-empty, the run widens to every
        /// test project.
        FailedExtensions: Map<string, string>
        /// Prior reds captured at launch. They are quarantined into this run's
        /// selection even when the current graph reaches different tests.
        OutstandingFailures: OutstandingFailure list
        /// The debt the run launches against, captured at dispatch.
        Debt: VerificationDebt
        /// The mode in effect at dispatch.
        Mode: TestMode
        /// Source files whose symbols changed in this launch snapshot. Runtime
        /// project attribution is file-granular, so it consumes this alongside
        /// the symbol-precise AST selection.
        ChangedFiles: string list
        /// Seed receipt captured at dispatch. A later BatchChecked may replace the
        /// live state's LastSeeds while this run is still executing.
        Seeds: string list
        /// The seeds' fanout reading, captured with `Seeds`.
        SeedFanout: SeedFanoutReading
        /// The evidence earned so far, or — before this daemon has earned any — what an
        /// earlier one recorded (`TestPruneState.RestoredEvidence`). Projects it does not
        /// cover whole under the current model run in full, so this run's own evidence can
        /// support a green.
        Earned: EarnedEvidence option
        /// The input tree the held test-evidence receipt is bound to; `None` when there
        /// is none. A launch over any other tree has nothing it may skip on.
        EvidenceTree: string option
        /// The entries of `EvidenceTree`, when they are held.
        EvidenceManifest: ReceiptTree option
        /// The tree the last `BatchChecked` folded its check results over: the symbol
        /// diff has run over this tree.
        SealedTree: string option
        /// The launch selects against a checkout baseline a held launch waited on, with no
        /// run of this session behind it: the daemon holds no evidence of its own yet
        /// (AUTOMATION-1587).
        OnCheckoutBaselineOnly: bool
    }

module TestRunInputs =
    /// Project the state down to what a run reads, at LAUNCH time.
    let ofState (state: TestPruneState) : TestRunInputs =
        { AffectedTests = state.AffectedTests
          ChangedSymbols = state.ChangedSymbols
          ChangedSymbolsAllUncovered = state.ChangedSymbolsAllUncovered
          UnanalyzableFiles = state.UnanalyzableFiles
          Unindexed = state.Unindexed
          FailedExtensions = state.FailedExtensions
          OutstandingFailures = state.OutstandingFailures
          Debt = state.Debt
          Mode = state.Mode
          ChangedFiles = state.ChangedFiles
          Seeds = state.LastSeeds
          SeedFanout = state.LastSeedFanout
          Earned = TestPruneState.coverageEvidence state
          // With no receipt standing, a held checkout baseline's tree and manifest are the
          // evidence the tree moved from (AUTOMATION-1587).
          EvidenceTree =
            state.EvidenceReceipt
            |> Option.bind (fun receipt -> receipt.InputTreeHash)
            |> Option.orElse (state.CheckoutBaseline |> Option.map _.Tree)
          EvidenceManifest =
            match state.Receipt with
            | HeldReceipt.Standing(receipt, manifest) ->
                manifest
                |> Option.filter (fun manifest ->
                    ReceiptInputTree.matches receipt.InputTreeHash (Some manifest.Identity))
            | HeldReceipt.NoneYet
            | HeldReceipt.Revoked _ -> state.CheckoutBaseline |> Option.map _.Manifest
          SealedTree = state.SealedTree
          OnCheckoutBaselineOnly = state.LastResults.IsNone && state.CheckoutBaseline.IsSome }
