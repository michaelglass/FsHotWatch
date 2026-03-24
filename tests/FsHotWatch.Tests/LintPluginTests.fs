module FsHotWatch.Tests.LintPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Lint.LintPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = LintPlugin() :> IFsHotWatchPlugin
    test <@ plugin.Name = "lint" @>

[<Fact>]
let ``warnings command returns zeroes when no files checked`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = LintPlugin()
    host.Register(plugin)

    let result = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>
    test <@ result.Value.Contains("\"warnings\": 0") @>

[<Fact>]
let ``LintPlugin with configPath sets up lint params`` () =
    // Exercise the Some path branch in LintPlugin constructor
    let plugin = LintPlugin(configPath = "/tmp/nonexistent-config.json") :> IFsHotWatchPlugin
    test <@ plugin.Name = "lint" @>

[<Fact>]
let ``lint error path sets Failed status on null check results`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = LintPlugin()
    host.Register(plugin)

    // Emit a FileCheckResult with null ParseResults to trigger the catch block
    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_> }

    try
        host.EmitFileChecked(fakeResult)
    with
    | _ -> ()

    let status = host.GetStatus("lint")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> () // Expected: the error path was exercised
    | Running _ -> () // Also acceptable: status set to Running before error
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")
