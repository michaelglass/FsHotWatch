/// No product wait measures its deadline on the wall clock (AUTOMATION-1488).
///
/// The wall clock counts the time the box slept; the awake clock (`HostClock.AwakeNow`,
/// a `Stopwatch`) does not. A product wait whose deadline was a wall-clock time had run out
/// by the first read after a sleep: `check` timed out, `stop` gave up on a daemon, a
/// running plugin was declared wedged and the daemon restarted, an idle daemon exited.
/// Every deadline a product wait holds is now taken on `HostClock.awakeUtc` (or the awake
/// clock directly), and these tests move a `ManualClock` by hand: `Suspend` moves the wall
/// clock alone, as a sleep does.
module FsHotWatch.Tests.ProductDeadlineTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.FakeClock
open FsHotWatch.Tests.RepoTasks

let private aDay = TimeSpan.FromDays 1.0

// --- the clock ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the awake clock as a DateTime moves with awake time and not with a sleep`` () =
    let clock = ManualClock()
    let now = HostClock.awakeUtcOn clock.Clock
    let start = now ()
    clock.Suspend aDay
    test <@ now () = start @>
    clock.AdvanceSeconds 3.0
    test <@ now () = start + TimeSpan.FromSeconds 3.0 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``pollWhile gives up on awake time only`` () =
    // Each pause the box sleeps for a day; only on the sixth does a second of awake time
    // pass, the whole bound. A poll on the wall clock would give up at the first pause.
    let clock = ManualClock()
    let mutable pauses = 0

    let pause () =
        pauses <- pauses + 1

        if pauses < 6 then
            clock.Suspend aDay
        else
            clock.AdvanceSeconds 1.0

    let stillHolds =
        HostClock.pollWhile (HostClock.awakeUtcOn clock.Clock) pause (TimeSpan.FromSeconds 1.0) (fun () -> true)

    test <@ stillHolds && pauses = 6 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``pollWhile stops when the condition stops holding`` () =
    let clock = ManualClock()
    let mutable reads = 0

    let holds () =
        reads <- reads + 1
        reads < 3

    test <@ not (HostClock.pollWhile (HostClock.awakeUtcOn clock.Clock) ignore aDay holds) @>

// --- the daemon's waits ---

/// A plugin whose handler blocks until released: the host stays busy, so a wait on it
/// ends only by its timeout.
let private stuck (name: string) (entered: ManualResetEventSlim) (release: ManualResetEventSlim) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun _ctx state _event ->
            async {
                entered.Set()
                release.Wait()
                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the settling wait's timeout is awake time, so a sleep does not end it`` () =
    use release = new ManualResetEventSlim(false)
    use entered = new ManualResetEventSlim(false)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        host.RegisterHandler(stuck "slow-plugin" entered release)
        host.EmitBuildCompleted(BuildSucceeded)
        test <@ entered.Wait(HangWait) @>

        let clock = ManualClock()

        // A one-millisecond timeout, which a wall-clock deadline spends before the wait's first
        // read, and a stall threshold no test reaches.
        let wait =
            Daemon.waitForAllTerminalCoreOn
                (HostClock.awakeUtcOn clock.Clock)
                host
                (TimeSpan.FromMilliseconds 1.0)
                aDay
                CancellationToken.None

        clock.Suspend aDay
        // The wait has read the clock after the sleep, and is still waiting.
        clock.AwaitSamples wait
        clock.AwaitSamples wait
        test <@ not wait.IsCompleted @>

        clock.AdvanceSeconds 2.0

        let ex = Assert.Throws<TimeoutException>(fun () -> wait.GetAwaiter().GetResult())

        test <@ ex.Message.Contains "timed out" @>
    finally
        release.Set()

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a wait for a plugin to leave Running spends awake time only`` () =
    let clock = ManualClock()

    let running (_: string) =
        Some(Running(DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)))

    let wait =
        Daemon.waitForPluginTerminalIfRunningOn
            (HostClock.awakeUtcOn clock.Clock)
            running
            "build"
            (TimeSpan.FromSeconds 1.0)
        |> Async.StartAsTask

    clock.Suspend aDay
    clock.AwaitSamples wait
    clock.AwaitSamples wait
    test <@ not wait.IsCompleted @>

    clock.AdvanceSeconds 2.0
    test <@ wait.Wait(HangWait) @>

// --- the wedge monitor's start stamps ---

let private wall = Origin

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an anchored start stamp ages with awake time and not with a sleep`` () =
    let clock = ManualClock()
    let now = HostClock.awakeUtcOn clock.Clock

    let anchors =
        PluginWedge.AwakeAnchors(clock.Clock, now, fun () -> [ "build", wall ])

    let age () =
        match anchors.Read() with
        | [ "build", since ] -> now () - since
        | other -> failwith $"%A{other}"

    test <@ age () = TimeSpan.Zero @>
    clock.Suspend aDay
    test <@ age () = TimeSpan.Zero @>
    clock.AdvanceSeconds 90.0
    test <@ age () = TimeSpan.FromSeconds 90.0 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a stamp first seen after a sleep is given no more than the awake time since the last read`` () =
    let before: HostClock.Reading = { Wall = wall; Awake = TimeSpan.Zero }
    // A day of sleep and 30 s awake since the previous read; the stamp is 6 hours old by
    // the wall clock, which the awake clock cannot have seen.
    let after: HostClock.Reading =
        { Wall = wall + aDay + TimeSpan.FromSeconds 30.0
          Awake = TimeSpan.FromSeconds 30.0 }

    let awakeNow = wall + TimeSpan.FromSeconds 30.0
    let stamp = after.Wall - TimeSpan.FromHours 6.0

    let read, anchored =
        PluginWedge.anchorStamps before after awakeNow Map.empty [ "tests", stamp ]

    test <@ read = [ "tests", awakeNow - TimeSpan.FromSeconds 30.0 ] @>
    // Anchored once: the next read keeps it, and a stamp no longer listed is forgotten.
    let again, kept =
        PluginWedge.anchorStamps after after awakeNow anchored [ "tests", stamp ]

    test <@ again = read @>
    test <@ (PluginWedge.anchorStamps after after awakeNow kept [] |> snd) = Map.empty @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a plugin that ran through a sleep is not declared wedged`` () =
    let clock = ManualClock()
    let now = HostClock.awakeUtcOn clock.Clock

    let anchors =
        PluginWedge.AwakeAnchors(clock.Clock, now, fun () -> [ "build", clock.WallNow() ])

    let bound = TimeSpan.FromMinutes 60.0

    let tick () =
        PluginWedge.decideTick
            bound
            bound
            (TimeSpan.FromMinutes 5.0)
            { Now = now ()
              RunningPlugins = anchors.Read()
              QueuedResults = []
              AnyBusy = true
              LastActivityAt = now () }
            Map.empty
        |> fst

    test <@ tick () |> List.isEmpty @>
    clock.Suspend aDay

    test
        <@
            tick ()
            |> List.forall (function
                | PluginWedge.TickAction.DeclareWedged _ -> false
                | _ -> true)
        @>

// --- the operation watchdog ---

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an operation in flight across a sleep has not overrun`` () =
    let clock = ManualClock()

    use watchdog =
        new OperationWatchdog.Watchdog(
            TimeSpan.FromMinutes 1.0,
            heartbeatEvery = aDay,
            now = clock.WallNow,
            log = ignore,
            tick = aDay,
            awake = clock.AwakeNow
        )

    watchdog.Begin "scan" |> ignore
    clock.Suspend aDay
    test <@ watchdog.WedgeReport() = None @>
    clock.Advance(TimeSpan.FromMinutes 2.0)
    test <@ (watchdog.WedgeReport()).IsSome @>

// --- the source: no wall-clock deadline in src/ ---

/// A wall-clock read that a deadline is built from or tested against: `UtcNow + bound`,
/// `UtcNow.AddSeconds …`, or `UtcNow < deadline` (a `->` is not a comparison).
let private wallClockDeadline =
    Regex(
        @"\bDateTime(Offset)?\.(Utc)?Now(\s*(<=|>=|<|>)(?!-)|\s*\+|\.Add(Milliseconds|Seconds|Minutes|Hours|Ticks)\b|\.Add\s*\()"
    )

/// A wall clock handed over as a clock (`fun () -> DateTime.UtcNow`), which a seam may
/// measure a deadline on.
let private wallClockSeam =
    Regex(@"fun\s*\(\)\s*->\s*(System\.)?DateTime(Offset)?\.(Utc)?Now\s*(\)|,|$)")

/// Every wall clock handed to a seam in `src/`, by file, and why that seam may have one.
/// None of them measures how long to wait: each is a time of day, a stamp compared with
/// files or persisted, or the wall half of a suspension measurement.
let private allowedWallSeams =
    Map
        [ "src/FsHotWatch/HostClock.fs", (1, "the wall half of `HostClock.system`")
          "src/FsHotWatch/Ipc.fs", (1, "the watchdog's wall clock, which measures suspensions against its awake clock")
          "src/FsHotWatch/RepositoryIpc.fs", (1, "the watchdog's wall clock, as in Ipc.fs")
          "src/FsHotWatch/RepositoryHost.fs", (2, "the host event log's line timestamps")
          "src/FsHotWatch/CheckCache.fs", (1, "the racy-mtime window, compared with file times")
          "src/FsHotWatch/OptionsRecords.fs", (1, "waiting past a file-time racy window, compared with file times")
          "src/FsHotWatch/TestEvidenceStore.fs", (1, "the evidence TTL, persisted across processes")
          "src/FsHotWatch/Daemon.fs", (1, "the heartbeat file's epoch seconds, read by other processes")
          "src/FsHotWatch/CheckPipeline.fs",
          (1, "the self-incompatible recheck cooldown: a rate limit whose spend is logged as a time of day") ]

let private sourcesUnder (root: string) =
    filesUnder SafeWalk.SourceExcludedDirs "*.fs" (Path.Combine(root, "src"))
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> not (relative.Contains "/obj/" || relative.Contains "/bin/"))
    |> Seq.map (fun (relative, path) -> relative, File.ReadAllLines path)
    |> List.ofSeq

let private code (line: string) = not (line.TrimStart().StartsWith "//")

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``no product deadline is built from or tested against the wall clock`` () =
    let found =
        [ for relative, lines in sourcesUnder (repoRoot ()) do
              for i in 0 .. lines.Length - 1 do
                  if code lines[i] && wallClockDeadline.IsMatch lines[i] then
                      yield $"%s{relative}:%d{i + 1}: %s{lines[i].Trim()}" ]

    if not found.IsEmpty then
        Assert.Fail(
            "These deadlines are wall-clock times, which a sleep of the box spends: take them on \
             HostClock.awakeUtc (or HostClock.pollWhile):\n"
            + String.Join("\n", found)
        )

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``every wall clock handed to a seam in src is one that measures no wait`` () =
    let counts =
        [ for relative, lines in sourcesUnder (repoRoot ()) do
              let n =
                  lines
                  |> Array.filter (fun l -> code l && wallClockSeam.IsMatch l)
                  |> Array.length

              if n > 0 then
                  yield relative, n ]
        |> Map.ofList

    let allowed = allowedWallSeams |> Map.map (fun _ (n, _) -> n)

    if counts <> allowed then
        Assert.Fail(
            $"Wall clocks handed to seams in src/ changed. Found %A{counts}, allowed %A{allowed}. A seam that \
              measures a wait takes HostClock.awakeUtc; one that is truly a time of day goes in allowedWallSeams \
              with why."
        )

// --- the CLI: no Running plugin aged on the CLI's own wall clock (AUTOMATION-1489) ---

/// The age of a Running plugin taken as the CLI's wall clock minus the daemon's
/// wall-clock start stamp (`now - since`, `now > since`). After a sleep of the box that
/// age is the sleep, and a running plugin read as WEDGED in `fshw status` and the verdict.
let private wallAgeOfRunning =
    Regex(@"\b\w*[Nn]ow\b(\s*\(\s*\))?\s*(-|>=|>)\s*since\b")

/// The one place in the CLI that may still take that age, and why.
let private allowedWallAges =
    Map
        [ "src/FsHotWatch.Cli/RunOnceOutput.fs",
          (1,
           "an older daemon's reply carries no awake age; its wall age is never shorter than the awake age, so it can call a running plugin wedged but never hide a wedge") ]

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``no CLI surface ages a Running plugin on its own wall clock`` () =
    let counts =
        [ for relative, lines in sourcesUnder (repoRoot ()) do
              if relative.StartsWith "src/FsHotWatch.Cli/" then
                  let n =
                      lines
                      |> Array.filter (fun l -> code l && wallAgeOfRunning.IsMatch l)
                      |> Array.length

                  if n > 0 then
                      yield relative, n ]
        |> Map.ofList

    let allowed = allowedWallAges |> Map.map (fun _ (n, _) -> n)

    if counts <> allowed then
        Assert.Fail(
            $"Wall-clock ages of a Running plugin in the CLI changed. Found %A{counts}, allowed %A{allowed}. \
              The CLI judges a Running plugin on the awake age the daemon sends (StatusView.runningAge), \
              never on its own wall clock against the daemon's start stamp."
        )

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("                    let elapsed = if now > since then now - since else TimeSpan.Zero")>]
[<InlineData("    let age = DateTime.UtcNow - since")>]
[<InlineData("    let age = clock.WallNow() - since")>]
let ``a wall-clock age of a Running plugin is found`` (line: string) =
    test <@ wallAgeOfRunning.IsMatch line @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("    | StatusView.Running(since, awake) -> StatusView.runningAge now since awake")>]
[<InlineData("    let ago = now - startedAt")>]
let ``an awake age or another stamp is not a wall-clock age of a Running plugin`` (line: string) =
    test <@ not (wallAgeOfRunning.IsMatch line) @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("    let deadline = DateTime.UtcNow.AddSeconds 60.0")>]
[<InlineData("            System.DateTime.UtcNow + timeout")>]
[<InlineData("        with :? IOException when DateTime.UtcNow < deadline ->")>]
[<InlineData("    if DateTimeOffset.UtcNow >= deadline then")>]
let ``a wall-clock deadline is found`` (line: string) =
    test <@ wallClockDeadline.IsMatch line @>

[<Theory(Timeout = SyncHangBackstopMs)>]
[<InlineData("    let startedAt = DateTime.UtcNow")>]
[<InlineData("        fun () -> System.DateTime.UtcNow - started")>]
[<InlineData("            Now = fun () -> DateTime.UtcNow")>]
[<InlineData("    let deadline = HostClock.awakeUtc () + TimeSpan.FromSeconds 5.0")>]
let ``a timestamp or a seam is not a wall-clock deadline`` (line: string) =
    test <@ not (wallClockDeadline.IsMatch line) @>
