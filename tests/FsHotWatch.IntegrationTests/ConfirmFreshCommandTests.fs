module FsHotWatch.Tests.ConfirmFreshCommandTests

open System.IO
open System.Text.Json
open Xunit
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

// `confirm` answers an unchanged tree from evidence it already holds; `confirm --fresh`
// must not. Real command boundaries only: one owned daemon, one real test project, and
// the fixture's execution marker as the witness that the test body actually ran.

let private prepare clock root =
    let directory = Path.Combine(root, "tests", "ColdProbe")
    Directory.CreateDirectory directory |> ignore
    File.WriteAllText(Path.Combine(directory, "ColdProbe.fsproj"), project)
    File.WriteAllText(Path.Combine(root, ".fshw.json"), config)
    writeValue directory "ColdProbe" "2"
    File.WriteAllText(Path.Combine(directory, "Tests.fs"), testSource "ColdProbe")
    initialize clock root
    // Restored and built BEFORE the daemon starts: this scenario is about what a warm
    // daemon replays, so the first confirm must not race the project's first restore.
    let code, text =
        run clock root "dotnet" [ "build"; "tests/ColdProbe/ColdProbe.fsproj"; "--disable-build-servers" ]

    Assert.True((code = 0), text)

let private runId (verdict: JsonElement) =
    verdict.GetProperty("runId").GetString()

let private treeHash (verdict: JsonElement) =
    verdict.GetProperty("treeHash").GetString()

let private verdictPath root =
    Path.Combine(root, ".fshw", "verdict.json")

[<Fact(Timeout = 900000)>]
let ``confirm replays an unchanged tree and confirm --fresh runs the full suite again`` () =
    let clock = budget ()

    withTempDir "fshw-confirm-fresh" (fun root ->
        prepare clock root
        let marker = markerPath root "ColdProbe"

        withDaemon clock root (fun cli ->
            let earned = confirm clock root cli []
            assertGreen earned
            let _, _, first = earned
            assertScope "full" 1 1 first
            assertSuite "ColdProbe" 1 0 first
            Assert.True(File.Exists marker, "the first confirm did not execute the test")

            // Positive control, CLI layer: the same question about the same bytes is
            // answered from `verdict.json`, which is left byte-identical.
            File.Delete marker
            let before = File.ReadAllText(verdictPath root)
            let replayCode, replayText = run clock root "dotnet" [ cli; "confirm"; "--agent" ]
            Assert.True((replayCode = 0), replayText)
            Assert.Equal(before, File.ReadAllText(verdictPath root))
            Assert.False(File.Exists marker, $"a plain confirm on an unchanged tree re-ran the test:\n{replayText}")

            // `--fresh` launches a new full-suite run under the current model and grades
            // THAT run: a new run id, the test body executed again, ordinary evidence.
            let priorRunIds = snapshotRunIds root
            let fresh = confirm clock root cli [ "--fresh" ]
            assertGreen fresh
            let _, _, third = fresh
            Assert.Equal(treeHash first, treeHash third)
            assertScope "full" 1 1 third
            assertSuite "ColdProbe" 1 0 third
            Assert.NotEqual<string>(runId first, runId third)
            Assert.DoesNotContain(runId third, priorRunIds)
            Assert.True(File.Exists marker, "confirm --fresh did not execute the test")

            // And again: `--fresh` never answers from the verdict the previous
            // `--fresh` wrote.
            File.Delete marker
            let again = confirm clock root cli [ "--fresh" ]
            assertGreen again
            let _, _, fourth = again
            Assert.NotEqual<string>(runId third, runId fourth)
            Assert.True(File.Exists marker, "a second confirm --fresh did not execute the test")))
