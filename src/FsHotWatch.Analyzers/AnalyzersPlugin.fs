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
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.ProcessHelper

/// Default per-event analyzer timeout (seconds). Used when no override is
/// configured. Chosen to match DaemonConfig.AnalyzersTimeoutDefaultSec.
[<Literal>]
let AnalyzersTimeoutDefaultSec = 120

type AnalyzersMsg =
    | AnalysisComplete of file: string * entries: ErrorEntry list
    | AnalysisFailed of file: string * error: string

type AnalyzersState =
    { DiagnosticsByFile: Map<string, ErrorEntry list>
      LoadedCount: int
      RunAnalyzed: int
      RunFindings: int
      RunErrors: int
      RunWarnings: int }

/// Assembly-name prefixes we always skip when loading analyzers. Analyzer
/// packages (e.g. FSharpLintAnalyzerShim) ship bundled BCL/FCS deps that aren't
/// analyzers; reflecting over them wastes startup and risks version mismatches.
let internal knownNonAnalyzerPrefixes =
    [| "FSharp.Compiler.Service"
       "FSharp.Compiler.Interactive"
       "FSharp.Core"
       "FSharp.Analyzers.SDK"
       "FSharp.DependencyManager"
       "FSharp.Control.Reactive"
       "FSharpx."
       "FParsec"
       "Ionide."
       "McMaster."
       "Microsoft."
       "System."
       "Newtonsoft."
       "SemanticVersioning" |]

/// True if `assemblyName` starts with any of the given non-analyzer prefixes.
/// Pure function extracted from the ExcludeFilter closure so both branches
/// (matched / not matched) can be unit-tested deterministically instead of
/// depending on which analyzer assemblies happen to ship with the SDK.
let internal isKnownNonAnalyzerPrefix (prefixes: string array) (assemblyName: string) : bool =
    prefixes
    |> Array.exists (fun p -> assemblyName.StartsWith(p, StringComparison.Ordinal))

/// Build the `AnalyzerProjectOptions` instance the SDK's CliContext expects.
/// The SDK's constructor shape is reflected at startup (`apoCtor`); extracted
/// so we can unit-test the `None` fallback and the `Invoke`-throws recovery
/// path without depending on which SDK version is loaded in-process.
///
/// `projectOptions` must be non-null — reflecting its type happens outside the
/// try/with so null-deref propagates to the analyzer wrapper's crash handler
/// instead of being silently swallowed (matches pre-refactor contract).
let internal buildAnalyzerProjectOptions
    (apoCtor: System.Reflection.ConstructorInfo option)
    (projectOptions: obj)
    : obj =
    let poType = projectOptions.GetType()

    match apoCtor with
    | None -> null
    | Some c ->
        let getField name =
            poType.GetProperty(name).GetValue(projectOptions)

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

/// Creates a framework plugin handler that hosts F# analyzers in-process
/// using the warm checker's results.
/// Uses reflection to construct CliContext, bypassing the FCS 43.10 vs 43.12
/// type mismatch at compile time (the types are structurally identical).
/// Internal constructor with a test seam. `slowHook` is invoked inside the
/// timeout-guarded region before the real analyzer call so tests can force
/// the timeout branch without needing a real slow analyzer DLL. The public
/// `create` passes `None`. In-process timeouts are advisory — the orphan
/// work continues running; only its result is discarded.
let internal createWithSlowHook
    (analyzerPaths: string list)
    (getCommitId: (unit -> string option) option)
    (timeoutSec: int option)
    (slowHook: (unit -> unit) option)
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
        let analyzerProjectOptions = buildAnalyzerProjectOptions apoCtor projectOptions

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

    // LoadAnalyzers reflects over every DLL in the directory; analyzer packages
    // that ship bundled deps (e.g. FSharpLintAnalyzerShim) expose dozens of
    // non-analyzer assemblies we'd otherwise load-and-inspect. Skip assemblies
    // whose filename is a well-known-non-analyzer prefix.
    let excludeKnownDeps =
        ExcludeInclude.ExcludeFilter(isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes)

    let mutable loadedCount = 0

    for path in analyzerPaths do
        if Directory.Exists(path) then
            let stats = client.LoadAnalyzers(path, excludeInclude = excludeKnownDeps)
            loadedCount <- loadedCount + stats.Analyzers

    info "analyzers" $"Loaded %d{loadedCount} analyzers from %d{analyzerPaths.Length} paths"

    let analyzerTimeout =
        let secs = defaultArg timeoutSec AnalyzersTimeoutDefaultSec
        TimeSpan.FromSeconds(float secs)

    { Name = PluginName.create "analyzers"
      Init =
        { DiagnosticsByFile = Map.empty
          LoadedCount = loadedCount
          RunAnalyzed = 0
          RunFindings = 0
          RunErrors = 0
          RunWarnings = 0 }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked result ->
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))
                    ctx.StartSubtask PrimarySubtaskKey $"analyzing {Path.GetFileName result.File}"

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
                            return!
                                PluginCtxHelpers.withSubtask
                                    ctx
                                    result.File
                                    $"analyzing {Path.GetFileName result.File}"
                                    (async {
                                        try
                                            let runAnalyzers () =
                                                match slowHook with
                                                | Some h -> h ()
                                                | None -> ()

                                                let sourceText = result.Source |> SourceText.ofString

                                                let context =
                                                    createCliContext
                                                        (box result.File)
                                                        (box sourceText)
                                                        (box result.ParseResults)
                                                        checkResultsObj
                                                        (box result.ProjectOptions)

                                                client.RunAnalyzersSafely(context) |> Async.RunSynchronously

                                            match runWithTimeout analyzerTimeout runAnalyzers with
                                            | WorkTimedOut after ->
                                                let reason = $"timed out after %d{int after.TotalSeconds}s"
                                                error "analyzers" $"Analyzers TIMED OUT for %s{result.File}: %s{reason}"

                                                ctx.EndSubtask PrimarySubtaskKey
                                                ctx.CompleteWithTimeout reason

                                                ctx.ReportStatus(
                                                    PluginStatus.Failed(
                                                        $"analyzers timed out: {reason}",
                                                        DateTime.UtcNow
                                                    )
                                                )
                                            // Do NOT Post(AnalysisFailed ...) — the status is already
                                            // terminal (TimedOut). Reposting would trigger the
                                            // AnalysisFailed handler's completeWith which records a
                                            // second RunRecord and overwrites the TimedOut outcome.
                                            | WorkCompleted messages ->

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

                                                PluginCtxHelpers.reportOrClearFile ctx result.File entries
                                                ctx.Post(AnalysisComplete(result.File, entries))
                                        with ex ->
                                            ctx.Post(AnalysisFailed(result.File, ex.ToString()))
                                            error "analyzers" $"Error analyzing %s{result.File}: %s{ex.ToString()}"
                                    })
                        finally
                            semaphore.Release() |> ignore
                    }
                    |> fun a -> Async.Start(a, cts.Token)

                    return state
                | Custom(AnalysisComplete(file, entries)) ->
                    let updated = state.DiagnosticsByFile |> Map.add file entries

                    let newErrors =
                        entries
                        |> List.filter (fun e -> e.Severity = DiagnosticSeverity.Error)
                        |> List.length

                    let newWarnings =
                        entries
                        |> List.filter (fun e -> e.Severity = DiagnosticSeverity.Warning)
                        |> List.length

                    let analyzed = state.RunAnalyzed + 1
                    let findings = state.RunFindings + entries.Length
                    let errors = state.RunErrors + newErrors
                    let warnings = state.RunWarnings + newWarnings

                    ctx.EndSubtask PrimarySubtaskKey

                    PluginCtxHelpers.completeWith
                        ctx
                        $"analyzed %d{analyzed} files, %d{findings} findings (%d{errors} errors, %d{warnings} warnings)"

                    return
                        { state with
                            DiagnosticsByFile = updated
                            RunAnalyzed = analyzed
                            RunFindings = findings
                            RunErrors = errors
                            RunWarnings = warnings }
                | Custom(AnalysisFailed(file, error)) ->
                    ctx.ReportErrors file [ ErrorEntry.error $"Analyzer crashed: %s{error}" ]

                    ctx.EndSubtask PrimarySubtaskKey
                    PluginCtxHelpers.completeWith ctx $"analyzer crashed on {Path.GetFileName file}"
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
                        | ParseOnly -> ContentHash.create (id + ":parse-only")
                        | FullCheck _ -> ContentHash.create id)
                | Custom _ -> None
                | _ -> getId () |> Option.map ContentHash.create)
      Teardown =
        Some(fun () ->
            cts.Cancel()
            cts.Dispose()
            semaphore.Dispose()) }

/// Creates a framework plugin handler that hosts F# analyzers in-process
/// using the warm checker's results. Per-event work is wrapped in
/// `runWithTimeout`; on expiry the run is recorded as `TimedOut` and the
/// orphan work continues running in the background (result discarded).
let create
    (analyzerPaths: string list)
    (getCommitId: (unit -> string option) option)
    (timeoutSec: int option)
    : PluginHandler<AnalyzersState, AnalyzersMsg> =
    createWithSlowHook analyzerPaths getCommitId timeoutSec None
