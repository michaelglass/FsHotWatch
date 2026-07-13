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

    let findTestClassesInFiles (testFiles: string list) (regexes: Regex list) : string list =
        testFiles
        |> List.collect (fun testFile ->
            let content = File.ReadAllText(testFile)

            if regexes |> List.exists (fun regex -> regex.IsMatch(content)) then
                let classes =
                    classPattern.Matches(content)
                    |> Seq.map (fun m -> m.Groups.[1].Value)
                    |> Seq.toList

                let modules =
                    modulePattern.Matches(content)
                    |> Seq.map (fun m -> m.Groups.[1].Value)
                    |> Seq.toList

                classes @ modules
            else
                [])
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
