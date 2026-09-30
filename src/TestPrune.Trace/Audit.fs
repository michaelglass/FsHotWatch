/// The isolated-vs-parallel audit: run a woven project once with its tests in parallel,
/// then a seeded sample of its tests one at a time, and compare each sampled test's OWN
/// probe ids. An id the parallel run attributed to a test that the test never hits alone
/// is contamination from a concurrent test; the bar is none. Ids a test's own and inherited
/// scopes hit alone but not in parallel are expected for once-per-process code (static
/// constructors, closure singletons), which another test ran first; the rest are listed
/// for a human. A missing type initializer is expected only when the test inherits that
/// initializer's static-init scope in parallel: otherwise what the initializer recorded is
/// in no scope the test depends on, and the audit fails.
///
/// The audit compares ids, not symbols: a join could hide an attribution error.
module TestPrune.Trace.Audit

open System
open System.IO
open System.Text
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

/// One sampled test.
type TestAudit =
    {
        /// The test's display name (a theory row is its own test).
        Display: string
        /// Ids the parallel run attributed to the test that its isolated run never hit.
        Extra: int list
        /// Ids the isolated run hit that the parallel run did not, per manifest kind
        /// (`user`, `gen`, `cctor`, `case`, `type`; `unknown` for an id with no row).
        MissingByKind: Map<string, int>
        /// `type::member` of every missing id that is not `cctor` or `gen`, for review.
        MissingUser: string list
        /// The type of every missing initializer whose static-init scope the parallel run
        /// recorded but the test does not inherit there.
        UncoveredInit: string list
        /// False when the isolated run recorded no scope for the test: the test hit no
        /// probe alone, or the test could not be run alone at all (`IsolationError`).
        IsolatedFound: bool
        /// Why the test could not be run alone (`could not isolate <test>: …`): the filter
        /// selected no test, or the run wrote no report. Nothing is compared for such a
        /// test, so it has no extra or missing ids; the audit is an error, not a pass.
        IsolationError: string option
    }

/// The audit of one project.
type AuditReport =
    {
        Sampled: int
        /// Extra ids summed over the sampled tests.
        ExtraTotal: int
        Tests: TestAudit list
        /// Tests of the parallel run per incomplete reason the dumps alone show (an
        /// untraced child process, recorder overflow, a rejected dump), by full reason
        /// code. Reasons that need the index or the outcomes are the census's.
        Incomplete: Map<string, int>
        /// The exit code of the parallel run the sampled tests are compared against.
        ParallelExitCode: int
        /// That run's output (the tail `Launch.run` keeps) when it exited nonzero; empty
        /// otherwise.
        ParallelOutput: string
    }

/// What one test recorded in one run: its own scopes' probe ids, and the ids and keys of
/// every scope it holds or inherits (`RunScopes.inheritedBy`).
type Observed =
    { Own: Set<int>
      All: Set<int>
      Scopes: Set<string> }

/// Each test's `Observed`, by display name.
let observe (manifest: Manifest) (dumps: ProcessDump list) : Map<string, Observed> =
    let scopes = RunScopes.merged dumps
    let inherited = RunScopes.inheritedBy manifest scopes

    let idsOf (keys: string seq) =
        keys |> Seq.collect (fun k -> scopes.[k].Ids) |> Set.ofSeq

    scopes
    |> Map.toList
    |> List.choose (fun (key, s) -> s.Test |> Option.map (fun t -> t.Display, key))
    |> List.groupBy fst
    |> List.map (fun (display, group) ->
        let own = group |> List.map snd
        let all = inherited own

        display,
        { Own = idsOf own
          All = idsOf all
          Scopes = all })
    |> Map.ofList

/// `max 1 (ceil (n × sample))` of `displays`, at most all of them, chosen by a
/// `Random(seed)` shuffle of the sorted list: the same seed picks the same tests.
let choose (sample: float) (seed: int) (displays: string list) : string list =
    let sorted = displays |> List.sort |> Array.ofList
    let n = sorted.Length
    let k = min n (max 1 (int (ceil (float n * sample))))
    Random(seed).Shuffle sorted
    sorted |> Array.truncate k |> List.ofArray

/// Compare one test in the parallel run with its isolated run (`None` when the isolated run
/// recorded no scope for it): its own ids for extras, everything it holds or inherits for
/// missing ids. `rows` is the weave manifest by id; `recordedInParallel` says whether the
/// parallel run recorded a scope key.
let compareTest
    (rows: Map<int, ManifestRow>)
    (display: string)
    (inParallel: Observed)
    (isolated: Observed option)
    (recordedInParallel: string -> bool)
    : TestAudit =
    let aloneOwn, aloneAll =
        match isolated with
        | Some o -> o.Own, o.All
        | None -> Set.empty, Set.empty

    let missing =
        Set.difference aloneAll inParallel.All
        |> List.ofSeq
        |> List.map (fun id -> id, rows.TryFind id)

    let kindOf (row: ManifestRow option) =
        row
        |> Option.map (fun r -> Manifest.kindCode r.Kind)
        |> Option.defaultValue "unknown"

    // An initializer that recorded nothing leaves no scope, and nothing to inherit.
    let uncovered (r: ManifestRow) =
        let key = RunScopes.StaticInitPrefix + r.TypeName
        recordedInParallel key && not (inParallel.Scopes.Contains key)

    { Display = display
      Extra = Set.difference inParallel.Own aloneOwn |> List.ofSeq
      MissingByKind = missing |> List.countBy (snd >> kindOf) |> Map.ofList
      MissingUser =
        missing
        |> List.filter (fun (_, r) -> not (List.contains (kindOf r) [ "cctor"; "gen" ]))
        |> List.map (fun (id, r) ->
            match r with
            | Some r -> $"%s{r.TypeName}::%s{r.Member}"
            | None -> $"?::%d{id}")
      UncoveredInit =
        missing
        |> List.choose (fun (_, r) -> r |> Option.filter (fun r -> r.Kind = StaticCtor && uncovered r))
        |> List.map (fun r -> r.TypeName)
        |> List.distinct
      IsolatedFound = isolated.IsSome
      IsolationError = None }

/// A sampled test its isolated run did not select: an audit error, compared with nothing.
let notIsolated (display: string) (why: string) : TestAudit =
    { Display = display
      Extra = []
      MissingByKind = Map.empty
      MissingUser = []
      UncoveredInit = []
      IsolatedFound = false
      IsolationError = Some $"could not isolate %s{display}: %s{why}" }

/// The `--filter-display-name` value that selects exactly `display`. xUnit reads a `*` at
/// either end as a wildcard, rejects one anywhere else, and decodes `&#xHHHH;` escapes; so
/// every character but a letter, digit or space is escaped and the match is exact (xUnit
/// compares display names ignoring case).
let displayFilter (display: string) : string =
    display
    |> String.collect (fun c ->
        if Char.IsAsciiLetterOrDigit c || c = ' ' then
            string c
        else
            $"&#x%04X{int c};")

/// Tests of a run per incomplete reason its dumps show without a join: each test's
/// children that left no dump, plus overflow and rejected dumps, which touch every test.
let incomplete (dumps: ProcessDump list) (rejected: (string * string) list) : Map<string, int> =
    let traced =
        dumps
        |> List.choose (fun d -> d.ParentScope |> Option.map (fun p -> p, d.Pid))
        |> Set.ofList

    let processWide =
        [ if dumps |> List.exists (fun d -> d.Counters.Overflow > 0L) then
              RecorderOverflow
          yield!
              rejected
              |> List.map (fun (f, why) -> DumpRejected $"%s{Path.GetFileName f}: %s{why}") ]

    RunScopes.merged dumps
    |> Map.toList
    |> List.choose (fun (key, s) ->
        s.Test
        |> Option.map (fun _ ->
            (s.Children
             |> List.filter (fun c -> not (traced.Contains(key, c.Pid)))
             |> List.map (fun c -> ChildProcessUntraced c.FileName))
            @ processWide
            |> List.map TraceStore.reasonCode
            |> List.distinct))
    |> List.concat
    |> List.countBy id
    |> Map.ofList

/// The report over the sampled tests.
let summarize (tests: TestAudit list) (incomplete: Map<string, int>) : AuditReport =
    { Sampled = tests.Length
      ExtraTotal = tests |> List.sumBy (fun t -> t.Extra.Length)
      Tests = tests
      Incomplete = incomplete
      ParallelExitCode = 0
      ParallelOutput = "" }

/// The report with the parallel run's exit code, and its output when it exited nonzero.
let withParallelRun (exitCode: int) (output: string) (r: AuditReport) : AuditReport =
    { r with
        ParallelExitCode = exitCode
        ParallelOutput = if exitCode = 0 then "" else output }

/// The bar: something was sampled, no id is extra in parallel, every sampled test was run
/// and recorded alone, and every initializer a test misses in parallel it inherits there.
/// Other missing ids are reported for review, never failed.
let passes (r: AuditReport) =
    r.Sampled > 0
    && r.ExtraTotal = 0
    && r.Tests |> List.forall (fun t -> t.IsolatedFound && t.UncoveredInit.IsEmpty)

/// True when a sampled test could not be run alone: the audit measured nothing for it.
let isError (r: AuditReport) =
    r.Tests |> List.exists (fun t -> t.IsolationError.IsSome)

/// The human report: the verdict, then per test its extra and missing ids.
let render (r: AuditReport) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore

    let verdict =
        if isError r then "ERROR"
        elif passes r then "PASS"
        else "FAIL"

    line $"audit  %s{verdict}  sampled %d{r.Sampled}  extra-in-parallel %d{r.ExtraTotal}"

    if r.ParallelExitCode <> 0 then
        line $"  parallel run exit %d{r.ParallelExitCode}; its output ends:"

        for l in Launch.outputLines r.ParallelOutput do
            line $"      %s{l}"

    for t in r.Tests do
        let missing =
            match Map.toList t.MissingByKind with
            | [] -> "-"
            | kinds -> kinds |> List.map (fun (k, n) -> $"%s{k}=%d{n}") |> String.concat " "

        line $"  %s{t.Display}  extra %d{t.Extra.Length}  missing %s{missing}"

        for id in t.Extra do
            line $"    extra id %d{id}"

        for m in t.MissingUser do
            line $"    missing user %s{m}"

        for i in t.UncoveredInit do
            line $"    uncovered init %s{i}"

        match t.IsolationError with
        | Some why -> line $"    %s{why}"
        | None when not t.IsolatedFound -> line "    not recorded when run alone"
        | None -> ()

    for reason, n in Map.toList r.Incomplete do
        line $"  incomplete %s{reason}  %d{n}"

    sb.ToString()

/// The launch's environment with its dumps going to a fresh `dumpDir`.
let private dumpingTo (launch: TraceSession.TraceLaunch) dumpDir =
    Directory.CreateDirectory dumpDir |> ignore

    launch.Env
    |> List.map (fun (k, v) -> if k = Contract.OutEnv then k, dumpDir else k, v)

/// Launch the woven app, dumps to a fresh `dumpDir`, and read them, with the launch's exit
/// code and output.
let private tracedRun (launch: TraceSession.TraceLaunch) workDir dumpDir args timeout =
    let code, output =
        Launch.run launch.Apphost args (dumpingTo launch dumpDir) workDir timeout

    DumpReader.readDirectory dumpDir, code, output

/// Run one sampled test alone (`--filter-display-name`, escaped) with a CTRF report, and
/// compare it; a run that selected no test is `notIsolated`, never a comparison with nothing.
let internal auditAlone
    (launch: TraceSession.TraceLaunch)
    (workDir: string)
    (manifest: Manifest)
    (rows: Map<int, ManifestRow>)
    (inParallel: Map<string, Observed>)
    (recordedInParallel: string -> bool)
    (appArgs: string list)
    (timeout: TimeSpan)
    (dir: string)
    (display: string)
    : TestAudit =
    let dumpDir = Path.Combine(dir, "dumps")

    let isolated =
        Ctrf.run
            "alone"
            launch.Apphost
            (appArgs @ [ "--filter-display-name"; displayFilter display ])
            (dumpingTo launch dumpDir)
            workDir
            (Path.Combine(dir, "results"))
            timeout

    match isolated with
    | Error why -> notIsolated display why
    | Ok [] -> notIsolated display "the filter selected nothing"
    | Ok _ ->
        let alone, _ = DumpReader.readDirectory dumpDir

        compareTest
            rows
            display
            (Map.find display inParallel)
            ((observe manifest alone).TryFind display)
            recordedInParallel

/// Prepare the project, run it once in parallel, then each of a seeded `sample` of its
/// traced tests alone (`auditAlone`), and compare. `appArgs` go to every
/// launch; a filter in them narrows the parallel run. `Error` when the project cannot be
/// prepared.
let run
    (req: TraceSession.PrepareRequest)
    (appArgs: string list)
    (sample: float)
    (seed: int)
    (timeout: TimeSpan)
    : Result<AuditReport, string> =
    TraceSession.prepareProject req
    |> Result.map (fun launch ->
        let rows =
            // Seq.map rather than Array.map, which FSharp.Core inlines with a null check.
            launch.Shadow.Manifest.Rows |> Seq.map (fun r -> r.Id, r) |> Map.ofSeq

        let (dumps, rejected), code, output =
            tracedRun launch req.RepoRoot launch.DumpDir appArgs timeout

        let manifest = launch.Shadow.Manifest
        let inParallel = observe manifest dumps
        let recorded = RunScopes.merged dumps

        let tests =
            choose sample seed (inParallel |> Map.keys |> List.ofSeq)
            |> List.mapi (fun i display ->
                let dir = Path.Combine(req.RunDir, "audit", string i)

                auditAlone
                    launch
                    req.RepoRoot
                    manifest
                    rows
                    inParallel
                    recorded.ContainsKey
                    appArgs
                    timeout
                    dir
                    display)

        summarize tests (incomplete dumps rejected) |> withParallelRun code output)
