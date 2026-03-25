module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.IO
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper
open TestPrune.AstAnalyzer
open TestPrune.Database
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
    let hasTestConfigs = testConfigs |> Option.map (List.isEmpty >> not) |> Option.defaultValue false

    let runTests (ctx: PluginContext) (configs: TestConfig list) =
        eprintfn "  [test-prune] runTests starting with %d configs" configs.Length
        testsRunning <- true
        let sw = Stopwatch.StartNew()

        match beforeRun with
        | Some setup ->
            eprintfn "  [test-prune] Running beforeRun setup..."
            setup ()
            eprintfn "  [test-prune] beforeRun complete"
        | None -> ()

        let affectedClasses =
            Volatile.Read(&lastAffectedTests)
            |> List.map (fun t -> t.TestClass)
            |> List.distinct

        // Group configs by Group key — same group runs sequentially, different groups run in parallel
        let groups =
            configs |> List.groupBy (fun c -> c.Group)

        let groupResults =
            groups
            |> List.map (fun (_, groupConfigs) ->
                async {
                    let mutable results = []

                    for config in groupConfigs do
                        let baseArgs =
                            match affectedClasses with
                            | [] -> config.Args
                            | classes ->
                                let filters =
                                    classes
                                    |> List.map (fun c -> $"--filter-class \"%s{c}\"")
                                    |> String.concat " "

                                $"%s{config.Args} %s{filters}"

                        let finalArgs =
                            match coverageArgs with
                            | Some covFn -> covFn config.Project baseArgs
                            | None -> baseArgs

                        eprintfn "  [test-prune] Running: %s %s" config.Command finalArgs
                        let (success, output) = runProcess config.Command finalArgs repoRoot config.Environment
                        eprintfn "  [test-prune] %s: %s" config.Project (if success then "PASSED" else "FAILED")

                        let result =
                            if success then
                                TestsPassed output
                            else
                                TestsFailed output

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
        eprintfn "  [test-prune] Tests complete: %d projects, %.1fs" testResults.Results.Count testResults.Elapsed.TotalSeconds
        ctx.EmitTestCompleted(testResults)

        let allPassed =
            testResults.Results |> Map.forall (fun _ r -> match r with TestsPassed _ -> true | _ -> false)

        if allPassed then
            ctx.ReportStatus(
                Completed(box testResults, DateTime.UtcNow)
            )
        else
            let failures =
                testResults.Results
                |> Map.filter (fun _ r -> match r with TestsFailed _ -> true | _ -> false)
                |> Map.count

            ctx.ReportStatus(
                PluginStatus.Failed($"%d{failures} test project(s) failed", DateTime.UtcNow)
            )

    interface IFsHotWatchPlugin with
        member _.Name = "test-prune"

        member _.Initialize(ctx) =
            ctx.OnFileChecked.Add(fun result ->
                // Don't overwrite test results with analysis status
                if not testsRunning && not testsCompleted then
                    ctx.ReportStatus(Running(since = DateTime.UtcNow))

                try
                    let relPath =
                        Path.GetRelativePath(repoRoot, result.File).Replace('\\', '/')

                    let currentFiles = Volatile.Read(&lastChangedFiles)

                    if not (currentFiles |> List.contains relPath) then
                        Volatile.Write(&lastChangedFiles, relPath :: currentFiles)

                    let storedSymbols = db.GetSymbolsInFile(relPath)

                    // When testConfigs is provided, don't report Completed after analysis —
                    // stay Running until tests finish. Otherwise a consumer polling status
                    // sees a brief Completed window before tests start.
                    if not testsRunning && not testsCompleted && not hasTestConfigs then
                        ctx.ReportStatus(Completed(box (Volatile.Read(&lastAffectedTests)), DateTime.UtcNow))
                with ex ->
                    if not testsRunning && not testsCompleted then
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
                            eprintfn "  [test-prune] BuildSucceeded received but tests already running — will re-run after"
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
                        let files = currentFiles |> String.concat ", "
                        return $"[%s{files}]"
                    }
            )

            ctx.RegisterCommand(
                "test-results",
                fun _args ->
                    async {
                        match Volatile.Read(&lastTestResults) with
                        | Some results ->
                            let passed =
                                results.Results
                                |> Map.filter (fun _ r -> match r with TestsPassed _ -> true | _ -> false)
                                |> Map.count

                            let failed =
                                results.Results
                                |> Map.filter (fun _ r -> match r with TestsFailed _ -> true | _ -> false)
                                |> Map.count

                            return
                                $"{{\"passed\": %d{passed}, \"failed\": %d{failed}, \"elapsed\": \"%.1f{results.Elapsed.TotalSeconds}s\"}}"
                        | None -> return "{\"status\": \"not run\"}"
                    }
            )

        member _.Dispose() = ()
