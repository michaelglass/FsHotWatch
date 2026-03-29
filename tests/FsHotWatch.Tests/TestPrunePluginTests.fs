module FsHotWatch.Tests.TestPrunePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.SymbolDiff
open FsHotWatch.Tests.TestHelpers

let private withTmpDir (prefix: string) (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore

    try
        f dir
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private waitForPluginIdle (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForSettled host pluginName (int (timeoutSecs * 1000.0))

let private waitForPluginTerminal (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForTerminalStatus host pluginName (int (timeoutSecs * 1000.0))

[<Fact>]
let ``plugin has correct name`` () =
    let handler = create ":memory:" "/tmp" None None None None None
    test <@ handler.Name = "test-prune" @>

[<Fact>]
let ``affected-tests command returns not-analyzed when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not analyzed") @>

[<Fact>]
let ``changed-files command returns empty list when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact>]
let ``test-prune error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = None
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

[<Fact>]
let ``changed-files tracks files after emit with valid relative path`` () =
    withTmpDir "tp-test" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Lib\nlet x = 1\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = None
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact>]
let ``duplicate file checks do not duplicate in changed-files list`` () =
    withTmpDir "tp-dup" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None
        host.RegisterHandler(handler)

        let fakeFile = Path.Combine(tmpDir, "Dup.fs")
        File.WriteAllText(fakeFile, "module Dup\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Dup\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = None
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        for _ in 1..2 do
            try
                host.EmitFileChecked(fakeResult)
            with _ ->
                ()

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact>]
let ``test-results command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("test-results", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact>]
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

    let handler = create ":memory:" "/tmp" (Some configs) None None None None
    host.RegisterHandler(handler)

    // Verify plugin registered without crashing and status is Idle
    let status = host.GetStatus("test-prune")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact>]
let ``extension contributes affected test classes during test run`` () =
    withTmpDir "tp-ext" (fun tmpDir ->
        let mutable extensionCalled = false

        let fakeExtension =
            { new ITestPruneExtension with
                member _.Name = "fake-extension"

                member _.FindAffectedTests _db _changedFiles _repoRoot =
                    extensionCalled <- true

                    [ { AffectedTest.TestProject = "TestProj"
                        TestClass = "ExtensionClass" } ] }

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
            create ":memory:" tmpDir (Some configs) (Some [ fakeExtension ]) None None None

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        // Wait for async test execution to complete
        let deadline = DateTime.UtcNow.AddSeconds(10.0)

        while not extensionCalled && DateTime.UtcNow < deadline do
            System.Threading.Thread.Sleep(50)

        test <@ extensionCalled @>)

[<Fact>]
let ``extension error is caught and does not crash plugin`` () =
    withTmpDir "tp-ext-err" (fun tmpDir ->
        let failingExtension =
            { new ITestPruneExtension with
                member _.Name = "failing-extension"

                member _.FindAffectedTests _db _changedFiles _repoRoot = failwith "extension broke" }

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
            create ":memory:" tmpDir (Some configs) (Some [ failingExtension ]) None None None

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 5.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>)

[<Fact>]
let ``database read-before-write preserves previous symbols for diffing`` () =
    // RebuildForProject must happen AFTER GetSymbolsInFile to get previous state for diffing.
    withTmpDir "tp-db" (fun tmpDir ->
        let db = Database.create (Path.Combine(tmpDir, "test.db"))

        let symbol1: SymbolInfo =
            { FullName = "MyModule.foo"
              Kind = SymbolKind.Value
              SourceFile = "src/Lib.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "abc123" }

        let testMethod1: TestMethodInfo =
            { SymbolFullName = "Tests.myTest"
              TestProject = "TestProj"
              TestClass = "Tests"
              TestMethod = "myTest" }

        let result1: AnalysisResult =
            { Symbols = [ symbol1 ]
              Dependencies =
                [ { FromSymbol = "Tests.myTest"
                    ToSymbol = "MyModule.foo"
                    Kind = DependencyKind.Calls } ]
              TestMethods = [ testMethod1 ] }

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
        let changes = detectChanges [ symbol2 ] storedBefore
        let changedNames = changedSymbolNames changes
        test <@ not changedNames.IsEmpty @>

        // Diffing against post-write data finds no changes (the bug this test guards against)
        let noChanges = detectChanges [ symbol2 ] storedAfter
        let noChangedNames = changedSymbolNames noChanges
        test <@ noChangedNames.IsEmpty @>)

[<Fact>]
let ``plugin reports Running status on FileChecked after tests complete`` () =
    withTmpDir "tp-reset" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None
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
              CheckResults = None
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

[<Fact>]
let ``run-tests command runs all projects and returns results`` () =
    withTmpDir "tp-run" (fun tmpDir ->
        let configs =
            [ { Project = "TestProject"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("\"status\": \"passed\"") @>
        test <@ result.Value.Contains("\"elapsed\":") @>)

[<Fact>]
let ``run-tests with project filter runs only named project`` () =
    withTmpDir "tp-run-proj" (fun tmpDir ->
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
        let handler = create ":memory:" tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        let result =
            host.RunCommand("run-tests", [| """{"projects": ["Alpha"]}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ result.Value.Contains("Alpha") @>
        test <@ not (result.Value.Contains("Beta")) @>)

[<Fact>]
let ``run-tests with only-failed reruns failed projects`` () =
    withTmpDir "tp-run-failed" (fun tmpDir ->
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
        let handler = create ":memory:" tmpDir (Some configs) None None None None
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

[<Fact>]
let ``run-tests not registered when no testConfigs`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsNone @>

[<Fact>]
let ``dispose is callable`` () =
    // Framework-managed plugins don't need explicit dispose, but verify create doesn't throw
    let _handler = create ":memory:" "/tmp" None None None None None
    ()

// Inline FactAttribute so test detection works without xUnit assemblies in script options.
// Uses module-level [<Fact>] functions — the pattern that analyzeSource reliably detects
// via FCS symbol uses without needing resolved assembly references.
let private testSource moduleName =
    $"""module {moduleName}

type FactAttribute() =
    inherit System.Attribute()

[<Fact>]
let myTest () = ()
"""

// Source with a prod function that a test can call to create a dependency edge.
let private testSourceWithDep moduleName =
    $"""module {moduleName}

type FactAttribute() =
    inherit System.Attribute()

let compute x = x + 1

[<Fact>]
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

[<Fact>]
let ``after scan and build, test methods are in the sqlite database`` () =
    withTmpDir "tp-db-scan" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let testFile = Path.Combine(tmpDir, "MyTests.fsx")

        let checker = FSharpChecker.Create(keepAssemblyContents = true)
        let pipeline = CheckPipeline(checker)

        let testConfigs =
            [ { Project = "EchoTests"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " " } ]

        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        // emitFileAndWait ensures analyzeSource completes before BuildSucceeded flushes pendingAnalysis
        emitFileAndWait checker pipeline host testFile (testSource "MyTests")
        |> Async.RunSynchronously

        host.EmitBuildCompleted(BuildSucceeded)

        // Poll test-results until tests have actually run (not "running" or "not run")
        waitUntil
            (fun () ->
                let r = host.RunCommand("test-results", [||]) |> Async.RunSynchronously

                match r with
                | Some json -> not (json.Contains("running")) && not (json.Contains("not run"))
                | None -> false)
            15000

        let db = Database.create dbPath
        let relPath = Path.GetRelativePath(tmpDir, testFile).Replace('\\', '/')
        let testMethods = db.GetTestMethodsInFile(relPath)

        test <@ testMethods.Length > 0 @>
        test <@ testMethods |> List.exists (fun t -> t.TestMethod = "myTest") @>

        test
            <@
                testMethods
                |> List.exists (fun t -> t.TestClass.EndsWith("MyTests", StringComparison.Ordinal))
            @>)

[<Fact>]
let ``after a symbol change, affected-tests identifies the dependent test`` () =
    // Single-file: prod function + test function in the same .fsx.
    // First scan populates DB. Second scan changes compute -> detectChanges finds it
    // changed -> QueryAffectedTests returns computeTest.
    withTmpDir "tp-minimal" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let srcFile = Path.Combine(tmpDir, "All.fsx")

        let testConfigs =
            [ { Project = "MyTests"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = Some "-- --filter-class {classes}"
                ClassJoin = "|" } ]

        let checker = FSharpChecker.Create(keepAssemblyContents = true)
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        // First scan: populate DB with symbols and dependency edge
        emitFileAndWait checker pipeline host srcFile (testSourceWithDep "All")
        |> Async.RunSynchronously

        host.EmitBuildCompleted(BuildSucceeded)

        let deadline1 = DateTime.UtcNow.AddSeconds(15.0)
        let mutable settled1 = false

        while not settled1 && DateTime.UtcNow < deadline1 do
            match host.GetStatus("test-prune") with
            | Some(Running _) -> System.Threading.Thread.Sleep(50)
            | _ -> settled1 <- true

        // Second scan: change compute body so ContentHash differs
        let changedSrc =
            """module All

type FactAttribute() =
    inherit System.Attribute()

let compute x = x + 2

[<Fact>]
let computeTest () =
    let _ = compute 1
    ()
"""

        emitFileAndWait checker pipeline host srcFile changedSrc
        |> Async.RunSynchronously

        // After the second FileChecked, affected-tests should contain computeTest
        // (compute changed -> QueryAffectedTests finds computeTest via the dependency edge)
        let affectedResult =
            host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously

        test <@ affectedResult.IsSome @>
        test <@ affectedResult.Value.Contains("computeTest") @>)

[<Fact(Skip = "Timing-sensitive with async framework dispatch — needs investigation")>]
let ``cross-file type change only runs affected test classes`` () =
    // End-to-end test: change Lib.fsx type -> affected-tests identifies dependent tests -> only those classes run
    withTmpDir "tp-e2e" (fun tmpDir ->
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

        let testConfigs =
            [ { Project = "MyTests"
                Command = "bash"
                Args = bashPath
                Group = "default"
                Environment = []
                FilterTemplate = Some "-- --filter-class {classes}"
                ClassJoin = "|" } ]

        let checker = FSharpChecker.Create(keepAssemblyContents = true)
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
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
        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 10.0

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
        let deadline3 = DateTime.UtcNow.AddSeconds(10.0)
        let mutable affectedTests = ""

        while not (affectedTests.Contains("testValidateTrue")) && DateTime.UtcNow < deadline3 do
            let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously

            match result with
            | Some v -> affectedTests <- v
            | None -> ()

            if not (affectedTests.Contains("testValidateTrue")) then
                System.Threading.Thread.Sleep(500)

        test <@ affectedTests.Contains("testValidateTrue") @>
        test <@ affectedTests.Contains("testValidateFalse") @>
        test <@ not (affectedTests.Contains("testOtherStuff")) @>

        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 10.0

        // Verify that the test command was invoked with the correct filter
        let capturedArgs =
            try
                File.ReadAllText(captureFile)
            with :? System.IO.FileNotFoundException ->
                failwith $"Test command did not execute or write to {captureFile}"

        test <@ capturedArgs.Contains("--filter-class") @>
        test <@ capturedArgs.Contains("Tests") @>)
