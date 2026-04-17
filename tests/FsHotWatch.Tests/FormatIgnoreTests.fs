module FsHotWatch.Tests.FormatIgnoreTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 30000)>]
let ``format check skips files matched by fantomasignore`` () =
    withTempDir "fmt-fantomasignore" (fun tmpDir ->
        // Create .fantomasignore that excludes vendor/
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "vendor/\n")

        // Create an unformatted file in vendor/
        let vendorDir = Path.Combine(tmpDir, "vendor")
        Directory.CreateDirectory(vendorDir) |> ignore
        let file = Path.Combine(vendorDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        // File should NOT be reported as unformatted — it's ignored
        let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("\"count\": 0") @>)

[<Fact(Timeout = 30000)>]
let ``format check skips files matched by gitignore`` () =
    withTempDir "fmt-gitignore" (fun tmpDir ->
        // Create .gitignore that excludes generated files
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.generated.fs\n")

        // Create an unformatted generated file
        let file = Path.Combine(tmpDir, "Types.generated.fs")
        File.WriteAllText(file, "module Types\nlet   x=1\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        // File should NOT be reported as unformatted — it's git-ignored
        let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("\"count\": 0") @>)

[<Fact(Timeout = 30000)>]
let ``format check still checks files not in any ignore file`` () =
    withTempDir "fmt-no-ignore" (fun tmpDir ->
        // Create .gitignore that excludes something else
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")

        // Create an unformatted source file
        let file = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\n")

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = createFormatCheck None
        host.RegisterHandler(handler)

        host.EmitFileChanged(SourceChanged [ file ])

        waitUntil
            (fun () ->
                match host.GetStatus("format-check") with
                | Some(Completed _) -> true
                | _ -> false)
            5000

        // File SHOULD be reported as unformatted
        let result = host.RunCommand("unformatted", [||]) |> Async.RunSynchronously
        test <@ result.IsSome @>
        test <@ result.Value.Contains("\"count\": 1") @>)

[<Fact(Timeout = 30000)>]
let ``FormatPreprocessor skips files matched by fantomasignore`` () =
    withTempDir "preproc-fantomasignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "vendor/\n")

        let vendorDir = Path.Combine(tmpDir, "vendor")
        Directory.CreateDirectory(vendorDir) |> ignore
        let file = Path.Combine(vendorDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.IsEmpty @>

        // File should be untouched
        let contents = File.ReadAllText(file)
        test <@ contents = "module Bad\nlet   x=1\n" @>)

[<Fact(Timeout = 30000)>]
let ``FormatPreprocessor skips files matched by gitignore`` () =
    withTempDir "preproc-gitignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.generated.fs\n")

        let file = Path.Combine(tmpDir, "Types.generated.fs")
        File.WriteAllText(file, "module Types\nlet   x=1\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.IsEmpty @>)

[<Fact(Timeout = 30000)>]
let ``FormatPreprocessor formats files not in any ignore file`` () =
    withTempDir "preproc-no-ignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")

        let file = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        let preprocessor = FormatPreprocessor() :> IFsHotWatchPreprocessor
        let modified = preprocessor.Process [ file ] tmpDir
        test <@ modified.Length = 1 @>)
