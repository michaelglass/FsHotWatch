/// `tests.projects[].alwaysClasses`: test classes every impact-filtered run includes.
///
/// Some tests read the repository's source tree as data: a convention guard that greps
/// every source file, or a census of test attributes. No symbol edge leads from the code
/// they police to them, so impact selection never picks them, and a filtered run cannot
/// see what they would catch. A project names such classes in `alwaysClasses`, and these
/// tests pin that every filtered launch carries them, without narrowing a full one.
module FsHotWatch.Tests.TestPruneAlwaysClassesTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open TestPrune.Database
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

let private config (project: string) (always: string list) : TestConfig =
    { Project = project
      Command = "sh"
      Args = "-c \"exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = Some "-- --filter-class {classes}"
      ClassJoin = "|"
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect
      Starvation = None
      AlwaysClasses = always }

let private configs =
    [ config "ProjA" [ "ProjA.TreeScanTests" ]
      config "ProjB" [ "ProjB.CensusTests"; "ProjB.GuardTests" ]
      config "ProjC" [] ]

// --- the pure widening ---------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``an empty selection stays empty, because empty already means every project in full`` () =
    test <@ AlwaysClasses.widen configs Map.empty |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``a filtered selection gains every project's always-classes`` () =
    let selected = Map.ofList [ "ProjA", [ "ProjA.FreshTests"; "ProjA.TreeScanTests" ] ]

    let widened = AlwaysClasses.widen configs selected

    test
        <@
            widened = Map.ofList
                [ "ProjA", [ "ProjA.FreshTests"; "ProjA.TreeScanTests" ]
                  "ProjB", [ "ProjB.CensusTests"; "ProjB.GuardTests" ] ]
        @>

[<Fact(Timeout = 10000)>]
let ``a project already running in full is not narrowed to its always-classes`` () =
    let selected = Map.ofList [ "ProjA", []; "ProjC", [ "ProjC.SomeTests" ] ]

    let widened = AlwaysClasses.widen configs selected

    test <@ List.isEmpty widened.["ProjA"] @>
    test <@ widened.["ProjC"] = [ "ProjC.SomeTests" ] @>
    test <@ widened.["ProjB"] = [ "ProjB.CensusTests"; "ProjB.GuardTests" ] @>

[<Fact(Timeout = 10000)>]
let ``only selects exactly the always-classes, and nothing for a project that has none`` () =
    test
        <@
            AlwaysClasses.only configs = Map.ofList
                [ "ProjA", [ "ProjA.TreeScanTests" ]
                  "ProjB", [ "ProjB.CensusTests"; "ProjB.GuardTests" ] ]
        @>

    test <@ AlwaysClasses.only [ config "ProjC" [] ] |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``the would-have-run projection carries the always-classes check would have run`` () =
    let symbolAffected = Map.ofList [ "ProjC", [ "ProjC.SomeTests" ] ]

    let selection =
        wouldHaveRunSelection configs symbolAffected Set.empty Set.empty false

    test
        <@
            selection = Map.ofList
                [ "ProjA", ProjectClasses(Set.ofList [ "ProjA.TreeScanTests" ])
                  "ProjB", ProjectClasses(Set.ofList [ "ProjB.CensusTests"; "ProjB.GuardTests" ])
                  "ProjC", ProjectClasses(Set.ofList [ "ProjC.SomeTests" ]) ]
        @>

// --- the launch ------------------------------------------------------------------

let private counts: FsHotWatch.ProjectModel.Counts =
    { Discovered = 2
      Loaded = 2
      OptionsMapped = 2
      Registered = 2 }

type private Fixture =
    { Handler: PluginHandler<TestPruneState, TestPruneMsg>
      Ctx: PluginCtx<TestPruneMsg>
      Scheduled: (SharedResourceState -> Async<TestPruneMsg>) option ref }

/// A plugin over an index in which `Lib.f` is covered by `ProjA.FreshTests` only, with
/// launches captured rather than started.
let private fixture (launchConfigs: TestConfig list) =
    let root = isolatedRoot ()
    let dbPath = Path.Combine(root, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "ProjA" "ProjA.FreshTests" "covers f"
    let handler = create dbPath root (Some launchConfigs) None None None None []
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
      Scheduled = scheduled }

let private update (f: Fixture) state event =
    f.Handler.Update f.Ctx state event |> Async.RunSynchronously

let private finishedGreen (launch: TestRunLaunch) =
    let results =
        launch.Selection
        |> Map.map (fun _ selection ->
            match selection with
            | ProjectInFull -> TestsPassed("ok", false, TimeSpan.FromSeconds 1.0)
            | ProjectClasses _ -> TestsPassed("ok", true, TimeSpan.FromSeconds 1.0))

    let runId = Guid.NewGuid()

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

    Custom(TestsFinished(started, completed, launch))

/// Every project verified in full, so a later run is graded against evidence.
let private verified (f: Fixture) (projects: string list) =
    let full =
        { fullSuiteLaunch projects with
            ModelGeneration = Some 1L }

    let state = update f f.Handler.Init (finishedGreen full)
    Assert.True(state.Earned.IsSome, "positive control: the full run earned evidence")
    state

let private buildAndLaunch (f: Fixture) state =
    f.Scheduled.Value <- None
    let launched = update f state (BuildCompleted BuildSucceeded)
    Assert.True(f.Scheduled.Value.IsSome, "the build must reach a launch")

    match f.Scheduled.Value.Value Ready |> Async.RunSynchronously with
    | TestsFinished(_, _, launch) -> launched, launch
    | other -> failwith $"expected TestsFinished, got %A{other}"

let private owing (symbol: string) (state: TestPruneState) =
    { state with
        ChangedSymbols = [ symbol ]
        Debt =
            { state.Debt with
                PendingQueue = Set.singleton symbol } }

[<Fact(Timeout = 30000)>]
let ``a change covered by one class also runs every project's always-classes`` () =
    let f = fixture configs
    let state = verified f [ "ProjA"; "ProjB"; "ProjC" ]

    let _, launch = buildAndLaunch f (owing "Lib.f" state)

    test
        <@
            launch.Selection = Map.ofList
                [ "ProjA", ProjectClasses(Set.ofList [ "ProjA.FreshTests"; "ProjA.TreeScanTests" ])
                  "ProjB", ProjectClasses(Set.ofList [ "ProjB.CensusTests"; "ProjB.GuardTests" ]) ]
        @>

/// The case the zero-skip would otherwise hide: an edit to code no test covers (say, a
/// new process kill a source guard forbids) selects nothing by symbol. The tree still
/// changed under classes that read every file of it.
[<Fact(Timeout = 30000)>]
let ``a change no test covers runs the always-classes instead of skipping`` () =
    let f = fixture configs

    let orphan: TestPrune.AstAnalyzer.SymbolInfo =
        { FullName = "Orphan.uncovered"
          Kind = TestPrune.AstAnalyzer.SymbolKind.Value
          SourceFile = "src/Orphan.fs"
          LineStart = 1
          LineEnd = 1
          ContentHash = "orphan-hash"
          IsExtern = false }

    let db = Database.create (Path.Combine(f.Ctx.RepoRoot, "tp.db"))
    db.RebuildProjects([ TestPrune.AstAnalyzer.AnalysisResult.Create([ orphan ], [], []) ])
    clearSqlitePoolForDb db
    let state = verified f [ "ProjA"; "ProjB"; "ProjC" ]

    let _, launch = buildAndLaunch f (owing "Orphan.uncovered" state)

    test
        <@
            launch.Selection = Map.ofList
                [ "ProjA", ProjectClasses(Set.ofList [ "ProjA.TreeScanTests" ])
                  "ProjB", ProjectClasses(Set.ofList [ "ProjB.CensusTests"; "ProjB.GuardTests" ]) ]
        @>

    match launch.ZeroSelection with
    | ZeroSelection.NotAZero -> ()
    | other -> Assert.Fail($"expected a launch, not a zero selection, got %A{other}")

/// Positive control for the test above: the same edit with no always-classes declared
/// still takes the nothing-to-verify skip, so the run above is the declaration's doing.
[<Fact(Timeout = 30000)>]
let ``without always-classes the same uncovered change still skips`` () =
    let plain = [ config "ProjA" []; config "ProjB" []; config "ProjC" [] ]
    let f = fixture plain

    let orphan: TestPrune.AstAnalyzer.SymbolInfo =
        { FullName = "Orphan.uncovered"
          Kind = TestPrune.AstAnalyzer.SymbolKind.Value
          SourceFile = "src/Orphan.fs"
          LineStart = 1
          LineEnd = 1
          ContentHash = "orphan-hash"
          IsExtern = false }

    let db = Database.create (Path.Combine(f.Ctx.RepoRoot, "tp.db"))
    db.RebuildProjects([ TestPrune.AstAnalyzer.AnalysisResult.Create([ orphan ], [], []) ])
    clearSqlitePoolForDb db
    let state = verified f [ "ProjA"; "ProjB"; "ProjC" ]

    let _, launch = buildAndLaunch f (owing "Orphan.uncovered" state)

    test <@ Map.isEmpty launch.Selection @>

    match launch.ZeroSelection with
    | ZeroSelection.ChangesUncovered _ -> ()
    | other -> Assert.Fail($"expected the nothing-to-verify skip, got %A{other}")
