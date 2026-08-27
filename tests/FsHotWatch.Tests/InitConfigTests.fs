module FsHotWatch.Tests.InitConfigTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.InitConfig
open FsHotWatch.Tests.TestHelpers

// --- classifyProject ---

[<Fact(Timeout = 15000)>]
let ``classifyProject identifies test project in tests directory`` () =
    let result = classifyProject "tests/MyApp.Tests/MyApp.Tests.fsproj"
    test <@ result = TestProject "MyApp.Tests" @>

[<Fact(Timeout = 15000)>]
let ``classifyProject identifies test project in test directory`` () =
    let result = classifyProject "test/MyApp.Tests/MyApp.Tests.fsproj"
    test <@ result = TestProject "MyApp.Tests" @>

[<Fact(Timeout = 15000)>]
let ``classifyProject identifies test project by Tests suffix`` () =
    let result = classifyProject "src/MyApp.Tests/MyApp.Tests.fsproj"
    test <@ result = TestProject "MyApp.Tests" @>

[<Fact(Timeout = 15000)>]
let ``classifyProject identifies test project by Test suffix`` () =
    let result = classifyProject "src/MyApp.Test/MyApp.Test.fsproj"
    test <@ result = TestProject "MyApp.Test" @>

[<Fact(Timeout = 15000)>]
let ``classifyProject identifies source project in src directory`` () =
    let result = classifyProject "src/MyApp/MyApp.fsproj"
    test <@ result = SourceProject "MyApp" @>

[<Fact(Timeout = 15000)>]
let ``classifyProject identifies source project at root`` () =
    let result = classifyProject "MyApp/MyApp.fsproj"
    test <@ result = SourceProject "MyApp" @>

[<Fact(Timeout = 15000)>]
let ``classifyProject normalizes backslashes`` () =
    let result = classifyProject @"tests\MyApp.Tests\MyApp.Tests.fsproj"
    test <@ result = TestProject "MyApp.Tests" @>

// --- generateConfig ---

[<Fact(Timeout = 15000)>]
let ``generateConfig with source and test projects`` () =
    let projects = [ "src/MyApp/MyApp.fsproj"; "tests/MyApp.Tests/MyApp.Tests.fsproj" ]

    let config = generateConfig projects

    test <@ config.Build.IsSome @>
    test <@ config.Build.Value.Length = 1 @>
    test <@ config.Format = Auto @>
    test <@ config.Lint = true @>
    test <@ config.Tests.IsSome @>
    let tests = config.Tests.Value
    test <@ tests.Projects.Length = 1 @>
    test <@ tests.Projects.[0].Project = "MyApp.Tests" @>

[<Fact(Timeout = 15000)>]
let ``generateConfig with no test projects omits tests section`` () =
    let projects = [ "src/MyApp/MyApp.fsproj" ]
    let config = generateConfig projects
    test <@ config.Tests = None @>

[<Fact(Timeout = 15000)>]
let ``generateConfig test project args use run with project path`` () =
    let projects = [ "tests/MyApp.Tests/MyApp.Tests.fsproj" ]
    let config = generateConfig projects
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Command = "dotnet" @>
    test <@ p.Args = "run --project tests/MyApp.Tests --no-build --" @>
    test <@ p.FilterTemplate = Some "--filter-class {classes}" @>
    test <@ p.ClassJoin = " " @>

[<Fact(Timeout = 15000)>]
let ``generateConfig sets cache to file`` () =
    let config = generateConfig [ "src/App/App.fsproj" ]
    test <@ config.Cache = NoCache @>

[<Fact(Timeout = 15000)>]
let ``generateConfig with multiple test projects groups by default`` () =
    let projects =
        [ "tests/Unit.Tests/Unit.Tests.fsproj"
          "tests/Integration.Tests/Integration.Tests.fsproj" ]

    let config = generateConfig projects
    let tests = config.Tests.Value
    test <@ tests.Projects.Length = 2 @>
    test <@ tests.Projects.[0].Project = "Unit.Tests" @>
    test <@ tests.Projects.[1].Project = "Integration.Tests" @>

[<Fact(Timeout = 15000)>]
let ``generateConfig with empty project list`` () =
    let config = generateConfig []
    test <@ config.Tests = None @>
    test <@ config.Build.IsSome @>

[<Fact(Timeout = 15000)>]
let ``generateConfig test project sets coverage to true`` () =
    let projects = [ "tests/MyApp.Tests/MyApp.Tests.fsproj" ]
    let config = generateConfig projects
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Coverage = true @>

[<Fact(Timeout = 15000)>]
let ``generateConfig test project group is default`` () =
    let projects = [ "tests/MyApp.Tests/MyApp.Tests.fsproj" ]
    let config = generateConfig projects
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Group = "default" @>

// --- serializeConfig ---

[<Fact(Timeout = 15000)>]
let ``serializeConfig produces valid JSON with build and tests`` () =
    let projects = [ "src/App/App.fsproj"; "tests/App.Tests/App.Tests.fsproj" ]

    let config = generateConfig projects
    let json = serializeConfig config

    let parsed =
        parseConfig
            json
            { defaultTestConfig () with
                Build = None
                Format = Off
                Lint = false
                Cache = NoCache }

    test <@ parsed.Build.IsSome @>
    test <@ parsed.Format = Auto @>
    test <@ parsed.Tests.IsSome @>
    test <@ parsed.Tests.Value.Projects.Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig with no build omits build section`` () =
    let config =
        { defaultTestConfig () with
            Build = None }


    let json = serializeConfig config
    test <@ not (json.Contains("\"build\"")) @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig with empty build list omits build section`` () =
    let config =
        { defaultTestConfig () with
            Build = Some [] }


    let json = serializeConfig config
    test <@ not (json.Contains("\"build\"")) @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig with multiple builds writes array`` () =
    let config =
        { defaultTestConfig () with
            Build =
                Some
                    [ {| Command = "dotnet"
                         Args = "build src/A"
                         BuildTemplate = None
                         DependsOn = []
                         TimeoutSec = None |}
                      {| Command = "dotnet"
                         Args = "build src/B"
                         BuildTemplate = None
                         DependsOn = []
                         TimeoutSec = None |} ] }


    let json = serializeConfig config
    test <@ json.Contains("build src/A") @>
    test <@ json.Contains("build src/B") @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig format Off writes false`` () =
    let config =
        { defaultTestConfig () with
            Build = None
            Format = Off }


    let json = serializeConfig config

    let parsed =
        parseConfig
            json
            { defaultTestConfig () with
                Build = None
                Lint = false
                Cache = NoCache }

    test <@ parsed.Format = Off @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig format Check writes check string`` () =
    let config =
        { defaultTestConfig () with
            Build = None
            Format = Check }


    let json = serializeConfig config

    let parsed =
        parseConfig
            json
            { defaultTestConfig () with
                Build = None
                Format = Off
                Lint = false
                Cache = NoCache }

    test <@ parsed.Format = Check @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig cache InMemoryOnly writes memory`` () =
    let config =
        { defaultTestConfig () with
            Build = None
            Cache = InMemoryOnly 100 }


    let json = serializeConfig config
    test <@ json.Contains("\"memory\"") @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig cache NoCache writes false`` () =
    let config =
        { defaultTestConfig () with
            Build = None
            Cache = NoCache }


    let json = serializeConfig config

    let parsed =
        parseConfig
            json
            { defaultTestConfig () with
                Build = None
                Format = Off
                Lint = false }

    test <@ parsed.Cache = NoCache @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig with no tests omits tests section`` () =
    let config =
        { defaultTestConfig () with
            Build = None }


    let json = serializeConfig config
    test <@ not (json.Contains("\"tests\"")) @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig with empty test projects omits tests section`` () =
    let config =
        { defaultTestConfig () with
            Build = None
            Tests =
                Some
                    {| BeforeRun = None
                       Extensions = []
                       Projects = []
                       Excluded = []
                       Solution = None
                       CoverageDir = "coverage"
                       DependsOn = [] |} }


    let json = serializeConfig config
    test <@ not (json.Contains("\"tests\"")) @>

[<Fact(Timeout = 15000)>]
let ``serializeConfig test project without filterTemplate omits it`` () =
    let config =
        { defaultTestConfig () with
            Build = None
            Tests =
                Some
                    {| BeforeRun = None
                       Extensions = []
                       Projects =
                        [ { Project = "MyTests"
                            Command = "dotnet"
                            Args = "test"
                            Group = "default"
                            Environment = []
                            FilterTemplate = None
                            ClassJoin = " "
                            CollectCoverage = true
                            Coverage = true
                            CoverageArgsTemplate = None
                            TimeoutSec = None
                            ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect } ]
                       Excluded = []
                       Solution = None
                       CoverageDir = "coverage"
                       DependsOn = [] |} }


    let json = serializeConfig config
    test <@ not (json.Contains("filterTemplate")) @>

// --- discoverProjects ---

[<Fact(Timeout = 15000)>]
let ``discoverProjects finds fsproj files in directory tree`` () =
    withTempDir "init-disc" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyApp")
        let testDir = Path.Combine(tmpDir, "tests", "MyApp.Tests")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(testDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "MyApp.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(testDir, "MyApp.Tests.fsproj"), "<Project/>")

        let projects = discoverProjects tmpDir None
        test <@ projects |> List.length = 2 @>
        test <@ projects |> List.exists (fun p -> p.Contains("MyApp.fsproj")) @>
        test <@ projects |> List.exists (fun p -> p.Contains("MyApp.Tests.fsproj")) @>)

[<Fact(Timeout = 15000)>]
let ``discoverProjects returns paths relative to repo root`` () =
    withTempDir "init-rel" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "App")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "App.fsproj"), "<Project/>")

        let projects = discoverProjects tmpDir None
        test <@ projects.Length = 1 @>
        test <@ not (Path.IsPathRooted(projects.[0])) @>
        test <@ projects.[0] = Path.Combine("src", "App", "App.fsproj") @>)

[<Fact(Timeout = 15000)>]
let ``discoverProjects excludes bin directories`` () =
    withTempDir "init-bin" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "App")
        let binDir = Path.Combine(tmpDir, "src", "App", "bin", "Debug")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(binDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "App.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(binDir, "App.fsproj"), "<Project/>")

        let projects = discoverProjects tmpDir None
        test <@ projects.Length = 1 @>
        test <@ projects.[0] = Path.Combine("src", "App", "App.fsproj") @>)

[<Fact(Timeout = 15000)>]
let ``discoverProjects excludes obj directories`` () =
    withTempDir "init-obj" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "App")
        let objDir = Path.Combine(tmpDir, "src", "App", "obj")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "App.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(objDir, "App.fsproj"), "<Project/>")

        let projects = discoverProjects tmpDir None
        test <@ projects.Length = 1 @>
        test <@ projects.[0] = Path.Combine("src", "App", "App.fsproj") @>)

[<Fact(Timeout = 15000)>]
let ``discoverProjects returns empty list for empty directory`` () =
    withTempDir "init-empty" (fun tmpDir ->
        let projects = discoverProjects tmpDir None
        test <@ List.isEmpty projects @>)

[<Fact(Timeout = 15000)>]
let ``discoverProjects returns sorted results`` () =
    withTempDir "init-sort" (fun tmpDir ->
        let dir1 = Path.Combine(tmpDir, "z-project")
        let dir2 = Path.Combine(tmpDir, "a-project")
        Directory.CreateDirectory(dir1) |> ignore
        Directory.CreateDirectory(dir2) |> ignore
        File.WriteAllText(Path.Combine(dir1, "Z.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(dir2, "A.fsproj"), "<Project/>")

        let projects = discoverProjects tmpDir None
        test <@ projects.Length = 2 @>
        test <@ projects.[0] < projects.[1] @>)

[<Fact(Timeout = 15000)>]
let ``discoverProjects returns empty list for missing directory`` () =
    let projects = discoverProjects "/nonexistent/path/that/does/not/exist" None
    test <@ List.isEmpty projects @>

[<Fact(Timeout = 15000)>]
let ``discoverProjects returns empty list on permission error`` () =
    let failEnumerate _ _ =
        raise (System.UnauthorizedAccessException("Access denied"))

    let projects = discoverProjects "/some/path" (Some failEnumerate)
    test <@ List.isEmpty projects @>

// The DirectoryNotFoundException arm only guards INJECTED enumerators: the
// SafeWalk default returns an empty sequence for a missing root rather than
// throwing, so a throwing enumerator is the only way to reach it.
[<Fact(Timeout = 15000)>]
let ``discoverProjects returns empty list when an injected enumerator reports a missing directory`` () =
    let failEnumerate _ _ =
        raise (DirectoryNotFoundException("gone"))

    let projects = discoverProjects "/some/path" (Some failEnumerate)
    test <@ List.isEmpty projects @>

[<Fact(Timeout = 15000)>]
let ``discoverProjects with injected enumerator uses it`` () =
    let fakeEnumerate (root: string) (_pattern: string) =
        seq {
            Path.Combine(root, "src", "Foo", "Foo.fsproj")
            Path.Combine(root, "tests", "Bar.Tests", "Bar.Tests.fsproj")
        }

    let projects = discoverProjects "/fake/root" (Some fakeEnumerate)
    test <@ projects.Length = 2 @>
    test <@ projects |> List.exists (fun p -> p.Contains("Foo.fsproj")) @>
    test <@ projects |> List.exists (fun p -> p.Contains("Bar.Tests.fsproj")) @>

[<Fact(Timeout = 15000)>]
let ``discoverProjects with injected enumerator still filters bin and obj`` () =
    let fakeEnumerate (root: string) (_pattern: string) =
        seq {
            Path.Combine(root, "src", "App", "App.fsproj")
            Path.Combine(root, "src", "App", "bin", "App.fsproj")
            Path.Combine(root, "src", "App", "obj", "App.fsproj")
        }

    let projects = discoverProjects "/fake/root" (Some fakeEnumerate)
    test <@ projects.Length = 1 @>

// REGRESSION (2026-07-13 wedge, second live instance): `fshw init` walks from
// the REPO ROOT, one hop from `.devenv/profile` → /nix/store → two self-loop
// symlinks in one dir. The pre-SafeWalk `SearchOption.AllDirectories` follows
// them and enumerates ~2^32 paths; on that code this test trips its Timeout.
[<Fact(Timeout = 15000)>]
let ``discoverProjects terminates on a repo containing a symlink cycle`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "init-cycle" (fun tmpDir ->
            let srcDir = Path.Combine(tmpDir, "src", "App")
            Directory.CreateDirectory(srcDir) |> ignore
            File.WriteAllText(Path.Combine(srcDir, "App.fsproj"), "<Project/>")

            // The `.devenv/profile` shape: a hidden dir portalling to a tree
            // with two self-loops in ONE directory (the branching cycle).
            let devenv = Path.Combine(tmpDir, ".devenv", "profile", "include")
            Directory.CreateDirectory(devenv) |> ignore
            Directory.CreateSymbolicLink(Path.Combine(devenv, "ncurses"), ".") |> ignore
            Directory.CreateSymbolicLink(Path.Combine(devenv, "ncursesw"), ".") |> ignore

            let projects = discoverProjects tmpDir None
            test <@ projects = [ Path.Combine("src", "App", "App.fsproj") ] @>)
