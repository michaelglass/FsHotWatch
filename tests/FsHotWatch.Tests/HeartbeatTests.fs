/// The activity heartbeat: the daemon publishes `<repoRoot>/.fshw/heartbeat`
/// while — and ONLY while — a run is in progress.
///
/// The load-bearing property, and the reason the feature exists at all, is the
/// NEGATIVE one: an idle daemon must not beat. Process liveness cannot
/// distinguish "working" from "alive but wedged"; a beat that only happens
/// during a run can. Every other property here (cadence, contents, the leading
/// edge, failure containment) exists to keep that signal trustworthy.
module FsHotWatch.Tests.HeartbeatTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Heartbeat
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc)

/// A deps record over a mutable clock and a mutable run-active flag, capturing
/// every write. Mirrors `IdleExitTests.makeDeps`: ticks are driven by hand, so
/// nothing here waits on a real 15s cadence.
let private makeDeps () =
    let clock = ref t0
    let running = ref false
    let writes = ResizeArray<string>()
    let logs = ResizeArray<string>()

    let deps: HeartbeatDeps =
        { Now = fun () -> clock.Value
          RunActive = fun () -> running.Value
          Write = fun c -> lock writes (fun () -> writes.Add c)
          Log = fun m -> lock logs (fun () -> logs.Add m)
          Cadence = DefaultCadence }

    deps, clock, running, writes, logs

// ---------------------------------------------------------------------------
// The core property: beats while running, silent while idle
// ---------------------------------------------------------------------------

[<Fact>]
let ``an idle daemon never beats`` () =
    // THE property. An idle daemon holding a consumer's attention is precisely
    // the state that must be reclaimable, so it must produce no signal at all —
    // not a stale one, not a slow one, none.
    let deps, clock, running, writes, _ = makeDeps ()
    let lastBeat = ref None
    running.Value <- false

    for _ in 1..100 do
        test <@ runTick deps lastBeat = false @>
        clock.Value <- clock.Value.AddSeconds 30.0

    test <@ writes.Count = 0 @>

[<Fact>]
let ``a run in progress beats`` () =
    let deps, _, running, writes, _ = makeDeps ()
    let lastBeat = ref None
    running.Value <- true

    test <@ runTick deps lastBeat = true @>
    test <@ writes.Count = 1 @>

[<Fact>]
let ``the beat stops when the run ends and resumes when the next one starts`` () =
    // The idle/running edge in both directions, on one gate. Mirrors
    // IdleExitTests' "defers when busy then fires when free" shape.
    let deps, clock, running, writes, _ = makeDeps ()
    let lastBeat = ref None

    running.Value <- true
    test <@ runTick deps lastBeat = true @>

    running.Value <- false
    clock.Value <- clock.Value.AddMinutes 5.0
    test <@ runTick deps lastBeat = false @>
    clock.Value <- clock.Value.AddMinutes 5.0
    test <@ runTick deps lastBeat = false @>
    test <@ writes.Count = 1 @>

    running.Value <- true
    test <@ runTick deps lastBeat = true @>
    test <@ writes.Count = 2 @>

// ---------------------------------------------------------------------------
// Cadence and the leading edge
// ---------------------------------------------------------------------------

[<Fact>]
let ``beats at most once per cadence while a run continues`` () =
    let deps, clock, running, writes, _ = makeDeps ()
    let lastBeat = ref None
    running.Value <- true

    test <@ runTick deps lastBeat = true @> // leading edge

    // Ticks inside the cadence window write nothing.
    for _ in 1..14 do
        clock.Value <- clock.Value.AddSeconds 1.0
        test <@ runTick deps lastBeat = false @>

    test <@ writes.Count = 1 @>

    // The tick at exactly the cadence boundary writes.
    clock.Value <- clock.Value.AddSeconds 1.0
    test <@ runTick deps lastBeat = true @>
    test <@ writes.Count = 2 @>

[<Fact>]
let ``the first beat of a run is immediate, not a cadence late`` () =
    // A run's first tick must beat straight away. The file is never deleted, so
    // between runs it carries the PREVIOUS run's timestamp — arbitrarily old. A
    // first beat up to a cadence late would leave a consumer reading an ancient
    // timestamp while a run genuinely was executing, i.e. concluding "dead"
    // during real work. That is the dangerous direction.
    let deps, clock, running, writes, _ = makeDeps ()
    let lastBeat = ref None

    running.Value <- true
    test <@ runTick deps lastBeat = true @>

    // Run ends; hours pass idle.
    running.Value <- false
    test <@ runTick deps lastBeat = false @>
    clock.Value <- clock.Value.AddHours 3.0
    test <@ runTick deps lastBeat = false @>

    // Next run: the very first tick beats, with no cadence wait inherited.
    running.Value <- true
    let beforeCount = writes.Count
    test <@ runTick deps lastBeat = true @>
    test <@ writes.Count = beforeCount + 1 @>

[<Fact>]
let ``a long silent phase keeps beating`` () =
    // A browser suite can run for many minutes emitting nothing. The beat is
    // driven by "a run is in progress", never by log output, so twenty minutes
    // of silence still produces a beat every cadence.
    let deps, clock, running, writes, _ = makeDeps ()
    let lastBeat = ref None
    running.Value <- true

    // 20 minutes of one-second ticks, no other activity of any kind.
    for _ in 1 .. (20 * 60) do
        runTick deps lastBeat |> ignore
        clock.Value <- clock.Value.AddSeconds 1.0

    // 20 min at a 15s cadence => 80 beats (leading edge at t=0, then every 15s).
    test <@ writes.Count = 80 @>

// ---------------------------------------------------------------------------
// Contents contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``the beat is unix epoch seconds in decimal ascii`` () =
    let rendered = render t0
    let expected = DateTimeOffset(t0).ToUnixTimeSeconds()

    test <@ rendered = string expected @>
    test <@ rendered |> Seq.forall Char.IsAsciiDigit @>
    test <@ Int64.Parse rendered = expected @>

[<Fact>]
let ``each beat rewrites the current time`` () =
    let deps, clock, running, writes, _ = makeDeps ()
    let lastBeat = ref None
    running.Value <- true

    runTick deps lastBeat |> ignore
    clock.Value <- clock.Value.AddSeconds 15.0
    runTick deps lastBeat |> ignore

    test <@ writes.Count = 2 @>
    test <@ writes.[0] = render t0 @>
    test <@ writes.[1] = render (t0.AddSeconds 15.0) @>
    test <@ Int64.Parse writes.[1] - Int64.Parse writes.[0] = 15L @>

[<Fact>]
let ``a non-utc clock still renders utc epoch seconds`` () =
    // Epoch seconds are absolute; a Local-kind instant must not shift by the
    // machine's offset. Guards against a consumer on another timezone reading a
    // beat that looks hours stale (or hours in the future).
    let local = t0.ToLocalTime()
    test <@ render local = render t0 @>

// ---------------------------------------------------------------------------
// Path
// ---------------------------------------------------------------------------

[<Fact>]
let ``the heartbeat lives at .fshw/heartbeat under the repo root`` () =
    let p = path "/some/workspace"
    test <@ p = Path.Combine("/some/workspace", ".fshw", "heartbeat") @>
    test <@ Path.GetFileName p = "heartbeat" @>

[<Fact>]
let ``writeTo publishes readable epoch seconds at the contract path`` () =
    withTempDir "heartbeat" (fun root ->
        writeTo root (render t0)

        let p = path root
        test <@ File.Exists p @>
        test <@ Int64.Parse(File.ReadAllText p) = DateTimeOffset(t0).ToUnixTimeSeconds() @>

        // Rewritten in place, same path, no accumulation of temp files.
        writeTo root (render (t0.AddSeconds 15.0))
        test <@ Int64.Parse(File.ReadAllText p) = DateTimeOffset(t0.AddSeconds 15.0).ToUnixTimeSeconds() @>

        let leftovers =
            Directory.GetFiles(Path.GetDirectoryName p)
            |> Array.filter (fun f -> f.EndsWith ".tmp")

        test <@ Array.isEmpty leftovers @>)

// ---------------------------------------------------------------------------
// Failure containment
// ---------------------------------------------------------------------------

[<Fact>]
let ``a failing write is logged and swallowed, never thrown`` () =
    // A tick runs on a timer callback. An escaping exception would take the
    // daemon down over a cosmetic signal — and a missing beat already degrades
    // safely to "unknown" at the consumer, so there is nothing to escalate.
    let logs = ResizeArray<string>()

    let deps: HeartbeatDeps =
        { Now = fun () -> t0
          RunActive = fun () -> true
          Write = fun _ -> raise (IOException "disk full")
          Log = fun m -> lock logs (fun () -> logs.Add m)
          Cadence = DefaultCadence }

    let lastBeat = ref None

    test <@ runTick deps lastBeat = false @>
    test <@ logs |> Seq.exists (fun m -> m.Contains "[heartbeat]" && m.Contains "disk full") @>

[<Fact>]
let ``a failed beat does not consume the cadence slot`` () =
    // If a write throws, the gate must NOT advance — otherwise one transient
    // IO error silences the heartbeat for a whole cadence while a run is live.
    let writes = ResizeArray<string>()
    let fail = ref true

    let deps: HeartbeatDeps =
        { Now = fun () -> t0
          RunActive = fun () -> true
          Write =
            fun c ->
                if fail.Value then
                    raise (IOException "transient")
                else
                    lock writes (fun () -> writes.Add c)
          Log = ignore
          Cadence = DefaultCadence }

    let lastBeat = ref None

    test <@ runTick deps lastBeat = false @>
    fail.Value <- false
    // Same instant, no cadence elapsed — the retry must still be allowed.
    test <@ runTick deps lastBeat = true @>
    test <@ writes.Count = 1 @>

// ---------------------------------------------------------------------------
// runActive predicate
// ---------------------------------------------------------------------------

[<Fact>]
let ``runActive is true while any plugin has work in flight`` () =
    // The leg that spans long quiet phases: a plugin's inflight count is held
    // for the whole lifetime of an exclusive run.
    test <@ runActive true 0 = true @>

[<Fact>]
let ``runActive is true while a client waits for a verdict`` () =
    // Covers the instants where no plugin work is in flight but a `fshw check`
    // client is still connected and blocked.
    test <@ runActive false 1 = true @>
    test <@ runActive false 3 = true @>

[<Fact>]
let ``runActive is false only when nothing is in flight and nobody is waiting`` () =
    test <@ runActive false 0 = false @>

// ---------------------------------------------------------------------------
// Live timer wiring
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``the live timer beats to disk while running and stops when idle`` () =
    // End-to-end through a real timer and a real file, at a compressed tick so
    // the test does not wait on the production cadence.
    withTempDir "heartbeat-live" (fun root ->
        let running = ref true
        let logs = ResizeArray<string>()

        let deps: HeartbeatDeps =
            { Now = fun () -> DateTime.UtcNow
              RunActive = fun () -> running.Value
              Write = writeTo root
              Log = fun m -> lock logs (fun () -> logs.Add m)
              // Compressed cadence so several beats land inside the test.
              Cadence = TimeSpan.FromMilliseconds 50.0 }

        use _beat = createBeatWith (TimeSpan.FromMilliseconds 25.0) deps

        let p = path root
        waitUntil (fun () -> File.Exists p) 5000
        test <@ File.Exists p @>

        // Contents parse as epoch seconds near now.
        let beat = Int64.Parse(File.ReadAllText p)
        let now = DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds()
        test <@ abs (now - beat) < 30L @>

        // Go idle: the file stops advancing. mtime is the observable here.
        running.Value <- false
        System.Threading.Thread.Sleep 200
        let mtimeAfterIdle = File.GetLastWriteTimeUtc p
        System.Threading.Thread.Sleep 300
        test <@ File.GetLastWriteTimeUtc p = mtimeAfterIdle @>

        // Going idle must not delete the file — a stale timestamp is a stronger
        // signal for a consumer than absence, which only means "unknown".
        test <@ File.Exists p @>

        test <@ logs.Count = 0 @>)

[<Fact(Timeout = 15000)>]
let ``a tick that finds a beat in flight skips it`` () =
    // THE serialisation property, asserted DETERMINISTICALLY: no threads, no
    // sleeps, no timer. `guardedBeat` re-entered from inside its own beat is
    // exactly the situation a Timer creates when a beat outlasts the tick, and
    // the latch must refuse the second one.
    //
    // This exists because the original guard's only test drove two real timers
    // under load and asserted on observed concurrency. That test failed on a busy
    // box having never exercised the guard at all — the failure mode a regression
    // test must not have, because it gets re-run, seen to pass, and learned to be
    // ignored. Timing can still be smoke-tested (below); the PROPERTY is pinned here.
    let beating = ref 0
    let ran = ResizeArray<string>()
    let reentrantResult = ref (Some true)

    let outerRan =
        guardedBeat beating (fun () ->
            ran.Add "outer"
            // Re-enter while the outer beat is still in flight.
            reentrantResult.Value <- Some(guardedBeat beating (fun () -> ran.Add "inner")))

    test <@ outerRan @>
    // The re-entrant call must report that it did NOT beat...
    test <@ reentrantResult.Value = Some false @>
    // ...and must not have run the body at all.
    test <@ List.ofSeq ran = [ "outer" ] @>
    // The latch is released once the outer beat finishes, so the next tick beats.
    test <@ guardedBeat beating (fun () -> ran.Add "next") @>
    test <@ List.ofSeq ran = [ "outer"; "next" ] @>

[<Fact(Timeout = 15000)>]
let ``overlapping ticks never beat concurrently`` () =
    // REGRESSION. A `Timer` fires on its period regardless of whether the previous
    // callback finished, so a beat slower than the tick overlaps — and two beats at
    // once race on the single temp file inside the atomic write, the loser's rename
    // finding its source already consumed. That was observed live as a swallowed
    // `FileNotFoundException` on `heartbeat.tmp` during heavy runs. Here the write
    // is deliberately far slower than the tick, and the cadence is zero so every
    // tick is due — maximum overlap pressure. Beats must still be strictly serial.
    let concurrent = ref 0
    let maxSeen = ref 0
    let logs = ResizeArray<string>()

    let deps: HeartbeatDeps =
        { Now = fun () -> DateTime.UtcNow
          RunActive = fun () -> true
          Write =
            fun _ ->
                let n = System.Threading.Interlocked.Increment(&concurrent.contents)
                lock maxSeen (fun () -> maxSeen.Value <- max maxSeen.Value n)
                System.Threading.Thread.Sleep 120
                System.Threading.Interlocked.Decrement(&concurrent.contents) |> ignore
          Log = fun m -> lock logs (fun () -> logs.Add m)
          Cadence = TimeSpan.Zero }

    use _beat = createBeatWith (TimeSpan.FromMilliseconds 15.0) deps

    // Wait for EVIDENCE that beats are happening rather than assuming a fixed
    // wall-clock window produced some. `maxSeen = 1` conflates two claims: "beats
    // never overlap" (the invariant this test guards, maxSeen <= 1) and "at least
    // one beat occurred" (maxSeen >= 1), which is pure timing. On a loaded box the
    // timer's thread-pool callbacks can be starved for the whole sleep, leaving
    // maxSeen at 0 — and the test then reds having never exercised the invariant.
    // Observed live: this failed with maxSeen = 0 during a `confirm` run minutes
    // after passing in the same gate's test pass, on an identical tree.
    //
    // A guard that cannot tell "the invariant broke" from "I saw nothing" is the
    // exact confusion the heartbeat contract refuses elsewhere — absence means
    // UNKNOWN, never a verdict. So the two claims are asserted separately.
    let deadline = DateTime.UtcNow.AddSeconds 8.0

    while lock maxSeen (fun () -> maxSeen.Value) = 0 && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep 25

    // Nothing observed at all: the run proved nothing, and fails saying so rather
    // than masquerading as a detected overlap.
    test <@ lock maxSeen (fun () -> maxSeen.Value) >= 1 @>

    // Beats are flowing; now give overlap a real chance. Cadence is zero and the
    // write is 8x the tick, so this window is ~26 ticks of maximum overlap
    // pressure against a write that is still in flight.
    System.Threading.Thread.Sleep 400

    test <@ lock maxSeen (fun () -> maxSeen.Value) = 1 @>
    test <@ lock logs (fun () -> logs.Count) = 0 @>

[<Fact(Timeout = 15000)>]
let ``the live timer never writes for a daemon that is idle from the start`` () =
    withTempDir "heartbeat-idle" (fun root ->
        let deps: HeartbeatDeps =
            { Now = fun () -> DateTime.UtcNow
              RunActive = fun () -> false
              Write = writeTo root
              Log = ignore
              Cadence = TimeSpan.FromMilliseconds 50.0 }

        use _beat = createBeatWith (TimeSpan.FromMilliseconds 25.0) deps

        System.Threading.Thread.Sleep 400

        test <@ not (File.Exists(path root)) @>)
