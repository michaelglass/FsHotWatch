/// Declared weights (AUTOMATION-682 slice 7, AUTOMATION-1299, ADR-053). A slot held for a
/// job of weight `w` charges its lane `w` times its slot-time, so under contention a
/// heavier declared project is admitted proportionally less. Measured against an injected
/// clock.
module FsHotWatch.Tests.WorkSchedulerWeightTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.WorkScheduler

type private ManualClock() =
    let mutable now = TimeSpan.Zero
    member _.Now() = now

    member _.Advance(seconds: float) =
        now <- now + TimeSpan.FromSeconds seconds

let private withClock (capacity: int) =
    let clock = ManualClock()
    Scheduler(capacity, ignore, clock.Now, TimeSpan.FromHours 1.0), clock

let private weighing (weight: float) =
    JobSpec.weighted weight JobSpec.interactive

/// Queue one labelled acquisition of `job` on `lane`; it queues (or is granted) before this
/// returns, and records its label when granted.
let private queue (lane: Lane) (job: JobSpec) (label: string) (granted: ConcurrentQueue<string>) : Task<bool> =
    let pending = lane.Acquire(job, CancellationToken.None)

    async {
        let! entered = pending

        if entered then
            granted.Enqueue label

        return entered
    }
    |> Async.StartImmediateAsTask

/// Release `lane`'s slot for `job` and return the label it was handed to.
let private handOver (lane: Lane) (job: JobSpec) (granted: ConcurrentQueue<string>) : string =
    let before = granted.Count
    lane.Release job

    while granted.Count = before do
        Thread.Yield() |> ignore

    Seq.last granted

[<Fact(Timeout = 5000)>]
let ``an unweighted job is weight 1`` () =
    test <@ JobSpec.interactive.Weight = 1.0 && JobSpec.bulk.Weight = 1.0 @>
    test <@ (JobSpec.weighted 3.0 JobSpec.bulk) = { JobSpec.bulk with Weight = 3.0 } @>

[<Fact(Timeout = 5000)>]
let ``a slot held for a weight-3 job charges three times its slot-time`` () =
    let scheduler, clock = withClock 2
    let lane = scheduler.Lane "session"
    test <@ lane.Acquire(weighing 3.0, CancellationToken.None) |> Async.RunSynchronously @>

    test
        <@
            lane.Acquire(JobSpec.interactive, CancellationToken.None)
            |> Async.RunSynchronously
        @>

    clock.Advance 2.0
    lane.Release(weighing 3.0)
    clock.Advance 1.0
    lane.Release JobSpec.interactive

    let status = lane.Status()
    // Held is raw slot-time: 2 s + 3 s. Charged weighs it: 3 × 2 s + 1 × 3 s.
    test <@ status.Held = TimeSpan.FromSeconds 5.0 @>
    test <@ status.Charged = TimeSpan.FromSeconds 9.0 @>
    test <@ scheduler.Running = 0 @>

[<Fact(Timeout = 30000)>]
let ``a weight-3 session gets a third of the grants of a weight-1 session under contention`` () =
    let scheduler, clock = withClock 1
    let light = scheduler.Lane "light"
    let heavy = scheduler.Lane "heavy"

    let lanes =
        Map.ofList [ "light", (light, weighing 1.0); "heavy", (heavy, weighing 3.0) ]

    let granted = ConcurrentQueue<string>()

    test <@ (queue light (weighing 1.0) "light" granted).Result @>
    queue heavy (weighing 3.0) "heavy" granted |> ignore
    queue light (weighing 1.0) "light" granted |> ignore
    let mutable holder = "light"

    // Equal 5 s jobs; both sessions always have one more waiting. 400 hand-overs, each a
    // thread-pool hop: 2000 of them outlasted the timeout under a loaded full suite.
    while clock.Now() < TimeSpan.FromSeconds 2000.0 do
        let lane, job = lanes[holder]
        clock.Advance 5.0
        queue lane job holder granted |> ignore
        holder <- handOver lane job granted

    let grantsLight = float (light.Status()).Granted
    let grantsHeavy = float (heavy.Status()).Granted
    let ratio = grantsLight / grantsHeavy
    test <@ ratio >= 3.0 * 0.8 && ratio <= 3.0 * 1.2 @>

    let chargedLight = (light.Status()).Charged.TotalSeconds
    let chargedHeavy = (heavy.Status()).Charged.TotalSeconds
    test <@ abs (chargedLight - chargedHeavy) / max chargedLight chargedHeavy <= 0.2 @>

[<Fact(Timeout = 10000)>]
let ``a weighted run gives its slot back with its own weight`` () =
    let scheduler, clock = withClock 1
    let lane = scheduler.Lane "session"

    let run =
        lane.Run(
            weighing 2.0,
            async {
                clock.Advance 4.0
                return 1
            }
        )
        |> Async.RunSynchronously

    test <@ run = 1 @>
    test <@ scheduler.Running = 0 @>
    test <@ (lane.Status()).Charged = TimeSpan.FromSeconds 8.0 @>

[<Fact(Timeout = 5000)>]
let ``a weight must be a positive finite number`` () =
    for bad in [ 0.0; -1.0; nan; infinity ] do
        Assert.Throws<ArgumentOutOfRangeException>(fun () -> JobSpec.weighted bad JobSpec.interactive |> ignore)
        |> ignore

[<Fact(Timeout = 5000)>]
let ``the admission payload carries the weighted charge`` () =
    let scheduler, clock = withClock 1
    let lane = scheduler.Lane "session"
    test <@ lane.Acquire(weighing 2.0, CancellationToken.None) |> Async.RunSynchronously @>
    clock.Advance 1.5
    lane.Release(weighing 2.0)
    let payload = LaneStatus.payload (lane.Status())
    test <@ payload["heldMs"].GetValue<int64>() = 1500L @>
    test <@ payload["chargedMs"].GetValue<int64>() = 3000L @>

[<Fact(Timeout = 5000)>]
let ``an unweighted release gives back a weight-1 slot first, else the oldest slot held`` () =
    let scheduler, clock = withClock 3
    let lane = scheduler.Lane "session"
    test <@ lane.Acquire(weighing 2.0, CancellationToken.None) |> Async.RunSynchronously @>

    test
        <@
            lane.Acquire(JobSpec.interactive, CancellationToken.None)
            |> Async.RunSynchronously
        @>

    test <@ lane.Acquire(weighing 4.0, CancellationToken.None) |> Async.RunSynchronously @>
    clock.Advance 1.0
    // The weight-1 slot goes first: 2 + 1 + 4 charged for the first second.
    lane.Release()
    clock.Advance 1.0
    // No weight-1 slot is left, so the oldest (weight 2) goes: 2 + 4 for the next second.
    lane.Release()
    clock.Advance 1.0
    lane.Release(weighing 4.0)

    test <@ scheduler.Running = 0 @>
    test <@ (lane.Status()).Charged = TimeSpan.FromSeconds(7.0 + 6.0 + 4.0) @>

[<Fact(Timeout = 5000)>]
let ``a release at a weight the lane does not hold is refused`` () =
    let scheduler, _ = withClock 1
    let lane = scheduler.Lane "session"
    test <@ lane.Acquire(weighing 2.0, CancellationToken.None) |> Async.RunSynchronously @>

    Assert.Throws<InvalidOperationException>(fun () -> lane.Release(weighing 3.0))
    |> ignore

    test <@ scheduler.Running = 1 @>
    lane.Release(weighing 2.0)
    test <@ scheduler.Running = 0 @>
