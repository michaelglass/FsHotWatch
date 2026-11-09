/// A daemon that has not yet begun its first discovery: the seconds between it starting
/// to serve and its startup scan reaching discovery, during which a client can already
/// have asked for a run.
module FsHotWatch.Tests.ColdDaemonDiscoveryTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.ProjectModel
open FsHotWatch.PluginFramework
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open Ionide.ProjInfo

/// A workspace of one fixed project, evaluated at once.
type private OneProjectLoader(project: Types.ProjectOptions) =
    let notifications = Event<Types.WorkspaceProjectState>()

    interface IWorkspaceLoader with
        member _.LoadProjects(_projectPaths) = [ project ] :> seq<_>
        member _.LoadProjects(_projectPaths, _customProperties, _binaryLog) = [ project ] :> seq<_>
        member _.LoadSln(_solutionPath) = [ project ] :> seq<_>
        member _.LoadSln(_solutionPath, _customProperties, _binaryLog) = [ project ] :> seq<_>

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

let private loadedProject (projectPath: string) : Types.ProjectOptions =
    { ProjectId = None
      ProjectFileName = projectPath
      TargetFramework = "net10.0"
      SourceFiles = []
      OtherOptions = []
      ReferencedProjects = []
      PackageReferences = []
      LoadTime = DateTime.UtcNow
      TargetPath = Path.ChangeExtension(projectPath, ".dll")
      TargetRefPath = None
      ProjectOutputType = Types.ProjectOutputType.Library
      ProjectSdkInfo =
        { IsTestProject = false
          Configuration = "Debug"
          IsPackable = false
          TargetFramework = "net10.0"
          TargetFrameworkIdentifier = ".NETCoreApp"
          TargetFrameworkVersion = "v10.0"
          MSBuildAllProjects = []
          MSBuildToolsVersion = "Current"
          ProjectAssetsFile = ""
          RestoreSuccess = true
          Configurations = [ "Debug" ]
          TargetFrameworks = [ "net10.0" ]
          RunArguments = None
          RunCommand = None
          IsPublishable = None }
      Items = []
      Properties = []
      CustomProperties = []
      AllProperties = Map.empty
      AllItems = Map.empty
      Analyzers = [] }

/// A project whose test command signals it started, then waits to be released.
let private heldProject (started: string) (release: string) : TestConfig =
    { Project = "ProjA"
      Command = "sh"
      Args = $"-c \"touch '%s{started}'; while [ ! -f '%s{release}' ]; do sleep 0.05; done; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = Disabled
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

let private isAvailable (observation: Observation) =
    match observation with
    | Observation.Available _ -> true
    | Observation.Unobserved
    | Observation.Rediscovering _
    | Observation.Unavailable _ -> false

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

/// The window a real daemon opens: it serves before its startup scan begins, and the scan
/// restores before it discovers. The serve callback is that window, so what it asks for
/// is what a client connecting first would have asked for.
[<Fact(Timeout = 60000)>]
let ``a run asked for before the first discovery has begun waits for the model and is not revoked`` () =
    withTempDir "cold-discovery" (fun repoRoot ->
        let srcDir = Path.Combine(repoRoot, "src")
        Directory.CreateDirectory(Path.Combine(srcDir, "obj")) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "obj", "project.assets.json"), "{}")
        File.WriteAllText(Path.Combine(srcDir, "Value.fs"), "module Value\nlet answer = 1\n")
        let projectPath = Path.Combine(srcDir, "Probe.fsproj")
        File.WriteAllText(projectPath, "<Project />")
        let started = Path.Combine(repoRoot, "started")
        let release = Path.Combine(repoRoot, "release")
        let lines = Collections.Concurrent.ConcurrentQueue<string>()

        use _sink =
            Logging.installSink
                { Write = lines.Enqueue
                  Level = Logging.LogLevel.Debug
                  File = None }

        let completion () =
            lines |> Seq.tryFind (fun l -> l.Contains " completed: launched under ")

        use daemon =
            Daemon.createWithWorkspaceLoader
                nullChecker
                repoRoot
                oneShotDaemonOptions
                (OneProjectLoader(loadedProject projectPath))
                (fun projects -> projects |> List.map (fun p -> makeProjectOptions p.ProjectFileName [] []))

        daemon.RegisterHandler(
            create ":memory:" repoRoot (Some [ heldProject started release ]) None None None None []
        )

        let serve (_: FsHotWatch.Ipc.DaemonRpcConfig) (_: CancellationTokenSource) =
            async {
                Task.Run(fun () ->
                    daemon.Host.RunCommand("run-tests", [| "{}" |])
                    |> Async.RunSynchronously
                    |> ignore)
                |> ignore

                // Held here, the startup scan has not begun: the launch has either
                // started its tests or said it is waiting.
                waitUntil
                    (fun () ->
                        File.Exists started
                        || lines |> Seq.exists (fun l -> l.Contains "is in flight; waiting"))
                    15000
            }

        use cts = new CancellationTokenSource()

        let running =
            Async.StartAsTask(daemon.RunWith(serve, TimeSpan.FromSeconds 5.0, DateTime.UtcNow, cts))

        try
            waitUntil (fun () -> isAvailable (daemon.ProjectModel())) 20000
            File.WriteAllText(release, "")
            waitUntil (fun () -> (completion ()).IsSome) 20000

            test
                <@
                    completion ()
                    |> Option.exists (fun l -> l.Contains "launched under project model generation 1,")
                @>

            test <@ completion () |> Option.exists (fun l -> l.Contains "receipt earned") @>
        finally
            File.WriteAllText(release, "")
            cts.Cancel()

            try
                running.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            with _ ->
                ())

let private settled: DiscoverySnapshot =
    { Discovered = 1
      Loaded = 1
      OptionsMapped = 1
      Registered = 1 }

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a coordinator that expects a discovery tells a reader to wait before any attempt began`` () =
    let announced = Collections.Concurrent.ConcurrentQueue<Observation>()
    let coordinator = DiscoveryCoordinator(announced.Enqueue)

    coordinator.ExpectFirstDiscovery()

    test
        <@
            match coordinator.Observation with
            | Observation.Rediscovering _ -> true
            | _ -> false
        @>

    test <@ announced.Count = 1 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an expected discovery that arrives ends as its model, with nothing left to wait for`` () =
    let coordinator = DiscoveryCoordinator()
    coordinator.ExpectFirstDiscovery()

    coordinator.Run(fun () -> async { return settled, () })
    |> Async.RunSynchronously

    coordinator.FirstDiscoveryNoLongerExpected()

    test <@ isAvailable coordinator.Observation @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a startup scan that ends without discovering stops the wait`` () =
    let announced = Collections.Concurrent.ConcurrentQueue<Observation>()
    let coordinator = DiscoveryCoordinator(announced.Enqueue)
    coordinator.ExpectFirstDiscovery()

    coordinator.FirstDiscoveryNoLongerExpected()

    test <@ coordinator.Observation = Observation.Unobserved @>
    test <@ announced.ToArray() |> Array.last = Observation.Unobserved @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``expecting a discovery once a model exists changes nothing`` () =
    let coordinator = DiscoveryCoordinator()

    coordinator.Run(fun () -> async { return settled, () })
    |> Async.RunSynchronously

    coordinator.ExpectFirstDiscovery()

    test <@ isAvailable coordinator.Observation @>
