module FsHotWatch.Analyzers.AnalyzersPlugin

open System
open System.IO
open System.Threading
open FSharp.Analyzers.SDK
open FSharp.Compiler.Text
open System.Text.Json
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Logging
open FsHotWatch.PluginFramework

type AnalyzersMsg =
    | AnalysisComplete of file: string * entries: ErrorEntry list
    | AnalysisFailed of file: string * error: string

type AnalyzersState =
    { DiagnosticsByFile: Map<string, ErrorEntry list>
      LoadedCount: int }

/// Creates a framework plugin handler that hosts F# analyzers in-process
/// using the warm checker's results.
/// Uses reflection to construct CliContext, bypassing the FCS 43.10 vs 43.12
/// type mismatch at compile time (the types are structurally identical).
let create
    (analyzerPaths: string list)
    (getCommitId: (unit -> string option) option)
    : PluginHandler<AnalyzersState, AnalyzersMsg> =
    let client = Client<CliAnalyzerAttribute, CliContext>()
    let concurrencyLimit = 4
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
                    warn "analyzers" $"AnalyzerProjectOptions ctor failed: %s{ex.Message}"
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

    // Load analyzers eagerly during create
    let mutable loadedCount = 0

    for path in analyzerPaths do
        if Directory.Exists(path) then
            let stats = client.LoadAnalyzers(path)
            loadedCount <- loadedCount + stats.Analyzers

    info "analyzers" $"Loaded %d{loadedCount} analyzers from %d{analyzerPaths.Length} paths"

    { Name = PluginName.create "analyzers"
      Init =
        { DiagnosticsByFile = Map.empty
          LoadedCount = loadedCount }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked result ->
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                    let checkResultsObj =
                        match result.CheckResults with
                        | FullCheck cr -> box cr
                        | ParseOnly ->
                            debug "analyzers" $"Running parse-only analyzers for %s{result.File}"
                            null

                    // Dispatch analysis to thread pool, semaphore-gated
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
                                        checkResultsObj
                                        (box result.ProjectOptions)

                                let! messages =
                                    try
                                        client.RunAnalyzersSafely(context)
                                    with ex ->
                                        error
                                            "analyzers"
                                            $"RunAnalyzersSafely failed for %s{result.File}: %s{ex.ToString()}"

                                        reraise ()

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
                                                  Column = m.Range.StartColumn
                                                  Detail = None })
                                        | Result.Error _ -> [])

                                debug
                                    "analyzers"
                                    $"Analyzed %s{Path.GetFileName result.File}: %d{entries.Length} diagnostics"

                                if entries.IsEmpty then
                                    ctx.ClearErrors result.File
                                else
                                    ctx.ReportErrors result.File entries

                                ctx.Post(AnalysisComplete(result.File, entries))
                            with ex ->
                                ctx.Post(AnalysisFailed(result.File, ex.ToString()))
                                error "analyzers" $"Error analyzing %s{result.File}: %s{ex.ToString()}"
                        finally
                            semaphore.Release() |> ignore
                    }
                    |> fun a -> Async.Start(a, cts.Token)

                    return state
                | Custom(AnalysisComplete(file, entries)) ->
                    ctx.ReportStatus(Completed(DateTime.UtcNow))

                    return
                        { state with
                            DiagnosticsByFile = state.DiagnosticsByFile |> Map.add file entries }
                | Custom(AnalysisFailed(file, error)) ->
                    ctx.ReportErrors file [ ErrorEntry.error $"Analyzer crashed: %s{error}" ]

                    ctx.ReportStatus(Completed(DateTime.UtcNow))
                    return state
                | _ -> return state
            }
      Commands =
        [ "diagnostics",
          fun state _args ->
              async {
                  let totalDiags =
                      state.DiagnosticsByFile
                      |> Map.toList
                      |> List.sumBy (fun (_, entries) -> entries.Length)

                  return
                      JsonSerializer.Serialize(
                          {| analyzers = state.LoadedCount
                             files = state.DiagnosticsByFile.Count
                             diagnostics = totalDiags |}
                      )
              } ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey =
        getCommitId
        |> Option.map (fun getId ->
            fun (event: PluginEvent<AnalyzersMsg>) ->
                match event with
                | FileChecked result ->
                    getId ()
                    |> Option.map (fun id ->
                        match result.CheckResults with
                        | ParseOnly -> id + ":parse-only"
                        | FullCheck _ -> id)
                | Custom _ -> None
                | _ -> getId ())
      Teardown =
        Some(fun () ->
            cts.Cancel()
            cts.Dispose()
            semaphore.Dispose()) }
