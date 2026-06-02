module FsHotWatch.Tests.PluginActivityConcurrencyTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginActivity

// Moved here from FsHotWatch.Tests/PluginActivityTests.fs. Both tests drive the
// flaky concurrency / cap-eviction paths in PluginActivity.fs (enforceGlobalCap,
// ~lines 112-138, and the concurrent lock branches), whose coverage jittered
// run-to-run under the concurrent-stress test. They still validate behavior
// here, just uncounted by the coverage metric. Deterministic single-threaded
// replacements for the non-racy cap-eviction path live in the unit suite.

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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
