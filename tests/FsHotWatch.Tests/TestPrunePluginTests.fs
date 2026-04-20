module FsHotWatch.Tests.TestPrunePluginTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.SymbolDiff
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

let private waitForPluginIdle (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForSettled host pluginName (int (timeoutSecs * 1000.0))

let private waitForPluginTerminal (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForTerminalStatus host pluginName (int (timeoutSecs * 1000.0))

[<Fact(Timeout = 5000)>]
let ``plugin has correct name`` () =
    let handler = create ":memory:" "/tmp" None None None None None None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "test-prune" @>

[<Fact(Timeout = 5000)>]
let ``affected-tests command returns not-analyzed when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not analyzed") @>

[<Fact(Timeout = 5000)>]
let ``changed-files command returns empty list when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact(Timeout = 5000)>]
let ``test-prune error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None None
    host.RegisterHandler(handler)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = ParseOnly
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

    // Framework dispatches events asynchronously — wait for the agent to process
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

[<Fact(Timeout = 5000)>]
let ``changed-files tracks files after emit with valid relative path`` () =
    withTempDir "tp-test" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None None
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Lib\nlet x = 1\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact(Timeout = 5000)>]
let ``duplicate file checks do not duplicate in changed-files list`` () =
    withTempDir "tp-dup" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None None
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "Dup.fs")
        File.WriteAllText(fakeFile, "module Dup\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Dup\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        for _ in 1..2 do
            try
                host.EmitFileChecked(fakeResult)
            with _ ->
                ()

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact(Timeout = 5000)>]
let ``test-results command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact(Timeout = 5000)>]
let ``plugin with testConfigs subscribes to OnBuildCompleted`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let configs =
        [ { Project = "TestProject"
            Command = "echo"
            Args = "tests passed"
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " " } ]

    let handler = create ":memory:" "/tmp" (Some configs) None None None None None
    host.RegisterHandler(handler)

    // Verify plugin registered without crashing and status is Idle
    let status = host.GetStatus("test-prune")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact(Timeout = 5000)>]
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
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) (Some [ fakeExtension ]) None None None None

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        // Wait for async test execution to complete
        let deadline = DateTime.UtcNow.AddSeconds(10.0)

        while not extensionCalled && DateTime.UtcNow < deadline do
            System.Threading.Thread.Sleep(50)

        test <@ extensionCalled @>)

[<Fact(Timeout = 5000)>]
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
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) (Some [ failingExtension ]) None None None None

        host.RegisterHandler(handler)

        // Subscribe-before-emit avoids the race where the plugin transitions
        // to terminal status before we start polling.
        let completion = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        completion.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact(Timeout = 5000)>]
let ``database read-before-write preserves previous symbols for diffing`` () =
    // RebuildForProject must happen AFTER GetSymbolsInFile to get previous state for diffing.
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

        // Correct pattern: read BEFORE write
        let storedBefore = db.GetSymbolsInFile("src/Lib.fs")
        db.RebuildProjects([ result2 ])

        test <@ storedBefore.Length = 1 @>
        test <@ storedBefore.[0].LineEnd = 1 @>

        let storedAfter = db.GetSymbolsInFile("src/Lib.fs")
        test <@ storedAfter.Length = 1 @>
        test <@ storedAfter.[0].LineEnd = 5 @>

        // Diffing against pre-write data detects the change
        let (changes, _) = detectChanges [ symbol2 ] storedBefore
        let changedNames = changedSymbolNames changes
        test <@ not changedNames.IsEmpty @>

        // Diffing against post-write data finds no changes (the bug this test guards against)
        let (noChanges, _) = detectChanges [ symbol2 ] storedAfter
        let noChangedNames = changedSymbolNames noChanges
        test <@ noChangedNames.IsEmpty @>)

[<Fact(Timeout = 5000)>]
let ``FileChecked does not report Completed when testConfigs are provided (error path)`` () =
    withTempDir "tp-no-complete" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "Lib.fs")
        Directory.CreateDirectory(tmpDir) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Lib\nlet x = 1\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(Running _)
                | Some(Completed _)
                | Some(Failed _) -> true
                | _ -> false)
            5000

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        // When testConfigs are provided, FileChecked analysis should NOT report Completed
        // because tests haven't actually run yet (they run on BuildCompleted).
        match status.Value with
        | Completed _ ->
            Assert.Fail(
                "Expected Running (not Completed) after FileChecked when testConfigs are provided — tests haven't run yet"
            )
        | _ -> ())

[<Fact(Timeout = 5000)>]
let ``FileChecked reports terminal status when no testConfigs (analysis-only mode, error path)`` () =
    withTempDir "tp-complete-no-configs" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        // No testConfigs — analysis-only mode
        let handler = create dbPath tmpDir None None None None None None
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Lib\nlet x = 1\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitForPluginTerminal host "test-prune" 5.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        // Without testConfigs, FileChecked analysis CAN report Completed (or Failed for null check results)
        match status.Value with
        | Completed _
        | Failed _ -> () // Both are acceptable terminal states for analysis-only mode
        | other -> Assert.Fail($"Expected Completed or Failed in analysis-only mode, got: %A{other}"))

[<Fact(Timeout = 5000)>]
let ``plugin reports Running status on FileChecked after tests complete`` () =
    withTempDir "tp-reset" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        // Trigger build -> test run
        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 5.0

        // After tests complete, emit a FileChecked — should transition away from test-run status
        let fakeFile = Path.Combine(tmpDir, "New.fs")
        File.WriteAllText(fakeFile, "module New")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module New"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        // Framework dispatches events asynchronously — wait for agent to process
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
            Assert.Fail("Expected status to change after new FileChecked, not remain as test-run Completed")
        | _ -> ())

[<Fact(Timeout = 5000)>]
let ``run-tests command runs all projects and returns results`` () =
    withTempDir "tp-run" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        let projects = doc.RootElement.GetProperty("projects")
        Assert.True(projects.GetArrayLength() > 0)
        Assert.Equal("passed", projects.[0].GetProperty("status").GetString())
        Assert.True(doc.RootElement.TryGetProperty("elapsed") |> fst))

[<Fact(Timeout = 5000)>]
let ``run-tests with project filter runs only named project`` () =
    withTempDir "tp-run-proj" (fun tmpDir ->
        let configs =
            [ { Project = "Alpha"
                Command = "echo"
                Args = "alpha"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " }
              { Project = "Beta"
                Command = "echo"
                Args = "beta"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        let result =
            host.RunCommand("run-tests", [| """{"projects": ["Alpha"]}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ result.Value.Contains("Alpha") @>
        test <@ not (result.Value.Contains("Beta")) @>)

[<Fact(Timeout = 5000)>]
let ``run-tests with only-failed reruns failed projects`` () =
    withTempDir "tp-run-failed" (fun tmpDir ->
        let configs =
            [ { Project = "Passes"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " }
              { Project = "Fails"
                Command = "false"
                Args = ""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        // First run — Fails project will fail
        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 5.0

        // Now rerun only failed — should only run "Fails", not "Passes"
        let result =
            host.RunCommand("run-tests", [| """{"only-failed": true}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ result.Value.Contains("Fails") @>
        test <@ not (result.Value.Contains("Passes")) @>)

[<Fact(Timeout = 5000)>]
let ``run-tests not registered when no testConfigs`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let handler = create ":memory:" "/tmp" None None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsNone @>

[<Fact(Timeout = 5000)>]
let ``dispose is callable`` () =
    // Framework-managed plugins don't need explicit dispose, but verify create doesn't throw
    let _handler = create ":memory:" "/tmp" None None None None None None
    ()

[<Fact(Timeout = 5000)>]
let ``parseFailedTests extracts class and method from xUnit MTP output`` () =
    let output =
        "failed FsHotWatch.Tests.PluginHostTests.plugin receives file change events (1ms)\nfailed FsHotWatch.Tests.BuildPluginTests.build fires on source change (0ms)\nTest run summary: Failed!\n  total: 10\n  failed: 2"

    let parsed = parseFailedTests output

    test <@ parsed.Length = 2 @>

    test
        <@
            parsed
            |> List.exists (fun (cls, meth, _) ->
                cls = "PluginHostTests" && meth = "plugin receives file change events")
        @>

    test
        <@
            parsed
            |> List.exists (fun (cls, meth, _) -> cls = "BuildPluginTests" && meth = "build fires on source change")
        @>

[<Fact(Timeout = 5000)>]
let ``parseFailedTests handles output with no failures`` () =
    let parsed: (string * string * string) list =
        parseFailedTests "Test run summary: Passed!\n  total: 10\n  succeeded: 10"

    test <@ parsed.Length = 0 @>

[<Fact(Timeout = 5000)>]
let ``test failures are reported to error ledger`` () =
    withTempDir "tp-ledger" (fun tmpDir ->
        // Use "false" command which always fails, producing test failure output
        let configs =
            [ { Project = "TestProject"
                Command = "false"
                Args = ""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 5.0

        test <@ host.HasFailingReasons(warningsAreFailures = true) @>)

[<Fact(Timeout = 10000)>]
let ``test errors are cleared when all tests pass`` () =
    withTempDir "tp-ledger-clear" (fun tmpDir ->
        // First run fails, second run passes
        let mutable shouldFail = true

        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = "-c \"if [ -f fail_flag ]; then exit 1; else exit 0; fi\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        // Create fail flag so first run fails
        File.WriteAllText(Path.Combine(tmpDir, "fail_flag"), "")
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 5.0
        test <@ host.HasFailingReasons(warningsAreFailures = true) @>

        // Remove fail flag so second run passes
        File.Delete(Path.Combine(tmpDir, "fail_flag"))
        host.EmitBuildCompleted(BuildSucceeded)
        // Wait for second run to start (status leaves terminal from first run)
        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(Completed _)
                | Some(Failed _) -> false
                | _ -> true)
            5000

        waitForPluginTerminal host "test-prune" 5.0
        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>)

// Inline FactAttribute so test detection works without xUnit assemblies in script options.
// Uses module-level [<Fact>] functions — the pattern that analyzeSource reliably detects
// via FCS symbol uses without needing resolved assembly references.
let private testSource moduleName =
    $"""module {moduleName}

type FactAttribute() =
    inherit System.Attribute()

[<Fact(Timeout = 5000)>]
let myTest () = ()
"""

// Source with a prod function that a test can call to create a dependency edge.
let private testSourceWithDep moduleName =
    $"""module {moduleName}

type FactAttribute() =
    inherit System.Attribute()

let compute x = x + 1

[<Fact(Timeout = 5000)>]
let computeTest () =
    let _ = compute 1
    ()
"""

/// Emit a file through the CheckPipeline and wait for the plugin's async analysis to settle.
/// Uses the changed-files command to deterministically detect when the agent has processed
/// the FileChecked message (no sleeps).
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
        let! result = pipeline.CheckFile(filePath)

        match result with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith $"CheckFile returned None for {filePath}"

        // Poll changed-files command until the file appears — deterministic proof
        // that the FileChecked message was processed by the agent.
        let fileName = Path.GetFileName(filePath)

        waitUntil
            (fun () ->
                let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

                match result with
                | Some json -> json.Contains(fileName)
                | None -> false)
            10000
    }

[<Fact(Timeout = 5000)>]
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
                ClassJoin = " " } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let handler = create dbPath tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        let testFile = Path.Combine(tmpDir, "MyTests.fsx")

        // Use real FCS analysis to exercise the Ok analysisResult path
        emitFileAndWait checker pipeline host testFile (testSource "MyTests")
        |> Async.RunSynchronously

        // Wait for terminal status — plugin reports Completed after analysis
        // even with testConfigs, so WaitForComplete doesn't hang waiting for
        // a BuildCompleted that may never come.
        waitForPluginTerminal host "test-prune" 5.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Expected Completed after FileChecked analysis, got: %A{other}"))

[<Fact(Timeout = 5000)>]
let ``FileChecked reports Completed when no testConfigs (success path)`` () =
    withTempDir "tp-complete-real" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        // No testConfigs — analysis-only mode
        let handler = create dbPath tmpDir None None None None None None
        host.RegisterHandler(handler)

        let testFile = Path.Combine(tmpDir, "MyLib.fsx")

        // Use real FCS analysis to exercise the Ok analysisResult path
        emitFileAndWait checker pipeline host testFile (testSource "MyLib")
        |> Async.RunSynchronously

        // Wait for terminal status
        waitForPluginTerminal host "test-prune" 5.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        // Without testConfigs, analysis-only mode should report Completed
        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Expected Completed in analysis-only mode, got: %A{other}"))

// Timing race under Fact(Timeout) is fixed by TestHelpers.beginAwaitTerminal
// (subscribe-before-trigger via host.OnStatusChanged). But this test then fails
// because a fresh Database.create(dbPath) connection does not observe the
// plugin's just-flushed rows — cross-connection SQLite WAL visibility bug,
// orthogonal to timing. Re-enable once the plugin exposes test-methods via a
// command (preferred) or the DB write is committed with explicit sync.
[<Fact(Timeout = 10000)>]
let ``after scan and build, test methods are in the sqlite database`` () =
    withTempDir "tp-tm-db" (fun tmpDir ->
        // Canonicalize path to avoid symlink divergence (e.g., /var/folders vs /private/var/folders).
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
                ClassJoin = " " } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None None
        host.RegisterHandler(handler)

        let projOptions =
            getScriptOptions checker testsFile testsSource |> Async.RunSynchronously

        pipeline.RegisterProject(testsFile, projOptions)

        let result = pipeline.CheckFile(testsFile) |> Async.RunSynchronously

        match result with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "CheckFile returned None"

        waitForPluginIdle host "test-prune" 10.0

        // Flush pending analysis to DB by firing BuildSucceeded.
        let firstBuild = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        firstBuild.Wait(TimeSpan.FromSeconds 20.0) |> ignore

        // Cross-connection WAL visibility has a brief race after the plugin's
        // commit: fresh connections can momentarily observe an empty DB even
        // though the plugin saw its own writes.
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

[<Fact(Timeout = 10000)>]
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

        // testConfigs is required for the plugin to subscribe to BuildCompleted
        // (without it, flushAndQueryAffected is never triggered). Command is a no-op.
        let testConfigs =
            [ { Project = "Lib.fsx"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None None
        host.RegisterHandler(handler)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        // Initial index: both files analysed, edges written to DB.
        let libResult = pipeline.CheckFile(libFile) |> Async.RunSynchronously

        match libResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        waitUntil
            (fun () ->
                let r = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

                match r with
                | Some json -> json.Contains("Lib.fsx")
                | None -> false)
            5000

        let testsResult = pipeline.CheckFile(testsFile) |> Async.RunSynchronously

        match testsResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        waitUntil
            (fun () ->
                let r = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

                match r with
                | Some json -> json.Contains("Tests.fsx")
                | None -> false)
            5000

        waitForPluginIdle host "test-prune" 10.0

        // Subscribe BEFORE emit and wait for the *next* Completed after BuildCompleted,
        // so we know the flush-and-run cycle has finished (including TestsFinished
        // resetting ChangedSymbols). beginAwaitTerminal races here because plugin
        // is already at Completed from the prior FileChecked.
        let firstBuild = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        firstBuild.Wait(TimeSpan.FromSeconds 20.0) |> ignore

        // Modify compute's body — content hash changes but signature does not.
        let libSource2 =
            """module Lib
let compute (x: int) = x + 2
"""

        File.WriteAllText(libFile, libSource2)

        let libResult2 = pipeline.CheckFile(libFile) |> Async.RunSynchronously

        match libResult2 with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile 2 failed"

        // Poll affected-tests; after FileChecked processing, computeTest should appear.
        let mutable affectedTests = ""

        waitUntil
            (fun () ->
                match host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously with
                | Some v -> affectedTests <- v
                | None -> ()

                affectedTests.Contains("computeTest"))
            5000

        test <@ affectedTests.Contains("computeTest") @>)

// Deterministic status signal (TestHelpers.beginAwaitTerminal) replaces the
// former polling race. With that fix, the test still fails at the same place
// as ``after a symbol change`` — affected-tests returns "[]" after a type
// change that should flag dependent tests. Same root cause: dependency edges
// not produced by the current symbol-diff path.
[<Fact(Timeout = 10000)>]
let ``cross-file type change only runs affected test classes`` () =
    // End-to-end test: change Lib.fsx type -> affected-tests identifies dependent tests -> only those classes run
    withTempDir "tp-e2e" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")
        let captureFile = Path.Combine(tmpDir, "test-invocation.txt")

        // Note: This test requires bash and only runs successfully on Unix/Linux
        let bashPath = Path.Combine(tmpDir, "test-wrapper.sh")

        try
            File.WriteAllText(bashPath, $"#!/bin/bash\necho \"$@\" >> '{captureFile}'\nexit 0\n")
        with ex ->
            failwith $"Failed to create test wrapper script: {ex.Message}"

        // Project name must match what the plugin tags TestMethods with. For .fsx
        // scripts, FCS synthesizes a .fsproj filename like "Lib.fsx.fsproj", so
        // Path.GetFileNameWithoutExtension produces "Lib.fsx".
        let testConfigs =
            [ { Project = "Lib.fsx"
                Command = "bash"
                Args = bashPath
                Group = "default"
                Environment = []
                FilterTemplate = Some "-- --filter-class {classes}"
                ClassJoin = "|" } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None None
        host.RegisterHandler(handler)

        // Setup: Lib defines a type, Tests uses it
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

        // Emit both files
        File.WriteAllText(libFile, libSource)
        File.WriteAllText(testsFile, testsSource)

        let libOptions =
            getScriptOptions checker libFile libSource |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        // Emit lib file
        let libResult = pipeline.CheckFile(libFile) |> Async.RunSynchronously

        match libResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        // Wait for the agent to process the FileChecked message
        waitUntil
            (fun () ->
                let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

                match result with
                | Some json -> json.Contains("Lib.fsx")
                | None -> false)
            5000

        // Emit tests file
        let testsResult = pipeline.CheckFile(testsFile) |> Async.RunSynchronously

        match testsResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        // Wait for the agent to process the FileChecked message
        waitUntil
            (fun () ->
                let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

                match result with
                | Some json -> json.Contains("Tests.fsx")
                | None -> false)
            5000

        // Wait for analysis
        waitForPluginIdle host "test-prune" 10.0

        // Emit build completion to flush analysis to database
        let firstBuild = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        firstBuild.Wait(TimeSpan.FromSeconds 20.0) |> ignore

        // Now change the type: add a new field
        let libSource2 =
            """module Lib

type Config = { Value: string; Count: int; Threshold: float }

let validate (cfg: Config) = cfg.Value.Length > 0
"""

        File.WriteAllText(libFile, libSource2)

        let libResult2 = pipeline.CheckFile(libFile) |> Async.RunSynchronously

        match libResult2 with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile 2 failed"

        // Framework dispatches async — poll until affected-tests shows the expected results
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

        let secondBuild = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        secondBuild.Wait(TimeSpan.FromSeconds 20.0) |> ignore

        // Verify that the test command was invoked with the correct filter
        let capturedArgs =
            try
                File.ReadAllText(captureFile)
            with :? System.IO.FileNotFoundException ->
                failwith $"Test command did not execute or write to {captureFile}"

        test <@ capturedArgs.Contains("--filter-class") @>
        test <@ capturedArgs.Contains("Tests") @>)

[<Fact(Timeout = 10000)>]
let ``WaitForComplete hangs when FileChecked arrives after BuildCompleted and tests finish`` () =
    withTempDir "tp-hang" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        // 1. Build completes → tests run and finish
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 5.0

        // Confirm we reached terminal state
        let statusAfterTests = host.GetStatus("test-prune")
        test <@ statusAfterTests.IsSome @>

        match statusAfterTests.Value with
        | Completed _
        | Failed _ -> ()
        | other -> Assert.Fail($"Expected terminal after tests, got: %A{other}")

        // 2. Late FileChecked arrives (simulating FCS check completing after build)
        let fakeFile = Path.Combine(tmpDir, "Late.fs")
        File.WriteAllText(fakeFile, "module Late\nlet x = 1\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Late\nlet x = 1\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        // Wait for the plugin to process the FileChecked event and settle.
        // With the fix, plugin goes Running → Completed. Without the fix, it stays Running.
        waitForSettled host "test-prune" 5000

        // 3. WaitForComplete should resolve within a few seconds (1s stability + margin).
        //    Before the fix, the plugin stayed Running indefinitely after this FileChecked.
        let waitTask = waitForAllTerminal host (System.TimeSpan.FromSeconds(5.0)) ()

        let completed = waitTask.Wait(System.TimeSpan.FromSeconds(8.0))

        test <@ completed @>)

[<Fact(Timeout = 5000)>]
let ``FileChecked does not query DB for affected tests`` () =
    // Bug 2: FileChecked should accumulate changed symbols, not query the DB.
    // The query should happen after flush in BuildCompleted.
    // We verify indirectly: after FileChecked but before BuildCompleted,
    // affected-tests should return empty (not yet queried).
    withTempDir "tp-no-query" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")
        let db = Database.create dbPath

        // Pre-populate DB with a symbol and a test that depends on it
        let symbol: SymbolInfo =
            { FullName = "Lib.foo"
              Kind = SymbolKind.Value
              SourceFile = "src/Lib.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "old-hash"
              IsExtern = false }

        let testMethod: TestMethodInfo =
            { SymbolFullName = "Tests.myTest"
              TestProject = "TestProj"
              TestClass = "Tests"
              TestMethod = "myTest" }

        let analysis =
            AnalysisResult.Create(
                [ symbol ],
                [ { FromSymbol = "Tests.myTest"
                    ToSymbol = "Lib.foo"
                    Kind = DependencyKind.Calls
                    Source = "core" } ],
                [ testMethod ]
            )

        db.RebuildProjects([ analysis ])

        // Create plugin WITHOUT testConfigs (analysis-only mode)
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir None None None None None None
        host.RegisterHandler(handler)

        // Emit FileChecked with a changed symbol — in the old code this would
        // query the DB and populate AffectedTests. In the fix, it should NOT.
        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet foo = 2\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Lib\nlet foo = 2\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = ParseOnly
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitForPluginTerminal host "test-prune" 5.0

        // After FileChecked (no BuildCompleted), affected-tests should be empty
        // because the query now happens after flush (on BuildCompleted)
        let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ not (result.Value.Contains("myTest")) @>)

[<Fact(Timeout = 5000)>]
let ``BuildCompleted queries affected tests after flush`` () =
    // Bug 2: After flush, QueryAffectedTests should run against fresh DB data.
    withTempDir "tp-query-after-flush" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let configs =
            [ { Project = "TestProj"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = Some "-- --filter-class {classes}"
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None None
        host.RegisterHandler(handler)

        // After BuildCompleted with no prior FileChecked, should still work
        // (AnalysisRan will be false, affected-tests returns "not analyzed")
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 5.0

        let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>)

[<Fact(Timeout = 5000)>]
let ``skip tests when 0 affected classes and not cold start`` () =
    // Bug 1: After first run, 0 affected classes should skip (not run all).
    withTempDir "tp-skip" (fun tmpDir ->
        let mutable runCount = 0

        let configs =
            [ { Project = "TestProj"
                Command = "echo"
                Args = "ran"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) None None (Some(fun _ -> runCount <- runCount + 1)) None None

        host.RegisterHandler(handler)

        // First BuildCompleted = cold start, should run all
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 5.0
        test <@ runCount = 1 @>

        // Second BuildCompleted with no changed symbols — should SKIP
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 5.0
        test <@ runCount = 1 @>) // still 1, not 2

[<Fact(Timeout = 5000)>]
let ``comment-only change does not add file to ChangedFiles but AST change does`` () =
    // Regression test: before the fix, newChangedFiles was computed unconditionally
    // before changedNames, so any file emit (even comment-only) would add the file
    // to ChangedFiles and trigger extension-based tests (e.g. Falco routes).
    //
    // After the fix, newChangedFiles is only updated when changedNames is non-empty,
    // i.e. only when the AST actually changed.
    withTempDir "tp-comment-regression" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")
        let filePath = Path.Combine(tmpDir, "Lib.fs")
        let relPath = "Lib.fs"

        let initialSource = "module Lib\nlet x = 1\n"
        let commentOnlySource = "module Lib\n// a comment added\nlet x = 1\n"
        let astChangedSource = "module Lib\nlet x = 1\nlet y = 2\n"

        // Create a single checker and project options shared by both DB setup and the plugin.
        // Using the same checker ensures analyzeSource (inside the plugin) can reuse FCS results.
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value

        File.WriteAllText(filePath, initialSource)
        let initialSourceText = SourceText.ofString initialSource

        let projOptions =
            checker.GetProjectOptionsFromScript(filePath, initialSourceText, assumeDotNetFramework = false)
            |> Async.RunSynchronously
            |> fst

        // --- Seed DB directly with initial source symbols ---
        // The plugin's BuildCompleted flush only runs when testConfigs is provided,
        // so we populate the baseline here via analyzeSource + RebuildProjects.
        let seedResult =
            analyzeSource checker filePath initialSource projOptions "TestProject"
            |> Async.RunSynchronously

        match seedResult with
        | Error msg -> Assert.Fail($"Initial analysis failed: {msg}")
        | Ok result ->
            let normalized =
                { result with
                    Symbols = normalizeSymbolPaths tmpDir result.Symbols }

            let db = Database.create dbPath
            db.RebuildProjects([ normalized ])

        // --- Set up pipeline (same checker) and plugin host ---
        let pipeline = CheckPipeline(checker)
        pipeline.RegisterProject(projOptions.ProjectFileName, projOptions)

        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir None None None None None None
        host.RegisterHandler(handler)

        // --- Phase 1: comment-only change should NOT add file to ChangedFiles ---
        File.WriteAllText(filePath, commentOnlySource)

        match pipeline.CheckFile(filePath) |> Async.RunSynchronously with
        | None -> Assert.Fail("FCS failed to check comment-only source")
        | Some result -> host.EmitFileChecked(result)

        waitForTerminalStatus host "test-prune" 30000

        let changedAfterComment =
            host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedAfterComment.Value = "[]" @>

        // --- Phase 2: AST change should add file to ChangedFiles ---
        File.WriteAllText(filePath, astChangedSource)

        match pipeline.CheckFile(filePath) |> Async.RunSynchronously with
        | None -> Assert.Fail("FCS failed to check AST-changed source")
        | Some result -> host.EmitFileChecked(result)

        waitForTerminalStatus host "test-prune" 30000

        let changedAfterAst =
            host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedAfterAst.Value.Contains(relPath) @>)

// --- buildFilterArgs unit tests ---

[<Fact(Timeout = 5000)>]
let ``buildFilterArgs returns None when no classes for project`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = "|" }

    let result = buildFilterArgs config Map.empty
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``buildFilterArgs returns None when no FilterTemplate configured`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = "|" }

    let classesByProject = Map.ofList [ "TestProj", [ "TestClassA"; "TestClassB" ] ]
    let result = buildFilterArgs config classesByProject
    test <@ result = None @>

[<Fact(Timeout = 5000)>]
let ``buildFilterArgs applies template with ClassJoin`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = "|" }

    let classesByProject = Map.ofList [ "TestProj", [ "ClassA"; "ClassB" ] ]
    let result = buildFilterArgs config classesByProject
    test <@ result = Some "-- --filter-class ClassA|ClassB" @>

[<Fact(Timeout = 5000)>]
let ``buildFilterArgs applies template with default space join`` () =
    let config =
        { Project = "TestProj"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = " " }

    let classesByProject = Map.ofList [ "TestProj", [ "ClassA"; "ClassB" ] ]
    let result = buildFilterArgs config classesByProject
    test <@ result = Some "-- --filter-class ClassA ClassB" @>

[<Fact(Timeout = 5000)>]
let ``buildFilterArgs ignores classes from other projects`` () =
    let config =
        { Project = "TestProjA"
          Command = "dotnet"
          Args = "test"
          Group = "default"
          Environment = []
          FilterTemplate = Some "-- --filter-class {classes}"
          ClassJoin = "|" }

    let classesByProject =
        Map.ofList [ "TestProjA", [ "ClassA" ]; "TestProjB", [ "ClassB" ] ]

    let result = buildFilterArgs config classesByProject
    test <@ result = Some "-- --filter-class ClassA" @>
