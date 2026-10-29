module FsHotWatch.Tests.SettleHeartbeatTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli

// AUTOMATION-1370. A land gate on a loaded box spent its whole 150-minute timeout inside the
// settle — three test-prune runs that starved, re-ran alone and were queued again — and its
// log said "Scanning..." from start to kill, because a non-interactive settle printed
// nothing. Everything here runs on a fake clock: the wait is hours long and takes no time.

let private t0 = DateTime(2026, 10, 29, 2, 50, 47, DateTimeKind.Utc)

let private iso (at: DateTime) = at.ToString("o")

/// A status map with test-prune Running since `since`, on the given subtasks.
let private testPruneRunning (since: DateTime) (subtasks: (string * DateTime) list) : string =
    let subtaskJson =
        subtasks
        |> List.map (fun (key, startedAt) ->
            $"""{{"key":"%s{key}","label":"%s{key}","startedAt":"%s{iso startedAt}"}}""")
        |> String.concat ","

    $"""{{"build":{{"status":{{"tag":"completed","at":"%s{iso t0}"}},"subtasks":[],"activityTail":[],"lastRun":null}},"test-prune":{{"status":{{"tag":"running","since":"%s{iso since}"}},"subtasks":[%s{subtaskJson}],"activityTail":[],"lastRun":null}}}}"""

let private nothingRunning =
    $"""{{"build":{{"status":{{"tag":"completed","at":"%s{iso t0}"}},"subtasks":[],"activityTail":[],"lastRun":null}},"test-prune":{{"status":{{"tag":"idle"}},"subtasks":[],"activityTail":[],"lastRun":null}}}}"""

/// Drive `pollUntilSettledWith` on a fake clock that each pause advances by `step`, until
/// `polls` readings have been taken. Returns every line it reported.
let private settleFor (interactive: bool) (step: TimeSpan) (polls: int) (status: DateTime -> string) =
    let mutable clock = t0
    let mutable readings = 0
    let reported = ResizeArray<string>()

    IpcOutput.pollUntilSettledWith
        (fun () -> clock)
        (fun () -> clock <- clock + step)
        reported.Add
        interactive
        (fun _ -> [])
        (fun () ->
            readings <- readings + 1
            status clock)
        (fun () -> readings >= polls)

    List.ofSeq reported

/// The progress reports, without the label that opens every non-interactive settle.
let private progress (reported: string list) =
    match reported with
    | label :: rest when label = SettleHeartbeat.settlingLabel -> rest
    | other -> other

[<Fact>]
let ``a non-interactive settle reports what it waits on while test runs go on for hours`` () =
    let runStarted = t0 + TimeSpan.FromMinutes 3.0
    let integration = t0 + TimeSpan.FromMinutes 83.0
    // 140 minutes of readings, 10 s apart — the daemon's verdict bound on entry 234.
    let polls = int (TimeSpan.FromMinutes(140.0).TotalSeconds / 10.0)

    let lines =
        settleFor false (TimeSpan.FromSeconds 10.0) polls (fun _ ->
            testPruneRunning runStarted [ "primary", runStarted; "Intelligence.Tests.Integration", integration ])

    // The phase is named as it begins, so the log no longer ends on the scan wait's label.
    test <@ List.head lines = SettleHeartbeat.settlingLabel @>
    let reported = progress lines

    // Never silent for longer than the interval: one report per 30 s of a 140-min wait.
    test <@ reported.Length >= 270 @>

    test
        <@
            reported
            |> List.forall (fun line -> line.Contains "still running: test-prune (")
        @>

    test
        <@
            reported
            |> List.forall (fun line -> line.Contains "Intelligence.Tests.Integration")
        @>

    test <@ reported |> List.forall (fun line -> not (line.Contains "Scanning")) @>
    // The elapsed time moves, so a reader can tell a long run from a stuck report.
    test <@ (List.last reported).StartsWith("settling 2h 19m", StringComparison.Ordinal) @>

[<Fact>]
let ``a settle with nothing Running says the daemon still owns work rather than nothing`` () =
    let reported =
        settleFor false (TimeSpan.FromSeconds 10.0) 10 (fun _ -> nothingRunning)
        |> progress

    test <@ not reported.IsEmpty @>
    test <@ reported |> List.forall (fun line -> line.Contains "no plugin is running") @>

[<Fact>]
let ``a settle that resolves inside the interval reports only its label`` () =
    let reported =
        settleFor false (TimeSpan.FromSeconds 1.0) 5 (fun at -> testPruneRunning at [])

    test <@ reported = [ SettleHeartbeat.settlingLabel ] @>

[<Fact>]
let ``an interactive settle leaves reporting to the status redraw`` () =
    let reported =
        settleFor true (TimeSpan.FromSeconds 10.0) 20 (fun at -> testPruneRunning at [])

    test <@ reported.IsEmpty @>

[<Fact>]
let ``the progress line names each running plugin in the daemon's wait form`` () =
    let now = t0 + TimeSpan.FromMinutes 75.0

    let statuses =
        IpcParsing.parsePluginStatuses (testPruneRunning t0 [ "primary", t0 ])
        |> Result.defaultValue Map.empty

    test
        <@
            SettleHeartbeat.progressLine now (TimeSpan.FromMinutes 75.0) statuses = "settling 1h 15m: still running: test-prune (1h 15m) [primary 1h 15m]"
        @>

[<Fact>]
let ``the report interval keeps a reader from waiting more than half a minute`` () =
    test <@ SettleHeartbeat.interval <= TimeSpan.FromSeconds 30.0 @>
