/// A project's compiler flags apply to its own checks only (AUTOMATION-1656).
///
/// FCS builds `TcGlobals` with the options of the first project it bootstraps and
/// shares it with every project on the same framework references: its framework-imports
/// key holds the references, the language version and nullness, not `--reflectionfree`.
/// One checker then checked a project that referenced a `--reflectionfree` one as if it
/// set the flag too, and reported `sprintf "%A"` as an error real fsc accepts.
module FsHotWatch.Tests.ProjectFlagIsolationTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// Every flag a test sets, and what it changes in a file that has one of each.
let private source (moduleName: string) (extra: string list) =
    [ $"module %s{moduleName}"
      // FS0025 (a warning): incomplete match.
      "let f (x: int option) = match x with Some v -> v"
      // FS1182 (off unless --warnon:1182): an unused variable.
      "let g () = let unused = 1 in 2"
      // FS0001 only when FLAGGED is defined.
      "#if FLAGGED"
      "let h : int = \"s\""
      "#endif"
      // An error only under --reflectionfree.
      "let s = sprintf \"%A\" 1" ]
    @ extra

/// A worktree holding projects A and B, B referencing A by `-r:` and as an F# project
/// as Ionide.ProjInfo reports it, each with its own `flags`.
type private Tree =
    { ASource: string
      BSource: string
      A: FSharpProjectOptions
      B: FSharpProjectOptions }

let private treeIn (checker: FSharpChecker) (root: string) (aFlags: string list) (bFlags: string list) =
    let aSource = Path.Combine(root, "A.fs")
    let bSource = Path.Combine(root, "B.fs")
    let baseScript = Path.Combine(root, "Base.fsx")
    File.WriteAllLines(baseScript, [| "let placeholder = 0" |])
    File.WriteAllLines(aSource, source "A" [ "let n = 1" ])
    File.WriteAllLines(bSource, source "B" [ "let fromA = A.s"; "let typed : int = A.n" ])

    let baseOptions, _ =
        scriptProjectOptions (checker, baseScript, SourceText.ofString (File.ReadAllText baseScript))
        |> Async.RunSynchronously

    let frameworkOptions =
        baseOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))

    let aDll = Path.Combine(root, "A", "bin", "A.dll")

    let project name (flags: string list) refs =
        { baseOptions with
            ProjectFileName = Path.Combine(root, $"%s{name}.fsproj")
            SourceFiles = [| Path.Combine(root, $"%s{name}.fs") |]
            OtherOptions =
                Array.concat
                    [ frameworkOptions
                      Array.ofList flags
                      [| for out, _ in refs -> $"-r:%s{out}" |] ]
            ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
            UseScriptResolutionRules = false
            ProjectId = None
            Stamp = None }

    let a = project "A" aFlags []

    { ASource = aSource
      BSource = bSource
      A = a
      B = project "B" bFlags [ aDll, a ] }

/// The diagnostics a full check of `file` reports through `pipeline`.
let private diagnosticsOf (pipeline: CheckPipeline) (file: string) =
    match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
    | Some { CheckResults = FullCheck(_, r) } -> r.Diagnostics
    | other -> failwith $"expected a full check, got %A{other}"

/// Which project of the pair declares the flag.
type private Declared =
    /// A, the referenced project.
    | Referenced
    /// B, the referencing project.
    | Referencing

/// A theory's `declared` argument.
let private declaredBy (name: string) =
    match name with
    | "referenced" -> Referenced
    | "referencing" -> Referencing
    | other -> failwith $"unknown project: %s{other}"

/// Check the declaring project first, so its options bootstrap the checker, then the
/// other: the flagged and the unflagged project's diagnostics.
let private checkPair (flags: string list) (declared: Declared) =
    withTempDir "project-flags" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()

        let tree =
            match declared with
            | Referenced -> treeIn checker dir flags []
            | Referencing -> treeIn checker dir [] flags

        let pipeline = CheckPipeline(checker, repoRoot = dir)
        pipeline.RegisterProject(tree.A.ProjectFileName, tree.A)
        pipeline.RegisterProject(tree.B.ProjectFileName, tree.B)

        let flaggedFile, otherFile =
            match declared with
            | Referenced -> tree.ASource, tree.BSource
            | Referencing -> tree.BSource, tree.ASource

        let flagged = diagnosticsOf pipeline flaggedFile
        let other = diagnosticsOf pipeline otherFile
        // And the flagged one again, now that the other has been checked.
        let flaggedAgain = diagnosticsOf pipeline flaggedFile
        flagged, other, flaggedAgain)

let private has (number: int) (severity: FSharpDiagnosticSeverity) (diagnostics: FSharpDiagnostic array) =
    diagnostics
    |> Array.exists (fun d -> d.ErrorNumber = number && d.Severity = severity)

let private reflectionFree (diagnostics: FSharpDiagnostic array) =
    diagnostics |> Array.exists (fun d -> d.Message.Contains "--reflectionfree")

[<Theory(Timeout = 300000)>]
[<InlineData("referenced")>]
[<InlineData("referencing")>]
let ``--reflectionfree applies only to the project that declares it`` (declared: string) =
    let flagged, other, flaggedAgain =
        checkPair [ "--reflectionfree" ] (declaredBy declared)
    // Positive control: the declaring project reports `%A`.
    test <@ reflectionFree flagged @>
    test <@ reflectionFree flaggedAgain @>
    test <@ not (reflectionFree other) @>

[<Theory(Timeout = 300000)>]
[<InlineData("referenced")>]
[<InlineData("referencing")>]
let ``--nowarn applies only to the project that declares it`` (declared: string) =
    let flagged, other, flaggedAgain = checkPair [ "--nowarn:25" ] (declaredBy declared)
    test <@ not (has 25 FSharpDiagnosticSeverity.Warning flagged) @>
    test <@ not (has 25 FSharpDiagnosticSeverity.Warning flaggedAgain) @>
    test <@ has 25 FSharpDiagnosticSeverity.Warning other @>

[<Theory(Timeout = 300000)>]
[<InlineData("referenced")>]
[<InlineData("referencing")>]
let ``--warnon applies only to the project that declares it`` (declared: string) =
    let flagged, other, flaggedAgain =
        checkPair [ "--warnon:1182" ] (declaredBy declared)

    test <@ has 1182 FSharpDiagnosticSeverity.Warning flagged @>
    test <@ has 1182 FSharpDiagnosticSeverity.Warning flaggedAgain @>
    test <@ not (other |> Array.exists (fun d -> d.ErrorNumber = 1182)) @>

[<Theory(Timeout = 300000)>]
[<InlineData("referenced")>]
[<InlineData("referencing")>]
let ``TreatWarningsAsErrors applies only to the project that declares it`` (declared: string) =
    let flagged, other, flaggedAgain =
        checkPair [ "--warnaserror+" ] (declaredBy declared)

    test <@ has 25 FSharpDiagnosticSeverity.Error flagged @>
    test <@ has 25 FSharpDiagnosticSeverity.Error flaggedAgain @>
    test <@ has 25 FSharpDiagnosticSeverity.Warning other @>

[<Theory(Timeout = 300000)>]
[<InlineData("referenced")>]
[<InlineData("referencing")>]
let ``DefineConstants apply only to the project that declares them`` (declared: string) =
    let flagged, other, flaggedAgain =
        checkPair [ "--define:FLAGGED" ] (declaredBy declared)

    test <@ has 1 FSharpDiagnosticSeverity.Error flagged @>
    test <@ has 1 FSharpDiagnosticSeverity.Error flaggedAgain @>
    test <@ not (has 1 FSharpDiagnosticSeverity.Error other) @>

/// Rewrite `path` with `edit` applied to its text, and move its mtime forward so no
/// stamp-keyed read can mistake it for the old content.
let private editFile (path: string) (edit: string -> string) =
    let before = File.GetLastWriteTimeUtc path
    File.WriteAllText(path, edit (File.ReadAllText path))
    File.SetLastWriteTimeUtc(path, before.AddSeconds 5.0)

/// `A.n` turned from an int into a string, and back. Not `A.s`: inside B's check FCS
/// types A under B's `TcGlobals`, so with B `--reflectionfree` `A.s` is typed `obj`
/// (an FCS limitation no checker choice reaches; see the CHANGELOG).
let private nAsString (text: string) =
    text.Replace("let n = 1", "let n = \"1\"")

let private nAsInt (text: string) =
    text.Replace("let n = \"1\"", "let n = 1")

let private typeMismatch (diagnostics: FSharpDiagnostic array) =
    has 1 FSharpDiagnosticSeverity.Error diagnostics

/// A and B registered with `--reflectionfree` on the `declared` one, which is checked
/// first so it holds the family's own checker, and the other project a sibling. Only the
/// FCS call is routed; generations and frames stay under the family's own checker.
let private onTwoCheckers (declared: string) (dir: string) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()

    let tree, flaggedFile, otherFile =
        match declaredBy declared with
        | Referenced ->
            let tree = treeIn checker dir [ "--reflectionfree" ] []
            tree, tree.ASource, tree.BSource
        | Referencing ->
            let tree = treeIn checker dir [] [ "--reflectionfree" ]
            tree, tree.BSource, tree.ASource

    let pipeline = CheckPipeline(checker, repoRoot = dir)
    pipeline.RegisterProject(tree.A.ProjectFileName, tree.A)
    pipeline.RegisterProject(tree.B.ProjectFileName, tree.B)
    diagnosticsOf pipeline flaggedFile |> ignore
    diagnosticsOf pipeline otherFile |> ignore
    // Positive control: the two projects really are checked through two checkers.
    test <@ (FsHotWatch.CheckerPartitions.membersOf checker).Length = 2 @>
    test <@ not (typeMismatch (diagnosticsOf pipeline tree.BSource)) @>
    pipeline, tree

// An edit to A must reach B whichever checker each is checked through:
//   "referenced"  — A (edited) on the family's own checker, B (dependent) on a sibling;
//   "referencing" — A (edited) on a sibling, B (dependent) on the family's own checker.
[<Theory(Timeout = 300000)>]
[<InlineData("referenced")>]
[<InlineData("referencing")>]
let ``an edit to the referenced project reaches its dependent across sibling checkers`` (declared: string) =
    withTempDir "project-flags-edit" (fun dir ->
        let pipeline, tree = onTwoCheckers declared dir
        editFile tree.ASource nAsString
        diagnosticsOf pipeline tree.ASource |> ignore
        test <@ typeMismatch (diagnosticsOf pipeline tree.BSource) @>

        editFile tree.ASource nAsInt
        test <@ not (typeMismatch (diagnosticsOf pipeline tree.BSource)) @>)

[<Theory(Timeout = 300000)>]
[<InlineData("referenced")>]
[<InlineData("referencing")>]
let ``the dependent's next check alone sees an edit across sibling checkers`` (declared: string) =
    withTempDir "project-flags-edit-alone" (fun dir ->
        let pipeline, tree = onTwoCheckers declared dir
        editFile tree.ASource nAsString
        test <@ typeMismatch (diagnosticsOf pipeline tree.BSource) @>)
