/// `tests.projects[].checkSkipClasses`: classes a `check` leaves out of a run that widened
/// the project to whole-project for a reason the change did not cause.
///
/// Five rules, one section each:
///   1. `confirm` ignores the key and runs everything;
///   2. a class the run selected by name runs, whatever widened the project;
///   3. a widened `check` runs the project through `excludeFilterTemplate` without them;
///   4. the skip is printed and recorded in the scope, which is then not `full`;
///   5. only the widenings `checkSkipOn` lists skip them: by default the session's first
///      run and runtime-coverage debt, never a dependency fanout.
module FsHotWatch.Tests.TestPruneCheckSkipClassesTests

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

let private landTests = "ProjA.LandTests"
let private gateTests = "ProjA.GateTests"

let private skip (on: Set<SkipWidening>) : CheckSkip =
    { Classes = [ landTests; gateTests ]
      On = on
      ExcludeTemplate = "--filter-not-class {classes}" }

/// The file the project's runner appends its arguments to: a capture names WHAT ran.
let private captureOf (root: string) (project: string) = Path.Combine(root, $"ran-%s{project}")

let private config (root: string) (project: string) (checkSkip: CheckSkip option) : TestConfig =
    let runner = Path.Combine(root, $"runner-%s{project}.sh")
    File.WriteAllText(runner, $"#!/bin/sh\necho \"$@\" >> '%s{captureOf root project}'\nexit 0\n")

    { Project = project
      Command = "sh"
      Args = runner
      Group = "default"
      Environment = []
      FilterTemplate = Some "--filter-class {classes}"
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect
      Starvation = None
      CheckSkip = checkSkip
      NameHungTests = false
      AlwaysClasses = [] }

// --- the declaration -------------------------------------------------------------

let private parse (json: string) =
    use doc = JsonDocument.Parse json
    CheckSkip.parse "T" doc.RootElement

[<Fact(Timeout = 10000)>]
let ``a project with no checkSkipClasses declares no skip`` () =
    test <@ parse """{"project": "T"}""" = Ok None @>

[<Fact(Timeout = 10000)>]
let ``checkSkipClasses reads the classes, the exclude template and the default widenings`` () =
    let declared =
        parse
            """{"project": "T", "checkSkipClasses": ["A.Land", "A.Gate", "A.Land"], "excludeFilterTemplate": "-- --filter-not-class {classes}"}"""

    test
        <@
            declared = Ok(
                Some
                    { Classes = [ "A.Land"; "A.Gate" ]
                      On = SkipWidening.defaults
                      ExcludeTemplate = "-- --filter-not-class {classes}" }
            )
        @>

    test <@ SkipWidening.defaults = Set.ofList [ SkipWidening.NoSessionBaseline; SkipWidening.RuntimeCoverageDebt ] @>

[<Fact(Timeout = 10000)>]
let ``checkSkipOn names the widenings explicitly, dependency fanout included`` () =
    let declared =
        parse
            """{"project": "T", "checkSkipClasses": ["A.Land"], "checkSkipOn": ["runtime-coverage-debt", "dependency-fanout"], "excludeFilterTemplate": "--filter-not-class {classes}"}"""

    match declared with
    | Ok(Some skip) ->
        test <@ skip.On = Set.ofList [ SkipWidening.RuntimeCoverageDebt; SkipWidening.DependencyFanout ] @>
    | other -> failwith $"expected a declaration, got %A{other}"

[<Theory(Timeout = 10000)>]
// No way to express the skip: refused, never "run everything" in silence.
[<InlineData("""{"checkSkipClasses": ["A.Land"]}""", "excludeFilterTemplate")>]
[<InlineData("""{"checkSkipClasses": ["A.Land"], "excludeFilterTemplate": "--filter-not-class"}""", "{classes}")>]
[<InlineData("""{"checkSkipClasses": "A.Land", "excludeFilterTemplate": "x {classes}"}""", "checkSkipClasses")>]
[<InlineData("""{"checkSkipClasses": ["A.Land", " "], "excludeFilterTemplate": "x {classes}"}""", "checkSkipClasses")>]
[<InlineData("""{"checkSkipClasses": [], "excludeFilterTemplate": "x {classes}"}""", "checkSkipClasses")>]
[<InlineData("""{"checkSkipClasses": ["A.Land"], "checkSkipOn": ["confirm"], "excludeFilterTemplate": "x {classes}"}""",
             "confirm")>]
[<InlineData("""{"checkSkipClasses": ["A.Land"], "checkSkipOn": [], "excludeFilterTemplate": "x {classes}"}""",
             "checkSkipOn")>]
[<InlineData("""{"checkSkipOn": ["dependency-fanout"]}""", "checkSkipOn")>]
[<InlineData("""{"checkSkipClasses": ["A.Land"], "checkSkipOn": "runtime-coverage-debt", "excludeFilterTemplate": "x {classes}"}""",
             "checkSkipOn")>]
[<InlineData("""{"checkSkipClasses": ["A.Land"], "checkSkipOn": [3], "excludeFilterTemplate": "x {classes}"}""", "3")>]
[<InlineData("""{"checkSkipClasses": [3], "excludeFilterTemplate": "x {classes}"}""", "checkSkipClasses")>]
[<InlineData("""{"checkSkipClasses": ["A.Land"], "excludeFilterTemplate": 3}""", "excludeFilterTemplate")>]
let ``a declaration nobody can honour is refused, naming the key`` (json: string, named: string) =
    match parse json with
    | Error message ->
        test <@ message.Contains "tests.projects[T]" @>
        test <@ message.Contains named @>
    | Ok declared -> failwith $"expected a refusal, got %A{declared}"

[<Fact(Timeout = 15000)>]
let ``the daemon config carries the declaration, and refuses one it cannot honour`` () =
    let defaults = defaultTestConfig ()

    let parsed =
        FsHotWatch.Cli.DaemonConfig.parseConfig
            """{"tests": {"projects": [{"project": "T", "checkSkipClasses": ["A.Land"], "excludeFilterTemplate": "--filter-not-class {classes}"}]}}"""
            defaults

    test
        <@
            parsed.Tests.Value.Projects.[0].CheckSkip = Some
                { Classes = [ "A.Land" ]
                  On = SkipWidening.defaults
                  ExcludeTemplate = "--filter-not-class {classes}" }
        @>

    let ex =
        Assert.Throws<FsHotWatch.Cli.DaemonConfig.ConfigErrorException>(fun () ->
            FsHotWatch.Cli.DaemonConfig.parseConfig
                """{"tests": {"projects": [{"project": "T", "checkSkipClasses": ["A.Land"]}]}}"""
                defaults
            |> ignore)

    test <@ ex.message.Contains "tests.projects[T]" @>

// --- the pure decision -----------------------------------------------------------

let private runtime = WholeCause.Skippable SkipWidening.RuntimeCoverageDebt
let private fanout = WholeCause.Skippable SkipWidening.DependencyFanout

[<Fact(Timeout = 10000)>]
let ``a project whole only for a listed widening leaves out every class it did not name`` () =
    test <@ CheckSkip.excluded (skip SkipWidening.defaults) [ runtime ] Set.empty = [ landTests; gateTests ] @>

    test
        <@
            CheckSkip.excluded
                (skip SkipWidening.defaults)
                [ WholeCause.Skippable SkipWidening.NoSessionBaseline; runtime ]
                Set.empty = [ landTests; gateTests ]
        @>

[<Fact(Timeout = 10000)>]
let ``rule 2: a class the run named is never left out`` () =
    test <@ CheckSkip.excluded (skip SkipWidening.defaults) [ runtime ] (Set.singleton landTests) = [ gateTests ] @>

[<Fact(Timeout = 10000)>]
let ``rule 5: any reason the declaration does not list runs every class`` () =
    let unskippable = WholeCause.Unskippable "an evidence gap"
    test <@ List.isEmpty (CheckSkip.excluded (skip SkipWidening.defaults) [ fanout ] Set.empty) @>
    test <@ List.isEmpty (CheckSkip.excluded (skip SkipWidening.defaults) [ runtime; unskippable ] Set.empty) @>
    test <@ List.isEmpty (CheckSkip.excluded (skip SkipWidening.defaults) [] Set.empty) @>
    // Positive control: the same fanout skips once the project lists it.
    test
        <@
            CheckSkip.excluded (skip (Set.add SkipWidening.DependencyFanout SkipWidening.defaults)) [ fanout ] Set.empty = [ landTests
                                                                                                                             gateTests ]
        @>

// --- the launch ------------------------------------------------------------------

let private counts: FsHotWatch.ProjectModel.Counts =
    { Discovered = 2
      Loaded = 2
      OptionsMapped = 2
      Registered = 2 }

type private Fixture =
    { Root: string
      Handler: PluginHandler<TestPruneState, TestPruneMsg>
      Ctx: PluginCtx<TestPruneMsg>
      Scheduled: (SharedResourceState -> Async<TestPruneMsg>) option ref }

/// A plugin over ProjA (declaring `checkSkip`) and ProjB (declaring nothing), an index in
/// which `Lib.f` is covered by ProjA's listed `LandTests` only, and runners that record
/// their arguments.
let private fixture (checkSkip: CheckSkip option) =
    let root = isolatedRoot ()
    let dbPath = Path.Combine(root, "tp.db")
    let db = Database.create dbPath
    PendingQueueHelpers.seedCoveredSymbol db "Lib.f" "src/Lib.fs" "ProjA" landTests "covers f"
    let configs = [ config root "ProjA" checkSkip; config root "ProjB" None ]
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

    { Root = root
      Handler = handler
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

/// Both projects verified in full, so a later run is graded against evidence.
let private verified (f: Fixture) =
    let full =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L
            InputTree = ReceiptInputTree.readTree f.Root }

    let state = update f f.Handler.Init (finishedGreen full)
    Assert.True(state.Earned.IsSome, "positive control: the full run earned evidence")
    state

/// Build, launch, and RUN: the runners record what they were asked to run.
let private buildAndRun (f: Fixture) state =
    f.Scheduled.Value <- None

    for project in [ "ProjA"; "ProjB" ] do
        File.Delete(captureOf f.Root project)

    update f state (BuildCompleted BuildSucceeded) |> ignore
    Assert.True(f.Scheduled.Value.IsSome, "the build must reach a launch")

    match f.Scheduled.Value.Value Ready |> Async.RunSynchronously with
    | TestsFinished(_, completed, launch) -> completed, launch
    | other -> failwith $"expected TestsFinished, got %A{other}"

let private ran (f: Fixture) (project: string) =
    let path = captureOf f.Root project
    if File.Exists path then File.ReadAllText path else ""

let private owing (symbol: string) (state: TestPruneState) =
    { state with
        ChangedSymbols = [ symbol ]
        Debt =
            { state.Debt with
                PendingQueue = Set.singleton symbol } }

/// Runtime coverage attributes a changed file to ProjA, so ProjA is widened to whole.
let private runtimeDebt (state: TestPruneState) =
    { state with
        Debt =
            { state.Debt with
                RuntimeObligations = Map.ofList [ "src/Other.fs", Map.ofList [ "ProjA", 1L ] ] } }

let private skippedOf (launch: TestRunLaunch) (project: string) =
    launch.SelectedBy
    |> List.tryFind (fun entry -> entry.Project = project)
    |> Option.map _.SkippedClasses

// Rule 3.
[<Fact(Timeout = 30000)>]
let ``rule 3: a check widened by runtime-coverage debt runs the project without the listed classes`` () =
    let f = fixture (Some(skip SkipWidening.defaults))
    let state = verified f

    let _, launch = buildAndRun f (runtimeDebt state)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectExcept(Set.ofList [ landTests; gateTests ]) ] @>
    test <@ (ran f "ProjA").Contains $"--filter-not-class %s{landTests} %s{gateTests}" @>
    test <@ not ((ran f "ProjA").Contains "--filter-class") @>

// Rule 4.
[<Fact(Timeout = 30000)>]
let ``rule 4: the skip is recorded in the attribution, and the run's scope is not full`` () =
    let f = fixture (Some(skip SkipWidening.defaults))
    let state = verified f

    let completed, launch = buildAndRun f (runtimeDebt state)

    test <@ skippedOf launch "ProjA" = Some [ landTests; gateTests ] @>
    test <@ (ProjectAttribution.describe launch.SelectedBy).Contains $"check skipped: %s{landTests}, %s{gateTests}" @>

    // The runner was handed a filter, so the result is a filtered one.
    test <@ completed.Results |> Map.find "ProjA" |> TestResult.wasFiltered @>

    // Coverage is what the report shows ran, never the whole project, so neither the
    // run's scope nor the selection reads as full.
    let coverage =
        RunCoverage.ofRun launch.Selection completed.Results (Map.ofList [ "ProjA", Set.ofList [ "ProjA.OtherTests" ] ])

    test <@ coverage = Map.ofList [ "ProjA", CoveredClasses(Set.ofList [ "ProjA.OtherTests" ]) ] @>
    test <@ scopeOf [ "ProjA" ] coverage = ScopeFiltered(1, 1) @>
    test <@ scopeOfSelection [ "ProjA" ] launch.Selection = ScopeFiltered(1, 1) @>

    // On the wire, too: the scope's attribution names what was skipped.
    let wire =
        JsonSerializer.Serialize(
            ProjectAttribution.wire (launch.SelectedBy |> List.find (fun e -> e.Project = "ProjA"))
        )

    test <@ wire.Contains "\"skippedClasses\":[\"ProjA.LandTests\",\"ProjA.GateTests\"]" @>

// Rule 2.
[<Fact(Timeout = 30000)>]
let ``rule 2: a listed class the change selects still runs when the project is widened`` () =
    let f = fixture (Some(skip SkipWidening.defaults))
    let state = verified f

    // Positive control: selected and not widened, it runs under the class filter.
    let _, filtered = buildAndRun f (owing "Lib.f" state)
    test <@ filtered.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton landTests) ] @>
    test <@ (ran f "ProjA").Contains $"--filter-class %s{landTests}" @>

    // Widened by runtime-coverage debt as well: only the class it did not name is left out.
    let _, widened = buildAndRun f (runtimeDebt (owing "Lib.f" state))
    test <@ widened.Selection = Map.ofList [ "ProjA", ProjectExcept(Set.singleton gateTests) ] @>
    test <@ (ran f "ProjA").Contains $"--filter-not-class %s{gateTests}" @>
    test <@ not ((ran f "ProjA").Contains landTests) @>
    test <@ skippedOf widened "ProjA" = Some [ gateTests ] @>

// Rule 1.
[<Fact(Timeout = 30000)>]
let ``rule 1: confirm runs every class over the same widening a check skips them on`` () =
    let f = fixture (Some(skip SkipWidening.defaults))
    let state = verified f

    // The check skips them...
    let _, checkLaunch = buildAndRun f (runtimeDebt state)
    test <@ checkLaunch.Selection.["ProjA"] = ProjectExcept(Set.ofList [ landTests; gateTests ]) @>

    // ...and confirm, over the same debt, runs every project in full.
    let _, confirmLaunch =
        buildAndRun
            f
            { runtimeDebt state with
                Mode = PassThrough }

    test <@ confirmLaunch.Selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ] @>
    test <@ skippedOf confirmLaunch "ProjA" |> Option.exists List.isEmpty @>
    test <@ not ((ran f "ProjA").Contains "--filter-not-class") @>
    test <@ File.Exists(captureOf f.Root "ProjA") @>

// Rule 5.
[<Fact(Timeout = 30000)>]
let ``rule 5: a dependency fanout runs the listed classes unless the project lists it`` () =
    let fanoutState (state: TestPruneState) =
        { state with
            PendingForceRunProjects = Set.singleton "ProjA" }

    let byDefault = fixture (Some(skip SkipWidening.defaults))
    let _, launch = buildAndRun byDefault (fanoutState (verified byDefault))
    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull ] @>
    test <@ not ((ran byDefault "ProjA").Contains "--filter-not-class") @>

    let optedIn =
        fixture (Some(skip (Set.add SkipWidening.DependencyFanout SkipWidening.defaults)))

    let _, launch = buildAndRun optedIn (fanoutState (verified optedIn))
    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectExcept(Set.ofList [ landTests; gateTests ]) ] @>

[<Fact(Timeout = 30000)>]
let ``rule 5: a session's first run skips them, every other project running in full`` () =
    let f = fixture (Some(skip SkipWidening.defaults))
    let state = { verified f with LastResults = None }

    let _, launch = buildAndRun f state

    test
        <@
            launch.Selection = Map.ofList
                [ "ProjA", ProjectExcept(Set.ofList [ landTests; gateTests ])
                  "ProjB", ProjectInFull ]
        @>

    test <@ (ran f "ProjA").Contains "--filter-not-class" @>
    test <@ not ((ran f "ProjB").Contains "--filter") @>

[<Fact(Timeout = 30000)>]
let ``rule 5: a project with no declaration is never narrowed`` () =
    let f = fixture None
    let state = verified f

    let _, launch = buildAndRun f (runtimeDebt state)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull ] @>
    test <@ skippedOf launch "ProjA" |> Option.exists List.isEmpty @>
