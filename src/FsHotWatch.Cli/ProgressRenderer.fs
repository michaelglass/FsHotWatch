module FsHotWatch.Cli.ProgressRenderer

open System
open CommandTree
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

/// Wedge classification for a Running plugin (AUTOMATION-147) — the SAME
/// ambient bound and the SAME words as the daemon-side monitor, so `fshw
/// status` and the daemon log can never disagree about a wedge. Past the
/// bound a plugin is never rendered as merely "running": the fault is named.
let private runningWedge (now: DateTime) (since: DateTime) : FsHotWatch.PluginWedge.RunningHealth =
    FsHotWatch.PluginWedge.classifyRunning (FsHotWatch.PluginWedge.ambientBound ()) now since

/// The one wedged status line body (name-independent part), local-time clock
/// to match the renderer's other timestamps.
let private wedgedBody (since: DateTime) (elapsed: TimeSpan) : string =
    FsHotWatch.PluginWedge.wedgedText (since.ToLocalTime()) elapsed

/// The words shown for a Completed status that carries NO run record. A
/// content-free ✓ is exactly the AUTOMATION-99/147 trap (the operator had to
/// diagnose a wedge from a MISSING `elapsed:` field), so the renderer fails
/// closed: no record ⇒ no ✓, and the absence is stated in words.
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
        // Fail closed: Completed with NO run record must never render as a
        // bare ✓ — the absence is stated in words instead of left to be
        // noticed. (When the ledger has failing diagnostics the issue-count
        // path below is already honest, so it takes precedence.)
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
                // Past the bound a plugin is BY DEFINITION wedged — say so,
                // never render it as merely running (AUTOMATION-147).
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
            // Past the bound: named as wedged, never rendered as merely
            // running (AUTOMATION-147).
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

                    // The `elapsed:` line is ALWAYS present. A `started:` with
                    // no `elapsed:` was the home-made wedge detector the
                    // 2026-07-14 operator had to invent — a tool must never
                    // require its user to detect a fault by noticing what
                    // ISN'T printed (AUTOMATION-147). Zero elapsed is stated
                    // as what it is (a replayed/synthetic record), not omitted.
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
        | StatusView.Idle -> []

    header :: body

// ----- Agent -----

/// Agent-mode rendering. Line-oriented, ANSI-free, parseable output with a
/// trailing `next:` hint.
module private Agent =
    let banner = "# fshw agent mode | cmds: check status format scan rerun"

    /// Terminal state for a plugin as seen by an agent consumer. `None` from
    /// `stateToken` means "omit this plugin from output" (idle with no history).
    [<RequireQualifiedAccess>]
    type State =
        | Ok
        | Fail
        | TimedOut
        | Warn
        | Running
        /// Running past the wedge bound with no completion posted — by
        /// definition wedged (AUTOMATION-147). Its own token so an agent
        /// consumer can never read a wedge as mere "running".
        | Wedged

    let private tokenOf =
        function
        | State.Ok -> "ok"
        | State.Fail -> "fail"
        | State.TimedOut -> "timed-out"
        | State.Warn -> "warn"
        | State.Running -> "running"
        | State.Wedged -> "wedged"

    /// Escape a summary for `summary="..."`: collapse newlines to spaces,
    /// escape embedded double quotes, truncate to 80 chars.
    let escapeSummary (s: string) : string =
        if String.IsNullOrEmpty s then
            ""
        else
            s.Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "\\\"").Trim()
            |> truncateTo80

    /// Determine the state for a plugin. Returns None when the plugin
    /// should be omitted (Idle with no lastRun).
    let stateToken (warningsAreFailures: bool) (now: DateTime) (parsed: ParsedPluginStatus) : State option =
        let okOrDiag () =
            if DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics then
                if parsed.Diagnostics.Errors > 0 then
                    State.Fail
                else
                    State.Warn
            else
                State.Ok

        let timedOutLastRun () =
            match parsed.LastRun with
            | Some r ->
                match r.Outcome with
                | TimedOut _ -> true
                | _ -> false
            | None -> false

        match parsed.Status with
        | StatusView.Running since ->
            match runningWedge now since with
            | FsHotWatch.PluginWedge.RunningHealth.Wedged _ -> Some State.Wedged
            | FsHotWatch.PluginWedge.RunningHealth.StillRunning _ -> Some State.Running
        | StatusView.Failed _ when timedOutLastRun () -> Some State.TimedOut
        | StatusView.Failed _ -> Some State.Fail
        // Fail closed: Completed with no run record can never token as "ok"
        // (the agent-mode ✓) — see CompletedNoRecordText. Failing diagnostics
        // still take precedence (fail/warn); a clean ledger downgrades to warn.
        | StatusView.Completed _ when parsed.LastRun.IsNone ->
            match okOrDiag () with
            | State.Ok -> Some State.Warn
            | other -> Some other
        | StatusView.Completed _ -> Some(okOrDiag ())
        | StatusView.Idle ->
            parsed.LastRun
            |> Option.map (fun r ->
                match r.Outcome with
                | FailedRun _ -> State.Fail
                | TimedOut _ -> State.TimedOut
                | CompletedRun -> okOrDiag ())

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

        // The collapsed CLI: `check` is the only gate (it re-runs every plugin
        // and blocks until done), `status` is the only observer. Point agents at
        // `status <plugin>` to inspect a specific failure without triggering a
        // run, or `check` to re-run the whole gate and block on the result.
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
