/// A zero-affected green must stand on a passing `tests.beforeRun` over the tree it
/// answers for (AUTOMATION-1835).
///
/// The zero-affected skip returns green with zero tests run, and so without the run that
/// is the only place `tests.beforeRun` executes. Route 1 (baseline-equivalent) only skips
/// over the tree a passing run bound after its `beforeRun`. Route 2 (every changed symbol
/// has no covering test) has no such tree requirement, so a repository whose `beforeRun`
/// holds gates (a route registry, a generated-types check) could reach a green over a tree
/// those gates never saw. These tests pin that a route-2 skip runs `beforeRun` first and
/// is green only when it passes and leaves the tree the skip answers for.
module FsHotWatch.Tests.TestPruneZeroSkipWitnessTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open TestPrune.Database
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport
open FsHotWatch.Tests.TestPruneModelChangeTests

type private Fixture =
    {
        Handler: PluginHandler<TestPruneState, TestPruneMsg>
        Ctx: PluginCtx<TestPruneMsg>
        Scheduled: (SharedResourceState -> Async<TestPruneMsg>) option ref
        /// How many times the plugin's configured `beforeRun` was called.
        BeforeRuns: int ref
    }

/// A plugin over an index holding `Lib.f` (covered by `ProjATests`) and `Orphan.uncovered`
/// (covered by nothing), configured with `beforeRun` (`None` for no hook), with launches
/// captured rather than started.
let private fixture (beforeRun: (string -> unit) option) =
    let root = isolatedRoot ()
    let dbPath = Path.Combine(root, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "ProjA" "ProjATests" "covers f"

    let orphan: TestPrune.AstAnalyzer.SymbolInfo =
        { FullName = "Orphan.uncovered"
          Kind = TestPrune.AstAnalyzer.SymbolKind.Value
          Location = TestPrune.AstAnalyzer.InSource("src/Orphan.fs", 1, 1, "orphan-hash") }

    db.RebuildProjects([ TestPrune.AstAnalyzer.AnalysisResult.Create([ orphan ], [], []) ])
    clearSqlitePoolForDb db

    let beforeRuns = ref 0

    let hook =
        beforeRun
        |> Option.map (fun body ->
            fun (_: Guid) (_: FsHotWatch.HookStep.Tracker) ->
                Interlocked.Increment beforeRuns |> ignore
                body root)

    let handler = create dbPath root (Some configs) None hook None None []
    let scheduled = ref None

    let ctx: PluginCtx<TestPruneMsg> =
        { ReportStatus = ignore
          ReportErrors = fun _ _ -> ()
          ClearErrors = ignore
          ClearAllErrors = ignore
          EmitBuildCompleted = ignore
          EmitTestRunStarted = ignore
          EmitTestProgress = ignore
          EmitTestRunCompleted = ignore
          EmitCommandCompleted = ignore
          Checker = Unchecked.defaultof<_>
          RepoRoot = root
          Post = ignore
          EnqueueExclusiveIntent = fun _ _ _ -> Task.FromResult(())
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = ignore
          Log = ignore
          CompleteWithTimeout = ignore
          RunExclusive = fun _ _ -> Claimed
          Processes = None
          RunExclusiveShared =
            fun _ _ work _ _ ->
                scheduled.Value <- Some work
                SharedClaimed
          SlotHolder = fun _ -> SlotHolder.Free
          DeclareBoundedWork = BoundedWork.undeclared
          FcsSuppressedCodes = Set.empty
          ProjectGraph =
            { ProjectGraphAccessor.none with
                ObserveModel = fun () -> FsHotWatch.ProjectModel.ofCompleted 1L counts } }

    { Handler = handler
      Ctx = ctx
      Scheduled = scheduled
      BeforeRuns = beforeRuns }

let private update (f: Fixture) state event =
    f.Handler.Update f.Ctx state event |> Async.RunSynchronously

/// Deliver a build and execute the launch it schedules: the completion and the launch.
let private buildAndRun (f: Fixture) state =
    f.Scheduled.Value <- None
    let launched = update f state (BuildCompleted BuildSucceeded)
    Assert.True(f.Scheduled.Value.IsSome, "the build must reach a launch")

    match f.Scheduled.Value.Value Ready |> Async.RunSynchronously with
    | TestsFinished(_, completed, launch) -> launched, completed, launch
    | other -> failwith $"expected TestsFinished, got %A{other}"

/// Both projects verified in full under model 1, by a completion the test hands in (the
/// hook is not called for it), so a later launch is graded against evidence.
let private verified (f: Fixture) =
    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L }

    let state = update f f.Handler.Init (finishedGreen full)
    Assert.True(state.Earned.IsSome, "positive control: the full run earned evidence")
    state

/// `Orphan.uncovered` changed: owed, and covered by no test, so route 2 applies.
let private owingUncovered (state: TestPruneState) =
    { state with
        ChangedSymbols = [ "Orphan.uncovered" ]
        Debt =
            { state.Debt with
                PendingQueue = Set.singleton "Orphan.uncovered" } }

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

// --- route 2 (nothing to verify) --------------------------------------------------

/// Positive control for the two tests below: with no hook configured, route 2 skips green
/// exactly as it always has.
[<Fact(Timeout = 30000)>]
let ``with no beforeRun configured an uncovered change still skips green`` () =
    let f = fixture None
    write f.Ctx.RepoRoot "src/Orphan.fs" "module Orphan\n\nlet uncovered = 1\n"

    let _, completed, launch = buildAndRun f (owingUncovered (verified f))

    test <@ completed.Outcome = Normal @>
    test <@ Map.isEmpty launch.Selection @>

    match launch.ZeroSelection with
    | ZeroSelection.ChangesUncovered _ -> ()
    | other -> Assert.Fail($"expected the nothing-to-verify skip, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``an uncovered change with a failing beforeRun is not green`` () =
    let f = fixture (Some(fun _ -> failwith "csrf-gate failed"))
    write f.Ctx.RepoRoot "src/Orphan.fs" "module Orphan\n\nlet uncovered = 1\n"

    let _, completed, _ = buildAndRun f (owingUncovered (verified f))

    test <@ f.BeforeRuns.Value = 1 @>

    match completed.Outcome with
    | Aborted reason -> test <@ reason.Contains "csrf-gate failed" @>
    | Normal -> Assert.Fail "a zero-affected skip went green without the failing beforeRun having run"

[<Fact(Timeout = 30000)>]
let ``an uncovered change runs a passing beforeRun before skipping green over the tree it left`` () =
    let f = fixture (Some ignore)
    write f.Ctx.RepoRoot "src/Orphan.fs" "module Orphan\n\nlet uncovered = 1\n"

    let _, completed, launch = buildAndRun f (owingUncovered (verified f))

    test <@ f.BeforeRuns.Value = 1 @>
    test <@ completed.Outcome = Normal @>
    test <@ Map.isEmpty launch.Selection @>
    test <@ launch.InputTreeHash = ReceiptInputTree.read f.Ctx.RepoRoot @>

    match launch.ZeroSelection with
    | ZeroSelection.ChangesUncovered _ -> ()
    | other -> Assert.Fail($"expected the nothing-to-verify skip, got %A{other}")

/// A `beforeRun` that rewrites a compile input (a generated types file) leaves a tree the
/// symbol diff never saw, so "every changed symbol is uncovered" no longer describes it:
/// the change runs instead of skipping.
[<Fact(Timeout = 30000)>]
let ``an uncovered change whose beforeRun moves a compile input runs instead of skipping`` () =
    let generation = ref 0

    let regenerate root =
        generation.Value <- generation.Value + 1
        write root "src/Generated.fs" $"module Generated\n\nlet value = %d{generation.Value}\n"

    let f = fixture (Some regenerate)
    write f.Ctx.RepoRoot "src/Orphan.fs" "module Orphan\n\nlet uncovered = 1\n"

    let _, _, launch = buildAndRun f (owingUncovered (verified f))

    test <@ launch.ZeroSelection = ZeroSelection.NotAZero @>
    test <@ f.BeforeRuns.Value >= 1 @>

let private untracked: FsHotWatch.HookStep.Tracker =
    fun _ ->
        { new IDisposable with
            member _.Dispose() = () }

// --- route 1 (baseline-equivalent) ------------------------------------------------

/// Route 1 already stands on a passing `beforeRun`: the receipt whose tree it matched was
/// earned with one. It skips as it always has, and runs no hook of its own.
[<Fact(Timeout = 30000)>]
let ``an unchanged tree after a run that passed beforeRun skips without running it again`` () =
    let f = fixture (Some ignore)
    let root = f.Ctx.RepoRoot
    write root "src/Lib.fs" "module Lib\n\nlet f x = x\n"

    // The witness of a run whose own beforeRun passed over this tree.
    let settled =
        BeforeRunWitness.settle
            (BeforeRunHook.Configured(fun _ _ -> ()))
            (Guid.NewGuid())
            untracked
            (fun () _ -> true)
            (ReceiptInputTree.readTree root)
            (fun () -> ReceiptInputTree.readTree root)

    let witnessed =
        match settled with
        | Settlement.Settled witnessed -> witnessed
        | Settlement.Unsettled _ -> failwith "positive control: an unmoved tree settles"

    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = witnessed.Tree
            BeforeRun = Some witnessed.Witness }

    let ran = update f f.Handler.Init (finishedGreen full)

    test <@ (TestRunInputs.ofState ran).EvidenceWitness = Some witnessed.Witness @>

    let _, completed, launch = buildAndRun f ran

    test <@ f.BeforeRuns.Value = 0 @>
    test <@ completed.Outcome = Normal @>
    test <@ launch.ZeroSelection = ZeroSelection.AlreadyVerified @>
    test <@ launch.BeforeRun = Some witnessed.Witness @>

// --- the witness ------------------------------------------------------------------

let private treeWith (rel: string) =
    let root = isolatedRoot ()
    write root rel "module Lib\n\nlet f x = x\n"
    root, ReceiptInputTree.readTree root

let private settleWith (hook: BeforeRunHook) (root: string) (launchTree: ReceiptTree option) =
    BeforeRunWitness.settle hook (Guid.NewGuid()) untracked (fun () rel -> rel.EndsWith ".fs") launchTree (fun () ->
        ReceiptInputTree.readTree root)

[<Fact(Timeout = 10000)>]
let ``with no hook configured settle runs nothing and its witness covers any tree`` () =
    let root, tree = treeWith "src/Lib.fs"

    match settleWith BeforeRunHook.NotConfigured root tree with
    | Settlement.Settled witnessed ->
        test <@ witnessed.Tree = tree @>
        test <@ BeforeRunWitness.covers (Some "any tree") witnessed.Witness @>
        test <@ BeforeRunWitness.covers None witnessed.Witness @>
    | Settlement.Unsettled _ -> Assert.Fail "with no hook the launch tree settles"

[<Fact(Timeout = 10000)>]
let ``a passing hook's witness covers exactly the tree it left`` () =
    let root, tree = treeWith "src/Lib.fs"
    let calls = ref 0

    let hook =
        BeforeRunHook.Configured(fun _ _ -> Interlocked.Increment calls |> ignore)

    match settleWith hook root tree with
    | Settlement.Settled witnessed ->
        test <@ calls.Value = 1 @>
        test <@ witnessed.Tree = tree @>
        test <@ BeforeRunWitness.covers (tree |> Option.map _.Identity) witnessed.Witness @>
        test <@ not (BeforeRunWitness.covers (Some "another tree") witnessed.Witness) @>
        test <@ not (BeforeRunWitness.covers None witnessed.Witness) @>
    | Settlement.Unsettled _ -> Assert.Fail "a hook that moved nothing settles"

[<Fact(Timeout = 10000)>]
let ``a failing hook yields no witness`` () =
    let root, tree = treeWith "src/Lib.fs"
    let hook = BeforeRunHook.Configured(fun _ _ -> failwith "csrf-gate failed")

    let thrown = Assert.ThrowsAny<exn>(fun () -> settleWith hook root tree |> ignore)
    test <@ thrown.Message.Contains "csrf-gate failed" @>

[<Fact(Timeout = 10000)>]
let ``a hook that moves a compile input yields no witness and keeps the launch binding`` () =
    let root, tree = treeWith "src/Lib.fs"

    let hook =
        BeforeRunHook.Configured(fun _ _ -> write root "src/Generated.fs" "module Generated\n")

    match settleWith hook root tree with
    | Settlement.Unsettled binding ->
        test <@ binding = tree @>
        test <@ Settlement.witness (Settlement.Unsettled binding) = None @>
    | Settlement.Settled _ -> Assert.Fail "a moved compile input must not settle"

[<Fact(Timeout = 10000)>]
let ``a hook that moves only what no build compiles vouches for the tree it left`` () =
    let root, tree = treeWith "src/Lib.fs"

    let hook =
        BeforeRunHook.Configured(fun _ _ -> write root "src/site.css" "body {}\n")

    match settleWith hook root tree with
    | Settlement.Settled witnessed ->
        let after = ReceiptInputTree.readTree root
        test <@ witnessed.Tree = after @>
        test <@ after <> tree @>
        test <@ BeforeRunWitness.covers (after |> Option.map _.Identity) witnessed.Witness @>
        test <@ not (BeforeRunWitness.covers (tree |> Option.map _.Identity) witnessed.Witness) @>
    | Settlement.Unsettled _ -> Assert.Fail "a hook that moved no compile input settles"

[<Fact(Timeout = 10000)>]
let ``a held witness is a no-hook config's, or a passing hook's over this very tree`` () =
    let root, tree = treeWith "src/Lib.fs"
    let _, other = treeWith "src/Other.fs"
    let hook = BeforeRunHook.Configured(fun _ _ -> ())

    let passed =
        match settleWith hook root tree with
        | Settlement.Settled witnessed -> witnessed.Witness
        | Settlement.Unsettled _ -> failwith "positive control: an unmoved tree settles"

    // No hook configured: every tree is held, whatever a receipt says.
    let noHook = BeforeRunWitness.held BeforeRunHook.NotConfigured None other
    test <@ noHook |> Option.map _.Witness = Some noBeforeRunWitness @>
    test <@ noHook |> Option.map _.Tree = Some other @>

    // A hook configured: only a passing run's witness over this very tree.
    test <@ (BeforeRunWitness.held hook (Some passed) tree |> Option.map _.Witness) = Some passed @>
    test <@ BeforeRunWitness.held hook (Some passed) other |> Option.isNone @>
    test <@ BeforeRunWitness.held hook None tree |> Option.isNone @>
    // A receipt earned while no hook was configured vouches for no hook that is.
    test <@ BeforeRunWitness.held hook (Some noBeforeRunWitness) tree |> Option.isNone @>

// --- the receipt ------------------------------------------------------------------

let private classifyRun (tree: ReceiptTree option) (witness: BeforeRunWitness option) =
    let launch =
        { fullSuiteLaunch [ "ProjA" ] with
            InputTree = tree
            BeforeRun = witness }

    let results =
        Map.ofList [ "ProjA", TestsPassed("ok", false, TimeSpan.FromSeconds 1.0) ]

    let completed: TestRunCompleted =
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

    ReceiptTransition.classify
        [ "ProjA" ]
        None
        None
        (tree |> Option.map _.Identity)
        None
        None
        None
        launch
        completed
        Map.empty

[<Fact(Timeout = 10000)>]
let ``a receipt is earned only with a witness that covers the tree it binds`` () =
    let _, tree = treeWith "src/Lib.fs"
    let otherRoot, other = treeWith "src/Other.fs"

    let passedOnOther =
        match settleWith (BeforeRunHook.Configured(fun _ _ -> ())) otherRoot other with
        | Settlement.Settled witnessed -> witnessed.Witness
        | Settlement.Unsettled _ -> failwith "positive control: an unmoved tree settles"

    // Positive control: the same run with the witness of its own settlement is earned.
    match classifyRun tree (Some noBeforeRunWitness) with
    | ReceiptTransition.Earned receipt -> test <@ receipt.BeforeRun = noBeforeRunWitness @>
    | other -> Assert.Fail($"expected Earned, got %A{other}")

    match classifyRun tree None with
    | ReceiptTransition.Revoked reason -> test <@ reason.Contains "no passing beforeRun" @>
    | other -> Assert.Fail($"a run nothing vouches for earned a receipt: %A{other}")

    match classifyRun tree (Some passedOnOther) with
    | ReceiptTransition.Revoked reason -> test <@ reason.Contains "no passing beforeRun" @>
    | other -> Assert.Fail($"a witness of another tree earned a receipt: %A{other}")
