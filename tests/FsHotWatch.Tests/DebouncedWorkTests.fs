[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.DebouncedWorkTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.PluginWorkOwner

let private finish (receipt: Task<unit>) =
    receipt.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``flush coalesces preceding input and keeps subsequent cohorts ordered`` () =
    let store = Store()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let cohorts = Collections.Concurrent.ConcurrentQueue<int list>()

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ ->
                async {
                    cohorts.Enqueue values

                    if values.Head = 1 then
                        entered.Set()
                        Assert.True(release.Wait(FsHotWatch.Tests.TestHelpers.HangWait))

                    return state + List.sum values
                })
        )

    let input = DebouncedWork.Queue(store, "changes", worker, (@))
    let receipts = ResizeArray<Task<unit>>()

    let post values delay =
        let receipt = input.Post(values, delay)
        receipts.Add receipt
        receipt

    try
        post [ 1 ] (TimeSpan.FromSeconds 5.0) |> ignore
        Assert.True(store.Snapshot.IsBusy, "debounce must already be owned")
        post [ 2 ] (TimeSpan.FromSeconds 5.0) |> ignore
        post [ 3 ] TimeSpan.Zero |> ignore
        Assert.True(entered.Wait(FsHotWatch.Tests.TestHelpers.HangWait))
        Assert.Equal<int list>([ 1; 2; 3 ], Seq.head cohorts)
        post [ 4 ] (TimeSpan.FromSeconds 5.0) |> ignore
        post [ 5 ] TimeSpan.Zero |> ignore
        Assert.All(receipts, fun receipt -> Assert.False(receipt.IsCompleted))
        Assert.True(store.Snapshot.IsBusy)
        release.Set()

        for receipt in receipts do
            finish receipt

        Assert.Equal<int list list>([ [ 1; 2; 3 ]; [ 4; 5 ] ], List.ofSeq cohorts)
        Assert.Equal(15, worker.State)
        Assert.Equal(2L, store.Snapshot.CompletedEvents)
        Assert.False(store.Snapshot.IsBusy)
    finally
        release.Set()

        for receipt in receipts do
            finish receipt

        input.Close()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``close rejects debounce input and retains a live supervised cohort`` () =
    let store = Store()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state _ _ _ ->
                async {
                    entered.Set()
                    Assert.True(release.Wait(FsHotWatch.Tests.TestHelpers.HangWait))
                    return state + 1
                })
        )

    let input = DebouncedWork.Queue(store, "changes", worker, (@))
    let active = input.Post([ 1 ], TimeSpan.Zero)

    try
        Assert.True(entered.Wait(FsHotWatch.Tests.TestHelpers.HangWait))
        let pending = input.Post([ 2 ], TimeSpan.FromSeconds 5.0)
        input.Close()
        Assert.Throws<ObjectDisposedException>(fun () -> finish pending) |> ignore

        Assert.Throws<ObjectDisposedException>(fun () -> input.Post([ 3 ], TimeSpan.Zero) |> ignore)
        |> ignore

        Assert.True(store.Snapshot.IsBusy)
        Assert.False(active.IsCompleted)
        release.Set()
        Assert.ThrowsAny<OperationCanceledException>(fun () -> finish active) |> ignore
        Assert.False(store.Snapshot.IsBusy)
    finally
        release.Set()

        try
            finish active
        with :? OperationCanceledException ->
            ()

        input.Close()

[<Fact(Timeout = 15000)>]
let ``worker admission rejection settles the original cohort and permits later work`` () =
    let store = Store()
    let failure = InvalidOperationException("controlled admission rejection")

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state values -> if values = [ 1 ] then raise failure else state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ -> async { return state + List.sum values })
        )

    let input = DebouncedWork.Queue(store, "changes", worker, (@))

    try
        let rejected = input.Post([ 1 ], TimeSpan.Zero)
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> finish rejected))
        Assert.False(store.Snapshot.IsBusy)
        Assert.Same(failure, snd store.Snapshot.Faults.Head)
        input.Post([ 2 ], TimeSpan.Zero) |> finish
        Assert.Equal(2, worker.State)
        Assert.Equal(1L, store.Snapshot.CompletedEvents)
        Assert.False(store.Snapshot.IsBusy)
        Assert.Empty(store.Snapshot.Faults)
    finally
        input.Close()

[<Fact(Timeout = 15000)>]
let ``failed coalescing leaves the accepted pending input intact`` () =
    let store = Store()
    let failure = InvalidOperationException("controlled merge rejection")

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ -> async { return state + List.sum values })
        )

    let input =
        DebouncedWork.Queue(
            store,
            "changes",
            worker,
            (fun earlier later -> if later = [ 2 ] then raise failure else earlier @ later)
        )

    let accepted = input.Post([ 1 ], TimeSpan.FromSeconds 5.0)

    try
        Assert.Same(
            failure,
            Assert.Throws<InvalidOperationException>(fun () -> input.Post([ 2 ], TimeSpan.Zero) |> ignore)
        )

        Assert.False(accepted.IsCompleted)
        Assert.True(store.Snapshot.IsBusy)
        input.Post([ 3 ], TimeSpan.Zero) |> finish
        finish accepted
        Assert.Equal(4, worker.State)
        Assert.Equal(1L, store.Snapshot.CompletedEvents)
        Assert.False(store.Snapshot.IsBusy)
    finally
        input.Close()
        // Cleanup must also drain a still-pending accepted receipt if an earlier
        // assertion failed, without masking the distinction from a worker fault.
        try
            finish accepted
        with :? ObjectDisposedException ->
            ()

[<Fact(Timeout = 15000)>]
let ``failed debounce scheduler settles its own pending receipt and records failure`` () =
    let store = Store()
    let failure = InvalidOperationException("controlled scheduler failure")

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ -> async { return state + List.sum values })
        )

    let input =
        DebouncedWork.Queue(store, "changes", worker, (@), delayTask = (fun _ -> raise failure))

    try
        let pending = input.Post([ 1 ], TimeSpan.FromSeconds 5.0)
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> finish pending))
        Assert.False(store.Snapshot.IsBusy)
        Assert.Equal(0, worker.State)
        Assert.Equal(0L, store.Snapshot.CompletedEvents)
        Assert.Same(failure, snd store.Snapshot.Faults.Head)
        // An explicit flush needs no timer and can recover after this failure.
        input.Post([ 2 ], TimeSpan.Zero) |> finish
        Assert.Equal(2, worker.State)
        Assert.False(store.Snapshot.IsBusy)
    finally
        input.Close()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``obsolete scheduler failure cannot reject its coalesced successor`` () =
    let store = Store()

    let firstDelay =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let secondDelay =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let delays = Collections.Concurrent.ConcurrentQueue<Task>()
    delays.Enqueue firstDelay.Task
    delays.Enqueue secondDelay.Task
    let cohorts = Collections.Concurrent.ConcurrentQueue<int list>()
    use failureHandled = new ManualResetEventSlim(false)
    let marker = "obsolete-scheduler-proof"

    use output =
        { new IO.TextWriter() with
            member _.Encoding = Text.Encoding.UTF8

            override _.Write(value: string) =
                if not (isNull value) && value.Contains(marker) then
                    failureHandled.Set() }

    let previousError = Console.Error
    let previousLevel = Logging.logLevel

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ ->
                async {
                    cohorts.Enqueue values
                    return state + List.sum values
                })
        )

    let input =
        DebouncedWork.Queue(
            store,
            "changes",
            worker,
            (@),
            delayTask =
                (fun _ ->
                    match delays.TryDequeue() with
                    | true, delay -> delay
                    | _ -> invalidOp "Unexpected fixture delay")
        )

    let receipts = ResizeArray<Task<unit>>()

    try
        Console.SetError(output)
        Logging.setLogLevel Logging.LogLevel.Error
        receipts.Add(input.Post([ 1 ], TimeSpan.FromSeconds 5.0))
        receipts.Add(input.Post([ 2 ], TimeSpan.FromSeconds 5.0))
        firstDelay.SetException(InvalidOperationException(marker))
        // This diagnostic is emitted after the failure transition and receipt
        // effects, so the assertion cannot race ahead of the obsolete handler.
        Assert.True(failureHandled.Wait(FsHotWatch.Tests.TestHelpers.HangWait))
        Assert.All(receipts, fun receipt -> Assert.False(receipt.IsCompleted))
        Assert.True(store.Snapshot.IsBusy)
        Assert.Empty(store.Snapshot.Faults)
        secondDelay.SetResult(())

        for receipt in receipts do
            finish receipt

        Assert.Equal<int list list>([ [ 1; 2 ] ], List.ofSeq cohorts)
        Assert.Equal(3, worker.State)
        Assert.False(store.Snapshot.IsBusy)
    finally
        firstDelay.TrySetResult(()) |> ignore
        secondDelay.TrySetResult(()) |> ignore

        try
            input.Close()

            for receipt in receipts do
                try
                    finish receipt
                with
                | :? OperationCanceledException -> ()
                | :? ObjectDisposedException -> ()
                | :? InvalidOperationException as failure when failure.Message = marker -> ()
        finally
            Console.SetError(previousError)
            Logging.setLogLevel previousLevel

[<Theory>]
[<InlineData(-1.0)>]
[<InlineData(1.7976931348623157E+308)>]
let ``invalid debounce duration admits no cohort`` milliseconds =
    let store = Store()

    let worker =
        SupervisedWork.Queue(
            store,
            "invalid-delay",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state _ _ _ -> async { return state + 1 })
        )

    let input = DebouncedWork.Queue(store, "invalid-delay", worker, (@))

    let delay =
        if milliseconds < 0.0 then
            TimeSpan.FromMilliseconds milliseconds
        else
            TimeSpan.MaxValue

    try
        Assert.Throws<ArgumentException>(fun () -> input.Post([ 1 ], delay) |> ignore)
        |> ignore

        Assert.False store.Snapshot.IsBusy
        Assert.Equal(0, worker.State)
    finally
        input.Close()

[<Fact(Timeout = 15000)>]
let ``an obsolete debounce timer neither flushes nor fails later input`` () =
    let store = Store()
    let delays = Collections.Concurrent.ConcurrentQueue<TaskCompletionSource<unit>>()
    let cohorts = Collections.Concurrent.ConcurrentQueue<int list>()

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ ->
                async {
                    cohorts.Enqueue values
                    return state + List.sum values
                })
        )

    let input =
        DebouncedWork.Queue(
            store,
            "changes",
            worker,
            (@),
            delayTask =
                (fun _ ->
                    let delay = TaskCompletionSource<unit>()
                    delays.Enqueue delay
                    delay.Task)
        )

    let nextDelay () =
        match delays.TryDequeue() with
        | true, delay -> delay
        | _ -> failwith "the fixture expected a debounce timer"

    // Completes one timer and waits until the store has decided it.
    let fire (complete: unit -> unit) =
        let version = store.Snapshot.Version
        complete ()

        Assert.True(
            SpinWait.SpinUntil((fun () -> store.Snapshot.Version > version), TimeSpan.FromSeconds 5.0),
            "the timer's expiry must reach the writer"
        )

    let receipts = ResizeArray<Task<unit>>()

    try
        receipts.Add(input.Post([ 1 ], TimeSpan.FromSeconds 5.0))
        let first = nextDelay ()
        receipts.Add(input.Post([ 2 ], TimeSpan.FromSeconds 5.0))
        let second = nextDelay ()

        // Superseded while input is still pending: it must not flush the successor.
        fire (fun () -> first.SetResult(()))
        Assert.Empty(cohorts)
        Assert.All(receipts, fun receipt -> Assert.False(receipt.IsCompleted))
        Assert.True(store.Snapshot.IsBusy)

        receipts.Add(input.Post([ 3 ], TimeSpan.Zero))

        for receipt in receipts do
            finish receipt

        // Nothing is pending any more: a late failure cannot fail it, and a late expiry
        // cannot admit anything.
        fire (fun () -> second.SetException(InvalidOperationException("late timer failure")))
        input.Post([ 4 ], TimeSpan.FromSeconds 5.0) |> receipts.Add
        let third = nextDelay ()
        input.Post([ 5 ], TimeSpan.Zero) |> finish
        fire (fun () -> third.SetResult(()))

        for receipt in receipts do
            finish receipt

        Assert.Equal<int list list>([ [ 1; 2; 3 ]; [ 4; 5 ] ], List.ofSeq cohorts)
        Assert.Empty(store.Snapshot.Faults)
        Assert.False(store.Snapshot.IsBusy)
    finally
        for delay in delays do
            delay.TrySetResult(()) |> ignore

        input.Close()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a debounce timer that has already finished still admits its cohort`` () =
    let store = Store()
    let cohorts = Collections.Concurrent.ConcurrentQueue<int list>()

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ ->
                async {
                    cohorts.Enqueue values
                    return state + List.sum values
                })
        )

    let input =
        DebouncedWork.Queue(store, "changes", worker, (@), delayTask = (fun _ -> Task.CompletedTask))

    try
        finish (input.Post([ 1 ], TimeSpan.FromSeconds 5.0))
        Assert.Equal<int list list>([ [ 1 ] ], List.ofSeq cohorts)
        Assert.Equal(1, worker.State)
    finally
        input.Close()

/// A supervised receipt as `settleWith` awaits it once the request has finished: the receipt,
/// returned only after it completed, whatever its outcome.
let private finishedFirst (receipt: Task<unit>) : Task<unit> =
    receipt.ContinueWith(fun (_: Task<unit>) -> ()).Wait()
    receipt

// `settleWith`'s `do! receipt` has two arms: the request still running when its cohort's input
// retires, and the request already finished. A worker held open pins the first; this pins the
// second, which a fast worker reached only when the pool happened to run it first (GitHub CI run
// 43671932991 missed it, 43670057067 on the same sha did not), as `delayTask` pins line 137's.
[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a supervised request that has already finished still settles its cohort`` () =
    let store = Store()
    let awaited = Collections.Concurrent.ConcurrentQueue<bool>()

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ -> async { return state + List.sum values })
        )

    let input =
        DebouncedWork.Queue(
            store,
            "changes",
            worker,
            (@),
            awaitReceipt =
                (fun receipt ->
                    let finished = finishedFirst receipt
                    awaited.Enqueue finished.IsCompleted
                    finished)
        )

    try
        finish (input.Post([ 1; 2 ], TimeSpan.Zero))
        Assert.Equal(3, worker.State)
        Assert.Equal<bool list>([ true ], List.ofSeq awaited)
        Assert.False(store.Snapshot.IsBusy)
    finally
        input.Close()

[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``a supervised request that has already failed settles its cohort with the failure`` () =
    let store = Store()
    let failure = InvalidOperationException("controlled supervised failure")

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun _ _ _ _ -> async { return raise failure })
        )

    let input =
        DebouncedWork.Queue(store, "changes", worker, (@), awaitReceipt = finishedFirst)

    try
        let receipt = input.Post([ 1 ], TimeSpan.Zero)
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> finish receipt))
    finally
        input.Close()

// AUTOMATION-1601: every store change a debounce queue awaits (the input publication, the
// expiry, the worker's admission, the cohort's retirement, the closing change) has two arms:
// still queued when awaited, or already published. A pool writer decides which; a store whose
// writer publishes inside the post makes it the second, provided no other writer is running.
// The worker is held until the cohort's input has retired (`awaitReceipt` runs right after),
// so it publishes nothing while the queue admits.
[<Fact(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
let ``with an inline store writer, every change a debounced cohort awaits is already published`` () =
    let store = Store(fun write -> write ())
    use retired = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ ->
                async {
                    Assert.True(release.Wait(FsHotWatch.Tests.TestHelpers.HangWait))
                    return state + List.sum values
                })
        )

    let input =
        DebouncedWork.Queue(
            store,
            "changes",
            worker,
            (@),
            delayTask = (fun _ -> Task.CompletedTask),
            awaitReceipt =
                (fun receipt ->
                    retired.Set()
                    receipt)
        )

    try
        let receipt = input.Post([ 1; 2 ], TimeSpan.FromSeconds 5.0)
        Assert.True(retired.Wait(FsHotWatch.Tests.TestHelpers.HangWait), "the cohort's input must retire")
        Assert.False(receipt.IsCompleted, "the held worker still owns the cohort")
        release.Set()
        finish receipt
        Assert.Equal(3, worker.State)
    finally
        release.Set()
        input.Close()

    Assert.False(store.Snapshot.IsBusy)

// Coalescing restarts the debounce with the longer of the pending and new delays: both arms of
// that choice, each seen as the delay the restarted timer is armed with (AUTOMATION-1601: the
// rest of this suite posts only equal or zero delays, so one arm had only an incidental caller).
[<Theory(Timeout = FsHotWatch.Tests.TestHelpers.SyncHangBackstopMs)>]
[<InlineData(1.0, 5.0, 5.0)>]
[<InlineData(5.0, 1.0, 5.0)>]
let ``coalesced input restarts the debounce with the longer delay`` (first: float) (second: float) (longer: float) =
    let store = Store()
    let armed = Collections.Concurrent.ConcurrentQueue<TimeSpan>()
    let timers = Collections.Concurrent.ConcurrentQueue<TaskCompletionSource>()

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            ignore,
            (fun state values _ _ -> async { return state + List.sum values })
        )

    // Timers that never fire on their own, so the second post always coalesces into the first.
    let input =
        DebouncedWork.Queue(
            store,
            "changes",
            worker,
            (@),
            delayTask =
                (fun delay ->
                    armed.Enqueue delay
                    let timer = TaskCompletionSource()
                    timers.Enqueue timer
                    timer.Task)
        )

    let receipts =
        [ input.Post([ 1 ], TimeSpan.FromSeconds first)
          input.Post([ 2 ], TimeSpan.FromSeconds second) ]

    try
        Assert.Equal<TimeSpan list>([ TimeSpan.FromSeconds first; TimeSpan.FromSeconds longer ], List.ofSeq armed)
    finally
        input.Close()

        for timer in timers do
            timer.TrySetResult() |> ignore

    // Close settled the coalesced cohort; its two posts share that outcome.
    for receipt in receipts do
        Assert.Throws<ObjectDisposedException>(fun () -> finish receipt) |> ignore
