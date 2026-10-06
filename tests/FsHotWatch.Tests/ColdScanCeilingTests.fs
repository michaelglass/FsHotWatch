[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ColdScanCeilingTests

// What a cold scan may cost, pinned per scenario across five scan generations.
//
// A workspace with a parse error or missing build output must reach its red result
// once, without cancelling and re-checking the same cohort or relaunching the same
// failing build. Each scenario runs five scans over one daemon and asserts, per
// generation, the exact number of FCS check starts, retry rounds, model loads and
// build launches, and the build's terminal status. A ceiling that moves is a behaviour
// change to explain, not a number to bump.

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Build
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers
open Ionide.ProjInfo

/// Every scenario's xUnit cap. Each generation waits at most twice on a real FCS check
/// (`RealCheckWaitMs`): five generations sit at 300 s, under this cap.
[<Literal>]
let private CeilingTestCapMs = 360_000

/// Scans per scenario: the generations the ceilings must hold across.
let private generations = 5

/// Counts the workspace evaluations: one is the cold discovery, any more is a model
/// replacement.
type private CountingLoader(results: Types.ProjectOptions list) =
    let notifications = Event<Types.WorkspaceProjectState>()
    let loads = ref 0

    member _.Loads = Volatile.Read(&loads.contents)

    member private _.Load() =
        Interlocked.Increment(&loads.contents) |> ignore
        results :> seq<_>

    interface IWorkspaceLoader with
        member this.LoadProjects(_projectPaths) = this.Load()
        member this.LoadProjects(_projectPaths, _customProperties, _binaryLog) = this.Load()
        member this.LoadSln(_solutionPath) = this.Load()
        member this.LoadSln(_solutionPath, _customProperties, _binaryLog) = this.Load()

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

/// What the build command does once it has recorded its launch.
type BuildBehaviour =
    /// Writes the project's output and exits 0.
    | WritesOutput
    /// Exits 0 without writing the output: the build "succeeded" and left nothing.
    | LeavesOutputMissing
    /// Reports a compile error and exits 1.
    | FailsToCompile

/// What one generation cost, read after the scan and everything it started settled.
type GenerationCost =
    {
        CheckStarts: int64
        RetryRounds: int
        FilesChecked: int
        FilesUnchecked: int
        /// Workspace evaluations so far, cumulative.
        Loads: int
        /// Build launches so far, cumulative.
        BuildLaunches: int
        /// `completed` or `failed`: the build's terminal status after the generation.
        Build: string
    }

type Scenario =
    {
        Name: string
        /// File name and source of each file of the one project, in compile order.
        Sources: (string * string) list
        Build: BuildBehaviour
        /// The edit, if any, landing after each scan captured its cohort and before it
        /// checked: the file and its new source for that 1-based generation.
        NewerEdit: (string * (int -> string)) option
    }

type Outcome =
    {
        Costs: GenerationCost list
        /// The source of the last FileChecked each generation published for the edited
        /// file, when the scenario edits one.
        LastEditedSources: string list
    }

let private loadedProject (projectPath: string) (sources: string list) (targetPath: string) : Types.ProjectOptions =
    { ProjectId = None
      ProjectFileName = projectPath
      TargetFramework = "net10.0"
      SourceFiles = sources
      OtherOptions = []
      ReferencedProjects = []
      PackageReferences = []
      LoadTime = DateTime.UtcNow
      TargetPath = targetPath
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

let private buildScript (behaviour: BuildBehaviour) (launches: string) (output: string) =
    let record = $"printf 'x\\n' >> '%s{launches}'\n"

    match behaviour with
    | WritesOutput ->
        record
        + $"mkdir -p '%s{Path.GetDirectoryName output}'\nprintf 'dll' > '%s{output}'\n"
    | LeavesOutputMissing -> record
    | FailsToCompile ->
        record
        + "echo 'B.fs(2,9): error FS0010: Incomplete structured construct'\nexit 1\n"

let private statusName (status: PluginStatus option) =
    match status with
    | Some(Completed _) -> "completed"
    | Some(Failed _) -> "failed"
    | Some(Running _) -> "running"
    | Some Idle -> "idle"
    | None -> "unregistered"

let private runGenerations (scenario: Scenario) : Outcome =
    withTempDir $"cold-scan-ceiling-%s{scenario.Name}" (fun root ->
        let srcDir = Path.Combine(root, "src")
        Directory.CreateDirectory srcDir |> ignore
        let projectPath = Path.Combine(srcDir, "Ceiling.fsproj")
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let sources =
            scenario.Sources
            |> List.map (fun (name, text) ->
                let path = Path.Combine(srcDir, name)
                File.WriteAllText(path, text)
                path)

        // A restore newer than its project file: the deps gate finds it fresh and runs
        // nothing.
        let assets = FsHotWatch.DepsFreshness.assetsPath projectPath
        Directory.CreateDirectory(Path.GetDirectoryName assets) |> ignore
        File.WriteAllText(assets, "{}")
        File.SetLastWriteTimeUtc(projectPath, DateTime.UtcNow.AddHours -1.0)

        let output = Path.Combine(srcDir, "bin", "Ceiling.dll")
        let launches = Path.Combine(root, "build-launches")
        let script = Path.Combine(root, "build.sh")
        File.WriteAllText(script, buildScript scenario.Build launches output)

        let checker = sharedChecker.Value

        let fcsOptions =
            let first = List.head sources

            let scriptOptions, _ =
                checker.GetProjectOptionsFromScript(
                    first,
                    FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText first),
                    assumeDotNetFramework = false
                )
                |> Async.RunSynchronously

            { scriptOptions with
                ProjectFileName = projectPath
                SourceFiles = Array.ofList sources }

        let loader = CountingLoader([ loadedProject projectPath sources output ])
        let restores = ref 0
        let deliver = ref None

        // A watching daemon whose only file events are the ones this test delivers.
        let watcher: Daemon.WatcherFactory =
            fun _ onChange _ _ _ ->
                deliver.Value <- Some onChange

                { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
                  Disposables = [] }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                root
                { watchingDaemonOptions with
                    Restore =
                        fun _ _ ->
                            Interlocked.Increment(&restores.contents) |> ignore
                            ProcessHelper.Failed(1, ProcessHelper.ProcessOutput.Drained "no restore runs here") }
                loader
                (fun _ -> [ fcsOptions ])
                watcher

        let published = Collections.Concurrent.ConcurrentQueue<string * string>()

        daemon.RegisterHandler
            { Name = PluginName.create "ceiling-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | FileChecked result -> published.Enqueue(AbsFilePath.value result.File, result.Source)
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        daemon.RegisterHandler(BuildPlugin.create "sh" script [] daemon.Graph [] None [] None)

        // The newer edit: the scan has captured its cohort (preprocessors run after the
        // capture, before any check), and the edit's own change batch checks the file
        // before the scan reaches it. Armed once per generation, so the batch's own
        // preprocessor pass goes straight through.
        let armed = ref 0
        let editLanded = ref true
        let lastBusy = ref []

        match scenario.NewerEdit with
        | None -> ()
        | Some(name, sourceFor) ->
            let edited = Path.Combine(srcDir, name)

            daemon.RegisterPreprocessor(
                { new FsHotWatch.Plugin.IFsHotWatchPreprocessor with
                    member _.Name = "newer-edit"

                    member _.Process files _ =
                        let generation = Interlocked.Exchange(&armed.contents, 0)

                        if generation > 0 then
                            let newer = sourceFor generation
                            File.WriteAllText(edited, newer)
                            (deliver.Value |> Option.get) (SourceChanged [ edited ])

                            // Checked, and everything the edit started (its batch, its
                            // build) settled: only this scan and this pass are at work. Left
                            // racing, the scan's build request can land while the edit's
                            // build is finishing and launch a second build of the same
                            // tree, which would make the build count depend on timing.
                            let settled () =
                                lastBusy.Value <- daemon.Host.BusyPluginNames()

                                published |> Seq.exists (fun (file, text) -> file = edited && text = newer)
                                && not (List.contains "changes" lastBusy.Value)
                                && not (List.contains "build" lastBusy.Value)

                            let landed = waitUntilTrue settled RealCheckWaitMs

                            if not landed then
                                editLanded.Value <- false

                        Ok
                            { Modified = []
                              Considered = files.Length
                              Evidence = "newer edit delivered" }

                    member _.Dispose() = () }
            )

        let mutable costs = []
        let mutable lastEdited = []

        for generation in 1..generations do
            armed.Value <- generation
            published.Clear()
            daemon.ScanAll() |> Async.RunSynchronously

            Assert.True(
                waitUntilTrue (fun () -> not (daemon.Host.AnyPluginBusy())) RealCheckWaitMs,
                $"generation %d{generation}: the work the scan started never settled"
            )

            Assert.True(
                editLanded.Value,
                $"generation %d{generation}: the newer edit was never checked and settled (busy: %A{lastBusy.Value})"
            )

            test
                <@
                    daemon.Host.WorkSnapshot.OperationFaults
                    |> List.forall (fun (name, _) -> name <> "scan")
                @>

            // One record per completed scan: no scan ran that this loop did not ask for.
            let series =
                FsHotWatch.ScanMetrics.readSeries (FsHotWatch.ScanMetrics.recordPath root)

            test <@ series.Length = generation @>
            let sample = List.last series

            costs <-
                costs
                @ [ { CheckStarts = sample.CheckStarts
                      RetryRounds = sample.RetryRounds
                      FilesChecked = sample.FilesChecked
                      FilesUnchecked = sample.FilesUnchecked
                      Loads = loader.Loads
                      BuildLaunches =
                        if File.Exists launches then
                            File.ReadAllLines(launches).Length
                        else
                            0
                      Build = statusName (daemon.Host.GetStatus "build") } ]

            match scenario.NewerEdit with
            | Some(name, _) ->
                let edited = Path.Combine(srcDir, name)

                lastEdited <-
                    lastEdited
                    @ [ published
                        |> Seq.filter (fun (file, _) -> file = edited)
                        |> Seq.map snd
                        |> Seq.last ]
            | None -> ()

        // The restore is fresh throughout: no scenario spends a restore.
        test <@ restores.Value = 0 @>

        { Costs = costs
          LastEditedSources = lastEdited })

/// The cost of a scenario whose every generation checks the same: `starts` FCS check
/// starts, no retry round, every file checked, one model load in total, and
/// `launchesAfter generation` build launches by the end of that generation.
let private steadyCosts (starts: int64) (files: int) (launchesAfter: int -> int) (build: string) =
    [ for generation in 1..generations ->
          { CheckStarts = starts
            RetryRounds = 0
            FilesChecked = files
            FilesUnchecked = 0
            Loads = 1
            BuildLaunches = launchesAfter generation
            Build = build } ]

let private healthySources =
    [ "A.fs", "module A\nlet a = 1\n"
      "B.fs", "module B\nlet b = A.a + 1\n"
      "C.fs", "module C\nlet c = B.b + 1\n" ]

[<Fact(Timeout = CeilingTestCapMs)>]
let ``a healthy cold scan starts each file once per generation and builds once in five`` () =
    let outcome =
        runGenerations
            { Name = "healthy"
              Sources = healthySources
              Build = WritesOutput
              NewerEdit = None }

    // Later generations replay the first build: its output is still on disk and its
    // inputs have not moved.
    test <@ outcome.Costs = steadyCosts 3L 3 (fun _ -> 1) "completed" @>

[<Fact(Timeout = CeilingTestCapMs)>]
let ``a cold scan with missing build output reaches one red build per scan and no extra checks`` () =
    let outcome =
        runGenerations
            { Name = "missing-output"
              Sources = healthySources
              Build = LeavesOutputMissing
              NewerEdit = None }

    // A stored success is never replayed over a missing output, so each scan builds
    // again: once, to one red result, with no rebuild and no rescan behind it.
    test <@ outcome.Costs = steadyCosts 3L 3 id "failed" @>

[<Fact(Timeout = CeilingTestCapMs)>]
let ``a cold scan over a parse error checks the broken file once and does not loop its failing build`` () =
    let outcome =
        runGenerations
            { Name = "parse-error"
              Sources =
                [ "A.fs", "module A\nlet a = 1\n"
                  "B.fs", "module B\nlet b =\n"
                  "C.fs", "module C\nlet c = 3\n" ]
              Build = FailsToCompile
              NewerEdit = None }

    // The broken file is checked once like the others: a parse error is an answer, not
    // a cancelled check to retry. The failing build runs once per scan.
    test <@ outcome.Costs = steadyCosts 3L 3 id "failed" @>

[<Fact(Timeout = CeilingTestCapMs)>]
let ``a newer edit mid-scan costs its own checks and one build, and the scan publishes the newer source`` () =
    let editedSource generation =
        $"module B\nlet b = A.a + %d{generation}\n"

    let outcome =
        runGenerations
            { Name = "newer-edit"
              Sources = healthySources
              Build = WritesOutput
              NewerEdit = Some("B.fs", editedSource) }

    // The edit's change batch checks B and its dependent C, and the scan then checks
    // every file once: two starts more than the cohort, and no retry. The edit's build is
    // the generation's only one; the scan's own build request replays it.
    test <@ outcome.Costs = steadyCosts 5L 3 id "completed" @>

    // Newer-edit ordering: the last result the scan published for B is the edit's.
    test <@ outcome.LastEditedSources = [ for generation in 1..generations -> editedSource generation ] @>
