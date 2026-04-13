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

        member this.AnalyzeEdges (symbolStore: SymbolStore) (changedFiles: string list) (repoRoot: string) =
            let affectedClasses = this.FindAffectedTestClasses(changedFiles, repoRoot)

            // For each changed handler file, get its symbols
            let handlerSourceFiles = routeStore.GetAllHandlerSourceFiles()

            let changedHandlerSymbols =
                changedFiles
                |> List.filter (fun f -> handlerSourceFiles |> Set.contains f)
                |> List.collect symbolStore.GetSymbolsInFile

            // For each affected test class, find test methods via symbol store
            let affectedTestMethods =
                affectedClasses
                |> List.collect (fun at ->
                    symbolStore.GetAllSymbols()
                    |> List.filter (fun s ->
                        s.FullName.Contains($".%s{at.TestClass}.")
                        || s.FullName.EndsWith($".%s{at.TestClass}")))

            // Create edges between each handler symbol and each affected test symbol
            [ for handler in changedHandlerSymbols do
                  for testSym in affectedTestMethods do
                      { FromSymbol = testSym.FullName
                        ToSymbol = handler.FullName
                        Kind = SharedState
                        Source = "falco" } ]
