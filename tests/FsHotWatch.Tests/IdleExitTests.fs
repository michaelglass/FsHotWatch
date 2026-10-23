module FsHotWatch.Tests.IdleExitTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.IdleExit

let private threshold = TimeSpan.FromMinutes(30.0)

// --- idleInhibitors (what forbids an idle exit, named) ---

[<Fact>]
let ``idleInhibitors names an in-flight verdict wait`` () =
    // Regression: an in-flight WaitForComplete (connected check
    // client) must inhibit idle-exit even when every plugin mailbox is quiet.
    test <@ idleInhibitors false 1 [] = [ IdleInhibitor.VerdictWait 1 ] @>
    test <@ idleInhibitors false 3 [] = [ IdleInhibitor.VerdictWait 3 ] @>

[<Fact>]
let ``idleInhibitors names an in-flight plugin mailbox`` () =
    test <@ idleInhibitors true 0 [] = [ IdleInhibitor.PluginBusy ] @>

[<Fact>]
let ``idleInhibitors names an in-flight cold scan`` () =
    // the leg that did not exist. A cold `fshw check` spends its
    // first minutes in performScan with no plugin mailbox in flight and no
    // verdict wait yet bracketed — the daemon read as idle and killed itself.
    test
        <@
            idleInhibitors false 0 [ ScanActivity.ScanKind.Cold ] = [ IdleInhibitor.ScanInFlight
                                                                          ScanActivity.ScanKind.Cold ]
        @>

[<Fact>]
let ``idleInhibitors names a forced scan distinctly from a cold one`` () =
    // The kind is reported, not decided upon: both inhibit, but a log line that
    // says "forced" is a `fshw scan` and one that says "cold" is a startup stall.
    test
        <@
            idleInhibitors false 0 [ ScanActivity.ScanKind.Forced ] = [ IdleInhibitor.ScanInFlight
                                                                            ScanActivity.ScanKind.Forced ]
        @>

[<Fact>]
let ``idleInhibitors is empty only when nothing is in flight`` () =
    test <@ List.isEmpty (idleInhibitors false 0 []) @>

[<Fact>]
let ``idleInhibitors reports every concurrent reason, not just the first`` () =
    // An operator reading a deferral wants all of it: suppressing the rest once
    // one leg is true is how the scan leg could have gone missing unnoticed.
    let inhibitors = idleInhibitors true 2 [ ScanActivity.ScanKind.Cold ]

    test <@ List.length inhibitors = 3 @>
    test <@ List.contains IdleInhibitor.PluginBusy inhibitors @>
    test <@ List.contains (IdleInhibitor.VerdictWait 2) inhibitors @>
    test <@ List.contains (IdleInhibitor.ScanInFlight ScanActivity.ScanKind.Cold) inhibitors @>

[<Fact>]
let ``describeAll renders every inhibitor into one line`` () =
    let rendered =
        IdleInhibitor.describeAll (idleInhibitors true 1 [ ScanActivity.ScanKind.Cold ])

    test <@ rendered.Contains "plugin" @>
    test <@ rendered.Contains "verdict wait" @>
    test <@ rendered.Contains "cold scan is in flight" @>

// --- decide (pure decision) ---

[<Fact>]
let ``below threshold does not fire`` () =
    test
        <@
            decide threshold (TimeSpan.FromMinutes 29.0) [] false = TickOutcome.WithinWindow(
                TimeSpan.FromMinutes 29.0,
                threshold
            )
        @>

[<Fact>]
let ``at threshold boundary fires`` () =
    test <@ decide threshold threshold [] false = TickOutcome.Fired @>

[<Fact>]
let ``past threshold fires`` () =
    test <@ decide threshold (TimeSpan.FromMinutes 31.0) [] false = TickOutcome.Fired @>

[<Fact>]
let ``work in flight defers even past threshold, naming the reason`` () =
    let inhibitors = [ IdleInhibitor.ScanInFlight ScanActivity.ScanKind.Cold ]

    test <@ decide threshold (TimeSpan.FromMinutes 31.0) inhibitors false = TickOutcome.Inhibited inhibitors @>

[<Fact>]
let ``already fired never fires again`` () =
    test <@ decide threshold (TimeSpan.FromMinutes 31.0) [] true = TickOutcome.AlreadyFired @>

[<Fact>]
let ``inside the window reports the window, not the work in flight`` () =
    // Ordering matters for the log: `Inhibited` is reserved for "would have
    // exited right now but for this work", which is the event the idle-exit audit trail
    // exists to record.
    let inhibitors = [ IdleInhibitor.PluginBusy ]

    test
        <@
            decide threshold (TimeSpan.FromMinutes 1.0) inhibitors false = TickOutcome.WithinWindow(
                TimeSpan.FromMinutes 1.0,
                threshold
            )
        @>

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

// --- FSHW_IDLE_EXIT_MIN: a per-daemon override outside .fshw.json ---
//
// `.fshw.json` is shared and tracked, and editing it stops every daemon of the
// repository ("config changed, stopping"). One workspace's idle window therefore has
// to be settable from the environment the daemon starts in, with the file's value as
// the fallback and the AUTO exemption untouched when the variable is absent.

let private envOf (value: string) : string -> string =
    fun name -> if name = OverrideEnvVar then value else null

let private noEnv: string -> string = fun _ -> null

[<Fact>]
let ``the override variable is FSHW_IDLE_EXIT_MIN`` () =
    test <@ OverrideEnvVar = "FSHW_IDLE_EXIT_MIN" @>

[<Fact>]
let ``an unset or blank override is Unset`` () =
    test <@ parseOverride null = OverrideValue.Unset @>
    test <@ parseOverride "" = OverrideValue.Unset @>
    test <@ parseOverride "   " = OverrideValue.Unset @>

[<Fact>]
let ``a positive override is a window in minutes`` () =
    test <@ parseOverride "45" = OverrideValue.Set(IdleExitConfig.Minutes 45) @>
    test <@ parseOverride " 7 " = OverrideValue.Set(IdleExitConfig.Minutes 7) @>

[<Fact>]
let ``zero, a negative number, false and true disable, as in .fshw.json`` () =
    for raw in [ "0"; "-3"; "false"; "FALSE"; "true" ] do
        test <@ parseOverride raw = OverrideValue.Set IdleExitConfig.Disabled @>

[<Fact>]
let ``anything else is Invalid and names the value`` () =
    test <@ parseOverride "soon" = OverrideValue.Invalid "soon" @>
    test <@ parseOverride "4.5" = OverrideValue.Invalid "4.5" @>

[<Fact>]
let ``without the override the default workspace keeps its AUTO exemption`` () =
    let resolved = resolveIdleExit noEnv IdleExitConfig.Absent "/dev/repo"
    test <@ resolved.ThresholdMin = None @>
    test <@ resolved.Source = ThresholdSource.Auto @>
    test <@ resolved.Warning = None @>

[<Fact>]
let ``without the override a workspace keeps AUTO 30`` () =
    let resolved = resolveIdleExit noEnv IdleExitConfig.Absent "/dev/repo/.workspaces/x"
    test <@ resolved.ThresholdMin = Some 30 @>
    test <@ resolved.Source = ThresholdSource.Auto @>

[<Fact>]
let ``without the override an explicit file value is sourced from the file`` () =
    let resolved =
        resolveIdleExit noEnv (IdleExitConfig.Minutes 15) "/dev/repo/.workspaces/x"

    test <@ resolved.ThresholdMin = Some 15 @>
    test <@ resolved.Source = ThresholdSource.ConfigFile @>

[<Fact>]
let ``the override disables idle-exit in a workspace AUTO would arm`` () =
    let resolved =
        resolveIdleExit (envOf "0") IdleExitConfig.Absent "/dev/repo/.workspaces/land"

    test <@ resolved.ThresholdMin = None @>
    test <@ resolved.Source = ThresholdSource.Environment @>
    test <@ resolved.Warning = None @>

[<Fact>]
let ``the override beats an explicit file value`` () =
    let resolved =
        resolveIdleExit (envOf "240") (IdleExitConfig.Minutes 15) "/dev/repo/.workspaces/land"

    test <@ resolved.ThresholdMin = Some 240 @>
    test <@ resolved.Source = ThresholdSource.Environment @>

[<Fact>]
let ``the override can arm the default workspace too`` () =
    let resolved = resolveIdleExit (envOf "60") IdleExitConfig.Absent "/dev/repo"
    test <@ resolved.ThresholdMin = Some 60 @>
    test <@ resolved.Source = ThresholdSource.Environment @>

[<Fact>]
let ``an invalid override falls back to the file and says so`` () =
    let resolved =
        resolveIdleExit (envOf "soon") IdleExitConfig.Absent "/dev/repo/.workspaces/x"

    test <@ resolved.ThresholdMin = Some 30 @>
    test <@ resolved.Source = ThresholdSource.Auto @>
    test <@ resolved.Warning.IsSome @>
    test <@ resolved.Warning.Value.Contains "FSHW_IDLE_EXIT_MIN" @>
    test <@ resolved.Warning.Value.Contains "soon" @>

[<Fact>]
let ``each source describes itself by the name a user sets`` () =
    test <@ (ThresholdSource.describe ThresholdSource.Environment).Contains "FSHW_IDLE_EXIT_MIN" @>
    test <@ (ThresholdSource.describe ThresholdSource.ConfigFile).Contains ".fshw.json idleExitMin" @>
    test <@ (ThresholdSource.describe ThresholdSource.Auto).Contains "AUTO" @>

[<Fact>]
let ``only an override that disables idle-exit has a disabled line`` () =
    test <@ disabledLine ThresholdSource.Auto = None @>
    test <@ disabledLine ThresholdSource.ConfigFile = None @>
    let line = disabledLine ThresholdSource.Environment
    test <@ line.IsSome @>
    test <@ line.Value.StartsWith "[idle-exit] disabled" @>
    test <@ line.Value.Contains "FSHW_IDLE_EXIT_MIN" @>

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

// --- effectiveThreshold (pressure shortens an already-eligible window) ---
// Eligibility (default/main workspace exempt → no window at all) is owned and
// tested by resolveThreshold above; effectiveThreshold only sees eligible ints.

[<Fact>]
let ``eligible without pressure uses the base window`` () =
    test <@ effectiveThreshold 30 false (Some 2) = 30 @>

[<Fact>]
let ``eligible under pressure shortens to min(base, floor)`` () =
    test <@ effectiveThreshold 30 true (Some 2) = 2 @>

[<Fact>]
let ``floor never lengthens a window already smaller`` () =
    test <@ effectiveThreshold 1 true (Some 2) = 1 @>

[<Fact>]
let ``floor disabled ignores pressure and uses the base window`` () =
    test <@ effectiveThreshold 30 true None = 30 @>

// --- runTick wiring (injected effects) ---

let private makeDeps (idleMinutes: float) (busy: bool) =
    let shutdownCalls = ref 0
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          Source = ThresholdSource.Auto
          PressureFloorMin = Some 2
          Pressure = fun () -> false
          Now = fun () -> now
          Inhibitors = fun () -> if busy then [ IdleInhibitor.PluginBusy ] else []
          LastActivityAt = fun () -> now.AddMinutes(-idleMinutes)
          Shutdown = fun () -> Interlocked.Increment(shutdownCalls) |> ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    deps, shutdownCalls, logs

[<Fact>]
let ``runTick fires shutdown and logs when idle past threshold`` () =
    let deps, shutdownCalls, logs = makeDeps 31.0 false
    let latch = FireLatch.create ()

    let outcome = runTick deps latch

    test <@ outcome = TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 1 @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "[idle-exit] idle for") @>
    test <@ FireLatch.hasFired latch @>

[<Fact>]
let ``runTick does not fire below threshold`` () =
    let deps, shutdownCalls, _ = makeDeps 10.0 false
    let latch = FireLatch.create ()

    let outcome = runTick deps latch

    test <@ outcome = TickOutcome.WithinWindow(TimeSpan.FromMinutes 10.0, TimeSpan.FromMinutes 30.0) @>
    test <@ shutdownCalls.Value = 0 @>

[<Fact>]
let ``runTick defers when busy then fires when free`` () =
    let shutdownCalls = ref 0
    let busy = ref true
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          Source = ThresholdSource.Auto
          PressureFloorMin = Some 2
          Pressure = fun () -> false
          Now = fun () -> now
          Inhibitors = fun () -> if busy.Value then [ IdleInhibitor.PluginBusy ] else []
          LastActivityAt = fun () -> now.AddMinutes(-31.0)
          Shutdown = fun () -> Interlocked.Increment(shutdownCalls) |> ignore
          Log = ignore }

    let latch = FireLatch.create ()

    test <@ runTick deps latch = TickOutcome.Inhibited [ IdleInhibitor.PluginBusy ] @>
    test <@ shutdownCalls.Value = 0 @>

    busy.Value <- false
    test <@ runTick deps latch = TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 1 @>

// --- runTick: pressure shortens the effective window ---

let private makePressureDeps (idleMinutes: float) (pressure: bool ref) (floor: int option) =
    let shutdownCalls = ref 0
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          Source = ThresholdSource.Auto
          PressureFloorMin = floor
          Pressure = fun () -> pressure.Value
          Now = fun () -> now
          Inhibitors = fun () -> []
          LastActivityAt = fun () -> now.AddMinutes(-idleMinutes)
          Shutdown = fun () -> Interlocked.Increment(shutdownCalls) |> ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    deps, shutdownCalls, logs

[<Fact>]
let ``runTick under pressure fires at the 2min floor, not before`` () =
    // Base 30min, floor 2min, idle 1min, under pressure → 1 < 2 → no fire.
    let deps, shutdownCalls, _ = makePressureDeps 1.0 (ref true) (Some 2)
    test <@ runTick deps (FireLatch.create ()) <> TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 0 @>

[<Fact>]
let ``runTick under pressure fires at the floor boundary`` () =
    // idle 2min == floor → fires (well below the 30min base).
    let deps, shutdownCalls, logs = makePressureDeps 2.0 (ref true) (Some 2)
    test <@ runTick deps (FireLatch.create ()) = TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 1 @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "memory pressure shortened") @>

[<Fact>]
let ``runTick re-evaluates pressure each tick - subsiding restores the full window`` () =
    // idle 2min, floor 2min: with pressure OFF the 30min base applies and nothing
    // fires; when pressure rises the window drops to the floor and the SAME idle
    // fires. Pressure is a live read, not latched.
    let pressure = ref false
    let deps, shutdownCalls, _ = makePressureDeps 2.0 pressure (Some 2)
    let latch = FireLatch.create ()

    test <@ runTick deps latch <> TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 0 @>

    pressure.Value <- true
    test <@ runTick deps latch = TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 1 @>

[<Fact>]
let ``runTick with floor disabled ignores pressure`` () =
    // Floor None, idle 2min, under pressure → still uses the 30min base → defer.
    let deps, shutdownCalls, _ = makePressureDeps 2.0 (ref true) None
    test <@ runTick deps (FireLatch.create ()) <> TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 0 @>

// Within the window, inhibited past it, and eligible past it: a latch that has
// already fired wins in every case, and the eligible case loses its atomic claim
// deterministically instead of only under a thread race.
[<Theory>]
[<InlineData(10.0, false)>]
[<InlineData(31.0, true)>]
[<InlineData(31.0, false)>]
let ``runTick preserves an already fired latch for every eligibility state`` (idleMin: float, busy: bool) =
    let deps, shutdownCalls, _ = makeDeps idleMin busy
    let latch = FireLatch.create ()
    test <@ FireLatch.tryFire latch @>
    test <@ runTick deps latch = TickOutcome.AlreadyFired @>
    test <@ shutdownCalls.Value = 0 @>

[<Fact>]
let ``runTick fires shutdown at most once across concurrent ticks`` () =
    let deps, shutdownCalls, _ = makeDeps 31.0 false
    let latch = FireLatch.create ()

    // Pins the atomic latch: a non-atomic one fired 7x in 41ms under this hammer.
    Parallel.For(0, 5_000, fun _ -> runTick deps latch |> ignore) |> ignore

    test <@ shutdownCalls.Value = 1 @>

[<Fact>]
let ``runTick swallows a throwing shutdown without escaping`` () =
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          Source = ThresholdSource.Auto
          PressureFloorMin = Some 2
          Pressure = fun () -> false
          Now = fun () -> now
          Inhibitors = fun () -> []
          LastActivityAt = fun () -> now.AddMinutes(-31.0)
          Shutdown = fun () -> failwith "boom"
          Log = fun msg -> logs.Add msg }

    let latch = FireLatch.create ()

    let outcome = runTick deps latch

    test <@ outcome = TickOutcome.TickFailed "boom" @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "tick failed") @>

// --- createTimer ---

[<Fact>]
let ``createTimer logs the enable line and is disposable`` () =
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          Source = ThresholdSource.Auto
          PressureFloorMin = Some 2
          Pressure = fun () -> false
          // Activity is "now" so the timer never fires within the test window.
          Now = fun () -> now
          Inhibitors = fun () -> []
          LastActivityAt = fun () -> now
          Shutdown = ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    use timer = createTimer deps

    test <@ logs |> Seq.exists (fun m -> m.Contains "[idle-exit] enabled") @>
    ignore timer

[<Fact>]
let ``the enable line names where its window came from`` () =
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 240
          Source = ThresholdSource.Environment
          PressureFloorMin = None
          Pressure = fun () -> false
          Now = fun () -> now
          Inhibitors = fun () -> []
          LastActivityAt = fun () -> now
          Shutdown = ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    use timer = createTimer deps

    let enable = logs |> Seq.find (fun m -> m.StartsWith "[idle-exit] enabled")
    test <@ enable.Contains "240min" @>
    test <@ enable.Contains "FSHW_IDLE_EXIT_MIN" @>
    ignore timer

// --- a scan in flight is not idleness ---
//
// The confirmed failure: a cold `fshw check` was more than two minutes into FCS
// analysis when the memory-pressure floor (2min) elapsed. Nothing else was in
// flight — no plugin mailbox, no bracketed verdict wait — so the scheduler read
// the daemon as idle and shut it down mid-scan, and the caller exited 2 with no
// verdict. These tests drive the SAME scheduler over a real `ScanLeases`.

let private makeScanDeps (leases: ScanActivity.ScanLeases) (idleMinutes: float) =
    let shutdownCalls = ref 0
    let logs = ResizeArray<string>()
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let deps: IdleExitDeps =
        { BaseThresholdMin = 30
          Source = ThresholdSource.Auto
          PressureFloorMin = Some 2
          // Pressure ON: this is the shortened-window case that actually fired.
          Pressure = fun () -> true
          Now = fun () -> now
          Inhibitors = fun () -> idleInhibitors false 0 (ScanActivity.ScanLeases.inFlight leases)
          LastActivityAt = fun () -> now.AddMinutes(-idleMinutes)
          Shutdown = fun () -> Interlocked.Increment(shutdownCalls) |> ignore
          Log = fun msg -> lock logs (fun () -> logs.Add msg) }

    deps, shutdownCalls, logs

[<Fact>]
let ``idle timer firing during a cold scan does not exit, and says why`` () =
    let leases = ScanActivity.ScanLeases.create ()
    // 11 minutes idle by the last-activity clock, far past the 2min pressure floor.
    let deps, shutdownCalls, logs = makeScanDeps leases 11.0
    let latch = FireLatch.create ()

    use _lease = ScanActivity.ScanLeases.acquire leases ScanActivity.ScanKind.Cold

    let outcome = runTick deps latch

    test <@ outcome = TickOutcome.Inhibited [ IdleInhibitor.ScanInFlight ScanActivity.ScanKind.Cold ] @>
    test <@ shutdownCalls.Value = 0 @>
    test <@ FireLatch.hasFired latch = false @>
    // The audit trail the incident could not produce: the window elapsed and the
    // daemon stayed up, naming the work that held it.
    test <@ logs |> Seq.exists (fun m -> m.Contains "window elapsed but work is in flight") @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "cold scan is in flight") @>

[<Fact>]
let ``the daemon becomes idle-exit eligible once the scan releases its lease`` () =
    // The other half of the invariant: inhibiting must not become pinning. A
    // genuinely quiescent daemon still sheds itself under the pressure floor.
    let leases = ScanActivity.ScanLeases.create ()
    let deps, shutdownCalls, _ = makeScanDeps leases 11.0
    let latch = FireLatch.create ()
    let lease = ScanActivity.ScanLeases.acquire leases ScanActivity.ScanKind.Cold

    test <@ runTick deps latch <> TickOutcome.Fired @>

    lease.Dispose()

    test <@ runTick deps latch = TickOutcome.Fired @>
    test <@ shutdownCalls.Value = 1 @>

[<Fact>]
let ``a forced scan inhibits idle-exit exactly as a cold one does`` () =
    let leases = ScanActivity.ScanLeases.create ()
    let deps, shutdownCalls, _ = makeScanDeps leases 11.0

    use _lease = ScanActivity.ScanLeases.acquire leases ScanActivity.ScanKind.Forced

    test
        <@
            runTick deps (FireLatch.create ()) = TickOutcome.Inhibited
                [ IdleInhibitor.ScanInFlight ScanActivity.ScanKind.Forced ]
        @>

    test <@ shutdownCalls.Value = 0 @>

[<Fact(Timeout = 30000)>]
let ``a tick in flight when the timer is disposed shuts down before the daemon's source is disposed`` () =
    let now = DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc)

    let seen =
        TestHelpers.cancelAcrossTeardown (fun hold shutdown ->
            createTimerWith
                (TimeSpan.FromMilliseconds 100.0)
                { BaseThresholdMin = 1
                  Source = ThresholdSource.Auto
                  PressureFloorMin = None
                  Pressure = fun () -> false
                  Now = fun () -> now
                  Inhibitors =
                    fun () ->
                        hold ()
                        []
                  LastActivityAt = fun () -> now - TimeSpan.FromHours 1.0
                  Shutdown = shutdown
                  Log = ignore })

    test <@ seen = None @>

// --- the pressure signal: the kernel's level on macOS, the GC's mark elsewhere ---
//
// AUTOMATION-1309. On macOS `GC.GetGCMemoryInfo()` reports MemoryLoadBytes = 0 while
// the box thrashes (kernel pressure level 2, 84 MB free), so a GC-only signal never
// reported pressure there and the 2-min idle exit never fired. The kernel's
// `kern.memorystatus_vm_pressure_level` (1 normal, 2 warning, 4 critical) is the
// macOS reading; the GC's high-load mark stays the signal where there is no kernel
// level (Linux, Windows).

[<Fact>]
let ``kernel pressure level 2 is pressure even when the GC reports none`` () =
    test <@ pressureFrom (Some 2) (fun () -> false) @>

[<Fact>]
let ``kernel pressure level 4 (critical) is pressure`` () =
    test <@ pressureFrom (Some 4) (fun () -> false) @>

[<Fact>]
let ``kernel pressure level 1 is no pressure`` () =
    // POSITIVE CONTROL for the two above: a normal kernel level must read as no
    // pressure, so they cannot pass by any `Some` reading as pressure.
    test <@ not (pressureFrom (Some 1) (fun () -> false)) @>

[<Fact>]
let ``a kernel level is authoritative over the GC's mark`` () =
    // Where the kernel answers, its answer decides; the GC's mark is not consulted.
    let mutable gcAsked = false

    let gc () =
        gcAsked <- true
        true

    test <@ not (pressureFrom (Some 1) gc) @>
    test <@ not gcAsked @>

[<Fact>]
let ``without a kernel level the GC's high-load mark decides`` () =
    test <@ pressureFrom None (fun () -> true) @>
    test <@ not (pressureFrom None (fun () -> false)) @>

[<Fact>]
let ``the kernel pressure level is read on macOS and only there`` () =
    match readKernelPressureLevel (), OperatingSystem.IsMacOS() with
    | Some level, true -> test <@ level = 1 || level = 2 || level = 4 @>
    | None, false -> ()
    | reading, isMac -> failwith $"kernel level %A{reading} on isMacOS=%b{isMac}"

[<Fact>]
let ``off macOS the kernel probe is never run`` () =
    let mutable probed = false

    let probe () =
        probed <- true
        Some 4

    test <@ kernelPressureLevelOn false probe = None @>
    test <@ not probed @>
    test <@ kernelPressureLevelOn true probe = Some 4 @>

[<Fact>]
let ``a sysctl answer is a level only when the call succeeded with four bytes`` () =
    let four, eight = 4un, 8un
    test <@ decodeInt32Sysctl 0 four 2 = Some 2 @>
    test <@ decodeInt32Sysctl -1 four 2 = None @>
    test <@ decodeInt32Sysctl 0 eight 2 = None @>

[<Fact>]
let ``the GC's high-load mark is pressure at or above it, never against a zero mark`` () =
    test <@ gcPressureFrom 100L 100L @>
    test <@ gcPressureFrom 100L 150L @>
    test <@ not (gcPressureFrom 100L 99L) @>
    // Some hosts report a zero threshold; that is no reading, not constant pressure.
    test <@ not (gcPressureFrom 0L 0L) @>

[<Fact>]
let ``the live GC reading is the pure decision over the GC's own numbers`` () =
    // On macOS the daemon no longer consults this reading, so nothing else exercises
    // it there; it is still the signal on Linux and Windows. GC numbers change only
    // when a collection completes, so bracket the read and compare only when no
    // collection landed in between.
    let before = GC.GetGCMemoryInfo()
    let live = readGcPressure ()
    let after = GC.GetGCMemoryInfo()

    if before.Index <> after.Index then
        Assert.Skip "a GC completed during the read; the comparison would be meaningless"

    let expected =
        gcPressureFrom before.HighMemoryLoadThresholdBytes before.MemoryLoadBytes

    test <@ live = expected @>
