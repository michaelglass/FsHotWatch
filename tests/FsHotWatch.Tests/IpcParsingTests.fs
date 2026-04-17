module FsHotWatch.Tests.IpcParsingTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Cli.IpcParsing

let private parseEl (json: string) =
    let doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

// --- parseTaggedStatus ---

[<Fact(Timeout = 5000)>]
let ``parseTaggedStatus parses idle`` () =
    let el = parseEl """{"tag":"idle"}"""
    test <@ parseTaggedStatus el = Some Idle @>

[<Fact(Timeout = 5000)>]
let ``parseTaggedStatus parses running`` () =
    let el = parseEl """{"tag":"running","since":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | Some(Running dt) ->
        let isExpected = dt.Year = 2026 && dt.Month = 4
        test <@ isExpected @>
    | other -> failwithf "expected Running, got %A" other

[<Fact(Timeout = 5000)>]
let ``parseTaggedStatus parses completed`` () =
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | Some(Completed _) -> ()
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 5000)>]
let ``parseTaggedStatus parses failed preserving multi-line error`` () =
    let err = "line one\nline two"

    let json =
        JsonSerializer.Serialize(
            {| tag = "failed"
               error = err
               at = "2026-04-05T12:00:00.0000000Z" |}
        )

    let el = parseEl json

    match parseTaggedStatus el with
    | Some(Failed(msg, _)) -> test <@ msg = err @>
    | other -> failwithf "expected Failed, got %A" other

[<Fact(Timeout = 5000)>]
let ``parseTaggedStatus returns None for unknown tag`` () =
    let el = parseEl """{"tag":"garbage"}"""
    test <@ parseTaggedStatus el = None @>

[<Fact(Timeout = 5000)>]
let ``parseTaggedStatus returns None for non-object`` () =
    let el = parseEl "\"idle\""
    test <@ parseTaggedStatus el = None @>

// --- parseStatusField accepts both shapes ---

[<Fact(Timeout = 5000)>]
let ``parseStatusField accepts tagged object`` () =
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseStatusField el with
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 5000)>]
let ``parseStatusField accepts legacy string`` () =
    let el = parseEl "\"Idle\""
    test <@ parseStatusField el = Idle @>

[<Fact(Timeout = 5000)>]
let ``parseStatusField falls back to Idle on malformed object`` () =
    let el = parseEl """{"tag":"unknown-future-tag"}"""
    test <@ parseStatusField el = Idle @>

// --- parseTaggedOutcome ---

[<Fact(Timeout = 5000)>]
let ``parseTaggedOutcome parses completed`` () =
    let el = parseEl """{"tag":"completed"}"""
    test <@ parseTaggedOutcome el = Some CompletedRun @>

[<Fact(Timeout = 5000)>]
let ``parseTaggedOutcome parses failed with error`` () =
    let el = parseEl """{"tag":"failed","error":"boom"}"""
    test <@ parseTaggedOutcome el = Some(FailedRun "boom") @>

[<Fact(Timeout = 5000)>]
let ``parseTaggedOutcome returns None for non-object`` () =
    let el = parseEl "\"Completed\""
    test <@ parseTaggedOutcome el = None @>

// --- parseOutcomeField: new and legacy ---

[<Fact(Timeout = 5000)>]
let ``parseOutcomeField new tagged failed`` () =
    let outcomeEl = parseEl """{"tag":"failed","error":"oops"}"""
    let result = parseOutcomeField outcomeEl ValueNone
    test <@ result = FailedRun "oops" @>

[<Fact(Timeout = 5000)>]
let ``parseOutcomeField legacy string failed uses separate error field`` () =
    let outcomeEl = parseEl "\"Failed\""
    let errorEl = parseEl "\"legacy error\""
    let result = parseOutcomeField outcomeEl (ValueSome errorEl)
    test <@ result = FailedRun "legacy error" @>

[<Fact(Timeout = 5000)>]
let ``parseOutcomeField legacy string completed ignores error`` () =
    let outcomeEl = parseEl "\"Completed\""
    let result = parseOutcomeField outcomeEl ValueNone
    test <@ result = CompletedRun @>

// --- parsePluginStatuses end-to-end with new wire format ---

[<Fact(Timeout = 5000)>]
let ``parsePluginStatuses parses tagged status objects`` () =
    let json =
        """{"build":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null},"lint":{"status":{"tag":"idle"},"subtasks":[],"activityTail":[],"lastRun":null}}"""

    let parsed = parsePluginStatuses json

    match parsed.["build"].Status with
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

    test <@ parsed.["lint"].Status = Idle @>

[<Fact(Timeout = 5000)>]
let ``parsePluginStatuses parses tagged lastRun outcome`` () =
    let json =
        """{"worker":{"status":{"tag":"failed","error":"e","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":{"startedAt":"2026-04-05T12:00:00.0000000Z","elapsedMs":42,"outcome":{"tag":"failed","error":"multi\nline"},"summary":null,"activityTail":[]}}}"""

    let parsed = parsePluginStatuses json
    let run = parsed.["worker"].LastRun.Value

    match run.Outcome with
    | FailedRun err -> test <@ err = "multi\nline" @>
    | other -> failwithf "expected FailedRun, got %A" other

[<Fact(Timeout = 5000)>]
let ``parsePluginStatuses backward-compat: legacy string status still parses`` () =
    let json =
        """{"build":{"status":"Completed at 2026-04-05T12:00:00.0000000Z","subtasks":[],"activityTail":[],"lastRun":null}}"""

    let parsed = parsePluginStatuses json

    match parsed.["build"].Status with
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 5000)>]
let ``parsePluginStatuses backward-compat: legacy lastRun outcome string still parses`` () =
    let json =
        """{"worker":{"status":{"tag":"idle"},"subtasks":[],"activityTail":[],"lastRun":{"startedAt":"2026-04-05T12:00:00.0000000Z","elapsedMs":1,"outcome":"Failed","summary":null,"activityTail":[],"error":"legacy"}}}"""

    let parsed = parsePluginStatuses json

    match parsed.["worker"].LastRun.Value.Outcome with
    | FailedRun err -> test <@ err = "legacy" @>
    | other -> failwithf "expected FailedRun, got %A" other

// --- parseStatus (legacy bare-string API) regression ---

[<Fact(Timeout = 5000)>]
let ``parseStatus parses Idle string`` () = test <@ parseStatus "Idle" = Idle @>

[<Fact(Timeout = 5000)>]
let ``parseStatus parses Running string`` () =
    match parseStatus "Running since 2026-04-05T12:00:00.0000000Z" with
    | Running _ -> ()
    | other -> failwithf "expected Running, got %A" other

[<Fact(Timeout = 5000)>]
let ``parseStatus parses Failed with error string`` () =
    match parseStatus "Failed at 2026-04-05T12:00:00.0000000Z: boom" with
    | Failed(msg, _) -> test <@ msg = "boom" @>
    | other -> failwithf "expected Failed, got %A" other

[<Fact(Timeout = 5000)>]
let ``parseStatus falls back to Idle on unknown garbage`` () =
    test <@ parseStatus "who knows" = Idle @>

// --- parseDiagnosticsResponse ---

[<Fact(Timeout = 5000)>]
let ``parseDiagnosticsResponse handles tagged status field`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":{"status":{"tag":"idle"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let resp = parseDiagnosticsResponse json
    test <@ resp.Statuses.["build"].Status = Idle @>

// --- isAllTerminal ---

[<Fact(Timeout = 5000)>]
let ``isAllTerminal false when any running`` () =
    let m = Map.ofList [ "a", Completed DateTime.UtcNow; "b", Running DateTime.UtcNow ]

    test <@ not (isAllTerminal m) @>

[<Fact(Timeout = 5000)>]
let ``isAllTerminal true when mix of Idle, Completed, Failed`` () =
    let m =
        Map.ofList [ "a", Completed DateTime.UtcNow; "b", Failed("x", DateTime.UtcNow); "c", Idle ]

    test <@ isAllTerminal m @>

[<Fact(Timeout = 5000)>]
let ``isAllTerminal false on empty map`` () =
    test <@ not (isAllTerminal Map.empty) @>
