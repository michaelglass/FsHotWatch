module FsHotWatch.Tests.CliTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open CommandTree
open FsHotWatch.Cli.Program
open FsHotWatch.Daemon
open FsHotWatch.Ipc
open FsHotWatch.PluginFramework
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

/// Poll until IPC server is accepting connections.
let private waitForIpcServer (pipeName: string) =
    waitUntil
        (fun () ->
            try
                IpcClient.getStatus pipeName |> Async.RunSynchronously |> ignore
                true
            with _ ->
                false)
        5000

// --- CommandTree.parse tests ---

let tree = FsHotWatch.Cli.Program.commandTree
let spec = FsHotWatch.Cli.Program.globalSpec

[<Fact>]
let ``parse empty args returns HelpRequested`` () =
    match CommandTree.parse tree [||] with
    | Error(HelpRequested _) -> ()
    | other -> failwith $"Expected HelpRequested, got %A{other}"

[<Fact>]
let ``parse start returns Start`` () =
    test <@ CommandTree.parse tree [| "start" |] = Ok Start @>

[<Fact>]
let ``parse stop returns Stop`` () =
    test <@ CommandTree.parse tree [| "stop" |] = Ok Stop @>

[<Fact>]
let ``parse check returns Check with no flags`` () =
    test <@ CommandTree.parse tree [| "check" |] = Ok(Check []) @>

[<Fact>]
let ``parse check --run-once returns Check RunOnce`` () =
    test <@ CommandTree.parse tree [| "check"; "--run-once" |] = Ok(Check [ RunOnce ]) @>

[<Fact>]
let ``parse build returns Build with no flags`` () =
    test <@ CommandTree.parse tree [| "build" |] = Ok(Build []) @>

[<Fact>]
let ``parse build --run-once returns Build RunOnce`` () =
    test <@ CommandTree.parse tree [| "build"; "--run-once" |] = Ok(Build [ RunOnce ]) @>

[<Fact>]
let ``parse test returns Test with no flags`` () =
    test <@ CommandTree.parse tree [| "test" |] = Ok(Test []) @>

[<Fact>]
let ``parse test --run-once returns Test RunOnce`` () =
    test <@ CommandTree.parse tree [| "test"; "--run-once" |] = Ok(Test [ RunOnce ]) @>

[<Fact>]
let ``parse format returns Format with no flags`` () =
    test <@ CommandTree.parse tree [| "format" |] = Ok(Format []) @>

[<Fact>]
let ``parse lint returns Lint with no flags`` () =
    test <@ CommandTree.parse tree [| "lint" |] = Ok(Lint []) @>

[<Fact>]
let ``parse lint --run-once returns Lint RunOnce`` () =
    test <@ CommandTree.parse tree [| "lint"; "--run-once" |] = Ok(Lint [ RunOnce ]) @>

[<Fact>]
let ``parse format-check returns FormatCheck with no flags`` () =
    test <@ CommandTree.parse tree [| "format-check" |] = Ok(FormatCheck []) @>

[<Fact>]
let ``parse analyze returns Analyze with no flags`` () =
    test <@ CommandTree.parse tree [| "analyze" |] = Ok(Analyze []) @>

[<Fact>]
let ``parse status returns Status None`` () =
    test <@ CommandTree.parse tree [| "status" |] = Ok(Status None) @>

[<Fact>]
let ``parse status with plugin returns Status Some`` () =
    test <@ CommandTree.parse tree [| "status"; "lint" |] = Ok(Status(Some "lint")) @>

[<Fact>]
let ``parse scan returns Scan`` () =
    match CommandTree.parse tree [| "scan" |] with
    | Ok(Scan flags) -> test <@ flags |> List.isEmpty @>
    | other -> failwith $"Expected Ok(Scan []), got %A{other}"

[<Fact>]
let ``parse scan --force returns Scan with Force`` () =
    match CommandTree.parse tree [| "scan"; "--force" |] with
    | Ok(Scan flags) -> test <@ flags = [ Force ] @>
    | other -> failwith $"Expected Ok(Scan [Force]), got %A{other}"

[<Fact>]
let ``parse errors returns Errors`` () =
    test <@ CommandTree.parse tree [| "errors" |] = Ok Errors @>

[<Fact>]
let ``parse invalidate-cache returns InvalidateCache`` () =
    test <@ CommandTree.parse tree [| "invalidate-cache"; "some/file.fs" |] = Ok(InvalidateCache "some/file.fs") @>

[<Fact>]
let ``parse init returns Init`` () =
    test <@ CommandTree.parse tree [| "init" |] = Ok Init @>

[<Fact>]
let ``parse unknown command returns UnknownCommand`` () =
    match CommandTree.parse tree [| "warnings" |] with
    | Error(UnknownCommand("warnings", _)) -> ()
    | other -> failwith $"Expected UnknownCommand, got %A{other}"

// --- GlobalSpec.Parse tests ---

[<Fact>]
let ``globalSpec parse with --verbose returns Verbose flag`` () =
    match spec.Parse [| "--verbose"; "start" |] with
    | Ok(globals, Start) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok(Verbose, Start), got %A{other}"

[<Fact>]
let ``globalSpec parse with -v returns Verbose flag`` () =
    match spec.Parse [| "-v"; "stop" |] with
    | Ok(globals, Stop) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok(Verbose, Stop), got %A{other}"

[<Fact>]
let ``globalSpec parse with --log-level returns LogLevel flag`` () =
    match spec.Parse [| "--log-level"; "debug"; "start" |] with
    | Ok(globals, Start) -> test <@ globals = [ LogLevel "debug" ] @>
    | other -> failwith $"Expected Ok(LogLevel debug, Start), got %A{other}"

[<Fact>]
let ``globalSpec parse with --no-cache returns NoCache flag`` () =
    match spec.Parse [| "--no-cache"; "build" |] with
    | Ok(globals, Build []) -> test <@ globals = [ NoCache ] @>
    | other -> failwith $"Expected Ok(NoCache, Build []), got %A{other}"

[<Fact>]
let ``globalSpec parse with multiple global flags`` () =
    match spec.Parse [| "--verbose"; "--no-cache"; "check" |] with
    | Ok(globals, Check []) -> test <@ globals = [ Verbose; NoCache ] @>
    | other -> failwith $"Expected Ok([Verbose; NoCache], Check []), got %A{other}"

[<Fact>]
let ``globalSpec parse with no global flags passes through`` () =
    match spec.Parse [| "scan"; "--force" |] with
    | Ok(globals, Scan flags) ->
        test <@ globals |> List.isEmpty @>
        test <@ flags = [ Force ] @>
    | other -> failwith $"Expected Ok([], Scan [Force]), got %A{other}"

[<Fact>]
let ``globalSpec parse with global flags after command`` () =
    match spec.Parse [| "start"; "--verbose" |] with
    | Ok(globals, Start) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok([Verbose], Start), got %A{other}"

// --- applyGlobalFlags tests ---

[<Fact>]
let ``applyGlobalFlags with NoCache returns noCache true`` () =
    let (noCache, _, _) = applyGlobalFlags [ NoCache ]
    test <@ noCache @>

[<Fact>]
let ``applyGlobalFlags with empty list returns noCache false`` () =
    let (noCache, noWarnFail, extraArgs) = applyGlobalFlags []
    test <@ not noCache @>
    test <@ not noWarnFail @>
    test <@ extraArgs = "" @>

[<Fact>]
let ``applyGlobalFlags builds daemon extra args`` () =
    let (_, _, extraArgs) = applyGlobalFlags [ Verbose; NoCache ]
    test <@ extraArgs = "--verbose --no-cache " @>

[<Fact>]
let ``applyGlobalFlags with LogLevel builds extra args`` () =
    let (_, _, extraArgs) = applyGlobalFlags [ LogLevel "debug" ]
    test <@ extraArgs = "--log-level debug " @>

[<Fact>]
let ``applyGlobalFlags with NoWarnFail returns noWarnFail true`` () =
    let (_, noWarnFail, _) = applyGlobalFlags [ NoWarnFail ]
    test <@ noWarnFail @>

[<Fact>]
let ``applyGlobalFlags NoWarnFail does not add to daemon extra args`` () =
    let (_, _, extraArgs) = applyGlobalFlags [ NoWarnFail ]
    test <@ extraArgs = "" @>

// --- findRepoRoot tests ---

[<Fact>]
let ``findRepoRoot finds git repo`` () =
    withTempDir "cli-git" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "a", "b")
        Directory.CreateDirectory(nested) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>)

[<Fact>]
let ``findRepoRoot finds jj repo`` () =
    withTempDir "cli-jj" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(nested) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>)

// --- shutdown tests ---

[<Fact>]
let ``shutdown via IPC stops the daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-shutdown-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None (set [ 1182 ])

    let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    waitForIpcServer pipeName

    try
        let result = IpcClient.shutdown pipeName |> Async.RunSynchronously
        test <@ result = "shutting down" @>

        // Daemon should stop within a few seconds
        try
            task.Wait(TimeSpan.FromSeconds(5.0)) |> ignore
        with _ ->
            ()

        test <@ task.IsCompleted @>
    finally
        if not cts.IsCancellationRequested then
            cts.Cancel()

        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

// --- computePipeName tests ---

[<Fact>]
let ``computePipeName is deterministic`` () =
    let name1 = computePipeName "/some/repo"
    let name2 = computePipeName "/some/repo"
    test <@ name1 = name2 @>

[<Fact>]
let ``computePipeName starts with prefix`` () =
    let name = computePipeName "/any/path"
    test <@ name.StartsWith("fs-hot-watch-") @>

[<Fact>]
let ``computePipeName differs for different paths`` () =
    let name1 = computePipeName "/repo/a"
    let name2 = computePipeName "/repo/b"
    test <@ name1 <> name2 @>

[<Fact>]
let ``computePipeName has expected length`` () =
    let name = computePipeName "/test"
    // "fs-hot-watch-" is 13 chars + 12 hex chars = 25
    test <@ name.Length = 25 @>

// --- CLI integration tests (real daemon + IPC) ---

[<Fact>]
let ``CLI status query works against running daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-inttest-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None (set [ 1182 ])

    let handler =
        { Name = PluginName.create "test-plugin"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    daemon.RegisterHandler(handler)
    let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    waitForIpcServer pipeName

    try
        let result = IpcClient.getStatus pipeName |> Async.RunSynchronously
        test <@ result.Contains("test-plugin") @>
        test <@ result.Contains("Idle") @>
    finally
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``CLI plugin status query works against running daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-inttest-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None (set [ 1182 ])

    let handler =
        { Name = PluginName.create "my-lint"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged _ -> ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChanged ]
          CacheKey = None
          Teardown = None }

    daemon.RegisterHandler(handler)
    // Trigger a FileChanged so the plugin reports Running status
    daemon.Host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    waitForIpcServer pipeName
    // Wait for the plugin to update
    waitUntil
        (fun () ->
            match daemon.Host.GetStatus("my-lint") with
            | Some(Running _) -> true
            | _ -> false)
        5000

    try
        let result = IpcClient.getPluginStatus pipeName "my-lint" |> Async.RunSynchronously
        test <@ result.Contains("Running") @>
    finally
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``CLI command proxying works against running daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-inttest-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None (set [ 1182 ])

    let handler =
        { Name = PluginName.create "greeter"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands =
            [ "greet",
              fun _state args ->
                  async {
                      let name = if args.Length > 0 then args.[0] else "world"
                      return $"hello {name}"
                  } ]
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    daemon.RegisterHandler(handler)
    let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    waitForIpcServer pipeName

    try
        let result =
            IpcClient.runCommand pipeName "greet" "Claude" |> Async.RunSynchronously

        test <@ result.Contains("hello Claude") @>
    finally
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

// --- executeCommand with fake IPC tests ---

let private fakeConfig: DaemonConfiguration =
    { Build = None
      Format = Off
      Lint = false
      Cache = FsHotWatch.Cli.DaemonConfig.NoCache
      Analyzers = None
      Tests = None
      Coverage = None
      FileCommands = [] }

let private fakeIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "shutting down" }
      Scan = fun _ _ -> async { return "scan started" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus = fun _ -> async { return """{"plugin": "Completed at 2026-01-01T00:00:00Z"}""" }
      GetPluginStatus = fun _ _ -> async { return "not found" }
      RunCommand = fun _ _ _ -> async { return "unknown command" }
      GetDiagnostics = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "formatted 0 files" }
      InvalidateCache = fun _ _ -> async { return "{\"status\": \"rechecked\"}" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> () }

[<Fact>]
let ``executeCommand Stop calls shutdown`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            Shutdown =
                fun _ ->
                    async {
                        called <- true
                        return "shutting down"
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Stop "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Status returns 0`` () =
    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            (fakeIpc ())
            "/tmp"
            "pipe"
            (Status None)
            ""
            false
            fakeConfig
            30.0

    test <@ result = 0 @>

[<Fact>]
let ``executePluginCommand proxies to IPC`` () =
    let mutable cmdName = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ cmd _ ->
                    async {
                        cmdName <- cmd
                        return "result"
                    } }

    let result = executePluginCommand ipc "pipe" "warnings" ""

    test <@ result = 0 @>
    test <@ cmdName = "warnings" @>

[<Fact>]
let ``executeCommand Scan calls scan IPC`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            Scan =
                fun _ _ ->
                    async {
                        called <- true
                        return "scan started"
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Scan []) "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Status with plugin name calls getPluginStatus`` () =
    let mutable calledWith = ""

    let ipc =
        { fakeIpc () with
            GetPluginStatus =
                fun _ name ->
                    async {
                        calledWith <- name
                        return "Running"
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Status(Some "lint"))
            ""
            false
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ calledWith = "lint" @>

[<Fact>]
let ``executeCommand Start with fake daemon throws on null daemon`` () =
    // Use a unique temp dir to avoid writing the test process PID to /tmp/.fs-hot-watch/daemon.pid
    // where killStaleDaemon from other tests would read it and kill the test process.
    withTempDir "cli-start" (fun tmpDir ->
        let mutable createCalled = false
        let fakeDaemon = Unchecked.defaultof<Daemon>

        let createDaemon _ =
            createCalled <- true
            fakeDaemon

        let threw =
            try
                executeCommand createDaemon (fakeIpc ()) tmpDir "pipe" Start "" false fakeConfig 30.0
                |> ignore

                false
            with _ ->
                true

        test <@ createCalled @>
        test <@ threw @>)

[<Fact>]
let ``executeCommand returns 1 when IPC fails`` () =
    let ipc =
        { fakeIpc () with
            GetStatus = fun _ -> async { return failwith "connection refused" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Status None) "" false fakeConfig 30.0

    test <@ result = 1 @>

// --- decideDaemonAction tests ---

[<Fact>]
let ``decideDaemonAction reuses running daemon with matching config`` () =
    let action = decideDaemonAction true "abc123" "abc123"
    test <@ action = Reuse @>

[<Fact>]
let ``decideDaemonAction restarts daemon when config hash changes`` () =
    let action = decideDaemonAction true "old-hash" "new-hash"
    test <@ action = Restart @>

[<Fact>]
let ``decideDaemonAction starts fresh when daemon not running`` () =
    let action = decideDaemonAction false "" "abc123"
    test <@ action = StartFresh @>

[<Fact>]
let ``decideDaemonAction starts fresh when not running even with matching hash`` () =
    let action = decideDaemonAction false "abc123" "abc123"
    test <@ action = StartFresh @>

// --- exit code paths via executeCommand ---

[<Fact>]
let ``executeCommand Errors with count 0 returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            GetDiagnostics = fun _ _ -> async { return """{"count": 0}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" false fakeConfig 30.0

    test <@ result = 0 @>

[<Fact>]
let ``executeCommand Errors with count 5 returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            GetDiagnostics =
                fun _ _ ->
                    async {
                        return
                            """{"count": 5, "files": {"src/Foo.fs": [{"plugin":"check","message":"err","severity":"error","line":1,"column":1}]}, "statuses": {}}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" false fakeConfig 30.0

    test <@ result = 1 @>

[<Fact>]
let ``executeCommand Errors with IPC failure returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            GetDiagnostics = fun _ _ -> async { return failwith "connection refused" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" false fakeConfig 30.0

    test <@ result = 1 @>

[<Fact>]
let ``executeCommand Build with status passed returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return """{"status": "passed"}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Build []) "" false fakeConfig 30.0

    test <@ result = 0 @>

[<Fact>]
let ``executeCommand Build with status failed returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return """{"status": "failed"}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Build []) "" false fakeConfig 30.0

    test <@ result = 1 @>

[<Fact>]
let ``executeCommand Build with plain text returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return "build completed successfully" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Build []) "" false fakeConfig 30.0

    test <@ result = 0 @>

// --- executeCommand for Build, Test, Format, Lint, Errors, Check ---

[<Fact>]
let ``executeCommand Build calls triggerBuild`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            TriggerBuild =
                fun _ ->
                    async {
                        called <- true
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Build []) "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Test calls runCommand with run-tests`` () =
    let mutable cmdName = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ cmd _ ->
                    async {
                        cmdName <- cmd
                        return """{"status": "passed"}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Test []) "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ cmdName = "run-tests" @>

[<Fact>]
let ``executeCommand Format calls formatAll`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            FormatAll =
                fun _ ->
                    async {
                        called <- true
                        return "formatted 3 files"
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Format []) "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand FormatCheck uses format-check filter not format`` () =
    let mutable errorFilter = ""

    let ipc =
        { fakeIpc () with
            GetDiagnostics =
                fun _ filter ->
                    async {
                        errorFilter <- filter
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (FormatCheck []) "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ errorFilter = "format-check" @>

[<Fact>]
let ``executeCommand Lint scans waits and gets lint errors`` () =
    let mutable errorFilter = ""

    let ipc =
        { fakeIpc () with
            GetDiagnostics =
                fun _ filter ->
                    async {
                        errorFilter <- filter
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Lint []) "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ errorFilter = "lint" @>

[<Fact>]
let ``executeCommand Errors calls getErrors`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            GetDiagnostics =
                fun _ _ ->
                    async {
                        called <- true
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Check waits for scan and returns errors`` () =
    let mutable waitForScanCalled = false
    let mutable getStatusCalled = false
    let mutable getErrorsCalled = false

    let ipc =
        { fakeIpc () with
            WaitForScan =
                fun _ _ ->
                    async {
                        waitForScanCalled <- true
                        return "idle"
                    }
            GetStatus =
                fun _ ->
                    async {
                        getStatusCalled <- true
                        return """{"check": "Completed at 2026-01-01T00:00:00Z"}"""
                    }
            GetDiagnostics =
                fun _ _ ->
                    async {
                        getErrorsCalled <- true
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Check []) "" false fakeConfig 30.0

    test <@ result = 0 @>
    test <@ waitForScanCalled @>
    test <@ getStatusCalled @>
    test <@ getErrorsCalled @>

// --- executeCommand for InvalidateCache ---

[<Fact>]
let ``executeCommand InvalidateCache calls invalidateCache with file path`` () =
    let mutable calledWithPath = ""

    let ipc =
        { fakeIpc () with
            InvalidateCache =
                fun _ path ->
                    async {
                        calledWithPath <- path
                        return """{"status": "rechecked"}"""
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (InvalidateCache "some/file.fs")
            ""
            false
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ calledWithPath = "some/file.fs" @>

// --- Regression tests for bug fixes ---

/// Run a test that triggers daemon startup failure using an isolated temp dir
/// so that killStaleDaemon cannot read another test's PID file.
let private withStartupFailure command =
    withTempDir "cli-fail" (fun tmpDir ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc tmpDir "pipe" command "" false fakeConfig 0.0)

[<Fact>]
let ``executeCommand Check returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Check []) = 1 @>

[<Fact>]
let ``executeCommand Build returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Build []) = 1 @>

[<Fact>]
let ``executeCommand Test returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Test []) = 1 @>

[<Fact>]
let ``executeCommand Lint returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Lint []) = 1 @>

[<Fact>]
let ``executeCommand Errors returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure Errors = 1 @>

// --- computeLaunchCommand tests ---

[<Fact>]
let ``computeLaunchCommand with dotnet process path returns dotnet tool run`` () =
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/dotnet"
    test <@ exe = "/usr/local/bin/dotnet" @>
    test <@ prefix.Contains("fs-hot-watch") @>

[<Fact>]
let ``computeLaunchCommand with native exe returns exe directly`` () =
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/fs-hot-watch"
    test <@ exe = "/usr/local/bin/fs-hot-watch" @>
    test <@ prefix = "" @>

[<Fact>]
let ``computeLaunchCommand with dotnet.exe on Windows returns dotnet tool run`` () =
    let (exe, prefix) = computeLaunchCommand """C:\Program Files\dotnet\dotnet.exe"""
    test <@ exe = """C:\Program Files\dotnet\dotnet.exe""" @>
    test <@ prefix.Contains("fs-hot-watch") @>
