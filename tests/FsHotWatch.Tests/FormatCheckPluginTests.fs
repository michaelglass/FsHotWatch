module FsHotWatch.Tests.FormatCheckPluginTests

open System
open System.IO
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
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FormatCheckPlugin()
    host.Register(plugin)

    let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
    test <@ result.IsSome @>
    test <@ result.Value.Contains("\"count\": 0") @>

[<Fact>]
let ``format check handles non-source change events without crashing`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

    let plugin = FormatCheckPlugin()
    host.Register(plugin)

    // ProjectChanged and SolutionChanged should not crash the plugin
    host.EmitFileChanged(ProjectChanged [ "/tmp/Test.fsproj" ])
    host.EmitFileChanged(SolutionChanged "test.sln")

    // The plugin still sets Completed status (empty unformatted set)
    let status = host.GetStatus("format-check")
    test <@ status.IsSome @>

    match status.Value with
    | Completed _ -> ()
    | other -> Assert.Fail($"Expected Completed, got: %A{other}")

[<Fact>]
let ``format check handles non-existent source file gracefully`` () =
    let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

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

[<Fact>]
let ``FormatPreprocessor formats unformatted file`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        // Badly formatted: missing spaces, wrong indentation
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.Length = 1 @>
        test <@ modified.[0] = file @>

        // Verify the file was rewritten
        let contents = File.ReadAllText(file)
        test <@ contents <> "module Bad\nlet   x=1\nlet   y   =   2\n" @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``FormatPreprocessor skips already formatted file`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Good.fs")
        // Well-formatted F# code
        File.WriteAllText(file, "module Good\n\nlet x = 1\nlet y = 2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.IsEmpty @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``FormatPreprocessor skips non-fs files`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "readme.txt")
        File.WriteAllText(file, "hello world")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.IsEmpty @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``FormatPreprocessor handles non-existent file gracefully`` () =
    let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
    let modified = preprocessor.Process [ "/tmp/nonexistent-file-xyz.fs" ] "/tmp"
    test <@ modified.IsEmpty @>

[<Fact>]
let ``FormatPreprocessor handles format error gracefully`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmt-err-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        // Write invalid F# that Fantomas cannot parse
        File.WriteAllText(file, "module \x00\x00\x00")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        // Should not throw; error is caught internally
        let modified = preprocessor.Process [ file ] tmpDir
        // May or may not modify depending on Fantomas behavior, but should not crash
        test <@ true @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)

[<Fact>]
let ``FormatPreprocessor dispose is callable`` () =
    let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
    preprocessor.Dispose()

[<Fact>]
let ``FormatCheckPlugin dispose is callable`` () =
    let plugin = FormatCheckPlugin() :> IFsHotWatchPlugin
    plugin.Dispose()

[<Fact>]
let ``format check handles exception gracefully`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-fmtchk-err-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        let file = Path.Combine(tmpDir, "Bad.fs")
        // Write invalid content that might cause Fantomas to throw
        File.WriteAllText(file, "module \x00\x00\x00")

        let host = PluginHost.create (Unchecked.defaultof<_>) "/tmp"

        let plugin = FormatCheckPlugin()
        host.Register(plugin)

        // This should not crash the plugin - errors are caught
        host.EmitFileChanged(SourceChanged [ file ])

        let status = host.GetStatus("format-check")
        test <@ status.IsSome @>
    finally
        if Directory.Exists tmpDir then
            Directory.Delete(tmpDir, true)
