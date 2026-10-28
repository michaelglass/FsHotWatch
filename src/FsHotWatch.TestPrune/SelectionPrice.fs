/// Price a test selection before it launches, from the class durations earlier runs
/// recorded.
///
/// Every retained run directory already holds, per project, each class's summed test
/// durations (`Insights.loadRun`: the profile's class lines, or the CTRF report's rows).
/// That is the project-and-class mapping pricing needs; the per-test history
/// (`Flakiness`) keys durations by test name alone and cannot say which project or class a
/// test belongs to. A class is priced at the median of its recorded durations over its
/// project's newest runs, and a selection at the sum of its classes. A class or project no
/// run recorded is named, never priced at zero silently.
module FsHotWatch.TestPrune.SelectionPrice

open System
open System.IO

/// How much of one project a selection asks for.
[<RequireQualifiedAccess>]
type Asked =
    /// Every class.
    | Whole
    /// Only these classes.
    | Classes of Set<string>
    /// Every class but these.
    | AllBut of Set<string>

/// Each project's classes, by name, priced in milliseconds.
type Book =
    { Classes: Map<string, Map<string, float>> }

/// How many of a project's newest usable runs price its classes, by default.
[<Literal>]
let DefaultWindow = 5

let private median (values: float list) : float =
    let sorted = List.sort values |> Array.ofList
    let n = sorted.Length

    if n % 2 = 1 then
        sorted[n / 2]
    else
        (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0

/// A run whose classes say what they cost: it finished. A timed-out project's classes are
/// the part that ran before the kill, which prices a class at less than it costs.
let private usable (run: Insights.ProjectRun) =
    match run.Outcome with
    | Some "passed"
    | Some "failed" -> not (List.isEmpty run.Classes)
    | _ -> false

module Book =
    /// The book over `runs` (newest first, as `Insights.loadRuns` orders them), each project
    /// priced from its own `window` newest usable runs.
    let ofRuns (window: int) (runs: (string * Insights.ProjectRun list) list) : Book =
        let classes =
            runs
            |> List.collect snd
            |> List.filter usable
            |> List.groupBy _.Project
            |> List.map (fun (project, projectRuns) ->
                let recorded =
                    projectRuns
                    |> List.truncate window
                    |> List.collect (fun r ->
                        r.Classes |> List.map (fun c -> c.Timing.Class, float c.Timing.DurationMs))
                    |> List.groupBy fst
                    |> List.map (fun (cls, samples) -> cls, samples |> List.map snd |> median)
                    |> Map.ofList

                project, recorded)
            |> Map.ofList

        { Classes = classes }

    /// The recorded price of one class, in milliseconds.
    let classMs (book: Book) (project: string) (cls: string) : float option =
        book.Classes |> Map.tryFind project |> Option.bind (Map.tryFind cls)

    /// The book over the newest `window` run directories under `runsDir`
    /// (`.fshw/test-runs`), and only those: each run read is a parse of its reports.
    let ofRunsDir (window: int) (runsDir: string) : Book =
        let runs =
            try
                if Directory.Exists runsDir then
                    DirectoryInfo(runsDir).GetDirectories()
                    |> Array.sortByDescending _.LastWriteTimeUtc
                    |> Array.truncate window
                    |> Array.map (fun d -> d.Name, Insights.loadRun d.FullName)
                    |> Array.toList
                else
                    []
            with
            | :? IOException
            | :? UnauthorizedAccessException -> []

        ofRuns window runs

/// What a selection is priced at.
type Price =
    {
        /// The summed recorded cost of every priced class, in milliseconds.
        EstimateMs: float
        /// How many classes carried a price.
        PricedClasses: int
        /// Classes the selection names that no recorded run of their project holds.
        Unpriced: (string * string) list
        /// Projects the selection takes that no recorded run holds at all.
        UnpricedProjects: string list
    }

/// Price `selection`, one entry per project.
let price (book: Book) (selection: (string * Asked) list) : Price =
    let perProject =
        selection
        |> List.map (fun (project, asked) ->
            match Map.tryFind project book.Classes with
            | None -> 0.0, 0, [], [ project ]
            | Some known ->
                let named, unknown =
                    match asked with
                    | Asked.Whole -> Map.toList known, []
                    | Asked.AllBut skipped ->
                        known |> Map.filter (fun c _ -> not (Set.contains c skipped)) |> Map.toList, []
                    | Asked.Classes only ->
                        let only = Set.toList only

                        only
                        |> List.choose (fun c -> Map.tryFind c known |> Option.map (fun ms -> c, ms)),
                        only
                        |> List.filter (fun c -> not (Map.containsKey c known))
                        |> List.map (fun c -> project, c)

                named |> List.sumBy snd, List.length named, unknown, [])

    { EstimateMs = perProject |> List.sumBy (fun (ms, _, _, _) -> ms)
      PricedClasses = perProject |> List.sumBy (fun (_, n, _, _) -> n)
      Unpriced = perProject |> List.collect (fun (_, _, u, _) -> u)
      UnpricedProjects = perProject |> List.collect (fun (_, _, _, p) -> p) }

/// One line for a log or a report.
let describe (p: Price) : string =
    let unpriced =
        [ if not (List.isEmpty p.Unpriced) then
              $"%d{List.length p.Unpriced} class(es) never recorded"
          if not (List.isEmpty p.UnpricedProjects) then
              $"""no recorded run of %s{String.concat ", " p.UnpricedProjects}""" ]

    let suffix =
        if List.isEmpty unpriced then
            ""
        else
            $""" — unpriced: %s{String.concat "; " unpriced}"""

    $"~%.1f{p.EstimateMs / 1000.0}s of test time over %d{p.PricedClasses} recorded class(es)%s{suffix}"

/// One run priced before the fact.
type Backtest =
    {
        RunId: string
        /// The price, from the runs before this one, of the classes it ran.
        EstimateMs: float
        /// What its classes recorded.
        ActualMs: float
        Unpriced: int
    }

/// Price each run's own classes from the runs before it (newest first), and set the price
/// beside what it spent: how well the book predicts. A run with nothing before it is left
/// out.
let backtest (window: int) (runs: (string * Insights.ProjectRun list) list) : Backtest list =
    runs
    |> List.mapi (fun i (runId, projects) -> i, runId, projects)
    |> List.choose (fun (i, runId, projects) ->
        let before = runs |> List.skip (i + 1)
        let ran = projects |> List.filter usable

        if List.isEmpty before || List.isEmpty ran then
            None
        else
            let book = Book.ofRuns window before

            let selection =
                ran
                |> List.map (fun p -> p.Project, Asked.Classes(p.Classes |> List.map _.Timing.Class |> Set.ofList))

            let priced = price book selection

            Some
                { RunId = runId
                  EstimateMs = priced.EstimateMs
                  ActualMs =
                    ran
                    |> List.sumBy (fun p -> p.Classes |> List.sumBy (fun c -> float c.Timing.DurationMs))
                  Unpriced = List.length priced.Unpriced + List.length priced.UnpricedProjects })
