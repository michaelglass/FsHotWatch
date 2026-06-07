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

/// The default pressure floor (minutes). When the daemon is already
/// idle-exit-eligible AND the machine is under memory pressure, the effective
/// idle window is shortened to `min(baseThreshold, pressureFloorMin)` so a tight
/// machine sheds idle daemons fast. See `pressureIdleFloorMin` in `.fshw.json`
/// and `effectiveThreshold`.
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

/// Read the live memory-pressure signal from the runtime GC. Pressure is true
/// when the GC's reported `MemoryLoadBytes` has reached its own
/// `HighMemoryLoadThresholdBytes` — i.e. the GC itself considers the system
/// high-memory-loaded. No percentage knob: we reuse the GC's own high-load mark.
/// Defensive against a 0 threshold (some hosts report it) — that never counts as
/// pressure. Injected as a function in the daemon wiring so the scheduler is
/// unit-testable without a real GC.
let readGcPressure () : bool =
    let info = System.GC.GetGCMemoryInfo()

    info.HighMemoryLoadThresholdBytes > 0L
    && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes

/// Pure: the effective idle-exit window (in minutes) given the base threshold,
/// the current pressure state, and the resolved pressure floor.
///
/// - When not idle-exit-eligible (`baseThreshold = None`), pressure NEVER makes
///   it eligible — the default/main workspace stays exempt under pressure. This
///   is the explicit product decision: pressure only SHORTENS an already-
///   applicable window; it never creates one.
/// - When eligible (`Some n`) and under pressure with a floor (`Some f`), the
///   window becomes `min(n, f)` — the floor never LENGTHENS a window already
///   smaller than it.
/// - When not under pressure, or the floor is disabled (`None`), the base
///   threshold is used unchanged.
let effectiveThreshold (baseThreshold: int option) (pressure: bool) (pressureFloor: int option) : int option =
    match baseThreshold with
    | None -> None
    | Some n ->
        match pressure, pressureFloor with
        | true, Some f -> Some(min n f)
        | _ -> Some n

/// True when `repoRoot` is a non-default jj workspace — i.e. the path contains a
/// `/.workspaces/` segment (the ecosystem convention for non-default checkouts).
/// Separators are normalized so this works regardless of the host OS. The match
/// is on a full path *segment*, so a filename substring like `foo.workspaces.bak`
/// does NOT match. Case-sensitive (fine on macOS/Linux).
let isNonDefaultWorkspace (repoRoot: string) : bool =
    if String.IsNullOrEmpty repoRoot then
        false
    else
        let normalized = repoRoot.Replace('\\', '/')
        // Bracket with separators so `.workspaces` only matches as a whole
        // segment. A trailing `/` is appended so a path ending in
        // `/.workspaces` (no child) still presents the segment boundary.
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

/// Atomic fire-once latch. `0` = armed, `1` = fired. The transition is performed
/// with `Interlocked.CompareExchange` so that under concurrent timer ticks
/// exactly one caller observes the arming-to-fired transition; every other
/// caller (including re-fires after shutdown begins) is rejected. This is the
/// fix for the prior experiment's non-atomic latch, which fired repeatedly under
/// concurrent ticks.
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

/// Pure decision: should idle-exit fire on this tick?
///
/// Fires when the idle duration has met/exceeded the threshold AND no work is
/// currently running AND the latch has not already fired. `busy` collapses
/// "any plugin running / any check in flight" to a single bool at the call site,
/// keeping this function free of host/FCS types. `alreadyFired` is the latch's
/// current state; a `true` here always vetoes (a second fire is impossible).
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

/// Run one scheduler tick. Reads the live clock / busy / activity / pressure,
/// resolves the (possibly pressure-shortened) effective window, runs the pure
/// `shouldFire` decision, and on a fire: atomically claims the latch, logs the
/// `[idle-exit] …` line, then invokes the graceful shutdown. Pressure is
/// re-evaluated every tick — if it subsides, the full window is restored. The
/// atomic latch means that even if many ticks reach this point concurrently,
/// `Shutdown` runs at most once. The whole body is guarded so a thrown exception
/// is logged, not propagated out of the timer callback — the daemon must never
/// crash on a tick. Returns `true` iff this tick fired the shutdown (for tests).
let runTick (deps: IdleExitDeps) (latch: FireLatch) : bool =
    try
        let idleFor = deps.Now() - deps.LastActivityAt()
        let busy = deps.Busy()
        let pressure = deps.Pressure()
        // BaseThresholdMin is always > 0 here (createTimer is only wired for a
        // Some-resolved threshold), so effectiveThreshold returns Some.
        let effectiveMin =
            effectiveThreshold (Some deps.BaseThresholdMin) pressure deps.PressureFloorMin
            |> Option.defaultValue deps.BaseThresholdMin

        let threshold = TimeSpan.FromMinutes(float effectiveMin)

        if shouldFire threshold idleFor busy (FireLatch.hasFired latch) then
            // Atomically claim the single fire. Even if the pure check above
            // raced past for several threads, only one wins tryFire.
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
