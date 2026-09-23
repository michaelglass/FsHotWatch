/// The seams a repository host uses to run today's `Daemon` as one of several
/// sessions in its process: it serves through whatever the host hands it, watches
/// through the host's shared stream, and leaves process-wide state alone.
module FsHotWatch.Tests.DaemonHostingTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.Ipc
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

let private inertWatcher: Watcher.FileWatcher =
    { Mode = Watcher.WatcherMode.NativeEvents
      Disposables = [] }

let private throwingFactory: Daemon.WatcherFactory =
    fun _ _ _ _ _ -> failwith "a hosted session must watch through the host's stream"

[<Fact(Timeout = 20000)>]
let ``a hosted session watches through the host's factory, never its own`` () =
    withTempDir "hosted-watcher" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        let calls = ref 0

        let shared: Daemon.WatcherFactory =
            fun root _ _ _ _ ->
                Interlocked.Increment(&calls.contents) |> ignore
                test <@ root = tmpDir @>
                inertWatcher

        use _daemon =
            Daemon.createWithWatcherFactory
                nullChecker
                tmpDir
                { Daemon.DaemonOptions.defaults with
                    Hosting = DaemonHosting.hostedBy shared }
                throwingFactory

        test <@ calls.Value = 1 @>)

[<Fact(Timeout = 5000)>]
let ``a standalone daemon keeps every process-wide behaviour`` () =
    let seams = DaemonHosting.seams (DaemonHosting.standalone ())
    test <@ seams.ResourceScope = DaemonHosting.ResourceScope.Process @>
    test <@ seams.MayForceGc && seams.ClearsProcessCaches @>
    // A standalone daemon builds its own watcher: the factory it was given runs.
    let own =
        Assert.Throws<exn>(fun () -> seams.Watcher throwingFactory "/r" ignore None [] 0.25 |> ignore)

    test <@ own.Message.Contains "host's stream" @>

[<Fact(Timeout = 5000)>]
let ``a hosted session subtracts exactly the process-wide behaviours, and watches through the host`` () =
    let calls = ref 0

    let shared: Daemon.WatcherFactory =
        fun _ _ _ _ _ ->
            calls.Value <- calls.Value + 1
            inertWatcher

    let seams = DaemonHosting.seams (DaemonHosting.hostedBy shared)
    test <@ seams.ResourceScope = DaemonHosting.ResourceScope.Host @>
    test <@ not seams.MayForceGc && not seams.ClearsProcessCaches @>
    // The session's own factory is never asked; the host's is.
    seams.Watcher throwingFactory "/r" ignore None [] 0.25 |> ignore
    test <@ calls.Value = 1 @>

[<Fact(Timeout = 5000)>]
let ``hosting defaults to standalone`` () =
    test <@ (DaemonHosting.seams Daemon.DaemonOptions.defaults.Hosting).ClearsProcessCaches @>

[<Fact(Timeout = 5000)>]
let ``a resource scope round-trips its wire spelling, and anything else is the process`` () =
    for scope in [ DaemonHosting.ResourceScope.Process; DaemonHosting.ResourceScope.Host ] do
        test <@ DaemonHosting.ResourceScope.parse (DaemonHosting.ResourceScope.render scope) = scope @>

    test <@ DaemonHosting.ResourceScope.parse "" = DaemonHosting.ResourceScope.Process @>
    test <@ DaemonHosting.ResourceScope.parse "elsewhere" = DaemonHosting.ResourceScope.Process @>

[<Fact(Timeout = 20000)>]
let ``RunWith hands serve the daemon's RPC configuration and stops when cancelled`` () =
    withTempDir "run-with" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        use cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults
        let served = TaskCompletionSource<DaemonRpcConfig>()

        let serve (config: DaemonRpcConfig) (serveCts: CancellationTokenSource) =
            async {
                served.SetResult config

                do!
                    Task.Delay(Timeout.Infinite, serveCts.Token)
                    |> Async.AwaitTask
                    |> Async.Catch
                    |> Async.Ignore
            }

        let startedAt = DateTime.UtcNow.AddSeconds -3.0

        let run =
            Async.StartAsTask(daemon.RunWith(serve, TimeSpan.FromSeconds 5.0, startedAt, cts))

        let config = served.Task.Result
        test <@ obj.ReferenceEquals(config.Host, daemon.Host) @>

        let startup =
            daemon.Host.Phases.Snapshot DateTime.UtcNow
            |> List.filter (fun p -> p.Scope = DaemonPhases.Phase.scope DaemonPhases.Phase.Startup)

        test
            <@
                startup
                |> List.exists (fun p -> p.StartedAt = startedAt && p.Elapsed >= TimeSpan.FromSeconds 3.0)
            @>

        cts.Cancel()
        test <@ run.Wait(TimeSpan.FromSeconds 10.0) @>)

[<Fact(Timeout = 20000)>]
let ``RunWith waits for serve no longer than its bound`` () =
    withTempDir "run-with-bound" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        use cts = new CancellationTokenSource()
        let daemon = Daemon.createWith nullChecker tmpDir Daemon.DaemonOptions.defaults

        // A serve that ignores cancellation entirely.
        let serve (_: DaemonRpcConfig) (_: CancellationTokenSource) =
            Task.Delay(Timeout.Infinite) |> Async.AwaitTask

        let run =
            Async.StartAsTask(daemon.RunWith(serve, TimeSpan.FromMilliseconds 200.0, DateTime.UtcNow, cts))

        daemon.Ready.Wait(TimeSpan.FromSeconds 10.0) |> ignore
        cts.Cancel()
        test <@ run.Wait(TimeSpan.FromSeconds 10.0) @>)

[<Fact(Timeout = 5000)>]
let ``a checked cohort's settle line keeps the shape benchmarks parse`` () =
    test <@ settledLine 7L (TimeSpan.FromMilliseconds 1234.9) 3 = "settled epoch=7 after=1234ms files=3" @>

[<Fact(Timeout = 5000)>]
let ``a change batch names a model wait long enough to explain a slow settle`` () =
    test
        <@ captureWaitLine (TimeSpan.FromMilliseconds 1500.7) = Some "change batch waited 1500ms for the project model" @>

    test
        <@ captureWaitLine (TimeSpan.FromMilliseconds 100.0) = Some "change batch waited 100ms for the project model" @>

    test <@ captureWaitLine (TimeSpan.FromMilliseconds 99.0) = None @>
