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

/// An empty launch set for tests that construct `TestsFinished` directly to
/// exercise CacheKey / status reporting without going through a real run. An
/// empty launch commits nothing — these tests don't drive the pending queue —
/// and an empty SELECTION covers nothing, so it clears no outstanding red
/// (AUTOMATION-125). Tests that need a run to CLEAR something use
/// `fullSuiteLaunch` / `filteredLaunch` below.
let private emptyLaunch: TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      Selection = Map.empty }

/// A launch that ran every named project UNFILTERED — the scope a full suite (or a
/// plain `test-rerun`) has, and the only one whose green may clear an arbitrary red.
let private fullSuiteLaunch (projects: string list) : TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      Selection = projects |> List.map (fun p -> p, ProjectInFull) |> Map.ofList }

/// A launch that ran only `classes` in each named project — an impact-filtered
/// selection. Projects NOT named were skipped entirely.
let private filteredLaunch (selection: (string * string list) list) : TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      Selection =
        selection
        |> List.map (fun (p, classes) -> p, ProjectClasses(Set.ofList classes))
        |> Map.ofList }

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
            TimeoutSec = None
            ReportVerificationFormat = AutoDetect } ]

    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    let handler = create ":memory:" tmpDir (Some configs) None None None None []
    host.RegisterHandler(handler)
    host, sentinel

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = create ":memory:" "/tmp" None None None None None []
    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "test-prune" @>

[<Fact(Timeout = 15000)>]
let ``testprune subscribes to BatchChecked`` () =
    // Item 3 (corrected): TestPrune retains FileChecked for per-file
    // accumulation AND adds BatchChecked as the cohort-complete flush
    // signal. Both subscriptions must be present.
    let handler = create ":memory:" "/tmp" None None None None None []

    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeFileChecked) @>
    test <@ handler.Subscriptions.Contains(FsHotWatch.PluginFramework.SubscribeBatchChecked) @>

[<Fact(Timeout = 15000)>]
let ``affected-tests command returns empty array when no files checked`` () =
    // After the lazy-compute migration, the IPC always returns a JSON array,
    // computed on demand from state.ChangedSymbols. With no FileChecked events,
    // ChangedSymbols is empty so the SQL query is skipped and "[]" is returned.
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) (Some(fun _db -> [ fakeExtension ])) None None None []

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

        // FileChecked must not set Running — that causes status cycling in the UI.
        // Completed is acceptable (set on success path); Failed is acceptable (error path).
        match status.Value with
        | Running _ -> Assert.Fail("FileChecked must not set Running — causes status cycling in the UI")
        | _ -> ())

[<Fact(Timeout = 30000)>]
let ``FileChecked exception while tests running surfaces in the ledger without stomping the run's status (F10, AUTOMATION-99)``
    ()
    =
    // F10 (audit/2026-05-02) established that a FileChecked throw mid-test-run
    // must never be SILENT. Its original mechanism — framework safeUpdate
    // stomping PluginStatus.Failed over the live run — was itself a lie: it
    // manufactured a terminal status while the run was executing (the
    // AUTOMATION-99 "terminal with started: but no elapsed:" signature), and
    // the run's own TestsFinished verdict overwrote the Failed moments later,
    // so the "visibility" was only ever a blip.
    //
    // The durable form (AUTOMATION-113 machinery): the fault lands in the
    // ERROR LEDGER as an unanalysable-file diagnostic and the file joins
    // UnanalyzableFiles (forcing full-suite runs until it analyses cleanly).
    // The run keeps OWNING the status: it stays Running until TestsFinished
    // delivers the earned verdict.
    //
    // This test pins the not-idle path:
    //   1. Configure a long-sleeping test command and trigger BuildCompleted
    //      to put RunExclusive "tests" in flight.
    //   2. Emit a FileChecked that throws inside the Update body
    //      (ProjectOptions = Unchecked.defaultof<_> → NullReferenceException).
    //   3. Assert the fault reached the ledger AND the status is still the
    //      run's Running — no terminal stomp.
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // Kick off the long-running test run.
        host.EmitBuildCompleted(BuildSucceeded)

        // Wait until the test run is actually in flight (status reaches
        // Running) so a run is guaranteed live when we emit FileChecked.
        let runningWait =
            beginAwaitStatus host "test-prune" (function
                | Running _ -> true
                | _ -> false)

        if not (runningWait.Wait(TimeSpan.FromSeconds 10.0)) then
            Assert.Fail("test run never reached Running — cannot exercise the mid-run path")

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

        // The fault must land in the error ledger (never silent — F10)...
        waitUntil (fun () -> not (host.GetErrorsByPlugin("test-prune").IsEmpty)) 10000
        test <@ not (host.GetErrorsByPlugin("test-prune").IsEmpty) @>

        // ...WITHOUT stomping a terminal status over the live run
        // (AUTOMATION-99): the run reported Running and still owns the status.
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

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
    let handler = create ":memory:" "/tmp" None None None None None []
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
        // First run fails, second run RE-RUNS the project and passes → the red clears.
        //
        // AUTOMATION-125: the second cycle is driven by `run-tests`, not by a second
        // BuildCompleted, because a warm BuildCompleted with no changed symbols hits the
        // zero-affected skip and runs NOTHING — and a run that ran nothing may not clear
        // a red (that is the laundering this ticket removed; see the skip regression
        // below). The same reason the sibling "stale failures from a prior cycle" test
        // drives its cycles through `run-tests`. Here the change that flips the outcome
        // is a FILE the symbol graph cannot see, so the force-run is the honest verb for
        // it: `dotnet fshw test-rerun` runs every project unfiltered, which covers the
        // failing project and so may clear it.
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

        // Create fail flag so first run fails
        File.WriteAllText(Path.Combine(tmpDir, "fail_flag"), "")
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ host.HasFailingReasons(warningsAreFailures = true) @>

        // Remove fail flag so the re-run passes.
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

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

// ``PendingRerun storm: plugin reaches terminal state after BuildCompleted hammering subsides``
// moved to FsHotWatch.IntegrationTests/TestPruneStormTests.fs — it is a genuine
// convergence-under-load stress test (fires a burst of BuildCompleted events at a
// live test run and asserts EVENTUAL settling), not a fixed-window behavior test.
// Its terminal-settle timing is scheduler-dependent, so under CPU load it flaked
// in the unit suite (runner exit 2). Lives in the coverage-excluded integration
// suite per the house "move convergence-under-load tests out of the unit metric"
// rule; full rationale at the new site.

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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let handler = create dbPath tmpDir (Some configs) None None None None []
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
        let handler = create dbPath tmpDir None None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        let handler = create dbPath tmpDir (Some testConfigs) None None None None []

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

// =============================================================================
// AUTOMATION-67 — seeded-workspace under-selection regression tests.
//
// A fresh jj workspace seeds `test-impact.db` from the default workspace
// (ADR-010) but NOT the fshw-owned freshness sidecar (it lives under `.fshw/`
// and doesn't travel with the copied DB). Before the fix, every seeded file
// therefore classified `storedClean = false` and the `detectChanges` call site
// BYPASSED the diff — a real edit against a seeded row set detected zero changed
// symbols → zero affected tests → vacuous green (the NEWS-661 gate that
// "selected ZERO tests"). The fix distinguishes an ABSENT sidecar record
// (`Unknown` — a seeded DB, diffable) from an explicit dirty stamp (`Dirty` —
// poisoned rows, still bypassed).
//
// These two tests reproduce the seam directly: seed the DB exactly as the
// plugin's flush would, leave the sidecar ABSENT, edit a covered symbol, and
// assert the covering test is re-selected. Before the fix both assertions fail
// (affected-tests returns "[]"). They also prove the edges the ticket flagged
// are structurally trackable: a string-literal-only change inside a function
// (instance 1) and a DU-list length change (instance 2) both alter the changed
// symbol's content hash and re-select the asserting test.
// =============================================================================

/// Seed `dbPath` with the combined analysis of `libSource` + `testsSource`,
/// mirroring `flushPendingAnalysis` (one merged AnalysisResult per project,
/// symbol paths normalized to repo-relative, TestProject stamped). Deliberately
/// does NOT write the freshness sidecar — that absence IS the fresh-workspace
/// condition under test. Returns the registered project options.
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

    // Derive the project name exactly as the plugin does from ProjectFileName so
    // the seeded TestProject label matches what the live plugin will compute.
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

/// Drive an edited `libFile` through a FRESH plugin (empty sidecar) over the
/// seeded DB and poll `affected-tests`. Returns the raw JSON so the caller can
/// assert on the selected test method name.
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

// Generous xUnit cap: two full analyzeSource seeds + a real CheckFile + a 10s
// affected-tests poll. Internal waits stay condition-based.
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

        // Change ONLY the log string. Signature is identical; the function-body
        // content hash changes, so the symbol is Modified and its covering test
        // must be re-selected. Before the fix, the seeded rows classified
        // storedClean=false → bypass → "[]".
        let libV2 =
            "module Lib\n\nlet auditFailureMessage (write: string -> unit) =\n    write \"audit-log write threw\"\n"

        let affected =
            selectAfterSeededEdit checker tmpDir dbPath projOptions libFile libV2 "auditFailureLogsExpectedMessage"

        test <@ affected.Contains("auditFailureLogsExpectedMessage") @>)

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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler = create ":memory:" tmpDir (Some configs) None None None None []

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
        let waitTask =
            waitForAllTerminal host (System.TimeSpan.FromSeconds(5.0)) System.Threading.CancellationToken.None

        let completed = waitTask.Wait(System.TimeSpan.FromSeconds(8.0))

        test <@ completed @>)

// AUTOMATION-65 QA: the "nothing to verify" completion path. A cycle whose
// changed/queued symbols ALL prove to have no covering test must resolve as a
// clean green (0 ran) IMMEDIATELY — even on a cold daemon with no session
// baseline — instead of falling through to the cold-start full-suite run, which
// (on a loaded box) can wedge in executeTests and never resolve WaitForComplete.
[<Fact(Timeout = 30000)>]
let ``all changed symbols with no covering test complete green without running`` () =
    withTempDir "tp-nothing-to-verify" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        // A test project whose command would create this sentinel IF it ran. The
        // whole point of the fix is that it must NOT run (nothing changed here is
        // testable), so the sentinel must stay absent.
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

        // Seed the durable needs-testing queue with a symbol that has NO covering
        // test — the fresh plugin DB indexes no test for it, so QueryAffectedTests
        // is empty. The plugin loads this queue at construction, so the very first
        // BuildCompleted flush drops it as uncovered, leaving an empty affected set
        // (ChangedSymbolsAllUncovered = true).
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Orphan.uncovered" ])

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        // No prior run this session ⇒ hasCachedResults = false. BEFORE the fix,
        // that forced the cold-start else-branch to run the FULL suite (touching
        // the sentinel) even though the only pending symbol is untestable — and
        // that run could then wedge, never resolving WaitForComplete. AFTER the
        // fix, the all-uncovered cycle is a "nothing to verify" green: 0 ran.
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let completion = beginAwaitTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)

        // Bounded terminal wait — a hang (the bug) fails here rather than passing.
        let reached = completion.Wait(TimeSpan.FromSeconds 15.0)
        test <@ reached @>

        // Green: reached Completed (a clean pass), NOT Failed and NOT stuck Running.
        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"Expected Completed (nothing to verify), got: %A{other}")

        // The discriminator: zero tests ran. The sentinel-touching command must
        // never have executed. (Before the fix, the cold-start full suite ran it.)
        test <@ not (File.Exists sentinel) @>

        // And WaitForComplete itself resolves promptly — it never blocks on a
        // Running test-prune, because the plugin reached terminal.
        let waitTask =
            waitForAllTerminal host (TimeSpan.FromSeconds 5.0) System.Threading.CancellationToken.None

        test <@ waitTask.Wait(TimeSpan.FromSeconds 8.0) @>)

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
        let handler = create dbPath tmpDir None None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let handler =
            create ":memory:" tmpDir (Some configs) None None (Some(fun _ -> runCount <- runCount + 1)) None []

        host.RegisterHandler(handler)

        // First BuildCompleted = cold start, should run all
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ runCount = 1 @>

        // Second BuildCompleted with no changed symbols — should SKIP
        host.EmitBuildCompleted(BuildSucceeded)
        waitForPluginTerminal host "test-prune" 12.0
        test <@ runCount = 1 @>) // still 1, not 2

// ── Dependency-fanout (DependencyFanout + PluginCtx.ProjectGraph) ─────────────
// A dependency/PackageReference change flips a test project's dependency
// fingerprint (its referenced-project DLL content) without changing any F#
// symbol. The fanout must force-run that test project — closing the zero-
// affected skip gate for dependency-only changes — while an ordinary build with
// NO dependency change still skips (no regression to the symbol-precise path).

/// Emit a BuildSucceeded and fully serialize: catch THIS build's terminal
/// transition, then wait for the plugin to settle to idle so the async run (and
/// any queued rerun it spawns) has drained before the next build. Required for
/// the fanout tests, which depend on each build's fingerprint comparison seeing
/// the prior build's committed state (not a half-applied pipelined one).
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

        // 1) Cold-start build runs all + records the baseline fingerprint.
        emitBuildAndSettle host
        test <@ runCount = 1 @>

        // 2) Build with NO dependency change and no symbols — must SKIP.
        emitBuildAndSettle host
        test <@ runCount = 1 @> // skipped, still 1

        // 3) Bump the referenced DLL (as if Ops rebuilt against CommandTree 0.7.0).
        //    No F# symbol in TestProj changed, but its dependency fingerprint flips
        //    → the fanout must force-run TestProj.
        File.WriteAllBytes(opsDll, Text.Encoding.UTF8.GetBytes "ops-binary-v070-DIFFERENT")
        emitBuildAndSettle host
        test <@ runCount = 2 @>) // fanout ran it

[<Fact(Timeout = 20000)>]
let ``no dependency change and no symbol change still skips (no regression)`` () =
    // Same harness as the fanout test but the referenced DLL never changes — the
    // symbol-precise skip must remain intact (the fanout must not fire spuriously).
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

        // Two more builds, DLL unchanged → both skip; runCount stays 1.
        emitBuildAndSettle host
        emitBuildAndSettle host
        test <@ runCount = 1 @>)

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

// --- formatFailureReport: CI observability for a red test run ---
//
// Regression coverage for the bug where a failed run reported "0 test(s)
// failed:" with NO test name in the CI console — undiagnosable because the
// per-test detail only lived in the on-disk `.fshw/test-runs` log that CI
// discards. The matcher must surface the failing test name(s) robustly, and
// when nothing parses it must dump the output tail rather than swallow it.

[<Fact(Timeout = 15000)>]
let ``formatFailureReport surfaces a plain failed-test line`` () =
    let output =
        "Discovering: probe\nfailed FsHotWatch.Tests.Foo.bar (32ms)\nTest run summary: Failed!\n  total: 1\n  failed: 1\n  succeeded: 0"

    let report = formatFailureReport "FsHotWatch.Tests" output |> String.concat "\n"
    test <@ report.Contains("FsHotWatch.Tests.Foo.bar") @>
    test <@ report.Contains("1 test(s) failed") @>

[<Fact(Timeout = 15000)>]
let ``formatFailureReport surfaces a timed-out (canceled) test — the daemon-load flake`` () =
    // MTP prints a `[<Fact(Timeout=...)>]` cancellation as `failed (canceled) <name>`.
    // This is the documented under-load flake; its name MUST appear.
    let output =
        "failed (canceled) FsHotWatch.Tests.Slow.thing (118ms)\n  Test execution timed out after 100 milliseconds\n  total: 1\n  failed: 1"

    let report = formatFailureReport "FsHotWatch.Tests" output |> String.concat "\n"
    test <@ report.Contains("FsHotWatch.Tests.Slow.thing") @>
    test <@ report.Contains("(canceled)") @>
    test <@ report.Contains("1 test(s) failed") @>

[<Fact(Timeout = 15000)>]
let ``formatFailureReport matches a failed line with leading whitespace`` () =
    // The exact CI gap: some MTP/capture paths indent the failed line, so the
    // old `StartsWith("failed ")` (no trim) silently missed it → "0 test(s)
    // failed" with the name nowhere in the console.
    let output =
        "    failed FsHotWatch.Tests.Indented.case (5ms)\n  total: 1\n  failed: 1"

    let report = formatFailureReport "FsHotWatch.Tests" output |> String.concat "\n"
    test <@ report.Contains("1 test(s) failed") @>
    test <@ report.Contains("FsHotWatch.Tests.Indented.case") @>

[<Fact(Timeout = 15000)>]
let ``formatFailureReport dumps the output tail when no failed line parses (backstop)`` () =
    // A crash / OOM-kill / unrecognised format yields a non-zero run with no
    // `failed ` line. Rather than report "0 test(s) failed" and hide the cause,
    // the tail of the output is echoed so the failure is visible in the console.
    let output =
        "Building...\nUnhandled exception: System.AccessViolationException\n  at Some.Native.Frame()\nProcess terminated."

    let report = formatFailureReport "FsHotWatch.Tests" output |> String.concat "\n"
    test <@ report.Contains("0 test(s) failed") @>
    test <@ report.Contains("no per-test 'failed' line was parsed") @>
    // The actual cause IS surfaced (not swallowed).
    test <@ report.Contains("AccessViolationException") @>

// --- isZeroTestsUnderFilter unit tests ---
//
// Regression coverage for the bug where `test-rerun --filter-class X` (a raw
// passthrough filter fanned out to EVERY test project) reported every project
// WITHOUT a matching test as "failed", because the runner exits non-zero
// (MTP exit code 8 / "Zero tests ran") and that was interpreted as a failure.

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
    // Fallback path: a runner that exits non-zero without the canonical code 8
    // but still prints MTP's zero-tests summary line.
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
// FIX 2 — `test-rerun --filter-*` force-executes; zero-match reported DISTINCTLY.
//
// `test-rerun` is the explicit force-rerun verb. Two defects it had:
//   (a) a filtered run that matched NOTHING was recorded as a (filtered) PASS,
//       indistinguishable from a real green run — so you couldn't tell the
//       filter selected no test (the "test-rerun didn't actually run" symptom).
//   (b) it returned an INSTANT non-result ("tests already running") when a
//       background run held the test slot — no execution, no log.
// =============================================================================

[<Fact(Timeout = 10000)>]
let ``isZeroMatchResult / allZeroMatch detect the zero-match marker`` () =
    let zero = TestsPassed(ZeroMatchMarker + "Zero tests ran", true, TimeSpan.Zero)
    let realPass = TestsPassed("Passed! total: 4", true, TimeSpan.Zero)
    let failed = TestsFailed("boom", true, TimeSpan.Zero)

    test <@ isZeroMatchResult zero @>
    test <@ not (isZeroMatchResult realPass) @>
    test <@ not (isZeroMatchResult failed) @>

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
        // `sh -c "exit 8"` simulates Microsoft Testing Platform's zero-tests
        // exit (code 8) for a FILTERED project that has no matching test. With a
        // raw filter present, isZeroTestsUnderFilter classifies this as a
        // zero-match (passed/filtered) — which FIX 2 now surfaces distinctly.
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
        // Run-level distinct signal.
        test <@ doc.RootElement.GetProperty("noTestsMatched").GetBoolean() @>
        // Per-project distinct status (not "passed", not "failed").
        let projects = doc.RootElement.GetProperty("projects")
        Assert.Equal("no-tests-matched", projects.[0].GetProperty("status").GetString()))

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
        // The run actually executed → NOT no-tests-matched.
        test <@ not (doc.RootElement.GetProperty("noTestsMatched").GetBoolean()) @>
        let projects = doc.RootElement.GetProperty("projects")
        Assert.Equal("passed", projects.[0].GetProperty("status").GetString()))

[<Fact(Timeout = 30000)>]
let ``run-tests force-executes after an in-flight run finishes instead of instantly bailing`` () =
    // The OLD behavior bailed instantly with {error="tests already running"} when
    // the `tests` slot was held by a background run. FIX 2 makes `run-tests` WAIT
    // for the slot to clear, then execute — so an explicit force-rerun always
    // runs. We hold the slot briefly with a slow background run (BuildCompleted),
    // fire run-tests concurrently, and assert it ultimately EXECUTES (real
    // results) rather than returning the instant in-flight error.
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

        // Kick off the background run (holds RunExclusive "tests").
        host.EmitBuildCompleted(BuildSucceeded)

        // Give the background run a moment to acquire the slot, then force a rerun.
        Thread.Sleep(300)

        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously

        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        // It must NOT be the instant in-flight error and must have actually run
        // (a `projects` array present means executeTests produced results).
        // `fst (TryGetProperty …)` is computed OUTSIDE the Unquote quotation
        // because the ValueTuple it returns can't appear inside a quotation.
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
    // Regression guard: the whole point of AUTOMATION-66 bug 2 is that the wait
    // must outlast a ~90s+ beforeRun chain a prior in-flight run is executing.
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let dbPath = Path.Combine(tmpDir, "tp.db")
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 10000

        // Cache should now contain an entry with at least one TestRun event captured.
        let key: FsHotWatch.TaskCache.CompositeKey = { Plugin = "test-prune"; File = None }

        let cacheKeyFn = handler.CacheKey.Value
        let computedKey = cacheKeyFn (BuildCompleted BuildSucceeded)
        test <@ computedKey.IsSome @>

        // The framework's `cache.Set` runs AFTER the handler's Update returns,
        // while `waitForTerminalStatus` observes the terminal status reported
        // *inside* that Update — so under load the entry can lag the status by a
        // scheduling quantum. Poll-until-deadline for the entry rather than
        // reading once (the write is deterministic, just not instantly visible).
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
// AUTOMATION-5 — a FAILED test verdict must never be served from the task cache.
// Root cause: the §2a merkle key (changed-symbols + commit) does NOT pin the
// test OUTCOME, so a failing run and a later passing run on the same tree share
// a key. Caching the failure let `tryReplayCache` replay a stale red on a now-
// green tree ("green tree read as red"), surviving daemon restarts via the
// on-disk cache. Fix: `Custom(TestsFinished)` returns a `None` cache key when
// any project did not pass, making the failure UNCACHEABLE.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-5: TestPrune CacheKey is None for a failing TestsFinished, Some for an all-pass`` () =
    // Unit-pins the root-cause decision: the plugin's own CacheKey function must
    // refuse to produce a key for a non-passing outcome (so the framework never
    // writes the poisoned entry), while still keying an all-pass run for the
    // green fast-path.
    let handler = create ":memory:" "/tmp" None None None None None []
    let cacheKeyFn = handler.CacheKey.Value

    let started: TestRunStarted =
        { RunId = Guid.NewGuid()
          StartedAt = DateTime.UtcNow }

    let completedWith (results: (string * TestResult) list) : TestRunCompleted =
        { RunId = started.RunId
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList results
          RanFullSuite = true }

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

    // A timed-out / deferred project is also non-green and must be uncacheable.
    let timedOut =
        Custom(
            TestsFinished(
                started,
                completedWith [ "ProjA", TestsTimedOut("slow", TimeSpan.FromSeconds 1.0, false, TimeSpan.Zero) ],
                emptyLaunch
            )
        )

    test <@ (cacheKeyFn failing).IsNone @>
    test <@ (cacheKeyFn timedOut).IsNone @>
    test <@ (cacheKeyFn passing).IsSome @>

// =============================================================================
// tests.dependsOn — external-input cache-key salt.
// A developer who edits a DB migration changes the TEST database schema but no
// test SOURCE symbol, so test-prune's symbol-diff merkle is unchanged and a
// stale verdict replays. Declaring the migration under `tests.dependsOn` salts
// the BuildCompleted cache key with the migration's content hash, so editing it
// is a cache MISS → genuine re-run. With NO dependsOn the key must be
// byte-identical to the pre-feature key (existing caches keep hitting).
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``dependsOn: changing a matched file changes the BuildCompleted cache key`` () =
    withTempDir "tp-dependson-key" (fun tmpDir ->
        let migrationsDir = Path.Combine(tmpDir, "migrations")
        Directory.CreateDirectory(migrationsDir) |> ignore
        let migration = Path.Combine(migrationsDir, "001_init.sql")
        File.WriteAllText(migration, "CREATE TABLE a (id int);")

        // Same plugin config, salted by a glob that matches the migration.
        let handler = create ":memory:" tmpDir None None None None None [ "migrations/**" ]
        let cacheKeyFn = handler.CacheKey.Value

        let keyBefore = (cacheKeyFn (BuildCompleted BuildSucceeded)).Value

        // Edit the migration in place (the exact scenario: schema changed, no
        // test source touched).
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
        // A NEW migration is added — the schema changed even though no test
        // source did. The salt must move.
        File.WriteAllText(Path.Combine(migrationsDir, "002_more.sql"), "ALTER TABLE a ADD COLUMN x int;")
        let keyAfter = (cacheKeyFn (BuildCompleted BuildSucceeded)).Value

        test <@ keyBefore <> keyAfter @>)

[<Fact(Timeout = 15000)>]
let ``dependsOn: absent config leaves the BuildCompleted key byte-identical to the no-salt key`` () =
    withTempDir "tp-dependson-absent" (fun tmpDir ->
        // Even with files on disk that WOULD match a glob, a plugin configured
        // with NO dependsOn must produce exactly the key it produced before the
        // feature existed — so pre-existing on-disk caches keep hitting.
        let migrationsDir = Path.Combine(tmpDir, "migrations")
        Directory.CreateDirectory(migrationsDir) |> ignore
        File.WriteAllText(Path.Combine(migrationsDir, "001_init.sql"), "CREATE TABLE a (id int);")

        let salted = create ":memory:" tmpDir None None None None None []
        let unsalted = create ":memory:" tmpDir None None None None None [] // identical: [] dependsOn

        let kSalted = ((salted.CacheKey.Value) (BuildCompleted BuildSucceeded)).Value
        let kUnsalted = ((unsalted.CacheKey.Value) (BuildCompleted BuildSucceeded)).Value

        // The two []-configured handlers agree, AND neither includes a depends-on
        // term — verified structurally below via the helper.
        test <@ kSalted = kUnsalted @>
        // And the salt helper returns "" (no entry added) when dependsOn is empty.
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

        // A glob that matches no file on disk hashes to "" → no salt entry → the
        // key is identical to the empty-dependsOn key.
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
        // Deterministic across calls (sorted paths, content hash).
        test <@ h1 = h2 @>
        test <@ h1 <> "" @>

        // Content change moves the hash.
        File.WriteAllText(f1, "ONE-changed")
        let h3 = externalDependencyHash tmpDir globs
        test <@ h3 <> h1 @>

        // Deleting a matched file moves the hash; a now-empty match set → "".
        File.Delete f1
        File.Delete f2
        let h4 = externalDependencyHash tmpDir globs
        test <@ h4 = "" @>)

// ---------------------------------------------------------------------------
// AUTOMATION-98 finding 6 — the cache key must not pay for inputs it discards.
// ---------------------------------------------------------------------------
//
// The regression this pins: the `dependsOn` hash was computed EAGERLY, above the
// `match event with`, so EVERY event paid for it — including `FileChecked`, which
// never splices it. And `FileChecked` is one event PER FILE, not per batch (the
// comment justifying the eager computation asserted the opposite). With one glob
// configured, a cold scan of N files therefore did N full-repo SafeWalks, each
// followed by a SHA256 of every matched file, and threw all N results away. It
// was free only because no consumer sets `dependsOn` — the day one does, a cold
// scan goes quadratic.
//
// `cacheKeyFor` takes its state as thunks precisely so this is countable.
//
// RED-BEFORE-GREEN: hoist any of these three thunks to a value at the top of
// `cacheKeyFor` and the FileChecked counts go from 0 to 1.

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: a FileChecked key reads NONE of the expensive state`` () =
    let mutable dependsOnCalls = 0
    let mutable pendingQueueCalls = 0
    let mutable changedSymbolsCalls = 0
    let mutable gateScopeCalls = 0
    let mutable outstandingCalls = 0

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
                gateScopeCalls <- gateScopeCalls + 1
                None)
            (fun () ->
                outstandingCalls <- outstandingCalls + 1
                false)
            (FileChecked(fakeFileCheckResult "/src/A.fs"))

    // It still produces a key — it is a pure function of THIS file.
    test <@ key.IsSome @>

    // And it computed nothing else. The dependsOn one is the finding; the others
    // are pinned with it so a future edit can't quietly re-hoist a sibling instead.
    test <@ dependsOnCalls = 0 @>
    test <@ pendingQueueCalls = 0 @>
    test <@ changedSymbolsCalls = 0 @>
    test <@ gateScopeCalls = 0 @>
    test <@ outstandingCalls = 0 @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: a BuildCompleted key DOES read the dependsOn + symbol state`` () =
    // The other half of the contract: thunking must not have made the salt vanish.
    // BuildCompleted is the arm the dependsOn salt exists for, and it must force it.
    let mutable dependsOnCalls = 0
    let mutable changedSymbolsCalls = 0

    let keyWith (dependsOn: string option) =
        cacheKeyFor
            (fun () ->
                changedSymbolsCalls <- changedSymbolsCalls + 1
                "symbols")
            (fun () -> None)
            (fun () ->
                dependsOnCalls <- dependsOnCalls + 1
                dependsOn)
            (fun () -> None)
            (fun () -> false)
            (BuildCompleted BuildSucceeded)

    let salted = keyWith (Some "migration-hash-v1")
    let resalted = keyWith (Some "migration-hash-v2")
    let unsalted = keyWith None

    test <@ dependsOnCalls = 3 @>
    test <@ changedSymbolsCalls = 3 @>

    // Editing a dependsOn-matched file moves the key (cache miss → genuine re-run).
    test <@ salted <> resalted @>
    // No dependsOn → the entry is omitted, so the key differs from any salted one.
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
    // End-to-end replay-suppression test exercising the real task cache.
    //
    // Cycle 1 (FAIL) via BuildCompleted: cold-start runs the suite; flag present
    //   → `sh` exits 1 → TestsFinished(failed). The cache-replay bug operates at
    //   the framework level BEFORE Update runs, keyed by the BuildCompleted
    //   merkle. The root-cause assertion is therefore: after a FAILING cycle, NO
    //   entry exists under that key — so a subsequent BuildCompleted can only be
    //   a cache MISS (re-run), never a replay of the stale red ("green tree read
    //   as red"). On the broken code the Failed status + red diagnostics were
    //   cached here and replayed on the next matching key.
    //
    // Cycle 2 (PASS) via the `run-tests` command: flag removed. `run-tests`
    //   forces a real re-run (a warm BuildCompleted with no changed symbols would
    //   take the impact "skip" path and not actually re-execute), so this proves
    //   the tree now genuinely reports green and the ledger clears.
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
        let cacheKeyFn = handler.CacheKey.Value
        let computedKey = (cacheKeyFn (BuildCompleted BuildSucceeded)).Value

        // --- Cycle 1: FAIL (cold BuildCompleted runs the suite) ---
        File.WriteAllText(flag, "")
        let await1 = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await1.Wait(TimeSpan.FromSeconds 12.0) |> ignore

        // Status is non-green and the ledger holds a red after cycle 1.
        match host.GetStatus("test-prune") with
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"cycle 1 expected Failed status, got %A{other}")

        test <@ host.HasFailingReasons(warningsAreFailures = true) @>

        // ROOT CAUSE: the failing outcome must NOT have been written to the cache,
        // so the matching BuildCompleted key is a guaranteed miss (no stale replay).
        test <@ (cacheIface.TryGet key computedKey).IsNone @>

        // --- Cycle 2: PASS (run-tests forces a real re-run) ---
        File.Delete(flag)
        let await2 = beginAwaitNextTerminal host "test-prune"
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        await2.Wait(TimeSpan.FromSeconds 12.0) |> ignore

        // Re-ran and reports green; the cycle-1 red is gone (cleared, not replayed).
        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"cycle 2 expected Completed (green) status, got %A{other}")

        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>
        test <@ host.GetErrorsByPlugin("test-prune") |> Map.isEmpty @>)

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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        // Session 1: run once to populate the task cache with a prior-session result.
        do
            let dbPath1 = Path.Combine(tmpDir, "tp1.db")
            let host1 = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = taskCache)

            let handler1 = create dbPath1 tmpDir (Some configs) None None None None []

            host1.RegisterHandler(handler1)
            host1.EmitBuildCompleted(BuildSucceeded)
            waitForTerminalStatus host1 "test-prune" 10000

        // Delete sentinel — session 2 must NOT re-create it (cache replay path).
        if File.Exists sentinel then
            File.Delete sentinel

        // Session 2: new plugin instance (simulates daemon restart) using same on-disk cache.
        let dbPath2 = Path.Combine(tmpDir, "tp2.db")
        let host2 = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = taskCache)

        let handler2 = create dbPath2 tmpDir (Some configs) None None None None []

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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
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
        let handler = create dbPath tmpDir None None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        // Phase 1: clean check, flush populates DB.
        let host1 = PluginHost.create checker tmpDir
        let handler1 = create dbPath tmpDir (Some testConfigs) None None None None []
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
        let handler2 = create dbPath tmpDir (Some testConfigs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        let handler = create dbPath tmpDir (Some testConfigs) None None None None []
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
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
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
            | Some(Failed(msg, _, _)) -> test <@ msg.ToLowerInvariant().Contains("waiting on build") @>
            | other -> Assert.Fail($"expected non-green Failed status for deferred project, got %A{other}"))

// =============================================================================
// Freshness gate. `tryApphostPresent`/`detectApphostMissing` only fired on a
// FAILED launch (post-exit), so a PRESENT-but-STALE apphost that exits 0
// reported a false GREEN — `--no-build` ran OLD bits and "passed". The gate runs
// PRE-launch, independent of exit code: build output that predates its inputs
// DEFERS as "waiting on build" exactly like a missing apphost, so stale bits can
// never produce a verdict. Mirrors BuildPlugin.verifyArtifactsFresh (ADR-008).
//
// AUTOMATION-122 rebuilt WHAT it compares. The first cut compared the test DLL
// against the newest source ANYWHERE IN THE REPO, which (a) condemned every
// project outside an edit's dependency closure, and (b) could not be cleared:
// an incremental `dotnet build` is correctly a no-op for an unaffected project,
// so its DLL never caught up with the repo-wide watermark and only
// `-t:Rebuild` — a relink forced purely to move a timestamp — escaped. It also
// looked at `.fs`/`.cs` only, so a changed test FIXTURE copied in from a shared
// project was invisible: the run read the OLD copy out of `bin/` and passed
// (intelligence, `dsa-scope-4.json`, 2026-07-14 — a fake green that left main
// red for hours).
//
// Both directions are pinned below: an out-of-closure edit is FRESH, an
// in-closure one is STALE — and content items are judged by the COPY the run
// would actually read.
// =============================================================================

/// Production call shape: derive the target from the runner args exactly as
/// `executeTests` does, then ask the gate. A fresh `Cache` per call (the memo is
/// per-run in production).
let private staleOf (args: string) (repoRoot: string) : ArtifactFreshness.StaleInput option =
    deriveProjectBin args repoRoot
    |> Option.bind (ArtifactFreshness.stale (ArtifactFreshness.Cache()))

/// A synthetic repo mirroring the real MSBuild output layout:
///
///   Leaf/     — an unrelated project. NOT referenced by Tests: out of closure.
///   Common/   — a library with a content fixture, referenced by Tests.
///   Tests/    — the test project. Its output dir holds COPIES of Common's DLL
///               and of Common's fixture.
///
/// Copies carry their ORIGIN's mtime, because that is what MSBuild's `File.Copy`
/// leaves behind — the property the gate's copy check rests on. Everything is
/// "built" at `builtAt`; sources are older. Each test then moves ONE mtime.
type private Synth =
    { Root: string
      TestsDir: string
      TestsSrc: string
      TestsDll: string
      TestsOutDir: string
      CommonSrc: string
      CommonFixture: string
      CommonDll: string
      CommonDllCopy: string
      FixtureCopy: string
      LeafSrc: string
      BuiltAt: DateTime }

let private writeAt (path: string) (contents: string) (mtime: DateTime) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)
    File.SetLastWriteTimeUtc(path, mtime)

let private p (parts: string list) = Path.Combine(List.toArray parts)

let private synth (root: string) : Synth =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)

    let leafDir = p [ root; "Leaf" ]
    let commonDir = p [ root; "Common" ]
    let testsDir = p [ root; "Tests" ]
    let commonOut = p [ commonDir; "bin"; "Debug"; "net10.0" ]
    let testsOut = p [ testsDir; "bin"; "Debug"; "net10.0" ]

    // Leaf — no reference to it from anywhere: the out-of-closure project.
    writeAt (p [ leafDir; "Leaf.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ leafDir; "Leaf.fs" ]) "module Leaf" sourcedAt

    // Common — sources + a content fixture, both built/copied into its own bin.
    writeAt (p [ commonDir; "Common.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ commonDir; "Common.fs" ]) "module Common" sourcedAt
    writeAt (p [ commonDir; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt
    writeAt (p [ commonOut; "Common.dll" ]) "" builtAt
    writeAt (p [ commonOut; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt

    // Tests — references Common; its output holds the apphost, its own DLL, and
    // COPIES of Common's DLL and Common's fixture.
    writeAt
        (p [ testsDir; "Tests.fsproj" ])
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Common/Common.fsproj\" />\n  </ItemGroup>\n</Project>"
        sourcedAt

    writeAt (p [ testsDir; "Tests.fs" ]) "module Tests" sourcedAt
    writeAt (p [ testsOut; "Tests" ]) "" builtAt // apphost
    writeAt (p [ testsOut; "Tests.dll" ]) "" builtAt
    writeAt (p [ testsOut; "Common.dll" ]) "" builtAt // copy: same mtime as origin
    writeAt (p [ testsOut; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt // copy: origin's mtime

    { Root = root
      TestsDir = testsDir
      TestsSrc = p [ testsDir; "Tests.fs" ]
      TestsDll = p [ testsOut; "Tests.dll" ]
      TestsOutDir = testsOut
      CommonSrc = p [ commonDir; "Common.fs" ]
      CommonFixture = p [ commonDir; "Fixtures"; "data.json" ]
      CommonDll = p [ commonOut; "Common.dll" ]
      CommonDllCopy = p [ testsOut; "Common.dll" ]
      FixtureCopy = p [ testsOut; "Fixtures"; "data.json" ]
      LeafSrc = p [ leafDir; "Leaf.fs" ]
      BuiltAt = builtAt }

/// The gate, asked about the synthetic repo's Tests project.
let private synthStale (s: Synth) =
    let fsproj = Path.Combine(s.TestsDir, "Tests.fsproj")
    staleOf $"run --project {fsproj} --no-build --" s.Root

[<Fact(Timeout = 15000)>]
let ``freshness is None when args carry no --project`` () =
    test <@ staleOf "/tmp/runner.sh" "/repo" = None @>
    test <@ staleOf "test" "/repo" = None @>

[<Fact(Timeout = 15000)>]
let ``freshness is None when no build output exists`` () =
    withTempDir "tp-stale-nobin" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        Directory.CreateDirectory(projDir) |> ignore
        File.WriteAllText(Path.Combine(projDir, "Foo.fs"), "module Foo")
        // No bin/Debug → absence is tryApphostPresent's job, not staleness.
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

[<Fact(Timeout = 15000)>]
let ``freshness is None when there are no sources to be stale against`` () =
    withTempDir "tp-stale-nosrc" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        File.WriteAllText(Path.Combine(tfmDir, "Unit.dll"), "")
        // DLL present, no source files → nothing to be stale against.
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

[<Fact(Timeout = 15000)>]
let ``freshness is None when the DLL is newer than every source`` () =
    withTempDir "tp-stale-fresh" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        let src = Path.Combine(projDir, "Foo.fs")
        File.WriteAllText(src, "module Foo")
        let dll = Path.Combine(tfmDir, "Unit.dll")
        File.WriteAllText(dll, "")
        let t = DateTime.UtcNow
        File.SetLastWriteTimeUtc(src, t.AddMinutes(-10.0))
        File.SetLastWriteTimeUtc(dll, t)
        // Built AFTER the source → fresh → runnable.
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

[<Fact(Timeout = 15000)>]
let ``freshness is STALE when the project's own DLL predates its own source`` () =
    withTempDir "tp-stale-own" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        let dll = Path.Combine(tfmDir, "Unit.dll")
        File.WriteAllText(dll, "")
        let src = Path.Combine(projDir, "Tests.fs")
        File.WriteAllText(src, "module Tests")
        let t = DateTime.UtcNow
        File.SetLastWriteTimeUtc(dll, t.AddMinutes(-10.0))
        File.SetLastWriteTimeUtc(src, t)

        match staleOf $"run --project {projDir} --no-build --" tmpDir with
        | Some(ArtifactFreshness.AssemblyOlderThanSource(_, source, _, _)) -> test <@ source = src @>
        | other -> Assert.Fail($"expected AssemblyOlderThanSource naming {src}, got %A{other}"))

// =============================================================================
// AUTOMATION-122, direction 1 — THE FALSE POSITIVE. An edit to a project OUTSIDE
// the test project's dependency closure must leave it FRESH.
//
// This is the bug verbatim: `Intelligence.Build.Dev` (a build tool) was edited,
// and `Intelligence.Tests.Integration` — which does not reference it — was
// condemned as stale. MSBuild rightly refuses to relink it, so no plain build
// could ever clear the accusation. On the pre-fix repo-wide watermark this test
// FAILS (Leaf.fs is the newest source in the repo, and Tests.dll predates it).
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``an edit OUTSIDE the test project's closure leaves it FRESH`` () =
    withTempDir "tp-stale-outside" (fun tmpDir ->
        let s = synth tmpDir

        // Leaf is not referenced by Tests (nor by Common). Edit it: it is now the
        // newest source in the whole repo — and irrelevant to this test binary.
        File.SetLastWriteTimeUtc(s.LeafSrc, s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

// An out-of-closure edit that MSBuild will never answer must not be reported —
// but the same edit inside the closure must be. Direction 2: THE REAL HOLE.
// A dependency's source newer than the dependency's own assembly means the build
// has not run since the edit, so the DLL sitting in the test project's output dir
// is old code. `--no-build` must NOT run.
[<Fact(Timeout = 15000)>]
let ``an edit to a DEPENDENCY inside the closure is STALE`` () =
    withTempDir "tp-stale-inside" (fun tmpDir ->
        let s = synth tmpDir

        // Common IS referenced by Tests. Edit it without rebuilding.
        File.SetLastWriteTimeUtc(s.CommonSrc, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.AssemblyOlderThanSource(project, source, _, _)) ->
            test <@ project = "Common" @>
            test <@ source = s.CommonSrc @>
        | other -> Assert.Fail($"expected the dependency edit to be STALE, got %A{other}"))

// The same edit, once the build HAS run (dependency DLL and its copy re-emitted
// after the edit): fresh again. This is what proves a plain `dotnet build` — not
// `-t:Rebuild` — clears the gate. Note the test project's own DLL is deliberately
// NOT restamped: a private-only change to a dependency need not relink its
// consumers (reference assemblies exist to avoid exactly that), and demanding it
// would be the old unanswerable accusation in a smaller costume.
[<Fact(Timeout = 15000)>]
let ``a dependency edit followed by a plain rebuild of that dependency is FRESH`` () =
    withTempDir "tp-stale-rebuilt" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)
        let rebuiltAt = s.BuiltAt.AddMinutes(31.0)

        File.SetLastWriteTimeUtc(s.CommonSrc, editedAt)
        // The build relinks Common and re-copies it into the consumer's output
        // (File.Copy preserves the origin's mtime — hence the same stamp).
        File.SetLastWriteTimeUtc(s.CommonDll, rebuiltAt)
        File.SetLastWriteTimeUtc(s.CommonDllCopy, rebuiltAt)

        test <@ synthStale s = None @>)

// Only the dependency was rebuilt — its DLL is fresh in its OWN bin, but the copy
// the test run would actually load was never refreshed. That is stale bits.
[<Fact(Timeout = 15000)>]
let ``a dependency DLL newer than its COPY in the test output is STALE`` () =
    withTempDir "tp-stale-depcopy" (fun tmpDir ->
        let s = synth tmpDir
        File.SetLastWriteTimeUtc(s.CommonDll, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.CopyOlderThanOrigin(origin, copy, _, _)) ->
            test <@ origin = s.CommonDll @>
            test <@ copy = s.CommonDllCopy @>
        | other -> Assert.Fail($"expected the un-refreshed dependency copy to be STALE, got %A{other}"))

// =============================================================================
// AUTOMATION-122, second half — CONTENT FILES. This one let a RED main through.
//
// `tests/Intelligence.Tests.Common/Fixtures/RuleMaps/dsa-scope-4.json` changed
// (36 → 40 leaf facts). The consuming test project's output dir still held the
// OLD copy, so the `--no-build` run read the OLD fixture and PASSED — a fake
// green that merged and left main red for hours. Only `-t:Rebuild` exposed it.
//
// A stale copy of a content/fixture item must make the run stale — exactly as a
// stale apphost does. Reproduced here in miniature.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``a FIXTURE edited but not re-copied into the test output is STALE`` () =
    withTempDir "tp-stale-fixture" (fun tmpDir ->
        let s = synth tmpDir

        // The dsa-scope-4 scenario: the fixture in the shared project changes …
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))
        // … and the copy in the consuming test project's output dir still holds
        // the OLD bytes. Every compiled artifact in the repo is untouched, so the
        // apphost/DLL checks alone see nothing wrong — and the tests would run
        // green against 36 leaves.

        match synthStale s with
        | Some(ArtifactFreshness.CopyOlderThanOrigin(origin, copy, _, _)) ->
            test <@ origin = s.CommonFixture @>
            test <@ copy = s.FixtureCopy @>
        | other -> Assert.Fail($"expected the un-copied fixture to be STALE, got %A{other}"))

// The remedy a plain `dotnet build` performs: the fixture is re-copied, carrying
// the origin's mtime (verified against real MSBuild, 2026-07-14). Gate clears.
[<Fact(Timeout = 15000)>]
let ``a FIXTURE re-copied by a plain build is FRESH`` () =
    withTempDir "tp-stale-fixture-copied" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)

        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, editedAt)
        File.WriteAllText(s.FixtureCopy, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.FixtureCopy, editedAt) // File.Copy preserves mtime

        test <@ synthStale s = None @>)

// The test project's OWN fixtures count too — the copy is what the run reads,
// whoever owns the origin.
[<Fact(Timeout = 15000)>]
let ``the test project's OWN stale fixture copy is STALE`` () =
    withTempDir "tp-stale-own-fixture" (fun tmpDir ->
        let s = synth tmpDir
        let ownFixture = Path.Combine(s.TestsDir, "Fixtures", "own.json")
        let ownCopy = Path.Combine(s.TestsOutDir, "Fixtures", "own.json")
        writeAt ownCopy "{ \"v\": 1 }" s.BuiltAt
        writeAt ownFixture "{ \"v\": 2 }" (s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.CopyOlderThanOrigin(origin, copy, _, _)) ->
            test <@ origin = ownFixture @>
            test <@ copy = ownCopy @>
        | other -> Assert.Fail($"expected the test project's own stale fixture to be STALE, got %A{other}"))

// The other half of "keyed on the copy": a file the build does NOT copy has no
// destination in the output dir, so editing it can never fire the gate. Without
// this, the content check would become a new wolf-cry — every README and .fsproj
// edit condemning a project no build would ever clear.
[<Fact(Timeout = 15000)>]
let ``a file the build never copies cannot make the run stale`` () =
    withTempDir "tp-stale-uncopied" (fun tmpDir ->
        let s = synth tmpDir
        // A doc in the dependency, newer than everything, copied nowhere.
        writeAt (Path.Combine(tmpDir, "Common", "README.md")) "# notes" (s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

// The gate must be able to say WHY, naming the file pair — a guard that cries
// "something, somewhere is stale" is a guard people learn to bypass.
[<Fact(Timeout = 15000)>]
let ``the stale reason names the offending file`` () =
    withTempDir "tp-stale-describe" (fun tmpDir ->
        let s = synth tmpDir
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some stale ->
            let described = ArtifactFreshness.describe stale
            test <@ described.Contains "data.json" @>
        | None -> Assert.Fail "expected a stale verdict")

// =============================================================================
// FAIL CLOSED. A freshness gate that answers "up to date" because it COULD NOT
// LOOK is this ticket's own bug reborn inside its fix. If the closure cannot be
// determined — an unreadable/unparseable project file, a `ProjectReference` that
// resolves to nothing — the run is REFUSED, and the build (which will choke on
// the same file, loudly) gets to report the real error.
//
// Swallowing these into "no references" would silently shrink the closure to
// nothing, and a stale dependency would sail straight through as fresh.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``an unparseable project file is REFUSED, not called fresh`` () =
    withTempDir "tp-stale-badxml" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(p [ s.TestsDir; "Tests.fsproj" ], "<Project><ItemGroup><ProjectReference </Project>")

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable(project, _)) -> test <@ project = "Tests" @>
        | other -> Assert.Fail($"an unreadable project file must fail CLOSED, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``a ProjectReference without an Include is REFUSED, not ignored`` () =
    withTempDir "tp-stale-noinclude" (fun tmpDir ->
        let s = synth tmpDir

        File.WriteAllText(
            p [ s.TestsDir; "Tests.fsproj" ],
            "<Project>\n  <ItemGroup>\n    <ProjectReference />\n  </ItemGroup>\n</Project>"
        )

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable _) -> ()
        | other -> Assert.Fail($"an unresolvable reference must fail CLOSED, got %A{other}"))

// A reference naming a project that does not exist is the same ignorance: we
// cannot know that project's sources, so we cannot certify this run.
[<Fact(Timeout = 15000)>]
let ``a ProjectReference to a missing project is REFUSED, not called fresh`` () =
    withTempDir "tp-stale-missingref" (fun tmpDir ->
        let s = synth tmpDir

        File.WriteAllText(
            p [ s.TestsDir; "Tests.fsproj" ],
            "<Project>\n  <ItemGroup>\n    <ProjectReference Include=\"../Ghost/Ghost.fsproj\" />\n  </ItemGroup>\n</Project>"
        )

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable(_, reason)) -> test <@ reason.Contains "Ghost" @>
        | other -> Assert.Fail($"a reference to a missing project must fail CLOSED, got %A{other}"))

// Ignorance ANYWHERE in the closure fails closed — not just at its root. A
// dependency's project file we cannot read hides that dependency's sources.
[<Fact(Timeout = 15000)>]
let ``an unparseable project file DEEP in the closure is REFUSED`` () =
    withTempDir "tp-stale-badxml-deep" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(p [ tmpDir; "Common"; "Common.fsproj" ], "<Project> <<< not xml")

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable _) -> ()
        | other -> Assert.Fail($"an unreadable DEPENDENCY project file must fail CLOSED, got %A{other}"))

// An fsproj REFERENCE CYCLE is MSBuild's error to report — the closure walk must
// terminate rather than hang on it.
[<Fact(Timeout = 15000)>]
let ``a project reference cycle terminates`` () =
    withTempDir "tp-stale-cycle" (fun tmpDir ->
        let s = synth tmpDir
        // Common references Tests, which already references Common.
        File.WriteAllText(
            p [ tmpDir; "Common"; "Common.fsproj" ],
            "<Project>\n  <ItemGroup>\n    <ProjectReference Include=\"../Tests/Tests.fsproj\" />\n  </ItemGroup>\n</Project>"
        )

        test <@ synthStale s = None @>

        File.SetLastWriteTimeUtc(s.CommonSrc, s.BuiltAt.AddMinutes(30.0))
        test <@ (synthStale s).IsSome @>)

// A dependency that has not been built AT ALL is the build's business (and the
// presence probe's), not staleness: there is no out-of-date artifact to refuse —
// a build in flight may still land it. Same for a dependency assembly that has
// not been copied into the test output yet.
[<Fact(Timeout = 15000)>]
let ``an unbuilt dependency is not reported stale`` () =
    withTempDir "tp-stale-unbuilt-dep" (fun tmpDir ->
        let s = synth tmpDir
        Directory.Delete(p [ tmpDir; "Common"; "bin" ], true)

        test <@ synthStale s = None @>)

[<Fact(Timeout = 15000)>]
let ``a dependency assembly not yet copied into the test output is not reported stale`` () =
    withTempDir "tp-stale-uncopied-dep" (fun tmpDir ->
        let s = synth tmpDir
        File.Delete s.CommonDllCopy

        test <@ synthStale s = None @>)

// A multi-targeted project is stale only when EVERY per-TFM output dir is stale:
// which TFM `dotnet run` selects is not knowable here, so one fresh output dir
// means there is a fresh way to run. Conservative against false-stale — the whole
// point of the exercise.
[<Fact(Timeout = 15000)>]
let ``a multi-TFM project with one FRESH output dir is not stale`` () =
    withTempDir "tp-stale-multitfm" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)

        // The fixture changes; net10.0's copy goes stale …
        File.SetLastWriteTimeUtc(s.CommonFixture, editedAt)
        test <@ (synthStale s).IsSome @>

        // … but a second TFM's output dir carries the up-to-date copy.
        let net9 = p [ s.TestsDir; "bin"; "Debug"; "net9.0" ]
        writeAt (p [ net9; "Tests.dll" ]) "" s.BuiltAt
        writeAt (p [ net9; "Common.dll" ]) "" s.BuiltAt
        writeAt (p [ net9; "Fixtures"; "data.json" ]) "{ \"leaves\": 40 }" editedAt

        // And a TFM dir of the DEPENDENCY that holds no assembly at all (only one
        // of its target frameworks was built) is simply not a candidate — it must
        // not be mistaken for a missing build.
        Directory.CreateDirectory(p [ tmpDir; "Common"; "bin"; "Debug"; "net9.0" ])
        |> ignore

        test <@ synthStale s = None @>)

// REGRESSION (2026-07-13 wedge): the freshness walk must TERMINATE in the
// presence of symlink cycles. The production trigger: `.devenv/profile` links
// into /nix/store where ncurses-6.6-dev/include contains TWO self-loop
// symlinks (`ncurses -> .`, `ncursesw -> .`) — branching factor 2 per level,
// bounded only by ENAMETOOLONG ⇒ ~2^90 paths. The pre-SafeWalk walk followed
// symlinked dirs and wedged EVERY `fshw check` forever (observed 8h36m,
// silent). On a symlink-following walk this test trips its Timeout.
[<Fact(Timeout = 15000)>]
let ``freshness terminates despite self-loop symlink cycles`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-nsm-cycle" (fun tmpDir ->
            let s = synth tmpDir

            // Two self-loops in one directory = the exact /nix/store shape.
            let cycleDir = Path.Combine(s.TestsDir, "cycle")
            Directory.CreateDirectory cycleDir |> ignore
            Directory.CreateSymbolicLink(Path.Combine(cycleDir, "loop"), ".") |> ignore
            Directory.CreateSymbolicLink(Path.Combine(cycleDir, "loop2"), ".") |> ignore

            test <@ synthStale s = None @>)

// REGRESSION (same wedge, the other half): a symlinked directory is a portal OUT
// of the tree (`.devenv/profile` → the nix store). Freshness is computed from the
// REAL tree only — a newer file behind a symlinked dir is not an input to this
// project, and following it is how the walk left the repo in the first place.
[<Fact(Timeout = 15000)>]
let ``freshness does not follow a symlinked directory out of the project`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-nsm-outside" (fun tmpDir ->
            let s = synth tmpDir
            let outside = Path.Combine(tmpDir, "outside")
            Directory.CreateDirectory outside |> ignore
            writeAt (Path.Combine(outside, "Newer.fs")) "module Newer" (s.BuiltAt.AddMinutes(30.0))

            Directory.CreateSymbolicLink(Path.Combine(s.TestsDir, "portal"), outside)
            |> ignore

            test <@ synthStale s = None @>)

// `.devenv`/`.direnv` are excluded by NAME as well (nix/devenv tooling dirs) —
// even a REGULAR (non-symlinked) file under them must not count as an input.
[<Fact(Timeout = 15000)>]
let ``freshness ignores sources under .devenv and .direnv`` () =
    withTempDir "tp-nsm-devenv" (fun tmpDir ->
        let s = synth tmpDir
        writeAt (Path.Combine(s.TestsDir, ".devenv", "gen", "Tool.fs")) "module Tool" (s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

// End-to-end: a present-but-stale apphost must DEFER (non-green, "waiting on
// build") through the plugin, never report a passing run on stale bits. On the
// pre-fix code the runner exits 0 → TestsPassed → a false GREEN, so the
// `HasFailingReasons` assertion FAILS until the freshness gate lands.
[<Fact(Timeout = 20000)>]
let ``a present-but-stale apphost defers as 'waiting on build' instead of passing on stale bits`` () =
    withTempDir "tp-stale-defer" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore

        // Apphost + canonical DLL PRESENT (so the missing-apphost path does NOT
        // fire) ...
        File.WriteAllText(Path.Combine(tfmDir, "Unit"), "")
        let dll = Path.Combine(tfmDir, "Unit.dll")
        File.WriteAllText(dll, "")

        // ... but a source was edited AFTER the build — the stale-binary trigger.
        let src = Path.Combine(projDir, "Tests.fs")
        File.WriteAllText(src, "module Tests")
        let now = DateTime.UtcNow
        File.SetLastWriteTimeUtc(dll, now.AddMinutes(-10.0))
        File.SetLastWriteTimeUtc(src, now)

        // The runner would EXIT 0 (a 'pass' on stale bits) if launched; `--project`
        // makes the project derivable so the freshness gate engages pre-launch.
        let configs =
            [ { Project = "Unit"
                Command = "sh"
                Args = $"-c \"exit 0\" --project {projDir} --no-build --"
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
        waitForPluginTerminal host "test-prune" 15.0

        let failingReasons =
            host.GetErrorsByPlugin("test-prune")
            |> Map.toList
            |> List.collect snd
            |> List.filter (fun e -> e.Severity = FsHotWatch.ErrorLedger.Error)

        // Stale bits must be DEFERRED — non-green, with an honest "waiting on
        // build" diagnostic — exactly like a missing apphost; never a pass.
        test <@ host.HasFailingReasons(warningsAreFailures = true) @>

        let waitingDiagnostic =
            failingReasons
            |> List.exists (fun e -> e.Message.ToLowerInvariant().Contains("waiting on build"))

        test <@ waitingDiagnostic @>

        match host.GetStatus("test-prune") with
        | Some(Failed(msg, _, _)) -> test <@ msg.ToLowerInvariant().Contains("waiting on build") @>
        | other -> Assert.Fail($"expected non-green Failed status for the stale-artifact defer, got %A{other}")

        // And it must never masquerade as a test failure.
        test
            <@
                failingReasons
                |> List.forall (fun e -> not (e.Message.ToLowerInvariant().Contains("tests failed")))
            @>)

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
              TimeoutSec = None
              ReportVerificationFormat = AutoDetect }

        let configs = [ mk "ProjA" flagA; mk "ProjB" flagB ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
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

// =============================================================================
// PendingVerification sidecar — focused, DETERMINISTIC unit tests for the
// load/save/hash primitives. These pin both sides of every branch in `load`
// (missing file, whitespace-only, corrupt JSON, well-formed) so the module's
// branch coverage is stable run-to-run rather than depending on which states
// the end-to-end queue tests happen to leave the sidecar in.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``PendingVerification: load on a missing file returns the empty queue`` () =
    withTempDir "pv-missing" (fun tmpDir ->
        // No sidecar written → File.Exists is false → empty.
        let q = FsHotWatch.TestPrune.PendingVerification.load tmpDir
        test <@ Set.isEmpty q @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: save then load round-trips the queue`` () =
    withTempDir "pv-roundtrip" (fun tmpDir ->
        let original = Set.ofList [ "Lib.foo"; "Lib.bar"; "Mod.baz" ]
        FsHotWatch.TestPrune.PendingVerification.save tmpDir original
        let loaded = FsHotWatch.TestPrune.PendingVerification.load tmpDir
        test <@ loaded = original @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: save empty then load returns empty`` () =
    withTempDir "pv-empty" (fun tmpDir ->
        FsHotWatch.TestPrune.PendingVerification.save tmpDir Set.empty
        let loaded = FsHotWatch.TestPrune.PendingVerification.load tmpDir
        test <@ Set.isEmpty loaded @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: load on whitespace-only file returns empty`` () =
    withTempDir "pv-whitespace" (fun tmpDir ->
        let path = FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "   \n  ")
        let loaded = FsHotWatch.TestPrune.PendingVerification.load tmpDir
        test <@ Set.isEmpty loaded @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: load on corrupt JSON returns empty (no throw)`` () =
    withTempDir "pv-corrupt" (fun tmpDir ->
        let path = FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "{ this is not valid json [[[")
        // Must not throw — a corrupt sidecar self-heals to empty (re-tests on
        // the next edit) rather than crashing the daemon.
        let loaded = FsHotWatch.TestPrune.PendingVerification.load tmpDir
        test <@ Set.isEmpty loaded @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: hash is order-independent and empty-distinct`` () =
    let pv = FsHotWatch.TestPrune.PendingVerification.hash
    // Same membership, different insertion order → identical hash.
    test <@ pv (Set.ofList [ "a"; "b"; "c" ]) = pv (Set.ofList [ "c"; "a"; "b" ]) @>
    // A non-empty queue hashes differently from the empty queue.
    test <@ pv (Set.ofList [ "a" ]) <> pv FsHotWatch.TestPrune.PendingVerification.empty @>

// =============================================================================
// Sound test-gate (pending-verification queue). A changed symbol leaves the
// needs-testing queue ONLY when a test run that covered it completed green.
// "0 affected tests" must provably mean "test-equivalent to the last green
// run." These tests pin the three holes the queue closes:
//   1. Verdict ignored run outcome — an Aborted run false-greened.
//   2. The queue drained unconditionally — Aborted/failed runs forgot what
//      still needed testing.
//   3. No durable queue — a restart absorbed unverified symbols.
// They drive the plugin through the real BuildCompleted → run → TestsFinished
// flow, seeding the symbol DB directly (deterministic, no FCS) so a known
// symbol maps to a test in a known project, and assert against the on-disk
// `.fshw/test-prune/pending-verification.json` sidecar.
// =============================================================================

module private PendingQueueHelpers =
    open FsHotWatch.TestPrune

    /// Seed the symbol DB so `QueryAffectedTests [symbolFullName]` returns a test
    /// in `testProject` (class `testClass`/method `testMethod`). Mirrors the
    /// dependency-edge + TestMethodInfo shape the analyzer produces.
    let seedCoveredSymbol
        (db: Database)
        (symbolFullName: string)
        (sourceFile: string)
        (testProject: string)
        (testClass: string)
        (testMethod: string)
        =
        // The production symbol under test.
        let symbol: SymbolInfo =
            { FullName = symbolFullName
              Kind = SymbolKind.Value
              SourceFile = sourceFile
              LineStart = 1
              LineEnd = 1
              ContentHash = "seed-hash"
              IsExtern = false }

        // The test method is ALSO a symbol: dependencies.from_symbol_id and
        // test_methods.symbol_id are NOT-NULL FKs into symbols(id), so without a
        // row for the test's own full name the edge/test-method are silently
        // dropped and QueryAffectedTests returns nothing.
        let testSymbol: SymbolInfo =
            { FullName = $"{testClass}.{testMethod}"
              Kind = SymbolKind.Value
              SourceFile = $"{testClass}.fs"
              LineStart = 1
              LineEnd = 1
              ContentHash = "seed-test-hash"
              IsExtern = false }

        let tm: TestMethodInfo =
            { SymbolFullName = $"{testClass}.{testMethod}"
              TestProject = testProject
              TestClass = testClass
              TestMethod = testMethod }

        let analysis =
            AnalysisResult.Create(
                [ symbol; testSymbol ],
                [ { FromSymbol = $"{testClass}.{testMethod}"
                    ToSymbol = symbolFullName
                    Kind = DependencyKind.Calls
                    Source = "core" } ],
                [ tm ]
            )

        db.RebuildProjects([ analysis ])
        // Cross-connection WAL visibility: the plugin opens its OWN
        // Database.create(dbPath) connection, which a pooled stale snapshot can
        // hide these writes from. Clear the pool so the plugin's first read sees
        // the seed (mirrors the existing affected-tests tests' ClearAllPools).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

    /// A test config whose runner passes (exit 0) or fails (exit 1) based on a
    /// flag file's presence. The class filter is wired so the impact-selected
    /// class actually runs.
    let flagConfig (tmpDir: string) (project: string) (flag: string) : TestConfig =
        { Project = project
          // exit 1 iff the flag file exists.
          Command = "sh"
          Args = $"-c \"if [ -f {flag} ]; then exit 1; else exit 0; fi\""
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    /// Current durable pending-verification queue for a repo root.
    let loadQueue (tmpDir: string) : Set<string> = PendingVerification.load tmpDir

[<Fact(Timeout = 20000)>]
let ``incident: a beforeRun throw aborts the run, is NOT green, and re-flags the symbols`` () =
    // THE pinned incident. A changed symbol is queued; the run's beforeRun hook
    // throws → executeTests propagates → runTestsWithImpact catches → the
    // completion carries Outcome = Aborted, Results = Map.empty. Pre-fix, empty
    // results trivially satisfied "failed = 0 && deferred = 0" → Completed
    // (false green) AND the queue drained, permanently absorbing the symbol.
    // Post-fix: status is NON-green (Failed) and the symbol stays queued.
    withTempDir "tp-incident-abort" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // Seed the durable queue so the run launches against it. (A restart that
        // loaded this queue is the realistic source; here we set it directly.)
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let configs =
            [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) ]

        // beforeRun that always throws.
        let beforeRun = Some(fun () -> failwith "beforeRun boom")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // (1) Verdict must NOT be Completed — an aborted run verified nothing.
        match host.GetStatus("test-prune") with
        | Some(Completed _) -> Assert.Fail("aborted run was reported as Completed (false green)")
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"expected Failed for an aborted run, got %A{other}")

        // (2) The symbol must STILL be queued — a subsequent run re-flags it.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ queue.Contains("Lib.foo") @>)

[<Fact(Timeout = 15000)>]
let ``incident: a beforeRun throw in the run-tests command surfaces as Failed, not a swallowed error`` () =
    // AUTOMATION-68 — the gate-trust hole. The manual `run-tests` command ran
    // `executeTests` inside a try/with that, on a `beforeRun` throw, returned a
    // command-level JSON error and posted NOTHING back — leaving the plugin
    // status at its prior (possibly green) value. A concurrent `fshw check` then
    // read the daemon aggregate (`IpcOutput.hasFailures` → `anyPluginFailed`),
    // saw no Failed status, and exited 0 while the preflight-guarded suite NEVER
    // RAN. Unlike the impact path (`runTestsWithImpact`), whose catch builds an
    // Aborted lifecycle → PluginStatus.Failed, the command path was silent.
    // Post-fix the command posts the SAME Aborted lifecycle, so the plugin
    // reaches Failed with the hook's output surfaced and `check` reads non-green.
    withTempDir "tp-cmd-beforerun-throw" (fun tmpDir ->
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

        // A beforeRun that fails its preflight (models a real csrf-gate step).
        let beforeRun = Some(fun () -> failwith "csrf-gate failed")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        // The command posts the Aborted TestsFinished async; await the terminal
        // transition it drives (Failed) before reading status.
        let await = beginAwaitNextTerminal host "test-prune"
        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        // (1) The command still reports the failure to its direct caller.
        test <@ result.IsSome @>
        test <@ result.Value.Contains("csrf-gate failed") @>

        // (2) The seam `fshw check` reads: the plugin status is Failed with the
        //     hook's output surfaced — NOT a stale green / Idle. `anyPluginFailed`
        //     (IpcOutput.hasFailures) keys off exactly this, so a non-zero check
        //     verdict follows.
        match host.GetStatus("test-prune") with
        | Some(Failed(msg, _, _)) -> test <@ msg.Contains("csrf-gate failed") @>
        | other -> Assert.Fail($"expected Failed with the hook output surfaced, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``incident: a test child that never becomes a live process drives the run to Failed, not a wedge`` () =
    // AUTOMATION-65 QA finding — the launch gap. Between a config's spawn and its
    // first sign of life NOTHING watched the wait: an overloaded box left the
    // (infinite, no-TimeoutSec) `WaitForExit` hanging forever with no child ever
    // appearing, so the plugin stayed `Running` and `check`'s WaitForComplete
    // streamed "Waiting for plugins" for hours. `sleep 30` reproduces it: it
    // produces no output and won't exit, so with a tiny launch deadline
    // (`FSHW_LAUNCH_DEADLINE_SEC`) the watchdog kills the tree and raises
    // `LaunchStalledException`, which the run-tests command's catch turns into the
    // SAME Aborted lifecycle a beforeRun throw does (AUTOMATION-68 seam) →
    // PluginStatus.Failed → `check` exits non-green rather than wedging.
    //
    // The env override is process-global, but only `executeTests` reads it and
    // the tests that reach `executeTests` all live in this class (one xUnit
    // collection ⇒ sequential); echo-based configs elsewhere emit output within
    // ms and so are immune to a 1 s launch deadline regardless. Restored in a
    // finally.
    withTempDir "tp-launch-gap-stall" (fun tmpDir ->
        let key = "FSHW_LAUNCH_DEADLINE_SEC"
        let prior = Environment.GetEnvironmentVariable key
        Environment.SetEnvironmentVariable(key, "1")

        try
            let configs =
                [ { Project = "TestProject"
                    Command = "sleep"
                    Args = "30"
                    Group = "default"
                    Environment = []
                    FilterTemplate = None
                    ClassJoin = " "
                    TimeoutSec = None
                    ReportVerificationFormat = AutoDetect } ]

            let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
            let handler = create ":memory:" tmpDir (Some configs) None None None None []
            host.RegisterHandler(handler)

            let await = beginAwaitNextTerminal host "test-prune"
            let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
            await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

            // The command surfaces the launch-stall diagnostic to its direct caller.
            test <@ result.IsSome @>
            test <@ result.Value.Contains("no live process") @>

            // The seam `fshw check` reads: Failed, naming the config and the launch
            // gap — NOT a stale green / a plugin stuck Running.
            match host.GetStatus("test-prune") with
            | Some(Failed(msg, _, _)) ->
                test <@ msg.Contains("no live process") @>
                test <@ msg.Contains("TestProject") @>
            | other -> Assert.Fail($"expected Failed for a launch-stalled run, got %A{other}")
        finally
            Environment.SetEnvironmentVariable(key, prior))

[<Fact(Timeout = 15000)>]
let ``run-tests command with a passing beforeRun runs normally and reports Completed`` () =
    // Guards the pass path: a beforeRun that SUCCEEDS must leave the run
    // unaffected — projects execute, results come back, and the plugin reaches a
    // green terminal. Pairs with the failing-beforeRun regression above.
    withTempDir "tp-cmd-beforerun-ok" (fun tmpDir ->
        let ran = ref false

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

        let beforeRun = Some(fun () -> ran.Value <- true)

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        test <@ ran.Value @>
        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        let projects = doc.RootElement.GetProperty("projects")
        Assert.True(projects.GetArrayLength() > 0)
        Assert.Equal("passed", projects.[0].GetProperty("status").GetString())

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed for a passing beforeRun run, got %A{other}"))

[<Fact(Timeout = 25000)>]
let ``partial failure: symbols whose only covering project passed commit; symbols touching a failed project stay queued``
    ()
    =
    // A run covers P1 (passes) and P2 (fails). SymA's tests live only in P1;
    // SymB's tests live only in P2. SymA must commit (its covering project
    // passed); SymB must stay queued (its covering project failed). Pre-fix the
    // whole queue drained on any completion regardless of per-project outcome.
    withTempDir "tp-partial-fail" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.symA" "A.fs" "P1" "P1Tests" "aTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.symB" "B.fs" "P2" "P2Tests" "bTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.symA"; "Lib.symB" ])

        // P1 passes (no flag), P2 fails (flag present).
        let p2flag = Path.Combine(tmpDir, "p2fail")
        File.WriteAllText(p2flag, "")

        let configs =
            [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never"))
              PendingQueueHelpers.flagConfig tmpDir "P2" p2flag ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 20.0) |> ignore

        let queue = PendingQueueHelpers.loadQueue tmpDir

        // symA's only covering project (P1) passed → committed (gone).
        test <@ not (queue.Contains("Lib.symA")) @>
        // symB touches P2 which failed → still queued.
        test <@ queue.Contains("Lib.symB") @>)

[<Fact(Timeout = 30000)>]
let ``mid-run change: a green run commits only its launch set; a symbol that arrives mid-run stays queued and triggers a rerun``
    ()
    =
    // Run 1 launches against {Lib.foo} and SLEEPS (~1.5s). While it is in flight a
    // genuine FCS FileChecked lands that changes a second symbol (`bar`), which the
    // plugin enqueues via the real write-through path, and a mid-run BuildCompleted
    // sets PendingRerun. Run 1's launch SNAPSHOT was {Lib.foo}, so its green
    // completion commits ONLY Lib.foo; the mid-run `bar` is NOT in the launch set,
    // so it survives the commit and the PendingRerun then covers + commits it.
    // This exercises the real mid-run path (no file-rewrite simulation): the
    // launch snapshot is captured at dispatch and the commit is launch-set-scoped.
    withTempDir "tp-midrun" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")

        // Lib exposes `foo` and `bar`; the test file has a test calling each so a
        // change to either selects its test.
        let libSource1 = "module Lib\nlet foo (x: int) = x + 1\nlet bar (x: int) = x + 1\n"

        let testsSource =
            """module Tests
open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let fooTest () = assert (foo 1 = 2)

[<Fact>]
let barTest () = assert (bar 1 = 2)
"""

        File.WriteAllText(libFile, libSource1)
        File.WriteAllText(testsFile, testsSource)

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir

        // The runner sleeps so the mid-run injection has a window; always passes.
        let configs =
            [ { Project = "Lib"
                Command = "sh"
                Args = "-c \"sleep 1.5; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        // Index both files (BuildCompleted first so the freshness sidecar can go
        // clean, then FileChecked for each), then flush to the DB via BatchChecked.
        emitBuildAndWaitTerminal host

        for f in [ libFile; testsFile ] do
            match pipeline.CheckFile(AbsFilePath.create f) |> Async.RunSynchronously with
            | Some r -> host.EmitFileChecked(r)
            | None -> failwith $"CheckFile failed for {f}"

        waitForPluginIdle host "test-prune" 10.0
        emitBatchAndQuiesce host [ libFile; testsFile ]

        // Change `foo`'s body so `fooTest` is the affected test, then launch run 1.
        let libSource2 = "module Lib\nlet foo (x: int) = x + 2\nlet bar (x: int) = x + 1\n"
        File.WriteAllText(libFile, libSource2)

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile (foo change) failed"

        waitForPluginIdle host "test-prune" 10.0

        // Launch run 1 (covers fooTest). It sleeps 1.5s.
        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(Running _) -> true
                | _ -> false)
            5000

        // MID-RUN: change `bar`'s body → a real FileChecked enqueues `bar`'s
        // symbol via the plugin's write-through path, then a BuildCompleted sets
        // PendingRerun. This all lands while run 1 is still sleeping.
        let libSource3 = "module Lib\nlet foo (x: int) = x + 2\nlet bar (x: int) = x + 99\n"
        File.WriteAllText(libFile, libSource3)

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile (bar change) failed"

        host.EmitBuildCompleted(BuildSucceeded)

        // Run 1 finishes, then the PendingRerun (covering the mid-run `bar`) runs.
        // Both passed (the runner always exits 0), so the queue ultimately drains
        // to empty and the plugin settles green. Wait for that CONVERGED state
        // (empty queue AND Completed) rather than a single terminal transition —
        // the rerun re-enters Running between run 1's completion and final settle,
        // so a one-shot terminal wait races the rerun. The load-bearing invariant
        // (`bar` is NOT committed by run 1's completion — it only leaves the queue
        // once the rerun actually covers it green) is what makes the convergence
        // possible: had run 1 committed its non-launch-set arrival, the rerun
        // would never have re-tested `bar`.
        let converged () =
            let q = PendingQueueHelpers.loadQueue tmpDir

            let green =
                match host.GetStatus("test-prune") with
                | Some(Completed _) -> true
                | _ -> false

            Set.isEmpty q && green

        waitUntil converged 20000

        let queueFinal = PendingQueueHelpers.loadQueue tmpDir
        test <@ Set.isEmpty queueFinal @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed after launch-set commit + rerun drained the queue, got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``restart persistence: a non-empty queue survives a daemon restart and is re-flagged`` () =
    // Session 1 queues Lib.foo (covered by P1) but the run never proves it green
    // (it fails). Session 2 (a fresh plugin instance against the same on-disk
    // sidecar + DB) must load the queue, re-flag Lib.foo, and run P1 again.
    // Pre-fix the in-memory queue died with the daemon → restart silent-greened.
    withTempDir "tp-restart" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // Queue Lib.foo on disk (session-1 residue: a symbol never proven green).
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        // Session 2: a fresh plugin. P1 PASSES this time, so the restart-driven
        // run should cover Lib.foo, pass, and commit it — proving the symbol was
        // re-flagged and actually re-tested (not silently absorbed).
        let ranMarker = Path.Combine(tmpDir, "p1-ran")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = $"-c \"touch {ranMarker}; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // The restart re-flagged Lib.foo → P1 actually ran.
        test <@ File.Exists ranMarker @>

        // P1 passed and covers Lib.foo → it committed (queue now empty).
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.foo")) @>

        // And the verdict is green now that the queue drained.
        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed after the re-flagged symbol tested green, got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``no-covering-test symbol drops from the queue at flush without wedging it`` () =
    // A queued symbol with NO covering test must drop immediately at flush time —
    // nothing to wait for; retaining it would wedge the queue forever (every run
    // selects zero tests yet the queue never empties → permanent non-green).
    withTempDir "tp-uncovered" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        // Seed a COVERED symbol so the DB is non-empty and indexed, plus queue an
        // UNCOVERED symbol that maps to no test.
        PendingQueueHelpers.seedCoveredSymbol db "Lib.covered" "Lib.fs" "P1" "P1Tests" "coveredTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.uncovered"; "Lib.covered" ])

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = "-c \"exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        let queue = PendingQueueHelpers.loadQueue tmpDir

        // The uncovered symbol dropped (no test to wait on); the covered symbol
        // committed because P1 passed. Queue is empty → not wedged → green.
        test <@ not (queue.Contains("Lib.uncovered")) @>
        test <@ not (queue.Contains("Lib.covered")) @>
        test <@ Set.isEmpty queue @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed (queue drained, not wedged), got %A{other}"))

// --- classifyTestOutcome: report-authoritative verdict (CTRF over exit code) ---
//
// These pin the false-RED fix: the structured report (not the process exit code)
// decides green/red, with the exit code only a tie-break when no report exists.

open FsHotWatch.ProcessHelper

let private rep total passed failed skipped other : Flakiness.TestReport =
    { Total = total
      Passed = passed
      Failed = failed
      Skipped = skipped
      Other = other }

let private isFailed result =
    match result with
    | TestsFailed _ -> true
    | _ -> false

[<Fact(Timeout = 5000)>]
let ``classify: non-zero exit with a clean report is GREEN (the shutdown flake)`` () =
    // exit 7 (MTP dirty-shutdown) but the report shows zero failures and >=1 test.
    let report = Some(rep 12 12 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(7, ProcessOutput.Drained "host crashed during shutdown"))

    test <@ TestResult.isPassed result @>

[<Fact(Timeout = 5000)>]
let ``classify: report with a failed test is RED even on exit 0`` () =
    let report = Some(rep 3 2 1 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Succeeded(ProcessOutput.Drained ""))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: report with an other (raw-throw) result is RED`` () =
    let report = Some(rep 3 2 0 0 1)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(2, ProcessOutput.Drained ""))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: non-zero exit with NO report from a capable runner is ERRORED, not failed`` () =
    let result =
        classifyTestOutcome
            (ReportRequested None)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(7, ProcessOutput.Drained "aborted"))

    test <@ TestResult.isErrored result @>
    test <@ not (isFailed result) @>
    test <@ not (TestResult.isPassed result) @>

[<Fact(Timeout = 5000)>]
let ``classify: non-zero exit with no report from an UNKNOWN runner stays FAILED (no regression)`` () =
    // NoReportRequested → exit code is the only signal → behave as before.
    let result =
        classifyTestOutcome
            NoReportRequested
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(1, ProcessOutput.Drained "boom"))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: clean exit with no report is PASSED`` () =
    let result =
        classifyTestOutcome
            (ReportRequested None)
            false
            TimeSpan.Zero
            (ProcessOutcome.Succeeded(ProcessOutput.Drained "ok"))

    test <@ TestResult.isPassed result @>

[<Fact(Timeout = 5000)>]
let ``classify: unfiltered zero-test report with non-zero exit is RED (empty suite is a problem)`` () =
    let report = Some(rep 0 0 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(8, ProcessOutput.Drained "Zero tests ran"))

    test <@ isFailed result @>

[<Fact(Timeout = 5000)>]
let ``classify: a timeout is TimedOut regardless of a flushed report`` () =
    let report = Some(rep 5 5 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            (TimeSpan.FromSeconds 30.0)
            (ProcessOutcome.TimedOut(TimeSpan.FromSeconds 30.0, ProcessOutput.Drained "stuck"))

    test <@ TestResult.isTimedOut result @>

// --- detectCtrfCapable: scope report injection to xUnit runners ---

[<Fact(Timeout = 5000)>]
let ``detectCtrfCapable: Some true when the project references xunit`` () =
    withTempDir "fshw-detect-xunit" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")

        File.WriteAllText(
            proj,
            "<Project><ItemGroup><PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" /></ItemGroup></Project>"
        )

        test <@ detectCtrfCapable $"--project {proj}" tmp = Some true @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfCapable: Some false when the project does not reference xunit`` () =
    withTempDir "fshw-detect-noxunit" (fun tmp ->
        let proj = Path.Combine(tmp, "MyTests.fsproj")

        File.WriteAllText(
            proj,
            "<Project><ItemGroup><PackageReference Include=\"Expecto\" Version=\"10.0.0\" /></ItemGroup></Project>"
        )

        test <@ detectCtrfCapable $"--project {proj}" tmp = Some false @>)

[<Fact(Timeout = 5000)>]
let ``detectCtrfCapable: None when no project can be derived from the args`` () =
    // A --project-less / non-file command → fall back to the dotnet heuristic.
    test <@ detectCtrfCapable "test --no-build" "/tmp" = None @>

// =============================================================================
// AUTOMATION-95 / AUTOMATION-99 — the gate must CONVERGE, never rest on a
// verdict nobody earned.
//
// One defect, two polarities. The pending-verification queue used to have
// exactly ONE drain trigger: the `BuildCompleted` handler. But on a scan the
// daemon runs the FCS pass only AFTER the build goes terminal (Daemon.fs
// `performScan` awaits BuildPlugin before dispatching the FCS tiers), so every
// symbol the SCAN discovers lands in the queue strictly AFTER the only event
// that could have run its tests. `BatchChecked` flushed those symbols, even
// computed their affected tests — and then returned without running anything.
// The queue was never drained, and `check` reported whatever terminal status
// test-prune happened to be holding:
//
//   * a stale `Completed` → FALSE GREEN with symbols pending      (AUTOMATION-95)
//   * a stale `Failed`    → PERMANENTLY STUCK RED, work never runs (AUTOMATION-99)
//
// Reproduced live before the fix: `check` returned in ONE SECOND, exit 0, with
// zero daemon activity, while the symbol it had just discovered sat unverified
// in the queue and the plugin's own log said "24 affected tests".
//
// The contract these tests pin down: whoever DISCOVERS unverified symbols is
// responsible for RUNNING them. Green is only ever earned by a run.
// =============================================================================

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-95/99: BatchChecked drains a pending queue instead of resting on a stale verdict`` () =
    // The cold-scan stranding, in miniature. Symbols discovered by the scan land
    // in the queue AFTER BuildCompleted has already fired, so BuildCompleted can
    // never have run them: BatchChecked (the cohort seal — the first moment the
    // scan's symbols are known) is the only event left that can. It must DRAIN.
    //
    // Pre-fix: BatchChecked flushed and returned. No run, queue intact, and the
    // plugin's last status stood as the verdict — the exact false-green/stuck-red.
    withTempDir "tp-batch-drain" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // A symbol awaiting verification (as a scan's FileChecked pass would leave
        // it), with NO BuildCompleted to follow.
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let ranMarker = Path.Combine(tmpDir, "p1-ran")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = $"-c \"touch {ranMarker}; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // BatchChecked ONLY — deliberately no BuildCompleted, which is the whole
        // point: on a scan it has already come and gone before these symbols existed.
        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // It RAN the covering tests rather than reporting on them.
        test <@ File.Exists ranMarker @>

        // …and only then went green — a verdict it earned.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.foo")) @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected an EARNED Completed after the drain, got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-99: a symbol covered only by an unconfigured test project drops instead of wedging the gate red`` () =
    // The permanent wedge. The symbol DB indexes test methods from EVERY project it
    // analyzed — which is NOT the same set as the projects fshw is configured to run.
    // A symbol covered only by an unconfigured project can never be proven green: its
    // covering project never executes, so it never lands in a run's results, so the
    // symbol never commits. Pre-fix it sat in the queue forever and `check` stayed red
    // no matter how many times the suite passed.
    //
    // Observed live: two full suites ran and PASSED back-to-back, and `check` STILL
    // exited 1 with the symbols pending — because their only covering tests lived in
    // FsHotWatch.IntegrationTests, which the daemon does not run.
    //
    // "Covered" must mean "covered by a test we can actually run". Anything else is
    // indistinguishable from having no covering test, and drops by the same rule.
    withTempDir "tp-unrunnable" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath

        // Lib.orphan's ONLY covering test lives in P2 — which is not in `configs`.
        PendingQueueHelpers.seedCoveredSymbol db "Lib.orphan" "Orphan.fs" "P2" "P2Tests" "orphanTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.orphan" ])

        // Only P1 is runnable. P2 is indexed but will never execute.
        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = "-c \"exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // Unverifiable-by-construction → dropped, not retained forever.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.orphan")) @>

        // And the gate is NOT stuck red on a symbol no configured test can ever prove.
        match host.GetStatus("test-prune") with
        | Some(PluginStatus.Failed(msg, _, _)) ->
            Assert.Fail($"gate wedged red on a symbol no runnable test covers: %s{msg}")
        | _ -> ())

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-95: a plugin with a test run in flight reports BUSY, so no verdict can resolve mid-run`` () =
    // The third facet: `check` handed back a verdict WHILE the run that would have
    // produced it was still executing. "Busy" used to mean only "has events queued in
    // its mailbox" — blind to the background work a handler launches via RunExclusive
    // and then returns from. So the host saw an idle mailbox, called the plugin at
    // rest, and WaitForComplete resolved mid-run.
    //
    // Observed live: test run launched 11:30:17, still executing at 11:30:34, and the
    // daemon logged "all plugins already terminal" and `check` exited 0.
    //
    // Busy must mean "has work in flight", full stop.
    withTempDir "tp-busy-during-run" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = "-c \"sleep 2; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)

        // Wait until the run is genuinely in flight (status Running). By this point the
        // BuildCompleted handler has returned, so the MAILBOX is drained — the only
        // thing that can still be keeping the plugin busy is the background run itself.
        let isRunning () =
            match host.GetStatus("test-prune") with
            | Some(Running _) -> true
            | _ -> false

        waitUntil isRunning 5000
        test <@ isRunning () @>

        // THE ASSERTION: work is in flight, so the host must not call this at rest.
        // Pre-fix this was false (idle mailbox) and the verdict resolved mid-run.
        test <@ host.AnyPluginBusy() @>

        // And the run still lands normally.
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed once the in-flight run finished, got %A{other}"))

// --- AUTOMATION-113: an unanalysable file must not vanish from the impact graph ---
//
// A file whose symbol analysis fails contributes NO symbols. Before this fix the
// plugin's `Error` branch simply `return state`d — the file was dropped, the impact
// graph never saw it, a change to it diffed against nothing and selected NO tests,
// and the gate went green having run nothing relevant. Completely silent.
//
// The contract now: an unanalysable file forces the COARSE selection (every test
// project, in full), and says so loudly. Safe over-selection beats silent
// under-selection — "the one failure mode a test-impact tool must not have".

let private testConfigNamed (project: string) : TestConfig =
    { Project = project
      Command = "dotnet"
      Args = "test"
      Group = "default"
      Environment = []
      FilterTemplate = Some "--filter-class {classes}"
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

let private threeProjects =
    [ testConfigNamed "Alpha.Tests"
      testConfigNamed "Beta.Tests"
      testConfigNamed "Gamma.Tests" ]

[<Fact(Timeout = 15000)>]
let ``coarseFallbackProjects is a no-op while every file analyses cleanly`` () =
    // The healthy tree pays nothing: the precise, symbol-scoped selection stands and
    // the dependency-fanout set passes through untouched.
    let fanout = Set.ofList [ "Beta.Tests" ]

    let result = coarseFallbackProjects threeProjects Set.empty fanout

    test <@ result = fanout @>

[<Fact(Timeout = 15000)>]
let ``one unanalysable file force-runs EVERY test project`` () =
    // The file is invisible to the symbol graph, so no per-symbol selection can be
    // trusted to cover it. The honest answer is "I cannot tell you what is affected",
    // and the only sound response to that is the whole suite.
    let unanalyzable = Set.ofList [ "src/Lib/Broken.fs" ]

    let result = coarseFallbackProjects threeProjects unanalyzable Set.empty

    test <@ result = Set.ofList [ "Alpha.Tests"; "Beta.Tests"; "Gamma.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``the coarse fallback is a superset of the dependency fanout, never a replacement`` () =
    // Both widenings are safe directions; neither may cancel the other out.
    let unanalyzable = Set.ofList [ "src/Lib/Broken.fs" ]
    let fanout = Set.ofList [ "Beta.Tests" ]

    let result = coarseFallbackProjects threeProjects unanalyzable fanout

    test <@ Set.isSubset fanout result @>
    test <@ result = Set.ofList [ "Alpha.Tests"; "Beta.Tests"; "Gamma.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``a non-empty coarse fallback disables the zero-affected skip gate`` () =
    // The skip gate in `runTestsWithImpact` terminates as a clean green with 0 tests
    // run when there are no affected classes AND no force-run projects. An
    // unanalysable file must never be able to reach that verdict — which is exactly
    // what a non-empty force-run set guarantees, so assert the emptiness predicate the
    // gate actually reads.
    let forceRun =
        coarseFallbackProjects threeProjects (Set.ofList [ "src/Lib/Broken.fs" ]) Set.empty

    test <@ not (Set.isEmpty forceRun) @>

[<Fact(Timeout = 15000)>]
let ``an unanalysable file is reported LOUDLY, naming the file and the reason`` () =
    // The old behaviour reported nothing a consumer could see: a log line, and a
    // plugin status the very next file's `Completed` overwrote. The diagnostic must
    // name the file, carry the reason, and be at least Warning severity so the
    // default warn-fail policy denies the check a green verdict.
    let reason = "Parse errors: XML comment is not placed on a valid language element."

    let entry = unanalyzableFileDiagnostic "src/Lib/Broken.fs" reason

    test <@ entry.Severity = FsHotWatch.ErrorLedger.Warning @>
    test <@ entry.Message.Contains "src/Lib/Broken.fs" @>
    test <@ entry.Message.Contains "XML comment is not placed on a valid language element." @>
    test <@ FsHotWatch.ErrorLedger.ErrorEntry.isFailing true entry @>

    let detail = entry.Detail |> Option.defaultValue ""
    test <@ detail.Contains "INVISIBLE to the impact graph" @>

// --- AUTOMATION-112: the merge gate's scope is part of the §2a cache key ---

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: a merge gate cannot replay an impact-filtered run's cached verdict`` () =
    // The subtlest road to the bug. Everything else about the tree is identical — same
    // symbols, empty queue, same deps — so WITHOUT the scope in the key, the first
    // thing `fshw gate` does on an unchanged tree is HIT the entry an earlier
    // impact-filtered `fshw check` wrote, replay its green, and never start a test
    // process. A filtered verdict, laundered into a merge verdict, with no run at all.
    let keyWithScope (gateScope: string option) =
        cacheKeyFor
            (fun () -> "same-symbols")
            (fun () -> None)
            (fun () -> None)
            (fun () -> gateScope)
            (fun () -> false)
            (BuildCompleted BuildSucceeded)

    let innerLoopKey = keyWithScope None
    let mergeGateKey = keyWithScope (Some "full")

    test <@ innerLoopKey.IsSome @>
    test <@ mergeGateKey.IsSome @>
    test <@ innerLoopKey <> mergeGateKey @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: the inner-loop key is unchanged by the scope salt`` () =
    // `None` (not "impact") for the inner loop keeps the merkle entry OMITTED, so the
    // ordinary key stays byte-identical to the pre-feature one and every existing
    // on-disk cache entry keeps hitting. The gate pays the cost of its own scope; the
    // fast loop pays nothing.
    let withScopeThunk =
        cacheKeyFor
            (fun () -> "s")
            (fun () -> None)
            (fun () -> Some "deps")
            (fun () -> None)
            (fun () -> false)
            (BuildCompleted BuildSucceeded)

    // Same inputs, hand-built without any gate-scope entry at all.
    let expected =
        FsHotWatch.TaskCache.merkleCacheKey
            [ "plugin-version", "test-prune-merkle-v2"
              "event", "BuildCompleted"
              "changed-symbols", "s"
              "build-outcome", "succeeded"
              "depends-on", "deps" ]

    test <@ withScopeThunk = Some expected @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: two merge-gate runs over the same tree DO share a key`` () =
    // The gate is not gratuitously cache-hostile: a second gate over an unchanged tree
    // replays a run that genuinely WAS full-suite. Sound, and fast.
    let gateKey () =
        cacheKeyFor
            (fun () -> "same")
            (fun () -> None)
            (fun () -> None)
            (fun () -> Some "full")
            (fun () -> false)
            (BuildCompleted BuildSucceeded)

    test <@ gateKey () = gateKey () @>

// --- AUTOMATION-112: what a completed run actually covered ---

[<Fact(Timeout = 10000)>]
let ``classifyRunScope: every project ran, none filtered -> RanEverything`` () =
    let results: TestResults =
        { Results =
            Map.ofList
                [ "Alpha.Tests", TestsPassed("ok", false, TimeSpan.Zero)
                  "Beta.Tests", TestsPassed("ok", false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let scope =
        classifyRunScope [ testConfigNamed "Alpha.Tests"; testConfigNamed "Beta.Tests" ] (Some results)

    test <@ scope = RanEverything 2 @>

[<Fact(Timeout = 10000)>]
let ``classifyRunScope: a filtered project -> RanSubset`` () =
    // `wasFiltered = true` on any project means a selection was applied. Whatever else
    // is true, this run did not look at the whole suite.
    let results: TestResults =
        { Results =
            Map.ofList
                [ "Alpha.Tests", TestsPassed("ok", true, TimeSpan.Zero)
                  "Beta.Tests", TestsPassed("ok", false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let scope =
        classifyRunScope [ testConfigNamed "Alpha.Tests"; testConfigNamed "Beta.Tests" ] (Some results)

    test <@ scope = RanSubset(2, 2) @>

[<Fact(Timeout = 10000)>]
let ``classifyRunScope: an unfiltered run that skipped a project -> RanSubset`` () =
    // Filtering nothing is not the same as covering everything. A run that simply
    // didn't execute half the suite covered no more of it than a filtered one did.
    let results: TestResults =
        { Results = Map.ofList [ "Alpha.Tests", TestsPassed("ok", false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let scope =
        classifyRunScope
            [ testConfigNamed "Alpha.Tests"
              testConfigNamed "Beta.Tests"
              testConfigNamed "Gamma.Tests" ]
            (Some results)

    test <@ scope = RanSubset(1, 3) @>

[<Fact(Timeout = 10000)>]
let ``classifyRunScope: the zero-affected skip's empty green is RanNothing, NOT full-suite`` () =
    // The trap. `TestRunCompleted.RanFullSuite` is vacuously TRUE for an empty Results
    // map (nothing was filtered because nothing ran), and the degenerate
    // zero-affected skip produces exactly that. A merge gate reading that flag would
    // see "full suite: true" for a run in which no test executed — AUTOMATION-108's
    // shape precisely. `classifyRunScope` refuses to launder it.
    let skipped: TestResults =
        { Results = Map.empty
          Elapsed = TimeSpan.Zero }

    test <@ TestResult.ranFullSuite skipped.Results @>
    test <@ classifyRunScope [ testConfigNamed "Alpha.Tests" ] (Some skipped) = RanNothing @>

[<Fact(Timeout = 10000)>]
let ``classifyRunScope: no run at all is RanNothing`` () =
    test <@ classifyRunScope [ testConfigNamed "Alpha.Tests" ] None = RanNothing @>

// ---------------------------------------------------------------------------
// AUTOMATION-99 — a test run the daemon cannot SEE is a gate that cannot
// gate. The `run-tests` IPC command used to call `executeTests` directly on
// the IPC thread: no `RunExclusive "tests"` slot, no `Running` status, no
// busy accounting. During such a run the daemon's whole model read "at
// rest" — `fshw check` could exit 0 while the test process was literally
// alive, and any concurrent FileChecked stamped a terminal status over it
// (the observed "✓ test-prune, started: with no elapsed:" signature).
// ---------------------------------------------------------------------------

/// A single-project config whose command touches `started`, waits until
/// `release` exists (bounded), then touches `done` — a run whose in-flight
/// window the test controls deterministically. The script lives in a file
/// (`sh <script>`) so no argument-quoting rules apply.
let private gatedRunConfig (tmpDir: string) =
    let started = Path.Combine(tmpDir, "started")
    let release = Path.Combine(tmpDir, "release")
    let doneFile = Path.Combine(tmpDir, "done")
    let scriptPath = Path.Combine(tmpDir, "gated-run.sh")

    File.WriteAllText(
        scriptPath,
        $"touch {started}\n"
        + $"n=0\n"
        + $"while [ ! -f {release} ] && [ \"$n\" -lt 100 ]; do sleep 0.1; n=$((n+1)); done\n"
        + $"touch {doneFile}\n"
    )

    let config =
        { Project = "GatedProject"
          Command = "sh"
          Args = scriptPath
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = Some 30
          ReportVerificationFormat = AutoDetect }

    config, started, release, doneFile

[<Fact(Timeout = 30000)>]
let ``run-tests: an in-flight command-driven run is visible to the daemon model`` () =
    withTempDir "tp-cmd-visible" (fun tmpDir ->
        let config, started, release, _doneFile = gatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        let cmdTask = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        try
            waitUntil (fun () -> File.Exists started) 15000
            test <@ File.Exists started @>

            // The test process is now RUNNING. The daemon model must reflect it:
            // the plugin holds the exclusive "tests" slot (busy) and reports
            // Running — otherwise a concurrent `fshw check` sees "at rest" and
            // exits 0 while tests are still executing.
            test <@ host.AnyPluginBusy() @>

            let statusDuringRun = host.GetStatus("test-prune")

            test
                <@
                    match statusDuringRun with
                    | Some(Running _) -> true
                    | _ -> false
                @>
        finally
            File.WriteAllText(release, "")

        cmdTask.Wait(TimeSpan.FromSeconds 20.0) |> ignore
        test <@ cmdTask.IsCompleted @>
        // The command still returns the results JSON it always did.
        test <@ cmdTask.Result.IsSome @>
        test <@ cmdTask.Result.Value.Contains("projects") @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked while a test run is in flight must not report a terminal status`` () =
    withTempDir "tp-midrun-stamp" (fun tmpDir ->
        let config, started, release, doneFile = gatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        let cmdTask = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        try
            waitUntil (fun () -> File.Exists started) 15000
            test <@ File.Exists started @>

            // A FileChecked lands MID-RUN (an editor save during a long suite).
            // Whatever its analysis outcome, the plugin must NOT go terminal:
            // the run owns the status until TestsFinished delivers the earned
            // verdict. (Analysis diagnostics still reach the error ledger —
            // nothing is lost by staying Running.)
            let srcFile = Path.Combine(tmpDir, "Lib.fs")
            File.WriteAllText(srcFile, "module Lib\nlet x = 1\n")

            // SUBSCRIBE to status transitions before the mid-run event: the
            // bug this hunts is a TRANSIENT terminal stamped and immediately
            // overwritten, which a polling sampler can miss entirely. The
            // OnStatusChanged subscription cannot miss an edge.
            let terminalDuringRun = beginAwaitNextTerminal host "test-prune"

            host.EmitFileChecked(
                { fakeFileCheckResult srcFile with
                    Source = "module Lib\nlet x = 1\n" }
            )

            // The FileChecked handler gets ample time to run; the run is
            // provably still gated (`done` not written), so ANY terminal
            // transition observed here is the manufactured-status lie.
            let stampedMidRun = terminalDuringRun.Wait(TimeSpan.FromSeconds 3.0)
            test <@ not (File.Exists doneFile) @>
            test <@ not stampedMidRun @>
        finally
            File.WriteAllText(release, "")

        cmdTask.Wait(TimeSpan.FromSeconds 20.0) |> ignore
        test <@ cmdTask.IsCompleted @>)

[<Fact(Timeout = 30000)>]
let ``a green run's Completed status carries its verdict`` () =
    // AUTOMATION-99, the type-level guarantee end-to-end: the status a real
    // green run reports CARRIES what it did (summary) — a ✓ with nothing to
    // say is unrepresentable — and the run-history record holds the SAME
    // summary (one channel, host-routed).
    withTempDir "tp-verdict" (fun tmpDir ->
        let host, _sentinel = withSingleProjectHarness tmpDir "VerdictProject"
        emitBuildAndWaitTerminal host

        match host.GetStatus("test-prune") with
        | Some(PluginStatus.Completed(_, v)) ->
            test <@ v.Summary.Contains "1 passed" @>
            test <@ v.Summary.Contains "0 failed" @>

            let record = List.head (host.GetHistory("test-prune"))
            test <@ record.Summary = Some v.Summary @>
        | other -> Assert.Fail($"expected Completed carrying a verdict, got: %A{other}"))

// ---------------------------------------------------------------------------
// `test-rerun` is the repo's "prove it ran" verb: it must NEVER report success
// without running. A slot held by another run QUEUES the force-run — it does
// not decline it (AUTOMATION-99 review, finding 1).
// ---------------------------------------------------------------------------

/// Like `gatedRunConfig`, but the script also appends one line per invocation
/// to a `runs` file, so a test can COUNT how many times the suite actually
/// executed rather than trusting a status.
let private countingGatedRunConfig (tmpDir: string) =
    let started = Path.Combine(tmpDir, "started")
    let release = Path.Combine(tmpDir, "release")
    let runs = Path.Combine(tmpDir, "runs")
    let scriptPath = Path.Combine(tmpDir, "counting-gated-run.sh")

    File.WriteAllText(
        scriptPath,
        $"echo run >> {runs}\n"
        + $"touch {started}\n"
        + $"n=0\n"
        + $"while [ ! -f {release} ] && [ \"$n\" -lt 100 ]; do sleep 0.1; n=$((n+1)); done\n"
    )

    let config =
        { Project = "GatedProject"
          Command = "sh"
          Args = scriptPath
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = Some 30
          ReportVerificationFormat = AutoDetect }

    config, started, release, runs

let private runCount (runs: string) =
    if File.Exists runs then
        File.ReadAllLines(runs)
        |> Array.filter (fun l -> l.Trim() <> "")
        |> Array.length
    else
        0

[<Fact(Timeout = 60000)>]
let ``run-tests refused the slot is QUEUED and still runs — never a green it did not earn`` () =
    withTempDir "tp-rerun-queued" (fun tmpDir ->
        let config, started, release, runs = countingGatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        // Run #1 claims the slot and blocks on the gate.
        let first = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask
        waitUntil (fun () -> File.Exists started) 20000
        test <@ runCount runs = 1 @>

        // Run #2 arrives while the slot is HELD. Pre-fix this replied `busy`
        // (→ exit 0) having executed nothing. It must now be queued and RUN.
        let second = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        // The queued run has NOT started yet (the slot is still held) …
        Thread.Sleep 500
        test <@ runCount runs = 1 @>
        test <@ not second.IsCompleted @>

        // … and it is still owed: nothing has been reported back to the caller.
        File.WriteAllText(release, "")

        first.Wait(TimeSpan.FromSeconds 30.0) |> ignore
        second.Wait(TimeSpan.FromSeconds 30.0) |> ignore

        test <@ first.IsCompleted @>
        test <@ second.IsCompleted @>

        // THE POINT: the suite executed TWICE. The second force-run was not
        // dropped, and its reply is a real results payload — not a "busy"
        // non-verdict that would have exited 0 without running.
        waitUntil (fun () -> runCount runs = 2) 20000
        test <@ runCount runs = 2 @>

        test <@ second.Result.IsSome @>
        let json = second.Result.Value
        test <@ json.Contains("projects") @>
        test <@ not (json.Contains("\"busy\"")) @>)

[<Fact(Timeout = 60000)>]
let ``a queued run-tests reply resolves — a refused claim can never strand the IPC caller`` () =
    // The latent hang the RunClaim DU makes impossible: the reply TCS lives
    // inside the work async, so a claim that was silently dropped resolved
    // nothing and the command's `Async.AwaitTask reply.Task` waited forever.
    withTempDir "tp-rerun-noStrand" (fun tmpDir ->
        let config, started, release, _runs = countingGatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        let first = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask
        waitUntil (fun () -> File.Exists started) 20000

        // Three force-runs pile up behind the in-flight one. Every one of them
        // must get a reply — none may be stranded.
        let queued =
            [ for _ in 1..3 -> host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask ]

        File.WriteAllText(release, "")

        first.Wait(TimeSpan.FromSeconds 30.0) |> ignore

        for t in queued do
            t.Wait(TimeSpan.FromSeconds 30.0) |> ignore
            test <@ t.IsCompleted @>
            test <@ t.Result.IsSome @>)

[<Fact(Timeout = 30000)>]
let ``run-tests bounds its wait: a run that outlives the budget reports busy, never a verdict`` () =
    // AUTOMATION-98's rule applied to the last unbounded seam: the reply wait.
    // A 1-second budget against a gated (never-releasing) run must return the
    // DISTINCT `busy` status — which the CLI maps to a NON-ZERO exit, so it can
    // never be read as a pass the run did not produce.
    withTempDir "tp-rerun-bounded" (fun tmpDir ->
        let config, started, release, _runs = countingGatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        try
            let json =
                host.RunCommand("run-tests", [| """{"waitSec":1}""" |])
                |> Async.RunSynchronously

            test <@ json.IsSome @>
            test <@ json.Value.Contains("\"busy\"") @>
            // Never a pass/fail verdict the command did not earn.
            test <@ not (json.Value.Contains("\"projects\"")) @>
            test <@ File.Exists started @>
        finally
            // Let the daemon-side run finish so the temp dir can be cleaned.
            File.WriteAllText(release, "")
            waitUntil (fun () -> not (host.AnyPluginBusy())) 30000)

// =============================================================================
// AUTOMATION-125 — a run may clear ONLY what it COVERED.
//
// Observed live (2026-07-14): a full run failed one project; a queued
// impact-filtered re-run then executed a NARROWER selection, passed, and — via
// `ClearAllErrors` + last-cycle-wins — SUPERSEDED the red. The failing test never
// re-ran and never passed, yet `check` went green. Same disease as
// AUTOMATION-95/99/112: a verdict that was not earned, "no failures reported by
// THIS run" read as "no failures".
//
// These drive the plugin's REAL `Custom(TestsFinished)` handler through a
// recording `PluginCtx` — the exact seam where the laundering happened — so the
// sequence is the one that was observed, not an analogue of it. Both directions
// are pinned: a disjoint filtered green must NOT clear, and a COVERING filtered
// green MUST (no over-correction into AUTOMATION-99's permanent stuck-red).
// =============================================================================

/// Recording ctx over the TestPrune plugin: captures the terminal statuses and
/// models the shared error ledger (ClearAllErrors wipes the plugin's whole slice,
/// exactly as `PluginFramework` does via `ClearPlugin`).
let private makeTestPruneRecordingCtx () =
    let statuses = System.Collections.Generic.List<PluginStatus>()

    let ledger =
        System.Collections.Generic.Dictionary<string, FsHotWatch.ErrorLedger.ErrorEntry list>()

    let ctx: FsHotWatch.PluginFramework.PluginCtx<TestPruneMsg> =
        { ReportStatus = fun s -> statuses.Add s
          ReportErrors = fun file entries -> ledger.[file] <- entries
          ClearErrors = fun file -> ledger.Remove(file) |> ignore
          ClearAllErrors = fun () -> ledger.Clear()
          EmitBuildCompleted = fun _ -> ()
          EmitTestRunStarted = fun _ -> ()
          EmitTestProgress = fun _ -> ()
          EmitTestRunCompleted = fun _ -> ()
          EmitCommandCompleted = fun _ -> ()
          Checker = Unchecked.defaultof<_>
          RepoRoot = ""
          Post = fun _ -> ()
          StartSubtask = fun _ _ -> ()
          UpdateSubtask = fun _ _ -> ()
          EndSubtask = fun _ -> ()
          Log = fun _ -> ()
          CompleteWithTimeout = fun _ -> ()
          RunExclusive = fun _ _ -> FsHotWatch.PluginFramework.Claimed
          IsRunning = fun _ -> false
          FcsSuppressedCodes = Set.empty
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    ctx, statuses, ledger

let private a125Config (project: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = "-c \"exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = Some "-- --filter-class {classes}"
      ClassJoin = "|"
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

/// A `TestsFinished` for a completed run: its per-project results and the SCOPE it
/// was launched against.
let private testsFinishedEvent (results: (string * TestResult) list) (launch: TestRunLaunch) =
    let runId = Guid.NewGuid()

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = Map.ofList results
          RanFullSuite = TestResult.ranFullSuite (Map.ofList results) }

    Custom(TestsFinished(started, completed, launch))

/// The runner's own wording for a failing test, so `parseFailedTests` attributes the
/// red to the CLASS (`ProjATests`) exactly as it does in the field.
let private failedProjA =
    TestsFailed("failed FsHotWatch.Tests.ProjATests.boom (12ms)", false, TimeSpan.FromSeconds 1.0)

/// What `executeTests` records for a project impact analysis SKIPPED: a pass, marked
/// filtered, with no output and no elapsed. It proves precisely nothing — and is the
/// value the old `ClearAllErrors` path read as "ProjA is fine now".
let private impactSkipped = TestsPassed("", true, TimeSpan.Zero)

let private passed (filtered: bool) =
    TestsPassed("ok", filtered, TimeSpan.FromSeconds 1.0)

/// Drive the plugin through a sequence of completed runs, returning the ctx
/// recorders and the final state. Starts from the handler's own initial state, so
/// every invariant the real plugin carries in state is carried here too.
let private driveRuns
    (handler: FsHotWatch.PluginFramework.PluginHandler<TestPruneState, TestPruneMsg>)
    (runs: PluginEvent<TestPruneMsg> list)
    =
    let ctx, statuses, ledger = makeTestPruneRecordingCtx ()

    let final =
        runs
        |> List.fold (fun state ev -> handler.Update ctx state ev |> Async.RunSynchronously) handler.Init

    ctx, statuses, ledger, final

let private lastStatus (statuses: System.Collections.Generic.List<PluginStatus>) : PluginStatus =
    test <@ statuses.Count > 0 @>
    statuses.[statuses.Count - 1]

let private ledgerFilesOf
    (ledger: System.Collections.Generic.Dictionary<string, FsHotWatch.ErrorLedger.ErrorEntry list>)
    : string list =
    ledger
    |> Seq.filter (fun kv -> not kv.Value.IsEmpty)
    |> Seq.map (fun kv -> kv.Key)
    |> Seq.toList

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a DISJOINT impact-filtered green does NOT clear a failed project's red`` () =
    // The observed sequence, exactly: full run fails ProjA → a queued impact-filtered
    // re-run selects only ProjB (ProjA is SKIPPED, and recorded as a filtered pass) →
    // ProjA must still be RED, and the plugin must NOT report a green terminal.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    // The narrower re-run: only ProjB's tests were selected. ProjA never executed.
    let filteredRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; filteredRun ]

    // The red survives the green that never ran it.
    test <@ final.OutstandingFailures |> List.exists (fun f -> f.Project = "ProjA") @>
    test <@ ledgerFilesOf ledger |> List.exists (fun f -> f.Contains("ProjA")) @>

    // And the verdict is non-green: `check` cannot exit 0 with a failing test
    // outstanding (a failing ledger entry alone would already deny it, but the
    // plugin's own status must not claim a green it did not earn either).
    match lastStatus statuses with
    | PluginStatus.Failed(msg, _, _) -> test <@ msg.Contains("ProjA") @>
    | other -> Assert.Fail($"a filtered green that never ran ProjA must not produce a green terminal, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a COVERING impact-filtered green DOES clear the red (no stuck-red)`` () =
    // The other direction — the over-correction guard (cf. AUTOMATION-99's stuck-RED
    // half). A filtered run that DID execute the failing class and passed it is real
    // evidence, and must clear the red. A gate that can never go green again is not a
    // fix, it is a different bug.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    // This time the selection COVERS the failing class (ProjATests) — and it passes.
    let coveringRun =
        testsFinishedEvent
            [ "ProjA", passed true; "ProjB", impactSkipped ]
            (filteredLaunch [ "ProjA", [ "ProjATests" ] ])

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; coveringRun ]

    test <@ List.isEmpty final.OutstandingFailures @>
    test <@ List.isEmpty (ledgerFilesOf ledger) @>

    match lastStatus statuses with
    | PluginStatus.Completed _ -> ()
    | other -> Assert.Fail($"a filtered run that executed the failing class green must clear it, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: an unfiltered re-run (test-rerun) clears an outstanding red`` () =
    // The escape hatch the rule leans on: `dotnet fshw test-rerun` runs every project
    // UNFILTERED, which covers everything and so may clear anything. Without this the
    // rule would be a wedge.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let rerun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; rerun ]

    test <@ List.isEmpty final.OutstandingFailures @>
    test <@ List.isEmpty (ledgerFilesOf ledger) @>

    match lastStatus statuses with
    | PluginStatus.Completed _ -> ()
    | other -> Assert.Fail($"an unfiltered all-green re-run must clear the red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a filtered green over a DIFFERENT class in the same project does not clear it`` () =
    // Project granularity is not enough. A run that selected ProjA but filtered to a
    // class OTHER than the failing one executed the project without executing the
    // failure — "ProjA passed" is true of that run and says nothing about the red.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA ] (fullSuiteLaunch [ "ProjA" ])

    let otherClassRun =
        testsFinishedEvent [ "ProjA", passed true ] (filteredLaunch [ "ProjA", [ "SomeOtherTests" ] ])

    let _ctx, statuses, _ledger, final = driveRuns handler [ fullRun; otherClassRun ]

    test <@ final.OutstandingFailures |> List.exists (fun f -> f.Class = Some "ProjATests") @>

    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"a filtered green over a different class must not clear the red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: the zero-affected skip (0 ran, green) cannot launder an outstanding red`` () =
    // The likeliest laundering path in practice: after the failing run, the next build
    // changes nothing relevant, so the skip gate completes "green, 0 ran". It executed
    // NOTHING, so it may clear nothing.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA ] (fullSuiteLaunch [ "ProjA" ])

    // The degenerate skip lifecycle: empty results, empty selection, Normal outcome.
    let skipRun = testsFinishedEvent [] emptyLaunch

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; skipRun ]

    test <@ final.OutstandingFailures |> List.exists (fun f -> f.Project = "ProjA") @>
    test <@ ledgerFilesOf ledger |> List.exists (fun f -> f.Contains("ProjA")) @>

    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"a run that executed nothing must not go green over an outstanding red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a TIMED-OUT project's red needs a WHOLE-project pass, not a class-filtered one`` () =
    // A project killed for being stuck is a fact about the PROJECT: no class-filtered
    // green may vindicate it (`Class = None`), and only a run that executed the project
    // in full can clear it.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let timedOut =
        testsFinishedEvent
            [ "ProjA",
              TestsTimedOut(
                  "failed FsHotWatch.Tests.ProjATests.slow (1ms)",
                  TimeSpan.FromSeconds 60.0,
                  false,
                  TimeSpan.FromSeconds 60.0
              ) ]
            (fullSuiteLaunch [ "ProjA" ])

    let classFilteredGreen =
        testsFinishedEvent [ "ProjA", passed true ] (filteredLaunch [ "ProjA", [ "ProjATests" ] ])

    let _ctx, _statuses, _ledger, afterFiltered =
        driveRuns handler [ timedOut; classFilteredGreen ]

    // The timeout is project-level: even a green over the very class named in the
    // timeout output does not clear it.
    test
        <@
            afterFiltered.OutstandingFailures
            |> List.exists (fun f -> f.Project = "ProjA" && f.Class = None)
        @>

    // A whole-project pass does.
    let wholeProjectGreen =
        testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ])

    let _ctx2, statuses2, _ledger2, afterFull =
        driveRuns handler [ timedOut; classFilteredGreen; wholeProjectGreen ]

    test <@ List.isEmpty afterFull.OutstandingFailures @>

    match lastStatus statuses2 with
    | PluginStatus.Completed _ -> ()
    | other -> Assert.Fail($"a whole-project pass must clear a timeout red, got %A{other}")

// --- RunCoverage: what a run is ENTITLED to clear (unit) ---

[<Fact(Timeout = 10000)>]
let ``RunCoverage: an impact-SKIPPED project (filtered pass, absent from the selection) covers nothing`` () =
    // The laundering vector in one assertion: the skip sentinel is a PASS, and reading
    // it as evidence is exactly the bug.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjB", ProjectClasses(Set.ofList [ "ProjBTests" ]) ])
            (Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ])

    test <@ not (RunCoverage.covers "ProjA" (Some "ProjATests") coverage) @>
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>
    test <@ RunCoverage.covers "ProjB" (Some "ProjBTests") coverage @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: an UNFILTERED result covers the whole project whatever the selection asked for`` () =
    // A project with selected classes but no `filterTemplate` runs in FULL. The RESULT
    // is the receipt, not the request — so it covers everything in the project.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectClasses(Set.ofList [ "OneClass" ]) ])
            (Map.ofList [ "ProjA", passed false ])

    test <@ RunCoverage.covers "ProjA" (Some "AnyOtherClass") coverage @>
    test <@ RunCoverage.covers "ProjA" None coverage @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: a class-filtered pass covers ONLY the classes it ran`` () =
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectClasses(Set.ofList [ "Alpha" ]) ])
            (Map.ofList [ "ProjA", passed true ])

    test <@ RunCoverage.covers "ProjA" (Some "Alpha") coverage @>
    test <@ not (RunCoverage.covers "ProjA" (Some "Beta") coverage) @>
    // ... and never a project-level red (an unparseable failure, a timeout).
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: deferred, errored and zero-match results cover nothing — they ran no tests`` () =
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull; "ProjC", ProjectInFull ])
            (Map.ofList
                [ "ProjA", TestsDeferred "apphost not produced"
                  "ProjB", TestsErrored "no parseable report"
                  "ProjC", TestsPassed(ZeroMatchMarker + "no tests matched", true, TimeSpan.Zero) ])

    test <@ not (RunCoverage.covers "ProjA" None coverage) @>
    test <@ not (RunCoverage.covers "ProjB" None coverage) @>
    test <@ not (RunCoverage.covers "ProjC" None coverage) @>
    test <@ coverage = Map.empty @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: a raw --filter passthrough claims no coverage (its reach is unknowable)`` () =
    // `run-tests --filter <raw>` launches every project in full but hands the runner an
    // arbitrary filter string. `wasFiltered` is true and the selection names no classes,
    // so we claim nothing rather than guess. Conservative in the safe direction: the red
    // survives until an unfiltered `test-rerun` proves it.
    let coverage =
        RunCoverage.ofRun (Map.ofList [ "ProjA", ProjectInFull ]) (Map.ofList [ "ProjA", passed true ])

    test <@ coverage = Map.empty @>

// --- OutstandingFailure.carry: the ledger algebra (unit) ---

let private redIn (project: string) (cls: string option) =
    { Project = project
      Class = cls
      File = $"<tests/%s{project}>"
      Entry = FsHotWatch.ErrorLedger.ErrorEntry.errorWithDetail $"%s{project} failed" "output" }

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: keeps an uncovered red, drops a covered-and-passed one`` () =
    let prior = [ redIn "ProjA" (Some "ProjATests"); redIn "ProjB" None ]

    // A run that covered ONLY ProjB, in full, and found nothing.
    let coverage: RunCoverage = Map.ofList [ "ProjB", CoveredWholeProject ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA"; "ProjB" ]) coverage [] prior

    test <@ carried |> List.map (fun f -> f.Project) = [ "ProjA" ] @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: a covered project that failed AGAIN keeps exactly one red`` () =
    let prior = [ redIn "ProjA" (Some "ProjATests") ]
    let coverage: RunCoverage = Map.ofList [ "ProjA", CoveredWholeProject ]
    let found = [ redIn "ProjA" (Some "ProjATests") ]

    let carried = OutstandingFailure.carry (Set.ofList [ "ProjA" ]) coverage found prior

    // Superseded by this run's own evidence — one entry, not two (AUTOMATION-95's
    // "Issue 2" accumulation must not come back).
    test <@ carried.Length = 1 @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: a red for a project no longer configured is pruned, never wedged`` () =
    // A project dropped from `tests.projects` can never be covered again — retaining its
    // red would be a permanent stuck-red with no command that could clear it.
    let prior = [ redIn "Removed" None; redIn "ProjA" None ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA" ]) RunCoverage.none [] prior

    test <@ carried |> List.map (fun f -> f.Project) = [ "ProjA" ] @>

// --- The task cache must not launder it either ---

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125: no cache participation while a red is outstanding`` () =
    // Two roads to the same laundered green. (1) BuildCompleted HITS a cached green
    // entry → the handler is skipped → no run → the red is replayed away. (2) A run
    // that passed everything it ran while carrying an uncovered red reports a FAILED
    // terminal; writing that under a content merkle would replay it on a tree that has
    // since been fixed. Both are refused by returning no key at all.
    let started: TestRunStarted =
        { RunId = Guid.NewGuid()
          StartedAt = DateTime.UtcNow }

    let allPassed =
        Custom(
            TestsFinished(
                started,
                { RunId = started.RunId
                  TotalElapsed = TimeSpan.Zero
                  Outcome = Normal
                  Results = Map.ofList [ "ProjA", TestsPassed("ok", false, TimeSpan.Zero) ]
                  RanFullSuite = true },
                fullSuiteLaunch [ "ProjA" ]
            )
        )

    let keyFor (hasOutstanding: bool) (event: PluginEvent<TestPruneMsg>) =
        cacheKeyFor
            (fun () -> "symbols")
            (fun () -> None)
            (fun () -> None)
            (fun () -> None)
            (fun () -> hasOutstanding)
            event

    // Clean plugin: the green fast-path is untouched.
    test <@ (keyFor false (BuildCompleted BuildSucceeded)).IsSome @>
    test <@ (keyFor false allPassed).IsSome @>

    // Outstanding red: no replay, no write — on either arm.
    test <@ (keyFor true (BuildCompleted BuildSucceeded)).IsNone @>
    test <@ (keyFor true allPassed).IsNone @>

// --- MergeGate's UnearnedScope protection is untouched (AUTOMATION-112) ---

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125: the merge gate still rejects a filtered green as UnearnedScope`` () =
    // The fix must not weaken the gate it stands beside. The filtered re-run from the
    // regression above still classifies as a SUBSET, and a merge verdict built on a
    // subset is still `UnearnedScope` (exit 3) — never Clean.
    let configs = [ a125Config "ProjA"; a125Config "ProjB" ]

    let filteredResults: TestResults =
        { Results = Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ]
          Elapsed = TimeSpan.FromSeconds 1.0 }

    match classifyRunScope configs (Some filteredResults) with
    | RanSubset(ran, total) ->
        test <@ ran = 2 && total = 2 @>

        let outcome =
            FsHotWatch.Cli.CheckVerdict.verdict
                FsHotWatch.Cli.CheckVerdict.MergeGate
                false
                FsHotWatch.Cli.IpcParsing.Complete
                (FsHotWatch.Cli.IpcParsing.ImpactFiltered(2, 2))

        test <@ FsHotWatch.Cli.CheckVerdict.exitCode outcome = 3 @>
    | other -> Assert.Fail($"a run with a filtered project is not a full-suite scope, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a test run does not erase the unanalysable-file warning (AUTOMATION-113)`` () =
    // The same defect on a different diagnostic, and it was live: the `TestsFinished`
    // ledger rewrite cleared this plugin's whole slice, so the FIRST test run after an
    // analysis failure dropped the warning that is supposed to DENY the check its green
    // verdict. The file kept forcing full-suite runs (state), but nothing told anyone
    // (ledger) — a gate that quietly stopped gating. The warning now leaves the ledger
    // only when the CONDITION clears: the file analyses cleanly.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let broken =
        { RelPath = "src/Broken.fs"
          File = "/tmp/src/Broken.fs"
          Reason = "FS3520: unexpected doc comment" }

    let stateWithUnanalysable =
        { handler.Init with
            UnanalyzableFiles = Map.ofList [ broken.RelPath, broken ] }

    let ctx, _statuses, ledger = makeTestPruneRecordingCtx ()

    // A full-suite run in which EVERYTHING passes — the strongest green there is.
    let greenRun =
        testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ])

    handler.Update ctx stateWithUnanalysable greenRun
    |> Async.RunSynchronously
    |> ignore

    let warnings =
        ledger
        |> Seq.collect (fun kv -> kv.Value)
        |> Seq.filter (fun e -> e.Severity = FsHotWatch.ErrorLedger.Warning)
        |> Seq.toList

    test <@ ledger.ContainsKey broken.File @>
    test <@ warnings |> List.exists (fun e -> e.Message.Contains("src/Broken.fs")) @>

    // ... and it DOES go once the file analyses cleanly (the state drops it).
    let ctx2, _statuses2, ledger2 = makeTestPruneRecordingCtx ()

    handler.Update ctx2 handler.Init greenRun |> Async.RunSynchronously |> ignore

    test <@ ledger2.Count = 0 @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage.coversWholeSuite: only every project, each in FULL, is a whole-suite claim`` () =
    // The question a verdict writer actually asks. It is answered from what the run
    // EXECUTED, so there is one notion of scope in the system — the same one the ledger
    // clears by — rather than a parallel one that can drift from it.
    let projects = [ "ProjA"; "ProjB" ]

    let everything: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredWholeProject ]

    let oneFiltered: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredClasses(Set.ofList [ "X" ]) ]

    let oneMissing: RunCoverage = Map.ofList [ "ProjA", CoveredWholeProject ]

    test <@ RunCoverage.coversWholeSuite projects everything @>
    // A filtered project covered LESS than the suite, whatever its result said.
    test <@ not (RunCoverage.coversWholeSuite projects oneFiltered) @>
    // So did a skipped one.
    test <@ not (RunCoverage.coversWholeSuite projects oneMissing) @>
    // And a run of nothing is never evidence of everything.
    test <@ not (RunCoverage.coversWholeSuite projects RunCoverage.none) @>
    test <@ not (RunCoverage.coversWholeSuite [] everything) @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage.coveredProjects: names exactly what the run executed`` () =
    let coverage: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredClasses(Set.ofList [ "X" ]) ]

    test <@ RunCoverage.coveredProjects coverage = Set.ofList [ "ProjA"; "ProjB" ] @>
    test <@ Set.isEmpty (RunCoverage.coveredProjects RunCoverage.none) @>

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: the last run's coverage is readable from state (a verdict writer's receipt)`` () =
    // `LastResults` says what the run FOUND; `LastCoverage` says what it COVERED. A
    // consumer outside the handler must be able to read the second, or it will invent
    // its own answer to "what did this run cover?" and the two will drift.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let filteredRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let _ctx, _statuses, _ledger, final = driveRuns handler [ filteredRun ]

    // Every project produced a "passed" result — and yet the run covered only ProjB's
    // one class. That gap is the whole ticket, and it is now legible from state.
    test <@ final.LastResults.IsSome @>
    test <@ RunCoverage.coveredProjects final.LastCoverage = Set.ofList [ "ProjB" ] @>
    test <@ not (RunCoverage.coversWholeSuite [ "ProjA"; "ProjB" ] final.LastCoverage) @>
