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

/// How many times a checker's state for one project has been invalidated. The
/// project's snapshots carry it, so each generation has cache keys of its own.
[<Struct>]
type Generation = Generation of int64

/// The generation a checker is in for each project, by project file.
let private generations =
    System.Runtime.CompilerServices.ConditionalWeakTable<
        FSharpChecker,
        System.Collections.Concurrent.ConcurrentDictionary<string, int64>
     >()

let private generationsOf (checker: FSharpChecker) =
    generations.GetValue(checker, fun _ -> System.Collections.Concurrent.ConcurrentDictionary<string, int64>())

/// The generation `checker` is in for the project `projectFileName`.
let generationOf (checker: FSharpChecker) (projectFileName: string) : Generation =
    match (generationsOf checker).TryGetValue projectFileName with
    | true, n -> Generation n
    | false, _ -> Generation 0L

/// `stamp` as generation `generation` of a project writes it. Generation 0 writes
/// every stamp as it is, so two checkouts of one revision still agree on every
/// version; each later generation derives different stamps from the same ones.
let private inGeneration (Generation n) (stamp: DateTime) =
    if n = 0L then
        stamp
    else
        contentStamp $"%d{stamp.Ticks}@%d{n}"

let private referenceOnDisk
    (hashFile: string -> string)
    (repoRoot: string option)
    (generation: Generation)
    (path: string)
    =
    let stamp =
        match repoRoot with
        | Some _ when CheckCache.isUnderRoot repoRoot path -> contentStamp (hashFile path)
        | _ -> FileSystem.GetLastWriteTimeShim path

    { Path = path
      LastModified = inGeneration generation stamp }

/// The snapshot for checking `openFile` in `options`: `FromOptions`' snapshot, with
/// content versions. `generation` is the generation each project is in (see
/// `generationOf`); `hashFile` is the content hash of one file; `repoRoot`, when
/// present, bounds the references stamped by content.
let build
    (generation: string -> Generation)
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
                         |> List.map (fun r ->
                             referenceOnDisk hashFile repoRoot (generation opts.ProjectFileName) (r.Substring 3))),
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

/// Start a new generation of `checker`'s state for `options`' project.
///
/// Snapshots built afterwards stamp the project's references differently, and the
/// references' stamps are part of the project's version. So the checker type-checks
/// the project again, and every project downstream of it, whose versions include
/// it, under cache keys nothing has used.
///
/// Nothing the checker holds is dropped. `InvalidateConfiguration` removes cache
/// entries that checks already running go on to request: such a check recomputes a
/// removed entry, or takes one another check recomputed, against type-check results
/// it obtained before the removal. Two computations of one file then declare two
/// copies of each of its types, and FCS reports a type as incompatible with itself.
/// The checker keeps one version of each cache entry strongly — per project, and per
/// file of a project — so each entry the new generation computes demotes the previous
/// generation's version of it to a weak reference, which the next collection releases.
///
/// A project with no references on disk has no stamps to carry a generation; every
/// project the daemon loads references at least FSharp.Core.
let invalidate (checker: FSharpChecker) (options: FSharpProjectOptions) : unit =
    (generationsOf checker).AddOrUpdate(options.ProjectFileName, 1L, fun _ n -> n + 1L)
    |> ignore
