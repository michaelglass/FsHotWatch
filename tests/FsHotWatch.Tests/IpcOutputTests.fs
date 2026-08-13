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
// `Verdict.read` states the policy one file away: an outcome token this build does not
// recognize becomes `Fail`, never `Ok`. The IPC parser did the opposite — four places
// rounded an unparseable status DOWN to `Idle`: quiescent, no failure, omitted from the
// verdict entirely. A live cross-version hazard, since `PluginOutcome` gained `Wedged`.
//
// Asserted through `hasFailures` — the one predicate both `check` and `confirm` decide on —
// so these pin the CONSEQUENCE, not the representation.
// ---------------------------------------------------------------------------

let private responseWithStatus (statusJson: string) =
    parseDiagnosticsResponse (
        """{"count":0,"files":{},"unchecked":0,"statuses":{"mystery":{"status":"""
        + statusJson
        + ""","subtasks":[],"activityTail":[],"lastRun":null}}}"""
    )

[<Fact(Timeout = 15000)>]
let ``a status tag this build does not recognize is a FAILURE, not idle`` () =
    // Rounding a newer daemon's unknown state to `Idle` makes the plugin quiescent, clean,
    // and INVISIBLE in the verdict's `plugins[]`.
    let resp = responseWithStatus """{"tag":"quantum-superposition"}"""
    test <@ hasFailures false resp @>
    test <@ exitCodeFromResponse false resp = 1 @>

[<Fact(Timeout = 15000)>]
let ``a running status whose since cannot be parsed is a FAILURE, not idle`` () =
    // This silently defeated all of AUTOMATION-147's wedge detection: the classifier only
    // fires on `StatusView.Running since`, so an unparseable `since` turned a WEDGED plugin
    // into an idle one.
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
    // Exit 3 is this tool's "refuse to green without evidence" code. The trap: this
    // assertion once demanded 0, so a filter matching zero tests printed "✓ Tests passed"
    // and exited 0 — an exit code that sails through `&&` is not a distinct outcome no
    // matter what the text beside it says.
    let json =
        """{"elapsed":"0.1s","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with an EMPTY projects array returns 3, never "Tests passed"`` () =
    // The case `noTestsMatched` cannot express: `allZeroMatch` is deliberately false for an
    // empty result set ("no project ran" ≠ "every project matched nothing"), so this JSON
    // sets neither flag and used to fall through to `UI.success "Tests passed"`, exit 0.
    let json = """{"elapsed":"0.0s","projects":[]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult still returns 0 for a genuine pass — the guard is not a blanket`` () =
    // Positive control for the two assertions above: without it, both would still pass if
    // `renderIpcResult` simply never returned 0.
    let json =
        """{"elapsed":"1.2s","projects":[{"project":"RealProject","status":"passed","output":"Passed! total: 7"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 15000)>]
let ``renderIpcResult with busy status NEVER returns 0 — no run, no green`` () =
    // AUTOMATION-99: `test-rerun` is the "prove it ran" verb, so a reply carrying NO run
    // result must never exit 0. The message still distinguishes it from "Tests failed"
    // (nothing is known to be broken), but the exit code is non-zero either way.
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
// If parsePluginStatuses rejects the GetStatus JSON shape, the parse silently yields an
// empty map — which once hung a status-polling consumer for 40+ minutes before being
// caught. These pin the accepted vs rejected wire shapes.

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses rejects bare-string values and returns empty`` () =
    let json = """{"plugin": "Completed at 2026-01-01T00:00:00Z"}"""
    test <@ parsePluginStatuses json = Ok Map.empty @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses accepts object-valued entries with status field`` () =
    let json =
        """{"plugin": {"status": {"tag": "completed", "at": "2026-01-01T00:00:00Z"}, "subtasks": [], "activityTail": [], "lastRun": null}}"""

    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json
    test <@ Map.containsKey "plugin" parsed @>

// --- Regression: check soundness (false green before the test-prune verdict) ---
//
// The scan signals its generation as soon as FCS check + BatchChecked finish; at that
// instant test-prune can still be Idle (its queued `BuildCompleted` has not been handled,
// so it has not transitioned Idle->Running). `isAllTerminal` treats Idle as quiescent and
// never consults the host's inflight/busy state, so `check` concluded "settled", read
// diagnostics during that Idle window, and exited 0 while real test failures were still
// pending. `pollAndRender` now blocks on `WaitForComplete` before reading diagnostics;
// status polling is rendering-only.
//
// The seams below reproduce the exact ordering deterministically — no real daemon, no
// sleep race — so `exit = 1` is red-before / green-after.

/// GetStatus JSON: fcs Completed; test-prune Idle until the run has finished, then
/// Completed. The Idle window is the false-green trap.
let private statusJsonFor (testRunFinished: bool) : string =
    let testPruneStatus =
        if testRunFinished then
            """{"tag":"completed","at":"2026-01-01T00:00:00.0000000Z"}"""
        else
            """{"tag":"idle"}"""

    $"""{{"fcs":{{"status":{{"tag":"completed","at":"2026-01-01T00:00:00.0000000Z"}},"subtasks":[],"activityTail":[],"lastRun":null}},"test-prune":{{"status":%s{testPruneStatus},"subtasks":[],"activityTail":[],"lastRun":null}}}}"""

/// GetDiagnostics JSON: complete coverage (unchecked 0). The one test-prune failure becomes
/// visible ONLY after the run finished — before that the ledger is deceptively clean,
/// exactly as during the Idle race window.
let private diagnosticsJsonFor (testRunFinished: bool) : string =
    if testRunFinished then
        """{"count":1,"files":{"tests/Foo.fs":[{"plugin":"test-prune","message":"1 test failed","severity":"error","line":0,"column":0,"detail":null}]},"statuses":{},"unchecked":0}"""
    else
        """{"count":0,"files":{},"statuses":{},"unchecked":0}"""

[<Fact(Timeout = 15000)>]
let ``pollAndRender waits for the test-prune verdict before deciding (no false green while test-prune is Idle)`` () =
    // The test-prune run's terminal state, flipped true ONLY by the authoritative wait —
    // mirroring `waitForVerdict` blocking until the BuildCompleted -> run reaches terminal.
    let mutable testRunFinished = false
    let mutable waitForCompleteCalls = 0

    // Determinism gate: `pollUntilSettled` runs `waitForComplete` on a `Task.Run` and exits
    // as soon as it observes `IsCompleted`. On an idle machine that can happen before the
    // FIRST `isSettled` poll, so the `if not allDone then Thread.Sleep(200)` arm flips
    // covered/uncovered run-to-run. Releasing this gate on the SECOND `getStatus` (not the
    // first) keeps the task un-finished through the first `isSettled` check, so the loop
    // always takes the wait-and-retry branch once, with no wall-clock race.
    use releaseComplete = new System.Threading.ManualResetEventSlim(false)
    let mutable pollCount = 0

    let waitForScan () : string =
        // Generation signalled; test-prune has NOT yet processed its queued
        // BuildCompleted. This is the false-green window.
        "idle"

    let waitForComplete () : string =
        // The sound verdict wait: returns only once the run has reached terminal. Blocks
        // until the render loop has done one full un-settled iteration.
        releaseComplete.Wait()
        waitForCompleteCalls <- waitForCompleteCalls + 1
        testRunFinished <- true
        statusJsonFor true

    let getStatus () : string =
        // Runs at the top of each iteration, before the `isSettled` check: the first poll
        // leaves the gate closed so the loop sleeps; the second releases it.
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
    // ...and exit 1 only happens if the check waited for the verdict before reading
    // diagnostics. Settling on `isAllTerminal` reads the clean ledger during the Idle
    // window and returns 0 — the false green.
    test <@ exitCode = 1 @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender surfaces a clean verdict once the test-prune run passes`` () =
    // The legitimate "nothing failed" case — guards against the fix over-blocking or
    // mis-reporting a genuinely green run.
    //
    // Determinism gate: this pins the OTHER arm of `pollUntilSettled`'s
    // `if not allDone then Thread.Sleep(200)` branch — the SKIP arm, taken when the verdict
    // task has already completed by the first `isSettled` poll. Blocking the first
    // `getStatus` on `completed` guarantees that ordering instead of racing the `Task.Run`.
    use completed = new System.Threading.ManualResetEventSlim(false)
    let mutable firstStatusPoll = true

    let waitForComplete () : string =
        let r = statusJsonFor true
        completed.Set()
        r

    let getStatus () : string =
        if firstStatusPoll then
            firstStatusPoll <- false
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
// AUTOMATION-65: a WaitForComplete that faults because the daemon shut down or the pipe
// dropped mid-wait must be recognised, so the check yields a diagnostic verdict (exit 2)
// rather than an opaque crash or a silent connection drop.

/// A stand-in whose type NAME contains "ConnectionLost" — exercises the StreamJsonRpc
/// connection-loss detection without a compile-time dependency on the transport assembly
/// (the production match is by type-name substring).
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
    // A real timeout or an arbitrary bug is NOT a mid-wait teardown.
    test <@ not (isDaemonShutdownDuringWait (System.TimeoutException("WaitForComplete timed out after 00:30:00"))) @>
    test <@ not (isDaemonShutdownDuringWait (exn "boom")) @>

[<Fact(Timeout = 15000)>]
let ``pollAndRender returns exit 2 when the daemon drops mid-wait`` () =
    // End-to-end: a faulting WaitForComplete reaches the diagnostic exit-2 path rather than
    // propagating a crash.
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
// The daemon bounds a client-unbounded WaitForComplete (`resolveVerdictDeadline`), and its
// TimeoutException crosses the RPC boundary as a remote error recognisable ONLY by its
// message. The check must turn that into a diagnostic exit 2 naming the wedged plugin —
// never an opaque crash, never an endless wait.

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
    // End-to-end: the daemon's deadline TimeoutException reaches the diagnostic exit-2 path
    // instead of crashing.
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
// `set-scope full` makes the next test run unfiltered; it does not make a run HAPPEN. On a
// warm daemon whose impact DB says nothing changed, the scan provokes no run at all, so
// `confirm` read the LAST (filtered) run's coverage and refused — correctly, but with no
// way for the caller to ever produce a satisfying answer. `confirm` now RUNS the suite it
// demands, and only then judges it.
// ---------------------------------------------------------------------------

/// A `pollAndRender` drive whose test scope starts impact-filtered and becomes full-suite
/// only once `forceFullRun` has been invoked — i.e. a warm daemon with nothing to do,
/// reporting the last filtered run until something forces a new one.
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
    // Without the force, `confirm` reads the stale ImpactFiltered scope and returns exit 3
    // (UnearnedScope) with no way for the caller to get a different answer.
    let exitCode, forceCalls = driveConfirm CheckVerdict.Confirmation

    test <@ forceCalls = 1 @>
    test <@ exitCode = 0 @>

[<Fact(Timeout = 15000)>]
let ``a confirm that already has full-suite evidence does NOT run the suite twice`` () =
    // The force is a BACKSTOP, not the mechanism. On a cold daemon the scan already provoked
    // the unfiltered run (`set-scope full` was sent first), so the scope reads full-suite
    // here and `confirm` must pay for exactly ONE suite.
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
    // An impact-filtered green is precisely the answer the inner loop wants.
    let exitCode, forceCalls = driveConfirm CheckVerdict.InnerLoop

    test <@ forceCalls = 0 @>
    test <@ exitCode = 0 @>

/// The same drive, but the VERDICT FILE is read before the temp dir goes away.
///
/// AUTOMATION-259 lives or dies in the wiring: `publishVerdict` will happily classify a
/// `None` it was handed, so a transport that captured nothing at the escalation would
/// record `no-impact-scoped-run` on a confirm that plainly escalated — and every
/// producer-level test would still pass.
let private driveConfirmForVerdict (checkMode: CheckVerdict.CheckMode) (firstScope: TestScope) : Verdict.Verdict =
    let mutable forceCalls = 0

    let getTestRun () : TestRunReport =
        if forceCalls > 0 then
            { Scope = FullSuite 1; RunId = None }
        else
            { Scope = firstScope; RunId = None }

    TestHelpers.withTempDir "ipcoutput-confirm-259" (fun repoRoot ->
        pollAndRender
            ProgressRenderer.Agent
            checkMode
            repoRoot
            []
            (fun _ -> [])
            false
            (fun () -> "idle")
            (fun () -> "idle")
            (fun () -> "{}")
            (fun () -> """{"count":0,"files":{},"statuses":{},"unchecked":0}""")
            getTestRun
            (fun () -> forceCalls <- forceCalls + 1)
            (fun () -> "idle")
        |> ignore

        match Verdict.read repoRoot with
        | Verdict.Reading.Found v -> v
        | other -> failwithf "the drive must leave a readable verdict, got %A" other)

[<Fact(Timeout = 15000)>]
let ``an escalating confirm records the impact-scoped reading it escalated away from`` () =
    let v = driveConfirmForVerdict CheckVerdict.Confirmation (ImpactFiltered(0, 1))

    // Both runs were clean, so this is the ordinary sample the feature exists to collect.
    test <@ v.Divergence = Verdict.Divergence.Agreed @>

    match v.ImpactScopedRun with
    // The scope the daemon reported BEFORE the force — not the full suite the verdict
    // itself rests on.
    | Some pre -> test <@ pre.Scope = ImpactFiltered(0, 1) @>
    | None -> failwith "an escalating confirm must record the reading it escalated away from"

[<Fact(Timeout = 15000)>]
let ``a confirm that never escalated says so, and a check records no comparison at all`` () =
    // The controls for the test above, one on each side. Without them, a transport that
    // recorded a reading unconditionally, or one that recorded nothing ever, would still
    // satisfy it.
    let noEscalation = driveConfirmForVerdict CheckVerdict.Confirmation (FullSuite 1)

    test <@ noEscalation.Divergence = Verdict.Divergence.NoImpactScopedRun @>
    test <@ noEscalation.ImpactScopedRun = None @>

    // `check` never escalates, so it never has a comparison to make — confirm-only, and
    // `Verdict.create` refuses a check that claims otherwise.
    let inner = driveConfirmForVerdict CheckVerdict.InnerLoop (ImpactFiltered(0, 1))

    test <@ inner.Command = Verdict.Check @>
    test <@ inner.Divergence = Verdict.Divergence.NotRecorded @>

// ---------------------------------------------------------------------------
// The `coverage` token is what a CURRENT daemon sends. These cover the other side of the
// contract: a CLI talking to an OLDER daemon that omits it. The rule the fallback holds is
// that an ABSENT field must never read as "ran", or upgrading the CLI ahead of the daemon
// silently reintroduces green-for-nothing.
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
let ``renderIpcResult trusts a coverage token that ran, over an empty-looking payload`` () =
    // Positive control on the token path: without it, a fallback that ignored the token
    // entirely would still satisfy the two tests above.
    //
    // Both breadths, because the exit code must not depend on scope — a partial run that
    // executed and passed is still a pass (AUTOMATION-282).
    for token in [ "ran-full-suite"; "ran-partial" ] do
        let json =
            $"""{{"elapsed":"1.0s","coverage":"{token}","verifiedNothing":false,"projects":[{{"project":"P","status":"passed","output":"Passed! total: 3"}}]}}"""

        test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 0 @>

[<Fact(Timeout = 15000)>]
let ``the retired bare "ran" token is refused, not read as a pass`` () =
    // Pre-282 daemons sent `"ran"`: tests executed, breadth unstated. The missing half used
    // to arrive as a separate bool that could claim a full suite for a run that executed
    // nothing, so the token is refused rather than having a scope invented for it — a CLI
    // newer than its daemon gets one exit 3 and instructions to restart it.
    let json =
        """{"elapsed":"1.0s","coverage":"ran","verifiedNothing":false,"projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 3 @>

[<Fact(Timeout = 15000)>]
let ``an OLDER daemon sending no coverage field still cannot report a no-op as a pass`` () =
    // The upgrade-skew case: with no `coverage` and no `noTestsMatched`, the fallback must
    // reconstruct "no project ran" from the counts rather than defaulting to "ran".
    let noCoverageEmpty = """{"elapsed":"0.0s","projects":[]}"""

    let noCoverageZeroMatch =
        """{"elapsed":"0.1s","noTestsMatched":true,"projects":[{"project":"P","status":"no-tests-matched","output":""}]}"""

    let noCoverageReal =
        """{"elapsed":"1.0s","projects":[{"project":"P","status":"passed","output":"Passed! total: 9"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageEmpty = 3 @>
    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageZeroMatch = 3 @>
    // …and the fallback is still not a blanket refusal.
    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false noCoverageReal = 0 @>

// ---------------------------------------------------------------------------
// The token was string-COMPARED, and anything unrecognized fell through an `else` to
// `Tests passed`, exit 0 — so a newer daemon, a typo, or a future case such as
// "all-deferred" each reported a green, reachable by the mere absence of a match arm.
// These pin the CLI to `IpcParsing`'s rule: a reading you cannot interpret is never good.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``an UNRECOGNIZED coverage token is refused, not read as a pass`` () =
    // The version-skew direction the fallback does NOT cover: daemon newer than CLI.
    let json =
        """{"elapsed":"1.0s","coverage":"all-deferred","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    let result = renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json
    test <@ result = 3 @>

[<Fact(Timeout = 15000)>]
let ``a garbage coverage token is refused too — it is not a whitelist of known-bad values`` () =
    let json =
        """{"elapsed":"1.0s","coverage":"","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 3 @>

    let typo =
        """{"elapsed":"1.0s","coverage":"rann","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false typo = 3 @>

[<Fact(Timeout = 15000)>]
let ``refusing unknown tokens is NOT a blanket — a known ran token is still a pass`` () =
    // Positive control: without it, a change that refused every token would satisfy both
    // tests above while making every real green a failure.
    let json =
        """{"elapsed":"1.0s","coverage":"ran-full-suite","projects":[{"project":"P","status":"passed","output":"Passed! total: 3"}]}"""

    test <@ renderIpcResult ProgressRenderer.Verbose (fun _ -> []) false json = 0 @>
