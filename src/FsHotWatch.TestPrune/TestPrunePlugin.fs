module FsHotWatch.TestPrune.TestPrunePlugin

open System
open System.Diagnostics
open System.IO
open System.Threading
open FsHotWatch.Events
open FsHotWatch.Plugin
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

    let runProcess (cmd: string) (args: string) (workDir: string) (env: (string * string) list) =
        let psi = ProcessStartInfo(cmd, args)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.WorkingDirectory <- workDir

        for (key, value) in env do
            psi.Environment[key] <- value

        use proc = Process.Start(psi)

        let stdout =
            proc.StandardOutput.ReadToEndAsync() |> Async.AwaitTask |> Async.RunSynchronously

        let stderr =
            proc.StandardError.ReadToEndAsync() |> Async.AwaitTask |> Async.RunSynchronously

        proc.WaitForExit()
        let output = $"%s{stdout}\n%s{stderr}".Trim()
        (proc.ExitCode = 0, output)

    let runTests (ctx: PluginContext) (configs: TestConfig list) =
        let sw = Stopwatch.StartNew()

        match beforeRun with
        | Some setup -> setup ()
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

                        let (success, output) = runProcess config.Command finalArgs repoRoot config.Environment

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
                ctx.ReportStatus(Running(since = DateTime.UtcNow))

                try
                    let relPath =
                        Path.GetRelativePath(repoRoot, result.File).Replace('\\', '/')

                    let currentFiles = Volatile.Read(&lastChangedFiles)

                    if not (currentFiles |> List.contains relPath) then
                        Volatile.Write(&lastChangedFiles, relPath :: currentFiles)

                    let storedSymbols = db.GetSymbolsInFile(relPath)

                    ctx.ReportStatus(Completed(box (Volatile.Read(&lastAffectedTests)), DateTime.UtcNow))
                with ex ->
                    ctx.ReportStatus(PluginStatus.Failed(ex.Message, DateTime.UtcNow)))

            // Subscribe to build completion for test execution
            match testConfigs with
            | Some configs when not configs.IsEmpty ->
                ctx.OnBuildCompleted.Add(fun result ->
                    match result with
                    | BuildSucceeded ->
                        ctx.ReportStatus(Running(since = DateTime.UtcNow))

                        try
                            runTests ctx configs
                        with ex ->
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
