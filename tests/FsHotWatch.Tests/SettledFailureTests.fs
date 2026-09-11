module FsHotWatch.Tests.SettledFailureTests

open System
open Xunit
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Build
open FsHotWatch.ProjectGraph
open FsHotWatch.Tests.TestHelpers

[<Theory(Timeout = 20000)>]
[<InlineData("current")>]
[<InlineData("new-model")>]
[<InlineData("new-inputs")>]
let ``public verdict wait reports current failed build without inventing test evidence`` (transition: string) =
    withTempDir "failed-build-verdict-wait" (fun root ->
        let source = System.IO.Path.Combine(root, "Source.fs")
        let project = System.IO.Path.Combine(root, "ManualTests.fsproj")
        let graph = ProjectGraph()
        System.IO.File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Source.fs\" /></ItemGroup></Project>")
        graph.RegisterFromFsproj(project) |> ignore
        graph.RegisterProjectOutput(AbsProjectPath.create project, System.IO.Path.Combine(root, "bin", "Debug", "net10.0", "ManualTests.dll"))
        let buildScript = System.IO.Path.Combine(root, "build.sh")
        let testScript = System.IO.Path.Combine(root, "test.sh")
        let testStarted = System.IO.Path.Combine(root, "test-started")
        System.IO.File.WriteAllText(source, "module Source\nlet value = 1\n")
        System.IO.File.WriteAllText(buildScript, $"echo '{source}(1,1): error FS0001: current-build-refusal'\nexit 1\n")
        System.IO.File.WriteAllText(testScript, $"touch '{testStarted}'\n")
        let host = PluginHost.create (Unchecked.defaultof<_>) root
        let available generation =
            FsHotWatch.ProjectModel.ofCompleted generation
                { Discovered = 1; Loaded = 1; OptionsMapped = 1; Registered = 1 }
        host.WorkStore.PublishProjectModelWithFiles(available 1L, Set.singleton (AbsFilePath.create source))
        let tests =
            FsHotWatch.TestPrune.TestPrunePlugin.create
                ":memory:" root
                (Some
                    [ { FsHotWatch.TestPrune.TestPrunePlugin.TestConfig.Project = "ManualTests"
                        Command = "sh"
                        Args = testScript
                        Group = "default"
                        Environment = []
                        FilterTemplate = None
                        ClassJoin = " "
                        TimeoutSec = Some 5
                        ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect } ])
                None None None None []
        host.RegisterHandler tests
        host.RegisterHandler(BuildPlugin.create "sh" buildScript [] graph [] None [] (Some 5))
        host.EmitFileChanged(SourceChanged [ source ])
        waitUntil
            (fun () ->
                not host.WorkSnapshot.IsBusy
                && (host.GetAllStatuses() |> Map.exists (fun name status ->
                    name = "build" && match status with | Failed _ -> true | _ -> false)))
            5000
        // Exercise the shared artifact admission path after a real failing command.
        // Invalid artifacts must prevent a test process from being started.
        let reply = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        Assert.Contains("preceding build left invalid artifacts", reply.Value)
        waitUntil (fun () -> not host.WorkSnapshot.IsBusy) 5000
        Assert.False(System.IO.File.Exists testStarted)
        Assert.Empty host.WorkSnapshot.Evidence
        match transition with
        | "new-model" ->
            host.WorkStore.PublishProjectModelWithFiles(available 2L, Set.singleton (AbsFilePath.create source))
        | "new-inputs" -> System.IO.File.WriteAllText(source, "module Source\nlet value = 2\n")
        | _ -> ()
        let rpcConfig: FsHotWatch.Ipc.DaemonRpcConfig =
            { Host = host
              RequestShutdown = ignore
              RequestScan = ignore
              GetScanStatus = fun () -> "idle"
              GetScanGeneration = fun () -> 1L
              TriggerBuild = fun () -> async.Return()
              FormatAll = fun () -> async.Return ""
              WaitForScanGeneration = fun _ -> System.Threading.Tasks.Task.FromResult(())
              WaitForAllTerminal = fun timeout ->
                  FsHotWatch.Daemon.waitForVerdict host timeout System.Threading.CancellationToken.None
              RerunPlugin = fun _ -> async.Return(Ok())
              InvalidateCache = fun () -> System.Threading.Tasks.Task.FromResult(())
              GetUncheckedCount = fun () -> 0 }
        let target = FsHotWatch.Ipc.DaemonRpcTarget rpcConfig
        if transition = "current" then
            let wire = target.WaitForComplete(1000).GetAwaiter().GetResult()
            Assert.Contains("current-build-refusal", wire)
            Assert.Contains("failed", wire)
        else
            // An earlier red is not authority to settle a different model/tree.
            Assert.Throws<TimeoutException>(fun () -> target.WaitForComplete(1000).GetAwaiter().GetResult() |> ignore)
            |> ignore
        Assert.Empty host.WorkSnapshot.Evidence
        Assert.False(System.IO.File.Exists testStarted))
