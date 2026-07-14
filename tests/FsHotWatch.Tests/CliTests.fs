[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
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

/// A currently-valid leaf command name, DERIVED from the command tree rather
/// than hard-coded. The tests below only need "some known command" to attach a
/// bad/help flag to; naming a specific verb meant they broke whenever that verb
/// was retired (the `test`→`check` churn during the verb collapse bit these
/// twice). Deriving the first leaf (a plain command, not a subcommand group)
/// keeps them fresh across any future verb changes.
let private someKnownCommand =
    match tree with
    | Group g ->
        g.Children
        |> List.pick (function
            | Leaf _ as node -> Some(CommandTree.name node)
            | Group _ -> None)
    | Leaf _ -> failwith "expected a command group at the CLI root"

[<Fact(Timeout = 15000)>]
let ``parse empty args returns HelpRequested`` () =
    match CommandTree.parse tree [||] with
    | Error(HelpRequested _) -> ()
    | other -> failwith $"Expected HelpRequested, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``parse start returns Start`` () =
    test <@ CommandTree.parse tree [| "start" |] = Ok Start @>

[<Fact(Timeout = 15000)>]
let ``parse stop returns Stop`` () =
    test <@ CommandTree.parse tree [| "stop" |] = Ok Stop @>

[<Fact(Timeout = 15000)>]
let ``parse check returns Check with no flags`` () =
    test <@ CommandTree.parse tree [| "check" |] = Ok(Check []) @>

[<Fact(Timeout = 15000)>]
let ``parse check --run-once returns Check RunOnce`` () =
    test <@ CommandTree.parse tree [| "check"; "--run-once" |] = Ok(Check [ RunOnce ]) @>

[<Fact(Timeout = 15000)>]
let ``parse test-rerun returns TestRerun with no flags`` () =
    test <@ CommandTree.parse tree [| "test-rerun" |] = Ok(TestRerun []) @>

[<Fact(Timeout = 15000)>]
let ``parse test-rerun --filter-class returns TestRerun FilterClass`` () =
    test
        <@
            CommandTree.parse tree [| "test-rerun"; "--filter-class"; "*CryptoTests*" |] = Ok(
                TestRerun [ FilterClass "*CryptoTests*" ]
            )
        @>

[<Fact(Timeout = 15000)>]
let ``parse test-rerun --filter-trait returns TestRerun FilterTrait`` () =
    test
        <@
            CommandTree.parse tree [| "test-rerun"; "--filter-trait"; "Category=Browser" |] = Ok(
                TestRerun [ FilterTrait "Category=Browser" ]
            )
        @>

[<Fact(Timeout = 15000)>]
let ``parse test-rerun --wait-sec returns TestRerun WaitSec`` () =
    test <@ CommandTree.parse tree [| "test-rerun"; "--wait-sec"; "300" |] = Ok(TestRerun [ WaitSec 300 ]) @>

[<Fact(Timeout = 15000)>]
let ``parse test-rerun rejects --run-once`` () =
    // --run-once belongs on `fshw test` (forward-progress); test-rerun is daemon-only.
    match CommandTree.parse tree [| "test-rerun"; "--run-once" |] with
    | Error(UnknownFlag _) -> ()
    | other -> failwith $"Expected UnknownFlag, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``parse format returns Format with no flags`` () =
    test <@ CommandTree.parse tree [| "format" |] = Ok(Format []) @>

[<Fact(Timeout = 15000)>]
let ``parse status returns Status None`` () =
    test <@ CommandTree.parse tree [| "status" |] = Ok(Status None) @>

[<Fact(Timeout = 15000)>]
let ``parse status with plugin returns Status Some`` () =
    test <@ CommandTree.parse tree [| "status"; "lint" |] = Ok(Status(Some "lint")) @>

[<Fact(Timeout = 15000)>]
let ``parse scan returns Scan`` () =
    match CommandTree.parse tree [| "scan" |] with
    | Ok Scan -> ()
    | other -> failwith $"Expected Ok Scan, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``parse rerun <name> returns Rerun`` () =
    test <@ CommandTree.parse tree [| "rerun"; "coverage-ratchet" |] = Ok(Rerun "coverage-ratchet") @>

[<Fact(Timeout = 15000)>]
let ``parse coverage refresh-baseline returns Coverage RefreshBaseline`` () =
    test
        <@
            CommandTree.parse tree [| "coverage"; "refresh-baseline" |] = Ok(
                FsHotWatch.Cli.Program.Coverage FsHotWatch.Cli.Program.RefreshBaseline
            )
        @>

[<Fact(Timeout = 15000)>]
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
              TimeoutSec = None
              ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect }

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
            { defaultTestConfig () with
                Build = None
                Format = FormatMode.Off
                Lint = false
                Cache = CacheBackendConfig.NoCache
                Tests =
                    Some
                        {| BeforeRun = None
                           Extensions = []
                           Projects = [ makeProj "ProjA" true; makeProj "ProjB" true; makeProj "ProjOptOut" false ]
                           CoverageDir = covDir
                           DependsOn = [] |} }

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

[<Fact(Timeout = 15000)>]
let ``parse init returns Init`` () =
    test <@ CommandTree.parse tree [| "init" |] = Ok Init @>

[<Fact(Timeout = 15000)>]
let ``parse unknown command returns UnknownCommand`` () =
    match CommandTree.parse tree [| "warnings" |] with
    // CommandTree 0.6.0: UnknownCommand carries (input, rest, groupPath). A root-level
    // unknown command has an empty groupPath; rest is the raw argv past the token.
    | Error(UnknownCommand("warnings", [||], [])) -> ()
    | other -> failwith $"Expected UnknownCommand, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``parse unknown command keeps trailing args in rest`` () =
    match CommandTree.parse tree [| "warnings"; "--verbose"; "x" |] with
    | Error(UnknownCommand("warnings", [| "--verbose"; "x" |], [])) -> ()
    | other -> failwith $"Expected UnknownCommand with rest, got %A{other}"

// --- reportParseError / renderParseError tests ---
//
// These assert the strict-CLI contract: garbage/invalid input renders a clear
// error PLUS the nearest subcommand/group help and returns a non-zero exit code,
// uniformly via CommandTree.renderParseError + isError.

/// Run `f`, capturing everything it writes to stderr, and return (stderr, result).
let private captureStderr (f: unit -> 'a) : string * 'a =
    let original = Console.Error
    use sw = new StringWriter()
    Console.SetError(sw)

    try
        let result = f ()
        sw.Flush()
        sw.ToString(), result
    finally
        Console.SetError(original)

[<Fact(Timeout = 15000)>]
let ``reportParseError on a known command with a bad flag renders the error plus help and exits non-zero`` () =
    // `--all` is not a flag on any known command → UnknownFlag. This is the case
    // the repo-owner wants to stop being masked when run outside a repo.
    let err =
        match spec.Parse [| someKnownCommand; "--all" |] with
        | Error e -> e
        | Ok _ -> failwith $"expected a parse error for `{someKnownCommand} --all`"

    let stderr, exitCode = captureStderr (fun () -> reportParseError err)

    test <@ exitCode <> 0 @>
    // Mentions the offending flag...
    test <@ stderr.Contains("--all") @>
    // ...and renders the command's own help (the command name appears).
    test <@ stderr.Contains(someKnownCommand) @>

[<Fact(Timeout = 15000)>]
let ``reportParseError on a nested unknown command fails hard with non-zero exit`` () =
    // `config bogus` — `config` is a known GROUP, `bogus` is an unknown child →
    // UnknownCommand with a non-empty groupPath. No daemon passthrough for this.
    let err =
        match spec.Parse [| "config"; "bogus" |] with
        | Error e -> e
        | Ok _ -> failwith "expected a parse error for `config bogus`"

    match err with
    | UnknownCommand(_, _, _ :: _) -> ()
    | other -> failwith $"expected a nested UnknownCommand, got %A{other}"

    let stderr, exitCode = captureStderr (fun () -> reportParseError err)
    test <@ exitCode <> 0 @>
    test <@ stderr.Length > 0 @>

[<Fact(Timeout = 15000)>]
let ``reportParseError returns 0 for HelpRequested`` () =
    // isError is false for Help/Version — informational, exit zero.
    let err =
        match spec.Parse [| someKnownCommand; "--help" |] with
        | Error e -> e
        | Ok _ -> failwith "expected HelpRequested"

    let _, exitCode = captureStderr (fun () -> reportParseError err)
    test <@ exitCode = 0 @>

// classifyParse is the pure dispatch that encodes the strict-CLI ordering: all
// repo-independent decisions (help/version + every genuine flag/arg error and a
// nested unknown command) resolve BEFORE the repo-root lookup; only Ok and a
// root-level unknown command defer to the daemon path.

[<Fact(Timeout = 15000)>]
let ``classifyParse Ok yields RunCommand`` () =
    match classifyParse (spec.Parse [| "start" |]) with
    | RunCommand([], Start) -> ()
    | other -> failwith $"expected RunCommand([], Start), got %A{other}"

[<Fact(Timeout = 15000)>]
let ``classifyParse help yields RepoIndependent 0`` () =
    let _, dispatch =
        captureStderr (fun () -> classifyParse (spec.Parse [| someKnownCommand; "--help" |]))

    test <@ dispatch = RepoIndependent 0 @>

[<Fact(Timeout = 15000)>]
let ``classifyParse version yields RepoIndependent 0`` () =
    let _, dispatch =
        captureStderr (fun () -> classifyParse (spec.Parse [| "--version" |]))

    test <@ dispatch = RepoIndependent 0 @>

[<Fact(Timeout = 15000)>]
let ``classifyParse unknown flag yields RepoIndependent non-zero (not masked, no repo needed)`` () =
    let stderr, dispatch =
        captureStderr (fun () -> classifyParse (spec.Parse [| someKnownCommand; "--all" |]))

    match dispatch with
    | RepoIndependent code -> test <@ code <> 0 @>
    | other -> failwith $"expected RepoIndependent, got %A{other}"

    test <@ stderr.Contains("--all") @>

[<Fact(Timeout = 15000)>]
let ``classifyParse nested unknown command yields RepoIndependent non-zero`` () =
    let _, dispatch =
        captureStderr (fun () -> classifyParse (spec.Parse [| "config"; "bogus" |]))

    match dispatch with
    | RepoIndependent code -> test <@ code <> 0 @>
    | other -> failwith $"expected RepoIndependent for nested unknown, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``classifyParse root unknown command yields RootUnknownCommand with raw rest`` () =
    match classifyParse (spec.Parse [| "deploy"; "--fast"; "x" |]) with
    | RootUnknownCommand("deploy", [| "--fast"; "x" |], UnknownCommand("deploy", _, [])) -> ()
    | other -> failwith $"expected RootUnknownCommand, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``unknownCommandReply round-trips through isUnknownCommandReply`` () =
    test <@ FsHotWatch.Ipc.isUnknownCommandReply (FsHotWatch.Ipc.unknownCommandReply "bogus") @>
    // The sentinel carries the command name so a consumer can render it.
    test <@ (FsHotWatch.Ipc.unknownCommandReply "bogus").Contains("bogus") @>
    test <@ not (FsHotWatch.Ipc.isUnknownCommandReply """{"status":"passed"}""") @>
    test <@ not (FsHotWatch.Ipc.isUnknownCommandReply "plain text") @>
    test <@ not (FsHotWatch.Ipc.isUnknownCommandReply null) @>

// --- GlobalSpec.Parse tests ---

[<Fact(Timeout = 15000)>]
let ``globalSpec parse with --verbose returns Verbose flag`` () =
    match spec.Parse [| "--verbose"; "start" |] with
    | Ok(globals, Start) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok(Verbose, Start), got %A{other}"

[<Fact(Timeout = 15000)>]
let ``globalSpec parse with -v returns Verbose flag`` () =
    match spec.Parse [| "-v"; "stop" |] with
    | Ok(globals, Stop) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok(Verbose, Stop), got %A{other}"

[<Fact(Timeout = 15000)>]
let ``globalSpec parse with --log-level returns LogLevel flag`` () =
    match spec.Parse [| "--log-level"; "debug"; "start" |] with
    | Ok(globals, Start) -> test <@ globals = [ LogLevel "debug" ] @>
    | other -> failwith $"Expected Ok(LogLevel debug, Start), got %A{other}"

[<Fact(Timeout = 15000)>]
let ``globalSpec parse with --no-cache returns NoCache flag`` () =
    match spec.Parse [| "--no-cache"; "check" |] with
    | Ok(globals, Check []) -> test <@ globals = [ NoCache ] @>
    | other -> failwith $"Expected Ok(NoCache, Check []), got %A{other}"

[<Fact(Timeout = 15000)>]
let ``globalSpec parse with multiple global flags`` () =
    match spec.Parse [| "--verbose"; "--no-cache"; "check" |] with
    | Ok(globals, Check []) -> test <@ globals = [ Verbose; NoCache ] @>
    | other -> failwith $"Expected Ok([Verbose; NoCache], Check []), got %A{other}"

[<Fact(Timeout = 15000)>]
let ``globalSpec parse with no global flags passes through`` () =
    match spec.Parse [| "scan" |] with
    | Ok(globals, Scan) -> test <@ globals |> List.isEmpty @>
    | other -> failwith $"Expected Ok([], Scan), got %A{other}"

[<Fact(Timeout = 15000)>]
let ``globalSpec parse with global flags after command`` () =
    match spec.Parse [| "start"; "--verbose" |] with
    | Ok(globals, Start) -> test <@ globals = [ Verbose ] @>
    | other -> failwith $"Expected Ok([Verbose], Start), got %A{other}"

// --- applyGlobalFlags tests ---

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags with NoCache returns noCache true`` () =
    test <@ (applyGlobalFlags [ NoCache ]).NoCache @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags with empty list returns noCache false`` () =
    let opts = applyGlobalFlags []
    test <@ not opts.NoCache @>
    test <@ not opts.NoWarnFail @>
    test <@ opts.DaemonExtraArgs = "" @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags builds daemon extra args`` () =
    test <@ (applyGlobalFlags [ Verbose; NoCache ]).DaemonExtraArgs = "--verbose --no-cache " @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags with LogLevel builds extra args`` () =
    test <@ (applyGlobalFlags [ LogLevel "debug" ]).DaemonExtraArgs = "--log-level debug " @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags with NoWarnFail returns noWarnFail true`` () =
    test <@ (applyGlobalFlags [ NoWarnFail ]).NoWarnFail @>

[<Fact(Timeout = 15000)>]
let ``applyGlobalFlags NoWarnFail does not add to daemon extra args`` () =
    test <@ (applyGlobalFlags [ NoWarnFail ]).DaemonExtraArgs = "" @>

// --- findRepoRoot tests ---

[<Fact(Timeout = 15000)>]
let ``findRepoRoot finds git repo`` () =
    withTempDir "cli-git" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "a", "b")
        Directory.CreateDirectory(nested) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>)

[<Fact(Timeout = 15000)>]
let ``findRepoRoot finds jj repo`` () =
    withTempDir "cli-jj" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(nested) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>)

// --- shutdown tests ---

[<Fact(Timeout = 20000)>]
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

[<Fact(Timeout = 15000)>]
let ``computePipeName is deterministic`` () =
    let name1 = computePipeName "/some/repo"
    let name2 = computePipeName "/some/repo"
    test <@ name1 = name2 @>

[<Fact(Timeout = 15000)>]
let ``computePipeName starts with prefix`` () =
    let name = computePipeName "/any/path"
    test <@ name.StartsWith("fshw-") @>

[<Fact(Timeout = 15000)>]
let ``computePipeName differs for different paths`` () =
    let name1 = computePipeName "/repo/a"
    let name2 = computePipeName "/repo/b"
    test <@ name1 <> name2 @>

[<Fact(Timeout = 15000)>]
let ``computePipeName has expected length`` () =
    let name = computePipeName "/test"
    // "fshw-" is 5 chars + 12 hex chars = 17
    test <@ name.Length = 17 @>

// --- CLI integration tests (real daemon + IPC) ---

[<Fact(Timeout = 20000)>]
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

[<Fact(Timeout = 20000)>]
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
        | RunOnceOutput.StatusView.Running _ -> ()
        | other -> failwithf "expected Running, got %A" other
    finally
        cts.Cancel()

        try
            task.Wait(TimeSpan.FromSeconds(3.0)) |> ignore
        with _ ->
            ()

        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 20000)>]
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
              fun _ctx _state args ->
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

        // An unrecognized command must come back as the distinguishable unknown-command
        // sentinel over IPC (not a plain echo), so the CLI can fail hard on it.
        let unknown =
            IpcClient.runCommand pipeName "definitely-not-a-command" ""
            |> Async.RunSynchronously

        test <@ FsHotWatch.Ipc.isUnknownCommandReply unknown @>
        test <@ not (FsHotWatch.Ipc.isUnknownCommandReply "hello Claude") @>
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
    { defaultTestConfig () with
        Build = None
        Format = Off
        Lint = false
        Cache = FsHotWatch.Cli.DaemonConfig.NoCache }

/// Structured plugin-status JSON in the shape expected by parsePluginStatuses
/// (object per plugin, not a bare string). Using the bare-string shape made the
/// pollAndRender loop hang because isAllTerminal on an empty parse is false.
let private completedStatusJson =
    """{"plugin": {"status": "Completed at 2026-01-01T00:00:00Z", "subtasks": [], "activityTail": [], "lastRun": null}}"""

let private fakeIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "shutting down" }
      Scan = fun _ -> async { return "scan started" }
      ScanStatus = fun _ -> async { return "idle" }
      GetStatus = fun _ -> async { return completedStatusJson }
      GetPluginStatus = fun _ _ -> async { return "{}" }
      RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name }
      GetDiagnostics = fun _ _ -> async { return """{"count": 0, "files": {}}""" }
      WaitForScan = fun _ _ -> async { return "idle" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "{}" }
      FormatAll = fun _ -> async { return "formatted 0 files" }
      RerunPlugin = fun _ _ -> async { return "{}" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> () }

/// Run `executeCommand` with the common test defaults.
let private exec (ipc: IpcOps) (command: Command) : int =
    executeCommand (fun _ -> Unchecked.defaultof<_>) ipc "/tmp" "pipe" command defaultGlobalOptions fakeConfig 30.0

[<Fact(Timeout = 15000)>]
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

    let result = exec ipc Stop

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 15000)>]
let ``executeCommand Config Check prints OK and returns 0`` () =
    let result = exec (fakeIpc ()) (Config ConfigCommand.Check)

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``parse config check returns Config ConfigCommand.Check`` () =
    test <@ CommandTree.parse tree [| "config"; "check" |] = Ok(Config ConfigCommand.Check) @>

[<Fact(Timeout = 15000)>]
let ``executeCommand Status returns 0`` () =
    let result = exec (fakeIpc ()) (Status None)

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``executePluginCommand proxies a recognized command to IPC`` () =
    let mutable cmdName = ""
    let mutable argsSeen = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ cmd args ->
                    async {
                        cmdName <- cmd
                        argsSeen <- args
                        return "result"
                    } }

    // A real plugin result (not the unknown-command sentinel) → Handled with the
    // rendered exit code, and the raw rest args are forwarded verbatim.
    let result =
        executePluginCommand ipc "pipe" defaultGlobalOptions "warnings" "--verbose x"

    test <@ result = Handled 0 @>
    test <@ cmdName = "warnings" @>
    test <@ argsSeen = "--verbose x" @>

[<Fact(Timeout = 15000)>]
let ``executePluginCommand reports NotRecognized when daemon returns unknown-command sentinel`` () =
    // The daemon's RunCommand replies with the unknown-command sentinel when the
    // plugin host doesn't recognize the command. The CLI must surface this distinctly
    // so the caller can fail hard with the canonical parse error.
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name } }

    let result = executePluginCommand ipc "pipe" defaultGlobalOptions "bogus" ""
    test <@ result = NotRecognized @>

[<Fact(Timeout = 15000)>]
let ``executePluginCommand reports DaemonUnavailable when IPC throws (with hint)`` () =
    // TimeoutException maps to a known recovery hint → the Some-hint branch.
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ _ _ -> async { return raise (TimeoutException("no daemon")) } }

    let result = executePluginCommand ipc "pipe" defaultGlobalOptions "bogus" ""
    test <@ result = DaemonUnavailable @>

[<Fact(Timeout = 15000)>]
let ``executePluginCommand reports DaemonUnavailable when IPC throws (no hint)`` () =
    // A plain exception has no known hint → the None-hint branch.
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ _ _ -> async { return raise (InvalidOperationException("pipe gone")) } }

    let result = executePluginCommand ipc "pipe" defaultGlobalOptions "bogus" ""
    test <@ result = DaemonUnavailable @>

[<Fact(Timeout = 15000)>]
let ``forwardRootUnknownCommand returns the daemon exit code when handled`` () =
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ _ _ -> async { return "ran" } }

    let mutable renderCalled = false

    let result =
        forwardRootUnknownCommand ipc "pipe" defaultGlobalOptions "plugin-cmd" "" (fun () ->
            renderCalled <- true
            99)

    test <@ result = 0 @>
    test <@ not renderCalled @>

[<Fact(Timeout = 15000)>]
let ``forwardRootUnknownCommand fails hard via renderErr when daemon does not recognize the command`` () =
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name } }

    let mutable renderCalled = false

    // The not-recognized path must invoke renderErr (which prints the canonical
    // error+help and returns the non-zero exit code) — garbage never silently succeeds.
    let result =
        forwardRootUnknownCommand ipc "pipe" defaultGlobalOptions "bogus" "" (fun () ->
            renderCalled <- true
            7)

    test <@ result = 7 @>
    test <@ renderCalled @>

[<Fact(Timeout = 15000)>]
let ``forwardRootUnknownCommand returns 1 when the daemon is unavailable`` () =
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ _ _ -> async { return raise (TimeoutException("no daemon")) } }

    let result =
        forwardRootUnknownCommand ipc "pipe" defaultGlobalOptions "bogus" "" (fun () -> 7)

    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``executeCommand Scan calls scan IPC`` () =
    let mutable called = false

    let ipc =
        { fakeIpc () with
            Scan =
                fun _ ->
                    async {
                        called <- true
                        return "scan started"
                    } }

    let result = exec ipc Scan

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 15000)>]
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

    let result = exec ipc (Status(Some "lint"))

    test <@ result = 0 @>
    test <@ calledWith = "lint" @>

[<Fact(Timeout = 15000)>]
let ``executeCommand Start exits 2 when no projects are discovered`` () =
    // Fail-fast contract: when project discovery would return 0, Start
    // exits 2 (config error) BEFORE creating the daemon, acquiring the
    // lockfile, or writing the pidfile — so a project-less directory
    // never spins up a daemon that would idle forever.
    withTempDir "cli-start-zero-projects" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let mutable createDaemonCalled = false

        let createDaemon (_: string) : Daemon =
            createDaemonCalled <- true
            Unchecked.defaultof<Daemon>

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        let exitCode =
            executeCommand createDaemon ipc tmpDir "fshw-test-pipe" Start defaultGlobalOptions fakeConfig 30.0

        test <@ exitCode = 2 @>
        // Pre-check fires before any daemon work, so the factory must not
        // be invoked and no .fshw state should be left behind.
        test <@ not createDaemonCalled @>
        test <@ not (Directory.Exists(Path.Combine(tmpDir, ".fshw"))) @>)

[<Fact(Timeout = 15000)>]
let ``executeCommand Check exits 2 when no projects are discovered`` () =
    // Same fail-fast contract as Start: project-requiring commands must
    // propagate exit code 2 (config error) when zero projects are
    // discoverable, instead of launching a daemon that immediately exits
    // 2 itself and surfacing as "Failed to start daemon" + exit 1.
    withTempDir "cli-check-zero-projects" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let mutable launched = false

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false
                LaunchDaemon = fun _ _ _ -> launched <- true }

        let exitCode =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                ipc
                tmpDir
                "fshw-test-pipe"
                (Check [])
                defaultGlobalOptions
                fakeConfig
                30.0

        test <@ exitCode = 2 @>
        // Pre-check fires before any daemon work; daemon launch is skipped.
        test <@ not launched @>)

[<Fact(Timeout = 15000)>]
let ``executeCommand Start with fake daemon throws on null daemon`` () =
    // Use a unique temp dir to avoid writing the test process PID to /tmp/.fshw/daemon.pid
    // where killStaleDaemon from other tests would read it and kill the test process.
    withTempDir "cli-start" (fun tmpDir ->
        // Stage a discoverable .fsproj so the failIfNoProjects pre-check
        // passes and execution actually reaches `createDaemon`.
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Stub.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

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

[<Fact(Timeout = 15000)>]
let ``executeCommand returns 1 when IPC fails`` () =
    let ipc =
        { fakeIpc () with
            GetDiagnostics = fun _ _ -> async { return failwith "connection refused" } }

    let result = exec ipc (Status None)

    test <@ result = 1 @>

// --- decideDaemonAction tests ---

[<Fact(Timeout = 15000)>]
let ``decideDaemonAction reuses running daemon with matching config`` () =
    let action = decideDaemonAction true "abc123" "abc123"
    test <@ action = Reuse @>

[<Fact(Timeout = 15000)>]
let ``decideDaemonAction restarts daemon when config hash changes`` () =
    let action = decideDaemonAction true "old-hash" "new-hash"
    test <@ action = Restart @>

[<Fact(Timeout = 15000)>]
let ``decideDaemonAction starts fresh when daemon not running`` () =
    let action = decideDaemonAction false "" "abc123"
    test <@ action = StartFresh @>

[<Fact(Timeout = 15000)>]
let ``decideDaemonAction starts fresh when not running even with matching hash`` () =
    let action = decideDaemonAction false "abc123" "abc123"
    test <@ action = StartFresh @>

// --- Daemon readiness gate (AUTOMATION-66) ---

/// A stand-in whose type name contains "ConnectionLost" — exercises the
/// StreamJsonRpc connection-loss detection without a compile-time dependency on
/// the transport assembly (the production match is by type-name substring).
type private FakeConnectionLostException() =
    inherit exn("connection lost")

[<Fact(Timeout = 15000)>]
let ``isTransientConnectFault recognises connect-phase transients`` () =
    test <@ isTransientConnectFault (TimeoutException("The operation has timed out")) @>
    test <@ isTransientConnectFault (System.IO.IOException("pipe is broken")) @>
    test <@ isTransientConnectFault (System.IO.EndOfStreamException()) @>
    test <@ isTransientConnectFault (System.ObjectDisposedException("pipe")) @>
    test <@ isTransientConnectFault (FakeConnectionLostException()) @>
    // ...seen through an AggregateException / inner-exception chain (as produced
    // by Async.RunSynchronously wrapping the connect fault).
    test <@ isTransientConnectFault (AggregateException(TimeoutException("timed out"))) @>

[<Fact(Timeout = 15000)>]
let ``isTransientConnectFault ignores non-connect faults`` () =
    test <@ not (isTransientConnectFault (exn "boom")) @>
    test <@ not (isTransientConnectFault (InvalidOperationException("bad state"))) @>
    test <@ not (isTransientConnectFault null) @>

[<Fact(Timeout = 15000)>]
let ``decideReadinessStep proceeds when the probe succeeds`` () =
    test <@ decideReadinessStep (Ok()) true false = ReadinessStep.ProceedReady @>

[<Fact(Timeout = 15000)>]
let ``decideReadinessStep keeps waiting on a transient fault while alive and in-budget`` () =
    let step = decideReadinessStep (Error(TimeoutException("timed out"))) true false
    test <@ step = ReadinessStep.KeepWaiting @>

[<Fact(Timeout = 15000)>]
let ``decideReadinessStep fails fast when the daemon process is gone`` () =
    let step = decideReadinessStep (Error(TimeoutException("timed out"))) false false
    test <@ step = ReadinessStep.FailCrashed @>

[<Fact(Timeout = 15000)>]
let ``decideReadinessStep times out when the deadline passes while still transient`` () =
    let step = decideReadinessStep (Error(TimeoutException("timed out"))) true true
    test <@ step = ReadinessStep.FailTimedOut @>

[<Fact(Timeout = 15000)>]
let ``decideReadinessStep proceeds on a non-connect probe error (daemon reached)`` () =
    // A non-transient error means the daemon answered (just not cleanly); proceed
    // and let the real check surface it, regardless of liveness/deadline.
    test <@ decideReadinessStep (Error(exn "weird")) true false = ReadinessStep.ProceedReady @>
    test <@ decideReadinessStep (Error(exn "weird")) false true = ReadinessStep.ProceedReady @>

[<Fact(Timeout = 15000)>]
let ``waitForDaemonReadyWith retries transient faults then reports Ready`` () =
    // Simulate a daemon still cold-scanning: the first two probes time out, the
    // third answers. The gate must WAIT (retry) and then succeed — not surface
    // the startup-race timeout as a failure.
    let mutable calls = 0
    let mutable slept = 0

    let probe () =
        calls <- calls + 1

        if calls < 3 then
            Error(TimeoutException("The operation has timed out") :> exn)
        else
            Ok()

    let result =
        waitForDaemonReadyWith
            probe
            (fun () -> true) // daemon alive
            (fun () -> DateTime.UtcNow)
            (fun _ -> slept <- slept + 1) // no real sleep
            ignore
            10
            60.0

    test <@ result = DaemonReadiness.Ready @>
    test <@ calls = 3 @>
    test <@ slept = 2 @> // waited between the two transient failures

[<Fact(Timeout = 15000)>]
let ``waitForDaemonReadyWith fails fast as Crashed when the process died`` () =
    let mutable calls = 0

    let probe () =
        calls <- calls + 1
        Error(TimeoutException("timed out") :> exn)

    let result =
        waitForDaemonReadyWith
            probe
            (fun () -> false) // process gone
            (fun () -> DateTime.UtcNow)
            ignore
            ignore
            10
            60.0

    test <@ result = DaemonReadiness.Crashed @>
    test <@ calls = 1 @> // did not spin the deadline

[<Fact(Timeout = 15000)>]
let ``waitForDaemonReadyWith reports TimedOut when never responsive within the deadline`` () =
    // A monotonic fake clock that jumps past the deadline on the second read.
    let mutable ticks = 0
    let start = DateTime.UtcNow

    let now () =
        ticks <- ticks + 1
        start.AddSeconds(if ticks >= 2 then 120.0 else 0.0)

    let probe () =
        Error(TimeoutException("timed out") :> exn)

    let result = waitForDaemonReadyWith probe (fun () -> true) now ignore ignore 10 60.0

    test <@ result = DaemonReadiness.TimedOut @>

[<Fact(Timeout = 15000)>]
let ``daemonProcessAliveWith treats a missing pidfile as alive`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> false }

    test <@ daemonProcessAliveWith fileOps "/tmp/whatever" @>

[<Fact(Timeout = 15000)>]
let ``daemonProcessAliveWith reports a dead pid as not alive`` () =
    // A pid that cannot name a live process → GetProcessById throws → not alive.
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> true
            ReadAllText = fun _ -> "2000000000" }

    test <@ not (daemonProcessAliveWith fileOps "/tmp/whatever") @>

[<Fact(Timeout = 15000)>]
let ``daemonProcessAliveWith treats an unparseable pidfile as alive`` () =
    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> true
            ReadAllText = fun _ -> "not-a-number" }

    test <@ daemonProcessAliveWith fileOps "/tmp/whatever" @>

[<Fact(Timeout = 15000)>]
let ``daemonProcessAliveWith reports a live pid as alive`` () =
    let pid = System.Diagnostics.Process.GetCurrentProcess().Id

    let fileOps =
        { defaultFileOps with
            FileExists = fun _ -> true
            ReadAllText = fun _ -> string pid }

    test <@ daemonProcessAliveWith fileOps "/tmp/whatever" @>

[<Fact(Timeout = 15000)>]
let ``executeCommand Check retries a startup connect race then succeeds`` () =
    withTempDir "cli-check-startup-race" (fun tmpDir ->
        // Force the Reuse path (daemon already listening) so the readiness gate,
        // not a fresh launch, is what absorbs the race.
        let stateDir = Path.Combine(tmpDir, ".fshw")
        Directory.CreateDirectory(stateDir) |> ignore
        let hash = computeConfigHashWith defaultFileOps tmpDir Environment.ProcessPath
        File.WriteAllText(Path.Combine(stateDir, "config.hash"), hash)

        // The daemon is mid cold-scan: the first two GetStatus probes time out
        // (ConnectAsync starved), the third answers. The readiness gate must WAIT
        // and then let the check run green — never surface the timeout as exit 1.
        let mutable statusCalls = 0

        let getStatus () =
            statusCalls <- statusCalls + 1

            if statusCalls <= 2 then
                raise (TimeoutException("The operation has timed out"))
            else
                completedStatusJson

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> true
                WaitForScan = fun _ _ -> async { return "idle" }
                GetStatus = fun _ -> async { return getStatus () }
                GetDiagnostics = fun _ _ -> async { return """{"count": 0, "unchecked": 0}""" } }

        let result =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                ipc
                tmpDir
                "fshw-test-pipe"
                (Check [])
                defaultGlobalOptions
                fakeConfig
                30.0

        test <@ result = 0 @>
        test <@ statusCalls > 2 @>) // proves it retried past the transient timeouts

// --- exit code paths via executeCommand ---

[<Fact(Timeout = 15000)>]
let ``executeCommand Check returns exit code 2 when daemon dies during poll`` () =
    // `check` polls GetStatus in a loop until plugins are terminal. If the daemon
    // dies (or is gracefully stopped) mid-poll the RPC throws — the daemon never
    // produced a verdict, so the check is UN-COMPLETABLE. That is exit 2, NEVER
    // exit 1 (which an autonomous loop reads as "the daemon ran and found
    // failures"). See `withCheckIpc` / AUTOMATION-66.
    let ipc =
        { fakeIpc () with
            WaitForScan = fun _ _ -> async { return "idle" }
            GetStatus = fun _ -> async { return failwith "pipe is broken" } }

    let result = exec ipc (Check [])

    test <@ result = 2 @>

// --- executeCommand for TestRerun, Format, Check ---

[<Fact(Timeout = 15000)>]
let ``executeCommand TestRerun with no flags sends default waitSec and no filter`` () =
    let mutable capturedArgs = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ cmd args ->
                    async {
                        capturedArgs <- args
                        test <@ cmd = "run-tests" @>
                        return """{"status": "passed"}"""
                    } }

    let result = exec ipc (TestRerun [])

    test <@ result = 0 @>
    // The slot-wait budget always travels (default) so a long beforeRun chain
    // can't defeat the rerun; no filter is sent when no filter flag is given.
    test <@ capturedArgs = $"""{{"waitSec":{DefaultTestRerunWaitSec}}}""" @>
    test <@ not (capturedArgs.Contains("filter")) @>

[<Fact(Timeout = 15000)>]
let ``executeCommand TestRerun --filter-class forwards filter to run-tests IPC`` () =
    let mutable capturedArgs = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ _ args ->
                    async {
                        capturedArgs <- args
                        return """{"status": "passed"}"""
                    } }

    let result = exec ipc (TestRerun [ FilterClass "*CryptoTests*" ])

    test <@ result = 0 @>
    test <@ capturedArgs.Contains("--filter-class") @>
    test <@ capturedArgs.Contains("*CryptoTests*") @>

[<Fact(Timeout = 15000)>]
let ``executeCommand TestRerun --filter-trait forwards filter to run-tests IPC`` () =
    let mutable capturedArgs = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ _ args ->
                    async {
                        capturedArgs <- args
                        return """{"status": "passed"}"""
                    } }

    let result = exec ipc (TestRerun [ FilterTrait "Category=Browser" ])

    test <@ result = 0 @>
    test <@ capturedArgs.Contains("--filter-trait") @>
    test <@ capturedArgs.Contains("Category=Browser") @>

[<Fact>]
let ``RerunFilter.render combines class and trait filters`` () =
    let rendered =
        RerunFilter.render [ FilterClass "*CryptoTests*"; FilterTrait "Category=Browser" ]

    test <@ rendered = "--filter-class *CryptoTests* --filter-trait Category=Browser" @>

[<Fact>]
let ``RerunFilter.render quotes patterns containing whitespace`` () =
    let rendered = RerunFilter.render [ FilterClass "Foo Bar" ]
    test <@ rendered = "--filter-class \"Foo Bar\"" @>

[<Fact>]
let ``RerunFilter.render quotes only the value half of a trait pair`` () =
    let rendered = RerunFilter.render [ FilterTrait "Category=Slow Browser" ]
    test <@ rendered = "--filter-trait Category=\"Slow Browser\"" @>

[<Fact>]
let ``RerunFilter.render returns empty string for empty flag list`` () = test <@ RerunFilter.render [] = "" @>

[<Fact>]
let ``RerunFilter.render omits WaitSec (it is not an xUnit filter)`` () =
    // `--wait-sec` is a client-side slot-wait knob; it must never leak into the
    // xUnit runner arg string.
    test <@ RerunFilter.render [ WaitSec 300; FilterClass "*Foo*" ] = "--filter-class *Foo*" @>
    test <@ RerunFilter.render [ WaitSec 300 ] = "" @>

[<Fact>]
let ``RerunFilter.waitSec returns the flag value`` () =
    test <@ RerunFilter.waitSec [ WaitSec 42 ] = 42 @>

[<Fact>]
let ``RerunFilter.waitSec falls back to the default when absent`` () =
    test <@ RerunFilter.waitSec [ FilterClass "*Foo*" ] = DefaultTestRerunWaitSec @>

[<Fact(Timeout = 15000)>]
let ``executeCommand TestRerun --wait-sec forwards waitSec to run-tests IPC`` () =
    let mutable capturedArgs = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ _ args ->
                    async {
                        capturedArgs <- args
                        return """{"status": "passed"}"""
                    } }

    let result = exec ipc (TestRerun [ WaitSec 300 ])

    test <@ result = 0 @>
    test <@ capturedArgs.Contains("\"waitSec\":300") @>

[<Fact(Timeout = 15000)>]
let ``executeCommand TestRerun forwards both filter and waitSec`` () =
    let mutable capturedArgs = ""

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ _ args ->
                    async {
                        capturedArgs <- args
                        return """{"status": "passed"}"""
                    } }

    let result = exec ipc (TestRerun [ FilterClass "*CryptoTests*"; WaitSec 250 ])

    test <@ result = 0 @>
    test <@ capturedArgs.Contains("--filter-class") @>
    test <@ capturedArgs.Contains("\"waitSec\":250") @>

[<Fact(Timeout = 15000)>]
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

    let result = exec ipc (Format [])

    test <@ result = 0 @>
    test <@ called @>

[<Fact(Timeout = 15000)>]
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
                        return """{"count": 0, "unchecked": 0}"""
                    } }

    let result = exec ipc (Check [])

    test <@ result = 0 @>
    test <@ waitForScanCalled @>
    test <@ getStatusCalled @>
    test <@ getErrorsCalled @>

// --- executeCommand for Rerun ---

[<Fact(Timeout = 15000)>]
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

    let result = exec ipc (Rerun "coverage-ratchet")

    test <@ result = 0 @>
    test <@ calledWithName = "coverage-ratchet" @>

// --- Regression tests for bug fixes ---

/// Run a test that triggers daemon startup failure using an isolated temp dir
/// so that killStaleDaemon cannot read another test's PID file.
let private withStartupFailure command =
    withTempDir "cli-fail" (fun tmpDir ->
        // Stage a discoverable .fsproj so the failIfNoProjects pre-check
        // (hoisted into executeCommand for project-requiring commands) passes
        // and execution actually reaches the daemon-launch poll-timeout path
        // that this helper is meant to exercise.
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Stub.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc tmpDir "pipe" command defaultGlobalOptions fakeConfig 0.0)

[<Fact(Timeout = 15000)>]
let ``executeCommand Check returns 2 when daemon startup fails`` () =
    // A daemon that never comes up means the check could not run at all —
    // un-completable, so exit 2 (never exit 1, which reads as "failures found").
    test <@ withStartupFailure (Check []) = 2 @>

// --- computeLaunchCommand tests ---

[<Fact(Timeout = 15000)>]
let ``computeLaunchCommand: dotnet with no entry assembly falls back to tool run`` () =
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/dotnet" None
    test <@ exe = "/usr/local/bin/dotnet" @>
    test <@ prefix.Contains("fshw") @>

[<Fact(Timeout = 15000)>]
let ``computeLaunchCommand: native exe returns exe directly`` () =
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/fs-hot-watch" None
    test <@ exe = "/usr/local/bin/fs-hot-watch" @>
    test <@ prefix = "" @>

[<Fact(Timeout = 15000)>]
let ``computeLaunchCommand: dotnet.exe on Windows with no entry assembly falls back to tool run`` () =
    let (exe, prefix) =
        computeLaunchCommand """C:\Program Files\dotnet\dotnet.exe""" None

    test <@ exe = """C:\Program Files\dotnet\dotnet.exe""" @>
    test <@ prefix.Contains("fshw") @>

[<Fact(Timeout = 15000)>]
let ``computeLaunchCommand: dotnet with a real local dll spawns that same dll, not the pinned tool`` () =
    // Use this test assembly's own dll as an existing .dll path on disk.
    let dll = System.Reflection.Assembly.GetExecutingAssembly().Location
    test <@ dll.ToLowerInvariant().EndsWith(".dll") && File.Exists dll @>

    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/dotnet" (Some dll)
    test <@ exe = "/usr/local/bin/dotnet" @>
    // The launch must reference the dll directly (quoted), NOT `tool run`.
    test <@ prefix = $"\"%s{dll}\" " @>
    test <@ not (prefix.Contains "tool run") @>

[<Fact(Timeout = 15000)>]
let ``computeLaunchCommand: dotnet with a nonexistent dll path falls back to tool run`` () =
    let bogus = "/no/such/path/FsHotWatch.Cli.dll"
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/dotnet" (Some bogus)
    test <@ exe = "/usr/local/bin/dotnet" @>
    test <@ prefix.Contains("fshw") @>

[<Fact(Timeout = 15000)>]
let ``computeLaunchCommand: dotnet with empty entry-assembly location falls back to tool run`` () =
    // Single-file / shim publish reports an empty GetEntryAssembly().Location.
    let (exe, prefix) = computeLaunchCommand "/usr/local/bin/dotnet" (Some "")
    test <@ exe = "/usr/local/bin/dotnet" @>
    test <@ prefix.Contains("fshw") @>

// ---------------------------------------------------------------------------
// The merge gate's daemon commands (AUTOMATION-129)
//
// `RunCommand` dispatches on the COMMAND name. The gate used to call it with the
// PLUGIN name — `RunCommand "test-prune" "test-scope"` — so the host looked up a
// command called `test-prune`, found none, and returned the unknown-command
// sentinel. `parseTestScope` then correctly, and SILENTLY, read that as
// `ScopeUnknown`, which the merge gate correctly, and SILENTLY, treats as "not
// full-suite". Result: `fshw gate` had NO PATH TO A GREEN on any repo, ever — it
// exited 3 even when the whole suite had just run unfiltered.
//
// It failed in the safe direction, which is why nothing caught it. A gate that
// always refuses is never WRONG; it is merely useless, and the workaround for a
// useless gate is a hand-rolled bash harness making merge decisions.
//
// These tests pin the WIRE NAMES, which is the thing that was broken.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``readTestRun asks the daemon for the command named test-scope`` () =
    let mutable seen: (string * string) list = []

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ name args ->
                    async {
                        seen <- (name, args) :: seen
                        return """{"scope":"full","ranProjects":6,"totalProjects":6}"""
                    } }

    let run = readTestRun ipc "pipe"

    // The command name — not the plugin name — travels in the command slot.
    test <@ seen |> List.map fst = [ "test-scope" ] @>
    // ...and the daemon's answer is actually READ, rather than collapsing to
    // ScopeUnknown because the call never reached a handler.
    test <@ run.Scope = IpcParsing.FullSuite 6 @>

[<Fact(Timeout = 15000)>]
let ``an unknown-command reply is ScopeUnknown — a gate never goes green on a scope it did not establish`` () =
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name } }

    test <@ (readTestRun ipc "pipe").Scope = IpcParsing.ScopeUnknown @>

[<Fact(Timeout = 15000)>]
let ``requestFullSuiteScope sends set-scope with a PARSEABLE {"scope":"full"} payload`` () =
    // Doubly broken before: the command name was wrong AND the args were
    // `set-scope {"scope":"full"}`, which is not JSON — so even if it had been
    // routed, the handler's `JsonDocument.Parse` would have thrown and defaulted
    // to IMPACT. The gate would have asked for a full suite and been given a
    // filtered one.
    let mutable seen: (string * string) list = []

    let ipc =
        { fakeIpc () with
            RunCommand =
                fun _ name args ->
                    async {
                        seen <- (name, args) :: seen
                        return """{"scope":"full"}"""
                    } }

    requestFullSuiteScope ipc "pipe"

    match seen with
    | [ (name, args) ] ->
        test <@ name = "set-scope" @>

        use doc = System.Text.Json.JsonDocument.Parse(args)
        test <@ doc.RootElement.GetProperty("scope").GetString() = "full" @>
    | other -> failwith $"expected exactly one set-scope call, got %A{other}"
