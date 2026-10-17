module FsHotWatch.Tests.TestPruneKillTimeoutLeaseTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// Every `TestRunStarted` and `TestRunCompleted` the host dispatches, by run id.
let private lifecycleRecorder () =
    let started = Collections.Concurrent.ConcurrentDictionary<Guid, unit>()
    let completed = Collections.Concurrent.ConcurrentDictionary<Guid, unit>()

    let handler: PluginHandler<unit, obj> =
        { Name = PluginName.create "test-run-lifecycle-recorder"
          Init = ()
          Update =
            fun _ state event ->
                async {
                    match event with
                    | TestRunStarted run -> started[run.RunId] <- ()
                    | TestRunCompleted run -> completed[run.RunId] <- ()
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeTestRunStarted; SubscribeTestRunCompleted ]
          PrepareCommit = None
          CacheKey = None
          Teardown = None }

    let unclosed () =
        started.Keys |> Seq.filter (completed.ContainsKey >> not) |> Set.ofSeq

    handler, (fun () -> started.Count), unclosed

[<Fact(Timeout = 60000)>]
let ``a test run whose child kill times out does not wedge the next run on the same daemon`` () =
    let root = isolatedRoot ()
    let invocations = Path.Combine(root, "invocations.txt")
    let script = Path.Combine(root, "fake-tests.sh")
    File.WriteAllText(script, $"echo ran >> \"%s{invocations}\"\nexit 0\n")

    let configs =
        [ { Project = "MyTests"
            Command = "sh"
            Args = $"\"%s{script}\""
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = Some 30
            ReportVerificationFormat = AutoDetect
            AlwaysClasses = [] } ]

    // The first run's process scope records what a kill that never returned records:
    // a child whose termination it cannot establish. The suite itself finishes.
    let runs = ref 0

    let beforeRun (_: Guid) (_: FsHotWatch.HookStep.Tracker) =
        if Threading.Interlocked.Increment(&runs.contents) = 1 then
            FsHotWatch.ProcessRegistry.reportLeak 4242 "`dotnet test` (pid 4242)" "the kill did not return within 10s"

    let recorder, startedCount, unclosed = lifecycleRecorder ()
    let host = PluginHost.create (Unchecked.defaultof<_>) root
    host.RegisterHandler recorder
    host.RegisterHandler(create ":memory:" root (Some configs) None (Some beforeRun) None None [])

    let runTests () =
        host.RunCommand("run-tests", [| """{"waitSec":15}""" |])
        |> Async.RunSynchronously
        |> Option.defaultValue "<no reply>"

    let first = runTests ()

    // The faulted run answers its client with the fault, rather than leaving it to time out.
    test <@ first.Contains "could not establish termination" @>
    test <@ not (first.Contains "busy") @>

    // The next run on the same daemon runs: the fault said nothing about the build output.
    let second = runTests ()
    test <@ not (second.Contains "invalid artifacts") @>
    test <@ File.ReadAllLines(invocations).Length = 2 @>

    // Every run that started was closed, so nothing waiting on a live run (the build
    // plugin defers every build while one is open) waits forever.
    waitUntil (fun () -> startedCount () = 2 && (unclosed ()).IsEmpty) 5000
