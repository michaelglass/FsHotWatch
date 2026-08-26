module FsHotWatch.Tests.DaemonConfigTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli.DaemonConfig
open FsHotWatch.Daemon
open FsHotWatch.ErrorLedger
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.WatchedDir

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
    test <@ config.Cache = NoCache @>
    test <@ config.Analyzers = None @>
    test <@ config.Tests = None @>
    test <@ config.FileCommands |> List.isEmpty @>
    test <@ config.Exclude |> List.isEmpty @>
    test <@ config.LogDir = "logs" @>

// --- Global timeoutSec default (AUTOMATION-15 item 1) ---

[<Fact(Timeout = 15000)>]
let ``loadConfig on a repo with no .fshw.json applies the baked-in default timeout (not infinite)`` () =
    // The primary wedge cure: with no config at all the bound must be finite, not
    // Infinite. This is the production path (defaultConfigFor), distinct from
    // parseConfig's "inherit the caller's defaults" fallback.
    let tmp =
        Path.Combine(Path.GetTempPath(), $"fshw-noconfig-{System.Guid.NewGuid():N}")

    Directory.CreateDirectory(tmp) |> ignore

    try
        let config = loadConfig tmp
        test <@ config.TimeoutSec = Some DefaultGlobalTimeoutSec @>
        test <@ DefaultGlobalTimeoutSec = 600 @>
    finally
        Directory.Delete(tmp, true)

[<Fact(Timeout = 15000)>]
let ``parseConfig timeoutSec 0 disables the global default (opt-out to unbounded)`` () =
    let config = parseConfig """{"timeoutSec": 0}""" defaults
    test <@ config.TimeoutSec = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig timeoutSec false disables the global default`` () =
    let config = parseConfig """{"timeoutSec": false}""" defaults
    test <@ config.TimeoutSec = None @>

[<Fact(Timeout = 15000)>]
let ``positive global timeoutSec flows to build/tests/fileCommands entries that omit their own`` () =
    let json =
        """{ "timeoutSec": 45,
             "build": { "command": "dotnet", "args": "build" },
             "tests": { "projects": [ { "project": "P" } ] },
             "fileCommands": [ { "name": "fc", "pattern": "*.sql", "command": "echo" } ] }"""

    let config = parseConfig json defaults
    // Per-entry overrides stay None and inherit at registration time, via
    // `b.TimeoutSec |> Option.orElse config.TimeoutSec` in registerPlugins.
    test <@ config.TimeoutSec = Some 45 @>

    match config.Build with
    | Some [ b ] -> test <@ (b.TimeoutSec |> Option.orElse config.TimeoutSec) = Some 45 @>
    | other -> failwithf "expected one build entry, got %A" other

    match config.Tests with
    | Some t ->
        match t.Projects with
        | [ p ] -> test <@ (p.TimeoutSec |> Option.orElse config.TimeoutSec) = Some 45 @>
        | other -> failwithf "expected one test project, got %A" other
    | None -> failwith "expected tests config"

    match config.FileCommands with
    | [ fc ] -> test <@ (fc.TimeoutSec |> Option.orElse config.TimeoutSec) = Some 45 @>
    | other -> failwithf "expected one fileCommand, got %A" other

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
    let config = parseConfig """{"fsEventsLatencyMs": -10}""" defaults
    test <@ config.FsEventsLatencyMs = 250 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig fsEventsLatencyMs non-numeric falls back to default 250`` () =
    let config = parseConfig """{"fsEventsLatencyMs": "nope"}""" defaults
    test <@ config.FsEventsLatencyMs = 250 @>

// --- parseConfig: run-level hooks (AUTOMATION-188) ---

[<Fact(Timeout = 15000)>]
let ``parseConfig run-level beforeRun/afterRun/runHookTimeoutSec round-trip to Some`` () =
    let config =
        parseConfig """{"beforeRun": "acquire-lock", "afterRun": "release-lock", "runHookTimeoutSec": 30}""" defaults

    test <@ config.BeforeRun = Some "acquire-lock" @>
    test <@ config.AfterRun = Some "release-lock" @>
    test <@ config.RunHookTimeoutSec = Some 30 @>

[<Fact(Timeout = 15000)>]
let ``parseConfig run-level hooks absent yield None`` () =
    let config = parseConfig "{}" defaults
    test <@ config.BeforeRun = None @>
    test <@ config.AfterRun = None @>
    test <@ config.RunHookTimeoutSec = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig run-level hook set to false yields None (opt-out without deleting)`` () =
    let config = parseConfig """{"beforeRun": false, "afterRun": false}""" defaults
    test <@ config.BeforeRun = None @>
    test <@ config.AfterRun = None @>

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookTimeoutSec false or zero yields None (falls through the chain)`` () =
    let viaFalse = parseConfig """{"runHookTimeoutSec": false}""" defaults
    test <@ viaFalse.RunHookTimeoutSec = None @>
    let viaZero = parseConfig """{"runHookTimeoutSec": 0}""" defaults
    test <@ viaZero.RunHookTimeoutSec = None @>

// --- parseConfig: runHookCommands (which verbs the run-level hooks bracket) ---

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookCommands absent brackets BOTH verbs`` () =
    // The compatibility property: this key is a pure addition, so an fshw upgrade must
    // never silently stop bracketing a run that used to be bracketed.
    let config = parseConfig "{}" defaults
    test <@ config.RunHookCommands = DefaultRunHookCommands @>
    test <@ config.RunHookCommands = Set.ofList [ RunHookCommand.Check; RunHookCommand.Confirm ] @>

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookCommands confirm-only selects confirm and NOT check`` () =
    let config = parseConfig """{"runHookCommands": ["confirm"]}""" defaults
    test <@ config.RunHookCommands = Set.singleton RunHookCommand.Confirm @>
    test <@ not (Set.contains RunHookCommand.Check config.RunHookCommands) @>

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookCommands accepts both verbs explicitly, in any case or spacing`` () =
    let config = parseConfig """{"runHookCommands": ["CONFIRM", " Check "]}""" defaults
    test <@ config.RunHookCommands = DefaultRunHookCommands @>

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookCommands empty array is legal and brackets nothing`` () =
    // An explicit `[]` is HONOURED, matching the opt-out idiom of the sibling run-hook
    // keys. It is the only input that can disable bracketing.
    let config = parseConfig """{"runHookCommands": []}""" defaults
    test <@ Set.isEmpty config.RunHookCommands @>

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookCommands falls back to BOTH when nothing parses`` () =
    // A typo must never un-gate: unrecognised entries would leave the set empty and
    // silently disable the bracket, so a non-empty array yielding nothing usable
    // resolves to the safe default instead.
    let config = parseConfig """{"runHookCommands": ["chekc", "confrim"]}""" defaults
    test <@ config.RunHookCommands = DefaultRunHookCommands @>

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookCommands keeps the verbs it understood, dropping a typo`` () =
    let config = parseConfig """{"runHookCommands": ["confirm", "chekc"]}""" defaults
    test <@ config.RunHookCommands = Set.singleton RunHookCommand.Confirm @>

[<Fact(Timeout = 15000)>]
let ``parseConfig runHookCommands of the wrong type falls back to BOTH`` () =
    for json in
        [ """{"runHookCommands": "confirm"}"""
          """{"runHookCommands": 3}"""
          """{"runHookCommands": {"confirm": true}}"""
          """{"runHookCommands": false}"""
          """{"runHookCommands": null}""" ] do
        let config = parseConfig json defaults
        test <@ config.RunHookCommands = DefaultRunHookCommands @>

[<Fact(Timeout = 15000)>]
let ``parseConfig keeps the run-level beforeRun separate from tests.beforeRun`` () =
    // Top-level `beforeRun` is run-level; `tests.beforeRun` is per test run. Different
    // scopes, and they must not fold into each other.
    let config =
        parseConfig
            """{"beforeRun": "run-level", "tests": {"beforeRun": "test-level", "projects": [{"project": "P"}]}}"""
            defaults

    test <@ config.BeforeRun = Some "run-level" @>
    // AUTOMATION-320: a string in the config is now a ONE-STEP chain.
    test <@ config.Tests |> Option.map (fun t -> t.BeforeRun) = Some(Some [ "test-level" ]) @>

// --- parseConfig: includeOutsideRepo ---

[<Fact(Timeout = 15000)>]
let ``parseConfig includeOutsideRepo true overrides the default`` () =
    let config = parseConfig """{"includeOutsideRepo": true}""" defaults
    test <@ config.IncludeOutsideRepo = true @>

[<Fact(Timeout = 15000)>]
let ``parseConfig includeOutsideRepo false is honored`` () =
    let config = parseConfig """{"includeOutsideRepo": false}""" defaults
    test <@ config.IncludeOutsideRepo = false @>

[<Fact(Timeout = 15000)>]
let ``parseConfig includeOutsideRepo absent defaults to false`` () =
    let config = parseConfig "{}" defaults
    test <@ config.IncludeOutsideRepo = false @>

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
    let config = parseConfig """{"idleExitMin": true}""" defaults
    test <@ config.IdleExitMin = FsHotWatch.IdleExit.IdleExitConfig.Disabled @>

[<Fact(Timeout = 15000)>]
let ``parseConfig idleExitMin non-numeric string yields Absent`` () =
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

// `"cache": "file"` / `"jj"` selected an on-disk FCS check cache that could never
// produce a hit. A warning was not enough — it scrolls past in a 10-minute gate, and the
// dead key sat in a real repo's .fshw.json for weeks. Dead config now FAILS the load.
[<Theory(Timeout = 15000)>]
[<InlineData("file")>]
[<InlineData("jj")>]
let ``parseConfig raises ConfigError on the removed file cache backend`` (value: string) =
    let ex =
        Assert.Throws<ConfigError>(fun () -> parseConfig $$"""{"cache": "{{value}}"}""" defaults |> ignore)

    // Without the offending value and both fixes in the message, a hard failure is just
    // a worse warning.
    Assert.Contains("has been REMOVED", ex.Message)
    Assert.Contains(value, ex.Message)
    Assert.Contains("\"cache\": \"memory\"", ex.Message)

[<Fact(Timeout = 15000)>]
let ``parseConfig cache unknown string returns defaults cache`` () =
    let config = parseConfig """{"cache": "redis"}""" defaults
    test <@ config.Cache = defaults.Cache @>

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
    test <@ tests.BeforeRun = Some [ "dotnet build" ] @>
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
// Coverage XMLs land under <repoRoot>/<tests.coverageDir>/<project>/ (default
// "coverage"); ratcheting is driven by a fileCommands afterTests hook or the coverage
// plugin.

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

[<Fact(Timeout = 15000)>]
let ``parseConfig tests reportVerificationFormat parses auto/ctrf/off and warns on unknown`` () =
    let parseFmt (v: string) =
        let json =
            """{"tests": {"projects": [{"project": "T", "reportVerificationFormat": "__V__"}]}}""".Replace("__V__", v)

        (parseConfig json defaults).Tests.Value.Projects.[0].ReportVerificationFormat

    let absent =
        (parseConfig """{"tests": {"projects": [{"project": "T"}]}}""" defaults)
            .Tests.Value.Projects.[0].ReportVerificationFormat

    test <@ absent = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect @>
    test <@ parseFmt "auto" = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect @>
    test <@ parseFmt "ctrf" = FsHotWatch.TestPrune.TestPrunePlugin.Ctrf @>
    test <@ parseFmt "off" = FsHotWatch.TestPrune.TestPrunePlugin.Disabled @>
    test <@ parseFmt "bogus" = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect @>

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
        "cache": "memory",
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
    test <@ config.Cache = InMemoryOnly 500 @>

    test
        <@
            config.Analyzers = Some
                {| Paths = [ "/analyzers" ]
                   FailOnSeverity = DiagnosticSeverity.Hint |}
        @>

    test <@ config.Tests.IsSome @>
    test <@ config.FileCommands.Length = 1 @>

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
        test <@ config.Cache = NoCache @>
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
    // An AltCover-style template, i.e. one that doesn't match the MTP default.
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
let ``loadConfig defaults to NoCache regardless of .jj presence`` () =
    withTempDir "cfg-def-jj" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, ".jj")) |> ignore
        let config = loadConfig tmpDir
        test <@ config.Cache = NoCache @>)

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
let ``stripConfig preserves the run-level hooks (run-once must honor them)`` () =
    // `--run-once` is the transport CI uses, so the run-level gate-lock the hooks bracket
    // must survive the strip untouched.
    let withHooks =
        { defaults with
            BeforeRun = Some "acquire-lock"
            AfterRun = Some "release-lock"
            RunHookTimeoutSec = Some 45 }

    let stripped = stripConfig withHooks
    test <@ stripped.BeforeRun = Some "acquire-lock" @>
    test <@ stripped.AfterRun = Some "release-lock" @>
    test <@ stripped.RunHookTimeoutSec = Some 45 @>

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
let ``registerPlugins raises ConfigError when configured analyzers load zero`` () =
    // A non-existent dir is the actual CI bug (bin built in the wrong config). The
    // alternative to raising is registering a do-nothing plugin that lets the gate pass.
    withTempDir "cfg-analyzers-zero" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

        let config =
            { stripConfig defaults with
                Analyzers =
                    Some
                        {| Paths = [ "no-such-analyzer-bin-dir" ]
                           FailOnSeverity = DiagnosticSeverity.Hint |} }

        let ex = Assert.Throws<ConfigError>(fun () -> registerPlugins daemon tmpDir config)

        Assert.Contains("loaded 0 analyzers", ex.Message)
        Assert.Contains("no-such-analyzer-bin-dir", ex.Message)
        test <@ not (daemon.Host.GetAllStatuses().ContainsKey("analyzers")) @>)

[<Fact(Timeout = 15000)>]
let ``registerPlugins with unconfigured analyzers does not raise or register`` () =
    // No analyzers requested ⇒ the guard never fires (no misconfiguration).
    withTempDir "cfg-analyzers-none" (fun tmpDir ->
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore

        let daemon =
            Daemon.createWith (Unchecked.defaultof<_>) tmpDir Daemon.DaemonOptions.defaults

        let config = stripConfig defaults
        test <@ config.Analyzers = None @>

        registerPlugins daemon tmpDir config
        test <@ not (daemon.Host.GetAllStatuses().ContainsKey("analyzers")) @>)

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
    // An embedded `*` diverges between FileSystemWatcher.Filter (glob) and
    // FilePattern.matches (literal). Failing at config-load beats the unhandled
    // ArgumentException it used to throw at registration time.
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
                       Excluded = []
                       Solution = None
                       CoverageDir = "coverage"
                       DependsOn = [] |}
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
    // Asserts the callback does NOT fire, so it awaits no OS event and can stay a
    // parallel module-level fact. Only RealWatchTests below need serializing.
    withTempDir "cfg-watch-none" (fun tmpDir ->
        let mutable called = false
        use w = watchRepoConfigFile tmpDir (fun _ -> called <- true)
        System.Threading.Thread.Sleep(50)
        test <@ not called @>)

// The next three tests block on a live `FileSystemWatcher` OS event, which under heavy
// parallel load can take >5s to deliver — hence the DisableParallelization collection.
// They also pin the watcher handler's LINES as covered; its BRANCHES are covered by the
// injected-clock unit tests below, so an OS double-fire here can only re-hit already
// covered branches and the ratchet stays deterministic.
[<Collection(FileWatchCollectionName)>]
type RealWatchTests() =

    // All three go through `withWatchedDir`, whose body gets no path to write to and
    // exactly one mutation — `WriteUntil`, which rewrites until the callback fires. The
    // shape these used to have (sleep 100ms, write once, `signal.Wait(5000)`) is not
    // expressible against that fixture: it was a coin flip against an unbounded window of FSEvents
    // cold-start latency on a fresh temp dir.
    [<Fact(Timeout = 20000)>]
    member _.``watchConfigFile invokes callback when .fshw.json is written``() =
        use signal = new System.Threading.ManualResetEventSlim(false)
        let observed = ref ""

        withWatchedDir
            "cfg-watch-write"
            (fun dir ->
                watchConfigFile (dir.Seed(".fshw.json", "{}")) (fun reason ->
                    observed.Value <- reason
                    signal.Set()))
            (fun dir ->
                let fired =
                    dir.WriteUntil(".fshw.json", """{"lint": false}""", (fun () -> signal.IsSet))

                Assert.True(fired, $"expected watcher callback within %d{WatchedDir.DefaultProbeTimeoutMs / 1000}s")
                test <@ observed.Value.Contains("config") @>)

    [<Fact(Timeout = 20000)>]
    member _.``watchRepoConfigFile watches existing config file``() =
        use signal = new System.Threading.ManualResetEventSlim(false)

        withWatchedDir
            "cfg-watch-existing"
            (fun dir ->
                dir.Seed(".fshw.json", "{}") |> ignore
                watchRepoConfigFile dir.Root (fun _ -> signal.Set()))
            (fun dir ->
                let fired =
                    dir.WriteUntil(".fshw.json", """{"lint": false}""", (fun () -> signal.IsSet))

                Assert.True(fired, $"expected callback within %d{WatchedDir.DefaultProbeTimeoutMs / 1000}s"))

    [<Fact(Timeout = 20000)>]
    member _.``watchConfigFile reports invalid reason when new contents fail to parse``() =
        use signal = new System.Threading.ManualResetEventSlim(false)
        let observed = ref ""

        withWatchedDir
            "cfg-watch-invalid"
            (fun dir ->
                watchConfigFile (dir.Seed(".fshw.json", "{}")) (fun reason ->
                    observed.Value <- reason
                    signal.Set()))
            (fun dir ->
                let fired =
                    dir.WriteUntil(".fshw.json", "{not valid json", (fun () -> signal.IsSet))

                Assert.True(fired, $"expected watcher callback within %d{WatchedDir.DefaultProbeTimeoutMs / 1000}s")
                Assert.Contains("invalid", observed.Value))

// --- debounceShouldFire / configChangeReason / onConfigFsEvent ---
// Injected clocks so BOTH arms of every watcher branch are covered deterministically.
// Production reaches the suppressed-debounce arm only on an OS double-fire inside the
// window, which used to coin-flip this file's branch coverage in the ratchet.

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
    // The watcher used to do `try onChange reason with _ -> ()`, so an exception from the
    // daemon-stop callback vanished: editing .fshw.json had no effect and no log line.
    // The dispatch is extracted so the sink can be injected, avoiding a stderr-capture
    // race under parallel run.
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
// FcsSuppressedCodes — the daemon-level default must stay EMPTY. It used to default to
// `[1182]` to silence one downstream project's noisy generated code, which put a
// project-level policy in the daemon. Projects that need it declare `<NoWarn>` or
// `#nowarn` themselves.
// ---------------------------------------------------------------------------

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
// shellInvocation — hooks used to run through `splitCommand` → `runProcess`, which
// tokenizes without invoking a shell, so `&&`, `|` and `$VAR` were silently ignored.
// Dispatch is now `/bin/sh -c` (unix) or `cmd /C` (windows).
// ---------------------------------------------------------------------------

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

// ---------------------------------------------------------------------------
// makeShellHookWithResult — `beforeRun` runs INSIDE the `RunExclusive "tests"` slot, so a
// hook that hangs holds that slot forever: TestPrune stays `Running`, every later `check`
// burns its full deadline, and only a daemon restart recovers. It used to run through
// `runProcess` with `InfiniteTimeSpan`. Two distinct ways to hang, one test each.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``a beforeRun hook that hangs TIMES OUT instead of wedging the tests slot`` () =
    // To see this go red, pass `None` for the timeout (the old InfiniteTimeSpan
    // behaviour): it then blocks for 60s, past the xUnit budget.
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let hook =
        FsHotWatch.Cli.DaemonConfig.makeShellHookWithResult "beforeRun" (Some 1) "." "sleep 60"

    let (success, output) = hook ()
    sw.Stop()

    test <@ not success @>
    test <@ output.Contains("timed out") @>

    Assert.True(
        sw.Elapsed < System.TimeSpan.FromSeconds 15.0,
        $"hook was not bounded: took %.1f{sw.Elapsed.TotalSeconds}s"
    )

[<Fact(Timeout = 20000)>]
let ``a beforeRun hook whose grandchild holds the stdout pipe still returns`` () =
    // The shape a timeout alone would not have caught quickly: the hook exits
    // immediately and successfully, but a grandchild it spawned (an MSBuild node, a
    // Playwright driver — here a backgrounded `sleep`) inherited the stdout pipe and
    // holds it open. The old success-path `Task.WaitAll` waited on an EOF that never
    // came, so a hook that had already SUCCEEDED never returned.
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let hook =
        FsHotWatch.Cli.DaemonConfig.makeShellHookWithResult "beforeRun" (Some 60) "." "( sleep 30 & ) ; echo ready"

    let (success, output) = hook ()
    sw.Stop()

    test <@ success @>
    test <@ output.Contains("ready") @>

    Assert.True(
        sw.Elapsed < System.TimeSpan.FromSeconds 15.0,
        $"hook waited on a grandchild-held pipe for a child that had already exited: \
          took %.1f{sw.Elapsed.TotalSeconds}s"
    )

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

// --- analyzerPathFailures: per-path fail-loud guard for silent-skip paths ---

[<Fact(Timeout = 2000)>]
let ``analyzerPathFailures fires per-path: one path loads, one loads zero`` () =
    // The shape the old aggregate (total == 0) guard MISSED: some paths load, one
    // silently loads 0.
    let result =
        analyzerPathFailures
            [ "/repo/good/bin", 3 // loads fine
              "/repo/empty/bin", 0 // exists but no analyzer DLLs
              "/repo/missing/bin", 0 ] // path doesn't exist

    test <@ result.IsSome @>
    test <@ result.Value.Contains("/repo/empty/bin") @>
    test <@ result.Value.Contains("/repo/missing/bin") @>
    test <@ not (result.Value.Contains("/repo/good/bin")) @>
    test <@ result.Value.Contains(".fshw.json analyzers.paths") @>

[<Fact(Timeout = 2000)>]
let ``analyzerPathFailures fires when every configured path loads zero`` () =
    let single = analyzerPathFailures [ "/repo/a/bin", 0 ]
    test <@ single.IsSome @>
    test <@ single.Value.Contains("/repo/a/bin") @>

    let multiple =
        analyzerPathFailures [ "/repo/a/bin", 0; "/repo/b/bin", 0; "/repo/c/bin", 0 ]

    test <@ multiple.IsSome @>
    test <@ multiple.Value.Contains("/repo/a/bin") @>
    test <@ multiple.Value.Contains("/repo/b/bin") @>
    test <@ multiple.Value.Contains("/repo/c/bin") @>

[<Fact(Timeout = 2000)>]
let ``analyzerPathFailures silent when every configured path loads at least one`` () =
    test <@ (analyzerPathFailures [ "/repo/a/bin", 1 ]).IsNone @>
    test <@ (analyzerPathFailures [ "/repo/a/bin", 2; "/repo/b/bin", 1 ]).IsNone @>
    test <@ (analyzerPathFailures [ "/repo/a/bin", 5; "/repo/b/bin", 7 ]).IsNone @>

[<Fact(Timeout = 2000)>]
let ``analyzerPathFailures silent when analyzers unconfigured`` () =
    test <@ (analyzerPathFailures []).IsNone @>

// --- loadConfig: AUTOMATION-158, the test scope must cover the solution ---
//
// THE REGRESSION THE TICKET ASKS FOR, at the boundary that matters: a dummy test
// project staged into the solution and left out of `.fshw.json` must make the
// config load FAIL. `loadConfig` is what every fshw verb calls, in the CLI
// process, on every invocation — so a run cannot reach a suite, let alone a
// verdict, over a scope that does not cover the solution.

/// Stage a repo whose solution holds one gated suite and one that is not gated.
let private stageSolutionRepo (root: string) (configJson: string) =
    let testProject =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>
</Project>"""

    File.WriteAllText(
        Path.Combine(root, "Repo.slnx"),
        "<Solution>\n\
         \  <Project Path=\"tests/App.Tests/App.Tests.fsproj\" />\n\
         \  <Project Path=\"tests/App.Dummy.Tests/App.Dummy.Tests.fsproj\" />\n\
         </Solution>\n"
    )

    for name in [ "App.Tests"; "App.Dummy.Tests" ] do
        let dir = Path.Combine(root, "tests", name)
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(Path.Combine(dir, $"%s{name}.fsproj"), testProject)

    File.WriteAllText(Path.Combine(root, ".fshw.json"), configJson)

let private gatingOnlyAppTests =
    """{ "tests": { "projects": [ { "project": "App.Tests",
                                   "args": "run --project tests/App.Tests --no-build --" } ] } }"""

[<Fact(Timeout = 30000)>]
let ``loadConfig REFUSES a config whose test scope omits a solution test project`` () =
    withTempDir "cfg-scope-undeclared" (fun tmpDir ->
        stageSolutionRepo tmpDir gatingOnlyAppTests

        let ex = Assert.Throws<ConfigError>(fun () -> loadConfig tmpDir |> ignore)

        // Names the project, the solution, and both ways out — a refusal nobody
        // can act on is its own kind of silence.
        Assert.Contains("tests/App.Dummy.Tests", ex.Message)
        Assert.Contains("Repo.slnx", ex.Message)
        Assert.Contains("tests.excluded", ex.Message))

[<Fact(Timeout = 30000)>]
let ``loadConfig ACCEPTS the same repo once the omission is declared with a reason`` () =
    // The other direction of the mutation. Without this, "fail closed" could be
    // satisfied by a check that never passes, and the escape hatch the ticket
    // blesses would not exist.
    withTempDir "cfg-scope-declared" (fun tmpDir ->
        let json =
            """{ "tests": { "projects": [ { "project": "App.Tests",
                                            "args": "run --project tests/App.Tests --no-build --" } ],
                            "excluded": [ { "project": "tests/App.Dummy.Tests",
                                            "reason": "slow end-to-end suite; run by `mise run test-integration`" } ] } }"""

        stageSolutionRepo tmpDir json

        let config = loadConfig tmpDir

        test <@ config.Tests.Value.Excluded |> List.map (fun e -> e.Project) = [ "tests/App.Dummy.Tests" ] @>
        test <@ config.Tests.Value.Excluded.Head.Reason.Contains "test-integration" @>)

[<Fact(Timeout = 30000)>]
let ``loadConfig REFUSES an exclusion with no reason — a declaration must declare something`` () =
    withTempDir "cfg-scope-noreason" (fun tmpDir ->
        let json =
            """{ "tests": { "projects": [ { "project": "App.Tests",
                                            "args": "run --project tests/App.Tests --no-build --" } ],
                            "excluded": [ { "project": "tests/App.Dummy.Tests", "reason": "  " } ] } }"""

        stageSolutionRepo tmpDir json

        let ex = Assert.Throws<ConfigError>(fun () -> loadConfig tmpDir |> ignore)
        Assert.Contains("reason", ex.Message))

[<Fact(Timeout = 30000)>]
let ``loadConfig leaves a repo that configures no tests alone`` () =
    // A repo that gates no suites makes no full-suite claim to be incomplete —
    // its scope is `NoTestsRun`/`ScopeUnknown` and `confirm` already refuses to
    // build a merge verdict from either. Failing a lint-only repo over test
    // projects it never asked fshw to run would be noise, not safety.
    withTempDir "cfg-scope-notests" (fun tmpDir ->
        stageSolutionRepo tmpDir """{ "format": true }"""

        let config = loadConfig tmpDir
        test <@ config.Tests = None @>)

// --- loadConfig: AUTOMATION-165, a verdictInputs declaration is never half-understood ---

[<Fact(Timeout = 30000)>]
let ``loadConfig REFUSES a verdictInputs declaration it cannot honour as written`` () =
    // The defect this closes is a declaration with no consumer: a consuming repo
    // listed the files that decide its checks, the tool read none of them, and
    // nothing said so. Replacing that with a declaration honoured only when
    // well-formed — and skipped in silence otherwise — would be the same bug in a
    // new place, so a declaration this build cannot act on stops the daemon.
    withTempDir "cfg-verdict-inputs-bad" (fun tmpDir ->
        File.WriteAllText(
            Path.Combine(tmpDir, ".fshw.json"),
            """{"verdictInputs": {"hashed": [{"path": "coverage-counts.json"}]}}"""
        )

        let ex = Assert.Throws<ConfigError>(fun () -> loadConfig tmpDir |> ignore)
        Assert.Contains("verdictInputs", ex.Message)
        Assert.Contains("why", ex.Message))

[<Fact(Timeout = 30000)>]
let ``loadConfig ACCEPTS a well-formed verdictInputs declaration — the control for the refusal above`` () =
    // Without this, the test above would also pass against a build that rejected
    // every config containing the key at all.
    withTempDir "cfg-verdict-inputs-ok" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, "coverage-counts.json"), "{}")

        File.WriteAllText(
            Path.Combine(tmpDir, ".fshw.json"),
            """{"verdictInputs": {"hashed": [
                 {"path": "coverage-counts.json", "why": "lower a floor and the prior green must stop applying"}]}}"""
        )

        loadConfig tmpDir |> ignore)

[<Fact(Timeout = 30000)>]
let ``loadConfig tolerates a declared input that is not on disk yet — but the tree hash still records it`` () =
    // An analyzer assembly a repo declares does not exist until the first build, and
    // refusing to start would wedge the very command that creates it. So this is a
    // warning, not a refusal — and the guarantee is carried structurally instead: the
    // declaration is hashed as ABSENT, so no verdict can apply as though the file had
    // been read.
    withTempDir "cfg-verdict-inputs-absent" (fun tmpDir ->
        File.WriteAllText(
            Path.Combine(tmpDir, ".fshw.json"),
            """{"verdictInputs": {"hashed": [
                 {"path": "analyzers/bin/Rules.dll", "why": "the assembly the analyze plugin loads"}]}}"""
        )

        loadConfig tmpDir |> ignore

        let tree = FsHotWatch.TreeHash.compute tmpDir []
        test <@ tree.AbsentDeclarationCount = 1 @>)
