module FsHotWatch.PluginWedge

open System
open System.IO
open FsHotWatch.Logging

/// Plugin wedge detection: a plugin that reported `Running` and posts no completion
/// past a bound is by definition wedged.
///   1. Report — `fshw status` and the daemon log both use `wedgedText`. Under the
///      bound the verdict is "still running … (no completion posted)": the detector
///      cannot yet tell, and never claims healthy by default.
///   2. Recover — past the bound the monitor writes the last-wedge breadcrumb and
///      gracefully shuts the daemon down (the same `cts.Cancel()` path as `fshw stop`,
///      so tracked child processes are reaped and SQLite is never killed mid-write).
///      The next fshw command starts a fresh daemon and prints the breadcrumb.
///   3. Don't over-correct — the bound sits above the verdict deadline (the longest
///      legitimately-bounded wait in the system) plus grace, so a healthy daemon's
///      warm FCS cache is not discarded for a long-but-live run, and a client blocked
///      on `WaitForComplete` gets its own more specific TimeoutException first.
///
/// Format elapsed as "5m 3s" / "45s" / "1h 12m". Daemon.formatElapsed delegates here
/// so the daemon's wait logs and the wedge wording cannot drift.
let formatElapsed (ts: TimeSpan) =
    if ts.TotalHours >= 1.0 then
        $"%d{int ts.TotalHours}h %d{ts.Minutes}m"
    elif ts.TotalMinutes >= 1.0 then
        $"%d{int ts.TotalMinutes}m %d{ts.Seconds}s"
    else
        $"%d{ts.Seconds}s"

/// Grace added on top of the verdict deadline before a Running plugin is
/// treated as wedged. A client blocked on `WaitForComplete` gets its own
/// TimeoutException (naming the plugin) at the verdict deadline FIRST — the
/// daemon-side recovery is the backstop for when no client is waiting at all.
let WedgeGrace = TimeSpan.FromMinutes 5.0

/// Env var that overrides the wedge bound directly (seconds). Wins over the
/// verdict-deadline derivation; used by tests/operators to shorten the bound.
[<Literal>]
let WedgeBoundEnvVar = "FSHW_PLUGIN_WEDGE_SEC"

/// How often a still-Running plugin earns a fresh "still running …" log line.
let DefaultEscalateEvery = TimeSpan.FromMinutes 5.0

/// Resolve the wedge bound. Precedence: a positive `FSHW_PLUGIN_WEDGE_SEC`
/// wins; otherwise the verdict deadline (`FSHW_VERDICT_DEADLINE_SEC`, default
/// 60 min — resolved by `Ipc.resolveVerdictDeadline`, the ONE deadline notion)
/// plus `WedgeGrace`. Pure so precedence is unit-testable.
let resolveBound (wedgeOverrideSec: string option) (verdictOverrideSec: string option) : TimeSpan =
    let fallback () =
        Ipc.resolveVerdictDeadline verdictOverrideSec + WedgeGrace

    match wedgeOverrideSec with
    | Some s ->
        match Int32.TryParse(s: string) with
        | true, n when n > 0 -> TimeSpan.FromSeconds(float n)
        | _ -> fallback ()
    | None -> fallback ()

/// The ambient wedge bound from process env. Read by both the daemon monitor
/// and the CLI status renderer, so the two surfaces agree on when "running"
/// becomes "wedged".
let ambientBound () : TimeSpan =
    let env name =
        Environment.GetEnvironmentVariable(name: string) |> Option.ofObj

    resolveBound (env WedgeBoundEnvVar) (env "FSHW_VERDICT_DEADLINE_SEC")

/// Health of one Running plugin at `now`. Not healthy-by-default: under the bound the
/// verdict is only that no completion has been posted yet.
[<RequireQualifiedAccess>]
type RunningHealth =
    /// Under the bound: running, no completion posted — cannot tell yet.
    | StillRunning of elapsed: TimeSpan
    /// Past the bound: by definition wedged.
    | Wedged of since: DateTime * elapsed: TimeSpan

/// Classify one Running plugin. A `since` in the future (clock skew) clamps
/// elapsed to zero rather than going negative.
let classifyRunning (bound: TimeSpan) (now: DateTime) (since: DateTime) : RunningHealth =
    let elapsed = if now > since then now - since else TimeSpan.Zero

    if elapsed >= bound then
        RunningHealth.Wedged(since, elapsed)
    else
        RunningHealth.StillRunning elapsed

/// The one wedge sentence, used verbatim by `fshw status` and the daemon log.
let wedgedText (since: DateTime) (elapsed: TimeSpan) : string =
    $"""WEDGED: started %s{since.ToString "HH:mm:ss"}, no completion in %s{formatElapsed elapsed}"""

/// The full recovery message: logged by the daemon as it restarts itself, and written
/// to the last-wedge breadcrumb the next CLI command prints.
let wedgeRecoveryMessage (plugin: string) (since: DateTime) (elapsed: TimeSpan) : string =
    $"daemon was wedged on '%s{plugin}' (%s{wedgedText since elapsed}) — restarted it; the next fshw command starts a fresh daemon"

/// The can't-tell recovery message: work was in flight but no plugin reported Running
/// for the whole bound, so the detector cannot name the plugin. Fails closed.
let unobservableWedgeMessage (quietFor: TimeSpan) : string =
    $"daemon was wedged: work was in flight for %s{formatElapsed quietFor} with no plugin reporting Running — cannot tell which plugin; failing closed and restarting the daemon"

/// Periodic escalation line for a Running plugin under the bound. States the absence
/// explicitly, and names when the daemon will treat the run as wedged.
let stillRunningText (plugin: string) (elapsed: TimeSpan) (bound: TimeSpan) : string =
    $"%s{plugin} still running after %s{formatElapsed elapsed} (no completion posted; treated as wedged at %s{formatElapsed bound})"

// ---------------------------------------------------------------------------
// Tick decision — pure, so the monitor's behaviour is deterministic in tests.
// ---------------------------------------------------------------------------

/// What one monitor tick decided to do.
[<RequireQualifiedAccess; NoComparison>]
type TickAction =
    /// Periodic escalation log for a Running plugin under the bound.
    | LogStillRunning of plugin: string * elapsed: TimeSpan
    /// A Running plugin crossed the bound — recover.
    | DeclareWedged of plugin: string * since: DateTime * elapsed: TimeSpan
    /// Busy with no Running plugin and no host activity for the whole bound:
    /// the detector cannot name the plugin — fail closed and recover.
    | DeclareUnobservableWedge of quietFor: TimeSpan

/// Live inputs for one tick.
[<NoComparison; NoEquality>]
type TickInputs =
    {
        Now: DateTime
        /// Every plugin currently reporting `Running`, with its start time.
        RunningPlugins: (string * DateTime) list
        /// True when any plugin has work in flight (mailbox events or an
        /// exclusive background run) — `PluginHost.AnyPluginBusy`.
        AnyBusy: bool
        /// UTC of the most recent host activity — `PluginHost.LastActivityAt`.
        LastActivityAt: DateTime
    }

/// Escalation bucket: how many whole `escalateEvery` intervals have elapsed.
/// A plugin logs one escalation line per bucket, not one per tick.
let internal escalationBucket (escalateEvery: TimeSpan) (elapsed: TimeSpan) : int =
    if escalateEvery <= TimeSpan.Zero then
        0
    else
        int (elapsed.Ticks / escalateEvery.Ticks)

/// Per-run key for escalation tracking: a NEW run of the same plugin (new
/// `since`) starts its escalations from scratch.
let private runKey (plugin: string) (since: DateTime) = $"%s{plugin}|%d{since.Ticks}"

/// Decide one tick's actions. `loggedBuckets` carries the last escalation
/// bucket already logged per in-flight run; the returned map is pruned to the
/// runs still in flight, so retired runs don't accumulate.
let decideTick
    (bound: TimeSpan)
    (escalateEvery: TimeSpan)
    (inputs: TickInputs)
    (loggedBuckets: Map<string, int>)
    : TickAction list * Map<string, int> =
    let mutable actions = []
    let mutable newBuckets = Map.empty

    for (plugin, since) in inputs.RunningPlugins do
        match classifyRunning bound inputs.Now since with
        | RunningHealth.Wedged(s, e) -> actions <- TickAction.DeclareWedged(plugin, s, e) :: actions
        | RunningHealth.StillRunning elapsed ->
            let key = runKey plugin since
            let bucket = escalationBucket escalateEvery elapsed
            let alreadyLogged = Map.tryFind key loggedBuckets |> Option.defaultValue 0

            if bucket > alreadyLogged then
                actions <- TickAction.LogStillRunning(plugin, elapsed) :: actions

            newBuckets <- Map.add key (max bucket alreadyLogged) newBuckets

    // The can't-tell wedge: busy, nothing Running, and the host has been
    // silent for the entire bound. If anything HAD happened (an event, a
    // status transition) LastActivityAt would be fresh.
    if inputs.RunningPlugins.IsEmpty && inputs.AnyBusy then
        let quietFor =
            if inputs.Now > inputs.LastActivityAt then
                inputs.Now - inputs.LastActivityAt
            else
                TimeSpan.Zero

        if quietFor >= bound then
            actions <- TickAction.DeclareUnobservableWedge quietFor :: actions

    List.rev actions, newBuckets

// ---------------------------------------------------------------------------
// Last-wedge breadcrumb — how "the tool says what it did" survives the restart.
// ---------------------------------------------------------------------------

/// Path of the breadcrumb the daemon writes when it restarts itself over a
/// wedge. The next CLI command prints it (⚠ …) and deletes it.
let breadcrumbPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "last-wedge")

/// Write the recovery message (atomic, torn-write safe). Failures are logged,
/// never thrown — the shutdown must proceed regardless.
let writeBreadcrumb (repoRoot: string) (message: string) : unit =
    try
        FsHwPaths.atomicWriteAllText (breadcrumbPath repoRoot) message
    with ex ->
        debug "wedge" $"could not write the last-wedge breadcrumb: %s{ex.GetType().Name}: %s{ex.Message}"

/// Read AND delete the breadcrumb. `None` when absent/empty/unreadable.
let consumeBreadcrumb (repoRoot: string) : string option =
    let path = breadcrumbPath repoRoot

    if not (File.Exists path) then
        None
    else
        try
            let content = File.ReadAllText(path).Trim()
            File.Delete path
            if content = "" then None else Some content
        with ex ->
            debug "wedge" $"could not consume the last-wedge breadcrumb: %s{ex.GetType().Name}: %s{ex.Message}"
            None

// ---------------------------------------------------------------------------
// Live monitor — a 30s timer around the pure tick, mirroring IdleExit's shape.
// ---------------------------------------------------------------------------

/// Dependencies for the live monitor, all injectable so the wiring is
/// unit-testable without a PluginHost or a real timer.
[<NoComparison; NoEquality>]
type MonitorDeps =
    {
        Bound: TimeSpan
        EscalateEvery: TimeSpan
        Now: unit -> DateTime
        RunningPlugins: unit -> (string * DateTime) list
        AnyBusy: unit -> bool
        LastActivityAt: unit -> DateTime
        /// Escalation log sink ("still running …" lines) — info-level in prod.
        Log: string -> unit
        /// Fired AT MOST ONCE (atomic latch), with the full recovery message.
        /// Production: error-log + write the breadcrumb + cancel the daemon's
        /// lifetime (graceful shutdown; the next command starts fresh).
        OnWedged: string -> unit
    }

/// Run one monitor tick. Returns true iff this tick declared a wedge (fired
/// `OnWedged`). Exceptions are logged, never propagated out of a timer tick.
let runTick (deps: MonitorDeps) (latch: IdleExit.FireLatch) (buckets: Map<string, int> ref) : bool =
    try
        let inputs =
            { Now = deps.Now()
              RunningPlugins = deps.RunningPlugins()
              AnyBusy = deps.AnyBusy()
              LastActivityAt = deps.LastActivityAt() }

        let actions, newBuckets =
            decideTick deps.Bound deps.EscalateEvery inputs buckets.Value

        buckets.Value <- newBuckets

        let mutable fired = false

        for action in actions do
            match action with
            | TickAction.LogStillRunning(plugin, elapsed) -> deps.Log(stillRunningText plugin elapsed deps.Bound)
            | TickAction.DeclareWedged(plugin, since, elapsed) ->
                if IdleExit.FireLatch.tryFire latch then
                    deps.OnWedged(wedgeRecoveryMessage plugin since elapsed)
                    fired <- true
            | TickAction.DeclareUnobservableWedge quietFor ->
                if IdleExit.FireLatch.tryFire latch then
                    deps.OnWedged(unobservableWedgeMessage quietFor)
                    fired <- true

        fired
    with ex ->
        deps.Log $"wedge-monitor tick failed: %s{ex.ToString()}"
        false

/// Create the live monitor timer with an explicit period. The caller owns
/// disposal. All effects come through `deps`.
let createMonitorWith (period: TimeSpan) (deps: MonitorDeps) : IDisposable =
    let latch = IdleExit.FireLatch.create ()
    let buckets = ref Map.empty
    let onTick (_: obj) = runTick deps latch buckets |> ignore
    new System.Threading.Timer(System.Threading.TimerCallback(onTick), null, period, period) :> IDisposable

/// Create the live monitor timer (period 30s).
let createMonitor (deps: MonitorDeps) : IDisposable =
    createMonitorWith (TimeSpan.FromSeconds 30.0) deps
