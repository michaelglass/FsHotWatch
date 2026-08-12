module FsHotWatch.Cli.ProgressRenderer

open System
open CommandTree
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginActivity
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing

/// Rendering mode for the progress block. Verbose is the default;
/// Compact collapses each plugin to a single line.
type RenderMode =
    | Compact
    | Verbose
    | Agent

/// Status glyphs (already wrapped in ANSI colors) and the em-dash used as an inline
/// text separator. Kept in one place so the visual language stays consistent.
module private Glyph =
    let check = $"%s{Color.green}✓%s{Color.reset}"
    let warn = $"%s{Color.red}⚠%s{Color.reset}"
    let cross = $"%s{Color.red}✗%s{Color.reset}"
    let timeout = $"%s{Color.red}⏱%s{Color.reset}"
    let ellipsis = $"%s{Color.yellow}…%s{Color.reset}"
    let idle = $"%s{Color.dim}—%s{Color.reset}"
    /// Em-dash used as an inline separator in prose (e.g. "  ✓ build — summary").
    let sep = "—"

let private padName (name: string) = name.PadRight(24)

/// Format a clock portion like "14:02:07" (UTC to match the rest of the
/// daemon's timestamps).
let private clock (t: DateTime) = t.ToString("HH:mm:ss")

let private truncateTo80 (s: string) : string =
    if s.Length <= 80 then s else s.Substring(0, 77) + "..."

/// Truncate a potentially multi-line error to its first non-empty line, then
/// shorten to roughly 80 printable characters.
let private summariseError (error: string) : string =
    if String.IsNullOrEmpty error then
        ""
    else
        error.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryHead
        |> Option.defaultValue ""
        |> truncateTo80

let private runSummary (lastRun: RunRecord) : string =
    match lastRun.Outcome, lastRun.Summary with
    | _, Some s -> s
    | CompletedRun, None -> ""
    | FailedRun err, None -> summariseError err
    | TimedOut reason, None -> summariseError reason

let private latestActivity (tail: string list) =
    match tail |> List.tryLast with
    | Some s -> s
    | None -> ""

let private isTimedOut (parsed: ParsedPluginStatus) : bool =
    match parsed.LastRun with
    | Some { Outcome = TimedOut _ } -> true
    | _ -> false

/// Wedge classification for a Running plugin — the SAME ambient bound and the SAME
/// words as the daemon-side monitor, so `fshw status` and the daemon log can never
/// disagree about a wedge. Past the bound a plugin is never rendered as merely
/// "running".
let private runningWedge (now: DateTime) (since: DateTime) : FsHotWatch.PluginWedge.RunningHealth =
    FsHotWatch.PluginWedge.classifyRunning (FsHotWatch.PluginWedge.ambientBound ()) now since

/// The one wedged status line body (name-independent part), local-time clock
/// to match the renderer's other timestamps.
let private wedgedBody (since: DateTime) (elapsed: TimeSpan) : string =
    FsHotWatch.PluginWedge.wedgedText (since.ToLocalTime()) elapsed

/// The words shown for a Completed status that carries NO run record. A
/// content-free ✓ is not evidence — there is no `elapsed:` to prove what ran — so
/// the renderer fails closed: no record ⇒ no ✓, and the absence is stated in words.
[<Literal>]
let CompletedNoRecordText =
    "completed, but no run record was posted — cannot verify what ran"

/// Pull the elapsed-time display for a terminal status. Returns `Some` only
/// when the run record is present AND its elapsed is non-zero — a missing
/// record or a zero elapsed almost always means the cache-replay path
/// produced a synthetic terminal record without going through `Running`,
/// so we'd rather show no timing than claim "(0ms)" for a build that
/// actually took 30s.
let private terminalTimingStr (parsed: ParsedPluginStatus) : string option =
    match parsed.LastRun with
    | Some r when r.Elapsed > TimeSpan.Zero -> Some(UI.timing r.Elapsed)
    | _ -> None

// ----- Compact -----

let private renderCompact
    (warningsAreFailures: bool)
    (now: DateTime)
    (name: string)
    (parsed: ParsedPluginStatus)
    : string list =
    let padded = padName name

    let line =
        match parsed.Status with
        // Fail closed: no run record ⇒ no bare ✓ (see `CompletedNoRecordText`). When the
        // ledger has failing diagnostics the issue-count path below is already honest, so
        // it takes precedence.
        | StatusView.Completed _ when
            parsed.LastRun.IsNone
            && not (DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics)
            ->
            $"  %s{Glyph.warn} %s{padded} %s{Color.dim}{Glyph.sep} %s{CompletedNoRecordText}%s{Color.reset}"
        | StatusView.Completed _ ->
            let withIssues = DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics

            let summary =
                if withIssues then
                    $" %s{Color.dim}{Glyph.sep} %s{DiagnosticCounts.summary parsed.Diagnostics}%s{Color.reset}"
                else
                    match parsed.LastRun with
                    | Some r ->
                        match runSummary r with
                        | "" -> ""
                        | s -> $" %s{Color.dim}{Glyph.sep} %s{s}%s{Color.reset}"
                    | None -> ""

            let timingPart =
                match terminalTimingStr parsed with
                | Some t -> $" %s{t}"
                | None -> ""

            let glyph = if withIssues then Glyph.warn else Glyph.check

            $"  %s{glyph} %s{padded}%s{timingPart}%s{summary}"
        | StatusView.Failed(err, _) ->
            let short = summariseError err

            let timingPart =
                match terminalTimingStr parsed with
                | Some t -> $" %s{t}"
                | None -> ""

            let timedOut = isTimedOut parsed
            let glyph = if timedOut then Glyph.timeout else Glyph.cross

            let label =
                if timedOut then
                    if String.IsNullOrEmpty short then
                        "timed out"
                    else
                        $"timed out: %s{short}"
                else
                    short

            $"  %s{glyph} %s{padded}%s{timingPart} %s{Color.dim}{Glyph.sep} %s{label}%s{Color.reset}"
        | StatusView.Running since ->
            match runningWedge now since with
            | FsHotWatch.PluginWedge.RunningHealth.Wedged(s, e) ->
                $"  %s{Glyph.warn} %s{padded} %s{Color.red}%s{wedgedBody s e}%s{Color.reset}"
            | FsHotWatch.PluginWedge.RunningHealth.StillRunning elapsed ->
                let timingStr = UI.timing elapsed

                let detail =
                    match parsed.Subtasks with
                    | [] ->
                        let la = latestActivity parsed.ActivityTail

                        if la = "" then
                            ""
                        else
                            $" %s{Color.dim}{Glyph.sep} %s{la}%s{Color.reset}"
                    | xs ->
                        // Prefer the primary subtask's descriptive label when present;
                        // otherwise summarise concurrent subtasks by key.
                        let primary = xs |> List.tryFind (fun s -> s.Key = PrimarySubtaskKey)

                        match primary with
                        | Some p -> $" %s{Color.dim}{Glyph.sep} %s{p.Label}%s{Color.reset}"
                        | None ->
                            let n = List.length xs
                            let names = xs |> List.map (fun s -> s.Key) |> String.concat ", "
                            $" %s{Color.dim}{Glyph.sep} %d{n} running: %s{names}%s{Color.reset}"

                $"  %s{Glyph.ellipsis} %s{padded} %s{timingStr}%s{detail}"
        // A status this build could not read. Stated in words and glyphed as a failure —
        // never a ✓, never a silent omission: the operator needs to know that the daemon
        // said something unreadable, not that the plugin was idle.
        | StatusView.Unreadable reason -> $"  %s{Glyph.cross} %s{padded} %s{Color.red}%s{reason}%s{Color.reset}"
        | StatusView.Idle ->
            match parsed.LastRun with
            | Some r ->
                let timingStr = UI.timing r.Elapsed
                let t = clock (r.StartedAt.ToLocalTime())

                let summary =
                    match runSummary r with
                    | "" -> ""
                    | s -> $" {Glyph.sep} %s{s}"

                $"  %s{Color.dim}{Glyph.sep} %s{padded} last: %s{timingStr} (%s{t})%s{summary}%s{Color.reset}"
            | None -> $"  %s{Color.dim}{Glyph.sep} %s{padded}%s{Color.reset}"

    [ line ]

// ----- Verbose -----

let private glyphForParsed (warningsAreFailures: bool) (parsed: ParsedPluginStatus) =
    match parsed.Status with
    | StatusView.Completed _ when DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics -> Glyph.warn
    | StatusView.Completed _ -> Glyph.check
    | StatusView.Failed _ when isTimedOut parsed -> Glyph.timeout
    | StatusView.Failed _ -> Glyph.cross
    | StatusView.Unreadable _ -> Glyph.cross
    | StatusView.Running _ -> Glyph.ellipsis
    | StatusView.Idle -> Glyph.idle

let private verboseHeader
    (warningsAreFailures: bool)
    (now: DateTime)
    (name: string)
    (parsed: ParsedPluginStatus)
    : string =
    let padded = padName name
    let glyph = glyphForParsed warningsAreFailures parsed

    match parsed.Status with
    | StatusView.Running since ->
        match runningWedge now since with
        | FsHotWatch.PluginWedge.RunningHealth.Wedged(s, e) ->
            $"  %s{Glyph.warn} %s{padded} %s{Color.red}%s{wedgedBody s e}%s{Color.reset}"
        | FsHotWatch.PluginWedge.RunningHealth.StillRunning elapsed ->
            let n = List.length parsed.Subtasks

            let detail =
                if n > 0 then
                    $" %s{Color.dim}{Glyph.sep} %d{n} running%s{Color.reset}"
                else
                    ""

            $"  %s{glyph} %s{padded} %s{UI.timing elapsed}%s{detail}"
    // Fail closed: Completed with NO run record never renders as a bare ✓ —
    // the absence is stated in words (see CompletedNoRecordText).
    | StatusView.Completed _ when
        parsed.LastRun.IsNone
        && not (DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics)
        ->
        $"  %s{Glyph.warn} %s{padded} %s{Color.dim}{Glyph.sep} %s{CompletedNoRecordText}%s{Color.reset}"
    | StatusView.Completed _ ->
        let timingPart =
            match terminalTimingStr parsed with
            | Some t -> $" %s{t}"
            | None -> ""

        let summary =
            match parsed.LastRun |> Option.map runSummary with
            | Some s when s <> "" -> $" %s{Color.dim}{Glyph.sep} %s{s}%s{Color.reset}"
            | _ -> ""

        $"  %s{glyph} %s{padded}%s{timingPart}%s{summary}"
    | StatusView.Failed(err, _) ->
        let timingPart =
            match terminalTimingStr parsed with
            | Some t -> $" %s{t}"
            | None -> ""

        $"  %s{glyph} %s{padded}%s{timingPart} %s{Color.dim}{Glyph.sep} %s{summariseError err}%s{Color.reset}"
    | StatusView.Unreadable reason -> $"  %s{glyph} %s{padded} %s{Color.red}%s{reason}%s{Color.reset}"
    | StatusView.Idle ->
        match parsed.LastRun with
        | Some r ->
            let t = clock (r.StartedAt.ToLocalTime())

            let summary =
                match runSummary r with
                | "" -> ""
                | s -> $" {Glyph.sep} %s{s}"

            $"  %s{glyph} %s{padded} last: %s{UI.timing r.Elapsed} (%s{t})%s{summary}"
        | None -> $"  %s{glyph} %s{padded}"

let private renderSubtasks (now: DateTime) (subtasks: Subtask list) : string list =
    let last = List.length subtasks - 1

    subtasks
    |> List.mapi (fun i s ->
        let glyph = if i = last then "\u2514\u2500" else "\u251c\u2500"
        let elapsed = now - s.StartedAt
        let key = s.Key.PadRight(16)
        $"      %s{Color.dim}%s{glyph}%s{Color.reset} %s{key} %s{UI.timing elapsed} %s{Color.dim}%s{s.Label}%s{Color.reset}")

let private renderRecent (tail: string list) : string list =
    match tail with
    | [] -> []
    | xs ->
        $"      %s{Color.dim}recent:%s{Color.reset}"
        :: (xs |> List.map (fun l -> $"        %s{l}"))

let private renderVerbose
    (warningsAreFailures: bool)
    (now: DateTime)
    (name: string)
    (parsed: ParsedPluginStatus)
    : string list =
    let header = verboseHeader warningsAreFailures now name parsed

    let body =
        match parsed.Status with
        | StatusView.Running _ ->
            let subtaskLines = renderSubtasks now parsed.Subtasks
            let recent = renderRecent parsed.ActivityTail
            subtaskLines @ recent
        | StatusView.Failed(err, _) ->
            let startedLine =
                match parsed.LastRun with
                | Some r -> [ $"      %s{Color.dim}started: %s{clock (r.StartedAt.ToLocalTime())}%s{Color.reset}" ]
                | None -> []

            let errorLines =
                let lines =
                    err.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList

                $"      %s{Color.dim}error detail:%s{Color.reset}"
                :: (lines |> List.map (fun l -> $"        %s{l}"))

            let recent = renderRecent parsed.ActivityTail
            startedLine @ errorLines @ recent
        | StatusView.Completed _ ->
            let started =
                match parsed.LastRun with
                | Some r ->
                    let startedLine =
                        $"      %s{Color.dim}started: %s{clock (r.StartedAt.ToLocalTime())}%s{Color.reset}"

                    // The `elapsed:` line is ALWAYS present: a tool must never
                    // require its user to detect a fault by noticing what ISN'T
                    // printed. Zero elapsed is stated as what it is (a
                    // replayed/synthetic record), not omitted.
                    if r.Elapsed > TimeSpan.Zero then
                        [ startedLine
                          $"      %s{Color.dim}elapsed: %s{UI.timing r.Elapsed}%s{Color.reset}" ]
                    else
                        [ startedLine
                          $"      %s{Color.dim}elapsed: not measured (replayed or synthetic run record)%s{Color.reset}" ]
                | None -> []

            let summary =
                match parsed.LastRun |> Option.bind (fun r -> r.Summary) with
                | Some s -> [ $"      %s{Color.dim}summary: %s{s}%s{Color.reset}" ]
                | None -> []

            let recent = renderRecent parsed.ActivityTail
            started @ summary @ recent
        // The header already carries the reason; there is no body to show, because
        // there is nothing we could read.
        | StatusView.Unreadable _
        | StatusView.Idle -> []

    header :: body

// ----- Agent -----

/// Agent-mode rendering. Line-oriented, ANSI-free, parseable output with a
/// trailing `next:` hint.
module private Agent =
    let banner = "# fshw agent mode | cmds: check status format scan rerun"

    /// Terminal state for a plugin as seen by an agent consumer. `None` from
    /// `stateToken` means "omit this plugin from output" (idle with no history).
    ///
    /// This IS `Verdict.PluginOutcome`, not a parallel copy: the agent status line and
    /// `plugins[]` in `.fshw/verdict.json` are two renderings of ONE value, so they
    /// cannot drift about whether a plugin passed — or whether it is WEDGED.
    type State = Verdict.PluginOutcome

    let private tokenOf = Verdict.PluginOutcome.token

    /// Escape a summary for `summary="..."`: collapse newlines to spaces,
    /// escape embedded double quotes, truncate to 80 chars.
    let escapeSummary (s: string) : string =
        if String.IsNullOrEmpty s then
            ""
        else
            s.Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "\\\"").Trim()
            |> truncateTo80

    /// Determine the state for a plugin. Returns None when the plugin should be omitted
    /// (Idle with no lastRun). ONE implementation, shared with the verdict file — see
    /// `State` and `Verdict.pluginOutcomeOf` for the two fail-closed rules it carries.
    let stateToken (warningsAreFailures: bool) (now: DateTime) (parsed: ParsedPluginStatus) : State option =
        Verdict.pluginOutcomeOf warningsAreFailures now parsed

    /// Extract a summary string for non-ok states. None when there's nothing to show.
    let private summaryFor (parsed: ParsedPluginStatus) : string option =
        let nonEmpty s =
            if System.String.IsNullOrEmpty s then None else Some s

        let fromLastRun () =
            parsed.LastRun |> Option.map runSummary |> Option.bind nonEmpty

        match parsed.Status with
        | StatusView.Failed(err, _) ->
            parsed.LastRun
            |> Option.bind (fun r -> r.Summary)
            |> Option.bind nonEmpty
            |> Option.orElseWith (fun () -> nonEmpty err)
        // The reason IS the summary — an agent reading this line must be told WHY the
        // status could not be read, not handed a bare `fail` to guess at.
        | StatusView.Unreadable reason -> nonEmpty reason
        | StatusView.Running _ -> fromLastRun ()
        // Fail closed: the missing run record is stated in words, never
        // left as a bare token the consumer must decode.
        | StatusView.Completed _ when parsed.LastRun.IsNone ->
            DiagnosticCounts.summary parsed.Diagnostics
            |> nonEmpty
            |> Option.orElse (Some CompletedNoRecordText)
        | StatusView.Completed _
        | StatusView.Idle ->
            DiagnosticCounts.summary parsed.Diagnostics
            |> nonEmpty
            |> Option.orElseWith fromLastRun

    let private formatLineWith (now: DateTime) (state: State) (name: string) (parsed: ParsedPluginStatus) : string =
        match state with
        | State.Ok
        | State.Running -> $"%s{name}: %s{tokenOf state}"
        | State.Wedged ->
            // The wedge is stated in the same words as verbose/compact status
            // and the daemon log — no inference from a missing field.
            let body =
                match parsed.Status with
                | StatusView.Running since ->
                    let elapsed = if now > since then now - since else TimeSpan.Zero
                    wedgedBody since elapsed
                | _ -> "WEDGED"

            $"%s{name}: %s{tokenOf state} summary=\"%s{escapeSummary body}\""
        | State.TimedOut ->
            let summary =
                summaryFor parsed |> Option.map escapeSummary |> Option.defaultValue ""

            let display =
                if summary = "" then
                    "timed out"
                else
                    $"timed out: %s{summary}"

            $"%s{name}: %s{tokenOf state} summary=\"%s{display}\""
        | State.Fail
        | State.Warn ->
            match summaryFor parsed |> Option.map escapeSummary with
            | Some s when s <> "" -> $"%s{name}: %s{tokenOf state} summary=\"%s{s}\""
            | _ -> $"%s{name}: %s{tokenOf state}"

    /// Format one plugin line. None when the plugin should be omitted.
    let formatLine
        (warningsAreFailures: bool)
        (now: DateTime)
        (name: string)
        (parsed: ParsedPluginStatus)
        : string option =
        stateToken warningsAreFailures now parsed
        |> Option.map (fun s -> formatLineWith now s name parsed)

    /// Compute the `next:` line from a plugin-name → resolved-state map.
    /// Callers pass the same map used for per-plugin line rendering.
    let nextStep (warningsAreFailures: bool) (stateByName: Map<string, State>) (activeStates: Set<State>) : string =
        let isFail name =
            match Map.tryFind name stateByName with
            | Some State.Fail
            | Some State.TimedOut
            // A wedged plugin is a fault to inspect, not work to wait on.
            | Some State.Wedged -> true
            | _ -> false

        // The collapsed CLI: `check` re-runs every plugin and blocks until done;
        // `status` is the only observer. Point agents at `status <plugin>` to
        // inspect a specific failure without triggering a run, or `check` to
        // re-run everything and block on the result.
        if Set.contains State.Running activeStates then
            "next: fshw --agent check"
        else
            let priority = [ "build"; "test"; "lint"; "analyze"; "format-check"; "coverage" ]

            match priority |> List.tryFind isFail with
            | Some p -> $"next: fshw --agent status %s{p}"
            | None when Set.contains State.Wedged activeStates -> "next: fshw --agent status"
            | None when Set.contains State.Warn activeStates && warningsAreFailures -> "next: fshw --agent status"
            | None -> "next: done"

    /// Render full Agent-mode output: banner, per-plugin lines, next-step line.
    /// Computes each plugin's state once and reuses it for both the
    /// per-plugin line and the next-step priority scan.
    let render
        (warningsAreFailures: bool)
        (now: DateTime)
        (statuses: (string * ParsedPluginStatus) list)
        : string list =
        let folder (lines, stateByName, activeStates) (name, parsed) =
            match stateToken warningsAreFailures now parsed with
            | None -> lines, stateByName, activeStates
            | Some s -> formatLineWith now s name parsed :: lines, Map.add name s stateByName, Set.add s activeStates

        let revLines, stateByName, activeStates =
            statuses |> List.fold folder ([], Map.empty, Set.empty)

        let pluginLines = List.rev revLines

        [ banner ]
        @ pluginLines
        @ [ nextStep warningsAreFailures stateByName activeStates ]

/// Render a single plugin's status block. Returns one or more lines.
/// `warningsAreFailures` controls whether ledger warnings count as "completed-with-issues".
let renderPlugin
    (mode: RenderMode)
    (warningsAreFailures: bool)
    (now: DateTime)
    (name: string)
    (parsed: ParsedPluginStatus)
    : string list =
    match mode with
    | Compact -> renderCompact warningsAreFailures now name parsed
    | Verbose -> renderVerbose warningsAreFailures now name parsed
    | Agent ->
        match Agent.formatLine warningsAreFailures now name parsed with
        | Some line -> [ line ]
        | None -> []

// ----- Steering hints -----

/// Point the reader at the MACHINE-READABLE results, in the output they are already
/// reading, naming the ACTUAL paths for THIS run. A hint that merely tells you to go
/// and find the file gets ignored — consumers have grepped `total:` and `elapsed:` out
/// of this human progress display instead, and built merge decisions on it.
///
/// Printed when stdout is NOT a terminal — that is when a machine is reading. This is
/// PRESENTATION adapting to the caller, which is allowed; the verdict is byte-for-byte
/// identical either way, and no check is stricter for an agent than for a human.
module AgentHints =

    /// The steering block for a completed check/confirm, naming this run's files.
    let forVerdict (v: Verdict.Verdict) : string list =
        // NEVER print a path for a file that was not written: a hint that sends you to an
        // empty directory teaches distrust of the tool.
        let suiteLines =
            match v.Suites, v.RunId with
            | [], None ->
                [ "    suites   NO TEST RUN — nothing was verified by tests in this check (there is no run directory)" ]
            | [], Some id ->
                let dir = id.ToString("N")
                [ $"    suites   NONE — the run executed no tests (its directory .fshw/test-runs/%s{dir}/ is empty)" ]
            | suites, _ ->
                suites
                |> List.mapi (fun i s ->
                    let label = if i = 0 then "suites  " else "        "
                    $"    %s{label} %s{s.Ctrf}")

        // A run can be RED with every test project at `failed: 0` — the failure living in
        // `analyzers`, `format` or `build` instead. Printing only suites then shows a wall
        // of passing counts on a failing run, which reads as "the red is not mine".
        // Observed: a `confirm` returned exit 1 with six projects all green and three
        // analyzer findings; two people misattributed it before anyone opened `plugins`.
        let failingPluginLines =
            match v.Plugins |> List.filter (fun p -> Verdict.PluginOutcome.isFailing p.Outcome) with
            | [] -> []
            | failing ->
                failing
                |> List.mapi (fun i p ->
                    let label = if i = 0 then "FAILING " else "        "
                    let summary = p.Summary |> Option.defaultValue "(no summary)"
                    $"    %s{label} %s{p.Name} — %s{Verdict.PluginOutcome.token p.Outcome}: %s{summary}")

        // A red verdict that names nothing is worse than a red verdict: it looks like
        // a clean report that happens to be non-zero. If neither a plugin nor a suite
        // accounts for the failure, say so rather than printing a tidy block.
        let unexplainedRed =
            let isRed = v.ExitCode <> 0

            let anySuiteFailed = v.Suites |> List.exists (fun s -> s.Failed > 0)

            if isRed && List.isEmpty failingPluginLines && not anySuiteFailed then
                [ $"    UNEXPLAINED  exit %d{v.ExitCode} with no failing plugin and no failing suite — \
                     do NOT read this as a pass; open %s{Verdict.RelativePath}" ]
            else
                []

        let scopeAdvice =
            match v.Command, v.Scope with
            | Verdict.Check, ImpactFiltered(ran, total) ->
                [ $"  this check was impact-scoped (%d{ran}/%d{total} test projects) — for a MERGE verdict use \
                     `fshw confirm` (unfiltered; exit 3 if the scope was not earned)" ]
            | Verdict.Check, (NoTestsRun | ScopeUnknown | ScopeUnreadable _) ->
                [ $"  this check did not establish a full-suite scope (%s{TestScope.describe v.Scope}) — for a MERGE \
                     verdict use `fshw confirm` (unfiltered; exit 3 if the scope was not earned)" ]
            | Verdict.Check, FullSuite _
            | Verdict.Confirm, _ -> []

        [ "  AGENTS: don't parse this output. Machine-readable results:"
          $"    verdict  %s{Verdict.RelativePath}   (treeHash-keyed — `dotnet fshw verdict` re-checks it against the \
             tree on disk; exit 4 = stale, do not reuse)" ]
        @ failingPluginLines
        @ unexplainedRed
        @ suiteLines
        @ scopeAdvice

    /// The steering block for `fshw status`, which triggers no run and therefore
    /// has no verdict of its own — it points at whatever the last one left behind.
    let forStatus (repoRoot: string) : string list =
        let suites =
            Ctrf.latestRunReports repoRoot
            |> List.map (fun r -> System.IO.Path.GetRelativePath(repoRoot, r.Path).Replace('\\', '/'))

        let suiteLines =
            match suites with
            | [] -> [ "    suites   (none — no test run has produced a report yet)" ]
            | paths ->
                paths
                |> List.mapi (fun i p ->
                    let label = if i = 0 then "suites  " else "        "
                    $"    %s{label} %s{p}")

        [ "  AGENTS: don't parse this output. Machine-readable state:"
          $"    verdict  %s{Verdict.RelativePath}   (treeHash-keyed — `dotnet fshw verdict` says whether it still \
             applies; exit 4 = stale)" ]
        @ suiteLines
        @ [ "    NOTE: `status` triggers no run — the verdict above is from whichever check last ran." ]

/// Render all plugin statuses in the given mode. Callers join with newlines
/// and use the line count for cursor-up erase.
let renderAll
    (mode: RenderMode)
    (warningsAreFailures: bool)
    (now: DateTime)
    (statuses: Map<string, ParsedPluginStatus>)
    : string list =
    match mode with
    | Agent -> Agent.render warningsAreFailures now (Map.toList statuses)
    | Compact
    | Verbose ->
        statuses
        |> Map.toList
        |> List.collect (fun (name, parsed) -> renderPlugin mode warningsAreFailures now name parsed)
