module FsHotWatch.Analyzers.AnalyzersPlugin

open System
open System.IO
open System.Threading
open FSharp.Analyzers.SDK
open FSharp.Compiler.Text
open FsHotWatch.ErrorLedger
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
        let ignoreRangesParam = ctor.GetParameters().[7].ParameterType
        let keyType = ignoreRangesParam.GetGenericArguments().[0]
        let valueType = ignoreRangesParam.GetGenericArguments().[1]

        let emptyIgnoreRanges =
            typedefof<Map<_, _>>.MakeGenericType(keyType, valueType).GetProperty("Empty").GetValue(null)

        ctor.Invoke(
            [| fileName
               sourceText
               parseResults
               checkResults
               box None // typedTree
               null // checkProjectResults
               null // projectOptions
               emptyIgnoreRanges |]
        )
        :?> CliContext

    interface IFsHotWatchPlugin with
        member _.Name = "analyzers"

        member _.Initialize(ctx) =
            for path in analyzerPaths do
                if Directory.Exists(path) then
                    let stats = client.LoadAnalyzers(path)
                    Interlocked.Add(&loadedCount, stats.Analyzers) |> ignore

            let mutable errorCount = 0
            let mutable processedCount = 0

            ctx.OnFileChecked.Add(fun result ->
                try
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    processedCount <- processedCount + 1

                    try
                        if isNull (box result.CheckResults) then
                            eprintfn "  [analyzers] Skipping %s — no type check results" result.File
                        else
                            let sourceText = result.Source |> SourceText.ofString

                            let context =
                                createCliContext result.File sourceText result.ParseResults result.CheckResults

                            let messages = client.RunAnalyzersSafely(context) |> Async.RunSynchronously

                            let current = Volatile.Read(&diagnosticsByFile)
                            Volatile.Write(&diagnosticsByFile, current |> Map.add result.File messages)

                            let entries =
                                messages
                                |> List.collect (fun ar ->
                                    match ar.Output with
                                    | Ok msgs ->
                                        msgs
                                        |> List.map (fun m ->
                                            { Message = m.Message
                                              Severity =
                                                match m.Severity with
                                                | Severity.Error -> "error"
                                                | Severity.Warning -> "warning"
                                                | Severity.Info -> "info"
                                                | Severity.Hint -> "hint"
                                              Line = m.Range.StartLine
                                              Column = m.Range.StartColumn })
                                    | Error _ -> [])

                            if entries.IsEmpty then
                                ctx.ClearErrors result.File
                            else
                                ctx.ReportErrors result.File entries
                    with ex ->
                        errorCount <- errorCount + 1
                        eprintfn "  [analyzers] Error analyzing %s: %s" result.File ex.Message

                    let currentDiags = Volatile.Read(&diagnosticsByFile)

                    if errorCount > 0 then
                        ctx.ReportStatus(
                            Completed(
                                box $"analyzed %d{currentDiags.Count} files, %d{errorCount} errors",
                                DateTime.UtcNow
                            )
                        )
                    else
                        ctx.ReportStatus(Completed(box currentDiags, DateTime.UtcNow))
                with outerEx ->
                    eprintfn "  [analyzers] OUTER error in OnFileChecked handler: %s" outerEx.Message)

            ctx.RegisterCommand(
                "diagnostics",
                fun _args ->
                    async {
                        let currentDiags = Volatile.Read(&diagnosticsByFile)
                        let currentLoaded = Volatile.Read(&loadedCount)

                        let totalDiags =
                            currentDiags |> Map.toList |> List.sumBy (fun (_, msgs) -> msgs.Length)

                        return
                            $"{{\"analyzers\": %d{currentLoaded}, \"files\": %d{currentDiags.Count}, \"diagnostics\": %d{totalDiags}}}"
                    }
            )

        member _.Dispose() = ()
