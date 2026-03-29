module TestPrune.ImpactAnalysis

open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.SymbolDiff

/// Result of test impact analysis: either a subset of affected tests or run-all with a reason.
type TestSelection =
    | RunSubset of TestMethodInfo list
    | RunAll of reason: SelectionReason

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
        let fsprojFile =
            changedFiles
            |> List.find (fun f -> f.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase))

        RunAll(FsprojChanged fsprojFile), []
    else
        let fsFiles =
            changedFiles
            |> List.filter (fun f -> not (f.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase)))

        let (newFile, allChanges, symbolEvents) =
            fsFiles
            |> List.fold
                (fun (newFile, changes, events) file ->
                    let storedSymbols = getStoredSymbols file

                    if storedSymbols.IsEmpty then
                        let currentSymbols =
                            currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

                        if not currentSymbols.IsEmpty then
                            (Some file, changes, events)
                        else
                            (newFile, changes, events)
                    else
                        let currentSymbols =
                            currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

                        let fileChanges, changeEvents = detectChanges currentSymbols storedSymbols

                        (newFile, changes @ fileChanges, events @ changeEvents))
                (None, [], [])

        match newFile with
        | Some file -> RunAll(NewFileNotIndexed file), symbolEvents
        | None ->
            let allChangedNames = changedSymbolNames allChanges
            let affectedTests = queryAffectedTests allChangedNames

            let testEvents =
                affectedTests
                |> List.map (fun testMethod ->
                    let name, kind =
                        match allChanges with
                        | change :: _ -> changedSymbolNames [ change ] |> List.head, SymbolDiff.changeKind change
                        | [] -> "", Domain.Modified

                    TestSelectedEvent(testMethod.SymbolFullName, SymbolChanged(name, kind)))

            RunSubset affectedTests, symbolEvents @ testEvents
