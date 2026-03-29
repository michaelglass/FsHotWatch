module TestPrune.SymbolDiff

open TestPrune.AstAnalyzer
open TestPrune.Domain

/// A change detected between stored and current symbols (Modified, Added, or Removed).
type SymbolChange =
    | Modified of symbolName: string
    | Added of symbolName: string
    | Removed of symbolName: string

/// Convert a SymbolChange to its corresponding ChangeKind.
let changeKind (change: SymbolChange) : ChangeKind =
    match change with
    | Modified _ -> Domain.Modified
    | Added _ -> Domain.Added
    | Removed _ -> Domain.Removed

/// Compare current symbols (from re-parsing) against stored symbols (from DB).
/// A symbol is "changed" if:
/// - It exists in both but content hash differs (Modified)
/// - Only in current (Added)
/// - Only in stored (Removed)
let detectChanges
    (currentSymbols: SymbolInfo list)
    (storedSymbols: SymbolInfo list)
    : SymbolChange list * AnalysisEvent list =
    let currentByName =
        currentSymbols |> List.map (fun s -> s.FullName, s) |> Map.ofList

    let storedByName = storedSymbols |> List.map (fun s -> s.FullName, s) |> Map.ofList

    let currentNames = currentByName |> Map.keys |> Set.ofSeq
    let storedNames = storedByName |> Map.keys |> Set.ofSeq

    let added = Set.difference currentNames storedNames |> Set.toList |> List.map Added

    let removed =
        Set.difference storedNames currentNames |> Set.toList |> List.map Removed

    let modified =
        Set.intersect currentNames storedNames
        |> Set.toList
        |> List.choose (fun name ->
            let curr = currentByName[name]
            let stored = storedByName[name]

            if curr.ContentHash <> stored.ContentHash then
                Some(Modified name)
            else
                None)

    let changes =
        added @ removed @ modified
        |> List.sortBy (fun change ->
            match change with
            | Added name
            | Removed name
            | Modified name -> name)

    let events =
        changes
        |> List.map (fun change ->
            let name, sourceFile =
                match change with
                | Modified n
                | Added n -> n, currentByName[n].SourceFile
                | Removed n -> n, storedByName[n].SourceFile

            SymbolChangeDetectedEvent(sourceFile, name, changeKind change))

    changes, events

/// Extract the symbol name from a single SymbolChange.
let symbolName (change: SymbolChange) : string =
    match change with
    | Modified name
    | Added name
    | Removed name -> name

/// Extract just the symbol names from changes.
let changedSymbolNames (changes: SymbolChange list) : string list = changes |> List.map symbolName
