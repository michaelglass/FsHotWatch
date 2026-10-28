/// How a run's test groups are launched: all at once (the default), or at most
/// `tests.maxParallelProjects` at a time, longest-expected first.
///
/// A group's projects run one after another, so the number of groups in flight is the
/// number of test hosts in flight. The cap therefore limits groups, never projects
/// within one.
module FsHotWatch.TestPrune.GroupSchedule

open System
open System.IO
open System.Text.Json

/// The wall a CTRF report records: `results.summary.stop - start`, in epoch
/// milliseconds (a top-level `summary` is read too, as `Ctrf.trySummary` reads it).
/// `None` for a report that is unreadable, ran no tests, or has no forward-running start
/// and stop.
let private wallOf (path: string) : TimeSpan option =
    let millis (summary: JsonElement) (name: string) =
        match summary.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetDouble())
        | _ -> None

    let objectAt (parent: JsonElement) (name: string) =
        if parent.ValueKind <> JsonValueKind.Object then
            None
        else
            match parent.TryGetProperty name with
            | true, child when child.ValueKind = JsonValueKind.Object -> Some child
            | _ -> None

    try
        use doc = JsonDocument.Parse(File.ReadAllText path)
        let root = doc.RootElement

        let summary =
            match objectAt root "results" with
            | Some results -> objectAt results "summary"
            | None -> objectAt root "summary"

        match summary with
        | None -> None
        | Some s ->
            // A run that ran no tests (an impact-filtered run that selected nothing) still
            // writes a report, with a wall of milliseconds: it says nothing about the
            // project, and read as history it would rank a long project last.
            match millis s "tests", millis s "start", millis s "stop" with
            | Some tests, Some start, Some stop when tests >= 1.0 && stop > start ->
                Some(TimeSpan.FromMilliseconds(stop - start))
            | _ -> None
    with
    | :? JsonException
    | :? IOException
    | :? UnauthorizedAccessException -> None

/// The longest wall each project's retained CTRF reports record
/// (`.fshw/test-runs/<runId>/<Project>.ctrf.json`). The longest rather than the latest,
/// because an impact-filtered run's short wall says little about the project's full one,
/// and the schedule must start the slow projects early. A project with no usable report
/// is absent.
let expectedWalls (repoRoot: string) : Map<string, TimeSpan> =
    let root = FsHotWatch.Ctrf.reportsDir repoRoot

    // One level: each run directory holds its reports and nothing else. A directory that
    // cannot be listed costs only its own reports.
    let listed (list: unit -> string array) =
        try
            list ()
        with
        | :? IOException
        | :? UnauthorizedAccessException -> [||]

    let reports =
        if Directory.Exists root then
            listed (fun () -> Directory.GetDirectories root)
            |> Array.collect (fun run -> listed (fun () -> Directory.GetFiles(run, "*" + FsHotWatch.Ctrf.ReportSuffix)))
        else
            [||]

    reports
    |> Array.choose (fun path ->
        let name = Path.GetFileName path
        let project = name.Substring(0, name.Length - FsHotWatch.Ctrf.ReportSuffix.Length)
        wallOf path |> Option.map (fun wall -> project, wall))
    |> Array.groupBy fst
    |> Array.map (fun (project, walls) -> project, walls |> Array.map snd |> Array.max)
    |> Map.ofArray

/// Order groups for launch, longest expected wall first. A group's expectation is the
/// sum of its projects' walls, since they run one after another.
///
/// A group with ANY project that has no history comes first, in configured order: it may
/// be the longest, and starting it early is the safe guess. With no history at all, every
/// group is unknown and the configured order stands. Equal expectations keep their
/// configured order (the sort is stable).
let longestFirst (projectsOf: 'g -> string list) (expected: Map<string, TimeSpan>) (groups: 'g list) : 'g list =
    let expectationOf group =
        projectsOf group
        |> List.fold
            (fun total project ->
                match total, Map.tryFind project expected with
                | Some sum, Some wall -> Some(sum + wall)
                | _ -> None)
            (Some TimeSpan.Zero)

    groups
    |> List.sortBy (fun group ->
        match expectationOf group with
        | None -> 0, TimeSpan.Zero
        | Some wall -> 1, -wall)

/// Run the group jobs: all at once when `maxParallel` is `None` (exactly today's
/// `Async.Parallel`), else at most `maxParallel` at a time, starting them in the order
/// given. Results come back in that order. A job still waiting for a slot when the run
/// is cancelled never starts.
let run (maxParallel: int option) (jobs: Async<'T> list) : Async<'T array> =
    match maxParallel with
    | None -> Async.Parallel jobs
    | Some cap -> Async.Parallel(jobs, maxDegreeOfParallelism = cap)
