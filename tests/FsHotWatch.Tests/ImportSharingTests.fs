/// Which imported assemblies two sessions of a repository host hold once, through one
/// checker, and which they must hold apart.
///
/// FCS imports a referenced assembly into a compiler unit (`CcuThunk`) owned by the
/// importing project's bootstrap. Two sessions hold one import graph for an assembly
/// exactly when their checks hand back the same unit for it.
module FsHotWatch.Tests.ImportSharingTests

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// The framework options of a script, which every project here starts from.
let private frameworkOptions (checker: FSharpChecker) (dir: string) =
    let script = Path.Combine(dir, "Base.fsx")
    File.WriteAllText(script, "let placeholder = 0")

    let options, _ =
        checker.GetProjectOptionsFromScript(
            script,
            SourceText.ofString "let placeholder = 0",
            assumeDotNetFramework = false
        )
        |> Async.RunSynchronously

    options.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))

/// One worktree's project P, referencing `references` on top of the framework.
[<NoComparison; NoEquality>]
type private Session =
    { Root: string
      Source: string
      Options: FSharpProjectOptions
      Frame: PathFrame.PathFrame }

let private session (framework: string array) (virtualRoot: string) (root: string) (references: string list) =
    Directory.CreateDirectory root |> ignore
    let source = Path.Combine(root, "P.fs")
    File.WriteAllText(source, "module P\nlet answer = 42\n")

    let options =
        makeProjectOptions
            (Path.Combine(root, "P.fsproj"))
            [ source ]
            (List.ofArray framework @ [ for r in references -> $"-r:%s{r}" ])

    { Root = root
      Source = source
      Options = options
      Frame = PathFrame.create root virtualRoot }

/// The compiler unit `session`'s check of P.fs imported for the assembly `simpleName`.
/// `framed`: under the session's virtual root, or at its own paths.
let private unitIn
    (framed: bool)
    (checker: FSharpChecker)
    (hasher: CheckCache.FileContentHasher)
    (s: Session)
    (simpleName: string)
    =
    let frame = if framed then Some s.Frame else None

    let built =
        ProjectSnapshots.buildFramed
            (ProjectSnapshots.generationOf checker)
            hasher.Hash
            (Some s.Root)
            (fun _ _ -> frame)
            (ProjectSnapshots.readOpenFile hasher.Hash s.Source)
            s.Options

    match
        ProjectSnapshots.parseAndCheck checker (PathFrame.nameIn frame s.Source) built.Snapshot
        |> Async.RunSynchronously
    with
    | _, FSharpCheckFileAnswer.Succeeded results ->
        let assembly =
            results.ProjectContext.GetReferencedAssemblies()
            |> List.tryFind (fun a -> a.SimpleName = simpleName)
            |> Option.defaultWith (fun () -> failwith $"P references no assembly %s{simpleName}")

        assembly.GetType().GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic)
        |> Array.tryPick (fun field ->
            if field.FieldType.Name = "CcuThunk" then
                Some(field.GetValue assembly)
            else
                None)
        |> Option.defaultWith (fun () -> failwith "FSharpAssembly holds no CcuThunk")
    | _, other -> failwith $"check of P.fs did not complete: %A{other}"

let private unitOf = unitIn true

/// The compiler unit for `simpleName` in a check's results.
let private unitInResults (results: FSharpCheckFileResults) (simpleName: string) =
    let assembly =
        results.ProjectContext.GetReferencedAssemblies()
        |> List.find (fun a -> a.SimpleName = simpleName)

    assembly.GetType().GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic)
    |> Array.pick (fun field ->
        if field.FieldType.Name = "CcuThunk" then
            Some(field.GetValue assembly)
        else
            None)

/// `s` checked as a hosted session checks it: through its own pipeline, sharing
/// `checker` and the repository's canonical projects.
let private unitThroughPipeline
    (checker: FSharpChecker)
    (canonical: CanonicalProjects.Registry)
    (virtualRoot: string)
    (s: Session)
    (simpleName: string)
    =
    let pipeline =
        CheckPipeline(checker, repoRoot = s.Root, frames = SessionFrames.choice canonical s.Root virtualRoot ignore)

    pipeline.RegisterProject(s.Options.ProjectFileName, s.Options)

    match (pipeline.CheckFile(AbsFilePath.create s.Source) |> Async.RunSynchronously) with
    | Some { CheckResults = FullCheck results } -> unitInResults results simpleName
    | other -> failwith $"check of P.fs did not complete: %A{other |> Option.map _.CheckResults}"

/// An assembly outside every worktree, as a NuGet package's is.
let private external = typeof<AssertionFailedException>.Assembly.Location

[<Fact(Timeout = 120000)>]
let ``identical sessions under one virtual root hold one import of an assembly outside the worktrees`` () =
    withTempDir "import-sharing-external" (fun dir ->
        let checker = Daemon.Daemon.createChecker ()
        let hasher = CheckCache.FileContentHasher()
        let framework = frameworkOptions checker dir
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = session framework virtualRoot (Path.Combine(dir, "a")) [ external ]
        let b = session framework virtualRoot (Path.Combine(dir, "b")) [ external ]

        let unitA = unitOf checker hasher a "Unquote"
        let unitB = unitOf checker hasher b "Unquote"

        test <@ obj.ReferenceEquals(unitA, unitB) @>)

[<Fact(Timeout = 120000)>]
let ``sessions at their own paths each hold their own import of an assembly outside the worktrees`` () =
    // Positive control for the probe: two bootstraps, two units.
    withTempDir "import-sharing-control" (fun dir ->
        let checker = Daemon.Daemon.createChecker ()
        let hasher = CheckCache.FileContentHasher()
        let framework = frameworkOptions checker dir
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = session framework virtualRoot (Path.Combine(dir, "a")) [ external ]
        let b = session framework virtualRoot (Path.Combine(dir, "b")) [ external ]

        let unitA = unitIn false checker hasher a "Unquote"
        let unitB = unitIn false checker hasher b "Unquote"

        test <@ not (obj.ReferenceEquals(unitA, unitB)) @>)

[<Fact(Timeout = 120000)>]
let ``identical hosted sessions, each through its own pipeline, hold one import of an assembly outside the worktrees``
    ()
    =
    withTempDir "import-sharing-pipeline" (fun dir ->
        let checker = Daemon.Daemon.createChecker ()
        let canonical = CanonicalProjects.Registry()
        let framework = frameworkOptions checker dir
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = session framework virtualRoot (Path.Combine(dir, "a")) [ external ]
        let b = session framework virtualRoot (Path.Combine(dir, "b")) [ external ]

        let unitA = unitThroughPipeline checker canonical virtualRoot a "Unquote"
        let unitB = unitThroughPipeline checker canonical virtualRoot b "Unquote"

        test <@ obj.ReferenceEquals(unitA, unitB) @>)
