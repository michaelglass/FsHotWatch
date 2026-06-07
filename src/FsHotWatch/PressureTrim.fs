module FsHotWatch.PressureTrim

open System
open System.Threading

/// How the daemon's memory-pressure-triggered FCS cache trim is configured in
/// `.fshw.json` via the `pressureTrimPct` key. Kept as distinct cases (rather
/// than a bare int) because the *absent* case is deliberately ENABLED at the
/// default percentage — unlike idle-exit, this feature only ever acts when the
/// machine is genuinely memory-starved, so being on by default is harmless and
/// desirable. See `resolvePct`.
[<RequireQualifiedAccess>]
type PressureTrimConfig =
    /// `pressureTrimPct` key not present. ENABLED at the default percentage.
    | Absent
    /// `pressureTrimPct: 0` or `pressureTrimPct: false`. Explicit opt-out.
    | Disabled
    /// `pressureTrimPct: N` (positive). Enabled, trigger at N% of the GC
    /// high-memory-load threshold.
    | Pct of int

/// The default trigger percentage AUTO/absent mode applies: 100 means "trim
/// exactly when the GC itself considers the system high-memory-loaded".
[<Literal>]
let DefaultPct = 100

/// Cooldown after a trim fires before another may fire, regardless of pressure.
/// Re-warming the FCS caches is cheap but not free; a 5-minute floor keeps a
/// sustained-pressure machine from thrashing the caches every poll period.
let Cooldown = TimeSpan.FromMinutes(5.0)

/// Poll period for the live scheduler timer (mirrors IdleExit's 30s cadence).
let PollPeriod = TimeSpan.FromSeconds(30.0)

/// Resolve the effective trigger percentage for this daemon.
///
/// - `Absent`  → `Some 100` (enabled at the default; this feature is on by
///   default because it only acts under genuine memory starvation)
/// - `Disabled`→ `None` (explicit opt-out)
/// - `Pct N` (N > 0) → `Some N`
/// - `Pct N` (N <= 0) → `None` (defensive: a non-positive explicit value is
///   treated as disabled rather than an always-firing trigger)
let resolvePct (config: PressureTrimConfig) : int option =
    match config with
    | PressureTrimConfig.Absent -> Some DefaultPct
    | PressureTrimConfig.Disabled -> None
    | PressureTrimConfig.Pct n -> if n > 0 then Some n else None

/// Thread-safe "a cold scan is currently running" flag. The pressure-trim timer
/// fires on a background thread every 30s and must DEFER while the daemon's cold
/// scan (`performScan`, which drives FCS checks directly — NOT through a plugin,
/// so `host.AnyPluginBusy()` does not cover it) is in flight: trimming mid-scan
/// destroys the very caches the scan is building, which the scan then rebuilds.
///
/// The daemon's `ScanState` lives inside the scan MailboxProcessor and is only
/// readable via a `PostAndReply` round-trip — which would BLOCK while the agent
/// is busy running the scan, so it cannot serve as the timer's non-blocking busy
/// guard. This flag is the minimal non-blocking signal instead: `performScan`
/// brackets its body with `enter`/`exit`, the timer reads it via `isScanning`.
/// Uses the repo's `Volatile.Read`/`Volatile.Write` convention for daemon state.
[<NoComparison; NoEquality>]
type ScanInProgress = { mutable scanning: bool }

module ScanInProgress =
    /// A fresh flag in the not-scanning state.
    let create () : ScanInProgress = { scanning = false }

    /// Mark a scan as started (called at `performScan` entry).
    let enter (flag: ScanInProgress) : unit = Volatile.Write(&flag.scanning, true)

    /// Mark the scan as finished (called from `performScan`'s finally).
    let exit (flag: ScanInProgress) : unit = Volatile.Write(&flag.scanning, false)

    /// Non-blocking read of the scan-in-progress state (for the timer's busy dep).
    let isScanning (flag: ScanInProgress) : bool = Volatile.Read(&flag.scanning)

    /// `enter` the flag and return an `IDisposable` whose `Dispose` calls `exit`.
    /// Lets a scan bracket its body with `use _ = enterScope flag`, clearing the
    /// flag on every exit path (normal return, exception, or cancellation).
    let enterScope (flag: ScanInProgress) : IDisposable =
        enter flag

        { new IDisposable with
            member _.Dispose() = exit flag }

/// Compose the pressure-trim busy guard from the plugin-busy signal and the
/// cold-scan-in-progress signal: the trim must defer when EITHER any plugin/check
/// is running OR a cold scan is in flight. Extracted so the OR-composition is a
/// named, unit-tested function (the production wiring is `composeBusy
/// host.AnyPluginBusy (fun () -> ScanInProgress.isScanning flag)`).
let composeBusy (anyPluginBusy: unit -> bool) (isScanning: unit -> bool) : unit -> bool =
    fun () -> anyPluginBusy () || isScanning ()

/// Atomic re-arm latch holding the UTC tick count at which the last trim fired,
/// or `Int64.MinValue` when no trim has fired yet (sentinel that is always
/// further than `Cooldown` in the past). The transition is performed with
/// `Interlocked.CompareExchange` against the *observed* last-fired value, so
/// that under concurrent timer ticks at most one caller wins the claim — every
/// other caller sees its CompareExchange fail and backs off. This mirrors the
/// `IdleExit.FireLatch` Interlocked discipline (no racy check-then-set), but is
/// re-armable: after `Cooldown` elapses a fresh claim can win again.
[<NoComparison; NoEquality>]
type CooldownLatch = { mutable lastFiredTicks: int64 }

module CooldownLatch =
    /// A fresh latch with no prior fire (sentinel = never fired).
    let create () : CooldownLatch = { lastFiredTicks = Int64.MinValue }

    /// The currently-observed last-fired tick (read for the pure decision/tests).
    let lastFired (latch: CooldownLatch) : int64 = Volatile.Read(&latch.lastFiredTicks)

    /// Attempt to claim a fire at `nowTicks`, given the `observed` last-fired
    /// value the caller based its decision on. Succeeds (returns `true`) for at
    /// most one caller per observed value: the CompareExchange only swaps in
    /// `nowTicks` if the field still equals `observed`. A racing winner moves the
    /// field, so every loser's CompareExchange fails and returns `false`.
    let tryClaim (latch: CooldownLatch) (observed: int64) (nowTicks: int64) : bool =
        Interlocked.CompareExchange(&latch.lastFiredTicks, nowTicks, observed) = observed

/// Pure decision: should a trim fire on this tick?
///
/// Fires when memory load has met/exceeded the configured fraction of the GC's
/// high-load threshold AND no work is currently running AND the cooldown since
/// the last fire has fully elapsed.
///
/// `pct` is the configured percentage of `thresholdBytes`. `loadBytes` /
/// `thresholdBytes` come straight from `GC.GetGCMemoryInfo()` at the call site
/// (`MemoryLoadBytes` / `HighMemoryLoadThresholdBytes`), injected so this stays
/// free of GC/host/FCS types and trivially unit-testable. `busy` collapses "any
/// plugin running / any check in flight" to a single bool — when busy we DEFER
/// (no trim), and deferral does NOT consume the cooldown. `lastFiredTicks` is
/// the latch's observed last-fire stamp; `nowTicks` the current UTC tick count.
let shouldTrim
    (pct: int)
    (loadBytes: int64)
    (thresholdBytes: int64)
    (busy: bool)
    (lastFiredTicks: int64)
    (nowTicks: int64)
    : bool =
    // Threshold·pct/100 in a widened domain so the multiply can't overflow.
    let trigger =
        thresholdBytes / 100L * int64 pct + thresholdBytes % 100L * int64 pct / 100L

    let underPressure = thresholdBytes > 0L && loadBytes >= trigger

    let cooldownElapsed =
        // MinValue sentinel (never fired) always counts as elapsed; otherwise
        // require a full Cooldown since the last fire. Guard the subtraction
        // against the sentinel so we don't compute a TimeSpan from MinValue.
        lastFiredTicks = Int64.MinValue
        || TimeSpan.FromTicks(nowTicks - lastFiredTicks) >= Cooldown

    underPressure && not busy && cooldownElapsed

/// Dependencies a live pressure-trim scheduler needs, all injectable so the
/// scheduler's wiring is unit-testable without a real GC, PluginHost, FCS
/// checker, or timer.
[<NoComparison; NoEquality>]
type PressureTrimDeps =
    {
        /// Trigger percentage of the GC high-load threshold (e.g. 100, 80, 120).
        Pct: int
        /// Current UTC time.
        Now: unit -> DateTime
        /// Reads the memory signal: `(loadBytes, thresholdBytes)`. Production
        /// supplies `GC.GetGCMemoryInfo()` (`MemoryLoadBytes`,
        /// `HighMemoryLoadThresholdBytes`).
        ReadMemory: unit -> int64 * int64
        /// True when any plugin/check is in flight (defers the trim).
        Busy: unit -> bool
        /// The trim action: release FCS root caches + GC. Production wires
        /// `checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients`.
        Trim: unit -> unit
        /// Structured logger: `log message`.
        Log: string -> unit
    }

/// Run one scheduler tick. Reads the live clock / memory / busy, runs the pure
/// `shouldTrim` decision, and on a fire: atomically claims the cooldown latch,
/// runs the trim (guarded), and logs. The atomic claim means that even if many
/// ticks decide to fire concurrently, the trim runs at most once per cooldown
/// window. The whole body is guarded so a thrown exception is logged, not
/// propagated out of the timer callback — the daemon must never crash on a tick.
/// Returns `true` iff this tick fired the trim (for tests).
let runTick (deps: PressureTrimDeps) (latch: CooldownLatch) : bool =
    try
        let now = deps.Now()
        let nowTicks = now.Ticks
        let busy = deps.Busy()
        let loadBytes, thresholdBytes = deps.ReadMemory()
        let observed = CooldownLatch.lastFired latch

        if shouldTrim deps.Pct loadBytes thresholdBytes busy observed nowTicks then
            // Atomically claim the fire against the value we decided on. Even if
            // the pure check raced past for several threads, only one wins.
            if CooldownLatch.tryClaim latch observed nowTicks then
                let loadMb = loadBytes / (1024L * 1024L)
                let triggerMb = thresholdBytes / (1024L * 1024L) * int64 deps.Pct / 100L

                try
                    deps.Trim()

                    deps.Log
                        $"[pressure-trim] memory load %d{loadMb}MB >= %d{triggerMb}MB — released FCS root caches (cooldown 5min)"

                    true
                with ex ->
                    deps.Log $"[pressure-trim] trim failed: %s{ex.ToString()}"
                    // A failed trim still consumed the latch; the cooldown will
                    // re-arm it. Report no-fire to callers/tests.
                    false
            else
                false
        else
            false
    with ex ->
        deps.Log $"[pressure-trim] tick failed: %s{ex.ToString()}"
        false

/// Create the live scheduler `System.Threading.Timer` (period 30s). The caller
/// owns disposal. Logs the enable line on creation. Pure wiring around `runTick`
/// — all effects come through `deps`.
let createTimer (deps: PressureTrimDeps) : Timer =
    let latch = CooldownLatch.create ()

    deps.Log
        $"[pressure-trim] enabled: will release FCS caches when memory load >= %d{deps.Pct}%% of the GC high-load threshold (checked every 30s, cooldown 5min)"

    let onTick (_: obj) = runTick deps latch |> ignore

    new Timer(TimerCallback(onTick), null, PollPeriod, PollPeriod)
