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
    let plugin = BuildPlugin(command = "echo", args = "build succeeded") :> IFsHotWatchPlugin
    test <@ plugin.Name = "build" @>

[<Fact>]
let ``build-status command returns not run initially`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "echo", args = "build succeeded")
    host.Register(plugin)

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("not run") @>

[<Fact>]
let ``build plugin emits BuildCompleted on successful build`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "echo", args = "build succeeded")
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"passed\": true") @>

[<Fact>]
let ``build-status command returns passed false after failed build`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = BuildPlugin(command = "false", args = "")
    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"passed\": false") @>
