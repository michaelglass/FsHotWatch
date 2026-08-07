module FsHotWatch.Tests.IpcOutputTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcOutput

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse extracts count`` () =
    let json = """{"count":2,"files":{},"statuses":{}}"""
    let result = parseDiagnosticsResponse json
    test <@ result.Count = 2 @>

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse extracts files with entries`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    test <@ result.Files.ContainsKey("src/Foo.fs") @>
    let entries = result.Files["src/Foo.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries[0].Plugin = "lint" @>
    test <@ entries[0].Message = "bad name" @>
    test <@ entries[0].Severity = Warning @>
    test <@ entries[0].Line = 17 @>

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse extracts statuses`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null},"lint":{"status":{"tag":"idle"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let result = parseDiagnosticsResponse json
    test <@ result.Statuses.ContainsKey("build") @>

    match result.Statuses["build"].Status with
    | StatusView.Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

// ---------------------------------------------------------------------------
// AN UNREADABLE PLUGIN STATUS IS NOT A PASSING ONE.
//
// `Verdict.read` already states the policy one file away: "a plugin outcome token
// this build does not recognize is NOT dropped and NOT rounded to `Ok` — it becomes
// `Fail`. An unknown state is not a passing state." The IPC parser did the opposite:
// four separate places rounded an unparseable status DOWN to `Idle` — quiescent, no
// failure, omitted from the verdict entirely.
//
// This is a LIVE cross-version hazard, not a hypothetical: `PluginOutcome` gained
// `Wedged` in this batch, so "old CLI, new daemon" is a shape that exists.
//
// Asserted through `hasFailures` — the one predicate both `check` and `confirm`
// decide on — so the tests pin the CONSEQUENCE, not the representation.
// ---------------------------------------------------------------------------

let private responseWithStatus (statusJson: string) =
    parseDiagnosticsResponse (
        """{"count":0,"files":{},"unchecked":0,"statuses":{"mystery":{"status":"""
        + statusJson
        + ""","subtasks":[],"activityTail":[],"lastRun":null}}}"""
    )

[<Fact(Timeout = 15000)>]
let ``a status tag this build does not recognize is a FAILURE, not idle`` () =
    // A newer daemon reporting a state we have no name for. Rounding it to `Idle`
    // makes the plugin quiescent, clean, and INVISIBLE in the verdict's `plugins[]`.
    let resp = responseWithStatus """{"tag":"quantum-superposition"}"""
    test <@ hasFailures false resp @>
    test <@ exitCodeFromResponse false resp = 1 @>

[<Fact(Timeout = 15000)>]
let ``a running status whose since cannot be parsed is a FAILURE, not idle`` () =
    // This one silently defeated ALL of AUTOMATION-147's wedge detection: the wedge
    // classifier only ever fires on `StatusView.Running since`, so a `since` we could
    // not parse turned a WEDGED plugin into an idle one — the 8h36m silence, restored.
    let resp = responseWithStatus """{"tag":"running","since":"not-a-timestamp"}"""
    test <@ hasFailures false resp @>

[<Fact(Timeout = 15000)>]
let ``a status that is not even an object is a FAILURE, not idle`` () =
    let resp = responseWithStatus "\"running\""
    test <@ hasFailures false resp @>

[<Fact(Timeout = 15000)>]
let ``a plugin element with NO status field is a FAILURE, not idle`` () =
    let resp =
        parseDiagnosticsResponse
            """{"count":0,"files":{},"unchecked":0,"statuses":{"mystery":{"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    test <@ hasFailures false resp @>

[<Fact(Timeout = 15000)>]
let ``a recognized idle status is still idle — the fail-closed rule does not swallow the healthy case`` () =
    let resp = responseWithStatus """{"tag":"idle"}"""
    test <@ not (hasFailures false resp) @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse with no errors shows clean message`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse with errors shows file and message`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":0,"detail":null}]},"statuses":{"lint":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("src/Foo.fs") @>
    test <@ output.Contains("[lint]") @>
    test <@ output.Contains("L17") @>
    test <@ output.Contains("bad name") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse with errors shows count summary`` () =
    let json =
        """{"count":2,"files":{"src/A.fs":[{"plugin":"lint","message":"x","severity":"warning","line":1,"column":0,"detail":null}],"src/B.fs":[{"plugin":"build","message":"y","severity":"error","line":2,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("1 error(s), 1 warning(s) in 2 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse returns 0 for count 0`` () =
    let resp =
        { Count = 0
          Files = Map.empty
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 0 @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse returns 1 for errors`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "fcs"
                      Message = "bad"
                      Severity = DiagnosticSeverity.Error
                      Line = 1
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 1 @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse with noWarnFail ignores warnings`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "lint"
                      Message = "bad"
                      Severity = DiagnosticSeverity.Warning
                      Line = 1
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse true resp = 0 @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse without noWarnFail fails on warnings`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "lint"
                      Message = "bad"
                      Severity = DiagnosticSeverity.Warning
                      Line = 1
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 1 @>

// --- renderIpcResult tests ---

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with GetDiagnostics format count 0 returns 0`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"count":0,"files":{},"statuses":{}}"""

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with GetDiagnostics format count > 0 returns 1`` () =
    let result =
        renderIpcResult
            ProgressRenderer.Verbose
            (fun _ -> [])
            false
            """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with status passed returns 0`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"status":"passed"}"""

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with status failed returns 1`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"status":"failed"}"""

    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with error field returns 1`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false """{"error":"something went wrong"}"""

    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with plain text returns 0`` () =
    let result =
        renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false "build completed successfully"

    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with test results JSON containing arrays does not crash`` () =
    let json =
        """{"elapsed":"1.5s","projects":[{"project":"TestProject","status":"passed","output":"ok"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with test results JSON with failed project returns 1`` () =
    let json =
        """{"elapsed":"2.0s","projects":[{"project":"FailProject","status":"failed","output":"FAIL: test1"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with noTestsMatched run returns 3 — refuses to green without evidence`` () =
    // A filtered run that matched NOTHING is reported distinctly and exits 3,
    // never 0. Exit 3 is this tool's established "refuse to green without
    // evidence" code (the same one `confirm` returns on a warm cache).
    //
    // This assertion previously demanded 0. That was wrong and it cost real time:
    // `dotnet fshw test-rerun --filter-class '*LlmCallBudget*'` printed
    // "✓ Tests passed" and exited 0 having run ZERO tests, and a reviewer read
    // that as verification. An exit code that sails through `&&` is not a
    // distinct outcome no matter what the text beside it says.
    let json =
        """{"elapsed":"0.1s","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with an EMPTY projects array returns 3, never "Tests passed"`` () =
    // The case `noTestsMatched` cannot express. `allZeroMatch` is deliberately
    // false for an empty result set — "no project ran" is a different claim from
    // "every project matched nothing" — so this JSON sets neither `hasFailed` nor
    // `noTestsMatched` and used to fall through to `UI.success "Tests passed"`,
    // exit 0. A run that executed nothing at all reported the best possible
    // outcome.
    let json = """{"elapsed":"0.0s","projects":[]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult still returns 0 for a genuine pass — the guard is not a blanket`` () =
    // Positive control for the two assertions above: tightening "nothing ran"
    // must not turn a real green into a refusal. Without this, both tests above
    // would still pass if `renderIpcResult` simply never returned 0.
    let json =
        """{"elapsed":"1.2s","projects":[{"project":"RealProject","status":"passed","output":"Passed! total: 7"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with busy status NEVER returns 0 — no run, no green`` () =
    // AUTOMATION-99. `test-rerun` is the repo's explicit "prove it ran" verb, so
    // a reply that carries NO run result must never exit 0: a vacuous green is
    // exactly the false verdict this ticket exists to kill. It is still DISTINCT
    // from "Tests failed" (nothing is known to be broken) — the message says so —
    // but the exit code is non-zero so no caller can read it as a pass.
    let json =
        """{"status":"busy","message":"the test run did not produce a result within 600s (still queued or running); retry, or raise --wait-sec"}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 1 @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse hides info-severity entries`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"fcs","message":"XML comment is not placed on a valid language element.","severity":"info","line":3,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ not (output.Contains("XML comment")) @>
    test <@ output.Contains("No errors") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse shows warnings but hides info in same file`` () =
    let json =
        """{"count":2,"files":{"src/Foo.fs":[{"plugin":"fcs","message":"XML comment","severity":"info","line":3,"column":0,"detail":null},{"plugin":"format-check","message":"File is not formatted","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("File is not formatted") @>
    test <@ not (output.Contains("XML comment")) @>
    test <@ output.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``formatDiagnosticsResponse excludes info-only files from count`` () =
    let json =
        """{"count":2,"files":{"src/A.fs":[{"plugin":"fcs","message":"XML comment","severity":"info","line":3,"column":0,"detail":null}],"src/B.fs":[{"plugin":"lint","message":"bad","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse ProgressRenderer.Verbose (fun _ -> []) result
    test <@ output.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 15000)>]
let ``exitCodeFromResponse ignores info-severity entries`` () =
    let resp =
        { Count = 1
          Files =
            Map.ofList
                [ "src/Foo.fs",
                  [ { Plugin = "fcs"
                      Message = "XML comment"
                      Severity = DiagnosticSeverity.Info
                      Line = 3
                      Column = 0
                      Detail = None } ] ]
          Statuses = Map.empty
          Coverage = Complete }

    test <@ exitCodeFromResponse false resp = 0 @>

// --- Regression: parsePluginStatuses format drift ---
//
// If parsePluginStatuses rejects the GetStatus JSON shape (e.g. a fixture
// returning `{"plugin": "Completed at ..."}` with a bare-string value instead of
// the real `{"plugin": {"status": "..."}}` object shape), the parse silently
// yields an empty map — which once hung a status-polling consumer for 40+ minutes
// before being caught. These tests pin the accepted vs rejected wire shapes.

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses rejects bare-string values and returns empty`` () =
    let json = """{"plugin": "Completed at 2026-01-01T00:00:00Z"}"""
    test <@ parsePluginStatuses json = Ok Map.empty @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses accepts object-valued entries with status field`` () =
    // The real GetStatus JSON shape. Object-per-plugin with a status string.
    let json =
        """{"plugin": {"status": {"tag": "completed", "at": "2026-01-01T00:00:00Z"}, "subtasks": [], "activityTail": [], "lastRun": null}}"""

    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json
    test <@ Map.containsKey "plugin" parsed @>

// --- Regression: check soundness (false green before the test-prune verdict) ---
//
// THE BUG (observed on alpha.30 against a large consumer repo): `fshw check`
// returned exit 0 "No errors" having computed N affected tests, but BEFORE the
// test-prune run's verdict was captured — the test-prune run launched/finished
// AFTER `check` had already exited green, so real test failures sat behind a
// green exit.
//
// ROOT CAUSE: the CLI `check` (`pollAndRender`) settled on the
// Idle-tolerant `isAllTerminal` status predicate instead of the daemon's
// authoritative `WaitForComplete` verdict. The scan signals its generation as
// soon as FCS check + BatchChecked finish; at that instant test-prune can still
// be Idle (the build's `BuildCompleted` event is queued in its mailbox but its
// handler hasn't run, so it hasn't transitioned Idle->Running yet). `isAllTerminal`
// treats Idle as quiescent and never consults the host's inflight/busy state, so
// the check concluded "settled" and read diagnostics during that Idle window —
// missing the test-prune failure that surfaces only once the run completes.
//
// THE FIX: `pollAndRender` now blocks on `WaitForComplete` (the daemon's
// `waitForVerdict`, which gates on plugin busy/inflight + generation advancement
// + quiescence) before reading diagnostics; status polling is rendering-only.
//
// This test drives `pollAndRender` through the exact ordering with injected,
// fully-deterministic seams (no real daemon, no sleep race):
//   - `getStatus` reports FCS=Completed + test-prune=Idle during the race window
//     (the false-green trap), flipping test-prune to Completed only once the
//     authoritative wait has run.
//   - `waitForComplete` is the authoritative settle: invoking it marks the
//     test-prune run finished (as `waitForVerdict` would, by blocking until the
//     inflight BuildCompleted -> run reaches terminal).
//   - `getErrors` reports the test failure ONLY after the run finished — i.e. a
//     check that reads diagnostics during the Idle window sees a (false) clean.
//
// With the fix the check waits for `waitForComplete`, so the failure is surfaced
// (exit 1). Without it the check stops at `isAllTerminal` while test-prune is Idle
// and reads the clean diagnostics (exit 0) — the false green. The assertion
// `exit = 1` is therefore red-before / green-after.

/// GetStatus JSON: fcs Completed; test-prune Idle until the run has finished,
/// then Completed. The Idle window is the false-green trap.
let private statusJsonFor (testRunFinished: bool) : string =
    let testPruneStatus =
        if testRunFinished then
            """{"tag":"completed","at":"2026-01-01T00:00:00.0000000Z"}"""
        else
            """{"tag":"idle"}"""

    $"""{{"fcs":{{"status":{{"tag":"completed","at":"2026-01-01T00:00:00.0000000Z"}},"subtasks":[],"activityTail":[],"lastRun":null}},"test-prune":{{"status":%s{testPruneStatus},"subtasks":[],"activityTail":[],"lastRun":null}}}}"""

/// GetDiagnostics JSON: complete coverage (unchecked 0). One test-prune failure
/// becomes visible ONLY after the test-prune run finished — before that the
/// ledger is (deceptively) clean, exactly as it is during the Idle race window.
let private diagnosticsJsonFor (testRunFinished: bool) : string =
    if testRunFinished then
        """{"count":1,"files":{"tests/Foo.fs":[{"plugin":"test-prune","message":"1 test failed","severity":"error","line":0,"column":0,"detail":null}]},"statuses":{},"unchecked":0}"""
    else
        """{"count":0,"files":{},"statuses":{},"unchecked":0}"""

[<Fact(Timeout = 15000)>]
let ``pollAndRender waits for the test-prune verdict before deciding (no false green while test-prune is Idle)`` () =
    // Shared mutable: the test-prune run's terminal state. Flipped true ONLY by
    // the authoritative wait, mirroring `waitForVerdict` blocking until the
    // BuildCompleted -> affected-tests run reaches its terminal verdict.
    let mutable testRunFinished = false
    let mutable waitForCompleteCalls = 0

    // Determinism gate (coverage stability): `pollUntilSettled` runs
    // `waitForComplete` on a `Task.Run` and exits as soon as that task's
    // `IsCompleted` is observed true. If the task finishes before the FIRST
    // `isSettled` poll (idle machine), the loop never takes its
    // `if not allDone then Thread.Sleep(200)` arm — so that branch in
    // IpcOutput.fs flips covered/uncovered run-to-run (the observed 28/54 <->
    // 29/54 branch-coverage flake around the 53% floor). We pin it
    // deterministically: `waitForComplete` blocks until the render loop has
    // completed a FULL un-settled iteration (status read + `isSettled` returned
    // false + the sleep arm taken). `getStatus` runs at the top of every
    // iteration; releasing the gate on the SECOND poll (not the first) keeps the
    // task un-finished THROUGH the first `isSettled` check, so the loop is
    // guaranteed to take the wait-and-retry branch once, then converge. The
    // branch is now ALWAYS covered, with no wall-clock race.
    use releaseComplete = new System.Threading.ManualResetEventSlim(false)
    let mutable pollCount = 0

    let waitForScan () : string =
        // The scan generation is signalled; test-prune has NOT yet processed its
        // queued BuildCompleted. This is the false-green window.
        "idle"

    let waitForComplete () : string =
        // The daemon's sound verdict wait: it returns only once the test-prune
        // run has reached terminal. Block until the render loop has done one full
        // un-settled iteration (so `pollUntilSettled` takes its wait-and-retry
        // branch), then mark the run finished.
        releaseComplete.Wait()
        waitForCompleteCalls <- waitForCompleteCalls + 1
        testRunFinished <- true
        statusJsonFor true

    let getStatus () : string =
        // Called at the top of each `pollUntilSettled` iteration, before the
        // `isSettled` check. The first poll observes the task still running (gate
        // closed) so `isSettled` is false and the loop sleeps; the second poll
        // releases the gate, letting `waitForComplete` finish and the loop exit.
        pollCount <- pollCount + 1

        if pollCount >= 2 then
            releaseComplete.Set()

        statusJsonFor testRunFinished

    let getErrors () : string = diagnosticsJsonFor testRunFinished
    let triggerScan () : string = "idle"

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                waitForScan
                waitForComplete
                getStatus
                getErrors
                (fun () ->
                    { IpcParsing.Scope = IpcParsing.FullSuite 1
                      IpcParsing.RunId = None })
                ignore // forceFullRun: never fires — the scope is already full-suite
                triggerScan)

    // The authoritative settle MUST have been consulted...
    test <@ waitForCompleteCalls >= 1 @>
    // ...and the test-prune failure surfaced. Exit 1 (failure) only happens if
    // the check waited for the verdict before reading diagnostics. With the bug
    // (settle on `isAllTerminal` while test-prune is Idle) the check reads the
    // clean ledger during the Idle window and returns exit 0 — the false green.
    test <@ exitCode = 1 @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender surfaces a clean verdict once the test-prune run passes`` () =
    // The legitimate "nothing failed" case: the authoritative wait completes and
    // the ledger is clean -> exit 0. Guards against the fix over-blocking or
    // mis-reporting a genuinely green run.
    //
    // Determinism gate (coverage stability): this test pins the OTHER arm of
    // `pollUntilSettled`'s `if not allDone then Thread.Sleep(200)` branch — the
    // SKIP arm, taken when the verdict task has already completed by the first
    // `isSettled` poll (so no sleep). Without pinning, whether the `Task.Run`
    // beats the first poll is a wall-clock race. We force it: `waitForComplete`
    // signals `completed` when it finishes, and the first `getStatus` (top of the
    // loop, before the `isSettled` check) blocks until that signal — so the loop's
    // first `isSettled` is GUARANTEED true and the sleep arm is skipped. Together
    // with the verdict test (which always takes the sleep arm), both arms of the
    // branch are deterministically covered, stabilising IpcOutput.fs at its peak.
    use completed = new System.Threading.ManualResetEventSlim(false)
    let mutable firstStatusPoll = true

    let waitForComplete () : string =
        let r = statusJsonFor true
        completed.Set()
        r

    let getStatus () : string =
        if firstStatusPoll then
            firstStatusPoll <- false
            // Ensure the verdict task has finished before the loop's first
            // `isSettled` check, so the loop exits without sleeping (skip arm).
            completed.Wait()

        statusJsonFor true

    let cleanDiagnostics () : string =
        """{"count":0,"files":{},"statuses":{},"unchecked":0}"""

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle")
                waitForComplete
                getStatus
                cleanDiagnostics
                (fun () ->
                    { IpcParsing.Scope = IpcParsing.FullSuite 1
                      IpcParsing.RunId = None })
                ignore // forceFullRun: never fires — the scope is already full-suite
                (fun () -> "idle"))

    test <@ exitCode = 0 @>

// --- isDaemonShutdownDuringWait (mid-wait teardown classification) ---
// Regression for AUTOMATION-65: a WaitForComplete that faults because the daemon
// shut down / the pipe dropped mid-wait must be recognised so the check yields a
// diagnostic verdict (exit 2) instead of an opaque crash / silent connection drop.

/// A stand-in whose type name contains "ConnectionLost" — exercises the
/// StreamJsonRpc connection-loss detection without a compile-time dependency on
/// the transport assembly (the production match is by type-name substring).
type private FakeConnectionLostException() =
    inherit exn("connection lost")

[<Fact(Timeout = 15000)>]
let ``isDaemonShutdownDuringWait recognises transport + shutdown faults`` () =
    test <@ isDaemonShutdownDuringWait (System.IO.IOException("pipe is broken")) @>
    test <@ isDaemonShutdownDuringWait (System.IO.EndOfStreamException()) @>
    test <@ isDaemonShutdownDuringWait (System.ObjectDisposedException("pipe")) @>
    test <@ isDaemonShutdownDuringWait (FakeConnectionLostException()) @>
    // The daemon's graceful-shutdown sentinel, propagated back as a remote error.
    test <@ isDaemonShutdownDuringWait (exn ("Error: daemon shutting down")) @>
    // ...and detected through the inner-exception chain.
    test <@ isDaemonShutdownDuringWait (exn ("outer", System.IO.IOException("inner drop"))) @>

[<Fact(Timeout = 15000)>]
let ``isDaemonShutdownDuringWait ignores unrelated faults`` () =
    // A real timeout or an arbitrary bug is NOT a mid-wait teardown — it must not
    // be silently reclassified as a shutdown.
    test <@ not (isDaemonShutdownDuringWait (System.TimeoutException("WaitForComplete timed out after 00:30:00"))) @>
    test <@ not (isDaemonShutdownDuringWait (exn "boom")) @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender returns exit 2 when the daemon drops mid-wait`` () =
    // End-to-end: a faulting WaitForComplete (daemon shut down mid-settle) drives
    // pollAndRender to the diagnostic exit-2 path rather than propagating a crash.
    let waitForComplete () : string =
        raise (System.IO.IOException("pipe is broken"))

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle") // waitForScan
                waitForComplete
                (fun () -> "{}") // getStatus
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""") // getErrors
                (fun () ->
                    { IpcParsing.Scope = IpcParsing.FullSuite 1
                      IpcParsing.RunId = None })
                ignore // forceFullRun: never fires — the scope is already full-suite
                (fun () -> "idle")) // triggerScan

    test <@ exitCode = 2 @>

// --- isVerdictWaitTimeout (verdict-deadline breach classification) ---
// Regression for the 2026-07-13 test-prune wedge: the daemon now bounds a
// client-unbounded WaitForComplete (`resolveVerdictDeadline`), and its
// TimeoutException crosses the RPC boundary as a remote error recognisable
// only by its message. The check must turn that into a diagnostic exit 2
// naming the wedged plugin — never an opaque crash, never an endless wait.

[<Fact(Timeout = 15000)>]
let ``isVerdictWaitTimeout recognises the daemon's deadline breach`` () =
    test
        <@
            isVerdictWaitTimeout (
                System.TimeoutException("WaitForComplete timed out after 01:00:00 — still running: test-prune (1h 0m)")
            )
        @>

    // ...as a remote-invocation error (arbitrary exception type, message-borne) ...
    test <@ isVerdictWaitTimeout (exn "Error: WaitForComplete timed out after 01:00:00 — still running: x") @>
    // ...and through the inner-exception chain.
    test <@ isVerdictWaitTimeout (exn ("outer", exn "WaitForComplete timed out after 00:30:00 — y")) @>

[<Fact(Timeout = 15000)>]
let ``isVerdictWaitTimeout ignores unrelated faults`` () =
    test <@ not (isVerdictWaitTimeout (exn "boom")) @>
    test <@ not (isVerdictWaitTimeout (System.IO.IOException("pipe is broken"))) @>
    test <@ not (isVerdictWaitTimeout (System.TimeoutException("some other timeout"))) @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender returns exit 2 when the verdict deadline is breached`` () =
    // End-to-end: the daemon's deadline TimeoutException drives pollAndRender to
    // the diagnostic exit-2 path (bounded + legible) instead of crashing.
    let waitForComplete () : string =
        raise (System.TimeoutException("WaitForComplete timed out after 01:00:00 — still running: test-prune (1h 0m)"))

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-verdict" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle") // waitForScan
                waitForComplete
                (fun () -> "{}") // getStatus
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""") // getErrors
                (fun () ->
                    { IpcParsing.Scope = IpcParsing.FullSuite 1
                      IpcParsing.RunId = None })
                ignore // forceFullRun: never fires — the scope is already full-suite
                (fun () -> "idle")) // triggerScan

    test <@ exitCode = 2 @>

// ---------------------------------------------------------------------------
// AUTOMATION-117 — `confirm` EARNS its evidence.
//
// `set-scope full` makes the next test run unfiltered. It does not make a run
// HAPPEN. On a warm daemon whose impact DB says nothing changed, the scan provokes
// no run at all, so `confirm` read the LAST (filtered) run's coverage and refused —
// correctly (it had no whole-suite evidence) but uselessly: there was no way to
// produce any. A check that can only be satisfied by luck is a check people route
// around with a shell script, which is exactly how a 40-line unverified bash
// harness ended up making merge decisions on this repo.
//
// So `confirm` now RUNS the suite it demands, and only then judges it.
// ---------------------------------------------------------------------------

/// A `pollAndRender` drive whose test scope starts out impact-filtered and only
/// becomes full-suite once `forceFullRun` has actually been invoked — i.e. the
/// daemon behaves exactly as a warm daemon with nothing to do: it reports the last
/// filtered run until something forces a new one.
let private driveConfirm (checkMode: CheckVerdict.CheckMode) : int * int =
    let mutable forceCalls = 0

    let getTestRun () : TestRunReport =
        if forceCalls > 0 then
            { Scope = FullSuite 1; RunId = None }
        else
            { Scope = ImpactFiltered(0, 1)
              RunId = None }

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-confirm-force" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                checkMode
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle") // waitForScan
                (fun () -> "idle") // waitForComplete
                (fun () -> "{}") // getStatus
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""") // getErrors
                getTestRun
                (fun () -> forceCalls <- forceCalls + 1) // forceFullRun
                (fun () -> "idle")) // triggerScan

    exitCode, forceCalls

[<Fact(Timeout = 15000)>]
let ``a confirm with no full-suite evidence FORCES the run and then goes green`` () =
    // RED BEFORE THE FIX: `confirm` never called `forceFullRun`, read the stale
    // ImpactFiltered scope, and returned exit 3 (UnearnedScope) — with no way for the
    // caller to ever get a different answer. GREEN AFTER: it runs the suite, re-reads
    // what that run actually covered, and earns the verdict.
    let exitCode, forceCalls = driveConfirm CheckVerdict.Confirmation

    test <@ forceCalls = 1 @>
    test <@ exitCode = 0 @>

[<Fact(Timeout = 15000)>]
let ``a confirm that already has full-suite evidence does NOT run the suite twice`` () =
    // The force is a BACKSTOP, not the mechanism. On a cold daemon the scan already
    // provoked the unfiltered run (`set-scope full` was sent first), so the scope reads
    // full-suite here and `confirm` must pay for exactly ONE suite. A check that re-ran
    // the world every time would be a check people turn off.
    let mutable forceCalls = 0

    let exitCode =
        TestHelpers.withTempDir "ipcoutput-confirm-noforce" (fun repoRoot ->
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.Confirmation
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle")
                (fun () -> "idle")
                (fun () -> "{}")
                (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""")
                (fun () -> { Scope = FullSuite 1; RunId = None })
                (fun () -> forceCalls <- forceCalls + 1)
                (fun () -> "idle"))

    test <@ forceCalls = 0 @>
    test <@ exitCode = 0 @>

[<Fact(Timeout = 15000)>]
let ``the inner loop NEVER forces a full suite`` () =
    // `check` is the fast loop. An impact-filtered green is precisely the answer it
    // wants, and a fast loop that secretly runs the whole suite is not a fast loop.
    let exitCode, forceCalls = driveConfirm CheckVerdict.InnerLoop

    test <@ forceCalls = 0 @>
    test <@ exitCode = 0 @>

// ---------------------------------------------------------------------------
// The `coverage` token is what a CURRENT daemon sends. These cover the other
// side of the contract: a CLI talking to an OLDER daemon that does not send it.
// The rule the fallback exists to hold is that an ABSENT field must never be
// read as "ran" — otherwise upgrading the CLI ahead of the daemon silently
// reintroduces the exact green-for-nothing this release removes.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``renderIpcResult reads the coverage token when the daemon sends one`` () =
    let json =
        """{"elapsed":"0.0s","coverage":"no-projects-selected","verifiedNothing":true,"projects":[]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult reads coverage=all-zero-match as a refusal, not a pass`` () =
    let json =
        """{"elapsed":"0.1s","coverage":"all-zero-match","verifiedNothing":true,"noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult trusts coverage=ran over an empty-looking payload`` () =
    // Positive control on the token path: the token is authoritative, so a
    // daemon that says it ran is believed. Without this, a fallback that ignored
    // the token entirely would still satisfy the two tests above.
    let json =
        """{"elapsed":"1.0s","coverage":"ran","verifiedNothing":false,"projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``an OLDER daemon sending no coverage field still cannot report a no-op as a pass`` () =
    // The upgrade-skew case. No `coverage`, no `noTestsMatched`, empty projects —
    // the fallback must reconstruct "no project ran" from the counts rather than
    // defaulting to "ran".
    let noCoverageEmpty = """{"elapsed":"0.0s","projects":[]}"""

    let noCoverageZeroMatch =
        """{"elapsed":"0.1s","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let noCoverageReal =
        """{"elapsed":"1.0s","projects":[{"project":"P","status":"passed","output":"Passed! total: 9"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageEmpty = 3 @>
    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageZeroMatch = 3 @>
    // …and the fallback is still not a blanket refusal.
    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageReal = 0 @>
