module TestPrune.Tests.SoundnessHarnessTests

// Makes "impact selection is reliable" a MEASURED property.
//
// The dangerous direction is UNDER-selection: a green impact-filtered run that
// skipped the one test which would have caught the bug. Over-selection only
// costs time. So the property under test is one-sided:
//
//     selected ⊇ truly-affected
//
// Two things make this a real check rather than a restatement of the code:
//
//   1. THE ORACLE IS INDEPENDENT. `trulyAffectedTests` walks the generated
//      graph with its own breadth-first search. It does not call the store's
//      `QueryAffectedTests`, so this is not TestPrune agreeing with itself — a
//      closure bug in the store makes the two disagree, which is the point.
//
//   2. IT IS RANDOMISED over generated graphs, so it explores shapes (diamonds,
//      cycles, long chains, disconnected islands, tests depending on tests) that
//      a hand-written corpus would not think to include. The seed is fixed and
//      printed with any failure, so a counterexample is reproducible.
//
// `RunAll` is trivially sound — it runs everything — so it always satisfies the
// property. That is deliberate: the harness must never pressure anyone into
// narrowing a conservative fallback, which would be a soundness REGRESSION
// dressed up as an improvement.
//
// Scope: the symbol-graph layer — seeds, the transitive walk, and the plumbing
// in `selectTests`. See "Not covered yet" at the bottom of this file.

open System
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.ImpactAnalysis
open TestPrune.InMemoryStore
open TestPrune.Ports

// ---------------------------------------------------------------------------
// A generated graph, and the ground truth that comes with it by construction
// ---------------------------------------------------------------------------

/// One generated corpus entry: the graph, plus which symbols are tests.
type private Corpus =
    {
        Result: AnalysisResult
        /// symbol -> the symbols it depends on (forward edges, as generated).
        Edges: Map<string, string list>
        TestSymbols: Set<string>
        /// Every symbol, in generation order.
        AllSymbols: string list
    }

let private fileOf (i: int) = $"src/Gen%d{i / 4}.fs"

let private symbolName (i: int) = $"Gen.sym%d{i}"

/// Build a graph of `n` symbols. Each symbol may depend on any other (including
/// later ones, so cycles are possible — the closure must cope). A quarter of the
/// symbols are test methods, and tests are allowed to depend on other tests,
/// which is a shape real fixtures produce and a naive walk gets wrong.
let private generateCorpus (rng: Random) (n: int) : Corpus =
    let names = [ 0 .. n - 1 ] |> List.map symbolName

    let edges =
        [ for i in 0 .. n - 1 do
              let outDegree = rng.Next(0, 4)

              let targets =
                  [ for _ in 1..outDegree do
                        let t = rng.Next(0, n)

                        if t <> i then
                            yield symbolName t ]
                  |> List.distinct

              yield symbolName i, targets ]
        |> Map.ofList

    let testSymbols = names |> List.filter (fun _ -> rng.Next(0, 4) = 0) |> Set.ofList

    let symbols =
        names
        |> List.mapi (fun i name ->
            { FullName = name
              Kind = Function
              SourceFile = fileOf i
              LineStart = 1
              LineEnd = 5
              // A stable baseline hash; the mutation below changes it.
              ContentHash = "v1"
              IsExtern = false })

    let dependencies =
        [ for KeyValue(from, tos) in edges do
              for t in tos do
                  yield
                      { FromSymbol = from
                        ToSymbol = t
                        Kind = Calls
                        Source = "core" } ]

    let testMethods =
        testSymbols
        |> Set.toList
        |> List.map (fun name ->
            { SymbolFullName = name
              TestProject = "tests/Gen.Tests"
              TestClass = "GenTests"
              TestMethod = name })

    { Result = AnalysisResult.Create(symbols, dependencies, testMethods)
      Edges = edges
      TestSymbols = testSymbols
      AllSymbols = names }

/// THE ORACLE. Which tests must run when `changed` changes?
///
/// Independent breadth-first walk over the REVERSE edges: anything that depends
/// (transitively) on a changed symbol is affected, and the affected symbols that
/// are tests are the ones that must be selected. Written against the generated
/// `Edges` map, never against the store — so a bug in the store's own closure
/// shows up as a disagreement rather than being mirrored.
let private trulyAffectedTests (corpus: Corpus) (changed: Set<string>) : Set<string> =
    let reverse =
        [ for KeyValue(from, tos) in corpus.Edges do
              for t in tos do
                  yield t, from ]
        |> List.groupBy fst
        |> List.map (fun (k, pairs) -> k, pairs |> List.map snd)
        |> Map.ofList

    let rec walk (frontier: Set<string>) (seen: Set<string>) =
        if Set.isEmpty frontier then
            seen
        else
            let next =
                frontier
                |> Set.toList
                |> List.collect (fun s -> reverse |> Map.tryFind s |> Option.defaultValue [])
                |> Set.ofList

            let fresh = Set.difference next seen
            walk fresh (Set.union seen fresh)

    // A changed test is itself affected — it must re-run.
    let affected = walk changed changed
    Set.intersect affected corpus.TestSymbols

/// Mutate `changed`: same symbols, new content hashes, so the differ sees them.
let private mutatedSymbolsByFile (corpus: Corpus) (changed: Set<string>) =
    corpus.Result.Symbols
    |> List.map (fun s ->
        if changed.Contains s.FullName then
            { s with ContentHash = "v2-mutated" }
        else
            s)
    |> List.groupBy _.SourceFile
    |> Map.ofList

let private selectedTestNames (selection: TestSelection) =
    match selection with
    | RunAll _ -> None // sound by construction: everything runs
    | RunSubset tests -> Some(tests |> List.map _.SymbolFullName |> Set.ofList)

// ---------------------------------------------------------------------------
// The property
// ---------------------------------------------------------------------------

/// What one generated case actually exercised — so the harness can prove it is
/// not passing vacuously.
type private CaseOutcome =
    /// Selection said RunAll: sound, but it checked nothing about the walk.
    | SkippedRunAll
    /// A real subset was compared against a non-empty expected set.
    | CheckedSubset
    /// A subset, but nothing was expected — passes without exercising anything.
    | CheckedEmpty
    | UnderSelected of report: string

/// Run one generated case.
let private checkOne (rng: Random) (n: int) : CaseOutcome =
    let corpus = generateCorpus rng n

    // Change a non-empty slice of the graph.
    let changed =
        corpus.AllSymbols
        |> List.filter (fun _ -> rng.Next(0, 5) = 0)
        |> function
            | [] -> [ List.head corpus.AllSymbols ]
            | xs -> xs
        |> Set.ofList

    let store = fromAnalysisResults [ corpus.Result ]
    let currentSymbols = mutatedSymbolsByFile corpus changed

    let changedFiles =
        changed
        |> Set.toList
        |> List.map (fun s -> corpus.Result.Symbols |> List.find (fun sym -> sym.FullName = s) |> _.SourceFile)
        |> List.distinct

    let selection, _events = selectTests store changedFiles currentSymbols
    let expected = trulyAffectedTests corpus changed

    match selectedTestNames selection with
    | None -> SkippedRunAll // runs everything, cannot under-select
    | Some selected ->
        let missing = Set.difference expected selected

        if not (Set.isEmpty missing) then
            UnderSelected
                $"UNDER-SELECTION: %d{Set.count missing} test(s) affected but not selected.\n\
                   missing:  %A{Set.toList missing}\n\
                   changed:  %A{Set.toList changed}\n\
                   selected: %A{Set.toList selected}"
        elif Set.isEmpty expected then
            CheckedEmpty
        else
            CheckedSubset

/// The harness. A fixed seed keeps it deterministic and any counterexample
/// reproducible; the case index is reported so a failure names which one.
[<Fact>]
let ``soundness: selection never drops a truly-affected test`` () =
    let seed = 20260805
    let rng = Random(seed)

    let outcomes =
        [ for case in 1..200 do
              // Vary the graph size so both trivial and tangled shapes are hit.
              let n = 3 + (case % 24)
              yield case, n, checkOne rng n ]

    let failures =
        outcomes
        |> List.choose (fun (case, n, outcome) ->
            match outcome with
            | UnderSelected report -> Some $"[seed=%d{seed} case=%d{case} n=%d{n}]\n%s{report}"
            | _ -> None)

    test <@ List.isEmpty failures @>

    // NON-VACUITY. If every case had come back RunAll (or with nothing expected),
    // the assertion above would hold while proving nothing whatsoever — the same
    // trap as a lint that reads no files. Demand that a healthy share of the
    // corpus actually compared a real subset against a real expected set.
    let realChecks =
        outcomes
        |> List.filter (fun (_, _, o) ->
            match o with
            | CheckedSubset -> true
            | _ -> false)
        |> List.length

    test <@ realChecks >= 20 @>

// ---------------------------------------------------------------------------
// The positive control — proof the harness can actually fail
// ---------------------------------------------------------------------------

/// A store that deliberately drops one affected test, standing in for an
/// under-selecting heuristic.
///
/// Without this, `soundness` above could pass because it never really looks —
/// a harness that cannot fail measures nothing.
let private underSelectingStore (drop: string) (inner: SymbolStore) : SymbolStore =
    { inner with
        QueryAffectedTests =
            fun seeds ->
                inner.QueryAffectedTests seeds
                |> List.filter (fun t -> t.SymbolFullName <> drop) }

[<Fact>]
let ``soundness: the harness detects a deliberately under-selecting store`` () =
    let rng = Random(1)
    let corpus = generateCorpus rng 12

    // Find a change with at least one affected test to drop.
    let changedOpt =
        corpus.AllSymbols
        |> List.map Set.singleton
        |> List.tryFind (fun c -> trulyAffectedTests corpus c |> Set.isEmpty |> not)

    match changedOpt with
    | None -> failwith "generator produced no case with an affected test — the control cannot run"
    | Some changed ->
        let expected = trulyAffectedTests corpus changed
        let victim = expected |> Set.toList |> List.head

        let store = underSelectingStore victim (fromAnalysisResults [ corpus.Result ])
        let currentSymbols = mutatedSymbolsByFile corpus changed

        let changedFiles =
            changed
            |> Set.toList
            |> List.map (fun s -> corpus.Result.Symbols |> List.find (fun sym -> sym.FullName = s) |> _.SourceFile)
            |> List.distinct

        let selection, _ = selectTests store changedFiles currentSymbols

        match selectedTestNames selection with
        | None -> failwith "control produced RunAll — it cannot demonstrate under-selection; adjust the fixture"
        | Some selected ->
            // The dropped test IS affected, and the sabotaged store omits it:
            // exactly the shape the property must catch.
            test <@ expected.Contains victim @>
            test <@ not (selected.Contains victim) @>

/// RunAll must always satisfy the property — it runs everything. Pinned so a
/// future change cannot "improve" the harness into penalising the conservative
/// fallback, which would turn a safe answer into a reported defect.
[<Fact>]
let ``soundness: RunAll is always sound`` () =
    test <@ selectedTestNames (RunAll(NewFileNotIndexed "src/New.fs")) = None @>

// ---------------------------------------------------------------------------
// Not covered yet — deliberately recorded rather than implied
// ---------------------------------------------------------------------------
//
// - The ROUTE-URL heuristic (TestPrune.Falco): a test navigating via a
//   `Route.link` value instead of a literal URL lives there, not in the symbol
//   graph. TestPrune.Falco handles that case; this harness does not prove it.
// - Fixture edges and `[<DependsOnFile>]`/`[<DependsOnGlob>]` seeds: exercised
//   incidentally by `selectTests`, but not generated adversarially here.
// - The real-suite sweep (mutate real code, run the full suite, diff against
//   selection) — a separate, much slower job.
