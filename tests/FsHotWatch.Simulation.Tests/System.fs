/// The system under simulation, and the engine that drives it.
///
/// The SYSTEM is today's lookup half of the verdict, called exactly as the CLI calls it:
/// `ShadowVerdict.observeWith` at a publication (it projects the run into outcome records,
/// files them, computes R(c, T) from the configuration and looks the tree up) and
/// `ShadowVerdict.observeReadWith` at a `fshw verdict` read, over a real `OutcomeStore` on a
/// temporary root. Nothing of it is reimplemented here.
///
/// The ENGINE stands in for the daemon: it hashes the tree at the start and the end of a
/// run (a moved tree makes the publication incomplete, as today's staleness check does),
/// selects test projects for a `check` from what changed and what failed last (today's
/// selector and outstanding-failure ledger, which survive a restart because they are files),
/// and decides each run's outcomes from the tree the run actually read.
///
/// A MUTANT is a known defect class planted in one of the two. A harness that cannot see the
/// defects already found in production has no teeth, so each must be caught.
module FsHotWatch.Simulation.System

open System
open System.IO
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Simulation.Model

/// A planted defect.
[<RequireQualifiedAccess>]
type Mutant =
    /// The tree key leaves this file's bytes out (ADR-059 Chain C: a key omits an input).
    | KeyOmitsFile of path: string
    /// A run whose tree moved while it ran still concludes, under the tree it started at.
    | NoStalenessCheck
    /// A check's selection forgets a project that failed (the outstanding-failure ledger lost).
    | SelectorForgetsFailures
    /// A lookup reads a recorded fail as a pass.
    | FailReadAsPass
    /// A lookup reads a key with no record as a pass (a partial filing read as green).
    | MissingReadAsPass
    /// A test project's K_P (its evidence key over the inputs its run reads) leaves this file out.
    | KpOmitsFile of path: string

/// The system the simulation drives: today's, or today's with one planted defect.
[<RequireQualifiedAccess>]
type Subject =
    | Current
    | Mutated of Mutant

/// What a configuration requires: every simulated plugin and test project.
let configured: ShadowVerdict.Configured =
    { RequiredPlugins = plugins
      TestProjects = testProjects }

/// The engine's persisted selection state: what each test project's inputs were when it last
/// ran, and the projects whose last run failed. Today these are files, so a restart keeps them.
type SelectorState =
    { LastRunInputs: Map<string, string>
      Outstanding: Set<string> }

module SelectorState =
    let cold =
        { LastRunInputs = Map.empty
          Outstanding = Set.empty }

/// The store over `root`: the same type and settings the CLI opens, logging nowhere.
let openStore (root: string) (maxBytes: int64) : OutcomeStore.Store =
    OutcomeStore.Store(
        { Root = root
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = maxBytes
          Log = ignore }
    )

/// The tree key the engine computes for `tree`.
let treeKey (subject: Subject) (tree: Tree) : string =
    match subject with
    | Subject.Mutated(Mutant.KeyOmitsFile path) -> digestOf (paths |> List.filter ((<>) path)) tree
    | Subject.Current
    | Subject.Mutated _ -> treeDigest tree

/// The two stores a lookup reads: the publication's (the shadow store under the checkout) and the
/// box-wide one the test runner files K_P records in.
type Stores =
    { Shadow: OutcomeStore.Store
      Kp: OutcomeStore.Store }

/// A test project's K_P over `tree`: a key over exactly the inputs its run reads, so a run of
/// another tree with the same inputs answers for this one (ADR-059 step 7).
let kpKey (subject: Subject) (testProject: string) (tree: Tree) : ActionKey.ActionKey =
    let inputs =
        match subject with
        | Subject.Mutated(Mutant.KpOmitsFile path) -> inputsOf testProject |> List.filter ((<>) path)
        | Subject.Current
        | Subject.Mutated _ -> inputsOf testProject

    ActionKey.create "test-kp" [ "project", testProject; "inputs", digestOf inputs tree ]

/// Whether the engine notices that the tree moved between `started` and `ended`.
let noticesMove (subject: Subject) (started: Tree) (ended: Tree) : bool =
    match subject with
    | Subject.Mutated Mutant.NoStalenessCheck -> false
    | Subject.Current
    | Subject.Mutated _ -> treeKey subject started <> treeKey subject ended

/// The test projects a run of `command` selects over `tree`, and the scope it is graded in.
let select (command: Verdict.Command) (state: SelectorState) (tree: Tree) : string list * ShadowVerdict.GradedScope =
    match command with
    | Verdict.Command.Confirm -> testProjects, ShadowVerdict.GradedScope.FullSuite
    | Verdict.Command.Check when Map.isEmpty state.LastRunInputs -> testProjects, ShadowVerdict.GradedScope.FullSuite
    | Verdict.Command.Check ->
        let selected =
            testProjects
            |> List.filter (fun tp ->
                state.Outstanding.Contains tp
                || Map.tryFind tp state.LastRunInputs <> Some(digestOf (inputsOf tp) tree))

        selected, ShadowVerdict.GradedScope.ImpactFiltered

/// The selection state after `selected` ran over `read`.
let afterRun (subject: Subject) (state: SelectorState) (selected: string list) (read: Tree) : SelectorState =
    let failed = selected |> List.filter (fun tp -> testFails tp read) |> Set.ofList

    let outstanding =
        match subject with
        | Subject.Mutated Mutant.SelectorForgetsFailures -> Set.empty
        | Subject.Current
        | Subject.Mutated _ -> state.Outstanding - Set.ofList selected + failed

    { LastRunInputs =
        selected
        |> List.fold (fun m tp -> Map.add tp (digestOf (inputsOf tp) read) m) state.LastRunInputs
      Outstanding = outstanding }

let private suiteOf (runId: string) (read: Tree) (testProject: string) : ShadowVerdict.Suite =
    let fails = testFails testProject read
    let failed = if fails then [ testProject + ".broken" ] else []

    { RunId = Some runId
      Project = testProject
      Counts =
        { Total = 3
          Passed = 3 - failed.Length
          Failed = failed.Length
          Skipped = 0 }
      Passed = not fails
      FailedTests = Some failed
      PassedTests = set [ testProject + ".one"; testProject + ".two" ]
      Report = None }

/// One run of the engine, as far as a publication can see it.
type Run =
    {
        Command: Verdict.Command
        /// The tree key, computed when the run started.
        Key: string
        /// The tree the run started at, which its key claims to describe.
        Started: Tree
        /// The tree the run's plugins and tests actually read.
        Read: Tree
        Selected: string list
        Scope: ShadowVerdict.GradedScope
        RunId: string
    }

/// The publication `run` makes, as `ShadowVerdict.ofVerdict` would project it. A run whose
/// tree moved under it publishes an incomplete verdict, which files nothing about the tree.
let observation
    (subject: Subject)
    (run: Run)
    (endedAt: Tree)
    (producer: string)
    (worktree: string)
    (now: DateTimeOffset)
    : ShadowVerdict.Observation =
    let failing = cleanFailing run.Read
    let suites = run.Selected |> List.map (suiteOf run.RunId run.Read)

    let pluginOutcomes =
        [ for name in plugins ->
              name,
              Some(
                  if failing.Contains("plugin:" + name) then
                      OutcomeStore.Outcome.Fail
                  else
                      OutcomeStore.Outcome.Pass
              ) ]

    let diagnosticsFailed = failing.Contains "diagnostics"

    let anyFailed =
        diagnosticsFailed
        || pluginOutcomes |> List.exists (snd >> (=) (Some OutcomeStore.Outcome.Fail))
        || suites |> List.exists (fun s -> not s.Passed)

    let today =
        if noticesMove subject run.Started endedAt then
            ShadowVerdict.Class.Missing
        elif anyFailed then
            ShadowVerdict.Class.Red
        else
            ShadowVerdict.Class.Green

    { Command = run.Command
      Tree = run.Key
      Producer = producer
      Worktree = worktree
      Today = today
      TodayOutcome = ShadowVerdict.Class.token today
      ExitCode =
        match today with
        | ShadowVerdict.Class.Green -> 0
        | ShadowVerdict.Class.Red -> 1
        | ShadowVerdict.Class.Missing -> 3
      Plugins = pluginOutcomes
      DiagnosticsFailed = diagnosticsFailed
      GradedRunId = Some run.RunId
      BaselineRunId = None
      Scope = run.Scope
      Suites = suites
      RunOrder = [ run.RunId ]
      RecordedAt = now
      OutcomeKeys =
        testProjects
        |> List.map (fun tp -> tp, (kpKey subject tp run.Started).Value)
        |> Map.ofList }

/// What the test runner files in the K_P store for `run`: a full run of each project it ran,
/// under the K_P of the tree the run READ. An impact-filtered run files nothing there.
let fileKp (subject: Subject) (stores: Stores) (run: Run) (producer: string) (worktree: string) (now: DateTimeOffset) =
    match run.Scope with
    | ShadowVerdict.GradedScope.FullSuite ->
        for tp in run.Selected do
            let outcome =
                if testFails tp run.Read then
                    OutcomeStore.Outcome.Fail
                else
                    OutcomeStore.Outcome.Pass

            stores.Kp.Record(OutcomeStore.Record.ofAction (kpKey subject tp run.Read) tp outcome producer worktree now)
            |> ignore
    | ShadowVerdict.GradedScope.ImpactFiltered
    | ShadowVerdict.GradedScope.Unscoped -> ()

/// The observation a `fshw verdict` read makes of `tree`: the requirements come from the
/// configuration, so nothing of a run is needed.
let readObservation
    (command: Verdict.Command)
    (key: string)
    (producer: string)
    (worktree: string)
    (now: DateTimeOffset)
    : ShadowVerdict.Observation =
    { Command = command
      Tree = key
      Producer = producer
      Worktree = worktree
      Today = ShadowVerdict.Class.Missing
      TodayOutcome = "read"
      ExitCode = 3
      Plugins = []
      DiagnosticsFailed = false
      GradedRunId = None
      BaselineRunId = None
      Scope = ShadowVerdict.GradedScope.Unscoped
      Suites = []
      RunOrder = []
      RecordedAt = now
      OutcomeKeys = Map.empty }

/// The answer for one key, as today's lookup reads it for `command`: the shadow store's, else
/// the K_P store's (`ShadowVerdict.lookupIn`).
let currentLookup (command: Verdict.Command) (stores: Stores) (key: ActionKey.KeyValue) =
    let readIn (store: OutcomeStore.Store) =
        match command with
        | Verdict.Command.Check -> TestFailureClearance.lookupForCheck store key
        | Verdict.Command.Confirm -> store.Lookup key |> Option.map _.Outcome

    readIn stores.Shadow |> Option.orElseWith (fun () -> readIn stores.Kp)

let private mutatedLookup (subject: Subject) (command: Verdict.Command) (stores: Stores) =
    let current = currentLookup command stores

    match subject with
    | Subject.Mutated Mutant.FailReadAsPass ->
        fun key ->
            match current key with
            | Some OutcomeStore.Outcome.Fail -> Some OutcomeStore.Outcome.Pass
            | other -> other
    | Subject.Mutated Mutant.MissingReadAsPass ->
        fun key -> current key |> Option.orElse (Some OutcomeStore.Outcome.Pass)
    | Subject.Current
    | Subject.Mutated _ -> current

/// The lookup verdict of `o` against `stores`, recomputed from R(c, T) with `subject`'s lookup.
/// A read names no K_P keys; as `observeReadWith` does, it takes those earlier publications named.
let decide (subject: Subject) (stores: Stores) (o: ShadowVerdict.Observation) =
    let o =
        ShadowVerdict.withRecordedOutcomeKeys stores.Shadow configured.TestProjects o

    LookupVerdict.decide (ShadowVerdict.requirements o configured) (mutatedLookup subject o.Command stores)

/// A publication: today's shadow files the run and answers. A lookup mutant answers again
/// over what today filed, with its defect.
let publish (subject: Subject) (repoRoot: string) (stores: Stores) (o: ShadowVerdict.Observation) =
    let answer =
        ShadowVerdict.observeWith repoRoot stores.Shadow (Some stores.Kp) configured o

    match subject with
    | Subject.Mutated Mutant.FailReadAsPass
    | Subject.Mutated Mutant.MissingReadAsPass -> decide subject stores o
    | Subject.Current
    | Subject.Mutated _ -> answer

/// A `fshw verdict` read, answered as `publish` answers. As the CLI does (`observeRead`), it
/// first takes the K_P keys earlier publications named for this tree.
let read (subject: Subject) (repoRoot: string) (stores: Stores) (o: ShadowVerdict.Observation) =
    let o =
        ShadowVerdict.withRecordedOutcomeKeys stores.Shadow configured.TestProjects o

    let answer =
        ShadowVerdict.observeReadWith repoRoot stores.Shadow (Some stores.Kp) configured o

    match subject with
    | Subject.Mutated Mutant.FailReadAsPass
    | Subject.Mutated Mutant.MissingReadAsPass -> decide subject stores o
    | Subject.Current
    | Subject.Mutated _ -> answer

/// File the first `count` records a publication of `o` would file, and stop: the process
/// was killed between two writes.
let fileKilled (store: OutcomeStore.Store) (o: ShadowVerdict.Observation) (count: int) : unit =
    if ShadowVerdict.concluded o then
        ShadowVerdict.recordsOf o configured.TestProjects
        |> List.truncate count
        |> List.iter (store.Record >> ignore)

/// Every record file under `root`, in a stable order.
let recordFiles (root: string) : string list =
    let records = Path.Combine(root, "records")

    // The store's fixed layout, one level at a time: records/<shard>/<key>/<record>.json.
    if Directory.Exists records then
        [ for shard in Directory.GetDirectories records do
              for key in Directory.GetDirectories shard do
                  yield! Directory.GetFiles(key, "*.json") ]
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
    else
        []
