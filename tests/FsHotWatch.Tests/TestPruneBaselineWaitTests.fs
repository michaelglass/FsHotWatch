/// A restarted daemon diffs from its own checkout's last full pass (AUTOMATION-1587).
///
/// A scan waits for the build before it type-checks, so a restarted daemon's first
/// `BuildSucceeded` arrives before the check results of the files the build compiled. It
/// launched at once, took its inputs before the diff folded, and with no session baseline
/// ran every project in full; the edit then ran again in a queued re-run (the repro: the diff
/// folded 9 s after the launch, 20,817 tests then 162 classes).
///
/// When the tree MOVED from this checkout's own last full pass at T (the ledger records T,
/// the index is still sealed at T, and a passing `test-baseline` record for exactly T holds
/// T's manifest), the first launch now waits for the fold and selects from the diff T to T'
/// as a warm session would. An unchanged tree does not wait: the skip on records answers it.
/// A miss, or a wait that runs out its bound, is today's launch.
module FsHotWatch.Tests.TestPruneBaselineWaitTests

open System
open System.IO
open System.Text.Json.Nodes
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

/// The box-wide stores, beside the checkout rather than in it.
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

/// A daemon over the checkout `f`, wired to the box-wide stores. `busy` makes every launch
/// find the tests key held, and `queued` counts the re-runs queued behind it.
type private Daemon =
    { F: Fixture
      Busy: bool ref
      Queued: int ref }

let private dbPathOf (f: Fixture) = Path.Combine(f.Ctx.RepoRoot, "tp.db")

let private daemonOver (f: Fixture) (wiring: TestEvidenceRun.Wiring) =
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
            (dbPathOf f)
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
      Busy = busy
      Queued = queued }

let private batch () : BatchChecked =
    { Trigger = BootScan
      Files = []
      Generation = 1L
      ModelGeneration = Some 1L
      Retained = None
      StartedAt = DateTime.UtcNow
      CompletedAt = DateTime.UtcNow }

/// File the `test-baseline` record a green full run over a sealed index files for the tree
/// on disk, in step 2's shape (schema, run, tree, analyser build, manifest).
let private fileRecord (outcomes: OutcomeStore.Store) (root: string) (runId: string) (analyzer: string) =
    let tree = (ReceiptInputTree.readTree root).Value
    let key = TestBaseline.keyOf tree.Identity
    let o = JsonObject()
    o["schema"] <- TestBaseline.Schema
    o["runId"] <- runId
    o["tree"] <- tree.Identity
    o["analyzer"] <- analyzer
    o["projects"] <- JsonObject()
    let manifest = JsonArray()

    for path, hash in tree.Entries do
        manifest.Add(JsonArray(JsonValue.Create path, JsonValue.Create hash, null))

    o["manifest"] <- manifest

    let filed =
        outcomes.Supersede(
            OutcomeStore.Record.ofAction
                key
                tree.Identity
                OutcomeStore.Outcome.Pass
                "test-producer"
                root
                DateTimeOffset.UtcNow,
            Some(o.ToJsonString())
        )

    Assert.True(filed.IsSome, "positive control: the baseline record was filed")
    key.Value.Text

/// A checkout whose daemon sealed its index at T, passed every project in full there and
/// published it, with T's `test-baseline` record filed (unless `record` is false): what a
/// green `confirm --fresh` leaves behind.
type private Verified =
    { F: Fixture
      Outcomes: OutcomeStore.Store
      Wiring: TestEvidenceRun.Wiring
      Tree: string
      Key: string option }

let private verified (record: bool) =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    write f.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x\n"
    write f.Ctx.RepoRoot "tests/ProjA/data.json" "{ \"v\": 1 }\n"
    let outcomes, wiring = storesBeside f.Ctx.RepoRoot
    let first = daemonOver f wiring
    let sealedState = update first.F first.F.Handler.Init (BatchChecked(batch ()))
    let tree = (ReceiptInputTree.read f.Ctx.RepoRoot).Value
    Assert.True((sealedState.SealedTree = Some tree), "positive control: the index is sealed at T")
    Assert.True((IndexSeal.load (dbPathOf f) = Some tree), "positive control: the seal is on disk")

    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree f.Ctx.RepoRoot }

    let passed = update first.F sealedState (finishedGreen full)
    Assert.True(passed.Earned.IsSome, "positive control: the full run earned evidence")

    Assert.True(
        (passed.Debt.Baseline |> Option.bind _.Tree = Some tree),
        "positive control: the ledger records the tree its full suite passed"
    )

    publish first.F sealedState passed

    let key =
        if record then
            let runId = passed.Debt.Baseline.Value.RunId.ToString "N"
            Some(fileRecord outcomes f.Ctx.RepoRoot runId AnalysisRecords.analyzerIdentity)
        else
            None

    { F = first.F
      Outcomes = outcomes
      Wiring = wiring
      Tree = tree
      Key = key }

let private restart (v: Verified) = daemonOver v.F v.Wiring

/// The cold scan's fold found `Lib.f` changed against the index's rows.
let private withDiff (state: TestPruneState) =
    { state with
        ChangedSymbols = [ "Lib.f" ]
        Debt =
            { state.Debt with
                PendingQueue = Set.singleton "Lib.f" } }

/// A covered body edit of `Lib.f`, made while no daemon watched.
let private editLib (v: Verified) =
    write v.F.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x + 0\n"

let private editData (v: Verified) =
    write v.F.Ctx.RepoRoot "tests/ProjA/data.json" "{ \"v\": 2 }\n"

let private scheduledLaunch (f: Fixture) =
    Assert.True(f.Scheduled.Value.IsSome, "a launch must have been scheduled")

    match f.Scheduled.Value.Value Ready |> Async.RunSynchronously with
    | TestsFinished(_, _, launch) -> launch
    | other -> failwith $"expected TestsFinished, got %A{other}"

let private firstBuild (d: Daemon) =
    d.F.Scheduled.Value <- None
    withLogLines (fun () -> update d.F d.F.Handler.Init (BuildCompleted BuildSucceeded))

let private waitLines (lines: string list) =
    lines |> List.filter (fun l -> l.Contains "diff-wait:")

let private everyProjectInFull =
    Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ]

// ---------------------------------------------------------------------------
// (1) The ticket's measured case: a restart after a COVERED body edit runs the covering
// classes, not the full suite.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a restart after a covered body edit waits for the fold and runs only the covering classes`` () =
    let v = verified true
    editLib v
    let d = restart v

    let held, lines = firstBuild d

    test <@ d.F.Scheduled.Value.IsNone @>
    test <@ held.AwaitingDiff.IsSome @>

    test
        <@
            waitLines lines
            |> List.exists (fun l ->
                l.Contains "diff-wait: holding"
                && l.Contains $"checkout-baseline tree=%s{v.Tree} record=%s{v.Key.Value}")
        @>

    let _, releaseLines =
        withLogLines (fun () -> update d.F (withDiff held) (BatchChecked(batch ())))

    let launch = scheduledLaunch d.F
    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>
    test <@ launch.Symbols.Contains "Lib.f" @>
    test <@ d.Queued.Value = 0 @>

    test
        <@
            waitLines releaseLines
            |> List.exists (fun l ->
                l.Contains "held=yes"
                && l.Contains "checkout-baseline"
                && l.Contains "timed-out=no")
        @>

[<Fact(Timeout = 30000)>]
let ``without a checkout baseline the same restart runs every project in full, as today`` () =
    // The control for (1): the same checkout and edit, no record for T.
    let v = verified false
    editLib v
    let d = restart v

    let held, lines = firstBuild d

    test <@ held.AwaitingDiff.IsNone @>
    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>

    test
        <@
            waitLines lines
            |> List.exists (fun l -> l.Contains "held=no" && l.Contains "reason=none")
        @>

// ---------------------------------------------------------------------------
// (2) A moved tree the symbol diff cannot answer still runs wide: T's manifest is the
// evidence, so a non-symbol path in T→T' forces every project (AUTOMATION-1559, ADR-039).
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a restart after a non-symbol edit waits and then runs every project in full`` () =
    let v = verified true
    editData v
    let d = restart v

    let held, _ = firstBuild d
    test <@ held.AwaitingDiff.IsSome @>

    // The fold finds no symbol: the moved path is test data.
    update d.F held (BatchChecked(batch ())) |> ignore
    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>

[<Fact(Timeout = 30000)>]
let ``a covered edit beside a non-symbol edit runs every project in full`` () =
    let v = verified true
    editLib v
    editData v
    let d = restart v

    let held, _ = firstBuild d
    update d.F (withDiff held) (BatchChecked(batch ())) |> ignore
    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>

/// A restart after the covered edit with a local red in ProjB, the record for T filed or not.
let private firstLaunchWithLocalRed (record: bool) =
    let v = verified record
    editLib v

    OutstandingFailures.OutstandingFailure.save
        v.F.Ctx.RepoRoot
        [ { Project = "ProjB"
            Class = Some "ProjBTests"
            Method = Some "bTest"
            File = "<tests/ProjB>"
            Entry = FsHotWatch.ErrorLedger.ErrorEntry.error "failed ProjBTests.bTest" } ]

    let d = restart v
    let held, _ = firstBuild d
    held, scheduledLaunch d.F

[<Fact(Timeout = 30000)>]
let ``a local red is not narrowed by a checkout baseline: the launch is today's, at once`` () =
    // §4.2 condition 5: local reds beat the baseline. T passed in full, but this checkout's
    // own ledger names a red in ProjB since, so something is owed and the launch does not
    // wait: it runs the red, exactly as it does with no record for T.
    let held, launched = firstLaunchWithLocalRed true
    let _, control = firstLaunchWithLocalRed false

    test <@ held.AwaitingDiff.IsNone @>
    test <@ held.CheckoutBaseline.IsNone @>
    test <@ launched.Selection.ContainsKey "ProjB" @>
    test <@ launched.Selection = control.Selection @>

// ---------------------------------------------------------------------------
// (3) The bound runs out: the launch is today's full suite, and the diff that folds later
// queues today's re-run. Never a narrowed set.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a wait that runs out its bound launches the full suite and a late diff queues a re-run`` () =
    let v = verified true
    editLib v
    let d = restart v
    let held, _ = firstBuild d
    test <@ held.AwaitingDiff.IsSome @>

    let expired, lines =
        withLogLines (fun () -> update d.F held (Custom(DiffWaitExpired held.AwaitingDiff.Value.Id)))

    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>
    test <@ expired.CheckoutBaseline.IsNone @>

    test
        <@
            waitLines lines
            |> List.exists (fun l -> l.Contains "checkout-baseline" && l.Contains "timed-out=yes")
        @>

    d.Busy.Value <- true
    d.F.Scheduled.Value <- None
    update d.F (withDiff expired) (BatchChecked(batch ())) |> ignore
    test <@ d.F.Scheduled.Value.IsNone @>
    test <@ d.Queued.Value = 1 @>

// ---------------------------------------------------------------------------
// (4) An unchanged tree does not wait: the skip on records answers it without a diff.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a restart over the unchanged tree does not wait for the fold`` () =
    let v = verified true
    let d = restart v

    let held, lines = firstBuild d

    test <@ held.AwaitingDiff.IsNone @>
    test <@ d.F.Scheduled.Value.IsSome @>

    test
        <@
            waitLines lines
            |> List.exists (fun l -> l.Contains "held=no" && l.Contains "reason=none")
        @>

    test <@ waitLines lines |> List.exists (fun l -> l.Contains "the tree has not moved") @>

// ---------------------------------------------------------------------------
// (5) Every miss is no checkout baseline: today's launch, at once.
// ---------------------------------------------------------------------------

/// The first build of a restart after the covered edit, with `spoil` applied first: it must
/// not wait, must launch every project in full, and must say why in its `diff-wait:` line.
let private missesWith (why: string) (spoil: Verified -> unit) =
    let v = verified true
    editLib v
    spoil v
    let d = restart v

    let held, lines = firstBuild d

    test <@ held.AwaitingDiff.IsNone @>
    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>

    test
        <@
            waitLines lines
            |> List.exists (fun l -> l.Contains "no checkout baseline" && l.Contains why)
        @>

[<Fact(Timeout = 30000)>]
let ``no record for T is a miss`` () =
    missesWith "no test-baseline record for that tree" (fun v ->
        Directory.Delete(v.Outcomes.DirectoryOf(TestBaseline.keyOf v.Tree), true))

[<Fact(Timeout = 30000)>]
let ``an index sealed elsewhere is a miss`` () =
    missesWith "the index is sealed at another-tree" (fun v ->
        IndexSeal.save ignore (dbPathOf v.F) (Some "another-tree"))

[<Fact(Timeout = 30000)>]
let ``an unsealed index is a miss`` () =
    missesWith "the index is sealed at no tree" (fun v -> IndexSeal.save ignore (dbPathOf v.F) None)

[<Fact(Timeout = 30000)>]
let ``a record from a different analyser build is a miss`` () =
    missesWith "a different analyser build" (fun v ->
        let tree = v.Tree
        // Filed over T's bytes, so the edit is undone for the filing and redone after.
        write v.F.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x\n"
        fileRecord v.Outcomes v.F.Ctx.RepoRoot "run" "another-analyser" |> ignore
        Assert.True(((ReceiptInputTree.read v.F.Ctx.RepoRoot).Value = tree), "positive control: filed over T")
        editLib v)

[<Fact(Timeout = 30000)>]
let ``a record that is not a pass is a miss`` () =
    missesWith "not a pass" (fun v ->
        v.Outcomes.Supersede(
            OutcomeStore.Record.ofAction
                (TestBaseline.keyOf v.Tree)
                v.Tree
                OutcomeStore.Outcome.Fail
                "test-producer"
                "/elsewhere"
                DateTimeOffset.UtcNow,
            None
        )
        |> ignore)

[<Fact(Timeout = 30000)>]
let ``a corrupt record is a miss`` () =
    missesWith "no test-baseline record for that tree" (fun v ->
        let dir = v.Outcomes.DirectoryOf(TestBaseline.keyOf v.Tree)

        for file in Directory.GetFiles(dir, "*.json") do
            File.WriteAllText(file, "{ not json"))

[<Fact(Timeout = 30000)>]
let ``a ledger with no tree is a miss`` () =
    missesWith "records no tree" (fun v ->
        match FullSuiteBaseline.load v.F.Ctx.RepoRoot with
        | FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) ->
            FullSuiteBaseline.save v.F.Ctx.RepoRoot { baseline with Tree = None }
        | other -> failwith $"expected a ledger baseline, got %A{other}")

// ---------------------------------------------------------------------------
// The seal itself.
// ---------------------------------------------------------------------------

[<Fact>]
let ``IndexSeal: a seal reads back, and a cleared, blank or unwritable one is no seal`` () =
    withTempDir "index-seal" (fun dir ->
        let db = Path.Combine(dir, "tp.db")
        test <@ IndexSeal.load db = None @>
        IndexSeal.save ignore db (Some "tree-1")
        test <@ IndexSeal.load db = Some "tree-1" @>
        IndexSeal.save ignore db None
        test <@ IndexSeal.load db = None @>
        IndexSeal.save ignore db None
        test <@ IndexSeal.load db = None @>
        File.WriteAllText(IndexSeal.pathOf db, "  \n")
        test <@ IndexSeal.load db = None @>

        // A seal that cannot be written says so and leaves no seal behind.
        let blocker = Path.Combine(dir, "blocker")
        File.WriteAllText(blocker, "")
        let logged = Collections.Generic.List<string>()
        IndexSeal.save logged.Add (Path.Combine(blocker, "tp.db")) (Some "tree-2")
        test <@ logged.Count >= 1 @>
        test <@ IndexSeal.load (Path.Combine(blocker, "tp.db")) = None @>)

[<Fact>]
let ``IndexSeal: a seal that cannot be read is no seal, and one that can be neither rewritten nor cleared says so`` () =
    withTempDir "index-seal" (fun dir ->
        let sub = Path.Combine(dir, "idx")
        Directory.CreateDirectory sub |> ignore
        let db = Path.Combine(sub, "tp.db")
        IndexSeal.save ignore db (Some "tree-1")
        let seal = IndexSeal.pathOf db
        let writable = File.GetUnixFileMode sub
        File.SetUnixFileMode(seal, UnixFileMode.None)
        File.SetUnixFileMode(sub, UnixFileMode.UserRead ||| UnixFileMode.UserExecute)

        try
            // The control must be real: a reader that can still read the file proves nothing.
            let readable =
                try
                    File.ReadAllText seal |> ignore
                    true
                with :? UnauthorizedAccessException ->
                    false

            test <@ not readable @>
            test <@ IndexSeal.load db = None @>

            let logged = Collections.Generic.List<string>()
            IndexSeal.save logged.Add db (Some "tree-2")
            test <@ logged |> Seq.exists (fun l -> l.Contains "could not be written") @>
            test <@ logged |> Seq.exists (fun l -> l.Contains "could not be cleared either") @>
        finally
            File.SetUnixFileMode(sub, writable)

            if File.Exists seal then
                File.SetUnixFileMode(seal, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact(Timeout = 30000)>]
let ``a restart whose diff finds only uncovered symbols runs rather than skip with no evidence of its own`` () =
    // The skip earns no evidence, and the verdict waits for evidence under the current
    // model: a first launch that skipped would leave `check` waiting for ever.
    let v = verified true
    editLib v
    let d = restart v
    let held, _ = firstBuild d
    test <@ held.AwaitingDiff.IsSome @>

    let uncovered =
        { held with
            ChangedSymbols = [ "Lib.f" ]
            ChangedSymbolsAllUncovered = UncoveredChanges.AllUncovered([ "Lib.f" ], Map.empty) }

    update d.F uncovered (BatchChecked(batch ())) |> ignore
    let launch = scheduledLaunch d.F
    test <@ launch.ZeroSelection = ZeroSelection.NotAZero @>
    test <@ not (Map.isEmpty launch.Selection) @>

// --- What a restart would select, published for idle-exit (AUTOMATION-1623, ADR-069) ---
//
// The test plugin publishes, from the same three facts `checkoutBaselineIn` reads, whether a
// restarted daemon's first check would select its tests correctly: the ledger's full-suite
// tree T, the index still sealed at T, and T's `test-baseline` record. idle-exit's AUTO window
// is 5 minutes only while it says so. A stale "selects correctly" must not survive anything
// that changes one of the three facts.

let private published (v: Verified) = Restart.read v.F.Ctx.RepoRoot

[<Fact(Timeout = 30000)>]
let ``a daemon starting over a usable checkout baseline publishes that its restart selects correctly`` () =
    let v = verified true
    restart v |> ignore
    test <@ published v = Restart.Selection.SelectsCorrectly v.Tree @>

[<Fact(Timeout = 30000)>]
let ``without T's test-baseline record a daemon publishes that its restart would run the full suite`` () =
    let v = verified false
    restart v |> ignore
    test <@ published v = Restart.Selection.WouldRunFullSuite "no test-baseline record for that tree" @>

[<Fact(Timeout = 30000)>]
let ``a re-seal at another tree republishes WouldRunFullSuite, so a stale SelectsCorrectly does not survive`` () =
    // m-1623-la: the ledger's tree was an edited tree, and every later check re-sealed the
    // index elsewhere; a restart there ran the full suite (461-651 s).
    let v = verified true
    let d = restart v
    Assert.True((published v = Restart.Selection.SelectsCorrectly v.Tree), "positive control: ready at start")
    editLib v
    let resealed = update d.F d.F.Handler.Init (BatchChecked(batch ()))
    let tree' = (ReceiptInputTree.read v.F.Ctx.RepoRoot).Value
    Assert.True((tree' <> v.Tree), "positive control: the edit moved the tree")
    Assert.True((resealed.SealedTree = Some tree'), "positive control: the index re-sealed at T'")

    test
        <@
            published v = Restart.Selection.WouldRunFullSuite
                $"the index is sealed at %s{tree'}, not at the baseline's tree %s{v.Tree}"
        @>

[<Fact(Timeout = 30000)>]
let ``a completion republishes what a restart would select`` () =
    // No record when the daemon starts, so it publishes WouldRunFullSuite. The record is then
    // filed (as another checkout's full pass of T would). Nothing reads the store until the
    // next completion, which must republish from the current facts.
    let v = verified false
    let d = restart v
    // The restarted daemon's scan seals its index at the unchanged tree T, as a live one does.
    let sealedState = update d.F d.F.Handler.Init (BatchChecked(batch ()))
    Assert.True((sealedState.SealedTree = Some v.Tree), "positive control: sealed at T")
    Assert.True((published v <> Restart.Selection.SelectsCorrectly v.Tree), "positive control: not ready yet")

    fileRecord v.Outcomes v.F.Ctx.RepoRoot "another-run" AnalysisRecords.analyzerIdentity
    |> ignore

    Assert.True(
        (published v <> Restart.Selection.SelectsCorrectly v.Tree),
        "positive control: filing alone republishes nothing"
    )

    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree v.F.Ctx.RepoRoot }

    update d.F sealedState (finishedGreen full) |> ignore
    test <@ published v = Restart.Selection.SelectsCorrectly v.Tree @>
