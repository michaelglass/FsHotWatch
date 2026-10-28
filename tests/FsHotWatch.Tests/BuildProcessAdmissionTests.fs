/// A build holds one of the repository host's process slots while it runs, the same
/// bound test hosts take turns at (AUTOMATION-682 slice 6, ADR-052).
///
/// Builds here are gated shell children: each records its start, waits on its own release
/// file and records its end. The tests wait on the scheduler's counts and on those
/// records, never on a clock.
module FsHotWatch.Tests.BuildProcessAdmissionTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.Build
open FsHotWatch.TestPrune
open FsHotWatch.ProjectGraph
open FsHotWatch.Tests.TestHelpers

/// A build command that appends `start-<name>` to `log`, waits for its release file and
/// appends `end-<name>`. Answers the command, its args and the release file.
let private gatedBuild (tmpDir: string) (log: string) (name: string) =
    let release = Path.Combine(tmpDir, $"release-%s{name}")
    let script = Path.Combine(tmpDir, $"build-%s{name}.sh")

    File.WriteAllText(
        script,
        "#!/bin/sh\n"
        + $"echo start-%s{name} >> '%s{log}'\n"
        + gatedWait tmpDir release 1500
        + "\n"
        + $"echo end-%s{name} >> '%s{log}'\n"
    )

    "sh", script, release

let private linesOf (log: string) =
    if File.Exists log then
        File.ReadAllLines log |> Array.toList
    else
        []

let private hostWith (lane: WorkScheduler.Lane) (root: string) (command: string) (args: string) =
    let host = PluginHost(Unchecked.defaultof<_>, root, processes = lane)
    host.RegisterHandler(BuildPlugin.create command args [] (ProjectGraph()) [] None [] None)
    host

let private changeIn (host: PluginHost) (root: string) =
    host.EmitFileChanged(SourceChanged [ Path.Combine(root, "Lib.fs") ])

let private spinUntil (condition: unit -> bool) =
    SpinWait.SpinUntil(condition, TimeSpan.FromSeconds 20.0)

// --- (a) builds hold slots ---

[<Fact(Timeout = 60000)>]
let ``two sessions' builds each hold a process slot, and the second waits for the first`` () =
    withTempDir "build-admission-two-sessions" (fun root ->
        let a = Path.Combine(root, "a")
        let b = Path.Combine(root, "b")
        Directory.CreateDirectory a |> ignore
        Directory.CreateDirectory b |> ignore
        let log = Path.Combine(root, "builds.log")
        let processes = WorkScheduler.Scheduler 1
        let laneA = processes.Lane "a"
        let laneB = processes.Lane "b"
        let commandA, scriptA, releaseA = gatedBuild a log "a"
        let commandB, scriptB, releaseB = gatedBuild b log "b"
        let hostA = hostWith laneA a commandA scriptA
        let hostB = hostWith laneB b commandB scriptB

        try
            changeIn hostA a
            changeIn hostB b

            // One build runs and the other waits for its slot, not for anything else.
            test <@ spinUntil (fun () -> processes.Running = 1 && processes.Queued = 1) @>
            test <@ spinUntil (fun () -> (linesOf log).Length = 1) @>
            let first = (linesOf log).Head
            test <@ first = "start-a" || first = "start-b" @>
            test <@ (linesOf log).Length = 1 @>
        finally
            File.WriteAllText(releaseA, "")
            File.WriteAllText(releaseB, "")

        waitForTerminalStatus hostA "build" 30000
        waitForTerminalStatus hostB "build" 30000

        // Never overlapped: every start is followed by its own end.
        let lines = linesOf log
        test <@ lines.Length = 4 @>

        test
            <@
                lines
                |> List.chunkBySize 2
                |> List.forall (fun pair -> pair = [ pair.Head; pair.Head.Replace("start", "end") ])
            @>

        test <@ (laneA.Status()).Granted = 1L && (laneB.Status()).Granted = 1L @>
        test <@ processes.Running = 0 && processes.Queued = 0 @>)

// --- (b) builds and test hosts share the bound ---

/// A job standing in for a test host launched through `GroupSchedule.runAdmitted`, the
/// way TestPrune launches them: it signals its start and holds until released.
let private gatedHost () =
    let started = new ManualResetEventSlim(false)
    let release = TaskCompletionSource<unit>()

    let job =
        async {
            started.Set()
            do! Async.AwaitTask release.Task
            return 0
        }

    job, started, release

[<Fact(Timeout = 60000)>]
let ``a test host waits for another session's build to give the slot back`` () =
    withTempDir "build-admission-build-first" (fun root ->
        let log = Path.Combine(root, "builds.log")
        let processes = WorkScheduler.Scheduler 1
        let command, script, release = gatedBuild root log "a"
        let host = hostWith (processes.Lane "a") root command script
        let job, started, releaseHost = gatedHost ()

        try
            changeIn host root
            test <@ spinUntil (fun () -> processes.Running = 1 && (linesOf log) = [ "start-a" ]) @>

            let tests =
                GroupSchedule.runAdmitted (Some(processes.Lane "b")) None [ job ]
                |> Async.StartAsTask

            test <@ spinUntil (fun () -> processes.Queued = 1) @>
            test <@ not started.IsSet @>
            File.WriteAllText(release, "")
            test <@ started.Wait(TimeSpan.FromSeconds 20.0) @>
            test <@ linesOf log = [ "start-a"; "end-a" ] @>
            test <@ processes.Running = 1 @>
            releaseHost.SetResult()
            test <@ tests.Wait(TimeSpan.FromSeconds 20.0) @>
        finally
            File.WriteAllText(release, "")
            releaseHost.TrySetResult() |> ignore

        waitForTerminalStatus host "build" 30000
        test <@ processes.Running = 0 && processes.Queued = 0 @>)

[<Fact(Timeout = 60000)>]
let ``a build waits for another session's test host to give the slot back`` () =
    withTempDir "build-admission-host-first" (fun root ->
        let log = Path.Combine(root, "builds.log")
        let processes = WorkScheduler.Scheduler 1
        let command, script, release = gatedBuild root log "a"
        let host = hostWith (processes.Lane "a") root command script
        let job, started, releaseHost = gatedHost ()

        try
            let tests =
                GroupSchedule.runAdmitted (Some(processes.Lane "b")) None [ job ]
                |> Async.StartAsTask

            test <@ started.Wait(TimeSpan.FromSeconds 20.0) @>
            File.WriteAllText(release, "")
            changeIn host root
            test <@ spinUntil (fun () -> processes.Queued = 1) @>
            test <@ List.isEmpty (linesOf log) @>
            releaseHost.SetResult()
            test <@ tests.Wait(TimeSpan.FromSeconds 20.0) @>
        finally
            File.WriteAllText(release, "")
            releaseHost.TrySetResult() |> ignore

        waitForTerminalStatus host "build" 30000
        test <@ linesOf log = [ "start-a"; "end-a" ] @>
        test <@ processes.Running = 0 && processes.Queued = 0 @>)

// --- (c) cancellation, failure and crash leak no slot ---

let private outcomeOf (run: Task<'T>) =
    try
        run.Wait(TimeSpan.FromSeconds 20.0) |> ignore
        "completed"
    with :? AggregateException as ex ->
        match ex.InnerException with
        | :? OperationCanceledException -> "cancelled"
        | other -> $"faulted: %s{other.Message}"

[<Fact(Timeout = 30000)>]
let ``a build cancelled while it waits for a slot never starts and leaves no waiter`` () =
    let processes = WorkScheduler.Scheduler 1
    let other = processes.Lane "other"
    test <@ other.Acquire CancellationToken.None |> Async.RunSynchronously @>
    let ran = ref false
    use cts = new CancellationTokenSource()

    let run =
        Async.StartAsTask(
            BuildPlugin.admitted (Some(processes.Lane "session")) (async { ran.Value <- true }),
            cancellationToken = cts.Token
        )

    test <@ spinUntil (fun () -> processes.Queued = 1) @>
    cts.Cancel()
    test <@ outcomeOf run = "cancelled" @>
    test <@ not ran.Value @>
    test <@ processes.Queued = 0 && processes.Running = 1 @>
    other.Release()
    test <@ processes.Running = 0 @>

[<Fact(Timeout = 30000)>]
let ``a build cancelled while it runs gives its slot back`` () =
    let processes = WorkScheduler.Scheduler 1
    let lane = processes.Lane "session"
    use started = new ManualResetEventSlim(false)
    use cts = new CancellationTokenSource()

    let building =
        async {
            let! ct = Async.CancellationToken
            started.Set()
            do! Async.AwaitTask(Task.Delay(Timeout.Infinite, ct))
        }

    let run =
        Async.StartAsTask(BuildPlugin.admitted (Some lane) building, cancellationToken = cts.Token)

    test <@ started.Wait(TimeSpan.FromSeconds 20.0) @>
    test <@ processes.Running = 1 @>
    cts.Cancel()
    test <@ outcomeOf run = "cancelled" @>
    test <@ processes.Running = 0 && processes.Queued = 0 @>
    test <@ (lane.Status()).Granted = 1L @>

[<Fact(Timeout = 30000)>]
let ``a build whose work throws gives its slot back`` () =
    let processes = WorkScheduler.Scheduler 1

    let run =
        BuildPlugin.admitted (Some(processes.Lane "session")) (async { return failwith "msbuild crashed" })
        |> Async.StartAsTask

    test <@ outcomeOf run = "faulted: msbuild crashed" @>
    test <@ processes.Running = 0 && processes.Queued = 0 @>

[<Theory(Timeout = 30000)>]
[<InlineData("false")>]
[<InlineData("this-command-does-not-exist-xyz")>]
let ``a failed or crashed build in a session gives its slot back`` (command: string) =
    withTempDir "build-admission-failed" (fun root ->
        let processes = WorkScheduler.Scheduler 1
        let lane = processes.Lane "session"
        let host = hostWith lane root command ""
        changeIn host root
        waitForTerminalStatus host "build" 20000

        test
            <@
                match host.GetStatus "build" with
                | Some(Failed _) -> true
                | _ -> false
            @>

        test <@ (lane.Status()).Granted = 1L @>
        test <@ processes.Running = 0 && processes.Queued = 0 @>)

// --- (d) a per-worktree daemon is unchanged ---

[<Fact(Timeout = 5000)>]
let ``with no repository host a build's work is run exactly as given`` () =
    let work = async { return 42 }
    test <@ obj.ReferenceEquals(BuildPlugin.admitted None work, work) @>

[<Fact(Timeout = 30000)>]
let ``a template build holds one slot for all its roots`` () =
    withTempDir "build-admission-template" (fun root ->
        let log = Path.Combine(root, "builds.log")
        let processes = WorkScheduler.Scheduler 1
        let lane = processes.Lane "session"
        let graph = ProjectGraph()

        let project name =
            let dir = Path.Combine(root, name)
            Directory.CreateDirectory dir |> ignore
            let source = Path.Combine(dir, $"%s{name}.fs")

            graph.RegisterProject(
                AbsProjectPath.create (Path.Combine(dir, $"%s{name}.fsproj")),
                [ AbsFilePath.create source ],
                []
            )

            source

        let sources = [ project "A"; project "B" ]
        let script = Path.Combine(root, "root-build.sh")
        File.WriteAllText(script, $"#!/bin/sh\necho \"$1\" >> '%s{log}'\n")
        let host = PluginHost(Unchecked.defaultof<_>, root, processes = lane)

        host.RegisterHandler(
            BuildPlugin.create "should-not-run" "" [] graph [] (Some $"sh %s{script} {{project}}") [] None
        )

        host.EmitFileChanged(SourceChanged sources)
        waitForTerminalStatus host "build" 20000
        test <@ (linesOf log).Length = 2 @>
        test <@ (lane.Status()).Granted = 1L @>
        test <@ processes.Running = 0 && processes.Queued = 0 @>)
