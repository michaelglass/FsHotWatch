module FsHotWatch.Tests.IpcOutputTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcOutput

[<Fact(Timeout = 30000)>]
let ``parseDiagnosticsResponse extracts count`` () =
    let json = """{"count":2,"files":{},"statuses":{}}"""
    let result = parseDiagnosticsResponse json
    test <@ result.Count = 2 @>

[<Fact(Timeout = 30000)>]
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

[<Fact(Timeout = 30000)>]
let ``parseDiagnosticsResponse extracts statuses`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":{"status":"Completed at 2026-04-05T12:00:00.0000000Z","subtasks":[],"activityTail":[],"lastRun":null},"lint":{"status":"Idle","subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let result = parseDiagnosticsResponse json
    test <@ result.Statuses.ContainsKey("build") @>

    match result.Statuses["build"].Status with
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 30000)>]
let ``parseStatusMap parses completed status`` () =
    let statuses = Map.ofList [ "build", "Completed at 2026-04-05T12:00:00.0000000Z" ]
    let parsed = parseStatusMap statuses
    test <@ parsed.ContainsKey("build") @>

    match parsed["build"] with
    | Completed _ -> ()
    | other -> failwith $"Expected Completed, got %A{other}"

[<Fact(Timeout = 30000)>]
let ``parseStatusMap parses running status`` () =
    let statuses = Map.ofList [ "lint", "Running since 2026-04-05T12:00:00.0000000Z" ]
    let parsed = parseStatusMap statuses
    test <@ parsed.ContainsKey("lint") @>

    match parsed["lint"] with
    | Running _ -> ()
    | other -> failwith $"Expected Running, got %A{other}"

[<Fact(Timeout = 30000)>]
let ``parseStatusMap parses failed status`` () =
    let statuses =
        Map.ofList [ "build", "Failed at 2026-04-05T12:00:00.0000000Z: compile error" ]

    let parsed = parseStatusMap statuses

    match parsed["build"] with
    | Failed(msg, _) -> test <@ msg = "compile error" @>
    | other -> failwith $"Expected Failed, got %A{other}"

[<Fact(Timeout = 30000)>]
let ``parseStatusMap parses idle status`` () =
    let statuses = Map.ofList [ "format", "Idle" ]
    let parsed = parseStatusMap statuses

    match parsed["format"] with
    | Idle -> ()
    | other -> failwith $"Expected Idle, got %A{other}"

[<Fact(Timeout = 30000)>]
let ``formatDiagnosticsResponse with no errors shows clean message`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":"Completed at 2026-04-05T12:00:00.0000000Z"}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse result
    test <@ output.Contains("No errors") @>

[<Fact(Timeout = 30000)>]
let ``formatDiagnosticsResponse with errors shows file and message`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad name","severity":"warning","line":17,"column":0,"detail":null}]},"statuses":{"lint":"Completed at 2026-04-05T12:00:00.0000000Z"}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse result
    test <@ output.Contains("src/Foo.fs") @>
    test <@ output.Contains("[lint]") @>
    test <@ output.Contains("L17") @>
    test <@ output.Contains("bad name") @>

[<Fact(Timeout = 30000)>]
let ``formatDiagnosticsResponse with errors shows count summary`` () =
    let json =
        """{"count":2,"files":{"src/A.fs":[{"plugin":"lint","message":"x","severity":"warning","line":1,"column":0,"detail":null}],"src/B.fs":[{"plugin":"build","message":"y","severity":"error","line":2,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse result
    test <@ output.Contains("1 error(s), 1 warning(s) in 2 file(s)") @>

[<Fact(Timeout = 30000)>]
let ``formatStepResult shows checkmark for completed`` () =
    let line = formatStepResult "build" (Completed(System.DateTime.UtcNow))
    test <@ line.Contains("\u2713") @>
    test <@ line.Contains("build") @>

[<Fact(Timeout = 30000)>]
let ``formatStepResult shows X for failed`` () =
    let line =
        formatStepResult "build" (Failed("compile error", System.DateTime.UtcNow))

    test <@ line.Contains("\u2717") @>
    test <@ line.Contains("compile error") @>

[<Fact(Timeout = 30000)>]
let ``isAllTerminal returns false for empty map`` () =
    test <@ not (isAllTerminal Map.empty) @>

[<Fact(Timeout = 30000)>]
let ``isAllTerminal returns true when all completed or failed`` () =
    let statuses =
        Map.ofList
            [ "build", Completed System.DateTime.UtcNow
              "lint", Failed("x", System.DateTime.UtcNow) ]

    test <@ isAllTerminal statuses @>

[<Fact(Timeout = 30000)>]
let ``isAllTerminal returns true when some plugins are idle`` () =
    let statuses =
        Map.ofList [ "build", Completed System.DateTime.UtcNow; "file-cmd", Idle ]

    test <@ isAllTerminal statuses @>

[<Fact(Timeout = 30000)>]
let ``isAllTerminal returns true when all plugins are idle`` () =
    let statuses = Map.ofList [ "file-cmd", Idle ]
    test <@ isAllTerminal statuses @>

[<Fact(Timeout = 30000)>]
let ``isAllTerminal returns false when any running`` () =
    let statuses =
        Map.ofList
            [ "build", Completed System.DateTime.UtcNow
              "lint", Running System.DateTime.UtcNow ]

    test <@ not (isAllTerminal statuses) @>

[<Fact(Timeout = 30000)>]
let ``exitCodeFromResponse returns 0 for count 0`` () =
    let resp =
        { Count = 0
          Files = Map.empty
          Statuses = Map.empty }

    test <@ exitCodeFromResponse false resp = 0 @>

[<Fact(Timeout = 30000)>]
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
          Statuses = Map.empty }

    test <@ exitCodeFromResponse false resp = 1 @>

[<Fact(Timeout = 30000)>]
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
          Statuses = Map.empty }

    test <@ exitCodeFromResponse true resp = 0 @>

[<Fact(Timeout = 30000)>]
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
          Statuses = Map.empty }

    test <@ exitCodeFromResponse false resp = 1 @>

[<Fact(Timeout = 30000)>]
let ``renderProgress shows all plugins`` () =
    let statuses =
        Map.ofList
            [ "build", Completed System.DateTime.UtcNow
              "lint", Running(System.DateTime.UtcNow.AddSeconds(-3.0)) ]

    let output = renderProgress statuses
    test <@ output.Contains("build") @>
    test <@ output.Contains("lint") @>
    test <@ output.Contains("\u2713") @>
    test <@ output.Contains("\u2026") @>

// --- renderIpcResult tests ---

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with GetDiagnostics format count 0 returns 0`` () =
    let result =
        renderIpcResult (fun _ -> []) false """{"count":0,"files":{},"statuses":{}}"""

    test <@ result = 0 @>

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with GetDiagnostics format count > 0 returns 1`` () =
    let result =
        renderIpcResult
            (fun _ -> [])
            false
            """{"count":1,"files":{"src/Foo.fs":[{"plugin":"lint","message":"bad","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    test <@ result = 1 @>

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with status passed returns 0`` () =
    let result = renderIpcResult (fun _ -> []) false """{"status":"passed"}"""
    test <@ result = 0 @>

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with status failed returns 1`` () =
    let result = renderIpcResult (fun _ -> []) false """{"status":"failed"}"""
    test <@ result = 1 @>

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with error field returns 1`` () =
    let result =
        renderIpcResult (fun _ -> []) false """{"error":"something went wrong"}"""

    test <@ result = 1 @>

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with plain text returns 0`` () =
    let result = renderIpcResult (fun _ -> []) false "build completed successfully"
    test <@ result = 0 @>

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with test results JSON containing arrays does not crash`` () =
    let json =
        """{"elapsed":"1.5s","projects":[{"project":"TestProject","status":"passed","output":"ok"}]}"""

    let result = renderIpcResult (fun _ -> []) false json
    test <@ result = 0 @>

[<Fact(Timeout = 30000)>]
let ``renderIpcResult with test results JSON with failed project returns 1`` () =
    let json =
        """{"elapsed":"2.0s","projects":[{"project":"FailProject","status":"failed","output":"FAIL: test1"}]}"""

    let result = renderIpcResult (fun _ -> []) false json
    test <@ result = 1 @>

[<Fact(Timeout = 30000)>]
let ``formatDiagnosticsResponse hides info-severity entries`` () =
    let json =
        """{"count":1,"files":{"src/Foo.fs":[{"plugin":"fcs","message":"XML comment is not placed on a valid language element.","severity":"info","line":3,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse result
    test <@ not (output.Contains("XML comment")) @>
    test <@ output.Contains("No errors") @>

[<Fact(Timeout = 30000)>]
let ``formatDiagnosticsResponse shows warnings but hides info in same file`` () =
    let json =
        """{"count":2,"files":{"src/Foo.fs":[{"plugin":"fcs","message":"XML comment","severity":"info","line":3,"column":0,"detail":null},{"plugin":"format-check","message":"File is not formatted","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse result
    test <@ output.Contains("File is not formatted") @>
    test <@ not (output.Contains("XML comment")) @>
    test <@ output.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 30000)>]
let ``formatDiagnosticsResponse excludes info-only files from count`` () =
    let json =
        """{"count":2,"files":{"src/A.fs":[{"plugin":"fcs","message":"XML comment","severity":"info","line":3,"column":0,"detail":null}],"src/B.fs":[{"plugin":"lint","message":"bad","severity":"warning","line":1,"column":0,"detail":null}]},"statuses":{}}"""

    let result = parseDiagnosticsResponse json
    let output = formatDiagnosticsResponse result
    test <@ output.Contains("1 warning(s) in 1 file(s)") @>

[<Fact(Timeout = 30000)>]
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
          Statuses = Map.empty }

    test <@ exitCodeFromResponse false resp = 0 @>

// --- Regression: parsePluginStatuses format drift ---
//
// pollAndRender's isAllTerminal returns false on an empty statuses map, which
// loops forever at 200ms intervals. If parsePluginStatuses rejects the GetStatus
// JSON shape (e.g. fakeIpc returning `{"plugin": "Completed at ..."}` with a
// bare-string value instead of the real `{"plugin": {"status": "..."}}` object
// shape), the parse yields an empty map and pollAndRender hangs. This hung the
// full test suite and `mise run check` for 40+ minutes before being caught.

[<Fact(Timeout = 30000)>]
let ``parsePluginStatuses rejects bare-string values and returns empty`` () =
    // The old broken fakeIpc shape — documents why that shape must never appear
    // in test fixtures: empty parse -> isAllTerminal false -> pollAndRender hang.
    let json = """{"plugin": "Completed at 2026-01-01T00:00:00Z"}"""
    let parsed = parsePluginStatuses json
    test <@ Map.isEmpty parsed @>
    test <@ not (isAllTerminal (statusOnly parsed)) @>

[<Fact(Timeout = 30000)>]
let ``parsePluginStatuses accepts object-valued entries with status field`` () =
    // The real GetStatus JSON shape. Object-per-plugin with a status string.
    let json =
        """{"plugin": {"status": "Completed at 2026-01-01T00:00:00Z", "subtasks": [], "activityTail": [], "lastRun": null}}"""

    let parsed = parsePluginStatuses json
    test <@ Map.containsKey "plugin" parsed @>
    test <@ isAllTerminal (statusOnly parsed) @>
