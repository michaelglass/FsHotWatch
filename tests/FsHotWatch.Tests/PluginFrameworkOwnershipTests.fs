/// Events, commits and commands under the plugin's work owner: a receipt acknowledges
/// only its own committed state, and commands read the published snapshot.
module FsHotWatch.Tests.PluginFrameworkOwnershipTests

open Xunit
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.PluginFrameworkFixtures

[<Fact(Timeout = 15000)>]
let ``executor fault fails accepted event receipts instead of abandoning their waiters`` () =
    use entered = new System.Threading.ManualResetEventSlim(false)
    use release = new System.Threading.ManualResetEventSlim(false)

    let services =
        { defaultServices with
            ReportStatus = fun _ _ -> raise (System.InvalidOperationException("status transport fault")) }

    let original =
        sharedWakeHandler "executor-fault" (fun _ -> async { return SharedFinished })

    let handler =
        { original with
            Update =
                fun _ _ _ ->
                    async {
                        entered.Set()
                        Assert.True(release.Wait(10000), "fixture must release the failing event")
                        return raise (System.InvalidOperationException("update fault"))
                    } }

    let registration = registerHandler services handler
    let first = registration.DispatchTracked(DispatchFileChanged SolutionChanged).Value

    let queued =
        try
            Assert.True(entered.Wait(5000), "the first event must reach its controlled update")
            registration.DispatchTracked(DispatchFileChanged SolutionChanged).Value
        finally
            release.Set()

    waitUntil (fun () -> registration.Fault().IsSome) 5000
    Assert.True(registration.Fault().IsSome, "the failed update must stay recorded in the owner snapshot")

    for receipt in [ first; queued ] do
        Assert.Throws<System.InvalidOperationException>(fun () ->
            receipt.Wait(System.TimeSpan.FromSeconds 1.0).GetAwaiter().GetResult())
        |> ignore

[<Fact(Timeout = 20000)>]
let ``read command returns committed snapshot while an update is blocked`` () =
    // A read queued behind Update hangs until unrelated work finishes. The approved
    // snapshot contract requires the last committed state to remain readable.
    let entered =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let release =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "snapshot-during-update"
          Init = 42
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        entered.TrySetResult(()) |> ignore
                        do! release.Task |> Async.AwaitTask
                        return state + 1
                    | _ -> return state
                }
          Commands =
            [ "read-count", fun _ctx state _args -> async { return $"%d{state}" } ]
            |> List.map (fun (name, callback) -> name, FsHotWatch.PluginFramework.PluginCommand.Observe callback)
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let registration =
        registerWith handler (Some(fun command -> registeredCmd <- Some command))

    let (_, readCount) = registeredCmd.Value

    let receipt =
        registration.DispatchTracked(DispatchFileChanged(SourceChanged [ "/tmp/repo/Snapshot.fs" ]))

    Assert.True(receipt.IsSome)
    let committedEvent = receipt.Value.Wait(System.TimeSpan.FromSeconds 10.0)

    try
        Assert.True(entered.Task.Wait(5000), "the update must enter before probing the read")
        let read = readCount [||] |> Async.StartAsTask
        Assert.True(read.Wait(2000), "read command waited behind the blocked update instead of reading a snapshot")
        Assert.Equal("42", read.Result)
        Assert.False(committedEvent.IsCompleted, "a blocked update has not committed")
    finally
        release.TrySetResult(()) |> ignore
        waitUntil (fun () -> not (registration.IsBusy())) 5000

    // The exact event receipt follows publication; unrelated progress cannot satisfy it.
    committedEvent.GetAwaiter().GetResult()
    Assert.False(registration.IsBusy())
    let committed = readCount [||] |> Async.RunSynchronously
    Assert.Equal("43", committed)

[<Fact(Timeout = 20000)>]
let ``a completed event receipt cannot acknowledge a different blocked event`` () =
    let entered = System.Threading.Tasks.TaskCompletionSource<unit>()
    let release = System.Threading.Tasks.TaskCompletionSource<unit>()
    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "distinct-dispatch-receipts"
          Init = 0
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged [ "/blocked" ]) ->
                        entered.TrySetResult(()) |> ignore
                        do! release.Task |> Async.AwaitTask
                        return state + 1
                    | FileChanged _ -> return state + 1
                    | _ -> return state
                }
          Commands = [ "count", PluginCommand.Observe(fun _ state _ -> async { return string state }) ]
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let registration =
        registerWith handler (Some(fun command -> registeredCmd <- Some command))

    let first =
        registration.DispatchTracked(DispatchFileChanged(SourceChanged [ "/first" ]))

    Assert.True(first.IsSome)
    first.Value.Wait(System.TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

    let second =
        registration.DispatchTracked(DispatchFileChanged(SourceChanged [ "/blocked" ]))

    Assert.True(second.IsSome)
    let secondCommit = second.Value.Wait(System.TimeSpan.FromSeconds 10.0)
    let read = snd registeredCmd.Value

    try
        Assert.True(entered.Task.Wait(5000))
        Assert.False(secondCommit.IsCompleted)
        Assert.True(registration.IsBusy())
        Assert.Equal("1", read [||] |> Async.RunSynchronously)
    finally
        release.TrySetResult(()) |> ignore
        secondCommit.GetAwaiter().GetResult()

    Assert.Equal("2", read [||] |> Async.RunSynchronously)

[<Fact(Timeout = 20000)>]
let ``request posts intent without waiting for a state snapshot`` () =
    let entered = System.Threading.Tasks.TaskCompletionSource<unit>()
    let release = System.Threading.Tasks.TaskCompletionSource<unit>()
    let committed = System.Threading.Tasks.TaskCompletionSource<unit>()
    let mutable registeredCmd: (string * CommandHandler) option = None

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "request-during-update"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        entered.TrySetResult(()) |> ignore
                        do! release.Task |> Async.AwaitTask
                    | Custom() -> committed.TrySetResult(()) |> ignore
                    | _ -> ()

                    return state
                }
          Commands =
            [ "request",
              PluginCommand.Request(fun ctx _args ->
                  async {
                      ctx.Post()
                      return "accepted"
                  }) ]
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let registration =
        registerWith handler (Some(fun command -> registeredCmd <- Some command))

    registration.Dispatch(DispatchFileChanged(SourceChanged [ "/tmp/repo/Request.fs" ]))
    let request = snd registeredCmd.Value
    let mutable invocation: System.Threading.Tasks.Task<string> option = None

    try
        Assert.True(entered.Task.Wait(5000), "the preceding update must enter")
        let pending = request [||] |> Async.StartAsTask
        invocation <- Some pending
        Assert.True(pending.Wait(2000), "request waited for a state snapshot before posting intent")
        Assert.Equal("accepted", pending.Result)
        Assert.False(committed.Task.IsCompleted, "acceptance is not an owner commit")
    finally
        release.TrySetResult(()) |> ignore

        invocation
        |> Option.iter (fun pending -> Assert.True(pending.Wait(5000), "the request must drain"))

        Assert.True(committed.Task.Wait(5000), "the posted intent must reach its owner")
        waitUntil (fun () -> not (registration.IsBusy())) 5000

[<Fact(Timeout = 15000)>]
let ``a failed update cannot acknowledge its event as committed`` () =
    let failure = System.InvalidOperationException("fixture update refused publication")
    let entered = System.Threading.Tasks.TaskCompletionSource<unit>()

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "failed-commit-receipt"
          Init = 17
          Update =
            fun _ _ _ ->
                async {
                    entered.TrySetResult(()) |> ignore
                    return raise failure
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let registration = registerDefault handler

    let receipt =
        registration.DispatchTracked(DispatchFileChanged(SourceChanged [ "/tmp/repo/Commit.fs" ]))
        |> Option.defaultWith (fun () -> failwith "subscribed event must be admitted")

    let settled = receipt.Wait(System.TimeSpan.FromSeconds 5.0)
    Assert.True(entered.Task.Wait(5000), "actual update must enter before observing its receipt")

    let winner =
        System.Threading.Tasks.Task.WhenAny(settled, System.Threading.Tasks.Task.Delay(5000)).GetAwaiter().GetResult()

    Assert.Same(settled, winner)

    let observed =
        Assert.Throws<System.InvalidOperationException>(fun () -> settled.GetAwaiter().GetResult())

    Assert.Same(failure, observed)

[<Fact(Timeout = 20000)>]
let ``event receipt and cache wait for durable finalization after candidate publication`` () =
    let signal () =
        System.Threading.Tasks.TaskCompletionSource<unit>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        )

    let preparing, prepared, finalizing, finalized =
        signal (), signal (), signal (), signal ()

    let mutable readCommand: CommandHandler option = None
    let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
    let name = PluginName.create "durable-commit"
    let key = ContentHash.create "durable-commit-key"

    let handler: PluginHandler<int, unit> =
        { Name = name
          Init = 4
          Update =
            fun ctx state _ ->
                async {
                    ctx.ReportStatus(PluginStatus.completedNow "candidate" System.TimeSpan.Zero)
                    return state + 1
                }
          PrepareCommit =
            Some(fun prior candidate ->
                async {
                    Assert.Equal(4, prior)
                    Assert.Equal(5, candidate)
                    preparing.TrySetResult(()) |> ignore
                    do! prepared.Task |> Async.AwaitTask

                    return
                        { Finalize =
                            async {
                                finalizing.TrySetResult(()) |> ignore
                                do! finalized.Task |> Async.AwaitTask
                            } }
                })
          Commands = [ "read", PluginCommand.Observe(fun _ state _ -> async { return string state }) ]
          Subscriptions = Set.singleton SubscribeBuildCompleted
          CacheKey = Some(fun _ _ -> Some key)
          Teardown = None }

    let registration =
        registerHandler
            { defaultServices with
                TaskCache = Some cache
                RegisterCommand = fun (_, command) -> readCommand <- Some command }
            handler

    let receipt =
        registration.DispatchTracked(DispatchBuildCompleted BuildSucceeded).Value.Wait(System.TimeSpan.FromSeconds 15.0)

    let read () =
        readCommand.Value [||] |> Async.RunSynchronously

    let composite: TaskCache.CompositeKey =
        { Plugin = PluginName.value name
          File = None }

    try
        Assert.True(preparing.Task.Wait(5000), "actual prepare must enter")
        Assert.Equal("4", read ())
        Assert.True(registration.IsBusy())
        Assert.False(receipt.IsCompleted)
        Assert.True((cache.TryGet composite key).IsNone)
        prepared.TrySetResult(()) |> ignore
        Assert.True(finalizing.Task.Wait(5000), "actual finalizer must enter")
        Assert.Equal("5", read ())
        Assert.True(registration.IsBusy())
        Assert.False(receipt.IsCompleted)
        Assert.True((cache.TryGet composite key).IsNone)
    finally
        prepared.TrySetResult(()) |> ignore
        finalized.TrySetResult(()) |> ignore
        receipt.GetAwaiter().GetResult()

    Assert.False(registration.IsBusy())
    Assert.Equal(1L, registration.CompletedDispatches())
    Assert.True((cache.TryGet composite key).IsSome, "successful hosted commit must persist its cache candidate")

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``durable commit failures fault the actual receipt and retain the appropriate publication`` (failFinalize: bool) =
    let failure = System.IO.IOException("fixture durable write refused")
    let mutable readCommand: CommandHandler option = None
    let mutable shouldFail = true

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "durable-failure"
          Init = 4
          Update = fun _ state _ -> async { return state + 1 }
          PrepareCommit =
            Some(fun _ _ ->
                async {
                    if not shouldFail then return { Finalize = async.Return() }
                    elif not failFinalize then return raise failure
                    else return { Finalize = async { return raise failure } }
                })
          Commands = [ "read", PluginCommand.Observe(fun _ state _ -> async { return string state }) ]
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let registration =
        registerWith handler (Some(fun (_, command) -> readCommand <- Some command))

    let receipt =
        registration
            .DispatchTracked(DispatchFileChanged(SourceChanged [ "/tmp/repo/Commit.fs" ]))
            .Value.Wait(System.TimeSpan.FromSeconds 5.0)

    let winner =
        System.Threading.Tasks.Task.WhenAny(receipt, System.Threading.Tasks.Task.Delay(5000)).GetAwaiter().GetResult()

    Assert.Same(receipt, winner)
    Assert.Same(failure, Assert.Throws<System.IO.IOException>(fun () -> receipt.GetAwaiter().GetResult()))
    Assert.Same(failure, registration.Fault().Value)
    Assert.Equal((if failFinalize then "5" else "4"), readCommand.Value [||] |> Async.RunSynchronously)
    Assert.Equal(0L, registration.CompletedDispatches())

    // An ordinary success cannot clear durable failure. A real successful
    // uncached prepared commit must recover both publication and its receipt.
    shouldFail <- false
    dispatchAndAwait registration (DispatchFileChanged(SourceChanged [ "/tmp/repo/Commit.fs" ]))
    Assert.True(registration.Fault().IsNone)
    Assert.Equal((if failFinalize then "6" else "5"), readCommand.Value [||] |> Async.RunSynchronously)
    Assert.Equal(1L, registration.CompletedDispatches())
    Assert.False(registration.IsBusy())

[<Fact(Timeout = 15000)>]
let ``ordinary cache write sees published candidate while its event is still owned`` () =
    let inner = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
    let writing = System.Threading.Tasks.TaskCompletionSource<unit>()
    let release = System.Threading.Tasks.TaskCompletionSource<unit>()

    let cache =
        { new TaskCache.ITaskCache with
            member _.TryGet composite key = inner.TryGet composite key
            member _.Lookup composite key = inner.Lookup composite key

            member _.Set composite key value =
                writing.TrySetResult(()) |> ignore
                release.Task.GetAwaiter().GetResult()
                inner.Set composite key value

            member _.Clear() = inner.Clear()
            member _.ClearPlugin name = inner.ClearPlugin name
            member _.ClearFile file = inner.ClearFile file
            member _.ClearPluginFile name file = inner.ClearPluginFile name file }

    let mutable readCommand: CommandHandler option = None

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "ordinary-cache-commit"
          Init = 1
          Update =
            fun ctx state _ ->
                async {
                    ctx.ReportStatus(PluginStatus.completedNow "completed" System.TimeSpan.Zero)
                    return state + 1
                }
          PrepareCommit = None
          Commands = [ "read", PluginCommand.Observe(fun _ state _ -> async { return string state }) ]
          Subscriptions = Set.singleton SubscribeBuildCompleted
          CacheKey = Some(fun _ _ -> Some(ContentHash.create "ordinary-key"))
          Teardown = None }

    let registration =
        registerHandler
            { defaultServices with
                TaskCache = Some cache
                RegisterCommand = fun (_, command) -> readCommand <- Some command }
            handler

    let receipt =
        registration.DispatchTracked(DispatchBuildCompleted BuildSucceeded).Value.Wait(System.TimeSpan.FromSeconds 10.0)

    try
        Assert.True(writing.Task.Wait(5000), "actual cache write must enter")
        Assert.Equal("2", readCommand.Value [||] |> Async.RunSynchronously)
        Assert.True(registration.IsBusy())
        Assert.False(receipt.IsCompleted)
        Assert.Equal(0L, registration.CompletedDispatches())
    finally
        release.TrySetResult(()) |> ignore
        receipt.GetAwaiter().GetResult()

    Assert.Equal(1L, registration.CompletedDispatches())

[<Fact(Timeout = 15000)>]
let ``failed diagnostic reporting cannot retire an event twice or kill its executor`` () =
    let failure = System.InvalidOperationException("original update failed")
    let reported = System.Threading.Tasks.TaskCompletionSource<unit>()

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "failure-reporting"
          Init = 0
          Update =
            fun _ state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged [ "/fail" ]) -> return raise failure
                    | _ -> return state + 1
                }
          PrepareCommit = None
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          Teardown = None }

    let registration =
        registerHandler
            { defaultServices with
                ReportStatus =
                    fun _ _ ->
                        reported.TrySetResult(()) |> ignore
                        raise (System.IO.IOException("diagnostic sink failed")) }
            handler

    let first =
        registration
            .DispatchTracked(DispatchFileChanged(SourceChanged [ "/fail" ]))
            .Value.Wait(System.TimeSpan.FromSeconds 5.0)

    Assert.Same(failure, Assert.Throws<System.InvalidOperationException>(fun () -> first.GetAwaiter().GetResult()))
    Assert.True(reported.Task.Wait(5000), "failure reporting must actually be attempted")
    dispatchAndAwait registration (DispatchFileChanged(SourceChanged [ "/succeed" ]))
    Assert.Equal(1L, registration.CompletedDispatches())
    Assert.True(registration.Fault().IsNone, "genuine successful work supersedes the failed update")

[<Fact(Timeout = 15000)>]
let ``terminal reported by an update that later fails cannot prepare or populate cache`` () =
    let failure =
        System.InvalidOperationException("candidate rejected after terminal notification")

    let preparation = System.Threading.Tasks.TaskCompletionSource<unit>()
    let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
    let key = ContentHash.create "failed-update-effects"

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "failed-update-effects"
          Init = 0
          Update =
            fun ctx _ _ ->
                async {
                    ctx.ReportStatus(PluginStatus.completedNow "uncommitted candidate" System.TimeSpan.Zero)
                    return raise failure
                }
          PrepareCommit =
            Some(fun _ _ ->
                async {
                    preparation.TrySetResult(()) |> ignore
                    return { Finalize = async.Return() }
                })
          Commands = []
          Subscriptions = Set.singleton SubscribeBuildCompleted
          CacheKey = Some(fun _ _ -> Some key)
          Teardown = None }

    let registration =
        registerHandler
            { defaultServices with
                TaskCache = Some cache }
            handler

    let receipt =
        registration.DispatchTracked(DispatchBuildCompleted BuildSucceeded).Value.Wait(System.TimeSpan.FromSeconds 5.0)

    Assert.Same(failure, Assert.Throws<System.InvalidOperationException>(fun () -> receipt.GetAwaiter().GetResult()))
    Assert.False(preparation.Task.IsCompleted)

    Assert.True(
        (cache.TryGet
            { Plugin = "failed-update-effects"
              File = None }
            key)
            .IsNone
    )

    Assert.Equal(0L, registration.CompletedDispatches())

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``cache write failure preserves published state and faults the actual event receipt`` prepared =
    let failure = System.IO.IOException("cache persistence failed")
    let inner = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache

    let cache =
        { new TaskCache.ITaskCache with
            member _.TryGet composite key = inner.TryGet composite key
            member _.Lookup composite key = inner.Lookup composite key
            member _.Set _ _ _ = raise failure
            member _.Clear() = inner.Clear()
            member _.ClearPlugin name = inner.ClearPlugin name
            member _.ClearFile file = inner.ClearFile file
            member _.ClearPluginFile name file = inner.ClearPluginFile name file }

    let mutable readCommand: CommandHandler option = None
    let key = ContentHash.create "failed-cache-write"

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "failed-cache-write"
          Init = 1
          Update =
            fun ctx state _ ->
                async {
                    ctx.ReportStatus(PluginStatus.completedNow "candidate" System.TimeSpan.Zero)
                    return state + 1
                }
          PrepareCommit =
            if prepared then
                Some(fun _ _ -> async.Return { Finalize = async.Return() })
            else
                None
          Commands = [ "read", PluginCommand.Observe(fun _ state _ -> async.Return(string state)) ]
          Subscriptions = Set.singleton SubscribeBuildCompleted
          CacheKey = Some(fun _ _ -> Some key)
          Teardown = None }

    let registration =
        registerHandler
            { defaultServices with
                TaskCache = Some cache
                RegisterCommand = fun (_, command) -> readCommand <- Some command }
            handler

    let receipt =
        registration.DispatchTracked(DispatchBuildCompleted BuildSucceeded).Value.Wait(System.TimeSpan.FromSeconds 5.)

    Assert.Same(failure, Assert.Throws<System.IO.IOException>(fun () -> receipt.GetAwaiter().GetResult()))
    Assert.Equal("2", readCommand.Value [||] |> Async.RunSynchronously)
    Assert.Same(failure, registration.Fault().Value)
    Assert.False(registration.IsBusy())
    Assert.Equal(0L, registration.CompletedDispatches())

    Assert.True(
        (cache.TryGet
            { Plugin = "failed-cache-write"
              File = None }
            key)
            .IsNone
    )

[<Fact(Timeout = 15000)>]
let ``external executor failure during finalization settles the original receipt only once`` () =
    let owner = PluginWorkOwner.Owner(0)
    let failure = System.IO.IOException("executor lost during durable finalization")
    let entered = System.Threading.Tasks.TaskCompletionSource<unit>()

    let handler: PluginHandler<int, unit> =
        { Name = PluginName.create "faulted-executor-commit"
          Init = 0
          Update = fun _ state _ -> async.Return(state + 1)
          PrepareCommit =
            Some(fun _ _ ->
                async.Return
                    { Finalize =
                        async {
                            owner.FaultExecutor failure
                            entered.TrySetResult(()) |> ignore
                            return raise failure
                        } })
          Commands = []
          Subscriptions = Set.singleton SubscribeBuildCompleted
          CacheKey = None
          Teardown = None }

    let registration = registerHandlerForOwner owner defaultServices handler

    let receipt =
        registration.DispatchTracked(DispatchBuildCompleted BuildSucceeded).Value.Wait(System.TimeSpan.FromSeconds 5.)

    Assert.Same(failure, Assert.Throws<System.IO.IOException>(fun () -> receipt.GetAwaiter().GetResult()))
    entered.Task.WaitAsync(System.TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
    Assert.Same(failure, registration.Fault().Value)
    Assert.False(registration.IsBusy())
    Assert.Equal(1, owner.Snapshot.State)
    Assert.Equal(0L, registration.CompletedDispatches())

    Assert.Same(
        failure,
        Assert.Throws<System.IO.IOException>(fun () ->
            registration.DispatchTracked(DispatchBuildCompleted BuildSucceeded) |> ignore)
    )
