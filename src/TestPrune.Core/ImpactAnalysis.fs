module TestPrune.ImpactAnalysis

open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.SymbolDiff

/// Result of test impact analysis: either a subset of affected tests or run-all with a reason.
type TestSelection =
    | RunSubset of TestMethodInfo list
    | RunAll of reason: string

/// Given changed files, determine which tests to run.
/// 1. Check for .fsproj changes -> RunAll
/// 2. For each changed file, get stored symbols and current symbols
/// 3. Detect changed symbols via SymbolDiff
/// 4. Query DB for transitively dependent test methods
/// 5. If any file is new (not indexed) -> RunAll (conservative)
let selectTests
    (db: Database)
    (changedFiles: string list)
    (currentSymbolsByFile: Map<string, SymbolInfo list>)
    : TestSelection =
    if changedFiles.IsEmpty then
        RunSubset []
    elif DiffParser.hasFsprojChanges changedFiles then
        RunAll "fsproj file changed"
    else
        let fsFiles =
            changedFiles
            |> List.filter (fun f -> not (f.EndsWith(".fsproj", System.StringComparison.OrdinalIgnoreCase)))

        let (hasNewFile, allChangedNames) =
            fsFiles
            |> List.fold
                (fun (newFile, changedNames) file ->
                    let storedSymbols = db.GetSymbolsInFile file

                    if storedSymbols.IsEmpty then
                        let currentSymbols =
                            currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

                        if not currentSymbols.IsEmpty then
                            (true, changedNames)
                        else
                            (newFile, changedNames)
                    else
                        let currentSymbols =
                            currentSymbolsByFile |> Map.tryFind file |> Option.defaultValue []

                        let changes = detectChanges currentSymbols storedSymbols
                        let names = changedSymbolNames changes
                        (newFile, changedNames @ names))
                (false, [])

        if hasNewFile then
            RunAll "new file not yet indexed"
        else
            let affectedTests = db.QueryAffectedTests allChangedNames
            RunSubset affectedTests
