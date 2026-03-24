module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.IO
open FsHotWatch.Events
open FsHotWatch.Plugin
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

/// TestPrune plugin — re-indexes changed files using the warm FSharpChecker
/// and reports which tests are affected by the changes.
type TestPrunePlugin(dbPath: string, repoRoot: string) =
    let db = Database.create dbPath
    let mutable lastAffectedTests: TestMethodInfo list = []
    let mutable lastChangedFiles: string list = []

    interface IFsHotWatchPlugin with
        member _.Name = "test-prune"

        member _.Initialize(ctx) =
            // On file check: re-index the file's symbols using warm checker results
            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = DateTime.UtcNow))

                try
                    let relPath =
                        Path.GetRelativePath(repoRoot, result.File).Replace('\\', '/')

                    let source =
                        if File.Exists(result.File) then
                            File.ReadAllText(result.File)
                        else
                            ""

                    // Extract symbols from check results (same as analyzeSource but
                    // using the already-checked results from the warm checker)
                    let allUses =
                        result.CheckResults.GetAllUsesOfAllSymbolsInFile() |> Seq.toList

                    // For now, just track which files changed for impact analysis
                    if not (lastChangedFiles |> List.contains relPath) then
                        lastChangedFiles <- relPath :: lastChangedFiles

                    // Get current symbols and compare with stored
                    let currentSymbols = db.GetSymbolsInFile(relPath)

                    if not currentSymbols.IsEmpty then
                        let storedSymbols = db.GetSymbolsInFile(relPath)
                        let changes = detectChanges currentSymbols storedSymbols
                        let changedNames = changedSymbolNames changes

                        if not changedNames.IsEmpty then
                            lastAffectedTests <- db.QueryAffectedTests(changedNames)

                    ctx.ReportStatus(Completed(box lastAffectedTests, DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            // Register command to query affected tests
            ctx.RegisterCommand(
                "affected-tests",
                fun _args ->
                    async {
                        let tests =
                            lastAffectedTests
                            |> List.map (fun t ->
                                $"{{\"project\": \"%s{t.TestProject}\", \"class\": \"%s{t.TestClass}\", \"method\": \"%s{t.TestMethod}\"}}")
                            |> String.concat ", "

                        return $"[%s{tests}]"
                    }
            )

            // Register command to get changed files
            ctx.RegisterCommand(
                "changed-files",
                fun _args ->
                    async {
                        let files = lastChangedFiles |> String.concat ", "
                        return $"[%s{files}]"
                    }
            )

        member _.Dispose() = ()
