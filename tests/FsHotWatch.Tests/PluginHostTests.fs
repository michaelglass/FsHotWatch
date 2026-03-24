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

[<Fact>]
let ``EmitBuildCompleted reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "build-listener"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    host.Register(plugin)
    host.EmitBuildCompleted(BuildSucceeded)
    test <@ receivedBuild = Some BuildSucceeded @>

[<Fact>]
let ``EmitBuildCompleted with failure reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedBuild: BuildResult option = None

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "build-fail-listener"

            member _.Initialize(ctx) =
                ctx.OnBuildCompleted.Add(fun result -> receivedBuild <- Some result)

            member _.Dispose() = () }

    host.Register(plugin)
    let errors = [ "error CS0001: Something broke" ]
    host.EmitBuildCompleted(BuildFailed errors)

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact>]
let ``EmitProjectChecked reaches plugins`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable receivedProject: ProjectCheckResult option = None

    let plugin =
        { new IFsHotWatchPlugin with
            member _.Name = "project-check-listener"

            member _.Initialize(ctx) =
                ctx.OnProjectChecked.Add(fun result -> receivedProject <- Some result)

            member _.Dispose() = () }

    host.Register(plugin)

    let result =
        { Project = "/tmp/test/Test.fsproj"
          FileResults = Map.empty }

    host.EmitProjectChecked(result)
    test <@ receivedProject.IsSome @>
    test <@ receivedProject.Value.Project = "/tmp/test/Test.fsproj" @>

[<Fact>]
let ``multiple plugins receive the same event`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let mutable received1 = false
    let mutable received2 = false
    let mutable received3 = false

    let makePlugin name (setter: unit -> unit) =
        { new IFsHotWatchPlugin with
            member _.Name = name

            member _.Initialize(ctx) =
                ctx.OnFileChanged.Add(fun _ -> setter ())

            member _.Dispose() = () }

    host.Register(makePlugin "p1" (fun () -> received1 <- true))
    host.Register(makePlugin "p2" (fun () -> received2 <- true))
    host.Register(makePlugin "p3" (fun () -> received3 <- true))

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    test <@ received1 @>
    test <@ received2 @>
    test <@ received3 @>
