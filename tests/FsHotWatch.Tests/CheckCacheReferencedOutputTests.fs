module FsHotWatch.Tests.CheckCacheReferencedOutputTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

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

/// Counts the checks a pipeline actually ran: a check served from the cache logs no start.
type private CheckCounter() =
    let mutable started = 0
    member _.Started = started

    interface FsHotWatch.PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()

        member _.Log line =
            if line.StartsWith "check start " then
                started <- started + 1

        member _.SetSummary _ = ()

/// A worktree holding `A.fs` and `B.fs` (`let y : int = A.v`), with B referencing A
/// through `-r:` its output and as an F# project, as Ionide.ProjInfo reports it.
type private Tree =
    { Root: string
      ASource: string
      BSource: string
      ADll: string
      FrameworkOptions: string array
      A: FSharpProjectOptions
      B: FSharpProjectOptions }

let private treeIn (checker: FSharpChecker) (root: string) =
    Directory.CreateDirectory root |> ignore
    let aSource = Path.Combine(root, "A.fs")
    let bSource = Path.Combine(root, "B.fs")
    let baseScript = Path.Combine(root, "Base.fsx")
    File.WriteAllLines(baseScript, [| "let placeholder = 0" |])
    File.WriteAllLines(aSource, [| "module A"; "let v : int = 1" |])
    File.WriteAllLines(bSource, [| "module B"; "let y : int = A.v" |])

    let baseOptions, _ =
        checker.GetProjectOptionsFromScript(
            baseScript,
            SourceText.ofString (File.ReadAllText baseScript),
            assumeDotNetFramework = false
        )
        |> Async.RunSynchronously

    let frameworkOptions =
        baseOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))

    let aDll = Path.Combine(root, "A", "bin", "A.dll")
    Directory.CreateDirectory(Path.GetDirectoryName aDll) |> ignore

    let project name (files: string list) refs =
        { baseOptions with
            ProjectFileName = Path.Combine(root, $"%s{name}.fsproj")
            SourceFiles = [| for f in files -> Path.Combine(root, f) |]
            OtherOptions = Array.append frameworkOptions [| for out, _ in refs -> $"-r:%s{out}" |]
            ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
            UseScriptResolutionRules = false
            ProjectId = None
            Stamp = None }

    let a = project "A" [ "A.fs" ] []

    { Root = root
      ASource = aSource
      BSource = bSource
      ADll = aDll
      FrameworkOptions = frameworkOptions
      A = a
      B = project "B" [ "B.fs" ] [ aDll, a ] }

let private pipelineOver
    (checker: FSharpChecker)
    (tree: Tree)
    (cache: FsHotWatch.CheckCache.ICheckCacheBackend option)
    (frames: FsHotWatch.PathFrame.FrameChoice)
    =
    let counter = CheckCounter()

    let pipeline =
        match cache with
        | Some backend ->
            CheckPipeline(checker, cacheBackend = backend, activity = counter, repoRoot = tree.Root, frames = frames)
        | None -> CheckPipeline(checker, activity = counter, repoRoot = tree.Root, frames = frames)

    pipeline.RegisterProject(tree.A.ProjectFileName, tree.A)
    pipeline.RegisterProject(tree.B.ProjectFileName, tree.B)
    pipeline, counter

/// The errors a full check of `file` reports through `pipeline`.
let private errorsOf (pipeline: CheckPipeline) (file: string) =
    match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
    | Some { CheckResults = FullCheck r } ->
        r.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        |> Array.length
    | other -> failwith $"expected a full check, got %A{other}"

[<Fact(Timeout = 180000)>]
let ``a cached check is not served after the referenced project's dll is rebuilt from unchanged sources`` () =
    // FCS types B against A's on-disk dll whenever that dll is newer than A's sources
    // (TransparentCompiler's ComputeAssemblyData). A restore that keeps old mtimes
    // leaves exactly that state with a dll built from OTHER sources; rebuilding then
    // changes the dll's bytes without touching A's sources. The cached result typed
    // against the old dll must not be served on.
    withTempDir "cache-referenced-dll" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let tree = treeIn checker dir

        // A dll built from sources that are about to be replaced.
        File.WriteAllLines(tree.ASource, [| "module A"; "let v : string = \"s\"" |])
        compile checker tree.FrameworkOptions tree.ADll tree.ASource

        // The sources restored with a preserved, older mtime: the dll stays newest.
        File.WriteAllLines(tree.ASource, [| "module A"; "let v : int = 1" |])
        File.SetLastWriteTimeUtc(tree.ASource, File.GetLastWriteTimeUtc(tree.ADll).AddHours -1.0)

        let cache = FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(100)

        let cached, checks =
            pipelineOver checker tree (Some cache) FsHotWatch.PathFrame.realPaths

        // Positive control: typed against the stale dll, where `v` is a string.
        test <@ errorsOf cached tree.BSource = 1 @>

        // Rebuild A from the sources now on disk. Only the dll's bytes change.
        compile checker tree.FrameworkOptions tree.ADll tree.ASource
        File.SetLastWriteTimeUtc(tree.ADll, File.GetLastWriteTimeUtc(tree.ASource).AddHours 2.0)

        // Control: without a check cache, the checker's answer is clean.
        let uncached, _ = pipelineOver checker tree None FsHotWatch.PathFrame.realPaths
        test <@ errorsOf uncached tree.BSource = 0 @>

        // The entry recorded the old dll's bytes, so it misses once and re-checks...
        let before = checks.Started
        test <@ errorsOf cached tree.BSource = 0 @>
        test <@ checks.Started = before + 1 @>

        // ...and with the dll unchanged since, the next check is served from the cache.
        test <@ errorsOf cached tree.BSource = 0 @>
        test <@ checks.Started = before + 1 @>)

[<Fact(Timeout = 180000)>]
let ``framed worktrees whose referenced dlls differ in bytes share one cache entry`` () =
    // Under a virtual root FCS types the upstream from its sources, never from a dll,
    // so the bytes of each worktree's own build (its PDB path, a stamped revision) are
    // not an input, and a sibling's entry for identical sources is served.
    withTempDir "cache-framed-output" (fun worktrees ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let virtualRoot = Path.Combine(worktrees, "virtual")
        let cache = FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(100)

        let framedPipeline (name: string) (dllBytes: string) =
            let tree = treeIn checker (Path.Combine(worktrees, name))
            File.WriteAllText(tree.ADll, dllBytes)
            let frame = FsHotWatch.PathFrame.create tree.Root virtualRoot

            let pipeline, checks =
                pipelineOver checker tree (Some cache) (fun _ _ -> Some frame)

            tree, pipeline, checks

        let treeA, pipelineA, checksA = framedPipeline "a" "built in a"
        let treeB, pipelineB, checksB = framedPipeline "b" "built in b, differently"

        test <@ errorsOf pipelineA treeA.BSource = 0 @>
        test <@ checksA.Started = 1 @>

        test <@ errorsOf pipelineB treeB.BSource = 0 @>
        test <@ checksB.Started = 0 @>)
