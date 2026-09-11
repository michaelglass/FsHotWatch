module FsHotWatch.Tests.ColdCheckCommandTests

open System
open System.IO
open Xunit
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.ColdCheckFixture

// Real command boundaries only: no mocked scope replies, seeded state, or confirm.
let private singleDirectory root =
    Path.Combine(root, "tests", "ColdProbe")

let private prepareSingle clock root hasTest =
    let directory = singleDirectory root
    Directory.CreateDirectory directory |> ignore
    File.WriteAllText(Path.Combine(directory, "ColdProbe.fsproj"), project)
    File.WriteAllText(Path.Combine(root, ".fshw.json"), config)
    writeValue directory "ColdProbe" "1"

    let source =
        if hasTest then
            testSource "ColdProbe"
        else
            "module ColdProbe.Tests\nlet noTestsHere = ColdProbe.Value.answer ()\n"

    File.WriteAllText(Path.Combine(directory, "Tests.fs"), source)
    initialize clock root
    writeValue directory "ColdProbe" "2"

[<Theory(Timeout = 900000)>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``cold check observes both committed and working-copy pre-start edits`` committedEdit =
    let clock = budget ()

    withTempDir "fshw-cold-check-command" (fun root ->
        prepareSingle clock root true

        if committedEdit then
            commit clock root "source edit before first daemon"

        withDaemon clock root (fun cli ->
            let first = check clock root cli
            assertGreen first
            let _, _, verdict = first
            assertScope "full" 1 1 verdict
            assertSuite "ColdProbe" 1 0 verdict
            Assert.Equal("observed 2", File.ReadAllText(markerPath root "ColdProbe"))))

[<Fact(Timeout = 900000)>]
let ``cold check refuses a real zero-test repository`` () =
    let clock = budget ()

    withTempDir "fshw-cold-check-empty" (fun root ->
        prepareSingle clock root false

        withDaemon clock root (fun cli ->
            let code, text, verdict = check clock root cli
            Assert.True(code <> 0, $"a zero-test repository was accepted:\n{text}")
            Assert.NotEqual<string>("green", verdict.GetProperty("outcome").GetProperty("kind").GetString())
            Assert.False(File.Exists(markerPath root "ColdProbe"))
            // A restore, analyzer, or transport failure must not satisfy the
            // control. Require the real runner's empty receipt and successful
            // completion of every plugin unrelated to test evidence.
            Assert.Contains(
                verdict.GetProperty("suites").EnumerateArray(),
                fun suite ->
                    suite.GetProperty("project").GetString() = "ColdProbe"
                    && suite.GetProperty("total").GetInt32() = 0
                    && suite.GetProperty("passed").GetInt32() = 0
                    && suite.GetProperty("failed").GetInt32() = 0
            )

            Assert.DoesNotContain(
                verdict.GetProperty("plugins").EnumerateArray(),
                fun plugin ->
                    plugin.GetProperty("name").GetString() <> "test-prune"
                    && plugin.GetProperty("outcome").GetString() <> "ok"
            )

            let emptySuite =
                verdict.GetProperty("suites").EnumerateArray()
                |> Seq.find (fun suite -> suite.GetProperty("project").GetString() = "ColdProbe")

            let runDirectory =
                Path.GetDirectoryName(Path.Combine(root, emptySuite.GetProperty("ctrf").GetString()))

            let runnerOutput =
                File.ReadAllText(Path.Combine(runDirectory, "ColdProbe.output.log"))

            Assert.True(
                runnerOutput.Contains("Zero tests ran", StringComparison.OrdinalIgnoreCase),
                $"the empty fixture must be refused for zero tests, not a runner failure:\n{runnerOutput}"
            )))

let private preparePair clock root =
    for name in [ "ColdA"; "ColdB" ] do
        let directory = Path.Combine(root, "tests", name)
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory, name + ".fsproj"), project)
        writeValue directory name "2"
        File.WriteAllText(Path.Combine(directory, "Tests.fs"), testSource name)

    File.WriteAllText(
        Path.Combine(root, "ColdPair.slnx"),
        """<Solution><Project Path="tests/ColdA/ColdA.fsproj" /><Project Path="tests/ColdB/ColdB.fsproj" /></Solution>"""
    )

    File.WriteAllText(
        Path.Combine(root, ".fshw.json"),
        """{
  "build": { "command": "dotnet", "args": "build ColdPair.slnx --disable-build-servers" },
  "format": false,
  "lint": false,
  "tests": {
    "beforeRun": "dotnet build ColdPair.slnx --disable-build-servers",
    "projects": [
      { "project": "ColdA", "command": "dotnet", "args": "run --project tests/ColdA/ColdA.fsproj --no-build --",
        "filterTemplate": "--filter-class {classes}", "classJoin": " " },
      { "project": "ColdB", "command": "dotnet", "args": "run --project tests/ColdB/ColdB.fsproj --no-build --",
        "filterTemplate": "--filter-class {classes}", "classJoin": " " }
    ]
  }
}
"""
    )

    initialize clock root

let private assertOnlyA (verdict: System.Text.Json.JsonElement) =
    assertScope "filtered" 1 2 verdict
    assertSuite "ColdA" 1 0 verdict

    Assert.DoesNotContain(
        verdict.GetProperty("suites").EnumerateArray(),
        fun suite -> suite.GetProperty("project").GetString() = "ColdB"
    )

[<Fact(Timeout = 900000)>]
let ``same-tree check retains filtered receipts and a changed failing tree cannot borrow them`` () =
    let clock = budget ()

    withTempDir "fshw-filtered-check-command" (fun root ->
        preparePair clock root

        withDaemon clock root (fun cli ->
            // This full baseline is earned by the ordinary command over real tests.
            let baseline = check clock root cli
            assertGreen baseline
            let _, _, full = baseline
            assertScope "full" 2 2 full
            assertSuite "ColdA" 1 0 full
            assertSuite "ColdB" 1 0 full

            let a = Path.Combine(root, "tests", "ColdA")
            // Remove only the fixture's untracked execution marker. Its recreation
            // proves this receipt came from a new A execution, not the baseline.
            File.Delete(markerPath root "ColdA")
            // Same answer, distinct implementation, no B edit or dependency on A.
            writeValue a "ColdA" "1 + 1"
            let selected = check clock root cli
            assertGreen selected
            let _, _, filtered = selected
            assertOnlyA filtered

            Assert.NotEqual<string>(
                full.GetProperty("treeHash").GetString(),
                filtered.GetProperty("treeHash").GetString()
            )

            Assert.Equal("observed 2", File.ReadAllText(markerPath root "ColdA"))

            let priorRunIds = snapshotRunIds root
            let repeated = check clock root cli
            assertGreen repeated
            let _, _, same = repeated
            Assert.Equal(filtered.GetProperty("treeHash").GetString(), same.GetProperty("treeHash").GetString())

            match same.GetProperty("scope").GetProperty("kind").GetString() with
            | "filtered" -> assertOnlyA same
            // A fresh full run is stronger applicable evidence. Merely relabelling
            // the old baseline as full cannot satisfy the new-run CTRF checks.
            | "full" -> assertFreshFullPair root priorRunIds same
            | kind -> Assert.Fail($"same-tree check lost positive evidence: {kind}")

            // A prior applicable receipt cannot cover these new, genuinely failing bytes.
            writeValue a "ColdA" "3"
            let failedCode, failedText, failed = check clock root cli
            Assert.True(failedCode <> 0, failedText + "\n" + failed.GetRawText())
            Assert.NotEqual<string>("green", failed.GetProperty("outcome").GetProperty("kind").GetString())

            Assert.NotEqual<string>(
                same.GetProperty("treeHash").GetString(),
                failed.GetProperty("treeHash").GetString()
            )

            assertSuite "ColdA" 0 1 failed))
