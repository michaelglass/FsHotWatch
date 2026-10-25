/// Who holds the work a command asks for. A CLI sends one RPC per connection and closes
/// it the moment the reply arrives, then waits for the work on OTHER connections. A
/// command whose intent outlives its reply (`set-scope owed`, which launches the run a
/// session held) must therefore not be held by its connection alone: that lease is
/// released while the CLI is still waiting, and the run it asked for is cancelled as
/// client-less. `RunCommandFor` holds it through the CLI invocation's run claim too, so it
/// is cancelled only once that invocation has really gone.
module FsHotWatch.Tests.CommandClientLeaseTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Ipc
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

let private bound = TimeSpan.FromSeconds 10.0

let private signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

type private AskMsg =
    | Launch
    | RunDone
    | RunWithdrawn

type private Probe =
    { Started: TaskCompletionSource<unit>
      Release: TaskCompletionSource<unit>
      Folds: Collections.Concurrent.ConcurrentQueue<string> }

let private newProbe () =
    { Started = signal ()
      Release = signal ()
      Folds = Collections.Concurrent.ConcurrentQueue<string>() }

/// A plugin whose `ask` command queues an intent and replies once it has committed, as
/// `set-scope owed` does. The intent's fold launches a cooperative-safe run that waits
/// for `Release`; abandoned, it folds as withdrawn.
let private asker (probe: Probe) : PluginHandler<unit, AskMsg> =
    let run =
        async {
            probe.Started.TrySetResult(()) |> ignore
            let! ct = Async.CancellationToken
            do! probe.Release.Task.WaitAsync(ct) |> Async.AwaitTask
            return RunDone
        }

    { Name = PluginName.create "asker"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | Custom Launch ->
                    let work =
                        PluginWork.cooperativeSafe run |> PluginWork.onAbandon (fun () -> RunWithdrawn)

                    match ctx.RunExclusive "tests" work with
                    | Claimed -> ()
                    | SlotBusy -> failwith "one request, one launch"
                | Custom RunDone -> probe.Folds.Enqueue "done"
                | Custom RunWithdrawn -> probe.Folds.Enqueue "withdrawn"
                | _ -> ()

                return state
            }
      Commands =
        [ "ask",
          PluginCommand.Request(fun ctx _ ->
              async {
                  do! ctx.EnqueueExclusiveIntent "tests" None Launch |> Async.AwaitTask
                  return "asked"
              }) ]
      Subscriptions = Set.empty
      CacheKey = None
      PrepareCommit = None
      Teardown = None }

let private config (host: PluginHost) (invocationReleased: string -> CancellationToken) : DaemonRpcConfig =
    { Host = host
      RequestShutdown = ignore
      RequestScan = ignore
      InvocationReleased = invocationReleased
      GetScanStatus = fun () -> "idle"
      GetScanGeneration = fun () -> 0L
      TriggerBuild = fun () -> async { return () }
      FormatAll = fun () -> async { return "" }
      WaitForScanGeneration = fun _ -> Task.FromResult(())
      WaitForAllTerminal = fun _ -> Task.FromResult(())
      WaitForRest = fun _ -> Task.FromResult(())
      RerunPlugin = fun _ -> async { return Result.Ok() }
      InvalidateCache = fun () -> Task.FromResult(())
      GetUncheckedCount = fun () -> 0
      GetProjectModel = fun () -> FsHotWatch.ProjectModel.Observation.Unobserved
      Context = None }

let private hostWith (probe: Probe) =
    let host =
        PluginHost.create (Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>) "/tmp/command-lease"

    host.RegisterHandler(asker probe)
    host

let private idle (host: PluginHost) =
    waitUntilTrue (fun () -> not host.WorkSnapshot.IsBusy) 10000

/// Ask over one connection, wait for the run it launched, then close that connection the
/// way a CLI does once the reply is in.
let private askThenCloseConnection (probe: Probe) (connection: CancellationTokenSource) (ask: unit -> Task<string>) =
    let reply = ask ()
    test <@ reply.Wait bound && reply.Result = "asked" @>
    test <@ probe.Started.Task.Wait bound @>
    connection.Cancel()

[<Fact(Timeout = 30000)>]
let ``a run a command asked for survives its connection closing while the invocation still waits`` () =
    let probe = newProbe ()
    let host = hostWith probe
    use connection = new CancellationTokenSource()
    use invocation = new CancellationTokenSource()

    let target =
        DaemonRpcTarget(config host (fun _ -> invocation.Token), disconnected = connection.Token)

    askThenCloseConnection probe connection (fun () -> target.RunCommandFor("ask", "", "invocation-1"))

    // Cancellation is synchronous with the last lease's release, so a run held only by
    // the closed connection is already cancelled here and cannot fold `done`.
    probe.Release.TrySetResult(()) |> ignore
    test <@ idle host @>
    test <@ List.ofSeq probe.Folds = [ "done" ] @>

[<Fact(Timeout = 30000)>]
let ``a run a command asked for is cancelled once its invocation has gone too`` () =
    let probe = newProbe ()
    let host = hostWith probe
    use connection = new CancellationTokenSource()
    use invocation = new CancellationTokenSource()

    let target =
        DaemonRpcTarget(config host (fun _ -> invocation.Token), disconnected = connection.Token)

    askThenCloseConnection probe connection (fun () -> target.RunCommandFor("ask", "", "invocation-1"))
    invocation.Cancel()

    test <@ idle host @>
    test <@ List.ofSeq probe.Folds = [ "withdrawn" ] @>

[<Fact(Timeout = 30000)>]
let ``a run asked for by an invocation with no claim to watch is the daemon's`` () =
    let probe = newProbe ()
    let host = hostWith probe
    use connection = new CancellationTokenSource()

    let target =
        DaemonRpcTarget(config host (fun _ -> CancellationToken.None), disconnected = connection.Token)

    askThenCloseConnection probe connection (fun () -> target.RunCommandFor("ask", "", "unclaimed"))

    probe.Release.TrySetResult(()) |> ignore
    test <@ idle host @>
    test <@ List.ofSeq probe.Folds = [ "done" ] @>

[<Fact(Timeout = 30000)>]
let ``a command that names no invocation is held by its connection alone`` () =
    let probe = newProbe ()
    let host = hostWith probe
    use connection = new CancellationTokenSource()

    let target =
        DaemonRpcTarget(config host (fun _ -> failwith "no invocation to look up"), disconnected = connection.Token)

    askThenCloseConnection probe connection (fun () -> target.RunCommand("ask", ""))

    test <@ idle host @>
    test <@ List.ofSeq probe.Folds = [ "withdrawn" ] @>
