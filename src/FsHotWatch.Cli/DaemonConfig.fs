module FsHotWatch.Cli.DaemonConfig

open System
open System.IO
open System.Text.Json
open FsHotWatch
open FsHotWatch.CheckCache
open FsHotWatch.ErrorLedger
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper
open FsHotWatch.TestPrune.TestPrunePlugin

/// Cache backend for the FCS check pipeline.
///
/// There is no file backend: FCS check results are not serializable (a deserialized
/// entry always reconstructs as `ParseOnly`, which `CheckPipeline.tryGetCachedFullCheck`
/// treats as a MISS), so it could never hit. A config value selecting the removed
/// `"cache": "file"` / `"jj"` backend is a hard `ConfigError` — dead config fails the
/// load rather than warning and carrying on, because a warning inside a long gate is
/// indistinguishable from noise and lets the dead key survive indefinitely.
type CacheBackendConfig =
    /// No check-result cache. This is the DEFAULT.
    | NoCache
    /// In-memory LRU cache (lost on restart). Holds the live `FileCheckResult`, FCS
    /// types and all.
    | InMemoryOnly of maxSize: int

/// Create cache backend and key provider from config.
let createCacheComponents
    (_repoRoot: string)
    (config: CacheBackendConfig)
    : (ICheckCacheBackend option * ICacheKeyProvider option) =
    match config with
    | NoCache -> (None, None)
    | InMemoryOnly maxSize ->
        (Some(FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(maxSize) :> ICheckCacheBackend),
         Some(TimestampCacheKeyProvider() :> ICacheKeyProvider))

/// Resolves which paths from `paths` exist, retrying with short backoff for the case
/// where the daemon starts immediately after `jj workspace add`: workspace population
/// is async, so `dirExists` can transiently return false for directories that are
/// about to appear. ~300 ms worst case (3 × 100 ms) is invisible in normal operation
/// but prevents a silent "0 paths resolved → plugin doesn't register" failure in a
/// fresh workspace.
///
/// Injectable `dirExists` and `sleep` for unit testing without timing or disk
/// dependencies.
let resolveExistingPathsWithRetry (dirExists: string -> bool) (sleep: int -> unit) (paths: string list) : string list =
    let resolveAttempt () = paths |> List.filter dirExists
    let mutable resolved = resolveAttempt ()
    let mutable attempts = 0

    while resolved.Length < paths.Length && attempts < 3 do
        attempts <- attempts + 1
        sleep 100
        resolved <- resolveAttempt ()

    resolved

/// Fail-loud guard: analyzers are treated like test failures — EVERY configured
/// `analyzers.paths` entry must contribute ≥1 analyzer, else the check goes RED.
///
/// PER PATH, not just in aggregate: a `.fshw.json` with several analyzer paths where ONE
/// points at a bin dir that doesn't exist in CI (built in the wrong configuration) or is
/// empty loads 0 from that path yet would stay green because the OTHERS loaded. Both
/// per-path failure modes collapse to a count of 0: the path didn't resolve, or it
/// resolved but holds no analyzer DLLs.
///
/// Input is the per-ORIGINAL-configured-path (absolute path, contributed count) list.
/// Returns `Some message` naming each zero-loading path when any exists, `None`
/// otherwise — unconfigured (empty list), or every path loaded ≥1.
let analyzerPathFailures (loadedByPath: (string * int) list) : string option =
    let zeroPaths =
        loadedByPath |> List.filter (fun (_, count) -> count = 0) |> List.map fst

    match zeroPaths with
    | [] -> None
    | paths ->
        let joined = String.concat "; " paths

        Some
            $"Analyzer path(s) loaded 0 analyzers (missing/empty or built in the wrong configuration): \
              %s{joined} — check .fshw.json analyzers.paths vs the build config"


/// Default global per-operation timeout (seconds) applied when neither a
/// per-entry `timeoutSec` nor the global `.fshw.json` `timeoutSec` is set.
///
/// This bounds process-spawning plugins (build / tests / fileCommands): without a
/// default they fall back to `Timeout.InfiniteTimeSpan`, so an op that hung (a
/// deadlocked `dotnet build`, a test runner stuck on a socket) would hang the daemon
/// with no recovery short of `fshw stop` + cold restart. With a non-`None` default an
/// unbounded op becomes `TimedOut`: the process tree is killed and the daemon stays
/// responsive.
///
/// 600s (10 min) is deliberately generous — large builds and full test suites
/// legitimately run for minutes, so it only ever fires on a genuine hang. Tighten per
/// repo via the global `timeoutSec` key or a per-entry `timeoutSec`, which take
/// precedence.
[<Literal>]
let DefaultGlobalTimeoutSec = 600

/// Configuration for a single test project.
///
/// `CoverageArgsTemplate` is the command-line template the daemon appends
/// when collecting coverage. `{output}` is substituted with the per-run
/// baseline or partial file path. `None` means use the MTP-cobertura default
/// (`FsHotWatch.TestPrune.TestPrunePlugin.defaultCoverageArgsTemplate`).
/// For other test runners (coverlet.collector, AltCover, etc.), provide
/// your own template.
type TestProjectConfig =
    {
        Project: string
        Command: string
        Args: string
        Group: string
        Environment: (string * string) list
        FilterTemplate: string option
        ClassJoin: string
        Coverage: bool
        CoverageArgsTemplate: string option
        TimeoutSec: int option
        /// How to obtain the structured test report the verdict is derived from
        /// (`reportVerificationFormat` in `.fshw.json`). Default `AutoDetect`.
        ReportVerificationFormat: ReportVerificationFormat
    }

/// The kind of test extension.
type TestExtensionKind =
    | Falco
    | Unknown of string

/// Configuration for a test extension (e.g. Falco route mapping).
type TestExtensionConfig =
    { Kind: TestExtensionKind
      Project: string
      TestDir: string }

/// Format mode configuration.
type FormatMode =
    /// No format plugin
    | Off
    /// Register FormatPreprocessor (auto-format on save)
    | Auto
    /// Register read-only format check (reports errors without modifying)
    | Check

/// A verb the run-level `beforeRun`/`afterRun` hooks can bracket, selected by
/// the top-level `runHookCommands` key.
///
/// Exists so a consumer can say "bracket `confirm` only". A box-wide gate that
/// serialises heavy runs across workspaces usually wants to guard the MERGE
/// verdict — `confirm`, unfiltered, expensive — while leaving the inner loop
/// (`check`, impact-scoped, run constantly) free. Bracketing both makes every
/// save-triggered check queue behind someone else's full suite.
[<RequireQualifiedAccess>]
type RunHookCommand =
    | Check
    | Confirm

/// Default for `runHookCommands`: BOTH verbs. Absent, wrongly-typed, and wholly
/// unrecognised values all resolve here, so un-gating is always something a config asked
/// for out loud — a typo must never cause it.
let DefaultRunHookCommands: Set<RunHookCommand> =
    Set.ofList [ RunHookCommand.Check; RunHookCommand.Confirm ]

/// Parsed daemon configuration from .fshw.json.
type DaemonConfiguration =
    {
        Build:
            {| Command: string
               Args: string
               BuildTemplate: string option
               DependsOn: string list
               TimeoutSec: int option |} list option
        Format: FormatMode
        Lint: bool
        Cache: CacheBackendConfig
        Analyzers:
            {| Paths: string list
               FailOnSeverity: DiagnosticSeverity |} option
        Tests:
            {| BeforeRun: string list option
               Extensions: TestExtensionConfig list
               Projects: TestProjectConfig list
               Excluded: SolutionScope.Exclusion list
               Solution: string option
               CoverageDir: string
               DependsOn: string list |} option
        FileCommands:
            {| PluginName: string
               Pattern: string option
               AfterTests: FsHotWatch.FileCommand.FileCommandPlugin.TestFilter option
               Command: string
               Args: string
               TimeoutSec: int option |} list
        Coverage:
            {| ConfigPath: string
               SearchDir: string |} option
        Exclude: string list
        /// When false (default), the report-producing plugins (analyzers, lint) skip
        /// compile items that resolve OUTSIDE the repo root — e.g. NuGet-injected
        /// `_content` source (xunit's `DefaultRunnerReporters.fs`) or files above/beside
        /// the repo. Such third-party source is compiled in but is not ours to lint. Set
        /// `includeOutsideRepo: true` to report on them anyway. (obj/bin is always skipped
        /// regardless — see `PathFilter.isGeneratedPath`.)
        IncludeOutsideRepo: bool
        /// Directory (relative to repoRoot or absolute) for daemon.log. Defaults to "logs".
        LogDir: string
        /// Global default timeout (seconds). Used when no per-entry override set.
        TimeoutSec: int option
        /// Idle-exit configuration from the `idleExitMin` key. Absent → AUTO
        /// (enabled at 30min only for non-default `/.workspaces/` checkouts);
        /// `0`/`false` → disabled everywhere; positive N → enabled at N minutes
        /// everywhere. See `FsHotWatch.IdleExit`.
        IdleExitMin: IdleExit.IdleExitConfig
        /// Pressure-shortened idle-exit floor from the `pressureIdleFloorMin`
        /// key. When idle-exit is eligible AND the machine is under memory
        /// pressure, the effective idle window is shortened to `min(idleExitMin,
        /// this)`. Absent → 2 min (default-on); `0`/`false` → pressure-shortening
        /// disabled; positive N → floor at N min. Pressure never makes a
        /// non-eligible daemon eligible. See `FsHotWatch.IdleExit`.
        PressureIdleFloorMin: IdleExit.PressureFloorConfig
        /// macOS FSEvents coalescing latency in milliseconds, from the
        /// `fsEventsLatencyMs` key. The native `FSEventStreamCreate` batches
        /// filesystem events within this window before delivering them. Default
        /// 250. `0` is valid (no coalescing). Higher = more event coalescing and
        /// lower fseventsd load per change, at the cost of slightly higher
        /// change-to-rebuild latency. Only affects the macOS FSEvents watcher.
        FsEventsLatencyMs: int
        /// Run-level `beforeRun` hook, from the top-level
        /// `beforeRun` key. A shell command run ONCE at the very start of a
        /// `check`/`confirm` run — BEFORE the daemon is contacted — as a
        /// FAIL-CLOSED preflight: a non-zero exit aborts the run with exit 2 and
        /// runs no plugin work. DISTINCT from `tests.beforeRun`, which the daemon
        /// runs per test run inside the tests slot; this one brackets the WHOLE
        /// run. The first consumer (intelligence) uses it to acquire a box-wide
        /// gate-lock. Absent / `false` → None.
        BeforeRun: string option
        /// Run-level `afterRun` hook, from the top-level
        /// `afterRun` key. A shell command run ONCE at the END of a
        /// `check`/`confirm` run as a `finally` — it fires on success, on a red
        /// verdict, AND on abort (including SIGINT/SIGTERM). Its own exit code is
        /// BEST-EFFORT: a non-zero `afterRun` is logged loudly but NEVER changes
        /// the run's verdict (a lock-release hiccup must not flip green↔red). The
        /// first consumer uses it to release the gate-lock its `beforeRun`
        /// acquired. Absent / `false` → None.
        AfterRun: string option
        /// Timeout (seconds) bounding EACH run-level hook (`beforeRun`/`afterRun`),
        /// from the top-level `runHookTimeoutSec` key. Resolution chain: this →
        /// global `timeoutSec` → `DefaultGlobalTimeoutSec`; a run-level hook is
        /// ALWAYS bounded — even when the global timeout is disabled — because a
        /// lock hook that hangs forever is worse than the lock it guards. A
        /// `number` → Some; `false` / absent → None (fall through the chain),
        /// mirroring the global `timeoutSec` tristate.
        RunHookTimeoutSec: int option
        /// Which verbs the run-level hooks bracket, from the top-level
        /// `runHookCommands` key (e.g. `["confirm"]`). Absent → BOTH verbs.
        ///
        /// A verb not in this set runs completely unwrapped — no latch, no signal
        /// handlers, no shell-out — the same straight `action ()` taken when no
        /// hook is configured at all.
        ///
        /// An explicitly EMPTY array is legal and means "bracket nothing",
        /// consistent with the opt-out idiom the other run-hook keys already use;
        /// it is warned about at load, because silently un-gating is the dangerous
        /// direction.
        RunHookCommands: Set<RunHookCommand>
    }

let private defaultConfigFor (repoRoot: string) =
    { Build =
        Some
            [ {| Command = "dotnet"
                 Args = "build"
                 BuildTemplate = None
                 DependsOn = []
                 TimeoutSec = None |} ]
      Format = Auto
      Lint = true
      Cache = NoCache
      Analyzers = None
      Tests = None
      FileCommands = []
      Coverage = None
      Exclude = []
      IncludeOutsideRepo = false
      LogDir = "logs"
      TimeoutSec = Some DefaultGlobalTimeoutSec
      IdleExitMin = IdleExit.IdleExitConfig.Absent
      PressureIdleFloorMin = IdleExit.PressureFloorConfig.Absent
      FsEventsLatencyMs = 250
      BeforeRun = None
      AfterRun = None
      RunHookTimeoutSec = None
      RunHookCommands = DefaultRunHookCommands }

/// Raised when `.fshw.json` cannot be read, parsed, or validated.
/// Carries a user-facing message.
exception ConfigError of message: string

/// Parse a JSON string into a DaemonConfiguration, using defaults for missing fields.
let parseConfig (json: string) (defaults: DaemonConfiguration) : DaemonConfiguration =
    use doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let parseBuildEntry (v: JsonElement) =
        let cmd =
            match v.TryGetProperty("command") with
            | true, c -> c.GetString()
            | _ -> "dotnet"

        let args =
            match v.TryGetProperty("args") with
            | true, a -> a.GetString()
            | _ -> "build"

        let buildTemplate =
            match v.TryGetProperty("buildTemplate") with
            | true, t -> Some(t.GetString())
            | _ -> None

        let dependsOn =
            match v.TryGetProperty("dependsOn") with
            | true, arr -> arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
            | _ -> []

        let timeoutSec =
            match v.TryGetProperty("timeoutSec") with
            | true, t when t.ValueKind = JsonValueKind.Number -> Some(t.GetInt32())
            | _ -> None

        {| Command = cmd
           Args = args
           BuildTemplate = buildTemplate
           DependsOn = dependsOn
           TimeoutSec = timeoutSec |}

    let build =
        match root.TryGetProperty("build") with
        | true, v when v.ValueKind = JsonValueKind.False -> Some []
        | true, v when v.ValueKind = JsonValueKind.Object -> Some [ parseBuildEntry v ]
        | true, v when v.ValueKind = JsonValueKind.Array ->
            Some(v.EnumerateArray() |> Seq.map parseBuildEntry |> Seq.toList)
        | _ -> defaults.Build

    let format =
        match root.TryGetProperty("format") with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString().ToLowerInvariant() with
            | "check" -> Check
            | "auto" -> Auto
            | "off"
            | "false" -> Off
            | other ->
                Logging.warn "config" $"Unknown format value '%s{other}', using Auto"
                Auto
        | true, v when v.ValueKind = JsonValueKind.True -> Auto
        | true, v when v.ValueKind = JsonValueKind.False -> Off
        | _ -> Auto

    let lint =
        match root.TryGetProperty("lint") with
        | true, v -> v.GetBoolean()
        | _ -> true

    let cache =
        match root.TryGetProperty("cache") with
        | true, v when v.ValueKind = JsonValueKind.False -> NoCache
        | true, v when v.ValueKind = JsonValueKind.True -> defaults.Cache
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString().ToLowerInvariant() with
            | "memory" -> InMemoryOnly 500
            | "none"
            | "false" -> NoCache
            | ("file" | "jj") as removed ->
                // FAIL, don't warn — see `CacheBackendConfig` for why the backend is
                // gone. A warning here scrolled past inside a 10-minute gate, so the
                // dead key survived in a real repo's `.fshw.json` for weeks.
                raise (
                    ConfigError(
                        $"cache: '%s{removed}' has been REMOVED — the on-disk FCS check cache could never "
                        + "produce a hit (FCS results aren't serializable, so every lookup returned ParseOnly, "
                        + "which the check pipeline treats as a miss) and only wrote dead JSON. It was already "
                        + "behaving as no cache at all. Fix .fshw.json: delete the \"cache\" key, or set "
                        + "\"cache\": \"memory\" for a real (in-process) cache."
                    )
                )
            | other ->
                Logging.warn "config" $"Unknown cache value '%s{other}', using default"
                defaults.Cache
        | _ -> defaults.Cache

    let analyzers =
        match root.TryGetProperty("analyzers") with
        | true, v ->
            let paths =
                match v.TryGetProperty("paths") with
                | true, arr -> arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
                | _ -> []

            let failOnSeverity =
                match v.TryGetProperty("failOnSeverity") with
                | true, s ->
                    let str = s.GetString()

                    match str with
                    | "error"
                    | "warning"
                    | "info"
                    | "hint" -> DiagnosticSeverity.fromString str |> Option.get
                    | other ->
                        Logging.warn "config" $"Unknown failOnSeverity value '%s{other}', using default 'hint'"
                        DiagnosticSeverity.Hint
                | _ -> DiagnosticSeverity.Hint

            if paths.IsEmpty then
                None
            else
                Some
                    {| Paths = paths
                       FailOnSeverity = failOnSeverity |}
        | _ -> None

    let tests =
        match root.TryGetProperty("tests") with
        | true, v ->
            // AUTOMATION-320: a STRING (one step, back-compatible) or an ARRAY
            // of steps. The array form is what makes per-step attribution
            // possible at all — a single `a && b && c` string is one opaque
            // process to the runner, so when it dies there is nothing to name.
            let beforeRun =
                match v.TryGetProperty("beforeRun") with
                | true, br when br.ValueKind = JsonValueKind.Array ->
                    let steps =
                        br.EnumerateArray()
                        |> Seq.map (fun e -> e.GetString())
                        |> Seq.filter (fun c -> not (String.IsNullOrWhiteSpace c))
                        |> Seq.toList

                    if steps.IsEmpty then None else Some steps
                | true, br when br.ValueKind = JsonValueKind.String -> Some [ br.GetString() ]
                | _ -> None

            let extensions =
                match v.TryGetProperty("extensions") with
                | true, arr ->
                    arr.EnumerateArray()
                    |> Seq.map (fun e ->
                        let kind =
                            match e.TryGetProperty("type") with
                            | true, v ->
                                match v.GetString().ToLowerInvariant() with
                                | "falco" -> Falco
                                | other -> Unknown other
                            | _ -> Unknown "unknown"

                        let project =
                            match e.TryGetProperty("project") with
                            | true, v -> v.GetString()
                            | _ -> "unknown"

                        let testDir =
                            match e.TryGetProperty("testDir") with
                            | true, v -> v.GetString()
                            | _ -> ""

                        { Kind = kind
                          Project = project
                          TestDir = testDir })
                    |> Seq.toList
                | _ -> []

            let projects =
                match v.TryGetProperty("projects") with
                | true, arr ->
                    arr.EnumerateArray()
                    |> Seq.map (fun p ->
                        let project =
                            match p.TryGetProperty("project") with
                            | true, v -> v.GetString()
                            | _ -> "unknown"

                        let command =
                            match p.TryGetProperty("command") with
                            | true, v -> v.GetString()
                            | _ -> "dotnet"

                        let args =
                            match p.TryGetProperty("args") with
                            | true, v -> v.GetString()
                            | _ -> $"test --project %s{project}"

                        let group =
                            match p.TryGetProperty("group") with
                            | true, v -> v.GetString()
                            | _ -> "default"

                        let env =
                            match p.TryGetProperty("environment") with
                            | true, envObj ->
                                envObj.EnumerateObject()
                                |> Seq.map (fun prop -> prop.Name, prop.Value.GetString())
                                |> Seq.toList
                            | _ -> []

                        let filterTemplate =
                            match p.TryGetProperty("filterTemplate") with
                            | true, v -> Some(v.GetString())
                            | _ -> None

                        let classJoin =
                            match p.TryGetProperty("classJoin") with
                            | true, v -> v.GetString()
                            | _ -> " "

                        // `coverage` may be bool (enable/disable, default template)
                        // or an object `{ "enabled": bool, "argsTemplate": string }`
                        // for per-project overrides.
                        let coverage, coverageArgsTemplate =
                            match p.TryGetProperty("coverage") with
                            | true, v when v.ValueKind = JsonValueKind.False -> false, None
                            | true, v when v.ValueKind = JsonValueKind.True -> true, None
                            | true, v when v.ValueKind = JsonValueKind.Object ->
                                let enabled =
                                    match v.TryGetProperty("enabled") with
                                    | true, e when e.ValueKind = JsonValueKind.False -> false
                                    | _ -> true

                                let tmpl =
                                    match v.TryGetProperty("argsTemplate") with
                                    | true, t when t.ValueKind = JsonValueKind.String -> Some(t.GetString())
                                    | _ -> None

                                enabled, tmpl
                            | _ -> true, None

                        let timeoutSec =
                            match p.TryGetProperty("timeoutSec") with
                            | true, t when t.ValueKind = JsonValueKind.Number -> Some(t.GetInt32())
                            | _ -> None

                        // `reportVerificationFormat`: how fshw obtains the structured
                        // test report the verdict is derived from. Absent → AutoDetect.
                        let reportVerificationFormat =
                            match p.TryGetProperty("reportVerificationFormat") with
                            | true, v when v.ValueKind = JsonValueKind.String ->
                                match v.GetString().ToLowerInvariant() with
                                | "ctrf" -> Ctrf
                                | "auto"
                                | "autodetect"
                                | "detect" -> AutoDetect
                                | "off"
                                | "none"
                                | "disabled"
                                | "false" -> Disabled
                                | other ->
                                    Logging.warn
                                        "config"
                                        $"Unknown reportVerificationFormat value '%s{other}', using AutoDetect"

                                    AutoDetect
                            | _ -> AutoDetect

                        { Project = project
                          Command = command
                          Args = args
                          Group = group
                          Environment = env
                          FilterTemplate = filterTemplate
                          ClassJoin = classJoin
                          Coverage = coverage
                          CoverageArgsTemplate = coverageArgsTemplate
                          TimeoutSec = timeoutSec
                          ReportVerificationFormat = reportVerificationFormat })
                    |> Seq.toList
                | _ -> []

            // AUTOMATION-158: `tests.excluded` — first-class, and the only
            // sanctioned way a solution test project may be outside the gate's
            // scope. Parsed by `SolutionScope.exclusionsOf`, the same function the
            // verdict writer reads them with, so the config that is VALIDATED and
            // the config that is RECORDED cannot be two different readings.
            let excluded = SolutionScope.exclusionsOf v

            let solution =
                match v.TryGetProperty("solution") with
                | true, s when s.ValueKind = JsonValueKind.String -> Some(s.GetString())
                | _ -> None

            let coverageDir =
                match v.TryGetProperty("coverageDir") with
                | true, cd when cd.ValueKind = JsonValueKind.String -> cd.GetString()
                | _ -> "coverage"

            // `tests.dependsOn`: repo-root-relative globs naming external inputs
            // (DB migrations, generated files, schemas) that test-prune's
            // symbol-diff can't see. Mirrors `build.dependsOn`'s string-array
            // shape. Absent → []  (no salt; cache behaves exactly as before).
            let dependsOn =
                match v.TryGetProperty("dependsOn") with
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
                | _ -> []

            if projects.IsEmpty then
                None
            else
                Some
                    {| BeforeRun = beforeRun
                       Extensions = extensions
                       Projects = projects
                       Excluded = excluded
                       Solution = solution
                       CoverageDir = coverageDir
                       DependsOn = dependsOn |}
        | _ -> None

    let fileCommands =
        match root.TryGetProperty("fileCommands") with
        | true, arr ->
            arr.EnumerateArray()
            |> Seq.map (fun fc ->
                let name =
                    match fc.TryGetProperty("name") with
                    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                    | _ -> None

                let pattern =
                    match fc.TryGetProperty("pattern") with
                    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                    | _ -> None

                let afterTests =
                    match fc.TryGetProperty("afterTests") with
                    | true, v when v.ValueKind = JsonValueKind.True ->
                        Some FsHotWatch.FileCommand.FileCommandPlugin.AnyTest
                    | true, v when v.ValueKind = JsonValueKind.False -> None
                    | true, v when v.ValueKind = JsonValueKind.Array ->
                        let projects = v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq

                        if projects.IsEmpty then
                            Logging.warn "config" "fileCommands entry has empty afterTests list; treating as absent"

                            None
                        else
                            Some(FsHotWatch.FileCommand.FileCommandPlugin.TestProjects projects)
                    | _ -> None

                let command =
                    match fc.TryGetProperty("command") with
                    | true, v -> v.GetString()
                    | _ -> "echo"

                let args =
                    match fc.TryGetProperty("args") with
                    | true, v -> v.GetString()
                    | _ -> ""

                if pattern.IsNone && afterTests.IsNone then
                    raise (ConfigError "fileCommands entry must specify `pattern` or `afterTests`")

                if afterTests.IsSome && name.IsNone then
                    raise (ConfigError "fileCommands entries with `afterTests` require an explicit `name`")

                // Validate the pattern shape at config-load so unsupported
                // globs (embedded `*`) fail with a clean `fshw: config error:`
                // exit instead of an unhandled ArgumentException later, at
                // plugin-registration time.
                match pattern with
                | Some p ->
                    try
                        FsHotWatch.Watcher.FilePattern.parse p |> ignore
                    with :? System.ArgumentException as ex ->
                        raise (ConfigError $"fileCommands pattern invalid: %s{ex.Message}")
                | None -> ()

                // Derive the effective plugin name up-front so registration is
                // a straight mapping. Uses the explicit `name` when given, else
                // falls back to a pattern-derived name (guaranteed Some here by
                // the validation above).
                let pluginName =
                    match name with
                    | Some n -> n
                    | None -> $"file-cmd-%s{Option.get pattern}"

                let timeoutSec =
                    match fc.TryGetProperty("timeoutSec") with
                    | true, t when t.ValueKind = JsonValueKind.Number -> Some(t.GetInt32())
                    | _ -> None

                {| PluginName = pluginName
                   Pattern = pattern
                   AfterTests = afterTests
                   Command = command
                   Args = args
                   TimeoutSec = timeoutSec |})
            |> Seq.toList
        | _ -> []

    let coverage =
        match root.TryGetProperty("coverage") with
        | true, v when v.ValueKind = JsonValueKind.Object ->
            let configPath =
                match v.TryGetProperty("configPath") with
                | true, cp when cp.ValueKind = JsonValueKind.String -> cp.GetString()
                | _ -> "coverage-ratchet.json"

            let searchDir =
                match v.TryGetProperty("searchDir") with
                | true, sd when sd.ValueKind = JsonValueKind.String -> sd.GetString()
                | _ -> "."

            Some
                {| ConfigPath = configPath
                   SearchDir = searchDir |}
        | _ -> None

    let exclude =
        match root.TryGetProperty("exclude") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
        | _ -> defaults.Exclude

    let includeOutsideRepo =
        match root.TryGetProperty("includeOutsideRepo") with
        | true, v when v.ValueKind = JsonValueKind.True -> true
        | true, v when v.ValueKind = JsonValueKind.False -> false
        | _ -> defaults.IncludeOutsideRepo

    let logDir =
        match root.TryGetProperty("logDir") with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> defaults.LogDir

    // `timeoutSec` (global default per-operation timeout, seconds):
    //   absent          → the baked-in default (`DefaultGlobalTimeoutSec`, see defaults);
    //   positive N      → N seconds;
    //   `0` / `false`   → DISABLED (no global default; ops fall back to each
    //                     plugin's own behaviour — `Infinite` for the
    //                     process-spawning plugins). An explicit opt-out for
    //                     repos that genuinely need unbounded ops.
    // Per-entry `timeoutSec` (build/tests.projects/fileCommands) still overrides
    // this global value.
    let timeoutSec =
        match root.TryGetProperty("timeoutSec") with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            let n = v.GetInt32()
            if n > 0 then Some n else None
        | true, v when v.ValueKind = JsonValueKind.False -> None
        | _ -> defaults.TimeoutSec

    // `fsEventsLatencyMs`: absent → default (250); a non-negative integer is used
    // verbatim (0 is valid — no coalescing); a present-but-invalid value
    // (negative, or non-number) warns and falls back to the default.
    let fsEventsLatencyMs =
        match root.TryGetProperty("fsEventsLatencyMs") with
        | false, _ -> defaults.FsEventsLatencyMs
        | true, v when v.ValueKind = JsonValueKind.Number ->
            let ms = v.GetInt32()

            if ms >= 0 then
                ms
            else
                Logging.warn
                    "config"
                    $"fsEventsLatencyMs must be >= 0, got %d{ms}; using default %d{defaults.FsEventsLatencyMs}"

                defaults.FsEventsLatencyMs
        | true, _ ->
            Logging.warn
                "config"
                $"fsEventsLatencyMs must be a non-negative integer; using default %d{defaults.FsEventsLatencyMs}"

            defaults.FsEventsLatencyMs

    // Parse a `number | false` tristate, shared by the two idle-exit windows:
    // positive N → `minutes N`; a non-positive number / `false` / `true` →
    // `disabled` (`true` is rejected rather than treated as an implicit window —
    // documented as `number | false`); absent or any other kind → `fallback`.
    let parseTristateMinutes (key: string) (minutes: int -> 'a) (disabled: 'a) (fallback: 'a) : 'a =
        match root.TryGetProperty(key) with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            let n = v.GetInt32()
            if n > 0 then minutes n else disabled
        | true, v when v.ValueKind = JsonValueKind.False -> disabled
        | true, v when v.ValueKind = JsonValueKind.True -> disabled
        | _ -> fallback

    // `idleExitMin`: absent → AUTO (30min for /.workspaces/ checkouts);
    // `0`/`false` → disabled; positive N → N minutes in any workspace.
    let idleExitMin =
        parseTristateMinutes
            "idleExitMin"
            IdleExit.IdleExitConfig.Minutes
            IdleExit.IdleExitConfig.Disabled
            defaults.IdleExitMin

    // `pressureIdleFloorMin`: absent → default-on at 2min; `0`/`false` →
    // pressure-shortening disabled; positive N → floor at N min.
    let pressureIdleFloorMin =
        parseTristateMinutes
            "pressureIdleFloorMin"
            IdleExit.PressureFloorConfig.Minutes
            IdleExit.PressureFloorConfig.Disabled
            defaults.PressureIdleFloorMin

    // Run-level hooks: deliberately TOP-LEVEL keys, kept separate from
    // `tests.beforeRun` (a different scope/cadence: inside the daemon, per test run).
    // Both are shell-command STRINGS; a present `false` or an absent key → None, so a
    // repo can opt a hook out without deleting it.
    let parseRunHook (key: string) : string option =
        match root.TryGetProperty(key) with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let beforeRun = parseRunHook "beforeRun"
    let afterRun = parseRunHook "afterRun"

    // `runHookTimeoutSec`: the same `number | false` tristate as the global
    // `timeoutSec`. A positive `number` → Some N; `0` / `false` / absent → None,
    // which `withRunHooks` resolves through global `timeoutSec` → the baked-in
    // default (a run-level hook is always bounded).
    let runHookTimeoutSec =
        match root.TryGetProperty("runHookTimeoutSec") with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            let n = v.GetInt32()
            if n > 0 then Some n else None
        | _ -> None

    // `runHookCommands`: which verbs the run-level hooks bracket. Every failure mode
    // leans towards KEEPING the bracket rather than quietly stopping — an un-gated heavy
    // run is far worse than a redundantly-gated one. Only an explicitly empty array can
    // disable bracketing, and even that is announced.
    let runHookCommands =
        let parseVerb (s: string) =
            match s.Trim().ToLowerInvariant() with
            | "check" -> Some RunHookCommand.Check
            | "confirm" -> Some RunHookCommand.Confirm
            | other ->
                Logging.warn
                    "config"
                    $"Unknown runHookCommands entry '%s{other}' (expected \"check\" or \"confirm\") — ignoring it"

                None

        match root.TryGetProperty("runHookCommands") with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let entries = v.EnumerateArray() |> Seq.toList

            let parsed =
                entries
                |> List.choose (fun e ->
                    if e.ValueKind = JsonValueKind.String then
                        parseVerb (e.GetString())
                    else
                        Logging.warn "config" "Ignoring a non-string runHookCommands entry"
                        None)
                |> Set.ofList

            if List.isEmpty entries then
                // Explicit `[]`. Honoured — the config said it plainly — but never
                // silently, because this disables the bracket for EVERY verb.
                Logging.warn
                    "config"
                    "runHookCommands is empty — the run-level beforeRun/afterRun hooks will not bracket ANY command"

                Set.empty
            elif Set.isEmpty parsed then
                // Non-empty, but nothing survived parsing (a typo, most likely).
                // Falling back to both verbs keeps the safe direction.
                Logging.warn
                    "config"
                    "No usable entries in runHookCommands — falling back to bracketing both check and confirm"

                DefaultRunHookCommands
            else
                parsed
        | true, v when
            v.ValueKind = JsonValueKind.String
            || v.ValueKind = JsonValueKind.Number
            || v.ValueKind = JsonValueKind.Object
            ->
            Logging.warn
                "config"
                "runHookCommands must be an array of \"check\" / \"confirm\" — ignoring it and bracketing both"

            DefaultRunHookCommands
        // Absent, `null`, `false`, `true` → the default. `false` reads as "I am not
        // using this key", the same opt-out spirit as `runHookTimeoutSec`.
        | _ -> DefaultRunHookCommands

    { Build = build
      Format = format
      Lint = lint
      Cache = cache
      Analyzers = analyzers
      Tests = tests
      FileCommands = fileCommands
      Coverage = coverage
      Exclude = exclude
      IncludeOutsideRepo = includeOutsideRepo
      LogDir = logDir
      TimeoutSec = timeoutSec
      IdleExitMin = idleExitMin
      PressureIdleFloorMin = pressureIdleFloorMin
      FsEventsLatencyMs = fsEventsLatencyMs
      BeforeRun = beforeRun
      AfterRun = afterRun
      RunHookTimeoutSec = runHookTimeoutSec
      RunHookCommands = runHookCommands }

/// Strip a config down to a minimal base for run-once subcommands.
/// Disables all plugins except format preprocessor. Caller overrides specific fields.
///
/// The run-level `BeforeRun`/`AfterRun`/`RunHookTimeoutSec`/`RunHookCommands` settings
/// are DELIBERATELY preserved: `--run-once` is the transport CI uses, so the box-wide
/// gate-lock those hooks bracket must fire there too, and must bracket the SAME verbs,
/// or the verb policy would mean something different in CI than locally.
let stripConfig (config: DaemonConfiguration) : DaemonConfiguration =
    { config with
        Build = Some []
        Lint = false
        Analyzers = None
        Tests = None
        FileCommands = [] }

/// AUTOMATION-158. Reconcile the configured test scope with the solution and
/// raise `ConfigError` on any disagreement.
///
/// Runs on EVERY config load — which is every fshw invocation, in the CLI
/// process — rather than once when a daemon boots. That placement is the whole
/// guarantee: a test project added to the solution without a matching
/// `.fshw.json` edit fails the very next `check` / `confirm`, even against a
/// warm daemon that has not reloaded anything since before the project existed.
///
/// Only fires when the config CLAIMS a test scope. A repo that configures no test
/// projects makes no full-suite claim to be incomplete: its scope is
/// `NoTestsRun`/`ScopeUnknown` and `confirm` already refuses to build a merge
/// verdict from either. So there is nothing here to fail closed about, and
/// failing a lint-only repo over test projects it never asked fshw to run would
/// be noise, not safety.
let internal validateTestScope (repoRoot: string) (config: DaemonConfiguration) : unit =
    match config.Tests with
    | None -> ()
    | Some t ->
        let gated =
            t.Projects
            |> List.map (fun p ->
                { SolutionScope.GatedProject.Project = p.Project
                  SolutionScope.GatedProject.Args = p.Args })

        match SolutionScope.reconcile repoRoot t.Solution gated t.Excluded with
        | [] ->
            // The declared gaps are LOGGED on the green path too. A declaration
            // that only ever appears inside a config file nobody re-reads is one
            // refactor away from being the silence again.
            for e in t.Excluded do
                Logging.warn "config" $"NOT RUN — %s{e.Project}: %s{e.Reason}"
        | findings ->
            raise (
                ConfigError(SolutionScope.describeFindings (SolutionScope.solutionNameFor repoRoot t.Solution) findings)
            )

/// Load config from .fshw.json in repoRoot. Returns defaults if no file exists.
/// Raises ConfigError on read / parse / validation failure.
let loadConfig (repoRoot: string) : DaemonConfiguration =
    let configPath = Path.Combine(repoRoot, ".fshw.json")

    let defaults = defaultConfigFor repoRoot

    if not (File.Exists configPath) then
        Logging.info "config" "No .fshw.json found, using defaults (build + format + lint)"
        defaults
    else
        let json =
            try
                File.ReadAllText(configPath)
            with ex ->
                raise (ConfigError $"Cannot read .fshw.json: %s{ex.Message}")

        try
            let config = parseConfig json defaults
            validateTestScope repoRoot config
            Logging.info "config" "Loaded .fshw.json"
            config
        with
        | ConfigError _ -> reraise ()
        | ex -> raise (ConfigError $".fshw.json: %s{ex.Message}")

/// Count the plugins that would be registered for a given configuration.
/// Used by `fshw config check` to report how many plugins are configured.
let countPlugins (config: DaemonConfiguration) : int =
    let buildCount =
        match config.Build with
        | Some builds -> List.length builds
        | None -> 0

    let lintCount = if config.Lint then 1 else 0
    let analyzerCount = if config.Analyzers.IsSome then 1 else 0
    let testsCount = if config.Tests.IsSome then 1 else 0
    let fcCount = List.length config.FileCommands
    buildCount + lintCount + analyzerCount + testsCount + fcCount

/// Invoke `onChange reason`; if it throws, surface the failure via `logError`.
/// Extracted (rather than inlined into `watchConfigFile`) so the boundary's behaviour is
/// unit-testable without a process-global `Console.SetError` capture, which races across
/// parallel tests.
///
/// Reraising from a FileSystemWatcher threadpool callback would crash the process via
/// unhandled-threadpool-exception, so the failure goes to the error log and execution
/// continues — the user sees it instead of the daemon mysteriously ignoring config edits.
let invokeOnChangeWith (logError: string -> unit) (onChange: string -> unit) (reason: string) : unit =
    try
        onChange reason
    with ex ->
        logError $"onChange callback failed (config edit not propagated): %s{ex.ToString()}"

/// Debounce decision for the config watcher: true when `now` is more than
/// `window` past the previous accepted fire (and records it as the new last
/// fire). Extracted (like `invokeOnChangeWith` above) so BOTH arms are
/// unit-testable with an injected clock: the suppressed arm only executes in
/// production when the OS double-fires events within the window, which is
/// nondeterministic.
let internal debounceShouldFire (gate: obj) (lastFire: DateTime ref) (window: TimeSpan) (now: DateTime) : bool =
    lock gate (fun () ->
        if now - lastFire.Value > window then
            lastFire.Value <- now
            true
        else
            false)

/// Compute the human-readable reason for a config-file change by re-parsing
/// the file: distinguishes "config changed" from "config invalid". Extracted
/// for the same deterministic-coverage reason as `debounceShouldFire` — both
/// arms are pinned by direct unit tests instead of depending on which FSW
/// events the OS happens to deliver.
let internal configChangeReason (configPath: string) (defaults: DaemonConfiguration) : string =
    try
        let _ = parseConfig (File.ReadAllText configPath) defaults
        "config changed, stopping (restart to apply)"
    with ex ->
        $"config invalid, stopping: %s{ex.Message}"

/// One config-watcher FS event: debounce, compute the reason, dispatch.
/// `now` is injected so the debounce path is unit-testable; the FSW lambda in
/// `watchConfigFile` passes `DateTime.UtcNow` and stays branchless (its line
/// coverage is pinned by the RealWatchTests; all branches live here and in the
/// helpers above, covered deterministically).
let internal onConfigFsEvent
    (gate: obj)
    (lastFire: DateTime ref)
    (window: TimeSpan)
    (configPath: string)
    (defaults: DaemonConfiguration)
    (logError: string -> unit)
    (onChange: string -> unit)
    (now: DateTime)
    : unit =
    if debounceShouldFire gate lastFire window now then
        invokeOnChangeWith logError onChange (configChangeReason configPath defaults)

/// Watch `.fshw.json` for any write/rename/create and invoke the callback
/// once with a human-readable reason. Re-parses the file to distinguish
/// "config changed" from "config invalid, stopping".
///
/// Debounces bursts (editors commonly emit multiple events per save) so the
/// callback fires at most once per ~200 ms window. Returns a disposable that
/// stops watching.
let watchConfigFile (configPath: string) (onChange: string -> unit) : IDisposable =
    let dir = Path.GetDirectoryName(configPath)
    let name = Path.GetFileName(configPath)
    let watcher = new FileSystemWatcher(dir, name)

    watcher.NotifyFilter <-
        NotifyFilters.LastWrite
        ||| NotifyFilters.FileName
        ||| NotifyFilters.Size
        ||| NotifyFilters.CreationTime

    // Capture defaults once at construction — defaultConfigFor probes the
    // filesystem (.jj detection) and we don't want that on every event.
    let defaults = defaultConfigFor dir
    let lastFire = ref DateTime.MinValue
    let gate = obj ()
    let window = TimeSpan.FromMilliseconds(200.0)

    let handler (_: FileSystemEventArgs) =
        onConfigFsEvent
            gate
            lastFire
            window
            configPath
            defaults
            (FsHotWatch.Logging.error "config-watcher")
            onChange
            DateTime.UtcNow

    watcher.Changed.Add(handler)
    watcher.Created.Add(handler)
    watcher.Renamed.Add(fun e -> handler (FileSystemEventArgs(WatcherChangeTypes.Renamed, dir, e.Name)))
    watcher.EnableRaisingEvents <- true
    watcher :> IDisposable

/// Watch `.fshw.json` at `repoRoot` if it exists, otherwise return a
/// no-op disposable. Keeps the `start` call-site tidy and gives tests a
/// direct entry point.
let watchRepoConfigFile (repoRoot: string) (onChange: string -> unit) : IDisposable =
    let configPath = Path.Combine(repoRoot, ".fshw.json")

    if File.Exists configPath then
        watchConfigFile configPath onChange
    else
        { new IDisposable with
            member _.Dispose() = () }


/// Split the (command, args) pair a shell hook should actually invoke.
/// Runs the user's command string through `/bin/sh -c` so shell features
/// (`&&`, pipes, globs, env-var interpolation) work as written in
/// `.fshw.json`. Unix-only — Windows isn't a supported platform.
/// Exposed for unit-testability.
let shellInvocation (cmd: string) : string * string =
    // Escape embedded double quotes so the outer `"..."` around the -c
    // argument stays balanced for /bin/sh.
    let escaped = cmd.Replace("\"", "\\\"")
    "/bin/sh", "-c \"" + escaped + "\""

/// Wrap a shell command string into a callback that runs it as a bounded child.
///
/// This runs INSIDE the `RunExclusive "tests"` slot, so a hook that hangs — a `dotnet
/// restore` stuck on the network, or one that EXITS while a grandchild MSBuild node
/// still holds the inherited stdout pipe — would hold the tests slot for good (the
/// plugin stays `Running`, every later `check` burns its full deadline). Hence a
/// `ProcessBounds.silent` child (`dotnet restore --verbosity quiet` prints nothing, so
/// output cannot prove liveness) bounded by `timeoutSec`: a hung hook TIMES OUT into
/// `Failed`/`Aborted` with a legible diagnostic.
/// AUTOMATION-320 — why a hook step failed, in a form that CANNOT be empty.
///
/// The old path logged `$"%s{label} failed:\n%s{output}"`. When the failing
/// process wrote nothing to either stream — which is exactly what happened on a
/// healthy box, four `confirm` runs in a row — that rendered as the literal text
/// `beforeRun failed:` and nothing else. No step, no exit code, no output. The
/// only way to find the culprit was to run all nine commands by hand, and it
/// very nearly misattributed the blame to the change under test.
///
/// So the reason is a RECORD, not a string. Every field a reader needs is
/// required to build one, which is what makes the empty message unconstructible
/// rather than merely discouraged.
type internal HookFailure =
    {
        Label: string
        /// 1-based position in the chain, with the total, so "which link broke" is
        /// answerable without counting.
        StepIndex: int
        StepCount: int
        /// The exact command that failed — not the whole chain.
        Command: string
        Outcome: ProcessOutcome
    }

module internal HookFailure =

    /// The operator-facing message. Non-empty by construction: even a process
    /// that wrote nothing still yields the step, its position and its
    /// disposition, and says SO EXPLICITLY that there was no output rather than
    /// trailing off after a colon.
    let describe (f: HookFailure) : string =
        let where =
            if f.StepCount > 1 then
                $"step %d{f.StepIndex}/%d{f.StepCount}"
            else
                "the command"

        let disposition =
            match f.Outcome with
            | Succeeded _ -> "reported success but was treated as failed (this is a bug in the hook runner)"
            | Failed(code, _) -> $"exited %d{code}"
            | TimedOut(after, _, _) -> $"timed out after %d{int after.TotalSeconds}s"

        let output =
            match outputOf f.Outcome with
            | o when String.IsNullOrWhiteSpace o -> "(no output on stdout or stderr)"
            | o -> o.TrimEnd()

        $"%s{f.Label} failed at %s{where}: %s{disposition}\n  command: %s{f.Command}\n  output:\n%s{output}"

/// The result of running a hook chain. A DU rather than `Result<unit, _>`
/// because `Error` is already a `DiagnosticSeverity` case in this file, and the
/// collision made every neighbouring match ambiguous.
type internal HookOutcome =
    | HookOk
    | HookFailed of HookFailure

/// Run an ordered chain of shell steps, stopping at the first failure and
/// reporting WHICH one broke. One step is the degenerate case of the same path,
/// so the string and array config forms share every line of this.
let internal runShellSteps
    (label: string)
    (timeoutSec: int option)
    (repoRoot: string)
    (steps: string list)
    : HookOutcome =
    let bounds =
        ProcessBounds.silent (
            match timeoutSec with
            | Some s -> TimeSpan.FromSeconds(float s)
            | None -> Threading.Timeout.InfiniteTimeSpan
        )

    let count = List.length steps

    steps
    |> List.indexed
    |> List.fold
        (fun acc (i, cmd) ->
            match acc with
            | HookFailed _ -> acc // first failure wins; do not run the rest
            | HookOk ->
                Logging.info label $"Running %s{label} step %d{i + 1}/%d{count}: %s{cmd}"
                let (command, args) = shellInvocation cmd
                let outcome = runProcess command args repoRoot [] bounds

                if isSucceeded outcome then
                    HookOk
                else
                    HookFailed
                        { Label = label
                          StepIndex = i + 1
                          StepCount = count
                          Command = cmd
                          Outcome = outcome })
        HookOk

let internal makeShellHookWithResult
    (label: string)
    (timeoutSec: int option)
    (repoRoot: string)
    (cmd: string)
    : unit -> bool * string =
    let bounds =
        ProcessBounds.silent (
            match timeoutSec with
            | Some s -> TimeSpan.FromSeconds(float s)
            | None -> Threading.Timeout.InfiniteTimeSpan
        )

    fun () ->
        Logging.info label $"Running %s{label}: %s{cmd}"
        let (command, args) = shellInvocation cmd
        let result = runProcess command args repoRoot [] bounds
        let success = isSucceeded result
        let output = outputOf result

        if not success then
            // AUTOMATION-320: the RUN-level hook had the same empty-reason bug as
            // `tests.beforeRun` — it interpolated the output, so a step that wrote
            // nothing logged `<label> failed:` and stopped. Same fix, same renderer.
            Logging.error
                label
                (HookFailure.describe
                    { Label = label
                      StepIndex = 1
                      StepCount = 1
                      Command = cmd
                      Outcome = result })

        success, output

/// Wrap a shell command string into a fire-and-forget callback.
/// If failOnError is true, raises on failure; otherwise only logs.
let private makeShellHook
    (label: string)
    (failOnError: bool)
    (timeoutSec: int option)
    (repoRoot: string)
    (cmd: string)
    : unit -> unit =
    let hook = makeShellHookWithResult label timeoutSec repoRoot cmd

    fun () ->
        let (success, output) = hook ()

        if not success && failOnError then
            // Surface the captured output in the raised message, not just the
            // command string: a failing `beforeRun` throw propagates through
            // TestPrune's Aborted lifecycle into the plugin's Failed status, so
            // including the hook's stdout/stderr here is what makes `fshw check`
            // / `fshw errors` show WHY the preflight failed, rather than only
            // that it did.
            failwith $"%s{label} failed: %s{cmd}\n%s{output}"

/// Register plugins on the daemon based on the loaded configuration.
let registerPlugins (daemon: Daemon) (repoRoot: string) (config: DaemonConfiguration) =
    // Format plugin
    match config.Format with
    | Auto ->
        Logging.info "config" "Registering FormatPreprocessor"

        // A preprocessor runs inside the change agent AND inside the scan, so an
        // unbounded one is the worse of the two to leave uncapped.
        daemon.RegisterPreprocessor(
            match config.TimeoutSec with
            | Some s -> FsHotWatch.Fantomas.FormatCheckPlugin.FormatPreprocessor(timeoutSec = s)
            | None -> FsHotWatch.Fantomas.FormatCheckPlugin.FormatPreprocessor()
        )
    | Check ->
        Logging.info "config" "Registering FormatCheckPlugin (read-only)"
        daemon.RegisterHandler(FsHotWatch.Fantomas.FormatCheckPlugin.createFormatCheck config.TimeoutSec)
    | Off -> ()

    // When includeOutsideRepo is false (default), the report-producing plugins
    // (analyzers, lint) skip compile items outside repoRoot (NuGet-injected
    // `_content` etc.); None disables the skip. obj/bin is always skipped
    // independently (PathFilter.isGeneratedPath).
    let outsideRepoScope = if config.IncludeOutsideRepo then None else Some repoRoot

    // Lint plugin
    if config.Lint then
        let lintConfigPath =
            let p = Path.Combine(repoRoot, "fsharplint.json")
            if File.Exists(p) then Some p else None

        match lintConfigPath with
        | Some path -> Logging.info "config" $"Registering LintPlugin with config: %s{path}"
        | None -> Logging.info "config" "Registering LintPlugin (no fsharplint.json found)"

        daemon.RegisterHandler(FsHotWatch.Lint.LintPlugin.create outsideRepoScope lintConfigPath None config.TimeoutSec)

    // Analyzers plugin
    match config.Analyzers with
    | Some a ->
        let absolutePaths =
            a.Paths
            |> List.map (fun p ->
                if Path.IsPathRooted(p) then
                    p
                else
                    Path.GetFullPath(Path.Combine(repoRoot, p)))

        let resolvedPaths =
            resolveExistingPathsWithRetry Directory.Exists System.Threading.Thread.Sleep absolutePaths

        if resolvedPaths.Length < absolutePaths.Length then
            let missing =
                absolutePaths |> List.filter (Directory.Exists >> not) |> String.concat ", "

            Logging.warn
                "config"
                $"Analyzers: %d{resolvedPaths.Length}/%d{absolutePaths.Length} paths resolved (missing after retry: %s{missing})"

        // Build the handler unconditionally (even when resolvedPaths is empty) so
        // its per-path load result is known: that is what the fail-loud guard
        // inspects. The factory loads analyzers eagerly, so Init.LoadedByPath is
        // the real per-path result.
        let handler =
            FsHotWatch.Analyzers.AnalyzersPlugin.create
                outsideRepoScope
                resolvedPaths
                config.TimeoutSec
                a.FailOnSeverity

        // Map every ORIGINAL configured path to its contributed analyzer count:
        // 0 if it was dropped during resolution (missing after retry), else the
        // plugin's per-path count (0 if it resolved but loaded no analyzer DLLs).
        let countByResolvedPath = handler.Init.LoadedByPath |> Map.ofList

        let loadedByOriginalPath =
            absolutePaths
            |> List.map (fun p -> p, (Map.tryFind p countByResolvedPath |> Option.defaultValue 0))

        // Fail-loud — see `analyzerPathFailures`.
        match analyzerPathFailures loadedByOriginalPath with
        | Some message -> raise (ConfigError message)
        | None ->
            // Every configured path loaded ≥1 here: we are inside `Some a`, so paths
            // were given, and the guard above only passes when none contributed 0.
            let loadedCount = handler.Init.LoadedCount

            Logging.info
                "config"
                $"Registering AnalyzersPlugin with %d{resolvedPaths.Length} paths (%d{loadedCount} analyzers loaded)"

            daemon.RegisterHandler(handler)
    | None -> ()

    // Build plugin(s)
    match config.Build with
    | Some builds when not builds.IsEmpty ->
        let testProjectNames =
            match config.Tests with
            | Some t -> t.Projects |> List.map (fun p -> p.Project)
            | None -> []

        for b in builds do
            Logging.info "config" $"Registering BuildPlugin: %s{b.Command} %s{b.Args}"

            let buildTimeout = b.TimeoutSec |> Option.orElse config.TimeoutSec

            daemon.RegisterHandler(
                // AUTOMATION-368: REPORT-ONLY in the daemon. Recording MSBuild's
                // TargetPath made the artifact gate reachable for the first time;
                // switching a build-reddening predicate on across every consuming
                // repo in the same change that makes it possible is how you turn
                // "no protection" into "every build red on a false reading".
                FsHotWatch.Build.BuildPlugin.createWith
                    false
                    b.Command
                    b.Args
                    []
                    daemon.Graph
                    testProjectNames
                    b.BuildTemplate
                    b.DependsOn
                    buildTimeout
            )
    | _ -> ()

    // TestPrune plugin
    match config.Tests with
    | Some t ->
        let dbPath = Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-impact.db")
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)) |> ignore

        let testConfigs =
            t.Projects
            |> List.map (fun p ->
                { TestConfig.Project = p.Project
                  Command = p.Command
                  Args = p.Args
                  Group = p.Group
                  Environment = p.Environment
                  FilterTemplate = p.FilterTemplate
                  ClassJoin = p.ClassJoin
                  TimeoutSec = p.TimeoutSec
                  ReportVerificationFormat = p.ReportVerificationFormat })

        // AUTOMATION-320: fail LOUD and SPECIFIC. `runShellSteps` names the step,
        // its exit code and its output; raising `describe` means the plugin's
        // `runTests failed: …` wrapper now carries that instead of a bare colon.
        let beforeRun =
            t.BeforeRun
            |> Option.map (fun steps ->
                fun () ->
                    match runShellSteps "beforeRun" config.TimeoutSec repoRoot steps with
                    | HookOk -> ()
                    | HookFailed failure ->
                        let message = HookFailure.describe failure
                        Logging.error "beforeRun" message
                        failwith message)

        // Coverage paths — resolve per-project artifact locations (respecting per-project opt-out).
        // TestPrune itself decides whether a given run writes baseline.json or partial.json
        // and performs the merge step; this function only exposes the three paths per project.
        let projectsByName = t.Projects |> List.map (fun p -> p.Project, p) |> Map.ofList

        let coverageExcludedProjects =
            t.Projects
            |> List.filter (fun p -> not p.Coverage)
            |> List.map (fun p -> p.Project)
            |> Set.ofList

        // Coverage artifacts live under <repoRoot>/<tests.coverageDir>/<project>/
        // so external coverage tools (e.g. coverageratchet invoked via a
        // fileCommands afterTests entry) can read the cobertura output. The
        // output directory is configurable via `tests.coverageDir` (default
        // `"coverage"`). Per-project opt-out is honored via `coverageExcludedProjects`.
        // The ArgsTemplate is per-project (defaults to the MTP-cobertura template),
        // so alternate test runners can swap in their own coverage invocation
        // without patching the plugin.
        let coveragePaths =
            Some(fun (project: string) ->
                if coverageExcludedProjects.Contains(project) then
                    None
                else
                    let outputDir = Path.Combine(repoRoot, t.CoverageDir, project)
                    Directory.CreateDirectory(outputDir) |> ignore

                    let argsTemplate =
                        Map.tryFind project projectsByName
                        |> Option.bind (fun p -> p.CoverageArgsTemplate)
                        |> Option.defaultValue FsHotWatch.TestPrune.TestPrunePlugin.defaultCoverageArgsTemplate

                    Some
                        { FsHotWatch.TestPrune.TestPrunePlugin.CoveragePaths.Baseline =
                            Path.GetFullPath(
                                Path.Combine(outputDir, FsHotWatch.TestPrune.TestPrunePlugin.BaselineName)
                            )
                          Partial =
                            Path.GetFullPath(Path.Combine(outputDir, FsHotWatch.TestPrune.TestPrunePlugin.PartialName))
                          // Single SHARED cobertura for EVERY project: the TestPrune DB
                          // unions coverage across all test projects, then emits once to
                          // this one file (no per-project subdir) — what coverageratchet checks.
                          Cobertura =
                            Path.GetFullPath(
                                Path.Combine(
                                    repoRoot,
                                    t.CoverageDir,
                                    FsHotWatch.TestPrune.TestPrunePlugin.CoberturaName
                                )
                            )
                          ArgsTemplate = argsTemplate })

        // Extension factories — invoked by the plugin with its own DB, so the
        // RouteStore/SymbolStore an extension captures is guaranteed to be the
        // same DB the plugin queries against.
        let buildExtensions =
            match t.Extensions with
            | [] -> None
            | exts ->
                Some(fun (db: TestPrune.Database.Database) ->
                    // Routes are not a core concept: TestPrune.Core exposes only the
                    // generic `PluginStore` seam (a connection to its cache DB), and
                    // Falco's RouteStore creates and owns `route_handlers` on top of it.
                    let routeStore = TestPrune.Falco.RouteStore(TestPrune.Ports.toPluginStore db)

                    exts
                    |> List.choose (fun ext ->
                        match ext.Kind with
                        | Falco ->
                            Logging.info
                                "config"
                                $"Creating FalcoRouteExtension for %s{ext.Project} (%s{ext.TestDir})"

                            Some(
                                TestPrune.Falco.FalcoRouteExtension(ext.Project, ext.TestDir, routeStore)
                                :> TestPrune.Extensions.ITestPruneExtension
                            )
                        | Unknown other ->
                            Logging.warn "config" $"Unknown test extension type: %s{other}"
                            None))

        Logging.info "config" $"Registering TestPrunePlugin with %d{testConfigs.Length} test projects"

        let handler =
            create dbPath repoRoot (Some testConfigs) buildExtensions beforeRun None coveragePaths t.DependsOn

        daemon.RegisterHandler(handler)
    | None -> ()

    // File commands
    for fc in config.FileCommands do
        let parsedPattern = fc.Pattern |> Option.map FsHotWatch.Watcher.FilePattern.parse

        let trigger: FsHotWatch.FileCommand.FileCommandPlugin.CommandTrigger =
            { FilePattern =
                parsedPattern
                |> Option.map (fun p -> fun (path: string) -> FsHotWatch.Watcher.FilePattern.matches p path)
              AfterTests = fc.AfterTests }

        Logging.info "config" $"Registering FileCommandPlugin: %s{fc.PluginName} → %s{fc.Command} %s{fc.Args}"

        let fcTimeout = fc.TimeoutSec |> Option.orElse config.TimeoutSec

        daemon.RegisterHandler(
            FsHotWatch.FileCommand.FileCommandPlugin.create
                (FsHotWatch.PluginFramework.PluginName.create fc.PluginName)
                trigger
                fc.Command
                fc.Args
                repoRoot
                fcTimeout
        )

        // Expose the parsed pattern to the host so the rerun IPC endpoint
        // can synthesize a matching fake file path.
        match parsedPattern with
        | Some pattern -> daemon.Host.RegisterFileCommandPattern(fc.PluginName, pattern)
        | None -> ()

    // Coverage plugin
    match config.Coverage with
    | Some cov ->
        let absSearchDir =
            if Path.IsPathRooted(cov.SearchDir) then
                cov.SearchDir
            else
                Path.GetFullPath(Path.Combine(repoRoot, cov.SearchDir))

        let absConfigPath =
            if Path.IsPathRooted(cov.ConfigPath) then
                cov.ConfigPath
            else
                Path.GetFullPath(Path.Combine(repoRoot, cov.ConfigPath))

        if not (File.Exists absConfigPath) then
            Logging.warn "config" $"CoveragePlugin: configPath does not exist: %s{absConfigPath}"

        if not (Directory.Exists absSearchDir) then
            Logging.warn "config" $"CoveragePlugin: searchDir does not exist: %s{absSearchDir}"

        Logging.info "config" $"Registering CoveragePlugin: config=%s{absConfigPath} searchDir=%s{absSearchDir}"
        daemon.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create absConfigPath absSearchDir)
    | None -> ()
