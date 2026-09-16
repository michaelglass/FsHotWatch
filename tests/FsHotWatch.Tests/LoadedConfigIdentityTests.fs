/// The daemon, not whoever launched it, attests which `.fshw.json` it is running.
module FsHotWatch.Tests.LoadedConfigIdentityTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.Program
open FsHotWatch.Daemon
open FsHotWatch.Ipc
open FsHotWatch.Tests.TestHelpers

let private noIpc () : IpcOps =
    { Shutdown = fun _ -> async { return "" }
      Scan = fun _ -> async { return "" }
      ScanStatus = fun _ -> async { return "" }
      GetStatus = fun _ -> async { return "{}" }
      GetPluginStatus = fun _ _ -> async { return "" }
      RunCommand = fun _ _ _ -> async { return "" }
      GetDiagnostics = fun _ _ -> async { return "{}" }
      WaitForScan = fun _ _ -> async { return "" }
      WaitForComplete = fun _ _ -> async { return "{}" }
      TriggerBuild = fun _ -> async { return "" }
      FormatAll = fun _ -> async { return "" }
      RerunPlugin = fun _ _ -> async { return "" }
      Invalidate = fun _ -> async { return "" }
      IsRunning = fun _ -> false
      LaunchDaemon = fun _ _ _ -> () }

let private daemonLockIsFree (root: string) =
    try
        use _probe =
            new FileStream(
                Path.Combine(root, ".fshw", "daemon.lock"),
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.None
            )

        true
    with :? IOException ->
        false

[<Theory(Timeout = 15000)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``launcher cannot overwrite or manufacture daemon loaded config identity`` (publishes: bool) =
    withTempDir "daemon-config-owner" (fun root ->
        let state = Path.Combine(root, ".fshw")
        let identity = Path.Combine(state, "config.hash")

        let ipc =
            { noIpc () with
                LaunchDaemon =
                    fun _ _ _ ->
                        if publishes then
                            Directory.CreateDirectory state |> ignore
                            File.WriteAllText(identity, "daemon-loaded-snapshot")
                IsRunning = fun _ -> publishes }

        let running =
            startFreshDaemonWith defaultFileOps ipc root "fixture-pipe" "" "logs" 0.0

        Assert.Equal(publishes, running)

        if publishes then
            Assert.Equal("daemon-loaded-snapshot", File.ReadAllText identity)
        else
            Assert.False(File.Exists identity, "a launch attempt cannot attest to a loaded configuration"))

[<Theory(Timeout = 15000)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``loaded configuration identity retains the parsed snapshot across later file changes`` (exists: bool) =
    withTempDir "daemon-config-snapshot" (fun root ->
        let path = Path.Combine(root, ".fshw.json")
        let original = if exists then """{"lint":false}""" else ""

        if exists then
            File.WriteAllText(path, original)

        let loaded, source = loadConfigWithSource root
        Assert.Equal(original, source)
        Assert.Equal(not exists, loaded.Lint)

        let identity = configContentHash source
        File.WriteAllText(path, """{"lint":true}""")
        Assert.Equal(configContentHash original, identity)
        Assert.NotEqual<string>(computeConfigHashWith defaultFileOps root, identity))

[<Fact(Timeout = 30000)>]
let ``direct Start publishes its loaded identity and stops on a later config edit`` () =
    withTempDir "cli-start-config-lifecycle" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
        File.WriteAllText(Path.Combine(root, "src", "Stub.fsproj"), "<Project />")
        let configPath = Path.Combine(root, ".fshw.json")
        File.WriteAllText(configPath, """{"build":false,"format":false,"lint":false}""")
        let config, loadedSource = loadConfigWithSource root
        let identity = configContentHash loadedSource
        let pipe = computePipeName root
        let receipt = Path.Combine(root, ".fshw", "config.hash")
        let pidFile = Path.Combine(root, ".fshw", "daemon.pid")
        let notifications = Event<Ionide.ProjInfo.Types.WorkspaceProjectState>()

        // This test owns startup and the config watcher, not SDK project loading, and
        // `OneShot` keeps the unrelated source watcher out; `RunWithIpc` still serves.
        let loader =
            { new Ionide.ProjInfo.IWorkspaceLoader with
                member _.LoadProjects(_paths) = Seq.empty
                member _.LoadProjects(_paths, _properties, _binaryLog) = Seq.empty
                member _.LoadSln(_path) = Seq.empty
                member _.LoadSln(_path, _properties, _binaryLog) = Seq.empty

                [<CLIEvent>]
                member _.Notifications = notifications.Publish }

        use daemon =
            Daemon.createWithWorkspaceLoader
                Unchecked.defaultof<_>
                root
                { Daemon.DaemonOptions.defaults with
                    RunMode = Daemon.RunMode.OneShot }
                loader
                (fun _ -> [])

        let run =
            Task.Run(fun () ->
                executeCommand identity (fun _ -> daemon) (noIpc ()) root pipe Start defaultGlobalOptions config 5.0)

        try
            Assert.True(
                waitUntilTrue
                    (fun () ->
                        try
                            Async.RunSynchronously(IpcClient.getStatus pipe, 1000) |> ignore
                            true
                        with _ ->
                            false)
                    10000,
                "positive control: the directly started daemon must serve IPC"
            )

            Assert.False(run.IsCompleted)
            Assert.Equal(identity, File.ReadAllText receipt)
            Assert.Equal(string Environment.ProcessId, File.ReadAllText pidFile)

            // IPC is up only after the watcher subscribed, so this edit must reach it.
            File.WriteAllText(configPath, """{"build":false,"format":false,"lint":false,"timeoutSec":42}""")
            Assert.True(run.Wait(TimeSpan.FromSeconds 10.0), "a config edit must stop the daemon it owns")
            Assert.Equal(0, run.Result)
            Assert.False(IpcClient.isRunning pipe)
            Assert.False(File.Exists pidFile)
            Assert.True(daemonLockIsFree root)
            // The receipt still names what the stopped daemon loaded, not the new file.
            Assert.Equal(identity, File.ReadAllText receipt)
            Assert.NotEqual<string>(computeConfigHashWith defaultFileOps root, identity)
        finally
            if not run.IsCompleted then
                try
                    IpcClient.shutdown pipe |> Async.RunSynchronously |> ignore
                with _ ->
                    ()

                run.Wait(TimeSpan.FromSeconds 10.0) |> ignore)
