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
open TestPrune.AstAnalyzer
open TestPrune.Database
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginRuns
open FsHotWatch.TestPrune.PluginFold
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.PluginStartup
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.PluginQueries
open FsHotWatch.TestPrune.OutstandingFailures
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.ReceiptTransitions
open FsHotWatch.TestPrune.RunLaunch
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginTypes
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
            Causes = [ SelectionCause.DependencyFanout; SelectionCause.ChangedSymbols ]
            SkippedClasses = [] }
          { Project = "ProjB"
            Causes = [ SelectionCause.AlwaysClasses ]
            SkippedClasses = [] } ]

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
                                                                                 SelectionCause.ChangedSymbols ]
                                                                             SkippedClasses = [] }
                                                                           { Project = "ProjB"
                                                                             Causes =
                                                                               [ SelectionCause.ChangedSymbols
                                                                                 SelectionCause.AlwaysClasses ]
                                                                             SkippedClasses = [] }
                                                                           { Project = "ProjC"
                                                                             Causes = [ SelectionCause.AlwaysClasses ]
                                                                             SkippedClasses = [] } ]
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
                                                                             Causes = [ SelectionCause.EmptySelection ]
                                                                             SkippedClasses = [] }
                                                                           { Project = "ProjB"
                                                                             Causes = [ SelectionCause.EmptySelection ]
                                                                             SkippedClasses = [] } ]
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
          AlwaysClasses = all
          CompositionRootRestored = all }

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
                       SelectionCause.CompositionRootRestored
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
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
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
let private fixtureWithTraces (queriesOf: (Database -> ImpactQueries) option) (traces: TraceWiring option) =
    let root = isolatedRoot ()
    let dbPath = Path.Combine(root, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "ProjA" "ProjA.FreshTests" "covers f"

    let handler =
        match queriesOf, traces with
        | None, None -> create dbPath root (Some configs) None None None None []
        | queriesOf, traces ->
            let queriesOf = queriesOf |> Option.defaultValue ImpactQueries.ofDatabase

            createWithQueries
                queriesOf
                (TimeSpan.FromMinutes 5.0)
                None
                (fun () -> Map.empty)
                dbPath
                TestPrune.Domain.WalkPolicy.FollowAll
                root
                (Some configs)
                None
                None
                None
                None
                []
                traces
                TestMode.initial

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
      Scheduled = scheduled }

let private fixtureWith (queriesOf: (Database -> ImpactQueries) option) = fixtureWithTraces queriesOf None

let private fixture () = fixtureWith None

let private update (f: Fixture) state event =
    f.Handler.Update f.Ctx state event |> Async.RunSynchronously

let private finishedGreen (launch: TestRunLaunch) =
    let results =
        launch.Selection
        |> Map.map (fun _ selection ->
            match selection with
            | ProjectInFull -> TestsPassed("ok", false, TimeSpan.FromSeconds 1.0)
            | ProjectClasses _
            | ProjectExcept _ -> TestsPassed("ok", true, TimeSpan.FromSeconds 1.0))

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
                                    Causes = [ SelectionCause.ChangedSymbols ]
                                    SkippedClasses = [] }
                                  { Project = "ProjB"
                                    Causes = [ SelectionCause.AlwaysClasses ]
                                    SkippedClasses = [] } ]
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

// --- what a launch is priced at, carried to the verdict's reply ------------------

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a launch is priced from the recorded runs, and a project none recorded is named`` () =
    let root = isolatedRoot ()

    let priced = pricedSelectionIn root (fullSuiteLaunch [ "ProjA" ])

    test
        <@
            priced.Price = Some
                { SelectionPrice.Reading.EstimateMs = 0.0
                  SelectionPrice.Reading.PricedClasses = 0
                  SelectionPrice.Reading.UnpricedClasses = 0
                  SelectionPrice.Reading.UnpricedProjects = [ "ProjA" ] }
        @>

[<Fact(Timeout = 30000)>]
let ``test-scope carries the price of the run it grades`` () =
    let f = fixture ()
    let launched, launch = buildAndLaunch f (owing "Lib.f" (verified f))

    test
        <@
            launch.Price
            |> Option.exists (fun p -> p.UnpricedProjects = [ "ProjA"; "ProjB" ])
        @>

    let completed = update f launched (finishedGreen launch)

    use reply = JsonDocument.Parse(testScope f completed)
    let price = reply.RootElement.GetProperty("price")

    let unpriced =
        price.GetProperty("unpricedProjects").EnumerateArray()
        |> Seq.map _.GetString()
        |> List.ofSeq

    test <@ unpriced = [ "ProjA"; "ProjB" ] @>
    let estimate = price.GetProperty("estimateMs").GetDouble()
    test <@ estimate = 0.0 @>

// --- the top seeds by fanout -----------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``a wide selection names its seeds by the tests each alone selects, widest first, at most five`` () =
    let alone =
        Map.ofList [ "a", 10; "b", 700; "c", 700; "d", 1; "e", 40; "f", 90; "g", 5 ]

    let reading =
        SeedFanoutReading.ofSelection
            WideSelectionTests
            MaxSeedsToAttribute
            800
            (Map.keys alone |> List.ofSeq)
            (fun s -> alone.[s])

    test
        <@
            reading = SeedFanoutReading.Top
                [ { Seed = "b"; Tests = 700 }
                  { Seed = "c"; Tests = 700 }
                  { Seed = "f"; Tests = 90 }
                  { Seed = "e"; Tests = 40 }
                  { Seed = "a"; Tests = 10 } ]
        @>

[<Fact(Timeout = 10000)>]
let ``a selection too narrow to attribute, or with too many seeds, says so and walks no seed`` () =
    let walked = ref 0

    let count (_: string) : int =
        walked.Value <- walked.Value + 1
        1

    test
        <@
            SeedFanoutReading.ofSelection WideSelectionTests MaxSeedsToAttribute WideSelectionTests [ "a" ] count = SeedFanoutReading.Narrow
        @>

    let many = [ for i in 0..MaxSeedsToAttribute -> $"s%d{i}" ]

    test
        <@
            SeedFanoutReading.ofSelection WideSelectionTests MaxSeedsToAttribute (WideSelectionTests + 1) many count = SeedFanoutReading
                .OverBudget(MaxSeedsToAttribute + 1)
        @>

    test <@ walked.Value = 0 @>

[<Fact(Timeout = 10000)>]
let ``every fanout reading has a wire kind`` () =
    let kinds =
        [ SeedFanoutReading.NoSelection
          SeedFanoutReading.Narrow
          SeedFanoutReading.OverBudget 3
          SeedFanoutReading.Top [ { Seed = "Lib.hub"; Tests = 9 } ] ]
        |> List.map (fun r -> JsonSerializer.Serialize(SeedFanoutReading.wire r))

    test
        <@
            kinds = [ """{"kind":"no-selection"}"""
                      """{"kind":"narrow"}"""
                      """{"kind":"over-budget","seeds":3}"""
                      """{"kind":"top","seeds":[{"seed":"Lib.hub","tests":9}]}""" ]
        @>

/// The index's `Lib.f` selects `count` tests in `ProjA`, one class each.
let private selecting (count: int) (db: Database) : ImpactQueries =
    { ImpactQueries.ofDatabase db with
        AffectedTests =
            fun _ ->
                [ for i in 1..count ->
                      { SymbolFullName = $"ProjA.T%d{i}.t"
                        TestProject = "ProjA"
                        TestClass = $"ProjA.T%d{i}"
                        TestMethod = "t" } ]
                |> TestPrune.AstAnalyzer.AffectedSelection.ofTests }

[<Fact(Timeout = 30000)>]
let ``a wide launch carries its top seeds by fanout, and test-scope sends them`` () =
    let f = fixtureWith (Some(selecting (WideSelectionTests + 100)))
    let state = verified f
    let launched, launch = buildAndLaunch f (owing "Lib.f" state)

    test
        <@
            launch.SeedFanout = SeedFanoutReading.Top
                [ { Seed = "Lib.f"
                    Tests = WideSelectionTests + 100 } ]
        @>

    let completed = update f launched (finishedGreen launch)
    use reply = JsonDocument.Parse(testScope f completed)
    let fanout = reply.RootElement.GetProperty("seedFanout")
    let kind = fanout.GetProperty("kind").GetString()
    let topSeed = fanout.GetProperty("seeds").[0].GetProperty("seed").GetString()
    test <@ kind = "top" @>
    test <@ topSeed = "Lib.f" @>

[<Fact(Timeout = 30000)>]
let ``a narrow launch says its fanout was not measured`` () =
    let f = fixture ()
    let state = verified f
    let _, launch = buildAndLaunch f (owing "Lib.f" state)
    test <@ launch.SeedFanout = SeedFanoutReading.Narrow @>

// --- shadow trace selection never changes a launch (AUTOMATION-975) -------------------

/// `tests.traces.select: "shadow"`, with a store at `.fshw/test-traces.db`.
let private shadowWiring: TraceWiring =
    { Policy =
        { Record = RecordEveryRun
          DbPath = ".fshw/test-traces.db"
          WeaveTests = WeaveTestSites
          FingerprintInputs = []
          FingerprintEnv = []
          VerifyTimeoutSec = 60
          Select = SelectShadow
          ShadowDbPath = Some ".fshw/shadow.db" }
      OptedOut = Set.empty
      WeaveOverrides = Map.empty
      Decide = fun _ _ _ _ _ -> Untraced None
      PredictFingerprint = fun _ _ _ -> Ok "E" }

/// A recorded run of ProjA whose complete trace of `ProjA.FreshTests.covers f` executed
/// nothing: the trace selection leaves the test out, where the static walk from `Lib.f`
/// selects it.
let private recordQuietTrace (root: string) =
    use store =
        TestPrune.Trace.TraceStore.Store.Open(Path.Combine(root, ".fshw", "test-traces.db"))

    store.RecordRun(
        { RunId = "r1"
          TestProject = "ProjA"
          TreeHash = "tree"
          EnvFingerprint = "E"
          RecordedAt = DateTimeOffset.UtcNow
          Kind = TestPrune.Trace.TraceStore.FullRun
          Status = TestPrune.Trace.TraceStore.Recorded
          Reason = ""
          StatsJson = "{}" },
        [ { Key = "T:1"
            Symbols = []
            Inputs = [] } ],
        [ { TestKey = TestPrune.Trace.TraceIngest.testKey "ProjA" "ProjA.FreshTests" "covers f"
            Status = Some TestPrune.Trace.Model.Passed
            Reasons = []
            ScopeKeys = [ "T:1" ] } ]
    )

    // Lib.f is probed: a change to it is one traces can verify, so R6 does not walk it.
    store.RecordSymbolFacts("ProjA", "E", TestPrune.Trace.TraceStore.ProbedFact, [ "Lib.f" ])

[<Fact(Timeout = 30000)>]
let ``an impact launch selects the same tests with shadow trace selection on as with it off`` () =
    let plain = fixture ()
    let _, without = buildAndLaunch plain (owing "Lib.f" (verified plain))

    let shadowed = fixtureWithTraces None (Some shadowWiring)
    recordQuietTrace shadowed.Ctx.RepoRoot
    let _, withShadow = buildAndLaunch shadowed (owing "Lib.f" (verified shadowed))

    // Positive control: shadow ran, and disagreed with the static selection.
    let reading = withShadow.TraceShadow |> List.exactlyOne

    test
        <@
            reading.Comparison.StaticOnly = set
                [ TestPrune.Trace.TraceIngest.testKey "ProjA" "ProjA.FreshTests" "covers f" ]
        @>

    // The pin: the launch is the one it would have been without shadow.
    test <@ withShadow.Selection = without.Selection @>
    test <@ withShadow.SelectedBy = without.SelectedBy @>
    test <@ without.TraceShadow.IsEmpty @>

// --- the composition-root fail-safe ----------------------------------------------

/// `App.handler` reaches `ProjA` directly and through the fixture boot, and `ProjC` only
/// through the boot. With the marker, the barrier narrows `ProjA` to `ProjA.Direct` and
/// empties `ProjC`, so the fail-safe restores `ProjC` whole; without it nothing is cut.
let private indexRestoringGraph (f: Fixture) (marked: bool) =
    let symbol name file : TestPrune.AstAnalyzer.SymbolInfo =
        { FullName = name
          Kind = TestPrune.AstAnalyzer.Function
          Location = TestPrune.AstAnalyzer.InSource(file, 1, 5, "") }

    let edge from target : TestPrune.AstAnalyzer.Dependency =
        { FromSymbol = from
          ToSymbol = target
          Kind = TestPrune.AstAnalyzer.Calls
          Source = "core" }

    let testIn project cls : TestPrune.AstAnalyzer.TestMethodInfo =
        { SymbolFullName = $"%s{cls}.t"
          TestProject = project
          TestClass = cls
          TestMethod = "t" }

    let analysis: TestPrune.AstAnalyzer.AnalysisResult =
        { Symbols =
            [ symbol "App.handler" "src/App.fs"
              symbol "App.routes" "src/App.fs"
              symbol "Fixture.boot" "tests/Fixture.fs"
              symbol "ProjA.Direct.t" "tests/ProjA.fs"
              symbol "ProjA.Booted.t" "tests/ProjA.fs"
              symbol "ProjC.Booted.t" "tests/ProjC.fs" ]
          Dependencies =
            [ edge "App.routes" "App.handler"
              edge "Fixture.boot" "App.routes"
              edge "ProjA.Direct.t" "App.handler"
              edge "ProjA.Booted.t" "Fixture.boot"
              edge "ProjC.Booted.t" "Fixture.boot" ]
          TestMethods =
            [ testIn "ProjA" "ProjA.Direct"
              testIn "ProjA" "ProjA.Booted"
              testIn "ProjC" "ProjC.Booted" ]
          Attributes =
            if marked then
                [ { SymbolFullName = "App.routes"
                    AttributeName = "CompositionRootAttribute"
                    ArgsJson = "[]" } ]
            else
                []
          ParentLinks = []
          Diagnostics = TestPrune.AstAnalyzer.AnalysisDiagnostics.Zero }

    let db = Database.create (Path.Combine(f.Ctx.RepoRoot, "tp.db"))
    db.RebuildProjects([ analysis ])

let private causesOnTheWire (reply: string) (project: string) =
    let scope =
        reply
        |> FsHotWatch.Cli.IpcParsing.parseTestRunReport
        |> VerdictFixtures.scopeOnTheWire

    scope.["selectedBy"].AsArray()
    |> Seq.find (fun e -> e.["project"].GetValue<string>() = project)
    |> fun e -> e.["causes"].AsArray() |> Seq.map (fun c -> c.GetValue<string>()) |> List.ofSeq

[<Fact(Timeout = 30000)>]
let ``a project the barrier emptied is attributed to the fail-safe in the verdict, and one it narrowed is not`` () =
    let f = fixture ()
    let state = verified f
    indexRestoringGraph f true

    let launched, launch = buildAndLaunch f (owing "App.handler" state)

    test
        <@
            launch.SelectedBy = [ { Project = "ProjA"
                                    Causes = [ SelectionCause.ChangedSymbols ]
                                    SkippedClasses = [] }
                                  { Project = "ProjB"
                                    Causes = [ SelectionCause.AlwaysClasses ]
                                    SkippedClasses = [] }
                                  { Project = "ProjC"
                                    Causes = [ SelectionCause.CompositionRootRestored; SelectionCause.ChangedSymbols ]
                                    SkippedClasses = [] } ]
        @>

    // The barrier really narrowed ProjA: what it cut is out of the run.
    test <@ launch.Selection["ProjA"] = ProjectClasses(Set.singleton "ProjA.Direct") @>
    test <@ launch.Selection["ProjC"] = ProjectClasses(Set.singleton "ProjC.Booted") @>

    let reply = testScope f (update f launched (finishedGreen launch))
    test <@ causesOnTheWire reply "ProjC" = [ "composition-root-restored"; "changed-symbols" ] @>
    test <@ causesOnTheWire reply "ProjA" = [ "changed-symbols" ] @>

[<Fact(Timeout = 30000)>]
let ``an unbarriered selection never reports the fail-safe`` () =
    let f = fixture ()
    let state = verified f
    indexRestoringGraph f false

    let launched, launch = buildAndLaunch f (owing "App.handler" state)

    // Positive control: without the marker the same seed still selects both projects, so
    // the absence below is about the barrier and not about a launch that selects nothing.
    test
        <@
            launch.SelectedBy
            |> List.filter (fun e -> e.Project <> "ProjB")
            |> List.map (fun e -> e.Project, e.Causes) = [ "ProjA", [ SelectionCause.ChangedSymbols ]
                                                           "ProjC", [ SelectionCause.ChangedSymbols ] ]
        @>

    let reply = testScope f (update f launched (finishedGreen launch))
    test <@ not (reply.Contains "composition-root-restored") @>
