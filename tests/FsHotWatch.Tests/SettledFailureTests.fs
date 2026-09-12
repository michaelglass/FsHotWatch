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
[<InlineData("new-project")>]
[<InlineData("deleted-source")>]
[<InlineData("unreadable-source")>]
[<InlineData("ignored-event")>]
[<InlineData("cached-failure")>]
[<InlineData("external-wire-inputs")>]
let ``public verdict wait reports current failed build without inventing test evidence`` (transition: string) =
    withTempDir "failed-build-external-source" (fun externalRoot ->
        withTempDir "failed-build-verdict-wait" (fun root ->
            let sourceRoot =
                if transition = "external-wire-inputs" then
                    externalRoot
                else
                    root

            let source = System.IO.Path.Combine(sourceRoot, "Source.fs")
            let project = System.IO.Path.Combine(root, "ManualTests.fsproj")
            let graph = ProjectGraph()
            writeMinimalFsproj project "net10.0" [ System.IO.Path.GetRelativePath(root, source) ]
            graph.RegisterFromFsproj(project) |> ignore

            graph.RegisterProjectOutput(
                AbsProjectPath.create project,
                System.IO.Path.Combine(root, "bin", "Debug", "net10.0", "ManualTests.dll")
            )

            let buildScript = System.IO.Path.Combine(root, "build.sh")
            let testScript = System.IO.Path.Combine(root, "test.sh")
            let testStarted = System.IO.Path.Combine(root, "test-started")
            let buildStarted = System.IO.Path.Combine(root, "build-started")
            System.IO.File.WriteAllText(source, "module Source\nlet value = 1\n")

            System.IO.File.WriteAllText(
                buildScript,
                $"touch '{buildStarted}'\necho '{source}(1,1): error FS0001: current-build-refusal'\nexit 1\n"
            )

            System.IO.File.WriteAllText(testScript, $"touch '{testStarted}'\n")

            let cache =
                FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

            let host = PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)

            let available generation =
                FsHotWatch.ProjectModel.ofCompleted
                    generation
                    { Discovered = 1
                      Loaded = 1
                      OptionsMapped = 1
                      Registered = 1 }

            host.WorkStore.PublishProjectModelWithFiles(available 1L, Set.singleton (AbsFilePath.create source))

            host.SetProjectGraph
                { ProjectGraphAccessor.none with
                    ObserveModel = fun () -> host.WorkSnapshot.ProjectModel
                    ObserveCheckableFiles = fun () -> host.WorkSnapshot.ProjectModelFiles
                    GetAllProjects = fun () -> [ project ]
                    GetCanonicalDllPath = fun path -> graph.GetCanonicalDllPath(AbsProjectPath.create path) }

            let tests =
                FsHotWatch.TestPrune.TestPrunePlugin.create
                    ":memory:"
                    root
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
                    None
                    None
                    None
                    None
                    []

            host.RegisterHandler tests
            let build = BuildPlugin.create "sh" buildScript [] graph [] None [] (Some 5)

            if transition = "cached-failure" then
                // A failed legacy cache entry carries diagnostics, not launch proof.
                // Fresh outputs make this exercise cache admission, not freshness bypass.
                let output = graph.GetCanonicalDllPath(AbsProjectPath.create project) |> Option.get

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName output)
                |> ignore

                System.IO.File.WriteAllText(output, "fixture assembly")

                let key =
                    build.CacheKey.Value build.Init (FileChanged(SourceChanged [ source ]))
                    |> Option.get

                cache.Set
                    { Plugin = "build"; File = None }
                    key
                    { CacheKey = key
                      Errors = [ "<build>", [ FsHotWatch.ErrorLedger.ErrorEntry.error "cached build failure" ] ]
                      Status =
                        FsHotWatch.TaskCache.CachedRunFailed(
                            "cached build failure",
                            RunVerdict.create "cached build failure" TimeSpan.Zero
                        )
                      EmittedEvents =
                        [ FsHotWatch.TaskCache.CachedBuildCompleted(BuildFailed [ "cached build failure" ]) ] }

            host.RegisterHandler build
            host.EmitFileChanged(SourceChanged [ source ])

            waitUntil
                (fun () ->
                    not host.WorkSnapshot.IsBusy
                    && (host.GetAllStatuses()
                        |> Map.exists (fun name status ->
                            name = "build"
                            && match status with
                               | Failed _ -> true
                               | _ -> false)))
                5000

            Assert.True(
                System.IO.File.Exists buildStarted,
                "a cached failure must execute a current build before owning failure authority"
            )
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
            | "new-project" -> System.IO.File.AppendAllText(project, "<!-- changed build input -->")
            | "deleted-source" -> System.IO.File.Delete source
            | "unreadable-source" ->
                System.IO.File.Delete source
                System.IO.Directory.CreateDirectory source |> ignore
            | "ignored-event" ->
                let previous = host.WorkSnapshot.CompletedEvents
                host.EmitFileChanged SolutionChanged

                waitUntil
                    (fun () -> not host.WorkSnapshot.IsBusy && host.WorkSnapshot.CompletedEvents > previous)
                    5000
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
                  WaitForAllTerminal =
                    fun timeout ->
                        FsHotWatch.Daemon.waitForVerdict host timeout System.Threading.CancellationToken.None
                  RerunPlugin = fun _ -> async.Return(Ok())
                  InvalidateCache = fun () -> System.Threading.Tasks.Task.FromResult(())
                  GetUncheckedCount = fun () -> 0 }

            let target = FsHotWatch.Ipc.DaemonRpcTarget rpcConfig

            if List.contains transition [ "current"; "ignored-event"; "cached-failure"; "external-wire-inputs" ] then
                let wire = target.WaitForComplete(1000).GetAwaiter().GetResult()
                Assert.Contains("current-build-refusal", wire)
                Assert.Contains("failed", wire)
                let diagnostics = target.GetDiagnostics("")
                Assert.True(FsHotWatch.Cli.IpcParsing.hasCurrentCompletedFailure root diagnostics)

                for field in [ "schema"; "owner"; "reason"; "inputTreeHash" ] do
                    let malformed = System.Text.Json.Nodes.JsonNode.Parse diagnostics
                    malformed.["completedFailures"].[0].[field] <- System.Text.Json.Nodes.JsonValue.Create("")
                    Assert.False(FsHotWatch.Cli.IpcParsing.hasCurrentCompletedFailure root (malformed.ToJsonString()))

                for generation in
                    [ System.Text.Json.Nodes.JsonValue.Create(2L) :> System.Text.Json.Nodes.JsonNode
                      System.Text.Json.Nodes.JsonValue.Create("1") :> System.Text.Json.Nodes.JsonNode ] do
                    let malformed = System.Text.Json.Nodes.JsonNode.Parse diagnostics
                    malformed.["completedFailures"].[0].["modelGeneration"] <- generation
                    Assert.False(FsHotWatch.Cli.IpcParsing.hasCurrentCompletedFailure root (malformed.ToJsonString()))
                // Drive the public CLI publication path from real RPC diagnostics and
                // the actual no-run test-scope response. Neither command may start tests.
                for mode in
                    [ FsHotWatch.Cli.CheckVerdict.InnerLoop
                      FsHotWatch.Cli.CheckVerdict.Confirmation ] do
                    let readRun () =
                        host.RunCommand(FsHotWatch.Cli.IpcParsing.TestScopeCommand, [||])
                        |> Async.RunSynchronously
                        |> Option.get
                        |> FsHotWatch.Cli.IpcParsing.parseTestRunReport

                    let exitCode =
                        FsHotWatch.Cli.IpcOutput.pollAndRender
                            FsHotWatch.Cli.ProgressRenderer.Agent
                            mode
                            root
                            []
                            (fun _ -> [])
                            false
                            (fun () -> "idle")
                            (fun () -> target.WaitForComplete(1000).GetAwaiter().GetResult())
                            target.GetStatus
                            (fun () -> target.GetDiagnostics(""))
                            readRun
                            (fun () -> FsHotWatch.Cli.IpcParsing.ReachUnavailable "no tests ran")
                            (fun () -> failwith "a current failed build must not request test execution")
                            (fun () -> failwith "a current failed build must not rescan")

                    Assert.Equal(1, exitCode)

                    match FsHotWatch.Cli.Verdict.read root with
                    | FsHotWatch.Cli.Verdict.Reading.Found verdict ->
                        Assert.Equal(1, verdict.ExitCode)
                        Assert.True(verdict.RunId.IsNone)
                        Assert.False(FsHotWatch.Cli.IpcParsing.TestScope.isFullSuite verdict.Scope)
                    | reading -> failwithf "expected a published explicit failed verdict, got %A" reading

                if transition = "current" || transition = "external-wire-inputs" then
                    // Serialization is not the consumer boundary. An edit after this
                    // real diagnostics response must revoke its failure authority too.
                    let original = System.IO.File.ReadAllText source

                    try
                        System.IO.File.WriteAllText(source, "module Source\nlet value = 3\n")
                        Assert.False(FsHotWatch.Cli.IpcParsing.hasCurrentCompletedFailure root diagnostics)
                    finally
                        System.IO.File.WriteAllText(source, original)
            else
                // An earlier red is not authority to settle a different model/tree.
                Assert.Throws<TimeoutException>(fun () ->
                    target.WaitForComplete(1000).GetAwaiter().GetResult() |> ignore)
                |> ignore

            Assert.Empty host.WorkSnapshot.Evidence
            Assert.False(System.IO.File.Exists testStarted)))

[<Fact(Timeout = 15000)>]
let ``a real successful successor retires failure proof and remains cacheable without earning test evidence`` () =
    withTempDir "failed-build-successor" (fun root ->
        let source = System.IO.Path.Combine(root, "Source.fs")
        let project = System.IO.Path.Combine(root, "Manual.fsproj")
        let script = System.IO.Path.Combine(root, "build.sh")
        let recover = System.IO.Path.Combine(root, "recover")
        let calls = System.IO.Path.Combine(root, "calls")
        let output = System.IO.Path.Combine(root, "bin", "Debug", "net10.0", "Manual.dll")
        System.IO.File.WriteAllText(source, "module Source\nlet value = 1\n")
        writeMinimalFsproj project "net10.0" [ "Source.fs" ]

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName output)
        |> ignore

        System.IO.File.WriteAllText(
            script,
            $"echo run >> '{calls}'\nif test -f '{recover}'; then echo assembly > '{output}'; exit 0; fi\necho '{source}(1,1): error FS0001: actual failure'\nexit 1\n"
        )

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(project) |> ignore
        graph.RegisterProjectOutput(AbsProjectPath.create project, output)

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)

        let model =
            FsHotWatch.ProjectModel.ofCompleted
                1L
                { Discovered = 1
                  Loaded = 1
                  OptionsMapped = 1
                  Registered = 1 }

        host.WorkStore.PublishProjectModelWithFiles(model, Set.singleton (AbsFilePath.create source))

        host.SetProjectGraph
            { ProjectGraphAccessor.none with
                ObserveModel = fun () -> host.WorkSnapshot.ProjectModel }

        host.RegisterHandler(BuildPlugin.create "sh" script [] graph [] None [] (Some 5))
        host.EmitFileChanged(SourceChanged [ source ])
        waitUntil (fun () -> not host.WorkSnapshot.IsBusy && not host.WorkSnapshot.CompletedFailures.IsEmpty) 5000

        FsHotWatch.Daemon.waitForVerdict host (TimeSpan.FromSeconds 1.) System.Threading.CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

        System.IO.File.WriteAllText(recover, "enabled")
        host.EmitFileChanged(SourceChanged [ source ])

        waitUntil
            (fun () ->
                not host.WorkSnapshot.IsBusy
                && (match host.GetStatus "build" with
                    | Some(Completed _) -> true
                    | _ -> false))
            5000

        Assert.Equal(2, System.IO.File.ReadAllLines(calls).Length)
        Assert.Empty host.WorkSnapshot.CompletedFailures
        Assert.Empty host.WorkSnapshot.Evidence

        Assert.Throws<TimeoutException>(fun () ->
            FsHotWatch.Daemon.waitForVerdict
                host
                (TimeSpan.FromMilliseconds 100.)
                System.Threading.CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult())
        |> ignore

        host.EmitFileChanged(SourceChanged [ source ])

        waitUntil
            (fun () ->
                not host.WorkSnapshot.IsBusy
                && (match host.GetStatus "build" with
                    | Some(Completed(_, verdict)) -> verdict.Summary.Contains "(cached)"
                    | _ -> false))
            5000

        Assert.Equal(2, System.IO.File.ReadAllLines(calls).Length)
        Assert.Empty host.WorkSnapshot.CompletedFailures)
