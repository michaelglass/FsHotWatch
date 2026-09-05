/// The TestPrune plugin surface: its commands, the FileChecked/BatchChecked analysis
/// path, `run-tests`, impact selection, coverage collection and the task-cache keys.
///
/// Split out of one file that outgrew TestPrune's 32,768-node symbol-traversal budget;
/// the shared harness lives in `TestPrunePluginTestSupport`, and the remaining parts are
/// `TestPruneFreshnessGateTests`, `TestPrunePendingVerificationTests` and
/// `TestPruneRunScopeTests`.
module FsHotWatch.Tests.TestPrunePluginTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.SymbolDiff
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = create ":memory:" "/tmp" None None None None None []
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "test-prune" @>

[<Fact(Timeout = 15000)>]
let ``testprune subscribes to BatchChecked`` () =
    // FileChecked (per-file accumulation) is retained alongside BatchChecked (the
    // cohort-complete flush): both subscriptions must be present, not one or the other.
    let handler = create ":memory:" "/tmp" None None None None None []

    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeFileChecked) @>
    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeBatchChecked) @>

[<Fact(Timeout = 15000)>]
let ``affected-tests command returns empty array when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact(Timeout = 15000)>]
let ``changed-files command returns empty list when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact(Timeout = 15000)>]
let ``test-prune error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None []
    host.RegisterHandler(handler)

    let fakeResult =
        { fakeFileCheckResult "/tmp/nonexistent/Fake.fs" with
            Source = "" }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

    let deadline = DateTime.UtcNow.AddSeconds(5.0)
    let mutable statusChanged = false

    while not statusChanged && DateTime.UtcNow < deadline do
        match host.GetStatus("test-prune") with
        | Some(Failed _)
        | Some(Running _) -> statusChanged <- true
        | _ -> System.Threading.Thread.Sleep(50)

    let status = host.GetStatus("test-prune")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> ()
    | Running _ -> ()
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")

[<Fact(Timeout = 15000)>]
let ``changed-files tracks files after emit with valid relative path`` () =
    withTempDir "tp-test" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None []
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\nlet x = 1\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-315 a clean FileChecked with zero symbols still records runtime file debt`` () =
    withTempDir "tp-runtime-zero-symbol" (fun tmpDir ->
        let host = PluginHost.create sharedChecker.Value tmpDir

        let handler =
            create (Path.Combine(tmpDir, "test.db")) tmpDir None None None None None []

        host.RegisterHandler(handler)

        let sourceFile = Path.Combine(tmpDir, "src", "RuntimeOnly.fs")
        let projectFile = Path.Combine(tmpDir, "RuntimeProject.fsproj")
        Directory.CreateDirectory(Path.GetDirectoryName sourceFile) |> ignore
        let source = "namespace RuntimeOnly\n"
        File.WriteAllText(sourceFile, source)

        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let projectOptions =
            getScriptOptions checker sourceFile source
            |> Async.RunSynchronously
            |> fun options ->
                { options with
                    ProjectFileName = projectFile
                    SourceFiles = [| sourceFile |] }

        pipeline.RegisterProject(sourceFile, projectOptions)

        let checkResult =
            pipeline.CheckFile(AbsFilePath.create sourceFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        emitFileAndQuiesce host checkResult

        let changed = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
        test <@ changed = Some "[\"src/RuntimeOnly.fs\"]" @>)

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-315 a zero-symbol FileChecked forces its runtime-covered project in full`` () =
    withTempDir "tp-runtime-zero-symbol-run" (fun tmpDir ->
        let sentinel = Path.Combine(tmpDir, "runtime-project-ran")
        let runner = Path.Combine(tmpDir, "runtime-project-runner.sh")
        File.WriteAllText(runner, $"#!/bin/sh\nset -eu\ntouch \"%s{sentinel}\"\n")

        let config =
            { Project = "RuntimeProject"
              Command = "sh"
              Args = runner
              Group = "default"
              Environment = []
              FilterTemplate = Some "--ignored-filter {classes}"
              ClassJoin = " "
              TimeoutSec = Some 10
              ReportVerificationFormat = Disabled }

        let coveragePaths _ =
            Some
                { Baseline = Path.Combine(tmpDir, BaselineName)
                  Partial = Path.Combine(tmpDir, PartialName)
                  Cobertura = Path.Combine(tmpDir, CoberturaName)
                  IncludeInRatchet = false
                  ArgsTemplate = "--coverage-output {output}" }

        let dbPath = Path.Combine(tmpDir, "test.db")
        let db = Database.create dbPath
        db.ReplaceRuntimeCoverage("RuntimeProject", "baseline", [ "src/RuntimeOnly.fs" ])

        let host = PluginHost.create sharedChecker.Value tmpDir

        let handler =
            create dbPath tmpDir (Some [ config ]) None None None (Some coveragePaths) []

        host.RegisterHandler(handler)

        // Establish the session baseline, then observe only the run caused by
        // the zero-symbol changed file.
        emitBuildAndWaitTerminal host
        File.Delete sentinel

        let sourceFile = Path.Combine(tmpDir, "src", "RuntimeOnly.fs")
        let projectFile = Path.Combine(tmpDir, "RuntimeProject.fsproj")
        Directory.CreateDirectory(Path.GetDirectoryName sourceFile) |> ignore
        let source = "namespace RuntimeOnly\n"
        File.WriteAllText(sourceFile, source)

        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let projectOptions =
            getScriptOptions checker sourceFile source
            |> Async.RunSynchronously
            |> fun options ->
                { options with
                    ProjectFileName = projectFile
                    SourceFiles = [| sourceFile |] }

        pipeline.RegisterProject(sourceFile, projectOptions)

        let checkResult =
            pipeline.CheckFile(AbsFilePath.create sourceFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        emitFileAndQuiesce host checkResult

        emitBatchAndQuiesce host [ sourceFile ]
        emitBuildAndWaitTerminal host
        test <@ File.Exists sentinel @>)

[<Fact(Timeout = 15000)>]
let ``duplicate file checks do not duplicate in changed-files list`` () =
    withTempDir "tp-dup" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None []
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "Dup.fs")
        File.WriteAllText(fakeFile, "module Dup\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Dup\n" }

        for _ in 1..2 do
            try
                host.EmitFileChecked(fakeResult)
            with _ ->
                ()

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``test-results command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact(Timeout = 15000)>]
let ``plugin with testConfigs subscribes to OnBuildCompleted`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let configs =
        [ { Project = "TestProject"
            Command = "echo"
            Args = "tests passed"
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = None
            ReportVerificationFormat = AutoDetect } ]

    let handler = create ":memory:" "/tmp" (Some configs) None None None None []

    host.RegisterHandler(handler)

    let status = host.GetStatus("test-prune")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 15000)>]
let ``extension is invoked via AnalyzeEdges during test run`` () =
    withTempDir "tp-ext" (fun tmpDir ->
        let mutable extensionCalled = false

        let fakeExtension =
            { new ITestPruneExtension with
                member _.Name = "fake-extension"

                member _.AnalyzeEdges _symbolStore _changedFiles _repoRoot =
                    extensionCalled <- true
                    [] }

        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "done"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) (Some(fun _db -> [ fakeExtension ])) None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        let deadline = DateTime.UtcNow.AddSeconds(10.0)

        while not extensionCalled && DateTime.UtcNow < deadline do
            System.Threading.Thread.Sleep(50)

        test <@ extensionCalled @>)

[<Fact(Timeout = 15000)>]
let ``extension error is caught and does not crash plugin`` () =
    withTempDir "tp-ext-err" (fun tmpDir ->
        let failingExtension =
            { new ITestPruneExtension with
                member _.Name = "failing-extension"

                member _.AnalyzeEdges _symbolStore _changedFiles _repoRoot = failwith "extension broke" }

        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "done"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) (Some(fun _db -> [ failingExtension ])) None None None []

        host.RegisterHandler(handler)

        // Subscribe-before-emit avoids the race where the plugin transitions
        // to terminal status before we start polling.
        let completion = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        completion.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``database read-before-write preserves previous symbols for diffing`` () =
    withTempDir "tp-db" (fun tmpDir ->
        let db = Database.create (Path.Combine(tmpDir, "test.db"))

        let symbol1: SymbolInfo =
            { FullName = "MyModule.foo"
              Kind = SymbolKind.Value
              SourceFile = "src/Lib.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "abc123"
              IsExtern = false }

        let testMethod1: TestMethodInfo =
            { SymbolFullName = "Tests.myTest"
              TestProject = "TestProj"
              TestClass = "Tests"
              TestMethod = "myTest" }

        let result1 =
            AnalysisResult.Create(
                [ symbol1 ],
                [ { FromSymbol = "Tests.myTest"
                    ToSymbol = "MyModule.foo"
                    Kind = DependencyKind.Calls
                    Source = "core" } ],
                [ testMethod1 ]
            )

        db.RebuildProjects([ result1 ])

        let symbol2 =
            { symbol1 with
                LineEnd = 5
                ContentHash = "changed" }

        let result2 = { result1 with Symbols = [ symbol2 ] }

        let storedBefore = db.GetSymbolsInFile("src/Lib.fs")
        db.RebuildProjects([ result2 ])

        test <@ storedBefore.Length = 1 @>
        test <@ storedBefore.[0].LineEnd = 1 @>

        let storedAfter = db.GetSymbolsInFile("src/Lib.fs")
        test <@ storedAfter.Length = 1 @>
        test <@ storedAfter.[0].LineEnd = 5 @>

        let (changes, _) = detectChanges [ symbol2 ] storedBefore
        let changedNames = changedSymbolNames changes
        test <@ not changedNames.IsEmpty @>

        // Diffing post-write finds nothing — the bug this guards against.
        let (noChanges, _) = detectChanges [ symbol2 ] storedAfter
        let noChangedNames = changedSymbolNames noChanges
        test <@ noChangedNames.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``FileChecked never transitions plugin to Running status`` () =
    withTempDir "tp-no-running" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir None None None None None []
        host.RegisterHandler(handler)

        let observedRunning = ref false

        host.OnStatusChanged.Add(fun (name, status) ->
            if name = "test-prune" then
                match status with
                | Running _ -> observedRunning.Value <- true
                | _ -> ())

        let fakeFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\nlet x = 1\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitForPluginTerminal host "test-prune" 12.0

        // Setting Running here caused rapid Running→Completed cycling during FCS
        // cold-start: the UI showed a constantly-changing elapsed time as if individual
        // tests were running one-by-one.
        test <@ not observedRunning.Value @>)

[<Fact(Timeout = 15000)>]
let ``FileChecked does not set Running status`` () =
    withTempDir "tp-no-complete" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "Lib.fs")
        Directory.CreateDirectory(tmpDir) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\nlet x = 1\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitForPluginTerminal host "test-prune" 12.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        match status.Value with
        | Running _ -> Assert.Fail("FileChecked must not set Running — causes status cycling in the UI")
        | _ -> ())

[<Fact(Timeout = 30000)>]
let ``FileChecked exception while tests running surfaces in the ledger without stomping the run's status (F10, AUTOMATION-99)``
    ()
    =
    // A FileChecked throw mid-test-run must never be silent, and must never be surfaced
    // by stomping a terminal status over the live run — that manufactured a terminal
    // while the run was still executing (the "started: but no elapsed:" signature) and
    // the run's own verdict overwrote it moments later anyway. The fault belongs in the
    // error ledger; the run keeps owning the status until TestsFinished lands.
    withTempDir "tp-f10-not-idle" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        // Long-running so the "tests" RunExclusive slot stays held through the whole
        // assertion; withTempDir cleanup tears down the process registry at end-of-scope.
        let configs =
            [ { Project = "TestProject"
                Command = "sleep"
                Args = "10"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        // A run must be provably live before the FileChecked, or this proves nothing.
        let runningWait =
            beginAwaitStatus host "test-prune" (function
                | Running _ -> true
                | _ -> false)

        if not (runningWait.Wait(TimeSpan.FromSeconds 10.0)) then
            Assert.Fail("test run never reached Running — cannot exercise the mid-run path")

        // `fakeFileCheckResult` carries ProjectOptions = Unchecked.defaultof<_>, so the
        // FileChecked branch of Update throws a NullReferenceException.
        let fakeFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(fakeFile, "module Lib\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        // The fault lands in the ledger (never silent) ...
        waitUntil (fun () -> not (host.GetErrorsByPlugin("test-prune").IsEmpty)) 10000
        test <@ not (host.GetErrorsByPlugin("test-prune").IsEmpty) @>

        // ... without stomping a terminal over the live run, which still owns the status.
        let statusAfterFault = host.GetStatus("test-prune")

        test
            <@
                match statusAfterFault with
                | Some(Running _) -> true
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``FileChecked sets Failed status on analysis error`` () =
    withTempDir "tp-complete-no-configs" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None []
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\nlet x = 1\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitForPluginTerminal host "test-prune" 12.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        // The null checker forces the error path; the success path would leave status alone.
        match status.Value with
        | Failed _ -> ()
        | other -> Assert.Fail($"Expected Failed on analysis error, got: %A{other}"))

[<Fact(Timeout = 15000)>]
let ``FileChecked replaces test-run Completed status with error state`` () =
    withTempDir "tp-reset" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 12.0

        // The null checker drives FileChecked down the error path, so the status must move
        // off the test run's Completed.
        let fakeFile = Path.Combine(tmpDir, "New.fs")
        File.WriteAllText(fakeFile, "module New")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module New" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        let deadline = DateTime.UtcNow.AddSeconds(5.0)
        let mutable statusChanged = false

        while not statusChanged && DateTime.UtcNow < deadline do
            match host.GetStatus("test-prune") with
            | Some(Completed _) -> System.Threading.Thread.Sleep(50)
            | _ -> statusChanged <- true

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ ->
            Assert.Fail("Expected status to change after FileChecked analysis error, not remain as test-run Completed")
        | _ -> ())

[<Fact(Timeout = 15000)>]
let ``run-tests command runs all projects and returns results`` () =
    withTempDir "tp-run" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        let projects = doc.RootElement.GetProperty("projects")
        Assert.True(projects.GetArrayLength() > 0)
        Assert.Equal("passed", projects.[0].GetProperty("status").GetString())
        Assert.True(doc.RootElement.TryGetProperty("elapsed") |> fst))

[<Fact(Timeout = 15000)>]
let ``run-tests with project filter runs only named project`` () =
    withTempDir "tp-run-proj" (fun tmpDir ->
        let configs =
            [ { Project = "Alpha"
                Command = "echo"
                Args = "alpha"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect }
              { Project = "Beta"
                Command = "echo"
                Args = "beta"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        let result =
            host.RunCommand("run-tests", [| """{"projects": ["Alpha"]}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ result.Value.Contains("Alpha") @>
        test <@ not (result.Value.Contains("Beta")) @>)

[<Fact(Timeout = 15000)>]
let ``run-tests with filter passes raw filter args through to the test command`` () =
    withTempDir "tp-run-filter" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "marker"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        let result =
            host.RunCommand(
                "run-tests",
                [| """{"filter": "--filter-class FooTests --filter-trait Category=Browser"}""" |]
            )
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        // `echo` echoes its argv, so the captured output proves the filter
        // string was appended to the test command line.
        test <@ result.Value.Contains("--filter-class FooTests") @>
        test <@ result.Value.Contains("--filter-trait Category=Browser") @>)

[<Fact(Timeout = 15000)>]
let ``run-tests with only-failed reruns failed projects`` () =
    withTempDir "tp-run-failed" (fun tmpDir ->
        let configs =
            [ { Project = "Passes"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect }
              { Project = "Fails"
                Command = "false"
                Args = ""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 12.0

        let result =
            host.RunCommand("run-tests", [| """{"only-failed": true}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ result.Value.Contains("Fails") @>
        test <@ not (result.Value.Contains("Passes")) @>)

[<Fact(Timeout = 15000)>]
let ``run-tests not registered when no testConfigs`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let handler = create ":memory:" "/tmp" None None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsNone @>

/// A minimal Cobertura document, seeded only so the coverage plugin's `pollForFiles`
/// finds a file on its FIRST attempt (see the test below). Fully covered (`hits="1"`) so
/// the default 100% thresholds pass and the run is a deterministic `Completed`.
let private trivialCoberturaXml =
    """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="1" branch-rate="1" version="1.9">
  <packages>
    <package name="pkg" line-rate="1" branch-rate="1">
      <classes>
        <class filename="Covered.fs" name="Covered.fs" line-rate="1" branch-rate="1">
          <lines>
            <line number="1" hits="1" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

[<Fact(Timeout = 30000)>]
let ``run-tests emits TestRunCompleted so other plugins see the run`` () =
    withTempDir "tp-run-trc" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, "{}")

        // Coverage is the observer here, not the thing under test. With no cobertura XML
        // on disk it polls `pollForFiles searchDir 50 100` — a designed 5s floor
        // (measured: 5122ms idle) that dilates past 10s under a saturated thread pool.
        // Seeding one file makes it return on attempt 0 without weakening anything: the
        // plugin still runs a real check and still has to reach a terminal status.
        File.WriteAllText(Path.Combine(dir, "coverage.cobertura.xml"), trivialCoberturaXml)

        let host, _ = withSingleProjectHarness dir "TestProject"
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Subscribe before the trigger, and to the NEXT transition rather than the current
        // status: `waitForTerminalStatus` polls `GetStatus`, which cannot tell "reached
        // terminal because of THIS run" from "was already terminal". `run-tests` returns
        // as soon as the force-run resolves its reply — strictly before the mailbox
        // handles `TestsFinished` — so the signal always arrives after this subscription.
        let completion = beginAwaitNextTerminal host "coverage"

        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore

        // A liveness backstop, not a timing assertion: the awaited work is one mailbox
        // dispatch plus a walk and a ~600-byte parse. It exists so a wedged plugin fails
        // with the message below instead of hanging to the xUnit timeout.
        let observed = completion.Wait(TimeSpan.FromSeconds 15.0)

        if not observed then
            let last = host.GetStatus("coverage")

            Assert.Fail(
                $"Expected coverage to process TestRunCompleted after run-tests; it never reported a terminal status. Last status: %A{last}"
            ))

[<Fact(Timeout = 15000)>]
let ``dispose is callable`` () =
    // Framework-managed plugins need no explicit dispose; this pins only construction.
    let _handler = create ":memory:" "/tmp" None None None None None []
    ()

[<Fact(Timeout = 15000)>]
let ``parseFailedTests extracts class and method from xUnit MTP output`` () =
    let output =
        "failed FsHotWatch.Tests.PluginHostTests.plugin receives file change events (1ms)\nfailed FsHotWatch.Tests.BuildPluginTests.build fires on source change (0ms)\nTest run summary: Failed!\n  total: 10\n  failed: 2"

    let parsed = parseFailedTests output

    test <@ parsed.Length = 2 @>

    test
        <@
            parsed
            |> List.exists (fun (cls, meth, _) ->
                cls = "FsHotWatch.Tests.PluginHostTests"
                && meth = "plugin receives file change events")
        @>

    test
        <@
            parsed
            |> List.exists (fun (cls, meth, _) ->
                cls = "FsHotWatch.Tests.BuildPluginTests"
                && meth = "build fires on source change")
        @>

[<Fact(Timeout = 15000)>]
let ``parseFailedTests handles output with no failures`` () =
    let parsed: (string * string * string) list =
        parseFailedTests "Test run summary: Passed!\n  total: 10\n  succeeded: 10"

    test <@ parsed.Length = 0 @>

[<Fact(Timeout = 15000)>]
let ``test failures are reported to error ledger`` () =
    withTempDir "tp-ledger" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "false"
                Args = ""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        test <@ host.HasFailingReasons(warningsAreFailures = true) @>)

[<Fact(Timeout = 20000)>]
let ``test errors are cleared when all tests pass`` () =
    withTempDir "tp-ledger-clear" (fun tmpDir ->
        // The second cycle is driven by `run-tests`, not a second BuildCompleted: a warm
        // BuildCompleted with no changed symbols hits the zero-affected skip and runs
        // NOTHING, and a run that ran nothing may not clear a red. Here the change that
        // flips the outcome is a FILE the symbol graph cannot see, so an unfiltered
        // force-run is the honest verb — it covers the failing project, so it may clear it.
        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = "-c \"if [ -f fail_flag ]; then exit 1; else exit 0; fi\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        File.WriteAllText(Path.Combine(tmpDir, "fail_flag"), "")
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ host.HasFailingReasons(warningsAreFailures = true) @>

        File.Delete(Path.Combine(tmpDir, "fail_flag"))
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        waitUntil (fun () -> not (host.HasFailingReasons(warningsAreFailures = true))) 12000

        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"an unfiltered re-run with every test passing must be green, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``RerunQueued path records previous run outcome to history before starting rerun`` () =
    withTempDir "tp-rerun-queued-history" (fun tmpDir ->
        // Run 1 sleeps ~1s and fails; the second BuildSucceeded arrives mid-run, putting
        // state into RerunQueued. That path used to drop the previous run's lifecycle
        // silently, so only the rerun's outcome ever reached history.
        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = "-c \"sleep 1; exit 1\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(Running _) -> true
                | _ -> false)
            5000

        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 12.0

        let history = host.GetHistory("test-prune")

        // Not an exact count: whether the rerun's no-op skip produces its own history
        // entry depends on scheduler timing.
        test <@ history.Length >= 1 @>

        let firstFailed =
            history
            |> List.exists (fun r ->
                match r.Outcome with
                | FailedRun _ -> true
                | _ -> false)

        test <@ firstFailed @>)

// ``PendingRerun storm: plugin reaches terminal state after BuildCompleted hammering
// subsides`` lives in FsHotWatch.IntegrationTests/TestPruneStormTests.fs: it asserts
// EVENTUAL settling under load, which is scheduler-dependent and flaked here.

// Inline FactAttribute so test detection works without xUnit assemblies in script options.
// Module-level [<Fact>] functions are the pattern analyzeSource reliably detects via FCS
// symbol uses without resolved assembly references.
let private testSource moduleName =
    $"""module {moduleName}

type FactAttribute() =
    inherit System.Attribute()

[<Fact(Timeout = 15000)>]
let myTest () = ()
"""

// Source with a prod function that a test can call to create a dependency edge.
let private testSourceWithDep moduleName =
    $"""module {moduleName}

type FactAttribute() =
    inherit System.Attribute()

let compute x = x + 1

[<Fact(Timeout = 15000)>]
let computeTest () =
    let _ = compute 1
    ()
"""

/// Emit a file through the CheckPipeline and wait for the plugin's async analysis to
/// settle, polling `changed-files` rather than sleeping.
let private emitFileAndWait
    (checker: FSharpChecker)
    (pipeline: CheckPipeline)
    (host: PluginHost)
    (filePath: string)
    (source: string)
    =
    async {
        File.WriteAllText(filePath, source)
        let! projOptions = getScriptOptions checker filePath source
        pipeline.RegisterProject(filePath, projOptions)
        let! result = pipeline.CheckFile(AbsFilePath.create filePath)

        match result with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith $"CheckFile returned None for {filePath}"

        let fileName = Path.GetFileName(filePath)

        waitUntil
            (fun () ->
                let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

                match result with
                | Some json -> json.Contains(fileName)
                | None -> false)
            10000
    }

// The 60s Fact cap is a cancellation guard, not a budget: tests that drive real FCS have
// a cold start that outruns the sum of their internal condition-based waits, and those
// waits still fail fast on a genuine hang. Same reasoning for every 60s cap in this file.
[<Fact(Timeout = 60000)>]
let ``FileChecked reports Completed when testConfigs provided (analysis done, awaiting build)`` () =
    withTempDir "tp-no-complete-real" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let testFile = Path.Combine(tmpDir, "MyTests.fsx")

        // Real FCS, to exercise the Ok analysisResult path.
        emitFileAndWait checker pipeline host testFile (testSource "MyTests")
        |> Async.RunSynchronously

        // Completed even with testConfigs, so WaitForComplete doesn't hang waiting for a
        // BuildCompleted that may never come.
        waitForPluginTerminal host "test-prune" 12.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Expected Completed after FileChecked analysis, got: %A{other}"))

[<Fact(Timeout = 60000)>]
let ``FileChecked reports Completed when no testConfigs (success path)`` () =
    withTempDir "tp-complete-real" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        // No testConfigs — analysis-only mode.
        let handler = create dbPath tmpDir None None None None None []
        host.RegisterHandler(handler)

        let testFile = Path.Combine(tmpDir, "MyLib.fsx")

        emitFileAndWait checker pipeline host testFile (testSource "MyLib")
        |> Async.RunSynchronously

        waitForPluginTerminal host "test-prune" 12.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Expected Completed in analysis-only mode, got: %A{other}"))

[<Fact(Timeout = 20000)>]
let ``after scan and build, test methods are in the sqlite database`` () =
    withTempDir "tp-tm-db" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")

        let testsSource =
            """module Tests

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let alpha () = ()

[<Fact>]
let beta () = ()
"""

        File.WriteAllText(testsFile, testsSource)

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let testConfigs =
            [ { Project = "MyTests"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None []

        host.RegisterHandler(handler)

        let projOptions =
            getScriptOptions checker testsFile testsSource |> Async.RunSynchronously

        pipeline.RegisterProject(testsFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create testsFile) |> Async.RunSynchronously

        match result with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "CheckFile returned None"

        waitForPluginIdle host "test-prune" 10.0

        let firstBuild = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        firstBuild.Wait(TimeSpan.FromSeconds 20.0) |> ignore

        // Cross-connection WAL visibility races the plugin's commit: a fresh connection
        // can momentarily observe an empty DB even though the plugin saw its own writes.
        // Hence the pool clear and the poll rather than a single read.
        let mutable testMethods: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let freshDb = Database.create dbPath
                testMethods <- freshDb.GetTestMethodsInFile "Tests.fsx"
                testMethods.Length >= 2)
            5000

        test <@ testMethods.Length = 2 @>
        test <@ testMethods |> List.exists (fun t -> t.TestMethod = "alpha") @>
        test <@ testMethods |> List.exists (fun t -> t.TestMethod = "beta") @>)

[<Fact(Timeout = 60000)>]
let ``after a symbol change, affected-tests identifies the dependent test`` () =
    withTempDir "tp-sym" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")

        let libSource1 =
            """module Lib
let compute (x: int) = x + 1
"""

        let testsSource =
            """module Tests
open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let computeTest () =
    let result = compute 1
    assert (result = 2)
"""

        File.WriteAllText(libFile, libSource1)
        File.WriteAllText(testsFile, testsSource)

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        // testConfigs is required for the plugin to subscribe to BuildCompleted; without
        // it flushAndQueryAffected never fires. The command itself is a no-op.
        let testConfigs =
            [ { Project = "Lib"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None []

        host.RegisterHandler(handler)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        emitBuildAndWaitTerminal host

        let libResult =
            pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously

        match libResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        // No waitUntil for `Lib.fsx` in ChangedFiles: the first detectChanges against an
        // empty stored sidecar is bypassed, so this check only primes the sidecar clean.
        let testsResult =
            pipeline.CheckFile(AbsFilePath.create testsFile) |> Async.RunSynchronously

        match testsResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        waitForPluginIdle host "test-prune" 10.0

        emitBatchAndQuiesce host [ libFile; testsFile ]

        // Modify compute's body — content hash changes but signature does not.
        let libSource2 =
            """module Lib
let compute (x: int) = x + 2
"""

        File.WriteAllText(libFile, libSource2)

        let libResult2 =
            pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously

        match libResult2 with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile 2 failed"

        let mutable affectedTests = ""

        waitUntil
            (fun () ->
                match host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously with
                | Some v -> affectedTests <- v
                | None -> ()

                affectedTests.Contains("computeTest"))
            5000

        test <@ affectedTests.Contains("computeTest") @>)

[<Fact(Timeout = 60000)>]
let ``cross-file type change only runs affected test classes`` () =
    withTempDir "tp-e2e" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")
        let captureFile = Path.Combine(tmpDir, "test-invocation.txt")

        // Requires bash: Unix/Linux only.
        let bashPath = Path.Combine(tmpDir, "test-wrapper.sh")

        try
            File.WriteAllText(bashPath, $"#!/bin/bash\necho \"$@\" >> '{captureFile}'\nexit 0\n")
        with ex ->
            failwith $"Failed to create test wrapper script: {ex.Message}"

        // Project name matches the registered project file's basename (stripping
        // .fsx if present), so "Lib.fsx" and a real "Lib.fsproj" both tag as "Lib".
        let testConfigs =
            [ { Project = "Lib"
                Command = "bash"
                Args = bashPath
                Group = "default"
                Environment = []
                FilterTemplate = Some "-- --filter-class {classes}"
                ClassJoin = "|"
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let handler = create dbPath tmpDir (Some testConfigs) None None None None []

        host.RegisterHandler(handler)

        let libSource =
            """module Lib

type Config = { Value: string; Count: int }

let validate (cfg: Config) = cfg.Value.Length > 0
"""

        let testsSource =
            """module Tests

open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let testValidateTrue () =
    let cfg = { Value = "hello"; Count = 5 }
    let result = validate cfg
    assert result

[<Fact>]
let testValidateFalse () =
    let cfg = { Value = ""; Count = 0 }
    let result = validate cfg
    assert (not result)

[<Fact>]
let testOtherStuff () =
    // This test doesn't use Config, so shouldn't be affected
    let x = 1 + 1
    assert (x = 2)
"""

        File.WriteAllText(libFile, libSource)
        File.WriteAllText(testsFile, testsSource)

        let libOptions =
            getScriptOptions checker libFile libSource |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        emitBuildAndWaitTerminal host

        let libResult =
            pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously

        match libResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        let testsResult =
            pipeline.CheckFile(AbsFilePath.create testsFile) |> Async.RunSynchronously

        match testsResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        waitForPluginIdle host "test-prune" 10.0

        // Flush, so the edited-file FileChecked below has stored rows to diff against.
        emitBatchAndQuiesce host [ libFile; testsFile ]

        let libSource2 =
            """module Lib

type Config = { Value: string; Count: int; Threshold: float }

let validate (cfg: Config) = cfg.Value.Length > 0
"""

        File.WriteAllText(libFile, libSource2)

        let libResult2 =
            pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously

        match libResult2 with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile 2 failed"

        let mutable affectedTests = ""

        waitUntil
            (fun () ->
                match host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously with
                | Some v -> affectedTests <- v
                | None -> ()

                affectedTests.Contains("testValidateTrue"))
            10000

        test <@ affectedTests.Contains("testValidateTrue") @>
        test <@ affectedTests.Contains("testValidateFalse") @>
        test <@ not (affectedTests.Contains("testOtherStuff")) @>

        emitBuildAndWaitTerminal host

        let capturedArgs =
            try
                File.ReadAllText(captureFile)
            with :? System.IO.FileNotFoundException ->
                failwith $"Test command did not execute or write to {captureFile}"

        test <@ capturedArgs.Contains("--filter-class") @>
        test <@ capturedArgs.Contains("Tests") @>)

// =============================================================================
// AUTOMATION-67 — seeded-workspace under-selection.
//
// A fresh jj workspace seeds `test-impact.db` from the default workspace (ADR-010) but
// NOT the freshness sidecar, which lives under `.fshw/`. Every seeded file therefore
// classified `storedClean = false`, the `detectChanges` call site bypassed the diff, and
// a real edit against seeded rows detected zero changed symbols → zero affected tests →
// vacuous green. The fix distinguishes an ABSENT sidecar record (a seeded DB, diffable)
// from an explicit dirty stamp (poisoned rows, still bypassed).
//
// Both tests seed the DB as the plugin's flush would, leave the sidecar absent, edit a
// covered symbol and assert the covering test is re-selected; pre-fix both get "[]".
// =============================================================================

/// Seed `dbPath` with the combined analysis of `libSource` + `testsSource`, mirroring
/// `flushPendingAnalysis`. Deliberately does NOT write the freshness sidecar — that
/// absence IS the fresh-workspace condition under test.
let private seedImpactDbNoSidecar
    (checker: FSharpChecker)
    (tmpDir: string)
    (dbPath: string)
    (libFile: string)
    (libSource: string)
    (testsFile: string)
    (testsSource: string)
    : FSharpProjectOptions =
    File.WriteAllText(libFile, libSource)
    File.WriteAllText(testsFile, testsSource)

    let libText = SourceText.ofString libSource

    let libOptions =
        checker.GetProjectOptionsFromScript(libFile, libText, assumeDotNetFramework = false)
        |> Async.RunSynchronously
        |> fst

    let projOptions =
        { libOptions with
            SourceFiles = [| libFile; testsFile |] }

    // Derived exactly as the plugin does, so the seeded TestProject label matches what
    // the live plugin will compute.
    let projectName =
        let raw = projOptions.ProjectFileName |> Path.GetFileNameWithoutExtension

        if raw.EndsWith(".fsx") then
            raw.Substring(0, raw.Length - 4)
        else
            raw

    let analyze file src =
        match analyzeSource checker file src projOptions projectName |> Async.RunSynchronously with
        | Ok r -> r
        | Error m -> failwith $"seed analyze failed for {file}: {m}"

    let libR = analyze libFile libSource
    let testsR = analyze testsFile testsSource

    let combined =
        { Symbols = normalizeSymbolPaths tmpDir (libR.Symbols @ testsR.Symbols)
          Dependencies = libR.Dependencies @ testsR.Dependencies
          TestMethods =
            (libR.TestMethods @ testsR.TestMethods)
            |> List.map (fun t -> { t with TestProject = projectName })
          Attributes = libR.Attributes @ testsR.Attributes
          ParentLinks = libR.ParentLinks @ testsR.ParentLinks
          Diagnostics = AnalysisDiagnostics.Zero }

    let db = Database.create dbPath
    db.RebuildProjects([ combined ])
    projOptions

/// Drive an edited `libFile` through a FRESH plugin (empty sidecar) over the seeded DB
/// and poll `affected-tests`, returning the raw JSON.
let private selectAfterSeededEdit
    (checker: FSharpChecker)
    (tmpDir: string)
    (dbPath: string)
    (projOptions: FSharpProjectOptions)
    (libFile: string)
    (libSourceEdited: string)
    (expectedTest: string)
    : string =
    // The fresh-workspace invariant: the seeded DB exists but the sidecar does not.
    test <@ not (File.Exists(FsHotWatch.TestPrune.FileFreshness.sidecarPath tmpDir)) @>

    let pipeline = CheckPipeline(checker)
    pipeline.RegisterProject(projOptions.ProjectFileName, projOptions)

    let host = PluginHost.create checker tmpDir
    let handler = create dbPath tmpDir None None None None None []
    host.RegisterHandler(handler)

    File.WriteAllText(libFile, libSourceEdited)

    match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
    | Some r -> host.EmitFileChecked(r)
    | None -> failwith "edited lib CheckFile failed"

    let mutable affected = ""

    waitUntil
        (fun () ->
            match host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously with
            | Some v -> affected <- v
            | None -> ()

            affected.Contains(expectedTest))
        10000

    affected

/// Drive the same edit through the cohort-complete flush before observing selection.
/// Literal coupling needs this stronger path: the flush replaces the producer's old
/// graph before it queries affected tests, which is where pre-rebuild evidence used to
/// disappear.
let private selectAfterSeededEditAndFlush
    (checker: FSharpChecker)
    (tmpDir: string)
    (dbPath: string)
    (projOptions: FSharpProjectOptions)
    (libFile: string)
    (libSourceEdited: string)
    : string =
    test <@ not (File.Exists(FsHotWatch.TestPrune.FileFreshness.sidecarPath tmpDir)) @>

    let pipeline = CheckPipeline(checker)
    pipeline.RegisterProject(projOptions.ProjectFileName, projOptions)

    let host = PluginHost.create checker tmpDir
    let handler = create dbPath tmpDir None None None None None []
    host.RegisterHandler(handler)

    File.WriteAllText(libFile, libSourceEdited)

    match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
    | Some result ->
        emitFileAndQuiesce host result
        emitBatchAndQuiesce host [ libFile ]
    | None -> failwith "edited lib CheckFile failed"

    host.RunCommand("affected-tests", [||])
    |> Async.RunSynchronously
    |> Option.defaultValue ""

[<Fact(Timeout = 60000)>]
let ``seeded DB + absent sidecar: string-literal change re-selects the asserting test (AUTOMATION-67 instance 1)`` () =
    withTempDir "tp-seed-logstring" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")
        let checker = sharedChecker.Value

        // Prod: a failure-path log helper. Test: asserts the exact logged string —
        // the LlmClient/Embeddings coupling shape.
        let libV1 =
            "module Lib\n\nlet auditFailureMessage (write: string -> unit) =\n    write \"audit-log write failed\"\n"

        let testsSrc =
            "module Tests\n\nopen Lib\n\ntype FactAttribute() =\n    inherit System.Attribute()\n\n[<Fact>]\nlet auditFailureLogsExpectedMessage () =\n    let mutable captured = \"\"\n    auditFailureMessage (fun s -> captured <- s)\n    assert (captured = \"audit-log write failed\")\n"

        let projOptions =
            seedImpactDbNoSidecar checker tmpDir dbPath libFile libV1 testsFile testsSrc

        // Only the log string changes. The signature is identical, so nothing but the
        // function-body content hash moves — and that must still re-select the test.
        let libV2 =
            "module Lib\n\nlet auditFailureMessage (write: string -> unit) =\n    write \"audit-log write threw\"\n"

        let affected =
            selectAfterSeededEdit checker tmpDir dbPath projOptions libFile libV2 "auditFailureLogsExpectedMessage"

        test <@ affected.Contains("auditFailureLogsExpectedMessage") @>)

[<Fact(Timeout = 60000)>]
let ``literal bridge survives the rebuild that observes a producer message change`` () =
    withTempDir "tp-literal-rebuild" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")
        let checker = sharedChecker.Value
        let oldMessage = "the audit log write failed and dropped the entry"
        let newMessage = "the audit outbox write failed and retained the entry"

        // No symbol edge joins these files: the test observes a boundary value and
        // asserts its old prose without calling or naming the producer.
        let libV1 = $"module Lib\n\nlet emit () = \"{oldMessage}\"\n"

        let testsSrc =
            $"module Tests\n\ntype FactAttribute() = inherit System.Attribute()\n\n[<Fact>]\nlet assertsObservedMessage () =\n    let observed = \"{oldMessage}\"\n    assert (observed = \"{oldMessage}\")\n"

        let projOptions =
            seedImpactDbNoSidecar checker tmpDir dbPath libFile libV1 testsFile testsSrc

        let libV2 = $"module Lib\n\nlet emit () = \"{newMessage}\"\n"

        let affected =
            selectAfterSeededEditAndFlush checker tmpDir dbPath projOptions libFile libV2

        test <@ affected.Contains("assertsObservedMessage") @>)

[<Fact(Timeout = 60000)>]
let ``seeded DB + absent sidecar: DU-list length change re-selects the count-asserting test (AUTOMATION-67 instance 2)``
    ()
    =
    withTempDir "tp-seed-ducount" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")
        let checker = sharedChecker.Value

        // Prod: a DU + a value enumerating its cases. Test: asserts the count —
        // the JobName.all.Length coupling shape.
        let libV1 =
            "module Lib\n\ntype JobName =\n    | Alpha\n    | Beta\n\nlet all = [ Alpha; Beta ]\n"

        let testsSrc =
            "module Tests\n\nopen Lib\n\ntype FactAttribute() =\n    inherit System.Attribute()\n\n[<Fact>]\nlet allHasExpectedCount () =\n    assert (List.length all = 2)\n"

        let projOptions =
            seedImpactDbNoSidecar checker tmpDir dbPath libFile libV1 testsFile testsSrc

        // Add a case and grow the enumerating list. `all`'s content hash changes,
        // so the test referencing it must be re-selected.
        let libV2 =
            "module Lib\n\ntype JobName =\n    | Alpha\n    | Beta\n    | Gamma\n\nlet all = [ Alpha; Beta; Gamma ]\n"

        let affected =
            selectAfterSeededEdit checker tmpDir dbPath projOptions libFile libV2 "allHasExpectedCount"

        test <@ affected.Contains("allHasExpectedCount") @>)

[<Fact(Timeout = 60000)>]
let ``WaitForComplete hangs when FileChecked arrives after BuildCompleted and tests finish`` () =
    withTempDir "tp-hang" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        let statusAfterTests = host.GetStatus("test-prune")
        test <@ statusAfterTests.IsSome @>

        match statusAfterTests.Value with
        | Completed _
        | Failed _ -> ()
        | other -> Assert.Fail($"Expected terminal after tests, got: %A{other}")

        // A late FileChecked: the FCS check completing after the build.
        let fakeFile = Path.Combine(tmpDir, "Late.fs")
        File.WriteAllText(fakeFile, "module Late\nlet x = 1\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Late\nlet x = 1\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitForSettled host "test-prune" 5000

        // Without the fix the plugin stays Running indefinitely after that FileChecked,
        // and this never resolves.
        let waitTask =
            waitForAllTerminal host (System.TimeSpan.FromSeconds(5.0)) System.Threading.CancellationToken.None

        let completed = waitTask.Wait(System.TimeSpan.FromSeconds(8.0))

        test <@ completed @>)

// The "nothing to verify" completion path. A cycle whose changed/queued symbols all prove
// to have no covering test must resolve as a clean green (0 ran) immediately, even on a
// cold daemon with no session baseline — rather than falling through to the cold-start
// full-suite run, which on a loaded box can wedge in executeTests and never resolve
// WaitForComplete.
[<Fact(Timeout = 30000)>]
let ``all changed symbols with no covering test complete green without running`` () =
    withTempDir "tp-nothing-to-verify" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        // Created only if the suite runs — and it must not.
        let sentinel = Path.Combine(tmpDir, "ran")

        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = $"-c \"touch {sentinel}\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        // The orphan must be INDEXED, not merely absent: only an index that knows the
        // symbol can prove it has no covering test. Against a DB that never heard of
        // `Orphan.uncovered`, this would assert the same green over "the index cannot
        // answer" — the silent-green bug, not this feature.
        let orphan: SymbolInfo =
            { FullName = "Orphan.uncovered"
              Kind = SymbolKind.Value
              SourceFile = "src/Orphan.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "orphan-hash"
              IsExtern = false }

        let seedDb = Database.create dbPath
        seedDb.RebuildProjects([ AnalysisResult.Create([ orphan ], [], []) ])

        // Positive control: indexed, and genuinely covered by nothing.
        test <@ (seedDb.QueryAffectedTests [ "Orphan.uncovered" ]).IsEmpty @>
        test <@ (seedDb.GetSymbolsInFile "src/Orphan.fs").Length = 1 @>

        // The plugin loads the queue at construction, so the first BuildCompleted flush
        // drops this symbol as uncovered, leaving an empty affected set.
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Orphan.uncovered" ])

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        // No prior run this session ⇒ hasCachedResults = false, which used to force the
        // cold-start branch into a FULL suite even though the only pending symbol is
        // untestable.
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let completion = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)

        // Bounded — a hang (the bug) fails here rather than passing.
        let reached = completion.Wait(TimeSpan.FromSeconds 15.0)
        test <@ reached @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"Expected Completed (nothing to verify), got: %A{other}")

        // The discriminator: zero tests ran.
        test <@ not (File.Exists sentinel) @>

        let waitTask =
            waitForAllTerminal host (TimeSpan.FromSeconds 5.0) System.Threading.CancellationToken.None

        test <@ waitTask.Wait(TimeSpan.FromSeconds 8.0) @>)

// The "nothing to verify" green above infers "no covering test" from an EMPTY
// `QueryAffectedTests` result. That is sound only when the symbol is KNOWN to the index;
// otherwise an empty result means "I have never heard of this name", which is not a fact
// about the symbol at all.
//
// A `SchemaVersion` bump makes the two differ: it deletes and recreates
// `test-impact.db` while the pending-verification sidecar beside it survives untouched,
// so every name the queue holds resolves to nothing. Real debt then reads as "provably
// uncovered", drops from the queue, and the cycle greens having run zero tests.
//
// Unlike the test above, `Lib.foo` here IS covered — only the recreate destroyed the
// evidence.
[<Fact(Timeout = 30000)>]
let ``a pending symbol orphaned by a DB recreate must not discharge as a zero-test green`` () =
    withTempDir "tp-recreate-orphan" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        // Touched IF any test runs. The bug is that nothing does.
        let sentinel = Path.Combine(tmpDir, "ran")

        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = $"-c \"touch {sentinel}\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        // A healthy index: `Lib.foo` exists and `Tests.myTest` in `TestProject` covers it.
        let symbol: SymbolInfo =
            { FullName = "Lib.foo"
              Kind = SymbolKind.Value
              SourceFile = "src/Lib.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "hash-v1"
              IsExtern = false }

        // The test method needs a `symbols` row of its own: `QueryAffectedTests` reaches
        // tests by joining `test_methods.symbol_id`, so a test method with no symbol row
        // is unreachable and every query returns empty.
        let testSymbol: SymbolInfo =
            { FullName = "Tests.myTest"
              Kind = SymbolKind.Value
              SourceFile = "tests/Tests.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "test-hash-v1"
              IsExtern = false }

        let testMethod: TestMethodInfo =
            { SymbolFullName = "Tests.myTest"
              TestProject = "TestProject"
              TestClass = "Tests"
              TestMethod = "myTest" }

        let db = Database.create dbPath

        db.RebuildProjects(
            [ AnalysisResult.Create(
                  [ symbol; testSymbol ],
                  [ { FromSymbol = "Tests.myTest"
                      ToSymbol = "Lib.foo"
                      Kind = DependencyKind.Calls
                      Source = "core" } ],
                  [ testMethod ]
              ) ]
        )

        // Positive control: while the index stands, `Lib.foo` IS covered, so an empty
        // result later is the recreate's doing and not a broken fixture. Without it an
        // unreachable test method would make the whole test pass vacuously.
        test <@ not (db.QueryAffectedTests [ "Lib.foo" ]).IsEmpty @>

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        // Stamp the incompatible version an older TestPrune.Core would have left, so the
        // plugin's own `Database.create` performs the REAL delete-and-recreate. Deleting
        // the file by hand does NOT work: Microsoft.Data.Sqlite pools connections, so a
        // later open is handed the deleted inode with its data intact and the test passes
        // against a database that was never recreated.
        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source=%s{dbPath}")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "PRAGMA user_version = 1;"
            cmd.ExecuteNonQuery() |> ignore

        // The next run opens a DB that has never heard of `Lib.foo`.
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // The paired control: the recreate really did wipe the index, so the plugin's
        // empty selection below is genuine.
        test <@ (Database.create dbPath).QueryAffectedTests([ "Lib.foo" ]).IsEmpty @>

        let completion = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)

        test <@ completion.Wait(TimeSpan.FromSeconds 20.0) @>

        // The recreate destroyed the record of what covers `Lib.foo`; it did not verify
        // it. Greening here would discharge real debt having executed nothing.
        test <@ File.Exists sentinel @>)

// The door a `WasRecreated` guard leaves open. The index here is healthy, populated and
// opened compatibly, but has never heard of the queued symbol — renamed or deleted while
// it sat in the queue. `QueryAffectedTests` answers empty exactly as it does for a symbol
// with genuinely no covering test, so the vacuous green is reachable through a healthy
// index. What licenses the shortcut is whether the index KNOWS the name, not how it came
// to be in its current state.
[<Fact(Timeout = 30000)>]
let ``a queued symbol the index has never heard of must not discharge as a zero-test green`` () =
    withTempDir "tp-unknown-symbol" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let sentinel = Path.Combine(tmpDir, "ran")

        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = $"-c \"touch {sentinel}\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        // `user_version` is never bumped, so the plugin's open below is a COMPATIBLE
        // REOPEN and `WasRecreated` is false.
        let liveSymbol: SymbolInfo =
            { FullName = "Lib.stillHere"
              Kind = SymbolKind.Value
              SourceFile = "src/Lib.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "hash-v1"
              IsExtern = false }

        let db = Database.create dbPath
        db.RebuildProjects([ AnalysisResult.Create([ liveSymbol ], [], []) ])

        // Positive controls both ways: the index IS populated, and it genuinely does not
        // know the queued name. Without these the test passes against an empty database.
        test <@ (db.GetAllSymbolNames()).Contains "Lib.stillHere" @>
        test <@ not ((db.GetAllSymbolNames()).Contains "Lib.renamedAway") @>

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.renamedAway" ])

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let completion = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)

        test <@ completion.Wait(TimeSpan.FromSeconds 20.0) @>

        // The index cannot vouch for `Lib.renamedAway`, so "no covering test" is not
        // proof about it and the run must actually happen.
        test <@ File.Exists sentinel @>)

// The poisoned-seed guard's decision, without a daemon. Each half of the conjunction gets
// a test that would pass if the OTHER half were deleted, so neither can rot into a
// tautology.

[<Fact>]
let ``a seed selecting the whole suite for one run is not yet a poison suspect`` () =
    // An expensive edit looks exactly like this for a run or two. Firing here is how
    // a warning gets tuned out before the run that matters.
    test <@ not (isPoisonSuspect 1 1000 1000) @>
    test <@ not (isPoisonSuspect (PoisonSeedRuns - 1) 1000 1000) @>

[<Fact>]
let ``a seed pinned for many runs but selecting narrowly is not a poison suspect`` () =
    // A symbol waiting on a slow fix is ordinary. Width is what makes a pin harmful.
    test <@ not (isPoisonSuspect 50 1000 10) @>

[<Fact>]
let ``a seed that is both pinned and dominant is a poison suspect`` () =
    // Exactly on the threshold: 250 of 1000 is the 25% share.
    test <@ isPoisonSuspect PoisonSeedRuns 1000 250 @>
    // The shape observed in the field: one mis-qualified symbol (`name`) taking the whole
    // run — 2,837 tests — every run.
    test <@ isPoisonSuspect 10 2837 2837 @>

[<Fact>]
let ``a run that selected nothing has no poison suspects`` () =
    // Guards the division and the premise: with no tests selected there is no
    // fraction to be a large part of, however long the symbol has been queued.
    test <@ not (isPoisonSuspect 100 0 0) @>

[<Fact>]
let ``seed ages count consecutive appearances only`` () =
    let afterFirst = bumpSeedAges Map.empty [ "A"; "B" ]
    test <@ afterFirst = Map.ofList [ "A", 1; "B", 1 ] @>

    // B does not seed this cycle, so it leaves the map entirely.
    let afterSecond = bumpSeedAges afterFirst [ "A" ]
    test <@ afterSecond = Map.ofList [ "A", 2 ] @>

    // B returns — and starts from one. Without this, a symbol that cleared and was
    // re-queued weeks later would inherit its old age and be accused immediately.
    let afterThird = bumpSeedAges afterSecond [ "A"; "B" ]
    test <@ afterThird = Map.ofList [ "A", 3; "B", 1 ] @>

[<Fact(Timeout = 15000)>]
let ``ParseOnly FileChecked fails closed and reports the file as unanalysable`` () =
    // ParseOnly has no trustworthy symbol graph. It must be a loud failure, not the old
    // silent "no changes" result. The resulting UnanalyzableFiles state drives the
    // full-suite fallback proved independently by the AUTOMATION-113 tests below.
    withTempDir "tp-parse-only" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir None None None None None []
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet foo = 2\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\nlet foo = 2\n" }

        host.EmitFileChecked(fakeResult)
        waitForPluginTerminal host "test-prune" 12.0

        match host.GetStatus("test-prune") with
        | Some(Failed(summary, _, _)) -> test <@ summary.Contains("Analysis failed") @>
        | other -> Assert.Fail($"Expected ParseOnly analysis to fail closed, got: %A{other}")

        let errors = host.GetErrorsByPlugin("test-prune")
        test <@ errors |> Map.containsKey fakeFile @>

        let entries = errors[fakeFile]
        test <@ entries.Length = 1 @>
        test <@ entries.Head.Message.Contains("full type-check results are required") @>)

[<Fact(Timeout = 60000)>]
let ``affected-tests computes lazily on demand from ChangedSymbols`` () =
    // FileChecked accumulates state.ChangedSymbols but does NOT eagerly
    // QueryAffectedTests; the IPC command runs the SQL on demand. Hence the deliberate
    // absence of a second BuildCompleted below — the answer must come from the lazy path.
    withTempDir "tp-lazy-affected" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")

        let libSource1 =
            """module Lib
let compute (x: int) = x + 1
"""

        let testsSource =
            """module Tests
open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let lazyComputeTest () =
    let result = compute 1
    assert (result = 2)
"""

        File.WriteAllText(libFile, libSource1)
        File.WriteAllText(testsFile, testsSource)

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let testConfigs =
            [ { Project = "Lib"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
        host.RegisterHandler(handler)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        emitBuildAndWaitTerminal host

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        match pipeline.CheckFile(AbsFilePath.create testsFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        waitForPluginIdle host "test-prune" 10.0

        emitBatchAndQuiesce host [ libFile; testsFile ]

        let libSource2 =
            """module Lib
let compute (x: int) = x + 2
"""

        File.WriteAllText(libFile, libSource2)

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile 2 failed"

        let mutable affectedTests = ""

        waitUntil
            (fun () ->
                match host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously with
                | Some v -> affectedTests <- v
                | None -> ()

                affectedTests.Contains("lazyComputeTest"))
            5000

        test <@ affectedTests.Contains("lazyComputeTest") @>)

[<Fact(Timeout = 15000)>]
let ``BuildCompleted queries affected tests after flush`` () =
    withTempDir "tp-query-after-flush" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let configs =
            [ { Project = "TestProj"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = Some "-- --filter-class {classes}"
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // No prior FileChecked, so AnalysisRan is false and affected-tests answers
        // "not analyzed" rather than throwing.
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``skip tests when 0 affected classes and not cold start`` () =
    withTempDir "tp-skip" (fun tmpDir ->
        let mutable runCount = 0

        let configs =
            [ { Project = "TestProj"
                Command = "echo"
                Args = "ran"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) None None (Some(fun _ -> runCount <- runCount + 1)) None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ runCount = 1 @>

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ runCount = 1 @>) // skipped: no changed symbols

// ── Dependency-fanout (DependencyFanout + PluginCtx.ProjectGraph) ─────────────
// A dependency/PackageReference change flips a test project's dependency fingerprint (its
// referenced-project DLL content) without changing any F# symbol, so it must force-run
// that project past the zero-affected skip gate — while a build with no dependency change
// still skips.

/// Emit a BuildSucceeded and fully serialize: catch THIS build's terminal transition, then
/// wait for idle so the async run (and any rerun it queues) has drained. The fanout tests
/// need each build's fingerprint comparison to see the prior build's committed state, not
/// a half-applied pipelined one.
let private emitBuildAndSettle (host: PluginHost) =
    let await = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    await.Wait(TimeSpan.FromSeconds 15.0) |> ignore
    waitForSettled host "test-prune" 15000

/// A fake project graph for a single test project `TestProj` that references one
/// library project whose compiled DLL is `opsDllPath`. The test mutates that
/// file's content to flip `TestProj`'s dependency fingerprint.
let private fanoutGraph (testProjFsproj: string) (opsFsproj: string) (opsDllPath: string) =
    { FsHotWatch.PluginFramework.ProjectGraphAccessor.none with
        GetAllProjects = fun () -> [ testProjFsproj; opsFsproj ]
        GetProjectReferences =
            fun p ->
                if p = testProjFsproj then [ opsFsproj ]
                elif p = opsFsproj then []
                else []
        GetCanonicalDllPath = fun p -> if p = opsFsproj then Some opsDllPath else None }

[<Fact(Timeout = 20000)>]
let ``dependency-fingerprint change force-runs the dependent test project`` () =
    withTempDir "tp-fanout" (fun tmpDir ->
        let mutable runCount = 0

        let testProjFsproj = Path.Combine(tmpDir, "TestProj", "TestProj.fsproj")
        let opsFsproj = Path.Combine(tmpDir, "Ops", "Ops.fsproj")
        let opsDll = Path.Combine(tmpDir, "Ops", "bin", "Debug", "net10.0", "Ops.dll")
        Directory.CreateDirectory(Path.GetDirectoryName testProjFsproj) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName opsDll) |> ignore
        File.WriteAllText(testProjFsproj, "<Project></Project>")
        File.WriteAllText(opsFsproj, "<Project></Project>")
        // Initial referenced DLL (as if built against CommandTree 0.6.3).
        File.WriteAllBytes(opsDll, Text.Encoding.UTF8.GetBytes "ops-binary-v063")

        let configs =
            [ { Project = "TestProj"
                Command = "echo"
                Args = "ran"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        // Wire the fake graph BEFORE registering the plugin (mirrors the daemon).
        host.SetProjectGraph(fanoutGraph testProjFsproj opsFsproj opsDll)

        let handler =
            create ":memory:" tmpDir (Some configs) None None (Some(fun _ -> runCount <- runCount + 1)) None []

        host.RegisterHandler(handler)

        // Cold start records the baseline fingerprint.
        emitBuildAndSettle host
        test <@ runCount = 1 @>

        emitBuildAndSettle host
        test <@ runCount = 1 @>

        // Ops rebuilt against a new CommandTree: no F# symbol in TestProj changed, but its
        // dependency fingerprint flips.
        File.WriteAllBytes(opsDll, Text.Encoding.UTF8.GetBytes "ops-binary-v070-DIFFERENT")
        emitBuildAndSettle host
        test <@ runCount = 2 @>)

[<Fact(Timeout = 20000)>]
let ``no dependency change and no symbol change still skips (no regression)`` () =
    // Same harness as the fanout test, with the referenced DLL held constant.
    withTempDir "tp-fanout-noregress" (fun tmpDir ->
        let mutable runCount = 0

        let testProjFsproj = Path.Combine(tmpDir, "TestProj", "TestProj.fsproj")
        let opsFsproj = Path.Combine(tmpDir, "Ops", "Ops.fsproj")
        let opsDll = Path.Combine(tmpDir, "Ops", "bin", "Debug", "net10.0", "Ops.dll")
        Directory.CreateDirectory(Path.GetDirectoryName testProjFsproj) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName opsDll) |> ignore
        File.WriteAllText(testProjFsproj, "<Project></Project>")
        File.WriteAllText(opsFsproj, "<Project></Project>")
        File.WriteAllBytes(opsDll, Text.Encoding.UTF8.GetBytes "ops-binary-stable")

        let configs =
            [ { Project = "TestProj"
                Command = "echo"
                Args = "ran"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        host.SetProjectGraph(fanoutGraph testProjFsproj opsFsproj opsDll)

        let handler =
            create ":memory:" tmpDir (Some configs) None None (Some(fun _ -> runCount <- runCount + 1)) None []

        host.RegisterHandler(handler)

        emitBuildAndSettle host
        test <@ runCount = 1 @>

        emitBuildAndSettle host
        emitBuildAndSettle host
        test <@ runCount = 1 @>)

[<Fact(Timeout = 15000)>]
let ``comment-only change does not add file to ChangedFiles but AST change does`` () =
    // `newChangedFiles` used to be computed before `changedNames`, so any emit — even a
    // comment-only one — added the file to ChangedFiles and triggered extension-based
    // tests (Falco routes and the like).
    let initialSource = "module Lib\nlet x = 1\n"
    let commentOnlySource = "module Lib\n// a comment added\nlet x = 1\n"
    let astChangedSource = "module Lib\nlet x = 1\nlet y = 2\n"

    withSeededTestEnv "tp-comment-regression" "Lib.fs" initialSource (fun env ->
        File.WriteAllText(env.FilePath, commentOnlySource)

        match
            env.Pipeline.CheckFile(AbsFilePath.create env.FilePath)
            |> Async.RunSynchronously
        with
        | None -> Assert.Fail("FCS failed to check comment-only source")
        | Some result -> env.Host.EmitFileChecked(result)

        waitForTerminalStatus env.Host "test-prune" 30000

        let changedAfterComment =
            env.Host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedAfterComment.Value = "[]" @>

        File.WriteAllText(env.FilePath, astChangedSource)

        match
            env.Pipeline.CheckFile(AbsFilePath.create env.FilePath)
            |> Async.RunSynchronously
        with
        | None -> Assert.Fail("FCS failed to check AST-changed source")
        | Some result -> env.Host.EmitFileChecked(result)

        waitForTerminalStatus env.Host "test-prune" 30000

        let changedAfterAst =
            env.Host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedAfterAst.Value.Contains(env.RelPath) @>)

// --- formatFailureReport: CI observability for a red test run ---
//
// A failed run once reported "0 test(s) failed:" with NO test name in the CI console —
// undiagnosable, because the per-test detail only lived in the on-disk `.fshw/test-runs`
// log that CI discards. The matcher must surface the failing test names robustly, and
// when nothing parses it must dump the output tail rather than swallow it.

[<Fact(Timeout = 15000)>]
let ``formatFailureReport surfaces a plain failed-test line`` () =
    let output =
        "Discovering: probe\nfailed FsHotWatch.Tests.Foo.bar (32ms)\nTest run summary: Failed!\n  total: 1\n  failed: 1\n  succeeded: 0"

    let report =
        formatFailureReport "FsHotWatch.Tests" savedLog output |> String.concat "\n"

    test <@ report.Contains("FsHotWatch.Tests.Foo.bar") @>
    test <@ report.Contains("1 test(s) failed") @>

[<Fact(Timeout = 15000)>]
let ``formatFailureReport surfaces a timed-out (canceled) test — the daemon-load flake`` () =
    // MTP prints a `[<Fact(Timeout=...)>]` cancellation as `failed (canceled) <name>` —
    // the documented under-load flake, whose name must still reach the console.
    let output =
        "failed (canceled) FsHotWatch.Tests.Slow.thing (118ms)\n  Test execution timed out after 100 milliseconds\n  total: 1\n  failed: 1"

    let report =
        formatFailureReport "FsHotWatch.Tests" savedLog output |> String.concat "\n"

    test <@ report.Contains("FsHotWatch.Tests.Slow.thing") @>
    test <@ report.Contains("(canceled)") @>
    test <@ report.Contains("1 test(s) failed") @>

[<Fact(Timeout = 15000)>]
let ``formatFailureReport matches a failed line with leading whitespace`` () =
    // The CI gap exactly: some MTP/capture paths indent the failed line, and an untrimmed
    // `StartsWith("failed ")` silently missed it.
    let output =
        "    failed FsHotWatch.Tests.Indented.case (5ms)\n  total: 1\n  failed: 1"

    let report =
        formatFailureReport "FsHotWatch.Tests" savedLog output |> String.concat "\n"

    test <@ report.Contains("1 test(s) failed") @>
    test <@ report.Contains("FsHotWatch.Tests.Indented.case") @>

[<Fact(Timeout = 15000)>]
let ``formatFailureReport dumps the output tail when no failed line parses (backstop)`` () =
    // A crash / OOM-kill / unrecognised format yields a non-zero run with no `failed `
    // line, and reporting "0 test(s) failed" there hides the cause entirely.
    let output =
        "Building...\nUnhandled exception: System.AccessViolationException\n  at Some.Native.Frame()\nProcess terminated."

    let report =
        formatFailureReport "FsHotWatch.Tests" savedLog output |> String.concat "\n"

    test <@ report.Contains("0 test(s) failed") @>
    test <@ report.Contains("no per-test 'failed' line was parsed") @>
    // The actual cause IS surfaced (not swallowed).
    test <@ report.Contains("AccessViolationException") @>

// --- AUTOMATION-279: the backstop message must name a log that EXISTS ---
//
// The message told its reader the failure was visible "without the saved log" — and
// there was no saved log. The plugin's only `File.WriteAllText` wrote Cobertura coverage,
// so the line pointed at a file no code had ever written.

[<Fact(Timeout = 15000)>]
let ``formatFailureReport names the run log it was actually given`` () =
    let output = "Building...\nProcess terminated."

    let path = "/repo/.fshw/test-runs/abc123/Intelligence.Tests.Integration.output.log"

    let report =
        formatFailureReport "Intelligence.Tests.Integration" (FsHotWatch.RunLog.Ref.Written path) output
        |> String.concat "\n"

    test <@ report.Contains(path) @>
    test <@ not (report.Contains("without the saved log")) @>

[<Fact(Timeout = 15000)>]
let ``formatFailureReport states WHY there is no log rather than naming one`` () =
    // A path is printed only when something opened it. When the open failed the message
    // says so — no plausible-looking fallback path, and no silence either.
    let output = "Building...\nProcess terminated."

    let report =
        formatFailureReport "FsHotWatch.Tests" (FsHotWatch.RunLog.Ref.Unavailable "disk full") output
        |> String.concat "\n"

    test <@ report.Contains("NO output log was saved") @>
    test <@ report.Contains("disk full") @>
    test <@ not (report.Contains(".output.log")) @>
    // The tail is still dumped — this arm loses the head, not the summary.
    test <@ report.Contains("Process terminated") @>

[<Fact(Timeout = 15000)>]
let ``the console tail cannot reach the head — which is why the log exists`` () =
    // The cause is line 1; the other 59 are the repeated startup logging that filled all
    // forty lines of the real tail. A fixed tail structurally cannot reach a head — that
    // is why the file is worth writing, and this is the positive control for the message
    // tests above.
    let output =
        [ "Test shard pool 'intelligence_test_9f80_integration' is already in use by PID 18024"
          yield! List.replicate 59 "Applying migration 20250714_AddThing" ]
        |> String.concat "\n"

    let report =
        formatFailureReport "Intelligence.Tests.Integration" savedLog output
        |> String.concat "\n"

    test <@ not (report.Contains("already in use by PID 18024")) @>
    // ...so the message had better point at something that does contain it.
    test <@ report.Contains(".output.log") @>

// --- isZeroTestsUnderFilter ---
//
// `test-rerun --filter-class X` fans a raw passthrough filter out to EVERY test project,
// and every project without a matching test was reported "failed": the runner exits
// non-zero (MTP exit code 8 / "Zero tests ran") and that read as a failure.

[<Fact(Timeout = 15000)>]
let ``isZeroTestsUnderFilter true for filtered run with MTP zero-tests exit code`` () =
    let outcome =
        FsHotWatch.ProcessHelper.ProcessOutcome.Failed(
            zeroTestsExitCode,
            FsHotWatch.ProcessHelper.ProcessOutput.Drained "Test run summary: Zero tests ran"
        )

    test <@ isZeroTestsUnderFilter true outcome @>

[<Fact(Timeout = 15000)>]
let ``isZeroTestsUnderFilter true for filtered run whose output reports zero tests (non-8 exit)`` () =
    // A runner that exits non-zero without the canonical code 8 but still prints MTP's
    // zero-tests summary line.
    let outcome =
        FsHotWatch.ProcessHelper.ProcessOutcome.Failed(
            1,
            FsHotWatch.ProcessHelper.ProcessOutput.Drained
                "...\nZero tests ran - Foo.Tests.dll (net10.0|arm64)\n  total: 0"
        )

    test <@ isZeroTestsUnderFilter true outcome @>

[<Fact(Timeout = 15000)>]
let ``isZeroTestsUnderFilter false for UNFILTERED run even with zero-tests exit`` () =
    // An unfiltered project that runs zero tests is a real problem (empty suite,
    // misconfigured runner) and must still surface — not be silently skipped.
    let outcome =
        FsHotWatch.ProcessHelper.ProcessOutcome.Failed(
            zeroTestsExitCode,
            FsHotWatch.ProcessHelper.ProcessOutput.Drained "Zero tests ran"
        )

    test <@ not (isZeroTestsUnderFilter false outcome) @>

[<Fact(Timeout = 15000)>]
let ``isZeroTestsUnderFilter false for a genuine test failure under filter`` () =
    let outcome =
        FsHotWatch.ProcessHelper.ProcessOutcome.Failed(
            2,
            FsHotWatch.ProcessHelper.ProcessOutput.Drained
                "failed Foo.Bar\nTest run summary: Failed!\n  total: 3\n  failed: 1\n  succeeded: 2"
        )

    test <@ not (isZeroTestsUnderFilter true outcome) @>

[<Fact(Timeout = 15000)>]
let ``isZeroTestsUnderFilter false for a passing filtered run`` () =
    let outcome =
        FsHotWatch.ProcessHelper.ProcessOutcome.Succeeded(
            FsHotWatch.ProcessHelper.ProcessOutput.Drained
                "Test run summary: Passed!\n  total: 4\n  failed: 0\n  succeeded: 4"
        )

    test <@ not (isZeroTestsUnderFilter true outcome) @>

// =============================================================================
// `test-rerun` is the explicit force-rerun verb. Two defects it had:
//   (a) a filtered run that matched NOTHING was recorded as a (filtered) PASS,
//       indistinguishable from a real green — so "test-rerun didn't actually run" was
//       invisible.
//   (b) it returned an INSTANT non-result ("tests already running") when a background run
//       held the test slot: no execution, no log.
// =============================================================================

[<Fact(Timeout = 10000)>]
let ``isNoMatch / allZeroMatch detect the zero-match case`` () =
    let zero = TestsNoMatch("Zero tests ran", TimeSpan.Zero)
    let realPass = TestsPassed("Passed! total: 4", true, TimeSpan.Zero)
    let failed = TestsFailed("boom", true, TimeSpan.Zero)

    test <@ TestResult.isNoMatch zero @>
    test <@ not (TestResult.isNoMatch realPass) @>
    test <@ not (TestResult.isNoMatch failed) @>

    // allZeroMatch is true only when EVERY project is a zero-match.
    let allZero =
        { Results = Map.ofList [ "A", zero; "B", zero ]
          Elapsed = TimeSpan.Zero }

    let mixed =
        { Results = Map.ofList [ "A", zero; "B", realPass ]
          Elapsed = TimeSpan.Zero }

    let emptyRun =
        { Results = Map.empty
          Elapsed = TimeSpan.Zero }

    test <@ allZeroMatch allZero @>
    test <@ not (allZeroMatch mixed) @>
    test <@ not (allZeroMatch emptyRun) @>

[<Fact(Timeout = 15000)>]
let ``run-tests with a filter that matches nothing reports no-tests-matched distinctly, not a generic pass`` () =
    withTempDir "tp-run-nomatch" (fun tmpDir ->
        // `exit 8` is Microsoft Testing Platform's zero-tests exit for a filtered project
        // with no matching test.
        let configs =
            [ { Project = "NoMatchProj"
                Command = "sh"
                Args = "-c \"exit 8\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let result =
            host.RunCommand("run-tests", [| """{"filter": "--filter-class *NoSuchClass*"}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        test <@ doc.RootElement.GetProperty("noTestsMatched").GetBoolean() @>
        let projects = doc.RootElement.GetProperty("projects")
        Assert.Equal("no-tests-matched", projects.[0].GetProperty("status").GetString()))

// AUTOMATION-227/272 — the PRODUCER side of the two facts the CLI's refusal now prints.
//
// Deliberately end-to-end through `RunCommand "run-tests"`, not a unit test of the
// formatter: a consumer test asserting fields the producer never writes vouches for a
// payload that does not occur. (This suite had exactly that — a `verifiedNothing` boolean
// asserted in four IpcOutput fixtures and emitted by nobody.)
[<Fact(Timeout = 20000)>]
let ``run-tests puts the ACTIVE FILTER and the per-project TEST COUNTS on the wire`` () =
    withTempDir "tp-run-wire" (fun tmpDir ->
        // The runner is handed `--results-directory <runDir>` among its extra args; the
        // plugin creates that directory before launching. Writing a real CTRF report into
        // it is what a real xUnit.v3 runner does, and it is the only route by which counts
        // reach the reply.
        //
        // A script FILE rather than `sh -c "…"`: the args string is tokenized on
        // whitespace by the process layer, so an inline script is not one argument.
        let reportPath = Path.Combine(tmpDir, "canned.ctrf.json")

        File.WriteAllText(
            reportPath,
            """{"results":{"summary":{"tests":7,"passed":6,"failed":0,"pending":0,"skipped":1,"other":0}}}"""
        )

        let scriptPath = Path.Combine(tmpDir, "fake-runner.sh")
        let capturedArgs = Path.Combine(tmpDir, "captured-args")

        File.WriteAllText(
            scriptPath,
            "printf '%s\\n' \"$@\" > \""
            + capturedArgs
            + "\"\nfor a in \"$@\"; do\n"
            + "  if [ -d \"$a\" ]; then cp \""
            + reportPath
            + "\" \"$a/CtrfProj.ctrf.json\"; fi\n"
            + "done\nexit 0\n"
        )

        let configs =
            [ { Project = "CtrfProj"
                Command = "sh"
                Args = scriptPath
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                // `Ctrf`, not `AutoDetect`: this custom runner has no project assets from
                // which AutoDetect could learn its flag family.
                ReportVerificationFormat = Ctrf } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let result =
            host.RunCommand("run-tests", [| """{"filter": "--filter-class CtrfProjTests"}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)

        // The filter, quoted back exactly as given.
        Assert.Equal("--filter-class CtrfProjTests", doc.RootElement.GetProperty("filter").GetString())

        let project = doc.RootElement.GetProperty("projects").[0]
        Assert.Equal("passed", project.GetProperty("status").GetString())

        let counts = project.GetProperty("counts")
        Assert.Equal(7, counts.GetProperty("total").GetInt32())
        Assert.Equal(6, counts.GetProperty("succeeded").GetInt32())
        Assert.Equal(0, counts.GetProperty("failed").GetInt32())
        Assert.Equal(1, counts.GetProperty("skipped").GetInt32())

        // Force-on remains useful for custom runners without an assets graph. Its
        // documented fallback is the established xUnit 3 switch family.
        let args = File.ReadAllLines capturedArgs
        test <@ args |> Array.contains "--report-ctrf" @>
        test <@ args |> Array.contains "--report-ctrf-filename" @>
        test <@ not (args |> Array.contains "--report-xunit-ctrf") @>)

[<Fact(Timeout = 15000)>]
let ``run-tests emits a NULL counts field for a project that wrote no report`` () =
    withTempDir "tp-run-nocounts" (fun tmpDir ->
        // The control for the test above, and the honest half of the contract: no report
        // means no counts, never zeros. `total: 0, failed: 0` reads as a suite that ran
        // cleanly.
        let configs =
            [ { Project = "NoReportProj"
                Command = "echo"
                Args = "ran"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = Disabled } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously

        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)

        // No filter was given, so the field is present and null — an absent field would be
        // indistinguishable from an older daemon that cannot report one.
        let filterKind = doc.RootElement.GetProperty("filter").ValueKind
        Assert.Equal(JsonValueKind.Null, filterKind)

        let project = doc.RootElement.GetProperty("projects").[0]
        let countsKind = project.GetProperty("counts").ValueKind
        Assert.Equal(JsonValueKind.Null, countsKind))

[<Fact(Timeout = 15000)>]
let ``run-tests with a filter that matches tests executes and reports a real pass (not no-tests-matched)`` () =
    withTempDir "tp-run-match" (fun tmpDir ->
        // `echo` exits 0 → a real (filtered) pass, NOT a zero-match.
        let configs =
            [ { Project = "MatchProj"
                Command = "echo"
                Args = "ran"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let result =
            host.RunCommand("run-tests", [| """{"filter": "--filter-class MatchProjTests"}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        test <@ not (doc.RootElement.GetProperty("noTestsMatched").GetBoolean()) @>
        let projects = doc.RootElement.GetProperty("projects")
        Assert.Equal("passed", projects.[0].GetProperty("status").GetString()))

[<Fact(Timeout = 30000)>]
let ``run-tests force-executes after an in-flight run finishes instead of instantly bailing`` () =
    // An explicit force-rerun always runs: it waits for the `tests` slot rather than
    // bailing instantly with {error="tests already running"}.
    withTempDir "tp-run-force" (fun tmpDir ->
        let configs =
            [ { Project = "SlowProj"
                // ~1.5s so the slot is genuinely held when run-tests arrives.
                Command = "sh"
                Args = "-c \"sleep 1.5\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // Kick off the background run, which holds RunExclusive "tests".
        host.EmitBuildCompleted(BuildSucceeded)

        // Give it a moment to acquire the slot before forcing a rerun into it.
        Thread.Sleep(300)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously

        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        // A `projects` array means executeTests produced real results. `fst
        // (TryGetProperty …)` is computed OUTSIDE the quotation because the ValueTuple it
        // returns cannot appear inside one.
        let hasError = fst (doc.RootElement.TryGetProperty("error"))
        let hasProjects = fst (doc.RootElement.TryGetProperty("projects"))
        test <@ not hasError @>
        test <@ hasProjects @>
        let projects = doc.RootElement.GetProperty("projects")
        Assert.True(projects.GetArrayLength() > 0))

// --- parseRunTestsWaitMs (run-tests slot-wait budget, AUTOMATION-66) ---

[<Fact(Timeout = 15000)>]
let ``parseRunTestsWaitMs reads waitSec and converts to milliseconds`` () =
    test <@ parseRunTestsWaitMs """{"waitSec":300}""" DefaultRunTestsWaitMs = 300_000 @>

[<Fact(Timeout = 15000)>]
let ``parseRunTestsWaitMs falls back when waitSec is absent`` () =
    test <@ parseRunTestsWaitMs """{"filter":"--filter-class *Foo*"}""" DefaultRunTestsWaitMs = DefaultRunTestsWaitMs @>
    test <@ parseRunTestsWaitMs "{}" DefaultRunTestsWaitMs = DefaultRunTestsWaitMs @>

[<Fact(Timeout = 15000)>]
let ``parseRunTestsWaitMs falls back on malformed or non-numeric or non-positive input`` () =
    test <@ parseRunTestsWaitMs "not json" 123 = 123 @>
    test <@ parseRunTestsWaitMs """{"waitSec":"lots"}""" 123 = 123 @>
    test <@ parseRunTestsWaitMs """{"waitSec":0}""" 123 = 123 @>
    test <@ parseRunTestsWaitMs """{"waitSec":-5}""" 123 = 123 @>

[<Fact(Timeout = 15000)>]
let ``DefaultRunTestsWaitMs is well above the old fixed 120s`` () =
    // The wait must outlast a ~90s+ beforeRun chain a prior in-flight run is executing.
    test <@ DefaultRunTestsWaitMs > 120_000 @>

// --- buildFilterArgs unit tests ---

[<Fact(Timeout = 15000)>]
let ``buildFilterArgs returns None when no classes for project`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = "|"
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    let result = buildFilterArgs config Map.empty
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``buildFilterArgs returns None when no FilterTemplate configured`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = "|"
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    let classesByProject = Map.ofList [ "TestProj", [ "TestClassA"; "TestClassB" ] ]
    let result = buildFilterArgs config classesByProject
    test <@ result = None @>

[<Fact(Timeout = 15000)>]
let ``buildFilterArgs applies template with ClassJoin`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = "|"
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    let classesByProject = Map.ofList [ "TestProj", [ "ClassA"; "ClassB" ] ]
    let result = buildFilterArgs config classesByProject
    test <@ result = Some "-- --filter-class ClassA|ClassB" @>

[<Fact(Timeout = 15000)>]
let ``buildFilterArgs applies template with default space join`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = " "
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    let classesByProject = Map.ofList [ "TestProj", [ "ClassA"; "ClassB" ] ]
    let result = buildFilterArgs config classesByProject
    test <@ result = Some "-- --filter-class ClassA ClassB" @>

[<Fact(Timeout = 15000)>]
let ``buildFilterArgs ignores classes from other projects`` () =
    let config =
        { Project = "TestProjA"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = "|"
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    let classesByProject =
        Map.ofList [ "TestProjA", [ "ClassA" ]; "TestProjB", [ "ClassB" ] ]

    let result = buildFilterArgs config classesByProject
    test <@ result = Some "-- --filter-class ClassA" @>

// --- Schema-drift recovery ---

[<Fact(Timeout = 2000)>]
let ``looksLikeSchemaDrift matches SQLite "no such column" wording`` () =
    let ex = exn "SQLite Error 1: 'no such column: foo'."
    test <@ looksLikeSchemaDrift ex @>

[<Fact(Timeout = 2000)>]
let ``looksLikeSchemaDrift matches "no column named" wording`` () =
    let ex = exn "table projects has no column named source"
    test <@ looksLikeSchemaDrift ex @>

[<Fact(Timeout = 2000)>]
let ``looksLikeSchemaDrift matches regardless of case`` () =
    let ex = exn "NO SUCH COLUMN: X"
    test <@ looksLikeSchemaDrift ex @>

[<Fact(Timeout = 2000)>]
let ``looksLikeSchemaDrift rejects unrelated errors`` () =
    test <@ not (looksLikeSchemaDrift (exn "connection refused")) @>
    test <@ not (looksLikeSchemaDrift (exn "database is locked")) @>
    test <@ not (looksLikeSchemaDrift (exn "")) @>

[<Fact(Timeout = 2000)>]
let ``tryRepairSchemaDrift deletes the DB when the error looks like schema drift`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-repair-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    let dbPath = Path.Combine(tmpDir, "testprune.db")
    File.WriteAllText(dbPath, "stale-cache-contents")

    try
        tryRepairSchemaDrift dbPath (exn "SQLite Error 1: 'no such column: source'")

        test <@ not (File.Exists dbPath) @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 2000)>]
let ``tryRepairSchemaDrift deletes WAL and SHM sidecars alongside the main DB`` () =
    // SQLite in WAL mode ties the sidecars to the main file: opening a fresh (empty) main
    // DB alongside stale -wal/-shm yields a 0-byte main DB with garbage recovery state,
    // and subsequent inserts hit "no such column: parent_symbol_id" because the schema
    // DDL never fully applied. Observed in production after a drift-recovery pass.
    let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-repair-wal-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    let dbPath = Path.Combine(tmpDir, "testprune.db")
    let walPath = dbPath + "-wal"
    let shmPath = dbPath + "-shm"

    File.WriteAllText(dbPath, "stale-main")
    File.WriteAllText(walPath, "stale-wal-entries")
    File.WriteAllText(shmPath, "stale-shm-header")

    try
        tryRepairSchemaDrift dbPath (exn "SQLite Error 1: 'no such column: parent_symbol_id'")

        test <@ not (File.Exists dbPath) @>
        test <@ not (File.Exists walPath) @>
        test <@ not (File.Exists shmPath) @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 2000)>]
let ``tryRepairSchemaDrift leaves the DB alone for unrelated errors`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"tp-repair-noop-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore
    let dbPath = Path.Combine(tmpDir, "testprune.db")
    File.WriteAllText(dbPath, "healthy-cache")

    try
        tryRepairSchemaDrift dbPath (exn "database is locked")

        test <@ File.Exists dbPath @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact(Timeout = 2000)>]
let ``tryRepairSchemaDrift is a no-op when the DB file is already gone`` () =
    let missingPath =
        Path.Combine(Path.GetTempPath(), $"tp-missing-{Guid.NewGuid():N}.db")

    tryRepairSchemaDrift missingPath (exn "no such column: source")
    test <@ not (File.Exists missingPath) @>

// A group that completes quickly emits a partial snapshot even while a slower group is
// still running. Without it, downstream TestCompleted subscribers (afterTests triggers and
// the like) are blocked by the slowest test project.

[<Fact(Timeout = 30000)>]
let ``executeTests emits a TestProgress per group as groups finish`` () =
    withTempDir "tp-progressive" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getEvents, recorder) = testProgressRecorder ()
        host.RegisterHandler(recorder)

        // Two near-instant groups and one slow one: if executeTests emits only once at
        // batch end this sees one event instead of three. runProcess tokenises args
        // space-separated with no shell, hence the single-binary commands.
        let configs =
            [ { Project = "ProjFastA"
                Command = "echo"
                Args = "a"
                Group = "fast-a"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect }
              { Project = "ProjFastB"
                Command = "echo"
                Args = "b"
                Group = "fast-b"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect }
              { Project = "ProjSlow"
                Command = "sleep"
                Args = "2"
                Group = "slow"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")

        let handler = create dbPath tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil (fun () -> getEvents () |> List.length >= 3) 20000

        let events = getEvents ()
        test <@ events.Length = 3 @>

        let runIds = events |> List.map (fun p -> p.RunId) |> List.distinct
        test <@ runIds.Length = 1 @>

        let allProjects =
            events
            |> List.collect (fun p -> p.NewResults |> Map.toList |> List.map fst)
            |> Set.ofList

        test <@ allProjects = Set.ofList [ "ProjFastA"; "ProjFastB"; "ProjSlow" ] @>

        // Deltas, not cumulative snapshots: the invariant is an ordering, so the slow
        // group appears in the LAST emission and in no earlier one.
        let lastEvent = events |> List.last

        test <@ lastEvent.NewResults |> Map.containsKey "ProjSlow" @>

        let earlierEvents = events |> List.take 2

        test
            <@
                earlierEvents
                |> List.forall (fun p -> not (p.NewResults |> Map.containsKey "ProjSlow"))
            @>)

[<Fact(Timeout = 15000)>]
let ``full run (no filter) produces TestResult with WasFiltered = false`` () =
    withTempDir "tp-wasfiltered-full" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getCompleted, recorder) = testRunCompletedRecorder ()
        host.RegisterHandler(recorder)

        let configs =
            [ { Project = "ProjA"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil (fun () -> getCompleted () |> List.isEmpty |> not) 10000

        let completed = getCompleted ()
        test <@ completed.Length >= 1 @>
        let last = completed |> List.last

        match last.Results |> Map.tryFind "ProjA" with
        | Some r -> test <@ TestResult.wasFiltered r = false @>
        | None -> Assert.Fail("ProjA not in Results"))

// Every project's output is STREAMED to `.fshw/test-runs/<runId>/<Project>.output.log`,
// so a suite KILLED at its timeout still leaves the part it managed to print. A killed
// child reaches no end-of-run writer, so anything that buffers and flushes at the end
// leaves nothing at all — which is how an integration suite hit its 900s cap four runs
// running with no evidence but a 40-line tail of repeated startup logging.

/// The run logs on disk under a repo root, as (file name, contents).
let private runLogsUnder (repoRoot: string) =
    let root = Path.Combine(repoRoot, ".fshw", "test-runs")

    if not (Directory.Exists root) then
        []
    else
        Directory.GetFiles(root, "*" + FsHotWatch.RunLog.Suffix, SearchOption.AllDirectories)
        |> Array.toList
        |> List.map (fun f -> Path.GetFileName f, File.ReadAllText f)
        |> List.sortBy fst

[<Fact(Timeout = 60000)>]
let ``a test project KILLED at its timeout still leaves its partial run log`` () =
    withTempDir "tp-runlog-kill" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getCompleted, recorder) = testRunCompletedRecorder ()
        host.RegisterHandler(recorder)

        // Announces its cause, then hangs. `TimeoutSec = 2` kills the tree, so this
        // project never exits and never reaches a writer of any kind.
        let configs =
            [ { Project = "ProjKilled"
                Command = "sh"
                Args = "-c \"echo SHARD-POOL-IN-USE-BY-PID-18024; sleep 60\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = Some 2
                // A `sh` fixture is not an MTP runner: asking it for CTRF would put
                // an unsupported flag on its command line.
                ReportVerificationFormat = Disabled } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil (fun () -> getCompleted () |> List.isEmpty |> not) 45000

        let completed = getCompleted ()
        test <@ completed.Length >= 1 @>

        // Without this the test proves nothing about the kill path.
        match (completed |> List.last).Results |> Map.tryFind "ProjKilled" with
        | Some(TestsTimedOut _) -> ()
        | other -> Assert.Fail $"fixture broken: expected ProjKilled to be TestsTimedOut, got %A{other}"

        match runLogsUnder tmpDir with
        | [ (name, contents) ] ->
            test <@ name = "ProjKilled" + FsHotWatch.RunLog.Suffix @>
            // What it said before it died — kept.
            test <@ contents.Contains("SHARD-POOL-IN-USE-BY-PID-18024") @>
        | other -> Assert.Fail $"expected exactly one run log for the killed project, got %A{other}")

[<Fact(Timeout = 60000)>]
let ``a PASSING project gets a run log too — no special-casing the suspect suite`` () =
    // Which project will need explaining is not knowable in advance, and a passing-but-slow
    // suite is worth reading. The log is not a failure artifact.
    withTempDir "tp-runlog-pass" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getCompleted, recorder) = testRunCompletedRecorder ()
        host.RegisterHandler(recorder)

        let configs =
            [ { Project = "ProjGreenA"
                Command = "echo"
                Args = "hello-from-a"
                Group = "a"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = Disabled }
              { Project = "ProjGreenB"
                Command = "echo"
                Args = "hello-from-b"
                Group = "b"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = Disabled } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil (fun () -> getCompleted () |> List.isEmpty |> not) 30000

        match runLogsUnder tmpDir with
        | [ (nameA, contentsA); (nameB, contentsB) ] ->
            test <@ nameA = "ProjGreenA" + FsHotWatch.RunLog.Suffix @>
            test <@ nameB = "ProjGreenB" + FsHotWatch.RunLog.Suffix @>
            // Each holds its OWN project's output, verbatim — not a merged pile.
            test <@ contentsA.Contains("hello-from-a") @>
            test <@ not (contentsA.Contains("hello-from-b")) @>
            test <@ contentsB.Contains("hello-from-b") @>
        | other -> Assert.Fail $"expected one run log per project, got %A{other}")

// Coverage path selection + post-test merge. Pure helpers, tested against the filesystem
// with pre-seeded fixtures rather than a real coverlet run.

open FsHotWatch.TestPrune

[<Fact>]
let ``buildCoverageArgs picks baseline on full run and partial on filtered run`` () =
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/baseline.cobertura.xml"
          Partial = "/tmp/cov/partial.cobertura.xml"
          Cobertura = "/tmp/cov/coverage.cobertura.xml"
          IncludeInRatchet = true
          ArgsTemplate = defaultCoverageArgsTemplate }

    let full = buildCoverageArgs paths false
    test <@ full.Contains("baseline.cobertura.xml") @>
    test <@ not (full.Contains("partial.cobertura.xml")) @>

    let partial = buildCoverageArgs paths true
    test <@ partial.Contains("partial.cobertura.xml") @>
    test <@ not (partial.Contains("baseline.cobertura.xml")) @>

[<Fact>]
let ``default template uses an MTP-accepted output format`` () =
    // MTP accepts only `coverage | xml | cobertura`. Emitting `--coverage-output-format
    // json` made every test run fail at startup with an invalid-args error and zero tests
    // executed.
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/baseline.cobertura.xml"
          Partial = "/tmp/cov/partial.cobertura.xml"
          Cobertura = "/tmp/cov/coverage.cobertura.xml"
          IncludeInRatchet = true
          ArgsTemplate = defaultCoverageArgsTemplate }

    let args = buildCoverageArgs paths false
    let mtpAccepts = [ "coverage"; "xml"; "cobertura" ]

    let usesAnAccepted =
        mtpAccepts
        |> List.exists (fun fmt -> args.Contains(sprintf "--coverage-output-format %s" fmt))

    test <@ usesAnAccepted @>
    test <@ not (args.Contains("--coverage-output-format json")) @>

[<Fact>]
let ``buildCoverageArgs honors a custom ArgsTemplate with {output} substitution`` () =
    // Coverage invocation varies by runner (MTP, classic dotnet test + coverlet.collector,
    // AltCover, OpenCover), so callers supply a template and the plugin substitutes
    // `{output}`. It is a pure string replace — the template owns its own quoting.
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/B.xml"
          Partial = "/tmp/cov/P.xml"
          Cobertura = "/tmp/cov/C.xml"
          IncludeInRatchet = true
          ArgsTemplate = "--custom-collector --out \"{output}\" --extra" }

    let full = buildCoverageArgs paths false
    test <@ full = "--custom-collector --out \"/tmp/cov/B.xml\" --extra" @>

    let partial = buildCoverageArgs paths true
    test <@ partial = "--custom-collector --out \"/tmp/cov/P.xml\" --extra" @>

[<Fact>]
let ``buildCoverageArgs treats a template missing {output} as invalid`` () =
    // A forgotten placeholder must surface, not silently produce invalid args.
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/B.xml"
          Partial = "/tmp/cov/P.xml"
          Cobertura = "/tmp/cov/C.xml"
          IncludeInRatchet = true
          ArgsTemplate = "--broken-template-no-placeholder" }

    let ex = Assert.ThrowsAny(fun () -> buildCoverageArgs paths false |> ignore)

    test <@ ex.Message.Contains("{output}") @>

[<Fact>]
let ``AUTOMATION-315 collected coverage retains test-project identity and ratchet intent`` () =
    let input = coverageInput "RuntimeOnlyTests" false "/tmp/cov/runtime-only.xml"

    test <@ input.Project = "RuntimeOnlyTests" @>
    test <@ input.RawPath = "/tmp/cov/runtime-only.xml" @>
    test <@ input.IncludeInRatchet = false @>

/// Build a minimal cobertura document with a single package/class/two-line shape.
let private mkCobertura (pkg: string) (file: string) (lines: (int * int) list) : string =
    let linesXml =
        lines
        |> List.map (fun (n, h) -> sprintf "<line number=\"%d\" hits=\"%d\" />" n h)
        |> String.concat ""

    sprintf
        "<?xml version=\"1.0\"?><coverage><packages><package name=\"%s\"><classes><class filename=\"%s\" name=\"%s\"><lines>%s</lines></class></classes></package></packages></coverage>"
        pkg
        file
        file
        linesXml

// Seed a DB with a single symbol spanning the given repo-relative file and line span, so
// `ingestCobertura` can map covered lines onto it.
let private seedSymbolDb (dbPath: string) (sourceFile: string) (lineStart: int) (lineEnd: int) =
    let db = Database.create dbPath

    let symbol: SymbolInfo =
        { FullName = "Foo.bar"
          Kind = SymbolKind.Value
          SourceFile = sourceFile
          LineStart = lineStart
          LineEnd = lineEnd
          ContentHash = "h"
          IsExtern = false }

    db.RebuildProjects([ AnalysisResult.Create([ symbol ], [], []) ])
    db

[<Fact>]
let ``AUTOMATION-315 runtime selector widens missing and stale configured projects with typed reasons`` () =
    withTempDir "runtime-widening" (fun dir ->
        let db = Database.create (Path.Combine(dir, "test.db"))

        let testMethods =
            [ "CurrentTests"; "MissingTests"; "StaleTests"; "UnconfiguredTests" ]
            |> List.map (fun project ->
                { SymbolFullName = $"%s{project}.Tests.runs"
                  TestProject = project
                  TestClass = $"%s{project}.Tests"
                  TestMethod = "runs" })

        let testSymbols =
            testMethods
            |> List.map (fun method ->
                { FullName = method.SymbolFullName
                  Kind = SymbolKind.Value
                  SourceFile = $"tests/%s{method.TestProject}.fs"
                  LineStart = 1
                  LineEnd = 1
                  ContentHash = "h"
                  IsExtern = false })

        let runtimeTarget =
            { FullName = "RuntimeDispatch.target"
              Kind = SymbolKind.Value
              SourceFile = "src/Foo.fs"
              LineStart = 1
              LineEnd = 3
              ContentHash = "target"
              IsExtern = false }

        // Deliberately no dependency edge from any test to runtimeTarget: this
        // is the tracer's static miss. Only project-attributed runtime coverage
        // can select CurrentTests for the changed file.
        db.RebuildProjects([ AnalysisResult.Create(runtimeTarget :: testSymbols, [], testMethods) ])
        test <@ db.QueryAffectedTests([ runtimeTarget.FullName ]).IsEmpty @>
        db.ReplaceRuntimeCoverage("CurrentTests", "current", [ "src/Foo.fs" ])
        db.ReplaceRuntimeCoverage("StaleTests", "old", [ "src/Other.fs" ])
        db.ReplaceRuntimeCoverage("UnconfiguredTests", "other", [ "src/Foo.fs" ])

        let selection =
            selectByRuntimeCoverage
                db
                [ "CurrentTests"; "MissingTests"; "StaleTests" ]
                [ "src/Foo.fs" ]
                (DateTimeOffset.UtcNow.Subtract RuntimeCoverageMaxAge)

        test <@ selection.ProjectsByFile.["src/Foo.fs"] = set [ "CurrentTests"; "MissingTests" ] @>

        test
            <@
                selection.Widenings
                |> List.exists (function
                    | "MissingTests", MissingBaseline -> true
                    | _ -> false)
            @>

        let staleSelection =
            selectByRuntimeCoverage db [ "StaleTests" ] [ "src/Foo.fs" ] (DateTimeOffset.UtcNow.AddSeconds 1.0)

        test
            <@
                staleSelection.Widenings
                |> List.exists (function
                    | "StaleTests", StaleBaseline _ -> true
                    | _ -> false)
            @>)

[<Fact>]
let ``AUTOMATION-315 runtime widening is diagnosed when the AST change has zero symbols`` () =
    let observedAt =
        DateTimeOffset.UtcNow.Subtract(RuntimeCoverageMaxAge).AddMinutes(-1.0)

    let selection =
        { ProjectsByFile = Map.ofList [ "src/RuntimeOnly.fs", set [ "RuntimeTests" ] ]
          Widenings =
            [ "MissingRuntimeTests", MissingBaseline
              "StaleRuntimeTests", StaleBaseline observedAt ] }

    let messages = ResizeArray<string>()

    // No symbol list participates in this reporting boundary: a file-level runtime
    // widening remains visible even when symbol extraction yielded [] for the edit.
    reportRuntimeCoverageWidenings messages.Add selection

    test <@ messages.Count = 2 @>
    test <@ messages.[0].Contains "MissingRuntimeTests" @>
    test <@ messages.[0].Contains "no complete baseline" @>
    test <@ messages.[1].Contains "StaleRuntimeTests" @>
    test <@ messages.[1].Contains "older than" @>

[<Fact>]
let ``AUTOMATION-315 runtime file obligations survive restart without cross-producting projects`` () =
    withTempDir "runtime-obligations" (fun dir ->
        let first =
            mergeRuntimeCoverageObligations
                Map.empty
                (Map.ofList [ "src/A.fs", set [ "ATests" ]; "src/B.fs", set [ "BTests" ] ])

        saveRuntimeCoverageObligations dir first

        let loaded = loadRuntimeCoverageObligations dir
        test <@ loaded = Ok first @>

        let merged =
            mergeRuntimeCoverageObligations
                first
                (Map.ofList [ "src/A.fs", set [ "AIntegration" ]; "src/C.fs", set [ "CTests" ] ])

        test <@ merged.["src/A.fs"] = Map.ofList [ "ATests", 1L; "AIntegration", 1L ] @>
        test <@ merged.["src/B.fs"] = Map.ofList [ "BTests", 1L ] @>
        test <@ merged.["src/C.fs"] = Map.ofList [ "CTests", 1L ] @>)

[<Fact>]
let ``AUTOMATION-315 a repeated pair arriving mid-run survives the older green generation`` () =
    let launched =
        mergeRuntimeCoverageObligations Map.empty (Map.ofList [ "src/A.fs", set [ "Project" ] ])

    let arrivedMidRun =
        mergeRuntimeCoverageObligations launched (Map.ofList [ "src/A.fs", set [ "Project" ] ])

    let remaining =
        retireRuntimeCoverageObligations arrivedMidRun launched (fun _ -> true)

    test <@ remaining.["src/A.fs"].["Project"] = 2L @>

[<Fact>]
let ``AUTOMATION-315 restart drops obligations for projects no longer configured for collection`` () =
    let obligations =
        mergeRuntimeCoverageObligations
            Map.empty
            (Map.ofList [ "src/A.fs", set [ "StillConfigured"; "RemovedProject" ] ])

    let pruned = pruneRuntimeCoverageObligations (set [ "StillConfigured" ]) obligations
    test <@ pruned.["src/A.fs"] = Map.ofList [ "StillConfigured", 1L ] @>

[<Fact>]
let ``AUTOMATION-315 interrupted obligation persistence restarts as unknown debt`` () =
    withTempDir "runtime-obligation-recovery" (fun dir ->
        let marker = runtimeCoverageRecoveryPath dir
        Directory.CreateDirectory(Path.GetDirectoryName marker) |> ignore
        File.WriteAllText(marker, "write started")

        match loadRuntimeCoverageObligations dir with
        | Error reason -> test <@ reason.Contains "did not complete" @>
        | Ok _ -> Assert.Fail "a surviving recovery marker must never load as an empty debt")

[<Fact>]
let ``AUTOMATION-315 a ledger write failure leaves recovery armed before restart`` () =
    let mutable markerWritten = false
    let mutable markerCleared = false

    let result =
        persistRuntimeCoverageObligationsWith
            (fun () -> markerWritten <- true)
            (fun () -> raise (IOException "disk refused obligation write"))
            (fun () -> markerCleared <- true)

    test <@ markerWritten @>
    test <@ not markerCleared @>

    match result with
    | Error ex -> test <@ ex.Message.Contains "disk refused" @>
    | Ok() -> Assert.Fail "a failed ledger write must not clear its recovery marker"

[<Fact>]
let ``AUTOMATION-315 a recovery marker failure does not accept an undurable obligation transition`` () =
    let before =
        mergeRuntimeCoverageObligations Map.empty (Map.ofList [ "src/Old.fs", set [ "Tests" ] ])

    let mutable saved = None
    let mutable markerCleared = false

    let result =
        persistRuntimeCoverageTransitionWith
            (fun () -> raise (IOException "disk refused recovery marker"))
            (fun obligations -> saved <- Some obligations)
            (fun () -> markerCleared <- true)
            before
            (fun current ->
                mergeRuntimeCoverageObligations current (Map.ofList [ "src/New.fs", set [ "Integration" ] ]))

    test <@ saved.IsNone @>
    test <@ not markerCleared @>

    match result with
    | Error(current, ex) ->
        test <@ current = before @>
        test <@ not (current.ContainsKey "src/New.fs") @>
        test <@ ex.Message.Contains "recovery marker" @>
    | Ok _ -> Assert.Fail "an obligation transition cannot be accepted before its recovery marker is durable"

// ── AUTOMATION-572 ────────────────────────────────────────────────────────────────────
// A scan's OBSERVATION that a file changed is not an obligation. Recording one for a
// file that obligates no project produces a debt `nothingOwed` reports as outstanding
// forever and `runtimeForceProjects` can select nothing for — which is an empty
// selection, which is every configured project in full.

[<Fact>]
let ``AUTOMATION-572 a changed file that obligates no project records no runtime debt`` () =
    // The shape `selectByRuntimeCoverage` produces for an edit nothing has ever traced:
    // the file is present (it changed) and its project set is empty.
    let ledger =
        mergeRuntimeCoverageObligations Map.empty (Map.ofList [ "src/Untraced.fs", Set.empty ])

    // `Map.isEmpty` is the exact question `nothingOwed` asks. Before this fix it
    // answered "something is owed" for the rest of the daemon session.
    test <@ Map.isEmpty ledger @>

[<Fact>]
let ``AUTOMATION-572 an untraced file in the same cycle as a traced one owes only the traced one`` () =
    // The mixed cycle is the one that hid the defect: the ledger looked populated and
    // correct, because one real obligation was in it.
    let ledger =
        mergeRuntimeCoverageObligations
            Map.empty
            (Map.ofList [ "src/Traced.fs", set [ "IntegrationTests" ]; "src/Untraced.fs", Set.empty ])

    test <@ Map.toList ledger = [ "src/Traced.fs", Map.ofList [ "IntegrationTests", 1L ] ] @>

[<Fact>]
let ``AUTOMATION-572 a later cycle finding nothing to add leaves a real obligation standing`` () =
    // Skipping the empty set must not become a way to DISCHARGE debt: only the run that
    // covered the file may do that (`retireRuntimeCoverageObligations`).
    let owed =
        mergeRuntimeCoverageObligations Map.empty (Map.ofList [ "src/Traced.fs", set [ "IntegrationTests" ] ])

    let afterUntracedCycle =
        mergeRuntimeCoverageObligations owed (Map.ofList [ "src/Traced.fs", Set.empty ])

    test <@ afterUntracedCycle = owed @>

[<Fact>]
let ``AUTOMATION-572 selectByRuntimeCoverage over an untraced file leaves the ledger empty`` () =
    withTempDir "runtime-untraced" (fun dir ->
        let db = Database.create (Path.Combine(dir, "test.db"))

        // The project has a CURRENT runtime baseline, so nothing widens; the changed
        // file simply has no attribution of its own. Both halves of
        // `Set.union current widenedProjects` are therefore empty.
        db.ReplaceRuntimeCoverage("IntegrationTests", "current", [ "src/Traced.fs" ])

        let selection =
            selectByRuntimeCoverage
                db
                [ "IntegrationTests" ]
                [ "src/Untraced.fs" ]
                (DateTimeOffset.UtcNow.Subtract RuntimeCoverageMaxAge)

        // The selection reports the file — this is not the bug, and callers rely on the
        // map being keyed by every changed file.
        test <@ selection.ProjectsByFile = Map.ofList [ "src/Untraced.fs", Set.empty ] @>
        test <@ selection.Widenings.IsEmpty @>

        // The ledger is what must stay clean.
        test <@ Map.isEmpty (mergeRuntimeCoverageObligations Map.empty selection.ProjectsByFile) @>)

[<Fact>]
let ``AUTOMATION-572 no runtime obligation ledger transition may name zero projects`` () =
    // The re-consumption guard. The failure mode is SILENT — an entry naming nothing
    // costs a full suite and logs nothing — so the invariant is asserted over every
    // transition that can produce a ledger, not just the one that broke it. A new arm
    // that admits an empty entry fails here rather than in a gate run three days later.
    let namesAProject (label: string) (ledger: RuntimeCoverageObligations) =
        let offenders =
            ledger |> Map.filter (fun _ projects -> Map.isEmpty projects) |> Map.keys

        Assert.True(
            Seq.isEmpty offenders,
            $"%s{label} left an obligation naming no project: %A{List.ofSeq offenders}. \
              Nothing can select it and nothing can discharge it, but `nothingOwed` reports \
              it as debt — so every cycle that selects nothing runs the whole suite."
        )

    let real = Map.ofList [ "src/Traced.fs", set [ "IntegrationTests" ] ]
    let untraced = Map.ofList [ "src/Untraced.fs", Set.empty ]

    let merged = mergeRuntimeCoverageObligations Map.empty real
    namesAProject "merge of a real obligation" merged
    namesAProject "merge of an untraced file" (mergeRuntimeCoverageObligations Map.empty untraced)
    namesAProject "merge of an untraced file onto real debt" (mergeRuntimeCoverageObligations merged untraced)

    namesAProject
        "retire of the only obligated project"
        (retireRuntimeCoverageObligations merged merged (fun _ -> true))

    namesAProject "prune of the only allowed project" (pruneRuntimeCoverageObligations Set.empty merged)

    withTempDir "runtime-invariant-roundtrip" (fun dir ->
        saveRuntimeCoverageObligations dir merged

        match loadRuntimeCoverageObligations dir with
        | Ok loaded -> namesAProject "save/load round trip" loaded
        | Error reason -> Assert.Fail $"the ledger could not be read back: %s{reason}")

[<Fact>]
let ``AUTOMATION-572 a zero-affected widening names every outstanding debt`` () =
    let causes =
        zeroAffectedWidening false true 3 (Map.ofList [ "src/Traced.fs", Map.ofList [ "IntegrationTests", 1L ] ]) 2

    test
        <@
            causes = [ ZeroAffectedWidening.NoSessionBaseline
                       ZeroAffectedWidening.UnreadableLedger
                       ZeroAffectedWidening.QueuedSymbols 3
                       ZeroAffectedWidening.RuntimeCoverageDebt(1, 1)
                       ZeroAffectedWidening.OutstandingFailures 2 ]
        @>

    let rendered = ZeroAffectedWidening.describeMany causes
    test <@ rendered.Contains "3 symbol(s)" @>
    test <@ rendered.Contains "1 file(s) naming 1 project(s)" @>
    test <@ rendered.Contains "2 outstanding test failure(s)" @>

[<Fact>]
let ``AUTOMATION-572 an obligation naming no project is not counted as a reason to widen`` () =
    // The alarm, stated as a test. If some future arm re-admits an entry naming nothing,
    // this function must NOT dress it up as runtime-coverage debt — it must return the
    // empty list, which is what makes the daemon warn instead of quietly running a whole
    // suite. Reporting it as a cause would restore exactly the silence this ticket closes.
    let phantom = Map.ofList [ "src/Untraced.fs", Map.empty<string, int64> ]

    test <@ List.isEmpty (zeroAffectedWidening true false 0 phantom 0) @>

    // And a real obligation beside the phantom is still counted — once, for the file
    // that actually owes something.
    let mixed = Map.add "src/Traced.fs" (Map.ofList [ "IntegrationTests", 1L ]) phantom

    test <@ zeroAffectedWidening true false 0 mixed 0 = [ ZeroAffectedWidening.RuntimeCoverageDebt(1, 1) ] @>

[<Fact>]
let ``AUTOMATION-572 nothing owed and a baseline in hand is no reason to widen at all`` () =
    test <@ List.isEmpty (zeroAffectedWidening true false 0 Map.empty 0) @>

// `ingestAndEmitCoverage` ingests each project's raw runner cobertura into the TestPrune
// DB (max-merge, symbol-relative), then emits the full DB once to the single shared
// cobertura file.
[<Fact>]
let ``ingestAndEmitCoverage ingests covered lines and emits the single shared cobertura`` () =
    withTempDir "cov-ingest" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12

        // Raw cobertura from the runner uses an ABSOLUTE filename, as a real run would;
        // ingest relativizes it against repoRoot to match the symbol's source_file.
        let absFile = Path.Combine(repoRoot, "src/Foo.fs")
        let rawPath = Path.Combine(dir, "coverage.baseline.cobertura.xml")
        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        File.WriteAllText(rawPath, mkCobertura "Foo.dll" absFile [ (10, 3); (11, 1); (12, 0) ])

        ingestAndEmitCoverage db repoRoot "run-1" (Some sharedOut) [ coverageInput "Tests" true rawPath ]
        |> ignore

        test <@ File.Exists sharedOut @>
        let xml = File.ReadAllText sharedOut
        // Reported back at the symbol's current absolute positions.
        test <@ xml.Contains("number=\"10\"") @>
        test <@ xml.Contains("number=\"11\"") @>
        test <@ xml.Contains("hits=\"3\"") @>)

[<Fact>]
let ``AUTOMATION-315 collect-only coverage stays project-attributed and out of ratchet output`` () =
    withTempDir "cov-impact-only" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12
        let absFile = Path.Combine(repoRoot, "src/Foo.fs")
        let ratchetRaw = Path.Combine(dir, "ratchet.xml")
        let impactRaw = Path.Combine(dir, "impact-only.xml")
        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        Directory.CreateDirectory(Path.GetDirectoryName(sharedOut)) |> ignore

        File.WriteAllText(ratchetRaw, mkCobertura "RatchetTests" absFile [ (10, 1) ])
        File.WriteAllText(impactRaw, mkCobertura "RuntimeTests" absFile [ (11, 9) ])

        ingestAndEmitCoverage
            db
            repoRoot
            "run-1"
            (Some sharedOut)
            [ coverageInput "RatchetTests" true ratchetRaw
              coverageInput "RuntimeTests" false impactRaw ]
        |> ignore

        let emitted = File.ReadAllText(sharedOut)
        test <@ emitted.Contains("number=\"10\"") @>
        test <@ not (emitted.Contains("number=\"11\"")) @>

        test <@ db.GetRuntimeCoverageProjects([ "src/Foo.fs" ]) = [ "RatchetTests"; "RuntimeTests" ] @>)

[<Fact>]
let ``AUTOMATION-315 disabling a formerly ratcheted project removes its persisted contribution`` () =
    withTempDir "cov-ratchet-transition" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12
        let absFile = Path.Combine(repoRoot, "src/Foo.fs")
        let unitRaw = Path.Combine(dir, "unit.xml")
        let integrationRaw = Path.Combine(dir, "integration.xml")
        let sharedOut = Path.Combine(dir, "coverage", CoberturaName)

        File.WriteAllText(unitRaw, mkCobertura "UnitTests" absFile [ (10, 3) ])
        File.WriteAllText(integrationRaw, mkCobertura "IntegrationTests" absFile [ (11, 7) ])

        ingestAndEmitCoverageForProjects
            db
            repoRoot
            "run-enabled"
            (set [ "UnitTests"; "IntegrationTests" ])
            (Some sharedOut)
            [ coverageInput "UnitTests" true unitRaw
              coverageInput "IntegrationTests" true integrationRaw ]
        |> ignore

        let before = File.ReadAllText sharedOut
        test <@ before.Contains("number=\"10\"") @>
        test <@ before.Contains("number=\"11\"") @>

        // The next configuration keeps collecting IntegrationTests for impact
        // evidence but removes it from the consumer ratchet. UnitTests does not
        // need to run again: its attributed contribution is persisted separately.
        File.WriteAllText(integrationRaw, mkCobertura "IntegrationTests" absFile [ (11, 9) ])

        ingestAndEmitCoverageForProjects
            db
            repoRoot
            "run-collect-only"
            (set [ "UnitTests" ])
            (Some sharedOut)
            [ coverageInput "IntegrationTests" false integrationRaw ]
        |> ignore

        let after = File.ReadAllText sharedOut
        test <@ after.Contains("number=\"10\"") @>
        test <@ not (after.Contains("number=\"11\"")) @>)

[<Fact>]
let ``AUTOMATION-315 first project-attributed ingest invalidates a legacy projectless ratchet`` () =
    withTempDir "cov-ratchet-legacy-migration" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12
        let absFile = Path.Combine(repoRoot, "src/Foo.fs")
        let integrationRaw = Path.Combine(dir, "integration.xml")
        let sharedOut = Path.Combine(dir, "coverage", CoberturaName)
        Directory.CreateDirectory(Path.GetDirectoryName sharedOut) |> ignore

        // f311 persisted only the projectless Core high-water mark plus the shared
        // consumer file. Deliberately never call the project-attributed ingest here.
        let legacy = mkCobertura "Legacy" absFile [ (10, 3); (11, 7) ]
        ingestCobertura db (Some repoRoot) legacy |> ignore
        File.WriteAllText(sharedOut, legacy)

        // Integration has become collect-only. With no attributable Unit baseline,
        // preserving the legacy file would preserve Integration's historical line.
        File.WriteAllText(integrationRaw, mkCobertura "IntegrationTests" absFile [ (11, 9) ])

        ingestAndEmitCoverageForProjects
            db
            repoRoot
            "first-attributed-run"
            (set [ "UnitTests" ])
            (Some sharedOut)
            [ coverageInput "IntegrationTests" false integrationRaw ]
        |> ignore

        test <@ not (File.Exists sharedOut) @>)

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-315 project-filtered run keeps unrelated configured ratchet projects eligible`` () =
    withTempDir "cov-ratchet-filtered-wiring" (fun dir ->
        let repoRoot = dir
        let dbPath = Path.Combine(dir, "test.db")
        seedSymbolDb dbPath "src/Foo.fs" 10 12 |> ignore
        let absFile = Path.Combine(repoRoot, "src/Foo.fs")
        let sharedOut = Path.Combine(dir, "coverage", CoberturaName)
        let mutable integrationEnabled = true

        let configFor project line =
            let source = Path.Combine(dir, $"%s{project}.source.xml")
            let runner = Path.Combine(dir, $"%s{project}.runner.sh")
            File.WriteAllText(source, mkCobertura project absFile [ (line, line) ])

            File.WriteAllText(
                runner,
                $"""#!/bin/sh
set -eu
output=""
while [ "$#" -gt 0 ]; do
  if [ "$1" = "--coverage-output" ]; then
    output="$2"
    shift 2
  else
    shift
  fi
done
cp "%s{source}" "$output"
"""
            )

            { Project = project
              Command = "sh"
              Args = runner
              Group = "default"
              Environment = []
              FilterTemplate = None
              ClassJoin = " "
              TimeoutSec = Some 10
              ReportVerificationFormat = Disabled }

        let configs = [ configFor "UnitTests" 10; configFor "IntegrationTests" 11 ]

        let coveragePaths project =
            let projectDir = Path.Combine(dir, "coverage", project)

            Some
                { Baseline = Path.Combine(projectDir, BaselineName)
                  Partial = Path.Combine(projectDir, PartialName)
                  Cobertura = sharedOut
                  IncludeInRatchet = project = "UnitTests" || integrationEnabled
                  ArgsTemplate = "--coverage-output {output}" }

        let host = PluginHost.create (Unchecked.defaultof<_>) dir

        let handler =
            create dbPath dir (Some configs) None None None (Some coveragePaths) []

        host.RegisterHandler(handler)

        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        let before = File.ReadAllText sharedOut
        test <@ before.Contains("number=\"10\"") @>
        test <@ before.Contains("number=\"11\"") @>

        integrationEnabled <- false

        host.RunCommand("run-tests", [| """{"projects": ["IntegrationTests"]}""" |])
        |> Async.RunSynchronously
        |> ignore

        let after = File.ReadAllText sharedOut
        test <@ after.Contains("number=\"10\"") @>
        test <@ not (after.Contains("number=\"11\"")) @>)

[<Fact>]
let ``AUTOMATION-315 coverage receipt removes stale artifact and accepts only a newly written successful result`` () =
    withTempDir "cov-fresh-receipt" (fun dir ->
        let rawPath = Path.Combine(dir, BaselineName)
        File.WriteAllText(rawPath, "stale")

        let launch = prepareCoverageArtifact "RuntimeTests" true false rawPath
        test <@ not (File.Exists rawPath) @>
        test <@ coverageInputFromReceipt true launch = None @>

        File.WriteAllText(rawPath, "fresh")

        match coverageInputFromReceipt true launch with
        | Some input ->
            test <@ input.Project = "RuntimeTests" @>
            test <@ input.RawPath = rawPath @>
            test <@ input.Scope = CoverageRunScope.Full @>
        | None -> Assert.Fail "a successful run that replaced its raw artifact must yield a receipt")

[<Fact>]
let ``AUTOMATION-315 an unreadable old artifact becoming readable is not a new-run receipt`` () =
    let launch =
        { Project = "RuntimeTests"
          IncludeInRatchet = false
          RawPath = "/tmp/runtime.xml"
          Scope = CoverageRunScope.Full
          Before = CoverageArtifactState.Unreadable "access denied"
          DeletionProven = false }

    let after =
        CoverageArtifactState.Fingerprinted
            { Length = 42L
              LastWriteUtc = DateTime.UnixEpoch
              Sha256 = "ABC" }

    test <@ coverageInputFromObservedState true launch after = None @>

[<Fact>]
let ``AUTOMATION-315 failed or filtered launches cannot replace a complete runtime baseline`` () =
    withTempDir "cov-receipt-scope" (fun dir ->
        let fullPath = Path.Combine(dir, BaselineName)
        let fullLaunch = prepareCoverageArtifact "RuntimeTests" true false fullPath
        File.WriteAllText(fullPath, "fresh-full")
        test <@ coverageInputFromReceipt false fullLaunch = None @>

        let partialPath = Path.Combine(dir, PartialName)
        let partialLaunch = prepareCoverageArtifact "RuntimeTests" true true partialPath
        File.WriteAllText(partialPath, "fresh-partial")

        match coverageInputFromReceipt true partialLaunch with
        | Some input -> test <@ input.Scope = CoverageRunScope.Partial @>
        | None -> Assert.Fail "a successful filtered run must yield a partial receipt")

[<Fact>]
let ``AUTOMATION-315 successful full run without a fresh receipt revokes its baseline and denies green`` () =
    withTempDir "cov-missing-full" (fun dir ->
        let db = Database.create (Path.Combine(dir, "test.db"))
        let staleBefore = DateTimeOffset.UtcNow.AddMinutes(-1.0)
        db.ReplaceRuntimeCoverage("RuntimeTests", "prior-green", [ "src/Prior.fs" ])

        let fullLaunch =
            { Project = "RuntimeTests"
              IncludeInRatchet = false
              RawPath = Path.Combine(dir, BaselineName)
              Scope = CoverageRunScope.Full
              Before = CoverageArtifactState.Missing
              DeletionProven = true }

        let failure =
            match coverageReceiptFromObservedState true fullLaunch CoverageArtifactState.Missing with
            | CoverageReceiptFailed failure -> failure
            | outcome ->
                Assert.Fail $"a green full run without a receipt must fail coverage processing, got %A{outcome}"
                Unchecked.defaultof<_>

        test <@ failure.Project = "RuntimeTests" @>
        test <@ failure.Reason.Contains "missing" @>

        invalidateCoverageReceiptFailure db failure

        test
            <@
                db.GetRuntimeCoverageAvailability([ "RuntimeTests" ], staleBefore) = [ ("RuntimeTests",
                                                                                        TestPrune.Domain.Missing) ]
            @>

        test <@ db.GetRuntimeCoverageProjects([ "src/Prior.fs" ]).IsEmpty @>

        let denied =
            Map.ofList [ "RuntimeTests", TestsPassed("", false, TimeSpan.FromSeconds 1.0) ]
            |> applyCoverageIngestFailures [ failure ]

        test <@ denied.["RuntimeTests"] |> TestResult.isErrored @>)

[<Fact>]
let ``AUTOMATION-315 successful filtered run without a receipt preserves its full baseline`` () =
    withTempDir "cov-missing-filtered" (fun dir ->
        let db = Database.create (Path.Combine(dir, "test.db"))
        let staleBefore = DateTimeOffset.UtcNow.AddMinutes(-1.0)
        db.ReplaceRuntimeCoverage("RuntimeTests", "prior-green", [ "src/Prior.fs" ])

        let partialLaunch =
            { Project = "RuntimeTests"
              IncludeInRatchet = false
              RawPath = Path.Combine(dir, PartialName)
              Scope = CoverageRunScope.Partial
              Before = CoverageArtifactState.Missing
              DeletionProven = true }

        test
            <@
                coverageReceiptFromObservedState true partialLaunch CoverageArtifactState.Missing = CoverageReceiptAbsent
            @>

        test
            <@
                db.GetRuntimeCoverageAvailability([ "RuntimeTests" ], staleBefore) = [ ("RuntimeTests",
                                                                                        TestPrune.Domain.Current) ]
            @>

        test <@ db.GetRuntimeCoverageProjects([ "src/Prior.fs" ]) = [ "RuntimeTests" ] @>)

[<Fact>]
let ``AUTOMATION-315 successful full run diagnoses unreadable and unchanged stale receipts`` () =
    let prior =
        CoverageArtifactState.Fingerprinted
            { Length = 42L
              LastWriteUtc = DateTime.UnixEpoch
              Sha256 = "ABC" }

    let launch =
        { Project = "RuntimeTests"
          IncludeInRatchet = false
          RawPath = "/tmp/runtime.xml"
          Scope = CoverageRunScope.Full
          Before = prior
          DeletionProven = false }

    let reasonFor after =
        match coverageReceiptFromObservedState true launch after with
        | CoverageReceiptFailed failure -> failure.Reason
        | outcome ->
            Assert.Fail $"a green full run must reject %A{after}, got %A{outcome}"
            ""

    test <@ (reasonFor (CoverageArtifactState.Unreadable "locked")).Contains "readable" @>
    test <@ (reasonFor prior).Contains "unchanged stale" @>

[<Fact>]
let ``AUTOMATION-315 malformed full receipt invalidates prior runtime evidence and denies green`` () =
    withTempDir "cov-malformed-full" (fun dir ->
        let dbPath = Path.Combine(dir, "test.db")
        let db = Database.create dbPath
        let staleBefore = DateTimeOffset.UtcNow.AddMinutes(-1.0)
        db.ReplaceRuntimeCoverage("RuntimeTests", "prior-green", [ "src/Prior.fs" ])

        let priorAvailability =
            db.GetRuntimeCoverageAvailability([ "RuntimeTests" ], staleBefore)

        test <@ priorAvailability = [ ("RuntimeTests", TestPrune.Domain.Current) ] @>

        let malformed = Path.Combine(dir, BaselineName)
        File.WriteAllText(malformed, "<coverage><not-closed>")

        let outcome =
            ingestAndEmitCoverage
                db
                dir
                "new-run"
                None
                [ { coverageInput "RuntimeTests" false malformed with
                      Scope = CoverageRunScope.Full } ]

        let failures = coverageIngestFailures outcome
        test <@ failures |> List.map _.Project = [ "RuntimeTests" ] @>

        let invalidatedAvailability =
            db.GetRuntimeCoverageAvailability([ "RuntimeTests" ], staleBefore)

        test <@ invalidatedAvailability = [ ("RuntimeTests", TestPrune.Domain.Missing) ] @>
        test <@ db.GetRuntimeCoverageProjects([ "src/Prior.fs" ]).IsEmpty @>

        let markerDirectory = Path.Combine(dir, "recovery-marker-is-a-directory")
        Directory.CreateDirectory markerDirectory |> ignore

        Assert.ThrowsAny<exn>(fun () ->
            armRuntimeCoverageUnknownDebt ignore markerDirectory (List.exactlyOne failures))
        |> ignore

        // Marker persistence is an additional live-process signal. The database
        // invalidation is already committed and survives independently when that
        // write fails or the process restarts immediately afterward.
        let availabilityAfterMarkerFailure =
            db.GetRuntimeCoverageAvailability([ "RuntimeTests" ], staleBefore)

        test <@ availabilityAfterMarkerFailure = [ ("RuntimeTests", TestPrune.Domain.Missing) ] @>

        let reopened = Database.create dbPath

        let reopenedAvailability =
            reopened.GetRuntimeCoverageAvailability([ "RuntimeTests" ], staleBefore)

        test <@ reopenedAvailability = [ ("RuntimeTests", TestPrune.Domain.Missing) ] @>

        test <@ reopened.GetRuntimeCoverageProjects([ "src/Prior.fs" ]).IsEmpty @>

        let marker = runtimeCoverageRecoveryPath dir
        let mutable unknownDebt = false

        armRuntimeCoverageUnknownDebt (fun () -> unknownDebt <- true) marker (List.exactlyOne failures)

        test <@ unknownDebt @>
        test <@ File.Exists marker @>

        let results =
            Map.ofList [ "RuntimeTests", TestsPassed("", false, TimeSpan.FromSeconds 1.0) ]
            |> applyCoverageIngestFailures failures

        test <@ results.["RuntimeTests"] |> TestResult.isErrored @>)

[<Fact>]
let ``ingestAndEmitCoverage with an empty raw cobertura does NOT clobber an existing emitted cobertura`` () =
    // An aborted run can leave an empty raw cobertura, which ingests nothing.
    withTempDir "cov-noclobber" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12

        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        Directory.CreateDirectory(Path.GetDirectoryName(sharedOut)) |> ignore
        let priorGood = mkCobertura "Foo.dll" "src/Foo.fs" [ (10, 7); (11, 7) ]
        File.WriteAllText(sharedOut, priorGood)

        // An aborted run wrote an empty <packages/> raw cobertura.
        let rawPath = Path.Combine(dir, "coverage.baseline.cobertura.xml")
        File.WriteAllText(rawPath, "<?xml version=\"1.0\"?><coverage><packages /></coverage>")

        ingestAndEmitCoverage db repoRoot "run-1" (Some sharedOut) [ coverageInput "Tests" true rawPath ]
        |> ignore

        // The empty raw still counts as an input on disk, so the DB is emitted — but
        // nothing was ingested, so no symbol coverage may have been recorded. The run
        // must never LOWER an existing good emission.
        let summary = TestPrune.Coverage.fileCoverageSummary db "src/Foo.fs"
        test <@ summary.Covered = 0 @>
        test <@ File.ReadAllText sharedOut = priorGood @>)

[<Fact>]
let ``ingestAndEmitCoverage with no inputs leaves a prior emitted cobertura untouched`` () =
    // Every project skipped or deferred, so nothing to emit from.
    withTempDir "cov-noinputs" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12

        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        Directory.CreateDirectory(Path.GetDirectoryName(sharedOut)) |> ignore
        let priorGood = mkCobertura "Foo.dll" "src/Foo.fs" [ (10, 7); (11, 7) ]
        File.WriteAllText(sharedOut, priorGood)

        // A raw path that does not exist on disk — filtered out, no emit.
        let missingRaw = Path.Combine(dir, "coverage.baseline.cobertura.xml")

        ingestAndEmitCoverage db repoRoot "run-1" (Some sharedOut) [ coverageInput "Tests" true missingRaw ]
        |> ignore

        test <@ File.ReadAllText sharedOut = priorGood @>)

[<Fact>]
let ``ingestAndEmitCoverage does NOT clobber prior coverage when the symbol graph is incomplete`` () =
    // Cold start — a schema bump recreated the DB and the scan has not yet reached the
    // covered files, so their lines cannot map. Emitting now would write a partial
    // cobertura that DROPS that file's coverage, clobbering a prior good emission and
    // failing the ratchet. The DB persists, so a later warm run emits in full.
    withTempDir "cov-coldstart" (fun dir ->
        let repoRoot = dir
        // The DB only knows an unrelated, already-indexed file — NOT the covered one.
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Indexed.fs" 1 5

        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        Directory.CreateDirectory(Path.GetDirectoryName(sharedOut)) |> ignore
        // A prior warm run emitted full, honest coverage for Embeddings.fs.
        let priorGood =
            mkCobertura "Intelligence.dll" "src/Embeddings.fs" [ (10, 3); (11, 3); (12, 1) ]

        File.WriteAllText(sharedOut, priorGood)

        // This run's raw cobertura covers Embeddings.fs, which the DB has no symbols for,
        // so every line is skipped.
        let absFile = Path.Combine(repoRoot, "src/Embeddings.fs")
        let rawPath = Path.Combine(dir, "coverage.baseline.cobertura.xml")
        File.WriteAllText(rawPath, mkCobertura "Intelligence.dll" absFile [ (10, 3); (11, 3); (12, 1) ])

        ingestAndEmitCoverage db repoRoot "run-1" (Some sharedOut) [ coverageInput "Tests" true rawPath ]
        |> ignore

        test <@ File.ReadAllText sharedOut = priorGood @>)

[<Fact>]
let ``ingestAndEmitCoverage emits when the symbol graph maps the bulk of lines (warm)`` () =
    // The complement of the cold-start skip above.
    withTempDir "cov-warm" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Embeddings.fs" 10 12

        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        let absFile = Path.Combine(repoRoot, "src/Embeddings.fs")
        let rawPath = Path.Combine(dir, "coverage.baseline.cobertura.xml")
        File.WriteAllText(rawPath, mkCobertura "Intelligence.dll" absFile [ (10, 3); (11, 1); (12, 0) ])

        ingestAndEmitCoverage db repoRoot "run-1" (Some sharedOut) [ coverageInput "Tests" true rawPath ]
        |> ignore

        test <@ File.Exists sharedOut @>
        let xml = File.ReadAllText sharedOut
        test <@ xml.Contains("Embeddings.fs") && xml.Contains("number=\"10\"") @>)

[<Fact>]
let ``symbolGraphLooksIncomplete: true below half mapped, false at/above half and when empty`` () =
    test <@ symbolGraphLooksIncomplete 10 90 @> // 10% mapped → still indexing
    test <@ symbolGraphLooksIncomplete 49 51 @> // just under half
    test <@ not (symbolGraphLooksIncomplete 50 50) @> // exactly half maps → real run
    test <@ not (symbolGraphLooksIncomplete 96 4) @> // healthy run
    test <@ not (symbolGraphLooksIncomplete 0 0) @> // nothing ingested → not "incomplete"

[<Fact>]
let ``clearFcsCheckCache removes the cache json files and reports the count`` () =
    withTempDir "fcs-cache" (fun repoRoot ->
        let cacheDir = Path.Combine(repoRoot, ".fshw", "cache")
        Directory.CreateDirectory(cacheDir) |> ignore
        File.WriteAllText(Path.Combine(cacheDir, "a.json"), "{}")
        File.WriteAllText(Path.Combine(cacheDir, "b.json"), "{}")
        // Only FCS check-cache entries are cleared; a non-json sibling must survive.
        File.WriteAllText(Path.Combine(cacheDir, "keep.txt"), "x")

        let cleared = clearFcsCheckCache repoRoot

        test <@ cleared = 2 @>
        test <@ not (File.Exists(Path.Combine(cacheDir, "a.json"))) @>
        test <@ File.Exists(Path.Combine(cacheDir, "keep.txt")) @>)

[<Fact>]
let ``clearFcsCheckCache is a no-op when there is no cache dir`` () =
    withTempDir "fcs-nocache" (fun repoRoot -> test <@ clearFcsCheckCache repoRoot = 0 @>)

// `TestResult.ranFullSuite`'s unit tests live in EventTests.fs beside the core type they
// cover. Duplicating them here meant editing both files in lockstep for every change.

[<Fact(Timeout = 15000)>]
let ``full run (no filter) emits TestRunCompleted verified as Ran FullSuite`` () =
    withTempDir "tp-ranfullsuite-full" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getCompleted, recorder) = testRunCompletedRecorder ()
        host.RegisterHandler(recorder)

        let configs =
            [ { Project = "ProjA"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil (fun () -> getCompleted () |> List.isEmpty |> not) 10000

        let last = getCompleted () |> List.last
        test <@ last.Verification = Ran RunScope.FullSuite @>)

[<Fact(Timeout = 20000)>]
let ``regression: TestPrune writes a cache entry with TestRunCompleted on terminal status`` () =
    // Lifecycle events must emit from the SYNCHRONOUS Custom(TestsFinished) handler, not
    // from the fire-and-forget async: the framework's per-event capture window only sees
    // the former, so emitting from the async left cached EmittedEvents empty and cache
    // replay could not re-fire TestRunCompleted to downstream subscribers.
    withTempDir "tp-cache-emit" (fun tmpDir ->
        let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
        let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cacheIface)

        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 10000

        let key: FsHotWatch.TaskCache.CompositeKey = { Plugin = "test-prune"; File = None }

        let cacheKeyFn = handler.CacheKey.Value
        let computedKey = cacheKeyFn (BuildCompleted BuildSucceeded)
        test <@ computedKey.IsSome @>

        // `cache.Set` runs AFTER the handler's Update returns, while
        // `waitForTerminalStatus` observes the status reported *inside* that Update — so
        // the entry can lag the status by a scheduling quantum. The write is
        // deterministic, just not instantly visible, hence the poll rather than one read.
        waitUntil (fun () -> (cacheIface.TryGet key computedKey.Value).IsSome) 5000
        let result = cacheIface.TryGet key computedKey.Value
        test <@ result.IsSome @>

        let hasCompleted =
            result.Value.EmittedEvents
            |> List.exists (fun e ->
                match e with
                | FsHotWatch.TaskCache.CachedTestRunCompleted _ -> true
                | _ -> false)

        test <@ hasCompleted @>)

// =============================================================================
// AUTOMATION-5 — a FAILED verdict must never be served from the task cache. The merkle
// key (changed-symbols + commit) does NOT pin the test OUTCOME, so a failing run and a
// later passing run on the same tree share a key.
// Caching the failure let `tryReplayCache` replay a stale red on a now-green tree,
// surviving daemon restarts via the on-disk cache. `Custom(TestsFinished)` therefore
// returns no key when any project did not pass, making the failure uncacheable.
// =============================================================================

let private outcomeCacheKey event =
    cacheKeyFor
        (fun () -> "symbols")
        (fun () -> None)
        (fun () -> None)
        (fun () -> "structure")
        (fun () -> None)
        (fun () -> false)
        (fun () -> true)
        event

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-5: TestPrune CacheKey is None for a failing TestsFinished, Some for an all-pass`` () =
    let started: TestRunStarted =
        { RunId = Guid.NewGuid()
          StartedAt = DateTime.UtcNow }

    let completedWith (results: (string * TestResult) list) : TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList results
          Verification = Ran RunScope.FullSuite }

    let failing =
        Custom(
            TestsFinished(
                started,
                completedWith
                    [ "ProjA", TestsPassed("ok", false, TimeSpan.Zero)
                      "ProjB", TestsFailed("boom", false, TimeSpan.Zero) ],
                emptyLaunch
            )
        )

    let passing =
        Custom(
            TestsFinished(
                started,
                completedWith
                    [ "ProjA", TestsPassed("ok", false, TimeSpan.Zero)
                      "ProjB", TestsPassed("ok", false, TimeSpan.Zero) ],
                emptyLaunch
            )
        )

    // A timed-out / deferred project is non-green too.
    let timedOut =
        Custom(
            TestsFinished(
                started,
                completedWith [ "ProjA", TestsTimedOut("slow", TimeSpan.FromSeconds 1.0, false, TimeSpan.Zero) ],
                emptyLaunch
            )
        )

    test <@ (outcomeCacheKey failing).IsNone @>
    test <@ (outcomeCacheKey timedOut).IsNone @>
    test <@ (outcomeCacheKey passing).IsSome @>

[<Fact(Timeout = 15000)>]
[<Trait("Regression", "AUTOMATION-357")>]
let ``AUTOMATION-357: a green partial receipt cannot write a whole-tree cache entry`` () =
    let started: TestRunStarted =
        { RunId = Guid.NewGuid()
          StartedAt = DateTime.UtcNow }

    let completed verification : TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList [ "ProjA", TestsPassed("ok", true, TimeSpan.Zero) ]
          Verification = verification }

    let finished verification =
        Custom(TestsFinished(started, completed verification, emptyLaunch))

    let keyFor verification = outcomeCacheKey (finished verification)

    // The break this catches: ignoring the run's receipt lets a narrow manual run mint
    // the same entry an ordinary whole-tree check looks up.
    test <@ (keyFor (Ran RunScope.Partial)).IsNone @>

    // Positive control: this is not a blanket ban on caching green tests. A run whose
    // own receipt says it covered the configured suite keeps the established fast path.
    test <@ (keyFor (Ran RunScope.FullSuite)).IsSome @>

[<Fact(Timeout = 15000)>]
[<Trait("Regression", "AUTOMATION-357")>]
let ``AUTOMATION-357: an unfiltered project subset receives partial verification`` () =
    let passed (project: string) : string * TestResult =
        project, TestsPassed("ok", false, TimeSpan.Zero)

    test
        <@ verificationWithin (Set.ofList [ "ProjA"; "ProjB" ]) (Map.ofList [ passed "ProjA" ]) = Ran RunScope.Partial @>

    test
        <@
            verificationWithin (Set.ofList [ "ProjA"; "ProjB" ]) (Map.ofList [ passed "ProjA"; passed "ProjB" ]) = Ran
                RunScope.FullSuite
        @>

[<Fact(Timeout = 20000)>]
[<Trait("Regression", "AUTOMATION-357")>]
let ``AUTOMATION-357: a project-scoped rerun cannot satisfy the next whole-suite check`` () =
    withTempDir "tp-a357-partial-cache" (fun tmpDir ->
        let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
        let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cacheIface)
        let runs = Path.Combine(tmpDir, "runs")

        let config project =
            { Project = project
              Command = "sh"
              Args = $"-c \"printf '%s{project}\\n' >> '%s{runs}'\""
              Group = "default"
              Environment = []
              FilterTemplate = None
              ClassJoin = " "
              TimeoutSec = None
              ReportVerificationFormat = AutoDetect }

        let handler =
            create
                (Path.Combine(tmpDir, "tp.db"))
                tmpDir
                (Some [ config "ProjA"; config "ProjB" ])
                None
                None
                None
                None
                []

        host.RegisterHandler(handler)

        let partialTerminal = beginAwaitNextTerminal host "test-prune"

        host.RunCommand("run-tests", [| "{\"projects\":[\"ProjA\"]}" |])
        |> Async.RunSynchronously
        |> ignore

        partialTerminal.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        let key: FsHotWatch.TaskCache.CompositeKey = { Plugin = "test-prune"; File = None }
        let wholeTreeKey = handler.CacheKey.Value(BuildCompleted BuildSucceeded)
        test <@ wholeTreeKey.IsSome @>
        test <@ (cacheIface.TryGet key wholeTreeKey.Value).IsNone @>

        // Make the following ordinary BuildCompleted unambiguously owe the full suite.
        // Before the fix, the partial run's cache entry replayed before this state could
        // execute, leaving ProjB untouched. A miss reaches Update and runs both projects.
        host.RunCommand("set-scope", [| "{\"scope\":\"full\"}" |])
        |> Async.RunSynchronously
        |> ignore

        let fullTerminal = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        fullTerminal.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        let executions = File.ReadAllLines(runs) |> Array.countBy id |> Map.ofArray
        test <@ executions.["ProjA"] = 2 @>
        test <@ executions.["ProjB"] = 1 @>)

// A filter that matched nothing is `TestsNoMatch`, for which `TestResult.isPassed` is
// deliberately TRUE — per project, selecting nothing is not that project's failure — so
// the cache key's `allPassed` fold cannot see the difference on its own. The entry it
// mints is replayable: a later `BuildCompleted` on the same tree hits a cached green
// produced by executing zero tests, and the poisoned entry outlives the run that made it.
// The neighbouring `notAborted` gate covers the empty-results version of this hazard;
// zero-match is the case it misses.
[<Fact(Timeout = 15000)>]
let ``a run where every project matched zero tests is not cacheable as a green`` () =
    let started: TestRunStarted =
        { RunId = Guid.NewGuid()
          StartedAt = DateTime.UtcNow }

    let completedWith (results: (string * TestResult) list) : TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList results
          Verification = Ran RunScope.FullSuite }

    let zeroMatch = TestsNoMatch("Zero tests ran", TimeSpan.Zero)

    let allZeroMatch =
        Custom(TestsFinished(started, completedWith [ "ProjA", zeroMatch; "ProjB", zeroMatch ], emptyLaunch))

    // The guard must key on "nothing was verified", not "a filter was used anywhere".
    let mixed =
        Custom(
            TestsFinished(
                started,
                completedWith [ "ProjA", zeroMatch; "ProjB", TestsPassed("ok", true, TimeSpan.Zero) ],
                emptyLaunch
            )
        )

    test <@ (outcomeCacheKey allZeroMatch).IsNone @>
    test <@ (outcomeCacheKey mixed).IsSome @>

// =============================================================================
// tests.dependsOn — external-input cache-key salt. Editing a DB migration changes the
// TEST database schema but no test SOURCE symbol, so the symbol-diff merkle is unchanged
// and a stale verdict replays. Declaring the migration under `tests.dependsOn` salts the
// BuildCompleted key with its content hash. With NO dependsOn the key must stay
// byte-identical to the pre-feature key, so existing caches keep hitting.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``dependsOn: changing a matched file changes the BuildCompleted cache key`` () =
    withTempDir "tp-dependson-key" (fun tmpDir ->
        let migrationsDir = Path.Combine(tmpDir, "migrations")
        Directory.CreateDirectory(migrationsDir) |> ignore
        let migration = Path.Combine(migrationsDir, "001_init.sql")
        File.WriteAllText(migration, "CREATE TABLE a (id int);")

        let handler = create ":memory:" tmpDir None None None None None [ "migrations/**" ]
        let cacheKeyFn = handler.CacheKey.Value

        let keyBefore = (cacheKeyFn (BuildCompleted BuildSucceeded)).Value

        // Schema changed, no test source touched.
        File.WriteAllText(migration, "CREATE TABLE a (id int, name text);")
        let keyAfter = (cacheKeyFn (BuildCompleted BuildSucceeded)).Value

        test <@ keyBefore <> keyAfter @>)

[<Fact(Timeout = 15000)>]
let ``dependsOn: adding a newly-matched file changes the BuildCompleted cache key`` () =
    withTempDir "tp-dependson-add" (fun tmpDir ->
        let migrationsDir = Path.Combine(tmpDir, "migrations")
        Directory.CreateDirectory(migrationsDir) |> ignore
        File.WriteAllText(Path.Combine(migrationsDir, "001_init.sql"), "CREATE TABLE a (id int);")

        let handler = create ":memory:" tmpDir None None None None None [ "migrations/**" ]
        let cacheKeyFn = handler.CacheKey.Value

        let keyBefore = (cacheKeyFn (BuildCompleted BuildSucceeded)).Value
        File.WriteAllText(Path.Combine(migrationsDir, "002_more.sql"), "ALTER TABLE a ADD COLUMN x int;")
        let keyAfter = (cacheKeyFn (BuildCompleted BuildSucceeded)).Value

        test <@ keyBefore <> keyAfter @>)

[<Fact(Timeout = 15000)>]
let ``dependsOn: absent config leaves the BuildCompleted key byte-identical to the no-salt key`` () =
    withTempDir "tp-dependson-absent" (fun tmpDir ->
        // Files on disk that WOULD match a glob, to prove the absent config is what
        // decides.
        let migrationsDir = Path.Combine(tmpDir, "migrations")
        Directory.CreateDirectory(migrationsDir) |> ignore
        File.WriteAllText(Path.Combine(migrationsDir, "001_init.sql"), "CREATE TABLE a (id int);")

        let salted = create ":memory:" tmpDir None None None None None []
        let unsalted = create ":memory:" tmpDir None None None None None [] // identical: [] dependsOn

        let kSalted = ((salted.CacheKey.Value) (BuildCompleted BuildSucceeded)).Value
        let kUnsalted = ((unsalted.CacheKey.Value) (BuildCompleted BuildSucceeded)).Value

        test <@ kSalted = kUnsalted @>
        // "" means no merkle entry was added at all, not an entry with an empty value.
        test <@ externalDependencyHash tmpDir [] = "" @>)

[<Fact(Timeout = 15000)>]
let ``dependsOn: a glob matching nothing contributes no salt (key equals empty-dependsOn key)`` () =
    withTempDir "tp-dependson-nomatch" (fun tmpDir ->
        let handlerEmpty = create ":memory:" tmpDir None None None None None []

        let handlerNoMatch =
            create ":memory:" tmpDir None None None None None [ "does-not-exist/**" ]

        let kEmpty = ((handlerEmpty.CacheKey.Value) (BuildCompleted BuildSucceeded)).Value

        let kNoMatch =
            ((handlerNoMatch.CacheKey.Value) (BuildCompleted BuildSucceeded)).Value

        test <@ kEmpty = kNoMatch @>
        test <@ externalDependencyHash tmpDir [ "does-not-exist/**" ] = "" @>)

[<Fact(Timeout = 10000)>]
let ``externalDependencyHash: deterministic and content-sensitive; missing files skipped`` () =
    withTempDir "tp-dependson-hash" (fun tmpDir ->
        let dir = Path.Combine(tmpDir, "ext")
        Directory.CreateDirectory(dir) |> ignore
        let f1 = Path.Combine(dir, "a.sql")
        let f2 = Path.Combine(dir, "b.sql")
        File.WriteAllText(f1, "one")
        File.WriteAllText(f2, "two")

        let globs = [ "ext/**" ]
        let h1 = externalDependencyHash tmpDir globs
        let h2 = externalDependencyHash tmpDir globs
        // Deterministic: sorted paths, content hash.
        test <@ h1 = h2 @>
        test <@ h1 <> "" @>

        File.WriteAllText(f1, "ONE-changed")
        let h3 = externalDependencyHash tmpDir globs
        test <@ h3 <> h1 @>

        // An emptied match set hashes back to "".
        File.Delete f1
        File.Delete f2
        let h4 = externalDependencyHash tmpDir globs
        test <@ h4 = "" @>)

// The cache key must not pay for inputs it discards. The `dependsOn` hash was computed
// EAGERLY, above the `match event with`, so every event paid for it — including
// `FileChecked`, which never splices it and which fires once PER FILE. With one glob
// configured, a cold scan of N files did N full-repo SafeWalks plus a SHA256 of every
// matched file, and threw all N results away. It was free only because no consumer sets
// `dependsOn`; the day one does, a cold scan goes quadratic.
//
// `cacheKeyFor` takes its state as thunks precisely so this is countable. RED-BEFORE-GREEN:
// hoist any thunk to a value at the top of `cacheKeyFor` and the FileChecked counts go
// from 0 to 1.

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: a FileChecked key reads only the cheap outstanding-red guard`` () =
    let mutable dependsOnCalls = 0
    let mutable pendingQueueCalls = 0
    let mutable changedSymbolsCalls = 0
    let mutable fullSuiteScopeCalls = 0
    let mutable outstandingCalls = 0
    let mutable sessionEvidenceCalls = 0
    let mutable structureCalls = 0

    let key =
        cacheKeyFor
            (fun () ->
                changedSymbolsCalls <- changedSymbolsCalls + 1
                "symbols")
            (fun () ->
                pendingQueueCalls <- pendingQueueCalls + 1
                None)
            (fun () ->
                dependsOnCalls <- dependsOnCalls + 1
                Some "depends")
            (fun () ->
                structureCalls <- structureCalls + 1
                "structure")
            (fun () ->
                fullSuiteScopeCalls <- fullSuiteScopeCalls + 1
                None)
            (fun () ->
                outstandingCalls <- outstandingCalls + 1
                false)
            (fun () ->
                sessionEvidenceCalls <- sessionEvidenceCalls + 1
                true)
            (FileChecked(fakeFileCheckResult "/src/A.fs"))

    // It still produces a key — it is a pure function of THIS file.
    test <@ key.IsSome @>

    // dependsOn is the finding; the siblings are pinned with it so a future edit cannot
    // quietly re-hoist one of them instead.
    test <@ dependsOnCalls = 0 @>
    test <@ pendingQueueCalls = 0 @>
    test <@ changedSymbolsCalls = 0 @>
    test <@ fullSuiteScopeCalls = 0 @>
    test <@ outstandingCalls = 1 @>
    test <@ sessionEvidenceCalls = 0 @>
    // AUTOMATION-303's structure hash is a FULL-REPO WALK plus a SHA-256 of every
    // project file. Paid once per BuildCompleted it is nothing; paid once per checked
    // file on a cold scan it is quadratic. The POSITIVE CONTROL for this zero is the
    // BuildCompleted test below, which pins the same thunk at 3 calls — an absence over
    // a thunk nothing ever calls would be worth nothing.
    test <@ structureCalls = 0 @>

[<Fact(Timeout = 10000)>]
let ``a prior test failure makes FileChecked uncacheable so it cannot relabel the fresh red`` () =
    let key =
        cacheKeyFor
            (fun () -> "symbols")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "structure")
            (fun () -> None)
            (fun () -> true)
            (fun () -> true)
            (FileChecked(fakeFileCheckResult "/src/TestHelper.fs"))

    test <@ key.IsNone @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: a BuildCompleted key DOES read the dependsOn + symbol state`` () =
    // The other half: thunking must not have made the salt vanish on the arm it exists for.
    let mutable dependsOnCalls = 0
    let mutable changedSymbolsCalls = 0
    let mutable structureCalls = 0

    let keyWith (dependsOn: string option) =
        cacheKeyFor
            (fun () ->
                changedSymbolsCalls <- changedSymbolsCalls + 1
                "symbols")
            (fun () -> None)
            (fun () ->
                dependsOnCalls <- dependsOnCalls + 1
                dependsOn)
            (fun () ->
                structureCalls <- structureCalls + 1
                "structure")
            (fun () -> None)
            (fun () -> false)
            (fun () -> true)
            (BuildCompleted BuildSucceeded)

    let salted = keyWith (Some "migration-hash-v1")
    let resalted = keyWith (Some "migration-hash-v2")
    let unsalted = keyWith None

    test <@ dependsOnCalls = 3 @>
    test <@ changedSymbolsCalls = 3 @>
    // THE POSITIVE CONTROL for the `structureCalls = 0` assertion above: the same thunk
    // IS read on the arm it exists for, once per key.
    test <@ structureCalls = 3 @>

    // Editing a matched file moves the key: a miss, so a genuine re-run.
    test <@ salted <> resalted @>
    // No dependsOn omits the entry entirely, so the key differs from any salted one.
    test <@ unsalted <> salted @>

[<Fact(Timeout = 10000)>]
let ``dependsOnGlobToRegex: ** crosses dirs, * does not, literals match exactly`` () =
    let m (glob: string) (rel: string) =
        (dependsOnGlobToRegex glob).IsMatch(rel)

    // ** crosses directory separators (including zero segments).
    test <@ m "src/Migrations/**" "src/Migrations/001.sql" @>
    test <@ m "src/Migrations/**" "src/Migrations/sub/002.sql" @>
    test <@ m "src/Migrations/**" "src/Migrations" @>
    test <@ not (m "src/Migrations/**" "src/Other/001.sql") @>
    // single * stays within a path segment.
    test <@ m "src/*.sql" "src/a.sql" @>
    test <@ not (m "src/*.sql" "src/sub/a.sql") @>
    // ? matches exactly one non-separator char.
    test <@ m "v?.sql" "v1.sql" @>
    test <@ not (m "v?.sql" "v12.sql") @>
    // case-insensitive (matches the IgnoreCase option).
    test <@ m "SRC/**" "src/x.sql" @>

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-5: a failed test run is not cached, so a later run on the same key is a miss and reports green`` () =
    // The same rule as the unit test above, end to end against the real task cache. The
    // replay happens at the framework level BEFORE Update runs, keyed by the
    // BuildCompleted merkle, so the load-bearing assertion is that after a FAILING cycle
    // no entry exists under that key.
    //
    // Cycle 2 is driven by `run-tests`, not BuildCompleted: a warm BuildCompleted with no
    // changed symbols takes the impact skip path and never re-executes.
    withTempDir "tp-fail-not-cached" (fun tmpDir ->
        let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
        let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cacheIface)

        let flag = Path.Combine(tmpDir, "fail")

        let configs =
            [ { Project = "FlipProj"
                Command = "sh"
                Args = $"-c \"if [ -f {flag} ]; then exit 1; else exit 0; fi\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let key: FsHotWatch.TaskCache.CompositeKey = { Plugin = "test-prune"; File = None }

        // The key a GREEN run would be written under, computed from the pure `cacheKeyFor`
        // rather than the live handler: the handler correctly refuses a BuildCompleted key
        // until this process has test evidence (AUTOMATION-161), and the question here is
        // whether a FAILING outcome was written under the key a passing one would use.
        // Same merkle terms the plugin's own thunks feed in at this point.
        let computedKey =
            (cacheKeyFor
                (fun () -> FsHotWatch.CheckCache.sha256Hex "")
                (fun () -> None)
                (fun () -> None)
                // The same structure the live handler sees: the plugin hashes its own
                // repoRoot, and this test's tmpDir holds no project files.
                (fun () -> projectStructureHash tmpDir)
                (fun () -> None)
                (fun () -> false)
                (fun () -> true)
                (BuildCompleted BuildSucceeded))
                .Value

        // Cycle 1: FAIL (a cold BuildCompleted runs the suite).
        File.WriteAllText(flag, "")
        let await1 = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await1.Wait(TimeSpan.FromSeconds 12.0) |> ignore

        match host.GetStatus("test-prune") with
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"cycle 1 expected Failed status, got %A{other}")

        test <@ host.HasFailingReasons(warningsAreFailures = true) @>

        // No entry under the key, so the next matching BuildCompleted is a guaranteed miss.
        test <@ (cacheIface.TryGet key computedKey).IsNone @>

        // Cycle 2: PASS.
        File.Delete(flag)
        let await2 = beginAwaitNextTerminal host "test-prune"
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        await2.Wait(TimeSpan.FromSeconds 12.0) |> ignore

        // Green, and the cycle-1 red is cleared rather than replayed.
        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"cycle 2 expected Completed (green) status, got %A{other}")

        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>
        test <@ host.GetErrorsByPlugin("test-prune") |> Map.isEmpty @>)

// ``run summary names the slowest project when 2+ projects ran`` lives in
// FsHotWatch.IntegrationTests: it spawns two real sh subprocesses with a 1-second sleep
// dependency, and its terminal-wait window starves under heavy parallel test load.

[<Fact(Timeout = 15000)>]
let ``run summary omits slowest when only 1 project ran`` () =
    withTempDir "tp-no-slowest" (fun tmpDir ->
        let host, _ = withSingleProjectHarness tmpDir "OnlyProj"

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        let history = host.GetHistory("test-prune")
        let lastRun = history |> List.last

        match lastRun.Summary with
        | Some s -> test <@ not (s.Contains("slowest")) @>
        | None -> failwith "expected summary on completed run")

[<Fact(Timeout = 15000)>]
let ``test-results JSON exposes per-project elapsedMs after a successful run`` () =
    withTempDir "tp-elapsed-capture" (fun tmpDir ->
        let configs =
            [ { Project = "TimedProj"
                Command = "sh"
                Args = "-c \"sleep 0.12\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        let json = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
        test <@ json.IsSome @>

        let doc = JsonDocument.Parse(json.Value)
        let projects = doc.RootElement.GetProperty("projects")
        Assert.Equal(1, projects.GetArrayLength())
        let proj = projects.[0]
        Assert.Equal("TimedProj", proj.GetProperty("project").GetString())
        Assert.Equal("passed", proj.GetProperty("status").GetString())
        let elapsedMs = proj.GetProperty("elapsedMs").GetDouble()
        test <@ elapsedMs >= 100.0 @>)

[<Fact(Timeout = 15000)>]
let ``executeTests runs project on BuildSucceeded`` () =
    // BuildSucceeded means artifacts are guaranteed fresh — BuildPlugin owns that
    // verification (`verifyArtifactsFresh`). TestPrune does not second-guess the signal,
    // so it never emits "binary is stale" warnings of its own.
    withTempDir "tp-runs-on-build-succeeded" (fun tmpDir ->
        let host, sentinel = withSingleProjectHarness tmpDir "TestProj"

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        test <@ File.Exists sentinel @>

        let staleWarning =
            host.GetErrorsByPlugin("test-prune")
            |> Map.toList
            |> List.collect snd
            |> List.exists (fun e -> e.Severity = FsHotWatch.ErrorLedger.Warning && e.Message.Contains("stale"))

        test <@ not staleWarning @>)

[<Fact(Timeout = 25000)>]
let ``AUTOMATION-161: a cold-start BuildCompleted must NOT replay a test result from the task cache`` () =
    // Asserting the opposite here — that session 2 must NOT re-create the sentinel because
    // the cached entry replays — is asserting the bug, and it passed for its whole life. A
    // replay SKIPS the handler, so no `TestsFinished` lands and `LastCoverage` stays empty:
    // the plugin then tells `test-scope` (and `.fshw/verdict.json`) that NO TESTS RAN while
    // its own status line says "1 passed (cached)", and both `check` and `confirm` exit 3
    // on a green tree.
    //
    // The key does not prove the inputs are identical. On a cold scan BuildCompleted is
    // dispatched BEFORE the FCS pass, so `changed-symbols` is empty whatever the tree
    // holds; what makes the entry sound in a WARM daemon is the symbol-diff pipeline that
    // runs after it, and a new process has no such run to supersede the replay with. So a
    // process may not assert a test result it has no record of running.
    withTempDir "tp-cold-cache-replay" (fun tmpDir ->
        let taskCache =
            FsHotWatch.FileTaskCache.FileTaskCache(Path.Combine(tmpDir, "task-cache"))
            :> FsHotWatch.TaskCache.ITaskCache

        let sentinel = Path.Combine(tmpDir, "ran")

        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = $"-c \"touch {sentinel}\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        do
            let dbPath1 = Path.Combine(tmpDir, "tp1.db")
            let host1 = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = taskCache)

            let handler1 = create dbPath1 tmpDir (Some configs) None None None None []

            host1.RegisterHandler(handler1)
            host1.EmitBuildCompleted(BuildSucceeded)
            waitForTerminalStatus host1 "test-prune" 10000

        if File.Exists sentinel then
            File.Delete sentinel

        // Session 2 is a new plugin instance — a daemon restart, or a fresh `--run-once` —
        // over the SAME on-disk cache and the SAME tree.
        let dbPath2 = Path.Combine(tmpDir, "tp2.db")
        let host2 = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = taskCache)

        let handler2 = create dbPath2 tmpDir (Some configs) None None None None []

        host2.RegisterHandler(handler2)

        host2.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host2 "test-prune" 10000

        // The run happened ...
        test <@ File.Exists sentinel @>

        // ... and the plugin SAYS so. `scope: none` here is the release blocker: `confirm`
        // reads it and refuses ("NO TESTS RAN") on a tree the same plugin's status line is
        // simultaneously calling green.
        let scope = host2.RunCommand("test-scope", [||]) |> Async.RunSynchronously
        test <@ scope.IsSome && scope.Value.Contains "\"scope\":\"full\"" @>)
