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

/// Truncate a potentially multi-line error to its first non-empty line, then
/// shorten to roughly 80 printable characters.
let private summariseError (error: string) : string =
    if String.IsNullOrEmpty error then
        ""
    else
        let firstLine =
            error.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue ""

        if firstLine.Length <= 80 then
            firstLine
        else
            firstLine.Substring(0, 77) + "..."

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

/// Render all plugin statuses in the given mode. Callers join with newlines
/// and use the line count for cursor-up erase.
let renderAll
    (mode: RenderMode)
    (warningsAreFailures: bool)
    (now: DateTime)
    (statuses: Map<string, ParsedPluginStatus>)
    : string list =
    statuses
    |> Map.toList
    |> List.collect (fun (name, parsed) -> renderPlugin mode warningsAreFailures now name parsed)
