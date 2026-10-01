/// The traced-run FCS read recorder notes the sources a check reads, on every check that
/// uses them, and is absent untraced.
[<Xunit.Collection("fcs-file-system")>]
module FsHotWatch.Tests.TraceFcsReadsTests

open System
open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.IO
open FSharp.Compiler.Text
open FsHotWatch.Tests.TestHelpers

/// Runs `body` with a recording file system installed over the current one, noting into
/// the returned queue only what lies under `dir`; restores the previous one after.
let private recordingUnder (dir: string) (body: ConcurrentQueue<string * string> -> unit) =
    let noted = ConcurrentQueue<string * string>()
    let previous = FileSystemAutoOpens.FileSystem

    TraceFcsReads.installWith (fun kind path ->
        if path.StartsWith(dir, StringComparison.Ordinal) then
            noted.Enqueue((kind, Path.GetFileName path)))

    try
        body noted
    finally
        FileSystemAutoOpens.FileSystem <- previous

/// A script `b.fsx` that loads `a.fs`, and the checker options for it.
let private scriptProject (dir: string) (checker: FSharpChecker) =
    let a = Path.Combine(dir, "a.fs")
    let b = Path.Combine(dir, "b.fsx")
    File.WriteAllText(a, "module A\nlet x = 1\n")
    let source = "#load \"a.fs\"\nlet y = A.x + 1\n"
    File.WriteAllText(b, source)

    let options, _ =
        checker.GetProjectOptionsFromScript(b, SourceText.ofString source, assumeDotNetFramework = false)
        |> Async.RunSynchronously

    b, source, options

let private check (checker: FSharpChecker) (file: string) (source: string) options =
    match
        checker.ParseAndCheckFileInProject(file, 0, SourceText.ofString source, options)
        |> Async.RunSynchronously
    with
    | _, FSharpCheckFileAnswer.Succeeded result ->
        test
            <@
                result.Diagnostics
                |> Array.forall (fun d -> d.Severity <> FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
            @>
    | _, FSharpCheckFileAnswer.Aborted -> failwith "check aborted"

[<Fact(Timeout = 120000)>]
let ``a check notes the source it reads itself, and not the file it was handed`` () =
    withTempDir "fcs-reads" (fun dir ->
        let checker = FSharpChecker.Create()

        recordingUnder dir (fun noted ->
            let b, source, options = scriptProject dir checker
            noted.Clear()
            check checker b source options

            let reads =
                noted |> Seq.filter (fun (k, _) -> k = "read") |> Seq.map snd |> Set.ofSeq

            test <@ reads.Contains "a.fs" @>
            test <@ noted |> Seq.forall (fun (_, name) -> TraceFcsReads.isSource name) @>))

[<Fact(Timeout = 120000)>]
let ``a second check through the same checker notes the cached source again`` () =
    // FCS keeps a.fs parsed after the first check; the second reads nothing. Its stamp
    // check is what attributes a.fs to whichever test checks next.
    withTempDir "fcs-reads-cached" (fun dir ->
        let checker = FSharpChecker.Create()

        recordingUnder dir (fun noted ->
            let b, source, options = scriptProject dir checker
            check checker b source options
            noted.Clear()
            check checker b source options

            test <@ noted |> Seq.contains ("read", "a.fs") @>))

[<Fact(Timeout = 30000)>]
let ``assemblies and other non-sources are never noted`` () =
    test <@ TraceFcsReads.isSource "/r/src/A.fs" && TraceFcsReads.isSource "/r/B.fsi" @>
    test <@ TraceFcsReads.isSource "/r/s.fsx" @>

    test
        <@
            not (TraceFcsReads.isSource "/r/bin/Debug/A.dll")
            && not (TraceFcsReads.isSource null)
        @>

[<Fact(Timeout = 30000)>]
let ``untraced, nothing is installed; traced, the recorder is bound`` () =
    test <@ (TraceFcsReads.traceNote (fun _ -> null)).IsNone @>
    test <@ (TraceFcsReads.traceNote (fun _ -> "")).IsNone @>
    // The recorder ships beside the test assembly, so a traced process finds NoteInput.
    test <@ (TraceFcsReads.traceNote (fun _ -> "/run/traces")).IsSome @>

    // This process: recording only when it was launched traced.
    let installed = FileSystemAutoOpens.FileSystem :? TraceFcsReads.RecordingFileSystem

    let traced =
        not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable TraceFcsReads.TracedEnv))

    test <@ installed = traced @>
