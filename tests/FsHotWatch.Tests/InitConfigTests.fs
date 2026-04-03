module FsHotWatch.Tests.InitConfigTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Cli.InitConfig
open FsHotWatch.Tests.TestHelpers

// --- classifyProject ---

[<Fact>]
let ``classifyProject identifies test project in tests directory`` () =
    let result = classifyProject "tests/MyApp.Tests/MyApp.Tests.fsproj"
    test <@ result = TestProject "MyApp.Tests" @>

[<Fact>]
let ``classifyProject identifies test project in test directory`` () =
    let result = classifyProject "test/MyApp.Tests/MyApp.Tests.fsproj"
    test <@ result = TestProject "MyApp.Tests" @>

[<Fact>]
let ``classifyProject identifies test project by Tests suffix`` () =
    let result = classifyProject "src/MyApp.Tests/MyApp.Tests.fsproj"
    test <@ result = TestProject "MyApp.Tests" @>

[<Fact>]
let ``classifyProject identifies source project in src directory`` () =
    let result = classifyProject "src/MyApp/MyApp.fsproj"
    test <@ result = SourceProject "MyApp" @>

[<Fact>]
let ``classifyProject identifies source project at root`` () =
    let result = classifyProject "MyApp/MyApp.fsproj"
    test <@ result = SourceProject "MyApp" @>

// --- generateConfig ---

[<Fact>]
let ``generateConfig with source and test projects`` () =
    let projects = [ "src/MyApp/MyApp.fsproj"; "tests/MyApp.Tests/MyApp.Tests.fsproj" ]

    let config = generateConfig projects false

    test <@ config.Build.IsSome @>
    test <@ config.Build.Value.Length = 1 @>
    test <@ config.Format = Auto @>
    test <@ config.Lint = true @>
    test <@ config.Tests.IsSome @>
    let tests = config.Tests.Value
    test <@ tests.Projects.Length = 1 @>
    test <@ tests.Projects.[0].Project = "MyApp.Tests" @>

[<Fact>]
let ``generateConfig with no test projects omits tests section`` () =
    let projects = [ "src/MyApp/MyApp.fsproj" ]
    let config = generateConfig projects false
    test <@ config.Tests = None @>

[<Fact>]
let ``generateConfig test project args use run with project path`` () =
    let projects = [ "tests/MyApp.Tests/MyApp.Tests.fsproj" ]
    let config = generateConfig projects false
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Command = "dotnet" @>
    test <@ p.Args = "run --project tests/MyApp.Tests --no-build --" @>
    test <@ p.FilterTemplate = Some "--filter-class {classes}" @>
    test <@ p.ClassJoin = " " @>

[<Fact>]
let ``generateConfig with jj sets cache to jj`` () =
    let config = generateConfig [ "src/App/App.fsproj" ] true
    test <@ config.Cache = JjFileBackend @>

[<Fact>]
let ``generateConfig without jj sets cache to file`` () =
    let config = generateConfig [ "src/App/App.fsproj" ] false
    test <@ config.Cache = FileBackend @>

[<Fact>]
let ``generateConfig with multiple test projects groups by default`` () =
    let projects =
        [ "tests/Unit.Tests/Unit.Tests.fsproj"
          "tests/Integration.Tests/Integration.Tests.fsproj" ]

    let config = generateConfig projects false
    let tests = config.Tests.Value
    test <@ tests.Projects.Length = 2 @>
    test <@ tests.Projects.[0].Project = "Unit.Tests" @>
    test <@ tests.Projects.[1].Project = "Integration.Tests" @>

// --- serializeConfig ---

[<Fact>]
let ``serializeConfig produces valid JSON with build and tests`` () =
    let projects = [ "src/App/App.fsproj"; "tests/App.Tests/App.Tests.fsproj" ]

    let config = generateConfig projects false
    let json = serializeConfig config

    // Should be parseable back
    let parsed =
        parseConfig
            json
            { DaemonConfiguration.Build = None
              Format = Off
              Lint = false
              Cache = NoCache
              Analyzers = None
              Tests = None
              Coverage = None
              FileCommands = [] }

    test <@ parsed.Build.IsSome @>
    test <@ parsed.Format = Auto @>
    test <@ parsed.Tests.IsSome @>
    test <@ parsed.Tests.Value.Projects.Length = 1 @>

// --- discoverProjects ---

[<Fact>]
let ``discoverProjects finds fsproj files in directory tree`` () =
    withTempDir "init-disc" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "MyApp")
        let testDir = Path.Combine(tmpDir, "tests", "MyApp.Tests")
        Directory.CreateDirectory(srcDir) |> ignore
        Directory.CreateDirectory(testDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "MyApp.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(testDir, "MyApp.Tests.fsproj"), "<Project/>")

        let projects = discoverProjects tmpDir
        test <@ projects |> List.length = 2 @>
        test <@ projects |> List.exists (fun p -> p.Contains("MyApp.fsproj")) @>
        test <@ projects |> List.exists (fun p -> p.Contains("MyApp.Tests.fsproj")) @>)

[<Fact>]
let ``discoverProjects returns paths relative to repo root`` () =
    withTempDir "init-rel" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src", "App")
        Directory.CreateDirectory(srcDir) |> ignore
        File.WriteAllText(Path.Combine(srcDir, "App.fsproj"), "<Project/>")

        let projects = discoverProjects tmpDir
        test <@ projects.Length = 1 @>
        // Should be relative, not absolute
        test <@ not (Path.IsPathRooted(projects.[0])) @>
        test <@ projects.[0] = Path.Combine("src", "App", "App.fsproj") @>)
