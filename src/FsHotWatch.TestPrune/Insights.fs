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

/// A test still running when its host ended.
type RunningTest =
    {
        Class: string
        Test: string
        /// From its start to the end of the run.
        RunningMs: float
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
        /// The mean 1-minute load per core over the run, from its profile's samples; `None`
        /// without a profile or a sample.
        LoadPerCoreMean: float option
        /// The tests still running when the host ended (a killed host's hung test).
        RunningAtEnd: RunningTest list
        /// The child processes seen in the host's tree, by command, most CPU first.
        Children: Profile.ChildCommand list
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

    let cores =
        lines
        |> List.tryPick (function
            | Profile.Start(_, _, cores, _, _) -> Some(max 1 cores)
            | _ -> None)
        |> Option.defaultValue 1

    let loads =
        lines
        |> List.choose (function
            | Profile.Sampled s -> s.Load1
            | _ -> None)

    { RunId = runId
      Project = project
      Outcome = outcome
      WallMs = wall
      TimeoutSec = timeout
      LoadPerCoreMean =
        if List.isEmpty loads then
            None
        else
            Some(List.average loads / float cores)
      RunningAtEnd =
        let ended =
            lines
            |> List.tryPick (function
                | Profile.End(at, _, _, _, _) -> Some at
                | _ -> None)

        lines
        |> List.choose (function
            | Profile.Running(test, since) ->
                Some
                    { Class = Profile.classOfTestName test
                      Test = test
                      RunningMs =
                        ended
                        |> Option.map (fun e -> (e - since).TotalMilliseconds)
                        |> Option.defaultValue 0.0 }
            | _ -> None)
      Children =
        lines
        |> List.choose (function
            | Profile.Children c -> Some c
            | _ -> None)
        |> List.sortBy (fun c -> -c.CpuMs, c.Command)
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
      LoadPerCoreMean = None
      RunningAtEnd = []
      Children = []
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
        /// The run to report on, by id; `None` is the newest.
        Run: string option
    }

    static member defaults =
        { Top = 10
          LastGreens = 5
          Budgets = Map.empty
          Run = None }

/// A class spanning at least this share of its project's wall is named its long pole: the
/// budget is spent waiting on it whatever else runs beside it.
[<Literal>]
let LongPoleShare = 0.5

/// Child commands the text report names.
[<Literal>]
let ChildCommandsNamed = 5

/// Tail classes named before "and N more".
[<Literal>]
let TailClassesNamed = 3

let private seconds (ms: float) = $"%.1f{ms / 1000.0}s"

/// The tests still running when the host ended, as one phrase (none: empty).
let private runningAtKill (running: RunningTest list) : string list =
    match running with
    | [] -> []
    | rs ->
        let named =
            rs
            |> List.map (fun r -> $"%s{r.Class} (%s{r.Test}, %s{seconds r.RunningMs})")
            |> String.concat "; "

        [ $"running at the kill: %s{named}" ]

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

    let runLoad =
        match p.LoadPerCoreMean with
        | Some l ->
            let verdict =
                if l >= Profile.ContendedLoadPerCore then
                    "contended"
                else
                    "quiet"

            [ $"  load over the run: %.2f{l} per core, %s{verdict}" ]
        | None -> []

    let running =
        runningAtKill p.RunningAtEnd |> List.map (fun phrase -> $"  %s{phrase}")

    let children =
        match p.Children with
        | [] -> []
        | cs ->
            let named =
                cs
                |> List.truncate ChildCommandsNamed
                |> List.map (fun c -> $"%s{c.Command} x%d{c.Processes} %s{seconds c.CpuMs} CPU")
                |> String.concat ", "

            [ $"  child processes (seen at the 30 s table reads, so at least): %s{named}" ]

    headline :: runLoad @ classes @ running @ longPole @ tail @ children @ regressed

// ---------------------------------------------------------------------------------------
// One project's insight, as a record: what `--json` prints and a verdict carries
// ---------------------------------------------------------------------------------------

/// A class and its span, with its share of the project's wall when the wall is known.
type ClassShare =
    { Class: string
      SpanMs: float
      Share: float option }

/// One project of one run, summarized: the answer to "why did this run take so long?"
/// that `fshw insights --json` prints and a verdict carries for each timeout red.
type ProjectInsight =
    {
        RunId: string
        Project: string
        Outcome: string option
        WallMs: float option
        TimeoutSec: int option
        /// The budget less the wall; negative when the run went over.
        HeadroomMs: float option
        /// The slowest class, when it spans at least `LongPoleShare` of the wall.
        LongPole: ClassShare option
        /// The `SlowestInInsight` slowest classes, slowest first.
        Slowest: ClassShare list
        LoadPerCoreMean: float option
        /// Mean load per core ≥ `Profile.ContendedLoadPerCore`; `None` when no sample read one.
        Contended: bool option
        /// The tests still running at the kill, with their classes.
        RunningAtKill: RunningTest list
        /// The child processes seen in the host's tree, by command, most CPU first.
        Children: Profile.ChildCommand list
    }

/// Classes an insight lists.
[<Literal>]
let SlowestInInsight = 3

/// `p` as an insight. `budgets` supplies a `timeoutSec` the run's own record lacks.
let summarize (budgets: Map<string, int>) (p: ProjectRun) : ProjectInsight =
    let budget = p.TimeoutSec |> Option.orElse (Map.tryFind p.Project budgets)

    let share (c: ClassRow) : ClassShare =
        { Class = c.Timing.Class
          SpanMs = spanMs c.Timing
          Share =
            p.WallMs
            |> Option.filter (fun w -> w > 0.0)
            |> Option.map (fun w -> spanMs c.Timing / w) }

    let slowest = p.Classes |> List.truncate SlowestInInsight |> List.map share

    { RunId = p.RunId
      Project = p.Project
      Outcome = p.Outcome
      WallMs = p.WallMs
      TimeoutSec = budget
      HeadroomMs =
        match budget, p.WallMs with
        | Some b, Some w -> Some(float b * 1000.0 - w)
        | _ -> None
      LongPole =
        slowest
        |> List.tryHead
        |> Option.filter (fun s -> s.Share |> Option.exists (fun x -> x >= LongPoleShare))
      Slowest = slowest
      LoadPerCoreMean = p.LoadPerCoreMean
      Contended = p.LoadPerCoreMean |> Option.map (fun l -> l >= Profile.ContendedLoadPerCore)
      RunningAtKill = p.RunningAtEnd
      Children = p.Children }

/// A value, or JSON null for `None`.
let private optional (value: 'T option) : JsonNode =
    match value with
    | Some v -> JsonValue.Create<'T>(v)
    | None -> null

let private shareJson (s: ClassShare) : JsonNode =
    let o = JsonObject()
    o["class"] <- JsonValue.Create s.Class
    o["spanMs"] <- JsonValue.Create s.SpanMs
    o["share"] <- optional s.Share
    o

/// An insight as JSON — one object of `fshw insights --json`'s `projects`.
let insightJson (i: ProjectInsight) : JsonObject =
    let o = JsonObject()
    o["runId"] <- JsonValue.Create i.RunId
    o["project"] <- JsonValue.Create i.Project
    o["outcome"] <- optional i.Outcome
    o["wallMs"] <- optional i.WallMs
    o["timeoutSec"] <- optional i.TimeoutSec
    o["headroomMs"] <- optional i.HeadroomMs
    o["longPole"] <- i.LongPole |> Option.map shareJson |> Option.toObj
    o["slowest"] <- JsonArray(i.Slowest |> List.map shareJson |> List.toArray)
    o["loadPerCoreMean"] <- optional i.LoadPerCoreMean
    o["contended"] <- optional i.Contended

    o["children"] <-
        JsonArray(
            i.Children
            |> List.map (fun c ->
                let e = JsonObject()
                e["command"] <- JsonValue.Create c.Command
                e["processes"] <- JsonValue.Create c.Processes
                e["cpuMs"] <- JsonValue.Create c.CpuMs
                e :> JsonNode)
            |> List.toArray
        )

    o["runningAtKill"] <-
        JsonArray(
            i.RunningAtKill
            |> List.map (fun r ->
                let e = JsonObject()
                e["class"] <- JsonValue.Create r.Class
                e["test"] <- JsonValue.Create r.Test
                e["runningMs"] <- JsonValue.Create r.RunningMs
                e :> JsonNode)
            |> List.toArray
        )

    o

let private value<'T> (node: JsonNode) : 'T option =
    match node with
    | :? JsonValue as v ->
        match v.TryGetValue<'T>() with
        | true, x -> Some x
        | _ -> None
    | _ -> None

let private tryShare (node: JsonNode) : ClassShare option =
    match node with
    | :? JsonObject as o ->
        match value<string> o["class"], value<float> o["spanMs"] with
        | Some cls, Some span ->
            Some
                { Class = cls
                  SpanMs = span
                  Share = value<float> o["share"] }
        | _ -> None
    | _ -> None

/// An insight from its JSON; `None` unless it names a run and a project.
let tryReadInsight (node: JsonNode) : ProjectInsight option =
    match node with
    | :? JsonObject as o ->
        match value<string> o["runId"], value<string> o["project"] with
        | Some runId, Some project ->
            Some
                { RunId = runId
                  Project = project
                  Outcome = value<string> o["outcome"]
                  WallMs = value<float> o["wallMs"]
                  TimeoutSec = value<int> o["timeoutSec"]
                  HeadroomMs = value<float> o["headroomMs"]
                  LongPole = tryShare o["longPole"]
                  Slowest =
                    match o["slowest"] with
                    | :? JsonArray as arr -> arr |> Seq.choose tryShare |> Seq.toList
                    | _ -> []
                  LoadPerCoreMean = value<float> o["loadPerCoreMean"]
                  Contended = value<bool> o["contended"]
                  RunningAtKill =
                    match o["runningAtKill"] with
                    | :? JsonArray as arr ->
                        arr
                        |> Seq.choose (fun e ->
                            match e with
                            | :? JsonObject as r ->
                                match
                                    value<string> r["class"], value<string> r["test"], value<float> r["runningMs"]
                                with
                                | Some cls, Some test, Some ms ->
                                    Some
                                        { Class = cls
                                          Test = test
                                          RunningMs = ms }
                                | _ -> None
                            | _ -> None)
                        |> Seq.toList
                    | _ -> []
                  Children =
                    match o["children"] with
                    | :? JsonArray as arr ->
                        arr
                        |> Seq.choose (fun e ->
                            match e with
                            | :? JsonObject as c ->
                                match
                                    value<string> c["command"], value<int> c["processes"], value<float> c["cpuMs"]
                                with
                                | Some command, Some processes, Some cpu ->
                                    Some(
                                        { Command = command
                                          Processes = processes
                                          CpuMs = cpu }
                                        : Profile.ChildCommand
                                    )
                                | _ -> None
                            | _ -> None)
                        |> Seq.toList
                    | _ -> [] }
        | _ -> None
    | _ -> None

/// The run `options` asks for (the newest when it names none), and the history it is
/// judged against: that run and every older one.
let private target (options: Options) (runs: (string * ProjectRun list) list) =
    match options.Run with
    | None -> runs
    | Some id -> runs |> List.skipWhile (fun (runId, _) -> runId <> id)

/// The report on the newest of `runs` (newest first, as `loadRuns` returns them), or on
/// the run `options.Run` names, judged against it and every older run.
let report (options: Options) (runs: (string * ProjectRun list) list) : string list =
    match runs, target options runs, options.Run with
    | [], _, _ -> [ "no test runs found" ]
    | _, [], Some id -> [ $"no retained run %s{id}" ]
    | _, [], None -> [ "no test runs found" ]
    | _, ((runId, projects) :: _ as fromTarget), _ ->
        let history = fromTarget |> List.collect snd

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

/// `report` as one JSON object: `runId` (null when there is no such run), `retained`,
/// `projects` (each an `insightJson`), and `loadSensitive`.
let reportJson (options: Options) (runs: (string * ProjectRun list) list) : string =
    let o = JsonObject()
    o["retained"] <- JsonValue.Create runs.Length

    let fromTarget = target options runs

    match fromTarget with
    | (runId, projects) :: _ ->
        o["runId"] <- JsonValue.Create runId

        o["projects"] <-
            JsonArray(
                projects
                |> List.map (fun p -> summarize options.Budgets p |> insightJson :> JsonNode)
                |> List.toArray
            )
    | [] ->
        o["runId"] <- null
        o["projects"] <- JsonArray()

    o["loadSensitive"] <-
        JsonArray(
            growth (fromTarget |> List.collect snd)
            |> List.truncate options.Top
            |> List.map (fun g ->
                let e = JsonObject()
                e["class"] <- JsonValue.Create g.Class
                e["quietMedianMs"] <- JsonValue.Create g.QuietMedianMs
                e["loadedMedianMs"] <- JsonValue.Create g.LoadedMedianMs
                e["ratio"] <- JsonValue.Create g.Ratio
                e :> JsonNode)
            |> List.toArray
        )

    o.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true))

/// One line naming a timed-out project's long pole, overrun and load, and the command
/// that says more — what a verdict prints for each timeout red.
let timeoutLine (i: ProjectInsight) : string =
    let percent (s: ClassShare) =
        s.Share |> Option.map (fun x -> $"%.0f{x * 100.0}%%") |> Option.defaultValue "?"

    let pole =
        match i.LongPole, i.Slowest, i.WallMs with
        | Some lp, _, Some wall -> $"long pole %s{lp.Class} (%s{percent lp} of the %s{seconds wall} wall)"
        | _, s :: _, _ -> $"slowest finished class %s{s.Class} (%s{seconds s.SpanMs}, %s{percent s} of the wall)"
        | _ -> "no class finished before the kill"

    let budget =
        match i.HeadroomMs, i.TimeoutSec with
        | Some h, Some b when h < 0.0 -> [ $"%s{seconds -h} over its %d{b}s budget" ]
        | Some h, Some b -> [ $"%s{seconds h} left of its %d{b}s budget" ]
        | _ -> []

    let load =
        match i.Contended, i.LoadPerCoreMean with
        | Some true, Some l -> $"contended (%.2f{l} load/core)"
        | Some false, Some l -> $"quiet (%.2f{l} load/core)"
        | _ -> "load unknown"

    let facts =
        pole :: runningAtKill i.RunningAtKill @ budget @ [ load ] |> String.concat ", "

    $"%s{i.Project} timed out: %s{facts} — fshw insights --run %s{i.RunId}"
