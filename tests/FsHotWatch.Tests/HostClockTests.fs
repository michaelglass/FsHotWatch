module FsHotWatch.Tests.HostClockTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.HostClock

let private t0 = DateTime(2026, 10, 5, 15, 30, 0, DateTimeKind.Utc)

let private at (wallSec: float) (awakeSec: float) : Reading =
    { Wall = t0.AddSeconds wallSec
      Awake = TimeSpan.FromSeconds awakeSec }

[<Fact(Timeout = 5000)>]
let ``suspendedBetween is the wall time that passed beyond the awake time`` () =
    // The b491 gate: 900s of wall clock, 339s of it awake.
    test <@ suspendedBetween (at 0.0 100.0) (at 900.0 439.0) = TimeSpan.FromSeconds 561.0 @>

[<Fact(Timeout = 5000)>]
let ``suspendedBetween reads a wall clock stepped backwards as no suspension`` () =
    test <@ suspendedBetween (at 0.0 0.0) (at -30.0 10.0) = TimeSpan.Zero @>

[<Fact(Timeout = 5000)>]
let ``suspensionLine names the suspension and both clocks`` () =
    test
        <@
            suspensionLine "during this run" (at 0.0 100.0) (at 900.0 439.0) = Some
                "host was suspended ~561s during this run (wall clock advanced 900s, awake 339s)"
        @>

[<Fact(Timeout = 5000)>]
let ``suspensionLine stays quiet below the floor and reports at it`` () =
    test <@ suspensionLine "x" (at 0.0 0.0) (at 14.9 10.0) = None @>
    test <@ suspensionLine "x" (at 0.0 0.0) (at 15.0 10.0) |> Option.isSome @>

[<Fact(Timeout = 5000)>]
let ``the system clock reads both clocks moving forward`` () =
    let first = read system
    let second = read system
    let awake1, awake2 = first.Awake, second.Awake
    let wall1, wall2 = first.Wall, second.Wall
    test <@ awake2 >= awake1 && awake1 > TimeSpan.Zero @>
    let kind = wall1.Kind
    test <@ wall2 >= wall1 && kind = DateTimeKind.Utc @>
