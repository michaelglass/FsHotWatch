module FsHotWatch.Tests.ConsumerScaleFixtureTests

// AUTOMATION-1365: consumer-shaped regressions, caught in FsHotWatch's own loop.
//
// Every scenario here reproduces a regression that FsHotWatch's suite could not see and
// that was found only when intelligence pinned the release or landed on it. Each runs the
// way a consumer does: a COLD daemon (the real CLI binary), a generated repository with the
// consumer's runner shape (xunit.v3 4 on Microsoft.Testing.Platform, Microsoft CodeCoverage),
// and the consumer's own config keys. Each also asserts that the rule under test actually
// fired, so a green cannot come from the scenario silently not exercising it.
//
// - 1353 (alpha.127): fshw injected xUnit's `--long-running` monitor into every host, which
//   holds a thread-pool thread for the whole run; a test that needs the pool failed. Two
//   `check` clients run concurrently against the cold daemon (the land's shape, and the
//   class of 1268, "a launch cancelled while a client still waits").
// - 1352: a project killed at its timeout under CPU starvation, then re-run alone, left the
//   coverage controller's instrumented copies in its bin, so the passing re-run wrote an
//   EMPTY coverage report and the consumer's coverage gate read 56,724 -> 0. It runs under a
//   CPU-load injector.
//
// Budget: each scenario is one cold daemon and one generated project, bounded by
// ColdCheckFixture's 840 s work budget inside a 900 s xUnit timeout; measured end to end in
// the handoff for AUTOMATION-1365.

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

/// The consumer's shape: a library under test in src/, and a test project on the runner
/// family intelligence pins (xunit.v3 4 on MTP, Microsoft CodeCoverage). Microsoft CodeCoverage
/// leaves the test assembly itself out of its report, so the measured code must be a library.
let private consumerProject (name: string) =
    $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <AssemblyName>{name}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Tests.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Subject/Subject.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
    <PackageReference Include="xunit.v3" Version="4.0.0" />
    <PackageReference Include="xunit.v3.mtp-v2" Version="4.0.0" />
    <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.9.0" />
  </ItemGroup>
</Project>
"""

let private projectEntry (name: string) (extra: string) =
    $"""{{ "project": "{name}", "command": "dotnet",
        "args": "run --project tests/{name}/{name}.fsproj --no-build --",
        "filterTemplate": "--filter-class {{classes}}", "classJoin": " "{extra} }}"""

let private writeRepo clock root (name: string) (tests: string) (entry: string) =
    let directory = Path.Combine(root, "tests", name)
    Directory.CreateDirectory directory |> ignore
    File.WriteAllText(Path.Combine(directory, name + ".fsproj"), consumerProject name)
    let subject = Path.Combine(root, "src", "Subject")
    Directory.CreateDirectory subject |> ignore

    File.WriteAllText(
        Path.Combine(subject, "Subject.fsproj"),
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Value.fs" /></ItemGroup>
  <ItemGroup><PackageReference Update="FSharp.Core" Version="10.1.*" /></ItemGroup>
</Project>
"""
    )

    writeValue subject "Subject" "2"
    File.WriteAllText(Path.Combine(directory, "Tests.fs"), tests)

    File.WriteAllText(
        Path.Combine(root, ".fshw.json"),
        $"""{{
  "build": {{ "command": "dotnet", "args": "build tests/{name}/{name}.fsproj --disable-build-servers" }},
  "format": false,
  "lint": false,
  "tests": {{
    "beforeRun": "dotnet build tests/{name}/{name}.fsproj --disable-build-servers",
    "projects": [ {entry} ]
  }}
}}
"""
    )

    initialize clock root

// ---- 1353: a diagnostic fshw injects must not take the host's pool thread ----

/// The pool probe intelligence's starvation tests depend on: a test that needs every pool
/// worker fails while one is held by other work. Same probe as Xunit4RunnerFixture's,
/// unconditional here.
let private poolProbeSource =
    """module PoolProbe.Tests
open System.Threading
open Xunit

[<Fact>]
let ``no pool thread is held by other work`` () =
    Assert.Equal(2, Subject.Value.answer ())
    let maxWorkers, _ = ThreadPool.GetMaxThreads()
    let own = if Thread.CurrentThread.IsThreadPoolThread then 1 else 0

    let held () =
        let available, _ = ThreadPool.GetAvailableThreads()
        maxWorkers - available - own

    let rec settle attemptsLeft lowest =
        if lowest <= 0 || attemptsLeft = 0 then
            lowest
        else
            Thread.Sleep 10
            settle (attemptsLeft - 1) (min lowest (held ()))

    Thread.Sleep 500
    let lowest = settle 100 (held ())
    Assert.True(lowest <= 0, $"%d{lowest} pool thread(s) held by other work for a whole second")
"""

/// Two `check --agent` clients at once against one daemon. Each blocks in its own task;
/// both are owned processes reaped by `run`.
let private concurrentChecks clock root cli =
    let one () =
        Task.Run(fun () -> run clock root "dotnet" [ cli; "check"; "--agent" ])

    let first, second = one (), one ()
    Task.WaitAll(first, second)
    first.Result, second.Result

[<Fact(Timeout = 900000)>]
let ``1353 - two concurrent checks on a cold daemon pass a test that needs the whole thread pool`` () =
    let clock = budget ()

    withTempDir "fshw-consumer-pool" (fun root ->
        writeRepo clock root "PoolProbe" poolProbeSource (projectEntry "PoolProbe" "")

        withDaemon clock root (fun cli ->
            let (firstCode, firstText), (secondCode, secondText) =
                concurrentChecks clock root cli

            let path = Path.Combine(root, ".fshw", "verdict.json")
            Assert.True(File.Exists path, $"no verdict:\n{firstText}\n---\n{secondText}")
            use document = JsonDocument.Parse(File.ReadAllText path)
            let verdict = document.RootElement.Clone()
            // Neither client may be left without a verdict (1268's exit 3) or red.
            Assert.True((firstCode = 0), $"first client exit {firstCode}:\n{firstText}\n{verdict.GetRawText()}")
            Assert.True((secondCode = 0), $"second client exit {secondCode}:\n{secondText}\n{verdict.GetRawText()}")
            Assert.Equal("green", verdict.GetProperty("outcome").GetProperty("kind").GetString())
            // The rule fired: the probe really ran in a real host, and passed.
            assertSuite "PoolProbe" 1 0 verdict))

// ---- 1352: a starved, killed first attempt must not empty the re-run's coverage ----

/// Passes, unless the fixture has ARMED it: then the first execution consumes the arm and
/// hangs, ignoring SIGTERM, until fshw kills the host at its timeout (SIGKILL), and the
/// re-run alone passes. Both files
/// live outside the gated tree.
let private hangsWhenArmedSource =
    """module Starved.Tests
open System.IO
open System.Threading
open Xunit

[<Fact>]
let ``hangs once when armed, passes alone`` () =
    Assert.Equal(2, Subject.Value.answer ())
    let receipts = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "fixture-receipts"))
    Directory.CreateDirectory receipts |> ignore
    let armed = Path.Combine(receipts, "Starved-armed.txt")

    if File.Exists armed then
        File.Delete armed
        File.WriteAllText(Path.Combine(receipts, "Starved-hung.txt"), "an armed attempt hung")
        // A starved host does not stop in the SIGTERM round, so fshw's teardown escalates to
        // SIGKILL and nothing in the tree gets to restore what it instrumented.
        use _ignoreTerm =
            System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM,
                fun context -> context.Cancel <- true
            )
        Thread.Sleep Timeout.Infinite
"""

/// Busy-loop processes at the lowest priority, for the life of `body`: the CPU contention
/// under which a consumer's land starves. The starvation DECISION is pinned by config
/// (`loadPerCore: 0`), so the scenario does not depend on the box's load average.
let private withLoad (body: unit -> 'a) : 'a =
    let workers = max 2 (Environment.ProcessorCount / 2)

    let injected =
        [ for _ in 1..workers ->
              let info =
                  ProcessStartInfo("nice", [ "-n"; "19"; "sh"; "-c"; "while :; do :; done" ])

              info.UseShellExecute <- false
              Process.Start info ]

    try
        body ()
    finally
        for p in injected do
            try
                if not p.HasExited then
                    p.Kill(entireProcessTree = true)

                p.WaitForExit 10000 |> ignore
            finally
                p.Dispose()

[<Fact(Timeout = 900000)>]
let ``1352 - a project starved and killed at its timeout, then re-run alone, still reports its coverage`` () =
    let clock = budget ()

    withTempDir "fshw-consumer-starved" (fun root ->
        let entry =
            projectEntry
                "Starved"
                """, "timeoutSec": 45, "coverage": true,
        "starvation": { "onTimeout": "rerun-alone", "loadPerCore": 0 }"""

        writeRepo clock root "Starved" hangsWhenArmedSource entry

        withDaemon clock root (fun cli ->
            // Settle first: a cold daemon's own scan can queue a later run, whose launch
            // preflight would repair the bin and hide the regression. On the land the
            // re-run alone was the LAST run, so it is here too.
            assertGreen (check clock root cli)

            withLoad (fun () ->
                Directory.CreateDirectory(Path.Combine(root, "fixture-receipts")) |> ignore
                File.WriteAllText(markerPath root "Starved-armed", "hang once")
                writeValue (Path.Combine(root, "src", "Subject")) "Subject" "1 + 1"
                let result = check clock root cli
                let _, text, verdict = result
                // The rule fired: the armed attempt really hung and was killed, and the
                // project was re-run alone and passed.
                Assert.True(File.Exists(markerPath root "Starved-hung"), $"the armed attempt never ran:\n{text}")

                // Each run directory is `.fshw/test-runs/<runId>/`, one level down.
                let starvedRecords =
                    Directory.GetDirectories(Path.Combine(root, ".fshw", "test-runs"))
                    |> Array.map (fun run -> Path.Combine(run, "Starved.starved.json"))
                    |> Array.filter File.Exists

                Assert.True(starvedRecords.Length > 0, $"Starved was not re-run alone after starving:\n{text}")
                use record = JsonDocument.Parse(File.ReadAllText starvedRecords[0])

                Assert.True(
                    record.RootElement.GetProperty("passedAlone").GetBoolean(),
                    $"the re-run alone did not pass: {record.RootElement.GetRawText()}"
                )

                assertGreen result
                assertSuite "Starved" 1 0 verdict
                // The consumer's coverage gate reads this report. An instrumented copy left
                // by the killed attempt makes the passing re-run write `<packages />`.
                let report =
                    Path.Combine(root, "coverage", "Starved", "coverage.baseline.cobertura.xml")

                Assert.True(File.Exists report, $"no coverage report at {report}:\n{text}")
                let xml = File.ReadAllText report

                Assert.True(
                    xml.Contains("name=\"Subject\"", StringComparison.Ordinal),
                    $"the re-run alone wrote an EMPTY coverage report ({xml.Length} bytes):\n{xml}"
                ))))
