/// The one fake clock the tests move by hand.
///
/// Production reads time through one seam, `HostClock.Clock` (wall time and awake time), or
/// a `unit -> DateTime` / `unit -> TimeSpan` cut from one: `WorkScheduler.Scheduler` takes
/// an awake clock, `Daemon.waitForAllTerminalCoreOn` a wall clock, the launch watchdog and
/// `OperationWatchdog` both. A test injects this clock's members there and moves time with
/// `Advance` or `Suspend`, so a threshold, deadline or fairness window is crossed when the
/// test says, never when the box gets round to it (FSHW-WALL-001,
/// docs/wall-clock-bounds.md).
///
/// Both clocks live in ONE reading, replaced whole under a lock, so a reader never sees
/// wall time moved and awake time not: two separate cells raced the watchdog's tick into a
/// phantom suspension (AUTOMATION-1148).
module FsHotWatch.Tests.FakeClock

open System
open System.Threading
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

/// A fixed origin, so nothing a fake clock reports depends on when the test ran.
let Origin = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

/// Wall and awake time that only `Advance` and `Suspend` move. Awake time starts at zero.
type ManualClock(start: DateTime) =
    let gate = obj ()
    let mutable reading: HostClock.Reading = { Wall = start; Awake = TimeSpan.Zero }
    let mutable reads = 0L

    new() = ManualClock(Origin)

    /// Both clocks, read together. Counts as one sample (see `AwaitSamples`).
    member _.Read() : HostClock.Reading =
        Interlocked.Increment(&reads) |> ignore
        lock gate (fun () -> reading)

    /// The wall clock (UTC).
    member this.WallNow() : DateTime = this.Read().Wall

    /// Awake time since the clock was made.
    member this.AwakeNow() : TimeSpan = this.Read().Awake

    /// This clock as the production seam.
    member this.Clock: HostClock.Clock =
        { WallNow = this.WallNow
          AwakeNow = this.AwakeNow }

    /// Both clocks forward by `by`, in one step.
    member _.Advance(by: TimeSpan) =
        lock gate (fun () ->
            reading <-
                { Wall = reading.Wall + by
                  Awake = reading.Awake + by })

    /// `Advance` by a number of seconds.
    member this.AdvanceSeconds(seconds: float) =
        this.Advance(TimeSpan.FromSeconds seconds)

    /// The host sleeps for `by`: the wall clock moves and the awake clock does not.
    member _.Suspend(by: TimeSpan) =
        lock gate (fun () ->
            reading <-
                { reading with
                    Wall = reading.Wall + by })

    /// How many times the clock has been read.
    member _.Reads = Interlocked.Read(&reads)

    /// Returns once the code under test has read the clock twice more, or `running` has
    /// ended and so stopped reading. A read counted after this call starts sees every
    /// `Advance` made before it, so this is how a test waits for a moved clock to be SEEN,
    /// instead of for time to pass.
    member this.AwaitSamples(running: Tasks.Task) =
        let seen = this.Reads

        waitUntilOr
            "the code under test sampling its clock"
            (fun () -> running.IsCompleted || this.Reads >= seen + 2L)
            HangWaitMs
