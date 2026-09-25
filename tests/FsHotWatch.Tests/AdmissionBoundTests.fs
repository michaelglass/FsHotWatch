/// A caller that waits for an admission or a close waits at most `AdmissionBound`,
/// however busy the thread pool is. The bound is the caller's liveness guarantee: one
/// kept by a pool callback would hold only while the pool has a thread to spare.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.AdmissionBoundTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.PluginWorkOwner
open FsHotWatch.Tests.TestHelpers

/// Well past the bound for a wait kept by its caller, well short of one that waits for
/// a starved pool to reach its timer.
let private Slack = TimeSpan.FromSeconds 2.0

let private queueIn (store: Store) =
    SupervisedWork.Queue(
        store,
        "controlled",
        0,
        TimeSpan.FromMinutes 1.0,
        (fun state (_: int list) -> state),
        (fun state _ -> state),
        ignore,
        (fun state values _ _ -> async { return state + List.sum values })
    )

/// Hold `store`'s writer, so no admission can be published, and run `wait` with every
/// pool thread busy. It must give up with `TimeoutException` within the bound.
let private givesUpWithinBound (store: Store) (wait: unit -> unit) =
    let blocker =
        store.Register("held writer", (), fun () -> RowStatus.ofWork false 0L None)

    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let held =
        store.ChangeAsync(
            blocker,
            fun _ () ->
                entered.Set()
                release.Wait(TimeSpan.FromSeconds 50.0) |> ignore
                (), ()
        )

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0))

        let elapsed, outcome =
            withEveryPoolThreadBusy (fun () ->
                let clock = Stopwatch.StartNew()

                let outcome =
                    try
                        wait ()
                        None
                    with failure ->
                        Some failure

                clock.Elapsed, outcome)

        Assert.True(
            (match outcome with
             | Some(:? TimeoutException) -> true
             | _ -> false),
            $"expected a timeout, got %A{outcome}"
        )

        Assert.True(
            elapsed < SupervisedWork.AdmissionBound + Slack,
            $"gave up after %O{elapsed}, bound %O{SupervisedWork.AdmissionBound}"
        )
    finally
        release.Set()
        held.Wait(TimeSpan.FromSeconds 10.0) |> ignore

[<Fact(Timeout = 60000)>]
let ``a submit whose admission is held gives up within its bound, however busy the thread pool`` () =
    let store = Store()
    let queue = queueIn store

    try
        givesUpWithinBound store (fun () -> queue.Submit([ 1 ], CancellationToken.None) |> ignore)
    finally
        queue.Close()

[<Fact(Timeout = 60000)>]
let ``a post whose admission is held gives up within its bound, however busy the thread pool`` () =
    let store = Store()
    let queue = queueIn store
    let input = DebouncedWork.Queue(store, "changes", queue, (@))

    try
        givesUpWithinBound store (fun () -> input.Post([ 1 ], TimeSpan.Zero) |> ignore)
    finally
        input.Close()

[<Fact(Timeout = 10000)>]
let ``a bounded wait returns the result, raises the failure unwrapped, or gives up`` () =
    test <@ SupervisedWork.waitWithin (TimeSpan.FromSeconds 1.0) (Task.FromResult 42) = 42 @>

    raisesWith<InvalidOperationException>
        <@
            SupervisedWork.waitWithin
                (TimeSpan.FromSeconds 1.0)
                (Task.FromException<int>(InvalidOperationException "own failure"))
        @>
        (fun failure -> <@ failure.Message = "own failure" @>)

    raises<TimeoutException>
        <@ SupervisedWork.waitWithin (TimeSpan.FromMilliseconds 50.0) (TaskCompletionSource<int>().Task) @>

[<Fact(Timeout = 60000)>]
let ``a daemon probe answers while every pool thread is busy`` () =
    // A listening pipe accepts a connection before its server calls accept, so the probe
    // needs no pool thread on either side to see a daemon that is there.
    let pipeName = $"fp-{Guid.NewGuid():N}"

    use server =
        new IO.Pipes.NamedPipeServerStream(
            pipeName,
            IO.Pipes.PipeDirection.InOut,
            1,
            IO.Pipes.PipeTransmissionMode.Byte,
            IO.Pipes.PipeOptions.Asynchronous
        )

    let answered =
        withEveryPoolThreadBusy (fun () ->
            let answer = TaskCompletionSource<bool>()

            Thread((fun () -> answer.SetResult(Ipc.IpcClient.isRunning pipeName)), IsBackground = true).Start()

            answer.Task.Wait(TimeSpan.FromSeconds 10.0) && answer.Task.Result)

    Assert.True(answered, "a listening daemon read as not running while the pool was busy")

/// Parallel tests block pool threads in synchronous waits. Past the pool's minimum, a new
/// thread waits on starvation injection, which a saturated CPU halts, so every in-flight
/// test waiting on pool work stalls at once. `ThreadPoolMinThreads` in the test project
/// raises the minimum, up to which the pool adds a thread for each without delay.
[<Fact>]
let ``the test host lets parallel tests block pool threads without starving the pool`` () =
    let workers, _ = ThreadPool.GetMinThreads()
    test <@ workers >= 128 @>
