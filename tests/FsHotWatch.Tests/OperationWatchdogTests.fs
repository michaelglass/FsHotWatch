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
    let state = { InFlight = []; Threshold = threshold }

    test <@ wedgeReport (t0.AddSeconds 500.0) state = None @>

[<Fact(Timeout = 5000)>]
let ``wedgeReport None when in flight but under threshold`` () =
    let state =
        { InFlight = [ { Name = "Scan"; StartedAt = t0 } ]
          Threshold = threshold }

    test <@ wedgeReport (t0.AddSeconds 10.0) state = None @>

[<Fact(Timeout = 5000)>]
let ``wedgeReport Some with WEDGED prefix, stuck op, elapsed, and inline recovery`` () =
    let state =
        { InFlight =
            [ { Name = "TriggerBuild"
                StartedAt = t0 } ]
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
    let state = { InFlight = []; Threshold = threshold }

    test <@ heartbeatLine t0 state = "heartbeat: idle" @>

[<Fact(Timeout = 5000)>]
let ``heartbeatLine names the in-flight op and elapsed`` () =
    let state =
        { InFlight = [ { Name = "WaitForScan"; StartedAt = t0 } ]
          Threshold = threshold }

    test <@ heartbeatLine (t0.AddSeconds 7.0) state = "heartbeat: in-flight WaitForScan running 7s" @>

[<Fact(Timeout = 5000)>]
let ``heartbeatLine names the OLDEST op and the count when several are in flight`` () =
    // Three acceptors is the IPC server's design, so several in flight is normal.
    // The heartbeat must not pretend there is only one — nor pick an arbitrary one.
    let state =
        { InFlight =
            [ { Name = "GetDiagnostics"
                StartedAt = t0.AddSeconds 5.0 }
              { Name = "WaitForComplete"
                StartedAt = t0 }
              { Name = "FormatAll"
                StartedAt = t0.AddSeconds 2.0 } ]
          Threshold = threshold }

    test <@ heartbeatLine (t0.AddSeconds 9.0) state = "heartbeat: 3 in-flight, oldest WaitForComplete running 9s" @>

[<Fact(Timeout = 5000)>]
let ``oldestOverrun picks the longest-running WEDGED op, ignoring young ones`` () =
    let ops =
        [ { Name = "young"
            StartedAt = t0.AddSeconds 200.0 } // 40s old at the observation time — fine
          { Name = "oldest-wedged"
            StartedAt = t0 } // 240s — wedged
          { Name = "also-wedged"
            StartedAt = t0.AddSeconds 30.0 } ] // 210s — wedged, but younger

    match oldestOverrun (t0.AddSeconds 240.0) threshold ops with
    | Some op -> test <@ op.Name = "oldest-wedged" @>
    | None -> Assert.Fail "expected an overrun"

    // Nothing has overrun yet at t0+10s.
    test <@ oldestOverrun (t0.AddSeconds 10.0) threshold ops = None @>

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

    test <@ List.isEmpty w.State.InFlight @>
    let token = w.Begin "WaitForComplete"

    match w.State.InFlight with
    | [ op ] -> test <@ op.Name = "WaitForComplete" @>
    | other -> Assert.Fail $"expected exactly one in-flight op after Begin, got %A{other}"

    w.End token
    test <@ List.isEmpty w.State.InFlight @>

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

    w.Begin "stuck-op" |> ignore
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

    w.Begin "wedged-rpc" |> ignore
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

    let first = w.Begin "first-op" // StartedAt = t0
    clock.Value <- t0.AddSeconds 100.0 // now wedged
    // Wait for the timer to record the first op's overrun before ending it, so
    // the episode-boundary is observed deterministically (no fixed-sleep race).
    waitUntil (fun () -> logcontains "first-op") 5000
    w.End first
    clock.Value <- t0.AddSeconds 200.0
    w.Begin "second-op" |> ignore // StartedAt = t0+200s
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

    let token = w.Begin "fast-op"
    Thread.Sleep(150) // clock never advances → never wedged
    w.End token
    Thread.Sleep(100)

    test <@ logged |> Seq.forall (fun l -> not (l.StartsWith("operation exceeded"))) @>

// ---------------------------------------------------------------------------
// AUTOMATION-98 finding 4 — CONCURRENT ops must not corrupt each other.
// ---------------------------------------------------------------------------
//
// The regression this pins: the watchdog held ONE `InFlightOp`. `Begin`
// overwrote it and `End()` cleared it unconditionally. The IPC server runs THREE
// acceptors concurrently by design, and two parallel `fshw check` clients on one
// box are routine — so op B's `Begin` erased op A's record, and A's `End` then
// erased B's. A genuinely wedged op consequently heartbeat as `idle` and
// `WedgeReport()` returned `None`: the diagnostic built for wedges went blind
// exactly under the concurrency it was built for.
//
// RED-BEFORE-GREEN: with the old single-slot `InFlightOp option`, the wedged op's
// record is gone by the time we look and `WedgeReport()` is `None`.

[<Fact(Timeout = 5000)>]
let ``a wedged op stays visible when a concurrent op begins and ends over it`` () =
    let clock = ref t0

    use w =
        new Watchdog(
            threshold,
            heartbeatEvery = TimeSpan.FromHours(1.0),
            now = (fun () -> clock.Value),
            log = ignore,
            tick = TimeSpan.FromHours(1.0) // no timer noise; we drive the clock
        )

    // A wedges: a `check` client blocked on WaitForComplete.
    w.Begin "WaitForComplete" |> ignore
    clock.Value <- t0.AddSeconds 300.0
    test <@ (w.WedgeReport() |> Option.isSome) @>

    // B is a SECOND client's short op that lands on a free acceptor and completes.
    // Under the old single-slot watchdog, this pair of calls silently erased A.
    let b = w.Begin "GetDiagnostics"
    w.End b

    // A is still wedged, and the watchdog still says so.
    match w.WedgeReport() with
    | Some msg -> test <@ msg.Contains("WaitForComplete") @>
    | None -> Assert.Fail "the wedged op vanished when a concurrent op ended — the watchdog is blind"

    test <@ w.State.InFlight |> List.map (fun o -> o.Name) = [ "WaitForComplete" ] @>
    test <@ heartbeatLine clock.Value w.State <> "heartbeat: idle" @>
