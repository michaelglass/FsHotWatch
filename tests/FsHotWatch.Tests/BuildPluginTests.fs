module FsHotWatch.Tests.BuildPluginTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Build

let private waitUntil (timeoutMs: int) (predicate: unit -> bool) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (predicate ()) && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep(50)

let private waitForBuildDone (host: PluginHost) (pluginName: string) (timeoutMs: int) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
    let mutable done' = false

    while not done' && DateTime.UtcNow < deadline do
        match host.GetStatus(pluginName) with
        | Some(PluginStatus.Completed _)
        | Some(PluginStatus.Failed _) -> done' <- true
        | _ -> System.Threading.Thread.Sleep(50)

[<Fact>]
let ``plugin has correct name`` () =
    let handler = BuildPlugin.create "echo" "build succeeded" []
    test <@ handler.Name = "build" @>

[<Fact>]
let ``build-status command returns not run initially`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.RegisterHandler(handler)

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

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.Register(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForBuildDone host "build" 5000

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

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForBuildDone host "build" 5000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"status\": \"passed\"") @>

[<Fact>]
let ``build-status command returns failed after failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" []
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForBuildDone host "build" 5000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"status\": \"failed\"") @>

[<Fact>]
let ``build plugin reports Failed status on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" []
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForBuildDone host "build" 5000

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

    let handler = BuildPlugin.create "false" "" []
    host.Register(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForBuildDone host "build" 5000

    test
        <@
            match receivedBuild with
            | Some(BuildFailed _) -> true
            | _ -> false
        @>

[<Fact>]
let ``build plugin reports errors on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" []
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForBuildDone host "build" 5000

    test <@ host.HasErrors() @>

[<Fact>]
let ``build plugin handles exception from runProcess`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    // Use a command that doesn't exist to trigger an exception
    let handler = BuildPlugin.create "this-command-does-not-exist-xyz" "" []

    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForBuildDone host "build" 5000

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

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.Register(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged "test.sln")

    // Give a small window — should NOT trigger
    System.Threading.Thread.Sleep(200)

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

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.Register(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    waitForBuildDone host "build" 5000

    test <@ receivedBuild = Some BuildSucceeded @>
