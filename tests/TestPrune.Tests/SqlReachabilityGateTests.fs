module TestPrune.Tests.SqlReachabilityGateTests

// Reachability-gated SQL coupling (`SqlCoupling.reachabilityGated`).
//
// When every test runs against its own database state, rows a writer stores are visible
// only to the test that ran the writer. A changed writer W can then affect a test T through
// a reader R of W's table only if T itself executes W. The gate says exactly that: T follows
// the SQL edge R → W only when T reaches W through the other edges.
//
// Every narrowing assertion here is paired with the same query under
// `WalkPolicy.FollowAll`, which must still select the reader-only test: that is the
// positive control proving the SQL edge is in the graph and the gate, not a missing edge,
// is what drops the test. Both stores answer every case and must agree.

open System
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.InMemoryStore
open TestPrune.Sql
open TestPrune.Tests.TestHelpers

let private symbolOf (kind: SymbolKind) (name: string) =
    { FullName = name
      Kind = kind
      SourceFile = $"src/%s{name}.fs"
      LineStart = 1
      LineEnd = 5
      ContentHash = ""
      IsExtern = false }

let private calls (from: string) (target: string) =
    { FromSymbol = from
      ToSymbol = target
      Kind = Calls
      Source = "core" }

let private usesType (from: string) (target: string) =
    { FromSymbol = from
      ToSymbol = target
      Kind = UsesType
      Source = "core" }

/// The edge `SqlHydraExtension` emits: the reader depends on the writer.
let private sqlEdge (reader: string) (writer: string) =
    { FromSymbol = reader
      ToSymbol = writer
      Kind = SharedState
      Source = SqlCoupling.SqlHydraSource }

let private testIn (project: string) (name: string) =
    { SymbolFullName = name
      TestProject = project
      TestClass = "Tests"
      TestMethod = name }

let private graphOf
    (symbols: SymbolInfo list)
    (edges: Dependency list)
    (tests: TestMethodInfo list)
    (compositionRoots: string list)
    =
    { Symbols = symbols
      Dependencies = edges
      TestMethods = tests
      Attributes =
        compositionRoots
        |> List.map (fun name ->
            { SymbolFullName = name
              AttributeName = "CompositionRootAttribute"
              ArgsJson = "[]" })
      ParentLinks = []
      Diagnostics = AnalysisDiagnostics.Zero }

let private selectedByInMemory (policy: WalkPolicy) (graph: AnalysisResult) (seeds: string list) =
    (fromAnalysisResultsWith policy [ graph ]).QueryAffectedTests seeds
    |> List.map _.SymbolFullName
    |> Set.ofList

let private selectedByDatabase (policy: WalkPolicy) (graph: AnalysisResult) (seeds: string list) =
    let path = tempDbPath ()

    try
        let db = TestPrune.Database.Database.create (path, policy)
        db.RebuildProjects([ graph ])
        db.QueryAffectedTests seeds |> List.map _.SymbolFullName |> Set.ofList
    finally
        cleanupDb path

// ---------------------------------------------------------------------------
// The measured shape, minimised
// ---------------------------------------------------------------------------
//
//   toDbString ← recordEdit (writer) ~sql~ getBriefById (reader)
//
// A real consumer's one-line edit of `toDbString` selected 2,246 tests, most of them only
// because they read briefs through `getBriefById`. None of those runs `recordEdit`, and in
// a suite whose tests each start from a truncated, re-seeded database none can observe a
// row it wrote.

[<Literal>]
let private Leaf = "App.FailureMode.toDbString"

[<Literal>]
let private Writer = "App.BriefQueries.recordEdit"

[<Literal>]
let private Reader = "App.BriefQueries.getBriefById"

/// Seeds the test's database through the production writer, as a real seeder does.
[<Literal>]
let private Seeder = "Tests.Seeder.seedBriefs"

/// A class fixture whose construction runs the seeder.
[<Literal>]
let private Fixture = "Tests.BriefFixture"

/// Boots the application: wires every writer up, calls none of them for the test.
[<Literal>]
let private Boot = "App.Startup.configure"

[<Literal>]
let private RunsWriter = "Tests.``edits a brief and reads it back``"

[<Literal>]
let private ReaderOnly = "Tests.``renders a brief``"

[<Literal>]
let private SeededByFixture = "Tests.``renders a seeded brief``"

[<Literal>]
let private ThroughCompositionRoot = "Tests.``booted app renders a brief``"

[<Literal>]
let private Unrelated = "Tests.``unrelated``"

[<Literal>]
let private Project = "tests/App.Tests"

let private briefGraph =
    graphOf
        [ symbolOf Function Leaf
          symbolOf Function Writer
          symbolOf Function Reader
          symbolOf Function Seeder
          symbolOf Type Fixture
          symbolOf Function Boot
          symbolOf Function "App.Other.thing"
          symbolOf Function RunsWriter
          symbolOf Function ReaderOnly
          symbolOf Function SeededByFixture
          symbolOf Function ThroughCompositionRoot
          symbolOf Function Unrelated ]
        [ calls Writer Leaf
          sqlEdge Reader Writer
          calls Seeder Writer
          calls Fixture Seeder
          calls Boot Writer
          calls RunsWriter Writer
          calls RunsWriter Reader
          calls ReaderOnly Reader
          usesType SeededByFixture Fixture // the fixture edge AstAnalyzer emits per test method
          calls SeededByFixture Reader
          calls ThroughCompositionRoot Boot
          calls ThroughCompositionRoot Reader
          calls Unrelated "App.Other.thing" ]
        ([ RunsWriter; ReaderOnly; SeededByFixture; ThroughCompositionRoot; Unrelated ]
         |> List.map (testIn Project))
        [ Boot ]

/// One case's selection, asserted identical across both stores.
let private selected (policy: WalkPolicy) (seeds: string list) =
    let inMemory = selectedByInMemory policy briefGraph seeds
    let database = selectedByDatabase policy briefGraph seeds
    test <@ inMemory = database @>
    database

module ``a changed writer under the gate`` =

    [<Fact>]
    let ``following every edge, a reader-only test is selected through the SQL edge`` () =
        // THE POSITIVE CONTROL for every narrowing below: the SQL edge is in the graph and
        // the walk follows it into the reader's tests, including the one that only reaches
        // the writer through the composition root.
        test
            <@
                selected WalkPolicy.FollowAll [ Writer ] = Set.ofList
                    [ RunsWriter; ReaderOnly; SeededByFixture; ThroughCompositionRoot ]
            @>

    [<Fact>]
    let ``a test that runs the writer is selected`` () =
        test <@ (selected SqlCoupling.reachabilityGated [ Writer ]).Contains RunsWriter @>

    [<Fact>]
    let ``a test that only reads the writer's table is not selected`` () =
        let gated = selected SqlCoupling.reachabilityGated [ Writer ]

        test <@ not (gated.Contains ReaderOnly) @>
        test <@ (selected WalkPolicy.FollowAll [ Writer ]).Contains ReaderOnly @>

    [<Fact>]
    let ``a test whose fixture seeds through the writer is selected`` () =
        test <@ (selected SqlCoupling.reachabilityGated [ Writer ]).Contains SeededByFixture @>

    [<Fact>]
    let ``a writer reached only through a composition root does not select through a reader`` () =
        // The gate uses the walk's own barriers: a test that reaches the writer only
        // through `Boot` is not taken to execute it, exactly as it is not taken to execute
        // `Leaf` through `Boot`. Following every edge, the reader brings it back.
        test <@ not ((selected SqlCoupling.reachabilityGated [ Writer ]).Contains ThroughCompositionRoot) @>

    [<Fact>]
    let ``the whole gated selection for the writer and for what the writer calls`` () =
        let expected = Set.ofList [ RunsWriter; SeededByFixture ]

        test <@ selected SqlCoupling.reachabilityGated [ Writer ] = expected @>
        test <@ selected SqlCoupling.reachabilityGated [ Leaf ] = expected @>

    [<Fact>]
    let ``a changed reader still selects every test that calls it`` () =
        // The gate removes SQL edges only; a reader's own callers are reached by calls.
        test
            <@
                selected SqlCoupling.reachabilityGated [ Reader ] = Set.ofList
                    [ RunsWriter; ReaderOnly; SeededByFixture; ThroughCompositionRoot ]
            @>

    [<Fact>]
    let ``the database reports the policy it walks under`` () =
        let path = tempDbPath ()

        try
            test <@ (TestPrune.Database.Database.create path).WalkPolicy = WalkPolicy.FollowAll @>

            test
                <@
                    (TestPrune.Database.Database.create (path, SqlCoupling.reachabilityGated)).WalkPolicy = SqlCoupling.reachabilityGated
                @>
        finally
            cleanupDb path

// ---------------------------------------------------------------------------
// The gate, stated literally, against the walk on random graphs
// ---------------------------------------------------------------------------
//
// The stores implement the gate by not following SQL edges at all (see `WalkPolicy`). The
// oracle below states the gate per test instead: for test T, the SQL edge R → W is usable
// only when W is in T's reach over the other edges under the same barriers, and T is
// selected when a seed is reachable from T through the other edges plus its usable SQL
// edges. The per-seed composition-root fail-safe is applied on top, as the stores do.

type private RandomGraph =
    { Graph: AnalysisResult
      Seeds: string list
      Code: Dependency list
      Sql: Dependency list
      Tests: TestMethodInfo list
      Roots: Set<string> }

let private generate (rng: Random) : RandomGraph =
    let codeCount = rng.Next(4, 14)
    let testCount = rng.Next(1, 7)
    let code = List.init codeCount (fun i -> $"App.m%d{i}")

    let tests =
        List.init testCount (fun i -> testIn (if rng.Next 3 = 0 then "tests/B" else "tests/A") $"Tests.t%d{i}")

    let pick (names: string list) = names[rng.Next names.Length]

    let codeEdges =
        [ for _ in 1 .. rng.Next(codeCount, codeCount * 2) do
              let a, b = pick code, pick code

              if a <> b then
                  calls a b
          for t in tests do
              for _ in 1 .. rng.Next(1, 4) do
                  calls t.SymbolFullName (pick code) ]
        |> List.distinct

    let sqlEdges =
        [ for _ in 1 .. rng.Next(1, codeCount) do
              let reader, writer = pick code, pick code

              if reader <> writer then
                  sqlEdge reader writer ]
        |> List.distinct

    let roots =
        [ for _ in 1 .. rng.Next 3 do
              pick code ]
        |> Set.ofList

    let symbols =
        (code @ (tests |> List.map _.SymbolFullName)) |> List.map (symbolOf Function)

    let seeds = [ for _ in 1 .. rng.Next(1, 3) -> pick code ] |> List.distinct

    { Graph = graphOf symbols (codeEdges @ sqlEdges) tests (Set.toList roots)
      Seeds = seeds
      Code = codeEdges
      Sql = sqlEdges
      Tests = tests
      Roots = roots }

/// Nodes reachable from `origin` over `successors`, expanding `origin` and every reached
/// node that is not a barrier.
let private forwardReach (origin: string) (successors: string -> string list) (barriers: Set<string>) =
    let rec walk (seen: Set<string>) (pending: string list) =
        match pending with
        | [] -> seen
        | node :: rest ->
            let next =
                if node = origin || not (barriers.Contains node) then
                    successors node |> List.filter (seen.Contains >> not)
                else
                    []

            walk (Set.union seen (Set.ofList next)) (next @ rest)

    walk (Set.singleton origin) [ origin ]

let private oracle (g: RandomGraph) : Set<string> =
    let out (edges: Dependency list) (node: string) =
        edges |> List.filter (fun e -> e.FromSymbol = node) |> List.map _.ToSymbol

    let selectedFor (seed: string) (barriers: Set<string>) =
        g.Tests
        |> List.filter (fun t ->
            let origin = t.SymbolFullName
            let executes = forwardReach origin (out g.Code) barriers

            let gated (node: string) =
                out g.Code node @ (out g.Sql node |> List.filter executes.Contains)

            (forwardReach origin gated barriers).Contains seed)

    g.Seeds
    |> List.collect (fun seed ->
        let barriered = selectedFor seed (Set.remove seed g.Roots)
        let unbarriered = selectedFor seed Set.empty
        CompositionRoot.restoreEmptiedProjects _.TestProject barriered unbarriered)
    |> List.map _.SymbolFullName
    |> Set.ofList

let private cases (seed: int) (count: int) =
    let rng = Random seed
    List.init count (fun _ -> generate rng)

[<Fact>]
let ``the in-memory walk under the gate equals the per-test gate on random graphs`` () =
    for g in cases 952 400 do
        let expected = oracle g
        let actual = selectedByInMemory SqlCoupling.reachabilityGated g.Graph g.Seeds
        test <@ actual = expected @>

[<Fact>]
let ``the database walk under the gate equals the per-test gate on random graphs`` () =
    for g in cases 953 60 do
        let expected = oracle g
        let actual = selectedByDatabase SqlCoupling.reachabilityGated g.Graph g.Seeds
        test <@ actual = expected @>

[<Fact>]
let ``the random graphs exercise the gate`` () =
    // The negative control: on these graphs, following every edge selects tests the gate
    // drops. Without it, the two equality properties above could pass on graphs whose SQL
    // edges never matter.
    let widened =
        cases 952 400
        |> List.filter (fun g -> selectedByInMemory WalkPolicy.FollowAll g.Graph g.Seeds <> oracle g)

    test <@ widened.Length >= 40 @>
