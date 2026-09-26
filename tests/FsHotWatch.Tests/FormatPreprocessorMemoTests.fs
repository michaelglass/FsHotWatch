module FsHotWatch.Tests.FormatPreprocessorMemoTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper
open FsHotWatch.Fantomas.FantomasTool
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.Tests.TestHelpers

// The format preprocessor runs on every scan, and `check` forces a scan over the whole
// tree. The pinned Fantomas is a pure function of (its version, the `.editorconfig`
// files above a file, the file's bytes), so bytes it has already left unchanged under
// the same version and configuration need not be handed to it again. These tests pin
// what may be skipped and, as importantly, what may not.

let private pinVersion (dir: string) (version: string) =
    Directory.CreateDirectory(Path.Combine(dir, ".config")) |> ignore

    File.WriteAllText(
        Path.Combine(dir, ".config", "dotnet-tools.json"),
        $"""{{ "version": 1, "isRoot": true, "tools": {{ "fantomas": {{ "version": "%s{version}", "commands": ["fantomas"] }} }} }}"""
    )

/// A runner that records the files each invocation was handed and answers with
/// `outcome`, after applying `rewrite` to each of them (the tool's effect on disk).
let private recorder (outcome: ProcessOutcome) (rewrite: string -> unit) =
    let calls = ResizeArray<string list>()

    let runner: Runner =
        fun _pin args _workDir _timeout ->
            let files =
                args.Split('"', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (fun s -> s <> "")
                |> List.ofArray

            calls.Add files
            List.iter rewrite files
            outcome

    runner, (fun () -> List.ofSeq calls)

let private clean = Succeeded(ProcessOutput.Drained "")

let private run (preprocessor: IFsHotWatchPreprocessor) (files: string list) (dir: string) =
    match preprocessor.Process files dir with
    | Ok result -> result
    | Error e -> failwith $"expected a run, got a refusal: %s{e}"

let private withRepo (body: string -> string -> string -> unit) =
    withTempDir "fmt-memo" (fun dir ->
        pinVersion dir "7.0.5"
        let a = Path.Combine(dir, "A.fs")
        let b = Path.Combine(dir, "B.fs")
        File.WriteAllText(a, "module A\n\nlet x = 1\n")
        File.WriteAllText(b, "module B\n\nlet y = 2\n")
        body dir a b)

[<Fact(Timeout = 15000)>]
let ``bytes the formatter left unchanged are not handed to it again`` () =
    withRepo (fun dir a b ->
        let runner, calls = recorder clean ignore
        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        run preprocessor [ a; b ] dir |> ignore
        let second = run preprocessor [ a; b ] dir

        test <@ calls () = [ [ a; b ] ] @>
        // Still counted: they were considered, and they are formatted.
        test <@ second.Considered = 2 @>
        test <@ List.isEmpty second.Modified @>)

[<Fact(Timeout = 15000)>]
let ``an edited file is handed to the formatter again, and only that file`` () =
    withRepo (fun dir a b ->
        let runner, calls = recorder clean ignore
        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        run preprocessor [ a; b ] dir |> ignore
        File.AppendAllText(a, "let z = 3\n")
        run preprocessor [ a; b ] dir |> ignore

        test <@ calls () = [ [ a; b ]; [ a ] ] @>)

[<Fact(Timeout = 15000)>]
let ``a changed editorconfig or pin sends every file back to the formatter`` () =
    withRepo (fun dir a b ->
        let runner, calls = recorder clean ignore
        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        run preprocessor [ a; b ] dir |> ignore
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")
        run preprocessor [ a; b ] dir |> ignore
        pinVersion dir "7.0.6"
        run preprocessor [ a; b ] dir |> ignore

        test <@ calls () = [ [ a; b ]; [ a; b ]; [ a; b ] ] @>)

[<Fact(Timeout = 15000)>]
let ``a file the formatter rewrote is checked again, since its new bytes are unproven`` () =
    withRepo (fun dir a _ ->
        let rewrite (file: string) = File.AppendAllText(file, "\n")
        let runner, calls = recorder clean rewrite
        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        let first = run preprocessor [ a ] dir
        run preprocessor [ a ] dir |> ignore

        test <@ first.Modified = [ a ] @>
        test <@ calls () = [ [ a ]; [ a ] ] @>)

[<Fact(Timeout = 15000)>]
let ``nothing is remembered from a run that timed out or could not format a file`` () =
    withRepo (fun dir a b ->
        let timedOut =
            TimedOut(
                TimeSpan.FromSeconds 1.0,
                ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0),
                KillOutcome.Killed
            )

        let outcomes =
            Collections.Generic.Queue<ProcessOutcome>(
                [ timedOut
                  Failed(1, ProcessOutput.Drained $"Failed to format file: %s{b} : parse error")
                  clean ]
            )

        let calls = ResizeArray<int>()

        let runner: Runner =
            fun _ _ _ _ ->
                calls.Add 1
                outcomes.Dequeue()

        let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor

        run preprocessor [ a; b ] dir |> ignore
        run preprocessor [ a; b ] dir |> ignore
        // The failed run proved `a` clean but not `b`; the timed-out run proved nothing.
        run preprocessor [ a; b ] dir |> ignore
        run preprocessor [ a ] dir |> ignore

        test <@ calls.Count = 3 @>)

[<Fact(Timeout = 15000)>]
let ``a file that cannot be read is never remembered as formatted`` () =
    // Unix permissions only: Windows ignores the mode bits this relies on.
    if not (OperatingSystem.IsWindows()) then
        withRepo (fun dir a _ ->
            let runner, calls = recorder clean ignore
            let preprocessor = FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor
            File.SetUnixFileMode(a, UnixFileMode.None)

            try
                run preprocessor [ a ] dir |> ignore
                run preprocessor [ a ] dir |> ignore
            finally
                File.SetUnixFileMode(a, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

            test <@ calls () = [ [ a ]; [ a ] ] @>)
