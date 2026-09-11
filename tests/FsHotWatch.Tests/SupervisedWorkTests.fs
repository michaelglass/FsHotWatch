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
              Evidence = None
              AnalysisEvidence = None }
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

    let deadlineCallback =
        TaskCompletionSource<unit -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let execution =
        SupervisedWork.execute
            "exclusive"
            (TimeSpan.FromMinutes 1.0)
            (fun _ callback ->
                deadlineCallback.SetResult callback

                { new IDisposable with
                    member _.Dispose() = () })
            (fun failure -> owner.MarkRunFailure(identity, failure))
            CancellationToken.None
            (fun _ ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                    return ()
                })
            (fun outcome settleChildren ->
                settleChildren ()

                match outcome with
                | Ok() -> owner.CompleteRun identity |> ignore
                | Result.Error failure -> owner.FailRun(identity, failure))
        |> fun work -> Async.StartAsTask(work, cancellationToken = CancellationToken.None)

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        deadlineCallback.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult () ()
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
    Assert.Equal<string list>([ "command-1" ], received |> Seq.map fst |> Seq.toList)
    Assert.True store.Snapshot.IsBusy
    Assert.True before.IsBusy
    Assert.Equal(1, owner.Snapshot.State)
    Assert.False first.IsCompleted

    for index in 0..2 do
        let _, identity = received[index]
        owner.CommitEvent(identity, owner.Snapshot.State + 1)

    Assert.Equal<string list>([ "command-1"; "new-flush"; "command-2" ], received |> Seq.map fst |> Seq.toList)
    Assert.False store.Snapshot.IsBusy
    Assert.True first.IsCompletedSuccessfully
    Assert.True automatic.IsCompletedSuccessfully
    Assert.True last.IsCompletedSuccessfully

[<Fact>]
let ``executor fault fails queued receipts but retains the live exclusive worker`` () =
    let store = PluginWorkOwner.Store()
    let owner = PluginWorkOwner.Owner((), store, "faulted")
    let active, _ = owner.TryClaim "tests" |> Option.get

    let queued =
        owner.EnqueueIntent("tests", None, fun _ -> failwith "must not deliver after executor fault")

    owner.FaultExecutor(InvalidOperationException("executor stopped"))

    Assert.Throws<InvalidOperationException>(fun () -> queued.GetAwaiter().GetResult())
    |> ignore

    Assert.True store.Snapshot.IsBusy
    owner.FailRun(active, InvalidOperationException("worker drained"))
    Assert.False store.Snapshot.IsBusy

[<Fact>]
let ``commands queued before a result fold stay ahead of later successor intents`` () =
    let store = PluginWorkOwner.Store()
    let owner = PluginWorkOwner.Owner((), store, "fifo")
    let delivered = ResizeArray<string * PluginWorkOwner.WorkId>()
    let firstRun, _ = owner.TryClaim "tests" |> Option.get

    let earlier =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("earlier", id))

    let fold = owner.CompleteRun firstRun |> Option.get
    let nextRun, _ = owner.TryClaim("tests", after = fold) |> Option.get
    let later = owner.EnqueueIntent("tests", None, fun id -> delivered.Add("later", id))
    owner.CommitEvent(fold, ())
    Assert.Empty delivered
    let nextFold = owner.CompleteRun nextRun |> Option.get
    owner.CommitEvent(nextFold, ())
    Assert.Equal("earlier", fst delivered[0])
    Assert.Equal(1, delivered.Count)
    owner.CommitEvent(snd delivered[0], ())
    Assert.Equal("later", fst delivered[1])
    owner.CommitEvent(snd delivered[1], ())
    Assert.True earlier.IsCompletedSuccessfully
    Assert.True later.IsCompletedSuccessfully
    Assert.False store.Snapshot.IsBusy

[<Fact>]
let ``only the exact result fold may claim its successor before commit`` () =
    let owner = PluginWorkOwner.Owner(())
    let active, _ = owner.TryClaim "tests" |> Option.get
    let earlierEvent = owner.AdmitEvent()
    let fold = owner.CompleteRun active |> Option.get
    Assert.True((owner.TryClaim("tests", after = earlierEvent)).IsNone)
    Assert.True((owner.TryClaim "tests").IsNone)
    let next, _ = owner.TryClaim("tests", after = fold) |> Option.get
    owner.CommitEvent(earlierEvent, ())
    owner.CommitEvent(fold, ())
    let nextFold = owner.CompleteRun next |> Option.get
    owner.CommitEvent(nextFold, ())
    Assert.False owner.Snapshot.IsBusy


[<Theory>]
[<InlineData("commit")>]
[<InlineData("prepared")>]
[<InlineData("failure")>]
let ``successor delivery failure cannot strand the retired predecessor receipt`` mode =
    let owner = PluginWorkOwner.Owner(0)
    let delivered = ResizeArray<PluginWorkOwner.WorkId>()
    let active, _ = owner.TryClaim "tests" |> Option.get
    let predecessor = owner.EnqueueIntent("tests", None, delivered.Add)
    let deliveryFailure = InvalidOperationException("successor delivery failed")
    let successor = owner.EnqueueIntent("tests", None, fun _ -> raise deliveryFailure)
    let fold = owner.CompleteRun active |> Option.get
    owner.CommitEvent(fold, 1)
    let identity = Assert.Single delivered
    let originalFailure = InvalidOperationException("predecessor update failed")

    let settle () =
        match mode with
        | "commit" -> owner.CommitEvent(identity, 2)
        | "prepared" ->
            owner.PublishEventState(identity, 2)
            owner.SettleEvent(identity, preparedCommit = true)
        | _ -> owner.FailEvent(identity, PluginWorkOwner.UpdateFailure originalFailure)

    let thrown = Assert.Throws<InvalidOperationException>(settle)
    Assert.Same(deliveryFailure, thrown)
    Assert.True(predecessor.IsCompleted, "retired predecessor receipt must settle even when successor delivery throws")

    if mode = "failure" then
        let failed =
            Assert.Throws<InvalidOperationException>(fun () -> predecessor.GetAwaiter().GetResult())

        Assert.Same(originalFailure, failed)
    else
        Assert.True predecessor.IsCompletedSuccessfully
        Assert.Equal(2, owner.Snapshot.State)

    let rejected =
        Assert.Throws<InvalidOperationException>(fun () -> successor.GetAwaiter().GetResult())

    Assert.Same(deliveryFailure, rejected)
    Assert.True owner.Snapshot.ExecutorFault.IsSome
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``idle intent reserves its key until its exact fold admits work`` () =
    let owner = PluginWorkOwner.Owner(())
    let delivered = ResizeArray<string * PluginWorkOwner.WorkId>()
    let first = owner.EnqueueIntent("tests", None, fun id -> delivered.Add("first", id))

    let second =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("second", id))

    Assert.Equal<string list>([ "first" ], delivered |> Seq.map fst |> Seq.toList)
    Assert.True((owner.TryClaim "tests").IsNone)
    let firstIdentity = snd delivered[0]
    let active, _ = owner.TryClaim("tests", after = firstIdentity) |> Option.get
    owner.CommitEvent(firstIdentity, ())
    Assert.True first.IsCompletedSuccessfully
    Assert.False second.IsCompleted
    let fold = owner.CompleteRun active |> Option.get
    owner.CommitEvent(fold, ())
    Assert.Equal<string list>([ "first"; "second" ], delivered |> Seq.map fst |> Seq.toList)
    owner.CommitEvent(snd delivered[1], ())
    Assert.True second.IsCompletedSuccessfully
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``coalescing spans the completing run and its already admitted successor`` () =
    let owner = PluginWorkOwner.Owner(())
    let delivered = ResizeArray<string * PluginWorkOwner.WorkId>()
    let firstRun, _ = owner.TryClaim "tests" |> Option.get

    let original =
        owner.EnqueueIntent("tests", Some "flush", fun id -> delivered.Add("old-flush", id))

    let earlier =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("earlier-command", id))

    let fold = owner.CompleteRun firstRun |> Option.get
    let nextRun, _ = owner.TryClaim("tests", after = fold) |> Option.get

    let replacement =
        owner.EnqueueIntent("tests", Some "flush", fun id -> delivered.Add("new-flush", id))

    let later =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("later-command", id))

    Assert.Same(original, replacement)
    owner.CommitEvent(fold, ())
    let nextFold = owner.CompleteRun nextRun |> Option.get
    owner.CommitEvent(nextFold, ())

    for index in 0..2 do
        Assert.Equal(index + 1, delivered.Count)
        owner.CommitEvent(snd delivered[index], ())

    Assert.Equal<string list>(
        [ "new-flush"; "earlier-command"; "later-command" ],
        delivered |> Seq.map fst |> Seq.toList
    )

    Assert.True original.IsCompletedSuccessfully
    Assert.True replacement.IsCompletedSuccessfully
    Assert.True earlier.IsCompletedSuccessfully
    Assert.True later.IsCompletedSuccessfully
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``only a committed actual worker result recovers a previous worker failure`` () =
    let owner = PluginWorkOwner.Owner(0)
    let failed, _ = owner.TryClaim "tests" |> Option.get
    owner.FailRun(failed, InvalidOperationException("first worker failed"))
    let delivered = ResizeArray<PluginWorkOwner.WorkId>()
    let queued = owner.EnqueueIntent("tests", None, delivered.Add)
    owner.PublishEventState(delivered[0], 1)
    owner.SettleEvent(delivered[0], preparedCommit = true)
    Assert.True queued.IsCompletedSuccessfully
    Assert.True owner.Snapshot.Fault.IsSome
    let recovered, _ = owner.TryClaim "tests" |> Option.get
    let result = owner.CompleteRun recovered |> Option.get
    owner.PublishEventState(result, 2)
    owner.SettleEvent(result)
    Assert.True owner.Snapshot.Fault.IsNone
    Assert.False owner.Snapshot.IsBusy
    Assert.Equal(2, owner.Snapshot.State)

[<Theory>]
[<InlineData("zero")>]
[<InlineData("negative")>]
[<InlineData("unbounded")>]
let ``external execution refuses an unbounded ownership lifetime`` kind =
    let deadline =
        match kind with
        | "zero" -> TimeSpan.Zero
        | "negative" -> TimeSpan.FromSeconds -1.0
        | _ -> TimeSpan.MaxValue

    let mutable started = false

    let execution =
        SupervisedWork.execute
            "invalid"
            deadline
            SupervisedWork.defaultScheduler
            ignore
            CancellationToken.None
            (fun _ -> async { started <- true })
            (fun outcome cleanup ->
                cleanup ()
                outcome)

    Assert.Throws<ArgumentException>(fun () -> Async.RunSynchronously execution |> ignore)
    |> ignore

    Assert.False started

[<Fact>]
let ``scheduler failure settles the operation without invoking external work`` () =
    let failure = InvalidOperationException("scheduler unavailable")
    let mutable started = false

    let result =
        SupervisedWork.execute
            "schedule"
            (TimeSpan.FromSeconds 1.0)
            (fun _ _ -> raise failure)
            ignore
            CancellationToken.None
            (fun _ ->
                async {
                    started <- true
                    return 1
                })
            (fun outcome cleanup ->
                cleanup ()
                outcome)
        |> Async.RunSynchronously

    match result with
    | Error actual -> Assert.Same(failure, actual)
    | Ok _ -> failwith "scheduler failure cannot return a successful operation"

    Assert.False started

[<Theory>]
[<InlineData("notification")>]
[<InlineData("timer-disposal")>]
let ``settlement failure remains owned until its failed receipt is published`` stage =
    let store = PluginWorkOwner.Store()
    let failure = InvalidOperationException(stage)
    let mutable disposed = 0

    let queue =
        SupervisedWork.Queue(
            store,
            "settlement",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: unit) -> state),
            (fun _ _ -> -1),
            (fun _ ->
                if stage = "notification" then
                    raise failure),
            (fun _ _ _ publish ->
                async {
                    publish 1
                    return 2
                }),
            scheduleDeadline =
                (fun _ _ ->
                    { new IDisposable with
                        member _.Dispose() =
                            Assert.True(store.Snapshot.IsBusy, "timer teardown must precede ownership retirement")
                            disposed <- disposed + 1

                            if stage = "timer-disposal" then
                                raise failure })
        )

    try
        let receipt = queue.Submit((), CancellationToken.None)
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> awaitResult receipt))
        Assert.Equal(1, disposed)
        Assert.Equal(-1, queue.State)
        Assert.False store.Snapshot.IsBusy
        Assert.Same(failure, snd store.Snapshot.OperationFaults.Head)
    finally
        queue.Close()

[<Fact>]
let ``foreign and wrong-kind capabilities cannot retire another owners work`` () =
    let owner = PluginWorkOwner.Owner(0)
    let other = PluginWorkOwner.Owner(0)
    let active, _ = owner.TryClaim "tests" |> Option.get
    let foreign, _ = other.TryClaim "tests" |> Option.get
    let event = owner.AdmitEvent()

    Assert.Throws<InvalidOperationException>(fun () -> owner.CompleteRun foreign |> ignore)
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> owner.CompleteRun event |> ignore)
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> owner.CommitEvent(active, 1))
    |> ignore

    Assert.True owner.Snapshot.IsBusy
    Assert.Equal(0, owner.Snapshot.State)
    owner.CommitEvent(event, 1)
    let completion = owner.TransferToCompletion active
    owner.CommitEvent(completion, 2)

    Assert.Throws<InvalidOperationException>(fun () -> owner.CommitEvent(completion, 3))
    |> ignore

    Assert.Equal(2, owner.Snapshot.State)
    Assert.False owner.Snapshot.IsBusy
    let otherCompletion = other.TransferToCompletion foreign
    other.CommitEvent(otherCompletion, 0)

[<Fact>]
let ``executor failure is immutable while outstanding worker capabilities drain`` () =
    let store = PluginWorkOwner.Store()
    let owner = PluginWorkOwner.Owner(0, store, "failed-owner")
    let event, receipt = owner.AdmitTrackedEvent()
    owner.PublishEventState(event, 1)
    let run, _ = owner.TryClaim "tests" |> Option.get
    let first = InvalidOperationException("executor failed first")
    owner.FaultExecutor first
    owner.FaultExecutor(InvalidOperationException("later report must not replace cause"))
    Assert.Same(first, Assert.Throws<InvalidOperationException>(fun () -> awaitResult receipt))
    Assert.Same(first, owner.Snapshot.ExecutorFault.Value)
    Assert.Same(first, snd (Assert.Single store.Snapshot.ExecutorFaults))
    Assert.True store.Snapshot.IsBusy

    Assert.Throws<InvalidOperationException>(fun () -> owner.TransferToCompletion run |> ignore)
    |> ignore

    Assert.False store.Snapshot.IsBusy
    Assert.Equal(1, owner.Snapshot.State)

[<Fact>]
let ``a retry cannot clear the failure of an overlapping host operation`` () =
    let store = PluginWorkOwner.Store()
    let active = store.BeginOperation "scan"
    let failure = InvalidOperationException("first scan still live")
    store.FailOperation(active, failure)
    let retry = store.BeginOperation "scan"
    Assert.Same(failure, snd (Assert.Single store.Snapshot.OperationFaults))
    Assert.Equal<string list>([ "scan" ], store.Snapshot.BusyNames)
    store.EndOperation retry
    store.EndOperation active
    store.FailOperation(active, InvalidOperationException("late callback"))
    Assert.Same(failure, snd (Assert.Single store.Snapshot.OperationFaults))

    Assert.Throws<InvalidOperationException>(fun () -> store.EndOperation active)
    |> ignore

    let fresh = store.BeginOperation "scan"
    Assert.Empty store.Snapshot.OperationFaults
    store.EndOperation fresh

[<Theory>]
[<InlineData("commit")>]
[<InlineData("run")>]
let ``an ordinary update cannot overwrite stronger failed verification`` kind =
    let owner = PluginWorkOwner.Owner(0)
    let first = InvalidOperationException("verification did not commit")

    if kind = "commit" then
        let event = owner.AdmitEvent()
        owner.FailEvent(event, PluginWorkOwner.CommitFailure first)
    else
        let run, _ = owner.TryClaim "tests" |> Option.get
        owner.MarkRunFailure(run, first)
        owner.MarkRunFailure(run, InvalidOperationException("later deadline"))
        Assert.Same(first, owner.Snapshot.Fault.Value)
        owner.FailRun(run, first)
        owner.MarkRunFailure(run, InvalidOperationException("late callback"))

    let unrelated = owner.AdmitEvent()
    owner.FailEvent(unrelated, PluginWorkOwner.UpdateFailure(InvalidOperationException("ordinary event")))
    Assert.Same(first, owner.Snapshot.Fault.Value)
    let ordinary = owner.AdmitEvent()
    owner.CommitEvent(ordinary, 1)
    Assert.Same(first, owner.Snapshot.Fault.Value)

    if kind = "commit" then
        let successfulRun, _ = owner.TryClaim "tests" |> Option.get
        owner.FailRun(successfulRun, InvalidOperationException("run cannot erase commit failure"))
        Assert.Same(first, owner.Snapshot.Fault.Value)

    let invalid = owner.AdmitEvent()

    Assert.Throws<ArgumentException>(fun () -> owner.FailEvent(invalid, PluginWorkOwner.ExecutorFailure first))
    |> ignore

    owner.CommitEvent(invalid, 2)

[<Theory>]
[<InlineData(0)>]
[<InlineData(-1)>]
[<InlineData(1)>]
let ``invalid queue deadline admits no owner or worker`` kind =
    let store = PluginWorkOwner.Store()

    let deadline =
        if kind = 1 then
            TimeSpan.MaxValue
        else
            TimeSpan.FromSeconds(float kind)

    Assert.Throws<ArgumentException>(fun () ->
        SupervisedWork.Queue(
            store,
            "invalid",
            0,
            deadline,
            (fun state (_: int) -> state),
            (fun state _ -> state),
            ignore,
            (fun state _ _ _ -> async.Return state)
        )
        |> ignore)
    |> ignore

    Assert.False store.Snapshot.IsBusy
    Assert.Empty store.Snapshot.BusyNames

[<Fact(Timeout = 15000)>]
let ``supervisor constructed without flowing context still settles its actual worker`` () =
    let store = PluginWorkOwner.Store()

    let queue =
        use suppressed = ExecutionContext.SuppressFlow()

        SupervisedWork.Queue(
            store,
            "suppressed",
            0,
            TimeSpan.FromSeconds 5.,
            (fun state (_: int) -> state),
            (fun state _ -> state),
            ignore,
            (fun state request _ _ -> async.Return(state + request))
        )

    try
        queue.Submit(3, CancellationToken.None) |> awaitResult
        Assert.Equal(3, queue.State)
        Assert.False store.Snapshot.IsBusy
    finally
        queue.Close()

[<Fact(Timeout = 15000)>]
let ``deadline publication failure still cancels work and late timer callbacks cannot reopen ownership`` () =
    let mutable expire = ignore
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let mutable cancelled = false

    let work =
        SupervisedWork.execute
            "deadline-report"
            (TimeSpan.FromSeconds 5.)
            (fun _ callback ->
                expire <- callback

                { new IDisposable with
                    member _.Dispose() = () })
            (fun _ -> failwith "diagnostic publication refused")
            CancellationToken.None
            (fun token ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 5.))
                    cancelled <- token.IsCancellationRequested
                    return ()
                })
            (fun outcome cleanup ->
                cleanup ()
                outcome)
        |> Async.StartAsTask

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.))
        expire ()
        release.Set()
        let outcome = work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        Assert.True cancelled

        match outcome with
        | Error failure -> Assert.IsAssignableFrom<OperationCanceledException>(failure) |> ignore
        | Ok() -> Assert.Fail("deadline cancellation must refuse successful settlement")

        expire ()
    finally
        release.Set()
        work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore

[<Fact(Timeout = 15000)>]
let ``work admitted during completion notification stays queued until predecessor cleanup`` () =
    let store = PluginWorkOwner.Store()
    use finishing = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let queue =
        SupervisedWork.Queue(
            store,
            "finishing",
            0,
            TimeSpan.FromSeconds 10.,
            (fun state (_: int) -> state),
            (fun state _ -> state),
            (fun state ->
                if state = 1 then
                    finishing.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 5.))),
            (fun state request _ _ -> async.Return(state + request))
        )

    let first = queue.Submit(1, CancellationToken.None)
    let mutable next: Task<unit> option = None

    try
        Assert.True(finishing.Wait(TimeSpan.FromSeconds 5.))
        let second = queue.Submit(2, CancellationToken.None)
        next <- Some second
        Assert.False first.IsCompleted
        Assert.False second.IsCompleted
        Assert.True store.Snapshot.IsBusy
        release.Set()
        awaitResult first
        awaitResult second
        Assert.Equal(3, queue.State)
        Assert.False store.Snapshot.IsBusy
    finally
        release.Set()
        awaitResult first
        next |> Option.iter awaitResult
        queue.Close()

[<Fact(Timeout = 15000)>]
let ``throwing cancellation registration cannot abandon the still running owned callback`` () =
    let store = PluginWorkOwner.Store()
    use entered = new ManualResetEventSlim(false)
    use cancelled = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let queue =
        SupervisedWork.Queue(
            store,
            "cancellation-refusal",
            0,
            TimeSpan.FromSeconds 10.,
            (fun state (_: int) -> state),
            (fun state _ -> state),
            ignore,
            (fun state _ token _ ->
                async {
                    use registration =
                        token.Register(fun () ->
                            cancelled.Set()
                            failwith "cancellation callback refused")

                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 5.))
                    return state + 1
                })
        )

    let active = queue.Submit(1, CancellationToken.None)

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.))
        queue.Close()
        Assert.True(cancelled.Wait(TimeSpan.FromSeconds 5.))
        Assert.True store.Snapshot.IsBusy
        Assert.False active.IsCompleted
        release.Set()

        Assert.ThrowsAny<OperationCanceledException>(fun () -> awaitResult active)
        |> ignore

        Assert.False store.Snapshot.IsBusy
        Assert.Equal(0, queue.State)
    finally
        release.Set()

        try
            awaitResult active
        with :? OperationCanceledException ->
            ()

        queue.Close()

[<Fact>]
let ``published update recovery restores rest while exclusive presence tracks only the actual run`` () =
    let owner = PluginWorkOwner.Owner(0)
    Assert.False owner.Snapshot.HasExclusiveRun
    let bad = owner.AdmitEvent()
    owner.FailEvent(bad, PluginWorkOwner.UpdateFailure(InvalidOperationException("update refused")))
    Assert.True owner.Snapshot.Fault.IsSome
    let recovery = owner.AdmitEvent()
    owner.PublishEventState(recovery, 1)
    owner.SettleEvent(recovery)
    Assert.True owner.Snapshot.Fault.IsNone
    let run, _ = owner.TryClaim "tests" |> Option.get
    Assert.True owner.Snapshot.HasExclusiveRun
    let fold = owner.CompleteRun run |> Option.get
    Assert.False owner.Snapshot.HasExclusiveRun
    owner.CommitEvent(fold, 2)
    Assert.False owner.Snapshot.HasExclusiveRun
    Assert.False owner.Snapshot.IsBusy

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``failure delivery cannot retire a capability of a different kind`` failEvent =
    let owner = PluginWorkOwner.Owner(0)
    let event = owner.AdmitEvent()
    let run, _ = owner.TryClaim "tests" |> Option.get
    let failure = InvalidOperationException("wrong failure capability")
    Assert.Throws<InvalidOperationException>(fun () ->
        if failEvent then
            owner.FailEvent(run, PluginWorkOwner.UpdateFailure failure)
        else
            owner.FailRun(event, failure)) |> ignore
    Assert.True owner.Snapshot.IsBusy
    Assert.True(owner.Snapshot.IsRunning "tests")
    Assert.True owner.Snapshot.Fault.IsNone
    owner.CommitEvent(event, 1)
    let fold = owner.CompleteRun run |> Option.get
    owner.CommitEvent(fold, 2)
    Assert.False owner.Snapshot.IsBusy
    Assert.Equal(2, owner.Snapshot.State)
