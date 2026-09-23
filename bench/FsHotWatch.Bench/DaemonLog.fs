/// Windowing `logs/daemon.log` so a count taken from it describes ONE daemon and ONE
/// cycle.
///
/// The log appends across restarts, and a single daemon run holds several scan, build
/// and test cycles (a warm-up test pass followed by a dependency-fanout re-run is
/// normal). A count taken from the whole file, or from the whole of one daemon's run,
/// mixes runs. Two cuts make it honest:
///
/// 1. `sinceLastStart` keeps only the lines from the LAST `[config] … verdictInputs:`
///    line — the first line every daemon start writes — onward.
/// 2. `cycles` splits that window into start/end-delimited cycles; a consumer reads the
///    last COMPLETE one.
///
/// Timestamps are accepted in both shapes the daemon has written: `hh:mm:ss.fff` (older
/// builds) and ISO-8601 UTC `yyyy-MM-ddTHH:mm:ss.fffZ` (current).
///
/// Lines echoed from a nested daemon (an integration test's own daemon output, which the
/// test runner relays as `[tag] hh:mm:ss.fff   |   [tag] …`) are never cycle markers.
module FsHotWatch.Bench.DaemonLog

open System
open System.Text.RegularExpressions

let private topLevel =
    Regex(@"^\s*\[(?<tag>[a-z-]+)\] (\d{4}-\d\d-\d\dT)?\d\d:\d\d:\d\d\.\d+Z? (?<body>.*)$", RegexOptions.Compiled)

/// A line's `[tag]` and message body when it is a top-level (not relayed) log line.
let tryTagged (line: string) : (string * string) option =
    let m = topLevel.Match(line)

    if not m.Success then
        None
    else
        let body = m.Groups.["body"].Value
        // A relayed line's body starts with the relay gutter `  |   [tag] …`.
        if body.TrimStart().StartsWith("|", StringComparison.Ordinal) then
            None
        else
            Some(m.Groups.["tag"].Value, body)

let private isDaemonStart (line: string) =
    match tryTagged line with
    | Some("config", body) -> body.StartsWith("verdictInputs:", StringComparison.Ordinal)
    | _ -> false

/// The lines of the most recent daemon start onward. Empty when the log records no start.
let sinceLastStart (lines: string list) : string list =
    let indexed = lines |> List.indexed

    match indexed |> List.filter (snd >> isDaemonStart) |> List.tryLast with
    | None -> []
    | Some(start, _) -> lines |> List.skip start

let private startedPid =
    Regex(@"^Starting FsHotWatch daemon for .* pid=(?<pid>\d+)", RegexOptions.Compiled)

/// The pid the window's daemon announced (`Starting FsHotWatch daemon for … pid=N`), so
/// a caller can prove the window belongs to the process it measured.
let announcedPid (window: string list) : int option =
    window
    |> List.tryPick (fun line ->
        let m = startedPid.Match(line)
        if m.Success then Some(int m.Groups.["pid"].Value) else None)

/// One start/end-delimited span. `EndLine = None` when the span never finished (the
/// daemon stopped, or a new start pre-empted it).
type Cycle =
    { StartLine: string
      EndLine: string option
      Lines: string list }

/// Split `window` into cycles opened by `isStart` and closed by `isEnd`. A second start
/// before an end closes the open cycle as unfinished; an end with no open cycle is
/// ignored.
let cycles (isStart: string -> bool) (isEnd: string -> bool) (window: string list) : Cycle list =
    let close (start: string, body: string list) (finish: string option) =
        { StartLine = start
          EndLine = finish
          Lines = List.rev body }

    let folder (openCycle: (string * string list) option, done': Cycle list) (line: string) =
        match openCycle with
        | None when isStart line -> Some(line, [ line ]), done'
        | None -> None, done'
        | Some current when isStart line -> Some(line, [ line ]), close current None :: done'
        | Some(start, body) when isEnd line -> None, close (start, line :: body) (Some line) :: done'
        | Some(start, body) -> Some(start, line :: body), done'

    let openCycle, finished = window |> List.fold folder (None, [])

    let all =
        match openCycle with
        | Some current -> close current None :: finished
        | None -> finished

    List.rev all

/// The last cycle that finished, if any.
let lastComplete (all: Cycle list) : Cycle option =
    all |> List.filter (fun c -> c.EndLine.IsSome) |> List.tryLast

let private tagged (tag: string) (prefixOrContains: string -> bool) (line: string) =
    match tryTagged line with
    | Some(t, body) when t = tag -> prefixOrContains body
    | _ -> false

let private scanRegistered =
    Regex(@"^(?<projects>\d+) projects, (?<files>\d+) files registered", RegexOptions.Compiled)

let private scanChecked =
    Regex(
        @"^Checked (?<checked>\d+) files \((?<tiers>\d+) tiers\), skipped (?<skipped>\d+), unchecked (?<unchecked>\d+)",
        RegexOptions.Compiled
    )

/// A scan cycle opens on `N projects, M files registered` and closes on
/// `Checked N files (T tiers), skipped S, unchecked U`.
let scanCycles (window: string list) : Cycle list =
    cycles (tagged "scan" (fun b -> scanRegistered.IsMatch b)) (tagged "scan" (fun b -> scanChecked.IsMatch b)) window

/// Counts one scan cycle reported.
type ScanCounts =
    { Projects: int
      Registered: int
      Checked: int
      Skipped: int
      Unchecked: int }

/// Parse a finished scan cycle's counts.
let scanCounts (cycle: Cycle) : ScanCounts option =
    match tryTagged cycle.StartLine, cycle.EndLine |> Option.bind tryTagged with
    | Some(_, startBody), Some(_, endBody) ->
        let s = scanRegistered.Match startBody
        let e = scanChecked.Match endBody

        if s.Success && e.Success then
            Some
                { Projects = int s.Groups.["projects"].Value
                  Registered = int s.Groups.["files"].Value
                  Checked = int e.Groups.["checked"].Value
                  Skipped = int e.Groups.["skipped"].Value
                  Unchecked = int e.Groups.["unchecked"].Value }
        else
            None
    | _ -> None

/// A test cycle opens on `executeTests starting` and closes on `Tests complete:`.
let testCycles (window: string list) : Cycle list =
    cycles
        (tagged "test-prune" (fun b -> b.StartsWith("executeTests starting", StringComparison.Ordinal)))
        (tagged "test-prune" (fun b -> b.StartsWith("Tests complete:", StringComparison.Ordinal)))
        window

let private outputLog =
    Regex(@"streaming run output to (?<path>\S+\.output\.log)", RegexOptions.Compiled)

/// The per-project run-output logs a test cycle streamed to.
let outputLogs (cycle: Cycle) : string list =
    cycle.Lines
    |> List.choose (fun line ->
        match tryTagged line with
        | Some("test-prune", body) ->
            let m = outputLog.Match(body)
            if m.Success then Some m.Groups.["path"].Value else None
        | _ -> None)

/// Totals from a Microsoft Testing Platform `Test run summary` block.
type TestTotals =
    { Total: int
      Failed: int
      Succeeded: int
      Skipped: int }

let private summaryField (name: string) =
    Regex($@"^\s*{name}: (?<n>\d+)\s*$", RegexOptions.Compiled ||| RegexOptions.Multiline)

let private totalField = summaryField "total"
let private failedField = summaryField "failed"
let private succeededField = summaryField "succeeded"
let private skippedField = summaryField "skipped"

/// Parse the LAST `Test run summary` block of one run-output log. `None` when the log
/// has no summary (the run died before reporting), which a caller must count as a
/// missing result, not as zero tests.
let parseTestTotals (outputText: string) : TestTotals option =
    let at = outputText.LastIndexOf("Test run summary:", StringComparison.Ordinal)

    if at < 0 then
        None
    else
        let block = outputText.Substring(at)

        let read (field: Regex) =
            let m = field.Match(block)
            if m.Success then Some(int m.Groups.["n"].Value) else None

        match read totalField, read failedField, read succeededField, read skippedField with
        | Some total, Some failed, Some succeeded, Some skipped ->
            Some
                { Total = total
                  Failed = failed
                  Succeeded = succeeded
                  Skipped = skipped }
        | _ -> None
