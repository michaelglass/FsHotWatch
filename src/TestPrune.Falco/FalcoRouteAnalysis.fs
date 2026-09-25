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
/// Answering (2) with (1)'s set drops truly-affected tests: in one measured
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

    // Every source file under the repo. Read afresh on every call: a daemon host keeps one
    // extension instance for its whole life, so text kept from an earlier call would describe
    // an older tree. Shared by the two additive resolvers below (Falco.UnionRoutes case→URL
    // links and plain string-route URL constants), so one call walks and reads the repo once.
    //
    // What IS kept is each file's parsed URL constants, keyed by its path and full text:
    // reusing one is indistinguishable from re-parsing the same text, and it spares a
    // whole-tree refresh from re-parsing the files that did not change.
    let constantParseCache = StringRouteConstants.ParseCache()

    let readRepoFiles (repoRoot: string) : (string * string) list =
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

    // Case → URL links for Falco.UnionRoutes symbolic navigation, derived from the route DU's
    // `[<Route(Path=...)>]` attributes (empty for plain string-route repos).
    let linkMapOf (repoFiles: (string * string) list) : Map<string, Set<string>> =
        repoFiles
        |> List.filter (fun (_, text) -> text.Contains "[<Route(")
        |> UnionRouteLinks.buildLinkMap

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
        // Measured on a real consumer, route `/` matched 4,886 comment openers
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

    let classPattern =
        Regex(
            @"^type[ \t]+(?:(?:private|internal|public)[ \t]+)?(?<name>``[^`]+``|[\w']+)\s*\(",
            RegexOptions.Multiline
        )

    let modulePattern =
        Regex(
            @"^module[ \t]+(?:(?:private|internal|public)[ \t]+)?(?:[\w']+\.)*(?<name>``[^`]+``|[\w']+)[ \t]*(?:(?<body>=)|\r?$)",
            RegexOptions.Multiline
        )

    let declarationName (matched: Match) = matched.Groups.["name"].Value.Trim('`')

    // Attribute blocks, scanned with STRING AWARENESS.
    //
    // This was `Regex(@"\[<(.*?)>\]")`, whose own comment conceded that a `>]`
    // inside a string argument closes the block early and accepted it as rare.
    // It is rare — but the direction is the dangerous one. Truncating
    // `[<Trait("k","v>]"); Fact>]` at the `>]` INSIDE the string leaves
    // `Trait("k","v`, so `Fact` never appears, `hasTestAttribute` returns false,
    // and the test becomes invisible to impact selection. The gate then goes
    // green having never run a test that was genuinely affected.
    //
    // Over-selection costs time; this costs a verdict. It is exactly the failure
    // the soundness work exists to prevent, so "rare" is not a reason to accept
    // it — a single instance buys a green that never ran.
    //
    // Blast radius measured before changing anything (as the ticket asked): ZERO
    // live instances in any test corpus, and TWO in a consumer's source, e.g. a
    // `Cmd` help string containing `--wait[=<minutes>]`. So this is a latent trap
    // rather than an active loss — and the pattern is demonstrably one a person
    // writes naturally, which is what makes leaving it unfixed a bet rather than
    // a judgement.
    //
    // Handles all three F# string forms, whose escaping rules differ:
    //   "…\"…"      regular, backslash escapes
    //   @"…""…"      verbatim, doubled quote escapes, backslash is literal
    //   """…"""      triple-quoted, no escapes at all
    let attributeBlocks (text: string) : string list =
        let blocks = ResizeArray<string>()
        let mutable i = 0

        while i < text.Length - 1 do
            if text.[i] = '[' && text.[i + 1] = '<' then
                let contentStart = i + 2
                let mutable j = contentStart
                let mutable closed = false

                while not closed && j < text.Length do
                    // Triple-quoted: no escapes, ends at the next """.
                    if
                        j + 2 < text.Length
                        && text.[j] = '"'
                        && text.[j + 1] = '"'
                        && text.[j + 2] = '"'
                    then
                        let close = text.IndexOf("\"\"\"", j + 3)
                        j <- if close < 0 then text.Length else close + 3
                    // Verbatim: `""` is an escaped quote, backslash is literal.
                    elif j + 1 < text.Length && text.[j] = '@' && text.[j + 1] = '"' then
                        let mutable k = j + 2
                        let mutable ended = false

                        while not ended && k < text.Length do
                            if text.[k] = '"' then
                                if k + 1 < text.Length && text.[k + 1] = '"' then
                                    k <- k + 2
                                else
                                    ended <- true
                                    k <- k + 1
                            else
                                k <- k + 1

                        j <- k
                    // Regular: backslash escapes the next character.
                    elif text.[j] = '"' then
                        let mutable k = j + 1
                        let mutable ended = false

                        while not ended && k < text.Length do
                            if text.[k] = '\\' && k + 1 < text.Length then
                                k <- k + 2
                            elif text.[k] = '"' then
                                ended <- true
                                k <- k + 1
                            else
                                k <- k + 1

                        j <- k
                    elif j + 1 < text.Length && text.[j] = '>' && text.[j + 1] = ']' then
                        blocks.Add(text.Substring(contentStart, j - contentStart))
                        closed <- true
                        j <- j + 2
                    else
                        j <- j + 1

                // An unterminated block consumes the rest of the text; resume
                // after the opener so a stray `[<` cannot swallow the file.
                i <- if closed then j else contentStart
            else
                i <- i + 1

        List.ofSeq blocks

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
        attributeBlocks text |> List.exists testAttributeNamePattern.IsMatch

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
    let matchDeclarationsInFiles (testFiles: (string * string) list) (regexes: Regex list) : RouteMatch =
        let perFile =
            testFiles
            |> List.map (fun (_, content) ->
                if regexes |> List.exists (fun regex -> regex.IsMatch(content)) |> not then
                    [], []
                else
                    let declarations =
                        let candidates =
                            [ for matched in classPattern.Matches(content) ->
                                  matched.Index, declarationName matched, true, true
                              for matched in modulePattern.Matches(content) ->
                                  matched.Index, declarationName matched, false, matched.Groups.["body"].Success ]
                            |> List.sortBy (fun (start, _, _, _) -> start)

                        match candidates with
                        | (start, _, false, false) :: (nextStart, _, _, _) :: _ when
                            content.Substring(start, nextStart - start) |> hasTestAttribute |> not
                            ->
                            List.tail candidates
                        | _ -> candidates
                        |> List.map (fun (start, name, isClass, _) -> start, name, isClass)

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

    /// Every integration test source file, with its text.
    let readTestFiles (repoRoot: string) : (string * string) list =
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
            |> Seq.map (fun path -> path, File.ReadAllText path)
            |> List.ofSeq

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
            let repoFiles = readRepoFiles repoRoot

            let leafRegexes =
                UnionRouteLinks.leafReferenceRegexes (linkMapOf repoFiles) affectedUrls

            // A test may also navigate via a NAMED URL CONSTANT (`navigateTo Routes.settingsUrl`)
            // whose literal lives in another file. These regexes match a reference to any constant
            // whose literal value is an affected route URL, attributed exactly like a literal match.
            let constantRegexes =
                let constantMap =
                    StringRouteConstants.buildConstantMapFrom (constantParseCache.Sources repoFiles) affectedUrls

                StringRouteConstants.constantReferenceRegexes constantMap urlRegexes

            let testFiles = readTestFiles repoRoot

            (matchDeclarationsInFiles testFiles (urlRegexes @ leafRegexes @ constantRegexes)).TestClasses
            |> List.map (fun cls ->
                { TestProject = integrationTestProject
                  TestClass = cls })

    interface ITestPruneExtension with
        member _.Name = "Falco Routes"

        /// Edges for EVERY route in the route table, derived from the tree as it stands: the
        /// host replaces this extension's stored edges with the answer, so a route missing
        /// here loses its edges. Nothing about which files changed enters into it.
        member _.AnalyzeEdges (symbolStore: SymbolStore) (repoRoot: string) =
            // One edge computation per distinct (URL, handler) pair: routes differing only
            // by HTTP method share their tests and their handler, hence their edges. Sorted
            // so the answer's order does not depend on the route table's row order.
            let routes =
                routeStore.GetAll()
                |> List.map (fun entry -> entry.UrlPattern, entry.HandlerSourceFile, entry.HandlerFunction)
                |> List.distinct
                |> List.sort

            if routes.IsEmpty then
                []
            else
                let testFiles = readTestFiles repoRoot
                let repoFiles = readRepoFiles repoRoot
                let linkMap = linkMapOf repoFiles
                let constantSources = constantParseCache.Sources repoFiles
                let allSymbols = symbolStore.GetAllSymbols()

                // Resolve the symbols belonging to a single declaration by the same
                // suffix/contains idiom the file-level path uses. Memoised: a declaration
                // carrying several routes is resolved once per call, not once per route.
                let declarationSymbols =
                    System.Collections.Generic.Dictionary<string, SymbolInfo list>()

                let symbolsForDeclaration (declaration: string) =
                    match declarationSymbols.TryGetValue declaration with
                    | true, symbols -> symbols
                    | false, _ ->
                        let symbols =
                            allSymbols
                            |> List.filter (fun s ->
                                s.FullName.Contains($".%s{declaration}.")
                                || s.FullName.EndsWith($".%s{declaration}"))

                        declarationSymbols[declaration] <- symbols
                        symbols

                let handlerFileSymbols =
                    System.Collections.Generic.Dictionary<string, SymbolInfo list>()

                let symbolsInHandlerFile (handlerFile: string) =
                    match handlerFileSymbols.TryGetValue handlerFile with
                    | true, symbols -> symbols
                    | false, _ ->
                        let symbols = symbolStore.GetSymbolsInFile handlerFile
                        handlerFileSymbols[handlerFile] <- symbols
                        symbols

                // Edges for one route. Tests are matched by THIS route's URL only (per-route
                // regex), so an unrelated route in the same handler file contributes no
                // edges — and each route's tests are scoped to the handler function serving
                // it, via core's shared edge-emission helper. A seed that cannot name the
                // function, or names one that no longer resolves, falls back to the whole
                // handler file's symbols; dropping the route's tests would under-select.
                //
                // This is the EDGE question, so it reads `RouteMatch.EdgeParticipants`, NOT
                // `TestClasses` — see `RouteMatch`.
                let edgesForRoute (urlPattern: string, handlerFile: string, handlerFunction: string option) =
                    let regex = urlPatternToRegex urlPattern
                    let affectedUrls = Set.singleton urlPattern

                    // Symbolic navigation to THIS route (see the run-selection path): a fixture or
                    // test that reaches the route via `Route.link (…)` or a named URL constant
                    // carries the route's edge too.
                    let leafRegexes = UnionRouteLinks.leafReferenceRegexes linkMap affectedUrls

                    let constantRegexes =
                        let constantMap =
                            StringRouteConstants.buildConstantMapFrom constantSources affectedUrls

                        StringRouteConstants.constantReferenceRegexes constantMap [ regex ]

                    let participants =
                        (matchDeclarationsInFiles testFiles (regex :: (leafRegexes @ constantRegexes))).EdgeParticipants

                    let routeTestMethods = participants |> List.collect symbolsForDeclaration

                    let target =
                        match handlerFunction with
                        | Some name -> NamedSymbol name
                        | None -> UnnamedSymbol

                    edgesTo "falco" SharedState (symbolsInHandlerFile handlerFile) target routeTestMethods

                routes |> List.collect edgesForRoute |> List.distinct
