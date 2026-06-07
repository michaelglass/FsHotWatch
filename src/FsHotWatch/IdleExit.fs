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
        /// Idle window before exit is eligible.
        Threshold: TimeSpan
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

/// Run one scheduler tick. Reads the live clock / busy / activity, runs the pure
/// `shouldFire` decision, and on a fire: atomically claims the latch, logs the
/// `[idle-exit] …` line, then invokes the graceful shutdown. The atomic latch
/// means that even if many ticks reach this point concurrently, `Shutdown` runs
/// at most once. The whole body is guarded so a thrown exception is logged, not
/// propagated out of the timer callback — the daemon must never crash on a tick.
/// Returns `true` iff this tick fired the shutdown (for tests).
let runTick (deps: IdleExitDeps) (latch: FireLatch) : bool =
    try
        let idleFor = deps.Now() - deps.LastActivityAt()
        let busy = deps.Busy()

        if shouldFire deps.Threshold idleFor busy (FireLatch.hasFired latch) then
            // Atomically claim the single fire. Even if the pure check above
            // raced past for several threads, only one wins tryFire.
            if FireLatch.tryFire latch then
                let minutes = int (Math.Round deps.Threshold.TotalMinutes)

                deps.Log $"[idle-exit] idle for %d{minutes}min — shutting down (next fshw command auto-restarts)"

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
    let minutes = int (Math.Round deps.Threshold.TotalMinutes)

    deps.Log $"[idle-exit] enabled: will shut down after %d{minutes}min idle (checked every 30s)"

    let onTick (_: obj) = runTick deps latch |> ignore

    new Timer(TimerCallback(onTick), null, period, period)
