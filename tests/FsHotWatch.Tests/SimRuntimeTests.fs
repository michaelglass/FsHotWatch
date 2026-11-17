/// The deterministic runtime itself (AUTOMATION-1695): what it promises the simulations
/// built on it.
module FsHotWatch.Tests.SimRuntimeTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.SimRuntime
open FsHotWatch.Tests.TestHelpers

/// A small world: three dispatches, each settling a receipt that a task awaits and logs.
let private scenario (sim: Sim) : int list =
    let log = Collections.Generic.List<int>()
    use _ = sim.Install()

    let awaiting =
        [ for i in 1..3 ->
              let receipt =
                  TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

              sim.Runtime.Dispatch(fun () ->
                  log.Add i
                  receipt.SetResult())

              task {
                  do! receipt.Task
                  log.Add(10 * i)
              } ]

    sim.RunUntilQuiet()
    test <@ awaiting |> List.forall (fun t -> t.IsCompletedSuccessfully) @>
    List.ofSeq log

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a world is a function of its seed`` () =
    let run seed =
        let sim = Sim seed
        let log = scenario sim
        log, sim.Choices

    test <@ run 7 = run 7 @>
    // Some seed among a handful orders the same world differently: the seed decides.
    test <@ [ 1..20 ] |> List.map (run >> fst) |> List.distinct |> List.length > 1 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a dispatch runs inline or queues, and both happen across seeds`` () =
    let choices =
        [ 1..20 ]
        |> List.collect (fun seed ->
            let sim = Sim seed
            scenario sim |> ignore
            sim.Choices)

    test <@ choices |> List.contains Choice.Inline @>
    test <@ choices |> List.contains Choice.Queued @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an awaited continuation runs on the simulation's thread, from its queue`` () =
    let sim = Sim(1, 0.0)
    let owner = Environment.CurrentManagedThreadId
    use _ = sim.Install()

    let receipt =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable resumedOn = -1

    let awaiting =
        task {
            do! receipt.Task
            resumedOn <- Environment.CurrentManagedThreadId
        }

    receipt.SetResult()
    // The continuation was posted, not run: nothing has resumed until the pump runs it.
    test <@ not awaiting.IsCompleted @>
    test <@ sim.ReadyCount = 1 @>
    test <@ sim.Step() = Some Ran.Posted @>
    test <@ awaiting.IsCompletedSuccessfully @>
    test <@ resumedOn = owner @>
    test <@ sim.Step() = None @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a wait pumps until its task settles`` () =
    let sim = Sim(3, 0.0)
    let settled = TaskCompletionSource<unit>()
    sim.Runtime.Dispatch(fun () -> settled.SetResult())
    test <@ sim.ReadyCount = 1 @>
    test <@ sim.Runtime.Wait Timeout.InfiniteTimeSpan settled.Task @>
    test <@ List.isEmpty sim.Faults @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an unbounded wait nothing can settle is a deadlock, raised instead of hanging`` () =
    let sim = Sim 4
    let never = TaskCompletionSource<unit>()

    raises<SimDeadlockException> <@ sim.Runtime.Wait Timeout.InfiniteTimeSpan never.Task @>
    test <@ sim.Faults |> List.exists (fun f -> f.IsDeadlock) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a bounded wait nothing can settle times out, with no virtual time passing`` () =
    let sim = Sim 5
    let never = TaskCompletionSource<unit>()
    test <@ not (sim.Runtime.Wait (TimeSpan.FromSeconds 5.0) never.Task) @>
    test <@ List.isEmpty sim.Faults @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``work from another thread is an escape, recorded and not run`` () =
    let sim = Sim 6
    let mutable ran = false

    let other = Thread(fun () -> sim.Runtime.Dispatch(fun () -> ran <- true))

    // The context too: install it, then post to it from another thread.
    use _ = sim.Install()
    let context = SynchronizationContext.Current

    let poster = Thread(fun () -> context.Post((fun _ -> ran <- true), null))
    other.Start()
    other.Join()
    poster.Start()
    poster.Join()

    test <@ not ran @>
    test <@ sim.ReadyCount = 0 @>
    test <@ sim.Faults |> List.filter (fun f -> f.IsEscape) |> List.length = 2 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``work that never stops arriving is reported as unquiet`` () =
    let sim = Sim(8, 0.0)

    let rec again () = sim.Runtime.Dispatch again

    again ()
    sim.RunUntilQuiet()
    test <@ sim.Faults = [ Fault.Unquiet QuietBound ] @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``virtual time advances only to the next armed timer, and a disposed timer never fires`` () =
    let sim = Sim(9, 0.0)
    let fired = Collections.Generic.List<string>()
    use _ = sim.Runtime.Schedule (TimeSpan.FromSeconds 2.0) (fun () -> fired.Add "two")

    let disarmed =
        sim.Runtime.Schedule (TimeSpan.FromSeconds 1.0) (fun () -> fired.Add "one")

    use _ =
        sim.Runtime.Schedule (TimeSpan.FromSeconds 3.0) (fun () -> fired.Add "three")

    disarmed.Dispose()

    test <@ sim.Step() = None @>
    test <@ sim.AdvanceToNextTimer() @>
    test <@ sim.Now = TimeSpan.FromSeconds 2.0 @>
    test <@ sim.Step() = Some Ran.Timer @>
    sim.RunUntilQuiet()
    test <@ List.ofSeq fired = [ "two"; "three" ] @>
    test <@ sim.Now = TimeSpan.FromSeconds 3.0 @>
    test <@ not (sim.AdvanceToNextTimer()) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a bounded wait lets timers due within its bound fire, then times out at its bound`` () =
    let sim = Sim(10, 0.0)
    // Virtual time: no thread waits on this bound, the simulated clock reaches it.
    let virtualBound = TimeSpan.FromSeconds 5.0
    let settled = TaskCompletionSource<unit>()

    use _ =
        sim.Runtime.Schedule (TimeSpan.FromSeconds 1.0) (fun () -> settled.SetResult())

    test <@ sim.Runtime.Wait virtualBound settled.Task @>
    test <@ sim.Now = TimeSpan.FromSeconds 1.0 @>

    let late = TaskCompletionSource<unit>()

    use _ =
        sim.Runtime.Schedule (TimeSpan.FromSeconds 10.0) (fun () -> late.SetResult())

    test <@ not (sim.Runtime.Wait virtualBound late.Task) @>
    test <@ sim.Now = TimeSpan.FromSeconds 6.0 @>
    // An unbounded wait reaches any armed timer.
    test <@ sim.Runtime.Wait Timeout.InfiniteTimeSpan late.Task @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``tasks started on the runtime's scheduler run from the queue, never inline`` () =
    let sim = Sim(11, 0.0)
    use _ = sim.Install()

    let started =
        Task.Factory.StartNew(
            (fun () -> 7),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            sim.Runtime.Scheduler
        )

    let continued =
        started.ContinueWith((fun (t: Task<int>) -> t.Result + 1), sim.Runtime.Scheduler)

    test <@ not started.IsCompleted @>
    sim.RunUntilQuiet()
    test <@ continued.Result = 8 @>
    test <@ sim.Choices |> List.exists (fun c -> c.IsRan) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``cancellation runs its callbacks from the runtime, and its task carries their failure`` () =
    let sim = Sim(12, 0.0)
    use source = new CancellationTokenSource()
    let mutable called = false
    use _ = source.Token.Register(fun () -> called <- true)
    let cancelling = sim.Runtime.Cancel source
    test <@ not called @>
    sim.RunUntilQuiet()
    test <@ called && cancelling.IsCompletedSuccessfully @>

    source.Dispose()
    let refused = sim.Runtime.Cancel source
    sim.RunUntilQuiet()
    test <@ refused.IsFaulted @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an async run synchronously pumps the world until it completes`` () =
    let sim = Sim(13, 0.0)
    use _ = sim.Install()

    let receipt =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    sim.Runtime.Dispatch(fun () -> receipt.SetResult 5)
    let mutable got = 0

    sim.Runtime.RunSynchronously(
        async {
            let! value = Async.AwaitTask receipt.Task
            got <- value
        }
    )

    test <@ got = 5 @>
    test <@ List.isEmpty sim.Faults @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a delay completes on the virtual clock, and a zero delay at once`` () =
    let sim = Sim(14, 0.0)
    test <@ (sim.Runtime.Delay TimeSpan.Zero).IsCompletedSuccessfully @>
    let delayed = sim.Runtime.Delay(TimeSpan.FromSeconds 3.0)
    test <@ not delayed.IsCompleted @>
    sim.RunUntilQuiet()
    test <@ delayed.IsCompletedSuccessfully @>
    test <@ sim.Now = TimeSpan.FromSeconds 3.0 @>
