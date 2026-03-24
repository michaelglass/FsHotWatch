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

[<Fact>]
let ``GetPluginStatus returns specific plugin's status`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "status-plugin"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }
    host.Register(plugin)

    let serverTask = Async.StartAsTask(IpcServer.start pipeName host cts.Token)
    Thread.Sleep(500)

    try
        let result = IpcClient.getPluginStatus pipeName "status-plugin" |> Async.RunSynchronously
        test <@ result = "Idle" @>
    finally
        cts.Cancel()
        try serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore with _ -> ()

[<Fact>]
let ``GetPluginStatus returns not found for unknown plugin`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let serverTask = Async.StartAsTask(IpcServer.start pipeName host cts.Token)
    Thread.Sleep(500)

    try
        let result = IpcClient.getPluginStatus pipeName "nonexistent" |> Async.RunSynchronously
        test <@ result = "not found" @>
    finally
        cts.Cancel()
        try serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore with _ -> ()

[<Fact>]
let ``RunCommand with plugin that returns a result`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "echo-plugin"
            member _.Initialize(ctx) =
                ctx.RegisterCommand("echo", fun args ->
                    async {
                        let msg = if args.Length > 0 then args.[0] else "empty"
                        return $"echoed: {msg}"
                    })
            member _.Dispose() = () }
    host.Register(plugin)

    let serverTask = Async.StartAsTask(IpcServer.start pipeName host cts.Token)
    Thread.Sleep(500)

    try
        let result = IpcClient.runCommand pipeName "echo" "test-data" |> Async.RunSynchronously
        test <@ result.Contains("echoed: test-data") @>
    finally
        cts.Cancel()
        try serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore with _ -> ()

[<Fact>]
let ``server handles client disconnection gracefully`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let serverTask = Async.StartAsTask(IpcServer.start pipeName host cts.Token)
    Thread.Sleep(500)

    // Connect and immediately disconnect
    try
        use pipeClient =
            new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous)
        pipeClient.Connect(3000)
        // Immediately dispose (disconnect)
    with _ -> ()

    // Give server time to handle the disconnection
    Thread.Sleep(500)

    // Server should still be running - verify by making a successful request
    try
        let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result <> null @>
    finally
        cts.Cancel()
        try serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore with _ -> ()
