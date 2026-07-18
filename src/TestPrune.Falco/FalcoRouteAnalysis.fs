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

/// Route-based integration test filtering.
/// Scans integration test source files for URL patterns that map to changed handler files.
type FalcoRouteExtension(integrationTestProject: string, integrationTestDir: string, routeStore: RouteStore) =

    let urlPatternToRegex (urlPattern: string) : Regex =
        // Replace {param} placeholders with a sentinel before escaping,
        // so we don't depend on Regex.Escape's treatment of braces
        // (which changed in .NET 9+).
        let placeholder = "__PARAM__"
        let withPlaceholders = Regex.Replace(urlPattern, @"\{[^}]+\}", placeholder)
        let escaped = Regex.Escape(withPlaceholders)
        let pattern = escaped.Replace(placeholder, "[^/]+")
        Regex($"(?:^|[\"'/])%s{pattern}(?:[\"'?#\\s]|$)", RegexOptions.Compiled)

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
    let testAttributeNamePattern =
        Regex(
            @"(?:^|;)\s*(?:[\w.]+\.)?(?:Fact|Theory|TestCaseSource|TestCase|TestMethod|DataTestMethod|Test)(?:Attribute)?\s*(?:[(;]|$)",
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

    // Selection is per-declaration, not per-file: a URL match is attributed to
    // the top-level declaration whose textual span contains it. A declaration
    // starts at a `classPattern`/`modulePattern` match (both anchor at column 0)
    // and its span runs to the next such match or EOF. Each span's OWN text is
    // matched (never global match positions: a `{param}` wildcard is greedy, so
    // a whole-file scan can swallow the text between two URL occurrences and
    // hide the second declaration's match). Classes are always selectable;
    // modules only when their span carries a test attribute — a helper module
    // without tests can never run anything. When the file matches anywhere
    // OUTSIDE the selectable spans (file header, helper module, top-level lets),
    // we cannot tell which tests exercise the route through that shared text —
    // a helper constant may feed test classes that never mention the URL — so
    // we select every selectable declaration in the file, even when some other
    // span also matched directly: over-selection wastes time, under-selection
    // silently skips affected tests.
    let findTestClassesInFiles (testFiles: string list) (regexes: Regex list) : string list =
        testFiles
        |> List.collect (fun testFile ->
            let content = File.ReadAllText(testFile)

            if regexes |> List.exists (fun regex -> regex.IsMatch(content)) |> not then
                []
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

                let selectable, nonSelectable =
                    spans |> List.partition (fun span -> span.IsClass || hasTestAttribute span.Text)

                let directlyMatched =
                    selectable
                    |> List.filter (fun span -> regexes |> List.exists (fun regex -> regex.IsMatch(span.Text)))

                // The text outside every selectable span: the header before the
                // first declaration plus each non-selectable span. Each piece is
                // matched on its own, like the spans above.
                let headerText =
                    match declarations with
                    | (firstStart, _, _) :: _ -> content.Substring(0, firstStart)
                    | [] -> content

                let matchesOutsideSelectable =
                    headerText :: (nonSelectable |> List.map (fun span -> span.Text))
                    |> List.exists (fun text -> regexes |> List.exists (fun regex -> regex.IsMatch(text)))

                let selected =
                    if matchesOutsideSelectable then
                        selectable
                    else
                        directlyMatched

                selected |> List.map (fun span -> span.Name))
        |> List.distinct

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
    /// Returns AffectedTest list for backward compatibility.
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
            let regexes = affectedUrlPatterns |> List.map urlPatternToRegex
            let testFiles = findTestFiles repoRoot
            let classes = findTestClassesInFiles testFiles regexes

            classes
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

                // Resolve the test-method symbols for a single test class by the same
                // suffix/contains idiom the file-level path uses.
                let testMethodsForClass (testClass: string) =
                    allSymbols
                    |> List.filter (fun s ->
                        s.FullName.Contains($".%s{testClass}.")
                        || s.FullName.EndsWith($".%s{testClass}"))

                // Edges for one route served by a changed handler file. Tests are matched by
                // THIS route's URL only (per-route regex), so an unrelated route in the same
                // file contributes no edges — and each route's tests are scoped to the handler
                // function serving it, via core's shared edge-emission helper. `None` (a seed
                // that cannot name the function) falls back to the whole file's symbols; so
                // does a name that no longer resolves, since dropping the route's tests
                // entirely would under-select.
                let edgesForRoute (changedFile: string) (entry: RouteHandlerEntry) : Dependency list =
                    let regex = urlPatternToRegex entry.UrlPattern
                    let testClasses = findTestClassesInFiles testFiles [ regex ]
                    let routeTestMethods = testClasses |> List.collect testMethodsForClass

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
