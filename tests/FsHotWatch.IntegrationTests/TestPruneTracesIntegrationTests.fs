/// `tests.traces` end to end: a real F# library and xUnit v3 test project, built with the
/// SDK, run through the plugin twice: once untraced, once traced with the released weaver.
/// The traced run launches the woven copy, records a trace for its test, and reaches the
/// same verdict as the untraced one.
module FsHotWatch.Tests.TestPruneTracesIntegrationTests

open System
open System.IO
open System.Text.Json
open System.Xml.Linq
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.PluginRuns
open FsHotWatch.TestPrune.PluginFold
open FsHotWatch.TestPrune.PluginSteps
open FsHotWatch.TestPrune.PluginStartup
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.TestPrune.SelectionAssembly
open FsHotWatch.TestPrune.PluginTypes
open TestPrune.Trace
open FsHotWatch.ProcessHelper

/// A scratch directory named by its real path. A build records the real path in its PDBs,
/// and macOS's temp directory sits under a symlink (`/var` for the usual `TMPDIR`, `/tmp`
/// when `TMPDIR` is unset), so the repository root must be spelled the way the build spells it.
let private scratchRoot () =
    (FsHotWatch.TestRoots.TestRoot.freshDirectory "fshw-traces-it").FullName
    |> FsHotWatch.CommandPreprocessor.realPathOf

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// The version `Directory.Packages.props` pins `package` to: what the repository's own
/// restore put in the package cache.
let internal repositoryPin (package: string) : string =
    let doc =
        XDocument.Load(FsHotWatch.TestRoots.RepositoryTree.path "Directory.Packages.props")

    doc.Descendants(XName.Get "PackageVersion")
    |> Seq.filter (fun e -> e.Attribute(XName.Get "Include").Value = package)
    |> Seq.exactlyOne
    |> fun e -> e.Attribute(XName.Get "Version").Value

/// The scratch solution, as (path under the root, contents).
///
/// Its restore is hermetic (AUTOMATION-1784): it needs nothing the repository's own restore
/// did not already put in the package cache. On a degraded CI runner the scratch restore
/// once took minutes, waiting on nuget.org for NuGetAudit's vulnerability database, before a
/// build that then hung. So `Directory.Build.props` turns NuGetAudit off and replaces the
/// SDK's implicit FSharp.Core with the repository's pin, and the test project names the
/// repository's xUnit pins.
let internal scaffoldFiles () : (string * string) list =
    let pin package =
        $"""<PackageReference Include="%s{package}" Version="%s{repositoryPin package}" />"""

    [ "Directory.Build.props",
      $"""<Project>
  <PropertyGroup>
    <NuGetAudit>false</NuGetAudit>
    <DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>
  </PropertyGroup>
  <ItemGroup>
    %s{pin "FSharp.Core"}
  </ItemGroup>
</Project>"""
      "src/L/L.fsproj",
      """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>"""
      "src/L/Lib.fs", "module L.Lib\n\nlet double (x: int) = x * 2\n"
      "tests/T/T.fsproj",
      $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup><Compile Include="Tests.fs" /></ItemGroup>
  <ItemGroup><ProjectReference Include="../../src/L/L.fsproj" /></ItemGroup>
  <ItemGroup>
    %s{pin "xunit.v3"}
    %s{pin "xunit.v3.mtp-v2"}
  </ItemGroup>
</Project>"""
      "tests/T/Tests.fs",
      "module T.Tests\n\nopen Xunit\n\n[<Fact>]\nlet ``doubles two`` () = Assert.Equal(4, L.Lib.double 2)\n" ]

let private scaffold (root: string) =
    scaffoldFiles () |> List.iter (fun (rel, text) -> write root rel text)

/// The `dotnet` this test host runs on.
let private dotnet () =
    match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
    | null
    | "" -> "dotnet"
    | host -> host

/// Where the scratch build writes its binary log.
let internal binlogPath (root: string) = Path.Combine(root, "build.binlog")

/// The scratch build, at normal verbosity with a timestamp on every console line and a
/// binary log, so a failure or timeout shows where the time went instead of "Restored …"
/// followed by silence.
let internal buildArgs (root: string) : string list =
    [ "build"
      "tests/T/T.fsproj"
      "-nologo"
      "-v:n"
      "-clp:ShowTimestamp"
      $"-bl:%s{binlogPath root}" ]

/// `dotnet <args>` in `root`, through `ProcessHelper.runProcess`: the child's environment is
/// `inheritedEnvironment`, without the MSBuild discovery variables an earlier daemon test
/// may have written into this process (docs/leaked-msbuild-env-bug.md).
let internal runScratchDotnet (root: string) (args: string list) (timeout: TimeSpan) : ProcessOutcome =
    runProcess
        (dotnet ())
        (args |> List.map quoteArg |> String.concat " ")
        root
        // Nested builds must not leave reusable SDK servers behind.
        [ "MSBUILDDISABLENODEREUSE", "1"; "DOTNET_CLI_USE_MSBUILD_SERVER", "0" ]
        (ProcessBounds.silent timeout)

/// The last `count` lines of `text`.
let private lastLines (count: int) (text: string) =
    let lines = text.Split '\n'
    lines |> Array.skip (max 0 (lines.Length - count)) |> String.concat "\n"

let private build (root: string) =
    let tailOf output =
        $"binlog (kept until this test process exits): %s{binlogPath root}\nlast lines:\n%s{lastLines 80 (ProcessOutput.text output)}"

    match runScratchDotnet root (buildArgs root) (TimeSpan.FromMinutes 10.0) with
    | Succeeded _ -> ()
    | Failed(code, output) -> Assert.Fail $"scratch build exited %d{code}\n%s{tailOf output}"
    | TimedOut(after, output, kill) ->
        Assert.Fail $"scratch build did not finish within %s{string after} (%s{renderKill kill})\n%s{tailOf output}"

let private config: TestConfig =
    { Project = "T"
      Command = "dotnet"
      Args = "run --project tests/T --no-build"
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = Some 300
      // The switches come from the restored xUnit major, as for a real project.
      ReportVerificationFormat = AutoDetect
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

let private runTests (root: string) (traces: TraceSettings option) =
    let host = FsHotWatch.PluginHost.PluginHost.create (Unchecked.defaultof<_>) root

    host.WorkStore.PublishProjectModel FsHotWatch.Tests.TestHelpers.fixtureModel

    host.RegisterHandler(
        createWithTraces
            traces
            Set.empty
            Map.empty
            (fun () -> Map.empty)
            (Path.Combine(root, "tp.db"))
            root
            (Some [ config ])
            None
            None
            None
            None
            []
    )

    let json =
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> Option.get

    use doc = JsonDocument.Parse json

    let status =
        doc.RootElement.GetProperty("projects").[0].GetProperty("status").GetString()

    let activity =
        host.GetActivitySnapshot("test-prune").LastRun
        |> Option.map (fun r -> r.ActivityTail)
        |> Option.defaultValue []

    status, activity

[<Fact(Timeout = 900000)>]
let ``a traced run records its test's trace and reaches the untraced run's verdict`` () =
    let root = scratchRoot ()

    scaffold root
    build root

    let untraced, _ = runTests root None

    let traced, activity =
        runTests
            root
            (Some
                { Record = RecordEveryRun
                  DbPath = TraceSettings.DefaultDbPath
                  WeaveTests = WeaveTestSites
                  FingerprintInputs = []
                  FingerprintEnv = []
                  VerifyTimeoutSec = 300
                  Select = SelectOff
                  ShadowDbPath = None })

    test <@ untraced = "passed" @>
    test <@ traced = untraced @>
    // Woven, attributed to its test, and complete. This scratch index holds no symbols
    // (nothing analysed it), so the joiner records the executed code as whole-file
    // dependencies on `src/L/Lib.fs` and `tests/T/Tests.fs`: coarse, and sound.
    test <@ activity |> List.contains "traces: T 1/1 traced, 1 complete" @>
    test <@ Directory.Exists(Path.Combine(root, "tests", "T", "bin", "Traced")) @>

    let run =
        use store = TraceStore.Store.Open(Path.Combine(root, ".fshw", "test-traces.db"))
        store.Runs "T" |> List.exactlyOne

    test <@ (run.Status, run.Kind) = (TraceStore.Recorded, TraceStore.FullRun) @>

    // Removed only once the test has passed. A failure leaves the scratch root, the build's
    // binlog included, for the process root's removal when this test process exits.
    try
        Directory.Delete(root, true)
    with _ ->
        ()

// AUTOMATION-1784: the scratch build hung silently for ten minutes on a degraded runner,
// after a restore that depended on nuget.org. The tests below pin the three parts of the fix.

/// The scratch restore needs nothing the repository's own restore did not already put in
/// the package cache: no NuGetAudit fetch of nuget.org's vulnerability database, and the
/// repository's FSharp.Core and xUnit rather than whatever the SDK would pick.
[<Fact>]
let ``the scratch restore asks nuget.org for nothing the repository's restore did not fetch`` () =
    let files = scaffoldFiles () |> Map.ofList
    let props = files["Directory.Build.props"]
    let testProject = files["tests/T/T.fsproj"]

    test <@ props.Contains "<NuGetAudit>false</NuGetAudit>" @>
    test <@ props.Contains "<DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference>" @>

    test
        <@ props.Contains($"""<PackageReference Include="FSharp.Core" Version="%s{repositoryPin "FSharp.Core"}" />""") @>

    for package in [ "xunit.v3"; "xunit.v3.mtp-v2" ] do
        test
            <@
                testProject.Contains(
                    $"""<PackageReference Include="%s{package}" Version="%s{repositoryPin package}" />"""
                )
            @>

/// A timeout or failure says where the time went: a timestamp on every console line, and a
/// binary log in the scratch root.
[<Fact>]
let ``the scratch build logs timestamped lines and writes a binlog in its root`` () =
    let root = FsHotWatch.TestRoots.TestRoot.pathOnlyBase
    let args = buildArgs root

    test <@ args |> List.contains "-clp:ShowTimestamp" @>
    test <@ args |> List.contains "-v:n" @>
    test <@ args |> List.contains $"-bl:%s{binlogPath root}" @>
    test <@ binlogPath root = Path.Combine(root, "build.binlog") @>

/// The MSBuild discovery variables a daemon test writes into this process
/// (docs/leaked-msbuild-env-bug.md) never reach the scratch build, whatever ran before it.
/// The leak is staged in a session environment, the base `ProcessHelper` spawns from, so
/// this process's own environment is never touched. The probe, an ordinary variable of that
/// session, must arrive: a spawn that bypasses `ProcessHelper` (a raw `ProcessStartInfo`,
/// which copies this process's environment with nothing removed) never sees it, and fails.
[<Fact>]
let ``a leaked MSBuild discovery variable never reaches a scratch dotnet`` () =
    FsHotWatch.TestRoots.TestRoot.withRoot "fshw-traces-leak" (fun root ->
        let sentinel = "/fshw-leak-sentinel"

        write
            root.Path
            "show.proj"
            """<Project>
  <Target Name="Show">
    <Message Importance="high" Text="probe=[$(FSHW_SCRATCH_PROBE)] exe=[$(MSBUILD_EXE_PATH)] ext=[$(MSBuildExtensionsPath)] sdks=[$(MSBuildSDKsPath)]" />
  </Target>
</Project>"""

        let leaked =
            FsHotWatch.SessionScope.SessionEnvironment.ofProcess root.Path
            |> FsHotWatch.SessionScope.SessionEnvironment.variables
            |> Map.add "FSHW_SCRATCH_PROBE" "arrived"
            |> Map.add "MSBUILD_EXE_PATH" (sentinel + "/MSBuild.dll")
            |> Map.add "MSBuildExtensionsPath" (sentinel + "/")
            |> Map.add "MSBuildSDKsPath" (sentinel + "/Sdks")
            |> FsHotWatch.SessionScope.SessionEnvironment.create root.Path

        let outcome =
            use _scope = FsHotWatch.SessionScope.SessionEnvironment.install leaked

            runScratchDotnet root.Path [ "msbuild"; "show.proj"; "-t:Show"; "-nologo" ] (TimeSpan.FromMinutes 2.0)

        let output = outputOf outcome
        test <@ isSucceeded outcome @>
        test <@ output.Contains "probe=[arrived]" @>
        test <@ not (output.Contains sentinel) @>)
