module FsHotWatch.Tests.FormatIgnoreTests

// The ignore files decide which files the formatter is asked about at all. These
// tests pin that filter at the seam: a recording runner shows exactly which paths
// reached `dotnet tool run fantomas`, so an ignored file is proven never to be
// offered rather than merely reported clean.

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.ProcessHelper
open FsHotWatch.Fantomas.FantomasTool
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.Tests.TestHelpers

/// A temp repository pinning a fake fantomas, so the runner seam is the only formatter.
let private withPinnedTempDir (prefix: string) (body: string -> 'a) : 'a =
    withTempDir prefix (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".config")) |> ignore

        File.WriteAllText(
            Path.Combine(dir, ".config", "dotnet-tools.json"),
            """{ "version": 1, "isRoot": true, "tools": { "fantomas": { "version": "7.0.5", "commands": ["fantomas"] } } }"""
        )

        body dir)

/// A runner that reports EVERY file it is offered as unformatted, and records them.
/// With it, "count 0" can only mean the file was never offered.
let private everythingUnformatted () =
    let offered = ResizeArray<string>()

    let runner: Runner =
        fun _ args _ _ ->
            let paths =
                args.Split('"', StringSplitOptions.RemoveEmptyEntries)
                |> Array.filter (fun s -> s.EndsWith ".fs")
                |> List.ofArray

            offered.AddRange paths

            let lines =
                paths |> List.map (fun p -> $"%s{p} needs formatting") |> String.concat "\n"

            if List.isEmpty paths then
                Succeeded(ProcessOutput.Drained "")
            else
                Failed(NeedsFormattingExitCode, ProcessOutput.Drained lines)

    runner, (fun () -> List.ofSeq offered)

let private runCheck (dir: string) (runner: Runner) (file: string) : string =
    let host = PluginHost.create (Unchecked.defaultof<_>) dir
    host.RegisterHandler(createFormatCheckWith runner dir None)

    host.EmitFileChanged(SourceChanged [ file ])

    waitUntil
        (fun () ->
            match host.GetStatus("format-check") with
            | Some(Completed _) -> true
            | _ -> false)
        5000

    (host.RunCommand("unformatted", [||]) |> Async.RunSynchronously).Value

[<Fact(Timeout = 20000)>]
let ``format check skips files matched by fantomasignore`` () =
    withPinnedTempDir "fmt-fantomasignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "vendor/\n")

        let vendorDir = Path.Combine(tmpDir, "vendor")
        Directory.CreateDirectory(vendorDir) |> ignore
        let file = Path.Combine(vendorDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\n")

        let runner, offered = everythingUnformatted ()
        test <@ (runCheck tmpDir runner file).Contains("\"count\": 0") @>
        test <@ List.isEmpty (offered ()) @>)

[<Fact(Timeout = 20000)>]
let ``format check skips files matched by gitignore`` () =
    withPinnedTempDir "fmt-gitignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.generated.fs\n")

        let file = Path.Combine(tmpDir, "Types.generated.fs")
        File.WriteAllText(file, "module Types\nlet   x=1\n")

        let runner, offered = everythingUnformatted ()
        test <@ (runCheck tmpDir runner file).Contains("\"count\": 0") @>
        test <@ List.isEmpty (offered ()) @>)

[<Fact(Timeout = 20000)>]
let ``format check still checks files not in any ignore file`` () =
    withPinnedTempDir "fmt-no-ignore" (fun tmpDir ->
        // The ignore file matches something else, so Bad.fs stays in scope.
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")

        let file = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\n")

        let runner, offered = everythingUnformatted ()
        test <@ (runCheck tmpDir runner file).Contains("\"count\": 1") @>
        test <@ offered () = [ file ] @>)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor skips files matched by fantomasignore`` () =
    withPinnedTempDir "preproc-fantomasignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".fantomasignore"), "vendor/\n")

        let vendorDir = Path.Combine(tmpDir, "vendor")
        Directory.CreateDirectory(vendorDir) |> ignore
        let file = Path.Combine(vendorDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\n")

        let runner, offered = everythingUnformatted ()
        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] tmpDir with
        | Ok result ->
            test <@ List.isEmpty result.Modified @>
            test <@ result.Considered = 0 @>
        | Error e -> failwith e

        test <@ List.isEmpty (offered ()) @>
        test <@ File.ReadAllText(file) = "module Bad\nlet   x=1\n" @>)

[<Fact(Timeout = 15000)>]
let ``FormatPreprocessor skips files matched by gitignore`` () =
    withPinnedTempDir "preproc-gitignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.generated.fs\n")

        let file = Path.Combine(tmpDir, "Types.generated.fs")
        File.WriteAllText(file, "module Types\nlet   x=1\n")

        let runner, offered = everythingUnformatted ()
        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] tmpDir with
        | Ok result -> test <@ result.Considered = 0 @>
        | Error e -> failwith e

        test <@ List.isEmpty (offered ()) @>)

[<Fact(Timeout = 20000)>]
let ``FormatPreprocessor offers files not in any ignore file`` () =
    withPinnedTempDir "preproc-no-ignore" (fun tmpDir ->
        File.WriteAllText(Path.Combine(tmpDir, ".gitignore"), "*.log\n")

        let file = Path.Combine(tmpDir, "Bad.fs")
        File.WriteAllText(file, "module Bad\nlet   x=1\nlet   y   =   2\n")

        // A rewriting runner: what a formatter does, so `Modified` is measured.
        let runner: Runner =
            fun _ _ _ _ ->
                File.WriteAllText(file, "module Bad\n\nlet x = 1\nlet y = 2\n")
                Succeeded(ProcessOutput.Drained "")

        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        match preprocessor.Process [ file ] tmpDir with
        | Ok result ->
            test <@ result.Modified = [ file ] @>
            test <@ result.Considered = 1 @>
        | Error e -> failwith e)
