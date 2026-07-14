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
/// Impact filtering is a latency optimization for the inner dev loop. A merge decision is
/// a correctness claim. An impact-filtered green means "your change didn't break
/// anything I chose to look at" — NOT "the suite is green". Those are different claims
/// and reading one as the other is how 35 tests sat red on `main` for an unknown period
/// while every run stayed green: they were never selected, so nothing ever ran them.
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

    /// Is this scope the EVIDENCE a merge verdict is made of?
    ///
    /// The ONE predicate. `CheckVerdict.verdict` decides what a scope is WORTH; this
    /// decides whether `confirm` must still go and EARN one, and both must agree on
    /// what "full suite" means or `confirm` would demand evidence it then refuses (or,
    /// far worse, accept evidence it never demanded). Every non-`FullSuite` case is
    /// listed, so a new scope defaults to "not evidence" only by an explicit edit here.
    ///
    /// NOTE ON WHAT "FULL" MEANS. `FullSuite n` says every test project THE CONFIG
    /// KNOWS ABOUT ran unfiltered — it is not a claim about every test in the repo. A
    /// suite missing from `.fshw.json` is missing from this number too (AUTOMATION-158).
    let isFullSuite (scope: TestScope) : bool =
        match scope with
        | FullSuite _ -> true
        | ImpactFiltered _
        | NoTestsRun
        | ScopeUnknown -> false

/// The test-prune plugin commands `confirm` speaks.
///
/// `RunCommand` dispatches on the COMMAND name — a plugin's own name is not a command
/// and resolves to nothing. Passing `"test-prune"` here is precisely the bug
/// AUTOMATION-129 fixed: the host found no such command, returned the unknown-command
/// sentinel, and `confirm` read it as `ScopeUnknown` → exit 3 on every repo, forever.
///
/// They live HERE, next to the parser of their replies, because there are now FOUR
/// callers — `confirm` and `confirm --run-once`, over two different transports — and a
/// literal that four call sites can spell independently is a literal three of them can
/// spell wrong.
[<Literal>]
let TestScopeCommand = "test-scope"

[<Literal>]
let SetScopeCommand = "set-scope"

/// The `run-tests` command, with no filter and no project selection: run EVERY
/// configured test project. This is how `confirm` FORCES the run it demands rather
/// than merely asking for it (see `RunOnceCheck` / `IpcOutput.pollAndRender`).
[<Literal>]
let RunTestsCommand = "run-tests"

/// The `set-scope` payload that turns impact filtering OFF for the rest of the
/// session. A REQUEST, never evidence: `confirm` reads back what actually ran
/// (`TestScopeCommand`) and refuses anything less.
[<Literal>]
let FullSuiteScopeArgs = """{"scope":"full"}"""

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
/// the CLI-side `StatusView`. TOTAL — every way of not understanding the element is a
/// `StatusView.Unreadable` carrying WHY, never a silent `Idle` and never a drop from
/// the map (which would make `isAllTerminal` read true for a plugin it can no longer
/// see). The verdict isn't carried here at all; it travels in `lastRun`, the one channel.
///
/// The `running`/`since` arm is the one that bites hardest. `since` is the ONLY input to
/// AUTOMATION-147's wedge classifier — `pluginOutcomeOf` fires it on `StatusView.Running
/// since` and nowhere else — so a `since` that would not parse used to turn a WEDGED
/// plugin into an idle one, and 147's entire detection was defeated silently, from a
/// timestamp. An unparseable `since` is now unreadable, not idle.
let parseTaggedStatus (el: JsonElement) : StatusView =
    let unreadable (why: string) =
        StatusView.Unreadable $"unreadable plugin status: %s{why}"

    if el.ValueKind <> JsonValueKind.Object then
        unreadable $"expected a tagged object, got %A{el.ValueKind}"
    else
        match tryGetStringProp el "tag" with
        | Some "idle" -> StatusView.Idle
        | Some "running" ->
            match tryGetStringProp el "since" |> Option.bind tryParseUtcOpt with
            | Some since -> StatusView.Running since
            | None -> unreadable "a `running` status with no readable `since` — the wedge bound cannot be applied to it"
        | Some "completed" ->
            match tryGetStringProp el "at" |> Option.bind tryParseUtcOpt with
            | Some at -> StatusView.Completed at
            | None -> unreadable "a `completed` status with no readable `at`"
        | Some "failed" ->
            let err = tryGetStringProp el "error" |> Option.defaultValue ""
            let at = tryGetStringProp el "at" |> Option.bind tryParseUtcOpt

            match at with
            | Some dt -> StatusView.Failed(err, dt)
            | None -> StatusView.Failed(err, DateTime.UtcNow)
        | Some tag -> unreadable $"a status tag this build does not recognize: '%s{tag}'"
        | None -> unreadable "a status object with no `tag`"

/// Parse the status field of a plugin-status payload. Fail closed: see `parseTaggedStatus`.
let parseStatusField (el: JsonElement) : StatusView = parseTaggedStatus el

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
///
/// An outcome tag this build does not recognize becomes `FailedRun`, NOT `CompletedRun`.
/// It defaulted to `CompletedRun` — a PASS — which is the same fail-open `Verdict.read`
/// forbids one file away, in words: "an unknown state is not a passing state". A daemon
/// reporting a run outcome we have no name for has not told us the run succeeded.
let parseOutcomeField (outcomeEl: JsonElement) : RunOutcome =
    match parseTaggedOutcome outcomeEl with
    | Some outcome -> outcome
    | None -> FailedRun "unrecognized outcome tag — this build cannot read the run's result"

/// Parse a single structured plugin-status JSON element.
let parsePluginStatusElement (el: JsonElement) : ParsedPluginStatus =
    let status =
        match el.TryGetProperty("status") with
        | true, s -> parseStatusField s
        // No `status` at all. Not idle — we were told nothing.
        | false, _ -> StatusView.Unreadable "unreadable plugin status: the payload carries no `status` field"

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

/// Parse the top-level JSON object returned by GetStatus into structured per-plugin
/// status — or say, in a NAMED case, that it could not be read.
///
/// AN UNREADABLE MAP IS NOT AN EMPTY ONE. This used to swallow a `JsonException` and
/// return `Map.empty`, at which point every plugin vanished: nothing Failed, nothing
/// Running, nothing to report — and any verdict computed over a clean ledger stayed
/// green. `Map.empty` is a CLAIM ("no plugin has anything to say"); a parse failure is
/// the absence of one, and the two must not be spelled the same way. Same shape as
/// `Verdict.read`, which already refuses to round an unreadable file to a verdict.
///
/// Only `JsonException` is caught. A real programming bug (null, ArgumentException)
/// still propagates — a fail-closed reader is not a bug-swallowing one.
let parsePluginStatuses (json: string) : Result<Map<string, ParsedPluginStatus>, string> =
    try
        use doc = JsonDocument.Parse(json)

        [ for prop in doc.RootElement.EnumerateObject() do
              if prop.Value.ValueKind = JsonValueKind.Object then
                  prop.Name, parsePluginStatusElement prop.Value ]
        |> Map.ofList
        |> Result.Ok
    with :? JsonException as ex ->
        FsHotWatch.Logging.warn "ipc-parsing" $"Failed to parse plugin-status JSON (schema drift?): %s{ex.Message}"
        Result.Error $"the daemon's plugin-status payload could not be parsed (schema drift?): %s{ex.Message}"

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

/// What the last completed test run covered, AND WHICH RUN IT WAS.
///
/// The run id is what lets the verdict DECLARE which CTRF reports are this run's
/// (they are the files in `.fshw/test-runs/<runId>/`) instead of inferring
/// membership from mtimes. Membership by inference is how a directory listing
/// becomes a forensics exercise — and how two readers, an hour apart, each
/// mistook another run's artifacts (or an absence) for an answer.
type TestRunReport =
    {
        Scope: TestScope
        /// `None` when no run has completed in this daemon session — in which case
        /// there is no run directory, and that ABSENCE is itself the fact.
        RunId: Guid option
    }

/// Parse the JSON reply from the test-prune `test-scope` command. Anything the
/// contract does not explicitly recognize collapses to `ScopeUnknown` — including an
/// `{"error": ...}` reply from a daemon with no test-prune plugin, and a `running`
/// reply from a run still in flight (no scope has been earned yet).
let parseTestRunReport (json: string) : TestRunReport =
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

        let scope =
            match tryGetStringProp root "scope", readInt "ranProjects", readInt "totalProjects" with
            | Some "full", Some ran, Some total when ran > 0 && ran = total -> FullSuite total
            | Some "filtered", Some ran, Some total -> ImpactFiltered(ran, total)
            | Some "none", _, _ -> NoTestsRun
            | _ -> ScopeUnknown

        let runId =
            tryGetStringProp root "runId"
            |> Option.bind (fun s ->
                match Guid.TryParse s with
                | true, g -> Some g
                | _ -> None)

        { Scope = scope; RunId = runId }
    with _ ->
        { Scope = ScopeUnknown; RunId = None }

/// Check if all statuses are quiescent (Completed, Failed, or Idle).
/// Returns false for empty maps (no plugins registered yet).
let isAllTerminal (statuses: Map<string, StatusView>) : bool =
    not statuses.IsEmpty
    && statuses |> Map.forall (fun _ s -> StatusView.isQuiescent s)
