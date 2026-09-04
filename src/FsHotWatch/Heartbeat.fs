module FsHotWatch.Heartbeat

open System
open System.IO
open System.Threading

/// Activity heartbeat: the daemon publishes a fact about ITSELF — "I am executing
/// work right now" — as `<repoRoot>/.fshw/heartbeat`, rewritten every 15 s for as
/// long as a run is in progress. Same shape as `.fshw/daemon.pid`: a plain,
/// externally-readable file. Consumers (e.g. a box-wide gate lock serialising heavy
/// runs across workspaces) interpret it; fshw itself knows nothing about any lock.
///
/// An idle daemon MUST NOT beat, so "beating" means "actually working" rather than
/// "still resident" — a process-liveness signal (is pid N alive?) cannot distinguish
/// a working daemon from a wedged one.
///
/// SAFETY DIRECTION: absence of the file, or unparseable contents, means UNKNOWN,
/// never "stale" — a consumer that cannot read a beat must fall back to its own
/// timeout. Erring toward "still alive" costs a slow reclaim; erring toward "dead"
/// lets two heavy runs proceed concurrently. Hence: the file is never deleted (a
/// stale timestamp is more actionable than absence), writes are atomic (no torn read
/// can masquerade as a fresh beat), and a failed write is logged, not propagated.
///
/// Name of the heartbeat file inside `.fshw/`.
[<Literal>]
let FileName = "heartbeat"

/// Absolute path to the heartbeat file for a repo root.
let path (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, FileName)

/// The published cadence: one beat every 15 s while a run is in progress.
/// Consumers must treat this as the FLOOR of a staleness threshold, never the
/// threshold itself — a beat can be late (a saturated box, a slow disk).
let DefaultCadence = TimeSpan.FromSeconds 15.0

/// How often the timer wakes to CONSIDER beating. Much finer than the cadence so the
/// FIRST beat of a run lands within a second of the run starting.
///
/// The file is never deleted, so between runs it holds the previous run's
/// (arbitrarily old) timestamp. A first beat up to 15 s late would let a consumer
/// polling in that window read an ancient timestamp while a run WAS executing, and
/// conclude "dead". The fine tick plus the cadence gate closes that to ~1 s while
/// still writing only every 15 s.
let DefaultTick = TimeSpan.FromSeconds 1.0

/// Render an instant as the file's contents: Unix epoch SECONDS in decimal
/// ASCII, no trailing newline (matching the `daemon.pid` precedent, which
/// writes bare `string pid`). Both the contents and the file's mtime are
/// therefore usable liveness signals — the contents matter because some
/// filesystems have coarse mtime granularity.
let render (at: DateTime) : string =
    let utc = DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc)
    string (DateTimeOffset(utc).ToUnixTimeSeconds())

/// True when the daemon is actively executing work — either any plugin has
/// work in flight (mailbox events OR an exclusive background run, both
/// observed by `PluginHost.AnyPluginBusy`) or a client is blocked waiting for
/// a verdict (`WaitForComplete`).
///
/// Both legs are needed:
///   * `anyPluginBusy` survives LONG QUIET PHASES. A plugin's inflight counter is
///     held for the entire lifetime of a `RunExclusive` run — from slot claim until
///     after the completion message is posted (see `PluginFramework`'s
///     `inflightCount` doc comment). So a suite that runs for ten minutes emitting
///     nothing keeps this true throughout: the beat tracks "a run is in progress",
///     not log output.
///   * `activeVerdictWaits` covers the instants where no plugin work is in flight but
///     a `fshw check` client is still connected and waiting.
///   * `scanInFlight` (AUTOMATION-609) covers a full-repository scan — cold FCS
///     analysis of the whole graph raises NEITHER of the other two, so without it
///     the daemon's own liveness signal went silent during its longest phase of
///     real work.
///
/// Deliberately separate from `IdleExit.idleInhibitors` despite the overlapping
/// inputs: they answer different questions (shut myself down? vs. tell the world
/// I'm working?), and a change to one must not silently redefine the other.
let runActive (anyPluginBusy: bool) (activeVerdictWaits: int) (scanInFlight: bool) : bool =
    anyPluginBusy || activeVerdictWaits > 0 || scanInFlight

/// Dependencies a live heartbeat needs, all injectable so the beat/no-beat
/// decision is unit-tested without a PluginHost, a real clock, a real timer,
/// or a real filesystem.
[<NoComparison; NoEquality>]
type HeartbeatDeps =
    {
        /// Current UTC time. Also the value written.
        Now: unit -> DateTime
        /// True iff a run is in progress. Production wires `runActive` over the
        /// host's live busy signal and the active-verdict-wait counter.
        RunActive: unit -> bool
        /// Publish the rendered beat. Production wires an atomic write to
        /// `path repoRoot`; tests capture it.
        Write: string -> unit
        /// Structured logger, used ONLY for failures. A healthy beat is silent:
        /// at four writes a minute for the life of a run, logging every beat
        /// would drown the daemon log.
        Log: string -> unit
        /// Minimum interval between two writes. `DefaultCadence` in production.
        Cadence: TimeSpan
    }

/// Run one heartbeat tick.
///
/// Idle ⇒ never writes, and RESETS the cadence gate so the first tick of the
/// next run beats immediately instead of inheriting the previous run's
/// schedule. Running ⇒ writes iff no beat has been written since the gate was
/// reset, or the cadence has elapsed since the last beat.
///
/// The whole body is guarded: a beat that cannot be written (disk full, a racing
/// `.fshw` cleanup, permissions) is logged and swallowed. A daemon must never die
/// because it failed to announce itself, and a missing beat degrades to "unknown".
///
/// Returns `true` iff this tick wrote a beat (for tests).
let runTick (deps: HeartbeatDeps) (lastBeat: DateTime option ref) : bool =
    try
        if not (deps.RunActive()) then
            lastBeat.Value <- None
            false
        else
            let now = deps.Now()

            let due =
                match lastBeat.Value with
                | None -> true
                | Some last -> now - last >= deps.Cadence

            if due then
                deps.Write(render now)
                lastBeat.Value <- Some now
                true
            else
                false
    with ex ->
        deps.Log $"[heartbeat] beat failed: %s{ex.GetType().Name}: %s{ex.Message}"
        false

/// Run `beat` only if no beat is already in flight, using `beating` as a
/// non-blocking latch. Returns `true` iff this call ran the beat.
///
/// A `Timer` fires on its period whether or not the previous callback has finished,
/// so at a one-second tick on a loaded box the callbacks DO overlap. Two beats at
/// once race on the single temp file inside the atomic write, and the loser's rename
/// finds its source already consumed — observed live as a swallowed
/// `FileNotFoundException` on `heartbeat.tmp` during heavy runs.
///
/// Non-blocking on purpose: a tick that finds a beat already in flight skips, because
/// the beat in flight publishes the same fact. Skipping is correct, not a lost update.
///
/// Extracted from the timer callback so the serialisation property can be tested
/// deterministically — re-entering this function proves the latch excludes a second
/// beat with no threads, sleeps, or timer scheduling involved.
let internal guardedBeat (beating: int ref) (beat: unit -> unit) : bool =
    if Interlocked.CompareExchange(&beating.contents, 1, 0) = 0 then
        try
            beat ()
            true
        finally
            Volatile.Write(&beating.contents, 0)
    else
        false

/// Create the heartbeat timer with an explicit tick period. The period is a
/// seam for tests (they run a real timer at tens of milliseconds); production
/// uses `createBeat`.
let createBeatWith (tick: TimeSpan) (deps: HeartbeatDeps) : IDisposable =
    let lastBeat: DateTime option ref = ref None
    let beating = ref 0

    let onTick (_: obj) =
        guardedBeat beating (fun () -> runTick deps lastBeat |> ignore) |> ignore

    new Timer(TimerCallback(onTick), null, tick, tick) :> IDisposable

/// Create the live heartbeat timer: considers beating every `DefaultTick`,
/// writes at most every `DefaultCadence`, and only while a run is in progress.
let createBeat (deps: HeartbeatDeps) : IDisposable = createBeatWith DefaultTick deps

/// Production `Write`: publish the beat atomically (temp file + rename) so a consumer
/// reading concurrently sees either the previous beat or this one, never a torn file.
let writeTo (repoRoot: string) (contents: string) : unit =
    FsHwPaths.atomicWriteAllText (path repoRoot) contents
