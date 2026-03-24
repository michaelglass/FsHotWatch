module FsHotWatch.Tests.IpcTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Ipc
open FsHotWatch.PluginHost
open FsHotWatch.Plugin
open FsHotWatch.Events

[<Fact>]
let ``server responds to GetStatus`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    // Register a plugin so there's something in status
    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "test-plugin"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }
    host.Register(plugin)

    let serverTask = Async.StartAsTask(IpcServer.start pipeName host cts.Token)
    Thread.Sleep(500)

    try
        let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result.Contains("test-plugin") @>
    finally
        cts.Cancel()
        try serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore with _ -> ()

[<Fact>]
let ``server responds to RunCommand`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "greeter"
            member _.Initialize(ctx) =
                ctx.RegisterCommand("greet", fun _args -> async { return "hello world" })
            member _.Dispose() = () }
    host.Register(plugin)

    let serverTask = Async.StartAsTask(IpcServer.start pipeName host cts.Token)
    Thread.Sleep(500)

    try
        let result = IpcClient.runCommand pipeName "greet" "" |> Async.RunSynchronously
        test <@ result.Contains("hello world") @>
    finally
        cts.Cancel()
        try serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore with _ -> ()
