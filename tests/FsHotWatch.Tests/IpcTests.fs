module FsHotWatch.Tests.IpcTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Ipc
open FsHotWatch.PluginHost
open FsHotWatch.Plugin
open FsHotWatch.Events

let private defaultRpcConfig (host: PluginHost) : DaemonRpcConfig =
    { Host = host
      RequestShutdown = ignore
      RequestScan = ignore
      GetScanStatus = fun () -> "idle"
      GetScanGeneration = fun () -> 0L
      TriggerBuild = fun () -> async { return () }
      FormatAll = fun () -> async { return "formatted 0 files" }
      WaitForScanGeneration = fun _ -> Task.FromResult(())
      WaitForAllTerminal = fun () -> Task.FromResult(())
      InvalidateAndRecheck = fun _ -> async { return "{\"status\": \"rechecked\"}" } }

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

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    Thread.Sleep(500)

    try
        let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result.Contains("test-plugin") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

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

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    Thread.Sleep(500)

    try
        let result = IpcClient.runCommand pipeName "greet" "" |> Async.RunSynchronously
        test <@ result.Contains("hello world") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

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

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    Thread.Sleep(500)

    try
        let result =
            IpcClient.getPluginStatus pipeName "status-plugin" |> Async.RunSynchronously

        test <@ result = "Idle" @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact>]
let ``GetPluginStatus returns not found for unknown plugin`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    Thread.Sleep(500)

    try
        let result =
            IpcClient.getPluginStatus pipeName "nonexistent" |> Async.RunSynchronously

        test <@ result = "not found" @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact>]
let ``RunCommand with plugin that returns a result`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "echo-plugin"

            member _.Initialize(ctx) =
                ctx.RegisterCommand(
                    "echo",
                    fun args ->
                        async {
                            let msg = if args.Length > 0 then args.[0] else "empty"
                            return $"echoed: {msg}"
                        }
                )

            member _.Dispose() = () }

    host.Register(plugin)

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    Thread.Sleep(500)

    try
        let result =
            IpcClient.runCommand pipeName "echo" "test-data" |> Async.RunSynchronously

        test <@ result.Contains("echoed: test-data") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact>]
let ``RunCommand returns unknown command for non-existent command`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    Thread.Sleep(500)

    try
        let result =
            IpcClient.runCommand pipeName "no-such-command" "" |> Async.RunSynchronously

        test <@ result = "unknown command" @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact>]
let ``GetStatus serializes multiple plugins with different statuses`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let idlePlugin =
        { new IFsHotWatchPlugin with
            member _.Name = "idle-p"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }

    let runningPlugin =
        { new IFsHotWatchPlugin with
            member _.Name = "running-p"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Running(since = System.DateTime(2025, 1, 1)))

            member _.Dispose() = () }

    let completedPlugin =
        { new IFsHotWatchPlugin with
            member _.Name = "completed-p"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Completed(box "result", System.DateTime(2025, 1, 2)))

            member _.Dispose() = () }

    let failedPlugin =
        { new IFsHotWatchPlugin with
            member _.Name = "failed-p"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Failed("something broke", System.DateTime(2025, 1, 3)))

            member _.Dispose() = () }

    host.Register(idlePlugin)
    host.Register(runningPlugin)
    host.Register(completedPlugin)
    host.Register(failedPlugin)

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    Thread.Sleep(500)

    try
        let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result.Contains("idle-p") @>
        test <@ result.Contains("Idle") @>
        test <@ result.Contains("running-p") @>
        test <@ result.Contains("Running since") @>
        test <@ result.Contains("completed-p") @>
        test <@ result.Contains("Completed at") @>
        test <@ result.Contains("failed-p") @>
        test <@ result.Contains("something broke") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact>]
let ``DaemonRpcTarget.GetStatus without IPC serializes all status variants`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let p1 =
        { new IFsHotWatchPlugin with
            member _.Name = "a"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }

    let p2 =
        { new IFsHotWatchPlugin with
            member _.Name = "b"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Running(since = System.DateTime(2025, 6, 15)))

            member _.Dispose() = () }

    let p3 =
        { new IFsHotWatchPlugin with
            member _.Name = "c"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Completed(box 42, System.DateTime(2025, 6, 16)))

            member _.Dispose() = () }

    let p4 =
        { new IFsHotWatchPlugin with
            member _.Name = "d"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Failed("oops", System.DateTime(2025, 6, 17)))

            member _.Dispose() = () }

    host.Register(p1)
    host.Register(p2)
    host.Register(p3)
    host.Register(p4)

    let target = DaemonRpcTarget(defaultRpcConfig host)

    let json = target.GetStatus()
    test <@ json.Contains("Idle") @>
    test <@ json.Contains("Running since") @>
    test <@ json.Contains("Completed at") @>
    test <@ json.Contains("oops") @>

[<Fact>]
let ``DaemonRpcTarget.RunCommand returns unknown command for missing command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let target = DaemonRpcTarget(defaultRpcConfig host)

    let result =
        target.RunCommand("nonexistent", "")
        |> Async.AwaitTask
        |> Async.RunSynchronously

    test <@ result = "unknown command" @>

[<Fact>]
let ``DaemonRpcTarget.RunCommand returns result for known command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "cmd-test"

            member _.Initialize(ctx) =
                ctx.RegisterCommand(
                    "hello",
                    fun args ->
                        async {
                            let arg = if args.Length > 0 then args.[0] else "world"
                            return $"hello {arg}"
                        }
                )

            member _.Dispose() = () }

    host.Register(plugin)

    let target = DaemonRpcTarget(defaultRpcConfig host)

    // Test with empty args
    let result1 =
        target.RunCommand("hello", "") |> Async.AwaitTask |> Async.RunSynchronously

    test <@ result1 = "hello world" @>

    // Test with non-empty args (exercises the else branch of argsJson parsing)
    let result2 =
        target.RunCommand("hello", "test-arg")
        |> Async.AwaitTask
        |> Async.RunSynchronously

    test <@ result2 = "hello test-arg" @>

[<Fact>]
let ``DaemonRpcTarget.GetPluginStatus returns status strings for each variant`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let p1 =
        { new IFsHotWatchPlugin with
            member _.Name = "idle-test"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }

    let p2 =
        { new IFsHotWatchPlugin with
            member _.Name = "failed-test"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Failed("bad", System.DateTime(2025, 1, 1)))

            member _.Dispose() = () }

    host.Register(p1)
    host.Register(p2)

    let target = DaemonRpcTarget(defaultRpcConfig host)

    test <@ target.GetPluginStatus("idle-test") = "Idle" @>
    test <@ (target.GetPluginStatus("failed-test")).Contains("bad") @>
    test <@ target.GetPluginStatus("no-such") = "not found" @>

[<Fact>]
let ``WaitForScan resolves immediately when generation already advanced`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            GetScanStatus = fun () -> "complete: 10 files checked in 0.5s"
            WaitForScanGeneration =
                fun afterGen ->
                    if 5L > afterGen then
                        Task.FromResult(())
                    else
                        task { do! Task.Delay(10_000) } }

    let target = DaemonRpcTarget(config)
    let result = target.WaitForScan(3L).Result
    test <@ result.Contains("complete") @>

[<Fact>]
let ``WaitForScan blocks until generation advances`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let tcs = TaskCompletionSource<unit>()

    let config =
        { defaultRpcConfig host with
            GetScanStatus = fun () -> "complete: 5 files"
            WaitForScanGeneration = fun _afterGen -> tcs.Task }

    let target = DaemonRpcTarget(config)
    let waitTask = target.WaitForScan(0L)

    test <@ not waitTask.IsCompleted @>

    tcs.SetResult(())

    let result = waitTask.Result
    test <@ result.Contains("complete") @>

[<Fact>]
let ``WaitForScan legacy path resolves immediately on hot daemon`` () =
    // Regression: WaitForGeneration(-1, currentGen>0) must not hang.
    // On a hot daemon the scan already completed (generation=1+),
    // so the legacy path (afterGeneration=-1) should return immediately.
    let signal = FsHotWatch.Daemon.ScanSignal()
    let task = signal.WaitForGeneration(-1L, 1L)
    test <@ task.IsCompleted @>

[<Fact>]
let ``WaitForScan legacy path blocks on cold daemon`` () =
    // On a cold daemon (generation=0), the legacy path should block
    // until a scan completes.
    let signal = FsHotWatch.Daemon.ScanSignal()
    let task = signal.WaitForGeneration(-1L, 0L)
    test <@ not task.IsCompleted @>
    // Signal generation 1 — should resolve
    signal.SignalGeneration(1L)
    test <@ task.IsCompleted @>

[<Fact>]
let ``WaitForComplete resolves when all plugins terminal`` () =
    let tcs = TaskCompletionSource<unit>()
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            WaitForAllTerminal = fun () -> tcs.Task }

    let target = DaemonRpcTarget(config)
    let waitTask = target.WaitForComplete()

    test <@ not waitTask.IsCompleted @>

    tcs.SetResult(())

    let result = waitTask.Result
    // Should return status JSON
    test <@ result.Contains("{") @>
