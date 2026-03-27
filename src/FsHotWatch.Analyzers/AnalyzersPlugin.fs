module FsHotWatch.Analyzers.AnalyzersPlugin

open System
open System.IO
open System.Threading
open FSharp.Analyzers.SDK
open FSharp.Compiler.Text
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.Plugin

/// Hosts F# analyzers in-process using the warm checker's results.
/// Uses reflection to construct CliContext, bypassing the FCS 43.10 vs 43.12
/// type mismatch at compile time (the types are structurally identical).
type AnalyzersPlugin(analyzerPaths: string list, ?maxConcurrency: int) =
    let mutable diagnosticsByFile: Map<string, AnalysisResult list> = Map.empty
    let client = Client<CliAnalyzerAttribute, CliContext>()
    let mutable loadedCount = 0
    let concurrencyLimit = defaultArg maxConcurrency 4
    let semaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit)
    let cts = new CancellationTokenSource()

    let createCliContext
        fileName
        sourceText
        parseResults
        checkResults
        (projectOptions: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions)
        =
        let ctor = typeof<CliContext>.GetConstructors().[0]
        let ignoreRangesParam = ctor.GetParameters().[7].ParameterType
        let keyType = ignoreRangesParam.GetGenericArguments().[0]
        let valueType = ignoreRangesParam.GetGenericArguments().[1]

        let emptyIgnoreRanges =
            typedefof<Map<_, _>>.MakeGenericType(keyType, valueType).GetProperty("Empty").GetValue(null)

        // Construct AnalyzerProjectOptions via reflection (SDK type, not FCS type)
        let apoType = ctor.GetParameters().[6].ParameterType
        let apoCtor = apoType.GetConstructors() |> Array.tryHead

        let analyzerProjectOptions =
            match apoCtor with
            | Some c ->
                try
                    // AnalyzerProjectOptions expects: tag, projectFileName, projectId, sourceFiles, referencedProjectsPath, loadTime, otherOptions
                    let sourceFiles = projectOptions.SourceFiles |> Array.toList
                    let otherOptions = projectOptions.OtherOptions |> Array.toList

                    c.Invoke(
                        [| box 0 // tag for BackgroundCompilerOptions
                           box projectOptions.ProjectFileName
                           box None // projectId
                           box sourceFiles
                           box ([]: string list) // referencedProjectsPath
                           box System.DateTime.UtcNow
                           box otherOptions |]
                    )
                with _ ->
                    null
            | None -> null

        ctor.Invoke(
            [| fileName
               sourceText
               parseResults
               checkResults
               box None // typedTree
               null // checkProjectResults — analyzers that need this will skip
               analyzerProjectOptions
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

            let reportCompleted () =
                let currentDiags = Volatile.Read(&diagnosticsByFile)

                if Volatile.Read(&errorCount) > 0 then
                    ctx.ReportStatus(
                        Completed(box $"analyzed %d{currentDiags.Count} files, %d{errorCount} errors", DateTime.UtcNow)
                    )
                else
                    ctx.ReportStatus(Completed(box currentDiags, DateTime.UtcNow))

            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = DateTime.UtcNow))
                Interlocked.Increment(&processedCount) |> ignore

                if isNull (box result.CheckResults) then
                    Logging.warn "analyzers" $"Skipping %s{result.File} — no type check results"
                    reportCompleted ()
                else
                    async {
                        do! semaphore.WaitAsync(cts.Token) |> Async.AwaitTask

                        try
                            try
                                let sourceText = result.Source |> SourceText.ofString

                                let context =
                                    createCliContext
                                        result.File
                                        sourceText
                                        result.ParseResults
                                        result.CheckResults
                                        result.ProjectOptions

                                let! messages = client.RunAnalyzersSafely(context)

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

                                reportCompleted ()
                            with ex ->
                                Interlocked.Increment(&errorCount) |> ignore
                                Logging.error "analyzers" $"Error analyzing %s{result.File}: %s{ex.Message}"
                                Logging.debug "analyzers" $"Stack trace: %s{ex.StackTrace}"
                                reportCompleted ()
                        finally
                            semaphore.Release() |> ignore
                    }
                    |> fun a -> Async.Start(a, cts.Token))

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

        member _.Dispose() =
            cts.Cancel()
            cts.Dispose()
            semaphore.Dispose()
