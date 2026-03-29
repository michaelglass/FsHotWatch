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
open FsHotWatch.AgentHost
open FsHotWatch.Logging
open FsHotWatch.Plugin

type private AnalyzersState =
    { DiagnosticsByFile: Map<string, AnalysisResult list>
      LoadedCount: int
      ErrorCount: int }

type private AnalyzersMsg =
    | AnalysisComplete of file: string * results: AnalysisResult list
    | AnalysisFailed of file: string * error: string
    | AddLoaded of int

/// Hosts F# analyzers in-process using the warm checker's results.
/// Uses reflection to construct CliContext, bypassing the FCS 43.10 vs 43.12
/// type mismatch at compile time (the types are structurally identical).
type AnalyzersPlugin(analyzerPaths: string list, ?maxConcurrency: int) =
    let client = Client<CliAnalyzerAttribute, CliContext>()
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

                let emptyMethod =
                    mapModuleType.GetMethods()
                    |> Array.find (fun m -> m.Name = "Empty" && m.IsGenericMethodDefinition)

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
            let initialState =
                { DiagnosticsByFile = Map.empty
                  LoadedCount = 0
                  ErrorCount = 0 }

            let theAgent =
                createAgent<AnalyzersState, AnalyzersMsg> "analyzers" initialState (fun state msg ->
                    async {
                        match msg with
                        | AddLoaded count ->
                            return
                                { state with
                                    LoadedCount = state.LoadedCount + count }
                        | AnalysisComplete(file, results) ->
                            return
                                { state with
                                    DiagnosticsByFile = state.DiagnosticsByFile |> Map.add file results }
                        | AnalysisFailed(_file, _error) ->
                            return
                                { state with
                                    ErrorCount = state.ErrorCount + 1 }
                    })

            for path in analyzerPaths do
                if Directory.Exists(path) then
                    let stats = client.LoadAnalyzers(path)
                    theAgent.Post(AddLoaded stats.Analyzers)

            let loadedCount = theAgent.GetState() |> Async.RunSynchronously |> _.LoadedCount

            Logging.info "analyzers" $"Loaded %d{loadedCount} analyzers from %d{analyzerPaths.Length} paths"

            let reportCompleted () =
                let currentState = theAgent.GetState() |> Async.RunSynchronously

                if currentState.ErrorCount > 0 then
                    ctx.ReportStatus(
                        Completed(
                            box
                                $"analyzed %d{currentState.DiagnosticsByFile.Count} files, %d{currentState.ErrorCount} errors",
                            DateTime.UtcNow
                        )
                    )
                else
                    ctx.ReportStatus(Completed(box currentState.DiagnosticsByFile, DateTime.UtcNow))

            ctx.OnFileChecked.Add(fun result ->
                ctx.ReportStatus(Running(since = DateTime.UtcNow))

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

                                theAgent.Post(AnalysisComplete(result.File, messages))

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
                                                    | Severity.Error -> DiagnosticSeverity.Error
                                                    | Severity.Warning -> DiagnosticSeverity.Warning
                                                    | Severity.Info -> DiagnosticSeverity.Info
                                                    | Severity.Hint -> DiagnosticSeverity.Hint
                                                  Line = m.Range.StartLine
                                                  Column = m.Range.StartColumn })
                                        | Result.Error _ -> [])

                                Logging.debug
                                    "analyzers"
                                    $"Analyzed %s{Path.GetFileName result.File}: %d{entries.Length} diagnostics"

                                if entries.IsEmpty then
                                    ctx.ClearErrors result.File
                                else
                                    ctx.ReportErrors result.File entries

                                reportCompleted ()
                            with ex ->
                                theAgent.Post(AnalysisFailed(result.File, ex.ToString()))
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
                        let! currentState = theAgent.GetState()

                        let totalDiags =
                            currentState.DiagnosticsByFile
                            |> Map.toList
                            |> List.sumBy (fun (_, msgs) -> msgs.Length)

                        return
                            JsonSerializer.Serialize(
                                {| analyzers = currentState.LoadedCount
                                   files = currentState.DiagnosticsByFile.Count
                                   diagnostics = totalDiags |}
                            )
                    }
            )

        member _.Dispose() =
            cts.Cancel()
            cts.Dispose()
            semaphore.Dispose()
