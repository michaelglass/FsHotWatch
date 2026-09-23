module TestPrune.Tests.SeedCoverageTests

// `QueryCoveringProjectsBySeed` answers, for EVERY seed at once, the question a caller
// otherwise asks one seed at a time: which test projects does `QueryAffectedTests [seed]`
// select? A consumer that classifies each queued symbol by its covering projects (does
// anything cover it? is it owed to a project this gate does not run?) paid one full
// recursive walk per symbol — thousands of walks for one wide change.
//
// Two properties hold it honest:
//  * EQUIVALENCE — for every seed, the grouped answer equals the test projects of the
//    single-seed query, over generated indexes that include cycles, type-member lifting,
//    composition-root barriers (a changed barrier, a barrier reached, and the per-project
//    fail-safe that restores what a barrier emptied), and symbols declared in both a
//    signature and an implementation file, whose edges are stored once per file.
//  * COST — the grouped query runs one recursive walk however many seeds it is given.

open System
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Tests.TestHelpers

let private symbolIn (file: string) (name: string) (kind: SymbolKind) =
    { FullName = name
      Kind = kind
      SourceFile = file
      LineStart = 1
      LineEnd = 2
      ContentHash = $"%s{file}:%s{name}"
      IsExtern = false }

let private symbolOf (name: string) (kind: SymbolKind) = symbolIn "src/Generated.fs" name kind

let private edgeOf (from: string) (target: string) =
    { FromSymbol = from
      ToSymbol = target
      Kind = Calls
      Source = "core" }

let private resultOf symbols dependencies testMethods attributes parentLinks =
    { Symbols = symbols
      Dependencies = dependencies
      TestMethods = testMethods
      Attributes = attributes
      ParentLinks = parentLinks
      Diagnostics = AnalysisDiagnostics.Zero }

let private projects = [| "tests/A.Tests"; "tests/B.Tests"; "tests/C.Tests" |]

/// A random index, one `AnalysisResult` per source file as the analyzer produces it.
/// Symbols (plain and types, some with members) spread over three implementation files;
/// some are also declared in the matching signature file, which contributes its own
/// occurrence and, for some of their edges, the SAME edge again, so the stored graph
/// carries an edge once per contributing file. Edges include cycles and self-loops; test
/// methods sit in three projects; when `withMarkers`, a few symbols are composition roots.
let private generateIndex (rng: Random) (withMarkers: bool) : AnalysisResult list =
    let n = rng.Next(4, 26)
    let name i = $"G.S%d{i}"
    let fileCount = 3

    let kinds =
        Array.init n (fun _ -> if rng.NextDouble() < 0.25 then Type else Function)

    let fileOf = Array.init n (fun _ -> rng.Next fileCount)
    let hasSignature = Array.init n (fun _ -> rng.NextDouble() < 0.35)
    let implFile k = $"src/F%d{k}.fs"
    let sigFile k = $"src/F%d{k}.fsi"

    let parentLinks =
        [ for child in 0 .. n - 1 do
              if rng.NextDouble() < 0.3 then
                  let parent = rng.Next n

                  if parent <> child then
                      child,
                      { Child = name child
                        Parent = name parent } ]

    let edges =
        [ for _ in 1 .. rng.Next(n, 3 * n) do
              rng.Next n, rng.Next n ]
        |> List.distinct

    let testMethods =
        [ for i in 0 .. n - 1 do
              if rng.NextDouble() < 0.35 then
                  i,
                  { SymbolFullName = name i
                    TestProject = projects[rng.Next projects.Length]
                    TestClass = "C"
                    TestMethod = name i } ]

    let attributes =
        if withMarkers then
            [ for i in 0 .. n - 1 do
                  if rng.NextDouble() < 0.2 then
                      i,
                      { SymbolFullName = name i
                        AttributeName =
                          (if rng.NextDouble() < 0.5 then
                               "CompositionRootAttribute"
                           else
                               "CompositionRoot")
                        ArgsJson = "[]" } ]
        else
            []

    // An edge the signature file repeats: its source is declared there too.
    let duplicated =
        edges
        |> List.filter (fun (from, _) -> hasSignature[from] && rng.NextDouble() < 0.6)

    let inFile k (facts: (int * 'T) list) =
        facts |> List.filter (fun (owner, _) -> fileOf[owner] = k) |> List.map snd

    [ for k in 0 .. fileCount - 1 do
          let declared = [ 0 .. n - 1 ] |> List.filter (fun i -> fileOf[i] = k)

          if not declared.IsEmpty then
              resultOf
                  (declared |> List.map (fun i -> symbolIn (implFile k) (name i) kinds[i]))
                  (edges
                   |> List.filter (fun (from, _) -> fileOf[from] = k)
                   |> List.map (fun (from, target) -> edgeOf (name from) (name target)))
                  (inFile k testMethods)
                  (inFile k attributes)
                  (inFile k parentLinks)

          let signed = declared |> List.filter (fun i -> hasSignature[i])

          if not signed.IsEmpty then
              resultOf
                  (signed |> List.map (fun i -> symbolIn (sigFile k) (name i) kinds[i]))
                  (duplicated
                   |> List.filter (fun (from, _) -> fileOf[from] = k)
                   |> List.map (fun (from, target) -> edgeOf (name from) (name target)))
                  []
                  []
                  [] ]

let private projectsOfSingleSeed (db: Database) (seed: string) =
    db.QueryAffectedTests [ seed ] |> List.map _.TestProject |> Set.ofList

let private checkCase (seed: int) (withMarkers: bool) =
    let rng = Random(seed)
    let index = generateIndex rng withMarkers

    withDb (fun db ->
        db.RebuildProjects index

        // Every symbol, plus one the index has never heard of.
        let seeds =
            (index |> List.collect _.Symbols |> List.map _.FullName |> List.distinct)
            @ [ "G.Unknown" ]

        let grouped = db.QueryCoveringProjectsBySeed seeds

        test <@ Set.ofSeq grouped.Keys = Set.ofList seeds @>

        for s in seeds do
            let expected = projectsOfSingleSeed db s
            let actual = grouped[s]

            if actual <> expected then
                failwith
                    $"graph seed %d{seed} (markers=%b{withMarkers}), symbol %s{s}: grouped %A{actual}, single-seed %A{expected}")

[<Fact>]
let ``each seed's covering projects equal its single-seed query, without markers`` () =
    for seed in 1..60 do
        checkCase seed false

[<Fact>]
let ``each seed's covering projects equal its single-seed query, with composition-root barriers`` () =
    for seed in 1001..1100 do
        checkCase seed true

[<Fact>]
let ``the fail-safe restores a project a barrier emptied, per seed`` () =
    // Handler ← Dispatch(marked) ← Boot ← IntegrationTest; Handler ← UnitTest.
    // Barriered, Handler reaches only the unit project, so the integration project is
    // emptied by the barrier and the fail-safe restores it. A seed that IS the barrier
    // expands through it.
    let graph =
        { Symbols =
            [ symbolOf "App.Handler" Function
              symbolOf "App.Dispatch" Function
              symbolOf "Tests.Boot" Function
              symbolOf "Tests.IntegrationTest" Function
              symbolOf "Tests.UnitTest" Function ]
          Dependencies =
            [ edgeOf "App.Dispatch" "App.Handler"
              edgeOf "Tests.Boot" "App.Dispatch"
              edgeOf "Tests.IntegrationTest" "Tests.Boot"
              edgeOf "Tests.UnitTest" "App.Handler" ]
          TestMethods =
            [ { SymbolFullName = "Tests.IntegrationTest"
                TestProject = "tests/Integration"
                TestClass = "I"
                TestMethod = "t" }
              { SymbolFullName = "Tests.UnitTest"
                TestProject = "tests/Unit"
                TestClass = "U"
                TestMethod = "t" } ]
          Attributes =
            [ { SymbolFullName = "App.Dispatch"
                AttributeName = "CompositionRootAttribute"
                ArgsJson = "[]" } ]
          ParentLinks = []
          Diagnostics = AnalysisDiagnostics.Zero }

    withDb (fun db ->
        db.RebuildProjects [ graph ]
        let grouped = db.QueryCoveringProjectsBySeed [ "App.Handler"; "App.Dispatch" ]

        test <@ grouped["App.Handler"] = projectsOfSingleSeed db "App.Handler" @>
        test <@ grouped["App.Handler"] = set [ "tests/Integration"; "tests/Unit" ] @>
        test <@ grouped["App.Dispatch"] = set [ "tests/Integration" ] @>)

[<Fact>]
let ``an edge contributed by both a signature and its implementation is walked once`` () =
    // `Lib.fsi` and `Lib.fs` both declare `Lib.calc` and both contribute its edge to
    // `Lib.Token`, so the stored graph holds that edge twice, once per file. A walk that
    // did not deduplicate it would still reach the same projects; one that dropped a row
    // per file would not. Either way the grouped answer must match the single-seed one.
    let signature =
        resultOf
            [ symbolIn "src/Lib.fsi" "Lib.Token" Type
              symbolIn "src/Lib.fsi" "Lib.calc" Function ]
            [ edgeOf "Lib.calc" "Lib.Token" ]
            []
            []
            []

    let implementation =
        resultOf
            [ symbolIn "src/Lib.fs" "Lib.Token" Type
              symbolIn "src/Lib.fs" "Lib.calc" Function ]
            [ edgeOf "Lib.calc" "Lib.Token" ]
            []
            []
            []

    let tests =
        resultOf
            [ symbolIn "tests/CalcTests.fs" "Tests.calcWorks" Function ]
            [ edgeOf "Tests.calcWorks" "Lib.calc" ]
            [ { SymbolFullName = "Tests.calcWorks"
                TestProject = "tests/Lib.Tests"
                TestClass = "CalcTests"
                TestMethod = "calcWorks" } ]
            []
            []

    withDb (fun db ->
        db.RebuildProjects [ signature; implementation; tests ]
        let seeds = [ "Lib.Token"; "Lib.calc"; "Tests.calcWorks" ]
        let grouped = db.QueryCoveringProjectsBySeed seeds

        for s in seeds do
            test <@ grouped[s] = projectsOfSingleSeed db s @>

        test <@ grouped["Lib.Token"] = set [ "tests/Lib.Tests" ] @>)

[<Fact>]
let ``an empty seed list answers an empty map without querying`` () =
    withDb (fun db ->
        let before = db.RecursiveWalks
        test <@ db.QueryCoveringProjectsBySeed [] = Map.empty @>
        test <@ db.RecursiveWalks = before @>)

[<Fact>]
let ``the grouped query is one recursive walk however many seeds it answers`` () =
    let index = generateIndex (Random 7) true
    let seeds = index |> List.collect _.Symbols |> List.map _.FullName |> List.distinct

    withDb (fun db ->
        db.RebuildProjects index

        // The positive control: the per-seed spelling this replaces walks at least once
        // per seed, so the counter can tell one walk from many.
        let beforeSingle = db.RecursiveWalks

        for s in seeds do
            db.QueryAffectedTests [ s ] |> ignore

        test <@ db.RecursiveWalks - beforeSingle >= int64 seeds.Length @>

        let before = db.RecursiveWalks
        db.QueryCoveringProjectsBySeed seeds |> ignore
        test <@ db.RecursiveWalks - before = 1L @>)
