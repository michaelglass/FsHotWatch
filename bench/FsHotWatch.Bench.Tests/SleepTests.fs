module FsHotWatch.Bench.Tests.SleepTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

/// Clocks whose readings the test moves by hand. Mono ticks at 1,000/s.
type private FakeClocks() =
    member val Wall = DateTime(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc) with get, set
    member val Mono = 0L with get, set

    member this.Clocks: Sleep.Clocks =
        { Wall = fun () -> this.Wall
          Mono = fun () -> this.Mono
          Frequency = 1000L }

    /// Awake: both clocks advance together.
    member this.Run(seconds: float) =
        this.Wall <- this.Wall.AddSeconds seconds
        this.Mono <- this.Mono + int64 (seconds * 1000.0)

    /// Asleep: only the wall clock advances.
    member this.Sleep(seconds: float) =
        this.Wall <- this.Wall.AddSeconds seconds

[<Fact>]
let ``an awake window has no gap and no problem`` () =
    let c = FakeClocks()
    let w = Sleep.openWindow c.Clocks
    c.Run 900.0
    let gap = Sleep.gap c.Clocks w
    test <@ gap = TimeSpan.Zero @>
    test <@ Sleep.problem gap w [] = None @>

[<Fact>]
let ``a sleep inside the window shows as the wall-minus-monotonic gap and invalidates it`` () =
    let c = FakeClocks()
    let w = Sleep.openWindow c.Clocks
    c.Run 120.0
    c.Sleep 600.0
    c.Run 30.0
    let gap = Sleep.gap c.Clocks w
    test <@ gap = TimeSpan.FromSeconds 600.0 @>
    test <@ Sleep.problem gap w [] = Some "machine slept 600 s inside this record's window" @>

[<Fact>]
let ``clock noise under the tolerance is not sleep`` () =
    let c = FakeClocks()
    let w = Sleep.openWindow c.Clocks
    c.Run 300.0
    c.Sleep 1.5 // an NTP slew, not a sleep
    test <@ Sleep.problem (Sleep.gap c.Clocks w) w [] = None @>

[<Fact>]
let ``pmset Sleep, DarkWake and Wake lines parse; wake requests and details do not`` () =
    let log =
        String.concat
            "\n"
            [ "2026-09-23 09:32:12 +0200 Sleep               \tEntering Sleep state due to 'Maintenance Sleep'"
              "2026-09-23 09:32:13 +0200 Wake Requests       \t[process=mDNSResponder request=Maintenance]"
              "2026-09-23 09:32:14 +0200 DarkWake            \tDarkWake from Deep Idle [CDNP]"
              "2026-09-23 09:33:21 +0200 Wake                \tWake from Deep Idle [CDNVA] : due to lid"
              "2026-09-23 09:33:21 +0200 WakeDetails         \tDriverReason:smc.sysState.Wake"
              "garbage" ]

    let events = Sleep.parsePowerLog log
    test <@ events |> List.map _.Kind = [ "Sleep"; "DarkWake"; "Wake" ] @>
    test <@ events.Head.At = DateTimeOffset(2026, 9, 23, 9, 32, 12, TimeSpan.FromHours 2.0) @>

[<Fact>]
let ``the problem names only the power events inside the window`` () =
    let c = FakeClocks()
    c.Wall <- DateTime(2026, 9, 23, 7, 30, 0, DateTimeKind.Utc) // 09:30 +0200
    let w = Sleep.openWindow c.Clocks
    c.Sleep 200.0

    let events =
        Sleep.parsePowerLog (
            String.concat
                "\n"
                [ "2026-09-23 09:10:00 +0200 Sleep               \tbefore the window"
                  "2026-09-23 09:32:12 +0200 Sleep               \tinside"
                  "2026-09-23 09:32:14 +0200 DarkWake            \tinside" ]
        )

    test
        <@
            Sleep.problem (Sleep.gap c.Clocks w) w events = Some
                "machine slept 200 s inside this record's window (pmset: Sleep 09:32:12, DarkWake 09:32:14)"
        @>

[<Fact>]
let ``the real clocks agree while this test runs awake`` () =
    let w = Sleep.openWindow Sleep.system
    Threading.Thread.Sleep 50
    test <@ abs (Sleep.gap Sleep.system w).TotalSeconds < Sleep.Tolerance.TotalSeconds @>
