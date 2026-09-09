module FsHotWatch.Tests.VerdictEvidenceTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Daemon

[<Fact(Timeout = 15000)>]
[<Trait("A104Evidence", "ReportedStatusCannotMintVerdict")>]
let ``a reported terminal status cannot mint evidence after its event drains`` () =
    let checker = Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>
    let host = PluginHost.create checker "/tmp/fshw-unearned-verdict"

    let reported =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    host.OnStatusChanged.Add(fun (name, status) ->
        match name, status with
        | "test-prune", Completed _ -> reported.TrySetResult(()) |> ignore
        | _ -> ())

    let handler: PluginHandler<unit, unit> =
        { Name = PluginName.create "test-prune"
          Init = ()
          Update =
            fun ctx state _ ->
                async {
                    // This UI claim carries no executed results, launch identity,
                    // coverage or pending-obligation evidence. It must remain legal
                    // to report progress without granting the ability to mint green.
                    ctx.ReportStatus(Completed(DateTime.UtcNow, RunVerdict.create "claimed success" TimeSpan.Zero))
                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    host.RegisterHandler handler
    host.EmitFileChanged(SourceChanged [ "Unverified.fs" ])
    reported.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
    Assert.True(SpinWait.SpinUntil((fun () -> host.CompletedDispatches() = 1L), TimeSpan.FromSeconds 5.0))
    Assert.False(host.AnyPluginBusy(), "the accepted event has actually drained")

    Assert.True(
        host.GetAllStatuses()
        |> Map.exists (fun _ status -> PluginStatus.isTerminal status),
        "positive control: the UI terminal claim was published"
    )

    let verdict = waitForVerdict host (TimeSpan.FromSeconds 1.0) CancellationToken.None

    let winner =
        Task.WhenAny([| verdict :> Task; Task.Delay(TimeSpan.FromSeconds 5.0) |]).GetAwaiter().GetResult()

    Assert.True(obj.ReferenceEquals(verdict, winner), "the original verdict wait must settle, not just its observer")

    Assert.Throws<TimeoutException>(fun () -> verdict.GetAwaiter().GetResult())
    |> ignore
