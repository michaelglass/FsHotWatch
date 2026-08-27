module FsHotWatch.Tests.IpcParsingTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Tests.TestHelpers

let private parseEl (json: string) =
    let doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

// --- parseTaggedStatus ---

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses idle`` () =
    let el = parseEl """{"tag":"idle"}"""
    test <@ parseTaggedStatus el = StatusView.Idle @>

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses running`` () =
    let el = parseEl """{"tag":"running","since":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | StatusView.Running dt ->
        let isExpected = dt.Year = 2026 && dt.Month = 4
        test <@ isExpected @>
    | other -> failwithf "expected Running, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses completed`` () =
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | StatusView.Completed _ -> ()
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
    | StatusView.Failed(msg, _) -> test <@ msg = err @>
    | other -> failwithf "expected Failed, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus is UNREADABLE for an unknown tag — never idle`` () =
    let el = parseEl """{"tag":"garbage"}"""

    match parseTaggedStatus el with
    | StatusView.Unreadable _ -> ()
    | other -> failwithf "expected Unreadable, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus is UNREADABLE for a non-object`` () =
    let el = parseEl "\"idle\""

    match parseTaggedStatus el with
    | StatusView.Unreadable _ -> ()
    | other -> failwithf "expected Unreadable, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus is UNREADABLE for a running status whose since will not parse`` () =
    // `since` is the ONLY input to the wedge classifier (AUTOMATION-147). Rounding it
    // to `Idle` does not lose a timestamp, it defeats wedge detection outright and
    // silently: an 8h36m hang reads back as a quiescent plugin.
    let el = parseEl """{"tag":"running","since":"whenever"}"""

    match parseTaggedStatus el with
    | StatusView.Unreadable _ -> ()
    | other -> failwithf "expected Unreadable, got %A" other

// --- parseStatusField ---

[<Fact(Timeout = 15000)>]
let ``parseStatusField accepts tagged object`` () =
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseStatusField el with
    | StatusView.Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseStatusField is UNREADABLE on a malformed object — it does NOT fall back to Idle`` () =
    // This asserted `= StatusView.Idle` and passed for its whole life: a green test
    // enshrining the fail-open is how one survives. Do not put it back.
    let el = parseEl """{"tag":"unknown-future-tag"}"""

    match parseStatusField el with
    | StatusView.Unreadable _ -> ()
    | other -> failwithf "expected Unreadable, got %A" other

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

[<Fact(Timeout = 15000)>]
let ``parseOutcomeField: an outcome tag this build does not recognize is a FAILED run, not a pass`` () =
    // An unknown run outcome used to default to `CompletedRun` — a PASS. `Verdict.fs`
    // states the opposite policy: "An unknown state is not a passing state." A newer
    // daemon reporting an outcome this CLI cannot name is never a clean run.
    let outcomeEl = parseEl """{"tag":"transmuted"}"""

    match parseOutcomeField outcomeEl with
    | FailedRun _ -> ()
    | other -> failwithf "expected FailedRun, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseOutcomeField: an outcome that is not even an object is a FAILED run`` () =
    match parseOutcomeField (parseEl "\"completed\"") with
    | FailedRun _ -> ()
    | other -> failwithf "expected FailedRun, got %A" other

// --- parsePluginStatuses end-to-end with new wire format ---

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses FAILS CLOSED on malformed JSON — an unreadable map is not an empty one`` () =
    // Returning `Map.empty` vanished every plugin — nothing Failed, nothing Running,
    // and a verdict over the resulting clean ledger stayed green. `Map.empty` is a
    // claim ("no plugins have anything to say"); a parse failure is the absence of
    // one. As in `Verdict.read`, unreadable is a NAMED case, never rounded to fine.
    match parsePluginStatuses "{ not json" with
    | Result.Error _ -> ()
    | Result.Ok m -> failwithf "expected Error, got a %d-entry map" (Map.count m)

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses propagates non-JSON exceptions (F8)`` () =
    // Null must surface as a real exception from JsonDocument.Parse, not degrade
    // into a readable-looking answer.
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

    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json

    match parsed.["build"].Status with
    | StatusView.Completed _ -> ()
    | other -> failwithf "expected Completed, got %A" other

    test <@ parsed.["lint"].Status = StatusView.Idle @>

[<Fact(Timeout = 15000)>]
let ``parsePluginStatuses parses tagged lastRun outcome`` () =
    let json =
        """{"worker":{"status":{"tag":"failed","error":"e","at":"2026-04-05T12:00:00.0000000Z"},"subtasks":[],"activityTail":[],"lastRun":{"startedAt":"2026-04-05T12:00:00.0000000Z","elapsedMs":42,"outcome":{"tag":"failed","error":"multi\nline"},"summary":null,"activityTail":[]}}}"""

    let parsed = FsHotWatch.Tests.TestHelpers.parseStatuses json
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
    test <@ resp.Statuses.["build"].Status = StatusView.Idle @>

// --- isAllTerminal ---

[<Fact(Timeout = 15000)>]
let ``isAllTerminal false when any running`` () =
    let m =
        Map.ofList
            [ "a", StatusView.Completed DateTime.UtcNow
              "b", StatusView.Running DateTime.UtcNow ]

    test <@ not (isAllTerminal m) @>

[<Fact(Timeout = 15000)>]
let ``isAllTerminal true when mix of Idle, Completed, Failed`` () =
    let m =
        Map.ofList
            [ "a", StatusView.Completed DateTime.UtcNow
              "b", StatusView.Failed("x", DateTime.UtcNow)
              "c", StatusView.Idle ]

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

// --- parseTaggedStatus: the wire carries NO verdict (AUTOMATION-99) ---------
//
// The verdict (summary + elapsed) travels exclusively in `lastRun`; the tagged
// status is state + timestamps only. The parse stays TOTAL over recognized tags,
// extra fields included, so a plugin can never drop out of the status map — which
// would make `isAllTerminal` read true for a plugin it can no longer see.

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus parses completed carrying only its timestamp`` () =
    let el = parseEl """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z"}"""

    match parseTaggedStatus el with
    | StatusView.Completed at ->
        let year = at.Year
        test <@ year = 2026 @>
    | other -> failwithf "expected Completed, got %A" other

[<Fact(Timeout = 15000)>]
let ``parseTaggedStatus ignores legacy verdict fields on a completed payload`` () =
    // A daemon still sending summary/elapsedMs parses identically: those fields are
    // simply not part of the status.
    let el =
        parseEl
            """{"tag":"completed","at":"2026-04-05T12:00:00.0000000Z","summary":"6 passed, 0 failed","elapsedMs":12500.0}"""

    match parseTaggedStatus el with
    | StatusView.Completed at ->
        let year = at.Year
        test <@ year = 2026 @>
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

// ---------------------------------------------------------------------------
// AUTOMATION-259 — `check-reach`, the projection's wire.
//
// The reply decides whether a `confirm` records a comparison or records that it could
// not make one. So every way of not understanding it has to land on a value that is not
// a reach, and the parser is where that is enforced — not at the reader, which by then
// has a shape it believes.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 10000)>]
let ``parseCheckReach reads a recorded projection, run id and scope included`` () =
    let reading =
        parseCheckReach
            """{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e","scope":"filtered",
                "ranProjects":2,"totalProjects":6,"reach":"reached-a-failure",
                "failingSuites":["Lib.Tests"],"reason":null,
                "conditionalFailureRecall":{"measured":true,"reached":3,"total":4,"threshold":1.0,
                           "acceptable":false,"reason":null}}"""

    match reading with
    | ReachRecorded r ->
        test <@ r.RunId = Some(Guid.Parse "5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e") @>
        test <@ r.Scope = ImpactFiltered(2, 6) @>
        test <@ r.Reach = ReachedAFailure [ "Lib.Tests" ] @>
        test <@ r.Recall = FailureRecallMeasured(3, 4, 1.0, false) @>
    | other -> failwithf "a well-formed reply must parse, got %A" other

[<Fact(Timeout = 10000)>]
let ``parseCheckReach reads every reach token, and a full-suite selection`` () =
    let replyWith (reach: string) =
        parseCheckReach
            $"""{{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e","scope":"full",
                 "ranProjects":6,"totalProjects":6,"reach":"%s{reach}","failingSuites":[],
                 "reason":"a project-level red"}}"""

    let reachOf (reading: CheckReachReading) =
        match reading with
        | ReachRecorded r -> r.Reach
        | other -> failwithf "expected a recorded projection, got %A" other

    test <@ reachOf (replyWith "reached-no-failure") = ReachedNoFailure @>
    test <@ reachOf (replyWith "no-failures-to-reach") = NoFailuresToReach @>
    test <@ reachOf (replyWith "unknown") = ReachUnknown "a project-level red" @>

    match replyWith "no-failures-to-reach" with
    | ReachRecorded r -> test <@ r.Scope = FullSuite 6 @>
    | other -> failwithf "expected a recorded projection, got %A" other

[<Fact(Timeout = 10000)>]
let ``every unreadable check-reach reply refuses, and none of them reads as a reach`` () =
    // The one property. A reply this build cannot make sense of must never produce a
    // reach — the reach is what turns into `Agreed`.
    let refusals =
        [ "not JSON at all", "}{"
          "a reply that records nothing", """{"recorded":false,"reason":"no run has completed"}"""
          "a reply with no `recorded` flag", """{"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e"}""" ]

    for label, json in refusals do
        match parseCheckReach json with
        | ReachUnavailable _ -> ()
        | other -> failwithf "%s must be UNAVAILABLE, got %A" label other

    let undecidable =
        [ "a reach token from another version",
          """{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e","reach":"probably-fine"}"""
          "a reply that names no reach at all", """{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e"}"""
          // A classification with no evidence for it. The two travel together or not at
          // all: a reached failure that names no suite cannot be recorded as one.
          "a reached failure that names no suite",
          """{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e","reach":"reached-a-failure","failingSuites":[]}""" ]

    for label, json in undecidable do
        match parseCheckReach json with
        | ReachRecorded r ->
            match r.Reach with
            | ReachUnknown _ -> ()
            | other -> failwithf "%s must be UNKNOWN, got %A" label other
        | other -> failwithf "%s must still parse to a record, got %A" label other

    // CONTROL. Without it, a parser that had started refusing every reply would satisfy
    // all six cases above.
    match
        parseCheckReach
            """{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e","reach":"no-failures-to-reach"}"""
    with
    | ReachRecorded r -> test <@ r.Reach = NoFailuresToReach @>
    | other -> failwithf "a well-formed reply must still parse, got %A" other

[<Fact(Timeout = 10000)>]
let ``a reply that names no scope for the selection reads as UNREADABLE, never full-suite`` () =
    match
        parseCheckReach
            """{"recorded":true,"runId":"5f2b7c9d4e1a4f3b8c6d0e2a1b3c4d5e","reach":"no-failures-to-reach"}"""
    with
    | ReachRecorded r -> test <@ TestScope.isUnreadable r.Scope @>
    | other -> failwithf "expected a recorded projection, got %A" other
