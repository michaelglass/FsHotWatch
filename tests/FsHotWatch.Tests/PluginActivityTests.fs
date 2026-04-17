module FsHotWatch.Tests.PluginActivityTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginActivity

[<Fact(Timeout = 5000)>]
let ``StartSubtask then GetSubtasks returns the subtask`` () =
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    let tasks = s.GetSubtasks("p")
    test <@ tasks |> List.exists (fun t -> t.Key = "k1" && t.Label = "label 1") @>

[<Fact(Timeout = 5000)>]
let ``EndSubtask removes the subtask`` () =
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    s.EndSubtask("p", "k1")
    test <@ List.isEmpty (s.GetSubtasks("p")) @>

[<Fact(Timeout = 5000)>]
let ``Log appends in order to activity tail`` () =
    let s = State()
    s.Log("p", "one")
    s.Log("p", "two")
    s.Log("p", "three")
    test <@ s.GetActivityTail("p") = [ "one"; "two"; "three" ] @>

[<Fact(Timeout = 5000)>]
let ``Log ring buffer caps at 64 entries per plugin`` () =
    let s = State()

    for i in 1..100 do
        s.Log("p", sprintf "line-%d" i)

    let tail = s.GetActivityTail("p")
    test <@ tail.Length = 64 @>
    test <@ List.last tail = "line-100" @>
    test <@ List.head tail = "line-37" @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``RecordTerminal with SetSummary uses override`` () =
    let s = State()
    s.Log("p", "last line")
    s.SetSummary("p", "explicit")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = Some "explicit" @>

[<Fact(Timeout = 5000)>]
let ``Derived summary is last log line when no explicit summary and no subtasks`` () =
    let s = State()
    s.Log("p", "first")
    s.Log("p", "second")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = Some "second" @>

[<Fact(Timeout = 5000)>]
let ``Derived summary is longest-lived subtask label when no log and no override`` () =
    let s = State()
    s.StartSubtask("p", "k-old", "oldest subtask")
    System.Threading.Thread.Sleep(5)
    s.StartSubtask("p", "k-new", "newer subtask")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = Some "oldest subtask" @>

[<Fact(Timeout = 5000)>]
let ``RecordTerminal auto-ends open subtasks and clears run state`` () =
    let s = State()
    s.StartSubtask("p", "k1", "l1")
    s.Log("p", "msg")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    test <@ List.isEmpty (s.GetSubtasks("p")) @>
    test <@ List.isEmpty (s.GetActivityTail("p")) @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``2 MB global cap evicts oldest history entries across plugins`` () =
    let s = State()
    let big = String('x', 10_000)
    let mutable total = 0

    // Push enough history to exceed cap across two plugins
    for i in 1..200 do
        let plugin = if i % 2 = 0 then "a" else "b"
        s.Log(plugin, big)
        let now = DateTime.UtcNow.AddMilliseconds(float i)
        s.RecordTerminal(plugin, CompletedRun, now, now.AddMilliseconds(1.0))
        total <- total + 1

    test <@ s.TotalByteSize <= 2 * 1024 * 1024 @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``ResetRun on idle plugin is a no-op and does not touch history`` () =
    let s = State()
    let t1 = DateTime.UtcNow
    s.Log("p", "line")
    s.RecordTerminal("p", CompletedRun, t1, t1.AddMilliseconds(1.0))
    // Plugin is now Idle. ResetRun must not throw nor alter history.
    s.ResetRun("p")
    s.ResetRun("p")
    test <@ List.isEmpty (s.GetSubtasks("p")) @>
    test <@ List.isEmpty (s.GetActivityTail("p")) @>
    test <@ (s.GetHistory("p")).Length = 1 @>

[<Fact(Timeout = 5000)>]
let ``Activity after RecordTerminal starts a fresh recording`` () =
    let s = State()
    s.Log("p", "first")
    let t1 = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, t1, t1.AddMilliseconds(1.0))
    // New activity must not revive the prior run's tail.
    s.Log("p", "second")
    test <@ s.GetActivityTail("p") = [ "second" ] @>
    let t2 = t1.AddMilliseconds(10.0)
    s.RecordTerminal("p", CompletedRun, t2, t2.AddMilliseconds(1.0))
    let hist = s.GetHistory("p")
    test <@ (List.item 0 hist).ActivityTail = [ "first" ] @>
    test <@ (List.item 1 hist).ActivityTail = [ "second" ] @>

[<Fact(Timeout = 5000)>]
let ``Thread-safe under concurrent StartSubtask EndSubtask calls`` () =
    let s = State()

    let tasks =
        [| for i in 1..100 ->
               Task.Run(
                   System.Action(fun () ->
                       let k = sprintf "k%d" i
                       s.StartSubtask("p", k, sprintf "l%d" i)
                       s.EndSubtask("p", k))
               ) |]

    Task.WaitAll(tasks)
    test <@ List.isEmpty (s.GetSubtasks("p")) @>
