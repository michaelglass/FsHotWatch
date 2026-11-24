/// A restarted daemon diffs from its own checkout's last full pass (AUTOMATION-1587).
///
/// A scan waits for the build before it type-checks, so a restarted daemon's first
/// `BuildSucceeded` arrives before the check results of the files the build compiled. It
/// launched at once, took its inputs before the diff folded, and with no session baseline
/// ran every project in full; the edit then ran again in a queued re-run (the repro: the diff
/// folded 9 s after the launch, 20,817 tests then 162 classes).
///
/// When the tree MOVED from a full pass of this checkout's own at T (the index is sealed at
/// T, the ledger records this checkout passing T in full, as its tree or an earlier one it
/// keeps, and a passing `test-baseline` record for exactly T holds T's manifest), the first launch now waits for the fold and selects from the diff T to T'
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
          Outcomes = Some(FsHotWatch.OutcomeStore.Wired.disk outcomes ignore) }

    outcomes, wiring

/// A daemon over the checkout `f`, wired to the box-wide stores. `busy` makes every launch
/// find the tests key held, and `queued` counts the re-runs queued behind it.
type private Daemon =
    { F: Fixture
      Busy: bool ref
      Queued: int ref }

let private dbPathOf (f: Fixture) = Path.Combine(f.Ctx.RepoRoot, "tp.db")

let private daemonOverWith (restartFrom: RestartBaselines) (f: Fixture) (wiring: TestEvidenceRun.Wiring) =
    let next = restarted f
    let busy = ref false
    let queued = ref 0

    let handler =
        createWithEvidenceAndQueries
            (Some wiring)
            ImpactQueries.ofDatabase
            (TimeSpan.FromMinutes 5.0)
            None
            restartFrom
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

let private daemonOver (f: Fixture) (wiring: TestEvidenceRun.Wiring) =
    daemonOverWith RestartBaselines.OwnCheckoutOnly f wiring

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
let private fileRecordWith
    (projects: Map<string, string>)
    (outcomes: OutcomeStore.Store)
    (root: string)
    (runId: string)
    (analyzer: string)
    =
    let tree = (ReceiptInputTree.readTree root).Value
    let key = TestBaseline.keyOf tree.Identity
    let o = JsonObject()
    o["schema"] <- TestBaseline.Schema
    o["runId"] <- runId
    o["tree"] <- tree.Identity
    o["analyzer"] <- analyzer
    let named = JsonObject()

    for KeyValue(project, key) in projects do
        named[project] <- key

    o["projects"] <- named
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

let private fileRecord = fileRecordWith Map.empty

/// A checkout whose daemon sealed its index at T, passed every project in full there and
/// published it, with T's `test-baseline` record filed (unless `record` is false): what a
/// green `confirm --fresh` leaves behind.
type private Verified =
    { F: Fixture
      Outcomes: OutcomeStore.Store
      Wiring: TestEvidenceRun.Wiring
      Tree: string
      Key: string option }

let private verifiedOver (f: Fixture) (record: bool) =
    write f.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x\n"
    write f.Ctx.RepoRoot "tests/ProjA/data.json" "{ \"v\": 1 }\n"
    let outcomes, wiring = storesBeside f.Ctx.RepoRoot
    let first = daemonOver f wiring
    let sealedState = update first.F first.F.Handler.Init (BatchChecked(batch ()))
    let tree = (ReceiptInputTree.read f.Ctx.RepoRoot).Value

    Assert.True(
        (sealedState.SealedTree |> Option.map _.Identity = Some tree),
        "positive control: the index is sealed at T"
    )

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

let private verified (record: bool) =
    verifiedOver (fixture () |> withIdentities (identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ])) record

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
// (6) AUTOMATION-1682: the cycle. A full pass earned at an EDITED tree X moved the ledger's
// tree to X; the revert re-sealed the index at T; the next restart refused ("the index is
// sealed at T, not at the baseline's tree X") and ran every project in full, earning the
// ledger at the next edited tree, and so on: every restart after an edit was a full suite.
// The cold scan's diff is from the tree the index IS sealed at, so that is the tree a
// restart diffs from, when this checkout's own full suite passed it.
// ---------------------------------------------------------------------------

let private ledgerOf (v: Verified) =
    match FullSuiteBaseline.load v.F.Ctx.RepoRoot with
    | FullSuiteBaseline.LoadedBaseline.Loaded(Some baseline) -> baseline
    | other -> failwith $"expected a ledger baseline, got %A{other}"

let private restoreLib (v: Verified) =
    write v.F.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x\n"

/// A daemon over `v` passes every project in full over the tree as it is now, with its index
/// sealed there, and files that tree's record: a full suite earned at an edited tree.
let private passInFullHere (v: Verified) =
    let d = restart v
    let sealedState = update d.F d.F.Handler.Init (BatchChecked(batch ()))
    let tree = (ReceiptInputTree.read v.F.Ctx.RepoRoot).Value

    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree v.F.Ctx.RepoRoot }

    let passed = update d.F sealedState (finishedGreen full)
    publish d.F sealedState passed
    let runId = passed.Debt.Baseline.Value.RunId.ToString "N"

    fileRecord v.Outcomes v.F.Ctx.RepoRoot runId AnalysisRecords.analyzerIdentity
    |> ignore

    Assert.True(((ledgerOf v).Tree = Some tree), "positive control: the ledger moved to the edited tree")
    d, passed, tree

[<Fact(Timeout = 30000)>]
let ``after a full pass at an edited tree and a revert, a restart diffs from the reverted tree it passed`` () =
    let v = verified true
    editLib v
    let d, passed, edited = passInFullHere v
    test <@ edited <> v.Tree @>

    // The revert: a fold re-seals the index at T, and no full suite runs there again.
    restoreLib v
    update d.F passed (BatchChecked(batch ())) |> ignore
    Assert.True((IndexSeal.load (dbPathOf v.F) = Some v.Tree), "positive control: the index is re-sealed at T")
    Assert.True(((ledgerOf v).Tree = Some edited), "positive control: the ledger still names the edited tree")

    // The next edit, and a fresh daemon.
    write v.F.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x + 1\n"
    let next = restart v
    // What idle-exit is told agrees: a restart now would select from T.
    test <@ Restart.read v.F.Ctx.RepoRoot = Restart.Selection.SelectsCorrectly v.Tree @>
    let held, lines = firstBuild next

    test <@ held.AwaitingDiff.IsSome @>

    test
        <@
            waitLines lines
            |> List.exists (fun l ->
                l.Contains "diff-wait: holding"
                && l.Contains $"checkout-baseline tree=%s{v.Tree} record=%s{v.Key.Value}")
        @>

    update next.F (withDiff held) (BatchChecked(batch ())) |> ignore
    let launch = scheduledLaunch next.F
    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>
    test <@ next.Queued.Value = 0 @>

[<Fact(Timeout = 30000)>]
let ``after a full pass at an edited tree, a restart still sealed there diffs from it, as before`` () =
    // The ordinary case keeps working: the index stayed sealed at the ledger's own tree.
    let v = verified true
    editLib v
    let _, _, edited = passInFullHere v
    write v.F.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x + 2\n"
    let next = restart v
    let held, lines = firstBuild next
    test <@ held.AwaitingDiff.IsSome @>

    test
        <@
            waitLines lines
            |> List.exists (fun l ->
                l.Contains "diff-wait: holding"
                && l.Contains $"checkout-baseline tree=%s{edited}")
        @>

/// The first build of a restart after the covered edit, with the index sealed at T and T's
/// record filed box-wide, but `forget` applied to this checkout's own ledger first. It must
/// not wait, must run every project in full, and must say why.
let private ownLedgerMisses (forget: Verified -> unit) =
    let v = verified true
    editLib v
    forget v
    Assert.True((IndexSeal.load (dbPathOf v.F) = Some v.Tree), "positive control: the index is sealed at T")

    Assert.True(
        (TestBaseline.checkoutRecordFor v.Outcomes v.Tree |> Result.isOk),
        "positive control: T's record is filed box-wide"
    )

    let d = restart v
    let held, lines = firstBuild d
    test <@ held.AwaitingDiff.IsNone @>
    test <@ held.CheckoutBaseline.IsNone @>
    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>
    test <@ waitLines lines |> List.exists (fun l -> l.Contains "no checkout baseline") @>

[<Fact(Timeout = 30000)>]
let ``negative control (1587): a checkout with no full-suite baseline of its own does not diff from another's record``
    ()
    =
    // T's record was filed by a run elsewhere and this checkout's index is sealed at T, but
    // this checkout never passed anything in full: its own tests may never have run here.
    ownLedgerMisses (fun v -> File.Delete(FullSuiteBaseline.sidecarPath v.F.Ctx.RepoRoot))

[<Fact(Timeout = 30000)>]
let ``negative control (1587): a sealed tree this checkout's full suite never passed is a miss, record or not`` () =
    // This checkout's own full passes were all at another tree; T's record is someone else's.
    ownLedgerMisses (fun v ->
        FullSuiteBaseline.save
            v.F.Ctx.RepoRoot
            { ledgerOf v with
                Tree = Some "sha256:elsewhere" })

// ---------------------------------------------------------------------------
// (7) AUTOMATION-1793 step 2: behind `tests.restartFromBoxBaseline`, a checkout with no full
// pass of its own restarts from the box's retained green baseline. Off, nothing changes.
// ---------------------------------------------------------------------------

/// A land: a checkout whose own full passes were all at another tree and whose index is
/// unsealed, after the covered edit, with T's record filed box-wide and named in the index. (A
/// checkout whose ledger records no full pass at all still owes one to earn its baseline, which
/// no restart baseline answers.)
let private boxed () =
    let v = verified true

    let producer: TestBaseline.Producer =
        { Binary = "test-producer"
          Worktree = "/elsewhere"
          Now = DateTimeOffset.UtcNow }

    let indexed =
        TestBaseline.appendIndex
            v.Outcomes
            producer
            { Tree = v.Tree
              Baseline = v.Key.Value
              RunId = "run"
              At = DateTimeOffset.UtcNow }

    Assert.True(indexed.IsSome, "positive control: T is named in the index")

    FullSuiteBaseline.save
        v.F.Ctx.RepoRoot
        { ledgerOf v with
            Tree = Some "sha256:elsewhere"
            EarlierTrees = [] }

    IndexSeal.save ignore (dbPathOf v.F) None
    editLib v
    v

/// `restartBaselineForIn` over `v` as a first build would ask it, counting index writes.
let private restartFor (restartFrom: RestartBaselines) (coldScan: ColdScan) (v: Verified) =
    let writes = ref 0
    let current = (ReceiptInputTree.read v.F.Ctx.RepoRoot).Value

    let outcome =
        PluginSteps.restartBaselineForIn
            restartFrom
            (Some v.Wiring)
            v.F.Ctx.RepoRoot
            (dbPathOf v.F)
            (fun _ _ -> writes.Value <- writes.Value + 1)
            (fun _ -> [])
            (fun _ _ _ -> Map.empty)
            coldScan
            (Some(ledgerOf v))
            current

    outcome, writes.Value, current

[<Fact(Timeout = 30000)>]
let ``flag off: a checkout with no full pass of its own gets exactly today's answer, and the index is untouched`` () =
    let v = boxed ()

    let outcome, writes, current =
        restartFor RestartBaselines.OwnCheckoutOnly ColdScan.NotStarted v

    let today =
        PluginSteps.checkoutBaselineIn (Some v.Wiring) (dbPathOf v.F) (Some(ledgerOf v)) current
        |> Result.map RestartBaseline.OwnBaseline

    test <@ outcome = today @>
    test <@ outcome = Error "the index is sealed at no tree" @>
    test <@ writes = 0 @>
    test <@ IndexSeal.load (dbPathOf v.F) = None @>

[<Fact(Timeout = 30000)>]
let ``flag on: a checkout with no full pass of its own restarts from the retained T, rebuilt and sealed there`` () =
    let v = boxed ()

    let outcome, writes, current =
        restartFor RestartBaselines.RetainedBoxBaselines ColdScan.NotStarted v

    match outcome with
    | Ok(RestartBaseline.BoxBaseline rebuilt) ->
        test <@ rebuilt.Baseline.Tree = v.Tree @>
        test <@ rebuilt.Baseline.Key = v.Key.Value @>
        test <@ rebuilt.Baseline.Tree <> current @>
        test <@ rebuilt.Distance.Differing = 1 @>
    | other -> failwith $"expected a box baseline, got %A{other}"

    test <@ writes = 1 @>
    test <@ IndexSeal.load (dbPathOf v.F) = Some v.Tree @>

[<Fact(Timeout = 30000)>]
let ``flag on: a checkout with its own full pass still restarts from it, and nothing is rebuilt`` () =
    let v = verified true
    editLib v

    let outcome, writes, current =
        restartFor RestartBaselines.RetainedBoxBaselines ColdScan.NotStarted v

    let own =
        PluginSteps.checkoutBaselineIn (Some v.Wiring) (dbPathOf v.F) (Some(ledgerOf v)) current

    test <@ outcome = (own |> Result.map RestartBaseline.OwnBaseline) @>
    test <@ Result.isOk outcome @>
    test <@ writes = 0 @>

[<Fact(Timeout = 30000)>]
let ``flag on: once a file has been checked this session the index is not rebuilt under the diff`` () =
    let v = boxed ()

    let outcome, writes, _ =
        restartFor RestartBaselines.RetainedBoxBaselines (ColdScan.InProgress []) v

    match outcome with
    | Error why -> test <@ why.Contains "already checked" @>
    | Ok other -> failwith $"expected no baseline, got %A{other}"

    test <@ writes = 0 @>
    test <@ IndexSeal.load (dbPathOf v.F) = None @>

[<Fact(Timeout = 30000)>]
let ``flag on: with no outcome store, or a tree that cannot be read, there is no box baseline`` () =
    let v = boxed ()

    let ask evidence root =
        PluginSteps.restartBaselineForIn
            RestartBaselines.RetainedBoxBaselines
            evidence
            root
            (dbPathOf v.F)
            (fun _ _ -> failwith "nothing may be written")
            (fun _ -> [])
            (fun _ _ _ -> Map.empty)
            ColdScan.NotStarted
            (Some(ledgerOf v))
            "sha256:current"

    test
        <@ ask None v.F.Ctx.RepoRoot = Error "the index is sealed at no tree; box baseline: no outcome store is wired" @>

    test
        <@
            ask (Some v.Wiring) (Path.Combine(v.F.Ctx.RepoRoot, "missing")) = Error
                "the index is sealed at no tree; box baseline: the tree could not be read"
        @>

    test <@ IndexSeal.load (dbPathOf v.F) = None @>

[<Fact(Timeout = 30000)>]
let ``flag on: the first build of a land holds for the fold and selects from the box baseline's diff`` () =
    let v = boxed ()
    let d = daemonOverWith RestartBaselines.RetainedBoxBaselines v.F v.Wiring

    let held, lines = firstBuild d

    test <@ held.AwaitingDiff.IsSome @>

    test
        <@
            waitLines lines
            |> List.exists (fun l ->
                l.Contains "diff-wait: holding"
                && l.Contains $"checkout-baseline tree=%s{v.Tree} record=%s{v.Key.Value}"
                && l.Contains "from=box")
        @>

    test <@ IndexSeal.load (dbPathOf v.F) = Some v.Tree @>
    update d.F (withDiff held) (BatchChecked(batch ())) |> ignore
    let launch = scheduledLaunch d.F
    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>

[<Fact(Timeout = 30000)>]
let ``flag off: the same land runs every project in full, as today`` () =
    let v = boxed ()
    let d = restart v

    let held, lines = firstBuild d

    test <@ held.AwaitingDiff.IsNone @>
    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>
    test <@ waitLines lines |> List.exists (fun l -> l.Contains "no checkout baseline") @>
    test <@ IndexSeal.load (dbPathOf v.F) = None @>

// ---------------------------------------------------------------------------
// (8) AUTOMATION-1793 step 2b: a box baseline counts as the checkout's earned baseline and
// per-project evidence, re-checked at the CURRENT K_P, and idle-exit is told.
// ---------------------------------------------------------------------------

let private repoFsproj (root: string) (project: string) =
    Path.Combine(root, "tests", project, project + ".fsproj")

/// The fixture with its test projects' `.fsproj` inside the checkout, so each one's K_P can be
/// keyed over the tree (`TestEvidenceRun.keysOf`), and `identity` naming each one's
/// compile-input identity by project.
let private inRepo (identity: Map<string, string> ref) =
    let f = fixture ()

    for project in [ "ProjA"; "ProjB" ] do
        write f.Ctx.RepoRoot $"tests/%s{project}/%s{project}.fsproj" "<Project Sdk=\"Microsoft.NET.Sdk\" />"

    let byFsproj () =
        identity.Value
        |> Map.toList
        |> List.map (fun (project, id) -> repoFsproj f.Ctx.RepoRoot project, id)
        |> Map.ofList

    { f with
        Ctx =
            { f.Ctx with
                ProjectGraph =
                    { f.Ctx.ProjectGraph with
                        ObserveProjectInputs = fun () -> Some(f.Generation.Value, byFsproj ())
                        GetAllProjects = fun () -> byFsproj () |> Map.keys |> List.ofSeq } } }

/// Each project's K_P over the tree as it is now, as the daemon keys it.
let private keysNow (v: Verified) =
    let graph = v.F.Ctx.ProjectGraph

    let identities =
        ReceiptTransitions.ObservedModel.inputs graph |> Option.defaultValue Map.empty

    TestEvidenceRun.keysOf
        v.Wiring
        v.F.Ctx.RepoRoot
        (fun fsproj -> Map.tryFind fsproj identities)
        (PluginSteps.evidenceCandidates graph configs)
    |> fst

/// A checkout that passed T in full with its projects' full passes filed box-wide at their K_P
/// over T, and T's record naming them: what a green `confirm --fresh` leaves for the box.
let private verifiedInRepo () =
    let identity = ref (Map.ofList [ "ProjA", "a1"; "ProjB", "b1" ])
    let v = verifiedOver (inRepo identity) true
    let runId = (ledgerOf v).RunId.ToString "N"

    let fullKeys =
        keysNow v
        |> Map.map (fun project key ->
            match key with
            | Ok kp ->
                let full = TestEvidenceRun.testActionKey kp "full"

                let filed =
                    v.Outcomes.Record
                        { Key = full
                          Subject = project
                          Outcome = OutcomeStore.Outcome.Pass
                          Counts = None
                          DurationMs = None
                          Producer =
                            { Binary = "test-producer"
                              Worktree = v.F.Ctx.RepoRoot
                              RunId = Some runId }
                          RecordedAt = DateTimeOffset.UtcNow }

                Assert.True(filed, "positive control: the full pass was filed")
                full.Value.Text
            | Error why -> failwith $"positive control: %s{project} has a K_P over T, not %s{why}")

    Assert.True((fullKeys.Count = 2), "positive control: both projects are keyed")

    let key =
        fileRecordWith fullKeys v.Outcomes v.F.Ctx.RepoRoot runId AnalysisRecords.analyzerIdentity

    let indexed =
        TestBaseline.appendIndex
            v.Outcomes
            { Binary = "test-producer"
              Worktree = v.F.Ctx.RepoRoot
              Now = DateTimeOffset.UtcNow }
            { Tree = v.Tree
              Baseline = key
              RunId = runId
              At = DateTimeOffset.UtcNow }

    Assert.True(indexed.IsSome, "positive control: T is named in the index")
    v, identity

/// The land: this checkout as a fresh land workspace finds it, after `edit` moved the tree:
/// no ledger at all, no whole-project evidence of its own, nothing queued, and an unsealed index.
let private asLand (v: Verified) =
    File.Delete(FullSuiteBaseline.sidecarPath v.F.Ctx.RepoRoot)
    File.Delete(WholeProjectEvidence.sidecarPath v.F.Ctx.RepoRoot)
    // The resident above queued Lib.f durably; a fresh land workspace owes nothing yet.
    File.Delete(PendingVerification.sidecarPath v.F.Ctx.RepoRoot)
    IndexSeal.save ignore (dbPathOf v.F) None

    Assert.True(
        (FullSuiteBaseline.load v.F.Ctx.RepoRoot = FullSuiteBaseline.LoadedBaseline.Loaded None),
        "positive control: the land's ledger records no full pass at all"
    )

/// A daemon's first build over `d` and the launch the fold releases.
let private holdThenLaunch (d: Daemon) =
    let held, lines = firstBuild d

    Assert.True(
        held.AwaitingDiff.IsSome,
        "the launch must hold for the fold: "
        + String.concat
            " | "
            (lines
             |> List.filter (fun l ->
                 l.Contains "diff-wait"
                 || l.Contains "box-baseline"
                 || l.Contains "Evidence gap"
                 || l.Contains "owed"))
    )

    update d.F (withDiff held) (BatchChecked(batch ())) |> ignore
    held, lines, scheduledLaunch d.F

[<Fact(Timeout = 30000)>]
let ``END-TO-END: a land with no full pass of its own selects exactly what a resident checkout that passed T selects``
    ()
    =
    let v, _ = verifiedInRepo ()
    editLib v

    // The resident: this checkout's own full pass at T, its own evidence, its index sealed at T.
    let _, residentLines, resident = holdThenLaunch (restart v)

    test
        <@
            waitLines residentLines
            |> List.exists (fun l -> l.Contains "diff-wait: holding" && not (l.Contains "from=box"))
        @>

    test <@ resident.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>

    // The land: the same bytes and edit, no ledger, no evidence, the box's T.
    asLand v
    let landing = daemonOverWith RestartBaselines.RetainedBoxBaselines v.F v.Wiring
    let held, lines, landed = holdThenLaunch landing

    test
        <@
            waitLines lines
            |> List.exists (fun l -> l.Contains "diff-wait: holding" && l.Contains "from=box")
        @>

    test <@ landed.Selection = resident.Selection @>
    test <@ landed.Symbols = resident.Symbols @>
    // (a) T's pass is the land's full-suite baseline, as an own full pass at T would be.
    test <@ held.Debt.Baseline |> Option.bind _.Tree = Some v.Tree @>
    // (b) both projects are covered by the box's evidence: none runs whole.
    test <@ (TestPruneState.coverageEvidence held).IsSome @>

[<Fact(Timeout = 30000)>]
let ``a land whose own ledger names no more than T's pass takes T's pass as its baseline, keeping its earlier trees``
    ()
    =
    let v, _ = verifiedInRepo ()
    editLib v
    asLand v
    // This land once passed another tree in full; T's pass names every project it did.
    FullSuiteBaseline.save
        v.F.Ctx.RepoRoot
        { RunId = Guid.NewGuid()
          EarnedAt = DateTime.UtcNow
          Projects = set [ "ProjA"; "ProjB" ]
          Tree = Some "sha256:elsewhere"
          EarlierTrees = [] }

    let held, _, landed =
        holdThenLaunch (daemonOverWith RestartBaselines.RetainedBoxBaselines v.F v.Wiring)

    test
        <@
            held.Debt.Baseline |> Option.map (fun b -> b.Tree, b.EarlierTrees) = Some(
                Some v.Tree,
                [ "sha256:elsewhere" ]
            )
        @>

    test <@ landed.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>

[<Fact(Timeout = 30000)>]
let ``flag off: the same land owes a full suite and runs every project in full, as today`` () =
    let v, _ = verifiedInRepo ()
    editLib v
    asLand v
    let d = restart v
    let held, _ = firstBuild d
    test <@ held.AwaitingDiff.IsNone @>
    test <@ held.Debt.Baseline = None @>
    test <@ (scheduledLaunch d.F).Selection = everyProjectInFull @>

[<Fact(Timeout = 30000)>]
let ``K_P: a project whose K_P moved outside the tree's contents is refused and runs whole, while the others narrow``
    ()
    =
    let v, identity = verifiedInRepo ()
    editLib v
    asLand v
    // ProjB's options move (a pin, a compiler option): its K_P's `options` label moves.
    identity.Value <- identity.Value |> Map.add "ProjB" "b2"
    let d = daemonOverWith RestartBaselines.RetainedBoxBaselines v.F v.Wiring
    d.F.Scheduled.Value <- None

    // The fold has already found Lib.f changed when the build lands, so the launch takes it.
    let held, lines =
        withLogLines (fun () -> update d.F (withDiff d.F.Handler.Init) (BuildCompleted BuildSucceeded))

    let adopted = lines |> List.filter (fun l -> l.Contains "box-baseline: adopted")

    test <@ adopted.Length = 1 @>
    test <@ adopted.Head.Contains "vouches for 1 project(s); refused 1" @>
    test <@ adopted.Head.Contains "ProjB (its K_P moved beyond the tree's file contents since T (options))" @>
    test <@ held.Debt.Baseline |> Option.bind _.Tree = Some v.Tree @>

    test
        <@
            (scheduledLaunch d.F).Selection = Map.ofList
                [ "ProjA", ProjectClasses(Set.singleton "ProjATests"); "ProjB", ProjectInFull ]
        @>

[<Fact(Timeout = 30000)>]
let ``idle-exit: a land a restart would rebuild from the box publishes RebuildsFromBox with T and its distance`` () =
    let v, _ = verifiedInRepo ()
    editLib v
    asLand v

    // Flag off: today's answer.
    restart v |> ignore

    match Restart.read v.F.Ctx.RepoRoot with
    | Restart.Selection.WouldRunFullSuite why ->
        test <@ why = "this checkout's ledger records no full-suite baseline" @>
    | other -> failwith $"flag off must publish today's answer, got %A{other}"

    // Flag on: the daemon publishes, at start, the box tree a restart would rebuild from.
    daemonOverWith RestartBaselines.RetainedBoxBaselines v.F v.Wiring |> ignore

    match Restart.read v.F.Ctx.RepoRoot with
    | Restart.Selection.RebuildsFromBox(tree, differing, _) ->
        test <@ tree = v.Tree @>
        test <@ differing >= 1 @>
    | other -> failwith $"expected RebuildsFromBox, got %A{other}"

[<Fact(Timeout = 30000)>]
let ``idle-exit: with no box tree to rebuild from, the flag reports a full suite and says why`` () =
    let v = boxed ()
    let ledger = Some(ledgerOf v)
    let dbPath = dbPathOf v.F

    let selection evidence root =
        PluginSteps.restartSelectionIn RestartBaselines.RetainedBoxBaselines evidence root dbPath ledger

    test
        <@
            selection None v.F.Ctx.RepoRoot = Restart.Selection.WouldRunFullSuite
                "the index is sealed at no tree; box baseline: no outcome store is wired"
        @>

    test
        <@
            selection (Some v.Wiring) (Path.Combine(v.F.Ctx.RepoRoot, "missing")) = Restart.Selection.WouldRunFullSuite
                "the index is sealed at no tree; box baseline: the tree could not be read"
        @>

    // `boxed` names T in the index; once it is gone there is nothing to rebuild from.
    Directory.Delete(v.Outcomes.DirectoryOf TestBaseline.indexKey, true)

    match selection (Some v.Wiring) v.F.Ctx.RepoRoot with
    | Restart.Selection.WouldRunFullSuite why -> test <@ why.Contains "box baseline: no retained test-baseline" @>
    | other -> failwith $"expected a full suite, got %A{other}"

    // And an own pass still answers first, flag or not.
    test
        <@
            PluginSteps.restartSelectionIn
                RestartBaselines.OwnCheckoutOnly
                (Some v.Wiring)
                v.F.Ctx.RepoRoot
                dbPath
                ledger = Restart.Selection.WouldRunFullSuite "the index is sealed at no tree"
        @>

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
// restarted daemon's first check would select its tests correctly: the tree T the index is
// sealed at, the ledger recording this checkout passing T in full, and T's `test-baseline`
// record. idle-exit's AUTO window
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

    Assert.True(
        (resealed.SealedTree |> Option.map _.Identity = Some tree'),
        "positive control: the index re-sealed at T'"
    )

    test
        <@
            published v = Restart.Selection.WouldRunFullSuite
                $"the index is sealed at %s{tree'}, not at a tree this checkout's full suite passed (the ledger's newest is %s{v.Tree}, 1 kept)"
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
    Assert.True((sealedState.SealedTree |> Option.map _.Identity = Some v.Tree), "positive control: sealed at T")
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

// --- A seal that lags the receipt tree by run-time-only paths (AUTOMATION-1682 live) ---
//
// Observed on alpha.148 in every intelligence workspace: the run's `BatchChecked` sealed the
// index at T0, then `beforeRun` (test-preflights) generated a JS bundle under a discovery root.
// No build compiles those paths, so the receipt rebinds to T1, the tree the tests run against.
// The full suite passed T1, but the seal still named T0: the ledger kept no tree, no record was
// filed, and every restart ran the full suite. The re-check that would re-seal at T1 is
// deferred until the test host completes, after the completion has already been folded.

/// A checkout sealed at T0 whose full run then passed T1, the tree after `moved` wrote into it.
let private passedAfterTheSeal (moved: string -> unit) =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    write f.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x\n"
    write f.Ctx.RepoRoot "tests/ProjA/data.json" "{ \"v\": 1 }\n"
    let outcomes, wiring = storesBeside f.Ctx.RepoRoot
    let first = daemonOver f wiring
    let sealedState = update first.F first.F.Handler.Init (BatchChecked(batch ()))
    let sealedAt = (ReceiptInputTree.read f.Ctx.RepoRoot).Value
    Assert.True((sealedState.SealedTree |> Option.map _.Identity = Some sealedAt), "positive control: sealed at T0")
    moved f.Ctx.RepoRoot
    let passedAt = (ReceiptInputTree.read f.Ctx.RepoRoot).Value
    Assert.True((passedAt <> sealedAt), "positive control: the write moved the tree")

    // The receipt binds to the tree after `beforeRun`, as `afterBeforeRun` rebinds it.
    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree f.Ctx.RepoRoot }

    let passed = update first.F sealedState (finishedGreen full)
    Assert.True(passed.Debt.Baseline.IsSome, "positive control: the full run earned the baseline")
    publish first.F sealedState passed
    f, outcomes, wiring, passed, sealedAt, passedAt

let private generateBundle (root: string) =
    write root "src/Web/wwwroot/js/app.js" "console.log('bundled');\n"

[<Fact(Timeout = 30000)>]
let ``a full pass over a tree that moved only in run-time paths since the seal records that tree and re-seals there``
    ()
    =
    let f, outcomes, wiring, passed, _, passedAt = passedAfterTheSeal generateBundle
    test <@ passed.Debt.Baseline |> Option.bind _.Tree = Some passedAt @>
    test <@ passed.SealedTree |> Option.map _.Identity = Some passedAt @>
    test <@ IndexSeal.load (dbPathOf f) = Some passedAt @>

    // With T1's record filed, a restart selects instead of running every project in full.
    let runId = passed.Debt.Baseline.Value.RunId.ToString "N"

    fileRecord outcomes f.Ctx.RepoRoot runId AnalysisRecords.analyzerIdentity
    |> ignore

    daemonOver f wiring |> ignore
    test <@ Restart.read f.Ctx.RepoRoot = Restart.Selection.SelectsCorrectly passedAt @>

[<Fact(Timeout = 30000)>]
let ``a full pass over a tree whose compile inputs moved since the seal records no tree`` () =
    // Negative control: the index's rows describe the sealed tree's sources, not the edit.
    let edit (root: string) =
        write root "src/Lib.fs" "module Lib\nlet f x = x + 0\n"

    let f, _, _, passed, sealedAt, _ = passedAfterTheSeal edit
    test <@ passed.Debt.Baseline |> Option.bind _.Tree = None @>
    test <@ passed.SealedTree |> Option.map _.Identity = Some sealedAt @>
    test <@ IndexSeal.load (dbPathOf f) = Some sealedAt @>

[<Fact(Timeout = 30000)>]
let ``a compile input that beforeRun moves keeps the launch binding, the seal and the record refused`` () =
    // Negative control for the live sequence: launch at the sealed tree T0, then `beforeRun`
    // writes the bundle AND moves a compile input. `afterBeforeRun` keeps the launch binding,
    // the tree the run completes over moved in an index input, so nothing is re-sealed and
    // the baseline record is refused as not sealed.
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    write f.Ctx.RepoRoot "src/Lib.fs" "module Lib\nlet f x = x\n"
    let _, wiring = storesBeside f.Ctx.RepoRoot
    let first = daemonOver f wiring
    let sealedState = update first.F first.F.Handler.Init (BatchChecked(batch ()))
    let launchTree = ReceiptInputTree.readTree f.Ctx.RepoRoot
    let sealedAt = launchTree.Value.Identity
    Assert.True((sealedState.SealedTree |> Option.map _.Identity = Some sealedAt), "positive control: sealed at T0")

    // What `beforeRun` wrote.
    generateBundle f.Ctx.RepoRoot
    write f.Ctx.RepoRoot "Directory.Build.props" "<Project><PropertyGroup><Moved>1</Moved></PropertyGroup></Project>\n"
    let afterSetup = ReceiptInputTree.readTree f.Ctx.RepoRoot

    let binding, _ =
        ReceiptInputTree.afterBeforeRun (ReceiptInputTree.feedsCompile f.Ctx.RepoRoot None) launchTree afterSetup

    Assert.True((binding = launchTree), "positive control: a moved compile input keeps the launch binding")

    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = binding }

    let passed, lines =
        withLogLines (fun () -> update first.F sealedState (finishedGreen full))

    test <@ passed.Debt.Baseline |> Option.bind _.Tree = None @>
    test <@ passed.SealedTree |> Option.map _.Identity = Some sealedAt @>
    test <@ IndexSeal.load (dbPathOf f) = Some sealedAt @>
    test <@ lines |> List.exists (fun l -> l.Contains "index stays sealed at") @>

    test
        <@
            lines
            |> List.exists (fun l -> l.Contains "test-baseline: not written" && l.Contains "not-sealed")
        @>

// --- What counts as an input of the index (`ReceiptInputTree.indexInput`) ---
//
// A re-seal over a moved index input binds a stale index to fresh evidence: a false green
// for selection. Each path below is one the index's analysis can read, and the model in these
// tests compiles none of them, so `feedsCompile` alone does not decide every one.

let private noModel (rel: string) =
    ReceiptInputTree.feedsCompile "/repo" (Some Set.empty) rel

let private moving (rel: string) =
    let sealedAt =
        { Identity = "sha256:t0"
          Entries = [ "src/Web/wwwroot/js/app.js", "h0"; rel, "h0" ] }

    let current =
        { Identity = "sha256:t1"
          Entries = [ "src/Web/wwwroot/js/app.js", "h1"; rel, "h1" ] }

    sealedAt, current

[<Theory(Timeout = 30000)>]
[<InlineData("Directory.Build.props")>]
[<InlineData("Directory.Packages.props")>]
[<InlineData("src/X/X.fsproj")>]
[<InlineData("src/X/X.fsproj.user")>]
[<InlineData("src/X/Lib.fs")>]
[<InlineData("src/X/Lib.fsi")>]
[<InlineData("src/X/obj/project.assets.json")>]
[<InlineData("global.json")>]
[<InlineData("lib/Vendor.dll")>]
[<InlineData("src/CSharp/Gen.cs")>]
[<InlineData("src/CSharp/CSharp.csproj")>]
[<InlineData("build/Shared.targets")>]
let ``a moved index input prevents the re-seal`` (rel: string) =
    let sealedAt, current = moving rel
    let indexInput = ReceiptInputTree.indexInput noModel Set.empty
    test <@ indexInput rel @>
    test <@ Result.isError (ReceiptInputTree.sealCarriesTo indexInput sealedAt current) @>

[<Fact(Timeout = 30000)>]
let ``a source the index recorded prevents the re-seal whatever its name`` () =
    let rel = "src/Gen/Provided.input"
    let sealedAt, current = moving rel
    test <@ not (ReceiptInputTree.indexInput noModel Set.empty rel) @>
    let indexInput = ReceiptInputTree.indexInput noModel (Set.singleton rel)
    test <@ Result.isError (ReceiptInputTree.sealCarriesTo indexInput sealedAt current) @>

[<Fact(Timeout = 30000)>]
let ``a moved bundle alone carries the seal`` () =
    let sealedAt, current = moving "src/Web/wwwroot/js/app.js.inputhash"
    let indexInput = ReceiptInputTree.indexInput noModel Set.empty
    test <@ Result.isOk (ReceiptInputTree.sealCarriesTo indexInput sealedAt current) @>
