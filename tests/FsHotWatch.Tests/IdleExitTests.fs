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

// --- resolvePressureFloor ---

[<Fact>]
let ``pressure floor absent resolves to default 2`` () =
    test <@ resolvePressureFloor PressureFloorConfig.Absent = Some 2 @>

[<Fact>]
let ``pressure floor disabled resolves to None`` () =
    test <@ resolvePressureFloor PressureFloorConfig.Disabled = None @>

[<Fact>]
let ``pressure floor positive N resolves to Some N`` () =
    test <@ resolvePressureFloor (PressureFloorConfig.Minutes 5) = Some 5 @>

[<Fact>]
let ``pressure floor non-positive N resolves to None`` () =
    test <@ resolvePressureFloor (PressureFloorConfig.Minutes 0) = None @>
    test <@ resolvePressureFloor (PressureFloorConfig.Minutes -3) = None @>

// --- effectiveThreshold (pressure shortens an eligible window only) ---

[<Fact>]
let ``not eligible stays None even under pressure`` () =
    // The default/main workspace (baseThreshold None) is exempt under pressure:
    // pressure NEVER creates a window. Explicit product decision.
    test <@ effectiveThreshold None true (Some 2) = None @>
    test <@ effectiveThreshold None false (Some 2) = None @>

[<Fact>]
let ``eligible without pressure uses the base window`` () =
    test <@ effectiveThreshold (Some 30) false (Some 2) = Some 30 @>

[<Fact>]
let ``eligible under pressure shortens to min(base, floor)`` () =
    test <@ effectiveThreshold (Some 30) true (Some 2) = Some 2 @>

[<Fact>]
let ``floor never lengthens a window already smaller`` () =
    // Some 1 + pressure + floor 2 → min(1,2) = 1.
    test <@ effectiveThreshold (Some 1) true (Some 2) = Some 1 @>

[<Fact>]
let ``floor disabled ignores pressure and uses the base window`` () =
    test <@ effectiveThreshold (Some 30) true None = Some 30 @>

// --- runTick wiring (injected effects) ---

let private makeDeps (idleMinutes: float) (busy: bool) =
    let shutdownCalls = ref 0
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          PressureFloorMin = Some 2
          Pressure = fun () -> false
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
        { BaseThresholdMin = 30
          PressureFloorMin = Some 2
          Pressure = fun () -> false
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

// --- runTick: pressure shortens the effective window ---

let private makePressureDeps (idleMinutes: float) (pressure: bool ref) (floor: int option) =
    let shutdownCalls = ref 0
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          PressureFloorMin = floor
          Pressure = fun () -> pressure.Value
          Now = fun () -> now
          Busy = fun () -> false
          LastActivityAt = fun () -> now.AddMinutes(-idleMinutes)
          Shutdown = fun () -> Interlocked.Increment(shutdownCalls) |> ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    deps, shutdownCalls, logs

[<Fact>]
let ``runTick under pressure fires at the 2min floor, not before`` () =
    // Base 30min, floor 2min, idle 1min, under pressure → 1 < 2 → no fire.
    let deps, shutdownCalls, _ = makePressureDeps 1.0 (ref true) (Some 2)
    test <@ runTick deps (FireLatch.create ()) = false @>
    test <@ shutdownCalls.Value = 0 @>

[<Fact>]
let ``runTick under pressure fires at the floor boundary`` () =
    // idle 2min == floor → fires (well below the 30min base).
    let deps, shutdownCalls, logs = makePressureDeps 2.0 (ref true) (Some 2)
    test <@ runTick deps (FireLatch.create ()) = true @>
    test <@ shutdownCalls.Value = 1 @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "memory pressure shortened") @>

[<Fact>]
let ``runTick re-evaluates pressure each tick - subsiding restores the full window`` () =
    // idle 2min, floor 2min. Under pressure → fires at 2min; but with pressure
    // OFF the same idle (2min < 30min base) must NOT fire — pressure is a live
    // read, not latched.
    let pressure = ref false
    let deps, shutdownCalls, _ = makePressureDeps 2.0 pressure (Some 2)
    let latch = FireLatch.create ()

    // No pressure: 2min idle is well under the 30min base → defer.
    test <@ runTick deps latch = false @>
    test <@ shutdownCalls.Value = 0 @>

    // Pressure rises: effective window drops to 2min → fires now.
    pressure.Value <- true
    test <@ runTick deps latch = true @>
    test <@ shutdownCalls.Value = 1 @>

[<Fact>]
let ``runTick with floor disabled ignores pressure`` () =
    // Floor None, idle 2min, under pressure → still uses the 30min base → defer.
    let deps, shutdownCalls, _ = makePressureDeps 2.0 (ref true) None
    test <@ runTick deps (FireLatch.create ()) = false @>
    test <@ shutdownCalls.Value = 0 @>

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
        { BaseThresholdMin = 30
          PressureFloorMin = Some 2
          Pressure = fun () -> false
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
        { BaseThresholdMin = 30
          PressureFloorMin = Some 2
          Pressure = fun () -> false
          // Activity is "now" so the timer never fires within the test window.
          Now = fun () -> now
          Busy = fun () -> false
          LastActivityAt = fun () -> now
          Shutdown = ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    use timer = createTimer deps

    test <@ logs |> Seq.exists (fun m -> m.Contains "[idle-exit] enabled") @>
    ignore timer
