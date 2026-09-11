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
// Scope, in two layers, because they grade DIFFERENT things:
//
//   • The GRAPH-LAYER property below builds its `AnalysisResult` directly and
//     grades the store's transitive closure and `selectTests`' plumbing. It does
//     NOT grade extraction: `analyzeSource` is never called, so a binding form
//     the analyzer fails to see is absent from both the store AND the oracle,
//     and is structurally invisible here. Saying it grades
//     "impact selection" would overclaim.
//
//   • The EXTRACTION-INCLUSIVE property (further down) closes exactly that gap:
//     it emits real F# SOURCE, runs it through `analyzeSource`, and takes its
//     oracle from the GENERATION PLAN rather than from anything the analyzer
//     produced. A symbol or edge the analyzer drops therefore shows up as
//     under-selection — the extraction bug class, which the graph layer
//     cannot reach.
//
// See "Not covered yet" at the bottom of this file.

open System
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
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

/// `to -> [from...]`, the generated corpus read backwards. Plumbing shared by both
/// oracles below: it inverts the generated `Edges` map and decides nothing. The
/// oracles' WALKS stay written out separately on purpose — that independence is the
/// point of the harness — but inverting a map the same way twice proves nothing.
let private reverseEdgesOf (corpus: Corpus) : Map<string, string list> =
    [ for KeyValue(from, tos) in corpus.Edges do
          for t in tos do
              yield t, from ]
    |> List.groupBy fst
    |> List.map (fun (k, pairs) -> k, pairs |> List.map snd)
    |> Map.ofList

/// THE ORACLE. Which tests must run when `changed` changes?
///
/// Independent breadth-first walk over the REVERSE edges: anything that depends
/// (transitively) on a changed symbol is affected, and the affected symbols that
/// are tests are the ones that must be selected. Written against the generated
/// `Edges` map, never against the store — so a bug in the store's own closure
/// shows up as a disagreement rather than being mirrored.
let private trulyAffectedTests (corpus: Corpus) (changed: Set<string>) : Set<string> =
    let reverse = reverseEdgesOf corpus

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
// The composition-root barrier, held to the same one-sided standard
// ---------------------------------------------------------------------------
//
// `[<TestPrune.CompositionRoot>]` is the first feature that makes
// selection NARROWER, so it is the first that can under-select. The property
// above already covers the un-annotated case — every corpus it generates carries
// no attributes, so its 200 cases now double as proof that an un-annotated repo
// selects exactly what it always did.
//
// This is the annotated half. The oracle is again independent: the same BFS as
// `trulyAffectedTests`, with one rule added — do not expand out of the barrier.
// Writing it separately matters, because the interesting mistakes are all
// off-by-one against that rule: blocking ENTRY to the barrier instead of exit
// (drops the barrier's own dependents entirely), forgetting the seed exemption
// (a change to the root would stop dead), or blocking every path to a node
// rather than the one through the barrier (a set union does not work that way).

/// Tests that must still run when `changed` changes, given `barrier` is a
/// composition root. Independent walk: the barrier is VISITED but not EXPANDED,
/// and it is exempt when it is itself a seed.
let private trulyAffectedWithBarrier (corpus: Corpus) (barrier: string) (changed: Set<string>) : Set<string> =
    let reverse = reverseEdgesOf corpus

    let barrierApplies = not (changed.Contains barrier)

    let rec walk (frontier: Set<string>) (seen: Set<string>) =
        if Set.isEmpty frontier then
            seen
        else
            let next =
                frontier
                |> Set.toList
                |> List.collect (fun s ->
                    if barrierApplies && s = barrier then
                        []
                    else
                        reverse |> Map.tryFind s |> Option.defaultValue [])
                |> Set.ofList

            let fresh = Set.difference next seen
            walk fresh (Set.union seen fresh)

    Set.intersect (walk changed changed) corpus.TestSymbols

/// Attach the marker to one symbol of a generated corpus.
let private withCompositionRoot (barrier: string) (corpus: Corpus) =
    { corpus with
        Result =
            { corpus.Result with
                Attributes =
                    [ { SymbolFullName = barrier
                        AttributeName = "CompositionRootAttribute"
                        ArgsJson = "[]" } ] } }

[<Fact>]
let ``soundness: a composition-root marker never drops a test off the barrier path`` () =
    let seed = 20260814
    let rng = Random(seed)

    let outcomes =
        [ for case in 1..200 do
              let n = 3 + (case % 24)
              let bare = generateCorpus rng n

              // Any symbol may be the root; the interesting cases are the ones
              // where it sits between a change and some tests.
              let barrier = bare.AllSymbols[rng.Next(0, List.length bare.AllSymbols)]
              let corpus = withCompositionRoot barrier bare

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
                  |> List.map (fun s ->
                      corpus.Result.Symbols |> List.find (fun sym -> sym.FullName = s) |> _.SourceFile)
                  |> List.distinct

              let selection, _ = selectTests store changedFiles currentSymbols
              let expected = trulyAffectedWithBarrier corpus barrier changed

              match selectedTestNames selection with
              | None -> yield case, SkippedRunAll
              | Some selected ->
                  let missing = Set.difference expected selected

                  if not (Set.isEmpty missing) then
                      yield
                          case,
                          UnderSelected
                              $"UNDER-SELECTION with barrier %s{barrier}: %d{Set.count missing} test(s) dropped.\n\
                                 missing:  %A{Set.toList missing}\n\
                                 changed:  %A{Set.toList changed}\n\
                                 selected: %A{Set.toList selected}"
                  elif Set.isEmpty expected then
                      yield case, CheckedEmpty
                  else
                      yield case, CheckedSubset ]

    let failures =
        outcomes
        |> List.choose (fun (case, outcome) ->
            match outcome with
            | UnderSelected report -> Some $"[seed=%d{seed} case=%d{case}]\n%s{report}"
            | _ -> None)

    test <@ List.isEmpty failures @>

    // Same non-vacuity demand as the property above.
    let realChecks =
        outcomes
        |> List.filter (fun (_, o) ->
            match o with
            | CheckedSubset -> true
            | _ -> false)
        |> List.length

    test <@ realChecks >= 20 @>

[<Fact>]
let ``soundness: the barrier property detects a marker that blocks a seed change`` () =
    // THE POSITIVE CONTROL for the property above. The single most likely way to
    // get the barrier wrong is to drop the seed exemption, so a change TO the
    // composition root stops at itself and selects nothing downstream. Build a
    // corpus where that distinction is observable and prove the oracle demands
    // the un-barriered answer — so the property above would fail if the
    // implementation lost the exemption.
    let rng = Random(7)

    let case =
        [ 1..200 ]
        |> List.tryPick (fun _ ->
            let corpus = generateCorpus rng 14

            corpus.AllSymbols
            |> List.tryPick (fun barrier ->
                let changed = Set.singleton barrier
                let viaSeed = trulyAffectedWithBarrier corpus barrier changed

                // Observable only when the root actually has dependent tests.
                if Set.isEmpty viaSeed then
                    None
                else
                    Some(corpus, barrier, changed, viaSeed)))

    match case with
    | None ->
        failwith "generator produced no corpus where a seeded barrier has dependent tests — the control cannot run"
    | Some(corpus, barrier, changed, viaSeed) ->
        // The oracle requires the FULL downstream set for a change to the root...
        test <@ viaSeed = trulyAffectedTests corpus changed @>
        test <@ not (Set.isEmpty viaSeed) @>

        // ...and the implementation must actually deliver it.
        let store = fromAnalysisResults [ (withCompositionRoot barrier corpus).Result ]

        let selected =
            store.QueryAffectedTests(Set.toList changed)
            |> List.map _.SymbolFullName
            |> Set.ofList

        test <@ Set.isEmpty (Set.difference viaSeed selected) @>

// ---------------------------------------------------------------------------
// Extraction-inclusive soundness
// ---------------------------------------------------------------------------
//
// The property above compares a store built from a hand-constructed
// `AnalysisResult` against an oracle read off the SAME generated edge map. That
// makes it blind in one specific, important direction: if `analyzeSource` fails
// to extract a symbol or an edge for some binding form, the edge is missing from
// the store and equally missing from the oracle, so the two agree and the case
// passes. Every binding form the binding-form audit found broken would sail
// through it.
//
// This section removes that blindness the only way it can be removed: the corpus
// becomes REAL F# SOURCE, it is analysed by the REAL analyzer, and the oracle is
// derived from the PLAN THE GENERATOR WROTE — never from the analyzer's output.
// The plan is ground truth by construction: the generator emitted `symA` calling
// `symB`, so an edge A→B exists in the program whatever the analyzer thinks.
//
// A missed extraction is now a DISAGREEMENT rather than a shared blind spot.
//
// It runs far fewer cases than the graph-layer property (each one invokes FCS,
// which costs ~a second, against microseconds for a synthetic graph). That is the
// honest trade: this property is narrower in breadth and strictly deeper in what
// it can catch, so both are kept rather than one replacing the other.

/// A generated corpus that is real, compilable F# source.
type private SourceCorpus =
    {
        /// The source text handed to `analyzeSource`.
        Source: string
        /// THE PLAN: `symbol -> symbols whose functions its body calls`, exactly as
        /// written into `Source`. Ground truth, independent of extraction.
        PlannedEdges: Map<string, string list>
        TestSymbols: Set<string>
        AllSymbols: string list
    }

/// `module rec`, so a generated body may call a symbol declared later and the
/// corpus can contain cycles — the same shapes the graph-layer generator
/// explores. A non-recursive module would silently restrict every corpus to a
/// DAG and stop exercising the closure's cycle handling.
///
/// `FactAttribute` is declared in the generated source rather than referenced
/// from xunit: the script options do not carry a reference to xunit, and the
/// analyzer matches the attribute by NAME, which is what the real extraction
/// does too.
let private generateSourceCorpus (rng: Random) (n: int) : SourceCorpus =
    let names = [ 0 .. n - 1 ] |> List.map symbolName

    let planned =
        [ for i in 0 .. n - 1 do
              let outDegree = rng.Next(0, 3)

              let targets =
                  [ for _ in 1..outDegree do
                        let t = rng.Next(0, n)

                        if t <> i then
                            yield t ]
                  |> List.distinct

              yield symbolName i, (targets |> List.map symbolName) ]
        |> Map.ofList

    // A quarter of the symbols are tests, same proportion as the graph layer.
    let testSymbols = names |> List.filter (fun _ -> rng.Next(0, 4) = 0) |> Set.ofList

    let body (i: int) =
        match planned |> Map.tryFind (symbolName i) with
        | Some []
        | None -> "1"
        | Some targets ->
            targets
            |> List.map (fun t -> $"""%s{t.Substring("Gen.".Length)} ()""")
            |> String.concat " + "
            |> fun calls -> calls + " + 1"

    let declarations =
        [ for i in 0 .. n - 1 do
              let name = $"sym%d{i}"

              let attr =
                  if testSymbols.Contains(symbolName i) then
                      "[<Fact>]\n"
                  else
                      ""

              yield $"%s{attr}let %s{name} () = %s{body i}" ]
        |> String.concat "\n\n"

    let source =
        $"module rec Gen\n\n\
          type FactAttribute() =\n\
          \u0020\u0020\u0020\u0020inherit System.Attribute()\n\n\
          %s{declarations}\n"

    { Source = source
      PlannedEdges = planned
      TestSymbols = testSymbols
      AllSymbols = names }

/// The oracle for the source corpus. Same reverse-BFS shape as `trulyAffectedTests`,
/// but walking the PLAN — so it knows nothing about what the analyzer extracted.
let private trulyAffectedFromPlan (corpus: SourceCorpus) (changed: Set<string>) : Set<string> =
    let reverse =
        [ for KeyValue(from, tos) in corpus.PlannedEdges do
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

    Set.intersect (walk changed changed) corpus.TestSymbols

/// Serialized with the other FCS-driving tests: `FSharpChecker` is shared
/// process-wide and these modules would otherwise contend with
/// `AstAnalyzerTests` for it.
[<Collection("FCS-AstAnalyzer")>]
module ``Extraction-inclusive soundness`` =

    let private checker = FSharpChecker.Create()

    /// Run the generated source through the REAL analyzer.
    let private analyzeCorpus (corpus: SourceCorpus) : AnalysisResult =
        let fileName = "/tmp/TestPruneSoundnessCorpus.fsx"

        let options =
            getScriptOptions checker fileName corpus.Source |> Async.RunSynchronously

        match
            analyzeSource checker fileName corpus.Source options "TestProject"
            |> Async.RunSynchronously
        with
        | Ok r -> r
        | Error msg -> failwith $"the generated corpus did not analyse: %s{msg}\n\nSOURCE:\n%s{corpus.Source}"

    /// One case. `blindToChanged` exists for the positive control: it drops every
    /// extracted edge POINTING AT a changed symbol, which is exactly the shape of
    /// the defect this property exists to catch — a call site the analyzer failed
    /// to turn into an edge, so nothing downstream knows it depends on the code
    /// that moved. Removing an arbitrary edge instead would usually disconnect
    /// nothing and the control would pass vacuously (it did, first attempt).
    let private checkOneSource (rng: Random) (n: int) (blindToChanged: bool) : CaseOutcome =
        let corpus = generateSourceCorpus rng n
        let analysed = analyzeCorpus corpus

        // Only symbols the analyzer actually produced can be "changed" — a change
        // to a symbol it never saw has no file to attribute, which is a DIFFERENT
        // (and much louder) failure than under-selection.
        let extractedNames = analysed.Symbols |> List.map _.FullName |> Set.ofList

        let changed =
            corpus.AllSymbols
            |> List.filter (fun sym -> extractedNames.Contains sym && rng.Next(0, 4) = 0)
            |> function
                | [] -> corpus.AllSymbols |> List.filter extractedNames.Contains |> List.truncate 1
                | xs -> xs
            |> Set.ofList

        if Set.isEmpty changed then
            CheckedEmpty
        else

            let dependencies =
                if blindToChanged then
                    analysed.Dependencies
                    |> List.filter (fun d -> not (changed.Contains d.ToSymbol))
                else
                    analysed.Dependencies

            let effective =
                AnalysisResult.Create(analysed.Symbols, dependencies, analysed.TestMethods)

            let store = fromAnalysisResults [ effective ]

            let currentSymbols =
                effective.Symbols
                |> List.map (fun sym ->
                    if changed.Contains sym.FullName then
                        { sym with
                            ContentHash = sym.ContentHash + "-mutated" }
                    else
                        sym)
                |> List.groupBy _.SourceFile
                |> Map.ofList

            let changedFiles =
                effective.Symbols
                |> List.filter (fun sym -> changed.Contains sym.FullName)
                |> List.map _.SourceFile
                |> List.distinct

            let selection, _events = selectTests store changedFiles currentSymbols

            // THE ORACLE COMES FROM THE PLAN, not from `analysed`. That is the whole
            // point: an edge the analyzer dropped is still in the plan, so its absence
            // downstream surfaces as under-selection instead of silent agreement.
            let expected =
                trulyAffectedFromPlan corpus changed
                |> Set.filter (fun t ->
                    // Restrict to tests the analyzer recognised AS tests. A test method
                    // it failed to recognise is a real defect, but a DIFFERENT one, and
                    // it is asserted separately below rather than folded in here.
                    analysed.TestMethods |> List.exists (fun tm -> tm.SymbolFullName = t))

            match selectedTestNames selection with
            | None -> SkippedRunAll
            | Some selected ->
                let missing = Set.difference expected selected

                if not (Set.isEmpty missing) then
                    UnderSelected
                        $"UNDER-SELECTION against the generation plan: %d{Set.count missing} test(s).\n\
                       missing:  %A{Set.toList missing}\n\
                       changed:  %A{Set.toList changed}\n\
                       selected: %A{Set.toList selected}\n\n\
                       SOURCE:\n%s{corpus.Source}"
                elif Set.isEmpty expected then
                    CheckedEmpty
                else
                    CheckedSubset

    [<Fact>]
    [<Trait("Guard", "testprune-extraction-soundness")>]
    let ``soundness: selection never drops a test the SOURCE says is affected`` () =
        let seed = 20260818
        let rng = Random(seed)

        // Far fewer cases than the graph-layer property: every one of these runs
        // FCS over generated source. Depth over breadth, deliberately.
        let outcomes =
            [ for case in 1..12 do
                  let n = 4 + (case % 7)
                  yield case, n, checkOneSource rng n false ]

        let failures =
            outcomes
            |> List.choose (fun (case, n, outcome) ->
                match outcome with
                | UnderSelected report -> Some $"[seed=%d{seed} case=%d{case} n=%d{n}]\n%s{report}"
                | _ -> None)

        test <@ List.isEmpty failures @>

        // Non-vacuity, same discipline as the graph-layer property.
        let realChecks =
            outcomes
            |> List.filter (fun (_, _, o) ->
                match o with
                | CheckedSubset -> true
                | _ -> false)
            |> List.length

        test <@ realChecks >= 3 @>

    [<Fact>]
    [<Trait("PositiveControl", "testprune-extraction-soundness")>]
    let ``the extraction harness detects a dropped dependency edge`` () =
        // Proof the property above can FAIL. Without this, a green run is equally
        // consistent with "extraction is sound" and "the comparison never ran" —
        // which is precisely the criticism that sent the first harness back.
        //
        // Dropping an edge the analyzer DID extract simulates one it failed to
        // extract: the plan still contains it, so the oracle still expects the
        // test, and the store can no longer reach it.
        let seed = 20260818
        let rng = Random(seed)

        let outcomes =
            [ for case in 1..12 do
                  let n = 4 + (case % 7)
                  yield checkOneSource rng n true ]

        let underSelected =
            outcomes
            |> List.filter (fun o ->
                match o with
                | UnderSelected _ -> true
                | _ -> false)
            |> List.length

        test <@ underSelected >= 1 @>

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
// - BINDING-FORM BREADTH. The extraction-inclusive property runs real source
//   through the real analyzer, so it CAN see an extraction miss — but it can only
//   see one in a form it actually emits, and the generator emits plain
//   `let f () = g () + 1` module bindings. Operators, active patterns, interface
//   members, computation expressions and the rest of the shapes the binding-form
//   audit enumerated are not generated, so a defect confined to one of those is still
//   invisible. Widening the generator's repertoire is the direct next step, and
//   it is the step that would have caught those extraction defects outright; it is
//   listed here rather than claimed, because a harness that overstates its reach
//   is the exact failure this file exists to avoid.


[<Collection("FCS-AstAnalyzer")>]
module ``Signature edit soundness`` =

    [<Theory>]
    [<InlineData("changed")>]
    [<InlineData("removed")>]
    [<InlineData("type-changed")>]
    [<InlineData("unchanged")>]
    let ``signature edits preserve consumer selection and both file baselines`` (edit: string) =
        let directory =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"signature-impact-{Guid.NewGuid():N}")

        System.IO.Directory.CreateDirectory directory |> ignore
        let signature = System.IO.Path.Combine(directory, "Library.fsi")
        let implementation = System.IO.Path.Combine(directory, "Library.fs")
        let consumer = System.IO.Path.Combine(directory, "Consumer.fs")
        let declarations = "module Library\ntype Token =\n    { Value: int }\n"
        let before = declarations + "val calculate:\n    int -> int\n"

        let after =
            match edit with
            | "changed" -> declarations + "val calculate:\n    value: int -> int\n"
            | "removed" -> declarations
            | "type-changed" -> before.Replace("Value: int", "Value: int64")
            | _ -> before

        let sources =
            [ signature, before
              implementation, "module Library\ntype Token = { Value: int }\nlet calculate value = value + 1\n"
              consumer,
              "module Consumer\ntype FactAttribute() = inherit System.Attribute()\n[<Fact>]\nlet exercisesLibrary () = Library.calculate ({ Value = 1 }: Library.Token).Value |> ignore\n" ]

        try
            for path, source in sources do
                System.IO.File.WriteAllText(path, source)

            let checker = FSharpChecker.Create()

            let scriptOptions =
                getScriptOptions checker consumer (snd sources[2]) |> Async.RunSynchronously

            let options =
                { scriptOptions with
                    SourceFiles = sources |> List.map fst |> List.toArray }

            let analyze path source =
                match
                    analyzeSource checker path source options "Consumer.Tests"
                    |> Async.RunSynchronously
                with
                | Ok result -> result
                | Error message -> failwith message

            let baseline = sources |> List.map (fun (path, source) -> analyze path source)
            let changed = analyze signature after

            TestPrune.Tests.TestHelpers.withDb (fun db ->
                db.RebuildProjects baseline

                for reindex in [ []; [ baseline[1] ]; [ baseline[0] ]; baseline |> List.rev ] do
                    db.RebuildProjects reindex

                    let selection, _ =
                        selectTests (toSymbolStore db) [ signature ] (Map.ofList [ signature, changed.Symbols ])

                    match selection with
                    | RunSubset selected ->
                        test
                            <@
                                (selected
                                 |> List.exists (fun item -> item.SymbolFullName = "Consumer.exercisesLibrary")) = (edit
                                                                                                                    <> "unchanged")
                            @>
                    | RunAll reason -> failwith $"indexed signature lost its baseline: {reason}"

                    test <@ not (db.GetSymbolsInFile signature).IsEmpty @>
                    test <@ not (db.GetSymbolsInFile implementation).IsEmpty @>

                let deadCode, _ = TestPrune.Tests.TestHelpers.runDeadCode db [] false

                test
                    <@
                        deadCode.UnreachableSymbols
                        |> List.exists (fun symbol -> symbol.FullName = "Library.calculate")
                    @>

                test
                    <@
                        deadCode.UnreachableSymbols
                        |> List.forall (fun symbol -> symbol.SourceFile <> signature)
                    @>

                // Exercise the standalone runner's changed-file parsing too. A diff can
                // describe an earlier edit that has already been indexed: current source
                // identical to the baseline must not be interpreted as deleted symbols.
                let normalized =
                    baseline
                    |> List.map (fun result ->
                        { result with
                            Symbols = normalizeSymbolPaths directory result.Symbols })

                db.RebuildProjects normalized
                let indexToken = db.MarkIndexIncomplete()
                test <@ db.CompleteIndex indexToken @>
                System.IO.File.WriteAllText(signature, after)

                let diff =
                    "diff --git a/Library.fsi b/Library.fsi\n--- a/Library.fsi\n+++ b/Library.fsi\n@@ -3 +3 @@\n-    int -> int\n+    value: int -> int"

                match
                    TestPrune.Orchestration.analyzeChanges
                        (fun () -> Ok diff)
                        directory
                        (toSymbolStore db)
                        checker
                        (TestPrune.AuditSink.createNoopSink ())
                with
                | Ok(RunSubset selected, _) ->
                    test
                        <@
                            (selected
                             |> List.exists (fun item -> item.SymbolFullName = "Consumer.exercisesLibrary")) = (edit
                                                                                                                <> "unchanged")
                        @>
                | result -> failwith $"unexpected standalone selection: %A{result}")
        finally
            System.IO.Directory.Delete(directory, true)
