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

/// An empty launch: commits nothing and covers nothing, so it clears no outstanding red.
/// For tests that build `TestsFinished` directly. Runs that must CLEAR something use
/// `fullSuiteLaunch` / `filteredLaunch` below.
let private emptyLaunch: TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      RuntimeProjectsByFile = Map.empty
      Selection = Map.empty
      WouldHaveRun = None
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero }

/// A launch that ran every named project UNFILTERED — the scope a full suite (or a
/// plain `test-rerun`) has, and the only one whose green may clear an arbitrary red.
let private fullSuiteLaunch (projects: string list) : TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      RuntimeProjectsByFile = Map.empty
      Selection = projects |> List.map (fun p -> p, ProjectInFull) |> Map.ofList
      WouldHaveRun = None
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero }

/// A launch that ran only `classes` in each named project — an impact-filtered
/// selection. Projects NOT named were skipped entirely.
let private filteredLaunch (selection: (string * string list) list) : TestRunLaunch =
    { Symbols = Set.empty
      CoveringProjectsBySymbol = Map.empty
      RuntimeProjectsByFile = Map.empty
      Selection =
        selection
        |> List.map (fun (p, classes) -> p, ProjectClasses(Set.ofList classes))
        |> Map.ofList
      WouldHaveRun = None
      Seeds = []
      ZeroSelection = ZeroSelection.NotAZero }

let private waitForPluginIdle (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForSettled host pluginName (int (timeoutSecs * 1000.0))

let private waitForPluginTerminal (host: PluginHost) (pluginName: string) (timeoutSecs: float) =
    waitForTerminalStatus host pluginName (int (timeoutSecs * 1000.0))

/// Emit a FileChecked and wait for the mailbox to drain. FileChecked persists symbol
/// analysis as an in-handler side-effect with no status transition, so
/// `beginAwaitNextTerminal` would hang the full timeout — quiescence is the right sync.
let private emitFileAndQuiesce (host: PluginHost) (result: FileCheckResult) =
    host.EmitFileChecked result
    waitForQuiescent host 10000

/// Emit the BatchChecked cohort-complete signal over `files` and wait for the mailbox to
/// drain. This is what flushes accumulated PendingAnalysis to the symbol DB; like
/// FileChecked it is an in-handler side-effect with no status transition.
let private emitBatchAndQuiesce (host: PluginHost) (files: string list) =
    host.EmitBatchChecked(fakeBatchChecked files)
    waitForQuiescent host 10000

/// Emit a successful BuildCompleted and wait for a terminal status. This handler spawns
/// the test run via `Async.Start`, so the work outlives it and quiescence could return
/// early — a terminal await is the right sync.
///
/// Tests that index files emit this FIRST: the sidecar's `markClean` only fires for
/// FileChecked events arriving after a BuildCompleted has been observed in the session,
/// mirroring fshw's cold scan where BuildPlugin's terminal status gates the FCS tiers.
let private emitBuildAndWaitTerminal (host: PluginHost) =
    let generationBefore =
        host.WorkCycleGenerations()
        |> Map.tryFind "test-prune"
        |> Option.defaultValue 0L

    host.EmitBuildCompleted(BuildSucceeded)

    let completedNewCycle =
        waitUntilTrue
            (fun () ->
                let generationAfter =
                    host.WorkCycleGenerations()
                    |> Map.tryFind "test-prune"
                    |> Option.defaultValue 0L

                let terminal =
                    match host.GetStatus("test-prune") with
                    | Some(Completed _)
                    | Some(Failed _) -> true
                    | _ -> false

                generationAfter > generationBefore && terminal && not (host.AnyPluginBusy()))
            20000

    test <@ completedNewCycle @>

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

/// The run log these tests pretend was written. Most are about the per-test MATCHER and
/// don't care which arm this is; the ones that ARE about the log pass their own.
let private savedLog =
    FsHotWatch.RunLog.Ref.Written "/repo/.fshw/test-runs/deadbeef/FsHotWatch.Tests.output.log"

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

// =============================================================================
// FCS cache-poisoning gate. Cold-start FCS sometimes returns "expected type X but here
// has type X" for files that compile cleanly once warm, and flushing those poisoned
// symbols overwrites the prior good DB snapshot, breaking cache replay on the next boot.
// =============================================================================

/// A real FCS FileCheckResult — full type-check, real diagnostics — so the gate tests
/// below see realistic Error / Warning / clean diagnostic shapes.
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

[<Fact(Timeout = 30000)>]
let ``a cached helper-file analysis cannot relabel a freshly executed test failure as cached`` () =
    withTempDir "tp-fresh-red-provenance" (fun tmpDir ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let checkedFile =
            checkSourceForReal tmpDir "TestHelper.fsx" "module TestHelper\nlet value = 42\n"
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        let configs =
            [ { Project = "Failing.Tests"
                Command = "sh"
                Args = "-c \"echo 'failed Failing.Tests.Example.still_fails (1ms)'; exit 1\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = Disabled } ]

        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)
        host.RegisterHandler(create (Path.Combine(tmpDir, "test.db")) tmpDir (Some configs) None None None None [])

        host.EmitFileChecked checkedFile
        waitForPluginTerminal host "test-prune" 12.0

        // Positive control: the same helper analysis remains cacheable while healthy.
        host.EmitFileChecked checkedFile
        test <@ waitForCachedReplay host "test-prune" 10000 @>

        host.EmitBuildCompleted BuildSucceeded

        let freshRed =
            waitUntilTrue
                (fun () ->
                    not (host.GetErrorsByPlugin("test-prune") |> Map.isEmpty)
                    && (terminalSummaryOf host "test-prune").Contains "failed")
                15000

        test <@ freshRed @>
        let fresh = terminalSummaryOf host "test-prune"
        let freshStatus = host.GetStatus("test-prune")
        let freshLedger = host.GetErrorsByPlugin("test-prune")
        test <@ not (fresh.Contains "(cached)") @>

        host.EmitFileChecked checkedFile
        waitForQuiescent host 10000

        test <@ terminalSummaryOf host "test-prune" = fresh @>
        test <@ host.GetStatus("test-prune") = freshStatus @>
        test <@ host.GetErrorsByPlugin("test-prune") = freshLedger @>)

[<Fact(Timeout = 30000)>]
let ``FileChecked analyzes its existing FCS payload without re-entering the checker`` () =
    withTempDir "tp-no-checker-reentry" (fun tmpDir ->
        let source = "module AlreadyChecked\nlet value = 42\n"

        let result =
            checkSourceForReal tmpDir "AlreadyChecked.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // The event already contains both parse and full-check results. Making the
        // host checker unusable proves TestPrune consumes that payload instead of
        // starting a second ParseAndCheckFileInProject pass.
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        host.RegisterHandler(create (Path.Combine(tmpDir, "test.db")) tmpDir None None None None None [])
        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 12.0

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"Expected payload-only analysis to complete, got: %A{other}"))

[<Fact(Timeout = 15000)>]
let ``hasFcsErrors returns false for ParseOnly`` () =
    test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty "" ParseOnly) @>

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors returns true for source with type error`` () =
    withTempDir "tp-poisoning-err" (fun tmpDir ->
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
        // An incomplete pattern match: FCS reports FS0025 at Warning severity.
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

        // Sanity: the source really does carry warning diagnostics.
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

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

// =============================================================================
// `hasFcsErrors` must apply the same suppression filter (parseNowarnCodes plus
// FcsSuppressedCodes) that `Daemon.reportFcsDiagnostics` applies to the user-visible
// error stream. Without it the gate trips on codes the user has already silenced — e.g.
// FS1182 promoted to Error by `<TreatWarningsAsErrors>` alongside `#nowarn` — killing
// cache replay across daemon restarts on cold scans.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors respects per-file #nowarn directives`` () =
    withTempDir "tp-poisoning-nowarn" (fun tmpDir ->
        let source =
            """#nowarn "1"
module Test
let x : int = "not-an-int"
"""

        let result =
            checkSourceForReal tmpDir "NoWarn.fsx" source
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "CheckFile returned None")

        // `#nowarn` does not suppress upstream FCS errors — FCS still reports FS0001 at
        // Severity = Error, and the gate's own suppression filter is what must drop it.
        let hasErrorDiagnostic =
            match result.CheckResults with
            | FullCheck cr ->
                cr.Diagnostics
                |> Array.exists (fun d ->
                    d.ErrorNumber = 1
                    && d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
            | ParseOnly -> false

        test <@ hasErrorDiagnostic @>

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>)

[<Fact(Timeout = 30000)>]
let ``hasFcsErrors respects configured FcsSuppressedCodes`` () =
    withTempDir "tp-poisoning-config" (fun tmpDir ->
        // No `#nowarn` in source: the caller passes the set instead, which is how daemons
        // silence cold-scan-only noise codes (`fcsSuppressedCodes` in DaemonConfig).
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
    // The positive control for the two suppression tests above: they would both pass
    // against a gate that had simply stopped firing.
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
    // Dirty FCS results do NOT block the symbol-DB write. The protection against Phase B
    // seeing "0 stored" lives in the freshness sidecar, which marks the file
    // `fcsClean = false` so detectChanges bypasses the diff rather than computing a
    // phantom "all symbols changed" delta against an empty stored row set.
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

        // Sanity: the result really is poisoned.
        test <@ FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults @>

        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        emitBuildAndWaitTerminal host

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        let freshDb = Database.create dbPath
        let symbols = freshDb.GetSymbolsInFile "Broken.fsx"
        test <@ not symbols.IsEmpty @>

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

        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result
        emitBatchAndQuiesce host [ cleanFile ]

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

        // A clean check arriving after BuildCompleted stamps `fcsClean = true`, so Phase B
        // detectChanges trusts the stored rows for this file.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "Clean.fsx" freshness @>)

[<Fact(Timeout = 30000)>]
let ``BatchChecked persists accumulated symbols to DB without a follow-up BuildCompleted`` () =
    // On a cold scan `performScan` awaits BuildPlugin terminal BEFORE the FCS tier checks,
    // so BuildCompleted reaches the mailbox before any FileChecked and flushes an empty
    // PendingAnalysis. The N FileCheckeds then populate it, and BatchChecked is the only
    // remaining signal that can flush them — otherwise the symbol DB stays empty and every
    // subsequent cold scan perpetuates that.
    withTempDir "tp-batchchecked-flush" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let host = PluginHost.create checker tmpDir
        // No testConfigs, so BuildCompleted is unsubscribed and only FileChecked and
        // BatchChecked can drive the flush — the BatchChecked subscription is
        // unconditional.
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

        // No BuildCompleted ever fires.
        emitBatchAndQuiesce host [ cleanFile ]

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
    // Dirty FCS may overwrite rows — persistence is unconditional — but the freshness
    // sidecar marks the file dirty so a later `detectChanges` against those
    // potentially-poisoned rows is bypassed. What must be prevented is the spurious large
    // diff (the 4921-affected-tests Phase B regression), and the sidecar, not the
    // symbol-DB write decision, is what prevents it.
    withTempDir "tp-poisoning-coldboot" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")

        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

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

        emitBuildAndWaitTerminal host1

        emitFileAndQuiesce host1 cleanResult
        emitBatchAndQuiesce host1 [ file ]

        let mutable phase1Tests: TestMethodInfo list = []

        waitUntil
            (fun () ->
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                let db = Database.create dbPath
                phase1Tests <- db.GetTestMethodsInFile "CB.fsx"
                phase1Tests.Length >= 1)
            5000

        test <@ phase1Tests.Length >= 1 @>

        // Phase 2: cold-boot poisoning — a fresh plugin instance reading the prior DB,
        // with the same file now carrying Error-severity diagnostics.
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

        emitBuildAndWaitTerminal host2

        emitFileAndQuiesce host2 brokenResult

        // `markUnverified` preserves a prior clean record: Phase 1's `fcsClean = true` is
        // NOT downgraded even though the current check has Error-severity diagnostics.
        // The trade-off is deliberate — cold-start reliability over precision on
        // user-broke-their-code transients — and the next genuine clean check refreshes
        // the timestamp.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "CB.fsx" freshness @>

        // The sidecar still reads clean, but `currentClean` is false for this event, so
        // detectChanges is bypassed regardless of stored state and ChangedFiles gains no
        // phantom entry from the poisoned check.
        let changedAfterDirty =
            host2.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        test <@ changedAfterDirty.Value = "[]" @>)

// =============================================================================
// The per-file freshness sidecar gates the detectChanges call site, so cross-restart
// Phase B replay never DIFFS against rows that ended their last session FCS-dirty:
// those rows may be partial, and diffing a complete extraction against them reports a
// phantom "all symbols changed" delta — the 4921-affected-tests regression.
//
// AUTOMATION-526. "Never diff against them" was always right. "Therefore contribute
// nothing" was not, and that is what this gate used to do. A file whose last check hit
// a transient FCS error had its tests selected by NO impact-filtered run afterwards —
// silently, under a green check, because nothing distinguished "I cannot tell what
// changed in this file" from "nothing changed in this file". The stored rows are not a
// baseline; the CURRENT extraction, which the gate has already established is clean, is
// complete. So the answer is the one `Clean, NoRows` already gives: there is no before,
// and every symbol in the file is new.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``Phase B replay: stored=dirty, current=clean → the file's tests are SELECTED, not dropped`` () =
    withTempDir "tp-phaseb-bypass" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let relPath = "PhaseB.fsx"
        let absPath = Path.Combine(tmpDir, relPath)

        // A dirty sidecar entry and no DB rows: the prior session ended dirty.
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

        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result

        // AUTOMATION-526. This read `= "[]"` before the fix, and that empty list IS the
        // defect: the sidecar said dirty when the FileChecked arrived, so the file was
        // dropped from selection entirely on the very pass that recovered from the FCS
        // error. Nothing warned; the run was green.
        let changedFiles = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously

        // Exactly this one file. `Contains` alone would also pass a fix that escalated
        // to "everything changed" — the over-widening AUTOMATION-526's positive control
        // forbids — so the whole list is pinned: the widening is per FILE.
        test <@ changedFiles.Value = $"[\"%s{relPath}\"]" @>

        // The clean recheck flips the sidecar dirty → clean, so the NEXT restart's Phase B
        // trusts the rows — which is what bounds the widening to ONE pass per recovery
        // rather than one on every subsequent save.
        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean relPath freshness @>)

[<Fact(Timeout = 30000)>]
let ``Phase B replay: stored=clean → detectChanges runs as today`` () =
    // The guard against an over-aggressive gate that would mask legitimate changes.
    let initialSource = "module Lib\nlet x = 1\n"
    let astChangedSource = "module Lib\nlet x = 1\nlet y = 2\n"

    withSeededTestEnv "tp-phaseb-realdiff" "Lib.fs" initialSource (fun env ->
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
// BuildCompleted-gated stamping. `markClean` fires only for FileChecked events arriving
// AFTER a BuildCompleted in the current session; earlier ones stamp `markUnverified`,
// treated as dirty unless a prior clean record exists. This is what the fcs-clean
// predicate alone could not solve: by the time the pipeline emits BuildCompleted, FCS has
// been warmed by the build's reference-graph realization, so subsequent FileChecked
// events extract the same number of symbols a warm Phase B rerun would.
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``Item 3: pre-BuildCompleted clean FileChecked → sidecar stays dirty`` () =
    // `currentClean = true` is necessary but not sufficient; BuildCompleted is what
    // signals warm-enough state.
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

        test <@ not (FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors Set.empty result.Source result.CheckResults) @>

        // Deliberately NOT emitBuildAndWaitTerminal first.
        host.EmitFileChecked(result)
        waitForPluginTerminal host "test-prune" 10.0

        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Pre.fsx" freshness) @>)

[<Fact(Timeout = 30000)>]
let ``Item 3: post-BuildCompleted clean FileChecked → sidecar stamped clean`` () =
    // Same harness as the pre-build case, with the ordering reversed.
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

        emitBuildAndWaitTerminal host

        emitFileAndQuiesce host result

        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ FsHotWatch.TestPrune.FileFreshness.isClean "Post.fsx" freshness @>)

[<Fact(Timeout = 30000)>]
let ``Item 3: clean check after prior dirty, still pre-build → stays dirty`` () =
    // Two FileCheckeds and no BuildCompleted: dirty, then clean. The clean one must not
    // promote the entry, because warm extraction stability is still not guaranteed.
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

        // A fresh pipeline, so FCS reanalyzes against the new source.
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

        let freshness = FsHotWatch.TestPrune.FileFreshness.load tmpDir
        test <@ not (FsHotWatch.TestPrune.FileFreshness.isClean "Mixed.fsx" freshness) @>)

// =============================================================================
// detectChanges call site: stored and current must agree on unit. The DB stores externs
// under the synthetic SourceFile "_extern" and `GetSymbolsInFile` filters by
// source_file = relPath, so the stored side is file-local only. Passing unfiltered
// `normalizedSymbols` (file-local + externs) on the current side produced a phantom diff
// equal to the file's extern count on every clean re-check — and externs are ~80% of a
// real file's allSymbols, hence "Phase B always reports 4921 affected tests".
// =============================================================================

[<Fact(Timeout = 30000)>]
let ``detectChanges: re-check of unchanged source with externs reports no changes`` () =
    // `List.length` makes the extractor pull in Microsoft.FSharp.Collections.List.length
    // as an extern symbol.
    let source = "module Lib\nlet xs = List.length []\n"

    withSeededTestEnv "tp-extern-filter" "Lib.fsx" source (fun env ->
        // Both controls: the extracted set contains externs, and the DB read-back does
        // not. Without them the test passes tautologically.
        let externs = env.SeededSymbols |> List.filter (fun s -> s.IsExtern)
        test <@ not externs.IsEmpty @>

        let storedFromDb = env.Db.GetSymbolsInFile(env.RelPath)
        test <@ storedFromDb |> List.forall (fun s -> not s.IsExtern) @>

        // Re-check the IDENTICAL source, no edit.
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
// A cold-start missing apphost must NOT be reported as a FAILED test. `dotnet run
// --no-build` launched before the build plugin produced the apphost fails with "An error
// occurred trying to start process … No such file or directory", which
// `looksLikeApphostMissing` distinguishes from a genuine non-zero test exit.
// =============================================================================

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing detects the start-process launch failure`` () =
    let output =
        "Unhandled exception: System.ComponentModel.Win32Exception (2): An error occurred trying to start process '/repo/tests/Unit/bin/Debug/net10.0/Unit' with working directory '/repo'. No such file or directory"

    test <@ looksLikeApphostMissing output @>

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing is false for a genuine test failure`` () =
    // Misclassifying a real failure as apphost-missing would SILENCE reds — the opposite,
    // and worse, failure mode.
    let output =
        "failed FsHotWatch.Tests.FooTests.bar (3ms)\nTest run summary: Failed!\n  total: 10\n  failed: 1\n  succeeded: 9"

    test <@ not (looksLikeApphostMissing output) @>

[<Fact(Timeout = 15000)>]
let ``looksLikeApphostMissing is false for empty / passing output`` () =
    test <@ not (looksLikeApphostMissing "") @>
    test <@ not (looksLikeApphostMissing "Test run summary: Passed!\n  total: 5\n  succeeded: 5") @>

// Structural apphost detection: `tryApphostPresent` derives the binary path from the
// runner's `--project` arg and File.Exists-checks it, rather than sniffing localized OS
// error text.

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent returns None when args carry no --project`` () =
    // Not derivable, so the caller falls back to the output sniff.
    test <@ tryApphostPresent "/tmp/runner.sh" "/repo" = None @>
    test <@ tryApphostPresent "test" "/repo" = None @>

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports false when the bin dir is absent`` () =
    withTempDir "tp-apphost-struct-missing" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        Directory.CreateDirectory(projDir) |> ignore
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some false @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports false when bin exists but apphost is missing`` () =
    withTempDir "tp-apphost-struct-empty-bin" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        // The DLL landed; the apphost did not.
        File.WriteAllText(Path.Combine(tfmDir, "Unit.dll"), "")
        test <@ tryApphostPresent $"run --project {projDir} --no-build --" tmpDir = Some false @>)

[<Fact(Timeout = 15000)>]
let ``tryApphostPresent reports true when the apphost binary exists`` () =
    withTempDir "tp-apphost-struct-present" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        // The apphost is the extension-less sibling of the canonical DLL.
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

// `transient` = the apphost-missing failure clears on retry (the cold-start race);
// otherwise it persists every run. The configs run a bare `sh <script>` with no
// `--project`, so `tryApphostPresent` returns None and the plugin falls back to the
// `looksLikeApphostMissing` output sniff — exercising that path end-to-end.
[<Theory(Timeout = 20000)>]
[<InlineData(true)>] // cold-start: fails once with the launch signature, then succeeds
[<InlineData(false)>] // persistent: apphost never appears
let ``apphost-missing cold-start retries green; persistent defers non-green (never FAILED test)`` (transient: bool) =
    withTempDir "tp-apphost" (fun tmpDir ->
        let scriptPath = Path.Combine(tmpDir, "runner.sh")

        // The .NET host's start-process signature with no test-summary block. The retry
        // counter lives in a file under the working dir (repoRoot = tmpDir) to avoid
        // nested shell quoting through the F# arg string.
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

        // In neither case may an apphost-missing launch be a test FAILED: it is an
        // ordering bug, never a real red.
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
            test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

            match host.GetStatus("test-prune") with
            | Some(Failed _) -> Assert.Fail("transient apphost-missing was reported as FAILED")
            | _ -> ()
        else
            // A persistently-missing apphost means the tests NEVER RAN: deferred, which is
            // NON-GREEN (a CI check must not silent-green it) but not a failure. The
            // diagnostic is `Deferred` severity, which the verdict routes to
            // Incomplete/exit 2, and the status is a non-failing terminal. Older code
            // returned TestsPassed here — a false green.
            test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

            let allEntries =
                host.GetErrorsByPlugin("test-prune") |> Map.toList |> List.collect snd

            let waitingDiagnostic =
                allEntries
                |> List.exists (fun e ->
                    e.Severity = FsHotWatch.ErrorLedger.Deferred
                    && e.Message.ToLowerInvariant().Contains("waiting on build"))

            test <@ waitingDiagnostic @>
            test <@ allEntries |> List.forall (fun e -> e.Severity <> FsHotWatch.ErrorLedger.Error) @>

            match host.GetStatus("test-prune") with
            | Some(Completed(_, v)) -> test <@ v.Summary.ToLowerInvariant().Contains("waiting on build") @>
            | other ->
                Assert.Fail($"expected a non-failing Completed status for a pure deferred project, got %A{other}"))

// =============================================================================
// Freshness gate. `tryApphostPresent`/`detectApphostMissing` only fire on a FAILED launch
// (post-exit), so a PRESENT-but-STALE apphost that exits 0 reported a false GREEN:
// `--no-build` ran OLD bits and "passed". This gate runs PRE-launch, independent of exit
// code — build output that predates its inputs defers as "waiting on build" exactly like
// a missing apphost. Mirrors BuildPlugin.verifyArtifactsFresh (ADR-008).
//
// WHAT it compares is the hard part. Comparing the test DLL against the newest source
// ANYWHERE IN THE REPO condemns every project outside an edit's dependency closure, and
// the accusation cannot be cleared: an incremental `dotnet build` is correctly a no-op for
// an unaffected project, so its DLL never catches the repo-wide watermark and only
// `-t:Rebuild` escapes. Looking at `.fs`/`.cs` alone also misses a changed test FIXTURE
// copied in from a shared project — the run reads the OLD copy out of `bin/` and passes
// (`dsa-scope-4.json`, 2026-07-14: a fake green that left main red for hours).
//
// Both directions are pinned below — an out-of-closure edit is FRESH, an in-closure one
// STALE — and content items are judged by the COPY the run would actually read.
// =============================================================================

// `ArtifactFreshness.Cache` is documented "each project is walked at most ONCE per run"
// and "thread-safe: test groups run in parallel". Both were true separately and neither
// implied the other: `ConcurrentDictionary.GetOrAdd(key, valueFactory)` is thread-SAFE
// (one result is published) without being once-ONLY — it may invoke the factory on
// several threads for one key and discard the losers' work. Test groups do run in
// parallel with heavily-overlapping ProjectReference closures, so the directory walks and
// `XDocument.Load` parses the memo exists to eliminate could still each happen N times.
//
// RED-BEFORE-GREEN: implement `OnceMemo.GetOrAdd` as `entries.GetOrAdd(key, factory)` and
// this counts 16 factory runs, not 1.

[<Fact(Timeout = 30000)>]
let ``OnceMemo runs the value factory exactly ONCE per key under concurrent access`` () =
    let memo = ArtifactFreshness.OnceMemo<string, int>()
    let mutable factoryRuns = 0
    let entrants = 16
    use released = new Barrier(entrants)

    let factory (_: string) =
        Interlocked.Increment(&factoryRuns) |> ignore
        // The real factories walk directories and parse XML. A slow factory widens the
        // window in which a second caller finds the key still absent — precisely the
        // window a plain `GetOrAdd` leaves open.
        Thread.Sleep 100
        42

    let results = Array.zeroCreate<int> entrants

    let threads =
        [| for i in 0 .. entrants - 1 ->
               Thread(fun () ->
                   released.SignalAndWait()
                   results[i] <- memo.GetOrAdd("the-one-key", factory)) |]

    for t in threads do
        t.Start()

    for t in threads do
        t.Join()

    test <@ results |> Array.forall (fun r -> r = 42) @>
    // Not "once was published" — once was RUN.
    test <@ factoryRuns = 1 @>

[<Fact(Timeout = 30000)>]
let ``OnceMemo runs the value factory once per DISTINCT key`` () =
    let memo = ArtifactFreshness.OnceMemo<string, string>()
    let mutable factoryRuns = 0

    let factory (k: string) =
        Interlocked.Increment(&factoryRuns) |> ignore
        k + "!"

    test <@ memo.GetOrAdd("a", factory) = "a!" @>
    test <@ memo.GetOrAdd("b", factory) = "b!" @>
    test <@ memo.GetOrAdd("a", factory) = "a!" @>
    test <@ factoryRuns = 2 @>

/// Derive the target from the runner args exactly as `executeTests` does, then ask the
/// gate. A fresh `Cache` per call, since the memo is per-run in production.
let private staleOf (args: string) (repoRoot: string) : ArtifactFreshness.StaleInput option =
    deriveProjectBin args repoRoot
    |> Option.bind (ArtifactFreshness.stale (ArtifactFreshness.Cache()))

/// A synthetic repo mirroring the real MSBuild output layout:
///
///   Leaf/     — an unrelated project, referenced by nobody: out of closure.
///   Common/   — a library with a content fixture, referenced by Tests.
///   Tests/    — the test project, whose output dir holds COPIES of Common's DLL and of
///               Common's fixture.
///
/// Copies carry their ORIGIN's mtime, because that is what MSBuild's `File.Copy` leaves
/// behind — the property the gate's copy check rests on. Everything is "built" at
/// `builtAt` and sources are older; each test then moves ONE mtime.
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

    writeAt (p [ leafDir; "Leaf.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ leafDir; "Leaf.fs" ]) "module Leaf" sourcedAt

    writeAt (p [ commonDir; "Common.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ commonDir; "Common.fs" ]) "module Common" sourcedAt
    writeAt (p [ commonDir; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt
    writeAt (p [ commonOut; "Common.dll" ]) "" builtAt
    writeAt (p [ commonOut; "Fixtures"; "data.json" ]) "{ \"leaves\": 36 }" sourcedAt

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
        // Absence is tryApphostPresent's job, not staleness'.
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

[<Fact(Timeout = 15000)>]
let ``freshness is None when there are no sources to be stale against`` () =
    withTempDir "tp-stale-nosrc" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore
        File.WriteAllText(Path.Combine(tfmDir, "Unit.dll"), "")
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

// AUTOMATION-122, direction 1 — the false positive, verbatim: a build tool was edited and
// an integration suite that does not reference it was condemned as stale. MSBuild rightly
// refuses to relink that suite, so no plain build could ever clear the accusation. On a
// repo-wide watermark this test FAILS — Leaf.fs is the newest source in the repo and
// Tests.dll predates it.
[<Fact(Timeout = 15000)>]
let ``an edit OUTSIDE the test project's closure leaves it FRESH`` () =
    withTempDir "tp-stale-outside" (fun tmpDir ->
        let s = synth tmpDir

        // Leaf is referenced by nobody, so this is now the newest source in the repo and
        // still irrelevant to this test binary.
        File.SetLastWriteTimeUtc(s.LeafSrc, s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

/// AUTOMATION-528, direction A — the dangerous one, and the one no other case can see.
///
/// `dotnet restore` rewrites `obj/project.assets.json`; only a BUILD regenerates
/// `bin/<tfm>/<Asm>.deps.json` from it. When a restore moves on without a build — which
/// is exactly what the deps-freshness gate's automatic recovery does — the manifest left
/// behind lists a superseded reference closure. The compile is repaired and the LOAD is
/// not: the host resolves assemblies through the manifest, not through the directory, so
/// a dependency sitting in the output folder and missing from the manifest is a
/// `FileNotFoundException` on the routes that touch it. Green build, red run, specific
/// routes only — indistinguishable from an application bug.
///
/// Reproduced against a real SDK before this test was written: with the manifest of an
/// earlier build restored over a fully-built tree, `dotnet App.dll` died with
/// `Could not load file or assembly 'Lib'` while `Lib.dll` sat beside it in the output
/// folder, every assembly was newer than every source, and every copy was byte-identical
/// to its origin.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-528: a deps manifest older than its restore is STALE though every assembly is current`` () =
    withTempDir "tp-a528-superseded" (fun tmpDir ->
        let projDir = p [ tmpDir; "Unit" ]
        let tfmDir = p [ projDir; "bin"; "Debug"; "net10.0" ]
        let expectedAssets = p [ projDir; "obj"; "project.assets.json" ]
        let expectedManifest = p [ tfmDir; "Unit.deps.json" ]
        let now = DateTime.UtcNow

        // A fully-built, otherwise impeccable tree: the assembly postdates its source and
        // there is nothing copied in to differ from an origin.
        writeAt (p [ projDir; "Foo.fs" ]) "module Foo" (now.AddMinutes(-30.0))
        writeAt (p [ tfmDir; "Unit.dll" ]) "" (now.AddMinutes(-10.0))
        writeAt expectedManifest "{}" (now.AddMinutes(-10.0))
        // ... and a restore that moved on after that build, without one following it.
        writeAt expectedAssets "{}" now

        match staleOf $"run --project {projDir} --no-build --" tmpDir with
        | Some(ArtifactFreshness.DepsManifestOlderThanRestore(project, assets, manifest, _, _)) ->
            test <@ project = "Unit" @>
            test <@ assets = expectedAssets @>
            test <@ manifest = expectedManifest @>
        | other -> Assert.Fail($"expected DepsManifestOlderThanRestore, got %A{other}"))

/// POSITIVE CONTROL, required: the ordinary shape a build leaves — restore first, manifest
/// after it — must NOT report staleness, and neither must one written inside the same tick
/// (a coarse filesystem, or a build fast enough that both land on one timestamp). A
/// detector that fired on every built tree would refuse every run and teach people to
/// ignore it.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-528: a deps manifest at or after its restore is fresh`` () =
    withTempDir "tp-a528-control" (fun tmpDir ->
        let projDir = p [ tmpDir; "Unit" ]
        let tfmDir = p [ projDir; "bin"; "Debug"; "net10.0" ]
        let assets = p [ projDir; "obj"; "project.assets.json" ]
        let manifest = p [ tfmDir; "Unit.deps.json" ]
        let now = DateTime.UtcNow

        writeAt (p [ projDir; "Foo.fs" ]) "module Foo" (now.AddMinutes(-30.0))
        writeAt (p [ tfmDir; "Unit.dll" ]) "" (now.AddMinutes(-10.0))
        writeAt assets "{}" (now.AddMinutes(-10.0))

        // Generated after the restore it came from: the normal case.
        writeAt manifest "{}" (now.AddMinutes(-9.0))
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>

        // Same tick: still the normal case, never stale.
        writeAt manifest "{}" (now.AddMinutes(-10.0))
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

/// Either half of the pair missing means there is nothing to compare, and absence is never
/// staleness in this module. BOTH directions are pinned: a project with no restore output
/// (an old-style project, or one whose `obj/` was cleaned), and one that generates no
/// runtime manifest at all (an ordinary library).
///
/// Pinned because every OTHER freshness test in this file builds a tree with no `obj/`, so
/// a check that read a missing assets file as stale would redden all of them at once and
/// the cause would present as a mass regression rather than as this one decision.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-528: neither half of the pair missing is judged as stale`` () =
    withTempDir "tp-a528-absent" (fun tmpDir ->
        let projDir = p [ tmpDir; "Unit" ]
        let tfmDir = p [ projDir; "bin"; "Debug"; "net10.0" ]
        let assets = p [ projDir; "obj"; "project.assets.json" ]
        let manifest = p [ tfmDir; "Unit.deps.json" ]
        let now = DateTime.UtcNow

        writeAt (p [ projDir; "Foo.fs" ]) "module Foo" (now.AddMinutes(-30.0))
        writeAt (p [ tfmDir; "Unit.dll" ]) "" (now.AddMinutes(-10.0))

        // A manifest with no restore beside it.
        writeAt manifest "{}" (now.AddMinutes(-10.0))
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>

        // ... and a restore, newer than everything, with no manifest generated from it.
        File.Delete manifest
        writeAt assets "{}" now
        test <@ staleOf $"run --project {projDir} --no-build --" tmpDir = None @>)

// Direction 2 — the real hole. A dependency's source newer than the dependency's own
// assembly means the build has not run since the edit, so the DLL in the test project's
// output dir is old code and `--no-build` must not run.
[<Fact(Timeout = 15000)>]
let ``an edit to a DEPENDENCY inside the closure is STALE`` () =
    withTempDir "tp-stale-inside" (fun tmpDir ->
        let s = synth tmpDir

        File.SetLastWriteTimeUtc(s.CommonSrc, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.AssemblyOlderThanSource(project, source, _, _)) ->
            test <@ project = "Common" @>
            test <@ source = s.CommonSrc @>
        | other -> Assert.Fail($"expected the dependency edit to be STALE, got %A{other}"))

// The same edit once the build HAS run: this is what proves a plain `dotnet build`, not
// `-t:Rebuild`, clears the gate. The test project's own DLL is deliberately NOT restamped
// — a private-only change to a dependency need not relink its consumers (reference
// assemblies exist to avoid exactly that), and demanding it would be the same
// unanswerable accusation in a smaller costume.
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

// The dependency's DLL is fresh in its OWN bin, but the copy the test run would load was
// never refreshed. A rebuild emits new BYTES — moving the origin's mtime alone would
// assert the old `copy < origin` rule rather than the real event.
[<Fact(Timeout = 15000)>]
let ``a dependency DLL rebuilt but not re-copied into the test output is STALE`` () =
    withTempDir "tp-stale-depcopy" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(s.CommonDll, "rebuilt bits")
        File.SetLastWriteTimeUtc(s.CommonDll, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = s.CommonDll @>
            test <@ copy = s.CommonDllCopy @>
        | other -> Assert.Fail($"expected the un-refreshed dependency copy to be STALE, got %A{other}"))

// AUTOMATION-122, second half — CONTENT FILES, which let a red main through. A shared
// fixture changed (36 → 40 leaf facts); the consuming test project's output dir still
// held the OLD copy, so the `--no-build` run read the old fixture and PASSED. Only
// `-t:Rebuild` exposed it. A stale copy of a content item must make the run stale exactly
// as a stale apphost does.

[<Fact(Timeout = 15000)>]
let ``a FIXTURE edited but not re-copied into the test output is STALE`` () =
    withTempDir "tp-stale-fixture" (fun tmpDir ->
        let s = synth tmpDir

        // The fixture changes and the copy in the test project's output dir still holds
        // the OLD bytes. Every compiled artifact is untouched, so the apphost/DLL checks
        // alone see nothing wrong and the tests would run green against 36 leaves.
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = s.CommonFixture @>
            test <@ copy = s.FixtureCopy @>
        | other -> Assert.Fail($"expected the un-copied fixture to be STALE, got %A{other}"))

// What a plain `dotnet build` does: re-copy the fixture, carrying the origin's mtime
// (verified against real MSBuild, 2026-07-14).
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
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = ownFixture @>
            test <@ copy = ownCopy @>
        | other -> Assert.Fail($"expected the test project's own stale fixture to be STALE, got %A{other}"))

// SHADOWING. Two projects in one closure can hold a file at the SAME relative path —
// `xunit.runner.json` sits in five projects of the repo this gate was fixed against.
// MSBuild copies both to one destination, last writer wins, so exactly one survives.
// Judging the survivor against only ONE claimant condemns the other for being shadowed,
// and no build can answer that. A CONTENT comparison would make that accusation PERMANENT
// where an mtime one only fired when the shadowed file happened to be newer — so a copy
// is checked against every claimant and is current if it matches ANY of them.
[<Fact(Timeout = 15000)>]
let ``a fixture SHADOWED by another project's file at the same path is not stale`` () =
    withTempDir "tp-stale-shadowed" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)

        // Tests has its OWN Fixtures/data.json — same relative path as Common's, different
        // bytes — and its copy is the one that survives in the output dir. Common's
        // fixture is now shadowed: its bytes appear nowhere in the output, and a build
        // would change nothing.
        let testsFixture = p [ s.TestsDir; "Fixtures"; "data.json" ]
        writeAt testsFixture "{ \"leaves\": 99 }" editedAt
        writeAt s.FixtureCopy "{ \"leaves\": 99 }" editedAt

        test <@ synthStale s = None @>

        // Shadowing is not a licence to stop checking.
        File.WriteAllText(s.FixtureCopy, "{ \"leaves\": 0 }")

        match synthStale s with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(_, copy)) -> test <@ copy = s.FixtureCopy @>
        | other -> Assert.Fail($"a copy matching no claimant must still be STALE, got %A{other}"))

// The other half of "keyed on the copy": a file the build does NOT copy has no
// destination in the output dir, so editing it can never fire the gate. Otherwise the
// content check becomes a new wolf-cry, every README and .fsproj edit condemning a
// project no build would ever clear.
[<Fact(Timeout = 15000)>]
let ``a file the build never copies cannot make the run stale`` () =
    withTempDir "tp-stale-uncopied" (fun tmpDir ->
        let s = synth tmpDir
        // Newer than everything, copied nowhere.
        writeAt (Path.Combine(tmpDir, "Common", "README.md")) "# notes" (s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

// A guard that cries "something, somewhere is stale" is a guard people learn to bypass.
[<Fact(Timeout = 15000)>]
let ``the stale reason names the offending file`` () =
    withTempDir "tp-stale-describe" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))

        match synthStale s with
        | Some stale ->
            let described = ArtifactFreshness.describe stale
            test <@ described.Contains "data.json" @>
        | None -> Assert.Fail "expected a stale verdict")

// =============================================================================
// FAIL CLOSED. A gate that answers "up to date" because it COULD NOT LOOK is the original
// bug reborn inside its own fix. If the closure cannot be determined — an unparseable
// project file, a `ProjectReference` resolving to nothing — the run is REFUSED and the
// build, which will choke on the same file loudly, reports the real error. Swallowing
// these into "no references" would shrink the closure to nothing and let a stale
// dependency sail through as fresh.
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

// The same ignorance: we cannot know a missing project's sources, so we cannot certify
// this run.
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

// Ignorance ANYWHERE in the closure fails closed, not just at its root.
[<Fact(Timeout = 15000)>]
let ``an unparseable project file DEEP in the closure is REFUSED`` () =
    withTempDir "tp-stale-badxml-deep" (fun tmpDir ->
        let s = synth tmpDir
        File.WriteAllText(p [ tmpDir; "Common"; "Common.fsproj" ], "<Project> <<< not xml")

        match synthStale s with
        | Some(ArtifactFreshness.InputsUndeterminable _) -> ()
        | other -> Assert.Fail($"an unreadable DEPENDENCY project file must fail CLOSED, got %A{other}"))

// AUTOMATION-164. The closure-parse channel was fail-closed from the start; the WALK
// channel was not. `SafeWalk` returned `[||]` for a directory it could not enumerate,
// so an unreadable source dir produced no sources, nothing was newer than the assembly,
// and the gate certified a `--no-build` run over bits it had never looked at. The walk
// now reports its holes and the gate refuses on them.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE SOURCE DIRECTORY in the closure is REFUSED, not called fresh`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-stale-unreadable-src" (fun tmpDir ->
            let s = synth tmpDir

            // POSITIVE CONTROL: this exact tree is FRESH while it is readable. Without
            // it, a gate that answered InputsUndeterminable for everything would pass.
            test <@ synthStale s = None @>

            let sealed' = p [ tmpDir; "Common"; "Internal" ]
            Directory.CreateDirectory sealed' |> ignore
            File.WriteAllText(p [ sealed'; "Deep.fs" ], "module Deep")
            File.SetUnixFileMode(sealed', UnixFileMode.None)

            try
                match synthStale s with
                | Some(ArtifactFreshness.InputsUndeterminable(project, reason)) ->
                    test <@ project = "Common" @>
                    test <@ reason.Contains "Internal" @>
                | other -> Assert.Fail($"an unreadable source directory must fail CLOSED, got %A{other}")
            finally
                File.SetUnixFileMode(
                    sealed',
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// The same hole on the OTHER side of the gate. `outputs` is read as "did the build put
// something here?", and a copy the walk never saw is a destination nobody checks — so a
// stale copy under an unreadable output subdirectory used to sail through as fresh.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE OUTPUT DIRECTORY is REFUSED, not called fresh`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-stale-unreadable-out" (fun tmpDir ->
            let s = synth tmpDir
            test <@ synthStale s = None @>

            let sealed' = p [ s.TestsOutDir; "runtimes" ]
            Directory.CreateDirectory sealed' |> ignore
            File.WriteAllText(p [ sealed'; "native.dylib" ], "")
            File.SetUnixFileMode(sealed', UnixFileMode.None)

            try
                match synthStale s with
                | Some(ArtifactFreshness.InputsUndeterminable(project, reason)) ->
                    test <@ project = "Tests" @>
                    test <@ reason.Contains "runtimes" @>
                | other -> Assert.Fail($"an unreadable output directory must fail CLOSED, got %A{other}")
            finally
                File.SetUnixFileMode(
                    sealed',
                    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ))

// A reference cycle is MSBuild's error to report; the closure walk must terminate rather
// than hang on it.
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

// A dependency never built at all is the build's business, not staleness': there is no
// out-of-date artifact to refuse, and a build in flight may still land it. Same for a
// dependency assembly not yet copied into the test output.
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

// =============================================================================
// AUTOMATION-169 — the same wolf-cry through a different door: not the wrong project this
// time, the wrong TARGET FRAMEWORK.
//
// A multi-targeted dependency (netstandard2.0/net8.0/net9.0/net10.0) consumed by a
// net10.0 test project. MSBuild copies the net10.0 output, but the gate resolved the
// ORIGIN to whichever TFM output was NEWEST — net8.0, built nine minutes later — so it
// compared a net10.0 copy against a net8.0 origin, found it "older", and condemned it.
// Every consumer's copy was byte-identical to net10.0's digest, and a plain `dotnet build`
// could not answer the accusation because a correct rebuild re-copies net10.0 and it
// re-fires. 4 of 6 test projects refused to run.
//
// An mtime comparison ACROSS TFMs is meaningless by construction; these tests pin that it
// is no longer expressible.
// =============================================================================

/// A dependency multi-targeting net8.0 and net10.0, consumed by a net10.0 test project.
/// The two TFM outputs carry DIFFERENT BYTES (real per-TFM builds differ at minimum in
/// `TargetFrameworkAttribute`) and DIFFERENT MTIMES, with net8.0 the NEWER: exactly the
/// shape that makes "newest output dir wins" pick the framework nobody consumes.
type private MultiTfm =
    { Root: string
      TestsDir: string
      DepNet8Dll: string
      DepNet10Dll: string
      DepDllCopy: string
      BuiltAt: DateTime }

let private multiTfmSynth (root: string) : MultiTfm =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)

    let depDir = p [ root; "Dep" ]
    let testsDir = p [ root; "Tests" ]
    let depNet8 = p [ depDir; "bin"; "Debug"; "net8.0" ]
    let depNet10 = p [ depDir; "bin"; "Debug"; "net10.0" ]
    let testsOut = p [ testsDir; "bin"; "Debug"; "net10.0" ]

    writeAt (p [ depDir; "Dep.fsproj" ]) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (p [ depDir; "Dep.fs" ]) "module Dep" sourcedAt

    // Different bytes, and net8.0 built nine minutes later than net10.0 — so net8.0 is the
    // newest, and a "newest wins" gate resolves the origin to it.
    writeAt (p [ depNet10; "Dep.dll" ]) "net10.0 bits" builtAt
    writeAt (p [ depNet8; "Dep.dll" ]) "net8.0 bits" (builtAt.AddMinutes(9.0))

    writeAt
        (p [ testsDir; "Tests.fsproj" ])
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Dep/Dep.fsproj\" />\n  </ItemGroup>\n</Project>"
        sourcedAt

    writeAt (p [ testsDir; "Tests.fs" ]) "module Tests" sourcedAt
    writeAt (p [ testsOut; "Tests" ]) "" builtAt // apphost
    writeAt (p [ testsOut; "Tests.dll" ]) "" builtAt
    // The copy: MSBuild took the net10.0 output — the TFM this consumer targets —
    // preserving its mtime. It is perfect and current.
    writeAt (p [ testsOut; "Dep.dll" ]) "net10.0 bits" builtAt

    { Root = root
      TestsDir = testsDir
      DepNet8Dll = p [ depNet8; "Dep.dll" ]
      DepNet10Dll = p [ depNet10; "Dep.dll" ]
      DepDllCopy = p [ testsOut; "Dep.dll" ]
      BuiltAt = builtAt }

let private multiTfmStale (m: MultiTfm) =
    let fsproj = Path.Combine(m.TestsDir, "Tests.fsproj")
    staleOf $"run --project {fsproj} --no-build --" m.Root

// Nothing is stale: the copy is byte-identical to the net10.0 output it came from. A
// newest-mtime gate calls it nine minutes "older" than net8.0 and cries stale.
[<Fact(Timeout = 15000)>]
let ``a copy of a multi-TFM dependency is FRESH even when a SIBLING TFM is newer`` () =
    withTempDir "tp-stale-tfm-sibling" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        test <@ multiTfmStale m = None @>)

// The converse, and why this is not a weakening: a copy whose bytes match NO current
// output of its origin is still caught. Its MTIME here EQUALS its origin's, so the
// `copy < origin` rule calls it fresh and runs the stale bits — this is the jj/git
// working-copy restamp, the coarse-timestamp filesystem, and "rebuilt within the same
// second", all at once.
[<Fact(Timeout = 15000)>]
let ``a copy whose bytes match NO output of its origin is STALE, even at an equal mtime`` () =
    withTempDir "tp-stale-tfm-realstale" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        // Both TFM outputs carry new bytes; the copy was never refreshed and holds the old
        // bits. Every mtime is identical, so no mtime comparison can see it.
        File.WriteAllText(m.DepNet10Dll, "net10.0 bits v2")
        File.WriteAllText(m.DepNet8Dll, "net8.0 bits v2")
        File.SetLastWriteTimeUtc(m.DepNet10Dll, m.BuiltAt)
        File.SetLastWriteTimeUtc(m.DepNet8Dll, m.BuiltAt)
        File.SetLastWriteTimeUtc(m.DepDllCopy, m.BuiltAt)

        match multiTfmStale m with
        | Some(ArtifactFreshness.CopyDiffersFromOrigin(origin, copy)) ->
            test <@ origin = m.DepNet10Dll @> // named for the TFM the consumer consumes
            test <@ copy = m.DepDllCopy @>
        | other -> Assert.Fail($"a copy holding OLD bytes must be STALE whatever the mtimes say, got %A{other}"))

// Fail closed at the copy check too: a file we cannot read is not one we may certify, so
// `ContentHash` hands back `UnhashableContent` and that is `InputsUndeterminable`, never
// "fresh". An exclusive lock is how a real build in flight holds a file mid-write.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE copy is REFUSED, not called fresh`` () =
    withTempDir "tp-stale-unreadable-copy" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        use _lock =
            new FileStream(m.DepDllCopy, FileMode.Open, FileAccess.Read, FileShare.None)

        match multiTfmStale m with
        | Some(ArtifactFreshness.InputsUndeterminable _) -> ()
        | other -> Assert.Fail($"an unreadable copy must fail CLOSED, got %A{other}"))

// The subtler side. The copy reads fine but an ORIGIN it must be checked against does
// not, so "it matches none of them" is a conclusion we did not earn: an unhashable origin
// cannot match anything, and calling that a MISMATCH manufactures a stale verdict out of
// a permissions error.
[<Fact(Timeout = 15000)>]
let ``an UNREADABLE origin is REFUSED, not called stale`` () =
    withTempDir "tp-stale-unreadable-origin" (fun tmpDir ->
        let m = multiTfmSynth tmpDir

        // No readable candidate matches: net8.0 holds different bytes by construction, and
        // net10.0 — the one it does match — cannot be read.
        use _lock =
            new FileStream(m.DepNet10Dll, FileMode.Open, FileAccess.Read, FileShare.None)

        match multiTfmStale m with
        | Some(ArtifactFreshness.InputsUndeterminable(_, reason)) -> test <@ reason.Contains "net10.0" @>
        | other -> Assert.Fail($"an unreadable ORIGIN must fail CLOSED, not read as stale, got %A{other}"))

// A multi-targeted project is stale only when EVERY per-TFM output dir is: which TFM
// `dotnet run` selects is not knowable here, so one fresh output dir means there is a
// fresh way to run.
[<Fact(Timeout = 15000)>]
let ``a multi-TFM project with one FRESH output dir is not stale`` () =
    withTempDir "tp-stale-multitfm" (fun tmpDir ->
        let s = synth tmpDir
        let editedAt = s.BuiltAt.AddMinutes(30.0)

        // net10.0's copy still holds the OLD bytes, so it is stale ...
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, editedAt)
        test <@ (synthStale s).IsSome @>

        // ... but a second TFM's output dir carries the up-to-date copy.
        let net9 = p [ s.TestsDir; "bin"; "Debug"; "net9.0" ]
        writeAt (p [ net9; "Tests.dll" ]) "" s.BuiltAt
        writeAt (p [ net9; "Common.dll" ]) "" s.BuiltAt
        writeAt (p [ net9; "Fixtures"; "data.json" ]) "{ \"leaves\": 40 }" editedAt

        // A dependency TFM dir holding no assembly — only one of its frameworks was built
        // — is not a candidate, and must not be mistaken for a missing build.
        Directory.CreateDirectory(p [ tmpDir; "Common"; "bin"; "Debug"; "net9.0" ])
        |> ignore

        test <@ synthStale s = None @>)

// A partial or interrupted build leaves behind a TFM output dir of the TEST PROJECT with
// no assembly in it. Judging it would mean walking an empty output tree, finding no copy
// of anything, and — since every project in the closure then contributes no finding —
// quietly calling the run fresh on the strength of a directory containing nothing.
[<Fact(Timeout = 15000)>]
let ``a TFM dir of the test project holding no assembly is not a candidate`` () =
    withTempDir "tp-stale-empty-tfmdir" (fun tmpDir ->
        let s = synth tmpDir

        Directory.CreateDirectory(p [ s.TestsDir; "bin"; "Debug"; "net9.0" ]) |> ignore

        test <@ synthStale s = None @>

        // The real output dir still decides: break it and the gate must fire rather than
        // be placated by the empty sibling.
        File.WriteAllText(s.CommonFixture, "{ \"leaves\": 40 }")
        File.SetLastWriteTimeUtc(s.CommonFixture, s.BuiltAt.AddMinutes(30.0))
        test <@ (synthStale s).IsSome @>)

// The freshness walk must TERMINATE through symlink cycles. Production trigger:
// `.devenv/profile` links into /nix/store, where ncurses-6.6-dev/include holds two
// self-loop symlinks (`ncurses -> .`, `ncursesw -> .`) — branching factor 2 per level,
// bounded only by ENAMETOOLONG, so ~2^90 paths. A symlink-following walk wedged every
// `fshw check` forever (observed 8h36m, silent) and trips this test's Timeout.
[<Fact(Timeout = 15000)>]
let ``freshness terminates despite self-loop symlink cycles`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "tp-nsm-cycle" (fun tmpDir ->
            let s = synth tmpDir

            // Two self-loops in one directory: the /nix/store shape exactly.
            let cycleDir = Path.Combine(s.TestsDir, "cycle")
            Directory.CreateDirectory cycleDir |> ignore
            Directory.CreateSymbolicLink(Path.Combine(cycleDir, "loop"), ".") |> ignore
            Directory.CreateSymbolicLink(Path.Combine(cycleDir, "loop2"), ".") |> ignore

            test <@ synthStale s = None @>)

// The same wedge, other half: a symlinked directory is a portal OUT of the tree
// (`.devenv/profile` → the nix store). Freshness is computed from the REAL tree only — a
// newer file behind a symlinked dir is not an input to this project, and following it is
// how the walk left the repo in the first place.
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

// `.devenv`/`.direnv` are excluded by NAME as well: even a regular, non-symlinked file
// under them must not count as an input.
[<Fact(Timeout = 15000)>]
let ``freshness ignores sources under .devenv and .direnv`` () =
    withTempDir "tp-nsm-devenv" (fun tmpDir ->
        let s = synth tmpDir
        writeAt (Path.Combine(s.TestsDir, ".devenv", "gen", "Tool.fs")) "module Tool" (s.BuiltAt.AddMinutes(30.0))

        test <@ synthStale s = None @>)

// The gate end-to-end through the plugin. Without it the runner exits 0, yielding
// TestsPassed and a false green on stale bits.
[<Fact(Timeout = 20000)>]
let ``a present-but-stale apphost defers as 'waiting on build' instead of passing on stale bits`` () =
    withTempDir "tp-stale-defer" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "Unit")
        let tfmDir = Path.Combine(projDir, "bin", "Debug", "net10.0")
        Directory.CreateDirectory(tfmDir) |> ignore

        // Apphost and canonical DLL present, so the missing-apphost path does NOT fire ...
        File.WriteAllText(Path.Combine(tfmDir, "Unit"), "")
        let dll = Path.Combine(tfmDir, "Unit.dll")
        File.WriteAllText(dll, "")

        // ... but a source was edited after the build.
        let src = Path.Combine(projDir, "Tests.fs")
        File.WriteAllText(src, "module Tests")
        let now = DateTime.UtcNow
        File.SetLastWriteTimeUtc(dll, now.AddMinutes(-10.0))
        File.SetLastWriteTimeUtc(src, now)

        // The runner exits 0 — a "pass" on stale bits — if it is ever launched. `--project`
        // makes the project derivable, so the gate engages pre-launch.
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

        let allEntries =
            host.GetErrorsByPlugin("test-prune") |> Map.toList |> List.collect snd

        // A defer is `Deferred` severity, which the verdict routes to Incomplete/exit 2 —
        // not a failing `Error` — so it does not register as a failing reason.
        test <@ not (host.HasFailingReasons(warningsAreFailures = true)) @>

        let waitingDiagnostic =
            allEntries
            |> List.exists (fun e ->
                e.Severity = FsHotWatch.ErrorLedger.Deferred
                && e.Message.ToLowerInvariant().Contains("waiting on build"))

        test <@ waitingDiagnostic @>

        // A non-failing terminal whose summary still says "waiting on build" — never
        // `Failed`, never a silent green.
        match host.GetStatus("test-prune") with
        | Some(Completed(_, v)) -> test <@ v.Summary.ToLowerInvariant().Contains("waiting on build") @>
        | other -> Assert.Fail($"expected a non-failing Completed status for the stale-artifact defer, got %A{other}")

        test <@ allEntries |> List.forall (fun e -> e.Severity <> FsHotWatch.ErrorLedger.Error) @>

        test
            <@
                allEntries
                |> List.forall (fun e -> not (e.Message.ToLowerInvariant().Contains("tests failed")))
            @>)

[<Fact(Timeout = 20000)>]
let ``stale failures from a prior cycle are cleared when the next cycle supersedes them`` () =
    // Cycle 1 reds ProjA; cycle 2 passes ProjA and reds ProjB, so only ProjB may remain.
    // The Custom(TestsFinished) handler used to clear only on the all-pass branch, so
    // `fshw errors` showed a stale red the fresh cycle had already disproved.
    //
    // Driven via `run-tests` rather than BuildCompleted so each cycle deterministically
    // re-runs: the impact path would skip a warm cycle with no changed symbols.
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

        // `run-tests` runs executeTests synchronously then posts TestsFinished, so the
        // ledger lags the call — wait for it.
        File.WriteAllText(flagA, "")
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        waitUntil (hasFileFor "ProjA") 12000

        let cycle1Files = ledgerFiles ()
        test <@ cycle1Files |> List.exists (fun f -> f.Contains("ProjA")) @>
        test <@ not (cycle1Files |> List.exists (fun f -> f.Contains("ProjB"))) @>

        // The ProjB red only appears after this cycle's clear-then-report has run.
        File.Delete(flagA)
        File.WriteAllText(flagB, "")
        host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously |> ignore
        waitUntil (hasFileFor "ProjB") 12000

        let cycle2Files = ledgerFiles ()

        test <@ cycle2Files |> List.exists (fun f -> f.Contains("ProjB")) @>
        test <@ not (cycle2Files |> List.exists (fun f -> f.Contains("ProjA"))) @>)

// =============================================================================
// PendingVerification sidecar — deterministic unit tests for load/save/hash. These pin
// both sides of every branch in `load` (missing file, whitespace-only, corrupt JSON,
// well-formed) so branch coverage is stable run-to-run rather than depending on which
// states the end-to-end queue tests happen to leave the sidecar in.
// =============================================================================

module private LedgerHelpers =
    open FsHotWatch.TestPrune

    /// Write raw bytes to the sidecar — the only way to produce the torn/corrupt shapes
    /// `save` itself can never write.
    let writeRawSidecar (tmpDir: string) (contents: string) =
        let path = PendingVerification.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, contents)

    /// The queue, or a test failure naming the reason it could not be read.
    let expectLoaded (tmpDir: string) : Set<string> =
        match PendingVerification.load tmpDir with
        | PendingVerification.LoadedQueue.Loaded queue -> queue
        | PendingVerification.LoadedQueue.Unreadable reason -> failwith $"expected Loaded, got Unreadable: {reason}"

    /// Assert the ledger is UNREADABLE — the fact that must never be spelled with the same
    /// value as an empty queue.
    let expectUnreadable (tmpDir: string) : string =
        match PendingVerification.load tmpDir with
        | PendingVerification.LoadedQueue.Unreadable reason -> reason
        | PendingVerification.LoadedQueue.Loaded queue ->
            failwith
                $"expected Unreadable, got Loaded %A{queue} — an unreadable ledger read as an empty queue is AUTOMATION-150 itself"

[<Fact(Timeout = 15000)>]
let ``PendingVerification: load on a MISSING file is Loaded empty, never Unreadable`` () =
    withTempDir "pv-missing" (fun tmpDir ->
        // The fresh-clone boundary: nothing was ever queued, so nothing is owed — a
        // PROVABLE empty. `Unreadable` here would wedge every fresh clone into a
        // permanent full suite.
        test <@ Set.isEmpty (LedgerHelpers.expectLoaded tmpDir) @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: save then load round-trips the queue`` () =
    withTempDir "pv-roundtrip" (fun tmpDir ->
        let original = Set.ofList [ "Lib.foo"; "Lib.bar"; "Mod.baz" ]
        FsHotWatch.TestPrune.PendingVerification.save tmpDir original
        test <@ LedgerHelpers.expectLoaded tmpDir = original @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: save empty then load is Loaded empty (a provable 'nothing owed')`` () =
    withTempDir "pv-empty" (fun tmpDir ->
        // `save` of an empty queue writes `[]`: well-formed, readable, and provably
        // "nothing is owed".
        FsHotWatch.TestPrune.PendingVerification.save tmpDir Set.empty
        test <@ Set.isEmpty (LedgerHelpers.expectLoaded tmpDir) @>)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: an EMPTY file is Unreadable (a torn write, not an empty queue)`` () =
    withTempDir "pv-whitespace" (fun tmpDir ->
        // `save` writes through an atomic tmp+rename and always emits at least `[]`, so a
        // zero-byte/whitespace file is a TORN WRITE — and reading it as `empty` absorbs
        // whatever the ledger held.
        LedgerHelpers.writeRawSidecar tmpDir "   \n  "
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: corrupt JSON is Unreadable, not empty (and never throws)`` () =
    withTempDir "pv-corrupt" (fun tmpDir ->
        LedgerHelpers.writeRawSidecar tmpDir "{ this is not valid json [[["
        // Must not throw — but the failure is REPORTED, not swallowed into an empty queue
        // a caller would read as "nothing owed".
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a TRUNCATED array is Unreadable, not empty`` () =
    withTempDir "pv-truncated" (fun tmpDir ->
        // The crash-mid-write shape: valid JSON right up to where it stops.
        LedgerHelpers.writeRawSidecar tmpDir "[\"Lib.foo\", \"Lib.ba"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: well-formed JSON that is not an array is Unreadable`` () =
    withTempDir "pv-not-array" (fun tmpDir ->
        // Parses cleanly, but it is not a queue: `AsArray` throws, and catching that to
        // return `empty` is the bug.
        LedgerHelpers.writeRawSidecar tmpDir "{\"pending\": [\"Lib.foo\"]}"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a bare JSON null is Unreadable`` () =
    withTempDir "pv-null" (fun tmpDir ->
        LedgerHelpers.writeRawSidecar tmpDir "null"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a NON-STRING entry makes the whole ledger Unreadable`` () =
    withTempDir "pv-bad-entry" (fun tmpDir ->
        // A `Seq.choose` here silently DROPS the entry it cannot read, absorbing that
        // symbol's debt while the rest of the queue looks healthy. A symbol we cannot name
        // is a symbol we cannot verify.
        LedgerHelpers.writeRawSidecar tmpDir "[\"Lib.foo\", 42, \"Lib.bar\"]"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: a null entry makes the whole ledger Unreadable`` () =
    withTempDir "pv-null-entry" (fun tmpDir ->
        LedgerHelpers.writeRawSidecar tmpDir "[\"Lib.foo\", null]"
        LedgerHelpers.expectUnreadable tmpDir |> ignore)

[<Fact(Timeout = 15000)>]
let ``PendingVerification: hash is order-independent and empty-distinct`` () =
    let pv = FsHotWatch.TestPrune.PendingVerification.hash
    test <@ pv (Set.ofList [ "a"; "b"; "c" ]) = pv (Set.ofList [ "c"; "a"; "b" ]) @>
    test <@ pv (Set.ofList [ "a" ]) <> pv FsHotWatch.TestPrune.PendingVerification.empty @>

// =============================================================================
// The pending-verification queue: a changed symbol leaves it ONLY when a test run that
// covered it completed green, so that "0 affected tests" provably means "test-equivalent
// to the last green run". Three holes it closes:
//   1. The verdict ignored run outcome, so an Aborted run false-greened.
//   2. The queue drained unconditionally, so Aborted/failed runs forgot what still
//      needed testing.
//   3. Without a durable queue, a restart absorbed unverified symbols.
// These drive the real BuildCompleted → run → TestsFinished flow, seeding the symbol DB
// directly (deterministic, no FCS) and asserting against the on-disk
// `.fshw/test-prune/pending-verification.json`.
// =============================================================================

module private PendingQueueHelpers =
    open FsHotWatch.TestPrune

    /// Seed the symbol DB so `QueryAffectedTests [symbolFullName]` returns a test in
    /// `testProject`, mirroring the dependency-edge + TestMethodInfo shape the analyzer
    /// produces.
    let seedCoveredSymbol
        (db: Database)
        (symbolFullName: string)
        (sourceFile: string)
        (testProject: string)
        (testClass: string)
        (testMethod: string)
        =
        let symbol: SymbolInfo =
            { FullName = symbolFullName
              Kind = SymbolKind.Value
              SourceFile = sourceFile
              LineStart = 1
              LineEnd = 1
              ContentHash = "seed-hash"
              IsExtern = false }

        // The test method is ALSO a symbol: `dependencies.from_symbol_id` and
        // `test_methods.symbol_id` are NOT-NULL FKs into symbols(id), so without a row for
        // the test's own full name the edge and test-method are silently dropped and
        // QueryAffectedTests returns nothing.
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
        // The plugin opens its OWN Database.create(dbPath) connection, which a pooled
        // stale snapshot can hide these writes from. Clear the pool so its first read sees
        // the seed.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

    /// A test config whose runner exits 1 iff `flag` exists, and 0 otherwise.
    let flagConfig (tmpDir: string) (project: string) (flag: string) : TestConfig =
        { Project = project
          Command = "sh"
          Args = $"-c \"if [ -f {flag} ]; then exit 1; else exit 0; fi\""
          Group = "default"
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = None
          ReportVerificationFormat = AutoDetect }

    /// The durable pending-verification queue for a repo root. An UNREADABLE ledger is a
    /// test failure, not an empty queue: these tests assert on what is owed, and reading
    /// an unreadable ledger as `empty` is the bug AUTOMATION-150 closes. The tests that
    /// WANT the unreadable case match on `LoadedQueue` themselves.
    let loadQueue (tmpDir: string) : Set<string> =
        match PendingVerification.load tmpDir with
        | PendingVerification.LoadedQueue.Loaded queue -> queue
        | PendingVerification.LoadedQueue.Unreadable reason ->
            failwith $"expected a readable pending-verification ledger, got Unreadable: {reason}"

[<Fact(Timeout = 20000)>]
[<Trait("Regression", "LifecycleMailboxOrder")>]
let ``incident: a beforeRun throw aborts the run, is NOT green, and re-flags the symbols`` () =
    // A beforeRun throw propagates out of executeTests, `runTestsWithImpact` catches it,
    // and the completion carries Outcome = Aborted with Results = Map.empty. Empty results
    // trivially satisfy "failed = 0 && deferred = 0", so the verdict greened AND the queue
    // drained, permanently absorbing the symbol.
    withTempDir "tp-incident-abort" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // Set directly; a restart that loaded this queue is the realistic source.
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        let configs =
            [ PendingQueueHelpers.flagConfig tmpDir "P1" (Path.Combine(tmpDir, "never")) ]

        let beforeRun = Some(fun _ -> failwith "beforeRun boom")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let mutable startedId: Guid option = None
        let mutable completedId: Guid option = None

        let lifecycleRecorder: PluginHandler<unit, unit> =
            { Name = PluginName.create "abort-lifecycle-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | TestRunStarted started -> startedId <- Some started.RunId
                        | TestRunCompleted completed ->
                            // Keep the recorder behind the producer long enough to prove
                            // that observing TestPrune's terminal status does not imply a
                            // separate plugin mailbox has consumed the lifecycle event.
                            do! Async.Sleep 250
                            completedId <- Some completed.RunId
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.ofList [ SubscribeTestRunStarted; SubscribeTestRunCompleted ]
              CacheKey = None
              Teardown = None }

        host.RegisterHandler(lifecycleRecorder)
        let handler = create dbPath tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        test <@ await.Wait(TimeSpan.FromSeconds 15.0) @>

        // TestPrune publishes its terminal status from its own mailbox after queuing the
        // lifecycle event. The recorder consumes that event on another mailbox, so wait
        // for both mailboxes to drain before reading recorder-owned state.
        waitForQuiescent host 10000

        // An aborted run verified nothing.
        match host.GetStatus("test-prune") with
        | Some(Completed _) -> Assert.Fail("aborted run was reported as Completed (false green)")
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"expected Failed for an aborted run, got %A{other}")

        // Still queued, so a subsequent run re-flags it.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ queue.Contains("Lib.foo") @>

        // The abort closes the exact lifecycle opened before beforeRun. Consumers such
        // as Build use this identity to release their active-host deferral gate.
        test <@ startedId.IsSome @>
        test <@ completedId = startedId @>)

[<Fact(Timeout = 15000)>]
let ``incident: a beforeRun throw in the run-tests command surfaces as Failed, not a swallowed error`` () =
    // The `run-tests` command ran `executeTests` inside a try/with that, on a `beforeRun`
    // throw, returned a command-level JSON error and posted NOTHING back — leaving the
    // status at its prior, possibly green, value. A concurrent `fshw check` read the
    // daemon aggregate, saw no Failed status, and exited 0 while the preflight-guarded
    // suite never ran. The impact path's catch builds an Aborted lifecycle; the command
    // path must post the same one.
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

        // A preflight failure, modelling a real csrf-gate step.
        let beforeRun = Some(fun _ -> failwith "csrf-gate failed")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None beforeRun None None []
        host.RegisterHandler(handler)

        // The command posts the Aborted TestsFinished asynchronously, so await the
        // terminal transition it drives before reading status.
        let await = beginAwaitNextTerminal host "test-prune"
        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        // The direct caller still hears about it ...
        test <@ result.IsSome @>
        test <@ result.Value.Contains("csrf-gate failed") @>

        // ... and so does the seam `fshw check` reads: `anyPluginFailed`
        // (IpcOutput.hasFailures) keys off exactly this status, so a non-zero verdict
        // follows rather than a stale green.
        match host.GetStatus("test-prune") with
        | Some(Failed(msg, _, _)) -> test <@ msg.Contains("csrf-gate failed") @>
        | other -> Assert.Fail($"expected Failed with the hook output surfaced, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``incident: a test child that never becomes a live process drives the run to Failed, not a wedge`` () =
    // The launch gap: between a config's spawn and its first sign of life nothing watched
    // the wait, so an overloaded box left an infinite `WaitForExit` hanging with no child
    // ever appearing — the plugin stayed Running and `check` streamed "Waiting for
    // plugins" for hours. `sleep 30` reproduces it: no output, no exit, so with a tiny
    // a handler-scoped launch deadline the watchdog kills the tree and raises
    // `LaunchStalledException`, which the command's catch turns into the same Aborted
    // lifecycle a beforeRun throw does.
    //
    // The deadline is injected into THIS handler. A process-global env mutation here used
    // to retroactively shorten already-created handlers' deadlines while the full suite
    // ran in parallel, killing unrelated silent children on Linux and cascading into four
    // false state-machine failures.
    withTempDir "tp-launch-gap-stall" (fun tmpDir ->
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

        let handler =
            createWithLaunchDeadline (TimeSpan.FromSeconds 1.0) ":memory:" tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
        await.Wait(TimeSpan.FromSeconds 10.0) |> ignore

        test <@ result.IsSome @>
        test <@ result.Value.Contains("no live process") @>

        // Failed, naming the config and the launch gap — not a stale green, and not a
        // plugin stuck Running.
        match host.GetStatus("test-prune") with
        | Some(Failed(msg, _, _)) ->
            test <@ msg.Contains("no live process") @>
            test <@ msg.Contains("TestProject") @>
        | other -> Assert.Fail($"expected Failed for a launch-stalled run, got %A{other}"))

[<Fact(Timeout = 15000)>]
let ``run-tests command with a passing beforeRun runs normally and reports Completed`` () =
    // The pass-path pair for the failing-beforeRun regression above.
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

        let beforeRun = Some(fun _ -> ran.Value <- true)

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
    // SymA's tests live only in P1 (passes), SymB's only in P2 (fails). The whole queue
    // used to drain on any completion, regardless of per-project outcome.
    withTempDir "tp-partial-fail" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.symA" "A.fs" "P1" "P1Tests" "aTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.symB" "B.fs" "P2" "P2Tests" "bTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.symA"; "Lib.symB" ])

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

        test <@ not (queue.Contains("Lib.symA")) @>
        test <@ queue.Contains("Lib.symB") @>)

[<Fact(Timeout = 30000)>]
let ``mid-run change: a green run commits only its launch set; a symbol that arrives mid-run stays queued and triggers a rerun``
    ()
    =
    // Run 1 launches against {Lib.foo} and sleeps ~1.5s. Mid-flight a real FCS FileChecked
    // changes `bar`, which the plugin enqueues through the genuine write-through path, and
    // a BuildCompleted sets PendingRerun. Run 1's launch SNAPSHOT was {Lib.foo}, so its
    // green completion commits only that; `bar` survives and the rerun covers it. No
    // file-rewrite simulation: the snapshot is captured at dispatch and the commit is
    // launch-set-scoped.
    withTempDir "tp-midrun" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")

        // One test per exported function, so a change to either selects its own test.
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

        // The sleep is the window the mid-run injection needs.
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

        emitBuildAndWaitTerminal host

        for f in [ libFile; testsFile ] do
            match pipeline.CheckFile(AbsFilePath.create f) |> Async.RunSynchronously with
            | Some r -> host.EmitFileChecked(r)
            | None -> failwith $"CheckFile failed for {f}"

        waitForPluginIdle host "test-prune" 10.0
        emitBatchAndQuiesce host [ libFile; testsFile ]

        // Change `foo`'s body, so `fooTest` is the affected test.
        let libSource2 = "module Lib\nlet foo (x: int) = x + 2\nlet bar (x: int) = x + 1\n"
        File.WriteAllText(libFile, libSource2)

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile (foo change) failed"

        waitForPluginIdle host "test-prune" 10.0

        // Run 1 covers fooTest, and sleeps 1.5s.
        host.EmitBuildCompleted(BuildSucceeded)

        waitUntil
            (fun () ->
                match host.GetStatus("test-prune") with
                | Some(Running _) -> true
                | _ -> false)
            5000

        // Mid-run, while run 1 is still sleeping: change `bar`'s body, so a real
        // FileChecked enqueues it, then a BuildCompleted sets PendingRerun.
        let libSource3 = "module Lib\nlet foo (x: int) = x + 2\nlet bar (x: int) = x + 99\n"
        File.WriteAllText(libFile, libSource3)

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some r -> host.EmitFileChecked(r)
        | None -> failwith "lib CheckFile (bar change) failed"

        host.EmitBuildCompleted(BuildSucceeded)

        // Wait for the CONVERGED state — empty queue AND Completed — not a single terminal
        // transition: the rerun re-enters Running between run 1's completion and the final
        // settle, so a one-shot terminal wait races it. The invariant that makes
        // convergence possible is that `bar` is not committed by run 1: had run 1
        // committed its non-launch-set arrival, the rerun would never have re-tested it.
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

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-228: a rerun queued for debt the active run clears preserves that run's evidence`` () =
    // The production lifecycle this pins is RED test -> production edit -> green retry.
    // While the retry is in flight, BatchChecked sees the same durable debt and queues a
    // rerun. The green retry then clears that debt. The queued rerun is stale now: if it
    // launches, it selects zero projects and its NoProjectsSelected completion overwrites
    // the passing evidence the gate was waiting for.
    withTempDir "tp-stale-rerun" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.singleton "Lib.foo")

        let started = Path.Combine(tmpDir, "started")
        let release = Path.Combine(tmpDir, "release")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                Args = $"-c \"touch '{started}'; while [ ! -f '{release}' ]; do sleep 0.05; done\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let (getCompleted, recorder) = testRunCompletedRecorder ()
        host.RegisterHandler(recorder)

        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBuildCompleted(BuildSucceeded)
        waitUntil (fun () -> File.Exists started) 10000

        // Re-observe the same debt while its covering run is active. RunCommand is a
        // mailbox barrier: when it returns, the preceding BatchChecked has set
        // PendingRerun, so releasing the runner cannot race the setup.
        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])
        host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously |> ignore

        File.WriteAllText(release, "")
        waitForQuiescent host 20000

        let completed = getCompleted ()
        Assert.Single(completed) |> ignore
        test <@ not (RunVerification.verifiedNothing completed.Head.Verification) @>

        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ Set.isEmpty queue @>)

type private A163ScenarioOutcome =
    { RunCount: int
      Queue: Set<string>
      Status: PluginStatus option }

let private runA163CohortScenario name trigger testExitCode =
    withTempDir name (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let libFile = Path.Combine(tmpDir, "Lib.fsx")
        let testsFile = Path.Combine(tmpDir, "Tests.fsx")
        let runMarker = Path.Combine(tmpDir, "runs")
        let started = Path.Combine(tmpDir, "started")
        let release = Path.Combine(tmpDir, "release")
        let checker = FsHotWatch.Tests.TestHelpers.sharedChecker.Value
        let pipeline = CheckPipeline(checker)

        let libSource1 = "module Lib\nlet foo (x: int) = x + 1\n"
        let libSource2 = "module Lib\nlet foo (x: int) = x + 2\n"

        let testsSource =
            """module Tests
open Lib

type FactAttribute() = inherit System.Attribute()

[<Fact>]
let fooTest () = assert (foo 1 = 2)
"""

        File.WriteAllText(libFile, libSource1)
        File.WriteAllText(testsFile, testsSource)

        let libOptions =
            getScriptOptions checker libFile libSource1 |> Async.RunSynchronously

        let projOptions =
            { libOptions with
                SourceFiles = [| libFile; testsFile |] }

        pipeline.RegisterProject(libFile, projOptions)

        // Prime the persisted symbol graph in an analysis-only host. The second host is
        // the cold daemon: empty in-memory state over a warm on-disk impact database.
        let primingHost = PluginHost.create checker tmpDir
        primingHost.RegisterHandler(create dbPath tmpDir None None None None None [])
        primingHost.EmitBuildCompleted(BuildSucceeded)
        waitForPluginIdle primingHost "test-prune" 5.0

        for file in [ libFile; testsFile ] do
            match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
            | Some result -> primingHost.EmitFileChecked(result)
            | None -> failwith $"priming check failed for {file}"

        emitBatchAndQuiesce primingHost [ libFile; testsFile ]

        let configs =
            [ { Project = "Lib"
                Command = "sh"
                Args =
                  $"-c \"printf 'run\\n' >> '{runMarker}'; touch '{started}'; while [ ! -f '{release}' ]; do sleep 0.05; done; exit {testExitCode}\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = Some 15
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create checker tmpDir
        host.RegisterHandler(create dbPath tmpDir (Some configs) None None None None [])

        host.RunCommand("set-scope", [| "{\"scope\":\"full\"}" |])
        |> Async.RunSynchronously
        |> ignore

        File.WriteAllText(libFile, libSource2)
        host.EmitBuildCompleted(BuildSucceeded)
        waitUntil (fun () -> File.Exists started) 10000

        match pipeline.CheckFile(AbsFilePath.create libFile) |> Async.RunSynchronously with
        | Some result -> host.EmitFileChecked(result)
        | None -> failwith "cold-scan changed-file check failed"

        host.EmitBatchChecked(
            { fakeBatchChecked [ libFile ] with
                Trigger = trigger }
        )

        // The command is deliberately held until the cohort seal is observed. A fixed
        // sleep made this test assert scheduler speed on loaded Linux runners: the full
        // run could finish before CheckFile, turning BootScan into a real second run.
        host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously |> ignore
        File.WriteAllText(release, "")

        waitForQuiescent host 20000

        { RunCount = File.ReadAllLines(runMarker).Length
          Queue = PendingQueueHelpers.loadQueue tmpDir
          Status = host.GetStatus("test-prune") })

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-163: boot-scan symbols discovered during a green full run are covered without a second run`` () =
    // The production change this catches is treating `BootScan` like `InSessionBatch`
    // when its cohort seal arrives during the full run that a cold confirm launched.
    // The scan is a baseline over the same built tree, so that full run covers its
    // symbols; queueing another run silently doubles CI.
    let outcome = runA163CohortScenario "tp-a163-boot-scan" BootScan 0
    Assert.Equal(1, outcome.RunCount)
    test <@ Set.isEmpty outcome.Queue @>

    match outcome.Status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"expected the one full run to complete green, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-163: an in-session cohort discovered during a full run still queues exactly one rerun`` () =
    // Mutation caught: matching every BatchChecked as BootScan would disable the real
    // edit queue. The only difference from the regression above is cohort provenance.
    let trigger = InSessionBatch [ SourceChanged [ "Lib.fsx" ] ]
    let outcome = runA163CohortScenario "tp-a163-in-session" trigger 0
    Assert.Equal(2, outcome.RunCount)
    test <@ Set.isEmpty outcome.Queue @>

    match outcome.Status with
    | Some(Completed _) -> ()
    | other -> Assert.Fail($"expected the edit rerun to converge green, got %A{other}")

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-163: a failing full run cannot discharge boot-scan debt`` () =
    // Mutation caught: absorbing boot debt on the requested scope rather than the
    // completed run's actual green evidence would erase work that no passing test proved.
    let outcome = runA163CohortScenario "tp-a163-failed-full" BootScan 1
    Assert.Equal(1, outcome.RunCount)
    test <@ outcome.Queue.Contains "Lib.foo" @>

    match outcome.Status with
    | Some(Failed _) -> ()
    | other -> Assert.Fail($"expected the failed full run to stay red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``restart persistence: a non-empty queue survives a daemon restart and is re-flagged`` () =
    // Session 1 queues Lib.foo (covered by P1) but never proves it green. Session 2 — a
    // fresh plugin over the same on-disk sidecar and DB — must load the queue, re-flag
    // Lib.foo and run P1 again. An in-memory-only queue dies with the daemon, so the
    // restart silent-greens.
    withTempDir "tp-restart" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // Session-1 residue: a symbol never proven green.
        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        // P1 passes this time, so the restart-driven run covers Lib.foo and commits it —
        // proving it was re-flagged and actually re-tested, not silently absorbed.
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

        test <@ File.Exists ranMarker @>

        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.foo")) @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed after the re-flagged symbol tested green, got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``no-covering-test symbol drops from the queue at flush without wedging it`` () =
    // Retaining it would wedge the queue forever: every run selects zero tests, the queue
    // never empties, and the verdict is permanently non-green.
    withTempDir "tp-uncovered" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        // A covered symbol too, so the DB is non-empty and indexed.
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

        // The uncovered symbol drops with no test to wait on; the covered one commits
        // because P1 passed. An empty queue is the not-wedged condition.
        test <@ not (queue.Contains("Lib.uncovered")) @>
        test <@ not (queue.Contains("Lib.covered")) @>
        test <@ Set.isEmpty queue @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed (queue drained, not wedged), got %A{other}"))

// AUTOMATION-278 — the FIFTH aggregator, and the one that survived the first fix.
//
// The per-symbol green-commit folded `TestResult.isPassed` over the covering projects.
// `isPassed` was TRUE for `TestsNoMatch`, so a symbol whose covering project ran under an
// impact-derived class filter that matched ZERO tests had its test debt DISCHARGED and
// left `pending-verification.json` — verified by a project that executed nothing. That is
// the harm AUTOMATION-275 exists to prevent ("widen, never wipe"), one fold over, and the
// repo-local FSHW-VERDICT-001 analyzer cannot see it: the predicate sits behind a
// `match` in a lookup lambda.
//
// End to end rather than a unit fold, because the bug was in the WIRING: the fold looked
// correct in isolation and was wrong about which results it was folding over.
[<Fact(Timeout = 20000)>]
let ``a covering project that matched ZERO tests does not discharge a pending symbol`` () =
    withTempDir "tp-nomatch-commit" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.foo" ])

        // POSITIVE CONTROL for the fixture itself. Without it, a run that never invoked
        // P1 at all would also leave `Lib.foo` queued and this test would pass having
        // proved nothing.
        let ranMarker = Path.Combine(tmpDir, "p1-ran")

        let configs =
            [ { Project = "P1"
                Command = "sh"
                // Exit 8 is Microsoft.Testing.Platform's "Zero tests ran". A
                // `FilterTemplate` is required for the run to count as FILTERED, which
                // is what makes exit 8 a zero MATCH rather than a plain failure.
                Args = $"-c \"touch {ranMarker}; exit 8\""
                Group = "default"
                Environment = []
                FilterTemplate = Some "--filter-class {classes}"
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBuildCompleted(BuildSucceeded)
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // The runner really was invoked and really did report zero matches.
        test <@ File.Exists ranMarker @>

        // THE ASSERTION. The debt is still owed: nothing verified `Lib.foo`.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ queue.Contains("Lib.foo") @>

        // And the run-level verdict says so too, rather than reporting a green over a
        // project that executed nothing.
        match host.GetStatus("test-prune") with
        | Some(Failed _) -> ()
        | other -> Assert.Fail($"expected a non-green terminal for a run that matched zero tests, got %A{other}"))

// --- classifyTestOutcome: the structured report, not the process exit code, decides
// green/red; the exit code is a tie-break only when no report exists.

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
    // Exit 7 is MTP's dirty shutdown; the report shows zero failures and >= 1 test.
    let report = Some(rep 12 12 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested report)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(7, ProcessOutput.Drained "host crashed during shutdown"))

    test <@ TestResult.verifiedGreen result @>

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
    test <@ not (TestResult.verifiedGreen result) @>

[<Fact(Timeout = 5000)>]
let ``classify: non-zero exit with no report from an UNKNOWN runner stays FAILED (no regression)`` () =
    // Under NoReportRequested the exit code is the only signal there is.
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

    test <@ TestResult.verifiedGreen result @>

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
            (ProcessOutcome.TimedOut(TimeSpan.FromSeconds 30.0, ProcessOutput.Drained "stuck", KillOutcome.Killed))

    test <@ TestResult.isTimedOut result @>

// ---------------------------------------------------------------------------
// AUTOMATION-294 — a KILLED host is an abort, and a real red is still a red.
//
// Under CPU load the gate reported large numbers of 0ms "failures". A 0ms failure is a
// test that never ran: the host was killed and everything it had not reached was written
// out in the same shape as a test that ran and failed. That is a non-result rendered as a
// definite negative — the fail-open degrade inverted, and more expensive, because a red
// gets investigated where a green merely gets trusted.
//
// Every test in this block has a partner asserting the OTHER direction. A fix that made a
// genuine mass failure look like an abort would be the same lie with the sign flipped.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: a SIGKILLed host is an ABORT even though it flushed a report full of failures`` () =
    // The exact shape the ticket records: the host dies mid-suite and MTP still leaves a
    // report behind whose rows for tests it never reached are marked failed at 0ms.
    // Reading that report as the verdict is what minted the phantom mass regression.
    let phantomMassRegression = Some(rep 2171 2032 139 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested phantomMassRegression)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(137, ProcessOutput.Drained "failed FsHotWatch.Tests.AbsFilePath.roundtrips (0ms)"))

    test <@ TestResult.isErrored result @>
    test <@ not (isFailed result) @>
    test <@ not (TestResult.verifiedGreen result) @>
    // Never a pass and never a failure: NOTHING was verified.
    test <@ TestResult.verdict result = NothingVerified @>

    // And the reason SAYS what killed it, so nobody has to infer "0ms means never ran".
    let reason = TestResult.output result
    test <@ reason.Contains "SIGKILL" @>
    test <@ reason.Contains "137" @>
    test <@ reason.ToUpperInvariant().Contains "NOTHING" @>
    // The partial report is named as a transcript, with its counts, rather than hidden.
    test <@ reason.Contains "PARTIAL" @>
    test <@ reason.Contains "139" @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: THE OTHER DIRECTION — a real mass failure is still RED, not an abort`` () =
    // Same report, same counts, same 139 failures. The ONLY difference is that the runner
    // reached its own exit and chose the code (MTP's 2 = "at least one test failed")
    // instead of being killed. This must stay a red, or the fix has merely inverted the
    // lie: a gate that reported every genuine regression as "the machine was busy" would
    // be worse than the bug it replaced.
    let realMassRegression = Some(rep 2171 2032 139 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested realMassRegression)
            false
            (TimeSpan.FromMinutes 4.0)
            (ProcessOutcome.Failed(2, ProcessOutput.Drained "failed FsHotWatch.Tests.Foo.bar (312ms)"))

    test <@ isFailed result @>
    test <@ not (TestResult.isErrored result) @>
    test <@ TestResult.verdict result = Refuted @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: a SIGABRTed host is an abort even when it wrote a CLEAN report`` () =
    // The direction that must NOT go green. Exit 134 is what a real gate run produced
    // (an unhandled TimeoutException → the runtime aborts). A clean report from a process
    // that never reached its own exit describes the part of the suite it got through, and
    // outcome 2 ("a report showing zero failures beats the exit code") would have called
    // that a pass.
    let partialButClean = Some(rep 812 812 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested partialButClean)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(134, ProcessOutput.Drained ""))

    test <@ not (TestResult.verifiedGreen result) @>
    test <@ TestResult.isErrored result @>
    test <@ (TestResult.output result).Contains "SIGABRT" @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: the dirty-shutdown flake (exit 7) is STILL green — no regression`` () =
    // The guard against over-reach. Exit 7 is MTP's dirty shutdown, a code the runner
    // CHOSE; it is not a signal death, so the clean report still decides. If the new arm
    // swallowed it, every dirty shutdown would stop being a pass.
    let clean = Some(rep 12 12 0 0 0)

    let result =
        classifyTestOutcome
            (ReportRequested clean)
            false
            TimeSpan.Zero
            (ProcessOutcome.Failed(7, ProcessOutput.Drained "host crashed during shutdown"))

    test <@ TestResult.verifiedGreen result @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: an abort report never counts the killed run's transcript as failures`` () =
    // The CONSOLE half. A killed host's capture still holds per-test rows, and
    // `formatFailureReport` would head them "N test(s) failed" — which is the sentence
    // that sent people hunting a regression that was not there.
    let transcript =
        "Discovering: probe\nfailed FsHotWatch.Tests.AbsFilePath.roundtrips (0ms)\nfailed FsHotWatch.Tests.Foo.bar (0ms)"

    let abort =
        formatAbortReport "FsHotWatch.Tests" savedLog "test host was KILLED by SIGKILL (exit 137)" transcript
        |> String.concat "\n"

    test <@ abort.Contains "ABORTED" @>
    test <@ abort.Contains "SIGKILL" @>
    test <@ abort.ToUpperInvariant().Contains "NOTHING WAS VERIFIED" @>
    test <@ abort.Contains "NOT a test failure" @>
    // It must NEVER produce the count-of-failures headline.
    test <@ not (abort.Contains "test(s) failed") @>
    // The transcript is still shown — it is the only evidence of how far the run got.
    test <@ abort.Contains "AbsFilePath.roundtrips" @>

    // THE OTHER DIRECTION: the same lines through the FAILURE report still say "failed",
    // because for a run that finished they are findings.
    let failure =
        formatFailureReport "FsHotWatch.Tests" savedLog transcript |> String.concat "\n"

    test <@ failure.Contains "2 test(s) failed" @>

[<Fact(Timeout = 5000)>]
let ``AUTOMATION-294: an aborted project is a HostAborted ledger entry, and a failed one still Error`` () =
    // The VERDICT half. At `Error` severity the abort was counted by `failingDiagnostics`,
    // so the exit code said 1 and `verdict.json` said `red` about a run in which nothing
    // failed.
    let aborted: TestResults =
        { Results = Map.ofList [ "ProjA", TestsErrored "test host was KILLED by SIGKILL (exit 137)" ]
          Elapsed = TimeSpan.Zero }

    let entry = (failuresOf Map.empty aborted |> List.exactlyOne).Entry

    test <@ entry.Severity = FsHotWatch.ErrorLedger.HostAborted @>
    test <@ FsHotWatch.ErrorLedger.ErrorEntry.isRunnerAbort entry @>
    // Never a failure, under EITHER warn-fail policy.
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing true entry) @>
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing false entry) @>
    // And not a DEFER either: the remedies are opposite, and "re-run once the build
    // settles" is advice that never arrives for a host killed by a busy box.
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isWaitingOnBuild entry) @>
    test <@ entry.Message.ToLowerInvariant().Contains "aborted" @>

    // THE OTHER DIRECTION: a genuine failure is untouched — still Error, still failing.
    let failed: TestResults =
        { Results = Map.ofList [ "ProjB", TestsFailed("Some.Test FAILED", false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let realFailures = failuresOf Map.empty failed
    test <@ not realFailures.IsEmpty @>

    test
        <@
            realFailures
            |> List.forall (fun f ->
                f.Entry.Severity = FsHotWatch.ErrorLedger.Error
                && FsHotWatch.ErrorLedger.ErrorEntry.isFailing true f.Entry
                && not (FsHotWatch.ErrorLedger.ErrorEntry.isRunnerAbort f.Entry))
        @>

// AUTOMATION-454 — the teardown boundary, seen from the plugin that consumes it.
//
// A per-project timeout whose TEARDOWN also failed still has to become a terminal project
// result, and it has to stay distinguishable from the two things it is not: a suite whose
// tests failed, and a run that reported nothing at all.
[<Fact(Timeout = 5000)>]
let ``classify: a timeout whose teardown never answered is still terminal, and says so`` () =
    let result =
        classifyTestOutcome
            (ReportRequested None)
            false
            (TimeSpan.FromSeconds 300.0)
            (ProcessOutcome.TimedOut(
                TimeSpan.FromSeconds 300.0,
                ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0),
                KillOutcome.KillTimedOut(TimeSpan.FromSeconds 10.0)
            ))

    // Terminal, and TIMED OUT — never `TestsFailed`. Conflating the two would put a
    // wedged runner in the same bucket as a suite that ran and went red.
    test <@ TestResult.isTimedOut result @>
    test <@ not (isFailed result) @>

    // ...and the leaked tree rides on the text the operator reads, so "the project timed
    // out" is not mistaken for "and the runaway is over".
    match result with
    | TestsTimedOut(output, _, _, _) ->
        test <@ output.Contains "KILL TIMED OUT" @>
        test <@ output.Contains "STILL RUNNING" @>
    | other -> failwith $"expected TestsTimedOut, got %A{other}"

// =============================================================================
// AUTOMATION-95 / AUTOMATION-99 — the check must CONVERGE, never rest on a verdict
// nobody earned. One defect, two polarities.
//
// The pending-verification queue had exactly ONE drain trigger, the `BuildCompleted`
// handler. But on a scan `performScan` awaits BuildPlugin before dispatching the FCS
// tiers, so every symbol the SCAN discovers lands in the queue strictly AFTER the only
// event that could have run its tests. `BatchChecked` flushed those symbols, even
// computed their affected tests, then returned without running anything — so the queue
// was never drained and `check` reported whatever terminal status test-prune happened to
// hold: a stale `Completed` is a false green with symbols pending; a stale `Failed` is a
// permanently stuck red whose work never runs. Live: `check` returned in one second,
// exit 0, zero daemon activity, while the plugin's own log said "24 affected tests".
//
// Whoever DISCOVERS unverified symbols is responsible for RUNNING them.
// =============================================================================

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-95/99: BatchChecked drains a pending queue instead of resting on a stale verdict`` () =
    // BatchChecked is the cohort seal — the first moment the scan's symbols are known —
    // and so the only event left that can drain them.
    withTempDir "tp-batch-drain" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"

        // A symbol awaiting verification, as a scan's FileChecked pass would leave it.
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

        // Deliberately no BuildCompleted: on a scan it has come and gone before these
        // symbols existed.
        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // It RAN the covering tests rather than reporting on them ...
        test <@ File.Exists ranMarker @>

        // ... and only then went green.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.foo")) @>

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected an EARNED Completed after the drain, got %A{other}"))

// =============================================================================
// AUTOMATION-150 — an unreadable ledger is not an empty one.
//
// The queue file records what is still OWED, so when it cannot be READ the debt is
// UNKNOWN — and "unknown" is not "nothing". `load` swallowed a corrupt/truncated sidecar
// into `empty`, byte-identical to what a genuinely clean queue produces, so the entire
// outstanding test debt vanished into a `with _ -> empty` and the module broke its own
// stated invariant: the queue may only err toward OVER-testing.
//
// The boundary that keeps the fix honest: "the file does not exist" (first run, fresh
// clone) and "the file exists and I could not read it" are DIFFERENT facts. The first is
// legitimately empty; collapsing them either wedges every fresh clone into a permanent
// full suite or re-opens the hole. All three tests below pin that boundary.
// =============================================================================

/// The two-project runner these tests share. P1 covers `Lib.foo` and P2 covers `Lib.debt`,
/// so an impact-filtered selection driven by a changed `Lib.foo` runs P1 and SKIPS P2,
/// while a widened full-suite run touches both.
let private ledgerRunner (project: string) (marker: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = $"-c \"touch {marker}; exit 0\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect }

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-150: an UNREADABLE ledger widens to the FULL suite rather than greening on nothing`` () =
    // The sidecar EXISTS but is truncated mid-array — a crashed/torn write — and it once
    // held real debt (Lib.debt, covered by P2, never proven green). Catching the parse
    // throw and returning `empty` makes the drain gate (`if Set.isEmpty pendingQueueRef
    // then return`) read "nothing owed", run ZERO tests, and rest on a green verdict.
    //
    // `Unreadable` is a DIFFERENT VALUE that cannot be mistaken for an empty queue, and a
    // selection made without the ledger cannot be trusted — so the run widens to every
    // configured project in full, the only scope that discharges a debt of unknown
    // membership.
    withTempDir "tp-ledger-unreadable" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.debt" "Debt.fs" "P2" "P2Tests" "debtTest"

        // The file EXISTS: this is emphatically not a fresh clone.
        let path = FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "[\"Lib.deb")

        let p1Ran = Path.Combine(tmpDir, "p1-ran")
        let p2Ran = Path.Combine(tmpDir, "p2-ran")
        let configs = [ ledgerRunner "P1" p1Ran; ledgerRunner "P2" p2Ran ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // The cold-scan shape again: BatchChecked is the only event that can drain what
        // the scan discovered.
        let await = beginAwaitNextTerminal host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])
        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        // It RAN — an unreadable ledger owes MORE testing, never less ...
        test <@ File.Exists p1Ran @>
        // ... and it ran EVERYTHING. P2 is the project a filtered selection skips, and
        // the one holding the debt the corrupt file swallowed.
        test <@ File.Exists p2Ran @>

        // And it SELF-HEALS: a full suite passed every configured project, so every symbol
        // the lost ledger could have held is verified and the corrupt file is rewritten.
        // The next session loads a readable ledger and goes back to impact filtering
        // rather than grinding a full suite forever.
        test <@ Set.isEmpty (LedgerHelpers.expectLoaded tmpDir) @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-150: a MISSING ledger (fresh clone) is legitimately empty and does NOT force a full suite`` () =
    // The trade this fix must NOT make: fail-open swapped for a stuck full suite. A fresh
    // clone has no ledger at all, which is a provable "nothing owed" and must stay a fast
    // no-op.
    withTempDir "tp-ledger-missing" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.debt" "Debt.fs" "P2" "P2Tests" "debtTest"

        test <@ not (File.Exists(FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir)) @>

        let p1Ran = Path.Combine(tmpDir, "p1-ran")
        let p2Ran = Path.Combine(tmpDir, "p2-ran")
        let configs = [ ledgerRunner "P1" p1Ran; ledgerRunner "P2" p2Ran ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])

        // Give a run every chance to start before concluding none did.
        waitUntil (fun () -> File.Exists p1Ran || File.Exists p2Ran) 3000
        waitForQuiescent host 5000

        test <@ not (File.Exists p1Ran) @>
        test <@ not (File.Exists p2Ran) @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-150: a genuinely EMPTY ledger stays a fast no-op (not a widened run)`` () =
    // The other half of the boundary. Misclassify `[]` as unreadable and every idle daemon
    // grinds a full suite forever.
    withTempDir "tp-ledger-empty" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "Lib.fs" "P1" "P1Tests" "fooTest"
        PendingQueueHelpers.seedCoveredSymbol db "Lib.debt" "Debt.fs" "P2" "P2Tests" "debtTest"

        FsHotWatch.TestPrune.PendingVerification.save tmpDir Set.empty
        test <@ File.Exists(FsHotWatch.TestPrune.PendingVerification.sidecarPath tmpDir) @>

        let p1Ran = Path.Combine(tmpDir, "p1-ran")
        let p2Ran = Path.Combine(tmpDir, "p2-ran")
        let configs = [ ledgerRunner "P1" p1Ran; ledgerRunner "P2" p2Ran ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create dbPath tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        host.EmitBatchChecked(fakeBatchChecked [ "Lib.fs" ])

        waitUntil (fun () -> File.Exists p1Ran || File.Exists p2Ran) 3000
        waitForQuiescent host 5000

        test <@ not (File.Exists p1Ran) @>
        test <@ not (File.Exists p2Ran) @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-99: a symbol covered only by an unconfigured test project drops instead of wedging the verdict red``
    ()
    =
    // The symbol DB indexes test methods from EVERY project it analyzed, which is not the
    // set of projects fshw is configured to run. A symbol covered only by an unconfigured
    // project can never be proven green: its covering project never executes, so it never
    // lands in a run's results and never commits. Live: two full suites passed
    // back-to-back and `check` still exited 1, because the only covering tests lived in
    // FsHotWatch.IntegrationTests, which the daemon does not run.
    //
    // "Covered" means "covered by a test we can actually run"; anything else is
    // indistinguishable from having no covering test and drops by the same rule.
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

        // Unverifiable by construction, so dropped rather than retained forever.
        let queue = PendingQueueHelpers.loadQueue tmpDir
        test <@ not (queue.Contains("Lib.orphan")) @>

        match host.GetStatus("test-prune") with
        | Some(PluginStatus.Failed(msg, _, _)) ->
            Assert.Fail($"check wedged red on a symbol no runnable test covers: %s{msg}")
        | _ -> ())

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-95: a plugin with a test run in flight reports BUSY, so no verdict can resolve mid-run`` () =
    // The third facet: `check` handed back a verdict WHILE the run that would have
    // produced it was still executing. "Busy" meant only "has events queued in its
    // mailbox", blind to the background work a handler launches via RunExclusive and then
    // returns from — so the host saw an idle mailbox and WaitForComplete resolved mid-run.
    // Live: a run launched 11:30:17, still executing at 11:30:34, and the daemon logged
    // "all plugins already terminal" while `check` exited 0.
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

        // By the time status reaches Running the BuildCompleted handler has returned, so
        // the MAILBOX is drained and the only thing that can keep the plugin busy is the
        // background run itself.
        let isRunning () =
            match host.GetStatus("test-prune") with
            | Some(Running _) -> true
            | _ -> false

        waitUntil isRunning 5000
        test <@ isRunning () @>

        test <@ host.AnyPluginBusy() @>

        await.Wait(TimeSpan.FromSeconds 15.0) |> ignore

        match host.GetStatus("test-prune") with
        | Some(Completed _) -> ()
        | other -> Assert.Fail($"expected Completed once the in-flight run finished, got %A{other}"))

// --- AUTOMATION-113: an unanalysable file must not vanish from the impact graph ---
//
// A file whose symbol analysis fails contributes NO symbols, and the `Error` branch
// simply `return state`d: the file was dropped, the impact graph never saw it, a change
// to it diffed against nothing and selected NO tests, and the check went green having run
// nothing relevant — silently. It now forces the COARSE selection (every test project, in
// full) and says so loudly. Safe over-selection beats silent under-selection.

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
    // A healthy tree pays nothing: the dependency-fanout set passes through untouched.
    let fanout = Set.ofList [ "Beta.Tests" ]

    let result = coarseFallbackProjects threeProjects Set.empty fanout

    test <@ result = fanout @>

[<Fact(Timeout = 15000)>]
let ``one unanalysable file force-runs EVERY test project`` () =
    // The file is invisible to the symbol graph, so no per-symbol selection can be trusted
    // to cover it, and the whole suite is the only sound response to "I cannot tell you
    // what is affected".
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
    // The skip gate in `runTestsWithImpact` greens with 0 tests run when there are no
    // affected classes AND no force-run projects, so a non-empty force-run set is what
    // keeps an unanalysable file away from that verdict. Asserted through the same
    // emptiness predicate `confirm` reads.
    let forceRun =
        coarseFallbackProjects threeProjects (Set.ofList [ "src/Lib/Broken.fs" ]) Set.empty

    test <@ not (Set.isEmpty forceRun) @>

[<Fact(Timeout = 15000)>]
let ``an unanalysable file is reported LOUDLY, naming the file and the reason`` () =
    // A log line and a plugin status the next file's `Completed` overwrites is nothing a
    // consumer can see. The diagnostic must name the file, carry the reason, and be at
    // least Warning severity so the default warn-fail policy denies the check a green.
    let reason = "Parse errors: XML comment is not placed on a valid language element."

    let entry = unanalyzableFileDiagnostic "src/Lib/Broken.fs" reason

    test <@ entry.Severity = FsHotWatch.ErrorLedger.Warning @>
    test <@ entry.Message.Contains "src/Lib/Broken.fs" @>
    test <@ entry.Message.Contains "XML comment is not placed on a valid language element." @>
    test <@ FsHotWatch.ErrorLedger.ErrorEntry.isFailing true entry @>

    let detail = entry.Detail |> Option.defaultValue ""
    test <@ detail.Contains "INVISIBLE to the impact graph" @>

// --- AUTOMATION-112: the full-suite scope is part of the task cache key ---

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: a confirm cannot replay an impact-filtered run's cached verdict`` () =
    // Everything else about the tree is identical — same symbols, empty queue, same deps
    // — so without the scope in the key, the first thing `fshw confirm` does on an
    // unchanged tree is hit the entry an earlier impact-filtered `check` wrote, replay its
    // green, and never start a test process: a filtered verdict laundered into a merge
    // verdict with no run at all.
    let keyWithScope (fullSuiteScope: string option) =
        cacheKeyFor
            (fun () -> "same-symbols")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> fullSuiteScope)
            (fun () -> false)
            (fun () -> true)
            (BuildCompleted BuildSucceeded)

    let innerLoopKey = keyWithScope None
    let fullSuiteKey = keyWithScope (Some "full")

    test <@ innerLoopKey.IsSome @>
    test <@ fullSuiteKey.IsSome @>
    test <@ innerLoopKey <> fullSuiteKey @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: the inner-loop key is unchanged by the scope salt`` () =
    // `None` rather than "impact" for the inner loop keeps the merkle entry OMITTED, so
    // the ordinary key stays byte-identical to the pre-feature one and existing on-disk
    // entries keep hitting. `confirm` pays for its own scope; the fast loop pays nothing.
    let withScopeThunk =
        cacheKeyFor
            (fun () -> "s")
            (fun () -> None)
            (fun () -> Some "deps")
            (fun () -> "struct")
            (fun () -> None)
            (fun () -> false)
            (fun () -> true)
            (BuildCompleted BuildSucceeded)

    // The same inputs, hand-built with no full-suite-scope entry at all.
    let expected =
        FsHotWatch.TaskCache.merkleCacheKey
            [ "plugin-version", "test-prune-merkle-v2"
              "event", "BuildCompleted"
              "changed-symbols", "s"
              "project-structure", "struct"
              "build-outcome", "succeeded"
              "depends-on", "deps" ]

    test <@ withScopeThunk = Some expected @>

[<Fact(Timeout = 10000)>]
let ``cacheKeyFor: two full-suite runs over the same tree DO share a key`` () =
    // Determinism of the key is not equivalence of the world. Reading this as "a second
    // `confirm` over an unchanged tree may replay a run that genuinely WAS full-suite" is
    // the belief that produced AUTOMATION-161: the key does not pin the TREE, because on a
    // cold scan BuildCompleted is dispatched before the FCS pass and `changed-symbols` is
    // empty whatever the tree holds.
    //
    // Sharing the key is still right — it lets a WARM daemon skip a redundant in-session
    // run, and lets the entry a `TestsFinished` writes be found by the next
    // `BuildCompleted`. What must not follow is a REPLAY into a process with no run of its
    // own; the session-evidence gate below forbids that.
    let fullSuiteKey () =
        cacheKeyFor
            (fun () -> "same")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> Some "full")
            (fun () -> false)
            (fun () -> true)
            (BuildCompleted BuildSucceeded)

    test <@ fullSuiteKey () = fullSuiteKey () @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-161: cacheKeyFor refuses BuildCompleted while the process has NO test evidence`` () =
    // A cached BuildCompleted entry ASSERTS a test result, and a process whose own state
    // records no run may not make that assertion. `None` means no replay and no write,
    // exactly as a non-empty pending queue and an outstanding failure already do.
    let keyWithEvidence (hasEvidence: bool) =
        cacheKeyFor
            (fun () -> "same")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> Some "full")
            (fun () -> false)
            (fun () -> hasEvidence)
            (BuildCompleted BuildSucceeded)

    test <@ (keyWithEvidence false).IsNone @>
    // Once a run has covered something, the warm in-session fast path is back.
    test <@ (keyWithEvidence true).IsSome @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-161: the TestsFinished WRITE is not gated on session evidence`` () =
    // The WRITE mints the entry the next BuildCompleted hits, and it is computed at
    // DISPATCH time — before the run this message carries has been folded into state, so
    // there IS no evidence to see. Gating it would mean the cache is never written and the
    // warm in-session fast path dies with it. Safe, because this key is never used for a
    // LOOKUP: the framework does not replay over a `Custom` message, whose payload is not
    // in its key (see PluginFrameworkTests).
    let allPassed =
        Custom(
            TestsFinished(
                { RunId = Guid.NewGuid()
                  StartedAt = DateTime.UtcNow },
                { RunId = Guid.NewGuid()
                  TotalElapsed = TimeSpan.Zero
                  Outcome = Normal
                  Results = Map.ofList [ "ProjA", TestsPassed("ok", false, TimeSpan.Zero) ]
                  Verification = Ran RunScope.FullSuite },
                fullSuiteLaunch [ "ProjA" ]
            )
        )

    let key =
        cacheKeyFor
            (fun () -> "same")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> Some "full")
            (fun () -> false)
            // No evidence yet — this run is the one about to provide it.
            (fun () -> false)
            allPassed

    test <@ key.IsSome @>

// --- AUTOMATION-129: `confirm`'s scope is a PROJECTION of RunCoverage ---
//
// `classifyRunScope` derived `confirm`'s scope independently from `LastResults`, while the
// ledger decided what a run may CLEAR from `RunCoverage` — two answers to one question
// with nothing making them agree, so `confirm` could go green on a scope the ledger would
// never have granted. `scopeOf` is a VIEW of the ledger's own value, so they cannot
// disagree by construction.

[<Fact(Timeout = 10000)>]
let ``scopeOf: every project executed in FULL is the only whole-suite scope`` () =
    let projects = [ "Alpha.Tests"; "Beta.Tests" ]

    let everything =
        Map.ofList [ "Alpha.Tests", CoveredWholeProject; "Beta.Tests", CoveredWholeProject ]

    test <@ scopeOf projects everything = ScopeFull 2 @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: a class-filtered project makes the run a SUBSET, never full-suite`` () =
    let projects = [ "Alpha.Tests"; "Beta.Tests" ]

    let oneFiltered =
        Map.ofList
            [ "Alpha.Tests", CoveredClasses(Set.ofList [ "SomeTests" ])
              "Beta.Tests", CoveredWholeProject ]

    test <@ scopeOf projects oneFiltered = ScopeFiltered(2, 2) @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: an unfiltered run that SKIPPED a project is a subset`` () =
    let projects = [ "Alpha.Tests"; "Beta.Tests" ]
    let oneMissing = Map.ofList [ "Alpha.Tests", CoveredWholeProject ]
    test <@ scopeOf projects oneMissing = ScopeFiltered(1, 2) @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: the zero-affected skip's empty green is NO SCOPE, not a full suite`` () =
    // The trap: `RanFullSuite` is vacuously TRUE for an empty map, and a run whose
    // coverage is empty verified nothing. `ScopeNone` is what the CLI refuses to call
    // green in either mode.
    test <@ scopeOf [ "Alpha.Tests" ] RunCoverage.none = ScopeNone 1 @>

[<Fact(Timeout = 10000)>]
let ``scopeOf: a repo with no test projects is not a covered suite`` () =
    // There is no evidence in a run of nothing.
    test <@ scopeOf [] RunCoverage.none = ScopeNone 0 @>


// A test run the daemon cannot SEE is evidence it cannot judge. The `run-tests` IPC
// command called `executeTests` directly on the IPC thread: no `RunExclusive "tests"`
// slot, no `Running` status, no busy accounting. During such a run the daemon's whole
// model read "at rest", so `fshw check` could exit 0 while the test process was alive and
// any concurrent FileChecked stamped a terminal status over it (the "✓ test-prune,
// started: with no elapsed:" signature).

/// A single-project config whose command touches `started`, waits (bounded) for `release`,
/// then touches `done` — so the test controls the in-flight window deterministically. The
/// script lives in a file so no argument-quoting rules apply.
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

            // The test process is running, so the plugin must hold the exclusive "tests"
            // slot and report Running — otherwise a concurrent `fshw check` sees "at rest"
            // and exits 0 mid-execution.
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
        // The results JSON is unchanged by the accounting.
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

            // An editor save during a long suite. Whatever its analysis outcome, the run
            // owns the status until TestsFinished delivers the earned verdict — analysis
            // diagnostics still reach the error ledger, so nothing is lost by staying
            // Running.
            let srcFile = Path.Combine(tmpDir, "Lib.fs")
            File.WriteAllText(srcFile, "module Lib\nlet x = 1\n")

            // Subscribe BEFORE the mid-run event: the bug is a TRANSIENT terminal, stamped
            // and immediately overwritten, which a polling sampler misses entirely.
            let terminalDuringRun = beginAwaitNextTerminal host "test-prune"

            host.EmitFileChecked(
                { fakeFileCheckResult srcFile with
                    Source = "module Lib\nlet x = 1\n" }
            )

            // The run is provably still gated (`done` unwritten), so any terminal
            // transition observed inside this window is the manufactured status.
            let stampedMidRun = terminalDuringRun.Wait(TimeSpan.FromSeconds 3.0)
            test <@ not (File.Exists doneFile) @>
            test <@ not stampedMidRun @>
        finally
            File.WriteAllText(release, "")

        cmdTask.Wait(TimeSpan.FromSeconds 20.0) |> ignore
        test <@ cmdTask.IsCompleted @>)

[<Fact(Timeout = 30000)>]
let ``a green run's Completed status carries its verdict`` () =
    // A ✓ with nothing to say is unrepresentable: the status carries what the run did, and
    // the history record holds the SAME summary — one channel, host-routed.
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

// `test-rerun` is the repo's "prove it ran" verb: it must never report success without
// running, so a slot held by another run QUEUES the force-run rather than declining it.

/// Like `gatedRunConfig`, but the script appends one line per invocation to a `runs` file,
/// so a test can COUNT executions rather than trusting a status.
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

        // Run #2 arrives while the slot is HELD. Replying `busy` here means exit 0 having
        // executed nothing.
        let second = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        // Still queued, and still owed a reply.
        Thread.Sleep 500
        test <@ runCount runs = 1 @>
        test <@ not second.IsCompleted @>

        File.WriteAllText(release, "")

        first.Wait(TimeSpan.FromSeconds 30.0) |> ignore
        second.Wait(TimeSpan.FromSeconds 30.0) |> ignore

        test <@ first.IsCompleted @>
        test <@ second.IsCompleted @>

        // The suite executed TWICE, and the second reply is a real results payload rather
        // than a "busy" non-verdict.
        waitUntil (fun () -> runCount runs = 2) 20000
        test <@ runCount runs = 2 @>

        test <@ second.Result.IsSome @>
        let json = second.Result.Value
        test <@ json.Contains("projects") @>
        test <@ not (json.Contains("\"busy\"")) @>)

[<Fact(Timeout = 60000)>]
let ``a queued run-tests reply resolves — a refused claim can never strand the IPC caller`` () =
    // The hang the RunClaim DU makes impossible: the reply TCS lives inside the work
    // async, so a silently-dropped claim resolved nothing and the command's
    // `Async.AwaitTask reply.Task` waited forever.
    withTempDir "tp-rerun-noStrand" (fun tmpDir ->
        let config, started, release, _runs = countingGatedRunConfig tmpDir

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some [ config ]) None None None None []
        host.RegisterHandler(handler)

        let first = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask
        waitUntil (fun () -> File.Exists started) 20000

        // Three force-runs pile up behind the in-flight one; none may be stranded.
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
    // The last unbounded seam is the reply wait. A 1-second budget against a gated,
    // never-releasing run must return the DISTINCT `busy` status, which the CLI maps to a
    // non-zero exit so it can never read as a pass the run did not produce.
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
            test <@ not (json.Value.Contains("\"projects\"")) @>
            test <@ File.Exists started @>
        finally
            // Let the daemon-side run finish so the temp dir can be cleaned.
            File.WriteAllText(release, "")
            waitUntil (fun () -> not (host.AnyPluginBusy())) 30000)

// =============================================================================
// AUTOMATION-125 — a run may clear ONLY what it COVERED.
//
// Live, 2026-07-14: a full run failed one project; a queued impact-filtered re-run then
// executed a NARROWER selection, passed, and — via `ClearAllErrors` plus
// last-cycle-wins — superseded the red. The failing test never re-ran and never passed,
// yet `check` went green: "no failures reported by THIS run" read as "no failures".
//
// These drive the plugin's real `Custom(TestsFinished)` handler through a recording
// `PluginCtx` — the exact seam where the laundering happened. Both directions are pinned:
// a disjoint filtered green must NOT clear, and a COVERING filtered green MUST, or the
// fix becomes AUTOMATION-99's permanent stuck-red.
// =============================================================================

/// Recording ctx over the plugin: captures terminal statuses and models the shared error
/// ledger, where ClearAllErrors wipes the plugin's whole slice exactly as
/// `PluginFramework` does via `ClearPlugin`.
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
          RunExclusiveShared = fun _ _ _ _ _ -> FsHotWatch.PluginFramework.SharedClaimed
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

/// A `TestsFinished` for a completed run: per-project results plus the SCOPE it was
/// launched against.
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
          Verification = RunVerification.ofResults (Map.ofList results) }

    Custom(TestsFinished(started, completed, launch))

/// The runner's own wording for a failing test, so `parseFailedTests` attributes the
/// red to the CLASS (`ProjATests`) exactly as it does in the field.
let private failedProjA =
    TestsFailed("failed FsHotWatch.Tests.ProjATests.boom (12ms)", false, TimeSpan.FromSeconds 1.0)

/// What `executeTests` records for a project impact analysis SKIPPED: a pass, marked
/// filtered, with no output and no elapsed. It proves nothing, and is the value the
/// `ClearAllErrors` path read as "ProjA is fine now".
let private impactSkipped = TestsPassed("", true, TimeSpan.Zero)

let private passed (filtered: bool) =
    TestsPassed("ok", filtered, TimeSpan.FromSeconds 1.0)

/// No per-test report evidence: the fail-closed default `RunCoverage.ofRun` reads
/// whenever a run wrote no readable, complete CTRF report. A raw `--filter` run under
/// this claims nothing.
let private noReportEvidence: Map<string, Set<string>> = Map.empty

/// Drive the plugin through a sequence of completed runs, returning the ctx recorders and
/// the final state. Starts from the handler's own `Init`, so every invariant the real
/// plugin carries in state is carried here too.
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
    // The observed sequence exactly: a full run fails ProjA, then a queued
    // impact-filtered re-run selects only ProjB while ProjA is skipped and recorded as a
    // filtered pass.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let filteredRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; filteredRun ]

    // The red survives the green that never ran it.
    test <@ final.OutstandingFailures |> List.exists (fun f -> f.Project = "ProjA") @>
    test <@ ledgerFilesOf ledger |> List.exists (fun f -> f.Contains("ProjA")) @>

    // A failing ledger entry alone would already deny `check` its exit 0, but the plugin's
    // own status must not claim a green it did not earn either.
    match lastStatus statuses with
    | PluginStatus.Failed(msg, _, _) -> test <@ msg.Contains("ProjA") @>
    | other -> Assert.Fail($"a filtered green that never ran ProjA must not produce a green terminal, got %A{other}")

// `recordRunOutcome` is the sole producer of this plugin's terminal status and decides
// purely on `TestResult.isPassed` counts. `isPassed` is TRUE for `TestsNoMatch` by design
// — per project, a filter selecting nothing is not that project's failure — so
// `failed = 0 && deferred = 0` holds and the green branch fires unless the run-level
// question is asked explicitly. It reported "N passed, 0 failed in N projects" about N
// projects that executed no test at all.
//
// A zero match in ONE project remains a pass for that project: an impact selection naming
// no class in the Integration project must not fail it. Only the run-level verdict
// changes, and only when nothing matched anywhere.
[<Fact(Timeout = 20000)>]
let ``a run where every project matched zero tests is not a green terminal status`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let zeroMatch = TestsNoMatch("Zero tests ran", TimeSpan.FromSeconds 1.0)

    let run =
        testsFinishedEvent [ "ProjA", zeroMatch; "ProjB", zeroMatch ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        Assert.Fail(
            $"every project matched zero tests, so the run verified nothing and must not be green — got %A{verdict}"
        )
    | _ -> ()

// So the guard above cannot be satisfied by refusing greens generally.
[<Fact(Timeout = 20000)>]
let ``a run where one project matched zero tests and another really ran is still green`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let zeroMatch = TestsNoMatch("Zero tests ran", TimeSpan.FromSeconds 1.0)

    let run =
        testsFinishedEvent [ "ProjA", zeroMatch; "ProjB", passed true ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Completed _ -> ()
    | other -> Assert.Fail($"ProjB executed tests and passed, so this run IS green — got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a COVERING impact-filtered green DOES clear the red (no stuck-red)`` () =
    // The over-correction guard. A filtered run that DID execute the failing class and
    // passed it is real evidence; a check that can never go green again is not a fix, it
    // is a different bug.
    withTempDir "a125-covering-receipt" (fun repoRoot ->
        let handler =
            create ":memory:" repoRoot (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

        let fullRun =
            testsFinishedEvent [ "ProjA", failedProjA; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

        let runId = Guid.NewGuid()
        let reportDir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
        Directory.CreateDirectory(reportDir) |> ignore

        File.WriteAllText(
            Path.Combine(reportDir, "ProjA.ctrf.json"),
            """{"results":{"summary":{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0},"tests":[{"name":"FsHotWatch.Tests.ProjATests.boom","status":"passed","duration":1}]}}"""
        )

        let started: TestRunStarted =
            { RunId = runId
              StartedAt = DateTime.UtcNow }

        let results = Map.ofList [ "ProjA", passed true; "ProjB", impactSkipped ]

        let completed: TestRunCompleted =
            { RunId = runId
              TotalElapsed = TimeSpan.FromSeconds 1.0
              Outcome = Normal
              Results = results
              Verification = RunVerification.ofResults results }

        let coveringRun =
            Custom(TestsFinished(started, completed, filteredLaunch [ "ProjA", [ "FsHotWatch.Tests.ProjATests" ] ]))

        let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; coveringRun ]

        test <@ List.isEmpty final.OutstandingFailures @>
        test <@ List.isEmpty (ledgerFilesOf ledger) @>

        match lastStatus statuses with
        | PluginStatus.Completed _ -> ()
        | other -> Assert.Fail($"a filtered run that executed the failing class green must clear it, got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: an unfiltered re-run (test-rerun) clears an outstanding red`` () =
    // The escape hatch the rule leans on: `test-rerun` runs every project UNFILTERED, so
    // it covers everything and may clear anything. Without it the rule is a wedge.
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
    // Project granularity is not enough: a run that selected ProjA but filtered to a class
    // OTHER than the failing one executed the project without executing the failure, so
    // "ProjA passed" is true of that run and says nothing about the red.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA ] (fullSuiteLaunch [ "ProjA" ])

    let otherClassRun =
        testsFinishedEvent [ "ProjA", passed true ] (filteredLaunch [ "ProjA", [ "FsHotWatch.Tests.SomeOtherTests" ] ])

    let _ctx, statuses, _ledger, final = driveRuns handler [ fullRun; otherClassRun ]

    test
        <@
            final.OutstandingFailures
            |> List.exists (fun f -> f.Class = Some "FsHotWatch.Tests.ProjATests")
        @>

    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"a filtered green over a different class must not clear the red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: the zero-affected skip (0 ran, green) cannot launder an outstanding red`` () =
    // The likeliest laundering path in practice: after the failing run the next build
    // changes nothing relevant, so the skip gate completes "green, 0 ran".
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", failedProjA ] (fullSuiteLaunch [ "ProjA" ])

    // The degenerate skip lifecycle: empty results, empty selection, Normal outcome.
    let skipRun = testsFinishedEvent [] emptyLaunch

    let _ctx, statuses, ledger, final = driveRuns handler [ fullRun; skipRun ]

    test <@ final.OutstandingFailures |> List.exists (fun f -> f.Project = "ProjA") @>
    test <@ ledgerFilesOf ledger |> List.exists (fun f -> f.Contains("ProjA")) @>

    // `Failed`, specifically: the OUTSTANDING RED is what makes this one a red. A run
    // that executes nothing with NO red outstanding is non-green for a different reason
    // and in a different way — `Completed` carrying a verified-nothing verdict, which the
    // AUTOMATION-198 test below pins. Neither may be a green.
    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"a run that executed nothing must not launder an outstanding red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-198: a run that executed NO project records a verdict that says nothing was verified`` () =
    // The same degenerate skip lifecycle as the AUTOMATION-125 test above, minus the
    // outstanding red: empty results, empty selection, `Normal` outcome, nothing owed.
    // This is the state that put `✓ test-prune — 0 passed, 0 failed in 0 projects` on a
    // check that verified nothing and then (correctly) refused to certify it.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let _ctx, statuses, _ledger, _final =
        driveRuns handler [ testsFinishedEvent [] emptyLaunch ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        // The STATUS stays `Completed` — nothing failed, and a `Failed` would turn
        // `check`'s honest exit 3 (NO VERDICT) into an exit 1 (failures found). The
        // VERDICT is what has to stop reading as a pass; every renderer glyphs off it.
        test <@ RunSummary.saysNothingVerified verdict.Summary @>
        // Never the counts line: "0 passed, 0 failed" is a pass report, and `in 0
        // projects` is the only part of it that says otherwise.
        test <@ not (verdict.Summary.Contains "0 passed") @>
    | other -> Assert.Fail($"a run that executed nothing must complete, not fail, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-198: a run that DID execute keeps its counts verdict`` () =
    // Positive control for the test above: the same path, one project that actually ran.
    // Without this, "the verdict says nothing was verified" would pass just as well if
    // every run started claiming it.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let _ctx, statuses, _ledger, _final =
        driveRuns handler [ testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ]) ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        test <@ not (RunSummary.saysNothingVerified verdict.Summary) @>
        test <@ verdict.Summary.Contains "1 passed, 0 failed" @>
        test <@ verdict.Summary.Contains "in 1 projects" @>
    | other -> Assert.Fail($"a run that executed and passed must complete green, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-294: a run whose test HOST DIED completes as an ABORT, never as "N failed"`` () =
    // The RUN-LEVEL console half. `recordRunOutcome` used to fold an errored project into
    // `failedList` (`not isDeferred`), so a killed host produced `1 failed: ProjB` on the
    // status line and `PluginStatus.Failed` underneath it — a definite negative about a
    // project whose tests never finished running.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let aborted =
        TestsErrored "test host was KILLED by SIGKILL (exit 137) — it never reached its own exit"

    let run =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", aborted ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) ->
        // `Completed`, exactly like the pure-defer case: nothing FAILED, and a `Failed`
        // here would turn the honest exit 2 back into the exit 1 this ticket is about.
        // The SUMMARY is what carries the fact, and it says NOTHING VERIFIED — so no
        // renderer can glyph this run with a bare tick either.
        test <@ RunSummary.saysNothingVerified verdict.Summary @>
        test <@ verdict.Summary.Contains "ABORTED" @>
        test <@ verdict.Summary.Contains "ProjB" @>
        test <@ verdict.Summary.Contains "NOT a test failure" @>
        // The words that used to be there, and must not be.
        test <@ not (verdict.Summary.Contains "1 failed:") @>
    | other ->
        Assert.Fail(
            $"a run whose host was killed must complete as an ABORT, not fail as though a test broke — got %A{other}"
        )

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-294: THE OTHER DIRECTION — a run with a REAL failure still fails, and names the abort apart`` () =
    // The guard against inverting the lie. A genuine red alongside a killed host stays a
    // red: `PluginStatus.Failed`, "1 failed: ProjA". The abort is still NAMED — it proved
    // nothing and a reader must not add it to the failure count — but it neither
    // launders the failure nor is counted as one.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let aborted = TestsErrored "test host was KILLED by SIGKILL (exit 137)"

    let run =
        testsFinishedEvent [ "ProjA", failedProjA; "ProjB", aborted ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let _ctx, statuses, _ledger, _final = driveRuns handler [ run ]

    match lastStatus statuses with
    | PluginStatus.Failed(msg, _, verdict) ->
        test <@ msg.Contains "1 failed: ProjA" @>
        // Named, and named as an abort — never rolled into the "1".
        test <@ msg.Contains "ABORTED" @>
        test <@ msg.Contains "ProjB" @>
        test <@ not (msg.Contains "2 failed") @>
        // The counts line agrees with the status line: one failure, one abort.
        test <@ verdict.Summary.Contains "1 failed" @>
        test <@ verdict.Summary.Contains "1 ABORTED" @>
    | other -> Assert.Fail($"a real failure beside an abort must stay a red, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a TIMED-OUT project's red needs a WHOLE-project pass, not a class-filtered one`` () =
    // A project killed for being stuck is a fact about the PROJECT (`Class = None`), so
    // only a run that executed the project in full can clear it.
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

    // Even a green over the very class named in the timeout output does not clear it.
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
    // The laundering vector in one assertion: the skip sentinel is a PASS, and reading it
    // as evidence is the bug.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjB", ProjectClasses(Set.ofList [ "ProjBTests" ]) ])
            (Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ])
            noReportEvidence

    test <@ not (RunCoverage.covers "ProjA" (Some "ProjATests") coverage) @>
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>
    test <@ RunCoverage.covers "ProjB" (Some "ProjBTests") coverage @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: an UNFILTERED result covers the whole project whatever the selection asked for`` () =
    // A project with selected classes but no `filterTemplate` runs in FULL: the RESULT is
    // the receipt, not the request.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectClasses(Set.ofList [ "OneClass" ]) ])
            (Map.ofList [ "ProjA", passed false ])
            noReportEvidence

    test <@ RunCoverage.covers "ProjA" (Some "AnyOtherClass") coverage @>
    test <@ RunCoverage.covers "ProjA" None coverage @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: a class-filtered pass covers ONLY the classes it ran`` () =
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectClasses(Set.ofList [ "Alpha" ]) ])
            (Map.ofList [ "ProjA", passed true ])
            noReportEvidence

    test <@ RunCoverage.covers "ProjA" (Some "Alpha") coverage @>
    test <@ not (RunCoverage.covers "ProjA" (Some "Beta") coverage) @>
    // Never a project-level red — an unparseable failure, or a timeout.
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: deferred, errored and zero-match results cover nothing — they ran no tests`` () =
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull; "ProjC", ProjectInFull ])
            (Map.ofList
                [ "ProjA", TestsDeferred "apphost not produced"
                  "ProjB", TestsErrored "no parseable report"
                  "ProjC", TestsNoMatch("no tests matched", TimeSpan.Zero) ])
            noReportEvidence

    test <@ not (RunCoverage.covers "ProjA" None coverage) @>
    test <@ not (RunCoverage.covers "ProjB" None coverage) @>
    test <@ not (RunCoverage.covers "ProjC" None coverage) @>
    test <@ coverage = Map.empty @>

[<Fact(Timeout = 10000)>]
let ``failuresOf: a TestsDeferred result is a Deferred-severity 'waiting on build' entry, never a failing Error`` () =
    // A deferred project's build artifact was not produced, so its tests did not run: it
    // must surface as a non-failing `Deferred` the verdict routes to Incomplete/exit 2.
    // As `errorWithDetail` (Error severity) it made the deploy preflight read a
    // build-ordering defer as a test failure.
    let deferred: TestResults =
        { Results = Map.ofList [ "ProjA", TestsDeferred "apphost not produced" ]
          Elapsed = TimeSpan.Zero }

    let entry = (failuresOf Map.empty deferred |> List.exactlyOne).Entry

    test <@ entry.Severity = FsHotWatch.ErrorLedger.Deferred @>
    test <@ FsHotWatch.ErrorLedger.ErrorEntry.isWaitingOnBuild entry @>
    // Never counted as a failure — in either warn-fail policy.
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing true entry) @>
    test <@ not (FsHotWatch.ErrorLedger.ErrorEntry.isFailing false entry) @>
    test <@ entry.Message.ToLowerInvariant().Contains "waiting on build" @>

    // The reclassification is surgical to the defer case, not a blanket downgrade.
    let failed: TestResults =
        { Results = Map.ofList [ "ProjB", TestsFailed("Some.Test FAILED", false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let realFailures = failuresOf Map.empty failed
    test <@ not realFailures.IsEmpty @>

    test
        <@
            realFailures
            |> List.forall (fun f -> f.Entry.Severity = FsHotWatch.ErrorLedger.Error)
        @>

    test
        <@
            realFailures
            |> List.forall (fun f -> FsHotWatch.ErrorLedger.ErrorEntry.isFailing true f.Entry)
        @>

[<Fact(Timeout = 10000)>]
let ``RunCoverage: a raw --filter passthrough with NO report evidence claims no coverage`` () =
    // `run-tests --filter <raw>` launches every project in full but hands the runner an
    // arbitrary filter string: `wasFiltered` is true and the selection names no classes,
    // so the LAUNCH REQUEST can say nothing about its reach. With no report to ask either,
    // claim nothing. This is the floor AUTOMATION-225 builds on, not a replacement for it.
    let coverage =
        RunCoverage.ofRun (Map.ofList [ "ProjA", ProjectInFull ]) (Map.ofList [ "ProjA", passed true ]) noReportEvidence

    test <@ coverage = Map.empty @>

// --- AUTOMATION-225: a green re-run retires the red it just disproved ---

/// A CTRF report in the shape fshw's runners really emit: summary counts and the per-test
/// array, both nested under `results`. The summary is DERIVED from the entries unless
/// `declaredTotal` overrides it, so a fixture cannot accidentally disagree with itself —
/// only deliberately, to pin the truncation guard.
let private ctrfReportWithTotal (declaredTotal: int option) (tests: (string * string) list) : string =
    let count status =
        tests |> List.filter (fun (_, s) -> s = status) |> List.length

    let entries =
        tests
        |> List.map (fun (name, status) ->
            sprintf """{"name":%s,"status":"%s","duration":1}""" (JsonSerializer.Serialize<string>(name)) status)
        |> String.concat ","

    sprintf
        """{"results":{"summary":{"tests":%d,"passed":%d,"failed":%d,"pending":0,"skipped":%d,"other":%d},"tests":[%s]}}"""
        (declaredTotal |> Option.defaultValue tests.Length)
        (count "passed")
        (count "failed")
        (count "skipped")
        (count "other")
        entries

let private ctrfReport (tests: (string * string) list) : string = ctrfReportWithTotal None tests

/// A completed run whose CTRF reports sit exactly where the real runner writes them:
/// `<repoRoot>/.fshw/test-runs/<runId>/<Project>.ctrf.json`. That directory is the only
/// route by which a run's per-test evidence reaches the ledger, so these tests drive the
/// whole path rather than handing `ofRun` a hand-made map.
let private testsFinishedEventWithReports
    (repoRoot: string)
    (reports: (string * string) list)
    (results: (string * TestResult) list)
    (launch: TestRunLaunch)
    =
    let runId = Guid.NewGuid()
    let dir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    for project, json in reports do
        File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), json)

    let started: TestRunStarted =
        { RunId = runId
          StartedAt = DateTime.UtcNow }

    let completed: TestRunCompleted =
        { RunId = runId
          TotalElapsed = TimeSpan.FromSeconds 1.0
          Outcome = Normal
          Results = Map.ofList results
          Verification = RunVerification.ofResults (Map.ofList results) }

    Custom(TestsFinished(started, completed, launch))

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-67 d223: a quarantined method passing in the exact CTRF receipt retires its red`` () =
    withTempDir "a67-d223-reconcile" (fun repoRoot ->
        let project = "Intelligence.Tests.Integration"

        let className =
            "Intelligence.Tests.Integration.AuthThrottleAcceptanceTests+AuthThrottleAcceptanceTests"

        let methodName =
            "NEWS-804 an unthrottled request answers the same for a real address and an unknown one"

        let fullName = $"%s{className}.%s{methodName}"

        let handler =
            create ":memory:" repoRoot (Some [ a125Config project ]) None None None None []

        let failedRun =
            testsFinishedEvent
                [ project, TestsFailed($"failed %s{fullName} (496ms)", false, TimeSpan.FromMilliseconds 496.0) ]
                (fullSuiteLaunch [ project ])

        let passingRun =
            testsFinishedEventWithReports
                repoRoot
                [ project, ctrfReport [ fullName, "passed" ] ]
                [ project, TestsPassed("passed", true, TimeSpan.FromMilliseconds 496.0) ]
                (filteredLaunch [ project, [ className ] ])

        let _ctx, statuses, ledger, final = driveRuns handler [ failedRun; passingRun ]

        test <@ List.isEmpty final.OutstandingFailures @>
        test <@ List.isEmpty (ledgerFilesOf ledger) @>

        match lastStatus statuses with
        | PluginStatus.Completed _ -> ()
        | other -> Assert.Fail($"the exact previously failing method passed, so its red must retire; got %A{other}"))

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-67: a different passing method in the quarantined class cannot erase the red`` () =
    withTempDir "a67-method-receipt" (fun repoRoot ->
        let project = "Intelligence.Tests.Integration"
        let className = "Intelligence.Tests.Integration.UserAuthTests+UserAuthTests"
        let failedMethod = "POST /api/user/login/request with valid email returns 200"
        let failedName = $"%s{className}.%s{failedMethod}"
        let siblingName = $"%s{className}.another method passed"

        let handler =
            create ":memory:" repoRoot (Some [ a125Config project ]) None None None None []

        let failedRun =
            testsFinishedEvent
                [ project, TestsFailed($"failed %s{failedName} (178ms)", false, TimeSpan.FromMilliseconds 178.0) ]
                (fullSuiteLaunch [ project ])

        let siblingOnlyRun =
            testsFinishedEventWithReports
                repoRoot
                [ project, ctrfReport [ siblingName, "passed" ] ]
                [ project, TestsPassed("passed", true, TimeSpan.FromMilliseconds 20.0) ]
                (filteredLaunch [ project, [ className ] ])

        let _ctx, statuses, _ledger, final = driveRuns handler [ failedRun; siblingOnlyRun ]

        test
            <@
                final.OutstandingFailures
                |> List.exists (fun red -> red.Method = Some failedMethod)
            @>

        match lastStatus statuses with
        | PluginStatus.Failed _ -> ()
        | other -> Assert.Fail($"a sibling pass did not contradict the failed method and must stay red; got %A{other}"))

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a report's PASSED classes are the receipt a raw-filter run is credited with`` () =
    let report =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.signs in", "passed" ]

    test <@ passedClassesOfReport report = Set.ofList [ "Acme.Tests.BrowserIntegrationTests" ] @>

    test
        <@
            passedTestsOfReport report = Set.ofList
                [ "Acme.Tests.BrowserIntegrationTests", "loads the dashboard"
                  "Acme.Tests.BrowserIntegrationTests", "signs in" ]
        @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-67 recall denominator requires every CTRF failed row promised by the summary`` () =
    let complete =
        ctrfReport
            [ "Acme.Tests.OneTests.first failure", "failed"
              "Acme.Tests.TwoTests.second failure", "failed" ]

    test
        <@
            failedTestsOfReport complete = Some
                [ "Acme.Tests.OneTests", "first failure"
                  "Acme.Tests.TwoTests", "second failure" ]
        @>

    let omittedRow =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},
             "tests":[{"name":"Acme.Tests.OneTests.first failure","status":"failed","duration":1}]}}"""

    test <@ failedTestsOfReport omittedRow = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 non-executing and shared-infrastructure runs cannot produce exact recall evidence`` () =
    withTempDir "a111-typed-run-evidence" (fun repoRoot ->
        let runId = Guid.NewGuid()
        let project = "Database.Tests"

        let errored =
            { Results = Map.ofList [ project, TestsErrored "host exited 139" ]
              Elapsed = TimeSpan.Zero }

        match failedTestsOfRun repoRoot runId errored with
        | Error reason -> test <@ reason.Contains "did not produce an executable test result" @>
        | Ok evidence -> failwithf "errored host produced exact evidence: %A" evidence

        let dir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
        Directory.CreateDirectory dir |> ignore

        let report =
            """{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Database.Tests.Query.loads","status":"failed","duration":1,"message":"Npgsql.NpgsqlException: connection failed","trace":"System.Net.Sockets.SocketException"}]}}"""

        File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), report)

        let failed =
            { Results = Map.ofList [ project, TestsFailed("failed", false, TimeSpan.Zero) ]
              Elapsed = TimeSpan.Zero }

        match failedTestsOfRun repoRoot runId failed with
        | Error reason -> test <@ reason.Contains "shared infrastructure" @>
        | Ok evidence -> failwithf "shared-infrastructure failures produced selector evidence: %A" evidence)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 one typed infrastructure exception invalidates a mixed failure sample`` () =
    let mixed =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.Contract.asserts","status":"failed","message":"expected true"},{"name":"Database.Tests.Query.loads","status":"failed","trace":"Npgsql.NpgsqlException: connection failed"}]}}"""

    test <@ sharedInfrastructureFailureOfReport mixed = Some "NpgsqlException" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 infrastructure tokens in names and assertion messages are not exception evidence`` () =
    let assertionOnly =
        """{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.NpgsqlExceptionExamples.render","status":"failed","message":"expected the text SocketException"}]}}"""

    test <@ sharedInfrastructureFailureOfReport assertionOnly = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 structured exception objects and stack arrays carry infrastructure evidence`` () =
    let structured =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Database.Tests.One.loads","status":"failed","exception":{"type":"Npgsql.NpgsqlException","message":"connection failed"}},{"name":"Database.Tests.Two.loads","status":"failed","stack":["at connector","System.Net.Sockets.SocketException: refused"]}]}}"""

    test <@ sharedInfrastructureFailureOfReport structured = Some "NpgsqlException/SocketException" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 an assertion trace naming a SocketException test class is not infrastructure evidence`` () =
    let ordinaryTrace =
        """{"results":{"summary":{"tests":1,"passed":0,"failed":1,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.SocketExceptionExamples.renders","status":"failed","trace":"at Api.Tests.SocketExceptionExamples.renders()"}]}}"""

    test <@ sharedInfrastructureFailureOfReport ordinaryTrace = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 longer identifiers containing exception type names are not infrastructure evidence`` () =
    let longerIdentifiers =
        """{"results":{"summary":{"tests":2,"passed":0,"failed":2,"pending":0,"skipped":0,"other":0},"tests":[{"name":"Api.Tests.One.asserts","status":"failed","trace":"at Npgsql.NpgsqlExceptionExamples.renders()"},{"name":"Api.Tests.Two.asserts","status":"failed","trace":"at System.Net.Sockets.SocketExceptionExamples.renders()"}]}}"""

    test <@ sharedInfrastructureFailureOfReport longerIdentifiers = None @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 malformed parseable CTRF structure becomes unknown evidence`` () =
    withTempDir "a111-malformed-structure" (fun repoRoot ->
        let runId = Guid.NewGuid()
        let project = "Database.Tests"
        let dir = Path.Combine(repoRoot, ".fshw", "test-runs", runId.ToString("N"))
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(Path.Combine(dir, project + ".ctrf.json"), """{"results":{"tests":{}}}""")

        let failed =
            { Results = Map.ofList [ project, TestsFailed("failed", false, TimeSpan.Zero) ]
              Elapsed = TimeSpan.Zero }

        match failedTestsOfRun repoRoot runId failed with
        | Error _ -> ()
        | Ok evidence -> failwithf "malformed CTRF produced exact evidence: %A" evidence)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-67 completion publishes real CTRF recall through check-reach IPC`` () =
    withTempDir "a67-recall-ipc" (fun repoRoot ->
        let project = "Acme.Tests"
        let reachedClass = "Acme.Tests.ReachedTests"
        let missedClass = "Acme.Tests.MissedTests"

        let handler =
            create ":memory:" repoRoot (Some [ a125Config project ]) None None None None []

        let report =
            ctrfReport [ $"%s{reachedClass}.fails", "failed"; $"%s{missedClass}.also fails", "failed" ]

        let launch =
            { fullSuiteLaunch [ project ] with
                WouldHaveRun = Some(Map.ofList [ project, ProjectClasses(Set.ofList [ reachedClass ]) ]) }

        let completion =
            testsFinishedEventWithReports
                repoRoot
                [ project, report ]
                [ project,
                  TestsFailed(
                      $"failed %s{reachedClass}.fails (1ms)\nfailed %s{missedClass}.also fails (1ms)",
                      false,
                      TimeSpan.FromMilliseconds 2.0
                  ) ]
                launch

        let pluginCtx, _, _ = makeTestPruneRecordingCtx ()

        let state =
            handler.Update pluginCtx handler.Init completion |> Async.RunSynchronously

        let command = handler.Commands |> List.find (fst >> (=) "check-reach") |> snd

        let commandCtx: FsHotWatch.PluginFramework.CommandCtx<TestPruneMsg> =
            { RepoRoot = repoRoot
              Log = ignore
              Post = ignore
              IsRunning = fun _ -> false
              ProjectGraph = pluginCtx.ProjectGraph }

        let json = command commandCtx state [||] |> Async.RunSynchronously

        match FsHotWatch.Cli.IpcParsing.parseCheckReach json with
        | FsHotWatch.Cli.IpcParsing.ReachRecorded reading ->
            test <@ reading.Recall = FsHotWatch.Cli.IpcParsing.FailureRecallMeasured(1, 2, 1.0, false) @>
        | other -> Assert.Fail($"the completed run must publish a measured recall sample, got %A{other}"))

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a class that RAN AND FAILED is claimed by nothing, not even its own passes`` () =
    // Per-class judgement on that class's own evidence: a class with a failure is not
    // vindicated by its siblings' greens, and the sibling IS vindicated by its own.
    let report =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.signs in", "failed"
              "Acme.Tests.SmokeTests.pings", "passed" ]

    test <@ passedClassesOfReport report = Set.ofList [ "Acme.Tests.SmokeTests" ] @>

    // An `other` status is an individually-ERRORED test — not a pass, so not a receipt.
    let errored =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.explodes", "other" ]

    test <@ passedClassesOfReport errored = Set.empty @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a class with nothing but skips proves nothing; a skip beside a pass is neutral`` () =
    let allSkipped =
        ctrfReport [ "Acme.Tests.BrowserIntegrationTests.disabled for now", "skipped" ]

    test <@ passedClassesOfReport allSkipped = Set.empty @>

    // Neutral, not disqualifying: the unfiltered arm this refines returns
    // `CoveredWholeProject` for a full run whose report contains skips, so blocking a
    // class on a skip here would be stricter than the path it refines.
    let mixed =
        ctrfReport
            [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed"
              "Acme.Tests.BrowserIntegrationTests.disabled for now", "skipped" ]

    test <@ passedClassesOfReport mixed = Set.ofList [ "Acme.Tests.BrowserIntegrationTests" ] @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: report evidence that is missing, unparseable or INCOMPLETE claims nothing`` () =
    // Fail CLOSED, four ways: a bug here must degrade to "the red stays red".
    test <@ passedClassesOfReport "" = Set.empty @>
    test <@ passedClassesOfReport "not json at all" = Set.empty @>

    // A per-test array with NO summary block was truncated or never flushed, so
    // completeness cannot be checked and it is not evidence.
    test <@ passedClassesOfReport """{"results":{"tests":[{"name":"A.BTests.c","status":"passed"}]}}""" = Set.empty @>

    // The dangerous case: a real report OMITS per-test entries for tests that threw a raw,
    // non-assertion exception while still counting them in the summary. Counting is the
    // only way to see the omission — a class could otherwise look all-green while one of
    // its tests exploded.
    let truncated =
        ctrfReportWithTotal
            (Some 3)
            [ "Acme.Tests.BrowserIntegrationTests.one", "passed"
              "Acme.Tests.BrowserIntegrationTests.two", "passed" ]

    test <@ passedClassesOfReport truncated = Set.empty @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a raw --filter run is credited with the classes its own report shows passing`` () =
    let evidence = Map.ofList [ "ProjA", Set.ofList [ "BrowserIntegrationTests" ] ]

    let coverage =
        RunCoverage.ofRun (Map.ofList [ "ProjA", ProjectInFull ]) (Map.ofList [ "ProjA", passed true ]) evidence

    test <@ RunCoverage.covers "ProjA" (Some "BrowserIntegrationTests") coverage @>

    // A class the report never mentions is untouched, and a PROJECT-level red (timeout,
    // errored, unparseable failure) still needs a full run.
    test <@ not (RunCoverage.covers "ProjA" (Some "SomeOtherTests") coverage) @>
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>

    // Still a FILTERED scope, never a whole-suite claim.
    test <@ not (RunCoverage.coversWholeSuite [ "ProjA" ] coverage) @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: report evidence never speaks for a project the run did not LAUNCH`` () =
    // The AUTOMATION-125 laundering vector re-checked against report evidence: ProjA was
    // impact-SKIPPED, and a report bearing its name must not resurrect it. The selection
    // is consulted BEFORE any evidence, and absence from it is final.
    let evidence = Map.ofList [ "ProjA", Set.ofList [ "ProjATests" ] ]

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjB", ProjectInFull ])
            (Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ])
            evidence

    test <@ not (RunCoverage.covers "ProjA" (Some "ProjATests") coverage) @>
    test <@ not (RunCoverage.covers "ProjA" None coverage) @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225: a TIMED-OUT project is credited with nothing, report or no report`` () =
    // Whatever a killed process managed to flush is not a receipt for anything.
    let evidence = Map.ofList [ "ProjA", Set.ofList [ "ProjATests" ] ]

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull ])
            (Map.ofList [ "ProjA", TestsTimedOut("killed", TimeSpan.FromSeconds 60.0, true, TimeSpan.FromSeconds 60.0) ])
            evidence

    test <@ coverage = Map.empty @>

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-225: a filtered re-run that PASSES retires the red it re-ran — and only that one`` () =
    // The deadlock, end to end. An environmental failure (`ERR_NETWORK_IO_SUSPENDED` after
    // a machine suspend) reds a class; `test-rerun --filter-class '*BrowserIntegrationTests'`
    // re-runs it and it passes — and the red STAYS, because the launch records every
    // project as `ProjectInFull` and the raw filter string's reach is unknowable. Sticky
    // forever; it blocked a production deploy three times.
    let repoRoot =
        Path.Combine(Path.GetTempPath(), "fshw-a225-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(repoRoot) |> ignore

    try
        let handler =
            create ":memory:" repoRoot (Some [ a125Config "ProjA" ]) None None None None []

        // A full run reds TWO classes: the environmental one, and an unrelated one.
        let fullRun =
            testsFinishedEvent
                [ "ProjA",
                  TestsFailed(
                      "failed Acme.Tests.BrowserIntegrationTests.loads the dashboard (12ms)\nfailed Acme.Tests.LedgerTests.balances (3ms)",
                      false,
                      TimeSpan.FromSeconds 1.0
                  ) ]
                (fullSuiteLaunch [ "ProjA" ])

        let _c1, _s1, _l1, afterFull = driveRuns handler [ fullRun ]

        test
            <@
                afterFull.OutstandingFailures |> List.map (fun f -> f.Class) |> List.sort = [ Some
                                                                                                  "Acme.Tests.BrowserIntegrationTests"
                                                                                              Some
                                                                                                  "Acme.Tests.LedgerTests" ]
            @>

        // A `ProjectInFull` selection plus an opaque filter string, exactly as
        // `commandForceRun` builds it: only the run's own report knows what executed.
        let filteredRerun =
            testsFinishedEventWithReports
                repoRoot
                [ "ProjA", ctrfReport [ "Acme.Tests.BrowserIntegrationTests.loads the dashboard", "passed" ] ]
                [ "ProjA", passed true ]
                (fullSuiteLaunch [ "ProjA" ])

        let _c2, _s2, ledger, afterRerun = driveRuns handler [ fullRun; filteredRerun ]

        // The class that re-ran and passed is retired ...
        test
            <@
                not (
                    afterRerun.OutstandingFailures
                    |> List.exists (fun f -> f.Class = Some "Acme.Tests.BrowserIntegrationTests")
                )
            @>

        // ... and the red the re-run never touched is still standing, in the state AND in
        // the ledger the verdict reads.
        test <@ afterRerun.OutstandingFailures |> List.map (fun f -> f.Class) = [ Some "Acme.Tests.LedgerTests" ] @>

        let ledgerMessages =
            ledger
            |> Seq.collect (fun kv -> kv.Value)
            |> Seq.map (fun e -> e.Message)
            |> Seq.toList

        test <@ ledgerMessages |> List.exists (fun m -> m.Contains "LedgerTests") @>
        test <@ not (ledgerMessages |> List.exists (fun m -> m.Contains "BrowserIntegrationTests")) @>
    finally
        try
            Directory.Delete(repoRoot, true)
        with _ ->
            ()

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-225: a filtered re-run whose report is missing or garbage retires NOTHING`` () =
    // Fail closed at the top of the stack too: a defect here must cost a stuck red, never
    // a false green.
    let repoRoot =
        Path.Combine(Path.GetTempPath(), "fshw-a225-closed-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(repoRoot) |> ignore

    try
        let handler =
            create ":memory:" repoRoot (Some [ a125Config "ProjA" ]) None None None None []

        let fullRun =
            testsFinishedEvent
                [ "ProjA",
                  TestsFailed(
                      "failed Acme.Tests.BrowserIntegrationTests.loads the dashboard (12ms)",
                      false,
                      TimeSpan.FromSeconds 1.0
                  ) ]
                (fullSuiteLaunch [ "ProjA" ])

        // No report at all.
        let noReport =
            testsFinishedEventWithReports repoRoot [] [ "ProjA", passed true ] (fullSuiteLaunch [ "ProjA" ])

        let _c1, _s1, _l1, afterNoReport = driveRuns handler [ fullRun; noReport ]

        test
            <@
                afterNoReport.OutstandingFailures
                |> List.exists (fun f -> f.Class = Some "Acme.Tests.BrowserIntegrationTests")
            @>

        // An unparseable one.
        let garbage =
            testsFinishedEventWithReports
                repoRoot
                [ "ProjA", "{ this is not a ctrf report" ]
                [ "ProjA", passed true ]
                (fullSuiteLaunch [ "ProjA" ])

        let _c2, _s2, _l2, afterGarbage = driveRuns handler [ fullRun; garbage ]

        test
            <@
                afterGarbage.OutstandingFailures
                |> List.exists (fun f -> f.Class = Some "Acme.Tests.BrowserIntegrationTests")
            @>
    finally
        try
            Directory.Delete(repoRoot, true)
        with _ ->
            ()

// --- OutstandingFailure.carry: the ledger algebra (unit) ---

let private redIn (project: string) (cls: string option) =
    { Project = project
      Class = cls
      Method = cls |> Option.map (fun _ -> "failed method")
      File = $"<tests/%s{project}>"
      Entry = FsHotWatch.ErrorLedger.ErrorEntry.errorWithDetail $"%s{project} failed" "output" }

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.quarantine automatically adds prior red classes to the next impact selection`` () =
    let affected = Map.ofList [ "ProjA", [ "FreshTests" ]; "ProjB", [ "OtherTests" ] ]

    let quarantined =
        OutstandingFailure.quarantine
            (Set.ofList [ "ProjA"; "ProjB"; "ProjC" ])
            [ redIn "ProjA" (Some "PriorRedTests")
              redIn "ProjC" (Some "OnlyPriorRedTests") ]
            affected

    test <@ quarantined.["ProjA"] |> Set.ofList = Set.ofList [ "FreshTests"; "PriorRedTests" ] @>
    test <@ quarantined.["ProjB"] = [ "OtherTests" ] @>
    test <@ quarantined.["ProjC"] = [ "OnlyPriorRedTests" ] @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.quarantine promotes an unknown-scope prior red to the whole project`` () =
    let affected = Map.ofList [ "ProjA", [ "FreshTests" ]; "ProjB", [ "OtherTests" ] ]

    let quarantined =
        OutstandingFailure.quarantine
            (Set.ofList [ "ProjA"; "ProjB" ])
            [ redIn "ProjA" (Some "PriorRedTests"); redIn "ProjA" None ]
            affected

    test <@ List.isEmpty quarantined.["ProjA"] @>
    test <@ quarantined.["ProjB"] = [ "OtherTests" ] @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-87 browser red survives a following edit whose graph selects zero tests`` () =
    let project = "Intelligence.Tests.Integration"

    let browserClass =
        "Intelligence.Tests.Integration.Web.BrowserIntegrationTests+BrowserIntegrationTests"

    // The historical AUTOMATION-87 edit changed only browser-test waits. Its graph
    // selection was empty; that must not let an already-red browser class disappear.
    let graphSelectionAfterBrowserEdit: Map<string, string list> = Map.empty

    let selected =
        OutstandingFailure.quarantine
            (Set.singleton project)
            [ redIn project (Some browserClass) ]
            graphSelectionAfterBrowserEdit

    test <@ selected = Map.ofList [ project, [ browserClass ] ] @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: keeps an uncovered red, drops a covered-and-passed one`` () =
    let prior = [ redIn "ProjA" (Some "ProjATests"); redIn "ProjB" None ]

    // Covered ONLY ProjB, in full, and found nothing.
    let coverage: RunCoverage = Map.ofList [ "ProjB", CoveredWholeProject ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA"; "ProjB" ]) coverage Map.empty [] prior

    test <@ carried |> List.map (fun f -> f.Project) = [ "ProjA" ] @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: a covered project that failed AGAIN keeps exactly one red`` () =
    let prior = [ redIn "ProjA" (Some "ProjATests") ]
    let coverage: RunCoverage = Map.ofList [ "ProjA", CoveredWholeProject ]
    let found = [ redIn "ProjA" (Some "ProjATests") ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA" ]) coverage Map.empty found prior

    // Superseded by this run's own evidence: one entry, not two. Reds must not accumulate.
    test <@ carried.Length = 1 @>

[<Fact(Timeout = 10000)>]
let ``OutstandingFailure.carry: a red for a project no longer configured is pruned, never wedged`` () =
    // A project dropped from `tests.projects` can never be covered again, so retaining its
    // red is a permanent stuck-red no command can clear.
    let prior = [ redIn "Removed" None; redIn "ProjA" None ]

    let carried =
        OutstandingFailure.carry (Set.ofList [ "ProjA" ]) RunCoverage.none Map.empty [] prior

    test <@ carried |> List.map (fun f -> f.Project) = [ "ProjA" ] @>

// --- The task cache must not launder it either ---

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125: no cache participation while a red is outstanding`` () =
    // Two roads to the same laundered green. A BuildCompleted that HITS a cached green
    // skips the handler, so no run happens and the red is replayed away. And a run that
    // passed everything it ran while carrying an uncovered red reports a FAILED terminal,
    // which written under a content merkle replays on a tree that has since been fixed.
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
                  Verification = Ran RunScope.FullSuite },
                fullSuiteLaunch [ "ProjA" ]
            )
        )

    let keyFor (hasOutstanding: bool) (event: PluginEvent<TestPruneMsg>) =
        cacheKeyFor
            (fun () -> "symbols")
            (fun () -> None)
            (fun () -> None)
            (fun () -> "same-structure")
            (fun () -> None)
            (fun () -> hasOutstanding)
            (fun () -> true)
            event

    // A clean plugin keeps the green fast-path ...
    test <@ (keyFor false (BuildCompleted BuildSucceeded)).IsSome @>
    test <@ (keyFor false allPassed).IsSome @>

    // ... and an outstanding red means no replay and no write, on either arm.
    test <@ (keyFor true (BuildCompleted BuildSucceeded)).IsNone @>
    test <@ (keyFor true allPassed).IsNone @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125: confirm still rejects a filtered green as UnearnedScope`` () =
    // The fix must not weaken the gate it stands beside: the filtered re-run above still
    // classifies as a SUBSET, and a merge verdict built on a subset is `UnearnedScope`.
    let configs = [ a125Config "ProjA"; a125Config "ProjB" ]

    let filteredResults: TestResults =
        { Results = Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ]
          Elapsed = TimeSpan.FromSeconds 1.0 }

    // ProjA never ran and ProjB was launched under a CLASS FILTER, so the run's honest
    // reach is "some of ProjB" and nothing else.
    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectClasses(Set.ofList [ "SomeTests" ]) ])
            filteredResults.Results
            noReportEvidence

    match scopeOf (configs |> List.map (fun c -> c.Project)) coverage with
    | ScopeFiltered(ran, total) ->
        test <@ ran = 1 && total = 2 @>

        let outcome =
            FsHotWatch.Cli.CheckVerdict.verdict
                FsHotWatch.Cli.CheckVerdict.Confirmation
                { PluginStatuses = Map.empty
                  FailingDiagnostics = 0
                  UnattributableDiagnostics = 0
                  WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
                  RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
                  Coverage = FsHotWatch.Cli.IpcParsing.Complete
                  Scope = FsHotWatch.Cli.IpcParsing.ImpactFiltered(ran, total) }

        test <@ FsHotWatch.Cli.CheckVerdict.exitCode outcome = 3 @>
    | other -> Assert.Fail($"a run with a filtered project is not a full-suite scope, got %A{other}")

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-125 x 129: a RAW-filter run with no report evidence claims NO coverage, so the gate sees no scope at all``
    ()
    =
    // Sharper than the case above: a raw filter's reach the LAUNCH REQUEST cannot express,
    // with no report to ask either, credits the run with NOTHING. That projects to
    // `ScopeNone`, not `ScopeFiltered`, and the CLI refuses it in either mode — strictly
    // safer than `classifyRunScope`, which called this a SUBSET (still exit 3, weaker
    // reason).
    let configs = [ a125Config "ProjA"; a125Config "ProjB" ]

    let rawFiltered: TestResults =
        { Results = Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ]
          Elapsed = TimeSpan.FromSeconds 1.0 }

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ])
            rawFiltered.Results
            noReportEvidence

    test <@ scopeOf (configs |> List.map (fun c -> c.Project)) coverage = ScopeNone 2 @>

    // NoTestsRun is UnearnedScope in the inner loop as well as in `confirm`.
    let noTestsRan: FsHotWatch.Cli.CheckVerdict.CheckInputs =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
          Coverage = FsHotWatch.Cli.IpcParsing.Complete
          Scope = FsHotWatch.Cli.IpcParsing.NoTestsRun FsHotWatch.Cli.IpcParsing.NoTestsReason.Unstated }

    let confirmed =
        FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.Confirmation noTestsRan

    let inner =
        FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.InnerLoop noTestsRan

    test <@ FsHotWatch.Cli.CheckVerdict.exitCode confirmed = 3 @>
    test <@ FsHotWatch.Cli.CheckVerdict.exitCode inner = 3 @>

[<Fact(Timeout = 10000)>]
let ``AUTOMATION-225 x 112: a raw-filter run WITH evidence is a FILTERED scope, and confirm still refuses it`` () =
    // Crediting a raw-filter run with what its report proves makes the scope projection
    // say what actually ran instead of "nothing ran". That is MORE evidence, not a weaker
    // gate: `confirm` still demands a full-suite scope and still exits 3. And `scopeOf`
    // stays a pure projection of `RunCoverage`, so the ledger and the verdict read the
    // same coverage.
    let configs = [ a125Config "ProjA"; a125Config "ProjB" ]

    let evidence = Map.ofList [ "ProjB", Set.ofList [ "BrowserIntegrationTests" ] ]

    let coverage =
        RunCoverage.ofRun
            (Map.ofList [ "ProjA", ProjectInFull; "ProjB", ProjectInFull ])
            (Map.ofList [ "ProjA", impactSkipped; "ProjB", passed true ])
            evidence

    test <@ scopeOf (configs |> List.map (fun c -> c.Project)) coverage = ScopeFiltered(1, 2) @>

    let filtered: FsHotWatch.Cli.CheckVerdict.CheckInputs =
        { PluginStatuses = Map.empty
          FailingDiagnostics = 0
          UnattributableDiagnostics = 0
          WaitingOnBuild = FsHotWatch.Cli.CheckVerdict.BuildWait.NotWaiting
          RunnerAborted = FsHotWatch.Cli.CheckVerdict.RunnerAbort.NoAbort
          Coverage = FsHotWatch.Cli.IpcParsing.Complete
          Scope = FsHotWatch.Cli.IpcParsing.ImpactFiltered(1, 2) }

    let confirmed =
        FsHotWatch.Cli.CheckVerdict.verdict FsHotWatch.Cli.CheckVerdict.Confirmation filtered

    test <@ FsHotWatch.Cli.CheckVerdict.exitCode confirmed = 3 @>

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-125: a test run does not erase the unanalysable-file warning (AUTOMATION-113)`` () =
    // The same defect on a different diagnostic. The `TestsFinished` ledger rewrite
    // cleared this plugin's whole slice, so the FIRST test run after an analysis failure
    // dropped the warning that is supposed to deny the check its green verdict: the file
    // kept forcing full-suite runs (state) but nothing told anyone (ledger). The warning
    // leaves only when the CONDITION clears — the file analyses cleanly.
    //
    // The broken file is written to DISK (AUTOMATION-303 case 4). A path that does not
    // exist has no condition left to discharge and is pruned, so a fixture naming an
    // imaginary file would assert this invariant over an entry that is now correctly
    // dropped — and pass or fail for the wrong reason.
    withTempDir "tp-a125-unanalysable" (fun tmpDir ->
        let handler =
            create ":memory:" tmpDir (Some [ a125Config "ProjA" ]) None None None None []

        let brokenFile = Path.Combine(tmpDir, "src", "Broken.fs")
        Directory.CreateDirectory(Path.GetDirectoryName brokenFile) |> ignore
        File.WriteAllText(brokenFile, "module Broken\n")

        let broken =
            { RelPath = "src/Broken.fs"
              File = brokenFile
              Reason = "FS3520: unexpected doc comment" }

        let stateWithUnanalysable =
            { handler.Init with
                UnanalyzableFiles = Map.ofList [ broken.RelPath, broken ] }

        let ctx, _statuses, ledger = makeTestPruneRecordingCtx ()

        // The strongest green there is: a full-suite run in which everything passes.
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

        // ... and it DOES go once the file analyses cleanly and the state drops it.
        let ctx2, _statuses2, ledger2 = makeTestPruneRecordingCtx ()

        handler.Update ctx2 handler.Init greenRun |> Async.RunSynchronously |> ignore

        test <@ ledger2.Count = 0 @>)

[<Fact(Timeout = 10000)>]
let ``RunCoverage.coversWholeSuite: only every project, each in FULL, is a whole-suite claim`` () =
    // Answered from what the run EXECUTED, so there is one notion of scope in the system —
    // the same one the ledger clears by — rather than a parallel one that can drift.
    let projects = [ "ProjA"; "ProjB" ]

    let everything: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredWholeProject ]

    let oneFiltered: RunCoverage =
        Map.ofList [ "ProjA", CoveredWholeProject; "ProjB", CoveredClasses(Set.ofList [ "X" ]) ]

    let oneMissing: RunCoverage = Map.ofList [ "ProjA", CoveredWholeProject ]

    test <@ RunCoverage.coversWholeSuite projects everything @>
    // A filtered project covered LESS than the suite, whatever its result said; so did a
    // skipped one; and a run of nothing is never evidence of everything.
    test <@ not (RunCoverage.coversWholeSuite projects oneFiltered) @>
    test <@ not (RunCoverage.coversWholeSuite projects oneMissing) @>
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
    // consumer outside the handler must be able to read the second, or it invents its own
    // answer to "what did this run cover?" and the two drift.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let filteredRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let _ctx, _statuses, _ledger, final = driveRuns handler [ filteredRun ]

    // Every project produced a "passed" result, and yet the run covered only ProjB's one
    // class. That gap is the whole ticket, and it must be legible from state.
    test <@ final.LastResults.IsSome @>
    test <@ RunCoverage.coveredProjects final.LastCoverage = Set.ofList [ "ProjB" ] @>
    test <@ not (RunCoverage.coversWholeSuite [ "ProjA"; "ProjB" ] final.LastCoverage) @>

[<Fact(Timeout = 20000)>]
let ``a queued narrow drain cannot replace the full-suite receipt exposed to the verdict writer`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let fullRunId =
        match fullRun with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    let _ctx, statuses, _ledger, final = driveRuns handler [ fullRun; narrowRun ]

    let receipt = final.EvidenceReceipt.Value
    test <@ receipt.RunId = fullRunId @>
    test <@ RunCoverage.coversWholeSuite [ "ProjA"; "ProjB" ] receipt.Coverage @>

    let scopeCommand = handler.Commands |> List.find (fst >> (=) "test-scope") |> snd

    let commandCtx: FsHotWatch.PluginFramework.CommandCtx<TestPruneMsg> =
        { RepoRoot = "/tmp"
          Log = ignore
          Post = ignore
          IsRunning = fun _ -> false
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    let report =
        scopeCommand commandCtx final [||]
        |> Async.RunSynchronously
        |> FsHotWatch.Cli.IpcParsing.parseTestRunReport

    test <@ report.RunId = Some fullRunId @>
    test <@ report.Scope = FsHotWatch.Cli.IpcParsing.FullSuite 2 @>

    match lastStatus statuses with
    | PluginStatus.Completed(_, verdict) -> test <@ verdict.Summary.Contains("2 projects") @>
    | other -> Assert.Fail($"latest run status must remain independently visible, got %A{other}")

[<Fact(Timeout = 20000)>]
let ``test-scope declares EVERY run the session completed, not only the one the receipt names`` () =
    // AUTOMATION-533. The receipt deliberately holds ONE run — the full-suite one, which
    // a later narrow drain may not downgrade (the test above). That is right for grading
    // and wrong for reporting: both runs wrote a directory, both hold reports, and a
    // reader looking for their own tests in the receipt's directory alone finds only
    // what the last batch happened to cover.
    //
    // So the reply carries both questions. `runId` is what this verdict was graded from;
    // `runIds` is everything this session ran, and it is what lets a check name every
    // batch it produced instead of only the last.
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let runIdOf event =
        match event with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    let fullRunId = runIdOf fullRun
    let narrowRunId = runIdOf narrowRun

    let _ctx, _statuses, _ledger, final = driveRuns handler [ fullRun; narrowRun ]

    let scopeCommand = handler.Commands |> List.find (fst >> (=) "test-scope") |> snd

    let commandCtx: FsHotWatch.PluginFramework.CommandCtx<TestPruneMsg> =
        { RepoRoot = "/tmp"
          Log = ignore
          Post = ignore
          IsRunning = fun _ -> false
          ProjectGraph = FsHotWatch.PluginFramework.ProjectGraphAccessor.none }

    let report =
        scopeCommand commandCtx final [||]
        |> Async.RunSynchronously
        |> FsHotWatch.Cli.IpcParsing.parseTestRunReport

    // Graded from the full-suite run, as before — this must not have moved.
    test <@ report.RunId = Some fullRunId @>

    // ...and the narrow drain, whose directory holds the only reports that batch wrote,
    // is no longer invisible. Newest first.
    test <@ report.SessionRuns = [ narrowRunId; fullRunId ] @>

[<Fact(Timeout = 20000)>]
let ``a queued manual filtered force-run clears the prior full receipt when its FIFO drain launches`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowRun =
        testsFinishedEvent
            [ "ProjA", impactSkipped; "ProjB", passed true ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let mutable claims = [ LocalSlotBusy; SharedClaimed ]
    let recordingCtx, _, _ = makeTestPruneRecordingCtx ()

    let ctx =
        { recordingCtx with
            RunExclusiveShared =
                fun _ _ _ _ _ ->
                    match claims with
                    | claim :: later ->
                        claims <- later
                        claim
                    | [] -> failwith "unexpected extra test-slot claim" }

    let fullState = handler.Update ctx handler.Init fullRun |> Async.RunSynchronously
    test <@ fullState.EvidenceReceipt.IsSome @>

    let queuedReply = System.Threading.Tasks.TaskCompletionSource<string>()

    let queuedState =
        handler.Update
            ctx
            fullState
            (Custom(RunTestsRequested([ a125Config "ProjB" ], Some "FullyQualifiedName~ProjBTests", queuedReply)))
        |> Async.RunSynchronously

    test <@ queuedState.EvidenceReceipt.IsSome @>
    test <@ queuedState.QueuedCommandRuns.Length = 1 @>

    // `narrowRun` completes the pre-existing in-flight run. Its terminal handler must
    // dequeue and LAUNCH the explicit manual filter as a new top-level receipt boundary.
    let drainedState =
        handler.Update ctx queuedState narrowRun |> Async.RunSynchronously

    test <@ drainedState.EvidenceReceipt.IsNone @>
    test <@ drainedState.QueuedCommandRuns.IsEmpty @>
    test <@ claims.IsEmpty @>

[<Fact(Timeout = 15000)>]
let ``manual run reply terminates when its shared test host cannot start`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let reply = System.Threading.Tasks.TaskCompletionSource<string>()
    let mutable posted: TestPruneMsg option = None
    let recordingCtx, _, _ = makeTestPruneRecordingCtx ()

    let ctx =
        { recordingCtx with
            Post = fun message -> posted <- Some message
            RunExclusiveShared =
                fun _ _ _ _ failureMessage ->
                    posted <- Some(failureMessage (InvalidOperationException("host start fault")))
                    SharedClaimed }

    let claimedState =
        handler.Update ctx handler.Init (Custom(RunTestsRequested([ a125Config "ProjA" ], None, reply)))
        |> Async.RunSynchronously

    test <@ posted.IsSome @>

    let finalState =
        handler.Update ctx claimedState (Custom posted.Value) |> Async.RunSynchronously

    test <@ reply.Task.Wait 5000 @>
    test <@ reply.Task.Result.Contains("test host could not start") @>
    test <@ reply.Task.Result.Contains("host start fault") @>
    test <@ finalState.PendingRerun @>

[<Fact(Timeout = 20000)>]
let ``a run receipt keeps its launch seeds when a later cohort flushes while it runs`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let seedsA = [ "Lib.A.changed" ]
    let seedsB = [ "Lib.B.changed" ]

    let launch =
        { fullSuiteLaunch [ "ProjA" ] with
            Seeds = seedsA }

    let run = testsFinishedEvent [ "ProjA", passed false ] launch

    // This is the state at completion after BatchChecked has flushed cohort B while
    // run A held the test slot. Every other receipt input already comes from `launch`.
    let stateAtCompletion = { handler.Init with LastSeeds = seedsB }
    let ctx, _, _ = makeTestPruneRecordingCtx ()
    let final = handler.Update ctx stateAtCompletion run |> Async.RunSynchronously
    let receipt = final.EvidenceReceipt.Value

    let runId =
        match run with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    test <@ receipt.RunId = runId @>
    test <@ RunCoverage.coversWholeSuite [ "ProjA" ] receipt.Coverage @>
    test <@ receipt.Seeds = seedsA @>

[<Fact(Timeout = 20000)>]
let ``a zero-selection receipt carries the previous seeds captured at launch`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA" ]) None None None None []

    let previousSeeds = [ "Lib.PreviouslyVerified.changed" ]

    let zeroLaunch =
        { emptyLaunch with
            Seeds = previousSeeds
            ZeroSelection = ZeroSelection.AlreadyVerified }

    let run = testsFinishedEvent [] zeroLaunch

    let stateAtCompletion =
        { handler.Init with
            LastSeeds = [ "Lib.Later.changed" ] }

    let ctx, _, _ = makeTestPruneRecordingCtx ()
    let final = handler.Update ctx stateAtCompletion run |> Async.RunSynchronously
    let receipt = final.EvidenceReceipt.Value

    test <@ receipt.Seeds = previousSeeds @>
    test <@ receipt.ZeroSelection = ZeroSelection.AlreadyVerified @>

[<Fact(Timeout = 20000)>]
let ``a queued narrow failure remains red while the earlier full-suite receipt is retained`` () =
    let handler =
        create ":memory:" "/tmp" (Some [ a125Config "ProjA"; a125Config "ProjB" ]) None None None None []

    let fullRun =
        testsFinishedEvent [ "ProjA", passed false; "ProjB", passed false ] (fullSuiteLaunch [ "ProjA"; "ProjB" ])

    let narrowFailure =
        testsFinishedEvent
            [ "ProjA", impactSkipped
              "ProjB", TestsFailed("boom", true, TimeSpan.FromSeconds 1.0) ]
            (filteredLaunch [ "ProjB", [ "ProjBTests" ] ])

    let fullRunId =
        match fullRun with
        | Custom(TestsFinished(_, completed, _)) -> completed.RunId
        | _ -> failwith "expected TestsFinished"

    let _ctx, statuses, _ledger, final = driveRuns handler [ fullRun; narrowFailure ]

    test <@ final.EvidenceReceipt |> Option.map (fun receipt -> receipt.RunId) = Some fullRunId @>

    match lastStatus statuses with
    | PluginStatus.Failed _ -> ()
    | other -> Assert.Fail($"the later narrow failure must remain red, got %A{other}")

// `verificationOf` replaces a boolean that could not tell "no project was selected" from
// "every project matched nothing": for an empty result set it answered `false` to both
// "did every project match nothing?" and "did anything run?", so an empty run reached the
// CLI looking like a real one and was reported as `Tests passed`, exit 0.

[<Fact>]
let ``verificationOf tells an EMPTY run apart from one that matched nothing — the bool could not`` () =
    let zero = TestsNoMatch("Zero tests ran", TimeSpan.Zero)
    let realPass = TestsPassed("Passed! total: 4", true, TimeSpan.Zero)

    let emptyRun =
        { Results = Map.empty
          Elapsed = TimeSpan.Zero }

    let allZero =
        { Results = Map.ofList [ "A", zero; "B", zero ]
          Elapsed = TimeSpan.Zero }

    let ran =
        { Results = Map.ofList [ "A", zero; "B", realPass ]
          Elapsed = TimeSpan.Zero }

    test <@ verificationOf emptyRun.Results = NoProjectsSelected @>
    test <@ verificationOf allZero.Results = AllZeroMatch 2 @>
    // `Ran`, and PARTIAL: project A matched nothing, which `wasFiltered` reports as
    // filtered, so this run cannot claim the whole suite. Under the bool the scope rode
    // alongside as a separate field with nothing tying it to having run.
    test <@ verificationOf ran.Results = Ran RunScope.Partial @>

    // The bool collapses the first two cases in a way that loses the empty one entirely.
    test <@ allZeroMatch emptyRun = false @>
    test <@ allZeroMatch allZero = true @>

// `RunVerification.verifiedNothing` and the wire tokens are core API, pinned in
// EventTests.fs. Duplicating them here meant editing both files in lockstep.

// ── ProjectReference Include with an MSBuild property ────────────────────────
//
// `directReferences` resolved an `Include` by string-concatenating it onto the project
// directory, with no MSBuild property expansion, so a computed reference —
//
//   <SomeProject>$(MSBuildThisFileDirectory)../../Other/Other.fsproj</SomeProject>
//   <ProjectReference Include="$(SomeProject)" Condition="..." />
//
// — became a search for a file literally named `$(SomeProject)`. The gate then answered
// `InputsUndeterminable` and deferred the whole test run as "waiting on build", forever,
// on a perfectly fresh tree. Observed against TestPrune's own test project, which
// computes its optional Falco.UnionRoutes reference exactly this way.
//
// The fail-CLOSED behaviour must survive: an `Include` that genuinely cannot be resolved
// still has to be an error, or this reintroduces the fail-open hole the module exists to
// close. Both directions are asserted below.

let private writeProj (path: string) (contents: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path: string) |> ignore
    File.WriteAllText(path, contents)

[<Fact(Timeout = 15000)>]
let ``ProjectReference Include is resolved through MSBuild properties`` () =
    withTempDir "af-msbuild-prop" (fun tmpDir ->
        let other = Path.Combine(tmpDir, "Other", "Other.fsproj")
        writeProj other "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

        // The shape that broke: the Include is a property, defined in the same file in
        // terms of another well-known MSBuild property.
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <PropertyGroup>\n\
             \    <OtherProject>$(MSBuildThisFileDirectory)../Other/Other.fsproj</OtherProject>\n\
             \  </PropertyGroup>\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(OtherProject)\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs ->
            // The real file, not a literal `$(OtherProject)`.
            test <@ refs |> List.exists (fun r -> Path.GetFileName r = "Other.fsproj") @>
        | Error e -> Assert.Fail($"a property-computed ProjectReference must resolve, got: %s{e}"))

[<Fact(Timeout = 15000)>]
let ``an unresolvable ProjectReference still fails closed`` () =
    // The positive control for the test above: if expansion made every reference
    // resolvable, the gate would answer "fresh" for a project whose inputs it cannot
    // determine.
    withTempDir "af-msbuild-missing" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"../Nope/Nope.fsproj\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs -> Assert.Fail($"a missing ProjectReference must NOT resolve, got: %A{refs}")
        | Error _ -> ())

[<Fact(Timeout = 15000)>]
let ``a CONDITIONAL ProjectReference to a missing project is optional, not undeterminable`` () =
    // The one trade-off this fix makes, so it gets its own test. A project guards an
    // optional sibling checkout with a Condition, and on a machine that has not cloned it
    // the file is absent — deferring the whole run over a reference the build correctly
    // ignores is the thing being fixed.
    withTempDir "af-cond-missing" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <PropertyGroup>\n\
             \    <Optional>$(MSBuildThisFileDirectory)../Absent/Absent.fsproj</Optional>\n\
             \    <HaveIt Condition=\"Exists('$(Optional)')\">true</HaveIt>\n\
             \  </PropertyGroup>\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(Optional)\" Condition=\"'$(HaveIt)' == 'true'\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs ->
            // Skipped, not resolved, and not a phantom entry either.
            test <@ refs |> List.forall (fun r -> Path.GetFileName r <> "Absent.fsproj") @>
        | Error e -> Assert.Fail($"a conditional reference to an absent project must not be an error, got: %s{e}"))

[<Fact(Timeout = 15000)>]
let ``an UNCONDITIONAL reference to an unexpandable property still fails closed`` () =
    // The other side of that trade-off. Nothing defines `$(Nope)`, so it stays unexpanded
    // and the path cannot exist — and with no Condition marking it optional, the gate must
    // still refuse.
    withTempDir "af-unexpandable" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(Nope)\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs -> Assert.Fail($"an unexpandable unconditional reference must fail closed, got: %A{refs}")
        | Error _ -> ())

[<Fact(Timeout = 15000)>]
let ``a self-referential property does not spin`` () =
    // A property defined in terms of itself never reaches a fixed point, so the expansion
    // loop is bounded; the bound turns it into an ordinary unresolvable path, which then
    // fails closed like any other.
    withTempDir "af-self-ref" (fun tmpDir ->
        let main = Path.Combine(tmpDir, "Main", "Main.fsproj")

        writeProj
            main
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n\
             \  <PropertyGroup>\n\
             \    <Loop>$(Loop)/x.fsproj</Loop>\n\
             \  </PropertyGroup>\n\
             \  <ItemGroup>\n\
             \    <ProjectReference Include=\"$(Loop)\" />\n\
             \  </ItemGroup>\n\
             </Project>"

        match ArtifactFreshness.Cache().Closure main with
        | Ok refs -> Assert.Fail($"a self-referential property must not resolve, got: %A{refs}")
        | Error _ -> ())

// ---------------------------------------------------------------------------
// AUTOMATION-303 case 1 — a STRUCTURAL change must MISS the BuildCompleted cache
// ---------------------------------------------------------------------------
//
// 2026-08-12: a test file plus its `<Compile Include=…>` was added, `fshw check`
// exited 0 with `outcome: green` and `scope: {kind: full, ranProjects: 6}`, and the 21
// new tests never executed. The BuildCompleted key is a merkle over the CHANGED
// SYMBOLS, and on a scan `BuildCompleted` is dispatched BEFORE the FCS pass — so that
// term is empty whatever the tree holds, the tree WITH the new file computes the same
// key as the tree without it, and the entry a previous green wrote replays. A replay
// skips the handler, so no run happens and the plugin's `LastCoverage` still describes
// the EARLIER run: the verdict reports that run's full-suite green over a tree it
// never saw.
//
// Driven through the plugin's REAL cache-key closure and a REAL project file on disk,
// not through `cacheKeyFor`'s thunks: the defect was that production never fed this
// input in, which a test supplying it by hand cannot see.

/// A minimal, well-formed `.fsproj` declaring `compiles` in order.
let private fsprojWithCompiles (compiles: string list) : string =
    let items =
        compiles
        |> List.map (fun f -> $"    <Compile Include=\"%s{f}\" />")
        |> String.concat "\n"

    $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n%s{items}\n  </ItemGroup>\n</Project>"

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: adding a compile item moves the BuildCompleted cache key`` () =
    withTempDir "tp-structure-key" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory projDir |> ignore
        let proj = Path.Combine(projDir, "Lib.fsproj")
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs" ])

        // Analysis-only (no test configs): with no runnable projects
        // `sessionHasTestEvidence` is vacuously true, so BuildCompleted HAS a key —
        // which is the value under test. With test configs a cold handler refuses the
        // cache outright (AUTOMATION-161) and there would be nothing to compare.
        let handler =
            create (Path.Combine(tmpDir, "tp.db")) tmpDir None None None None None []

        let keyOf () =
            handler.CacheKey.Value(BuildCompleted BuildSucceeded)

        let before = keyOf ()
        test <@ before.IsSome @>

        // POSITIVE CONTROL. "The key changed" is worth nothing from a key that changes
        // on every call — that would be a cache that never hits, not a cache that is
        // sound. On an untouched tree the key must be STABLE.
        test <@ keyOf () = before @>

        // The structural change: one new compile item. Nothing else moves — no symbol
        // has been checked, no build outcome differs, the queue is still empty.
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "NewTests.fs" ])

        test <@ keyOf () <> before @>

        // ... and removing one moves it too: a DELETED test file must not replay the
        // green that ran it.
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "NewTests.fs"; "More.fs" ])
        let three = keyOf ()
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "NewTests.fs" ])
        test <@ keyOf () <> three @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: the structure hash sees a compile item, not a source edit`` () =
    // The scope of the fix, pinned. Source EDITS are already covered by the symbol-diff
    // pipeline that runs after BuildCompleted and supersedes the entry; what that
    // pipeline cannot rescue is a file it has never seen. So the hash tracks the files
    // that DECLARE what is compiled and deliberately not their contents — otherwise
    // every keystroke would invalidate the whole test cache.
    withTempDir "tp-structure-scope" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory projDir |> ignore
        let proj = Path.Combine(projDir, "Lib.fsproj")
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs" ])
        File.WriteAllText(Path.Combine(projDir, "A.fs"), "module A\nlet x = 1\n")

        let before = projectStructureHash tmpDir

        // A source edit: not a structural change.
        File.WriteAllText(Path.Combine(projDir, "A.fs"), "module A\nlet x = 2\n")
        test <@ projectStructureHash tmpDir = before @>

        // POSITIVE CONTROL — the same hash over the same tree DOES move for the input
        // it exists for, so the equality above is a scope statement and not a hash that
        // never changes.
        File.WriteAllText(proj, fsprojWithCompiles [ "A.fs"; "B.fs" ])
        test <@ projectStructureHash tmpDir <> before @>

        // Build output is excluded: a restore/rebuild dropping generated project files
        // under `obj/` must not invalidate every cached verdict in the repo.
        let objDir = Path.Combine(projDir, "obj")
        Directory.CreateDirectory objDir |> ignore
        let withItems = projectStructureHash tmpDir
        File.WriteAllText(Path.Combine(objDir, "Generated.fsproj"), fsprojWithCompiles [ "Z.fs" ])
        test <@ projectStructureHash tmpDir = withItems @>)

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: the structure hash sees EVERY MSBuild implicit import, not just Directory.Build.props`` () =
    // The list this hash walks was a private copy that knew about `Directory.Build.props`
    // and not about `Directory.Build.targets` or `Directory.Packages.props` — all three
    // are implicit imports on identical terms, and each can carry a `<Compile Include=…>`
    // that adds a file to every project in the repo. Two of the three doors were open.
    //
    // RED before the fix for the two new names: the hash was byte-identical across the
    // edit, so a tree that had just gained a repo-wide compile item computed the key of
    // the tree without it and replayed a green that never ran the new tests.
    withTempDir "tp-structure-imports" (fun tmpDir ->
        let projDir = Path.Combine(tmpDir, "src", "Lib")
        Directory.CreateDirectory projDir |> ignore
        File.WriteAllText(Path.Combine(projDir, "Lib.fsproj"), fsprojWithCompiles [ "A.fs" ])

        for name in FsHotWatch.StructureFiles.implicitImportNames do
            let path = Path.Combine(tmpDir, name)
            File.WriteAllText(path, "<Project />")
            let before = projectStructureHash tmpDir

            // POSITIVE CONTROL, before the claim: stable on an untouched tree.
            test <@ projectStructureHash tmpDir = before @>

            File.WriteAllText(path, "<Project><ItemGroup><Compile Include=\"Generated.fs\" /></ItemGroup></Project>")
            test <@ projectStructureHash tmpDir <> before @>)

// ---------------------------------------------------------------------------
// AUTOMATION-303 case 4 — a DELETED file must not keep blocking the verdict
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``AUTOMATION-303: an unanalysable file that was DELETED stops blocking the verdict`` () =
    // `UnanalyzableFiles` entries leave only when the file analyses CLEANLY
    // (AUTOMATION-113/125), and a deleted file never analyses again — no `FileChecked`
    // will ever arrive for a path that is gone. So its warning was re-reported after
    // every test run for the rest of the daemon's life, and under the default
    // warn-fail policy it denied every check its green while ALSO widening every run to
    // the full suite. Deleting a file is not a defect in the tree.
    withTempDir "tp-deleted-unanalysable" (fun tmpDir ->
        let srcDir = Path.Combine(tmpDir, "src")
        Directory.CreateDirectory srcDir |> ignore

        let stillHere = Path.Combine(srcDir, "StillBroken.fs")
        File.WriteAllText(stillHere, "module StillBroken\n")

        let deleted = Path.Combine(srcDir, "Gone.fs")

        let handler =
            create ":memory:" tmpDir (Some [ a125Config "ProjA" ]) None None None None []

        let entry (relPath: string) (file: string) =
            relPath,
            { RelPath = relPath
              File = file
              Reason = "FS3520: unexpected doc comment" }

        let stateWithBoth =
            { handler.Init with
                UnanalyzableFiles = Map.ofList [ entry "src/StillBroken.fs" stillHere; entry "src/Gone.fs" deleted ] }

        let ctx, _statuses, ledger = makeTestPruneRecordingCtx ()

        let greenRun =
            testsFinishedEvent [ "ProjA", passed false ] (fullSuiteLaunch [ "ProjA" ])

        let after = handler.Update ctx stateWithBoth greenRun |> Async.RunSynchronously

        // POSITIVE CONTROL FIRST. The warning for a file that IS still on disk is still
        // reported — so "the deleted one is absent" is a fact about deletion and not
        // about a detector that stopped firing. Without this the assertion below would
        // pass just as well if the ledger were empty for every reason.
        test <@ ledger.ContainsKey stillHere @>
        test <@ after.UnanalyzableFiles.ContainsKey "src/StillBroken.fs" @>

        // The finding: a path that no longer exists is neither a warning nor a reason to
        // widen the next run.
        test <@ not (ledger.ContainsKey deleted) @>
        test <@ not (after.UnanalyzableFiles.ContainsKey "src/Gone.fs") @>)

// =============================================================================
// AUTOMATION-201 — the stale-artifact PREFLIGHT.
//
// `ArtifactFreshness.stale` was always right about WHAT was stale; it was asked too
// late. Its single call site sat inside the per-config body of the PARALLEL run loop,
// so a group-A project wrote its report before group B's staleness had been looked at
// — a three-minute partial-execution red that reads like progress.
//
// These tests are about ORDER, and they settle it with a file on disk rather than an
// assertion about intent: each runner is a real `sh` process that `touch`es a marker.
// A marker that exists is a suite that ran. Nothing here trusts a log line.
// =============================================================================

/// A synthetic two-project repo in the real MSBuild output layout, with runners that
/// leave evidence. `Common` is referenced by both test projects and its DLL is copied
/// into each of their output dirs — the copy that goes stale in the field.
type private PreflightSynth =
    { Root: string
      P1Proj: string
      P2Proj: string
      P1Marker: string
      P2Marker: string
      P2Src: string
      P2Dll: string
      CommonDll: string
      CommonCopyInP2: string
      BuiltAt: DateTime }

let private preflightSynth (root: string) : PreflightSynth =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)

    let commonDir = Path.Combine(root, "Common")
    let commonOut = p [ commonDir; "bin"; "Debug"; "net10.0" ]

    writeAt (Path.Combine(commonDir, "Common.fsproj")) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (Path.Combine(commonDir, "Common.fs")) "module Common" sourcedAt
    writeAt (Path.Combine(commonOut, "Common.dll")) "COMMON-V2" builtAt

    let mkTestProject (name: string) =
        let dir = Path.Combine(root, name)
        let out = p [ dir; "bin"; "Debug"; "net10.0" ]

        writeAt
            (Path.Combine(dir, $"{name}.fsproj"))
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Common/Common.fsproj\" />\n  </ItemGroup>\n</Project>"
            sourcedAt

        writeAt (Path.Combine(dir, $"{name}.fs")) $"module {name}" sourcedAt
        writeAt (Path.Combine(out, name)) "" builtAt // apphost — the presence probe's target
        writeAt (Path.Combine(out, $"{name}.dll")) "" builtAt
        writeAt (Path.Combine(out, "Common.dll")) "COMMON-V2" builtAt // a CURRENT copy
        dir, out

    let p1Dir, _ = mkTestProject "P1"
    let p2Dir, p2Out = mkTestProject "P2"

    { Root = root
      P1Proj = Path.Combine(p1Dir, "P1.fsproj")
      P2Proj = Path.Combine(p2Dir, "P2.fsproj")
      P1Marker = Path.Combine(root, "p1-ran")
      P2Marker = Path.Combine(root, "p2-ran")
      P2Src = Path.Combine(p2Dir, "P2.fs")
      P2Dll = Path.Combine(p2Out, "P2.dll")
      CommonDll = Path.Combine(commonOut, "Common.dll")
      CommonCopyInP2 = Path.Combine(p2Out, "Common.dll")
      BuiltAt = builtAt }

/// A runner that leaves proof it ran, and carries the `--project` the freshness gate
/// derives its target from. `sh` ignores the trailing args (they become positional
/// params of the `-c` script), so the process is a plain `touch` either way.
let private markerRunner (project: string) (marker: string) (projFile: string) : TestConfig =
    { Project = project
      Command = "sh"
      Args = $"-c \"touch {marker}; exit 0\" run --project {projFile} --no-build"
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = Disabled }

/// Drive one real run of both projects and return the repo root's marker facts.
/// Seeds a covering symbol per project and a pending queue naming both, so a
/// `BuildCompleted` runs BOTH — the two-suite shape the ordering bug needs.
let private drivePreflightRunWithHost (tmpDir: string) (s: PreflightSynth) =
    let dbPath = Path.Combine(tmpDir, "tp.db")
    let db = Database.create dbPath
    // DISTINCT source files per symbol: two symbols sharing one file collapse into a
    // single affected test, which would select only ONE project and quietly turn the
    // "the fresh project did not run" assertion into "the fresh project was never
    // selected" — a test that passes for the wrong reason.
    PendingQueueHelpers.seedCoveredSymbol db "Lib.a" "A.fs" "P1" "P1Tests" "aTest"
    PendingQueueHelpers.seedCoveredSymbol db "Lib.b" "B.fs" "P2" "P2Tests" "bTest"
    FsHotWatch.TestPrune.PendingVerification.save tmpDir (Set.ofList [ "Lib.a"; "Lib.b" ])

    let configs =
        [ markerRunner "P1" s.P1Marker s.P1Proj; markerRunner "P2" s.P2Marker s.P2Proj ]

    let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
    let handler = create dbPath tmpDir (Some configs) None None None None []
    host.RegisterHandler(handler)

    let await = beginAwaitNextTerminal host "test-prune"
    host.EmitBuildCompleted(BuildSucceeded)
    await.Wait(TimeSpan.FromSeconds 25.0) |> ignore
    waitForQuiescent host 5000
    host

/// Drive one real run and discard the host — for the tests whose evidence is on disk.
let private drivePreflightRun (tmpDir: string) (s: PreflightSynth) =
    drivePreflightRunWithHost tmpDir s |> ignore

/// POSITIVE CONTROL, and it comes first on purpose. Every assertion below about a
/// suite NOT running is worthless without it: a preflight that refused everything —
/// or a harness that never launched anything — would satisfy those tests trivially.
/// This pins that the very same path DOES run both suites when the tree is fresh.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: POSITIVE CONTROL — a fresh tree runs every suite`` () =
    withTempDir "tp-preflight-fresh" (fun tmpDir ->
        let s = preflightSynth tmpDir
        drivePreflightRun tmpDir s

        // Both processes really ran. This is the control the refusal tests lean on.
        test <@ File.Exists s.P1Marker @>
        test <@ File.Exists s.P2Marker @>)

/// THE ORDERING TEST. P2's source is edited after its assembly was compiled — the
/// unhealable `AssemblyOlderThanSource` case, which only a real build can clear.
///
/// RED BEFORE THE FIX: with the freshness gate living inside the parallel per-config
/// body, P1 was examined, found fresh and LAUNCHED, and only then was P2 examined and
/// deferred — so `p1-ran` existed and this test failed on its first assertion. That is
/// the three-minute partial-execution red, reproduced in miniature.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: a stale project refuses the run BEFORE any suite executes`` () =
    withTempDir "tp-preflight-stale" (fun tmpDir ->
        let s = preflightSynth tmpDir

        // The trigger from the field: a source edit under a live daemon. P2's compile
        // has not run since, so its assembly cannot be trusted.
        File.SetLastWriteTimeUtc(s.P2Src, s.BuiltAt.AddMinutes 30.0)

        drivePreflightRun tmpDir s

        // NOTHING launched — not the stale project, and not the fresh one either.
        // A run whose tree is provably not built cannot reach a green verdict, so
        // spending minutes on the projects that happen to be fresh buys nothing but
        // the appearance of progress.
        test <@ not (File.Exists s.P2Marker) @>
        test <@ not (File.Exists s.P1Marker) @>)

/// The heal, end to end. P2's copy of `Common.dll` holds bytes no current build output
/// holds — the same-timestamp/different-bytes inversion MSBuild's incremental copy
/// leaves behind. The repair is provably complete (write the origin's bytes across),
/// so the preflight fixes it, re-reads the bytes, and lets the run proceed.
///
/// RED BEFORE THE FIX: P2 was deferred as "waiting on build" and never ran, so
/// `p2-ran` was absent. Nothing repaired anything.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: a stale build-output COPY is repaired and the run proceeds`` () =
    withTempDir "tp-preflight-heal" (fun tmpDir ->
        let s = preflightSynth tmpDir

        // The inversion: different bytes, and the mtime MSBuild would compare as equal,
        // so no plain incremental build re-copies it.
        File.WriteAllText(s.CommonCopyInP2, "COMMON-V1-STALE")
        File.SetLastWriteTimeUtc(s.CommonCopyInP2, s.BuiltAt)

        drivePreflightRun tmpDir s

        // Repaired, so BOTH suites ran ...
        test <@ File.Exists s.P1Marker @>
        test <@ File.Exists s.P2Marker @>

        // ... the bytes are the origin's ...
        test <@ File.ReadAllText s.CommonCopyInP2 = File.ReadAllText s.CommonDll @>

        // ... and the repair was RECORDED, not just done. A heal that fires every run
        // is itself the finding, and nobody greps history for it.
        let ledger = FsHotWatch.TestPrune.StaleArtifactPreflight.loadLedger tmpDir
        test <@ ledger |> List.exists (fun r -> r.File = s.CommonCopyInP2) @>)

/// END TO END, through the ledger the CLI actually reads. AC2's "states the concrete
/// remedy" is about the message an operator ACTS on, and for a `check` that is the
/// top-level verdict line — which, before this rework, said "a test project's build
/// artifact was not produced … re-run once the build settles" and pointed at `fshw
/// confirm`. Every clause of that is wrong here: the artifact WAS produced, re-running
/// returns the identical refusal, and `fshw confirm` spends a full gate cycle to reach
/// it. So this asserts the whole chain — real run → real ledger entry →
/// `BuildWait.classify` — rather than any one link.
[<Fact(Timeout = 40000)>]
let ``AUTOMATION-201: an unrepairable stale tree reaches the CLI as StaleOutput, not as a build-ordering defer`` () =
    withTempDir "tp-preflight-classify" (fun tmpDir ->
        let s = preflightSynth tmpDir
        // The unhealable case: P2's source was edited after its assembly was compiled.
        File.SetLastWriteTimeUtc(s.P2Src, s.BuiltAt.AddMinutes 30.0)

        let host = drivePreflightRunWithHost tmpDir s

        test <@ not (File.Exists s.P1Marker) @>
        test <@ not (File.Exists s.P2Marker) @>

        let deferrals =
            host.GetErrors()
            |> Map.toList
            |> List.collect snd
            |> List.filter (fun (_, e) -> FsHotWatch.ErrorLedger.ErrorEntry.isWaitingOnBuild e)

        test <@ not (List.isEmpty deferrals) @>

        // The message the CLI classifies on — from the run, not hand-written.
        let messages = deferrals |> List.map (fun (_, e) -> e.Message)

        // THE OPERATOR-FACING MESSAGE, derived the way the CLI derives it: classify the
        // ledger, take the verdict, read the reason. Asserting on the reason rather than
        // on the classifier is deliberate — the defect was never "the enum is wrong", it
        // was "the person reading this is sent to run the wrong command".
        let outcome =
            FsHotWatch.Cli.CheckVerdict.CheckOutcome.WaitingOnBuild(
                FsHotWatch.Cli.CheckVerdict.BuildWait.staleDeferrals (
                    FsHotWatch.Cli.CheckVerdict.BuildWait.classify messages
                )
            )

        match FsHotWatch.Cli.Verdict.outcomeOfCheck outcome with
        | FsHotWatch.Cli.Verdict.Incomplete reason ->
            // Names the affected project, and the command that actually repairs it.
            test <@ reason.Contains "P2" @>
            test <@ reason.Contains "dotnet build" @>
            test <@ reason.Contains "--no-incremental" @>

            // And names the projects to ACT on, not every project the refusal deferred.
            // P1 is fresh and was deferred only because the run was refused as a whole —
            // listing it would be the over-listing that made the headline unreadable in
            // the first place. Its own deferral still names P2 as the cause.
            test <@ not (reason.Contains "P1") @>

            // And no longer asserts the cause that is FALSE here — the artifact WAS
            // produced — nor prescribes the two remedies that cannot clear it.
            test <@ not (reason.Contains "was not produced") @>
            test <@ not (reason.Contains "re-run once the build settles") @>
        | other -> failwith $"a stale build-output refusal must stay incomplete, got %A{other}"

        // The DETAIL stops asserting the wrong cause too. It used to read "This is a
        // build-ordering issue, not a test failure" for every defer — advice that costs
        // a gate cycle when the tree is stale rather than merely early.
        let staleDetails =
            deferrals
            |> List.filter (fun (_, e) -> FsHotWatch.TestPrune.StaleArtifactPreflight.isStaleOutputDeferral e.Message)
            |> List.choose (fun (_, e) -> e.Detail)

        test <@ not (List.isEmpty staleDetails) @>
        test <@ staleDetails |> List.forall (fun d -> not (d.Contains "build-ordering issue")) @>
        test <@ staleDetails |> List.forall (fun d -> d.Contains "build OUTPUT is stale") @>)

/// POSITIVE CONTROL for the classification: the OTHER defer — a build artifact that
/// genuinely was not produced — must keep classifying as `ArtifactNotProduced`, because
/// for it "re-run once the build settles" is correct advice and replacing it would be a
/// regression wearing a fix's clothes. Without this, a classifier that answered
/// `StaleOutput` to everything would pass the test above.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: an apphost-missing defer still classifies as a build-ordering wait`` () =
    let deferred: TestResults =
        { Results = Map.ofList [ "P2", TestsDeferred "apphost not produced; tests did not run" ]
          Elapsed = TimeSpan.Zero }

    let messages = failuresOf Map.empty deferred |> List.map (fun f -> f.Entry.Message)
    test <@ not (List.isEmpty messages) @>

    test
        <@
            FsHotWatch.Cli.CheckVerdict.BuildWait.classify messages = FsHotWatch.Cli.CheckVerdict.BuildWait.ArtifactNotProduced
        @>

// ---------------------------------------------------------------------------
// AUTOMATION-343 — TEST-PRUNE's own cold-vs-cached ledger parity
// ---------------------------------------------------------------------------

[<Fact(Timeout = 60000)>]
let ``AUTOMATION-343: a cached test-prune replay clears the whole ledger, exactly as its real run does`` () =
    // THIS PLUGIN IS THE OPPOSITE CASE, and asserting the same thing here as for
    // build / file-command would paper over the difference.
    //
    // `reportOutstanding` is test-prune's ONLY path to the ledger, and it clears the
    // slate wholesale (`ClearAllErrors`) before re-reporting the outstanding set. So a
    // finding about a file outside the run is not "preserved" here — a real run
    // deletes it, on purpose, and a replay that preserved it would be the FALSE RED
    // the fix's risk inverts towards: findings accumulating across replays because
    // the replay stopped doing what the run did.
    //
    // The framework captures that wholesale clear as a `("*", [])` marker, which is
    // exactly why deleting the blanket pre-replay `ClearPlugin` was safe: the run that
    // really cleared everything already says so in its own captured errors. This test
    // is what proves that claim about the plugin that actually relies on it.
    withTempDir "a343-tp-parity" (fun tmpDir ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, tmpDir, taskCache = cache)

        // Exit 0 with no parseable report is a PASS (`decideTestOutcome`'s clean-exit
        // arm), so the run leaves nothing outstanding — which is what lets the entry
        // be cacheable at all.
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

        let handler =
            create (Path.Combine(tmpDir, "tp.db")) tmpDir (Some configs) None None None None []

        host.RegisterHandler(handler)

        // COLD.
        seedOutOfBatch host "test-prune"
        test <@ ledgerHasOutOfBatch host "test-prune" @>

        host.EmitBuildCompleted(BuildSucceeded)
        waitForTerminalStatus host "test-prune" 30000
        let settled = waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 30000
        test <@ settled @>
        let coldSummary = terminalSummaryOf host "test-prune"
        test <@ not (coldSummary.Contains "(cached)") @>

        let cold = ledgerSlice host "test-prune"

        // The real run really did wipe it. Asserted, not assumed — if it did not, the
        // cached assertion below would be pinning the wrong behaviour.
        test <@ not (ledgerHasOutOfBatch host "test-prune") @>

        // WARM. Re-seed so the replay has something to destroy: without this the
        // "sentinel is gone" assertion below would hold trivially, since the cold run
        // already removed it.
        seedOutOfBatch host "test-prune"
        test <@ ledgerHasOutOfBatch host "test-prune" @>

        host.EmitBuildCompleted(BuildSucceeded)
        let replayed = waitForCachedReplay host "test-prune" 30000
        test <@ replayed @>

        let cached = ledgerSlice host "test-prune"

        // The replay honoured the captured `("*", [])` and cleared it too ...
        test <@ not (ledgerHasOutOfBatch host "test-prune") @>
        // ... landing the ledger in exactly the state the real run landed it in.
        test <@ cached = cold @>)

// ---------------------------------------------------------------------------
// AUTOMATION-259 (rework) — retaining the selection `confirm` widens past, and asking
// whether it would have reached a failure.
//
// `confirm` sends `set-scope full` BEFORE the scan that provokes the run, so the impact
// selection is computed at the launch chokepoint and thrown away in the same breath.
// Retaining it is what turns each confirm into a same-tree sample instead of a positive
// assertion that nothing was compared.
// ---------------------------------------------------------------------------

let private a259Projects =
    [ testConfigNamed "Alpha.Tests"
      testConfigNamed "Beta.Tests"
      testConfigNamed "Gamma.Tests" ]

let private a259Failure (project: string) (cls: string option) : OutstandingFailure =
    { Project = project
      Class = cls
      Method = cls |> Option.map (fun _ -> "failed method")
      File = "tests/X.fs"
      Entry = FsHotWatch.ErrorLedger.ErrorEntry.error $"%s{project}: 1 test(s) failed" }

[<Fact(Timeout = 15000)>]
let ``the would-have-run selection drops the FULL-SUITE widening and keeps every other one`` () =
    // The load-bearing line. Remove too much and the projection models a `check` narrower
    // than the one that runs, and reports misses the selector never made; remove too
    // little and every project is selected, the projection agrees with everything, and
    // the sample is worthless.
    let symbolAffected = Map.ofList [ "Alpha.Tests", [ "Alpha.OneTests" ] ]

    let selection =
        wouldHaveRunSelection a259Projects symbolAffected Set.empty Set.empty false

    test <@ selection = Map.ofList [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ]) ] @>

    // The coarse fallback for unanalysable files (AUTOMATION-113) fires in the inner loop
    // too, so it SURVIVES the projection — as a whole-project run, which is what it is.
    let withCoarse =
        wouldHaveRunSelection a259Projects symbolAffected (Set.ofList [ "Beta.Tests" ]) Set.empty false

    test <@ Map.tryFind "Beta.Tests" withCoarse = Some ProjectInFull @>
    test <@ Map.tryFind "Alpha.Tests" withCoarse = Some(ProjectClasses(Set.ofList [ "Alpha.OneTests" ])) @>
    test <@ Map.containsKey "Gamma.Tests" withCoarse |> not @>

    // An UNREADABLE pending-verification ledger (AUTOMATION-150) widens `check` to the
    // whole suite as well, and must therefore widen the projection.
    let withUnreadableLedger =
        wouldHaveRunSelection a259Projects symbolAffected Set.empty Set.empty true

    test <@ withUnreadableLedger |> Map.forall (fun _ sel -> sel = ProjectInFull) @>
    test <@ withUnreadableLedger.Count = 3 @>

[<Fact(Timeout = 15000)>]
let ``the would-have-run selection retains a runtime-only project's reach`` () =
    // There is deliberately no AST-selected class for Beta. Runtime coverage is
    // the only reason check would launch it, and it must launch the whole project.
    // Dropping this input makes the same-tree confirm comparison falsely report
    // Beta's red as a selector miss.
    let selection =
        wouldHaveRunSelection
            a259Projects
            (Map.ofList [ "Alpha.Tests", [ "Alpha.OneTests" ] ])
            Set.empty
            (Set.ofList [ "Beta.Tests" ])
            false

    test <@ Map.tryFind "Beta.Tests" selection = Some ProjectInFull @>

    let reach =
        CheckReach.classify (Some selection) [ a259Failure "Beta.Tests" (Some "Beta.RuntimeOnlyTests") ]

    test <@ reach = ReachedAFailure [ "Beta.Tests" ] @>

[<Fact(Timeout = 15000)>]
let ``scopeOfSelection describes what check would have covered, and never rounds up`` () =
    let projects = a259Projects |> List.map (fun c -> c.Project)

    test <@ scopeOfSelection projects Map.empty = ScopeNone 3 @>

    test
        <@
            scopeOfSelection projects (a259Projects |> List.map (fun c -> c.Project, ProjectInFull) |> Map.ofList) = ScopeFull
                3
        @>

    // Every project selected, but one of them under a class filter — NOT a full suite.
    let allButFiltered =
        [ "Alpha.Tests", ProjectClasses(Set.ofList [ "X" ])
          "Beta.Tests", ProjectInFull
          "Gamma.Tests", ProjectInFull ]
        |> Map.ofList

    test <@ scopeOfSelection projects allButFiltered = ScopeFiltered(3, 3) @>
    test <@ scopeOfSelection projects (Map.ofList [ "Alpha.Tests", ProjectInFull ]) = ScopeFiltered(1, 3) @>

[<Fact(Timeout = 15000)>]
let ``CheckReach.classify decides reach per failure, and refuses what it cannot decide`` () =
    let inFull = Map.ofList [ "Alpha.Tests", ProjectInFull ]

    let filtered =
        Map.ofList [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ]) ]

    // Nothing failed: there was no failure for a selection to miss.
    test <@ CheckReach.classify (Some inFull) [] = NoFailuresToReach @>

    // A project the selection never launches — the ordinary miss.
    let projectMiss =
        CheckReach.classify (Some inFull) [ a259Failure "Beta.Tests" (Some "Beta.OneTests") ]

    test
        <@
            projectMiss = ReachedNoFailure
                [ { Project = "Beta.Tests"
                    Class = "Beta.OneTests"
                    Cause = MissCause.ProjectNotSelected } ]
        @>

    // A project it launches in full reaches every failure in it, named class or not.
    test
        <@
            CheckReach.classify (Some inFull) [ a259Failure "Alpha.Tests" (Some "Alpha.TwoTests") ] = ReachedAFailure
                [ "Alpha.Tests" ]
        @>

    test <@ CheckReach.classify (Some inFull) [ a259Failure "Alpha.Tests" None ] = ReachedAFailure [ "Alpha.Tests" ] @>

    // A class-filtered project reaches the failure only when the class is in the filter.
    test
        <@
            CheckReach.classify (Some filtered) [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests") ] = ReachedAFailure
                [ "Alpha.Tests" ]
        @>

    let classMiss =
        CheckReach.classify (Some filtered) [ a259Failure "Alpha.Tests" (Some "Alpha.TwoTests") ]

    test
        <@
            classMiss = ReachedNoFailure
                [ { Project = "Alpha.Tests"
                    Class = "Alpha.TwoTests"
                    Cause = MissCause.ClassNotInFilter } ]
        @>

    // A PROJECT-LEVEL red (a timeout, an errored host, unparseable output) names no
    // class, so a class-filtered selection cannot be asked whether it reaches it. Refused
    // rather than guessed — guessing "not reached" invents a missed failure, guessing
    // "reached" invents an agreement.
    match CheckReach.classify (Some filtered) [ a259Failure "Alpha.Tests" None ] with
    | ReachUnknown reason -> test <@ reason.Contains "names no test class" @>
    | other -> failwithf "a project-level red under a class filter must be UNKNOWN, got %A" other

    // ONE undecidable failure poisons the whole reading, even beside a decided one: the
    // question is about the RUN, and a run we cannot fully account for is not a sample.
    match
        CheckReach.classify
            (Some filtered)
            [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests")
              a259Failure "Alpha.Tests" None ]
    with
    | ReachUnknown _ -> ()
    | other -> failwithf "an undecidable failure beside a decided one must be UNKNOWN, got %A" other

    // No retained selection at all (a forced re-run, an aborted run, a skip): there is
    // nothing to project through, and `Selection` must NOT be used as a stand-in — under
    // full-suite scope it names every project in full and would agree forever.
    match CheckReach.classify None [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests") ] with
    | ReachUnknown reason -> test <@ reason.Contains "no retained impact selection" @>
    | other -> failwithf "a run with no retained selection must be UNKNOWN, got %A" other

[<Theory(Timeout = 15000)>]
[<InlineData("Database.Tests failed but wrote no current-run CTRF report")>]
[<InlineData("Database.Tests's CTRF failed rows do not reconcile to its summary")>]
[<InlineData("Database.Tests's CTRF report could not be read")>]
let ``AUTOMATION-111 incomplete failure evidence cannot become a selection alarm`` reason =
    let selection = Some(Map.ofList [ "Unit.Tests", ProjectInFull ])
    let reach, recall = CheckReach.classifyEvidence selection (Error reason)

    test <@ reach = ReachUnknown reason @>
    test <@ recall = RecallNotMeasurable reason @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-111 complete evidence preserves genuine mass selection misses`` () =
    let selection = Some(Map.ofList [ "Unit.Tests", ProjectInFull ])

    let failures =
        [ 1..600 ]
        |> List.map (fun index -> a259Failure "Database.Tests" (Some $"Database.Case%d{index}"))

    let reach, _ = CheckReach.classifyEvidence selection (Ok failures)

    match reach with
    | ReachedNoFailure missed -> test <@ missed.Length = 600 @>
    | other -> failwithf "complete mass misses remain actionable, got %A" other

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-67 failure recall is an exact numerator over full-run failures and never vacuous`` () =
    let selection =
        Map.ofList
            [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ])
              "Beta.Tests", ProjectClasses(Set.ofList [ "Beta.OtherTests" ]) ]

    let failures =
        [ a259Failure "Alpha.Tests" (Some "Alpha.OneTests")
          a259Failure "Beta.Tests" (Some "Beta.MissedTests") ]

    test <@ CheckReach.measure (Some selection) failures = RecallMeasured(1, 2, 1.0, false) @>

    let interventionSelection =
        Map.ofList
            [ "Alpha.Tests", ProjectClasses(Set.ofList [ "Alpha.OneTests" ])
              "Beta.Tests", ProjectClasses(Set.ofList [ "Beta.MissedTests" ]) ]

    test <@ CheckReach.measure (Some interventionSelection) failures = RecallMeasured(2, 2, 1.0, true) @>

    match CheckReach.measure (Some selection) [] with
    | RecallNotMeasurable reason -> test <@ reason.Contains("denominator is zero") @>
    | measured -> Assert.Fail($"a zero-denominator sample cannot claim recall, got %A{measured}")

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-67 failure recall requires every observed failure to be decidable`` () =
    let inFull = Map.ofList [ "Alpha.Tests", ProjectInFull ]

    match CheckReach.measure (Some inFull) [ a259Failure "Alpha.Tests" None ] with
    | RecallNotMeasurable reason -> test <@ reason.Contains("exact failing-test denominator") @>
    | measured -> Assert.Fail($"an undecidable failure cannot enter a recall denominator, got %A{measured}")

// ---------------------------------------------------------------------------
// AUTOMATION-747 — a red project's ledger slice is a SUM, not a PRODUCT.
//
// `failuresOf` used to attach `output` — the whole captured project run — to every
// parsed per-test failure. In the incident this ticket records that made one project's
// ledger slice 753 × 48 MB: ~36 GB, from a plugin holding a single string. It is
// invisible in the daemon's own heap (every entry is the same reference) and fatal the
// moment any mirror of the ledger writes each entry's copy out — which is exactly where
// seven finished merge gates died.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``failuresOf does not attach the whole project output to every parsed failure`` () =
    let failures = 200
    let noise = String.replicate 40_000 "n"

    let output =
        [ yield noise
          for i in 1..failures do
              yield $"  failed Some.Suite%d{i}.the test (0ms)" ]
        |> String.concat "\n"

    let results: TestResults =
        { Results = Map.ofList [ "ProjA", TestsFailed(output, false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let entries = failuresOf Map.empty results

    // Every failure is still FILED — this bound may not be bought by losing reds.
    test <@ entries.Length = failures @>

    let totalChars =
        entries
        |> List.sumBy (fun f ->
            f.Entry.Message.Length
            + (f.Entry.Detail |> Option.map String.length |> Option.defaultValue 0))

    // The law: the slice is bounded by the failures PLUS the output, never their
    // product. Before this fix the same input produced 200 × 40,000 = 8,000,000 chars.
    test <@ totalChars < output.Length @>

[<Fact(Timeout = 15000)>]
let ``failuresOf still carries the whole output when NO test could be named`` () =
    // The one arm where that text is the entry's own subject: nothing named a test, so
    // the run itself is all the reader has. ONE entry, so it is carried once.
    let output = "the host said something unparseable\n" + String.replicate 5_000 "z"

    let results: TestResults =
        { Results = Map.ofList [ "ProjA", TestsFailed(output, false, TimeSpan.Zero) ]
          Elapsed = TimeSpan.Zero }

    let entry = (failuresOf Map.empty results |> List.exactlyOne).Entry
    test <@ entry.Detail = Some output @>
