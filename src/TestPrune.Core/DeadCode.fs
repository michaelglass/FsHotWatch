module TestPrune.DeadCode

open System
open TestPrune.AstAnalyzer
open TestPrune.Database

type DeadCodeResult =
    { TotalSymbols: int
      ReachableSymbols: int
      UnreachableSymbols: SymbolInfo list }

/// Check whether a symbol name matches a wildcard pattern.
/// Supports * at start, end, or both (e.g., "*.main", "Prog.*", "*Route*").
let private matchesPattern (pattern: string) (name: string) =
    let startsWithStar = pattern.StartsWith("*", StringComparison.Ordinal)
    let endsWithStar = pattern.EndsWith("*", StringComparison.Ordinal)

    match startsWithStar, endsWithStar with
    | true, true ->
        let inner = pattern.Trim('*')
        name.Contains(inner, StringComparison.Ordinal)
    | true, false ->
        let suffix = pattern.TrimStart('*')
        name.EndsWith(suffix, StringComparison.Ordinal)
    | false, true ->
        let prefix = pattern.TrimEnd('*')
        name.StartsWith(prefix, StringComparison.Ordinal)
    | false, false -> name = pattern

/// Find symbols that are not reachable from the given entry point patterns.
let findDeadCode (db: Database) (entryPointPatterns: string list) : DeadCodeResult =
    let allSymbols = db.GetAllSymbols()
    let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList

    // Match entry point patterns against all symbol names
    let entryPoints =
        allNames
        |> Set.filter (fun name -> entryPointPatterns |> List.exists (fun pat -> matchesPattern pat name))
        |> Set.toList

    // Get all symbols reachable from entry points
    let reachable = db.GetReachableSymbols(entryPoints)

    // Find unreachable symbol names
    let unreachableNames = allNames - reachable

    // Get test method symbol names to exclude
    let testMethodNames = db.GetTestMethodSymbolNames()

    // Filter to unreachable symbols, excluding:
    // - Test methods (they're test entry points)
    // - Module declarations (containers)
    // - DU cases (part of parent type)
    // - Symbols in test files (anything under tests/)
    let unreachableSymbols =
        allSymbols
        |> List.filter (fun s ->
            unreachableNames |> Set.contains s.FullName
            && not (testMethodNames |> Set.contains s.FullName)
            && s.Kind <> Module
            && s.Kind <> DuCase
            && not (s.SourceFile.StartsWith("tests/", StringComparison.Ordinal)))

    { TotalSymbols = allNames.Count
      ReachableSymbols = reachable.Count
      UnreachableSymbols = unreachableSymbols }
