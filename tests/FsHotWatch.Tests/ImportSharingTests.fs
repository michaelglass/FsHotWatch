/// Which imported assemblies two sessions of a repository host hold once, through one
/// checker, and which they must hold apart.
///
/// FCS imports a referenced assembly into a compiler unit (`CcuThunk`) owned by the
/// importing project's bootstrap. Two sessions hold one import graph for an assembly
/// exactly when their checks hand back the same unit for it.
module FsHotWatch.Tests.ImportSharingTests

#nowarn "57"

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

/// The compiler unit for `simpleName` in a check's results.
let private unitInResults (results: FSharpCheckFileResults) (simpleName: string) =
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

/// The compiler unit `s`'s check of P.fs imported for the assembly `simpleName`, its
/// references placed by `place`. `framed`: under the session's virtual root, or at its
/// own paths.
let private unitPlaced
    (place: ProjectSnapshots.ReferencePlacement)
    (framed: bool)
    (checker: FSharpChecker)
    (hasher: CheckCache.FileContentHasher)
    (s: Session)
    (simpleName: string)
    =
    let frame = if framed then Some s.Frame else None

    let built =
        ProjectSnapshots.buildFramedWith
            (ProjectSnapshots.SnapshotMemo())
            (ProjectSnapshots.generationOf checker)
            hasher.Hash
            (Some s.Root)
            (fun _ _ -> frame)
            place
            (ProjectSnapshots.readOpenFile hasher.Hash s.Source)
            s.Options

    match
        ProjectSnapshots.parseAndCheck checker (PathFrame.nameIn frame s.Source) built.Snapshot
        |> Async.RunSynchronously
    with
    | _, FSharpCheckFileAnswer.Succeeded results -> unitInResults results simpleName
    | _, other -> failwith $"check of P.fs did not complete: %A{other}"

let private unitIn = unitPlaced ProjectSnapshots.inPlace

let private unitOf = unitIn true

/// `s` checked as a hosted session checks it: through its own pipeline, sharing
/// `checker`, the repository's canonical projects and its reference placement.
let private unitThroughPipeline
    (checker: FSharpChecker)
    (canonical: CanonicalProjects.Registry)
    (virtualRoot: string)
    (place: ProjectSnapshots.ReferencePlacement)
    (s: Session)
    (simpleName: string)
    =
    let pipeline =
        CheckPipeline(
            checker,
            repoRoot = s.Root,
            frames = SessionFrames.choice canonical s.Root virtualRoot ignore,
            imports = place
        )

    pipeline.RegisterProject(s.Options.ProjectFileName, s.Options)

    match (pipeline.CheckFile(AbsFilePath.create s.Source) |> Async.RunSynchronously) with
    | Some { CheckResults = FullCheck results } -> unitInResults results simpleName
    | other -> failwith $"check of P.fs did not complete: %A{other |> Option.map _.CheckResults}"

/// Compile `source` into the library `output` (assembly name: its file name) with
/// FCS's own compiler. Deterministic, so one source gives one set of bytes.
let private compile (checker: FSharpChecker) (framework: string array) (output: string) (source: string) =
    Directory.CreateDirectory(Path.GetDirectoryName output) |> ignore
    let sourceFile = Path.ChangeExtension(output, ".fs")
    File.WriteAllText(sourceFile, source)

    let diagnostics, error =
        checker.Compile(
            Array.concat
                [ [| "fsc.exe"
                     $"-o:%s{output}"
                     "-a"
                     "--deterministic+"
                     "--debug-"
                     "--optimize-" |]
                  framework
                  [| sourceFile |] ]
        )
        |> Async.RunSynchronously

    let errors =
        diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

    if Option.isSome error || not (Array.isEmpty errors) then
        failwith $"compiling %s{output} failed: %A{error} %A{errors}"

    output

/// `built` copied into `root`'s lib/ as a worktree vendors it.
let private vendor (root: string) (built: string) =
    let path = Path.Combine(root, "lib", Path.GetFileName built)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.Copy(built, path, true)
    path

/// Two worktrees' sessions of P, each referencing its own copy of a vendored
/// `Vendored.dll` (built from `sourceA` and `sourceB`) and `extraB` in b, with the
/// repository's import store.
[<NoComparison; NoEquality>]
type private VendoredWorld =
    { Checker: FSharpChecker
      Hasher: CheckCache.FileContentHasher
      VirtualRoot: string
      Store: ImportStore.Store
      A: Session
      B: Session }

let private vendoredWorld (dir: string) (sourceA: string) (sourceB: string) (extraB: bool) =
    let checker = Daemon.Daemon.createChecker ()
    let hasher = CheckCache.FileContentHasher()
    let framework = frameworkOptions checker dir
    let virtualRoot = Path.Combine(dir, "virtual")

    let builtA =
        compile checker framework (Path.Combine(dir, "build-a", "Vendored.dll")) sourceA
    // Built once when the sources agree: a build embeds its source's path in the dll.
    let builtB =
        if sourceB = sourceA then
            builtA
        else
            compile checker framework (Path.Combine(dir, "build-b", "Vendored.dll")) sourceB

    let rootA = Path.Combine(dir, "a")
    let rootB = Path.Combine(dir, "b")

    let extra =
        if extraB then
            [ vendor
                  rootB
                  (compile checker framework (Path.Combine(dir, "build-x", "Extra.dll")) "module Extra\nlet e = 1\n") ]
        else
            []

    { Checker = checker
      Hasher = hasher
      VirtualRoot = virtualRoot
      Store = ImportStore.Store(Path.Combine(dir, "state", "imports"), hasher.Hash)
      A = session framework virtualRoot rootA [ vendor rootA builtA ]
      B = session framework virtualRoot rootB (vendor rootB builtB :: extra) }

let private vendoredSource = "module Vendored\nlet v = 1\n"

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

        let unitA =
            unitThroughPipeline checker canonical virtualRoot ProjectSnapshots.inPlace a "Unquote"

        let unitB =
            unitThroughPipeline checker canonical virtualRoot ProjectSnapshots.inPlace b "Unquote"

        test <@ obj.ReferenceEquals(unitA, unitB) @>)

[<Fact(Timeout = 120000)>]
let ``identical vendored bytes at two worktree paths are one import through the store`` () =
    withTempDir "import-sharing-vendored" (fun dir ->
        let w = vendoredWorld dir vendoredSource vendoredSource false
        let place = w.Store.PlaceReference

        let unitA = unitPlaced place true w.Checker w.Hasher w.A "Vendored"
        let unitB = unitPlaced place true w.Checker w.Hasher w.B "Vendored"

        test <@ obj.ReferenceEquals(unitA, unitB) @>)

[<Fact(Timeout = 120000)>]
let ``identical hosted sessions hold one import of a vendored assembly through the store`` () =
    withTempDir "import-sharing-vendored-pipeline" (fun dir ->
        let w = vendoredWorld dir vendoredSource vendoredSource false
        let canonical = CanonicalProjects.Registry()
        let place = w.Store.PlaceReference

        let unitA =
            unitThroughPipeline w.Checker canonical w.VirtualRoot place w.A "Vendored"

        let unitB =
            unitThroughPipeline w.Checker canonical w.VirtualRoot place w.B "Vendored"

        test <@ obj.ReferenceEquals(unitA, unitB) @>)

[<Fact(Timeout = 120000)>]
let ``without the store, identical vendored bytes at two worktree paths are two imports`` () =
    // The control: what the store changes.
    withTempDir "import-sharing-vendored-control" (fun dir ->
        let w = vendoredWorld dir vendoredSource vendoredSource false

        let unitA = unitIn true w.Checker w.Hasher w.A "Vendored"
        let unitB = unitIn true w.Checker w.Hasher w.B "Vendored"

        test <@ not (obj.ReferenceEquals(unitA, unitB)) @>)

[<Fact(Timeout = 120000)>]
let ``vendored assemblies of different bytes are never one import, even under one virtual identity`` () =
    withTempDir "import-sharing-vendored-differs" (fun dir ->
        let w = vendoredWorld dir vendoredSource "module Vendored\nlet v = 2\n" false
        let place = w.Store.PlaceReference

        let unitA = unitPlaced place true w.Checker w.Hasher w.A "Vendored"
        let unitB = unitPlaced place true w.Checker w.Hasher w.B "Vendored"

        test <@ not (obj.ReferenceEquals(unitA, unitB)) @>)

[<Fact(Timeout = 120000)>]
let ``sessions whose reference sets differ hold their own imports, even of identical bytes`` () =
    // FCS 43.12 imports per project bootstrap, keyed by the whole reference set. A
    // checker sharing imports across bootstraps would key each by its closure instead.
    withTempDir "import-sharing-vendored-extra" (fun dir ->
        let w = vendoredWorld dir vendoredSource vendoredSource true
        let place = w.Store.PlaceReference

        let unitA = unitPlaced place true w.Checker w.Hasher w.A "Vendored"
        let unitB = unitPlaced place true w.Checker w.Hasher w.B "Vendored"

        test <@ not (obj.ReferenceEquals(unitA, unitB)) @>)

[<Fact(Timeout = 120000)>]
let ``a second identical session under the virtual root starts no second bootstrap`` () =
    withTempDir "import-sharing-bootstraps-shared" (fun dir ->
        let checker = Daemon.Daemon.createChecker ()
        let hasher = CheckCache.FileContentHasher()
        let framework = frameworkOptions checker dir
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = session framework virtualRoot (Path.Combine(dir, "a")) [ external ]
        let b = session framework virtualRoot (Path.Combine(dir, "b")) [ external ]

        unitOf checker hasher a "Unquote" |> ignore
        let afterA = FcsBootstraps.countsOf checker
        unitOf checker hasher b "Unquote" |> ignore
        let afterB = FcsBootstraps.countsOf checker

        test <@ afterA.Started >= 1L && afterA.Projects = 1 @>
        test <@ afterB = afterA @>)

[<Fact(Timeout = 120000)>]
let ``a second session at its own paths starts a second bootstrap, for a second project`` () =
    // What a host whose sessions do not share would record: one import graph per
    // session.
    withTempDir "import-sharing-bootstraps-apart" (fun dir ->
        let checker = Daemon.Daemon.createChecker ()
        let hasher = CheckCache.FileContentHasher()
        let framework = frameworkOptions checker dir
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = session framework virtualRoot (Path.Combine(dir, "a")) [ external ]
        let b = session framework virtualRoot (Path.Combine(dir, "b")) [ external ]

        unitIn false checker hasher a "Unquote" |> ignore
        let afterA = FcsBootstraps.countsOf checker
        unitIn false checker hasher b "Unquote" |> ignore
        let afterB = FcsBootstraps.countsOf checker

        test <@ afterB.Started > afterA.Started @>
        test <@ afterB.Projects = afterA.Projects + 1 @>)

[<Fact>]
let ``a checker never attached, or none, counts no bootstraps`` () =
    let unattached = FSharpChecker.Create(useTransparentCompiler = true)

    test <@ FcsBootstraps.countsOf unattached = { Started = 0L; Projects = 0 } @>

    test <@ FcsBootstraps.countsOf Unchecked.defaultof<FSharpChecker> = { Started = 0L; Projects = 0 } @>

[<Fact(Timeout = 120000)>]
let ``a hosted check names each reference it read from the store by the worktree path it was given as`` () =
    withTempDir "import-sharing-placed" (fun dir ->
        let w = vendoredWorld dir vendoredSource vendoredSource false
        let canonical = CanonicalProjects.Registry()

        let vendoredOf (s: Session) =
            Path.Combine(s.Root, "lib", "Vendored.dll")

        let placedBy (place: ProjectSnapshots.ReferencePlacement) (s: Session) =
            let pipeline =
                CheckPipeline(
                    w.Checker,
                    repoRoot = s.Root,
                    frames = SessionFrames.choice canonical s.Root w.VirtualRoot ignore,
                    imports = place
                )

            pipeline.RegisterProject(s.Options.ProjectFileName, s.Options)

            match pipeline.CheckFile(AbsFilePath.create s.Source) |> Async.RunSynchronously with
            | Some result -> result.Placed
            | None -> failwith "check of P.fs did not complete"

        let stored = (w.Store.PlaceReference(vendoredOf w.B)).Value

        test <@ placedBy w.Store.PlaceReference w.B = Map.ofList [ stored, vendoredOf w.B ] @>
        test <@ placedBy w.Store.PlaceReference w.A = Map.ofList [ stored, vendoredOf w.A ] @>
        test <@ placedBy ProjectSnapshots.inPlace w.A = Map.empty @>)

[<Fact>]
let ``a checker whose caches cannot be read counts nothing, and attaching none is harmless`` () =
    // The background compiler has no TransparentCompiler caches to read.
    let background = FSharpChecker.Create(useTransparentCompiler = false)
    FcsBootstraps.attach background
    FcsBootstraps.attach Unchecked.defaultof<FSharpChecker>

    test <@ FcsBootstraps.countsOf background = { Started = 0L; Projects = 0 } @>
