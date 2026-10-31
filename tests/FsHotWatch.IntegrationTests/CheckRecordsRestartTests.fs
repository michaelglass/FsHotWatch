/// ADR-059 §17 step 3: check records end to end. A real daemon with the real plugins checks
/// a fixture repository and files its checks; a daemon started after it answers the same
/// tree from those records, and from its plugins' own task caches, without a check in FCS.
module FsHotWatch.Tests.CheckRecordsRestartTests

open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

let private projectXml =
    project.Replace(
        """    <Compile Include="Value.fs" />""",
        """    <Compile Include="Value.fs" />
    <Compile Include="Middle.fs" />"""
    )

/// `tests/Replay`: Value.fs, Middle.fs, Tests.fs, gated by build, lint, a real analyzer
/// and TestPrune, with check records on.
let private prepare clock (root: string) =
    let directory = Path.Combine(root, "tests", "Replay")
    Directory.CreateDirectory directory |> ignore
    File.WriteAllText(Path.Combine(directory, "Replay.fsproj"), projectXml)
    writeValue directory "Replay" "2"

    File.WriteAllText(
        Path.Combine(directory, "Middle.fs"),
        "module Replay.Middle\nlet doubled () = Replay.Value.answer () * 2\n"
    )

    File.WriteAllText(Path.Combine(directory, "Tests.fs"), testSource "Replay")
    let analyzer = IntegrationTests.exampleAnalyzerPath.Value.Replace("\\", "/")

    File.WriteAllText(
        Path.Combine(root, ".fshw.json"),
        $$"""{
  "build": { "command": "dotnet", "args": "build tests/Replay/Replay.fsproj --disable-build-servers" },
  "format": false,
  "lint": true,
  "checkRecords": true,
  "analyzers": { "paths": [ "{{analyzer}}" ] },
  "tests": {
    "beforeRun": "dotnet build tests/Replay/Replay.fsproj --disable-build-servers",
    "projects": [{
      "project": "Replay",
      "command": "dotnet",
      "args": "run --project tests/Replay/Replay.fsproj --no-build --",
      "filterTemplate": "--filter-class {classes}",
      "classJoin": " "
    }]
  }
}
"""
    )

    initialize clock root
    directory

/// The tally a daemon logs as it stops (`CheckPipeline.CheckRecordsTally`): replayed,
/// started in FCS, demanded by a plugin.
let private tally (log: string) =
    let found =
        Regex.Match(
            log,
            @"this daemon: (\d+) check\(s\) replayed from check records, (\d+) FCS check\(s\) started, (\d+) demanded by a plugin"
        )

    Assert.True(found.Success, $"the daemon logged no check-record tally as it stopped:\n%s{log}")
    int found.Groups[1].Value, int found.Groups[2].Value, int found.Groups[3].Value

/// Each plugin's name and outcome in a verdict.
let private outcomes (verdict: JsonElement) =
    verdict.GetProperty("plugins").EnumerateArray()
    |> Seq.map (fun plugin -> plugin.GetProperty("name").GetString(), plugin.GetProperty("outcome").GetString())
    |> Seq.sort
    |> List.ofSeq

/// Check `root` with a cold daemon, stop it, run `between`, then check again with a new one.
/// Both verdicts, and the second daemon's tally.
let private checkTwice clock root (between: unit -> unit) =
    let first, firstLog =
        withDaemonOutput true clock root (fun cli -> check clock root cli)

    assertGreen first
    let replayedFirst, startedFirst, _ = tally firstLog
    // The cold daemon had no records to answer from: it checked every file.
    Assert.Equal(0, replayedFirst)
    Assert.True(startedFirst >= 3, firstLog)
    between ()

    let second, secondLog =
        withDaemonOutput false clock root (fun cli -> check clock root cli)

    assertGreen second
    let _, _, firstVerdict = first
    let _, _, secondVerdict = second
    Assert.Equal<(string * string) list>(outcomes firstVerdict, outcomes secondVerdict)
    tally secondLog, secondLog

[<Fact(Timeout = 900000)>]
let ``a restarted daemon reaches the same green from check records, with no FCS check and no demand`` () =
    let clock = budget ()

    withTempDir "fshw-check-records-restart" (fun root ->
        prepare clock root |> ignore
        let (replayed, started, demanded), log = checkTwice clock root ignore
        // Every file answered from its record: the three in the project and the
        // `DefaultRunnerReporters.fs` xunit.v3 compiles in after them from the NuGet cache.
        // No plugin lacked a cached answer for one, so none demanded a live check.
        Assert.True((replayed, started, demanded) = (4, 0, 0), log))

[<Fact(Timeout = 900000)>]
let ``an edit while the daemon is stopped live-checks exactly the edited file and the files compiled after it`` () =
    let clock = budget ()

    withTempDir "fshw-check-records-edit" (fun root ->
        let directory = prepare clock root

        let (replayed, started, demanded), log =
            checkTwice clock root (fun () ->
                // An API edit to the middle file: it and every file after it (Tests.fs, and the
                // package's DefaultRunnerReporters.fs) must be checked again; Value.fs before
                // it is answered from its record.
                File.AppendAllText(Path.Combine(directory, "Middle.fs"), "let extra () = 3\n"))

        Assert.True((replayed, started, demanded) = (1, 3, 0), log))

/// A library project of the fixture that compiles `shared/Shared.fs` through a link, as
/// `tests/Replay` does, and one file of its own. It references nothing, so it shares the
/// first tier with `tests/Replay`.
let private linkedLibrary (root: string) (name: string) =
    let directory = Path.Combine(root, "src", name)
    Directory.CreateDirectory directory |> ignore

    File.WriteAllText(
        Path.Combine(directory, $"%s{name}.fsproj"),
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../../shared/Shared.fs" Link="Shared.fs" />
    <Compile Include="Lib.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.1.*" />
  </ItemGroup>
</Project>
"""
    )

    File.WriteAllText(
        Path.Combine(directory, "Lib.fs"),
        $"module %s{name}.Lib\nlet two () = Shared.Helper.one () + 1\n"
    )

    $"src/%s{name}/%s{name}.fsproj"

[<Fact(Timeout = 900000)>]
let ``a file compiled into three projects of one tier is replayed after a restart, with no FCS check`` () =
    // AUTOMATION-1432. The scan checks a shared file once, under the first project of its
    // tier to reach it, and files its record there. That project was the first in the
    // graph's hash order, which is seeded per process, so a restart checked it under
    // another project, missed, and re-checked the files compiled after it there as well.
    let clock = budget ()

    withTempDir "fshw-check-records-linked" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, "shared")) |> ignore
        File.WriteAllText(Path.Combine(root, "shared", "Shared.fs"), "module Shared.Helper\nlet one () = 1\n")
        let libraries = [ linkedLibrary root "LinkA"; linkedLibrary root "LinkB" ]
        let directory = prepare clock root

        let replay = Path.Combine(directory, "Replay.fsproj")

        File.WriteAllText(
            replay,
            File
                .ReadAllText(replay)
                .Replace(
                    """    <Compile Include="Value.fs" />""",
                    """    <Compile Include="../../shared/Shared.fs" Link="Shared.fs" />
    <Compile Include="Value.fs" />"""
                )
        )

        // The build plugin requires every project's output, so the build covers all three.
        File.WriteAllText(
            Path.Combine(root, "Fixture.slnx"),
            "<Solution>\n"
            + String.concat
                ""
                [ for project in "tests/Replay/Replay.fsproj" :: libraries -> $"  <Project Path=\"%s{project}\" />\n" ]
            + "</Solution>\n"
        )

        let config = Path.Combine(root, ".fshw.json")

        File.WriteAllText(
            config,
            File
                .ReadAllText(config)
                .Replace(
                    "\"args\": \"build tests/Replay/Replay.fsproj --disable-build-servers\"",
                    "\"args\": \"build Fixture.slnx --disable-build-servers\""
                )
        )

        Assert.Contains("build Fixture.slnx", File.ReadAllText config)

        for library in libraries do
            let code, text = run clock root "dotnet" [ "restore"; library ]
            Assert.True((code = 0), text)

        commit clock root "a file linked into three projects"

        let (replayed, started, demanded), log = checkTwice clock root ignore
        // Every checkable file once: Replay's four, LinkA's and LinkB's Lib.fs, and
        // Shared.fs under the one project that claims it in both daemons.
        Assert.True((replayed, started, demanded) = (7, 0, 0), log))
