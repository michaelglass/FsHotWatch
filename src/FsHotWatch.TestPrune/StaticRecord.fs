/// `runtimeSelection: "runtime/record-static"` (AUTOMATION-1686, ADR-070).
///
/// Runtime coverage decides what runs, exactly as under `"runtime"`. Beside every completed
/// run, one `StaticRecord` is filed: the selection static selection ALONE would have
/// launched, the selection that ran, and the outcome of every test that ran. A static miss
/// is then read straight off real outcomes: a test OUTSIDE the static set failed on a tree
/// where every test INSIDE it passed (`judge`).
///
/// Nothing here feeds the run. The record is computed from what the launch already decided
/// and filed after the run completed; a record that cannot be built or written is one
/// warning and a missing record, never a different selection, verdict or cache entry.
module FsHotWatch.TestPrune.StaticRecord

open System
open System.IO
open FsHotWatch.Events
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.TestReports

/// A set with at least one member. Only `NonEmptySet.ofSet` makes one.
type NonEmptySet<'T when 'T: comparison> = private NonEmptySet of Set<'T>

module NonEmptySet =
    let ofSet (set: Set<'T>) : NonEmptySet<'T> option =
        if Set.isEmpty set then None else Some(NonEmptySet set)

    let toSet (NonEmptySet set) : Set<'T> = set

/// One test, by the project that ran it, the class the selection names it by, and the
/// fully-qualified name its report gave it.
type TestId =
    { Project: string
      Class: string
      Name: string }

/// A test's outcome after the runner's own retry policy: a test with any passing row passed.
type TestOutcome =
    | Passed
    | Failed
    | Skipped

/// What one project's part of a completed run established about its tests.
type ProjectOutcome =
    /// The project ran to its end. `rows` are the tests its report named, empty when it
    /// wrote no report (a green project without one, or a filter that matched nothing).
    | Ran of rows: Map<TestId, TestOutcome>
    /// The project failed and its report named no failed test.
    | RedUnnamed
    /// The runner was killed at its timeout. A failure of the project's run, whichever of
    /// its tests were still running.
    | TimedOut
    /// The project verified nothing (its host errored before writing a report, or its tests
    /// were deferred), so whether any of its tests would pass is not known.
    | ProvedNothing of reason: string

/// The tree a record describes.
type RecordedTree =
    | Tree of identity: string
    /// The checkout's tree could not be read. Such a record counts toward no distinct-tree
    /// bar (ADR-070).
    | Unidentified

/// One completed run under `"runtime/record-static"`.
type StaticRecord =
    {
        Tree: RecordedTree
        RunId: Guid
        /// What static selection alone would have launched.
        Static: Map<string, ProjectSelection>
        /// What the run launched: static selection plus runtime coverage's projects.
        Ran: Map<string, ProjectSelection>
        Outcomes: Map<string, ProjectOutcome>
        /// The run's tests whose recorded history had flipped between pass and fail before
        /// this run launched. Data beside a judgement, never a part of it.
        KnownFlaky: Set<TestId>
    }

/// A failure a record holds.
type Failure =
    | TestFailed of TestId
    | ProjectTimedOut of project: string
    | ProjectRedUnnamed of project: string

/// The judgement of one record, a pure function of it (`judge`).
type StaticJudgement =
    /// Static selection would have launched exactly what ran: the record carries no
    /// evidence about static selection either way.
    | StaticRanEverything
    /// A project in the run verified nothing, so whether every test inside the static set
    /// passed is not known.
    | OutcomesUnknown of projects: NonEmptySet<string>
    /// Nothing failed.
    | Green
    /// A failure inside the static set: static selection alone would have gone red too.
    /// `outsideFailed` lists what failed only outside it.
    | CaughtByStatic of insideFailed: NonEmptySet<Failure> * outsideFailed: Set<Failure>
    /// Every test inside the static set passed, and these, outside it, failed: static
    /// selection alone would have reported green over a red tree.
    | MissedByStatic of outsideFailed: NonEmptySet<Failure>

/// A run that executed to its natural end. Only `CompletedRun.ofCompletion` makes one, so a
/// cancelled or crashed run cannot be filed.
type CompletedRun =
    private { Completion: TestRunCompleted }

module CompletedRun =
    let ofCompletion (completion: TestRunCompleted) : Result<CompletedRun, string> =
        match completion.Outcome with
        | Normal -> Ok { Completion = completion }
        | Aborted reason -> Error reason

    let runId (run: CompletedRun) = run.Completion.RunId
    let results (run: CompletedRun) = run.Completion.Results

/// The selection static selection alone would have launched, from the selection that ran.
///
/// Runtime coverage enters a launch only by forcing whole projects. `runtimeOnly` names
/// the projects that nothing but a recorded project's runtime obligation forced
/// (`LaunchWidenings.RuntimeOnlyProjects`); every other project is in the static selection
/// exactly as it ran. A runtime-only project keeps what static causes named in it: its
/// outstanding failures (`[]`, a project-level red, is the whole project) and its declared
/// always-classes. With neither, static selection would not have launched it.
let staticOf
    (ran: Map<string, ProjectSelection>)
    (runtimeOnly: Set<string>)
    (outstanding: Map<string, string list>)
    (always: Map<string, string list>)
    : Map<string, ProjectSelection> =
    ran
    |> Map.toSeq
    |> Seq.choose (fun (project, selection) ->
        if not (Set.contains project runtimeOnly) then
            Some(project, selection)
        else
            match Map.tryFind project outstanding, Map.tryFind project always with
            | Some [], _ -> Some(project, ProjectInFull)
            | Some classes, extra -> Some(project, ProjectClasses(Set.ofList (classes @ defaultArg extra [])))
            | None, Some extra -> Some(project, ProjectClasses(Set.ofList extra))
            | None, None -> None)
    |> Map.ofSeq

/// Whether static selection would have run what `failure` names. A test is inside when its
/// project ran whole, or its class was named, or (an exclude filter) its class was not. A
/// project-level failure is inside when static selection launched the project at all.
let isInside (staticSelection: Map<string, ProjectSelection>) (failure: Failure) : bool =
    match failure with
    | TestFailed test ->
        match Map.tryFind test.Project staticSelection with
        | None -> false
        | Some ProjectInFull -> true
        | Some(ProjectClasses classes) -> Set.contains test.Class classes
        | Some(ProjectExcept classes) -> not (Set.contains test.Class classes)
    | ProjectTimedOut project
    | ProjectRedUnnamed project -> Map.containsKey project staticSelection

/// The failures a project's outcome holds, or `Error` when it verified nothing and so holds
/// no answer either way.
let private failuresOf (project: string) (outcome: ProjectOutcome) : Result<Failure list, string> =
    match outcome with
    | Ran rows ->
        rows
        |> Map.toList
        |> List.choose (fun (test, result) ->
            match result with
            | Failed -> Some(TestFailed test)
            | Passed
            | Skipped -> None)
        |> Ok
    | RedUnnamed -> Ok [ ProjectRedUnnamed project ]
    | TimedOut -> Ok [ ProjectTimedOut project ]
    | ProvedNothing _ -> Error project

/// The tests a project's outcome names.
let testsOf (outcome: ProjectOutcome) : TestId seq =
    match outcome with
    | Ran rows -> rows |> Map.toSeq |> Seq.map fst
    | RedUnnamed
    | TimedOut
    | ProvedNothing _ -> Seq.empty

/// Judge one record.
let judge (record: StaticRecord) : StaticJudgement =
    if record.Static = record.Ran then
        StaticRanEverything
    else
        // One pass: each project either holds its failures or verified nothing.
        let failures, unknown =
            record.Outcomes
            |> Map.fold
                (fun (failed, unknown) project outcome ->
                    match failuresOf project outcome with
                    | Ok found -> Set.union failed (Set.ofList found), unknown
                    | Error p -> failed, Set.add p unknown)
                (Set.empty, Set.empty)

        match NonEmptySet.ofSet unknown with
        | Some projects -> OutcomesUnknown projects
        | None ->
            let inside, outside = Set.partition (isInside record.Static) failures

            match NonEmptySet.ofSet inside, NonEmptySet.ofSet outside with
            | Some insideFailed, _ -> CaughtByStatic(insideFailed, outside)
            | None, Some outsideFailed -> MissedByStatic outsideFailed
            | None, None -> Green

/// A miss whose every outside failure is a test in `KnownFlaky`: no project-level failure,
/// no test without a flaky history. ADR-070 says how such a miss counts.
let isFlakyOnly (record: StaticRecord) (outsideFailed: NonEmptySet<Failure>) : bool =
    NonEmptySet.toSet outsideFailed
    |> Set.forall (fun failure ->
        match failure with
        | TestFailed test -> Set.contains test record.KnownFlaky
        | ProjectTimedOut _
        | ProjectRedUnnamed _ -> false)

/// Fold one test's report rows into its outcome: any passing row passed (the runner retried
/// it green), else any failed or unrecognised row failed, else it was skipped.
let private outcomeOfRows (rows: Flakiness.TestOutcome list) : TestOutcome =
    if rows |> List.contains Flakiness.Passed then
        Passed
    elif
        rows
        |> List.exists (fun row ->
            match row with
            | Flakiness.Failed
            | Flakiness.Other -> true
            | Flakiness.Passed
            | Flakiness.Skipped -> false)
    then
        Failed
    else
        Skipped

/// The rows one project's CTRF report names, by test.
let rowsOfReport (project: string) (json: string) : Map<TestId, TestOutcome> =
    let classes = Flakiness.parseCtrfDeclaredClasses json

    Flakiness.parseCtrfTests json
    |> List.groupBy _.Name
    |> List.map (fun (name, rows) ->
        let testClass, _ = splitTestName classes name

        { Project = project
          Class = testClass
          Name = name },
        outcomeOfRows (rows |> List.map _.Outcome))
    |> Map.ofList

/// What each project of a completed run established, from its result and the report it
/// wrote into the run's directory (`reportOf`, `None` when it wrote none).
let outcomesOf (reportOf: string -> string option) (results: Map<string, TestResult>) : Map<string, ProjectOutcome> =
    let rows project =
        reportOf project
        |> Option.map (rowsOfReport project)
        |> Option.defaultValue Map.empty

    results
    |> Map.map (fun project result ->
        match result with
        | TestsPassed _
        | TestsNoMatch _ -> Ran(rows project)
        | TestsFailed _ ->
            let named = rows project

            if named |> Map.exists (fun _ outcome -> outcome = Failed) then
                Ran named
            else
                RedUnnamed
        | TestsTimedOut _ -> TimedOut
        | TestsDeferred reason
        | TestsErrored reason -> ProvedNothing reason)

/// The CTRF report `project` wrote into `runId`'s directory, read.
let reportIn (repoRoot: string) (runId: Guid) (project: string) : string option =
    let path =
        Path.Combine(FsHotWatch.Ctrf.runDir repoRoot runId, project + FsHotWatch.Ctrf.ReportSuffix)

    if File.Exists path then
        Some(File.ReadAllText path)
    else
        None

/// The test names whose history (`Flakiness.loadHistory`) flipped between pass and fail.
let flakyNamesOf (history: Map<string, Flakiness.TestRunRecord list>) : Set<string> =
    history
    |> Map.filter (fun _ records -> Flakiness.computeFlakiness (records |> List.map _.Outcome) > 0.0)
    |> Map.keys
    |> Set.ofSeq

/// What a launch knows that its record needs, captured before any test runs: the two
/// selections and the flaky history as it stood.
type AtLaunch =
    { Static: Map<string, ProjectSelection>
      Ran: Map<string, ProjectSelection>
      FlakyNames: Set<string> }

/// The record of `run`, launched as `atLaunch`, over `tree`, with its outcomes.
let recordOf
    (atLaunch: AtLaunch)
    (tree: RecordedTree)
    (run: CompletedRun)
    (outcomes: Map<string, ProjectOutcome>)
    : StaticRecord =
    let knownFlaky =
        outcomes
        |> Map.values
        |> Seq.collect testsOf
        |> Seq.filter (fun test -> Set.contains test.Name atLaunch.FlakyNames)
        |> Set.ofSeq

    { Tree = tree
      RunId = CompletedRun.runId run
      Static = atLaunch.Static
      Ran = atLaunch.Ran
      Outcomes = outcomes
      KnownFlaky = knownFlaky }
