module FsHotWatch.Tests.CliTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open CommandTree
open FsHotWatch.Cli
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

[<Fact(Timeout = 5000)>]
let ``parse empty args returns HelpRequested`` () =
    match CommandTree.parse tree [||] with
    | Error(HelpRequested _) -> ()
    | other -> failwith $"Expected HelpRequested, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``parse start returns Start`` () =
    test <@ CommandTree.parse tree [| "start" |] = Ok Start @>

[<Fact(Timeout = 5000)>]
let ``parse stop returns Stop`` () =
    test <@ CommandTree.parse tree [| "stop" |] = Ok Stop @>

[<Fact(Timeout = 5000)>]
let ``parse check returns Check with no flags`` () =
    test <@ CommandTree.parse tree [| "check" |] = Ok(Check []) @>

[<Fact(Timeout = 5000)>]
let ``parse check --run-once returns Check RunOnce`` () =
    test <@ CommandTree.parse tree [| "check"; "--run-once" |] = Ok(Check [ RunOnce ]) @>

[<Fact(Timeout = 5000)>]
let ``parse build returns Build with no flags`` () =
    test <@ CommandTree.parse tree [| "build" |] = Ok(Build []) @>

[<Fact(Timeout = 5000)>]
let ``parse build --run-once returns Build RunOnce`` () =
    test <@ CommandTree.parse tree [| "build"; "--run-once" |] = Ok(Build [ RunOnce ]) @>

[<Fact(Timeout = 5000)>]
let ``parse test returns Test with no flags`` () =
    test <@ CommandTree.parse tree [| "test" |] = Ok(Test []) @>

[<Fact(Timeout = 5000)>]
let ``parse test --run-once returns Test RunOnce`` () =
    test <@ CommandTree.parse tree [| "test"; "--run-once" |] = Ok(Test [ RunOnce ]) @>

[<Fact(Timeout = 5000)>]
let ``parse format returns Format with no flags`` () =
    test <@ CommandTree.parse tree [| "format" |] = Ok(Format []) @>

[<Fact(Timeout = 5000)>]
let ``parse lint returns Lint with no flags`` () =
    test <@ CommandTree.parse tree [| "lint" |] = Ok(Lint []) @>

[<Fact(Timeout = 5000)>]
let ``parse lint --run-once returns Lint RunOnce`` () =
    test <@ CommandTree.parse tree [| "lint"; "--run-once" |] = Ok(Lint [ RunOnce ]) @>

[<Fact(Timeout = 5000)>]
let ``parse format-check returns FormatCheck with no flags`` () =
    test <@ CommandTree.parse tree [| "format-check" |] = Ok(FormatCheck []) @>

[<Fact(Timeout = 5000)>]
let ``parse analyze returns Analyze with no flags`` () =
    test <@ CommandTree.parse tree [| "analyze" |] = Ok(Analyze []) @>

[<Fact(Timeout = 5000)>]
let ``parse status returns Status None`` () =
    test <@ CommandTree.parse tree [| "status" |] = Ok(Status None) @>

[<Fact(Timeout = 5000)>]
let ``parse status with plugin returns Status Some`` () =
    test <@ CommandTree.parse tree [| "status"; "lint" |] = Ok(Status(Some "lint")) @>

[<Fact(Timeout = 5000)>]
let ``parse scan returns Scan`` () =
    match CommandTree.parse tree [| "scan" |] with
    | Ok(Scan flags) -> test <@ flags |> List.isEmpty @>
    | other -> failwith $"Expected Ok(Scan []), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``parse scan --force returns Scan with Force`` () =
    match CommandTree.parse tree [| "scan"; "--force" |] with
    | Ok(Scan flags) -> test <@ flags = [ Force ] @>
    | other -> failwith $"Expected Ok(Scan [Force]), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``parse errors returns Errors`` () =
    test <@ CommandTree.parse tree [| "errors" |] = Ok(Errors []) @>

[<Fact(Timeout = 5000)>]
let ``parse errors --wait returns Errors with Wait flag`` () =
    test <@ CommandTree.parse tree [| "errors"; "--wait" |] = Ok(Errors [ Wait ]) @>

[<Fact(Timeout = 5000)>]
let ``parse errors --timeout returns Errors with Timeout flag`` () =
    test <@ CommandTree.parse tree [| "errors"; "--timeout"; "30" |] = Ok(Errors [ Timeout 30 ]) @>

[<Fact(Timeout = 5000)>]
let ``parse errors --wait --timeout combines both flags`` () =
    match CommandTree.parse tree [| "errors"; "--wait"; "--timeout"; "45" |] with
    | Ok(Errors flags) ->
        test <@ flags |> List.contains Wait @>
        test <@ flags |> List.contains (Timeout 45) @>
    | other -> failwith $"Expected Ok(Errors [Wait; Timeout 45]), got %A{other}"

// --- WaitMode.fromFlags (pure normalization) ---

[<Fact(Timeout = 5000)>]
let ``WaitMode.fromFlags empty is NoWait`` () =
    test <@ WaitMode.fromFlags [] = Ok WaitMode.NoWait @>

[<Fact(Timeout = 5000)>]
let ``WaitMode.fromFlags Wait uses default timeout`` () =
    test <@ WaitMode.fromFlags [ Wait ] = Ok(WaitMode.WaitFor WaitMode.defaultTimeout) @>

[<Fact(Timeout = 5000)>]
let ``WaitMode.fromFlags Wait with Timeout uses supplied seconds`` () =
    test <@ WaitMode.fromFlags [ Wait; Timeout 30 ] = Ok(WaitMode.WaitFor(TimeSpan.FromSeconds 30.0)) @>

[<Fact(Timeout = 5000)>]
let ``WaitMode.fromFlags Timeout without Wait is rejected`` () =
    match WaitMode.fromFlags [ Timeout 30 ] with
    | Error msg -> test <@ msg.Contains("--wait") @>
    | Ok _ -> failwith "expected Error"

[<Fact(Timeout = 5000)>]
let ``WaitMode.fromFlags rejects zero timeout`` () =
    match WaitMode.fromFlags [ Wait; Timeout 0 ] with
    | Error msg -> test <@ msg.Contains("positive") @>
    | Ok _ -> failwith "expected Error"

[<Fact(Timeout = 5000)>]
let ``WaitMode.fromFlags rejects negative timeout`` () =
    match WaitMode.fromFlags [ Wait; Timeout -5 ] with
    | Error msg -> test <@ msg.Contains("positive") @>
    | Ok _ -> failwith "expected Error"

[<Fact(Timeout = 5000)>]
let ``parse rerun <name> returns Rerun`` () =
    test <@ CommandTree.parse tree [| "rerun"; "coverage-ratchet" |] = Ok(Rerun "coverage-ratchet") @>

[<Fact(Timeout = 5000)>]
let ``parse coverage refresh-baseline returns Coverage RefreshBaseline`` () =
    test
        <@
            CommandTree.parse tree [| "coverage"; "refresh-baseline" |] = Ok(
                FsHotWatch.Cli.Program.Coverage FsHotWatch.Cli.Program.RefreshBaseline
            )
        @>

[<Fact(Timeout = 5000)>]
let ``refreshCoverageBaseline deletes baseline and partial cobertura across configured projects`` () =
    let tmp = Path.Combine(Path.GetTempPath(), $"fshw-cov-refresh-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tmp) |> ignore

    try
        let makeProj (name: string) (cov: bool) : TestProjectConfig =
            { Project = name
              Command = "dotnet"
              Args = "test"
              Group = "default"
              Environment = []
              FilterTemplate = None
              ClassJoin = " "
              Coverage = cov
              CoverageArgsTemplate = None
              TimeoutSec = None }

        let covDir = "coverage"

        let writeFiles proj =
            let d = Path.Combine(tmp, covDir, proj)
            Directory.CreateDirectory(d) |> ignore
            File.WriteAllText(Path.Combine(d, "coverage.baseline.cobertura.xml"), "{}")
            File.WriteAllText(Path.Combine(d, "coverage.partial.cobertura.xml"), "{}")
            File.WriteAllText(Path.Combine(d, "coverage.cobertura.xml"), "<coverage/>")

        writeFiles "ProjA"
        writeFiles "ProjB"
        writeFiles "ProjOptOut"

        let config: DaemonConfiguration =
            { Build = None
              Format = FormatMode.Off
              Lint = false
              Cache = CacheBackendConfig.NoCache
              Analyzers = None
              Tests =
                Some
                    {| BeforeRun = None
                       Extensions = []
                       Projects = [ makeProj "ProjA" true; makeProj "ProjB" true; makeProj "ProjOptOut" false ]
                       CoverageDir = covDir |}
              FileCommands = []
              Exclude = []
              LogDir = "logs"
              TimeoutSec = None }

        let deleted = FsHotWatch.Cli.Program.refreshCoverageBaseline tmp config
        // 4 files total: 2 projects × (baseline + partial)
        test <@ deleted.Length = 4 @>

        // Cobertura stays (not baseline/partial)
        test <@ File.Exists(Path.Combine(tmp, covDir, "ProjA", "coverage.cobertura.xml")) @>
        // Both flavors gone for opted-in projects
        test <@ not (File.Exists(Path.Combine(tmp, covDir, "ProjA", "coverage.baseline.cobertura.xml"))) @>
        test <@ not (File.Exists(Path.Combine(tmp, covDir, "ProjA", "coverage.partial.cobertura.xml"))) @>
        test <@ not (File.Exists(Path.Combine(tmp, covDir, "ProjB", "coverage.baseline.cobertura.xml"))) @>
        // Opt-out project is untouched
        test <@ File.Exists(Path.Combine(tmp, covDir, "ProjOptOut", "coverage.baseline.cobertura.xml")) @>
    finally
        if Directory.Exists tmp then
            Directory.Delete(tmp, true)

[<Fact(Timeout = 5000)>]
let ``parse init returns Init`` () =
    test <@ CommandTree.parse tree [| "init" |] = Ok Init @>

[<Fact(Timeout = 5000)>]
let ``parse unknown command returns UnknownCommand`` () =
    match CommandTree.parse tree [| "warnings" |] with
    | Error(UnknownCommand("warnings", _)) -> ()
    | other -> failwith $"Expected UnknownCommand, got %A{other}"

// --- GlobalSpec.Parse tests ---

[<Fact(Timeout = 5000)>]
let ``globalSpec parse with --verbose returns Verbose flag`` () =
    match spec.Parse [| "--verbose"; "start" |] with
    | Ok(globals, Start) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok(Verbose, Start), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``globalSpec parse with -v returns Verbose flag`` () =
    match spec.Parse [| "-v"; "stop" |] with
    | Ok(globals, Stop) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok(Verbose, Stop), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``globalSpec parse with --log-level returns LogLevel flag`` () =
    match spec.Parse [| "--log-level"; "debug"; "start" |] with
    | Ok(globals, Start) -> test <@ globals = [ LogLevel "debug" ] @>
    | other -> failwith $"Expected Ok(LogLevel debug, Start), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``globalSpec parse with --no-cache returns NoCache flag`` () =
    match spec.Parse [| "--no-cache"; "build" |] with
    | Ok(globals, Build []) -> test <@ globals = [ NoCache ] @>
    | other -> failwith $"Expected Ok(NoCache, Build []), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``globalSpec parse with multiple global flags`` () =
    match spec.Parse [| "--verbose"; "--no-cache"; "check" |] with
    | Ok(globals, Check []) -> test <@ globals = [ Verbose; NoCache ] @>
    | other -> failwith $"Expected Ok([Verbose; NoCache], Check []), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``globalSpec parse with no global flags passes through`` () =
    match spec.Parse [| "scan"; "--force" |] with
    | Ok(globals, Scan flags) ->
        test <@ globals |> List.isEmpty @>
        test <@ flags = [ Force ] @>
    | other -> failwith $"Expected Ok([], Scan [Force]), got %A{other}"

[<Fact(Timeout = 5000)>]
let ``globalSpec parse with global flags after command`` () =
    match spec.Parse [| "start"; "--verbose" |] with
    | Ok(globals, Start) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok([Verbose], Start), got %A{other}"

// --- applyGlobalFlags tests ---

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags with NoCache returns noCache true`` () =
    test <@ (applyGlobalFlags [ NoCache ]).NoCache @>

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags with empty list returns noCache false`` () =
    let opts = applyGlobalFlags []
    test <@ not opts.NoCache @>
    test <@ not opts.NoWarnFail @>
    test <@ opts.DaemonExtraArgs = "" @>

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags builds daemon extra args`` () =
    test <@ (applyGlobalFlags [ Verbose; NoCache ]).DaemonExtraArgs = "--verbose --no-cache " @>

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags with LogLevel builds extra args`` () =
    test <@ (applyGlobalFlags [ LogLevel "debug" ]).DaemonExtraArgs = "--log-level debug " @>

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags with NoWarnFail returns noWarnFail true`` () =
    test <@ (applyGlobalFlags [ NoWarnFail ]).NoWarnFail @>

[<Fact(Timeout = 5000)>]
let ``applyGlobalFlags NoWarnFail does not add to daemon extra args`` () =
    test <@ (applyGlobalFlags [ NoWarnFail ]).DaemonExtraArgs = "" @>

// --- findRepoRoot tests ---

[<Fact(Timeout = 5000)>]
let ``findRepoRoot finds git repo`` () =
    withTempDir "cli-git" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "a", "b")
        Directory.CreateDirectory(nested) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>)

[<Fact(Timeout = 5000)>]
let ``findRepoRoot finds jj repo`` () =
    withTempDir "cli-jj" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(nested) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>)

// --- shutdown tests ---

[<Fact(Timeout = 10000)>]
let ``shutdown via IPC stops the daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-shutdown-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

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

[<Fact(Timeout = 5000)>]
let ``computePipeName is deterministic`` () =
    let name1 = computePipeName "/some/repo"
    let name2 = computePipeName "/some/repo"
    test <@ name1 = name2 @>

[<Fact(Timeout = 5000)>]
let ``computePipeName starts with prefix`` () =
    let name = computePipeName "/any/path"
    test <@ name.StartsWith("fs-hot-watch-") @>

[<Fact(Timeout = 5000)>]
let ``computePipeName differs for different paths`` () =
    let name1 = computePipeName "/repo/a"
    let name2 = computePipeName "/repo/b"
    test <@ name1 <> name2 @>

[<Fact(Timeout = 5000)>]
let ``computePipeName has expected length`` () =
    let name = computePipeName "/test"
    // "fs-hot-watch-" is 13 chars + 12 hex chars = 25
    test <@ name.Length = 25 @>

// --- CLI integration tests (real daemon + IPC) ---

[<Fact(Timeout = 10000)>]
let ``CLI status query works against running daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-inttest-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

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
        test <@ result.Contains("\"tag\":\"idle\"") @>
    finally
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 10000)>]
let ``CLI plugin status query works against running daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-inttest-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

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
        let parsed = IpcParsing.parsePluginStatuses result

        match parsed.["my-lint"].Status with
        | Running _ -> ()
        | other -> failwithf "expected Running, got %A" other
    finally
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 10000)>]
let ``CLI command proxying works against running daemon`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"cli-inttest-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
    let pipeName = computePipeName tmpDir
    let cts = new CancellationTokenSource()

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

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
      FileCommands = []
      Exclude = []
      LogDir = "logs"
      TimeoutSec = None }

/// Structured plugin-status JSON in the shape expected by parsePluginStatuses
/// (object per plugin, not a bare string). Using the bare-string shape made the
/// pollAndRender loop hang because isAllTerminal on an empty parse is false.
let private completedStatusJson =
    """{"plugin": {"status": "Completed at 2026-01-01T00:00:00Z", "subtasks": [], "activityTail": [], "lastRun": null}}"""

let private fakeIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "shutting down" }
      Scan = fun _ _ -> async { return "scan started" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus = fun _ -> async { return completedStatusJson }
      GetPluginStatus = fun _ _ -> async { return "{}" }
      RunCommand = fun _ _ _ -> async { return "unknown command" }
      GetDiagnostics = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "formatted 0 files" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> () }

[<Fact(Timeout = 5000)>]
let ``executeCommand Stop calls shutdown`` () =
    let mutable running = true
    let mutable called = false

    let ipc =
        { fakeIpc () with
            IsRunning = fun _ -> running
            Shutdown =
                fun _ ->
                    async {
                        called <- true
                        running <- false
                        return "shutting down"
                    } }

    let result =
        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" Stop defaultGlobalOptions fakeConfig 30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Config Check prints OK and returns 0`` () =
    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            (fakeIpc ())
            "/tmp"
            "pipe"
            (Config ConfigCommand.Check)
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>

[<Fact(Timeout = 5000)>]
let ``parse config check returns Config ConfigCommand.Check`` () =
    test <@ CommandTree.parse tree [| "config"; "check" |] = Ok(Config ConfigCommand.Check) @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Status returns 0`` () =
    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            (fakeIpc ())
            "/tmp"
            "pipe"
            (Status None)
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>

[<Fact(Timeout = 5000)>]
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

    let result = executePluginCommand ipc "pipe" defaultGlobalOptions "warnings" ""

    test <@ result = 0 @>
    test <@ cmdName = "warnings" @>

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Scan [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Status with plugin name queries GetDiagnostics for that plugin`` () =
    let mutable calledWith = ""

    let ipc =
        { fakeIpc () with
            GetDiagnostics =
                fun _ name ->
                    async {
                        calledWith <- name

                        return
                            """{"count": 0, "files": {}, "statuses": {"lint": {"status": {"tag": "running", "since": "2026-01-01T00:00:00Z"}, "subtasks": [], "activityTail": [], "lastRun": null, "diagnostics": {"errors": 0, "warnings": 0}}}}"""
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Status(Some "lint"))
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ calledWith = "lint" @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Start with fake daemon throws on null daemon`` () =
    // Use a unique temp dir to avoid writing the test process PID to /tmp/.fs-hot-watch/daemon.pid
    // where killStaleDaemon from other tests would read it and kill the test process.
    withTempDir "cli-start" (fun tmpDir ->
        let mutable createCalled = false
        let fakeDaemon = Unchecked.defaultof<Daemon>

        let createDaemon _ =
            createCalled <- true
            fakeDaemon

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        let threw =
            try
                executeCommand createDaemon ipc tmpDir "pipe" Start defaultGlobalOptions fakeConfig 30.0
                |> ignore

                false
            with _ ->
                true

        test <@ createCalled @>
        test <@ threw @>)

[<Fact(Timeout = 5000)>]
let ``executeCommand returns 1 when IPC fails`` () =
    let ipc =
        { fakeIpc () with
            GetDiagnostics = fun _ _ -> async { return failwith "connection refused" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Status None)
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 1 @>

// --- decideDaemonAction tests ---

[<Fact(Timeout = 5000)>]
let ``decideDaemonAction reuses running daemon with matching config`` () =
    let action = decideDaemonAction true "abc123" "abc123"
    test <@ action = Reuse @>

[<Fact(Timeout = 5000)>]
let ``decideDaemonAction restarts daemon when config hash changes`` () =
    let action = decideDaemonAction true "old-hash" "new-hash"
    test <@ action = Restart @>

[<Fact(Timeout = 5000)>]
let ``decideDaemonAction starts fresh when daemon not running`` () =
    let action = decideDaemonAction false "" "abc123"
    test <@ action = StartFresh @>

[<Fact(Timeout = 5000)>]
let ``decideDaemonAction starts fresh when not running even with matching hash`` () =
    let action = decideDaemonAction false "abc123" "abc123"
    test <@ action = StartFresh @>

// --- exit code paths via executeCommand ---

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors with count 0 returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            GetDiagnostics = fun _ _ -> async { return """{"count": 0}""" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors with IPC failure returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            GetDiagnostics = fun _ _ -> async { return failwith "connection refused" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Check returns exit code 1 when daemon dies during poll`` () =
    // `check` polls GetStatus in a loop until plugins are terminal. If the
    // daemon dies (or is gracefully stopped) mid-poll the RPC throws and we
    // must exit non-zero so wait-style scripts notice.
    let ipc =
        { fakeIpc () with
            WaitForScan = fun _ _ -> async { return "idle" }
            GetStatus = fun _ -> async { return failwith "pipe is broken" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Check [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors --wait blocks on WaitForComplete before reading diagnostics`` () =
    let mutable waitCalled = false
    let mutable waitFinishedBeforeDiagnostics = false
    let mutable diagnosticsCalled = false

    let ipc =
        { fakeIpc () with
            WaitForComplete =
                fun _ _ ->
                    async {
                        waitCalled <- true
                        return "{}"
                    }
            GetDiagnostics =
                fun _ _ ->
                    async {
                        diagnosticsCalled <- true
                        waitFinishedBeforeDiagnostics <- waitCalled
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [ Wait ])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ waitCalled @>
    test <@ diagnosticsCalled @>
    test <@ waitFinishedBeforeDiagnostics @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors without --wait does not call WaitForComplete`` () =
    let mutable waitCalled = false

    let ipc =
        { fakeIpc () with
            WaitForComplete =
                fun _ _ ->
                    async {
                        waitCalled <- true
                        return "{}"
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ not waitCalled @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors --wait returns exit code 2 when WaitForComplete times out`` () =
    // The daemon raises TimeoutException when its internal deadline fires; we simulate that
    // directly here rather than blocking past a client-side timeout.
    let ipc =
        { fakeIpc () with
            WaitForComplete =
                fun _ _ -> async { return raise (TimeoutException("WaitForComplete timed out — still running: plug")) }
            GetDiagnostics = fun _ _ -> async { return """{"count": 0}""" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [ Wait; Timeout 1 ])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 2 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors --wait returns exit code 2 when daemon dies mid-wait`` () =
    // Simulates the daemon being gracefully stopped (or crashing) while a client
    // is blocked in WaitForComplete: the RPC stream breaks and the StreamJsonRpc
    // call throws an IOException-shaped error. The waiter must surface a non-zero
    // exit so wait-based scripts (`fs-hot-watch errors --wait`, etc.) don't
    // silently succeed when the daemon disappears.
    let ipc =
        { fakeIpc () with
            WaitForComplete = fun _ _ -> async { return raise (System.IO.IOException("pipe is broken")) }
            GetDiagnostics = fun _ _ -> async { return """{"count": 0}""" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [ Wait ])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 2 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors --wait returns exit code 1 when diagnostics report errors`` () =
    let ipc =
        { fakeIpc () with
            WaitForComplete = fun _ _ -> async { return "{}" }
            GetDiagnostics =
                fun _ _ ->
                    async {
                        return
                            """{"count": 1, "files": {"src/Foo.fs": [{"plugin":"check","message":"err","severity":"error","line":1,"column":1}]}, "statuses": {}}"""
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [ Wait ])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors with invalid flag combination exits 2 without touching daemon`` () =
    let mutable launched = false

    let ipc =
        { fakeIpc () with
            LaunchDaemon = fun _ _ _ -> launched <- true
            IsRunning = fun _ -> false }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [ Timeout 30 ])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 2 @>
    test <@ not launched @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Build with status passed returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return """{"status": "passed"}""" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Build [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Build with status failed returns exit code 1`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return """{"status": "failed"}""" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Build [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Build with plain text returns exit code 0`` () =
    let ipc =
        { fakeIpc () with
            TriggerBuild = fun _ -> async { return "build completed successfully" } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Build [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>

// --- executeCommand for Build, Test, Format, Lint, Errors, Check ---

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Build [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Test [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ cmdName = "run-tests" @>

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Format [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (FormatCheck [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ errorFilter = "format-check" @>

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Lint [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ errorFilter = "lint" @>

[<Fact(Timeout = 5000)>]
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
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Errors [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 5000)>]
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
                        return completedStatusJson
                    }
            GetDiagnostics =
                fun _ _ ->
                    async {
                        getErrorsCalled <- true
                        return """{"count": 0}"""
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Check [])
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ waitForScanCalled @>
    test <@ getStatusCalled @>
    test <@ getErrorsCalled @>

// --- executeCommand for Rerun ---

[<Fact(Timeout = 5000)>]
let ``executeCommand Rerun calls rerunPlugin with plugin name`` () =
    let mutable calledWithName = ""

    let ipc =
        { fakeIpc () with
            RerunPlugin =
                fun _ name ->
                    async {
                        calledWithName <- name
                        return """{}"""
                    } }

    let result =
        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            ipc
            "/tmp"
            "pipe"
            (Rerun "coverage-ratchet")
            defaultGlobalOptions
            fakeConfig
            30.0

    test <@ result = 0 @>
    test <@ calledWithName = "coverage-ratchet" @>

// --- Regression tests for bug fixes ---

/// Run a test that triggers daemon startup failure using an isolated temp dir
/// so that killStaleDaemon cannot read another test's PID file.
let private withStartupFailure command =
    withTempDir "cli-fail" (fun tmpDir ->
        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc tmpDir "pipe" command defaultGlobalOptions fakeConfig 0.0)

[<Fact(Timeout = 5000)>]
let ``executeCommand Check returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Check []) = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Build returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Build []) = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Test returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Test []) = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Lint returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Lint []) = 1 @>

[<Fact(Timeout = 5000)>]
let ``executeCommand Errors returns 1 when daemon startup fails`` () =
    test <@ withStartupFailure (Errors []) = 1 @>

// --- computeLaunchCommand tests ---

[<Fact(Timeout = 5000)>]
let ``computeLaunchCommand with dotnet process path returns dotnet tool run`` () =
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/dotnet"
    test <@ exe = "/usr/local/bin/dotnet" @>
    test <@ prefix.Contains("fs-hot-watch") @>

[<Fact(Timeout = 5000)>]
let ``computeLaunchCommand with native exe returns exe directly`` () =
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/fs-hot-watch"
    test <@ exe = "/usr/local/bin/fs-hot-watch" @>
    test <@ prefix = "" @>

[<Fact(Timeout = 5000)>]
let ``computeLaunchCommand with dotnet.exe on Windows returns dotnet tool run`` () =
    let (exe, prefix) = computeLaunchCommand """C:\Program Files\dotnet\dotnet.exe"""
    test <@ exe = """C:\Program Files\dotnet\dotnet.exe""" @>
    test <@ prefix.Contains("fs-hot-watch") @>
