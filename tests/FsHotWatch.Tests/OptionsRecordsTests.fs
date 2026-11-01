/// The persisted project options (ADR-059 §18, AUTOMATION-1372): a daemon that starts
/// again over the same bytes takes its projects' evaluated options from the outcome store
/// instead of evaluating them with MSBuild, and evaluates whenever anything the evaluation
/// read has moved. A stale replay is far worse than a fresh evaluation, so every test that
/// moves one input asserts the evaluation runs again.
///
/// The design-time half (Ionide's build of each project) is a counting stand-in, so no
/// restore is needed; the inputs are listed by a REAL MSBuild evaluation of a real tree
/// (`OptionsInputs.list`), so what the key covers is what MSBuild actually reads.
module FsHotWatch.Tests.OptionsRecordsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Ionide.ProjInfo
open FsHotWatch
open FsHotWatch.OptionsRecords
open FsHotWatch.Tests.TestHelpers

/// One project's evaluated options, every field set, so a round trip that drops one shows.
let private sampleOptions (projectFile: string) (assets: string) : Types.ProjectOptions =
    let dir = Path.GetDirectoryName projectFile

    { ProjectId = Some "app-id"
      ProjectFileName = projectFile
      TargetFramework = "net10.0"
      SourceFiles =
        [ Path.Combine(dir, "obj", "Debug", "net10.0", "App.AssemblyInfo.fs")
          Path.Combine(dir, "Lib.fs") ]
      OtherOptions = [ "--define:DEBUG"; "-r:/sdk/ref/System.Runtime.dll" ]
      ReferencedProjects =
        [ { RelativePath = "../Other/Other.fsproj"
            ProjectFileName = Path.Combine(dir, "..", "Other", "Other.fsproj")
            TargetFramework = "net10.0" } ]
      PackageReferences =
        [ { Name = "FSharp.Core"
            Version = "10.0.100"
            FullPath = "/packages/fsharp.core/10.0.100" } ]
      LoadTime = DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc)
      TargetPath = Path.Combine(dir, "bin", "App.dll")
      TargetRefPath = Some(Path.Combine(dir, "obj", "ref", "App.dll"))
      ProjectOutputType = Types.ProjectOutputType.Custom "WinExe"
      ProjectSdkInfo =
        { IsTestProject = false
          Configuration = "Debug"
          IsPackable = true
          TargetFramework = "net10.0"
          TargetFrameworkIdentifier = ".NETCoreApp"
          TargetFrameworkVersion = "v10.0"
          MSBuildAllProjects = [ projectFile ]
          MSBuildToolsVersion = "Current"
          ProjectAssetsFile = assets
          RestoreSuccess = true
          Configurations = [ "Debug"; "Release" ]
          TargetFrameworks = [ "net10.0" ]
          RunArguments = Some "exec App.dll"
          RunCommand = Some "dotnet"
          IsPublishable = Some false }
      Items =
        [ Types.ProjectItem.Compile("Lib.fs", Path.Combine(dir, "Lib.fs"), Some(Map.ofList [ "Link", "Lib.fs" ]))
          Types.ProjectItem.Compile("Gen.fs", Path.Combine(dir, "Gen.fs"), None) ]
      Properties =
        [ { Name = "OutputType"
            Value = "Library" } ]
      CustomProperties = [ { Name = "Custom"; Value = "1" } ]
      AllProperties = Map.empty
      AllItems = Map.empty
      Analyzers =
        [ { PropertyName = "PkgIonide_Analyzers"
            NugetPackageRoot = "/packages/ionide.analyzers/0.14.7"
            DllRootPath = "/packages/ionide.analyzers/0.14.7/analyzers/dotnet/fs" } ] }

/// The design-time half of an evaluation, counted. `during` runs inside each load, as a
/// write that lands while MSBuild is still evaluating would.
type private CountingLoader(answer: unit -> Types.ProjectOptions list, during: unit -> unit, failure: string option) =
    let notifications = Event<Types.WorkspaceProjectState>()
    let mutable loads = 0

    member _.Loads = loads

    member private _.Load(paths: string list) : seq<Types.ProjectOptions> =
        loads <- loads + 1

        for path in paths do
            notifications.Trigger(Types.WorkspaceProjectState.Loading path)

        during ()

        match failure with
        | Some project ->
            // Ionide reports a project it could not load and returns the ones it could.
            notifications.Trigger(Types.WorkspaceProjectState.Failed(project, Types.ProjectNotRestored project))
        | None -> ()

        answer () |> Seq.ofList

    interface IWorkspaceLoader with
        member this.LoadProjects(paths) = this.Load paths
        member this.LoadProjects(paths, _customProperties, _binaryLog) = this.Load paths
        member this.LoadSln(_sln) = this.Load []
        member this.LoadSln(_sln, _customProperties, _binaryLog) = this.Load []

        [<CLIEvent>]
        member _.Notifications = notifications.Publish

/// A repository with one SDK project. `Directory.Build.props` imports a nested props
/// file, a wildcard of props files, and a props file only while it exists, and reads the
/// `FSHW_OPTIONS_KNOB` environment variable as a property.
type private Tree =
    { Root: string
      Project: string
      Common: string
      Assets: string
      Store: OutcomeStore.Store }

    member t.ProjectDir = Path.GetDirectoryName t.Project

    member t.Path([<ParamArray>] parts: string array) =
        Path.Combine(Array.append [| t.Root |] parts)

let private writeFile (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private withTree (body: Tree -> unit) =
    withTempDir "options-records" (fun root ->
        // MSBuild is located the way the daemon locates it, before anything evaluates.
        Daemon.Daemon.msbuildWorkspaceLoader root |> ignore

        writeFile
            (Path.Combine(root, "Directory.Build.props"))
            """<Project>
  <Import Project="$(MSBuildThisFileDirectory)build/common.props" />
  <Import Project="$(MSBuildThisFileDirectory)extra/*.props" />
  <Import Project="$(MSBuildThisFileDirectory)maybe.props" Condition="Exists('$(MSBuildThisFileDirectory)maybe.props')" />
  <PropertyGroup><Knob>$(FSHW_OPTIONS_KNOB)</Knob></PropertyGroup>
</Project>
"""

        let common = Path.Combine(root, "build", "common.props")
        writeFile common "<Project><PropertyGroup><Common>1</Common></PropertyGroup></Project>\n"
        Directory.CreateDirectory(Path.Combine(root, "extra")) |> ignore
        let project = Path.Combine(root, "src", "App", "App.fsproj")

        writeFile
            project
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>
"""

        writeFile (Path.Combine(root, "src", "App", "Lib.fs")) "module Lib\n"
        let assets = Path.Combine(root, "src", "App", "obj", "project.assets.json")
        writeFile assets """{"version":3,"restore":1}"""

        writeFile
            (Path.Combine(root, "src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs"))
            "module AssemblyInfo\n"

        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(root, ".store")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        body
            { Root = root
              Project = project
              Common = common
              Assets = assets
              Store = store })

let private request (t: Tree) : Request =
    { Root = t.Root
      Projects = [ t.Project ]
      Toolchain = "fshw-test-toolchain"
      Sdk = "sdk-10.0.400"
      GlobalProperties = [ "Configuration", "Debug" ] }

/// Change times as the filer reads them, for paths inside `t` only. A test tree is written
/// moments before it is evaluated, so the racy window is zero here, and the temporary
/// directory above it is shared with every test running beside it, whose writes would
/// refuse every filing. The write-during-evaluation test pins the check inside the tree.
let private inTree (t: Tree) (path: string) : FileStamp.Stamp voption =
    if path.StartsWith(t.Root, StringComparison.Ordinal) then
        FileStamp.read path
    else
        ValueNone

/// Returns once the file system's clock has reached the wall-clock time this was called at.
/// Linux stamps a change with the kernel's COARSE clock, up to a tick (1–10 ms) behind
/// `DateTime.UtcNow`, so a write made just after an evaluation started can carry a change
/// time from just before it; with the racy window zero here, the filer would not see it.
/// The daemon's own window (`RacyWindow`, 2 s) covers that tick. The probe is written
/// beside the tree, never in it: a write in the tree would be refused for its own sake.
let private untilFileClockReachesNow () =
    let now = DateTime.UtcNow

    let probe =
        Path.Combine(Path.GetTempPath(), "fshw-file-clock-" + Guid.NewGuid().ToString("N"))

    let deadline = DateTime.UtcNow.AddSeconds 5.0

    try
        let rec wait () =
            File.WriteAllText(probe, "")

            match FileStamp.read probe with
            | ValueSome s when s.ChangedAt < now && DateTime.UtcNow < deadline -> wait ()
            | _ -> ()

        wait ()
    finally
        File.Delete probe

/// One daemon's loader over `t`: a fresh process, as far as the store can tell.
let private daemonLoader
    (t: Tree)
    (req: Request)
    (environment: (string * string) list)
    (during: unit -> unit)
    (failure: string option)
    =
    let inner =
        CountingLoader((fun () -> [ sampleOptions t.Project t.Assets ]), during, failure)

    let loader =
        CachingLoader(
            inner,
            OptionsInputs.list,
            t.Store,
            (fun _ -> req),
            (fun () -> environment),
            ignore,
            racyWindow = TimeSpan.Zero,
            stamp = inTree t
        )

    inner, (loader :> IWorkspaceLoader)

/// Evaluate once in one daemon, then start another; how many evaluations did the second run?
let private restartAfter (t: Tree) (change: unit -> unit) (second: Request) (environment: (string * string) list) =
    let _, first = daemonLoader t (request t) [ "FSHW_OPTIONS_KNOB", "a" ] ignore None
    first.LoadProjects [ t.Project ] |> Seq.toList |> ignore
    change ()
    let counted, restarted = daemonLoader t second environment ignore None
    let options = restarted.LoadProjects [ t.Project ] |> Seq.toList
    counted.Loads, options

let private sameEnvironment = [ "FSHW_OPTIONS_KNOB", "a" ]

[<Fact(Timeout = 120000)>]
let ``a restart whose every keyed input is byte-identical replays the options without evaluating`` () =
    withTree (fun t ->
        let loads, options = restartAfter t ignore (request t) sameEnvironment
        test <@ loads = 0 @>
        let expected = sampleOptions t.Project t.Assets
        test <@ options.Length = 1 @>
        // Everything but the load time, which a replay sets to its own, as an evaluation does.
        test
            <@
                { options.Head with
                    LoadTime = expected.LoadTime } = expected
            @>

        test <@ options.Head.LoadTime > expected.LoadTime @>)

[<Fact(Timeout = 120000)>]
let ``an import that changes is evaluated again`` () =
    withTree (fun t ->
        let loads, _ =
            restartAfter
                t
                (fun () ->
                    File.WriteAllText(
                        t.Common,
                        "<Project><PropertyGroup><Common>2</Common></PropertyGroup></Project>\n"
                    ))
                (request t)
                sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``an import changed and changed back is byte-identical again and replays`` () =
    withTree (fun t ->
        let original = File.ReadAllText t.Common

        let loads, _ =
            restartAfter
                t
                (fun () ->
                    File.WriteAllText(t.Common, "<Project />\n")
                    File.WriteAllText(t.Common, original))
                (request t)
                sameEnvironment

        test <@ loads = 0 @>)

[<Fact(Timeout = 120000)>]
let ``the project file changing is evaluated again`` () =
    withTree (fun t ->
        let loads, _ =
            restartAfter t (fun () -> File.AppendAllText(t.Project, "<!-- edited -->\n")) (request t) sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``an SDK, toolchain or global property change is evaluated again`` () =
    withTree (fun t ->
        let req = request t

        for changed in
            [ { req with Sdk = "sdk-10.0.401" }
              { req with Toolchain = "another-build" }
              { req with
                  GlobalProperties = [ "Configuration", "Release" ] } ] do
            let loads, _ = restartAfter t ignore changed sameEnvironment
            test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a restore that changes the assets is evaluated again`` () =
    withTree (fun t ->
        let loads, _ =
            restartAfter
                t
                (fun () -> File.WriteAllText(t.Assets, """{"version":3,"restore":2}"""))
                (request t)
                sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a generated compile item a clean removed is evaluated again, which regenerates it`` () =
    withTree (fun t ->
        let generated =
            t.Path("src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs")

        let loads, _ =
            restartAfter t (fun () -> File.Delete generated) (request t) sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a file an Exists condition looked for that appears is evaluated again`` () =
    withTree (fun t ->
        let loads, _ =
            restartAfter t (fun () -> writeFile (t.Path "maybe.props") "<Project />\n") (request t) sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a Directory.Build.props that appears nearer the project is evaluated again`` () =
    withTree (fun t ->
        let loads, _ =
            restartAfter
                t
                (fun () -> writeFile (t.Path("src", "Directory.Build.props")) "<Project />\n")
                (request t)
                sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a file a wildcard import now matches is evaluated again`` () =
    withTree (fun t ->
        let loads, _ =
            restartAfter
                t
                (fun () -> writeFile (t.Path("extra", "late.props")) "<Project />\n")
                (request t)
                sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a resource the SDK's default embedded-resource glob now matches is evaluated again`` () =
    withTree (fun t ->
        let loads, _ =
            restartAfter
                t
                (fun () -> writeFile (t.Path("src", "App", "Strings.resx")) "<root />\n")
                (request t)
                sameEnvironment

        test <@ loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a variable the evaluation reads as a property is keyed, and one it never reads is not`` () =
    withTree (fun t ->
        let moved, _ = restartAfter t ignore (request t) [ "FSHW_OPTIONS_KNOB", "b" ]
        test <@ moved = 1 @>

        let unset, _ = restartAfter t ignore (request t) []
        test <@ unset = 1 @>

        let unrelated, _ =
            restartAfter t ignore (request t) [ "FSHW_OPTIONS_KNOB", "a"; "FSHW_NEVER_READ", "x" ]

        test <@ unrelated = 0 @>

        let prefixed, _ =
            restartAfter t ignore (request t) [ "FSHW_OPTIONS_KNOB", "a"; "DOTNET_SOMETHING", "x" ]

        test <@ prefixed = 1 @>)

// AUTOMATION-1438. Measured on intelligence: a daemon the gate starts through the detached
// launcher runs with DOTNET_INTERNAL_ThreadSuspendInjection=0, which the launcher sets for
// the daemon's own runtime, and a plainly started one runs without it. Every `DOTNET_*`
// variable was keyed, so options filed in one start mode missed in the other ("inputs
// moved: env:DOTNET_INTERNAL_ThreadSuspendInjection"). A runtime-internal knob configures
// the process hosting MSBuild, never what MSBuild evaluates; one a project file names is
// still read, and still keyed.
[<Fact(Timeout = 120000)>]
let ``options filed by a plainly started daemon replay in one the detached launcher started, and back`` () =
    withTree (fun t ->
        let launched = sameEnvironment @ [ "DOTNET_INTERNAL_ThreadSuspendInjection", "0" ]

        let plainToLaunched, _ = restartAfter t ignore (request t) launched
        test <@ plainToLaunched = 0 @>

        let _, gate = daemonLoader t (request t) launched ignore None
        gate.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        let counted, plain = daemonLoader t (request t) sameEnvironment ignore None
        plain.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ counted.Loads = 0 @>)

[<Fact>]
let ``a runtime-internal variable is keyed only where the evaluation's files name it`` () =
    let environment =
        [ "DOTNET_INTERNAL_ThreadSuspendInjection", "0"; "DOTNET_ROOT", "/sdk" ]

    test <@ environmentInputs [] environment |> List.map fst = [ "env:DOTNET_ROOT" ] @>

    test
        <@
            environmentInputs [ "DOTNET_INTERNAL_ThreadSuspendInjection" ] environment
            |> List.map fst = [ "env:DOTNET_INTERNAL_ThreadSuspendInjection"; "env:DOTNET_ROOT" ]
        @>

[<Fact(Timeout = 120000)>]
let ``an input written while the projects were evaluated is never filed`` () =
    withTree (fun t ->
        let _, first =
            daemonLoader
                t
                (request t)
                sameEnvironment
                (fun () ->
                    untilFileClockReachesNow ()

                    File.WriteAllText(
                        t.Common,
                        "<Project><PropertyGroup><Common>3</Common></PropertyGroup></Project>\n"
                    ))
                None

        first.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None
        restarted.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ counted.Loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``an input removed while the projects were evaluated is never filed with the evaluation that read it`` () =
    withTree (fun t ->
        // Read while it existed, absent when the inputs are listed: the absence would be
        // filed under a key the options were not evaluated from, so its directory decides.
        // The path is not there, so the projects are evaluated once more (AUTOMATION-1438),
        // and that evaluation, which found it absent, is the one filed.
        let maybe = t.Path "maybe.props"
        writeFile maybe "<Project />\n"
        let evaluations = ref 0

        let inner =
            CountingLoader(
                (fun () ->
                    evaluations.Value <- evaluations.Value + 1

                    [ { sampleOptions t.Project t.Assets with
                          ProjectId = Some $"evaluation-%d{evaluations.Value}" } ]),
                (fun () ->
                    if File.Exists maybe then
                        untilFileClockReachesNow ()
                        File.Delete maybe),
                None
            )

        let first =
            CachingLoader(
                inner,
                OptionsInputs.list,
                t.Store,
                (fun _ -> request t),
                (fun () -> sameEnvironment),
                ignore,
                racyWindow = TimeSpan.Zero,
                stamp = inTree t
            )
            :> IWorkspaceLoader

        first.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ inner.Loads = 2 @>
        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None
        let replayed = restarted.LoadProjects [ t.Project ] |> Seq.toList
        test <@ counted.Loads = 0 @>
        test <@ replayed |> List.map _.ProjectId = [ Some "evaluation-2" ] @>)

/// The design-time build of a checkout that was never built, as Ionide's runs it: it
/// writes the project's generated compile item and creates `bin/` beside the project, but
/// only what is not there yet. A second design-time build over the same bytes writes
/// nothing, which is what MSBuild's incremental targets do.
let private firstDesignTimeBuild (t: Tree) () =
    let generated =
        t.Path("src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs")

    let bin = Path.Combine(t.ProjectDir, "bin", "Debug")

    if not (File.Exists generated && Directory.Exists bin) then
        untilFileClockReachesNow ()
        writeFile generated "module AssemblyInfo\n"
        Directory.CreateDirectory bin |> ignore

// AUTOMATION-1438. Measured on intelligence: the gate's first daemon in a fresh workspace
// evaluated while Ionide's design-time build wrote every project's obj/ AssemblyInfo and
// created bin/, so its filing was refused ("...AssemblyInfo.fs changed while the projects
// were evaluated"), and the next plain start found nothing filed and evaluated for 10.1 s.
// The start mode was never the variable: the key was the same, the record was never
// written. An evaluation the design-time build changed is evaluated once more, and that
// one, which changed nothing, is filed.
[<Fact(Timeout = 120000)>]
let ``an evaluation whose own design-time build wrote its inputs is filed from a second one, and the next start replays it``
    ()
    =
    withTree (fun t ->
        File.Delete(t.Path("src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs"))

        let cold, first =
            daemonLoader t (request t) sameEnvironment (firstDesignTimeBuild t) None

        first.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ cold.Loads = 2 @>
        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None
        let options = restarted.LoadProjects [ t.Project ] |> Seq.toList
        test <@ counted.Loads = 0 @>
        test <@ options.Length = 1 @>)

// The same, under a racy window: the first design-time build's last writes land just before
// the second evaluation starts, inside the window a filing is judged from. Measured on this
// repository's own tree: the second evaluation was refused for the first one's last
// AssemblyInfo. The second evaluation starts only once the window has passed those writes.
[<WallClock("timestamp granularity: the sleep lets the racy window pass the tree the test just wrote, so only the design-time build's writes fall inside it; the assertion is on evaluation counts, never on a duration")>]
[<Fact(Timeout = 120000)>]
let ``the second evaluation starts after the racy window has passed the first one's writes`` () =
    withTree (fun t ->
        File.Delete(t.Path("src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs"))
        // The tree was written just now: let the window pass it, so only the design-time
        // build's writes fall inside it.
        Threading.Thread.Sleep(TimeSpan.FromMilliseconds 600.0)

        let inner =
            CountingLoader((fun () -> [ sampleOptions t.Project t.Assets ]), firstDesignTimeBuild t, None)

        let loader =
            CachingLoader(
                inner,
                OptionsInputs.list,
                t.Store,
                (fun _ -> request t),
                (fun () -> sameEnvironment),
                ignore,
                racyWindow = TimeSpan.FromMilliseconds 500.0,
                stamp = inTree t
            )
            :> IWorkspaceLoader

        loader.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ inner.Loads = 2 @>
        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None
        restarted.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ counted.Loads = 0 @>)

// The first design-time build can write a path only the SECOND evaluation's inputs name,
// and waiting for the window to pass the latest change among the first evaluation's inputs
// leaves that write inside it. Here the first build writes such a path well after its last
// input. The window is judged from
// when the first load returned, after every one of its writes.
[<WallClock("timestamp granularity: the sleep puts a write the first evaluation's inputs do not name a clear interval after those they do; the assertion is on evaluation counts, never on a duration")>]
[<Fact(Timeout = 120000)>]
let ``the second evaluation starts after the racy window has passed every write of the first one`` () =
    withTree (fun t ->
        File.Delete(t.Path("src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs"))
        // In a directory no input of the first evaluation is judged by, so writing it moves none.
        let late = Path.Combine(t.ProjectDir, "obj", "late", "late.props")
        Directory.CreateDirectory(Path.GetDirectoryName late) |> ignore
        Threading.Thread.Sleep(TimeSpan.FromMilliseconds 2100.0)

        let designTimeBuild () =
            if not (File.Exists late) then
                firstDesignTimeBuild t ()
                Threading.Thread.Sleep(TimeSpan.FromMilliseconds 1000.0)
                writeFile late "<Project />\n"

        let inner =
            CountingLoader((fun () -> [ sampleOptions t.Project t.Assets ]), designTimeBuild, None)

        // The second evaluation reads the late file; the first, which wrote it, did not.
        let listings = ref 0

        let listInputs projects =
            listings.Value <- listings.Value + 1
            let inputs = OptionsInputs.list projects

            if listings.Value = 1 then
                inputs
            else
                { inputs with
                    Reads = late :: inputs.Reads }

        let loader =
            CachingLoader(
                inner,
                listInputs,
                t.Store,
                (fun _ -> request t),
                (fun () -> sameEnvironment),
                ignore,
                racyWindow = TimeSpan.FromMilliseconds 2000.0,
                stamp = inTree t
            )
            :> IWorkspaceLoader

        loader.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ inner.Loads = 2 @>
        test <@ t.Store.Lookup(requestKey (request t)) |> Option.isSome @>)

[<Fact(Timeout = 120000)>]
let ``a project directory that changes on every evaluation is evaluated twice at most and never filed`` () =
    withTree (fun t ->
        let n = ref 0

        let churn () =
            n.Value <- n.Value + 1
            untilFileClockReachesNow ()

            writeFile
                (Path.Combine(t.ProjectDir, "obj", "Debug", "net10.0", "App.AssemblyInfo.fs"))
                $"// %d{n.Value}\n"

        let cold, first = daemonLoader t (request t) sameEnvironment churn None
        first.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ cold.Loads = 2 @>
        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None
        restarted.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ counted.Loads = 1 @>)

/// What the first daemon in a checkout writes outside its projects while they are evaluated:
/// test-prune's registration creates `<root>/coverage/<project>/` (ADR-059 §24). The
/// checkout root is the directory the absent `Directory.Build.targets`,
/// `Directory.Packages.props` and `global.json` there are judged by. A later daemon finds
/// the directory there and writes nothing.
let private firstDaemonCreatesCoverage (t: Tree) () =
    let coverage = t.Path("coverage", "App.Tests")

    if not (Directory.Exists coverage) then
        untilFileClockReachesNow ()
        Directory.CreateDirectory coverage |> ignore

// AUTOMATION-1438, measured again at 0.14.0-alpha.134 on intelligence: the gate's daemon, the
// first in a fresh workspace, would have evaluated a second time for the design-time build's
// writes inside the projects, but not once the checkout root moved too. Its test-prune registration
// created `coverage/` 0.26 s before the evaluation started, inside the window, which moved the
// root that the absent root-level `Directory.Packages.props` and `Directory.Build.targets` are
// judged by. That is outside every project, so nothing was filed, and the next plain start
// (which found `coverage/` there and wrote nothing) evaluated for 12.2 s and filed. An absent
// input whose directory moved is evaluated once more too, under the same rule.
[<Fact(Timeout = 120000)>]
let ``the first daemon in a checkout, which writes in the checkout root while its projects are evaluated, files them and the next start replays them``
    ()
    =
    withTree (fun t ->
        File.Delete(t.Path("src", "App", "obj", "Debug", "net10.0", "App.AssemblyInfo.fs"))

        let firstDaemon () =
            firstDesignTimeBuild t ()
            firstDaemonCreatesCoverage t ()

        let cold, gate = daemonLoader t (request t) sameEnvironment firstDaemon None
        gate.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ cold.Loads = 2 @>
        let counted, plain = daemonLoader t (request t) sameEnvironment ignore None
        let options = plain.LoadProjects [ t.Project ] |> Seq.toList
        test <@ counted.Loads = 0 @>
        test <@ options.Length = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a directory an absent input is judged by that moves while a built checkout is evaluated is evaluated once more and filed``
    ()
    =
    withTree (fun t ->
        let cold, first =
            daemonLoader t (request t) sameEnvironment (firstDaemonCreatesCoverage t) None

        first.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ cold.Loads = 2 @>
        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None
        restarted.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ counted.Loads = 0 @>)

// The boundary: a file the evaluation READ outside the projects that changes under it is an
// edit, not a side effect of evaluating. It is not evaluated again here; the edit's own file
// event re-discovers the projects.
[<Fact(Timeout = 120000)>]
let ``a file read outside the projects that changes while they are evaluated is not evaluated again`` () =
    withTree (fun t ->
        let n = ref 0

        let edit () =
            n.Value <- n.Value + 1
            untilFileClockReachesNow ()
            writeFile t.Common $"<Project><PropertyGroup><Common>%d{n.Value}</Common></PropertyGroup></Project>\n"

        let cold, first = daemonLoader t (request t) sameEnvironment edit None
        first.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ cold.Loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``an evaluation in which a project failed to load is never filed`` () =
    withTree (fun t ->
        let other = t.Path("src", "Other", "Other.fsproj")
        let _, first = daemonLoader t (request t) sameEnvironment ignore (Some other)
        // The projects that did load are still answered; they are just not filed.
        test <@ first.LoadProjects [ t.Project ] |> Seq.length = 1 @>
        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None
        restarted.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ counted.Loads = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a binary-log request always evaluates and files nothing`` () =
    withTree (fun t ->
        let counted, first = daemonLoader t (request t) sameEnvironment ignore None
        let binlog = BinaryLogGeneration.Within(DirectoryInfo(t.Path "binlogs"))
        first.LoadProjects([ t.Project ], [], binlog) |> Seq.toList |> ignore
        first.LoadProjects([ t.Project ], [], binlog) |> Seq.toList |> ignore
        test <@ counted.Loads = 2 @>

        let restartedCount, restarted =
            daemonLoader t (request t) sameEnvironment ignore None

        restarted.LoadProjects [ t.Project ] |> Seq.toList |> ignore
        test <@ restartedCount.Loads = 1 @>)

[<Fact>]
let ``evaluated options survive the record format, all but the evaluation's own property and item dumps`` () =
    let options =
        sampleOptions "/repo/src/App/App.fsproj" "/repo/src/App/obj/project.assets.json"

    let withDumps =
        { options with
            AllProperties = Map.ofList [ "Knob", Set.singleton "a" ]
            AllItems = Map.ofList [ "Compile", Set.singleton ("Lib.fs", Map.empty) ] }

    let loadTime = DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc)

    let read =
        ProjectOptionsJson.tryOfJson loadTime (ProjectOptionsJson.toJson [ withDumps ])

    test <@ read = Some [ { options with LoadTime = loadTime } ] @>

    for outputType in [ Types.ProjectOutputType.Library; Types.ProjectOutputType.Exe ] do
        let plain =
            { options with
                ProjectId = None
                TargetRefPath = None
                ProjectOutputType = outputType
                ProjectSdkInfo =
                    { options.ProjectSdkInfo with
                        RunArguments = None
                        RunCommand = None
                        IsPublishable = None } }

        test <@ ProjectOptionsJson.tryOfJson plain.LoadTime (ProjectOptionsJson.toJson [ plain ]) = Some [ plain ] @>

[<Fact>]
let ``options of another format, or not options at all, are a miss`` () =
    let text =
        ProjectOptionsJson.toJson [ sampleOptions "/r/A.fsproj" "/r/obj/project.assets.json" ]

    test
        <@ ProjectOptionsJson.tryOfJson DateTime.UtcNow (text.Replace(OutputsSchema, "fshw-options-outputs-v0")) = None @>

    test <@ ProjectOptionsJson.tryOfJson DateTime.UtcNow "{" = None @>
    test <@ ProjectOptionsJson.tryOfJson DateTime.UtcNow $"""{{"schema":"%s{OutputsSchema}"}}""" = None @>

[<Fact(Timeout = 120000)>]
let ``an evaluation that loaded no project is never filed`` () =
    withTree (fun t ->
        let evaluate () =
            let inner = CountingLoader((fun () -> []), ignore, None)

            let loader =
                CachingLoader(
                    inner,
                    OptionsInputs.list,
                    t.Store,
                    (fun _ -> request t),
                    (fun () -> sameEnvironment),
                    ignore,
                    racyWindow = TimeSpan.Zero
                )
                :> IWorkspaceLoader

            test <@ loader.LoadProjects [ t.Project ] |> Seq.isEmpty @>
            inner.Loads

        test <@ evaluate () = 1 @>
        test <@ evaluate () = 1 @>)

[<Fact(Timeout = 120000)>]
let ``a load with no custom properties and no binary log is the keyed request, and solutions pass through`` () =
    withTree (fun t ->
        let _, first = daemonLoader t (request t) sameEnvironment ignore None

        first.LoadProjects([ t.Project ], [], BinaryLogGeneration.Off)
        |> Seq.toList
        |> ignore

        let counted, restarted = daemonLoader t (request t) sameEnvironment ignore None

        restarted.LoadProjects([ t.Project ], [], BinaryLogGeneration.Off)
        |> Seq.toList
        |> ignore

        test <@ counted.Loads = 0 @>

        restarted.LoadSln(t.Path "App.sln") |> Seq.toList |> ignore

        restarted.LoadSln(t.Path "App.sln", [], BinaryLogGeneration.Off)
        |> Seq.toList
        |> ignore

        test <@ counted.Loads = 2 @>
        let mutable seen = 0
        use _ = restarted.Notifications.Subscribe(fun _ -> seen <- seen + 1)
        restarted.LoadSln(t.Path "App.sln") |> Seq.toList |> ignore
        test <@ seen = 0 @>

        restarted.LoadProjects([ t.Project ], [ "Custom=1" ], BinaryLogGeneration.Off)
        |> Seq.toList
        |> ignore

        test <@ seen = 1 @>)

[<Fact(Timeout = 120000)>]
let ``an evaluation whose inputs cannot be listed is never filed`` () =
    withTree (fun t ->
        let evaluate (listInputs: Types.ProjectOptions list -> EvaluationInputs) =
            let inner =
                CountingLoader((fun () -> [ sampleOptions t.Project t.Assets ]), ignore, None)

            let loader =
                CachingLoader(inner, listInputs, t.Store, (fun _ -> request t), (fun () -> sameEnvironment), ignore)
                :> IWorkspaceLoader

            loader.LoadProjects [ t.Project ] |> Seq.toList |> ignore
            inner.Loads

        test <@ evaluate (fun _ -> failwith "MSBuild could not evaluate it") = 1 @>
        test <@ evaluate (fun _ -> failwith "MSBuild could not evaluate it") = 1 @>)

/// Inputs that need no MSBuild: two files, one probe that is absent, one listing.
let private syntheticInputs (root: string) : EvaluationInputs =
    { Reads = [ Path.Combine(root, "a.props"); Path.Combine(root, "b.props") ]
      Probes = [ Path.Combine(root, "missing", "deeper", "c.props"); "relative.props" ]
      Listings =
        [ { Kind = ListingKind.Files
            Directory = root
            Pattern = "*.props"
            Recursive = false } ]
      EnvNames = [ "Knob" ] }

let private syntheticRequest (root: string) : Request =
    { Root = root
      Projects = [ Path.Combine(root, "A.fsproj") ]
      Toolchain = "t"
      Sdk = "s"
      GlobalProperties = [] }

let private withSynthetic (body: string -> OutcomeStore.Store -> EvaluationInputs -> Request -> unit) =
    withTempDir "options-synthetic" (fun root ->
        File.WriteAllText(Path.Combine(root, "a.props"), "a")
        File.WriteAllText(Path.Combine(root, "b.props"), "b")

        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(root, ".store")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        body root store (syntheticInputs root) (syntheticRequest root))

let private never: string -> FileStamp.Stamp voption = fun _ -> ValueNone
let private projects = [ sampleOptions "/r/A.fsproj" "/r/obj/project.assets.json" ]

[<Fact>]
let ``a lookup names why it misses: nothing filed, unreadable records, gone options, and every moved input`` () =
    withSynthetic (fun root store inputs req ->
        let missed replay =
            match replay with
            | Missed why -> why
            | Replayed _ -> "replayed"

        test <@ missed (tryReplay store req [] DateTime.UtcNow) = "no evaluation of this request is filed" @>
        test <@ file store req inputs projects [ "KNOB", "1" ] DateTime.MinValue never = Filed @>
        test <@ missed (tryReplay store req [ "KNOB", "1" ] DateTime.UtcNow) = "replayed" @>

        // Six inputs moved: the reason names five and counts the rest.
        let moved =
            missed (tryReplay store req [ for i in 1..6 -> $"DOTNET_X%d{i}", "v" ] DateTime.UtcNow)

        test <@ moved.StartsWith "inputs moved: env:DOTNET_X1" && moved.EndsWith " and 2 more" @>

        // The options under the filed key are gone (GC): a miss that says so.
        let key = optionsKey req inputs [ "KNOB", "1" ]
        Directory.Delete(store.DirectoryOf key, true)
        test <@ missed (tryReplay store req [ "KNOB", "1" ] DateTime.UtcNow) = "the filed options are gone" @>

        let record (k: ActionKey.ActionKey) (outputs: string) =
            store.RecordWithOutputs(
                { Key = k
                  Subject = "test"
                  Outcome = OutcomeStore.Outcome.Pass
                  Counts = None
                  DurationMs = None
                  Producer =
                    { Binary = "t"
                      Worktree = root
                      RunId = None }
                  RecordedAt = DateTimeOffset.UtcNow.AddMinutes 1.0 },
                Some outputs
            )
            |> ignore

        record key """{"schema":"fshw-options-outputs-v0"}"""
        test <@ missed (tryReplay store req [ "KNOB", "1" ] DateTime.UtcNow) = "the filed options are unreadable" @>

        record (requestKey req) $"""{{"schema":"%s{InputsSchema}"}}"""
        test <@ missed (tryReplay store req [ "KNOB", "1" ] DateTime.UtcNow) = "the filed inputs are unreadable" @>)

[<Fact>]
let ``nothing is filed into a store that cannot be written`` () =
    withSynthetic (fun root _ inputs req ->
        let blocked = Path.Combine(root, "store-is-a-file")
        File.WriteAllText(blocked, "")

        let store =
            OutcomeStore.Store(
                { Root = blocked
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        test
            <@
                file store req inputs projects [] DateTime.MinValue never = Refused
                    "the outcome store could not be written"
            @>

        // A tree MSBuild listed recursively is not keyed, so its evaluation is never filed.
        let tree =
            { inputs with
                Listings =
                    [ { inputs.Listings.Head with
                          Recursive = true } ] }

        let refused = file store req tree projects [] DateTime.MinValue never
        test <@ refused = Refused $"MSBuild listed the tree under %s{root}, which is not keyed" @>)

[<Fact>]
let ``a listing is keyed by the names it holds, never by their bytes`` () =
    withTempDir "options-listing" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "sub")) |> ignore
        File.WriteAllText(Path.Combine(root, "sub", "a.txt"), "a")
        File.WriteAllText(Path.Combine(root, "b.txt"), "b")

        let state kind recursive =
            listingState
                { Kind = kind
                  Directory = root
                  Pattern = "*"
                  Recursive = recursive }

        let before =
            [ state ListingKind.Files false
              state ListingKind.Directories false
              state ListingKind.Entries false ]

        test <@ before |> List.distinct |> List.length = 3 @>
        // A tree MSBuild listed is never walked here; such an evaluation is not filed.
        test <@ state ListingKind.Files true = "recursive" @>
        File.WriteAllText(Path.Combine(root, "b.txt"), "changed bytes")

        test
            <@
                [ state ListingKind.Files false
                  state ListingKind.Directories false
                  state ListingKind.Entries false ] = before
            @>

        File.WriteAllText(Path.Combine(root, "c.txt"), "c")

        test
            <@
                state ListingKind.Files false <> before[0]
                && state ListingKind.Directories false = before[1]
            @>

        let gone =
            listingState
                { Kind = ListingKind.Files
                  Directory = Path.Combine(root, "nowhere")
                  Pattern = "*"
                  Recursive = false }

        test <@ gone = "absent" @>

        let refused =
            listingState
                { Kind = ListingKind.Files
                  Directory = root
                  Pattern = "\u0000"
                  Recursive = false }

        test <@ refused.StartsWith "error:" @>)

[<Fact(Timeout = 120000)>]
let ``the recording file system answers as the real one and records every question`` () =
    withTempDir "options-recording" (fun root ->
        Daemon.Daemon.msbuildWorkspaceLoader root |> ignore
        let file = Path.Combine(root, "a.props")
        File.WriteAllText(file, "text")
        let fs = OptionsInputs.RecordingFileSystem.Create()

        test <@ fs.ReadFile(file).ReadToEnd() = "text" @>

        do
            use stream = fs.GetFileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)
            test <@ stream.Length = 4L @>

        test <@ fs.ReadFileAllText file = "text" @>
        test <@ fs.ReadFileAllBytes file = "text"B @>
        test <@ fs.EnumerateFiles(root, "*.props", SearchOption.TopDirectoryOnly) |> Seq.length = 1 @>
        test <@ fs.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly) |> Seq.isEmpty @>

        test
            <@
                fs.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
                |> Seq.length = 1
            @>

        test <@ fs.GetAttributes file = File.GetAttributes file @>
        test <@ fs.GetLastWriteTimeUtc file = File.GetLastWriteTimeUtc file @>
        test <@ fs.DirectoryExists root @>
        test <@ not (fs.FileExists "relative.props") @>
        test <@ fs.FileOrDirectoryExists file @>

        test <@ fs.Reads = [ file ] @>
        test <@ Set.ofList fs.Probes = set [ file; root; "relative.props" ] @>

        test
            <@
                fs.Listings |> List.map (fun l -> l.Kind, l.Pattern, l.Recursive) |> Set.ofList = set
                    [ ListingKind.Files, "*.props", false
                      ListingKind.Directories, "*", false
                      ListingKind.Entries, "*", false ]
            @>)

[<Fact(Timeout = 120000)>]
let ``the toolchain names fshw, Ionide and MSBuild, and a file names what it reads from the environment`` () =
    withTempDir "options-toolchain" (fun root ->
        Daemon.Daemon.msbuildWorkspaceLoader root |> ignore
        let toolchain = OptionsInputs.toolchain ()
        test <@ toolchain.Contains "FsHotWatch@" @>
        test <@ toolchain.Contains "Ionide.ProjInfo@" @>
        test <@ toolchain.Contains "Microsoft.Build@" @>)

    let text =
        """<P Condition="'$(Knob)' != ''">$([System.Environment]::GetEnvironmentVariable('HOME_X'))</P>"""

    test <@ OptionsInputs.environmentNamesIn text = [ "Knob"; "HOME_X" ] @>
    test <@ processEnvironment () |> List.exists (fun (name, _) -> name = "PATH") @>

// Seen on Linux CI: the second evaluation of a never-built checkout started 0.7 ms before
// the racy window had passed the first load, and was refused for the first design-time
// build's own write. A wait of a fractional millisecond slept a whole one less.
[<Fact>]
let ``the wait for the racy window ends only once the clock is past it`` () =
    let start = DateTime(2026, 11, 1, 6, 47, 15, DateTimeKind.Utc)
    let clock = ref start
    let deadline = start.AddTicks 7_000L // 0.7 ms

    waitPast (fun () -> clock.Value) (fun ms -> clock.Value <- clock.Value.AddMilliseconds(float ms)) deadline

    test <@ clock.Value > deadline @>

[<Fact>]
let ``a listing's kind keys under its token, and an index naming any other kind is unreadable`` () =
    withSynthetic (fun root store inputs req ->
        // The label a files listing keys under: the token, then the depth, then the path.
        let key = optionsKey req inputs []
        let label = "listing:files:*:" + Path.Combine(root, "*.props")
        test <@ key.Inputs |> List.exists (fun (l, _) -> l = label) @>

        test <@ file store req inputs projects [] DateTime.MinValue never = Filed @>

        let filed = (store.LookupWithOutputs(requestKey req)).Value |> snd |> Option.get

        store.RecordWithOutputs(
            { (store.Lookup(requestKey req)).Value with
                RecordedAt = DateTimeOffset.UtcNow.AddMinutes 1.0 },
            Some(filed.Replace("\"kind\":\"files\"", "\"kind\":\"sockets\""))
        )
        |> ignore

        test
            <@
                match tryReplay store req [] DateTime.UtcNow with
                | Missed why -> why = "the filed inputs are unreadable"
                | Replayed _ -> false
            @>)
