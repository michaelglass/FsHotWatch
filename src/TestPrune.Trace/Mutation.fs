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

/// One repository method, made to fail by throwing from every one of its probes; or one
/// union type, made to fail by throwing from every reflective use of it.
type Target =
    {
        /// `<CLR type>::<member>`, or a union type's CLR name.
        Name: string
        Ids: int list
        /// The CLR names of the union types whose reflection throws (`Contract.MutateTypesEnv`):
        /// empty for a method, the type itself for a union-type target.
        Types: string list
    }

/// What a test's trace holds: the probe ids it held or inherited, and the union types it
/// reflected over (`type` inputs).
type TestTrace = { Ids: Set<int>; Types: Set<string> }

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
        /// A method (symbol) or a union-type mutant.
        Kind: TraceStore.MutationKind
        Outcome: Outcome
        /// Tests that passed before and failed (or lost their row) with the mutant, sorted.
        Failing: string list
        /// How many tests trace selection selects for a change to the target.
        Selected: int
        /// Failing tests trace selection does not select, sorted.
        Missed: string list
    }

/// What a mutant's failing tests are judged against.
type Judging =
    /// The tests whose recorded probe ids or reflected unions include the target's: a lower
    /// bound on what symbol-level selection selects.
    | ByProbe
    /// The trace selection (`TraceSelect`, R1-R4 and R6) for "the target's symbol changed",
    /// over the baseline's ingested traces and the repository index.
    | BySymbol

/// The repository index a symbol-level judgement reads, and the root its paths are relative to.
type Index =
    { Symbols: TestPrune.Ports.SymbolStore
      Root: string }

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
        /// Changed methods (`run`'s `changed`) that no baseline test ran: a mutant of one could
        /// fail nothing, so each is recorded as not mutable and never sampled.
        NotMutable: string list
        /// What each mutant was judged against.
        Judging: Judging
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
          Ids = rows |> Seq.map _.Id |> Seq.sort |> List.ofSeq
          Types = [] })
    |> Seq.filter (fun t -> t.Ids |> List.exists hit.Contains)
    |> Seq.sortBy _.Name
    |> List.ofSeq

/// The CLR names of the union types the scopes reflected over (`type` inputs).
let reflectedTypes (scopes: RecordedScope seq) : Set<string> =
    scopes
    |> Seq.collect _.Inputs
    |> Seq.choose (fun i ->
        match i.Kind with
        | TypeReflected ->
            match i.Path.LastIndexOf '|' with
            | -1 -> None
            | cut -> Some(i.Path.Substring(cut + 1))
        | _ -> None)
    |> Set.ofSeq

/// The union types outside `testAssembly` (those with union-case rows in the manifest) that
/// `reflected` names. A union-type mutant throws on reflection over its type, so a union no
/// test reflected over could fail nothing. Sorted by name.
let unionTargets (testAssembly: string) (reflected: Set<string>) (manifest: Manifest) : Target list =
    manifest.Rows
    |> Seq.filter (fun r -> r.Kind = UnionCase && r.Assembly <> testAssembly)
    |> Seq.map _.TypeName
    |> Seq.distinct
    |> Seq.filter reflected.Contains
    |> Seq.sort
    |> Seq.map (fun name ->
        { Name = name
          Ids = []
          Types = [ name ] })
    |> List.ofSeq

/// The lines a unified diff changes, by repo-relative path, on the new side: the lines a
/// hunk adds or rewrites, or, for a hunk that only deletes, the two lines around the cut. A
/// deleted file has no new side and is left out. Read with no context (`-U0`) the ranges are
/// exactly the changed lines; with context they include it, which only widens the sample.
let changedSpans (diff: string) : Map<string, (int * int) list> =
    let hunk =
        Text.RegularExpressions.Regex(
            @"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@",
            Text.RegularExpressions.RegexOptions.Compiled
        )

    let folder (file: string option, acc: Map<string, (int * int) list>) (line: string) =
        if line.StartsWith("+++ ", StringComparison.Ordinal) then
            let path = line.Substring 4

            if path = "/dev/null" then
                None, acc
            else
                Some(
                    if path.StartsWith("b/", StringComparison.Ordinal) then
                        path.Substring 2
                    else
                        path
                ),
                acc
        else
            match file, hunk.Match line with
            | Some f, m when m.Success ->
                let start = int m.Groups.[1].Value
                let count = if m.Groups.[2].Success then int m.Groups.[2].Value else 1

                let span =
                    if count = 0 then
                        start, start + 1
                    else
                        start, start + count - 1

                file,
                acc
                |> Map.change f (fun spans -> Some((spans |> Option.defaultValue []) @ [ span ]))
            | _ -> file, acc

    diff.Split '\n' |> Array.fold folder (None, Map.empty) |> snd

/// The repository methods outside `testAssembly` (by `Type::member`, as `targets` names
/// them) whose source lines a change touches: some manifest row of the method lies in a
/// changed file and overlaps a changed span. Hit or not: a changed method no test ran is
/// still a change, and `run` reports it as not mutable.
let changedTargets
    (repoRoot: string)
    (testAssembly: string)
    (changed: Map<string, (int * int) list>)
    (manifest: Manifest)
    : Set<string> =
    manifest.Rows
    |> Seq.filter (fun r -> r.Kind = UserMethod && r.Assembly <> testAssembly)
    |> Seq.filter (fun r ->
        match r.Document with
        | None -> false
        | Some doc ->
            let rel = Path.GetRelativePath(repoRoot, doc).Replace('\\', '/')

            changed.TryFind rel
            |> Option.exists (List.exists (fun (a, b) -> r.FirstLine <= b && a <= r.LastLine)))
    |> Seq.map (fun r -> $"%s{r.TypeName}::%s{r.Member}")
    |> Set.ofSeq

/// Each baseline test's trace, by its CTRF name: every probe id its own scopes and the
/// scopes it inherits recorded (`RunScopes.inheritedBy`). A test is matched to its scopes
/// by display name, else by `Class.Method` against the name up to its argument list. `None`
/// for a test with no scope: it hit no probe, or ran where the recorder could not see it.
let tracesOf
    (manifest: Manifest)
    (dumps: ProcessDump list)
    (outcomes: TestOutcome list)
    : Map<string, TestTrace option> =
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
            let held =
                inherited (keys |> List.map snd) |> Seq.map (fun k -> scopes.[k]) |> List.ofSeq

            { Ids = held |> Seq.collect _.Ids |> Set.ofSeq
              Types = reflectedTypes held }))
    |> Map.ofList

/// The tests trace selection selects for a change to `target`: those whose trace names one
/// of its ids or reflected over one of its union types, and those with no trace.
let selected (traces: Map<string, TestTrace option>) (target: Target) : Set<string> =
    traces
    |> Map.filter (fun _ trace ->
        match trace with
        | None -> true
        | Some t ->
            target.Ids |> List.exists t.Ids.Contains
            || target.Types |> List.exists t.Types.Contains)
    |> Map.keys
    |> Set.ofSeq

/// Judge one mutant run against the baseline. A test that passed in the baseline fails
/// with the mutant when it failed, ended otherwise, or has no row in the mutant's report.
/// Judge one mutant run against the baseline and the CTRF names `selection` says a change to
/// the target selects.
let judgeWith
    (selection: Set<string>)
    (baseline: TestOutcome list)
    (target: Target)
    (mutant: Result<TestOutcome list, string>)
    : Sample =

    let sample outcome failing =
        { Target = target.Name
          Kind =
            if target.Types.IsEmpty then
                TraceStore.SymbolMutant
            else
                TraceStore.UnionTypeMutant
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

/// `judgeWith` the probe-level selection (`selected`).
let judge
    (traces: Map<string, TestTrace option>)
    (baseline: TestOutcome list)
    (target: Target)
    (mutant: Result<TestOutcome list, string>)
    : Sample =
    judgeWith (selected traces target) baseline target mutant

/// The trace key of each baseline test, by its CTRF name: the key ingestion stores its trace
/// under (`TraceIngest.testKey` of its scope's class and method), matched as `tracesOf` matches.
/// A test with no scope has no key.
let keysOf (testProject: string) (dumps: ProcessDump list) (outcomes: TestOutcome list) : Map<string, string> =
    let tests = RunScopes.merged dumps |> Map.values |> Seq.choose _.Test |> List.ofSeq

    let byDisplay = tests |> List.map (fun t -> t.Display, t) |> Map.ofList

    let byName =
        tests
        |> List.map (fun t -> FileCensus.normalise (t.Class + "." + t.Method), t)
        |> Map.ofList

    outcomes
    |> List.choose (fun o ->
        byDisplay.TryFind o.Name
        |> Option.orElse (byName.TryFind(FileCensus.normalise o.Name))
        |> Option.map (fun t -> o.Name, TraceIngest.testKey testProject t.Class t.Method))
    |> Map.ofList

/// The fallbacks of a symbol-level judgement: R6 over `seeds` (the index's static walk and
/// attributes, the facts stored under `fingerprint`); no file changed, so R7 adds nothing.
let symbolFallbacks
    (store: TraceStore.Store)
    (fingerprint: string)
    (testProject: string)
    (index: TestPrune.Ports.SymbolStore)
    (seeds: string list)
    : TraceSelect.Fallbacks =
    let keyOf (t: TestPrune.AstAnalyzer.TestMethodInfo) =
        TraceIngest.testKey t.TestProject t.TestClass (TestPrune.AstAnalyzer.canonicalShortName t.TestMethod)

    { Seeds = seeds
      ChangedFiles = []
      Facts = store.SymbolFacts(testProject, fingerprint)
      ClaimedInputs = []
      IsIndexed = fun _ -> true
      AttributesOf = index.GetAttributesForSymbol >> List.map fst
      StaticWalk = index.QueryAffectedTests >> List.map keyOf
      FileFallback = fun _ -> Some []
      FileGaps = Set.empty }

/// The universe keys trace selection selects when `seeds` changed and nothing else did: R1-R4
/// over the traces stored under `fingerprint` (each seed's version replaced), then R6 for a
/// seed the probes cannot represent, walking the index statically. Inputs are hashed against
/// `inputRoot`, the root the run's inputs are relative to.
let symbolSelection
    (store: TraceStore.Store)
    (fingerprint: string)
    (testProject: string)
    (index: TestPrune.Ports.SymbolStore)
    (inputRoot: string)
    (universe: string list)
    (seeds: string list)
    : Set<string> =
    let changed = Set.ofList seeds
    let versions = TraceIngest.versionHashes index

    let selection =
        TraceSelect.select
            testProject
            universe
            (fun key -> store.TryRead(key, fingerprint))
            (fun symbol ->
                if changed.Contains symbol then
                    Some "mutant"
                else
                    versions.TryFind symbol)
            (TraceIngest.currentInputHash inputRoot)
        |> TraceSelect.withFallbacks (symbolFallbacks store fingerprint testProject index seeds)

    selection.Selected |> Map.keys |> Set.ofSeq

/// A target's selection: `bySymbol` its seeds, or `byProbe` when it has none (nothing it names is
/// indexed).
let bySymbolOr (byProbe: Set<string>) (seeds: string list) (bySymbol: string list -> Set<string>) =
    match seeds with
    | [] -> byProbe
    | _ -> bySymbol seeds

/// The indexed symbols a target stands for: the symbols its probe ids join to, and its union
/// types' symbols. Empty when nothing it names is indexed.
let targetSymbols (ix: Joiner.SymbolIndex) (joined: Joiner.JoinTarget[]) (target: Target) : string list =
    (target.Ids
     |> List.choose (fun id ->
         match joined.[id] with
         | Joiner.ToSymbol name -> Some name
         | _ -> None))
    @ (target.Types
       |> List.choose (fun t -> Joiner.typeCandidates t |> List.tryFind ix.Exists))
    |> List.distinct

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
      Samples = samples
      NotMutable = []
      Judging = ByProbe }

/// The bar: some mutant failed a test, and trace selection missed none of them.
let passes (r: MutationReport) = r.Measured > 0 && r.MissedTotal = 0

/// An outcome's name, as the report and its JSON print it.
let outcomeName (o: Outcome) =
    match o with
    | Measured -> "measured"
    | NoFailure -> "no-failure"
    | RunFailed _ -> "run-failed"

/// One sample as plain data: its outcome by `outcomeName`, with a failed run's reason
/// beside it (null otherwise).
type SampleJson =
    { Target: string
      Kind: string
      Outcome: string
      Reason: string
      Failing: string list
      Selected: int
      Missed: string list }

/// The report as plain data for `JsonSerializer`, which cannot write an F# union.
type MutationReportJson =
    { Universe: int
      BaselineNotPassed: string list
      Targets: int
      Measured: int
      FailingTotal: int
      MissedTotal: int
      Recall: float option
      Samples: SampleJson list }

/// The report as plain data a JSON serializer can write.
let toJson (r: MutationReport) : MutationReportJson =
    { Universe = r.Universe
      BaselineNotPassed = r.BaselineNotPassed
      Targets = r.Targets
      Measured = r.Measured
      FailingTotal = r.FailingTotal
      MissedTotal = r.MissedTotal
      Recall = r.Recall
      Samples =
        r.Samples
        |> List.map (fun s ->
            { Target = s.Target
              Kind = TraceStore.mutationKindCode s.Kind
              Outcome = outcomeName s.Outcome
              Reason =
                match s.Outcome with
                | RunFailed why -> why
                | Measured
                | NoFailure -> null
              Failing = s.Failing
              Selected = s.Selected
              Missed = s.Missed }) }

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

    let judgedBy =
        match r.Judging with
        | ByProbe -> "probe"
        | BySymbol -> "symbol"

    line
        $"mutation  %s{verdict}  recall %s{recall}  samples %d{r.Samples.Length} (measured %d{r.Measured})  targets %d{r.Targets}  tests %d{r.Universe}  judged by %s{judgedBy}"

    if not r.NotMutable.IsEmpty then
        let names = String.concat ", " r.NotMutable
        line $"  not mutable: %d{r.NotMutable.Length} changed method(s) no test ran: %s{names}"

    if not r.BaselineNotPassed.IsEmpty then
        line $"  baseline: %d{r.BaselineNotPassed.Length} test(s) did not pass and are not judged"

    for s in r.Samples do
        let outcome =
            match s.Outcome with
            | RunFailed why -> $"%s{outcomeName s.Outcome}: %s{why}"
            | Measured
            | NoFailure -> outcomeName s.Outcome

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

/// `n` of `pool` by a `Random(seed)` shuffle (`Audit.choose`; all of them when `n` is at least
/// their number), sorted by name.
let private draw (n: int) (seed: int) (pool: Target list) : Target list =
    if pool.IsEmpty || n <= 0 then
        []
    else
        let byName = pool |> List.map (fun t -> t.Name, t) |> Map.ofList

        Audit.choose (min 1.0 (float n / float pool.Length)) seed (pool |> List.map _.Name)
        |> List.truncate n
        |> List.sort
        |> List.map (fun name -> byName.[name])

/// The mutant's environment additions: the target's probe ids throw, and reflection over its
/// union types throws.
let private mutantEnv (target: Target) =
    [ if not target.Ids.IsEmpty then
          Contract.MutateEnv, target.Ids |> List.map string |> String.concat ","
      if not target.Types.IsEmpty then
          Contract.MutateTypesEnv, String.concat ";" target.Types ]

/// Prepare the project, run it once traced (the baseline), draw `samples` method targets from
/// what it hit and `unionSamples` union-type targets from the unions it reflected over (each by
/// a `Random(seed)` shuffle, `Audit.choose`; every target when the count is at least their
/// number), and run one mutant per target. `only`, when non-empty, keeps the targets whose
/// name contains one of its strings before sampling; `changed` (`changedSpans` of a real
/// change), when given, keeps the methods it touched (`changedTargets`) and reports the touched
/// methods no test ran as not mutable. A union-type target stands for adding a case to the
/// union, which no diff shows, so `changed` does not filter them. `Error` when the project
/// cannot be prepared or the baseline wrote no test report.
let runWith
    (req: TraceSession.PrepareRequest)
    (appArgs: string list)
    (samples: int)
    (unionSamples: int)
    (seed: int)
    (only: string list)
    (changed: Map<string, (int * int) list> option)
    (timeout: TimeSpan)
    (index: Index option)
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
            let merged = RunScopes.merged dumps
            let hit = merged |> Map.values |> Seq.collect _.Ids |> Set.ofSeq

            let keep (t: Target) =
                only.IsEmpty || only |> List.exists t.Name.Contains

            // With a change, only the methods it touched are candidates.
            let changedNames =
                changed
                |> Option.map (fun c -> changedTargets req.RepoRoot req.AssemblyName c manifest)

            let methods =
                targets req.AssemblyName hit manifest
                |> List.filter keep
                |> List.filter (fun t -> changedNames |> Option.forall (fun names -> Set.contains t.Name names))

            let unions =
                unionTargets req.AssemblyName (reflectedTypes (Map.values merged)) manifest
                |> List.filter keep

            let chosen = draw samples seed methods @ draw unionSamples seed unions
            let traces = tracesOf manifest dumps baseline

            // With an index, each mutant is judged against the trace selection for "its symbol
            // changed", over the baseline's traces ingested into a scratch store.
            let bySymbol =
                index
                |> Option.bind (fun ix ->
                    let store = TraceStore.Store.Open(Path.Combine(dir, "baseline", "traces.db"))

                    let summary =
                        TraceIngest.ingest
                            store
                            { RunId = "baseline"
                              TestProject = req.TestProject
                              RepoRoot = ix.Root
                              InputRoot = launch.InputRoot
                              Kind = TraceStore.FullRun
                              LaunchTreeHash = ""
                              CurrentTreeHash = ""
                              DumpDir = baselineDumps
                              Shadow = launch.Shadow
                              Outcomes = baseline
                              Symbols = ix.Symbols
                              FingerprintFiles = []
                              FingerprintEnv = []
                              RecordedAt = DateTimeOffset.UtcNow }

                    summary.EnvFingerprint
                    |> Option.map (fun fingerprint ->
                        let symbolIndex = Joiner.ofStore ix.Symbols ix.Root
                        let joined = Joiner.joinManifest symbolIndex manifest
                        let keys = keysOf req.TestProject dumps baseline
                        let universe = keys |> Map.values |> Seq.distinct |> List.ofSeq

                        fun (target: Target) ->
                            bySymbolOr
                                (selected traces target)
                                (targetSymbols symbolIndex joined target)
                                (fun seeds ->
                                    let chosen =
                                        symbolSelection
                                            store
                                            fingerprint
                                            req.TestProject
                                            ix.Symbols
                                            launch.InputRoot
                                            universe
                                            seeds

                                    // A test with no key has no trace: R1 selects it.
                                    baseline
                                    |> List.map _.Name
                                    |> List.filter (fun name -> keys.TryFind name |> Option.forall chosen.Contains)
                                    |> Set.ofList)))

            let selectionOf (target: Target) =
                match bySymbol with
                | Some select -> select target
                | None -> selected traces target

            let judged =
                chosen
                |> List.mapi (fun i target ->
                    let at = Path.Combine(dir, string i)

                    Ctrf.run
                        $"with %s{target.Name} mutated"
                        launch.Apphost
                        appArgs
                        (envWith launch (Path.Combine(at, "dumps")) (mutantEnv target))
                        req.RepoRoot
                        (Path.Combine(at, "results"))
                        timeout
                    |> judgeWith (selectionOf target) baseline target)

            { summarize
                  baseline.Length
                  (notPassed baseline)
                  (methods.Length + (if unionSamples > 0 then unions.Length else 0))
                  judged with
                NotMutable =
                    match changedNames with
                    | None -> []
                    | Some names ->
                        Set.difference names (targets req.AssemblyName hit manifest |> List.map _.Name |> Set.ofList)
                        |> Set.toList
                Judging = if bySymbol.IsSome then BySymbol else ByProbe }))

/// `runWith` judging by probe: no index.
let run req appArgs samples unionSamples seed only changed timeout =
    runWith req appArgs samples unionSamples seed only changed timeout None

/// The samples a report stores (`TraceStore.MutationSample`), one per judged target (of its
/// kind: symbol or union-type) plus one symbol `NotMutable` per changed method no test ran.
/// `treeHash` names what was mutated (the tree, or the change it was sampled from).
let toSamples (testProject: string) (at: DateTimeOffset) (treeHash: string) (r: MutationReport) =
    let sample kind symbol outcome failing selected missed : TraceStore.MutationSample =
        { TestProject = testProject
          Kind = kind
          Symbol = symbol
          TreeHash = treeHash
          RecordedAt = at
          Outcome = outcome
          Failing = failing
          Selected = selected
          Missed = missed }

    (r.Samples
     |> List.map (fun s ->
         let outcome =
             match s.Outcome with
             | Measured -> TraceStore.Measured
             | NoFailure -> TraceStore.NoFailure
             | RunFailed _ -> TraceStore.RunFailed

         sample s.Kind s.Target outcome s.Failing.Length s.Selected s.Missed))
    @ (r.NotMutable
       |> List.map (fun n -> sample TraceStore.SymbolMutant n TraceStore.NotMutable 0 0 []))
