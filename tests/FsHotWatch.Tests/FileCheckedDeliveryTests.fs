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
    {
        Root: string
        /// Lib's output, which App's `-r:` names. The fixture does not create it.
        LibOutput: string
        Early: string
        Middle: string
        Late: string
        Use: string
        Daemon: Daemon.Daemon
        Deliver: FileChangeKind -> unit
        Deliveries: unit -> Delivery list
        SetPhase: string -> unit
        Seals: unit -> BatchChecked list
    }

/// `appFlags` are compiler flags App is checked with beyond the framework's.
let private withFixtureOf
    (checkCache: FsHotWatch.InMemoryCheckCache.CheckCacheSetup)
    (appFlags: string list)
    (name: string)
    (body: Fixture -> unit)
    =
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

        let fcsProject
            (project: string)
            (sources: string list)
            (refs: (string * FSharpProjectOptions) list)
            (flags: string list)
            =
            { baseOptions with
                ProjectFileName = project
                SourceFiles = Array.ofList sources
                OtherOptions =
                    Array.concat
                        [ frameworkOptions
                          [| for out, _ in refs -> $"-r:%s{out}" |]
                          Array.ofList flags ]
                ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
                UseScriptResolutionRules = false
                ProjectId = None
                Stamp = None }

        let libOptions = fcsProject libProject [ early; middle; late ] [] []

        let appOptions =
            fcsProject appProject [ usage ] [ Path.ChangeExtension(libProject, ".dll"), libOptions ] appFlags

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
                    CheckCache = checkCache }
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
            { Root = root
              LibOutput = Path.ChangeExtension(libProject, ".dll")
              Early = early
              Middle = middle
              Late = late
              Use = usage
              Daemon = daemon
              Deliver = fun change -> (callback.Value |> Option.get) change
              Deliveries = fun () -> deliveries.ToArray() |> List.ofArray
              SetPhase = fun name -> Volatile.Write(&phase.contents, name)
              Seals = fun () -> seals.ToArray() |> List.ofArray })

let private withFixtureCaching checkCache = withFixtureOf checkCache []

let private inMemoryCache () =
    FsHotWatch.InMemoryCheckCache.CheckCacheSetup.On(FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(1000), None, None)

let private withFixture name body =
    withFixtureCaching (inMemoryCache ()) name body

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
    // Files before the edit cannot see it: a signature edit to Late re-checks Late and
    // its dependent project, never Early or Middle, and an edit to the last file of a
    // project nothing references re-checks that file alone.
    withFixture "batch-later-files-only" (fun fixture ->
        fixture.Daemon.ScanAll() |> Async.RunSynchronously
        settle fixture

        let lateEdit =
            changeBatchAfter fixture "late edit" fixture.Late "module Late\nlet tripled = int64 Middle.doubled * 3L\n"

        Assert.Equal<Set<string>>(set [ "Late.fs"; "Use.fs" ], lateEdit |> Map.keys |> Set.ofSeq)

        let lastFileEdit =
            changeBatchAfter fixture "last-file edit" fixture.Use "module Use\nlet total = 1\n"

        Assert.Equal<Set<string>>(set [ "Use.fs" ], lastFileEdit |> Map.keys |> Set.ofSeq))

/// Lib's output, at `year`: older than every source, like a build that predates the edits.
let private writeLibOutput (fixture: Fixture) (year: int) (bytes: byte[]) =
    File.WriteAllBytes(fixture.LibOutput, bytes)
    File.SetLastWriteTimeUtc(fixture.LibOutput, DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc))

/// Scan the whole tree and return the scan's record.
let private rescan (fixture: Fixture) =
    fixture.Daemon.ScanAll() |> Async.RunSynchronously
    settle fixture

    FsHotWatch.ScanMetrics.readSeries (FsHotWatch.ScanMetrics.recordPath fixture.Root)
    |> List.last

/// The FCS checks the daemon has started so far.
let private starts (fixture: Fixture) = fixture.Daemon.Pipeline.CheckStarts

/// `changeBatchAfter`, returning the files the batch delivered and the FCS checks it started.
let private deliveredAndStarted (fixture: Fixture) (label: string) (file: string) (text: string) =
    let before = starts fixture
    let delivered = changeBatchAfter fixture label file text |> Map.keys |> Set.ofSeq
    delivered, starts fixture - before

/// Early, with a declaration of each kind a later file can observe.
let private earlyApi =
    String.concat
        "\n"
        [ "module Early"
          "type internal Hidden = { H: int }"
          "type Shape = | Circle of float"
          "[<System.Obsolete(\"a\")>]"
          "let old = 0"
          "[<Literal>]"
          "let Limit = 1"
          "let value = 1"
          "" ]

/// `earlyApi` with `before` replaced by `after`.
let private earlyWith (before: string) (after: string) =
    Assert.Contains(before, earlyApi)
    earlyApi.Replace(before, after)

[<Fact(Timeout = 300000)>]
let ``a signature change to an early file re-checks every file after it and every dependent project`` () =
    // Each edit changes something Middle, Late or Use could type-check against, so each
    // batch must re-check all of them. A signature hash that missed one of these edits
    // would leave them standing, and this fact red.
    withFixture "batch-signature-changes" (fun fixture ->
        fixture.Daemon.ScanAll() |> Async.RunSynchronously
        settle fixture

        let everything = set [ "Early.fs"; "Middle.fs"; "Late.fs"; "Use.fs" ]

        let edits =
            [ "new declarations", earlyApi
              "internal member type", earlyWith "{ H: int }" "{ H: int64 }"
              "attribute argument", earlyWith "Obsolete(\"a\")" "Obsolete(\"b\")"
              "literal value", earlyWith "let Limit = 1" "let Limit = 2"
              "union case", earlyWith "| Circle of float" "| Circle of float | Square of float"
              "inline member", earlyApi + "let inline twice (x: int) = x + x\n"
              "inline body", earlyApi + "let inline twice (x: int) = x * 2\n"
              // Content stands in for a file with an inline member, so even a comment moves it.
              "comment beside an inline member", earlyApi + "// note\nlet inline twice (x: int) = x * 2\n" ]

        for label, text in edits do
            let delivered, _ = deliveredAndStarted fixture label fixture.Early text
            Assert.True((delivered = everything), $"%s{label}: the batch delivered %A{delivered}"))

[<Fact(Timeout = 300000)>]
let ``a comment, whitespace or body edit to an early file re-checks that file alone`` () =
    // Nothing after Early can tell these edits apart: its declarations are unchanged. The
    // edited file is still checked (its own diagnostics may move), and nothing else is,
    // in the batch or in the scan that follows.
    withFixture "batch-signature-unchanged" (fun fixture ->
        fixture.Daemon.ScanAll() |> Async.RunSynchronously
        settle fixture

        // Positive control: a declaration edit cascades, so the cuts below are not a
        // batch that never reaches past the edited file.
        let declared, _ = deliveredAndStarted fixture "declarations" fixture.Early earlyApi
        Assert.Equal<Set<string>>(set [ "Early.fs"; "Middle.fs"; "Late.fs"; "Use.fs" ], declared)

        let edits =
            [ "comment", "// a comment moves every declaration down a line\n" + earlyApi
              "whitespace", earlyApi.Replace("let value = 1", "let value =   1\n\n")
              "body", earlyApi.Replace("let old = 0", "let old = 0 + 0") ]

        for label, text in edits do
            let delivered, started = deliveredAndStarted fixture label fixture.Early text
            Assert.True((delivered = set [ "Early.fs" ]), $"%s{label}: the batch delivered %A{delivered}")
            Assert.True((started = 1L), $"%s{label}: the batch started %d{started} checks")

        let before = starts fixture
        let sample = rescan fixture
        Assert.Equal(0L, starts fixture - before)
        Assert.Equal(4L, sample.CacheHits))

[<Fact(Timeout = 300000)>]
let ``a signature change in a referenced project re-checks its dependents, and a body edit does not`` () =
    withFixture "batch-referenced-signature" (fun fixture ->
        fixture.Daemon.ScanAll() |> Async.RunSynchronously
        settle fixture

        let body, _ =
            deliveredAndStarted fixture "late body" fixture.Late "module Late\nlet tripled = Middle.doubled * 4\n"

        Assert.Equal<Set<string>>(set [ "Late.fs" ], body)

        let retyped =
            changeBatchAfter fixture "late signature" fixture.Late "module Late\nlet tripled = float Middle.doubled\n"

        Assert.Equal<Set<string>>(set [ "Late.fs"; "Use.fs" ], retyped |> Map.keys |> Set.ofSeq)
        Assert.True(retyped["Use.fs"] > 0, $"Use must be red after the batch: %A{retyped}"))

[<Fact(Timeout = 300000)>]
let ``a #nowarn file a change batch re-checks leaves no refusal in the analysis receipt or the ledger`` () =
    // App is built with --warnaserror+ and Use.fs silences FS0057 with #nowarn, so FCS
    // reports the experimental use at Error severity anyway (dotnet/fsharp#9796). What
    // reaches the receipt must be filtered exactly as what reaches the ledger is,
    // whichever cohort delivered the result.
    withFixtureOf (inMemoryCache ()) [ "--warnaserror+" ] "batch-nowarn-receipt" (fun fixture ->
        File.WriteAllText(fixture.Late, "module Late\n[<Experimental(\"try\")>]\nlet tripled = Middle.doubled * 3\n")

        File.WriteAllText(fixture.Use, "#nowarn \"57\"\nmodule Use\nlet total : int = Late.tripled + 1\n")

        fixture.Daemon.RegisterHandler(
            FsHotWatch.TestPrune.TestPrunePlugin.create
                (Path.Combine(fixture.Root, "test-impact.db"))
                fixture.Root
                None
                None
                None
                None
                None
                []
        )

        fixture.Daemon.ScanAll() |> Async.RunSynchronously
        settle fixture

        // Only a receipt that covers the file can vouch for it: an absent one would
        // hold no refusal either.
        let refusalsFor (file: string) =
            let covering =
                fixture.Daemon.Host.WorkSnapshot.AnalysisEvidence
                |> List.filter (fun receipt -> receipt.CheckedFiles.Contains(AbsFilePath.create file))

            Assert.NotEmpty covering

            covering
            |> List.collect (fun receipt -> receipt.FailureReasons)
            |> List.filter (fun reason -> reason.StartsWith(file: string))

        let ledgerFor (file: string) =
            fixture.Daemon.Host.GetErrorsByPlugin(PluginActivity.FcsPluginName)
            |> Map.tryFind file
            |> Option.defaultValue []

        Assert.Empty(refusalsFor fixture.Use)
        Assert.Empty(ledgerFor fixture.Use)

        // A signature change to Late: the batch re-checks Use as a dependent.
        let batch =
            changeBatchAfter
                fixture
                "late signature"
                fixture.Late
                "module Late\n[<Experimental(\"try\")>]\nlet tripled = Middle.doubled * 3\nlet extra = 0\n"

        // Positive control: the batch delivered Use, and FCS really did report the
        // suppressed code at Error severity in it.
        Assert.True(batch.ContainsKey "Use.fs", $"the batch must re-check Use: %A{batch}")
        Assert.True(batch["Use.fs"] > 0, $"Use must carry the #nowarn-suppressed error: %A{batch}")

        Assert.Empty(refusalsFor fixture.Use)
        Assert.Empty(ledgerFor fixture.Use))

[<Fact(Timeout = 300000)>]
let ``a rescan starts no check unless a file's own inputs moved`` () =
    // An unchanged rescan is answered from the check-result cache, file by file, so the
    // checker is asked nothing. A no-op rebuild that rewrites a referenced output with
    // its own bytes is still unchanged. The last rescan is the positive control: the
    // counter reads a check where one runs, so its zeros are not a counter that never
    // moves.
    withFixture "rescan-starts-nothing" (fun fixture ->
        writeLibOutput fixture 2020 [| 1uy; 2uy; 3uy |]

        let startsAndHits () =
            let sample = rescan fixture
            sample.CheckStarts, sample.CacheHits

        let coldStarts, _ = startsAndHits ()
        Assert.Equal(4L, coldStarts)

        Assert.Equal((0L, 4L), startsAndHits ())

        writeLibOutput fixture 2021 [| 1uy; 2uy; 3uy |]
        Assert.Equal((0L, 4L), startsAndHits ())

        // Only Use.fs types against Lib's output.
        writeLibOutput fixture 2021 [| 4uy; 5uy; 6uy |]
        Assert.Equal((1L, 3L), startsAndHits ()))

[<Fact(Timeout = 300000)>]
let ``with no check-result cache a rescan after a byte-identical rebuild imports nothing again`` () =
    // Without the cache every rescan asks the checker for every file, and what keeps that
    // cheap is the checker's own state: a project whose inputs are unchanged keeps its
    // import of its references. An in-repository reference is stamped by its bytes, so an
    // output a no-op rebuild rewrote with its own bytes leaves App's import standing. New
    // bytes are the positive control: App imports Lib's output again.
    withFixtureCaching FsHotWatch.InMemoryCheckCache.CheckCacheSetup.Off "rescan-imports-nothing" (fun fixture ->
        writeLibOutput fixture 2020 [| 1uy; 2uy; 3uy |]
        rescan fixture |> ignore
        let warm = rescan fixture
        Assert.Equal(4L, warm.CheckStarts)

        writeLibOutput fixture 2021 [| 1uy; 2uy; 3uy |]
        let sameBytes = rescan fixture
        Assert.Equal(warm.FcsBootstrapStarts, sameBytes.FcsBootstrapStarts)

        writeLibOutput fixture 2021 [| 4uy; 5uy; 6uy |]
        let newBytes = rescan fixture
        Assert.True(newBytes.FcsBootstrapStarts > sameBytes.FcsBootstrapStarts, $"%A{newBytes}"))

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
