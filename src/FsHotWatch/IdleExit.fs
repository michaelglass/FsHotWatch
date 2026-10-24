module FsHotWatch.IdleExit

open System
open System.Runtime.InteropServices
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

/// Pure: the GC's high-load decision. True when `memoryLoad` has reached the GC's own
/// `highLoadThreshold`. No percentage knob — we reuse the GC's high-load mark. A 0
/// threshold (some hosts report it) never counts as pressure.
let gcPressureFrom (highLoadThreshold: int64) (memoryLoad: int64) : bool =
    highLoadThreshold > 0L && memoryLoad >= highLoadThreshold

/// Read the GC's memory-pressure signal (`gcPressureFrom` over a live
/// `GC.GetGCMemoryInfo()`).
///
/// This is the signal where the kernel offers none (Linux, Windows). On macOS it is
/// blind: the GC reports `MemoryLoadBytes = 0` while the machine thrashes, so
/// `readMemoryPressure` asks the kernel there instead.
let readGcPressure () : bool =
    let info = System.GC.GetGCMemoryInfo()
    gcPressureFrom info.HighMemoryLoadThresholdBytes info.MemoryLoadBytes

[<DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)>]
extern int private sysctlByNameInt32(
    string name,
    int& value,
    unativeint& length,
    nativeint newValue,
    unativeint newLength
)

/// Pure: decode a `sysctlbyname` answer for a 32-bit integer — the value only when the
/// call succeeded (`status = 0`) and wrote exactly four bytes.
let internal decodeInt32Sysctl (status: int) (length: unativeint) (value: int) : int option =
    if status = 0 && length = unativeint sizeof<int> then
        Some value
    else
        None

/// `kern.memorystatus_vm_pressure_level` straight from the kernel. macOS only.
let private sysctlKernelPressureLevel () : int option =
    let mutable level = 0
    let mutable length = unativeint sizeof<int>

    let status =
        sysctlByNameInt32 ("kern.memorystatus_vm_pressure_level", &level, &length, IntPtr.Zero, 0un)

    decodeInt32Sysctl status length level

/// The kernel level with the platform and the probe passed in: the probe runs only on
/// macOS, the one kernel that has this sysctl.
let internal kernelPressureLevelOn (isMacOS: bool) (probe: unit -> int option) : int option =
    if isMacOS then probe () else None

/// The macOS kernel's memory-pressure level, `kern.memorystatus_vm_pressure_level`:
/// 1 normal, 2 warning, 4 critical — the level `memory_pressure(1)` prints and
/// `DISPATCH_SOURCE_TYPE_MEMORYPRESSURE` fires on. `None` off macOS, or when the
/// sysctl does not answer.
let readKernelPressureLevel () : int option =
    kernelPressureLevelOn (OperatingSystem.IsMacOS()) sysctlKernelPressureLevel

/// Pure: the machine is under memory pressure. Where the kernel reports a level, the
/// kernel decides — warning (2) or critical (4) is pressure, normal (1) is not — and
/// the GC's mark is not consulted. Without a kernel level the GC's high-load mark
/// decides. Both readings are passed in so the decision is testable without either.
let pressureFrom (kernelLevel: int option) (gcPressure: unit -> bool) : bool =
    match kernelLevel with
    | Some level -> level >= 2
    | None -> gcPressure ()

/// Read the live memory-pressure signal: the kernel's level on macOS, the GC's
/// high-load mark elsewhere. Injected in the daemon wiring so the scheduler is
/// unit-testable without a real machine under pressure.
let readMemoryPressure () : bool =
    pressureFrom (readKernelPressureLevel ()) readGcPressure

/// Pure: the sentence appended to a daemon-disconnect or aborted-check message
/// when the MACHINE is under memory pressure.
///
/// A daemon squeezed out by memory pressure does not report an out-of-memory
/// error. It stops answering, or dies mid-scan, and the message names the
/// transport — "could not connect", "the daemon shut down" — which reads as an
/// fshw bug and sends the reader to the daemon log. The cause is usually that the
/// tree needs more memory than the machine has: fshw's footprint is dominated by
/// native FCS allocations and scales with tree size.
///
/// Empty when there is no pressure, so a genuine transport fault is not given a
/// memory story it does not deserve. Pure and taking the signal as an argument so
/// the wording is testable without a real GC.
let disconnectPressureNote (pressure: bool) : string =
    if not pressure then
        ""
    else
        " The machine is under memory pressure (the kernel pressure level, or the GC high-load mark). \
          fshw's footprint is dominated by native FCS allocations and scales with the size of the tree, so on a \
          machine the tree has outgrown this is what running out looks like rather than an out-of-memory error. \
          Measure with `footprint <daemon pid>` — `ps` refuses the field on macOS and `top`'s MEM column is a \
          different number — and note that a re-run will not help if the tree does not fit."

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

/// The environment variable that overrides `.fshw.json`'s `idleExitMin` for the one
/// daemon started with it. `.fshw.json` is shared and tracked, and an edit to it stops
/// every daemon of the repository ("config changed, stopping"), so a single workspace's
/// window cannot be set there. The variable is read once, when the daemon starts; it is
/// not a file, so setting it never triggers that restart.
[<Literal>]
let OverrideEnvVar = "FSHW_IDLE_EXIT_MIN"

/// What `FSHW_IDLE_EXIT_MIN` holds.
[<RequireQualifiedAccess>]
type OverrideValue =
    /// Not set, or blank: `.fshw.json` decides.
    | Unset
    /// A value with the same meaning it has as `idleExitMin` in `.fshw.json`.
    | Set of IdleExitConfig
    /// Neither an integer nor a boolean. Ignored (`.fshw.json` decides), and reported.
    | Invalid of string

/// Parse `FSHW_IDLE_EXIT_MIN` under the `idleExitMin` rules: a positive integer is a
/// window in minutes; `0`, a negative integer, `false` and `true` disable idle-exit
/// (`true` is not an implicit window, exactly as in `.fshw.json`). Case-insensitive,
/// surrounding whitespace ignored.
let parseOverride (raw: string) : OverrideValue =
    if String.IsNullOrWhiteSpace raw then
        OverrideValue.Unset
    else
        let value = raw.Trim()

        match
            Int32.TryParse(
                value,
                Globalization.NumberStyles.AllowLeadingSign,
                Globalization.CultureInfo.InvariantCulture
            )
        with
        | true, n when n > 0 -> OverrideValue.Set(IdleExitConfig.Minutes n)
        | true, _ -> OverrideValue.Set IdleExitConfig.Disabled
        | false, _ ->
            match value.ToLowerInvariant() with
            | "false"
            | "true" -> OverrideValue.Set IdleExitConfig.Disabled
            | _ -> OverrideValue.Invalid value

/// Where a daemon's idle-exit window came from. Named on the `[idle-exit] enabled:`
/// line so a log answers "why did this daemon (not) quit?" without reading the config
/// and the environment it was started in.
[<RequireQualifiedAccess>]
type ThresholdSource =
    /// `idleExitMin` absent: AUTO, keyed on whether the root is a `/.workspaces/` checkout.
    | Auto
    /// An explicit `idleExitMin` in `.fshw.json`.
    | ConfigFile
    /// `FSHW_IDLE_EXIT_MIN` in the daemon's environment.
    | Environment

module ThresholdSource =
    /// The name a user sets to change this source's answer.
    let describe (source: ThresholdSource) : string =
        match source with
        | ThresholdSource.Auto -> "AUTO (idleExitMin absent: 30min in /.workspaces/ checkouts, else off)"
        | ThresholdSource.ConfigFile -> ".fshw.json idleExitMin"
        | ThresholdSource.Environment -> OverrideEnvVar

/// A daemon's resolved idle-exit window, where it came from, and the warning for an
/// override that could not be read.
type ResolvedIdleExit =
    { ThresholdMin: int option
      Source: ThresholdSource
      Warning: string option }

/// Resolve the idle-exit window from `FSHW_IDLE_EXIT_MIN` (read through `getEnv`) over
/// the `.fshw.json` value. A set override wins everywhere; an unset one leaves
/// `resolveThreshold`'s answer — including AUTO's default-workspace exemption — exactly
/// as it was; an unreadable one is ignored with a warning rather than silently.
let resolveIdleExit (getEnv: string -> string) (config: IdleExitConfig) (repoRoot: string) : ResolvedIdleExit =
    let fromFile warning =
        { ThresholdMin = resolveThreshold config repoRoot
          Source =
            match config with
            | IdleExitConfig.Absent -> ThresholdSource.Auto
            | IdleExitConfig.Disabled
            | IdleExitConfig.Minutes _ -> ThresholdSource.ConfigFile
          Warning = warning }

    match parseOverride (getEnv OverrideEnvVar) with
    | OverrideValue.Unset -> fromFile None
    | OverrideValue.Set overrideConfig ->
        { ThresholdMin = resolveThreshold overrideConfig repoRoot
          Source = ThresholdSource.Environment
          Warning = None }
    | OverrideValue.Invalid value ->
        fromFile (
            Some
                $"[idle-exit] ignoring %s{OverrideEnvVar}=%s{value}: expected a positive number of minutes, 0 or false; using .fshw.json"
        )

/// The line a daemon logs when its override turned idle-exit off. A daemon with no
/// window otherwise logs nothing, which for the file's own settings is the long-standing
/// behaviour; an override that disables it must be visible in the log it is diagnosed from.
let disabledLine (source: ThresholdSource) : string option =
    match source with
    | ThresholdSource.Environment -> Some $"[idle-exit] disabled by %s{OverrideEnvVar} (overrides .fshw.json)"
    | ThresholdSource.Auto
    | ThresholdSource.ConfigFile -> None

/// Atomic fire-once latch. `0` = armed, `1` = fired. `Interlocked.CompareExchange`
/// means that under concurrent timer ticks exactly one caller observes the
/// armed-to-fired transition; every other caller is rejected.
[<NoComparison; NoEquality>]
type FireLatch = { mutable State: int }

module FireLatch =
    /// A fresh, armed latch.
    let create () : FireLatch = { State = 0 }

    /// Attempt to claim the single fire. Returns `true` for exactly one caller
    /// across all threads; `false` for every subsequent caller.
    let tryFire (latch: FireLatch) : bool =
        Interlocked.CompareExchange(&latch.State, 1, 0) = 0

    /// True once the latch has fired (or is firing). Read for diagnostics/tests.
    let hasFired (latch: FireLatch) : bool = Volatile.Read(&latch.State) = 1

/// A typed reason the daemon must NOT idle-exit right now. —
/// "busy" used to collapse to a bare bool, which is why a whole class of work
/// (a cold or forced scan) could be missing from it without anything in the log
/// or the type saying so. Each inhibitor names itself, so a deferral is
/// explainable and a missing one is a missing CASE rather than a silent `false`.
[<RequireQualifiedAccess>]
type IdleInhibitor =
    /// At least one plugin mailbox has work in flight (includes the whole
    /// lifetime of an exclusive background run).
    | PluginBusy
    /// `n` connected clients are blocked on a verdict wait (`WaitForComplete`).
    | VerdictWait of waits: int
    /// A full-repository scan of this kind is in flight (`ScanActivity`).
    | ScanInFlight of kind: ScanActivity.ScanKind

/// Rendering for `IdleInhibitor`, used in the deferral log line.
module IdleInhibitor =
    /// Human-readable phrase for one inhibitor.
    let describe (inhibitor: IdleInhibitor) : string =
        match inhibitor with
        | IdleInhibitor.PluginBusy -> "a plugin has work in flight"
        | IdleInhibitor.VerdictWait n -> $"%d{n} client verdict wait(s) in flight"
        | IdleInhibitor.ScanInFlight kind -> $"a %s{ScanActivity.ScanKind.describe kind} scan is in flight"

    /// Comma-joined phrases, for one log line.
    let describeAll (inhibitors: IdleInhibitor list) : string =
        inhibitors |> List.map describe |> String.concat ", "

/// Everything that currently forbids an idle exit. Empty means genuinely idle.
///
/// The scan leg is the fix for a daemon that shut itself down mid-scan: a cold
/// `fshw check` spends minutes in `performScan` where no plugin mailbox is in flight
/// and the client's verdict wait has not yet been bracketed, so the first two legs
/// alone read a working daemon as idle. Pure; the daemon injects the three live signals.
let idleInhibitors
    (anyPluginBusy: bool)
    (observingClients: int)
    (scansInFlight: ScanActivity.ScanKind list)
    : IdleInhibitor list =
    [ if anyPluginBusy then
          yield IdleInhibitor.PluginBusy
      if observingClients > 0 then
          yield IdleInhibitor.VerdictWait observingClients
      for kind in scansInFlight do
          yield IdleInhibitor.ScanInFlight kind ]

/// What one scheduler tick decided. Returned (rather than a bare bool) so the
/// deferral REASON is available to the caller and to tests: "deferred because a
/// cold scan is in flight" and "deferred because the window has not elapsed" are
/// different facts, and the mid-scan self-shutdown was a bug in exactly that distinction.
[<RequireQualifiedAccess>]
type TickOutcome =
    /// The latch was claimed and `Shutdown` was invoked (exactly one tick ever).
    | Fired
    /// Still inside the (possibly pressure-shortened) idle window.
    | WithinWindow of idleFor: TimeSpan * window: TimeSpan
    /// The window has elapsed but work is in flight. Never empty.
    | Inhibited of inhibitors: IdleInhibitor list
    /// A previous tick already fired; this one is a no-op.
    | AlreadyFired
    /// The tick body threw; logged and swallowed so the timer callback survives.
    | TickFailed of message: string

/// Pure decision for one tick. Order is deliberate and load-bearing for the
/// logs: `alreadyFired` vetoes first (a fired latch makes every other fact
/// irrelevant), then the window, then live work. Checking the window BEFORE the
/// inhibitors means `Inhibited` carries the strong statement "this daemon would
/// have exited right now had it not been working" — the exact event
/// the mid-scan shutdown investigation needed in the log and could not find.
let decide
    (idleThreshold: TimeSpan)
    (idleFor: TimeSpan)
    (inhibitors: IdleInhibitor list)
    (alreadyFired: bool)
    : TickOutcome =
    if alreadyFired then
        TickOutcome.AlreadyFired
    elif idleFor < idleThreshold then
        TickOutcome.WithinWindow(idleFor, idleThreshold)
    elif not (List.isEmpty inhibitors) then
        TickOutcome.Inhibited inhibitors
    else
        TickOutcome.Fired

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
        /// Where `BaseThresholdMin` came from (`resolveIdleExit`), named on the
        /// enable line.
        Source: ThresholdSource
        /// Resolved pressure floor (minutes), or `None` when pressure-shortening
        /// is disabled (`resolvePressureFloor`). Re-read against `Pressure` each
        /// tick — it never lengthens a window already smaller than it.
        PressureFloorMin: int option
        /// Current memory-pressure signal. Re-evaluated each tick (a live
        /// current-state read, not latched): if pressure subsides the full window
        /// is restored. Production wires `IdleExit.readMemoryPressure`.
        Pressure: unit -> bool
        /// Current UTC time.
        Now: unit -> DateTime
        /// Everything currently forbidding an exit — plugin work, connected
        /// verdict waits, and in-flight scans (`idleInhibitors`). Empty means
        /// genuinely idle. Typed rather than a bool so the deferral reason
        /// reaches the log.
        Inhibitors: unit -> IdleInhibitor list
        /// UTC of the most recent activity (event / completed work).
        LastActivityAt: unit -> DateTime
        /// The graceful-shutdown action (cts.Cancel() in production). Invoked at
        /// most once, guaranteed by the atomic latch.
        Shutdown: unit -> unit
        /// Structured logger: `log message`.
        Log: string -> unit
    }

/// Run one scheduler tick: resolve the (possibly pressure-shortened) effective
/// window, run the pure `decide`, and on a fire atomically claim the latch, log,
/// then invoke the graceful shutdown. Pressure is re-evaluated every tick, so if
/// it subsides the full window is restored. The latch means `Shutdown` runs at
/// most once even if many ticks reach this point concurrently. The whole body is
/// guarded so a thrown exception is logged rather than escaping the timer
/// callback. Returns what the tick decided.
///
/// An `Inhibited` tick LOGS: the window had elapsed and the daemon stayed up
/// only because work was in flight. That line is the audit trail the mid-scan
/// shutdown incident could not produce — a cold scan that outlives the (pressure-shortened) window says so
/// every 30s instead of being terminated in silence.
let runTick (deps: IdleExitDeps) (latch: FireLatch) : TickOutcome =
    try
        let idleFor = deps.Now() - deps.LastActivityAt()
        let inhibitors = deps.Inhibitors()
        let pressure = deps.Pressure()
        // BaseThresholdMin is always > 0 here — createTimer is only wired once
        // resolveThreshold returned Some, so the daemon is already eligible.
        let effectiveMin =
            effectiveThreshold deps.BaseThresholdMin pressure deps.PressureFloorMin

        let threshold = TimeSpan.FromMinutes(float effectiveMin)

        // An eligible tick makes one atomic latch claim. Reading the latch first and
        // claiming it after would make the losing claim depend on how threads
        // interleave; a later or concurrent eligible tick loses the same claim instead.
        match decide threshold idleFor inhibitors false with
        | TickOutcome.Fired ->
            if FireLatch.tryFire latch then
                let pressureNote =
                    if pressure && effectiveMin < deps.BaseThresholdMin then
                        $" (memory pressure shortened the %d{deps.BaseThresholdMin}min window)"
                    else
                        ""

                deps.Log
                    $"[idle-exit] idle for %d{effectiveMin}min — shutting down (next fshw command auto-restarts)%s{pressureNote}"

                deps.Shutdown()
                TickOutcome.Fired
            else
                TickOutcome.AlreadyFired
        // An ineligible tick still reports a latch that has already fired.
        | _ when FireLatch.hasFired latch -> TickOutcome.AlreadyFired
        | TickOutcome.Inhibited reasons as outcome ->
            deps.Log
                $"[idle-exit] %d{effectiveMin}min window elapsed but work is in flight — staying up: %s{IdleInhibitor.describeAll reasons}"

            outcome
        | outcome -> outcome
    with ex ->
        deps.Log $"[idle-exit] tick failed: %s{ex.ToString()}"
        TickOutcome.TickFailed(ex.Message)

/// Create the scheduler timer with an explicit tick period. The caller owns
/// disposal; once it returns, no tick is running and none will, so the caller may
/// then dispose what `deps.Shutdown` uses. Logs the enable line on creation. Pure
/// wiring around `runTick` — all effects come through `deps`.
let createTimerWith (period: TimeSpan) (deps: IdleExitDeps) : IDisposable =
    let latch = FireLatch.create ()

    let floorNote =
        match deps.PressureFloorMin with
        | Some f when f < deps.BaseThresholdMin -> $" (or %d{f}min under memory pressure)"
        | _ -> ""

    deps.Log
        $"[idle-exit] enabled: will shut down after %d{deps.BaseThresholdMin}min idle (checked every 30s)%s{floorNote} — from %s{ThresholdSource.describe deps.Source}"

    DrainedTimer.every period (fun () -> runTick deps latch |> ignore)

/// Create the live scheduler timer (period 30s).
let createTimer (deps: IdleExitDeps) : IDisposable =
    createTimerWith (TimeSpan.FromSeconds 30.0) deps
