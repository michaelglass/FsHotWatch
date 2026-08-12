module FsHotWatch.Tests.PluginActivityTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginActivity

[<Fact(Timeout = 15000)>]
let ``StartSubtask then GetSubtasks returns the subtask`` () =
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    let tasks = s.GetSubtasks("p")
    test <@ tasks |> List.exists (fun t -> t.Key = "k1" && t.Label = "label 1") @>

[<Fact(Timeout = 15000)>]
let ``EndSubtask removes the subtask`` () =
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    s.EndSubtask("p", "k1")
    test <@ List.isEmpty (s.GetSubtasks("p")) @>

[<Fact(Timeout = 15000)>]
let ``EndSubtask for an absent key while Recording is a no-op`` () =
    // Covers EndSubtask's absent-key arm. In production it is reached only via a
    // thread race — PluginCtxHelpers.withSubtask's finally-EndSubtask landing after
    // RecordTerminal/ResetRun already cleared the subtask — so driving it directly
    // makes the case deterministic.
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    s.EndSubtask("p", "k2")
    let tasks = s.GetSubtasks("p")
    test <@ tasks |> List.exists (fun t -> t.Key = "k1" && t.Label = "label 1") @>
    test <@ tasks.Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``Log appends in order to activity tail`` () =
    let s = State()
    s.Log("p", "one")
    s.Log("p", "two")
    s.Log("p", "three")
    test <@ s.GetActivityTail("p") = [ "one"; "two"; "three" ] @>

[<Fact(Timeout = 15000)>]
let ``Log ring buffer caps at 64 entries per plugin`` () =
    let s = State()

    for i in 1..100 do
        s.Log("p", sprintf "line-%d" i)

    let tail = s.GetActivityTail("p")
    test <@ tail.Length = 64 @>
    test <@ List.last tail = "line-100" @>
    test <@ List.head tail = "line-37" @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal captures subtasks and activity into history`` () =
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    s.Log("p", "hello")
    let started = DateTime.UtcNow
    let ended = started.AddMilliseconds(50.0)
    s.RecordTerminal("p", CompletedRun, started, ended)
    let hist = s.GetHistory("p")
    test <@ hist.Length = 1 @>
    let r = List.head hist
    test <@ r.StartedAt = started @>
    test <@ r.Elapsed = ended - started @>
    test <@ r.Outcome = CompletedRun @>
    test <@ r.ActivityTail = [ "hello" ] @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal with SetSummary uses override`` () =
    let s = State()
    s.Log("p", "last line")
    s.SetSummary("p", "explicit")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = Some "explicit" @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal does not derive summary from last log line`` () =
    let s = State()
    s.Log("p", "processing foo.fs")
    s.Log("p", "processing bar.fs")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = None @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal does not derive summary from subtask labels`` () =
    let s = State()
    s.StartSubtask("p", "k-old", "oldest subtask")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = None @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal auto-ends open subtasks and clears run state`` () =
    let s = State()
    s.StartSubtask("p", "k1", "l1")
    s.Log("p", "msg")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    test <@ List.isEmpty (s.GetSubtasks("p")) @>
    test <@ List.isEmpty (s.GetActivityTail("p")) @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal captures Failed outcome`` () =
    let s = State()
    let now = DateTime.UtcNow
    s.RecordTerminal("p", FailedRun "boom", now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))

    test
        <@
            match r.Outcome with
            | FailedRun e -> e = "boom"
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal accepts TimedOut outcome`` () =
    let s = State()
    let started = DateTime.UtcNow
    s.RecordTerminal("p", TimedOut "exceeded 60s", started, started.AddSeconds 60.0)
    let snap = s.GetSnapshot("p")

    test
        <@
            match snap.LastRun.Value.Outcome with
            | TimedOut msg -> msg = "exceeded 60s"
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``ResetRun clears current subtasks, activity, summary override but keeps history`` () =
    let s = State()
    let now = DateTime.UtcNow
    s.Log("p", "from prior run")
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    // new run
    s.StartSubtask("p", "k", "label")
    s.Log("p", "midway")
    s.SetSummary("p", "override")
    s.ResetRun("p")
    test <@ List.isEmpty (s.GetSubtasks("p")) @>
    test <@ List.isEmpty (s.GetActivityTail("p")) @>
    test <@ (s.GetHistory("p")).Length = 1 @>

// Single-threaded so enforceGlobalCap's eviction path runs every time: entries of
// KNOWN size are pushed until the 2 MB cap is exceeded. The concurrent-stress
// variant lives in FsHotWatch.IntegrationTests, where cross-lock recheck races
// make it flaky.
[<Fact(Timeout = 15000)>]
let ``global cap evicts oldest history entries first (single-threaded, deterministic)`` () =
    let s = State()
    // ~80 KB per record (40_000-char string * 2 bytes/char). 2 MB / 80 KB ≈ 26
    // records, so 80 records forces many evictions deterministically.
    let big = String('x', 40_000)
    let baseTime = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    for i in 1..80 do
        s.Log("p", big)
        let started = baseTime.AddSeconds(float i)
        s.RecordTerminal("p", CompletedRun, started, started.AddMilliseconds(1.0))

    test <@ s.TotalByteSize <= 2 * 1024 * 1024 @>

    let hist = s.GetHistory("p")
    let histLen = hist.Length
    test <@ histLen > 0 && histLen < 80 @>

    // The oldest survivor's StartedAt is strictly later than the first pushed
    // record, which is what proves eviction happened oldest-first.
    let firstPushed = baseTime.AddSeconds(1.0)
    let lastPushed = baseTime.AddSeconds(80.0)
    let oldestSurvivor = (List.head hist).StartedAt
    let newestSurvivor = (List.last hist).StartedAt
    let evictedOldestFirst = oldestSurvivor > firstPushed
    test <@ evictedOldestFirst @>

    // Ascending StartedAt confirms FIFO eviction left a contiguous newest window
    // ending at the last pushed record.
    let starts = hist |> List.map (fun r -> r.StartedAt)
    let ascending = starts = List.sort starts
    test <@ ascending @>
    test <@ newestSurvivor = lastPushed @>

[<Fact(Timeout = 15000)>]
let ``SetNextTerminalOutcome overrides the outcome of the next RecordTerminal`` () =
    let s = State()
    s.SetNextTerminalOutcome("p", TimedOut "forced")
    let now = DateTime.UtcNow
    // Pass CompletedRun, but the override must win.
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))

    test
        <@
            match r.Outcome with
            | TimedOut msg -> msg = "forced"
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``RecordTerminal twice does not leak state from first run into second`` () =
    let s = State()
    s.StartSubtask("p", "k", "in-flight")
    s.Log("p", "first-run line")
    let t1 = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, t1, t1.AddMilliseconds(1.0))
    // Second RecordTerminal with no new activity — phase is Idle, so the
    // second record must be clean (no stale subtasks / log / summary).
    let t2 = t1.AddMilliseconds(10.0)
    s.RecordTerminal("p", CompletedRun, t2, t2.AddMilliseconds(1.0))
    let hist = s.GetHistory("p")
    test <@ hist.Length = 2 @>
    test <@ (List.item 0 hist).ActivityTail = [ "first-run line" ] @>
    test <@ List.isEmpty (List.item 1 hist).ActivityTail @>
    test <@ (List.item 1 hist).Summary = None @>

[<Fact(Timeout = 15000)>]
let ``ResetRun on idle plugin is a no-op and does not touch history`` () =
    let s = State()
    let t1 = DateTime.UtcNow
    s.Log("p", "line")
    s.RecordTerminal("p", CompletedRun, t1, t1.AddMilliseconds(1.0))
    s.ResetRun("p")
    s.ResetRun("p")
    test <@ List.isEmpty (s.GetSubtasks("p")) @>
    test <@ List.isEmpty (s.GetActivityTail("p")) @>
    test <@ (s.GetHistory("p")).Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``Activity after RecordTerminal starts a fresh recording`` () =
    let s = State()
    s.Log("p", "first")
    let t1 = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, t1, t1.AddMilliseconds(1.0))
    s.Log("p", "second")
    test <@ s.GetActivityTail("p") = [ "second" ] @>
    let t2 = t1.AddMilliseconds(10.0)
    s.RecordTerminal("p", CompletedRun, t2, t2.AddMilliseconds(1.0))
    let hist = s.GetHistory("p")
    test <@ (List.item 0 hist).ActivityTail = [ "first" ] @>
    test <@ (List.item 1 hist).ActivityTail = [ "second" ] @>

[<Fact(Timeout = 15000)>]
let ``UpdateSubtask replaces label without changing StartedAt`` () =
    let s = State()
    s.StartSubtask("p", "primary", "v1")
    let t1 = s.GetSubtasks("p") |> List.exactlyOne
    System.Threading.Thread.Sleep(5)
    s.UpdateSubtask("p", "primary", "v2")
    let t2 = s.GetSubtasks("p") |> List.exactlyOne
    test <@ t2.Label = "v2" @>
    test <@ t1.StartedAt = t2.StartedAt @>

[<Fact(Timeout = 15000)>]
let ``UpdateSubtask is a no-op when key not present`` () =
    let s = State()
    s.UpdateSubtask("p", "missing", "label")
    test <@ List.isEmpty (s.GetSubtasks("p")) @>

// The concurrent-stress variant (100 Task.Run workers) lives in
// FsHotWatch.IntegrationTests/PluginActivityConcurrencyTests.fs: its cross-lock
// branch hits fire flakily under load, which made PluginActivity.fs line coverage
// jitter here.

[<Fact(Timeout = 15000)>]
let ``StartSubtask then EndSubtask sequentially leaves no open subtasks`` () =
    // Single-threaded analogue of that moved concurrency test.
    let s = State()

    for i in 1..100 do
        let k = sprintf "k%d" i
        s.StartSubtask("p", k, sprintf "l%d" i)
        s.EndSubtask("p", k)

    test <@ List.isEmpty (s.GetSubtasks("p")) @>
