module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.Lifecycle
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

/// Per-project coverage artifact paths + the command-line template used to
/// produce them. The plugin writes Cobertura XML to Baseline (full run) or
/// Partial (impact-filtered run), then merges into the Cobertura XML consumed
/// by downstream tooling (coverageratchet). Callers (DaemonConfig) decide the
/// directory layout and the arg template; the plugin treats the paths as
/// opaque absolute paths and substitutes `{output}` in `ArgsTemplate`.
///
/// The file format is Cobertura regardless of `ArgsTemplate` — the template
/// is responsible for telling its runner to write Cobertura to `{output}`.
/// For Microsoft Testing Platform, use `defaultCoverageArgsTemplate`; for
/// other runners (coverlet.collector, AltCover, OpenCover) supply your own.
type CoveragePaths =
    { Baseline: string
      Partial: string
      Cobertura: string
      ArgsTemplate: string }

/// Default coverage args template for Microsoft Testing Platform hosts
/// (xUnit v3, MSTest v3 — anything invoked as `dotnet run --project <test>
/// --no-build -- ...`). `{output}` is replaced with the target file path.
[<Literal>]
let defaultCoverageArgsTemplate =
    "--coverage --coverage-output-format cobertura --coverage-output \"{output}\""

[<Literal>]
let private OutputPlaceholder = "{output}"

/// Substitute `{output}` in `paths.ArgsTemplate` with either Baseline or
/// Partial depending on `wasFiltered`. Creates the output dir if missing.
/// Raises with a clear message if the template is missing the placeholder —
/// silent emission of broken args is the bug we just fixed.
let buildCoverageArgs (paths: CoveragePaths) (wasFiltered: bool) : string =
    let target = if wasFiltered then paths.Partial else paths.Baseline

    let dir = Path.GetDirectoryName(target)

    if not (String.IsNullOrEmpty dir) then
        Directory.CreateDirectory(dir) |> ignore

    if not (paths.ArgsTemplate.Contains(OutputPlaceholder)) then
        invalidArg
            "ArgsTemplate"
            (sprintf "coverage args template must contain %s placeholder; got %A" OutputPlaceholder paths.ArgsTemplate)

    paths.ArgsTemplate.Replace(OutputPlaceholder, target)

/// After a test run completes, collapse the coverlet JSON output into a
/// Cobertura document downstream tools can read. Behavior depends on whether
/// this was a full or filtered run:
///
/// - Full run: the fresh baseline cobertura becomes the authoritative coverage
///   snapshot. Copy it to the final cobertura path and delete any stale
///   partial file so a subsequent filtered run starts clean.
/// - Filtered run, no baseline on disk: bootstrap — skip final cobertura entirely.
///   Downstream gating (coverageratchet) that reads the cobertura file will
///   see no file, which is the intended "no baseline yet" signal.
/// - Filtered run with baseline: merge per-line max, emit final cobertura. Keep
///   partial on disk for debugging.
let processCoverageOutput (paths: CoveragePaths) (wasFiltered: bool) : unit =
    // MTP's cobertura shape is stable but empty parsed data from a non-empty
    // file is the warning signal — emit a log so silent coverage collapse is
    // at least visible in the daemon log.
    let parseAndCheck path =
        let raw = File.ReadAllText(path)
        let data = CoverageMerge.parse raw

        if Map.isEmpty data && raw.Trim().Length > 0 then
            Logging.warn "test-prune" $"coverage: parsed 0 entries from %s{path} (cobertura schema drift?)"

        data

    try
        if not wasFiltered then
            if File.Exists(paths.Baseline) then
                let baseline = parseAndCheck paths.Baseline
                let xml = CoverageMerge.toCobertura baseline
                File.WriteAllText(paths.Cobertura, xml)

            if File.Exists(paths.Partial) then
                File.Delete(paths.Partial)
        else if not (File.Exists(paths.Baseline)) then
            // Bootstrap: no baseline yet, partial can't produce a faithful
            // Cobertura on its own. Leave everything as-is so downstream
            // ratchets skip (or fail with a clear "missing file" message
            // that prompts the user to run a full test).
            Logging.info "test-prune" "coverage: skipping cobertura emit (no baseline — run a full test first)"
        else if File.Exists(paths.Partial) then
            let baseline = parseAndCheck paths.Baseline
            let partial = parseAndCheck paths.Partial
            let merged = CoverageMerge.mergePerLineMax baseline partial
            let xml = CoverageMerge.toCobertura merged
            File.WriteAllText(paths.Cobertura, xml)
        else
            // Filtered run produced no partial (e.g. test skipped entirely);
            // nothing to merge. Leave existing cobertura alone.
            ()
    with ex ->
        Logging.error "test-prune" $"coverage post-processing failed: %s{ex.Message}"

/// Configuration for a test project to run.
type TestConfig =
    {
        Project: string
        Command: string
        Args: string
        Group: string
        Environment: (string * string) list
        /// Template for class-based test filtering. {classes} is replaced with
        /// the joined class names. Example: "-- --filter-class {classes}"
        FilterTemplate: string option
        /// Separator for joining class names in the filter. Default: " "
        /// Example: "|ClassName=" for dotnet test --filter "ClassName=A|ClassName=B"
        ClassJoin: string
        /// Per-project timeout in seconds. None → use top-level default.
        TimeoutSec: int option
    }

type RerunIntent =
    | NoRerun
    | RerunQueued

type TestRunPhase =
    | TestsIdle of Lifecycle<Idle, TestResults option>
    | TestsRunning of Lifecycle<Running, TestResults option> * RerunIntent

type AffectedTestsState =
    | NotYetAnalyzed
    | Analyzed of TestMethodInfo list

type TestPruneState =
    {
        PendingAnalysis: Map<string, AnalysisResult list>
        SymbolSnapshot: Map<string, SymbolInfo list>
        AffectedTests: AffectedTestsState
        ChangedSymbols: string list
        ChangedFiles: string list
        TestPhase: TestRunPhase
        /// Maps test class name → absolute source file path (built during FileChecked analysis).
        TestClassFiles: Map<string, string>
    }

/// Custom message posted from the async test runner back to the synchronous
/// Custom handler. Carries the lifecycle events (Started + Completed) so the
/// handler can emit them inside the framework's per-event capture window —
/// required for the §2a cache to record EmittedEvents on terminal status,
/// which `tryReplayCache` re-fires to downstream subscribers (FileCommandPlugin
/// keys off TestRunCompleted) when the cache hits.
///
/// Live `TestProgress` events still fire from the async (per-group, streaming)
/// because they're not part of cache replay (cache replay skips per-group
/// progress and goes straight from Started to Completed by design).
type TestPruneMsg = TestsFinished of started: TestRunStarted * completed: TestRunCompleted

let private formatTestResultsJson (results: TestResults) =
    let projects =
        results.Results
        |> Map.toList
        |> List.map (fun (name, result) ->
            let (status, output) =
                match result with
                | TestsPassed(o, _) -> ("passed", o)
                | TestsFailed(o, _) -> ("failed", o)
                | TestsTimedOut(o, _, _) -> ("timed-out", o)

            {| project = name
               status = status
               output = truncateOutput 200 output |})

    JsonSerializer.Serialize(
        {| elapsed = $"%.1f{results.Elapsed.TotalSeconds}s"
           projects = projects |}
    )

/// Build the filter arg string for a config given affected classes.
let internal buildFilterArgs (config: TestConfig) (classesByProject: Map<string, string list>) : string option =
    let classes =
        classesByProject |> Map.tryFind config.Project |> Option.defaultValue []

    match classes, config.FilterTemplate with
    | [], _ -> None
    | _, None ->
        Logging.debug "test-prune" $"No filterTemplate configured — running all tests for %s{config.Project}"
        None
    | classes, Some template ->
        let joined = classes |> String.concat config.ClassJoin
        let result = template.Replace("{classes}", joined)
        Logging.info "test-prune" $"Filter: %s{result}"
        Some result

/// Parse "failed Namespace.Class.Method (Xms)" lines from test output.
/// Returns (className, methodName, fullLine) tuples.
let parseFailedTests (output: string) : (string * string * string) list =
    output.Split('\n')
    |> Array.choose (fun line ->
        let trimmed = line.Trim()

        if trimmed.StartsWith("failed ") then
            // Strip "failed " prefix and optional trailing timing "(Xms)"
            let rest = trimmed.Substring(7).Trim()

            let name =
                match rest.LastIndexOf(" (") with
                | -1 -> rest
                | i -> rest.Substring(0, i)

            // Split qualified name: last segment is method, second-to-last is class
            let parts = name.Split('.')

            if parts.Length >= 2 then
                let methodName = parts.[parts.Length - 1]
                let className = parts.[parts.Length - 2]
                Some(className, methodName, trimmed)
            else
                Some(name, name, trimmed)
        else
            None)
    |> Array.toList

/// Report test failures to the error ledger grouped by source file.
/// Falls back to a synthetic "<tests>" path for tests without a known source file.
let private reportTestErrors (ctx: PluginCtx<TestPruneMsg>) (classFiles: Map<string, string>) (results: TestResults) =
    // Collect all failure entries grouped by file
    let entriesByFile =
        results.Results
        |> Map.toList
        |> List.collect (fun (project, result) ->
            match result with
            | TestsFailed(output, _)
            | TestsTimedOut(output, _, _) ->
                let parsed = parseFailedTests output

                if parsed.IsEmpty then
                    [ $"<tests/%s{project}>",
                      ErrorLedger.ErrorEntry.errorWithDetail $"Tests failed in %s{project}" output ]
                else
                    parsed
                    |> List.map (fun (className, _methodName, line) ->
                        let file =
                            classFiles
                            |> Map.tryFind className
                            |> Option.defaultValue $"<tests/%s{project}>"

                        file, ErrorLedger.ErrorEntry.errorWithDetail line output)
            | TestsPassed _ -> [])
        |> List.groupBy fst
        |> List.map (fun (file, entries) -> file, entries |> List.map snd)

    for (file, entries) in entriesByFile do
        ctx.ReportErrors file entries

/// Execute test configs with optional affected classes for filtering.
/// Handles beforeRun, coveragePaths, process execution, result storage.
/// rawFilter is a passthrough filter string (from run-tests command), bypassing the template.
///
/// Emission contract (when `ctx` is Some):
///   1. `TestRunStarted` once, before any group begins.
///   2. `TestProgress` once per group as it completes, carrying only that
///      group's projects as a delta.
///   3. `TestRunCompleted` once, after all groups finish, carrying the full
///      cumulative Results plus an Outcome.
/// All three share a single RunId generated at the start of the run.
/// When `ctx` is None (e.g. invoked from a one-off command), no lifecycle
/// events fire; the caller just gets back the final TestResults.
let private executeTests
    (ctx: PluginCtx<'msg> option)
    (repoRoot: string)
    (beforeRun: (unit -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (afterRun: (TestResults -> unit) option)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    (dirtyTracker: FsHotWatch.ProjectDirtyTracker.ProjectDirtyTracker option)
    =
    async {
        Logging.info "test-prune" $"executeTests starting with %d{configs.Length} configs"
        let sw = Stopwatch.StartNew()
        let runId = Guid.NewGuid()

        let isFilteredRun = not affectedClassesByProject.IsEmpty || Option.isSome rawFilter

        let primaryLabel =
            if isFilteredRun then
                $"running %d{configs.Length} selected test projects"
            else
                $"running full suite (%d{configs.Length} projects)"

        let startedAt = DateTime.UtcNow

        ctx |> Option.iter (fun c -> c.StartSubtask PrimarySubtaskKey primaryLabel)
        // EmitTestRunStarted moved to caller (so the synchronous Custom
        // TestsFinished handler can emit it inside the cache-write capture
        // window). Caller receives `started` in the returned tuple.
        let started: TestRunStarted = { RunId = runId; StartedAt = startedAt }

        match beforeRun with
        | Some setup ->
            Logging.info "test-prune" "Running beforeRun setup..."
            setup ()
            Logging.info "test-prune" "beforeRun complete"
        | None -> ()

        let groups = configs |> List.groupBy (fun c -> c.Group)

        // Cumulative results built up as groups complete. Mutable under a lock
        // so concurrent group completions see a consistent prefix-chain. Per-
        // group deltas are emitted via TestProgress; the final cumulative is
        // carried by TestRunCompleted (and returned to non-daemon callers).
        let mutable cumulative: Map<string, TestResult> = Map.empty
        let accumulatorLock = obj ()

        let foldAndEmit (groupOutput: (string * TestResult) list) =
            lock accumulatorLock (fun () ->
                for (k, v) in groupOutput do
                    cumulative <- Map.add k v cumulative

                ctx
                |> Option.iter (fun c ->
                    c.EmitTestProgress
                        { RunId = runId
                          NewResults = Map.ofList groupOutput }))

        let! groupResults =
            groups
            |> List.map (fun (_, groupConfigs) ->
                async {
                    let mutable results = []

                    for config in groupConfigs do
                        // Collect extra args (filter + coverage) to append
                        let extraArgs = ResizeArray<string>()

                        let staleProject =
                            match dirtyTracker with
                            | Some t when t.IsDirty config.Project -> true
                            | _ -> false

                        // Template-based class filter (from impact analysis).
                        // When the map is non-empty but has no classes for this project,
                        // skip the project entirely (impact analysis found no relevant tests).
                        let skipProject =
                            not affectedClassesByProject.IsEmpty
                            && not (affectedClassesByProject |> Map.containsKey config.Project)

                        if staleProject then
                            Logging.warn "test-prune" $"Skipping %s{config.Project} — stale binary (dirty tracker)"

                            results <- (config.Project, TestsPassed("", true)) :: results
                        elif skipProject then
                            Logging.info "test-prune" $"Skipping %s{config.Project} — no affected classes"

                            // Skipped-due-to-impact-analysis is the strongest form of filtering;
                            // its coverage contribution is "nothing new", so mark as filtered.
                            results <- (config.Project, TestsPassed("", true)) :: results
                        else

                            let filterArgs = buildFilterArgs config affectedClassesByProject

                            match filterArgs with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            // Raw passthrough filter (from run-tests command)
                            match rawFilter with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            let wasFiltered = Option.isSome filterArgs || Option.isSome rawFilter

                            // Resolve per-project coverage paths (if coverage is configured for
                            // this project). wasFiltered determines which file coverlet writes
                            // to; the post-test step reads those files back to produce cobertura.
                            let projectCoveragePaths =
                                coveragePaths |> Option.bind (fun fn -> fn config.Project)

                            match projectCoveragePaths with
                            | Some paths -> extraArgs.Add(buildCoverageArgs paths wasFiltered)
                            | None -> ()

                            let finalArgs =
                                if extraArgs.Count > 0 then
                                    let extra = String.concat " " extraArgs
                                    $"%s{config.Args} %s{extra}"
                                else
                                    config.Args

                            Logging.info "test-prune" $"Running: %s{config.Command} %s{finalArgs}"

                            let logToCtx msg = ctx |> Option.iter (fun c -> c.Log msg)

                            let timeoutSpan =
                                match config.TimeoutSec with
                                | Some s -> TimeSpan.FromSeconds(float s)
                                | None -> System.Threading.Timeout.InfiniteTimeSpan

                            let runTest =
                                async {
                                    return
                                        runProcessWithTimeout
                                            config.Command
                                            finalArgs
                                            repoRoot
                                            config.Environment
                                            timeoutSpan
                                }

                            let! processResult =
                                match ctx with
                                | Some c ->
                                    PluginCtxHelpers.withSubtask c config.Project $"testing {config.Project}" runTest
                                | None -> runTest

                            let success = isSucceeded processResult
                            let output = outputOf processResult

                            if success then
                                logToCtx $"{config.Project}: passed"
                                Logging.info "test-prune" $"%s{config.Project}: PASSED"
                            else
                                logToCtx $"{config.Project}: failed"
                                Logging.error "test-prune" $"%s{config.Project}: FAILED"

                            if not success then
                                try
                                    let logDir = Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-runs")
                                    Directory.CreateDirectory(logDir) |> ignore
                                    let timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")
                                    let logPath = Path.Combine(logDir, $"%s{config.Project}-%s{timestamp}.log")
                                    File.WriteAllText(logPath, output)
                                    Logging.info "test-prune" $"%s{config.Project}: full output saved to %s{logPath}"
                                with ex ->
                                    Logging.error "test-prune" $"Failed to persist test output: %s{ex.Message}"

                                let lines = output.Split('\n')

                                let failedTests = lines |> Array.filter (fun l -> l.StartsWith("failed "))

                                let summaryLines =
                                    lines
                                    |> Array.filter (fun l ->
                                        l.StartsWith("failed ")
                                        || l.StartsWith("Test run summary:")
                                        || l.Contains("total:")
                                        || l.Contains("failed:")
                                        || l.Contains("succeeded:"))

                                Logging.error
                                    "test-prune"
                                    $"%s{config.Project}: %d{failedTests.Length} test(s) failed:"

                                for line in failedTests do
                                    Logging.error "test-prune" $"  %s{line}"

                                for line in summaryLines |> Array.filter (fun l -> not (l.StartsWith("failed "))) do
                                    Logging.error "test-prune" $"  %s{line}"

                            let result =
                                match processResult with
                                | ProcessOutcome.Succeeded _ -> TestsPassed(output, wasFiltered)
                                | ProcessOutcome.TimedOut(after, _) -> TestsTimedOut(output, after, wasFiltered)
                                | ProcessOutcome.Failed _ -> TestsFailed(output, wasFiltered)

                            // Post-test coverage step: merge or convert the coverlet JSON
                            // output into the Cobertura file downstream consumers read.
                            match projectCoveragePaths with
                            | Some paths -> processCoverageOutput paths wasFiltered
                            | None -> ()

                            results <- (config.Project, result) :: results

                    // Atomically fold this group's results into the shared
                    // accumulator and emit a cumulative snapshot. Groups that
                    // complete later will extend (never contradict) this one.
                    foldAndEmit results
                    return results
                })
            |> Async.Parallel

        // groupResults is the per-group return values; we ignore it because
        // `cumulative` (populated under the lock inside foldAndEmit) is the
        // canonical run-wide aggregate.
        groupResults |> ignore
        sw.Stop()

        let finalResults = lock accumulatorLock (fun () -> cumulative)

        let testResults =
            { Results = finalResults
              Elapsed = sw.Elapsed }

        // Outcome = Normal means the run completed naturally. Per-project
        // pass/fail lives in Results; Aborted is reserved for cancellation,
        // timeouts, or crashes (none wired through this path today).
        ctx |> Option.iter (fun c -> c.EndSubtask PrimarySubtaskKey)

        // EmitTestRunCompleted moved to caller (synchronous Custom handler)
        // so it's captured in EmittedEvents for cache replay. Returned as part
        // of the tuple instead.
        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = sw.Elapsed
              Outcome = Normal
              Results = finalResults
              RanFullSuite = TestResult.ranFullSuite finalResults }

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        Logging.info
            "test-prune"
            $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

        return testResults, started, completed
    }

/// Flush accumulated per-file analysis results to the DB in a single RebuildProjects
/// call. Pure function: takes state, returns updated state.
let private flushPendingAnalysis (db: Database) (state: TestPruneState) =
    let allResults = ResizeArray<AnalysisResult>()

    let mutable newPending = state.PendingAnalysis

    for projectName in state.PendingAnalysis |> Map.toList |> List.map fst do
        match Map.tryFind projectName newPending with
        | Some items ->
            newPending <- Map.remove projectName newPending

            // Use a full record literal (not AnalysisResult.Create) so per-file
            // Attributes and ParentLinks survive the per-project merge.
            // Create defaults both to []; the per-file results above carry them
            // and we'd silently drop them on every flush. Single fold over
            // items to avoid 5 separate passes.
            let syms, deps, tms, attrs, pls =
                (([], [], [], [], []), items)
                ||> List.fold (fun (s, d, t, a, p) r ->
                    (r.Symbols :: s, r.Dependencies :: d, r.TestMethods :: t, r.Attributes :: a, r.ParentLinks :: p))

            let combined =
                { Symbols = syms |> List.rev |> List.concat
                  Dependencies = deps |> List.rev |> List.concat
                  TestMethods = tms |> List.rev |> List.concat
                  Attributes = attrs |> List.rev |> List.concat
                  ParentLinks = pls |> List.rev |> List.concat
                  Diagnostics = AnalysisDiagnostics.Zero }

            Logging.info "test-prune" $"Flushing %d{items.Length} files for %s{projectName} to DB"
            allResults.Add(combined)
        | None -> ()

    if allResults.Count > 0 then
        db.RebuildProjects(Seq.toList allResults)

    // Update in-memory snapshot so subsequent FileChecked reads see the
    // new symbols instead of hitting the DB mid-rebuild.
    let mutable newSnapshot = state.SymbolSnapshot

    for result in allResults do
        for (file, symbols) in result.Symbols |> List.groupBy (fun s -> s.SourceFile) do
            newSnapshot <- Map.add file symbols newSnapshot

    { state with
        PendingAnalysis = newPending
        SymbolSnapshot = newSnapshot }

/// Detect schema-drift errors (stale cache DB lacking a column the current
/// `TestPrune.Core` requires). These surface as SQLite "no such column" /
/// "no column named" messages. Deliberately pure / internal so the caller
/// can unit-test both branches without needing a corrupt DB on disk.
let internal looksLikeSchemaDrift (ex: exn) =
    let msg = ex.Message.ToLowerInvariant()
    msg.Contains("no such column") || msg.Contains("no column named")

/// If `ex` looks like schema drift, delete the cache DB at `dbPath` so the
/// next run rebuilds from scratch. The cache is derivative and safe to
/// regenerate; requiring a user to know which file to delete was the trap
/// this routine exists to close.
let internal tryRepairSchemaDrift (dbPath: string) (ex: exn) =
    if looksLikeSchemaDrift ex && File.Exists dbPath then
        try
            // Delegate to TestPrune.Core — it owns the SQLite-sidecar
            // invariant. Deleting only the main file leaves stale `-wal` /
            // `-shm` sidecars that SQLite may try to "recover" against a
            // freshly created empty DB, producing a 0-byte main DB with no
            // tables — every subsequent INSERT then hits "no such column:
            // <name>".
            TestPrune.Database.deleteCacheFiles dbPath

            Logging.warn
                "test-prune"
                $"Deleted stale cache DB %s{dbPath} after schema-drift error: %s{ex.Message}. Next run will rebuild from scratch."
        with deleteEx ->
            Logging.error
                "test-prune"
                $"Could not delete stale cache DB %s{dbPath}: %s{deleteEx.Message}. Delete it manually and restart the daemon."

/// Create a TestPrune plugin handler using the declarative plugin framework.
/// `buildExtensions` receives the plugin's own `Database` so extensions that
/// need a `RouteStore`/`SymbolStore` derive it from the same DB the plugin
/// queries against — structurally prevents the caller from wiring an extension
/// to a different DB than the plugin's.
let create
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (buildExtensions: (Database -> ITestPruneExtension list) option)
    (beforeRun: (unit -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (getCommitId: (unit -> string option) option)
    (dirtyTracker: FsHotWatch.ProjectDirtyTracker.ProjectDirtyTracker option)
    =
    let db = Database.create dbPath
    let extensions = buildExtensions |> Option.map (fun f -> f db)

    let tryRepairSchemaDrift ex = tryRepairSchemaDrift dbPath ex

    // Flush pending analysis to DB and query affected tests from changed symbols.
    // Extensions (if any) contribute dependency edges via AnalyzeEdges, written
    // to the DB before QueryAffectedTests so they participate in impact traversal.
    let flushAndQueryAffected (state: TestPruneState) =
        let flushedState = flushPendingAnalysis db state

        match extensions with
        | Some exts when not exts.IsEmpty ->
            let store = TestPrune.Ports.toSymbolStore db

            let extensionDeps =
                exts
                |> List.collect (fun ext ->
                    try
                        ext.AnalyzeEdges store flushedState.ChangedFiles repoRoot
                    with ex ->
                        Logging.error "test-prune" $"Extension '%s{ext.Name}' failed: %s{ex.Message}"
                        [])

            if not extensionDeps.IsEmpty then
                let edgeResult =
                    { Symbols = []
                      Dependencies = extensionDeps
                      TestMethods = []
                      Attributes = []
                      ParentLinks = []
                      Diagnostics = AnalysisDiagnostics.Zero }

                db.RebuildProjects([ edgeResult ])
        | _ -> ()

        let symbols = flushedState.ChangedSymbols |> List.distinct

        let affectedTests =
            if symbols.IsEmpty then
                []
            else
                let affected = db.QueryAffectedTests(symbols)

                Logging.info "test-prune" $"QueryAffectedTests(%A{symbols}): %d{affected.Length} affected tests"

                affected

        { flushedState with
            AffectedTests = Analyzed affectedTests }

    // Mutable snapshot of ChangedSymbols for the cache key function.
    // Updated from the Update handler so the cache intercept (which runs
    // before Update) sees the symbols accumulated from prior FileChecked events.
    let mutable changedSymbolsRef: string list = []

    let hasTestConfigs =
        testConfigs |> Option.map (List.isEmpty >> not) |> Option.defaultValue false

    let initialState =
        { PendingAnalysis = Map.empty
          SymbolSnapshot = Map.empty
          AffectedTests = NotYetAnalyzed
          ChangedSymbols = []
          ChangedFiles = []
          TestPhase = TestsIdle(Lifecycle.create None)
          TestClassFiles = Map.empty }

    let runTestsWithImpact
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        (state: TestPruneState)
        (hasCachedResults: bool)
        =
        async {
            try
                // Extension-contributed edges were already written to the DB by
                // flushAndQueryAffected, so state.AffectedTests already includes tests
                // reachable through extension edges (sql, sql-hydra, falco, etc.).
                let affectedTestsList =
                    match state.AffectedTests with
                    | Analyzed tests -> tests
                    | NotYetAnalyzed -> []

                let affectedByProject =
                    affectedTestsList
                    |> List.groupBy (fun t -> t.TestProject)
                    |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)
                    |> Map.ofList

                let totalClasses = affectedByProject |> Map.values |> Seq.sumBy List.length

                if totalClasses = 0 && hasCachedResults then
                    Logging.info "test-prune" "No affected classes — skipping tests (not cold start)"

                    // Build a degenerate lifecycle (Started → Completed with empty
                    // Results) and post it. The synchronous Custom handler emits
                    // both events inside the cache-write capture window so they
                    // replay correctly on cache hit.
                    let runId = Guid.NewGuid()

                    let started: TestRunStarted =
                        { RunId = runId
                          StartedAt = DateTime.UtcNow }

                    let completed: TestRunCompleted =
                        { RunId = runId
                          TotalElapsed = TimeSpan.Zero
                          Outcome = Normal
                          Results = Map.empty
                          RanFullSuite = true }

                    ctx.Post(TestsFinished(started, completed))
                else
                    if totalClasses = 0 then
                        Logging.info "test-prune" "No affected classes (cold start) — running all tests"
                    else
                        for (proj, classes) in affectedByProject |> Map.toList do
                            Logging.info "test-prune" $"Affected classes for %s{proj}: %A{classes}"

                    let! results, started, completed =
                        executeTests
                            (Some ctx)
                            repoRoot
                            beforeRun
                            coveragePaths
                            afterRun
                            configs
                            affectedByProject
                            None
                            dirtyTracker

                    // executeTests still emits per-group TestProgress live; the
                    // synchronous handler emits Started + Completed for the
                    // §2a cache-write capture window.
                    ignore results
                    ctx.Post(TestsFinished(started, completed))
            with ex ->
                Logging.error "test-prune" $"runTests failed: %s{ex.Message}"

                // Build an Aborted lifecycle so subscribers see a coherent end
                // to this run rather than hanging at TestRunStarted.
                let runId = Guid.NewGuid()

                let started: TestRunStarted =
                    { RunId = runId
                      StartedAt = DateTime.UtcNow }

                let completed: TestRunCompleted =
                    { RunId = runId
                      TotalElapsed = TimeSpan.Zero
                      Outcome = Aborted ex.Message
                      Results = Map.empty
                      RanFullSuite = true }

                ctx.Post(TestsFinished(started, completed))
        }

    let commands =
        [ "affected-tests",
          fun (state: TestPruneState) (_args: string array) ->
              async {
                  match state.AffectedTests with
                  | NotYetAnalyzed -> return JsonSerializer.Serialize({| status = "not analyzed" |})
                  | Analyzed tests ->
                      let testsData =
                          tests
                          |> List.map (fun t ->
                              {| project = t.TestProject
                                 ``class`` = t.TestClass
                                 ``method`` = t.TestMethod |})

                      return JsonSerializer.Serialize(testsData)
              }

          "changed-files",
          fun (state: TestPruneState) (_args: string array) ->
              async { return JsonSerializer.Serialize(state.ChangedFiles) }

          "test-results",
          fun (state: TestPruneState) (_args: string array) ->
              async {
                  match state.TestPhase with
                  | TestsRunning _ -> return JsonSerializer.Serialize({| status = "running" |})
                  | TestsIdle idle ->
                      match Lifecycle.value idle with
                      | Some results -> return formatTestResultsJson results
                      | None -> return JsonSerializer.Serialize({| status = "not run" |})
              } ]

    // run-tests command (only if testConfigs are provided)
    let allCommands =
        match testConfigs with
        | Some allConfigs when not allConfigs.IsEmpty ->
            commands
            @ [ "run-tests",
                fun (state: TestPruneState) (args: string array) ->
                    async {
                        match state.TestPhase with
                        | TestsRunning _ -> return JsonSerializer.Serialize({| error = "tests already running" |})
                        | TestsIdle _ ->
                            try
                                let argStr = if args.Length > 0 then args.[0].Trim() else "{}"

                                let parseResult =
                                    try
                                        Ok(JsonDocument.Parse(argStr))
                                    with ex ->
                                        Error ex.Message

                                match parseResult with
                                | Error msg -> return JsonSerializer.Serialize({| error = $"invalid JSON: %s{msg}" |})
                                | Ok doc ->

                                    use doc = doc
                                    let root = doc.RootElement

                                    let filter =
                                        match root.TryGetProperty("filter") with
                                        | true, v -> Some(v.GetString())
                                        | false, _ -> None

                                    let onlyFailed =
                                        match root.TryGetProperty("only-failed") with
                                        | true, v -> v.GetBoolean()
                                        | false, _ -> false

                                    let projectFilter =
                                        match root.TryGetProperty("projects") with
                                        | true, v ->
                                            v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq |> Some
                                        | false, _ -> None

                                    // Resolve configs or produce an error
                                    let lastResults =
                                        match state.TestPhase with
                                        | TestsIdle idle -> Lifecycle.value idle
                                        | TestsRunning _ -> None

                                    let configsResult =
                                        if onlyFailed then
                                            match lastResults with
                                            | Some prev ->
                                                let failedNames =
                                                    prev.Results
                                                    |> Map.toList
                                                    |> List.choose (fun (name, r) ->
                                                        match r with
                                                        | TestsFailed _
                                                        | TestsTimedOut _ -> Some name
                                                        | _ -> None)
                                                    |> Set.ofList

                                                Ok(allConfigs |> List.filter (fun c -> failedNames.Contains(c.Project)))
                                            | None -> Error "no previous results — cannot determine failed projects"
                                        else
                                            match projectFilter with
                                            | Some names ->
                                                Ok(allConfigs |> List.filter (fun c -> names.Contains(c.Project)))
                                            | None -> Ok allConfigs

                                    match configsResult with
                                    | Error msg -> return JsonSerializer.Serialize({| error = msg |})
                                    | Ok configs when configs.IsEmpty ->
                                        return JsonSerializer.Serialize({| error = "no matching test projects" |})
                                    | Ok configs ->
                                        let! results, _started, _completed =
                                            executeTests
                                                None
                                                repoRoot
                                                beforeRun
                                                coveragePaths
                                                afterRun
                                                configs
                                                Map.empty
                                                filter
                                                dirtyTracker

                                        return formatTestResultsJson results
                            with ex ->
                                Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                                return JsonSerializer.Serialize({| error = ex.Message |})
                    } ]
        | _ -> commands

    { Name = PluginName.create "test-prune"
      Init = initialState
      Update =
        fun ctx state event ->
            async {
                match event with
                | PluginEvent.FileChecked result ->
                    // Reset completed flag so new file changes can report status
                    let isIdle =
                        match state.TestPhase with
                        | TestsIdle _ -> true
                        | TestsRunning _ -> false

                    if isIdle then
                        ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

                    try
                        let relPath = Path.GetRelativePath(repoRoot, result.File).Replace('\\', '/')

                        // Canonical project identity. For real .fsproj files, FCS
                        // gives "MyProject.fsproj" → "MyProject". For .fsx scripts
                        // FCS synthesizes "Lib.fsx.fsproj" → "Lib.fsx" after one
                        // strip; drop the trailing ".fsx" so config that specifies
                        // `"Lib"` matches both cases.
                        let projectName =
                            let raw = result.ProjectOptions.ProjectFileName |> Path.GetFileNameWithoutExtension

                            if raw.EndsWith(".fsx") then
                                raw.Substring(0, raw.Length - 4)
                            else
                                raw

                        let! analysisResult =
                            analyzeSource ctx.Checker result.File result.Source result.ProjectOptions projectName

                        match analysisResult with
                        | Ok analysisResult ->
                            let normalizedSymbols = normalizeSymbolPaths repoRoot analysisResult.Symbols

                            let fileAnalysis =
                                { Symbols = normalizedSymbols
                                  Dependencies = analysisResult.Dependencies
                                  TestMethods =
                                    analysisResult.TestMethods
                                    |> List.map (fun t -> { t with TestProject = projectName })
                                  Attributes = analysisResult.Attributes
                                  ParentLinks = analysisResult.ParentLinks
                                  Diagnostics = analysisResult.Diagnostics }

                            // Read stored symbols from the in-memory snapshot (populated after
                            // each flush). Falls back to DB for warm starts where the snapshot
                            // hasn't been populated yet.
                            let storedSymbols =
                                match Map.tryFind relPath state.SymbolSnapshot with
                                | Some symbols -> symbols
                                | None -> db.GetSymbolsInFile(relPath)

                            // Accumulate per-project; flush on BuildCompleted.
                            // Replace any prior analysis for this file to avoid double-counting
                            // when a file is checked more than once before the flush (e.g. initial
                            // scan followed by a file-change recheck).
                            let existingForProject =
                                state.PendingAnalysis |> Map.tryFind projectName |> Option.defaultValue []

                            let filteredExisting =
                                existingForProject
                                |> List.filter (fun a ->
                                    not (a.Symbols |> List.exists (fun s -> s.SourceFile = relPath)))

                            let newPending =
                                state.PendingAnalysis
                                |> Map.add projectName (filteredExisting @ [ fileAnalysis ])

                            let (changes, _events) = detectChanges normalizedSymbols storedSymbols
                            let changedNames = changedSymbolNames changes

                            Logging.info
                                "test-prune"
                                $"detectChanges for %s{relPath}: %d{changes.Length} changes, %d{storedSymbols.Length} stored, %d{normalizedSymbols.Length} current"

                            let newChangedSymbols =
                                if not changedNames.IsEmpty then
                                    Logging.info "test-prune" $"Changed symbols: %A{changedNames}"
                                    (state.ChangedSymbols @ changedNames) |> List.distinct
                                else
                                    state.ChangedSymbols

                            // Only track file as changed if its AST actually changed.
                            // Comment-only changes produce the same symbol hashes, so they
                            // should not trigger extension-based tests (e.g. Falco routes).
                            let newChangedFiles =
                                if not changedNames.IsEmpty && not (state.ChangedFiles |> List.contains relPath) then
                                    relPath :: state.ChangedFiles
                                else
                                    state.ChangedFiles

                            // Update class→file mapping for test methods found in this file
                            let newClassFiles =
                                fileAnalysis.TestMethods
                                |> List.fold (fun acc t -> Map.add t.TestClass result.File acc) state.TestClassFiles

                            // Query affected tests against currently-persisted DB state so
                            // `affected-tests` reflects the latest change without waiting for
                            // BuildCompleted. Skip when this FileChecked contributed no new
                            // changed symbols — the prior AffectedTests is still current,
                            // and hitting SQLite on every unchanged save is wasteful under
                            // rapid-save storms.
                            let queriedAffected =
                                if changedNames.IsEmpty then
                                    match state.AffectedTests with
                                    | Analyzed t -> t
                                    | NotYetAnalyzed -> []
                                else
                                    let distinct = newChangedSymbols |> List.distinct

                                    if distinct.IsEmpty then
                                        []
                                    else
                                        db.QueryAffectedTests(distinct)

                            let newState =
                                { state with
                                    ChangedFiles = newChangedFiles
                                    PendingAnalysis = newPending
                                    ChangedSymbols = newChangedSymbols
                                    TestClassFiles = newClassFiles
                                    AffectedTests = Analyzed queriedAffected }

                            // Keep the mutable snapshot in sync for the cache key function
                            changedSymbolsRef <- newState.ChangedSymbols

                            if isIdle then
                                // Analysis done — report Completed. If a BuildCompleted arrives
                                // later it will re-trigger test execution and set Running again.
                                // Previously we stayed Running here when testConfigs existed,
                                // which caused WaitForComplete to hang when FileChecked events
                                // arrived after the build had already completed.
                                ctx.ReportStatus(Completed(DateTime.UtcNow))

                            return newState
                        | Error msg ->
                            Logging.error "test-prune" $"Analysis failed for %s{relPath}: %s{msg}"

                            if isIdle then
                                ctx.ReportStatus(PluginStatus.Failed($"Analysis failed: %s{msg}", DateTime.UtcNow))

                            return state
                    with ex ->
                        if isIdle then
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                        return state

                | PluginEvent.BuildCompleted buildResult ->
                    match buildResult with
                    | BuildSucceeded ->
                        match state.TestPhase with
                        | TestsRunning(running, _) ->
                            ctx.Log "queued re-run (tests already running)"

                            Logging.info
                                "test-prune"
                                "BuildSucceeded received but tests already running — will re-run after"

                            return
                                { state with
                                    TestPhase = TestsRunning(running, RerunQueued) }
                        | TestsIdle idle ->
                            Logging.info "test-prune" $"BuildSucceeded received, running tests"

                            // Flush/query before announcing Running so the reported status never
                            // lies (the old order would flash Running even on schema-drifted DBs).
                            // The framework catches uncaught throws and forces Failed as a
                            // defense-in-depth net; we still trap locally here so we can run
                            // the schema-drift self-heal and preserve the TestsIdle transition.
                            match
                                (try
                                    Ok(flushAndQueryAffected state)
                                 with ex ->
                                     Error ex)
                            with
                            | Error ex ->
                                Logging.error "test-prune" $"flushAndQueryAffected failed: %s{ex.Message}"
                                tryRepairSchemaDrift ex
                                ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                                return state
                            | Ok stateWithAffected ->
                                ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
                                let running = Lifecycle.start idle

                                let newState =
                                    { stateWithAffected with
                                        TestPhase = TestsRunning(running, NoRerun) }

                                // Dispatch tests to thread pool
                                let hasCachedResults = (Lifecycle.value idle).IsSome

                                match testConfigs with
                                | Some configs when not configs.IsEmpty ->
                                    async { do! runTestsWithImpact ctx configs newState hasCachedResults }
                                    |> Async.Start

                                    return newState
                                | _ ->
                                    // No test configs — flush only, transition back to idle
                                    let idleAgain = Lifecycle.complete None running

                                    return
                                        { newState with
                                            TestPhase = TestsIdle idleAgain }
                    | BuildFailed _ -> return state

                | Custom(TestsFinished(started, completed)) ->
                    // §2a: emit lifecycle events synchronously here (inside the framework's
                    // per-event capture window) so they're recorded in the cached
                    // EmittedEvents and re-fired on cache replay. Live per-group
                    // TestProgress already fired from the async; subscribers that key off
                    // TestRunCompleted (e.g. FileCommandPlugin) must see it on cache hit.
                    ctx.EmitTestRunStarted started
                    ctx.EmitTestRunCompleted completed

                    // Apply error reporting synchronously here too — live emission from
                    // the async wouldn't be captured for cache replay.
                    let testResults: TestResults =
                        { Results = completed.Results
                          Elapsed = completed.TotalElapsed }

                    if testResults.Results |> Map.forall (fun _ r -> TestResult.isPassed r) then
                        ctx.ClearAllErrors()
                    else
                        reportTestErrors ctx state.TestClassFiles testResults

                    // Re-emit stale-binary warnings for any project the dirty tracker still
                    // reports as stale. Done after the clear/report step above so the warning
                    // survives ClearAllErrors when stale-skipped projects pass trivially.
                    match dirtyTracker, testConfigs with
                    | Some tracker, Some configs ->
                        for c in configs do
                            if tracker.IsDirty c.Project then
                                let warnEntry: ErrorLedger.ErrorEntry =
                                    { Message =
                                        $"Tests for %s{c.Project} skipped — binary is stale "
                                        + "(sources newer than DLL). This usually means MSBuild's incremental "
                                        + "cache decided the project was up-to-date when it was not. "
                                        + "A full rebuild should fix it."
                                      Severity = ErrorLedger.Warning
                                      Line = 0
                                      Column = 0
                                      Detail = None }

                                ctx.ReportErrors c.Project [ warnEntry ]
                    | _ -> ()

                    // Pushing a terminal Completed/Failed status is what appends the
                    // run to history; both RerunQueued and NoRerun branches must call this.
                    let recordRunOutcome (results: TestResults) =
                        let total = results.Results.Count

                        let failedList =
                            results.Results
                            |> Map.toList
                            |> List.filter (fun (_, r) -> not (TestResult.isPassed r))

                        let failed = failedList.Length
                        let passed = total - failed

                        let anyFiltered =
                            results.Results |> Map.exists (fun _ r -> TestResult.wasFiltered r)

                        let selectedSuffix = if anyFiltered then "yes" else "no"

                        let timedOutProjects =
                            failedList
                            |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then Some name else None)

                        if not timedOutProjects.IsEmpty then
                            let names = timedOutProjects |> String.concat ", "
                            ctx.CompleteWithTimeout $"test project(s): {names}"

                            ctx.ReportStatus(
                                PluginStatus.Failed(
                                    $"%d{timedOutProjects.Length} timed out: %s{names}",
                                    DateTime.UtcNow
                                )
                            )
                        else
                            ctx.CompleteWithSummary
                                $"%d{passed} passed, %d{failed} failed in %d{total} projects (selected: %s{selectedSuffix})"

                            if failed = 0 then
                                ctx.ReportStatus(Completed(DateTime.UtcNow))
                            else
                                let names = failedList |> List.map fst |> String.concat ", "

                                ctx.ReportStatus(PluginStatus.Failed($"%d{failed} failed: %s{names}", DateTime.UtcNow))

                    match state.TestPhase with
                    | TestsRunning(running, RerunQueued) ->
                        Logging.info "test-prune" "Re-running tests (queued during previous run)"

                        // Flush any new pending analysis. If the DB errors out here the rerun
                        // never happens, so we must bail to TestsIdle (capturing the just-
                        // completed testResults) instead of leaving the phase stuck in Running.
                        match
                            (try
                                Ok(
                                    flushAndQueryAffected
                                        { state with
                                            TestPhase = TestsRunning(running, NoRerun) }
                                )
                             with ex ->
                                 Error ex)
                        with
                        | Error ex ->
                            Logging.error "test-prune" $"flushAndQueryAffected (rerun) failed: %s{ex.Message}"
                            tryRepairSchemaDrift ex
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                            let completed = Lifecycle.complete (Some testResults) running

                            return
                                { state with
                                    TestPhase = TestsIdle completed
                                    ChangedFiles = []
                                    ChangedSymbols = []
                                    AffectedTests = Analyzed [] }
                        | Ok rerunState ->
                            recordRunOutcome testResults
                            changedSymbolsRef <- []

                            let rerunRunning = Lifecycle.complete (Some testResults) running |> Lifecycle.start

                            ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

                            let rerunState =
                                { rerunState with
                                    TestPhase = TestsRunning(rerunRunning, NoRerun) }

                            match testConfigs with
                            | Some configs when not configs.IsEmpty ->
                                async { do! runTestsWithImpact ctx configs rerunState true } |> Async.Start
                            | _ -> ()

                            return rerunState
                    | TestsRunning(running, NoRerun) ->
                        let completed = Lifecycle.complete (Some testResults) running
                        changedSymbolsRef <- []
                        recordRunOutcome testResults

                        return
                            { state with
                                TestPhase = TestsIdle completed
                                ChangedFiles = []
                                ChangedSymbols = []
                                AffectedTests = Analyzed [] }
                    | TestsIdle _ ->
                        // Unexpected but handle gracefully
                        return state

                | _ -> return state
            }
      Commands = allCommands
      Subscriptions =
        Set.ofList (
            [ SubscribeFileChecked ]
            @ (if hasTestConfigs then [ SubscribeBuildCompleted ] else [])
        )
      CacheKey =
        // §2a: drop commit_id; key on inputs that actually determine the result.
        // For BuildCompleted: changed symbols + build outcome — together these
        // dictate which tests run. For FileChecked: file path + source content
        // (TestPrune updates internal symbol state from the source bytes).
        ignore getCommitId

        let cacheKey (event: PluginEvent<TestPruneMsg>) : ContentHash option =
            // Reuses the same merkle for BuildCompleted and Custom TestsFinished
            // so the cache writes on TestsFinished (synchronous handler — captures
            // EmittedEvents) and the next BuildCompleted hits via the matching key.
            // TestsFinished only fires after BuildSucceeded (BuildFailed short-circuits
            // earlier), so outcome="succeeded" is correct for the Custom path.
            let buildCompletedKey () =
                let symbolsHash =
                    changedSymbolsRef
                    |> List.distinct
                    |> List.sort
                    |> String.concat "|"
                    |> FsHotWatch.CheckCache.sha256Hex

                FsHotWatch.TaskCache.merkleCacheKey
                    [ "plugin-version", "test-prune-merkle-v1"
                      "event", "BuildCompleted"
                      "changed-symbols", symbolsHash
                      "build-outcome", "succeeded" ]

            match event with
            | BuildCompleted BuildSucceeded -> Some(buildCompletedKey ())
            | BuildCompleted(BuildFailed errs) ->
                let symbolsHash =
                    changedSymbolsRef
                    |> List.distinct
                    |> List.sort
                    |> String.concat "|"
                    |> FsHotWatch.CheckCache.sha256Hex

                Some(
                    FsHotWatch.TaskCache.merkleCacheKey
                        [ "plugin-version", "test-prune-merkle-v1"
                          "event", "BuildCompleted"
                          "changed-symbols", symbolsHash
                          "build-outcome", "failed:" + String.concat "|" (List.sort errs) ]
                )
            | Custom(TestsFinished _) -> Some(buildCompletedKey ())
            | FileChecked r ->
                // §1: fcs-signature captures cross-file FCS state so symbol
                // changes upstream invalidate this file's cached symbol-diff.
                let fcsSignature = FsHotWatch.CheckCache.fcsCheckSignature r.CheckResults

                Some(
                    FsHotWatch.TaskCache.merkleCacheKey
                        [ "plugin-version", "test-prune-merkle-v2"
                          "event", "FileChecked"
                          "file", r.File
                          "source", r.Source
                          "fcs-signature", fcsSignature ]
                )
            | _ -> None

        Some cacheKey
      Teardown = None }
