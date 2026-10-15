/// A test run a client's check set off is that client's: once it has gone, the run is
/// cancelled, its test host reaped, and the plugin is ready for the next run the daemon
/// owes. A run the daemon itself owes is never cancelled.
module FsHotWatch.Tests.TestPruneClientLeaseTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
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

let private alive (pid: int) =
    try
        not (Diagnostics.Process.GetProcessById(pid).HasExited)
    with :? ArgumentException ->
        false

/// A plugin whose first run starts a test host that only a kill ends (its pid written to
/// `pidFile`), and whose later runs pass at once (each appending to `ran`).
let private hostWithHangingFirstRun (root: string) =
    let pidFile = Path.Combine(root, "host.pid")
    let first = Path.Combine(root, "first-started")
    let ran = Path.Combine(root, "ran.txt")
    let script = Path.Combine(root, "fake-tests.sh")

    File.WriteAllText(
        script,
        $"if [ -f \"%s{first}\" ]; then echo ran >> \"%s{ran}\"; exit 0; fi\n\
          touch \"%s{first}\"\necho $$ > \"%s{pidFile}\"\nexec sleep 120\n"
    )

    let configs =
        [ { Project = "MyTests"
            Command = "sh"
            Args = $"\"%s{script}\""
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = Some 300
            ReportVerificationFormat = AutoDetect } ]

    let recorder, startedCount, unclosed = lifecycleRecorder ()
    let host = PluginHost.create (Unchecked.defaultof<_>) root
    host.RegisterHandler recorder
    host.RegisterHandler(create ":memory:" root (Some configs) None None None None [])

    let hostPid () =
        if File.Exists pidFile then
            match Int32.TryParse((File.ReadAllText pidFile).Trim()) with
            | true, pid -> Some pid
            | _ -> None
        else
            None

    let ranCount () =
        if File.Exists ran then File.ReadAllLines(ran).Length else 0

    host, hostPid, ranCount, startedCount, unclosed

let private reap (hostPid: unit -> int option) =
    match hostPid () with
    | Some pid when alive pid -> Diagnostics.Process.GetProcessById(pid).Kill true
    | _ -> ()

[<Fact(Timeout = 60000)>]
let ``a test run whose client has gone is cancelled and its host reaped`` () =
    let root = isolatedRoot ()
    let host, hostPid, ranCount, startedCount, unclosed = hostWithHangingFirstRun root
    use client = new CancellationTokenSource()

    try
        // The build a client's scan set off completes: the run it launches is the client's.
        PluginWorkOwner.ConsumerLeases.onBehalfOf (PluginWorkOwner.ConsumerLeases.ofClient client.Token) (fun () ->
            host.EmitBuildCompleted BuildSucceeded)

        waitUntil (fun () -> (hostPid ()).IsSome) 20000
        let pid = (hostPid ()).Value
        test <@ alive pid @>

        client.Cancel()

        // The host is reaped, and the run it belonged to is closed.
        waitUntil (fun () -> not (alive pid)) 15000
        waitForQuiescent host 20000
        waitUntil (fun () -> startedCount () = 1 && (unclosed ()).IsEmpty) 5000
        test <@ ranCount () = 0 @>

        // Nothing of the cancelled run is left in flight: the next build the daemon owes
        // launches its run.
        host.EmitBuildCompleted BuildSucceeded
        waitUntil (fun () -> ranCount () = 1) 20000
        waitForQuiescent host 20000
        waitUntil (fun () -> (unclosed ()).IsEmpty) 5000
    finally
        reap hostPid

[<Fact(Timeout = 60000)>]
let ``a test run the daemon owes is not cancelled when a client goes`` () =
    let root = isolatedRoot ()
    let host, hostPid, _, _, _ = hostWithHangingFirstRun root
    use client = new CancellationTokenSource()

    try
        // A watcher-driven build: no client holds it.
        host.EmitBuildCompleted BuildSucceeded
        waitUntil (fun () -> (hostPid ()).IsSome) 20000
        let pid = (hostPid ()).Value

        client.Cancel()

        Thread.Sleep 1000
        test <@ alive pid @>
    finally
        reap hostPid
