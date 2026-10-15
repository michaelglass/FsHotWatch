/// A repository host's sessions share one bounded scheduler: the bound holds across
/// sessions, and a session with a long queue cannot keep another session's work
/// waiting behind all of it.
module FsHotWatch.Tests.WorkSchedulerTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.WorkScheduler

/// Waits until `condition` holds, failing the test after five seconds.
let private waitUntil (what: string) (condition: unit -> bool) =
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while not (condition ()) do
        if DateTime.UtcNow > deadline then
            failwith $"timed out waiting for %s{what}"

        Thread.Sleep 5

/// Queues one acquisition on `lane` that records the lane's name in `order` once it
/// is granted, and keeps the slot until the test releases it.
let private queue (lane: Lane) (order: ConcurrentQueue<string>) (granted: SemaphoreSlim) : Task<bool> =
    async {
        let! entered = lane.Acquire CancellationToken.None

        if entered then
            order.Enqueue lane.Name
            granted.Release() |> ignore

        return entered
    }
    |> Async.StartAsTask

/// Hands the held slot on `releases` times, one grant at a time, so the order the
/// grants were made in is the order recorded.
let private handOn (holder: Lane) (granted: SemaphoreSlim) (releases: int) =
    for _ in 1..releases do
        holder.Release()
        test <@ granted.Wait(TimeSpan.FromSeconds 5.0) @>

[<Fact(Timeout = 20000)>]
let ``no more work runs at once than the capacity, across every lane`` () =
    let scheduler = Scheduler 3
    let mutable inside = 0
    let mutable peak = 0

    let job (lane: Lane) =
        async {
            let! entered = lane.Acquire CancellationToken.None
            test <@ entered @>
            let now = Interlocked.Increment &inside

            lock scheduler (fun () -> peak <- max peak now)
            do! Async.Sleep 1
            Interlocked.Decrement &inside |> ignore
            lane.Release()
        }

    [ for session in 1..4 do
          let lane = scheduler.Lane $"session-%d{session}"

          for _ in 1..25 do
              job lane ]
    |> Async.Parallel
    |> Async.RunSynchronously
    |> ignore

    test <@ peak >= 1 && peak <= 3 @>
    test <@ scheduler.Running = 0 && scheduler.Queued = 0 @>

[<Fact(Timeout = 20000)>]
let ``a session with fifty queued jobs cannot keep another session's one job behind them`` () =
    let scheduler = Scheduler 1
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"
    let order = ConcurrentQueue<string>()
    use granted = new SemaphoreSlim(0)

    test <@ a.Acquire CancellationToken.None |> Async.RunSynchronously @>

    let flood = [ for _ in 1..50 -> queue a order granted ]
    waitUntil "A's fifty to queue" (fun () -> scheduler.Queued = 50)
    let one = queue b order granted
    waitUntil "B's one to queue" (fun () -> scheduler.Queued = 51)

    handOn a granted 51

    let served = List.ofSeq order
    // A is served once, then B: B waits behind one of A's jobs, not fifty.
    test <@ served |> List.findIndex ((=) "B") = 1 @>
    test <@ one.Result && flood |> List.forall _.Result @>

[<Fact(Timeout = 20000)>]
let ``waiting sessions are served in turn`` () =
    let scheduler = Scheduler 1
    let lanes = [ "A"; "B"; "C" ] |> List.map scheduler.Lane
    let holder = scheduler.Lane "holder"
    let order = ConcurrentQueue<string>()
    use granted = new SemaphoreSlim(0)

    test <@ holder.Acquire CancellationToken.None |> Async.RunSynchronously @>

    // Queued A, A, A, B, B, B, C, C, C: arrival order alone would serve them so.
    for lane in lanes do
        for _ in 1..3 do
            let before = scheduler.Queued
            queue lane order granted |> ignore
            waitUntil "the job to queue" (fun () -> scheduler.Queued = before + 1)

    handOn holder granted 9

    test <@ List.ofSeq order = [ "A"; "B"; "C"; "A"; "B"; "C"; "A"; "B"; "C" ] @>

[<Fact(Timeout = 20000)>]
let ``a waiter whose caller gives up takes no slot and holds up nobody`` () =
    let scheduler = Scheduler 1
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"
    let order = ConcurrentQueue<string>()
    use granted = new SemaphoreSlim(0)
    use cts = new CancellationTokenSource()

    test <@ a.Acquire CancellationToken.None |> Async.RunSynchronously @>
    let abandoned = a.Acquire cts.Token |> Async.StartAsTask
    waitUntil "the abandoned job to queue" (fun () -> scheduler.Queued = 1)
    let waiting = queue b order granted
    waitUntil "B to queue" (fun () -> scheduler.Queued = 2)

    cts.Cancel()

    test <@ not abandoned.Result @>
    test <@ scheduler.Queued = 1 @>

    handOn a granted 1

    test <@ waiting.Result && List.ofSeq order = [ "B" ] @>
    test <@ scheduler.Running = 1 && scheduler.Queued = 0 @>
    b.Release()
    test <@ scheduler.Running = 0 @>

[<Fact(Timeout = 20000)>]
let ``a lane keeps its place when one of its waiters gives up`` () =
    let scheduler = Scheduler 1
    let a = scheduler.Lane "A"
    let order = ConcurrentQueue<string>()
    use granted = new SemaphoreSlim(0)
    use cts = new CancellationTokenSource()

    test <@ a.Acquire CancellationToken.None |> Async.RunSynchronously @>
    let abandoned = a.Acquire cts.Token |> Async.StartAsTask
    waitUntil "the abandoned job to queue" (fun () -> scheduler.Queued = 1)
    let kept = queue a order granted
    waitUntil "the kept job to queue" (fun () -> scheduler.Queued = 2)

    cts.Cancel()
    test <@ not abandoned.Result @>

    handOn a granted 1

    test <@ kept.Result && List.ofSeq order = [ "A" ] @>
    a.Release()
    test <@ scheduler.Running = 0 && scheduler.Queued = 0 @>

[<Fact(Timeout = 5000)>]
let ``a waiter given up just after a slot was handed to it keeps the slot`` () =
    use cts = new CancellationTokenSource()
    // Gives up in the window between the hand-over and the waiter waking.
    let scheduler = Scheduler(1, cts.Cancel)
    let a = scheduler.Lane "A"
    let b = scheduler.Lane "B"

    test <@ a.Acquire CancellationToken.None |> Async.RunSynchronously @>
    let handedOver = b.Acquire cts.Token |> Async.StartAsTask
    waitUntil "B to queue" (fun () -> scheduler.Queued = 1)

    a.Release()

    test <@ cts.IsCancellationRequested @>
    test <@ handedOver.Result @>
    test <@ scheduler.Running = 1 && scheduler.Queued = 0 @>
    b.Release()
    test <@ scheduler.Running = 0 @>

[<Fact(Timeout = 5000)>]
let ``a caller that has already given up is refused at once`` () =
    let scheduler = Scheduler 1
    use cts = new CancellationTokenSource()
    cts.Cancel()

    test <@ not ((scheduler.Lane "A").Acquire cts.Token |> Async.RunSynchronously) @>
    test <@ scheduler.Running = 0 && scheduler.Queued = 0 @>

[<Fact(Timeout = 5000)>]
let ``releasing a slot nobody holds is a bug, not a no-op`` () =
    let scheduler = Scheduler 1
    raises<InvalidOperationException> <@ (scheduler.Lane "A").Release() @>

[<Fact(Timeout = 5000)>]
let ``a scheduler has at least one slot`` () =
    raises<ArgumentOutOfRangeException> <@ Scheduler 0 @>

[<Fact(Timeout = 15000)>]
let ``parallelWithin never has more jobs in flight than its bound, and keeps their order`` () =
    let gate = obj ()
    let inFlight = ref 0
    let peak = ref 0

    let job (i: int) =
        async {
            lock gate (fun () ->
                inFlight.Value <- inFlight.Value + 1
                peak.Value <- max peak.Value inFlight.Value)

            do! Async.Sleep 30
            lock gate (fun () -> inFlight.Value <- inFlight.Value - 1)
            return i
        }

    let results = parallelWithin 3 [ for i in 1..20 -> job i ] |> Async.RunSynchronously

    test <@ results = [| 1..20 |] @>
    test <@ peak.Value = 3 @>

[<Fact(Timeout = 5000)>]
let ``parallelWithin runs one at a time when asked for none`` () =
    let results =
        parallelWithin 0 [ async { return 1 }; async { return 2 } ]
        |> Async.RunSynchronously

    test <@ results = [| 1; 2 |] @>

/// The core library's source, next to this test project.
let private coreSources () =
    let dir =
        IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "FsHotWatch"))

    IO.Directory.GetFiles(dir, "*.fs")

/// Every code line of `path` that fans out with `Async.Parallel`, as "File.fs:line".
/// Comments are not code: a line comment's text, and doc comments, are dropped.
let private parallelSites (path: string) =
    IO.File.ReadAllLines path
    |> Array.indexed
    |> Array.choose (fun (index, line) ->
        let code =
            match line.IndexOf "//" with
            | -1 -> line
            | at -> line.Substring(0, at)

        if code.Contains "Async.Parallel" then
            Some($"%s{IO.Path.GetFileName path}:%d{index + 1}", code.Trim())
        else
            None)

[<Fact(Timeout = 5000)>]
let ``no core code fans out with an unbounded Async.Parallel`` () =
    // A tier or batch of checks fans out through `parallelWithin`, bounded by the
    // admission slots, so how much one fan-out holds live does not grow with the files.
    let sites = coreSources () |> Array.collect parallelSites

    test <@ not (Array.isEmpty sites) @>

    let unbounded =
        sites
        |> Array.filter (fun (_, code) -> not (code.Contains "maxDegreeOfParallelism"))
        |> Array.map fst
        |> List.ofArray

    test <@ List.isEmpty unbounded @>
