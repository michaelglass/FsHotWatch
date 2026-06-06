module FsHotWatch.Tests.IdleTrimTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.IdleTrim

let private threshold = TimeSpan.FromMinutes(2.0)
let private baseTime = DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc)
let private activity0 = baseTime.Ticks

/// A trim sink that counts invocations.
let private counter () =
    let calls = ref 0
    let trim () = calls.Value <- calls.Value + 1
    calls, trim

[<Fact>]
let ``below idle threshold does not trim`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()

    // 1 minute idle < 2 minute threshold
    let fired = tick threshold (baseTime.AddMinutes(1.0)) false activity0 trim state

    test <@ fired = false @>
    test <@ calls.Value = 0 @>

[<Fact>]
let ``idle threshold reached with nothing running trims exactly once`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()

    // 3 minutes idle >= 2 minute threshold
    let fired = tick threshold (baseTime.AddMinutes(3.0)) false activity0 trim state

    test <@ fired = true @>
    test <@ calls.Value = 1 @>

[<Fact>]
let ``trim does not re-fire while idle continues`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()

    // First tick past threshold → trims
    tick threshold (baseTime.AddMinutes(3.0)) false activity0 trim state |> ignore
    // Subsequent ticks, same activity stamp (still idle) → must NOT re-trim
    let second = tick threshold (baseTime.AddMinutes(4.0)) false activity0 trim state
    let third = tick threshold (baseTime.AddMinutes(10.0)) false activity0 trim state

    test <@ second = false @>
    test <@ third = false @>
    test <@ calls.Value = 1 @>

[<Fact>]
let ``activity after trim re-arms the trimmer`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()

    // First idle window → trims
    tick threshold (baseTime.AddMinutes(3.0)) false activity0 trim state |> ignore
    test <@ calls.Value = 1 @>

    // A file event lands at t+4m → activity stamp advances
    let activity1 = baseTime.AddMinutes(4.0).Ticks

    // Not yet idle long enough after the new activity
    let tooSoon = tick threshold (baseTime.AddMinutes(5.0)) false activity1 trim state
    test <@ tooSoon = false @>
    test <@ calls.Value = 1 @>

    // Idle window satisfied again against the new stamp → trims a second time
    let again = tick threshold (baseTime.AddMinutes(7.0)) false activity1 trim state
    test <@ again = true @>
    test <@ calls.Value = 2 @>

[<Fact>]
let ``threshold reached but work running defers the trim`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()

    // Idle long enough, but busy=true → no trim this tick
    let whileBusy = tick threshold (baseTime.AddMinutes(3.0)) true activity0 trim state
    test <@ whileBusy = false @>
    test <@ calls.Value = 0 @>

    // Work finishes; still idle (same activity stamp) and threshold still
    // satisfied → now it trims.
    let afterBusy = tick threshold (baseTime.AddMinutes(4.0)) false activity0 trim state
    test <@ afterBusy = true @>
    test <@ calls.Value = 1 @>

[<Fact>]
let ``shouldTrim is a pure predicate and does not mutate`` () =
    let state = IdleTrimState.create ()

    // pure predicate: true when idle+not busy+not yet trimmed
    test <@ shouldTrim threshold (baseTime.AddMinutes(3.0)) false activity0 state = true @>
    // repeated calls keep returning true until tick latches
    test <@ shouldTrim threshold (baseTime.AddMinutes(3.0)) false activity0 state = true @>

[<Fact>]
let ``exactly at threshold boundary trims`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()

    let fired = tick threshold (baseTime.Add(threshold)) false activity0 trim state

    test <@ fired = true @>
    test <@ calls.Value = 1 @>

// --- runTick (live-callback wiring with injected effects) ---

/// Build IdleTrimDeps with injectable/recording effects.
let private makeDeps (now: DateTime) (busy: bool) (lastActivity: int64) (trim: unit -> unit) =
    let logs = ResizeArray<string * string>()

    let deps: IdleTrimDeps =
        { Threshold = threshold
          Now = fun () -> now
          Busy = fun () -> busy
          LastActivityTicks = fun () -> lastActivity
          Trim = trim
          Log = fun level msg -> logs.Add(level, msg) }

    deps, logs

[<Fact>]
let ``runTick fires trim and logs the release line when idle`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()
    let deps, logs = makeDeps (baseTime.AddMinutes(3.0)) false activity0 trim

    let fired = runTick deps 2 state

    test <@ fired = true @>
    test <@ calls.Value = 1 @>

    test
        <@
            logs
            |> Seq.exists (fun (lvl, m) -> lvl = "info" && m.Contains("released FCS root caches"))
        @>

[<Fact>]
let ``runTick does not fire or log a release when below threshold`` () =
    let state = IdleTrimState.create ()
    let calls, trim = counter ()
    let deps, logs = makeDeps (baseTime.AddMinutes(1.0)) false activity0 trim

    let fired = runTick deps 2 state

    test <@ fired = false @>
    test <@ calls.Value = 0 @>
    test <@ logs |> Seq.forall (fun (_, m) -> not (m.Contains("released FCS root caches"))) @>

[<Fact>]
let ``runTick swallows a throwing trim and logs an error`` () =
    let state = IdleTrimState.create ()
    let throwingTrim () = failwith "boom"
    let deps, logs = makeDeps (baseTime.AddMinutes(3.0)) false activity0 throwingTrim

    // Must not throw out of runTick.
    let fired = runTick deps 2 state

    // The decision fired (trim was attempted), and the failure was logged.
    test <@ fired = true @>

    test
        <@
            logs
            |> Seq.exists (fun (lvl, m) -> lvl = "error" && m.Contains("idle trim failed"))
        @>

[<Fact>]
let ``createTimer logs the enable line and is disposable`` () =
    let calls, trim = counter ()
    // Now far in the past relative to a fresh activity stamp → never trims in
    // this test; we only assert the enable log + clean disposal.
    let deps, logs = makeDeps DateTime.UtcNow false DateTime.UtcNow.Ticks trim

    use timer = createTimer deps 2

    test
        <@
            logs
            |> Seq.exists (fun (lvl, m) -> lvl = "info" && m.Contains("idle-trim enabled"))
        @>

    test <@ calls.Value = 0 @>
