/// TestPrune decides from the state its owner supplies. Cache keys, observations and
/// durable debt are functions of that state, so a retained snapshot never learns a later
/// completion, and a completion whose fold failed changes nothing.
///
/// A file of its own because `TestPruneRunScopeTests` is near TestPrune's symbol
/// traversal budget; shared harness in `TestPrunePluginTestSupport`.
module FsHotWatch.Tests.TestPruneOwnerStateTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestPrunePluginTestSupport

let private recordingCtx () : PluginCtx<TestPruneMsg> =
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
      RepoRoot = ""
      Post = ignore
      EnqueueExclusiveIntent = fun _ _ _ -> Task.FromResult(())
      StartSubtask = fun _ _ -> ()
      UpdateSubtask = fun _ _ -> ()
      EndSubtask = ignore
      Log = ignore
      CompleteWithTimeout = ignore
      RunExclusive = fun _ _ -> Claimed
      RunExclusiveShared = fun _ _ _ _ _ -> SharedClaimed
      SlotHolder = fun _ -> SlotHolder.Free
      DeclareBoundedWork = FsHotWatch.PluginFramework.BoundedWork.undeclared
      FcsSuppressedCodes = Set.empty
      ProjectGraph = ProjectGraphAccessor.none }

let private commandCtx (root: string) : CommandCtx<TestPruneMsg> =
    { RepoRoot = root
      Log = ignore
      Post = ignore
      EnqueueExclusiveIntent = fun _ _ _ -> Task.FromResult(())
      IsRunning = fun _ -> false
      ProjectGraph = ProjectGraphAccessor.none }

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

let private finished (results: (string * TestResult) list) (launch: TestRunLaunch) =
    let runId = Guid.NewGuid()

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = Map.ofList results
          Verification = RunVerification.ofResults (Map.ofList results) }

    Custom(TestsFinished(started, completed, launch))

let private passing = TestsPassed("ok", false, TimeSpan.FromSeconds 1.0)

let private failing =
    TestsFailed("failed ProjA.Tests.case (1ms)", false, TimeSpan.Zero)

let private update (ctx: PluginCtx<TestPruneMsg>) handler state event =
    (handler: PluginHandler<TestPruneState, TestPruneMsg>).Update ctx state event
    |> Async.RunSynchronously

let private buildKey (handler: PluginHandler<TestPruneState, TestPruneMsg>) state =
    handler.CacheKey.Value state (BuildCompleted BuildSucceeded)

let private invoke (handler: PluginHandler<TestPruneState, TestPruneMsg>) name ctx state (args: string array) =
    let command = handler.Commands |> List.find (fst >> (=) name) |> snd
    PluginCommand.invoke command ctx state args

let private read handler name root state =
    invoke handler name (commandCtx root) state [||] |> Async.RunSynchronously

/// The context a status report refuses from, so a completion fold fails after its ledger
/// and debt decisions have been made.
let private refusingCtx (refusal: exn) =
    { recordingCtx () with
        ReportStatus = fun _ -> raise refusal }

[<Fact(Timeout = 15000)>]
let ``test cache hashes the changed symbols of its supplied owner snapshot`` () =
    let handler = create ":memory:" (isolatedRoot ()) None None None None None []
    let initial = buildKey handler handler.Init
    Assert.True(initial.IsSome, "positive control: an analysis-only cache key exists")

    let changed =
        { handler.Init with
            ChangedSymbols = [ "Library.changed" ] }

    let changedKey = buildKey handler changed
    Assert.True(changedKey.IsSome)
    Assert.NotEqual<ContentHash option>(initial, changedKey)
    Assert.Equal<ContentHash option>(initial, buildKey handler handler.Init)

[<Theory(Timeout = 15000)>]
[<InlineData("test-scope")>]
[<InlineData("check-reach")>]
let ``completion observations remain bound to their supplied owner snapshot`` (commandName: string) =
    let root = isolatedRoot ()

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let ctx = recordingCtx ()

    let reading state =
        use document = JsonDocument.Parse(read handler commandName root state)
        let field = if commandName = "test-scope" then "runIds" else "runId"
        document.RootElement.GetProperty(field).GetRawText()

    let complete state =
        update ctx handler state (finished [ "ProjA", passing ] (fullSuiteLaunch [ "ProjA" ]))

    let first = complete handler.Init
    let firstReading = reading first
    let second = complete first
    Assert.NotEqual<string>(firstReading, reading second)
    Assert.Equal(firstReading, reading first)

[<Fact(Timeout = 15000)>]
let ``a failed completion fold cannot clear the committed cache refusal`` () =
    let root = isolatedRoot ()

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let red =
        update (recordingCtx ()) handler handler.Init (finished [ "ProjA", failing ] (fullSuiteLaunch [ "ProjA" ]))

    Assert.NotEmpty(red.OutstandingFailures)
    Assert.True((buildKey handler red).IsNone, "positive control: a red refuses the cache")
    let refusal = InvalidOperationException("fixture refuses the completion")

    let observed =
        Assert.Throws<InvalidOperationException>(fun () ->
            update (refusingCtx refusal) handler red (finished [ "ProjA", passing ] (fullSuiteLaunch [ "ProjA" ]))
            |> ignore)

    Assert.Same(refusal, observed)

    Assert.True(
        (buildKey handler red).IsNone,
        "a passing fold that never returned state must not make the committed red cacheable"
    )

[<Fact(Timeout = 15000)>]
let ``a retained owner cannot learn a full suite baseline earned by a later completion`` () =
    let root = isolatedRoot ()

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let baseline state =
        use document = JsonDocument.Parse(read handler "test-scope" root state)

        document.RootElement.GetProperty("baseline").GetRawText(),
        document.RootElement.GetProperty("baselineAbsent").GetRawText()

    let initial = baseline handler.Init
    Assert.Equal("null", fst initial)
    Assert.NotEqual<string>("null", snd initial)

    let completed =
        update (recordingCtx ()) handler handler.Init (finished [ "ProjA", passing ] (fullSuiteLaunch [ "ProjA" ]))

    let current = baseline completed
    Assert.NotEqual<string>("null", fst current)
    Assert.Equal("null", snd current)
    Assert.Equal(initial, baseline handler.Init)

/// A handler whose durable queue owes `Library.changed` to ProjA, after one passing full
/// suite that launched no symbols: the session has test evidence, and the debt remains.
let private pendingDebtFixture () =
    let root = isolatedRoot ()
    let symbol = "Library.changed"
    PendingVerification.save root (Set.singleton symbol)

    let make () =
        create (Path.Combine(root, "pending-owner.db")) root (Some [ config "ProjA" ]) None None None None []

    let handler = make ()
    let ctx = recordingCtx ()

    let prior =
        update ctx handler handler.Init (finished [ "ProjA", passing ] (fullSuiteLaunch [ "ProjA" ]))

    Assert.NotEmpty(RunCoverage.coveredProjects prior.LastCoverage)
    Assert.True((buildKey handler prior).IsNone, "positive control: queued debt refuses the cache")

    let launch =
        { fullSuiteLaunch [ "ProjA" ] with
            Symbols = Set.singleton symbol
            CoveringProjectsBySymbol = Map.ofList [ symbol, Set.singleton "ProjA" ] }

    root, symbol, make, handler, ctx, prior, launch

let private durableQueueOwes root symbol =
    match PendingVerification.load root with
    | PendingVerification.LoadedQueue.Unreadable _ -> true
    | PendingVerification.LoadedQueue.Loaded queue -> Set.contains symbol queue

[<Theory(Timeout = 15000)>]
[<InlineData("cache")>]
[<InlineData("disk")>]
let ``a failed completion fold cannot discharge committed pending debt`` (observer: string) =
    let root, symbol, make, handler, _, prior, launch = pendingDebtFixture ()
    let refusal = InvalidOperationException("fixture refuses the completion")

    Assert.Throws<InvalidOperationException>(fun () ->
        update (refusingCtx refusal) handler prior (finished [ "ProjA", passing ] launch)
        |> ignore)
    |> ignore

    if observer = "cache" then
        Assert.True((buildKey handler prior).IsNone, "the committed owner still owes the symbol")
    else
        let restarted = make ()
        Assert.True(durableQueueOwes root symbol, "the durable queue must still name the symbol")
        Assert.True((buildKey restarted restarted.Init).IsNone, "a restart must still owe the symbol")

[<Fact(Timeout = 15000)>]
let ``a successful completion discharges only its returned pending debt snapshot`` () =
    let _, _, _, handler, ctx, prior, launch = pendingDebtFixture ()
    let completed = update ctx handler prior (finished [ "ProjA", passing ] launch)
    Assert.True((buildKey handler completed).IsSome, "positive control: the completed owner paid its debt")
    Assert.True((buildKey handler prior).IsNone, "a retained snapshot must not learn a later discharge")

[<Theory(Timeout = 15000)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a failed command receipt acknowledges only its published owner outcome`` (invalidArtifacts: bool) =
    let handler =
        create ":memory:" (isolatedRoot ()) (Some [ config "ProjA" ]) None None None None []

    let reply = TaskCompletionSource<string>()

    let message =
        if invalidArtifacts then
            ArtifactsUnavailable("fixture refusal", Set.empty, Some reply)
        else
            TestHostUnavailable("fixture refusal", Set.empty, Some reply)

    let candidate = update (recordingCtx ()) handler handler.Init (Custom message)
    Assert.False(reply.Task.IsCompleted, "the command was acknowledged before its outcome was published")

    let prepared =
        handler.PrepareCommit.Value handler.Init candidate |> Async.RunSynchronously

    Assert.False(reply.Task.IsCompleted, "the command was acknowledged before its outcome was published")
    prepared.Finalize |> Async.RunSynchronously
    Assert.Contains("fixture refusal", reply.Task.GetAwaiter().GetResult())

[<Fact(Timeout = 15000)>]
let ``pending debt persistence follows the successful proposal and precedes acknowledgement`` () =
    let root, symbol, make, handler, ctx, prior, launch = pendingDebtFixture ()
    let candidate = update ctx handler prior (finished [ "ProjA", passing ] launch)
    Assert.True(durableQueueOwes root symbol, "an unpublished proposal must not discharge durable debt")

    let prepared = handler.PrepareCommit.Value prior candidate |> Async.RunSynchronously
    Assert.False(durableQueueOwes root symbol, "a successful preparation writes the candidate's queue")
    Assert.True((make ()).Init.Debt.RecoveryOutstanding, "before publication a restart cannot trust the sidecars")

    prepared.Finalize |> Async.RunSynchronously
    let published = make ()
    Assert.False(published.Init.Debt.RecoveryOutstanding)
    Assert.Empty(published.Init.Debt.PendingQueue)
    Assert.Contains(symbol, prior.Debt.PendingQueue)

[<Fact(Timeout = 15000)>]
let ``failed durable preparation retains restart debt after a partial sidecar write`` () =
    let root, symbol, make, handler, ctx, prior, launch = pendingDebtFixture ()
    let candidate = update ctx handler prior (finished [ "ProjA", passing ] launch)

    // The queue is written before the baseline. Refusing the baseline write models a
    // preparation that failed after part of it reached disk.
    let baselinePath = FullSuiteBaseline.sidecarPath root

    if File.Exists baselinePath then
        File.Delete baselinePath

    Directory.CreateDirectory baselinePath |> ignore

    Assert.ThrowsAny<Exception>(fun () ->
        handler.PrepareCommit.Value prior candidate |> Async.RunSynchronously |> ignore)
    |> ignore

    Assert.False(durableQueueOwes root symbol, "positive control: the queue write happened")
    Assert.Contains(symbol, prior.Debt.PendingQueue)
    let restarted = make ()
    Assert.True(restarted.Init.Debt.RecoveryOutstanding)
    Assert.True((buildKey restarted restarted.Init).IsNone)

[<Fact(Timeout = 15000)>]
let ``a completed launch cannot discharge a newer revision of the same symbol`` () =
    let _, symbol, _, handler, ctx, prior, launch = pendingDebtFixture ()

    let changedAgain =
        { prior with
            Debt =
                { prior.Debt with
                    Revision = 1L
                    SymbolRevisions = Map.ofList [ symbol, 1L ] } }

    let completed =
        update ctx handler changedAgain (finished [ "ProjA", passing ] launch)

    Assert.Contains(symbol, completed.Debt.PendingQueue)
    Assert.Equal(1L, completed.Debt.SymbolRevisions[symbol])
    Assert.True((buildKey handler completed).IsNone)

    let currentLaunch =
        { launch with
            SymbolRevisions = changedAgain.Debt.SymbolRevisions }

    let verified =
        update ctx handler changedAgain (finished [ "ProjA", passing ] currentLaunch)

    Assert.DoesNotContain(symbol, verified.Debt.PendingQueue)

/// A command context whose intents are recorded and acknowledged by `receipt`, as the
/// owner acknowledges once the intent's fold is committed.
let private intentCtx root (receipt: Task<unit>) =
    let intents = ResizeArray<string * TestPruneMsg>()

    { commandCtx root with
        EnqueueExclusiveIntent =
            fun key _ message ->
                intents.Add(key, message)
                receipt },
    intents

[<Theory(Timeout = 15000)>]
[<InlineData("full")>]
[<InlineData("impact")>]
let ``set-scope replies only after the owner applies its intent`` (scope: string) =
    task {
        let root = isolatedRoot ()

        let handler =
            create ":memory:" root (Some [ config "ProjA" ]) None None None None []

        let receipt =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let ctx, intents = intentCtx root receipt.Task
        let args = [| JsonSerializer.Serialize {| scope = scope |} |]

        let reply =
            invoke handler "set-scope" ctx handler.Init args |> Async.StartImmediateAsTask

        Assert.Equal(1, intents.Count)
        Assert.False(reply.IsCompleted, "set-scope acknowledged before its owner committed the intent")

        let applied =
            update (recordingCtx ()) handler { handler.Init with Mode = PassThrough } (Custom(snd intents[0]))

        Assert.Equal((if scope = "full" then PassThrough else ImpactSelection), applied.Mode)
        Assert.False(reply.IsCompleted, "set-scope acknowledged before its owner committed the intent")
        receipt.SetResult(())
        let! response = reply.WaitAsync(TimeSpan.FromSeconds 5.0)
        use json = JsonDocument.Parse response
        Assert.Equal(scope, json.RootElement.GetProperty("scope").GetString())
    }

[<Fact(Timeout = 15000)>]
let ``run-tests reports a refused intent instead of waiting for a run that cannot come`` () =
    let root = isolatedRoot ()

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let ctx, intents =
        intentCtx root (Task.FromException<unit>(InvalidOperationException "executor stopped"))

    let response =
        invoke handler "run-tests" ctx handler.Init [| """{"wait-sec": 5}""" |]
        |> Async.RunSynchronously

    Assert.Equal(1, intents.Count)
    Assert.Contains("executor stopped", response)

[<Fact(Timeout = 15000)>]
let ``a failed coverage ingest makes the owner's debt unknown`` () =
    let handler =
        create ":memory:" (isolatedRoot ()) (Some [ config "ProjA" ]) None None None None []

    Assert.False(handler.Init.Debt.RecoveryOutstanding)

    let failed =
        update (recordingCtx ()) handler handler.Init (Custom(RuntimeCoverageFailed "ProjA"))

    Assert.True(failed.Debt.RecoveryOutstanding)
    Assert.True((buildKey handler failed).IsNone)

/// A plugin context whose intents and shared claims are recorded.
let private launchRecordingCtx (claim: SharedRunClaim) =
    let intents = ResizeArray<string * string option>()
    let mutable claims = 0

    { recordingCtx () with
        EnqueueExclusiveIntent =
            fun key coalescing _ ->
                intents.Add(key, coalescing)
                Task.FromResult(())
        RunExclusiveShared =
            fun _ _ _ _ _ ->
                claims <- claims + 1
                claim },
    intents,
    (fun () -> claims)

[<Fact(Timeout = 15000)>]
let ``a queued impact run whose debt was discharged launches nothing`` () =
    let root = isolatedRoot ()
    seedBaseline root [ "ProjA" ]

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let ctx, intents, claims = launchRecordingCtx SharedClaimed
    let idle = update ctx handler handler.Init (Custom ImpactRunRequested)
    Assert.Equal(0, claims ())
    Assert.Empty intents
    Assert.Equal<Set<string>>(Set.empty, idle.PendingForceRunProjects)

[<Theory(Timeout = 15000)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a queued impact run launches owed fanout or queues again behind a held key`` (free: bool) =
    let root = isolatedRoot ()
    seedBaseline root [ "ProjA" ]

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let ctx, intents, claims =
        launchRecordingCtx (if free then SharedClaimed else LocalSlotBusy)

    let owed =
        { handler.Init with
            PendingForceRunProjects = Set.singleton "ProjA" }

    let next = update ctx handler owed (Custom ImpactRunRequested)
    Assert.Equal(1, claims ())

    if free then
        Assert.Empty intents
        Assert.Empty next.PendingForceRunProjects
    else
        Assert.Equal<(string * string option) list>([ "tests", Some "impact" ], Seq.toList intents)
        Assert.Equal<Set<string>>(Set.singleton "ProjA", next.PendingForceRunProjects)

/// A tests run's result folds ahead of the file checks queued before it: its fold sheds
/// only the symbols the run launched with, so checks that arrive during the run lose
/// nothing by folding after it. Both of the run's shapes are declared: the run itself,
/// and the refusal it becomes when the artifact lease is invalid.
[<Fact(Timeout = 15000)>]
let ``a tests run is declared result-first`` () =
    let root = isolatedRoot ()
    seedBaseline root [ "ProjA" ]

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let runs = ResizeArray<SharedResourceState -> Async<TestPruneMsg>>()

    let ctx =
        { recordingCtx () with
            RunExclusiveShared =
                fun _ _ workFor _ _ ->
                    runs.Add workFor
                    SharedClaimed }

    let owed =
        { handler.Init with
            PendingForceRunProjects = Set.singleton "ProjA" }

    update ctx handler owed (Custom ImpactRunRequested) |> ignore
    let workFor = Assert.Single runs
    Assert.True(PluginWork.isResultFirst (workFor Ready), "the run")
    Assert.True(PluginWork.isResultFirst (workFor (Invalid "stale artifacts")), "the refusal")

[<Fact(Timeout = 15000)>]
let ``a queued impact run without test projects only analyses`` () =
    let handler = create ":memory:" (isolatedRoot ()) None None None None None []
    let ctx, intents, claims = launchRecordingCtx SharedClaimed
    let next = update ctx handler handler.Init (Custom ImpactRunRequested)
    Assert.Equal(0, claims ())
    Assert.Empty intents
    Assert.Equal(handler.Init.Debt, next.Debt)

[<Fact(Timeout = 15000)>]
let ``a build landing while tests hold their key queues one impact run`` () =
    let root = isolatedRoot ()

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let ctx, intents, claims = launchRecordingCtx SharedClaimed

    let busy =
        { ctx with
            SlotHolder =
                fun key ->
                    if key = "tests" then
                        SlotHolder.LiveRun
                    else
                        SlotHolder.Free }

    update busy handler handler.Init (BuildCompleted BuildSucceeded) |> ignore
    Assert.Equal(0, claims ())
    Assert.Equal<(string * string option) list>([ "tests", Some "impact" ], Seq.toList intents)

/// A finished run's result fold holds the key with no live worker, so a claim is refused.
/// Selection is the expensive step, and the refused claim would discard it: the build
/// queues its run before selecting anything. `effects` records every selection, claim and
/// intent in the order the handler made them.
[<Fact(Timeout = 15000)>]
let ``a build landing while a result fold holds the tests key queues without selecting`` () =
    let root = isolatedRoot ()

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let effects = ResizeArray<string>()
    let activity = ResizeArray<string>()

    let folding =
        { recordingCtx () with
            SlotHolder = fun key -> if key = "tests" then SlotHolder.Fold else SlotHolder.Free
            DeclareBoundedWork =
                fun label _ ->
                    effects.Add $"select: %s{label}"

                    { new IDisposable with
                        member _.Dispose() = () }
            RunExclusiveShared =
                fun key _ _ _ _ ->
                    effects.Add $"claim: %s{key}"
                    LocalSlotBusy
            EnqueueExclusiveIntent =
                fun key _ _ ->
                    effects.Add $"intent: %s{key}"
                    Task.FromResult(())
            Log = activity.Add }

    update folding handler handler.Init (BuildCompleted BuildSucceeded) |> ignore
    Assert.Equal<string list>([ "intent: tests" ], Seq.toList effects)
    Assert.Contains(activity, fun line -> line.Contains "queued re-run" && line.Contains "uncommitted fold")

/// A handler with a full-suite baseline and one passing full run: session coverage, an
/// empty queue and no reds, so an ordinary `BuildCompleted` may replay a cached green.
let private replayEligibleOwner () =
    let root = isolatedRoot ()
    seedBaseline root [ "ProjA" ]

    let handler =
        create ":memory:" root (Some [ config "ProjA" ]) None None None None []

    let green =
        update (recordingCtx ()) handler handler.Init (finished [ "ProjA", passing ] (fullSuiteLaunch [ "ProjA" ]))

    Assert.True((buildKey handler green).IsSome, "positive control: a settled green may replay")
    handler, green

[<Fact(Timeout = 15000)>]
let ``owed dependency fanout refuses the build cache`` () =
    let handler, green = replayEligibleOwner ()

    let owed =
        { green with
            PendingForceRunProjects = Set.singleton "ProjA" }

    Assert.True((buildKey handler owed).IsNone, "a replayed green would skip the launch that runs the owed fanout")

[<Fact(Timeout = 15000)>]
let ``a build folded over owed dependency fanout launches it`` () =
    let handler, green = replayEligibleOwner ()
    let mutable scheduled: (SharedResourceState -> Async<TestPruneMsg>) option = None

    let ctx =
        { recordingCtx () with
            RunExclusiveShared =
                fun key _ work _ _ ->
                    Assert.Equal("tests", key)
                    scheduled <- Some work
                    SharedClaimed }

    let owed =
        { green with
            PendingForceRunProjects = Set.singleton "ProjA" }

    let launched = update ctx handler owed (BuildCompleted BuildSucceeded)
    Assert.True(scheduled.IsSome, "the owed fanout must reach a launch")
    Assert.Empty launched.PendingForceRunProjects

    // An unavailable resource hands back exactly what the launch consumed, without
    // starting a test host.
    match scheduled.Value(Invalid "fixture refusal") |> Async.RunSynchronously with
    | ArtifactsUnavailable(_, consumed, _) -> Assert.Equal<Set<string>>(Set.singleton "ProjA", consumed)
    | other -> Assert.Fail($"expected the refused launch to carry its fanout, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``a scan's build does not replay a cached green over owed dependency fanout`` () =
    let root = isolatedRoot ()
    seedBaseline root [ "ProjA" ]
    let runs = Path.Combine(root, "runs")

    let cache =
        FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

    let host =
        FsHotWatch.PluginHost.PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)

    let runner =
        { config "ProjA" with
            Args = $"-c \"printf x >> '%s{runs}'\"" }

    let handler =
        create (Path.Combine(root, "tp.db")) root (Some [ runner ]) None None None None []

    // The fanout a refused launch leaves owed, delivered as a fixture command outcome so
    // the host's own dispatch and cache replay decide what happens next.
    let owing =
        { handler with
            Subscriptions = Set.add SubscribeCommandCompleted handler.Subscriptions
            Update =
                fun ctx state event ->
                    match event with
                    | CommandCompleted { Name = "fixture-owe-fanout" } ->
                        async {
                            return
                                { state with
                                    PendingForceRunProjects = Set.singleton "ProjA" }
                        }
                    | _ -> handler.Update ctx state event }

    host.RegisterHandler owing

    let runCount () =
        if File.Exists runs then
            File.ReadAllText(runs).Length
        else
            0

    let settle () =
        Assert.True(
            FsHotWatch.Tests.TestHelpers.waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 15000,
            "the host must come to rest"
        )

    host.EmitBuildCompleted BuildSucceeded
    settle ()
    Assert.Equal(1, runCount ())

    // Positive control: an unchanged build replays the cached green without a run.
    host.EmitBuildCompleted BuildSucceeded
    settle ()
    Assert.Equal(1, runCount ())

    host.EmitCommandCompleted
        { Name = "fixture-owe-fanout"
          Outcome = CommandSucceeded "owed" }

    settle ()
    host.EmitBuildCompleted BuildSucceeded
    settle ()
    Assert.Equal(2, runCount ())
