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

[<RequireQualifiedAccess>]
type NoTestsReason =
    | AlreadyVerified
    | ChangesUncovered of symbols: string list * total: int
    | Unstated
    | UnknownReason of token: string

module NoTestsReason =
    let describe (reason: NoTestsReason) =
        match reason with
        | NoTestsReason.AlreadyVerified ->
            "no tests ran — this tree is test-equivalent to the last green run (nothing needed re-verifying)"
        | NoTestsReason.ChangesUncovered(symbols, total) ->
            let more = total - List.length symbols
            let suffix = if more > 0 then $" (and %d{more} more)" else ""
            let listed = String.concat ", " symbols
            $"no tests ran — %d{total} changed symbol(s) have NO covering test in the index: %s{listed}%s{suffix}"
        | NoTestsReason.Unstated -> "no tests ran (the daemon did not say why)"
        | NoTestsReason.UnknownReason token ->
            $"no tests ran (reason '%s{token}', which this build does not understand)"

    let ofToken token symbols total =
        match token with
        | None -> NoTestsReason.Unstated
        | Some "already-verified" -> NoTestsReason.AlreadyVerified
        | Some "changes-uncovered" -> NoTestsReason.ChangesUncovered(symbols, max total (List.length symbols))
        | Some token -> NoTestsReason.UnknownReason token

/// What the last completed test run actually COVERED.
///
/// Impact filtering is a latency optimization for the inner dev loop. A merge decision is
/// a correctness claim. An impact-filtered green means "your change didn't break
/// anything I chose to look at" — NOT "the suite is green". Reading one as the other lets
/// tests sit red on `main` while every run stays green, because they were never selected.
///
/// So the scope is a VALUE the verdict is a total function over, not an assumption a
/// caller is trusted to make. Nothing here may EVER read as full-suite unless it
/// positively is one, exactly as `Coverage.Unknown` must never read as `Complete`.
///
/// THERE IS NO SCOPE TO REPORT and I COULD NOT READ THE SCOPE are different facts and
/// are therefore different values. Conflating them makes the inner loop's (correct)
/// tolerance of the first tolerate the second as well, so any fault on the read path
/// becomes a pass. See `ScopeUnreadable`.
type TestScope =
    /// Every configured test project ran, none of them impact-filtered.
    | FullSuite of projects: int
    /// A subset ran: some project was filtered to selected classes, or did not run.
    | ImpactFiltered of ranProjects: int * totalProjects: int
    /// No test run has completed, or the run executed no tests at all.
    | NoTestsRun of reason: NoTestsReason
    /// THERE IS NO SCOPE TO REPORT, and we established that positively: the daemon /
    /// host has no `test-scope` command at all (no test projects are configured, so no
    /// run will ever produce a scope), or it answered that a run is still IN FLIGHT
    /// (the scope is not earned yet). Both are answers, not failures to get one.
    ///
    /// `check` tolerates this — a repo with no tests has no tests to run, and a fast
    /// loop that goes red over that is a fast loop nobody runs. `confirm` refuses it:
    /// an absent scope is not a full-suite scope.
    | ScopeUnknown
    /// I ASKED AND COULD NOT GET AN ANSWER: the command threw, the transport threw, the
    /// reply was not JSON, or it was JSON this build cannot turn into a scope (a daemon
    /// that contradicts its own counts, a shape from another version).
    ///
    /// Distinct from `ScopeUnknown` because the safe answers differ. `ScopeUnknown` is a
    /// provable absence of test evidence to report; this is an absence of KNOWLEDGE about
    /// test evidence that may well exist and may well be `NoTestsRun` — the state both
    /// modes already refuse. So it is refused in BOTH modes, like `NoTestsRun`.
    ///
    /// Carries WHY: a check that starts refusing must be able to say what it could not
    /// read.
    ///
    /// Also the value a verdict RECORDS when the scope it can see does not describe the run
    /// the verdict is about — `confirm` whose forced full run did not complete, left holding
    /// the earlier filtered run's reading (`Verdict.scopeToRecord`, AUTOMATION-258), and a
    /// check that aborted before anyone asked (`IpcOutput.pollAndRender`). Same fact in
    /// every case: THIS verdict has no scope reading of its own, and the reason says so.
    | ScopeUnreadable of reason: string

module TestScope =
    let describe (scope: TestScope) : string =
        match scope with
        | FullSuite n -> $"full suite (%d{n}/%d{n} projects, unfiltered)"
        | ImpactFiltered(ran, total) -> $"impact-filtered (%d{ran}/%d{total} projects)"
        | NoTestsRun reason -> NoTestsReason.describe reason
        | ScopeUnknown -> "unknown (the daemon did not report a test scope)"
        | ScopeUnreadable reason -> $"UNREADABLE — the test scope could not be read (%s{reason})"

    /// Is this scope the EVIDENCE a merge verdict is made of?
    ///
    /// The ONE predicate. `CheckVerdict.verdict` decides what a scope is WORTH; this
    /// decides whether `confirm` must still go and EARN one, and both must agree on
    /// what "full suite" means or `confirm` would demand evidence it then refuses (or,
    /// far worse, accept evidence it never demanded). Every non-`FullSuite` case is
    /// listed, so a new scope defaults to "not evidence" only by an explicit edit here.
    ///
    /// What "full" means: `FullSuite n` says every test project THE CONFIG KNOWS
    /// ABOUT ran unfiltered — it is not a claim about every test in the repo. A
    /// suite missing from `.fshw.json` is missing from this number too.
    let isFullSuite (scope: TestScope) : bool =
        match scope with
        | FullSuite _ -> true
        | ImpactFiltered _
        | NoTestsRun _
        | ScopeUnknown
        | ScopeUnreadable _ -> false

    /// "We asked what ran and the answer faulted" — as distinct from `ScopeUnknown`,
    /// which means the daemon positively reported no scope. Named here rather than
    /// re-matched by hand at each of its several call sites.
    ///
    /// Exhaustive for the same reason as `isFullSuite`: a new scope case is "not
    /// unreadable" only by an explicit edit here.
    let isUnreadable (scope: TestScope) : bool =
        match scope with
        | ScopeUnreadable _ -> true
        | FullSuite _
        | ImpactFiltered _
        | NoTestsRun _
        | ScopeUnknown -> false

/// The test-prune plugin commands `confirm` speaks.
///
/// `RunCommand` dispatches on the COMMAND name — a plugin's own name is not a command
/// and resolves to nothing, so passing `"test-prune"` here would return the
/// unknown-command sentinel and `confirm` would read it as `ScopeUnknown` → exit 3.
///
/// They live HERE, next to the parser of their replies, because four call sites use
/// them (`confirm` and `confirm --run-once`, over two transports) and a literal spelled
/// independently at each is a literal three of them can spell wrong.
[<Literal>]
let TestScopeCommand = "test-scope"

[<Literal>]
let SetScopeCommand = "set-scope"

/// AUTOMATION-259. Ask what `check`'s impact selection WOULD have executed in the run
/// `confirm` widened to full — and whether it would have reached a test that run saw
/// fail.
///
/// `test-scope` says what DID run. This says what would NOT have, and the two are
/// separate commands rather than two fields of one reply on purpose: `test-scope` is on
/// the path that earns every verdict, and a second field there is a second way for the
/// reply that decides a merge to fail to parse.
[<Literal>]
let CheckReachCommand = "check-reach"

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

/// The `force-rebuild` command: make the NEXT build a real one, never a cache
/// replay (AUTOMATION-224).
///
/// The build cache key is a content merkle over SOURCE files only, so a cache hit
/// asserts "the outputs are up to date" on evidence that never covered the outputs.
/// After a working-copy flip (`jj new main`) the sources can match a previously-built
/// tree while `bin/` still holds the PREVIOUS tree's artifacts: the build replays "built
/// N projects (cached)" without running, TestPrune's freshness gate correctly sees stale
/// output and defers every affected project as "waiting on build", and neither side ever
/// moves. That deadlock blocked a production deploy three times.
///
/// `confirm` is the unfiltered verb, so it forces the build the same way it
/// already forces a from-disk scan and a full-suite run. Plain `check` keeps the cache
/// (and reports the residual honestly as incomplete/exit 2).
[<Literal>]
let ForceRebuildCommand = "force-rebuild"

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

/// Parse a tagged status object, e.g. {"tag":"running","since":"..."}, into the CLI-side
/// `StatusView`. TOTAL — every way of not understanding the element is a
/// `StatusView.Unreadable` carrying WHY, never a silent `Idle` and never a drop from the
/// map (which would make `isAllTerminal` read true for a plugin it can no longer see).
/// The verdict travels in `lastRun`, not here.
///
/// The `running`/`since` arm bites hardest: `since` is the ONLY input to the wedge
/// classifier (`pluginOutcomeOf` fires it on `StatusView.Running since` and nowhere
/// else), so an unparseable `since` must read as unreadable rather than idle — otherwise
/// a WEDGED plugin becomes an idle one and wedge detection is defeated by a timestamp.
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
/// An outcome tag this build does not recognize becomes `FailedRun`, NOT `CompletedRun`:
/// an unknown state is not a passing state, the same fail-closed rule as `Verdict.read`.
/// A daemon reporting a run outcome we have no name for has not told us the run
/// succeeded.
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
/// AN UNREADABLE MAP IS NOT AN EMPTY ONE. Swallowing a `JsonException` and returning
/// `Map.empty` would make every plugin vanish — nothing Failed, nothing Running — and
/// any verdict computed over a clean ledger would stay green. `Map.empty` is a CLAIM
/// ("no plugin has anything to say"); a parse failure is the absence of one. Same
/// shape as `Verdict.read`.
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

    // Coverage backstop: a present, numeric `unchecked` field maps to Complete (0)
    // or Incomplete n (>0); a MISSING or non-numeric field maps to Unknown — never
    // Complete. So an old daemon that doesn't send the field, or a schema/parse gap,
    // can never read as green.
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
/// membership from mtimes, which turns a directory listing into a forensics exercise.
type TestRunReport =
    {
        Scope: TestScope
        /// `None` when no run has completed in this daemon session — in which case
        /// there is no run directory, and that ABSENCE is itself the fact.
        ///
        /// AUTOMATION-533. It is ONE run of the several a check provokes — the one the
        /// daemon's evidence receipt names, which is what a verdict is graded from. It
        /// is NOT the answer to "what did this check execute": see `CheckRuns`.
        RunId: Guid option
        /// AUTOMATION-533. Every run the daemon has COMPLETED in this session, newest
        /// first, exactly as the daemon declared them — never inferred from mtimes.
        /// Truncated on the wire (`SessionRunsOnTheWire`).
        ///
        /// EMPTY from a daemon that predates the field, which must read as "this daemon
        /// does not say", never as "there were no other runs".
        SessionRuns: Guid list
        /// AUTOMATION-533. The runs THIS CHECK is accountable for: the session runs the
        /// daemon had not yet completed when the check began. Computed by the check
        /// driver (`TestRunEvidence.attribute`) by diffing `SessionRuns` against the
        /// baseline it read before its scan — so membership is DECLARED at both ends,
        /// and a run belonging to an earlier check cannot be adopted by this one.
        ///
        /// EMPTY on a report nobody has attributed yet, and on the abort paths, where
        /// the check never got far enough to ask.
        CheckRuns: Guid list
        /// The change that SELECTED the last run's tests, truncated on the wire.
        /// Empty from a daemon older than this field, and empty when nothing has
        /// triggered a run yet — both mean "cannot say", and both must render as
        /// silence rather than as "nothing triggered it".
        Seeds: string list
        /// How many seeds there really were, before `Seeds` was truncated. Lets a
        /// report say "and 12 more" instead of implying the short list is all of
        /// them. Zero from an older daemon.
        SeedCount: int
    }

module TestRunReport =
    /// A report carrying ONLY a scope — for the paths where no run was observed at
    /// all: the command threw, the transport faulted, the check aborted before the
    /// scope could be read.
    ///
    /// Exists so those sites state the one fact they have instead of spelling out
    /// every empty field. Five of them had to be edited by hand when `Seeds` was
    /// added; with this, the next field is one edit, and no call site can quietly
    /// invent a value for something it never learned.
    let ofScopeOnly (scope: TestScope) : TestRunReport =
        { Scope = scope
          RunId = None
          SessionRuns = []
          CheckRuns = []
          Seeds = []
          SeedCount = 0 }

/// Parse the JSON reply from the test-prune `test-scope` command.
///
/// Fails CLOSED in every direction — nothing here can round UP to `FullSuite` — but the
/// two ways of not getting a scope are kept APART, because the inner loop must treat
/// them differently:
///
///   * `running` is an ANSWER: a run is in flight, so no scope has been earned yet.
///     `ScopeUnknown`, which `check` tolerates. (An `{"error": ...}` reply from a daemon
///     with no test-prune plugin never reaches here — both transports detect the
///     unknown-command sentinel first.)
///   * ANY other unrecognized reply is a FAILURE to get one: not JSON, a shape from
///     another version, or a daemon contradicting its own counts ("full", 2 of 4 — the
///     counts are the evidence and the label is not). `ScopeUnreadable`, which BOTH
///     modes refuse (see the case's docs).
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

        let noTestsReason () =
            let symbols =
                match root.TryGetProperty("uncoveredSymbols") with
                | true, value when value.ValueKind = JsonValueKind.Array ->
                    value.EnumerateArray()
                    |> Seq.choose (fun item ->
                        if item.ValueKind = JsonValueKind.String then
                            Some(item.GetString())
                        else
                            None)
                    |> Seq.toList
                | _ -> []

            NoTestsReason.ofToken
                (tryGetStringProp root "noTestsReason")
                symbols
                (readInt "uncoveredSymbolCount" |> Option.defaultValue (List.length symbols))

        let scope =
            match tryGetStringProp root "scope", readInt "ranProjects", readInt "totalProjects" with
            | Some "full", Some ran, Some total when ran > 0 && ran = total -> FullSuite total
            | Some "filtered", Some ran, Some total when ran > 0 && total > 0 && ran <= total ->
                ImpactFiltered(ran, total)
            | Some "none", _, _ -> NoTestsRun(noTestsReason ())
            | Some "running", _, _ -> ScopeUnknown
            | _ -> ScopeUnreadable $"the daemon's `%s{TestScopeCommand}` reply is not a scope this build recognizes"

        let runId =
            tryGetStringProp root "runId"
            |> Option.bind (fun s ->
                match Guid.TryParse s with
                | true, g -> Some g
                | _ -> None)

        // Absent seeds are NOT a shape this build fails to recognize — an older
        // daemon simply does not send them. They must degrade to silence, never to
        // `ScopeUnreadable`: a diagnostic nicety may not be able to turn a check
        // into a refusal.
        let seeds =
            match root.TryGetProperty("seeds") with
            | true, v when v.ValueKind = JsonValueKind.Array ->
                v.EnumerateArray()
                |> Seq.choose (fun el ->
                    if el.ValueKind = JsonValueKind.String then
                        Some(el.GetString())
                    else
                        None)
                |> Seq.toList
            | _ -> []

        let seedCount =
            match readInt "seedCount" with
            | Some n when n >= 0 -> n
            | _ -> List.length seeds

        // AUTOMATION-533. Absent from a daemon that predates the field — silence, not
        // "there was one run". An unparseable entry is DROPPED rather than allowed to
        // fail the reply: a diagnostic list may not turn a check into a refusal.
        let sessionRuns =
            match root.TryGetProperty("runIds") with
            | true, v when v.ValueKind = JsonValueKind.Array ->
                v.EnumerateArray()
                |> Seq.choose (fun el ->
                    if el.ValueKind = JsonValueKind.String then
                        match Guid.TryParse(el.GetString()) with
                        | true, g -> Some g
                        | _ -> None
                    else
                        None)
                |> Seq.toList
            | _ -> []

        { Scope = scope
          RunId = runId
          SessionRuns = sessionRuns
          // Attribution is the DRIVER's to make: it is the only party that knows what
          // the daemon had already run before this check started. A parser that guessed
          // would hand every check the whole session.
          CheckRuns = []
          Seeds = seeds
          SeedCount = seedCount }
    with ex ->
        { Scope = ScopeUnreadable $"the daemon's `%s{TestScopeCommand}` reply could not be parsed: %s{ex.Message}"
          RunId = None
          SessionRuns = []
          CheckRuns = []
          Seeds = []
          SeedCount = 0 }

/// AUTOMATION-259. Did the impact selection `check` WOULD have used reach a test this
/// run saw fail?
///
/// The one question a `confirm` can answer for free about `check`. `confirm` widens the
/// scope to full BEFORE the scan that provokes the run, so the impact selection is
/// computed, discarded, and — until it was retained — unrecoverable: `check` and
/// `confirm` are separate invocations, and a day of running them side by side
/// (2026-08-06) produced not one clean pair because the tree moved between every one.
///
/// It is a claim about REACH and nothing else. A test that failed here failed inside a
/// full suite; whether it would also have failed in a narrower run — order, isolation, a
/// shared fixture — cannot be observed from one execution and is never asserted. That is
/// what the verdict's `basis` field exists to tell a reader.
type CheckReach =
    /// At least one test this run saw fail is inside the retained selection: `check`
    /// would have executed it, and would have been red for the same reason.
    | ReachedAFailure of failingSuites: string list
    /// The run saw tests fail; the retained selection reaches NONE of them. `check` would
    /// have gone green over a tree with a real failure in it — AUTOMATION-160's defect,
    /// caught on the same tree that produced it.
    | ReachedNoFailure of missed: MissedFailure list
    /// No test failed, so there was no failure for a selection to reach. Distinct from
    /// `ReachedNoFailure`: nothing was missed because nothing was there.
    | NoFailuresToReach
    /// The reach could not be decided, and says why. Refused rather than guessed: a guess
    /// in either direction invents a comparison, and a guess in the reaching direction
    /// invents an AGREEMENT — the one reading this record must never manufacture.
    | ReachUnknown of reason: string

and MissCause =
    | ProjectNotSelected
    | ClassNotInFilter
    | UnknownMissCause of token: string

and MissedFailure =
    { Project: string
      Class: string
      Cause: MissCause }

type FailureRecall =
    | FailureRecallMeasured of reached: int * total: int * threshold: float * acceptable: bool
    | FailureRecallNotMeasurable of reason: string

/// The `check-reach` reply, parsed.
type CheckReachReport =
    {
        /// The run the projection belongs to. Compared with the run the VERDICT is about,
        /// so a projection left over from an earlier run can never be attached to a later
        /// one. `None` when the daemon did not name a run — which is itself a mismatch.
        RunId: Guid option
        /// What `check` would have covered, in the same shape `test-scope` reports what
        /// actually ran. `ScopeUnreadable` where there is no retained selection to
        /// describe.
        Scope: TestScope
        Reach: CheckReach
        Recall: FailureRecall
    }

/// Whether there is a projection to read at all. Absence is a VALUE — a daemon with no
/// `check-reach` command, a reply that would not parse, a session with no completed run —
/// because every one of those must reach the verdict as "nothing was compared" and none
/// of them may reach it as "they agreed".
type CheckReachReading =
    | ReachRecorded of CheckReachReport
    | ReachUnavailable of reason: string

/// Parse a `check-reach` reply. TOTAL and FAIL-CLOSED: every way of not understanding the
/// reply lands on `ReachUnavailable` or `ReachUnknown`, never on a reach that claims the
/// selection covered anything.
let parseCheckReach (json: string) : CheckReachReading =
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

        let recorded =
            match root.TryGetProperty("recorded") with
            | true, v when v.ValueKind = JsonValueKind.True -> true
            | _ -> false

        if not recorded then
            ReachUnavailable(
                tryGetStringProp root "reason"
                |> Option.defaultValue $"the daemon's `%s{CheckReachCommand}` reply records no projection"
            )
        else
            let reason =
                tryGetStringProp root "reason"
                |> Option.defaultValue "the daemon did not say why the reach could not be decided"

            let failingSuites =
                match root.TryGetProperty("failingSuites") with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    v.EnumerateArray()
                    |> Seq.choose (fun el ->
                        if el.ValueKind = JsonValueKind.String then
                            Some(el.GetString())
                        else
                            None)
                    |> Seq.toList
                | _ -> []

            let missed =
                match root.TryGetProperty("missed") with
                | true, value when value.ValueKind = JsonValueKind.Array ->
                    value.EnumerateArray()
                    |> Seq.choose (fun item ->
                        match tryGetStringProp item "project", tryGetStringProp item "class" with
                        | Some project, Some className ->
                            let cause =
                                match tryGetStringProp item "cause" with
                                | Some "project-not-selected" -> ProjectNotSelected
                                | Some "class-not-in-filter" -> ClassNotInFilter
                                | Some token -> UnknownMissCause token
                                | None -> UnknownMissCause "unstated"

                            Some
                                { Project = project
                                  Class = className
                                  Cause = cause }
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            let reach =
                match tryGetStringProp root "reach" with
                | Some "reached-a-failure" when not (List.isEmpty failingSuites) -> ReachedAFailure failingSuites
                // A reach that claims a failure and names no suite is not a reading this
                // build can record: the classification and the evidence for it travel
                // together or not at all.
                | Some "reached-a-failure" ->
                    ReachUnknown "the daemon reported a reached failure but named no failing suite"
                | Some "reached-no-failure" -> ReachedNoFailure missed
                | Some "no-failures-to-reach" -> NoFailuresToReach
                | Some "unknown" -> ReachUnknown reason
                | Some other ->
                    ReachUnknown $"the daemon reported reach '%s{other}', which this build does not understand"
                | None -> ReachUnknown "the daemon's reply does not say what the selection reached"

            let scope =
                match tryGetStringProp root "scope", readInt "ranProjects", readInt "totalProjects" with
                | Some "full", Some ran, Some total when ran > 0 && ran = total -> FullSuite total
                | Some "filtered", Some ran, Some total -> ImpactFiltered(ran, total)
                | Some "none", _, _ -> NoTestsRun NoTestsReason.Unstated
                | _ -> ScopeUnreadable "the daemon reported no scope for the selection `check` would have used"

            let runId =
                tryGetStringProp root "runId"
                |> Option.bind (fun s ->
                    match Guid.TryParse s with
                    | true, g -> Some g
                    | _ -> None)

            let recall =
                match root.TryGetProperty("conditionalFailureRecall") with
                | true, value when value.ValueKind = JsonValueKind.Object ->
                    let measured =
                        match value.TryGetProperty("measured") with
                        | true, flag when flag.ValueKind = JsonValueKind.True -> true
                        | _ -> false

                    let nestedInt (name: string) =
                        match value.TryGetProperty(name) with
                        | true, number when number.ValueKind = JsonValueKind.Number ->
                            match number.TryGetInt32() with
                            | true, parsed -> Some parsed
                            | _ -> None
                        | _ -> None

                    if measured then
                        match
                            nestedInt "reached",
                            nestedInt "total",
                            value.TryGetProperty("threshold"),
                            value.TryGetProperty("acceptable")
                        with
                        | Some reached, Some total, (true, threshold), (true, acceptable) when
                            total > 0
                            && reached >= 0
                            && reached <= total
                            && threshold.ValueKind = JsonValueKind.Number
                            && (acceptable.ValueKind = JsonValueKind.False
                                || acceptable.ValueKind = JsonValueKind.True)
                            ->
                            let thresholdValue = threshold.GetDouble()
                            let acceptableValue = acceptable.GetBoolean()

                            if thresholdValue = 1.0 && acceptableValue = (reached = total) then
                                FailureRecallMeasured(reached, total, thresholdValue, acceptableValue)
                            else
                                FailureRecallNotMeasurable
                                    "the daemon's recall threshold or acceptance flag is inconsistent"
                        | _ -> FailureRecallNotMeasurable "the daemon's measured recall fields are invalid"
                    else
                        FailureRecallNotMeasurable(
                            tryGetStringProp value "reason"
                            |> Option.defaultValue "the daemon did not produce a measurable recall denominator"
                        )
                | _ -> FailureRecallNotMeasurable "the daemon predates measured failure recall"

            ReachRecorded
                { RunId = runId
                  Scope = scope
                  Reach = reach
                  Recall = recall }
    with ex ->
        ReachUnavailable $"the daemon's `%s{CheckReachCommand}` reply could not be parsed: %s{ex.Message}"

/// Check if all statuses are quiescent (Completed, Failed, or Idle).
/// Returns false for empty maps (no plugins registered yet).
let isAllTerminal (statuses: Map<string, StatusView>) : bool =
    not statuses.IsEmpty
    && statuses |> Map.forall (fun _ s -> StatusView.isQuiescent s)

// ---------------------------------------------------------------------------
// AUTOMATION-555 (rework). The daemon's OWN account of where its wall time went.
// ---------------------------------------------------------------------------

/// The phase ledger a daemon serves beside its plugin statuses (`daemonPhases` on the
/// diagnostics response) — every phase it spent wall time in, its own and every
/// plugin's `Running` interval, superseded runs included. `NotServed` is an older
/// daemon (or an embedder) that carries no ledger: the verdict then falls back to
/// each plugin's `lastRun`, and says so through its coverage rather than pretending.
[<RequireQualifiedAccess>]
type DaemonEvidence =
    | NotServed
    | Served of FsHotWatch.DaemonPhases.PhaseRecord list

module DaemonEvidence =
    /// The ledger of an in-process host (`--run-once`), read now.
    let ofHost (host: FsHotWatch.PluginHost.PluginHost) : DaemonEvidence =
        DaemonEvidence.Served(host.Phases.Snapshot(DateTime.UtcNow))

    /// The `daemonPhases` array of a diagnostics response. Entries that do not carry a
    /// scope, a parseable start and an elapsed time are dropped: a phase that cannot be
    /// placed is not evidence, and the coverage it fails to explain surfaces as a gap.
    let parse (json: string) : DaemonEvidence =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            match root.ValueKind, root.TryGetProperty("daemonPhases") with
            | JsonValueKind.Object, (true, phases) when phases.ValueKind = JsonValueKind.Array ->
                [ for phase in phases.EnumerateArray() do
                      if phase.ValueKind = JsonValueKind.Object then
                          let scope = tryGetStringProp phase "scope"

                          let startedAt =
                              match tryGetStringProp phase "startedAt" with
                              | Some s ->
                                  match
                                      DateTime.TryParse(
                                          s,
                                          Globalization.CultureInfo.InvariantCulture,
                                          Globalization.DateTimeStyles.AdjustToUniversal
                                          ||| Globalization.DateTimeStyles.AssumeUniversal
                                      )
                                  with
                                  | true, parsed -> Some parsed
                                  | _ -> None
                              | None -> None

                          let elapsedMs =
                              match phase.TryGetProperty("elapsedMs") with
                              | true, v when v.ValueKind = JsonValueKind.Number ->
                                  match v.TryGetInt64() with
                                  | true, n -> Some n
                                  | _ -> None
                              | _ -> None

                          match scope, startedAt, elapsedMs with
                          | Some scope, Some startedAt, Some elapsedMs ->
                              yield
                                  ({ Scope = scope
                                     StartedAt = startedAt
                                     Elapsed = TimeSpan.FromMilliseconds(float elapsedMs)
                                     Detail = tryGetStringProp phase "detail" }
                                  : FsHotWatch.DaemonPhases.PhaseRecord)
                          | _ -> () ]
                |> DaemonEvidence.Served
            | _ -> DaemonEvidence.NotServed
        with :? JsonException ->
            DaemonEvidence.NotServed
