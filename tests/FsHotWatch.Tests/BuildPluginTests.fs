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
    let handler = BuildPlugin.create "echo" "build" [] graph [] None [] None None
    test <@ handler.Name = PluginName.create "build" @>

[<Fact(Timeout = 5000)>]
let ``plugin has correct name`` () =
    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None None

    test <@ handler.Name = PluginName.create "build" @>

[<Fact(Timeout = 5000)>]
let ``build-status command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None None

    host.RegisterHandler(handler)

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact(Timeout = 5000)>]
let ``formatSilentFailureDiagnostic includes exit code and output length`` () =
    let output = "Build FAILED.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:02.96"
    let detail = formatSilentFailureDiagnostic 1 output
    test <@ detail.Contains "exit=1" @>
    test <@ detail.Contains $"output={output.Length} bytes" @>
    test <@ detail.Contains "MSBuild aborted" @>

[<Fact(Timeout = 5000)>]
let ``formatSilentFailureDiagnostic includes elapsed time when present in output`` () =
    let output = "Build FAILED.\n    0 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:01:23.45"
    let detail = formatSilentFailureDiagnostic 134 output
    test <@ detail.Contains "elapsed=00:01:23.45" @>

[<Fact(Timeout = 5000)>]
let ``formatSilentFailureDiagnostic omits elapsed when not present`` () =
    let output = "Build FAILED.\n    0 Warning(s)\n    0 Error(s)"
    let detail = formatSilentFailureDiagnostic 1 output
    test <@ not (detail.Contains "elapsed=") @>

[<Fact(Timeout = 5000)>]
let ``build plugin sets MSBUILDDISABLENODEREUSE to prevent worker process accumulation`` () =
    // Regression: when the daemon (a long-running dotnet process) repeatedly
    // spawns `dotnet build`, MSBuild Server's `/nodeReuse:true` workers
    // accumulate as orphan processes — observed 431 stale `MSBuild.dll
    // /nodemode:1 /nodeReuse:true` workers after a few hours of editing.
    // Stale workers serve subsequent builds with bad cwd/state, producing
    // the silent "Build FAILED. 0 errors" output where MSBuild reports zero
    // projects built. Setting MSBUILDDISABLENODEREUSE=1 in the build env
    // prevents reuse and forces fresh workers per invocation.
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    // The shell test exits 0 if the env var is "1", non-zero otherwise.
    let handler =
        BuildPlugin.create
            "sh"
            "-c \"test \\\"$MSBUILDDISABLENODEREUSE\\\" = \\\"1\\\"\""
            []
            (ProjectGraph())
            []
            None
            []
            None
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    match host.GetStatus("build") with
    | Some(Completed _) -> ()
    | other ->
        Assert.Fail(
            $"Expected build Completed (MSBUILDDISABLENODEREUSE=1 should be in env), \
              got: %A{other}"
        )

[<Fact(Timeout = 5000)>]
let ``build plugin emits BuildCompleted on successful build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getBuild, recorder) = buildRecorder ()

    let handler =
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None None

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

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None None
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
        BuildPlugin.create "sleep" "10" [] (ProjectGraph()) [] None [] None (Some 1)

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

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None None
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

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None None
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

    let handler = BuildPlugin.create "false" "" [] (ProjectGraph()) [] None [] None None
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact(Timeout = 10000)>]
let ``build plugin handles exception from runProcess`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        BuildPlugin.create "this-command-does-not-exist-xyz" "" [] (ProjectGraph()) [] None [] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None None

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

    let handler = BuildPlugin.create "false" "" [] graph [ "MyTests" ] None [] None None

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

    let handler = BuildPlugin.create "false" "" [] graph [ "MyTests" ] None [] None None

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
        BuildPlugin.create "false" "should-not-run" [] graph [] (Some "echo {project}") [] None None

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

    let handler =
        BuildPlugin.create "echo" "fallback-build" [] graph [] None [] None None

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
        BuildPlugin.create "echo" "fallback-for-unknown" [] graph [] (Some "false {project}") [] None None

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
        BuildPlugin.create "echo" "fallback ok" [] graph [] (Some "false {project}") [] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup" ] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup" ] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup" ] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [] None None

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
        BuildPlugin.create "echo" "build succeeded" [] (ProjectGraph()) [] None [ "setup"; "codegen" ] None None

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
