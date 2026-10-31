/// The mutation harness: does trace-based selection select every test a change breaks?
///
/// Run the woven app once to record each test's trace (the probe ids it held or inherited).
/// Then, per sampled target (one repository method, all of its probe ids), run it again with
/// those probes throwing (`Contract.MutateEnv`): the method now fails as if it had a bug.
/// Every test that passed before and fails now depends on the target, so a change to the
/// target must select it. Trace selection selects a test when its trace names one of the
/// target's ids, or when it has no trace at all. A failing test it does not select is a
/// miss: the trace said the test never ran the target, and the target broke it anyway
/// (code that ran in no test's scope, such as test discovery or a type initializer no test
/// inherits, is the expected cause).
///
/// The harness works on probe ids, not on joined symbols: a method's closures are their own
/// targets, so its recall is a lower bound on what symbol-level selection (which joins a
/// closure to the method that holds it) achieves. Self-contained: it needs no trace store
/// and no symbol index.
module TestPrune.Trace.Mutation

open System
open System.IO
open System.Text
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

/// One repository method, made to fail by throwing from every one of its probes.
type Target =
    {
        /// `<CLR type>::<member>`
        Name: string
        Ids: int list
    }

/// What one mutant showed.
type Outcome =
    /// At least one test that passed before failed with the mutant.
    | Measured
    /// No test changed outcome: nothing depends on the target observably.
    | NoFailure
    /// The mutant run wrote no test report; nothing is judged.
    | RunFailed of reason: string

/// One sampled target and what its mutant broke.
type Sample =
    {
        Target: string
        Outcome: Outcome
        /// Tests that passed before and failed (or lost their row) with the mutant, sorted.
        Failing: string list
        /// How many tests trace selection selects for a change to the target.
        Selected: int
        /// Failing tests trace selection does not select, sorted.
        Missed: string list
    }

/// The harness's report over one project.
type MutationReport =
    {
        /// Tests in the baseline run.
        Universe: int
        /// Tests that did not pass in the baseline run; they cannot show a mutant's failure.
        BaselineNotPassed: string list
        /// Targets the baseline run hit, from which the samples were drawn.
        Targets: int
        Samples: Sample list
    }

    /// Samples whose mutant failed at least one test.
    member this.Measured =
        this.Samples |> List.filter (fun s -> s.Outcome = Measured) |> List.length

    /// Failing tests, summed over the samples.
    member this.FailingTotal = this.Samples |> List.sumBy _.Failing.Length

    /// Missed tests, summed over the samples.
    member this.MissedTotal = this.Samples |> List.sumBy _.Missed.Length

    /// Caught failing tests over failing tests; `None` when no mutant failed a test.
    member this.Recall =
        if this.FailingTotal = 0 then
            None
        else
            Some(float (this.FailingTotal - this.MissedTotal) / float this.FailingTotal)

/// Every repository method the baseline run hit (`hit`: every id recorded in any scope,
/// ambient and static init included), outside `testAssembly`, with all of its probe ids.
/// A target nothing hit cannot throw, so it could never fail a test. Sorted by name.
let targets (testAssembly: string) (hit: Set<int>) (manifest: Manifest) : Target list =
    manifest.Rows
    |> Seq.filter (fun r -> r.Kind = UserMethod && r.Assembly <> testAssembly)
    |> Seq.groupBy (fun r -> $"%s{r.TypeName}::%s{r.Member}")
    |> Seq.map (fun (name, rows) ->
        { Name = name
          Ids = rows |> Seq.map _.Id |> Seq.sort |> List.ofSeq })
    |> Seq.filter (fun t -> t.Ids |> List.exists hit.Contains)
    |> Seq.sortBy _.Name
    |> List.ofSeq

/// Each baseline test's trace, by its CTRF name: every probe id its own scopes and the
/// scopes it inherits recorded (`RunScopes.inheritedBy`). A test is matched to its scopes
/// by display name, else by `Class.Method` against the name up to its argument list. `None`
/// for a test with no scope: it hit no probe, or ran where the recorder could not see it.
let tracesOf
    (manifest: Manifest)
    (dumps: ProcessDump list)
    (outcomes: TestOutcome list)
    : Map<string, Set<int> option> =
    let scopes = RunScopes.merged dumps
    let inherited = RunScopes.inheritedBy manifest scopes

    let tests =
        scopes
        |> Map.toList
        |> List.choose (fun (key, s) -> s.Test |> Option.map (fun t -> t, key))

    let byDisplay = tests |> List.groupBy (fst >> _.Display) |> Map.ofList

    let byName =
        tests
        |> List.groupBy (fun (t, _) -> FileCensus.normalise (t.Class + "." + t.Method))
        |> Map.ofList

    outcomes
    |> List.map (fun o ->
        let own =
            byDisplay.TryFind o.Name
            |> Option.orElse (byName.TryFind(FileCensus.normalise o.Name))

        o.Name,
        own
        |> Option.map (fun keys ->
            inherited (keys |> List.map snd)
            |> Seq.collect (fun k -> scopes.[k].Ids)
            |> Set.ofSeq))
    |> Map.ofList

/// The tests trace selection selects for a change to `target`: those whose trace names one
/// of its ids, and those with no trace.
let selected (traces: Map<string, Set<int> option>) (target: Target) : Set<string> =
    traces
    |> Map.filter (fun _ trace ->
        match trace with
        | None -> true
        | Some ids -> target.Ids |> List.exists ids.Contains)
    |> Map.keys
    |> Set.ofSeq

/// Judge one mutant run against the baseline. A test that passed in the baseline fails
/// with the mutant when it failed, ended otherwise, or has no row in the mutant's report.
let judge
    (traces: Map<string, Set<int> option>)
    (baseline: TestOutcome list)
    (target: Target)
    (mutant: Result<TestOutcome list, string>)
    : Sample =
    let selection = selected traces target

    let sample outcome failing =
        { Target = target.Name
          Outcome = outcome
          Failing = failing
          Selected = selection.Count
          Missed = failing |> List.filter (selection.Contains >> not) }

    match mutant with
    | Error why -> sample (RunFailed why) []
    | Ok rows ->
        let now = rows |> List.map (fun o -> o.Name, o.Outcome) |> Map.ofList

        let failing =
            baseline
            |> List.filter (fun o ->
                o.Outcome = Passed
                && (match now.TryFind o.Name with
                    | Some(Failed | OtherOutcome)
                    | None -> true
                    | Some _ -> false))
            |> List.map _.Name
            |> List.distinct
            |> List.sort

        sample (if failing.IsEmpty then NoFailure else Measured) failing

/// The tests of a run that did not pass, sorted: a mutant cannot show their failure.
let notPassed (outcomes: TestOutcome list) : string list =
    outcomes
    |> List.filter (fun o -> o.Outcome <> Passed)
    |> List.map _.Name
    |> List.sort

/// The report over the samples.
let summarize (universe: int) (baselineNotPassed: string list) (targetCount: int) (samples: Sample list) =
    { Universe = universe
      BaselineNotPassed = baselineNotPassed
      Targets = targetCount
      Samples = samples }

/// The bar: some mutant failed a test, and trace selection missed none of them.
let passes (r: MutationReport) = r.Measured > 0 && r.MissedTotal = 0

/// The human report: the verdict and recall, each sample, then every miss by name.
let render (r: MutationReport) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore

    let verdict =
        if r.Measured = 0 then "NOTHING MEASURED"
        elif passes r then "PASS"
        else "FAIL"

    let recall =
        match r.Recall with
        | Some v -> $"%d{r.FailingTotal - r.MissedTotal}/%d{r.FailingTotal} = %.4f{v}"
        | None -> "n/a"

    line
        $"mutation  %s{verdict}  recall %s{recall}  samples %d{r.Samples.Length} (measured %d{r.Measured})  targets %d{r.Targets}  tests %d{r.Universe}"

    if not r.BaselineNotPassed.IsEmpty then
        line $"  baseline: %d{r.BaselineNotPassed.Length} test(s) did not pass and are not judged"

    for s in r.Samples do
        let outcome =
            match s.Outcome with
            | Measured -> "measured"
            | NoFailure -> "no-failure"
            | RunFailed why -> $"run-failed: %s{why}"

        line
            $"  %s{s.Target}  %s{outcome}  failing %d{s.Failing.Length}  selected %d{s.Selected}  missed %d{s.Missed.Length}"

    for s in r.Samples do
        for m in s.Missed do
            line $"  missed  %s{m}  by %s{s.Target}"

    sb.ToString()

/// The launch's environment with dumps going to `dumpDir` and `extra` added.
let private envWith (launch: TraceSession.TraceLaunch) (dumpDir: string) (extra: (string * string) list) =
    Directory.CreateDirectory dumpDir |> ignore

    (launch.Env
     |> List.map (fun (k, v) -> if k = Contract.OutEnv then k, dumpDir else k, v))
    @ extra

/// Prepare the project, run it once traced (the baseline), draw `samples` targets from what
/// it hit by a `Random(seed)` shuffle (`Audit.choose`; every target when `samples` is at
/// least their number), and run one mutant per target. `only`, when non-empty, keeps the
/// targets whose name contains one of its strings before sampling. `Error` when the
/// project cannot be prepared or the baseline wrote no test report.
let run
    (req: TraceSession.PrepareRequest)
    (appArgs: string list)
    (samples: int)
    (seed: int)
    (only: string list)
    (timeout: TimeSpan)
    : Result<MutationReport, string> =
    TraceSession.prepareProject req
    |> Result.bind (fun launch ->
        let dir = Path.Combine(req.RunDir, "mutation")
        let baselineDumps = Path.Combine(dir, "baseline", "dumps")

        Ctrf.run
            "baseline"
            launch.Apphost
            appArgs
            (envWith launch baselineDumps [])
            req.RepoRoot
            (Path.Combine(dir, "baseline", "results"))
            timeout
        |> Result.map (fun baseline ->
            let manifest = launch.Shadow.Manifest
            let dumps, _ = DumpReader.readDirectory baselineDumps

            let hit = RunScopes.merged dumps |> Map.values |> Seq.collect _.Ids |> Set.ofSeq

            let all =
                targets req.AssemblyName hit manifest
                |> List.filter (fun t -> only.IsEmpty || only |> List.exists t.Name.Contains)

            let byName = all |> List.map (fun t -> t.Name, t) |> Map.ofList

            let fraction =
                if all.IsEmpty then
                    0.0
                else
                    float samples / float all.Length

            let chosen =
                if all.IsEmpty then
                    []
                else
                    Audit.choose (min 1.0 fraction) seed (all |> List.map _.Name)
                    |> List.truncate samples
                    |> List.sort
                    |> List.map (fun n -> byName.[n])

            let traces = tracesOf manifest dumps baseline

            let judged =
                chosen
                |> List.mapi (fun i target ->
                    let at = Path.Combine(dir, string i)
                    let ids = target.Ids |> List.map string |> String.concat ","

                    Ctrf.run
                        $"with %s{target.Name} mutated"
                        launch.Apphost
                        appArgs
                        (envWith launch (Path.Combine(at, "dumps")) [ Contract.MutateEnv, ids ])
                        req.RepoRoot
                        (Path.Combine(at, "results"))
                        timeout
                    |> judge traces baseline target)

            summarize baseline.Length (notPassed baseline) all.Length judged))
