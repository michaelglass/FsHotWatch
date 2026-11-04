/// `fshw status` and the verdict judge a Running plugin's wedge on the awake time the
/// daemon measured, never on the CLI's wall clock against the daemon's start stamp
/// (AUTOMATION-1489).
///
/// The wall clock counts the time the box slept. A CLI that subtracted the daemon's
/// wall-clock `since` from its own wall clock read a plugin still running after a laptop
/// sleep longer than the wedge bound as WEDGED, in `fshw status` and in `plugins[]` of
/// `.fshw/verdict.json`. The daemon now sends each Running plugin's age on its awake clock
/// (`awakeElapsedMs`), and the CLI judges the wedge from that. A reply with no such field
/// (an older daemon) falls back to the wall age, which can only be longer than the awake
/// age, so the fallback can call a running plugin wedged but never hide a wedge.
module FsHotWatch.Tests.StatusAwakeAgeTests

open System
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Cli
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.ProgressRenderer
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.FakeClock

let private bound = PluginWedge.ambientBound ()

let private stripAnsi (s: string) =
    Regex.Replace(s, "\x1b\\[[0-9;]*[A-Za-z]", "")

/// A status reply for one Running plugin, as the daemon serializes it. `awakeMs` is the
/// age the daemon measured on its awake clock; `None` is an older daemon that sends none.
let private runningReply (since: DateTime) (awakeMs: int64 option) =
    let awake =
        match awakeMs with
        | Some ms -> $""","awakeElapsedMs":%d{ms}"""
        | None -> ""

    $"""{{"build":{{"status":{{"tag":"running","since":"%s{since.ToString "O"}"%s{awake}}},"subtasks":[],"activityTail":[],"lastRun":null,"diagnostics":{{"errors":0,"warnings":0}}}}}}"""

let private outcomeAt (now: DateTime) (reply: string) =
    Verdict.pluginOutcomeOf true now (parseStatuses reply).["build"]

let private lineAt (mode: RenderMode) (now: DateTime) (reply: string) =
    renderPlugin mode true now "build" (parseStatuses reply).["build"]
    |> List.map stripAnsi
    |> String.concat "\n"

// --- the CLI judges the wedge on the daemon's awake age ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a plugin the daemon measured 90 s awake is running, not WEDGED, after a sleep longer than the bound`` () =
    let now = Origin
    // Started 90 s of awake time ago, with a sleep past the wedge bound in between.
    let since = now - bound - TimeSpan.FromHours 1.0 - TimeSpan.FromSeconds 90.0
    let reply = runningReply since (Some 90_000L)

    test <@ outcomeAt now reply = Some Verdict.PluginOutcome.Running @>
    test <@ lineAt Agent now reply = "build: running" @>
    test <@ not ((lineAt Compact now reply).Contains "WEDGED") @>
    test <@ not ((lineAt Verbose now reply).Contains "WEDGED") @>
    // The age shown is the awake age: the line is the one a plugin 90 s into a run with
    // no sleep gets.
    let unslept = runningReply (now - TimeSpan.FromSeconds 90.0) (Some 90_000L)
    test <@ lineAt Compact now reply = lineAt Compact now unslept @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a plugin the daemon measured past the bound is WEDGED, whatever the CLI's wall clock says`` () =
    let now = Origin
    // The wall clock says one minute; the daemon's awake clock says past the bound. The
    // daemon's measurement decides, so a genuinely wedged plugin is never waited out.
    let since = now - TimeSpan.FromMinutes 1.0
    let awake = bound + TimeSpan.FromMinutes 1.0
    let reply = runningReply since (Some(int64 awake.TotalMilliseconds))

    test <@ outcomeAt now reply = Some Verdict.PluginOutcome.Wedged @>
    let agent = lineAt Agent now reply
    test <@ agent.StartsWith "build: wedged" @>
    test <@ agent.Contains $"no completion in %s{PluginWedge.formatElapsed awake}" @>
    test <@ (lineAt Compact now reply).Contains "WEDGED" @>
    test <@ (lineAt Verbose now reply).Contains "WEDGED" @>

// --- an older daemon ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an older daemon's reply, with no awake age, is judged on the wall age, which never hides a wedge`` () =
    let now = Origin
    let wedged = runningReply (now - bound - TimeSpan.FromMinutes 1.0) None
    let running = runningReply (now - TimeSpan.FromMinutes 1.0) None

    test <@ outcomeAt now wedged = Some Verdict.PluginOutcome.Wedged @>
    test <@ (lineAt Compact now wedged).Contains "WEDGED" @>
    test <@ outcomeAt now running = Some Verdict.PluginOutcome.Running @>

// --- the daemon measures the age on its awake clock ---

/// A host on `clock`'s awake time, with one plugin, `build`, that reports `Running`
/// stamped on the clock's wall time each time a file changes.
let private hostOn (clock: ManualClock) =
    let host =
        PluginHost.PluginHost(Unchecked.defaultof<_>, "/tmp", awakeNow = HostClock.awakeUtcOn clock.Clock)

    host.RegisterHandler(
        { Name = PluginName.create "build"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = clock.WallNow()))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          PrepareCommit = None
          CacheKey = None
          Teardown = None }
    )

    host

/// Start a run of `build` and wait until the host holds its `Running`, stamped at the
/// clock's current wall time.
let private startRun (host: PluginHost.PluginHost) (clock: ManualClock) =
    let since = clock.WallNow()
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus "build" with
            | Some(Running s) -> s = since
            | _ -> false)
        HangWaitMs

/// The daemon's status reply for `build`, as the CLI parses it.
let private replyOf (host: PluginHost.PluginHost) =
    let reply =
        Ipc.DaemonRpcTarget(IpcTests.defaultRpcConfig host).GetPluginStatus "build"

    reply, (parseStatuses reply).["build"]

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the status reply carries a Running plugin's awake age, so a sleep does not age it`` () =
    let clock = ManualClock()
    let host = hostOn clock
    test <@ host.RunningAwakeAge "build" = None @>
    startRun host clock
    clock.Suspend(bound + TimeSpan.FromHours 1.0)
    clock.AdvanceSeconds 90.0

    let reply, parsed = replyOf host
    test <@ parsed.Status = StatusView.Running(Origin, Some(TimeSpan.FromSeconds 90.0)) @>
    test <@ outcomeAt (clock.WallNow()) reply = Some Verdict.PluginOutcome.Running @>
    test <@ lineAt Agent (clock.WallNow()) reply = "build: running" @>

    // A plugin that stays Running for the bound in awake time is WEDGED, sleep or not.
    clock.Advance bound
    let reply, _ = replyOf host
    test <@ outcomeAt (clock.WallNow()) reply = Some Verdict.PluginOutcome.Wedged @>
    test <@ (lineAt Compact (clock.WallNow()) reply).Contains "WEDGED" @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a new run's awake age starts again from zero`` () =
    let clock = ManualClock()
    let host = hostOn clock
    startRun host clock
    clock.Advance(TimeSpan.FromHours 1.0)
    test <@ host.RunningAwakeAge "build" = Some(TimeSpan.FromHours 1.0) @>
    startRun host clock
    test <@ host.RunningAwakeAge "build" = Some TimeSpan.Zero @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the run-once path carries the host's awake age too`` () =
    let clock = ManualClock()
    let host = hostOn clock
    startRun host clock
    clock.Suspend(bound + TimeSpan.FromHours 1.0)
    clock.AdvanceSeconds 90.0

    let parsed = (snapshotHost host (host.GetAllStatuses())).["build"]
    test <@ parsed.Status = StatusView.Running(Origin, Some(TimeSpan.FromSeconds 90.0)) @>
    test <@ Verdict.pluginOutcomeOf true (clock.WallNow()) parsed = Some Verdict.PluginOutcome.Running @>

// --- a reply that cannot be read ---

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("\"soon\"")>]
[<InlineData("-1")>]
[<InlineData("1.5")>]
let ``an awake age that cannot be read makes the status unreadable, never idle or young`` (awake: string) =
    let reply =
        $"""{{"build":{{"status":{{"tag":"running","since":"%s{Origin.ToString "O"}","awakeElapsedMs":%s{awake}}},"subtasks":[],"activityTail":[],"lastRun":null,"diagnostics":{{"errors":0,"warnings":0}}}}}}"""

    match (parseStatuses reply).["build"].Status with
    | StatusView.Unreadable reason -> test <@ reason.Contains "awakeElapsedMs" @>
    | other -> failwith $"expected Unreadable, got %A{other}"

    test <@ outcomeAt Origin reply = Some Verdict.PluginOutcome.Fail @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a null awake age is read as an older daemon's reply`` () =
    let since = Origin - bound - TimeSpan.FromMinutes 1.0

    let reply =
        $"""{{"build":{{"status":{{"tag":"running","since":"%s{since.ToString "O"}","awakeElapsedMs":null}},"subtasks":[],"activityTail":[],"lastRun":null,"diagnostics":{{"errors":0,"warnings":0}}}}}}"""

    test <@ (parseStatuses reply).["build"].Status = StatusView.Running(since, None) @>
    test <@ outcomeAt Origin reply = Some Verdict.PluginOutcome.Wedged @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an older daemon's start stamp in the CLI's future is aged zero, not negative`` () =
    test <@ StatusView.runningAge Origin (Origin + TimeSpan.FromMinutes 3.0) None = TimeSpan.Zero @>

// --- the host's stamps ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a Running reported again with the same since keeps its age, so re-reporting cannot hide a wedge`` () =
    let clock = ManualClock()
    let reports = ref 0

    let host =
        PluginHost.PluginHost(Unchecked.defaultof<_>, "/tmp", awakeNow = HostClock.awakeUtcOn clock.Clock)

    host.RegisterHandler(
        { Name = PluginName.create "build"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(Running(since = Origin))
                        System.Threading.Interlocked.Increment(&reports.contents) |> ignore
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          PrepareCommit = None
          CacheKey = None
          Teardown = None }
    )

    // Each report is awaited by the handler's own count, taken after the status is applied.
    let reportAndAwait () =
        let before = reports.Value
        host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
        waitUntil (fun () -> System.Threading.Volatile.Read(&reports.contents) > before) HangWaitMs

    reportAndAwait ()
    clock.Advance(TimeSpan.FromHours 1.0)
    reportAndAwait ()
    test <@ host.RunningAwakeAge "build" = Some(TimeSpan.FromHours 1.0) @>
    // A `since` that is not the plugin's current interval has no age.
    test <@ host.AwakeAgeOf("build", Origin + TimeSpan.FromDays 1.0) = None @>
