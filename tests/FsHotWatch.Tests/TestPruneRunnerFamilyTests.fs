module FsHotWatch.Tests.TestPruneRunnerFamilyTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

// --- CTRF runner family: report flags follow the resolved xUnit major ---

/// A restored graph shaped as NuGet writes it for a project whose ONLY xUnit
/// reference is `referencedId`: the reference, plus the transitive closure the
/// xUnit v3 packages actually declare (`libraries` lists every package in the
/// closure, not just direct references).
let private restoredClosure (referencedId: string) (version: string) =
    let closure =
        match referencedId with
        | "xunit.v3" ->
            let mtp = if version.StartsWith "3." then "mtp-v1" else "mtp-v2"
            [ "xunit.v3"; $"xunit.v3.%s{mtp}"; $"xunit.v3.core.%s{mtp}" ]
        | "xunit.v3.mtp-v1" -> [ "xunit.v3.mtp-v1"; "xunit.v3.core.mtp-v1" ]
        | "xunit.v3.mtp-v2" -> [ "xunit.v3.mtp-v2"; "xunit.v3.core.mtp-v2" ]
        | other -> [ other ]

    let libraries =
        closure
        @ [ "xunit.v3.assert"
            "xunit.v3.extensibility.core"
            "xunit.v3.runner.inproc.console" ]
        |> List.map (fun id -> $"\"%s{id}/%s{version}\":{{\"type\":\"package\"}}")
        |> String.concat ","

    $"""{{"version":3,"libraries":{{%s{libraries},"Microsoft.Testing.Platform/2.0.0":{{"type":"package"}}}}}}"""

let private writeRunnerAssetsJson (projectPath: string) (json: string) =
    let objDir = Path.Combine(Path.GetDirectoryName(projectPath), "obj")
    Directory.CreateDirectory(objDir) |> ignore
    File.WriteAllText(Path.Combine(objDir, "project.assets.json"), json)

let private writeRunnerAssets (projectPath: string) (packageVersion: string) =
    writeRunnerAssetsJson projectPath (restoredClosure "xunit.v3" packageVersion)


[<Theory(Timeout = 5000)>]
[<InlineData("xunit.v3", "3.2.2", 3)>]
[<InlineData("xunit.v3", "4.0.0", 4)>]
[<InlineData("xunit.v3.mtp-v1", "3.2.2", 3)>]
[<InlineData("xunit.v3.mtp-v2", "3.2.2", 3)>]
[<InlineData("xunit.v3.mtp-v2", "4.0.0", 4)>]
[<InlineData("xunit.v3.core.mtp-v1", "3.2.2", 3)>]
[<InlineData("xunit.v3.core.mtp-v2", "4.0.0", 4)>]
let ``detectCtrfRunnerFamily detects every xUnit v3 package id that brings the MTP runner``
    (referencedId: string)
    (version: string)
    (major: int)
    =
    withTempDir "fshw-detect-xunit-mtp-ids" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssetsJson proj (restoredClosure referencedId version)
        let expected = if major = 3 then Xunit3 else Xunit4

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = Some expected @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily does not detect a project whose xUnit v3 packages carry no MTP runner`` () =
    withTempDir "fshw-detect-xunit-no-runner" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        // An extensibility library and a plain MTP project: xUnit packages and the
        // platform are present, but nothing that registers xUnit's CTRF reporter.
        writeRunnerAssetsJson proj (restoredClosure "xunit.v3.common" "3.2.2")

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = None @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily resolves xUnit 3 from restored assets`` () =
    withTempDir "fshw-detect-xunit3" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "3.2.2"

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = Some Xunit3 @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily resolves xUnit 4 from restored assets`` () =
    withTempDir "fshw-detect-xunit4" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "4.0.0"

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = Some Xunit4 @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily resolves a prerelease xUnit 4 package`` () =
    withTempDir "fshw-detect-xunit4-prerelease" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "4.0.0-pre.12"

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = Some Xunit4 @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily resolves a quoted project path containing spaces`` () =
    withTempDir "fshw-detect-xunit4-quoted" (fun tmp ->
        let projectDir = Path.Combine(tmp, "project with spaces")
        Directory.CreateDirectory(projectDir) |> ignore
        let proj = Path.Combine(projectDir, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "4.0.0"

        test <@ detectCtrfRunnerFamily $"--project \"%s{proj}\"" tmp = Some Xunit4 @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily resolves an escaped quote inside a quoted project path`` () =
    withTempDir "fshw-detect-xunit4-escaped-quote" (fun tmp ->
        let projectDir = Path.Combine(tmp, "project \"quoted\" path")
        Directory.CreateDirectory(projectDir) |> ignore
        let proj = Path.Combine(projectDir, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "4.0.0"
        let escaped = proj.Replace("\"", "\\\"")

        test <@ detectCtrfRunnerFamily $"--project \"%s{escaped}\"" tmp = Some Xunit4 @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily fails closed on an unterminated project quote`` () =
    withTempDir "fshw-detect-xunit4-unterminated" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "4.0.0"

        test <@ detectCtrfRunnerFamily $"--project \"%s{proj}" tmp = None @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily does not treat single quotes as argument grouping`` () =
    withTempDir "fshw-detect-xunit4-single-quote" (fun tmp ->
        let projectDir = Path.Combine(tmp, "project with spaces")
        Directory.CreateDirectory(projectDir) |> ignore
        let proj = Path.Combine(projectDir, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "4.0.0"

        test <@ detectCtrfRunnerFamily $"--project '%s{proj}'" tmp = None @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily fails closed when restored assets contain conflicting runner majors`` () =
    withTempDir "fshw-detect-xunit-conflict" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")

        writeRunnerAssetsJson
            proj
            """{"version":3,"libraries":{"xunit.v3.core.mtp-v1/3.2.2":{"type":"package"},"xunit.v3.core.mtp-v2/4.0.0":{"type":"package"}}}"""

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = None @>)

[<Theory(Timeout = 5000)>]
[<InlineData("5.0.0")>]
[<InlineData("not-a-version")>]
[<InlineData("4.not-semver")>]
[<InlineData("4.")>]
[<InlineData("4.0.0 garbage")>]
[<InlineData("4.0.0-01")>]
[<InlineData("4.0.0-alpha.01")>]
[<InlineData("4.2147483648.0")>]
[<InlineData("4.0.2147483648")>]
[<InlineData("4.0.0.2147483648")>]
let ``detectCtrfRunnerFamily fails closed when a supported runner is mixed with an unknown version``
    (unknownVersion: string)
    =
    withTempDir "fshw-detect-xunit-unknown-conflict" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")

        writeRunnerAssetsJson
            proj
            $"""{{"version":3,"libraries":{{"xunit.v3.core.mtp-v2/4.0.0":{{"type":"package"}},"xunit.v3.core.mtp-v2/%s{unknownVersion}":{{"type":"package"}}}}}}"""

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = None @>)

[<Theory(Timeout = 5000)>]
[<InlineData("4.0.0-pre.12+sha.abcdef")>]
[<InlineData("4.0.0+build.5")>]
let ``detectCtrfRunnerFamily accepts complete xUnit 4 prerelease and build versions`` (version: string) =
    withTempDir "fshw-detect-xunit-complete-semver" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj version

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = Some Xunit4 @>)

[<Theory(Timeout = 5000)>]
[<InlineData("{}")>]
[<InlineData("{\"type\":\"project\"}")>]
[<InlineData("{\"type\":\"Package\"}")>]
let ``detectCtrfRunnerFamily requires the xUnit asset to be exactly a package`` (library: string) =
    withTempDir "fshw-detect-xunit-library-type" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssetsJson proj $"""{{"version":3,"libraries":{{"xunit.v3.core.mtp-v2/4.0.0":%s{library}}}}}"""

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = None @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily fails closed when restored assets are missing`` () =
    withTempDir "fshw-detect-xunit-missing-assets" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = None @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily fails closed when restored assets are malformed`` () =
    withTempDir "fshw-detect-xunit-malformed-assets" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssetsJson proj "not JSON"

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = None @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfRunnerFamily rejects an unknown xUnit major`` () =
    withTempDir "fshw-detect-xunit5" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(proj, "<Project />")
        writeRunnerAssets proj "5.0.0"

        test <@ detectCtrfRunnerFamily $"--project {proj}" tmp = None @>)

[<Theory(Timeout = 5000)>]
[<InlineData(3, "--report-ctrf --report-ctrf-filename MyTests.ctrf.json --results-directory \"/tmp/results\"")>]
[<InlineData(4,
             "--report-xunit-ctrf --report-xunit-ctrf-filename MyTests.ctrf.json --results-directory \"/tmp/results\"")>]
let ``ctrfArguments uses the report switches supported by the resolved xUnit major`` (major: int) (expected: string) =
    let family = if major = 3 then Xunit3 else Xunit4
    test <@ ctrfArguments family "MyTests.ctrf.json" "/tmp/results" = expected @>

[<Theory(Timeout = 5000)>]
[<InlineData("run --project T --no-build --", true)>]
[<InlineData("run --project T --no-build -- --hangdump", true)>]
[<InlineData("run --project T --no-build -- --output Normal", false)>]
[<InlineData("run --project T -- --output=Detailed", false)>]
let ``detailedOutputArgument asks an MTP host for per-test result lines unless the project already chose``
    (configArgs: string)
    (injected: bool)
    =
    test <@ detailedOutputArgument configArgs = (if injected then Some "--output Detailed" else None) @>

[<Fact(Timeout = 15000)>]
let ``run-tests gives an xUnit 4 runner its v4 flags and reads the report from that path`` () =
    withTempDir "fshw-xunit4-functional" (fun tmp ->
        let projectPath = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(projectPath, "<Project />")
        writeRunnerAssets projectPath "4.0.0-pre.12"

        let cannedReport = Path.Combine(tmp, "canned.ctrf.json")

        File.WriteAllText(
            cannedReport,
            """{"results":{"summary":{"tests":2,"passed":2,"failed":0,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Lib.Tests.T.one","status":"passed"},{"name":"Lib.Tests.T.two","status":"passed"}]}}"""
        )

        let capturedArgs = Path.Combine(tmp, "captured-args")
        let runner = Path.Combine(tmp, "fake-xunit4.sh")

        File.WriteAllText(
            runner,
            "printf '%s\\n' \"$@\" > \""
            + capturedArgs
            + "\"\n"
            + "report_name=''\nresults_dir=''\n"
            + "while [ \"$#\" -gt 0 ]; do\n"
            + "  case \"$1\" in\n"
            + "    --report-xunit-ctrf-filename) report_name=\"$2\"; shift 2 ;;\n"
            + "    --results-directory) results_dir=\"$2\"; shift 2 ;;\n"
            + "    *) shift ;;\n"
            + "  esac\n"
            + "done\n"
            + "if [ -n \"$report_name\" ] && [ -n \"$results_dir\" ]; then cp \""
            + cannedReport
            + "\" \"$results_dir/$report_name\"; fi\n"
        )

        let configs =
            [ { Project = "MyTests"
                Command = "sh"
                // A bare project-file token is enough for runner detection without making
                // the fake shell script look like a `dotnet run --project` launch.
                Args = $"{runner} {projectPath}"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect
                Starvation = None
                CheckSkip = None
                AlwaysClasses = [] } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmp
        let handler = create ":memory:" tmp (Some configs) None None None None []
        host.RegisterHandler(handler)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        test <@ result.IsSome @>

        let args = File.ReadAllLines capturedArgs
        test <@ args |> Array.contains "--report-xunit-ctrf" @>
        test <@ args |> Array.contains "--report-xunit-ctrf-filename" @>
        test <@ not (args |> Array.contains "--report-ctrf") @>
        // Per-test result lines on the console, which a killed host's profile reads its
        // classes from (AUTOMATION-1300).
        test <@ args |> Array.pairwise |> Array.contains ("--output", "Detailed") @>
        // xUnit reports a test still running after the long-running threshold, so a killed
        // host's profile can name it; its reports need diagnostic messages on.
        test <@ args |> Array.pairwise |> Array.contains ("--long-running", "10") @>

        let configFile =
            args |> Array.pairwise |> Array.find (fun (a, _) -> a = "--config-file") |> snd

        let config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText configFile)
        test <@ (config.Item("xUnit").Item("diagnosticMessages").GetValue<bool>()) @>

        use doc = JsonDocument.Parse(result.Value)
        let project = doc.RootElement.GetProperty("projects").[0]
        Assert.Equal("passed", project.GetProperty("status").GetString())
        Assert.Equal(2, project.GetProperty("counts").GetProperty("total").GetInt32()))

// A real `dotnet run` of the fixture: MSBuild evaluation, then a test host, and a loaded box
// slows both. The test asserts the CTRF verification, not a duration, so the project's
// `TimeoutSec` only ends a hang (it was 30 s, and a slow start was failed as TimedOut), and
// the cap sits above it.
[<Fact(Timeout = 300_000)>]
let ``run-tests invokes the real xUnit 4 runner and verifies its CTRF report`` () =
    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let fixtureProject =
        Path.Combine(repoRoot, "tests", "Fixtures", "Xunit4RunnerFixture", "Xunit4RunnerFixture.fsproj")

    let configs =
        [ { Project = "Xunit4RunnerFixture"
            Command = "dotnet"
            Args = $"run --project \"%s{fixtureProject}\" --no-build --"
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = Some 240
            ReportVerificationFormat = AutoDetect
            Starvation = None
            CheckSkip = None
            AlwaysClasses = [] } ]

    // The plugin's OWN root is isolated: over `repoRoot` it would write this repository's
    // `.fshw/test-prune/` ledgers — and since the full-suite baseline became
    // durable, that stamped the real daemon's baseline with `Xunit4RunnerFixture` as the
    // only project, which it then (correctly) refused and re-earned with a full run.
    // Only the fixture project path needs the real repository.
    let pluginRoot = isolatedRoot ()
    let host = PluginHost.create (Unchecked.defaultof<_>) pluginRoot
    let handler = create ":memory:" pluginRoot (Some configs) None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsSome @>

    use doc = JsonDocument.Parse(result.Value)
    let project = doc.RootElement.GetProperty("projects").[0]
    Assert.Equal("passed", project.GetProperty("status").GetString())
    Assert.Equal(4, project.GetProperty("counts").GetProperty("total").GetInt32())
    Assert.Equal(4, project.GetProperty("counts").GetProperty("succeeded").GetInt32())

// --- naming the test running at a kill: xUnit long-running reports (AUTOMATION-1300) ---

[<Theory(Timeout = 5000)>]
[<InlineData(null, 10)>]
[<InlineData(600, 10)>]
[<InlineData(30, 10)>]
[<InlineData(12, 4)>]
[<InlineData(2, 1)>]
let ``the long-running threshold reports a hung test at least twice before the kill``
    (timeoutSec: System.Nullable<int>)
    (expected: int)
    =
    test <@ longRunningSeconds (Option.ofNullable timeoutSec) = expected @>

[<Fact(Timeout = 5000)>]
let ``the diagnostics config keeps the project's own testconfig and turns diagnostic messages on`` () =
    let own =
        """{"platformOptions": {"exitProcessOnUnhandledException": true}, "xUnit": {"maxParallelThreads": 2}}"""

    match diagnosticsTestConfig (Some own) with
    | Ok text ->
        let o = System.Text.Json.Nodes.JsonNode.Parse text
        test <@ (o.Item("platformOptions").Item("exitProcessOnUnhandledException").GetValue<bool>()) @>
        test <@ (o.Item("xUnit").Item("maxParallelThreads").GetValue<int>()) = 2 @>
        test <@ (o.Item("xUnit").Item("diagnosticMessages").GetValue<bool>()) @>
    | Error e -> failwith e

    match diagnosticsTestConfig None with
    | Ok text ->
        test
            <@ (System.Text.Json.Nodes.JsonNode.Parse(text).Item("xUnit").Item("diagnosticMessages").GetValue<bool>()) @>
    | Error e -> failwith e

    // A project config fshw cannot read is left alone: no config is injected over it.
    test <@ Result.isError (diagnosticsTestConfig (Some "{ not json")) @>
    test <@ Result.isError (diagnosticsTestConfig (Some """{"xUnit": 3}""")) @>

[<Fact(Timeout = 5000)>]
let ``long-running reporting is not injected over a project's own choice`` () =
    test <@ longRunningArguments "run --project T -- --long-running 5" "/r/P.testconfig.json" 10 = None @>
    test <@ longRunningArguments "run --project T -- --config-file my.json" "/r/P.testconfig.json" 10 = None @>

    test
        <@
            longRunningArguments "run --project T --" "/r d/P.testconfig.json" 4 = Some
                "--long-running 4 --config-file \"/r d/P.testconfig.json\""
        @>

// --- which testconfig.json is the project's own: by its built assembly's name ---

/// A project `Proj.Tests.fsproj` under `root/proj` holding `projectXml`, and built files
/// (path relative to its `bin/Debug/net10.0`, content).
let private builtProject (root: string) (projectXml: string) (built: (string * string) list) =
    let dir = Path.Combine(root, "proj")
    let output = Path.Combine(dir, "bin", "Debug", "net10.0")
    Directory.CreateDirectory output |> ignore
    let project = Path.Combine(dir, "Proj.Tests.fsproj")
    File.WriteAllText(project, projectXml)

    for name, content in built do
        File.WriteAllText(Path.Combine(output, name), content)

    project

let private plainProject = "<Project><PropertyGroup></PropertyGroup></Project>"

let private renamedProject =
    "<Project><PropertyGroup><AssemblyName>Renamed.Tests</AssemblyName></PropertyGroup></Project>"

[<Fact(Timeout = 5000)>]
let ``a project whose AssemblyName differs from its file name has its own testconfig found by that name`` () =
    withTempDir "fshw-testconfig-renamed" (fun root ->
        let project =
            builtProject
                root
                renamedProject
                [ "Renamed.Tests.dll", ""
                  "Renamed.Tests.testconfig.json", """{"own": true}""" ]

        test <@ projectTestConfigOf project root = Some(Some """{"own": true}""") @>)

[<Fact(Timeout = 5000)>]
let ``a project without an AssemblyName is read by its file name, and one with no config has none`` () =
    withTempDir "fshw-testconfig-default" (fun root ->
        let project =
            builtProject root plainProject [ "Proj.Tests.dll", ""; "Proj.Tests.testconfig.json", "{}" ]

        test <@ projectTestConfigOf project root = Some(Some "{}") @>)

    withTempDir "fshw-testconfig-none" (fun root ->
        let project = builtProject root plainProject [ "Proj.Tests.dll", "" ]
        test <@ projectTestConfigOf project root = Some None @>)

[<Fact(Timeout = 5000)>]
let ``an assembly name fshw cannot be sure of, or a config of another name, injects nothing`` () =
    // Computed by MSBuild: not knowable from the text.
    withTempDir "fshw-testconfig-computed" (fun root ->
        let project =
            builtProject
                root
                "<Project><PropertyGroup><AssemblyName>$(MSBuildProjectName).Suite</AssemblyName></PropertyGroup></Project>"
                [ "Proj.Tests.Suite.dll", ""; "Proj.Tests.Suite.testconfig.json", "{}" ]

        test <@ projectTestConfigOf project root = None @>)

    // Set by a Directory.Build.props above the project.
    withTempDir "fshw-testconfig-props" (fun root ->
        File.WriteAllText(
            Path.Combine(root, "Directory.Build.props"),
            "<Project><PropertyGroup><AssemblyName>Other</AssemblyName></PropertyGroup></Project>"
        )

        let project =
            builtProject root plainProject [ "Other.dll", ""; "Other.testconfig.json", "{}" ]

        test <@ projectTestConfigOf project root = None @>)

    // The output holds a config, beside its assembly, that is not the expected name's.
    withTempDir "fshw-testconfig-other" (fun root ->
        let project =
            builtProject
                root
                plainProject
                [ "Proj.Tests.dll", ""; "Elsewhere.dll", ""; "Elsewhere.testconfig.json", "{}" ]

        test <@ projectTestConfigOf project root = None @>)

[<Fact(Timeout = 5000)>]
let ``a testconfig with no assembly beside it is not a config of anything`` () =
    withTempDir "fshw-testconfig-orphan" (fun root ->
        let project =
            builtProject root plainProject [ "Proj.Tests.dll", ""; "Stray.testconfig.json", "{}" ]

        test <@ projectTestConfigOf project root = Some None @>)
