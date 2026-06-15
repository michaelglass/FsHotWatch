module FsHotWatch.Tests.DaemonConfigTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Daemon
open FsHotWatch.ErrorLedger
open FsHotWatch.Tests.TestHelpers

// --- Helper: defaults with known cache backend ---

let private defaults: DaemonConfiguration = defaultTestConfig ()

// --- parseConfig: empty JSON ---

[<Fact(Timeout = 15000)>]
let ``parseConfig with empty JSON returns defaults`` () =
    let config = parseConfig "{}" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = []
                     TimeoutSec = None |} ]
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

[<Fact(Timeout = 15000)>]
let ``parseConfig logDir custom value overrides default`` () =
    let config = parseConfig """{"logDir": "var/log"}""" defaults
    test <@ config.LogDir = "var/log" @>

[<Fact(Timeout = 15000)>]
let ``parseConfig logDir absolute path is preserved`` () =
    let config = parseConfig """{"logDir": "/var/log/fshw"}""" defaults
    test <@ config.LogDir = "/var/log/fshw" @>

// --- parseConfig: fsEventsLatencyMs ---

[<Fact(Timeout = 15000)>]
let ``parseConfig fsEventsLatencyMs custom value overrides default`` () =
    let config = parseConfig """{"fsEventsLatencyMs": 100}""" defaults
    test <@ config.FsEventsLatencyMs = 100 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig fsEventsLatencyMs absent yields default 250`` () =
    let config = parseConfig "{}" defaults
    test <@ config.FsEventsLatencyMs = 250 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig fsEventsLatencyMs zero is valid (no coalescing)`` () =
    let config = parseConfig """{"fsEventsLatencyMs": 0}""" defaults
    test <@ config.FsEventsLatencyMs = 0 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig fsEventsLatencyMs negative falls back to default 250`` () =
    // A negative latency is invalid; warn and fall back to the default.
    let config = parseConfig """{"fsEventsLatencyMs": -10}""" defaults
    test <@ config.FsEventsLatencyMs = 250 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig fsEventsLatencyMs non-numeric falls back to default 250`` () =
    // A non-number value is invalid; warn and fall back to the default.
    let config = parseConfig """{"fsEventsLatencyMs": "nope"}""" defaults
    test <@ config.FsEventsLatencyMs = 250 @>

// --- parseConfig: idleExitMin ---

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin absent yields Absent (AUTO)`` () =
    let config = parseConfig "{}" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Absent @>

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin positive yields Minutes`` () =
    let config = parseConfig """{"idleExitMin": 45}""" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Minutes 45 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin zero yields Disabled`` () =
    let config = parseConfig """{"idleExitMin": 0}""" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin false yields Disabled`` () =
    let config = parseConfig """{"idleExitMin": false}""" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin negative yields Disabled`` () =
    let config = parseConfig """{"idleExitMin": -5}""" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin true yields Disabled`` () =
    // `true` is not a valid threshold; treated as disabled (no implicit window).
    let config = parseConfig """{"idleExitMin": true}""" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin non-numeric string yields Absent`` () =
    // A garbage value falls through to the default (Absent / AUTO).
    let config = parseConfig """{"idleExitMin": "nope"}""" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Absent @>

// --- parseConfig: pressureIdleFloorMin ---

[<Fact(Timeout = 15000)>]
let ``parseConfig pressureIdleFloorMin absent yields Absent (default 2)`` () =
    let config = parseConfig "{}" defaults
    test <@ config.PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Absent @>

[<Fact(Timeout = 15000)>]
let ``parseConfig pressureIdleFloorMin positive yields Minutes`` () =
    let config = parseConfig """{"pressureIdleFloorMin": 5}""" defaults
    test <@ config.PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Minutes 5 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig pressureIdleFloorMin zero yields Disabled`` () =
    let config = parseConfig """{"pressureIdleFloorMin": 0}""" defaults
    test <@ config.PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig pressureIdleFloorMin false yields Disabled`` () =
    let config = parseConfig """{"pressureIdleFloorMin": false}""" defaults
    test <@ config.PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig pressureIdleFloorMin negative yields Disabled`` () =
    let config = parseConfig """{"pressureIdleFloorMin": -10}""" defaults
    test <@ config.PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig pressureIdleFloorMin true yields Disabled`` () =
    let config = parseConfig """{"pressureIdleFloorMin": true}""" defaults
    test <@ config.PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig pressureIdleFloorMin non-numeric string yields Absent`` () =
    // A garbage value falls through to the default (Absent / default 2).
    let config = parseConfig """{"pressureIdleFloorMin": "nope"}""" defaults
    test <@ config.PressureIdleFloorMin = FsHotWatch.IdleExit.PressureFloorConfig.Absent @>

// --- parseConfig: exclude ---

[<Fact(Timeout = 15000)>]
let ``parseConfig exclude with patterns`` () =
    let config = parseConfig """{"exclude": ["vendor/", "generated/"]}""" defaults
    test <@ config.Exclude = [ "vendor/"; "generated/" ] @>

[<Fact(Timeout = 15000)>]
let ``parseConfig exclude empty array`` () =
    let config = parseConfig """{"exclude": []}""" defaults
    test <@ config.Exclude |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``parseConfig no exclude returns empty list`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Exclude |> List.isEmpty @>

// --- parseConfig: build ---

[<Fact(Timeout = 15000)>]
let ``parseConfig build false disables build`` () =
    let config = parseConfig """{"build": false}""" defaults
    test <@ config.Build = Some [] @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build true uses default build`` () =
    let config = parseConfig """{"build": true}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = []
                     TimeoutSec = None |} ]
        @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build object with custom command and args`` () =
    let config =
        parseConfig """{"build": {"command": "make", "args": "all"}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "make"
                     Args = "all"
                     BuildTemplate = None
                     DependsOn = []
                     TimeoutSec = None |} ]
        @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build object with only command uses default args`` () =
    let config = parseConfig """{"build": {"command": "make"}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "make"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = []
                     TimeoutSec = None |} ]
        @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build object with only args uses default command`` () =
    let config = parseConfig """{"build": {"args": "release"}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "release"
                     BuildTemplate = None
                     DependsOn = []
                     TimeoutSec = None |} ]
        @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build empty object uses defaults`` () =
    let config = parseConfig """{"build": {}}""" defaults

    test
        <@
            config.Build = Some
                [ {| Command = "dotnet"
                     Args = "build"
                     BuildTemplate = None
                     DependsOn = []
                     TimeoutSec = None |} ]
        @>

// --- parseConfig: format ---

[<Fact(Timeout = 15000)>]
let ``parseConfig format true returns Auto`` () =
    let config = parseConfig """{"format": true}""" defaults
    test <@ config.Format = Auto @>

[<Fact(Timeout = 15000)>]
let ``parseConfig format false returns Off`` () =
    let config = parseConfig """{"format": false}""" defaults
    test <@ config.Format = Off @>

[<Fact(Timeout = 15000)>]
let ``parseConfig format check string returns Check`` () =
    let config = parseConfig """{"format": "check"}""" defaults
    test <@ config.Format = Check @>

// --- parseConfig: lint ---

[<Fact(Timeout = 15000)>]
let ``parseConfig lint true enables lint`` () =
    let config = parseConfig """{"lint": true}""" defaults
    test <@ config.Lint = true @>

[<Fact(Timeout = 15000)>]
let ``parseConfig lint false disables lint`` () =
    let config = parseConfig """{"lint": false}""" defaults
    test <@ config.Lint = false @>

// --- parseConfig: cache ---

[<Fact(Timeout = 15000)>]
let ``parseConfig cache none string returns NoCache`` () =
    let config = parseConfig """{"cache": "none"}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache false string returns NoCache`` () =
    let config = parseConfig """{"cache": "false"}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache false bool returns NoCache`` () =
    let config = parseConfig """{"cache": false}""" defaults
    test <@ config.Cache = NoCache @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache true bool returns defaults cache`` () =
    let defaultsWithMem =
        { defaults with
            Cache = InMemoryOnly 200 }

    let config = parseConfig """{"cache": true}""" defaultsWithMem
    test <@ config.Cache = InMemoryOnly 200 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache memory returns InMemoryOnly 500`` () =
    let config = parseConfig """{"cache": "memory"}""" defaults
    test <@ config.Cache = InMemoryOnly 500 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache file returns FileBackend`` () =
    let config = parseConfig """{"cache": "file"}""" defaults
    test <@ config.Cache = FileBackend @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache jj is treated as file (legacy alias)`` () =
    let config = parseConfig """{"cache": "jj"}""" defaults
    test <@ config.Cache = FileBackend @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache unknown string returns defaults cache`` () =
    let config = parseConfig """{"cache": "redis"}""" defaults
    test <@ config.Cache = FileBackend @>

[<Fact(Timeout = 15000)>]
let ``parseConfig cache missing uses defaults`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Cache = defaults.Cache @>

// --- parseConfig: analyzers ---

[<Fact(Timeout = 15000)>]
let ``parseConfig analyzers with paths`` () =
    let config = parseConfig """{"analyzers": {"paths": ["path1", "path2"]}}""" defaults

    test
        <@
            config.Analyzers = Some
                {| Paths = [ "path1"; "path2" ]
                   FailOnSeverity = DiagnosticSeverity.Hint |}
        @>

[<Fact(Timeout = 15000)>]
let ``analyzers config defaults failOnSeverity to Hint`` () =
    let config = parseConfig """{"analyzers":{"paths":["p1"]}}""" defaults

    test
        <@
            config.Analyzers = Some
                {| Paths = [ "p1" ]
                   FailOnSeverity = DiagnosticSeverity.Hint |}
        @>

[<Fact(Timeout = 15000)>]
let ``analyzers config parses explicit failOnSeverity`` () =
    let config =
        parseConfig """{"analyzers":{"paths":["p1"],"failOnSeverity":"warning"}}""" defaults

    test
        <@
            config.Analyzers = Some
                {| Paths = [ "p1" ]
                   FailOnSeverity = DiagnosticSeverity.Warning |}
        @>

[<Fact(Timeout = 15000)>]
let ``parseConfig analyzers unknown failOnSeverity falls back to Hint`` () =
    let config =
        parseConfig """{"analyzers":{"paths":["p1"],"failOnSeverity":"bogus"}}""" defaults

    test
        <@
            config.Analyzers = Some
                {| Paths = [ "p1" ]
                   FailOnSeverity = DiagnosticSeverity.Hint |}
        @>

[<Fact(Timeout = 15000)>]
let ``parseConfig format string variants land deterministically`` () =
    test <@ (parseConfig """{"format":"auto"}""" defaults).Format = Auto @>
    test <@ (parseConfig """{"format":"check"}""" defaults).Format = Check @>
    test <@ (parseConfig """{"format":"off"}""" defaults).Format = Off @>
    test <@ (parseConfig """{"format":"false"}""" defaults).Format = Off @>
    // Unknown format value falls through to Auto with a warning.
    test <@ (parseConfig """{"format":"weird"}""" defaults).Format = Auto @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build entry parses buildTemplate`` () =
    let config =
        parseConfig """{"build": {"command": "dotnet", "args": "build", "buildTemplate": "$cmd $args"}}""" defaults

    match config.Build with
    | Some [ entry ] -> test <@ entry.BuildTemplate = Some "$cmd $args" @>
    | _ -> failwith "expected single build entry with buildTemplate"

[<Fact(Timeout = 15000)>]
let ``parseConfig analyzers with empty paths returns None`` () =
    let config = parseConfig """{"analyzers": {"paths": []}}""" defaults

    test <@ config.Analyzers = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig analyzers without paths returns None`` () =
    let config = parseConfig """{"analyzers": {}}""" defaults
    test <@ config.Analyzers = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig no analyzers returns None`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.Analyzers = None @>

// --- parseConfig: tests ---

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``parseConfig tests with empty projects returns None`` () =
    let json = """{"tests": {"projects": []}}"""
    let config = parseConfig json defaults
    test <@ config.Tests = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig tests without projects key returns None`` () =
    let json = """{"tests": {}}"""
    let config = parseConfig json defaults
    test <@ config.Tests = None @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``parseConfig tests project with no project key defaults to unknown`` () =
    let json = """{"tests": {"projects": [{}]}}"""
    let config = parseConfig json defaults
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Project = "unknown" @>

// --- parseConfig: coverage plugin ---

[<Fact(Timeout = 15000)>]
let ``parseConfig coverage section parses configPath and searchDir`` () =
    let json =
        """{"coverage": {"configPath": "coverage-ratchet-Proj.json", "searchDir": "artifacts"}}"""

    let config = parseConfig json defaults
    test <@ config.Coverage.IsSome @>
    test <@ config.Coverage.Value.ConfigPath = "coverage-ratchet-Proj.json" @>
    test <@ config.Coverage.Value.SearchDir = "artifacts" @>

[<Fact(Timeout = 15000)>]
let ``parseConfig coverage section defaults configPath and searchDir when absent`` () =
    let json = """{"coverage": {}}"""
    let config = parseConfig json defaults
    test <@ config.Coverage.IsSome @>
    test <@ config.Coverage.Value.ConfigPath = "coverage-ratchet.json" @>
    test <@ config.Coverage.Value.SearchDir = "." @>

[<Fact(Timeout = 15000)>]
let ``parseConfig no coverage section yields None`` () =
    let config = parseConfig "{}" defaults
    test <@ config.Coverage.IsNone @>

// --- parseConfig: tests.coverageDir ---
// Coverage XMLs are emitted under <repoRoot>/<tests.coverageDir>/<project>/ (default "coverage"),
// and ratcheting is driven by fileCommands afterTests invoking an external tool or the coverage plugin.

[<Fact(Timeout = 15000)>]
let ``parseConfig tests without coverageDir defaults to coverage`` () =
    let json = """{"tests": {"projects": [{"project": "T"}]}}"""
    let config = parseConfig json defaults
    test <@ config.Tests.Value.CoverageDir = "coverage" @>

[<Fact(Timeout = 15000)>]
let ``parseConfig tests with explicit coverageDir`` () =
    let json =
        """{"tests": {"coverageDir": "artifacts/cov", "projects": [{"project": "T"}]}}"""

    let config = parseConfig json defaults
    test <@ config.Tests.Value.CoverageDir = "artifacts/cov" @>

// --- parseConfig: fileCommands ---

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``parseConfig fileCommands entry without pattern or afterTests is rejected`` () =
    let json = """{"fileCommands": [{}]}"""

    let ex = Assert.ThrowsAny<exn>(fun () -> parseConfig json defaults |> ignore)

    test <@ ex.Message.Contains("pattern") && ex.Message.Contains("afterTests") @>

[<Fact(Timeout = 15000)>]
let ``parseConfig fileCommands afterTests true parses to AnyTest`` () =
    let json =
        """{"fileCommands": [{"name": "cov", "afterTests": true, "command": "echo", "args": "ran"}]}"""

    let config = parseConfig json defaults
    test <@ config.FileCommands.Length = 1 @>
    test <@ config.FileCommands.[0].PluginName = "cov" @>
    test <@ config.FileCommands.[0].Pattern = None @>

    test <@ config.FileCommands.[0].AfterTests = Some FsHotWatch.FileCommand.FileCommandPlugin.AnyTest @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``parseConfig fileCommands afterTests without name is rejected`` () =
    let json =
        """{"fileCommands": [{"afterTests": true, "command": "echo", "args": "ran"}]}"""

    let ex = Assert.ThrowsAny<exn>(fun () -> parseConfig json defaults |> ignore)

    test <@ ex.Message.Contains("name") @>

[<Fact(Timeout = 15000)>]
let ``parseConfig fileCommands empty array`` () =
    let config = parseConfig """{"fileCommands": []}""" defaults
    test <@ config.FileCommands |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``parseConfig no fileCommands returns empty list`` () =
    let config = parseConfig """{}""" defaults
    test <@ config.FileCommands |> List.isEmpty @>

// --- parseConfig: invalid JSON ---

[<Fact(Timeout = 15000)>]
let ``parseConfig with invalid JSON throws`` () =
    Assert.ThrowsAny<JsonException>(fun () -> parseConfig "not json" defaults |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
let ``parseConfig with malformed JSON throws`` () =
    Assert.ThrowsAny<JsonException>(fun () -> parseConfig """{"build":}""" defaults |> ignore)
    |> ignore

// --- parseConfig: combined configuration ---

[<Fact(Timeout = 15000)>]
let ``parseConfig with full configuration`` () =
    let json =
        """{
        "build": {"command": "make", "args": "all"},
        "format": false,
        "lint": false,
        "cache": "file",
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
                     DependsOn = []
                     TimeoutSec = None |} ]
        @>

    test <@ config.Format = Off @>
    test <@ config.Lint = false @>
    test <@ config.Cache = FileBackend @>

    test
        <@
            config.Analyzers = Some
                {| Paths = [ "/analyzers" ]
                   FailOnSeverity = DiagnosticSeverity.Hint |}
        @>

    test <@ config.Tests.IsSome @>
    test <@ config.FileCommands.Length = 1 @>

// --- detectDefaultCacheBackend ---

[<Fact(Timeout = 15000)>]
let ``detectDefaultCacheBackend returns FileBackend regardless of .jj presence`` () =
    withTempDir "cfg-jj" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let result = detectDefaultCacheBackend tmpDir
        test <@ result = FileBackend @>)

[<Fact(Timeout = 15000)>]
let ``detectDefaultCacheBackend returns FileBackend when no .jj`` () =
    withTempDir "cfg-nojj" (fun tmpDir ->
        let result = detectDefaultCacheBackend tmpDir
        test <@ result = FileBackend @>)

// --- createCacheComponents ---

[<Fact(Timeout = 15000)>]
let ``createCacheComponents NoCache returns None None`` () =
    withTempDir "cfg-cc" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir NoCache
        test <@ backend = None @>
        test <@ keyProvider = None @>)

[<Fact(Timeout = 15000)>]
let ``createCacheComponents InMemoryOnly returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-mem" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir (InMemoryOnly 100)
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``createCacheComponents FileBackend returns Some backend and Some keyProvider`` () =
    withTempDir "cfg-cc-file" (fun tmpDir ->
        let (backend, keyProvider) = createCacheComponents tmpDir FileBackend
        test <@ backend.IsSome @>
        test <@ keyProvider.IsSome @>)

// --- defaultConfigFor ---

[<Fact(Timeout = 15000)>]
let ``loadConfig with no config file returns expected defaults`` () =
    withTempDir "cfg-def" (fun tmpDir ->
        let config = loadConfig tmpDir

        test
            <@
                config.Build = Some
                    [ {| Command = "dotnet"
                         Args = "build"
                         BuildTemplate = None
                         DependsOn = []
                         TimeoutSec = None |} ]
            @>

        test <@ config.Format = Auto @>
        test <@ config.Lint = true @>
        test <@ config.Cache = FileBackend @>
        test <@ config.Analyzers = None @>
        test <@ config.Tests = None @>
        test <@ config.FileCommands |> List.isEmpty @>)

// --- parseConfig: per-project coverage exclusion ---

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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
    test <@ p.CoverageArgsTemplate = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig test project with coverage as object captures argsTemplate`` () =
    // Custom coverage args for an AltCover-style runner — any template that
    // doesn't match the MTP default.
    let json =
        """{
        "tests": {
            "projects": [{
                "project": "UnitTests",
                "coverage": {
                    "enabled": true,
                    "argsTemplate": "--altcover --out \"{output}\""
                }
            }]
        }
    }"""

    let config = parseConfig json defaults
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Coverage = true @>
    test <@ p.CoverageArgsTemplate = Some "--altcover --out \"{output}\"" @>

[<Fact(Timeout = 15000)>]
let ``parseConfig test project with coverage object enabled=false disables coverage`` () =
    let json =
        """{
        "tests": {
            "projects": [{
                "project": "IntTests",
                "coverage": { "enabled": false }
            }]
        }
    }"""

    let config = parseConfig json defaults
    let p = config.Tests.Value.Projects.[0]
    test <@ p.Coverage = false @>
    test <@ p.CoverageArgsTemplate = None @>

// --- parseConfig: build as array ---

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``parseConfig build single object still works`` () =
    let json = """{"build": {"command": "make", "args": "all"}}"""
    let config = parseConfig json defaults

    test <@ config.Build.IsSome @>
    let builds = config.Build.Value
    test <@ builds.Length = 1 @>
    test <@ builds.[0].Command = "make" @>
    test <@ builds.[0].Args = "all" @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build false disables build as empty list`` () =
    let config = parseConfig """{"build": false}""" defaults
    test <@ config.Build.IsSome @>
    test <@ config.Build.Value |> List.isEmpty @>

// --- parseConfig: test extensions ---

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``parseConfig tests without extensions defaults to empty`` () =
    let json =
        """{
        "tests": {
            "projects": [{"project": "Tests"}]
        }
    }"""

    let config = parseConfig json defaults
    test <@ config.Tests.Value.Extensions |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``loadConfig defaults to FileBackend regardless of .jj presence`` () =
    withTempDir "cfg-def-jj" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let config = loadConfig tmpDir
        test <@ config.Cache = FileBackend @>)

// --- parseConfig: build dependsOn ---

[<Fact(Timeout = 15000)>]
let ``parseConfig build with dependsOn`` () =
    let json =
        """{"build": {"command": "dotnet", "args": "build", "dependsOn": ["npm-deps"]}}"""

    let config = parseConfig json defaults

    test <@ config.Build.IsSome @>
    let builds = config.Build.Value
    test <@ builds.Length = 1 @>
    test <@ builds.[0].DependsOn = [ "npm-deps" ] @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build without dependsOn defaults to empty`` () =
    let json = """{"build": {"command": "dotnet", "args": "build"}}"""
    let config = parseConfig json defaults
    let builds = config.Build.Value
    test <@ builds.[0].DependsOn |> List.isEmpty @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build with multiple dependsOn`` () =
    let json = """{"build": {"dependsOn": ["setup", "codegen"]}}"""

    let config = parseConfig json defaults
    let builds = config.Build.Value
    test <@ builds.[0].DependsOn = [ "setup"; "codegen" ] @>

// --- stripConfig tests ---

[<Fact(Timeout = 15000)>]
let ``stripConfig preserves format mode`` () =
    let config = { defaults with Format = Check }
    let stripped = stripConfig config
    test <@ stripped.Format = Check @>

[<Fact(Timeout = 15000)>]
let ``stripConfig disables lint`` () =
    let stripped = stripConfig defaults
    test <@ stripped.Lint = false @>

[<Fact(Timeout = 15000)>]
let ``stripConfig sets build to empty list`` () =
    let stripped = stripConfig defaults
    test <@ stripped.Build = Some [] @>

[<Fact(Timeout = 15000)>]
let ``stripConfig caller can restore build config`` () =
    let stripped =
        { stripConfig defaults with
            Build = defaults.Build }

    test <@ stripped.Build = defaults.Build @>
    test <@ stripped.Build.Value.Length = 1 @>

[<Fact(Timeout = 15000)>]
let ``registerPlugins with build config registers build plugin`` () =
    withTempDir "cfg-build-reg" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

        let config =
            { stripConfig defaults with
                Build = defaults.Build }

        registerPlugins daemon tmpDir config
        let statuses = daemon.Host.GetAllStatuses()
        test <@ statuses.ContainsKey("build") @>)

[<Fact(Timeout = 15000)>]
let ``registerPlugins with stripped config does not register build plugin`` () =
    withTempDir "cfg-build-noreg" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

        let config = stripConfig defaults
        registerPlugins daemon tmpDir config
        let statuses = daemon.Host.GetAllStatuses()
        test <@ not (statuses.ContainsKey("build")) @>)

[<Fact(Timeout = 15000)>]
let ``registerPlugins stores FileCommand pattern on host`` () =
    withTempDir "cfg-fc-register" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

        let config =
            { stripConfig defaults with
                FileCommands =
                    [ {| PluginName = "coverage-ratchet"
                         Pattern = Some "*.ratchet.json"
                         AfterTests = None
                         Command = "echo"
                         Args = "hi"
                         TimeoutSec = None |} ] }

        registerPlugins daemon tmpDir config

        test
            <@
                daemon.Host.GetFileCommandPattern("coverage-ratchet") = Some(
                    FsHotWatch.Watcher.FilePattern.parse "*.ratchet.json"
                )
            @>)

[<Fact(Timeout = 15000)>]
let ``registerPlugins with afterTests-only plugin does not register pattern`` () =
    withTempDir "cfg-fc-aftertests-only" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

        let config =
            { stripConfig defaults with
                FileCommands =
                    [ {| PluginName = "post-test-hook"
                         Pattern = None
                         AfterTests = Some FsHotWatch.FileCommand.FileCommandPlugin.AnyTest
                         Command = "echo"
                         Args = "done"
                         TimeoutSec = None |} ] }

        registerPlugins daemon tmpDir config
        test <@ daemon.Host.GetFileCommandPattern("post-test-hook") = None @>)

// --- loadConfig: strict parse errors ---

[<Fact(Timeout = 15000)>]
let ``loadConfig throws ConfigError on malformed JSON`` () =
    withTempDir "cfg-malformed" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".fshw.json"), "{not valid json")
        let ex = Assert.Throws<ConfigError>(fun () -> loadConfig tmpDir |> ignore)
        Assert.Contains(".fshw.json", ex.Message))

[<Fact(Timeout = 15000)>]
let ``parseConfig raises ConfigError when fileCommands entry lacks pattern and afterTests`` () =
    let json = """{ "fileCommands": [ { "command": "echo", "args": "hi" } ] }"""

    Assert.Throws<ConfigError>(fun () -> parseConfig json defaults |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
let ``parseConfig raises ConfigError when afterTests entry lacks name`` () =
    let json =
        """{ "fileCommands": [ { "afterTests": true, "command": "echo", "args": "hi" } ] }"""

    Assert.Throws<ConfigError>(fun () -> parseConfig json defaults |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
let ``parseConfig raises ConfigError on fileCommands pattern with embedded wildcard`` () =
    // Embedded `*` diverges between FileSystemWatcher.Filter (globs) and
    // FilePattern.matches (literal) — must fail at config-load with a clean
    // ConfigError, not an unhandled ArgumentException at registration time.
    let json =
        """{ "fileCommands": [ { "pattern": "schema.*.sql", "command": "echo", "args": "hi" } ] }"""

    let ex = Assert.Throws<ConfigError>(fun () -> parseConfig json defaults |> ignore)
    Assert.Contains("schema.*.sql", ex.Message)

// --- countPlugins ---

[<Fact(Timeout = 15000)>]
let ``countPlugins counts build lint analyzers tests and fileCommands`` () =
    let cfg =
        { defaults with
            Lint = true
            Analyzers =
                Some
                    {| Paths = [ "some/path" ]
                       FailOnSeverity = DiagnosticSeverity.Hint |}
            Tests =
                Some
                    {| BeforeRun = None
                       Extensions = []
                       Projects = []
                       CoverageDir = "coverage"
                       DependsOn = []
                       SeedTestImpactFrom = None |}
            FileCommands =
                [ {| PluginName = "a"
                     Pattern = Some "*.md"
                     AfterTests = None
                     Command = "echo"
                     Args = ""
                     TimeoutSec = None |}
                  {| PluginName = "b"
                     Pattern = Some "*.fsx"
                     AfterTests = None
                     Command = "echo"
                     Args = ""
                     TimeoutSec = None |} ] }

    // build(1) + lint(1) + analyzers(1) + tests(1) + 2 fileCommands = 6
    test <@ countPlugins cfg = 6 @>

[<Fact(Timeout = 15000)>]
let ``countPlugins returns 0 for stripped config`` () =
    let cfg = stripConfig { defaults with Lint = false }
    test <@ countPlugins cfg = 0 @>

// --- watchConfigFile ---

[<Fact(Timeout = 15000)>]
let ``watchRepoConfigFile returns no-op disposable when no config file exists`` () =
    // No live OS event is awaited here (it asserts the callback does NOT fire),
    // so it stays a parallel module-level fact — only the tests that block on a
    // real FileSystemWatcher delivery are serialized (see RealWatchTests below).
    withTempDir "cfg-watch-none" (fun tmpDir ->
        let mutable called = false
        use w = watchRepoConfigFile tmpDir (fun _ -> called <- true)
        System.Threading.Thread.Sleep(50)
        test <@ not called @>)

// The next three tests block on a live `FileSystemWatcher` OS event. Under
// heavy parallel load the OS can take >5s to deliver, so they're grouped into
// the FileWatch DisableParallelization collection (mirrors MacFsEventsTests)
// to keep delivery deterministic instead of racing the saturated suite.
//
// Coverage note (2026-06-12): these tests are also what pins the watcher
// handler's lines as deterministically covered in this (ratcheted) suite — a
// passing run means the callback fired. The handler's BRANCHES live in the
// extracted `debounceShouldFire`/`configChangeReason` functions (unit-tested
// directly below with injected clocks), so an OS double-fire during these
// tests can only re-hit already-covered branches and the ratchet stays
// deterministic.
[<Collection(FileWatchCollectionName)>]
type RealWatchTests() =

    [<Fact(Timeout = 20000)>]
    member _.``watchConfigFile invokes callback when .fshw.json is written``() =
        withTempDir "cfg-watch-write" (fun tmpDir ->
            let configPath = Path.Combine(tmpDir, ".fshw.json")
            File.WriteAllText(configPath, "{}")

            use signal = new System.Threading.ManualResetEventSlim(false)
            let observed = ref ""

            use _watcher =
                watchConfigFile configPath (fun reason ->
                    observed.Value <- reason
                    signal.Set())

            // Give the FSW a moment to become active.
            System.Threading.Thread.Sleep(100)

            File.WriteAllText(configPath, """{"lint": false}""")

            Assert.True(signal.Wait(5000), "expected watcher callback within 5s")
            test <@ observed.Value.Contains("config") @>)

    [<Fact(Timeout = 20000)>]
    member _.``watchRepoConfigFile watches existing config file``() =
        withTempDir "cfg-watch-existing" (fun tmpDir ->
            File.WriteAllText(Path.Combine(tmpDir, ".fshw.json"), "{}")
            use signal = new System.Threading.ManualResetEventSlim(false)
            use _w = watchRepoConfigFile tmpDir (fun _ -> signal.Set())
            System.Threading.Thread.Sleep(100)
            File.WriteAllText(Path.Combine(tmpDir, ".fshw.json"), """{"lint": false}""")
            Assert.True(signal.Wait(5000), "expected callback within 5s"))

    [<Fact(Timeout = 20000)>]
    member _.``watchConfigFile reports invalid reason when new contents fail to parse``() =
        withTempDir "cfg-watch-invalid" (fun tmpDir ->
            let configPath = Path.Combine(tmpDir, ".fshw.json")
            File.WriteAllText(configPath, "{}")

            use signal = new System.Threading.ManualResetEventSlim(false)
            let observed = ref ""

            use _watcher =
                watchConfigFile configPath (fun reason ->
                    observed.Value <- reason
                    signal.Set())

            System.Threading.Thread.Sleep(100)

            File.WriteAllText(configPath, "{not valid json")

            Assert.True(signal.Wait(5000), "expected watcher callback within 5s")
            Assert.Contains("invalid", observed.Value))

// --- debounceShouldFire / configChangeReason / onConfigFsEvent ---
// Direct tests with injected clocks so BOTH arms of every watcher branch are
// covered deterministically — production only hits the suppressed-debounce arm
// when the OS double-fires within the window, which is nondeterministic and
// used to coin-flip this file's branch coverage in the ratchet.

[<Fact(Timeout = 15000)>]
let ``debounceShouldFire fires on first event and suppresses within the window`` () =
    let gate = obj ()
    let lastFire = ref System.DateTime.MinValue
    let window = System.TimeSpan.FromMilliseconds(200.0)
    let t0 = System.DateTime(2026, 6, 12, 12, 0, 0, System.DateTimeKind.Utc)

    let first = debounceShouldFire gate lastFire window t0
    // 100ms later: inside the window — suppressed.
    let withinWindow =
        debounceShouldFire gate lastFire window (t0.AddMilliseconds(100.0))
    // 300ms after the accepted fire: outside the window — fires again.
    let afterWindow =
        debounceShouldFire gate lastFire window (t0.AddMilliseconds(300.0))

    test <@ first && not withinWindow && afterWindow @>

[<Fact(Timeout = 15000)>]
let ``configChangeReason distinguishes valid from invalid config`` () =
    withTempDir "cfg-reason" (fun tmpDir ->
        let configPath = Path.Combine(tmpDir, ".fshw.json")

        File.WriteAllText(configPath, """{"lint": false}""")
        test <@ (configChangeReason configPath defaults).Contains("config changed") @>

        File.WriteAllText(configPath, "{not valid json")
        test <@ (configChangeReason configPath defaults).Contains("config invalid") @>)

[<Fact(Timeout = 15000)>]
let ``onConfigFsEvent dispatches once per debounce window`` () =
    withTempDir "cfg-fsevent" (fun tmpDir ->
        let configPath = Path.Combine(tmpDir, ".fshw.json")
        File.WriteAllText(configPath, "{}")

        let gate = obj ()
        let lastFire = ref System.DateTime.MinValue
        let window = System.TimeSpan.FromMilliseconds(200.0)
        let t0 = System.DateTime(2026, 6, 12, 12, 0, 0, System.DateTimeKind.Utc)
        let calls = ResizeArray<string>()

        let fire now =
            onConfigFsEvent gate lastFire window configPath defaults ignore (fun r -> calls.Add(r)) now

        fire t0 // fires
        fire (t0.AddMilliseconds(50.0)) // double-fire within window: suppressed
        fire (t0.AddMilliseconds(500.0)) // next save: fires

        test <@ calls.Count = 2 @>
        test <@ calls |> Seq.forall (fun r -> r.Contains("config changed")) @>)

[<Fact(Timeout = 15000)>]
let ``invokeOnChangeWith routes onChange exception to logError sink (F3)`` () =
    // F3 (audit/2026-05-02): the watcher previously did `try onChange reason with _ -> ()`,
    // silently swallowing any exception from the daemon-stop callback. A user editing
    // .fshw.json would see no effect with no log line. The dispatch is now extracted
    // so we can inject a logError sink directly — no stderr capture race in parallel.
    let captured = ResizeArray<string>()

    let sink (msg: string) =
        lock captured (fun () -> captured.Add(msg))

    invokeOnChangeWith sink (fun _reason -> failwith "boom from onChange") "edit-1"

    test <@ captured.Count = 1 @>
    test <@ captured.[0].Contains("onChange callback failed") @>
    test <@ captured.[0].Contains("boom from onChange") @>

[<Fact(Timeout = 15000)>]
let ``invokeOnChangeWith does not invoke logError on success (F3)`` () =
    let captured = ResizeArray<string>()

    let sink (msg: string) =
        lock captured (fun () -> captured.Add(msg))

    let mutable called = ""

    invokeOnChangeWith sink (fun reason -> called <- reason) "edit-ok"

    test <@ called = "edit-ok" @>
    test <@ captured.Count = 0 @>

// --- parseConfig: timeoutSec ---

[<Fact(Timeout = 15000)>]
let ``parseConfig top-level timeoutSec lands on config`` () =
    let config = parseConfig """{"timeoutSec": 42}""" defaults
    test <@ config.TimeoutSec = Some 42 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig top-level timeoutSec absent is None`` () =
    let config = parseConfig "{}" defaults
    test <@ config.TimeoutSec = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig build entry timeoutSec lands on build entry`` () =
    let config =
        parseConfig """{"build": {"command": "dotnet", "args": "build", "timeoutSec": 300}}""" defaults

    match config.Build with
    | Some [ b ] -> test <@ b.TimeoutSec = Some 300 @>
    | other -> failwithf "expected one build entry, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseConfig test project timeoutSec lands on project`` () =
    let json =
        """{"tests": {"projects": [{"project": "Tests.fsproj", "timeoutSec": 600}]}}"""

    let config = parseConfig json defaults

    match config.Tests with
    | Some t ->
        match t.Projects with
        | [ p ] -> test <@ p.TimeoutSec = Some 600 @>
        | _ -> failwith "expected one project"
    | None -> failwith "expected tests"

[<Fact(Timeout = 15000)>]
let ``parseConfig fileCommand timeoutSec lands on entry`` () =
    let json =
        """{"fileCommands": [{"pattern": "*.md", "command": "echo", "args": "hi", "timeoutSec": 60}]}"""

    let config = parseConfig json defaults

    match config.FileCommands with
    | [ fc ] -> test <@ fc.TimeoutSec = Some 60 @>
    | _ -> failwith "expected one file command"

// ---------------------------------------------------------------------------
// FcsSuppressedCodes — daemon must not embed project-level policy
// ---------------------------------------------------------------------------
//
// Regression: the daemon used to default `FcsSuppressedCodes` to `[1182]`
// (silencing F# warning FS1182, "unused binding") because one downstream
// project (SqlHydra) generated noisy code. That embedded a project-level
// policy in the daemon — the wrong layer. Projects that hit FS1182 now
// declare it explicitly via `<NoWarn>FS1182</NoWarn>` in the fsproj or
// `#nowarn "1182"` in source. The daemon-level default is empty.

[<Fact(Timeout = 5000)>]
let ``FcsSuppressedCodes default resolves to empty Set when not configured`` () =
    let resolved =
        Daemon.resolveFcsSuppressedCodes Daemon.DaemonOptions.defaults.FcsSuppressedCodes

    test <@ resolved = Set.empty @>

[<Fact(Timeout = 5000)>]
let ``FcsSuppressedCodes None resolves to empty Set`` () =
    let resolved = Daemon.resolveFcsSuppressedCodes None
    test <@ resolved = Set.empty @>

[<Fact(Timeout = 5000)>]
let ``FcsSuppressedCodes Some resolves to that Set`` () =
    let resolved = Daemon.resolveFcsSuppressedCodes (Some [ 42; 99 ])
    test <@ resolved = Set.ofList [ 42; 99 ] @>

// ---------------------------------------------------------------------------
// shellInvocation — shell-hook command dispatch
// ---------------------------------------------------------------------------
//
// Regression: `beforeRun`/hooks used to run the user's command via
// `splitCommand` → `runProcess`, which tokenizes but doesn't invoke a shell.
// That silently ignored `&&`, `|`, `$VAR`, etc., because those are shell
// metacharacters, not arguments. Now we dispatch through `/bin/sh -c`
// (unix) or `cmd /C` (windows) so the string is interpreted as a shell
// command.

[<Fact(Timeout = 2000)>]
let ``shellInvocation wraps with /bin/sh -c`` () =
    let (cmd, args) =
        FsHotWatch.Cli.DaemonConfig.shellInvocation "echo hi && echo there"

    test <@ cmd = "/bin/sh" @>
    test <@ args.StartsWith("-c ") @>
    test <@ args.Contains("echo hi && echo there") @>

[<Fact(Timeout = 2000)>]
let ``shellInvocation escapes double quotes in the passed command`` () =
    // Inside the -c string, embedded double quotes must be backslash-escaped
    // so the outer `"..."` quoting the whole command stays balanced.
    let (_, args) = FsHotWatch.Cli.DaemonConfig.shellInvocation "echo \"hello world\""

    test <@ args.Contains("\\\"hello world\\\"") @>

// --- resolveExistingPathsWithRetry ---

[<Fact(Timeout = 2000)>]
let ``resolveExistingPathsWithRetry returns all paths when all exist on first attempt`` () =
    let mutable sleepCount = 0
    let dirExists _ = true
    let sleep _ = sleepCount <- sleepCount + 1

    let result = resolveExistingPathsWithRetry dirExists sleep [ "/a"; "/b"; "/c" ]

    test <@ result = [ "/a"; "/b"; "/c" ] @>
    test <@ sleepCount = 0 @>

[<Fact(Timeout = 2000)>]
let ``resolveExistingPathsWithRetry retries when paths transiently missing`` () =
    // Simulate a workspace race: 1st batch reports false, 2nd reports true.
    let mutable callsBeforeSucceed = 3 // 3 paths × 1 batch = 3 calls before flipping
    let mutable sleepCount = 0

    let dirExists _ =
        if callsBeforeSucceed > 0 then
            callsBeforeSucceed <- callsBeforeSucceed - 1
            false
        else
            true

    let sleep _ = sleepCount <- sleepCount + 1

    let result = resolveExistingPathsWithRetry dirExists sleep [ "/a"; "/b"; "/c" ]

    test <@ result.Length = 3 @>
    test <@ sleepCount >= 1 @>

[<Fact(Timeout = 2000)>]
let ``resolveExistingPathsWithRetry gives up after 3 attempts when paths still missing`` () =
    let mutable sleepCount = 0
    let dirExists _ = false
    let sleep _ = sleepCount <- sleepCount + 1

    let result = resolveExistingPathsWithRetry dirExists sleep [ "/a"; "/b" ]

    test <@ List.isEmpty result @>
    test <@ sleepCount = 3 @>

[<Fact(Timeout = 2000)>]
let ``resolveExistingPathsWithRetry returns subset when some paths permanently missing`` () =
    let mutable sleepCount = 0
    let dirExists path = path = "/exists"
    let sleep _ = sleepCount <- sleepCount + 1

    let result =
        resolveExistingPathsWithRetry dirExists sleep [ "/exists"; "/missing"; "/also-missing" ]

    test <@ result = [ "/exists" ] @>
    test <@ sleepCount = 3 @>

[<Fact(Timeout = 2000)>]
let ``resolveExistingPathsWithRetry handles empty input without sleeping`` () =
    let mutable sleepCount = 0
    let dirExists _ = false
    let sleep _ = sleepCount <- sleepCount + 1

    let result = resolveExistingPathsWithRetry dirExists sleep []
    test <@ List.isEmpty result @>
    test <@ sleepCount = 0 @>
