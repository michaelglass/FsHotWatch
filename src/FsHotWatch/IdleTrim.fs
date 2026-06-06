module FsHotWatch.IdleTrim

open System

/// Mutable bookkeeping for the idle-trim scheduler. The *source* of activity is
/// supplied per tick (the daemon feeds `PluginHost.LastActivityAt()`, which is
/// already bumped on every event dispatch and plugin status transition), so the
/// scheduler doesn't duplicate that tracking. This state only latches the
/// activity stamp a trim was last fired against, so a periodic tick doesn't
/// re-fire every period while the daemon stays idle. A fresh activity bumps the
/// fed-in `lastActivityTicks` past the latch, re-arming the next idle window.
///
/// The latch is read by the timer thread and written after a trim; touched
/// through `Volatile` for visibility.
[<NoComparison; NoEquality>]
type IdleTrimState =
    { mutable TrimmedSinceActivityTicks: int64 }

module IdleTrimState =
    /// Initial state: no trim fired yet (sentinel -1 can never equal a real
    /// activity tick, so the first idle window is always eligible).
    let create () : IdleTrimState = { TrimmedSinceActivityTicks = -1L }

/// Pure decision: should the scheduler fire a trim on this tick?
///
/// Fires exactly once per idle window: when the idle duration since the last
/// activity has met/exceeded `idleThreshold`, no work is currently running,
/// and a trim has not already been fired against the current activity stamp.
///
/// `busy` collapses "any plugin running OR any check in flight" to a single
/// bool at the call site, keeping this function free of host/FCS types and
/// trivially unit-testable. `lastActivityTicks` is the UTC tick count of the
/// most recent activity (file event / completed work).
let shouldTrim
    (idleThreshold: TimeSpan)
    (now: DateTime)
    (busy: bool)
    (lastActivityTicks: int64)
    (state: IdleTrimState)
    : bool =
    let trimmedSince = System.Threading.Volatile.Read(&state.TrimmedSinceActivityTicks)
    let idleFor = now - DateTime(lastActivityTicks, DateTimeKind.Utc)

    idleFor >= idleThreshold && not busy && trimmedSince <> lastActivityTicks

/// Run one scheduler tick: if `shouldTrim` holds, invoke `trim`, then latch the
/// current activity stamp so the trim doesn't re-fire while the daemon stays
/// idle. A later activity bumps `lastActivityTicks` past this latch, re-arming.
/// Returns `true` when a trim was fired (for logging/tests).
let tick
    (idleThreshold: TimeSpan)
    (now: DateTime)
    (busy: bool)
    (lastActivityTicks: int64)
    (trim: unit -> unit)
    (state: IdleTrimState)
    : bool =
    if shouldTrim idleThreshold now busy lastActivityTicks state then
        trim ()
        System.Threading.Volatile.Write(&state.TrimmedSinceActivityTicks, lastActivityTicks)
        true
    else
        false

/// Dependencies a live idle-trim scheduler needs, all injectable so the
/// scheduler's wiring (the per-tick callback and the trim action) can be
/// unit-tested without FCS, a PluginHost, or a real timer.
[<NoComparison; NoEquality>]
type IdleTrimDeps =
    {
        /// Idle window before a trim is eligible.
        Threshold: System.TimeSpan
        /// Current UTC time.
        Now: unit -> DateTime
        /// True when any plugin/check is in flight (defers the trim).
        Busy: unit -> bool
        /// UTC tick count of the most recent activity (event / completed work).
        LastActivityTicks: unit -> int64
        /// The trim action (release FCS root caches). Wrapped by the runner with
        /// a try/with so a failing trim never escapes the timer callback.
        Trim: unit -> unit
        /// Structured logger: `log level message`. `level` is "info" / "error".
        Log: string -> string -> unit
    }

/// Build the per-tick callback for the scheduler. Reads `Now`/`Busy`/
/// `LastActivityTicks`, runs the pure `tick` decision, and on a fire logs the
/// "released FCS root caches" line. Guards the whole tick (including `Trim`) so
/// a thrown exception is logged, not propagated out of the timer callback —
/// the daemon must never crash on a trim failure. Returns whether a trim fired
/// (for tests). The trim's own body is additionally guarded so a partial trim
/// failure still latches/logs consistently.
let runTick (deps: IdleTrimDeps) (idleMinutesLabel: int) (state: IdleTrimState) : bool =
    try
        let guardedTrim () =
            try
                deps.Trim()
                deps.Log "info" $"released FCS root caches after %d{idleMinutesLabel}min idle"
            with ex ->
                deps.Log "error" $"idle trim failed: %s{ex.ToString()}"

        tick deps.Threshold (deps.Now()) (deps.Busy()) (deps.LastActivityTicks()) guardedTrim state
    with ex ->
        deps.Log "error" $"idle-trim tick failed: %s{ex.ToString()}"
        false

/// Create the live scheduler `System.Threading.Timer` (period 30s). The caller
/// owns disposal. Logs the enable line on creation. Pure wiring around
/// `runTick` — all effects come through `deps`.
let createTimer (deps: IdleTrimDeps) (idleMinutesLabel: int) : System.Threading.Timer =
    let state = IdleTrimState.create ()
    let period = System.TimeSpan.FromSeconds(30.0)

    deps.Log
        "info"
        $"idle-trim enabled: releasing FCS root caches after %d{idleMinutesLabel}min idle (checked every 30s)"

    let onTick (_: obj) =
        runTick deps idleMinutesLabel state |> ignore

    new System.Threading.Timer(System.Threading.TimerCallback(onTick), null, period, period)
