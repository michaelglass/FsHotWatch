module FsHotWatch.Tests.IpcTests

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Ipc
open FsHotWatch.Cli
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.PluginHost
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Events
open FsHotWatch.Daemon
open FsHotWatch.IdleExit
open FsHotWatch.Tests.TestHelpers

let private waitForServer (pipeName: string) =
    waitUntil
        (fun () ->
            try
                IpcClient.getStatus pipeName |> Async.RunSynchronously |> ignore
                true
            with _ ->
                false)
        5000

let private runProcessIn (fileName: string) (workingDirectory: string) (args: string list) : int * string * string =
    // ArgumentList avoids shell-quoting differences on paths with spaces.
    let psi = ProcessStartInfo(fileName)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.WorkingDirectory <- workingDirectory

    for arg in args do
        psi.ArgumentList.Add(arg)

    use proc = Process.Start(psi)

    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()

    if not (proc.WaitForExit(90000)) then
        try
            proc.Kill(entireProcessTree = true)
        with _ ->
            ()

        proc.WaitForExit()

        failwithf
            "%s %s did not exit within 90 seconds\nstdout:\n%s\nstderr:\n%s"
            fileName
            (String.concat " " args)
            (stdout.GetAwaiter().GetResult())
            (stderr.GetAwaiter().GetResult())

    proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()

let private runCliIn (workingDirectory: string) (args: string list) : int * string * string =
    // Exercise the packaged CLI entry assembly, so `check` starts its daemon as a
    // separate process just as a caller does.
    let cliAssembly = typeof<FsHotWatch.Cli.Program.Command>.Assembly.Location
    runProcessIn "dotnet" workingDirectory (cliAssembly :: args)

let private defaultRpcConfig (host: PluginHost) : DaemonRpcConfig =
    { Host = host
      RequestShutdown = ignore
      RequestScan = ignore
      GetScanStatus = fun () -> "idle"
      GetScanGeneration = fun () -> 0L
      TriggerBuild = fun () -> async { return () }
      FormatAll = fun () -> async { return "formatted 0 files" }
      WaitForScanGeneration = fun _ -> Task.FromResult(())
      WaitForAllTerminal = fun _ -> Task.FromResult(())
      RerunPlugin = fun _ -> async { return Result.Ok() }
      InvalidateCache = fun () -> Task.FromResult(())
      GetUncheckedCount = fun () -> 0 }

type private BlockingPreprocessor(entered: ManualResetEventSlim, release: ManualResetEventSlim) =
    interface IFsHotWatchPreprocessor with
        member _.Name = "hold-cold-fcs"

        member _.Process (changedFiles: string list) (_repoRoot: string) =
            entered.Set()

            if not (release.Wait(TimeSpan.FromSeconds(10.0))) then
                failwith "test did not release the cold scan preprocessor"

            changedFiles

        member _.Dispose() = ()

[<Fact(Timeout = 15000)>]
let ``tracked RPC owns and releases the idle-exit work lease`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let work = WorkLease.create ()

    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let config =
        { defaultRpcConfig host with
            WaitForScanGeneration =
                fun _ ->
                    entered.TrySetResult(()) |> ignore
                    release.Task }

    let target =
        DaemonRpcTarget(
            config,
            deadline = TimeSpan.FromSeconds(5.0),
            beginActiveWork = fun () -> WorkLease.acquire work
        )

    let wait = target.WaitForScan(0L)

    entered.Task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
    test <@ WorkLease.count work = 1 @>

    release.TrySetResult(()) |> ignore
    wait.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
    test <@ WorkLease.count work = 0 @>

[<Fact(Timeout = 15000)>]
let ``check session owns the idle-exit lease through its terminal response`` () =
    // Catches a check protocol that releases after Scan/WaitForScan but before
    // WaitForComplete, diagnostics, completeness, and verdict publication.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let work = WorkLease.create ()

    let target =
        DaemonRpcTarget(defaultRpcConfig host, beginActiveWork = (fun () -> WorkLease.acquire work))

    let session = target.BeginCheckSession()
    test <@ WorkLease.count work = 1 @>

    target.EndCheckSession(session) |> ignore
    test <@ WorkLease.count work = 0 @>

[<Fact(Timeout = 5000)>]
let ``abandoned check session releases its lease at the bounded deadline`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let work = WorkLease.create ()

    let target =
        DaemonRpcTarget(
            defaultRpcConfig host,
            deadline = TimeSpan.FromMilliseconds(100.0),
            beginActiveWork = (fun () -> WorkLease.acquire work)
        )

    target.BeginCheckSession() |> ignore
    test <@ WorkLease.count work = 1 @>
    test <@ waitUntilTrue (fun () -> WorkLease.count work = 0) 2000 @>

[<Fact(Timeout = 5000)>]
let ``renewed check session holds its lease across a multi-step transaction`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let work = WorkLease.create ()

    let target =
        DaemonRpcTarget(
            defaultRpcConfig host,
            deadline = TimeSpan.FromMilliseconds(250.0),
            beginActiveWork = (fun () -> WorkLease.acquire work)
        )

    let session = target.BeginCheckSession()

    // Three protocol steps span beyond the original 250ms lease. Each renewal
    // moves the bounded orphan deadline forward, rather than letting a live
    // check lose ownership midway through its terminal response.
    for _ in 1..3 do
        Task.Delay(120).GetAwaiter().GetResult()
        target.RenewCheckSession(session) |> ignore
        test <@ WorkLease.count work = 1 @>

    target.EndCheckSession(session) |> ignore
    test <@ WorkLease.count work = 0 @>

[<Fact(Timeout = 5000)>]
let ``cancelling a parked session renewal does not hold terminal cleanup hostage`` () =
    // A renewal is a real RPC and can be parked in its remote invoke when the
    // client begins normal terminal cleanup. That cleanup must not wait for the
    // ambient IPC deadline before it can release the process.
    let renewEntered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let endCalled =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let parkedRenew =
        TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return "session" }
          Renew =
            fun _ _ ->
                async {
                    renewEntered.TrySetResult(()) |> ignore
                    return! parkedRenew.Task |> Async.AwaitTask
                }
          End =
            fun _ _ ->
                async {
                    endCalled.TrySetResult(()) |> ignore
                    return "ended"
                }
          RenewCadence = fun () -> TimeSpan.FromMilliseconds(1.0)
          FinalRenewWait = TimeSpan.FromMilliseconds(50.0)
          ShutdownWait = TimeSpan.FromMilliseconds(50.0) }

    let stopwatch = Stopwatch.StartNew()

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith transport "fshw-parked-renewal" (fun _ _ ->
            test <@ renewEntered.Task.Wait(TimeSpan.FromSeconds(1.0)) @>
            0)

    stopwatch.Stop()
    parkedRenew.TrySetResult("renewed") |> ignore

    test <@ exitCode = 0 @>
    test <@ endCalled.Task.Wait(TimeSpan.FromSeconds(1.0)) @>
    test <@ stopwatch.Elapsed < TimeSpan.FromMilliseconds(500.0) @>

[<Fact(Timeout = 5000)>]
let ``a check whose session expires before its terminal response fails closed`` () =
    // A long transaction that missed its renewal deadline must not still return
    // the action's green result: the daemon has already reclaimed its lease.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let work = WorkLease.create ()

    let target =
        DaemonRpcTarget(
            defaultRpcConfig host,
            deadline = TimeSpan.FromMilliseconds(50.0),
            beginActiveWork = (fun () -> WorkLease.acquire work)
        )

    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return target.BeginCheckSession() }
          Renew = fun _ session -> async { return target.RenewCheckSession(session) }
          End = fun _ session -> async { return target.EndCheckSession(session) }
          RenewCadence = fun () -> TimeSpan.FromMilliseconds(100.0)
          FinalRenewWait = TimeSpan.FromMilliseconds(50.0)
          ShutdownWait = TimeSpan.FromMilliseconds(50.0) }

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith transport "fshw-expired-session" (fun sessionFailure _ ->
            test <@ waitUntilTrue (fun () -> WorkLease.count work = 0) 1000 @>
            // Let the renewal see the daemon's missing reply before the action
            // attempts to publish a terminal result.
            Task.Delay(150).GetAwaiter().GetResult()
            test <@ sessionFailure().IsSome @>
            0)

    test <@ exitCode = 2 @>

[<Fact(Timeout = 5000)>]
let ``a final renewal fence catches expiry after a prior successful heartbeat`` () =
    // The first heartbeat is deliberately successful, then the check spends longer
    // than the lease deadline preparing its terminal answer. Only the final fence can
    // observe that reclamation; accepting the earlier heartbeat would mint a stale green.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let work = WorkLease.create ()

    let firstRenewed =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable cadenceCalls = 0

    let target =
        DaemonRpcTarget(
            defaultRpcConfig host,
            deadline = TimeSpan.FromMilliseconds(50.0),
            beginActiveWork = (fun () -> WorkLease.acquire work)
        )

    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return target.BeginCheckSession() }
          Renew =
            fun _ session ->
                async {
                    let reply = target.RenewCheckSession(session)

                    if reply = "renewed" then
                        firstRenewed.TrySetResult(()) |> ignore

                    return reply
                }
          End = fun _ session -> async { return target.EndCheckSession(session) }
          RenewCadence =
            fun () ->
                cadenceCalls <- cadenceCalls + 1

                if cadenceCalls = 1 then
                    TimeSpan.FromMilliseconds(1.0)
                else
                    TimeSpan.FromSeconds(1.0)
          FinalRenewWait = TimeSpan.FromMilliseconds(50.0)
          ShutdownWait = TimeSpan.FromMilliseconds(50.0) }

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith transport "fshw-final-fence-expiry" (fun _ finalFence ->
            test <@ firstRenewed.Task.Wait(TimeSpan.FromSeconds(1.0)) @>
            Task.Delay(150).GetAwaiter().GetResult()

            match finalFence () with
            | Some reason -> test <@ reason.Contains("no longer owns") @>
            | None -> failwith "expected the final renewal fence to observe lease expiry"

            0)

    test <@ exitCode = 2 @>

[<Fact(Timeout = 5000)>]
let ``a timed out final renewal fence refuses a terminal green`` () =
    let parkedRenew =
        TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

    let endCalled =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return "session" }
          Renew = fun _ _ -> async { return! parkedRenew.Task |> Async.AwaitTask }
          End =
            fun _ _ ->
                async {
                    endCalled.TrySetResult(()) |> ignore
                    return "ended"
                }
          RenewCadence = fun () -> TimeSpan.FromHours(1.0)
          FinalRenewWait = TimeSpan.FromMilliseconds(25.0)
          ShutdownWait = TimeSpan.FromMilliseconds(50.0) }

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith transport "fshw-final-fence-timeout" (fun _ finalFence ->
            match finalFence () with
            | Some reason -> test <@ reason.Contains("timed out") @>
            | None -> failwith "expected the parked final renewal to time out"

            0)

    parkedRenew.TrySetResult("renewed") |> ignore
    test <@ exitCode = 2 @>
    test <@ endCalled.Task.Wait(TimeSpan.FromSeconds(1.0)) @>

[<Fact(Timeout = 5000)>]
let ``an infinite final renewal wait is clamped before a terminal green`` () =
    let parkedRenew =
        TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return "session" }
          Renew = fun _ _ -> async { return! parkedRenew.Task |> Async.AwaitTask }
          End = fun _ _ -> async { return "ended" }
          RenewCadence = fun () -> TimeSpan.FromHours(1.0)
          FinalRenewWait = Timeout.InfiniteTimeSpan
          ShutdownWait = TimeSpan.FromMilliseconds(50.0) }

    let stopwatch = Stopwatch.StartNew()

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith transport "fshw-final-fence-infinite" (fun _ finalFence ->
            test <@ finalFence().IsSome @>
            0)

    stopwatch.Stop()
    parkedRenew.TrySetResult("renewed") |> ignore
    test <@ exitCode = 2 @>
    test <@ stopwatch.Elapsed < TimeSpan.FromSeconds(1.0) @>

[<Fact(Timeout = 5000)>]
let ``a parked End cleanup is bounded and preserves the action result`` () =
    let parkedEnd =
        TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return "session" }
          Renew = fun _ _ -> async { return "renewed" }
          End = fun _ _ -> async { return! parkedEnd.Task |> Async.AwaitTask }
          RenewCadence = fun () -> TimeSpan.FromHours(1.0)
          FinalRenewWait = TimeSpan.FromMilliseconds(50.0)
          ShutdownWait = Timeout.InfiniteTimeSpan }

    let stopwatch = Stopwatch.StartNew()

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith transport "fshw-parked-end" (fun _ _ -> 0)

    stopwatch.Stop()
    parkedEnd.TrySetResult("ended") |> ignore

    test <@ exitCode = 0 @>
    test <@ stopwatch.Elapsed < TimeSpan.FromSeconds(1.0) @>

[<Fact(Timeout = 5000)>]
let ``a failed final renewal fence refuses a terminal green`` () =
    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return "session" }
          Renew = fun _ _ -> async { return raise (InvalidOperationException "final renew transport failed") }
          End = fun _ _ -> async { return "ended" }
          RenewCadence = fun () -> TimeSpan.FromHours(1.0)
          FinalRenewWait = TimeSpan.FromMilliseconds(50.0)
          ShutdownWait = TimeSpan.FromMilliseconds(50.0) }

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith transport "fshw-final-fence-exception" (fun _ finalFence ->
            match finalFence () with
            | Some reason -> test <@ reason.Contains("transport failed") @>
            | None -> failwith "expected the failed final renewal to refuse the terminal result"

            0)

    test <@ exitCode = 2 @>

[<Fact(Timeout = 5000)>]
let ``a successful final renewal fence preserves a terminal green`` () =
    let mutable renewCalls = 0

    let transport: FsHotWatch.Cli.Program.CheckSessionTransport =
        { Begin = fun _ -> async { return "session" }
          Renew =
            fun _ _ ->
                async {
                    renewCalls <- renewCalls + 1
                    return "renewed"
                }
          End = fun _ _ -> async { return "ended" }
          RenewCadence = fun () -> TimeSpan.FromHours(1.0)
          FinalRenewWait = TimeSpan.FromMilliseconds(50.0)
          ShutdownWait = TimeSpan.FromMilliseconds(50.0) }

    let exitCode =
        FsHotWatch.Cli.Program.withDaemonCheckSessionWith
            transport
            "fshw-final-fence-green"
            (fun sessionFailure finalFence ->
                test <@ sessionFailure().IsNone @>
                test <@ finalFence().IsNone @>
                0)

    test <@ exitCode = 0 @>
    test <@ renewCalls = 1 @>

[<Fact(Timeout = 15000)>]
let ``check session holds a real pipe lease until the client ends it`` () =
    // Regression for the wire protocol, not merely the target object: check opens a
    // fresh pipe per RPC, so the server must retain ownership across connections.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let work = WorkLease.create ()
    use cts = new CancellationTokenSource()
    // macOS maps named pipes to Unix-domain sockets with a 104-byte path cap.
    let pipeName = $"fshw-s-{Guid.NewGuid():N}"

    let server =
        Async.StartAsTask(
            IpcServer.startWithActiveWork pipeName (defaultRpcConfig host) cts (Some(fun () -> WorkLease.acquire work))
        )

    waitForServer pipeName

    try
        let session = IpcClient.beginCheckSession pipeName |> Async.RunSynchronously
        IpcClient.getStatus pipeName |> Async.RunSynchronously |> ignore
        test <@ WorkLease.count work = 1 @>

        IpcClient.endCheckSession pipeName session |> Async.RunSynchronously |> ignore
        test <@ WorkLease.count work = 0 @>
    finally
        cts.Cancel()

        try
            server.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with :? AggregateException ->
            ()

[<Fact(Timeout = 120000)>]
let ``check returns a terminal verdict from a separately launched daemon process`` () =
    // This goes through the executable's normal `check` route, rather than calling
    // IpcClient in-process: that route owns the multi-RPC check session and only
    // releases it after it has rendered the daemon's terminal verdict.
    withTempDir "fshw-process-verdict" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "App")
        Directory.CreateDirectory(projDir) |> ignore
        // Program.findRepoRoot requires a VCS-root marker. Tree hashing is
        // filesystem-based, so a directory marker is sufficient for this fixture.
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore

        File.WriteAllText(
            Path.Combine(projDir, "App.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Program.fs" /></ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(projDir, "Program.fs"), "module App\nlet value = 42\n")

        // The fixture's tiny real build gives the daemon a terminal plugin and
        // satisfies its post-build artifact verifier. With no handlers at all,
        // WaitForComplete has no terminal cohort to observe.
        File.WriteAllText(
            Path.Combine(tmpDir, ".fshw.json"),
            """{"build":{"command":"dotnet","args":"build src/App/App.fsproj"},"format":false,"lint":false,"cache":false}"""
        )

        let restoreExit, _, restoreErr =
            runProcessIn "dotnet" projDir [ "restore"; "--nologo" ]

        Assert.True((restoreExit = 0), $"fixture restore failed (exit {restoreExit}): {restoreErr}")

        try
            let exitCode, stdout, stderr = runCliIn tmpDir [ "check" ]
            let verdictPath = Path.Combine(tmpDir, ".fshw", "verdict.json")

            Assert.True(
                (exitCode = 0),
                $"process check did not return green (exit {exitCode})\nstdout:\n{stdout}\nstderr:\n{stderr}"
            )

            Assert.True(File.Exists verdictPath, "process check returned without publishing verdict.json")

            use document = System.Text.Json.JsonDocument.Parse(File.ReadAllText verdictPath)

            let outcome =
                document.RootElement.GetProperty("outcome").GetProperty("kind").GetString()

            Assert.Equal("green", outcome)
        finally
            // The test owns this temporary daemon even when the assertion fails.
            try
                runCliIn tmpDir [ "stop" ] |> ignore
            with _ ->
                ())

[<Fact(Timeout = 30000)>]
let ``pressure idle exit waits for real cold ScanAll and check terminal response`` () =
    // Catches both omissions that made pressure reclaim a daemon mid-check: a cold
    // scan has no plugin transition yet, and a check spans multiple pipe requests.
    withTempDir "fshw-pressure-cold" (fun tmpDir ->
        let source = Path.Combine(tmpDir, "Cold.fs")
        let sourceText = "module Cold\nlet value = 42\n"
        File.WriteAllText(source, sourceText)

        let checker = sharedChecker.Value

        let projectOptions, _ =
            checker.GetProjectOptionsFromScript(source, FSharp.Compiler.Text.SourceText.ofString sourceText)
            |> Async.RunSynchronously

        let mutable capturedDeps: IdleExitDeps option = None

        let timerFactory (deps: IdleExitDeps) : IDisposable =
            capturedDeps <- Some deps

            { new IDisposable with
                member _.Dispose() = () }

        let daemon =
            Daemon.createWithIdleExitTimer
                checker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    IdleExitMin = Some 30
                    PressureIdleFloorMin = Some 2 }
                timerFactory

        daemon.RegisterProject(Path.Combine(tmpDir, "Cold.fsproj"), projectOptions)

        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        daemon.RegisterPreprocessor(BlockingPreprocessor(entered, release))

        use cts = new CancellationTokenSource()
        let pipeName = Program.computePipeName tmpDir
        let daemonTask = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))

        try
            waitForServer pipeName
            let session = IpcClient.beginCheckSession pipeName |> Async.RunSynchronously

            test <@ entered.Wait(TimeSpan.FromSeconds(8.0)) @>
            test <@ capturedDeps.IsSome @>

            let liveDeps = capturedDeps.Value

            let pressuredDeps =
                { liveDeps with
                    Now = fun () -> liveDeps.LastActivityAt().AddMinutes(3.0)
                    Pressure = fun () -> true
                    Shutdown = fun () -> cts.Cancel()
                    Log = ignore }

            let latch = FireLatch.create ()
            test <@ not (runTick pressuredDeps latch) @>
            test <@ not cts.IsCancellationRequested @>

            release.Set()
            IpcClient.waitForScan pipeName -1L |> Async.RunSynchronously |> ignore

            // The scan is terminal, but the client still owns the check transaction
            // until it has rendered the terminal verdict.
            test <@ not (runTick pressuredDeps latch) @>
            IpcClient.endCheckSession pipeName session |> Async.RunSynchronously |> ignore
            test <@ runTick pressuredDeps latch @>
            test <@ daemonTask.Wait(TimeSpan.FromSeconds(8.0)) @>
        finally
            release.Set()
            cts.Cancel()
            (daemon :> IDisposable).Dispose())

[<Fact(Timeout = 15000)>]
let ``server responds to GetStatus`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    // Register a plugin so there's something in status
    let handler =
        { Name = PluginName.create "test-plugin"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

    try
        let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result.Contains("test-plugin") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``server responds to RunCommand`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let handler =
        { Name = PluginName.create "greeter"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = [ "greet", fun _ctx _state _args -> async { return "hello world" } ]
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

    try
        let result = IpcClient.runCommand pipeName "greet" "" |> Async.RunSynchronously
        test <@ result.Contains("hello world") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``GetPluginStatus returns specific plugin's status`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let handler =
        { Name = PluginName.create "status-plugin"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

    try
        let result =
            IpcClient.getPluginStatus pipeName "status-plugin" |> Async.RunSynchronously

        let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses result
        test <@ parsed.ContainsKey("status-plugin") @>
        test <@ parsed.["status-plugin"].Status = StatusView.Idle @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``GetPluginStatus returns not found for unknown plugin`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

    try
        let result =
            IpcClient.getPluginStatus pipeName "nonexistent" |> Async.RunSynchronously

        let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses result
        test <@ Map.isEmpty parsed @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``RunCommand with plugin that returns a result`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let handler =
        { Name = PluginName.create "echo-plugin"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands =
            [ "echo",
              fun _ctx _state args ->
                  async {
                      let msg = if args.Length > 0 then args.[0] else "empty"
                      return $"echoed: {msg}"
                  } ]
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

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

[<Fact(Timeout = 20000)>]
let ``RunCommand returns unknown command for non-existent command`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

    try
        let result =
            IpcClient.runCommand pipeName "no-such-command" "" |> Async.RunSynchronously

        // 0.6.0 strict-CLI: unknown command comes back as a distinguishable JSON
        // sentinel (not a plain string) so the CLI can fail hard on it.
        test <@ isUnknownCommandReply result @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``GetStatus serializes multiple plugins with different statuses`` () =
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let makeStatusHandler name (reportFn: PluginCtx<unit> -> unit) =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> reportFn ctx
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeStatusHandler "idle-p" (fun ctx -> ctx.ReportStatus(Idle)))

    host.RegisterHandler(
        makeStatusHandler "running-p" (fun ctx -> ctx.ReportStatus(Running(since = System.DateTime(2025, 1, 1))))
    )

    host.RegisterHandler(
        makeStatusHandler "completed-p" (fun ctx -> ctx.ReportStatus(completedAt (System.DateTime(2025, 1, 2))))
    )

    host.RegisterHandler(
        makeStatusHandler "failed-p" (fun ctx ->
            ctx.ReportStatus(
                Failed("something broke", System.DateTime(2025, 1, 3), FsHotWatch.Tests.TestHelpers.testVerdict)
            ))
    )

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("failed-p") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

    try
        let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
        // Status field is a tagged JSON variant per plugin.
        test <@ result.Contains("idle-p") @>
        test <@ result.Contains("\"tag\":\"idle\"") @>
        test <@ result.Contains("running-p") @>
        test <@ result.Contains("\"tag\":\"running\"") @>
        test <@ result.Contains("completed-p") @>
        test <@ result.Contains("\"tag\":\"completed\"") @>
        test <@ result.Contains("failed-p") @>
        test <@ result.Contains("\"tag\":\"failed\"") @>
        test <@ result.Contains("something broke") @>

        // Round-trip: consumer parser should recover the original DU values.
        let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses result

        match parsed.["idle-p"].Status with
        | StatusView.Idle -> ()
        | other -> failwithf "expected Idle, got %A" other

        match parsed.["running-p"].Status with
        | StatusView.Running _ -> ()
        | other -> failwithf "expected Running, got %A" other

        match parsed.["completed-p"].Status with
        | StatusView.Completed _ -> ()
        | other -> failwithf "expected Completed, got %A" other

        match parsed.["failed-p"].Status with
        | StatusView.Failed(msg, _) -> test <@ msg = "something broke" @>
        | other -> failwithf "expected Failed, got %A" other
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

// `GetStatus without IPC serializes all status variants` lives in
// FsHotWatch.IntegrationTests — 4-handler dispatch flaked ~20% here.

// --- Wedge-aware status ---

[<Fact(Timeout = 15000)>]
let ``GetStatus splices a WEDGED report when the watchdog has a stuck op`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let clock = ref (DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))

    // Inject a watchdog with a tiny threshold and a stuck op (Begin, no End).
    use watchdog =
        new FsHotWatch.OperationWatchdog.Watchdog(
            TimeSpan.FromSeconds(1.0),
            heartbeatEvery = TimeSpan.FromHours(1.0),
            now = (fun () -> clock.Value),
            log = ignore,
            tick = TimeSpan.FromMilliseconds(50.0)
        )

    watchdog.Begin "RunCommand:run-tests" |> ignore
    // Advance the injected clock past the threshold so it reads as wedged.
    clock.Value <- clock.Value.AddSeconds 30.0

    let target = DaemonRpcTarget(defaultRpcConfig host, watchdog)
    let json = target.GetStatus()

    test <@ json.Contains("fshw-wedge") @>
    test <@ json.Contains("WEDGED:") @>
    test <@ json.Contains("RunCommand:run-tests") @>
    // Inline recovery action.
    test <@ json.Contains("fshw stop") @>

[<Fact(Timeout = 15000)>]
let ``GetStatus omits the wedge entry when no op is stuck`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    use watchdog =
        new FsHotWatch.OperationWatchdog.Watchdog(
            TimeSpan.FromSeconds(120.0),
            heartbeatEvery = TimeSpan.FromHours(1.0),
            now = (fun () -> DateTime.UtcNow),
            log = ignore,
            tick = TimeSpan.FromMilliseconds(50.0)
        )

    // No op in flight at all.
    let target = DaemonRpcTarget(defaultRpcConfig host, watchdog)
    let json = target.GetStatus()
    test <@ not (json.Contains("fshw-wedge")) @>
    test <@ not (json.Contains("WEDGED:")) @>

[<Fact(Timeout = 15000)>]
let ``ScanStatus prefixes the wedge report when a stuck op is in flight`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let clock = ref (DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))

    use watchdog =
        new FsHotWatch.OperationWatchdog.Watchdog(
            TimeSpan.FromSeconds(1.0),
            heartbeatEvery = TimeSpan.FromHours(1.0),
            now = (fun () -> clock.Value),
            log = ignore,
            tick = TimeSpan.FromMilliseconds(50.0)
        )

    watchdog.Begin "WaitForComplete" |> ignore
    clock.Value <- clock.Value.AddSeconds 30.0

    let config =
        { defaultRpcConfig host with
            GetScanStatus = fun () -> "complete: 10 files checked in 1.0s" }

    let target = DaemonRpcTarget(config, watchdog)
    let status = target.ScanStatus()
    test <@ status.StartsWith("WEDGED:") @>
    test <@ status.Contains("WaitForComplete") @>
    // The underlying scan line is still present.
    test <@ status.Contains("complete: 10 files") @>

[<Fact(Timeout = 30000)>]
let ``status stays responsive over a real pipe while another op is wedged`` () =
    // End-to-end acceptance: a real daemon over a real pipe, ONE RPC op blocked
    // indefinitely (a stuck WaitForComplete), and a concurrent `status` call must
    // STILL return — reporting the wedge + stuck op — instead of the consumer timing
    // out on a dead socket ("could not connect to daemon: operation timed out"). The
    // multi-acceptor server + watchdog make status land on a free acceptor.
    let pipeName = $"fshw-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()
    let blockForever = new TaskCompletionSource<unit>()

    let config =
        { defaultRpcConfig host with
            // A WaitForComplete that never resolves — the wedged op.
            WaitForAllTerminal = fun _ -> blockForever.Task }

    host.RegisterHandler(
        { Name = PluginName.create "p"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }
    )

    let serverTask = Async.StartAsTask(IpcServer.start pipeName config cts)
    waitForServer pipeName

    try
        // Park a client in WaitForComplete — this occupies one accept task and
        // never returns (the wedge).
        let wedged =
            Async.StartAsTask(
                async {
                    try
                        return! IpcClient.waitForComplete pipeName 0
                    with ex ->
                        return $"faulted: {ex.Message}"
                }
            )

        Thread.Sleep(500)

        if wedged.IsCompleted then
            failwithf "wedged WaitForComplete should still be parked, but completed with: %s" wedged.Result

        // The defining assertion: `status` must return within a bounded time.
        let statusTask = Async.StartAsTask(async { return! IpcClient.getStatus pipeName })

        let completed = statusTask.Wait(TimeSpan.FromSeconds(8.0))
        test <@ completed @>
        // The wedged op is still parked (we didn't accidentally unblock it).
        test <@ not wedged.IsCompleted @>
        // status returned a real payload (the registered plugin is present).
        test <@ statusTask.Result.Contains("\"p\"") @>
    finally
        blockForever.TrySetResult(()) |> ignore
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.RunCommand returns unknown command for missing command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let target = DaemonRpcTarget(defaultRpcConfig host)

    let result =
        target.RunCommand("nonexistent", "")
        |> Async.AwaitTask
        |> Async.RunSynchronously

    test <@ isUnknownCommandReply result @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.RunCommand returns result for known command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        { Name = PluginName.create "cmd-test"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands =
            [ "hello",
              fun _ctx _state args ->
                  async {
                      let arg = if args.Length > 0 then args.[0] else "world"
                      return $"hello {arg}"
                  } ]
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let target = DaemonRpcTarget(defaultRpcConfig host)

    let result1 =
        target.RunCommand("hello", "") |> Async.AwaitTask |> Async.RunSynchronously

    test <@ result1 = "hello world" @>

    // Exercises the else branch of argsJson parsing.
    let result2 =
        target.RunCommand("hello", "test-arg")
        |> Async.AwaitTask
        |> Async.RunSynchronously

    test <@ result2 = "hello test-arg" @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.GetPluginStatus returns status strings for each variant`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let makeStatusHandler name (reportFn: PluginCtx<unit> -> unit) =
        { Name = PluginName.create name
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> reportFn ctx
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(makeStatusHandler "idle-test" (fun ctx -> ctx.ReportStatus(Idle)))

    host.RegisterHandler(
        makeStatusHandler "failed-test" (fun ctx ->
            ctx.ReportStatus(Failed("bad", System.DateTime(2025, 1, 1), FsHotWatch.Tests.TestHelpers.testVerdict)))
    )

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("failed-test") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let target = DaemonRpcTarget(defaultRpcConfig host)

    let getParsed name =
        FsHotWatch.Tests.TestHelpers.parseStatuses (target.GetPluginStatus(name))

    test <@ (getParsed "idle-test").["idle-test"].Status = StatusView.Idle @>

    match (getParsed "failed-test").["failed-test"].Status with
    | StatusView.Failed(msg, _) -> test <@ msg = "bad" @>
    | other -> failwithf "expected Failed, got %A" other

    test <@ Map.isEmpty (getParsed "no-such") @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``WaitForScan legacy path resolves immediately on hot daemon`` () =
    // Regression: WaitForGeneration(-1, currentGen>0) must not hang. On a hot daemon
    // the scan already completed, so the legacy path returns immediately.
    let signal = FsHotWatch.Daemon.ScanSignal()
    let task = signal.WaitForGeneration(-1L, 1L)
    test <@ task.IsCompleted @>

[<Fact(Timeout = 15000)>]
let ``WaitForScan legacy path blocks on cold daemon`` () =
    // Cold daemon = generation 0.
    let signal = FsHotWatch.Daemon.ScanSignal()
    let task = signal.WaitForGeneration(-1L, 0L)
    test <@ not task.IsCompleted @>
    // Post is fire-and-forget, so wait for completion rather than asserting at once.
    signal.SignalGeneration(1L)
    task.Wait(System.TimeSpan.FromSeconds(5.0)) |> ignore
    test <@ task.IsCompleted @>

[<Fact(Timeout = 15000)>]
let ``WaitForGeneration does not hang when scan completes before waiter is registered`` () =
    // Race: the client reads currentGeneration=0, then before posting WaitFor to the
    // agent the scan completes and SignalGeneration(1) fires. Without latching the
    // latest seen generation in the agent, WaitFor arrives after Signal was processed
    // (no waiters present) and hangs forever despite the scan being done.
    let signal = FsHotWatch.Daemon.ScanSignal()
    // Simulate: signal arrives BEFORE waiter registration.
    signal.SignalGeneration(1L)
    // Caller still sees stale currentGeneration=0 (Volatile.Write happens after
    // SignalGeneration in performScan).
    let task = signal.WaitForGeneration(-1L, 0L)
    task.Wait(System.TimeSpan.FromSeconds(2.0)) |> ignore
    test <@ task.IsCompleted @>

[<Fact(Timeout = 15000)>]
let ``WaitForComplete resolves when all plugins terminal`` () =
    let tcs = TaskCompletionSource<unit>()
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            WaitForAllTerminal = fun _ -> tcs.Task }

    let target = DaemonRpcTarget(config)
    let waitTask = target.WaitForComplete(0)

    test <@ not waitTask.IsCompleted @>

    tcs.SetResult(())

    let result = waitTask.Result
    // Should return status JSON
    test <@ result.Contains("{") @>

// --- DaemonRpcTarget unit tests (no IPC pipe) ---

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.Shutdown calls RequestShutdown and returns message`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable called = false

    let config =
        { defaultRpcConfig host with
            RequestShutdown = fun () -> called <- true }

    let target = DaemonRpcTarget(config)
    let result = target.Shutdown()
    test <@ result = "shutting down" @>
    test <@ called @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.Scan returns generation and calls RequestScan`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable called = false

    let config =
        { defaultRpcConfig host with
            GetScanGeneration = fun () -> 42L
            RequestScan = fun () -> called <- true }

    let target = DaemonRpcTarget(config)

    let result = target.Scan()
    test <@ result = "scan started:42" @>
    test <@ called @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.ScanStatus delegates to GetScanStatus`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            GetScanStatus = fun () -> "complete: 70 files checked in 15.5s" }

    let target = DaemonRpcTarget(config)
    test <@ target.ScanStatus() = "complete: 70 files checked in 15.5s" @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.Invalidate awaits cache invalidation and acknowledges it`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable called = false

    let config =
        { defaultRpcConfig host with
            InvalidateCache = fun () -> task { called <- true } }

    let target = DaemonRpcTarget(config)
    test <@ target.Invalidate().Result = "invalidated" @>
    test <@ called @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.Invalidate is bounded by the RPC seam`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            InvalidateCache = fun () -> TaskCompletionSource<unit>().Task }

    let target = DaemonRpcTarget(config, deadline = TimeSpan.FromMilliseconds 300.0)
    let ex = Assert.Throws<AggregateException>(fun () -> target.Invalidate().Wait())
    Assert.IsType<TimeoutException>(ex.InnerException) |> ignore
    test <@ ex.InnerException.Message.Contains("Invalidate") @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.GetDiagnostics returns all errors when filter is empty`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    host.ReportErrors(
        "error-plugin",
        "/tmp/test.fs",
        [ { Message = "bad code"
            Severity = DiagnosticSeverity.Error
            Line = 10
            Column = 5
            Detail = None } ]
    )

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetDiagnostics("")
    test <@ json.Contains("\"count\":1") @>
    test <@ json.Contains("bad code") @>
    test <@ json.Contains("error-plugin") @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.GetDiagnostics filters by plugin name`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    host.ReportErrors(
        "lint",
        "/tmp/a.fs",
        [ { Message = "lint issue"
            Severity = DiagnosticSeverity.Warning
            Line = 1
            Column = 0
            Detail = None } ]
    )

    host.ReportErrors(
        "analyzers",
        "/tmp/b.fs",
        [ { Message = "analyzer issue"
            Severity = DiagnosticSeverity.Info
            Line = 2
            Column = 0
            Detail = None } ]
    )

    let target = DaemonRpcTarget(defaultRpcConfig host)

    let lintJson = target.GetDiagnostics("lint")
    test <@ lintJson.Contains("lint issue") @>
    test <@ not (lintJson.Contains("analyzer issue")) @>

    let analyzerJson = target.GetDiagnostics("analyzers")
    test <@ analyzerJson.Contains("analyzer issue") @>
    test <@ not (analyzerJson.Contains("lint issue")) @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.GetDiagnostics returns zero count when no errors`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetDiagnostics("")
    test <@ json.Contains("\"count\":0") @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.GetDiagnostics includes detail field`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    host.ReportErrors(
        "test-prune",
        "/tmp/Tests.fs",
        [ ErrorEntry.errorWithDetail
              "failed MyTests.test1 (5ms)"
              "full stdout\nprintln debug: x = 42\nfailed MyTests.test1 (5ms)" ]
    )

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetDiagnostics("")
    // The full output, not just the message.
    test <@ json.Contains("\"detail\"") @>
    test <@ json.Contains("println debug: x = 42") @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.GetDiagnostics includes detail when filtered by plugin`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    host.ReportErrors(
        "test-prune",
        "/tmp/Tests.fs",
        [ ErrorEntry.errorWithDetail "failed MyTests.test1 (5ms)" "full output with debug info" ]
    )

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetDiagnostics("test-prune")
    test <@ json.Contains("\"detail\"") @>
    test <@ json.Contains("full output with debug info") @>

[<Fact(Timeout = 20000)>]
let ``DaemonRpcTarget.TriggerBuild calls config and returns status`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable buildCalled = false

    let config =
        { defaultRpcConfig host with
            TriggerBuild =
                fun () ->
                    async {
                        buildCalled <- true
                        return ()
                    } }

    let target = DaemonRpcTarget(config)
    let result = target.TriggerBuild().Result
    test <@ buildCalled @>
    test <@ result.Contains("{") @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.FormatAll delegates to config`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            FormatAll = fun () -> async { return "formatted 5 files" } }

    let target = DaemonRpcTarget(config)
    let result = target.FormatAll().Result
    test <@ result = "formatted 5 files" @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.RerunPlugin delegates to config`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let mutable capturedName = ""

    let config =
        { defaultRpcConfig host with
            RerunPlugin =
                fun name ->
                    async {
                        capturedName <- name
                        return Result.Ok()
                    } }

    let target = DaemonRpcTarget(config)
    let result = target.RerunPlugin("coverage-ratchet").Result
    // Returns GetStatus payload (empty map "{}" when no plugins registered aside from mocks).
    test <@ result = "{}" @>
    test <@ capturedName = "coverage-ratchet" @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.RerunPlugin returns error payload when plugin has no pattern`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            RerunPlugin = fun _ -> async { return Result.Error "Plugin 'missing' has no registered file pattern" } }

    let target = DaemonRpcTarget(config)
    let result = target.RerunPlugin("missing").Result
    test <@ result.Contains("error") @>
    test <@ result.Contains("missing") @>

[<Fact(Timeout = 15000)>]
let ``DaemonRpcTarget.GetDiagnostics includes plugin statuses in response`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        { Name = PluginName.create "test-prune"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ ->
                        ctx.ReportStatus(
                            Failed(
                                "2 failed: Foo.Tests, Bar.Tests",
                                System.DateTime(2025, 1, 1),
                                FsHotWatch.Tests.TestHelpers.testVerdict
                            )
                        )
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("test-prune") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let target = DaemonRpcTarget(defaultRpcConfig host)
    let json = target.GetDiagnostics("")
    test <@ json.Contains("\"count\":0") @>
    test <@ json.Contains("\"statuses\"") @>
    test <@ json.Contains("test-prune") @>
    test <@ json.Contains("\"tag\":\"failed\"") @>

    let resp = FsHotWatch.Cli.IpcParsing.parseDiagnosticsResponse json

    match resp.Statuses.["test-prune"].Status with
    | StatusView.Failed(msg, _) -> test <@ msg.Contains("2 failed") @>
    | other -> failwithf "expected Failed, got %A" other

[<Fact(Timeout = 20000)>]
let ``WaitForComplete times out when plugin stays Running`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        { Name = PluginName.create "stuck-plugin"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = System.DateTime(2025, 1, 1)))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("stuck-plugin") with
            | Some(Running _) -> true
            | _ -> false)
        5000

    let config =
        { defaultRpcConfig host with
            WaitForAllTerminal = fun timeout -> waitForAllTerminal host timeout System.Threading.CancellationToken.None }

    let target = DaemonRpcTarget(config)

    let ex =
        Assert.ThrowsAsync<System.TimeoutException>(fun () -> target.WaitForComplete(200) :> Task)
        |> Async.AwaitTask
        |> Async.RunSynchronously

    test <@ ex.Message.Contains("timed out") @>

[<Fact(Timeout = 30000)>]
let ``WaitForComplete client observes failure when daemon is shut down mid-wait`` () =
    // Real IPC over a real named pipe: the client blocks in WaitForComplete on a
    // stuck-Running plugin, we cancel the server CTS, and the client must observe a
    // failure within a bounded time. Catches regressions where the daemon-side wait
    // stops observing the shutdown token and the client hangs, or races OS pipe
    // teardown into a clean exit.
    let pipeName = $"fshw-test-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        { Name = PluginName.create "stuck-plugin"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = System.DateTime(2025, 1, 1)))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("stuck-plugin") with
            | Some(Running _) -> true
            | _ -> false)
        5000

    let cts = new CancellationTokenSource()

    let config =
        { defaultRpcConfig host with
            RequestShutdown = fun () -> cts.Cancel()
            WaitForAllTerminal = fun timeout -> waitForAllTerminal host timeout cts.Token }

    let serverTask = Async.StartAsTask(IpcServer.start pipeName config cts)
    waitForServer pipeName

    try
        let clientTask =
            Async.StartAsTask(
                async {
                    try
                        let! result = IpcClient.waitForComplete pipeName 0
                        return Choice1Of2 result
                    with ex ->
                        return Choice2Of2 ex
                }
            )

        // Give the client time to establish the connection and enter the wait.
        Thread.Sleep(500)
        test <@ not clientTask.IsCompleted @>

        // Simulate daemon shutdown.
        cts.Cancel()

        // The contract: the client must observe a failure within a bounded
        // time. 8 seconds is generous — production teardown is sub-second.
        let completed = clientTask.Wait(TimeSpan.FromSeconds(8.0))
        test <@ completed @>

        match clientTask.Result with
        | Choice1Of2 result ->
            // Silent success would let `fshw errors --wait` exit 0 after the daemon
            // went away.
            failwithf "client returned success after daemon shutdown: %s" result
        | Choice2Of2 _ -> ()
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

[<Fact(Timeout = 20000)>]
let ``repeated scan force via IPC increments generation each time`` () =
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fshw-rescan-{Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpDir, "src"))
    |> ignore

    let pipeName = FsHotWatch.Cli.Program.computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

    let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    waitForServer pipeName

    // Wait for initial scan to complete (gen=1)
    let waitResult = IpcClient.waitForScan pipeName -1L |> Async.RunSynchronously
    let gen1 = daemon.GetScanGeneration()
    test <@ gen1 >= 1L @>

    try
        let scanResult1 = IpcClient.scan pipeName |> Async.RunSynchronously
        test <@ scanResult1.Contains("scan started") @>

        IpcClient.waitForScan pipeName gen1 |> Async.RunSynchronously |> ignore
        let gen2 = daemon.GetScanGeneration()
        test <@ gen2 > gen1 @>

        // Second scan — the one that was reported as broken.
        let scanResult2 = IpcClient.scan pipeName |> Async.RunSynchronously
        test <@ scanResult2.Contains("scan started") @>

        IpcClient.waitForScan pipeName gen2 |> Async.RunSynchronously |> ignore
        let gen3 = daemon.GetScanGeneration()
        test <@ gen3 > gen2 @>
    finally
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with _ ->
            ()

        if System.IO.Directory.Exists tmpDir then
            System.IO.Directory.Delete(tmpDir, true)

[<Fact(Timeout = 30000)>]
let ``WaitForScan client observes failure when daemon is shut down mid-wait`` () =
    // Companion to the WaitForComplete shutdown test, for the OTHER blocking wait: a
    // client parked in WaitForScan for a generation that will never arrive (no scan is
    // triggered), then the daemon's CTS is cancelled. Without the shutdown-token race
    // in WaitForScanGeneration the RPC could resolve cleanly during teardown and
    // `fshw scan --wait` would exit 0 after the daemon went away.
    let tmpDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fshw-scanwait-{Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpDir, "src"))
    |> ignore

    let pipeName = FsHotWatch.Cli.Program.computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

    let serverTask = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    waitForServer pipeName

    // Let the initial scan settle so the next WaitForScan genuinely blocks.
    IpcClient.waitForScan pipeName -1L |> Async.RunSynchronously |> ignore
    let currentGen = daemon.GetScanGeneration()

    try
        // Park in WaitForScan for a generation past the current one. No scan is
        // triggered, so this only unblocks on shutdown.
        let clientTask =
            Async.StartAsTask(
                async {
                    try
                        let! result = IpcClient.waitForScan pipeName currentGen
                        return Choice1Of2 result
                    with ex ->
                        return Choice2Of2 ex
                }
            )

        Thread.Sleep(500)
        test <@ not clientTask.IsCompleted @>

        // Simulate daemon shutdown.
        cts.Cancel()

        let completed = clientTask.Wait(TimeSpan.FromSeconds(8.0))
        test <@ completed @>

        match clientTask.Result with
        | Choice1Of2 result -> failwithf "client returned success after daemon shutdown: %s" result
        | Choice2Of2 _ -> ()
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with _ ->
            ()

        if System.IO.Directory.Exists tmpDir then
            System.IO.Directory.Delete(tmpDir, true)

// --- Listener-wedge survival under malformed client traffic ---
//
// When a client connection faults StreamJsonRpc's read path (e.g. an
// `OverflowException` from a malformed/oversized `Content-Length` header),
// `IpcServer.acceptOne` must dispose just that connection and let the outer accept
// loop spawn a replacement listener, keeping subsequent well-formed RPC calls
// working. A server wedged after one bad message makes every later CLI invocation
// surface the same error — what the Intelligence Phase D stress test reported.

/// Connect raw to the named pipe, write garbage, close. Returns true if the raw
/// connect/write/close succeeded — the *server's* response is whatever the rpc layer
/// does with our malformed bytes.
let private writeMalformedFrame (pipeName: string) (payload: byte[]) : bool =
    try
        use pipeClient =
            new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)

        pipeClient.ConnectAsync(2000).Wait()
        pipeClient.Write(payload, 0, payload.Length)
        pipeClient.Flush()
        true
    with _ ->
        false

[<Fact(Timeout = 30000)>]
let ``server keeps accepting connections after a malformed-frame client`` () =
    let pipeName = $"fshw-wedge-{Guid.NewGuid():N}"
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let cts = new CancellationTokenSource()

    let handler =
        { Name = PluginName.create "wedge-test"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    host.RegisterHandler(handler)

    let serverTask =
        Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

    waitForServer pipeName

    try
        // Sanity: server is up and answering well-formed traffic.
        let beforeStatus = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ beforeStatus.Contains("wedge-test") @>

        // Frames StreamJsonRpc might trip an OverflowException on:
        //   1. Content-Length larger than fits in Int32 → integer-parse OF.
        //   2. Random binary noise (no header at all).
        //   3. Truncated header with no body.
        let malformedFrames =
            [ Encoding.ASCII.GetBytes("Content-Length: 999999999999999\r\n\r\n{}")
              [| 0xFFuy; 0xFEuy; 0x00uy; 0x01uy; 0x02uy; 0x03uy |]
              Encoding.ASCII.GetBytes("Content-Length: -1\r\n") ]

        for frame in malformedFrames do
            writeMalformedFrame pipeName frame |> ignore
            // Give the server a moment to react / dispose.
            Thread.Sleep(50)

        // A well-formed RPC after the garbage MUST still succeed; a wedged listener
        // times out or throws here.
        let afterStatus = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ afterStatus.Contains("wedge-test") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

// === Regression: the accept loop must not silently swallow acceptor faults ===
//
// A pipe name that (with the runtime's `CoreFxPipe_` temp-dir prefix) exceeds the
// platform's Unix-domain-socket path limit makes the NamedPipeServerStream
// constructor throw deterministically — a PERSISTENT bind failure. The old accept
// loop's `| _ -> ()` (and the unobserved faulted task handed back by Task.WhenAny)
// silently respawned acceptors in a tight spin with no diagnostic at all. The loop
// must log the fault loudly (and back off) while still shutting down cleanly on
// cancellation.
//
// Touches Logging.logLevel + Console.Error → joins the LogGlobal serialized
// collection (see TestHelpers).
[<Collection(LogGlobalCollectionName)>]
type AcceptLoopFaultTests() =

    [<Fact(Timeout = 20000)>]
    member _.``accept loop logs pipe-bind failures instead of swallowing them``() =
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
        // Long enough that the socket path exceeds the 104-char macOS limit
        // (and Linux's 108) once the temp-dir prefix is prepended.
        let pipeName = String.replicate 200 "x"

        let original = FsHotWatch.Logging.logLevel
        let sb = StringBuilder()
        let writer = new IO.StringWriter(sb)
        let prevErr = Console.Error
        use cts = new CancellationTokenSource()

        try
            Console.SetError(writer)
            FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error

            let serverTask =
                Async.StartAsTask(IpcServer.start pipeName (defaultRpcConfig host) cts)

            // The bind failure is persistent: the loop must observe the faulted
            // acceptor and log it (old code: nothing is ever logged).
            waitUntil
                (fun () ->
                    writer.Flush()
                    sb.ToString().Contains("accept task faulted"))
                10000

            cts.Cancel()

            // Loud-but-alive: the fault must not have killed the loop before
            // cancellation, and cancellation must still shut it down cleanly.
            let exited =
                try
                    serverTask.Wait(TimeSpan.FromSeconds(5.0))
                with :? AggregateException ->
                    serverTask.IsCompleted

            writer.Flush()
            let output = sb.ToString()
            test <@ output.Contains("accept task faulted") @>
            test <@ exited @>
        finally
            cts.Cancel()
            Console.SetError(prevErr)
            FsHotWatch.Logging.setLogLevel original

// --- resolveVerdictDeadline: override precedence (pure — no process env touched) ---
// Regression for the test-prune wedge: a client-unbounded WaitForComplete used to
// become TimeSpan.MaxValue — an INFINITE wait that heartbeat-logged for 8h36m while a
// plugin was wedged. The daemon now always applies a finite bound; there is
// deliberately no "infinite" setting.

[<Fact(Timeout = 15000)>]
let ``resolveVerdictDeadline: absent override falls back to the default`` () =
    Assert.Equal(DefaultVerdictDeadline, resolveVerdictDeadline None)

[<Fact(Timeout = 15000)>]
let ``resolveVerdictDeadline: a positive integer override wins`` () =
    Assert.Equal(TimeSpan.FromSeconds 7200.0, resolveVerdictDeadline (Some "7200"))

[<Theory(Timeout = 15000)>]
[<InlineData("0")>]
[<InlineData("-5")>]
[<InlineData("nonsense")>]
[<InlineData("")>]
let ``resolveVerdictDeadline: junk / non-positive override falls back to the default`` (value: string) =
    Assert.Equal(DefaultVerdictDeadline, resolveVerdictDeadline (Some value))

// ---------------------------------------------------------------------------
// EVERY tracked RPC is bounded, at the seam.
// ---------------------------------------------------------------------------
//
// The regression this pins: `WaitForScan` had NO deadline. `WaitForScanGeneration`
// raced only daemon shutdown, never a clock; the CLI passes `-1L` (including on
// every convergence re-scan); and it is `check`'s FIRST step. So any hang inside
// `performScan` — the Fantomas preprocessor, an FCS `ParseAndCheckFileInProject` —
// meant "Scanning…" forever: no timeout, no error, no verdict. The 8h36m wedge, on
// a path nobody had bounded.
//
// The fix is at the `trackedTask` SEAM rather than in `WaitForScan`, so the property
// holds for every bracketed RPC — including ones not yet written. These tests assert
// the SEAM, using an RPC (`WaitForScan`) whose own body contains no timeout code.
//
// RED-BEFORE-GREEN: drop the deadline race from `trackedTask` and the first test
// hangs until xUnit kills it at 15s.

[<Fact(Timeout = 15000)>]
let ``an RPC whose work never completes faults with TimeoutException at the seam`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    // A scan that never signals — a Fantomas preprocessor wedged inside performScan.
    let config =
        { defaultRpcConfig host with
            WaitForScanGeneration = fun _ -> TaskCompletionSource<unit>().Task }

    let target = DaemonRpcTarget(config, deadline = TimeSpan.FromMilliseconds 300.0)

    let ex = Assert.Throws<AggregateException>(fun () -> target.WaitForScan(-1L).Wait())

    let inner = ex.InnerException
    Assert.IsType<TimeoutException>(inner) |> ignore
    test <@ inner.Message.Contains("WaitForScan") @>
    // The wedge report's inline recovery rides along, so the client knows what to do.
    test <@ inner.Message.Contains("fshw stop") @>

[<Fact(Timeout = 15000)>]
let ``an RPC that completes inside the deadline returns normally`` () =
    // The seam must not fire on healthy work — the deadline is a backstop, not a
    // budget.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let config =
        { defaultRpcConfig host with
            GetScanStatus = fun () -> "complete: 3 files checked in 0.1s" }

    let target = DaemonRpcTarget(config, deadline = TimeSpan.FromSeconds 10.0)
    let result = target.WaitForScan(-1L).Result
    test <@ result = "complete: 3 files checked in 0.1s" @>

[<Fact(Timeout = 15000)>]
let ``the seam refuses an infinite deadline rather than obeying it`` () =
    // The one way left to ask for an unbounded RPC is to pass an infinite deadline to
    // the seam. It is not honoured: it falls back to the ambient deadline.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let target =
        DaemonRpcTarget(defaultRpcConfig host, deadline = Timeout.InfiniteTimeSpan)

    // Healthy work still returns — the fallback deadline is finite but ample, not zero.
    test <@ target.WaitForScan(-1L).Result = "idle" @>
