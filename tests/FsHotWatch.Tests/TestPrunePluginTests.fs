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

/// Emit a FileChecked event and wait for the plugin mailbox to drain.
/// FileChecked persists symbol analysis as an in-handler side-effect with no
/// status transition, so quiescence — not `beginAwaitNextTerminal`, which
/// would hang the full timeout waiting for a Completed/Failed that never
/// fires — is the correct (and fast) synchronization.
let private emitFileAndQuiesce (host: PluginHost) (result: FileCheckResult) =
    host.EmitFileChecked result
    waitForQuiescent host 10000

/// Emit a BatchChecked cohort-complete signal over `files` and wait for the
/// plugin mailbox to drain. Like FileChecked, the flush is an in-handler
/// side-effect with no status transition.
let private emitBatchAndQuiesce (host: PluginHost) (files: string list) =
    host.EmitBatchChecked(fakeBatchChecked files)
    waitForQuiescent host 10000

/// Emit a successful BuildCompleted and wait for the plugin to reach a
/// terminal status. Unlike FileChecked/BatchChecked, this handler spawns the
/// test run via `Async.Start`, so the work outlives the message handler and
/// quiescence could return before the run finishes — a terminal await is the
/// correct sync here.
let private emitBuildAndWaitTerminal (host: PluginHost) =
    let await = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    await.Wait(TimeSpan.FromSeconds 20.0) |> ignore

/// Stand up a test-prune plugin around a single one-project test config whose
/// command is `sh -c "touch <sentinel>"`. Returns `(host, sentinel)`.
let private withSingleProjectHarness (tmpDir: string) (projectName: string) =
    let sentinel = Path.Combine(tmpDir, "ran")

    let configs =
        [ { Project = projectName
            Command = "sh"
            Args = $"-c \"touch {sentinel}\""
            Group = "default"
            Environment = []
            FilterTemplate = None
            ClassJoin = " "
            TimeoutSec = None } ]

    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    let handler = create ":memory:" tmpDir (Some configs) None None None None
    host.RegisterHandler(handler)
    host, sentinel

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = create ":memory:" "/tmp" None None None None None
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "test-prune" @>

[<Fact(Timeout = 15000)>]
let ``testprune subscribes to BatchChecked`` () =
    // Item 3 (corrected): TestPrune retains FileChecked for per-file
    // accumulation AND adds BatchChecked as the cohort-complete flush
    // signal. Both subscriptions must be present.
    let handler = create ":memory:" "/tmp" None None None None None

    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeFileChecked) @>
    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeBatchChecked) @>

[<Fact(Timeout = 15000)>]
let ``affected-tests command returns empty array when no files checked`` () =
    // After the lazy-compute migration, the IPC always returns a JSON array,
    // computed on demand from state.ChangedSymbols. With no FileChecked events,
    // ChangedSymbols is empty so the SQL query is skipped and "[]" is returned.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact(Timeout = 15000)>]
let ``changed-files command returns empty list when no files checked`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact(Timeout = 15000)>]
let ``test-prune error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let fakeResult =
        { fakeFileCheckResult "/tmp/nonexistent/Fake.fs" with
            Source = "" }

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

[<Fact(Timeout = 15000)>]
let ``changed-files tracks files after emit with valid relative path`` () =
    withTempDir "tp-test" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None
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
let ``duplicate file checks do not duplicate in changed-files list`` () =
    withTempDir "tp-dup" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None
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

    let handler = create ":memory:" "/tmp" None None None None None
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
            TimeoutSec = None } ]

    let handler = create ":memory:" "/tmp" (Some configs) None None None None

    host.RegisterHandler(handler)

    // Verify plugin registered without crashing and status is Idle
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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) (Some(fun _db -> [ fakeExtension ])) None None None

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        // Wait for async test execution to complete
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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) (Some(fun _db -> [ failingExtension ])) None None None

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

[<Fact(Timeout = 15000)>]
let ``FileChecked never transitions plugin to Running status`` () =
    withTempDir "tp-no-running" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir None None None None None
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

        // Wait for processing to complete (error path sets Failed with null checker)
        waitForPluginTerminal host "test-prune" 12.0

        // Regression: FileChecked must never set Running — that caused rapid Running→Completed
        // cycling during FCS cold-start, making the UI show constantly-changing elapsed time
        // as if individual tests were running one-by-one.
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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir (Some configs) None None None None
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

        // FileChecked must not set Running — that causes status cycling in the UI.
        // Completed is acceptable (set on success path); Failed is acceptable (error path).
        match status.Value with
        | Running _ -> Assert.Fail("FileChecked must not set Running — causes status cycling in the UI")
        | _ -> ())

[<Fact(Timeout = 30000)>]
let ``FileChecked exception while tests running surfaces Failed via framework safeUpdate (F10)`` () =
    // F10 (audit/2026-05-02): the previous FileChecked Update had an inner
    // try/with that only reported Failed *when isIdle* (no test run in flight)
    // and silently swallowed otherwise — masking real bugs whenever a check
    // happened mid-test. Dropping the inner catch lets PluginFramework's
    // safeUpdate own the boundary; safeUpdate ALWAYS reports Failed.
    //
    // This test pins the not-isIdle path:
    //   1. Configure a long-sleeping test command and trigger BuildCompleted
    //      to put RunExclusive "tests" in flight (so ctx.IsRunning "tests" =
    //      true and isIdle = false).
    //   2. Emit a FileChecked that throws inside the Update body
    //      (ProjectOptions = Unchecked.defaultof<_> → NullReferenceException).
    //   3. Assert the plugin transitions to Failed.
    //
    // Before the fix: Failed never reached (inner catch swallowed because
    // isIdle was false). After the fix: Failed reached via safeUpdate.
    withTempDir "tp-f10-not-idle" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        // Long-running test command so "tests" RunExclusive stays in flight
        // through the whole assertion. We force-cancel it via the host
        // disposable at end-of-scope (withTempDir cleanup tears down the
        // process registry).
        let configs =
            [ { Project = "TestProject"
                Command = "sleep"
                Args = "10"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        // Subscribe to Failed *before* triggering anything — avoids the race
        // where the transition happens before we start polling.
        let failedAwaiter =
            beginAwaitStatus host "test-prune" (function
                | Failed _ -> true
                | _ -> false)

        // Kick off the long-running test run.
        host.EmitBuildCompleted(BuildSucceeded)

        // Wait until the test run is actually in flight (status reaches
        // Running) so isIdle is guaranteed false when we emit FileChecked.
        let runningWait =
            beginAwaitStatus host "test-prune" (function
                | Running _ -> true
                | _ -> false)

        if not (runningWait.Wait(TimeSpan.FromSeconds 10.0)) then
            Assert.Fail("test run never reached Running — cannot exercise not-isIdle path")

        // Now emit a FileChecked that throws inside the FileChecked branch
        // of Update (ProjectOptions = Unchecked.defaultof<_>).
        let fakeFile = Path.Combine(tmpDir, "Lib.fs")
        File.WriteAllText(fakeFile, "module Lib\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        // safeUpdate must observe the throw and report Failed even though
        // the test run is in flight. Before F10 was fixed, the inner catch
        // would swallow silently and we'd time out here.
        if not (failedAwaiter.Wait(TimeSpan.FromSeconds 10.0)) then
            let cur = host.GetStatus("test-prune")
            Assert.Fail($"expected Failed status from safeUpdate after FileChecked throw; got: %A{cur}"))

[<Fact(Timeout = 15000)>]
let ``FileChecked sets Failed status on analysis error`` () =
    withTempDir "tp-complete-no-configs" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create dbPath tmpDir None None None None None
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

        // Error path (null checker in tests) reports Failed; success path leaves status unchanged.
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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

        host.RegisterHandler(handler)

        // Trigger build -> test run
        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 12.0

        // After tests complete (Completed), emit FileChecked — error path (null checker) sets Failed,
        // so status changes away from Completed. On success path it would remain Completed.
        let fakeFile = Path.Combine(tmpDir, "New.fs")
        File.WriteAllText(fakeFile, "module New")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module New" }

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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

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
                TimeoutSec = None }
              { Project = "Beta"
                Command = "echo"
                Args = "beta"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

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
                TimeoutSec = None }
              { Project = "Fails"
                Command = "false"
                Args = ""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

        host.RegisterHandler(handler)

        // First run — Fails project will fail
        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 12.0

        // Now rerun only failed — should only run "Fails", not "Passes"
        let result =
            host.RunCommand("run-tests", [| """{"only-failed": true}""" |])
            |> Async.RunSynchronously

        test <@ result.IsSome @>
        test <@ result.Value.Contains("Fails") @>
        test <@ not (result.Value.Contains("Passes")) @>)

[<Fact(Timeout = 15000)>]
let ``run-tests not registered when no testConfigs`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let handler = create ":memory:" "/tmp" None None None None None
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsNone @>

[<Fact(Timeout = 15000)>]
let ``run-tests emits TestRunCompleted so other plugins see the run`` () =
    withTempDir "tp-run-trc" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, "{}")

        let host, _ = withSingleProjectHarness dir "TestProject"
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore

        waitForTerminalStatus host "coverage" 10000

        match host.GetStatus("coverage") with
        | Some(Completed _)
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"Expected coverage to process TestRunCompleted after run-tests, got: %A{other}"))

[<Fact(Timeout = 15000)>]
let ``dispose is callable`` () =
    // Framework-managed plugins don't need explicit dispose, but verify create doesn't throw
    let _handler = create ":memory:" "/tmp" None None None None None
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
                cls = "PluginHostTests" && meth = "plugin receives file change events")
        @>

    test
        <@
            parsed
            |> List.exists (fun (cls, meth, _) -> cls = "BuildPluginTests" && meth = "build fires on source change")
        @>

[<Fact(Timeout = 15000)>]
let ``parseFailedTests handles output with no failures`` () =
    let parsed: (string * string * string) list =
        parseFailedTests "Test run summary: Passed!\n  total: 10\n  succeeded: 10"

    test <@ parsed.Length = 0 @>

[<Fact(Timeout = 15000)>]
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
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        test <@ host.HasFailingReasons(warningsAreFailures = true) @>)

[<Fact(Timeout = 20000)>]
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
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

        host.RegisterHandler(handler)

        // Create fail flag so first run fails
        File.WriteAllText(Path.Combine(tmpDir, "fail_flag"), "")
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
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

        waitForPluginTerminal host "test-prune" 12.0
        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>)

[<Fact(Timeout = 15000)>]
let ``RerunQueued path records previous run outcome to history before starting rerun`` () =
    withTempDir "tp-rerun-queued-history" (fun tmpDir ->
        // First run sleeps ~1s and fails. The second BuildSucceeded arrives mid-run,
        // putting state into RerunQueued. Both runs' outcomes must end up in history —
        // before the fix, the RerunQueued path silently dropped the previous run's
        // lifecycle, so only the rerun's outcome ever made it to history.
        let configs =
            [ { Project = "TestProject"
                Command = "sh"
                Args = "-c \"sleep 1; exit 1\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        // Wait until run 1 is actually executing before queueing the rerun.
        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(Running _) -> true
                | _ -> false)
            5000

        host.EmitBuildCompleted(BuildSucceeded)

        waitForPluginTerminal host "test-prune" 12.0

        let history = host.GetHistory("test-prune")

        // The bug under test: before the fix, RerunQueued silently dropped the
        // previous run's outcome — history would be empty (or contain only the
        // rerun's no-op skip). After the fix, the previous failed run is
        // recorded. We assert exactly that — not an exact count, because the
        // rerun's no-op skip may or may not produce its own history entry
        // depending on scheduler timing (race-prone).
        test <@ history.Length >= 1 @>

        let firstFailed =
            history
            |> List.exists (fun r ->
                match r.Outcome with
                | FailedRun _ -> true
                | _ -> false)

        // The first run definitely failed (script always exits 1). Whether the
        // rerun produces its own entry is incidental.
        test <@ firstFailed @>)

[<Fact(Timeout = 30000)>]
let ``PendingRerun storm: plugin reaches terminal state after BuildCompleted hammering subsides`` () =
    withTempDir "tp-rerun-storm" (fun tmpDir ->
        // Stress the PendingRerun loop: while a test run is in flight, fire
        // additional BuildCompleted events so the plugin sets PendingRerun on
        // each one. After we stop emitting builds, the plugin must eventually
        // drain its queued reruns and settle on a terminal status.
        //
        // Reproduces the user-reported "stuck Running" symptom against a
        // continuous BuildCompleted storm: if any code path leaves
        // PendingRerun set but never schedules a rerun, or schedules a rerun
        // whose TestsFinished never fires terminal, the plugin sits in
        // Running indefinitely.
        let configs =
            [ { Project = "FastTests"
                Command = "sh"
                Args = "-c \"sleep 0.3; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        // Fire many BuildCompleteds in quick succession. The first transitions
        // Idle → Running and starts the test run. Subsequent ones land mid-run
        // and set PendingRerun (each new one re-sets it, idempotent). After the
        // initial run finishes the rerun branch fires; another batch of builds
        // will re-arm PendingRerun, and so on.
        for _ in 1..6 do
            host.EmitBuildCompleted(BuildSucceeded)
            // Tiny sleep so they don't all coalesce into the inbox before the
            // first one transitions to Running. Without this the first 6 land
            // before any RunExclusive starts, which trivially passes.
            Thread.Sleep(80)

        // Allow the rerun loop to keep cycling, then stop emitting. Within a
        // reasonable settle window (test run = 0.3s, leaving generous slack),
        // the plugin must reach a terminal status.
        waitForPluginTerminal host "test-prune" 20.0

        let finalStatus = host.GetStatus("test-prune")

        let isTerminal =
            match finalStatus with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false

        test <@ isTerminal @>)

// Inline FactAttribute so test detection works without xUnit assemblies in script options.
// Uses module-level [<Fact>] functions — the pattern that analyzeSource reliably detects
// via FCS symbol uses without needing resolved assembly references.
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
        let! result = pipeline.CheckFile(AbsFilePath.create filePath)

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

// Generous xUnit cap: this test drives real FCS analysis (emitFileAndWait,
// 10s condition-based cap) then waits for the plugin terminal (12s cap). On a
// fast machine both resolve in <1s, but FCS cold-start on a slow/loaded CI
// runner can take >15s — the previous 15000ms Fact cap fired mid-progress and
// the run was CANCELED. The internal waits stay condition-based (they fail fast
// on a genuine hang); only the hard xUnit cap is raised so a slow-but-
// progressing run can finish.
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
                TimeoutSec = None } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let handler = create dbPath tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        let testFile = Path.Combine(tmpDir, "MyTests.fsx")

        // Use real FCS analysis to exercise the Ok analysisResult path
        emitFileAndWait checker pipeline host testFile (testSource "MyTests")
        |> Async.RunSynchronously

        // Wait for terminal status — plugin reports Completed after analysis
        // even with testConfigs, so WaitForComplete doesn't hang waiting for
        // a BuildCompleted that may never come.
        waitForPluginTerminal host "test-prune" 12.0

        let status = host.GetStatus("test-prune")
        test <@ status.IsSome @>

        match status.Value with
        | Completed _ -> ()
        | other -> Assert.Fail($"Expected Completed after FileChecked analysis, got: %A{other}"))

// Generous xUnit cap (real FCS via emitFileAndWait + 12s terminal wait =
// 22s internal budget). Same slow-runner cancellation risk as the testConfigs
// sibling above; internal waits remain condition-based.
[<Fact(Timeout = 60000)>]
let ``FileChecked reports Completed when no testConfigs (success path)`` () =
    withTempDir "tp-complete-real" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "test.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        // No testConfigs — analysis-only mode
        let handler = create dbPath tmpDir None None None None None
        host.RegisterHandler(handler)

        let testFile = Path.Combine(tmpDir, "MyLib.fsx")

        // Use real FCS analysis to exercise the Ok analysisResult path
        emitFileAndWait checker pipeline host testFile (testSource "MyLib")
        |> Async.RunSynchronously

        // Wait for terminal status
        waitForPluginTerminal host "test-prune" 12.0

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
[<Fact(Timeout = 20000)>]
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
                ClassJoin = " "
                TimeoutSec = None } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None

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

// Generous xUnit cap: real FCS (getScriptOptions + multiple CheckFile) plus
// several condition-based waits (build/batch terminal 20s each, idle 10s,
// affected-tests poll 5s) whose summed budget already exceeds 20s. On a slow
// runner the 20000ms Fact cap fired before the legitimately-slow FCS work
// finished. Internal condition-based waits keep their own caps (fail fast on a
// real hang); the xUnit cap is raised so a slow-but-progressing run completes.
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

        // testConfigs is required for the plugin to subscribe to BuildCompleted
        // (without it, flushAndQueryAffected is never triggered). Command is a no-op.
        let testConfigs =
            [ { Project = "Lib"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None

        host.RegisterHandler(handler)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        // Item 3 ordering: BuildCompleted FIRST so subsequent FileChecked
        // events are allowed to promote the freshness sidecar to clean.
        // Mirrors fshw's real cold-scan pipeline (BuildPlugin terminal
        // gates FCS tier dispatch).
        emitBuildAndWaitTerminal host

        // Initial index: both files analysed, edges written to DB.
        let libResult =
            pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously

        match libResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        // (No waitUntil for `Lib.fsx` in ChangedFiles here — under Item 3 the
        // first detectChanges against an empty stored sidecar is bypassed,
        // so this initial check just primes the sidecar to clean. The real
        // assertion is the affected-tests check at the end.)
        let testsResult =
            pipeline.CheckFile(AbsFilePath.create testsFile) |> Async.RunSynchronously

        match testsResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        waitForPluginIdle host "test-prune" 10.0

        // Drive a BatchChecked so the symbol DB is flushed from
        // PendingAnalysis.
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
// Generous xUnit cap: real FCS over two files plus three 20s terminal waits
// and two 10s/5s polls — internal budget far exceeds 20s, so the old 20000ms
// Fact cap canceled slow-but-progressing CI runs. Internal waits stay
// condition-based.
[<Fact(Timeout = 60000)>]
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
                TimeoutSec = None } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
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

        // Item 3 ordering: BuildCompleted FIRST so subsequent FileCheckeds
        // promote the freshness sidecar to clean. Mirrors fshw's real
        // cold-scan pipeline.
        emitBuildAndWaitTerminal host

        // Emit lib file
        let libResult =
            pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously

        match libResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        // Emit tests file
        let testsResult =
            pipeline.CheckFile(AbsFilePath.create testsFile) |> Async.RunSynchronously

        match testsResult with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        // Wait for analysis
        waitForPluginIdle host "test-prune" 10.0

        // BatchChecked drives the flush of accumulated PendingAnalysis to
        // the symbol DB so the subsequent edited-file FileChecked has stored
        // rows to diff against.
        emitBatchAndQuiesce host [ libFile; testsFile ]

        // Now change the type: add a new field
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

        emitBuildAndWaitTerminal host

        // Verify that the test command was invoked with the correct filter
        let capturedArgs =
            try
                File.ReadAllText(captureFile)
            with :? System.IO.FileNotFoundException ->
                failwith $"Test command did not execute or write to {captureFile}"

        test <@ capturedArgs.Contains("--filter-class") @>
        test <@ capturedArgs.Contains("Tests") @>)

// Generous xUnit cap: chains a 12s terminal wait, a 5s settle wait, and an 8s
// waitForAllTerminal task (25s internal budget). The previous 20000ms Fact cap
// could fire before the (condition-based) stability window resolved on a slow
// runner. Internal waits stay condition-based and fail fast on a real hang.
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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

        host.RegisterHandler(handler)

        // 1. Build completes → tests run and finish
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

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

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Late\nlet x = 1\n" }

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

[<Fact(Timeout = 15000)>]
let ``FileChecked with no detected symbol changes leaves ChangedSymbols empty`` () =
    // After the lazy-compute migration, FileChecked accumulates ChangedSymbols
    // (it does not eagerly query DB or populate AffectedTests). Because this
    // test uses a fake CheckResults=ParseOnly, analyzeSource yields no symbols
    // and ChangedSymbols stays []; affected-tests therefore returns "[]".
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
        let handler = create dbPath tmpDir None None None None None
        host.RegisterHandler(handler)

        // Emit FileChecked with a changed symbol — in the old code this would
        // query the DB and populate AffectedTests. In the fix, it should NOT.
        let fakeFile = Path.Combine(tmpDir, "src", "Lib.fs")
        Directory.CreateDirectory(Path.Combine(tmpDir, "src")) |> ignore
        File.WriteAllText(fakeFile, "module Lib\nlet foo = 2\n")

        let fakeResult =
            { fakeFileCheckResult fakeFile with
                Source = "module Lib\nlet foo = 2\n" }

        try
            host.EmitFileChecked(fakeResult)
        with _ ->
            ()

        waitForPluginTerminal host "test-prune" 12.0

        // After FileChecked with no real analysis results, ChangedSymbols stays
        // empty so the lazy IPC returns "[]" without hitting the DB.
        let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value = "[]" @>
        test <@ not (result.Value.Contains("myTest")) @>)

// Generous xUnit cap: real FCS over two files plus 20s build + 10s idle + 20s
// batch terminal waits and a 5s affected-tests poll. Internal budget exceeds
// 20s, so the old 20000ms Fact cap canceled slow CI runs mid-progress. Internal
// waits stay condition-based.
[<Fact(Timeout = 60000)>]
let ``affected-tests computes lazily on demand from ChangedSymbols`` () =
    // Locks in the post-migration contract: FileChecked accumulates
    // state.ChangedSymbols but does NOT eagerly QueryAffectedTests; the IPC
    // command runs the SQL on demand against the current DB state. After
    // an initial FileChecked + BuildCompleted populates the DB, a second
    // FileChecked that mutates a symbol should make affected-tests return
    // the dependent test BEFORE another BuildCompleted fires.
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
                TimeoutSec = None } ]

        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        // Item 3 ordering: BuildCompleted FIRST so the seed FileCheckeds
        // can promote the freshness sidecar to clean. (Mirrors fshw's real
        // cold-scan: BuildPlugin terminal gates FCS tier checks.)
        emitBuildAndWaitTerminal host

        // Seed the DB with the initial baseline.
        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile failed"

        match pipeline.CheckFile(AbsFilePath.create testsFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "tests CheckFile failed"

        waitForPluginIdle host "test-prune" 10.0

        // BatchChecked drives the flush of accumulated PendingAnalysis to
        // the symbol DB.
        emitBatchAndQuiesce host [ libFile; testsFile ]

        // Now mutate the symbol and emit a single FileChecked. We do NOT
        // emit BuildCompleted afterward — affected-tests must be answered
        // by the lazy on-demand SQL, not by an eager populate from FileChecked.
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
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        // After BuildCompleted with no prior FileChecked, should still work
        // (AnalysisRan will be false, affected-tests returns "not analyzed")
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0

        let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>)

[<Fact(Timeout = 15000)>]
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
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) None None (Some(fun _ -> runCount <- runCount + 1)) None

        host.RegisterHandler(handler)

        // First BuildCompleted = cold start, should run all
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ runCount = 1 @>

        // Second BuildCompleted with no changed symbols — should SKIP
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ runCount = 1 @>) // still 1, not 2

[<Fact(Timeout = 15000)>]
let ``comment-only change does not add file to ChangedFiles but AST change does`` () =
    // Regression test: before the fix, newChangedFiles was computed unconditionally
    // before changedNames, so any file emit (even comment-only) would add the file
    // to ChangedFiles and trigger extension-based tests (e.g. Falco routes).
    //
    // After the fix, newChangedFiles is only updated when changedNames is non-empty,
    // i.e. only when the AST actually changed.
    let initialSource = "module Lib\nlet x = 1\n"
    let commentOnlySource = "module Lib\n// a comment added\nlet x = 1\n"
    let astChangedSource = "module Lib\nlet x = 1\nlet y = 2\n"

    withSeededTestEnv "tp-comment-regression" "Lib.fs" initialSource (fun env ->
        // --- Phase 1: comment-only change should NOT add file to ChangedFiles ---
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

        // --- Phase 2: AST change should add file to ChangedFiles ---
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
          TimeoutSec = None }

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
          TimeoutSec = None }

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
          TimeoutSec = None }

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
          TimeoutSec = None }

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
          TimeoutSec = None }

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
    // Regression: deleting only the main DB file leaves -wal/-shm on disk.
    // SQLite in WAL mode ties the sidecars to the main file; a new connection
    // opening a fresh (empty) main DB with stale sidecars produces a
    // 0-byte main DB with garbage recovery state — subsequent inserts hit
    // "no such column: parent_symbol_id" because the schema DDL never fully
    // applied. Observed in production after a schema-drift recovery pass.
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
    // Should not throw even though the file does not exist.
    tryRepairSchemaDrift missingPath (exn "no such column: source")
    test <@ not (File.Exists missingPath) @>

// ---------------------------------------------------------------------------
// Progressive TestCompleted emission: a group that completes quickly emits a
// partial snapshot even while a slower group is still running. Without this,
// downstream plugins subscribed to TestCompleted (e.g. afterTests triggers)
// are forever blocked by the slowest test project.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``executeTests emits a TestProgress per group as groups finish`` () =
    withTempDir "tp-progressive" (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getEvents, recorder) = testProgressRecorder ()
        host.RegisterHandler(recorder)

        // Three groups: two run `echo` (near-instant), one runs `sleep 2`.
        // Each group's async must resolve independently; if executeTests emits
        // only once at batch end, the test will see one event instead of three.
        // runProcess tokenises args space-separated with no shell, so we use
        // simple single-binary commands with one numeric arg to avoid quoting.
        let configs =
            [ { Project = "ProjFastA"
                Command = "echo"
                Args = "a"
                Group = "fast-a"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None }
              { Project = "ProjFastB"
                Command = "echo"
                Args = "b"
                Group = "fast-b"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None }
              { Project = "ProjSlow"
                Command = "sleep"
                Args = "2"
                Group = "slow"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")

        let handler = create dbPath tmpDir (Some configs) None None None None

        host.RegisterHandler(handler)

        // Trigger test execution by emitting BuildSucceeded.
        host.EmitBuildCompleted(BuildSucceeded)

        // Wait for three TestProgress emissions — one per group.
        waitUntil (fun () -> getEvents () |> List.length >= 3) 20000

        let events = getEvents ()
        // One emission per group — three groups, three emissions.
        test <@ events.Length = 3 @>

        // All three share a single RunId.
        let runIds = events |> List.map (fun p -> p.RunId) |> List.distinct
        test <@ runIds.Length = 1 @>

        // Each emission carries exactly that group's projects as a delta.
        let allProjects =
            events
            |> List.collect (fun p -> p.NewResults |> Map.toList |> List.map fst)
            |> Set.ofList

        test <@ allProjects = Set.ofList [ "ProjFastA"; "ProjFastB"; "ProjSlow" ] @>

        // The slow group must appear in the LAST emission — it completes after
        // the two fast groups. (Under cumulative emission the invariant was a
        // prefix-chain; under delta emission the invariant is an ordering.)
        let lastEvent = events |> List.last

        test <@ lastEvent.NewResults |> Map.containsKey "ProjSlow" @>

        let earlierEvents = events |> List.take 2

        test
            <@
                earlierEvents
                |> List.forall (fun p -> not (p.NewResults |> Map.containsKey "ProjSlow"))
            @>)

// ---------------------------------------------------------------------------
// WasFiltered on per-project test results. Full runs (no impact filter applied)
// must report WasFiltered = false; partial runs report true.
// ---------------------------------------------------------------------------

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
                TimeoutSec = None } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil (fun () -> getCompleted () |> List.isEmpty |> not) 10000

        let completed = getCompleted ()
        test <@ completed.Length >= 1 @>
        let last = completed |> List.last

        match last.Results |> Map.tryFind "ProjA" with
        | Some r -> test <@ TestResult.wasFiltered r = false @>
        | None -> Assert.Fail("ProjA not in Results"))

// ---------------------------------------------------------------------------
// Coverage path selection + post-test merge step. Pure helpers — tested
// directly against the filesystem with pre-seeded JSON fixtures so we don't
// need a real coverlet run.
// ---------------------------------------------------------------------------

open FsHotWatch.TestPrune

[<Fact>]
let ``buildCoverageArgs picks baseline on full run and partial on filtered run`` () =
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/baseline.cobertura.xml"
          Partial = "/tmp/cov/partial.cobertura.xml"
          Cobertura = "/tmp/cov/coverage.cobertura.xml"
          ArgsTemplate = defaultCoverageArgsTemplate }

    let full = buildCoverageArgs paths false
    test <@ full.Contains("baseline.cobertura.xml") @>
    test <@ not (full.Contains("partial.cobertura.xml")) @>

    let partial = buildCoverageArgs paths true
    test <@ partial.Contains("partial.cobertura.xml") @>
    test <@ not (partial.Contains("baseline.cobertura.xml")) @>

[<Fact>]
let ``default template uses an MTP-accepted output format`` () =
    // Regression: previous impl emitted `--coverage-output-format json`, but
    // MTP (Microsoft Testing Platform) only accepts `coverage | xml | cobertura`.
    // Passing `json` made every test run fail at startup with an invalid-args
    // error and zero tests executed. Pin the default template value so this
    // can't silently regress.
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/baseline.cobertura.xml"
          Partial = "/tmp/cov/partial.cobertura.xml"
          Cobertura = "/tmp/cov/coverage.cobertura.xml"
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
    // Coverage invocation varies by test runner (MTP / classic dotnet test +
    // coverlet.collector / AltCover / xUnit classic + OpenCover). The plugin
    // must not hard-code one shape. Callers supply a template; the plugin
    // substitutes `{output}` with the chosen baseline/partial path.
    //
    // Substitution is a pure string replace — the template is responsible for
    // its own quoting if paths might contain spaces.
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/B.xml"
          Partial = "/tmp/cov/P.xml"
          Cobertura = "/tmp/cov/C.xml"
          ArgsTemplate = "--custom-collector --out \"{output}\" --extra" }

    let full = buildCoverageArgs paths false
    test <@ full = "--custom-collector --out \"/tmp/cov/B.xml\" --extra" @>

    let partial = buildCoverageArgs paths true
    test <@ partial = "--custom-collector --out \"/tmp/cov/P.xml\" --extra" @>

[<Fact>]
let ``buildCoverageArgs treats a template missing {output} as invalid`` () =
    // If the caller forgot the placeholder we don't silently produce invalid
    // args — surface the mistake. Pattern-pinning via substring makes the
    // error diagnosable.
    let paths: CoveragePaths =
        { Baseline = "/tmp/cov/B.xml"
          Partial = "/tmp/cov/P.xml"
          Cobertura = "/tmp/cov/C.xml"
          ArgsTemplate = "--broken-template-no-placeholder" }

    let ex = Assert.ThrowsAny(fun () -> buildCoverageArgs paths false |> ignore)

    test <@ ex.Message.Contains("{output}") @>

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

// Seed a DB with a single symbol spanning the given repo-relative file and line
// span, so `ingestCobertura` can map covered lines onto it. Returns the DB.
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

// New DB-backed coverage path: `ingestAndEmitCoverage` ingests each project's
// raw runner cobertura into the TestPrune DB (max-merge, symbol-relative) and
// emits the FULL DB ONCE to the single shared cobertura file. These replace the
// old per-line/per-file max-merge `processCoverageOutput` tests.
[<Fact>]
let ``ingestAndEmitCoverage ingests covered lines and emits the single shared cobertura`` () =
    withTempDir "cov-ingest" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12

        // Raw cobertura from the runner uses an ABSOLUTE filename (as a real run
        // would); ingest relativizes it against repoRoot to match the symbol's
        // repo-relative source_file. Lines 10 and 11 covered, 12 not.
        let absFile = Path.Combine(repoRoot, "src/Foo.fs")
        let rawPath = Path.Combine(dir, "coverage.baseline.cobertura.xml")
        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        File.WriteAllText(rawPath, mkCobertura "Foo.dll" absFile [ (10, 3); (11, 1); (12, 0) ])

        ingestAndEmitCoverage db repoRoot (Some sharedOut) [ rawPath ]

        test <@ File.Exists sharedOut @>
        let xml = File.ReadAllText sharedOut
        // The emitted cobertura reports the covered lines back, at the symbol's
        // current absolute positions.
        test <@ xml.Contains("number=\"10\"") @>
        test <@ xml.Contains("number=\"11\"") @>
        test <@ xml.Contains("hits=\"3\"") @>)

[<Fact>]
let ``ingestAndEmitCoverage with an empty raw cobertura does NOT clobber an existing emitted cobertura`` () =
    // Issue 3: an aborted run can leave an empty raw cobertura. It ingests
    // nothing (parse → [] → no-op), so the previously emitted shared cobertura
    // must survive untouched rather than being overwritten with empty coverage.
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

        ingestAndEmitCoverage db repoRoot (Some sharedOut) [ rawPath ]

        // The empty raw still counts as an input on disk, so the DB is emitted —
        // but it ingested nothing, so the DB has no coverage and emits an empty
        // document. Critically, the run must not LOWER an existing good emission:
        // since nothing was ingested, the emitted file must contain no covered
        // lines newly introduced and certainly must not silently drop the prior
        // coverage to a worse number. Assert no symbol coverage was recorded.
        let summary = TestPrune.Coverage.fileCoverageSummary db "src/Foo.fs"
        test <@ summary.Covered = 0 @>)

[<Fact>]
let ``ingestAndEmitCoverage with no inputs leaves a prior emitted cobertura untouched`` () =
    // When NO raw inputs exist on disk (e.g. every project skipped/deferred),
    // the shared cobertura must NOT be written — a prior good emission survives.
    withTempDir "cov-noinputs" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Foo.fs" 10 12

        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        Directory.CreateDirectory(Path.GetDirectoryName(sharedOut)) |> ignore
        let priorGood = mkCobertura "Foo.dll" "src/Foo.fs" [ (10, 7); (11, 7) ]
        File.WriteAllText(sharedOut, priorGood)

        // A raw path that does not exist on disk — filtered out, no emit.
        let missingRaw = Path.Combine(dir, "coverage.baseline.cobertura.xml")

        ingestAndEmitCoverage db repoRoot (Some sharedOut) [ missingRaw ]

        // Untouched: still the prior good document.
        test <@ File.ReadAllText sharedOut = priorGood @>)

[<Fact>]
let ``ingestAndEmitCoverage does NOT clobber prior coverage when the symbol graph is incomplete`` () =
    // Cold start (the first run after a schema bump recreated the TestPrune DB, before the
    // daemon's scan reached the covered files): the covered file has no symbols yet, so its
    // lines can't map. Emitting the DB now would write a partial cobertura that DROPS that
    // file's coverage entirely, clobbering a prior good emission and failing the ratchet.
    // The plugin must SKIP the emit until the graph is populated; the DB persists, so a
    // later warm run emits in full.
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

        // This run's raw cobertura covers Embeddings.fs, but the DB has no symbols for it
        // yet → every line is skipped.
        let absFile = Path.Combine(repoRoot, "src/Embeddings.fs")
        let rawPath = Path.Combine(dir, "coverage.baseline.cobertura.xml")
        File.WriteAllText(rawPath, mkCobertura "Intelligence.dll" absFile [ (10, 3); (11, 3); (12, 1) ])

        ingestAndEmitCoverage db repoRoot (Some sharedOut) [ rawPath ]

        // The prior good emission must survive untouched — NOT be overwritten with a
        // partial snapshot that dropped Embeddings.fs.
        test <@ File.ReadAllText sharedOut = priorGood @>)

[<Fact>]
let ``ingestAndEmitCoverage emits when the symbol graph maps the bulk of lines (warm)`` () =
    // The complement: once the covered file IS indexed, the lines map and the emit proceeds.
    withTempDir "cov-warm" (fun dir ->
        let repoRoot = dir
        let db = seedSymbolDb (Path.Combine(dir, "test.db")) "src/Embeddings.fs" 10 12

        let sharedOut = Path.Combine(dir, "coverage", "coverage.cobertura.xml")
        let absFile = Path.Combine(repoRoot, "src/Embeddings.fs")
        let rawPath = Path.Combine(dir, "coverage.baseline.cobertura.xml")
        File.WriteAllText(rawPath, mkCobertura "Intelligence.dll" absFile [ (10, 3); (11, 1); (12, 0) ])

        ingestAndEmitCoverage db repoRoot (Some sharedOut) [ rawPath ]

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
        // A non-json sibling must survive — we only clear FCS check-cache entries.
        File.WriteAllText(Path.Combine(cacheDir, "keep.txt"), "x")

        let cleared = clearFcsCheckCache repoRoot

        test <@ cleared = 2 @>
        test <@ not (File.Exists(Path.Combine(cacheDir, "a.json"))) @>
        test <@ File.Exists(Path.Combine(cacheDir, "keep.txt")) @>)

[<Fact>]
let ``clearFcsCheckCache is a no-op when there is no cache dir`` () =
    withTempDir "fcs-nocache" (fun repoRoot -> test <@ clearFcsCheckCache repoRoot = 0 @>)

[<Fact>]
let ``TestRunCompleted carries RanFullSuite=true when no projects filtered`` () =
    let evt =
        { RunId = System.Guid.NewGuid()
          TotalElapsed = System.TimeSpan.Zero
          Outcome = Normal
          Results = Map.empty
          RanFullSuite = true }


    Assert.True evt.RanFullSuite

[<Fact>]
let ``ranFullSuite is true for empty results`` () =
    test <@ TestResult.ranFullSuite Map.empty @>

[<Fact>]
let ``ranFullSuite is true when no project was filtered`` () =
    let results =
        Map.ofList
            [ "A", TestsPassed("", false, TimeSpan.Zero)
              "B", TestsFailed("", false, TimeSpan.Zero) ]

    test <@ TestResult.ranFullSuite results @>

[<Fact>]
let ``ranFullSuite is false when at least one project was filtered`` () =
    let results =
        Map.ofList
            [ "A", TestsPassed("", false, TimeSpan.Zero)
              "B", TestsPassed("", true, TimeSpan.Zero) ]

    test <@ not (TestResult.ranFullSuite results) @>

[<Fact(Timeout = 15000)>]
let ``full run (no filter) emits TestRunCompleted with RanFullSuite=true`` () =
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
                TimeoutSec = None } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil (fun () -> getCompleted () |> List.isEmpty |> not) 10000

        let last = getCompleted () |> List.last
        test <@ last.RanFullSuite @>)

[<Fact(Timeout = 20000)>]
let ``regression: TestPrune writes a cache entry with TestRunCompleted on terminal status`` () =
    // Before this fix, TestPrune emitted TestRunStarted/Completed from the
    // fire-and-forget async (runTestsWithImpact). The framework's per-event
    // capture window for the synchronous Custom TestsFinished handler had
    // no events to capture, so the cached EmittedEvents was empty.
    // After: lifecycle events emit from the synchronous handler instead, so
    // they're captured. Cache replay can re-fire TestRunCompleted to
    // downstream subscribers (e.g. FileCommandPlugin) on a hit.
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
                TimeoutSec = None } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 10000

        // Cache should now contain an entry with at least one TestRun event captured.
        let key: FsHotWatch.TaskCache.CompositeKey = { Plugin = "test-prune"; File = None }

        let cacheKeyFn = handler.CacheKey.Value
        let computedKey = cacheKeyFn (BuildCompleted BuildSucceeded)
        test <@ computedKey.IsSome @>

        let result = cacheIface.TryGet key computedKey.Value
        test <@ result.IsSome @>

        let hasCompleted =
            result.Value.EmittedEvents
            |> List.exists (fun e ->
                match e with
                | FsHotWatch.TaskCache.CachedTestRunCompleted _ -> true
                | _ -> false)

        test <@ hasCompleted @>)

// NOTE: ``run summary names the slowest project when 2+ projects ran`` was moved
// to FsHotWatch.IntegrationTests — it spawns two real sh subprocesses with a
// 1-second sleep dependency to assert "slowest" ordering, and the 5-second
// terminal-wait window starves under heavy parallel test load (manifesting as
// `List.last` ArgumentException when history is empty after timeout).

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
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None

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
    // Contract: BuildSucceeded means artifacts are guaranteed fresh (BuildPlugin
    // owns the verification — see verifyArtifactsFresh). TestPrune does not
    // second-guess the signal; if it sees BuildSucceeded, it runs every test
    // project and never emits "binary is stale" warnings.
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
let ``cold-start BuildCompleted with unchanged state replays from task cache`` () =
    // The design that introduced BatchChecked deletes the RequireWarmStart
    // workaround. On a cold daemon restart with NO changes since the prior
    // session, BuildCompleted's cache key (changed-symbols ⊕ outcome) matches
    // the prior session's entry — the framework replays without re-running
    // tests. The pre-BatchChecked behavior of "cold-start always re-runs once"
    // was a workaround for a half-formed-key window that no longer exists.
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
                TimeoutSec = None } ]

        // Session 1: run once to populate the task cache with a prior-session result.
        do
            let dbPath1 = Path.Combine(tmpDir, "tp1.db")
            let host1 = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = taskCache)

            let handler1 = create dbPath1 tmpDir (Some configs) None None None None

            host1.RegisterHandler(handler1)
            host1.EmitBuildCompleted(BuildSucceeded)
            waitForTerminalStatus host1 "test-prune" 10000

        // Delete sentinel — session 2 must NOT re-create it (cache replay path).
        if File.Exists sentinel then
            File.Delete sentinel

        // Session 2: new plugin instance (simulates daemon restart) using same on-disk cache.
        let dbPath2 = Path.Combine(tmpDir, "tp2.db")
        let host2 = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = taskCache)

        let handler2 = create dbPath2 tmpDir (Some configs) None None None None

        host2.RegisterHandler(handler2)

        host2.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host2 "test-prune" 10000

        // Cold start with unchanged state replays the cached run — the test
        // command (touch sentinel) must NOT have executed.
        test <@ not (File.Exists sentinel) @>)

// =============================================================================
// FCS cache-poisoning gate: don't flush symbols for files whose FCS check
// produced Error-severity diagnostics. Cold-start FCS sometimes returns
// "expected type X but here has type X" for files that compile cleanly
// once warm; flushing those poisoned symbols overwrites the prior good DB
// snapshot and breaks the cache-replay path on the next boot.
// =============================================================================

/// Returns a real FCS FileCheckResult for the given source — full type-check,
/// real diagnostics. Used by the cache-poisoning gate tests below to feed
/// realistic Error / Warning / clean diagnostic shapes through the plugin.
let private checkSourceForReal (tmpDir: string) (fileName: string) (source: string) =
    async {
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let filePath = Path.Combine(tmpDir, fileName)
        File.WriteAllText(filePath, source)
        let! projOptions = getScriptOptions checker filePath source
        pipeline.RegisterProject(filePath, projOptions)
        let! result = pipeline.CheckFile(AbsFilePath.create filePath)
        return result
    }

[<Fact(Timeout = 15000)>]
let ``hasFcsErrors returns false for ParseOnly`` () =
    test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty "" ParseOnly) @>

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors returns true for source with type error`` () =
    withTempDir "tp-poisoning-err" (fun tmpDir ->
        // Type mismatch: assigning string to int → Error.
        let brokenSource =
            """module Broken
let x : int = "not an int"
"""

        let result =
            checkSourceForReal tmpDir "Broken.fsx" brokenSource
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors returns false for clean source`` () =
    withTempDir "tp-poisoning-clean" (fun tmpDir ->
        let cleanSource =
            """module Clean
let answer = 42
"""

        let result =
            checkSourceForReal tmpDir "Clean.fsx" cleanSource
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors returns false for warning-only source`` () =
    withTempDir "tp-poisoning-warn" (fun tmpDir ->
        // Incomplete pattern match: warning, not error. FCS reports
        // FS0025 at Warning severity. The gate must allow flush.
        let warnSource =
            """module Warn
let f x =
    match x with
    | 1 -> "one"
"""

        let result =
            checkSourceForReal tmpDir "Warn.fsx" warnSource
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Sanity: the source actually does have warning diagnostics.
        let diagnostics =
            match result.CheckResults with
            | FullCheck cr -> cr.Diagnostics
            | ParseOnly -> [||]

        test <@ diagnostics.Length > 0 @>

        test
            <@
                diagnostics
                |> Array.forall (fun d -> d.Severity <> FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
            @>

        // Gate result: warnings alone do NOT block flush.
        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

// =============================================================================
// F38 gate suppression symmetry — `hasFcsErrors` must apply the same
// `parseNowarnCodes ∪ FcsSuppressedCodes` filter that
// `Daemon.reportFcsDiagnostics` applies to the user-visible error stream.
// Without the filter the gate trips on codes the user has already silenced
// (e.g. FS1182 promoted to Error by `<TreatWarningsAsErrors>` + `#nowarn`),
// killing TestPrune cache-replay across daemon restarts on cold scans.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors respects per-file #nowarn directives`` () =
    withTempDir "tp-poisoning-nowarn" (fun tmpDir ->
        // Source has a real Error-severity diagnostic (FS0001 type mismatch).
        // `#nowarn "1"` in the source adds code 1 to the gate's effective
        // suppression set via `parseNowarnCodes`. The gate must drop the
        // diagnostic — symmetric with `reportFcsDiagnostics` — even though
        // FCS itself still reports it at Severity = Error.
        let source =
            """#nowarn "1"
module Test
let x : int = "not-an-int"
"""

        let result =
            checkSourceForReal tmpDir "NoWarn.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Sanity: FCS still emits an Error-severity diagnostic the gate would
        // otherwise trip on. (`#nowarn` does not actually suppress upstream FCS
        // errors; the gate's own suppression filter is what carries the day.)
        let hasErrorDiagnostic =
            match result.CheckResults with
            | FullCheck cr ->
                cr.Diagnostics
                |> Array.exists (fun d ->
                    d.ErrorNumber = 1
                    && d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
            | ParseOnly -> false

        test <@ hasErrorDiagnostic @>

        // The bug pre-fix: gate sees raw `cr.Diagnostics` and trips. Post-fix:
        // `parseNowarnCodes` puts FS1 in the suppressed set and the gate falls through.
        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors respects configured FcsSuppressedCodes`` () =
    withTempDir "tp-poisoning-config" (fun tmpDir ->
        // No `#nowarn` in source — caller passes the suppression set instead.
        // This is the path daemons use to silence cold-scan-only noise codes
        // (`fcsSuppressedCodes` in DaemonConfig). The gate must honour it.
        let source =
            """module Test
let x : int = "not-an-int"
"""

        let result =
            checkSourceForReal tmpDir "Config.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        let hasErrorDiagnostic =
            match result.CheckResults with
            | FullCheck cr -> cr.Diagnostics |> Array.exists (fun d -> d.ErrorNumber = 1)
            | ParseOnly -> false

        test <@ hasErrorDiagnostic @>

        test
            <@
                not (
                    FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors
                        (Set.singleton 1)
                        result.Source
                        result.CheckResults
                )
            @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors still trips on real error not covered by suppression`` () =
    // Load-bearing regression: the symmetry fix must not weaken the F38 gate
    // for diagnostics the user has NOT silenced. A real type error with no
    // matching suppression must still hold the prior DB snapshot.
    withTempDir "tp-poisoning-loadbearing" (fun tmpDir ->
        let source =
            """module Test
let x : int = "not-an-int"
"""

        let result =
            checkSourceForReal tmpDir "Real.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults @>

        // Suppressing an unrelated code must NOT mask the real FS0001.
        test
            <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors (Set.singleton 9999) result.Source result.CheckResults @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked with FCS errors persists symbols to DB and stamps sidecar dirty`` () =
    // Path D contract (replaces the prior F38 "withhold" behaviour):
    // dirty FCS results no longer block the symbol-DB write. Symbols flow
    // through to the DB as normal; the protection that prevented Phase B
    // from spuriously seeing "0 stored" is moved to the FsHotWatch-owned
    // freshness sidecar, which marks the file `fcsClean = false`. Phase B
    // detectChanges then bypasses the diff for that file rather than
    // computing a phantom "all symbols changed" delta against an empty
    // stored row set.
    withTempDir "tp-poisoning-persist-dirty" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Broken"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        let brokenSource =
            """module Broken
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let brokenTest () = ()

let badTypeUse : int = "not-an-int"
"""

        let brokenFile = Path.Combine(tmpDir, "Broken.fsx")
        File.WriteAllText(brokenFile, brokenSource)

        let projOptions =
            getScriptOptions checker brokenFile brokenSource |> Async.RunSynchronously

        pipeline.RegisterProject(brokenFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create brokenFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Confirm the result is poisoned (Error-severity diagnostics).
        test <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults @>

        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        // Drive a flush via BuildSucceeded.
        emitBuildAndWaitTerminal host

        // NEW contract: symbols ARE in the DB (gate no longer withholds the write).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        let freshDb = Database.create dbPath
        let symbols = freshDb.GetSymbolsInFile "Broken.fsx"
        test <@ not symbols.IsEmpty @>

        // NEW contract: the freshness sidecar marks the file dirty so Phase B
        // detectChanges bypasses it.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Broken.fsx" freshness) @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked without FCS errors flushes symbols to DB (gate doesn't break clean path)`` () =
    withTempDir "tp-poisoning-cleanflush" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Clean"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        let cleanSource =
            """module Clean
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let cleanTest () = ()
"""

        let cleanFile = Path.Combine(tmpDir, "Clean.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>

        // Item 3 ordering: BuildSucceeded fires FIRST (matches real fshw cold
        // scan — BuildPlugin's terminal status gates FCS tier checks). The
        // sidecar's `markClean` only fires for FileChecked events that arrive
        // AFTER BuildCompleted has been observed in this session.
        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result
        // BatchChecked drives the cohort-complete flush that persists
        // accumulated FileChecked analysis to the DB.
        emitBatchAndQuiesce host [ cleanFile ]

        // Symbols MUST be in DB.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        let mutable testMethods: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let freshDb = Database.create dbPath
                testMethods <- freshDb.GetTestMethodsInFile "Clean.fsx"
                testMethods.Length >= 1)
            5000

        test <@ testMethods.Length >= 1 @>
        test <@ testMethods |> List.exists (fun t -> t.TestMethod = "cleanTest") @>

        // NEW Path D + Item 3 contract: clean FCS check that arrives AFTER
        // BuildCompleted stamps the sidecar `fcsClean = true` so Phase B
        // detectChanges trusts the stored rows for this file.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "Clean.fsx" freshness @>)

[<Fact(Timeout = 30000)>]
let ``BatchChecked persists accumulated symbols to DB without a follow-up BuildCompleted`` () =
    // Phase B persistence regression (2026-05-02): on a cold scan, performScan
    // awaits BuildPlugin terminal BEFORE FCS tier checks, so BuildCompleted
    // arrives at the TestPrune mailbox before any FileChecked. The
    // BuildCompleted handler then flushes against an empty PendingAnalysis
    // (no-op). FileChecked × N follow, populating PendingAnalysis. BatchChecked
    // is the cohort-complete signal — it MUST flush the accumulated analysis,
    // otherwise the symbol DB stays empty and every subsequent cold scan
    // perpetuates the empty-DB state.
    withTempDir "tp-batchchecked-flush" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        // No testConfigs — BuildCompleted is unsubscribed; only FileChecked
        // and BatchChecked drive the flush. The BatchChecked subscription is
        // unconditional (independent of testConfigs).
        let handler = create dbPath tmpDir None None None None None
        host.RegisterHandler(handler)

        let cleanSource =
            """module Clean
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let cleanTest () = ()
"""

        let cleanFile = Path.Combine(tmpDir, "Clean.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        // Cohort-complete signal — no BuildCompleted ever fires.
        emitBatchAndQuiesce host [ cleanFile ]

        // Symbols MUST be in DB after BatchChecked, even without a
        // subsequent BuildCompleted.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        let mutable testMethods: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let freshDb = Database.create dbPath
                testMethods <- freshDb.GetTestMethodsInFile "Clean.fsx"
                testMethods.Length >= 1)
            5000

        test <@ testMethods.Length >= 1 @>
        test <@ testMethods |> List.exists (fun t -> t.TestMethod = "cleanTest") @>)

[<Fact(Timeout = 60000)>]
let ``cold-boot regression: dirty FCS leaves sidecar dirty so detectChanges falls back`` () =
    // Path D replaces the prior cold-boot regression test. Under the old F38
    // gate the contract was "prior DB rows survive a dirty FCS check."
    // Under Path D the contract is: dirty FCS may overwrite rows (we always
    // persist), BUT the freshness sidecar marks the file dirty so a future
    // `detectChanges` against those potentially-poisoned rows is bypassed
    // rather than producing a phantom large diff.
    //
    // What we still must protect against is a *spurious large diff* — the
    // 4921-affected-tests Phase B regression. The sidecar is the load-bearing
    // piece for that, not the symbol-DB write decision.
    withTempDir "tp-poisoning-coldboot" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        // testConfigs is required for the plugin to subscribe to BuildCompleted
        // and run the flush-and-query cycle. Command is a no-op.
        let testConfigs =
            [ { Project = "CB"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        // Phase 1: clean check, flush populates DB.
        let host1 = PluginHost.create checker tmpDir
        let handler1 = create dbPath tmpDir (Some testConfigs) None None None None
        host1.RegisterHandler(handler1)

        let cleanSource =
            """module CB
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let coldBootTest () = ()
"""

        let file = Path.Combine(tmpDir, "CB.fsx")
        File.WriteAllText(file, cleanSource)

        let projOptions =
            getScriptOptions checker file cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(file, projOptions)

        let cleanResult =
            pipeline.CheckFile(AbsFilePath.create file)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None (clean)")

        // Item 3 ordering: BuildSucceeded fires FIRST, then FileChecked, then
        // BatchChecked drives the flush. Mirrors the real fshw cold-scan
        // pipeline (BuildPlugin terminal gates FCS tier checks). Only this
        // ordering allows the sidecar to stamp `fcsClean = true`.
        emitBuildAndWaitTerminal host1

        emitFileAndQuiesce host1 cleanResult
        emitBatchAndQuiesce host1 [ file ]

        // Verify Phase 1 populated DB.
        let mutable phase1Tests: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let db = Database.create dbPath
                phase1Tests <- db.GetTestMethodsInFile "CB.fsx"
                phase1Tests.Length >= 1)
            5000

        test <@ phase1Tests.Length >= 1 @>

        // Phase 2: simulate cold-boot poisoning with the same file but
        // synthesized broken source — fresh plugin instance reading prior DB,
        // FileChecked carries Error-severity diagnostics. The gate must
        // prevent the prior good DB rows from being overwritten.
        let brokenSource =
            """module CB
type FactAttribute() = inherit System.Attribute()

[<Fact>]
let coldBootTest () = ()

let badTypeUse : int = "wrong-type"
"""

        File.WriteAllText(file, brokenSource)

        let projOptionsBroken =
            getScriptOptions checker file brokenSource |> Async.RunSynchronously

        let pipeline2 = CheckPipeline(checker)
        pipeline2.RegisterProject(file, projOptionsBroken)

        let brokenResult =
            pipeline2.CheckFile(AbsFilePath.create file)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None (broken)")

        test
            <@
                FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors
                    Set.empty
                    brokenResult.Source
                    brokenResult.CheckResults
            @>

        let host2 = PluginHost.create checker tmpDir
        let handler2 = create dbPath tmpDir (Some testConfigs) None None None None
        host2.RegisterHandler(handler2)

        // Item 3 ordering for Phase 2 too: BuildSucceeded first, then dirty
        // FileChecked. This exercises the `markUnverified` preservation rule:
        // a prior `fcsClean = true` record (from Phase 1) is NOT downgraded to
        // dirty even though the current FCS check has Error-severity
        // diagnostics. The trade-off is intentional — cold-start reliability
        // over correctness on user-broke-their-code transients. The next
        // genuine clean check refreshes the timestamp.
        emitBuildAndWaitTerminal host2

        emitFileAndQuiesce host2 brokenResult

        // Item 3 contract: prior clean record from Phase 1 survives a dirty
        // Phase 2 FileChecked.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "CB.fsx" freshness @>

        // Even though the sidecar still reads clean, the *current* FCS check
        // is dirty — `currentClean` in the FileChecked handler is false, so
        // detectChanges is bypassed for this event regardless of stored state.
        // ChangedFiles therefore does not gain a phantom entry from the
        // poisoned check.
        let changedAfterDirty =
            host2.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedAfterDirty.Value = "[]" @>)

// =============================================================================
// Path D — per-file freshness sidecar gates the detectChanges call site so
// cross-restart Phase B replay only computes a real diff for files that
// ended their last session FCS-clean. This is the load-bearing change for
// the 4921-affected-tests Phase B regression: without the sidecar gate, a
// fresh daemon's first FCS check sees ~0 stored rows for files whose prior
// session ended dirty, producing a phantom "all symbols changed" delta.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``Phase B replay: stored=dirty, current=clean → detectChanges bypassed`` () =
    // Pre-populate the DB with stale (deliberately empty) symbol rows for a
    // file whose prior session ended FCS-dirty. The freshness sidecar
    // already records that file as dirty. The plugin then receives a fresh
    // FCS-clean FileChecked for the same file with full symbols. Without
    // the sidecar gate, detectChanges would report N current vs 0 stored =
    // N changes (the Phase B 4921-affected-tests bug). With the gate, the
    // diff is bypassed and ChangedFiles stays empty.
    withTempDir "tp-phaseb-bypass" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let relPath = "PhaseB.fsx"
        let absPath = Path.Combine(tmpDir, relPath)

        // Seed the sidecar with a dirty entry for this file (simulates "prior
        // session ended dirty"). Empty DB rows for the file simulates "F38
        // gate previously withheld the write" — though under the new contract
        // the rows are written, the sidecar is what gates the diff.
        let earlier = DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)

        let priorSidecar =
            Map.empty
            |> Map.add
                relPath
                { FsHotWatch.TestPrune.FileFreshness.FcsClean = false
                  FsHotWatch.TestPrune.FileFreshness.LastCleanCheckAt = Some earlier }

        FsHotWatch.TestPrune.FileFreshness.save tmpDir priorSidecar

        let cleanSource =
            """module PhaseB
type FactAttribute() = inherit System.Attribute()

let usefulValue = 42
let anotherValue = "hello"

[<Fact>]
let phaseBTest () = ()
"""

        File.WriteAllText(absPath, cleanSource)

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let projOptions =
            getScriptOptions checker absPath cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(absPath, projOptions)

        // testConfigs needed so the plugin subscribes to BuildCompleted;
        // Item 3 gates `markClean` on a BuildCompleted having been observed.
        let testConfigs =
            [ { Project = "PhaseB"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        let result =
            pipeline.CheckFile(AbsFilePath.create absPath)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Sanity: this is a clean check.
        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>

        // Item 3 ordering: BuildSucceeded first so the FileChecked that
        // follows is allowed to promote the sidecar to clean.
        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result

        // Load-bearing: ChangedFiles must NOT include relPath. Stored=empty,
        // current=N would produce N changes without the gate; with the gate
        // the diff is bypassed entirely (sidecar said dirty when FileChecked
        // arrived, so storedClean=false and the diff is skipped).
        let changedFiles = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedFiles.Value = "[]" @>

        // The clean recheck flips the sidecar from dirty → clean so the NEXT
        // restart's Phase B (post-this-session) trusts the rows.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean relPath freshness @>)

[<Fact(Timeout = 30000)>]
let ``Phase B replay: stored=clean → detectChanges runs as today`` () =
    // Counterpart to the prior test. Once a file has gone clean → clean
    // across a restart boundary, detectChanges runs normally and a real AST
    // change produces a real diff. This guards against an over-aggressive
    // gate that would mask legitimate changes.
    let initialSource = "module Lib\nlet x = 1\n"
    let astChangedSource = "module Lib\nlet x = 1\nlet y = 2\n"

    withSeededTestEnv "tp-phaseb-realdiff" "Lib.fs" initialSource (fun env ->
        // Real AST change. detectChanges should report a diff.
        File.WriteAllText(env.FilePath, astChangedSource)

        match
            env.Pipeline.CheckFile(AbsFilePath.create env.FilePath)
            |> Async.RunSynchronously
        with
        | None -> Assert.Fail("FCS failed on AST-changed source")
        | Some r -> env.Host.EmitFileChecked(r)

        waitForTerminalStatus env.Host "test-prune" 30000

        let changed = env.Host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
        test <@ changed.Value.Contains(env.RelPath) @>)

// =============================================================================
// Item 3 — BuildCompleted-gated stamping. The sidecar's `markClean` only
// fires for FileChecked events that arrive AFTER a BuildCompleted has been
// observed in the current session. Pre-build FileChecked events stamp
// `markUnverified` (treated as dirty unless a prior clean record exists).
// This eliminates the cold-FCS-vs-warm-FCS extractor-stability problem
// the Path D fcs-clean predicate alone couldn't solve: by the time
// fshw's pipeline emits BuildCompleted, FCS has been warmed by the build's
// reference-graph realization, so subsequent FileChecked events extract
// the same number of symbols a Phase B warm rerun would.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``Item 3: pre-BuildCompleted clean FileChecked → sidecar stays dirty`` () =
    // Mirrors the realistic case where, on plugin startup, sidecar is empty
    // and a FileChecked arrives BEFORE any BuildCompleted. The plugin must
    // refuse to stamp clean — `currentClean=true` is necessary but not
    // sufficient; warm-enough state is signalled by BuildCompleted.
    withTempDir "tp-item3-pre-build-clean" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Pre"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        let cleanSource = "module Pre\nlet n = 1\n"
        let cleanFile = Path.Combine(tmpDir, "Pre.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Sanity.
        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>

        // Critically — emit FileChecked WITHOUT a prior BuildCompleted.
        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        // Item 3: even though the FCS check itself was clean, the absence of
        // a prior BuildCompleted means the plugin stamps `markUnverified` →
        // sidecar entry is fcsClean=false.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Pre.fsx" freshness) @>)

[<Fact(Timeout = 30000)>]
let ``Item 3: post-BuildCompleted clean FileChecked → sidecar stamped clean`` () =
    // Counterpart: same harness, but BuildCompleted fires first. The plugin
    // observes the build, sets its session flag, and the subsequent
    // FileChecked is allowed to promote the sidecar entry to clean.
    withTempDir "tp-item3-post-build-clean" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Post"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        let cleanSource = "module Post\nlet n = 1\n"
        let cleanFile = Path.Combine(tmpDir, "Post.fsx")
        File.WriteAllText(cleanFile, cleanSource)

        let projOptions =
            getScriptOptions checker cleanFile cleanSource |> Async.RunSynchronously

        pipeline.RegisterProject(cleanFile, projOptions)

        let result =
            pipeline.CheckFile(AbsFilePath.create cleanFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // Build first.
        emitBuildAndWaitTerminal host

        // Then FileChecked.
        emitFileAndQuiesce host result

        // Item 3 promotion: sidecar now records the file clean.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "Post.fsx" freshness @>)

[<Fact(Timeout = 30000)>]
let ``Item 3: clean check after prior dirty, still pre-build → stays dirty`` () =
    // Two FileCheckeds in sequence, NO BuildCompleted. First is dirty, second
    // is clean. The clean-but-pre-build event must NOT promote the entry —
    // exactly because warm extraction stability isn't guaranteed yet.
    withTempDir "tp-item3-dirty-then-clean" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let testConfigs =
            [ { Project = "Mixed"
                Command = "echo"
                Args = "ok"
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None
        host.RegisterHandler(handler)

        let mixedFile = Path.Combine(tmpDir, "Mixed.fsx")

        // Phase 1: dirty source, dirty FileChecked.
        let dirtySource =
            """module Mixed
let bad : int = "not-an-int"
"""

        File.WriteAllText(mixedFile, dirtySource)

        let dirtyOpts =
            getScriptOptions checker mixedFile dirtySource |> Async.RunSynchronously

        pipeline.RegisterProject(mixedFile, dirtyOpts)

        let dirtyResult =
            pipeline.CheckFile(AbsFilePath.create mixedFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile None (dirty)")

        test
            <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty dirtyResult.Source dirtyResult.CheckResults @>

        emitFileAndQuiesce host dirtyResult

        // Phase 2: same file, clean source. Re-check via a fresh pipeline so
        // FCS reanalyzes against the new source.
        let cleanSource = "module Mixed\nlet n = 1\n"
        File.WriteAllText(mixedFile, cleanSource)

        let cleanOpts =
            getScriptOptions checker mixedFile cleanSource |> Async.RunSynchronously

        let pipeline2 = CheckPipeline(checker)
        pipeline2.RegisterProject(mixedFile, cleanOpts)

        let cleanResult =
            pipeline2.CheckFile(AbsFilePath.create mixedFile)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile None (clean)")

        test
            <@
                not (
                    FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors
                        Set.empty
                        cleanResult.Source
                        cleanResult.CheckResults
                )
            @>

        emitFileAndQuiesce host cleanResult

        // No BuildCompleted has fired in this session. Even though the latest
        // FCS check is clean, Item 3 refuses to promote — sidecar stays dirty.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Mixed.fsx" freshness) @>)

// =============================================================================
// detectChanges call site: stored vs current must agree on unit. The DB stores
// externs under the synthetic SourceFile "_extern", and `GetSymbolsInFile`
// filters by source_file = relPath — so the stored side is file-local only.
// Before this fix, the call passed unfiltered `normalizedSymbols` (file-local +
// externs) on the current side, producing a phantom diff equal to the file's
// extern count for every clean re-check. ~80% of every file's allSymbols are
// externs in real codebases, hence "Phase B always reports 4921 affected tests"
// in Intelligence stress runs. Fix: filter current symbols by SourceFile match
// before invoking detectChanges.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``detectChanges: re-check of unchanged source with externs reports no changes`` () =
    // Source uses List.length so the extractor pulls in
    // Microsoft.FSharp.Collections.List.length as an extern symbol.
    let source = "module Lib\nlet xs = List.length []\n"

    withSeededTestEnv "tp-extern-filter" "Lib.fsx" source (fun env ->
        // Sanity: extracted set actually contains externs (otherwise the test
        // tautologically passes regardless of the fix).
        let externs = env.SeededSymbols |> List.filter (fun s -> s.IsExtern)
        test <@ not externs.IsEmpty @>

        // Sanity: DB read-back is file-local only — externs absent.
        let storedFromDb = env.Db.GetSymbolsInFile(env.RelPath)
        test <@ storedFromDb |> List.forall (fun s -> not s.IsExtern) @>

        // Re-check the IDENTICAL source — no edit. detectChanges should report
        // zero changes. Without TestPrune.Core's internal extern filter, externs
        // in the current set would produce a phantom diff equal to externs.Length.
        match
            env.Pipeline.CheckFile(AbsFilePath.create env.FilePath)
            |> Async.RunSynchronously
        with
        | None -> Assert.Fail("FCS failed on re-check")
        | Some r -> env.Host.EmitFileChecked(r)

        waitForTerminalStatus env.Host "test-prune" 30000

        let changedFiles =
            env.Host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedFiles.Value = "[]" @>)

// =============================================================================
// Issue 1 — cold-start apphost-missing must NOT be reported as a spurious
// FAILED. A `dotnet run --no-build` launched before the build plugin produced
// the apphost fails with an "An error occurred trying to start process … No
// such file or directory" message — distinct from a genuine non-zero test
// exit. `looksLikeApphostMissing` is the classifier that distinguishes the two.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing detects the start-process launch failure`` () =
    // The exact shape the .NET host emits when `dotnet run --no-build` cannot
    // find the apphost binary because the build plugin hasn't produced it yet.
    let output =
        "Unhandled exception: System.ComponentModel.Win32Exception (2): An error occurred trying to start process '/repo/tests/Unit/bin/Debug/net10.0/Unit' with working directory '/repo'. No such file or directory"

    test <@ looksLikeApphostMissing output @>

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing is false for a genuine test failure`` () =
    // A real xUnit/MTP failure carries `failed <name>` + a `failed:` summary,
    // never the start-process signature. Misclassifying this as apphost-missing
    // would SILENCE real reds — the opposite, and worse, failure mode.
    let output =
        "failed FsHotWatch.Tests.FooTests.bar (3ms)\nTest run summary: Failed!\n  total: 10\n  failed: 1\n  succeeded: 9"

    test <@ not (looksLikeApphostMissing output) @>

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing is false for empty / passing output`` () =
    test <@ not (looksLikeApphostMissing "") @>
    test <@ not (looksLikeApphostMissing "Test run summary: Passed!\n  total: 5\n  succeeded: 5") @>

// Issue 2: STRUCTURAL apphost detection. `tryApphostPresent` derives the
// apphost binary path from the runner's `--project` arg and File.Exists-checks
// it, instead of sniffing localized OS error text.

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent returns None when args carry no --project`` () =
    // A custom, non-`dotnet run` command isn't derivable — caller must fall
    // back to the output sniff.
    test <@ tryApphostPresent "/tmp/runner.sh" "/repo" = None @>
    test <@ tryApphostPresent "test" "/repo" = None @>

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports false when the bin dir is absent`` () =
    withTempDir "tp-apphost-struct-missing" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        Directory.CreateDirectory(projDir) |> ignore
        // No bin/Debug at all → apphost definitionally absent.
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some false @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports false when bin exists but apphost is missing`` () =
    withTempDir "tp-apphost-struct-empty-bin" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        // Only the DLL landed, not the apphost.
        File.WriteAllText(Path.Combine(tfmDir, "Unit.dll"), "")
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some false @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports true when the apphost binary exists`` () =
    withTempDir "tp-apphost-struct-present" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        // The extension-less apphost sibling of the canonical DLL.
        File.WriteAllText(Path.Combine(tfmDir, "Unit"), "")
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some(true) @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent resolves an fsproj --project to its assembly name`` () =
    withTempDir "tp-apphost-struct-fsproj" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        let fsproj = Path.Combine(projDir, "MyTests.fsproj")
        File.WriteAllText(fsproj, "<Project/>")
        // Apphost name follows the project file base name, not the dir leaf.
        File.WriteAllText(Path.Combine(tfmDir, "MyTests"), "")
        test <@ tryApphostPresent $"run --project {fsproj} --no-build --" tmpDir = Some(true) @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent finds a Windows .exe apphost`` () =
    withTempDir "tp-apphost-struct-exe" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        File.WriteAllText(Path.Combine(tfmDir, "Unit.exe"), "")
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some(true) @>)

// Issue 4: the cold-start and persistent apphost-missing cases share all
// scaffolding and differ only in the runner script + the expected verdict, so
// they collapse into one [<Theory>]. `transient` = the apphost-missing failure
// clears on retry (cold-start race); otherwise it persists every run.
//
// These configs run a bare `sh <script>` (no `--project` arg), so the
// structural `tryApphostPresent` check returns None and the plugin falls back
// to the `looksLikeApphostMissing` output sniff — exercising the defensive
// fallback path end-to-end.
[<Theory(Timeout = 20000)>]
[<InlineData(true)>] // cold-start: fails once with the launch signature, then succeeds
[<InlineData(false)>] // persistent: apphost never appears
let ``apphost-missing cold-start retries green; persistent defers non-green (never FAILED test)`` (transient: bool) =
    withTempDir "tp-apphost" (fun tmpDir ->
        let scriptPath = Path.Combine(tmpDir, "runner.sh")

        // The launch-failure line carries the .NET host's start-process
        // signature (no test-summary block), which the fallback sniff
        // classifies as apphost-missing. Counter lives in the working dir
        // (repoRoot = tmpDir); written to a file to avoid fragile nested
        // shell quoting through the F# arg string.
        let launchFailure =
            "echo \"Unhandled exception: An error occurred trying to start process '/x/bin/Debug/net10.0/Unit' with working directory '/x'. No such file or directory\" 1>&2"

        let script =
            if transient then
                // First run: emit the failure and exit 1. Retry: exit 0.
                "n=$(cat attempts 2>/dev/null || echo 0)\n"
                + "n=$((n+1))\n"
                + "echo $n > attempts\n"
                + "if [ \"$n\" -le 1 ]; then\n"
                + "  "
                + launchFailure
                + "\n"
                + "  exit 1\n"
                + "else\n"
                + "  echo ok\n"
                + "  exit 0\n"
                + "fi\n"
            else
                // Apphost never appears — fails identically every run.
                launchFailure + "\n" + "exit 1\n"

        File.WriteAllText(scriptPath, script)

        let configs =
            [ { Project = "Unit"
                Command = "sh"
                Args = scriptPath
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 15.0

        // In NEITHER case may an apphost-missing launch be reported as a test
        // FAILED — that's an ordering bug, never a real red.
        let failingReasons =
            host.GetErrorsByPlugin("test-prune")
            |> Map.toList
            |> List.collect snd
            |> List.filter (fun e -> e.Severity = FsHotWatch.ErrorLedger.Error)

        test
            <@
                failingReasons
                |> List.forall (fun e -> not (e.Message.ToLowerInvariant().Contains("tests failed")))
            @>

        if transient then
            // Retry succeeded → PASSED. No failing reasons, status not Failed.
            test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

            match host.GetStatus("test-prune") with
            | Some(Failed _) -> Assert.Fail("transient apphost-missing was reported as FAILED")
            | _ -> ()
        else
            // Issue 1 regression: a persistently-missing apphost means the tests
            // NEVER RAN — it must be DEFERRED, which is NON-GREEN (nothing was
            // verified; a CI gate must not silent-green it), with an honest
            // "waiting on build" diagnostic rather than a "test failed" one.
            // On pre-Issue-1 code this returned TestsPassed → a false green.
            test <@ host.HasFailingReasons(warningsAreFailures = true) @>

            let waitingDiagnostic =
                failingReasons
                |> List.exists (fun e -> e.Message.ToLowerInvariant().Contains("waiting on build"))

            test <@ waitingDiagnostic @>

            // Status must be non-green (Failed) — but the message must say
            // "waiting on build", not "failed".
            match host.GetStatus("test-prune") with
            | Some(Failed(msg, _)) -> test <@ msg.ToLowerInvariant().Contains("waiting on build") @>
            | other -> Assert.Fail($"expected non-green Failed status for deferred project, got %A{other}"))

// =============================================================================
// Issue 2 — `fshw errors` must reflect ONLY the most recent completed cycle.
// When a cycle re-runs, the plugin's prior-cycle diagnostics must be
// cleared/replaced so stale reds from a superseded run don't accumulate.
// =============================================================================

[<Fact(Timeout = 20000)>]
let ``stale failures from a prior cycle are cleared when the next cycle supersedes them`` () =
    // Cycle 1: ProjA fails (ProjB passes) → ledger holds a ProjA red.
    // Cycle 2: ProjA passes, ProjB fails → ledger must hold ONLY a ProjB red;
    // the superseded ProjA entry must be gone. Before the fix, the
    // Custom(TestsFinished) handler only cleared on the all-pass branch, so the
    // ProjA red from cycle 1 was never cleared when cycle 2 reported ProjB —
    // `fshw errors` showed a stale red the fresh cycle had already cleared.
    //
    // Driven via the `run-tests` IPC command rather than BuildCompleted so each
    // cycle deterministically RE-RUNS the given projects (BuildCompleted's
    // impact path would skip on a warm cycle with no changed symbols).
    withTempDir "tp-stale-clear" (fun tmpDir ->
        let flagA = Path.Combine(tmpDir, "failA")
        let flagB = Path.Combine(tmpDir, "failB")

        let mk (proj: string) (flag: string) =
            { Project = proj
              Command = "sh"
              Args = $"-c \"if [ -f {flag} ]; then exit 1; else exit 0; fi\""
              Group = "default"
              Environment = []
              FilterTemplate = None
              ClassJoin = " "
              TimeoutSec = None }

        let configs = [ mk "ProjA" flagA; mk "ProjB" flagB ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None
        host.RegisterHandler(handler)

        let ledgerFiles () =
            host.GetErrorsByPlugin("test-prune") |> Map.toList |> List.map fst

        let hasFileFor (substr: string) () =
            ledgerFiles () |> List.exists (fun f -> f.Contains(substr))

        // Cycle 1: only ProjA fails. run-tests runs executeTests synchronously
        // then posts TestsFinished; wait for the ledger to reflect the ProjA red.
        File.WriteAllText(flagA, "")
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        waitUntil (hasFileFor "ProjA") 12000

        let cycle1Files = ledgerFiles ()
        test <@ cycle1Files |> List.exists (fun f -> f.Contains("ProjA")) @>
        test <@ not (cycle1Files |> List.exists (fun f -> f.Contains("ProjB"))) @>

        // Cycle 2: ProjA now passes, ProjB fails. Wait for the ledger to reflect
        // the new ProjB red (which only appears after the Custom(TestsFinished)
        // handler ran clear-then-report for this cycle).
        File.Delete(flagA)
        File.WriteAllText(flagB, "")
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        waitUntil (hasFileFor "ProjB") 12000

        let cycle2Files = ledgerFiles ()

        // ProjB red is present, ProjA red has been superseded/cleared.
        test <@ cycle2Files |> List.exists (fun f -> f.Contains("ProjB")) @>
        test <@ not (cycle2Files |> List.exists (fun f -> f.Contains("ProjA"))) @>)
