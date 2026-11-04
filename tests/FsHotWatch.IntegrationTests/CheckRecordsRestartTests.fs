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

/// Each full scan a daemon logged (`Checked N files (T tiers), skipped M, unchecked U`): the
/// files it checked and the files it left unchecked, in order.
let private scans (log: string) =
    Regex.Matches(log, @"\[scan\] \S+ Checked (\d+) files \(\d+ tiers\), skipped \d+, unchecked (\d+)")
    |> Seq.map (fun found -> int found.Groups[1].Value, int found.Groups[2].Value)
    |> List.ofSeq

/// How many full scans a daemon ran, after asserting the scans and the tally agree with what its
/// FIRST scan did: `firstReplayed` files answered from records and `firstStarted` checked in FCS.
///
/// A daemon scans as it starts, and `check` asks for a scan of its own. A request admitted while
/// a scan runs is answered by it; one admitted after it completed runs a second full scan
/// (`answeredByLastScan` in Daemon.fs). Which one happens is a race between that scan and the
/// CLI process starting up, and a scan answered from records takes well under a second, so on a
/// loaded runner the request can land after it. The tally counts across scans, so a second scan
/// adds every file again, each answered from the record the first scan read or filed: none is
/// started in FCS, and no plugin demands one.
let private assertTally (firstReplayed, firstStarted) (log: string) (replayed, started, demanded) =
    let files = firstReplayed + firstStarted
    let ran = scans log

    Assert.True(
        ran.Length = 1 || ran.Length = 2,
        $"expected the start-up scan and at most the one `check` asked for, saw %d{ran.Length}:\n%s{log}"
    )

    for checkedFiles, unchecked in ran do
        Assert.True((checkedFiles, unchecked) = (files, 0), log)

    Assert.True((replayed, started, demanded) = (firstReplayed + files * (ran.Length - 1), firstStarted, 0), log)
    ran.Length

/// Each plugin's name and outcome in a verdict.
let private outcomes (verdict: JsonElement) =
    verdict.GetProperty("plugins").EnumerateArray()
    |> Seq.map (fun plugin -> plugin.GetProperty("name").GetString(), plugin.GetProperty("outcome").GetString())
    |> Seq.sort
    |> List.ofSeq

/// `root` as the daemon's child CLI sees it: macOS temp paths are symlinks, and the daemon
/// keys its pipe and its records by the physical directory.
let private physicalRoot clock root =
    let code, physical = run clock root "/bin/pwd" [ "-P" ]
    Assert.True((code = 0), physical)
    physical.Trim()

/// Wait until the daemon serving `root` has completed its startup scan.
let private startupScanSettled clock root =
    let pipe = FsHotWatch.Cli.Program.computePipeName (physicalRoot clock root)

    Async.RunSynchronously(FsHotWatch.Ipc.IpcClient.waitForScan pipe 0L, remaining clock 120000)
    |> ignore

/// The `check` records on file for `root`'s repository, in the store its daemon opens.
let private checkRecordsOnFile clock root =
    let store = FsHotWatch.OutcomeStore.directoryFor (physicalRoot clock root)

    (FsHotWatch.OutcomeStore.readAll store).Records
    |> List.filter (fun record -> record.Key.Kind = FsHotWatch.CheckRecords.CheckKind)

/// Check `root` with a cold daemon, stop it, run `between`, then check again with a new one;
/// with `settleFirst`, only once the new daemon has completed its start-up scan. The second
/// daemon's tally and log.
let private checkTwice clock root (between: unit -> unit) (settleFirst: bool) =
    // Cold means no record on file, not "the start-up scan replayed nothing" (AUTOMATION-1460):
    // the build plugin's restore rewrites obj/project.assets.json while the daemon starts, and
    // the change batch that follows checks every file and files its records. A start-up scan
    // that reaches the files after that batch replays them, from records this daemon filed.
    Assert.Empty(checkRecordsOnFile clock root)

    let first, firstLog =
        withDaemonOutput true clock root (fun cli -> check clock root cli)

    assertGreen first
    let _, startedFirst, _ = tally firstLog
    Assert.True(startedFirst >= 3, firstLog)
    // The store the assertion above found empty is the one this daemon filed into.
    Assert.NotEmpty(checkRecordsOnFile clock root)
    between ()

    let second, secondLog =
        withDaemonOutput false clock root (fun cli ->
            if settleFirst then
                startupScanSettled clock root

            check clock root cli)

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
        let found, log = checkTwice clock root ignore false
        // Every file answered from its record: the three in the project and the
        // `DefaultRunnerReporters.fs` xunit.v3 compiles in after them from the NuGet cache.
        // No plugin lacked a cached answer for one, so none demanded a live check.
        assertTally (4, 0) log found |> ignore)

[<Fact(Timeout = 900000)>]
let ``a check sent after the restarted daemon's startup scan completed is answered from check records too`` () =
    let clock = budget ()

    withTempDir "fshw-check-records-settled" (fun root ->
        prepare clock root |> ignore
        let found, log = checkTwice clock root ignore true
        // Admitted after the start-up scan completed, `check`'s request ran a second full scan
        // (the path that only a slow runner took by chance): it too answered every file from
        // its record.
        Assert.Equal(2, assertTally (4, 0) log found))

[<Fact(Timeout = 900000)>]
let ``an edit while the daemon is stopped live-checks exactly the edited file and the files compiled after it`` () =
    let clock = budget ()

    withTempDir "fshw-check-records-edit" (fun root ->
        let directory = prepare clock root

        let found, log =
            checkTwice
                clock
                root
                (fun () ->
                    // An API edit to the middle file: it and every file after it (Tests.fs, and the
                    // package's DefaultRunnerReporters.fs) must be checked again; Value.fs before
                    // it is answered from its record.
                    File.AppendAllText(Path.Combine(directory, "Middle.fs"), "let extra () = 3\n"))
                false

        assertTally (1, 3) log found |> ignore)

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

        let found, log = checkTwice clock root ignore false
        // Every checkable file once: Replay's four, LinkA's and LinkB's Lib.fs, and
        // Shared.fs under the one project that claims it in both daemons.
        assertTally (7, 0) log found |> ignore)
