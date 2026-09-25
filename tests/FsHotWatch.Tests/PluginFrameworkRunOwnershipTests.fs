/// Exclusive and shared runs under the plugin's work owner: a run keeps its key until its
/// cleanup and result fold finish, and a failed run keeps its failure.
module FsHotWatch.Tests.PluginFrameworkRunOwnershipTests

open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.PluginFrameworkFixtures

[<Fact(Timeout = 15000)>]
let ``failed queued starter is acknowledged after the tail accepts handoff`` () =
    use tailEntered = new System.Threading.ManualResetEventSlim(false)
    use releaseTail = new System.Threading.ManualResetEventSlim(false)
    use acknowledged = new System.Threading.ManualResetEventSlim(false)
    let scheduler = SharedRunScheduler()
    Assert.Equal(Some Ready, scheduler.ClaimOrQueue("artifacts", fun _ -> SharedStarted))

    let failed =
        scheduler.ClaimOrQueue(
            "artifacts",
            fun _ ->
                SharedStartFailed(fun outcome ->
                    match outcome with
                    | Ok() -> acknowledged.Set()
                    | Result.Error ex -> raise ex)
        )

    Assert.True(failed.IsNone)

    let tail =
        scheduler.ClaimOrQueue(
            "artifacts",
            fun state ->
                Assert.Equal(Invalid "shared waiter failed to start", state)
                tailEntered.Set()
                Assert.True(releaseTail.Wait(10000), "fixture must release the tail starter")
                SharedStarted
        )

    Assert.True(tail.IsNone)

    let handoff =
        System.Threading.Tasks.Task.Run(fun () -> scheduler.Release("artifacts", Ready))

    try
        Assert.True(tailEntered.Wait(5000), "the tail must receive ownership")
        Assert.False(acknowledged.IsSet, "failed starter still owns completion until handoff returns")
    finally
        releaseTail.Set()
        handoff.WaitAsync(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

    Assert.True(acknowledged.IsSet, "completed handoff must acknowledge the failed starter")
    scheduler.Release("artifacts", Ready)
    Assert.Equal(Some Ready, scheduler.ClaimOrQueue("artifacts", fun _ -> SharedStarted))

[<Theory(Timeout = 20000)>]
[<InlineData("classification")>]
[<InlineData("handoff")>]
let ``exclusive ownership survives shared completion processing`` (blockedStage: string) =
    use entered = new System.Threading.ManualResetEventSlim(false)
    use release = new System.Threading.ManualResetEventSlim(false)
    let scheduler = SharedRunScheduler()
    let mutable capturedCtx: PluginCtx<SharedWakeMsg> option = None

    let pause stage =
        if blockedStage = stage then
            entered.Set()
            Assert.True(release.Wait(10000), "test must release the completion stage")

    let services =
        { defaultServices with
            ClaimOrQueueSharedRun = fun key start -> scheduler.ClaimOrQueue(key, start)
            ReleaseSharedRun =
                fun key state ->
                    pause "handoff"
                    scheduler.Release(key, state) }

    let original =
        sharedWakeHandlerWithClassifier "completion-ownership" (fun _ -> async { return SharedFinished }) (fun _ ->
            pause "classification"
            Ready)

    let handler =
        { original with
            Update =
                fun ctx state event ->
                    capturedCtx <- Some ctx
                    original.Update ctx state event }

    let registration = registerHandler services handler

    try
        dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
        Assert.True(entered.Wait(5000), "shared completion must reach the controlled stage")
        Assert.True(registration.IsBusy(), "completion still owns outstanding work")
        let ctx = capturedCtx.Value
        let secondClaim = ctx.RunExclusive "work" (async { return SharedFinished })
        test <@ secondClaim = SlotBusy @>

        Assert.True(
            ctx.SlotHolder "work" = SlotHolder.LiveRun,
            "the slot must remain owned through completion processing"
        )
    finally
        release.Set()
        waitUntil (fun () -> not (registration.IsBusy())) 5000

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``exclusive workers retain their registered owner context instead of a triggering scope`` shared =
    let ambient = System.Threading.AsyncLocal<string>()
    ambient.Value <- "registered owner"
    let mutable captured: PluginCtx<SharedWakeMsg> option = None

    let observed =
        System.Threading.Tasks.TaskCompletionSource<string>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let original =
        sharedWakeHandler "owner-context" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ -> captured <- Some ctx
                        | _ -> ()

                        return state
                    } }

    let registration = registerDefault handler

    try
        dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
        let ctx = captured.Value
        ambient.Value <- "short-lived triggering scope"

        let work =
            async {
                observed.TrySetResult(ambient.Value) |> ignore
                return SharedFinished
            }

        if shared then
            Assert.Equal(
                SharedClaimed,
                ctx.RunExclusiveShared "work" "resource" (fun _ -> work) (fun _ -> Ready) (fun ex ->
                    SharedFailed ex.Message)
            )
        else
            Assert.Equal(Claimed, ctx.RunExclusive "work" work)

        let actual =
            observed.Task.WaitAsync(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        Assert.True(
            System.Threading.SpinWait.SpinUntil(
                (fun () -> not (registration.IsBusy())),
                System.TimeSpan.FromSeconds 5.0
            ),
            "the worker and result fold must actually drain"
        )

        Assert.Equal("registered owner", actual)
    finally
        ambient.Value <- null

[<Fact(Timeout = 15000)>]
let ``ordinary exclusive launch failure uses the host launcher and retires ownership`` () =
    let mutable capturedCtx: PluginCtx<SharedWakeMsg> option = None
    let mutable launchCount = 0
    let failure = System.InvalidOperationException("ordinary launch fault")
    let statuses = System.Collections.Concurrent.ConcurrentBag<PluginStatus>()

    let services =
        { defaultServices with
            StartAsync =
                fun _ ->
                    launchCount <- launchCount + 1
                    raise failure
            ReportStatus = fun _ status -> statuses.Add(status) }

    let original =
        sharedWakeHandler "ordinary-launch-failure" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun ctx state _ ->
                    capturedCtx <- Some ctx
                    async { return state } }

    let registration = registerHandler services handler
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
    let claim = capturedCtx.Value.RunExclusive "work" (async { return SharedFinished })
    test <@ claim = Claimed @>
    Assert.Equal(1, launchCount)
    Assert.False(registration.IsBusy(), "failed startup must resolve its admitted obligation")
    Assert.True(registration.Fault().IsSome, "failed launch must retain failure in the owner snapshot")
    Assert.Same(failure, registration.Fault().Value)
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
    Assert.Same(failure, registration.Fault().Value)

    Assert.Contains(
        statuses,
        fun status ->
            match status with
            | Failed(summary, _, _) -> summary.Contains("ordinary launch fault")
            | _ -> false
    )

[<Fact(Timeout = 15000)>]
let ``throwing shared cleanup terminalizes the run without publishing success`` () =
    let launched =
        System.Threading.Tasks.TaskCompletionSource<System.Threading.Tasks.Task<unit>>()

    let failure = System.InvalidOperationException("shared cleanup fault")
    let statuses = System.Collections.Concurrent.ConcurrentBag<PluginStatus>()

    let services =
        { defaultServices with
            StartAsync = fun work -> launched.SetResult(Async.StartAsTask(work))
            ReportStatus = fun _ status -> statuses.Add(status)
            ReleaseSharedRun = fun _ _ -> raise failure }

    let registration =
        registerHandler services (sharedWakeHandler "cleanup-failure" (fun _ -> async { return SharedFinished }))

    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)

    let worker =
        launched.Task.WaitAsync(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

    // Observe the worker's terminal state even on the broken implementation, so
    // an escaped callback exception cannot terminate the test host.
    try
        worker.WaitAsync(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
    with :? System.InvalidOperationException ->
        ()

    Assert.False(registration.IsBusy(), "cleanup failure must resolve local ownership")
    Assert.True(registration.Fault().IsSome, "failed cleanup must retain failure in the owner snapshot")
    Assert.Same(failure, registration.Fault().Value)

    Assert.Contains(
        statuses,
        fun status ->
            match status with
            | Failed(summary, _, _) -> summary.Contains("shared cleanup fault")
            | _ -> false
    )

    Assert.DoesNotContain(
        statuses,
        fun status ->
            match status with
            | Completed _ -> true
            | _ -> false
    )

[<Fact(Timeout = 15000)>]
let ``throwing shared startup failure mapper still releases both owners`` () =
    let mutable capturedCtx: PluginCtx<SharedWakeMsg> option = None
    let releases = System.Collections.Concurrent.ConcurrentBag<SharedResourceState>()
    let statuses = System.Collections.Concurrent.ConcurrentBag<PluginStatus>()

    let services =
        { defaultServices with
            StartAsync = fun _ -> raise (System.InvalidOperationException("launch fault"))
            ReportStatus = fun _ status -> statuses.Add(status)
            ReleaseSharedRun = fun _ state -> releases.Add(state) }

    let original =
        sharedWakeHandler "failure-mapper" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun ctx state _ ->
                    capturedCtx <- Some ctx
                    async { return state } }

    let registration = registerHandler services handler
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)

    try
        capturedCtx.Value.RunExclusiveShared
            "work"
            "artifacts"
            (fun _ -> async { return SharedFinished })
            (fun _ -> Ready)
            (fun _ -> raise (System.InvalidOperationException("failure mapper fault")))
        |> function
            | SharedClaimed -> ()
            | claim -> failwithf "unexpected shared claim: %A" claim
    with :? System.InvalidOperationException ->
        ()

    Assert.False(registration.IsBusy(), "throwing failure mapper must not strand the local owner")
    Assert.Single(releases) |> ignore

    Assert.Contains(
        statuses,
        fun status ->
            match status with
            | Failed(summary, _, _) -> summary.Contains("failure mapper fault")
            | _ -> false
    )

[<Fact(Timeout = 15000)>]
let ``shared startup failure retains local ownership through resource handoff`` () =
    use entered = new System.Threading.ManualResetEventSlim(false)
    use release = new System.Threading.ManualResetEventSlim(false)
    let mutable capturedCtx: PluginCtx<SharedWakeMsg> option = None

    let services =
        { defaultServices with
            StartAsync = fun _ -> raise (System.InvalidOperationException("launch fault"))
            ReleaseSharedRun =
                fun _ _ ->
                    entered.Set()
                    Assert.True(release.Wait(10000), "fixture must release shared handoff") }

    let original =
        sharedWakeHandler "startup-handoff" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun ctx state event ->
                    capturedCtx <- Some ctx

                    match event with
                    | FileChanged _ -> async { return state }
                    | _ -> original.Update ctx state event }

    let registration = registerHandler services handler
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
    let ctx = capturedCtx.Value

    let attempt =
        System.Threading.Tasks.Task.Run(fun () ->
            ctx.RunExclusiveShared
                "work"
                "artifacts"
                (fun _ -> async { return SharedFinished })
                (fun _ -> Ready)
                (fun ex -> SharedFailed ex.Message))

    try
        Assert.True(entered.Wait(5000), "startup cleanup must reach the controlled handoff")

        Assert.True(
            ctx.SlotHolder "work" = SlotHolder.LiveRun,
            "startup failure still owns the slot until handoff finishes"
        )

        Assert.True(registration.IsBusy(), "handoff remains an outstanding obligation")
    finally
        release.Set()

        let claim =
            attempt.WaitAsync(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        test <@ claim = SharedClaimed @>
        waitUntil (fun () -> not (registration.IsBusy())) 5000

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``shared classification or release failure retires work without folding a success`` releaseFails =
    let failure = System.IO.IOException("shared completion failed")
    let released = System.Threading.Tasks.TaskCompletionSource<SharedResourceState>()
    let folded = System.Threading.Tasks.TaskCompletionSource<unit>()
    let scheduler = SharedRunScheduler()

    let original =
        sharedWakeHandlerWithClassifier "shared-completion-failure" (fun _ -> async.Return SharedFinished) (fun _ ->
            if releaseFails then Ready else raise failure)

    let handler =
        { original with
            Update =
                fun ctx state event ->
                    async {
                        match event with
                        | Custom _ -> folded.TrySetResult(()) |> ignore
                        | _ -> ()

                        return! original.Update ctx state event
                    } }

    let registration =
        registerHandler
            { defaultServices with
                ClaimOrQueueSharedRun = fun key start -> scheduler.ClaimOrQueue(key, start)
                ReleaseSharedRun =
                    fun key state ->
                        scheduler.Release(key, state)
                        released.TrySetResult(state) |> ignore

                        if releaseFails then
                            raise failure }
            handler

    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)

    let resource =
        released.Task.WaitAsync(System.TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()

    waitUntil (fun () -> not (registration.IsBusy())) 5000
    Assert.False(registration.IsBusy(), "failed shared completion must retire its local obligation")
    Assert.Same(failure, registration.Fault().Value)
    Assert.False(folded.Task.IsCompleted, "a failed classifier or handoff cannot publish the success result")

    match resource with
    | Ready -> Assert.True(releaseFails)
    | Invalid reason -> Assert.Contains("classifier faulted", reason)

    Assert.Equal(Some resource, scheduler.ClaimOrQueue("artifacts", fun _ -> SharedStarted))
    scheduler.Release("artifacts", Ready)

[<Fact(Timeout = 15000)>]
let ``failed shared launch retains cleanup failure instead of publishing mapped completion`` () =
    let failure = System.IO.IOException("shared launch cleanup failed")
    let mapped = System.Threading.Tasks.TaskCompletionSource<unit>()

    let original =
        sharedWakeHandler "launch-release-failure" (fun _ -> async.Return SharedFinished)

    let handler =
        { original with
            Update =
                fun ctx state event ->
                    async {
                        match event with
                        | FileChanged _ ->
                            let claim =
                                ctx.RunExclusiveShared
                                    "work"
                                    "artifacts"
                                    (fun _ -> async.Return SharedFinished)
                                    (fun _ -> Ready)
                                    (fun _ ->
                                        mapped.TrySetResult(()) |> ignore
                                        SharedFinished)

                            Assert.Equal(SharedClaimed, claim)
                        | _ -> ()

                        return state
                    } }

    let registration =
        registerHandler
            { defaultServices with
                StartAsync = fun _ -> raise (System.InvalidOperationException("launcher failed"))
                ReleaseSharedRun = fun _ _ -> raise failure }
            handler

    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
    Assert.False(registration.IsBusy())
    Assert.Same(failure, registration.Fault().Value)
    Assert.False(mapped.Task.IsCompleted, "failed cleanup must not mint a completion message")

[<Fact(Timeout = 15000)>]
let ``registration with suppressed execution context still owns and completes exclusive work`` () =
    let completed = System.Threading.Tasks.TaskCompletionSource<unit>()

    let original =
        sharedWakeHandler "suppressed-owner-context" (fun _ -> async.Return SharedFinished)

    let handler =
        { original with
            Update =
                fun ctx state event ->
                    async {
                        match event with
                        | Custom SharedFinished -> completed.TrySetResult(()) |> ignore
                        | _ -> ()

                        return! original.Update ctx state event
                    } }

    let registration =
        use suppression = System.Threading.ExecutionContext.SuppressFlow()
        Assert.True(System.Threading.ExecutionContext.IsFlowSuppressed())
        registerDefault handler

    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
    completed.Task.WaitAsync(System.TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
    waitUntil (fun () -> not (registration.IsBusy())) 5000
    Assert.False(registration.IsBusy())
    Assert.True(registration.Fault().IsNone)

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``a terminal before shared admission cannot cache work that is claimed or queued`` queued =
    let scheduler = SharedRunScheduler()

    if queued then
        Assert.Equal(Some Ready, scheduler.ClaimOrQueue("artifacts", fun _ -> SharedStarted))

    let release = System.Threading.Tasks.TaskCompletionSource<unit>()
    let entered = System.Threading.Tasks.TaskCompletionSource<unit>()
    let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
    let key = ContentHash.create "shared-cache-owner"

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "shared-cache-owner"
          Init = 0
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(PluginStatus.completedNow "prior result" System.TimeSpan.Zero)

                        let work _ =
                            async {
                                entered.TrySetResult(()) |> ignore
                                do! release.Task |> Async.AwaitTask
                                return ()
                            }

                        let claim =
                            ctx.RunExclusiveShared "work" "artifacts" work (fun _ -> Ready) (fun ex -> raise ex)

                        Assert.Equal((if queued then SharedQueued else SharedClaimed), claim)

                        let refused =
                            ctx.RunExclusiveShared "work" "artifacts" work (fun _ -> Ready) (fun ex -> raise ex)

                        Assert.Equal(LocalSlotBusy, refused)
                    | Custom _ -> ctx.ReportStatus(PluginStatus.completedNow "actual work" System.TimeSpan.Zero)
                    | _ -> ()

                    return state + 1
                }
          PrepareCommit = None
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey =
            Some(fun _ event ->
                match event with
                | FileChanged _ -> Some key
                | _ -> None)
          Teardown = None }

    let registration =
        registerHandler
            { defaultServices with
                TaskCache = Some cache
                ClaimOrQueueSharedRun = fun resource start -> scheduler.ClaimOrQueue(resource, start)
                ReleaseSharedRun = fun resource state -> scheduler.Release(resource, state) }
            handler

    try
        dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
        Assert.True(registration.IsBusy())

        Assert.True(
            (cache.TryGet
                { Plugin = "shared-cache-owner"
                  File = None }
                key)
                .IsNone
        )

        if queued then
            Assert.False(entered.Task.IsCompleted, "a queued worker must wait for its shared resource")
    finally
        if queued then
            scheduler.Release("artifacts", Ready)

        release.TrySetResult(()) |> ignore
        waitUntil (fun () -> not (registration.IsBusy())) 5000

    Assert.True(entered.Task.IsCompleted, "the admitted worker must actually run")
    Assert.False(registration.IsBusy())
    Assert.True(registration.Fault().IsNone)

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``executor fault preserves a live worker until cleanup and suppresses its completion fold`` failedStartup =
    let owner = PluginWorkOwner.Owner(0)
    let failure = System.IO.IOException("executor stopped while work was owned")
    use entered = new System.Threading.ManualResetEventSlim(false)
    use release = new System.Threading.ManualResetEventSlim(false)
    let folded = System.Threading.Tasks.TaskCompletionSource<unit>()
    let mutable captured: PluginCtx<SharedWakeMsg> option = None

    let handler: PluginHandler<int, SharedWakeMsg> =
        { Name = PluginName.create "faulted-executor-completion"
          Init = 0
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> captured <- Some ctx
                    | Custom _ -> folded.TrySetResult(()) |> ignore
                    | _ -> ()

                    return state
                }
          PrepareCommit = None
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let services =
        { defaultServices with
            StartAsync =
                fun work ->
                    if failedStartup then
                        raise (System.InvalidOperationException("start failed"))
                    else
                        Async.Start work
            ReleaseSharedRun =
                fun _ _ ->
                    entered.Set()
                    Assert.True(release.Wait(10000), "fixture must release actual startup cleanup") }

    let registration = registerHandlerForOwner owner services handler
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)

    let attempt =
        System.Threading.Tasks.Task.Run(fun () ->
            if failedStartup then
                Assert.Equal(
                    SharedClaimed,
                    captured.Value.RunExclusiveShared
                        "work"
                        "artifacts"
                        (fun _ -> async.Return SharedFinished)
                        (fun _ -> Ready)
                        (fun _ -> SharedFinished)
                )
            else
                Assert.Equal(
                    Claimed,
                    captured.Value.RunExclusive
                        "work"
                        (async {
                            entered.Set()
                            Assert.True(release.Wait(10000), "fixture must release actual work")
                            return SharedFinished
                        })
                ))

    try
        Assert.True(entered.Wait(5000), "work or shared cleanup must actually start")
        owner.FaultExecutor failure
        Assert.True(owner.Snapshot.IsRunning "work")
        Assert.True(registration.IsBusy(), "faulting the executor does not invent worker cleanup")
    finally
        release.Set()
        attempt.WaitAsync(System.TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        waitUntil (fun () -> not (registration.IsBusy())) 5000

    Assert.False(registration.IsBusy())
    Assert.False(folded.Task.IsCompleted, "a stopped executor cannot fold the completed result")
    Assert.Same(failure, registration.Fault().Value)
    Assert.Equal(0, owner.Snapshot.State)
    Assert.Equal(1L, registration.CompletedDispatches())

type private IntentMsg =
    | IntentRun
    | IntentRunDone
    | IntentApplied

[<Fact(Timeout = 15000)>]
let ``an exclusive intent waits behind the running key and acknowledges its own commit`` () =
    let release =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let applied = System.Collections.Concurrent.ConcurrentQueue<string>()
    let mutable request: CommandHandler option = None

    let handler: PluginHandler<int, IntentMsg> =
        { Name = PluginName.create "exclusive-intent"
          Init = 0
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        let claim =
                            ctx.RunExclusive
                                "work"
                                (async {
                                    do! release.Task |> Async.AwaitTask
                                    return IntentRunDone
                                })

                        test <@ claim = Claimed @>
                        return state
                    | Custom IntentRunDone ->
                        applied.Enqueue "run"
                        return state + 1
                    | Custom IntentApplied ->
                        applied.Enqueue "intent"
                        return state + 10
                    | _ -> return state
                }
          Commands =
            [ "apply",
              PluginCommand.Request(fun ctx _ ->
                  async {
                      do! ctx.EnqueueExclusiveIntent "work" None IntentApplied |> Async.AwaitTask
                      return "applied"
                  }) ]
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let registration =
        registerWith handler (Some(fun (_, command) -> request <- Some command))

    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)
    let reply = request.Value [||] |> Async.StartAsTask

    try
        Assert.False(reply.Wait 200, "the intent must wait while the run holds its key")
        Assert.True(applied.IsEmpty)
    finally
        release.TrySetResult(()) |> ignore

    Assert.Equal("applied", reply.WaitAsync(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())
    // The acknowledgement follows the intent's own commit, after the run's result fold.
    Assert.Equal<string list>([ "run"; "intent" ], List.ofSeq applied)
    waitUntil (fun () -> not (registration.IsBusy())) 5000
    Assert.False(registration.IsBusy())

[<Fact(Timeout = 15000)>]
let ``a claim whose Running report throws retires the run with that failure`` () =
    let failure = System.InvalidOperationException("status transport refused Running")
    let mutable captured: PluginCtx<SharedWakeMsg> option = None

    let services =
        { defaultServices with
            ReportStatus =
                fun _ status ->
                    match status with
                    | Running _ -> raise failure
                    | _ -> () }

    let original =
        sharedWakeHandler "running-report-failure" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun ctx state _ ->
                    captured <- Some ctx
                    async { return state } }

    let registration = registerHandler services handler
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)

    let thrown =
        Assert.Throws<System.InvalidOperationException>(
            System.Action(fun () ->
                match captured.Value.RunExclusive "work" (async { return SharedFinished }) with
                | Claimed -> failwith "a claim whose Running report failed must not return Claimed"
                | SlotBusy -> failwith "the key was free")
        )

    Assert.Same(failure, thrown)
    Assert.Equal(SlotHolder.Free, captured.Value.SlotHolder "work")
    Assert.False(registration.IsBusy())
    Assert.Same(failure, registration.Fault().Value)

[<Fact(Timeout = 15000)>]
let ``a run failure that cannot be reported still retires the run with its own failure`` () =
    let workFailure = System.InvalidOperationException("exclusive work failed")

    let services =
        { defaultServices with
            ReportStatus =
                fun _ status ->
                    match status with
                    | Failed _ -> raise (System.IO.IOException("status sink failed"))
                    | _ -> () }

    let mutable captured: PluginCtx<SharedWakeMsg> option = None

    let original =
        sharedWakeHandler "unreportable-run-failure" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun ctx state _ ->
                    captured <- Some ctx
                    async { return state } }

    let registration = registerHandler services handler
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)

    // Claimed outside the quotation: Unquote evaluates by reflection and would wrap the
    // work's exception.
    let claim = captured.Value.RunExclusive "work" (async { return raise workFailure })
    test <@ claim = Claimed @>
    waitUntil (fun () -> registration.Fault().IsSome && not (registration.IsBusy())) 5000
    Assert.False(registration.IsBusy())
    Assert.Same(workFailure, registration.Fault().Value)

[<Fact(Timeout = 15000)>]
let ``shared work whose failure mapper throws invalidates the resource and fails the run`` () =
    let workFailure = System.InvalidOperationException("shared work failed")
    let mapperFailure = System.InvalidOperationException("failure mapper failed")
    let released = System.Threading.Tasks.TaskCompletionSource<SharedResourceState>()
    let mutable captured: PluginCtx<SharedWakeMsg> option = None

    let services =
        { defaultServices with
            ReleaseSharedRun = fun _ state -> released.TrySetResult state |> ignore }

    let original =
        sharedWakeHandler "shared-mapper-failure" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun ctx state _ ->
                    captured <- Some ctx
                    async { return state } }

    let registration = registerHandler services handler
    dispatchAndAwait registration (DispatchFileChanged SolutionChanged)

    let claim =
        captured.Value.RunExclusiveShared
            "work"
            "artifacts"
            (fun _ -> async { return raise workFailure })
            (fun _ -> Ready)
            (fun _ -> raise mapperFailure)

    test <@ claim = SharedClaimed @>

    let resource =
        released.Task.WaitAsync(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

    test <@ resource = Invalid "shared-mapper-failure shared work faulted" @>
    waitUntil (fun () -> not (registration.IsBusy())) 5000
    Assert.Same(mapperFailure, registration.Fault().Value)

[<Fact(Timeout = 15000)>]
let ``untracked dispatch to a stopped executor drops the event without throwing`` () =
    let owner = PluginWorkOwner.Owner(0)
    let failure = System.IO.IOException("executor stopped")

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "stopped-executor-dispatch"
          Init = 0
          Update = fun _ state _ -> async.Return(state + 1)
          PrepareCommit = None
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let registration = registerHandlerForOwner owner defaultServices handler
    owner.FaultExecutor failure

    registration.Dispatch(DispatchFileChanged SolutionChanged)

    Assert.Same(
        failure,
        Assert.Throws<System.IO.IOException>(fun () ->
            registration.DispatchTracked(DispatchFileChanged SolutionChanged) |> ignore)
    )

    Assert.False(registration.IsBusy())
    Assert.Equal(0, owner.Snapshot.State)
