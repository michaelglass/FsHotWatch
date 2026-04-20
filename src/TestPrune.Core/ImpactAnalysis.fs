module TestPrune.ImpactAnalysis

open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.SymbolDiff

/// Result of test impact analysis: either a subset of affected tests or run-all with a reason.
type TestSelection =
    | RunSubset of TestMethodInfo list
    | RunAll of reason: SelectionReason

/// Per-file outcome of comparing the current file's symbols to what's in the store.
/// `NotIndexedButHasSymbols` forces RunAll: we have no baseline, so we can't prove
/// what's affected. `NoBaseline` (both sides empty) and `Diffed []` are both benign
/// and produce no events or changes.
type private FileDiff =
    | NotIndexedButHasSymbols of file: string
    | NoBaseline
    | Diffed of changes: SymbolChange list * events: AnalysisEvent list

let private diffFile
    (getStoredSymbols: string -> SymbolInfo list)
    (currentSymbolsByFile: Map<string, SymbolInfo list>)
    (file: string)
    : FileDiff =
    let storedSymbols = getStoredSymbols file

    let currentSymbols =
        currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

    if storedSymbols.IsEmpty then
        if currentSymbols.IsEmpty then
            NoBaseline
        else
            NotIndexedButHasSymbols file
    else
        let changes, events = detectChanges currentSymbols storedSymbols
        Diffed(changes, events)

/// Given changed files, determine which tests to run.
let selectTests
    (getStoredSymbols: string -> SymbolInfo list)
    (queryAffectedTests: string list -> TestMethodInfo list)
    (changedFiles: string list)
    (currentSymbolsByFile: Map<string, SymbolInfo list>)
    : TestSelection * AnalysisEvent list =
    if changedFiles.IsEmpty then
        RunSubset [], []
    elif DiffParser.hasFsprojChanges changedFiles then
        RunAll(FsprojChanged(changedFiles |> List.find DiffParser.isFsproj)), []
    else
        let diffs =
            changedFiles
            |> List.filter (DiffParser.isFsproj >> not)
            |> List.map (diffFile getStoredSymbols currentSymbolsByFile)

        let firstNewFile =
            diffs
            |> List.tryPick (function
                | NotIndexedButHasSymbols f -> Some f
                | _ -> None)

        let allChanges =
            diffs
            |> List.collect (function
                | Diffed(c, _) -> c
                | _ -> [])

        let symbolEvents =
            diffs
            |> List.collect (function
                | Diffed(_, e) -> e
                | _ -> [])

        match firstNewFile with
        | Some file -> RunAll(NewFileNotIndexed file), symbolEvents
        | None ->
            let affectedTests = queryAffectedTests (changedSymbolNames allChanges)

            let selectionReason =
                match allChanges with
                | [ change ] -> SymbolChanged(SymbolDiff.symbolName change, SymbolDiff.changeKind change)
                | _ -> MultipleChanges(allChanges |> List.map SymbolDiff.symbolName)

            let testEvents =
                affectedTests
                |> List.map (fun testMethod -> TestSelectedEvent(testMethod.SymbolFullName, selectionReason))

            RunSubset affectedTests, symbolEvents @ testEvents
