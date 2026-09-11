module FsHotWatch.Tests.SupervisedWorkTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch

let private awaitResult (work: Task<unit>) =
    let winner =
        Task.WhenAny([| work :> Task; Task.Delay(TimeSpan.FromSeconds 5.0) |]).GetAwaiter().GetResult()

    Assert.True(
        obj.ReferenceEquals(work, winner),
        "the original operation receipt must settle within the observation bound"
    )

    work.GetAwaiter().GetResult()

[<Fact(Timeout = 20000)>]
let ``queued successor is owned before predecessor receipt completes`` () =
    let store = PluginWorkOwner.Store()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    use nextEntered = new ManualResetEventSlim(false)
    use nextRelease = new ManualResetEventSlim(false)
    let completions = Collections.Concurrent.ConcurrentQueue<int * bool>()

    let queue =
        SupervisedWork.Queue(
            store,
            "controlled",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state _ -> state),
            (fun state _ -> state),
            (fun state -> completions.Enqueue((state, store.Snapshot.IsBusy))),
            (fun state request _ _ ->
                async {
                    if request = 1 then
                        entered.Set()
                        Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                    else
                        nextEntered.Set()
                        Assert.True(nextRelease.Wait(TimeSpan.FromSeconds 10.0))

                    return state + request
                })
        )

    let first = queue.Submit(1, CancellationToken.None)
    let mutable second: Task<unit> option = None

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        let queued = queue.Submit(2, CancellationToken.None)
        second <- Some queued
        let pinned = store.Snapshot
        release.Set()
        awaitResult first
        Assert.True(nextEntered.Wait(TimeSpan.FromSeconds 5.0))
        Assert.True(store.Snapshot.IsBusy)
        Assert.False(queued.IsCompleted)
        Assert.Contains((1, true), completions)
        nextRelease.Set()
        awaitResult queued
        Assert.False(store.Snapshot.IsBusy)
        Assert.Equal(3, queue.State)
        Assert.Equal(2L, store.Snapshot.CompletedEvents)
        Assert.True(pinned.IsBusy)
        Assert.Equal(0L, pinned.CompletedEvents)
    finally
        release.Set()
        nextRelease.Set()
        awaitResult first
        second |> Option.iter awaitResult
        queue.Close()

[<Fact(Timeout = 20000)>]
let ``closing admission rejects queued work but retains a live callback until cleanup`` () =
    let store = PluginWorkOwner.Store()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let queue =
        SupervisedWork.Queue(
            store,
            "controlled",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state _ -> state),
            (fun state _ -> state),
            ignore,
            (fun state (_: int) _ _ ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                    return state + 1
                })
        )

    let active = queue.Submit(1, CancellationToken.None)

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        let queued = queue.Submit(2, CancellationToken.None)
        queue.Close()
        Assert.Throws<ObjectDisposedException>(fun () -> awaitResult queued) |> ignore

        Assert.Throws<ObjectDisposedException>(fun () -> queue.Submit(3, CancellationToken.None) |> ignore)
        |> ignore

        Assert.True(store.Snapshot.IsBusy)
        Assert.False(active.IsCompleted)
        release.Set()

        Assert.ThrowsAny<OperationCanceledException>(fun () -> awaitResult active)
        |> ignore

        Assert.False(store.Snapshot.IsBusy)
        Assert.Equal(0, queue.State)
    finally
        release.Set()

        try
            awaitResult active
        with :? OperationCanceledException ->
            ()

        queue.Close()

[<Fact(Timeout = 20000)>]
let ``deadline records failure without retiring a noncooperative callback`` () =
    let store = PluginWorkOwner.Store()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let scheduled =
        TaskCompletionSource<unit -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let queue =
        SupervisedWork.Queue(
            store,
            "controlled",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state _ -> state),
            (fun state _ -> state),
            ignore,
            (fun state (_: int) _ _ ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                    return state + 1
                }),
            scheduleDeadline =
                (fun _ expire ->
                    scheduled.SetResult(expire)

                    { new IDisposable with
                        member _.Dispose() = () })
        )

    let active = queue.Submit(1, CancellationToken.None)

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))

        let expire =
            scheduled.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        expire ()
        Assert.True(store.Snapshot.IsBusy)
        Assert.False(active.IsCompleted)
        Assert.IsType<TimeoutException>(snd store.Snapshot.Faults.Head) |> ignore

        Assert.IsType<TimeoutException>(snd store.Snapshot.OperationFaults.Head)
        |> ignore

        Assert.Empty(store.Snapshot.ExecutorFaults)
        release.Set()
        Assert.Throws<TimeoutException>(fun () -> awaitResult active) |> ignore
        Assert.False(store.Snapshot.IsBusy)
        Assert.IsType<TimeoutException>(snd store.Snapshot.Faults.Head) |> ignore
        Assert.Equal(0, queue.State)
    finally
        release.Set()

        try
            awaitResult active
        with :? TimeoutException ->
            ()

        queue.Close()

[<Fact(Timeout = 20000)>]
let ``completion notification stays owned and remains covered by its deadline`` () =
    let store = PluginWorkOwner.Store()
    use notifying = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let scheduled =
        TaskCompletionSource<unit -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let queue =
        SupervisedWork.Queue(
            store,
            "controlled",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state _ -> state),
            (fun state _ -> state),
            (fun _ ->
                notifying.Set()
                Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))),
            (fun state (_: int) _ _ -> async { return state + 1 }),
            scheduleDeadline =
                (fun _ expire ->
                    scheduled.SetResult(expire)

                    { new IDisposable with
                        member _.Dispose() = () })
        )

    let active = queue.Submit(1, CancellationToken.None)

    try
        Assert.True(notifying.Wait(TimeSpan.FromSeconds 5.0))
        Assert.Equal(1, queue.State)
        Assert.True(store.Snapshot.IsBusy)
        Assert.False(active.IsCompleted)

        let expire =
            scheduled.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        expire ()
        Assert.True(store.Snapshot.IsBusy)
        release.Set()
        Assert.Throws<TimeoutException>(fun () -> awaitResult active) |> ignore
        Assert.False(store.Snapshot.IsBusy)
        Assert.IsType<TimeoutException>(snd store.Snapshot.Faults.Head) |> ignore
    finally
        release.Set()

        try
            awaitResult active
        with :? TimeoutException ->
            ()

        queue.Close()

[<Fact(Timeout = 20000)>]
let ``handoff transition failure settles accepted receipts and closes admission`` () =
    let store = PluginWorkOwner.Store()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let failure = InvalidOperationException("controlled handoff failure")

    let queue =
        SupervisedWork.Queue(
            store,
            "controlled",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state request -> if request = 2 then raise failure else state),
            (fun state _ -> state),
            ignore,
            (fun state (_: int) _ _ ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                    return state + 1
                })
        )

    let active = queue.Submit(1, CancellationToken.None)
    let mutable queued: Task<unit> option = None

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        let next = queue.Submit(2, CancellationToken.None)
        queued <- Some next
        release.Set()
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> awaitResult active))
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> awaitResult next))
        Assert.False(store.Snapshot.IsBusy)
        Assert.Same(failure, snd store.Snapshot.Faults.Head)

        Assert.Throws<ObjectDisposedException>(fun () -> queue.Submit(3, CancellationToken.None) |> ignore)
        |> ignore
    finally
        release.Set()

        for request in active :: (queued |> Option.toList) do
            try
                awaitResult request
            with :? InvalidOperationException ->
                ()

        queue.Close()

[<Fact(Timeout = 15000)>]
let ``worker scope charges teardown failures to its owner rather than the request caller`` () =
    let store = PluginWorkOwner.Store()
    let ownerRegistry = ProcessRegistry.Registry()
    let callerRegistry = ProcessRegistry.Registry()
    // These are data-only sentinel leak records. No process is created/signaled.
    let sentinel description : ProcessRegistry.LeakedTree =
        { Pid = 0
          Description = description
          Reason = "fixture scope sentinel"
          At = DateTime.UtcNow }

    ownerRegistry.ReportLeak(sentinel "owner")
    callerRegistry.ReportLeak(sentinel "caller")

    let queue =
        use _ownerScope = ProcessRegistry.install ownerRegistry

        SupervisedWork.Queue(
            store,
            "controlled",
            [],
            TimeSpan.FromMinutes 1.0,
            (fun state (_: unit) -> state),
            (fun state _ -> state),
            ignore,
            (fun _ _ _ _ ->
                async {
                    ProcessRegistry.reportLeak 0 "operation" "fixture teardown failure"
                    return []
                })
        )

    use _callerScope = ProcessRegistry.install callerRegistry

    try
        Assert.Throws<InvalidOperationException>(fun () -> queue.Submit((), CancellationToken.None) |> awaitResult)
        |> ignore

        Assert.Equal<string list>(
            [ "owner"; "operation" ],
            ownerRegistry.Leaks |> List.map (fun leak -> leak.Description)
        )

        Assert.Empty(queue.State)
        Assert.Equal<string list>([ "caller" ], ProcessRegistry.leaked () |> List.map (fun leak -> leak.Description))
    finally
        queue.Close()

[<Fact(Timeout = 30000)>]
let ``admission after caller timeout still launches and settles its work`` () =
    let store = PluginWorkOwner.Store()

    let blocker =
        store.Register(
            { Name = "bounded writer fixture"
              Value = box ()
              Busy = false
              Completed = 0L
              Failure = None
              Evidence = None }
        )

    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    use started = new ManualResetEventSlim(false)

    let queue =
        SupervisedWork.Queue(
            store,
            "controlled",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: unit) -> state),
            (fun state _ -> state),
            ignore,
            (fun state _ _ _ ->
                async {
                    started.Set()
                    return state + 1
                })
        )
    // Deliberately hold the writer only in this bounded fixture, longer than
    // Submit's existing five-second caller bound.
    let held =
        store.ChangeAsync(
            blocker,
            fun row ->
                entered.Set()
                Assert.True(release.Wait(TimeSpan.FromSeconds 12.0))
                row, ()
        )

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))

        Assert.Throws<TimeoutException>(fun () -> queue.Submit((), CancellationToken.None) |> ignore)
        |> ignore

        release.Set()
        awaitResult held
        Assert.True(started.Wait(TimeSpan.FromSeconds 5.0), "Late admission lost its worker launch")
        Assert.True(SpinWait.SpinUntil((fun () -> store.Snapshot.CompletedEvents = 1L), TimeSpan.FromSeconds 5.0))
        Assert.False(store.Snapshot.IsBusy)
        Assert.Equal(1, queue.State)
    finally
        release.Set()
        awaitResult held
        queue.Close()

[<Fact(Timeout = 15000)>]
let ``shared execution deadline retains an exclusive capability until real completion`` () =
    let store = PluginWorkOwner.Store()
    let owner = PluginWorkOwner.Owner((), store, "exclusive")
    let identity, _ = owner.TryClaim "run" |> Option.get
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let deadlineCallback = TaskCompletionSource<unit -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let execution =
        SupervisedWork.execute
            "exclusive"
            (TimeSpan.FromMinutes 1.0)
            (fun _ callback ->
                deadlineCallback.SetResult callback
                { new IDisposable with member _.Dispose() = () })
            (fun failure -> owner.MarkRunFailure(identity, failure))
            CancellationToken.None
            (fun _ -> async {
                entered.Set()
                Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                return () })
            (fun outcome settleChildren ->
                settleChildren ()
                match outcome with
                | Ok () -> owner.CompleteRun identity |> ignore
                | Result.Error failure -> owner.FailRun(identity, failure))
        |> fun work -> Async.StartAsTask(work, cancellationToken = CancellationToken.None)

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        deadlineCallback.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult() ()
        Assert.True(store.Snapshot.IsBusy)
        Assert.NotEmpty store.Snapshot.Faults
        Assert.False execution.IsCompleted
    finally
        release.Set()
        awaitResult execution

    Assert.False(store.Snapshot.IsBusy)
    Assert.NotEmpty store.Snapshot.Faults

[<Fact>]
let ``failed host operation stays visible after cleanup until a new attempt`` () =
    let store = PluginWorkOwner.Store()
    let identity = store.BeginOperation "preprocessor"
    store.FailOperation(identity, InvalidOperationException("refused input"))
    let active = store.Snapshot
    store.EndOperation identity
    Assert.True active.IsBusy
    Assert.False store.Snapshot.IsBusy
    Assert.Single store.Snapshot.OperationFaults |> ignore
    let retry = store.BeginOperation "preprocessor"
    Assert.Empty store.Snapshot.OperationFaults
    Assert.True store.Snapshot.IsBusy
    store.EndOperation retry

[<Fact>]
let ``queued intents follow the exact run through prepared completion before FIFO delivery`` () =
    let store = PluginWorkOwner.Store()
    let owner = PluginWorkOwner.Owner(0, store, "queued")
    let received = ResizeArray<string * PluginWorkOwner.WorkId>()
    let post name identity = received.Add(name, identity)
    let active, _ = owner.TryClaim "tests" |> Option.get
    let first = owner.EnqueueIntent("tests", None, post "command-1")
    let automatic = owner.EnqueueIntent("tests", Some "flush", post "old-flush")
    let replacement = owner.EnqueueIntent("tests", Some "flush", post "new-flush")
    Assert.Same(automatic, replacement)
    Assert.Empty received
    let completion = owner.CompleteRun active |> Option.get
    owner.PublishEventState(completion, 1)
    let last = owner.EnqueueIntent("tests", None, post "command-2")
    Assert.Empty received
    Assert.False first.IsCompleted
    let before = store.Snapshot
    owner.SettleEvent(completion, preparedCommit = true)
    Assert.Equal<string list>([ "command-1"; "new-flush"; "command-2" ], received |> Seq.map fst |> Seq.toList)
    Assert.True store.Snapshot.IsBusy
    Assert.True before.IsBusy
    Assert.Equal(1, owner.Snapshot.State)
    Assert.False first.IsCompleted
    for _, identity in received do
        owner.CommitEvent(identity, owner.Snapshot.State + 1)
    Assert.False store.Snapshot.IsBusy
    Assert.True first.IsCompletedSuccessfully
    Assert.True automatic.IsCompletedSuccessfully
    Assert.True last.IsCompletedSuccessfully

[<Fact>]
let ``executor fault fails queued receipts but retains the live exclusive worker`` () =
    let store = PluginWorkOwner.Store()
    let owner = PluginWorkOwner.Owner((), store, "faulted")
    let active, _ = owner.TryClaim "tests" |> Option.get
    let queued = owner.EnqueueIntent("tests", None, fun _ -> failwith "must not deliver after executor fault")
    owner.FaultExecutor(InvalidOperationException("executor stopped"))
    Assert.Throws<InvalidOperationException>(fun () -> queued.GetAwaiter().GetResult()) |> ignore
    Assert.True store.Snapshot.IsBusy
    owner.FailRun(active, InvalidOperationException("worker drained"))
    Assert.False store.Snapshot.IsBusy
