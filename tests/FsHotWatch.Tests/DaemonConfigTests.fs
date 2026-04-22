module FsHotWatch.Tests.DaemonConfigTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Daemon
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
      FileCommands = []
      Exclude = []
      LogDir = "logs" }

// --- parseConfig: empty JSON ---

[<Fact(Timeout = 5000)>]
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
    test <@ config.FileCommands |> List.isEmpty @>
    test <@ config.Exclude |> List.isEmpty @>
    test <@ config.LogDir = "logs" @>

// --- parseConfig: logDir ---

[<Fact(Timeout = 5000)>]
let ``parseConfig logDir custom value overrides default`` () =
    let config = parseConfig """{"logDir": "var/log"}""" defaults
    test <@ config.LogDir = "var/log" @>

[<Fact(Timeout = 5000)>]
let ``parseConfig logDir absolute path is preserved`` () =
    let config = parseConfig """{"logDir": "/var/log/fshw"}""" defaults
    test <@ config.LogDir = "/var/log/fshw" @>

// --- parseConfig: exclude ---

[<Fact(Timeout = 5000)>]
let ``parseConfig exclude with patterns`` () =
    let config = parseConfig """{"exclude": ["vendor/", "generated/"]}""" defaults
    test <@ config.Exclude = [ "vendor/"; "generated/" ] @>

[<Fact(Timeout = 5000)>]
let ``parseConfig exclude empty array`` () =
    let config = parseConfig """{"exclude": []}""" defaults
    test <@ config.Exclude |> List.isEmpty @>

[<Fact(Timeout = 5000)>]
let ``parseConfig no exclude returns empty list`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Exclude |> List.isEmpty @>

// --- parseConfig: build ---

[<Fact(Timeout = 5000)>]
let ``parseConfig build false disables build`` () =
    let config = parseConfig """{"build": false}""" defaults
    test <@ config.Build = Some [] @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``parseConfig format true returns Auto`` () =
    let config = parseConfig """{"format": true}""" defaults
    test <@ config.Format = Auto @>

[<Fact(Timeout = 5000)>]
let ``parseConfig format false returns Off`` () =
    let config = parseConfig """{"format": false}""" defaults
    test <@ config.Format = Off @>

[<Fact(Timeout = 5000)>]
let ``parseConfig format check string returns Check`` () =
    let config = parseConfig """{"format": "check"}""" defaults
    test <@ config.Format = Check @>

// --- parseConfig: lint ---

[<Fact(Timeout = 5000)>]
let ``parseConfig lint true enables lint`` () =
    let config = parseConfig """{"lint": true}""" defaults
    test <@ config.Lint = true @>

[<Fact(Timeout = 5000)>]
let ``parseConfig lint false disables lint`` () =
    let config = parseConfig """{"lint": false}""" defaults
    test <@ config.Lint = false @>

// --- parseConfig: cache ---

[<Fact(Timeout = 5000)>]
let ``parseConfig cache none string returns NoCache`` () =
    let config = parseConfig """{"cache": "none"}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache false string returns NoCache`` () =
    let config = parseConfig """{"cache": "false"}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache false bool returns NoCache`` () =
    let config = parseConfig """{"cache": false}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache true bool returns defaults cache`` () =
    let defaultsWithJj = { defaults with Cache = JjFileBackend }
    let config = parseConfig """{"cache": true}""" defaultsWithJj
    test <@ config.Cache = JjFileBackend @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache memory returns InMemoryOnly 500`` () =
    let config = parseConfig """{"cache": "memory"}""" defaults
    test <@ config.Cache = InMemoryOnly 500 @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache file returns FileBackend`` () =
    let config = parseConfig """{"cache": "file"}""" defaults
    test <@ config.Cache = FileBackend @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache jj returns JjFileBackend`` () =
    let config = parseConfig """{"cache": "jj"}""" defaults
    test <@ config.Cache = JjFileBackend @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache unknown string returns defaults cache`` () =
    let config = parseConfig """{"cache": "redis"}""" defaults
    test <@ config.Cache = FileBackend @>

[<Fact(Timeout = 5000)>]
let ``parseConfig cache missing uses defaults`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Cache = defaults.Cache @>

// --- parseConfig: analyzers ---

[<Fact(Timeout = 5000)>]
let ``parseConfig analyzers with paths`` () =
    let config = parseConfig """{"analyzers": {"paths": ["path1", "path2"]}}""" defaults

    test <@ config.Analyzers = Some {| Paths = [ "path1"; "path2" ] |} @>

[<Fact(Timeout = 5000)>]
let ``parseConfig analyzers with empty paths returns None`` () =
    let config = parseConfig """{"analyzers": {"paths": []}}""" defaults

    test <@ config.Analyzers = None @>

[<Fact(Timeout = 5000)>]
let ``parseConfig analyzers without paths returns None`` () =
    let config = parseConfig """{"analyzers": {}}""" defaults
    test <@ config.Analyzers = None @>

[<Fact(Timeout = 5000)>]
let ``parseConfig no analyzers returns None`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Analyzers = None @>

// --- parseConfig: tests ---

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``parseConfig tests with empty projects returns None`` () =
    let json = """{"tests": {"projects": []}}"""
    let config = parseConfig json defaults
    test <@ config.Tests = None @>

[<Fact(Timeout = 5000)>]
let ``parseConfig tests without projects key returns None`` () =
    let json = """{"tests": {}}"""
    let config = parseConfig json defaults
    test <@ config.Tests = None @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``parseConfig tests project with no project key defaults to unknown`` () =
    let json = """{"tests": {"projects": [{}]}}"""
    let config = parseConfig json defaults
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Project = "unknown" @>

// --- parseConfig: coverage ---
// Coverage config block was removed — coverage XMLs are now emitted under
// <repoRoot>/coverage/<project>/ unconditionally, and ratcheting is driven
// by fileCommands afterTests invoking an external tool.

// --- parseConfig: fileCommands ---

[<Fact(Timeout = 5000)>]
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
    test <@ config.FileCommands.[0].Pattern = Some "*.fsx" @>
    test <@ config.FileCommands.[0].AfterTests = None @>
    test <@ config.FileCommands.[0].Command = "dotnet" @>
    test <@ config.FileCommands.[0].Args = "fsi" @>
    test <@ config.FileCommands.[1].Pattern = Some "*.sql" @>
    test <@ config.FileCommands.[1].Command = "psql" @>
    test <@ config.FileCommands.[1].Args = "-f" @>

[<Fact(Timeout = 5000)>]
let ``parseConfig fileCommands entry without pattern or afterTests is rejected`` () =
    let json = """{"fileCommands": [{}]}"""

    let ex =
        Assert.ThrowsAny<exn>(fun () -> parseConfig json defaults |> ignore)

    test <@ ex.Message.Contains("pattern") && ex.Message.Contains("afterTests") @>

[<Fact(Timeout = 5000)>]
let ``parseConfig fileCommands afterTests true parses to AnyTest`` () =
    let json =
        """{"fileCommands": [{"name": "cov", "afterTests": true, "command": "echo", "args": "ran"}]}"""

    let config = parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>
    test <@ config.FileCommands.[0].Name = Some "cov" @>
    test <@ config.FileCommands.[0].Pattern = None @>

    test
        <@ config.FileCommands.[0].AfterTests = Some FsHotWatch.FileCommand.FileCommandPlugin.AnyTest @>

[<Fact(Timeout = 5000)>]
let ``parseConfig fileCommands afterTests list parses to TestProjects`` () =
    let json =
        """{"fileCommands": [{"name": "cov", "afterTests": ["A", "B"], "command": "echo", "args": "ran"}]}"""

    let config = parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>

    test
        <@
            config.FileCommands.[0].AfterTests = Some(
                FsHotWatch.FileCommand.FileCommandPlugin.TestProjects(Set.ofList [ "A"; "B" ])
            )
        @>

[<Fact(Timeout = 5000)>]
let ``parseConfig fileCommands afterTests without name is rejected`` () =
    let json =
        """{"fileCommands": [{"afterTests": true, "command": "echo", "args": "ran"}]}"""

    let ex =
        Assert.ThrowsAny<exn>(fun () -> parseConfig json defaults |> ignore)

    test <@ ex.Message.Contains("name") @>

[<Fact(Timeout = 5000)>]
let ``parseConfig fileCommands empty array`` () =
    let config = parseConfig """{"fileCommands": []}""" defaults
    test <@ config.FileCommands |> List.isEmpty @>

[<Fact(Timeout = 5000)>]
let ``parseConfig no fileCommands returns empty list`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.FileCommands |> List.isEmpty @>

// --- parseConfig: invalid JSON ---

[<Fact(Timeout = 5000)>]
let ``parseConfig with invalid JSON throws`` () =
    Assert.ThrowsAny<JsonException>(fun () -> parseConfig "not json" defaults |> ignore)
    |> ignore

[<Fact(Timeout = 5000)>]
let ``parseConfig with malformed JSON throws`` () =
    Assert.ThrowsAny<JsonException>(fun () -> parseConfig """{"build":}""" defaults |> ignore)
    |> ignore

// --- parseConfig: combined configuration ---

[<Fact(Timeout = 5000)>]
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
    test <@ config.FileCommands.Length = 1 @>

// --- detectDefaultCacheBackend ---

[<Fact(Timeout = 5000)>]
let ``detectDefaultCacheBackend returns JjFileBackend when .jj exists`` () =
    withTempDir "cfg-jj" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let result = detectDefaultCacheBackend tmpDir
        test <@ result = JjFileBackend @>)

[<Fact(Timeout = 5000)>]
let ``detectDefaultCacheBackend returns FileBackend when no .jj`` () =
    withTempDir "cfg-nojj" (fun tmpDir ->
        let result = detectDefaultCacheBackend tmpDir
        test <@ result = FileBackend @>)

// --- createCacheComponents ---

[<Fact(Timeout = 5000)>]
let ``createCacheComponents NoCache returns None None`` () =
    withTempDir "cfg-cc" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir NoCache
        test <@ backend = None @>
        test <@ keyProvider = None @>)

[<Fact(Timeout = 5000)>]
let ``createCacheComponents InMemoryOnly returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-mem" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir (InMemoryOnly 100)
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

[<Fact(Timeout = 5000)>]
let ``createCacheComponents FileBackend returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-file" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir FileBackend
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

[<Fact(Timeout = 5000)>]
let ``createCacheComponents JjFileBackend returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-jj" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir JjFileBackend
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

// --- defaultConfigFor ---

[<Fact(Timeout = 5000)>]
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
        test <@ config.FileCommands |> List.isEmpty @>)

// --- parseConfig: per-project coverage exclusion ---

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``parseConfig build single object still works`` () =
    let json = """{"build": {"command": "make", "args": "all"}}"""
    let config = parseConfig json defaults

    test <@ config.Build.IsSome @>
    let builds = config.Build.Value
    test <@ builds.Length = 1 @>
    test <@ builds.[0].Command = "make" @>
    test <@ builds.[0].Args = "all" @>

[<Fact(Timeout = 5000)>]
let ``parseConfig build false disables build as empty list`` () =
    let config = parseConfig """{"build": false}""" defaults
    test <@ config.Build.IsSome @>
    test <@ config.Build.Value |> List.isEmpty @>

// --- parseConfig: test extensions ---

[<Fact(Timeout = 5000)>]
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
    test <@ tests.Extensions.[0].Kind = Falco @>
    test <@ tests.Extensions.[0].Project = "IntTests" @>
    test <@ tests.Extensions.[0].TestDir = "tests/IntTests" @>

[<Fact(Timeout = 5000)>]
let ``parseConfig tests without extensions defaults to empty`` () =
    let json =
        """{
        "tests": {
            "projects": [{"project": "Tests"}]
        }
    }"""

    let config = parseConfig json defaults
    test <@ config.Tests.Value.Extensions |> List.isEmpty @>

[<Fact(Timeout = 5000)>]
let ``loadConfig with jj repo defaults to JjFileBackend`` () =
    withTempDir "cfg-def-jj" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let config = loadConfig tmpDir
        test <@ config.Cache = JjFileBackend @>)

// --- parseConfig: build dependsOn ---

[<Fact(Timeout = 5000)>]
let ``parseConfig build with dependsOn`` () =
    let json =
        """{"build": {"command": "dotnet", "args": "build", "dependsOn": ["npm-deps"]}}"""

    let config = parseConfig json defaults

    test <@ config.Build.IsSome @>
    let builds = config.Build.Value
    test <@ builds.Length = 1 @>
    test <@ builds.[0].DependsOn = [ "npm-deps" ] @>

[<Fact(Timeout = 5000)>]
let ``parseConfig build without dependsOn defaults to empty`` () =
    let json = """{"build": {"command": "dotnet", "args": "build"}}"""
    let config = parseConfig json defaults
    let builds = config.Build.Value
    test <@ builds.[0].DependsOn |> List.isEmpty @>

[<Fact(Timeout = 5000)>]
let ``parseConfig build with multiple dependsOn`` () =
    let json = """{"build": {"dependsOn": ["setup", "codegen"]}}"""

    let config = parseConfig json defaults
    let builds = config.Build.Value
    test <@ builds.[0].DependsOn = [ "setup"; "codegen" ] @>

// --- stripConfig tests ---

[<Fact(Timeout = 5000)>]
let ``stripConfig preserves format mode`` () =
    let config = { defaults with Format = Check }
    let stripped = stripConfig config
    test <@ stripped.Format = Check @>

[<Fact(Timeout = 5000)>]
let ``stripConfig disables lint`` () =
    let stripped = stripConfig defaults
    test <@ stripped.Lint = false @>

[<Fact(Timeout = 5000)>]
let ``stripConfig sets build to empty list`` () =
    let stripped = stripConfig defaults
    test <@ stripped.Build = Some [] @>

[<Fact(Timeout = 5000)>]
let ``stripConfig caller can restore build config`` () =
    let stripped =
        { stripConfig defaults with
            Build = defaults.Build }

    test <@ stripped.Build = defaults.Build @>
    test <@ stripped.Build.Value.Length = 1 @>

[<Fact(Timeout = 5000)>]
let ``registerPlugins with build config registers build plugin`` () =
    withTempDir "cfg-build-reg" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None (set [ 1182 ]) []

        let config =
            { stripConfig defaults with
                Build = defaults.Build }

        registerPlugins daemon tmpDir config
        let statuses = daemon.Host.GetAllStatuses()
        test <@ statuses.ContainsKey("build") @>)

[<Fact(Timeout = 5000)>]
let ``registerPlugins with stripped config does not register build plugin`` () =
    withTempDir "cfg-build-noreg" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir None None (set [ 1182 ]) []

        let config = stripConfig defaults
        registerPlugins daemon tmpDir config
        let statuses = daemon.Host.GetAllStatuses()
        test <@ not (statuses.ContainsKey("build")) @>)
