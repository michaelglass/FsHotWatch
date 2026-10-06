/// Wall time and awake time, read together so a bound can count only the time the
/// host was running and a log can say when it was not.
///
/// The awake clock is `Stopwatch`'s timestamp: `CLOCK_UPTIME_RAW` on macOS and
/// `CLOCK_MONOTONIC` on Linux, both of which stop while the host is suspended. The wall
/// clock (`DateTime.UtcNow`) keeps running through a suspension, so the difference
/// between the two over an interval is how long the host slept in it.
module FsHotWatch.HostClock

open System
open System.Diagnostics

/// A source of wall and awake time. Production uses `system`; tests inject both.
[<NoComparison; NoEquality>]
type Clock =
    {
        /// The wall clock (UTC). Runs through a suspension.
        WallNow: unit -> DateTime
        /// Time the host has been awake, from an arbitrary origin. Stops while suspended.
        AwakeNow: unit -> TimeSpan
    }

/// One reading of both clocks.
[<Struct>]
type Reading = { Wall: DateTime; Awake: TimeSpan }

/// The process's clocks: `DateTime.UtcNow` and `Stopwatch`'s timestamp.
let system: Clock =
    { WallNow = fun () -> DateTime.UtcNow
      AwakeNow = fun () -> Stopwatch.GetElapsedTime 0L }

/// Both clocks, read now.
let read (clock: Clock) : Reading =
    { Wall = clock.WallNow()
      Awake = clock.AwakeNow() }

/// How long the host was suspended between two readings: the wall time that passed
/// beyond the awake time. Never negative; a wall clock stepped backwards reads as zero.
let suspendedBetween (earlier: Reading) (later: Reading) : TimeSpan =
    let gap = (later.Wall - earlier.Wall) - (later.Awake - earlier.Awake)
    if gap > TimeSpan.Zero then gap else TimeSpan.Zero

/// The smallest gap reported as a suspension. Smaller gaps are scheduling jitter or a
/// small wall-clock adjustment.
let SuspensionFloor = TimeSpan.FromSeconds 5.0

/// `host was suspended ~Ns <during>` when the host slept for at least
/// `SuspensionFloor` between the readings, else `None`. A wall-clock step forward of
/// the same size reads the same; the line names both figures so the two can be told
/// apart against the system's sleep log (`pmset -g log` on macOS).
let suspensionLine (during: string) (earlier: Reading) (later: Reading) : string option =
    let suspended = suspendedBetween earlier later

    if suspended >= SuspensionFloor then
        let wall = later.Wall - earlier.Wall
        let awake = later.Awake - earlier.Awake

        Some
            $"host was suspended ~%d{int suspended.TotalSeconds}s %s{during} (wall clock advanced %d{int wall.TotalSeconds}s, awake %d{int awake.TotalSeconds}s)"
    else
        None
