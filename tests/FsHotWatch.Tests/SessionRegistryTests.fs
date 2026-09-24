/// The sessions a repository host holds: each one a today's-`Daemon` built in its own
/// scope, removed the moment its run ends for any reason, and never able to disturb a
/// sibling on the way in or out.
module FsHotWatch.Tests.SessionRegistryTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.AttachHandshake
open FsHotWatch.Daemon
open FsHotWatch.Ipc
open FsHotWatch.RepositoryIdentity
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

/// A jj primary at `<dir>/r` with a nested secondary at `<dir>/r/.workspaces/b`.
let private withTwoWorktrees (body: ResolvedWorktree -> ResolvedWorktree -> unit) =
    withTempDir "registry" (fun dir ->
        let primary = Path.Combine(dir, "r")
        Directory.CreateDirectory(Path.Combine(primary, ".jj", "repo")) |> ignore
        Directory.CreateDirectory(Path.Combine(primary, "src")) |> ignore
        let b = Path.Combine(primary, ".workspaces", "b")
        Directory.CreateDirectory(Path.Combine(b, ".jj")) |> ignore
        Directory.CreateDirectory(Path.Combine(b, "src")) |> ignore
        File.WriteAllText(Path.Combine(b, ".jj", "repo"), "../../../.jj/repo")
        body (resolved primary) (resolved b))

let private sessionIdFor (worktree: ResolvedWorktree) =
    { Repository = worktree.Repository
      Worktree = worktree.Worktree
      Incarnation = SessionIncarnation.mint () }

/// Records every log line by the sink it reached.
type private Logs() =
    let lines = ConcurrentDictionary<string, ConcurrentQueue<string>>()

    member _.SinkFor(name: string) : Logging.LogSink =
        let q = lines.GetOrAdd(name, fun _ -> ConcurrentQueue())

        { Write = q.Enqueue
          Level = Logging.LogLevel.Info }

    member _.Of(name: string) =
        match lines.TryGetValue name with
        | true, q -> List.ofSeq q
        | _ -> []

let private specFor (logs: Logs) (name: string) (worktree: ResolvedWorktree) (owned: IDisposable list) =
    { Worktree = worktree
      Config = ConfigDigest.ofText name
      Environment = SessionEnvironment.create worktree.Root.Value (Map.ofList [ "PATH", "/usr/bin:/bin" ])
      Sink = logs.SinkFor name
      Owned = owned }

/// A factory building a real (checker-less) Daemon that watches through nothing.
let private daemonFactory (onBuilt: SessionSpec -> Daemon -> unit) : SessionFactory =
    fun spec ->
        let daemon =
            Daemon.createWithWatcherFactory
                nullChecker
                spec.Worktree.Root.Value
                { Daemon.DaemonOptions.defaults with
                    Hosting = FsHotWatch.DaemonHosting.hostedBy inertWatcher (fun _ -> nullChecker) }
                inertWatcher

        onBuilt spec daemon
        daemon

let private flag () =
    let disposed = ref 0

    let d =
        { new IDisposable with
            member _.Dispose() =
                Interlocked.Increment(&disposed.contents) |> ignore }

    d, disposed

let private serving (session: WorktreeSession) =
    session.Serving.Wait(TimeSpan.FromSeconds 20.0) |> ignore
    session.Serving.Result

[<Fact(Timeout = 60000)>]
let ``a started session serves its own daemon and is found by id, worktree and live incarnation`` () =
    withTwoWorktrees (fun a _ ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))
        let logs = Logs()
        let id = sessionIdFor a

        match registry.Start(id, specFor logs "a" a []) with
        | Error e -> failwith e
        | Ok session ->
            let config = serving session
            test <@ obj.ReferenceEquals(config.Host, session.Daemon.Host) @>
            test <@ registry.TryGet id |> Option.map (fun s -> s.Id) = Some id @>
            test <@ registry.TryGetWorktree a.Worktree |> Option.map (fun s -> s.Id) = Some id @>

            test
                <@
                    registry.Live a.Worktree = Some
                        { Session = id
                          Config = ConfigDigest.ofText "a" }
                @>

            test <@ registry.Sessions |> List.map (fun s -> s.Id) = [ id ] @>
            test <@ session.Worktree = a @>)

[<Fact(Timeout = 60000)>]
let ``each session is built in its own scope: its own sink, environment and process registry`` () =
    withTwoWorktrees (fun a b ->
        let seen = ConcurrentDictionary<string, string * ProcessRegistry.Registry>()

        let factory =
            daemonFactory (fun spec daemon ->
                Logging.info "session" $"built %s{spec.Worktree.Root.Value}"

                let path =
                    SessionEnvironment.current ()
                    |> Option.map (fun e -> SessionEnvironment.root e)
                    |> Option.defaultValue "<none>"

                seen[spec.Worktree.Root.Value] <- (path, daemon.ProcessRegistry))

        use registry = new SessionRegistry(factory)
        let logs = Logs()

        let sa =
            Result.toOption (registry.Start(sessionIdFor a, specFor logs "a" a []))
            |> Option.get

        let sb =
            Result.toOption (registry.Start(sessionIdFor b, specFor logs "b" b []))
            |> Option.get

        serving sa |> ignore
        serving sb |> ignore

        test <@ logs.Of "a" |> List.exists (fun l -> l.EndsWith $"built %s{a.Root.Value}") @>
        test <@ logs.Of "a" |> List.forall (fun l -> not (l.Contains b.Root.Value)) @>
        test <@ logs.Of "b" |> List.exists (fun l -> l.EndsWith $"built %s{b.Root.Value}") @>
        test <@ fst seen[a.Root.Value] = a.Root.Value && fst seen[b.Root.Value] = b.Root.Value @>
        test <@ not (obj.ReferenceEquals(snd seen[a.Root.Value], snd seen[b.Root.Value])) @>
        // Nothing the sessions installed leaked back into the thread that started them.
        test <@ SessionEnvironment.current () |> Option.isNone @>)

[<Fact(Timeout = 60000)>]
let ``stopping one session ends it alone: its resources go, its sibling keeps serving`` () =
    withTwoWorktrees (fun a b ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))
        let logs = Logs()
        let ownedA, disposedA = flag ()
        let ownedB, disposedB = flag ()
        let ida = sessionIdFor a
        let idb = sessionIdFor b

        let sa =
            Result.toOption (registry.Start(ida, specFor logs "a" a [ ownedA ]))
            |> Option.get

        let sb =
            Result.toOption (registry.Start(idb, specFor logs "b" b [ ownedB ]))
            |> Option.get

        serving sa |> ignore
        serving sb |> ignore

        test <@ registry.Detach ida @>
        test <@ sa.Ended.IsCompleted @>
        test <@ disposedA.Value = 1 @>
        test <@ registry.TryGet ida |> Option.isNone @>
        test <@ registry.Live a.Worktree |> Option.isNone @>

        test <@ disposedB.Value = 0 @>
        test <@ not sb.Ended.IsCompleted @>
        test <@ registry.TryGet idb |> Option.map (fun s -> s.Id) = Some idb @>
        test <@ not sb.Serving.IsCanceled @>)

[<Fact(Timeout = 60000)>]
let ``a session that asks to shut down ends itself, not its sibling`` () =
    withTwoWorktrees (fun a b ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))
        let logs = Logs()

        let sa =
            Result.toOption (registry.Start(sessionIdFor a, specFor logs "a" a []))
            |> Option.get

        let sb =
            Result.toOption (registry.Start(sessionIdFor b, specFor logs "b" b []))
            |> Option.get

        let configA = serving sa
        serving sb |> ignore

        // The `Shutdown` RPC, an idle-exit and a wedge restart all take this path.
        configA.RequestShutdown()
        test <@ sa.Ended.Wait(TimeSpan.FromSeconds 20.0) @>
        test <@ waitUntilTrue (fun () -> registry.TryGet sa.Id |> Option.isNone) 5000 @>
        test <@ not sb.Ended.IsCompleted @>
        test <@ registry.Sessions |> List.map (fun s -> s.Id) = [ sb.Id ] @>)

[<Fact(Timeout = 60000)>]
let ``a session whose run faults is removed and releases what it owned; its sibling is untouched`` () =
    withTwoWorktrees (fun a b ->
        let faulting: SessionRun =
            fun daemon serve startedAt cts ->
                async {
                    if daemon.RepoRoot = a.Root.Value then
                        failwith "session A crashed"

                    return! defaultRun daemon serve startedAt cts
                }

        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()), faulting)
        let logs = Logs()
        let ownedA, disposedA = flag ()

        let sa =
            Result.toOption (registry.Start(sessionIdFor a, specFor logs "a" a [ ownedA ]))
            |> Option.get

        let sb =
            Result.toOption (registry.Start(sessionIdFor b, specFor logs "b" b []))
            |> Option.get

        test <@ sa.Ended.Wait(TimeSpan.FromSeconds 20.0) @>
        test <@ waitUntilTrue (fun () -> registry.TryGet sa.Id |> Option.isNone) 5000 @>
        test <@ disposedA.Value = 1 @>
        test <@ sa.Serving.IsCanceled @>
        test <@ logs.Of "a" |> List.exists (fun l -> l.Contains "session A crashed") @>
        serving sb |> ignore
        test <@ not sb.Ended.IsCompleted @>)

[<Fact(Timeout = 60000)>]
let ``a factory that throws starts nothing and releases what the start was handed`` () =
    withTwoWorktrees (fun a _ ->
        use registry = new SessionRegistry(fun _ -> invalidOp "no projects discovered")
        let owned, disposed = flag ()

        match registry.Start(sessionIdFor a, specFor (Logs()) "a" a [ owned ]) with
        | Ok _ -> failwith "the start should have failed"
        | Error message ->
            test <@ message.Contains "no projects discovered" @>
            test <@ disposed.Value = 1 @>
            test <@ registry.Live a.Worktree |> Option.isNone @>)

[<Fact(Timeout = 60000)>]
let ``a worktree holds one session: starting a second for it is refused`` () =
    withTwoWorktrees (fun a _ ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))

        let first =
            Result.toOption (registry.Start(sessionIdFor a, specFor (Logs()) "a" a []))
            |> Option.get

        serving first |> ignore
        let owned, disposed = flag ()

        match registry.Start(sessionIdFor a, specFor (Logs()) "a2" a [ owned ]) with
        | Ok _ -> failwith "a second session for one worktree must be refused"
        | Error message ->
            test <@ message.Contains "already" @>
            test <@ disposed.Value = 1 @>
            test <@ registry.TryGetWorktree a.Worktree |> Option.map (fun s -> s.Id) = Some first.Id @>)

[<Fact(Timeout = 60000)>]
let ``detaching an unknown session changes nothing`` () =
    withTwoWorktrees (fun a _ ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))
        test <@ not (registry.Detach(sessionIdFor a)) @>)

[<Fact(Timeout = 60000)>]
let ``disposing the registry ends every session`` () =
    withTwoWorktrees (fun a b ->
        let registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))

        let sa =
            Result.toOption (registry.Start(sessionIdFor a, specFor (Logs()) "a" a []))
            |> Option.get

        let sb =
            Result.toOption (registry.Start(sessionIdFor b, specFor (Logs()) "b" b []))
            |> Option.get

        serving sa |> ignore
        serving sb |> ignore
        (registry :> IDisposable).Dispose()
        test <@ sa.Ended.IsCompleted && sb.Ended.IsCompleted @>
        test <@ List.isEmpty registry.Sessions @>)

[<Fact(Timeout = 60000)>]
let ``stopping one session never reaps a sibling's children`` () =
    if OperatingSystem.IsWindows() then
        Assert.Skip "POSIX sleep"

    withTwoWorktrees (fun a b ->
        let children = ConcurrentDictionary<string, Process>()

        // Each child is tracked by THAT session's daemon's registry.
        let factory =
            daemonFactory (fun spec daemon ->
                use _ = ProcessRegistry.install daemon.ProcessRegistry
                let p = Process.Start("sleep", "60")
                ProcessRegistry.track p
                children[spec.Worktree.Root.Value] <- p)

        use registry = new SessionRegistry(factory)

        let sa =
            Result.toOption (registry.Start(sessionIdFor a, specFor (Logs()) "a" a []))
            |> Option.get

        let sb =
            Result.toOption (registry.Start(sessionIdFor b, specFor (Logs()) "b" b []))
            |> Option.get

        serving sa |> ignore
        serving sb |> ignore
        let childA = children[a.Root.Value]
        let childB = children[b.Root.Value]

        try
            test <@ registry.Detach sb.Id @>
            test <@ childB.WaitForExit 10000 @>
            test <@ not childA.HasExited @>
        finally
            registry.Detach sa.Id |> ignore

        test <@ childA.WaitForExit 10000 @>)

[<Fact(Timeout = 60000)>]
let ``an older incarnation of a live worktree is not the live session`` () =
    withTwoWorktrees (fun a _ ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))

        let live =
            Result.toOption (registry.Start(sessionIdFor a, specFor (Logs()) "a" a []))
            |> Option.get

        serving live |> ignore
        let stale = sessionIdFor a
        test <@ registry.TryGet stale |> Option.isNone @>
        test <@ not (registry.Detach stale) @>
        test <@ registry.TryGet live.Id |> Option.isSome @>)

[<Fact(Timeout = 60000)>]
let ``stopping a session that already ended is harmless`` () =
    withTwoWorktrees (fun a _ ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))

        let session =
            Result.toOption (registry.Start(sessionIdFor a, specFor (Logs()) "a" a []))
            |> Option.get

        serving session |> ignore
        test <@ registry.Detach session.Id @>
        session.Stop()
        test <@ session.Ended.Result = SessionEnd.Stopped @>)

[<Fact(Timeout = 60000)>]
let ``a resource that fails to release does not stop the others, or the session's end`` () =
    withTwoWorktrees (fun a _ ->
        use registry = new SessionRegistry(daemonFactory (fun _ _ -> ()))
        let logs = Logs()

        let broken =
            { new IDisposable with
                member _.Dispose() = invalidOp "lock already gone" }

        let owned, disposed = flag ()

        let session =
            Result.toOption (registry.Start(sessionIdFor a, specFor logs "a" a [ broken; owned ]))
            |> Option.get

        serving session |> ignore
        test <@ registry.Detach session.Id @>
        test <@ disposed.Value = 1 @>
        test <@ logs.Of "a" |> List.exists (fun l -> l.Contains "lock already gone") @>)
