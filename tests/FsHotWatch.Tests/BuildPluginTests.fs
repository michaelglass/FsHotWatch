module FsHotWatch.Tests.BuildPluginTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Build
open FsHotWatch.ProjectGraph
open FsHotWatch.Tests.TestHelpers

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
let ``build is skipped when only test files change`` () =
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

    waitForTerminalStatus host "build" 5000

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
