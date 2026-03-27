module FsHotWatch.Analyzers.AnalyzersPlugin

open System
open System.IO
open System.Threading
open FSharp.Analyzers.SDK
open FSharp.Compiler.Text
open System.Text.Json
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

    // Cache invariant reflection artifacts lazily (CliContext ctor signature never changes at runtime,
    // but the SDK assembly may not be fully loaded at plugin construction time in tests)
    let cachedReflection =
        lazy
            let ctor = typeof<CliContext>.GetConstructors().[0]
            let ctorParams = ctor.GetParameters()

            if ctorParams.Length <> 8 then
                failwith
                    $"CliContext constructor has %d{ctorParams.Length} params (expected 8) — FSharp.Analyzers.SDK may have changed"

            let ignoreRangesType = ctorParams.[7].ParameterType
            let keyType = ignoreRangesType.GetGenericArguments().[0]
            let valueType = ignoreRangesType.GetGenericArguments().[1]

            // Get Map.empty from the same FSharp.Core assembly as the SDK uses.
            // Map<_,_> has no static Empty property — it lives in MapModule.
            let emptyIgnoreRanges =
                let mapModuleType =
                    ignoreRangesType.Assembly.GetType("Microsoft.FSharp.Collections.MapModule")

                let emptyMethod = mapModuleType.GetMethod("Empty")
                emptyMethod.MakeGenericMethod(keyType, valueType).Invoke(null, null)

            let apoCtor = ctorParams.[6].ParameterType.GetConstructors() |> Array.tryHead
            (ctor, ctorParams, emptyIgnoreRanges, apoCtor)

    /// Construct CliContext via reflection to bypass FCS version mismatch.
    /// All params are obj to prevent JIT from binding to wrong FCS assembly version.
    let createCliContext
        (fileName: obj)
        (sourceText: obj)
        (parseResults: obj)
        (checkResults: obj)
        (projectOptions: obj)
        : CliContext =
        let (ctor, _, emptyIgnoreRanges, apoCtor) = cachedReflection.Value
        let poType = projectOptions.GetType()

        let getField name =
            poType.GetProperty(name).GetValue(projectOptions)

        let analyzerProjectOptions =
            match apoCtor with
            | Some c ->
                try
                    let sourceFiles = getField "SourceFiles" :?> string array |> Array.toList
                    let otherOptions = getField "OtherOptions" :?> string array |> Array.toList
                    let projectFileName = getField "ProjectFileName" :?> string

                    c.Invoke(
                        [| box 0 // tag for BackgroundCompilerOptions
                           box projectFileName
                           box None // projectId
                           box sourceFiles
                           box ([]: string list) // referencedProjectsPath
                           box DateTime.UtcNow
                           box otherOptions |]
                    )
                with ex ->
                    Logging.warn "analyzers" $"AnalyzerProjectOptions ctor failed: %s{ex.Message}"
                    null
            | None -> null

        ctor.Invoke(
            [| fileName
               sourceText
               parseResults
               checkResults
               box None // typedTree
               null // checkProjectResults
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
                                        (box result.File)
                                        (box sourceText)
                                        (box result.ParseResults)
                                        (box result.CheckResults)
                                        (box result.ProjectOptions)

                                let! messages =
                                    try
                                        client.RunAnalyzersSafely(context)
                                    with ex ->
                                        Logging.error
                                            "analyzers"
                                            $"RunAnalyzersSafely failed for %s{result.File}: %s{ex.ToString()}"

                                        reraise ()

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
                                Logging.error "analyzers" $"Error analyzing %s{result.File}: %s{ex.ToString()}"
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
                            JsonSerializer.Serialize(
                                {| analyzers = currentLoaded
                                   files = currentDiags.Count
                                   diagnostics = totalDiags |}
                            )
                    }
            )

        member _.Dispose() =
            cts.Cancel()
            cts.Dispose()
            semaphore.Dispose()
