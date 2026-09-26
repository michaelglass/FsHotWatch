/// The isolated-vs-parallel audit: run a woven project once with its tests in parallel,
/// then a seeded sample of its tests one at a time, and compare each sampled test's OWN
/// probe ids. An id the parallel run attributed to a test that the test never hits alone
/// is contamination from a concurrent test; the bar is none. Ids a test hits alone but
/// not in parallel are expected for once-per-process code (static constructors, closure
/// singletons), which another test ran first; the rest are listed for a human.
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
        /// False when the isolated run recorded no scope for the test: the display-name
        /// filter matched nothing, or the test hit no probe alone. Its ids are all extra.
        IsolatedFound: bool
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
    }

/// The run-wide static-init scope; a child process's belongs to the scope that started it.
[<Literal>]
let private StaticInit = "S:static-init"

/// Every scope of the run by key, merged across processes. A child process records into
/// the scope that started it, and so does its static init (as ingestion links them).
let merged (dumps: ProcessDump list) : Map<string, RecordedScope> =
    dumps
    |> List.collect (fun d ->
        d.Scopes
        |> List.map (fun s ->
            match d.ParentScope with
            | Some parent when s.Key = StaticInit -> { s with Key = parent }
            | _ -> s))
    |> List.groupBy (fun s -> s.Key)
    |> List.map (fun (key, group) ->
        key,
        { Key = key
          Test = group |> List.tryPick (fun s -> s.Test)
          Parents = group |> List.collect (fun s -> s.Parents) |> List.distinct
          Links = group |> List.collect (fun s -> s.Links) |> List.distinct
          Ids = group |> Seq.collect (fun s -> s.Ids) |> Seq.distinct |> Array.ofSeq
          Inputs = group |> List.collect (fun s -> s.Inputs) |> List.distinct
          Children = group |> List.collect (fun s -> s.Children) })
    |> Map.ofList

/// Each test scope's own ids, by display name.
let ownIds (dumps: ProcessDump list) : Map<string, Set<int>> =
    merged dumps
    |> Map.toList
    |> List.choose (fun (_, s) -> s.Test |> Option.map (fun t -> t.Display, Set.ofArray s.Ids))
    |> List.groupBy fst
    |> List.map (fun (display, group) -> display, group |> List.map snd |> Set.unionMany)
    |> Map.ofList

/// `max 1 (ceil (n × sample))` of `displays`, at most all of them, chosen by a
/// `Random(seed)` shuffle of the sorted list: the same seed picks the same tests.
let choose (sample: float) (seed: int) (displays: string list) : string list =
    let sorted = displays |> List.sort |> Array.ofList
    let n = sorted.Length
    let k = min n (max 1 (int (ceil (float n * sample))))
    Random(seed).Shuffle sorted
    sorted |> Array.truncate k |> List.ofArray

/// Compare one test's own ids in the parallel run with its isolated run (`None` when the
/// isolated run recorded no scope for it). `rows` is the weave manifest by id.
let compareTest
    (rows: Map<int, ManifestRow>)
    (display: string)
    (parallelIds: Set<int>)
    (isolatedIds: Set<int> option)
    : TestAudit =
    let alone = isolatedIds |> Option.defaultValue Set.empty

    let missing =
        Set.difference alone parallelIds
        |> List.ofSeq
        |> List.map (fun id -> id, rows.TryFind id)

    let kindOf (row: ManifestRow option) =
        row
        |> Option.map (fun r -> Manifest.kindCode r.Kind)
        |> Option.defaultValue "unknown"

    { Display = display
      Extra = Set.difference parallelIds alone |> List.ofSeq
      MissingByKind = missing |> List.countBy (snd >> kindOf) |> Map.ofList
      MissingUser =
        missing
        |> List.filter (fun (_, r) -> not (List.contains (kindOf r) [ "cctor"; "gen" ]))
        |> List.map (fun (id, r) ->
            match r with
            | Some r -> $"%s{r.TypeName}::%s{r.Member}"
            | None -> $"?::%d{id}")
      IsolatedFound = isolatedIds.IsSome }

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

    merged dumps
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
      Incomplete = incomplete }

/// The bar: something was sampled, no id is extra in parallel, and every sampled test was
/// recorded alone. Missing ids are reported for review, never failed.
let passes (r: AuditReport) =
    r.Sampled > 0
    && r.ExtraTotal = 0
    && r.Tests |> List.forall (fun t -> t.IsolatedFound)

/// The human report: the verdict, then per test its extra and missing ids.
let render (r: AuditReport) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    let verdict = if passes r then "PASS" else "FAIL"
    line $"audit  %s{verdict}  sampled %d{r.Sampled}  extra-in-parallel %d{r.ExtraTotal}"

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

        if not t.IsolatedFound then
            line "    not recorded when run alone"

    for reason, n in Map.toList r.Incomplete do
        line $"  incomplete %s{reason}  %d{n}"

    sb.ToString()

/// Launch the woven app with `env`, dumps to a fresh `dumpDir`, and read them.
let private tracedRun (launch: TraceSession.TraceLaunch) workDir dumpDir args timeout =
    Directory.CreateDirectory dumpDir |> ignore

    let env =
        launch.Env
        |> List.map (fun (k, v) -> if k = Contract.OutEnv then k, dumpDir else k, v)

    Launch.run launch.Apphost args env workDir timeout |> ignore
    DumpReader.readDirectory dumpDir

/// Prepare the project, run it once in parallel, then each of a seeded `sample` of its
/// traced tests alone (`--filter-display-name`), and compare. `appArgs` go to every
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

        let dumps, rejected = tracedRun launch req.RepoRoot launch.DumpDir appArgs timeout
        let inParallel = ownIds dumps

        let tests =
            choose sample seed (inParallel |> Map.keys |> List.ofSeq)
            |> List.mapi (fun i display ->
                let dir = Path.Combine(req.RunDir, "audit", string i)

                let alone, _ =
                    tracedRun launch req.RepoRoot dir (appArgs @ [ "--filter-display-name"; display ]) timeout

                compareTest rows display inParallel.[display] ((ownIds alone).TryFind display))

        summarize tests (incomplete dumps rejected))
