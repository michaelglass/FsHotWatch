module FsHotWatch.Cli.DaemonConfig

open System
open System.IO
open System.Text.Json
open FsHotWatch
open FsHotWatch.CheckCache
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

/// Detect the default cache backend: jj if .jj/ exists, otherwise file.
let detectDefaultCacheBackend (repoRoot: string) : CacheBackendConfig =
    if Directory.Exists(Path.Combine(repoRoot, ".jj")) then
        JjFileBackend
    else
        FileBackend

/// Configuration for a single test project.
type TestProjectConfig =
    { Project: string
      Command: string
      Args: string
      Group: string
      Environment: (string * string) list
      FilterTemplate: string option
      ClassJoin: string
      Coverage: bool }

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
               DependsOn: string list |} list option
        Format: FormatMode
        Lint: bool
        Cache: CacheBackendConfig
        Analyzers: {| Paths: string list |} option
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
               Args: string |} list
        Exclude: string list
        /// Directory (relative to repoRoot or absolute) for daemon.log. Defaults to "logs".
        LogDir: string
    }

let private defaultConfigFor (repoRoot: string) =
    { Build =
        Some
            [ {| Command = "dotnet"
                 Args = "build"
                 BuildTemplate = None
                 DependsOn = [] |} ]
      Format = Auto
      Lint = true
      Cache = detectDefaultCacheBackend repoRoot
      Analyzers = None
      Tests = None
      FileCommands = []
      Exclude = []
      LogDir = "logs" }

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

        {| Command = cmd
           Args = args
           BuildTemplate = buildTemplate
           DependsOn = dependsOn |}

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

            if paths.IsEmpty then None else Some {| Paths = paths |}
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

                        let coverage =
                            match p.TryGetProperty("coverage") with
                            | true, v when v.ValueKind = JsonValueKind.False -> false
                            | _ -> true

                        { Project = project
                          Command = command
                          Args = args
                          Group = group
                          Environment = env
                          FilterTemplate = filterTemplate
                          ClassJoin = classJoin
                          Coverage = coverage })
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
                    failwith "fileCommands entry must specify `pattern` or `afterTests`"

                if afterTests.IsSome && name.IsNone then
                    failwith "fileCommands entries with `afterTests` require an explicit `name`"

                // Derive the effective plugin name up-front so registration is
                // a straight mapping. Uses the explicit `name` when given, else
                // falls back to a pattern-derived name (guaranteed Some here by
                // the validation above).
                let pluginName =
                    match name with
                    | Some n -> n
                    | None -> $"file-cmd-%s{Option.get pattern}"

                {| PluginName = pluginName
                   Pattern = pattern
                   AfterTests = afterTests
                   Command = command
                   Args = args |})
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

    { Build = build
      Format = format
      Lint = lint
      Cache = cache
      Analyzers = analyzers
      Tests = tests
      FileCommands = fileCommands
      Exclude = exclude
      LogDir = logDir }

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
let loadConfig (repoRoot: string) : DaemonConfiguration =
    let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")

    let defaults = defaultConfigFor repoRoot

    if not (File.Exists configPath) then
        Logging.info "config" "No .fs-hot-watch.json found, using defaults (build + format + lint)"
        defaults
    else

        try
            let json = File.ReadAllText(configPath)
            let config = parseConfig json defaults
            Logging.info "config" "Loaded .fs-hot-watch.json"
            config
        with ex ->
            Logging.error "config" $"Failed to parse .fs-hot-watch.json: %s{ex.Message}"
            Logging.info "config" "Using defaults"
            defaults

/// Wrap a shell command string into a callback that runs via splitCommand + runProcess.
/// Returns (success, output).
let private makeShellHookWithResult (label: string) (repoRoot: string) (cmd: string) : unit -> bool * string =
    fun () ->
        Logging.info label $"Running %s{label}: %s{cmd}"
        let (command, args) = FsHotWatch.StringHelpers.splitCommand cmd
        let (success, output) = runProcess command args repoRoot []

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
        daemon.RegisterHandler(FsHotWatch.Fantomas.FormatCheckPlugin.createFormatCheck getCommitId)
    | Off -> ()

    // Lint plugin
    if config.Lint then
        let lintConfigPath =
            let p = Path.Combine(repoRoot, "fsharplint.json")
            if File.Exists(p) then Some p else None

        match lintConfigPath with
        | Some path -> Logging.info "config" $"Registering LintPlugin with config: %s{path}"
        | None -> Logging.info "config" "Registering LintPlugin (no fsharplint.json found)"

        daemon.RegisterHandler(FsHotWatch.Lint.LintPlugin.create lintConfigPath getCommitId None)

    // Analyzers plugin
    match config.Analyzers with
    | Some a ->
        let resolvedPaths =
            a.Paths
            |> List.map (fun p ->
                if Path.IsPathRooted(p) then
                    p
                else
                    Path.GetFullPath(Path.Combine(repoRoot, p)))
            |> List.filter Directory.Exists

        if not resolvedPaths.IsEmpty then
            Logging.info "config" $"Registering AnalyzersPlugin with %d{resolvedPaths.Length} paths"
            daemon.RegisterHandler(FsHotWatch.Analyzers.AnalyzersPlugin.create resolvedPaths getCommitId)
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
                  ClassJoin = p.ClassJoin })

        let beforeRun = t.BeforeRun |> Option.map (makeShellHook "beforeRun" true repoRoot)

        // Coverage args — generate Cobertura XML per test project (respecting per-project opt-out)
        let coverageExcludedProjects =
            t.Projects
            |> List.filter (fun p -> not p.Coverage)
            |> List.map (fun p -> p.Project)
            |> Set.ofList

        // Coverage XML is emitted under <repoRoot>/<tests.coverageDir>/<project>/
        // so external coverage tools (e.g. coverageratchet invoked via a
        // fileCommands afterTests entry) can read it. The output directory is
        // configurable via `tests.coverageDir` (default `"coverage"`).
        // Per-project opt-out is honored via `coverageExcludedProjects`.
        let coverageArgs =
            Some(fun (project: string) ->
                if coverageExcludedProjects.Contains(project) then
                    ""
                else
                    let outputDir = Path.Combine(repoRoot, t.CoverageDir, project)
                    Directory.CreateDirectory(outputDir) |> ignore
                    let outputPath = Path.GetFullPath(Path.Combine(outputDir, "coverage.cobertura.xml"))

                    $"--coverage --coverage-output-format cobertura --coverage-output \"%s{outputPath}\"")

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

        Logging.info "config" $"Registering TestPrunePlugin with %d{testConfigs.Length} test projects"

        let handler =
            create dbPath repoRoot (Some testConfigs) buildExtensions beforeRun None coverageArgs getCommitId

        daemon.RegisterHandler(handler)
    | None -> ()

    // File commands
    for fc in config.FileCommands do
        let trigger: FsHotWatch.FileCommand.FileCommandPlugin.CommandTrigger =
            { FilePattern =
                fc.Pattern
                |> Option.map (fun p -> fun (path: string) -> FsHotWatch.Watcher.matchesPattern p path)
              AfterTests = fc.AfterTests }

        Logging.info "config" $"Registering FileCommandPlugin: %s{fc.PluginName} → %s{fc.Command} %s{fc.Args}"

        daemon.RegisterHandler(
            FsHotWatch.FileCommand.FileCommandPlugin.create
                (FsHotWatch.PluginFramework.PluginName.create fc.PluginName)
                trigger
                fc.Command
                fc.Args
                getCommitId
        )

        // Expose the pattern to the host so the rerun IPC endpoint can
        // synthesize a matching fake file path.
        match fc.Pattern with
        | Some pattern -> daemon.Host.RegisterFileCommandPattern(fc.PluginName, pattern)
        | None -> ()
