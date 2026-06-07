module FsHotWatch.Tests.PressureTrimTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.PressureTrim

// A round GiB to keep the byte arithmetic readable.
let private gib (n: int64) = n * 1024L * 1024L * 1024L

let private baseTicks = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc).Ticks

let private now0 = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

// --- resolvePct (config → effective percentage) ---

[<Fact>]
let ``absent resolves to default-enabled 100`` () =
    test <@ resolvePct PressureTrimConfig.Absent = Some 100 @>

[<Fact>]
let ``disabled resolves to None`` () =
    test <@ resolvePct PressureTrimConfig.Disabled = None @>

[<Fact>]
let ``positive N resolves to Some N`` () =
    test <@ resolvePct (PressureTrimConfig.Pct 80) = Some 80 @>
    test <@ resolvePct (PressureTrimConfig.Pct 120) = Some 120 @>

[<Fact>]
let ``non-positive explicit N resolves to None`` () =
    test <@ resolvePct (PressureTrimConfig.Pct 0) = None @>
    test <@ resolvePct (PressureTrimConfig.Pct -5) = None @>

// --- shouldTrim: pressure threshold semantics ---

[<Fact>]
let ``below threshold does not fire`` () =
    // load 7GiB, threshold 8GiB, pct 100 → trigger 8GiB → 7 < 8 → no fire
    test <@ shouldTrim 100 (gib 7L) (gib 8L) false Int64.MinValue baseTicks = false @>

[<Fact>]
let ``at threshold boundary fires`` () =
    test <@ shouldTrim 100 (gib 8L) (gib 8L) false Int64.MinValue baseTicks = true @>

[<Fact>]
let ``above threshold fires`` () =
    test <@ shouldTrim 100 (gib 9L) (gib 8L) false Int64.MinValue baseTicks = true @>

[<Fact>]
let ``pct 80 fires earlier than 100`` () =
    // threshold 10GiB, load 8GiB. pct 80 → trigger 8GiB → fires; pct 100 → no.
    test <@ shouldTrim 80 (gib 8L) (gib 10L) false Int64.MinValue baseTicks = true @>
    test <@ shouldTrim 100 (gib 8L) (gib 10L) false Int64.MinValue baseTicks = false @>

[<Fact>]
let ``pct 120 only fires beyond the GC threshold`` () =
    // threshold 10GiB. At load 11GiB: pct 100 fires, pct 120 (trigger 12GiB) no.
    test <@ shouldTrim 100 (gib 11L) (gib 10L) false Int64.MinValue baseTicks = true @>
    test <@ shouldTrim 120 (gib 11L) (gib 10L) false Int64.MinValue baseTicks = false @>
    // At 12GiB pct 120 fires.
    test <@ shouldTrim 120 (gib 12L) (gib 10L) false Int64.MinValue baseTicks = true @>

[<Fact>]
let ``zero threshold never fires`` () =
    // Defensive: a GC reporting a 0 high-load threshold must not divide-trigger.
    test <@ shouldTrim 100 (gib 8L) 0L false Int64.MinValue baseTicks = false @>

// --- shouldTrim: busy deferral ---

[<Fact>]
let ``busy defers even under pressure`` () =
    test <@ shouldTrim 100 (gib 9L) (gib 8L) true Int64.MinValue baseTicks = false @>

// --- shouldTrim: cooldown ---

[<Fact>]
let ``within cooldown does not fire even under pressure`` () =
    // Last fired 1 minute ago; cooldown is 5 minutes → no fire.
    let lastFired = baseTicks - TimeSpan.FromMinutes(1.0).Ticks
    test <@ shouldTrim 100 (gib 9L) (gib 8L) false lastFired baseTicks = false @>

[<Fact>]
let ``after cooldown fires again`` () =
    // Last fired 6 minutes ago; cooldown is 5 minutes → fires.
    let lastFired = baseTicks - TimeSpan.FromMinutes(6.0).Ticks
    test <@ shouldTrim 100 (gib 9L) (gib 8L) false lastFired baseTicks = true @>

[<Fact>]
let ``exactly at cooldown boundary fires`` () =
    let lastFired = baseTicks - Cooldown.Ticks
    test <@ shouldTrim 100 (gib 9L) (gib 8L) false lastFired baseTicks = true @>

// --- CooldownLatch atomicity ---

[<Fact>]
let ``fresh latch reports sentinel last-fired`` () =
    let latch = CooldownLatch.create ()
    test <@ CooldownLatch.lastFired latch = Int64.MinValue @>

[<Fact>]
let ``tryClaim succeeds once per observed value then fails`` () =
    let latch = CooldownLatch.create ()
    let observed = CooldownLatch.lastFired latch
    test <@ CooldownLatch.tryClaim latch observed baseTicks = true @>
    // Re-claiming against the stale observed value fails (field has moved).
    test <@ CooldownLatch.tryClaim latch observed (baseTicks + 1L) = false @>
    test <@ CooldownLatch.lastFired latch = baseTicks @>

[<Fact>]
let ``tryClaim is atomic under heavy concurrency`` () =
    // Hammer the claim from many threads against the SAME observed value;
    // exactly one must win — mirrors IdleExit's FireLatch concurrency test.
    let latch = CooldownLatch.create ()
    let observed = CooldownLatch.lastFired latch
    let wins = ref 0

    Parallel.For(
        0,
        10_000,
        fun i ->
            if CooldownLatch.tryClaim latch observed (baseTicks + int64 i) then
                Interlocked.Increment(wins) |> ignore
    )
    |> ignore

    test <@ wins.Value = 1 @>

// --- ScanInProgress flag (cold-scan busy signal) ---

[<Fact>]
let ``ScanInProgress starts not-scanning`` () =
    let flag = ScanInProgress.create ()
    test <@ ScanInProgress.isScanning flag = false @>

[<Fact>]
let ``ScanInProgress enter then exit toggles the flag`` () =
    let flag = ScanInProgress.create ()
    ScanInProgress.enter flag
    test <@ ScanInProgress.isScanning flag = true @>
    ScanInProgress.exit flag
    test <@ ScanInProgress.isScanning flag = false @>

// --- composeBusy (plugin-busy OR cold-scan-in-progress) ---

[<Fact>]
let ``composeBusy is true while a cold scan is in progress even if no plugin is busy`` () =
    // The defect: a cold scan drives FCS directly, not through a plugin, so
    // AnyPluginBusy alone is false during the scan. The composed predicate must
    // still report busy so the trim defers and does not wipe scan-built caches.
    let flag = ScanInProgress.create ()
    let busy = composeBusy (fun () -> false) (fun () -> ScanInProgress.isScanning flag)
    test <@ busy () = false @>
    ScanInProgress.enter flag
    test <@ busy () = true @>
    ScanInProgress.exit flag
    test <@ busy () = false @>

[<Fact>]
let ``composeBusy is true when any plugin is busy regardless of scan state`` () =
    let busy = composeBusy (fun () -> true) (fun () -> false)
    test <@ busy () = true @>

[<Fact>]
let ``composeBusy is false only when neither plugin-busy nor scanning`` () =
    test <@ (composeBusy (fun () -> false) (fun () -> false)) () = false @>
    test <@ (composeBusy (fun () -> true) (fun () -> true)) () = true @>

[<Fact>]
let ``runTick defers while a cold scan is in progress and fires once it settles`` () =
    // End-to-end through runTick using the composed scan-aware busy predicate:
    // under pressure but mid-cold-scan the trim must DEFER and NOT consume the
    // cooldown; once the scan settles the very next tick fires.
    let flag = ScanInProgress.create ()
    ScanInProgress.enter flag
    let trimCalls = ref 0

    let deps: PressureTrimDeps =
        { Pct = 100
          Now = fun () -> now0
          ReadMemory = fun () -> (gib 9L, gib 8L)
          Busy = composeBusy (fun () -> false) (fun () -> ScanInProgress.isScanning flag)
          Trim = fun () -> Interlocked.Increment(trimCalls) |> ignore
          Log = ignore }

    let latch = CooldownLatch.create ()

    // Mid-scan: defer, latch untouched (cooldown not consumed).
    test <@ runTick deps latch = false @>
    test <@ trimCalls.Value = 0 @>
    test <@ CooldownLatch.lastFired latch = Int64.MinValue @>

    // Scan settles; still under pressure → fires now.
    ScanInProgress.exit flag
    test <@ runTick deps latch = true @>
    test <@ trimCalls.Value = 1 @>

// --- runTick wiring (injected effects) ---

let private makeDeps (pct: int) (load: int64) (threshold: int64) (busy: bool) (now: DateTime) =
    let trimCalls = ref 0
    let logs = ResizeArray<string>()

    let deps: PressureTrimDeps =
        { Pct = pct
          Now = fun () -> now
          ReadMemory = fun () -> (load, threshold)
          Busy = fun () -> busy
          Trim = fun () -> Interlocked.Increment(trimCalls) |> ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    deps, trimCalls, logs

[<Fact>]
let ``runTick trims and logs when under pressure`` () =
    let deps, trimCalls, logs = makeDeps 100 (gib 9L) (gib 8L) false now0
    let latch = CooldownLatch.create ()

    let fired = runTick deps latch

    test <@ fired = true @>
    test <@ trimCalls.Value = 1 @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "[pressure-trim] memory load") @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "released FCS root caches") @>
    test <@ CooldownLatch.lastFired latch = now0.Ticks @>

[<Fact>]
let ``runTick does not trim below threshold`` () =
    let deps, trimCalls, _ = makeDeps 100 (gib 7L) (gib 8L) false now0
    let latch = CooldownLatch.create ()

    let fired = runTick deps latch

    test <@ fired = false @>
    test <@ trimCalls.Value = 0 @>

[<Fact>]
let ``runTick defers when busy and does not consume cooldown`` () =
    // Busy under pressure → defer. The latch must remain at the sentinel so the
    // very next free tick can fire (deferral does NOT consume the cooldown).
    let busy = ref true
    let trimCalls = ref 0

    let deps: PressureTrimDeps =
        { Pct = 100
          Now = fun () -> now0
          ReadMemory = fun () -> (gib 9L, gib 8L)
          Busy = fun () -> busy.Value
          Trim = fun () -> Interlocked.Increment(trimCalls) |> ignore
          Log = ignore }

    let latch = CooldownLatch.create ()

    test <@ runTick deps latch = false @>
    test <@ trimCalls.Value = 0 @>
    // Cooldown not consumed: latch still at sentinel.
    test <@ CooldownLatch.lastFired latch = Int64.MinValue @>

    // Work finishes; still under pressure → fires now.
    busy.Value <- false
    test <@ runTick deps latch = true @>
    test <@ trimCalls.Value = 1 @>

[<Fact>]
let ``runTick within cooldown does not re-fire under sustained pressure`` () =
    let now = ref now0

    let deps: PressureTrimDeps =
        { Pct = 100
          Now = fun () -> now.Value
          ReadMemory = fun () -> (gib 9L, gib 8L)
          Busy = fun () -> false
          Trim = ignore
          Log = ignore }

    let latch = CooldownLatch.create ()

    // First tick fires.
    test <@ runTick deps latch = true @>
    // 1 minute later, still under pressure → cooldown blocks.
    now.Value <- now0.AddMinutes(1.0)
    test <@ runTick deps latch = false @>

[<Fact>]
let ``runTick fires again after cooldown elapses`` () =
    let now = ref now0
    let trimCalls = ref 0

    let deps: PressureTrimDeps =
        { Pct = 100
          Now = fun () -> now.Value
          ReadMemory = fun () -> (gib 9L, gib 8L)
          Busy = fun () -> false
          Trim = fun () -> Interlocked.Increment(trimCalls) |> ignore
          Log = ignore }

    let latch = CooldownLatch.create ()

    test <@ runTick deps latch = true @>
    // 6 minutes later (> 5min cooldown) → fires again.
    now.Value <- now0.AddMinutes(6.0)
    test <@ runTick deps latch = true @>
    test <@ trimCalls.Value = 2 @>

[<Fact>]
let ``runTick trims at most once across concurrent ticks under pressure`` () =
    // Hammer runTick from many threads at the same instant under pressure; the
    // atomic cooldown claim must let exactly one tick trim.
    let deps, trimCalls, _ = makeDeps 100 (gib 9L) (gib 8L) false now0
    let latch = CooldownLatch.create ()

    Parallel.For(0, 5_000, fun _ -> runTick deps latch |> ignore) |> ignore

    test <@ trimCalls.Value = 1 @>

[<Fact>]
let ``runTick swallows a throwing trim without escaping`` () =
    let logs = ResizeArray<string>()

    let deps: PressureTrimDeps =
        { Pct = 100
          Now = fun () -> now0
          ReadMemory = fun () -> (gib 9L, gib 8L)
          Busy = fun () -> false
          Trim = fun () -> failwith "boom"
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    let latch = CooldownLatch.create ()

    let fired = runTick deps latch

    test <@ fired = false @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "trim failed") @>

[<Fact>]
let ``runTick swallows a throwing memory reader without escaping`` () =
    let logs = ResizeArray<string>()

    let deps: PressureTrimDeps =
        { Pct = 100
          Now = fun () -> now0
          ReadMemory = fun () -> failwith "no gc"
          Busy = fun () -> false
          Trim = ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    let latch = CooldownLatch.create ()

    let fired = runTick deps latch

    test <@ fired = false @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "tick failed") @>

// --- createTimer ---

[<Fact>]
let ``createTimer logs the enable line and is disposable`` () =
    let logs = ResizeArray<string>()

    let deps: PressureTrimDeps =
        { Pct = 100
          Now = fun () -> now0
          // No pressure, so the timer never trims within the test window.
          ReadMemory = fun () -> (gib 1L, gib 8L)
          Busy = fun () -> false
          Trim = ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    use timer = createTimer deps

    test <@ logs |> Seq.exists (fun m -> m.Contains "[pressure-trim] enabled") @>
    ignore timer
