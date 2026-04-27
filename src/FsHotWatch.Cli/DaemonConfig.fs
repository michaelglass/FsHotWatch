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

/// Cache backend configuration.
type CacheBackendConfig =
    /// Disable caching entirely
    | NoCache
    /// In-memory LRU cache only (lost on restart)
    | InMemoryOnly of maxSize: int
    /// File-based cache with timestamp key provider
    | FileBackend
    /// File-based cache with jj-aware key provider
    | JjFileBackend

/// Create cache backend and key provider from config.
let createCacheComponents
    (repoRoot: string)
    (config: CacheBackendConfig)
    : (ICheckCacheBackend option * ICacheKeyProvider option) =
    let cacheDir = Path.Combine(FsHotWatch.FsHwPaths.root repoRoot, "cache")

    match config with
    | NoCache -> (None, None)
    | InMemoryOnly maxSize ->
        (Some(FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(maxSize) :> ICheckCacheBackend),
         Some(TimestampCacheKeyProvider() :> ICacheKeyProvider))
    | FileBackend ->
        (Some(FsHotWatch.FileCheckCache.FileCheckCache(cacheDir) :> ICheckCacheBackend),
         Some(TimestampCacheKeyProvider() :> ICacheKeyProvider))
    | JjFileBackend ->
        (Some(FsHotWatch.FileCheckCache.FileCheckCache(cacheDir) :> ICheckCacheBackend),
         Some(JjCacheKeyProvider(repoRoot) :> ICacheKeyProvider))

/// Resolves which paths from `paths` exist, retrying with short backoff to
/// handle the case where the daemon starts immediately after `jj workspace
/// add`: workspace population is async and `dirExists` can transiently return
/// false for newly-materialised directories even though they're about to
/// appear. ~300 ms total wait in the worst case (3 retries × 100 ms) is
/// invisible in normal operation but prevents a silent "0 paths resolved →
/// plugin doesn't register" failure when starting in a fresh workspace.
///
/// Injectable `dirExists` and `sleep` for unit testing without timing or
/// disk dependencies.
let resolveExistingPathsWithRetry (dirExists: string -> bool) (sleep: int -> unit) (paths: string list) : string list =
    let resolveAttempt () = paths |> List.filter dirExists
    let mutable resolved = resolveAttempt ()
    let mutable attempts = 0

    while resolved.Length < paths.Length && attempts < 3 do
        attempts <- attempts + 1
        sleep 100
        resolved <- resolveAttempt ()

    resolved

/// Detect the default cache backend: jj if .jj/ exists, otherwise file.
let detectDefaultCacheBackend (repoRoot: string) : CacheBackendConfig =
    if Directory.Exists(Path.Combine(repoRoot, ".jj")) then
        JjFileBackend
    else
        FileBackend

/// Configuration for a single test project.
///
/// `CoverageArgsTemplate` is the command-line template the daemon appends
/// when collecting coverage. `{output}` is substituted with the per-run
/// baseline or partial file path. `None` means use the MTP-cobertura default
/// (`FsHotWatch.TestPrune.TestPrunePlugin.defaultCoverageArgsTemplate`).
/// For other test runners (coverlet.collector, AltCover, etc.), provide
/// your own template.
type TestProjectConfig =
    { Project: string
      Command: string
      Args: string
      Group: string
      Environment: (string * string) list
      FilterTemplate: string option
      ClassJoin: string
      Coverage: bool
      CoverageArgsTemplate: string option
      TimeoutSec: int option }

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

/// Parsed daemon configuration from .fs-hot-watch.json.
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
            {| BeforeRun: string option
               Extensions: TestExtensionConfig list
               Projects: TestProjectConfig list
               CoverageDir: string |} option
        FileCommands:
            {| PluginName: string
               Pattern: string option
               AfterTests: FsHotWatch.FileCommand.FileCommandPlugin.TestFilter option
               Command: string
               Args: string
               TimeoutSec: int option |} list
        Exclude: string list
        /// Directory (relative to repoRoot or absolute) for daemon.log. Defaults to "logs".
        LogDir: string
        /// Global default timeout (seconds). Used when no per-entry override set.
        TimeoutSec: int option
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
      Cache = detectDefaultCacheBackend repoRoot
      Analyzers = None
      Tests = None
      FileCommands = []
      Exclude = []
      LogDir = "logs"
      TimeoutSec = None }

/// Raised when `.fs-hot-watch.json` cannot be read, parsed, or validated.
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
            | "file" -> FileBackend
            | "jj" -> JjFileBackend
            | "none"
            | "false" -> NoCache
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
                    | "hint" -> DiagnosticSeverity.fromString str
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
            let beforeRun =
                match v.TryGetProperty("beforeRun") with
                | true, br -> Some(br.GetString())
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

                        { Project = project
                          Command = command
                          Args = args
                          Group = group
                          Environment = env
                          FilterTemplate = filterTemplate
                          ClassJoin = classJoin
                          Coverage = coverage
                          CoverageArgsTemplate = coverageArgsTemplate
                          TimeoutSec = timeoutSec })
                    |> Seq.toList
                | _ -> []

            let coverageDir =
                match v.TryGetProperty("coverageDir") with
                | true, cd when cd.ValueKind = JsonValueKind.String -> cd.GetString()
                | _ -> "coverage"

            if projects.IsEmpty then
                None
            else
                Some
                    {| BeforeRun = beforeRun
                       Extensions = extensions
                       Projects = projects
                       CoverageDir = coverageDir |}
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

    let exclude =
        match root.TryGetProperty("exclude") with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            arr.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
        | _ -> defaults.Exclude

    let logDir =
        match root.TryGetProperty("logDir") with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> defaults.LogDir

    let timeoutSec =
        match root.TryGetProperty("timeoutSec") with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
        | _ -> defaults.TimeoutSec

    { Build = build
      Format = format
      Lint = lint
      Cache = cache
      Analyzers = analyzers
      Tests = tests
      FileCommands = fileCommands
      Exclude = exclude
      LogDir = logDir
      TimeoutSec = timeoutSec }

/// Strip a config down to a minimal base for run-once subcommands.
/// Disables all plugins except format preprocessor. Caller overrides specific fields.
let stripConfig (config: DaemonConfiguration) : DaemonConfiguration =
    { config with
        Build = Some []
        Lint = false
        Analyzers = None
        Tests = None
        FileCommands = [] }

/// Load config from .fs-hot-watch.json in repoRoot. Returns defaults if no file exists.
/// Raises ConfigError on read / parse / validation failure.
let loadConfig (repoRoot: string) : DaemonConfiguration =
    let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")

    let defaults = defaultConfigFor repoRoot

    if not (File.Exists configPath) then
        Logging.info "config" "No .fs-hot-watch.json found, using defaults (build + format + lint)"
        defaults
    else
        let json =
            try
                File.ReadAllText(configPath)
            with ex ->
                raise (ConfigError $"Cannot read .fs-hot-watch.json: %s{ex.Message}")

        try
            let config = parseConfig json defaults
            Logging.info "config" "Loaded .fs-hot-watch.json"
            config
        with
        | ConfigError _ -> reraise ()
        | ex -> raise (ConfigError $".fs-hot-watch.json: %s{ex.Message}")

/// Count the plugins that would be registered for a given configuration.
/// Used by `fs-hot-watch config check` to report how many plugins are configured.
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

/// Watch `.fs-hot-watch.json` for any write/rename/create and invoke the callback
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

    let handler (_: FileSystemEventArgs) =
        let fire =
            lock gate (fun () ->
                let now = DateTime.UtcNow

                if now - lastFire.Value > TimeSpan.FromMilliseconds(200.0) then
                    lastFire.Value <- now
                    true
                else
                    false)

        if fire then
            let reason =
                try
                    let _ = parseConfig (File.ReadAllText configPath) defaults
                    "config changed, stopping (restart to apply)"
                with ex ->
                    $"config invalid, stopping: %s{ex.Message}"

            try
                onChange reason
            with _ ->
                ()

    watcher.Changed.Add(handler)
    watcher.Created.Add(handler)
    watcher.Renamed.Add(fun e -> handler (FileSystemEventArgs(WatcherChangeTypes.Renamed, dir, e.Name)))
    watcher.EnableRaisingEvents <- true
    watcher :> IDisposable

/// Watch `.fs-hot-watch.json` at `repoRoot` if it exists, otherwise return a
/// no-op disposable. Keeps the `start` call-site tidy and gives tests a
/// direct entry point.
let watchRepoConfigFile (repoRoot: string) (onChange: string -> unit) : IDisposable =
    let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")

    if File.Exists configPath then
        watchConfigFile configPath onChange
    else
        { new IDisposable with
            member _.Dispose() = () }


/// Wrap a shell command string into a callback that runs via splitCommand + runProcess.
/// Returns (success, output).
/// Split the (command, args) pair a shell hook should actually invoke.
/// Runs the user's command string through `/bin/sh -c` so shell features
/// (`&&`, pipes, globs, env-var interpolation) work as written in
/// `.fs-hot-watch.json`. Unix-only — Windows isn't a supported platform.
/// Exposed for unit-testability.
let shellInvocation (cmd: string) : string * string =
    // Escape embedded double quotes so the outer `"..."` around the -c
    // argument stays balanced for /bin/sh.
    let escaped = cmd.Replace("\"", "\\\"")
    "/bin/sh", "-c \"" + escaped + "\""

let private makeShellHookWithResult (label: string) (repoRoot: string) (cmd: string) : unit -> bool * string =
    fun () ->
        Logging.info label $"Running %s{label}: %s{cmd}"
        let (command, args) = shellInvocation cmd
        let result = runProcess command args repoRoot []
        let success = isSucceeded result
        let output = outputOf result

        if not success then
            Logging.error label $"%s{label} failed:\n%s{output}"

        success, output

/// Wrap a shell command string into a fire-and-forget callback.
/// If failOnError is true, raises on failure; otherwise only logs.
let private makeShellHook (label: string) (failOnError: bool) (repoRoot: string) (cmd: string) : unit -> unit =
    let hook = makeShellHookWithResult label repoRoot cmd

    fun () ->
        let (success, _) = hook ()

        if not success && failOnError then
            failwith $"%s{label} failed: %s{cmd}"

/// Register plugins on the daemon based on the loaded configuration.
let registerPlugins (daemon: Daemon) (repoRoot: string) (config: DaemonConfiguration) =
    let getCommitId =
        Some(fun () -> FsHotWatch.JjHelper.getWorkingCopyCommitId repoRoot)

    // Format plugin
    match config.Format with
    | Auto ->
        Logging.info "config" "Registering FormatPreprocessor"
        daemon.RegisterPreprocessor(FsHotWatch.Fantomas.FormatCheckPlugin.FormatPreprocessor())
    | Check ->
        Logging.info "config" "Registering FormatCheckPlugin (read-only)"
        daemon.RegisterHandler(FsHotWatch.Fantomas.FormatCheckPlugin.createFormatCheck getCommitId config.TimeoutSec)
    | Off -> ()

    // Lint plugin
    if config.Lint then
        let lintConfigPath =
            let p = Path.Combine(repoRoot, "fsharplint.json")
            if File.Exists(p) then Some p else None

        match lintConfigPath with
        | Some path -> Logging.info "config" $"Registering LintPlugin with config: %s{path}"
        | None -> Logging.info "config" "Registering LintPlugin (no fsharplint.json found)"

        daemon.RegisterHandler(FsHotWatch.Lint.LintPlugin.create lintConfigPath getCommitId None config.TimeoutSec)

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

        if not resolvedPaths.IsEmpty then
            Logging.info "config" $"Registering AnalyzersPlugin with %d{resolvedPaths.Length} paths"

            daemon.RegisterHandler(
                FsHotWatch.Analyzers.AnalyzersPlugin.create resolvedPaths getCommitId config.TimeoutSec a.FailOnSeverity
            )
    | None -> ()

    // Shared dirty tracker — populated by BuildPlugin (on FileChanged) and queried
    // by TestPrunePlugin to skip projects whose binaries are stale.
    let dirtyTracker =
        if config.Build.IsSome && config.Tests.IsSome then
            Some(FsHotWatch.ProjectDirtyTracker.ProjectDirtyTracker())
        else
            None

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
                FsHotWatch.Build.BuildPlugin.create
                    b.Command
                    b.Args
                    []
                    daemon.Graph
                    testProjectNames
                    b.BuildTemplate
                    b.DependsOn
                    getCommitId
                    buildTimeout
                    dirtyTracker
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
                  TimeoutSec = p.TimeoutSec })

        let beforeRun = t.BeforeRun |> Option.map (makeShellHook "beforeRun" true repoRoot)

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
                            Path.GetFullPath(Path.Combine(outputDir, FsHotWatch.TestPrune.CoverageMerge.BaselineName))
                          Partial =
                            Path.GetFullPath(Path.Combine(outputDir, FsHotWatch.TestPrune.CoverageMerge.PartialName))
                          Cobertura =
                            Path.GetFullPath(Path.Combine(outputDir, FsHotWatch.TestPrune.CoverageMerge.CoberturaName))
                          ArgsTemplate = argsTemplate })

        // Extension factories — invoked by the plugin with its own DB, so the
        // RouteStore/SymbolStore an extension captures is guaranteed to be the
        // same DB the plugin queries against.
        let buildExtensions =
            match t.Extensions with
            | [] -> None
            | exts ->
                Some(fun (db: TestPrune.Database.Database) ->
                    let routeStore = TestPrune.Ports.toRouteStore db

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

        // Belt-and-suspenders mtime check for stale test DLLs. Used by executeTests
        // when the dirty tracker has no record for a project (e.g. run-tests command
        // at cold start, or a project MSBuild's incremental cache omitted from the
        // last build output). Returns true if sources are newer than the DLL.
        let stalenessCheck =
            let allProjects = daemon.Graph.GetAllProjects()

            let projByName =
                allProjects
                |> List.map (fun p -> Path.GetFileNameWithoutExtension(FsHotWatch.Events.AbsProjectPath.value p), p)
                |> Map.ofList

            Some(fun (projectName: string) ->
                match Map.tryFind projectName projByName with
                | None -> false
                | Some proj ->
                    let projPath = FsHotWatch.Events.AbsProjectPath.value proj
                    let projDir = Path.GetDirectoryName(projPath)
                    let sources = daemon.Graph.GetSourceFiles(proj)

                    let maxSourceTime =
                        sources
                        |> List.choose (fun f ->
                            let path = FsHotWatch.Events.AbsFilePath.value f

                            if File.Exists path then
                                Some(File.GetLastWriteTimeUtc path)
                            else
                                None)
                        |> function
                            | [] -> None
                            | times -> Some(List.max times)

                    match maxSourceTime with
                    | None -> false
                    | Some srcTime ->
                        let binDir = Path.Combine(projDir, "bin")

                        if not (Directory.Exists binDir) then
                            false
                        else
                            let dllFiles =
                                try
                                    Directory.GetFiles(binDir, projectName + ".dll", SearchOption.AllDirectories)
                                with _ ->
                                    [||]

                            match dllFiles with
                            | [||] -> false
                            | files ->
                                let maxDllTime = files |> Array.map File.GetLastWriteTimeUtc |> Array.max
                                maxDllTime < srcTime)

        Logging.info "config" $"Registering TestPrunePlugin with %d{testConfigs.Length} test projects"

        let handler =
            create
                dbPath
                repoRoot
                (Some testConfigs)
                buildExtensions
                beforeRun
                None
                coveragePaths
                getCommitId
                dirtyTracker
                stalenessCheck

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
                getCommitId
                fcTimeout
        )

        // Expose the parsed pattern to the host so the rerun IPC endpoint
        // can synthesize a matching fake file path.
        match parsedPattern with
        | Some pattern -> daemon.Host.RegisterFileCommandPattern(fc.PluginName, pattern)
        | None -> ()
