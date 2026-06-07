module FsHotWatch.Daemon

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open Ionide.ProjInfo
open System.Text.Json
open FsHotWatch.CheckCache
open FsHotWatch.CheckPipeline
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.FcsDiagnosticFilter
open FsHotWatch.Ipc
open FsHotWatch.Logging
open FsHotWatch.Plugin
open FsHotWatch.Watcher
open FsHotWatch.PluginHost
open FsHotWatch.ProjectGraph

/// Extract FCS diagnostics from check results and report to the error ledger.
/// Reports all severity levels (Error, Warning, Info, Hidden) with configurable
/// suppressed diagnostic codes.
let private reportFcsDiagnostics (suppressedCodes: Set<int>) (host: PluginHost) (checkResult: Events.FileCheckResult) =
    match checkResult.CheckResults with
    | ParseOnly -> ()
    | FullCheck checkResults ->
        let mapSeverity =
            function
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error -> DiagnosticSeverity.Error
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning -> DiagnosticSeverity.Warning
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Info -> DiagnosticSeverity.Info
            | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Hidden -> DiagnosticSeverity.Hint

        // Merge global suppressed codes with per-file #nowarn directives.
        // Workaround for https://github.com/dotnet/fsharp/issues/9796.
        // Shared with TestPrunePlugin's `hasFcsErrors` cache-poisoning gate
        // via `FcsDiagnosticFilter.allSuppressedCodes` so the user-visible
        // diagnostic stream and the gate agree on which codes are noise.
        let allSuppressed = allSuppressedCodes suppressedCodes checkResult.Source

        let diagnostics =
            checkResults.Diagnostics
            |> Array.choose (fun d ->
                if allSuppressed.Contains(d.ErrorNumber) then
                    None
                else
                    Some
                        { Message = d.Message
                          Severity = mapSeverity d.Severity
                          Line = d.StartLine
                          Column = d.StartColumn
                          Detail = None })
            |> Array.toList

        if diagnostics.IsEmpty then
            host.ClearErrors(
                PluginActivity.FcsPluginName,
                AbsFilePath.value checkResult.File,
                version = checkResult.Version
            )
        else
            host.ReportErrors(
                PluginActivity.FcsPluginName,
                AbsFilePath.value checkResult.File,
                diagnostics,
                version = checkResult.Version
            )

/// Apply the deps-freshness gate to one project before its files are checked.
/// Returns `true` when FCS analysis should proceed for the project, `false`
/// when it must be skipped (stale assets that couldn't be auto-recovered, or a
/// still-stale state we already attempted). On a fail-fast, exactly one Error
/// diagnostic is reported through the ledger under the `deps` plugin so the
/// aggregate verdict is non-zero and the phantom error-storm is never produced.
/// `None` gate (test daemons) always proceeds. Pure of FCS — only consults the
/// injected gate and reports/clears via the host.
let internal applyDepsGate
    (gate: (string -> DepsFreshness.GateResult) option)
    (host: PluginHost)
    (projPath: string)
    : bool =
    match gate with
    | None -> true
    | Some g ->
        match g projPath with
        | DepsFreshness.Proceed ->
            host.ClearErrors(DepsFreshness.pluginName, projPath)
            true
        | DepsFreshness.RecoveredOk ->
            Logging.info "deps" $"%s{Path.GetFileName projPath}: deps were stale — auto-restored OK"
            host.ClearErrors(DepsFreshness.pluginName, projPath)
            true
        | DepsFreshness.SkipAlreadyAttempted ->
            Logging.warn
                "deps"
                $"%s{Path.GetFileName projPath}: deps still stale (recovery already attempted) — skipping FCS analysis"

            false
        | DepsFreshness.FailFast(message, detail) ->
            Logging.error "deps" message

            let entry =
                if System.String.IsNullOrEmpty detail then
                    ErrorLedger.ErrorEntry.error message
                else
                    ErrorLedger.ErrorEntry.errorWithDetail message detail

            host.ReportErrors(DepsFreshness.pluginName, projPath, [ entry ])
            false

/// Fingerprint fsproj files by path + last-write-time. Used by ScanAll to skip
/// expensive MSBuild re-evaluation when no project files have changed.
let private fingerprintFsprojFiles (repoRoot: string) =
    let searchDirs =
        [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]
        |> List.filter Directory.Exists

    searchDirs
    |> List.collect (fun dir -> Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories) |> Array.toList)
    |> List.filter (fun f -> not (PathFilter.isGeneratedPath f))
    |> List.map (fun f -> f, File.GetLastWriteTimeUtc(f).Ticks)
    |> Set.ofList

[<Literal>]
let internal projInfoBinlogEnvVar = "FSHW_PROJINFO_BINLOG"

let internal projInfoLogDir (repoRoot: string) =
    Path.Combine(FsHwPaths.root repoRoot, "logs", "projinfo")

let internal isTruthyEnv (name: string) =
    match Environment.GetEnvironmentVariable(name) with
    | null
    | "" -> false
    | v ->
        let v = v.Trim()

        not (v.Equals("0", StringComparison.OrdinalIgnoreCase))
        && not (v.Equals("false", StringComparison.OrdinalIgnoreCase))

let internal countReferences (otherOptions: string[]) =
    otherOptions |> Array.sumBy (fun o -> if o.StartsWith("-r:") then 1 else 0)

/// Dumps source files, OtherOptions, and referenced project outputs to
/// `<logDir>/<ProjectName>.opts.txt` for diffing vs `dotnet build`.
let internal dumpProjectOptions (logDir: string) (fcsOptions: FSharpProjectOptions) : unit =
    try
        let name = Path.GetFileNameWithoutExtension(fcsOptions.ProjectFileName)
        let file = Path.Combine(logDir, $"%s{name}.opts.txt")

        let lines =
            seq {
                yield $"# Project: %s{fcsOptions.ProjectFileName}"
                yield $"# SourceFiles ({fcsOptions.SourceFiles.Length}):"
                yield! fcsOptions.SourceFiles |> Seq.map (fun f -> $"  %s{f}")
                yield $"# OtherOptions ({fcsOptions.OtherOptions.Length}):"
                yield! fcsOptions.OtherOptions |> Seq.map (fun o -> $"  %s{o}")
                yield $"# ReferencedProjects ({fcsOptions.ReferencedProjects.Length}):"
                yield! fcsOptions.ReferencedProjects |> Seq.map (fun r -> $"  %s{r.OutputFile}")
            }

        File.WriteAllLines(file, lines)
    with ex ->
        Logging.debug "discover" $"Could not dump options for %s{fcsOptions.ProjectFileName}: %s{ex.Message}"

/// Discover .fsproj files and register them with the graph and pipeline.
/// Uses Ionide.ProjInfo for MSBuild design-time evaluation to get real
/// assembly references, NuGet packages, and compiler flags.
let private discoverAndRegisterProjects
    (repoRoot: string)
    (loader: IWorkspaceLoader)
    (graph: ProjectGraph)
    (pipeline: CheckPipeline)
    (excludePatterns: string list)
    (clearCheckCache: bool)
    =
    async {
        let isExcluded = PathFilter.isExcludedPath repoRoot excludePatterns

        let searchDirs =
            [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]
            |> List.filter Directory.Exists

        let fsprojFiles =
            searchDirs
            |> List.collect (fun dir ->
                Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories) |> Array.toList)
            |> List.filter (fun f -> not (isExcluded f))

        if List.isEmpty fsprojFiles then
            // Surface zero-project discoveries: this is almost always a
            // misconfiguration (wrong working directory, an over-eager
            // .fshw.json `exclude` pattern, or an empty repo) and silently
            // running with no work to do hides the problem from users.
            let searched =
                searchDirs
                |> List.map (fun d -> Path.GetFileName(d.TrimEnd('/')))
                |> String.concat ", "

            let searched = if searched = "" then "src/, tests/" else searched

            Logging.warn
                "discover"
                $"No .fsproj files discovered under %s{searched} of %s{repoRoot}. Check `.fshw.json` exclude patterns or working directory."

        graph.PrepareForRediscovery()
        pipeline.PrepareForRediscovery(clearCheckCache = clearCheckCache)

        let logDir = projInfoLogDir repoRoot
        Directory.CreateDirectory(logDir) |> ignore

        let binlogDir =
            if isTruthyEnv projInfoBinlogEnvVar then
                let dir = Path.Combine(logDir, "binlogs")
                Directory.CreateDirectory(dir) |> ignore
                Some(DirectoryInfo(dir))
            else
                None

        // Subscribe to per-project load notifications so failures (ProjectNotRestored,
        // ReferencesNotLoaded, etc.) surface instead of being silently dropped from the
        // LoadProjects return seq. Without this, a project that fails design-time eval
        // just doesn't appear in `loaded` — callers never learn why references are missing.
        use _sub =
            loader.Notifications.Subscribe(fun state ->
                match state with
                | Types.WorkspaceProjectState.Failed(proj, reason) ->
                    Logging.error "discover" $"LoadProject FAILED for %s{Path.GetFileName proj}: %A{reason}"
                | Types.WorkspaceProjectState.Loading proj ->
                    Logging.debug "discover" $"Loading %s{Path.GetFileName proj}"
                | Types.WorkspaceProjectState.Loaded _ -> ())

        let sw = System.Diagnostics.Stopwatch.StartNew()
        Logging.info "discover" "Loading project options via MSBuild evaluation..."

        try
            let loaded =
                match binlogDir with
                | Some dir ->
                    Logging.info "discover" $"Binlog capture enabled: %s{dir.FullName}"

                    loader.LoadProjects(fsprojFiles, [], BinaryLogGeneration.Within dir)
                    |> Seq.toList
                | None -> loader.LoadProjects(fsprojFiles) |> Seq.toList

            // Register projects in the graph using Ionide-derived data (not XML parse)
            // so source file lists match what FCS sees (handles globs, conditionals, generated files)
            for proj in loaded do
                if not (isExcluded proj.ProjectFileName) then
                    let absProject = AbsProjectPath.create proj.ProjectFileName
                    let sourceFiles = proj.SourceFiles |> List.map AbsFilePath.create

                    let references =
                        proj.ReferencedProjects
                        |> List.map (fun r -> AbsProjectPath.create r.ProjectFileName)

                    graph.RegisterProject(absProject, sourceFiles, references)

            let fcsOptionsList = Ionide.ProjInfo.FCS.mapManyOptions loaded |> Seq.toList
            sw.Stop()

            Logging.info
                "discover"
                $"MSBuild evaluation complete: %d{fcsOptionsList.Length} projects in %.1f{sw.Elapsed.TotalSeconds}s"

            for fcsOptions in fcsOptionsList do
                if not (isExcluded fcsOptions.ProjectFileName) then
                    try
                        let absProject = Path.GetFullPath(fcsOptions.ProjectFileName)
                        pipeline.RegisterProject(absProject, fcsOptions)
                        dumpProjectOptions logDir fcsOptions
                        let refCount = countReferences fcsOptions.OtherOptions

                        Logging.info
                            "discover"
                            $"Registered %s{Path.GetFileName fcsOptions.ProjectFileName} (%d{fcsOptions.SourceFiles.Length} files, %d{fcsOptions.OtherOptions.Length} opts, %d{refCount} refs)"
                    with ex ->
                        Logging.error
                            "discover"
                            $"Failed to register %s{Path.GetFileName fcsOptions.ProjectFileName}: %s{ex.Message}"
        with ex ->
            sw.Stop()
            Logging.error "discover" $"MSBuild evaluation failed (%.1f{sw.Elapsed.TotalSeconds}s): %s{ex.Message}"
    }

/// Map a batch of changed project-tier paths (`.fsproj`, `.props`, or
/// `obj/project.assets.json`) to the set of *known* `.fsproj` paths whose FCS
/// state should be scoped-invalidated.
///
/// Returns `None` when the change is repo-wide and a full re-discovery is the
/// only safe response:
///   - any `.props` file (Directory.Build.props et al. affect every project),
///   - a `project.assets.json` or `.fsproj` under a directory that doesn't
///     match a currently-known project (a brand-new project the graph hasn't
///     registered yet — needs full discovery to pick up).
///
/// `Some projects` lists the affected known projects; the caller still expands
/// to transitive dependents before invalidating/re-checking. Order is not
/// significant.
let internal resolveAffectedProjects (knownProjects: string list) (changedPaths: string list) : string list option =
    let normalize (p: string) = Path.GetFullPath(p).Replace('\\', '/')

    let knownNorm = knownProjects |> List.map normalize

    let projDirToFsproj =
        knownNorm
        |> List.map (fun p -> (Path.GetDirectoryName(p: string)), p)
        |> Map.ofList

    let rec loop (acc: string list) (remaining: string list) =
        match remaining with
        | [] -> Some(List.distinct acc)
        | path :: rest ->
            let basename = Path.GetFileName(path: string)

            if basename.EndsWith(".props", StringComparison.OrdinalIgnoreCase) then
                None
            elif basename.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase) then
                // <projDir>/obj/project.assets.json → owning <projDir>/<name>.fsproj
                let objDir = Path.GetDirectoryName(path: string)

                if isNull objDir then
                    None
                else
                    let projDir = normalize (Path.GetDirectoryName(objDir: string))

                    match Map.tryFind projDir projDirToFsproj with
                    | Some fsproj -> loop (fsproj :: acc) rest
                    | None -> None
            elif basename.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) then
                let norm = normalize path

                if List.contains norm knownNorm then
                    loop (norm :: acc) rest
                else
                    None
            else
                // Unexpected non-project path in a project-tier batch — be safe.
                None

    loop [] changedPaths

/// Re-discover projects and clear FCS errors for any files that were removed.
/// `clearCheckCache` controls whether the full FileCheckCache is dropped:
///   - `true` (full re-discovery): the conservative behavior — every cached
///     check result is discarded, so the subsequent re-check recomputes
///     everything. Used when the change is repo-wide (`.props`, solution,
///     new/removed project) and we can't reason about which projects are stale.
///   - `false` (scoped re-discovery): the project→options maps are rebuilt
///     (so file/project membership is fresh) but cached check results are
///     kept. Projects whose options didn't change keep their warm cache; the
///     caller is responsible for explicitly invalidating the affected project
///     and its transitive dependents (see `InvalidateProjectFiles`).
/// Returns the set of removed files.
let private rediscoverAndClearRemoved
    (repoRoot: string)
    (loader: IWorkspaceLoader)
    (graph: ProjectGraph)
    (pipeline: CheckPipeline)
    (host: PluginHost)
    (logTag: string)
    (excludePatterns: string list)
    (clearCheckCache: bool)
    =
    async {
        let oldFiles = graph.GetAllFiles() |> Set.ofList
        do! discoverAndRegisterProjects repoRoot loader graph pipeline excludePatterns clearCheckCache
        let newFiles = graph.GetAllFiles() |> Set.ofList
        let removedFiles = Set.difference oldFiles newFiles

        for file in removedFiles do
            host.ClearErrors(PluginActivity.FcsPluginName, AbsFilePath.value file)

        if not removedFiles.IsEmpty then
            Logging.info logTag $"Cleared errors for %d{removedFiles.Count} removed files"

        return removedFiles
    }

/// Manages TaskCompletionSource instances for signal-based WaitForScan.
[<NoComparison; NoEquality>]
type private ScanSignalMsg =
    | WaitFor of afterGen: int64 * TaskCompletionSource<unit>
    | Signal of newGen: int64
    /// F12 (audit 2026-05-02) test seam: see ErrorLedger.LedgerMsg.RaiseFaultForTest
    /// for the rationale. Production messages don't have a natural failure
    /// mode, so this is the only realistic way to verify the agent surfaces
    /// programming bugs instead of swallowing them.
    | RaiseFaultForTest of exn

type ScanSignal(?cancellationToken: CancellationToken) =
    let agent =
        MailboxProcessor.Start(
            (fun inbox ->
                // latestGeneration latches the most recent SignalGeneration so a
                // WaitFor that arrives after the signal can resolve immediately.
                // Without this, a race between performScan signalling completion
                // and the client posting WaitFor leaves the waiter hanging.
                let rec loop (latestGeneration: int64) (waiters: (int64 * TaskCompletionSource<unit>) list) =
                    async {
                        let! msg = inbox.Receive()

                        // F12 (audit 2026-05-02): no inner try/with — the body is a
                        // typed match over messages we own; tcs.TrySetResult,
                        // List.partition, and Logging.debug do not throw on valid
                        // state. Anything that throws is a programming bug that
                        // surfaces via `agent.Error` (exposed as `AgentCrashed`)
                        // rather than silently looping in the original state.
                        match msg with
                        | WaitFor(afterGeneration, tcs) ->
                            let alreadySatisfied =
                                if afterGeneration >= 0L then
                                    latestGeneration > afterGeneration
                                else
                                    latestGeneration > 0L

                            if alreadySatisfied then
                                Logging.debug
                                    "scan-signal"
                                    $"WaitFor(%d{afterGeneration}) — already satisfied (latest=%d{latestGeneration}), resolving"

                                tcs.TrySetResult(()) |> ignore
                                return! loop latestGeneration waiters
                            else
                                Logging.debug "scan-signal" $"WaitFor(%d{afterGeneration}) — registering waiter"
                                return! loop latestGeneration ((afterGeneration, tcs) :: waiters)

                        | Signal newGeneration ->
                            let toSignal, remaining =
                                waiters
                                |> List.partition (fun (afterGen, _) -> afterGen < 0L || newGeneration > afterGen)

                            Logging.debug
                                "scan-signal"
                                $"SignalGeneration(%d{newGeneration}) — resolving %d{toSignal.Length} waiters, %d{remaining.Length} remaining"

                            for _, tcs in toSignal do
                                tcs.TrySetResult(()) |> ignore

                            return! loop (max latestGeneration newGeneration) remaining

                        | RaiseFaultForTest ex -> raise ex
                    }

                loop 0L []),
            ?cancellationToken = cancellationToken
        )

    do
        agent.Error.Add(fun ex ->
            // F12 (audit 2026-05-02): an unhandled exception inside the agent
            // loop is a programming bug. Log loudly with the full stack trace
            // (ex.ToString(), not ex.Message); the agent stops and pending
            // waiters' WaitForGeneration tasks remain unresolved — a visible
            // hang at the next caller, not the previous silent loop-and-drop.
            Logging.error "scan-signal" $"Mailbox loop crashed (programming bug, agent stopped): %s{ex.ToString()}")

    /// Register a waiter that resolves when generation exceeds afterGeneration.
    /// If afterGeneration < 0, resolves on the next generation increment.
    member _.WaitForGeneration(afterGeneration: int64, currentGeneration: int64) : Task<unit> =
        let alreadySatisfied =
            if afterGeneration >= 0L then
                currentGeneration > afterGeneration
            else
                currentGeneration > 0L

        if alreadySatisfied then
            Logging.debug
                "scan-signal"
                $"WaitForGeneration(%d{afterGeneration}, %d{currentGeneration}) — already satisfied, returning immediately"

            Task.FromResult(())
        else
            let tcs =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            agent.Post(WaitFor(afterGeneration, tcs))
            tcs.Task

    /// Signal all waiters whose afterGeneration is now satisfied.
    member _.SignalGeneration(newGeneration: int64) = agent.Post(Signal newGeneration)

    /// F12 (audit 2026-05-02): unhandled exceptions inside the mailbox loop
    /// surface here. Subscribe to observe programming bugs that the previous
    /// inner try/with would have silently swallowed.
    member _.AgentCrashed: IEvent<exn> = agent.Error

    /// F12 test seam: deterministically raise inside the agent loop. See
    /// `ErrorLedger.RaiseFaultForTest` for rationale.
    member internal _.RaiseFaultForTest(ex: exn) = agent.Post(RaiseFaultForTest ex)

/// Messages handled by the scan agent. The agent owns ScanState + Generation
/// in its loop's recursion — readers round-trip via PostAndReply so they never
/// see a stale snapshot. Replaces an earlier wrapper with mutable Volatile
/// fields and an `option ref` bootstrap cycle.
[<NoComparison; NoEquality>]
type private ScanMsg =
    | RequestScan of CancellationToken * AsyncReplyChannel<unit>
    | GetState of AsyncReplyChannel<ScanState>
    | GetGeneration of AsyncReplyChannel<int64>
    | SetState of ScanState * AsyncReplyChannel<unit>

/// Internal state managed by the scan agent.
type private ScanAgentState =
    { ScanState: ScanState
      Generation: int64
      LastFingerprint: Set<string * int64> }

/// Opaque handle to the scan MailboxProcessor. State (ScanState, Generation)
/// lives inside the loop body; reads are sub-microsecond mailbox round-trips.
[<NoComparison; NoEquality>]
type ScanAgent = private ScanAgent of MailboxProcessor<ScanMsg>

let private requestScan (ScanAgent agent) ct =
    agent.PostAndAsyncReply(fun ch -> RequestScan(ct, ch))

let private getScanGeneration (ScanAgent agent) =
    agent.PostAndReply(fun ch -> GetGeneration ch)

let private getScanStatus (ScanAgent agent) =
    agent.PostAndReply(fun ch -> GetState ch)

let private setScanStatus (ScanAgent agent) state =
    agent.PostAndReply(fun ch -> SetState(state, ch))

let private isTerminal (s: PluginStatus) =
    match s with
    | Running _ -> false
    | _ -> true

let private allTerminal (statuses: Map<string, PluginStatus>) =
    not statuses.IsEmpty && statuses |> Map.forall (fun _ s -> isTerminal s)

/// F13 (audit 2026-05-02): centralized failure handler for daemon batch/scan
/// steps. `processBatch` and `performScan` transitively call FCS, MSBuild, and
/// arbitrary plugin Update functions — there isn't a smaller exception type
/// that captures "anything from this layer", so the broad `Exception` catch
/// is genuinely justified at this boundary. We hoist the policy to one place
/// (rather than inlining the same `with ex -> log; idle` arm at three call
/// sites) so the contract is readable and reviewable in one read:
///   - `OperationCanceledException` is NOT a failure: it's the cancellation
///     signal threaded through `CancellationToken`. Re-raise so async
///     pipelines unwind; the caller decides whether to enter idle.
///   - Anything else is logged with `ex.ToString()` (full stack + inner
///     chain — `ex.Message` alone strips the diagnostic trail) and returned
///     as `Error`. Callers fall back to idle to keep the daemon responsive.
///
/// Follow-up (deferred — see audit F13): the broad `Exception` arm should
/// eventually be replaced by the narrow set FCS/MSBuild/plugins actually
/// raise once we have a top-level daemon Failed-status path that surfaces
/// truly unexpected exceptions to the user instead of just logging them.
/// That refactor is invasive (touches PluginHost status reporting) and is
/// tracked as the §5 Tier-2 follow-up "top-level daemon failure handler design".
let internal runDaemonStep (label: string) (work: Async<'T>) : Async<Result<'T, exn>> =
    async {
        try
            let! r = work
            return Ok r
        with
        | :? OperationCanceledException as ex ->
            Logging.debug "daemon" $"%s{label} cancelled"
            return raise ex
        | ex ->
            Logging.error "daemon" $"%s{label} failed: %s{ex.ToString()}"
            return Result.Error ex
    }

/// Dependencies for processBatch, bundled to avoid a long closure capture list.
[<NoComparison; NoEquality>]
type internal BatchContext =
    {
        Host: PluginHost
        /// Global FCS invalidation — drops every project's checker state plus
        /// the language-service root caches. Used by the full re-discovery
        /// path (repo-wide `.props` / solution / new-project changes).
        InvalidateFcs: (unit -> unit) option
        /// Scoped FCS invalidation — drops just the given projects' checker
        /// configurations, leaving every other project's state warm. Used by
        /// the scoped project-change path. Receives each affected project's
        /// *current* (pre-re-discovery) `FSharpProjectOptions`. `None` disables
        /// the scoped path (test daemons with a null checker).
        InvalidateFcsForProjects: (FSharpProjectOptions list -> unit) option
        RepoRoot: string
        Loader: IWorkspaceLoader
        Graph: ProjectGraph.ProjectGraph
        Pipeline: CheckPipeline
        DaemonCt: CancellationToken ref
        FcsSuppressedCodes: Set<int>
        ExcludePatterns: string list
        /// Monotonic counter bumped per `InSessionBatch` `BatchChecked` emitted
        /// from `processBatch`. Per-trigger generation lets subscribers dedup
        /// "latest in-session cohort" without colliding with scan generations
        /// (which use the scan agent's own counter). Boxed `ref` so the
        /// long-lived BatchContext shares state across batches.
        InSessionBatchGen: int64 ref
        /// Deps-freshness gate: given a project's `.fsproj` path, decides
        /// whether its restored `obj/project.assets.json` is in sync with its
        /// declared deps (running a one-shot restore to recover) before FCS
        /// analysis. `None` disables the gate (test daemons with a null
        /// checker). See `DepsFreshness.evaluateProject`.
        DepsGate: (string -> DepsFreshness.GateResult) option
    }

/// Process a batch of debounced file changes: filter, re-discover projects if needed,
/// run preprocessors, emit events, and check files.
let internal processBatch (ctx: BatchContext) (changes: FileChangeKind list) (suppressed: Set<string>) =
    async {
        let mutable sourceFiles = []
        let mutable projFiles = []
        let mutable hasSolution = false

        for c in changes do
            match c with
            | SourceChanged files -> sourceFiles <- files @ sourceFiles
            | ProjectChanged files -> projFiles <- files @ projFiles
            | SolutionChanged -> hasSolution <- true

        Logging.debug
            "daemon"
            $"processChanges: %d{sourceFiles.Length} source, %d{projFiles.Length} project, solution=%b{hasSolution}"

        for f in sourceFiles do
            Logging.debug "daemon" $"source: %s{f}"

        for f in projFiles do
            Logging.debug "daemon" $"project: %s{f}"

        // Filter out files written by preprocessors (suppress re-trigger)
        let filteredSourceFiles, remainingSuppressed =
            sourceFiles
            |> List.distinct
            |> List.fold
                (fun (accepted, sup) f ->
                    if Set.contains f sup then
                        Logging.debug "daemon" $"suppressed: %s{f}"
                        (accepted, Set.remove f sup)
                    else
                        (f :: accepted, sup))
                ([], suppressed)

        let mutable allSourceFiles =
            filteredSourceFiles
            |> List.rev
            |> List.filter (fun f ->
                let changed = ContentDedup.hasContentChanged f

                if not changed then
                    Logging.debug "daemon" $"content unchanged: %s{f}"

                changed)

        let projFilesChanged =
            projFiles
            |> List.distinct
            |> List.filter (fun f ->
                let changed = ContentDedup.hasContentChanged f

                if not changed then
                    Logging.debug "daemon" $"content unchanged: %s{f}"

                changed)

        if hasSolution then
            ctx.Host.EmitFileChanged(SolutionChanged)

        if not projFilesChanged.IsEmpty || hasSolution then
            // Generated obj/ files (MSBuild's AssemblyInfo / AssemblyAttributes)
            // are in the ProjectGraph (it stores the raw ProjInfo SourceFiles)
            // but the CheckPipeline filters them out of its options, so feeding
            // them to FCS yields a spurious "not part of the project" error.
            // The pipeline's source list is authoritative for what's checkable.
            let checkableFilesOf (projects: AbsProjectPath list) =
                projects
                |> List.collect ctx.Graph.GetSourceFiles
                |> List.map AbsFilePath.value
                |> List.filter (fun f -> not (PathFilter.isGeneratedPath f))
                |> List.distinct

            // Decide scoped vs. full. Scoped applies only when every changed
            // path maps to a known project (no `.props`, no new project, no
            // solution edit) AND a scoped FCS invalidator is wired.
            let scopedProjects =
                if hasSolution then
                    None
                else
                    resolveAffectedProjects (ctx.Pipeline.GetRegisteredProjects()) projFilesChanged

            match scopedProjects, ctx.InvalidateFcsForProjects with
            | Some affectedFsprojs, Some invalidateScoped when not (List.isEmpty affectedFsprojs) ->
                // ── Scoped path ──────────────────────────────────────────────
                // Re-check the affected projects AND their transitive
                // dependents (a dependent's compilation can break when the
                // changed project's public surface changes). Everything else
                // keeps its warm FCS + cached check results.
                let recheckProjects =
                    affectedFsprojs
                    |> List.map AbsProjectPath.create
                    |> List.collect ctx.Graph.GetTransitiveDependents
                    |> List.distinct

                // Snapshot current options BEFORE re-discovery — these are the
                // configs FCS currently holds and the keys the cache currently
                // uses. Used for FCS invalidation and (via InvalidateFile) cache
                // eviction so dependents whose options-hash is unchanged still
                // recompute instead of serving a stale cached result.
                let oldOpts =
                    recheckProjects
                    |> List.choose (fun p -> ctx.Pipeline.GetProjectOptions(AbsProjectPath.value p))

                for f in checkableFilesOf recheckProjects |> List.map AbsFilePath.create do
                    ctx.Pipeline.InvalidateFile f

                invalidateScoped oldOpts

                Logging.info
                    "daemon"
                    $"Scoped project change — %d{affectedFsprojs.Length} changed + %d{recheckProjects.Length - affectedFsprojs.Length} dependent project(s) invalidated; rest stay warm"

                let! _ =
                    rediscoverAndClearRemoved
                        ctx.RepoRoot
                        ctx.Loader
                        ctx.Graph
                        ctx.Pipeline
                        ctx.Host
                        "daemon"
                        ctx.ExcludePatterns
                        false // keep unrelated projects' check cache

                if not projFilesChanged.IsEmpty then
                    ctx.Host.EmitFileChanged(ProjectChanged projFilesChanged)

                // Re-derive source files from the refreshed graph (membership
                // may have shifted) for the same project set.
                allSourceFiles <- (allSourceFiles @ checkableFilesOf recheckProjects) |> List.distinct

            | _ ->
                // ── Full path ────────────────────────────────────────────────
                // Repo-wide change (.props / solution), a project the graph
                // doesn't know yet, or no scoped invalidator (null-checker test
                // daemon). Drop everything and re-check the whole graph.
                Logging.info "daemon" "Project/solution change detected — full re-discovery"

                ctx.InvalidateFcs |> Option.iter (fun invalidate -> invalidate ())

                let! _ =
                    rediscoverAndClearRemoved
                        ctx.RepoRoot
                        ctx.Loader
                        ctx.Graph
                        ctx.Pipeline
                        ctx.Host
                        "daemon"
                        ctx.ExcludePatterns
                        true

                Logging.info
                    "daemon"
                    $"Re-discovery complete: %d{ctx.Graph.GetAllProjects().Length} projects, %d{ctx.Pipeline.GetAllRegisteredFiles().Length} files"

                if not projFilesChanged.IsEmpty then
                    ctx.Host.EmitFileChanged(ProjectChanged projFilesChanged)

                allSourceFiles <-
                    (allSourceFiles @ checkableFilesOf (ctx.Graph.GetAllProjects()))
                    |> List.distinct

        let batchStartedAt = System.DateTime.UtcNow
        let dispatchedFiles = ResizeArray<AbsFilePath>()

        if not allSourceFiles.IsEmpty then
            let modifiedByPreprocessors = ctx.Host.RunPreprocessors(allSourceFiles)

            let newSuppressed =
                Set.union remainingSuppressed (Set.ofList modifiedByPreprocessors)

            let absSourceFiles = allSourceFiles |> List.map AbsFilePath.create

            let changedProjects =
                absSourceFiles
                |> List.collect (fun f -> ctx.Graph.GetProjectsForFile(f))
                |> List.distinct

            let changedProjectSet = Set.ofList changedProjects

            let dependentProjectFiles =
                changedProjects
                |> List.collect (fun p -> ctx.Graph.GetTransitiveDependents(p))
                |> List.distinct
                |> List.filter (fun p -> not (Set.contains p changedProjectSet))
                |> List.collect (fun proj -> ctx.Graph.GetSourceFiles(proj))
                |> List.map AbsFilePath.value

            let allFilesToCheck =
                (allSourceFiles @ dependentProjectFiles)
                |> List.map AbsFilePath.create
                |> List.distinct

            ctx.Host.EmitFileChanged(SourceChanged(allFilesToCheck |> List.map AbsFilePath.value))

            Logging.debug "daemon" $"Checking %d{allFilesToCheck.Length} files after change"
            let mutable checkedFiles = Set.empty
            let filesToCheckSet = allFilesToCheck |> Set.ofList
            let tiers = ctx.Graph.GetParallelTiers()

            let emitResults (results: FileCheckResult option array) =
                for result in results do
                    match result with
                    | Some checkResult ->
                        Logging.debug
                            "daemon"
                            $"EmitFileChecked: %s{Path.GetFileName(AbsFilePath.value checkResult.File)}"

                        dispatchedFiles.Add(checkResult.File)
                        ctx.Host.EmitFileChecked(checkResult)
                        reportFcsDiagnostics ctx.FcsSuppressedCodes ctx.Host checkResult
                    | None -> ()

            for tier in tiers do
                let tierChecks = ResizeArray<Async<FileCheckResult option>>()

                for proj in tier do
                    let projPath = AbsProjectPath.value proj

                    let projFiles =
                        ctx.Graph.GetSourceFiles(proj) |> List.filter filesToCheckSet.Contains

                    checkedFiles <- Set.union checkedFiles (Set.ofList projFiles)

                    // Deps-freshness gate (incremental path): skip FCS for a
                    // project whose restored assets are stale and couldn't be
                    // auto-recovered — see `applyDepsGate`.
                    if applyDepsGate ctx.DepsGate ctx.Host projPath then
                        match ctx.Pipeline.GetProjectOptions(projPath) with
                        | Some options ->
                            for file in projFiles do
                                tierChecks.Add(ctx.Pipeline.CheckFileWithOptions(file, options, ctx.DaemonCt.Value))
                        | None ->
                            for file in projFiles do
                                tierChecks.Add(ctx.Pipeline.CheckFile(file, ctx.DaemonCt.Value))

                let! results = tierChecks |> Seq.toList |> Async.Parallel
                emitResults results

            // Check files not belonging to any project (e.g. standalone .fsx files)
            let uncovered = Set.difference filesToCheckSet checkedFiles |> Set.toList

            if not uncovered.IsEmpty then
                let! results =
                    uncovered
                    |> List.map (fun file -> ctx.Pipeline.CheckFile(file, ctx.DaemonCt.Value))
                    |> Async.Parallel

                emitResults results

            // Empty cohorts (every file filtered as content-unchanged or no
            // results from the pipeline) skip the emit — there's nothing to
            // "flush and decide" against.
            if dispatchedFiles.Count > 0 then
                let nextGen =
                    System.Threading.Interlocked.Increment(&ctx.InSessionBatchGen.contents)

                ctx.Host.EmitBatchChecked
                    { Trigger = InSessionBatch changes
                      Files = dispatchedFiles |> List.ofSeq
                      Generation = nextGen
                      StartedAt = batchStartedAt
                      CompletedAt = System.DateTime.UtcNow }

            return newSuppressed
        else
            return remainingSuppressed
    }

/// Format elapsed as human-readable "5m 3s" / "45s" / "1h 12m". Public so
/// consumers + tests can share the same cadence.
let formatElapsed (ts: System.TimeSpan) =
    if ts.TotalHours >= 1.0 then
        $"%d{int ts.TotalHours}h %d{ts.Minutes}m"
    elif ts.TotalMinutes >= 1.0 then
        $"%d{int ts.TotalMinutes}m %d{ts.Seconds}s"
    else
        $"%d{ts.Seconds}s"

/// Pure formatter for "Waiting for plugins" log line. Includes plugin elapsed
/// time + each active subtask's label + elapsed, so a stuck daemon is
/// diagnosable from a single log line.
///
/// Example output: `test-prune (25m 12s) [Intelligence.Tests.Unit 12m 3s, Intelligence.Tests.Database 10m 1s]`
let formatPluginWait
    (now: System.DateTime)
    (pluginName: string)
    (since: System.DateTime)
    (subtasks: (string * System.DateTime) list)
    : string =
    let elapsed = formatElapsed (now - since)

    let subtaskPart =
        match subtasks with
        | [] -> ""
        | ts ->
            let joined =
                ts
                |> List.map (fun (label, startedAt) -> $"%s{label} %s{formatElapsed (now - startedAt)}")
                |> String.concat ", "

            $" [%s{joined}]"

    $"%s{pluginName} (%s{elapsed}){subtaskPart}"

/// Quiescence window applied after the last host activity (event dispatch or
/// plugin status transition) before WaitForComplete declares the host idle.
/// Picks up the "plugin transitioned through Idle between cycles" race: a
/// plugin that's about to start a new cycle in response to a freshly-emitted
/// event won't have updated its status yet, so the waiter must give the
/// dispatch pipeline a chance to land before returning.
let internal waitForAllTerminalQuiescenceWindow =
    System.TimeSpan.FromMilliseconds(200.0)

/// Wait for all plugins to settle. A plugin "settles" when:
///   1. It's in a non-Running status (Idle / Completed / Failed) AND its
///      work-cycle generation has advanced past the snapshot taken at call
///      time — i.e. it has actually completed at least one cycle since we
///      started waiting; OR
///   2. It's been quiet through a 200ms quiescence window measured from the
///      most recent host activity (event dispatch or status change). This
///      handles plugins that legitimately have no work to do during this
///      cycle (don't subscribe to a relevant event), and makes the wait
///      bounded when nothing is happening.
///
/// The quiescence window also closes the race that motivated this design:
/// without it, WaitForComplete could observe `allTerminal=true` in the brief
/// window between a plugin emitting BuildCompleted, transitioning Completed,
/// and a downstream plugin's mailbox actually picking up the BuildCompleted
/// event and transitioning into Running. Times out with TimeoutException
/// after the specified timeout.
let internal waitForAllTerminal (host: PluginHost) (timeout: System.TimeSpan) () : Task<unit> =
    // TimeSpan.MaxValue signals "no timeout"; adding it to UtcNow overflows, so skip
    // deadline computation entirely in that case and rely on the MaxValue guard in loop.
    let deadline =
        if timeout = System.TimeSpan.MaxValue then
            System.DateTime.MaxValue
        else
            System.DateTime.UtcNow + timeout

    let mutable lastLogTime = System.DateTime.UtcNow

    // Snapshot per-plugin generations at call time. A plugin satisfies the
    // "advanced a generation" leg of the wait condition once its current
    // generation exceeds the snapshot value AND it's in a non-Running status.
    // Plugins registered after the snapshot default to 0, which any later
    // Idle->Running transition will exceed.
    let snapshotGenerations = host.WorkCycleGenerations()

    let generationOf (name: string) (gens: Map<string, int64>) =
        Map.tryFind name gens |> Option.defaultValue 0L

    let getRunningPlugins () =
        let now = System.DateTime.UtcNow

        host.GetAllStatuses()
        |> Map.toList
        |> List.choose (fun (name, s) ->
            match s with
            | Running since ->
                let subtasks =
                    host.GetActivitySnapshot(name).Subtasks
                    |> List.map (fun t -> t.Key, t.StartedAt)

                Some(formatPluginWait now name since subtasks)
            | _ -> None)

    let logRunningPlugins () =
        let now = System.DateTime.UtcNow

        if (now - lastLogTime).TotalSeconds >= 10.0 then
            lastLogTime <- now

            match getRunningPlugins () with
            | [] -> Logging.info "wait" "All plugins terminal, waiting for quiescence..."
            | plugins ->
                let joined = plugins |> String.concat ", "
                Logging.info "wait" $"Waiting for plugins: %s{joined}"

    let formatTimeoutDetail () =
        match getRunningPlugins () with
        | [] -> "all terminal but quiescence check failed"
        | running ->
            let joined = running |> String.concat ", "
            $"still running: %s{joined}"

    let isQuiescent () =
        System.DateTime.UtcNow - host.LastActivityAt()
        >= waitForAllTerminalQuiescenceWindow

    let allPluginsAdvancedToTerminal () =
        let statuses = host.GetAllStatuses()
        let currentGens = host.WorkCycleGenerations()

        not statuses.IsEmpty
        // Even when every plugin has reached terminal AND its generation has
        // advanced past the snapshot, a downstream plugin can still have an
        // event queued in its mailbox (or be inside a handler that hasn'''t yet
        // returned). The BuildCompleted -> TestPrune.PendingRerun -> Running
        // edge is the canonical case: BuildCompleted is dispatched
        // (inflight=1) while TestPrune is still showing Completed from the
        // prior FileChecked cycle. Without this gate the wait would resolve
        // in that window.
        && not (host.AnyPluginBusy())
        && statuses
           |> Map.forall (fun name s ->
               match s with
               | Completed _
               | Failed _ ->
                   let snap = generationOf name snapshotGenerations
                   let cur = generationOf name currentGens
                   // Plugin must have completed a cycle DURING this wait.
                   // For plugins already terminal at snapshot with the same
                   // generation, that means no work happened — fall back to
                   // quiescence in the caller.
                   cur > snap
               | _ -> false)

    let allPluginsAtRest () =
        // Conservative quiescence-based completion: no plugin is Running, no
        // plugin has events still inflight (queued or being processed by its
        // mailbox), and no host-level activity has happened in the quiescence
        // window. Together these prove there's no work in flight that we could
        // miss by returning now.
        let statuses = host.GetAllStatuses()

        not statuses.IsEmpty
        && not (host.AnyPluginBusy())
        && statuses
           |> Map.forall (fun _ s ->
               match s with
               | Running _ -> false
               | _ -> true)
        && isQuiescent ()

    let rec loop () =
        async {
            if timeout <> System.TimeSpan.MaxValue && System.DateTime.UtcNow >= deadline then
                let detail = formatTimeoutDetail ()

                raise (System.TimeoutException($"WaitForComplete timed out after %O{timeout} — %s{detail}"))

            // Two satisfaction paths:
            //   1. Every plugin started a new cycle since the snapshot AND has
            //      reached terminal — clearly all the work triggered while we
            //      were waiting has completed.
            //   2. No plugin is Running, no plugin has inflight events, and the
            //      host has been quiet for the quiescence window. This handles
            //      plugins that legitimately have nothing to do this cycle, and
            //      bounds the wait when nothing is happening.
            if allPluginsAdvancedToTerminal () || allPluginsAtRest () then
                return ()
            else
                logRunningPlugins ()
                do! Async.Sleep 50
                return! loop ()
        }

    loop () |> Async.StartAsTask

/// Wait for a single named plugin to leave Running. Returns immediately if the
/// plugin is not registered or is already terminal. Polling-based; bounded by
/// `timeout`. Used by `performScan` to serialize "BuildPlugin completes"
/// before "FCS tier checks begin" on cold scans.
///
/// Why this exists: cold scans emit `FileChanged(SourceChanged files)` to all
/// subscribers, which kicks BuildPlugin into running `dotnet build`. MSBuild
/// then rewrites `obj/Debug/.../ref/*.dll`. If FCS tier checks run in parallel,
/// they read those same `-r:` paths mid-write, producing the spurious
/// "type 'X' does not match type 'X'" CCU-mismatch diagnostics. Awaiting the
/// build's terminal status before FCS reads stabilizes the obj/ tree.
///
/// Phase B (warm cache) preservation: BuildPlugin's task-cache replay path
/// emits `BuildCompleted` synchronously inside the captured-events handler,
/// so when the cache hits this wait completes in milliseconds.
/// Polling implementation parameterised on a status-reader so unit tests can
/// drive deterministic state sequences without Task.Delay. The PluginHost
/// adapter below is the production caller.
let internal waitForPluginTerminalIfRunningWith
    (getStatus: string -> PluginStatus option)
    (pluginName: string)
    (timeout: System.TimeSpan)
    : Async<unit> =
    async {
        let deadline =
            if timeout = System.TimeSpan.MaxValue then
                System.DateTime.MaxValue
            else
                System.DateTime.UtcNow + timeout

        let isRunning () =
            match getStatus pluginName with
            | Some(Running _) -> true
            | _ -> false

        // EmitFileChanged dispatches to mailboxes synchronously but plugins
        // transition to Running asynchronously when their handler runs. Give
        // the dispatch a brief settle window so we don't observe the plugin
        // as "Idle" right before it enters Running.
        let settleDeadline =
            System.DateTime.UtcNow + System.TimeSpan.FromMilliseconds(200.0)

        while not (isRunning ()) && System.DateTime.UtcNow < settleDeadline do
            do! Async.Sleep 25

        // If the plugin never entered Running (not registered, or finished
        // before we polled), there's nothing to wait for.
        if not (isRunning ()) then
            return ()
        else
            let mutable lastLogTime = System.DateTime.UtcNow

            while isRunning ()
                  && (timeout = System.TimeSpan.MaxValue || System.DateTime.UtcNow < deadline) do
                let now = System.DateTime.UtcNow

                if (now - lastLogTime).TotalSeconds >= 10.0 then
                    lastLogTime <- now
                    Logging.info "scan" $"Waiting for plugin '%s{pluginName}' to leave Running..."

                do! Async.Sleep 50

            if isRunning () then
                Logging.warn
                    "scan"
                    $"waitForPluginTerminalIfRunning: '%s{pluginName}' still Running after %O{timeout}; proceeding anyway"
    }

let internal waitForPluginTerminalIfRunning
    (host: PluginHost)
    (pluginName: string)
    (timeout: System.TimeSpan)
    : Async<unit> =
    waitForPluginTerminalIfRunningWith host.GetStatus pluginName timeout

/// The daemon ties together a warm FSharpChecker, file watcher, check pipeline, and plugin host.
/// It runs until the provided CancellationToken is cancelled.
type Daemon
    internal
    (
        host: PluginHost,
        watcher: FileWatcher,
        pipeline: CheckPipeline,
        graph: ProjectGraph,
        repoRoot: string,
        workspaceLoader: IWorkspaceLoader,
        scanAgent: ScanAgent,
        cancellationTokenRef: CancellationToken ref,
        ready: ManualResetEventSlim,
        scanSignal: ScanSignal,
        _fcsSuppressedCodes: Set<int>,
        lifetime: CancellationTokenSource,
        formatAllFn: (unit -> Async<string>) option,
        excludePatterns: string list,
        idleExitMin: int option
    ) =

    let mutable disposed = false

    // Per-daemon process registry. Installed as AsyncLocal current on construction
    // so plugin-spawned children (test runners, playwright drivers, etc.) register
    // against this daemon's registry. Dispose kills everything still tracked —
    // that's how `dotnet fshw stop` reaps in-flight test runners.
    // The install IDisposable is intentionally discarded: the daemon owns this
    // registry for its full lifetime.
    let processRegistry = ProcessRegistry.Registry()
    do ProcessRegistry.install processRegistry |> ignore

    /// The plugin host that manages plugin lifecycle and event dispatch.
    member _.Host = host

    member internal _.ProcessRegistry = processRegistry

    /// The check pipeline that performs incremental file checking.
    member _.Pipeline = pipeline

    /// The project dependency graph.
    member _.Graph = graph

    /// The repository root directory.
    member _.RepoRoot = repoRoot

    /// Signalled when the daemon is ready to accept file change events.
    member _.Ready = ready

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                // Call directly on the daemon's own registry rather than the
                // AsyncLocal current one — Dispose may run from a different
                // async context than the one that installed it.
                processRegistry.KillAll()
                lifetime.Cancel()
                lifetime.Dispose()
                (watcher :> IDisposable).Dispose()

    /// Register a declarative framework-managed plugin handler.
    member _.RegisterHandler<'State, 'Msg>(handler: PluginFramework.PluginHandler<'State, 'Msg>) =
        host.RegisterHandler(handler)

    /// Register a preprocessor (e.g., formatter) that runs before events are dispatched.
    member _.RegisterPreprocessor(preprocessor: IFsHotWatchPreprocessor) = host.RegisterPreprocessor(preprocessor)

    /// Register a project's options so its files can be checked incrementally.
    member _.RegisterProject(projectPath: string, options: FSharpProjectOptions) =
        pipeline.RegisterProject(projectPath, options)

    /// Get current scan state.
    member _.GetScanState() = getScanStatus scanAgent

    /// Get current scan generation (incremented after each completed scan).
    member _.GetScanGeneration() = getScanGeneration scanAgent

    /// Set scan state (internal, for testing).
    member internal _.SetScanState(state: ScanState) = setScanStatus scanAgent state

    /// Scan all registered files — check each one and emit events to plugins.
    /// Blocks until complete. If a scan is already running, waits for it to finish.
    member _.ScanAll() =
        async {
            let! ct = Async.CancellationToken

            do! requestScan scanAgent ct
        }

    /// Run a single full scan in-process without watcher or IPC.
    /// Discovers projects, scans all files, waits for plugins to complete, returns statuses.
    member this.RunOnce() =
        async {
            do! this.ScanAll()

            do!
                waitForAllTerminal host (System.TimeSpan.FromMinutes(30.0)) ()
                |> Async.AwaitTask

            return host.GetAllStatuses()
        }

    /// Run the daemon until cancellation is requested.
    member this.Run(cancellationToken: CancellationToken) =
        async {
            ready.Set()

            try
                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()

                use _reg = cancellationToken.Register(fun () -> tcs.TrySetResult() |> ignore)

                do! tcs.Task |> Async.AwaitTask
            finally
                ready.Dispose()
                (this :> IDisposable).Dispose()
        }

    /// Discover .fsproj files in src/ and tests/ and register them with the pipeline.
    member _.DiscoverAndRegisterProjects() =
        discoverAndRegisterProjects repoRoot workspaceLoader graph pipeline excludePatterns true

    /// Format scan state as a human-readable string.
    member _.FormatScanStatus() =
        let scanState = getScanStatus scanAgent

        match scanState with
        | ScanIdle -> "idle"
        | Scanning(total, completed, _) ->
            let pct = if total > 0 then completed * 100 / total else 0
            $"scanning: %d{completed}/%d{total} files (%d{pct}%%)"
        | ScanComplete(total, unchecked, elapsed) ->
            if unchecked > 0 then
                // Non-ok: a truncated scan must not present as clean. The
                // checked count is (total - unchecked) so the discrepancy is
                // explicit to an agent/user reading status or check output.
                $"incomplete: %d{total - unchecked} files checked, %d{unchecked} unchecked in %.1f{elapsed.TotalSeconds}s"
            else
                $"complete: %d{total} files checked in %.1f{elapsed.TotalSeconds}s"

    /// Run the daemon with IPC server on the given pipe name.
    /// Discovers projects, performs initial scan, then watches for changes.
    member this.RunWithIpc(pipeName: string, cts: CancellationTokenSource) =
        async {
            try
                let onScan () =
                    Async.StartAsTask(this.ScanAll()) |> ignore

                let triggerBuild () =
                    async {
                        let files = pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value

                        if not files.IsEmpty then
                            host.EmitFileChanged(SourceChanged files)
                    }

                let formatAll () =
                    match formatAllFn with
                    | Some fn -> fn ()
                    | None ->
                        async {
                            let files = pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value

                            let modified = host.RunPreprocessors(files)
                            return $"formatted %d{modified.Length} files"
                        }

                let rerunPlugin (name: string) =
                    async { return host.RerunFileCommandPlugin(name) }

                let rpcConfig: DaemonRpcConfig =
                    { Host = host
                      RequestShutdown = fun () -> cts.Cancel()
                      RequestScan = onScan
                      GetScanStatus = this.FormatScanStatus
                      GetScanGeneration = this.GetScanGeneration
                      TriggerBuild = triggerBuild
                      FormatAll = formatAll
                      WaitForScanGeneration =
                        fun afterGen -> scanSignal.WaitForGeneration(afterGen, this.GetScanGeneration())
                      WaitForAllTerminal = fun timeout -> waitForAllTerminal host timeout ()
                      RerunPlugin = rerunPlugin }

                let ipcTask = Async.StartAsTask(IpcServer.start pipeName rpcConfig cts)

                // Idle-exit scheduler. When a threshold is configured, arm a 30s
                // timer that gracefully shuts the daemon down (the SAME
                // `cts.Cancel()` path the IPC Shutdown request uses — see
                // `rpcConfig.RequestShutdown`) once the daemon has been idle for
                // the window. Cancelling `cts` unblocks the wait below, runs this
                // method's `finally` (Dispose: KillAll + lifetime.Cancel +
                // watcher dispose), and lets the CLI clean up the pidfile — a
                // fully graceful exit, no `Environment.Exit`. The decision logic
                // lives in the pure, unit-tested `IdleExit` module; here we inject
                // the live clock/busy/activity + the shutdown action. The next
                // `fshw` command auto-restarts the daemon, so this is transparent.
                use _idleExitTimer: IDisposable =
                    match idleExitMin with
                    | Some minutes when minutes > 0 ->
                        let deps: IdleExit.IdleExitDeps =
                            { Threshold = System.TimeSpan.FromMinutes(float minutes)
                              Now = fun () -> System.DateTime.UtcNow
                              Busy = host.AnyPluginBusy
                              LastActivityAt = host.LastActivityAt
                              Shutdown = fun () -> cts.Cancel()
                              Log = fun message -> Logging.info "idle-exit" message }

                        IdleExit.createTimer deps :> IDisposable
                    | _ ->
                        // No-op disposable when idle-exit is off.
                        { new IDisposable with
                            member _.Dispose() = () }

                cancellationTokenRef.Value <- cts.Token
                ready.Set()

                // Register cancellation before starting the scan so that cancellation during
                // the initial scan unblocks RunWithIpc immediately rather than waiting for the
                // scan to complete. This prevents test-process hangs when cts is cancelled while
                // the scan is still running (e.g. under thread-pool contention in test suites).
                let tcs = System.Threading.Tasks.TaskCompletionSource<unit>()
                use _reg = cts.Token.Register(fun () -> tcs.TrySetResult() |> ignore)

                // Race against cancellation so a slow scan doesn't block shutdown.
                let scanTask = Async.StartAsTask(this.ScanAll())

                do!
                    [| scanTask :> System.Threading.Tasks.Task
                       tcs.Task :> System.Threading.Tasks.Task |]
                    |> System.Threading.Tasks.Task.WhenAny
                    |> Async.AwaitTask
                    |> Async.Ignore

                do! tcs.Task |> Async.AwaitTask

                try
                    ipcTask.Wait(System.TimeSpan.FromSeconds(1.0)) |> ignore
                with ex ->
                    Logging.debug "daemon" $"IPC shutdown: %s{ex.Message}"
            finally
                ready.Dispose()
                (this :> IDisposable).Dispose()
        }

/// Drive per-file checks with a bounded retry on `None` results.
///
/// Why this exists — the cold-scan silent-truncation race: while the initial
/// scan runs its FCS tiers, the BuildPlugin's `dotnet build` touches
/// `obj/**/ref/*.dll`; the watcher fires, `processBatch` re-checks the affected
/// files, and `CancelPreviousCheck` cancels the scan-side in-flight check of the
/// same file. A cancelled check surfaces as `None` from the pipeline, and the
/// scan emit loop used to silently drop `None` (`| None -> ()`) — so a scan
/// could report green while the ErrorLedger never saw diagnostics for the
/// dropped files (neither reported NOR cleared).
///
/// This helper closes that hole: files whose `check` returned `None` are
/// re-enqueued and re-checked, up to `maxRetries` additional rounds. The retry
/// calls the SAME `check` thunk, which (via `CheckFile`/`CheckFileWithOptions`
/// → `CancelPreviousCheck`) re-reads current disk content — so a NEWER user edit
/// that legitimately superseded the in-flight check is observed on retry rather
/// than duplicated, preserving CancelPreviousCheck's ordering guarantee. Each
/// file is emitted at most once (only when its check returns `Some`).
///
/// Returns the number of files STILL unchecked after the budget is exhausted
/// (a file under continuous concurrent edit, or a hard failure). The caller
/// must surface a non-zero count as a non-ok scan condition — see `performScan`
/// / `ScanComplete` — so a truncated scan cannot be read as clean.
///
/// Pure of FCS and disk: `check` is injected, making the retry/convergence and
/// honest-completion invariants unit-testable at the narrowest seam.
let internal runChecksWithRetry
    (maxRetries: int)
    (check: AbsFilePath -> Async<'r option>)
    (emit: 'r -> unit)
    (files: AbsFilePath list)
    : Async<int> =
    async {
        let mutable pending = files
        let mutable round = 0

        // Initial pass plus up to `maxRetries` retry rounds over the residual
        // `None` set. Converges (pending shrinks or we hit the budget).
        while not pending.IsEmpty && round <= maxRetries do
            let! results =
                pending
                |> List.map (fun file ->
                    async {
                        let! r = check file
                        return file, r
                    })
                |> Async.Parallel

            let mutable stillPending = []

            for file, result in results do
                match result with
                | Some r -> emit r
                | None -> stillPending <- file :: stillPending

            pending <- List.rev stillPending
            round <- round + 1

        return pending.Length
    }

/// Execute the full scan logic, returning the updated agent state.
let private performScan (ctx: BatchContext) (scanSignal: ScanSignal) (state: ScanAgentState) (ct: CancellationToken) =
    async {
        let host = ctx.Host
        let pipeline = ctx.Pipeline
        let graph = ctx.Graph

        // Re-discover projects before scanning so that removed files/projects
        // are cleared before results are returned to the client. Without this,
        // a concurrent processChanges re-discovery (triggered by the file watcher
        // with debounce delay) may complete AFTER the scan signals waiters,
        // leaving stale FCS errors visible for one cycle.
        // Guarded by fsproj fingerprint to skip expensive MSBuild evaluation
        // when no project files have changed.
        let currentFingerprint = fingerprintFsprojFiles ctx.RepoRoot
        let mutable lastFingerprint = state.LastFingerprint

        if currentFingerprint <> state.LastFingerprint then
            let! _ =
                rediscoverAndClearRemoved ctx.RepoRoot ctx.Loader graph pipeline host "scan" ctx.ExcludePatterns true

            lastFingerprint <- currentFingerprint

        let registeredProjects = pipeline.GetRegisteredProjects()

        let files = pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value

        let total = files.Length
        Logging.info "scan" $"%d{registeredProjects.Length} projects, %d{total} files registered"
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let scanStartedAt = System.DateTime.UtcNow
        let mutable scanState: ScanState = Scanning(total, 0, scanStartedAt)
        let dispatchedFiles = ResizeArray<AbsFilePath>()
        // Files whose check never returned Some, even after the bounded
        // scan-retry budget (the silent-truncation race: a scan-side check
        // cancelled by processBatch's same-file re-check, or a hard failure).
        // Surfaced in the scan-complete state and log so a truncated scan can
        // never read as clean. See `runChecksWithRetry`.
        let mutable uncheckedCount = 0

        if not files.IsEmpty then
            // Run preprocessors (e.g., formatter) before dispatching
            let modified = host.RunPreprocessors(files)

            if modified.Length > 0 then
                Logging.info "scan" $"Preprocessors modified %d{modified.Length} files (watcher may re-trigger)"

            host.EmitFileChanged(SourceChanged files)

            // Serialize: wait for BuildPlugin to leave Running BEFORE running
            // FCS check tiers. The build refreshes obj/Debug/.../ref/*.dll
            // which FCS reads via `-r:` flags from FSharpProjectOptions.
            // Without this wait, MSBuild and FCS race over the same files
            // and FCS produces spurious "type 'X' does not match type 'X'"
            // CCU-identity diagnostics on cold scans where stale obj/ ref
            // dlls are concurrently rewritten under FCS's read.
            //
            // No-op when BuildPlugin is not registered or already terminal;
            // millisecond-scale when BuildPlugin's task-cache replay path
            // emits BuildCompleted synchronously (Phase B / warm cache).
            do! waitForPluginTerminalIfRunning host "build" (System.TimeSpan.FromMinutes(10.0))

            let mutable completed = 0

            let mutable checkedCount = 0
            let mutable skippedCount = 0

            let filesToCheckSet = Set.ofList files

            // Check files in parallel tiers based on project dependency graph
            let tiers = graph.GetParallelTiers()

            // Bounded retry budget for cancelled/aborted/failed scan checks.
            // The common case (a single processBatch race per file) converges
            // in one retry; the budget caps work for files under continuous
            // concurrent edit, which are then honestly reported as unchecked.
            let scanRetryBudget = 3

            for tier in tiers do
                // Resolve each checkable file in the tier to its check thunk,
                // honouring the deps-freshness gate and per-project options.
                // The thunk is re-runnable (an Async description), so
                // runChecksWithRetry can re-invoke it on a cancelled result —
                // re-reading current disk content (invariant 3).
                let tierThunks =
                    System.Collections.Generic.Dictionary<AbsFilePath, Async<FileCheckResult option>>()

                for proj in tier do
                    let projPath = AbsProjectPath.value proj

                    let projFiles =
                        graph.GetSourceFiles(proj)
                        |> List.map AbsFilePath.value
                        |> List.filter filesToCheckSet.Contains

                    skippedCount <- skippedCount + ((graph.GetSourceFiles(proj) |> List.length) - projFiles.Length)

                    // Deps-freshness gate: if this project's restored assets are
                    // stale, try a one-shot restore; if that fails, fail fast
                    // (one diagnostic) and skip FCS for the project so the
                    // phantom "namespace not found" storm is never produced.
                    if applyDepsGate ctx.DepsGate host projPath then
                        match pipeline.GetProjectOptions(projPath) with
                        | Some options ->
                            for file in projFiles do
                                let absFile = AbsFilePath.create file
                                tierThunks[absFile] <- pipeline.CheckFileWithOptions(absFile, options, ct)
                        | None ->
                            for file in projFiles do
                                let absFile = AbsFilePath.create file
                                tierThunks[absFile] <- pipeline.CheckFile(absFile, ct)
                    else
                        skippedCount <- skippedCount + projFiles.Length

                let tierFiles = tierThunks.Keys |> Seq.toList

                let emitChecked (checkResult: FileCheckResult) =
                    checkedCount <- checkedCount + 1
                    dispatchedFiles.Add(checkResult.File)
                    host.EmitFileChecked(checkResult)
                    reportFcsDiagnostics ctx.FcsSuppressedCodes host checkResult
                    completed <- completed + 1
                    scanState <- Scanning(total, completed, System.DateTime.UtcNow)

                let! tierUnchecked = runChecksWithRetry scanRetryBudget (fun f -> tierThunks[f]) emitChecked tierFiles

                uncheckedCount <- uncheckedCount + tierUnchecked

            // Keep the existing "Checked N files (T tiers), skipped M" prefix
            // intact (external tooling greps it); append the unchecked count so
            // a truncated scan is never silently green in the log either.
            Logging.info
                "scan"
                $"Checked %d{checkedCount} files (%d{tiers.Length} tiers), skipped %d{skippedCount}, unchecked %d{uncheckedCount}"

        sw.Stop()
        let finalScanState = ScanComplete(total, uncheckedCount, sw.Elapsed)
        let newGeneration = state.Generation + 1L

        // Emit BatchChecked *before* SignalGeneration so WaitForScanGeneration
        // callers (IPC) safely assume BatchChecked has already been dispatched
        // by the time `fshw scan --wait` returns. Empty cohorts (no registered
        // files) skip — there's nothing to "flush and decide" against.
        if dispatchedFiles.Count > 0 then
            host.EmitBatchChecked
                { Trigger = BootScan
                  Files = dispatchedFiles |> List.ofSeq
                  Generation = newGeneration
                  StartedAt = scanStartedAt
                  CompletedAt = System.DateTime.UtcNow }

        scanSignal.SignalGeneration(newGeneration)

        return
            { ScanState = finalScanState
              Generation = newGeneration
              LastFingerprint = lastFingerprint }
    }

/// Functions for creating and managing daemons.
module Daemon =
    let private sourceDebounceMs = 500
    let private projectDebounceMs = 200

    /// Options controlling daemon construction. Callers use `DaemonOptions.defaults`
    /// and modify only what they need.
    [<NoComparison; NoEquality>]
    type DaemonOptions =
        {
            CacheBackend: ICheckCacheBackend option
            CacheKeyProvider: ICacheKeyProvider option
            /// FCS diagnostic codes to suppress. `None` means the default suppression set.
            /// FCS diagnostic codes to suppress globally. `None` means no
            /// daemon-level suppression — projects opt in via `<NoWarn>` in
            /// their fsproj or `#nowarn "code"` in source.
            FcsSuppressedCodes: int list option
            /// `PathFilter.isExcludedPath` patterns applied during project discovery.
            ExcludePatterns: string list
            /// Extra file patterns from FileCommandPlugin configs that the watcher
            /// should monitor beyond the default F# source/project set.
            ExtraWatchPatterns: FilePattern list
            /// Resolved idle-exit threshold in minutes. `Some n` arms a 30s timer
            /// that gracefully shuts the daemon down after `n` minutes of
            /// idleness (no events, no running work); the next `fshw` command
            /// auto-restarts. `None` (the default) disables it — no timer is
            /// created. Resolution from the `idleExitMin` config + repo path is
            /// done by the caller (`IdleExit.resolveThreshold`).
            IdleExitMin: int option
        }

    module DaemonOptions =
        let defaults: DaemonOptions =
            { CacheBackend = None
              CacheKeyProvider = None
              FcsSuppressedCodes = None
              ExcludePatterns = []
              ExtraWatchPatterns = []
              IdleExitMin = None }

    /// Resolve the configured FCS-suppression option to the runtime `Set<int>`.
    /// `None` resolves to `Set.empty` — fshw deliberately ships no built-in
    /// suppressions. Projects that need to silence a code declare it via
    /// `<NoWarn>` in the fsproj or `#nowarn "code"` in source.
    let resolveFcsSuppressedCodes (configured: int list option) : Set<int> =
        configured |> Option.defaultValue [] |> Set.ofList

    /// Create a daemon with the given checker (internal, for testing).
    let internal createWith (checker: FSharpChecker) (repoRoot: string) (opts: DaemonOptions) =
        let cacheBackend = opts.CacheBackend
        let cacheKeyProvider = opts.CacheKeyProvider

        let fcsSuppressedCodes = resolveFcsSuppressedCodes opts.FcsSuppressedCodes

        let excludePatterns = opts.ExcludePatterns
        let extraWatchPatterns = opts.ExtraWatchPatterns
        let lifetime = new CancellationTokenSource()

        try
            let errorDir = Path.Combine(FsHwPaths.root repoRoot, "errors")

            let fileReporter: IErrorReporter =
                FsHotWatch.FileErrorReporter.FileErrorReporter(errorDir)

            fileReporter.ClearAll()
            let taskCacheDir = Path.Combine(FsHwPaths.root repoRoot, "cache", "tasks")
            let taskCache = FsHotWatch.FileTaskCache.FileTaskCache(taskCacheDir)
            // §2c telemetry: log cache size on startup so we can pick LRU thresholds from data.
            let stats = taskCache.Stats

            Logging.info
                "task-cache"
                $"On-disk cache: %d{stats.EntryCount} entries, %.1f{float stats.SizeBytes / 1024.0 / 1024.0} MB"

            let host =
                PluginHost(
                    checker,
                    repoRoot,
                    reporters = [ fileReporter ],
                    taskCache = taskCache,
                    fcsSuppressedCodes = fcsSuppressedCodes
                )

            let fcsSink = host.ActivitySinkFor(PluginActivity.FcsPluginName)

            let pipeline =
                match cacheBackend, cacheKeyProvider with
                | Some b, Some kp -> CheckPipeline(checker, cacheBackend = b, cacheKeyProvider = kp, activity = fcsSink)
                | Some b, None -> CheckPipeline(checker, cacheBackend = b, activity = fcsSink)
                | _ -> CheckPipeline(checker, activity = fcsSink)

            let graph = ProjectGraph()
            let toolsPath = Init.init (DirectoryInfo(repoRoot)) None
            let loader = WorkspaceLoader.Create(toolsPath, [])

            let daemonCtRef = ref CancellationToken.None

            let delayForChange change =
                match change with
                | ProjectChanged _
                | SolutionChanged -> projectDebounceMs
                | SourceChanged _ -> sourceDebounceMs

            let batchCtx: BatchContext =
                { Host = host
                  InvalidateFcs =
                    if isNull (box checker) then
                        None
                    else
                        Some(fun () ->
                            checker.InvalidateAll()
                            checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients())
                  InvalidateFcsForProjects =
                    if isNull (box checker) then
                        None
                    else
                        Some(fun optsList ->
                            // Per-project invalidation only. No global
                            // ClearLanguageServiceRootCaches — that GC is what
                            // makes the full path cold; scoping is the point.
                            for opts in optsList do
                                checker.InvalidateConfiguration(opts))
                  RepoRoot = repoRoot
                  Loader = loader
                  Graph = graph
                  Pipeline = pipeline
                  DaemonCt = daemonCtRef
                  FcsSuppressedCodes = fcsSuppressedCodes
                  ExcludePatterns = excludePatterns
                  InSessionBatchGen = ref 0L
                  DepsGate =
                    if isNull (box checker) then
                        // No FCS analysis happens with a null checker (test
                        // daemons), so the gate would have nothing to guard.
                        None
                    else
                        let tracker = DepsFreshness.RecoveryTracker()
                        let runner = DepsFreshness.productionRestoreRunner repoRoot

                        Some(fun projPath ->
                            DepsFreshness.evaluateProject
                                (DepsFreshness.detectProjectFreshness repoRoot)
                                (DepsFreshness.staleSignature repoRoot)
                                DepsFreshness.assetsPresent
                                runner
                                tracker
                                projPath) }

            let formatAllAndSuppress (suppressed: Set<string>) (replyChannel: AsyncReplyChannel<string>) =
                let files = pipeline.GetAllRegisteredFiles() |> List.map AbsFilePath.value

                let modified = host.RunPreprocessors(files)
                let newSuppressed = Set.union suppressed (Set.ofList modified)
                replyChannel.Reply($"formatted %d{modified.Length} files")
                newSuppressed

            let changeAgent =
                MailboxProcessor<Choice<FileChangeKind, AsyncReplyChannel<string>>>
                    .Start(
                        (fun inbox ->
                            let rec idle (suppressed: Set<string>) =
                                async {
                                    let! msg = inbox.Receive()

                                    match msg with
                                    | Choice2Of2 replyChannel ->
                                        let newSuppressed = formatAllAndSuppress suppressed replyChannel
                                        return! idle newSuppressed
                                    | Choice1Of2 change ->
                                        let delayMs = delayForChange change
                                        return! debouncing [ change ] delayMs suppressed
                                }

                            and debouncing (pending: FileChangeKind list) (delayMs: int) (suppressed: Set<string>) =
                                async {
                                    let! msg = inbox.TryReceive(delayMs)

                                    match msg with
                                    | Some(Choice1Of2 change) ->
                                        let newDelay = max delayMs (delayForChange change)
                                        return! debouncing (change :: pending) newDelay suppressed
                                    | Some(Choice2Of2 replyChannel) ->
                                        // F13 (audit 2026-05-02): policy hoisted to runDaemonStep —
                                        // OperationCanceledException propagates; everything else is
                                        // logged with full stack and we fall back to idle so the
                                        // daemon stays responsive while the user sees the failure.
                                        match!
                                            runDaemonStep
                                                "processChanges (with replyChannel)"
                                                (processBatch batchCtx (List.rev pending) suppressed)
                                        with
                                        | Ok newSuppressed ->
                                            let finalSuppressed = formatAllAndSuppress newSuppressed replyChannel
                                            return! idle finalSuppressed
                                        | Result.Error _ ->
                                            replyChannel.Reply("format failed")
                                            return! idle suppressed
                                    | None ->
                                        // Debounce expired — process batch (F13: see runDaemonStep).
                                        match!
                                            runDaemonStep
                                                "processChanges"
                                                (processBatch batchCtx (List.rev pending) suppressed)
                                        with
                                        | Ok newSuppressed -> return! idle newSuppressed
                                        | Result.Error _ -> return! idle suppressed
                                }

                            idle Set.empty),
                        cancellationToken = lifetime.Token
                    )

            let onChange change =
                Logging.debug "watcher" $"%O{change}"
                changeAgent.Post(Choice1Of2 change)

            let watcher = FileWatcher.create repoRoot onChange None extraWatchPatterns

            let scanSignal = ScanSignal(cancellationToken = lifetime.Token)

            let scanMailbox =
                MailboxProcessor.Start(
                    (fun inbox ->
                        let rec loop (state: ScanAgentState) =
                            async {
                                let! msg = inbox.Receive()

                                match msg with
                                | RequestScan(ct, reply) ->
                                    // F13 (audit 2026-05-02): policy hoisted to runDaemonStep — see
                                    // its docblock for the broad-catch justification at this
                                    // boundary (FCS / MSBuild / plugin Update surface).
                                    match! runDaemonStep "performScan" (performScan batchCtx scanSignal state ct) with
                                    | Ok newState ->
                                        reply.Reply(())
                                        return! loop newState
                                    | Result.Error _ ->
                                        reply.Reply(())
                                        return! loop state
                                | GetState reply ->
                                    reply.Reply(state.ScanState)
                                    return! loop state
                                | GetGeneration reply ->
                                    reply.Reply(state.Generation)
                                    return! loop state
                                | SetState(newScanState, reply) ->
                                    reply.Reply(())
                                    return! loop { state with ScanState = newScanState }
                            }

                        loop
                            { ScanState = ScanIdle
                              Generation = 0L
                              LastFingerprint = Set.empty }),
                    cancellationToken = lifetime.Token
                )

            let scanAgentWrapper = ScanAgent scanMailbox

            let formatAllViaAgent () =
                changeAgent.PostAndAsyncReply(Choice2Of2)

            new Daemon(
                host,
                watcher,
                pipeline,
                graph,
                repoRoot,
                loader,
                scanAgentWrapper,
                daemonCtRef,
                new ManualResetEventSlim(false),
                scanSignal,
                fcsSuppressedCodes,
                lifetime,
                Some formatAllViaAgent,
                excludePatterns,
                opts.IdleExitMin
            )
        with _ ->
            lifetime.Dispose()
            reraise ()

    /// Create a new daemon for the given repository root with a warm FSharpChecker.
    /// Pass `DaemonOptions.defaults` and override only the fields you need.
    let create (repoRoot: string) (opts: DaemonOptions) =
        let checker =
            FSharpChecker.Create(
                keepAssemblyContents = true,
                keepAllBackgroundResolutions = true,
                parallelReferenceResolution = true,
                useTransparentCompiler = true
            )

        createWith checker repoRoot opts
