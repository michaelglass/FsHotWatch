module TestPrune.InMemoryStore

open TestPrune.AstAnalyzer
open TestPrune.Ports

/// Create an in-memory SymbolStore from a list of AnalysisResults.
let fromAnalysisResults (results: AnalysisResult list) : SymbolStore =
    let allSymbols = results |> List.collect (fun r -> r.Symbols)
    let allDeps = results |> List.collect (fun r -> r.Dependencies)
    let allTests = results |> List.collect (fun r -> r.TestMethods)
    let allAttrs = results |> List.collect (fun r -> r.Attributes)
    let allParentLinks = results |> List.collect (fun r -> r.ParentLinks)

    let kindBySymbol =
        allSymbols |> List.map (fun s -> s.FullName, s.Kind) |> Map.ofList

    let parentByChild =
        allParentLinks |> List.map (fun l -> l.Child, l.Parent) |> Map.ofList

    let childrenByParent =
        allParentLinks
        |> List.groupBy (fun l -> l.Parent)
        |> Map.ofList
        |> Map.map (fun _ links -> links |> List.map (fun l -> l.Child) |> Set.ofList)

    let attrsBySymbol =
        allAttrs |> List.groupBy (fun a -> a.SymbolFullName) |> Map.ofList

    let symbolsByFile = allSymbols |> List.groupBy (fun s -> s.SourceFile) |> Map.ofList

    let symbolFileMap =
        allSymbols |> List.map (fun s -> s.FullName, s.SourceFile) |> Map.ofList

    let depsByFile =
        allDeps
        |> List.groupBy (fun d -> symbolFileMap |> Map.tryFind d.FromSymbol |> Option.defaultValue "")
        |> Map.ofList

    let testsByFile =
        allTests
        |> List.groupBy (fun t -> symbolFileMap |> Map.tryFind t.SymbolFullName |> Option.defaultValue "")
        |> Map.ofList

    let parentLinksByFile =
        allParentLinks
        |> List.groupBy (fun l -> symbolFileMap |> Map.tryFind l.Child |> Option.defaultValue "")
        |> Map.ofList

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
    let allSymbolNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList

    /// Transitive closure over `edges`, stopping at `barriers`.
    ///
    /// A barrier node is VISITED (it is genuinely affected) but not EXPANDED: the
    /// walk does not continue to whatever depends on it. Mirrors the `barriers`
    /// CTE in `Database.QueryAffectedTests` — the two must agree, or the soundness
    /// harness measures a different selector than the one that ships.
    let transitiveClosureWithBarriers
        (edges: Map<string, Set<string>>)
        (barriers: Set<string>)
        (seeds: string list)
        : Set<string> =
        let rec walk visited frontier =
            match frontier with
            | [] -> visited
            | node :: rest ->
                if Set.contains node visited then
                    walk visited rest
                else
                    let visited = Set.add node visited

                    let neighbors =
                        if Set.contains node barriers then
                            Set.empty
                        else
                            edges |> Map.tryFind node |> Option.defaultValue Set.empty

                    let newFrontier = Set.toList (neighbors - visited) @ rest
                    walk visited newFrontier

        walk Set.empty seeds

    let transitiveClosure (edges: Map<string, Set<string>>) (seeds: string list) : Set<string> =
        transitiveClosureWithBarriers edges Set.empty seeds

    /// Symbols carrying the `[<TestPrune.CompositionRoot>]` marker. Computed once;
    /// the per-query part is only subtracting that query's seeds.
    let compositionRoots =
        attrsBySymbol
        |> Map.toList
        |> List.filter (fun (_, attrs) ->
            attrs |> List.exists (fun a -> Domain.CompositionRoot.isMarker a.AttributeName))
        |> List.map fst
        |> Set.ofList

    // Mirrors the aggregate-type expansion in Database.QueryAffectedTests — see the
    // CTE there for the two-phase lift/expand rationale.
    let expandChanged (changedNames: string list) : string list =
        let isType name =
            kindBySymbol |> Map.tryFind name = Some Type

        let liftedParents =
            changedNames
            |> List.choose (fun name ->
                match parentByChild |> Map.tryFind name with
                | Some parent when isType parent -> Some parent
                | _ -> None)

        let afterLift = changedNames @ liftedParents |> List.distinct

        let expandedChildren =
            afterLift
            |> List.collect (fun name ->
                if isType name then
                    childrenByParent
                    |> Map.tryFind name
                    |> Option.defaultValue Set.empty
                    |> Set.toList
                else
                    [])

        afterLift @ expandedChildren |> List.distinct

    { GetSymbolsInFile = fun file -> symbolsByFile |> Map.tryFind file |> Option.defaultValue []
      GetDependenciesFromFile = fun file -> depsByFile |> Map.tryFind file |> Option.defaultValue []
      GetParentLinksInFile = fun file -> parentLinksByFile |> Map.tryFind file |> Option.defaultValue []
      GetTestMethodsInFile = fun file -> testsByFile |> Map.tryFind file |> Option.defaultValue []
      GetFileKey = fun _ -> None
      GetProjectKey = fun _ -> None
      QueryAffectedTests =
        fun changedNames ->
            let seeds = expandChanged changedNames

            // A composition root that CHANGED is an ordinary seed — the walk runs
            // past it in full, because rewired composition is exactly what
            // host-booting tests verify. Only a root REACHED from something it
            // aggregates stops the walk.
            let barriers = Set.difference compositionRoots (Set.ofList seeds)

            let testsIn (affected: Set<string>) =
                allTests |> List.filter (fun t -> Set.contains t.SymbolFullName affected)

            if Set.isEmpty compositionRoots then
                testsIn (transitiveClosure reverseEdges seeds)
            else
                let barriered = testsIn (transitiveClosureWithBarriers reverseEdges barriers seeds)
                let unbarriered = testsIn (transitiveClosure reverseEdges seeds)

                // Same per-project fail-safe as `Database.QueryAffectedTests`, and it
                // has to be the same rule or the soundness harness grades a more
                // permissive selector than the one that ships: a barrier may narrow a
                // project's selection, never empty it.
                let barrieredProjects = barriered |> List.map _.TestProject |> Set.ofList

                let restored =
                    unbarriered
                    |> List.filter (fun t -> not (Set.contains t.TestProject barrieredProjects))

                barriered @ restored
      GetAllSymbols = fun () -> allSymbols
      GetAllSymbolNames = fun () -> allSymbolNames
      GetReachableSymbols =
        fun entryPoints ->
            // Forward reachability, unlike QueryAffectedTests' reverse walk.
            transitiveClosure forwardEdges entryPoints
      GetTestMethodSymbolNames = fun () -> testMethodNames
      GetIncomingEdgesBatch =
        fun symbolNames ->
            symbolNames
            |> List.choose (fun name ->
                reverseEdges
                |> Map.tryFind name
                |> Option.map (fun froms -> name, Set.toList froms))
            |> Map.ofList
      GetAttributesForSymbol =
        fun symbolName ->
            attrsBySymbol
            |> Map.tryFind symbolName
            |> Option.defaultValue []
            |> List.map (fun a -> a.AttributeName, a.ArgsJson)
      GetAllAttributes =
        fun () ->
            attrsBySymbol
            |> Map.map (fun _ attrs -> attrs |> List.map (fun a -> a.AttributeName, a.ArgsJson)) }
