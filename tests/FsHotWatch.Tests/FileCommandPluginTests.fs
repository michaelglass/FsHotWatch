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
        FileCommandPlugin("run-scripts", (fun f -> f.EndsWith(".fsx")), "echo", "hello")
        :> IFsHotWatchPlugin

    test <@ plugin.Name = "run-scripts" @>

[<Fact>]
let ``command runs when matching files change`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin =
        FileCommandPlugin("run-scripts", (fun f -> f.EndsWith(".fsx")), "echo", "hello")

    host.Register(plugin)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    let status = host.GetStatus("run-scripts")
    test <@ status.IsSome @>
    test <@ status.Value = Idle @>
