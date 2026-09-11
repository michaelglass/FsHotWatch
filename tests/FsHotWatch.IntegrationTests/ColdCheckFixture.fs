module FsHotWatch.Tests.ColdCheckFixture

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FsHotWatch.Cli
open FsHotWatch.Ipc
open FsHotWatch.Tests.TestHelpers

// real cold and filtered command scenarios share only process
// ownership and fixture construction. No confirm or seeded fshw state supplies
// evidence. This does not decide the separate explicit-baseline CLI policy.

type private Child =
    { Process: Process
      Stdout: System.Threading.Tasks.Task<string>
      Stderr: System.Threading.Tasks.Task<string> }

let private start root executable (arguments: string list) =
    let info = ProcessStartInfo(executable: string)
    info.WorkingDirectory <- root
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    // Child-local settings: nested fixture builds must not leave reusable SDK
    // servers behind after the directly owned command/daemon has exited.
    info.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
    info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] <- "0"
    arguments |> List.iter info.ArgumentList.Add
    let childProcess = Process.Start info

    { Process = childProcess
      Stdout = childProcess.StandardOutput.ReadToEndAsync()
      Stderr = childProcess.StandardError.ReadToEndAsync() }

let private disposeChild child =
    try
        if not child.Process.HasExited then
            // This handle comes directly from Process.Start above, never a shared
            // pidfile or a process-name search. Reap only this owned process tree.
            child.Process.Kill(entireProcessTree = true)

        Assert.True(child.Process.WaitForExit(10000), "owned CLI child did not exit after cleanup")
    finally
        child.Process.Dispose()

// One 840-second work budget leaves 60 seconds inside each 900-second xUnit
// timeout for bounded process reaping and temporary-directory cleanup.
let budget () = Stopwatch.StartNew()

let remaining (clock: Stopwatch) cap =
    let left = 840000L - clock.ElapsedMilliseconds
    Assert.True(left > 0L, "cold-check scenario exhausted its overall work budget")
    int (min (int64 cap) left)

let private output clock child =
    Assert.True(child.Stdout.Wait(remaining clock 10000), "owned child's stdout did not close")
    Assert.True(child.Stderr.Wait(remaining clock 10000), "owned child's stderr did not close")
    child.Stdout.Result + "\n" + child.Stderr.Result

let run clock root executable arguments =
    let child = start root executable arguments

    try
        Assert.True(child.Process.WaitForExit(remaining clock Int32.MaxValue), $"timed out: {executable} {arguments}")
        child.Process.ExitCode, output clock child
    finally
        disposeChild child

let private cliAssembly () =
    let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
    let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))

    let assembly =
        Path.Combine(root, "src", "FsHotWatch.Cli", "bin", configuration, "net10.0", "FsHotWatch.Cli.dll")

    Assert.True(File.Exists assembly, $"build the real CLI before running this integration test: {assembly}")
    assembly

let project =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Value.fs" />
    <Compile Include="Tests.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" />
    <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.9.0" />
  </ItemGroup>
</Project>
"""

let config =
    """{
  "build": { "command": "dotnet", "args": "build tests/ColdProbe/ColdProbe.fsproj --disable-build-servers" },
  "format": false,
  "lint": false,
  "tests": {
    "beforeRun": "dotnet build tests/ColdProbe/ColdProbe.fsproj --disable-build-servers",
    "projects": [{
      "project": "ColdProbe",
      "command": "dotnet",
      "args": "run --project tests/ColdProbe/ColdProbe.fsproj --no-build --",
      "filterTemplate": "--filter-class {classes}",
      "classJoin": " "
    }]
  }
}
"""

let git clock root args =
    let code, text = run clock root "git" args
    Assert.True((code = 0), text)

let commit clock root message =
    git clock root [ "add"; "." ]

    git
        clock
        root
        [ "-c"
          "user.name=Cold Check Fixture"
          "-c"
          "user.email=fixture@example.invalid"
          "-c"
          "commit.gpgsign=false"
          "commit"
          "--quiet"
          "-m"
          message ]

let initialize clock root =
    File.WriteAllText(Path.Combine(root, ".gitignore"), "bin/\nobj/\n.fshw/\nlogs/\nfixture-receipts/\n")
    git clock root [ "init"; "--quiet" ]
    commit clock root "baseline"

let writeValue directory name expression =
    File.WriteAllText(Path.Combine(directory, "Value.fs"), $"module {name}.Value\nlet answer () = {expression}\n")

let markerPath root name =
    Path.Combine(root, "fixture-receipts", name + ".txt")

let testSource name =
    $"""module {name}.Tests
open System.IO
open Xunit
[<Fact>]
let ``observes the selected source value`` () =
    Assert.Equal(2, {name}.Value.answer ())
    // Observation output must stay outside the src/tests tree being gated.
    let receipts = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "fixture-receipts"))
    Directory.CreateDirectory receipts |> ignore
    File.WriteAllText(Path.Combine(receipts, "{name}.txt"), "observed 2")
"""

let withDaemon clock root body =
    Assert.False(Directory.Exists(Path.Combine(root, ".fshw")))
    let cli = cliAssembly ()
    // The child CLI hashes its physical working directory. Path.GetFullPath
    // leaves macOS /var symlinks intact, so resolve it through an owned child.
    let directoryCode, physicalDirectory = run clock root "/bin/pwd" [ "-P" ]
    Assert.True((directoryCode = 0), physicalDirectory)
    let pipe = Program.computePipeName (physicalDirectory.Trim())
    // Program.Start synchronously runs RunWithIpc. Own that foreground daemon,
    // never the detached launcher used by ensureDaemon.
    let daemon = start root "dotnet" [ cli; "start" ]

    try
        let listening =
            waitUntilTrue (fun () -> daemon.Process.HasExited || IpcClient.isRunning pipe) (remaining clock 120000)

        Assert.True(listening, "owned cold daemon did not open its IPC endpoint")

        if daemon.Process.HasExited then
            Assert.Fail(output clock daemon)

        Assert.Equal(string daemon.Process.Id, File.ReadAllText(Path.Combine(root, ".fshw", "daemon.pid")))
        body cli
    finally
        try
            if not daemon.Process.HasExited && IpcClient.isRunning pipe then
                IpcClient.shutdown pipe
                |> fun shutdown -> Async.RunSynchronously(shutdown, 10000) |> ignore

            daemon.Process.WaitForExit(10000) |> ignore
        finally
            disposeChild daemon

let check clock root cli =
    let code, text = run clock root "dotnet" [ cli; "check"; "--agent" ]
    let path = Path.Combine(root, ".fshw", "verdict.json")
    Assert.True(File.Exists path, $"check did not produce a verdict:\n{text}")
    use document = JsonDocument.Parse(File.ReadAllText path)
    let verdict = document.RootElement.Clone()
    Assert.Equal("check", verdict.GetProperty("command").GetString())
    // A compile failure cannot satisfy a negative test-evidence control.
    Assert.Contains(
        verdict.GetProperty("plugins").EnumerateArray(),
        fun (plugin: JsonElement) ->
            plugin.GetProperty("name").GetString() = "build"
            && plugin.GetProperty("outcome").GetString() = "ok"
    )

    code, text, verdict

let assertScope kind ran total (verdict: JsonElement) =
    let scope = verdict.GetProperty("scope")
    Assert.Equal<string>(kind, scope.GetProperty("kind").GetString())
    Assert.Equal<int>(ran, scope.GetProperty("ranProjects").GetInt32())
    Assert.Equal<int>(total, scope.GetProperty("totalProjects").GetInt32())

let assertGreen ((code, text, verdict): int * string * JsonElement) =
    Assert.True((code = 0), text + "\n" + verdict.GetRawText())
    Assert.Equal("green", verdict.GetProperty("outcome").GetProperty("kind").GetString())

let assertSuite name passed failed (verdict: JsonElement) =
    Assert.Contains(
        verdict.GetProperty("suites").EnumerateArray(),
        fun (suite: JsonElement) ->
            suite.GetProperty("project").GetString() = name
            && suite.GetProperty("passed").GetInt32() = passed
            && suite.GetProperty("failed").GetInt32() = failed
            && suite.GetProperty("total").GetInt32() = passed + failed
    )

let snapshotRunIds root =
    let directory = Path.Combine(root, ".fshw", "test-runs")
    Assert.True(Directory.Exists directory, "successful fixture checks must have produced run directories")

    Directory.EnumerateDirectories directory
    |> Seq.map Path.GetFileName
    |> Set.ofSeq

let private assertFreshReport (root: string) (runId: string) (name: string) (suite: JsonElement) =
    // The actual upstream verdict stores repo-relative CTRF paths under its
    // declared run ID. Bind the file we read to that exact new run and project.
    Guid.ParseExact(runId, "N") |> ignore
    let relative = suite.GetProperty("ctrf").GetString()
    Assert.False(Path.IsPathRooted relative)
    let actual = Path.GetFullPath(Path.Combine(root, relative))

    let expected =
        Path.GetFullPath(Path.Combine(root, ".fshw", "test-runs", runId, name + ".ctrf.json"))

    Assert.Equal(expected, actual)
    Assert.Equal(1, suite.GetProperty("total").GetInt32())
    Assert.Equal(1, suite.GetProperty("passed").GetInt32())
    Assert.Equal(0, suite.GetProperty("failed").GetInt32())
    Assert.Equal(0, suite.GetProperty("skipped").GetInt32())

    use document = JsonDocument.Parse(File.ReadAllText actual)
    let results = document.RootElement.GetProperty("results")
    let summary = results.GetProperty("summary")
    Assert.Equal(1, summary.GetProperty("tests").GetInt32())
    Assert.Equal(1, summary.GetProperty("passed").GetInt32())

    for field in [ "failed"; "skipped"; "pending"; "other" ] do
        Assert.Equal(0, summary.GetProperty(field).GetInt32())

    let test = Assert.Single(results.GetProperty("tests").EnumerateArray())
    Assert.Equal("passed", test.GetProperty("status").GetString())
    Assert.Equal(name + ".Tests", test.GetProperty("extra").GetProperty("type").GetString())
    Assert.Equal("observes the selected source value", test.GetProperty("extra").GetProperty("method").GetString())

let assertFreshFullPair root (priorRunIds: Set<string>) (verdict: JsonElement) =
    assertScope "full" 2 2 verdict

    let newRuns =
        verdict.GetProperty("runs").EnumerateArray()
        |> Seq.filter (fun run -> not (priorRunIds.Contains(run.GetProperty("runId").GetString())))
        |> Seq.toList

    Assert.NotEmpty newRuns

    for name in [ "ColdA"; "ColdB" ] do
        let receipts =
            newRuns
            |> List.collect (fun run ->
                run.GetProperty("suites").EnumerateArray()
                |> Seq.filter (fun suite -> suite.GetProperty("project").GetString() = name)
                |> Seq.map (fun suite -> run.GetProperty("runId").GetString(), suite)
                |> Seq.toList)

        Assert.True(not receipts.IsEmpty, $"full recovery did not freshly execute {name}")

        for runId, suite in receipts do
            assertFreshReport root runId name suite

            Assert.Contains(
                verdict.GetProperty("suites").EnumerateArray(),
                fun flat ->
                    flat.GetProperty("project").GetString() = name
                    && flat.GetProperty("ctrf").GetString() = suite.GetProperty("ctrf").GetString()
                    && flat.GetProperty("passed").GetInt32() = 1
                    && flat.GetProperty("total").GetInt32() = 1
                    && flat.GetProperty("failed").GetInt32() = 0
                    && flat.GetProperty("skipped").GetInt32() = 0
            )
