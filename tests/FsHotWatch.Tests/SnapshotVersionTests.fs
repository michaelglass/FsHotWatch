module FsHotWatch.Tests.SnapshotVersionTests

// Reads FSharpProjectSnapshot, which FCS marks experimental.
#nowarn "57"

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// A two-project tree checked by the daemon's own checker (TransparentCompiler):
/// B (B1.fs, B2.fs) references A (A.fs) both in memory and through `-r:` A's
/// output dll, and also references a vendored dll that no project builds — the
/// shape Ionide.ProjInfo hands the daemon.
type private Tree =
    {
        Dir: string
        Checker: FSharpChecker
        Pipeline: CheckPipeline
        AOptions: FSharpProjectOptions
        BOptions: FSharpProjectOptions
        ADll: string
        VendoredDll: string
        /// Files FCS type-checked since the last `drain`, by file name.
        Checked: Collections.Concurrent.ConcurrentQueue<string>
    }

let private write dir name (lines: string array) =
    File.WriteAllLines(Path.Combine(dir, name), lines)

/// Build `source` into the library `output` with FCS's own compiler.
let private compile (checker: FSharpChecker) (frameworkOptions: string array) (output: string) (source: string) =
    let diagnostics, error =
        checker.Compile(
            Array.concat
                [ [| "fsc.exe"
                     $"-o:%s{output}"
                     "-a"
                     "--deterministic+"
                     "--debug-"
                     "--optimize-" |]
                  frameworkOptions |> Array.filter (fun o -> not (o.StartsWith "-o:"))
                  [| source |] ]
        )
        |> Async.RunSynchronously

    let errors =
        diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

    if Option.isSome error || not (Array.isEmpty errors) then
        failwith $"compiling %s{source} failed: %A{error} %A{errors}"

let private makeTree (dir: string) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()
    let checkedFiles = Collections.Concurrent.ConcurrentQueue<string>()
    checker.BeforeBackgroundFileCheck.Add(fun (file, _) -> checkedFiles.Enqueue(Path.GetFileName file))

    write dir "A.fs" [| "module A"; "let f (x: int) = x + 1" |]
    write dir "B1.fs" [| "module B1"; "let g x = A.f x" |]
    write dir "B2.fs" [| "module B2"; "let h : int = B1.g 1" |]
    write dir "Base.fsx" [| "let placeholder = 0" |]

    let baseOptions, _ =
        let script = Path.Combine(dir, "Base.fsx")

        checker.GetProjectOptionsFromScript(
            script,
            SourceText.ofString (File.ReadAllText script),
            assumeDotNetFramework = false
        )
        |> Async.RunSynchronously

    let frameworkOptions =
        baseOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))

    // A real assembly nothing else references, standing in for a C# project's output.
    let vendoredDll = Path.Combine(dir, "lib", "Vendored.dll")
    Directory.CreateDirectory(Path.GetDirectoryName vendoredDll) |> ignore
    File.Copy(typeof<Swensen.Unquote.AssertionFailedException>.Assembly.Location, vendoredDll)

    // A's real build output: FCS type-checks B against it whenever it is newer than
    // A's sources (TransparentCompiler's ComputeAssemblyData), so it must be A.
    let aDll = Path.Combine(dir, "A", "bin", "A.dll")
    Directory.CreateDirectory(Path.GetDirectoryName aDll) |> ignore
    compile checker frameworkOptions aDll (Path.Combine(dir, "A.fs"))

    let project name (files: string list) (extraRefs: string list) refs =
        { baseOptions with
            ProjectFileName = Path.Combine(dir, $"%s{name}.fsproj")
            SourceFiles = [| for f in files -> Path.Combine(dir, f) |]
            OtherOptions =
                Array.concat
                    [ frameworkOptions
                      [| for out, _ in refs -> $"-r:%s{out}" |]
                      [| for r in extraRefs -> $"-r:%s{r}" |] ]
            ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
            UseScriptResolutionRules = false
            ProjectId = None
            Stamp = None }

    let a = project "A" [ "A.fs" ] [] []
    let b = project "B" [ "B1.fs"; "B2.fs" ] [ vendoredDll ] [ aDll, a ]

    let pipeline = CheckPipeline(checker, repoRoot = dir)
    pipeline.RegisterProject(a.ProjectFileName, a)
    pipeline.RegisterProject(b.ProjectFileName, b)

    { Dir = dir
      Checker = checker
      Pipeline = pipeline
      AOptions = a
      BOptions = b
      ADll = aDll
      VendoredDll = vendoredDll
      Checked = checkedFiles }

/// Check B2.fs and return the errors FCS reported for it.
let private checkB2 (tree: Tree) =
    match
        tree.Pipeline.CheckFile(AbsFilePath.create (Path.Combine(tree.Dir, "B2.fs")))
        |> Async.RunSynchronously
    with
    | Some { CheckResults = FullCheck r } ->
        r.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        |> Array.map (fun d -> d.Message)
    | other -> failwith $"expected a full check, got %A{other}"

let private drain (tree: Tree) =
    let files = Collections.Generic.List<string>()
    let mutable item: string = null

    while tree.Checked.TryDequeue(&item) do
        files.Add item

    files |> Seq.toList

/// Rewrite `path` with its own bytes and move its mtime — what a no-op rebuild,
/// a restore or a checkout does to a file whose content did not change.
let private rewriteIdentical (path: string) =
    let bytes = File.ReadAllBytes path
    File.WriteAllBytes(path, bytes)
    File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes 5.0)

/// Warm the checker on B2.fs and return what the first check type-checked.
let private warm (tree: Tree) =
    test <@ checkB2 tree |> Array.isEmpty @>
    let first = drain tree
    // Positive control: the counter sees B's files being checked at all.
    test <@ first |> List.contains "B1.fs" @>
    first

[<Fact(Timeout = 120000)>]
let ``an unchanged re-check type-checks nothing`` () =
    withTempDir "snapshot-baseline" (fun dir ->
        let tree = makeTree dir
        warm tree |> ignore

        checkB2 tree |> ignore
        let second = drain tree
        test <@ not (second |> List.contains "B1.fs") @>)

[<Fact(Timeout = 120000)>]
let ``rewriting a project reference's output dll with identical bytes re-checks nothing downstream`` () =
    withTempDir "snapshot-project-dll" (fun dir ->
        let tree = makeTree dir
        warm tree |> ignore

        rewriteIdentical tree.ADll
        test <@ checkB2 tree |> Array.isEmpty @>
        let after = drain tree
        test <@ not (after |> List.contains "B1.fs") @>)

[<Fact(Timeout = 120000)>]
let ``rewriting a vendored dll with identical bytes re-checks nothing downstream`` () =
    withTempDir "snapshot-vendored-dll" (fun dir ->
        let tree = makeTree dir
        warm tree |> ignore

        rewriteIdentical tree.VendoredDll
        test <@ checkB2 tree |> Array.isEmpty @>
        let after = drain tree
        test <@ not (after |> List.contains "B1.fs") @>)

[<Fact(Timeout = 120000)>]
let ``rewriting an upstream source with identical bytes re-checks nothing`` () =
    withTempDir "snapshot-source-touch" (fun dir ->
        let tree = makeTree dir
        warm tree |> ignore

        rewriteIdentical (Path.Combine(dir, "B1.fs"))
        rewriteIdentical (Path.Combine(dir, "A.fs"))
        test <@ checkB2 tree |> Array.isEmpty @>
        let after = drain tree
        test <@ not (after |> List.contains "B1.fs") @>
        test <@ not (after |> List.contains "A.fs") @>)

[<Fact(Timeout = 120000)>]
let ``editing an upstream source re-checks downstream`` () =
    withTempDir "snapshot-source-edit" (fun dir ->
        let tree = makeTree dir
        warm tree |> ignore

        write dir "A.fs" [| "module A"; "let f (x: string) = x + \"!\"" |]
        test <@ not (checkB2 tree |> Array.isEmpty) @>
        let after = drain tree
        test <@ after |> List.contains "A.fs" @>
        test <@ after |> List.contains "B1.fs" @>)

/// The errors the checker reports for B2.fs through the plain options path, on a
/// fresh checker — FCS's own reading of the tree, to hold the snapshot path to.
let private optionsPathErrorsOfB2 (tree: Tree) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()
    let path = Path.Combine(tree.Dir, "B2.fs")

    match
        checker.ParseAndCheckFileInProject(path, 0, SourceText.ofString (File.ReadAllText path), tree.BOptions)
        |> Async.RunSynchronously
    with
    | _, FSharpCheckFileAnswer.Succeeded r ->
        r.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        |> Array.map (fun d -> d.Message)
    | _, FSharpCheckFileAnswer.Aborted -> failwith "the options-path check aborted"

[<Fact(Timeout = 120000)>]
let ``an upstream dll older than an edited upstream source does not hide the edit`` () =
    // FCS type-checks B against A's dll on disk when the dll is at least as new as
    // A's sources, and against A's sources otherwise. That choice reads the real
    // filesystem, never the snapshot's reference stamps — pinned here so a content
    // stamp can never flip it: after an edit, A's stale dll must not be what B sees.
    withTempDir "snapshot-stale-dll" (fun dir ->
        let tree = makeTree dir
        warm tree |> ignore

        write dir "A.fs" [| "module A"; "let f (x: string) = x + \"!\"" |]
        File.SetLastWriteTimeUtc(tree.ADll, File.GetLastWriteTimeUtc(Path.Combine(dir, "A.fs")).AddHours -1.0)

        let errors = checkB2 tree
        test <@ not (Array.isEmpty errors) @>
        test <@ errors = optionsPathErrorsOfB2 tree @>)

[<Fact(Timeout = 120000)>]
let ``the snapshot path reports what the options path reports`` () =
    withTempDir "snapshot-parity" (fun dir ->
        let tree = makeTree dir
        write dir "B2.fs" [| "module B2"; "let h : string = B1.g 1" |]

        let errors = checkB2 tree
        test <@ not (Array.isEmpty errors) @>
        test <@ errors = optionsPathErrorsOfB2 tree @>)

[<Fact(Timeout = 120000)>]
let ``invalidating a project makes the checker type-check it again`` () =
    withTempDir "snapshot-invalidate" (fun dir ->
        let tree = makeTree dir
        warm tree |> ignore

        FsHotWatch.ProjectSnapshots.invalidate tree.Checker tree.BOptions
        checkB2 tree |> ignore
        test <@ drain tree |> List.contains "B1.fs" @>)

[<Fact(Timeout = 120000)>]
let ``two checkouts with identical content get identical snapshot versions`` () =
    withTempDir "snapshot-checkout-1" (fun dir1 ->
        withTempDir "snapshot-checkout-2" (fun dir2 ->
            let tree1 = makeTree dir1
            let tree2 = makeTree dir2

            // Both checkouts hold the same build of A, as two deterministic builds of
            // one revision do. (FCS's in-process compile is not byte-reproducible
            // across directories, so the fixture shares one build instead.)
            File.Copy(tree1.ADll, tree2.ADll, true)

            // The second checkout was written at another time, as a checkout is.
            for f in Directory.GetFiles(dir2, "*", SearchOption.AllDirectories) do
                File.SetLastWriteTimeUtc(f, File.GetLastWriteTimeUtc(f).AddDays -3.0)

            let versionsOf (tree: Tree) =
                let hasher = FsHotWatch.CheckCache.FileContentHasher()
                let b2 = Path.Combine(tree.Dir, "B2.fs")

                let snapshot =
                    FsHotWatch.ProjectSnapshots.build
                        hasher.Hash
                        (Some tree.Dir)
                        (FsHotWatch.ProjectSnapshots.readOpenFile hasher.Hash b2)
                        tree.BOptions
                    |> Async.RunSynchronously

                let local (path: string) = path.Replace(tree.Dir, "<checkout>")

                [ for f in snapshot.SourceFiles -> local f.FileName, f.Version ],
                [ for r in snapshot.ReferencesOnDisk -> local r.Path, r.LastModified ]

            let files1, refs1 = versionsOf tree1
            let files2, refs2 = versionsOf tree2
            test <@ not (List.isEmpty files1) @>
            test <@ files1 = files2 @>
            test <@ refs1 = refs2 @>))
