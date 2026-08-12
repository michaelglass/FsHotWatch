module FsHotWatch.IdleExit

open System
open System.Threading

/// How the daemon's idle-exit feature is configured in `.fshw.json` via the
/// `idleExitMin` key. The three cases are kept distinct (rather than collapsing
/// absent into a default int) because the *absent* case has path-conditional
/// behaviour that an explicit value never has — see `resolveThreshold`.
[<RequireQualifiedAccess>]
type IdleExitConfig =
    /// `idleExitMin` key not present. AUTO mode: enabled (30min) only for
    /// non-default workspaces (a path containing a `/.workspaces/` segment).
    | Absent
    /// `idleExitMin: 0` or `idleExitMin: false`. Explicit opt-out everywhere.
    | Disabled
    /// `idleExitMin: N` (positive). Explicit opt-in everywhere, N-minute window.
    | Minutes of int

/// The threshold AUTO mode applies to non-default workspaces.
[<Literal>]
let AutoThresholdMin = 30

/// The default pressure floor (minutes). When an idle-exit-eligible daemon is on a
/// machine under memory pressure, the effective idle window shortens to
/// `min(baseThreshold, pressureFloorMin)`. See `effectiveThreshold`.
[<Literal>]
let DefaultPressureFloorMin = 2

/// How the daemon's pressure floor is configured in `.fshw.json` via the
/// `pressureIdleFloorMin` key. Mirrors `IdleExitConfig`'s tri-state so the
/// *absent* case (default-on at 2 min) is distinct from an explicit opt-out.
[<RequireQualifiedAccess>]
type PressureFloorConfig =
    /// `pressureIdleFloorMin` key not present. Default: floor at 2 minutes.
    | Absent
    /// `pressureIdleFloorMin: 0` or `: false`. Pressure-shortening disabled — a
    /// daemon under pressure waits its full idle-exit window, same as no pressure.
    | Disabled
    /// `pressureIdleFloorMin: N` (positive). Pressure shortens the window to
    /// `min(baseThreshold, N)`.
    | Minutes of int

/// Resolve the effective pressure floor (in minutes) from its config.
///
/// - `Absent`   → `Some 2` (default-on)
/// - `Disabled` → `None` (no pressure-shortening)
/// - `Minutes N` (N > 0) → `Some N`
/// - `Minutes N` (N <= 0) → `None` (defensive: non-positive disables, never an
///   instantly-firing floor)
let resolvePressureFloor (config: PressureFloorConfig) : int option =
    match config with
    | PressureFloorConfig.Absent -> Some DefaultPressureFloorMin
    | PressureFloorConfig.Disabled -> None
    | PressureFloorConfig.Minutes n -> if n > 0 then Some n else None

/// Read the live memory-pressure signal from the runtime GC: true when
/// `MemoryLoadBytes` has reached the GC's own `HighMemoryLoadThresholdBytes`. No
/// percentage knob — we reuse the GC's high-load mark. A 0 threshold (some hosts report
/// it) never counts as pressure. Injected in the daemon wiring so the scheduler is
/// unit-testable without a real GC.
let readGcPressure () : bool =
    let info = System.GC.GetGCMemoryInfo()

    info.HighMemoryLoadThresholdBytes > 0L
    && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes

/// Pure: shorten an already-eligible idle window (in minutes) under memory pressure.
/// Eligibility is decided upstream by `resolveThreshold`, so by the time we have a
/// `baseThreshold: int` the daemon is eligible and pressure here only SHORTENS, never
/// creates, a window. Under pressure with a floor it is `min(base, f)`, so the floor
/// never lengthens a window already smaller than it; otherwise `base` is unchanged.
let effectiveThreshold (baseThreshold: int) (pressure: bool) (pressureFloor: int option) : int =
    match pressure, pressureFloor with
    | true, Some f -> min baseThreshold f
    | _ -> baseThreshold

/// True when `repoRoot` is a non-default jj workspace — the path contains a
/// `/.workspaces/` segment. Separators are normalized for host-OS independence, and the
/// match is on a full path *segment*, so `foo.workspaces.bak` does NOT match.
/// Case-sensitive (fine on macOS/Linux).
let isNonDefaultWorkspace (repoRoot: string) : bool =
    if String.IsNullOrEmpty repoRoot then
        false
    else
        let normalized = repoRoot.Replace('\\', '/')
        // Bracket with separators so `.workspaces` only matches as a whole segment,
        // including a path that ends in `/.workspaces` with no child.
        let padded = "/" + normalized.Trim('/') + "/"
        padded.Contains("/.workspaces/")

/// Resolve the effective idle-exit threshold (in minutes) for this daemon.
///
/// - `Absent`  → `Some 30` iff `repoRoot` is a non-default workspace, else `None`
/// - `Disabled`→ `None` everywhere (explicit opt-out beats AUTO)
/// - `Minutes N` (N > 0) → `Some N` everywhere (explicit opt-in, path-agnostic)
/// - `Minutes N` (N <= 0) → `None` (defensive: a non-positive explicit value is
///   treated as disabled rather than an instantly-firing window)
let resolveThreshold (config: IdleExitConfig) (repoRoot: string) : int option =
    match config with
    | IdleExitConfig.Absent ->
        if isNonDefaultWorkspace repoRoot then
            Some AutoThresholdMin
        else
            None
    | IdleExitConfig.Disabled -> None
    | IdleExitConfig.Minutes n -> if n > 0 then Some n else None

/// Atomic fire-once latch. `0` = armed, `1` = fired. `Interlocked.CompareExchange`
/// means that under concurrent timer ticks exactly one caller observes the
/// armed-to-fired transition; every other caller is rejected.
[<NoComparison; NoEquality>]
type FireLatch = { mutable state: int }

module FireLatch =
    /// A fresh, armed latch.
    let create () : FireLatch = { state = 0 }

    /// Attempt to claim the single fire. Returns `true` for exactly one caller
    /// across all threads; `false` for every subsequent caller.
    let tryFire (latch: FireLatch) : bool =
        Interlocked.CompareExchange(&latch.state, 1, 0) = 0

    /// True once the latch has fired (or is firing). Read for diagnostics/tests.
    let hasFired (latch: FireLatch) : bool = Volatile.Read(&latch.state) = 1

/// The daemon is "busy" for idle-exit purposes when EITHER any plugin mailbox is in
/// flight OR at least one client is actively blocked on a verdict wait
/// (`WaitForComplete`). The wait leg matters: a background `RunExclusive` run holds a
/// plugin Running in a way that mailbox-inflight (`AnyPluginBusy`) does not observe,
/// so without it idle-exit could fire out from under a connected `fshw check` and drop
/// the client with a connection error instead of a verdict. Pure; the daemon injects
/// the two live signals.
let busyForIdleExit (anyPluginBusy: bool) (activeVerdictWaits: int) : bool = anyPluginBusy || activeVerdictWaits > 0

/// Pure decision: fire when the idle duration has met the threshold, no work is
/// running, and the latch has not already fired. `busy` collapses "any plugin running /
/// any check in flight" to a bool at the call site, keeping this free of host/FCS
/// types. `alreadyFired = true` always vetoes.
let shouldFire (idleThreshold: TimeSpan) (idleFor: TimeSpan) (busy: bool) (alreadyFired: bool) : bool =
    idleFor >= idleThreshold && not busy && not alreadyFired

/// Dependencies a live idle-exit scheduler needs, all injectable so the
/// scheduler's wiring can be unit-tested without FCS, a PluginHost, or a real
/// timer or process exit.
[<NoComparison; NoEquality>]
type IdleExitDeps =
    {
        /// Base idle window (minutes) before exit is eligible — the resolved
        /// `idleExitMin` threshold (`IdleExit.resolveThreshold`). Memory pressure
        /// can SHORTEN this per tick to `min(BaseThresholdMin, PressureFloorMin)`.
        BaseThresholdMin: int
        /// Resolved pressure floor (minutes), or `None` when pressure-shortening
        /// is disabled (`resolvePressureFloor`). Re-read against `Pressure` each
        /// tick — it never lengthens a window already smaller than it.
        PressureFloorMin: int option
        /// Current memory-pressure signal. Re-evaluated each tick (a live
        /// current-state read, not latched): if pressure subsides the full window
        /// is restored. Production wires `IdleExit.readGcPressure`.
        Pressure: unit -> bool
        /// Current UTC time.
        Now: unit -> DateTime
        /// True when any plugin/check is in flight (defers the exit).
        Busy: unit -> bool
        /// UTC of the most recent activity (event / completed work).
        LastActivityAt: unit -> DateTime
        /// The graceful-shutdown action (cts.Cancel() in production). Invoked at
        /// most once, guaranteed by the atomic latch.
        Shutdown: unit -> unit
        /// Structured logger: `log message`.
        Log: string -> unit
    }

/// Run one scheduler tick: resolve the (possibly pressure-shortened) effective
/// window, run the pure `shouldFire` decision, and on a fire atomically claim the
/// latch, log, then invoke the graceful shutdown. Pressure is re-evaluated every tick,
/// so if it subsides the full window is restored. The latch means `Shutdown` runs at
/// most once even if many ticks reach this point concurrently. The whole body is
/// guarded so a thrown exception is logged rather than escaping the timer callback.
/// Returns `true` iff this tick fired the shutdown (for tests).
let runTick (deps: IdleExitDeps) (latch: FireLatch) : bool =
    try
        let idleFor = deps.Now() - deps.LastActivityAt()
        let busy = deps.Busy()
        let pressure = deps.Pressure()
        // BaseThresholdMin is always > 0 here — createTimer is only wired once
        // resolveThreshold returned Some, so the daemon is already eligible.
        let effectiveMin =
            effectiveThreshold deps.BaseThresholdMin pressure deps.PressureFloorMin

        let threshold = TimeSpan.FromMinutes(float effectiveMin)

        if shouldFire threshold idleFor busy (FireLatch.hasFired latch) then
            // Even if the pure check above raced past for several threads, only one
            // wins tryFire.
            if FireLatch.tryFire latch then
                let pressureNote =
                    if pressure && effectiveMin < deps.BaseThresholdMin then
                        $" (memory pressure shortened the %d{deps.BaseThresholdMin}min window)"
                    else
                        ""

                deps.Log
                    $"[idle-exit] idle for %d{effectiveMin}min — shutting down (next fshw command auto-restarts)%s{pressureNote}"

                deps.Shutdown()
                true
            else
                false
        else
            false
    with ex ->
        deps.Log $"[idle-exit] tick failed: %s{ex.ToString()}"
        false

/// Create the live scheduler `System.Threading.Timer` (period 30s). The caller
/// owns disposal. Logs the enable line on creation. Pure wiring around `runTick`
/// — all effects come through `deps`.
let createTimer (deps: IdleExitDeps) : Timer =
    let latch = FireLatch.create ()
    let period = TimeSpan.FromSeconds(30.0)

    let floorNote =
        match deps.PressureFloorMin with
        | Some f when f < deps.BaseThresholdMin -> $" (or %d{f}min under memory pressure)"
        | _ -> ""

    deps.Log
        $"[idle-exit] enabled: will shut down after %d{deps.BaseThresholdMin}min idle (checked every 30s)%s{floorNote}"

    let onTick (_: obj) = runTick deps latch |> ignore

    new Timer(TimerCallback(onTick), null, period, period)
