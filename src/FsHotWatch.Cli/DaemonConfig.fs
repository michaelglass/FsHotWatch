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
    /// No check-result cache. This is the DEFAULT: the cache holds live FCS results
    /// (about 250 KB per file beyond what the checker keeps), so a repository opts in.
    | NoCache
    /// In-memory cache (lost on restart), holding the live `FileCheckResult`.
    | InMemory of InMemoryCacheSettings

/// `cache.maxEntries`.
and [<RequireQualifiedAccess>] CacheSize =
    /// A fixed budget. Below the number of files cached, a scan gets ~0 hits (it is
    /// warned about at startup).
    | Entries of int
    /// `"all"`: every file the cache admits — the size a repeated scan needs.
    | All

/// `cache.scope`: which checkouts of the repository run a cache.
and [<RequireQualifiedAccess>] CacheScope =
    /// `"all"`: every checkout.
    | AllCheckouts
    /// `"default-workspace"`: only the jj default workspace / git main checkout, so
    /// short-lived task workspaces (jj secondary workspaces, git worktrees) do not each
    /// hold a full cache. Read from the filesystem, not from config, since every
    /// workspace shares the tracked `.fshw.json`.
    | DefaultWorkspaceOnly

/// `"cache": { "maxEntries", "scope", "include", "exclude" }`.
and InMemoryCacheSettings =
    {
        MaxEntries: CacheSize
        Scope: CacheScope
        /// Gitignore-style globs over repo-relative PROJECT paths; empty = every project.
        Include: string list
        /// Globs over repo-relative project paths never cached; wins over `Include`.
        Exclude: string list
    }

/// `"cache": "memory"` / `true`: every file, every checkout, every project.
let defaultInMemoryCache =
    { MaxEntries = CacheSize.All
      Scope = CacheScope.AllCheckouts
      Include = []
      Exclude = [] }

/// Whether `scope` runs a cache in a checkout of `kind`, and the startup line saying
/// what was detected and what follows from it. A checkout whose layout cannot be read
/// is not provably the default workspace, so `"default-workspace"` leaves it off.
let resolveCacheScope
    (scope: CacheScope)
    (kind: Result<FsHotWatch.RepositoryIdentity.CheckoutKind, FsHotWatch.RepositoryIdentity.IdentityError>)
    : bool * string =
    let detected =
        match kind with
        | Result.Ok kind -> FsHotWatch.RepositoryIdentity.CheckoutKind.describe kind
        | Result.Error error ->
            $"a checkout whose layout cannot be read (%s{FsHotWatch.RepositoryIdentity.IdentityError.describe error})"

    let secondaryOrUnknown =
        kind
        |> Result.map FsHotWatch.RepositoryIdentity.CheckoutKind.isSecondary
        |> Result.defaultValue true

    match scope with
    | CacheScope.AllCheckouts -> true, $"check-result cache: cache.scope is \"all\"; this checkout is %s{detected}"
    | CacheScope.DefaultWorkspaceOnly when secondaryOrUnknown ->
        false,
        $"check-result cache: OFF in this checkout — cache.scope is \"default-workspace\" and this is %s{detected}"
    | CacheScope.DefaultWorkspaceOnly ->
        true, $"check-result cache: ON — cache.scope is \"default-workspace\" and this is %s{detected}"

/// Which projects the cache holds: a project path (absolute) is admitted when it
/// matches an `include` glob (or `include` is empty) and no `exclude` glob. Globs are
/// gitignore-style, relative to the repository root.
let cacheAdmits (repoRoot: string) (includes: string list) (excludes: string list) : string -> bool =
    let matcher (patterns: string list) =
        let ignore = (Ignore.Ignore(), patterns) ||> List.fold (fun ig p -> ig.Add(p))

        fun (relative: string) -> ignore.IsIgnored relative

    let included =
        if List.isEmpty includes then
            (fun _ -> true)
        else
            matcher includes

    let excluded =
        if List.isEmpty excludes then
            (fun _ -> false)
        else
            matcher excludes

    fun projectPath ->
        let relative = Path.GetRelativePath(repoRoot, projectPath).Replace('\\', '/')

        if relative.StartsWith("../", System.StringComparison.Ordinal) then
            List.isEmpty includes
        else
            included relative && not (excluded relative)

/// Create cache backend and key provider from config. Logs the scope decision.
let createCacheComponents
    (repoRoot: string)
    (config: CacheBackendConfig)
    : (ICheckCacheBackend option * ICacheKeyProvider option) =
    match config with
    | NoCache -> (None, None)
    | InMemory settings ->
        let on, message =
            resolveCacheScope
                settings.Scope
                (FsHotWatch.RepositoryIdentity.resolveWorktree repoRoot |> Result.map _.Kind)

        Logging.info "cache" message

        if not on then
            (None, None)
        else
            let capacity =
                match settings.MaxEntries with
                | CacheSize.Entries n -> FsHotWatch.InMemoryCheckCache.CacheCapacity.Entries n
                | CacheSize.All -> FsHotWatch.InMemoryCheckCache.CacheCapacity.WorkingSet

            let cache =
                FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(
                    capacity,
                    cacheAdmits repoRoot settings.Include settings.Exclude
                )

            (Some(cache :> ICheckCacheBackend), Some(TimestampCacheKeyProvider() :> ICacheKeyProvider))

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
/// empty loads 0 from that path yet would stay green because the OTHERS loaded. Every
/// per-path failure mode collapses to a count of 0; `AnalyzerPathDiagnosis` then reads
/// the filesystem to say which one each path is.
///
/// Input is the per-ORIGINAL-configured-path (absolute path, contributed count) list and
/// the bootstrap hint for each absolute path. Returns `Some message` naming each
/// zero-loading path with its classification when any exists, `None` otherwise —
/// unconfigured (empty list), or every path loaded ≥1.
let analyzerPathFailuresWith (hintFor: string -> string option) (loadedByPath: (string * int) list) : string option =
    let zeroPaths =
        loadedByPath |> List.filter (fun (_, count) -> count = 0) |> List.map fst

    match zeroPaths with
    | [] -> None
    | paths -> Some(AnalyzerPathDiagnosis.render (AnalyzerPathDiagnosis.diagnose hintFor paths))

/// `analyzerPathFailuresWith` for a configuration that declares no bootstrap hints.
let analyzerPathFailures (loadedByPath: (string * int) list) : string option =
    analyzerPathFailuresWith (fun _ -> None) loadedByPath


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

/// Where the daemon writes `daemon.log` when `.fshw.json` says nothing. Relative to the
/// repo root; `logDir` overrides it, and may be absolute. ONE definition, because every
/// message that points a reader at the log has to name the same place the daemon writes
/// it — see `DaemonLog`.
[<Literal>]
let DefaultLogDir = "logs"

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
        /// Collect runner coverage for TestPrune impact attribution.
        CollectCoverage: bool
        /// Include this project's contribution in the consumer coverage ratchet.
        Coverage: bool
        CoverageArgsTemplate: string option
        TimeoutSec: int option
        /// How to obtain the structured test report the verdict is derived from
        /// (`reportVerificationFormat` in `.fshw.json`). Default `AutoDetect`.
        ReportVerificationFormat: ReportVerificationFormat
        /// Whether this project takes part in `tests.traces` recording. Default true;
        /// `"traces": false` opts it out.
        Traces: bool
    }

/// Configuration for Falco route attribution.
type FalcoExtensionConfig = { Project: string; TestDir: string }

/// Configuration for SqlHydra table attribution.
type SqlHydraExtensionConfig = { GeneratedModulePrefix: string }

/// A configured TestPrune extension. Each case carries only the fields its
/// factory consumes, so malformed combinations cannot survive parsing.
type TestExtensionConfig =
    | FalcoExtension of FalcoExtensionConfig
    | SqlExtension
    | SqlHydraExtension of SqlHydraExtensionConfig

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

/// One `preprocessors` entry as written: paths still repo-relative, the timeout still
/// unresolved against the global default. `registerPlugins` resolves both.
[<NoComparison>]
type PreprocessorConfig =
    {
        Name: string
        Command: string
        Args: string
        /// `cwd`: repo-relative or absolute. Absent → the repository root.
        WorkDir: string option
        /// `triggers`: absent → before every run.
        Trigger: FsHotWatch.CommandPreprocessor.Trigger
        /// `writes`: repo-relative paths the command may rewrite.
        Writes: string list
        TimeoutSec: int option
    }

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
               FailOnSeverity: DiagnosticSeverity
               BootstrapHints: Map<string, string> |} option
        Tests:
            {| BeforeRun: string list option
               Extensions: TestExtensionConfig list
               Projects: TestProjectConfig list
               Excluded: SolutionScope.Exclusion list
               Solution: string option
               CoverageDir: string
               DependsOn: string list
               Traces: FsHotWatch.TestPrune.TraceSettings option |} option
        FileCommands:
            {| PluginName: string
               Pattern: string option
               AfterTests: FsHotWatch.FileCommand.FileCommandPlugin.TestFilter option
               Command: string
               Args: string
               TimeoutSec: int option |} list
        /// The `preprocessors` array: commands that rewrite files in place before the
        /// build and the checks see them, run in this order, ahead of the built-in
        /// formatter. See `FsHotWatch.CommandPreprocessor`.
        Preprocessors: PreprocessorConfig list
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
        /// TransparentCompiler cache size factor, from `checker.cacheSizeFactor`.
        /// A positive integer; anything else is a `ConfigError`. Absent →
        /// `Daemon.DefaultCheckerCacheSizeFactor` (FCS's own default, 100).
        CheckerCacheSizeFactor: int
        /// Run-level `beforeRun` hook, from the top-level
        /// `beforeRun` key. A shell command run ONCE at the very start of a
        /// `check`/`confirm` run — BEFORE the daemon is contacted — as a
        /// FAIL-CLOSED preflight: a non-zero exit aborts the run with exit 2 and
        /// runs no plugin work. DISTINCT from `tests.beforeRun`, which the daemon
        /// runs per test run inside the tests slot; this one brackets the WHOLE
        /// run. The first consumer, a large private downstream repository, uses it to
        /// acquire a box-wide gate-lock. Absent / `false` → None.
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
      Preprocessors = []
      Coverage = None
      Exclude = []
      IncludeOutsideRepo = false
      LogDir = DefaultLogDir
      TimeoutSec = Some DefaultGlobalTimeoutSec
      IdleExitMin = IdleExit.IdleExitConfig.Absent
      PressureIdleFloorMin = IdleExit.PressureFloorConfig.Absent
      FsEventsLatencyMs = 250
      CheckerCacheSizeFactor = FsHotWatch.Daemon.Daemon.DefaultCheckerCacheSizeFactor
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
        let refuse (detail: string) =
            raise (ConfigError $"cache: %s{detail}")

        let globs (settings: JsonElement) (name: string) =
            match settings.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Array ->
                v.EnumerateArray()
                |> Seq.map (fun e ->
                    if e.ValueKind = JsonValueKind.String then
                        e.GetString()
                    else
                        refuse $"%s{name} must be a list of glob strings")
                |> Seq.toList
            | true, _ -> refuse $"%s{name} must be a list of glob strings, e.g. [\"src/Libs/\"]"
            | false, _ -> []

        match root.TryGetProperty("cache") with
        | true, v when v.ValueKind = JsonValueKind.False -> NoCache
        | true, v when v.ValueKind = JsonValueKind.True -> InMemory defaultInMemoryCache
        | true, v when v.ValueKind = JsonValueKind.Object ->
            let maxEntries =
                match v.TryGetProperty "maxEntries" with
                | false, _ -> CacheSize.All
                | true, m when m.ValueKind = JsonValueKind.String && m.GetString() = "all" -> CacheSize.All
                | true, m when m.ValueKind = JsonValueKind.Number ->
                    match m.TryGetInt32() with
                    | true, n when n > 0 -> CacheSize.Entries n
                    | _ -> refuse "maxEntries must be a positive whole number or \"all\""
                | true, _ -> refuse "maxEntries must be a positive whole number or \"all\""

            let scope =
                match v.TryGetProperty "scope" with
                | false, _ -> CacheScope.AllCheckouts
                | true, s when s.ValueKind = JsonValueKind.String ->
                    match s.GetString() with
                    | "all" -> CacheScope.AllCheckouts
                    | "default-workspace" -> CacheScope.DefaultWorkspaceOnly
                    | other -> refuse $"scope '%s{other}' is not \"all\" or \"default-workspace\""
                | true, _ -> refuse "scope must be \"all\" or \"default-workspace\""

            InMemory
                { MaxEntries = maxEntries
                  Scope = scope
                  Include = globs v "include"
                  Exclude = globs v "exclude" }
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString().ToLowerInvariant() with
            | "memory" -> InMemory defaultInMemoryCache
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

            let bootstrapHints =
                match v.TryGetProperty("bootstrapHints") with
                | true, hints when hints.ValueKind = JsonValueKind.Object ->
                    hints.EnumerateObject()
                    |> Seq.map (fun hint ->
                        if not (List.contains hint.Name paths) then
                            raise (
                                ConfigError(
                                    $"analyzers.bootstrapHints names '%s{hint.Name}', which is not an analyzers.paths entry \
                                      — a hint for an unconfigured path could never be shown"
                                )
                            )

                        match hint.Value.ValueKind with
                        | JsonValueKind.String when not (String.IsNullOrWhiteSpace(hint.Value.GetString())) ->
                            hint.Name, hint.Value.GetString()
                        | _ ->
                            raise (
                                ConfigError(
                                    $"analyzers.bootstrapHints['%s{hint.Name}'] must be a non-blank string command"
                                )
                            ))
                    |> Map.ofSeq
                | true, hints when hints.ValueKind = JsonValueKind.Null -> Map.empty
                | true, _ -> raise (ConfigError "analyzers.bootstrapHints must be an object of path → command")
                | _ -> Map.empty

            if paths.IsEmpty then
                None
            else
                Some
                    {| Paths = paths
                       FailOnSeverity = failOnSeverity
                       BootstrapHints = bootstrapHints |}
        | _ -> None

    let tests =
        match root.TryGetProperty("tests") with
        | true, v ->
            // a STRING (one step, back-compatible) or an ARRAY
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
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    arr.EnumerateArray()
                    |> Seq.map (fun e ->
                        let requiredNonBlank (propertyName: string) =
                            match e.TryGetProperty(propertyName) with
                            | true, value when value.ValueKind = JsonValueKind.String ->
                                let parsed = value.GetString()

                                if String.IsNullOrWhiteSpace(parsed) then
                                    raise (ConfigError($"tests.extensions.%s{propertyName} must be non-blank"))

                                parsed
                            | _ -> raise (ConfigError($"tests.extensions.%s{propertyName} is required"))

                        match (requiredNonBlank "type").ToLowerInvariant() with
                        | "falco" ->
                            FalcoExtension
                                { Project = requiredNonBlank "project"
                                  TestDir = requiredNonBlank "testDir" }
                        | "sql" -> SqlExtension
                        | "sql-hydra"
                        | "sqlhydra" ->
                            SqlHydraExtension { GeneratedModulePrefix = requiredNonBlank "generatedModulePrefix" }
                        | other -> raise (ConfigError($"tests.extensions has unknown type '%s{other}'")))
                    |> Seq.toList
                | true, _ -> raise (ConfigError("tests.extensions must be an array"))
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

                        // `coverage` may be bool (collect + enforce together) or an
                        // object. `collectForImpact` decouples TestPrune collection
                        // from consumer-ratchet participation (`enabled`).
                        let collectCoverage, coverage, coverageArgsTemplate =
                            match p.TryGetProperty("coverage") with
                            | true, v when v.ValueKind = JsonValueKind.False -> false, false, None
                            | true, v when v.ValueKind = JsonValueKind.True -> true, true, None
                            | true, v when v.ValueKind = JsonValueKind.Object ->
                                let enabled =
                                    match v.TryGetProperty("enabled") with
                                    | true, e when e.ValueKind = JsonValueKind.False -> false
                                    | _ -> true

                                let collectForImpact =
                                    match v.TryGetProperty("collectForImpact") with
                                    | true, e when e.ValueKind = JsonValueKind.True -> true
                                    | true, e when e.ValueKind = JsonValueKind.False -> false
                                    | _ -> enabled

                                let tmpl =
                                    match v.TryGetProperty("argsTemplate") with
                                    | true, t when t.ValueKind = JsonValueKind.String -> Some(t.GetString())
                                    | _ -> None

                                collectForImpact, enabled, tmpl
                            | _ -> true, true, None

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

                        // `traces`: every project takes part in `tests.traces`
                        // recording unless it says `"traces": false`.
                        let traces =
                            match p.TryGetProperty("traces") with
                            | true, t when t.ValueKind = JsonValueKind.False -> false
                            | _ -> true

                        { Project = project
                          Command = command
                          Args = args
                          Group = group
                          Environment = env
                          FilterTemplate = filterTemplate
                          ClassJoin = classJoin
                          CollectCoverage = collectCoverage
                          Coverage = coverage
                          CoverageArgsTemplate = coverageArgsTemplate
                          TimeoutSec = timeoutSec
                          ReportVerificationFormat = reportVerificationFormat
                          Traces = traces })
                    |> Seq.toList
                | _ -> []

            // `tests.excluded` — first-class, and the only
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

            // `tests.traces`: opt-in per-test trace recording. Absent → None (off,
            // byte-for-byte today's behaviour). An unknown `record` value is a
            // ConfigError: a typo would otherwise switch tracing off unnoticed.
            let traces =
                match v.TryGetProperty("traces") with
                | true, t when t.ValueKind = JsonValueKind.Object ->
                    let str (name: string) =
                        match t.TryGetProperty(name) with
                        | true, s when s.ValueKind = JsonValueKind.String -> Some(s.GetString())
                        | _ -> None

                    let strs (name: string) =
                        match t.TryGetProperty(name) with
                        | true, arr when arr.ValueKind = JsonValueKind.Array ->
                            arr.EnumerateArray()
                            |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.String)
                            |> Seq.map (fun e -> e.GetString())
                            |> Seq.toList
                        | _ -> []

                    let record =
                        match str "record" with
                        | None -> FsHotWatch.TestPrune.RecordOff
                        | Some raw ->
                            match FsHotWatch.TestPrune.TraceSettings.parseRecord raw with
                            | Some r -> r
                            | None ->
                                raise (
                                    ConfigError
                                        $"tests.traces.record has unknown value '%s{raw}' (expected off, full-runs or every-run)"
                                )

                    let weaveTests =
                        match str "weaveTests" |> Option.map (fun w -> w.ToLowerInvariant()) with
                        | Some "full" -> FsHotWatch.TestPrune.WeaveTestFull
                        | _ -> FsHotWatch.TestPrune.WeaveTestSites

                    let verifyTimeoutSec =
                        match t.TryGetProperty("verifyTimeoutSec") with
                        | true, n when n.ValueKind = JsonValueKind.Number -> n.GetInt32()
                        | _ -> FsHotWatch.TestPrune.TraceSettings.defaultVerifyTimeoutSec

                    let settings: FsHotWatch.TestPrune.TraceSettings =
                        { Record = record
                          DbPath = str "db" |> Option.defaultValue FsHotWatch.TestPrune.TraceSettings.defaultDbPath
                          WeaveTests = weaveTests
                          FingerprintInputs = strs "fingerprintInputs"
                          FingerprintEnv = strs "fingerprintEnv"
                          VerifyTimeoutSec = verifyTimeoutSec }

                    Some settings
                | _ -> None

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
                       DependsOn = dependsOn
                       Traces = traces |}
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

    // `preprocessors`: every shape mistake is a `ConfigError`, because an entry that
    // silently did not register is a generator that silently did not run.
    let preprocessors =
        let entry (index: int) (e: JsonElement) : PreprocessorConfig =
            let at = $"preprocessors[%d{index}]"

            let requiredString (key: string) =
                match e.TryGetProperty(key) with
                | true, v when v.ValueKind = JsonValueKind.String && v.GetString().Trim() <> "" -> v.GetString()
                | _ -> raise (ConfigError $"%s{at} must have a non-empty string `%s{key}`")

            let optionalString (key: string) =
                match e.TryGetProperty(key) with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | true, _ -> raise (ConfigError $"%s{at}.%s{key} must be a string")
                | _ -> None

            let stringList (key: string) =
                match e.TryGetProperty(key) with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    v.EnumerateArray()
                    |> Seq.map (fun item ->
                        if item.ValueKind = JsonValueKind.String then
                            item.GetString()
                        else
                            raise (ConfigError $"%s{at}.%s{key} must be an array of strings"))
                    |> Seq.toList
                    |> Some
                | true, _ -> raise (ConfigError $"%s{at}.%s{key} must be an array of strings")
                | _ -> None

            let trigger =
                match stringList "triggers" with
                | None
                | Some [] -> FsHotWatch.CommandPreprocessor.Trigger.Always
                | Some patterns ->
                    patterns
                    |> List.map (fun pattern ->
                        try
                            FsHotWatch.Watcher.FilePattern.parse pattern
                        with :? System.ArgumentException as ex ->
                            raise (ConfigError $"%s{at}.triggers pattern invalid: %s{ex.Message}"))
                    |> FsHotWatch.CommandPreprocessor.Trigger.Matching

            let timeoutSec =
                match e.TryGetProperty("timeoutSec") with
                | true, t when t.ValueKind = JsonValueKind.Number && t.GetInt32() > 0 -> Some(t.GetInt32())
                | true, _ -> raise (ConfigError $"%s{at}.timeoutSec must be a positive whole number")
                | _ -> None

            { Name = requiredString "name"
              Command = requiredString "command"
              Args = optionalString "args" |> Option.defaultValue ""
              WorkDir = optionalString "cwd"
              Trigger = trigger
              Writes = stringList "writes" |> Option.defaultValue []
              TimeoutSec = timeoutSec }

        match root.TryGetProperty("preprocessors") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            let entries = arr.EnumerateArray() |> Seq.mapi entry |> Seq.toList

            entries
            |> List.countBy (fun p -> p.Name)
            |> List.iter (fun (name, count) ->
                if count > 1 then
                    raise (
                        ConfigError $"preprocessors: the name '%s{name}' is used %d{count} times; names must be unique"
                    ))

            entries
        | true, _ -> raise (ConfigError "preprocessors must be an array of entries")
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

    // `checker.cacheSizeFactor`: a positive integer, else a hard ConfigError — a
    // mistyped factor silently falling back would make a benchmark row measure the
    // default while claiming otherwise.
    let checkerCacheSizeFactor =
        let refuse () =
            raise (ConfigError "checker.cacheSizeFactor must be a positive whole number, e.g. 10")

        match root.TryGetProperty("checker") with
        | false, _ -> defaults.CheckerCacheSizeFactor
        | true, v when v.ValueKind = JsonValueKind.Object ->
            match v.TryGetProperty("cacheSizeFactor") with
            | false, _ -> defaults.CheckerCacheSizeFactor
            | true, f when f.ValueKind = JsonValueKind.Number ->
                match f.TryGetInt32() with
                | true, n when n > 0 -> n
                | _ -> refuse ()
            | true, _ -> refuse ()
        | true, _ -> raise (ConfigError "checker must be an object, e.g. {\"cacheSizeFactor\": 10}")

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
      Preprocessors = preprocessors
      Coverage = coverage
      Exclude = exclude
      IncludeOutsideRepo = includeOutsideRepo
      LogDir = logDir
      TimeoutSec = timeoutSec
      IdleExitMin = idleExitMin
      PressureIdleFloorMin = pressureIdleFloorMin
      FsEventsLatencyMs = fsEventsLatencyMs
      CheckerCacheSizeFactor = checkerCacheSizeFactor
      BeforeRun = beforeRun
      AfterRun = afterRun
      RunHookTimeoutSec = runHookTimeoutSec
      RunHookCommands = runHookCommands }

/// WHERE THE DAEMON LOG IS — one answer, taken from the configuration.
///
/// The daemon's log is `daemon.log` inside `logDir` (see `DefaultLogDir`), and every
/// sentence that sends a reader there has to agree with that. A sentence that hard-codes
/// a directory is a guess about someone else's configuration, and the guess loses
/// silently: the red-cause pointer shipped naming `.fshw/logs/daemon.log` — a directory
/// fshw writes no daemon log into under ANY configuration — so a reader with nothing else
/// to go on was sent to a file that was never there.
module DaemonLog =
    [<Literal>]
    let FileName = "daemon.log"

    /// The log file under `logDir`, as a message SHOWS it: forward slashes, relative
    /// left relative. A pointer for a human to follow, not a path to open — `ensureDaemon`
    /// resolves the real one against the repo root.
    let under (logDir: string) : string =
        let dir = (if isNull logDir then "" else logDir).Replace('\\', '/').TrimEnd('/')

        if dir = "" then FileName else $"%s{dir}/%s{FileName}"

    /// The log file of one repo, read from that repo's own `.fshw.json` — through
    /// `parseConfig`, the same parser the daemon loads with, so there is no second reading
    /// of the key to drift.
    ///
    /// Deliberately NOT `loadConfig`: naming a log file must not validate a configuration,
    /// log a line, or throw. A repo whose config cannot be read gets the default, because
    /// this is a courtesy inside an error message and may never become the reason a
    /// verdict cannot be read.
    let forRepo (repoRoot: string) : string =
        try
            let configPath = Path.Combine(repoRoot, ".fshw.json")

            if File.Exists configPath then
                under (parseConfig (File.ReadAllText configPath) (defaultConfigFor repoRoot)).LogDir
            else
                under DefaultLogDir
        with _ ->
            under DefaultLogDir

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

/// Reconcile the configured test scope with the solution and
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

/// `verdictInputs` decides WHICH FILES the verdict is
/// content-addressed by, so a declaration this build cannot honour AS WRITTEN is a
/// hard failure. The alternative is the defect this validation prevents: a repo that
/// believes it is gated on its coverage floors and its analyzer rules, is not, and is
/// told nothing.
///
/// `notInputs` is validated but never filters: it is a STATED DECISION that a file
/// cannot change an answer, reviewable in the config. A declaration that could REMOVE
/// files from the hash would be a supported way to weaken the gate silently, which is
/// the wrong half of the feature to build.
let private validateVerdictInputs (repoRoot: string) (json: string) : unit =
    let declaration = VerdictInputs.parse json

    match declaration.Errors with
    | [] -> ()
    | errors -> raise (ConfigError("verdictInputs — " + String.concat "; " errors))

    // A declared path that matches nothing is NOT fatal: an analyzer assembly a repo
    // declares does not exist until the first build, and refusing to start would wedge
    // exactly the command that creates it. It is still said out loud, and it still moves
    // the tree hash (`VerdictInputs.AbsentDeclaration`), so no verdict can quietly apply
    // as though the file had been hashed.
    let resolved = VerdictInputs.resolve repoRoot declaration

    for path in resolved.Absent do
        Logging.warn
            "config"
            $"verdictInputs.hashed declares '%s{path}' but nothing on disk matches it — it is hashed as ABSENT (the tree hash moves when it appears), and until then this repo is NOT gating on that file"

    if not (List.isEmpty declaration.Hashed) then
        Logging.info
            "config"
            $"verdictInputs: %d{List.length declaration.Hashed} declared, %d{List.length resolved.Files} file(s) folded into the tree hash, %d{List.length resolved.Absent} absent"

/// Load config from .fshw.json in repoRoot, together with the exact text that was
/// parsed (`""` when there is no file). The text is what a daemon's loaded-config
/// identity is computed from: re-reading the file later could describe an edit the
/// daemon never loaded. Returns defaults if no file exists.
/// Raises ConfigError on read / parse / validation failure.
let loadConfigWithSource (repoRoot: string) : DaemonConfiguration * string =
    let configPath = Path.Combine(repoRoot, ".fshw.json")

    let defaults = defaultConfigFor repoRoot

    if not (File.Exists configPath) then
        Logging.info "config" "No .fshw.json found, using defaults (build + format + lint)"
        defaults, ""
    else
        let json =
            try
                File.ReadAllText(configPath)
            with ex ->
                raise (ConfigError $"Cannot read .fshw.json: %s{ex.Message}")

        try
            let config = parseConfig json defaults
            validateVerdictInputs repoRoot json
            validateTestScope repoRoot config
            Logging.info "config" "Loaded .fshw.json"
            config, json
        with
        | ConfigError _ -> reraise ()
        | ex -> raise (ConfigError $".fshw.json: %s{ex.Message}")

/// Load config from .fshw.json in repoRoot. Returns defaults if no file exists.
/// Raises ConfigError on read / parse / validation failure.
let loadConfig (repoRoot: string) : DaemonConfiguration = loadConfigWithSource repoRoot |> fst

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
/// why a hook step failed, in a form that CANNOT be empty.
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

/// One shell-hook step that ACTUALLY RAN, with when and how long.
/// Steps after a failed one are deliberately absent: fail-fast attribution must
/// never imply work happened.
type internal HookStepTiming =
    {
        Label: string
        StepIndex: int
        StepCount: int
        Command: string
        StartedAtUtc: DateTime
        ElapsedMs: int64
        /// `"ok"` or `"fail"`.
        Outcome: string
    }

/// `tests.beforeRun` runs INSIDE the daemon, per test run; the verdict
/// is published by the CLI on the other side of the IPC boundary. The run directory
/// (`.fshw/test-runs/<runId>/`) is already the shared ground between them — the CTRF
/// reports cross the same way — so each run's hook timings are filed there, keyed by
/// the run id the verdict carries. A missing or malformed file reads as "no hook
/// evidence", never as a crash: attribution is an additional surface, not a new way
/// for a check to fail.
module internal HookTimings =
    [<Literal>]
    let private FileName = "hook-timings.json"

    let private path (repoRoot: string) (runId: Guid) =
        Path.Combine(Ctrf.runDir repoRoot runId, FileName)

    let record (repoRoot: string) (runId: Guid) (timings: HookStepTiming list) : unit =
        let payload =
            [ for t in timings ->
                  {| label = t.Label
                     stepIndex = t.StepIndex
                     stepCount = t.StepCount
                     command = t.Command
                     startedAtUtc = t.StartedAtUtc.ToString("O")
                     elapsedMs = t.ElapsedMs
                     outcome = t.Outcome |} ]

        try
            FsHwPaths.atomicWriteAllText (path repoRoot runId) (JsonSerializer.Serialize payload + "\n")
        with
        | :? IOException
        | :? UnauthorizedAccessException as ex ->
            let run = runId.ToString "N"
            Logging.warn "beforeRun" $"could not record hook timings for run %s{run}: %s{ex.Message}"

    let read (repoRoot: string) (runId: Guid option) : HookStepTiming list =
        match runId with
        | Some id when File.Exists(path repoRoot id) ->
            try
                use document = JsonDocument.Parse(File.ReadAllText(path repoRoot id))

                document.RootElement.EnumerateArray()
                |> Seq.map (fun item ->
                    { Label = item.GetProperty("label").GetString()
                      StepIndex = item.GetProperty("stepIndex").GetInt32()
                      StepCount = item.GetProperty("stepCount").GetInt32()
                      Command = item.GetProperty("command").GetString()
                      StartedAtUtc =
                        DateTime.Parse(
                            item.GetProperty("startedAtUtc").GetString(),
                            Globalization.CultureInfo.InvariantCulture,
                            Globalization.DateTimeStyles.AdjustToUniversal
                        )
                      ElapsedMs = item.GetProperty("elapsedMs").GetInt64()
                      Outcome = item.GetProperty("outcome").GetString() })
                |> Seq.toList
            with
            | :? JsonException
            | :? FormatException
            | :? InvalidOperationException
            | :? Collections.Generic.KeyNotFoundException
            | :? IOException
            | :? UnauthorizedAccessException -> []
        | _ -> []

/// The result of running a hook chain. A DU rather than `Result<unit, _>`
/// because `Error` is already a `DiagnosticSeverity` case in this file, and the
/// collision made every neighbouring match ambiguous.
///
/// Both cases carry the timings of every step that RAN, in chain
/// order — on failure, up to and including the step that failed.
type internal HookOutcome =
    | HookOk of HookStepTiming list
    | HookFailed of HookStepTiming list * HookFailure

/// Run an ordered chain of shell steps, stopping at the first failure and
/// reporting WHICH one broke. One step is the degenerate case of the same path,
/// so the string and array config forms share every line of this.
///
/// Each step is handed to `track` once its child is running and released once the
/// child has exited, however it exits, so a wait that falls inside the chain names
/// the step it is on.
let internal runShellSteps
    (label: string)
    (timeoutSec: int option)
    (repoRoot: string)
    (track: HookStep.Tracker)
    (steps: string list)
    : HookOutcome =
    let bound = timeoutSec |> Option.map (fun s -> TimeSpan.FromSeconds(float s))

    let bounds =
        ProcessBounds.silent (bound |> Option.defaultValue Threading.Timeout.InfiniteTimeSpan)

    let count = List.length steps

    // Timings accumulate newest-first while folding and are reversed once at the end.
    steps
    |> List.indexed
    |> List.fold
        (fun acc (i, cmd) ->
            match acc with
            | HookFailed _ -> acc // first failure wins; do not run the rest
            | HookOk timings ->
                Logging.info label $"Running %s{label} step %d{i + 1}/%d{count}: %s{cmd}"
                let (command, args) = shellInvocation cmd
                let startedAt = DateTime.UtcNow
                let stopwatch = Diagnostics.Stopwatch.StartNew()
                let held: IDisposable option ref = ref None

                let onStarted pid =
                    let step: HookStep.Running =
                        { Label = label
                          StepIndex = i + 1
                          StepCount = count
                          Command = cmd
                          Pid = pid
                          Bound = bound }

                    Logging.info label $"Started %s{HookStep.describe step}"
                    held.Value <- Some(track step)

                let outcome =
                    try
                        runProcessObserved onStarted command args repoRoot [] bounds
                    finally
                        held.Value |> Option.iter _.Dispose()

                stopwatch.Stop()

                let timing =
                    { Label = label
                      StepIndex = i + 1
                      StepCount = count
                      Command = cmd
                      StartedAtUtc = startedAt
                      ElapsedMs = stopwatch.ElapsedMilliseconds
                      Outcome = if isSucceeded outcome then "ok" else "fail" }

                Logging.info
                    label
                    $"Completed %s{label} step %d{i + 1}/%d{count} in %d{timing.ElapsedMs}ms (%s{timing.Outcome}): %s{cmd}"

                if isSucceeded outcome then
                    HookOk(timing :: timings)
                else
                    HookFailed(
                        List.rev (timing :: timings),
                        { Label = label
                          StepIndex = i + 1
                          StepCount = count
                          Command = cmd
                          Outcome = outcome }
                    ))
        (HookOk [])
    |> function
        | HookOk timings -> HookOk(List.rev timings)
        | failed -> failed

let internal makeShellHookWithResult
    (label: string)
    (timeoutSec: int option)
    (repoRoot: string)
    (cmd: string)
    : unit -> bool * string =
    let bound = timeoutSec |> Option.map (fun s -> TimeSpan.FromSeconds(float s))

    let bounds =
        ProcessBounds.silent (bound |> Option.defaultValue Threading.Timeout.InfiniteTimeSpan)

    fun () ->
        Logging.info label $"Running %s{label}: %s{cmd}"
        let (command, args) = shellInvocation cmd

        // The pid and bound, logged the moment the child runs: this hook runs outside
        // any plugin, so its start line is what a hang inside it is traced from.
        let onStarted pid =
            let step: HookStep.Running =
                { Label = label
                  StepIndex = 1
                  StepCount = 1
                  Command = cmd
                  Pid = pid
                  Bound = bound }

            Logging.info label $"Started %s{HookStep.describe step}"

        let result = runProcessObserved onStarted command args repoRoot [] bounds
        let success = isSucceeded result
        let output = outputOf result

        if not success then
            // the RUN-level hook had the same empty-reason bug as
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

/// Construct configured TestPrune extensions against the plugin-owned database.
/// Construction fails closed: attribution participates in the gate's recall
/// claim, so an invalid extension must not silently become an empty graph.
let internal buildTestExtensions
    (db: TestPrune.Database.Database)
    (configs: TestExtensionConfig list)
    : TestPrune.Extensions.ITestPruneExtension list =
    configs
    |> List.map (function
        | FalcoExtension config ->
            let routeStore = TestPrune.Falco.RouteStore(TestPrune.Ports.toPluginStore db)

            TestPrune.Falco.FalcoRouteExtension(config.Project, config.TestDir, routeStore)
            :> TestPrune.Extensions.ITestPruneExtension
        | SqlExtension -> TestPrune.Sql.AutoSqlExtension() :> TestPrune.Extensions.ITestPruneExtension
        | SqlHydraExtension config ->
            TestPrune.SqlHydra.SqlHydraExtension(config.GeneratedModulePrefix)
            :> TestPrune.Extensions.ITestPruneExtension)

/// Register plugins on the daemon based on the loaded configuration.
/// Where TestPrune keeps a worktree's test-impact index.
let testImpactDbPath (repoRoot: string) =
    Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "test-impact.db")

let registerPlugins (daemon: Daemon) (repoRoot: string) (config: DaemonConfiguration) =
    // Configured preprocessors, in config order, BEFORE the built-in formatter: what a
    // generator writes is then formatted by the pinned formatter in the same pass.
    let absoluteUnder (p: string) =
        if Path.IsPathRooted(p) then
            p
        else
            Path.GetFullPath(Path.Combine(repoRoot, p))

    for p in config.Preprocessors do
        let timeoutSec =
            p.TimeoutSec
            |> Option.orElse config.TimeoutSec
            |> Option.defaultValue DefaultGlobalTimeoutSec

        let spec: FsHotWatch.CommandPreprocessor.Spec =
            { Name = p.Name
              Command = p.Command
              Args = p.Args
              WorkDir = p.WorkDir |> Option.map absoluteUnder |> Option.defaultValue repoRoot
              Trigger = p.Trigger
              Writes = p.Writes |> List.map absoluteUnder
              Timeout = TimeSpan.FromSeconds(float timeoutSec) }

        Logging.info "config" $"Registering preprocessor %s{p.Name}: %s{p.Command} %s{p.Args}"
        daemon.RegisterPreprocessor(FsHotWatch.CommandPreprocessor.create spec)

    // Format plugin. Both shapes run the repository's PINNED `dotnet fantomas`;
    // say which one at registration so the daemon log carries the
    // version before any file is touched, and say loudly when there is none — the
    // plugin will refuse every run until the pin exists, and that must not read as
    // a formatter that found nothing to do.
    let logFormatPin () =
        match FsHotWatch.Fantomas.FantomasTool.readPin repoRoot with
        | Result.Ok pin -> Logging.info "config" $"format: %s{FsHotWatch.Fantomas.FantomasTool.describe repoRoot pin}"
        | Result.Error e ->
            Logging.error
                "config"
                $"format: %s{FsHotWatch.Fantomas.FantomasTool.PinError.render e} — the format plugin refuses every run until then"

    match config.Format with
    | Auto ->
        Logging.info "config" "Registering FormatPreprocessor"
        logFormatPin ()

        // A preprocessor runs inside a change batch AND inside the scan, so an
        // unbounded one is the worse of the two to leave uncapped.
        daemon.RegisterPreprocessor(
            match config.TimeoutSec with
            | Some s -> FsHotWatch.Fantomas.FormatCheckPlugin.FormatPreprocessor(timeoutSec = s)
            | None -> FsHotWatch.Fantomas.FormatCheckPlugin.FormatPreprocessor()
        )
    | Check ->
        Logging.info "config" "Registering FormatCheckPlugin (read-only)"
        logFormatPin ()
        daemon.RegisterHandler(FsHotWatch.Fantomas.FormatCheckPlugin.createFormatCheck repoRoot config.TimeoutSec)
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
        let toAbsolute (p: string) =
            if Path.IsPathRooted(p) then
                p
            else
                Path.GetFullPath(Path.Combine(repoRoot, p))

        let absolutePaths = a.Paths |> List.map toAbsolute

        let hintByAbsolutePath =
            a.BootstrapHints
            |> Map.toList
            |> List.map (fun (p, hint) -> toAbsolute p, hint)
            |> Map.ofList

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
        match analyzerPathFailuresWith (fun p -> Map.tryFind p hintByAbsolutePath) loadedByOriginalPath with
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
                // Case 1 promotes the corrected issue
                // detector: a cached or freshly returned success must describe the
                // current graph outputs, not a deleted, older, or divergent artifact.
                FsHotWatch.Build.BuildPlugin.create
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
        let dbPath = testImpactDbPath repoRoot
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

        // fail LOUD and SPECIFIC. `runShellSteps` names the step,
        // its exit code and its output; raising `describe` means the plugin's
        // `runTests failed: …` wrapper now carries that instead of a bare colon.
        // The plugin hands over the run id so the steps' timings are
        // filed under that run, where the CLI's verdict publisher reads them back —
        // including the failing step of a fail-fast chain, which ran and cost time.
        let beforeRun =
            t.BeforeRun
            |> Option.map (fun steps ->
                fun (runId: Guid) (track: HookStep.Tracker) ->
                    match runShellSteps "beforeRun" config.TimeoutSec repoRoot track steps with
                    | HookOk timings -> HookTimings.record repoRoot runId timings
                    | HookFailed(timings, failure) ->
                        HookTimings.record repoRoot runId timings
                        let message = HookFailure.describe failure
                        Logging.error "beforeRun" message
                        failwith message)

        // Coverage paths — resolve per-project artifact locations (respecting per-project opt-out).
        // TestPrune itself decides whether a given run writes baseline.json or partial.json
        // and performs the merge step; this function only exposes the three paths per project.
        let projectsByName = t.Projects |> List.map (fun p -> p.Project, p) |> Map.ofList

        let coverageCollectionExcludedProjects =
            t.Projects
            |> List.filter (fun p -> not p.CollectCoverage)
            |> List.map (fun p -> p.Project)
            |> Set.ofList

        // Coverage artifacts live under <repoRoot>/<tests.coverageDir>/<project>/
        // so external coverage tools (e.g. coverageratchet invoked via a
        // fileCommands afterTests entry) can read the cobertura output. The
        // output directory is configurable via `tests.coverageDir` (default
        // `"coverage"`). Per-project collection opt-out is honored separately
        // from consumer-ratchet participation.
        // The ArgsTemplate is per-project (defaults to the MTP-cobertura template),
        // so alternate test runners can swap in their own coverage invocation
        // without patching the plugin.
        let coveragePaths =
            Some(fun (project: string) ->
                if coverageCollectionExcludedProjects.Contains(project) then
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
                          IncludeInRatchet = projectsByName.[project].Coverage
                          ArgsTemplate = argsTemplate })

        // Extension factories — invoked by the plugin with its own DB, so the
        // RouteStore/SymbolStore an extension captures is guaranteed to be the
        // same DB the plugin queries against.
        let buildExtensions =
            match t.Extensions with
            | [] -> None
            | exts -> Some(fun db -> buildTestExtensions db exts)

        Logging.info "config" $"Registering TestPrunePlugin with %d{testConfigs.Length} test projects"

        // `tests.excluded`, resolved to the project identities the symbol index keys
        // tests by, against the solution and the CURRENT discovered projects. A declared,
        // reasoned exclusion is what lets debt covered only by that project leave the
        // queue; any other unconfigured covering project keeps it owed.
        let excludedProjects =
            SolutionScope.createExclusionResolver repoRoot t.Solution t.Excluded (fun () ->
                daemon.Graph.GetAllProjects() |> List.map AbsProjectPath.value)

        let handler =
            createWithScope
                excludedProjects
                dbPath
                repoRoot
                (Some testConfigs)
                buildExtensions
                beforeRun
                None
                coveragePaths
                t.DependsOn

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
