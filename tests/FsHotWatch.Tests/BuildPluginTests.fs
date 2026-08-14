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

// ``concurrent FileChanged events do not start two builds`` lives in
// FsHotWatch.IntegrationTests: it spawns a real `sleep 1` and asserts on cross-thread timing
// windows, which flakes under the parallel runner from scheduler starvation.

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

    // SolutionChanged is ignored, so this wait is expected to time out.
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
    // Stale-binary false green: FCS's in-memory BatchChecked proves the .fs type-checks but
    // does NOT emit the runnable DLL for an xUnit v3 standalone-exe project. The old "skip
    // build, wait for BatchChecked, emit BuildSucceeded" path skipped MSBuild, so
    // `dotnet run --no-build` executed the STALE DLL.
    //
    // Pinned via a build command that FAILS (`false`): a built test-file change runs it and
    // reports BuildFailed, whereas a skipped build never invokes it and reports
    // BuildSucceeded off the BatchChecked signal alone.
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

    // `false` terminates in ms; the wait is bounded well under the Fact timeout so that
    // under the bug — where the plugin parks in the skip-wait forever — the assertion below
    // still fires.
    waitForTerminalStatus host "build" 8000
    waitUntil (fun () -> (getBuild ()).IsSome) 4000

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``test-file-only change builds and succeeds without waiting on BatchChecked`` () =
    // No BatchChecked is emitted, yet BuildSucceeded still fires: the build is no longer
    // parked waiting on the FCS cohort signal, so the on-disk test DLL is re-emitted before
    // test-prune runs it `--no-build` (see ADR-012).
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

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // A short bound: this wait is expected to time out, so it only has to be long enough to
    // catch a build that wrongly started.
    waitUntil (fun () -> (getBuild ()).IsSome) 500
    test <@ getBuild () = None @>

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

    host.EmitCommandCompleted(
        { Name = "setup"
          Outcome = CommandSucceeded "ok" }
    )

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

    host.EmitCommandCompleted(
        { Name = "setup"
          Outcome = CommandSucceeded "ok" }
    )

    waitUntil (fun () -> (getBuild ()).IsSome) 500
    test <@ getBuild () = None @>

    host.EmitCommandCompleted(
        { Name = "codegen"
          Outcome = CommandSucceeded "ok" }
    )

    waitForTerminalStatus host "build" 5000
    waitUntil (fun () -> (getBuild ()).IsSome) 12000
    test <@ getBuild () = Some BuildSucceeded @>

// --- BuildPlugin cache key behaviour ---

[<Fact(Timeout = 15000)>]
let ``BuildPlugin cache key is provided regardless of getCommitId`` () =
    let h1 = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    let h2 = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    test <@ h1.CacheKey.IsSome @>
    test <@ h2.CacheKey.IsSome @>

[<Fact(Timeout = 15000)>]
let ``regression: BuildPlugin writes a cache entry on terminal Custom BuildDone`` () =
    // `applyBuildOutcome` used to emit from inside the fire-and-forget async, so the
    // framework's per-event cache-write window for FileChanged saw only "Running" and the
    // Custom BuildDone window had nothing left to capture. The captured operations now live
    // in the Custom BuildDone handler, which runs synchronously.
    let cache = FsHotWatch.TaskCache.InMemoryTaskCache()
    let cacheIface = cache :> FsHotWatch.TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cacheIface)

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitForTerminalStatus host "build" 5000

    let key: FsHotWatch.TaskCache.CompositeKey = { Plugin = "build"; File = None }

    let cacheKeyFn = handler.CacheKey.Value
    // The lookup happens at FileChanged time in production, and the entry is stored under
    // the same merkle key from either site — they share the input set.
    let computedKey = cacheKeyFn (FileChanged(SourceChanged [ "src/Lib.fs" ]))
    test <@ computedKey.IsSome @>

    // `cache.Set` runs AFTER the handler's Update returns, whereas `waitForTerminalStatus`
    // observes the status reported *inside* it, so the entry can lag the status by a
    // scheduling quantum under load. Poll rather than read once.
    waitUntil (fun () -> (cacheIface.TryGet key computedKey.Value).IsSome) 5000
    let result = cacheIface.TryGet key computedKey.Value
    test <@ result.IsSome @>
    // The captured BuildCompleted is what cache replay re-fires to TestPrune and Coverage.
    test <@ not result.Value.EmittedEvents.IsEmpty @>

// Drive a real build through the host so the plugin's cold-start guard flips before the
// cache key is inspected.
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
    // The result is stored from the synchronous Custom BuildDone handler and looked up from
    // a later FileChanged, so both must compute identical keys for the cache to hit.
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
    // Every source change (test files included) drives a real build, so there is no
    // test-only-skip phase waiting on the FCS cohort signal. TestPrune still owns its
    // BatchChecked subscription (the AffectedTests seal); the build plugin must not, or a
    // test-only edit could be sealed without re-emitting the DLL.
    let handler = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None
    test <@ not (handler.Subscriptions.Contains(SubscribeBatchChecked)) @>

[<Fact(Timeout = 15000)>]
let ``BuildPlugin cache key reflects build command`` () =
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

// --- BuildInputsHasher (extracted via internal visibility for testability) ---

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
        // Advance the mtime so this is not also a same-mtime case.
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

        // No exception, and the merkle distinguishes a file-set with a missing entry from
        // one without it — the old read-error swallow collapsed keys to (path, 0L).
        let withMissing = BuildInputsHasher(stubGraph [ exists; missing ] []).Compute()
        let onlyExists = BuildInputsHasher(stubGraph [ exists ] []).Compute()
        test <@ not (System.String.IsNullOrEmpty(withMissing)) @>
        test <@ withMissing <> onlyExists @>)

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher distinct missing paths produce distinct merkles`` () =
    // The "missing" sentinel must still be combined with the path, or two different missing
    // files collapse to one merkle entry — the silent under-build the old swallow caused.
    withTempDir "binhasher-missing-distinct" (fun tmpDir ->
        let m1 = System.IO.Path.Combine(tmpDir, "M1.fs")
        let m2 = System.IO.Path.Combine(tmpDir, "M2.fs")
        let h1 = BuildInputsHasher(stubGraph [ m1 ] []).Compute()
        let h2 = BuildInputsHasher(stubGraph [ m2 ] []).Compute()
        test <@ h1 <> h2 @>)

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher propagates IOException from unreadable file`` () =
    // chmod 000 makes ReadAllText throw UnauthorizedAccessException, which must propagate
    // rather than be swallowed as a "read-error" cache entry that poisons the merkle.
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
    // The trap: this once asserted that mutating content while PRESERVING mtime returns the
    // same merkle, which enshrined the FS1178 phantom below. The invariant is idempotence —
    // hashing the same on-disk CONTENT repeatedly yields the same merkle — never that mtime
    // is a proxy for content.
    withTempDir "binhasher-stable" (fun tmpDir ->
        let f = System.IO.Path.Combine(tmpDir, "A.fs")
        System.IO.File.WriteAllText(f, "let a = 1")
        let mtime = System.IO.File.GetLastWriteTimeUtc(f)

        let h = BuildInputsHasher(stubGraph [ f ] [])
        let first = h.Compute()

        let second = h.Compute()
        System.IO.File.SetLastWriteTimeUtc(f, mtime)
        let third = h.Compute()

        test <@ first = second @>
        test <@ second = third @>)

// --- REGRESSION: stale FCS phantom from an mtime-preserved content rewrite ---
//
// `BuildInputsHasher.hashFile` used to cache `(path, mtimeTicks) -> contentHash`. When
// content changes but mtime does NOT (`rsync -a`, `cp -p`, `tar` extraction, a git checkout
// restoring an old mtime), the cache returns the STALE hash, the build merkle is unchanged,
// and the task cache replays a stale `BuildDone` forever. In the incident this pins, the
// affected files were also gitignored — outside the watch set — so no FileChanged ever
// fired to invalidate them either, and the daemon reported `FS1178` for a type that did not
// exist anywhere on disk until a full `fshw stop` + cold rebuild.
//
// So the merkle reflects on-disk CONTENT. mtime is never trusted to prove content equality:
// size + mtime, or a re-hash on size/mtime ambiguity, are all still fooled by a same-size
// mtime-preserved rewrite.

[<Fact(Timeout = 15000)>]
let ``BuildInputsHasher hash differs when content changes but mtime is preserved (rsync -a)`` () =
    withTempDir "binhasher-mtime-preserved" (fun tmpDir ->
        let f = System.IO.Path.Combine(tmpDir, "Vendored.fs")
        System.IO.File.WriteAllText(f, "type Provider = { X: int }")
        let originalMtime = System.IO.File.GetLastWriteTimeUtc(f)

        let h = BuildInputsHasher(stubGraph [ f ] [])
        let before = h.Compute()

        // `rsync -a` over the vendored file: a genuinely different definition, with the
        // mtime restored to the source's preserved value.
        System.IO.File.WriteAllText(f, "type SomethingElse = { Y: string }")
        System.IO.File.SetLastWriteTimeUtc(f, originalMtime)

        let after = h.Compute()

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

/// Build a single-project fixture (`MyLib.fsproj` + `Lib.fs` + a fake DLL at the canonical
/// path) and run the plugin against it. The caller controls the relative source/DLL mtimes
/// via `srcOffset`/`dllOffset` (offsets from "now").
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

        // `true` succeeds with empty output — the "MSBuild silently skipped" condition that
        // mtime verification has to disambiguate.
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
    // Drives the Failed-result arm of `startTemplateBuild`.
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
    // Drives the TimedOut arm of `startTemplateBuild`: `sleep 10` against a 1s timeout.
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
    // A test-file change used to park the plugin in WaitingForBatchPhase, needing a
    // ProjectChanged to interrupt the wait. Both builds must now reach BuildSucceeded on
    // their own, with neither silently dropped.
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
    waitForTerminalStatus host "build" 20000
    waitUntil (fun () -> (getBuild ()).IsSome) 8000
    test <@ getBuild () = Some BuildSucceeded @>

    host.EmitFileChanged(ProjectChanged [ "/tmp/tests/MyTests/MyTests.fsproj" ])
    waitForTerminalStatus host "build" 20000
    waitUntil (fun () -> (getBuild ()).IsSome) 8000
    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 30000)>]
let ``a mixed test-and-source change runs a real build`` () =
    // A mixed change was never the skip path, and still isn't.
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
    // Drives the build-status path that reads `Lifecycle.value` and matches
    // BuildOutputFailed.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None
    host.RegisterHandler(handler)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    waitForTerminalStatus host "build" 20000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    let doc = JsonDocument.Parse(result.Value)
    Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString())
    // The "output" field exists even when stdout was empty (`false` produces no bytes) —
    // the JSON shape is what proves the BuildOutputFailed serializer arm.
    let mutable outputProp = Unchecked.defaultof<JsonElement>
    let hasOutput = doc.RootElement.TryGetProperty("output", &outputProp)
    test <@ hasOutput @>

[<Fact(Timeout = 30000)>]
let ``build-status returns failed JSON after BuildArtifactsStale demotion`` () =
    // The runVerifyHarness shape (which produces BuildArtifactsStale) plus build-status,
    // to drive that arm of the JSON serializer.
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
// AUTOMATION-224 — `force-rebuild`: a cache hit must not assert freshness it never
// verified.
//
// The build cache key is a content merkle over SOURCE files only, so a hit claims "the
// outputs are up to date" on evidence that never looked at the outputs. That is false
// whenever `bin/` changes under a tree whose sources did not — a working-copy flip being
// the usual way. The build then replays "built N projects (cached)" without running,
// TestPrune's freshness gate correctly finds the output stale and defers every affected
// project as "waiting on build", and NOTHING EVER REBUILDS. `dotnet fshw scan` was the only
// escape from that deadlock, and only because it forced a real build.
//
// `confirm` issues `force-rebuild` for the same reason it already forces a from-disk scan:
// the merge verb does not get to trust a cache when its job is to be what everything else
// trusts.
// ---------------------------------------------------------------------------

/// A handler warmed through one real build, plus its cache-key function.
let private warmedWithKeyFn () =
    let handler = warmedHandler "echo" "ok" []
    handler, handler.CacheKey.Value

[<Fact(Timeout = 15000)>]
let ``force-rebuild makes the next FileChanged lookup miss the build cache`` () =
    // The key used to be unconditional, so a warm cache replayed a BuildPassed whose
    // artifacts were long gone and the deadlock above became unexitable.
    let handler, cacheKeyFn = warmedWithKeyFn ()
    let fileEvt = FileChanged(SourceChanged [ "/tmp/Foo.fs" ])

    // Baseline: the ordinary warm path still gets its cache. Bound outside the quotation —
    // Unquote cannot splice an inner generic call.
    let before = cacheKeyFn fileEvt
    test <@ before.IsSome @>

    handler.Commands
    |> List.find (fun (name, _) -> name = "force-rebuild")
    |> snd
    |> fun run -> run Unchecked.defaultof<_> Unchecked.defaultof<_> [||] |> Async.RunSynchronously
    |> ignore

    // `None` is the framework's "skip the cache, run Update" bypass — i.e. a REAL build.
    let after = cacheKeyFn fileEvt
    test <@ after.IsNone @>

[<Fact(Timeout = 15000)>]
let ``force-rebuild still lets the fresh build's result be cached`` () =
    // The asymmetry is deliberate: suppressing the STORE too would make every forced build
    // permanently uncacheable, turning a correctness fix into a standing perf regression.
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
    // Clearing on the LOOKUP would let a dispatch that never reached a build consume the
    // request and leave the artifacts stale — the same deadlock, one run later.
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

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // Poll rather than `waitForTerminalStatus`: the status is ALREADY terminal from the
    // warm-up build, so a wait-for-terminal is satisfied instantly and reads the flag before
    // the second build's BuildDone lands.
    //
    // `waitUntilTrue`, not `waitUntil`: the unit-returning version gives up SILENTLY on
    // timeout, so a loaded machine that missed the budget failed the assertion below — which
    // reads as "force-rebuild was never spent by the build", a real bug's signature
    // manufactured by slowness. Asserting the wait separates that from "we stopped looking".
    let built = waitUntilTrue (fun () -> (cacheKeyFn fileEvt).IsSome) 11000

    test <@ built @>

    let afterBuild = cacheKeyFn fileEvt
    test <@ afterBuild.IsSome @>

[<Fact(Timeout = 15000)>]
let ``the build plugin's force-rebuild command matches the name the CLI sends`` () =
    // Plugins sit below the CLI, so the name is a bare literal here and a [<Literal>] there.
    // A rename on either side degrades silently to "unknown command", restoring the deadlock.
    let handler = BuildPlugin.create "echo" "ok" [] (ProjectGraph()) [] None [] None

    let names = handler.Commands |> List.map fst
    let expected = FsHotWatch.Cli.IpcParsing.ForceRebuildCommand
    test <@ names |> List.contains expected @>

// ---------------------------------------------------------------------------
// AUTOMATION-245 — re-verify the ARTIFACTS at cache-REPLAY time, not only on the
// real-build path.
//
// 224 gave `confirm` an escape hatch. `check` kept the deadlock, and it is reachable
// without anyone killing a build: the cache key is a content merkle over SOURCES, so a
// file rewritten to byte-identical content (a formatter's no-op pass), a `bin/` from
// another workspace, or a checkout with no `bin/` at all leaves the key unmoved. The
// stored `BuildPassed` replays as "built N projects (cached)", TestPrune's freshness
// gate compares MTIMES, correctly finds the output older than its source, and defers.
// Neither side is wrong and neither side moves — and because nothing in the loop is a
// function of the previous attempt, re-running `check` reproduces it verbatim.
//
// The arbiter is the artifacts: a cache hit may not assert freshness it has never
// confirmed. Every absence assertion below ("the cache is NOT served") is paired with a
// positive control on the SAME detector ("with fresh artifacts it IS served"), because a
// gate that suppressed the key unconditionally would satisfy the absence half while
// turning every check into a full rebuild.
// ---------------------------------------------------------------------------

/// A one-project graph on disk: `MyLib.fsproj` + `Lib.fs` + a fake DLL at exactly the
/// canonical path `GetCanonicalDllPath` computes. The DLL starts NEWER than the source
/// (i.e. fresh), so each test states its own staleness rather than inheriting it.
let private withOneProjectGraph (label: string) (body: ProjectGraph * string * string -> 'a) : 'a =
    withTempDir label (fun tmpDir ->
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
        System.IO.File.SetLastWriteTimeUtc(srcPath, now - TimeSpan.FromMinutes 10.0)
        System.IO.File.SetLastWriteTimeUtc(dllPath, now)

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(projPath) |> ignore
        body (graph, srcPath, dllPath))

/// The terminal verdict summary the UI would render; "" while non-terminal.
let private terminalSummary (host: PluginHost) =
    match host.GetStatus("build") with
    | Some(Completed(_, v)) -> v.Summary
    | Some(Failed(_, _, v)) -> v.Summary
    | _ -> ""

[<Fact(Timeout = 15000)>]
let ``a source touched after the build bypasses the cache, though the merkle cannot see it`` () =
    // THE WEDGE, at the key. Before this gate the lookup returned the same key it always
    // had and the stale outputs replayed as a pass.
    withOneProjectGraph "replay-stale" (fun (graph, srcPath, dllPath) ->
        let handler = BuildPlugin.create "true" "" [] graph [] None [] None
        let cacheKeyFn = handler.CacheKey.Value
        let fileEvt = FileChanged(SourceChanged [ srcPath ])
        let storeEvt = Custom(BuildDone(BuildPassed "x", [], TimeSpan.Zero))

        // POSITIVE CONTROL (inline): with the outputs fresh, this very detector serves
        // the cache. Without it the assertion below would also pass against a gate that
        // had simply gone blind and suppressed every key.
        let served = cacheKeyFn fileEvt
        test <@ served.IsSome @>

        // Touch the source WITHOUT changing a byte — a formatter's no-op rewrite.
        let merkleBefore = cacheKeyFn storeEvt
        let dllTime = System.IO.File.GetLastWriteTimeUtc dllPath
        System.IO.File.SetLastWriteTimeUtc(srcPath, dllTime.AddMinutes 1.0)
        let merkleAfter = cacheKeyFn storeEvt

        // The content merkle CANNOT see it — the deadlock's other half, pinned so a
        // future key change cannot make this test vacuous by moving the key instead.
        test <@ merkleBefore = merkleAfter @>

        let wedged = cacheKeyFn fileEvt
        test <@ wedged.IsNone @>)

[<Fact(Timeout = 15000)>]
let ``a checkout with no build output at all bypasses the cache`` () =
    // The first `check` in a brand-new `jj workspace add`: sources byte-identical to the
    // workspace whose entry it therefore hits, and no `bin/` whatsoever. "built 21
    // projects (cached)" was being asserted about outputs that had never existed here.
    withOneProjectGraph "replay-missing" (fun (graph, srcPath, dllPath) ->
        let handler = BuildPlugin.create "true" "" [] graph [] None [] None
        let cacheKeyFn = handler.CacheKey.Value
        let fileEvt = FileChanged(SourceChanged [ srcPath ])

        // POSITIVE CONTROL: served while the DLL is there. Bound outside the quotation —
        // Unquote cannot splice an inner generic call.
        let withDll = cacheKeyFn fileEvt
        test <@ withDll.IsSome @>

        System.IO.File.Delete dllPath
        let withoutDll = cacheKeyFn fileEvt
        test <@ withoutDll.IsNone @>)

[<Fact(Timeout = 15000)>]
let ``stale artifacts suppress the cache LOOKUP only, never the STORE`` () =
    // Same asymmetry `force-rebuild` relies on: suppressing the store too would make
    // every recovered build permanently uncacheable — a correctness fix that pays for
    // itself forever in the inner loop.
    withOneProjectGraph "replay-store" (fun (graph, srcPath, dllPath) ->
        let handler = BuildPlugin.create "true" "" [] graph [] None [] None
        let cacheKeyFn = handler.CacheKey.Value

        let dllTime = System.IO.File.GetLastWriteTimeUtc dllPath
        System.IO.File.SetLastWriteTimeUtc(srcPath, dllTime.AddMinutes 1.0)

        let lookupKey = cacheKeyFn (FileChanged(SourceChanged [ srcPath ]))
        let storeKey = cacheKeyFn (Custom(BuildDone(BuildPassed "x", [], TimeSpan.Zero)))
        test <@ lookupKey.IsNone @>
        test <@ storeKey.IsSome @>)

[<Fact(Timeout = 30000)>]
let ``a warm cache does not replay a pass over stale outputs — the build recovers itself`` () =
    // END TO END, through the framework's real replay path with a real task cache: warm
    // the cache with a passing build, then make the outputs stale WITHOUT moving the
    // merkle. Before the fix this replayed `built … (cached)` forever and only
    // `dotnet fshw stop` appeared to break it. `touch <dll>` stands in for a build that
    // actually re-emits its artifact, so the recovery is observable.
    withOneProjectGraph "replay-e2e" (fun (graph, srcPath, dllPath) ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)
        let handler = BuildPlugin.create "touch" dllPath [] graph [] None [] None
        host.RegisterHandler(handler)

        host.EmitFileChanged(SourceChanged [ srcPath ])
        waitForTerminalStatus host "build" 20000
        let warm = terminalSummary host
        test <@ not (warm.Contains "(cached)") @>

        // POSITIVE CONTROL: nothing changed, so the very next dispatch MUST be served
        // from the cache. This is what proves the "(cached)" detector below can fire —
        // and that the gate has not quietly turned every check into a rebuild.
        host.EmitFileChanged(SourceChanged [ srcPath ])

        let replayed =
            waitUntilTrue (fun () -> (terminalSummary host).Contains "(cached)") 10000

        test <@ replayed @>

        // Now the wedge: touch the source, same bytes, so the merkle is unmoved and the
        // stored entry still matches — but the DLL is older than its source.
        let dllTime = System.IO.File.GetLastWriteTimeUtc dllPath
        System.IO.File.SetLastWriteTimeUtc(srcPath, dllTime.AddMilliseconds 1.0)

        host.EmitFileChanged(SourceChanged [ srcPath ])

        // A REAL build ran: `touch` moved the DLL past its source again.
        let rebuilt =
            waitUntilTrue
                (fun () -> System.IO.File.GetLastWriteTimeUtc dllPath > System.IO.File.GetLastWriteTimeUtc srcPath)
                15000

        test <@ rebuilt @>
        waitForSettled host "build" 15000

        // …and the verdict is a fresh one, not a replayed pass.
        let recovered = terminalSummary host
        test <@ not (recovered.Contains "(cached)") @>)
