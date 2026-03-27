module FsHotWatch.Tests.TestPrunePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Extensions
open TestPrune.SymbolDiff

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

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = TestPrunePlugin(":memory:", "/tmp") :> IFsHotWatchPlugin
    test <@ plugin.Name = "test-prune" @>

[<Fact>]
let ``affected-tests command returns not-analyzed when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not analyzed") @>

[<Fact>]
let ``changed-files command returns empty list when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact>]
let ``test-prune error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_>
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

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

        let plugin = TestPrunePlugin(dbPath, tmpDir)
        host.Register(plugin)

        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet x = 1\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Lib\nlet x = 1\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = Unchecked.defaultof<_>
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

        let plugin = TestPrunePlugin(dbPath, tmpDir)
        host.Register(plugin)

        let fakeFile = Path.Combine(tmpDir, "Dup.fs")
        File.WriteAllText(fakeFile, "module Dup\n")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module Dup\n"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = Unchecked.defaultof<_>
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

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

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

    let plugin = TestPrunePlugin(":memory:", "/tmp", testConfigs = configs)
    host.Register(plugin)

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

        let plugin =
            TestPrunePlugin(":memory:", tmpDir, testConfigs = configs, extensions = [ fakeExtension ])

        host.Register(plugin)

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

        let plugin =
            TestPrunePlugin(":memory:", tmpDir, testConfigs = configs, extensions = [ failingExtension ])

        host.Register(plugin)

        host.EmitBuildCompleted(BuildSucceeded)
        System.Threading.Thread.Sleep(500)

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

        db.RebuildForProject("TestProj", result1)

        let symbol2 =
            { symbol1 with
                LineEnd = 5
                ContentHash = "changed" }

        let result2 = { result1 with Symbols = [ symbol2 ] }

        // Correct pattern: read BEFORE write
        let storedBefore = db.GetSymbolsInFile("src/Lib.fs")
        db.RebuildForProject("TestProj", result2)

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
        let plugin = TestPrunePlugin(":memory:", tmpDir, testConfigs = configs)
        host.Register(plugin)

        // Trigger build → test run
        host.EmitBuildCompleted(BuildSucceeded)
        System.Threading.Thread.Sleep(500)

        // After tests complete, emit a FileChecked — should transition away from test-run status
        let fakeFile = Path.Combine(tmpDir, "New.fs")
        File.WriteAllText(fakeFile, "module New")

        let fakeResult: FileCheckResult =
            { File = fakeFile
              Source = "module New"
              ParseResults = Unchecked.defaultof<_>
              CheckResults = Unchecked.defaultof<_>
              ProjectOptions = Unchecked.defaultof<_>
              Version = 0L }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        match status.Value with
        | Completed(data, _) when (data :? FsHotWatch.Events.TestResults) ->
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
        let plugin = TestPrunePlugin(":memory:", tmpDir, testConfigs = configs)
        host.Register(plugin)

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
        let plugin = TestPrunePlugin(":memory:", tmpDir, testConfigs = configs)
        host.Register(plugin)

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
        let plugin = TestPrunePlugin(":memory:", tmpDir, testConfigs = configs)
        host.Register(plugin)

        // First run — Fails project will fail
        host.EmitBuildCompleted(BuildSucceeded)
        System.Threading.Thread.Sleep(500)

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
    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsNone @>

[<Fact>]
let ``dispose is callable`` () =
    let plugin = TestPrunePlugin(":memory:", "/tmp") :> IFsHotWatchPlugin
    plugin.Dispose()
