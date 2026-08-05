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

[<Fact(Timeout = 15000)>]
let ``decideBuildOutcome success with clean output yields BuildPassed and no entries`` () =
    let output = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)"
    let (outcome, entries) = decideBuildOutcome true output
    test <@ outcome = BuildPassed output @>
    test <@ entries.IsEmpty @>

[<Fact(Timeout = 15000)>]
let ``decideBuildOutcome success with warnings yields BuildPassed and parsed warnings`` () =
    let output =
        "/src/Bar.fs(3,1): warning FS0040: This construct causes code to be less generic"

    let (outcome, entries) = decideBuildOutcome true output
    test <@ outcome = BuildPassed output @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Warning @>
    test <@ entries.[0].Line = 3 @>

[<Fact(Timeout = 15000)>]
let ``decideBuildOutcome failure with parsed errors yields BuildOutputFailed and parsed entries`` () =
    let output =
        "/src/Foo.fs(12,5): error FS0001: This expression was expected to have type int"

    let (outcome, entries) = decideBuildOutcome false output
    test <@ outcome = BuildOutputFailed [ output ] @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Error @>

[<Fact(Timeout = 15000)>]
let ``decideBuildOutcome failure with empty output yields single synthetic error`` () =
    let (outcome, entries) = decideBuildOutcome false ""
    test <@ outcome = BuildOutputFailed [ "" ] @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Error @>
    test <@ entries.[0].Message = "" @>

[<Fact(Timeout = 15000)>]
let ``decideBuildOutcome failure with unparseable output falls back to raw-text error`` () =
    let output = "Segmentation fault\nrandom stderr blob\nnot an MSBuild line"
    let (outcome, entries) = decideBuildOutcome false output
    test <@ outcome = BuildOutputFailed [ output ] @>
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Message = output @>
    test <@ entries.[0].Severity = DiagnosticSeverity.Error @>

[<Fact(Timeout = 15000)>]
let ``decideBuildOutcome failure with mixed stderr and MSBuild lines prefers parsed entries`` () =
    let output =
        "Startup trace noise\n/src/Foo.fs(12,5): error FS0001: Bad type\nrandom stderr\n/src/Bar.fs(3,1): warning FS0040: Less generic"

    let (outcome, entries) = decideBuildOutcome false output
    test <@ outcome = BuildOutputFailed [ output ] @>
    test <@ entries.Length = 2 @>
    test <@ entries |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Error) @>
    test <@ entries |> List.exists (fun e -> e.Severity = DiagnosticSeverity.Warning) @>

[<Fact(Timeout = 15000)>]
let ``create accepts graph and test project names`` () =
    let graph = FsHotWatch.ProjectGraph.ProjectGraph()
    let handler = BuildPlugin.create "echo" "build" [] graph [] None [] None
    test <@ handler.Name = PluginName.create "build" @>

// NOTE: ``concurrent FileChanged events do not start two builds`` was moved to
// FsHotWatch.IntegrationTests because it spawns a real `sleep 1` subprocess and
// asserts on cross-thread timing windows; under the parallel xUnit collection
// runner with system load it intermittently flakes due to scheduler starvation.

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    test <@ handler.Name = PluginName.create "build" @>

[<Fact(Timeout = 15000)>]
let ``build-status command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(handler)

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact(Timeout = 15000)>]
let ``formatSilentFailureDiagnostic includes exit code and output length`` () =
    let output =
        "Build FAILED.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:02.96"

    let detail = formatSilentFailureDiagnostic 1 output
    test <@ detail.Contains "exit=1" @>
    test <@ detail.Contains $"output={output.Length} bytes" @>
    test <@ detail.Contains "MSBuild aborted" @>

[<Fact(Timeout = 15000)>]
let ``formatSilentFailureDiagnostic includes elapsed time when present in output`` () =
    let output =
        "Build FAILED.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:01:23.45"

    let detail = formatSilentFailureDiagnostic 134 output
    test <@ detail.Contains "elapsed=00:01:23.45" @>

[<Fact(Timeout = 15000)>]
let ``formatSilentFailureDiagnostic omits elapsed when not present`` () =
    let output = "Build FAILED.\n    0 Warning(s)\n    0 Error(s)"
    let detail = formatSilentFailureDiagnostic 1 output
    test <@ not (detail.Contains "elapsed=") @>

[<Fact(Timeout = 15000)>]
let ``build plugin emits BuildCompleted on successful build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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

// ``build plugin honors timeoutSec and records TimedOut outcome`` moved to
// FsHotWatch.IntegrationTests/PluginTimeoutTests.fs (coverage-deterministic; rationale there).

[<Fact(Timeout = 20000)>]
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

[<Fact(Timeout = 15000)>]
let ``build plugin emits BuildFailed on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 12000

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``build plugin reports errors on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 20000)>]
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

[<Fact(Timeout = 15000)>]
let ``build plugin ignores SolutionChanged events`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged)

    // SolutionChanged is ignored — poll briefly; will time out (expected)
    waitUntil (fun () -> (getBuild ()).IsSome) 200

    test <@ getBuild () = None @>

[<Fact(Timeout = 15000)>]
let ``build plugin triggers on ProjectChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
let ``test-file-only change runs a real build (re-emits the DLL) — does not trust BatchChecked as freshness`` () =
    // REGRESSION (stale-binary false-green): a test-file-only edit MUST run the
    // real build so MSBuild re-emits the executable test assembly on disk before
    // test-prune launches it with `dotnet run --no-build`. FCS's in-memory
    // BatchChecked only proves the .fs type-checks; it does NOT emit the runnable
    // DLL for an xUnit v3 standalone-exe project. The old "skip build, wait for
    // BatchChecked, emit BuildSucceeded" path skipped MSBuild, so `--no-build`
    // executed the STALE DLL → false green.
    //
    // Pinned via a build command that FAILS (`false`): if the test-file change is
    // (correctly) built, the failing command runs → BuildFailed. If the build is
    // (wrongly) skipped, the command never runs and BuildSucceeded fires off the
    // BatchChecked signal alone — the exact bug. So BuildFailed here proves the
    // build was NOT skipped for a test-file-only change.
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

    // The real build (`false`) terminates in ms; bound the wait well under the
    // Fact timeout so the assertion runs (under the bug the plugin parks in the
    // skip-wait and never reaches a terminal status — the assert must still fire).
    waitForTerminalStatus host "build" 8000
    waitUntil (fun () -> (getBuild ()).IsSome) 4000

    // The build command ran (and failed) → the build was NOT skipped. A skipped
    // build would have emitted BuildSucceeded without ever invoking `false`.
    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``test-file-only change builds and succeeds without waiting on BatchChecked`` () =
    // Corrected invariant (was "build is skipped when only test files change"):
    // a test-file change runs the real build like any other source change and
    // succeeds when the build command succeeds — no BatchChecked is emitted, yet
    // BuildSucceeded still fires because the build is no longer parked waiting on
    // the FCS cohort signal. This re-emits the on-disk test DLL before test-prune
    // runs it `--no-build` (see ADR-012).
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/tests/MyTests/MyTests.fsproj",
        [ AbsFilePath.create "/tmp/tests/MyTests/Tests.fs" ],
        []
    )

    let handler =
        BuildPlugin.create "echo" "rebuilt" [] graph [ "MyTests" ] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/tests/MyTests/Tests.fs" ])

    // No BatchChecked is emitted — under the old skip path this would hang at None
    // forever; now the real build runs and completes on its own.
    waitForTerminalStatus host "build" 8000
    waitUntil (fun () -> (getBuild ()).IsSome) 4000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
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

    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
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

    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 20000)>]
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

    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
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

    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

// --- dependsOn tests ---

[<Fact(Timeout = 15000)>]
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
    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 20000)>]
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
    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
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
            | Failed(msg, _, _) -> msg.Contains("dependency failed: setup")
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``build with empty dependsOn works normally`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000
    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
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
    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

// --- §2a: BuildPlugin cache key behaviour ---

[<Fact(Timeout = 15000)>]
let ``BuildPlugin cache key is provided regardless of getCommitId`` () =
    let h1 = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    let h2 = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    test <@ h1.CacheKey.IsSome @>
    test <@ h2.CacheKey.IsSome @>

[<Fact(Timeout = 15000)>]
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

    // The framework writes the cache entry (`cache.Set`) AFTER the handler's
    // Update fully returns, whereas `waitForTerminalStatus` observes the
    // terminal status reported *inside* that Update — so the entry can lag the
    // status by a scheduling quantum under load. Poll-until-deadline for the
    // entry instead of reading once (deterministic write, just not yet visible
    // the instant the status flips).
    waitUntil (fun () -> (cacheIface.TryGet key computedKey.Value).IsSome) 5000
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

[<Fact(Timeout = 15000)>]
let ``BuildPlugin cache key matches between FileChanged and Custom BuildDone`` () =
    // The cache stores a result on the synchronous Custom BuildDone handler;
    // future FileChanged events look up by the same merkle key. Both must
    // compute identical keys for the cache to hit.
    let handler = warmedHandler "echo" "ok" []

    let cacheKeyFn = handler.CacheKey.Value
    let fileEvt = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])

    let buildDoneEvt = Custom(BuildDone(BuildPassed "x", [], System.TimeSpan.Zero))

    let fileKey = cacheKeyFn fileEvt
    let doneKey = cacheKeyFn buildDoneEvt
    test <@ fileKey.IsSome @>
    test <@ fileKey = doneKey @>

[<Fact(Timeout = 15000)>]
let ``BuildPlugin does not subscribe to BatchChecked`` () =
    // The build plugin no longer reacts to BatchChecked: every source change
    // (test files included) drives a real build, so there is no test-only-skip
    // phase that waited on the FCS cohort signal. TestPrune still owns its
    // BatchChecked subscription (the AffectedTests seal); the build plugin must
    // not, or a test-only edit could be sealed without re-emitting the DLL.
    let handler = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    test <@ not (handler.Subscriptions.Contains(SubscribeBatchChecked)) @>

[<Fact(Timeout = 15000)>]
let ``BuildPlugin cache key reflects build command`` () =
    // §2a: changing the build command/args should invalidate the cache.
    // Tests the pure merkle directly, bypassing the cold-start guard.
    let inputs = "stub-inputs-hash"
    let k1 = BuildPlugin.computeBuildCacheKey "dotnet" "build" [] inputs
    let k2 = BuildPlugin.computeBuildCacheKey "dotnet" "test" [] inputs
    test <@ k1 <> k2 @>

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher produces stable hash for unchanged files`` () =
    withTempDir "binhasher-stable" (fun tmpDir ->
        let f1 = System.IO.Path.Combine(tmpDir, "A.fs")
        let f2 = System.IO.Path.Combine(tmpDir, "B.fs")
        System.IO.File.WriteAllText(f1, "let a = 1")
        System.IO.File.WriteAllText(f2, "let b = 2")

        let graph = stubGraph [ f1; f2 ] []
        let h = BuildInputsHasher(graph)
        test <@ h.Compute() = h.Compute() @>)

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher hash differs when files are added or removed`` () =
    withTempDir "binhasher-fileset" (fun tmpDir ->
        let f1 = System.IO.Path.Combine(tmpDir, "A.fs")
        let f2 = System.IO.Path.Combine(tmpDir, "B.fs")
        System.IO.File.WriteAllText(f1, "let a = 1")
        System.IO.File.WriteAllText(f2, "let b = 2")

        let oneFile = BuildInputsHasher(stubGraph [ f1 ] []).Compute()
        let twoFiles = BuildInputsHasher(stubGraph [ f1; f2 ] []).Compute()
        test <@ oneFile <> twoFiles @>)

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher returns 'missing' sentinel for non-existent file`` () =
    withTempDir "binhasher-missing" (fun tmpDir ->
        let exists = System.IO.Path.Combine(tmpDir, "A.fs")
        let missing = System.IO.Path.Combine(tmpDir, "MissingNeverWritten.fs")
        System.IO.File.WriteAllText(exists, "let a = 1")

        // Missing file is hashed via the explicit "missing" sentinel; no exception,
        // and the merkle distinguishes a file-set with a missing entry from one
        // without it (the old read-error swallow could collapse keys to (path,0L)).
        let withMissing = BuildInputsHasher(stubGraph [ exists; missing ] []).Compute()
        let onlyExists = BuildInputsHasher(stubGraph [ exists ] []).Compute()
        test <@ not (System.String.IsNullOrEmpty(withMissing)) @>
        test <@ withMissing <> onlyExists @>)

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher distinct missing paths produce distinct merkles`` () =
    // Belt-and-suspenders: the "missing" sentinel must still be combined with
    // the path (else two different missing files would collapse to one merkle
    // entry — exactly the silent under-build the old (path,0L) swallow caused).
    withTempDir "binhasher-missing-distinct" (fun tmpDir ->
        let m1 = System.IO.Path.Combine(tmpDir, "M1.fs")
        let m2 = System.IO.Path.Combine(tmpDir, "M2.fs")
        let h1 = BuildInputsHasher(stubGraph [ m1 ] []).Compute()
        let h2 = BuildInputsHasher(stubGraph [ m2 ] []).Compute()
        test <@ h1 <> h2 @>)

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher propagates IOException from unreadable file`` () =
    // Simulate IO failure: chmod 000 a real file. ReadAllText throws
    // UnauthorizedAccessException, which now propagates instead of being
    // swallowed as a "read-error" cache entry that would poison the merkle.
    withTempDir "binhasher-unreadable" (fun tmpDir ->
        let f = System.IO.Path.Combine(tmpDir, "Locked.fs")
        System.IO.File.WriteAllText(f, "let a = 1")

        let canSimulate =
            try
                System.IO.File.SetUnixFileMode(f, System.IO.UnixFileMode.None)

                try
                    System.IO.File.ReadAllText f |> ignore
                    false // running as root or filesystem ignores perms
                with _ ->
                    true
            with _ ->
                false

        if canSimulate then
            try
                let h = BuildInputsHasher(stubGraph [ f ] [])

                let raised =
                    try
                        h.Compute() |> ignore
                        false
                    with
                    | :? System.UnauthorizedAccessException
                    | :? System.IO.IOException -> true

                test <@ raised @>
            finally
                // restore perms so the temp-dir cleanup can delete the file
                try
                    System.IO.File.SetUnixFileMode(
                        f,
                        System.IO.UnixFileMode.UserRead ||| System.IO.UnixFileMode.UserWrite
                    )
                with _ ->
                    ())

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher merkle is stable across repeat computes when content is unchanged`` () =
    // UPDATED (Bug 1): this test previously asserted that mutating content while
    // preserving mtime returns the SAME merkle ("caches by (path, mtime):
    // repeated computes hash once", `first = second` after a content rewrite).
    // That enshrined the very bug that produced the FS1178 phantom — the merkle
    // is supposed to track on-disk CONTENT, and (path, mtime) is not a safe
    // proxy for it. The mtime-preserved content rewrite is now covered by
    // `... hash differs when content changes but mtime is preserved (rsync -a)`.
    //
    // The legitimate invariant the merkle must satisfy is idempotence: hashing
    // the SAME on-disk content repeatedly yields the SAME merkle (so an
    // unchanged tree produces a stable cache key). That is what this asserts.
    withTempDir "binhasher-stable" (fun tmpDir ->
        let f = System.IO.Path.Combine(tmpDir, "A.fs")
        System.IO.File.WriteAllText(f, "let a = 1")
        let mtime = System.IO.File.GetLastWriteTimeUtc(f)

        let h = BuildInputsHasher(stubGraph [ f ] [])
        let first = h.Compute()

        // Content is NOT changed; mtime is left as-is. Repeat computes must agree.
        let second = h.Compute()
        System.IO.File.SetLastWriteTimeUtc(f, mtime)
        let third = h.Compute()

        // Stable merkle for unchanged content — the correctness-preserving
        // property (not "mtime hides content changes").
        test <@ first = second @>
        test <@ second = third @>)

// --- REGRESSION (Bug 1): stale FCS phantom from mtime-preserved content rewrite ---
//
// Production incident (thellma/intelligence, 3rd recurrence, ~30 min to
// diagnose): vendored F# source under a gitignored `paket-files/.../sqlhydra/`
// dir is compiled as part of the build graph. After the vendored files were
// rewritten on disk by `rsync` (which with `-a`/`--times` PRESERVES the source
// mtime), the build plugin kept compiling a CACHED OLD VERSION — reporting
// `FS1178: type 'Provider' is not structurally comparable` for a type that does
// not exist anywhere in the on-disk source (verified by grep; `diff -r` against
// a known-good checkout was byte-identical). ~1700 cascading errors across 392
// files. `fshw scan` did NOT clear it; only `fshw stop` + cold rebuild did.
//
// Root cause: `BuildInputsHasher.hashFile` caches `(path, mtimeTicks) ->
// contentHash`. When content changes but mtime does NOT (mtime-preserving
// rsync, `cp -p`, `tar` extraction, git checkout that restores an old mtime),
// the cache returns the STALE hash, the build merkle key is unchanged, and the
// build-plugin task cache replays a stale `BuildDone` forever. The gitignored
// path is also outside the watch set, so no FileChanged event ever fires to
// invalidate it either — hence "stale forever".
//
// Fix: the input merkle reflects on-disk CONTENT, not (path, mtime). mtime is
// never trusted to prove content equality (size + mtime, or content re-hash on
// size/mtime ambiguity, would all still be fooled by a same-size mtime-preserved
// rewrite — only a content hash is safe).

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher hash differs when content changes but mtime is preserved (rsync -a)`` () =
    withTempDir "binhasher-mtime-preserved" (fun tmpDir ->
        let f = System.IO.Path.Combine(tmpDir, "Vendored.fs")
        System.IO.File.WriteAllText(f, "type Provider = { X: int }")
        let originalMtime = System.IO.File.GetLastWriteTimeUtc(f)

        let h = BuildInputsHasher(stubGraph [ f ] [])
        let before = h.Compute()

        // Simulate `rsync -a` over the vendored file: content is rewritten to a
        // genuinely different definition, but the mtime is restored to the
        // source's (preserved) value — exactly what bit downstream.
        System.IO.File.WriteAllText(f, "type SomethingElse = { Y: string }")
        System.IO.File.SetLastWriteTimeUtc(f, originalMtime)

        let after = h.Compute()

        // The merkle MUST reflect the new on-disk content. If it does not, the
        // build cache key is stable across a real content change and a stale
        // BuildDone replays forever (the FS1178 phantom).
        test <@ before <> after @>)

[<Fact(Timeout = 15000)>]
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

[<Fact(Timeout = 15000)>]
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
        waitUntil (fun () -> (getBuild ()).IsSome) 12000
        result <- getBuild)

    result

[<Fact(Timeout = 15000)>]
let ``BuildPlugin demotes BuildPassed to BuildFailed when canonical DLL is older than sources`` () =
    let getBuild =
        runVerifyHarness "build-verify-stale-demotion" (TimeSpan.Zero) (TimeSpan.FromMinutes(-10.0))

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``BuildPlugin emits BuildSucceeded when canonical DLL is newer than sources`` () =
    let getBuild =
        runVerifyHarness "build-verify-fresh" (TimeSpan.FromMinutes(-5.0)) (TimeSpan.Zero)

    test <@ getBuild () = Some BuildSucceeded @>

// --- Template build failure paths (startTemplateBuild's TimedOut/Failed/exception arms) ---

[<Fact(Timeout = 30000)>]
let ``template build with failing command emits BuildFailed and reports Failed status`` () =
    // Drives the Failed-result arm of startTemplateBuild: the rendered template
    // exits non-zero, so failures accumulates, applyBuildOutcome takes the
    // BuildOutputFailed arm, and Custom(BuildDone) reports Failed status.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/src/MyLib/MyLib.fsproj",
        [ AbsFilePath.create "/tmp/src/MyLib/Lib.fs" ],
        []
    )

    let handler =
        BuildPlugin.create "should-not-run" "" [] graph [] (Some "false {project}") [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "/tmp/src/MyLib/Lib.fs" ])

    waitForTerminalStatus host "build" 20000
    waitUntil (fun () -> (getBuild ()).IsSome) 20000

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

    let status = host.GetStatus("build")

    test
        <@
            match status with
            | Some(FsHotWatch.Events.Failed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``template build honors timeoutSec and surfaces TimedOut`` () =
    // Drives the TimedOut arm of startTemplateBuild (lines 380-384): the rendered
    // template runs `sleep 10` against a 1-second timeout.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/src/MyLib/MyLib.fsproj",
        [ AbsFilePath.create "/tmp/src/MyLib/Lib.fs" ],
        []
    )

    let handler =
        BuildPlugin.create "should-not-run" "" [] graph [] (Some "sleep 10") [] (Some 1)

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "/tmp/src/MyLib/Lib.fs" ])

    waitForTerminalStatus host "build" 8000
    waitUntil (fun () -> (getBuild ()).IsSome) 8000

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

// --- test-file changes drive a real build (no skip-and-wait phase) ---

[<Fact(Timeout = 30000)>]
let ``ProjectChanged after a test-file change runs a real build`` () =
    // Previously a test-file change parked the plugin in WaitingForBatchPhase and
    // a ProjectChanged had to interrupt that wait. Now the test-file change builds
    // immediately; a following ProjectChanged builds again. Both reach
    // BuildSucceeded — neither is silently dropped.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/tests/MyTests/MyTests.fsproj",
        [ AbsFilePath.create "/tmp/tests/MyTests/Tests.fs" ],
        []
    )

    let handler =
        BuildPlugin.create "echo" "rebuilt" [] graph [ "MyTests" ] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    // Test-file change → real build (no wait on BatchChecked).
    host.EmitFileChanged(SourceChanged [ "/tmp/tests/MyTests/Tests.fs" ])
    waitForTerminalStatus host "build" 20000
    waitUntil (fun () -> (getBuild ()).IsSome) 8000
    test <@ getBuild () = Some BuildSucceeded @>

    // ProjectChanged → another real build.
    host.EmitFileChanged(ProjectChanged [ "/tmp/tests/MyTests/MyTests.fsproj" ])
    waitForTerminalStatus host "build" 20000
    waitUntil (fun () -> (getBuild ()).IsSome) 8000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 30000)>]
let ``a mixed test-and-source change runs a real build`` () =
    // A change touching both a test file and a non-test source file builds once
    // and succeeds — it was never the skip path, and still isn't.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let graph = ProjectGraph()

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/tests/MyTests/MyTests.fsproj",
        [ AbsFilePath.create "/tmp/tests/MyTests/A.fs" ],
        []
    )

    graph.RegisterProject(
        AbsProjectPath.create "/tmp/src/MyLib/MyLib.fsproj",
        [ AbsFilePath.create "/tmp/src/MyLib/Lib.fs" ],
        []
    )

    let handler =
        BuildPlugin.create "echo" "non-test rebuild" [] graph [ "MyTests" ] None [] None

    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "/tmp/tests/MyTests/A.fs"; "/tmp/src/MyLib/Lib.fs" ])
    waitForTerminalStatus host "build" 20000
    waitUntil (fun () -> (getBuild ()).IsSome) 8000
    test <@ getBuild () = Some BuildSucceeded @>

// --- build-status command in failed-state lifecycles ---

[<Fact(Timeout = 30000)>]
let ``build-status returns failed JSON after BuildOutputFailed lifecycle`` () =
    // Drives the build-status command path that reads `Lifecycle.value` and
    // matches BuildOutputFailed (lines 615-619).
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitForTerminalStatus host "build" 20000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    let doc = JsonDocument.Parse(result.Value)
    Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString())
    // The "output" field exists even when stdout was empty (false produces no
    // bytes); having the JSON shape proves the BuildOutputFailed serializer arm.
    let mutable outputProp = Unchecked.defaultof<JsonElement>
    let hasOutput = doc.RootElement.TryGetProperty("output", &outputProp)
    test <@ hasOutput @>

[<Fact(Timeout = 30000)>]
let ``build-status returns failed JSON after BuildArtifactsStale demotion`` () =
    // Combine the runVerifyHarness pattern (which produces BuildArtifactsStale)
    // with build-status to drive the BuildArtifactsStale arm of the JSON
    // serializer (lines 609-614).
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    withTempDir "build-status-stale" (fun tmpDir ->
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
        System.IO.File.SetLastWriteTimeUtc(srcPath, now)
        // DLL older than source → demotion to BuildArtifactsStale.
        System.IO.File.SetLastWriteTimeUtc(dllPath, now - TimeSpan.FromMinutes(10.0))

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(projPath) |> ignore

        let handler = BuildPlugin.create "true" "" [] graph [] None [] None
        host.RegisterHandler(handler)
        host.EmitFileChanged(SourceChanged [ srcPath ])

        waitForTerminalStatus host "build" 20000

        let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        let doc = JsonDocument.Parse(result.Value)
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString())
        // The stale-diagnostic body must surface the "MSBuild lied" prefix.
        let output = doc.RootElement.GetProperty("output").GetString()
        test <@ output.Contains("stale") || output.Contains("MSBuild") @>)

// ---------------------------------------------------------------------------
// AUTOMATION-224 — `force-rebuild`: a cache hit must not assert freshness it
// never verified.
//
// The build cache key is a content merkle over SOURCE files only. A hit therefore
// claims "the outputs are up to date" on evidence that never looked at the
// outputs. That claim is false whenever `bin/` is changed out from under a tree
// whose sources are unchanged — a working-copy flip being the usual way. The
// build then replays "built N projects (cached)" without running, TestPrune's
// freshness gate correctly finds the output stale and defers every affected
// project as "waiting on build", and NOTHING EVER REBUILDS. That deadlock blocked
// a production deploy three times; `dotnet fshw scan` was the only escape, and it
// worked only because it forced a real build.
//
// `confirm` issues `force-rebuild` for the same reason it already forces a
// from-disk scan: the merge verb does not get to trust a cache when its entire
// job is to be the thing everything else trusts.
// ---------------------------------------------------------------------------

/// A handler warmed through one real build, plus its cache-key function.
let private warmedWithKeyFn () =
    let handler = warmedHandler "echo" "ok" []
    handler, handler.CacheKey.Value

[<Fact(Timeout = 15000)>]
let ``force-rebuild makes the next FileChanged lookup miss the build cache`` () =
    // THE REGRESSION. Before the fix the key was unconditional, so a warm cache
    // replayed a BuildPassed whose artifacts were long gone and the deadlock
    // above became unexitable.
    let handler, cacheKeyFn = warmedWithKeyFn ()
    let fileEvt = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])

    // Baseline: cacheable, so the ordinary warm path still gets its cache.
    // Bound outside the quotation — Unquote cannot splice an inner generic call.
    let before = cacheKeyFn fileEvt
    test <@ before.IsSome @>

    handler.Commands
    |> List.find (fun (name, _) -> name = "force-rebuild")
    |> snd
    |> fun run -> run Unchecked.defaultof<_> Unchecked.defaultof<_> [||] |> Async.RunSynchronously
    |> ignore

    // `None` is the framework's documented "skip the cache, run Update" bypass —
    // i.e. a REAL build, which is the whole point.
    let after = cacheKeyFn fileEvt
    test <@ after.IsNone @>

[<Fact(Timeout = 15000)>]
let ``force-rebuild still lets the fresh build's result be cached`` () =
    // Asymmetry is deliberate: suppressing the STORE too would make every forced
    // build permanently uncacheable, turning a correctness fix into a standing
    // performance regression.
    let handler, cacheKeyFn = warmedWithKeyFn ()

    handler.Commands
    |> List.find (fun (name, _) -> name = "force-rebuild")
    |> snd
    |> fun run -> run Unchecked.defaultof<_> Unchecked.defaultof<_> [||] |> Async.RunSynchronously
    |> ignore

    let buildDoneEvt = Custom(BuildDone(BuildPassed "x", [], TimeSpan.Zero))

    let lookupKey = cacheKeyFn (FileChanged(SourceChanged [ "/tmp/Foo.fs" ]))
    let storeKey = cacheKeyFn buildDoneEvt
    test <@ lookupKey.IsNone @>
    test <@ storeKey.IsSome @>

[<Fact(Timeout = 15000)>]
let ``force-rebuild is spent by a completed build, not by the lookup alone`` () =
    // Clearing on the LOOKUP would let a dispatch that never reached a build
    // consume the request and leave the artifacts stale anyway — the same
    // deadlock, one run later and harder to see.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    let handler = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitForTerminalStatus host "build" 5000

    let cacheKeyFn = handler.CacheKey.Value
    let fileEvt = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])

    host.RunCommand("force-rebuild", [||]) |> Async.RunSynchronously |> ignore
    let whileForced = cacheKeyFn fileEvt
    test <@ whileForced.IsNone @>

    // Drive a real build to completion; the request is now satisfied.
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Poll rather than `waitForTerminalStatus`: the status is ALREADY terminal
    // from the warm-up build above, so a wait-for-terminal is satisfied instantly
    // and would read the flag before the second build's BuildDone lands. (That
    // already-satisfied-wait shape is the same class of bug as AUTOMATION-224
    // itself — worth not reproducing in its own regression test.)
    waitUntil (fun () -> (cacheKeyFn fileEvt).IsSome) 5000

    let afterBuild = cacheKeyFn fileEvt
    test <@ afterBuild.IsSome @>

[<Fact(Timeout = 15000)>]
let ``the build plugin's force-rebuild command matches the name the CLI sends`` () =
    // Plugins sit below the CLI, so the name is a bare literal on this side and a
    // [<Literal>] on the other. Two spellings of one contract is exactly the
    // failure this pins: a rename on either side would otherwise degrade silently
    // to "unknown command" and quietly restore the deadlock.
    let handler = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None

    let names = handler.Commands |> List.map fst
    let expected = FsHotWatch.Cli.IpcParsing.ForceRebuildCommand
    test <@ names |> List.contains expected @>
