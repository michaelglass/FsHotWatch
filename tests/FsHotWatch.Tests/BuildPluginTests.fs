module FsHotWatch.Tests.BuildPluginTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Build
open FsHotWatch.Build.BuildPlugin
open FsHotWatch.ProjectGraph
open FsHotWatch.Tests.TestHelpers

// --- decideBuildOutcome: pure parse/decide logic ---

[<Fact(Timeout = 5000)>]
let ``decideBuildOutcome success with clean output yields BuildPassed and no entries`` () =
    let output = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)"
    let (outcome, entries) = decideBuildOutcome true output
    test <@ outcome = BuildPassed output @>
    test <@ entries.IsEmpty @>

[<Fact(Timeout = 5000)>]
let ``decideBuildOutcome success with warnings yields BuildPassed and parsed warnings`` () =
    let output =
        "/src/Bar.fs(3,1): warning FS0040: This construct causes code to be less generic"

    let (outcome, entries) = decideBuildOutcome true output
    test <@ outcome = BuildPassed output @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Warning @>
    test <@ entries.[0].Line = 3 @>

[<Fact(Timeout = 5000)>]
let ``decideBuildOutcome failure with parsed errors yields BuildOutputFailed and parsed entries`` () =
    let output =
        "/src/Foo.fs(12,5): error FS0001: This expression was expected to have type int"

    let (outcome, entries) = decideBuildOutcome false output
    test <@ outcome = BuildOutputFailed [ output ] @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Error @>

[<Fact(Timeout = 5000)>]
let ``decideBuildOutcome failure with empty output yields single synthetic error`` () =
    let (outcome, entries) = decideBuildOutcome false ""
    test <@ outcome = BuildOutputFailed [ "" ] @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Error @>
    test <@ entries.[0].Message = "" @>

[<Fact(Timeout = 5000)>]
let ``decideBuildOutcome failure with unparseable output falls back to raw-text error`` () =
    let output = "Segmentation fault\nrandom stderr blob\nnot an MSBuild line"
    let (outcome, entries) = decideBuildOutcome false output
    test <@ outcome = BuildOutputFailed [ output ] @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Message = output @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Error @>

[<Fact(Timeout = 5000)>]
let ``decideBuildOutcome failure with mixed stderr and MSBuild lines prefers parsed entries`` () =
    let output =
        "Startup trace noise\n/src/Foo.fs(12,5): error FS0001: Bad type\nrandom stderr\n/src/Bar.fs(3,1): warning FS0040: Less generic"

    let (outcome, entries) = decideBuildOutcome false output
    test <@ outcome = BuildOutputFailed [ output ] @>
    test <@ entries.Length = 2 @>
    test <@ entries |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Error) @>
    test <@ entries |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Warning) @>

[<Fact(Timeout = 5000)>]
let ``create accepts graph and test project names`` () =
    let graph = FsHotWatch.ProjectGraph.ProjectGraph()
    let handler = BuildPlugin.create "echo" "build" [] graph [] None [] None
    test <@ handler.Name = PluginName.create "build" @>

[<Fact(Timeout = 5000)>]
let ``plugin has correct name`` () =
    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    test <@ handler.Name = PluginName.create "build" @>

[<Fact(Timeout = 5000)>]
let ``build-status command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(handler)

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact(Timeout = 5000)>]
let ``formatSilentFailureDiagnostic includes exit code and output length`` () =
    let output =
        "Build FAILED.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:02.96"

    let detail = formatSilentFailureDiagnostic 1 output
    test <@ detail.Contains "exit=1" @>
    test <@ detail.Contains $"output={output.Length} bytes" @>
    test <@ detail.Contains "MSBuild aborted" @>

[<Fact(Timeout = 5000)>]
let ``formatSilentFailureDiagnostic includes elapsed time when present in output`` () =
    let output =
        "Build FAILED.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:01:23.45"

    let detail = formatSilentFailureDiagnostic 134 output
    test <@ detail.Contains "elapsed=00:01:23.45" @>

[<Fact(Timeout = 5000)>]
let ``formatSilentFailureDiagnostic omits elapsed when not present`` () =
    let output = "Build FAILED.\n    0 Warning(s)\n    0 Error(s)"
    let detail = formatSilentFailureDiagnostic 1 output
    test <@ not (detail.Contains "elapsed=") @>

[<Fact(Timeout = 5000)>]
let ``build plugin emits BuildCompleted on successful build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``build-status command returns passed true after successful build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    let doc = JsonDocument.Parse(result.Value)
    Assert.Equal("passed", doc.RootElement.GetProperty("status").GetString())

[<Fact(Timeout = 5000)>]
let ``build-status command returns failed after failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    let doc = JsonDocument.Parse(result.Value)
    Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString())

[<Fact(Timeout = 15000)>]
let ``build plugin honors timeoutSec and records TimedOut outcome`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "sleep" "10" [] (ProjectGraph()) [] None [] (Some 1)

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 8000

    let history = host.GetHistory("build")
    test <@ not history.IsEmpty @>
    let last = List.last history

    test
        <@
            match last.Outcome with
            | TimedOut _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 10000)>]
let ``build plugin reports Failed status on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``build plugin emits BuildFailed on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``build plugin reports errors on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 10000)>]
let ``build plugin handles exception from runProcess`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "this-command-does-not-exist-xyz" "" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 5000)>]
let ``build plugin ignores SolutionChanged events`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged "test.sln")

    // SolutionChanged is ignored — poll briefly; will time out (expected)
    waitUntil (fun () -> (getBuild ()).IsSome) 200

    test <@ getBuild () = None @>

[<Fact(Timeout = 5000)>]
let ``build plugin triggers on ProjectChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 5000)>]
let ``build is skipped when only test files change, after FCS confirms the file`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/tests/MyTests/MyTests.fsproj",
        [ AbsFilePath.create "/tmp/tests/MyTests/Tests.fs" ],
        []
    )

    let handler = BuildPlugin.create "false" "" [] graph [ "MyTests" ] None [] None
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/tests/MyTests/Tests.fs" ])

    // BuildSucceeded must not be emitted before FCS finishes checking the changed file —
    // downstream test-prune queries stale AffectedTests if it fires too early.
    Threading.Thread.Sleep(200)
    test <@ getBuild () = None @>

    host.EmitFileChecked(fakeFileCheckResult "/tmp/tests/MyTests/Tests.fs")

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 5000)>]
let ``build skip waits for FileChecked of all changed test files before emitting BuildSucceeded`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/tests/MyTests/MyTests.fsproj",
        [ AbsFilePath.create "/tmp/tests/MyTests/A.fs"
          AbsFilePath.create "/tmp/tests/MyTests/B.fs" ],
        []
    )

    let handler = BuildPlugin.create "false" "" [] graph [ "MyTests" ] None [] None
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/tests/MyTests/A.fs"; "/tmp/tests/MyTests/B.fs" ])

    // First FileChecked: still missing B.fs, build must NOT have completed yet.
    host.EmitFileChecked(fakeFileCheckResult "/tmp/tests/MyTests/A.fs")
    Threading.Thread.Sleep(150)
    test <@ getBuild () = None @>

    // Second FileChecked: now both files seen, BuildSucceeded should fire.
    host.EmitFileChecked(fakeFileCheckResult "/tmp/tests/MyTests/B.fs")
    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 5000)>]
let ``build uses template for affected project`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/src/MyLib/MyLib.fsproj",
        [ AbsFilePath.create "/tmp/src/MyLib/Lib.fs" ],
        []
    )

    let handler =
        BuildPlugin.create "false" "should-not-run" [] graph [] (Some "echo {project}") [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/src/MyLib/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 5000)>]
let ``build falls back to original command when no template`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/src/MyLib/MyLib.fsproj",
        [ AbsFilePath.create "/tmp/src/MyLib/Lib.fs" ],
        []
    )

    let handler = BuildPlugin.create "echo" "fallback-build" [] graph [] None [] None
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/src/MyLib/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 10000)>]
let ``build falls back when file not in graph`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    let handler =
        BuildPlugin.create "echo" "fallback-for-unknown" [] graph [] (Some "false {project}") [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/src/Unknown/File.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 5000)>]
let ``ProjectChanged always uses fallback command`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    let handler =
        BuildPlugin.create "echo" "fallback ok" [] graph [] (Some "false {project}") [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

// --- dependsOn tests ---

[<Fact(Timeout = 5000)>]
let ``build with dependsOn buffers FileChanged until dependency satisfied`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup" ] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    // FileChanged should be buffered — dependency not yet satisfied
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Brief wait — build should NOT start
    waitUntil (fun () -> (getBuild ()).IsSome) 500
    test <@ getBuild () = None @>

    // Now satisfy the dependency
    host.EmitCommandCompleted(
        { Name = "setup"
          Outcome = CommandSucceeded "ok" }
    )

    waitForTerminalStatus host "build" 5000
    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 10000)>]
let ``build with dependsOn proceeds immediately when deps already satisfied`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup" ] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    // Satisfy dependency first
    host.EmitCommandCompleted(
        { Name = "setup"
          Outcome = CommandSucceeded "ok" }
    )

    // Now FileChanged should proceed immediately
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000
    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 5000)>]
let ``build with dependsOn reports Failed when dependency fails`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup" ] None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    host.EmitCommandCompleted(
        { Name = "setup"
          Outcome = CommandFailed "error" }
    )

    waitForTerminalStatus host "build" 5000

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed(msg, _) -> msg.Contains("dependency failed: setup")
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``build with empty dependsOn works normally`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000
    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 5000)>]
let ``build with multiple dependsOn waits for all`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup"; "codegen" ] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Satisfy only one dependency
    host.EmitCommandCompleted(
        { Name = "setup"
          Outcome = CommandSucceeded "ok" }
    )

    // Build should still NOT start
    waitUntil (fun () -> (getBuild ()).IsSome) 500
    test <@ getBuild () = None @>

    // Satisfy the second dependency
    host.EmitCommandCompleted(
        { Name = "codegen"
          Outcome = CommandSucceeded "ok" }
    )

    waitForTerminalStatus host "build" 5000
    waitUntil (fun () -> (getBuild ()).IsSome) 5000
    test <@ getBuild () = Some BuildSucceeded @>

// --- §2a: BuildPlugin cache key behaviour ---

[<Fact(Timeout = 5000)>]
let ``BuildPlugin cache key is provided regardless of getCommitId`` () =
    let h1 = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    let h2 = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    test <@ h1.CacheKey.IsSome @>
    test <@ h2.CacheKey.IsSome @>

[<Fact(Timeout = 5000)>]
let ``regression: BuildPlugin writes a cache entry on terminal Custom BuildDone`` () =
    // Before this fix, BuildPlugin's applyBuildOutcome called EmitBuildCompleted
    // and ReportErrors from inside the fire-and-forget async, so the framework's
    // per-event cache-write window for FileChanged saw only "Running" and the
    // Custom BuildDone window had nothing to capture (events emitted earlier).
    // After: the captured operations move into the Custom BuildDone handler,
    // which runs synchronously and IS captured.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitForTerminalStatus host "build" 5000

    // After terminal status, the cache should contain an entry for build.
    let key: FsHotWatch.TaskCache.CompositeKey = { Plugin = "build"; File = None }

    let cacheKeyFn = handler.CacheKey.Value
    // The cache lookup happens at FileChanged time in production; the entry
    // is stored with the same merkle key (whether emitted from FileChanged
    // or Custom BuildDone — they share the input set).
    let computedKey = cacheKeyFn (FileChanged(SourceChanged [ "src/Lib.fs" ]))
    test <@ computedKey.IsSome @>

    let result = cacheIface.TryGet key computedKey.Value
    test <@ result.IsSome @>
    // EmittedEvents should include the BuildCompleted that the synchronous
    // handler emitted — this is what cache replay will re-fire to downstream
    // plugins (TestPrune, Coverage).
    test <@ not result.Value.EmittedEvents.IsEmpty @>

// Drive a real build through the host so the plugin's cold-start guard flips
// before we inspect the cache key. Returns the (warmed) handler.
let private warmedHandler (command: string) (args: string) (dependsOn: string list) =
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    let handler =
        BuildPlugin.create command args [] (ProjectGraph()) [] None dependsOn None

    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitForTerminalStatus host "build" 5000
    handler

[<Fact(Timeout = 5000)>]
let ``BuildPlugin cache key matches between FileChanged and Custom BuildDone`` () =
    // The cache stores a result on the synchronous Custom BuildDone handler;
    // future FileChanged events look up by the same merkle key. Both must
    // compute identical keys for the cache to hit.
    let handler = warmedHandler "echo" "ok" []

    let cacheKeyFn = handler.CacheKey.Value
    let fileEvt = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])
    let buildDoneEvt = Custom(BuildDone(BuildPassed "x", []))

    let fileKey = cacheKeyFn fileEvt
    let doneKey = cacheKeyFn buildDoneEvt
    test <@ fileKey.IsSome @>
    test <@ fileKey = doneKey @>

[<Fact(Timeout = 5000)>]
let ``BuildPlugin cache key returns None for FileChecked events`` () =
    // FileChecked events use a different composite key (File = Some x) so they
    // would always miss if looked up; returning None skips the cache entirely.
    let handler = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    let cacheKeyFn = handler.CacheKey.Value
    let checkedEvt = FileChecked(fakeFileCheckResult "/tmp/Foo.fs")
    test <@ cacheKeyFn checkedEvt = None @>

[<Fact(Timeout = 5000)>]
let ``BuildPlugin cache key reflects build command`` () =
    // §2a: changing the build command/args should invalidate the cache.
    // Tests the pure merkle directly, bypassing the cold-start guard.
    let inputs = "stub-inputs-hash"
    let k1 = BuildPlugin.computeBuildCacheKey "dotnet" "build" [] inputs
    let k2 = BuildPlugin.computeBuildCacheKey "dotnet" "test" [] inputs
    test <@ k1 <> k2 @>

[<Fact(Timeout = 5000)>]
let ``BuildPlugin cache key reflects dependsOn ordering and content`` () =
    let inputs = "stub-inputs-hash"
    let k1 = BuildPlugin.computeBuildCacheKey "dotnet" "build" [ "a"; "b" ] inputs
    let k2 = BuildPlugin.computeBuildCacheKey "dotnet" "build" [ "b"; "a" ] inputs
    let k3 = BuildPlugin.computeBuildCacheKey "dotnet" "build" [ "a"; "c" ] inputs
    test <@ k1 = k2 @> // sorted internally
    test <@ k1 <> k3 @>

// --- §2a: BuildInputsHasher (extracted via internal visibility for testability) ---

let private stubGraph (sources: string list) (projects: string list) =
    { new IProjectGraphReader with
        member _.GetProjectForFile _ = None
        member _.GetProjectsForFile _ = []
        member _.GetSourceFiles _ = []
        member _.GetDependents _ = []
        member _.GetAffectedProjects _ = []

        member _.GetAllProjects() =
            projects |> List.map AbsProjectPath.create

        member _.GetAllFiles() = sources |> List.map AbsFilePath.create
        member _.GetTargetFramework _ = None
        member _.GetCanonicalDllPath _ = None
        member _.GetMaxSourceMtime _ = None }

[<Fact(Timeout = 5000)>]
let ``BuildInputsHasher produces stable hash for unchanged files`` () =
    withTempDir "binhasher-stable" (fun tmpDir ->
        let f1 = System.IO.Path.Combine(tmpDir, "A.fs")
        let f2 = System.IO.Path.Combine(tmpDir, "B.fs")
        System.IO.File.WriteAllText(f1, "let a = 1")
        System.IO.File.WriteAllText(f2, "let b = 2")

        let graph = stubGraph [ f1; f2 ] []
        let h = BuildInputsHasher(graph)
        test <@ h.Compute() = h.Compute() @>)

[<Fact(Timeout = 5000)>]
let ``BuildInputsHasher hash differs when a source file's content changes`` () =
    withTempDir "binhasher-content" (fun tmpDir ->
        let f1 = System.IO.Path.Combine(tmpDir, "A.fs")
        System.IO.File.WriteAllText(f1, "let a = 1")

        let graph = stubGraph [ f1 ] []
        let h = BuildInputsHasher(graph)
        let before = h.Compute()
        // Brief sleep to ensure mtime advances; the cache key is (path, mtimeTicks).
        System.Threading.Thread.Sleep(50)
        System.IO.File.WriteAllText(f1, "let a = 2")
        test <@ before <> h.Compute() @>)

[<Fact(Timeout = 5000)>]
let ``BuildInputsHasher hash differs when files are added or removed`` () =
    withTempDir "binhasher-fileset" (fun tmpDir ->
        let f1 = System.IO.Path.Combine(tmpDir, "A.fs")
        let f2 = System.IO.Path.Combine(tmpDir, "B.fs")
        System.IO.File.WriteAllText(f1, "let a = 1")
        System.IO.File.WriteAllText(f2, "let b = 2")

        let oneFile = BuildInputsHasher(stubGraph [ f1 ] []).Compute()
        let twoFiles = BuildInputsHasher(stubGraph [ f1; f2 ] []).Compute()
        test <@ oneFile <> twoFiles @>)

[<Fact(Timeout = 5000)>]
let ``BuildInputsHasher does not crash when a listed file is missing`` () =
    withTempDir "binhasher-missing" (fun tmpDir ->
        let exists = System.IO.Path.Combine(tmpDir, "A.fs")
        let missing = System.IO.Path.Combine(tmpDir, "MissingNeverWritten.fs")
        System.IO.File.WriteAllText(exists, "let a = 1")

        let h = BuildInputsHasher(stubGraph [ exists; missing ] [])
        // Missing file is hashed via the read-error sentinel; no exception.
        test <@ not (System.String.IsNullOrEmpty(h.Compute())) @>)

[<Fact(Timeout = 5000)>]
let ``BuildInputsHasher mtime cache returns stable hash across repeat calls`` () =
    withTempDir "binhasher-cache" (fun tmpDir ->
        let f = System.IO.Path.Combine(tmpDir, "A.fs")
        System.IO.File.WriteAllText(f, "let a = 1")

        let h = BuildInputsHasher(stubGraph [ f ] [])
        let h1 = h.Compute()
        let h2 = h.Compute()
        let h3 = h.Compute()
        test <@ h1 = h2 @>
        test <@ h2 = h3 @>)

[<Fact(Timeout = 5000)>]
let ``BuildInputsHasher includes project files in the merkle`` () =
    withTempDir "binhasher-projfiles" (fun tmpDir ->
        let proj = System.IO.Path.Combine(tmpDir, "P.fsproj")
        System.IO.File.WriteAllText(proj, "<Project></Project>")

        let withProj = BuildInputsHasher(stubGraph [] [ proj ]).Compute()
        let empty = BuildInputsHasher(stubGraph [] []).Compute()
        test <@ withProj <> empty @>)

// --- Post-build artifact verification (the BuildSucceeded contract) ---

/// Build a single-project fixture (`MyLib.fsproj` + `Lib.fs` + a fake DLL at
/// the canonical path) and run the plugin against it. The caller controls the
/// relative source/DLL mtimes via `srcOffset`/`dllOffset` (offsets from "now").
/// Returns the recorded `getBuild` so the test can assert the final outcome.
let private runVerifyHarness
    (label: string)
    (srcOffset: TimeSpan)
    (dllOffset: TimeSpan)
    : (unit -> BuildResult option) =
    let mutable captured = ignore
    let mutable result: (unit -> BuildResult option) = fun () -> None

    withTempDir label (fun tmpDir ->
        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
        let (getBuild, recorder) = buildRecorder ()
        let projDir = System.IO.Path.Combine(tmpDir, "MyLib")
        let projPath = System.IO.Path.Combine(projDir, "MyLib.fsproj")
        let srcPath = System.IO.Path.Combine(projDir, "Lib.fs")
        let dllDir = System.IO.Path.Combine(projDir, "bin", "Debug", "net10.0")
        let dllPath = System.IO.Path.Combine(dllDir, "MyLib.dll")
        System.IO.Directory.CreateDirectory(dllDir) |> ignore

        writeMinimalFsproj projPath "net10.0" [ "Lib.fs" ]
        System.IO.File.WriteAllText(srcPath, "let x = 1")
        System.IO.File.WriteAllText(dllPath, "fake-dll")
        let now = DateTime.UtcNow
        System.IO.File.SetLastWriteTimeUtc(srcPath, now + srcOffset)
        System.IO.File.SetLastWriteTimeUtc(dllPath, now + dllOffset)

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(projPath) |> ignore

        // `true` succeeds with empty output — the "MSBuild silently skipped"
        // condition that mtime verification has to disambiguate.
        let handler = BuildPlugin.create "true" "" [] graph [] None [] None
        host.RegisterHandler(recorder)
        host.RegisterHandler(handler)
        host.EmitFileChanged(SourceChanged [ srcPath ])

        waitForTerminalStatus host "build" 5000
        waitUntil (fun () -> (getBuild ()).IsSome) 5000
        result <- getBuild)

    result

[<Fact(Timeout = 5000)>]
let ``BuildPlugin demotes BuildPassed to BuildFailed when canonical DLL is older than sources`` () =
    let getBuild =
        runVerifyHarness "build-verify-stale-demotion" (TimeSpan.Zero) (TimeSpan.FromMinutes(-10.0))

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 5000)>]
let ``BuildPlugin emits BuildSucceeded when canonical DLL is newer than sources`` () =
    let getBuild =
        runVerifyHarness "build-verify-fresh" (TimeSpan.FromMinutes(-5.0)) (TimeSpan.Zero)

    test <@ getBuild () = Some BuildSucceeded @>
