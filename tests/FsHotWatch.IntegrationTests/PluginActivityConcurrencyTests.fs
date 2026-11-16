module FsHotWatch.Tests.PluginActivityConcurrencyTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginActivity

// The concurrent tests of PluginActivity. They were moved here (2026-06-02) because the unit
// suite's coverage of PluginActivity.fs jittered run to run: the old `enforceGlobalCap` scanned
// every plugin for the oldest run and re-checked it under that plugin's lock, a branch only taken
// when another thread won the race. That never failed a test; it moved a coverage number. The
// AUTOMATION-1703 cap replaced it with eviction under one history lock and no re-check, so no
// branch is reachable only under contention any more (AUTOMATION-1738). Every branch has a
// deterministic single-threaded test in the unit suite.

[<Fact(Timeout = 15000)>]
let ``2 MB global cap evicts oldest history entries across plugins`` () =
    let s = State()
    let big = String('x', 10_000)
    let mutable total = 0

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

// AUTOMATION-1738: the activity cap under concurrent live recordings. Twelve plugins log past
// their share at once, set summaries and subtasks, and commit runs between rounds, with history
// absent and then present. The cap must settle (a regression to a spinning cap fails the bounded
// wait instead of hanging the suite), stay under 2 MB, and count exactly the bytes still held.

let private capBytes = 2 * 1024 * 1024
let private bytesOf (s: string) = s.Length * 2

let private heldInHistory (s: State) (plugins: string list) =
    plugins
    |> List.sumBy (fun p ->
        s.GetHistory p
        |> List.sumBy (fun r ->
            (r.ActivityTail |> List.sumBy bytesOf)
            + (r.Summary |> Option.map bytesOf |> Option.defaultValue 0)))

let private loggingPastTheCap (withHistory: bool) =
    let s = State()
    let at = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let plugins = [ for i in 1..12 -> $"p%02d{i}" ]

    if withHistory then
        for p in plugins do
            s.Log(p, String('h', 50_000))
            s.RecordTerminal(p, CompletedRun, at, at.AddMilliseconds 1.0)

    // 80_000 bytes a line: four lines pass a recording's 262_144-byte share, twelve recordings
    // hold far more than the cap between them.
    let line (p: string) (i: int) = $"%s{p}-%04d{i}" + String('x', 39_990)

    let work =
        [| for p in plugins ->
               Task.Run(fun () ->
                   for round in 1..6 do
                       s.StartSubtask(p, "k", $"round %d{round}")

                       for i in 1..12 do
                           s.Log(p, line p (round * 100 + i))

                       s.SetSummary(p, $"%s{p} round %d{round}")
                       s.EndSubtask(p, "k")

                       if round % 2 = 0 then
                           let now = at.AddSeconds(float round)
                           s.RecordTerminal(p, CompletedRun, now, now.AddMilliseconds 1.0)) |]

    let settled = Task.WhenAll(work).Wait(TimeSpan.FromSeconds 30.0)
    s, plugins, settled

[<Fact(Timeout = 60000)>]
let ``live recordings logging past their share concurrently settle under the cap, bytes exact`` () =
    for withHistory in [ false; true ] do
        let s, plugins, settled = loggingPastTheCap withHistory
        test <@ (withHistory, settled) = (withHistory, true) @>
        // Every worker committed its last round, so every byte still counted is in history.
        test <@ (withHistory, s.TotalByteSize <= capBytes) = (withHistory, true) @>
        test <@ (withHistory, s.TotalByteSize) = (withHistory, heldInHistory s plugins) @>
        test <@ (withHistory, plugins |> List.forall (fun p -> List.isEmpty (s.GetSubtasks p))) = (withHistory, true) @>
