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
      ReportVerificationFormat = AutoDetect
      Starvation = None
      CheckSkip = None
      AlwaysClasses = [] }

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

    // Bound to the tree, as a real launch is: a receipt over no tree is no evidence
    // for it, and the next build would run every project to earn some.
    let verified =
        update
            f
            f.Handler.Init
            (finishedGreen
                { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
                    InputTree = ReceiptInputTree.readTree root })

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

// ---------------------------------------------------------------------------
// Whole-project evidence across a daemon restart (AUTOMATION-1320). A new daemon used to
// hold no evidence at all, so every test project it had not yet seen run whole under its
// own model ran in full: "Evidence gap: 7 project(s) have no whole-project run under the
// current project model", logged by each cold land daemon. The evidence is keyed on each
// project's compile-input identity (`ProjectModel.ProjectInputs`), which does not depend
// on any one daemon's model generation, so a record of it can outlive the daemon.
// ---------------------------------------------------------------------------

let private fsprojOf (project: string) = $"/repo/%s{project}.fsproj"

/// The fixture, with a model that publishes `identities` as each project's compile-input
/// identity under whatever generation it holds.
let private withIdentities (identities: Map<string, string> ref) (f: Fixture) =
    { f with
        Ctx =
            { f.Ctx with
                ProjectGraph =
                    { f.Ctx.ProjectGraph with
                        ObserveProjectInputs = fun () -> Some(f.Generation.Value, identities.Value)
                        GetAllProjects = fun () -> identities.Value |> Map.keys |> List.ofSeq } } }

/// What the host does with a state the handler produced: make its durable record the one a
/// restart reads.
let private publish (f: Fixture) (prior: TestPruneState) (candidate: TestPruneState) =
    match f.Handler.PrepareCommit with
    | Some prepare ->
        let prepared = prepare prior candidate |> Async.RunSynchronously
        prepared.Finalize |> Async.RunSynchronously
    | None -> ()

/// A new daemon over the same checkout: a fresh handler, whose model starts again at
/// generation 1, reading whatever the previous one left on disk.
let private restarted (f: Fixture) =
    f.Generation.Value <- 1L
    f.Scheduled.Value <- None

    { f with
        Handler = create (Path.Combine(f.Ctx.RepoRoot, "tp.db")) f.Ctx.RepoRoot (Some configs) None None None None [] }

let private identitiesOf (pairs: (string * string) list) =
    ref (
        pairs
        |> List.map (fun (project, identity) -> fsprojOf project, identity)
        |> Map.ofList
    )

[<Fact(Timeout = 30000)>]
let ``a restarted daemon over unchanged compile inputs runs only the classes a change needs`` () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    let verified = verifiedUnderModelOne f
    publish f f.Handler.Init verified

    let next = restarted f
    let _, launch = buildAndLaunch next (withChangeToLibF next.Handler.Init)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>

[<Fact(Timeout = 30000)>]
let ``a restarted daemon runs in full only the project whose compile inputs moved while it was down`` () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    let verified = verifiedUnderModelOne f
    publish f f.Handler.Init verified

    identities.Value <- Map.add (fsprojOf "ProjB") "b2" identities.Value
    let next = restarted f
    let _, launch = buildAndLaunch next (withChangeToLibF next.Handler.Init)

    test
        <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests"); "ProjB", ProjectInFull ] @>

[<Fact(Timeout = 30000)>]
let ``a restarted daemon whose model publishes no identities trusts no recorded evidence`` () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    let verified = verifiedUnderModelOne f
    publish f f.Handler.Init verified

    let next =
        let r = restarted f

        { r with
            Ctx =
                { r.Ctx with
                    ProjectGraph =
                        { r.Ctx.ProjectGraph with
                            ObserveProjectInputs = fun () -> None } } }

    let _, launch = buildAndLaunch next (withChangeToLibF next.Handler.Init)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ] @>

[<Fact(Timeout = 30000)>]
let ``a restarted daemon whose evidence record cannot be read runs every project in full`` () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    let verified = verifiedUnderModelOne f
    publish f f.Handler.Init verified
    File.WriteAllText(WholeProjectEvidence.sidecarPath f.Ctx.RepoRoot, "{ torn")

    let next = restarted f
    let _, launch = buildAndLaunch next (withChangeToLibF next.Handler.Init)

    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ] @>

let private recordOf (root: string) =
    match WholeProjectEvidence.load root with
    | WholeProjectEvidence.LoadedRecord.Loaded record -> Ok record
    | WholeProjectEvidence.LoadedRecord.Unreadable reason -> Error reason

let private writeRecord (root: string) (text: string) =
    let path = WholeProjectEvidence.sidecarPath root
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

[<Fact(Timeout = 15000)>]
let ``WholeProjectEvidence: no file is an empty record, and a saved record reads back`` () =
    let root = isolatedRoot ()
    test <@ recordOf root = Ok Map.empty @>

    let record =
        Map.ofList [ "ProjA", ("/repo/ProjA.fsproj", "a1"); "ProjB", ("/repo/ProjB.fsproj", "b1") ]

    WholeProjectEvidence.save root record
    test <@ recordOf root = Ok record @>

[<Theory(Timeout = 15000)>]
[<InlineData("{ torn")>]
[<InlineData("[]")>]
[<InlineData("null")>]
[<InlineData("{}")>]
[<InlineData("{\"projects\": []}")>]
[<InlineData("{\"projects\": {\"ProjA\": {\"fsproj\": \"/repo/ProjA.fsproj\"}}}")>]
[<InlineData("{\"projects\": {\"ProjA\": {\"fsproj\": \"/repo/ProjA.fsproj\", \"identity\": 7}}}")>]
[<InlineData("{\"projects\": {\"ProjA\": \"a1\"}}")>]
let ``WholeProjectEvidence: a record that exists and cannot be read is Unreadable, never empty`` (text: string) =
    let root = isolatedRoot ()
    writeRecord root text
    test <@ Result.isError (recordOf root) @>

[<Fact(Timeout = 15000)>]
let ``EarnedEvidence: a restored record vouches only for projects whose compile inputs are unchanged`` () =
    let restored =
        EarnedEvidence.restored (
            Map.ofList [ "ProjA", ("/repo/ProjA.fsproj", "a1"); "ProjB", ("/repo/ProjB.fsproj", "b1") ]
        )

    let now = Map.ofList [ "/repo/ProjA.fsproj", "a1"; "/repo/ProjB.fsproj", "b2" ]

    test <@ EarnedEvidence.carriedCoverage (Some 1L) (Some now) restored = Set.singleton "ProjA" @>
    test <@ EarnedEvidence.carriedCoverage (Some 1L) None restored = Set.empty @>
    test <@ EarnedEvidence.restored Map.empty = None @>
    // It refuses, so on its own it can never support a green.
    test <@ not (List.isEmpty restored.Value.FailureReasons) @>

// ---------------------------------------------------------------------------
// One project's launch stall (AUTOMATION-1320). "launch produced no live process within
// 300s — box overloaded" for ONE project used to abort the whole run: the stall raised out
// of the run, the completion carried no results, and the evidence every other project had
// just earned was discarded, so the next run ran all of them in full again.
// ---------------------------------------------------------------------------

/// `configs`, except that ProjB's runner never shows a sign of life: `sleep` writes
/// nothing, so the launch deadline kills it as a stall.
let private stallingConfigs =
    [ config "ProjA"
      { config "ProjB" with
          Command = "sleep"
          Args = "30" } ]

/// A fixture whose ProjB stalls at launch, under a one-second launch deadline, and whose
/// launches really execute.
let private stallingFixture () =
    let f = fixture ()
    // A baseline already earned, so that what runs in full is the evidence gap's doing alone.
    seedBaseline f.Ctx.RepoRoot [ "ProjA"; "ProjB" ]

    { f with
        Handler =
            createWithLaunchDeadline
                (TimeSpan.FromSeconds 1.0)
                None
                (fun () -> Map.empty)
                (Path.Combine(f.Ctx.RepoRoot, "tp.db"))
                TestPrune.Domain.WalkPolicy.FollowAll
                f.Ctx.RepoRoot
                (Some stallingConfigs)
                None
                None
                None
                None
                []
                None
                TestMode.initial }

[<Fact(Timeout = 60000)>]
let ``one project's launch stall costs only that project's evidence`` () =
    let f = stallingFixture ()
    f.Scheduled.Value <- None

    let launched =
        update f (withChangeToLibF f.Handler.Init) (BuildCompleted BuildSucceeded)

    let finished =
        match f.Scheduled.Value with
        | Some work -> work Ready |> Async.RunSynchronously
        | None -> failwith "the build must reach a launch"

    let completed, launch =
        match finished with
        | TestsFinished(_, completed, launch) -> completed, launch
        | other -> failwith $"expected TestsFinished, got %A{other}"

    // The run completes: ProjB's stall is ProjB's result, not the run's. It used to abort
    // the run ("runTests failed: ProjB: launch produced no live process"), with no results.
    test <@ completed.Outcome = Normal @>

    // A cold daemon ran both in full, so each could earn whole-project evidence.
    test <@ launch.Selection = Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ] @>

    test
        <@
            completed.Results
            |> Map.tryFind "ProjA"
            |> Option.exists TestResult.verifiedGreen
        @>

    match Map.tryFind "ProjB" completed.Results with
    | Some(TestsErrored reason) -> test <@ reason.Contains "no live process" && reason.Contains "ProjB" @>
    | other -> Assert.Fail($"expected ProjB's stall as its own errored result, got %A{other}")

    let settled = update f launched (Custom finished)

    // The stalled project still owes its whole-project run; the one that ran keeps its.
    let _, next = buildAndLaunch f (withChangeToLibF settled)

    test
        <@ next.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests"); "ProjB", ProjectInFull ] @>

// ---------------------------------------------------------------------------
// Unknown verification debt, said for what it is (AUTOMATION-1320). On 10-25 the land
// daemon logged "Scope: FULL SUITE (unreadable pending-verification ledger)" for six hours,
// across a restart, while the pending-verification ledger read fine: the debt was unknown
// because `Intelligence.Build.Tests`'s runtime coverage did not ingest ("SqliteException:
// SQLite Error 5: 'database is locked'"), and after the restart because that failure's
// marker was still on disk. Every cause of unknown debt was printed as the ledger's.
// ---------------------------------------------------------------------------

/// Run `body` with this context's log lines collected.
let private withLogLines (body: unit -> 'T) : 'T * string list =
    let lines = System.Collections.Concurrent.ConcurrentQueue<string>()

    let result =
        using
            (FsHotWatch.Logging.installSink
                { Write = lines.Enqueue
                  Level = FsHotWatch.Logging.LogLevel.Debug
                  File = None })
            (fun _ -> body ())

    result, List.ofSeq lines

[<Fact(Timeout = 30000)>]
let ``a full suite widened for unknown debt names the cause, not the pending-verification ledger`` () =
    // The cause the land daemon carried across its restart: a runtime-obligation write
    // that did not finish (in that incident, the marker a failed ingest used to leave).
    let f = fixture ()
    let verified = verifiedUnderModelOne f
    publish f f.Handler.Init verified
    let marker = runtimeCoverageRecoveryPath f.Ctx.RepoRoot
    Directory.CreateDirectory(Path.GetDirectoryName marker) |> ignore
    File.WriteAllText(marker, "runtime obligation write in progress")

    let next = restarted f

    let _, lines =
        withLogLines (fun () -> buildAndLaunch next (withChangeToLibF next.Handler.Init))

    let scope = lines |> List.filter (fun line -> line.Contains "Scope: FULL SUITE")
    test <@ not scope.IsEmpty @>

    test
        <@
            scope
            |> List.forall (fun line -> not (line.Contains "pending-verification ledger"))
        @>

    test
        <@
            scope
            |> List.exists (fun line -> line.Contains "runtime coverage obligation ledger")
        @>

[<Fact(Timeout = 30000)>]
let ``a daemon that starts over an unfinished debt record names that record as the cause`` () =
    let f = fixture ()
    let marker = runtimeCoverageRecoveryPath f.Ctx.RepoRoot
    Directory.CreateDirectory(Path.GetDirectoryName marker) |> ignore
    File.WriteAllText(marker, "runtime obligation write in progress")

    let next = restarted f
    let causes = next.Handler.Init.Debt.UnknownDebt

    test <@ causes.Length = 1 @>
    test <@ causes.Head.Contains "runtime coverage obligation ledger" @>
    test <@ causes.Head.Contains(runtimeCoverageObligationsPath f.Ctx.RepoRoot) @>
    test <@ not (causes.Head.Contains "pending-verification") @>

// ---------------------------------------------------------------------------
// A session that runs while what is owed is UNKNOWN publishes nothing (AUTOMATION-1347).
// It still wrote `owner-publication-pending` before every commit that changed the debt,
// and only a state that recovered removed it, so a daemon-only session over a red suite
// left the marker behind: the next daemon read "a publication did not finish" and widened
// every run to the full suite, whatever the original cause was. It also logged "Full-suite
// baseline recorded" for a baseline it never wrote.
// ---------------------------------------------------------------------------

let private publicationMarker (root: string) =
    Path.Combine(FsHotWatch.FsHwPaths.root root, "test-prune", "owner-publication-pending")

/// A full suite of both projects in which ProjA failed: accountable, so it earns a baseline,
/// and red, so it discharges no unknown debt.
let private finishedRedFullSuite () =
    let launch =
        { fullSuiteLaunch [ "ProjA"; "ProjB" ] with
            ModelGeneration = Some 1L }

    let results =
        Map.ofList
            [ "ProjA", TestsFailed("failed FsHotWatch.Tests.ProjATests.boom (12ms)", false, TimeSpan.FromSeconds 1.0)
              "ProjB", TestsPassed("ok", false, TimeSpan.FromSeconds 1.0) ]

    let started: TestRunStarted =
        { RunId = Guid.NewGuid()
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = results
          Verification = RunVerification.ofResults results }

    Custom(TestsFinished(started, completed, launch))

/// A daemon session with nothing attached: one red full suite, published as the owner
/// publishes it, then the daemon exits. Returns the session's log lines.
let private redSessionOver (f: Fixture) =
    let session = restarted f
    test <@ session.Handler.Init.Debt.RecoveryOutstanding @>

    let red, lines =
        withLogLines (fun () -> update session session.Handler.Init (finishedRedFullSuite ()))

    test <@ red.Debt.RecoveryOutstanding @>
    test <@ red.Debt.Baseline.IsSome @>
    publish session session.Handler.Init red
    lines

[<Fact(Timeout = 30000)>]
let ``a red session over an unreadable ledger leaves no unfinished publication behind`` () =
    let f = fixture ()
    let ledger = PendingVerification.sidecarPath f.Ctx.RepoRoot
    Directory.CreateDirectory(Path.GetDirectoryName ledger) |> ignore
    File.WriteAllText(ledger, "[ torn")

    redSessionOver f |> ignore

    test <@ not (File.Exists(publicationMarker f.Ctx.RepoRoot)) @>

    let causes = (restarted f).Handler.Init.Debt.UnknownDebt
    test <@ causes.Length = 1 @>
    test <@ causes.Head.Contains ledger @>

[<Fact(Timeout = 30000)>]
let ``a red session over an unfinished publication says the debt is still unrecovered, not newly interrupted`` () =
    let f = fixture ()
    let marker = publicationMarker f.Ctx.RepoRoot
    Directory.CreateDirectory(Path.GetDirectoryName marker) |> ignore
    File.WriteAllText(marker, "verification debt publication in progress")

    redSessionOver f |> ignore

    // The sidecars on disk are still the unfinished publication's, so the debt stays
    // unknown; the restart must read it as that, not as a session that died mid-write.
    let causes = (restarted f).Handler.Init.Debt.UnknownDebt
    test <@ causes.Length = 1 @>
    test <@ causes.Head.Contains marker @>
    test <@ not (causes.Head.Contains "before the previous daemon stopped") @>
    test <@ causes.Head.Contains "no full suite has passed" @>

[<Fact(Timeout = 30000)>]
let ``a baseline that is not written while debt is unknown is not logged as recorded`` () =
    let f = fixture ()
    let marker = publicationMarker f.Ctx.RepoRoot
    Directory.CreateDirectory(Path.GetDirectoryName marker) |> ignore
    File.WriteAllText(marker, "verification debt publication in progress")

    let lines = redSessionOver f

    test <@ not (File.Exists(FullSuiteBaseline.sidecarPath f.Ctx.RepoRoot)) @>

    test
        <@
            lines
            |> List.forall (fun line -> not (line.Contains "Full-suite baseline recorded"))
        @>

    test <@ lines |> List.exists (fun line -> line.Contains "not written") @>

// ---------------------------------------------------------------------------
// Runtime coverage that did not ingest is ITS project's debt (AUTOMATION-1320). One
// project's ingest failure ("database is locked") made what is owed unknown for the whole
// suite: every run was FULL SUITE until one passed every project, and the marker it left
// made the next daemon start the same way. What is unknown is that one project's runtime
// coverage, so that project runs in full until a run of it ingests; the rest select as
// usual, before and after a restart.
// ---------------------------------------------------------------------------

let private lockedIngest = "SqliteException: SQLite Error 5: 'database is locked'"

[<Fact(Timeout = 30000)>]
let ``a project whose runtime coverage did not ingest runs in full, and only it`` () =
    let f = fixture ()
    let verified = verifiedUnderModelOne f

    let failed =
        update f verified (Custom(RuntimeCoverageFailed("ProjB", lockedIngest)))

    let _, launch = buildAndLaunch f (withChangeToLibF failed)

    test
        <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests"); "ProjB", ProjectInFull ] @>

[<Fact(Timeout = 30000)>]
let ``a restarted daemon still owes only the project whose runtime coverage did not ingest`` () =
    let identities = identitiesOf [ "ProjA", "a1"; "ProjB", "b1" ]
    let f = fixture () |> withIdentities identities
    let verified = verifiedUnderModelOne f
    publish f f.Handler.Init verified

    // As a run does: the durable record first, then the owner hears of it.
    armCoverageNotIngested
        ignore
        (CoverageNotIngested.sidecarPath f.Ctx.RepoRoot)
        { Project = "ProjB"
          Reason = lockedIngest }

    let failed =
        update f verified (Custom(RuntimeCoverageFailed("ProjB", lockedIngest)))

    publish f verified failed

    let next = restarted f
    test <@ List.isEmpty next.Handler.Init.Debt.UnknownDebt @>

    let _, launch = buildAndLaunch next (withChangeToLibF next.Handler.Init)

    test
        <@ launch.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests"); "ProjB", ProjectInFull ] @>

[<Fact(Timeout = 30000)>]
let ``a passing whole-project run of that project discharges its runtime coverage debt`` () =
    let f = fixture ()
    let verified = verifiedUnderModelOne f

    let failed =
        update f verified (Custom(RuntimeCoverageFailed("ProjB", lockedIngest)))

    let launched, launch = buildAndLaunch f (withChangeToLibF failed)
    let settled = update f launched (finishedGreen launch)

    let _, next = buildAndLaunch f (withChangeToLibF settled)

    test <@ next.Selection = Map.ofList [ "ProjA", ProjectClasses(Set.singleton "ProjATests") ] @>

[<Fact(Timeout = 15000)>]
let ``CoverageNotIngested: arm keeps every project recorded, and an empty record removes the file`` () =
    let root = isolatedRoot ()
    let path = CoverageNotIngested.sidecarPath root
    test <@ CoverageNotIngested.load root = CoverageNotIngested.LoadedRecord.Loaded Map.empty @>

    let notified = ref 0
    CoverageNotIngested.arm (fun () -> notified.Value <- notified.Value + 1) path "ProjA" "locked"
    CoverageNotIngested.arm (fun () -> notified.Value <- notified.Value + 1) path "ProjB" "malformed"

    test <@ notified.Value = 2 @>

    test
        <@
            CoverageNotIngested.load root = CoverageNotIngested.LoadedRecord.Loaded(
                Map.ofList [ "ProjA", "locked"; "ProjB", "malformed" ]
            )
        @>

    CoverageNotIngested.save root Map.empty
    test <@ not (File.Exists path) @>
    CoverageNotIngested.save root Map.empty
    test <@ not (File.Exists path) @>

[<Theory(Timeout = 15000)>]
[<InlineData("{ torn")>]
[<InlineData("[]")>]
[<InlineData("null")>]
[<InlineData("{\"ProjA\": 7}")>]
[<InlineData("{\"ProjA\": {}}")>]
let ``CoverageNotIngested: a record that cannot be read is Unreadable, and arm leaves it so`` (text: string) =
    let root = isolatedRoot ()
    let path = CoverageNotIngested.sidecarPath root
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

    let unreadable () =
        match CoverageNotIngested.load root with
        | CoverageNotIngested.LoadedRecord.Unreadable _ -> true
        | CoverageNotIngested.LoadedRecord.Loaded _ -> false

    test <@ unreadable () @>

    let notified = ref false
    CoverageNotIngested.arm (fun () -> notified.Value <- true) path "ProjA" "locked"

    test <@ notified.Value @>
    test <@ unreadable () @>
    test <@ File.ReadAllText path = text @>

[<Fact(Timeout = 30000)>]
let ``a daemon that starts over an unreadable ingest record owes unknown debt, naming it`` () =
    let f = fixture ()
    let path = CoverageNotIngested.sidecarPath f.Ctx.RepoRoot
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, "{ torn")

    let causes = (restarted f).Handler.Init.Debt.UnknownDebt

    test <@ causes.Length = 1 && causes.Head.Contains path @>
