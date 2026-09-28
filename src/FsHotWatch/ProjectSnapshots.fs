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
open System.Threading
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
/// this stamp cannot change that choice. It must therefore name what that choice
/// reads: a real path's bytes, or a framed upstream's closure (see `buildFramed`).
let contentStamp (contentHash: string) : DateTime =
    let digest = SHA256.HashData(Encoding.UTF8.GetBytes contentHash)
    let ticks = BitConverter.ToUInt64(digest, 0) % uint64 (DateTime.MaxValue.Ticks + 1L)
    DateTime(int64 ticks, DateTimeKind.Utc)

/// How many times a checker's state for one project has been invalidated. The
/// project's snapshots carry it, so each generation has cache keys of its own.
[<Struct>]
type Generation = Generation of int64

/// The generation a checker is in for each project, by the name the checker knows the
/// project by: its project file, or that file under a virtual root.
let private generations =
    System.Runtime.CompilerServices.ConditionalWeakTable<
        FSharpChecker,
        System.Collections.Concurrent.ConcurrentDictionary<string, int64>
     >()

let private generationsOf (checker: FSharpChecker) =
    generations.GetValue(checker, fun _ -> System.Collections.Concurrent.ConcurrentDictionary<string, int64>())

/// The generation `checker` is in for the project it knows as `projectName`.
let generationOf (checker: FSharpChecker) (projectName: string) : Generation =
    match (generationsOf checker).TryGetValue projectName with
    | true, n -> Generation n
    | false, _ -> Generation 0L

/// For each project file, the virtual names a checker has checked the project under.
let private aliases =
    System.Runtime.CompilerServices.ConditionalWeakTable<
        FSharpChecker,
        System.Collections.Concurrent.ConcurrentDictionary<string, Set<string>>
     >()

let private aliasesOf (checker: FSharpChecker) =
    aliases.GetValue(checker, fun _ -> System.Collections.Concurrent.ConcurrentDictionary<string, Set<string>>())

/// Record that `checker` checked the project `projectFileName` under the virtual name
/// `virtualName`, so `invalidateShared` reaches the entries it shares there.
let recordFrame (checker: FSharpChecker) (projectFileName: string) (virtualName: string) : unit =
    (aliasesOf checker)
        .AddOrUpdate(projectFileName, Set.singleton virtualName, fun _ names -> Set.add virtualName names)
    |> ignore

/// `stamp` as generation `generation` of a project writes it. Generation 0 writes
/// every stamp as it is, so two checkouts of one revision still agree on every
/// version; each later generation derives different stamps from the same ones.
let private inGeneration (Generation n) (stamp: DateTime) =
    if n = 0L then
        stamp
    else
        contentStamp $"%d{stamp.Ticks}@%d{n}"

let private referenceOnDisk (hashFile: string -> string) (repoRoot: string option) (path: string) =
    let stamp =
        match repoRoot with
        | Some _ when CheckCache.isUnderRoot repoRoot path -> contentStamp (hashFile path)
        | _ -> FileSystem.GetLastWriteTimeShim path

    { Path = path; LastModified = stamp }

/// A snapshot, the frame its project was checked under, and the real-path project
/// outputs anywhere in its reference tree: the ones FCS may type against, since it
/// reads such an output whenever it is at least as new as its project's sources.
/// `SnapshotKey` is `snapshotKey Snapshot`, computed once per project build.
[<NoComparison; NoEquality>]
type Framed =
    { Snapshot: FSharpProjectSnapshot
      Frame: PathFrame.PathFrame option
      RealProjectOutputs: string list
      SnapshotKey: string }

/// A project's snapshot, the frame it was built under, its closure hash, and the parts
/// the snapshot was created from, so the file being checked can be swapped in.
[<NoComparison; NoEquality>]
type internal Built =
    {
        Snapshot: FSharpProjectSnapshot
        Frame: PathFrame.PathFrame option
        Closure: string
        RealProjectOutputs: string list
        /// The name FCS knows a path of this project by: virtual when framed.
        Name: string -> string
        /// One per source file, in `SourceFiles` order.
        Files: FSharpFileSnapshot list
        ReferencesOnDisk: ReferenceOnDisk list
        OtherOptions: string list
        ReferencedProjects: FSharpReferencedProjectSnapshot list
        Key: Lazy<string>
    }

/// A short name for what `snapshot` asks FCS to type-check: its project, its source
/// versions and its references' stamps — the parts an edit or a new generation
/// changes. For the log: two checks naming the same key ask FCS for the same
/// project type-check, which it may compute once and hand to both.
let snapshotKey (snapshot: FSharpProjectSnapshot) : string =
    let parts =
        seq {
            yield snapshot.ProjectFileName

            for file in snapshot.SourceFiles do
                yield $"%s{file.FileName}@%s{file.Version}"

            for reference in snapshot.ReferencesOnDisk do
                yield $"%s{reference.Path}@%d{reference.LastModified.Ticks}"
        }

    let digest = SHA256.HashData(Encoding.UTF8.GetBytes(String.Join("\n", parts)))
    Convert.ToHexString(digest, 0, 6).ToLowerInvariant()

/// Everything a project's `Built` is derived from besides its options, which key the
/// memo: its sources' versions, each `-r:` reference's on-disk stamp (`None` for a
/// reference to a framed upstream, whose stamp is that upstream's closure), the
/// upstream builds (by identity: an upstream rebuilt is a different object), and the
/// frame and generation it was built under.
[<NoComparison; NoEquality>]
type internal Inputs =
    { SourceVersions: string array
      ReferenceStamps: DateTime option array
      Upstreams: Built list
      Frame: PathFrame.PathFrame option
      Generation: Generation }

/// One project's memo entry, locked while it is validated or rebuilt.
[<AllowNullLiteral>]
type internal MemoSlot() =
    member val Entry: (Inputs * Built) option = None with get, set

/// The project snapshots a checker's checks share.
///
/// Every file check used to build the snapshot of its project and of every project
/// upstream of it afresh: hashing a closure string over each project's sources,
/// options and references, and creating a new snapshot for each. On a cold scan that
/// is O(files × projects in the tree) work and garbage, and with every file of a tier
/// checked at once it became an allocation storm (a 30 GB heap of garbage on a
/// 2,000-file repository). A project's snapshot depends only on its inputs, so it is
/// built once per distinct inputs and reused by every check that asks for it.
///
/// Keyed by the options object: a new project model registers new options, so each
/// model generation has entries of its own, and the old ones go with the old options.
/// An entry is reused only while every input still matches, re-read on every check —
/// the memo can make a check cheaper, never staler.
type SnapshotMemo() =
    let slots =
        System.Runtime.CompilerServices.ConditionalWeakTable<FSharpProjectOptions, MemoSlot>()

    let mutable builds = 0L

    /// How many project snapshots were built, as opposed to reused.
    member _.Builds: int64 = Interlocked.Read &builds

    member internal _.SlotFor(options: FSharpProjectOptions) : MemoSlot =
        slots.GetValue(options, fun _ -> MemoSlot())

    member internal _.CountBuild() = Interlocked.Increment &builds |> ignore

/// A project's reference, resolved for one build: an F# project upstream of it and the
/// output it is referenced by, or any other reference, as its snapshot.
[<NoComparison; NoEquality>]
type private Resolved =
    | Upstream of output: string * Built
    | Other of FSharpReferencedProjectSnapshot

/// How one call reaches the snapshots of the projects it needs.
[<NoComparison; NoEquality>]
type private Store =
    /// Shared across checks: validated against its inputs, rebuilt when they moved.
    | Shared of SnapshotMemo
    /// This call's own, so a diamond in the reference graph is built once.
    | Local of Dictionary<FSharpProjectOptions, Built>

/// Build (or reuse, per `store`) the snapshot of `options` and of every project it
/// references. `versionOf` is each source's version and `fileSnapshot` the file
/// snapshot for a source under its version and name.
let private buildTree
    (store: Store)
    (generation: string -> Generation)
    (hashFile: string -> string)
    (repoRoot: string option)
    (choose: PathFrame.FrameChoice)
    (versionOf: string -> string)
    (fileSnapshot: string -> string -> string -> FSharpFileSnapshot)
    (options: FSharpProjectOptions)
    : Built =
    let relative (path: string) =
        match repoRoot with
        | Some root when CheckCache.isUnderRoot repoRoot path -> Path.GetRelativePath(root, path)
        | _ -> path

    let rec snapshotOf (opts: FSharpProjectOptions) : Built =
        match store with
        | Local built ->
            match built.TryGetValue opts with
            | true, b -> b
            | false, _ ->
                let b = compute opts None
                built[opts] <- b
                b
        | Shared memo ->
            let slot = memo.SlotFor opts
            // Held while this project's upstreams are resolved, which lock theirs: the
            // reference graph is acyclic, so locks are only ever taken downstream-first.
            Monitor.Enter slot

            try
                let prior = slot.Entry
                let b = compute opts prior

                match prior with
                | Some(_, previous) when obj.ReferenceEquals(previous, b) -> ()
                | _ -> memo.CountBuild()

                b
            finally
                Monitor.Exit slot

    /// `previous`, when its inputs are this call's; otherwise a new build, recorded in
    /// the slot when there is one.
    and compute (opts: FSharpProjectOptions) (previous: (Inputs * Built) option) : Built =
        // Referenced projects first: their closures are part of this one, and their
        // frames name their outputs.
        let referenced =
            opts.ReferencedProjects
            |> List.ofArray
            |> List.map (function
                | FSharpReferencedProject.FSharpReference(output, project) -> Upstream(output, snapshotOf project)
                | FSharpReferencedProject.PEReference(getStamp, reader) ->
                    Other(FSharpReferencedProjectSnapshot.PEReference(getStamp, reader))
                | FSharpReferencedProject.ILModuleReference(output, getStamp, getReader) ->
                    Other(FSharpReferencedProjectSnapshot.ILModuleReference(output, getStamp, getReader)))

        let upstreamPairs =
            referenced
            |> List.choose (function
                | Upstream(output, upstream) -> Some(output, upstream)
                | Other _ -> None)

        let upstreams = upstreamPairs |> List.map snd
        let upstreamByOutput = Map.ofList upstreamPairs

        let references, otherOptions =
            opts.OtherOptions
            |> Array.partition (fun o -> o.StartsWith("-r:", StringComparison.Ordinal))

        let referencePaths = references |> Array.map (fun r -> r.Substring 3)

        // Each source is hashed once: the closure and the file's version must
        // describe the same read.
        let sourceVersions = opts.SourceFiles |> Array.map versionOf

        // Each on-disk reference is stamped once, for the closure and the snapshot.
        let referenceStamps =
            referencePaths
            |> Array.map (fun path ->
                match Map.tryFind path upstreamByOutput with
                | Some { Frame = Some _ } -> None
                | Some { Frame = None }
                | None -> Some (referenceOnDisk hashFile repoRoot path).LastModified)

        // The memo is keyed by the options object, so the source and upstream lists are
        // the same length as the entry's: only their contents can differ.
        let sameAs (inputs: Inputs) =
            Array.forall2 (fun (a: string) b -> String.Equals(a, b)) inputs.SourceVersions sourceVersions
            && inputs.ReferenceStamps = referenceStamps
            && List.forall2 (fun a b -> obj.ReferenceEquals(a, b)) inputs.Upstreams upstreams

        let closure =
            match previous with
            | Some(inputs, b) when sameAs inputs -> b.Closure
            | _ ->
                let masked (option: string) =
                    repoRoot
                    |> Option.fold (fun (o: string) root -> o.Replace(root, "<root>")) option

                // A path with no upstream always has an on-disk stamp (`referenceStamps`).
                let referencePart (path: string) (stamp: DateTime option) =
                    match Map.tryFind path upstreamByOutput with
                    | Some upstream -> $"project-reference:%s{relative path}:%s{upstream.Closure}"
                    | None -> $"reference:%s{relative path}:%d{stamp.Value.Ticks}"

                List.concat
                    [ [ $"project:%s{relative opts.ProjectFileName}" ]
                      Array.map2
                          (fun path version -> $"source:%s{relative path}:%s{version}")
                          opts.SourceFiles
                          sourceVersions
                      |> List.ofArray
                      otherOptions
                      |> List.ofArray
                      |> List.map (fun option -> "option:" + masked option)
                      Array.map2 referencePart referencePaths referenceStamps |> List.ofArray
                      [ $"flags:%b{opts.IsIncompleteTypeCheckEnvironment}:%b{opts.UseScriptResolutionRules}" ] ]
                |> String.concat "\n"
                |> sha256Hex

        // Framed only when every project it references is: a project checked at its
        // own paths has a different output path in every worktree, and a dependent
        // sharing one virtual identity across worktrees would then be asked for a
        // different version of it by each, evicting the other's work on every check.
        // Asked on every check, reused or not: the choice is the caller's to make.
        let frame =
            if upstreamByOutput |> Map.forall (fun _ upstream -> upstream.Frame.IsSome) then
                choose opts closure
            else
                None

        let name =
            match frame with
            | Some f -> PathFrame.toVirtual f
            | None -> id

        let projectGeneration = generation (name opts.ProjectFileName)

        match previous with
        | Some(inputs, b) when sameAs inputs && inputs.Frame = frame && inputs.Generation = projectGeneration -> b
        | _ ->
            // A framed upstream's references are framed too, so its tree has none.
            let realProjectOutputs =
                upstreamPairs
                |> List.collect (fun (output, upstream) ->
                    match upstream.Frame with
                    | None -> output :: upstream.RealProjectOutputs
                    | Some _ -> [])
                |> List.distinct

            let inThisGeneration = inGeneration projectGeneration

            let referencesOnDisk =
                Array.map2
                    (fun (path: string) (stamp: DateTime option) ->
                        let reference =
                            match Map.tryFind path upstreamByOutput with
                            // Under a virtual root the output path never exists, so FCS types
                            // the upstream from its snapshot: the closure is what it read.
                            | Some({ Frame = Some f } as upstream) ->
                                { Path = PathFrame.toVirtual f path
                                  LastModified = contentStamp upstream.Closure }
                            // At a real path FCS types against the output whenever it is at
                            // least as new as the upstream's sources: its bytes are an input,
                            // stamped in `referenceStamps`.
                            | Some { Frame = None }
                            | None ->
                                { Path = path
                                  LastModified = stamp.Value }

                        { reference with
                            LastModified = inThisGeneration reference.LastModified })
                    referencePaths
                    referenceStamps
                |> List.ofArray

            let referencedProjects =
                referenced
                |> List.map (function
                    | Upstream(output, upstream) ->
                        FSharpReferencedProjectSnapshot.FSharpReference(
                            PathFrame.nameIn upstream.Frame output,
                            upstream.Snapshot
                        )
                    | Other snapshot -> snapshot)

            let files =
                Array.map2 (fun path version -> fileSnapshot path version (name path)) opts.SourceFiles sourceVersions
                |> List.ofArray

            let virtualOptions =
                otherOptions
                |> List.ofArray
                |> List.map (fun option ->
                    match frame with
                    | Some f -> PathFrame.textToVirtual f option
                    | None -> option)

            let snapshot =
                FSharpProjectSnapshot.Create(
                    projectFileName = name opts.ProjectFileName,
                    outputFileName = None,
                    projectId = opts.ProjectId,
                    sourceFiles = files,
                    referencesOnDisk = referencesOnDisk,
                    otherOptions = virtualOptions,
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
                  Closure = closure
                  RealProjectOutputs = realProjectOutputs
                  Name = name
                  Files = files
                  ReferencesOnDisk = referencesOnDisk
                  OtherOptions = virtualOptions
                  ReferencedProjects = referencedProjects
                  Key = lazy (snapshotKey snapshot) }

            match store with
            | Shared memo ->
                (memo.SlotFor opts).Entry <-
                    Some(
                        { SourceVersions = sourceVersions
                          ReferenceStamps = referenceStamps
                          Upstreams = upstreams
                          Frame = frame
                          Generation = projectGeneration },
                        b
                    )
            | Local _ -> ()

            b

    snapshotOf options

/// The file snapshot of the file being checked: the text read for it.
let private openFileSnapshot (openFile: OpenFile) (name: string) =
    FSharpFileSnapshot.Create(name, openFile.Version, fun () -> Task.FromResult(SourceTextNew.ofString openFile.Text))

/// `built`'s snapshot with the file being checked served from the text read for it.
/// Its version is unchanged, so FCS is asked the identical question.
let private withOpenFile (openFile: OpenFile) (options: FSharpProjectOptions) (built: Built) : FSharpProjectSnapshot =
    match options.SourceFiles |> Array.tryFindIndex (fun path -> path = openFile.Path) with
    | None -> built.Snapshot
    | Some index ->
        FSharpProjectSnapshot.Create(
            projectFileName = built.Snapshot.ProjectFileName,
            outputFileName = None,
            projectId = options.ProjectId,
            sourceFiles =
                (built.Files
                 |> List.mapi (fun i file ->
                     if i = index then
                         openFileSnapshot openFile (built.Name openFile.Path)
                     else
                         file)),
            referencesOnDisk = built.ReferencesOnDisk,
            otherOptions = built.OtherOptions,
            referencedProjects = built.ReferencedProjects,
            isIncompleteTypeCheckEnvironment = options.IsIncompleteTypeCheckEnvironment,
            useScriptResolutionRules = options.UseScriptResolutionRules,
            loadTime = options.LoadTime,
            unresolvedReferences = options.UnresolvedReferences,
            originalLoadReferences = options.OriginalLoadReferences,
            stamp = options.Stamp
        )

/// The snapshot for checking `openFile` in `options`, with content versions, each
/// project under the frame `choose` gives it, its projects' snapshots shared through
/// `memo` with every other check. `generation` is the generation each project is in
/// (see `generationOf`); `hashFile` is the content hash of one file; `repoRoot`, when
/// present, bounds the references stamped by content.
///
/// A framed project names its project file, sources and output under the virtual root,
/// and reads its sources from the worktree. A reference to a framed project's output
/// takes that project's frame, since FCS matches the two by exact string. Nothing exists
/// at that virtual path, so FCS types the upstream from its snapshot, never from a dll,
/// and the stamp is derived from the upstream's closure: two worktrees whose builds
/// differ in bytes still agree. Every other in-repository reference, an unframed
/// project's output included, keeps its real path and is stamped by its bytes: FCS
/// opens it, and types against it whenever it is at least as new as the upstream's
/// sources (FCS 43.12.401, TransparentCompiler `ComputeAssemblyData`). See ADR-037.
///
/// A project's generation is looked up by the name the checker knows it by (virtual
/// when framed), and reaches only its reference stamps, never its closure, so a new
/// generation gives the project new cache keys under the same frame.
///
/// Every project is built from its sources as `hashFile` versions them, which is also
/// the open file's version unless it moved while it was read; then the open file's
/// version differs, and the tree is built for this check alone, as it reads.
let buildFramedWith
    (memo: SnapshotMemo)
    (generation: string -> Generation)
    (hashFile: string -> string)
    (repoRoot: string option)
    (choose: PathFrame.FrameChoice)
    (openFile: OpenFile)
    (options: FSharpProjectOptions)
    : Framed =
    let diskSnapshot path version name = diskFile hashFile version name path

    if hashFile openFile.Path = openFile.Version then
        let top =
            buildTree (Shared memo) generation hashFile repoRoot choose hashFile diskSnapshot options

        { Snapshot = withOpenFile openFile options top
          Frame = top.Frame
          RealProjectOutputs = top.RealProjectOutputs
          SnapshotKey = top.Key.Value }
    else
        let versionOf path =
            if path = openFile.Path then
                openFile.Version
            else
                hashFile path

        let fileSnapshot path version name =
            if path = openFile.Path then
                openFileSnapshot openFile name
            else
                diskSnapshot path version name

        let top =
            buildTree
                (Local(Dictionary HashIdentity.Reference))
                generation
                hashFile
                repoRoot
                choose
                versionOf
                fileSnapshot
                options

        { Snapshot = top.Snapshot
          Frame = top.Frame
          RealProjectOutputs = top.RealProjectOutputs
          SnapshotKey = top.Key.Value }

/// `buildFramedWith`, sharing nothing with any other call.
let buildFramed
    (generation: string -> Generation)
    (hashFile: string -> string)
    (repoRoot: string option)
    (choose: PathFrame.FrameChoice)
    (openFile: OpenFile)
    (options: FSharpProjectOptions)
    : Framed =
    buildFramedWith (SnapshotMemo()) generation hashFile repoRoot choose openFile options

/// The snapshot for checking `openFile` in `options`, every project at its own paths.
let build
    (generation: string -> Generation)
    (hashFile: string -> string)
    (repoRoot: string option)
    (openFile: OpenFile)
    (options: FSharpProjectOptions)
    : FSharpProjectSnapshot =
    (buildFramed generation hashFile repoRoot PathFrame.realPaths openFile options).Snapshot

/// Parse and type-check `path` against `snapshot`.
let parseAndCheck
    (checker: FSharpChecker)
    (path: string)
    (snapshot: FSharpProjectSnapshot)
    : Async<FSharpParseFileResults * FSharpCheckFileAnswer> =
    checker.ParseAndCheckFileInProject(path, snapshot)

let private advance (checker: FSharpChecker) (name: string) =
    (generationsOf checker).AddOrUpdate(name, 1L, fun _ n -> n + 1L) |> ignore

/// Start a new generation of `checker`'s state for `options`' project, as this session
/// knows it at its own paths.
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
/// A project checked under a virtual root keeps its generation there: its key there
/// already carries its content and its references' stamps, so a change of this
/// session's own model reaches it without a new generation, and sibling sessions
/// sharing it are not made to type-check it again for nothing. See `invalidateShared`.
///
/// A project with no references on disk has no stamps to carry a generation; every
/// project the daemon loads references at least FSharp.Core.
let invalidate (checker: FSharpChecker) (options: FSharpProjectOptions) : unit = advance checker options.ProjectFileName

/// `invalidate`, reaching also every virtual name `checker` has checked the project
/// under (`recordFrame`). A suspect entry under a shared virtual identity is read by
/// every session sharing it, so each of them moves to the new generation together;
/// advancing one session alone would leave two versions of the project under one key,
/// each demoting the other on every check.
let invalidateShared (checker: FSharpChecker) (options: FSharpProjectOptions) : unit =
    advance checker options.ProjectFileName

    match (aliasesOf checker).TryGetValue options.ProjectFileName with
    | true, names -> names |> Set.iter (advance checker)
    | false, _ -> ()
