module FsHotWatch.Tests.TestPruneRunnerFamilyTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

// --- CTRF runner family: report flags follow the resolved xUnit major ---

let private writeRunnerAssets (projectPath: string) (packageVersion: string) =
    let objDir = Path.Combine(Path.GetDirectoryName(projectPath), "obj")
    Directory.CreateDirectory(objDir) |> ignore

    File.WriteAllText(
        Path.Combine(objDir, "project.assets.json"),
        $"""{{"version":3,"libraries":{{"xunit.v3/%s{packageVersion}":{{"type":"package"}}}}}}"""
    )

let private writeRunnerAssetsJson (projectPath: string) (json: string) =
    let objDir = Path.Combine(Path.GetDirectoryName(projectPath), "obj")
    Directory.CreateDirectory(objDir) |> ignore
    File.WriteAllText(Path.Combine(objDir, "project.assets.json"), json)

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
            """{"version":3,"libraries":{"xunit.v3/3.2.2":{"type":"package"},"xunit.v3/4.0.0":{"type":"package"}}}"""

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
            $"""{{"version":3,"libraries":{{"xunit.v3/4.0.0":{{"type":"package"}},"xunit.v3/%s{unknownVersion}":{{"type":"package"}}}}}}"""

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
        writeRunnerAssetsJson proj $"""{{"version":3,"libraries":{{"xunit.v3/4.0.0":%s{library}}}}}"""

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

[<Fact(Timeout = 15000)>]
let ``run-tests gives an xUnit 4 runner its v4 flags and reads the report from that path`` () =
    withTempDir "fshw-xunit4-functional" (fun tmp ->
        let projectPath = Path.Combine(tmp, "MyTests.fsproj")
        File.WriteAllText(projectPath, "<Project />")
        writeRunnerAssets projectPath "4.0.0-pre.12"

        let cannedReport = Path.Combine(tmp, "canned.ctrf.json")

        File.WriteAllText(
            cannedReport,
            """{"results":{"summary":{"tests":2,"passed":2,"failed":0,"pending":0,"skipped":0,"other":0}}}"""
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
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmp
        let handler = create ":memory:" tmp (Some configs) None None None None []
        host.RegisterHandler(handler)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        test <@ result.IsSome @>

        let args = File.ReadAllLines capturedArgs
        test <@ args |> Array.contains "--report-xunit-ctrf" @>
        test <@ args |> Array.contains "--report-xunit-ctrf-filename" @>
        test <@ not (args |> Array.contains "--report-ctrf") @>

        use doc = JsonDocument.Parse(result.Value)
        let project = doc.RootElement.GetProperty("projects").[0]
        Assert.Equal("passed", project.GetProperty("status").GetString())
        Assert.Equal(2, project.GetProperty("counts").GetProperty("total").GetInt32()))

[<Fact(Timeout = 60000)>]
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
            TimeoutSec = Some 30
            ReportVerificationFormat = AutoDetect } ]

    let host = PluginHost.create (Unchecked.defaultof<_>) repoRoot
    let handler = create ":memory:" repoRoot (Some configs) None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsSome @>

    use doc = JsonDocument.Parse(result.Value)
    let project = doc.RootElement.GetProperty("projects").[0]
    Assert.Equal("passed", project.GetProperty("status").GetString())
    Assert.Equal(1, project.GetProperty("counts").GetProperty("total").GetInt32())
    Assert.Equal(1, project.GetProperty("counts").GetProperty("succeeded").GetInt32())
