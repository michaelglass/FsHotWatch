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
