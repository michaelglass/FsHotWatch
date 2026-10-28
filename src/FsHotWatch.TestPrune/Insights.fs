/// `fshw insights`: where a suite's wall clock goes, read from the retained run directories
/// (`.fshw/test-runs/<runId>/`) alone (AUTOMATION-1300, slice 3).
///
/// For the newest run, per project:
///
///   * the outcome, and the wall time against the project's budget (`timeoutSec`);
///   * the slowest classes by span, with the load each ran under when a profile recorded it;
///   * the serial tail — the stretch at the end where at most one class was still running,
///     and which class that was: the long pole a budget is spent waiting on;
///   * the classes that regressed against the same project's recent greens;
///   * across every retained run with a profile, the classes whose span grows most when
///     the box is contended.
///
/// A project's source is its `<P>.profile.jsonl` when it has one (a killed host's class
/// lines included); otherwise its CTRF report, whose rows carry their own start and stop,
/// and its `<P>.timed-out.json`. So runs written before profiles existed still answer.
/// Reading only: nothing here starts a daemon or touches a run.
module FsHotWatch.TestPrune.Insights

open System
open System.IO
open System.Text.Json.Nodes

/// One class of one project's run.
type ClassRow =
    {
        Timing: Profile.ClassTiming
        /// From a profile's class line; `None` from a report, which records no load.
        Contended: bool option
        LoadPerCoreMean: float option
    }

/// One project's part of one run.
type ProjectRun =
    {
        RunId: string
        Project: string
        /// `passed`, `failed`, `timed-out`, … — `Profile.outcomeOf`'s words.
        Outcome: string option
        WallMs: float option
        TimeoutSec: int option
        /// Slowest span first.
        Classes: ClassRow list
    }

// ---------------------------------------------------------------------------------------
// Reading a run directory
// ---------------------------------------------------------------------------------------

let private projectsIn (dir: string) : string list =
    let suffixes =
        [ Profile.Suffix; FsHotWatch.Ctrf.ReportSuffix; TimeoutMarker.Suffix ]

    try
        Directory.GetFiles dir
        |> Array.choose (fun path ->
            let name = Path.GetFileName path

            suffixes
            |> List.tryFind (fun s -> name.EndsWith(s, StringComparison.Ordinal))
            |> Option.map (fun s -> name.Substring(0, name.Length - s.Length)))
        |> Array.distinct
        |> Array.sort
        |> Array.toList
    with
    | :? IOException
    | :? UnauthorizedAccessException -> []

let private readText (path: string) : string option =
    try
        if File.Exists path then
            Some(File.ReadAllText path)
        else
            None
    with
    | :? IOException
    | :? UnauthorizedAccessException -> None

let private ofProfile (runId: string) (project: string) (lines: Profile.Line list) : ProjectRun =
    let timeout =
        lines
        |> List.tryPick (function
            | Profile.Start(_, _, _, t, _) -> Some t
            | _ -> None)
        |> Option.flatten

    let wall, outcome =
        lines
        |> List.tryPick (function
            | Profile.End(_, wall, _, _, outcome) -> Some(Some(float wall), Some outcome)
            | _ -> None)
        |> Option.defaultValue (None, None)

    { RunId = runId
      Project = project
      Outcome = outcome
      WallMs = wall
      TimeoutSec = timeout
      Classes =
        lines
        |> List.choose (function
            | Profile.Class(t, c, _) ->
                Some
                    { Timing = t
                      Contended = c.Contended
                      LoadPerCoreMean = c.LoadPerCoreMean }
            | _ -> None) }

/// A `<P>.timed-out.json`: the budget, and the wall from the spawn to the stop.
let private ofTimeoutMarker (json: string) : int option * float option =
    try
        match JsonNode.Parse json with
        | :? JsonObject as o ->
            let date (node: JsonNode) =
                match node with
                | :? JsonValue as v ->
                    match v.TryGetValue<DateTime>() with
                    | true, d -> Some(d.ToUniversalTime())
                    | _ -> None
                | _ -> None

            let budget =
                match o["timeoutSec"] with
                | :? JsonValue as v ->
                    match v.TryGetValue<int>() with
                    | true, n -> Some n
                    | _ -> None
                | _ -> None

            let wall =
                match
                    date o["spawnedAt"],
                    (match o["stop"] with
                     | null -> None
                     | stop -> date stop["at"])
                with
                | Some spawned, Some stopped -> Some (stopped - spawned).TotalMilliseconds
                | _ -> None

            budget, wall
        | _ -> None, None
    with :? Text.Json.JsonException ->
        None, None

let private ofReports (runId: string) (dir: string) (project: string) : ProjectRun =
    let report = readText (Path.Combine(dir, project + FsHotWatch.Ctrf.ReportSuffix))

    let timeout, killedWall =
        readText (TimeoutMarker.pathIn dir project)
        |> Option.map ofTimeoutMarker
        |> Option.defaultValue (None, None)

    let timings = report |> Option.map Profile.classTimings |> Option.defaultValue []

    let reportWall =
        match timings with
        | [] -> None
        | ts ->
            let first = ts |> List.map _.Start |> List.min
            let last = ts |> List.map _.Stop |> List.max
            Some (last - first).TotalMilliseconds

    let outcome =
        if Option.isSome killedWall || Option.isSome timeout then
            Some "timed-out"
        else
            report
            |> Option.bind FsHotWatch.Ctrf.trySummary
            |> Option.map (fun s -> if s.Failed + s.Other > 0 then "failed" else "passed")

    { RunId = runId
      Project = project
      Outcome = outcome
      WallMs = killedWall |> Option.orElse reportWall
      TimeoutSec = timeout
      Classes =
        timings
        |> List.map (fun t ->
            { Timing = t
              Contended = None
              LoadPerCoreMean = None }) }

/// Every project of one run directory, by name. A project with a profile is read from it.
let loadRun (dir: string) : ProjectRun list =
    let runId =
        Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

    projectsIn dir
    |> List.map (fun project ->
        match Profile.read (Profile.pathIn dir project) with
        | [] -> ofReports runId dir project
        | lines -> ofProfile runId project lines)

/// Every run directory under `runsDir` (`.fshw/test-runs`), newest first by write time —
/// the order `fshw status` reads them in. A missing root has no runs.
let loadRuns (runsDir: string) : (string * ProjectRun list) list =
    try
        if Directory.Exists runsDir then
            DirectoryInfo(runsDir).GetDirectories()
            |> Array.sortByDescending _.LastWriteTimeUtc
            |> Array.map (fun d -> d.Name, loadRun d.FullName)
            |> Array.toList
        else
            []
    with
    | :? IOException
    | :? UnauthorizedAccessException -> []

// ---------------------------------------------------------------------------------------
// Analyses
// ---------------------------------------------------------------------------------------

/// The end of a run where at most one class was running.
type Tail =
    {
        Start: DateTime
        LengthMs: float
        /// The classes running in it with their time in it, longest first: one at a time.
        Classes: (string * float) list
    }

/// The serial tail: from the last moment two or more classes were running to the last
/// stop. `None` when two classes run to the very end, or there are no classes.
let serialTail (timings: Profile.ClassTiming list) : Tail option =
    match timings with
    | [] -> None
    | _ ->
        // Stops before starts at the same instant: a class handing over to the next is
        // not two running at once.
        let events =
            timings
            |> List.collect (fun t -> [ t.Start, 1; t.Stop, -1 ])
            |> List.sortBy (fun (at, delta) -> at, delta)

        let last = timings |> List.map _.Stop |> List.max

        let tailStart =
            events
            |> List.fold
                (fun (running, start) (at, delta) ->
                    let now = running + delta
                    now, (if running >= 2 && now < 2 then Some at else start))
                (0, None)
            |> snd
            |> Option.defaultValue (timings |> List.map _.Start |> List.min)

        if tailStart >= last then
            None
        else
            Some
                { Start = tailStart
                  LengthMs = (last - tailStart).TotalMilliseconds
                  Classes =
                    timings
                    |> List.filter (fun t -> t.Stop > tailStart)
                    |> List.map (fun t -> t.Class, (t.Stop - max t.Start tailStart).TotalMilliseconds)
                    |> List.sortByDescending (fun (cls, ms) -> ms, cls) }

/// A class whose span grew past `MinRegressionRatio` and `MinRegressionMs` over the median
/// of its spans in the project's recent greens.
type Regression =
    {
        Class: string
        SpanMs: float
        BaselineMedianMs: float
        /// The greens the median was taken over.
        Baselines: int
    }

[<Literal>]
let MinRegressionRatio = 1.5

/// A second, so a class going from 10 ms to 30 ms is not news.
[<Literal>]
let MinRegressionMs = 1000.0

let private median (values: float list) : float =
    match List.sort values with
    | [] -> 0.0
    | sorted ->
        let n = sorted.Length

        if n % 2 = 1 then
            sorted[n / 2]
        else
            (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0

let private spanMs (t: Profile.ClassTiming) = (t.Stop - t.Start).TotalMilliseconds

/// The greens of `target`'s project among `history` (newest first), other than `target`'s
/// own run, at most `lastGreens` of them.
let private recentGreens (lastGreens: int) (target: ProjectRun) (history: ProjectRun list) =
    history
    |> List.filter (fun p ->
        p.Project = target.Project
        && p.RunId <> target.RunId
        && p.Outcome = Some "passed")
    |> List.truncate lastGreens

/// The classes of `target` that regressed against the last `lastGreens` greens of its
/// project in `history` (newest first), worst first. A class no green ran is not compared.
let regressions (lastGreens: int) (target: ProjectRun) (history: ProjectRun list) : Regression list =
    let greens = recentGreens lastGreens target history

    target.Classes
    |> List.choose (fun row ->
        let spans =
            greens
            |> List.choose (fun g -> g.Classes |> List.tryFind (fun c -> c.Timing.Class = row.Timing.Class))
            |> List.map (fun c -> spanMs c.Timing)

        match spans with
        | [] -> None
        | _ ->
            let now = spanMs row.Timing
            let baseline = median spans

            if now >= baseline * MinRegressionRatio && now - baseline >= MinRegressionMs then
                Some
                    { Class = row.Timing.Class
                      SpanMs = now
                      BaselineMedianMs = baseline
                      Baselines = spans.Length }
            else
                None)
    |> List.sortByDescending (fun r -> r.SpanMs / max 1.0 r.BaselineMedianMs)

/// How much longer a class runs when the box is contended.
type Growth =
    {
        Class: string
        QuietMedianMs: float
        LoadedMedianMs: float
        /// `LoadedMedianMs / QuietMedianMs`.
        Ratio: float
    }

/// Every class that ran both quiet and contended (by its profile line's own load), most
/// load-sensitive first. Report-only rows carry no load and are not counted.
let growth (runs: ProjectRun list) : Growth list =
    runs
    |> List.collect _.Classes
    |> List.choose (fun c ->
        c.Contended
        |> Option.map (fun loaded -> c.Timing.Class, loaded, spanMs c.Timing))
    |> List.groupBy (fun (cls, _, _) -> cls)
    |> List.choose (fun (cls, rows) ->
        let spans loaded =
            rows
            |> List.filter (fun (_, l, _) -> l = loaded)
            |> List.map (fun (_, _, s) -> s)

        match spans false, spans true with
        | [], _
        | _, [] -> None
        | quiet, loaded ->
            let q = median quiet
            let l = median loaded

            Some
                { Class = cls
                  QuietMedianMs = q
                  LoadedMedianMs = l
                  Ratio = if q > 0.0 then l / q else 0.0 })
    |> List.sortByDescending (fun g -> g.Ratio, g.Class)

// ---------------------------------------------------------------------------------------
// The report
// ---------------------------------------------------------------------------------------

type Options =
    {
        /// Slowest classes listed per project.
        Top: int
        /// Greens a regression is judged against.
        LastGreens: int
        /// Each project's `timeoutSec` from the configuration, for a run whose own record
        /// does not carry it (a report-only run).
        Budgets: Map<string, int>
    }

    static member defaults =
        { Top = 10
          LastGreens = 5
          Budgets = Map.empty }

/// A class spanning at least this share of its project's wall is named its long pole: the
/// budget is spent waiting on it whatever else runs beside it.
[<Literal>]
let LongPoleShare = 0.5

/// Tail classes named before "and N more".
[<Literal>]
let TailClassesNamed = 3

let private seconds (ms: float) = $"%.1f{ms / 1000.0}s"

let private projectLines (options: Options) (history: ProjectRun list) (p: ProjectRun) : string list =
    let budget = p.TimeoutSec |> Option.orElse (Map.tryFind p.Project options.Budgets)

    let outcome = p.Outcome |> Option.defaultValue "outcome unknown"

    let headline =
        match p.WallMs, budget with
        | Some wall, Some b ->
            let share = wall / (float b * 1000.0) * 100.0
            let room = float b * 1000.0 - wall

            if room >= 0.0 then
                $"%s{p.Project}: %s{outcome}, wall %s{seconds wall} of a %d{b}s budget (%.0f{share}%%, %s{seconds room} headroom)"
            else
                $"%s{p.Project}: %s{outcome}, wall %s{seconds wall} OVER its %d{b}s budget by %s{seconds -room}"
        | Some wall, None -> $"%s{p.Project}: %s{outcome}, wall %s{seconds wall} (no budget known)"
        | None, _ -> $"%s{p.Project}: %s{outcome}, wall unknown"

    let load (c: ClassRow) =
        match c.Contended, c.LoadPerCoreMean with
        | Some true, Some l -> $"  contended (%.2f{l} load/core)"
        | Some false, Some l -> $"  quiet (%.2f{l} load/core)"
        | _ -> ""

    let classes =
        match p.Classes with
        | [] -> [ "  no class timings (no report, and no printed per-test results)" ]
        | rows ->
            "  slowest classes (span, tests, summed test time):"
            :: (rows
                |> List.truncate options.Top
                |> List.map (fun c ->
                    $"    %8s{seconds (spanMs c.Timing)}  %s{c.Timing.Class}  %d{c.Timing.Tests} tests, %s{seconds (float c.Timing.DurationMs)}%s{load c}"))

    let longPole =
        match p.Classes, p.WallMs with
        | slowest :: _, Some wall when wall > 0.0 && spanMs slowest.Timing >= LongPoleShare * wall ->
            [ $"  long pole: %s{slowest.Timing.Class}, %.0f{spanMs slowest.Timing / wall * 100.0}%% of the wall" ]
        | _ -> []

    let tail =
        match serialTail (p.Classes |> List.map _.Timing) with
        | Some t ->
            match t.Classes with
            | [ (only, _) ] -> [ $"  serial tail: %s{seconds t.LengthMs}, %s{only} alone" ]
            | many ->
                let named =
                    many
                    |> List.truncate TailClassesNamed
                    |> List.map (fun (cls, ms) -> $"%s{cls} %s{seconds ms}")
                    |> String.concat ", "

                let rest =
                    match many.Length - TailClassesNamed with
                    | n when n > 0 -> $", and %d{n} more"
                    | _ -> ""

                [ $"  serial tail: %s{seconds t.LengthMs}, one class at a time: %s{named}%s{rest}" ]
        | None -> []

    let regressed =
        let greens = recentGreens options.LastGreens p history

        match greens, regressions options.LastGreens p history with
        | [], _ -> [ "  no earlier green of this project to compare with" ]
        | _, [] -> [ $"  nothing regressed vs the last %d{greens.Length} greens" ]
        | _, found ->
            $"  regressed vs the last %d{greens.Length} greens:"
            :: (found
                |> List.map (fun r ->
                    $"    %s{r.Class} %s{seconds r.SpanMs}, median %s{seconds r.BaselineMedianMs} (%.1f{r.SpanMs / r.BaselineMedianMs}x)"))

    headline :: classes @ longPole @ tail @ regressed

/// The report on the newest of `runs` (newest first, as `loadRuns` returns them), against
/// all of them as history.
let report (options: Options) (runs: (string * ProjectRun list) list) : string list =
    match runs with
    | [] -> [ "no test runs found" ]
    | (runId, projects) :: _ ->
        let history = runs |> List.collect snd

        let grown =
            match growth history with
            | [] -> []
            | gs ->
                "load-sensitive classes (median span when contended vs quiet, every retained profile):"
                :: (gs
                    |> List.truncate options.Top
                    |> List.map (fun g ->
                        $"  %.1f{g.Ratio}x  %s{g.Class}  quiet %s{seconds g.QuietMedianMs}, contended %s{seconds g.LoadedMedianMs}"))

        $"run %s{runId} (%d{runs.Length} retained)"
        :: (projects |> List.collect (projectLines options history))
        @ grown
