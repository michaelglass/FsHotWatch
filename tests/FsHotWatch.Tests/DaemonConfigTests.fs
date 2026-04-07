module FsHotWatch.Tests.DaemonConfigTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Tests.TestHelpers

// --- Helper: defaults with known cache backend ---

let private defaults: DaemonConfiguration =
    { Build =
        Some
            [ {| Command = "dotnet"
                 Args = "build"
                 BuildTemplate = None
                 DependsOn = [] |} ]
      Format = Auto
      Lint = true
      Cache = FileBackend
      Analyzers = None
      Tests = None
      Coverage = None
      FileCommands = [] }

// --- parseConfig: empty JSON ---

[<Fact>]
let ``parseConfig with empty JSON returns defaults`` () =
    let config = parseConfig "{}" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = [] |} ]
        @>

    test <@ config.Format = Auto @>
    test <@ config.Lint = true @>
    test <@ config.Cache = FileBackend @>
    test <@ config.Analyzers = None @>
    test <@ config.Tests = None @>
    test <@ config.Coverage = None @>
    test <@ config.FileCommands |> List.isEmpty @>

// --- parseConfig: build ---

[<Fact>]
let ``parseConfig build false disables build`` () =
    let config = parseConfig """{"build": false}""" defaults
    test <@ config.Build = Some [] @>

[<Fact>]
let ``parseConfig build true uses default build`` () =
    let config = parseConfig """{"build": true}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = [] |} ]
        @>

[<Fact>]
let ``parseConfig build object with custom command and args`` () =
    let config =
        parseConfig """{"build": {"command": "make", "args": "all"}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "make"
                     Args = "all"
                     BuildTemplate = None
                     DependsOn = [] |} ]
        @>

[<Fact>]
let ``parseConfig build object with only command uses default args`` () =
    let config = parseConfig """{"build": {"command": "make"}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "make"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = [] |} ]
        @>

[<Fact>]
let ``parseConfig build object with only args uses default command`` () =
    let config = parseConfig """{"build": {"args": "release"}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "release"
                     BuildTemplate = None
                     DependsOn = [] |} ]
        @>

[<Fact>]
let ``parseConfig build empty object uses defaults`` () =
    let config = parseConfig """{"build": {}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = [] |} ]
        @>

// --- parseConfig: format ---

[<Fact>]
let ``parseConfig format true returns Auto`` () =
    let config = parseConfig """{"format": true}""" defaults
    test <@ config.Format = Auto @>

[<Fact>]
let ``parseConfig format false returns Off`` () =
    let config = parseConfig """{"format": false}""" defaults
    test <@ config.Format = Off @>

[<Fact>]
let ``parseConfig format check string returns Check`` () =
    let config = parseConfig """{"format": "check"}""" defaults
    test <@ config.Format = Check @>

// --- parseConfig: lint ---

[<Fact>]
let ``parseConfig lint true enables lint`` () =
    let config = parseConfig """{"lint": true}""" defaults
    test <@ config.Lint = true @>

[<Fact>]
let ``parseConfig lint false disables lint`` () =
    let config = parseConfig """{"lint": false}""" defaults
    test <@ config.Lint = false @>

// --- parseConfig: cache ---

[<Fact>]
let ``parseConfig cache none string returns NoCache`` () =
    let config = parseConfig """{"cache": "none"}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact>]
let ``parseConfig cache false string returns NoCache`` () =
    let config = parseConfig """{"cache": "false"}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact>]
let ``parseConfig cache false bool returns NoCache`` () =
    let config = parseConfig """{"cache": false}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact>]
let ``parseConfig cache true bool returns defaults cache`` () =
    let defaultsWithJj = { defaults with Cache = JjFileBackend }
    let config = parseConfig """{"cache": true}""" defaultsWithJj
    test <@ config.Cache = JjFileBackend @>

[<Fact>]
let ``parseConfig cache memory returns InMemoryOnly 500`` () =
    let config = parseConfig """{"cache": "memory"}""" defaults
    test <@ config.Cache = InMemoryOnly 500 @>

[<Fact>]
let ``parseConfig cache file returns FileBackend`` () =
    let config = parseConfig """{"cache": "file"}""" defaults
    test <@ config.Cache = FileBackend @>

[<Fact>]
let ``parseConfig cache jj returns JjFileBackend`` () =
    let config = parseConfig """{"cache": "jj"}""" defaults
    test <@ config.Cache = JjFileBackend @>

[<Fact>]
let ``parseConfig cache unknown string returns defaults cache`` () =
    let config = parseConfig """{"cache": "redis"}""" defaults
    test <@ config.Cache = FileBackend @>

[<Fact>]
let ``parseConfig cache missing uses defaults`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Cache = defaults.Cache @>

// --- parseConfig: analyzers ---

[<Fact>]
let ``parseConfig analyzers with paths`` () =
    let config = parseConfig """{"analyzers": {"paths": ["path1", "path2"]}}""" defaults

    test <@ config.Analyzers = Some {| Paths = [ "path1"; "path2" ] |} @>

[<Fact>]
let ``parseConfig analyzers with empty paths returns None`` () =
    let config = parseConfig """{"analyzers": {"paths": []}}""" defaults

    test <@ config.Analyzers = None @>

[<Fact>]
let ``parseConfig analyzers without paths returns None`` () =
    let config = parseConfig """{"analyzers": {}}""" defaults
    test <@ config.Analyzers = None @>

[<Fact>]
let ``parseConfig no analyzers returns None`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Analyzers = None @>

// --- parseConfig: tests ---

[<Fact>]
let ``parseConfig tests with all fields`` () =
    let json =
        """{
        "tests": {
            "beforeRun": "dotnet build",
            "projects": [{
                "project": "MyTests.fsproj",
                "command": "dotnet",
                "args": "test --project MyTests.fsproj",
                "group": "unit",
                "environment": {"CI": "true", "VERBOSE": "1"},
                "filterTemplate": "--filter {0}",
                "classJoin": "|"
            }]
        }
    }"""

    let config = parseConfig json defaults

    test <@ config.Tests.IsSome @>
    let tests = config.Tests.Value
    test <@ tests.BeforeRun = Some "dotnet build" @>
    test <@ tests.Projects.Length = 1 @>
    let p = tests.Projects.[0]
    test <@ p.Project = "MyTests.fsproj" @>
    test <@ p.Command = "dotnet" @>
    test <@ p.Args = "test --project MyTests.fsproj" @>
    test <@ p.Group = "unit" @>
    test <@ p.Environment = [ ("CI", "true"); ("VERBOSE", "1") ] @>
    test <@ p.FilterTemplate = Some "--filter {0}" @>
    test <@ p.ClassJoin = "|" @>

[<Fact>]
let ``parseConfig tests with minimal project uses defaults`` () =
    let json =
        """{
        "tests": {
            "projects": [{
                "project": "Tests.fsproj"
            }]
        }
    }"""

    let config = parseConfig json defaults
    let tests = config.Tests.Value
    test <@ tests.BeforeRun = None @>
    let p = tests.Projects.[0]
    test <@ p.Project = "Tests.fsproj" @>
    test <@ p.Command = "dotnet" @>
    test <@ p.Args = "test --project Tests.fsproj" @>
    test <@ p.Group = "default" @>
    test <@ p.Environment |> List.isEmpty @>
    test <@ p.FilterTemplate = None @>
    test <@ p.ClassJoin = " " @>

[<Fact>]
let ``parseConfig tests with empty projects returns None`` () =
    let json = """{"tests": {"projects": []}}"""
    let config = parseConfig json defaults
    test <@ config.Tests = None @>

[<Fact>]
let ``parseConfig tests without projects key returns None`` () =
    let json = """{"tests": {}}"""
    let config = parseConfig json defaults
    test <@ config.Tests = None @>

[<Fact>]
let ``parseConfig tests with multiple projects`` () =
    let json =
        """{
        "tests": {
            "projects": [
                {"project": "UnitTests.fsproj"},
                {"project": "IntTests.fsproj", "group": "integration"}
            ]
        }
    }"""

    let config = parseConfig json defaults
    let tests = config.Tests.Value
    test <@ tests.Projects.Length = 2 @>
    test <@ tests.Projects.[0].Project = "UnitTests.fsproj" @>
    test <@ tests.Projects.[1].Project = "IntTests.fsproj" @>
    test <@ tests.Projects.[1].Group = "integration" @>

[<Fact>]
let ``parseConfig tests project with no project key defaults to unknown`` () =
    let json = """{"tests": {"projects": [{}]}}"""
    let config = parseConfig json defaults
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Project = "unknown" @>

// --- parseConfig: coverage ---

[<Fact>]
let ``parseConfig coverage with directory`` () =
    let config = parseConfig """{"coverage": {"directory": "./cov"}}""" defaults

    test
        <@
            config.Coverage = Some
                {| AfterCheck = None
                   Directory = "./cov"
                   ThresholdsFile = None |}
        @>

[<Fact>]
let ``parseConfig coverage with directory and thresholdsFile`` () =
    let json =
        """{"coverage": {"directory": "./cov", "thresholdsFile": "thresholds.json"}}"""

    let config = parseConfig json defaults

    test
        <@
            config.Coverage = Some
                {| AfterCheck = None
                   Directory = "./cov"
                   ThresholdsFile = Some "thresholds.json" |}
        @>

[<Fact>]
let ``parseConfig coverage with empty object uses default directory`` () =
    let config = parseConfig """{"coverage": {}}""" defaults

    test
        <@
            config.Coverage = Some
                {| AfterCheck = None
                   Directory = "./coverage"
                   ThresholdsFile = None |}
        @>

[<Fact>]
let ``parseConfig no coverage returns None`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Coverage = None @>

// --- parseConfig: fileCommands ---

[<Fact>]
let ``parseConfig fileCommands with entries`` () =
    let json =
        """{
        "fileCommands": [
            {"pattern": "*.fsx", "command": "dotnet", "args": "fsi"},
            {"pattern": "*.sql", "command": "psql", "args": "-f"}
        ]
    }"""

    let config = parseConfig json defaults
    test <@ config.FileCommands.Length = 2 @>
    test <@ config.FileCommands.[0].Pattern = "*.fsx" @>
    test <@ config.FileCommands.[0].Command = "dotnet" @>
    test <@ config.FileCommands.[0].Args = "fsi" @>
    test <@ config.FileCommands.[1].Pattern = "*.sql" @>
    test <@ config.FileCommands.[1].Command = "psql" @>
    test <@ config.FileCommands.[1].Args = "-f" @>

[<Fact>]
let ``parseConfig fileCommands with empty entry uses defaults`` () =
    let json = """{"fileCommands": [{}]}"""
    let config = parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>
    test <@ config.FileCommands.[0].Pattern = "*.fsx" @>
    test <@ config.FileCommands.[0].Command = "echo" @>
    test <@ config.FileCommands.[0].Args = "" @>

[<Fact>]
let ``parseConfig fileCommands empty array`` () =
    let config = parseConfig """{"fileCommands": []}""" defaults
    test <@ config.FileCommands |> List.isEmpty @>

[<Fact>]
let ``parseConfig no fileCommands returns empty list`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.FileCommands |> List.isEmpty @>

// --- parseConfig: invalid JSON ---

[<Fact>]
let ``parseConfig with invalid JSON throws`` () =
    Assert.ThrowsAny<JsonException>(fun () -> parseConfig "not json" defaults |> ignore)
    |> ignore

[<Fact>]
let ``parseConfig with malformed JSON throws`` () =
    Assert.ThrowsAny<JsonException>(fun () -> parseConfig """{"build":}""" defaults |> ignore)
    |> ignore

// --- parseConfig: combined configuration ---

[<Fact>]
let ``parseConfig with full configuration`` () =
    let json =
        """{
        "build": {"command": "make", "args": "all"},
        "format": false,
        "lint": false,
        "cache": "jj",
        "analyzers": {"paths": ["/analyzers"]},
        "tests": {
            "beforeRun": "make build",
            "projects": [{"project": "Tests.fsproj"}]
        },
        "coverage": {"directory": "./cov"},
        "fileCommands": [{"pattern": "*.sql", "command": "psql", "args": "-f"}]
    }"""

    let config = parseConfig json defaults

    test
        <@
            config.Build = Some
                [ {| Command = "make"
                     Args = "all"
                     BuildTemplate = None
                     DependsOn = [] |} ]
        @>

    test <@ config.Format = Off @>
    test <@ config.Lint = false @>
    test <@ config.Cache = JjFileBackend @>
    test <@ config.Analyzers = Some {| Paths = [ "/analyzers" ] |} @>
    test <@ config.Tests.IsSome @>
    test <@ config.Coverage.IsSome @>
    test <@ config.FileCommands.Length = 1 @>

// --- detectDefaultCacheBackend ---

[<Fact>]
let ``detectDefaultCacheBackend returns JjFileBackend when .jj exists`` () =
    withTempDir "cfg-jj" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let result = detectDefaultCacheBackend tmpDir
        test <@ result = JjFileBackend @>)

[<Fact>]
let ``detectDefaultCacheBackend returns FileBackend when no .jj`` () =
    withTempDir "cfg-nojj" (fun tmpDir ->
        let result = detectDefaultCacheBackend tmpDir
        test <@ result = FileBackend @>)

// --- createCacheComponents ---

[<Fact>]
let ``createCacheComponents NoCache returns None None`` () =
    withTempDir "cfg-cc" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir NoCache
        test <@ backend = None @>
        test <@ keyProvider = None @>)

[<Fact>]
let ``createCacheComponents InMemoryOnly returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-mem" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir (InMemoryOnly 100)
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

[<Fact>]
let ``createCacheComponents FileBackend returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-file" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir FileBackend
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

[<Fact>]
let ``createCacheComponents JjFileBackend returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-jj" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir JjFileBackend
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

// --- defaultConfigFor ---

[<Fact>]
let ``loadConfig with no config file returns expected defaults`` () =
    withTempDir "cfg-def" (fun tmpDir ->
        let config = loadConfig tmpDir

        test
            <@
                config.Build = Some
                    [ {| Command = "dotnet"
                         Args = "build"
                         BuildTemplate = None
                         DependsOn = [] |} ]
            @>

        test <@ config.Format = Auto @>
        test <@ config.Lint = true @>
        test <@ config.Cache = FileBackend @>
        test <@ config.Analyzers = None @>
        test <@ config.Tests = None @>
        test <@ config.Coverage = None @>
        test <@ config.FileCommands |> List.isEmpty @>)

// --- parseConfig: coverage afterCheck ---

[<Fact>]
let ``parseConfig coverage with afterCheck`` () =
    let json =
        """{"coverage": {"directory": "./cov", "afterCheck": "dotnet run -- ratchet"}}"""

    let config = parseConfig json defaults

    test
        <@
            config.Coverage = Some
                {| AfterCheck = Some "dotnet run -- ratchet"
                   Directory = "./cov"
                   ThresholdsFile = None |}
        @>

[<Fact>]
let ``parseConfig coverage without afterCheck defaults to None`` () =
    let config = parseConfig """{"coverage": {"directory": "./cov"}}""" defaults

    test
        <@
            config.Coverage = Some
                {| AfterCheck = None
                   Directory = "./cov"
                   ThresholdsFile = None |}
        @>

// --- parseConfig: per-project coverage exclusion ---

[<Fact>]
let ``parseConfig test project with coverage false`` () =
    let json =
        """{
        "tests": {
            "projects": [{
                "project": "IntTests",
                "coverage": false
            }]
        }
    }"""

    let config = parseConfig json defaults
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Coverage = false @>

[<Fact>]
let ``parseConfig test project without coverage defaults to true`` () =
    let json =
        """{
        "tests": {
            "projects": [{
                "project": "UnitTests"
            }]
        }
    }"""

    let config = parseConfig json defaults
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Coverage = true @>

// --- parseConfig: build as array ---

[<Fact>]
let ``parseConfig build array of commands`` () =
    let json =
        """{
        "build": [
            {"command": "dotnet", "args": "build src/App"},
            {"command": "dotnet", "args": "build src/Analyzers -c Release"}
        ]
    }"""

    let config = parseConfig json defaults

    test <@ config.Build.IsSome @>
    let builds = config.Build.Value
    test <@ builds.Length = 2 @>
    test <@ builds.[0].Command = "dotnet" @>
    test <@ builds.[0].Args = "build src/App" @>
    test <@ builds.[1].Command = "dotnet" @>
    test <@ builds.[1].Args = "build src/Analyzers -c Release" @>

[<Fact>]
let ``parseConfig build single object still works`` () =
    let json = """{"build": {"command": "make", "args": "all"}}"""
    let config = parseConfig json defaults

    test <@ config.Build.IsSome @>
    let builds = config.Build.Value
    test <@ builds.Length = 1 @>
    test <@ builds.[0].Command = "make" @>
    test <@ builds.[0].Args = "all" @>

[<Fact>]
let ``parseConfig build false disables build as empty list`` () =
    let config = parseConfig """{"build": false}""" defaults
    test <@ config.Build.IsSome @>
    test <@ config.Build.Value |> List.isEmpty @>

// --- parseConfig: test extensions ---

[<Fact>]
let ``parseConfig tests with extensions`` () =
    let json =
        """{
        "tests": {
            "extensions": [
                {"type": "falco", "project": "IntTests", "testDir": "tests/IntTests"}
            ],
            "projects": [{"project": "IntTests"}]
        }
    }"""

    let config = parseConfig json defaults
    test <@ config.Tests.IsSome @>
    let tests = config.Tests.Value
    test <@ tests.Extensions.Length = 1 @>
    test <@ tests.Extensions.[0].Type = "falco" @>
    test <@ tests.Extensions.[0].Project = "IntTests" @>
    test <@ tests.Extensions.[0].TestDir = "tests/IntTests" @>

[<Fact>]
let ``parseConfig tests without extensions defaults to empty`` () =
    let json =
        """{
        "tests": {
            "projects": [{"project": "Tests"}]
        }
    }"""

    let config = parseConfig json defaults
    test <@ config.Tests.Value.Extensions |> List.isEmpty @>

// --- parseConfig: fileCommands runOnStart ---

[<Fact>]
let ``parseConfig fileCommands with runOnStart true`` () =
    let json =
        """{"fileCommands": [{"pattern": "*.lock", "command": "npm", "args": "install", "runOnStart": true}]}"""

    let config = parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>
    test <@ config.FileCommands.[0].RunOnStart = true @>

[<Fact>]
let ``parseConfig fileCommands without runOnStart defaults to false`` () =
    let json =
        """{"fileCommands": [{"pattern": "*.fsx", "command": "dotnet", "args": "fsi"}]}"""

    let config = parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>
    test <@ config.FileCommands.[0].RunOnStart = false @>

[<Fact>]
let ``loadConfig with jj repo defaults to JjFileBackend`` () =
    withTempDir "cfg-def-jj" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let config = loadConfig tmpDir
        test <@ config.Cache = JjFileBackend @>)

// --- parseConfig: build dependsOn ---

[<Fact>]
let ``parseConfig build with dependsOn`` () =
    let json =
        """{"build": {"command": "dotnet", "args": "build", "dependsOn": ["npm-deps"]}}"""

    let config = parseConfig json defaults

    test <@ config.Build.IsSome @>
    let builds = config.Build.Value
    test <@ builds.Length = 1 @>
    test <@ builds.[0].DependsOn = [ "npm-deps" ] @>

[<Fact>]
let ``parseConfig build without dependsOn defaults to empty`` () =
    let json = """{"build": {"command": "dotnet", "args": "build"}}"""
    let config = parseConfig json defaults
    let builds = config.Build.Value
    test <@ builds.[0].DependsOn |> List.isEmpty @>

[<Fact>]
let ``parseConfig build with multiple dependsOn`` () =
    let json = """{"build": {"dependsOn": ["setup", "codegen"]}}"""

    let config = parseConfig json defaults
    let builds = config.Build.Value
    test <@ builds.[0].DependsOn = [ "setup"; "codegen" ] @>
