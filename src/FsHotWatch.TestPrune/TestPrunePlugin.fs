module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsDiagnosticFilter
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

/// Per-project raw cobertura written by a FULL (unfiltered) test run.
[<Literal>]
let BaselineName = "coverage.baseline.cobertura.xml"

/// Per-project raw cobertura written by an impact-FILTERED test run.
[<Literal>]
let PartialName = "coverage.partial.cobertura.xml"

/// The single shared cobertura emitted from the full TestPrune DB and consumed
/// by downstream gating (coverageratchet).
[<Literal>]
let CoberturaName = "coverage.cobertura.xml"

/// Per-project raw-coverage artifact paths + the command-line template used to
/// produce them. The runner writes Cobertura XML to `Baseline` (full run) or
/// `Partial` (impact-filtered run); the plugin ingests whichever this run wrote
/// into the TestPrune DB and emits the whole DB once to `Cobertura`. Callers
/// (DaemonConfig) decide the directory layout and the arg template; the plugin
/// treats the paths as opaque absolute paths and substitutes `{output}` in
/// `ArgsTemplate`.
///
/// The file format is Cobertura regardless of `ArgsTemplate` — the template
/// is responsible for telling its runner to write Cobertura to `{output}`.
/// For Microsoft Testing Platform, use `defaultCoverageArgsTemplate`; for
/// other runners (coverlet.collector, AltCover, OpenCover) supply your own.
type CoveragePaths =
    {
        Baseline: string
        Partial: string
        /// The SHARED cobertura the plugin emits the whole DB to — set identically
        /// for every project by DaemonConfig (the DB unions coverage across them),
        /// so the daemon writes one run-wide artifact, not one per project.
        Cobertura: string
        ArgsTemplate: string
    }

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

/// True when most of a run's coverage lines failed to attribute to a symbol — a sign the
/// symbol graph is still being indexed (e.g. the first run after a schema bump recreated the
/// TestPrune DB, before the daemon's scan reached the covered files). A healthy run maps the
/// vast majority of lines (the only misses are rare inter-symbol lines), so requiring at
/// least half to map cleanly separates "still indexing" from a real run.
let internal symbolGraphLooksIncomplete (ingested: int) (skipped: int) : bool =
    ingested + skipped > 0 && ingested < skipped

/// Serially ingest each project's raw runner cobertura into the TestPrune DB
/// (symbol-relative, max-merged across all test projects), then emit the FULL
/// DB ONCE to a single shared cobertura file that downstream gating reads.
///
/// `inputs` is the list of `(rawCoberturaPath, sharedCoberturaOutputPath)`
/// tuples collected from each project that ran with coverage; the output path
/// is identical for every project (DaemonConfig points every project at one
/// shared file), so emitting once at the end is the single source of truth.
///
/// Invariants preserved from the old per-line merge:
/// - An empty / aborted raw cobertura parses to zero rows → ingests nothing →
///   cannot clobber the DB or the emitted file (Issue 3).
/// - If NO raw inputs exist on disk, the shared cobertura is NOT written, so a
///   prior good emission is never overwritten with nothing.
let internal ingestAndEmitCoverage
    (db: Database)
    (repoRoot: string)
    (coverageOutput: string option)
    (rawPaths: string list)
    : unit =
    try
        let existing = rawPaths |> List.filter File.Exists

        let results =
            existing
            |> List.map (fun rawPath -> File.ReadAllText rawPath |> ingestCobertura db (Some repoRoot))

        let totalIngested = results |> List.sumBy (fun r -> r.Ingested)
        let totalSkipped = results |> List.sumBy (fun r -> r.Skipped)

        // Cold-start guard. If most coverage lines found NO containing symbol, the symbol
        // graph is still being indexed — e.g. the FIRST run after a schema bump recreated the
        // TestPrune DB, before the daemon's scan reached the covered files. Emitting now would
        // write a partial cobertura that DROPS every not-yet-indexed file's coverage, clobbering
        // a prior good emission and failing the ratchet. So skip the emit; the DB persists and
        // max-merges, so a later warm run emits in full.
        match existing, coverageOutput with
        | [], _
        | _, None -> ()
        | _, Some _ when symbolGraphLooksIncomplete totalIngested totalSkipped ->
            Logging.warn
                "test-prune"
                $"coverage: only %d{totalIngested} of %d{totalIngested + totalSkipped} lines mapped to a symbol — symbol graph still indexing; skipping emit to avoid a partial snapshot (will emit once warm)."
        | _, Some out ->
            let dir = Path.GetDirectoryName(out)

            if not (String.IsNullOrEmpty dir) then
                Directory.CreateDirectory(dir) |> ignore

            File.WriteAllText(out, emitCobertura db)
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
        /// Last completed test run's results, if any. Replaces the prior
        /// `Lifecycle.value`-encoded last results — `ctx.IsRunning "tests"`
        /// is the source of truth for "currently running", so we no longer
        /// need a phantom-typed Idle/Running phase to wrap this value.
        LastResults: TestResults option
        /// True if a BuildCompleted arrived while a test run was in flight.
        /// The synchronous `Custom(TestsFinished)` handler reads this AFTER
        /// the run completes — at which point `state.ChangedSymbols` reflects
        /// every FileChecked that landed during the run, including ones that
        /// arrived between the queueing BuildCompleted and TestsFinished.
        /// Cleared when the rerun is dispatched.
        PendingRerun: bool
        /// Maps test class name → absolute source file path (built during FileChecked analysis).
        TestClassFiles: Map<string, string>
        /// Item 3 (BuildCompleted-gated stamping). True after the plugin has
        /// observed at least one `BuildCompleted BuildSucceeded` event in this
        /// daemon session. The `FileChecked` handler uses this to decide
        /// whether a clean FCS check is allowed to promote the freshness
        /// sidecar to `fcsClean = true`.
        ///
        /// fshw's cold-scan pipeline guarantees BuildCompleted reaches the
        /// TestPrune mailbox before any FileChecked (see Daemon.fs's
        /// performScan: `BuildPlugin terminal awaited before FCS tier
        /// checks`). So in normal operation this flag is set before any
        /// FileChecked is processed; the gate is therefore effective on the
        /// very first cold start of a fresh daemon, no two-session warm-up
        /// required.
        ///
        /// Resets on plugin restart — that's by design. A daemon restart
        /// clears the in-process "I've seen warm FCS this session"
        /// assertion.
        BuildCompletedInThisSession: bool
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
                | TestsPassed(o, _, _) -> ("passed", o)
                | TestsFailed(o, _, _) -> ("failed", o)
                | TestsTimedOut(o, _, _, _) -> ("timed-out", o)
                | TestsDeferred reason -> ("deferred", reason)

            {| project = name
               status = status
               output = truncateOutput 200 output
               elapsedMs = (TestResult.elapsed result).TotalMilliseconds |})

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

/// Issue 2 — STRUCTURAL apphost-missing detection. On a cold daemon a
/// `dotnet run --project <proj> --no-build` can be launched before the build
/// plugin produced that project's apphost binary; `dotnet run` then fails to
/// spawn it and exits non-zero. That is an ORDERING bug, never a test failure.
///
/// Rather than sniff localized OS error text out of the runner output (fragile
/// to locale and SDK phrasing — see `looksLikeApphostMissing`, kept only as a
/// defensive fallback), we derive the apphost binary path from the runner's
/// `--project` arg and check `File.Exists` BEFORE/around the launch. The
/// apphost is the extension-less sibling of the canonical
/// `<projDir>/bin/Debug/<tfm>/<assemblyName>.dll` (`.exe` on Windows). We don't
/// know the TFM without the project graph here, so we glob every
/// `bin/Debug/*/` TFM dir for the assembly. If NO apphost is found for a
/// derivable project, that absence IS the "apphost not yet produced" signal.
///
/// Returns:
///   Some true  — project derivable AND apphost present
///   Some false — project derivable AND apphost absent (the deferred signal)
///   None       — could not derive a project from args (e.g. a non-`dotnet run`
///                custom command); caller falls back to the output sniff.
let internal tryApphostPresent (args: string) (repoRoot: string) : bool option =
    // Tokenize on whitespace and find the value after `--project`.
    let tokens =
        if String.IsNullOrWhiteSpace args then
            [||]
        else
            args.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

    let projArg =
        tokens
        |> Array.tryFindIndex (fun t -> t = "--project" || t = "-p")
        |> Option.bind (fun i -> if i + 1 < tokens.Length then Some(tokens.[i + 1]) else None)
        |> Option.map (fun raw -> raw.Trim('"'))

    match projArg with
    | None -> None
    | Some proj ->
        // Resolve to an absolute path (relative paths are repoRoot-relative).
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        // The `--project` value may point at a `.fsproj`/`.csproj` file or at a
        // directory. Derive (projDir, assemblyName) for both shapes. The
        // assembly name defaults to the project/dir leaf — matching the
        // canonical DLL derivation in ProjectGraph.GetCanonicalDllPath, which
        // uses the project file's base name.
        let projDir, assemblyName =
            if
                File.Exists abs
                && (abs.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || abs.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            then
                Path.GetDirectoryName(abs), Path.GetFileNameWithoutExtension(abs)
            else
                // Treat as a directory. The assembly name conventionally matches
                // the directory leaf; if a single project file lives there, prefer
                // that file's base name.
                let dir = abs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                let nameFromProjFile =
                    if Directory.Exists dir then
                        Directory.GetFiles(dir, "*.fsproj")
                        |> Array.append (Directory.GetFiles(dir, "*.csproj"))
                        |> Array.tryHead
                        |> Option.map Path.GetFileNameWithoutExtension
                    else
                        None

                dir, (nameFromProjFile |> Option.defaultValue (Path.GetFileName dir))

        let binDir = Path.Combine(projDir, "bin", "Debug")

        if not (Directory.Exists binDir) then
            // No build output at all yet — apphost definitionally absent.
            Some false
        else
            // The apphost lives at bin/Debug/<tfm>/<assemblyName>(.exe). We don't
            // know the TFM, so scan every TFM dir for the extension-less binary
            // (Unix) or the `.exe` (Windows).
            let present =
                Directory.GetDirectories(binDir)
                |> Array.exists (fun tfmDir ->
                    File.Exists(Path.Combine(tfmDir, assemblyName))
                    || File.Exists(Path.Combine(tfmDir, assemblyName + ".exe")))

            Some present

/// Issue 1/2 — defensive fallback apphost-missing classifier, used ONLY when
/// `tryApphostPresent` can't derive a project from the runner args (custom,
/// non-`dotnet run` commands). On a cold daemon a `dotnet run --project <proj>
/// --no-build` launched before the build plugin produced the apphost fails to
/// spawn it and surfaces the .NET host's start-process error, exiting non-zero.
/// That non-zero exit is an ORDERING bug, never a test failure.
///
/// This classifier distinguishes that launch failure from a genuine non-zero
/// test exit. A real xUnit/MTP failure carries `failed <name>` lines and a
/// `failed:`/`Test run summary` block; the apphost-launch failure carries the
/// host's "An error occurred trying to start process …" / "No such file or
/// directory" signature and NO test-summary block. The match is deliberately
/// conservative: when in doubt we treat output as a real failure (never
/// silence a red). Pure + internal so both branches are unit-testable without
/// a live daemon.
let internal looksLikeApphostMissing (output: string) : bool =
    if String.IsNullOrWhiteSpace output then
        false
    else
        let lower = output.ToLowerInvariant()

        // Signatures the .NET host emits when it cannot launch the apphost the
        // build was supposed to produce.
        let hasStartProcessFailure =
            lower.Contains("an error occurred trying to start process")
            || (lower.Contains("no such file or directory")
                && (lower.Contains("apphost")
                    || lower.Contains("/bin/")
                    || lower.Contains("\\bin\\")))
            || lower.Contains("apphost_version not found")

        // A genuine test run always emits a summary / per-test `failed ` lines.
        // Their PRESENCE means the runner actually executed tests, so this is a
        // real failure, not a launch race — don't misclassify it.
        let looksLikeRealTestFailure =
            lower.Contains("test run summary")
            || lower.Contains("failed:")
            || (output.Split('\n')
                |> Array.exists (fun l -> l.TrimStart().StartsWith("failed ")))

        hasStartProcessFailure && not looksLikeRealTestFailure

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
            | TestsFailed(output, _, _)
            | TestsTimedOut(output, _, _, _) ->
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
            | TestsDeferred reason ->
                // Issue 1: NOT a test failure — surface an honest "waiting on
                // build / did not run" diagnostic so the verdict is non-green
                // (nothing was verified) WITHOUT claiming a test failed.
                [ $"<tests/%s{project}>",
                  ErrorLedger.ErrorEntry.errorWithDetail
                      $"%s{project}: waiting on build — %s{reason}"
                      $"The %s{project} test project did not run because its build artifact (apphost) was not produced. Tests were NOT executed, so this cycle cannot be reported as passing. This is a build-ordering issue, not a test failure." ]
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
/// events fire; the caller just gets back the final TestResults. ctx=None
/// also disables the skip-on-stale shortcut so manual runs aren't deadlocked
/// by a stuck dirty bit — see the staleness branch below.
let private flakinessHistoryPath (repoRoot: string) =
    Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-history.json")

let private testRunsDir (repoRoot: string) =
    Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-runs")

let private executeTests
    (db: Database)
    (ctx: PluginCtx<'msg> option)
    (repoRoot: string)
    (beforeRun: (unit -> unit) option)
    (coveragePaths: (string -> CoveragePaths option) option)
    (afterRun: (TestResults -> unit) option)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
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

        // Raw-cobertura ingest inputs collected across the parallel per-project
        // runs. Each entry is (rawCoberturaPathThisProjectWrote,
        // sharedCoberturaOutputPath). Ingest+emit runs SERIALLY after
        // Async.Parallel completes so concurrent group completions never race on
        // the DB write or the single shared output file.
        let mutable coverageRawPaths: string list = []
        // Every project's CoveragePaths.Cobertura is the same run-wide shared path;
        // captured once so the DB is emitted to a single file after the run.
        let mutable coverageOutput: string option = None
        let coverageRawPathsLock = obj ()

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

                        // Template-based class filter (from impact analysis).
                        // When the map is non-empty but has no classes for this project,
                        // skip the project entirely (impact analysis found no relevant tests).
                        let skipProject =
                            not affectedClassesByProject.IsEmpty
                            && not (affectedClassesByProject |> Map.containsKey config.Project)

                        if skipProject then
                            Logging.info "test-prune" $"Skipping %s{config.Project} — no affected classes"

                            // Skipped-due-to-impact-analysis is the strongest form of filtering;
                            // its coverage contribution is "nothing new", so mark as filtered.
                            // Elapsed=Zero since we didn't actually run the test runner.
                            results <- (config.Project, TestsPassed("", true, TimeSpan.Zero)) :: results
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

                            // Microsoft Testing Platform (xUnit v3, MTP-compatible
                            // runners) supports `--report-ctrf` for structured per-test
                            // output that the flakiness tracker can ingest. Gated on a
                            // `dotnet` command — non-MTP runners (sleep, echo, etc.)
                            // would error on the unknown flag.
                            let isDotnetCommand (cmd: string) =
                                let leaf = Path.GetFileNameWithoutExtension(cmd)
                                leaf = "dotnet"

                            let ctrfPath =
                                if isDotnetCommand config.Command then
                                    let ctrfDir = testRunsDir repoRoot
                                    Directory.CreateDirectory(ctrfDir) |> ignore
                                    let ctrfName = $"{config.Project}-{Guid.NewGuid():N}.ctrf.json"

                                    extraArgs.Add(
                                        $"--report-ctrf --report-ctrf-filename {ctrfName} --results-directory \"{ctrfDir}\""
                                    )

                                    Some(Path.Combine(ctrfDir, ctrfName))
                                else
                                    None

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

                            let projectSw = Stopwatch.StartNew()

                            let runOnce =
                                async {
                                    return
                                        runProcessWithTimeout
                                            config.Command
                                            finalArgs
                                            repoRoot
                                            config.Environment
                                            timeoutSpan
                                }

                            // Issue 2: STRUCTURAL apphost-missing detection. Prefer
                            // a `File.Exists` check on the derived apphost binary
                            // over sniffing localized OS error text. When the
                            // project isn't derivable from the runner args (custom,
                            // non-`dotnet run` command), fall back to the output
                            // sniff. `outcome` is the process result whose output
                            // the fallback inspects.
                            let detectApphostMissing (outcome: ProcessOutcome) : bool =
                                // A clean exit means the apphost ran — never a
                                // launch-ordering problem, regardless of artifacts.
                                if isSucceeded outcome then
                                    false
                                else
                                    match tryApphostPresent config.Args repoRoot with
                                    | Some present -> not present
                                    | None ->
                                        // Not derivable — fall back to the text sniff.
                                        match outcome with
                                        | ProcessOutcome.Failed(_, out) -> looksLikeApphostMissing out
                                        | _ -> false

                            // Issue 1/2: cold-start apphost-missing retry. The
                            // BuildCompleted→TestPrune ordering already gates the
                            // launch on a successful build, but a narrow race can
                            // still fire `--no-build` before the apphost lands. If
                            // the FIRST run looks like an apphost-missing launch
                            // (structural check, or text sniff fallback), wait
                            // briefly for the build to settle and retry ONCE. A
                            // still-missing apphost after the retry is surfaced as
                            // DEFERRED ("waiting on build"), never FAILED.
                            let runTestWithRetry =
                                async {
                                    let! first = runOnce

                                    if detectApphostMissing first then
                                        Logging.warn
                                            "test-prune"
                                            $"%s{config.Project}: apphost missing at launch (build not settled yet); retrying once after a short wait"

                                        do! Async.Sleep 750
                                        let! second = runOnce
                                        return second
                                    else
                                        return first
                                }

                            let! processResult =
                                match ctx with
                                | Some c ->
                                    PluginCtxHelpers.withSubtask
                                        c
                                        config.Project
                                        $"testing {config.Project}"
                                        runTestWithRetry
                                | None -> runTestWithRetry

                            projectSw.Stop()
                            let projectElapsed = projectSw.Elapsed

                            // Issue 1/2: distinguish a still-missing apphost (an
                            // ordering bug) from a genuine non-zero test exit,
                            // via the same structural-with-fallback check.
                            let apphostMissing = detectApphostMissing processResult

                            let success = isSucceeded processResult
                            let output = outputOf processResult

                            if apphostMissing then
                                logToCtx $"{config.Project}: waiting on build (apphost not yet produced)"

                                Logging.warn
                                    "test-prune"
                                    $"%s{config.Project}: apphost still missing after retry — surfacing as 'waiting on build', not FAILED (this is a build-ordering issue, never a test failure)"
                            elif success then
                                logToCtx $"{config.Project}: passed"
                                Logging.info "test-prune" $"%s{config.Project}: PASSED"
                            else
                                logToCtx $"{config.Project}: failed"
                                Logging.error "test-prune" $"%s{config.Project}: FAILED"

                            if not success && not apphostMissing then
                                try
                                    let logDir = testRunsDir repoRoot
                                    Directory.CreateDirectory(logDir) |> ignore
                                    let timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")
                                    let logPath = Path.Combine(logDir, $"%s{config.Project}-%s{timestamp}.log")
                                    File.WriteAllText(logPath, output)
                                    Logging.info "test-prune" $"%s{config.Project}: full output saved to %s{logPath}"
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException as ex ->
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
                                | _ when apphostMissing ->
                                    // Issue 1: the tests NEVER RAN — the apphost
                                    // wasn't produced. This is a dedicated
                                    // `TestsDeferred` case, NOT a pass: `isPassed`
                                    // is false for it, so it can never produce a
                                    // silent false-green verdict. It is also not a
                                    // real failure — the verdict surfaces it as an
                                    // honest "waiting on build" diagnostic.
                                    // `TestsDeferred` carries no elapsed/wasFiltered,
                                    // and `wasFiltered` reports true for it, so it
                                    // never lowers a coverage baseline (Issue 3).
                                    TestsDeferred "apphost not produced; tests did not run"
                                | ProcessOutcome.Succeeded _ -> TestsPassed(output, wasFiltered, projectElapsed)
                                | ProcessOutcome.TimedOut(after, _) ->
                                    TestsTimedOut(output, after, wasFiltered, projectElapsed)
                                | ProcessOutcome.Failed _ -> TestsFailed(output, wasFiltered, projectElapsed)

                            // Post-test coverage step: collect this project's raw
                            // runner cobertura for SERIAL ingest after Async.Parallel
                            // (a parallel DB write + shared-file write would race).
                            // Issue 3: a run that never executed (apphost missing)
                            // contributes NO input — a partial/empty file must not
                            // lower coverage. An empty/aborted raw cobertura that IS
                            // collected ingests nothing (parse → [] → no-op), so it
                            // also can't clobber the emitted file.
                            match projectCoveragePaths with
                            | Some paths when not apphostMissing ->
                                let rawPath = if wasFiltered then paths.Partial else paths.Baseline

                                lock coverageRawPathsLock (fun () ->
                                    coverageRawPaths <- rawPath :: coverageRawPaths
                                    coverageOutput <- Some paths.Cobertura)
                            | _ -> ()

                            // Per-test flakiness tracking: parse the CTRF report this run
                            // emitted, append per-test records to the rolling history file
                            // (capped at 20 runs per test). Best-effort — exceptions don't
                            // fail the run.
                            match ctrfPath with
                            | None -> ()
                            | Some p ->
                                try
                                    let records = Flakiness.parseCtrfTests (File.ReadAllText p)

                                    if not records.IsEmpty then
                                        Flakiness.appendRecords (flakinessHistoryPath repoRoot) 20 records

                                    File.Delete p
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException
                                | :? JsonException as ex ->
                                    Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"

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

        // Coverage: now that ALL projects have finished, serially ingest each
        // project's raw runner cobertura into the TestPrune DB (max-merged,
        // symbol-relative) and emit the FULL DB ONCE to the single shared
        // cobertura file. Done here, outside Async.Parallel, so there is no
        // DB-write contention and no file-write race on the shared output.
        let collectedRawPaths, sharedOutput =
            lock coverageRawPathsLock (fun () -> List.rev coverageRawPaths, coverageOutput)

        ingestAndEmitCoverage db repoRoot sharedOutput collectedRawPaths

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

/// FCS cache-poisoning gate. A `FileChecked` whose underlying FCS result
/// reports any Error-severity diagnostic is treated as untrustworthy: cold-
/// start FCS sometimes returns "expected type X but here has type X" for
/// files that compile cleanly once warm, and flushing those poisoned
/// symbols would overwrite the prior good DB snapshot. Gating by severity
/// (not message text) handles both the cold-start race and the user-broke-
/// their-code case identically: in both, we hold the prior DB row instead
/// of replacing it. `ParseOnly` (check aborted) is treated as "no observable
/// errors" so the existing fall-through behaviour is preserved.
///
/// `suppressedCodes` (caller-configured) is merged with per-file `#nowarn`
/// directives via `FcsDiagnosticFilter.allSuppressedCodes` so the gate sees
/// the same filter the user-visible error stream applies in
/// `Daemon.reportFcsDiagnostics`. Without this symmetry the gate trips on
/// codes the user has already silenced (e.g. FS1182 promoted to Error by
/// `<TreatWarningsAsErrors>` but suppressed via `#nowarn "1182"`), killing
/// cache-replay across daemon restarts on every cold scan.
let internal hasFcsErrors (suppressedCodes: Set<int>) (source: string) (state: FileCheckState) : bool =
    match state with
    | FullCheck cr ->
        let allSuppressed = allSuppressedCodes suppressedCodes source

        cr.Diagnostics
        |> Array.exists (fun d ->
            d.Severity = FSharpDiagnosticSeverity.Error
            && not (allSuppressed.Contains d.ErrorNumber))
    | ParseOnly -> false

/// Count of Error-severity diagnostics not in the effective suppression set —
/// used only for the skip-log message so operators have a number to
/// correlate against the FCS-error stream. Must apply the same filter as
/// `hasFcsErrors` so the count matches what the gate decided on.
let internal fcsErrorCount (suppressedCodes: Set<int>) (source: string) (state: FileCheckState) : int =
    match state with
    | FullCheck cr ->
        let allSuppressed = allSuppressedCodes suppressedCodes source

        cr.Diagnostics
        |> Array.filter (fun d ->
            d.Severity = FSharpDiagnosticSeverity.Error
            && not (allSuppressed.Contains d.ErrorNumber))
        |> Array.length
    | ParseOnly -> 0

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

    // Per-file FCS freshness sidecar (Path D in the 0.10 fix-forward design).
    // Loaded once at plugin construction from `.fshw/test-prune/file-freshness.json`
    // and updated incrementally on each FileChecked. Survives daemon restarts
    // so cross-restart Phase B replay can decide which files' stored symbols
    // are trustworthy enough to run detectChanges against.
    //
    // Held in a closure-local mutable cell + Volatile for the same reason
    // changedSymbolsRef is — the plugin Update handler reads/writes from
    // multiple threads (mailbox + cache intercept).
    let mutable freshnessRef: FileFreshness.Store = FileFreshness.load repoRoot

    let updateFreshness (newStore: FileFreshness.Store) =
        Volatile.Write(&freshnessRef, newStore)

        try
            FileFreshness.save repoRoot newStore
        with ex ->
            Logging.warn
                "test-prune"
                $"failed to persist file-freshness sidecar: %s{ex.Message}; in-memory state still updated"

    let hasTestConfigs =
        testConfigs |> Option.map (List.isEmpty >> not) |> Option.defaultValue false

    let initialState =
        { PendingAnalysis = Map.empty
          SymbolSnapshot = Map.empty
          AffectedTests = NotYetAnalyzed
          ChangedSymbols = []
          ChangedFiles = []
          LastResults = None
          PendingRerun = false
          TestClassFiles = Map.empty
          BuildCompletedInThisSession = false }

    /// Returns the `TestsFinished` message that the framework's RunExclusive
    /// will post back to the agent. The synchronous `Custom(TestsFinished)`
    /// handler emits the `TestRunStarted`/`TestRunCompleted` events inside
    /// the §2a cache-write capture window. Catches its own exceptions to
    /// produce an `Aborted` lifecycle rather than letting RunExclusive eat
    /// the message (which would leave the slot freed but no completion
    /// posted, stranding `LastResults`/`PendingRerun` in a Schrödinger state).
    let runTestsWithImpact
        (ctx: PluginCtx<TestPruneMsg>)
        (configs: TestConfig list)
        (state: TestPruneState)
        (hasCachedResults: bool)
        : Async<TestPruneMsg> =
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
                    // Results). The synchronous Custom handler emits both events
                    // inside the cache-write capture window so they replay
                    // correctly on cache hit.
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

                    return TestsFinished(started, completed)
                else
                    if totalClasses = 0 then
                        Logging.info "test-prune" "No affected classes (cold start) — running all tests"
                    else
                        for (proj, classes) in affectedByProject |> Map.toList do
                            Logging.info "test-prune" $"Affected classes for %s{proj}: %A{classes}"

                    let! results, started, completed =
                        executeTests
                            db
                            (Some ctx)
                            repoRoot
                            beforeRun
                            coveragePaths
                            afterRun
                            configs
                            affectedByProject
                            None

                    // executeTests still emits per-group TestProgress live; the
                    // synchronous handler emits Started + Completed for the
                    // §2a cache-write capture window.
                    ignore results
                    return TestsFinished(started, completed)
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

                return TestsFinished(started, completed)
        }

    let commands =
        [ "affected-tests",
          fun (_ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) (_args: string array) ->
              async {
                  // Compute on demand from state.ChangedSymbols against current DB
                  // state. ChangedSymbols accumulates across FileChecked events and
                  // is reset by flushAndQueryAffected on BuildCompleted.
                  let symbols = state.ChangedSymbols |> List.distinct

                  let tests =
                      if symbols.IsEmpty then
                          []
                      else
                          db.QueryAffectedTests(symbols)

                  let testsData =
                      tests
                      |> List.map (fun t ->
                          {| project = t.TestProject
                             ``class`` = t.TestClass
                             ``method`` = t.TestMethod |})

                  return JsonSerializer.Serialize(testsData)
              }

          "changed-files",
          fun (_ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) (_args: string array) ->
              async { return JsonSerializer.Serialize(state.ChangedFiles) }

          "test-results",
          fun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) (_args: string array) ->
              async {
                  if ctx.IsRunning "tests" then
                      return JsonSerializer.Serialize({| status = "running" |})
                  else
                      match state.LastResults with
                      | Some results -> return formatTestResultsJson results
                      | None -> return JsonSerializer.Serialize({| status = "not run" |})
              }

          "flaky-tests",
          fun (_ctx: PluginCtx<TestPruneMsg>) (_state: TestPruneState) (_args: string array) ->
              async {
                  let history = Flakiness.loadHistory (flakinessHistoryPath repoRoot)
                  let top = Flakiness.topFlaky 10 history

                  let payload =
                      top
                      |> List.map (fun (name, score) ->
                          let runs =
                              history |> Map.tryFind name |> Option.map List.length |> Option.defaultValue 0

                          {| name = name
                             flakiness = score
                             runs = runs |})

                  return JsonSerializer.Serialize({| tests = payload |})
              } ]

    // run-tests command (only if testConfigs are provided)
    let allCommands =
        match testConfigs with
        | Some allConfigs when not allConfigs.IsEmpty ->
            commands
            @ [ "run-tests",
                fun (ctx: PluginCtx<TestPruneMsg>) (state: TestPruneState) (args: string array) ->
                    async {
                        if ctx.IsRunning "tests" then
                            return JsonSerializer.Serialize({| error = "tests already running" |})
                        else
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
                                    let lastResults = state.LastResults

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
                                                        | TestsTimedOut _
                                                        // A deferred project never ran — `--only-failed`
                                                        // (rerun non-green projects) should pick it up.
                                                        | TestsDeferred _ -> Some name
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
                                        let! results, started, completed =
                                            executeTests
                                                db
                                                None
                                                repoRoot
                                                beforeRun
                                                coveragePaths
                                                afterRun
                                                configs
                                                Map.empty
                                                filter

                                        // Post rather than EmitTestRunCompleted directly — the
                                        // Custom(TestsFinished) handler also does error reporting and
                                        // status updates that a bare emit call would skip.
                                        ctx.Post(TestsFinished(started, completed))

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
                    let isIdle = not (ctx.IsRunning "tests")

                    let fileStr = AbsFilePath.value result.File
                    let relPath = Path.GetRelativePath(repoRoot, fileStr).Replace('\\', '/')

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

                    // Path D — fshw-owned per-file freshness sidecar gates the
                    // detectChanges call site. The F38 "withhold the symbol-DB
                    // write entirely" branch is gone: dirty FCS results no
                    // longer block persistence (cold-scan rows still go in).
                    // Instead the sidecar records `fcsClean = false` for the
                    // file so cross-restart Phase B replay treats those rows
                    // as untrusted-for-diff. The next clean recheck will both
                    // overwrite the rows with good extractions and flip the
                    // sidecar back to clean.
                    let currentClean =
                        not (hasFcsErrors ctx.FcsSuppressedCodes result.Source result.CheckResults)

                    let storedClean =
                        let store = Volatile.Read(&freshnessRef)
                        FileFreshness.isClean relPath store

                    if not currentClean then
                        let errCount =
                            fcsErrorCount ctx.FcsSuppressedCodes result.Source result.CheckResults

                        Logging.warn
                            "test-prune"
                            $"FCS reported %d{errCount} error(s) for %s{relPath}; persisting symbols but marking file dirty in freshness sidecar (Phase B detectChanges will fall back for this file)"

                    let! analysisResult =
                        analyzeSource ctx.Checker fileStr result.Source result.ProjectOptions projectName

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
                            |> List.filter (fun a -> not (a.Symbols |> List.exists (fun s -> s.SourceFile = relPath)))

                        let newPending =
                            state.PendingAnalysis
                            |> Map.add projectName (filteredExisting @ [ fileAnalysis ])

                        // Path D gate: only run detectChanges when both ends of
                        // the comparison are FCS-clean. If the sidecar says the
                        // stored rows ended their last session dirty, those
                        // rows may be partial / poisoned cold-scan extractions
                        // — comparing against them produces a phantom "all
                        // symbols changed" delta (the 4921-affected-tests
                        // Phase B regression). If the current FCS result is
                        // dirty, the just-extracted symbols themselves are
                        // suspect. Either side untrusted → bypass the diff
                        // and treat as "no change information for this file."
                        // The first clean-clean comparison (typically: warm
                        // recheck after BuildCompleted within the same
                        // session) restores the normal flow.
                        let (changedNames, suppressedDiff) =
                            if currentClean && storedClean then
                                // detectChanges filters externs internally; no pre-filter needed here.
                                let (changes, _events) = detectChanges normalizedSymbols storedSymbols

                                Logging.info
                                    "test-prune"
                                    $"detectChanges for %s{relPath}: %d{changes.Length} changes, %d{storedSymbols.Length} stored, %d{normalizedSymbols.Length} current"

                                changedSymbolNames changes, false
                            else
                                Logging.info
                                    "test-prune"
                                    $"detectChanges bypassed for %s{relPath} (currentClean=%b{currentClean}, storedClean=%b{storedClean}); falling back to no-diff for this file"

                                [], true

                        ignore suppressedDiff

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
                            |> List.fold (fun acc t -> Map.add t.TestClass fileStr acc) state.TestClassFiles

                        // AffectedTests is no longer eagerly populated here. The
                        // `affected-tests` IPC command computes it on demand from
                        // state.ChangedSymbols against the current DB. AffectedTests
                        // is set exclusively by flushAndQueryAffected on BuildCompleted
                        // and consumed by runTestsWithImpact.
                        let newState =
                            { state with
                                ChangedFiles = newChangedFiles
                                PendingAnalysis = newPending
                                ChangedSymbols = newChangedSymbols
                                TestClassFiles = newClassFiles }

                        // Keep the mutable snapshot in sync for the cache key function
                        Volatile.Write(&changedSymbolsRef, newState.ChangedSymbols)

                        // Stamp the freshness sidecar with the result of THIS check.
                        // Done after analysis (rather than at the very top) so a
                        // failed `analyzeSource` doesn't lock in a clean stamp for
                        // a file we have no symbols for.
                        //
                        // Item 3 gate: only `markClean` if we've observed a
                        // BuildCompleted in this session AND the current FCS
                        // result is clean. fshw's pipeline guarantees
                        // BuildCompleted reaches the mailbox before any
                        // FileChecked on a cold scan, so post-build clean
                        // stamps fire on the very first session — no
                        // two-session warm-up required. Otherwise
                        // `markUnverified` (won't downgrade a previously-clean
                        // entry to dirty — see FileFreshness.markUnverified).
                        let now = DateTime.UtcNow

                        let updatedFreshness =
                            let prior = Volatile.Read(&freshnessRef)

                            if currentClean && state.BuildCompletedInThisSession then
                                FileFreshness.markClean now relPath prior
                            else
                                FileFreshness.markUnverified relPath prior

                        updateFreshness updatedFreshness

                        if isIdle then
                            ctx.ReportStatus(Completed(DateTime.UtcNow))

                        return newState
                    | Error msg ->
                        Logging.error "test-prune" $"Analysis failed for %s{relPath}: %s{msg}"

                        if isIdle then
                            ctx.ReportStatus(PluginStatus.Failed($"Analysis failed: %s{msg}", DateTime.UtcNow))

                        return state

                | PluginEvent.BatchChecked _ ->
                    // Cohort-complete flush. Per-file accumulation already
                    // happened in the FileChecked handler; by the time we get
                    // here every FileChecked from this cohort has been folded
                    // into state.ChangedSymbols / state.PendingAnalysis
                    // (mailbox is FIFO and the daemon emits BatchChecked
                    // strictly after the last FileChecked).
                    //
                    // We persist PendingAnalysis to the DB here — this is the
                    // canonical persistence point, NOT BuildCompleted. On a
                    // cold scan, performScan awaits BuildPlugin terminal
                    // BEFORE running FCS tier checks (Daemon.fs:1195) so
                    // BuildCompleted reaches the TestPrune mailbox BEFORE any
                    // FileChecked. If the flush only ran on BuildCompleted, it
                    // would always fire against an empty PendingAnalysis on
                    // cold scans, leaving the symbol DB permanently empty.
                    // BatchChecked owning the flush makes the persistence
                    // independent of event ordering.
                    //
                    // BuildCompleted's flush is retained as an idempotent
                    // re-run (PendingAnalysis is already empty by then,
                    // RebuildProjects is skipped) plus the test-trigger.
                    let flushed =
                        try
                            Ok(flushAndQueryAffected state)
                        with ex ->
                            Error ex

                    match flushed with
                    | Error ex ->
                        Logging.error "test-prune" $"BatchChecked flushAndQueryAffected failed: %s{ex.Message}"
                        tryRepairSchemaDrift ex
                        return state
                    | Ok flushedState ->
                        Volatile.Write(&changedSymbolsRef, flushedState.ChangedSymbols)
                        return flushedState

                | PluginEvent.BuildCompleted buildResult ->
                    match buildResult with
                    | BuildSucceeded ->
                        // Item 3: record that BuildCompleted has fired this
                        // session. Subsequent FileChecked events are now
                        // allowed to promote the freshness sidecar to clean.
                        // Set unconditionally for both the queued-rerun and
                        // run-now branches — the gate is about "has the build
                        // process realized the reference graph yet", not
                        // about whether tests are queued.
                        let state =
                            { state with
                                BuildCompletedInThisSession = true }

                        if ctx.IsRunning "tests" then
                            // Leading "  ↳ " (↳) indents this entry one
                            // level beyond test-result lines in the activity-fold
                            // `recent:` view. The renderer adds 8 spaces to every
                            // tail entry; we add 2 more here so it visually nests
                            // under the in-flight test run rather than reading as
                            // a sibling of the test-result lines.
                            ctx.Log "  ↳ queued re-run (tests already running)"

                            Logging.info
                                "test-prune"
                                "BuildSucceeded received but tests already running — will re-run after"

                            return { state with PendingRerun = true }
                        else
                            Logging.info "test-prune" "BuildSucceeded: starting test run"

                            // Flush/query before announcing Running so the reported status never
                            // lies (the old order would flash Running even on schema-drifted DBs).
                            // The framework catches uncaught throws and forces Failed as a
                            // defense-in-depth net; we still trap locally here so we can run
                            // the schema-drift self-heal and preserve the idle transition.
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
                                match testConfigs with
                                | Some configs when not configs.IsEmpty ->
                                    ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))
                                    let hasCachedResults = state.LastResults.IsSome

                                    ctx.RunExclusive
                                        "tests"
                                        (runTestsWithImpact ctx configs stateWithAffected hasCachedResults)

                                    return stateWithAffected
                                | _ ->
                                    // No test configs — flush only; nothing to run.
                                    return stateWithAffected
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

                    // Issue 2: clear this plugin's prior-cycle diagnostics
                    // UNCONDITIONALLY before re-reporting, so `fshw errors` /
                    // the aggregate verdict reflects ONLY the most recent
                    // completed cycle. Previously the clear only happened on the
                    // all-passed branch; when a later cycle had a different set
                    // of failing projects, the superseded reds from the prior
                    // cycle were never cleared and accumulated as stale entries.
                    // ClearAllErrors == ClearPlugin "test-prune" (see
                    // PluginFramework), so this replaces the whole plugin ledger
                    // rather than only the all-pass case.
                    ctx.ClearAllErrors()

                    if not (testResults.Results |> Map.forall (fun _ r -> TestResult.isPassed r)) then
                        reportTestErrors ctx state.TestClassFiles testResults

                    // Pushing a terminal Completed/Failed status is what appends the
                    // run to history; both rerun and final-idle branches must call this.
                    let recordRunOutcome (results: TestResults) =
                        let total = results.Results.Count

                        // Non-green = anything not passed. Split into genuine
                        // failures vs deferred (never-ran) so the verdict can be
                        // honest: deferred is non-green but is "could not run /
                        // waiting on build", NOT "failed".
                        let nonGreen =
                            results.Results
                            |> Map.toList
                            |> List.filter (fun (_, r) -> not (TestResult.isPassed r))

                        let deferredList = nonGreen |> List.filter (fun (_, r) -> TestResult.isDeferred r)

                        let failedList =
                            nonGreen |> List.filter (fun (_, r) -> not (TestResult.isDeferred r))

                        let failed = failedList.Length
                        let deferred = deferredList.Length
                        let passed = total - failed - deferred

                        let anyFiltered =
                            results.Results |> Map.exists (fun _ r -> TestResult.wasFiltered r)

                        let selectedSuffix = if anyFiltered then "yes" else "no"

                        let timedOutProjects =
                            failedList
                            |> List.choose (fun (name, r) -> if TestResult.isTimedOut r then Some name else None)

                        // When 2+ projects ran and at least one has recorded elapsed,
                        // surface the slowest in the summary so a bottlenecked project
                        // is visible without having to query test-results JSON.
                        let slowestSuffix =
                            if total < 2 then
                                ""
                            else
                                let withElapsed =
                                    results.Results
                                    |> Map.toList
                                    |> List.choose (fun (name, r) ->
                                        let e = TestResult.elapsed r
                                        if e > TimeSpan.Zero then Some(name, e) else None)

                                match withElapsed with
                                | [] -> ""
                                | _ ->
                                    let (n, e) = withElapsed |> List.maxBy snd
                                    $", slowest: %s{n} %.1f{e.TotalSeconds}s"

                        let deferredSuffix =
                            if deferred > 0 then
                                $", %d{deferred} waiting on build"
                            else
                                ""

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
                                $"%d{passed} passed, %d{failed} failed%s{deferredSuffix} in %d{total} projects (selected: %s{selectedSuffix}%s{slowestSuffix})"

                            if failed = 0 && deferred = 0 then
                                ctx.ReportStatus(Completed(DateTime.UtcNow))
                            elif failed = 0 then
                                // Issue 1: only deferred projects — nothing FAILED,
                                // but nothing was verified either. Non-green, with an
                                // honest "waiting on build" message (never "failed").
                                let names = deferredList |> List.map fst |> String.concat ", "

                                ctx.ReportStatus(
                                    PluginStatus.Failed(
                                        $"%d{deferred} waiting on build (tests did not run): %s{names}",
                                        DateTime.UtcNow
                                    )
                                )
                            else
                                let names = failedList |> List.map fst |> String.concat ", "

                                let deferredNote =
                                    if deferred > 0 then
                                        let dn = deferredList |> List.map fst |> String.concat ", "
                                        $" (%d{deferred} waiting on build: %s{dn})"
                                    else
                                        ""

                                ctx.ReportStatus(
                                    PluginStatus.Failed(
                                        $"%d{failed} failed: %s{names}%s{deferredNote}",
                                        DateTime.UtcNow
                                    )
                                )

                    if state.PendingRerun then
                        Logging.info "test-prune" "Re-running tests (queued during previous run)"

                        // Flush any new pending analysis against CURRENT state — picking up any
                        // FileChecked symbols that landed between the queueing BuildCompleted
                        // and now. If the DB errors out here the rerun never happens, so we
                        // must bail back to idle (capturing testResults) instead of leaving
                        // PendingRerun stuck and the slot already freed.
                        match
                            (try
                                Ok(flushAndQueryAffected { state with PendingRerun = false })
                             with ex ->
                                 Error ex)
                        with
                        | Error ex ->
                            Logging.error "test-prune" $"flushAndQueryAffected (rerun) failed: %s{ex.Message}"
                            tryRepairSchemaDrift ex
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                            return
                                { state with
                                    LastResults = Some testResults
                                    PendingRerun = false
                                    ChangedFiles = []
                                    ChangedSymbols = []
                                    AffectedTests = Analyzed [] }
                        | Ok rerunState ->
                            recordRunOutcome testResults
                            Volatile.Write(&changedSymbolsRef, [])

                            ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

                            let rerunState =
                                { rerunState with
                                    LastResults = Some testResults
                                    PendingRerun = false }

                            match testConfigs with
                            | Some configs when not configs.IsEmpty ->
                                ctx.RunExclusive "tests" (runTestsWithImpact ctx configs rerunState true)
                            | _ -> ()

                            return rerunState
                    else
                        Volatile.Write(&changedSymbolsRef, [])
                        recordRunOutcome testResults

                        return
                            { state with
                                LastResults = Some testResults
                                ChangedFiles = []
                                ChangedSymbols = []
                                AffectedTests = Analyzed [] }

                | _ -> return state
            }
      Commands = allCommands
      Subscriptions =
        Set.ofList (
            // FileChecked: per-file analysis fold into PendingAnalysis /
            // changedSymbolsRef (unchanged from the pre-BatchChecked design).
            // BatchChecked: cohort-complete flush signal — fires after the
            // last FileChecked of a batch, before any subsequent BuildCompleted
            // racing the same change. By the time the agent processes
            // BatchChecked, every FileChecked update has been folded in, so
            // changedSymbolsRef is consistent with state.ChangedSymbols and
            // any cache key derived from it is well-formed. This is the seal
            // point that let the old `RequireWarmStart` gate retire (commit 4).
            // BuildCompleted is now subscribed unconditionally so the
            // Item 3 freshness-stamp gate works even when the plugin is
            // configured analysis-only (no testConfigs). When testConfigs is
            // None / empty, the BuildCompleted handler still runs
            // `flushAndQueryAffected` (idempotent on empty PendingAnalysis)
            // and skips the test-run path — see the
            // `match testConfigs with | Some configs when not configs.IsEmpty`
            // branch in the handler.
            [ SubscribeFileChecked; SubscribeBatchChecked; SubscribeBuildCompleted ]
        )
      CacheKey =
        // §2a: pure-content cache key. For BuildCompleted: changed symbols +
        // build outcome — together these dictate which tests run. For
        // FileChecked: file path + source content (TestPrune updates internal
        // symbol state from the source bytes).
        let cacheKey (event: PluginEvent<TestPruneMsg>) : ContentHash option =
            // Reuses the same merkle for BuildCompleted and Custom TestsFinished
            // so the cache writes on TestsFinished (synchronous handler — captures
            // EmittedEvents) and the next BuildCompleted hits via the matching key.
            // TestsFinished only fires after BuildSucceeded (BuildFailed short-circuits
            // earlier), so outcome="succeeded" is correct for the Custom path.
            let buildCompletedKey () =
                let symbolsHash =
                    Volatile.Read(&changedSymbolsRef)
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
                    Volatile.Read(&changedSymbolsRef)
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
                          "file", AbsFilePath.value r.File
                          "source", r.Source
                          "fcs-signature", fcsSignature ]
                )
            | _ -> None

        Some cacheKey
      Teardown = None }
