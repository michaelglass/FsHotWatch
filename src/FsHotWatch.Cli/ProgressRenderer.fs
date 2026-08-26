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

/// Shorten to the 80-character budget this fixed-width surface has, NAMING what was
/// dropped (AUTOMATION-201).
///
/// A bare `"..."` is indistinguishable from prose. The reported symptom was a status
/// line reading `4 waiting on build (tests did not run): Intelligence.Build.Dev.Tests,
/// Intelli...` — a reader cannot tell a shortened list from a complete one, so the
/// natural conclusion is that they have seen the whole story. Stating the omitted
/// count makes that impossible; the untruncated text is always in the ledger entry
/// and the log, which is where the remedy lives too.
let private truncateTo80 (s: string) : string =
    if s.Length <= 80 then
        s
    else
        // The marker's own length is computed from the WORST case (dropping the whole
        // string), so its digit count can only shrink and the result is never longer
        // than the 80 the contract promises.
        let marker (omitted: int) = $"… (+%d{omitted} more)"
        let keep = max 0 (80 - (marker s.Length).Length)
        s.Substring(0, keep) + marker (s.Length - keep)

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

/// The glyph a terminal status earns. ONE decider, shared by compact and verbose —
/// they are two renderings of one judgement, and every fail-closed rule (a ledger
/// with failing diagnostics; a run that VERIFIED NOTHING, AUTOMATION-198) has to hold
/// in both or it does not hold. Deciding it twice is how they drift.
///
/// `Completed` with no run record at all is handled by the callers, which have a
/// sentence to print about it as well as a glyph to pick.
let private glyphForParsed (warningsAreFailures: bool) (parsed: ParsedPluginStatus) =
    match parsed.Status with
    | StatusView.Completed _ when
        DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics
        || ParsedPluginStatus.verifiedNothing parsed
        ->
        Glyph.warn
    | StatusView.Completed _ -> Glyph.check
    | StatusView.Failed _ when isTimedOut parsed -> Glyph.timeout
    | StatusView.Failed _ -> Glyph.cross
    | StatusView.Unreadable _ -> Glyph.cross
    | StatusView.Running _ -> Glyph.ellipsis
    | StatusView.Idle -> Glyph.idle

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

            // The glyph comes from `glyphForParsed`, the shared decider — including the
            // AUTOMATION-198 rule that a run which VERIFIED NOTHING is not a ✓. The
            // status stays `Completed` (nothing failed), so the glyph is what has to
            // stop reading as success; the words are already in the summary.
            $"  %s{glyphForParsed warningsAreFailures parsed} %s{padded}%s{timingPart}%s{summary}"
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

    /// Escape a summary for `summary="..."`: collapse newlines to spaces and escape
    /// embedded double quotes. NOT shortened.
    ///
    /// The 80-character budget belongs to the CALLER that redraws, not to the message.
    /// Compact/Verbose are erased and rewritten in place — `IpcOutput` counts the lines
    /// it printed and emits that many `\x1b[A`, so a summary wide enough to WRAP makes
    /// the erase count wrong and smears the block. Agent mode is erased by nothing: the
    /// redraw is guarded by `UI.isInteractive`, and agent mode is what a non-interactive
    /// caller gets. It is line-oriented parseable output, one line per plugin, ANSI-free.
    ///
    /// So the cap here was a fixed-width constraint copied onto a surface that has no
    /// width — and it cost the machine reader the thing it most needed: the reported
    /// symptom was `4 waiting on build (tests did not run): Intelligence.Build.Dev.Tests,
    /// Intelli…`, a list cut off mid-name (AUTOMATION-201, AC2 "names every affected
    /// project (no truncation)"). Newlines still collapse, so this stays ONE line.
    let escapeSummary (s: string) : string =
        if String.IsNullOrEmpty s then
            ""
        else
            s.Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "\\\"").Trim()

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

    /// When THIS run executed no tests, the reader's next question is always the
    /// same one: was this tree ever verified at all? Answering with only "no tests
    /// ran" conflates NOTHING WAS VERIFIED with NOTHING NEEDED RE-VERIFYING, and
    /// leaves no way to tell which you got. `TestScope.NoTestsRun` says as much in
    /// its own doc — "No test run has completed, OR the run executed no tests at
    /// all" — and that `or` is the whole defect. It is the same conflation
    /// AUTOMATION-150 already split apart for `ScopeUnknown`/`ScopeUnreadable`:
    /// different facts are different values, and a report that merges them turns a
    /// reader against the tool. Observed: an agent read the bare line, concluded it
    /// had no evidence, and spent an afternoon hunting a phantom test-selection bug
    /// while the passing run for that exact tree sat in `verdict.json`.
    ///
    /// So name the prior verdict — but ONLY when it genuinely applies:
    ///   * same `treeHash`, or it is not about this code; and
    ///   * it ran at least one suite, or it answers "when was this verified?" with
    ///     a dressed-up "it wasn't".
    /// Anything else prints nothing, per this module's standing rule that a hint
    /// pointing somewhere useless teaches distrust — and stale evidence presented
    /// as current would be the worse version of that.
    ///
    /// This is CONTEXT ON A REFUSAL, never a softening of one. The verdict line is
    /// untouched, the exit code is untouched, and `NoTestsRun` remains outside
    /// `TestScope.isEvidence`.
    let private priorEvidenceLines (prior: Verdict.Verdict option) (v: Verdict.Verdict) : string list =
        match prior with
        | Some p when p.TreeHash = v.TreeHash && not (List.isEmpty p.Suites) ->
            let total = p.Suites |> List.sumBy (fun s -> s.Total)
            let failed = p.Suites |> List.sumBy (fun s -> s.Failed)
            let projects = p.Suites |> List.map (fun s -> s.Project) |> String.concat ", "
            let at = p.ProducedAt.ToUniversalTime().ToString("u")

            // The CHANGE that selected those tests. This is the half of the answer
            // that a reader actually acts on: "unchanged since 11:53" tells them
            // when, but not what — and without the what, they cannot judge whether
            // the run that happened was the run their edit deserved.
            //
            // Silent when empty: an unfiltered run has no trigger (nothing selected
            // it), and a daemon older than the field sends none. Printing "triggered
            // by nothing" for either would invent a fact.
            let triggerLines =
                match p.Trigger with
                | [] -> []
                | shown ->
                    let listed = String.concat ", " shown
                    let hidden = p.TriggerCount - List.length shown

                    let suffix = if hidden > 0 then $" (and %d{hidden} more)" else ""

                    [ $"             triggered by %s{listed}%s{suffix}" ]

            [ $"             tree unchanged since the verdict at %s{at} — %s{TestScope.describe p.Scope}" ]
            @ triggerLines
            @ [ $"             which ran %d{total} test(s) across %s{projects} (%d{failed} failed)" ]
        | _ -> []

    /// The steering block for a completed check/confirm, naming this run's files.
    ///
    /// `prior` is the verdict that was on disk BEFORE this run overwrote it. It has
    /// to be an argument: the caller writes the new verdict before rendering, so by
    /// the time this runs the file is already gone. Passing it in keeps this
    /// function pure and makes that ordering impossible to get silently wrong.
    let forVerdict (prior: Verdict.Verdict option) (v: Verdict.Verdict) : string list =
        // NEVER print a path for a file that was not written: a hint that sends you to an
        // empty directory teaches distrust of the tool.
        //
        // Two DIFFERENT kinds of line have always hidden behind one name here, and they
        // belong in different halves of the block: reports that exist are PATHS (machine
        // fodder, filed under the verdict pointer), while "no tests ran" is a FACT about
        // what this run verified — the single most actionable line the output has when it
        // is true, and therefore something a human must meet before any pointer.
        let zeroSelectionLines =
            match v.Scope with
            | NoTestsRun(NoTestsReason.ChangesUncovered(symbols, total)) ->
                let listed = String.concat ", " symbols
                let more = total - List.length symbols
                let suffix = if more > 0 then $" (and {more} more)" else ""

                [ $"    ZERO     ⚠ NOTHING COVERS THIS CHANGE — %d{total} changed symbol(s) have no covering test."
                  "             This run verified nothing about them and completed green anyway. The index cannot see"
                  "             reflection, DI, or generated code, so this is a NON-ANSWER, not a pass."
                  $"             uncovered: %s{listed}%s{suffix}" ]
            | NoTestsRun NoTestsReason.AlreadyVerified ->
                [ "             this tree is test-equivalent to the last green run — nothing needed re-verifying;"
                  "             this is a legitimate zero rather than a missed selection" ]
            | NoTestsRun NoTestsReason.Unstated ->
                [ "             the daemon did not say WHICH kind of zero this is, so it may be read as neither"
                  "             `nothing needed re-verifying` nor `nothing covers this change`" ]
            | NoTestsRun(NoTestsReason.UnknownReason token) ->
                [ $"             the daemon called this zero '%s{token}', which this build does not understand" ]
            | _ -> []

        let suitePathLines, noSuiteFactLines =
            match v.Suites, v.RunId with
            | [], None ->
                [],
                "    suites   NO TEST RUN — nothing was verified by tests in this check (there is no run directory)"
                :: priorEvidenceLines prior v
            | [], Some id ->
                let dir = id.ToString("N")

                [],
                $"    suites   NONE — the run executed no tests (its directory .fshw/test-runs/%s{dir}/ is empty)"
                :: priorEvidenceLines prior v
            | suites, _ ->
                suites
                |> List.mapi (fun i s ->
                    let label = if i = 0 then "suites  " else "        "
                    $"    %s{label} %s{s.Ctrf}"),
                []

        // AUTOMATION-111 — a RECALL MISS must SHOUT, in the output already being read.
        //
        // `Divergence.CheckMissedFailures` means the impact-scoped run was GREEN and the
        // full suite was RED: the selector did not choose a test that fails. It has been
        // computed and written to `verdict.json` since AUTOMATION-259 — and rendered
        // NOWHERE. That is the exact failure this ticket names: a fact filed in a document
        // you must remember to open is not a safeguard, and the moment it is worth
        // anything is the moment the person is looking at the output.
        //
        // Without this line the miss is indistinguishable from an ordinary test failure,
        // so it gets FIXED as one — the test is repaired, the selector's blind spot is
        // never seen, and the same class of green-that-lied ships again. TestPrune's own
        // source calls under-selection "the one failure mode a test-impact tool must not
        // have"; this is the line that says it happened.
        let recallMissLines =
            match v.Divergence with
            | Verdict.Divergence.CheckMissedFailures ->
                [ "    RECALL   ⚠ SELECTION BUG — the impact-scoped run was GREEN over a tree the full suite finds RED."
                  "             `check` told someone this change was fine and it was not. This is an fshw defect,"
                  "             NOT a merge saved: fix the selector, not only the test. See checkComparison in"
                  "             .fshw/verdict.json for the scoped run's own scope and failing suites." ]
            | Verdict.Divergence.Agreed
            | Verdict.Divergence.CheckOnlyFailures
            | Verdict.Divergence.NoImpactScopedRun
            | Verdict.Divergence.Incomparable _
            | Verdict.Divergence.NotRecorded -> []

        let conditionalFailureRecallLines =
            match v.ConditionalFailureRecall with
            | Some(IpcParsing.FailureRecallMeasured(reached, total, threshold, acceptable)) ->
                let percent = 100.0 * float reached / float total
                let disposition = if acceptable then "acceptable" else "BELOW THRESHOLD"

                [ $"    RECALL   conditional failing-test recall %d{reached}/%d{total} (%0.1f{percent}%%); threshold %0.0f{threshold * 100.0}%% — %s{disposition}" ]
            | Some(IpcParsing.FailureRecallNotMeasurable reason) ->
                [ $"    RECALL   conditional failing-test recall not measurable — %s{reason}" ]
            | None -> []

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

                    // NEVER shortened. The 80-column cap on the `✗` status line is a real
                    // constraint (that surface is erased and redrawn by line count, so a
                    // wrapping line smears the block) — but it cost a reader the cause
                    // twice: the line ended `… (+20 more)` and the omitted part was the
                    // whole answer. This surface has no width budget, so a capped
                    // one-liner is only ever allowed to coexist with an uncapped copy,
                    // and this is that copy. Newlines collapse; nothing else is dropped.
                    let summary =
                        p.Summary
                        |> Option.map (fun s -> s.Replace('\r', ' ').Replace('\n', ' ').Trim())
                        |> Option.filter (fun s -> s <> "")
                        |> Option.defaultValue "(the plugin reported no reason — see the daemon log)"

                    $"    %s{label} %s{p.Name} — %s{Verdict.PluginOutcome.token p.Outcome}: %s{summary}")

        // A red verdict that names nothing is worse than a red verdict: it looks like
        // a clean report that happens to be non-zero. If neither a plugin nor a suite
        // accounts for the failure, say so rather than printing a tidy block.
        // AUTOMATION-303. The red that reddened a `confirm` with all four plugins `ok`
        // and 9,064 tests passed lived in the ledger under `fcs` — a SOURCE that is not a
        // plugin and so has no line above. Naming it here (and in `reddenedBy` in the
        // file) is what turns "unexplained" into an answer.
        let ledgerCauseLines =
            match v.RedCauses with
            | [] -> []
            | causes ->
                let shown =
                    causes
                    |> List.mapi (fun i (c: Verdict.RedCause) ->
                        let label = if i = 0 then "REDDENED" else "        "
                        let msg = c.Message.Replace('\r', ' ').Replace('\n', ' ').Trim()

                        // AUTOMATION-303. The kind is printed only when it is NOT
                        // `AboutThisTree`, so the ordinary red keeps its ordinary line and
                        // the exceptional one is visibly exceptional. Naming the kind on
                        // every line would make the distinction furniture.
                        let mark =
                            match c.Kind with
                            | Verdict.AboutThisTree -> ""
                            | Verdict.VanishedFile -> "  [NOT-THIS-TREE: file is not on disk]"
                            | Verdict.CheckerFault -> "  [NOT-THIS-TREE: the checker crashed; it found nothing]"

                        $"    %s{label} %s{c.Source}:%s{c.File}: %s{c.Severity} %s{msg}%s{mark}")

                let more = v.RedCauseCount - List.length causes

                let truncation =
                    if more > 0 then
                        [ $"             … and %d{more} more (see `reddenedBy` in %s{Verdict.RelativePath})" ]
                    else
                        []

                // AUTOMATION-303 AC5. The mixed case: SOME causes are stale and some are
                // real, so the exit code stays a red (a real defect outranks the noise)
                // and nothing above would tell the reader that a chunk of the wall in
                // front of them is not theirs. This is the line that saves the cycle —
                // the 2026-08-12 incident was ~51 checker faults beside 3 diagnostics
                // that looked real, and separating them by hand took the rest of the
                // evening.
                let staleAdvice =
                    match Verdict.RedCause.unattributable causes with
                    | [] -> []
                    | stale ->
                        [ $"             %d{List.length stale} of the cause(s) above are NOT about this tree — stale \
                             daemon state. Run `fshw stop`, then re-run; `fshw scan` does NOT clear it." ]

                shown @ truncation @ staleAdvice

        let unexplainedRed =
            let isRed = v.ExitCode <> 0

            let anySuiteFailed = v.Suites |> List.exists (fun s -> s.Failed > 0)

            if
                isRed
                && List.isEmpty failingPluginLines
                && not anySuiteFailed
                && List.isEmpty ledgerCauseLines
            then
                [ $"    UNEXPLAINED  exit %d{v.ExitCode} with no failing plugin, no failing suite and no failing \
                     diagnostic — do NOT read this as a pass; open %s{Verdict.RelativePath}" ]
            else
                []

        let scopeAdvice =
            match v.Command, v.Scope with
            | Verdict.Check, ImpactFiltered(ran, total) ->
                [ $"  this check was impact-scoped (%d{ran}/%d{total} test projects) — for a MERGE verdict use \
                     `fshw confirm` (unfiltered; exit 3 if the scope was not earned)" ]
            | Verdict.Check, (NoTestsRun _ | ScopeUnknown | ScopeUnreadable _) ->
                [ $"  this check did not establish a full-suite scope (%s{TestScope.describe v.Scope}) — for a MERGE \
                     verdict use `fshw confirm` (unfiltered; exit 3 if the scope was not earned)" ]
            | Verdict.Check, FullSuite _
            | Verdict.Confirm, _ -> []

        // ORDER IS THE FIX (AUTOMATION-198 follow-up). Every one of these lines already
        // existed; they were printed UNDER a header reading "AGENTS: don't parse this
        // output", which was meant as "don't screen-scrape, read the JSON" and landed as
        // "this section is not for you". A reader who obeyed it skipped the only place
        // the failures are enumerated and misdiagnosed the same failing run twice —
        // first as "check is flaky", then as "the selector is blind" — while the exact
        // file, the exact numbers and the next command sat on screen.
        //
        // So the causes come FIRST, under a header that says to read them, and the
        // machine-readable pointer follows. The pointer is not weakened: `verdict.json`
        // is still the authority and still named, with the same words about staleness.
        let causeLines = failingPluginLines @ ledgerCauseLines @ unexplainedRed

        let causeHeader =
            match causeLines with
            | [] -> []
            | _ ->
                // Deliberately NOT "why this run is RED": `failingPluginLines` is driven by
                // `PluginOutcome.isFailing`, which is a fact about a plugin, not about the
                // exit code. A header that claimed red on a run that exited 0 would be the
                // same class of lie this block exists to stop.
                [ "  WHAT FAILED — the causes in full; nothing below this is needed to act on them:" ]

        // AUTOMATION-111: FIRST, ahead of the cause header. A recall miss is a fact about
        // the TOOL, not about this change, and a reader who meets it after a wall of test
        // causes has already started debugging the wrong thing.
        recallMissLines
        @ conditionalFailureRecallLines
        @ causeHeader
        @ causeLines
        @ noSuiteFactLines
        @ zeroSelectionLines
        @ [ "  AGENTS: READ the above — just don't SCREEN-SCRAPE it. The same facts, machine-readable:"
            $"    verdict  %s{Verdict.RelativePath}   (treeHash-keyed — `dotnet fshw verdict` re-checks it against \
               the tree on disk; exit 4 = stale, do not reuse)" ]
        @ suitePathLines
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

        [ "  AGENTS: READ the above — just don't SCREEN-SCRAPE it. The same facts, machine-readable:"
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
