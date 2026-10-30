/// A file must never vanish from the symbol graph because of a diagnostic that
/// does not actually break its AST.
///
/// `FSharpParseFileResults.ParseHadErrors` is not that predicate. Under the
/// TransparentCompiler — how FsHotWatch's daemon creates its checker
/// (`FSharpChecker.Create(useTransparentCompiler = true)`) — FCS sets the flag for a file
/// whose ONLY parse diagnostic is INFORMATIONAL, e.g. FS3520 "XML comment is not placed
/// on a valid language element" (severity `Info`); the legacy compiler leaves it unset for
/// the very same file. Refusing such a file is silent and total: it compiles fine but
/// contributes NO symbols, so an edit to it selects NO tests and the gate goes green
/// having run nothing relevant (see `EdgeEmission`).
///
/// These tests therefore drive the REAL configuration (TransparentCompiler +
/// command-line project options); the legacy checker cannot reproduce the bug.
module TestPrune.Tests.ParseDiagnosticSeverityTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer

/// The daemon's checker, exactly: FsHotWatch's `Daemon.create` builds it with
/// `useTransparentCompiler = true`, and that backend is the one that mislabels an
/// informational diagnostic as "parse had errors".
let private transparentChecker =
    FSharpChecker.Create(
        keepAssemblyContents = true,
        keepAllBackgroundResolutions = true,
        parallelReferenceResolution = true,
        useTransparentCompiler = true
    )

/// A scratch directory under the test binary's own folder. Deliberately NOT
/// `Path.GetTempPath()`: on macOS that resolves through a symlink (/var →
/// /private/var) and the TransparentCompiler's snapshot keys files by the exact
/// path string, so a symlinked path makes it fail to find the file at all.
let private scratchDir =
    let unique = Guid.NewGuid().ToString("n")

    let dir =
        Path.Combine(AppContext.BaseDirectory, "parse-diagnostic-severity", unique)

    Directory.CreateDirectory dir |> ignore
    dir

/// Analyze `source` as a real compiled file in a real project, through the same
/// entry point the FsHotWatch plugin calls.
let private analyzeAsProjectFile (source: string) : Result<AnalysisResult, string> =
    let unique = Guid.NewGuid().ToString("n")
    let file = Path.Combine(scratchDir, $"Sample-%s{unique}.fs")
    File.WriteAllText(file, source)

    let systemRefs =
        let runtimeDir = Path.GetDirectoryName(typeof<obj>.Assembly.Location)

        Directory.GetFiles(runtimeDir, "*.dll") |> Array.map (fun dll -> $"-r:%s{dll}")

    let args =
        Array.append [| "--target:library"; "--noframework"; "--simpleresolution"; "--warn:3" |] systemRefs

    let options =
        let fromArgs =
            transparentChecker.GetProjectOptionsFromCommandLineArgs(Path.Combine(scratchDir, "Sample.fsproj"), args)

        { fromArgs with
            SourceFiles = [| file |] }

    analyzeSource transparentChecker file source options "TestProject"
    |> Async.RunSynchronously

/// FS3520 ("XML comment is not placed on a valid language element", severity
/// Info). The doc comment sits between `type X =` and the record body — the exact
/// shape found in FsHotWatch's own `BuildPlugin.fs`.
let private misplacedDocComment =
    """module M

type BuildState =
    /// This doc comment is not placed on a valid language element.
    { LastBuild: int }

let buildState () = { LastBuild = 0 }
"""

let private wellFormed =
    """module M

/// This doc comment IS placed on a valid language element.
type BuildState = { LastBuild: int }

let buildState () = { LastBuild = 0 }
"""

/// A genuine syntax error: the AST really is unusable, so refusing is correct.
let private syntaxError =
    """module M

let broken ( = =
"""

[<Fact>]
let ``a misplaced doc comment does not remove the file from the symbol graph`` () =
    match analyzeAsProjectFile misplacedDocComment with
    | Error msg ->
        failwith
            $"A file whose only parse diagnostic is INFORMATIONAL (FS3520) was refused, so every symbol in it \
               vanished from the impact graph and an edit to it would select no tests. Got: %s{msg}"
    | Ok result ->
        let names = result.Symbols |> List.map _.FullName

        test <@ names |> List.contains "M.buildState" @>

[<Fact>]
let ``a misplaced doc comment yields the same symbols as the well-formed file`` () =
    // The stronger claim: the informational diagnostic changes NOTHING about the
    // extraction. Whatever the well-formed file contributes, the misplaced-comment
    // file contributes too — so selection cannot differ between them.
    let expected =
        match analyzeAsProjectFile wellFormed with
        | Ok r -> r.Symbols |> List.map _.FullName |> List.sort
        | Error msg -> failwith $"the well-formed control file failed to analyze: %s{msg}"

    let actual =
        match analyzeAsProjectFile misplacedDocComment with
        | Ok r -> r.Symbols |> List.map _.FullName |> List.sort
        | Error msg -> failwith $"the misplaced-doc-comment file failed to analyze: %s{msg}"

    test <@ actual = expected @>

[<Fact>]
let ``a genuine syntax error is still refused`` () =
    // The guard is narrowed, not removed. An Error-severity parse diagnostic means
    // the tree is untrustworthy and the file must still be refused — callers treat a
    // refusal as owed work (over-select), never as "nothing to do".
    match analyzeAsProjectFile syntaxError with
    | Ok _ -> failwith "a file with a real syntax error was analyzed as if its AST were trustworthy"
    | Error msg -> test <@ msg.StartsWith("Parse errors:", StringComparison.Ordinal) @>
