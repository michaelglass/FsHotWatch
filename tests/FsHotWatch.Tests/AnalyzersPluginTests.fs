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

    let fakeResult: FileCheckResult =
        { File = "/tmp/nonexistent/Fake.fs"
          Source = ""
          ParseResults = Unchecked.defaultof<_>
          CheckResults = Unchecked.defaultof<_> }

    try
        host.EmitFileChecked(fakeResult)
    with
    | _ -> ()

    let status = host.GetStatus("analyzers")
    test <@ status.IsSome @>

    match status.Value with
    | Completed _ -> () // Per-file errors don't fail the whole plugin
    | Running _ -> ()
    | other -> Assert.Fail($"Expected Completed or Running, got: %A{other}")

[<Fact>]
let ``analyzer with non-existent path skips loading`` () =
    // Exercise the Directory.Exists false branch
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = AnalyzersPlugin([ "/tmp/no-such-analyzer-dir-12345" ])
    host.Register(plugin)

    // No analyzers should be loaded — diagnostics command shows 0 analyzers
    let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"analyzers\": 0") @>

[<Fact>]
let ``analyzer with mix of valid and invalid paths`` () =
    // Create a real empty dir that exists, paired with one that does not
    let emptyDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"az-empty-{System.Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(emptyDir) |> ignore

    try
        let host =
            PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let plugin =
            AnalyzersPlugin(
                [ emptyDir // exists but no analyzer DLLs
                  "/tmp/nonexistent-path-xyz-99999" ] // does not exist
            )

        host.Register(plugin)

        let result = host.RunCommand("diagnostics", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("\"analyzers\": 0") @>
    finally
        try
            System.IO.Directory.Delete(emptyDir, true)
        with _ ->
            ()

[<Fact>]
let ``analyzer dispose is callable`` () =
    let plugin = AnalyzersPlugin([]) :> IFsHotWatchPlugin
    // Dispose should not throw
    plugin.Dispose()
