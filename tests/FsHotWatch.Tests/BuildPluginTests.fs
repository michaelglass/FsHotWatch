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
let ``file changes observed during a test host defer the build until that run completes`` () =
    withTempDir "build-during-test-host" (fun tmpDir ->
        let marker = System.IO.Path.Combine(tmpDir, "build-ran")
        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let tests =
            FsHotWatch.TestPrune.TestPrunePlugin.create
                ":memory:"
                tmpDir
                (Some
                    [ { FsHotWatch.TestPrune.TestPrunePlugin.TestConfig.Project = "SlowTests"
                        Command = "sleep"
                        Args = "1"
                        Group = "default"
                        Environment = []
                        FilterTemplate = None
                        ClassJoin = " "
                        TimeoutSec = None
                        ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect } ])
                None
                None
                None
                None
                []

        let build = BuildPlugin.create "touch" marker [] (ProjectGraph()) [] None [] None
        let mutable liveRun: Guid option = None

        let lifecycleRecorder: PluginHandler<unit, unit> =
            { Name = PluginName.create "build-test-lifecycle-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | TestRunStarted started -> liveRun <- Some started.RunId
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.singleton SubscribeTestRunStarted
              CacheKey = None
              Teardown = None }

        host.RegisterHandler(lifecycleRecorder)
        host.RegisterHandler(tests)
        host.RegisterHandler(build)

        let runTask = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        waitUntil (fun () -> liveRun.IsSome) 5000

        host.EmitFileChanged(SourceChanged [ System.IO.Path.Combine(tmpDir, "Source.fs") ])
        System.Threading.Thread.Sleep(250)
        test <@ not (System.IO.File.Exists marker) @>

        runTask.GetAwaiter().GetResult() |> ignore

        waitUntil (fun () -> System.IO.File.Exists marker) 5000)

let private overlappingBuildAndTest (buildDelay: string) (testDelay: string) =
    withTempDir "build-test-overlap" (fun tmpDir ->
        let countFile = System.IO.Path.Combine(tmpDir, "build-count")
        let script = System.IO.Path.Combine(tmpDir, "build.sh")
        System.IO.File.WriteAllText(script, $"printf 'x\\n' >> '{countFile}'\nsleep {buildDelay}\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir

        let tests =
            FsHotWatch.TestPrune.TestPrunePlugin.create
                ":memory:"
                tmpDir
                (Some
                    [ { FsHotWatch.TestPrune.TestPrunePlugin.TestConfig.Project = "SlowTests"
                        Command = "sleep"
                        Args = testDelay
                        Group = "default"
                        Environment = []
                        FilterTemplate = None
                        ClassJoin = " "
                        TimeoutSec = None
                        ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect } ])
                None
                None
                None
                None
                []

        let build = BuildPlugin.create "sh" script [] (ProjectGraph()) [] None [] None
        let mutable liveRun: Guid option = None

        let lifecycleRecorder: PluginHandler<unit, unit> =
            { Name = PluginName.create "overlap-lifecycle-recorder"
              Init = ()
              Update =
                fun _ state event ->
                    async {
                        match event with
                        | TestRunStarted started -> liveRun <- Some started.RunId
                        | _ -> ()

                        return state
                    }
              Commands = []
              Subscriptions = Set.singleton SubscribeTestRunStarted
              CacheKey = None
              Teardown = None }

        host.RegisterHandler(lifecycleRecorder)
        host.RegisterHandler(tests)
        host.RegisterHandler(build)

        host.EmitFileChanged(SourceChanged [ System.IO.Path.Combine(tmpDir, "First.fs") ])

        waitUntil
            (fun () ->
                match host.GetStatus("build") with
                | Some(Running _) -> true
                | _ -> false)
            5000

        let runTask = host.RunCommand("run-tests", [| "{}" |]) |> Async.StartAsTask

        waitUntil (fun () -> liveRun.IsSome) 5000

        host.EmitFileChanged(SourceChanged [ System.IO.Path.Combine(tmpDir, "Deferred.fs") ])
        runTask.GetAwaiter().GetResult() |> ignore

        waitUntil
            (fun () ->
                System.IO.File.Exists countFile
                && System.IO.File.ReadAllLines(countFile).Length = 2)
            8000

        test <@ System.IO.File.ReadAllLines(countFile).Length = 2 @>)

[<Fact(Timeout = 20000)>]
let ``deferred change survives when the overlapping build completes before the test`` () =
    overlappingBuildAndTest "0.2" "1"

[<Fact(Timeout = 20000)>]
let ``deferred change survives when the overlapping test completes before the build`` () =
    overlappingBuildAndTest "1" "0.2"

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

// ---------------------------------------------------------------------------
// AUTOMATION-303 CASE 2 — a cached build may not hide a compile item it never saw.
//
// The incident: a new test file plus its `<Compile Include=…>` was reported as
// `build ok — built 21 projects (cached)` while `[fcs]` reported the new module as
// undefined. The FCS error was REAL. The build had never compiled the change, and
// `test-rerun` "passing" was running against stale DLLs.
//
// AC1 asks for a test per case and case 2 had none: the landed fix declared it already
// closed by the build plugin's project-file merkle. Half of that is true, and the two
// tests below say WHICH half.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303 case 2: a compile item added to a PROJECT FILE moves the build merkle`` () =
    // The half that was already closed. A project file is an input to the merkle, so its
    // content moving moves the key by construction — but "by construction" is a claim
    // about code that can be edited, and this is the case the ticket was opened on.
    withTempDir "a303-case2-fsproj" (fun root ->
        let proj = System.IO.Path.Combine(root, "Thing.fsproj")

        System.IO.File.WriteAllText(proj, "<Project><ItemGroup><Compile Include=\"A.fs\" /></ItemGroup></Project>")

        let source = System.IO.Path.Combine(root, "A.fs")
        System.IO.File.WriteAllText(source, "let a = 1")

        let hasher = BuildInputsHasher(stubGraph [ source ] [ proj ])
        let before = hasher.Compute()

        // THE POSITIVE CONTROL, and it comes first on purpose: prove the merkle is STABLE
        // on an untouched tree before proving it MOVES, or "it changed" is just noise.
        test <@ hasher.Compute() = before @>

        System.IO.File.WriteAllText(
            proj,
            "<Project><ItemGroup><Compile Include=\"A.fs\" /><Compile Include=\"New.fs\" /></ItemGroup></Project>"
        )

        test <@ hasher.Compute() <> before @>)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303 case 2: a compile item added to Directory.Build.props moves the build merkle`` () =
    // The half that was NOT closed, and it is the same defect through a wider door.
    //
    // MSBuild's implicit imports are inputs to every project beneath them, and they are
    // neither compile items (so not in `GetAllFiles`) nor projects (so not in
    // `GetAllProjects`) — so before this change they were in NEITHER list the merkle
    // hashed. A `<Compile Include=…>` added there adds a file to the WHOLE REPO while the
    // key stays byte-identical: `built N projects (cached)`, nothing compiled, and the
    // FCS error beside it is real. That is case 2 exactly.
    //
    // RED before the fix: `after = before`, the two merkles byte-identical.
    withTempDir "a303-case2-props" (fun root ->
        let projDir = System.IO.Path.Combine(root, "src")
        System.IO.Directory.CreateDirectory projDir |> ignore
        let proj = System.IO.Path.Combine(projDir, "Thing.fsproj")
        System.IO.File.WriteAllText(proj, "<Project />")

        let source = System.IO.Path.Combine(projDir, "A.fs")
        System.IO.File.WriteAllText(source, "let a = 1")

        let props = System.IO.Path.Combine(root, "Directory.Build.props")
        System.IO.File.WriteAllText(props, "<Project />")

        let hasher = BuildInputsHasher(stubGraph [ source ] [ proj ])
        let before = hasher.Compute()
        test <@ hasher.Compute() = before @>

        System.IO.File.WriteAllText(
            props,
            "<Project><ItemGroup><Compile Include=\"Generated.fs\" /></ItemGroup></Project>"
        )

        test <@ hasher.Compute() <> before @>)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303 case 2: Directory.Build.targets and Directory.Packages.props move it too`` () =
    // The other two implicit imports, each on its own so a list that grew only one entry
    // cannot pass. `Directory.Build.targets` is the one that matters most here: it is
    // imported AFTER the project body, so it is where a generated-source item most often
    // lives.
    withTempDir "a303-case2-imports" (fun root ->
        let projDir = System.IO.Path.Combine(root, "src")
        System.IO.Directory.CreateDirectory projDir |> ignore
        let proj = System.IO.Path.Combine(projDir, "Thing.fsproj")
        System.IO.File.WriteAllText(proj, "<Project />")

        let hasher = BuildInputsHasher(stubGraph [] [ proj ])

        for name in [ "Directory.Build.targets"; "Directory.Packages.props" ] do
            let before = hasher.Compute()
            let path = System.IO.Path.Combine(root, name)
            System.IO.File.WriteAllText(path, "<Project />")
            let added = hasher.Compute()
            test <@ added <> before @>

            System.IO.File.WriteAllText(path, "<Project><ItemGroup><Compile Include=\"G.fs\" /></ItemGroup></Project>")
            test <@ hasher.Compute() <> added @>)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-303 case 2: an unrelated file beside the implicit imports does NOT move it`` () =
    // THE FLOOR. The three tests above would all pass against a hasher that simply
    // re-read the whole directory tree — and such a hasher would invalidate every build
    // on every keystroke in any file anywhere, which is the failure that gets a cache key
    // reverted. The merkle must move for the STRUCTURE and stay still for everything else.
    withTempDir "a303-case2-floor" (fun root ->
        let projDir = System.IO.Path.Combine(root, "src")
        System.IO.Directory.CreateDirectory projDir |> ignore
        let proj = System.IO.Path.Combine(projDir, "Thing.fsproj")
        System.IO.File.WriteAllText(proj, "<Project />")

        let hasher = BuildInputsHasher(stubGraph [] [ proj ])
        let before = hasher.Compute()

        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "README.md"), "# not an MSBuild import")
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Directory.Build.props.bak"), "<Project />")
        test <@ hasher.Compute() = before @>

        // The control for THAT absence: the same hasher, the same tree, one real implicit
        // import — and it moves. An "it did not move" assertion over a hasher that can
        // never move is worth nothing, which is the whole bug class this ticket is about.
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, "Directory.Build.props"), "<Project />")
        test <@ hasher.Compute() <> before @>)

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

/// Build a single-project fixture and run the plugin against it, registering the project
/// THE WAY THE DAEMON DOES — `RegisterProject` + `RegisterProjectOutput`, from what
/// MSBuild reported — never `RegisterFromFsproj`'s XML parse (AUTOMATION-368: every test
/// of this gate used the parse path, which is why a gate that examined nothing in every
/// live daemon stayed green here for two releases).
///
/// The compile items are what MSBuild really hands over: the authored `Lib.fs` AND the
/// generated `obj/Debug/net10.0/MyLib.AssemblyInfo.fs` that every SDK project compiles.
/// The caller controls all three mtimes as offsets from "now" — the generated one
/// separately, because a design-time evaluation (project discovery) rewrites it without
/// building anything, and treating that as an edit is what made the gate unpromotable.
let private runVerifyHarness
    (label: string)
    (srcOffset: TimeSpan)
    (generatedOffset: TimeSpan)
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
        let objDir = System.IO.Path.Combine(projDir, "obj", "Debug", "net10.0")
        let generatedPath = System.IO.Path.Combine(objDir, "MyLib.AssemblyInfo.fs")
        System.IO.Directory.CreateDirectory(dllDir) |> ignore
        System.IO.Directory.CreateDirectory(objDir) |> ignore

        writeMinimalFsproj projPath "net10.0" [ "Lib.fs" ]
        System.IO.File.WriteAllText(srcPath, "let x = 1")
        System.IO.File.WriteAllText(generatedPath, "// generated by MSBuild")
        System.IO.File.WriteAllText(dllPath, "fake-dll")
        let now = DateTime.UtcNow
        System.IO.File.SetLastWriteTimeUtc(srcPath, now + srcOffset)
        System.IO.File.SetLastWriteTimeUtc(generatedPath, now + generatedOffset)
        System.IO.File.SetLastWriteTimeUtc(dllPath, now + dllOffset)

        let graph = ProjectGraph()

        graph.RegisterProject(
            AbsProjectPath.create projPath,
            [ AbsFilePath.create srcPath; AbsFilePath.create generatedPath ],
            []
        )

        graph.RegisterProjectOutput(AbsProjectPath.create projPath, dllPath)

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
    // RED direction, on the daemon's own registration path. An authored edit at "now"
    // against a DLL from ten minutes ago is the stale artifact the gate exists for —
    // the shape observed live on 2026-08-20, where two test runs then passed against
    // pre-edit bytes.
    let getBuild =
        runVerifyHarness
            "build-verify-stale-demotion"
            TimeSpan.Zero
            (TimeSpan.FromMinutes(-10.0))
            (TimeSpan.FromMinutes(-10.0))

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``BuildPlugin emits BuildSucceeded when canonical DLL is newer than sources`` () =
    // GREEN direction. Without this the red one above is also satisfied by a gate that
    // condemns every build — which would not be a stale-artifact detector, it would be
    // caching turned off.
    let getBuild =
        runVerifyHarness "build-verify-fresh" (TimeSpan.FromMinutes(-5.0)) (TimeSpan.FromMinutes(-5.0)) TimeSpan.Zero

    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
let ``a discovery-regenerated obj compile item does not redden a fresh build`` () =
    // THE reason AUTOMATION-368's gate could not be promoted, as a test.
    //
    // `obj/<cfg>/<tfm>/<Project>.AssemblyInfo.fs` is a compile item of every SDK
    // project, and every design-time MSBuild evaluation rewrites it. Project DISCOVERY
    // is a design-time evaluation, so each discovery pass stamped it AFTER the DLL the
    // last build had just produced. Nothing was edited; nothing was out of date; every
    // project in the graph read as stale.
    //
    // Measured across ~40 workspaces of the consuming repo over the report-only window:
    // 2090 stale findings, 91% of them within 90s of an `MSBuild evaluation` line in
    // the same daemon log.
    let getBuild =
        runVerifyHarness "build-verify-generated" (TimeSpan.FromMinutes(-5.0)) (TimeSpan.FromMinutes 1.0) TimeSpan.Zero

    test <@ getBuild () = Some BuildSucceeded @>

[<Fact(Timeout = 15000)>]
let ``an authored edit still reddens even when the generated item is older`` () =
    // The mutation the other way, so the exclusion above cannot have been implemented
    // by ignoring the source list. Same fixture, same generated item — only the
    // AUTHORED file moves, and the gate must still fire.
    let getBuild =
        runVerifyHarness
            "build-verify-authored-edit"
            TimeSpan.Zero
            (TimeSpan.FromMinutes(-30.0))
            (TimeSpan.FromMinutes(-10.0))

    test
        <@
            match getBuild () with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

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
        graph.RegisterProject(AbsProjectPath.create projPath, [ AbsFilePath.create srcPath ], [])
        graph.RegisterProjectOutput(AbsProjectPath.create projPath, dllPath)

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

/// A one-project graph on disk: `MyLib.fsproj` + `Lib.fs` + the generated
/// `obj/Debug/net10.0/MyLib.AssemblyInfo.fs` every SDK project compiles + a fake DLL.
/// The DLL starts NEWER than both (i.e. fresh), so each test states its own staleness
/// rather than inheriting it.
///
/// Registered THE WAY THE DAEMON DOES — `RegisterProject` + `RegisterProjectOutput`,
/// from what MSBuild reported. AUTOMATION-368: while these fixtures used
/// `RegisterFromFsproj`'s XML parse, they proved the gate worked on a path no live
/// daemon takes, and it shipped twice having examined nothing at all.
let private withOneProjectGraph (label: string) (body: ProjectGraph * string * string -> 'a) : 'a =
    withTempDir label (fun tmpDir ->
        let projDir = System.IO.Path.Combine(tmpDir, "MyLib")
        let projPath = System.IO.Path.Combine(projDir, "MyLib.fsproj")
        let srcPath = System.IO.Path.Combine(projDir, "Lib.fs")
        let dllDir = System.IO.Path.Combine(projDir, "bin", "Debug", "net10.0")
        let dllPath = System.IO.Path.Combine(dllDir, "MyLib.dll")
        let objDir = System.IO.Path.Combine(projDir, "obj", "Debug", "net10.0")
        let generatedPath = System.IO.Path.Combine(objDir, "MyLib.AssemblyInfo.fs")
        System.IO.Directory.CreateDirectory(dllDir) |> ignore
        System.IO.Directory.CreateDirectory(objDir) |> ignore

        writeMinimalFsproj projPath "net10.0" [ "Lib.fs" ]
        System.IO.File.WriteAllText(srcPath, "let x = 1")
        System.IO.File.WriteAllText(generatedPath, "// generated by MSBuild")
        System.IO.File.WriteAllText(dllPath, "fake-dll")

        let now = DateTime.UtcNow
        System.IO.File.SetLastWriteTimeUtc(srcPath, now - TimeSpan.FromMinutes 10.0)
        System.IO.File.SetLastWriteTimeUtc(generatedPath, now - TimeSpan.FromMinutes 10.0)
        System.IO.File.SetLastWriteTimeUtc(dllPath, now)

        let graph = ProjectGraph()

        graph.RegisterProject(
            AbsProjectPath.create projPath,
            [ AbsFilePath.create srcPath; AbsFilePath.create generatedPath ],
            []
        )

        graph.RegisterProjectOutput(AbsProjectPath.create projPath, dllPath)
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

// ---------------------------------------------------------------------------
// AUTOMATION-245 (production reachability) — every test above builds the plugin with
// `create`, i.e. `artifactGateReddens = true`. NO LIVE DAEMON DOES: `DaemonConfig`
// passes `false` so AUTOMATION-368 can hold the mtime reading back until it has been
// watched on a real tree. In that mode the arbiter returned `[]` unconditionally, so
// the whole replay gate was dead exactly where it was supposed to be running — proved
// by 820 `artifact-gate (report-only)` lines in one consuming repo's daemon log, every
// one of them discarded, including runs whose canonical DLLs were simply ABSENT.
//
// The split these tests pin: refusing a replay cannot redden anything (it returns
// `None` from the cache key — one real build, whose own result decides the colour), so
// the flag governs only the reading that can be WRONG. `DllOlderThanSources` is an
// mtime comparison and stays behind it. `DllMissing` is not a reading; the file is not
// there, and no stored "built N projects" can be true about an output that does not
// exist.
//
// Each absence assertion is paired with a positive control on the SAME detector, and
// the mode tests are paired with each other: a gate that had simply gone blind (or one
// that suppressed every key) fails one half of every pair.
// ---------------------------------------------------------------------------

/// The production wiring: report-only, exactly as `DaemonConfig` constructs it.
let private reportOnlyPlugin graph =
    BuildPlugin.createWith false "true" "" [] graph [] None [] None

[<Fact(Timeout = 15000)>]
let ``report-only: a missing build output still bypasses the cache`` () =
    // THE PRODUCTION WEDGE. First `check` in a brand-new `jj workspace add`: sources
    // byte-identical to the workspace whose entry it hits, no `bin/` at all.
    withOneProjectGraph "replay-reportonly-missing" (fun (graph, srcPath, dllPath) ->
        let cacheKeyFn = (reportOnlyPlugin graph).CacheKey.Value
        let fileEvt = FileChanged(SourceChanged [ srcPath ])

        // POSITIVE CONTROL: served while the DLL is there, in this same mode. Without
        // it the assertion below would also pass against a gate that suppressed
        // everything and turned report-only into rebuild-every-time.
        let withDll = cacheKeyFn fileEvt
        test <@ withDll.IsSome @>

        System.IO.File.Delete dllPath
        let withoutDll = cacheKeyFn fileEvt
        test <@ withoutDll.IsNone @>)

[<Fact(Timeout = 15000)>]
let ``report-only: an mtime-stale output still replays, as AUTOMATION-368 intends`` () =
    // The other half of the split, and the reason this is not just "flip the flag".
    // The mtime reading is the one the report-only window caught being wrong (2090
    // findings, 91% of them a design-time evaluation restamping AssemblyInfo.fs), so
    // promoting it belongs to 368 and not here.
    withOneProjectGraph "replay-reportonly-mtime" (fun (graph, srcPath, dllPath) ->
        let fileEvt = FileChanged(SourceChanged [ srcPath ])
        let dllTime = System.IO.File.GetLastWriteTimeUtc dllPath
        System.IO.File.SetLastWriteTimeUtc(srcPath, dllTime.AddMinutes 1.0)

        // POSITIVE CONTROL on the same tree: the reddening plugin DOES bypass here, so
        // the tree really is mtime-stale and this test cannot pass by staging nothing.
        let reddening =
            ((BuildPlugin.create "true" "" [] graph [] None [] None).CacheKey.Value) fileEvt

        test <@ reddening.IsNone @>

        let reportOnly = ((reportOnlyPlugin graph).CacheKey.Value) fileEvt
        test <@ reportOnly.IsSome @>)

[<Fact(Timeout = 30000)>]
let ``a build that does not produce an output stops it justifying a bypass`` () =
    // THE FLOOR, without which the arm above has no termination argument: a project the
    // build command never builds is missing before the build and missing after it, so
    // every lookup would bypass and the repo would rebuild on every `check`.
    //
    // `true` as the build command is exactly that situation — it succeeds and writes
    // nothing. So the bypass is worth ONE build, and then the cache is served again.
    withOneProjectGraph "replay-unproduced" (fun (graph, srcPath, dllPath) ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)
        let handler = BuildPlugin.createWith false "true" "" [] graph [] None [] None
        host.RegisterHandler(handler)

        System.IO.File.Delete dllPath

        // Warm the cache with a build that passes and produces nothing.
        host.EmitFileChanged(SourceChanged [ srcPath ])
        waitForTerminalStatus host "build" 20000
        let first = terminalSummary host
        test <@ not (first.Contains "(cached)") @>

        // The output is STILL absent — but the build just demonstrated it will never
        // produce it, so the cache is served rather than rebuilt forever.
        host.EmitFileChanged(SourceChanged [ srcPath ])

        let replayed =
            waitUntilTrue (fun () -> (terminalSummary host).Contains "(cached)") 15000

        test <@ replayed @>
        test <@ not (System.IO.File.Exists dllPath) @>)

// ---------------------------------------------------------------------------
// AUTOMATION-245 (QA rework) — the gate has to cover every event that READS the
// cache, and it has to say so when it examined nothing.
//
// Two holes the landed work left, both of the same shape: a guard that is present,
// correct, and simply not consulted.
//
//   * It matched `FileChanged` alone. A plugin configured with `dependsOn` BUFFERS
//     file changes until its dependencies report, and the build is started by the
//     `CommandCompleted` that satisfies the last one — an event that fell through to
//     an ungated key. For exactly those repos, both `force-rebuild` (AUTOMATION-224)
//     and the artifact re-verification were bypassed on the only lookup that mattered.
//
//   * `verifyArtifactsFresh` returns the projects it found stale, and "nothing is
//     stale" is the same value as "nothing could be examined". The path is derived
//     from each project's OWN `<TargetFramework>`, so a repo that centralises that in
//     a `Directory.Build.props` verifies not one artifact — silently, on both the
//     post-build path and at replay.
// ---------------------------------------------------------------------------

/// The event a `dependsOn` plugin actually starts its build from.
let private depSatisfied (name: string) =
    CommandCompleted
        { Name = name
          Outcome = CommandSucceeded "ok" }

[<Fact(Timeout = 15000)>]
let ``a dependency-gated lookup re-verifies the artifacts too, not just a FileChanged`` () =
    withOneProjectGraph "replay-dep-stale" (fun (graph, srcPath, dllPath) ->
        // `dependsOn` is what moves the build-starting event from `FileChanged` to
        // `CommandCompleted` — the configuration under which the gate was blind.
        let handler = BuildPlugin.create "true" "" [] graph [] None [ "fmt" ] None
        let cacheKeyFn = handler.CacheKey.Value
        let depEvt = depSatisfied "fmt"

        // POSITIVE CONTROL: with the outputs fresh this very event IS served from the
        // cache, so the absence assertion below cannot be satisfied by a gate that had
        // simply gone blind and suppressed every key.
        let served = cacheKeyFn depEvt
        test <@ served.IsSome @>

        // The wedge, at the event that starts the build: source touched to a newer
        // mtime with its bytes unchanged, so the merkle is unmoved.
        let dllTime = System.IO.File.GetLastWriteTimeUtc dllPath
        System.IO.File.SetLastWriteTimeUtc(srcPath, dllTime.AddMinutes 1.0)

        let wedged = cacheKeyFn depEvt
        test <@ wedged.IsNone @>)

[<Fact(Timeout = 15000)>]
let ``force-rebuild reaches a dependency-gated lookup`` () =
    // AUTOMATION-224's escape hatch had the same hole: `confirm` sets the flag, and in a
    // `dependsOn` repo the lookup that decides whether a build runs never read it.
    withOneProjectGraph "replay-dep-force" (fun (graph, _, _) ->
        let handler = BuildPlugin.create "true" "" [] graph [] None [ "fmt" ] None
        let cacheKeyFn = handler.CacheKey.Value
        let depEvt = depSatisfied "fmt"

        let before = cacheKeyFn depEvt
        test <@ before.IsSome @>

        handler.Commands
        |> List.find (fun (name, _) -> name = "force-rebuild")
        |> snd
        |> fun run -> run Unchecked.defaultof<_> Unchecked.defaultof<_> [||] |> Async.RunSynchronously
        |> ignore

        let after = cacheKeyFn depEvt
        test <@ after.IsNone @>)

[<Fact(Timeout = 15000)>]
let ``a dependency-gated build still stores its result`` () =
    // The asymmetry has to survive the wider gate: only READS are suppressed. If the
    // `Custom BuildDone` store were caught by it, a `dependsOn` repo would rebuild from
    // scratch forever — a correctness fix paying for itself every single run.
    withOneProjectGraph "replay-dep-store" (fun (graph, srcPath, dllPath) ->
        let handler = BuildPlugin.create "true" "" [] graph [] None [ "fmt" ] None
        let cacheKeyFn = handler.CacheKey.Value

        let dllTime = System.IO.File.GetLastWriteTimeUtc dllPath
        System.IO.File.SetLastWriteTimeUtc(srcPath, dllTime.AddMinutes 1.0)

        let lookupKey = cacheKeyFn (depSatisfied "fmt")
        let storeKey = cacheKeyFn (Custom(BuildDone(BuildPassed "x", [], TimeSpan.Zero)))
        test <@ lookupKey.IsNone @>
        test <@ storeKey.IsSome @>)

// --- THE FLOOR: what the freshness pass could not examine ---

/// A project file with NO `<TargetFramework>` — the shape every project in a repo that
/// centralises the property in a `Directory.Build.props` has on disk.
let private writeTfmlessFsproj (projPath: string) (compiles: string list) =
    let items =
        compiles
        |> List.map (fun c -> $"    <Compile Include=\"%s{c}\" />")
        |> String.concat "\n"

    System.IO.File.WriteAllText(projPath, "<Project>\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>")

[<Fact(Timeout = 15000)>]
let ``a fully examined graph has no coverage gap to report`` () =
    // POSITIVE CONTROL for every assertion below: this is the tree the gate is supposed
    // to be silent about, and a floor that reported on it would be noise, not a floor.
    withOneProjectGraph "coverage-clean" (fun (graph, _, _) -> test <@ (artifactCoverageGap graph).IsNone @>)

[<Fact(Timeout = 15000)>]
let ``a missing DLL is a finding, not a coverage gap`` () =
    // The distinction the floor turns on: a derivable path whose DLL is absent WAS
    // examined — `DllMissing` is the finding. Reporting it as unexamined would drown the
    // one case that means the guard is off.
    withOneProjectGraph "coverage-missing-dll" (fun (graph, _, dllPath) ->
        System.IO.File.Delete dllPath
        test <@ (artifactCoverageGap graph).IsNone @>)

[<Fact(Timeout = 15000)>]
let ``a project with no TargetFramework of its own is named as unverified`` () =
    // Nothing about this project is checked, on either path, and the stale list it
    // contributes to is empty — which is indistinguishable from a clean tree.
    withTempDir "coverage-no-tfm" (fun tmpDir ->
        let projDir = System.IO.Path.Combine(tmpDir, "Central")
        System.IO.Directory.CreateDirectory(projDir) |> ignore
        let projPath = System.IO.Path.Combine(projDir, "Central.fsproj")
        let srcPath = System.IO.Path.Combine(projDir, "Lib.fs")
        writeTfmlessFsproj projPath [ "Lib.fs" ]
        System.IO.File.WriteAllText(srcPath, "let x = 1")

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(projPath) |> ignore

        let gap = artifactCoverageGap graph
        test <@ gap.IsSome @>
        let text = gap.Value
        test <@ text.Contains "Central" @>
        test <@ text.Contains "examined 0 of 1" @>
        test <@ text.Contains "TargetFramework" @>)

[<Fact(Timeout = 15000)>]
let ``an output with no source to compare against is named as half-checked`` () =
    // Its existence was verified; its currency never was. Silently counting it as fresh
    // is how a mtime guard becomes a file-exists guard without anyone deciding to.
    withOneProjectGraph "coverage-no-source" (fun (graph, srcPath, _) ->
        // POSITIVE CONTROL first: with the source present this graph is clean.
        test <@ (artifactCoverageGap graph).IsNone @>

        System.IO.File.Delete srcPath

        let gap = artifactCoverageGap graph
        test <@ gap.IsSome @>
        let text = gap.Value
        test <@ text.Contains "MyLib" @>
        test <@ text.Contains "only its existence was checked" @>)

[<Fact(Timeout = 15000)>]
let ``a tree the gate cannot examine is reported, not refused`` () =
    // The floor REPORTS. Bypassing the cache on every lookup whose artifacts could not be
    // examined would wedge every repo that centralises its TargetFramework into a
    // rebuild-every-time loop — trading one wedge class for the regression this ticket's
    // own acceptance forbids. Pinned as a decision so a later "make it stricter" pass has
    // to argue with a test rather than discover the cost in production.
    withTempDir "coverage-serves" (fun tmpDir ->
        let projDir = System.IO.Path.Combine(tmpDir, "Central")
        System.IO.Directory.CreateDirectory(projDir) |> ignore
        let projPath = System.IO.Path.Combine(projDir, "Central.fsproj")
        let srcPath = System.IO.Path.Combine(projDir, "Lib.fs")
        writeTfmlessFsproj projPath [ "Lib.fs" ]
        System.IO.File.WriteAllText(srcPath, "let x = 1")

        let graph = ProjectGraph()
        graph.RegisterFromFsproj(projPath) |> ignore

        // This tree really is unexaminable — otherwise the assertion below would be
        // agreeing with an ordinary healthy graph and proving nothing.
        test <@ (artifactCoverageGap graph).IsSome @>

        let handler = BuildPlugin.create "true" "" [] graph [] None [] None
        let served = handler.CacheKey.Value(FileChanged(SourceChanged [ srcPath ]))
        test <@ served.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``a graph the daemon's own registration path produced is reported as unexamined`` () =
    // The registrar, not the repo. `GetCanonicalDllPath` needs a TargetFramework, and the
    // ONLY thing that puts one in the graph is `RegisterFromFsproj`'s XML parse — which no
    // production code calls. The daemon registers every project through `RegisterProject`
    // (Daemon.fs, from Ionide's MSBuild evaluation), which records source files and
    // references and no framework, so a live daemon's graph answers `None` for every
    // project and this whole freshness apparatus examines nothing at all.
    //
    // The floor cannot fix that — locating a real build output is a change to what the
    // graph is TOLD, not to what it is asked — but it is the difference between a guard
    // that is off and a guard that is off SILENTLY.
    withTempDir "coverage-registerproject" (fun tmpDir ->
        let projPath = System.IO.Path.Combine(tmpDir, "Daemonish.fsproj")
        let srcPath = System.IO.Path.Combine(tmpDir, "Lib.fs")
        System.IO.File.WriteAllText(srcPath, "let x = 1")

        let graph = ProjectGraph()

        graph.RegisterProject(AbsProjectPath.create projPath, [ AbsFilePath.create srcPath ], [])

        let gap = artifactCoverageGap graph
        test <@ gap.IsSome @>
        test <@ gap.Value.Contains "examined 0 of 1" @>
        test <@ gap.Value.Contains "Daemonish" @>)

[<Fact(Timeout = 15000)>]
let ``an empty project graph is reported, not read as a clean tree`` () =
    // The quietest degradation of the lot: with no projects the stale list is empty AND
    // the merkle hashes an empty input, so every such repo shares one constant cache key
    // while looking perfectly healthy. Found by making exactly this mistake in the path
    // filter of this ticket's own measurement — the count was the only thing that showed.
    let gap = artifactCoverageGap (ProjectGraph())
    test <@ gap.IsSome @>
    test <@ gap.Value.Contains "no projects at all" @>

// ---------------------------------------------------------------------------
// AUTOMATION-343 — BUILD's own cold-vs-cached ledger parity
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``AUTOMATION-343: a cached build replay leaves an out-of-batch finding standing`` () =
    // The seam regression in PluginFrameworkTests proves the framework replays only
    // what the run captured. It cannot prove what THIS plugin captures, and that is
    // the half a plugin-local clear/report divergence would escape through.
    //
    // BuildPlugin reports and clears exactly one pseudo-file, `<build>`, and never
    // calls ClearAllErrors — so nothing it does is licensed to touch a finding about
    // any other file. Under the deleted blanket `ClearPlugin` the replay wiped the
    // whole `build` slice anyway, and this test is red.
    withOneProjectGraph "a343-build-parity" (fun (graph, srcPath, _dllPath) ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)
        host.RegisterHandler(BuildPlugin.create "true" "" [] graph [] None [] None)

        // A finding carried from an earlier batch, about a file this build never
        // mentions — the one the blanket destroyed.
        seedOutOfBatch host "build"

        // COLD. A real build runs and mints the cache entry.
        host.EmitFileChanged(SourceChanged [ srcPath ])
        waitForTerminalStatus host "build" 20000
        let coldSummary = terminalSummary host
        test <@ not (coldSummary.Contains "(cached)") @>
        let cold = ledgerSlice host "build"

        // The real run keeps it — the baseline the replay has to reproduce.
        test <@ ledgerHasOutOfBatch host "build" @>

        // WARM. Nothing moved, so this dispatch MUST be served from the cache;
        // asserting the marker is what stops the test passing on a second real build.
        host.EmitFileChanged(SourceChanged [ srcPath ])
        let replayed = waitForCachedReplay host "build" 20000
        test <@ replayed @>

        let cached = ledgerSlice host "build"

        // The sentinel survived the replay ...
        test <@ ledgerHasOutOfBatch host "build" @>
        // ... and so did everything else. Equality, not "both non-empty": the batch's
        // own `<build>` entry replayed correctly throughout the bug's life.
        test <@ cached = cold @>)

// ---------------------------------------------------------------------------
// AUTOMATION-245 (QA rework) — the COPY, which is what the wedge is actually made of.
//
// Everything above asks about a project's OWN assembly: is it there, is it older than
// its sources. The refusals that blocked real merges named something else — a
// dependency assembly that had not been copied into a test project's output directory.
// That predicate lived only in `FsHotWatch.TestPrune`, which this plugin cannot see
// (siblings over core), and the ticket named exactly that as the reason its acceptance
// could not be met. The rule now lives in core's `OutputCopyFreshness`, so the plugin
// that owns the build cache can ask it.
//
// What it asks is MSBuild's own incremental-copy predicate — same size AND same mtime
// — and not the byte comparison, deliberately. MEASURED on a real two-project build:
// a copy left behind by a refreshed producer is re-emitted by a plain `dotnet build`
// and comes back with size and mtime equal again, while a copy that differs in BYTES
// at equal size and mtime is skipped by that same build and left exactly as it was.
// Gating a cache on the second class would buy a rebuild that provably cannot fix it,
// on every lookup, for ever.
//
// Every absence assertion below is paired with a positive control on the same detector
// over the same tree.
// ---------------------------------------------------------------------------

/// Producer `Lib` ← consumer `App`, each with its own output dir, registered THE WAY
/// THE DAEMON DOES. `settled` chooses what a build left behind: the copy carrying the
/// origin's bytes/size/mtime (what a real `dotnet build` produces, measured), or the
/// copy left behind while the producer's output was refreshed — the merge-flip shape.
///
/// Both projects' DLLs are newer than their sources, so no test here inherits a
/// staleness the mtime gate would have caught anyway.
let private withProducerConsumerGraph
    (label: string)
    (settled: bool)
    (body: ProjectGraph * string * string -> 'a)
    : 'a =
    withTempDir label (fun tmpDir ->
        let now = DateTime.UtcNow

        let mk name =
            let dir = System.IO.Path.Combine(tmpDir, name)
            let outDir = System.IO.Path.Combine(dir, "bin", "Debug", "net10.0")
            System.IO.Directory.CreateDirectory(outDir) |> ignore
            let proj = System.IO.Path.Combine(dir, name + ".fsproj")
            let src = System.IO.Path.Combine(dir, "Lib.fs")
            let dll = System.IO.Path.Combine(outDir, name + ".dll")
            writeMinimalFsproj proj "net10.0" [ "Lib.fs" ]
            System.IO.File.WriteAllText(src, "let x = 1")
            System.IO.File.WriteAllText(dll, name + "-bytes-v2")
            System.IO.File.SetLastWriteTimeUtc(src, now - TimeSpan.FromMinutes 10.0)
            System.IO.File.SetLastWriteTimeUtc(dll, now)
            proj, src, dll

        let libProj, libSrc, libDll = mk "Lib"
        let appProj, appSrc, appDll = mk "App"

        let copy = System.IO.Path.Combine(System.IO.Path.GetDirectoryName appDll, "Lib.dll")

        if settled then
            System.IO.File.WriteAllText(copy, System.IO.File.ReadAllText libDll)
            System.IO.File.SetLastWriteTimeUtc(copy, System.IO.File.GetLastWriteTimeUtc libDll)
        else
            // Same length, so nothing below can be passing on a size difference alone.
            System.IO.File.WriteAllText(copy, "Lib-bytes-v1")
            System.IO.File.SetLastWriteTimeUtc(copy, now - TimeSpan.FromMinutes 5.0)

        let graph = ProjectGraph()
        graph.RegisterProject(AbsProjectPath.create libProj, [ AbsFilePath.create libSrc ], [])

        graph.RegisterProject(
            AbsProjectPath.create appProj,
            [ AbsFilePath.create appSrc ],
            [ AbsProjectPath.create libProj ]
        )

        graph.RegisterProjectOutput(AbsProjectPath.create libProj, libDll)
        graph.RegisterProjectOutput(AbsProjectPath.create appProj, appDll)
        body (graph, libSrc, copy))

[<Fact(Timeout = 15000)>]
let ``a dependency copy the build still owes bypasses the cache`` () =
    // THE MERGE-FLIP WEDGE, at the key. The producer's output was refreshed and the
    // consumer's copy of it was not — and because the merkle is over SOURCES, the
    // stored entry replayed `built N projects (cached)` without running, so nothing
    // ever made the copy. Both sides correct, neither moving.
    withProducerConsumerGraph "replay-copy-pending" false (fun (graph, srcPath, _copy) ->
        let cacheKeyFn =
            (BuildPlugin.create "true" "" [] graph [] None [] None).CacheKey.Value

        let wedged = cacheKeyFn (FileChanged(SourceChanged [ srcPath ]))
        test <@ wedged.IsNone @>)

[<Fact(Timeout = 15000)>]
let ``a settled dependency copy still serves the cache`` () =
    // THE NEGATIVE CONTROL for the test above, on the same fixture with the same
    // detector: a gate that suppressed every key would satisfy the absence half while
    // turning every check in every repo into a full rebuild.
    withProducerConsumerGraph "replay-copy-settled" true (fun (graph, srcPath, _copy) ->
        let cacheKeyFn =
            (BuildPlugin.create "true" "" [] graph [] None [] None).CacheKey.Value

        let served = cacheKeyFn (FileChanged(SourceChanged [ srcPath ]))
        test <@ served.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``report-only: a dependency copy the build still owes bypasses the cache`` () =
    // PRODUCTION WIRING. `DaemonConfig` constructs this plugin with
    // `artifactGateReddens = false`, and the last time a gate shipped here it was inert
    // in exactly that mode for two releases. Refusing a replay reddens nothing — it
    // returns `None` from the cache key, the same bypass `force-rebuild` uses — so the
    // flag that holds back the mtime READING has no jurisdiction over MSBuild's own
    // copy predicate.
    withProducerConsumerGraph "replay-copy-reportonly" false (fun (graph, srcPath, _copy) ->
        let cacheKeyFn = (reportOnlyPlugin graph).CacheKey.Value
        let wedged = cacheKeyFn (FileChanged(SourceChanged [ srcPath ]))
        test <@ wedged.IsNone @>)

[<Fact(Timeout = 15000)>]
let ``report-only: a settled dependency copy still serves the cache`` () =
    // The negative control for the production wiring specifically. Without it,
    // report-only mode could have become rebuild-every-time and every test above would
    // still pass.
    withProducerConsumerGraph "replay-copy-reportonly-ok" true (fun (graph, srcPath, _copy) ->
        let cacheKeyFn = (reportOnlyPlugin graph).CacheKey.Value
        let served = cacheKeyFn (FileChanged(SourceChanged [ srcPath ]))
        test <@ served.IsSome @>)

[<Fact(Timeout = 15000)>]
let ``a pending dependency copy suppresses the cache LOOKUP only, never the STORE`` () =
    // Same asymmetry `force-rebuild` and the missing-output arm rely on: suppressing
    // the store too would make every recovered build permanently uncacheable.
    withProducerConsumerGraph "replay-copy-store" false (fun (graph, srcPath, _copy) ->
        let cacheKeyFn =
            (BuildPlugin.create "true" "" [] graph [] None [] None).CacheKey.Value

        let lookupKey = cacheKeyFn (FileChanged(SourceChanged [ srcPath ]))
        let storeKey = cacheKeyFn (Custom(BuildDone(BuildPassed "x", [], TimeSpan.Zero)))
        test <@ lookupKey.IsNone @>
        test <@ storeKey.IsSome @>)

[<Fact(Timeout = 30000)>]
let ``a build that cannot settle a copy stops it justifying a bypass`` () =
    // THE FLOOR, and the reason this gate is not a rebuild-every-time regression.
    //
    // A copy that differs in bytes at equal size and mtime is one MSBuild skips for
    // ever (measured); so is one whose producer the build command does not build. Both
    // reach this plugin as "still pending after a build that passed", and without a
    // floor each would bypass the cache on every single lookup while the rebuild it
    // paid for changed nothing. `true` as the build command is that situation exactly:
    // it succeeds and touches nothing.
    withProducerConsumerGraph "replay-copy-floor" false (fun (graph, srcPath, copy) ->
        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)
        host.RegisterHandler(BuildPlugin.createWith false "true" "" [] graph [] None [] None)

        // Warm the cache with a real build that passes and settles nothing.
        host.EmitFileChanged(SourceChanged [ srcPath ])
        waitForTerminalStatus host "build" 20000
        let first = terminalSummary host
        test <@ not (first.Contains "(cached)") @>

        // The copy is STILL pending — but the build just demonstrated it will not settle
        // it, so the cache is served rather than rebuilt for ever.
        host.EmitFileChanged(SourceChanged [ srcPath ])

        let replayed =
            waitUntilTrue (fun () -> (terminalSummary host).Contains "(cached)") 15000

        test <@ replayed @>
        test <@ System.IO.File.ReadAllText copy = "Lib-bytes-v1" @>)

[<Fact(Timeout = 30000)>]
let ``a build that settles the copy restores an ordinary cached replay`` () =
    // The floor's other direction, and what makes the wedge RECOVER rather than merely
    // be reported: when the forced build does re-emit the copy (measured — a plain
    // `dotnet build` of the consumer re-copies a left-behind dependency and leaves size
    // and mtime equal again), the very next dispatch is an ordinary cache hit.
    //
    // `cp` stands in for that copy step, so the recovery is observable without a real
    // MSBuild in a unit test.
    withProducerConsumerGraph "replay-copy-recovers" false (fun (graph, srcPath, copy) ->
        let origin =
            graph.GetAllProjects()
            |> List.pick (fun p ->
                if System.IO.Path.GetFileName(AbsProjectPath.value p) = "Lib.fsproj" then
                    graph.GetCanonicalDllPath p
                else
                    None)

        let cache =
            FsHotWatch.TaskCache.InMemoryTaskCache() :> FsHotWatch.TaskCache.ITaskCache

        let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)
        // `-p` preserves the timestamp, which is what MSBuild's own copy does and what
        // makes the pair settled afterwards.
        host.RegisterHandler(BuildPlugin.createWith false "cp" $"-p %s{origin} %s{copy}" [] graph [] None [] None)

        host.EmitFileChanged(SourceChanged [ srcPath ])
        waitForTerminalStatus host "build" 20000

        let settled =
            waitUntilTrue (fun () -> System.IO.File.ReadAllText copy = System.IO.File.ReadAllText origin) 15000

        test <@ settled @>

        host.EmitFileChanged(SourceChanged [ srcPath ])

        let replayed =
            waitUntilTrue (fun () -> (terminalSummary host).Contains "(cached)") 15000

        test <@ replayed @>)
