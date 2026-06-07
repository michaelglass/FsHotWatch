module FsHotWatch.Tests.IdleExitTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.IdleExit

let private threshold = TimeSpan.FromMinutes(30.0)

// --- shouldFire (pure decision) ---

[<Fact>]
let ``below threshold does not fire`` () =
    test <@ shouldFire threshold (TimeSpan.FromMinutes 29.0) false false = false @>

[<Fact>]
let ``at threshold boundary fires`` () =
    test <@ shouldFire threshold threshold false false = true @>

[<Fact>]
let ``past threshold fires`` () =
    test <@ shouldFire threshold (TimeSpan.FromMinutes 31.0) false false = true @>

[<Fact>]
let ``busy defers even past threshold`` () =
    test <@ shouldFire threshold (TimeSpan.FromMinutes 31.0) true false = false @>

[<Fact>]
let ``already fired never fires again`` () =
    test <@ shouldFire threshold (TimeSpan.FromMinutes 31.0) false true = false @>

// --- FireLatch atomicity ---

[<Fact>]
let ``fresh latch has not fired`` () =
    let latch = FireLatch.create ()
    test <@ FireLatch.hasFired latch = false @>

[<Fact>]
let ``tryFire succeeds once then fails`` () =
    let latch = FireLatch.create ()
    test <@ FireLatch.tryFire latch = true @>
    test <@ FireLatch.tryFire latch = false @>
    test <@ FireLatch.hasFired latch = true @>

[<Fact>]
let ``latch fires exactly once under heavy concurrency`` () =
    // Hammer the latch from many threads; exactly one tryFire must win.
    let latch = FireLatch.create ()
    let wins = ref 0

    Parallel.For(
        0,
        10_000,
        fun _ ->
            if FireLatch.tryFire latch then
                Interlocked.Increment(wins) |> ignore
    )
    |> ignore

    test <@ wins.Value = 1 @>

// --- isNonDefaultWorkspace ---

[<Fact>]
let ``workspaces segment matches`` () =
    test <@ isNonDefaultWorkspace "/Users/me/dev/FsHotWatch/.workspaces/idle-exit" = true @>

[<Fact>]
let ``nested deeper under workspaces matches`` () =
    test <@ isNonDefaultWorkspace "/Users/me/dev/repo/.workspaces/feature/sub/dir" = true @>

[<Fact>]
let ``default repo root does not match`` () =
    test <@ isNonDefaultWorkspace "/Users/me/dev/FsHotWatch" = false @>

[<Fact>]
let ``workspaces as filename substring does not match`` () =
    test <@ isNonDefaultWorkspace "/Users/me/dev/foo.workspaces.bak" = false @>

[<Fact>]
let ``workspaces with no trailing child still matches`` () =
    test <@ isNonDefaultWorkspace "/Users/me/dev/repo/.workspaces" = true @>

[<Fact>]
let ``windows separators normalized and match`` () =
    test <@ isNonDefaultWorkspace "C:\\dev\\repo\\.workspaces\\feature" = true @>

[<Fact>]
let ``empty path does not match`` () =
    test <@ isNonDefaultWorkspace "" = false @>

// --- resolveThreshold ---

[<Fact>]
let ``absent + workspaces path resolves to auto 30`` () =
    test <@ resolveThreshold IdleExitConfig.Absent "/dev/repo/.workspaces/x" = Some 30 @>

[<Fact>]
let ``absent + normal path resolves to None`` () =
    test <@ resolveThreshold IdleExitConfig.Absent "/dev/repo" = None @>

[<Fact>]
let ``disabled resolves to None in workspace`` () =
    test <@ resolveThreshold IdleExitConfig.Disabled "/dev/repo/.workspaces/x" = None @>

[<Fact>]
let ``disabled resolves to None in default repo`` () =
    test <@ resolveThreshold IdleExitConfig.Disabled "/dev/repo" = None @>

[<Fact>]
let ``positive N resolves to Some N in default repo`` () =
    test <@ resolveThreshold (IdleExitConfig.Minutes 5) "/dev/repo" = Some 5 @>

[<Fact>]
let ``positive N resolves to Some N in workspace`` () =
    test <@ resolveThreshold (IdleExitConfig.Minutes 5) "/dev/repo/.workspaces/x" = Some 5 @>

[<Fact>]
let ``non-positive explicit N resolves to None`` () =
    test <@ resolveThreshold (IdleExitConfig.Minutes 0) "/dev/repo/.workspaces/x" = None @>
    test <@ resolveThreshold (IdleExitConfig.Minutes -3) "/dev/repo/.workspaces/x" = None @>

// --- runTick wiring (injected effects) ---

let private makeDeps (idleMinutes: float) (busy: bool) =
    let shutdownCalls = ref 0
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { Threshold = threshold
          Now = fun () -> now
          Busy = fun () -> busy
          LastActivityAt = fun () -> now.AddMinutes(-idleMinutes)
          Shutdown = fun () -> Interlocked.Increment(shutdownCalls) |> ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    deps, shutdownCalls, logs

[<Fact>]
let ``runTick fires shutdown and logs when idle past threshold`` () =
    let deps, shutdownCalls, logs = makeDeps 31.0 false
    let latch = FireLatch.create ()

    let fired = runTick deps latch

    test <@ fired = true @>
    test <@ shutdownCalls.Value = 1 @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "[idle-exit] idle for") @>
    test <@ FireLatch.hasFired latch @>

[<Fact>]
let ``runTick does not fire below threshold`` () =
    let deps, shutdownCalls, _ = makeDeps 10.0 false
    let latch = FireLatch.create ()

    let fired = runTick deps latch

    test <@ fired = false @>
    test <@ shutdownCalls.Value = 0 @>

[<Fact>]
let ``runTick defers when busy then fires when free`` () =
    let shutdownCalls = ref 0
    let busy = ref true
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { Threshold = threshold
          Now = fun () -> now
          Busy = fun () -> busy.Value
          LastActivityAt = fun () -> now.AddMinutes(-31.0)
          Shutdown = fun () -> Interlocked.Increment(shutdownCalls) |> ignore
          Log = ignore }

    let latch = FireLatch.create ()

    // Busy at threshold → defer.
    test <@ runTick deps latch = false @>
    test <@ shutdownCalls.Value = 0 @>

    // Work finishes; still idle past threshold → now fires.
    busy.Value <- false
    test <@ runTick deps latch = true @>
    test <@ shutdownCalls.Value = 1 @>

[<Fact>]
let ``runTick fires shutdown at most once across concurrent ticks`` () =
    let deps, shutdownCalls, _ = makeDeps 31.0 false
    let latch = FireLatch.create ()

    // Hammer runTick from many threads simultaneously — the prior experiment's
    // non-atomic latch fired 7x in 41ms here; the atomic latch must fire once.
    Parallel.For(0, 5_000, fun _ -> runTick deps latch |> ignore) |> ignore

    test <@ shutdownCalls.Value = 1 @>

[<Fact>]
let ``runTick swallows a throwing shutdown without escaping`` () =
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { Threshold = threshold
          Now = fun () -> now
          Busy = fun () -> false
          LastActivityAt = fun () -> now.AddMinutes(-31.0)
          Shutdown = fun () -> failwith "boom"
          Log = fun msg -> logs.Add msg }

    let latch = FireLatch.create ()

    // Must not throw out of runTick.
    let fired = runTick deps latch

    test <@ fired = false @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "tick failed") @>

// --- createTimer ---

[<Fact>]
let ``createTimer logs the enable line and is disposable`` () =
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { Threshold = threshold
          // Activity is "now" so the timer never fires within the test window.
          Now = fun () -> now
          Busy = fun () -> false
          LastActivityAt = fun () -> now
          Shutdown = ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    use timer = createTimer deps

    test <@ logs |> Seq.exists (fun m -> m.Contains "[idle-exit] enabled") @>
    ignore timer
