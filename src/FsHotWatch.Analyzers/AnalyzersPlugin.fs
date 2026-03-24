module FsHotWatch.Analyzers.AnalyzersPlugin

open System
open System.IO
open FSharp.Analyzers.SDK
open FSharp.Compiler.Text
open FsHotWatch.Events
open FsHotWatch.Plugin

/// Hosts F# analyzers in-process using the warm checker's results.
/// Uses reflection to construct CliContext, bypassing the FCS 43.10 vs 43.12
/// type mismatch at compile time (the types are structurally identical).
type AnalyzersPlugin(analyzerPaths: string list) =
    let mutable diagnosticsByFile: Map<string, AnalysisResult list> = Map.empty
    let client = Client<CliAnalyzerAttribute, CliContext>()
    let mutable loadedCount = 0

    let createCliContext fileName sourceText parseResults checkResults =
        let ctor = typeof<CliContext>.GetConstructors().[0]

        ctor.Invoke(
            [| fileName
               sourceText
               parseResults
               checkResults
               box None // typedTree
               null // checkProjectResults
               null // projectOptions
               box (Map.empty: Map<string, (Set<int> * Set<int>)>) |]
        )
        :?> CliContext

    interface IFsHotWatchPlugin with
        member _.Name = "analyzers"

        member _.Initialize(ctx) =
            for path in analyzerPaths do
                if Directory.Exists(path) then
                    let stats = client.LoadAnalyzers(path)
                    loadedCount <- loadedCount + stats.Analyzers

            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = DateTime.UtcNow))

                try
                    let sourceText = result.Source |> SourceText.ofString

                    let context =
                        createCliContext result.File sourceText result.ParseResults result.CheckResults

                    let messages =
                        client.RunAnalyzersSafely(context) |> Async.RunSynchronously

                    diagnosticsByFile <- diagnosticsByFile |> Map.add result.File messages
                    ctx.ReportStatus(Completed(box diagnosticsByFile, DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            ctx.RegisterCommand(
                "diagnostics",
                fun _args ->
                    async {
                        let totalDiags =
                            diagnosticsByFile
                            |> Map.toList
                            |> List.sumBy (fun (_, msgs) -> msgs.Length)

                        return
                            $"{{\"analyzers\": %d{loadedCount}, \"files\": %d{diagnosticsByFile.Count}, \"diagnostics\": %d{totalDiags}}}"
                    }
            )

        member _.Dispose() = ()
