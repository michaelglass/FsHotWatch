/// What a scheduler's admission looks like from outside (AUTOMATION-682): per lane, how
/// much is waiting and held now, how many acquisitions were granted or given up, and how
/// long the granted ones waited and the slots were held. Measured against an injected
/// clock, so every figure here is exact.
module FsHotWatch.Tests.WorkSchedulerStatusTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.FSharp
open FsHotWatch.WorkScheduler
open FsHotWatch.Tests.TestHelpers

/// A clock the test moves by hand.
type private ManualClock() =
    let mutable now = TimeSpan.Zero
    member _.Now() = now

    member _.Advance(seconds: float) =
        now <- now + TimeSpan.FromSeconds seconds

let private seconds (s: float) = TimeSpan.FromSeconds s

let private withClock (capacity: int) =
    let clock = ManualClock()
    Scheduler(capacity, ignore, clock.Now), clock

/// Acquire on `lane`; the acquisition queues (or is granted) before this returns.
let private acquire (lane: Lane) (ct: CancellationToken) = lane.Acquire ct |> Async.StartAsTask

[<Fact(Timeout = 5000)>]
let ``a lane that is granted at once waited nothing`` () =
    let scheduler, clock = withClock 1
    let a = scheduler.Lane "A"

    test <@ (acquire a CancellationToken.None).Result @>
    clock.Advance 3.0

    let status = a.Status()
    test <@ status.Lane = "A" @>
    test <@ status.Running = 1 && status.Queued = 0 @>
    test <@ status.Granted = 1L && status.Abandoned = 0L @>
    test <@ status.Waited = TimeSpan.Zero && status.LongestWait = TimeSpan.Zero @>
    // The slot is held now, so its held time runs up to the clock.
    test <@ status.Held = seconds 3.0 @>

[<Fact(Timeout = 5000)>]
let ``a waiter's wait runs from its enqueue to the hand-over, and the slot moves lanes`` () =
    let scheduler, clock = withClock 1
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"

    test <@ (acquire a CancellationToken.None).Result @>
    clock.Advance 2.0
    let waiting = acquire b CancellationToken.None
    test <@ (b.Status()).Queued = 1 && (b.Status()).Running = 0 @>

    clock.Advance 7.0
    a.Release()
    test <@ waiting.Result @>
    clock.Advance 1.0

    let sa = a.Status()
    let sb = b.Status()
    test <@ sa.Running = 0 && sa.Granted = 1L && sa.Held = seconds 9.0 @>
    test <@ sb.Running = 1 && sb.Queued = 0 && sb.Granted = 1L @>
    test <@ sb.Waited = seconds 7.0 && sb.LongestWait = seconds 7.0 @>
    test <@ sb.Held = seconds 1.0 @>

    test
        <@
            scheduler.Status() = { Capacity = 1
                                   Running = 1
                                   Queued = 0 }
        @>

[<Fact(Timeout = 5000)>]
let ``waits add up, and the longest is kept`` () =
    let scheduler, clock = withClock 1
    let holder = scheduler.Lane "holder"
    let b = scheduler.Lane "B"

    test <@ (acquire holder CancellationToken.None).Result @>
    let first = acquire b CancellationToken.None
    clock.Advance 4.0
    let second = acquire b CancellationToken.None
    clock.Advance 1.0
    holder.Release()
    test <@ first.Result @>
    clock.Advance 2.0
    b.Release()
    test <@ second.Result @>

    let status = b.Status()
    // The first waited 5 s, the second 3 s.
    test
        <@
            status.Granted = 2L
            && status.Waited = seconds 8.0
            && status.LongestWait = seconds 5.0
        @>

[<Fact(Timeout = 5000)>]
let ``a waiter that gives up is counted abandoned, not granted, and adds no wait`` () =
    let scheduler, clock = withClock 1
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"
    use cts = new CancellationTokenSource()

    test <@ (acquire a CancellationToken.None).Result @>
    let abandoned = acquire b cts.Token
    clock.Advance 6.0
    cts.Cancel()
    test <@ not abandoned.Result @>

    let status = b.Status()
    test <@ status.Abandoned = 1L && status.Granted = 0L && status.Queued = 0 @>
    test <@ status.Waited = TimeSpan.Zero && status.Running = 0 @>

[<Fact(Timeout = 5000)>]
let ``a caller that has already given up counts nothing`` () =
    let scheduler, _ = withClock 1
    let a = scheduler.Lane "A"
    use cts = new CancellationTokenSource()
    cts.Cancel()

    test <@ not (acquire a cts.Token).Result @>
    let status = a.Status()
    test <@ status.Granted = 0L && status.Abandoned = 0L && status.Queued = 0 @>

[<Fact(Timeout = 5000)>]
let ``a lane releasing a slot another lane holds is refused, and the slot stays held`` () =
    let scheduler, _ = withClock 2
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"

    test <@ (acquire a CancellationToken.None).Result @>

    raises<InvalidOperationException> <@ b.Release() @>
    test <@ (a.Status()).Running = 1 && (b.Status()).Running = 0 @>
    test <@ scheduler.Running = 1 @>

[<Fact(Timeout = 5000)>]
let ``a lane's status is a JSON object with its counts and times in milliseconds`` () =
    let status =
        { Lane = "/r"
          Queued = 2
          Running = 1
          Granted = 5L
          Abandoned = 1L
          Waited = TimeSpan.FromMilliseconds 1500.0
          LongestWait = TimeSpan.FromMilliseconds 900.0
          Held = TimeSpan.FromMilliseconds 4200.0
          Interactive =
            { Granted = 4L
              Abandoned = 0L
              Waited = TimeSpan.FromMilliseconds 500.0
              LongestWait = TimeSpan.FromMilliseconds 300.0 }
          Bulk =
            { Granted = 1L
              Abandoned = 1L
              Waited = TimeSpan.FromMilliseconds 1000.0
              LongestWait = TimeSpan.FromMilliseconds 900.0 } }

    let json = (LaneStatus.payload status).ToJsonString()

    test
        <@
            json = """{"queued":2,"running":1,"granted":5,"abandoned":1,"waitedMs":1500,"longestWaitMs":900,"heldMs":4200,"interactive":{"granted":4,"abandoned":0,"waitedMs":500,"longestWaitMs":300},"bulk":{"granted":1,"abandoned":1,"waitedMs":1000,"longestWaitMs":900}}"""
        @>

[<Fact(Timeout = 5000)>]
let ``a scheduler's status is a JSON object with its capacity and what is held and queued`` () =
    let json =
        (SchedulerStatus.payload
            { Capacity = 12
              Running = 3
              Queued = 40 })
            .ToJsonString()

    test <@ json = """{"capacity":12,"running":3,"queued":40}""" @>

// ---------------------------------------------------------------------------
// The model: every lane's counts add up to the scheduler's, under any sequence
// ---------------------------------------------------------------------------

/// One step a test applies to a scheduler: acquire on a lane, release one of a lane's
/// held slots, or give up one of a lane's waiters.
type private Step =
    | Acquire of lane: int * bulk: bool
    | Advance of seconds: int
    | Release of lane: int
    | GiveUp of lane: int

let private stepGen (lanes: int) =
    Gen.choose (0, lanes - 1)
    |> Gen.bind (fun lane ->
        Gen.elements
            [ Acquire(lane, false)
              Acquire(lane, true)
              Release lane
              GiveUp lane
              Advance 3 ])

[<Fact(Timeout = PropertyTestCapMs)>]
let ``under any sequence, the bound holds and every lane's counts add up to the scheduler's`` () =
    let laneCount = 3

    let scenario =
        gen {
            let! capacity = Gen.choose (1, 3)
            let! steps = Gen.listOf (stepGen laneCount)
            return capacity, steps
        }

    let holds (capacity: int, steps: Step list) =
        let scheduler, clock = withClock capacity
        let lanes = Array.init laneCount (fun i -> scheduler.Lane $"L%d{i}")
        // Per lane, the acquisitions not yet resolved, and the slots held.
        let pending =
            Array.init laneCount (fun _ -> ResizeArray<CancellationTokenSource * Tasks.Task<bool>>())

        let held = Array.zeroCreate<int> laneCount
        let acquired = Array.zeroCreate<int64> laneCount
        let gaveUp = Array.zeroCreate<int64> laneCount

        for step in steps do
            match step with
            | Advance s -> clock.Advance(float s)
            | Acquire(i, bulk) ->
                let cts = new CancellationTokenSource()

                let task =
                    lanes[i].Acquire((if bulk then JobSpec.bulk else JobSpec.interactive), cts.Token)
                    |> Async.StartAsTask

                pending[i].Add((cts, task))
            | Release i ->
                if held[i] > 0 then
                    lanes[i].Release()
                    held[i] <- held[i] - 1
            | GiveUp i ->
                // The oldest still-waiting acquisition on this lane, if any.
                match pending[i] |> Seq.tryFind (fun (_, t) -> not t.IsCompleted) with
                | Some(cts, t) ->
                    cts.Cancel()
                    t.Wait()
                | None -> ()

            // Resolve what has been decided: a granted acquisition holds a slot, a
            // refused one was given up. Grants are made under the scheduler's lock
            // before release returns, so every grant is already decided here.
            for i in 0 .. laneCount - 1 do
                let queued = (lanes[i].Status()).Queued
                // `pending - queued` acquisitions are decided, not necessarily the oldest: a
                // class can overtake another. A decided one's task completes off this thread.
                let decided = pending[i].Count - queued

                let completed () =
                    pending[i] |> Seq.filter (fun (_, t) -> t.IsCompleted) |> List.ofSeq

                while (completed ()).Length < decided do
                    Thread.Yield() |> ignore

                let resolved = completed ()

                for (cts, t) in resolved do
                    if t.Result then
                        held[i] <- held[i] + 1
                        acquired[i] <- acquired[i] + 1L
                    else
                        gaveUp[i] <- gaveUp[i] + 1L

                    cts.Dispose()

                pending[i].RemoveAll(fun (_, t) -> resolved |> List.exists (fun (_, r) -> obj.ReferenceEquals(r, t)))
                |> ignore

            let statuses = lanes |> Array.map _.Status()
            let total = scheduler.Status()

            if total.Running > capacity then
                failwith $"%d{total.Running} slots held over a capacity of %d{capacity}"

            if (statuses |> Array.sumBy _.Running) <> total.Running then
                failwith "the lanes' running does not add up to the scheduler's"

            if (statuses |> Array.sumBy _.Queued) <> total.Queued then
                failwith "the lanes' queued does not add up to the scheduler's"

            for i in 0 .. laneCount - 1 do
                if statuses[i].Running <> held[i] then
                    failwith $"lane %d{i} reports %d{statuses[i].Running} running, holds %d{held[i]}"

                if statuses[i].Granted <> acquired[i] || statuses[i].Abandoned <> gaveUp[i] then
                    failwith $"lane %d{i} reports granted/abandoned %A{statuses[i]}"

                if
                    statuses[i].Interactive.Granted + statuses[i].Bulk.Granted
                    <> statuses[i].Granted
                    || statuses[i].Interactive.Abandoned + statuses[i].Bulk.Abandoned
                       <> statuses[i].Abandoned
                then
                    failwith $"lane %d{i}'s class counts do not add up to its totals"

        // Waiters left queued at the end give up, so no case leaves a task behind.
        for lane in pending do
            for (cts, t) in lane do
                cts.Cancel()
                t.Wait()
                cts.Dispose()

    Check.One(Config.QuickThrowOnFailure.WithMaxTest(300), Prop.forAll (Arb.fromGen scenario) holds)
