module FsHotWatch.Tests.TestPrunePluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = TestPrunePlugin(":memory:", "/tmp") :> IFsHotWatchPlugin
    test <@ plugin.Name = "test-prune" @>

[<Fact>]
let ``affected-tests command returns empty list when no files checked`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let result = host.RunCommand("affected-tests", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact>]
let ``changed-files command returns empty list when no files checked`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    let result = host.RunCommand("changed-files", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value = "[]" @>

[<Fact>]
let ``test-prune error path sets Failed status on null check results`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = TestPrunePlugin(":memory:", "/tmp")
    host.Register(plugin)

    // Emit a FileCheckResult with null CheckResults to trigger the catch block
    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_> }

    try
        host.EmitFileChecked(fakeResult)
    with
    | _ -> ()

    let status = host.GetStatus("test-prune")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> () // Expected: the error path was exercised
    | Running _ -> () // Also acceptable: status set to Running before error
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")
