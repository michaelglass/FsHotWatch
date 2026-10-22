/// Selection attribution: which cause put each test project in a run.
///
/// A run's selection is the union of the symbol-precise classes and every widening
/// (dependency fanout, the coarse fallback, evidence gaps, runtime coverage, outstanding
/// reds, always-classes, the full-suite scope). Before attribution they were unioned
/// before anything was reported, so a wide selection could not be told from an honest
/// one without reading the impact database by hand. These tests pin that the launch
/// names every cause per project, widest first, and that `test-scope` carries it.
module FsHotWatch.Tests.TestPruneSelectionAttributionTests

open System
open System.IO
open System.Text.Json
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

// --- the vocabulary --------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``every selection cause has a distinct token that reads back as itself`` () =
    let tokens = SelectionCause.all |> List.map SelectionCause.token
    test <@ List.distinct tokens = tokens @>

    for cause in SelectionCause.all do
        test <@ SelectionCause.tryOfToken (SelectionCause.token cause) = Some cause @>

    test <@ SelectionCause.tryOfToken "a-cause-from-another-version" = None @>

[<Fact(Timeout = 10000)>]
let ``an attribution describes each project and its causes in one log clause`` () =
    let attribution =
        [ { Project = "ProjA"
            Causes = [ SelectionCause.DependencyFanout; SelectionCause.ChangedSymbols ] }
          { Project = "ProjB"
            Causes = [ SelectionCause.AlwaysClasses ] } ]

    test
        <@
            ProjectAttribution.describe attribution = "ProjA <- dependency-fanout, changed-symbols; ProjB <- always-classes"
        @>

    let wire = JsonSerializer.Serialize(ProjectAttribution.wire attribution.Head)
    test <@ wire = """{"causes":["dependency-fanout","changed-symbols"],"project":"ProjA"}""" @>

// --- the pure attribution --------------------------------------------------------

let private sources: SelectionSources =
    { SelectionSources.none with
        DependencyFanout = Set.ofList [ "ProjA" ]
        ChangedSymbols = Set.ofList [ "ProjA"; "ProjB" ]
        AlwaysClasses = Set.ofList [ "ProjB"; "ProjC" ]
        OutstandingFailure = Set.ofList [ "ProjD" ] }

[<Fact(Timeout = 10000)>]
let ``each selected project names every source that selected it, widest first`` () =
    let byProject =
        Map.ofList [ "ProjA", []; "ProjB", [ "ProjB.SomeTests" ]; "ProjC", [ "ProjC.TreeTests" ] ]

    let selection =
        Map.ofList
            [ "ProjA", ProjectInFull
              "ProjB", ProjectClasses(Set.ofList [ "ProjB.SomeTests" ])
              "ProjC", ProjectClasses(Set.ofList [ "ProjC.TreeTests" ]) ]

    test
        <@
            SelectionAttribution.attribute sources byProject selection = [ { Project = "ProjA"
                                                                             Causes =
                                                                               [ SelectionCause.DependencyFanout
                                                                                 SelectionCause.ChangedSymbols ] }
                                                                           { Project = "ProjB"
                                                                             Causes =
                                                                               [ SelectionCause.ChangedSymbols
                                                                                 SelectionCause.AlwaysClasses ] }
                                                                           { Project = "ProjC"
                                                                             Causes = [ SelectionCause.AlwaysClasses ] } ]
        @>

[<Fact(Timeout = 10000)>]
let ``a source naming a project the run did not select attributes nothing to it`` () =
    let byProject = Map.ofList [ "ProjA", [] ]
    let selection = Map.ofList [ "ProjA", ProjectInFull ]

    let attributed =
        SelectionAttribution.attribute sources byProject selection |> List.map _.Project

    test <@ attributed = [ "ProjA" ] @>

[<Fact(Timeout = 10000)>]
let ``an empty selection that still launched names the empty selection, not a source`` () =
    let selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ]

    test
        <@
            SelectionAttribution.attribute sources Map.empty selection = [ { Project = "ProjA"
                                                                             Causes = [ SelectionCause.EmptySelection ] }
                                                                           { Project = "ProjB"
                                                                             Causes = [ SelectionCause.EmptySelection ] } ]
        @>

[<Fact(Timeout = 10000)>]
let ``every source maps to its own cause`` () =
    let all = Set.ofList [ "P" ]

    let every: SelectionSources =
        { FullSuite = all
          CoarseFallback = all
          EvidenceGap = all
          DependencyFanout = all
          RuntimeCoverage = all
          OutstandingFailure = all
          ChangedSymbols = all
          SymbolFreeEdit = all
          AlwaysClasses = all }

    let causes =
        SelectionAttribution.attribute every (Map.ofList [ "P", [] ]) (Map.ofList [ "P", ProjectInFull ])
        |> List.collect _.Causes

    test
        <@
            causes = [ SelectionCause.FullSuite
                       SelectionCause.CoarseFallback
                       SelectionCause.EvidenceGap
                       SelectionCause.DependencyFanout
                       SelectionCause.RuntimeCoverage
                       SelectionCause.OutstandingFailure
                       SelectionCause.ChangedSymbols
                       SelectionCause.SymbolFreeEdit
                       SelectionCause.AlwaysClasses ]
        @>

// --- the launch ------------------------------------------------------------------

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
    [ config "ProjA" []; config "ProjB" [ "ProjB.GuardTests" ]; config "ProjC" [] ]

let private counts: FsHotWatch.ProjectModel.Counts =
    { Discovered = 3
      Loaded = 3
      OptionsMapped = 3
      Registered = 3 }

type private Fixture =
    { Handler: PluginHandler<TestPruneState, TestPruneMsg>
      Ctx: PluginCtx<TestPruneMsg>
      Scheduled: (SharedResourceState -> Async<TestPruneMsg>) option ref }

/// A plugin over an index in which `Lib.f` is covered by `ProjA.FreshTests` only, with
/// launches captured rather than started.
let private fixture () =
    let root = isolatedRoot ()
    let dbPath = Path.Combine(root, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "ProjA" "ProjA.FreshTests" "covers f"
    let handler = create dbPath root (Some configs) None None None None []
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

let private verified (f: Fixture) =
    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB"; "ProjC" ] with
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

let private testScope (f: Fixture) state =
    let command = f.Handler.Commands |> List.find (fst >> (=) "test-scope") |> snd

    let ctx: CommandCtx<TestPruneMsg> =
        { RepoRoot = f.Ctx.RepoRoot
          Log = ignore
          Post = ignore
          EnqueueExclusiveIntent = fun _ _ _ -> Task.FromResult(())
          IsRunning = fun _ -> false
          ProjectGraph = f.Ctx.ProjectGraph }

    PluginCommand.invoke command ctx state [||] |> Async.RunSynchronously

[<Fact(Timeout = 30000)>]
let ``a filtered launch attributes the covering project to the change and the declared one to its always-classes`` () =
    let f = fixture ()
    let state = verified f

    let _, launch = buildAndLaunch f (owing "Lib.f" state)

    test
        <@
            launch.SelectedBy = [ { Project = "ProjA"
                                    Causes = [ SelectionCause.ChangedSymbols ] }
                                  { Project = "ProjB"
                                    Causes = [ SelectionCause.AlwaysClasses ] } ]
        @>

[<Fact(Timeout = 30000)>]
let ``a cold launch attributes every project to the full-suite widening first`` () =
    let f = fixture ()

    let _, launch = buildAndLaunch f (owing "Lib.f" f.Handler.Init)

    test <@ launch.SelectedBy |> List.map _.Project = [ "ProjA"; "ProjB"; "ProjC" ] @>

    test
        <@
            launch.SelectedBy
            |> List.forall (fun e -> List.head e.Causes = SelectionCause.FullSuite)
        @>

[<Fact(Timeout = 30000)>]
let ``test-scope carries the attribution of the run it grades`` () =
    let f = fixture ()
    let state = verified f
    let launched, launch = buildAndLaunch f (owing "Lib.f" state)
    let completed = update f launched (finishedGreen launch)

    use reply = JsonDocument.Parse(testScope f completed)
    let root = reply.RootElement
    let kind = root.GetProperty("kind").GetString()
    test <@ kind = "filtered" @>

    let selectedBy =
        root.GetProperty("selectedBy").EnumerateArray()
        |> Seq.map (fun e ->
            e.GetProperty("project").GetString(),
            e.GetProperty("causes").EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq)
        |> List.ofSeq

    test <@ selectedBy = [ "ProjA", [ "changed-symbols" ]; "ProjB", [ "always-classes" ] ] @>

[<Fact(Timeout = 30000)>]
let ``test-scope carries the attribution of a full run too`` () =
    let f = fixture ()
    let launched, launch = buildAndLaunch f (owing "Lib.f" f.Handler.Init)
    let completed = update f launched (finishedGreen launch)

    use reply = JsonDocument.Parse(testScope f completed)
    let root = reply.RootElement
    let kind = root.GetProperty("kind").GetString()
    let attributed = root.GetProperty("selectedBy").GetArrayLength()
    test <@ kind = "full" @>
    test <@ attributed = 3 @>
