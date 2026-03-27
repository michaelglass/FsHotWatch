module FsHotWatch.Tests.BuildPluginTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Build.BuildPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin =
        BuildPlugin(command = "echo", args = "build succeeded") :> IFsHotWatchPlugin

    test <@ plugin.Name = "build" @>

[<Fact>]
let ``build-status command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "echo", args = "build succeeded")
    host.Register(plugin)

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact>]
let ``build plugin emits BuildCompleted on successful build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { new IFsHotWatchPlugin with
            member _.Name = "build-recorder"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    let plugin = BuildPlugin(command = "echo", args = "build succeeded")
    host.Register(recorder)
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    test <@ receivedBuild = Some BuildSucceeded @>

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Completed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``build-status command returns passed true after successful build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "echo", args = "build succeeded")
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"status\": \"passed\"") @>

[<Fact>]
let ``build-status command returns failed after failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "false", args = "")
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"status\": \"failed\"") @>

[<Fact>]
let ``build plugin reports Failed status on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "false", args = "")
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``build plugin emits BuildFailed on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { new IFsHotWatchPlugin with
            member _.Name = "build-recorder"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    let plugin = BuildPlugin(command = "false", args = "")
    host.Register(recorder)
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact>]
let ``build plugin reports errors on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "false", args = "")
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    test <@ host.HasErrors() @>

[<Fact>]
let ``build plugin handles exception from runProcess`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    // Use a command that doesn't exist to trigger an exception
    let plugin = BuildPlugin(command = "this-command-does-not-exist-xyz", args = "")
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let status = host.GetStatus("build")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Failed _ -> true
            | _ -> false
        @>

    test <@ host.HasErrors() @>

[<Fact>]
let ``build plugin ignores SolutionChanged events`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { new IFsHotWatchPlugin with
            member _.Name = "build-recorder"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    let plugin = BuildPlugin(command = "echo", args = "build succeeded")
    host.Register(recorder)
    host.Register(plugin)

    host.EmitFileChanged(SolutionChanged)

    test <@ receivedBuild = None @>

[<Fact>]
let ``build plugin triggers on ProjectChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { new IFsHotWatchPlugin with
            member _.Name = "build-recorder"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    let plugin = BuildPlugin(command = "echo", args = "build succeeded")
    host.Register(recorder)
    host.Register(plugin)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    test <@ receivedBuild = Some BuildSucceeded @>

[<Fact>]
let ``build plugin dispose is callable`` () =
    let plugin = BuildPlugin(command = "echo", args = "") :> IFsHotWatchPlugin
    plugin.Dispose()
