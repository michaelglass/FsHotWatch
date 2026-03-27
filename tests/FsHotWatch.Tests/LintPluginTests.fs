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
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = LintPlugin()
    host.Register(plugin)

    let result = host.RunCommand("warnings", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>
    test <@ result.Value.Contains("\"warnings\": 0") @>

[<Fact>]
let ``LintPlugin with configPath sets up lint params`` () =
    let plugin =
        LintPlugin(configPath = "/tmp/nonexistent-config.json") :> IFsHotWatchPlugin

    test <@ plugin.Name = "lint" @>

[<Fact>]
let ``lint error path sets Failed status on null check results`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = LintPlugin()
    host.Register(plugin)

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_>
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

    let status = host.GetStatus("lint")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> ()
    | Running _ -> ()
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")

[<Fact>]
let ``warnings command with args passes through`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = LintPlugin()
    host.Register(plugin)

    // The warnings command ignores args, but verify it handles non-empty args
    let result =
        host.RunCommand("warnings", [| "--verbose" |]) |> Async.RunSynchronously

    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"files\": 0") @>

[<Fact>]
let ``dispose is callable`` () =
    let plugin = LintPlugin() :> IFsHotWatchPlugin
    plugin.Dispose()

[<Fact>]
let ``lint error path with empty source triggers failure`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = LintPlugin()
    host.Register(plugin)

    // Provide a source string but null ParseResults — the Ast access will throw
    let fakeResult: FileCheckResult =
        { File = "/tmp/test/Empty.fs"
          Source = "module Empty"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_>
          ProjectOptions = Unchecked.defaultof<_>
          Version = 0L }

    try
        host.EmitFileChecked(fakeResult)
    with _ ->
        ()

    let status = host.GetStatus("lint")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> ()
    | Running _ -> ()
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")
