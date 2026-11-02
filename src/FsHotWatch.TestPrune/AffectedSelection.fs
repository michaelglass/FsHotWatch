/// Impact selection over a flushed state: which tests the queued symbols reach, the poisoned-seed guard, attribution, and how every queued symbol is classified by the projects covering it.
module FsHotWatch.TestPrune.AffectedSelection

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

/// What impact selection reads from the plugin that runs it: its index and queries, the
/// configured projects, how debt is classified and committed, and the poison-seed ages as
/// they stand when it is called.
type internal SelectionEnv =
    {
        Db: Database
        Queries: ImpactQueries
        RunnableProjects: Set<string>
        /// The test projects covering each of `symbols`.
        CoveringOf: string seq -> string -> Set<string>
        /// The projects a symbol is owed to, under the declared exclusions.
        DebtScope: (string -> Set<string>) -> string -> Set<string>
        /// Retire `symbols` from the debt.
        CommitPending: Set<string> -> VerificationDebt -> VerificationDebt
        /// The poison-seed ages, read when called (`pendingAgeRef`).
        SeedAges: unit -> Map<string, int>
    }

let internal selectAffectedWith
    (env: SelectionEnv)
    (flushedState: TestPruneState)
    (runtimeObligations: RuntimeCoverageObligations)
    =
    let db = env.Db
    let queries = env.Queries
    let runnableProjects = env.RunnableProjects
    let coveringOf = env.CoveringOf
    let lazyDebtScope = env.DebtScope
    let commitPending = env.CommitPending

    // Affected tests must be computed from the WHOLE needs-testing queue —
    // the in-memory hot view UNION the durable sidecar — not just the latest
    // diff. The persisted queue holds symbols a green run hasn't yet cleared
    // (e.g. carried across a restart, or left behind by an Aborted/failed
    // run); they must keep selecting tests until a covering run passes.
    let symbols =
        Set.union flushedState.Debt.PendingQueue (Set.ofList flushedState.ChangedSymbols)
        |> Set.toList

    let affectedTests, seedFanout =
        if symbols.IsEmpty then
            [], SeedFanoutReading.NoSelection
        else
            // Scoped to projects this daemon actually runs. Selecting tests in an
            // unconfigured project would put classes in the run map that never
            // execute — and make `allChangesUncovered` (and so the zero-affected
            // skip) disagree with the commit rule. See `debtScope`.
            let queryRunnable (seeds: string list) =
                queries.AffectedTests seeds
                |> fun ts ->
                    if Set.isEmpty runnableProjects then
                        ts
                    else
                        ts |> List.filter (fun t -> Set.contains t.TestProject runnableProjects)

            let affected = queryRunnable symbols
            let sortedSeeds = List.sort symbols

            // Count the INPUT explicitly, not just the output. `symbols` is the
            // union of the durable pending-verification queue and the in-memory
            // hot view, so it grows monotonically across aborted runs until a
            // green run clears it — "the queue is wedged and growing" and "a
            // small, precise selection" look identical without this number, and
            // `%A` truncated it away at 100.
            //
            // The seed list is logged in FULL and sorted (`describeAll`, not
            // `%A`): when a handful of junk seeds drags in thousands of tests,
            // the offending seed is the whole diagnosis, and a sample that
            // happens to omit it costs hours.
            Logging.info
                "test-prune"
                $"QueryAffectedTests: %d{symbols.Length} seed(s) → %d{affected.Length} affected tests"

            Logging.info "test-prune" $"  seeds: %s{describeAll sortedSeeds}"

            // How many tests ONE seed selects on its own — a full recursive
            // reverse-walk each time, so it is memoised. Two diagnostics below ask
            // this same question (the poisoned-seed guard and the per-seed
            // attribution), and the seeds they ask about OVERLAP: `agedSeeds` is a
            // subset of `sortedSeeds`, so in the case that matters most — a wide
            // selection AND pinned seeds, which is exactly the shape both exist to
            // catch — every aged seed was paying for the same query twice in one
            // flush. Only `.Length` is ever used, so nothing needs the rows.
            let aloneCounts = System.Collections.Generic.Dictionary<string, int>()

            let aloneCount (seed: string) : int =
                match aloneCounts.TryGetValue seed with
                | true, n -> n
                | false, _ ->
                    let n = (queryRunnable [ seed ]).Length
                    aloneCounts[seed] <- n
                    n

            // The poisoned-seed guard.
            //
            // The per-seed attribution below asks "is one seed dominating THIS run?",
            // a question about a moment. The failure that happened was about TIME:
            // `name` and `kind` dominated every run for days, and each run looked
            // like a legitimately expensive edit. Only width that PERSISTS separates
            // them.
            //
            // This only READS the ages — they advance once per test RUN, at the
            // launch point (see `pendingAgeRef`). Do not bump them here: this
            // function runs 2-3 times per edit-save cycle, so the counter would
            // measure flushes, and two edits to one function on a green repo would
            // trip a guard designed to fire late.
            //
            // Independent of `WideSelectionTests`: a seed pinning 200 tests of a
            // 400-test suite is the same disease as one pinning 3,000, and gating on
            // absolute width would miss every smaller repo.
            let ages = env.SeedAges()

            let ageOf s =
                ages |> Map.tryFind s |> Option.defaultValue 0

            // Each check below is a full recursive reverse-walk, so both the count and
            // the decision to run at all are gated:
            //
            //  * an EMPTY selection can never yield a suspect (`isPoisonSuspect`
            //    requires `affectedCount > 0`), so the loop is skipped — the common
            //    case on a no-op cycle;
            //  * the same `MaxSeedsToAttribute` budget the attribution loop uses.
            //    `agedSeeds` grows precisely when the queue is wedged — the situation
            //    this guard exists to report — so an unbudgeted loop would pay N graph
            //    walks per build exactly when the daemon is already struggling. A
            //    suspect must account for >=25% of the selection, so at most four
            //    seeds can qualify and a large aged list is waste by construction.
            //    The cap is never silent.
            let agedSeeds = sortedSeeds |> List.filter (fun s -> ageOf s >= PoisonSeedRuns)

            if not affected.IsEmpty && agedSeeds.Length > MaxSeedsToAttribute then
                Logging.warn
                    "test-prune"
                    $"%d{agedSeeds.Length} seed(s) have been queued across %d{PoisonSeedRuns}+ consecutive \
                      runs, which exceeds the %d{MaxSeedsToAttribute}-seed budget — the poisoned-seed check \
                      is SKIPPED this cycle. A pending queue that size is itself the finding; the full seed \
                      list is logged above."

            for seed in
                (if affected.IsEmpty || agedSeeds.Length > MaxSeedsToAttribute then
                     []
                 else
                     agedSeeds) do
                let runs = ageOf seed
                let alone = aloneCount seed

                if isPoisonSuspect runs affected.Length alone then
                    let pct = alone * 100 / (max 1 affected.Length)

                    // Deliberately a WARNING and not a quarantine: dropping the
                    // symbol would be under-testing on a guess. The failure this
                    // addresses is that nobody could SEE the pattern.
                    Logging.warn
                        "test-prune"
                        $"POSSIBLE POISONED SEED: '%s{seed}' has been queued for verification across \
                          %d{runs} consecutive runs and alone selects %d{alone} of %d{affected.Length} \
                          tests (%d{pct}%%). A symbol only leaves the queue once every runnable project \
                          covering it passes, so one persistently-failing project pins it and it re-seeds \
                          this selection every run. Check whether it is a real dependency-graph hub, a \
                          mis-qualified symbol, or a test project that has been red for a \
                          while."

            // Per-seed attribution, but ONLY when the selection is already wide.
            // A single seed accounting for most of a run is either a genuine
            // graph hub or a mis-qualified symbol, and is the only thing worth
            // looking at; below the threshold this costs nothing because the
            // re-query loop never runs.
            if affected.Length > WideSelectionTests then
                if sortedSeeds.Length > MaxSeedsToAttribute then
                    // Never cap silently: say the attribution was skipped and why,
                    // so an absent breakdown is not read as "no seed dominated".
                    Logging.warn
                        "test-prune"
                        $"%d{affected.Length} tests selected, but %d{sortedSeeds.Length} seeds exceeds the \
                          %d{MaxSeedsToAttribute}-seed attribution budget — per-seed breakdown SKIPPED"
                else
                    // Derived from the same constant `isPoisonSuspect` uses, never a
                    // second spelling of it: a `/ 4` here beside a `25` there drifts,
                    // and the two diagnostics then disagree about "dominant".
                    let dominantShare = affected.Length * PoisonSeedSharePercent / 100

                    for seed in sortedSeeds do
                        let alone = aloneCount seed

                        if alone > affected.Length then
                            // Not a hub: the selection is not monotone in its seeds. The
                            // composition-root fail-safe restores a test project's
                            // unbarriered tests only when the whole seed set leaves it
                            // empty, so a sibling seed that reaches the project directly
                            // can suppress the restore this seed gets on its own.
                            Logging.warn
                                "test-prune"
                                $"seed '%s{seed}' alone selects %d{alone} tests, MORE than the %d{affected.Length} \
                                  the whole queue selects — the composition-root fail-safe is not monotone in \
                                  its seeds, so this seed's own tests are narrower when it is queued with others"
                        elif alone > dominantShare then
                            let pct = alone * 100 / affected.Length

                            Logging.warn
                                "test-prune"
                                $"seed '%s{seed}' alone selects %d{alone} of %d{affected.Length} tests \
                                  (%d{pct}%%) — a dependency-graph hub, or a mis-qualified symbol"

            // The top seeds by fanout, for the verdict: the same memoised walks the
            // breakdown above paid for, under the same width and seed budget.
            let fanout =
                SeedFanoutReading.ofSelection
                    WideSelectionTests
                    MaxSeedsToAttribute
                    affected.Length
                    sortedSeeds
                    aloneCount

            affected, fanout

    // Classify every considered symbol ONCE, under one resolution of the declared
    // exclusions (see `debtScope`):
    //
    //  * NOTHING OWED — no covering test at all, or only coverers that `tests.excluded`
    //    declares out of scope with a reason. Dropped from the durable queue now:
    //    retaining it would re-select zero tests forever and never empty. Only ever
    //    REMOVES from the queue. A declaration-covered drop is a WRITE-OFF, not a
    //    discharge, so it is REPORTED, naming the project, in the log and on the verdict
    //    (`UncoveredChanges`).
    //  * OWED ELSEWHERE — a covering project this daemon does not run and nothing
    //    declares excluded. Nothing here can discharge it, and dropping it would let a
    //    configured-suite green retire tests that never ran. It STAYS owed; the verdict
    //    stays red and names the project until the config lists or excludes it.
    let covering = coveringOf symbols
    let owedTo = lazyDebtScope covering
    let owing = symbols |> List.map (fun s -> s, owedTo s) |> Map.ofList

    // An index missing a file's analysis (`Unindexed`) cannot prove that nothing covers
    // a change: the missing file may hold the very test. Nothing is dropped as
    // uncovered then; it stays owed until a covering run passes.
    let indexIncomplete = not (Set.isEmpty flushedState.Unindexed)

    let uncovered =
        if indexIncomplete then
            Set.empty
        else
            owing
            |> Map.filter (fun _ projects -> Set.isEmpty projects)
            |> Map.keys
            |> Set.ofSeq

    let unrunnable: UnrunnableCoverage =
        if Set.isEmpty uncovered || Set.isEmpty runnableProjects then
            Map.empty
        else
            uncovered
            |> Set.toList
            |> List.choose (fun s ->
                match covering s with
                | projects when Set.isEmpty projects -> None
                | projects -> Some(s, projects))
            |> Map.ofList

    let owedToUnrunnable =
        owing
        |> Map.map (fun _ projects -> Set.difference projects runnableProjects)
        |> Map.filter (fun _ projects -> not (Set.isEmpty projects))

    if not (Set.isEmpty uncovered) then
        Logging.info
            "test-prune"
            $"Dropping %d{Set.count uncovered} queued symbol(s) with no covering test in a governed project from pending-verification queue"

        if not (Map.isEmpty unrunnable) then
            let projects =
                UnrunnableCoverage.projects unrunnable |> Set.toList |> String.concat ", "

            Logging.warn
                "test-prune"
                $"%d{Map.count unrunnable} of them ARE covered — only by tests in %s{projects}, which \
                  `tests.excluded` declares out of this gate's scope. The obligation is written off by that \
                  declaration, not discharged by a test. Symbols: \
                  %s{describeAll (unrunnable |> Map.keys |> List.ofSeq)}"

    if not (Map.isEmpty owedToUnrunnable) then
        let projects =
            UnrunnableCoverage.projects owedToUnrunnable |> Set.toList |> String.concat ", "

        Logging.warn
            "test-prune"
            $"%d{Map.count owedToUnrunnable} queued symbol(s) stay OWED to tests in %s{projects}, which this \
              daemon does not run and `tests.excluded` does not declare. No run here can discharge them, so \
              the verdict stays red: list the project in `tests.projects`, or declare it in `tests.excluded` \
              with a reason. Symbols: %s{describeAll (owedToUnrunnable |> Map.keys |> List.ofSeq)}"

    // Keep the in-memory hot view aligned with the durable queue so the
    // ChangedSymbols carried in state (and the cache-key snapshot) don't
    // re-select the uncovered symbols on the next event.
    let remainingSymbols =
        flushedState.ChangedSymbols
        |> List.filter (fun s -> not (Set.contains s uncovered))

    // There WERE symbols to consider this cycle, yet the affected set is empty — so
    // every one of them was just dropped as uncovered (a union query returning zero
    // tests means every per-symbol query did too). That is a definitive "nothing to
    // verify" green, which the run-trigger reads to complete immediately instead of
    // running the full suite. An EMPTY `symbols` (genuine cold start, nothing
    // pending) leaves this false so the baseline still runs.
    //
    // The flag buys a green that executes NOTHING, so it must rest
    // on proof — and an empty `QueryAffectedTests` proves "no test covers this" only
    // for a symbol the index KNOWS. For a name it has never heard of, the identical
    // empty result means "I cannot answer". So ask the index what it KNOWS
    // (`unknownToIndex`), never how it came to be in that state.
    //
    // The two files drift apart in practice: a `SchemaVersion` bump deletes and
    // recreates `test-impact.db`, but the pending-verification sidecar beside it
    // carries no version and SURVIVES. Every name in the queue then resolves to
    // nothing, is dropped just above as "no runnable covering test", and the cycle
    // completes GREEN with ZERO tests run — the debt discharged by the schema bump
    // rather than by a test. A stale queue entry naming a since-renamed symbol takes
    // the same route with no recreate involved.
    //
    // The symbols are still dropped from the queue above, so a permanently-absent
    // name cannot wedge it; the run happens once and discharges the debt.
    //
    // `GetAllSymbolNames` is a full read of the `symbols` table, so it sits behind
    // `noCoveringTest` — already the rare branch (queued symbols AND a zero-length
    // selection). On the ordinary path the index is never consulted.
    let noCoveringTest = not symbols.IsEmpty && List.isEmpty affectedTests

    let unknownToIndex =
        if not noCoveringTest then
            []
        else
            let known = db.GetAllSymbolNames()
            symbols |> List.filter (fun s -> not (known.Contains s)) |> List.sort

    let indexCannotVouch = noCoveringTest && not (List.isEmpty unknownToIndex)

    if indexCannotVouch then
        Logging.warn
            "test-prune"
            $"%d{symbols.Length} queued symbol(s) resolved to no covering test, but %d{unknownToIndex.Length} \
              of them are NOT KNOWN to the symbol index — so for those that is 'the index cannot answer', not \
              proof they are untested. Refusing the zero-test green; this run verifies them for real. \
              Unknown: %s{describeAll unknownToIndex}"

    if noCoveringTest && indexIncomplete then
        Logging.warn
            "test-prune"
            $"%d{symbols.Length} queued symbol(s) resolved to no covering test, but the index lacks the analysis \
              of %d{Set.count flushedState.Unindexed} file(s) a project-model change retired \
              (%s{describeAll (Set.toList flushedState.Unindexed)}) — refusing the zero-test green"

    // Symbols still owed elsewhere are not uncovered: the zero-test green must not
    // retire the red they hold.
    let allChangesUncovered =
        if
            noCoveringTest
            && List.isEmpty unknownToIndex
            && Map.isEmpty owedToUnrunnable
            && not indexIncomplete
        then
            UncoveredChanges.AllUncovered(List.sort symbols, unrunnable)
        else
            UncoveredChanges.No

    // Remember the seeds ONLY when this selection actually has tests to run.
    // The question the report has to answer is "what was the last change that
    // DID trigger tests?" — a selection that chose nothing did not, and
    // recording it would answer that question with the one change guaranteed
    // to be irrelevant. Carrying the previous value forward is the point: it
    // is what survives to be reported by a later check that selects nothing.
    let seedsThatSelectedTests, fanoutOfThoseSeeds =
        if List.isEmpty affectedTests then
            flushedState.LastSeeds, flushedState.LastSeedFanout
        else
            List.sort symbols, seedFanout

    { flushedState with
        Debt =
            { commitPending uncovered flushedState.Debt with
                RuntimeObligations = runtimeObligations }
        ChangedSymbols = remainingSymbols
        AffectedTests = Analyzed affectedTests
        ChangedSymbolsAllUncovered = allChangesUncovered
        LastSeeds = seedsThatSelectedTests
        LastSeedFanout = fanoutOfThoseSeeds }
