[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.FileCheckedDeliveryTests

// FS0057: `TransparentCompiler.CacheSizes` is marked experimental in FCS 43.*.
#nowarn "57"

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers
open Ionide.ProjInfo

/// Evaluates to the same projects every time.
type private FixedWorkspaceLoader(results: Types.ProjectOptions list) =
    let notifications = Event<Types.WorkspaceProjectState>()

    interface IWorkspaceLoader with
        member _.LoadProjects(_projectPaths) = results :> seq<_>
        member _.LoadProjects(_projectPaths, _customProperties, _binaryLog) = results :> seq<_>
        member _.LoadSln(_solutionPath) = results :> seq<_>
        member _.LoadSln(_solutionPath, _customProperties, _binaryLog) = results :> seq<_>

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

let private loadedProject (projectPath: string) (sources: string list) (references: string list) =
    { ProjectId = None
      ProjectFileName = projectPath
      TargetFramework = "net10.0"
      SourceFiles = sources
      OtherOptions = []
      ReferencedProjects =
        [ for reference in references ->
              { RelativePath = reference
                ProjectFileName = reference
                TargetFramework = "net10.0" }
              : Types.ProjectReference ]
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
    : Types.ProjectOptions

/// One `FileChecked` a plugin received: which cohort delivered it, for which file, and
/// how many compiler errors the result carries (-1 when type checking did not finish).
type private Delivery =
    { Phase: string
      File: string
      Errors: int }

/// Two projects: `Lib` compiles Early.fs, Middle.fs, Late.fs in that order, each using
/// the one before it; `App` references `Lib` and uses Late.
type private Fixture =
    { Early: string
      Middle: string
      Late: string
      Use: string
      Daemon: Daemon.Daemon
      Deliver: FileChangeKind -> unit
      Deliveries: unit -> Delivery list
      SetPhase: string -> unit
      Seals: unit -> BatchChecked list }

let private withFixture (name: string) (body: Fixture -> unit) =
    withTempDir name (fun root ->
        let checker = Daemon.createChecker ()

        let projectDir name =
            let dir = Path.Combine(root, "src", name)
            Directory.CreateDirectory dir |> ignore
            let project = Path.Combine(dir, name + ".fsproj")
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />")
            // A restore newer than its project file, so the deps gate lets FCS check.
            let assets = FsHotWatch.DepsFreshness.assetsPath project
            Directory.CreateDirectory(Path.GetDirectoryName assets) |> ignore
            File.WriteAllText(assets, "{}")
            File.SetLastWriteTimeUtc(assets, File.GetLastWriteTimeUtc(project).AddSeconds 1.0)
            dir, project

        let libDir, libProject = projectDir "Lib"
        let appDir, appProject = projectDir "App"

        let source dir (file: string) (text: string) =
            let path = Path.Combine(dir, file)
            File.WriteAllText(path, text)
            path

        let early = source libDir "Early.fs" "module Early\nlet value = 1\n"

        let middle =
            source libDir "Middle.fs" "module Middle\nlet doubled = Early.value * 2\n"

        let late = source libDir "Late.fs" "module Late\nlet tripled = Middle.doubled * 3\n"

        let usage =
            source appDir "Use.fs" "module Use\nlet total : int = Late.tripled + 1\n"

        // Framework references and flags, borrowed from a script's options.
        let baseOptions, _ =
            let script = source root "Base.fsx" "let placeholder = 0\n"

            checker.GetProjectOptionsFromScript(
                script,
                SourceText.ofString (File.ReadAllText script),
                assumeDotNetFramework = false
            )
            |> Async.RunSynchronously

        let frameworkOptions =
            baseOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))

        let fcsProject (project: string) (sources: string list) (refs: (string * FSharpProjectOptions) list) =
            { baseOptions with
                ProjectFileName = project
                SourceFiles = Array.ofList sources
                OtherOptions = Array.append frameworkOptions [| for out, _ in refs -> $"-r:%s{out}" |]
                ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
                UseScriptResolutionRules = false
                ProjectId = None
                Stamp = None }

        let libOptions = fcsProject libProject [ early; middle; late ] []

        let appOptions =
            fcsProject appProject [ usage ] [ Path.ChangeExtension(libProject, ".dll"), libOptions ]

        let loader =
            FixedWorkspaceLoader(
                [ loadedProject libProject [ early; middle; late ] []
                  loadedProject appProject [ usage ] [ libProject ] ]
            )

        let callback = ref None

        let watcher: Daemon.WatcherFactory =
            fun _ onChange _ _ _ ->
                callback.Value <- Some onChange

                { Mode = FsHotWatch.Watcher.WatcherMode.NativeEvents
                  Disposables = [] }

        use daemon =
            Daemon.createWithWorkspaceLoaderAndWatcher
                checker
                root
                { watchingDaemonOptions with
                    CacheBackend = Some(FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(1000)) }
                loader
                (fun _ -> [ libOptions; appOptions ])
                watcher

        let phase = ref "cold scan"
        let deliveries = ConcurrentQueue<Delivery>()
        let seals = ConcurrentQueue<BatchChecked>()

        daemon.RegisterHandler
            { Name = PluginName.create "delivery-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | FileChecked result ->
                            deliveries.Enqueue
                                { Phase = Volatile.Read(&phase.contents)
                                  File = Path.GetFileName(AbsFilePath.value result.File)
                                  Errors =
                                    match result.CheckResults with
                                    | FullCheck r ->
                                        r.Diagnostics
                                        |> Array.filter (fun d ->
                                            d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
                                        |> Array.length
                                    | ParseOnly -> -1 }
                        | BatchChecked batch -> seals.Enqueue batch
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeFileChecked; SubscribeBatchChecked ]
              CacheKey = None
              PrepareCommit = None
              Teardown = None }

        body
            { Early = early
              Middle = middle
              Late = late
              Use = usage
              Daemon = daemon
              Deliver = fun change -> (callback.Value |> Option.get) change
              Deliveries = fun () -> deliveries.ToArray() |> List.ofArray
              SetPhase = fun name -> Volatile.Write(&phase.contents, name)
              Seals = fun () -> seals.ToArray() |> List.ofArray })

let private settle (fixture: Fixture) =
    Assert.True(
        SpinWait.SpinUntil((fun () -> not fixture.Daemon.Host.WorkSnapshot.IsBusy), TimeSpan.FromSeconds 60.0),
        "the daemon's dispatch must retire"
    )

/// Write `text` to `file`, deliver the watcher's change batch for it, and return what
/// that batch alone delivered — no scan runs.
let private changeBatchAfter (fixture: Fixture) (label: string) (file: string) (text: string) =
    fixture.SetPhase label
    let sealsBefore = fixture.Seals().Length
    File.WriteAllText(file, text)
    fixture.Deliver(SourceChanged [ file ])

    Assert.True(
        SpinWait.SpinUntil((fun () -> fixture.Seals().Length > sealsBefore), TimeSpan.FromSeconds 60.0),
        $"%s{label}: the change batch must seal"
    )

    settle fixture

    fixture.Deliveries()
    |> List.filter (fun d -> d.Phase = label)
    |> List.map (fun d -> d.File, d.Errors)
    |> Map.ofList

[<Fact(Timeout = 300000)>]
let ``a change batch re-checks the later files of the edited file's own project`` () =
    // F# types a file against every file before it in its project, so changing Early's
    // type breaks Middle and Late in the same project as surely as Use in a dependent
    // one. The watcher's batch alone must report all three, not leave Middle and Late
    // green until the next scan.
    withFixture "batch-later-files" (fun fixture ->
        fixture.Daemon.ScanAll() |> Async.RunSynchronously
        settle fixture

        // Positive control: the cold scan delivered every file clean.
        let cold =
            fixture.Deliveries() |> List.map (fun d -> d.File, d.Errors) |> Map.ofList

        Assert.Equal<Map<string, int>>(Map.ofList [ "Early.fs", 0; "Middle.fs", 0; "Late.fs", 0; "Use.fs", 0 ], cold)

        let retyped =
            changeBatchAfter fixture "signature edit" fixture.Early "module Early\nlet value = 2L\n"

        Assert.Equal<Set<string>>(
            set [ "Early.fs"; "Middle.fs"; "Late.fs"; "Use.fs" ],
            retyped |> Map.keys |> Set.ofSeq
        )

        Assert.Equal(0, retyped["Early.fs"])
        Assert.True(retyped["Middle.fs"] > 0, $"Middle must be red after the batch: %A{retyped}")
        Assert.True(retyped["Late.fs"] > 0, $"Late must be red after the batch: %A{retyped}")
        Assert.True(retyped["Use.fs"] > 0, $"Use must be red after the batch: %A{retyped}"))

[<Fact(Timeout = 300000)>]
let ``a change batch re-checks only what follows the edited file`` () =
    // Files before the edit cannot see it: an edit to Late re-checks Late and its
    // dependent project, never Early or Middle, and an edit to the last file of a
    // project nothing references re-checks that file alone.
    withFixture "batch-later-files-only" (fun fixture ->
        fixture.Daemon.ScanAll() |> Async.RunSynchronously
        settle fixture

        let lateEdit =
            changeBatchAfter fixture "late edit" fixture.Late "module Late\nlet tripled = Middle.doubled * 4\n"

        Assert.Equal<Set<string>>(set [ "Late.fs"; "Use.fs" ], lateEdit |> Map.keys |> Set.ofSeq)

        let lastFileEdit =
            changeBatchAfter fixture "last-file edit" fixture.Use "module Use\nlet total = 1\n"

        Assert.Equal<Set<string>>(set [ "Use.fs" ], lastFileEdit |> Map.keys |> Set.ofSeq))

[<Fact>]
let ``laterFilesInOwnProjects follows every project that compiles the file, skipping generated files`` () =
    let a = AbsProjectPath.create "/r/A/A.fsproj"
    let b = AbsProjectPath.create "/r/B/B.fsproj"
    let shared = AbsFilePath.create "/r/Shared.fs"
    let generated = "/r/A/obj/Debug/A.AssemblyInfo.fs"

    let sources =
        Map.ofList
            [ a, [ "/r/A/First.fs"; "/r/Shared.fs"; generated; "/r/A/Last.fs" ]
              b, [ "/r/Shared.fs"; "/r/B/Only.fs" ] ]

    let projectsOf (file: AbsFilePath) = if file = shared then [ a; b ] else []

    let later changed =
        laterFilesInOwnProjects (fun p -> sources[p]) projectsOf changed

    Assert.Equal<string list>([ "/r/A/Last.fs"; "/r/B/Only.fs" ], later [ shared ])
    // A file its project no longer lists has no later files.
    Assert.Empty(later [ AbsFilePath.create "/r/Gone.fs" ])
