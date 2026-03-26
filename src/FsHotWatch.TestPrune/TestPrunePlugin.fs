module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

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
    }

/// TestPrune plugin — re-indexes changed files using the warm FSharpChecker,
/// reports which tests are affected, and optionally runs tests after build completes.
type TestPrunePlugin
    (
        dbPath: string,
        repoRoot: string,
        ?testConfigs: TestConfig list,
        ?extensions: ITestPruneExtension list,
        ?beforeRun: unit -> unit,
        ?afterRun: TestResults -> unit,
        ?coverageArgs: string -> string
    ) =
    let db = Database.create dbPath
    let mutable lastAffectedTests: TestMethodInfo list = []
    let mutable lastChangedFiles: string list = []
    let mutable lastTestResults: TestResults option = None

    let mutable testsRunning = false
    let mutable testsCompleted = false
    let mutable analysisRan = false

    let hasTestConfigs =
        testConfigs |> Option.map (List.isEmpty >> not) |> Option.defaultValue false

    let formatTestResultsJson (results: TestResults) =
        let truncate (s: string) =
            let lines = s.Split('\n')

            if lines.Length <= 200 then
                s
            else
                lines |> Array.skip (lines.Length - 200) |> String.concat "\n"

        let projects =
            results.Results
            |> Map.toList
            |> List.map (fun (name, result) ->
                let (status, output) =
                    match result with
                    | TestsPassed o -> ("passed", o)
                    | TestsFailed o -> ("failed", o)

                let escapedName = JsonSerializer.Serialize(name)
                let escapedOutput = JsonSerializer.Serialize(truncate output)
                $"{{\"project\": %s{escapedName}, \"status\": \"%s{status}\", \"output\": %s{escapedOutput}}}")
            |> String.concat ", "

        $"{{\"elapsed\": \"%.1f{results.Elapsed.TotalSeconds}s\", \"projects\": [%s{projects}]}}"

    /// Build the filter arg string for a config given affected classes.
    let buildFilterArgs (config: TestConfig) (classes: string list) : string option =
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

    /// Execute test configs with optional affected classes for filtering.
    /// Handles beforeRun, coverageArgs, process execution, result storage, and status reporting.
    /// rawFilter is a passthrough filter string (from run-tests command), bypassing the template.
    let executeTests
        (ctx: PluginContext)
        (configs: TestConfig list)
        (affectedClasses: string list)
        (rawFilter: string option)
        =
        Volatile.Write(&testsRunning, true)

        async {
            Logging.info "test-prune" $"executeTests starting with %d{configs.Length} configs"
            let sw = Stopwatch.StartNew()

            match beforeRun with
            | Some setup ->
                Logging.info "test-prune" "Running beforeRun setup..."
                setup ()
                Logging.info "test-prune" "beforeRun complete"
            | None -> ()

            let groups = configs |> List.groupBy (fun c -> c.Group)

            let! groupResults =
                groups
                |> List.map (fun (_, groupConfigs) ->
                    async {
                        let mutable results = []

                        for config in groupConfigs do
                            // Collect extra args (filter + coverage) to append
                            let extraArgs = ResizeArray<string>()

                            // Template-based class filter (from impact analysis)
                            match buildFilterArgs config affectedClasses with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            // Raw passthrough filter (from run-tests command)
                            match rawFilter with
                            | Some f -> extraArgs.Add(f)
                            | None -> ()

                            match coverageArgs with
                            | Some covFn -> extraArgs.Add(covFn config.Project)
                            | None -> ()

                            let finalArgs =
                                if extraArgs.Count > 0 then
                                    let extra = String.concat " " extraArgs
                                    $"%s{config.Args} %s{extra}"
                                else
                                    config.Args

                            Logging.info "test-prune" $"Running: %s{config.Command} %s{finalArgs}"

                            let (success, output) =
                                runProcess config.Command finalArgs repoRoot config.Environment

                            if success then
                                Logging.info "test-prune" $"%s{config.Project}: PASSED"
                            else
                                Logging.error "test-prune" $"%s{config.Project}: FAILED"

                            if not success then
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

                            let result = if success then TestsPassed output else TestsFailed output
                            results <- (config.Project, result) :: results

                        return results
                    })
                |> Async.Parallel

            let groupResults = groupResults |> Array.toList |> List.collect id

            sw.Stop()

            let testResults =
                { Results = groupResults |> Map.ofList
                  Elapsed = sw.Elapsed }

            Volatile.Write(&lastTestResults, Some testResults)

            match afterRun with
            | Some hook -> hook testResults
            | None -> ()

            Volatile.Write(&testsRunning, false)
            testsCompleted <- true

            Logging.info
                "test-prune"
                $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

            ctx.EmitTestCompleted(testResults)

            let allPassed =
                testResults.Results
                |> Map.forall (fun _ r ->
                    match r with
                    | TestsPassed _ -> true
                    | _ -> false)

            if allPassed then
                ctx.ReportStatus(Completed(box testResults, DateTime.UtcNow))
            else
                let failedProjects =
                    testResults.Results
                    |> Map.toList
                    |> List.choose (fun (name, r) ->
                        match r with
                        | TestsFailed _ -> Some name
                        | _ -> None)

                let names = failedProjects |> String.concat ", "
                ctx.ReportStatus(PluginStatus.Failed($"%d{failedProjects.Length} failed: %s{names}", DateTime.UtcNow))

            return testResults
        }

    /// Run tests with impact-analysis filtering (called from OnBuildCompleted).
    let runTests (ctx: PluginContext) (configs: TestConfig list) =
        async {
            // Combine AST-based affected tests with extension results
            let extensionClasses =
                match extensions with
                | Some exts ->
                    let changedFiles = Volatile.Read(&lastChangedFiles)

                    exts
                    |> List.collect (fun ext ->
                        try
                            ext.FindAffectedTests db changedFiles repoRoot
                            |> List.map (fun t -> t.TestClass)
                        with ex ->
                            Logging.error "test-prune" $"Extension '%s{ext.Name}' failed: %s{ex.Message}"
                            [])
                | None -> []

            let affectedClasses =
                let astClasses =
                    Volatile.Read(&lastAffectedTests) |> List.map (fun t -> t.TestClass)

                (astClasses @ extensionClasses) |> List.distinct

            if affectedClasses.IsEmpty then
                Logging.info "test-prune" "No affected classes found — running all tests"
            else
                Logging.info "test-prune" $"Affected classes: %A{affectedClasses}"

            let! _ = executeTests ctx configs affectedClasses None
            return ()
        }

    interface IFsHotWatchPlugin with
        member _.Name = "test-prune"

        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                // Reset completed flag so new file changes can report status
                if Volatile.Read(&testsCompleted) && not (Volatile.Read(&testsRunning)) then
                    Volatile.Write(&testsCompleted, false)

                if not (Volatile.Read(&testsRunning)) && not (Volatile.Read(&testsCompleted)) then
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                async {
                    try
                        let relPath = Path.GetRelativePath(repoRoot, result.File).Replace('\\', '/')

                        let currentFiles = Volatile.Read(&lastChangedFiles)

                        if not (currentFiles |> List.contains relPath) then
                            Volatile.Write(&lastChangedFiles, relPath :: currentFiles)

                        let! analysisResult = analyzeSource ctx.Checker result.File result.Source result.ProjectOptions

                        match analysisResult with
                        | Ok analysisResult ->
                            let projectName =
                                result.ProjectOptions.ProjectFileName |> Path.GetFileNameWithoutExtension

                            let normalizedSymbols = normalizeSymbolPaths repoRoot analysisResult.Symbols

                            let fileAnalysis =
                                { Symbols = normalizedSymbols
                                  Dependencies = analysisResult.Dependencies
                                  TestMethods =
                                    analysisResult.TestMethods
                                    |> List.map (fun t -> { t with TestProject = projectName }) }

                            let storedSymbols = db.GetSymbolsInFile(relPath)
                            db.RebuildForProject(projectName, fileAnalysis)
                            let changes = detectChanges normalizedSymbols storedSymbols
                            let changedNames = changedSymbolNames changes

                            if not changedNames.IsEmpty then
                                let affected = db.QueryAffectedTests(changedNames)
                                Volatile.Write(&lastAffectedTests, affected)

                            analysisRan <- true
                        | Error msg ->
                            Logging.error "test-prune" $"Analysis failed for %s{relPath}: %s{msg}"

                            if not (Volatile.Read(&testsRunning)) then
                                ctx.ReportStatus(PluginStatus.Failed($"Analysis failed: %s{msg}", DateTime.UtcNow))

                        if not (Volatile.Read(&testsRunning)) then
                            ctx.ReportStatus(Completed(box (Volatile.Read(&lastAffectedTests)), DateTime.UtcNow))
                    with ex ->
                        if not (Volatile.Read(&testsRunning)) then
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                }
                |> Async.Start)

            // Subscribe to build completion for test execution
            match testConfigs with
            | Some configs when not configs.IsEmpty ->
                Logging.info "test-prune" $"Subscribing to OnBuildCompleted with %d{configs.Length} test configs"

                let mutable pendingRerun = false

                ctx.OnBuildCompleted.Add(fun result ->
                    match result with
                    | BuildSucceeded ->
                        if Volatile.Read(&testsRunning) then
                            Logging.info
                                "test-prune"
                                "BuildSucceeded received but tests already running — will re-run after"

                            pendingRerun <- true
                        else
                            Logging.info
                                "test-prune"
                                $"BuildSucceeded received, running %d{configs.Length} test configs"

                            ctx.ReportStatus(Running(since = DateTime.UtcNow))
                            Volatile.Write(&testsRunning, true)

                            async {
                                try
                                    do! runTests ctx configs

                                    if pendingRerun then
                                        pendingRerun <- false
                                        Logging.info "test-prune" "Re-running tests (queued during previous run)"
                                        do! runTests ctx configs
                                with ex ->
                                    Volatile.Write(&testsRunning, false)
                                    pendingRerun <- false
                                    Logging.error "test-prune" $"runTests failed: %s{ex.Message}"
                                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                            }
                            |> Async.Start
                    | BuildFailed _ -> ())
            | _ -> ()

            ctx.RegisterCommand(
                "affected-tests",
                fun _args ->
                    async {
                        if not analysisRan then
                            return "{\"status\": \"not analyzed\"}"
                        else
                            let currentTests = Volatile.Read(&lastAffectedTests)

                            let tests =
                                currentTests
                                |> List.map (fun t ->
                                    $"{{\"project\": \"%s{t.TestProject}\", \"class\": \"%s{t.TestClass}\", \"method\": \"%s{t.TestMethod}\"}}")
                                |> String.concat ", "

                            return $"[%s{tests}]"
                    }
            )

            ctx.RegisterCommand(
                "changed-files",
                fun _args ->
                    async {
                        let currentFiles = Volatile.Read(&lastChangedFiles)

                        let files = currentFiles |> List.map (fun f -> $"\"%s{f}\"") |> String.concat ", "

                        return $"[%s{files}]"
                    }
            )

            ctx.RegisterCommand(
                "test-results",
                fun _args ->
                    async {
                        if Volatile.Read(&testsRunning) then
                            return "{\"status\": \"running\"}"
                        else
                            match Volatile.Read(&lastTestResults) with
                            | Some results -> return formatTestResultsJson results
                            | None -> return "{\"status\": \"not run\"}"
                    }
            )

            // Run tests on demand. Args JSON:
            //   {}                                    — run all configured test projects
            //   {"projects": ["Foo.Tests"]}           — run only named projects
            //   {"filter": "--filter ClassName~Foo"}   — pass-through filter (framework-agnostic)
            //   {"only-failed": true}                 — rerun only previously-failed projects
            // Ignores impact analysis — runs exactly what you ask for.
            // Always runs beforeRun, coverageArgs, updates lastTestResults.
            match testConfigs with
            | Some allConfigs when not allConfigs.IsEmpty ->
                ctx.RegisterCommand(
                    "run-tests",
                    fun args ->
                        async {
                            if Volatile.Read(&testsRunning) then
                                return "{\"error\": \"tests already running\"}"
                            else
                                ctx.ReportStatus(Running(since = DateTime.UtcNow))

                                try
                                    let argStr = if args.Length > 0 then args.[0].Trim() else "{}"

                                    let parseResult =
                                        try
                                            Ok(JsonDocument.Parse(argStr))
                                        with ex ->
                                            Error ex.Message

                                    match parseResult with
                                    | Error msg ->
                                        return $"{{\"error\": \"invalid JSON: %s{JsonSerializer.Serialize(msg)}\"}}"
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
                                                v.EnumerateArray()
                                                |> Seq.map (fun e -> e.GetString())
                                                |> Set.ofSeq
                                                |> Some
                                            | false, _ -> None

                                        // Resolve configs or produce an error
                                        let configsResult =
                                            if onlyFailed then
                                                match Volatile.Read(&lastTestResults) with
                                                | Some prev ->
                                                    let failedNames =
                                                        prev.Results
                                                        |> Map.toList
                                                        |> List.choose (fun (name, r) ->
                                                            match r with
                                                            | TestsFailed _ -> Some name
                                                            | _ -> None)
                                                        |> Set.ofList

                                                    Ok(
                                                        allConfigs
                                                        |> List.filter (fun c -> failedNames.Contains(c.Project))
                                                    )
                                                | None -> Error "no previous results — cannot determine failed projects"
                                            else
                                                match projectFilter with
                                                | Some names ->
                                                    Ok(allConfigs |> List.filter (fun c -> names.Contains(c.Project)))
                                                | None -> Ok allConfigs

                                        match configsResult with
                                        | Error msg -> return $"{{\"error\": %s{JsonSerializer.Serialize(msg)}}}"
                                        | Ok configs when configs.IsEmpty ->
                                            return "{\"error\": \"no matching test projects\"}"
                                        | Ok configs ->
                                            let! results = executeTests ctx configs [] filter
                                            return formatTestResultsJson results
                                with ex ->
                                    Volatile.Write(&testsRunning, false)
                                    Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                                    return $"{{\"error\": %s{JsonSerializer.Serialize(ex.Message)}}}"
                        }
                )
            | _ -> ()

        member _.Dispose() = ()
