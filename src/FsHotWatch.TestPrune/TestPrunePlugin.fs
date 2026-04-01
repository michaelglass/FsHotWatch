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
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
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

type TestRunPhase =
    | TestsIdle of Lifecycle<Idle, TestResults option>
    | TestsRunning of Lifecycle<Running, TestResults option>
    | TestsRunningRerunQueued of Lifecycle<Running, TestResults option>

type TestPruneState =
    {
        PendingAnalysis: Map<string, AnalysisResult list>
        SymbolSnapshot: Map<string, SymbolInfo list>
        AffectedTests: TestMethodInfo list
        ChangedSymbols: string list
        ChangedFiles: string list
        TestPhase: TestRunPhase
        AnalysisRan: bool
        /// Maps test class name → absolute source file path (built during FileChecked analysis).
        TestClassFiles: Map<string, string>
    }

type TestPruneMsg = TestsFinished of TestResults

let private formatTestResultsJson (results: TestResults) =
    let projects =
        results.Results
        |> Map.toList
        |> List.map (fun (name, result) ->
            let (status, output) =
                match result with
                | TestsPassed o -> ("passed", o)
                | TestsFailed o -> ("failed", o)

            let escapedName = JsonSerializer.Serialize(name)
            let escapedOutput = JsonSerializer.Serialize(truncateOutput 200 output)
            $"{{\"project\": %s{escapedName}, \"status\": \"%s{status}\", \"output\": %s{escapedOutput}}}")
        |> String.concat ", "

    $"{{\"elapsed\": \"%.1f{results.Elapsed.TotalSeconds}s\", \"projects\": [%s{projects}]}}"

/// Build the filter arg string for a config given affected classes.
let private buildFilterArgs (config: TestConfig) (classesByProject: Map<string, string list>) : string option =
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
            | TestsFailed output ->
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
/// Handles beforeRun, coverageArgs, process execution, result storage.
/// rawFilter is a passthrough filter string (from run-tests command), bypassing the template.
let private executeTests
    (repoRoot: string)
    (beforeRun: (unit -> unit) option)
    (coverageArgs: (string -> string) option)
    (afterRun: (TestResults -> unit) option)
    (configs: TestConfig list)
    (affectedClassesByProject: Map<string, string list>)
    (rawFilter: string option)
    =
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

                        // Template-based class filter (from impact analysis).
                        // When the map is non-empty but has no classes for this project,
                        // skip the project entirely (impact analysis found no relevant tests).
                        let skipProject =
                            not affectedClassesByProject.IsEmpty
                            && not (affectedClassesByProject |> Map.containsKey config.Project)

                        if skipProject then
                            Logging.info "test-prune" $"Skipping %s{config.Project} — no affected classes"

                            results <- (config.Project, TestsPassed "") :: results
                        else

                            match buildFilterArgs config affectedClassesByProject with
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

        match afterRun with
        | Some hook -> hook testResults
        | None -> ()

        Logging.info
            "test-prune"
            $"Tests complete: %d{testResults.Results.Count} projects, %.1f{testResults.Elapsed.TotalSeconds}s"

        return testResults
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

            let combined =
                { Symbols = items |> List.collect (fun r -> r.Symbols)
                  Dependencies = items |> List.collect (fun r -> r.Dependencies)
                  TestMethods = items |> List.collect (fun r -> r.TestMethods) }

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

/// Create a TestPrune plugin handler using the declarative plugin framework.
let create
    (dbPath: string)
    (repoRoot: string)
    (testConfigs: TestConfig list option)
    (extensions: ITestPruneExtension list option)
    (beforeRun: (unit -> unit) option)
    (afterRun: (TestResults -> unit) option)
    (coverageArgs: (string -> string) option)
    =
    let db = Database.create dbPath

    let hasTestConfigs =
        testConfigs |> Option.map (List.isEmpty >> not) |> Option.defaultValue false

    let initialState =
        { PendingAnalysis = Map.empty
          SymbolSnapshot = Map.empty
          AffectedTests = []
          ChangedSymbols = []
          ChangedFiles = []
          TestPhase = TestsIdle(Lifecycle.create None)
          AnalysisRan = false
          TestClassFiles = Map.empty }

    let runTestsWithImpact (ctx: PluginCtx<TestPruneMsg>) (configs: TestConfig list) (state: TestPruneState) =
        async {
            try
                // Combine AST-based affected tests with extension results
                let extensionTests =
                    match extensions with
                    | Some exts ->
                        exts
                        |> List.collect (fun ext ->
                            try
                                ext.FindAffectedTests (TestPrune.Ports.toRouteStore db) state.ChangedFiles repoRoot
                            with ex ->
                                Logging.error "test-prune" $"Extension '%s{ext.Name}' failed: %s{ex.Message}"
                                [])
                    | None -> []

                // Group affected classes by test project so each project only gets its own classes
                let astByProject =
                    state.AffectedTests
                    |> List.groupBy (fun t -> t.TestProject)
                    |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)

                let extByProject =
                    extensionTests
                    |> List.groupBy (fun t -> t.TestProject)
                    |> List.map (fun (proj, tests) -> proj, tests |> List.map (fun t -> t.TestClass) |> List.distinct)

                let affectedByProject =
                    (astByProject @ extByProject)
                    |> List.groupBy fst
                    |> List.map (fun (proj, groups) -> proj, groups |> List.collect snd |> List.distinct)
                    |> Map.ofList

                let totalClasses = affectedByProject |> Map.values |> Seq.sumBy List.length

                if totalClasses = 0 then
                    Logging.info "test-prune" "No affected classes found — running all tests"
                else
                    for (proj, classes) in affectedByProject |> Map.toList do
                        Logging.info "test-prune" $"Affected classes for %s{proj}: %A{classes}"

                let! results = executeTests repoRoot beforeRun coverageArgs afterRun configs affectedByProject None

                ctx.EmitTestCompleted(results)

                let allPassed =
                    results.Results
                    |> Map.forall (fun _ r ->
                        match r with
                        | TestsPassed _ -> true
                        | _ -> false)

                if allPassed then
                    ctx.ClearAllErrors()
                    ctx.ReportStatus(Completed(DateTime.UtcNow))
                else
                    reportTestErrors ctx state.TestClassFiles results

                    let failedProjects =
                        results.Results
                        |> Map.toList
                        |> List.choose (fun (name, r) ->
                            match r with
                            | TestsFailed _ -> Some name
                            | _ -> None)

                    let names = failedProjects |> String.concat ", "

                    ctx.ReportStatus(
                        PluginStatus.Failed($"%d{failedProjects.Length} failed: %s{names}", DateTime.UtcNow)
                    )

                ctx.Post(TestsFinished results)
            with ex ->
                Logging.error "test-prune" $"runTests failed: %s{ex.Message}"
                ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                // Post a dummy failed result so we transition back to idle
                let failResult =
                    { Results = Map.empty
                      Elapsed = TimeSpan.Zero }

                ctx.Post(TestsFinished failResult)
        }

    let commands =
        [ "affected-tests",
          fun (state: TestPruneState) (_args: string array) ->
              async {
                  if not state.AnalysisRan then
                      return "{\"status\": \"not analyzed\"}"
                  else
                      let tests =
                          state.AffectedTests
                          |> List.map (fun t ->
                              $"{{\"project\": \"%s{t.TestProject}\", \"class\": \"%s{t.TestClass}\", \"method\": \"%s{t.TestMethod}\"}}")
                          |> String.concat ", "

                      return $"[%s{tests}]"
              }

          "changed-files",
          fun (state: TestPruneState) (_args: string array) ->
              async {
                  let files =
                      state.ChangedFiles |> List.map (fun f -> $"\"%s{f}\"") |> String.concat ", "

                  return $"[%s{files}]"
              }

          "test-results",
          fun (state: TestPruneState) (_args: string array) ->
              async {
                  match state.TestPhase with
                  | TestsRunning _
                  | TestsRunningRerunQueued _ -> return "{\"status\": \"running\"}"
                  | TestsIdle idle ->
                      match Lifecycle.value idle with
                      | Some results -> return formatTestResultsJson results
                      | None -> return "{\"status\": \"not run\"}"
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
                        | TestsRunning _
                        | TestsRunningRerunQueued _ -> return "{\"error\": \"tests already running\"}"
                        | TestsIdle _ ->
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
                                            v.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq |> Some
                                        | false, _ -> None

                                    // Resolve configs or produce an error
                                    let lastResults =
                                        match state.TestPhase with
                                        | TestsIdle idle -> Lifecycle.value idle
                                        | TestsRunning _
                                        | TestsRunningRerunQueued _ -> None

                                    let configsResult =
                                        if onlyFailed then
                                            match lastResults with
                                            | Some prev ->
                                                let failedNames =
                                                    prev.Results
                                                    |> Map.toList
                                                    |> List.choose (fun (name, r) ->
                                                        match r with
                                                        | TestsFailed _ -> Some name
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
                                    | Error msg -> return $"{{\"error\": %s{JsonSerializer.Serialize(msg)}}}"
                                    | Ok configs when configs.IsEmpty ->
                                        return "{\"error\": \"no matching test projects\"}"
                                    | Ok configs ->
                                        let! results =
                                            executeTests
                                                repoRoot
                                                beforeRun
                                                coverageArgs
                                                afterRun
                                                configs
                                                Map.empty
                                                filter

                                        return formatTestResultsJson results
                            with ex ->
                                Logging.error "test-prune" $"run-tests failed: %s{ex.Message}"
                                return $"{{\"error\": %s{JsonSerializer.Serialize(ex.Message)}}}"
                    } ]
        | _ -> commands

    { Name = "test-prune"
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
                        | TestsRunning _
                        | TestsRunningRerunQueued _ -> false

                    if isIdle then
                        ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

                    try
                        let relPath = Path.GetRelativePath(repoRoot, result.File).Replace('\\', '/')

                        let newChangedFiles =
                            if not (state.ChangedFiles |> List.contains relPath) then
                                relPath :: state.ChangedFiles
                            else
                                state.ChangedFiles

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

                            // Update class→file mapping for test methods found in this file
                            let newClassFiles =
                                fileAnalysis.TestMethods
                                |> List.fold (fun acc t -> Map.add t.TestClass result.File acc) state.TestClassFiles

                            let newState =
                                { state with
                                    ChangedFiles = newChangedFiles
                                    PendingAnalysis = newPending
                                    ChangedSymbols = newChangedSymbols
                                    TestClassFiles = newClassFiles
                                    AnalysisRan = true }

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

                            return
                                { state with
                                    ChangedFiles = newChangedFiles }
                    with ex ->
                        if isIdle then
                            ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow))

                        return state

                | PluginEvent.BuildCompleted buildResult ->
                    match buildResult with
                    | BuildSucceeded ->
                        match state.TestPhase with
                        | TestsRunning running
                        | TestsRunningRerunQueued running ->
                            Logging.info
                                "test-prune"
                                "BuildSucceeded received but tests already running — will re-run after"

                            return
                                { state with
                                    TestPhase = TestsRunningRerunQueued running }
                        | TestsIdle idle ->
                            Logging.info "test-prune" $"BuildSucceeded received, running tests"

                            ctx.ReportStatus(PluginStatus.Running(since = DateTime.UtcNow))

                            // Flush pending analysis to DB
                            let flushedState = flushPendingAnalysis db state

                            // Query affected tests AFTER flush (Bug 2 fix)
                            let affectedTests =
                                let symbols = flushedState.ChangedSymbols |> List.distinct

                                if symbols.IsEmpty then
                                    []
                                else
                                    let affected = db.QueryAffectedTests(symbols)

                                    Logging.info
                                        "test-prune"
                                        $"QueryAffectedTests(%A{symbols}): %d{affected.Length} affected tests"

                                    affected

                            let stateWithAffected =
                                { flushedState with
                                    AffectedTests = affectedTests }

                            let running = Lifecycle.start idle

                            let newState =
                                { stateWithAffected with
                                    TestPhase = TestsRunning running }

                            // Dispatch tests to thread pool
                            match testConfigs with
                            | Some configs when not configs.IsEmpty ->
                                async { do! runTestsWithImpact ctx configs newState } |> Async.Start

                                return newState
                            | _ ->
                                // No test configs — flush only, transition back to idle
                                let idleAgain = Lifecycle.complete None running

                                return
                                    { newState with
                                        TestPhase = TestsIdle idleAgain }
                    | BuildFailed _ -> return state

                | Custom(TestsFinished testResults) ->
                    match state.TestPhase with
                    | TestsRunningRerunQueued running ->
                        Logging.info "test-prune" "Re-running tests (queued during previous run)"

                        // Flush any new pending analysis
                        let flushedState =
                            flushPendingAnalysis
                                db
                                { state with
                                    TestPhase = TestsRunning running }

                        // Query affected tests after flush
                        let affectedTests =
                            let symbols = flushedState.ChangedSymbols |> List.distinct

                            if symbols.IsEmpty then
                                []
                            else
                                let affected = db.QueryAffectedTests(symbols)

                                Logging.info
                                    "test-prune"
                                    $"QueryAffectedTests(%A{symbols}): %d{affected.Length} affected tests"

                                affected

                        let rerunState =
                            { flushedState with
                                AffectedTests = affectedTests }

                        match testConfigs with
                        | Some configs when not configs.IsEmpty ->
                            async { do! runTestsWithImpact ctx configs rerunState } |> Async.Start
                        | _ -> ()

                        return rerunState
                    | TestsRunning running ->
                        let completed = Lifecycle.complete (Some testResults) running

                        return
                            { state with
                                TestPhase = TestsIdle completed
                                ChangedFiles = []
                                ChangedSymbols = []
                                AffectedTests = [] }
                    | TestsIdle _ ->
                        // Unexpected but handle gracefully
                        return state

                | _ -> return state
            }
      Commands = allCommands
      Subscriptions =
        { PluginSubscriptions.none with
            FileChecked = true
            BuildCompleted = hasTestConfigs } }
