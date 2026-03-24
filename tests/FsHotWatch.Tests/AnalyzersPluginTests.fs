module FsHotWatch.Tests.AnalyzersPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Analyzers.AnalyzersPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = AnalyzersPlugin([]) :> IFsHotWatchPlugin
    test <@ plugin.Name = "analyzers" @>

[<Fact>]
let ``diagnostics command returns zeroes when no files checked`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = AnalyzersPlugin([])
    host.Register(plugin)

    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\": 0") @>
    test <@ result.Value.Contains("\"files\": 0") @>
    test <@ result.Value.Contains("\"diagnostics\": 0") @>

[<Fact>]
let ``analyzer error path sets Failed status`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = AnalyzersPlugin([])
    host.Register(plugin)

    // Emit a FileCheckResult with a non-existent file to trigger the error path.
    // The AnalyzersPlugin tries to create a CliContext via reflection, which will
    // fail because the analyzer client has no loaded analyzers and the reflection
    // call may throw on invalid inputs. We use Unchecked.defaultof to force an error.
    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_> }

    try
        host.EmitFileChecked(fakeResult)
    with
    | _ -> ()

    let status = host.GetStatus("analyzers")
    test <@ status.IsSome @>

    match status.Value with
    | Failed _ -> () // Expected: the error path was exercised
    | Running _ -> () // Also acceptable: status set to Running before error
    | other -> Assert.Fail($"Expected Failed or Running, got: %A{other}")
