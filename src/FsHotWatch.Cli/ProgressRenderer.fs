module FsHotWatch.Cli.ProgressRenderer

open System
open CommandTree
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
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

let private latestActivity (tail: string list) =
    match tail |> List.tryLast with
    | Some s -> s
    | None -> ""

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
        | Completed _ ->
            let elapsed =
                match parsed.LastRun with
                | Some r -> r.Elapsed
                | None -> TimeSpan.Zero

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

            let timingStr = UI.timing elapsed

            let glyph = if withIssues then Glyph.warn else Glyph.check

            $"  %s{glyph} %s{padded} %s{timingStr}%s{summary}"
        | Failed(err, _) ->
            let short = summariseError err

            let elapsed =
                match parsed.LastRun with
                | Some r -> r.Elapsed
                | None -> TimeSpan.Zero

            let timingStr = UI.timing elapsed
            $"  %s{Glyph.cross} %s{padded} %s{timingStr} %s{Color.dim}{Glyph.sep} %s{short}%s{Color.reset}"
        | Running since ->
            let elapsed = now - since
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
                    let n = List.length xs
                    let names = xs |> List.map (fun s -> s.Key) |> String.concat ", "
                    $" %s{Color.dim}{Glyph.sep} %d{n} running: %s{names}%s{Color.reset}"

            $"  %s{Glyph.ellipsis} %s{padded} %s{timingStr}%s{detail}"
        | Idle ->
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
    | Completed _ when DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics -> Glyph.warn
    | Completed _ -> Glyph.check
    | Failed _ -> Glyph.cross
    | Running _ -> Glyph.ellipsis
    | Idle -> Glyph.idle

let private verboseHeader
    (warningsAreFailures: bool)
    (now: DateTime)
    (name: string)
    (parsed: ParsedPluginStatus)
    : string =
    let padded = padName name
    let glyph = glyphForParsed warningsAreFailures parsed

    match parsed.Status with
    | Running since ->
        let elapsed = now - since
        let n = List.length parsed.Subtasks

        let detail =
            if n > 0 then
                $" %s{Color.dim}{Glyph.sep} %d{n} running%s{Color.reset}"
            else
                ""

        $"  %s{glyph} %s{padded} %s{UI.timing elapsed}%s{detail}"
    | Completed _ ->
        let elapsed =
            match parsed.LastRun with
            | Some r -> r.Elapsed
            | None -> TimeSpan.Zero

        let summary =
            match parsed.LastRun |> Option.map runSummary with
            | Some s when s <> "" -> $" %s{Color.dim}{Glyph.sep} %s{s}%s{Color.reset}"
            | _ -> ""

        $"  %s{glyph} %s{padded} %s{UI.timing elapsed}%s{summary}"
    | Failed(err, _) ->
        let elapsed =
            match parsed.LastRun with
            | Some r -> r.Elapsed
            | None -> TimeSpan.Zero

        $"  %s{glyph} %s{padded} %s{UI.timing elapsed} %s{Color.dim}{Glyph.sep} %s{summariseError err}%s{Color.reset}"
    | Idle ->
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
        | Running _ ->
            let subtaskLines = renderSubtasks now parsed.Subtasks
            let recent = renderRecent parsed.ActivityTail
            subtaskLines @ recent
        | Failed(err, _) ->
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
        | Completed _ ->
            let started =
                match parsed.LastRun with
                | Some r ->
                    [ $"      %s{Color.dim}started: %s{clock (r.StartedAt.ToLocalTime())}%s{Color.reset}"
                      $"      %s{Color.dim}elapsed: %s{UI.timing r.Elapsed}%s{Color.reset}" ]
                | None -> []

            let summary =
                match parsed.LastRun |> Option.bind (fun r -> r.Summary) with
                | Some s -> [ $"      %s{Color.dim}summary: %s{s}%s{Color.reset}" ]
                | None -> []

            let recent = renderRecent parsed.ActivityTail
            started @ summary @ recent
        | Idle -> []

    header :: body

// ----- Agent -----

/// Agent-mode rendering. Line-oriented, ANSI-free, parseable output with a
/// trailing `next:` hint.
module private Agent =
    let banner =
        "# fs-hot-watch agent mode | cmds: check build test lint analyze format format-check errors status"

    /// Terminal state for a plugin as seen by an agent consumer. `None` from
    /// `stateToken` means "omit this plugin from output" (idle with no history).
    type State =
        | SOk
        | SFail
        | SWarn
        | SRunning

    let private tokenOf =
        function
        | SOk -> "ok"
        | SFail -> "fail"
        | SWarn -> "warn"
        | SRunning -> "running"

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
    let stateToken (warningsAreFailures: bool) (parsed: ParsedPluginStatus) : State option =
        let okOrDiag () =
            if DiagnosticCounts.isFailing warningsAreFailures parsed.Diagnostics then
                if parsed.Diagnostics.Errors > 0 then SFail else SWarn
            else
                SOk

        match parsed.Status with
        | Running _ -> Some SRunning
        | Failed _ -> Some SFail
        | Completed _ -> Some(okOrDiag ())
        | Idle ->
            parsed.LastRun
            |> Option.map (fun r ->
                match r.Outcome with
                | FailedRun _ -> SFail
                | CompletedRun -> okOrDiag ())

    /// Extract a summary string for non-ok states. None when there's nothing to show.
    let private summaryFor (parsed: ParsedPluginStatus) : string option =
        let nonEmpty s =
            if System.String.IsNullOrEmpty s then None else Some s

        let fromLastRun () =
            parsed.LastRun |> Option.map runSummary |> Option.bind nonEmpty

        match parsed.Status with
        | Failed(err, _) ->
            parsed.LastRun
            |> Option.bind (fun r -> r.Summary)
            |> Option.bind nonEmpty
            |> Option.orElseWith (fun () -> nonEmpty err)
        | Running _ -> fromLastRun ()
        | Completed _
        | Idle ->
            DiagnosticCounts.summary parsed.Diagnostics
            |> nonEmpty
            |> Option.orElseWith fromLastRun

    let private formatLineWith (state: State) (name: string) (parsed: ParsedPluginStatus) : string =
        match state with
        | SOk
        | SRunning -> $"%s{name}: %s{tokenOf state}"
        | SFail
        | SWarn ->
            match summaryFor parsed |> Option.map escapeSummary with
            | Some s when s <> "" -> $"%s{name}: %s{tokenOf state} summary=\"%s{s}\""
            | _ -> $"%s{name}: %s{tokenOf state}"

    /// Format one plugin line. None when the plugin should be omitted.
    let formatLine (warningsAreFailures: bool) (name: string) (parsed: ParsedPluginStatus) : string option =
        stateToken warningsAreFailures parsed
        |> Option.map (fun s -> formatLineWith s name parsed)

    /// Compute the `next:` line from pre-resolved plugin state tokens.
    let nextStep (warningsAreFailures: bool) (tokens: (string * State option) list) : string =
        let tokenMap = tokens |> Map.ofList

        let isState state name =
            Map.tryFind name tokenMap
            |> Option.bind id
            |> Option.exists (fun s -> s = state)

        let anyState state =
            tokens |> List.exists (fun (_, t) -> t = Some state)

        if anyState SRunning then
            "next: fs-hot-watch --agent errors --wait"
        elif isState SFail "build" then
            "next: fs-hot-watch --agent build"
        elif isState SFail "test" then
            "next: fs-hot-watch --agent test"
        else
            let priority = [ "lint"; "analyze"; "format-check"; "coverage" ]

            match priority |> List.tryFind (isState SFail) with
            | Some p -> $"next: fs-hot-watch --agent %s{p}"
            | None when anyState SWarn && warningsAreFailures -> "next: fs-hot-watch --agent errors"
            | None -> "next: done"

    /// Render full Agent-mode output: banner, per-plugin lines, next-step line.
    /// Computes each plugin's state once and reuses it for both the
    /// per-plugin line and the next-step priority scan.
    let render (warningsAreFailures: bool) (statuses: (string * ParsedPluginStatus) list) : string list =
        let resolved =
            statuses |> List.map (fun (n, p) -> n, stateToken warningsAreFailures p, p)

        let pluginLines =
            resolved
            |> List.choose (fun (n, tok, p) -> tok |> Option.map (fun s -> formatLineWith s n p))

        let tokens = resolved |> List.map (fun (n, tok, _) -> n, tok)

        [ banner ] @ pluginLines @ [ nextStep warningsAreFailures tokens ]

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
        match Agent.formatLine warningsAreFailures name parsed with
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
    | Agent -> Agent.render warningsAreFailures (Map.toList statuses)
    | Compact
    | Verbose ->
        statuses
        |> Map.toList
        |> List.collect (fun (name, parsed) -> renderPlugin mode warningsAreFailures now name parsed)
