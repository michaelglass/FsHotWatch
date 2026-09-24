/// A daemon's RPCs run in the daemon's context however it is served: on its own pipe,
/// or as a session of a repository host, whose endpoint the host serves. What a call
/// spawns is the daemon's to reap, and what it logs goes to the daemon's log, in both
/// modes alike.
module FsHotWatch.Tests.HostedRpcScopeTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.Daemon
open FsHotWatch.Ipc
open FsHotWatch.ProcessHelper
open FsHotWatch.RepositoryHost
open FsHotWatch.RepositoryIdentity
open FsHotWatch.RepositoryIpc
open FsHotWatch.SessionRegistry
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private inertWatcher: Daemon.WatcherFactory =
    fun _ _ _ _ _ ->
        { Mode = Watcher.WatcherMode.NativeEvents
          Disposables = [] }

[<Literal>]
let private CommandRan = "spawn command ran"

/// A plugin whose `spawn` command starts a long-lived child from the call that runs it,
/// recording the child's pid, and logs that it ran.
let private spawner (root: string) (pidFile: string) : PluginFramework.PluginHandler<unit, unit> =
    { Name = PluginFramework.PluginName.create "spawner"
      Init = ()
      Update = fun _ state _ -> async { return state }
      Commands =
        [ "spawn",
          PluginFramework.PluginCommand.Request(fun ctx _ ->
              async {
                  ctx.Log CommandRan

                  // Started from the call, so it runs in the call's context.
                  Task.Run(fun () ->
                      runProcess
                          "sh"
                          $"-c \"echo $$ > '%s{pidFile}'; exec sleep 30\""
                          root
                          []
                          (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))
                      |> ignore)
                  |> ignore

                  return "started"
              }) ]
      Subscriptions = Set.empty
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

let private alive (pid: int) =
    try
        use p = Process.GetProcessById pid
        not p.HasExited
    with _ ->
        false

let private readPid (path: string) =
    if File.Exists path then
        match Int32.TryParse((File.ReadAllText path).Trim()) with
        | true, pid -> Some pid
        | _ -> None
    else
        None

/// Call `spawn` through `call`, then `stop` the daemon: the child it started must be
/// reaped, and the command's log line must be in `lines`, the daemon's own log.
let private spawnThenStop
    (pidFile: string)
    (lines: ConcurrentQueue<string>)
    (call: unit -> string)
    (stop: unit -> unit)
    =
    let reply = call ()
    test <@ reply = "started" @>

    let pid =
        match waitUntilTrue (fun () -> (readPid pidFile).IsSome) 20000, readPid pidFile with
        | true, Some pid -> pid
        | _ -> failwith "the command never spawned"

    try
        test <@ alive pid @>
        test <@ lines |> Seq.exists (fun l -> l.Contains CommandRan) @>
        stop ()
        test <@ waitUntilTrue (fun () -> not (alive pid)) 10000 @>
    finally
        try
            (Process.GetProcessById pid).Kill(true)
        with _ ->
            ()

[<Fact(Timeout = 120000)>]
let ``a command called on a daemon's own pipe spawns and logs in the daemon's scope`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "rpc-scope-pipe" (fun dir ->
            Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
            let pidFile = Path.Combine(dir, "child.pid")
            let lines = ConcurrentQueue<string>()
            let pipeName = FsHotWatch.Cli.Program.computePipeName dir
            use cts = new CancellationTokenSource()

            let run =
                isolated (fun () ->
                    Logging.installSink
                        { Write = lines.Enqueue
                          Level = Logging.LogLevel.Info }
                    |> ignore

                    let daemon = Daemon.createWith nullChecker dir Daemon.DaemonOptions.defaults

                    daemon.RegisterHandler(spawner dir pidFile)
                    Async.StartImmediateAsTask(daemon.RunWithIpc(pipeName, cts)))

            try
                spawnThenStop
                    pidFile
                    lines
                    (fun () -> IpcClient.runCommand pipeName "spawn" "" |> Async.RunSynchronously)
                    (fun () ->
                        cts.Cancel()
                        run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)
            finally
                cts.Cancel()
                run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)

let private hostEnv = Map.ofList [ "PATH", "/usr/bin:/bin"; "DOTNET_ROOT", "/sdk" ]

[<Fact(Timeout = 120000)>]
let ``a command called through a repository host spawns and logs in its session's scope`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "rpc-scope-host" (fun dir ->
            let canonical =
                match canonicalize dir with
                | Ok c -> c.Value
                | Error e -> failwith (IdentityError.describe e)

            let root = Path.Combine(canonical, "r")
            Directory.CreateDirectory(Path.Combine(root, ".jj", "repo")) |> ignore
            Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore

            let worktree =
                match resolveWorktree root with
                | Ok w -> w
                | Error e -> failwith (IdentityError.describe e)

            let pidFile = Path.Combine(canonical, "child.pid")
            let lines = ConcurrentQueue<string>()

            let settings: HostSettings =
                { Identity =
                    { Protocol = ProtocolIdentity.current ()
                      Repository = worktree.Repository }
                  Control = repositoryControlPaths (Path.Combine(canonical, "state")) worktree.Repository
                  Environment = SessionEnvironment.create root hostEnv
                  Toolchain = "10.0.100"
                  ToolchainOf = fun _ _ -> "10.0.100"
                  // The session's own log.
                  SinkFor =
                    fun _ ->
                        { Write = lines.Enqueue
                          Level = Logging.LogLevel.Info }
                  WatchConfig =
                    fun _ _ ->
                        { new IDisposable with
                            member _.Dispose() = () }
                  SessionResources = fun _ -> []
                  Describe = fun () -> JsonObject() }

            let factory: SessionFactory =
                fun spec ->
                    let daemon =
                        Daemon.createWithWatcherFactory
                            nullChecker
                            spec.Worktree.Root.Value
                            { Daemon.DaemonOptions.defaults with
                                Hosting = DaemonHosting.hostedBy inertWatcher (fun _ -> nullChecker) }
                            inertWatcher

                    daemon.RegisterHandler(spawner root pidFile)
                    daemon

            use cts = new CancellationTokenSource()
            let endpoint = settings.Control.Endpoint

            let run =
                Task.Run(fun () -> RepositoryHost.run settings factory (TimeSpan.FromMinutes 5.0) cts)

            try
                test <@ waitUntilTrue (fun () -> RepositoryIpc.isRunning endpoint) 20000 @>

                let request =
                    { requestFor
                          (ProtocolIdentity.current ())
                          worktree
                          IncarnationExpectation.Fresh
                          (ConfigDigest.ofText "a") with
                        Environment = hostEnv }

                let session =
                    match
                        decodeResponse (
                            RepositoryIpc.attach endpoint (encodeRequest request) |> Async.RunSynchronously
                        )
                    with
                    | Ok(AttachedReply(id, _, _)) -> id
                    | other -> failwith $"%A{other}"

                spawnThenStop
                    pidFile
                    lines
                    (fun () ->
                        RepositoryIpc.invoke
                            endpoint
                            (Preamble.Session(session, InvocationId.mint ()))
                            "RunCommand"
                            [| box "spawn"; box "" |]
                        |> Async.RunSynchronously)
                    (fun () ->
                        RepositoryIpc.invoke endpoint (Preamble.Repository(InvocationId.mint ())) "StopHost" [||]
                        |> Async.RunSynchronously
                        |> ignore

                        run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)
            finally
                cts.Cancel()
                run.Wait(TimeSpan.FromSeconds 60.0) |> ignore)
