/// The CLI's side of the repository host: who opts in, how commands parse, how a
/// worktree attaches — and that a host killed outright leaves every worktree
/// recoverable by the next command.
module FsHotWatch.Tests.RepositoryHostModeTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open CommandTree
open FsHotWatch.Cli
open FsHotWatch.Cli.Program
open FsHotWatch.RepositoryIdentity
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Opting in
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``a worktree opts in through its config, and the environment overrides it either way`` () =
    let on = "{ \"repositoryHost\": true }"
    let off = "{ \"repositoryHost\": false }"

    let env (value: string) =
        fun (name: string) -> if name = RepositoryHostMode.EnvVar then value else null

    test <@ RepositoryHostMode.configured on @>
    test <@ not (RepositoryHostMode.configured off) @>
    test <@ not (RepositoryHostMode.configured "") @>
    test <@ not (RepositoryHostMode.configured "not json") @>
    test <@ not (RepositoryHostMode.configured "{ \"repositoryHost\": \"yes\" }") @>

    test <@ RepositoryHostMode.enabled on (env null) @>
    test <@ RepositoryHostMode.enabled on (env "") @>
    test <@ not (RepositoryHostMode.enabled off (env null)) @>
    test <@ RepositoryHostMode.enabled off (env "1") @>
    test <@ RepositoryHostMode.enabled off (env " true ") @>
    test <@ not (RepositoryHostMode.enabled on (env "0")) @>

[<Fact(Timeout = 5000)>]
let ``the host is launched with every path quoted for the shell`` () =
    let line =
        RepositoryHostMode.hostShellCommand "/bin/fs hw" "tool run " "/r/it's" "/s/host.log"

    test <@ line = "nohup '/bin/fs hw' tool run host '/r/it'\\''s' >> '/s/host.log' 2>&1 < /dev/null &" @>

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``status, stop and host parse, with --repository where it applies`` () =
    let tree = FsHotWatch.Cli.Program.commandTree
    test <@ CommandTree.parse tree [| "status"; "--repository" |] = Ok(Status(None, [ Repository ])) @>
    test <@ CommandTree.parse tree [| "status"; "build"; "--repository" |] = Ok(Status(Some "build", [ Repository ])) @>
    test <@ CommandTree.parse tree [| "stop"; "--repository" |] = Ok(Stop [ Repository ]) @>
    test <@ CommandTree.parse tree [| "host"; "/some/root" |] = Ok(Host "/some/root") @>

[<Fact(Timeout = 5000)>]
let ``only commands that bring this worktree's daemon up attach; status and stop only observe`` () =
    for command in [ Start; Check []; Confirm []; Format []; Scan; Invalidate; Rerun "build" ] do
        test <@ needsDaemon command @>

    for command in
        [ Check [ RunOnce ]
          Confirm [ RunOnce ]
          Format [ RunOnce ]
          Status(None, [])
          Status(None, [ Repository ])
          Stop []
          Stop [ Repository ]
          Verdict
          Init
          Completions
          DeadCode []
          Host "/r" ] do
        test <@ not (needsDaemon command) @>

[<Fact(Timeout = 5000)>]
let ``the repository status names the host, each session and the shared watcher`` () =
    let json =
        """{"watch":{"nativeStreams":1},"hostPid":7,"endpoint":"fshw-repo-x","sessions":[{"root":"/r","serving":true,"scanGeneration":3},{"root":"/r/.workspaces/b","serving":false,"scanGeneration":0}]}"""

    test
        <@
            renderRepositoryStatus json = [ "Repository host pid 7 on fshw-repo-x: 2 session(s)"
                                            "  /r  serving, scan generation 3"
                                            "  /r/.workspaces/b  starting, scan generation 0"
                                            "  watch: {\"nativeStreams\":1}" ]
        @>

    test
        <@
            renderRepositoryStatus """{"hostPid":7,"endpoint":"e","sessions":[]}""" = [ "Repository host pid 7 on e: 0 session(s)" ]
        @>

// ---------------------------------------------------------------------------
// Attaching
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a directory that cannot be resolved is refused before anything is launched`` () =
    let launched = ref false

    match
        RepositoryHostMode.attach
            "/unused"
            (fun _ -> launched.Value <- true)
            (TimeSpan.FromSeconds 1.0)
            "/definitely/not/here"
            ""
    with
    | RepositoryHostMode.Attach.Refused message -> test <@ message.Contains "no repository identity" @>
    | other -> failwith $"%A{other}"

    test <@ not launched.Value @>

[<Fact(Timeout = 30000)>]
let ``a host that never comes up is refused within the bound, naming its log`` () =
    withTempDir "mode-nohost" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".jj", "repo")) |> ignore

        match RepositoryHostMode.attach (Path.Combine(dir, "state")) ignore (TimeSpan.FromSeconds 2.0) dir "" with
        | RepositoryHostMode.Attach.Refused message ->
            test <@ message.Contains "did not start" && message.Contains "host.log" @>
        | other -> failwith $"%A{other}")

// ---------------------------------------------------------------------------
// Real host processes
// ---------------------------------------------------------------------------

/// A jj primary with one F# project, configured for the smallest possible session.
let private withRepository (body: string -> string -> unit) =
    withTempDir "mode-host" (fun dir ->
        let root =
            match canonicalize dir with
            | Ok c -> Path.Combine(c.Value, "r")
            | Error e -> failwith (IdentityError.describe e)

        Directory.CreateDirectory(Path.Combine(root, ".jj", "repo")) |> ignore
        let lib = Path.Combine(root, "src", "Lib")
        Directory.CreateDirectory lib |> ignore

        File.WriteAllText(
            Path.Combine(lib, "Lib.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""
        )

        File.WriteAllText(Path.Combine(lib, "Lib.fs"), "module Lib\nlet answer = 42\n")

        let config =
            """{ "build": false, "format": false, "lint": false, "repositoryHost": true }"""

        File.WriteAllText(Path.Combine(root, ".fshw.json"), config)
        body root (Path.Combine(Path.GetDirectoryName root, "state")))

/// The built CLI, run in `root` as an opted-in shell would run it. Its exit code.
let private runCli (stateHome: string) (root: string) (args: string list) : int =
    let cli = typeof<HostLink>.Assembly.Location
    let apphost = Path.Combine(Path.GetDirectoryName cli, "FsHotWatch.Cli")
    let psi = ProcessStartInfo(apphost)

    for a in args do
        psi.ArgumentList.Add a

    psi.WorkingDirectory <- root
    psi.Environment["FSHW_STATE_HOME"] <- stateHome
    psi.Environment[RepositoryHostMode.EnvVar] <- "1"
    psi.UseShellExecute <- false
    psi.RedirectStandardError <- true
    psi.RedirectStandardOutput <- true
    use p = Process.Start psi
    p.BeginErrorReadLine()
    p.BeginOutputReadLine()

    if not (p.WaitForExit 180000) then
        p.Kill true
        failwith $"fshw %A{args} did not finish"

    p.ExitCode

[<Fact(Timeout = 600000)>]
let ``a host killed outright leaves the worktree to the next command, which starts a new host`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root stateHome ->
            let worktree =
                match resolveWorktree root with
                | Ok w -> w
                | Error e -> failwith (IdentityError.describe e)

            let control = repositoryControlPaths stateHome worktree.Repository

            let hostPid () =
                int (File.ReadAllText(control.PidFile).Trim())

            let seen = Collections.Generic.HashSet<int>()

            try
                test <@ runCli stateHome root [ "scan" ] = 0 @>
                let first = hostPid ()
                seen.Add first |> ignore

                let firstSession =
                    (RepositoryHost.HostSessionRecord.tryReadLive RepositoryHost.processAlive root).Value

                test <@ firstSession.HostPid = first @>

                // SIGKILL: no shutdown path runs, nothing is cleaned up.
                use killed = Process.GetProcessById first
                killed.Kill()
                test <@ killed.WaitForExit 30000 @>

                // What the dead host left behind says so.
                test
                    <@
                        RepositoryHost.HostSessionRecord.tryReadLive RepositoryHost.processAlive root
                        |> Option.isNone
                    @>

                test <@ runCli stateHome root [ "scan" ] = 0 @>
                let second = hostPid ()
                seen.Add second |> ignore
                test <@ second <> first @>

                let secondSession =
                    RepositoryHost.HostSessionRecord.tryReadLive RepositoryHost.processAlive root

                test
                    <@
                        secondSession
                        |> Option.exists (fun r -> r.HostPid = second && r.Session <> firstSession.Session)
                    @>

                // A clean stop hands the worktree back.
                test <@ runCli stateHome root [ "stop"; "--repository" ] = 0 @>
                test <@ waitUntilTrue (fun () -> not (RepositoryHost.processAlive second)) 30000 @>
                use relock = (RepositoryHost.tryLockWorktree root).Value
                ()
            finally
                for pid in seen do
                    if RepositoryHost.processAlive pid then
                        use p = Process.GetProcessById pid
                        p.Kill true)

// ---------------------------------------------------------------------------
// The host verb, in process
// ---------------------------------------------------------------------------

/// `fshw host` run in this process (it pins the process working directory and reads
/// the state home from the environment, so the test restores both).
[<Collection(LogGlobalCollectionName)>]
type HostVerbInProcess() =
    [<Fact(Timeout = 300000)>]
    member _.``the host verb serves a session end to end, and stops on request``() =
        withRepository (fun root stateHome ->
            let cwd = Directory.GetCurrentDirectory()

            withEnv "FSHW_STATE_HOME" (Some stateHome) (fun () ->
                try
                    let run = Task.Run(fun () -> runHostVerb defaultGlobalOptions root)

                    let config = File.ReadAllText(Path.Combine(root, ".fshw.json"))

                    let endpoint, session =
                        match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 60.0) root config with
                        | RepositoryHostMode.Attach.Serving(endpoint, session) -> endpoint, session
                        | other -> failwith $"%A{other}"

                    let link: HostLink =
                        { Endpoint = endpoint
                          Session = fun () -> session
                          Reattach = fun () -> true }

                    let ipc = sessionIpcOps link
                    test <@ ipc.IsRunning "ignored" @>
                    test <@ not (String.IsNullOrWhiteSpace(ipc.GetStatus "ignored" |> Async.RunSynchronously)) @>
                    test <@ not (String.IsNullOrWhiteSpace(ipc.ScanStatus "ignored" |> Async.RunSynchronously)) @>
                    test <@ repositoryStatus false root = 0 @>
                    test <@ repositoryStatus true root = 0 @>

                    // The session's own log names the host behind it.
                    let log = File.ReadAllText(Path.Combine(root, "logs", "daemon.log"))
                    test <@ log.Contains $"session=%s{SessionId.render session}" @>

                    test <@ stopRepositoryHost root = 0 @>
                    test <@ run.Wait(TimeSpan.FromSeconds 60.0) && run.Result = 0 @>
                    test <@ repositoryStatus false root = 0 @>
                    test <@ stopRepositoryHost root = 0 @>
                finally
                    Directory.SetCurrentDirectory cwd))

    [<Fact(Timeout = 60000)>]
    member _.``the host verb refuses a root it cannot resolve``() =
        test <@ runHostVerb defaultGlobalOptions "/definitely/not/here" = 2 @>

    [<Fact(Timeout = 60000)>]
    member _.``the SDK probe answers with a version, or says why it could not``() =
        withRepository (fun root _ ->
            let here = SessionScope.SessionEnvironment.ofProcess root
            let version = RepositoryHostMode.sdkVersion root here
            test <@ version.Length > 0 && Char.IsDigit version[0] @>

            // With no `dotnet` on the session's PATH, the probe says it could not run.
            let nowhere =
                SessionScope.SessionEnvironment.create root (Map.ofList [ "PATH", "/nonexistent" ])

            test <@ (RepositoryHostMode.sdkVersion root nowhere).StartsWith "unresolved" @>)


// ---------------------------------------------------------------------------
// What an attach comes to, against a host in this process
// ---------------------------------------------------------------------------

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private inertWatcher: FsHotWatch.Daemon.Daemon.WatcherFactory =
    fun _ _ _ _ _ ->
        { Mode = Watcher.WatcherMode.NativeEvents
          Disposables = [] }

let private inProcessHost (root: string) (stateHome: string) (factory: SessionRegistry.SessionFactory) =
    let worktree =
        match resolveWorktree root with
        | Ok w -> w
        | Error e -> failwith (IdentityError.describe e)

    let settings: RepositoryHost.HostSettings =
        { Identity =
            { Protocol = AttachHandshake.ProtocolIdentity.current ()
              Repository = worktree.Repository }
          Control = repositoryControlPaths stateHome worktree.Repository
          // Whatever the attaching side sends: this very process's environment.
          Environment = SessionScope.SessionEnvironment.ofProcess root
          Toolchain = "sdk"
          ToolchainOf = fun _ _ -> "sdk"
          SinkFor =
            fun _ ->
                { Write = ignore
                  Level = Logging.LogLevel.Info }
          WatchConfig =
            fun _ _ ->
                { new IDisposable with
                    member _.Dispose() = () }
          SessionResources = fun _ -> []
          Describe = fun () -> Text.Json.Nodes.JsonObject() }

    let cts = new CancellationTokenSource()

    let run =
        Task.Run(fun () -> RepositoryHost.run settings factory (TimeSpan.FromMinutes 5.0) cts)

    test <@ waitUntilTrue (fun () -> RepositoryIpc.isRunning settings.Control.Endpoint) 20000 @>
    cts, run

let private daemonFactory: SessionRegistry.SessionFactory =
    fun spec ->
        FsHotWatch.Daemon.Daemon.createWithWatcherFactory
            nullChecker
            spec.Worktree.Root.Value
            { FsHotWatch.Daemon.Daemon.DaemonOptions.defaults with
                Hosting = FsHotWatch.DaemonHosting.hostedBy inertWatcher (fun _ -> nullChecker) }
            inertWatcher

[<Fact(Timeout = 120000)>]
let ``a worktree its own daemon holds stays with that daemon`` () =
    withRepository (fun root stateHome ->
        use legacy = (RepositoryHost.tryLockWorktree root).Value
        let cts, run = inProcessHost root stateHome daemonFactory

        try
            match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 10.0) root "" with
            | RepositoryHostMode.Attach.OwnDaemon reason -> test <@ reason.Contains "fshw stop" @>
            | other -> failwith $"%A{other}"
        finally
            cts.Cancel()
            run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)

[<Fact(Timeout = 120000)>]
let ``a session the host cannot build is a stop, not a fallback`` () =
    withRepository (fun root stateHome ->
        let cts, run =
            inProcessHost root stateHome (fun _ -> invalidOp "no projects discovered")

        try
            match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 10.0) root "" with
            | RepositoryHostMode.Attach.Refused reason -> test <@ reason.Contains "no projects discovered" @>
            | other -> failwith $"%A{other}"
        finally
            cts.Cancel()
            run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)

[<Fact(Timeout = 60000)>]
let ``an SDK probe whose dotnet fails says how`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root _ ->
            let bin = Path.Combine(root, "fake-bin")
            Directory.CreateDirectory bin |> ignore
            let fake = Path.Combine(bin, "dotnet")
            File.WriteAllText(fake, "#!/bin/sh\nexit 3\n")
            File.SetUnixFileMode(fake, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

            let env =
                SessionScope.SessionEnvironment.create root (Map.ofList [ "PATH", $"%s{bin}:/usr/bin:/bin" ])

            test <@ RepositoryHostMode.sdkVersion root env = "unresolved (`dotnet --version` exited 3)" @>)

[<Fact(Timeout = 60000)>]
let ``an SDK probe that does not finish within its bound says so`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun root _ ->
            let bin = Path.Combine(root, "slow-bin")
            Directory.CreateDirectory bin |> ignore
            let fake = Path.Combine(bin, "dotnet")
            File.WriteAllText(fake, "#!/bin/sh\nsleep 30\n")
            File.SetUnixFileMode(fake, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

            let env =
                SessionScope.SessionEnvironment.create root (Map.ofList [ "PATH", $"%s{bin}:/usr/bin:/bin" ])

            test
                <@
                    RepositoryHostMode.sdkVersionWithin (TimeSpan.FromMilliseconds 500.0) root env = "unresolved (`dotnet --version` did not complete)"
                @>)

[<Fact(Timeout = 60000)>]
let ``an attach reply the host cannot have written is a stop, naming why`` () =
    withRepository (fun root stateHome ->
        let worktree =
            match resolveWorktree root with
            | Ok w -> w
            | Error e -> failwith (IdentityError.describe e)

        let control = repositoryControlPaths stateHome worktree.Repository

        // Something on the repository's endpoint that answers every request with bytes
        // no host writes.
        let opener: Ipc.IpcServer.ConnectionOpener =
            fun pipe _ ->
                async {
                    match! RepositoryIpc.readFrame pipe CancellationToken.None |> Async.AwaitTask with
                    | Ok _ ->
                        do!
                            RepositoryIpc.writeFrame pipe "not json" CancellationToken.None
                            |> Async.AwaitTask
                    | Error _ -> ()

                    return None
                }

        use cts = new CancellationTokenSource()

        let server =
            Async.StartAsTask(Ipc.IpcServer.serveConnections (TimeSpan.FromSeconds 5.0) control.Endpoint opener cts)

        try
            test <@ waitUntilTrue (fun () -> RepositoryIpc.isRunning control.Endpoint) 10000 @>

            match RepositoryHostMode.attach stateHome ignore (TimeSpan.FromSeconds 10.0) root "" with
            | RepositoryHostMode.Attach.Refused reason -> test <@ reason.Contains "could not be read" @>
            | other -> failwith $"%A{other}"
        finally
            cts.Cancel()
            server.Wait(TimeSpan.FromSeconds 10.0) |> ignore)

// ---------------------------------------------------------------------------
// Plugin passthrough
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``a plugin command in host mode is refused, naming the gap, never sent to a per-worktree pipe`` () =
    let noEnv (_: string) : string = null
    let on = "{ \"repositoryHost\": true }"

    match passthroughRefusal on noEnv false "coverage-report" with
    | Some message ->
        test <@ message.Contains "coverage-report" @>
        test <@ message.Contains "repository host" && message.Contains "FSHW_REPOSITORY_HOST=0" @>
    | None -> failwith "an opted-in worktree must refuse plugin passthrough"

    // A worktree the host serves refuses too, even from a shell that has not opted in.
    test <@ (passthroughRefusal "" noEnv true "coverage-report").IsSome @>
    // Its own daemon still takes plugin commands.
    test <@ (passthroughRefusal "" noEnv false "coverage-report").IsNone @>

    let off (name: string) =
        if name = RepositoryHostMode.EnvVar then "0" else null

    test <@ (passthroughRefusal on off false "coverage-report").IsNone @>
