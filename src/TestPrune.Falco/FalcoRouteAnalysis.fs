namespace TestPrune.Falco

open System
open System.IO
open System.Text.RegularExpressions
open TestPrune.AstAnalyzer
open TestPrune.Ports
open TestPrune.Extensions

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
            Directory.GetFiles(testDir, "*.fs", SearchOption.AllDirectories)
            |> Array.filter (fun f ->
                let n = f.Replace('\\', '/')
                not (n.Contains("/obj/")) && not (n.Contains("/bin/")))
            |> Array.toList

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

                // Edges for one route served by a changed handler file. Tests are
                // matched by THIS route's URL only (per-route regex), so an unrelated
                // route in the same file contributes no edges.
                let edgesForRoute (changedFile: string) (entry: RouteHandlerEntry) : Dependency list =
                    let regex = urlPatternToRegex entry.UrlPattern
                    let testClasses = findTestClassesInFiles testFiles [ regex ]

                    let routeTestMethods = testClasses |> List.collect testMethodsForClass

                    // Handler symbols this route's tests link to. With a resolved
                    // HandlerFunction, scope to just that function (matched by suffix,
                    // since the seed carries the short `Module.function` while the store
                    // holds the fully-qualified name); config-applied handlers carry the
                    // bare function symbol (e.g. `WellKnown.robots`), which the same
                    // suffix match resolves. With None, fall back to the whole file's
                    // symbols (today's file-level cross-product) for that route.
                    let handlerSymbols =
                        let fileSymbols = symbolStore.GetSymbolsInFile changedFile

                        match entry.HandlerFunction with
                        | Some handlerFunction ->
                            fileSymbols
                            |> List.filter (fun s -> s.FullName.EndsWith($".%s{handlerFunction}"))
                        | None -> fileSymbols

                    [ for handler in handlerSymbols do
                          for testSym in routeTestMethods do
                              { FromSymbol = testSym.FullName
                                ToSymbol = handler.FullName
                                Kind = SharedState
                                Source = "falco" } ]

                changedHandlerFiles
                |> List.collect (fun changedFile ->
                    routeStore.GetRouteHandlersForSourceFile changedFile
                    |> List.collect (edgesForRoute changedFile))
                |> List.distinct
