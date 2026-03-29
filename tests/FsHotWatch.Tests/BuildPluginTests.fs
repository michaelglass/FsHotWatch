module FsHotWatch.Tests.BuildPluginTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Build
open FsHotWatch.Tests.TestHelpers

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
        { Name = "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions =
            { PluginSubscriptions.none with
                BuildCompleted = true } }

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> receivedBuild.IsSome) 5000
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

    waitForTerminalStatus host "build" 5000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"status\": \"passed\"") @>

[<Fact>]
let ``build-status command returns failed after failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" []
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    let result = host.RunCommand("build-status", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"status\": \"failed\"") @>

[<Fact>]
let ``build plugin reports Failed status on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let handler = BuildPlugin.create "false" "" []
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

[<Fact>]
let ``build plugin emits BuildFailed on failed build`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { Name = "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions =
            { PluginSubscriptions.none with
                BuildCompleted = true } }

    let handler = BuildPlugin.create "false" "" []
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SourceChanged [ "src/Lib.fs" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> receivedBuild.IsSome) 5000

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

    waitForTerminalStatus host "build" 5000

    test <@ host.HasErrors() @>

[<Fact>]
let ``build plugin handles exception from runProcess`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    // Use a command that doesn't exist to trigger an exception
    let handler = BuildPlugin.create "this-command-does-not-exist-xyz" "" []

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

    test <@ host.HasErrors() @>

[<Fact>]
let ``build plugin ignores SolutionChanged events`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { Name = "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions =
            { PluginSubscriptions.none with
                BuildCompleted = true } }

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(SolutionChanged "test.sln")

    // SolutionChanged is ignored — poll briefly; will time out (expected)
    waitUntil (fun () -> receivedBuild.IsSome) 200

    test <@ receivedBuild = None @>

[<Fact>]
let ``build plugin triggers on ProjectChanged`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let mutable receivedBuild: BuildResult option = None

    let recorder =
        { Name = "build-recorder"
          Init = ()
          Update =
            fun _ctx state event ->
                async {
                    match event with
                    | BuildCompleted result -> receivedBuild <- Some result
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions =
            { PluginSubscriptions.none with
                BuildCompleted = true } }

    let handler = BuildPlugin.create "echo" "build succeeded" []
    host.RegisterHandler(recorder)
    host.RegisterHandler(handler)

    host.EmitFileChanged(ProjectChanged [ "src/Lib.fsproj" ])

    waitForTerminalStatus host "build" 5000

    waitUntil (fun () -> receivedBuild.IsSome) 5000
    test <@ receivedBuild = Some BuildSucceeded @>
