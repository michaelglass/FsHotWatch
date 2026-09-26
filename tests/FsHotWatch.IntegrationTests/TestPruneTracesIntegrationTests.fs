/// `tests.traces` end to end: a real F# library and xUnit v3 test project, built with the
/// SDK, run through the plugin twice: once untraced, once traced with the released weaver.
/// The traced run launches the woven copy, records a trace for its test, and reaches the
/// same verdict as the untraced one.
module FsHotWatch.Tests.TestPruneTracesIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.Trace

/// A scratch directory named by its real path. macOS's temp directory is under the `/var`
/// symlink, and a build records the path it was given in its PDBs, so the repository root
/// must be spelled the same way the build spells it.
let private scratchRoot () =
    let dir = Directory.CreateTempSubdirectory("fshw-traces-it-").FullName

    if
        dir.StartsWith("/var/", StringComparison.Ordinal)
        && Directory.Exists "/private/var"
    then
        "/private" + dir
    else
        dir

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private scaffold (root: string) =
    write
        root
        "src/L/L.fsproj"
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
</Project>"""

    write root "src/L/Lib.fs" "module L.Lib\n\nlet double (x: int) = x * 2\n"

    write
        root
        "tests/T/T.fsproj"
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup><Compile Include="Tests.fs" /></ItemGroup>
  <ItemGroup><ProjectReference Include="../../src/L/L.fsproj" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.0" />
    <PackageReference Include="xunit.v3.mtp-v2" Version="4.0.0" />
  </ItemGroup>
</Project>"""

    write
        root
        "tests/T/Tests.fs"
        "module T.Tests\n\nopen Xunit\n\n[<Fact>]\nlet ``doubles two`` () = Assert.Equal(4, L.Lib.double 2)\n"

/// The `dotnet` this test host runs on.
let private dotnet () =
    match Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" with
    | null
    | "" -> "dotnet"
    | host -> host

let private build (root: string) =
    let info = ProcessStartInfo(dotnet ())
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    // Nested builds must not leave reusable SDK servers behind.
    info.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
    info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] <- "0"

    for arg in [ "build"; "tests/T/T.fsproj"; "-nologo" ] do
        info.ArgumentList.Add arg

    use p = Process.Start info
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()

    if not (p.WaitForExit(600000)) then
        p.Kill(entireProcessTree = true)

    p.WaitForExit()
    Assert.True((p.ExitCode = 0), $"scratch build failed:\n%s{out.Result}\n%s{err.Result}")

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
      ReportVerificationFormat = AutoDetect }

let private runTests (root: string) (traces: TraceSettings option) =
    let host = FsHotWatch.PluginHost.PluginHost.create (Unchecked.defaultof<_>) root

    host.WorkStore.PublishProjectModel FsHotWatch.Tests.TestHelpers.fixtureModel

    host.RegisterHandler(
        createWithTraces
            traces
            Set.empty
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

    try
        scaffold root
        build root

        let untraced, _ = runTests root None

        let traced, activity =
            runTests
                root
                (Some
                    { Record = RecordEveryRun
                      DbPath = TraceSettings.defaultDbPath
                      WeaveTests = WeaveTestSites
                      FingerprintInputs = []
                      FingerprintEnv = []
                      VerifyTimeoutSec = 300 })

        test <@ untraced = "passed" @>
        test <@ traced = untraced @>
        // Woven, attributed to its test, and complete. This scratch index holds no symbols
        // (nothing analysed it), so the joiner records the executed code as whole-file
        // dependencies on `src/L/Lib.fs` and `tests/T/Tests.fs`: coarse, and sound.
        test <@ activity |> List.contains "traces: T 1/1 traced, 1 complete" @>
        test <@ Directory.Exists(Path.Combine(root, "tests", "T", "bin", "Traced")) @>

        use store = TraceStore.Store.Open(Path.Combine(root, ".fshw", "test-traces.db"))
        let run = store.Runs "T" |> List.exactlyOne
        test <@ (run.Status, run.Kind) = (TraceStore.Recorded, TraceStore.FullRun) @>
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()
