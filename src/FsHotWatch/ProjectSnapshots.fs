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
let private diskFile (hashFile: string -> string) (path: string) : FSharpFileSnapshot =
    let version = hashFile path

    FSharpFileSnapshot.Create(
        path,
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

/// The snapshot for checking `openFile` in `options`: `FromOptions`' snapshot, with
/// content versions. `hashFile` is the content hash of one file; `repoRoot`, when
/// present, bounds the references stamped by content.
let build
    (hashFile: string -> string)
    (repoRoot: string option)
    (openFile: OpenFile)
    (options: FSharpProjectOptions)
    : FSharpProjectSnapshot =
    let built =
        Dictionary<FSharpProjectOptions, FSharpProjectSnapshot>(HashIdentity.Reference)

    let fileSnapshot path =
        if path = openFile.Path then
            FSharpFileSnapshot.Create(
                path,
                openFile.Version,
                fun () -> Task.FromResult(SourceTextNew.ofString openFile.Text)
            )
        else
            diskFile hashFile path

    let rec snapshotOf (opts: FSharpProjectOptions) =
        match built.TryGetValue opts with
        | true, snapshot -> snapshot
        | false, _ ->
            let referencedProjects =
                opts.ReferencedProjects
                |> List.ofArray
                |> List.map (function
                    | FSharpReferencedProject.FSharpReference(output, referenced) ->
                        FSharpReferencedProjectSnapshot.FSharpReference(output, snapshotOf referenced)
                    | FSharpReferencedProject.PEReference(getStamp, reader) ->
                        FSharpReferencedProjectSnapshot.PEReference(getStamp, reader)
                    | FSharpReferencedProject.ILModuleReference(output, getStamp, getReader) ->
                        FSharpReferencedProjectSnapshot.ILModuleReference(output, getStamp, getReader))

            let references, otherOptions =
                opts.OtherOptions
                |> Array.partition (fun o -> o.StartsWith("-r:", StringComparison.Ordinal))

            let snapshot =
                FSharpProjectSnapshot.Create(
                    projectFileName = opts.ProjectFileName,
                    outputFileName = None,
                    projectId = opts.ProjectId,
                    sourceFiles = (opts.SourceFiles |> List.ofArray |> List.map fileSnapshot),
                    referencesOnDisk =
                        (references
                         |> List.ofArray
                         |> List.map (fun r -> referenceOnDisk hashFile repoRoot (r.Substring 3))),
                    otherOptions = List.ofArray otherOptions,
                    referencedProjects = referencedProjects,
                    isIncompleteTypeCheckEnvironment = opts.IsIncompleteTypeCheckEnvironment,
                    useScriptResolutionRules = opts.UseScriptResolutionRules,
                    loadTime = opts.LoadTime,
                    unresolvedReferences = opts.UnresolvedReferences,
                    originalLoadReferences = opts.OriginalLoadReferences,
                    stamp = opts.Stamp
                )

            built[opts] <- snapshot
            snapshot

    snapshotOf options

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
