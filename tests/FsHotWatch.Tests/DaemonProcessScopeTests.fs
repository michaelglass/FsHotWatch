/// A daemon's process registry is the daemon's. Building one in-process (a test, a
/// run-once check, a repository host's session) leaves the caller's process scope as
/// it was: a spawn the caller makes afterwards is the caller's, never the daemon's, and
/// never refused by a daemon that has since shut down. However the caller starts the
/// daemon's work, and however busy the thread pool is.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.DaemonProcessScopeTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.ProcessHelper
open FsHotWatch.SessionScope
open FsHotWatch.Tests.TestHelpers

let private createAndDispose (dir: string) =
    Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore

    let daemon =
        Daemon.createWith (Unchecked.defaultof<_>) dir Daemon.DaemonOptions.defaults

    (daemon :> IDisposable).Dispose()

[<Fact(Timeout = 30000)>]
let ``a spawn after a daemon is created and disposed is not refused by its shut registry`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-refused" (fun dir ->
            let outcome =
                isolated (fun () ->
                    createAndDispose dir
                    runProcess "sh" "-c true" dir [] (ProcessBounds.silent (TimeSpan.FromSeconds 10.0)))

            test <@ isSucceeded outcome @>)

[<Fact(Timeout = 60000)>]
let ``a spawn after a daemon is created and disposed belongs to the caller's scope`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-caller" (fun dir ->
            let pidFile = Path.Combine(dir, "child.pid")
            let outcome = ref None

            let started =
                isolated (fun () ->
                    let caller = ProcessRegistry.Registry()
                    use _ = ProcessRegistry.install caller
                    createAndDispose dir

                    // A thread of its own carries the caller's context, so the spawn
                    // resolves whatever registry the caller has in scope now, and its
                    // admission never waits for the pool.
                    let spawner =
                        Thread(fun () ->
                            outcome.Value <-
                                try
                                    Some(
                                        Ok(
                                            runProcess
                                                "sh"
                                                $"-c \"echo $$ > '%s{pidFile}'; exec sleep 30\""
                                                dir
                                                []
                                                (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))
                                        )
                                    )
                                with ex ->
                                    Some(Error ex.Message))

                    let started =
                        withEveryPoolThreadBusy (fun () ->
                            spawner.Start()
                            // Reaped only once it runs: the caller's scope has admitted it.
                            let running = waitUntilTrue (fun () -> File.Exists pidFile) 20000
                            caller.KillAll()
                            running)

                    spawner.Join(TimeSpan.FromSeconds 30.0) |> ignore
                    started)

            test <@ started @>

            test
                <@
                    match outcome.Value with
                    | Some(Ok _) -> true
                    | _ -> false
                @>)

/// Start the daemon's work on the caller's thread with `start`, which returns once it
/// has ended; then spawn. The spawn is the caller's, not refused by the daemon's scope.
let private spawnAfter (dir: string) (start: unit -> unit) =
    isolated (fun () ->
        let caller = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install caller
        start ()
        runProcess "sh" "-c true" dir [] (ProcessBounds.silent (TimeSpan.FromSeconds 10.0)))

let private daemonIn (dir: string) =
    Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
    Daemon.createWith (Unchecked.defaultof<_>) dir Daemon.DaemonOptions.defaults

[<Fact(Timeout = 60000)>]
let ``a daemon served on the caller's thread leaves the caller's scope as it was`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-serve" (fun dir ->
            let outcome =
                spawnAfter dir (fun () ->
                    let daemon = daemonIn dir
                    use cts = new CancellationTokenSource()

                    let serving =
                        Async.StartImmediateAsTask(daemon.RunWithIpc(FsHotWatch.Cli.Program.computePipeName dir, cts))

                    cts.Cancel()
                    serving.Wait(TimeSpan.FromSeconds 30.0) |> ignore)

            test <@ isSucceeded outcome @>)

[<Fact(Timeout = 60000)>]
let ``a daemon run started on the caller's thread leaves the caller's scope as it was`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-run" (fun dir ->
            let outcome =
                spawnAfter dir (fun () ->
                    let daemon = daemonIn dir
                    use cts = new CancellationTokenSource()
                    let run = Async.StartImmediateAsTask(daemon.Run cts.Token)
                    cts.Cancel()
                    run.Wait(TimeSpan.FromSeconds 30.0) |> ignore)

            test <@ isSucceeded outcome @>)

[<Fact(Timeout = 30000)>]
let ``a child scope started on the caller's thread leaves the caller's scope as it was`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-child" (fun dir ->
            let outcome =
                spawnAfter dir (fun () ->
                    let child =
                        Async.StartImmediateAsTask(
                            ProcessRegistry.withChildScopeAsync CancellationToken.None (Async.Sleep 50)
                        )

                    child.Wait(TimeSpan.FromSeconds 10.0) |> ignore)

            test <@ isSucceeded outcome @>)

[<Fact(Timeout = 30000)>]
let ``with context flow suppressed, work runs in its registry and the caller's is restored`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-suppressed" (fun dir ->
            let shut = ProcessRegistry.Registry()
            shut.KillAll()

            let inWork, afterwards =
                isolated (fun () ->
                    let inWork =
                        using (ExecutionContext.SuppressFlow()) (fun _ ->
                            // Completes before any wait: the spawn sees the shut registry.
                            Async.StartImmediateAsTask(
                                ProcessRegistry.withRegistryAsync
                                    shut
                                    (async {
                                        return
                                            try
                                                runProcess
                                                    "sh"
                                                    "-c true"
                                                    dir
                                                    []
                                                    (ProcessBounds.silent (TimeSpan.FromSeconds 10.0))
                                                |> Ok
                                            with ex ->
                                                Error ex.Message
                                    })
                            ))

                    let afterwards =
                        runProcess "sh" "-c true" dir [] (ProcessBounds.silent (TimeSpan.FromSeconds 10.0))

                    inWork.Result, afterwards)

            test
                <@
                    match inWork with
                    | Error message -> message.Contains "process scope has shut down"
                    | Ok _ -> false
                @>

            test <@ isSucceeded afterwards @>)

let private alive (pid: int) =
    try
        use p = Diagnostics.Process.GetProcessById pid
        not p.HasExited
    with _ ->
        false

[<Fact(Timeout = 60000)>]
let ``a plugin registered after the daemon is built spawns into the daemon's scope`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-plugin" (fun dir ->
            Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
            let pidFile = Path.Combine(dir, "plugin.pid")

            isolated (fun () ->
                // Registered from a context with no process scope of its own, as the CLI
                // and a repository host register plugins once the daemon is built.
                let daemon =
                    Daemon.createWith (Unchecked.defaultof<_>) dir Daemon.DaemonOptions.defaults

                let handler: PluginFramework.PluginHandler<unit, obj> =
                    { Name = PluginFramework.PluginName.create "spawner"
                      Init = ()
                      Update =
                        fun _ state event ->
                            async {
                                match event with
                                | Events.FileChanged _ ->
                                    runProcess
                                        "sh"
                                        $"-c \"echo $$ > '%s{pidFile}'; exec sleep 30\""
                                        dir
                                        []
                                        (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))
                                    |> ignore
                                | _ -> ()

                                return state
                            }
                      Commands = []
                      Subscriptions = Set.ofList [ PluginFramework.SubscribeFileChanged ]
                      PrepareCommit = None
                      CacheKey = None
                      Teardown = None }

                daemon.RegisterHandler handler
                daemon.Host.EmitFileChanged(Events.SourceChanged [ Path.Combine(dir, "src", "X.fs") ])

                let pid =
                    if
                        waitUntilTrue (fun () -> File.Exists pidFile && (File.ReadAllText pidFile).Trim() <> "") 20000
                    then
                        int ((File.ReadAllText pidFile).Trim())
                    else
                        failwith "the plugin never spawned"

                try
                    test <@ alive pid @>
                    // The daemon's scope owns it: disposing the daemon reaps it.
                    (daemon :> IDisposable).Dispose()
                    test <@ waitUntilTrue (fun () -> not (alive pid)) 5000 @>
                finally
                    try
                        (Diagnostics.Process.GetProcessById pid).Kill(true)
                    with _ ->
                        ()))
