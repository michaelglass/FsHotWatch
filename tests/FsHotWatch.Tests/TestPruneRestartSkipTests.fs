/// The zero-affected skip across a daemon restart (AUTOMATION-1587).
///
/// A daemon that verified tree T with a full suite, published that evidence, and then
/// restarted, refuses the zero-affected skip on its first build: `hasCachedResults` is
/// `LastResults.IsSome`, which only a run completed in THIS process sets, so the cause
/// "no run has completed in this session yet" runs every configured project in full,
/// whatever the persisted evidence says. These tests pin both sides of that seam: what a
/// restart over the verified tree should cost (the positive tests, red today), and what
/// it must keep costing when the tree moved while the daemon was down (the negative
/// controls, green today and after any relaxation).
module FsHotWatch.Tests.TestPruneRestartSkipTests

open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Database
open FsHotWatch.Events
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open FsHotWatch.Tests.TestPruneModelChangeTests

/// Write `text` at the repo-relative `rel` under the fixture's root.
let private write (f: Fixture) (rel: string) (text: string) =
    let path = Path.Combine(f.Ctx.RepoRoot, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// A checkout with a covered source file, an uncovered one, and a test-data file: the
/// three kinds of edit a restart can find.
let private seededTree (f: Fixture) =
    write f "src/Lib.fs" "module Lib\nlet f x = x\n"
    write f "src/Orphan.fs" "module Orphan\nlet uncovered x = x\n"
    write f "tests/ProjA/data.json" "{ \"v\": 1 }\n"

/// Both projects passed a full suite under model 1, bound to the tree as it is on disk
/// now, and that evidence was published: what a green `confirm` leaves behind.
let private confirmedThenPublished (f: Fixture) =
    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree f.Ctx.RepoRoot }

    let verified = update f f.Handler.Init (finishedGreen full)

    Assert.True(verified.Earned.IsSome, "positive control: the full run earned evidence")
    Assert.Empty(verified.Earned.Value.FailureReasons)
    Assert.True(verified.EvidenceReceipt.IsSome, "positive control: the full run left a standing receipt")
    publish f f.Handler.Init verified

/// The first build a restarted daemon sees, with the log lines it wrote.
let private firstBuildAfterRestart (f: Fixture) =
    let next = restarted f
    withLogLines (fun () -> buildAndLaunch next next.Handler.Init |> snd)

let private refusedForSession (lines: string list) =
    lines
    |> List.exists (fun l -> l.Contains "no run has completed in this session yet")

let private setUp () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    seededTree f
    f, identities

// ---------------------------------------------------------------------------
// Positive: a restart over the verified tree. RED today (AUTOMATION-1587): both run every
// configured project in full, logging "the zero-affected skip is refused — no run has
// completed in this session yet". Unskip when the relaxation lands.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000,
       Skip = "AUTOMATION-1587 (red today): a restart over the confirmed tree runs every project in full")>]
let ``a restarted daemon over the tree its full suite passed on runs nothing`` () =
    let f, _ = setUp ()
    confirmedThenPublished f

    let launch, lines = firstBuildAfterRestart f

    test <@ not (refusedForSession lines) @>
    test <@ Map.isEmpty launch.Selection @>
    test <@ launch.ZeroSelection = ZeroSelection.AlreadyVerified @>

// ---------------------------------------------------------------------------
// Negative controls: the tree moved while the daemon was down, so the persisted receipt
// is for a DIFFERENT tree. Covering projects must still run — the moved-tree false green
// the "a BuildCompleted never replays while no receipt is bound to the current input
// tree" fix closed. GREEN today; must stay green after any relaxation.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a restarted daemon whose tree moved by a covered source edit still runs the covering project`` () =
    let f, identities = setUp ()
    confirmedThenPublished f

    // Edited while no daemon watched: ProjA compiles src/Lib.fs, so its compile inputs
    // moved too.
    write f "src/Lib.fs" "module Lib\nlet f x = x + 1\n"
    identities.Value <- Map.add (fsprojOf "ProjA") "a2" identities.Value

    let launch, _ = firstBuildAfterRestart f

    test <@ launch.Selection.ContainsKey "ProjA" @>
    test <@ launch.ZeroSelection = ZeroSelection.NotAZero @>

[<Fact(Timeout = 30000)>]
let ``a restarted daemon whose tree moved by a non-source edit does not skip`` () =
    let f, _ = setUp ()
    confirmedThenPublished f

    // A test-data file no symbol diff sees, and no compile input: only the receipt's tree
    // binding can tell this tree from the confirmed one.
    write f "tests/ProjA/data.json" "{ \"v\": 2 }\n"

    let launch, _ = firstBuildAfterRestart f

    test <@ not (Map.isEmpty launch.Selection) @>
    test <@ launch.ZeroSelection = ZeroSelection.NotAZero @>

// ---------------------------------------------------------------------------
// The nothing-to-verify route, across a restart: once the restarted daemon's symbol diff
// has found the edited symbol and proved no test covers it, that route skips without a
// session baseline. Pinned so the design can say which half of the measured case is the
// refusal and which half is the cold scan's ordering (BuildCompleted before the diff).
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a restarted daemon whose diff found only an uncovered symbol runs nothing`` () =
    let f, _ = setUp ()

    let orphan: TestPrune.AstAnalyzer.SymbolInfo =
        { FullName = "Orphan.uncovered"
          Kind = TestPrune.AstAnalyzer.SymbolKind.Value
          Location = TestPrune.AstAnalyzer.InSource("src/Orphan.fs", 1, 1, "orphan-hash") }

    let db = Database.create (Path.Combine(f.Ctx.RepoRoot, "tp.db"))
    db.RebuildProjects([ TestPrune.AstAnalyzer.AnalysisResult.Create([ orphan ], [], []) ])
    clearSqlitePoolForDb db
    confirmedThenPublished f
    write f "src/Orphan.fs" "module Orphan\nlet uncovered x = x + 0\n"

    let next = restarted f

    let owed =
        { next.Handler.Init with
            ChangedSymbols = [ "Orphan.uncovered" ]
            Debt =
                { next.Handler.Init.Debt with
                    PendingQueue = Set.singleton "Orphan.uncovered" } }

    let _, launch = buildAndLaunch next owed

    test <@ Map.isEmpty launch.Selection @>

    match launch.ZeroSelection with
    | ZeroSelection.ChangesUncovered _ -> ()
    | other -> Assert.Fail($"expected the nothing-to-verify skip, got %A{other}")

// ---------------------------------------------------------------------------
// The same seam over the box-wide evidence store (AUTOMATION-1587 design §4.2). A restart
// over a tree whose every project holds a full-scope PASS record at its K_P is the case
// the design's first step answers from those records. These are the soundness conditions
// that answer must keep: each is a tree, a store or a ledger on which a restarted daemon
// must still RUN the project. Green today; they stay green under the relaxation.
//
// Every project keys over the whole verdict tree (`reads` at its default, as every
// intelligence project does), so a byte anywhere under `src/` or `tests/` moves every K_P.
// ---------------------------------------------------------------------------

module Ev = FsHotWatch.Tests.TestEvidenceRunTests

/// The box-wide outcome store beside `store` (ADR-059): every completed project run is
/// filed there under the K_P it ran over, a fail as well as a pass.
let private outcomesBeside (store: FsHotWatch.TestEvidenceStore.Store) =
    FsHotWatch.OutcomeStore.Store(
        { Root = Path.Combine(Path.GetDirectoryName(store.Root: string), "outcomes")
          Ttl = FsHotWatch.OutcomeStore.defaultTtl
          MaxBytes = FsHotWatch.OutcomeStore.defaultMaxBytes
          Log = ignore }
    )

/// Every project keyed over the whole verdict tree, with the outcome store wired.
let private verdictTreeWiring (store: FsHotWatch.TestEvidenceStore.Store) : TestEvidenceRun.Wiring =
    { Ev.wiringFor store with
        Configs =
            Ev.testProjects
            |> List.map (fun t -> t, FsHotWatch.TestEvidenceKey.defaultConfig)
            |> Map.ofList
        Outcomes = Some(outcomesBeside store) }

/// A daemon over `root` in a long-lived checkout. `seeded` gives it the full-suite
/// baseline such a checkout already holds; a restart reads the one its predecessor left.
let private daemonOver
    (beforeRun: (System.Guid -> FsHotWatch.HookStep.Tracker -> unit) option)
    (seeded: bool)
    store
    root
    =
    Ev.checkoutSetUp None beforeRun seeded root (Some(verdictTreeWiring store)) false

/// The first check a daemon `start`ed over a checkout answers, with the log lines it
/// wrote. The daemon is created inside the capture, so its log reaches it.
let private firstCheckOf (start: unit -> Ev.Checkout) =
    withLogLines (fun () -> Ev.run (start ()))

let private failed (result: TestResult) =
    match result with
    | TestsFailed _ -> true
    | _ -> false

/// A checkout whose first daemon passed every project in full and published each green.
let private earned store root =
    let launched, completed = Ev.run (daemonOver None true store root)
    Assert.True((launched = Ev.testProjects), "positive control: the first check ran every project")
    Assert.True(Ev.allGreen completed, "positive control: and every one passed")
    completed

[<Fact(Timeout = Ev.RunsTestCapMs)>]
let ``a red full run leaves no record a restarted daemon could skip on`` () =
    // §4.2 condition 2: a red run earns no green record for the project that failed.
    // Another long-lived checkout of the SAME red tree, restarted, must still run it.
    Ev.withRepo (fun store rootNamed ->
        let red = rootNamed "red"
        Ev.write red "src/X/fail" ""
        let _, first = Ev.run (daemonOver None true store red)
        test <@ failed first.Results["TX"] @>

        let other = rootNamed "other"
        Ev.write other "src/X/fail" ""

        let (launched, completed), _ =
            firstCheckOf (fun () -> daemonOver None true store other)

        test <@ launched = [ "TX" ] @>
        test <@ failed completed.Results["TX"] @>)

[<Fact(Timeout = Ev.RunsTestCapMs)>]
let ``a stop-on-fail red run leaves its failing project to run after a restart`` () =
    // §4.2 condition 2, AUTOMATION-1581's shape: a red report that names only the test
    // that stopped the run. It is accounted for, so it may earn the ADR-021 baseline, but
    // it earns no full-pass record, and a restart over the same tree runs TX again. The
    // projects that passed in that run are not launched.
    Ev.withRepo (fun store rootNamed ->
        let root = rootNamed "a"
        Ev.write root "src/X/fail" ""
        let _, first = Ev.run (daemonOver None true store root)
        test <@ failed first.Results["TX"] @>

        let (launched, completed), _ =
            firstCheckOf (fun () -> daemonOver None false store root)

        test <@ launched = [ "TX" ] @>
        test <@ failed completed.Results["TX"] @>)

[<Fact(Timeout = Ev.RunsTestCapMs)>]
let ``a restart over a tree moved by a non-symbol path runs every project`` () =
    // §4.2 condition 3: a path no symbol diff answers (test data) moved while no daemon
    // watched. Every K_P names the whole verdict tree, so no record stands for any project.
    Ev.withRepo (fun store rootNamed ->
        let root = rootNamed "a"
        earned store root |> ignore
        Ev.write root "tests/TX/data.json" "{ \"v\": 2 }\n"

        let (launched, completed), _ =
            firstCheckOf (fun () -> daemonOver None false store root)

        test <@ launched = Ev.testProjects @>
        test <@ List.isEmpty (Ev.reusedIn completed) @>
        test <@ Ev.allGreen completed @>)

[<Fact(Timeout = Ev.RunsTestCapMs)>]
let ``a local outstanding failure runs its project after a restart whatever the store holds`` () =
    // §4.2 condition 5: local reds beat a box-wide green. The store holds a green for TX at
    // exactly this tree, earned in another checkout; THIS checkout's ledger still names
    // TX's failure, so its restarted daemon runs TX.
    Ev.withRepo (fun store rootNamed ->
        earned store (rootNamed "passed") |> ignore

        let root = rootNamed "a"
        Ev.write root "src/X/fail" ""
        let _, red = Ev.run (daemonOver None true store root)
        test <@ failed red.Results["TX"] @>
        // Back to the passed tree's bytes while no daemon watched.
        File.Delete(Path.Combine(root, "src/X/fail"))

        let (launched, completed), _ =
            firstCheckOf (fun () -> daemonOver None false store root)

        test <@ launched = [ "TX" ] @>
        test <@ TestResult.verifiedGreen completed.Results["TX"] @>
        test <@ not (Ev.isReused completed.Results["TX"]) @>)

[<Fact(Timeout = Ev.RunsTestCapMs,
       Skip =
           "AUTOMATION-1587 step 6 (red today): a fresh workspace diffs against no baseline tree, so one edit runs every project")>]
let ``a fresh workspace one body edit from a passed tree runs only the project over the edit`` () =
    // The design's general case (§4.1): a new checkout at T' = T + one edit finds T's
    // baseline record, diffs T→T' by analysis identity, and selects as a long-lived
    // checkout would. Today the fresh workspace holds no baseline and every K_P moved.
    Ev.withRepo (fun store rootNamed ->
        earned store (rootNamed "passed") |> ignore
        let fresh = rootNamed "fresh"
        Ev.edit fresh "Z" "let z = 2"

        let (launched, _), _ = firstCheckOf (fun () -> daemonOver None false store fresh)

        test <@ launched = [ "TZ" ] @>)
