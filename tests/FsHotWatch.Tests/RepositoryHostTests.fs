/// The repository host: attaching is a transaction that refuses loudly and leaves
/// nothing behind, a refused client never disturbs the sessions already attached, and a
/// worktree is served by the host or by its own daemon — never both.
module FsHotWatch.Tests.RepositoryHostTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.Daemon
open FsHotWatch.DaemonIdentity
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

let private resolved (root: string) =
    match resolveWorktree root with
    | Ok r -> r
    | Error e -> failwith (IdentityError.describe e)

let private hostEnv = Map.ofList [ "PATH", "/usr/bin:/bin"; "DOTNET_ROOT", "/sdk" ]

let private noop =
    { new IDisposable with
        member _.Dispose() = () }

/// A repository — a jj primary `r` with a nested secondary `r/.workspaces/b` — and a
/// state home for its host.
[<NoComparison; NoEquality>]
type private Fixture =
    { Primary: ResolvedWorktree
      Secondary: ResolvedWorktree
      StateHome: string }

let private withRepository (body: Fixture -> unit) =
    withTempDir "host" (fun dir ->
        let canonical =
            match canonicalize dir with
            | Ok c -> c.Value
            | Error e -> failwith (IdentityError.describe e)

        let primary = Path.Combine(canonical, "r")
        Directory.CreateDirectory(Path.Combine(primary, ".jj", "repo")) |> ignore
        Directory.CreateDirectory(Path.Combine(primary, "src")) |> ignore
        let b = Path.Combine(primary, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(b, ".jj")) |> ignore
        Directory.CreateDirectory(Path.Combine(b, "src")) |> ignore
        File.WriteAllText(Path.Combine(b, ".jj", "repo"), "../../../.jj/repo")

        body
            { Primary = resolved primary
              Secondary = resolved b
              StateHome = Path.Combine(canonical, "state") })

let private settingsFor (fx: Fixture) : HostSettings =
    { Identity =
        { Protocol = ProtocolIdentity.current ()
          Repository = fx.Primary.Repository }
      Control = repositoryControlPaths fx.StateHome fx.Primary.Repository
      Environment = SessionEnvironment.create fx.Primary.Root.Value hostEnv
      Toolchain = "10.0.100"
      ToolchainOf = fun _ _ -> "10.0.100"
      SinkFor =
        fun _ ->
            { Write = ignore
              Level = Logging.LogLevel.Info }
      WatchConfig = fun _ _ -> noop
      Describe = fun () -> JsonObject() }

let private daemonFactory: SessionFactory =
    fun spec ->
        Daemon.createWithWatcherFactory
            nullChecker
            spec.Worktree.Root.Value
            { Daemon.DaemonOptions.defaults with
                Hosting = Daemon.Hosting.Hosted inertWatcher }
            inertWatcher

let private requestFrom (worktree: ResolvedWorktree) (config: string) =
    { requestFor (ProtocolIdentity.current ()) worktree IncarnationExpectation.Fresh (ConfigDigest.ofText config) with
        Environment = hostEnv }

/// A host driven through its handlers, with no pipe.
let private withHost
    (settings: Fixture -> HostSettings)
    (factory: SessionFactory)
    (body: Fixture -> RepositoryHost -> unit)
    =
    withRepository (fun fx ->
        use registry = new SessionRegistry(factory)
        let host = RepositoryHost(settings fx, registry, ignore, TimeSpan.FromSeconds 10.0)
        body fx host)

let private attachVia (host: RepositoryHost) (request: AttachRequest) =
    match decodeResponse (host.Handlers.Attach(encodeRequest request)) with
    | Ok reply -> reply
    | Error e -> failwith $"%A{e}"

let private attachedId reply =
    match reply with
    | AttachedReply(id, _, _) -> id
    | RefusedReply(kind, message, _) -> failwith $"refused %s{kind}: %s{message}"

let private refusalKind reply =
    match reply with
    | RefusedReply(kind, _, _) -> kind
    | AttachedReply _ -> "attached"

let private refusalMessage reply =
    match reply with
    | RefusedReply(_, message, _) -> message
    | AttachedReply _ -> ""

let private sessionCall (host: RepositoryHost) (id: SessionId) =
    host.Handlers.Session id (InvocationId.mint ()) |> fun t -> t.Result

// ---------------------------------------------------------------------------
// Attaching
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``two worktrees attach as two sessions, each routed to its own daemon`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let a = attachedId (attachVia host (requestFrom fx.Primary "a"))
        let b = attachedId (attachVia host (requestFrom fx.Secondary "b"))
        test <@ a <> b @>

        let hostOf id =
            match sessionCall host id with
            | Ok config -> config.Host
            | Error e -> failwith $"%A{e}"

        let sa = (host.Registry.TryGet a).Value
        let sb = (host.Registry.TryGet b).Value
        test <@ obj.ReferenceEquals(hostOf a, sa.Daemon.Host) @>
        test <@ obj.ReferenceEquals(hostOf b, sb.Daemon.Host) @>
        let listed = JsonNode.Parse(host.ListSessions())

        let roots =
            listed["sessions"].AsArray()
            |> Seq.map (fun s -> s["root"].GetValue<string>())
            |> List.ofSeq

        test <@ roots = List.sort [ fx.Primary.Root.Value; fx.Secondary.Root.Value ] @>
        test <@ listed["hostPid"].GetValue<int>() = Environment.ProcessId @>)

[<Fact(Timeout = 60000)>]
let ``attaching again with the same configuration rejoins the same session`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let first = attachedId (attachVia host (requestFrom fx.Primary "a"))

        match attachVia host (requestFrom fx.Primary "a") with
        | AttachedReply(again, AttachDisposition.Rejoined, _) -> test <@ again = first @>
        | other -> failwith $"%A{other}")

[<Fact(Timeout = 60000)>]
let ``a changed configuration reloads that session as a new incarnation, and the old one is void`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let oldA = attachedId (attachVia host (requestFrom fx.Primary "a"))
        let b = attachedId (attachVia host (requestFrom fx.Secondary "b"))
        let daemonB = (host.Registry.TryGet b).Value.Daemon

        match attachVia host (requestFrom fx.Primary "a-edited") with
        | AttachedReply(newA, AttachDisposition.RejoinedConfigChanged, _) ->
            test <@ newA <> oldA && newA.Worktree = oldA.Worktree @>

            test
                <@
                    match sessionCall host oldA with
                    | Error("stale-incarnation", _) -> true
                    | _ -> false
                @>

            test <@ sessionCall host newA |> Result.isOk @>
        | other -> failwith $"%A{other}"

        // The sibling is the same session and the same daemon.
        test <@ obj.ReferenceEquals((host.Registry.TryGet b).Value.Daemon, daemonB) @>)

// ---------------------------------------------------------------------------
// T5: a mismatched client is refused loudly and never disturbs a sibling
// ---------------------------------------------------------------------------

let private assertSiblingUntouched (host: RepositoryHost) (b: SessionId) (daemonB: Daemon) =
    test <@ (host.Registry.TryGet b).IsSome @>
    test <@ obj.ReferenceEquals((host.Registry.TryGet b).Value.Daemon, daemonB) @>
    test <@ not (host.Registry.TryGet b).Value.Ended.IsCompleted @>

[<Fact(Timeout = 60000)>]
let ``a client from a different binary is refused, and the attached session is untouched`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let b = attachedId (attachVia host (requestFrom fx.Secondary "b"))
        let daemonB = (host.Registry.TryGet b).Value.Daemon

        let foreign =
            { requestFrom fx.Primary "a" with
                Protocol =
                    { ProtocolIdentity.current () with
                        Binary =
                            { Version = "0.0.1"
                              ContentHash = "deadbeef" } } }

        let reply = attachVia host foreign
        test <@ refusalKind reply = "binary-mismatch" @>
        test <@ (refusalMessage reply).Contains "0.0.1" @>
        test <@ host.Registry.TryGetWorktree fx.Primary.Worktree |> Option.isNone @>
        assertSiblingUntouched host b daemonB)

[<Fact(Timeout = 60000)>]
let ``a client speaking another protocol version is refused before anything else is read`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let b = attachedId (attachVia host (requestFrom fx.Secondary "b"))
        let daemonB = (host.Registry.TryGet b).Value.Daemon
        let json = JsonNode.Parse(encodeRequest (requestFrom fx.Primary "a")).AsObject()
        json["protocol"] <- JsonValue.Create 1

        match decodeResponse (host.Handlers.Attach(json.ToJsonString())) with
        | Ok(RefusedReply("protocol-version-mismatch", _, _)) -> ()
        | other -> failwith $"%A{other}"

        assertSiblingUntouched host b daemonB)

[<Fact(Timeout = 60000)>]
let ``a worktree of another repository is refused`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        withTempDir "other-repo" (fun other ->
            Directory.CreateDirectory(Path.Combine(other, ".jj", "repo")) |> ignore
            let reply = attachVia host (requestFrom (resolved other) "x")
            test <@ refusalKind reply = "wrong-repository" @>))

[<Fact(Timeout = 60000)>]
let ``an unreadable attach request is answered with a refusal, not silence`` () =
    withHost settingsFor daemonFactory (fun _ host ->
        match decodeResponse (host.Handlers.Attach "{\"schema\":\"fshw.attach\",\"protocol\":2}") with
        | Ok(RefusedReply("malformed-request", _, _)) -> ()
        | other -> failwith $"%A{other}")

// ---------------------------------------------------------------------------
// Refusals that leave a worktree on its own daemon
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``a client whose MSBuild environment differs is refused, and nothing is taken`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let request =
            { requestFrom fx.Primary "a" with
                Environment = hostEnv.Add("Configuration", "Release") }

        let reply = attachVia host request
        test <@ refusalKind reply = "environment-mismatch" @>
        test <@ (refusalMessage reply).Contains "Configuration" @>
        test <@ AttachRefusal.fallsBackToOwnDaemon (refusalKind reply) @>
        use relock = (tryLockWorktree fx.Primary.Root.Value).Value
        test <@ not (File.Exists(HostSessionRecord.path fx.Primary.Root.Value)) @>)

[<Fact(Timeout = 60000)>]
let ``a worktree selecting another SDK is refused, and nothing is taken`` () =
    let settings fx =
        { settingsFor fx with
            ToolchainOf =
                fun w _ ->
                    if w.Root.Value = fx.Primary.Root.Value then
                        "9.0.300"
                    else
                        "10.0.100" }

    withHost settings daemonFactory (fun fx host ->
        let reply = attachVia host (requestFrom fx.Primary "a")
        test <@ refusalKind reply = "toolchain-mismatch" @>
        test <@ (refusalMessage reply).Contains "9.0.300" @>
        use relock = (tryLockWorktree fx.Primary.Root.Value).Value
        attachedId (attachVia host (requestFrom fx.Secondary "b")) |> ignore)

// ---------------------------------------------------------------------------
// T9: the host and a worktree's own daemon never serve one worktree at once
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``a worktree whose own daemon holds its lock is refused, naming that daemon`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        // What `fshw start` holds for a per-worktree daemon's lifetime.
        use legacyLock = (tryLockWorktree fx.Primary.Root.Value).Value
        File.WriteAllText(Path.Combine(FsHwPaths.root fx.Primary.Root.Value, "daemon.pid"), "4242")
        let reply = attachVia host (requestFrom fx.Primary "a")
        test <@ refusalKind reply = "worktree-owned-by-daemon" @>
        test <@ (refusalMessage reply).Contains "4242" @>
        test <@ AttachRefusal.fallsBackToOwnDaemon (refusalKind reply) @>
        test <@ host.Registry.TryGetWorktree fx.Primary.Worktree |> Option.isNone @>)

[<Fact(Timeout = 60000)>]
let ``a worktree the host serves cannot be locked by its own daemon, and says who serves it`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let a = attachedId (attachVia host (requestFrom fx.Primary "a"))
        test <@ tryLockWorktree fx.Primary.Root.Value |> Option.isNone @>

        test
            <@
                HostSessionRecord.tryReadLive processAlive fx.Primary.Root.Value = Some
                    { HostPid = Environment.ProcessId
                      Endpoint = (settingsFor fx).Control.Endpoint
                      Session = a }
            @>)

[<Fact(Timeout = 60000)>]
let ``a detached session hands its worktree back: lock released, record gone`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let a = attachedId (attachVia host (requestFrom fx.Primary "a"))
        test <@ host.Registry.Detach a @>
        use relock = (tryLockWorktree fx.Primary.Root.Value).Value
        test <@ not (File.Exists(HostSessionRecord.path fx.Primary.Root.Value)) @>)

[<Fact(Timeout = 60000)>]
let ``a record naming a dead host is stale`` () =
    withRepository (fun fx ->
        let dead =
            use p = Diagnostics.Process.Start("true")
            p.WaitForExit()
            p.Id

        Directory.CreateDirectory(FsHwPaths.root fx.Primary.Root.Value) |> ignore

        File.WriteAllText(
            HostSessionRecord.path fx.Primary.Root.Value,
            HostSessionRecord.render
                { HostPid = dead
                  Endpoint = "fshw-repo-x"
                  Session =
                    { Repository = fx.Primary.Repository
                      Worktree = fx.Primary.Worktree
                      Incarnation = SessionIncarnation.mint () } }
        )

        test
            <@
                HostSessionRecord.tryReadLive processAlive fx.Primary.Root.Value
                |> Option.isNone
            @>

        test
            <@
                HostSessionRecord.tryReadLive (fun _ -> true) fx.Primary.Root.Value
                |> Option.isSome
            @>)

[<Fact(Timeout = 15000)>]
let ``an unreadable or absent record reads as no record`` () =
    withRepository (fun fx ->
        test
            <@
                HostSessionRecord.tryReadLive (fun _ -> true) fx.Primary.Root.Value
                |> Option.isNone
            @>

        Directory.CreateDirectory(FsHwPaths.root fx.Primary.Root.Value) |> ignore

        for junk in
            [ "not json"
              "[]"
              "{\"hostPid\":1}"
              "{\"hostPid\":1,\"endpoint\":\"e\",\"session\":\"x\"}" ] do
            File.WriteAllText(HostSessionRecord.path fx.Primary.Root.Value, junk)

            test
                <@
                    HostSessionRecord.tryReadLive (fun _ -> true) fx.Primary.Root.Value
                    |> Option.isNone
                @>)

[<Fact(Timeout = 15000)>]
let ``processAlive tells a live pid from a gone one`` () =
    test <@ processAlive Environment.ProcessId @>
    test <@ not (processAlive -1) @>

// ---------------------------------------------------------------------------
// Sessions that cannot start, or have not yet begun serving
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``a session that cannot be built is refused, and its worktree is handed back`` () =
    withHost settingsFor (fun _ -> invalidOp "no projects discovered") (fun fx host ->
        let reply = attachVia host (requestFrom fx.Primary "a")
        test <@ refusalKind reply = "session-start-failed" @>
        test <@ (refusalMessage reply).Contains "no projects discovered" @>
        test <@ not (AttachRefusal.fallsBackToOwnDaemon (refusalKind reply)) @>
        use relock = (tryLockWorktree fx.Primary.Root.Value).Value
        test <@ not (File.Exists(HostSessionRecord.path fx.Primary.Root.Value)) @>)

[<Fact(Timeout = 60000)>]
let ``a call for a session the host never had is refused as unknown`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let never =
            { Repository = fx.Primary.Repository
              Worktree = fx.Primary.Worktree
              Incarnation = SessionIncarnation.mint () }

        test
            <@
                match sessionCall host never with
                | Error("unknown-session", _) -> true
                | _ -> false
            @>)

[<Fact(Timeout = 60000)>]
let ``a call for a session that has not begun serving is refused once the bound passes`` () =
    withRepository (fun fx ->
        let neverServes: SessionRun =
            fun _ _ _ cts -> async { cts.Token.WaitHandle.WaitOne() |> ignore }

        use registry = new SessionRegistry(daemonFactory, neverServes)

        let host =
            RepositoryHost(settingsFor fx, registry, ignore, TimeSpan.FromMilliseconds 100.0)

        let a = attachedId (attachVia host (requestFrom fx.Primary "a"))

        test
            <@
                match sessionCall host a with
                | Error("session-not-serving", _) -> true
                | _ -> false
            @>)

[<Fact(Timeout = 60000)>]
let ``a configuration change the host sees ends that session, and only that one`` () =
    let reload = Collections.Concurrent.ConcurrentDictionary<string, unit -> unit>()

    let settings fx =
        { settingsFor fx with
            WatchConfig =
                fun w onChange ->
                    reload[w.Root.Value] <- onChange
                    noop }

    withHost settings daemonFactory (fun fx host ->
        let a = attachedId (attachVia host (requestFrom fx.Primary "a"))
        let b = attachedId (attachVia host (requestFrom fx.Secondary "b"))
        let sessionA = (host.Registry.TryGet a).Value
        reload[fx.Primary.Root.Value]()
        test <@ sessionA.Ended.Wait(TimeSpan.FromSeconds 20.0) @>
        test <@ waitUntilTrue (fun () -> (host.Registry.TryGet a).IsNone) 5000 @>
        test <@ (host.Registry.TryGet b).IsSome @>)

// ---------------------------------------------------------------------------
// The endpoint, end to end
// ---------------------------------------------------------------------------

let private runHost (settings: HostSettings) (idleGrace: TimeSpan) (cts: CancellationTokenSource) =
    let run =
        Task.Run(fun () -> RepositoryHost.run settings daemonFactory idleGrace cts)

    test <@ waitUntilTrue (fun () -> RepositoryIpc.isRunning settings.Control.Endpoint) 20000 @>
    run

[<Fact(Timeout = 120000)>]
let ``over the endpoint: attach, call a session, list the host, and stop it`` () =
    withRepository (fun fx ->
        let settings = settingsFor fx
        use cts = new CancellationTokenSource()
        let run = runHost settings (TimeSpan.FromMinutes 5.0) cts
        let endpoint = settings.Control.Endpoint

        let attachOver worktree config =
            match
                decodeResponse (
                    RepositoryIpc.attach endpoint (encodeRequest (requestFrom worktree config))
                    |> Async.RunSynchronously
                )
            with
            | Ok reply -> attachedId reply
            | Error e -> failwith $"%A{e}"

        let a = attachOver fx.Primary "a"
        let b = attachOver fx.Secondary "b"

        let status =
            RepositoryIpc.invoke endpoint (Preamble.Session(a, InvocationId.mint ())) "GetStatus" [||]
            |> Async.RunSynchronously

        test <@ not (String.IsNullOrWhiteSpace status) @>

        let listed =
            RepositoryIpc.invoke endpoint (Preamble.Repository(InvocationId.mint ())) "ListSessions" [||]
            |> Async.RunSynchronously

        test <@ listed.Contains(SessionId.render a) && listed.Contains(SessionId.render b) @>

        let stale =
            { a with
                Incarnation = SessionIncarnation.mint () }

        let refused =
            Assert.ThrowsAny<exn>(fun () ->
                RepositoryIpc.invoke endpoint (Preamble.Session(stale, InvocationId.mint ())) "GetStatus" [||]
                |> Async.RunSynchronously
                |> ignore)

        test
            <@
                match refused with
                | RepositoryRefusedException("stale-incarnation", _) -> true
                | _ -> false
            @>

        RepositoryIpc.invoke endpoint (Preamble.Repository(InvocationId.mint ())) "StopHost" [||]
        |> Async.RunSynchronously
        |> ignore

        test <@ run.Wait(TimeSpan.FromSeconds 60.0) @>
        test <@ run.Result = HostRun.Stopped @>
        // Every worktree is handed back when the host stops.
        use relockA = (tryLockWorktree fx.Primary.Root.Value).Value
        use relockB = (tryLockWorktree fx.Secondary.Root.Value).Value
        test <@ not (File.Exists settings.Control.PidFile) @>)

[<Fact(Timeout = 120000)>]
let ``a second host for the same repository finds the first, and leaves it serving`` () =
    withRepository (fun fx ->
        let settings = settingsFor fx
        use cts = new CancellationTokenSource()
        let run = runHost settings (TimeSpan.FromMinutes 5.0) cts
        use cts2 = new CancellationTokenSource()

        test
            <@
                RepositoryHost.run settings daemonFactory (TimeSpan.FromMinutes 5.0) cts2 = HostRun.AlreadyRunning(
                    Some Environment.ProcessId
                )
            @>

        test <@ RepositoryIpc.isRunning settings.Control.Endpoint @>
        cts.Cancel()
        test <@ run.Wait(TimeSpan.FromSeconds 60.0) @>)

[<Fact(Timeout = 120000)>]
let ``a host with no session exits after its idle grace`` () =
    withRepository (fun fx ->
        use cts = new CancellationTokenSource()
        let run = runHost (settingsFor fx) (TimeSpan.FromMilliseconds 200.0) cts
        test <@ run.Wait(TimeSpan.FromSeconds 30.0) @>
        test <@ run.Result = HostRun.Stopped @>)

[<Fact(Timeout = 120000)>]
let ``a malformed preamble is refused, and the host keeps serving`` () =
    withRepository (fun fx ->
        let settings = settingsFor fx
        use cts = new CancellationTokenSource()
        let run = runHost settings (TimeSpan.FromMinutes 5.0) cts

        use pipe =
            new Pipes.NamedPipeClientStream(".", settings.Control.Endpoint, Pipes.PipeDirection.InOut)

        pipe.Connect 5000
        (writeFrame pipe "{\"schema\":\"something-else\"}" CancellationToken.None).Wait()
        let reply = (readFrame pipe CancellationToken.None).Result

        test
            <@
                match reply |> Result.map decodeReply with
                | Ok(Ok(PreambleReply.Refused("malformed-preamble", _))) -> true
                | _ -> false
            @>

        // An oversized preamble is refused without reading it.
        use big =
            new Pipes.NamedPipeClientStream(".", settings.Control.Endpoint, Pipes.PipeDirection.InOut)

        big.Connect 5000

        let header =
            BitConverter.GetBytes(Net.IPAddress.HostToNetworkOrder(MaxFrameBytes + 1))

        big.Write(header, 0, header.Length)
        big.Flush()

        test
            <@
                match (readFrame big CancellationToken.None).Result |> Result.map decodeReply with
                | Ok(Ok(PreambleReply.Refused("malformed-preamble", message))) -> message.Contains "exceeds"
                | _ -> false
            @>

        test <@ RepositoryIpc.isRunning settings.Control.Endpoint @>
        cts.Cancel()
        test <@ run.Wait(TimeSpan.FromSeconds 60.0) @>)

[<Fact(Timeout = 60000)>]
let ``a session releasing its worktree deletes only its own record`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let a = attachedId (attachVia host (requestFrom fx.Primary "a"))
        let record = HostSessionRecord.path fx.Primary.Root.Value

        let later =
            { HostPid = Environment.ProcessId
              Endpoint = "fshw-repo-later"
              Session =
                { a with
                    Incarnation = SessionIncarnation.mint () } }

        File.WriteAllText(record, HostSessionRecord.render later)
        test <@ host.Registry.Detach a @>
        test <@ HostSessionRecord.tryReadLive (fun _ -> true) fx.Primary.Root.Value = Some later @>)

[<Fact(Timeout = 60000)>]
let ``a reload the host refuses leaves the worktree unattached, and says why`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        let a = attachedId (attachVia host (requestFrom fx.Primary "a"))

        let reloadedElsewhere =
            { requestFrom fx.Primary "a-edited" with
                Environment = hostEnv.Add("Configuration", "Release") }

        let reply = attachVia host reloadedElsewhere
        test <@ refusalKind reply = "environment-mismatch" @>
        test <@ host.Registry.TryGet a |> Option.isNone @>
        use relock = (tryLockWorktree fx.Primary.Root.Value).Value
        ())

[<Fact(Timeout = 60000)>]
let ``a daemon lock with an unreadable pid is still its own daemon's`` () =
    withHost settingsFor daemonFactory (fun fx host ->
        use legacyLock = (tryLockWorktree fx.Primary.Root.Value).Value
        File.WriteAllText(Path.Combine(FsHwPaths.root fx.Primary.Root.Value, "daemon.pid"), "not a pid")
        let reply = attachVia host (requestFrom fx.Primary "a")
        test <@ refusalKind reply = "worktree-owned-by-daemon" @>
        test <@ not ((refusalMessage reply).Contains "pid") @>)

[<Fact(Timeout = 15000)>]
let ``a record nobody can read reads as no record`` () =
    if not (OperatingSystem.IsWindows()) then
        withRepository (fun fx ->
            Directory.CreateDirectory(FsHwPaths.root fx.Primary.Root.Value) |> ignore
            let record = HostSessionRecord.path fx.Primary.Root.Value
            File.WriteAllText(record, "{}")
            File.SetUnixFileMode(record, UnixFileMode.None)

            try
                test
                    <@
                        HostSessionRecord.tryReadLive (fun _ -> true) fx.Primary.Root.Value
                        |> Option.isNone
                    @>
            finally
                File.SetUnixFileMode(record, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact(Timeout = 60000)>]
let ``a host lock held without a pid says only that a host is running`` () =
    withRepository (fun fx ->
        let settings = settingsFor fx
        Directory.CreateDirectory settings.Control.Directory |> ignore

        use _held =
            new FileStream(settings.Control.LockFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)

        use cts = new CancellationTokenSource()
        test <@ RepositoryHost.run settings daemonFactory DefaultIdleGrace cts = HostRun.AlreadyRunning None @>)
