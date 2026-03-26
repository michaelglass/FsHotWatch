module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff

/// Configuration for a test project to run.
type TestConfig =
    { Project: string
      Command: string
      Args: string
      Group: string
      Environment: (string * string) list }

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
        ?coverageArgs: string -> string -> string
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

    /// Execute test configs with an optional extra filter appended to args.
    /// Handles beforeRun, coverageArgs, process execution, result storage, and status reporting.
    let executeTests (ctx: PluginContext) (configs: TestConfig list) (extraFilter: string option) =
        eprintfn "  [test-prune] executeTests starting with %d configs" configs.Length
        testsRunning <- true
        let sw = Stopwatch.StartNew()

        match beforeRun with
        | Some setup ->
            eprintfn "  [test-prune] Running beforeRun setup..."
            setup ()
            eprintfn "  [test-prune] beforeRun complete"
        | None -> ()

        let groups = configs |> List.groupBy (fun c -> c.Group)

        let groupResults =
            groups
            |> List.map (fun (_, groupConfigs) ->
                async {
                    let mutable results = []

                    for config in groupConfigs do
                        let baseArgs =
                            match extraFilter with
                            | Some filter -> $"%s{config.Args} %s{filter}"
                            | None -> config.Args

                        let finalArgs =
                            match coverageArgs with
                            | Some covFn -> covFn config.Project baseArgs
                            | None -> baseArgs

                        eprintfn "  [test-prune] Running: %s %s" config.Command finalArgs

                        let (success, output) =
                            runProcess config.Command finalArgs repoRoot config.Environment

                        eprintfn "  [test-prune] %s: %s" config.Project (if success then "PASSED" else "FAILED")

                        if not success then
                            let lines = output.Split('\n')

                            // Extract failed test names and summary
                            let failedTests = lines |> Array.filter (fun l -> l.StartsWith("failed "))

                            let summaryLines =
                                lines
                                |> Array.filter (fun l ->
                                    l.StartsWith("failed ")
                                    || l.StartsWith("Test run summary:")
                                    || l.Contains("total:")
                                    || l.Contains("failed:")
                                    || l.Contains("succeeded:"))

                            eprintfn "  [test-prune] %s: %d test(s) failed:" config.Project failedTests.Length

                            for line in failedTests do
                                eprintfn "    %s" line

                            for line in summaryLines |> Array.filter (fun l -> not (l.StartsWith("failed "))) do
                                eprintfn "    %s" line

                        let result = if success then TestsPassed output else TestsFailed output
                        results <- (config.Project, result) :: results

                    return results
                })
            |> Async.Parallel
            |> Async.RunSynchronously
            |> Array.toList
            |> List.collect id

        sw.Stop()

        let testResults =
            { Results = groupResults |> Map.ofList
              Elapsed = sw.Elapsed }

        Volatile.Write(&lastTestResults, Some testResults)

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        testsRunning <- false
        testsCompleted <- true

        eprintfn
            "  [test-prune] Tests complete: %d projects, %.1fs"
            testResults.Results.Count
            testResults.Elapsed.TotalSeconds

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

        testResults

    /// Run tests with impact-analysis filtering (called from OnBuildCompleted).
    let runTests (ctx: PluginContext) (configs: TestConfig list) =
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
                        eprintfn $"  [test-prune] Extension '%s{ext.Name}' failed: %s{ex.Message}"
                        [])
            | None -> []

        let affectedClasses =
            let astClasses =
                Volatile.Read(&lastAffectedTests) |> List.map (fun t -> t.TestClass)

            (astClasses @ extensionClasses) |> List.distinct

        let filter =
            match affectedClasses with
            | [] -> None
            | classes ->
                classes
                |> List.map (fun c -> $"--filter-class \"%s{c}\"")
                |> String.concat " "
                |> Some

        executeTests ctx configs filter |> ignore

    interface IFsHotWatchPlugin with
        member _.Name = "test-prune"

        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                // Don't overwrite test results with analysis status — stay at
                // Completed/Failed until the next build triggers a new test run
                if not testsRunning && not testsCompleted then
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                try
                    let relPath = Path.GetRelativePath(repoRoot, result.File).Replace('\\', '/')

                    let currentFiles = Volatile.Read(&lastChangedFiles)

                    if not (currentFiles |> List.contains relPath) then
                        Volatile.Write(&lastChangedFiles, relPath :: currentFiles)

                    match
                        analyzeSource ctx.Checker result.File result.Source result.ProjectOptions
                        |> Async.RunSynchronously
                    with
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

                        // Diff current symbols against previously stored (read BEFORE overwriting)
                        let storedSymbols = db.GetSymbolsInFile(relPath)
                        db.RebuildForProject(projectName, fileAnalysis)
                        let changes = detectChanges normalizedSymbols storedSymbols
                        let changedNames = changedSymbolNames changes

                        if not changedNames.IsEmpty then
                            let affected = db.QueryAffectedTests(changedNames)
                            Volatile.Write(&lastAffectedTests, affected)

                        analysisRan <- true
                    | Error msg ->
                        eprintfn $"  [test-prune] Analysis failed for %s{relPath}: %s{msg}"

                        if not testsRunning then
                            ctx.ReportStatus(PluginStatus.Failed($"Analysis failed: %s{msg}", DateTime.UtcNow))

                    if not testsRunning then
                        ctx.ReportStatus(Completed(box (Volatile.Read(&lastAffectedTests)), DateTime.UtcNow))
                with ex ->
                    if not testsRunning then
                        ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            // Subscribe to build completion for test execution
            match testConfigs with
            | Some configs when not configs.IsEmpty ->
                eprintfn "  [test-prune] Subscribing to OnBuildCompleted with %d test configs" configs.Length

                let mutable pendingRerun = false

                ctx.OnBuildCompleted.Add(fun result ->
                    match result with
                    | BuildSucceeded ->
                        if testsRunning then
                            eprintfn
                                "  [test-prune] BuildSucceeded received but tests already running — will re-run after"

                            pendingRerun <- true
                        else
                            eprintfn "  [test-prune] BuildSucceeded received, running %d test configs" configs.Length
                            ctx.ReportStatus(Running(since = DateTime.UtcNow))

                            try
                                runTests ctx configs

                                if pendingRerun then
                                    pendingRerun <- false
                                    eprintfn "  [test-prune] Re-running tests (queued during previous run)"
                                    runTests ctx configs
                            with ex ->
                                testsRunning <- false
                                pendingRerun <- false
                                eprintfn "  [test-prune] runTests failed: %s" ex.Message
                                ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
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
                        if testsRunning then
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
                            if testsRunning then
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
                                            let results = executeTests ctx configs filter
                                            return formatTestResultsJson results
                                with ex ->
                                    testsRunning <- false
                                    eprintfn "  [test-prune] run-tests failed: %s" ex.Message
                                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))
                                    return $"{{\"error\": %s{JsonSerializer.Serialize(ex.Message)}}}"
                        }
                )
            | _ -> ()

        member _.Dispose() = ()
