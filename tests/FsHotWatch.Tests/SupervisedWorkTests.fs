module FsHotWatch.Tests.SupervisedWorkTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.PluginWorkOwner

/// Await a receipt that must settle within the observation bound, preserving its exception.
let private awaitResult (work: Task<unit>) =
    let winner =
        Task.WhenAny([| work :> Task; Task.Delay(TimeSpan.FromSeconds 5.0) |]).GetAwaiter().GetResult()

    Assert.True(obj.ReferenceEquals(work, winner), "the operation receipt must settle within the observation bound")
    work.GetAwaiter().GetResult()

/// A deadline scheduler that hands its expiry to the test instead of arming a timer.
let private capturedDeadline () =
    let scheduled =
        TaskCompletionSource<unit -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let schedule (_: TimeSpan) (expire: unit -> unit) =
        scheduled.TrySetResult expire |> ignore

        { new IDisposable with
            member _.Dispose() = () }

    schedule, (fun () -> scheduled.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult())

[<Fact(Timeout = 20000)>]
let ``closing admission rejects queued work but retains a live callback until cleanup`` () =
    let store = Store()
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
    let store = Store()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let schedule, expiry = capturedDeadline ()
    let leakedPublish = ref ignore

    let queue =
        SupervisedWork.Queue(
            store,
            "controlled",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state _ -> state),
            (fun state _ -> state),
            ignore,
            (fun state (_: int) _ publish ->
                async {
                    leakedPublish.Value <- publish
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                    return state + 1
                }),
            scheduleDeadline = schedule
        )

    let active = queue.Submit(1, CancellationToken.None)

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        let expire = expiry ()
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
        let recorded = snd store.Snapshot.Faults.Head
        Assert.IsType<TimeoutException>(recorded) |> ignore
        Assert.Equal(0, queue.State)

        // Late callbacks from the retired request change nothing.
        let version = store.Snapshot.Version
        expire ()
        Assert.Same(recorded, snd (Assert.Single store.Snapshot.Faults))
        Assert.False(store.Snapshot.IsBusy)
        Assert.True(store.Snapshot.Version > version, "the late deadline must have been decided in the writer")

        Assert.Throws<InvalidOperationException>(fun () -> leakedPublish.Value 7)
        |> ignore

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
    let store = Store()
    use notifying = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let schedule, expiry = capturedDeadline ()

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
            scheduleDeadline = schedule
        )

    let active = queue.Submit(1, CancellationToken.None)

    try
        Assert.True(notifying.Wait(TimeSpan.FromSeconds 5.0))
        Assert.Equal(1, queue.State)
        Assert.True(store.Snapshot.IsBusy)
        Assert.False(active.IsCompleted)
        expiry () ()
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
    let store = Store()
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
    let queued = ref None

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        let next = queue.Submit(2, CancellationToken.None)
        queued.Value <- Some next
        release.Set()
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> awaitResult active))
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> awaitResult next))
        Assert.False(store.Snapshot.IsBusy)
        Assert.Same(failure, snd store.Snapshot.Faults.Head)

        Assert.Throws<ObjectDisposedException>(fun () -> queue.Submit(3, CancellationToken.None) |> ignore)
        |> ignore
    finally
        release.Set()

        for request in active :: Option.toList queued.Value do
            try
                awaitResult request
            with :? InvalidOperationException ->
                ()

        queue.Close()

[<Fact(Timeout = 15000)>]
let ``worker scope charges teardown failures to its owner rather than the request caller`` () =
    let store = Store()
    let ownerRegistry = ProcessRegistry.Registry()
    let callerRegistry = ProcessRegistry.Registry()

    // Data-only leak records. No process is created or signalled.
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
                    return [ "unreachable" ]
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
    let store = Store()

    let blocker =
        store.Register("bounded writer fixture", (), fun () -> RowStatus.ofWork false 0L None)

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

    // Holds the writer longer than Submit's admission bound, in this fixture only.
    let held =
        store.ChangeAsync(
            blocker,
            fun _ () ->
                entered.Set()
                Assert.True(release.Wait(TimeSpan.FromSeconds 12.0))
                (), ()
        )

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))

        Assert.Throws<TimeoutException>(fun () -> queue.Submit((), CancellationToken.None) |> ignore)
        |> ignore

        release.Set()
        awaitResult held
        Assert.True(started.Wait(TimeSpan.FromSeconds 5.0), "the late admission lost its worker launch")
        Assert.True(SpinWait.SpinUntil((fun () -> store.Snapshot.CompletedEvents = 1L), TimeSpan.FromSeconds 5.0))
        Assert.False(store.Snapshot.IsBusy)
        Assert.Equal(1, queue.State)
    finally
        release.Set()
        awaitResult held
        queue.Close()

[<Fact(Timeout = 15000)>]
let ``shared execution deadline retains an exclusive capability until real completion`` () =
    let store = Store()
    let owner = Owner((), store, "exclusive")
    let identity = owner.TryClaim "run" |> Option.get
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let schedule, expiry = capturedDeadline ()

    let execution =
        SupervisedWork.execute
            "exclusive"
            (TimeSpan.FromMinutes 1.0)
            schedule
            (fun failure -> owner.MarkRunFailure(identity, failure))
            CancellationToken.None
            (fun _ ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))
                })
            (fun outcome releaseDeadline ->
                releaseDeadline ()

                match outcome with
                | Ok() -> owner.CompleteRun identity |> ignore
                | Error failure -> owner.FailRun(identity, failure))
        |> fun work -> Async.StartAsTask(work, cancellationToken = CancellationToken.None)

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))
        expiry () ()
        Assert.True(store.Snapshot.IsBusy)
        Assert.NotEmpty store.Snapshot.Faults
        Assert.False execution.IsCompleted
    finally
        release.Set()
        awaitResult execution

    Assert.False(store.Snapshot.IsBusy)
    Assert.NotEmpty store.Snapshot.Faults

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

    let started = ref false

    let execution =
        SupervisedWork.execute
            "invalid"
            deadline
            SupervisedWork.defaultScheduler
            ignore
            CancellationToken.None
            (fun _ -> async { started.Value <- true })
            (fun outcome release ->
                release ()
                outcome)

    Assert.Throws<ArgumentException>(fun () -> Async.RunSynchronously execution |> ignore)
    |> ignore

    Assert.False started.Value

[<Fact>]
let ``scheduler failure settles the operation without invoking external work`` () =
    let failure = InvalidOperationException("scheduler unavailable")
    let started = ref false

    let result =
        SupervisedWork.execute
            "schedule"
            (TimeSpan.FromSeconds 1.0)
            (fun _ _ -> raise failure)
            ignore
            CancellationToken.None
            (fun _ ->
                async {
                    started.Value <- true
                    return 1
                })
            (fun outcome release ->
                release ()
                outcome)
        |> Async.RunSynchronously

    match result with
    | Error actual -> Assert.Same(failure, actual)
    | Ok _ -> failwith "a scheduler failure cannot return a successful operation"

    Assert.False started.Value

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``an execution releases its deadline exactly once`` (releasedByFinish: bool) =
    let disposals = ref 0

    let outcome =
        SupervisedWork.execute
            "release"
            (TimeSpan.FromMinutes 1.0)
            (fun _ _ ->
                { new IDisposable with
                    member _.Dispose() = disposals.Value <- disposals.Value + 1 })
            ignore
            CancellationToken.None
            (fun _ -> async.Return 1)
            (fun outcome release ->
                if releasedByFinish then
                    release ()

                outcome)
        |> Async.RunSynchronously

    Assert.Equal(Ok 1, outcome)
    Assert.Equal(1, disposals.Value)

[<Theory>]
[<InlineData("notification")>]
[<InlineData("timer-disposal")>]
let ``settlement failure remains owned until its failed receipt is published`` stage =
    let store = Store()
    let failure = InvalidOperationException(stage)
    let disposed = ref 0

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
                            Assert.True(store.Snapshot.IsBusy, "deadline teardown must precede retirement")
                            disposed.Value <- disposed.Value + 1

                            if stage = "timer-disposal" then
                                raise failure })
        )

    try
        let receipt = queue.Submit((), CancellationToken.None)
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> awaitResult receipt))
        Assert.Equal(1, disposed.Value)
        Assert.Equal(-1, queue.State)
        Assert.False store.Snapshot.IsBusy
        Assert.Same(failure, snd store.Snapshot.OperationFaults.Head)
    finally
        queue.Close()

[<Theory>]
[<InlineData(0)>]
[<InlineData(-1)>]
[<InlineData(1)>]
let ``invalid queue deadline admits no owner or worker`` kind =
    let store = Store()

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

    Assert.Equal(0L, store.Snapshot.Version)
    Assert.Empty store.Snapshot.BusyNames

[<Fact(Timeout = 15000)>]
let ``supervisor constructed without flowing context still settles its actual worker`` () =
    let store = Store()

    let queue =
        use _suppressed = ExecutionContext.SuppressFlow()

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
    let expire = ref ignore
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let cancelled = ref false

    let work =
        SupervisedWork.execute
            "deadline-report"
            (TimeSpan.FromSeconds 5.)
            (fun _ callback ->
                expire.Value <- callback

                { new IDisposable with
                    member _.Dispose() = () })
            (fun _ -> failwith "diagnostic publication refused")
            CancellationToken.None
            (fun token ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(TimeSpan.FromSeconds 5.))
                    cancelled.Value <- token.IsCancellationRequested
                })
            (fun outcome release ->
                release ()
                outcome)
        |> Async.StartAsTask

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.))
        expire.Value()
        release.Set()
        let outcome = work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        Assert.True cancelled.Value

        match outcome with
        | Error failure -> Assert.IsAssignableFrom<OperationCanceledException>(failure) |> ignore
        | Ok() -> Assert.Fail("deadline cancellation must refuse successful settlement")

        // The execution's cancellation source is gone; a late expiry is absorbed.
        expire.Value()
    finally
        release.Set()
        work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore

[<Fact(Timeout = 15000)>]
let ``work admitted during completion notification stays queued until predecessor cleanup`` () =
    let store = Store()
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
    let next = ref None

    try
        Assert.True(finishing.Wait(TimeSpan.FromSeconds 5.))
        let second = queue.Submit(2, CancellationToken.None)
        next.Value <- Some second
        Assert.False first.IsCompleted
        Assert.False second.IsCompleted
        Assert.True store.Snapshot.IsBusy
        release.Set()
        awaitResult first
        awaitResult second
        Assert.Equal(3, queue.State)
        Assert.Equal(2L, store.Snapshot.CompletedEvents)
        Assert.False store.Snapshot.IsBusy
    finally
        release.Set()
        awaitResult first
        next.Value |> Option.iter awaitResult
        queue.Close()

[<Fact(Timeout = 15000)>]
let ``throwing cancellation registration cannot abandon the still running owned callback`` () =
    let store = Store()
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
                    use _registration =
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
