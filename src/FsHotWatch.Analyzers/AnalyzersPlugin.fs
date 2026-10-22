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

/// An analyzer that raised instead of returning findings. The SDK hands the exception
/// back as that analyzer's result; folded to "no findings" it would read as a clean
/// file with the rule silently off.
type AnalyzerCrash =
    {
        /// The analyzer's name as the SDK reports it.
        Analyzer: string
        /// Exception type and message, e.g. `MissingMethodException: Method not found: …`.
        Cause: string
        /// The full exception, stack included.
        Trace: string
    }

/// The ledger entry for a crash: an Error at line 1 of the file, which no failure
/// threshold demotes, naming the analyzer so the verdict says which rule is off.
let internal crashFinding (crash: AnalyzerCrash) : ErrorEntry =
    { Message = $"analyzer %s{crash.Analyzer} crashed: %s{crash.Cause}"
      Severity = DiagnosticSeverity.Error
      Line = 1
      Column = 0
      Detail = Some crash.Trace }

/// The crash a ledger entry records, when it is one `crashFinding` wrote. A replayed
/// result carries only its ledger entries, so this is how a replay recovers the crashes.
let internal crashOfFinding (entry: ErrorEntry) : AnalyzerCrash option =
    let prefix = "analyzer "
    let separator = " crashed: "

    match entry.Detail with
    | Some trace when
        entry.Severity = DiagnosticSeverity.Error
        && entry.Line = 1
        && entry.Column = 0
        && entry.Message.StartsWith(prefix, StringComparison.Ordinal)
        ->
        match entry.Message.IndexOf(separator, prefix.Length, StringComparison.Ordinal) with
        | -1 -> None
        | at ->
            Some
                { Analyzer = entry.Message.Substring(prefix.Length, at - prefix.Length)
                  Cause = entry.Message.Substring(at + separator.Length)
                  Trace = trace }
    | _ -> None

type AnalyzersState =
    {
        /// Every entry this plugin reported to the ledger for each file: the analyzers'
        /// findings and one Error finding per analyzer that crashed on the file.
        DiagnosticsByFile: Map<AbsFilePath, ErrorEntry list>
        /// The analyzers that crashed on each file, replaced per file like
        /// `DiagnosticsByFile` so a file that stops crashing drops out of the tally.
        CrashesByFile: Map<AbsFilePath, AnalyzerCrash list>
        LoadedCount: int
        /// Per-path analyzer load result, in the order paths were configured. The
        /// count is 0 when the path doesn't exist OR exists but contains no
        /// analyzer DLLs — both are silent-skip cases the fail-loud guard catches.
        LoadedByPath: (string * int) list
        RunAnalyzed: int
    }

    // A replayed analysis is recorded by the rule a live fold applies: only a result
    // published against the model published now is analyzed (see `notCurrent`), and a
    // completed analysis replaces the file's entries and crashes with the ones it
    // reported. `RunAnalyzed` counts what this handler analyzed, so a replay leaves it.
    interface IFileReplayState<AnalyzersState> with
        member this.Replayed _ currentModel result analysis =
            match analysis with
            | Result.Ok entries when result.ModelGeneration.IsSome && result.ModelGeneration = currentModel ->
                { this with
                    DiagnosticsByFile = this.DiagnosticsByFile |> Map.add result.File entries
                    CrashesByFile =
                        this.CrashesByFile
                        |> Map.add result.File (entries |> List.choose crashOfFinding) }
            | Result.Ok _
            | Result.Error _ -> this

/// Render the analyzer run summary from the LIVE per-file diagnostic map — the
/// SAME `DiagnosticsByFile` set that backs `reportOrClearFile` → ErrorLedger →
/// the pass/fail verdict for the current cycle.
///
/// The count must never be a monotonic accumulator carried across cycles: a file
/// that re-checks clean must drop to 0. The file's entry is REPLACED (with []) when
/// it passes, so summing the map always reflects the current gated set. `✓` ⇒
/// `0 findings`; any non-zero count ⇒ the verdict gates.
///
/// It also states its evidence: how many files this handler analyzed, how many were
/// replayed from cache instead, and which analyzer set ran. `0 findings` over files
/// nobody examined must not read like `0 findings` over files that were.
///
/// Crashes are counted per (file, analyzer) and are included in `findings`, since each
/// is an Error finding in the ledger. Each crashed analyzer is then named ONCE, with the
/// number of files it crashed on and its first cause: an analyzer that raises on every
/// file is one fact, and a thousand copies of it would bury the rest of the line.
let internal summarize
    (analyzed: int)
    (replayed: int)
    (analyzerSet: string)
    (diagnosticsByFile: Map<AbsFilePath, ErrorEntry list>)
    (crashesByFile: Map<AbsFilePath, AnalyzerCrash list>)
    : string =
    // `findings` is the total across ALL severities (incl. Info/Hint), so it stays
    // its own count rather than being derived from the severity tally.
    let allEntries = diagnosticsByFile |> Map.toList |> List.collect snd
    let counts = DiagnosticCounts.ofEntries allEntries
    let findings = List.length allEntries
    let crashes = crashesByFile |> Map.toList |> List.collect snd

    let crashed =
        match crashes with
        | [] -> "0 analyzer crashes"
        | _ ->
            let perAnalyzer =
                crashes
                |> List.groupBy _.Analyzer
                |> List.map (fun (analyzer, occurrences) ->
                    $"%s{analyzer} on %d{List.length occurrences} files: %s{(List.head occurrences).Cause}")
                |> String.concat "; "

            $"%d{List.length crashes} analyzer crashes (%s{perAnalyzer})"

    $"analyzed %d{analyzed} files, replayed %d{replayed} from cache, %d{findings} findings (%d{counts.Errors} errors, %d{counts.Warnings} warnings), %s{crashed} — %s{analyzerSet}"

/// Name the crash of `analyzer` by exception type and message.
let internal crashOf (analyzer: string) (ex: exn) : AnalyzerCrash =
    { Analyzer = analyzer
      Cause = $"%s{ex.GetType().Name}: %s{ex.Message}"
      Trace = ex.ToString() }

/// Split one file's analyzer results into what the analyzers found and which of them
/// crashed. `toEntry` renders a message; the crashes are left to `crashFinding`.
let internal foldResults
    (toEntry: Message -> ErrorEntry)
    (results: AnalysisResult list)
    : ErrorEntry list * AnalyzerCrash list =
    let findings =
        results
        |> List.collect (fun r ->
            match r.Output with
            | Result.Ok messages -> List.map toEntry messages
            | Result.Error _ -> [])

    let crashes =
        results
        |> List.choose (fun r ->
            match r.Output with
            | Result.Ok _ -> None
            | Result.Error ex -> Some(crashOf r.AnalyzerName ex))

    findings, crashes

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
/// Kept out of the `ExcludeFilter` closure so both branches can be unit-tested
/// deterministically rather than depending on which analyzer assemblies happen to
/// ship with the SDK.
let internal isKnownNonAnalyzerPrefix (prefixes: string array) (assemblyName: string) : bool =
    prefixes
    |> Array.exists (fun p -> assemblyName.StartsWith(p, StringComparison.Ordinal))

/// The DLLs the loader inspects: every `*.dll` in each existing configured path,
/// in configured order, minus the known-non-analyzer prefixes — excluding those keeps
/// the identity from churning on unrelated bundled-dep refreshes. A missing path
/// contributes nothing here; the 0-analyzer guard reports it.
let internal analyzerCandidates (prefixes: string array) (paths: string list) : string list =
    paths
    |> List.collect (fun path ->
        if Directory.Exists path then
            Directory.GetFiles(path, "*.dll")
            |> Array.filter (fun dll -> not (isKnownNonAnalyzerPrefix prefixes (Path.GetFileNameWithoutExtension dll)))
            |> Array.sort
            |> Array.toList
        else
            [])

/// What the plugin knows about the analyzer set on disk at one moment.
///
/// `Inputs` is the `analyzer-inputs` cache-key slot: the set's SEMANTIC identity
/// (`AnalyzerIdentity`), which a first-party analyzer keeps across checkouts because
/// it is derived from the compiler's receipt rather than from bytes fsc salted with
/// the PDB path. `Error` carries why no such identity exists right now — a DLL whose
/// sources have drifted, a missing PDB — in which case the analyzers still run but
/// nothing is read from or written to the cache: a verdict that cannot be named
/// cannot be replayed.
///
/// `Materialization` is the raw-byte digest of the same DLLs. It drives
/// reload-if-stale: a rebuild whose receipt is unchanged (same sources, new bytes —
/// the cross-workspace case) swaps the loaded set in the process but keeps every
/// cache entry.
type internal AnalyzerSetSnapshot =
    { Inputs: Result<string, AnalyzerIdentity.Refusal list>
      Materialization: string }

/// The raw-byte digest of the candidate DLLs; the one thing a snapshot needs that
/// never depends on the tree. An unreadable DLL yields a digest of the refusal, so a
/// transient lock is a change to observe rather than a throw that aborts the event.
let private materializationOf (candidates: string list) : string =
    match AnalyzerIdentity.ofPaths None candidates with
    | Result.Ok identity -> identity.MaterializationDigest
    | Result.Error refusals -> FsHotWatch.CheckCache.sha256Hex $"unreadable:%A{refusals}"

let internal snapshotAnalyzerSet
    (repoRoot: string option)
    (prefixes: string array)
    (paths: string list)
    : AnalyzerSetSnapshot =
    let candidates = analyzerCandidates prefixes paths

    match AnalyzerIdentity.ofPaths repoRoot candidates with
    | Result.Ok identity ->
        { Inputs = Result.Ok identity.SemanticKey
          Materialization = identity.MaterializationDigest }
    | Result.Error refusals ->
        { Inputs = Result.Error refusals
          Materialization = materializationOf candidates }

/// How a summary names the analyzer set: the head of the `analyzer-inputs` cache-key
/// slot, so the set a summary names and the set its cache entries are keyed under are
/// one value. A set with no identity has its cache off, and says so.
let internal analyzerSetLabel (inputs: Result<string, AnalyzerIdentity.Refusal list>) : string =
    match inputs with
    | Result.Ok key -> $"analyzer set %s{key.Substring(0, min 12 key.Length)}"
    | Result.Error _ -> "analyzer set unidentified (cache off)"

/// One line per refusal, for the warning that explains why the analyzer cache is
/// off. Hashes are left out: the reader needs the file and the cause, not 64 hex
/// digits, and the line doubles as the de-duplication signature below.
let internal describeRefusals (refusals: AnalyzerIdentity.Refusal list) : string =
    refusals
    |> List.map (fun refusal ->
        match refusal with
        | AnalyzerIdentity.Refusal.Unreadable(dll, reason) -> $"%s{dll}: unreadable (%s{reason})"
        | AnalyzerIdentity.Refusal.MissingPdb dll -> $"%s{dll}: no portable PDB beside a first-party build"
        | AnalyzerIdentity.Refusal.PdbMismatch(dll, pdb) -> $"%s{dll}: %s{pdb} is not the PDB this build wrote"
        | AnalyzerIdentity.Refusal.UnverifiableChecksum(file, algorithm) ->
            $"%s{file}: checksum algorithm %O{algorithm} cannot be recomputed"
        | AnalyzerIdentity.Refusal.DocumentMissing file -> $"%s{file}: named by the PDB, absent from the tree"
        | AnalyzerIdentity.Refusal.DocumentDrift(file, _, _) ->
            $"%s{file}: differs from what the analyzer was compiled from"
        | AnalyzerIdentity.Refusal.ProducerNotFound dll -> $"%s{dll}: no unique *.fsproj above it in the repository"
        | AnalyzerIdentity.Refusal.OutputOlderThanProject(dll, project) -> $"%s{dll}: older than %s{project}")
    |> String.concat "; "

/// Build the `AnalyzerProjectOptions` instance the SDK's CliContext expects.
/// The SDK's constructor shape is reflected at startup (`apoCtor`); kept separate
/// so the `None` fallback and the `Invoke`-throws recovery path are unit-testable
/// without depending on which SDK version is loaded in-process.
///
/// `projectOptions` must be non-null — reflecting its type happens OUTSIDE the
/// try/with so a null-deref propagates to the analyzer wrapper's crash handler
/// instead of being silently swallowed.
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

/// The configuration files analyzers find by walking up from the analyzed file, as
/// (file name, cache-key label). Each one found between the repository root and the
/// file is an input to the verdict.
let internal analyzerConfigFiles =
    [ ".editorconfig", "editorconfig"; "fsharplint.json", "fsharplint" ]

let private isFSharpLintAssembly (name: string) =
    name.Contains("FSharpLint", StringComparison.OrdinalIgnoreCase)

/// Which analyzer assemblies read each config file in `analyzerConfigFiles`, by label
/// and DLL file name: `fsharplint.json` is the FSharpLint shim's, and `.editorconfig`
/// is every other analyzer's (MichaelGlass.FSharp.Analyzers reads its `mga_*` keys
/// there). A config edit reloads only the paths holding one of its readers.
let internal analyzerConfigReaders: (string * (string -> bool)) list =
    [ "editorconfig", (isFSharpLintAssembly >> not)
      "fsharplint", isFSharpLintAssembly ]

/// Whether `loaded` holds an analyzer that reads the config file labelled `label`.
let internal readsConfig (label: string) (loaded: AnalyzerLoad.LoadedPath) : bool =
    analyzerConfigReaders
    |> List.exists (fun (reader, reads) -> reader = label && List.exists reads loaded.Assemblies)

/// The labels of the config files whose content differs from what `observed` last
/// saw. A candidate never seen before is not a change: nothing has read it yet. A
/// file created where one was seen absent is.
let internal changedConfigLabels
    (observed: Map<string, string option>)
    (chain: (string * (string * string option) list) list)
    : string list =
    chain
    |> List.filter (fun (_, candidates) ->
        candidates
        |> List.exists (fun (key, digest) ->
            match Map.tryFind key observed with
            | Some seen -> seen <> digest
            | None -> false))
    |> List.map fst

/// A finding at or above the failure threshold becomes an Error, and the message
/// records what it was BEFORE promotion so the provenance is not lost.
///
/// The prefix must say "promoted from", not the bare severity name: a record
/// carrying `severity: error` whose text began `[warning]` got a build-blocking
/// finding triaged as non-urgent.
let internal promoteIfFailing (threshold: DiagnosticSeverity) (entry: ErrorEntry) : ErrorEntry =
    if entry.Severity = DiagnosticSeverity.Error then
        entry
    elif DiagnosticSeverity.order entry.Severity >= DiagnosticSeverity.order threshold then
        { entry with
            Severity = DiagnosticSeverity.Error
            Message = $"[promoted from {DiagnosticSeverity.toString entry.Severity}] {entry.Message}" }
    else
        entry

/// Creates a framework plugin handler that hosts F# analyzers in-process
/// using the warm checker's results.
///
/// CliContext is constructed by REFLECTION to bypass the FCS 43.10 vs 43.12 type
/// mismatch at compile time (the types are structurally identical).
// Cache invariant reflection artifacts lazily (CliContext ctor signature never changes at runtime,
// but the SDK assembly may not be fully loaded at plugin construction time in tests)
let internal cachedReflection =
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
let internal createCliContext
    (fileName: obj)
    (sourceText: obj)
    (parseResults: obj)
    (checkResults: obj)
    (typedTree: obj)
    (projectOptions: obj)
    : CliContext =
    let (ctor, _, emptyIgnoreRanges, apoCtor) = cachedReflection.Value
    let analyzerProjectOptions = buildAnalyzerProjectOptions apoCtor projectOptions

    ctor.Invoke(
        [| fileName
           sourceText
           parseResults
           checkResults
           typedTree
           null // checkProjectResults
           analyzerProjectOptions
           emptyIgnoreRanges |]
    )
    :?> CliContext

/// The SDK's `CliContext.TypedTree` when no typed tree is being offered.
let internal noTypedTree: obj =
    box (None: FSharp.Compiler.Symbols.FSharpImplementationFileContents option)

/// The typed implementation contents an analyzer walks, boxed as the SDK's
/// `CliContext.TypedTree` (an `FSharpImplementationFileContents option`).
///
/// Supplying this is what keeps typed-tree rules ALIVE. An analyzer that needs type
/// information and receives `None` returns no findings rather than failing, so a
/// host that withholds it does not break — it quietly reports clean, which is a
/// check that cannot fail.
///
/// `ImplementationFile` RAISES when the checker was built without
/// `keepAssemblyContents`, so the access is guarded: such a host still analyzes,
/// with typed-tree rules quiet and every other rule running, rather than losing the
/// whole analyzer stage to an exception it cannot act on.
///
/// The tree is offered to every analyzer on every file. An analyzer compiled against a
/// different FCS raises once it walks it (`MissingMethodException`, `TypeLoadException`);
/// that raise is its result for the file, and `crashFinding` reports it as an Error. It
/// is never answered by withholding the tree: an analyzer handed `None` returns no
/// findings, and the gate would go green with its typed rules off.
let internal typedTreeOf (checkResults: FileCheckState) : obj =
    match checkResults with
    | ParseOnly -> noTypedTree
    | FullCheck results ->
        try
            box results.ImplementationFile
        with :? InvalidOperationException ->
            noTypedTree

/// The generation of the model the host currently publishes, when it is available.
/// `None` while no model is observable (never discovered, mid-rediscovery, or
/// unavailable): membership never outlives its model, so nothing can be shown to
/// belong to the model in force.
let private currentModelGeneration (ctx: PluginCtx<'Msg>) =
    match ctx.ProjectGraph.ObserveModel() with
    | FsHotWatch.ProjectModel.Observation.Available model -> Some model.Generation
    | FsHotWatch.ProjectModel.Observation.Unobserved
    | FsHotWatch.ProjectModel.Observation.Rediscovering _
    | FsHotWatch.ProjectModel.Observation.Unavailable _ -> None

/// Runs the loaded analyzer set — one Client per configured path — over one file's
/// context. Production uses the SDK's `RunAnalyzersSafely`, which hands a raising
/// analyzer's exception back as that analyzer's result instead of throwing.
type internal AnalyzerRunner = Client<CliAnalyzerAttribute, CliContext> list -> CliContext -> Async<AnalysisResult list>

let internal runSafely: AnalyzerRunner =
    fun clients context ->
        async {
            let! perPath =
                clients
                |> List.map (fun client -> client.RunAnalyzersSafely context)
                |> Async.Parallel

            return List.concat perPath
        }

/// The handler, with two test seams: `slowHook` is invoked inside the
/// timeout-guarded region before the analyzers run, and `runAnalyzersOver` stands in
/// for the SDK's run so a test can supply analyzers without loading an assembly.
let internal createWithSeams
    (repoRoot: string option)
    (analyzerPaths: string list)
    (timeoutSec: int option)
    (failOnSeverity: DiagnosticSeverity)
    (slowHook: (unit -> unit) option)
    (runAnalyzersOver: AnalyzerRunner)
    : PluginHandler<AnalyzersState, AnalyzersMsg> =
    let concurrencyLimit = 4
    let semaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit)
    // Only one synchronous analyzer callback may be alive at a time. Normal
    // FileChecked delivery is serialized already; this fence matters after a
    // timeout, when a token-ignoring callback can outlive its event handler.
    let executionFence = new SemaphoreSlim(1, 1)
    let cts = new CancellationTokenSource()



    // One Client per configured path, each in its own collectible load contexts (see
    // `AnalyzerLoad`), so a path reloads alone and what it replaced is unloaded. A
    // reload swaps in a FRESH Client rather than re-loading the existing one: it
    // picks up a newly-added analyzer, and can never replay a now-removed/changed
    // analyzer instance — or the statics it built from a config since edited. Volatile
    // per the plugin's thread-safety convention.
    let loadOne =
        AnalyzerLoad.loadPath (isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes)

    let mutable loaded = analyzerPaths |> List.map loadOne

    // Per-path load result (path, count). A non-existent path counts 0 without
    // touching the loader; both 0-cases are silent-skips the fail-loud guard
    // turns RED.
    let loadedByPath =
        loaded
        |> List.map (fun (path: AnalyzerLoad.LoadedPath) -> path.Path, path.Count)

    let loadedCount = loadedByPath |> List.sumBy snd

    /// Load the paths `stale` selects again, swap the fresh Clients in, and unload the
    /// ones they replace. Returns how many analyzers the reloaded paths now hold.
    let reloadWhere (stale: AnalyzerLoad.LoadedPath -> bool) : int =
        let next =
            Volatile.Read(&loaded)
            |> List.map (fun path ->
                if stale path then
                    Some path, loadOne path.Path
                else
                    None, path)

        Volatile.Write(&loaded, next |> List.map snd)

        next
        |> List.iter (fun (retired, _) -> retired |> Option.iter AnalyzerLoad.unload)

        next
        |> List.sumBy (fun (retired, path) -> if retired.IsSome then path.Count else 0)

    info "analyzers" $"Loaded %d{loadedCount} analyzers from %d{analyzerPaths.Length} paths"

    // A long-lived (warm) daemon loads analyzers ONCE at construction, so when a
    // downstream repo ADDS an analyzer and the gate's build refreshes the DLL on
    // disk, the in-memory `client` still holds the old set: the new analyzer never
    // runs and the gate reports green without ever applying it. Neither existing
    // guard catches that — the cache key only invalidates stale RESULTS for the
    // loaded set, and the fail-loud guard only fires on a 0-analyzer load.
    //
    // So track a snapshot of the analyzer set and refresh it when the bytes on disk
    // move: reload the client into a FRESH instance and recompute the cache-key slot.
    // While the slot is refused, the snapshot is retaken on every event instead —
    // a refusal can clear without any byte moving (an edited source reverted, a
    // touched project file rebuilt to the same bytes), and a daemon that only
    // watched bytes would stay uncached for its whole life. Volatile-guarded per the
    // plugin convention.
    let mutable snapshot =
        snapshotAnalyzerSet repoRoot knownNonAnalyzerPrefixes analyzerPaths

    // The refusal warning is emitted once per distinct refusal SET (by file and
    // cause, not by hash), not once per file checked: a thousand FileChecked events
    // under one drifted analyzer are one fact.
    let mutable warnedRefusals: string option = None

    // Files served from cache instead of analyzed. The framework computes this
    // handler's key and then EITHER replays the entry OR runs `Update`, one event at a
    // time on the plugin's loop — so a keyed event that `Update` never saw was
    // replayed. The key marks it pending, `Update` clears the mark, and whichever
    // key computation comes next counts a mark still standing.
    let mutable replayPending = false
    let mutable replayedFiles = 0

    let countPendingReplay () =
        if Volatile.Read(&replayPending) then
            Volatile.Write(&replayedFiles, Volatile.Read(&replayedFiles) + 1)
            Volatile.Write(&replayPending, false)

    let summarizeRun analyzed diagnosticsByFile crashesByFile =
        summarize
            analyzed
            (Volatile.Read(&replayedFiles))
            (analyzerSetLabel (Volatile.Read(&snapshot)).Inputs)
            diagnosticsByFile
            crashesByFile

    // Each crashing analyzer is logged the first time it raises, not once per file:
    // an analyzer that raises on every file is one fact.
    let mutable loggedCrashes: Set<string> = Set.empty

    let logCrashOnce (fileStr: string) (crash: AnalyzerCrash) =
        if not (Volatile.Read(&loggedCrashes) |> Set.contains crash.Analyzer) then
            Volatile.Write(&loggedCrashes, Volatile.Read(&loggedCrashes) |> Set.add crash.Analyzer)
            error "analyzers" $"Analyzer %s{crash.Analyzer} crashed on %s{fileStr}: %s{crash.Trace}"

    let warnOnce (refusals: AnalyzerIdentity.Refusal list) =
        let signature = describeRefusals refusals

        if Volatile.Read(&warnedRefusals) <> Some signature then
            Volatile.Write(&warnedRefusals, Some signature)

            warn
                "analyzers"
                $"Analyzer cache is off — no checkout-independent identity for the analyzer set: %s{signature}"

    match snapshot.Inputs with
    | Result.Error refusals -> warnOnce refusals
    | Result.Ok _ -> ()

    let refreshAnalyzerSet () =
        let candidates = analyzerCandidates knownNonAnalyzerPrefixes analyzerPaths
        let onDisk = materializationOf candidates
        let current = Volatile.Read(&snapshot)

        if onDisk <> current.Materialization then
            // Load the current set into FRESH clients and swap them in, so the added
            // analyzer is live (and any removed/changed one is gone) before we analyze.
            let reloadedCount = reloadWhere (fun _ -> true)

            info
                "analyzers"
                $"Analyzer assembly set changed on disk — reloaded %d{reloadedCount} analyzers from %d{analyzerPaths.Length} paths"

        if onDisk <> current.Materialization || Result.isError current.Inputs then
            let retaken = snapshotAnalyzerSet repoRoot knownNonAnalyzerPrefixes analyzerPaths

            Volatile.Write(&snapshot, retaken)

            match retaken.Inputs with
            | Result.Error refusals -> warnOnce refusals
            | Result.Ok _ -> ()

    // Every config file the analyzers could read, as the walk-up from each analyzed
    // file found it: label, then (`label:<repo-relative path>`, content if present).
    let configChain (file: string) : (string * (string * string option) list) list =
        // Analyzers read configuration by walking up from the file, not by being told:
        // MichaelGlass.FSharp.Analyzers reads `mga_*` keys from `.editorconfig`, and the
        // FSharpLint shim reads `fsharplint.json`. Without a repository root the walk
        // has no floor, so it runs to the filesystem root.
        let root =
            repoRoot
            |> Option.defaultWith (fun () -> Path.GetPathRoot(Path.GetFullPath file))

        analyzerConfigFiles
        |> List.map (fun (fileName, label) ->
            label, FsHotWatch.CacheInputs.configChainCandidates root fileName label [ file ])

    // What each candidate held when last seen, as a digest (None = absent). Kept for
    // the REPOSITORY's config set, not per file: the key below hashes each file's own
    // chain, and two files under different nested `.editorconfig`s must not read as an
    // edit to each other.
    let mutable observedConfigs: Map<string, string option> = Map.empty

    // An analyzer may cache what it read from its config in a static (the FSharpLint
    // shim did), so a re-run under the new cache key can still answer from the old
    // config. Never rely on an analyzer noticing: when a config it reads changes,
    // reload the paths that read it into fresh load contexts, and only those.
    //
    // Returns the chain it read, so the cache key is computed from the same bytes.
    let refreshConfigs (file: string) : (string * (string * string option) list) list =
        let found = configChain file

        let chain =
            found
            |> List.map (fun (label, candidates) ->
                label,
                candidates
                |> List.map (fun (key, content) -> key, content |> Option.map FsHotWatch.CheckCache.sha256Hex))

        let observed = Volatile.Read(&observedConfigs)
        let changed = changedConfigLabels observed chain

        Volatile.Write(
            &observedConfigs,
            chain
            |> List.collect snd
            |> List.fold (fun seen (key, digest) -> Map.add key digest seen) observed
        )

        if not (List.isEmpty changed) then
            let stopwatch = Diagnostics.Stopwatch.StartNew()

            let reloadedCount =
                reloadWhere (fun path -> changed |> List.exists (fun label -> readsConfig label path))

            info
                "analyzers"
                $"""Analyzer config changed (%s{String.concat ", " changed}) — reloaded %d{reloadedCount} analyzers that read it in %d{stopwatch.ElapsedMilliseconds}ms"""

        found

    let analyzerTimeout =
        let secs = defaultArg timeoutSec AnalyzersTimeoutDefaultSec
        TimeSpan.FromSeconds(float secs)

    { Name = PluginName.create FsHotWatch.PluginActivity.AnalyzersPluginName
      Init =
        { DiagnosticsByFile = Map.empty
          CrashesByFile = Map.empty
          LoadedCount = loadedCount
          LoadedByPath = loadedByPath
          RunAnalyzed = 0 }
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChecked _ -> Volatile.Write(&replayPending, false)
                | _ -> ()

                let modelGeneration = currentModelGeneration ctx

                // Analyze only a result published against the model this host publishes
                // NOW. One from a replaced model was admitted before the replacement,
                // and one with no model was captured against none, so it cannot
                // describe this one.
                let notCurrent (published: int64 option) =
                    published.IsNone || published <> modelGeneration

                match event with
                // A rediscovery that drops a file clears that file's findings in every
                // plugin ledger, and it clears them BEFORE the new model is published.
                // Nothing checks a dropped path again, so an old-model result folded
                // after that clear re-reports a finding for a file that is no longer in
                // the build and nothing will ever clear it again. A file the new model
                // still has is re-checked against it and republishes, so refusing costs
                // that file nothing.
                | FileChecked result when notCurrent result.ModelGeneration ->
                    info
                        "analyzers"
                        $"ignoring FileChecked for %s{AbsFilePath.value result.File} from model %A{result.ModelGeneration}; current %A{modelGeneration}"

                    return state
                | FileChecked result ->
                    let fileStr = AbsFilePath.value result.File

                    // Never analyze compile items outside the repo.
                    // Packages inject F# source into a consumer via
                    // contentFiles/_content (e.g. xunit.v3.core.mtp-v1's
                    // `_content/DefaultRunnerReporters.fs` from the ~/.nuget cache),
                    // which FCS type-checks and fires FileChecked for. That source is
                    // third-party — not ours to lint — and running FSharpLint over it
                    // crashed the analyzer host.
                    if FsHotWatch.PathFilter.isOutsideRepoScoped repoRoot fileStr then
                        debug "analyzers" $"Skipping out-of-repo compile item %s{fileStr}"
                        return state
                    else

                        refreshAnalyzerSet ()
                        refreshConfigs fileStr |> ignore

                        let checkResultsObj =
                            match result.CheckResults with
                            | FullCheck cr -> box cr
                            | ParseOnly ->
                                debug "analyzers" $"Running parse-only analyzers for %s{fileStr}"
                                null

                        let typedTreeObj = typedTreeOf result.CheckResults

                        // Run analysis inline (awaited) so the framework's per-event
                        // cache-write window sees the final terminal status. Semaphore
                        // still bounds concurrency across plugins; per-plugin events
                        // are already serialized by the MailboxProcessor.
                        //
                        // Returns Some entries on success, None on timeout (terminal status
                        // already reported), or raises on crash (caught below as failure).
                        let! elapsed, analysisOutcome =
                            async {
                                do! semaphore.WaitAsync(cts.Token) |> Async.AwaitTask
                                do! executionFence.WaitAsync(cts.Token) |> Async.AwaitTask
                                let elapsed = PluginCtxHelpers.beginRun ctx
                                ctx.StartSubtask PrimarySubtaskKey $"analyzing {Path.GetFileName fileStr}"
                                let mutable releaseExecutionFenceOnExit = true

                                try
                                    let! outcome =
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

                                                        // Drive the run under the timeout's token so a stuck
                                                        // analyzer is actually cancelled on expiry rather than
                                                        // orphaned holding the semaphore slot.
                                                        let activeClients = Volatile.Read(&loaded) |> List.map _.Client

                                                        // Checked under a virtual root, every range in
                                                        // the results names the file by its virtual
                                                        // path: the analyzers are told the same name.
                                                        let analyzedFile =
                                                            FsHotWatch.PathFrame.nameIn result.Frame fileStr

                                                        let context =
                                                            createCliContext
                                                                (box analyzedFile)
                                                                (box sourceText)
                                                                (box result.ParseResults)
                                                                checkResultsObj
                                                                typedTreeObj
                                                                (box result.ProjectOptions)

                                                        Async.RunSynchronously(
                                                            runAnalyzersOver activeClients context,
                                                            cancellationToken = workCt
                                                        )

                                                    let outcome, actualCompletion =
                                                        runWithCancellableTimeoutTracked analyzerTimeout runAnalyzers

                                                    match outcome with
                                                    | WorkTimedOut after ->
                                                        // Cancellation is cooperative. A synchronous analyzer
                                                        // callback can ignore the token and keep executing after
                                                        // the wall-clock timeout. Retain this permit until that
                                                        // callback really exits; otherwise the mailbox starts the
                                                        // next file alongside it and each timeout multiplies the
                                                        // runaway analyzer work.
                                                        releaseExecutionFenceOnExit <- false

                                                        actualCompletion.ContinueWith(
                                                            (fun (_: Threading.Tasks.Task) ->
                                                                executionFence.Release() |> ignore),
                                                            Threading.CancellationToken.None,
                                                            Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                                                            Threading.Tasks.TaskScheduler.Default
                                                        )
                                                        |> ignore

                                                        let reason = $"timed out after %d{int after.TotalSeconds}s"

                                                        error
                                                            "analyzers"
                                                            $"Analyzers TIMED OUT for %s{fileStr}: %s{reason}"

                                                        ctx.EndSubtask PrimarySubtaskKey
                                                        PluginCtxHelpers.timedOutWith ctx "analyzers" reason after

                                                        return Choice1Of3()
                                                    | WorkCompleted results ->
                                                        let toEntry (m: Message) : ErrorEntry =
                                                            { Message = FileCheckResult.textToReal result m.Message
                                                              Severity =
                                                                match m.Severity with
                                                                | Severity.Error -> DiagnosticSeverity.Error
                                                                | Severity.Warning -> DiagnosticSeverity.Warning
                                                                | Severity.Info -> DiagnosticSeverity.Info
                                                                | Severity.Hint -> DiagnosticSeverity.Hint
                                                              Line = m.Range.StartLine
                                                              Column = m.Range.StartColumn
                                                              Detail = None }

                                                        let findings, crashes = foldResults toEntry results

                                                        crashes |> List.iter (logCrashOnce fileStr)

                                                        let entries =
                                                            (findings |> List.map (promoteIfFailing failOnSeverity))
                                                            @ (crashes |> List.map crashFinding)

                                                        debug
                                                            "analyzers"
                                                            $"Analyzed %s{Path.GetFileName fileStr}: %d{entries.Length} diagnostics, %d{crashes.Length} analyzer crashes"

                                                        return Choice2Of3(entries, crashes)
                                                // Analyzers are third-party assemblies and may
                                                // raise anything. The broad catch keeps one buggy
                                                // analyzer from taking down the whole pass;
                                                // `ex.ToString()` preserves type + stack so the
                                                // offending analyzer is diagnosable.
                                                with ex ->
                                                    error "analyzers" $"Error analyzing %s{fileStr}: %s{ex.ToString()}"

                                                    return Choice3Of3(ex.ToString())
                                            })

                                    return elapsed, outcome
                                finally
                                    if releaseExecutionFenceOnExit then
                                        executionFence.Release() |> ignore

                                    semaphore.Release() |> ignore
                            }

                        match analysisOutcome with
                        | Choice1Of3() ->
                            // Timed out — terminal status already reported, state unchanged.
                            return state
                        | Choice2Of3(entries, crashes) ->
                            // completeWith must run inside this event's window so the
                            // framework writes the cache for FileChecked.
                            PluginCtxHelpers.reportOrClearFile ctx fileStr entries

                            // Replace (not merge) this file's entries so a re-check that
                            // passes drops to []; see `summarize`.
                            let updated = state.DiagnosticsByFile |> Map.add result.File entries
                            let crashesByFile = state.CrashesByFile |> Map.add result.File crashes
                            let analyzed = state.RunAnalyzed + 1

                            ctx.EndSubtask PrimarySubtaskKey

                            PluginCtxHelpers.completeWith ctx (summarizeRun analyzed updated crashesByFile) (elapsed ())

                            return
                                { state with
                                    DiagnosticsByFile = updated
                                    CrashesByFile = crashesByFile
                                    RunAnalyzed = analyzed }
                        | Choice3Of3 errMsg ->
                            let entries = [ ErrorEntry.error $"Analyzer crashed: %s{errMsg}" ]
                            ctx.ReportErrors fileStr entries
                            ctx.EndSubtask PrimarySubtaskKey

                            PluginCtxHelpers.completeWith
                                ctx
                                $"analyzer crashed on {Path.GetFileName fileStr}"
                                (elapsed ())

                            // The file's entries are what the ledger now holds for it — the
                            // crash — so the summary and the `diagnostics` command count it,
                            // and a replay of this result leaves the same state.
                            return
                                { state with
                                    DiagnosticsByFile = state.DiagnosticsByFile |> Map.add result.File entries
                                    CrashesByFile = state.CrashesByFile |> Map.add result.File [] }
                | Custom(AnalysisComplete(file, entries)) ->
                    // Report/clear so the gated ledger set and the summary agree, and
                    // replace this file's map entry — see `summarize`.
                    PluginCtxHelpers.reportOrClearFile ctx file entries

                    let updated = state.DiagnosticsByFile |> Map.add (AbsFilePath.create file) entries
                    let crashesByFile = state.CrashesByFile |> Map.remove (AbsFilePath.create file)
                    let analyzed = state.RunAnalyzed + 1

                    ctx.EndSubtask PrimarySubtaskKey

                    // Legacy test-driving arm: the analysis ran outside this
                    // handler, so there is no duration to swear to — Zero renders
                    // as "no timing shown", never a fabricated measurement.
                    PluginCtxHelpers.completeWith ctx (summarizeRun analyzed updated crashesByFile) TimeSpan.Zero

                    return
                        { state with
                            DiagnosticsByFile = updated
                            CrashesByFile = crashesByFile
                            RunAnalyzed = analyzed }
                | Custom(AnalysisFailed(file, error)) ->
                    ctx.ReportErrors file [ ErrorEntry.error $"Analyzer crashed: %s{error}" ]

                    ctx.EndSubtask PrimarySubtaskKey
                    PluginCtxHelpers.completeWith ctx $"analyzer crashed on {Path.GetFileName file}" TimeSpan.Zero
                    return state
                | _ -> return state
            }
      PrepareCommit = None
      Commands =
        [ "diagnostics",
          PluginCommand.Observe(fun _ctx state _args ->
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
              }) ]
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey =
        // pure-content cache key: the analyzer set's identity and failure threshold, the
        // config files the analyzers discover, the file, its source, the shape of what its
        // check resolved in other files, and its fcs-signature.
        // REPO-RELATIVE, like every other path in this key: an analyzer directory
        // inside the repository (`analyzers/`, the usual layout) named absolutely made
        // the key workspace-specific for no analytical reason. The CONTENT of the
        // assemblies is what decides the verdict, and that is the next slot down.
        let analyzerPathsHash =
            FsHotWatch.CheckCache.sha256Hex (
                String.concat
                    "|"
                    (analyzerPaths
                     |> List.map (FsHotWatch.CachePathIdentity.keyOf repoRoot)
                     |> List.sort)
            )

        // The present config files of a file's chain, by repo-relative path and content.
        let analyzerConfigHash (chain: (string * (string * string option) list) list) =
            chain
            |> List.collect snd
            |> List.choose (fun (key, content) ->
                content
                |> Option.map (fun text -> $"%s{key} %s{FsHotWatch.CheckCache.sha256Hex text}"))
            |> String.concat "\n"
            |> FsHotWatch.CheckCache.sha256Hex

        let cacheKey (event: PluginEvent<AnalyzersMsg>) : ContentHash option =
            countPendingReplay ()

            match event with
            | FileChecked result ->
                let file = AbsFilePath.value result.File

                // The key is computed once per event on the plugin's own loop, before
                // the lookup and the run, so refreshing here is what makes the entry
                // looked up and the analyzers that run describe the same set on disk —
                // and, for a config edit, analyzers loaded since it.
                refreshAnalyzerSet ()
                let chain = refreshConfigs file

                match (Volatile.Read(&snapshot)).Inputs with
                | Result.Error _ -> None
                | Result.Ok analyzerInputs ->
                    Volatile.Write(&replayPending, true)

                    // A shape that cannot be described leaves no key: the analyzers
                    // run and nothing is read from or written to the cache.
                    FsHotWatch.CacheInputs.usedSignaturesHash repoRoot result.ProjectOptions result.CheckResults
                    |> Option.map (fun usedSignatures ->
                        FsHotWatch.TaskCache.merkleCacheKey
                            // v7 orphans every entry keyed without the shape of what the
                            // file resolved upstream.
                            [ "plugin-version", "analyzers-merkle-v7"
                              "analyzer-paths", analyzerPathsHash
                              "analyzer-inputs", analyzerInputs
                              // Entries are stored AFTER promotion, so the same finding is
                              // a failure under one threshold and a pass under another.
                              "fail-on-severity", DiagnosticSeverity.toString failOnSeverity
                              "analyzer-config", analyzerConfigHash chain
                              "file", FsHotWatch.CachePathIdentity.keyOf repoRoot file
                              "source", result.Source
                              // A typed analyzer sees the types this file resolved in
                              // other files; its own diagnostics do not move when one
                              // changes shape and the file still compiles.
                              "used-signatures", usedSignatures
                              // Typed analyzers can read the file's own diagnostics.
                              "fcs-signature", FsHotWatch.CheckCache.fcsCheckSignature result.CheckResults ])
            | _ -> None

        Some(fun _state event -> cacheKey event)
      Teardown =
        Some(fun () ->
            // Cancellation is cooperative. Do not dispose tokens/semaphores while
            // a token-ignoring synchronous analyzer may still be unwinding: its
            // completion continuation owns the retained execution-fence permit.
            // These small managed objects become collectible with the handler.
            cts.Cancel()
            // A handler replaced by a config reload must not keep its analyzers loaded.
            // Unloading is cooperative too: a still-running analyzer keeps its context
            // alive until it returns.
            Volatile.Read(&loaded) |> List.iter AnalyzerLoad.unload) }

/// Creates a framework plugin handler that hosts F# analyzers in-process
/// using the warm checker's results. Per-event work is bounded by
/// `runWithCancellableTimeout`; on expiry the run is recorded as `TimedOut` and
/// the in-flight analyzer is cancelled rather than left holding its slot.
let create
    (repoRoot: string option)
    (analyzerPaths: string list)
    (timeoutSec: int option)
    (failOnSeverity: DiagnosticSeverity)
    : PluginHandler<AnalyzersState, AnalyzersMsg> =
    createWithSeams repoRoot analyzerPaths timeoutSec failOnSeverity None runSafely
