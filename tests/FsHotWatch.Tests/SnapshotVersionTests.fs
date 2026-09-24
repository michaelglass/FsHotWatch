module FsHotWatch.Tests.SnapshotVersionTests

// Reads FSharpProjectSnapshot, which FCS marks experimental.
#nowarn "57"

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
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

/// A checker event handler that holds the first event `matches` accepts: it signals
/// `reached` and blocks that computation until `release` is set. Every other event
/// passes.
let private holdFirst
    (matches: string -> bool)
    (reached: Threading.ManualResetEventSlim)
    (release: Threading.ManualResetEventSlim)
    =
    let held = ref 0

    fun (file: string, _: FSharpProjectOptions) ->
        if
            matches (Path.GetFileName file)
            && Threading.Interlocked.CompareExchange(&held.contents, 1, 0) = 0
        then
            reached.Set()

            if not (release.Wait(TimeSpan.FromSeconds 60.0)) then
                failwith $"%s{file} was held past its bound"

/// Two checks of one project under way when `invalidate` runs, and the errors each
/// reports.
let private checksAcrossInvalidation (invalidate: FSharpChecker -> FSharpProjectOptions -> unit) =
    withTempDir "snapshot-invalidate-inflight" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()

        // A declares a type and B's signature mentions it; B also depends on S, so a
        // check holding S's type-check has A's result in hand and has not asked for
        // B. C and D each pass an A value to B. D precedes C, so D's check never
        // type-checks C (a check type-checks every file before its own), and E
        // keeps D from being the last file.
        write dir "A.fs" [| "module A"; "type T = { X: int }"; "let make () = { X = 1 }" |]
        write dir "S.fs" [| "module S"; "let s = 1" |]
        write dir "B.fs" [| "module B"; "let f (t: A.T) : A.T = { t with X = t.X + S.s }" |]
        write dir "C.fs" [| "module C"; "let c : A.T = B.f (A.make ())" |]
        write dir "D.fs" [| "module D"; "let d : A.T = B.f (A.make ())" |]
        write dir "E.fs" [| "module E"; "let e = 0" |]
        write dir "Base.fsx" [| "let placeholder = 0" |]

        let baseOptions, _ =
            let script = Path.Combine(dir, "Base.fsx")

            checker.GetProjectOptionsFromScript(
                script,
                SourceText.ofString (File.ReadAllText script),
                assumeDotNetFramework = false
            )
            |> Async.RunSynchronously

        let options =
            { baseOptions with
                ProjectFileName = Path.Combine(dir, "P.fsproj")
                SourceFiles = [| for f in [ "A.fs"; "S.fs"; "B.fs"; "D.fs"; "C.fs"; "E.fs" ] -> Path.Combine(dir, f) |]
                OtherOptions = baseOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))
                UseScriptResolutionRules = false
                ProjectId = None
                Stamp = None }

        let hasher = FsHotWatch.CheckCache.FileContentHasher()

        let check (file: string) (edit: string) =
            let path = Path.Combine(dir, file)
            let read = FsHotWatch.ProjectSnapshots.readOpenFile hasher.Hash path

            let openFile =
                { read with
                    Text = read.Text + edit
                    Version = read.Version + edit }

            let snapshot =
                FsHotWatch.ProjectSnapshots.build
                    (FsHotWatch.ProjectSnapshots.generationOf checker)
                    hasher.Hash
                    (Some dir)
                    openFile
                    options

            async {
                match! FsHotWatch.ProjectSnapshots.parseAndCheck checker path snapshot with
                | _, FSharpCheckFileAnswer.Succeeded results ->
                    return
                        results.Diagnostics
                        |> Array.filter (fun d ->
                            d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
                        |> Array.map (fun d -> d.Message)
                | _, other -> return failwith $"check of %s{file} did not complete: %A{other}"
            }
            |> Async.StartAsTask

        let aChecked = new Threading.ManualResetEventSlim()
        let sReached = new Threading.ManualResetEventSlim()
        let sRelease = new Threading.ManualResetEventSlim()
        let dReached = new Threading.ManualResetEventSlim()
        let dRelease = new Threading.ManualResetEventSlim()

        checker.FileChecked.Add(fun (file, _) ->
            if Path.GetFileName file = "A.fs" then
                aChecked.Set())

        checker.BeforeBackgroundFileCheck.Add(holdFirst ((=) "S.fs") sReached sRelease)
        checker.FileParsed.Add(holdFirst (fun f -> f = "D.fs" && sReached.IsSet) dReached dRelease)

        let bound = TimeSpan.FromSeconds 60.0

        // C's check has A's types and is held before it asks for B.
        let checkC = check "C.fs" ""
        test <@ sReached.Wait bound && aChecked.Wait bound @>

        // D's check starts from the same checker state, and is held while it parses
        // its own text of D.fs, before it asks for any type-check.
        let checkD = check "D.fs" "\n// being edited"
        test <@ dReached.Wait bound @>

        invalidate checker options

        // If the invalidation dropped A, S and B, D's check type-checks all three
        // again and finishes, and C's check then receives D's B, whose signature
        // names the other copy of A.T. If nothing was dropped, D's check waits for
        // the S that C's check holds; the delay bounds that wait.
        dRelease.Set()
        Threading.Tasks.Task.WhenAny(checkD, Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 5.0)).Wait()
        sRelease.Set()

        checkC.Result, checkD.Result)

[<Fact(Timeout = 120000)>]
let ``invalidating a project leaves the checks already under way coherent`` () =
    let errorsOfC, errorsOfD =
        checksAcrossInvalidation FsHotWatch.ProjectSnapshots.invalidate

    test <@ Array.isEmpty errorsOfC @>
    test <@ Array.isEmpty errorsOfD @>

[<Fact(Timeout = 120000)>]
let ``a rediscovery leaves the checks already under way coherent`` () =
    let errorsOfC, errorsOfD =
        checksAcrossInvalidation (fun checker options ->
            FsHotWatch.Daemon.Daemon.dropForRediscovery checker [ options ])

    test <@ Array.isEmpty errorsOfC @>
    test <@ Array.isEmpty errorsOfD @>

/// The compiler unit a check result imported for the referenced project A: FCS's
/// internal `CcuThunk`, which the importing project's bootstrap owns. Framework units
/// are shared between bootstraps, so they would prove nothing here.
let private importsOf (results: FSharpCheckFileResults) : obj =
    let a =
        results.ProjectContext.GetReferencedAssemblies()
        |> List.find (fun assembly -> assembly.SimpleName = "A")

    a.GetType().GetFields(Reflection.BindingFlags.Instance ||| Reflection.BindingFlags.NonPublic)
    |> Array.tryPick (fun field ->
        if field.FieldType.Name = "CcuThunk" then
            Some(field.GetValue a)
        else
            None)
    |> Option.defaultWith (fun () -> failwith "FSharpAssembly holds no CcuThunk")

/// Check B2.fs through the snapshot path and return its imports, weakly, so nothing
/// here keeps the check alive.
[<Runtime.CompilerServices.MethodImpl(Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let private checkB2Imports (tree: Tree) : obj =
    let path = Path.Combine(tree.Dir, "B2.fs")
    let hasher = FsHotWatch.CheckCache.FileContentHasher()

    let snapshot =
        FsHotWatch.ProjectSnapshots.build
            (FsHotWatch.ProjectSnapshots.generationOf tree.Checker)
            hasher.Hash
            (Some tree.Dir)
            (FsHotWatch.ProjectSnapshots.readOpenFile hasher.Hash path)
            tree.BOptions

    match
        FsHotWatch.ProjectSnapshots.parseAndCheck tree.Checker path snapshot
        |> Async.RunSynchronously
    with
    | _, FSharpCheckFileAnswer.Succeeded results -> importsOf results
    | _, other -> failwith $"check of B2.fs did not complete: %A{other}"

/// `checkB2Imports`, held only weakly.
[<Runtime.CompilerServices.MethodImpl(Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let private checkB2ImportsWeakly (tree: Tree) = WeakReference(checkB2Imports tree)

let private collect () =
    for _ in 1..3 do
        GC.Collect()
        GC.WaitForPendingFinalizers()

/// After `supersede` and a check in the new generation, the previous generation's
/// imports are collected while the new generation's are alive.
let private supersededImportsAreCollected (supersede: Tree -> unit) =
    withTempDir "snapshot-generation-reclaim" (fun dir ->
        let tree = makeTree dir
        let before = checkB2ImportsWeakly tree
        supersede tree
        let current = checkB2Imports tree
        collect ()

        test <@ not before.IsAlive @>
        GC.KeepAlive current)

[<Fact(Timeout = 120000)>]
let ``a generation still in use is not released by a collection`` () =
    // Positive control for the two below: the same observation of a generation
    // nothing superseded sees it held.
    withTempDir "snapshot-generation-held" (fun dir ->
        let tree = makeTree dir
        let before = checkB2ImportsWeakly tree
        collect ()
        test <@ before.IsAlive @>)

[<Fact(Timeout = 120000)>]
let ``a project's superseded generation is released by a collection`` () =
    supersededImportsAreCollected (fun tree -> FsHotWatch.ProjectSnapshots.invalidate tree.Checker tree.BOptions)

[<Fact(Timeout = 120000)>]
let ``a rediscovery's superseded generation is released by a collection`` () =
    supersededImportsAreCollected (fun tree ->
        FsHotWatch.Daemon.Daemon.dropForRediscovery tree.Checker [ tree.AOptions; tree.BOptions ])

/// Project R, whose compile items begin with two files MSBuild-style tooling writes
/// under obj/ (assembly attributes granting D its internals, and generated code R's
/// own c.fs uses), and project D, which references R in memory: R has no output on
/// disk, so the checker type-checks R's sources for D.
type private GeneratedTree =
    { Checker: FSharpChecker
      Pipeline: CheckPipeline
      RProject: string
      RB: string
      RC: string
      DX: string }

let private makeGeneratedTree (dir: string) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()
    let rDir = Path.Combine(dir, "R")
    let dDir = Path.Combine(dir, "D")
    let generated = Path.Combine(rDir, "obj", "Debug", "net10.0")
    Directory.CreateDirectory generated |> ignore
    Directory.CreateDirectory dDir |> ignore

    write
        generated
        "R.AssemblyInfo.fs"
        [| "namespace FSharp"
           "[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"D\")>]"
           "do ()" |]

    write generated "R.Generated.fs" [| "module R.Generated"; "let answer = 42" |]
    write rDir "a.fs" [| "module R.A"; "type T = { X: int }"; "let internal secret = 1" |]
    write rDir "b.fs" [| "module R.B"; "let f (t: R.A.T) : R.A.T = { t with X = t.X + 1 }" |]
    write rDir "c.fs" [| "module R.C"; "let g : R.A.T = R.B.f { R.A.X = R.Generated.answer }" |]
    write dDir "x.fs" [| "module D.X"; "let y : R.A.T = R.B.f { R.A.X = R.A.secret }" |]
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

    let rOutput = Path.Combine(rDir, "bin", "R.dll")

    let project (projectFile: string) (output: string) (files: string list) refs =
        { baseOptions with
            ProjectFileName = projectFile
            SourceFiles = Array.ofList files
            OtherOptions =
                Array.concat
                    [ frameworkOptions
                      [| $"-o:%s{output}"; "-a" |]
                      [| for out, _ in refs -> $"-r:%s{out}" |] ]
            ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
            UseScriptResolutionRules = false
            ProjectId = None
            Stamp = None }

    let rProject = Path.Combine(rDir, "R.fsproj")

    let r =
        project
            rProject
            rOutput
            [ Path.Combine(generated, "R.AssemblyInfo.fs")
              Path.Combine(generated, "R.Generated.fs")
              Path.Combine(rDir, "a.fs")
              Path.Combine(rDir, "b.fs")
              Path.Combine(rDir, "c.fs") ]
            []

    let d =
        project
            (Path.Combine(dDir, "D.fsproj"))
            (Path.Combine(dDir, "bin", "D.dll"))
            [ Path.Combine(dDir, "x.fs") ]
            [ rOutput, r ]

    let pipeline = CheckPipeline(checker, repoRoot = dir)
    pipeline.RegisterProject(r.ProjectFileName, r)
    pipeline.RegisterProject(d.ProjectFileName, d)

    { Checker = checker
      Pipeline = pipeline
      RProject = rProject
      RB = Path.Combine(rDir, "b.fs")
      RC = Path.Combine(rDir, "c.fs")
      DX = Path.Combine(dDir, "x.fs") }

/// The errors the pipeline's check of `path` reports. Only the messages leave, so the
/// check's results are not kept alive by the caller.
[<Runtime.CompilerServices.MethodImpl(Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let private errorsOf (tree: GeneratedTree) (path: string) =
    match tree.Pipeline.CheckFile(AbsFilePath.create path) |> Async.RunSynchronously with
    | Some { CheckResults = FullCheck r } ->
        r.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        |> Array.map (fun d -> d.Message)
    | other -> failwith $"expected a full check of %s{path}, got %A{other}"

[<Fact(Timeout = 120000)>]
let ``a project's own check sees the source files generated under obj`` () =
    withTempDir "snapshot-generated-own" (fun dir ->
        let tree = makeGeneratedTree dir
        let errors = errorsOf tree tree.RC
        test <@ Array.isEmpty errors @>)

[<Fact(Timeout = 120000)>]
let ``a project checked for a downstream one is not type-checked again for its own files`` () =
    withTempDir "snapshot-generated-roles" (fun dir ->
        let tree = makeGeneratedTree dir
        let started = Collections.Concurrent.ConcurrentQueue<string>()

        FcsCacheProbe.onJobEvent tree.Checker "TcIntermediate" (fun jobEvent label ->
            if jobEvent = "Started" then
                started.Enqueue label)

        let ofR (label: string) =
            label.EndsWith($"(%s{FcsCacheProbe.projectLabel tree.RProject})", StringComparison.Ordinal)

        let drainR () =
            let labels = Collections.Generic.List<string>()
            let mutable label: string = null

            while started.TryDequeue(&label) do
                if ofR label then
                    labels.Add label

            labels |> Seq.toList

        errorsOf tree tree.RB |> ignore
        // Positive control: the observation sees R's files being type-checked.
        test <@ not (List.isEmpty (drainR ())) @>

        // D reaches R through its references, and uses what R's generated
        // attributes grant it.
        let errorsOfD = errorsOf tree tree.DX
        test <@ Array.isEmpty errorsOfD @>
        drainR () |> ignore

        errorsOf tree tree.RC |> ignore
        let restarted = drainR ()
        test <@ List.isEmpty restarted @>)

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
                        (FsHotWatch.ProjectSnapshots.generationOf tree.Checker)
                        hasher.Hash
                        (Some tree.Dir)
                        (FsHotWatch.ProjectSnapshots.readOpenFile hasher.Hash b2)
                        tree.BOptions

                let local (path: string) = path.Replace(tree.Dir, "<checkout>")

                [ for f in snapshot.SourceFiles -> local f.FileName, f.Version ],
                [ for r in snapshot.ReferencesOnDisk -> local r.Path, r.LastModified ]

            let files1, refs1 = versionsOf tree1
            let files2, refs2 = versionsOf tree2
            test <@ not (List.isEmpty files1) @>
            test <@ files1 = files2 @>
            test <@ refs1 = refs2 @>))

// --- ProjectSnapshots, without a checker ---

/// A `hashFile` that answers `first` once and `later` after that — a file that
/// moves between two looks at it.
let private movingHash (first: string) (later: string) =
    let calls = ref 0

    fun (_: string) ->
        calls.Value <- calls.Value + 1
        if calls.Value = 1 then first else later

/// Every project in the generation a checker starts in.
let private firstGeneration (_: string) =
    FsHotWatch.ProjectSnapshots.Generation 0L

let private openFile path version text : FsHotWatch.ProjectSnapshots.OpenFile =
    { Path = path
      Version = version
      Text = text }

let private readSource (file: FSharp.Compiler.CodeAnalysis.ProjectSnapshot.FSharpFileSnapshot) =
    file.GetSource().Result.GetSubTextString(0, file.GetSource().Result.Length)

[<Fact>]
let ``the open file is versioned by its content hash`` () =
    withTempDir "snapshot-open" (fun dir ->
        let path = Path.Combine(dir, "Open.fs")
        File.WriteAllText(path, "module Open")
        let hasher = FsHotWatch.CheckCache.FileContentHasher()

        let opened = FsHotWatch.ProjectSnapshots.readOpenFile hasher.Hash path
        test <@ opened.Text = "module Open" @>
        test <@ opened.Version = hasher.Hash path @>)

[<Fact>]
let ``a missing open file reads as empty`` () =
    withTempDir "snapshot-open-missing" (fun dir ->
        let path = Path.Combine(dir, "Gone.fs")
        let opened = FsHotWatch.ProjectSnapshots.readOpenFile (fun _ -> "missing") path
        test <@ opened.Text = "" @>
        test <@ opened.Version = "missing" @>)

[<Fact>]
let ``an open file in a missing directory reads as empty`` () =
    withTempDir "snapshot-open-no-dir" (fun dir ->
        let path = Path.Combine(dir, "gone", "Gone.fs")
        let opened = FsHotWatch.ProjectSnapshots.readOpenFile (fun _ -> "missing") path
        test <@ opened.Text = "" @>)

[<Fact>]
let ``an open file that cannot be read for another reason is not read as empty`` () =
    withTempDir "snapshot-open-directory" (fun dir ->
        // A directory where the file should be: not missing, and not readable.
        raises<UnauthorizedAccessException> <@ FsHotWatch.ProjectSnapshots.readOpenFile (fun _ -> "h") dir @>)

[<Fact>]
let ``an open file that moves while it is read is versioned by the text read`` () =
    withTempDir "snapshot-open-moving" (fun dir ->
        let path = Path.Combine(dir, "Moving.fs")
        File.WriteAllText(path, "module Moving")

        let opened =
            FsHotWatch.ProjectSnapshots.readOpenFile (movingHash "before" "after") path

        test <@ opened.Version.StartsWith "text:" @>

        let again =
            FsHotWatch.ProjectSnapshots.readOpenFile (movingHash "before" "after") path

        test <@ again.Version = opened.Version @>)

[<Fact>]
let ``a source read after it moved is refused, not served under its old version`` () =
    withTempDir "snapshot-disk-moving" (fun dir ->
        let openPath = Path.Combine(dir, "Open.fs")
        let other = Path.Combine(dir, "Other.fs")
        File.WriteAllText(other, "module Other")

        let options =
            makeProjectOptions (Path.Combine(dir, "P.fsproj")) [ other; openPath ] []

        let opened = openFile openPath "v" "module Open"

        let snapshot =
            FsHotWatch.ProjectSnapshots.build firstGeneration (movingHash "before" "after") None opened options

        let otherFile = snapshot.SourceFiles |> List.find (fun f -> f.FileName = other)
        test <@ otherFile.Version = "before" @>
        raises<IOException> <@ otherFile.GetSource() @>)

[<Fact>]
let ``sources are read when the checker asks for them`` () =
    withTempDir "snapshot-disk" (fun dir ->
        let openPath = Path.Combine(dir, "Open.fs")
        let other = Path.Combine(dir, "Other.fs")
        File.WriteAllText(other, "module Other")

        let options =
            makeProjectOptions (Path.Combine(dir, "P.fsproj")) [ other; openPath ] []

        let hasher = FsHotWatch.CheckCache.FileContentHasher()

        let opened = openFile openPath "v" "module Open"

        let snapshot =
            FsHotWatch.ProjectSnapshots.build firstGeneration hasher.Hash None opened options

        let texts = [ for f in snapshot.SourceFiles -> f.FileName, readSource f ]
        test <@ texts = [ other, "module Other"; openPath, "module Open" ] @>)

[<Fact>]
let ``only references inside the repository are stamped by content`` () =
    withTempDir "snapshot-refs" (fun dir ->
        withTempDir "snapshot-refs-outside" (fun outside ->
            let inside = Path.Combine(dir, "In.dll")
            let external = Path.Combine(outside, "Out.dll")
            File.WriteAllText(inside, "in")
            File.WriteAllText(external, "out")

            let options =
                makeProjectOptions
                    (Path.Combine(dir, "P.fsproj"))
                    []
                    [ $"-r:%s{inside}"; $"-r:%s{external}"; "--noframework" ]

            let hasher = FsHotWatch.CheckCache.FileContentHasher()

            let opened = openFile (Path.Combine(dir, "Open.fs")) "v" ""

            let stamps repoRoot =
                (FsHotWatch.ProjectSnapshots.build firstGeneration hasher.Hash repoRoot opened options)
                    .ReferencesOnDisk
                |> List.map (fun r -> r.Path, r.LastModified)

            let real path =
                FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem.GetLastWriteTimeShim path

            let byContent path =
                FsHotWatch.ProjectSnapshots.contentStamp (hasher.Hash path)

            test <@ stamps (Some dir) = [ inside, byContent inside; external, real external ] @>
            // With no repository to bound them, every reference keeps FCS's own stamp.
            test <@ stamps None = [ inside, real inside; external, real external ] @>

            let snapshot =
                FsHotWatch.ProjectSnapshots.build firstGeneration hasher.Hash (Some dir) opened options

            test <@ snapshot.OtherOptions = [ "--noframework" ] @>))

[<Fact>]
let ``each generation of a project stamps its references differently`` () =
    withTempDir "snapshot-generations" (fun dir ->
        let reference = Path.Combine(dir, "In.dll")
        File.WriteAllText(reference, "in")
        let project = Path.Combine(dir, "P.fsproj")
        let options = makeProjectOptions project [] [ $"-r:%s{reference}" ]
        let opened = openFile (Path.Combine(dir, "Open.fs")) "v" ""
        let hasher = FsHotWatch.CheckCache.FileContentHasher()

        let stampIn generation =
            (FsHotWatch.ProjectSnapshots.build (fun _ -> generation) hasher.Hash (Some dir) opened options)
                .ReferencesOnDisk
            |> List.map (fun r -> r.LastModified)

        let stamps =
            [ 0L; 1L; 2L ] |> List.map (FsHotWatch.ProjectSnapshots.Generation >> stampIn)

        test <@ stamps.Head = [ FsHotWatch.ProjectSnapshots.contentStamp (hasher.Hash reference) ] @>
        test <@ List.distinct stamps = stamps @>)

[<Fact>]
let ``invalidating a project advances that project's generation only`` () =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()
    let other = FsHotWatch.Daemon.Daemon.createChecker ()
    let p = makeProjectOptions "/repo/P.fsproj" [] []
    let generation = FsHotWatch.ProjectSnapshots.generationOf

    FsHotWatch.ProjectSnapshots.invalidate checker p
    FsHotWatch.ProjectSnapshots.invalidate checker p

    test <@ generation checker p.ProjectFileName = FsHotWatch.ProjectSnapshots.Generation 2L @>
    test <@ generation checker "/repo/Q.fsproj" = FsHotWatch.ProjectSnapshots.Generation 0L @>
    test <@ generation other p.ProjectFileName = FsHotWatch.ProjectSnapshots.Generation 0L @>

[<Fact>]
let ``content stamps are equal exactly when the content hashes are`` () =
    let stamp = FsHotWatch.ProjectSnapshots.contentStamp
    test <@ stamp "abc" = stamp "abc" @>
    test <@ stamp "abc" <> stamp "abd" @>
    test <@ (stamp "abc").Kind = DateTimeKind.Utc @>

[<Fact>]
let ``a project reached twice is snapshotted once, and non-F# references pass through`` () =
    withTempDir "snapshot-graph" (fun dir ->
        let a = makeProjectOptions (Path.Combine(dir, "A.fsproj")) [] []
        let getStamp () = DateTime(2020, 1, 1)

        let ilReference =
            FSharpReferencedProject.ILModuleReference(
                Path.Combine(dir, "IL.dll"),
                getStamp,
                fun () -> failwith "not read"
            )

        let peReference =
            FSharpReferencedProject.PEReference(
                getStamp,
                DelayedILModuleReader(Path.Combine(dir, "PE.dll"), fun _ -> failwith "not read")
            )

        let b =
            { makeProjectOptions (Path.Combine(dir, "B.fsproj")) [] [] with
                ReferencedProjects = [| FSharpReferencedProject.FSharpReference(Path.Combine(dir, "A.dll"), a) |] }

        let c =
            { makeProjectOptions (Path.Combine(dir, "C.fsproj")) [] [] with
                ReferencedProjects =
                    [| FSharpReferencedProject.FSharpReference(Path.Combine(dir, "A.dll"), a)
                       FSharpReferencedProject.FSharpReference(Path.Combine(dir, "B.dll"), b)
                       ilReference
                       peReference |] }

        let opened = openFile (Path.Combine(dir, "Open.fs")) "v" ""

        let snapshot =
            FsHotWatch.ProjectSnapshots.build firstGeneration (fun _ -> "h") None opened c

        let outputs = snapshot.ReferencedProjects |> List.map (fun r -> r.OutputFile)

        test
            <@
                outputs = [ Path.Combine(dir, "A.dll")
                            Path.Combine(dir, "B.dll")
                            Path.Combine(dir, "IL.dll")
                            Path.Combine(dir, "PE.dll") ]
            @>

        let aDirect, aThroughB =
            match snapshot.ReferencedProjects with
            | FSharpReferencedProjectSnapshot.FSharpReference(_, direct) :: FSharpReferencedProjectSnapshot.FSharpReference(_,
                                                                                                                            viaB) :: _ ->
                match viaB.ReferencedProjects with
                | [ FSharpReferencedProjectSnapshot.FSharpReference(_, throughB) ] -> direct, throughB
                | other -> failwith $"unexpected %A{other}"
            | other -> failwith $"unexpected %A{other}"

        test <@ obj.ReferenceEquals(aDirect, aThroughB) @>)
