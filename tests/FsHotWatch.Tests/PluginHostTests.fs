module FsHotWatch.Tests.PluginHostTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost

/// A null checker is fine for tests that don't perform actual compilation.
let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

type RecordingPlugin() =
    let mutable fileChanges = []

    interface IFsHotWatchPlugin with
        member _.Name = "recorder"

        member _.Initialize(ctx) =
            ctx.OnFileChanged.Add(fun change -> fileChanges <- change :: fileChanges)

        member _.Dispose() = ()

    member _.FileChanges = fileChanges |> List.rev

[<Fact>]
let ``plugin receives file change events`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let plugin = RecordingPlugin()
    host.Register(plugin)
    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])
    test <@ plugin.FileChanges.Length = 1 @>

[<Fact>]
let ``plugin registers command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable called = false

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "cmd-test"

            member _.Initialize(ctx) =
                ctx.RegisterCommand(
                    "greet",
                    fun _args ->
                        async {
                            called <- true
                            return "hello"
                        }
                )

            member _.Dispose() = () }

    host.Register(plugin)
    let result = host.RunCommand("greet", [||]) |> Async.RunSynchronously
    test <@ result = Some "hello" @>
    test <@ called @>

[<Fact>]
let ``RunCommand returns None for unknown command`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let result = host.RunCommand("bogus", [||]) |> Async.RunSynchronously
    test <@ result = None @>

[<Fact>]
let ``plugin reports status`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "status-test"

            member _.Initialize(ctx) =
                ctx.ReportStatus(Running(since = DateTime.UtcNow))

            member _.Dispose() = () }

    host.Register(plugin)
    let status = host.GetStatus("status-test")
    test <@ status.IsSome @>

    test
        <@
            match status.Value with
            | Running _ -> true
            | _ -> false
        @>

[<Fact>]
let ``GetAllStatuses returns all plugin statuses`` () =
    let host = PluginHost.create nullChecker "/tmp/test"

    let p1 =
        { new IFsHotWatchPlugin with
            member _.Name = "a"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }

    let p2 =
        { new IFsHotWatchPlugin with
            member _.Name = "b"
            member _.Initialize(ctx) = ctx.ReportStatus(Idle)
            member _.Dispose() = () }

    host.Register(p1)
    host.Register(p2)
    let all = host.GetAllStatuses()
    test <@ all.Count = 2 @>
    test <@ all |> Map.containsKey "a" @>
    test <@ all |> Map.containsKey "b" @>
