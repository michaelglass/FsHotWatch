/// A daemon's process registry is the daemon's. Building one in-process (a test, a
/// run-once check, a repository host's session) leaves the caller's process scope as
/// it was: a spawn the caller makes afterwards is the caller's, never the daemon's, and
/// never refused by a daemon that has since shut down.
module FsHotWatch.Tests.DaemonProcessScopeTests

open System
open System.IO
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

[<Fact(Timeout = 30000)>]
let ``a spawn after a daemon is created and disposed belongs to the caller's scope`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "daemon-scope-caller" (fun dir ->
            let finished =
                isolated (fun () ->
                    let caller = ProcessRegistry.Registry()
                    use _ = ProcessRegistry.install caller
                    createAndDispose dir

                    // Flows the caller's context, so the spawn resolves whatever registry
                    // the caller has in scope now.
                    let run =
                        Task.Run(fun () ->
                            runProcess "sleep" "30" dir [] (ProcessBounds.silent (TimeSpan.FromSeconds 60.0)))

                    Threading.Thread.Sleep 500
                    // The caller's scope reaps it: it was admitted there.
                    caller.KillAll()
                    run.Wait(TimeSpan.FromSeconds 10.0) && not run.IsFaulted)

            test <@ finished @>)

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
