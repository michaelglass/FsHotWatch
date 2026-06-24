module FsHotWatch.Tests.OperationWatchdogTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.OperationWatchdog
open FsHotWatch.Tests.TestHelpers

// --- Pure decision functions ---

let private t0 = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
let private threshold = TimeSpan.FromSeconds(120.0)

[<Fact(Timeout = 5000)>]
let ``isWedgedAt false before threshold`` () =
    let op =
        { Name = "WaitForComplete"
          StartedAt = t0 }

    test <@ not (isWedgedAt (t0.AddSeconds 119.0) threshold op) @>

[<Fact(Timeout = 5000)>]
let ``isWedgedAt true at and past threshold`` () =
    let op =
        { Name = "WaitForComplete"
          StartedAt = t0 }

    test <@ isWedgedAt (t0.AddSeconds 120.0) threshold op @>
    test <@ isWedgedAt (t0.AddSeconds 300.0) threshold op @>

[<Fact(Timeout = 5000)>]
let ``overrunLogRecord names op + threshold + elapsed in a stable shape`` () =
    let op =
        { Name = "RunCommand:run-tests"
          StartedAt = t0 }

    let record = overrunLogRecord (t0.AddSeconds 200.0) threshold op
    // Stable, greppable shape: "operation exceeded Ns: <op> running Ms"
    test <@ record = "operation exceeded 120s: RunCommand:run-tests running 200s" @>

[<Fact(Timeout = 5000)>]
let ``wedgeReport None when nothing in flight`` () =
    let state =
        { InFlight = None
          Threshold = threshold }

    test <@ wedgeReport (t0.AddSeconds 500.0) state = None @>

[<Fact(Timeout = 5000)>]
let ``wedgeReport None when in flight but under threshold`` () =
    let state =
        { InFlight = Some { Name = "Scan"; StartedAt = t0 }
          Threshold = threshold }

    test <@ wedgeReport (t0.AddSeconds 10.0) state = None @>

[<Fact(Timeout = 5000)>]
let ``wedgeReport Some with WEDGED prefix, stuck op, elapsed, and inline recovery`` () =
    let state =
        { InFlight =
            Some
                { Name = "TriggerBuild"
                  StartedAt = t0 }
          Threshold = threshold }

    match wedgeReport (t0.AddSeconds 240.0) state with
    | None -> Assert.Fail "expected a wedge report"
    | Some msg ->
        test <@ msg.StartsWith("WEDGED:") @>
        test <@ msg.Contains("TriggerBuild") @>
        test <@ msg.Contains("running 240s") @>
        test <@ msg.Contains("exceeded 120s threshold") @>
        // Inline recovery action so the consumer needn't look it up.
        test <@ msg.Contains("fshw stop") @>
        test <@ msg.Contains(RecoveryAction) @>

[<Fact(Timeout = 5000)>]
let ``heartbeatLine reports idle when nothing in flight`` () =
    let state =
        { InFlight = None
          Threshold = threshold }

    test <@ heartbeatLine t0 state = "heartbeat: idle" @>

[<Fact(Timeout = 5000)>]
let ``heartbeatLine names the in-flight op and elapsed`` () =
    let state =
        { InFlight = Some { Name = "WaitForScan"; StartedAt = t0 }
          Threshold = threshold }

    test <@ heartbeatLine (t0.AddSeconds 7.0) state = "heartbeat: in-flight WaitForScan running 7s" @>

// --- Live Watchdog: Begin/End state tracking ---

[<Fact(Timeout = 5000)>]
let ``Watchdog tracks the in-flight op via Begin and clears via End`` () =
    let clock = ref t0

    use w =
        new Watchdog(
            threshold,
            heartbeatEvery = TimeSpan.FromSeconds(30.0),
            now = (fun () -> clock.Value),
            log = ignore,
            tick = TimeSpan.FromMilliseconds(50.0)
        )

    test <@ w.State.InFlight = None @>
    w.Begin "WaitForComplete"

    match w.State.InFlight with
    | Some op -> test <@ op.Name = "WaitForComplete" @>
    | None -> Assert.Fail "expected an in-flight op after Begin"

    w.End()
    test <@ w.State.InFlight = None @>

[<Fact(Timeout = 5000)>]
let ``Watchdog WedgeReport fires once the injected clock crosses threshold`` () =
    let clock = ref t0

    use w =
        new Watchdog(
            threshold,
            heartbeatEvery = TimeSpan.FromSeconds(30.0),
            now = (fun () -> clock.Value),
            log = ignore,
            tick = TimeSpan.FromMilliseconds(50.0)
        )

    w.Begin "stuck-op"
    // Not yet wedged.
    test <@ w.WedgeReport() = None @>
    // Advance the injected clock past the threshold.
    clock.Value <- t0.AddSeconds 130.0

    match w.WedgeReport() with
    | Some msg -> test <@ msg.Contains("stuck-op") @>
    | None -> Assert.Fail "expected a wedge report after threshold"

// --- Live Watchdog: injected stuck op makes the timer emit the overrun record ---

[<Fact(Timeout = 10000)>]
let ``Watchdog timer emits the structured overrun record exactly once for a stuck op`` () =
    // An injected stuck op (Begin then never End): once the injected clock
    // advances past the threshold, the watchdog timer must emit the "operation
    // exceeded Ns" record — and only once per overrun episode, not on every tick.
    let logged = System.Collections.Concurrent.ConcurrentBag<string>()
    // Clock at t0 when the op begins; the op's StartedAt is captured here.
    let clock = ref t0

    use w =
        new Watchdog(
            TimeSpan.FromSeconds(1.0),
            heartbeatEvery = TimeSpan.FromHours(1.0), // suppress heartbeat noise here
            now = (fun () -> clock.Value),
            log = logged.Add,
            tick = TimeSpan.FromMilliseconds(20.0)
        )

    w.Begin "wedged-rpc"
    // Advance the clock past the threshold so subsequent ticks see a wedge.
    clock.Value <- t0.AddSeconds 100.0

    let overrunsSoFar () =
        logged |> Seq.filter (fun l -> l.StartsWith("operation exceeded")) |> Seq.toList

    // Poll until the timer fires the overrun record (load-robust; the 20ms tick
    // can be delayed under parallel-suite CPU pressure). Once seen, let a few
    // more ticks pass and assert it stays at exactly one — once per episode.
    waitUntil (fun () -> not (overrunsSoFar ()).IsEmpty) 5000
    Thread.Sleep(200)

    let overruns = overrunsSoFar ()
    test <@ overruns.Length = 1 @>
    test <@ overruns.Head.Contains("wedged-rpc") @>

[<Fact(Timeout = 10000)>]
let ``Watchdog re-arms the overrun record for a new op after End`` () =
    let logged = System.Collections.Concurrent.ConcurrentBag<string>()
    // Clock far enough ahead of any op's StartedAt that each op reads as wedged
    // the moment a tick observes it (StartedAt is captured at Begin = clock at
    // that instant; we hold the clock fixed well past the 1s threshold relative
    // to the t0-based ops we begin here).
    let clock = ref t0

    use w =
        new Watchdog(
            TimeSpan.FromSeconds(1.0),
            heartbeatEvery = TimeSpan.FromHours(1.0),
            now = (fun () -> clock.Value),
            log = logged.Add,
            tick = TimeSpan.FromMilliseconds(20.0)
        )

    let logcontains (needle: string) =
        logged
        |> Seq.exists (fun l -> l.StartsWith("operation exceeded") && l.Contains(needle))

    w.Begin "first-op" // StartedAt = t0
    clock.Value <- t0.AddSeconds 100.0 // now wedged
    // Wait for the timer to record the first op's overrun before ending it, so
    // the episode-boundary is observed deterministically (no fixed-sleep race).
    waitUntil (fun () -> logcontains "first-op") 5000
    w.End()
    clock.Value <- t0.AddSeconds 200.0
    w.Begin "second-op" // StartedAt = t0+200s
    clock.Value <- t0.AddSeconds 300.0 // wedged again
    waitUntil (fun () -> logcontains "second-op") 5000

    test <@ logcontains "first-op" @>
    test <@ logcontains "second-op" @>

[<Fact(Timeout = 10000)>]
let ``Watchdog does not emit overrun for an op that ends before threshold`` () =
    let logged = System.Collections.Concurrent.ConcurrentBag<string>()
    let clock = ref t0

    use w =
        new Watchdog(
            TimeSpan.FromSeconds(60.0),
            heartbeatEvery = TimeSpan.FromHours(1.0),
            now = (fun () -> clock.Value),
            log = logged.Add,
            tick = TimeSpan.FromMilliseconds(20.0)
        )

    w.Begin "fast-op"
    Thread.Sleep(150) // clock never advances → never wedged
    w.End()
    Thread.Sleep(100)

    test <@ logged |> Seq.forall (fun l -> not (l.StartsWith("operation exceeded"))) @>
