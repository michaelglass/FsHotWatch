module FsHotWatch.Tests.IpcTests

open System
open System.IO.Pipes
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Ipc
open FsHotWatch.Cli
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework
open FsHotWatch.Events
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

/// Poll until IPC server is accepting connections.
let private waitForServer (pipeName: string) =
    waitUntil
        (fun () ->
            try
                IpcClient.getStatus pipeName |> Async.RunSynchronously |> ignore
                true
            with _ ->
                false)
        5000

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
      GetUncheckedCount = fun () -> 0 }

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

        let parsed = IpcParsing.parsePluginStatuses result
        test <@ parsed.ContainsKey("status-plugin") @>
        test <@ parsed.["status-plugin"].Status = Idle @>
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

        let parsed = IpcParsing.parsePluginStatuses result
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
        makeStatusHandler "completed-p" (fun ctx -> ctx.ReportStatus(Completed(System.DateTime(2025, 1, 2))))
    )

    host.RegisterHandler(
        makeStatusHandler "failed-p" (fun ctx ->
            ctx.ReportStatus(Failed("something broke", System.DateTime(2025, 1, 3))))
    )

    // Trigger status updates
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Wait for all plugins to process their events
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
        let parsed = FsHotWatch.Cli.IpcParsing.parsePluginStatuses result

        match parsed.["idle-p"].Status with
        | Idle -> ()
        | other -> failwithf "expected Idle, got %A" other

        match parsed.["running-p"].Status with
        | Running _ -> ()
        | other -> failwithf "expected Running, got %A" other

        match parsed.["completed-p"].Status with
        | Completed _ -> ()
        | other -> failwithf "expected Completed, got %A" other

        match parsed.["failed-p"].Status with
        | Failed(msg, _) -> test <@ msg = "something broke" @>
        | other -> failwithf "expected Failed, got %A" other
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

// `DaemonRpcTarget.GetStatus without IPC serializes all status variants`
// moved to FsHotWatch.IntegrationTests 2026-04-30 — flaked under load
// (~20% rate locally), 4-handler dispatch is integration-grade.

// --- Wedge-aware status (AUTOMATION-15 item 4) ---

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

    watchdog.Begin "RunCommand:run-tests"
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

    watchdog.Begin "WaitForComplete"
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
    // The end-to-end acceptance: a real daemon over a real pipe, ONE RPC op
    // blocked indefinitely (a stuck WaitForComplete), and a concurrent `status`
    // call must STILL return — reporting the wedge + stuck op — instead of the
    // consumer blindly timing out on a dead socket. This is what 2026-06-16's
    // "could not connect to daemon: operation timed out" looked like; the
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

        // The defining assertion: a concurrent `status` over the same pipe MUST
        // still return within a bounded time, not hang on the wedged op.
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
        makeStatusHandler "failed-test" (fun ctx -> ctx.ReportStatus(Failed("bad", System.DateTime(2025, 1, 1))))
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
        IpcParsing.parsePluginStatuses (target.GetPluginStatus(name))

    test <@ (getParsed "idle-test").["idle-test"].Status = Idle @>

    match (getParsed "failed-test").["failed-test"].Status with
    | Failed(msg, _) -> test <@ msg = "bad" @>
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
    // Regression: WaitForGeneration(-1, currentGen>0) must not hang.
    // On a hot daemon the scan already completed (generation=1+),
    // so the legacy path (afterGeneration=-1) should return immediately.
    let signal = FsHotWatch.Daemon.ScanSignal()
    let task = signal.WaitForGeneration(-1L, 1L)
    test <@ task.IsCompleted @>

[<Fact(Timeout = 15000)>]
let ``WaitForScan legacy path blocks on cold daemon`` () =
    // On a cold daemon (generation=0), the legacy path should block
    // until a scan completes.
    let signal = FsHotWatch.Daemon.ScanSignal()
    let task = signal.WaitForGeneration(-1L, 0L)
    test <@ not task.IsCompleted @>
    // Signal generation 1 — should resolve (Post is fire-and-forget, so wait for completion)
    signal.SignalGeneration(1L)
    task.Wait(System.TimeSpan.FromSeconds(5.0)) |> ignore
    test <@ task.IsCompleted @>

[<Fact(Timeout = 15000)>]
let ``WaitForGeneration does not hang when scan completes before waiter is registered`` () =
    // Race: client reads currentGeneration=0, then before posting WaitFor to the
    // agent the scan completes and SignalGeneration(1) fires. Without latching
    // the latest seen generation in the agent, the WaitFor message arrives after
    // Signal has already been processed (no waiters present), and the waiter
    // hangs forever despite the scan being done.
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
    // Verify the detail field is present with the full output, not just the message
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
                        ctx.ReportStatus(Failed("2 failed: Foo.Tests, Bar.Tests", System.DateTime(2025, 1, 1)))
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
    | Failed(msg, _) -> test <@ msg.Contains("2 failed") @>
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

    // Put plugin into Running state
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
    // Drives the real IPC over a real named pipe: client blocks in
    // WaitForComplete on a stuck-Running plugin, we cancel the server CTS,
    // the client must observe a failure within a bounded time. Catches
    // regressions where the daemon-side wait stops observing the shutdown
    // token and the client hangs (in-process callers) or races OS pipe
    // teardown for a clean exit.
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
            // Silent success would let `fshw errors --wait` exit 0 even though
            // the daemon went away. That's the bug we want to prevent.
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
        // First scan
        let scanResult1 = IpcClient.scan pipeName |> Async.RunSynchronously
        test <@ scanResult1.Contains("scan started") @>

        // Wait for this scan to complete
        IpcClient.waitForScan pipeName gen1 |> Async.RunSynchronously |> ignore
        let gen2 = daemon.GetScanGeneration()
        test <@ gen2 > gen1 @>

        // Second scan — this is the one that was reported as broken
        let scanResult2 = IpcClient.scan pipeName |> Async.RunSynchronously
        test <@ scanResult2.Contains("scan started") @>

        // Wait for this scan to complete
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
    // Companion to the WaitForComplete shutdown test, for the OTHER blocking
    // wait: a real daemon over a real pipe, a client parked in WaitForScan for
    // a generation that will never arrive (no scan is triggered), then the
    // daemon's CTS is cancelled. The client must observe a failure — without
    // the shutdown-token race in WaitForScanGeneration, the RPC could resolve
    // cleanly during teardown and `fshw scan --wait` would exit 0 even though
    // the daemon went away.
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
// Hypothesis (from rpc-overflow investigation): when a client connection
// triggers an exception inside StreamJsonRpc's read path (e.g. an
// `OverflowException` arising from a malformed/oversized `Content-Length`
// header), the daemon-side `IpcServer.acceptOne` *should* dispose just that
// connection and let the outer accept loop spawn a replacement listener,
// keeping subsequent well-formed RPC calls working.
//
// If the server instead got wedged after one bad message, every subsequent
// CLI invocation would surface the same error — which is what the
// Intelligence Phase D stress test reported. This test exercises the
// recovery path deterministically without a 7.8 GB daemon.

/// Connect raw to the named pipe, write garbage, close. Returns true if the
/// raw connect/write/close succeeded — the *server's* response is whatever
/// the rpc layer does with our malformed bytes.
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

        // Hit the server with several rounds of garbage, mimicking the kinds
        // of frames StreamJsonRpc might trip an OverflowException on:
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

        // The whole point: a well-formed RPC after the garbage MUST still
        // succeed. If the listener was wedged we'd time out or get an
        // exception here.
        let afterStatus = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ afterStatus.Contains("wedge-test") @>
    finally
        cts.Cancel()

        try
            serverTask.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

// === Item 3 regression: accept loop must not silently swallow acceptor faults ===
//
// A pipe name that (with the runtime's `CoreFxPipe_` temp-dir prefix) exceeds
// the platform's Unix-domain-socket path limit makes the NamedPipeServerStream
// constructor throw deterministically — a PERSISTENT bind failure. On the old
// code the accept loop's `| _ -> ()` (and the unobserved faulted task handed
// back by Task.WhenAny) silently respawned acceptors in a tight spin with no
// diagnostic at all. The loop must log the fault loudly (and back off) while
// still shutting down cleanly on cancellation.
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
// Regression for the 2026-07-13 test-prune wedge: a client-unbounded
// WaitForComplete used to become TimeSpan.MaxValue — an INFINITE wait that
// heartbeat-logged for 8h36m while a plugin was wedged. The daemon now always
// applies a finite bound; there is deliberately no "infinite" setting.

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
