namespace TestPrune.Falco

open System
open System.IO
open System.Text.RegularExpressions
open TestPrune.Ports
open TestPrune.Extensions

/// Route-based integration test filtering.
/// Scans integration test source files for URL patterns that map to changed handler files.
type FalcoRouteExtension(integrationTestProject: string, integrationTestDir: string) =

    let urlPatternToRegex (urlPattern: string) : Regex =
        // Replace {param} placeholders with a sentinel before escaping,
        // so we don't depend on Regex.Escape's treatment of braces
        // (which changed in .NET 9+).
        let placeholder = "__PARAM__"
        let withPlaceholders = Regex.Replace(urlPattern, @"\{[^}]+\}", placeholder)
        let escaped = Regex.Escape(withPlaceholders)
        let pattern = escaped.Replace(placeholder, "[^/]+")
        Regex($"(?:^|[\"'/])%s{pattern}(?:[\"'?#\\s]|$)", RegexOptions.Compiled)

    let findTestClassesInFiles (testFiles: string list) (regexes: Regex list) : string list =
        let mutable matchedClasses = Set.empty

        for testFile in testFiles do
            let content = File.ReadAllText(testFile)
            let hasMatch = regexes |> List.exists (fun regex -> regex.IsMatch(content))

            if hasMatch then
                let classPattern = Regex(@"^type\s+(\w+)\s*\(", RegexOptions.Multiline)

                let modulePattern =
                    Regex(@"^module\s+(?:``[^`]+``|[\w.]+\.)?(\w+)\s*=", RegexOptions.Multiline)

                for m in classPattern.Matches(content) do
                    matchedClasses <- matchedClasses |> Set.add m.Groups.[1].Value

                for m in modulePattern.Matches(content) do
                    matchedClasses <- matchedClasses |> Set.add m.Groups.[1].Value

        matchedClasses |> Set.toList

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

    interface ITestPruneExtension with
        member _.Name = "Falco Routes"

        member _.FindAffectedTests (routeStore: RouteStore) (changedFiles: string list) (repoRoot: string) =
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
