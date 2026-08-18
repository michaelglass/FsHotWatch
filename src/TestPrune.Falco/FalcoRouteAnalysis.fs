namespace TestPrune.Falco

open System.IO
open System.Text.RegularExpressions
open TestPrune
open TestPrune.AstAnalyzer
open TestPrune.EdgeEmission
open TestPrune.Ports
open TestPrune.Extensions

/// A test class selected by route matching, as returned by
/// `FalcoRouteExtension.FindAffectedTestClasses`.
type AffectedTest =
    { TestProject: string
      TestClass: string }

/// A top-level declaration (test class or test module) in a test file, carrying
/// the text of its own span for per-declaration URL match attribution.
type private DeclarationSpan =
    { Name: string
      IsClass: bool
      Text: string }

/// The two DIFFERENT answers a route match has to give, kept apart because
/// conflating them is a bug in one direction or the other.
///
///   1. "Which classes should I RUN as test classes?" — a fixture holds no test
///      method, so naming it runs nothing; it belongs in `TestClasses` only if
///      `isTestBearing` says it can hold a runnable test.
///   2. "Whose symbols carry this route across the HTTP boundary?" — a fixture
///      that hits the URL is exactly such a carrier. Its members are what the
///      tests in other files depend on, and core's `QueryAffectedTests` is a
///      TRANSITIVE reverse-walk that only ever reports rows joined to
///      `test_methods`. So a fixture symbol on this path can never make a
///      non-test run: it can only widen the set of real tests reached.
///
/// Answering (2) with (1)'s set drops truly-affected tests: in the intelligence
/// consumer `IntegrationTestFixture` authenticates via `/login/verify`, and the
/// reverse-walk through its members reaches 37 test classes / 324 test methods
/// (of 50 / 520) — every test that logs in. Answering (1) with (2)'s set
/// over-selects, returning fixtures as "test classes".
type private RouteMatch =
    {
        /// Declarations to run as test classes. Test-bearing spans only.
        TestClasses: string list
        /// Declarations whose symbols participate in the route's dependency edge.
        /// A superset of `TestClasses`: every span whose OWN text matches the
        /// route, test-bearing or not.
        EdgeParticipants: string list
    }

/// Route-based integration test filtering.
/// Scans integration test source files for URL patterns that map to changed handler files.
type FalcoRouteExtension(integrationTestProject: string, integrationTestDir: string, routeStore: RouteStore) =

    // Every source file under the repo, read once. Shared by the two additive resolvers below
    // (Falco.UnionRoutes case→URL links and plain string-route URL constants), so the repo is
    // walked and read a single time per extension instance rather than once per resolver.
    let mutable repoFilesCache: (string * string) list option = None

    let getRepoFiles (repoRoot: string) : (string * string) list =
        match repoFilesCache with
        | Some files -> files
        | None ->
            let files =
                if Directory.Exists repoRoot then
                    SafeWalk.enumerateFiles "*.fs" repoRoot
                    |> Seq.choose (fun path ->
                        try
                            Some(path, File.ReadAllText path)
                        with _ ->
                            None)
                    |> List.ofSeq
                else
                    []

            repoFilesCache <- Some files
            files

    // Case → URL links for Falco.UnionRoutes symbolic navigation, derived once per repo from
    // the route DU's `[<Route(Path=...)>]` attributes (empty for plain string-route repos).
    let mutable linkMapCache: Map<string, Set<string>> option = None

    let getLinkMap (repoRoot: string) : Map<string, Set<string>> =
        match linkMapCache with
        | Some m -> m
        | None ->
            let routeDuFiles =
                getRepoFiles repoRoot |> List.filter (fun (_, text) -> text.Contains "[<Route(")

            let m = UnionRouteLinks.buildLinkMap routeDuFiles
            linkMapCache <- Some m
            m

    /// One `{param}` placeholder in a route pattern. Bound once because both readers of a
    /// route pattern strip placeholders — `carriesOnlySeparators` to see what literal text
    /// is left, `urlPatternToRegex` to swap in a match-any segment.
    let routeParamPattern = Regex(@"\{[^}]+\}")

    /// True when a route pattern carries no literal path text of its own: everything left
    /// after removing `{param}` placeholders is separators. The root route `/` and a
    /// param-only route like `/{lang}` qualify; `/users` does not.
    let carriesOnlySeparators (urlPattern: string) : bool =
        routeParamPattern.Replace(urlPattern, "") |> String.forall (fun c -> c = '/')

    let urlPatternToRegex (urlPattern: string) : Regex =
        // Replace {param} placeholders with a sentinel before escaping,
        // so we don't depend on Regex.Escape's treatment of braces
        // (which changed in .NET 9+).
        let placeholder = "__PARAM__"
        let withPlaceholders = routeParamPattern.Replace(urlPattern, placeholder)
        let escaped = Regex.Escape(withPlaceholders)
        let pattern = escaped.Replace(placeholder, "[^/]+")

        // The opening boundary normally admits a `/` so a doubled separator still reads as a
        // path start. For a pattern with no literal text of its own that `/` pairs with the
        // pattern's OWN leading `/` to match `//` — the F# COMMENT token — and `^` lets a
        // file-opening `// license header` do the same. Such a route then matches every
        // commented line in the repo and selects the entire suite.
        //
        // Measured on the intelligence consumer, route `/` matched 4,886 comment openers
        // (`// `, `/// `, and their bare-line forms) against 43 real URL literals, and so
        // matched 65 of its 65 integration test files. Requiring a QUOTE for these patterns
        // drops only the comment matches: every quoted literal (`"/"`, `"/?lang=en"`, `'/'`)
        // still matches, because a quote opens it.
        //
        // Scoped to text-free patterns rather than applied to every route: across that
        // consumer's other 175 route patterns the `/` alternative changed no file's outcome,
        // but losing a match is the dangerous direction, so a route with text of its own
        // keeps the broader boundary.
        let openingBoundary =
            if carriesOnlySeparators urlPattern then
                "[\"']"
            else
                "^|[\"'/]"

        // `/?` before the closing boundary tolerates a trailing slash — `/users/` matches route
        // `/users` — WITHOUT enabling parent-prefix matching: `/users/123` still does not match
        // `/users`, because after the optional slash the boundary must be end/quote/?/#/space, and
        // `1` is none of those.
        Regex($"(?:%s{openingBoundary})%s{pattern}/?(?:[\"'?#\\s]|$)", RegexOptions.Compiled)

    let classPattern = Regex(@"^type\s+(\w+)\s*\(", RegexOptions.Multiline)

    let modulePattern =
        Regex(@"^module\s+(?:``[^`]+``|[\w.]+\.)?(\w+)\s*=", RegexOptions.Multiline)

    // An attribute block: `[<` up to the FIRST `>]`, possibly spanning lines.
    // Purely textual, consistent with the rest of this file: a `>]` inside a
    // string argument would close the block early (and a block whose `>]` only
    // ever appears inside a string would never close) — both are rare enough
    // to accept.
    let attributeBlockPattern =
        Regex(@"\[<(.*?)>\]", RegexOptions.Compiled ||| RegexOptions.Singleline)

    // Textual spellings of the test attributes core's AST analysis recognises
    // (xUnit / NUnit / MSTest — see `knownTestAttributes` in AstAnalyzer),
    // matched against the CONTENTS of one `[<...>]` block. The name may open
    // the block (`[<Fact>]`) or follow a `;` inside a combined list
    // (`[<Trait(...); Fact>]`), with an optional dotted qualifier and
    // `Attribute` suffix, and is terminated by an argument list, the next
    // `;`, or the end of the block.
    //
    // The `\w*` before `Fact`/`Theory` also admits the xUnit convention of
    // SUBCLASSING `FactAttribute` (`[<SkippableFact>]`, `[<WindowsTheory>]`):
    // those declare real tests, and a span with no recognised marker is DROPPED,
    // so failing to recognise one loses tests. The alternatives are capitalised,
    // so `[<Artifact>]` does not match.
    let testAttributeNamePattern =
        Regex(
            @"(?:^|;)\s*(?:[\w.]+\.)?(?:\w*(?:Fact|Theory)|TestCaseSource|TestCase|TestMethod|DataTestMethod|Test)(?:Attribute)?\s*(?:[(;]|$)",
            RegexOptions.Compiled
        )

    // A span holds a test marker only when one of its ATTRIBUTE BLOCKS names a
    // test attribute. A module whose span carries no such block holds no
    // tests, so selecting it could never run anything — and an attribute-like
    // name in ordinary code (`let cases = [ users; TestCase(1) ]`) is not an
    // attribute and must not make a helper module count as test-bearing.
    let hasTestAttribute (text: string) : bool =
        attributeBlockPattern.Matches(text)
        |> Seq.exists (fun m -> testAttributeNamePattern.IsMatch(m.Groups.[1].Value))

    // An `inherit BaseType(...)` clause opening a class body. Only the keyword
    // at the start of an indented line counts, so the word inside a comment or
    // a string does not.
    let inheritPattern = Regex(@"^[ \t]+inherit[ \t]+\S", RegexOptions.Multiline)

    /// A declaration is selectable only when its OWN span shows evidence that it
    /// can contribute a runnable test: a test attribute, or — classes only — an
    /// `inherit` clause, because xUnit also runs the test methods a BASE class
    /// declares (`type PostgresContractTests() = inherit ContractTests(pg)` has
    /// no marker of its own yet runs the base's facts).
    ///
    /// A class with neither is positively identifiable as a non-test
    /// declaration: a fixture (`type IntegrationTestFixture()`), a collection
    /// marker (`[<CollectionDefinition(..)>] type FooCollection() = class end`),
    /// or a plain helper (`type TestServer(..)`, `type BrowserErrorTracker()`).
    /// Naming one as an affected "test class" filters to zero tests. Merely
    /// implementing `IClassFixture`/`ICollectionFixture` is not evidence either:
    /// that wires a fixture in, it does not declare a test.
    ///
    /// This governs RUN SELECTION only. A fixture excluded here still
    /// participates in the route's dependency edges — see `RouteMatch`, and do
    /// not reuse this predicate on that path.
    let isTestBearing (span: DeclarationSpan) : bool =
        hasTestAttribute span.Text
        || (span.IsClass && inheritPattern.IsMatch(span.Text))

    // Selection is per-declaration, not per-file: a URL match is attributed to the
    // top-level declaration whose textual span contains it. A declaration starts at a
    // `classPattern`/`modulePattern` match (both anchor at column 0) and runs to the
    // next such match or EOF. Match each span's OWN text, never global match positions
    // — a `{param}` wildcard is greedy, so a whole-file scan can swallow the text
    // between two URL occurrences and hide the second declaration's match.
    //
    // When the file matches anywhere OUTSIDE the selectable spans (header, helper module
    // or fixture, top-level lets) we cannot tell which tests reach the route through that
    // shared text — a helper constant may feed test classes that never mention the URL —
    // so every selectable declaration in the file is selected: over-selection wastes time,
    // under-selection silently skips affected tests. A file whose ONLY declarations are
    // non-selectable therefore contributes no TEST CLASS, though it can still contribute
    // EDGE PARTICIPANTS — see `RouteMatch`.
    let matchDeclarationsInFiles (testFiles: string list) (regexes: Regex list) : RouteMatch =
        let perFile =
            testFiles
            |> List.map (fun testFile ->
                let content = File.ReadAllText(testFile)

                if regexes |> List.exists (fun regex -> regex.IsMatch(content)) |> not then
                    [], []
                else
                    let declarations =
                        [ for m in classPattern.Matches(content) -> m.Index, m.Groups.[1].Value, true
                          for m in modulePattern.Matches(content) -> m.Index, m.Groups.[1].Value, false ]
                        |> List.sortBy (fun (start, _, _) -> start)

                    let spans =
                        declarations
                        |> List.mapi (fun i (start, name, isClass) ->
                            let finish =
                                match declarations |> List.tryItem (i + 1) with
                                | Some(nextStart, _, _) -> nextStart
                                | None -> content.Length

                            { Name = name
                              IsClass = isClass
                              Text = content.Substring(start, finish - start) })

                    let matchesText (text: string) =
                        regexes |> List.exists (fun regex -> regex.IsMatch(text))

                    let selectable, nonSelectable = spans |> List.partition isTestBearing

                    let directlyMatched = selectable |> List.filter (fun span -> matchesText span.Text)

                    // The text outside every selectable span: the header before the
                    // first declaration plus each non-selectable span. Each piece is
                    // matched on its own, like the spans above.
                    let headerText =
                        match declarations with
                        | (firstStart, _, _) :: _ -> content.Substring(0, firstStart)
                        | [] -> content

                    let matchesOutsideSelectable =
                        headerText :: (nonSelectable |> List.map (fun span -> span.Text))
                        |> List.exists matchesText

                    let testClasses =
                        if matchesOutsideSelectable then
                            selectable
                        else
                            directlyMatched

                    // Every span holding the route URL carries the route across the
                    // HTTP boundary, whether or not it can run a test: a fixture that
                    // calls the endpoint is precisely the symbol the tests in other
                    // files depend on.
                    //
                    // This path has no narrower "selectable" subset — every
                    // declaration participates — so a match inside ANY span is
                    // attributable to it and the run path's fallback must NOT fire
                    // here. Only the file HEADER belongs to no declaration, and a
                    // route matched there could be reached by any of them, so that
                    // (and only that) makes every declaration a carrier.
                    let routeCarriers =
                        if matchesText headerText then
                            spans
                        else
                            spans |> List.filter (fun span -> matchesText span.Text)

                    // Unioned with the test classes so a class the run path picked up
                    // only through its own fallback still gets its edge.
                    let edgeParticipants = routeCarriers @ testClasses

                    testClasses |> List.map (fun span -> span.Name),
                    edgeParticipants |> List.map (fun span -> span.Name))

        { TestClasses = perFile |> List.collect fst |> List.distinct
          EdgeParticipants = perFile |> List.collect snd |> List.distinct }

    let findTestFiles (repoRoot: string) : string list =
        let testDir = Path.Combine(repoRoot, integrationTestDir)

        if not (Directory.Exists(testDir)) then
            []
        else
            // SafeWalk, never AllDirectories: the latter follows directory
            // symlinks, and tests/*/bin holds Playwright's Nix-store browser
            // symlinks — walking those reaches /nix/store's self-loop symlinks
            // and never terminates (the 2026-07-13 wedge: fshw check hung 8h36m
            // here, silently, without ever launching a test). SafeWalk also
            // prunes bin/ and obj/ during traversal rather than filtering them
            // out afterwards, so their subtrees are never entered at all.
            SafeWalk.enumerateFiles "*.fs" testDir

    /// Find affected test classes using route-based matching.
    ///
    /// This is the RUN-SELECTION question, so it reads `RouteMatch.TestClasses` and
    /// nothing else: every name returned here becomes a test filter, and a fixture would
    /// filter to zero tests. `AnalyzeEdges` deliberately reads the OTHER field — read
    /// `RouteMatch` before merging the two call sites back together.
    member _.FindAffectedTestClasses(changedFiles: string list, repoRoot: string) : AffectedTest list =
        let handlerSourceFiles = routeStore.GetAllHandlerSourceFiles()

        let affectedUrlPatterns =
            changedFiles
            |> List.collect (fun file ->
                if handlerSourceFiles |> Set.contains file then
                    routeStore.GetUrlPatternsForSourceFile(file)
                else
                    [])
            |> List.distinct

        if affectedUrlPatterns.IsEmpty then
            []
        else
            let urlRegexes = affectedUrlPatterns |> List.map urlPatternToRegex
            let affectedUrls = Set.ofList affectedUrlPatterns

            // A test may navigate the affected route SYMBOLICALLY (`Route.link (Route.Admin(_,
            // AdminPages.Settings))`) with no URL literal in its span. These regexes match a
            // qualified reference to any route case whose composed URL is affected, and join the
            // URL regexes so the symbolic-nav test's class is attributed exactly like a literal
            // match. Empty for string-route repos — no behaviour change there.
            let leafRegexes =
                UnionRouteLinks.leafReferenceRegexes (getLinkMap repoRoot) affectedUrls

            // A test may also navigate via a NAMED URL CONSTANT (`navigateTo Routes.settingsUrl`)
            // whose literal lives in another file. These regexes match a reference to any constant
            // whose literal value is an affected route URL, attributed exactly like a literal match.
            let constantRegexes =
                let constantMap =
                    StringRouteConstants.buildConstantMap (getRepoFiles repoRoot) affectedUrls

                StringRouteConstants.constantReferenceRegexes constantMap urlRegexes

            let testFiles = findTestFiles repoRoot

            (matchDeclarationsInFiles testFiles (urlRegexes @ leafRegexes @ constantRegexes)).TestClasses
            |> List.map (fun cls ->
                { TestProject = integrationTestProject
                  TestClass = cls })

    interface ITestPruneExtension with
        member _.Name = "Falco Routes"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (changedFiles: string list) (repoRoot: string) =
            let handlerSourceFiles = routeStore.GetAllHandlerSourceFiles()

            let changedHandlerFiles =
                changedFiles |> List.filter (fun f -> handlerSourceFiles |> Set.contains f)

            if changedHandlerFiles.IsEmpty then
                []
            else
                let testFiles = findTestFiles repoRoot
                let allSymbols = symbolStore.GetAllSymbols()

                // Resolve the symbols belonging to a single declaration by the same
                // suffix/contains idiom the file-level path uses.
                let symbolsForDeclaration (declaration: string) =
                    allSymbols
                    |> List.filter (fun s ->
                        s.FullName.Contains($".%s{declaration}.")
                        || s.FullName.EndsWith($".%s{declaration}"))

                // Edges for one route served by a changed handler file. Tests are matched by
                // THIS route's URL only (per-route regex), so an unrelated route in the same
                // file contributes no edges — and each route's tests are scoped to the handler
                // function serving it, via core's shared edge-emission helper. A seed that
                // cannot name the function, or names one that no longer resolves, falls back
                // to the whole file's symbols; dropping the route's tests would under-select.
                //
                // This is the EDGE question, so it reads `RouteMatch.EdgeParticipants`, NOT
                // `TestClasses` — see `RouteMatch`.
                let edgesForRoute (changedFile: string) (entry: RouteHandlerEntry) : Dependency list =
                    let regex = urlPatternToRegex entry.UrlPattern
                    let affectedUrls = Set.singleton entry.UrlPattern

                    // Symbolic navigation to THIS route (see the run-selection path): a fixture or
                    // test that reaches the route via `Route.link (…)` or a named URL constant
                    // carries the route's edge too.
                    let leafRegexes =
                        UnionRouteLinks.leafReferenceRegexes (getLinkMap repoRoot) affectedUrls

                    let constantRegexes =
                        let constantMap =
                            StringRouteConstants.buildConstantMap (getRepoFiles repoRoot) affectedUrls

                        StringRouteConstants.constantReferenceRegexes constantMap [ regex ]

                    let participants =
                        (matchDeclarationsInFiles testFiles (regex :: (leafRegexes @ constantRegexes))).EdgeParticipants

                    let routeTestMethods = participants |> List.collect symbolsForDeclaration

                    let target =
                        match entry.HandlerFunction with
                        | Some handlerFunction -> NamedSymbol handlerFunction
                        | None -> UnnamedSymbol

                    let fileSymbols = symbolStore.GetSymbolsInFile changedFile

                    edgesTo "falco" SharedState fileSymbols target routeTestMethods

                changedHandlerFiles
                |> List.collect (fun changedFile ->
                    routeStore.GetRouteHandlersForSourceFile changedFile
                    |> List.collect (edgesForRoute changedFile))
                |> List.distinct
