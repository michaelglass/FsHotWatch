module FsHotWatch.Tests.FormatRecordsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Plugin
open FsHotWatch.ProcessHelper
open FsHotWatch.Fantomas.FantomasTool
open FsHotWatch.Fantomas.FormatCheckPlugin
open FsHotWatch.Tests.TestHelpers

// AUTOMATION-1436: what the format preprocessor proved about a file's bytes outlives the
// daemon that proved it. A restart over an unchanged tree hands the pinned Fantomas
// nothing; anything that could change what the tool says about a file is a miss. Each
// test below runs one preprocessor, then a NEW one over the same outcome store — the
// shape of a daemon restart.

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

let private storeAt (root: string) =
    OutcomeStore.Store(
        { Root = root
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = ignore }
    )

/// One daemon's preprocessor over `store`, with `runner` as its tool.
let private daemonWith (store: OutcomeStore.Store) (runner: Runner) =
    FormatPreprocessor(runner = runner, store = store) :> IFsHotWatchPreprocessor

let private run (preprocessor: IFsHotWatchPreprocessor) (files: string list) (dir: string) =
    match preprocessor.Process (FsHotWatch.RepoFiles.parseAll dir files) dir with
    | Ok result -> result
    | Error e -> failwith $"expected a run, got a refusal: %s{e}"

let private withRepo (body: OutcomeStore.Store -> string -> string -> string -> unit) =
    withTempDir "fmt-records" (fun scratch ->
        let dir = Path.Combine(scratch, "repo")
        Directory.CreateDirectory dir |> ignore
        pinVersion dir "7.0.5"
        let a = Path.Combine(dir, "A.fs")
        let b = Path.Combine(dir, "B.fs")
        File.WriteAllText(a, "module A\n\nlet x = 1\n")
        File.WriteAllText(b, "module B\n\nlet y = 2\n")
        body (storeAt (Path.Combine(scratch, "outcomes"))) dir a b)

/// The first daemon: formats `files` with a tool that leaves every byte as it is.
let private settle (store: OutcomeStore.Store) (files: string list) (dir: string) =
    let runner, _ = recorder clean ignore
    run (daemonWith store runner) files dir |> ignore

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a restart over an unchanged tree hands the formatter no file`` () =
    withRepo (fun store dir a b ->
        settle store [ a; b ] dir

        let runner, calls = recorder clean ignore
        let restarted = run (daemonWith store runner) [ a; b ] dir

        test <@ List.isEmpty (calls ()) @>
        // Still counted: they were considered, and they are formatted.
        test <@ restarted.Considered = 2 @>
        test <@ List.isEmpty restarted.Modified && List.isEmpty restarted.Failures @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``after a restart an edited file is handed to the formatter, and formatted`` () =
    withRepo (fun store dir a b ->
        settle store [ a; b ] dir
        File.AppendAllText(a, "let   z=3\n")

        let runner, calls =
            recorder clean (fun f -> File.WriteAllText(f, "module A\n\nlet z = 3\n"))

        let restarted = run (daemonWith store runner) [ a; b ] dir

        test <@ calls () = [ [ a ] ] @>
        test <@ restarted.Modified = [ a ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a changed editorconfig invalidates every record`` () =
    withRepo (fun store dir a b ->
        settle store [ a; b ] dir
        let unchanged, unchangedCalls = recorder clean ignore
        run (daemonWith store unchanged) [ a; b ] dir |> ignore

        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")
        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a; b ] dir |> ignore

        test <@ List.isEmpty (unchangedCalls ()) @>
        test <@ calls () = [ [ a; b ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an editorconfig in a nested directory invalidates the records of the files beneath it`` () =
    withRepo (fun store dir a _ ->
        let sub = Path.Combine(dir, "sub")
        Directory.CreateDirectory sub |> ignore
        let c = Path.Combine(sub, "C.fs")
        File.WriteAllText(c, "module C\n")
        settle store [ a; c ] dir

        File.WriteAllText(Path.Combine(sub, ".editorconfig"), "[*.fs]\nindent_size = 2\n")
        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a; c ] dir |> ignore

        test <@ calls () = [ [ c ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a changed fantomas pin invalidates every record`` () =
    withRepo (fun store dir a b ->
        settle store [ a; b ] dir
        let unchanged, unchangedCalls = recorder clean ignore
        run (daemonWith store unchanged) [ a; b ] dir |> ignore

        pinVersion dir "7.0.6"
        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a; b ] dir |> ignore

        test <@ List.isEmpty (unchangedCalls ()) @>
        test <@ calls () = [ [ a; b ] ] @>)

// THE GUARD. A record says "the pinned tool left exactly these bytes unchanged". If one
// were ever filed for bytes the tool rewrote, a later daemon handed those unformatted
// bytes again (a revert, a checkout) would skip them, and the gate would pass a file that
// needs formatting.
[<Fact(Timeout = SyncHangBackstopMs)>]
let ``bytes the formatter rewrote are never filed as formatted`` () =
    withRepo (fun store dir a b ->
        let unformatted = File.ReadAllBytes a

        let rewrite (file: string) =
            if file = a then
                File.AppendAllText(file, "\n")

        let first, _ = recorder clean rewrite
        let firstRun = run (daemonWith store first) [ a; b ] dir
        // The checkout puts the unformatted bytes back.
        File.WriteAllBytes(a, unformatted)

        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a; b ] dir |> ignore

        test <@ firstRun.Modified = [ a ] @>
        test <@ calls () = [ [ a ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an editorconfig saved while the formatter ran files nothing`` () =
    // The tool left every byte as it was, but under which configuration is unknown: the
    // key before the run no longer names what is on disk after it.
    withRepo (fun store dir a b ->
        let editorconfig = Path.Combine(dir, ".editorconfig")

        let first, _ =
            recorder clean (fun _ -> File.WriteAllText(editorconfig, "[*.fs]\nindent_size = 2\n"))

        run (daemonWith store first) [ a; b ] dir |> ignore
        File.Delete editorconfig

        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a; b ] dir |> ignore

        test <@ calls () = [ [ a; b ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file deleted while the formatter ran is not filed`` () =
    withRepo (fun store dir a b ->
        let original = File.ReadAllBytes a

        let first, _ =
            recorder clean (fun file ->
                if file = a then
                    File.Delete file)

        run (daemonWith store first) [ a; b ] dir |> ignore
        File.WriteAllBytes(a, original)

        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a; b ] dir |> ignore

        test <@ calls () = [ [ a ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file the formatter could not format is not filed, and a timed-out pass files nothing`` () =
    withRepo (fun store dir a b ->
        let parseError, _ =
            recorder (Failed(1, ProcessOutput.Drained $"Failed to format file: %s{b} : Could not parse file.")) ignore

        run (daemonWith store parseError) [ a; b ] dir |> ignore
        let afterError, afterErrorCalls = recorder clean ignore
        run (daemonWith store afterError) [ a; b ] dir |> ignore

        let c = Path.Combine(dir, "C.fs")
        File.WriteAllText(c, "module C\n")

        let timedOut, _ =
            recorder
                (TimedOut(
                    TimeSpan.FromSeconds 1.0,
                    ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0),
                    KillOutcome.Killed
                ))
                ignore

        run (daemonWith store timedOut) [ c ] dir |> ignore
        let afterTimeout, afterTimeoutCalls = recorder clean ignore
        run (daemonWith store afterTimeout) [ a; b; c ] dir |> ignore

        test <@ afterErrorCalls () = [ [ b ] ] @>
        test <@ afterTimeoutCalls () = [ [ c ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a record speaks for one path, not for the same bytes at another`` () =
    // An `.editorconfig` section can match a file by NAME, so identical bytes under
    // another name are not proven formatted.
    withRepo (fun store dir a _ ->
        settle store [ a ] dir
        let copy = Path.Combine(dir, "Copy.fs")
        File.Copy(a, copy)
        let signature = Path.Combine(dir, "A.fsi")
        File.Copy(a, signature)

        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a; copy; signature ] dir |> ignore

        test <@ calls () = [ [ copy; signature ] ] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``another checkout of the same bytes reads the records`` () =
    withRepo (fun store dir a b ->
        settle store [ a; b ] dir
        let other = Path.Combine(Path.GetDirectoryName dir, "other")
        Directory.CreateDirectory other |> ignore
        pinVersion other "7.0.5"
        let a2 = Path.Combine(other, "A.fs")
        let b2 = Path.Combine(other, "B.fs")
        File.Copy(a, a2)
        File.Copy(b, b2)

        let runner, calls = recorder clean ignore
        run (daemonWith store runner) [ a2; b2 ] other |> ignore

        test <@ List.isEmpty (calls ()) @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a preprocessor with no store remembers nothing across a restart`` () =
    withRepo (fun _ dir a b ->
        let first, _ = recorder clean ignore

        run (FormatPreprocessor(runner = first) :> IFsHotWatchPreprocessor) [ a; b ] dir
        |> ignore

        let runner, calls = recorder clean ignore

        run (FormatPreprocessor(runner = runner) :> IFsHotWatchPreprocessor) [ a; b ] dir
        |> ignore

        test <@ calls () = [ [ a; b ] ] @>)
