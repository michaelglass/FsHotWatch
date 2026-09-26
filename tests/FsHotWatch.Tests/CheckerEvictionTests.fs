module FsHotWatch.Tests.CheckerEvictionTests

// FS0057: `TransparentCompiler.CacheSizes` and project snapshots are experimental in FCS 43.*.
#nowarn "57"

// The checker caches each file's type-check under a key made of the file's content and
// the content before it — not of the type-check results it was computed from. A cached
// file therefore holds the types of the particular computation of each file above it
// that it was checked against. If an upstream file's entry is released while a
// downstream one is kept, the next check computes the upstream file again, declares
// its types a second time, and folds the kept downstream entry — which names the
// first declaration — into the same environment. The compiler then reports a type as
// incompatible with itself.
//
// The checker releases entries in least-recently-used order, and a check reaches a
// project's files in dependency order, so the files at the top of a project are the
// first to go once the project's files outnumber the entries kept.
//
// The project below is a chain, so the order of every check is fixed: A, then F1..F25
// (each using the one before, F1 using A), then B (a value of A's type), then D and E.
// Checking D computes the 27 files above it. At factor 1 the checker keeps 20 of them,
// so A and the first fillers are released first, and a full collection frees them.
// Checking E then computes A again, keeps B, and asks for B's value as an A.T.

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.Text
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

let private fillers = 25

let private sources =
    [ yield "A.fs", "module A\ntype T = { X: int }\n"
      yield "F1.fs", "module F1\nopen A\nlet x = 1\n"
      for i in 2..fillers do
          yield $"F%d{i}.fs", $"module F%d{i}\nlet x = F%d{i - 1}.x + 1\n"
      yield "B.fs", $"module B\nlet value : A.T = {{ X = F%d{fillers}.x }}\n"
      yield "D.fs", "module D\nlet x = 1\n"
      yield "E.fs", "module E\nlet ok : A.T = B.value\n" ]

/// The error messages of checking `D.fs` and then, after a full collection, `E.fs`,
/// with a checker built from `sizes`.
let private checkAcrossACollection (sizes: TransparentCompiler.CacheSizes) : string list =
    withTempDir "checker-eviction" (fun dir ->
        for name, text in sources do
            File.WriteAllText(Path.Combine(dir, name), text)

        let checker =
            FSharpChecker.Create(useTransparentCompiler = true, transparentCompilerCacheSizes = sizes)

        let script = Path.Combine(dir, "references.fsx")
        File.WriteAllText(script, "")

        let scriptOptions, _ =
            checker.GetProjectOptionsFromScript(script, SourceText.ofString "", assumeDotNetFramework = false)
            |> Async.RunSynchronously

        let options =
            { scriptOptions with
                ProjectFileName = Path.Combine(dir, "Chain.fsproj")
                SourceFiles = [| for name, _ in sources -> Path.Combine(dir, name) |]
                OtherOptions = scriptOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx")) }

        let snapshot =
            FSharpProjectSnapshot.FromOptions(options, DocumentSource.FileSystem)
            |> Async.RunSynchronously

        let errorsOf (name: string) =
            match
                checker.ParseAndCheckFileInProject(Path.Combine(dir, name), snapshot)
                |> Async.RunSynchronously
            with
            | _, FSharpCheckFileAnswer.Succeeded results ->
                results.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
                |> Array.map _.Message
                |> List.ofArray
            | _, FSharpCheckFileAnswer.Aborted -> [ $"%s{name}: check aborted" ]

        let first = errorsOf "D.fs"

        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()

        first @ errorsOf "E.fs")

[<Fact(Timeout = 120000)>]
let ``a checker keeping fewer type-checks than the project has reports a type incompatible with itself`` () =
    // The control: FCS's own sizes at factor 1 keep 20 per-file type-checks for a
    // 27-file prefix. If this stops failing, FCS no longer mixes computations across a
    // release, and `Daemon.checkerCacheSizes` can go back to `CacheSizes.Create`.
    let errors = checkAcrossACollection (TransparentCompiler.CacheSizes.Create 1)

    test <@ errors |> List.exists FcsDiagnosticFilter.isSelfIncompatibleTypeMessage @>

[<Fact(Timeout = 120000)>]
let ``the daemon's checker keeps every file's type-check, so a collection cannot split a type`` () =
    let errors = checkAcrossACollection (Daemon.checkerCacheSizes 1)

    test <@ List.isEmpty errors @>

[<Fact>]
let ``checkerCacheSizes bounds everything the factor bounds except the per-file type-checks`` () =
    for factor in [ 1; 10; Daemon.DefaultCheckerCacheSizeFactor; 1000 ] do
        let sizes = Daemon.checkerCacheSizes factor
        let fcs = TransparentCompiler.CacheSizes.Create factor

        test <@ sizes.TcIntermediateKeepStrongly = Int32.MaxValue @>

        test
            <@
                { sizes with
                    TcIntermediateKeepStrongly = fcs.TcIntermediateKeepStrongly } = fcs
            @>
