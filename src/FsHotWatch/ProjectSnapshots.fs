/// The FCS project snapshots the check pipeline type-checks against, versioned by
/// CONTENT.
///
/// Handed `FSharpProjectOptions`, the TransparentCompiler builds its own snapshot
/// (`FSharpProjectSnapshot.FromOptions`), and that one versions every source file by
/// its last-write time and every `-r:` reference by its path and last-write time.
/// Both versions feed every cache key of the project and — through a project
/// reference's signature version — of every project downstream of it. So a no-op
/// rebuild, a restore or a checkout that rewrites a file with its own bytes discarded
/// the type-check work for everything downstream, and two checkouts of one revision
/// could never agree on a version.
///
/// Here a source file's version is the hash of its bytes, and an in-repository
/// reference's stamp is derived from the hash of its bytes. References outside the
/// repository (the NuGet cache, the SDK) keep their real stamps: their paths are
/// version-qualified and the same in every checkout, and hashing hundreds of them
/// would spend the CPU the checker's caches exist to save.
module FsHotWatch.ProjectSnapshots

// FSharpProjectSnapshot is marked experimental; it is the checker's native input.
#nowarn "57"

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.IO
open FSharp.Compiler.Text

/// The file being checked: the text read for it and the version that text is checked under.
type OpenFile =
    { Path: string
      Version: string
      Text: string }

let private sha256Hex (text: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

/// Read the file about to be checked. A missing file reads as empty.
///
/// Its version is its content hash — the same `hashFile` names it by when another
/// file of the project is checked, so which file is open never changes the
/// project's version. When the file moved while it was read, the hash before and
/// the hash after disagree about what was read, and the text names itself instead.
let readOpenFile (hashFile: string -> string) (path: string) : OpenFile =
    let before = hashFile path

    let text =
        try
            File.ReadAllText path
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> ""

    let version =
        if hashFile path = before then
            before
        else
            $"text:%s{sha256Hex text}"

    { Path = path
      Version = version
      Text = text }

/// A source file other than the open one, versioned by `hashFile`.
///
/// The checker reads it later, when it needs the source. Text that no longer matches
/// the version is refused rather than returned: cached under the old version, it
/// would be served back the next time the file holds the old bytes (an undo).
let private diskFile (hashFile: string -> string) (version: string) (name: string) (path: string) : FSharpFileSnapshot =
    FSharpFileSnapshot.Create(
        name,
        version,
        fun () ->
            let text = File.ReadAllText path

            if hashFile path <> version then
                raise (IOException $"%s{path} changed while it was being checked")

            Task.FromResult(SourceTextNew.ofString text)
    )

/// A reference stamp named by content. The checker does one thing with a reference's
/// `LastModified`: hash it into the project's version. Which of an upstream project's
/// dll and sources it type-checks against is decided from the real filesystem, so
/// this stamp cannot change that choice.
let contentStamp (contentHash: string) : DateTime =
    let digest = SHA256.HashData(Encoding.UTF8.GetBytes contentHash)
    let ticks = BitConverter.ToUInt64(digest, 0) % uint64 (DateTime.MaxValue.Ticks + 1L)
    DateTime(int64 ticks, DateTimeKind.Utc)

let private referenceOnDisk (hashFile: string -> string) (repoRoot: string option) (path: string) =
    let stamp =
        match repoRoot with
        | Some _ when CheckCache.isUnderRoot repoRoot path -> contentStamp (hashFile path)
        | _ -> FileSystem.GetLastWriteTimeShim path

    { Path = path; LastModified = stamp }

/// A snapshot, and the frame its project was checked under.
[<NoComparison; NoEquality>]
type Framed =
    { Snapshot: FSharpProjectSnapshot
      Frame: PathFrame.PathFrame option }

/// A project's snapshot, the frame it was built under, and its closure hash.
[<NoComparison; NoEquality>]
type private Built =
    { Snapshot: FSharpProjectSnapshot
      Frame: PathFrame.PathFrame option
      Closure: string }

/// The snapshot for checking `openFile` in `options`, with content versions, each
/// project under the frame `choose` gives it. `hashFile` is the content hash of one
/// file; `repoRoot`, when present, bounds the references stamped by content.
///
/// A framed project names its project file, sources and output under the virtual root,
/// and reads its sources from the worktree. A reference to another project's output
/// takes that project's frame, since FCS matches the two by exact string; its stamp is
/// derived from that project's closure, so two worktrees whose builds differ in bytes
/// still agree. Every other in-repository reference keeps its real path: FCS opens it.
let buildFramed
    (hashFile: string -> string)
    (repoRoot: string option)
    (choose: PathFrame.FrameChoice)
    (openFile: OpenFile)
    (options: FSharpProjectOptions)
    : Framed =
    let built = Dictionary<FSharpProjectOptions, Built>(HashIdentity.Reference)

    let relative (path: string) =
        match repoRoot with
        | Some root when CheckCache.isUnderRoot repoRoot path -> Path.GetRelativePath(root, path)
        | _ -> path

    let rec snapshotOf (opts: FSharpProjectOptions) : Built =
        match built.TryGetValue opts with
        | true, b -> b
        | false, _ ->
            // Referenced projects first: their closures are part of this one, and their
            // frames name their outputs.
            let referenced =
                opts.ReferencedProjects
                |> List.ofArray
                |> List.map (fun reference ->
                    match reference with
                    | FSharpReferencedProject.FSharpReference(output, project) ->
                        let upstream = snapshotOf project
                        reference.OutputFile, Some(output, upstream)
                    | _ -> reference.OutputFile, None)

            let upstreamByOutput =
                referenced
                |> List.choose (fun (output, upstream) -> upstream |> Option.map (fun u -> output, snd u))
                |> Map.ofList

            let references, otherOptions =
                opts.OtherOptions
                |> Array.partition (fun o -> o.StartsWith("-r:", StringComparison.Ordinal))

            let referencePaths = references |> List.ofArray |> List.map (fun r -> r.Substring 3)

            // Each source is hashed once: the closure and the file's version must
            // describe the same read.
            let sourceVersions =
                opts.SourceFiles
                |> List.ofArray
                |> List.map (fun path ->
                    path,
                    (if path = openFile.Path then
                         openFile.Version
                     else
                         hashFile path))

            let versionOf =
                let byPath = dict sourceVersions
                fun path -> byPath[path]

            let closure =
                let masked (option: string) =
                    repoRoot
                    |> Option.fold (fun (o: string) root -> o.Replace(root, "<root>")) option

                let referencePart path =
                    match Map.tryFind path upstreamByOutput with
                    | Some upstream -> $"project-reference:%s{relative path}:%s{upstream.Closure}"
                    | None ->
                        let stamp = (referenceOnDisk hashFile repoRoot path).LastModified
                        $"reference:%s{relative path}:%d{stamp.Ticks}"

                List.concat
                    [ [ $"project:%s{relative opts.ProjectFileName}" ]
                      sourceVersions
                      |> List.map (fun (path, version) -> $"source:%s{relative path}:%s{version}")
                      otherOptions
                      |> List.ofArray
                      |> List.map (fun option -> "option:" + masked option)
                      referencePaths |> List.map referencePart
                      [ $"flags:%b{opts.IsIncompleteTypeCheckEnvironment}:%b{opts.UseScriptResolutionRules}" ] ]
                |> String.concat "\n"
                |> sha256Hex

            // Framed only when every project it references is: a project checked at its
            // own paths has a different output path in every worktree, and a dependent
            // sharing one virtual identity across worktrees would then be asked for a
            // different version of it by each, evicting the other's work on every check.
            let frame =
                if upstreamByOutput |> Map.forall (fun _ upstream -> upstream.Frame.IsSome) then
                    choose opts closure
                else
                    None

            let name =
                match frame with
                | Some f -> PathFrame.toVirtual f
                | None -> id

            let fileSnapshot path =
                if path = openFile.Path then
                    FSharpFileSnapshot.Create(
                        name path,
                        openFile.Version,
                        fun () -> Task.FromResult(SourceTextNew.ofString openFile.Text)
                    )
                else
                    diskFile hashFile (versionOf path) (name path) path

            let referencesOnDisk =
                referencePaths
                |> List.map (fun path ->
                    match Map.tryFind path upstreamByOutput with
                    | Some upstream ->
                        { Path =
                            (match upstream.Frame with
                             | Some f -> PathFrame.toVirtual f path
                             | None -> path)
                          LastModified = contentStamp upstream.Closure }
                    | None -> referenceOnDisk hashFile repoRoot path)

            let referencedProjects =
                opts.ReferencedProjects
                |> List.ofArray
                |> List.map (function
                    | FSharpReferencedProject.FSharpReference(output, project) ->
                        let upstream = snapshotOf project

                        let output =
                            match upstream.Frame with
                            | Some f -> PathFrame.toVirtual f output
                            | None -> output

                        FSharpReferencedProjectSnapshot.FSharpReference(output, upstream.Snapshot)
                    | FSharpReferencedProject.PEReference(getStamp, reader) ->
                        FSharpReferencedProjectSnapshot.PEReference(getStamp, reader)
                    | FSharpReferencedProject.ILModuleReference(output, getStamp, getReader) ->
                        FSharpReferencedProjectSnapshot.ILModuleReference(output, getStamp, getReader))

            let snapshot =
                FSharpProjectSnapshot.Create(
                    projectFileName = name opts.ProjectFileName,
                    outputFileName = None,
                    projectId = opts.ProjectId,
                    sourceFiles = (opts.SourceFiles |> List.ofArray |> List.map fileSnapshot),
                    referencesOnDisk = referencesOnDisk,
                    otherOptions =
                        (otherOptions
                         |> List.ofArray
                         |> List.map (fun option ->
                             match frame with
                             | Some f -> PathFrame.textToVirtual f option
                             | None -> option)),
                    referencedProjects = referencedProjects,
                    isIncompleteTypeCheckEnvironment = opts.IsIncompleteTypeCheckEnvironment,
                    useScriptResolutionRules = opts.UseScriptResolutionRules,
                    loadTime = opts.LoadTime,
                    unresolvedReferences = opts.UnresolvedReferences,
                    originalLoadReferences = opts.OriginalLoadReferences,
                    stamp = opts.Stamp
                )

            let b =
                { Snapshot = snapshot
                  Frame = frame
                  Closure = closure }

            built[opts] <- b
            b

    let top = snapshotOf options

    { Snapshot = top.Snapshot
      Frame = top.Frame }

/// The snapshot for checking `openFile` in `options`, every project at its own paths.
let build
    (hashFile: string -> string)
    (repoRoot: string option)
    (openFile: OpenFile)
    (options: FSharpProjectOptions)
    : FSharpProjectSnapshot =
    (buildFramed hashFile repoRoot PathFrame.realPaths openFile options).Snapshot

/// Parse and type-check `path` against `snapshot`.
let parseAndCheck
    (checker: FSharpChecker)
    (path: string)
    (snapshot: FSharpProjectSnapshot)
    : Async<FSharpParseFileResults * FSharpCheckFileAnswer> =
    checker.ParseAndCheckFileInProject(path, snapshot)

/// Drop everything the checker holds for `options`' project.
///
/// The options overload of `InvalidateConfiguration` clears nothing on the
/// TransparentCompiler — it forwards to the background compiler the daemon does not
/// use. The snapshot overload clears by the snapshot's identifier, which is the
/// project file and its `-o:` output alone, so the snapshot names those and no files.
let invalidate (checker: FSharpChecker) (options: FSharpProjectOptions) : unit =
    let identity =
        FSharpProjectSnapshot.Create(
            projectFileName = options.ProjectFileName,
            outputFileName = None,
            projectId = options.ProjectId,
            sourceFiles = [],
            referencesOnDisk = [],
            otherOptions =
                (options.OtherOptions
                 |> Array.filter (fun o -> o.StartsWith("-o:", StringComparison.Ordinal))
                 |> List.ofArray),
            referencedProjects = [],
            isIncompleteTypeCheckEnvironment = options.IsIncompleteTypeCheckEnvironment,
            useScriptResolutionRules = options.UseScriptResolutionRules,
            loadTime = options.LoadTime,
            unresolvedReferences = None,
            originalLoadReferences = [],
            stamp = None
        )

    checker.InvalidateConfiguration(identity)
