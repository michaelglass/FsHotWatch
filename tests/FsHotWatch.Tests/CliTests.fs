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

/// A currently-valid leaf command name, DERIVED from the command tree rather than
/// hard-coded. The tests below only need "some known command" to hang a bad/help flag on,
/// and naming a specific verb broke them every time that verb was retired.
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
let ``parse invalidate returns Invalidate`` () =
    test <@ CommandTree.parse tree [| "invalidate" |] = Ok Invalidate @>

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
              CollectCoverage = cov
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
                           Excluded = []
                           Solution = None
                           CoverageDir = covDir
                           DependsOn = [] |} }

        let deleted = FsHotWatch.Cli.Program.refreshCoverageBaseline tmp config
        // 4 files total: 2 projects × (baseline + partial)
        test <@ deleted.Length = 4 @>

        // The plain cobertura file is neither baseline nor partial, so it stays.
        test <@ File.Exists(Path.Combine(tmp, covDir, "ProjA", "coverage.cobertura.xml")) @>
        test <@ not (File.Exists(Path.Combine(tmp, covDir, "ProjA", "coverage.baseline.cobertura.xml"))) @>
        test <@ not (File.Exists(Path.Combine(tmp, covDir, "ProjA", "coverage.partial.cobertura.xml"))) @>
        test <@ not (File.Exists(Path.Combine(tmp, covDir, "ProjB", "coverage.baseline.cobertura.xml"))) @>
        // The opt-out project is untouched.
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
    // UnknownCommand carries (input, rest, groupPath): a root-level unknown command has an
    // empty groupPath, and `rest` is the raw argv past the token.
    | Error(UnknownCommand("warnings", [||], [])) -> ()
    | other -> failwith $"Expected UnknownCommand, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``parse unknown command keeps trailing args in rest`` () =
    match CommandTree.parse tree [| "warnings"; "--verbose"; "x" |] with
    | Error(UnknownCommand("warnings", [| "--verbose"; "x" |], [])) -> ()
    | other -> failwith $"Expected UnknownCommand with rest, got %A{other}"

// --- reportParseError / renderParseError tests ---
//
// The strict-CLI contract: garbage or invalid input renders a clear error PLUS the nearest
// subcommand/group help, and returns a non-zero exit code.

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
    // `--all` is not a flag on any known command → UnknownFlag, which used to be masked when
    // run outside a repo.
    let err =
        match spec.Parse [| someKnownCommand; "--all" |] with
        | Error e -> e
        | Ok _ -> failwith $"expected a parse error for `{someKnownCommand} --all`"

    let stderr, exitCode = captureStderr (fun () -> reportParseError err)

    test <@ exitCode <> 0 @>
    test <@ stderr.Contains("--all") @>
    // The command's own help is rendered too, which is why its name appears.
    test <@ stderr.Contains(someKnownCommand) @>

[<Fact(Timeout = 15000)>]
let ``reportParseError on a nested unknown command fails hard with non-zero exit`` () =
    // `config` is a known GROUP and `bogus` an unknown child → UnknownCommand with a
    // non-empty groupPath, which gets no daemon passthrough.
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
    // Help and Version are informational, not errors.
    let err =
        match spec.Parse [| someKnownCommand; "--help" |] with
        | Error e -> e
        | Ok _ -> failwith "expected HelpRequested"

    let _, exitCode = captureStderr (fun () -> reportParseError err)
    test <@ exitCode = 0 @>

// classifyParse encodes the strict-CLI ordering: every repo-independent decision
// (help/version, genuine flag/arg errors, a nested unknown command) resolves BEFORE the
// repo-root lookup, and only Ok and a root-level unknown command defer to the daemon path.

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
[<Trait("Issue", "AUTOMATION-440")>]
let ``findRepoRoot finds git repo`` () =
    withTempDir "cli-git" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "a", "b")
        Directory.CreateDirectory(nested) |> ignore
        let metadata = Directory.CreateDirectory(Path.Combine(tmpDir, ".git"))
        File.WriteAllText(Path.Combine(metadata.FullName, "HEAD"), "ref: refs/heads/main\n")
        let result = findRepoRoot nested
        test <@ result = Some tmpDir @>)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-440")>]
let ``findRepoRoot finds linked git worktree metadata whose dotgit is a file`` () =
    // A Git linked worktree stores `.git` as a pointer file, not a directory.
    // `findRepoRoot` only needs Git's documented on-disk contract (a `gitdir:`
    // pointer to metadata carrying HEAD), so construct that contract directly:
    // invoking `git init`/`worktree add` here would make a pure parser test depend
    // on the host Git binary, its global configuration, hooks, and process timing.
    withTempDir "cli-linked-worktree" (fun tmpDir ->
        let metadata = Path.Combine(tmpDir, "main", ".git", "worktrees", "linked")
        Directory.CreateDirectory(metadata) |> ignore
        File.WriteAllText(Path.Combine(metadata, "HEAD"), "ref: refs/heads/linked\n")

        let linked = Path.Combine(tmpDir, "linked")
        let nested = Path.Combine(linked, "src", "nested")
        Directory.CreateDirectory(nested) |> ignore
        File.WriteAllText(Path.Combine(linked, ".git"), $"gitdir: {metadata}\n")

        test <@ File.Exists(Path.Combine(linked, ".git")) @>
        test <@ not (Directory.Exists(Path.Combine(linked, ".git"))) @>
        test <@ findRepoRoot nested = Some linked @>)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-440")>]
let ``findRepoRoot does not mistake an unrelated dotgit file for a repository`` () =
    withTempDir "cli-non-git-dotgit-file" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(nested) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, ".git"), "this is not a gitdir pointer")

        test <@ findRepoRoot nested = None @>)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-440")>]
let ``findRepoRoot does not mistake an arbitrary dotgit directory for a repository`` () =
    withTempDir "cli-non-git-dotgit-directory" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(nested) |> ignore
        Directory.CreateDirectory(Path.Combine(tmpDir, ".git")) |> ignore

        test <@ findRepoRoot nested = None @>)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-440")>]
let ``findRepoRoot does not accept a dangling linked-worktree gitdir pointer`` () =
    withTempDir "cli-dangling-gitdir-pointer" (fun tmpDir ->
        let nested = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(nested) |> ignore
        File.WriteAllText(Path.Combine(tmpDir, ".git"), "gitdir: missing-worktree-metadata")

        test <@ findRepoRoot nested = None @>)

[<Fact(Timeout = 15000)>]
[<Trait("Issue", "AUTOMATION-440")>]
let ``findRepoRoot preserves spaces at the end of a valid gitdir path`` () =
    if not (OperatingSystem.IsWindows()) then
        // Win32 normalizes trailing spaces in path components, so that platform
        // cannot represent this otherwise-valid Git pointer target.
        withTempDir "cli-spaced-gitdir-pointer" (fun tmpDir ->
            let metadata = Path.Combine(tmpDir, "metadata ")
            Directory.CreateDirectory(metadata) |> ignore
            File.WriteAllText(Path.Combine(metadata, "HEAD"), "ref: refs/heads/main\n")

            let checkout = Path.Combine(tmpDir, "checkout")
            let nested = Path.Combine(checkout, "src")
            Directory.CreateDirectory(nested) |> ignore
            File.WriteAllText(Path.Combine(checkout, ".git"), "gitdir: ../metadata ")

            test <@ findRepoRoot nested = Some checkout @>)

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
    daemon.Host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    let task = Async.StartAsTask(daemon.RunWithIpc(pipeName, cts))
    waitForIpcServer pipeName

    waitUntil
        (fun () ->
            match daemon.Host.GetStatus("my-lint") with
            | Some(Running _) -> true
            | _ -> false)
        5000

    try
        let result = IpcClient.getPluginStatus pipeName "my-lint" |> Async.RunSynchronously
        let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses result

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

        // An unrecognized command comes back as the distinguishable unknown-command sentinel
        // rather than a plain echo, so the CLI can fail hard on it.
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

/// Structured plugin-status JSON in the shape `parsePluginStatuses` expects — object per
/// plugin, not a bare string. The bare-string shape parses to an empty map, on which
/// `isAllTerminal` is false, so the pollAndRender loop hangs.
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
      Invalidate = fun _ -> async { return "invalidated" }
      IsRunning = fun _ -> true
      LaunchDaemon = fun _ _ _ -> () }

/// Run `executeCommand` with the common test defaults. "/tmp" is made to look like a repo
/// whose stubbed always-running daemon is THIS process's binary with the current config, so
/// `ensureDaemon` takes the Reuse path — otherwise the identity handshake restarts the fake
/// daemon on every call, costing a 1s shutdown sleep per test plus a real killStaleDaemon
/// walk over /tmp/.fshw.
let private exec (ipc: IpcOps) (command: Command) : int =
    Directory.CreateDirectory("/tmp/.fshw") |> ignore
    FsHotWatch.DaemonIdentity.recordCurrent "/tmp"
    File.WriteAllText("/tmp/.fshw/config.hash", computeConfigHashWith defaultFileOps "/tmp")
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

    let result =
        executePluginCommand ipc "pipe" defaultGlobalOptions "warnings" "--verbose x"

    test <@ result = Handled 0 @>
    test <@ cmdName = "warnings" @>
    test <@ argsSeen = "--verbose x" @>

[<Fact(Timeout = 15000)>]
let ``executePluginCommand reports NotRecognized when daemon returns unknown-command sentinel`` () =
    // The CLI must surface this distinctly, so the caller can fail hard with the canonical
    // parse error rather than treating the sentinel as output.
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name } }

    let result = executePluginCommand ipc "pipe" defaultGlobalOptions "bogus" ""
    test <@ result = NotRecognized @>

[<Fact(Timeout = 15000)>]
let ``executePluginCommand reports DaemonUnavailable when IPC throws (with hint)`` () =
    // TimeoutException maps to a known recovery hint — the Some-hint branch.
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ _ _ -> async { return raise (TimeoutException("no daemon")) } }

    let result = executePluginCommand ipc "pipe" defaultGlobalOptions "bogus" ""
    test <@ result = DaemonUnavailable @>

[<Fact(Timeout = 15000)>]
let ``executePluginCommand reports DaemonUnavailable when IPC throws (no hint)`` () =
    // A plain exception has no known hint — the None-hint branch.
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

    // The not-recognized path must invoke renderErr — which prints the canonical error+help
    // and returns the non-zero exit code — so garbage never silently succeeds.
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
    // Exit 2 lands BEFORE creating the daemon, acquiring the lockfile or writing the pidfile,
    // so a project-less directory never spins up a daemon that would idle forever.
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
        test <@ not createDaemonCalled @>
        test <@ not (Directory.Exists(Path.Combine(tmpDir, ".fshw"))) @>)

[<Fact(Timeout = 15000)>]
let ``executeCommand Check exits 2 when no projects are discovered`` () =
    // The same fail-fast as Start. Without it, the daemon launches, exits 2 itself, and the
    // whole thing surfaces as "Failed to start daemon" with exit 1.
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
        test <@ not launched @>)

[<Fact(Timeout = 15000)>]
let ``executeCommand Start with fake daemon throws on null daemon`` () =
    // A unique temp dir, so the test process PID is not written to /tmp/.fshw/daemon.pid
    // where another test's killStaleDaemon would read it and kill this process.
    withTempDir "cli-start" (fun tmpDir ->
        // A discoverable .fsproj, so the failIfNoProjects pre-check passes and execution
        // actually reaches `createDaemon`.
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

// --- decideRunningDaemonAction tests (AUTOMATION-147 identity handshake) ---

let private ident (v: string) (h: string) : FsHotWatch.DaemonIdentity.BinaryIdentity = { Version = v; ContentHash = h }

[<Fact(Timeout = 15000)>]
let ``decideRunningDaemonAction reuses running daemon with matching identity and config`` () =
    let action =
        decideRunningDaemonAction FsHotWatch.DaemonIdentity.IdentityVerdict.Match "abc123" "abc123"

    test <@ action = Reuse @>

[<Fact(Timeout = 15000)>]
let ``decideRunningDaemonAction restarts daemon when config hash changes`` () =
    let action =
        decideRunningDaemonAction FsHotWatch.DaemonIdentity.IdentityVerdict.Match "old-hash" "new-hash"

    test <@ action = RestartConfigChanged @>

[<Fact(Timeout = 15000)>]
let ``decideRunningDaemonAction restarts a different-binary daemon even when config matches`` () =
    // Every answer a stale daemon gives comes from the wrong code, whatever its config says.
    let recorded = ident "0.9.0" "cafebabe00000000"

    let action =
        decideRunningDaemonAction
            (FsHotWatch.DaemonIdentity.IdentityVerdict.Stale(
                FsHotWatch.DaemonIdentity.StaleReason.DifferentBinary recorded
            ))
            "abc123"
            "abc123"

    test <@ action = RestartStaleBinary(FsHotWatch.DaemonIdentity.StaleReason.DifferentBinary recorded) @>

[<Fact(Timeout = 15000)>]
let ``decideRunningDaemonAction restarts a daemon with no recorded identity`` () =
    // An old daemon predating the handshake never recorded an identity. It must be restarted
    // unilaterally, with no cooperation from the old build required.
    let action =
        decideRunningDaemonAction
            (FsHotWatch.DaemonIdentity.IdentityVerdict.Stale FsHotWatch.DaemonIdentity.StaleReason.NotRecorded)
            "abc123"
            "abc123"

    test <@ action = RestartStaleBinary FsHotWatch.DaemonIdentity.StaleReason.NotRecorded @>

[<Fact(Timeout = 15000)>]
let ``restartReasonLine names the reason for every restart and none for reuse`` () =
    test <@ restartReasonLine Reuse = None @>

    let noRecord =
        restartReasonLine (RestartStaleBinary FsHotWatch.DaemonIdentity.StaleReason.NotRecorded)

    test <@ noRecord.IsSome && noRecord.Value.Contains "no recorded binary identity" @>

    let different =
        restartReasonLine (
            RestartStaleBinary(FsHotWatch.DaemonIdentity.StaleReason.DifferentBinary(ident "0.9.0" "cafebabe"))
        )

    test <@ different.IsSome && different.Value.Contains "different fshw binary" @>
    test <@ different.Value.Contains "0.9.0" @>

    let config = restartReasonLine RestartConfigChanged
    test <@ config.IsSome && config.Value.Contains "config changed" @>

// --- Daemon readiness gate (AUTOMATION-66) ---

/// A stand-in whose type NAME contains "ConnectionLost" — exercises the StreamJsonRpc
/// connection-loss detection without a compile-time dependency on the transport assembly
/// (the production match is by type-name substring).
type private FakeConnectionLostException() =
    inherit exn("connection lost")

[<Fact(Timeout = 15000)>]
let ``isTransientConnectFault recognises connect-phase transients`` () =
    test <@ isTransientConnectFault (TimeoutException("The operation has timed out")) @>
    test <@ isTransientConnectFault (System.IO.IOException("pipe is broken")) @>
    test <@ isTransientConnectFault (System.IO.EndOfStreamException()) @>
    test <@ isTransientConnectFault (System.ObjectDisposedException("pipe")) @>
    test <@ isTransientConnectFault (FakeConnectionLostException()) @>
    // ...and through the AggregateException chain `Async.RunSynchronously` wraps it in.
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
    // A non-transient error means the daemon answered, just not cleanly — so proceed and let
    // the real check surface it, regardless of liveness or deadline.
    test <@ decideReadinessStep (Error(exn "weird")) true false = ReadinessStep.ProceedReady @>
    test <@ decideReadinessStep (Error(exn "weird")) false true = ReadinessStep.ProceedReady @>

[<Fact(Timeout = 15000)>]
let ``waitForDaemonReadyWith retries transient faults then reports Ready`` () =
    // A daemon still cold-scanning: the first two probes time out, the third answers. The
    // gate must retry and then succeed, not surface the startup race as a failure.
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
    // A pid that cannot name a live process: GetProcessById throws, so it reads as not alive.
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
        // Force the Reuse path (daemon already listening) so the readiness gate, not a fresh
        // launch, is what absorbs the race. Reuse also requires the identity handshake to
        // match, hence recording THIS process's identity as the daemon's.
        let stateDir = Path.Combine(tmpDir, ".fshw")
        Directory.CreateDirectory(stateDir) |> ignore
        let hash = computeConfigHashWith defaultFileOps tmpDir
        File.WriteAllText(Path.Combine(stateDir, "config.hash"), hash)
        FsHotWatch.DaemonIdentity.recordCurrent tmpDir

        // The daemon is mid cold-scan: the first two GetStatus probes time out with
        // ConnectAsync starved, the third answers. The gate must wait and let the check run
        // green, never surfacing the timeout as exit 1.
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
    // If the daemon dies mid-poll the RPC throws, and no verdict was ever produced, so the
    // check is UN-COMPLETABLE. That is exit 2, never exit 1 — which an autonomous loop reads
    // as "the daemon ran and found failures".
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
    // The slot-wait budget always travels, so a long beforeRun chain cannot defeat the
    // rerun; and no filter is sent when no filter flag was given.
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
    // `--wait-sec` is a client-side slot-wait knob, not something the runner understands.
    test <@ RerunFilter.render [ WaitSec 300; FilterClass "*Foo*" ] = "--filter-class *Foo*" @>
    test <@ RerunFilter.render [ WaitSec 300 ] = "" @>

[<Fact>]
let ``RerunFilter.render omits Project (it selects projects, it is not an xUnit filter)`` () =
    // AUTOMATION-272: `--project` picks WHICH test projects the daemon invokes and travels in
    // the run-tests payload. Leaked into the runner arg string it hands xUnit an option it
    // does not know, failing the very run the flag exists to aim.
    test <@ RerunFilter.render [ Project "Acme.Tests"; FilterClass "*Foo*" ] = "--filter-class *Foo*" @>
    test <@ RerunFilter.render [ Project "Acme.Tests" ] = "" @>

[<Fact>]
let ``RerunFilter.projects collects the named projects, de-duplicated`` () =
    // Repeatable, order preserved, and EMPTY means "every configured project" — the
    // historical behaviour, which stays the default (AUTOMATION-272).
    test <@ List.isEmpty (RerunFilter.projects []) @>
    test <@ List.isEmpty (RerunFilter.projects [ FilterClass "*Foo*"; WaitSec 30 ]) @>
    test <@ RerunFilter.projects [ Project "A" ] = [ "A" ] @>
    test <@ RerunFilter.projects [ Project "B"; FilterClass "*Foo*"; Project "A" ] = [ "B"; "A" ] @>
    test <@ RerunFilter.projects [ Project "A"; Project "A" ] = [ "A" ] @>

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

[<Fact(Timeout = 15000)>]
let ``executeCommand Invalidate clears the live workspace cache without shutdown`` () =
    let mutable invalidated = false
    let mutable shutdown = false

    let ipc =
        { fakeIpc () with
            Invalidate =
                fun _ ->
                    async {
                        invalidated <- true
                        return "invalidated"
                    }
            Shutdown =
                fun _ ->
                    async {
                        shutdown <- true
                        return "shutting down"
                    } }

    test <@ exec ipc Invalidate = 0 @>
    test <@ invalidated @>
    test <@ not shutdown @>

// --- Regression tests for bug fixes ---

/// Trigger a daemon startup failure in an isolated temp dir, so `killStaleDaemon` cannot
/// read another test's PID file.
let private withStartupFailure command =
    withTempDir "cli-fail" (fun tmpDir ->
        // A discoverable .fsproj, so the failIfNoProjects pre-check passes and execution
        // reaches the daemon-launch poll-timeout path this helper exists to exercise.
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "Stub.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let ipc =
            { fakeIpc () with
                IsRunning = fun _ -> false }

        executeCommand (fun _ -> Unchecked.defaultof<_>) ipc tmpDir "pipe" command defaultGlobalOptions fakeConfig 0.0)

[<Fact(Timeout = 15000)>]
let ``executeCommand Check returns 2 when daemon startup fails`` () =
    // A daemon that never comes up means the check could not run at all — un-completable, so
    // exit 2, never exit 1 (which reads as "failures found").
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
    // This test assembly's own dll, as an existing .dll path on disk.
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

// --- AUTOMATION-147: the daemon self-heal, end to end through executeCommand ---
//
// The simulated daemon behaves like the real one in the two ways that matter: it RECORDS ITS
// BINARY IDENTITY when launched, and every answer it gives is tagged with the generation
// that produced it. That tag is what lets these tests assert not just "a restart happened"
// but "the reply came from the NEW daemon".

[<NoComparison; NoEquality>]
type private FakeDaemon =
    {
        mutable Running: bool
        mutable Generation: int
        mutable Shutdowns: int
        mutable Launches: int
        /// The generation that served each GetDiagnostics call, in order.
        Served: ResizeArray<int>
    }

/// A real throwing frame whose compiled declaring type supplies the same marker the
/// production classifier sees in StreamJsonRpc. NoInlining + NoOptimization and the
/// catch/rethrow body keep this frame observable instead of tail-call-eliding it.
module private HeaderDelimitedMessageHandler =
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining
                                                 ||| System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)>]
    let ReadCoreAsync () : unit =
        try
            raise (OverflowException("Arithmetic operation resulted in an overflow."))
        with :? OverflowException ->
            reraise ()

let private raiseFrameReaderOverflow () =
    let captured =
        try
            HeaderDelimitedMessageHandler.ReadCoreAsync()
            failwith "unreachable"
        with ex ->
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex)

    captured.Throw()

/// An IpcOps backed by `FakeDaemon`, rooted at `repoRoot`. Launching records THIS process's
/// identity into `.fshw/daemon.identity`, exactly as the real daemon's `start` path does, so
/// the CLI's next handshake sees a current daemon and reuses it.
let private fakeDaemonIpc (repoRoot: string) (d: FakeDaemon) : IpcOps =
    { fakeIpc () with
        IsRunning = fun _ -> d.Running
        Shutdown =
            fun _ ->
                async {
                    d.Shutdowns <- d.Shutdowns + 1
                    d.Running <- false
                    return "shutting down"
                }
        LaunchDaemon =
            fun _ _ _ ->
                d.Launches <- d.Launches + 1
                d.Generation <- d.Generation + 1
                d.Running <- true
                FsHotWatch.DaemonIdentity.recordCurrent repoRoot
        GetStatus = fun _ -> async { return completedStatusJson }
        GetDiagnostics =
            fun _ _ ->
                async {
                    d.Served.Add d.Generation
                    return """{"count": 0, "files": {}, "unchecked": 0}"""
                } }

/// A daemon already running from generation 1. Its identity is whatever the caller staged
/// beforehand — that is the variable each test below sets.
let private runningDaemon () =
    { Running = true
      Generation = 1
      Shutdowns = 0
      Launches = 0
      Served = ResizeArray() }

/// Stage `.fshw/` with a config hash matching `tmpDir`, so the ONLY thing under test is the
/// identity handshake and never the config-drift restart.
let private stageStateDir (tmpDir: string) =
    Directory.CreateDirectory(Path.Combine(tmpDir, ".fshw")) |> ignore

    File.WriteAllText(Path.Combine(tmpDir, ".fshw", "config.hash"), computeConfigHashWith defaultFileOps tmpDir)

[<Fact(Timeout = 15000)>]
let ``check against a daemon with NO recorded identity replaces it and runs on the NEW daemon`` () =
    // The running daemon is an OLD build that never wrote an identity. The new CLI must
    // detect that UNILATERALLY — the old daemon reports nothing and is asked for nothing —
    // then stop it, start a fresh one, and answer from THAT.
    withTempDir "cli-identity-notrecorded" (fun tmpDir ->
        stageStateDir tmpDir
        // No daemon.identity file at all — an old build's footprint.
        test <@ not (File.Exists(FsHotWatch.DaemonIdentity.identityFilePath tmpDir)) @>

        let d = runningDaemon ()
        let ipc = fakeDaemonIpc tmpDir d

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
        test <@ d.Shutdowns = 1 @>
        test <@ d.Launches = 1 @>
        // The new daemon recorded THIS binary's identity, so the next command reuses it...
        test <@ FsHotWatch.DaemonIdentity.verdictFor tmpDir = FsHotWatch.DaemonIdentity.IdentityVerdict.Match @>
        // ...and every answer the check consumed came from generation 2 (the NEW code),
        // never from the stale generation 1.
        test <@ d.Served.Count > 0 @>
        test <@ d.Served |> Seq.forall (fun g -> g = 2) @>)

[<Fact(Timeout = 15000)>]
let ``check against a daemon built from a DIFFERENT binary replaces it and runs on the NEW daemon`` () =
    // Same-version, different-content is the AUTOMATION-123 repack: the version label
    // matches and the daemon is still the wrong code, so only the content hash catches it.
    withTempDir "cli-identity-different" (fun tmpDir ->
        stageStateDir tmpDir

        let current = FsHotWatch.DaemonIdentity.currentIdentity ()

        // A daemon whose recorded version is identical but whose content differs.
        File.WriteAllText(
            FsHotWatch.DaemonIdentity.identityFilePath tmpDir,
            FsHotWatch.DaemonIdentity.BinaryIdentity.render
                { current with
                    ContentHash = "0000000000000000" }
        )

        let d = runningDaemon ()
        let ipc = fakeDaemonIpc tmpDir d

        let stderr, result =
            captureStderr (fun () ->
                executeCommand
                    (fun _ -> Unchecked.defaultof<_>)
                    ipc
                    tmpDir
                    "fshw-test-pipe"
                    (Check [])
                    defaultGlobalOptions
                    fakeConfig
                    30.0)

        test <@ result = 0 @>
        test <@ d.Shutdowns = 1 && d.Launches = 1 @>
        test <@ d.Served |> Seq.forall (fun g -> g = 2) @>
        // It SAYS what it found — no silent swap.
        test <@ stderr.Contains "different fshw binary" @>)

[<Fact(Timeout = 15000)>]
let ``check against a HEALTHY daemon never restarts it — the warm cache survives`` () =
    // The guard against over-correction: discarding a warm FCS cache costs a ~30s cold
    // rebuild, so a matching identity plus matching config must always REUSE.
    withTempDir "cli-identity-healthy" (fun tmpDir ->
        stageStateDir tmpDir
        FsHotWatch.DaemonIdentity.recordCurrent tmpDir

        let d = runningDaemon ()
        let ipc = fakeDaemonIpc tmpDir d

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
        test <@ d.Shutdowns = 0 @>
        test <@ d.Launches = 0 @>
        // Served by the ORIGINAL warm daemon.
        test <@ d.Served |> Seq.forall (fun g -> g = 1) @>)

[<Fact(Timeout = 15000)>]
let ``status names a stale-binary daemon instead of presenting its output as current`` () =
    // `status` is a read-only observer and does not restart — a fresh daemon would have
    // nothing to report — so it must SAY that what it shows came from a different build.
    withTempDir "cli-identity-status" (fun tmpDir ->
        stageStateDir tmpDir
        let d = runningDaemon ()
        let ipc = fakeDaemonIpc tmpDir d

        let stderr, _ =
            captureStderr (fun () ->
                executeCommand
                    (fun _ -> Unchecked.defaultof<_>)
                    ipc
                    tmpDir
                    "pipe"
                    (Status None)
                    defaultGlobalOptions
                    fakeConfig
                    30.0)

        test <@ stderr.Contains "no recorded binary identity" @>
        test <@ d.Shutdowns = 0 && d.Launches = 0 @>)

[<Fact(Timeout = 15000)>]
let ``a corrupted IPC reply restarts the daemon and retries the command automatically`` () =
    // Corrupt frame-length arithmetic surfaces as OverflowException, and used to hand the
    // operator a `fshw stop` + `fshw start` ritual. The tool performs it now.
    withTempDir "cli-heal-corrupt" (fun tmpDir ->
        stageStateDir tmpDir
        FsHotWatch.DaemonIdentity.recordCurrent tmpDir

        let d = runningDaemon ()
        let mutable calls = 0

        let ipc =
            { fakeDaemonIpc tmpDir d with
                GetDiagnostics =
                    fun _ _ ->
                        async {
                            calls <- calls + 1

                            if calls = 1 then
                                // A garbage Content-Length header can overflow StreamJsonRpc's
                                // framing arithmetic before it can accept the reply.
                                raiseFrameReaderOverflow ()

                            d.Served.Add d.Generation
                            return """{"count": 0, "files": {}, "unchecked": 0}"""
                        } }

        let stderr, result =
            captureStderr (fun () ->
                executeCommand
                    (fun _ -> Unchecked.defaultof<_>)
                    ipc
                    tmpDir
                    "pipe"
                    (Status None)
                    defaultGlobalOptions
                    fakeConfig
                    30.0)

        // The command SUCCEEDED — no ritual, no exit-1 handed to the human.
        test <@ result = 0 @>
        test <@ d.Shutdowns >= 1 && d.Launches = 1 @>
        // The retry was served by the fresh daemon.
        test <@ d.Served |> Seq.forall (fun g -> g = 2) @>
        test <@ stderr.Contains "corrupted" @>
        test <@ stderr.Contains "restarting the daemon and retrying" @>)

[<Fact(Timeout = 15000)>]
let ``a client OOM names the client and leaves the workspace daemon owned and reusable`` () =
    // A failure allocating in this CLI says nothing about the daemon's health. Replacing
    // the daemon would turn a workspace-owned warm process into needless churn; abandoning
    // it would leave an orphan. The next command must reuse that same generation.
    withTempDir "cli-client-oom-ownership" (fun tmpDir ->
        stageStateDir tmpDir
        FsHotWatch.DaemonIdentity.recordCurrent tmpDir

        let d = runningDaemon ()
        let mutable calls = 0

        let ipc =
            { fakeDaemonIpc tmpDir d with
                GetDiagnostics =
                    fun _ _ ->
                        async {
                            calls <- calls + 1

                            if calls = 1 then
                                raise (OutOfMemoryException("client heap exhausted"))

                            d.Served.Add d.Generation
                            return """{"count": 0, "files": {}, "unchecked": 0}"""
                        } }

        let stderr, failedResult =
            captureStderr (fun () ->
                executeCommand
                    (fun _ -> Unchecked.defaultof<_>)
                    ipc
                    tmpDir
                    "pipe"
                    (Status None)
                    defaultGlobalOptions
                    fakeConfig
                    30.0)

        test <@ failedResult = 1 @>
        test <@ stderr.Contains "fshw CLI ran out of memory" @>
        test <@ not (stderr.Contains "Could not connect to daemon") @>
        test <@ d.Running && d.Generation = 1 @>
        test <@ d.Shutdowns = 0 && d.Launches = 0 @>

        let nextResult =
            executeCommand
                (fun _ -> Unchecked.defaultof<_>)
                ipc
                tmpDir
                "pipe"
                (Status None)
                defaultGlobalOptions
                fakeConfig
                30.0

        test <@ nextResult = 0 @>
        test <@ d.Served |> Seq.toList = [ 1 ] @>)

[<Fact(Timeout = 15000)>]
let ``a stale daemon-pid file is cleaned up on the next command`` () =
    withTempDir "cli-stale-pid" (fun tmpDir ->
        stageStateDir tmpDir
        FsHotWatch.DaemonIdentity.recordCurrent tmpDir

        let pidPath = Path.Combine(tmpDir, ".fshw", "daemon.pid")
        // A pid that cannot name a live process — the leftover of a crash or a kill -9.
        File.WriteAllText(pidPath, "2000000000")

        let d = runningDaemon ()

        executeCommand
            (fun _ -> Unchecked.defaultof<_>)
            (fakeDaemonIpc tmpDir d)
            tmpDir
            "pipe"
            (Status None)
            defaultGlobalOptions
            fakeConfig
            30.0
        |> ignore

        test <@ not (File.Exists pidPath) @>)

[<Fact(Timeout = 15000)>]
let ``a LIVE daemon-pid file is never eaten by the hygiene pass`` () =
    // Unknowns lean ALIVE: deleting a live daemon's pidfile strands the process beyond the
    // reach of `fshw stop` — the same mess, recreated by an over-eager cleaner.
    withTempDir "cli-live-pid" (fun tmpDir ->
        stageStateDir tmpDir
        let pidPath = Path.Combine(tmpDir, ".fshw", "daemon.pid")
        File.WriteAllText(pidPath, string (System.Diagnostics.Process.GetCurrentProcess().Id))

        test <@ not (cleanStalePidfileWith defaultFileOps tmpDir) @>
        test <@ File.Exists pidPath @>)

[<Fact(Timeout = 15000)>]
let ``an unparseable daemon-pid file is left alone rather than assumed dead`` () =
    withTempDir "cli-garbage-pid" (fun tmpDir ->
        stageStateDir tmpDir
        let pidPath = Path.Combine(tmpDir, ".fshw", "daemon.pid")
        File.WriteAllText(pidPath, "not-a-number")

        test <@ not (cleanStalePidfileWith defaultFileOps tmpDir) @>
        test <@ File.Exists pidPath @>)

[<Fact(Timeout = 15000)>]
let ``the next command reports that the daemon restarted ITSELF over a wedge`` () =
    // The daemon has no terminal to print to, so the breadcrumb is how the report of what it
    // did survives the restart — printed once, then consumed.
    withTempDir "cli-wedge-breadcrumb" (fun tmpDir ->
        stageStateDir tmpDir
        FsHotWatch.DaemonIdentity.recordCurrent tmpDir

        FsHotWatch.PluginWedge.writeBreadcrumb
            tmpDir
            "daemon was wedged on 'analyzers' (WEDGED: started 11:38:39, no completion in 65m 0s) — restarted it"

        let d = runningDaemon ()

        let stderr, _ =
            captureStderr (fun () ->
                executeCommand
                    (fun _ -> Unchecked.defaultof<_>)
                    (fakeDaemonIpc tmpDir d)
                    tmpDir
                    "pipe"
                    (Status None)
                    defaultGlobalOptions
                    fakeConfig
                    30.0)

        test <@ stderr.Contains "daemon was wedged on 'analyzers'" @>
        test <@ stderr.Contains "restarted it" @>
        test <@ stderr.Contains "⚠" @>
        // Consumed: it reports once, it does not nag.
        test <@ not (File.Exists(FsHotWatch.PluginWedge.breadcrumbPath tmpDir)) @>)

[<Fact(Timeout = 15000)>]
let ``runIpcWithSelfHeal retries ONLY proven corrupted frames, once`` () =
    // Every other fault goes straight through: self-healing on, say, a timeout would restart
    // healthy daemons and torch their warm caches.
    let mutable restarts = 0
    let mutable lastFailure: exn option = None

    let stampedStack =
        try
            raiseFrameReaderOverflow ()
            "unreachable"
        with ex ->
            ex.StackTrace

    test <@ stampedStack.Contains("HeaderDelimitedMessageHandler", StringComparison.Ordinal) @>

    let heal (throwCount: int) (raiseFault: unit -> unit) =
        restarts <- 0
        lastFailure <- None
        let mutable calls = 0

        let action () =
            calls <- calls + 1

            if calls <= throwCount then
                raiseFault ()

            0

        let result =
            runIpcWithSelfHeal
                (fun () ->
                    restarts <- restarts + 1
                    true)
                (fun ex ->
                    lastFailure <- Some ex
                    99)
                action

        result, calls

    // Frame-reader overflow is the framing arithmetic failure observed in production:
    // its stack is evidence, so heal once.
    let healedOnce = heal 1 raiseFrameReaderOverflow

    match lastFailure with
    | Some ex -> failwithf "frame-reader fault was not healed; stack:\n%s" ex.StackTrace
    | None -> ()

    test <@ healedOnce = (0, 2) @>
    test <@ restarts = 1 @>

    // Corrupted pipe that RECURS: retried exactly once, then reported honestly.
    let mutable recurringFault = 0

    let raiseRecurringFault () =
        recurringFault <- recurringFault + 1

        if recurringFault = 1 then
            raiseFrameReaderOverflow ()
        else
            raise (OverflowException("corruption recurred"))

    test <@ heal 2 raiseRecurringFault = (99, 2) @>
    test <@ restarts = 1 @>

    // Overflow outside the frame reader carries no daemon-corruption evidence.
    test <@ heal 1 (fun () -> raise (OverflowException("client arithmetic"))) = (99, 1) @>
    test <@ restarts = 0 @>

    // A bare OOM has no frame-reader evidence: it is the client's own allocation
    // failure, so restarting the healthy daemon would only destroy its warm cache.
    test <@ heal 1 (fun () -> raise (OutOfMemoryException("client heap exhausted"))) = (99, 1) @>
    test <@ restarts = 0 @>

    // Other faults also go straight to the failure path.
    test <@ heal 1 (fun () -> raise (TimeoutException("busy"))) = (99, 1) @>
    test <@ restarts = 0 @>

let private remoteIpcFault (typeName: string) (message: string) (stackTrace: string) : exn =
    let data =
        StreamJsonRpc.Protocol.CommonErrorData(TypeName = typeName, Message = message, StackTrace = stackTrace)

    StreamJsonRpc.RemoteInvocationException(message, 0, null, data) :> exn

let private remoteIpcFaultWithData (data: StreamJsonRpc.Protocol.CommonErrorData) : exn =
    StreamJsonRpc.RemoteInvocationException(data.Message, 0, null, data) :> exn

let private corruptedRemoteIpcFault () =
    remoteIpcFault
        "System.OutOfMemoryException"
        "Insufficient memory to continue the execution of the program."
        "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()"

[<Fact(Timeout = 15000)>]
let ``runIpcWithSelfHeal restarts once and retries a corrupted remote fault`` () =
    let mutable actionCalls = 0
    let mutable restartCalls = 0
    let mutable failureCalls = 0

    let result =
        runIpcWithSelfHeal
            (fun () ->
                restartCalls <- restartCalls + 1
                true)
            (fun _ ->
                failureCalls <- failureCalls + 1
                99)
            (fun () ->
                actionCalls <- actionCalls + 1

                if actionCalls = 1 then
                    raise (AggregateException(corruptedRemoteIpcFault ()))

                42)

    test <@ result = 42 @>
    test <@ actionCalls = 2 @>
    test <@ restartCalls = 1 @>
    test <@ failureCalls = 0 @>

[<Fact(Timeout = 15000)>]
let ``runIpcWithSelfHeal recognizes a corrupted remote fault inside AggregateException data`` () =
    let inner =
        StreamJsonRpc.Protocol.CommonErrorData(
            TypeName = "System.OutOfMemoryException",
            Message = "Insufficient memory to continue the execution of the program.",
            StackTrace = "at StreamJsonRpc.HeaderDelimitedMessageHandler.ReadCoreAsync()"
        )

    let outer =
        StreamJsonRpc.Protocol.CommonErrorData(
            TypeName = "System.AggregateException",
            Message = "One or more errors occurred.",
            StackTrace = "at System.Threading.Tasks.Task.Wait()",
            Inner = inner
        )

    let mutable actionCalls = 0
    let mutable restartCalls = 0

    let result =
        runIpcWithSelfHeal
            (fun () ->
                restartCalls <- restartCalls + 1
                true)
            (fun _ -> 99)
            (fun () ->
                actionCalls <- actionCalls + 1

                if actionCalls = 1 then
                    raise (remoteIpcFaultWithData outer)

                42)

    test <@ result = 42 @>
    test <@ actionCalls = 2 @>
    test <@ restartCalls = 1 @>

[<Fact(Timeout = 15000)>]
let ``runIpcWithSelfHeal reports the retry fault without restarting twice`` () =
    let firstFault = corruptedRemoteIpcFault ()
    let retryFault = corruptedRemoteIpcFault ()
    let mutable actionCalls = 0
    let mutable restartCalls = 0
    let mutable failures: exn list = []

    let result =
        runIpcWithSelfHeal
            (fun () ->
                restartCalls <- restartCalls + 1
                true)
            (fun ex ->
                failures <- ex :: failures
                99)
            (fun () ->
                actionCalls <- actionCalls + 1
                raise (if actionCalls = 1 then firstFault else retryFault))

    test <@ result = 99 @>
    test <@ actionCalls = 2 @>
    test <@ restartCalls = 1 @>
    test <@ failures.Length = 1 @>
    test <@ obj.ReferenceEquals(failures.Head, retryFault) @>

[<Fact(Timeout = 15000)>]
let ``runIpcWithSelfHeal does not restart for an unrelated remote fault`` () =
    let fault =
        remoteIpcFault "System.InvalidOperationException" "plugin failed" "at Plugin.Run()"

    let mutable actionCalls = 0
    let mutable restartCalls = 0
    let mutable failures: exn list = []

    let result =
        runIpcWithSelfHeal
            (fun () ->
                restartCalls <- restartCalls + 1
                true)
            (fun ex ->
                failures <- ex :: failures
                99)
            (fun () ->
                actionCalls <- actionCalls + 1
                raise fault)

    test <@ result = 99 @>
    test <@ actionCalls = 1 @>
    test <@ restartCalls = 0 @>
    test <@ failures.Length = 1 @>
    test <@ obj.ReferenceEquals(failures.Head, fault) @>

[<Fact(Timeout = 15000)>]
let ``runIpcWithSelfHeal reports the original fault when restart fails`` () =
    let fault = corruptedRemoteIpcFault ()
    let mutable actionCalls = 0
    let mutable restartCalls = 0
    let mutable failures: exn list = []

    let result =
        runIpcWithSelfHeal
            (fun () ->
                restartCalls <- restartCalls + 1
                false)
            (fun ex ->
                failures <- ex :: failures
                99)
            (fun () ->
                actionCalls <- actionCalls + 1
                raise fault)

    test <@ result = 99 @>
    test <@ actionCalls = 1 @>
    test <@ restartCalls = 1 @>
    test <@ failures.Length = 1 @>
    test <@ obj.ReferenceEquals(failures.Head, fault) @>

[<Fact(Timeout = 15000)>]
let ``runIpcWithSelfHeal does not restart for a remote timeout`` () =
    let fault = remoteIpcFault "System.TimeoutException" "daemon busy" "at Plugin.Run()"
    let mutable restartCalls = 0
    let mutable failedWith: exn option = None

    let result =
        runIpcWithSelfHeal
            (fun () ->
                restartCalls <- restartCalls + 1
                true)
            (fun ex ->
                failedWith <- Some ex
                99)
            (fun () -> raise fault)

    test <@ result = 99 @>
    test <@ restartCalls = 0 @>
    test <@ failedWith |> Option.exists (fun ex -> obj.ReferenceEquals(ex, fault)) @>

// ---------------------------------------------------------------------------
// `confirm`'s daemon commands (AUTOMATION-129)
//
// `RunCommand` dispatches on the COMMAND name, and `confirm` used to call it with the PLUGIN
// name — `RunCommand "test-prune" "test-scope"` — so the host found no command called
// `test-prune` and returned the unknown-command sentinel. `parseTestScope` then correctly and
// SILENTLY read that as `ScopeUnknown`, which `confirm` correctly and SILENTLY treats as "not
// full-suite", so `fshw confirm` had NO PATH TO A GREEN on any repo: exit 3 even right after
// the whole suite had run unfiltered. It failed in the safe direction, which is why nothing
// caught it.
//
// These pin the WIRE NAMES, which is the thing that was broken.
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

    // The command name — not the plugin name — travels in the command slot...
    test <@ seen |> List.map fst = [ "test-scope" ] @>
    // ...and the daemon's answer is actually READ, rather than collapsing to ScopeUnknown
    // because the call never reached a handler.
    test <@ run.Scope = IpcParsing.FullSuite 6 @>

[<Fact(Timeout = 15000)>]
let ``an unknown-command reply is ScopeUnknown — `confirm` never goes green on a scope it did not establish`` () =
    let ipc =
        { fakeIpc () with
            RunCommand = fun _ name _ -> async { return FsHotWatch.Ipc.unknownCommandReply name } }

    test <@ (readTestRun ipc "pipe").Scope = IpcParsing.ScopeUnknown @>

[<Fact(Timeout = 15000)>]
let ``requestFullSuiteScope sends set-scope with a PARSEABLE {"scope":"full"} payload`` () =
    // Doubly broken before: the command name was wrong AND the args were `set-scope
    // {"scope":"full"}`, which is not JSON — so even correctly routed, the handler's
    // `JsonDocument.Parse` would have thrown and defaulted to IMPACT, handing `confirm` a
    // filtered run in answer to its request for a full suite.
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

/// Run `f`, capturing BOTH streams, and return (output, result).
///
/// Both, deliberately: `UI.fail` writes the verdict line to stderr while `UI.info`
/// writes the evidence lines to stdout, so a stderr-only capture reads a refusal that
/// named nothing — which is exactly the bug being pinned here.
let private captureBothStreams (f: unit -> 'a) : string * 'a =
    let originalOut = Console.Out
    let originalErr = Console.Error
    use sw = new StringWriter()
    Console.SetOut(sw)
    Console.SetError(sw)

    try
        let result = f ()
        sw.Flush()
        sw.ToString(), result
    finally
        Console.SetOut(originalOut)
        Console.SetError(originalErr)

// --- AUTOMATION-227/272: a refusal must name WHAT it searched for and WHERE ---
//
// These live here rather than in IpcOutputTests because they capture `Console.Error`,
// a process-global, and this module is already in the serialized log-global collection.
//
// The acceptance clause both tickets were failed on: "exits non-zero with a message
// naming the filter and the projects it searched". What shipped named a project COUNT
// and pointed at `fshw status test-prune` — so the reader was left to distinguish a
// typo, a renamed class, and a filter aimed at a project that does not contain it, which
// is the three-way ambiguity that produced a wrong conclusion in the first place.

[<Fact(Timeout = 15000)>]
let ``a zero-match refusal names the filter and every project it searched`` () =
    let json =
        """{"elapsed":"0.1s","coverage":"all-zero-match","noTestsMatched":true,"filter":"--filter-class *JudgeIntegrationTests","projects":[{"project":"Intelligence.Analyzers.Tests","status":"no-tests-matched","output":""},{"project":"Intelligence.Tests.Unit","status":"no-tests-matched","output":""}]}"""

    let stderr, exitCode =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ exitCode = 3 @>
    test <@ stderr.Contains("--filter-class *JudgeIntegrationTests") @>
    test <@ stderr.Contains("Intelligence.Analyzers.Tests") @>
    test <@ stderr.Contains("Intelligence.Tests.Unit") @>

[<Fact(Timeout = 15000)>]
let ``a refusal from a daemon that reports no filter says so, rather than claiming none was set`` () =
    // "(none)" would be a LIE about an older daemon: an absent field means this daemon
    // predates it, which is a different fact from "the run was unfiltered", and a refusal
    // that guesses between them is how the next wrong conclusion gets drawn.
    let json =
        """{"elapsed":"0.1s","coverage":"all-zero-match","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let stderr, exitCode =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ exitCode = 3 @>
    test <@ stderr.Contains("not reported") @>
    test <@ stderr.Contains("P") @>

[<Fact(Timeout = 15000)>]
let ``a searched-project list is CAPPED, and says how many it left out`` () =
    // A truncated list that does not say it was truncated reads as the whole selection.
    let projects =
        [ 1..14 ]
        |> List.map (fun i -> $"""{{"project":"P%d{i}","status":"no-tests-matched","output":""}}""")
        |> String.concat ","

    let json =
        $"""{{"elapsed":"0.1s","coverage":"all-zero-match","noTestsMatched":true,"filter":"--filter-class *Nope","projects":[%s{projects}]}}"""

    let stderr, exitCode =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ exitCode = 3 @>
    test <@ stderr.Contains("P1 (no-tests-matched)") @>
    test <@ stderr.Contains("+4 more") @>

[<Fact(Timeout = 15000)>]
let ``a mixed no-op refusal names each project and the status that made it a no-op`` () =
    // The diagnosis IS the per-project statuses here, so they are printed rather than
    // pointed at: "every project failed to execute a test" is not actionable without
    // knowing which project deferred and which matched nothing.
    let json =
        """{"elapsed":"0.2s","coverage":"nothing-executed","projects":[{"project":"A","status":"no-tests-matched","output":""},{"project":"B","status":"deferred","output":"apphost not produced"}]}"""

    let stderr, exitCode =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ exitCode = 3 @>
    test <@ stderr.Contains("A (no-tests-matched)") @>
    test <@ stderr.Contains("B (deferred)") @>

[<Fact(Timeout = 15000)>]
let ``a passing run does NOT print the search evidence`` () =
    // POSITIVE CONTROL for the four above: without it, a change that printed the filter
    // and project list unconditionally would satisfy every one of them while making the
    // ordinary green noisier.
    let json =
        """{"elapsed":"1.0s","coverage":"ran-partial","filter":"--filter-class *Real","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    let stderr, exitCode =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ exitCode = 0 @>
    test <@ not (stderr.Contains("Searched:")) @>
    test <@ not (stderr.Contains("Filter:")) @>

// --- AUTOMATION-272 criterion 3: the CLI states per-project test counts ---
//
// "The missing summary line is the tell that separates a real pass from a vacuous one,
// and it should not require reading the log to notice." Until now total/succeeded/failed
// lived only in `daemon.log`, so noticing required going to look.

[<Fact(Timeout = 15000)>]
let ``a passing test-rerun prints total, succeeded and failed for every project it ran`` () =
    let json =
        """{"elapsed":"1.0s","coverage":"ran-partial","projects":[{"project":"Unit","status":"passed","output":"","counts":{"total":12,"succeeded":12,"failed":0,"skipped":0,"other":0}},{"project":"Integration","status":"passed","output":"","counts":{"total":6,"succeeded":5,"failed":0,"skipped":1,"other":0}}]}"""

    let stderr, exitCode =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ exitCode = 0 @>
    test <@ stderr.Contains("Unit [passed] — total 12, 12 succeeded, 0 failed") @>
    test <@ stderr.Contains("Integration [passed] — total 6, 5 succeeded, 0 failed, 1 skipped") @>

[<Fact(Timeout = 15000)>]
let ``a project with NO test report says so, rather than printing zeros`` () =
    // `total: 0, failed: 0` reads as "this suite ran cleanly" — the same vacuous green one
    // level down. An unknown runner we never asked a report from, or a host that aborted
    // before flushing one, must not be rendered as a clean empty suite.
    let json =
        """{"elapsed":"1.0s","coverage":"ran-partial","projects":[{"project":"Custom","status":"passed","output":"","counts":null}]}"""

    let stderr, _ =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ stderr.Contains("no test report — counts unknown (not zero)") @>
    test <@ not (stderr.Contains("total 0")) @>

[<Fact(Timeout = 15000)>]
let ``a zero-match project reports its real total of zero, beside the refusal`` () =
    // The whole ticket in one line of output: the run said "matched nothing", and the
    // counts agree rather than being absent. A reader does not have to trust the status
    // word — the number is there.
    let json =
        """{"elapsed":"0.1s","coverage":"all-zero-match","noTestsMatched":true,"filter":"--filter-class *Nope","projects":[{"project":"P","status":"no-tests-matched","output":"","counts":{"total":0,"succeeded":0,"failed":0,"skipped":0,"other":0}}]}"""

    let stderr, exitCode =
        captureBothStreams (fun () ->
            FsHotWatch.Cli.IpcOutput.renderIpcResult FsHotWatch.Cli.ProgressRenderer.Verbose (fun _ -> []) false json)

    test <@ exitCode = 3 @>
    test <@ stderr.Contains("P [no-tests-matched] — total 0, 0 succeeded, 0 failed") @>

// ---------------------------------------------------------------------------
// AUTOMATION-320 — a failing beforeRun step must SAY WHAT FAILED
// ---------------------------------------------------------------------------
//
// The defect: `beforeRun failed:` — colon, then nothing. No step, no exit code,
// no output. It happened on a healthy box, four `confirm` runs in a row, and it
// aborts the merge verb, so the work simply could not be landed. Finding the
// culprit meant running all nine chained commands by hand, and the message very
// nearly misattributed the blame to the change under test.

module ``beforeRun failure reporting`` =

    let private tmpRepo () =
        let suffix = Guid.NewGuid().ToString("N")
        let dir = Path.Combine(Path.GetTempPath(), $"fshw-hook-%s{suffix}")
        Directory.CreateDirectory dir |> ignore
        dir

    let private cleanup dir =
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

    /// AUTOMATION-555. Hook timings are filed under the run they belong to, so a later
    /// verdict cannot pick up an earlier run's steps; a file that cannot be parsed is
    /// no evidence, never a crash.
    [<Fact>]
    let ``hook evidence is keyed by test run id and malformed evidence fails closed`` () =
        let repo = tmpRepo ()

        try
            let first = Guid.NewGuid()
            let second = Guid.NewGuid()

            let timing command index =
                { HookStepTiming.Label = "beforeRun"
                  StepIndex = index
                  StepCount = 2
                  Command = command
                  StartedAtUtc = DateTime.UtcNow
                  ElapsedMs = int64 index
                  Outcome = "ok" }

            HookTimings.record repo first [ timing "first-a" 1; timing "first-b" 2 ]
            HookTimings.record repo second [ timing "second-a" 1 ]

            test <@ HookTimings.read repo (Some first) |> List.map _.Command = [ "first-a"; "first-b" ] @>
            test <@ HookTimings.read repo (Some second) |> List.map _.Command = [ "second-a" ] @>
            test <@ HookTimings.read repo (Some(Guid.NewGuid())) |> List.isEmpty @>
            test <@ HookTimings.read repo None |> List.isEmpty @>

            let malformed = Path.Combine(FsHotWatch.Ctrf.runDir repo first, "hook-timings.json")
            File.WriteAllText(malformed, "{not json")
            test <@ HookTimings.read repo (Some first) |> List.isEmpty @>

            // Every other way the file can be wrong is equally "no evidence": not an
            // array, a step missing a field, a step whose start time does not parse.
            for wrong in
                [ "{}"
                  """[{"label":"beforeRun","stepIndex":1}]"""
                  """[{"label":"beforeRun","stepIndex":1,"stepCount":1,"command":"true","startedAtUtc":"yesterday","elapsedMs":1,"outcome":"ok"}]""" ] do
                File.WriteAllText(malformed, wrong)
                test <@ HookTimings.read repo (Some first) |> List.isEmpty @>

            // A run directory that cannot be created (its path is a FILE) makes
            // `record` warn and carry on: attribution never fails the test run.
            let blocked = Guid.NewGuid()

            Directory.CreateDirectory(Path.GetDirectoryName(FsHotWatch.Ctrf.runDir repo blocked))
            |> ignore

            File.WriteAllText(FsHotWatch.Ctrf.runDir repo blocked, "not a directory")
            HookTimings.record repo blocked [ timing "blocked" 1 ]
            test <@ HookTimings.read repo (Some blocked) |> List.isEmpty @>
        finally
            cleanup repo

    [<Fact>]
    let ``a failing step is named, with its exit code and its output`` () =
        let repo = tmpRepo ()

        try
            let steps =
                [ "echo first-ok"; "echo the-failing-output && exit 3"; "echo never-reached" ]

            match runShellSteps "beforeRun" (Some 60) repo steps with
            | HookOk _ -> failwith "expected the chain to fail"
            | HookFailed(ran, failure) ->
                // AUTOMATION-555. The steps that RAN are timed — the passing one and the
                // failing one — and the never-reached step is absent, not zero.
                test <@ ran |> List.map _.Command = [ "echo first-ok"; "echo the-failing-output && exit 3" ] @>
                test <@ ran |> List.map _.Outcome = [ "ok"; "fail" ] @>

                let message = HookFailure.describe failure

                // WHICH step — the whole point. Position and the command itself.
                test <@ failure.StepIndex = 2 @>
                test <@ failure.StepCount = 3 @>
                test <@ message.Contains "step 2/3" @>
                test <@ message.Contains "exit 3" @>

                // ITS output, not the chain's.
                test <@ message.Contains "the-failing-output" @>

                // And it STOPPED: running on would waste time and could bury the
                // first failure behind a later one.
                test <@ not (message.Contains "never-reached") @>
        finally
            cleanup repo

    [<Fact>]
    let ``a step that fails SILENTLY still produces a non-empty reason`` () =
        // THE REGRESSION. This is the exact shape that produced `beforeRun failed:`
        // and nothing else: a process that writes to neither stream. The old code
        // interpolated an empty string after the colon; the reason is a record now,
        // so a message that says nothing cannot be rendered.
        let repo = tmpRepo ()

        try
            match runShellSteps "beforeRun" (Some 60) repo [ "exit 7" ] with
            | HookOk _ -> failwith "expected the chain to fail"
            | HookFailed(_, failure) ->
                let message = HookFailure.describe failure

                test <@ message.Contains "exit 7" @>
                test <@ message.Contains "(no output" @>
                // Never again: a message that trails off after the colon.
                test <@ not (message.TrimEnd().EndsWith("failed:", StringComparison.Ordinal)) @>
        finally
            cleanup repo

    [<Fact>]
    let ``a green chain reports success and runs every step`` () =
        // The control. Without it, a runner that always returned HookFailed would
        // satisfy both tests above.
        let repo = tmpRepo ()

        try
            let marker = Path.Combine(repo, "ran.txt")

            let steps = [ "echo one > ran.txt"; "echo two >> ran.txt"; "echo three >> ran.txt" ]

            match runShellSteps "beforeRun" (Some 60) repo steps with
            | HookFailed(_, f) -> failwith $"expected success, got: %s{HookFailure.describe f}"
            | HookOk timings ->
                // AUTOMATION-555. Every step is timed, in chain order, as itself.
                test <@ timings |> List.map _.StepIndex = [ 1; 2; 3 ] @>
                test <@ timings |> List.forall (fun t -> t.StepCount = 3) @>
                test <@ timings |> List.map _.Command = steps @>
                test <@ timings |> List.forall (fun t -> t.ElapsedMs >= 0L && t.Outcome = "ok") @>

                let contents = File.ReadAllText marker
                test <@ contents.Contains "one" @>
                test <@ contents.Contains "two" @>
                test <@ contents.Contains "three" @>
        finally
            cleanup repo
