/// `tests.maxParallelProjects`: at most N test groups in flight, longest-expected first,
/// and today's all-at-once launch when the option is absent.
///
/// The concurrency tests observe the cap with gates rather than timing: every job
/// records how many jobs are in flight when it starts, then holds until the test
/// releases it, so "never more than N" is read from a counter, not inferred from a clock.
module FsHotWatch.Tests.GroupScheduleTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers

/// N jobs that each wait on their own release gate, with the in-flight count, its peak,
/// and the order the jobs started in.
type private Gated(count: int) =
    let mutable inFlight = 0
    let mutable peak = 0
    let started = Collections.Concurrent.ConcurrentQueue<int>()
    let releases = Array.init count (fun _ -> TaskCompletionSource<unit>())
    let startedSignal = new SemaphoreSlim(0)

    member _.Jobs: Async<int> list =
        [ for i in 0 .. count - 1 ->
              async {
                  let now = Interlocked.Increment(&inFlight)
                  lock started (fun () -> peak <- max peak now)
                  started.Enqueue i
                  startedSignal.Release() |> ignore
                  do! Async.AwaitTask releases.[i].Task
                  Interlocked.Decrement(&inFlight) |> ignore
                  return i
              } ]

    /// Wait until `n` jobs in total have started.
    member _.AwaitStarted(n: int) =
        for _ in 1..n do
            test <@ startedSignal.Wait(TimeSpan.FromSeconds 10.0) @>

    member _.Release(i: int) = releases.[i].TrySetResult() |> ignore
    member _.Peak = lock started (fun () -> peak)
    member _.StartOrder = started.ToArray() |> Array.toList

[<Fact(Timeout = 30000)>]
let ``a cap of 2 never has more than 2 groups in flight and runs them all`` () =
    let gated = Gated 5
    let run = GroupSchedule.run (Some 2) gated.Jobs |> Async.StartAsTask

    // Release the jobs one at a time, each only after its successor slot has filled.
    gated.AwaitStarted 2
    test <@ gated.Peak = 2 @>

    for i in 0..4 do
        gated.Release i

        if i + 2 <= 4 then
            gated.AwaitStarted 1

    test <@ run.Wait(TimeSpan.FromSeconds 10.0) @>
    test <@ gated.Peak = 2 @>
    test <@ run.Result = [| 0; 1; 2; 3; 4 |] @>

[<Fact(Timeout = 30000)>]
let ``a capped run starts groups in the order it is given`` () =
    let gated = Gated 4
    let run = GroupSchedule.run (Some 1) gated.Jobs |> Async.StartAsTask

    for i in 0..3 do
        gated.AwaitStarted 1
        gated.Release i

    test <@ run.Wait(TimeSpan.FromSeconds 10.0) @>
    test <@ gated.StartOrder = [ 0; 1; 2; 3 ] @>
    test <@ gated.Peak = 1 @>

[<Fact(Timeout = 30000)>]
let ``without a cap every group starts at once, exactly as before the option existed`` () =
    // Positive control: the gates would deadlock if any job waited for a slot.
    let gated = Gated 6
    let run = GroupSchedule.run None gated.Jobs |> Async.StartAsTask
    gated.AwaitStarted 6
    test <@ gated.Peak = 6 @>

    for i in 0..5 do
        gated.Release i

    test <@ run.Wait(TimeSpan.FromSeconds 10.0) @>

[<Fact(Timeout = 30000)>]
let ``a group still queued when the run is cancelled never starts`` () =
    let gated = Gated 3
    use cts = new CancellationTokenSource()

    let run =
        Async.StartAsTask(GroupSchedule.run (Some 1) gated.Jobs, cancellationToken = cts.Token)

    gated.AwaitStarted 1
    cts.Cancel()
    // The job in flight finishes its own work; nothing queued behind it may launch.
    gated.Release 0

    let outcome =
        try
            run.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            "completed"
        with :? AggregateException as ex ->
            match ex.InnerException with
            | :? OperationCanceledException -> "cancelled"
            | other -> $"faulted: %s{other.GetType().Name}"

    test <@ outcome = "cancelled" @>
    test <@ gated.StartOrder = [ 0 ] @>

// --- longestFirst ---

let private walls (pairs: (string * float) list) =
    pairs |> List.map (fun (p, s) -> p, TimeSpan.FromSeconds s) |> Map.ofList

[<Fact>]
let ``groups launch longest expected wall first, summing a group's sequential projects`` () =
    let groups = [ "short", [ "A" ]; "long", [ "B" ]; "pair", [ "C"; "D" ] ]

    let ordered =
        GroupSchedule.longestFirst snd (walls [ "A", 10.0; "B", 100.0; "C", 60.0; "D", 50.0 ]) groups

    test <@ ordered |> List.map fst = [ "pair"; "long"; "short" ] @>

[<Fact>]
let ``a group with no history for one of its projects goes first, in configured order`` () =
    let groups =
        [ "known", [ "A" ]
          "new1", [ "N1" ]
          "mixed", [ "B"; "N2" ]
          "known2", [ "C" ] ]

    let ordered =
        GroupSchedule.longestFirst snd (walls [ "A", 10.0; "B", 500.0; "C", 20.0 ]) groups

    test <@ ordered |> List.map fst = [ "new1"; "mixed"; "known2"; "known" ] @>

[<Fact>]
let ``equal expected walls keep their configured order`` () =
    let groups = [ "x", [ "A" ]; "y", [ "B" ]; "z", [ "C" ] ]

    let ordered =
        GroupSchedule.longestFirst snd (walls [ "A", 5.0; "B", 5.0; "C", 5.0 ]) groups

    test <@ ordered |> List.map fst = [ "x"; "y"; "z" ] @>

// --- expectedWalls ---

let private writeReport (repoRoot: string) (run: string) (project: string) (body: string) =
    let dir = Path.Combine(repoRoot, ".fshw", "test-runs", run)
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), body)

let private report (startMs: int64) (stopMs: int64) =
    $"""{{"results":{{"tool":{{"name":"x"}},"summary":{{"tests":1,"passed":1,"failed":0,"start":%d{startMs},"stop":%d{stopMs}}},"tests":[]}}}}"""

[<Fact>]
let ``a project's expected wall is the longest its retained reports record`` () =
    withTempDir "group-schedule-walls" (fun repoRoot ->
        writeReport repoRoot "run1" "Unit" (report 1000L 31000L)
        writeReport repoRoot "run2" "Unit" (report 5000L 95000L)
        writeReport repoRoot "run2" "Integration" (report 0L 350000L)

        let expected = GroupSchedule.expectedWalls repoRoot

        test <@ expected = walls [ "Unit", 90.0; "Integration", 350.0 ] @>)

[<Fact>]
let ``a report without a usable start and stop says nothing about its project`` () =
    withTempDir "group-schedule-unusable" (fun repoRoot ->
        writeReport repoRoot "run1" "NoTimes" """{"results":{"summary":{"tests":1}}}"""
        writeReport repoRoot "run1" "Backwards" (report 9000L 1000L)
        writeReport repoRoot "run1" "Garbage" "{ not json"
        writeReport repoRoot "run1" "NotAnObject" "[1,2,3]"
        writeReport repoRoot "run1" "TopLevel" """{"summary":{"tests":1,"start":0,"stop":2000}}"""
        writeReport repoRoot "run1" "StringTimes" """{"results":{"summary":{"start":"a","stop":"b"}}}"""
        writeReport repoRoot "run1" "SummaryNotObject" """{"results":{"summary":5}}"""
        writeReport repoRoot "run1" "TopLevelNotObject" """{"summary":[0,1]}"""
        writeReport repoRoot "run1" "NoSummary" """{"other":{}}"""
        writeReport repoRoot "run1" "OnlyStart" """{"results":{"summary":{"start":0}}}"""
        // `results` that is not an object falls back to a top-level summary.
        writeReport repoRoot "run1" "ResultsNotObject" """{"results":3,"summary":{"tests":2,"start":0,"stop":3000}}"""

        test <@ GroupSchedule.expectedWalls repoRoot = walls [ "TopLevel", 2.0; "ResultsNotObject", 3.0 ] @>)

[<Fact>]
let ``a report of a run that ran no tests says nothing about its project's wall`` () =
    // An impact-filtered run that selected nothing still writes a report, with a wall of
    // milliseconds. Read as history, it ranks a long project as the shortest one.
    withTempDir "group-schedule-zero-tests" (fun repoRoot ->
        writeReport
            repoRoot
            "filtered"
            "Database"
            """{"results":{"summary":{"tests":0,"passed":0,"start":1000,"stop":1012}}}"""

        writeReport repoRoot "full" "Unit" (report 0L 4000L)

        test <@ GroupSchedule.expectedWalls repoRoot = walls [ "Unit", 4.0 ] @>)

[<Fact>]
let ``an unreadable run directory or report costs only its own history`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "group-schedule-unreadable" (fun repoRoot ->
            writeReport repoRoot "readable" "Unit" (report 0L 4000L)
            writeReport repoRoot "locked" "Hidden" (report 0L 9000L)
            writeReport repoRoot "readable" "LockedFile" (report 0L 7000L)
            let lockedDir = Path.Combine(repoRoot, ".fshw", "test-runs", "locked")

            let lockedFile =
                Path.Combine(repoRoot, ".fshw", "test-runs", "readable", "LockedFile.ctrf.json")

            File.SetUnixFileMode(lockedDir, UnixFileMode.None)
            File.SetUnixFileMode(lockedFile, UnixFileMode.None)
            // A report whose file vanished between listing and reading (here: a link to nothing).
            File.CreateSymbolicLink(
                Path.Combine(repoRoot, ".fshw", "test-runs", "readable", "Vanished.ctrf.json"),
                Path.Combine(repoRoot, "nowhere.json")
            )
            |> ignore

            try
                test <@ GroupSchedule.expectedWalls repoRoot = walls [ "Unit", 4.0 ] @>
            finally
                File.SetUnixFileMode(
                    lockedDir,
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                )

                File.SetUnixFileMode(lockedFile, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``a repository that has never run tests has no expected walls`` () =
    withTempDir "group-schedule-none" (fun repoRoot -> test <@ GroupSchedule.expectedWalls repoRoot |> Map.isEmpty @>)

// --- through the plugin: `tests.maxParallelProjects` reaches the run ---

/// A runner script: logs `start <project>` and `end <project>` around a short pause, so
/// overlapping hosts interleave in the log and a capped run cannot.
let private loggingScript (tmpDir: string) =
    let log = Path.Combine(tmpDir, "hosts.log")
    let script = Path.Combine(tmpDir, "runner.sh")
    File.WriteAllText(script, $"echo \"start $1\" >> '%s{log}'\nsleep 0.2\necho \"end $1\" >> '%s{log}'\n")
    script, log

/// A runner script that waits (bounded) until `count` runners have started, and records
/// how many it saw: all of them only when every host was in flight at once.
let private barrierScript (tmpDir: string) (count: int) =
    let script = Path.Combine(tmpDir, "barrier.sh")

    File.WriteAllText(
        script,
        $"cd '%s{tmpDir}'\ntouch \"started-$1\"\ni=0\n"
        + $"while [ \"$(ls started-* | wc -l | tr -d ' ')\" -lt %d{count} ] && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done\n"
        + "ls started-* | wc -l | tr -d ' ' > \"seen-$1\"\n"
    )

    script

let private runnerConfig (script: string) (project: string) : TestPrunePlugin.TestConfig =
    { Project = project
      Command = "sh"
      Args = $"%s{script} %s{project}"
      Group = project
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = TestPrunePlugin.AutoDetect }

let private runAll (tmpDir: string) (cap: int option) (configs: TestPrunePlugin.TestConfig list) =
    let host = FsHotWatch.PluginHost.PluginHost.create (Unchecked.defaultof<_>) tmpDir

    host.RegisterHandler(
        TestPrunePlugin.createWithLaunchDeadline
            (TimeSpan.FromMinutes 2.0)
            cap
            (fun () -> Map.empty)
            ":memory:"
            TestPrune.Domain.WalkPolicy.FollowAll
            tmpDir
            (Some configs)
            None
            None
            None
            None
            []
            None
            TestMode.initial
    )

    host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore

[<Fact(Timeout = 60000)>]
let ``a capped run launches one host at a time, longest expected first`` () =
    withTempDir "group-schedule-plugin-capped" (fun tmpDir ->
        let script, log = loggingScript tmpDir
        // History: C is the longest, then A; B has none, so it goes first.
        writeReport tmpDir "earlier" "A" (report 0L 20000L)
        writeReport tmpDir "earlier" "C" (report 0L 90000L)

        runAll tmpDir (Some 1) [ runnerConfig script "A"; runnerConfig script "B"; runnerConfig script "C" ]

        let lines = File.ReadAllLines log |> Array.toList

        test <@ lines = [ "start B"; "end B"; "start C"; "end C"; "start A"; "end A" ] @>)

[<Fact(Timeout = 60000)>]
let ``an uncapped run still launches every group at once`` () =
    // Positive control for the plugin path: each host waits until all three have started.
    withTempDir "group-schedule-plugin-uncapped" (fun tmpDir ->
        let script = barrierScript tmpDir 3

        runAll tmpDir None [ runnerConfig script "A"; runnerConfig script "B"; runnerConfig script "C" ]

        let seen =
            [ "A"; "B"; "C" ]
            |> List.map (fun p -> File.ReadAllText(Path.Combine(tmpDir, $"seen-%s{p}")).Trim())

        test <@ seen = [ "3"; "3"; "3" ] @>)
