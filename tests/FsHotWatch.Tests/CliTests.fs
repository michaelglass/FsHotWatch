module FsHotWatch.Tests.CliTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
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

// --- parseCommand tests ---

[<Fact>]
let ``parseCommand empty args returns Help`` () = test <@ parseCommand [] = Help @>

[<Fact>]
let ``parseCommand help returns Help`` () =
    test <@ parseCommand [ "help" ] = Help @>

[<Fact>]
let ``parseCommand --help returns Help`` () =
    test <@ parseCommand [ "--help" ] = Help @>

[<Fact>]
let ``parseCommand -h returns Help`` () = test <@ parseCommand [ "-h" ] = Help @>

[<Fact>]
let ``parseCommand start returns Start`` () =
    test <@ parseCommand [ "start" ] = Start @>

[<Fact>]
let ``parseCommand stop returns Stop`` () =
    test <@ parseCommand [ "stop" ] = Stop @>

[<Fact>]
let ``parseCommand status returns Status None`` () =
    test <@ parseCommand [ "status" ] = Status None @>

[<Fact>]
let ``parseCommand status with plugin returns Status Some`` () =
    test <@ parseCommand [ "status"; "lint" ] = Status(Some "lint") @>

[<Fact>]
let ``parseCommand scan returns Scan false`` () =
    test <@ parseCommand [ "scan" ] = Scan false @>

[<Fact>]
let ``parseCommand scan --force returns Scan true`` () =
    test <@ parseCommand [ "scan"; "--force" ] = Scan true @>

[<Fact>]
let ``parseCommand scan-status returns ScanStatus`` () =
    test <@ parseCommand [ "scan-status" ] = ScanStatus @>

[<Fact>]
let ``parseCommand unknown command returns PluginCommand`` () =
    test <@ parseCommand [ "warnings" ] = PluginCommand("warnings", "") @>

[<Fact>]
let ``parseCommand command with args joins them`` () =
    test <@ parseCommand [ "run"; "--verbose"; "foo" ] = PluginCommand("run", "--verbose foo") @>

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

    let daemon = Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None
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

    let daemon = Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None

    let handler =
        { Name = "test-plugin"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None }

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

    let daemon = Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None

    let handler =
        { Name = "my-lint"
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
          Subscriptions =
            { PluginSubscriptions.none with
                FileChanged = true }
          CacheKey = None }

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

    let daemon = Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None

    let handler =
        { Name = "greeter"
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
          CacheKey = None }

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
      Format = false
      Lint = false
      Cache = NoCache
      Analyzers = None
      Tests = None
      Coverage = None
      FileCommands = [] }

let private fakeIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "shutting down" }
      Scan = fun _ _ -> async { return "scan started" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus = fun _ -> async { return "{}" }
      GetPluginStatus = fun _ _ -> async { return "not found" }
      RunCommand = fun _ _ _ -> async { return "unknown command" }
      GetErrors = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "formatted 0 files" }
      InvalidateCache = fun _ _ -> async { return "{\"status\": \"rechecked\"}" }
      IsRunning = fun _ -> true }

[<Fact>]
let ``executeCommand Help returns 0`` () =
    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) (fakeIpc ()) "/tmp" "pipe" Help "" fakeConfig

    test <@ result = 0 @>

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
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Stop "" fakeConfig

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Status returns 0`` () =
    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) (fakeIpc ()) "/tmp" "pipe" (Status None) "" fakeConfig

    test <@ result = 0 @>

[<Fact>]
let ``executeCommand PluginCommand proxies to IPC`` () =
    let mutable cmdName = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ cmd _ ->
                    async {
                        cmdName <- cmd
                        return "result"
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (PluginCommand("warnings", "")) "" fakeConfig

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
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Scan false) "" fakeConfig

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand ScanStatus calls scanStatus IPC`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            ScanStatus =
                fun _ ->
                    async {
                        called <- true
                        return "idle"
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" ScanStatus "" fakeConfig

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
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Status(Some "lint")) "" fakeConfig

    test <@ result = 0 @>
    test <@ calledWith = "lint" @>

[<Fact>]
let ``executeCommand Start with fake daemon throws on null daemon`` () =
    // A defaultof Daemon has null fields, so RunWithIpc will throw.
    // This verifies the createDaemon parameter is actually called and used.
    let mutable createCalled = false
    let fakeDaemon = Unchecked.defaultof<Daemon>

    let createDaemon _ =
        createCalled <- true
        fakeDaemon

    let threw =
        try
            executeCommand createDaemon (fakeIpc ()) "/tmp" "pipe" Start "" fakeConfig
            |> ignore

            false
        with _ ->
            true

    test <@ createCalled @>
    test <@ threw @>

[<Fact>]
let ``executeCommand returns 1 when IPC fails`` () =
    let ipc =
        { fakeIpc () with
            GetStatus = fun _ -> async { return failwith "connection refused" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Status None) "" fakeConfig

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

// --- parseCommand "test" variations ---

[<Fact>]
let ``parseCommand test with no flags returns empty JSON`` () =
    test <@ parseCommand [ "test" ] = Test "{}" @>

[<Fact>]
let ``parseCommand test with --project returns projects JSON`` () =
    let result = parseCommand [ "test"; "--project"; "MyProj" ]

    match result with
    | Test json -> test <@ json.Contains("\"projects\": [\"MyProj\"]") @>
    | _ -> failwith "Expected Test command"

[<Fact>]
let ``parseCommand test with -p short flag returns projects JSON`` () =
    let result = parseCommand [ "test"; "-p"; "MyProj" ]

    match result with
    | Test json -> test <@ json.Contains("\"projects\": [\"MyProj\"]") @>
    | _ -> failwith "Expected Test command"

[<Fact>]
let ``parseCommand test with --filter returns filter JSON`` () =
    let result = parseCommand [ "test"; "--filter"; "MyClass" ]

    match result with
    | Test json -> test <@ json.Contains("\"filter\": \"MyClass\"") @>
    | _ -> failwith "Expected Test command"

[<Fact>]
let ``parseCommand test with -f short flag returns filter JSON`` () =
    let result = parseCommand [ "test"; "-f"; "MyClass" ]

    match result with
    | Test json -> test <@ json.Contains("\"filter\": \"MyClass\"") @>
    | _ -> failwith "Expected Test command"

[<Fact>]
let ``parseCommand test with --only-failed returns only-failed JSON`` () =
    let result = parseCommand [ "test"; "--only-failed" ]

    match result with
    | Test json -> test <@ json.Contains("\"only-failed\": true") @>
    | _ -> failwith "Expected Test command"

[<Fact>]
let ``parseCommand test with all flags combined`` () =
    let result = parseCommand [ "test"; "-p"; "A"; "-f"; "B"; "--only-failed" ]

    match result with
    | Test json ->
        test <@ json.Contains("\"only-failed\": true") @>
        test <@ json.Contains("\"projects\": [\"A\"]") @>
        test <@ json.Contains("\"filter\": \"B\"") @>
    | _ -> failwith "Expected Test command"

[<Fact>]
let ``parseCommand test with unknown flags ignores them`` () =
    let result = parseCommand [ "test"; "--unknown-flag" ]

    match result with
    | Test json -> test <@ json = "{}" @>
    | _ -> failwith "Expected Test command"

// --- runIpcWithExitCode exit code paths via executeCommand ---

[<Fact>]
let ``executeCommand Errors with count 0 returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            GetErrors = fun _ _ -> async { return """{"count": 0}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" fakeConfig

    test <@ result = 0 @>

[<Fact>]
let ``executeCommand Errors with count 5 returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            GetErrors = fun _ _ -> async { return """{"count": 5}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" fakeConfig

    test <@ result = 1 @>

[<Fact>]
let ``executeCommand Errors with error field returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            GetErrors = fun _ _ -> async { return """{"error": "something went wrong"}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" fakeConfig

    test <@ result = 1 @>

[<Fact>]
let ``executeCommand Build with status passed returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return """{"status": "passed"}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Build "" fakeConfig

    test <@ result = 0 @>

[<Fact>]
let ``executeCommand Build with status failed returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return """{"status": "failed"}""" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Build "" fakeConfig

    test <@ result = 1 @>

[<Fact>]
let ``executeCommand Build with plain text returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return "build completed successfully" } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Build "" fakeConfig

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
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Build "" fakeConfig

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Test calls runCommand with run-tests`` () =
    let mutable cmdName = ""
    let mutable cmdArgs = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ cmd args ->
                    async {
                        cmdName <- cmd
                        cmdArgs <- args
                        return """{"status": "passed"}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" (Test """{"filter": "Foo"}""") "" fakeConfig

    test <@ result = 0 @>
    test <@ cmdName = "run-tests" @>
    test <@ cmdArgs = """{"filter": "Foo"}""" @>

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
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Format "" fakeConfig

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Lint scans waits and gets lint errors`` () =
    let mutable errorFilter = ""

    let ipc =
        { fakeIpc () with
            GetErrors =
                fun _ filter ->
                    async {
                        errorFilter <- filter
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Lint "" fakeConfig

    test <@ result = 0 @>
    test <@ errorFilter = "lint" @>

[<Fact>]
let ``executeCommand Errors calls getErrors`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            GetErrors =
                fun _ _ ->
                    async {
                        called <- true
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Errors "" fakeConfig

    test <@ result = 0 @>
    test <@ called @>

[<Fact>]
let ``executeCommand Check waits for scan and returns errors`` () =
    let mutable waitForScanCalled = false
    let mutable waitForCompleteCalled = false
    let mutable getErrorsCalled = false

    let ipc =
        { fakeIpc () with
            WaitForScan =
                fun _ _ ->
                    async {
                        waitForScanCalled <- true
                        return "idle"
                    }
            WaitForComplete =
                fun _ ->
                    async {
                        waitForCompleteCalled <- true
                        return "{}"
                    }
            GetErrors =
                fun _ _ ->
                    async {
                        getErrorsCalled <- true
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Check "" fakeConfig

    test <@ result = 0 @>
    test <@ waitForScanCalled @>
    test <@ waitForCompleteCalled @>
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
            fakeConfig

    test <@ result = 0 @>
    test <@ calledWithPath = "some/file.fs" @>
