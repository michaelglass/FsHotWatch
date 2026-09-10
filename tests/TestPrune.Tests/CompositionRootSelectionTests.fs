module TestPrune.Tests.CompositionRootSelectionTests

// A one-line handler edit must not select the whole integration
// suite, and the fix must not buy that by dropping tests.
//
// THE SHAPE, measured on a real consumer graph (2026-08-14, 29 906 symbols):
//
//     Handlers.AdminJournal.translate          the edited handler
//       ← RouteEndpoints.productHandler        the match arm naming EVERY handler
//       ← LabRouteEndpoints.endpoints
//       ← TestServerFixture.TestServer.BuildAndStartOnce
//       ← TestServer.Start                     boots the entire application
//       ← TestServerPool → IntegrationTestFixture / BrowserTestFixture
//       ← 57 test classes / 537 test methods
//
// Every edge on that path is TRUE. `productHandler` really does reference
// `translate`, and the fixture really does boot the app. What is false is the
// conclusion — that editing one handler makes all 537 tests relevant. The graph
// cannot tell "wires X up" from "calls X", so the composition root is marked and
// the walk declines to propagate relevance THROUGH it.
//
// THE TRAP THIS FILE EXISTS TO AVOID. Narrowing selection is the direction that
// silently ships bugs: a selection test passes just as happily when the selector
// returns nothing. So every narrowing assertion here is paired with the SAME
// query over the SAME graph without the marker, which must return the full set.
// That pairing is the positive control — it proves the selector can still report
// presence, so an empty answer is a failure rather than a pass.
//
// Both stores are exercised on every case. `Database` is what ships (FsHotWatch
// calls `db.QueryAffectedTests` directly); `InMemoryStore` is what the soundness
// harness measures. If they disagree, the harness is grading a selector nobody
// runs — so `both stores agree` is asserted case by case.

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.InMemoryStore
open TestPrune.Tests.TestHelpers

// ---------------------------------------------------------------------------
// The fixture graph — the measured shape, minimised
// ---------------------------------------------------------------------------

[<Literal>]
let private TranslateHandler = "App.Handlers.translate"

[<Literal>]
let private OtherHandler = "App.Handlers.other"

/// The composition root: names every handler in order to wire them up.
[<Literal>]
let private Dispatch = "App.RouteEndpoints.productHandler"

/// Startup configuration the host applies — the OTHER shape a marker must serve.
/// Reached by the fixture DIRECTLY, not through the dispatch table, which is what
/// lets one marker serve both.
[<Literal>]
let private AntiforgeryConfig = "App.Web.AntiforgeryConfig.configure"

/// Boots the whole application, so it depends on the composition root.
[<Literal>]
let private FixtureBoot = "Tests.Fixtures.TestServer.Start"

[<Literal>]
let private TranslateTest = "Tests.JournalTranslateTests.``translate returns 400``"

[<Literal>]
let private OtherTest = "Tests.OtherRouteTests.``other route renders``"

[<Literal>]
let private UnrelatedTest = "Tests.BrowserTests.``dashboard loads``"

/// A UNIT test with a direct call edge to `OtherHandler`, in a second test
/// project. It exists to reproduce the trap that a global "is the answer empty?"
/// fail-safe walks straight into — see the multi-project test below.
[<Literal>]
let private UnitTest = "Tests.Unit.CompanyProfileTests.``products round-trip``"

[<Literal>]
let private IntegrationProject = "tests/App.Tests.Integration"

[<Literal>]
let private UnitProject = "tests/App.Tests.Unit"

let private allTestNames = [ TranslateTest; OtherTest; UnrelatedTest ]

let private symbol (name: string) (file: string) =
    { FullName = name
      Kind = Function
      SourceFile = file
      LineStart = 1
      LineEnd = 5
      ContentHash = ""
      IsExtern = false }

let private edge (from: string) (target: string) =
    { FromSymbol = from
      ToSymbol = target
      Kind = Calls
      Source = "core" }

let private testMethodIn (project: string) (name: string) =
    { SymbolFullName = name
      TestProject = project
      TestClass =
        (if project = UnitProject then
             "UnitTests"
         else
             "IntegrationTests")
      TestMethod = name }

let private testMethod (name: string) = testMethodIn IntegrationProject name

/// The graph, with the composition-root marker present or absent.
///
/// Edges read "depends on". The one that carries the whole result is
/// `TranslateTest → TranslateHandler`: TestPrune.Falco emits exactly that edge
/// for a route, attributing the route to its own tests. It is the alternative
/// attribution that makes cutting the composition edge safe rather than reckless
/// — without it there would be nothing left to select the right test.
let private graph (marked: bool) =
    { Symbols =
        [ symbol TranslateHandler "src/Handlers.fs"
          symbol OtherHandler "src/Handlers.fs"
          symbol Dispatch "src/RouteEndpoints.fs"
          symbol AntiforgeryConfig "src/AntiforgeryConfig.fs"
          symbol FixtureBoot "tests/Fixtures.fs"
          symbol TranslateTest "tests/JournalTranslateTests.fs"
          symbol OtherTest "tests/OtherRouteTests.fs"
          symbol UnrelatedTest "tests/BrowserTests.fs"
          symbol UnitTest "tests/unit/CompanyProfileTests.fs" ]
      Dependencies =
        [ edge Dispatch TranslateHandler
          edge Dispatch OtherHandler
          edge FixtureBoot Dispatch
          edge FixtureBoot AntiforgeryConfig
          edge TranslateTest FixtureBoot
          edge TranslateTest TranslateHandler // ← the Falco route→test edge
          edge OtherTest FixtureBoot
          edge UnrelatedTest FixtureBoot
          edge UnitTest OtherHandler ] // a plain call edge, no HTTP involved
      TestMethods = (allTestNames |> List.map testMethod) @ [ testMethodIn UnitProject UnitTest ]
      Attributes =
        if marked then
            [ { SymbolFullName = Dispatch
                AttributeName = "CompositionRootAttribute"
                ArgsJson = "[]" } ]
        else
            []
      ParentLinks = []
      Diagnostics = AnalysisDiagnostics.Zero }

// ---------------------------------------------------------------------------
// Running one case against BOTH stores
// ---------------------------------------------------------------------------

let private selectedByInMemory (marked: bool) (seeds: string list) =
    let store = fromAnalysisResults [ graph marked ]
    store.QueryAffectedTests seeds |> List.map _.SymbolFullName |> Set.ofList

let private selectedByDatabase (marked: bool) (seeds: string list) =
    let mutable result = Set.empty

    withDb (fun db ->
        db.RebuildProjects([ graph marked ])
        result <- db.QueryAffectedTests seeds |> List.map _.SymbolFullName |> Set.ofList)

    result

/// Selection for one (marker, seeds) case, asserted identical across both stores
/// before being returned. A divergence here means the soundness harness and the
/// shipping selector have drifted apart, which is worth failing on its own.
let private selected (marked: bool) (seeds: string list) =
    let inMemory = selectedByInMemory marked seeds
    let database = selectedByDatabase marked seeds
    test <@ inMemory = database @>
    database

// ---------------------------------------------------------------------------
// Direction 1 — it selects FEWER tests (the improvement)
// ---------------------------------------------------------------------------

module ``a handler edit stops at the composition root`` =

    [<Fact>]
    let ``unmarked, one handler drags in every fixture-using test`` () =
        // THE POSITIVE CONTROL for the narrowing test below, and the reproduction
        // of the defect: nothing about this graph stops `translate` reaching all
        // three tests through the dispatch table and the booting fixture.
        let affected = selected false [ TranslateHandler ]

        test <@ affected = Set.ofList allTestNames @>

    [<Fact>]
    let ``marked, the same edit selects only the route's own test`` () =
        let affected = selected true [ TranslateHandler ]

        // Exactly the Falco-attributed test — 3 → 1 on this graph, which is the
        // 537 → 4 measured against a real consumer's database.
        test <@ affected = Set.ofList [ TranslateTest ] @>

        // Not vacuous: something WAS selected. A selector that returned nothing
        // would satisfy "fewer tests" while being the worst possible outcome.
        test <@ not (Set.isEmpty affected) @>

    [<Fact>]
    let ``a handler with no alternative attribution falls back instead of selecting nothing`` () =
        // THE FAIL-SAFE, and the reason this feature is shippable at all.
        //
        // `OtherHandler` has no route→test edge — the shape of a real browser test
        // that navigates by CLICKING (`page.ClickAsync "#stop-impersonating"`)
        // rather than naming the URL, which Falco cannot attribute. Measured on
        // a real consumer, 3 of 32 handler files are in exactly this position.
        //
        // Barriering alone would answer "no tests affected" for such an edit: a
        // green gate that verified nothing, which is strictly worse than the
        // over-selection being fixed. So a barrier may narrow the answer, never
        // empty it — with nothing left, the markers are not trustworthy here and
        // the unbarriered walk is the honest fallback.
        let unmarked = selected false [ OtherHandler ]
        let marked = selected true [ OtherHandler ]

        // Every integration test is back, and nothing was lost against the
        // unbarriered answer.
        test <@ Set.isSubset (Set.ofList allTestNames) marked @>
        test <@ marked = unmarked @>

    [<Fact>]
    let ``a surviving sibling project does not mask an emptied one`` () =
        // THE REGRESSION TEST for a fail-safe that was wrong on first attempt, found
        // by measuring rather than reasoning. `OtherHandler` is called directly by a
        // UNIT test, so barriering leaves that unit test standing while the whole
        // integration suite disappears. A global "is the answer empty?" guard sees a
        // non-empty answer and does nothing — exactly the real
        // `CompanyProfile.saveProducts` case, which keeps 4 unit tests while all 537
        // integration tests vanish, including the browser test that is the only
        // thing catching a broken product-card save.
        //
        // Hence the rule is PER PROJECT: a project the unbarriered walk covers and
        // the barriered one does not gets its rows restored.
        let marked = selected true [ OtherHandler ]

        let integrationSelected = Set.intersect marked (Set.ofList allTestNames)

        test <@ integrationSelected = Set.ofList allTestNames @>
        test <@ marked.Contains UnitTest @>

    [<Fact>]
    let ``the fallback does not fire when the route IS attributed`` () =
        // The positive control for the fail-safe: it must not swallow the win. The
        // narrowing above and the fallback here run the same code path over the
        // same graph, and differ only in whether a route edge exists — so neither
        // result can be an artefact of the guard always (or never) firing.
        test <@ selected true [ TranslateHandler ] = Set.ofList [ TranslateTest ] @>
        test <@ Set.isSubset (Set.ofList allTestNames) (selected true [ OtherHandler ]) @>

// ---------------------------------------------------------------------------
// Direction 2 — it still selects every behaviour-coupled test (the safety property)
// ---------------------------------------------------------------------------

module ``recall is not regressed`` =

    [<Fact>]
    let ``a change TO the composition root still selects every host-booting test`` () =
        // The asymmetry that reconciles narrowing with recall: a
        // barrier REACHED stops the walk, a barrier CHANGED is an ordinary seed.
        // Rewired composition is exactly what host-booting tests verify.
        let affected = selected true [ Dispatch ]

        test <@ affected = Set.ofList allTestNames @>

    [<Fact>]
    let ``a startup-config change still selects every browser test`` () =
        // The reverted-AntiforgeryConfig case: every CSRF-protected POST 400s, and
        // the only thing that catches it is a browser test. Those tests reach the
        // config through app composition, so a naive terminator at the fixture
        // would guarantee they are never selected. This path does not pass through
        // the dispatch table, so marking the table leaves it untouched — verified
        // on the real graph, where `AntiforgeryConfig.configure` selects the same
        // 57 classes / 537 tests with and without the marker.
        let unmarked = selected false [ AntiforgeryConfig ]
        let marked = selected true [ AntiforgeryConfig ]

        test <@ marked = Set.ofList allTestNames @>
        test <@ marked = unmarked @>

    [<Fact>]
    let ``a change to the fixture itself still fans out to everything using it`` () =
        // The reviewer's named recall case — "a case that should fan out widely,
        // e.g. a change to TestServer.Start itself" — so the fix cannot degenerate
        // into "select less, always".
        let marked = selected true [ FixtureBoot ]

        test <@ marked = Set.ofList allTestNames @>
        test <@ marked = selected false [ FixtureBoot ] @>

    [<Fact>]
    let ``a marked root in the same batch as a handler propagates for both`` () =
        // Seeds are exempt as a SET, not one at a time: a batch that touched both
        // the handler and the root must behave like the root changed.
        let affected = selected true [ TranslateHandler; Dispatch ]

        test <@ affected = Set.ofList allTestNames @>

// ---------------------------------------------------------------------------
// Direction 3 — codebases that never opt in are untouched
// ---------------------------------------------------------------------------

module ``opt-in only`` =

    [<Fact>]
    let ``every seed selects identically with no marker present`` () =
        // The blast radius of this feature on an un-annotated repo is nil. Asserted
        // over every symbol rather than a sample, and non-vacuously: at least one
        // seed must actually have selected something.
        let cases =
            [ TranslateHandler, Set.ofList allTestNames
              OtherHandler, Set.ofList (UnitTest :: allTestNames)
              Dispatch, Set.ofList allTestNames
              AntiforgeryConfig, Set.ofList allTestNames
              FixtureBoot, Set.ofList allTestNames
              TranslateTest, Set.ofList [ TranslateTest ] ]

        for (seed, expected) in cases do
            test <@ (seed, selected false [ seed ]) = (seed, expected) @>

        test <@ cases |> List.forall (fun (_, e) -> not (Set.isEmpty e)) @>

    [<Fact>]
    let ``an unknown attribute name is not mistaken for the marker`` () =
        // Guards the name match. `CompositionRootish` must not barrier anything —
        // otherwise a stray attribute silently narrows a consumer's suite.
        let decoy =
            { graph false with
                Attributes =
                    [ { SymbolFullName = Dispatch
                        AttributeName = "CompositionRootishAttribute"
                        ArgsJson = "[]" } ] }

        let store = fromAnalysisResults [ decoy ]

        let affected =
            store.QueryAffectedTests [ TranslateHandler ]
            |> List.map _.SymbolFullName
            |> Set.ofList

        test <@ affected = Set.ofList allTestNames @>

    [<Fact>]
    let ``the unsuffixed spelling is honoured`` () =
        // FCS stores the DisplayName, which may or may not carry the `Attribute`
        // suffix depending on how the consumer declared it. Both spellings must
        // work, or the feature silently does nothing for half of them.
        let unsuffixed =
            { graph false with
                Attributes =
                    [ { SymbolFullName = Dispatch
                        AttributeName = "CompositionRoot"
                        ArgsJson = "[]" } ] }

        let store = fromAnalysisResults [ unsuffixed ]

        let affected =
            store.QueryAffectedTests [ TranslateHandler ]
            |> List.map _.SymbolFullName
            |> Set.ofList

        test <@ affected = Set.ofList [ TranslateTest ] @>
