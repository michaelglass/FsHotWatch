module TestPrune.InMemoryStore

open TestPrune.AstAnalyzer
open TestPrune.Ports

/// Create an in-memory SymbolStore from a list of AnalysisResults.
let fromAnalysisResults (results: AnalysisResult list) : SymbolStore =
    let allSymbols = results |> List.collect (fun r -> r.Symbols)
    let allDeps = results |> List.collect (fun r -> r.Dependencies)
    let allTests = results |> List.collect (fun r -> r.TestMethods)

    let symbolsByFile = allSymbols |> List.groupBy (fun s -> s.SourceFile) |> Map.ofList

    let depsByFile =
        allDeps
        |> List.groupBy (fun d ->
            allSymbols
            |> List.tryFind (fun s -> s.FullName = d.FromSymbol)
            |> Option.map (fun s -> s.SourceFile)
            |> Option.defaultValue "")
        |> Map.ofList

    let testsByFile =
        allTests
        |> List.groupBy (fun t ->
            allSymbols
            |> List.tryFind (fun s -> s.FullName = t.SymbolFullName)
            |> Option.map (fun s -> s.SourceFile)
            |> Option.defaultValue "")
        |> Map.ofList

    // Build adjacency list for transitive queries
    let forwardEdges = // from -> [to]
        allDeps
        |> List.groupBy (fun d -> d.FromSymbol)
        |> Map.ofList
        |> Map.map (fun _ deps -> deps |> List.map (fun d -> d.ToSymbol) |> Set.ofList)

    let reverseEdges = // to -> [from] (for QueryAffectedTests: who depends on this?)
        allDeps
        |> List.groupBy (fun d -> d.ToSymbol)
        |> Map.ofList
        |> Map.map (fun _ deps -> deps |> List.map (fun d -> d.FromSymbol) |> Set.ofList)

    let testMethodNames = allTests |> List.map (fun t -> t.SymbolFullName) |> Set.ofList

    // Transitive closure: follow edges from seeds
    let transitiveClosure (edges: Map<string, Set<string>>) (seeds: string list) : Set<string> =
        let rec walk visited frontier =
            match frontier with
            | [] -> visited
            | node :: rest ->
                if Set.contains node visited then
                    walk visited rest
                else
                    let visited = Set.add node visited

                    let neighbors = edges |> Map.tryFind node |> Option.defaultValue Set.empty

                    let newFrontier = Set.toList (neighbors - visited) @ rest
                    walk visited newFrontier

        walk Set.empty seeds

    { GetSymbolsInFile = fun file -> symbolsByFile |> Map.tryFind file |> Option.defaultValue []
      GetDependenciesFromFile = fun file -> depsByFile |> Map.tryFind file |> Option.defaultValue []
      GetTestMethodsInFile = fun file -> testsByFile |> Map.tryFind file |> Option.defaultValue []
      GetFileKey = fun _ -> None
      GetProjectKey = fun _ -> None
      QueryAffectedTests =
        fun changedNames ->
            // Find all symbols transitively depending on changedNames (reverse edges)
            let affected = transitiveClosure reverseEdges changedNames
            // Return test methods among the affected
            allTests |> List.filter (fun t -> Set.contains t.SymbolFullName affected)
      GetAllSymbols = fun () -> allSymbols
      GetAllSymbolNames = fun () -> allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList
      GetReachableSymbols =
        fun entryPoints ->
            // Forward reachability from entry points
            transitiveClosure forwardEdges entryPoints
      GetTestMethodSymbolNames = fun () -> testMethodNames }
