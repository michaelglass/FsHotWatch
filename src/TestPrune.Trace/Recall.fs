/// The recall check: does impact selection select every test that a recorded trace shows
/// executing a symbol? For every complete trace of test T and every symbol X the trace
/// executed that the index knows at the same content hash, T must be in
/// `QueryAffectedTests [X]`. A pair where it is not is a miss: a change to X that would
/// leave a test which runs X unselected.
///
/// Recall is measured under two walk policies over one index: following every edge, and a
/// narrower policy (`Domain.WalkPolicy`) that stops following some edge sources. A narrower
/// policy is safe to adopt when it adds no miss: every pair it misses, the follow-all walk
/// misses too.
module TestPrune.Trace.Recall

open System.Collections.Concurrent
open System.Threading.Tasks
open TestPrune.Ports
open TestPrune.Trace.TraceStore

/// A test that executed a symbol whose change does not select it.
type Miss =
    {
        /// "<project>|<class>|<method>"
        Test: string
        Symbol: string
    }

/// What a recall check checked and missed.
type RecallReport =
    {
        /// Complete traces whose test the index knows: the traces checked.
        Traces: int
        /// Traces skipped because they are incomplete.
        IncompleteTraces: int
        /// Complete traces whose test the index has no test method for, by test key.
        /// Their pairs cannot be checked.
        UnindexedTests: string list
        /// (test, symbol) pairs checked.
        Pairs: int
        /// Pairs skipped because the index holds the symbol at another content hash than
        /// the trace recorded: the trace and the index are of different trees there.
        StalePairs: int
        /// Executed symbols the index does not know, counted once each.
        UnindexedSymbols: int
        /// Misses following every edge.
        FollowAll: Miss list
        /// Misses under the narrower policy.
        Narrowed: Miss list
    }

    /// Misses the narrower policy adds: the pairs it misses that the follow-all walk does not.
    member this.NarrowedOnly: Miss list =
        let followAll = Set.ofList this.FollowAll
        this.Narrowed |> List.filter (followAll.Contains >> not)

/// The JSON form of a report: the report itself, and the misses the narrower policy adds
/// (`RecallReport.NarrowedOnly`, which a serializer does not see).
type RecallJson =
    { Report: RecallReport
      NarrowedOnly: Miss list }

/// The JSON form of `r`.
let toJson (r: RecallReport) : RecallJson =
    { Report = r
      NarrowedOnly = r.NarrowedOnly }

/// A test's key as the index names it, the form a trace is stored under.
let private keyOf (t: TestPrune.AstAnalyzer.TestMethodInfo) =
    TraceIngest.testKey t.TestProject t.TestClass t.TestMethod

/// The project of a test key.
let private projectOf (testKey: string) = testKey.Split('|').[0]

/// Check `traces` against one index read through two stores, one per policy: `followAll`
/// and `narrowed`. Each executed symbol is queried once per store; `jobs` queries run at
/// a time.
let check (jobs: int) (followAll: SymbolStore) (narrowed: SymbolStore) (traces: StoredTrace list) : RecallReport =
    let complete, incomplete = traces |> List.partition _.Complete

    let indexed =
        complete
        |> List.map (_.TestKey >> projectOf)
        |> List.distinct
        |> followAll.GetTestMethodsInProjects
        |> List.map keyOf
        |> Set.ofList

    let checkedTraces, unindexed =
        complete |> List.partition (fun t -> indexed.Contains t.TestKey)

    let versions = TraceIngest.versionHashes followAll

    let executed =
        checkedTraces
        |> List.collect (fun t -> t.Symbols |> Set.toList |> List.map (fun (name, hash) -> t.TestKey, name, hash))

    let pairs, stale =
        executed
        |> List.filter (fun (_, name, _) -> versions.ContainsKey name)
        |> List.partition (fun (_, name, hash) -> versions.[name] = hash)

    let testsBySymbol =
        pairs
        |> List.map (fun (test, name, _) -> name, test)
        |> List.distinct
        |> List.groupBy fst
        |> List.map (fun (name, group) -> name, group |> List.map snd)

    let missesUnder (store: SymbolStore) =
        let found = ConcurrentBag<Miss>()

        Parallel.ForEach(
            testsBySymbol,
            ParallelOptions(MaxDegreeOfParallelism = jobs),
            fun (symbol, tests) ->
                let selected = store.QueryAffectedTests [ symbol ] |> List.map keyOf |> Set.ofList

                for test in tests do
                    if not (selected.Contains test) then
                        found.Add { Test = test; Symbol = symbol }
        )
        |> ignore

        found |> List.ofSeq |> List.sort

    { Traces = checkedTraces.Length
      IncompleteTraces = incomplete.Length
      UnindexedTests = unindexed |> List.map _.TestKey |> List.sort
      Pairs = testsBySymbol |> List.sumBy (snd >> List.length)
      StalePairs = stale.Length
      UnindexedSymbols =
        executed
        |> List.choose (fun (_, name, _) -> if versions.ContainsKey name then None else Some name)
        |> List.distinct
        |> List.length
      FollowAll = missesUnder followAll
      Narrowed = missesUnder narrowed }

/// Every current trace in a trace database: per test project, the traces stored under the
/// fingerprint of its newest recorded run (run `runId` when given). A run that refused,
/// failed, or saw the tree move stored no complete trace, so it is passed over. A project
/// with no recorded run contributes nothing.
let tracesIn (store: Store) (runId: string option) : StoredTrace list =
    store.TestProjects()
    |> List.collect (fun project ->
        store.Runs project
        |> List.filter (fun r -> r.Status = Recorded && runId |> Option.forall ((=) r.RunId))
        |> List.tryHead
        |> Option.map (fun run ->
            store.TestKeysOf(project, run.EnvFingerprint)
            |> Set.toList
            |> List.choose (fun key -> store.TryRead(key, run.EnvFingerprint)))
        |> Option.defaultValue [])

/// The bar: something was checked, and the narrower policy adds no miss.
let passes (r: RecallReport) = r.Pairs > 0 && r.NarrowedOnly.IsEmpty

/// The human report: the verdict and counts, then each added miss, then up to `limit`
/// misses per policy.
let render (limit: int) (r: RecallReport) : string =
    let sb = System.Text.StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    let narrowedOnly = r.NarrowedOnly

    let verdict =
        if r.Pairs = 0 then "NOTHING CHECKED"
        elif passes r then "PASS"
        else "FAIL"

    line
        $"recall  %s{verdict}  traces %d{r.Traces}  pairs %d{r.Pairs}  follow-all misses %d{r.FollowAll.Length}  narrowed misses %d{r.Narrowed.Length}  added by narrowing %d{narrowedOnly.Length}"

    line
        $"  skipped: incomplete traces %d{r.IncompleteTraces}  unindexed tests %d{r.UnindexedTests.Length}  stale pairs %d{r.StalePairs}  unindexed symbols %d{r.UnindexedSymbols}"

    for m in narrowedOnly do
        line $"  added miss  %s{m.Test}  %s{m.Symbol}"

    for label, misses in [ "follow-all", r.FollowAll; "narrowed", r.Narrowed ] do
        for m in List.truncate limit misses do
            line $"  %s{label} miss  %s{m.Test}  %s{m.Symbol}"

        if misses.Length > limit then
            line $"  %s{label}: %d{misses.Length - limit} more misses (--json lists them all)"

    for t in List.truncate limit r.UnindexedTests do
        line $"  unindexed test  %s{t}"

    sb.ToString()
