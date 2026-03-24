module FsHotWatch.Tests.FormatCheckPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Fantomas.FormatCheckPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = FormatCheckPlugin() :> IFsHotWatchPlugin
    test <@ plugin.Name = "format-check" @>

[<Fact>]
let ``unformatted command returns zero count when no files processed`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FormatCheckPlugin()
    host.Register(plugin)

    let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"count\": 0") @>

[<Fact>]
let ``format check handles non-source change events without crashing`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FormatCheckPlugin()
    host.Register(plugin)

    // ProjectChanged and SolutionChanged should not crash the plugin
    host.EmitFileChanged(ProjectChanged [ "/tmp/Test.fsproj" ])
    host.EmitFileChanged(SolutionChanged)

    // The plugin still sets Completed status (empty unformatted set)
    let status = host.GetStatus("format-check")
    test <@ status.IsSome @>

    match status.Value with
    | Completed(result, _) ->
        let unformatted = result :?> Set<string>
        test <@ unformatted.IsEmpty @>
    | other -> Assert.Fail($"Expected Completed, got: %A{other}")

[<Fact>]
let ``format check handles non-existent source file gracefully`` () =
    let host =
        PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FormatCheckPlugin()
    host.Register(plugin)

    // Emit a SourceChanged with a file that doesn't exist — File.Exists check
    // should cause it to be skipped (no crash)
    host.EmitFileChanged(SourceChanged [ "/tmp/nonexistent/Fake.fs" ])

    // The plugin sets Completed at the end of the event handler regardless,
    // but the non-existent file should not be in the unformatted set
    let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"count\": 0") @>
