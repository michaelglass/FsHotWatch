module FsHotWatch.Tests.PluginActivityTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginActivity

[<Fact>]
let ``StartSubtask then GetSubtasks returns the subtask`` () =
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    let tasks = s.GetSubtasks("p")
    test <@ tasks |> List.exists (fun t -> t.Key = "k1" && t.Label = "label 1") @>

[<Fact>]
let ``EndSubtask removes the subtask`` () =
    let s = State()
    s.StartSubtask("p", "k1", "label 1")
    s.EndSubtask("p", "k1")
    test <@ s.GetSubtasks("p") = [] @>

[<Fact>]
let ``Log appends in order to activity tail`` () =
    let s = State()
    s.Log("p", "one")
    s.Log("p", "two")
    s.Log("p", "three")
    test <@ s.GetActivityTail("p") = [ "one"; "two"; "three" ] @>

[<Fact>]
let ``Log ring buffer caps at 64 entries per plugin`` () =
    let s = State()

    for i in 1..100 do
        s.Log("p", sprintf "line-%d" i)

    let tail = s.GetActivityTail("p")
    test <@ tail.Length = 64 @>
    test <@ List.last tail = "line-100" @>
    test <@ List.head tail = "line-37" @>

[<Fact>]
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

[<Fact>]
let ``RecordTerminal with SetSummary uses override`` () =
    let s = State()
    s.Log("p", "last line")
    s.SetSummary("p", "explicit")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = Some "explicit" @>

[<Fact>]
let ``Derived summary is last log line when no explicit summary and no subtasks`` () =
    let s = State()
    s.Log("p", "first")
    s.Log("p", "second")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = Some "second" @>

[<Fact>]
let ``Derived summary is longest-lived subtask label when no log and no override`` () =
    let s = State()
    s.StartSubtask("p", "k-old", "oldest subtask")
    System.Threading.Thread.Sleep(5)
    s.StartSubtask("p", "k-new", "newer subtask")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    let r = List.head (s.GetHistory("p"))
    test <@ r.Summary = Some "oldest subtask" @>

[<Fact>]
let ``RecordTerminal auto-ends open subtasks and clears run state`` () =
    let s = State()
    s.StartSubtask("p", "k1", "l1")
    s.Log("p", "msg")
    let now = DateTime.UtcNow
    s.RecordTerminal("p", CompletedRun, now, now.AddMilliseconds(1.0))
    test <@ s.GetSubtasks("p") = [] @>
    test <@ s.GetActivityTail("p") = [] @>

[<Fact>]
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

[<Fact>]
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
    test <@ s.GetSubtasks("p") = [] @>
    test <@ s.GetActivityTail("p") = [] @>
    test <@ (s.GetHistory("p")).Length = 1 @>

[<Fact>]
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

[<Fact>]
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
    test <@ s.GetSubtasks("p") = [] @>
