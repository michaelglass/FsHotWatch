/// Measured slot-time fairness (AUTOMATION-682 slice 5). A waiting session is served while
/// the slot-time it has held is within a quantum of the least any waiting session has
/// held, so a session whose jobs are ten times as long gets a tenth of the grants, not
/// ten times the slot-time. A session that was idle catches up to the sessions that kept
/// working instead of coming back with banked credit. Measured against an injected clock.
module FsHotWatch.Tests.WorkSchedulerFairnessTests

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

/// Far beyond any wait here, so aging never decides a hand-over in these tests.
let private noAging = TimeSpan.FromHours 1.0

let private withClock (capacity: int) =
    let clock = ManualClock()
    Scheduler(capacity, ignore, clock.Now, noAging), clock

/// Queue one labelled interactive acquisition on `lane`; it queues (or is granted)
/// before this returns, and records its label when granted.
let private queue (lane: Lane) (label: string) (granted: ConcurrentQueue<string>) : Task<bool> =
    let pending = lane.Acquire(JobSpec.interactive, CancellationToken.None)

    async {
        let! entered = pending

        if entered then
            granted.Enqueue label

        return entered
    }
    |> Async.StartImmediateAsTask

/// Release `lane`'s slot and return the label it was handed to.
let private handOver (lane: Lane) (granted: ConcurrentQueue<string>) : string =
    let before = granted.Count
    lane.Release()

    while granted.Count = before do
        Thread.Yield() |> ignore

    Seq.last granted

[<Fact(Timeout = 10000)>]
let ``sessions with 1 s and 10 s jobs hold the slot for about the same time`` () =
    let scheduler, clock = withClock 1
    let short = scheduler.Lane "short"
    let long = scheduler.Lane "long"
    let lanes = Map.ofList [ "short", (short, 1.0); "long", (long, 10.0) ]
    let granted = ConcurrentQueue<string>()

    test <@ (queue short "short" granted).Result @>
    queue long "long" granted |> ignore
    queue short "short" granted |> ignore
    let mutable holder = "short"

    // Both sessions always have one more job waiting: continuous contention.
    while clock.Now() < TimeSpan.FromSeconds 2000.0 do
        let lane, jobSeconds = lanes[holder]
        clock.Advance jobSeconds
        queue lane holder granted |> ignore
        holder <- handOver lane granted

    let heldShort = (short.Status()).Held.TotalSeconds
    let heldLong = (long.Status()).Held.TotalSeconds
    let ratio = abs (heldShort - heldLong) / max heldShort heldLong

    test <@ ratio <= 0.2 @>
    // Round-robin by count would have given the long session ten times the slot-time.
    test <@ (short.Status()).Granted > 5L * (long.Status()).Granted @>

[<Fact(Timeout = 10000)>]
let ``a session that was idle comes back level with the busy one, not with banked credit`` () =
    let scheduler, clock = withClock 1
    let busy = scheduler.Lane "busy"
    let idle = scheduler.Lane "idle"
    let granted = ConcurrentQueue<string>()

    // The busy session runs alone for 50 s of 1 s jobs.
    test <@ (queue busy "busy" granted).Result @>

    for _ in 1..50 do
        clock.Advance 1.0
        queue busy "busy" granted |> ignore
        test <@ handOver busy granted = "busy" @>

    // The idle session wakes with a backlog; both keep a job waiting from here on.
    for _ in 1..6 do
        queue idle "idle" granted |> ignore

    let order = ResizeArray<string>()
    let mutable holder = "busy"

    for _ in 1..12 do
        clock.Advance 1.0
        let lane = if holder = "busy" then busy else idle

        if holder = "busy" then
            queue busy "busy" granted |> ignore

        holder <- handOver lane granted
        order.Add holder

    // Banked credit would have served the idle session's backlog all at once.
    let longestIdleRun =
        order
        |> Seq.fold
            (fun (run, longest) label ->
                let run = if label = "idle" then run + 1 else 0
                run, max longest run)
            (0, 0)
        |> snd

    test <@ longestIdleRun <= 2 @>
    test <@ order |> Seq.filter ((=) "idle") |> Seq.length >= 4 @>

[<Fact(Timeout = 10000)>]
let ``an aged bulk job is served first even when its session has held far more`` () =
    let clock = ManualClock()
    let scheduler = Scheduler(1, ignore, clock.Now, TimeSpan.FromSeconds 5.0)
    let scan = scheduler.Lane "scan"
    let saves = scheduler.Lane "saves"
    let granted = ConcurrentQueue<string>()

    // The scan session has held the slot for a long time.
    test <@ (queue scan "scan-0" granted).Result @>
    clock.Advance 100.0

    let tier =
        scan.Acquire(JobSpec.bulk, CancellationToken.None) |> Async.StartImmediateAsTask

    queue saves "save-1" granted |> ignore
    test <@ handOver scan granted = "save-1" @>

    // Saves keep coming; the tier ages past 5 s and is served despite its session's
    // hundred seconds of service.
    clock.Advance 5.0
    queue saves "save-2" granted |> ignore
    saves.Release()
    test <@ tier.Result @>

[<Fact(Timeout = 5000)>]
let ``the fairness quantum is 100 ms`` () =
    test <@ FairnessQuantum = TimeSpan.FromMilliseconds 100.0 @>
