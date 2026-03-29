module TestPrune.ImpactAnalysis

open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.SymbolDiff

/// Result of test impact analysis: either a subset of affected tests or run-all with a reason.
type TestSelection =
    | RunSubset of TestMethodInfo list
    | RunAll of reason: string

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
        RunAll "fsproj file changed", []
    else
        let fsFiles =
            changedFiles
            |> List.filter (fun f -> not (f.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase)))

        let changeKindStr change =
            match change with
            | Modified _ -> "Modified"
            | Added _ -> "Added"
            | Removed _ -> "Removed"

        let (hasNewFile, allChanges, symbolEvents) =
            fsFiles
            |> List.fold
                (fun (newFile, changes, events) file ->
                    let storedSymbols = getStoredSymbols file

                    if storedSymbols.IsEmpty then
                        let currentSymbols =
                            currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

                        if not currentSymbols.IsEmpty then
                            (true, changes, events)
                        else
                            (newFile, changes, events)
                    else
                        let currentSymbols =
                            currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

                        let fileChanges, changeEvents = detectChanges currentSymbols storedSymbols

                        (newFile, changes @ fileChanges, events @ changeEvents))
                (false, [], [])

        if hasNewFile then
            RunAll "new file not yet indexed", symbolEvents
        else
            let allChangedNames = changedSymbolNames allChanges
            let affectedTests = queryAffectedTests allChangedNames

            let testEvents =
                affectedTests
                |> List.map (fun testMethod ->
                    let name, kind =
                        match allChanges with
                        | change :: _ -> changedSymbolNames [ change ] |> List.head, changeKindStr change
                        | [] -> "", "Modified"

                    TestSelectedEvent(testMethod.SymbolFullName, SymbolChanged(name, kind)))

            RunSubset affectedTests, symbolEvents @ testEvents
