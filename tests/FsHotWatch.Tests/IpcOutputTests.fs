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
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

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
let ``renderIpcResult with noTestsMatched run returns 0 (distinct, not a failure)`` () =
    // FIX 2: a filtered run that matched NOTHING is reported distinctly, exit 0,
    // and must NOT render as "Tests failed" / "Tests passed".
    let json =
        """{"elapsed":"0.1s","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with busy status returns 0 (distinct in-progress, not a verdict)`` () =
    // FIX 2: the force-rerun waited and a run is still in progress — distinct
    // non-failure signal, exit 0 so the caller retries rather than seeing red.
    let json =
        """{"status":"busy","message":"a test run is still in progress; retry once it finishes"}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

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
    let parsed = parsePluginStatuses json
    test <@ Map.isEmpty parsed @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses accepts object-valued entries with status field`` () =
    // The real GetStatus JSON shape. Object-per-plugin with a status string.
    let json =
        """{"plugin": {"status": {"tag": "completed", "at": "2026-01-01T00:00:00Z"}, "subtasks": [], "activityTail": [], "lastRun": null}}"""

    let parsed = parsePluginStatuses json
    test <@ Map.containsKey "plugin" parsed @>

// --- Regression: check-gate soundness (false green before the test-prune verdict) ---
//
// THE BUG (observed on alpha.30 against a large consumer repo): `fshw check`
// returned exit 0 "No errors" having computed N affected tests, but BEFORE the
// test-prune run's verdict was captured — the test-prune run launched/finished
// AFTER `check` had already exited green, so real test failures sat behind a
// green exit.
//
// ROOT CAUSE: the CLI `check` gate (`pollAndRender`) settled on the
// Idle-tolerant `isAllTerminal` status predicate instead of the daemon's
// authoritative `WaitForComplete` verdict. The scan signals its generation as
// soon as FCS check + BatchChecked finish; at that instant test-prune can still
// be Idle (the build's `BuildCompleted` event is queued in its mailbox but its
// handler hasn't run, so it hasn't transitioned Idle->Running yet). `isAllTerminal`
// treats Idle as quiescent and never consults the host's inflight/busy state, so
// the gate concluded "settled" and read diagnostics during that Idle window —
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
//     gate that reads diagnostics during the Idle window sees a (false) clean.
//
// With the fix the gate waits for `waitForComplete`, so the failure is surfaced
// (exit 1). Without it the gate stops at `isAllTerminal` while test-prune is Idle
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
        pollAndRender
            ProgressRenderer.Agent
            (fun _ -> [])
            false
            waitForScan
            waitForComplete
            getStatus
            getErrors
            triggerScan

    // The authoritative settle MUST have been consulted...
    test <@ waitForCompleteCalls >= 1 @>
    // ...and the test-prune failure surfaced. Exit 1 (failure) only happens if
    // the gate waited for the verdict before reading diagnostics. With the bug
    // (settle on `isAllTerminal` while test-prune is Idle) the gate reads the
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
        pollAndRender
            ProgressRenderer.Agent
            (fun _ -> [])
            false
            (fun () -> "idle")
            waitForComplete
            getStatus
            cleanDiagnostics
            (fun () -> "idle")

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
        pollAndRender
            ProgressRenderer.Agent
            (fun _ -> [])
            false
            (fun () -> "idle") // waitForScan
            waitForComplete
            (fun () -> "{}") // getStatus
            (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""") // getErrors
            (fun () -> "idle") // triggerScan

    test <@ exitCode = 2 @>
