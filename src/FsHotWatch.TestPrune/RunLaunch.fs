/// What a launched run carries to its completion, how far its check reached, and the plugin's messages.
module FsHotWatch.TestPrune.RunLaunch

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

/// Custom message posted from the async test runner back to the synchronous Custom
/// handler. Carries the completed lifecycle event so the handler can emit it inside the
/// framework's per-event capture window — required for the cache to record it on terminal
/// status, which `tryReplayCache` re-fires to downstream subscribers (FileCommandPlugin
/// keys off TestRunCompleted) on a hit. The start is emitted before the host launches;
/// cache replay synthesizes a matching start for completion-only entries.
///
/// Live `TestProgress` events still fire from the async because cache replay
/// deliberately skips per-group progress and goes straight from Started to Completed.
///
/// `launch` is what the run was LAUNCHED against, captured at dispatch: the queue
/// snapshot (`Symbols`) and, per symbol, the test PROJECTS covering it
/// (`CoveringProjectsBySymbol`). The `TestsFinished` handler decides per-symbol
/// green-commit from THIS, not from live `state.AffectedTests`/`state.ChangedSymbols`,
/// which mid-run `BatchChecked` flushes overwrite. A symbol leaves the queue only when
/// EVERY project covering it passed; a symbol with no covering projects is committed
/// unconditionally at flush time.
///
/// `Selection` is the run's SCOPE and the input to `RunCoverage.ofRun`,
/// so it decides what this run's green may CLEAR. A project absent from it was never
/// launched and vindicates nothing, whatever its result says — an impact-skip is
/// recorded as a filtered PASS. Empty for the zero-affected skip and the aborted-run
/// lifecycle: they executed nothing, so they clear nothing.
type TestRunLaunch =
    {
        /// Immutable input tree captured before this run executes, entries and all: what
        /// moved by completion is named from these, never looked up.
        InputTree: ReceiptTree option
        /// The available project-model generation this run was selected under. A
        /// completion observed under a different model keeps the results of the projects
        /// whose compile inputs it did not change (`ProjectInputs`) and discharges nothing
        /// through the others.
        ModelGeneration: int64 option
        /// Each configured test project's `.fsproj` and compile-input identity under the
        /// model it launched under (`ProjectModel.ProjectInputs`). `None` when the host
        /// published no identities; a project missing from it counts as changed.
        ProjectInputs: Map<string, string * string> option
        Symbols: Set<string>
        /// The revision of each launched symbol at dispatch. A completion retires a
        /// symbol only while it is still at this revision: an edit made during the run
        /// is newer debt the run never built.
        SymbolRevisions: Map<string, int64>
        CoveringProjectsBySymbol: Map<string, Set<string>>
        /// Durable file-granular runtime obligations launched independently of
        /// the symbol queue. Each project runs unfiltered; only those exact file
        /// obligations may be retired by its green result.
        RuntimeProjectsByFile: RuntimeCoverageObligations
        Selection: Map<string, ProjectSelection>
        /// The selection this run would have been launched against had
        /// `confirm` not widened the scope to full — i.e. what `check` would have run over
        /// the same tree, same daemon, same instant.
        ///
        /// `Some` ONLY where an impact selection was actually computed and then widened
        /// past. `None` — a `run-tests` force run, an aborted lifecycle, the zero-affected
        /// skip — means THERE IS NO SELECTION TO PROJECT THROUGH, and the projection must
        /// refuse rather than fall back to `Selection` (which under full-suite scope is
        /// every project in full, and would read as "check would have run everything": a
        /// perfect, permanent, false agreement).
        WouldHaveRun: Map<string, ProjectSelection> option
        /// The trace selection computed beside this run's static one (`TraceShadow.atLaunch`),
        /// stored at completion. Empty unless `tests.traces.select` is shadow. It selects nothing.
        TraceShadow: ShadowReading list
        /// The seeds that selected this run, captured atomically with its scope.
        Seeds: string list
        ZeroSelection: ZeroSelection
        /// Why this run covers every project, should it do so (`fullSuiteCauseOf`).
        /// Decided at launch, where the widenings are known; read only when the run's
        /// coverage turns out to be the whole suite.
        FullSuiteCause: FullSuiteCause
        /// Every selected project and each cause that put it in the run, widest first
        /// (`SelectionAttribution.attribute`). Empty for a launch that selected nothing.
        SelectedBy: ProjectAttribution list
        /// The seeds that alone selected the most tests (`SeedFanoutReading.ofSelection`),
        /// captured with `Seeds`; `NoSelection` for a launch no impact selection made.
        SeedFanout: SeedFanoutReading
        /// The changed files this run launched against. Its completion clears only
        /// these; a file that changed during the run still selects the next one.
        ChangedFiles: string list
        /// When the test host started: after `beforeRun`, so a write stamped at or after
        /// it was made while the tests ran. `None` until the run reaches the host.
        TestHostStartedAt: DateTime option
        /// The projects this run did not launch because the test-evidence store held a
        /// green another run earned under byte-identical inputs, and where each came from.
        /// Only reuses whose key still held at completion are here. A run that reused
        /// anything never earns a full-suite baseline.
        Reused: Map<string, TestEvidenceRun.Provenance>
    }

    /// The launch tree's identity: what a receipt binds to.
    member this.InputTreeHash = this.InputTree |> Option.map _.Identity

module CheckReach =
    let private recallThreshold = 1.0

    /// The wire token. Total.
    let token (r: CheckReach) : string =
        match r with
        | ReachedAFailure _ -> "reached-a-failure"
        | ReachedNoFailure _ -> "reached-no-failure"
        | NoFailuresToReach -> "no-failures-to-reach"
        | ReachUnknown _ -> "unknown"

    /// Does the retained selection reach `failure`?
    ///
    /// `None` where it cannot be decided. A project ABSENT from the selection is decided
    /// — `check` would not have launched it at all — but a project selected under a
    /// CLASS filter cannot be asked about a failure that names no class.
    type private Reach =
        | Reached
        | Missed of MissCause
        | Undecidable

    let private reaches (selection: Map<string, ProjectSelection>) (failure: OutstandingFailure) : Reach =
        match Map.tryFind failure.Project selection with
        | None -> Missed MissCause.ProjectNotSelected
        | Some ProjectInFull -> Reached
        | Some(ProjectExcept skipped) ->
            match failure.Class with
            | Some cls when Set.contains cls skipped -> Missed MissCause.ClassNotInFilter
            | Some _ -> Reached
            | None -> Undecidable
        | Some(ProjectClasses classes) ->
            match failure.Class with
            | Some cls when Set.contains cls classes -> Reached
            | Some _ -> Missed MissCause.ClassNotInFilter
            | None -> Undecidable

    /// Measure the retained selection against every distinct failure the full run
    /// observed. Zero failures is no sample, not perfect recall. One undecidable
    /// project-level failure makes the denominator unknown, so the whole measurement
    /// is refused rather than silently shrinking the denominator.
    let measure
        (wouldHaveRun: Map<string, ProjectSelection> option)
        (failures: OutstandingFailure list)
        : FailureRecall =
        match wouldHaveRun with
        | None -> RecallNotMeasurable "this run carries no retained impact selection"
        | Some _ when List.isEmpty failures ->
            RecallNotMeasurable "the full run observed no failures, so the recall denominator is zero"
        | Some selection ->
            let distinctFailures =
                failures
                |> List.distinctBy (fun failure -> failure.Project, failure.Class, failure.Method)

            match
                distinctFailures
                |> List.tryFind (fun failure -> Option.isNone failure.Class || Option.isNone failure.Method)
            with
            | Some failure ->
                RecallNotMeasurable
                    $"the %s{failure.Project} failure names no exact test class and method, so an exact failing-test denominator cannot be formed"
            | None ->
                let decisions =
                    distinctFailures |> List.map (fun failure -> failure, reaches selection failure)

                match decisions |> List.tryFind (fun (_, decision) -> decision = Undecidable) with
                | Some(failure, _) ->
                    RecallNotMeasurable
                        $"the %s{failure.Project} failure cannot be projected through the retained selection"
                | None ->
                    let total = List.length decisions

                    let reached =
                        decisions
                        |> List.sumBy (fun (_, decision) -> if decision = Reached then 1 else 0)

                    let recall = float reached / float total
                    RecallMeasured(reached, total, recallThreshold, recall >= recallThreshold)

    /// Classify one completed run against the selection `check` would have used.
    ///
    /// `failures` is `failuresOf` — the very list the outstanding ledger is rewritten
    /// from, so the reds this asks about are exactly the reds the verdict reports.
    let classify (wouldHaveRun: Map<string, ProjectSelection> option) (failures: OutstandingFailure list) : CheckReach =
        match wouldHaveRun with
        | None ->
            ReachUnknown
                "this run carries no retained impact selection (a forced re-run, an aborted run, or a skip), so \
                 there is nothing to project the result through"
        | Some _ when List.isEmpty failures -> NoFailuresToReach
        | Some selection ->
            let decided = failures |> List.map (fun f -> f, reaches selection f)

            match decided |> List.tryFind (fun (_, r) -> r = Undecidable) with
            | Some(undecidable, _) ->
                ReachUnknown
                    $"the %s{undecidable.Project} red names no test class (a timeout, an errored host, or \
                       unparseable failure output) and the retained selection runs that project under a CLASS \
                       filter, so whether `check` would have executed it cannot be decided"
            | None ->
                let reached =
                    decided
                    |> List.choose (fun (f, r) -> if r = Reached then Some f.Project else None)
                    |> List.distinct
                    |> List.sort

                if List.isEmpty reached then
                    decided
                    |> List.choose (fun (failure, reach) ->
                        match failure.Class, reach with
                        | Some className, Missed cause ->
                            Some
                                { Project = failure.Project
                                  Class = className
                                  Cause = cause }
                        | _ -> None)
                    |> ReachedNoFailure
                else
                    ReachedAFailure reached

    /// A selection alarm and its recall metric must be based on the same complete,
    /// per-test receipt. A runner/host/infrastructure failure that leaves no report,
    /// an incomplete report, or an unreadable report is typed as `Error` by
    /// `failedTestsOfRun`; it is evidence that the run failed, not evidence that the
    /// selector missed each apparent failure.
    let classifyEvidence
        (wouldHaveRun: Map<string, ProjectSelection> option)
        (failures: Result<OutstandingFailure list, string>)
        : CheckReach * FailureRecall =
        match failures with
        | Ok exactFailures -> classify wouldHaveRun exactFailures, measure wouldHaveRun exactFailures
        | Error reason -> ReachUnknown reason, RecallNotMeasurable reason

[<NoComparison; NoEquality>]
type TestPruneMsg =
    | TestsFinished of started: TestRunStarted * completed: TestRunCompleted * launch: TestRunLaunch
    /// The launch found the build artifacts unusable. `owed` is the dependency fanout the
    /// launch consumed and did not run, which stays owed.
    | ArtifactsUnavailable of reason: string * owed: Set<string> * reply: Tasks.TaskCompletionSource<string> option
    /// The test host could not start, or the run has no result to fold. `owed` as for
    /// `ArtifactsUnavailable`. `opened` is the run the launch had already started, which
    /// nothing else will close.
    | TestHostUnavailable of
        reason: string *
        owed: Set<string> *
        reply: Tasks.TaskCompletionSource<string> option *
        opened: TestRunStarted option
    /// The run was cancelled because every client that wanted it has gone. It proves
    /// nothing, and what it would have discharged stays owed. `owed`, `reply` and `opened`
    /// as for `TestHostUnavailable`.
    | TestRunWithdrawn of
        owed: Set<string> *
        reply: Tasks.TaskCompletionSource<string> option *
        opened: TestRunStarted option
    /// A `run-tests` force-run completed. Folded exactly like `TestsFinished`; `reply` is
    /// resolved with `response` once that fold is published.
    | CommandTestsFinished of
        started: TestRunStarted *
        completed: TestRunCompleted *
        launch: TestRunLaunch *
        reply: Tasks.TaskCompletionSource<string> *
        response: string
    /// A run owed after the one holding the "tests" key: queued as an intent behind it,
    /// coalesced, and decided against the state it is delivered into.
    | ImpactRunRequested
    /// The held launch `id` (`DiffWait`) waited out its bound.
    | DiffWaitExpired of id: Guid
    /// `set-scope`: the mode every later launch runs under.
    | ScopeRequested of TestMode
    /// `set-scope owed`: a command (`check`) asks for the run this session owes. Arms a
    /// session that launches only requested runs (`TestMode.armOwedRun`), and launches the
    /// run it held, if any.
    | OwedRunRequested
    /// A run could not ingest a project's runtime coverage. Its durable record
    /// (`CoverageNotIngested`) is already written; this makes the owner owe the project too.
    | RuntimeCoverageFailed of project: string * reason: string
    /// A `run-tests` IPC command asking the MAILBOX to launch its force-run under the
    /// "tests" key. The command must never execute tests
    /// on the IPC thread itself: a run outside the slot is invisible to the daemon's
    /// runtime model — `IsRunning "tests"` reads false (so a concurrent FileChecked
    /// stamps a terminal status over it), the plugin never reports Running, and
    /// `AnyPluginBusy()` reads false, letting a concurrent `fshw check` resolve its
    /// verdict wait and exit 0 while the test process is still alive. `reply` carries
    /// the results JSON back to the awaiting command; every completion path must
    /// resolve it.
    | RunTestsRequested of configs: TestConfig list * filter: string option * reply: Tasks.TaskCompletionSource<string>
    /// `run-tests --only-failed`. Which projects failed is resolved by `Update` from the
    /// state it folds this message into, never from a snapshot the command read earlier.
    | RunFailedTestsRequested of filter: string option * reply: Tasks.TaskCompletionSource<string>
    /// Posted by a traced run before it joins its traces against the symbol index, and
    /// resolved when folded. Messages this plugin posts fold in the order they were
    /// admitted, interleaved with dispatched events; they are folded ahead of those events
    /// only while a result-first run's result is queued, and the only result-first run is
    /// the test run that posts this, before its result exists. So once this folds, every
    /// analysis event admitted before it has been folded into the index.
    | IndexFoldProbe of folded: Tasks.TaskCompletionSource
