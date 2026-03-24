module FsHotWatch.Analyzers.AnalyzersPlugin

open System
open FsHotWatch.Events
open FsHotWatch.Plugin

/// Hosts F# analyzers in-process using FSharp.Analyzers.SDK.
/// Loads analyzer DLLs from configured directory paths and runs them
/// on FileChecked events with check results from the warm checker.
///
/// TODO: CliContext construction requires understanding the SDK's internal
/// factory methods. The context needs ParseFileResults + CheckFileResults
/// from the warm FSharpChecker, which we have from FileChecked events.
type AnalyzersPlugin(analyzerPaths: string list) =
    let mutable diagnosticsByFile: Map<string, string list> = Map.empty

    interface IFsHotWatchPlugin with
        member _.Name = "analyzers"

        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = DateTime.UtcNow))

                try
                    // TODO: Load analyzer DLLs from analyzerPaths via
                    // FSharp.Analyzers.SDK.Client<CliAnalyzerAttribute, CliContext>
                    // and construct CliContext from result.ParseResults + result.CheckResults.
                    // The SDK's CliContext construction API needs investigation —
                    // it may require FSharpCheckProjectResults which we don't have per-file.
                    diagnosticsByFile <- diagnosticsByFile |> Map.add result.File []
                    ctx.ReportStatus(Completed(box diagnosticsByFile, DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(Failed(ex.Message, DateTime.UtcNow)))

            ctx.RegisterCommand(
                "diagnostics",
                fun _args ->
                    async {
                        let totalDiags =
                            diagnosticsByFile
                            |> Map.toList
                            |> List.sumBy (fun (_, msgs) -> msgs.Length)

                        return
                            $"{{\"analyzers_paths\": %d{analyzerPaths.Length}, \"files\": %d{diagnosticsByFile.Count}, \"diagnostics\": %d{totalDiags}}}"
                    }
            )

        member _.Dispose() = ()
