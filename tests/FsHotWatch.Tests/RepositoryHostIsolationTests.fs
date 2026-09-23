/// Two real sessions — real compiler, real MSBuild evaluation — in one repository host.
/// Whatever happens to session A (an edit, a configuration reload, a cancelled or
/// abandoned request, a crash), session B's status, diagnostics, scan generation, and
/// everything it keeps under its `.fshw`, are exactly what they were.
module FsHotWatch.Tests.RepositoryHostIsolationTests

open System
open System.IO
open System.IO.Pipes
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open StreamJsonRpc
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.RepositoryHost
open FsHotWatch.RepositoryIdentity
open FsHotWatch.RepositoryIpc
open FsHotWatch.SessionRegistry
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

let private inertWatcher: Daemon.Daemon.WatcherFactory =
    fun _ _ _ _ _ ->
        { Mode = Watcher.WatcherMode.NativeEvents
          Disposables = [] }

let private resolved (root: string) =
    match resolveWorktree root with
    | Ok r -> r
    | Error e -> failwith (IdentityError.describe e)

let private project =
    """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="Lib.fs" /></ItemGroup></Project>"""

let private writeWorktree (root: string) =
    let lib = Path.Combine(root, "src", "Lib")
    Directory.CreateDirectory lib |> ignore
    File.WriteAllText(Path.Combine(lib, "Lib.fsproj"), project)
    File.WriteAllText(Path.Combine(lib, "Lib.fs"), "module Lib\nlet answer = 42\n")

    let fsproj = Path.Combine(lib, "Lib.fsproj")

    match
        ProcessHelper.runProcess
            "dotnet"
            $"restore \"%s{fsproj}\""
            root
            []
            (ProcessHelper.ProcessBounds.silent (TimeSpan.FromMinutes 3.0))
    with
    | ProcessHelper.Succeeded _ -> ()
    | other -> failwith $"restore failed: %A{other}"

/// Two sessions, A (the primary) and B (a nested secondary), attached to a host that
/// serves them on a real endpoint.
[<NoComparison; NoEquality>]
type private World =
    { Host: RepositoryHost
      Endpoint: string
      A: ResolvedWorktree
      B: ResolvedWorktree
      SessionA: SessionId
      SessionB: SessionId }

let private call (endpoint: string) (session: SessionId) (methodName: string) (args: obj array) =
    invoke endpoint (Preamble.Session(session, InvocationId.mint ())) methodName args
    |> Async.RunSynchronously

let private attachWith (world: World) (worktree: ResolvedWorktree) (config: string) =
    let request =
        { requestFor (ProtocolIdentity.current ()) worktree IncarnationExpectation.Fresh (ConfigDigest.ofText config) with
            Environment = SessionEnvironment.variables (SessionEnvironment.ofProcess worktree.Root.Value) }

    match decodeResponse (world.Host.Handlers.Attach(encodeRequest request)) with
    | Ok(AttachedReply(id, _, _)) -> id
    | other -> failwith $"%A{other}"

/// Scan once and wait for that scan to publish.
let private scanAndWait (world: World) (session: SessionId) =
    let live = (world.Host.Registry.TryGet session).Value
    let before = live.Daemon.GetScanGeneration()
    call world.Endpoint session "Scan" [||] |> ignore
    call world.Endpoint session "WaitForScan" [| box before |] |> ignore

let private withWorld (run: SessionRun) (body: World -> unit) =
    withTempDir "isolation" (fun dir ->
        let canonical =
            match canonicalize dir with
            | Ok c -> c.Value
            | Error e -> failwith (IdentityError.describe e)

        let rootA = Path.Combine(canonical, "r")
        Directory.CreateDirectory(Path.Combine(rootA, ".jj", "repo")) |> ignore
        let rootB = Path.Combine(rootA, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(rootB, ".jj")) |> ignore
        File.WriteAllText(Path.Combine(rootB, ".jj", "repo"), "../../../.jj/repo")
        writeWorktree rootA
        writeWorktree rootB
        let a = resolved rootA
        let b = resolved rootB

        let settings: HostSettings =
            { Identity =
                { Protocol = ProtocolIdentity.current ()
                  Repository = a.Repository }
              Control = repositoryControlPaths (Path.Combine(canonical, "state")) a.Repository
              Environment = SessionEnvironment.ofProcess rootA
              Toolchain = "sdk"
              ToolchainOf = fun _ _ -> "sdk"
              SinkFor =
                fun w -> Logging.fileSink (Path.Combine(w.Root.Value, "logs", "daemon.log")) Logging.LogLevel.Info
              WatchConfig =
                fun _ _ ->
                    { new IDisposable with
                        member _.Dispose() = () }
              SessionResources = fun _ -> []
              Describe = fun () -> JsonObject() }

        let factory (spec: SessionSpec) =
            Daemon.Daemon.create
                spec.Worktree.Root.Value
                { Daemon.Daemon.DaemonOptions.defaults with
                    Hosting = FsHotWatch.DaemonHosting.hostedBy inertWatcher }

        use registry = new SessionRegistry(factory, run)
        let host = RepositoryHost(settings, registry, ignore, TimeSpan.FromSeconds 60.0)
        use cts = new CancellationTokenSource()
        let serving = Async.StartAsTask(serve settings.Control.Endpoint host.Handlers cts)

        try
            test <@ waitUntilTrue (fun () -> isRunning settings.Control.Endpoint) 20000 @>

            let world =
                { Host = host
                  Endpoint = settings.Control.Endpoint
                  A = a
                  B = b
                  SessionA = Unchecked.defaultof<SessionId>
                  SessionB = Unchecked.defaultof<SessionId> }

            let sessionA = attachWith world a "a"
            let sessionB = attachWith world b "b"

            let world =
                { world with
                    SessionA = sessionA
                    SessionB = sessionB }

            // Both sessions have finished their first scan before anything happens.
            call world.Endpoint sessionA "WaitForScan" [| box 0L |] |> ignore
            call world.Endpoint sessionB "WaitForScan" [| box 0L |] |> ignore
            body world
        finally
            cts.Cancel()
            serving.Wait(TimeSpan.FromSeconds 30.0) |> ignore)

/// What an observer of B can see: its diagnostics (without their clocks), its scan
/// generation, and the bytes of everything under its `.fshw`.
let private observeB (world: World) =
    let diagnostics =
        JsonNode.Parse(call world.Endpoint world.SessionB "GetDiagnostics" [| box "" |])

    let projection =
        [ "count"; "files"; "unchecked"; "projectModel" ]
        |> List.map (fun key ->
            key,
            (match diagnostics[key] with
             | null -> "null"
             | node -> node.ToJsonString()))

    let session = (world.Host.Registry.TryGet world.SessionB).Value
    let state = FsHwPaths.root world.B.Root.Value

    let files =
        Directory.GetFiles(state, "*", SearchOption.AllDirectories)
        |> Array.filter (fun f -> not (f.EndsWith "daemon.lock"))
        |> Array.sort
        |> Array.map (fun f ->
            let hash =
                try
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes f))
                with :? IOException ->
                    "unreadable"

            Path.GetRelativePath(state, f), hash)
        |> List.ofArray

    projection, session.Daemon.GetScanGeneration(), files, obj.ReferenceEquals(session.Daemon, session.Daemon)

let private assertBUntouched (world: World) before =
    let after = observeB world
    test <@ after = before @>
    test <@ (world.Host.Registry.TryGet world.SessionB).IsSome @>

/// A verdict B's CLI wrote earlier: nothing A does may rewrite it.
let private plantVerdict (world: World) =
    File.WriteAllText(Path.Combine(FsHwPaths.root world.B.Root.Value, "verdict.json"), "{\"sentinel\":true}")

// ---------------------------------------------------------------------------

[<Fact(Timeout = 300000)>]
let ``T1: an edit in A is checked in A, and B sees nothing of it`` () =
    withWorld defaultRun (fun world ->
        plantVerdict world
        let daemonB = (world.Host.Registry.TryGet world.SessionB).Value.Daemon
        let before = observeB world

        File.WriteAllText(
            Path.Combine(world.A.Root.Value, "src", "Lib", "Lib.fs"),
            "module Lib\nlet answer: int = \"no\"\n"
        )

        scanAndWait world world.SessionA

        let diagnosticsA =
            JsonNode.Parse(call world.Endpoint world.SessionA "GetDiagnostics" [| box "" |])

        test <@ diagnosticsA["count"].GetValue<int>() > 0 @>
        assertBUntouched world before
        test <@ obj.ReferenceEquals((world.Host.Registry.TryGet world.SessionB).Value.Daemon, daemonB) @>)

[<Fact(Timeout = 300000)>]
let ``T2: reloading A's configuration replaces A alone`` () =
    withWorld defaultRun (fun world ->
        plantVerdict world
        let daemonB = (world.Host.Registry.TryGet world.SessionB).Value.Daemon
        let before = observeB world

        let reloaded = attachWith world world.A "a, edited"
        test <@ reloaded <> world.SessionA && reloaded.Worktree = world.SessionA.Worktree @>

        let refused =
            Assert.ThrowsAny<exn>(fun () -> call world.Endpoint world.SessionA "GetStatus" [||] |> ignore)

        test
            <@
                match refused with
                | RepositoryRefusedException("stale-incarnation", _) -> true
                | _ -> false
            @>

        call world.Endpoint reloaded "WaitForScan" [| box 0L |] |> ignore
        assertBUntouched world before
        test <@ obj.ReferenceEquals((world.Host.Registry.TryGet world.SessionB).Value.Daemon, daemonB) @>)

/// Start `methodName` on `session` and abandon it: the client goes away mid-call.
let private abandon (world: World) (session: SessionId) (methodName: string) (args: obj array) =
    use pipe =
        new NamedPipeClientStream(".", world.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous)

    pipe.Connect 5000

    (writeFrame pipe (encodePreamble (Preamble.Session(session, InvocationId.mint ()))) CancellationToken.None).Wait()

    let reply =
        (readFrame pipe CancellationToken.None).Result
        |> Result.mapError string
        |> Result.bind decodeReply

    test <@ reply = Ok PreambleReply.Accepted @>
    let rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe :> Stream))
    rpc.StartListening()
    let pending = rpc.InvokeAsync<string>(methodName, args)
    Thread.Sleep 200
    rpc.Dispose()
    pending

[<Fact(Timeout = 300000)>]
let ``T3: requests abandoned and a stop in A leave B serving, mid-request included`` () =
    withWorld defaultRun (fun world ->
        plantVerdict world
        let before = observeB world

        // A client of A waits for a scan that will never come, then vanishes.
        let abandoned = abandon world world.SessionA "WaitForScan" [| box Int64.MaxValue |]
        test <@ Task.WaitAny([| abandoned :> Task |], 30000) = 0 @>

        // A request of B is in flight while A is stopped under it.
        let generationB =
            (world.Host.Registry.TryGet world.SessionB).Value.Daemon.GetScanGeneration()

        let inFlightB =
            Task.Run(fun () ->
                call world.Endpoint world.SessionB "Scan" [||] |> ignore
                call world.Endpoint world.SessionB "WaitForScan" [| box generationB |])

        call world.Endpoint world.SessionA "Scan" [||] |> ignore
        test <@ world.Host.Registry.Detach world.SessionA @>

        test <@ inFlightB.Wait(TimeSpan.FromSeconds 120.0) @>
        test <@ not (String.IsNullOrWhiteSpace inFlightB.Result) @>

        // B rescanned on its own request, so compare what a rescan of unchanged
        // files must leave alone: its diagnostics and its verdict.
        let projection, _, files, _ = observeB world
        let beforeProjection, _, beforeFiles, _ = before
        test <@ projection = beforeProjection @>

        let verdict (files: (string * string) list) =
            files |> List.filter (fun (f, _) -> f = "verdict.json")

        test <@ verdict files = verdict beforeFiles @>
        test <@ not (String.IsNullOrWhiteSpace(call world.Endpoint world.SessionB "GetStatus" [||])) @>)

[<Fact(Timeout = 300000)>]
let ``T4: A crashing ends A alone; B is untouched and A can attach afresh`` () =
    // Carries the root of the session to crash. A session started after the crash runs
    // in the ordinary way.
    let crash = TaskCompletionSource<string>()

    let crashingA: SessionRun =
        fun daemon serve startedAt cts ->
            async {
                if crash.Task.IsCompleted then
                    return! defaultRun daemon serve startedAt cts
                else
                    let running = Async.StartAsTask(defaultRun daemon serve startedAt cts)
                    let! winner = Task.WhenAny(running, crash.Task) |> Async.AwaitTask

                    if obj.ReferenceEquals(winner, crash.Task) && crash.Task.Result = daemon.RepoRoot then
                        cts.Cancel()
                        do! running |> Async.AwaitTask
                        failwith "session A crashed"
                    else
                        do! running |> Async.AwaitTask
            }

    withWorld crashingA (fun world ->
        plantVerdict world
        let before = observeB world
        let sessionA = (world.Host.Registry.TryGet world.SessionA).Value
        crash.SetResult world.A.Root.Value
        test <@ sessionA.Ended.Wait(TimeSpan.FromSeconds 120.0) @>
        test <@ sessionA.Ended.Result = SessionEnd.Faulted "session A crashed" @>
        assertBUntouched world before

        // What A held is released, and A attaches again as a new incarnation.
        use relock = (tryLockWorktree world.A.Root.Value).Value
        relock.Dispose()
        let again = attachWith world world.A "a"
        test <@ again <> world.SessionA @>
        call world.Endpoint again "WaitForScan" [| box 0L |] |> ignore
        assertBUntouched world before)
