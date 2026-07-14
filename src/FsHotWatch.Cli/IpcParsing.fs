module FsHotWatch.Cli.IpcParsing

open System
open System.Globalization
open System.Text.Json
open FsHotWatch.Events
open FsHotWatch.ErrorLedger
open FsHotWatch.Cli.RunOnceOutput

/// A single diagnostic entry parsed from IPC JSON.
type DiagnosticEntry =
    { Plugin: string
      Message: string
      Severity: DiagnosticSeverity
      Line: int
      Column: int
      Detail: string option }

type ParsedPluginStatus = RunOnceOutput.ParsedPluginStatus

/// Whether the daemon has fully checked every file it's responsible for, as of
/// the moment the diagnostics response was produced.
///
/// `Unknown` is the cross-version / parse-gap backstop: a daemon that doesn't
/// send the `unchecked` field (old build), or a response whose field can't be
/// parsed, yields `Unknown` — which MUST NOT be treated as `Complete`. This is
/// what makes a false 0 exit code unrepresentable.
type Coverage =
    /// Every registered file holds a valid full-check result.
    | Complete
    /// `unchecked` registered files lack a valid full-check result.
    | Incomplete of unchecked: int
    /// Coverage data was absent or unparseable. Never treated as complete.
    | Unknown

/// Parsed GetDiagnostics response. `Coverage` is a REQUIRED field of the parsed
/// shape (not optional) so a verdict can never be computed without it.
type DiagnosticsResponse =
    { Count: int
      Files: Map<string, DiagnosticEntry list>
      Statuses: Map<string, ParsedPluginStatus>
      Coverage: Coverage }

/// What the last completed test run actually COVERED (AUTOMATION-112).
///
/// Impact filtering is a latency optimization for the inner dev loop. A merge gate is
/// a correctness claim. An impact-filtered green means "your change didn't break
/// anything I chose to look at" — NOT "the suite is green". Those are different claims
/// and reading one as the other is how 35 tests sat red on `main` for an unknown period
/// while the gate stayed green: they were never selected, so nothing ever ran them.
///
/// So the scope is a VALUE the verdict is a total function over, not an assumption a
/// caller is trusted to make. `ScopeUnknown` is the cross-version / parse-gap backstop
/// — a daemon too old to answer, an absent test-prune plugin, an unparseable reply —
/// and it must NEVER read as full-suite, exactly as `Coverage.Unknown` must never read
/// as `Complete`.
type TestScope =
    /// Every configured test project ran, none of them impact-filtered.
    | FullSuite of projects: int
    /// A subset ran: some project was filtered to selected classes, or did not run.
    | ImpactFiltered of ranProjects: int * totalProjects: int
    /// No test run has completed, or the run executed no tests at all.
    | NoTestsRun
    /// The scope could not be determined. Never treated as full-suite.
    | ScopeUnknown

module TestScope =
    let describe (scope: TestScope) : string =
        match scope with
        | FullSuite n -> $"full suite (%d{n}/%d{n} projects, unfiltered)"
        | ImpactFiltered(ran, total) -> $"impact-filtered (%d{ran}/%d{total} projects)"
        | NoTestsRun -> "no tests ran"
        | ScopeUnknown -> "unknown (the daemon did not report a test scope)"

let private tryParseUtcOpt (s: string) : DateTime option =
    match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) with
    | true, dt -> Some dt
    | false, _ -> None

let private tryParseUtcOr (fallback: DateTime) (s: string) : DateTime =
    match tryParseUtcOpt s with
    | Some dt -> dt
    | None -> fallback

let private tryGetStringProp (el: JsonElement) (name: string) : string option =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// Parse a tagged status object, e.g. {"tag":"running","since":"..."}, into
/// the CLI-side `StatusView`. Returns None if the element isn't a
/// recognizable tagged status. Total over recognized tags — a missing verdict
/// can never drop a plugin from the map (which would make `isAllTerminal`
/// read true for a plugin it can no longer see); the verdict simply isn't
/// carried here at all (it travels in `lastRun`, the one channel).
let parseTaggedStatus (el: JsonElement) : StatusView option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        match tryGetStringProp el "tag" with
        | Some "idle" -> Some StatusView.Idle
        | Some "running" ->
            tryGetStringProp el "since"
            |> Option.bind tryParseUtcOpt
            |> Option.map StatusView.Running
        | Some "completed" ->
            tryGetStringProp el "at"
            |> Option.bind tryParseUtcOpt
            |> Option.map StatusView.Completed
        | Some "failed" ->
            let err = tryGetStringProp el "error" |> Option.defaultValue ""
            let at = tryGetStringProp el "at" |> Option.bind tryParseUtcOpt

            match at with
            | Some dt -> Some(StatusView.Failed(err, dt))
            | None -> Some(StatusView.Failed(err, DateTime.UtcNow))
        | _ -> None

/// Parse the status field of a plugin-status payload.
let parseStatusField (el: JsonElement) : StatusView =
    match el.ValueKind with
    | JsonValueKind.Object ->
        match parseTaggedStatus el with
        | Some s -> s
        | None -> StatusView.Idle
    | _ -> StatusView.Idle

/// Parse a tagged RunOutcome object, e.g. {"tag":"failed","error":"..."}.
let parseTaggedOutcome (el: JsonElement) : RunOutcome option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        match tryGetStringProp el "tag" with
        | Some "completed" -> Some CompletedRun
        | Some "failed" ->
            let err = tryGetStringProp el "error" |> Option.defaultValue ""
            Some(FailedRun err)
        | Some "timedOut" ->
            let reason = tryGetStringProp el "reason" |> Option.defaultValue ""
            Some(TimedOut reason)
        | _ -> None

/// Parse a lastRun.outcome field (tagged-object shape).
let parseOutcomeField (outcomeEl: JsonElement) : RunOutcome =
    parseTaggedOutcome outcomeEl |> Option.defaultValue CompletedRun

/// Parse a single structured plugin-status JSON element.
let parsePluginStatusElement (el: JsonElement) : ParsedPluginStatus =
    let status =
        match el.TryGetProperty("status") with
        | true, s -> parseStatusField s
        | false, _ -> StatusView.Idle

    let subtasks =
        match el.TryGetProperty("subtasks") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            [ for item in arr.EnumerateArray() ->
                  { Key = item.GetProperty("key").GetString()
                    Label = item.GetProperty("label").GetString()
                    StartedAt = tryParseUtcOr DateTime.UtcNow (item.GetProperty("startedAt").GetString()) } ]
        | _ -> []

    let activityTail =
        match el.TryGetProperty("activityTail") with
        | true, arr when arr.ValueKind = JsonValueKind.Array -> [ for item in arr.EnumerateArray() -> item.GetString() ]
        | _ -> []

    let lastRun =
        match el.TryGetProperty("lastRun") with
        | true, r when r.ValueKind = JsonValueKind.Object ->
            let startedAt =
                tryParseUtcOr DateTime.UtcNow (r.GetProperty("startedAt").GetString())

            let elapsedMs = r.GetProperty("elapsedMs").GetInt64()

            let outcome = parseOutcomeField (r.GetProperty("outcome"))

            let summary = tryGetStringProp r "summary"

            let tail =
                match r.TryGetProperty("activityTail") with
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    [ for item in arr.EnumerateArray() -> item.GetString() ]
                | _ -> []

            Some
                { StartedAt = startedAt
                  Elapsed = TimeSpan.FromMilliseconds(float elapsedMs)
                  Outcome = outcome
                  Summary = summary
                  ActivityTail = tail }
        | _ -> None

    let diagnostics: DiagnosticCounts =
        match el.TryGetProperty("diagnostics") with
        | true, d when d.ValueKind = JsonValueKind.Object ->
            let readInt (name: string) =
                match d.TryGetProperty(name) with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                | _ -> 0

            { Errors = readInt "errors"
              Warnings = readInt "warnings" }
        | _ -> DiagnosticCounts.empty

    { Status = status
      Subtasks = subtasks
      ActivityTail = activityTail
      LastRun = lastRun
      Diagnostics = diagnostics }

/// Parse the top-level JSON object returned by GetStatus into structured per-plugin status.
let parsePluginStatuses (json: string) : Map<string, ParsedPluginStatus> =
    // F8 (audit 2026-05-02): JSON-shape drift from the daemon must be visible —
    // the previous bare `with _` silently rendered an empty UI. Narrow to
    // :? JsonException so a real bug (null, ArgumentException) propagates,
    // and warn-log so producer/consumer schema drift surfaces in CLI logs.
    try
        use doc = JsonDocument.Parse(json)

        [ for prop in doc.RootElement.EnumerateObject() do
              if prop.Value.ValueKind = JsonValueKind.Object then
                  prop.Name, parsePluginStatusElement prop.Value ]
        |> Map.ofList
    with :? JsonException as ex ->
        FsHotWatch.Logging.warn "ipc-parsing" $"Failed to parse plugin-status JSON (schema drift?): %s{ex.Message}"
        Map.empty

/// Project a ParsedPluginStatus map to plain StatusView values.
let statusOnly (parsed: Map<string, ParsedPluginStatus>) : Map<string, StatusView> =
    parsed |> Map.map (fun _ p -> p.Status)

/// Parse the JSON response from GetDiagnostics RPC.
let parseDiagnosticsResponse (json: string) : DiagnosticsResponse =
    use doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let count =
        match root.TryGetProperty("count") with
        | true, v -> v.GetInt32()
        | false, _ -> 0

    let files =
        match root.TryGetProperty("files") with
        | true, filesEl ->
            [ for prop in filesEl.EnumerateObject() do
                  let entries =
                      [ for entry in prop.Value.EnumerateArray() do
                            { Plugin = entry.GetProperty("plugin").GetString()
                              Message = entry.GetProperty("message").GetString()
                              Severity =
                                entry.GetProperty("severity").GetString()
                                |> DiagnosticSeverity.fromString
                                |> Option.defaultValue DiagnosticSeverity.Error
                              Line = entry.GetProperty("line").GetInt32()
                              Column = entry.GetProperty("column").GetInt32()
                              Detail = tryGetStringProp entry "detail" } ]

                  prop.Name, entries ]
            |> Map.ofList
        | false, _ -> Map.empty

    let statuses =
        match root.TryGetProperty("statuses") with
        | true, statusEl ->
            [ for prop in statusEl.EnumerateObject() do
                  if prop.Value.ValueKind = JsonValueKind.Object then
                      prop.Name, parsePluginStatusElement prop.Value ]
            |> Map.ofList
        | false, _ -> Map.empty

    // Coverage backstop (requirement #2): a present, numeric `unchecked` field
    // maps to Complete (0) or Incomplete n (>0); a MISSING or non-numeric field
    // maps to Unknown — never Complete. So an old daemon that doesn't send the
    // field, or a schema/parse gap, can never read as green.
    let coverage =
        match root.TryGetProperty("unchecked") with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, 0 -> Complete
            | true, n when n > 0 -> Incomplete n
            | _ -> Unknown
        | _ -> Unknown

    { Count = count
      Files = files
      Statuses = statuses
      Coverage = coverage }

/// Parse the JSON reply from the test-prune `test-scope` command. Anything the
/// contract does not explicitly recognize collapses to `ScopeUnknown` — including an
/// `{"error": ...}` reply from a daemon with no test-prune plugin, and a `running`
/// reply from a run still in flight (no scope has been earned yet).
let parseTestScope (json: string) : TestScope =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let readInt (name: string) =
            match root.TryGetProperty(name) with
            | true, v when v.ValueKind = JsonValueKind.Number ->
                match v.TryGetInt32() with
                | true, n -> Some n
                | _ -> None
            | _ -> None

        match tryGetStringProp root "scope", readInt "ranProjects", readInt "totalProjects" with
        | Some "full", Some ran, Some total when ran > 0 && ran = total -> FullSuite total
        | Some "filtered", Some ran, Some total -> ImpactFiltered(ran, total)
        | Some "none", _, _ -> NoTestsRun
        | _ -> ScopeUnknown
    with _ ->
        ScopeUnknown

/// Check if all statuses are quiescent (Completed, Failed, or Idle).
/// Returns false for empty maps (no plugins registered yet).
let isAllTerminal (statuses: Map<string, StatusView>) : bool =
    not statuses.IsEmpty
    && statuses |> Map.forall (fun _ s -> StatusView.isQuiescent s)
