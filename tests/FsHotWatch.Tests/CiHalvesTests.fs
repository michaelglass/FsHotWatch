/// `mise run ci` runs two halves through scripts/ci-halves.sh: the full-suite `confirm`,
/// then the integration suite `.fshw.json` keeps out of the daemon's test slot. The
/// script runs the integration half WHATEVER the confirm half did, and ends with one
/// line per half saying what ran and what was reused.
///
/// AUTOMATION-1689: a post-land spot check printed confirm's "reused — this confirm ran no
/// tests" and its config notice "NOT RUN — tests/FsHotWatch.IntegrationTests", then exited
/// 0. The integration suite HAD run (151 passed, macOS); the notice was the declared
/// exclusion. Nothing in the log said which half ran what, so a reader could not tell. Each
/// test below fakes the two commands, so it pins the script's contract, not the suites.
module FsHotWatch.Tests.CiHalvesTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks
open FsHotWatch.Tests.TestHelpers

/// Run the script in `dir` with the two commands; the exit code and everything it printed.
let private runHalves (dir: string) (confirm: string) (integration: string) =
    let start = ProcessStartInfo("bash")
    start.WorkingDirectory <- dir
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add(Path.Combine(repoRoot (), "scripts", "ci-halves.sh"))
    start.ArgumentList.Add confirm
    start.ArgumentList.Add integration
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

/// The summary line the script prints for `half`.
let private summaryOf (half: string) (output: string) =
    output.Split('\n')
    |> Array.filter (fun line -> line.StartsWith($"ci: %s{half} ", StringComparison.Ordinal))
    |> Array.toList

/// A verdict.json with one run and the given suites. `producedAt` is literal text, so a
/// fake confirm can substitute the moment it ran (see `confirmThatRuns`).
let private verdictJson (runId: string) (producedAt: string) (suites: string list) =
    $"""{{ "runId": "%s{runId}", "producedAt": "%s{producedAt}", "outcome": {{ "kind": "green" }},
  "suites": [ %s{String.Join(", ", suites)} ] }}"""

let private suite (project: string) (total: int) =
    $"""{{ "project": "%s{project}", "total": %d{total}, "passed": %d{total}, "failed": 0, "skipped": 0 }}"""

let private reusedSuite (project: string) (total: int) (fromRun: string) =
    $"""{{ "project": "%s{project}", "total": %d{total}, "passed": %d{total}, "failed": 0, "skipped": 0,
  "reusedFrom": {{ "worktree": "/w", "runId": "%s{fromRun}", "earnedAt": "2026-01-01T00:00:00Z", "key": "k" }} }}"""

/// A fake `confirm` that writes `verdict` as THIS run's verdict, stamped now, and exits `code`.
let private confirmThatRuns (dir: string) (verdict: string) (code: int) =
    File.WriteAllText(Path.Combine(dir, "next-verdict.json"), verdict)

    "mkdir -p .fshw && sed \"s/PRODUCED_AT/$(date -u +%Y-%m-%dT%H:%M:%S.1234567Z)/\" next-verdict.json > .fshw/verdict.json"
    + $" && exit %d{code}"

/// A verdict published before this run started: what `confirm` answers an unchanged tree from.
let private earlierVerdict (dir: string) (verdict: string) =
    Directory.CreateDirectory(Path.Combine(dir, ".fshw")) |> ignore
    File.WriteAllText(Path.Combine(dir, ".fshw", "verdict.json"), verdict)

/// A fake integration run: prints MTP's summary block and leaves a marker that it ran.
let private integrationThatRuns (total: int) (failed: int) =
    let code = if failed = 0 then 0 else 2

    $"touch integration-ran && printf 'Test run summary: x\\n  total: %d{total}\\n  failed: %d{failed}\\n' && exit %d{code}"

[<Fact>]
let ``a reused confirm is named as reused, and the integration half still runs`` () =
    withTempDir "ci-halves-reused" (fun dir ->
        earlierVerdict dir (verdictJson "6bf6c9ad" "2026-11-14T05:47:00.0000000Z" [ suite "FsHotWatch.Tests" 8022 ])

        let code, output = runHalves dir "true" (integrationThatRuns 151 0)

        test <@ code = 0 @>
        test <@ File.Exists(Path.Combine(dir, "integration-ran")) @>

        test
            <@
                summaryOf "confirm" output = [ "ci: confirm      REUSED run 6bf6c9ad (2026-11-14T05:47:00.0000000Z) — this ci ran no confirm tests" ]
            @>

        test <@ summaryOf "integration" output = [ "ci: integration  ran 151 tests, 0 failed" ] @>)

[<Fact>]
let ``a confirm that runs is named with the tests it ran and the suites it reused`` () =
    withTempDir "ci-halves-ran" (fun dir ->
        let verdict =
            verdictJson
                "a5c98de0"
                "PRODUCED_AT"
                [ suite "FsHotWatch.Tests" 8022
                  suite "TestPrune.Tests" 1155
                  reusedSuite "TestPrune.Trace.Tests" 576 "11112222" ]

        let code, output =
            runHalves dir (confirmThatRuns dir verdict 0) (integrationThatRuns 151 0)

        test <@ code = 0 @>

        test
            <@
                summaryOf "confirm" output = [ "ci: confirm      ran 9177 tests in run a5c98de0; 1 suite(s) reused from earlier runs: TestPrune.Trace.Tests (run 11112222)" ]
            @>)

[<Fact>]
let ``a red confirm still runs the integration half, and the run is red`` () =
    withTempDir "ci-halves-red-confirm" (fun dir ->
        let verdict = verdictJson "deadbeef" "PRODUCED_AT" [ suite "FsHotWatch.Tests" 10 ]

        let code, output =
            runHalves dir (confirmThatRuns dir verdict 1) (integrationThatRuns 151 0)

        test <@ code = 1 @>
        test <@ File.Exists(Path.Combine(dir, "integration-ran")) @>
        test <@ summaryOf "confirm" output = [ "ci: confirm      ran 10 tests in run deadbeef — FAILED (exit 1)" ] @>
        test <@ summaryOf "integration" output = [ "ci: integration  ran 151 tests, 0 failed" ] @>)

[<Fact>]
let ``a confirm that fails before it writes a verdict is not mistaken for a reuse`` () =
    withTempDir "ci-halves-no-verdict" (fun dir ->
        // A stale verdict on disk from earlier: the failing pre-step must not be read as it.
        earlierVerdict dir (verdictJson "6bf6c9ad" "2026-11-14T05:47:00.0000000Z" [ suite "FsHotWatch.Tests" 1 ])

        let code, output = runHalves dir "exit 3" (integrationThatRuns 151 0)

        test <@ code = 3 @>
        test <@ File.Exists(Path.Combine(dir, "integration-ran")) @>

        test
            <@ summaryOf "confirm" output = [ "ci: confirm      produced no verdict of this run — FAILED (exit 3)" ] @>)

[<Fact>]
let ``a red integration suite reds the run`` () =
    withTempDir "ci-halves-red-integration" (fun dir ->
        earlierVerdict dir (verdictJson "6bf6c9ad" "2026-11-14T05:47:00.0000000Z" [ suite "FsHotWatch.Tests" 1 ])

        let code, output = runHalves dir "true" (integrationThatRuns 151 3)

        test <@ code = 2 @>
        test <@ summaryOf "integration" output = [ "ci: integration  ran 151 tests, 3 failed — FAILED (exit 2)" ] @>)

[<Fact>]
let ``an integration half that reports no tests is NOT RUN, and the run is red`` () =
    withTempDir "ci-halves-integration-not-run" (fun dir ->
        earlierVerdict dir (verdictJson "6bf6c9ad" "2026-11-14T05:47:00.0000000Z" [ suite "FsHotWatch.Tests" 1 ])

        // Exit 0 with no test summary: a green that ran nothing must not pass.
        let code, output = runHalves dir "true" "echo nothing here"

        test <@ code = 1 @>

        test
            <@ summaryOf "integration" output = [ "ci: integration  NOT RUN — no test summary in its output (exit 0)" ] @>)
