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
    {
        DiagnosticsByFile: Map<AbsFilePath, ErrorEntry list>
        LoadedCount: int
        /// Per-path analyzer load result, in the order paths were configured. The
        /// count is 0 when the path doesn't exist OR exists but contains no
        /// analyzer DLLs — both are silent-skip cases the fail-loud guard catches.
        LoadedByPath: (string * int) list
        RunAnalyzed: int
    }

/// Render the analyzer run summary from the LIVE per-file diagnostic map — the
/// SAME `DiagnosticsByFile` set that backs `reportOrClearFile` → ErrorLedger →
/// the pass/fail verdict for the current cycle.
///
/// Verdict-reliability (2026-06-02 Issue A): the summary count must never be a
/// monotonic accumulator carried across cycles. A prior cycle's 9 findings on a
/// file that later re-checks clean must drop to 0 — the file's entry in the map
/// is replaced (with []) when it passes, so summing the map always reflects the
/// current gated set. `✓` ⇒ `0 findings`; any non-zero count ⇒ the verdict gates.
let internal summarize (analyzed: int) (diagnosticsByFile: Map<AbsFilePath, ErrorEntry list>) : string =
    // Materialize once, then reuse the canonical DiagnosticCounts.ofEntries
    // (single fold) for the severity tally — the same counter every other
    // plugin/IPC path routes through. `findings` is the total across ALL
    // severities (incl. Info/Hint), so it stays its own count.
    let allEntries = diagnosticsByFile |> Map.toList |> List.collect snd
    let counts = DiagnosticCounts.ofEntries allEntries
    let findings = List.length allEntries

    $"analyzed %d{analyzed} files, %d{findings} findings (%d{counts.Errors} errors, %d{counts.Warnings} warnings)"

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

/// Content-addressed identity of the analyzer assemblies in the configured
/// paths — the cache-correctness fix for the "stale-green after analyzer
/// rebuild" class (2026-06-17). The per-file analyzer cache previously keyed on
/// the analyzer PATH STRINGS only, so when a custom-analyzer DLL was rebuilt
/// (a rule changed, or a new analyzer added) but the path stayed the same, every
/// cached per-file verdict for unchanged source was replayed — the new/changed
/// rule never re-ran on those files, masking real findings. Folding the DLL
/// CONTENT into the key invalidates exactly those entries when (and only when)
/// the analyzer set actually changes.
///
/// We hash the same DLL set the loader inspects: every `*.dll` in each existing
/// path whose filename is NOT a known-non-analyzer prefix (BCL/FCS/shim deps).
/// Excluding those keeps the identity from churning on unrelated bundled-dep
/// refreshes while still tracking the analyzer assemblies themselves. Each DLL
/// contributes its relative filename + a SHA-256 of its bytes; the per-file
/// digests are sorted so directory enumeration order is irrelevant. A path that
/// doesn't exist (or an unreadable DLL) contributes a stable sentinel rather
/// than throwing, so identity computation never crashes plugin construction.
///
/// Pure (filesystem-reading) and `internal` so the keying behaviour is
/// unit-testable with throwaway byte files — no real SDK analyzer load needed.
let internal analyzerAssemblyIdentity (prefixes: string array) (paths: string list) : string =
    let perFile =
        paths
        |> List.sort
        |> List.collect (fun path ->
            if not (Directory.Exists(path)) then
                [ $"%s{path}=>missing" ]
            else
                Directory.GetFiles(path, "*.dll")
                |> Array.filter (fun dll ->
                    not (isKnownNonAnalyzerPrefix prefixes (Path.GetFileNameWithoutExtension dll)))
                |> Array.map (fun dll ->
                    let name = Path.GetFileName dll

                    let contentHash =
                        try
                            File.ReadAllBytes dll
                            |> System.Security.Cryptography.SHA256.HashData
                            |> System.Convert.ToHexString
                        with ex ->
                            // Unreadable (transient lock, perms): a stable sentinel
                            // keyed on the message so distinct failures stay distinct,
                            // never a throw that aborts plugin construction.
                            $"unreadable:%s{ex.Message}"

                    $"%s{name}:%s{contentHash}")
                |> Array.toList)
        |> List.sort

    FsHotWatch.CheckCache.sha256Hex (String.concat "\n" perFile)

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

let internal promoteIfFailing (threshold: DiagnosticSeverity) (entry: ErrorEntry) : ErrorEntry =
    if entry.Severity = DiagnosticSeverity.Error then
        entry
    elif DiagnosticSeverity.order entry.Severity >= DiagnosticSeverity.order threshold then
        { entry with
            Severity = DiagnosticSeverity.Error
            Message = $"[{DiagnosticSeverity.toString entry.Severity}] {entry.Message}" }
    else
        entry

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
    (repoRoot: string option)
    (analyzerPaths: string list)
    (timeoutSec: int option)
    (failOnSeverity: DiagnosticSeverity)
    (slowHook: (unit -> unit) option)
    : PluginHandler<AnalyzersState, AnalyzersMsg> =
    // The SDK Client holds the loaded analyzer set in private state. On a reload
    // (Bug C) we swap in a FRESH Client rather than re-loading the existing one:
    // a fresh Client is the path verified to pick up a newly-added analyzer for
    // RunAnalyzersSafely, and it can never replay a now-removed/changed analyzer
    // instance from prior private state. Volatile per the plugin's thread-safety
    // convention (concurrent FileChecked events read it under the semaphore).
    let mutable client = Client<CliAnalyzerAttribute, CliContext>()
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

    // Per-path load result (path, count). A non-existent path counts 0 without
    // touching the loader; an existing path's count is what LoadAnalyzers found.
    // Both 0-cases are silent-skips the fail-loud guard turns RED.
    let loadInto (c: Client<CliAnalyzerAttribute, CliContext>) =
        analyzerPaths
        |> List.map (fun path ->
            if Directory.Exists(path) then
                let stats = c.LoadAnalyzers(path, excludeInclude = excludeKnownDeps)
                path, stats.Analyzers
            else
                path, 0)

    let loadedByPath = loadInto client

    let loadedCount = loadedByPath |> List.sumBy snd

    info "analyzers" $"Loaded %d{loadedCount} analyzers from %d{analyzerPaths.Length} paths"

    // Bug C (2026-06-17): a long-lived (warm) daemon loads analyzers ONCE at
    // construction. When a downstream repo adds a NEW analyzer (a new .fs +
    // <Compile> entry) and the gate's build refreshes the analyzer DLL on disk,
    // the in-memory `client` still holds the OLD analyzer set — the new analyzer
    // never runs, so the gate reports green without ever inspecting the codebase
    // with it. (Bug A's cache key invalidates stale RESULTS so the loaded set
    // re-runs; Bug B fails loud on a 0-analyzer load — but neither catches a
    // PARTIAL stale load that's merely MISSING the newly-added analyzer.)
    //
    // Track the content identity of the loaded assembly set (the same hash Bug A
    // computes for the cache key). At the start of each FileChecked event, if the
    // on-disk identity differs, re-load the client so the new analyzer is present
    // before this file is analyzed. Volatile-guarded per the plugin convention
    // ("All mutable plugin state uses Volatile.Read/Write for thread safety").
    let mutable loadedAssemblyIdentity =
        analyzerAssemblyIdentity knownNonAnalyzerPrefixes analyzerPaths

    let reloadIfStale () =
        let onDisk = analyzerAssemblyIdentity knownNonAnalyzerPrefixes analyzerPaths

        if onDisk <> Volatile.Read(&loadedAssemblyIdentity) then
            // The analyzer assemblies on disk changed since we loaded (rule
            // changed, or — the Bug C case — a new analyzer was added). Load the
            // current set into a FRESH client and swap it in, so the new analyzer
            // is live (and any removed/changed one is gone) before we analyze.
            let fresh = Client<CliAnalyzerAttribute, CliContext>()
            let reloaded = loadInto fresh
            let reloadedCount = reloaded |> List.sumBy snd

            Volatile.Write(&client, fresh)
            Volatile.Write(&loadedAssemblyIdentity, onDisk)

            info
                "analyzers"
                $"Analyzer assembly set changed on disk — reloaded %d{reloadedCount} analyzers from %d{analyzerPaths.Length} paths"

    let analyzerTimeout =
        let secs = defaultArg timeoutSec AnalyzersTimeoutDefaultSec
        TimeSpan.FromSeconds(float secs)

    { Name = PluginName.create "analyzers"
      Init =
        { DiagnosticsByFile = Map.empty
          LoadedCount = loadedCount
          LoadedByPath = loadedByPath
          RunAnalyzed = 0 }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked result ->
                    let fileStr = AbsFilePath.value result.File

                    // AUTOMATION-49: never analyze compile items outside the repo.
                    // Packages can inject F# source into a consumer via
                    // contentFiles/_content (e.g. xunit.v3.core.mtp-v1's
                    // `_content/DefaultRunnerReporters.fs` from the ~/.nuget cache),
                    // which FCS type-checks and fires FileChecked for. That source
                    // is third-party — not ours to lint — and running FSharpLint
                    // (via the shim) over it crashed the analyzer host. Skip it.
                    if FsHotWatch.PathFilter.isOutsideRepoScoped repoRoot fileStr then
                        debug "analyzers" $"Skipping out-of-repo compile item %s{fileStr}"
                        return state
                    else

                        let runStarted = DateTime.UtcNow
                        ctx.ReportStatus(Running(since = runStarted))
                        ctx.StartSubtask PrimarySubtaskKey $"analyzing {Path.GetFileName fileStr}"

                        // Bug C: pick up a changed analyzer assembly set (e.g. a
                        // newly-added analyzer) before analyzing, so a warm daemon
                        // never runs a stale set that's missing the new analyzer.
                        reloadIfStale ()

                        let checkResultsObj =
                            match result.CheckResults with
                            | FullCheck cr -> box cr
                            | ParseOnly ->
                                debug "analyzers" $"Running parse-only analyzers for %s{fileStr}"
                                null

                        // Run analysis inline (awaited) so the framework's per-event
                        // cache-write window sees the final terminal status. Semaphore
                        // still bounds concurrency across plugins; per-plugin events
                        // are already serialized by the MailboxProcessor.
                        //
                        // Returns Some entries on success, None on timeout (terminal status
                        // already reported), or raises on crash (caught below as failure).
                        let! analysisOutcome =
                            async {
                                do! semaphore.WaitAsync(cts.Token) |> Async.AwaitTask

                                try
                                    return!
                                        PluginCtxHelpers.withSubtask
                                            ctx
                                            fileStr
                                            $"analyzing {Path.GetFileName fileStr}"
                                            (async {
                                                try
                                                    let runAnalyzers (workCt: Threading.CancellationToken) =
                                                        match slowHook with
                                                        | Some h -> h ()
                                                        | None -> ()

                                                        let sourceText = result.Source |> SourceText.ofString

                                                        let context =
                                                            createCliContext
                                                                (box fileStr)
                                                                (box sourceText)
                                                                (box result.ParseResults)
                                                                checkResultsObj
                                                                (box result.ProjectOptions)

                                                        // Read the current client: reloadIfStale may have
                                                        // swapped in a fresh one carrying a newly-added analyzer.
                                                        // Drive the analyzer run under the timeout's token so a
                                                        // stuck analyzer is actually cancelled on expiry rather
                                                        // than orphaned holding the semaphore slot.
                                                        let activeClient = Volatile.Read(&client)

                                                        Async.RunSynchronously(
                                                            activeClient.RunAnalyzersSafely(context),
                                                            cancellationToken = workCt
                                                        )

                                                    match runWithCancellableTimeout analyzerTimeout runAnalyzers with
                                                    | WorkTimedOut after ->
                                                        let reason = $"timed out after %d{int after.TotalSeconds}s"

                                                        error
                                                            "analyzers"
                                                            $"Analyzers TIMED OUT for %s{fileStr}: %s{reason}"

                                                        ctx.EndSubtask PrimarySubtaskKey
                                                        ctx.CompleteWithTimeout reason

                                                        ctx.ReportStatus(
                                                            PluginStatus.Failed(
                                                                $"analyzers timed out: {reason}",
                                                                DateTime.UtcNow
                                                            )
                                                        )

                                                        return Choice1Of3()
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
                                                                            | Severity.Error ->
                                                                                DiagnosticSeverity.Error
                                                                            | Severity.Warning ->
                                                                                DiagnosticSeverity.Warning
                                                                            | Severity.Info -> DiagnosticSeverity.Info
                                                                            | Severity.Hint -> DiagnosticSeverity.Hint
                                                                          Line = m.Range.StartLine
                                                                          Column = m.Range.StartColumn
                                                                          Detail = None })
                                                                | Result.Error _ -> [])

                                                        let entries =
                                                            entries |> List.map (promoteIfFailing failOnSeverity)

                                                        debug
                                                            "analyzers"
                                                            $"Analyzed %s{Path.GetFileName fileStr}: %d{entries.Length} diagnostics"

                                                        return Choice2Of3 entries
                                                // F16 (audit 2026-05-02): analyzers are loaded
                                                // from third-party assemblies (FSharpLint shim,
                                                // user-supplied analyzers). They may raise
                                                // anything during execution. The broad catch
                                                // keeps one buggy analyzer from taking down
                                                // the whole analysis pass over a project; the
                                                // failure is reported as Choice3Of3 so the
                                                // pipeline surfaces the analyzer's exception
                                                // text to the user. Logging ex.ToString()
                                                // preserves type + stack for diagnosing the
                                                // offending analyzer.
                                                with ex ->
                                                    error "analyzers" $"Error analyzing %s{fileStr}: %s{ex.ToString()}"

                                                    return Choice3Of3(ex.ToString())
                                            })
                                finally
                                    semaphore.Release() |> ignore
                            }

                        match analysisOutcome with
                        | Choice1Of3() ->
                            // Timed out — terminal status already reported, state unchanged.
                            return state
                        | Choice2Of3 entries ->
                            // Success — apply the same state updates the old AnalysisComplete handler did,
                            // then call completeWith inside this event's window so the framework writes the
                            // cache for FileChecked.
                            PluginCtxHelpers.reportOrClearFile ctx fileStr entries

                            // Replace (not merge) this file's entry so a re-check that
                            // passes drops to []; the summary derives from the resulting
                            // live map, never a cross-cycle accumulator (Issue A).
                            let updated = state.DiagnosticsByFile |> Map.add result.File entries
                            let analyzed = state.RunAnalyzed + 1

                            ctx.EndSubtask PrimarySubtaskKey

                            PluginCtxHelpers.completeWith
                                ctx
                                (summarize analyzed updated)
                                (DateTime.UtcNow - runStarted)

                            return
                                { state with
                                    DiagnosticsByFile = updated
                                    RunAnalyzed = analyzed }
                        | Choice3Of3 errMsg ->
                            // Crash — same logic as old Custom AnalysisFailed handler.
                            ctx.ReportErrors fileStr [ ErrorEntry.error $"Analyzer crashed: %s{errMsg}" ]
                            ctx.EndSubtask PrimarySubtaskKey

                            PluginCtxHelpers.completeWith
                                ctx
                                $"analyzer crashed on {Path.GetFileName fileStr}"
                                (DateTime.UtcNow - runStarted)

                            return state
                | Custom(AnalysisComplete(file, entries)) ->
                    // Report/clear so the gated ledger set and the summary agree, and
                    // replace this file's map entry so the summary reflects the current
                    // cycle — not a cross-cycle accumulator (Issue A).
                    PluginCtxHelpers.reportOrClearFile ctx file entries

                    let updated = state.DiagnosticsByFile |> Map.add (AbsFilePath.create file) entries
                    let analyzed = state.RunAnalyzed + 1

                    ctx.EndSubtask PrimarySubtaskKey

                    // Legacy test-driving arm: the analysis ran outside this
                    // handler, so there is no duration to swear to — Zero renders
                    // as "no timing shown", never a fabricated measurement.
                    PluginCtxHelpers.completeWith ctx (summarize analyzed updated) TimeSpan.Zero

                    return
                        { state with
                            DiagnosticsByFile = updated
                            RunAnalyzed = analyzed }
                | Custom(AnalysisFailed(file, error)) ->
                    ctx.ReportErrors file [ ErrorEntry.error $"Analyzer crashed: %s{error}" ]

                    ctx.EndSubtask PrimarySubtaskKey
                    PluginCtxHelpers.completeWith ctx $"analyzer crashed on {Path.GetFileName file}" TimeSpan.Zero
                    return state
                | _ -> return state
            }
      Commands =
        [ "diagnostics",
          fun _ctx state _args ->
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
        // §2a: pure-content cache key (file source + analyzer identity + fcs-signature).
        let analyzerPathsHash =
            FsHotWatch.CheckCache.sha256Hex (String.concat "|" (List.sort analyzerPaths))

        // Cache-correctness (2026-06-17): the key folds in the CONTENT identity of
        // the loaded analyzer assemblies, not just the path strings. A rebuilt
        // analyzer DLL (rule changed / analyzer added) changes this hash even when
        // the configured paths are byte-identical, invalidating cached per-file
        // verdicts so the new/changed rule re-runs instead of replaying stale-green.
        let analyzerAssemblyHash =
            analyzerAssemblyIdentity knownNonAnalyzerPrefixes analyzerPaths

        let cacheKey (event: PluginEvent<AnalyzersMsg>) : ContentHash option =
            match event with
            | FileChecked result ->
                // §1: fcs-signature captures cross-file FCS state changes so
                // upstream symbol changes invalidate this file's cache.
                let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature result.CheckResults

                Some(
                    FsHotWatch.TaskCache.merkleCacheKey
                        // plugin-version bumped to v3: the added analyzer-assemblies
                        // slot makes every old on-disk entry's key non-matching, so a
                        // daemon that cached "clean" under the path-only key (v2) can't
                        // replay it after the analyzer set changed.
                        [ "plugin-version", "analyzers-merkle-v3"
                          "analyzer-paths", analyzerPathsHash
                          "analyzer-assemblies", analyzerAssemblyHash
                          "file", AbsFilePath.value result.File
                          "source", result.Source
                          "fcs-signature", fcsSignature ]
                )
            | _ -> None

        Some cacheKey
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
    (repoRoot: string option)
    (analyzerPaths: string list)
    (timeoutSec: int option)
    (failOnSeverity: DiagnosticSeverity)
    : PluginHandler<AnalyzersState, AnalyzersMsg> =
    createWithSlowHook repoRoot analyzerPaths timeoutSec failOnSeverity None
