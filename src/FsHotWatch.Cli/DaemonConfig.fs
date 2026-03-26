module FsHotWatch.Cli.DaemonConfig

open System
open System.IO
open System.Text.Json
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper
open FsHotWatch.TestPrune.TestPrunePlugin

/// Configuration for a single test project.
type TestProjectConfig =
    { Project: string
      Command: string
      Args: string
      Group: string
      Environment: (string * string) list
      FilterTemplate: string option
      ClassJoin: string }

/// Parsed daemon configuration from .fs-hot-watch.json.
type DaemonConfiguration =
    { Build: {| Command: string; Args: string |} option
      Format: bool
      Lint: bool
      Analyzers: {| Paths: string list |} option
      Tests:
          {| BeforeRun: string option
             Projects: TestProjectConfig list |} option
      Coverage:
          {| Directory: string
             ThresholdsFile: string option |} option
      FileCommands:
          {| Pattern: string
             Command: string
             Args: string |} list }

let private defaultConfig =
    { Build = Some {| Command = "dotnet"; Args = "build" |}
      Format = true
      Lint = true
      Analyzers = None
      Tests = None
      Coverage = None
      FileCommands = [] }

/// Load config from .fs-hot-watch.json in repoRoot. Returns defaults if no file exists.
let loadConfig (repoRoot: string) : DaemonConfiguration =
    let configPath = Path.Combine(repoRoot, ".fs-hot-watch.json")

    if not (File.Exists configPath) then
        Logging.info "config" "No .fs-hot-watch.json found, using defaults (build + format + lint)"
        defaultConfig
    else

        try
            let json = File.ReadAllText(configPath)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let build =
                match root.TryGetProperty("build") with
                | true, v when v.ValueKind = JsonValueKind.False -> None
                | true, v when v.ValueKind = JsonValueKind.Object ->
                    let cmd =
                        match v.TryGetProperty("command") with
                        | true, c -> c.GetString()
                        | _ -> "dotnet"

                    let args =
                        match v.TryGetProperty("args") with
                        | true, a -> a.GetString()
                        | _ -> "build"

                    Some {| Command = cmd; Args = args |}
                | _ -> Some {| Command = "dotnet"; Args = "build" |}

            let format =
                match root.TryGetProperty("format") with
                | true, v -> v.GetBoolean()
                | _ -> true

            let lint =
                match root.TryGetProperty("lint") with
                | true, v -> v.GetBoolean()
                | _ -> true

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

                                { Project = project
                                  Command = command
                                  Args = args
                                  Group = group
                                  Environment = env
                                  FilterTemplate = filterTemplate
                                  ClassJoin = classJoin })
                            |> Seq.toList
                        | _ -> []

                    if projects.IsEmpty then
                        None
                    else
                        Some
                            {| BeforeRun = beforeRun
                               Projects = projects |}
                | _ -> None

            let coverage =
                match root.TryGetProperty("coverage") with
                | true, v ->
                    let dir =
                        match v.TryGetProperty("directory") with
                        | true, d -> d.GetString()
                        | _ -> "./coverage"

                    let thresholds =
                        match v.TryGetProperty("thresholdsFile") with
                        | true, t -> Some(t.GetString())
                        | _ -> None

                    Some
                        {| Directory = dir
                           ThresholdsFile = thresholds |}
                | _ -> None

            let fileCommands =
                match root.TryGetProperty("fileCommands") with
                | true, arr ->
                    arr.EnumerateArray()
                    |> Seq.map (fun fc ->
                        let pattern =
                            match fc.TryGetProperty("pattern") with
                            | true, v -> v.GetString()
                            | _ -> "*.fsx"

                        let command =
                            match fc.TryGetProperty("command") with
                            | true, v -> v.GetString()
                            | _ -> "echo"

                        let args =
                            match fc.TryGetProperty("args") with
                            | true, v -> v.GetString()
                            | _ -> ""

                        {| Pattern = pattern
                           Command = command
                           Args = args |})
                    |> Seq.toList
                | _ -> []

            let config =
                { Build = build
                  Format = format
                  Lint = lint
                  Analyzers = analyzers
                  Tests = tests
                  Coverage = coverage
                  FileCommands = fileCommands }

            Logging.info "config" "Loaded .fs-hot-watch.json"
            config
        with ex ->
            Logging.error "config" $"Failed to parse .fs-hot-watch.json: %s{ex.Message}"
            Logging.info "config" "Using defaults"
            defaultConfig

/// Register plugins on the daemon based on the loaded configuration.
let registerPlugins (daemon: Daemon) (repoRoot: string) (config: DaemonConfiguration) =
    // Format preprocessor (runs before other plugins see the file)
    if config.Format then
        Logging.info "config" "Registering FormatPreprocessor"
        daemon.RegisterPreprocessor(FsHotWatch.Fantomas.FormatCheckPlugin.FormatPreprocessor())

    // Lint plugin
    if config.Lint then
        Logging.info "config" "Registering LintPlugin"
        daemon.Register(FsHotWatch.Lint.LintPlugin.LintPlugin())

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
            daemon.Register(FsHotWatch.Analyzers.AnalyzersPlugin.AnalyzersPlugin(resolvedPaths))
    | None -> ()

    // Build plugin
    match config.Build with
    | Some b ->
        Logging.info "config" $"Registering BuildPlugin: %s{b.Command} %s{b.Args}"
        daemon.Register(FsHotWatch.Build.BuildPlugin.BuildPlugin(command = b.Command, args = b.Args))
    | None -> ()

    // TestPrune plugin
    match config.Tests with
    | Some t ->
        let dbPath = Path.Combine(repoRoot, ".fs-hot-watch", "test-impact.db")
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

        let beforeRun =
            match t.BeforeRun with
            | Some cmd ->
                Some(fun () ->
                    Logging.info "test-prune" $"Running beforeRun: %s{cmd}"
                    let parts = cmd.Split(' ', 2)
                    let command = parts.[0]
                    let args = if parts.Length > 1 then parts.[1] else ""
                    let (success, output) = runProcess command args repoRoot []

                    if not success then
                        Logging.error "test-prune" $"beforeRun failed:\n%s{output}"
                        failwith $"beforeRun failed: %s{cmd}")
            | None -> None

        // Coverage args — generate Cobertura XML per test project
        let coverageArgs =
            match config.Coverage with
            | Some cov ->
                Some(fun (project: string) ->
                    let outputDir = Path.Combine(cov.Directory, project)
                    Directory.CreateDirectory(outputDir) |> ignore
                    let outputPath = Path.GetFullPath(Path.Combine(outputDir, "coverage.cobertura.xml"))

                    $"--coverage --coverage-output-format cobertura --coverage-output \"%s{outputPath}\"")
            | None -> None

        Logging.info "config" $"Registering TestPrunePlugin with %d{testConfigs.Length} test projects"

        let plugin =
            match beforeRun, coverageArgs with
            | Some br, Some ca ->
                TestPrunePlugin(dbPath, repoRoot, testConfigs = testConfigs, beforeRun = br, coverageArgs = ca)
            | Some br, None -> TestPrunePlugin(dbPath, repoRoot, testConfigs = testConfigs, beforeRun = br)
            | None, Some ca -> TestPrunePlugin(dbPath, repoRoot, testConfigs = testConfigs, coverageArgs = ca)
            | None, None -> TestPrunePlugin(dbPath, repoRoot, testConfigs = testConfigs)

        daemon.Register(plugin)
    | None -> ()

    // Coverage plugin (after tests)
    match config.Coverage with
    | Some cov ->
        Logging.info "config" $"Registering CoveragePlugin: %s{cov.Directory}"

        daemon.Register(
            FsHotWatch.Coverage.CoveragePlugin.CoveragePlugin(
                coverageDir = cov.Directory,
                ?thresholdsFile = cov.ThresholdsFile
            )
        )
    | None -> ()

    // File commands
    for fc in config.FileCommands do
        Logging.info "config" $"Registering FileCommandPlugin: %s{fc.Pattern} → %s{fc.Command} %s{fc.Args}"
        let pattern = fc.Pattern

        let fileFilter (path: string) =
            path.EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase)

        daemon.Register(
            FsHotWatch.FileCommand.FileCommandPlugin.FileCommandPlugin(
                name = $"file-cmd-%s{fc.Pattern}",
                fileFilter = fileFilter,
                command = fc.Command,
                args = fc.Args
            )
        )
