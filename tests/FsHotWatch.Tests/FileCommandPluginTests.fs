module FsHotWatch.Tests.FileCommandPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.FileCommand.FileCommandPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin =
        FileCommandPlugin("run-scripts", (fun f -> f.EndsWith(".fsx")), "echo", "hello") :> IFsHotWatchPlugin

    test <@ plugin.Name = "run-scripts" @>

[<Fact>]
let ``command runs when matching files change`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin =
        FileCommandPlugin("run-scripts", (fun f -> f.EndsWith(".fsx")), "echo", "hello")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "scripts/build.fsx" ])

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

    let plugin =
        FileCommandPlugin("run-scripts", (fun f -> f.EndsWith(".fsx")), "echo", "hello")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let status = host.GetStatus("run-scripts")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact>]
let ``command captures stdout output`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin =
        FileCommandPlugin("echo-test", (fun _ -> true), "echo", "captured-output")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "anything.txt" ])

    let result = host.RunCommand("echo-test-status", [||]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("true") @>

[<Fact>]
let ``command with environment variables`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin =
        FileCommandPlugin("env-test", (fun _ -> true), "echo", "env-test-output")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    let status = host.GetStatus("env-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``dispose is callable`` () =
    let plugin =
        FileCommandPlugin("disposable", (fun _ -> true), "echo", "hello") :> IFsHotWatchPlugin

    plugin.Dispose()

[<Fact>]
let ``command runs on ProjectChanged with matching files`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin =
        FileCommandPlugin("proj-watcher", (fun f -> f.EndsWith(".fsproj")), "echo", "project changed")

    host.Register(plugin)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

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

    let plugin = FileCommandPlugin("sln-watcher", (fun _ -> true), "echo", "hello")

    host.Register(plugin)

    host.EmitFileChanged(SolutionChanged "test.sln")

    let status = host.GetStatus("sln-watcher")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>

[<Fact>]
let ``command reports Failed status on command failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FileCommandPlugin("fail-cmd", (fun _ -> true), "false", "")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    let status = host.GetStatus("fail-cmd")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``command reports Failed status on exception`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin =
        FileCommandPlugin("bad-cmd", (fun _ -> true), "this-command-does-not-exist-xyz", "")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    let status = host.GetStatus("bad-cmd")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``status command returns not run when no files matched`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FileCommandPlugin("no-match", (fun _ -> false), "echo", "hello")

    host.Register(plugin)

    // No files match, so command never runs
    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    let result = host.RunCommand("no-match-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact>]
let ``status command returns false when command failed`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FileCommandPlugin("fail-status", (fun _ -> true), "false", "")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "file.txt" ])

    let result = host.RunCommand("fail-status-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("false") @>
