/// The one place TestPrune's behaviour depends on how a verdict is being earned.
///
/// `check` and `confirm` share one run lifecycle: claim the "tests" key, launch, run,
/// fold the result, record its evidence, baseline, coverage and flakiness history, and
/// write every cache. `confirm` runs every test by definition, so it SUBTRACTS the work
/// that only exists to pick a subset — the named, typed set `PassThroughSkip` — and
/// nothing else. Everything not in that set runs in both modes by default.
///
/// The cases of `TestMode` are named only in this file. Elsewhere, code asks this module
/// a question (`skips`, `requestsFullSuite`), so a new feature cannot silently land on
/// one side of a mode branch: adding to the skip set is an edit here, in view.
/// `TestModeSeamTests` scans `src/` and fails on any other branch on the mode.
namespace FsHotWatch.TestPrune

/// How the plugin earns a verdict, set by `set-scope`.
type TestMode =
    /// `check`: each run is the impact selection of what is owed.
    | ImpactSelection
    /// `confirm`: every launch runs every configured project in full.
    | PassThrough

/// Work `PassThrough` removes from the shared lifecycle, because a run that executes
/// every project in full has no use for it.
///
/// Never a cache READ or WRITE, and never evidence: the symbol index, file freshness,
/// sidecars, pending-verification state, task-cache entries, coverage and flakiness
/// history are populated in both modes, so a `confirm` leaves a fully warm daemon.
type PassThroughSkip =
    /// The flush's impact selection: the affected-tests query, the poisoned-seed and
    /// attribution diagnostics, and the per-symbol covering classification that drops
    /// uncovered symbols. The launch still asks one grouped affected-tests query, for the
    /// check-reach sample of what `check` would have selected.
    | FlushSelection
    /// The launch's per-symbol capture of which projects cover each launched symbol.
    | LaunchCoveringCapture
    /// The poisoned-seed age counters, which measure how long a seed keeps widening a
    /// selection. No seed widens a run that selects nothing.
    | SeedAgeing
    /// Queueing another run behind the one in flight. Debt that arrives while a full run
    /// is in flight attaches to it instead; the run covers it only over an unchanged tree.
    | RerunIntents

[<RequireQualifiedAccess>]
module TestMode =
    /// A daemon session starts in `check`'s mode.
    let initial = ImpactSelection

    /// The mode a `set-scope` scope argument asks for: `"full"` is `confirm`'s.
    let ofScope (scope: string) =
        if scope = "full" then PassThrough else ImpactSelection

    /// Every skip, in one place.
    let skipped (mode: TestMode) : Set<PassThroughSkip> =
        match mode with
        | ImpactSelection -> Set.empty
        | PassThrough -> Set.ofList [ FlushSelection; LaunchCoveringCapture; SeedAgeing; RerunIntents ]

    /// Whether `mode` removes `work` from the lifecycle.
    let skips (work: PassThroughSkip) (mode: TestMode) = Set.contains work (skipped mode)

    /// The mode once a run launched under `launchedUnder` has concluded. Pass-through
    /// lasts for the confirm's run: the run it launched ends it, so a later `check` in the
    /// same daemon is back under impact selection.
    let afterRun (launchedUnder: TestMode) (current: TestMode) =
        match launchedUnder with
        | PassThrough -> ImpactSelection
        | ImpactSelection -> current

    /// Whether every launch runs every configured project in full, unfiltered.
    let requestsFullSuite (mode: TestMode) =
        match mode with
        | PassThrough -> true
        | ImpactSelection -> false

    /// Whether a run launched under `mode` records per-test traces. `full-runs` records only
    /// where every project runs in full (confirm/nightly); `every-run` also records the
    /// impact-selected subset, which refreshes exactly the traces of the tests it ran.
    let recordsTraces (policy: TraceRecordPolicy) (mode: TestMode) =
        match policy with
        | RecordOff -> false
        | RecordEveryRun -> true
        | RecordFullRuns -> requestsFullSuite mode
