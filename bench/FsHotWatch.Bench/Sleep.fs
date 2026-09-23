/// Whether the machine slept (or dark-woke) while a record's window was open.
///
/// A laptop with its lid closed on battery sleeps whatever `caffeinate` says, and a
/// record whose window spans a sleep describes a daemon that was frozen for part of it:
/// its phase timings include the sleep, and its footprint was sampled around a resume.
/// Load and free memory cannot see that, so it is checked directly.
///
/// The signal is two clocks that disagree only across sleep. On macOS the .NET
/// `Stopwatch` reads `CLOCK_UPTIME_RAW`, which does not advance while the machine sleeps;
/// wall-clock time does. Measured on the benchmark box: 7d 15h 21m of wall time since boot
/// against 6d 18h 44m of `Stopwatch` time, a 20h 38m gap across 397 `pmset` sleep entries.
/// So wall-elapsed minus monotonic-elapsed over a window is the time spent asleep in it.
module FsHotWatch.Bench.Sleep

open System
open System.Globalization
open System.Text.RegularExpressions

/// The two clocks, injectable for tests.
type Clocks =
    {
        Wall: unit -> DateTime
        /// Monotonic ticks that do NOT advance during sleep.
        Mono: unit -> int64
        /// `Mono` ticks per second.
        Frequency: int64
    }

/// The real clocks.
let system: Clocks =
    { Wall = fun () -> DateTime.UtcNow
      Mono = Diagnostics.Stopwatch.GetTimestamp
      Frequency = Diagnostics.Stopwatch.Frequency }

/// Where a window opened, on both clocks.
type Window =
    { WallStart: DateTime
      MonoStart: int64 }

let openWindow (clocks: Clocks) : Window =
    { WallStart = clocks.Wall()
      MonoStart = clocks.Mono() }

/// Wall-elapsed minus monotonic-elapsed since the window opened: the time asleep.
let gap (clocks: Clocks) (window: Window) : TimeSpan =
    let wall = clocks.Wall() - window.WallStart

    let mono =
        TimeSpan.FromSeconds(float (clocks.Mono() - window.MonoStart) / float clocks.Frequency)

    wall - mono

/// Gaps up to this are clock noise (NTP slews the wall clock), not sleep.
let Tolerance = TimeSpan.FromSeconds 2.0

/// A `pmset -g log` power event.
type PowerEvent =
    {
        At: DateTimeOffset
        /// `Sleep`, `DarkWake` or `Wake`.
        Kind: string
    }

let private powerLine =
    Regex(
        @"^(?<at>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d [+-]\d{4}) (?<kind>Sleep|DarkWake|Wake)(\s{2,}|\t)",
        RegexOptions.Compiled
    )

/// Parse the Sleep / DarkWake / Wake lines of `pmset -g log` (other lines, including
/// `Wake Requests`, are ignored). The event column is padded to a fixed width, so a
/// real event name is followed by two or more spaces or a tab; `Wake Requests` has one.
let parsePowerLog (text: string) : PowerEvent list =
    text.Split('\n')
    |> Array.toList
    |> List.choose (fun line ->
        let m = powerLine.Match(line)

        if not m.Success then
            None
        else
            match
                DateTimeOffset.TryParseExact(
                    m.Groups.["at"].Value,
                    "yyyy-MM-dd HH:mm:ss zzz",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                )
            with
            | true, at ->
                Some
                    { At = at
                      Kind = m.Groups.["kind"].Value }
            | _ -> None)

/// Validity problem for a window, `None` when the machine stayed awake. `events` (from
/// `pmset`) only add detail; the clock gap alone decides.
let problem (gapNow: TimeSpan) (window: Window) (events: PowerEvent list) : string option =
    if gapNow <= Tolerance then
        None
    else
        let inWindow =
            events
            |> List.filter (fun e -> e.At.UtcDateTime >= window.WallStart)
            |> List.map (fun e -> $"""%s{e.Kind} %s{e.At.ToString("HH:mm:ss")}""")

        let detail =
            if List.isEmpty inWindow then
                ""
            else
                " (pmset: " + String.concat ", " (List.truncate 6 inWindow) + ")"

        Some $"machine slept %.0f{gapNow.TotalSeconds} s inside this record's window%s{detail}"
