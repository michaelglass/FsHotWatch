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

/// The ledger entry for a crash: an Error at line 1 of the file, which no failure
/// threshold demotes, naming the analyzer so the verdict says which rule is off.
let internal crashFinding (crash: AnalyzerCrash) : ErrorEntry =
    { Message = $"analyzer %s{crash.Analyzer} crashed: %s{crash.Cause}"
      Severity = DiagnosticSeverity.Error
      Line = 1
      Column = 0
      Detail = Some crash.Trace }

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

/// Whether an analyzer failure is the FCS BINARY MISMATCH rather than a fault in the
/// analyzer or in the file it was given.
///
/// An analyzer package is compiled against one FCS and loaded here beside another.
/// While `CliContext.TypedTree` is `None` an analyzer that walks the typed tree
/// returns early and never touches the differing types, so the mismatch stays
/// invisible; hand it a real typed tree and it calls a member that no longer exists
/// and raises `MissingMethodException` — MEASURED with g-research 0.23.0 against FCS
/// 43.12.x, where 11 of 13 analyzers raise
/// `Method not found: FSharp.Compiler.Symbols.FSharpType.get_BasicQualifiedName()`.
///
/// `MissingMethodException` derives from `MissingMemberException`, and a type whose
/// shape moved surfaces as `TypeLoadException`; both mean the same thing here.
let internal isFcsBinaryMismatch (ex: exn) : bool =
    match ex with
    | :? MissingMemberException
    | :? TypeLoadException -> true
    | _ -> false

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
/// Whether an analyzer set has proved it cannot take a typed tree (see
/// `isFcsBinaryMismatch`). Once withheld, it stays withheld: the incompatibility is a
/// property of the LOADED ASSEMBLIES, not of the file being analyzed, so re-testing it
/// per file would re-break every file.
///
/// One per handler, not one per process: sessions sharing a repository host each load
/// their own analyzer set, and one set's incompatibility says nothing about another's.
type internal TypedTreeLatch() =
    let mutable withheld = 0

    member _.IsWithheld = Volatile.Read(&withheld) = 1

    /// Latch the withholding. Returns true the FIRST time, so the caller logs once
    /// rather than once per file.
    member _.Withhold() : bool =
        Threading.Interlocked.Exchange(&withheld, 1) = 0

let internal typedTreeOf (latch: TypedTreeLatch) (checkResults: FileCheckState) : obj =
    if latch.IsWithheld then
        noTypedTree
    else
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

/// Runs the loaded analyzer set over one file's context. Production uses the SDK's
/// `RunAnalyzersSafely`, which hands a raising analyzer's exception back as that
/// analyzer's result instead of throwing.
type internal AnalyzerRunner = Client<CliAnalyzerAttribute, CliContext> -> CliContext -> Async<AnalysisResult list>

let internal runSafely: AnalyzerRunner =
    fun client context -> client.RunAnalyzersSafely context

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
    // The SDK Client holds the loaded analyzer set in private state. A reload swaps
    // in a FRESH Client rather than re-loading the existing one: a fresh Client
    // picks up a newly-added analyzer for RunAnalyzersSafely, and can never replay a
    // now-removed/changed analyzer instance from prior private state. Volatile per
    // the plugin's thread-safety convention (concurrent FileChecked events read it
    // under the semaphore).
    let mutable client = Client<CliAnalyzerAttribute, CliContext>()
    let concurrencyLimit = 4
    let semaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit)
    // Only one synchronous analyzer callback may be alive at a time. Normal
    // FileChecked delivery is serialized already; this fence matters after a
    // timeout, when a token-ignoring callback can outlive its event handler.
    let executionFence = new SemaphoreSlim(1, 1)
    let typedTreeLatch = TypedTreeLatch()
    let cts = new CancellationTokenSource()



    // LoadAnalyzers reflects over every DLL in the directory — see
    // `knownNonAnalyzerPrefixes`.
    let excludeKnownDeps =
        ExcludeInclude.ExcludeFilter(isKnownNonAnalyzerPrefix knownNonAnalyzerPrefixes)

    // Per-path load result (path, count). A non-existent path counts 0 without
    // touching the loader; both 0-cases are silent-skips the fail-loud guard
    // turns RED.
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
            // Load the current set into a FRESH client and swap it in, so the added
            // analyzer is live (and any removed/changed one is gone) before we analyze.
            let fresh = Client<CliAnalyzerAttribute, CliContext>()
            let reloaded = loadInto fresh
            let reloadedCount = reloaded |> List.sumBy snd

            Volatile.Write(&client, fresh)

            info
                "analyzers"
                $"Analyzer assembly set changed on disk — reloaded %d{reloadedCount} analyzers from %d{analyzerPaths.Length} paths"

        if onDisk <> current.Materialization || Result.isError current.Inputs then
            let retaken = snapshotAnalyzerSet repoRoot knownNonAnalyzerPrefixes analyzerPaths

            Volatile.Write(&snapshot, retaken)

            match retaken.Inputs with
            | Result.Error refusals -> warnOnce refusals
            | Result.Ok _ -> ()

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
                    debug
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

                        let mutable runStarted = DateTime.UtcNow

                        refreshAnalyzerSet ()

                        let checkResultsObj =
                            match result.CheckResults with
                            | FullCheck cr -> box cr
                            | ParseOnly ->
                                debug "analyzers" $"Running parse-only analyzers for %s{fileStr}"
                                null

                        let typedTreeObj = typedTreeOf typedTreeLatch result.CheckResults

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
                                do! executionFence.WaitAsync(cts.Token) |> Async.AwaitTask
                                runStarted <- DateTime.UtcNow
                                ctx.ReportStatus(Running(since = runStarted))
                                ctx.StartSubtask PrimarySubtaskKey $"analyzing {Path.GetFileName fileStr}"
                                let mutable releaseExecutionFenceOnExit = true

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

                                                        // Drive the run under the timeout's token so a stuck
                                                        // analyzer is actually cancelled on expiry rather than
                                                        // orphaned holding the semaphore slot.
                                                        let activeClient = Volatile.Read(&client)

                                                        // Checked under a virtual root, every range in
                                                        // the results names the file by its virtual
                                                        // path: the analyzers are told the same name.
                                                        let analyzedFile =
                                                            FsHotWatch.PathFrame.nameIn result.Frame fileStr

                                                        let runWith (typedTree: obj) =
                                                            let context =
                                                                createCliContext
                                                                    (box analyzedFile)
                                                                    (box sourceText)
                                                                    (box result.ParseResults)
                                                                    checkResultsObj
                                                                    typedTree
                                                                    (box result.ProjectOptions)

                                                            Async.RunSynchronously(
                                                                runAnalyzersOver activeClient context,
                                                                cancellationToken = workCt
                                                            )

                                                        let results = runWith typedTreeObj

                                                        // An analyzer compiled against a different FCS raises
                                                        // only once it WALKS the typed tree. Withhold the tree
                                                        // and re-run rather than report an assembly mismatch as
                                                        // a finding about the file: the second run is what the
                                                        // host did before it offered a typed tree at all, so
                                                        // these analyzers keep the behaviour they already had
                                                        // while the rest keep the typed tree. No re-check is
                                                        // involved — the expensive half is already done.
                                                        let mismatched =
                                                            results
                                                            |> List.exists (fun r ->
                                                                match r.Output with
                                                                | Result.Error ex -> isFcsBinaryMismatch ex
                                                                | Result.Ok _ -> false)

                                                        if not mismatched then
                                                            results
                                                        else
                                                            if typedTreeLatch.Withhold() then
                                                                warn
                                                                    "analyzers"
                                                                    "An analyzer could not walk the typed tree (compiled against a different FSharp.Compiler.Service). Withholding CliContext.TypedTree for the rest of this session; typed-tree rules will report nothing. Rebuild the analyzer package against this FCS to enable them."

                                                            runWith noTypedTree

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
                                                        // Flip the recorded outcome to TimedOut; the
                                                        // verdict carries the summary (one channel).
                                                        ctx.CompleteWithTimeout reason

                                                        ctx.ReportStatus(
                                                            PluginStatus.failedNow
                                                                $"analyzers timed out: {reason}"
                                                                $"analyzers timed out: {reason}"
                                                                after
                                                        )

                                                        return Choice1Of3()
                                                    | WorkCompleted results ->
                                                        let toEntry (m: Message) : ErrorEntry =
                                                            { Message =
                                                                FsHotWatch.PathFrame.textFrom result.Frame m.Message
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

                            PluginCtxHelpers.completeWith
                                ctx
                                (summarizeRun analyzed updated crashesByFile)
                                (DateTime.UtcNow - runStarted)

                            return
                                { state with
                                    DiagnosticsByFile = updated
                                    CrashesByFile = crashesByFile
                                    RunAnalyzed = analyzed }
                        | Choice3Of3 errMsg ->
                            ctx.ReportErrors fileStr [ ErrorEntry.error $"Analyzer crashed: %s{errMsg}" ]
                            ctx.EndSubtask PrimarySubtaskKey

                            PluginCtxHelpers.completeWith
                                ctx
                                $"analyzer crashed on {Path.GetFileName fileStr}"
                                (DateTime.UtcNow - runStarted)

                            return state
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
        // config files the analyzers discover, the file, its source and its fcs-signature.
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

        let analyzerConfigHash (file: string) =
            // Analyzers read configuration by walking up from the file, not by being told:
            // MichaelGlass.FSharp.Analyzers reads `mga_*` keys from `.editorconfig`, and the
            // FSharpLint shim reads `fsharplint.json`. Without a repository root the walk
            // has no floor, so it runs to the filesystem root.
            let root =
                repoRoot
                |> Option.defaultWith (fun () -> Path.GetPathRoot(Path.GetFullPath file))

            analyzerConfigFiles
            |> List.collect (fun (fileName, label) ->
                FsHotWatch.CacheInputs.configChainInputs root fileName label [ file ])
            |> List.map (fun (label, content) -> $"%s{label} %s{FsHotWatch.CheckCache.sha256Hex content}")
            |> String.concat "\n"
            |> FsHotWatch.CheckCache.sha256Hex

        let cacheKey (event: PluginEvent<AnalyzersMsg>) : ContentHash option =
            countPendingReplay ()

            match event with
            | FileChecked result ->
                // The key is computed once per event on the plugin's own loop, before
                // the lookup and the run, so refreshing here is what makes the entry
                // looked up and the analyzers that run describe the same set on disk.
                refreshAnalyzerSet ()

                match (Volatile.Read(&snapshot)).Inputs with
                | Result.Error _ -> None
                | Result.Ok analyzerInputs ->
                    let file = AbsFilePath.value result.File
                    Volatile.Write(&replayPending, true)

                    Some(
                        FsHotWatch.TaskCache.merkleCacheKey
                            // v6 orphans every entry keyed without the failure threshold
                            // and the analyzers' config files.
                            [ "plugin-version", "analyzers-merkle-v6"
                              "analyzer-paths", analyzerPathsHash
                              "analyzer-inputs", analyzerInputs
                              // Entries are stored AFTER promotion, so the same finding is
                              // a failure under one threshold and a pass under another.
                              "fail-on-severity", DiagnosticSeverity.toString failOnSeverity
                              "analyzer-config", analyzerConfigHash file
                              "file", FsHotWatch.CachePathIdentity.keyOf repoRoot file
                              "source", result.Source
                              // fcs-signature captures cross-file FCS state changes so
                              // upstream symbol changes invalidate this file's cache.
                              "fcs-signature", FsHotWatch.CheckCache.fcsCheckSignature result.CheckResults ]
                    )
            | _ -> None

        Some(fun _state event -> cacheKey event)
      Teardown =
        Some(fun () ->
            // Cancellation is cooperative. Do not dispose tokens/semaphores while
            // a token-ignoring synchronous analyzer may still be unwinding: its
            // completion continuation owns the retained execution-fence permit.
            // These small managed objects become collectible with the handler.
            cts.Cancel()) }

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
