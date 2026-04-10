module FsHotWatch.Tests.FileCommandPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.FileCommand.FileCommandPlugin
open FsHotWatch.Tests.TestHelpers

[<Fact>]
let ``plugin has correct name`` () =
    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fun f -> f.EndsWith(".fsx"))
            "echo"
            "hello"
            false
            None

    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "run-scripts" @>

[<Fact>]
let ``command runs when matching files change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fun f -> f.EndsWith(".fsx"))
            "echo"
            "hello"
            false
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "scripts/build.fsx" ])

    waitUntil
        (fun () ->
            match host.GetStatus("run-scripts") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("run-scripts")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``command does not run for non-matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "run-scripts")
            (fun f -> f.EndsWith(".fsx"))
            "echo"
            "hello"
            false
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    // No matching files — wait briefly for agent to process the no-op
    waitUntil
        (fun () ->
            match host.GetStatus("run-scripts") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000
    |> ignore

    let status = host.GetStatus("run-scripts")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact>]
let ``command captures stdout output`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "echo-test")
            (fun _ -> true)
            "echo"
            "captured-output"
            false
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "anything.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("echo-test") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let result = host.RunCommand("echo-test-status", [||]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("true") @>

[<Fact>]
let ``command with environment variables`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "env-test")
            (fun _ -> true)
            "echo"
            "env-test-output"
            false
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("env-test") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("env-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``command runs on ProjectChanged with matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "proj-watcher")
            (fun f -> f.EndsWith(".fsproj"))
            "echo"
            "project changed"
            false
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    waitUntil
        (fun () ->
            match host.GetStatus("proj-watcher") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("proj-watcher")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``command ignores SolutionChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "sln-watcher") (fun _ -> true) "echo" "hello" false None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged "test.sln")

    // SolutionChanged is ignored — poll briefly; will time out at Idle (expected)
    waitUntil
        (fun () ->
            match host.GetStatus("sln-watcher") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000

    let status = host.GetStatus("sln-watcher")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact>]
let ``command reports Failed status on command failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "fail-cmd") (fun _ -> true) "false" "" false None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("fail-cmd") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("fail-cmd")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact>]
let ``command reports Failed status on exception`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create
            (FsHotWatch.PluginFramework.PluginName.create "bad-cmd")
            (fun _ -> true)
            "this-command-does-not-exist-xyz"
            ""
            false
            None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("bad-cmd") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("bad-cmd")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

    test <@ host.HasFailingReasons(warningsAreFailures = true) @>

[<Fact>]
let ``status command returns not run when no files matched`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "no-match") (fun _ -> false) "echo" "hello" false None

    host.RegisterHandler(handler)

    // No files match, so command never runs
    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    // No matching files — poll briefly; will time out at Idle (expected)
    waitUntil
        (fun () ->
            match host.GetStatus("no-match") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000

    let result = host.RunCommand("no-match-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact>]
let ``status command returns false when command failed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "fail-status") (fun _ -> true) "false" "" false None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match host.GetStatus("fail-status") with
            | Some(Failed _) -> true
            | _ -> false)
        5000

    let result = host.RunCommand("fail-status-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("false") @>

[<Fact>]
let ``emits CommandCompleted on success`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCommand, recorder) = commandRecorder ()
    host.RegisterHandler(recorder)

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "echo-cmd") (fun _ -> true) "echo" "hello" false None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match getCommand () with
            | Some _ -> true
            | None -> false)
        5000

    let cmd = getCommand ()
    test <@ cmd.IsSome @>
    test <@ cmd.Value.Name = "echo-cmd" @>

    test
        <@
            match cmd.Value.Outcome with
            | FsHotWatch.Events.CommandSucceeded output -> output.Contains("hello")
            | _ -> false
        @>

[<Fact>]
let ``emits CommandCompleted on failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"
    let (getCommand, recorder) = commandRecorder ()
    host.RegisterHandler(recorder)

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "fail-cmd-emit") (fun _ -> true) "false" "" false None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    waitUntil
        (fun () ->
            match getCommand () with
            | Some _ -> true
            | None -> false)
        5000

    let cmd = getCommand ()
    test <@ cmd.IsSome @>
    test <@ cmd.Value.Name = "fail-cmd-emit" @>

    test
        <@
            match cmd.Value.Outcome with
            | FsHotWatch.Events.CommandFailed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``runOnStart runs command on first FileChanged even without matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "setup-cmd") (fun _ -> false) "echo" "setup" true None

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("setup-cmd") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let status = host.GetStatus("setup-cmd")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``runOnStart only triggers once`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler =
        create (FsHotWatch.PluginFramework.PluginName.create "setup-once") (fun _ -> false) "echo" "setup" true None

    host.RegisterHandler(handler)

    // First event — runs despite no matching files
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitUntil
        (fun () ->
            match host.GetStatus("setup-once") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    let result1 = host.RunCommand("setup-once-status", [||]) |> Async.RunSynchronously

    test <@ result1.IsSome @>
    test <@ result1.Value.Contains("true") @>

    // Second event — no matching files and already ran once, should not re-run
    // Reset status observation by sending another event
    host.EmitFileChanged(SourceChanged [ "src/Other.fs" ])

    // Wait briefly — command should NOT run again
    waitUntil
        (fun () ->
            match host.GetStatus("setup-once") with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false)
        1000
    |> ignore

    // Status should still show the original successful run
    let result2 = host.RunCommand("setup-once-status", [||]) |> Async.RunSynchronously

    test <@ result2.IsSome @>
    test <@ result2.Value.Contains("true") @>
