module FsHotWatch.Tests.IpcParsingTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Tests.TestHelpers

let private parseEl (json: string) =
    let doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

// --- parseTaggedStatus ---

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses idle`` () =
    let el = parseEl """{"tag":"idle"}"""
    test <@ parseTaggedStatus el = Some Idle @>

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses running`` () =
    let el = parseEl """{"tag":"running","since":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | Some(Running dt) ->
        let isExpected = dt.Year = 2026 && dt.Month = 4
        test <@ isExpected @>
    | other -> failwithf "expected Running, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses completed`` () =
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | Some(Completed _) -> ()
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus returns None for unknown tag`` () =
    let el = parseEl """{"tag":"garbage"}"""
    test <@ parseTaggedStatus el = None @>

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus returns None for non-object`` () =
    let el = parseEl "\"idle\""
    test <@ parseTaggedStatus el = None @>

// --- parseStatusField ---

[<Fact(Timeout = 15000)>]
let ``parseStatusField accepts tagged object`` () =
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseStatusField el with
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseStatusField falls back to Idle on malformed object`` () =
    let el = parseEl """{"tag":"unknown-future-tag"}"""
    test <@ parseStatusField el = Idle @>

// --- parseTaggedOutcome ---

[<Fact(Timeout = 15000)>]
let ``parseTaggedOutcome parses completed`` () =
    let el = parseEl """{"tag":"completed"}"""
    test <@ parseTaggedOutcome el = Some CompletedRun @>

[<Fact(Timeout = 15000)>]
let ``parseTaggedOutcome parses failed with error`` () =
    let el = parseEl """{"tag":"failed","error":"boom"}"""
    test <@ parseTaggedOutcome el = Some(FailedRun "boom") @>

[<Fact(Timeout = 15000)>]
let ``parseTaggedOutcome returns None for non-object`` () =
    let el = parseEl "\"Completed\""
    test <@ parseTaggedOutcome el = None @>

// --- parseOutcomeField ---

[<Fact(Timeout = 15000)>]
let ``parseOutcomeField tagged failed`` () =
    let outcomeEl = parseEl """{"tag":"failed","error":"oops"}"""
    test <@ parseOutcomeField outcomeEl = FailedRun "oops" @>

[<Fact(Timeout = 15000)>]
let ``parseOutcomeField tagged completed`` () =
    let outcomeEl = parseEl """{"tag":"completed"}"""
    test <@ parseOutcomeField outcomeEl = CompletedRun @>

// --- parsePluginStatuses end-to-end with new wire format ---

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses returns empty map on malformed JSON (F8)`` () =
    // F8: invalid JSON should produce Map.empty rather than crash the CLI,
    // but only :? JsonException should be caught — anything else (a real
    // programming bug) must surface.
    let parsed = parsePluginStatuses "{ not json"
    test <@ parsed = Map.empty @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses propagates non-JSON exceptions (F8)`` () =
    // F8: passing null should surface as a real exception (ArgumentNullException
    // from JsonDocument.Parse), not silently degrade to Map.empty.
    let mutable thrown = false

    try
        parsePluginStatuses null |> ignore
    with
    | :? System.ArgumentNullException -> thrown <- true
    | :? System.NullReferenceException -> thrown <- true

    test <@ thrown @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses parses tagged status objects`` () =
    let json =
        """{"build":{"status":{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":null},"lint":{"status":{"tag":"idle"},"subtasks":[],"activityTail":[],"lastRun":null}}"""

    let parsed = parsePluginStatuses json

    match parsed.["build"].Status with
    | Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

    test <@ parsed.["lint"].Status = Idle @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses parses tagged lastRun outcome`` () =
    let json =
        """{"worker":{"status":{"tag":"failed","error":"e","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":{"startedAt":"2026-04-05T12:00:00.0000000Z","elapsedMs":42,"outcome":{"tag":"failed","error":"multi\nline"},"summary":null,"activityTail":[]}}}"""

    let parsed = parsePluginStatuses json
    let run = parsed.["worker"].LastRun.Value

    match run.Outcome with
    | FailedRun err -> test <@ err = "multi\nline" @>
    | other -> failwithf "expected FailedRun, got %A" other

// --- parseDiagnosticsResponse ---

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse handles tagged status field`` () =
    let json =
        """{"count":0,"files":{},"statuses":{"build":{"status":{"tag":"idle"},"subtasks":[],"activityTail":[],"lastRun":null}}}"""

    let resp = parseDiagnosticsResponse json
    test <@ resp.Statuses.["build"].Status = Idle @>

// --- isAllTerminal ---

[<Fact(Timeout = 15000)>]
let ``isAllTerminal false when any running`` () =
    let m =
        Map.ofList [ "a", completedAt DateTime.UtcNow; "b", Running DateTime.UtcNow ]

    test <@ not (isAllTerminal m) @>

[<Fact(Timeout = 15000)>]
let ``isAllTerminal true when mix of Idle, Completed, Failed`` () =
    let m =
        Map.ofList
            [ "a", completedAt DateTime.UtcNow
              "b", Failed("x", DateTime.UtcNow)
              "c", Idle ]

    test <@ isAllTerminal m @>

[<Fact(Timeout = 15000)>]
let ``isAllTerminal false on empty map`` () =
    test <@ not (isAllTerminal Map.empty) @>

// --- Coverage parsing (unchecked field on the diagnostics response) ---

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse unchecked=0 -> Complete`` () =
    let json = """{"count":0,"files":{},"statuses":{},"unchecked":0}"""
    let resp = parseDiagnosticsResponse json
    test <@ resp.Coverage = Complete @>

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse unchecked=3 -> Incomplete 3`` () =
    let json = """{"count":0,"files":{},"statuses":{},"unchecked":3}"""
    let resp = parseDiagnosticsResponse json
    test <@ resp.Coverage = Incomplete 3 @>

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse missing unchecked field -> Unknown`` () =
    // Cross-version backstop: an old daemon that doesn't send `unchecked` must
    // never read as Complete.
    let json = """{"count":0,"files":{},"statuses":{}}"""
    let resp = parseDiagnosticsResponse json
    test <@ resp.Coverage = Unknown @>

[<Fact(Timeout = 15000)>]
let ``parseDiagnosticsResponse garbage unchecked field -> Unknown`` () =
    let json = """{"count":0,"files":{},"statuses":{},"unchecked":"banana"}"""
    let resp = parseDiagnosticsResponse json
    test <@ resp.Coverage = Unknown @>

// --- parseTaggedStatus: RunVerdict on completed (AUTOMATION-99) -------------

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses completed with its verdict`` () =
    let el =
        parseEl
            """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z","summary":"6 passed, 0 failed","elapsedMs":12500.0}"""

    match parseTaggedStatus el with
    | Some(Completed(at, v)) ->
        let year = at.Year
        test <@ year = 2026 @>
        test <@ v.Summary = "6 passed, 0 failed" @>
        test <@ v.Elapsed = System.TimeSpan.FromSeconds 12.5 @>
    | other -> failwithf "expected Completed with verdict, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus tolerates a completed payload from an older daemon (no verdict fields)`` () =
    // Deserialization edge: the verdict fields are absent — the status parse
    // must still succeed, degrading to an empty verdict, never failing the
    // whole status read.
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | Some(Completed(_, v)) ->
        test <@ v.Summary = "" @>
        test <@ v.Elapsed = System.TimeSpan.Zero @>
    | other -> failwithf "expected Completed with empty verdict, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus ignores a non-numeric elapsedMs`` () =
    let el =
        parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z","summary":"ok","elapsedMs":"soon"}"""

    match parseTaggedStatus el with
    | Some(Completed(_, v)) ->
        test <@ v.Summary = "ok" @>
        test <@ v.Elapsed = System.TimeSpan.Zero @>
    | other -> failwithf "expected Completed, got %A" other

// --- TestScope.describe / parseTaggedOutcome: totality pins -----------------

[<Fact(Timeout = 10000)>]
let ``TestScope.describe names every scope`` () =
    test <@ (TestScope.describe (FullSuite 6)).Contains "full suite" @>
    test <@ (TestScope.describe (ImpactFiltered(2, 6))).Contains "impact-filtered" @>
    test <@ TestScope.describe NoTestsRun = "no tests ran" @>
    test <@ (TestScope.describe ScopeUnknown).Contains "unknown" @>

[<Fact(Timeout = 10000)>]
let ``parseTaggedOutcome parses timedOut with its reason`` () =
    let el = parseEl """{"tag":"timedOut","reason":"620s"}"""
    test <@ parseTaggedOutcome el = Some(TimedOut "620s") @>

[<Fact(Timeout = 10000)>]
let ``parseTaggedOutcome parses failed with its error`` () =
    let el = parseEl """{"tag":"failed","error":"3 failed"}"""
    test <@ parseTaggedOutcome el = Some(FailedRun "3 failed") @>
