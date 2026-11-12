/// The first launch waits for the cold scan's fold when a baseline is in hand
/// (AUTOMATION-1587 step 4, design §4.1(d) and §7 Q3).
///
/// A scan waits for the build before it type-checks, so a restarted daemon's first
/// `BuildSucceeded` arrives before the check results of the files the build compiled. With
/// a session baseline `DiffWait` holds that launch for the fold; without one it launched at
/// once and took its inputs before the diff arrived, and the edit ran only in a queued
/// re-run (the repro: the diff folded 9 s after the launch). A `test-baseline` record for
/// the current receipt tree, named by the box-wide index, is now a baseline in hand too.
///
/// The record decides only WHETHER the launch waits. It is never read by selection: the
/// launch selects from the state it is delivered into exactly as before, and a wait that
/// runs out its bound launches what the launch would have launched without it.
module FsHotWatch.Tests.TestPruneBaselineWaitTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open FsHotWatch.Tests.TestPruneModelChangeTests

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// The box-wide stores, beside the checkout rather than in it: a store inside the root
/// would move the receipt tree it is asked about.
let private storesBeside (root: string) =
    let box = root + ".box"

    let outcomes =
        OutcomeStore.Store(
            { Root = Path.Combine(box, "outcomes")
              Ttl = OutcomeStore.defaultTtl
              MaxBytes = OutcomeStore.defaultMaxBytes
              Log = ignore }
        )

    let wiring: TestEvidenceRun.Wiring =
        { Store =
            TestEvidenceStore.Store(
                { Root = Path.Combine(box, "evidence")
                  Ttl = TimeSpan.FromHours 72.0
                  MaxBytes = 64L * 1024L * 1024L
                  Now = fun () -> DateTimeOffset.UtcNow
                  Log = ignore }
            )
          Configs = Map.empty
          Toolchain = lazy "test-toolchain"
          Loaded = TestEvidenceKey.LoadedEnvironment.empty
          ProducerHash = "test-producer"
          ExcludePatterns = []
          Outcomes = Some outcomes }

    outcomes, wiring

/// What a restart sees: the checkout `f` verified and published, and a new daemon over it
/// wired to the box-wide stores. `busy` makes every later launch find the tests key held,
/// and `queued` counts the re-runs queued behind it.
type private Restart =
    { F: Fixture
      Outcomes: OutcomeStore.Store
      Busy: bool ref
      Queued: int ref }

let private restartOver (f: Fixture) (wiring: TestEvidenceRun.Wiring) outcomes =
    let next = restarted f
    let busy = ref false
    let queued = ref 0

    let handler =
        createWithEvidenceAndQueries
            (Some wiring)
            ImpactQueries.ofDatabase
            (TimeSpan.FromMinutes 5.0)
            None
            (fun () -> Map.empty)
            (Path.Combine(f.Ctx.RepoRoot, "tp.db"))
            TestPrune.Domain.WalkPolicy.FollowAll
            f.Ctx.RepoRoot
            (Some configs)
            None
            None
            None
            None
            []
            None
            TestMode.initial

    let ctx =
        { next.Ctx with
            RunExclusiveShared =
                fun _ _ work _ _ ->
                    if busy.Value then
                        LocalSlotBusy
                    else
                        next.Scheduled.Value <- Some work
                        SharedClaimed
            EnqueueExclusiveIntent =
                fun _ _ _ ->
                    queued.Value <- queued.Value + 1
                    Task.FromResult(()) }

    { F =
        { next with
            Handler = handler
            Ctx = ctx }
      Outcomes = outcomes
      Busy = busy
      Queued = queued }

/// A covered source file and a test-data file, verified in full and published.
let private verifiedCheckout () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    write f.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x\n"
    write f.Ctx.RepoRoot "tests/ProjA/data.json" "{ \"v\": 1 }\n"

    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree f.Ctx.RepoRoot }

    let verified = update f f.Handler.Init (finishedGreen full)
    Assert.True(verified.Earned.IsSome, "positive control: the full run earned evidence")
    publish f f.Handler.Init verified
    f

/// File a `test-baseline` record for `tree` and name it in the index, as a green full run
/// over a sealed index does (step 2).
let private fileBaseline (outcomes: OutcomeStore.Store) (tree: string) =
    let key = TestBaseline.keyOf tree
    let now = DateTimeOffset.UtcNow

    let filed =
        outcomes.Supersede(
            OutcomeStore.Record.ofAction key tree OutcomeStore.Outcome.Pass "test-producer" "/elsewhere" now,
            Some "{}"
        )

    Assert.True(filed.IsSome, "positive control: the baseline record was filed")

    let indexed =
        TestBaseline.appendIndex
            outcomes
            { Binary = "test-producer"
              Worktree = "/elsewhere"
              Now = now }
            { Tree = tree
              Baseline = key.Value.Text
              RunId = "earning-run"
              At = now }

    Assert.True(indexed.IsSome, "positive control: the index names it")
    key.Value.Text

let private currentTree (f: Fixture) =
    (ReceiptInputTree.read f.Ctx.RepoRoot).Value

/// A restart over a verified checkout, with (`record`) or without a baseline record for
/// the tree on disk.
let private setUp (record: bool) =
    let f = verifiedCheckout ()
    let outcomes, wiring = storesBeside f.Ctx.RepoRoot

    let key =
        if record then
            Some(fileBaseline outcomes (currentTree f))
        else
            None

    restartOver f wiring outcomes, key

let private batch () : BatchChecked =
    { Trigger = BootScan
      Files = []
      Generation = 1L
      ModelGeneration = Some 1L
      Retained = None
      StartedAt = DateTime.UtcNow
      CompletedAt = DateTime.UtcNow }

/// The cold scan's fold found `Lib.f` changed against the index's rows.
let private withDiff (state: TestPruneState) =
    { state with
        ChangedSymbols = [ "Lib.f" ]
        Debt =
            { state.Debt with
                PendingQueue = Set.singleton "Lib.f" } }

/// The launch the last event scheduled, executed.
let private scheduledLaunch (f: Fixture) =
    Assert.True(f.Scheduled.Value.IsSome, "a launch must have been scheduled")

    match f.Scheduled.Value.Value Ready |> Async.RunSynchronously with
    | TestsFinished(_, _, launch) -> launch
    | other -> failwith $"expected TestsFinished, got %A{other}"

/// Deliver the first build.
let private firstBuild (r: Restart) =
    r.F.Scheduled.Value <- None
    withLogLines (fun () -> update r.F r.F.Handler.Init (BuildCompleted BuildSucceeded))

let private waitLines (lines: string list) =
    lines |> List.filter (fun l -> l.Contains "diff-wait:")

/// What a launch decided to run, apart from the inputs it took.
let private decided (launch: TestRunLaunch) =
    launch.Selection, launch.ZeroSelection, launch.FullSuiteCause

// ---------------------------------------------------------------------------
// (1) A baseline record for the receipt tree holds the first launch for the fold, so its
// inputs include the diff, and no re-run is queued for it.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``with a baseline record for the receipt tree the first launch waits and takes the diff that folds after it`` () =
    let r, key = setUp true

    let held, lines = firstBuild r

    test <@ r.F.Scheduled.Value.IsNone @>
    test <@ held.AwaitingDiff.IsSome @>

    test
        <@
            waitLines lines
            |> List.exists (fun l -> l.Contains "holding" && l.Contains $"reason=baseline-record %s{key.Value}")
        @>

    // The diff arrives after the build, as the cold scan delivers it.
    let released, releaseLines =
        withLogLines (fun () -> update r.F (withDiff held) (BatchChecked(batch ())))

    test <@ released.AwaitingDiff.IsNone @>
    let launch = scheduledLaunch r.F
    test <@ launch.Symbols.Contains "Lib.f" @>
    test <@ r.Queued.Value = 0 @>

    test
        <@
            waitLines releaseLines
            |> List.exists (fun l ->
                l.Contains "waited-ms="
                && l.Contains $"reason=baseline-record %s{key.Value}"
                && l.Contains "timed-out=no")
        @>

// ---------------------------------------------------------------------------
// (2) No baseline in hand, session or record: exactly today's launch, no wait.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``with no baseline record and no session baseline the first launch does not wait`` () =
    let r, _ = setUp false

    let held, lines = firstBuild r

    test <@ held.AwaitingDiff.IsNone @>
    test <@ r.F.Scheduled.Value.IsSome @>

    test
        <@
            waitLines lines
            |> List.exists (fun l ->
                l.Contains "waited-ms=0"
                && l.Contains "reason=none"
                && l.Contains "timed-out=no")
        @>

[<Fact(Timeout = 30000)>]
let ``a baseline record for another tree is not a baseline in hand`` () =
    let r, _ = setUp false
    fileBaseline r.Outcomes "some-other-tree" |> ignore

    let held, _ = firstBuild r

    test <@ held.AwaitingDiff.IsNone @>
    test <@ r.F.Scheduled.Value.IsSome @>

// ---------------------------------------------------------------------------
// (3) The bound runs out: the launch takes what it has, a full suite as today, and the
// diff that folds later queues today's re-run. It never launches a narrowed set.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a wait that runs out its bound launches the full suite and a late diff queues a re-run`` () =
    let r, key = setUp true
    let held, _ = firstBuild r
    test <@ held.AwaitingDiff.IsSome @>

    let expired, lines =
        withLogLines (fun () -> update r.F held (Custom(DiffWaitExpired held.AwaitingDiff.Value.Id)))

    let launch = scheduledLaunch r.F

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ] @>
    test <@ Set.isEmpty launch.Symbols @>

    test
        <@
            waitLines lines
            |> List.exists (fun l -> l.Contains $"reason=baseline-record %s{key.Value}" && l.Contains "timed-out=yes")
        @>

    // The diff folds while that run holds the tests key.
    r.Busy.Value <- true
    r.F.Scheduled.Value <- None
    update r.F (withDiff expired) (BatchChecked(batch ())) |> ignore
    test <@ r.F.Scheduled.Value.IsNone @>
    test <@ r.Queued.Value = 1 @>

// ---------------------------------------------------------------------------
// (4) The record changes WHEN the launch takes its inputs, never WHICH tests it selects
// from them: the same inputs select the same run with and without it.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a held launch selects exactly what the same inputs select without a record`` () =
    // Nothing folded: released owing nothing, against launched at once.
    let withRecord, _ = setUp true
    let held, _ = firstBuild withRecord
    test <@ held.AwaitingDiff.IsSome @>
    update withRecord.F held (BatchChecked(batch ())) |> ignore
    let releasedEmpty = scheduledLaunch withRecord.F

    let without, _ = setUp false
    firstBuild without |> ignore
    let atOnceEmpty = scheduledLaunch without.F

    test <@ decided releasedEmpty = decided atOnceEmpty @>

    // The diff folded: released into it, against a build that finds it already folded.
    let withRecord, _ = setUp true
    let held, _ = firstBuild withRecord
    update withRecord.F (withDiff held) (BatchChecked(batch ())) |> ignore
    let releasedDiff = scheduledLaunch withRecord.F

    let without, _ = setUp false
    without.F.Scheduled.Value <- None

    update without.F (withDiff without.F.Handler.Init) (BuildCompleted BuildSucceeded)
    |> ignore

    let atOnceDiff = scheduledLaunch without.F

    test <@ decided releasedDiff = decided atOnceDiff @>
    test <@ releasedDiff.Symbols = atOnceDiff.Symbols @>

// ---------------------------------------------------------------------------
// (5) An index that cannot be read, or that names a record the store no longer holds, is
// a miss: no wait.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``an index whose record is corrupt is a miss and the launch does not wait`` () =
    // A baseline for this tree IS filed and indexed; only the index record's bytes are bad.
    let r, _ = setUp true
    let dir = r.Outcomes.DirectoryOf TestBaseline.indexKey
    let file = Directory.GetFiles(dir, "*.json") |> Array.exactlyOne
    File.WriteAllText(file, "{ not json")

    let held, lines = firstBuild r

    test <@ held.AwaitingDiff.IsNone @>
    test <@ r.F.Scheduled.Value.IsSome @>
    test <@ waitLines lines |> List.exists (fun l -> l.Contains "reason=none") @>

[<Fact(Timeout = 30000)>]
let ``an index the daemon cannot read is a miss and the launch does not wait`` () =
    let r, _ = setUp true
    let dir = r.Outcomes.DirectoryOf TestBaseline.indexKey
    let file = Directory.GetFiles(dir, "*.json") |> Array.exactlyOne
    File.SetUnixFileMode(file, UnixFileMode.None)

    try
        let readable =
            try
                File.ReadAllText file |> ignore
                true
            with :? UnauthorizedAccessException ->
                false

        // The control must be real: a reader that can still read the file proves nothing.
        test <@ not readable @>

        let held, _ = firstBuild r

        test <@ held.AwaitingDiff.IsNone @>
        test <@ r.F.Scheduled.Value.IsSome @>
    finally
        if File.Exists file then
            File.SetUnixFileMode(file, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

[<Fact(Timeout = 30000)>]
let ``an index entry whose baseline record is gone is a miss and the launch does not wait`` () =
    let r, _ = setUp true
    let tree = currentTree r.F
    Directory.Delete(r.Outcomes.DirectoryOf(TestBaseline.keyOf tree), true)

    let held, _ = firstBuild r

    test <@ held.AwaitingDiff.IsNone @>
    test <@ r.F.Scheduled.Value.IsSome @>
