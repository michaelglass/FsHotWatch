/// Consumer leases follow the work a client's scan sets off. The scan dispatches its
/// events on behalf of the clients that asked for it; a plugin's run launched from such an
/// event, the result fold of that run, and the events that fold emits stay held by the same
/// clients. A test run three plugins downstream is therefore cancelled, and its test hosts
/// reaped, once every client that wanted it has gone — and kept while one still wants it.
/// Work the daemon itself stands for (the watcher, a scan nobody claimed) is never cancelled.
module FsHotWatch.Tests.ClientLeaseLineageTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private bound = TimeSpan.FromSeconds 10.0

let private signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

type private BuildMsg = | Built

type private TestMsg =
    | TestsDone
    /// The fold an abandoned run delivers in place of its result.
    | TestsWithdrawn

/// What the downstream test plugin did.
type private Probe =
    { Launches: int ref
      Started: TaskCompletionSource<unit>
      Release: TaskCompletionSource<unit>
      Folds: Collections.Concurrent.ConcurrentQueue<string>
      Child: Diagnostics.Process option ref }

let private newProbe () =
    { Launches = ref 0
      Started = signal ()
      Release = signal ()
      Folds = Collections.Concurrent.ConcurrentQueue<string>()
      Child = ref None }

/// A build plugin: a source change runs a build (not cooperative-safe), and the build's
/// result fold emits `BuildCompleted`, as `BuildPlugin` does.
let private builder: PluginHandler<unit, BuildMsg> =
    { Name = PluginName.create "builder"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged(SourceChanged _) ->
                    match ctx.RunExclusive "build" (async { return Built }) with
                    | Claimed -> ()
                    | SlotBusy -> ()
                | Custom Built -> ctx.EmitBuildCompleted BuildSucceeded
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.singleton SubscribeFileChanged
      CacheKey = None
      PrepareCommit = None
      Teardown = None }

/// A test plugin: a completed build launches a cooperative-safe test run that starts a
/// long-lived "test host" in its process scope and waits for `Release`.
let private tester (probe: Probe) : PluginHandler<unit, TestMsg> =
    let run =
        async {
            let psi = Diagnostics.ProcessStartInfo("sleep", "120")
            psi.UseShellExecute <- false
            let child = Diagnostics.Process.Start psi
            ProcessRegistry.track child
            probe.Child.Value <- Some child
            probe.Started.TrySetResult(()) |> ignore
            let! ct = Async.CancellationToken
            do! probe.Release.Task.WaitAsync(ct) |> Async.AwaitTask
            return TestsDone
        }

    { Name = PluginName.create "tester"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | BuildCompleted _ ->
                    Interlocked.Increment(&probe.Launches.contents) |> ignore

                    let work =
                        PluginWork.cooperativeSafe run
                        |> PluginWork.onAbandon (fun () -> TestsWithdrawn)

                    match ctx.RunExclusive "tests" work with
                    | Claimed -> ()
                    | SlotBusy -> failwith "one build, one launch"
                | Custom TestsDone -> probe.Folds.Enqueue "done"
                | Custom TestsWithdrawn -> probe.Folds.Enqueue "withdrawn"
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.singleton SubscribeBuildCompleted
      CacheKey = None
      PrepareCommit = None
      Teardown = None }

let private hostWith (probe: Probe) =
    let host = PluginHost.create nullChecker "/tmp/lineage"
    host.RegisterHandler builder
    host.RegisterHandler(tester probe)
    host

let private idle (host: PluginHost) =
    waitUntilTrue (fun () -> not host.WorkSnapshot.IsBusy) 10000

let private change = SourceChanged [ "/tmp/lineage/a.fs" ]

let private reap (probe: Probe) =
    match probe.Child.Value with
    | Some child when not child.HasExited -> child.Kill true
    | _ -> ()

[<Fact(Timeout = 30000)>]
let ``a test run set off by a client's scan is cancelled and its hosts reaped when that client goes`` () =
    let probe = newProbe ()
    let host = hostWith probe
    use client = new CancellationTokenSource()

    try
        host.EmitFileChangedFor(PluginWorkOwner.ConsumerLeases.ofClient client.Token, change)
        test <@ probe.Started.Task.Wait bound @>
        let child = probe.Child.Value.Value
        test <@ not child.HasExited @>

        client.Cancel()

        test <@ child.WaitForExit 10000 @>
        test <@ idle host @>
        test <@ List.ofSeq probe.Folds = [ "withdrawn" ] @>
        // Nothing relaunched it: the client that wanted it is gone.
        test <@ probe.Launches.Value = 1 @>
    finally
        reap probe

[<Fact(Timeout = 30000)>]
let ``a test run another client still wants survives one client going`` () =
    let probe = newProbe ()
    let host = hostWith probe
    use leaving = new CancellationTokenSource()
    use staying = new CancellationTokenSource()

    try
        let both =
            PluginWorkOwner.ConsumerLeases.merge
                (PluginWorkOwner.ConsumerLeases.ofClient leaving.Token)
                (PluginWorkOwner.ConsumerLeases.ofClient staying.Token)

        host.EmitFileChangedFor(both, change)
        test <@ probe.Started.Task.Wait bound @>
        let child = probe.Child.Value.Value

        leaving.Cancel()

        test <@ not (child.WaitForExit 1000) @>
        test <@ probe.Folds.IsEmpty @>

        probe.Release.TrySetResult(()) |> ignore
        test <@ idle host @>
        test <@ List.ofSeq probe.Folds = [ "done" ] @>
    finally
        reap probe

[<Fact(Timeout = 30000)>]
let ``a test run the daemon stands for is never cancelled`` () =
    let probe = newProbe ()
    let host = hostWith probe

    try
        // The watcher, or a scan no client claimed: no lease to release.
        host.EmitFileChanged change
        test <@ probe.Started.Task.Wait bound @>
        let child = probe.Child.Value.Value

        test <@ not (child.WaitForExit 1000) @>

        probe.Release.TrySetResult(()) |> ignore
        test <@ idle host @>
        test <@ List.ofSeq probe.Folds = [ "done" ] @>
    finally
        reap probe
