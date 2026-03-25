module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.IO
open System.Threading
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

                    // For now, just track which files changed for impact analysis
                    let currentFiles = Volatile.Read(&lastChangedFiles)

                    if not (currentFiles |> List.contains relPath) then
                        Volatile.Write(&lastChangedFiles, relPath :: currentFiles)

                    // Compare stored symbols to detect changes
                    let storedSymbols = db.GetSymbolsInFile(relPath)
                    // TODO: extract currentSymbols from allUses via analyzeSource extraction
                    // For now, we track changed files but symbol diffing needs proper implementation

                    ctx.ReportStatus(Completed(box (Volatile.Read(&lastAffectedTests)), DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            // Register command to query affected tests
            ctx.RegisterCommand(
                "affected-tests",
                fun _args ->
                    async {
                        let currentTests = Volatile.Read(&lastAffectedTests)

                        let tests =
                            currentTests
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
                        let currentFiles = Volatile.Read(&lastChangedFiles)
                        let files = currentFiles |> String.concat ", "
                        return $"[%s{files}]"
                    }
            )

        member _.Dispose() = ()
