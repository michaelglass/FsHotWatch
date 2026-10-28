/// Each acquisition carries a `JobSpec` (AUTOMATION-682, AUTOMATION-681 slice 7). An
/// interactive job, such as the checks of a save, goes ahead of bulk work, such as a scan's
/// tiers, in its own lane and across lanes. A bulk job that has waited past the aging bound
/// counts as interactive, so a continuous run of saves cannot starve a scan. Measured
/// against an injected clock: every ordering here is exact.
module FsHotWatch.Tests.WorkSchedulerPriorityTests

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

let private aging = TimeSpan.FromSeconds 10.0

let private withClock (capacity: int) =
    let clock = ManualClock()
    Scheduler(capacity, ignore, clock.Now, aging), clock

/// One labelled acquisition. It queues (or is granted) before this returns.
let private acquire (lane: Lane) (job: JobSpec) (label: string) (granted: ConcurrentQueue<string>) : Task<bool> =
    let pending = lane.Acquire(job, CancellationToken.None)

    async {
        let! entered = pending

        if entered then
            granted.Enqueue label

        return entered
    }
    |> Async.StartImmediateAsTask

/// Release `lane`'s slot and return the label of whoever it was handed to.
let private handOver (lane: Lane) (granted: ConcurrentQueue<string>) : string =
    let before = granted.Count
    lane.Release()
    // The hand-over is decided under the lock before `Release` returns; the winner's task
    // completes off this thread, so wait for its label.
    while granted.Count = before do
        Thread.Yield() |> ignore

    Seq.last granted

[<Fact(Timeout = 5000)>]
let ``an unmarked acquisition is interactive`` () =
    let scheduler, _ = withClock 1
    let a = scheduler.Lane "A"

    test <@ (a.Acquire CancellationToken.None |> Async.StartImmediateAsTask).Result @>
    let status = a.Status()
    test <@ status.Interactive.Granted = 1L && status.Bulk.Granted = 0L @>

[<Fact(Timeout = 5000)>]
let ``in one lane an interactive job goes ahead of bulk work queued before it`` () =
    let scheduler, clock = withClock 1
    let a = scheduler.Lane "A"
    let granted = ConcurrentQueue<string>()

    test <@ (acquire a JobSpec.interactive "holder" granted).Result @>
    let bulk = acquire a JobSpec.bulk "bulk" granted
    clock.Advance 1.0
    let save = acquire a JobSpec.interactive "save" granted

    test <@ handOver a granted = "save" @>
    test <@ handOver a granted = "bulk" @>

[<Fact(Timeout = 5000)>]
let ``bulk work in one lane cannot keep another lane's interactive job waiting`` () =
    let scheduler, _ = withClock 1
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"
    let granted = ConcurrentQueue<string>()

    test <@ (acquire a JobSpec.bulk "a0" granted).Result @>
    // A's scan is first in the turns with three more tiers waiting.
    let aBulk = [ for i in 1..3 -> acquire a JobSpec.bulk $"a%d{i}" granted ]
    let bSave = acquire b JobSpec.interactive "b-save" granted

    test <@ handOver a granted = "b-save" @>
    // With no interactive work left, A's bulk is served in its turn.
    test <@ handOver b granted = "a1" @>
    test <@ aBulk.Length = 3 @>

[<Fact(Timeout = 5000)>]
let ``under a continuous run of saves a bulk job is served on the first hand-over after its aging bound`` () =
    let scheduler, clock = withClock 1
    let scan = scheduler.Lane "scan"
    let saves = scheduler.Lane "saves"
    let granted = ConcurrentQueue<string>()

    test <@ (acquire saves JobSpec.interactive "save-0" granted).Result @>
    let tier = acquire scan JobSpec.bulk "tier" granted

    // Every hand-over before the bound goes to a save, however long the run.
    for i in 1..5 do
        acquire saves JobSpec.interactive $"save-%d{i}" granted |> ignore
        clock.Advance 1.9
        test <@ handOver saves granted = $"save-%d{i}" @>

    // 9.5 s queued: still bulk.
    acquire saves JobSpec.interactive "save-6" granted |> ignore
    test <@ handOver saves granted = "save-6" @>
    test <@ not tier.IsCompleted @>

    // Past 10 s it counts as interactive, and it is first in the turns.
    clock.Advance 0.5
    acquire saves JobSpec.interactive "save-7" granted |> ignore
    test <@ handOver saves granted = "tier" @>
    test <@ tier.Result @>

    let status = scan.Status()
    test <@ status.Bulk.Granted = 1L && status.Bulk.Waited = TimeSpan.FromSeconds 10.0 @>

[<Fact(Timeout = 5000)>]
let ``in one lane an aged bulk job goes ahead of a newer interactive one`` () =
    let scheduler, clock = withClock 1
    let a = scheduler.Lane "A"
    let granted = ConcurrentQueue<string>()

    test <@ (acquire a JobSpec.interactive "holder" granted).Result @>
    acquire a JobSpec.bulk "old-bulk" granted |> ignore
    clock.Advance 11.0
    acquire a JobSpec.interactive "new-save" granted |> ignore

    test <@ handOver a granted = "old-bulk" @>
    test <@ handOver a granted = "new-save" @>

[<Fact(Timeout = 5000)>]
let ``among interactive heads, lanes still take turns`` () =
    let scheduler, _ = withClock 1
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"
    let granted = ConcurrentQueue<string>()

    test <@ (acquire a JobSpec.interactive "holder" granted).Result @>
    acquire a JobSpec.interactive "a1" granted |> ignore
    acquire a JobSpec.interactive "a2" granted |> ignore
    acquire b JobSpec.interactive "b1" granted |> ignore

    test <@ handOver a granted = "a1" @>
    test <@ handOver a granted = "b1" @>
    test <@ handOver b granted = "a2" @>

[<Fact(Timeout = 5000)>]
let ``a lane counts its grants, waits and give-ups per class`` () =
    let scheduler, clock = withClock 1
    let a = scheduler.Lane "A"
    let granted = ConcurrentQueue<string>()
    use cts = new CancellationTokenSource()

    test <@ (acquire a JobSpec.interactive "holder" granted).Result @>
    let bulk = acquire a JobSpec.bulk "bulk" granted
    let givenUp = a.Acquire(JobSpec.bulk, cts.Token) |> Async.StartImmediateAsTask
    clock.Advance 3.0
    cts.Cancel()
    test <@ not givenUp.Result @>
    test <@ handOver a granted = "bulk" @>
    test <@ bulk.Result @>

    let status = a.Status()

    let expectedInteractive: ClassStatus =
        { Granted = 1L
          Abandoned = 0L
          Waited = TimeSpan.Zero
          LongestWait = TimeSpan.Zero }

    let expectedBulk: ClassStatus =
        { Granted = 1L
          Abandoned = 1L
          Waited = TimeSpan.FromSeconds 3.0
          LongestWait = TimeSpan.FromSeconds 3.0 }

    test <@ status.Interactive = expectedInteractive @>
    test <@ status.Bulk = expectedBulk @>

    // The lane's totals are the classes' sums.
    test
        <@
            status.Granted = 2L
            && status.Abandoned = 1L
            && status.Waited = TimeSpan.FromSeconds 3.0
        @>

[<Fact(Timeout = 5000)>]
let ``a class's counts are a JSON object in the lane's status`` () =
    let classStatus granted =
        ({ Granted = granted
           Abandoned = 0L
           Waited = TimeSpan.FromMilliseconds 250.0
           LongestWait = TimeSpan.FromMilliseconds 250.0 }
        : ClassStatus)

    let json = (ClassStatus.payload (classStatus 4L)).ToJsonString()

    test <@ json = """{"granted":4,"abandoned":0,"waitedMs":250,"longestWaitMs":250}""" @>

[<Fact(Timeout = 5000)>]
let ``the default aging bound is five seconds`` () =
    test <@ DefaultAgingBound = TimeSpan.FromSeconds 5.0 @>
