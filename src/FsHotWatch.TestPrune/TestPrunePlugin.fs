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

/// Stable first-line sentinel prefixed onto the recorded output of a project
/// whose FILTERED run matched ZERO tests. `test-rerun --filter-*` fans a raw
/// filter out to every test project; a project with no matching test runs zero
/// tests and is recorded as a (filtered) pass so it can't masquerade as a
/// failure. But a pass is indistinguishable from a REAL pass, which hides the
/// important "your filter selected nothing" case — making `test-rerun` look
/// like it force-ran when it ran nothing. This marker lets the `run-tests`
/// command detect a zero-match project STRUCTURALLY (no fragile re-grep of
/// runner output) and report `no-tests-matched` distinctly when EVERY project
/// in a filtered run matched nothing.
[<Literal>]
let ZeroMatchMarker = "[fshw:no-tests-matched] "

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

/// How fshw obtains the structured pass/fail report a test verdict is derived
/// from. The report (CTRF) — not the process exit code — is authoritative, but
/// only a runner that actually emits a parseable report can be trusted that
/// way, and an UNSUPPORTED `--report-*` flag is fatal (the runner exits
/// "invalid command line" and runs nothing). So injection of the report flag is
/// scoped by this setting.
type ReportVerificationFormat =
    /// Inject `--report-ctrf` iff the runner is detected as CTRF-capable
    /// (xUnit.v3, from the test project's package references), else fall back to
    /// the broad "is a dotnet command" heuristic. The default.
    | AutoDetect
    /// Always inject `--report-ctrf` (force-on for a capable runner the detector
    /// misses).
    | Ctrf
    /// Never inject a report flag — the process exit code is authoritative
    /// (force-off; e.g. a custom runner that would error on `--report-ctrf`).
    | Disabled

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
        /// How to obtain the structured test report for the verdict. Default
        /// `AutoDetect`. Override via `.fshw.json` `reportVerificationFormat`.
        ReportVerificationFormat: ReportVerificationFormat
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
        /// Per-test-project dependency fingerprint observed at the last
        /// `BuildCompleted`. A project whose fingerprint moves between builds had
        /// a dependency/binary change the symbol diff can't see, so its tests are
        /// force-run (dependency-fanout). Empty until the first build establishes
        /// the baseline. See `DependencyFanout`.
        PriorProjectFingerprints: Map<string, string>
        /// Test projects whose dependency fingerprint changed but whose force-run
        /// is deferred because a run was already in flight when the build landed.
        /// The queued rerun consumes (and clears) this so a dependency change that
        /// arrives mid-run is not lost. Unioned with the rerun's own fanout.
        PendingForceRunProjects: Set<string>
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
///
/// `launch` carries the set the run was LAUNCHED against — the queue snapshot
/// (`Symbols`) and, per launched symbol, the set of test PROJECTS whose tests
/// cover it (`CoveringProjectsBySymbol`) — both captured at dispatch time. The
/// synchronous `Custom(TestsFinished)` handler uses this, NOT the live
/// `state.AffectedTests`/`state.ChangedSymbols` (which mid-run `BatchChecked`
/// flushes overwrite), to decide per-symbol green-commit: a symbol leaves the
/// queue only when EVERY project covering it passed. A symbol with NO covering
/// projects (empty set) is committed unconditionally at flush time — there's
/// nothing to wait for. `Symbols` empty for the degenerate zero-affected skip.
type TestRunLaunch =
    { Symbols: Set<string>
      CoveringProjectsBySymbol: Map<string, Set<string>> }

type TestPruneMsg = TestsFinished of started: TestRunStarted * completed: TestRunCompleted * launch: TestRunLaunch

/// Translate a repo-root-relative glob (`*`, `?`, `**`, `/`) into a regex
/// anchored against a repo-relative path. `**` matches across directory
/// separators (including none); a single `*`/`?` does NOT cross `/`. A trailing
/// `dir/**` also matches `dir` itself (zero segments). Paths are normalised to
/// `/` before matching so the same config works on every OS.
let internal dependsOnGlobToRegex (glob: string) : System.Text.RegularExpressions.Regex =
    // Collapse a trailing `/**` to a sentinel so the leading `/` becomes
    // optional (`dir/**` matches `dir`, `dir/x`, `dir/x/y`). Done before the
    // char scan so the `/` isn't emitted as a mandatory literal.
    let normalized = glob.Replace('\\', '/').TrimStart('/')

    let normalized, trailingDoubleStar =
        if normalized.EndsWith("/**") then
            normalized.Substring(0, normalized.Length - 3), true
        else
            normalized, false

    let sb = System.Text.StringBuilder()
    sb.Append('^') |> ignore
    let mutable i = 0

    while i < normalized.Length do
        let c = normalized.[i]

        if c = '*' then
            if i + 1 < normalized.Length && normalized.[i + 1] = '*' then
                // `**` — match any chars including `/`. A `**/ ` (cross-dir,
                // zero-or-more leading segments) makes the following separator
                // optional so `a/**/b` matches `a/b` too.
                if i + 2 < normalized.Length && normalized.[i + 2] = '/' then
                    sb.Append("(?:.*/)?") |> ignore
                    i <- i + 3
                else
                    sb.Append(".*") |> ignore
                    i <- i + 2
            else
                // single `*` — any run of non-separator chars
                sb.Append("[^/]*") |> ignore
                i <- i + 1
        elif c = '?' then
            sb.Append("[^/]") |> ignore
            i <- i + 1
        else
            sb.Append(System.Text.RegularExpressions.Regex.Escape(string c)) |> ignore
            i <- i + 1

    // Re-attach the trailing `/**`: an OPTIONAL `/<anything>` so the bare dir
    // and any descendant both match.
    if trailingDoubleStar then
        sb.Append("(?:/.*)?") |> ignore

    sb.Append('$') |> ignore

    System.Text.RegularExpressions.Regex(
        sb.ToString(),
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ||| System.Text.RegularExpressions.RegexOptions.CultureInvariant
    )

/// Resolve the on-disk files under `repoRoot` whose repo-relative path matches
/// any of the `dependsOn` globs. Deterministic: returns sorted, distinct
/// absolute paths. Globs that match nothing contribute nothing; a glob that is
/// a plain existing file path resolves to that one file. Directory enumeration
/// errors are swallowed (best-effort) so a transient IO hiccup can't crash the
/// cache-key computation.
let internal resolveDependsOnFiles (repoRoot: string) (dependsOn: string list) : string list =
    if dependsOn.IsEmpty then
        []
    else
        let rootFull = Path.GetFullPath(repoRoot)

        let toRel (abs: string) =
            Path.GetRelativePath(rootFull, abs).Replace('\\', '/')

        // A plain glob with no wildcard meta is a direct file reference — resolve
        // it without walking the whole tree (cheap + handles files outside any
        // enumerable subdir uniformly).
        let isLiteral (g: string) =
            not (g.Contains('*') || g.Contains('?'))

        let literalHits =
            dependsOn
            |> List.filter isLiteral
            |> List.choose (fun g ->
                let abs =
                    Path.GetFullPath(Path.Combine(rootFull, g.Replace('\\', '/').TrimStart('/')))

                if File.Exists abs then Some abs else None)

        let globPatterns =
            dependsOn |> List.filter (isLiteral >> not) |> List.map dependsOnGlobToRegex

        let globHits =
            if globPatterns.IsEmpty then
                []
            elif not (Directory.Exists rootFull) then
                []
            else
                let allFiles =
                    try
                        Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories)
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> Seq.empty

                allFiles
                |> Seq.filter (fun abs ->
                    let rel = toRel abs
                    globPatterns |> List.exists (fun rx -> rx.IsMatch(rel)))
                |> Seq.toList

        (literalHits @ globHits) |> List.distinct |> List.sort

/// Deterministic content hash of the files matched by the `dependsOn` globs.
/// Editing, adding, or deleting a matched file changes this hash, which salts
/// the test cache key so an external input (a DB migration, a generated file,
/// a schema) that test-prune's symbol diff can't see still invalidates a stale
/// cached test verdict. Empty `dependsOn` → empty string (NO salt: the key is
/// byte-identical to the pre-feature key, so existing caches keep hitting).
/// Missing files are skipped; a glob matching nothing contributes nothing.
let internal externalDependencyHash (repoRoot: string) (dependsOn: string list) : string =
    let files = resolveDependsOnFiles repoRoot dependsOn

    if files.IsEmpty then
        ""
    else
        let sb = System.Text.StringBuilder()

        for path in files do
            let rel = Path.GetRelativePath(Path.GetFullPath(repoRoot), path).Replace('\\', '/')

            let h =
                try
                    FsHotWatch.CheckCache.sha256Hex (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes path))
                with
                | :? IOException
                | :? UnauthorizedAccessException -> "unreadable"

            sb.Append(rel.Length) |> ignore
            sb.Append(':') |> ignore
            sb.Append(rel) |> ignore
            sb.Append('@') |> ignore
            sb.Append(h) |> ignore
            sb.Append('\n') |> ignore

        FsHotWatch.CheckCache.sha256Hex (sb.ToString())

/// True when this result is a zero-match-under-filter pass (the runner found no
/// test matching the active `--filter-*`). Detected structurally via the
/// `ZeroMatchMarker` prefix `executeTests` stamps on such results.
let internal isZeroMatchResult (result: TestResult) : bool =
    match result with
    | TestsPassed(o, _, _) -> o.StartsWith(ZeroMatchMarker, StringComparison.Ordinal)
    | _ -> false

/// True when a run matched NO tests anywhere: every project was a
/// zero-match-under-filter pass (and there was at least one project). Lets the
/// `run-tests` command report `no-tests-matched` distinctly instead of a green
/// that actually executed nothing — the "test-rerun didn't force" symptom.
let internal allZeroMatch (results: TestResults) : bool =
    not results.Results.IsEmpty
    && results.Results |> Map.forall (fun _ r -> isZeroMatchResult r)

let private formatTestResultsJson (results: TestResults) =
    let projects =
        results.Results
        |> Map.toList
        |> List.map (fun (name, result) ->
            let (status, output) =
                match result with
                // A zero-match-under-filter pass gets a DISTINCT status so a
                // consumer can tell "ran, all green" from "matched nothing".
                // Strip the internal marker from the surfaced output.
                | TestsPassed(o, _, _) when o.StartsWith(ZeroMatchMarker, StringComparison.Ordinal) ->
                    ("no-tests-matched", o.Substring(ZeroMatchMarker.Length))
                | TestsPassed(o, _, _) -> ("passed", o)
                | TestsFailed(o, _, _) -> ("failed", o)
                | TestsTimedOut(o, _, _, _) -> ("timed-out", o)
                | TestsDeferred reason -> ("deferred", reason)
                | TestsErrored reason -> ("errored", reason)

            {| project = name
               status = status
               output = truncateOutput 200 output
               elapsedMs = (TestResult.elapsed result).TotalMilliseconds |})

    JsonSerializer.Serialize(
        {| elapsed = $"%.1f{results.Elapsed.TotalSeconds}s"
           // `noTestsMatched` is true iff EVERY project matched zero tests under
           // the active filter — the run-level distinct signal the CLI renders.
           noTestsMatched = allZeroMatch results
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

/// Microsoft.Testing.Platform exit code for "no tests matched / zero tests ran".
[<Literal>]
let internal zeroTestsExitCode = 8

/// True when a *filtered* run matched no tests in this project. An explicit
/// `--filter-*` passthrough (run-tests / test-rerun) is fanned out to EVERY test
/// project; a project that has no test matching the filter runs zero tests and
/// the runner exits non-zero (MTP uses exit code 8, `ZeroTests`). That is NOT a
/// test failure — the tests simply don't exist for this filter — so it must be
/// treated like an impact-skip (passed/filtered, contributing no coverage),
/// exactly as a template-filtered project with no affected classes already is.
///
/// Gated on `wasFiltered`: an UNFILTERED project that runs zero tests is a real
/// problem (misconfigured runner, empty suite) and must still surface, so this
/// returns false for it. Detection is structural (the canonical exit code) with
/// a text fallback for runners that exit non-zero without emitting code 8 but
/// still print MTP's zero-tests summary line.
let internal isZeroTestsUnderFilter (wasFiltered: bool) (outcome: ProcessOutcome) : bool =
    wasFiltered
    && match outcome with
       | ProcessOutcome.Failed(code, _) when code = zeroTestsExitCode -> true
       | ProcessOutcome.Failed(_, output) -> output.Contains("Zero tests ran", StringComparison.OrdinalIgnoreCase)
       | _ -> false

/// Build the human-readable error lines for a FAILED test project run, parsed
/// from the runner's captured `output`. The header line plus the per-test
/// `failed ...` lines plus the MTP summary lines (`total:`/`failed:`/
/// `succeeded:`).
///
/// CRITICAL for CI observability: a red run that only reports "failed: 1" with
/// NO test name is undiagnosable when the on-disk `.fshw/test-runs` log isn't
/// uploaded as an artifact. MTP prints a failing test as a line whose TRIMMED
/// form starts with `failed ` — INCLUDING `failed (canceled) <name> (Nms)` for a
/// test killed by its `[<Fact(Timeout=...)>]` under CI load (the documented
/// daemon-load flake class). We match the trimmed prefix so leading indentation
/// (which varies by MTP version / capture path) never hides the name. As a
/// backstop, when the run failed but NO `failed ` line parsed (a crash, an
/// OOM-kill, or an output shape the matcher doesn't yet recognise), the tail of
/// the captured output is echoed so the failure is ALWAYS visible from the CI
/// console alone — never silently swallowed into "0 test(s) failed".
let internal formatFailureReport (projectName: string) (output: string) : string list =
    let lines = output.Split('\n')

    let isFailedLine (l: string) = l.TrimStart().StartsWith("failed ")

    let failedTests = lines |> Array.filter isFailedLine |> Array.toList

    let summaryLines =
        lines
        |> Array.filter (fun l ->
            let t = l.TrimStart()

            t.StartsWith("Test run summary:")
            || t.Contains("total:")
            || t.Contains("failed:")
            || t.Contains("succeeded:"))
        |> Array.filter (isFailedLine >> not)
        |> Array.toList

    [ $"%s{projectName}: %d{failedTests.Length} test(s) failed:"
      yield! failedTests |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      yield! summaryLines |> List.map (fun l -> $"  %s{l.TrimEnd()}")
      if List.isEmpty failedTests then
          $"%s{projectName}: run failed but no per-test 'failed' line was parsed — dumping last output lines so the failure is visible without the saved log:"

          let tail =
              lines
              |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
              |> (fun ls -> ls.[max 0 (ls.Length - 40) ..])

          yield! tail |> Array.map (fun l -> $"  | %s{l.TrimEnd()}") |> Array.toList ]

/// What is known about the structured test report for a run — the verdict
/// input. Modelled as a DU rather than a `bool * report option` so the
/// meaningless "no report was requested yet somehow a parsed report exists"
/// state cannot be constructed.
type ReportEvidence =
    /// No report was requested (an unknown / unsupported runner) — the process
    /// exit code is the only pass/fail signal available.
    | NoReportRequested
    /// A report WAS requested from a capable runner. `Some` carries the parsed
    /// summary; `None` means the file was absent / unreadable / unparseable —
    /// the host aborted before flushing, or wrote a truncated report.
    | ReportRequested of report: Flakiness.TestReport option

/// Decide a single project's verdict. The structured test report (when present
/// and parseable) is AUTHORITATIVE for pass/fail; the process exit code is only
/// a tie-break when there is no usable report. This inverts the prior logic
/// (exit-code-only), which produced false REDs: a test host that exits non-zero
/// during a dirty shutdown (e.g. the Microsoft.Testing.Platform exit-7 flake)
/// after flushing a clean report was reported as "Tests failed" with zero named
/// tests, while `test-rerun` came back green.
///
/// Precedence (apphost-missing / zero-match-under-filter are handled by the
/// caller BEFORE this — they are not test outcomes):
///   1. report has any failed/other result → `TestsFailed` (red). Exit irrelevant.
///   2. report is all-clear (no failed/other) AND ran ≥1 test → `TestsPassed`
///      (green) EVEN IF the process exited non-zero — the flake case.
///   3. no usable report (absent / unparseable / no summary) AND exit ≠ 0:
///        - report WAS requested from a capable runner → `TestsErrored`: the host
///          aborted before writing results; nothing was verified. Never green,
///          never the misleading "tests failed".
///        - report NOT requested (unknown runner) → exit code is the only signal
///          we have → `TestsFailed` (unchanged behaviour, no regression).
///   4. no usable report AND exit = 0 → trust the clean exit → `TestsPassed`.
///   A `summary.tests == 0` report that reaches here is an UNFILTERED zero-test
///   run (the filtered case was handled upstream) — a real misconfiguration, so
///   it falls to the exit-code tie-break rather than going green.
///
/// TODO(approach C, deferred): once the set of benign shutdown exit codes is
/// confirmed per runner/version, outcome 2 could additionally require the exit
/// code to be 0 or a whitelisted shutdown code (e.g. MTP's 7), treating other
/// non-zero exits with a clean report as errored. Left out for now — the exact
/// benign code is runner/version-specific and a report that positively shows
/// zero failures is stronger evidence than the exit number.
let internal classifyTestOutcome
    (evidence: ReportEvidence)
    (wasFiltered: bool)
    (elapsed: System.TimeSpan)
    (outcome: ProcessOutcome)
    : TestResult =
    match outcome with
    | ProcessOutcome.TimedOut(after, output) ->
        // A timeout KILL is a real "stuck" signal; a partial report it may have
        // flushed must not override it. Keep it distinct (unchanged).
        TestsTimedOut(output, after, wasFiltered, elapsed)
    | _ ->
        let output = outputOf outcome
        let succeeded = isSucceeded outcome

        match evidence with
        | ReportRequested(Some r) when r.Failed > 0 || r.Other > 0 ->
            // Outcome 1: the report names failing/errored tests — authoritative red.
            TestsFailed(output, wasFiltered, elapsed)
        | ReportRequested(Some r) when Flakiness.TestReport.allClear r && r.Total > 0 ->
            // Outcome 2: report parsed, zero non-pass results, ≥1 test ran →
            // GREEN even if the process exited non-zero (the dirty-shutdown flake).
            TestsPassed(output, wasFiltered, elapsed)
        | ReportRequested(Some _) ->
            // Report parsed but Total == 0 — an unfiltered zero-test run. Real
            // problem; defer to the exit code (preserves "empty suite is red").
            if succeeded then
                TestsPassed(output, wasFiltered, elapsed)
            else
                TestsFailed(output, wasFiltered, elapsed)
        | _ when succeeded ->
            // Outcome 4: clean exit and no usable report (none requested, or
            // requested-but-absent) → trust the pass.
            TestsPassed(output, wasFiltered, elapsed)
        | ReportRequested None ->
            // Outcome 3: we asked a capable runner for a report, the process
            // exited non-zero, and none is parseable → the host aborted before
            // writing results. Errored — never green, never "tests failed".
            TestsErrored "test host exited non-zero but wrote no parseable report — nothing verified"
        | NoReportRequested ->
            // Unknown runner we never asked for a report: exit code is the only
            // signal. Preserve today's behaviour (no false-errored regression).
            TestsFailed(output, wasFiltered, elapsed)

/// Split runner args on whitespace into tokens (empty entries removed).
let private argTokens (args: string) : string[] =
    if String.IsNullOrWhiteSpace args then
        [||]
    else
        args.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

/// The value following `--project`/`-p` in the tokenized args, quote-trimmed.
/// Shared by `tryApphostPresent` and `detectCtrfCapable` (both derive a project
/// from the runner command line the same way).
let private projectFlagValue (tokens: string[]) : string option =
    tokens
    |> Array.tryFindIndex (fun t -> t = "--project" || t = "-p")
    |> Option.bind (fun i -> if i + 1 < tokens.Length then Some tokens.[i + 1] else None)
    |> Option.map (fun raw -> raw.Trim('"'))

/// Derive `(projDir, assemblyName, binDir)` from a runner's `--project` arg, or
/// `None` when no `--project`/`-p` token is present (a custom, non-`dotnet run`
/// command). `binDir` is `<projDir>/bin/Debug`. The `--project` value may point
/// at a `.fsproj`/`.csproj` file OR a directory; the assembly name defaults to
/// the project/dir leaf — matching `ProjectGraph.GetCanonicalDllPath`, which
/// uses the project file's base name. Shared by `tryApphostPresent` (presence)
/// and `apphostStale` (freshness) so this fsproj-or-directory derivation has ONE
/// definition.
let private deriveProjectBin (args: string) (repoRoot: string) : (string * string * string) option =
    projectFlagValue (argTokens args)
    |> Option.map (fun proj ->
        // Resolve to an absolute path (relative paths are repoRoot-relative).
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

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

        projDir, assemblyName, Path.Combine(projDir, "bin", "Debug"))

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
/// Presence only — `apphostStale` is the freshness complement (a PRESENT but
/// out-of-date artifact).
///
/// Returns:
///   Some true  — project derivable AND apphost present
///   Some false — project derivable AND apphost absent (the deferred signal)
///   None       — could not derive a project from args (e.g. a non-`dotnet run`
///                custom command); caller falls back to the output sniff.
let internal tryApphostPresent (args: string) (repoRoot: string) : bool option =
    match deriveProjectBin args repoRoot with
    | None -> None
    | Some(_, assemblyName, binDir) ->
        if not (Directory.Exists binDir) then
            // No build output at all yet — apphost definitionally absent.
            Some false
        else
            // The apphost lives at bin/Debug/<tfm>/<assemblyName>(.exe). We don't
            // know the TFM, so scan every TFM dir for the extension-less binary
            // (Unix) or the `.exe` (Windows).
            Directory.GetDirectories(binDir)
            |> Array.exists (fun tfmDir ->
                File.Exists(Path.Combine(tfmDir, assemblyName))
                || File.Exists(Path.Combine(tfmDir, assemblyName + ".exe")))
            |> Some

/// Source-file extensions whose mtime feeds the freshness gate — the F#/C#
/// compile inputs whose edit should force a rebuild before tests are trusted.
let private freshnessSourceExtensions = set [ ".fs"; ".fsi"; ".fsx"; ".cs" ]

/// Directory names never walked when computing the newest source mtime: build
/// output (where a regenerated obj/ file could masquerade as a newer "source"),
/// VCS, and tooling state.
let private freshnessExcludedDirs =
    set
        [ "bin"
          "obj"
          ".git"
          ".jj"
          ".hg"
          ".svn"
          ".fshw"
          ".vs"
          ".idea"
          "node_modules"
          ".workspaces" ]

/// Newest `LastWriteTimeUtc` across the on-disk source files under `repoRoot`
/// (recursive; build-output and VCS/tooling dirs skipped). `None` when no source
/// file exists. Mirrors the LOGIC of `ProjectGraph.GetMaxSourceMtime` (max of
/// `File.GetLastWriteTimeUtc`) but is self-contained — `executeTests` is
/// deliberately graph-free (shared with the one-off `run-tests` command), so the
/// freshness gate cannot reach `IProjectGraphReader`. Scanning the whole repo is
/// the conservative superset of any single test project's dependency-closure
/// sources: an edit ANYWHERE that a (whole-solution) build hasn't yet re-emitted
/// leaves the test artifact older than that edit. Per-subtree IO errors are
/// swallowed (best-effort) so a transient hiccup can't crash the gate.
let internal newestSourceMtime (repoRoot: string) : DateTime option =
    let rec walk (dir: string) : DateTime list =
        let filesHere =
            try
                Directory.GetFiles dir
                |> Array.choose (fun f ->
                    if freshnessSourceExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) then
                        Some(File.GetLastWriteTimeUtc f)
                    else
                        None)
                |> Array.toList
            with
            | :? IOException
            | :? UnauthorizedAccessException -> []

        let fromSubdirs =
            try
                Directory.GetDirectories dir
                |> Array.filter (fun d -> not (freshnessExcludedDirs.Contains(Path.GetFileName d)))
                |> Array.toList
                |> List.collect walk
            with
            | :? IOException
            | :? UnauthorizedAccessException -> []

        filesHere @ fromSubdirs

    if not (Directory.Exists repoRoot) then
        None
    else
        match walk repoRoot with
        | [] -> None
        | times -> Some(List.max times)

/// Freshness complement to `tryApphostPresent`: `true` when the test project's
/// canonical `<assemblyName>.dll` (the managed assembly `dotnet run --no-build`
/// executes — the same artifact `ProjectGraph.GetCanonicalDllPath` names) EXISTS
/// but PREDATES the newest source. Running `--no-build` then executes STALE code
/// and yields a verdict (pass OR fail) from bits that don't match the sources —
/// the false-green/false-red this gate prevents.
///
/// Mirrors `BuildPlugin.verifyArtifactsFresh`'s `DllOlderThanSources` arm (see
/// ADR-008): mtime is the right *temporal* signal — "was the artifact re-emitted
/// AFTER the newest source?" A real edit bumps the source mtime, so `dll < source`
/// is exactly the tell that the build did not run (or ran `--no-build`). We probe
/// the managed DLL, not the apphost: the apphost is a thin native launcher whose
/// mtime can lag an incremental rebuild, whereas the DLL always re-stamps on
/// recompile — so it is the reliable freshness signal (and the one
/// `verifyArtifactsFresh` checks).
///
/// `false` (treat as runnable) when: the project isn't derivable from args, no
/// build output exists (absence is `tryApphostPresent`'s job — kept on the
/// retry-friendly cold-start path, since a build in flight may still land the
/// apphost; a stale one won't refresh without a real build), or there are no
/// sources to be stale against.
let internal apphostStale (args: string) (repoRoot: string) : bool =
    match deriveProjectBin args repoRoot with
    | None -> false
    | Some(_, assemblyName, binDir) ->
        if not (Directory.Exists binDir) then
            false
        else
            let dllMtimes =
                Directory.GetDirectories binDir
                |> Array.choose (fun tfmDir ->
                    let dll = Path.Combine(tfmDir, assemblyName + ".dll")

                    if File.Exists dll then
                        Some(File.GetLastWriteTimeUtc dll)
                    else
                        None)
                |> Array.toList

            match newestSourceMtime repoRoot, dllMtimes with
            // The NEWEST built DLL still predates the newest source ⇒ even the
            // most recent build ran before the last edit ⇒ stale. Using the max
            // (most-recent) DLL mtime is conservative against false-stale.
            | Some srcMtime, (_ :: _ as mtimes) -> List.max mtimes < srcMtime
            | _ -> false

/// Detect whether a test runner emits a CTRF report we can parse. The
/// `--report-ctrf` family is provided by xUnit.v3's runner (NOT the MTP core),
/// and an UNSUPPORTED `--report-*` flag is fatal — the runner exits "invalid
/// command line" and runs zero tests. So we positively identify xUnit.v3 from
/// the test project file's package references before injecting the flag.
///
/// Returns `Some true`/`Some false` when a project file is located and read
/// (mentions xunit or not), and `None` when no project file can be derived from
/// the args (a custom runner, or a `--project`-less command) — the caller treats
/// `None` as "fall back to the broad dotnet heuristic", preserving existing
/// behaviour without risking a fatal flag on a positively-non-xunit runner.
let internal detectCtrfCapable (args: string) (repoRoot: string) : bool option =
    let tokens = argTokens args

    let looksLikeProjectFile (t: string) =
        t.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || t.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)

    // The project hint: the value after --project/-p, else any token that is
    // itself a project file path (e.g. `dotnet test path/to/Proj.fsproj`).
    let projArg =
        projectFlagValue tokens
        |> Option.orElse (
            tokens
            |> Array.tryFind looksLikeProjectFile
            |> Option.map (fun raw -> raw.Trim('"'))
        )

    let resolveProjectFile (proj: string) : string option =
        let abs =
            if Path.IsPathRooted proj then
                proj
            else
                Path.Combine(repoRoot, proj)

        if File.Exists abs && looksLikeProjectFile abs then
            Some abs
        elif Directory.Exists abs then
            Directory.GetFiles(abs, "*.fsproj")
            |> Array.append (Directory.GetFiles(abs, "*.csproj"))
            |> Array.tryHead
        else
            None

    projArg
    |> Option.bind resolveProjectFile
    |> Option.bind (fun projFile ->
        try
            // xUnit is a DIRECT package reference in a test project, so a text
            // probe of the project file is sufficient and build-independent.
            Some((File.ReadAllText projFile).Contains("xunit", StringComparison.OrdinalIgnoreCase))
        with _ ->
            None)

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
            | TestsErrored reason ->
                // NOT a test failure (no test was shown to fail) and NOT a pass
                // (nothing was verified) — an honest "errored" diagnostic so the
                // verdict is non-green without the misleading "Tests failed in X".
                [ $"<tests/%s{project}>",
                  ErrorLedger.ErrorEntry.errorWithDetail
                      $"%s{project}: errored — %s{reason}"
                      $"The %s{project} test host exited non-zero but wrote no parseable test report, so NO pass/fail verdict could be derived — nothing was verified. This is NOT a reported test failure and NOT a pass; re-run (e.g. `dotnet fshw test-rerun`). A run that only goes green on retry is itself a real failure, so this stays non-green." ]
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
                        elif apphostStale config.Args repoRoot then
                            // FRESHNESS GATE (mirrors BuildPlugin.verifyArtifactsFresh; ADR-008).
                            // The test project's compiled artifact PREDATES the newest source, so
                            // `dotnet run --no-build` would execute STALE bits and report a verdict
                            // (pass OR fail) that doesn't match the sources — the false-green this
                            // exists to prevent. Runs PRE-launch, independent of exit code: the old
                            // apphost check only fired on a FAILED launch (`detectApphostMissing`
                            // short-circuits on a clean exit), so a stale apphost that exited 0
                            // sailed through as `TestsPassed`. DEFER without launching — exactly the
                            // "waiting on build" signal a MISSING apphost yields — so a stale
                            // artifact can never produce a passing verdict. (A MISSING apphost stays
                            // on the launch+retry+defer path below: a build in flight may still land
                            // it; a stale one won't refresh without a real build.)
                            Logging.warn
                                "test-prune"
                                $"%s{config.Project}: compiled artifact is STALE (older than newest source) — deferring as 'waiting on build', NOT running --no-build on stale code (mirrors BuildPlugin.verifyArtifactsFresh; ADR-008)"

                            ctx
                            |> Option.iter (fun c ->
                                c.Log $"{config.Project}: waiting on build (compiled artifact is stale)")

                            results <-
                                (config.Project,
                                 TestsDeferred
                                     "compiled artifact is stale (older than newest source) — would run --no-build on stale code")
                                :: results
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

                            // xUnit.v3's runner supports `--report-ctrf`, which fshw
                            // reads back as the AUTHORITATIVE pass/fail verdict (and for
                            // flakiness history). An UNSUPPORTED `--report-*` flag is
                            // FATAL (the runner exits "invalid command line" and runs
                            // zero tests), so injection is scoped: `Disabled` never
                            // injects, `Ctrf` always does, `AutoDetect` injects iff the
                            // runner is detected as xUnit (from the project's package
                            // refs) and otherwise falls back to the broad "is a dotnet
                            // command" heuristic — non-dotnet test fixtures (sleep, echo)
                            // are thereby never given the flag.
                            let isDotnetCommand (cmd: string) =
                                let leaf = Path.GetFileNameWithoutExtension(cmd)
                                leaf = "dotnet"

                            let shouldRequestCtrf =
                                match config.ReportVerificationFormat with
                                | Disabled -> false
                                | Ctrf -> true
                                | AutoDetect ->
                                    match detectCtrfCapable config.Args repoRoot with
                                    | Some capable -> capable
                                    | None -> isDotnetCommand config.Command

                            let ctrfPath =
                                if shouldRequestCtrf then
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

                            // A filtered run that matched zero tests in this project is
                            // not a failure (see `isZeroTestsUnderFilter`) — treat it
                            // like an impact-skip.
                            let zeroTestsUnderFilter = isZeroTestsUnderFilter wasFiltered processResult

                            let output = outputOf processResult

                            // Read the structured report ONCE (when one was requested
                            // from a capable runner). It is BOTH the authoritative
                            // pass/fail signal (summary counts) AND the flakiness
                            // source (per-test records). Read BEFORE the verdict so
                            // the REPORT — not the exit code — decides green/red.
                            let reportJson =
                                match ctrfPath with
                                | Some p ->
                                    try
                                        Some(File.ReadAllText p)
                                    with
                                    | :? IOException
                                    | :? UnauthorizedAccessException -> None
                                | None -> None

                            let reportEvidence =
                                match ctrfPath with
                                | None -> NoReportRequested
                                | Some _ -> ReportRequested(reportJson |> Option.bind Flakiness.tryParseReport)

                            let result =
                                if apphostMissing then
                                    // Tests NEVER RAN — the apphost wasn't produced.
                                    // A dedicated `TestsDeferred`, NOT a pass
                                    // (`isPassed`=false → never a silent false-green)
                                    // and NOT a real failure: surfaced as an honest
                                    // "waiting on build" diagnostic. Carries no
                                    // elapsed/wasFiltered, so it never lowers a
                                    // coverage baseline.
                                    TestsDeferred "apphost not produced; tests did not run"
                                elif zeroTestsUnderFilter then
                                    // A filtered run matched no tests here — record
                                    // like an impact-skip (passed + filtered), NOT a
                                    // failure. The ZeroMatchMarker lets `run-tests`
                                    // report `no-tests-matched` distinctly.
                                    TestsPassed(ZeroMatchMarker + output, true, projectElapsed)
                                else
                                    // Report-authoritative verdict (exit code only a
                                    // tie-break). Fixes the false-RED: a dirty-shutdown
                                    // non-zero exit with a clean report is GREEN; a
                                    // non-zero exit with NO parseable report is ERRORED
                                    // (never a misleading "tests failed").
                                    classifyTestOutcome reportEvidence wasFiltered projectElapsed processResult

                            // Log driven off the AUTHORITATIVE verdict (not the raw
                            // exit code) so the console line can never disagree with
                            // the recorded result.
                            match result with
                            | TestsDeferred _ ->
                                logToCtx $"{config.Project}: waiting on build (apphost not yet produced)"

                                Logging.warn
                                    "test-prune"
                                    $"%s{config.Project}: apphost still missing after retry — surfacing as 'waiting on build', not FAILED (a build-ordering issue, never a test failure)"
                            | TestsPassed(o, _, _) when o.StartsWith(ZeroMatchMarker, StringComparison.Ordinal) ->
                                logToCtx $"{config.Project}: no tests matched the filter — skipped"

                                Logging.info
                                    "test-prune"
                                    $"%s{config.Project}: no tests matched the active filter — skipped, not FAILED (a filtered run that selects nothing here is not a test failure)"
                            | TestsPassed _ ->
                                logToCtx $"{config.Project}: passed"
                                Logging.info "test-prune" $"%s{config.Project}: PASSED"
                            | TestsErrored reason ->
                                logToCtx $"{config.Project}: errored — {reason}"

                                Logging.error
                                    "test-prune"
                                    $"%s{config.Project}: ERRORED — %s{reason}. Nothing was verified; this is NOT a test failure and NOT a pass — re-run (e.g. `dotnet fshw test-rerun`)."
                            | TestsFailed _
                            | TestsTimedOut _ ->
                                logToCtx $"{config.Project}: failed"
                                Logging.error "test-prune" $"%s{config.Project}: FAILED"

                            // Persist the raw output for any non-clean run (Failed,
                            // TimedOut, Errored) so the CI console has the diagnostic
                            // even when `.fshw/test-runs` isn't uploaded as an artifact.
                            match result with
                            | TestsFailed _
                            | TestsTimedOut _
                            | TestsErrored _ ->
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

                                for line in formatFailureReport config.Project output do
                                    Logging.error "test-prune" line
                            | _ -> ()

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

                            // Per-test flakiness tracking: reuse the report content
                            // already read for the verdict; append per-test records to
                            // the rolling history file (capped at 20 runs per test).
                            // Best-effort — exceptions never fail the run.
                            match ctrfPath, reportJson with
                            | Some p, Some json ->
                                try
                                    let records = Flakiness.parseCtrfTests json

                                    if not records.IsEmpty then
                                        Flakiness.appendRecords (flakinessHistoryPath repoRoot) 20 records

                                    File.Delete p
                                with
                                | :? IOException
                                | :? UnauthorizedAccessException
                                | :? JsonException as ex ->
                                    Logging.warn "test-prune" $"flakiness: failed to record run: %s{ex.Message}"
                            | Some p, None ->
                                // Report requested but unreadable (missing — the host
                                // aborted before flushing — or locked). Best-effort
                                // cleanup; absence already drove the Errored verdict.
                                try
                                    File.Delete p
                                with _ ->
                                    ()
                            | None, _ -> ()

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

/// Delete the FCS check cache (`.fshw/cache/*.json`) for `repoRoot`, returning the number
/// of entries removed. Called when the TestPrune symbol DB was recreated (a schema bump):
/// the persisted FCS cache would otherwise let unchanged files hit the cache and SKIP
/// re-checking, so their symbols never re-flush into the freshly-emptied DB — leaving the
/// symbol graph (and therefore coverage + impact analysis) permanently partial. Clearing it
/// forces the next scan to re-check, and thus re-index, every file. Pure path logic so it
/// is unit-testable without a daemon.
let internal clearFcsCheckCache (repoRoot: string) : int =
    let cacheDir = Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "cache")

    if Directory.Exists cacheDir then
        let files = Directory.GetFiles(cacheDir, "*.json")

        for f in files do
            File.Delete f

        files.Length
    else
        0

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
    // `dependsOn`: repo-root-relative globs naming EXTERNAL inputs (DB
    // migrations, generated files, schemas) that the symbol-diff cache key can't
    // see. Their content hash salts the BuildCompleted cache key so editing one
    // forces a genuine re-run instead of replaying a stale verdict. `[]` → no
    // salt (key byte-identical to the pre-feature key).
    (dependsOn: string list)
    =
    let db = Database.create dbPath

    // The symbol DB was just recreated (schema bump deleted the old one). The FCS check
    // cache is now stale: cache-hit files would skip re-checking and never re-flush their
    // symbols into the empty DB, leaving the graph (and coverage/impact analysis) partial.
    // Clear it so the next scan re-checks — and re-indexes — every file.
    if db.WasRecreated then
        try
            let cleared = clearFcsCheckCache repoRoot

            Logging.warn
                "test-prune"
                $"TestPrune DB was recreated (schema change) — cleared %d{cleared} FCS check-cache entries so every file re-indexes on this scan."
        with ex ->
            Logging.error "test-prune" $"failed to clear the FCS check cache after a DB recreate: %s{ex.Message}"

    let extensions = buildExtensions |> Option.map (fun f -> f db)

    let tryRepairSchemaDrift ex = tryRepairSchemaDrift dbPath ex

    // Durable "needs-testing" queue (plugin-owned sidecar). The set of changed
    // symbols not yet proven test-equivalent to the last green run. Loaded once
    // at construction so a restart with a non-empty queue re-flags those
    // symbols; updated write-through (in-memory ChangedSymbols stays the hot
    // view, this is the durable copy). A symbol leaves ONLY when a covering test
    // run passed (or it has no covering test). See PendingVerification.fs.
    //
    // Held in a closure-local mutable cell + Volatile for the same reason
    // changedSymbolsRef/freshnessRef are — read/written from multiple threads
    // (mailbox + cache intercept).
    let mutable pendingQueueRef: PendingVerification.Queue =
        PendingVerification.load repoRoot

    /// Add `symbols` to the in-memory queue. Called at the FileChecked
    /// accumulation point; the durable persist is batched to the flush
    /// chokepoint (`flushAndQueryAffected` saves BEFORE the analysis flush
    /// advances the durable snapshot). Crash-safety holds without a per-file
    /// write: losing un-flushed enqueues also means the analysis snapshot was
    /// not advanced, so a restart re-DETECTS the same changes and re-enqueues
    /// them (over-testing is the safe direction).
    let enqueuePending (symbols: string list) =
        if not symbols.IsEmpty then
            let updated = (pendingQueueRef, symbols) ||> List.fold (fun q s -> Set.add s q)
            Volatile.Write(&pendingQueueRef, updated)

    /// Remove `symbols` from the persisted queue and flush to disk. Called only
    /// when a covering test run for those symbols completed green (or a symbol
    /// has no covering test).
    let commitPending (symbols: Set<string>) =
        if not symbols.IsEmpty then
            let updated = Set.difference pendingQueueRef symbols
            Volatile.Write(&pendingQueueRef, updated)

            try
                PendingVerification.save repoRoot updated
            with ex ->
                Logging.warn
                    "test-prune"
                    $"failed to persist pending-verification queue after commit: %s{ex.Message}; in-memory queue still updated"

    // Flush pending analysis to DB and query affected tests from changed symbols.
    // Extensions (if any) contribute dependency edges via AnalyzeEdges, written
    // to the DB before QueryAffectedTests so they participate in impact traversal.
    let flushAndQueryAffected (state: TestPruneState) =
        // Persist the pending queue BEFORE flushPendingAnalysis advances the
        // durable analysis snapshot: once the snapshot advances, un-persisted
        // queue entries would no longer be re-detectable after a crash. One
        // write per flush (vs per FileChecked) — same crash-safety, batch-size
        // fewer disk writes.
        try
            PendingVerification.save repoRoot pendingQueueRef
        with ex ->
            Logging.warn
                "test-prune"
                $"failed to persist pending-verification queue: %s{ex.Message}; in-memory queue still updated"

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

        // Affected tests must be computed from the WHOLE needs-testing queue —
        // the in-memory hot view UNION the durable sidecar — not just the latest
        // diff. The persisted queue holds symbols a green run hasn't yet cleared
        // (e.g. carried across a restart, or left behind by an Aborted/failed
        // run); they must keep selecting tests until a covering run passes.
        let symbols =
            Set.union pendingQueueRef (Set.ofList flushedState.ChangedSymbols) |> Set.toList

        let affectedTests =
            if symbols.IsEmpty then
                []
            else
                let affected = db.QueryAffectedTests(symbols)

                Logging.info "test-prune" $"QueryAffectedTests(%A{symbols}): %d{affected.Length} affected tests"

                affected

        // Drop queued symbols that have NO covering test from the durable queue
        // immediately: there is nothing for them to wait on, and retaining them
        // would wedge the queue forever (every future run would re-select zero
        // tests yet the queue would never empty → permanent non-green). A symbol
        // is "covered" iff QueryAffectedTests([symbol]) returns at least one test.
        // Only ever REMOVES from the queue, so it cannot under-test.
        let uncovered =
            symbols
            |> List.filter (fun s -> (db.QueryAffectedTests [ s ]).IsEmpty)
            |> Set.ofList

        if not (Set.isEmpty uncovered) then
            Logging.info
                "test-prune"
                $"Dropping %d{Set.count uncovered} queued symbol(s) with no covering test from pending-verification queue"

            commitPending uncovered

        // Keep the in-memory hot view aligned with the durable queue so the
        // ChangedSymbols carried in state (and the cache-key snapshot) don't
        // re-select the uncovered symbols on the next event.
        let remainingSymbols =
            flushedState.ChangedSymbols
            |> List.filter (fun s -> not (Set.contains s uncovered))

        { flushedState with
            ChangedSymbols = remainingSymbols
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

    // Seed the in-memory hot view from the durable queue so a restart with a
    // non-empty queue re-flags those symbols: the first flushAndQueryAffected
    // (on cold-scan BatchChecked) then queries them and the next run re-tests
    // anything not yet proven green. Without this, a restart would diff current
    // symbols against the already-advanced analysis snapshot → "nothing changed"
    // → zero tests run → false green (hole #3).
    let initialState =
        { PendingAnalysis = Map.empty
          SymbolSnapshot = Map.empty
          AffectedTests = NotYetAnalyzed
          ChangedSymbols = pendingQueueRef |> Set.toList
          ChangedFiles = []
          LastResults = None
          PendingRerun = false
          TestClassFiles = Map.empty
          BuildCompletedInThisSession = false
          PriorProjectFingerprints = Map.empty
          PendingForceRunProjects = Set.empty }

    // Keep the cache-key snapshot consistent with the seeded queue from the
    // very first event (the cache intercept runs before any Update handler).
    Volatile.Write(&changedSymbolsRef, initialState.ChangedSymbols)

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
        // `hasCachedResults` (state.LastResults.IsSome) means a run already
        // completed THIS session — i.e. a green baseline exists to be
        // "test-equivalent" to. The zero-affected skip needs this AS WELL AS an
        // empty queue: a cold daemon with an empty queue but no prior run has no
        // baseline yet and must run the full suite once to establish one. See
        // the skip gate below.
        (hasCachedResults: bool)
        // Dependency-fanout (DependencyFanout): test projects whose dependency
        // fingerprint changed since the last build. Their tests run in FULL
        // (project-coarse), UNIONED with the symbol-precise selection — a binary
        // change the symbol diff can't see still re-runs the dependent tests.
        // Empty in the common case (no dependency change), so ordinary
        // source-symbol edits keep their precise, minimal selection.
        (forceRunProjects: Set<string>)
        : Async<TestPruneMsg> =
        async {
            // The queue snapshot this run is LAUNCHED against — the durable
            // queue UNION the in-memory hot view. Captured here (not read from
            // state at completion time) because mid-run BatchChecked flushes
            // mutate both; the synchronous TestsFinished handler commits ONLY
            // these symbols and leaves mid-run arrivals queued for the rerun.
            let launchedSymbols = Set.union pendingQueueRef (Set.ofList state.ChangedSymbols)

            // For each launched symbol, the set of test PROJECTS whose tests
            // cover it. An empty set ⇒ no covering test. Queried per-symbol so
            // a symbol commits ONLY when every project covering IT passed (a
            // run-wide union would over-couple unrelated symbols). Empty queue ⇒
            // no queries.
            let coveringProjectsBySymbol =
                launchedSymbols
                |> Set.toList
                |> List.map (fun s ->
                    let projs =
                        db.QueryAffectedTests [ s ] |> List.map (fun t -> t.TestProject) |> Set.ofList

                    s, projs)
                |> Map.ofList

            let launch =
                { Symbols = launchedSymbols
                  CoveringProjectsBySymbol = coveringProjectsBySymbol }

            try
                // Extension-contributed edges were already written to the DB by
                // flushAndQueryAffected, so state.AffectedTests already includes tests
                // reachable through extension edges (sql, sql-hydra, falco, etc.).
                let affectedTestsList =
                    match state.AffectedTests with
                    | Analyzed tests -> tests
                    | NotYetAnalyzed -> []

                let symbolAffectedByProject =
                    affectedTestsList
                    |> List.groupBy (fun t -> t.TestProject)
                    |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)
                    |> Map.ofList

                // UNION the dependency-fanout: each force-run project enters the
                // map with an EMPTY class list, which `buildFilterArgs` treats as
                // "no filter → run ALL tests in this project" (a project ABSENT
                // from a non-empty map is skipped; present-with-[] runs in full).
                // We don't overwrite a project that already has symbol-affected
                // classes — its precise selection plus a forced full run are the
                // same "run this project"; [] (full) is the safe superset, so a
                // fanout hit promotes a partially-selected project to full.
                let affectedByProject =
                    forceRunProjects
                    |> Set.fold (fun acc proj -> Map.add proj [] acc) symbolAffectedByProject

                // The skip gate counts symbol-affected classes only. A pure
                // dependency-fanout (force-run projects, zero symbol classes) must
                // NOT be counted as "0 affected" and skipped — so the gate below
                // also checks `forceRunProjects` is empty.
                let totalClasses = symbolAffectedByProject |> Map.values |> Seq.sumBy List.length

                // Gate the zero-affected skip on the PERSISTED queue being empty
                // (§3c). The queue-empty check is the load-bearing addition: an
                // empty queue means "test-equivalent to the last green run", so "0
                // affected tests" is a sound green; a NON-empty queue with 0 affected
                // classes (covered symbols whose tests aren't indexed yet, etc.) must
                // run the suite rather than silent-green. `hasCachedResults` is
                // retained ONLY as the cold-start guard: the very first run of a
                // session (no baseline yet) must run the full suite to ESTABLISH the
                // green baseline the empty queue is then equivalent to. Both must
                // hold to skip — either alone would under-test (queue-empty cold
                // start) or be unsound (warm with a non-empty queue).
                if
                    totalClasses = 0
                    && Set.isEmpty forceRunProjects
                    && Set.isEmpty pendingQueueRef
                    && hasCachedResults
                then
                    Logging.info
                        "test-prune"
                        "No affected classes, no dependency fanout, empty pending queue, baseline exists — skipping tests"

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

                    return TestsFinished(started, completed, launch)
                else
                    if totalClasses = 0 then
                        Logging.info "test-prune" "No affected classes (cold start / pending queue) — running all tests"
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
                    return TestsFinished(started, completed, launch)
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

                // launch carries the queue snapshot this aborted run was
                // launched against; the TestsFinished handler commits NOTHING
                // for an Aborted outcome, so those symbols stay queued.
                return TestsFinished(started, completed, launch)
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
                        // FORCE semantics: `test-rerun` is the explicit "run it now"
                        // verb, so it must execute regardless of cache state or a
                        // prior run. The cache is already bypassed (commands call
                        // `executeTests` directly, never `runAndCache`). The only
                        // thing that previously made it return an INSTANT non-result
                        // ("tests already running" — no run, no log) was an in-flight
                        // background run from a recent BuildCompleted holding the
                        // `RunExclusive "tests"` slot. Rather than bail instantly,
                        // WAIT (bounded) for that run to finish, then run — so the
                        // command always executes. If the slot is still held after
                        // the wait (a genuinely long run, or a stuck slot), report a
                        // DISTINCT `busy` status — never a generic verdict that could
                        // read as a pass/fail the command never produced.
                        let waitForSlotMs = 120_000
                        let pollMs = 100
                        let mutable waitedMs = 0

                        while ctx.IsRunning "tests" && waitedMs < waitForSlotMs do
                            do! Async.Sleep pollMs
                            waitedMs <- waitedMs + pollMs

                        if ctx.IsRunning "tests" then
                            return
                                JsonSerializer.Serialize(
                                    {| status = "busy"
                                       message =
                                        $"a test run is still in progress after waiting %d{waitForSlotMs / 1000}s; retry once it finishes" |}
                                )
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
                                                        // A deferred project never ran, and an errored one
                                                        // aborted without a verdict — both are non-green, so
                                                        // `--only-failed` (rerun non-green projects) must pick
                                                        // them up.
                                                        | TestsDeferred _
                                                        | TestsErrored _ -> Some name
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
                                        //
                                        // Empty launch set: `run-tests` is a manual FORCE run
                                        // (optionally filtered to a subset / only-failed). It is NOT
                                        // the impact-analysis queue-draining path, and a filtered
                                        // force-run may not cover every queued symbol — so it commits
                                        // NOTHING from the pending-verification queue (over-testing is
                                        // the safe direction). The queue drains through the normal
                                        // BuildCompleted impact flow.
                                        let emptyLaunch =
                                            { Symbols = Set.empty
                                              CoveringProjectsBySymbol = Map.empty }

                                        ctx.Post(TestsFinished(started, completed, emptyLaunch))

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

                                // Write-through to the durable needs-testing queue at the
                                // SAME point the in-memory hot view accumulates. Persisted
                                // here (before the BatchChecked analysis flush) so a crash
                                // between this and the DB rebuild leaves the symbols QUEUED
                                // — over-testing is the safe direction. They leave the queue
                                // only when a covering test run passes (TestsFinished) or
                                // they prove to have no covering test (flushAndQueryAffected).
                                enqueuePending changedNames

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

                        // ── Dependency-fanout fingerprint (computed for EVERY
                        // build, before the running/idle split, so the prior
                        // fingerprint always advances and a mid-run dependency
                        // change is never lost). Fingerprint each test project from
                        // its referenced-project DLL hashes + own package versions
                        // (DependencyFanout). A project whose fingerprint moved
                        // since the last build had a dependency/binary change the
                        // symbol diff can't see → force-run it. An empty graph
                        // (tests / no graph wired) yields no fanout, so the
                        // symbol-precise path is unchanged.
                        let fsprojByName =
                            ctx.ProjectGraph.GetAllProjects()
                            |> List.map (fun p -> Path.GetFileNameWithoutExtension p, p)
                            |> Map.ofList

                        let currentFingerprints =
                            match testConfigs with
                            | Some configs ->
                                configs
                                |> List.choose (fun c ->
                                    Map.tryFind c.Project fsprojByName
                                    |> Option.map (fun fsproj -> c.Project, fsproj))
                                |> List.map (fun (name, fsproj) ->
                                    name, DependencyFanout.computeProjectFingerprint ctx.ProjectGraph fsproj)
                                |> Map.ofList
                            | None -> Map.empty

                        let fanoutNow =
                            DependencyFanout.changedProjects state.PriorProjectFingerprints currentFingerprints

                        if not (Set.isEmpty fanoutNow) then
                            Logging.info
                                "test-prune"
                                $"Dependency fanout: %d{Set.count fanoutNow} test project(s) had a dependency-fingerprint change — force-running: %A{Set.toList fanoutNow}"

                        // Advance the baseline on every build; carry any not-yet-run
                        // fanout in the pending set (consumed by the queued rerun).
                        let state =
                            { state with
                                PriorProjectFingerprints = currentFingerprints }

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

                            // Stash the fanout so the rerun runs it (don't lose a
                            // mid-run dependency change).
                            return
                                { state with
                                    PendingRerun = true
                                    PendingForceRunProjects = Set.union state.PendingForceRunProjects fanoutNow }
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

                                    // Union this build's fanout with any pending
                                    // fanout deferred from a prior mid-run build,
                                    // then clear the pending set (it's being run).
                                    let forceRunProjects = Set.union fanoutNow stateWithAffected.PendingForceRunProjects

                                    let stateWithAffected =
                                        { stateWithAffected with
                                            PendingForceRunProjects = Set.empty }

                                    ctx.RunExclusive
                                        "tests"
                                        (runTestsWithImpact
                                            ctx
                                            configs
                                            stateWithAffected
                                            hasCachedResults
                                            forceRunProjects)

                                    return stateWithAffected
                                | _ ->
                                    // No test configs — flush only; nothing to run.
                                    return stateWithAffected
                    | BuildFailed _ -> return state

                | Custom(TestsFinished(started, completed, launch)) ->
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

                    // Outcome-conditional, per-project green-commit. A launched
                    // symbol leaves the needs-testing queue ONLY when the run
                    // genuinely covered it green:
                    //   - the run did NOT abort (a beforeRun throw / crash gives
                    //     Outcome = Aborted, Results = Map.empty → commit nothing), AND
                    //   - EVERY project covering the symbol produced a PASSED result.
                    // A project counts as passed only if it appears in completed.Results
                    // with TestResult.isPassed — a covering project ABSENT from the
                    // results (didn't run this cycle) blocks the commit (we can't claim
                    // it green). A symbol with NO covering project was already dropped at
                    // flush time, but if one slipped through it commits here (nothing to
                    // wait on). Mid-run arrivals are NOT in launch.Symbols, so they stay
                    // queued and the PendingRerun flow re-runs them.
                    let aborted =
                        match completed.Outcome with
                        | Aborted _ -> true
                        | Normal -> false

                    let projectPassed (proj: string) =
                        match Map.tryFind proj completed.Results with
                        | Some r -> TestResult.isPassed r
                        | None -> false

                    let committedSymbols =
                        if aborted then
                            Set.empty
                        else
                            launch.Symbols
                            |> Set.filter (fun s ->
                                match Map.tryFind s launch.CoveringProjectsBySymbol with
                                | Some projs when not (Set.isEmpty projs) -> projs |> Set.forall projectPassed
                                | _ -> true)

                    if not (Set.isEmpty committedSymbols) then
                        Logging.info
                            "test-prune"
                            $"Committing %d{Set.count committedSymbols} verified symbol(s) — removing from pending-verification queue"

                        commitPending committedSymbols

                    // The in-memory hot view must shed ONLY the committed symbols,
                    // never the whole list — symbols left in the queue (mid-run
                    // arrivals, projects that failed/aborted) must keep selecting
                    // tests until a covering run passes. `queueAfterCommit` is the
                    // post-commit durable queue; it drives the cleared ChangedSymbols
                    // and the cache-key snapshot in every return branch below.
                    let queueAfterCommit = pendingQueueRef
                    let remainingChangedSymbols = queueAfterCommit |> Set.toList

                    // Pushing a terminal Completed/Failed status is what appends the
                    // run to history; both rerun and final-idle branches must call this.
                    let recordRunOutcome (results: TestResults) =
                        let total = results.Results.Count

                        // §3a — verdict hardening: consult completed.Outcome FIRST.
                        // An Aborted run (beforeRun threw, runner crashed, run
                        // cancelled) MUST be non-green regardless of result counts —
                        // empty results trivially satisfy "failed = 0 && deferred = 0"
                        // and would otherwise false-green. Surface the abort message.
                        // §3b — a run that executed ZERO projects while the pending
                        // queue still holds symbols verified nothing: reuse the honest
                        // "waiting on build (tests did not run)" deferred path rather
                        // than reporting a green that tested nothing.
                        let abortMessage =
                            match completed.Outcome with
                            | Aborted reason -> Some reason
                            | Normal -> None

                        match abortMessage with
                        | Some reason ->
                            ctx.CompleteWithSummary $"test run aborted: %s{reason}"

                            ctx.ReportStatus(
                                PluginStatus.Failed(
                                    $"test run aborted (tests did not run): %s{reason}",
                                    DateTime.UtcNow
                                )
                            )
                        | None when total = 0 && not (Set.isEmpty queueAfterCommit) ->
                            // Zero projects executed but symbols still await
                            // verification — honest non-green, same wording/path as a
                            // deferred (never-ran) project.
                            ctx.CompleteWithSummary "0 projects ran; symbols still awaiting verification"

                            ctx.ReportStatus(
                                PluginStatus.Failed(
                                    $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)",
                                    DateTime.UtcNow
                                )
                            )
                        | None ->

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

                                if failed = 0 && deferred = 0 && Set.isEmpty queueAfterCommit then
                                    ctx.ReportStatus(Completed(DateTime.UtcNow))
                                elif failed = 0 && deferred = 0 then
                                    // Everything that RAN passed, but the pending queue
                                    // still holds symbols this (e.g. filtered) run did not
                                    // cover green — NOT test-equivalent to a green run yet.
                                    // Non-green with the honest "waiting on build" wording;
                                    // the next BuildCompleted re-selects and runs them.
                                    ctx.ReportStatus(
                                        PluginStatus.Failed(
                                            $"%d{Set.count queueAfterCommit} symbol(s) waiting on build (tests did not run)",
                                            DateTime.UtcNow
                                        )
                                    )
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
                        // and now. ChangedSymbols is reset to the POST-COMMIT queue
                        // (committed symbols removed, still-pending + mid-run arrivals
                        // retained) so the rerun re-selects exactly what hasn't been
                        // proven green. flushAndQueryAffected unions this with the durable
                        // queue, so the rerun keeps testing the unverified symbols. If the
                        // DB errors out here the rerun never happens, so we must bail back
                        // to idle (capturing testResults) instead of leaving PendingRerun
                        // stuck and the slot already freed.
                        match
                            (try
                                Ok(
                                    flushAndQueryAffected
                                        { state with
                                            PendingRerun = false
                                            ChangedSymbols = remainingChangedSymbols }
                                )
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
                                    ChangedSymbols = remainingChangedSymbols
                                    AffectedTests = Analyzed [] }
                        | Ok rerunState ->
                            recordRunOutcome testResults
                            Volatile.Write(&changedSymbolsRef, rerunState.ChangedSymbols)

                            ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

                            // Consume the deferred dependency-fanout: a build that
                            // landed mid-run stashed its changed test projects here
                            // (it couldn't run them then). The rerun runs them now,
                            // alongside the queued symbols. Clear so a later rerun
                            // doesn't re-run them.
                            let deferredFanout = rerunState.PendingForceRunProjects

                            let rerunState =
                                { rerunState with
                                    LastResults = Some testResults
                                    PendingRerun = false
                                    PendingForceRunProjects = Set.empty }

                            match testConfigs with
                            | Some configs when not configs.IsEmpty ->
                                // A run just completed (LastResults set below), so the
                                // baseline exists — hasCachedResults = true. The
                                // deferred fanout force-runs any test project whose
                                // dependency fingerprint changed during the prior run.
                                ctx.RunExclusive "tests" (runTestsWithImpact ctx configs rerunState true deferredFanout)
                            | _ -> ()

                            return rerunState
                    else
                        // Clear ONLY the committed symbols from the hot view; the
                        // durable queue (post-commit) is the source of truth and is
                        // mirrored into the cache-key snapshot so a non-empty queue
                        // keeps a cached green from replaying (see CacheKey below).
                        Volatile.Write(&changedSymbolsRef, remainingChangedSymbols)
                        recordRunOutcome testResults

                        return
                            { state with
                                LastResults = Some testResults
                                ChangedFiles = []
                                ChangedSymbols = remainingChangedSymbols
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
            // External-dependency salt: a content hash of the files matched by
            // the configured `dependsOn` globs. Editing a matched file (e.g. a DB
            // migration that changes the TEST database schema but no test SOURCE)
            // changes this hash → the key below changes → cache miss → genuine
            // re-execution on the next BuildCompleted. Empty `dependsOn` → "",
            // and the entry is OMITTED entirely, so the key stays byte-identical
            // to the pre-feature key and existing on-disk caches keep hitting.
            // Computed once per cacheKey invocation (cacheKey runs once per event,
            // not per file), so the file reads are bounded.
            let dependsOnEntries =
                match externalDependencyHash repoRoot dependsOn with
                | "" -> []
                | h -> [ "depends-on", h ]

            // §3d — fold the persisted needs-testing queue hash into the key so a
            // cached green `TestRunCompleted` can be replayed ONLY for a state whose
            // pending queue is identical to the one the cached run produced. Without
            // this, a green that left symbols queued (a fully-passing FILTERED run
            // that didn't cover every queued symbol) shares the changed-symbols
            // merkle with a later BuildCompleted and could replay a green while
            // symbols still await verification. Empty queue → empty entry, keeping
            // the empty-queue green fast-path key byte-stable. Thunked: FileChecked
            // — the per-file, highest-frequency probe — never splices this entry,
            // so the queue is hashed only on the BuildCompleted/TestsFinished paths.
            let pendingQueueEntry () =
                if Set.isEmpty pendingQueueRef then
                    []
                else
                    [ "pending-queue", PendingVerification.hash pendingQueueRef ]

            // One definition shared by the BuildSucceeded and BuildFailed keys so
            // the two branches cannot drift; thunked so probes that never splice it
            // don't pay the sort/concat/hash.
            let changedSymbolsHash () =
                Volatile.Read(&changedSymbolsRef)
                |> List.distinct
                |> List.sort
                |> String.concat "|"
                |> FsHotWatch.CheckCache.sha256Hex

            let buildCompletedKey () =
                // AUTOMATION-5: salt bumped v1→v2 so any entry written by the
                // prior code (which cached FAILED test verdicts and could replay
                // them on a now-green tree) can never match a key computed here.
                // Orphans legacy poison on disk without needing a manual cache wipe.
                FsHotWatch.TaskCache.merkleCacheKey (
                    [ "plugin-version", "test-prune-merkle-v2"
                      "event", "BuildCompleted"
                      "changed-symbols", changedSymbolsHash ()
                      "build-outcome", "succeeded" ]
                    @ pendingQueueEntry ()
                    @ dependsOnEntries
                )

            match event with
            | BuildCompleted BuildSucceeded -> Some(buildCompletedKey ())
            | Custom(TestsFinished(_, completed, _)) ->
                // AUTOMATION-5 (2026-06-07): a FAILED test outcome must never be
                // served from cache as a current verdict. Unlike BuildPlugin —
                // whose result is a pure function of its content-merkle inputs, so
                // replaying a cached failure on an identical tree is correct — a
                // test outcome is NOT pinned by the changed-symbols merkle: the same
                // key recurs after the tree is fixed (or for a flaky test), and a
                // cached `Failed` would then replay as a stale red on a green tree
                // ("green tree read as red"). Field evidence: an 08:35 failure
                // replayed at 10:19/10:49 and through four deploy-preflights on a
                // `failed: 0` tree. Returning None here makes a non-passing run
                // UNCACHEABLE, so `runAndCache` skips the write and the next
                // matching BuildCompleted finds no poisoned entry and re-runs.
                // A fully-passing run still caches (key matches BuildSucceeded) and
                // replays cleanly — the desired green fast-path.
                //
                // §3d also requires the queue to be EMPTY for a green to be
                // cacheable: a green that left symbols queued is not a sound
                // "safe to skip" verdict, so it must re-run rather than replay.
                // The Aborted-outcome / abort short-circuit is covered because an
                // aborted run has empty Results that the all-passed check treats as
                // trivially passing — so we ALSO gate on a non-Aborted outcome here.
                let allPassed = completed.Results |> Map.forall (fun _ r -> TestResult.isPassed r)

                let notAborted =
                    match completed.Outcome with
                    | Aborted _ -> false
                    | Normal -> true

                if allPassed && notAborted && Set.isEmpty pendingQueueRef then
                    Some(buildCompletedKey ())
                else
                    None
            | BuildCompleted(BuildFailed errs) ->
                Some(
                    // AUTOMATION-5: salt bumped v1→v2 in lockstep with the
                    // BuildSucceeded key so the two never split across versions.
                    // dependsOn salt mirrors the BuildSucceeded key (same
                    // external-input invalidation applies to a failed build).
                    // pending-queue salt mirrors the BuildSucceeded key too.
                    FsHotWatch.TaskCache.merkleCacheKey (
                        [ "plugin-version", "test-prune-merkle-v2"
                          "event", "BuildCompleted"
                          "changed-symbols", changedSymbolsHash ()
                          "build-outcome", "failed:" + String.concat "|" (List.sort errs) ]
                        @ pendingQueueEntry ()
                        @ dependsOnEntries
                    )
                )
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
