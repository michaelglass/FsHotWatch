module TestPrune.DeadCode

open System
open TestPrune.AstAnalyzer
open TestPrune.Domain

/// Result of dead code analysis containing total, reachable, and unreachable symbol counts.
type DeadCodeResult =
    { TotalSymbols: int
      ReachableSymbols: int
      UnreachableSymbols: SymbolInfo list }

type UnreachabilityReason =
    | NoIncomingEdges
    | DisconnectedFromEntryPoints of incomingFrom: string list

type UnreachableSymbolInfo =
    { Symbol: SymbolInfo
      Reason: UnreachabilityReason }

type VerboseDeadCodeResult =
    { TotalSymbols: int
      ReachableSymbols: int
      UnreachableSymbols: UnreachableSymbolInfo list }

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

/// Match entry point patterns against all symbol names and return the matching names.
let findEntryPoints (allNames: Set<string>) (entryPointPatterns: string list) : string list =
    allNames
    |> Set.filter (fun name -> entryPointPatterns |> List.exists (fun pat -> matchesPattern pat name))
    |> Set.toList

/// Find symbols that are not reachable from the given entry point patterns.
let findDeadCode
    (allSymbols: SymbolInfo list)
    (reachable: Set<string>)
    (testMethodNames: Set<string>)
    (includeTests: bool)
    : DeadCodeResult * AnalysisEvent list =
    let allNames = allSymbols |> List.map (fun s -> s.FullName) |> Set.ofList

    // Find unreachable symbol names
    let unreachableNames = allNames - reachable

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
            && (includeTests
                || not (s.SourceFile.StartsWith("tests/", StringComparison.Ordinal)))
            && not s.IsExtern)

    // Filter out local bindings and parameters — symbols without a dot in their
    // name are locals that aren't independently actionable. Also filter symbols
    // whose line range is contained within another symbol's range (for cases where
    // full body ranges are available).
    let isLocal (s: SymbolInfo) = not (s.FullName.Contains('.'))

    let symbolsByFile = allSymbols |> List.groupBy (fun s -> s.SourceFile) |> Map.ofList

    let isContainedByAnother (s: SymbolInfo) =
        symbolsByFile
        |> Map.tryFind s.SourceFile
        |> Option.defaultValue []
        |> List.exists (fun parent ->
            parent.Kind <> Module
            && parent.Kind <> DuCase
            && parent.LineStart <= s.LineStart
            && parent.LineEnd >= s.LineEnd
            && (parent.LineStart <> s.LineStart || parent.LineEnd <> s.LineEnd))

    let shallowest =
        unreachableSymbols
        |> List.filter (fun s -> not (isLocal s) && not (isContainedByAnother s))

    let result: DeadCodeResult =
        { TotalSymbols = allNames.Count
          ReachableSymbols = reachable.Count
          UnreachableSymbols = shallowest }

    let events = [ DeadCodeFoundEvent(shallowest |> List.map (fun s -> s.FullName)) ]

    result, events

/// Find dead code with verbose reasons for why each symbol is unreachable.
let findDeadCodeVerbose
    (allSymbols: SymbolInfo list)
    (reachable: Set<string>)
    (testMethodNames: Set<string>)
    (includeTests: bool)
    (getIncomingEdges: string -> string list)
    : VerboseDeadCodeResult * AnalysisEvent list =
    let result, events = findDeadCode allSymbols reachable testMethodNames includeTests

    let verboseUnreachable =
        result.UnreachableSymbols
        |> List.map (fun s ->
            let incoming = getIncomingEdges s.FullName

            let reason =
                if List.isEmpty incoming then
                    NoIncomingEdges
                else
                    DisconnectedFromEntryPoints incoming

            { Symbol = s; Reason = reason })

    { TotalSymbols = result.TotalSymbols
      ReachableSymbols = result.ReachableSymbols
      UnreachableSymbols = verboseUnreachable },
    events
