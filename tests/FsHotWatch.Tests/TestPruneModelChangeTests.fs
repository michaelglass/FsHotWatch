/// A project model change after a verified run: what the next impact run launches, and
/// whether the evidence it earns can support a green.
///
/// A receipt vouches for a filtered project only through a whole-project run under the
/// CURRENT model. After the model changes, a run that selects a filtered slice of a project
/// earns a receipt that refuses; if the launch does not also run those projects in full,
/// `check` refuses every time it is asked, and a re-run that selects nothing keeps the
/// refusing receipt. These tests pin that the launch closes that gap in the same run.
module FsHotWatch.Tests.TestPruneModelChangeTests

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

let private counts: FsHotWatch.ProjectModel.Counts =
    { Discovered = 2
      Loaded = 2
      OptionsMapped = 2
      Registered = 2 }

let private config (project: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = "-c \"exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = Some "-- --filter-class {classes}"
      ClassJoin = "|"
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

let private configs = [ config "ProjA"; config "ProjB" ]

/// A plugin over a symbol index in which `Lib.f` is covered by `ProjATests` only, and a
/// ctx whose project model is whatever `generation` holds when asked. Launches are
/// captured rather than started, so a test decides when (and whether) one executes.
type private Fixture =
    { Handler: PluginHandler<TestPruneState, TestPruneMsg>
      Ctx: PluginCtx<TestPruneMsg>
      Generation: int64 ref
      Scheduled: (SharedResourceState -> Async<TestPruneMsg>) option ref }

let private fixture () =
    let root = isolatedRoot ()
    let dbPath = Path.Combine(root, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "ProjA" "ProjATests" "covers f"
    let handler = create dbPath root (Some configs) None None None None []
    let generation = ref 1L
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
                ObserveModel = fun () -> FsHotWatch.ProjectModel.ofCompleted generation.Value counts } }

    { Handler = handler
      Ctx = ctx
      Generation = generation
      Scheduled = scheduled }

let private update (f: Fixture) state event =
    f.Handler.Update f.Ctx state event |> Async.RunSynchronously

/// A completion over `selection`: a project that ran in full passed unfiltered, a
/// filtered one passed filtered, and a skipped one has no result — what a green run of
/// exactly that launch reports.
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

/// Both projects verified in full under model 1: the evidence covers each of them whole.
let private verifiedUnderModelOne (f: Fixture) =
    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L }

    let state = update f f.Handler.Init (finishedGreen full)
    let earned = state.Earned

    Assert.True(earned.IsSome, "positive control: the full run earned evidence")
    Assert.Empty(earned.Value.FailureReasons)
    state

/// Deliver a build and execute the launch it schedules, returning the launch and the
/// state the build left. The runner commands exit 0 at once; only the launch is inspected.
let private buildAndLaunch (f: Fixture) state =
    f.Scheduled.Value <- None
    let launched = update f state (BuildCompleted BuildSucceeded)
    Assert.True(f.Scheduled.Value.IsSome, "the build must reach a launch")

    match f.Scheduled.Value.Value Ready |> Async.RunSynchronously with
    | TestsFinished(_, _, launch) -> launched, launch
    | other -> failwith $"expected TestsFinished, got %A{other}"

/// `Lib.f` changed: owed, and covered by `ProjATests` alone.
let private withChangeToLibF (state: TestPruneState) =
    { state with
        ChangedSymbols = [ "Lib.f" ]
        Debt =
            { state.Debt with
                PendingQueue = Set.singleton "Lib.f" } }

[<Fact(Timeout = 30000)>]
let ``under an unchanged model a change runs only the classes that cover it`` () =
    let f = fixture ()
    let verified = verifiedUnderModelOne f

    let _, launch = buildAndLaunch f (withChangeToLibF verified)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>

[<Fact(Timeout = 30000)>]
let ``after a model change the launch runs in full every project its evidence no longer covers`` () =
    let f = fixture ()
    let verified = verifiedUnderModelOne f
    f.Generation.Value <- 2L

    let launched, launch = buildAndLaunch f (withChangeToLibF verified)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ] @>

    // The same run, completed green, earns a receipt that supports a green: nothing it
    // needed was left to a whole-project run under a replaced model.
    let settled = update f launched (finishedGreen launch)
    let earned = settled.Earned

    Assert.True(earned.IsSome, "the run under the current model earns evidence")
    test <@ earned.Value.Generation = 2L @>
    Assert.Empty(earned.Value.FailureReasons)

[<Fact(Timeout = 30000)>]
let ``after a model change a build with nothing owed still earns evidence for the new model`` () =
    let f = fixture ()
    let verified = verifiedUnderModelOne f
    f.Generation.Value <- 2L

    let launched, launch = buildAndLaunch f verified

    // Selecting nothing would keep no evidence for model 2, and every later `check` would
    // select nothing again.
    test <@ launch.ZeroSelection = ZeroSelection.NotAZero @>
    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ] @>

    let settled = update f launched (finishedGreen launch)
    Assert.Empty(settled.Earned.Value.FailureReasons)

[<Fact(Timeout = 30000)>]
let ``with no project model nothing is widened, because no evidence can be earned`` () =
    let f = fixture ()

    let noModel =
        { f with
            Ctx =
                { f.Ctx with
                    ProjectGraph = ProjectGraphAccessor.none } }

    let verified =
        update noModel noModel.Handler.Init (finishedGreen (fullSuiteLaunch [ "ProjA"; "ProjB" ]))

    let _, launch = buildAndLaunch noModel (withChangeToLibF verified)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>

/// A build that lands while the project graph is being rediscovered sees no projects and
/// fingerprints none. The fingerprints the next build compares against must still be the
/// last ones computed, or a dependency that changed across the rediscovery never fans out.
[<Fact(Timeout = 30000)>]
let ``a build over an empty graph does not erase the fingerprints the next build compares against`` () =
    let f = fixture ()
    let root = f.Ctx.RepoRoot
    let testProject = Path.Combine(root, "ProjB.fsproj")
    let library = Path.Combine(root, "Lib.fsproj")
    let libraryDll = Path.Combine(root, "Lib.dll")
    File.WriteAllText(libraryDll, "build one")
    let mutable rediscovering = false

    let graph =
        { ProjectGraphAccessor.none with
            GetAllProjects = fun () -> if rediscovering then [] else [ testProject; library ]
            GetProjectReferences = fun project -> if project = testProject then [ library ] else []
            GetCanonicalDllPath = fun project -> if project = library then Some libraryDll else None }

    // No model: the evidence gap stays out of the way, so any project run in full is the
    // dependency fanout's doing.
    let f =
        { f with
            Ctx = { f.Ctx with ProjectGraph = graph } }

    let settle state =
        let launched, launch = buildAndLaunch f state
        launch, update f launched (finishedGreen launch)

    let verified =
        update f f.Handler.Init (finishedGreen (fullSuiteLaunch [ "ProjA"; "ProjB" ]))

    // Positive control: with the graph available, an unchanged build fans out nothing.
    let quiet, afterFirst = settle verified
    test <@ Map.isEmpty quiet.Selection @>

    rediscovering <- true
    let _, afterRediscovery = settle afterFirst
    rediscovering <- false

    File.WriteAllText(libraryDll, "build two")
    let fannedOut, _ = settle afterRediscovery

    test <@ Map.tryFind "ProjB" fannedOut.Selection = Some ProjectInFull @>

/// Where the evidence of the current model already covers every project, a change that no
/// test covers runs nothing: the gap is empty, and the nothing-to-verify skip holds.
[<Fact(Timeout = 30000)>]
let ``with current evidence a change no test covers runs nothing`` () =
    let f = fixture ()

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
    let verified = verifiedUnderModelOne f

    let owed =
        { verified with
            ChangedSymbols = [ "Orphan.uncovered" ]
            Debt =
                { verified.Debt with
                    PendingQueue = Set.singleton "Orphan.uncovered" } }

    let _, launch = buildAndLaunch f owed

    test <@ Map.isEmpty launch.Selection @>

    match launch.ZeroSelection with
    | ZeroSelection.ChangesUncovered _ -> ()
    | other -> Assert.Fail($"expected the nothing-to-verify skip, got %A{other}")
