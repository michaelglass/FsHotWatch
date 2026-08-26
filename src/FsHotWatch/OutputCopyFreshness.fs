/// The COPY half of build-output freshness, in core so both plugins can ask it.
///
/// A build does two separable things. It COMPILES each project's sources into that
/// project's own assembly — the question `BuildPlugin.examineArtifact` already asks —
/// and it COPIES each dependency's assembly into every consumer's output directory.
/// Only the second one explains the wedge this module exists for: the build reports
/// `built N projects` (replayed from cache, so nothing ran), and the copy under a test
/// project's output still holds the previous tree's bytes.
///
/// This lived only in `FsHotWatch.TestPrune.ArtifactFreshness`, which `FsHotWatch.Build`
/// cannot see — they are siblings over core, and AUTOMATION-245 named that as the reason
/// its own acceptance could not be met. Hoisting the RULE (not TestPrune's `RunnerTarget`,
/// not its `.fsproj` closure walk) is what makes it reachable from both, with ONE
/// implementation rather than two that have to agree.
///
/// TWO QUESTIONS, NOT ONE, and the difference decides what a caller may do about it:
///
///   * `isPending` — would MSBuild's own incremental copy still have work to do here?
///     Pure `stat`: a copy is pending iff it matches NO candidate origin on BOTH size
///     and mtime, which is exactly `Copy`'s `SkipUnchangedFiles` predicate. A build
///     therefore PROVABLY clears it, so refusing a cache replay over it terminates.
///     Measured: after a successful build every copy carries its origin's exact size and
///     mtime (`File.Copy` propagates the timestamp), and 37 of 37 dependency copies in
///     one consuming repo's tree matched on both.
///
///   * `verdict` — do the BYTES match? The honest question, and the expensive one (it
///     reads both files). It is NOT a substitute for `isPending`, because a copy that is
///     byte-divergent while matching its origin on size and mtime is a copy MSBuild will
///     skip forever: measured, a plain `dotnet build` leaves it exactly as it found it.
///     Bypassing a cache over that class buys a rebuild that cannot fix it — so content
///     belongs on the cold path (explaining a build that did not help), never as the
///     gate on the hot one.
module FsHotWatch.OutputCopyFreshness

open System.IO
open FsHotWatch.Events

/// One dependency assembly the build is responsible for placing in a consumer project's
/// output directory, with every output of the PRODUCING project it could have been
/// copied from.
///
/// `PrimaryOrigin` is separate from `OtherOrigins` rather than the head of one list so
/// that "there is at least one origin to compare against" is a fact of the type. A pair
/// with no origin at all would be a producer that has never been built — the
/// `DllMissing` finding, which its own gate already owns — and representing it here
/// would put an unreachable arm in every consumer.
type CopyPair =
    {
        /// The producing project's stem (the assembly whose copy this is).
        Producer: string
        /// The consuming project's stem — the output directory holding the copy.
        Consumer: string
        /// The origin the consumer most likely took its copy from: the producer's output
        /// under the SAME target framework as the consumer, when there is one. Only ever
        /// used to name a file in a message; every verdict is over the whole set.
        PrimaryOrigin: string
        /// The producer's other per-TFM outputs. Which framework MSBuild chose is not
        /// knowable from the graph, and a net10.0 consumer takes a netstandard2.0
        /// dependency's netstandard2.0 output quite happily.
        OtherOrigins: string list
        /// The file in the consumer's output directory a `--no-build` run would load.
        Copy: string
    }

    /// Every output the copy could legitimately have come from.
    member this.Origins = this.PrimaryOrigin :: this.OtherOrigins

/// What a CONTENT comparison of a copy against its origins found.
///
/// Fail-closed on ignorance, in both directions: a file we could not read is never a
/// verdict. An unreadable COPY could otherwise masquerade as a mismatch (or, worse, as a
/// match), and a mismatch we could not fully check against every origin is ignorance
/// rather than evidence.
type CopyContent =
    /// The copy holds the bytes of one of the origins — the run loads code that matches
    /// what the build produced.
    | MatchesAnOrigin
    /// The copy holds the bytes of NO origin. Carries the origin worth naming.
    | DiffersFromOrigins of origin: string
    /// The copy itself could not be read.
    | CopyUnreadable of copy: string
    /// The copy matched nothing, but an origin could not be read, so "matched nothing"
    /// is not a finding.
    | OriginUnreadable of origin: string

/// THE copy rule, by CONTENT: is `copy` byte-identical to any of the origins the build
/// could have produced it from?
///
/// No mtime anywhere in here, and no attempt to work out WHICH origin MSBuild picked —
/// answering that needs the nearest-compatible-framework rules. Matching ANY current
/// output means the run loads code that matches the sources; matching NONE means old
/// bytes whichever framework produced them.
///
/// `hashFile` is injected so a caller with a per-run memo (TestPrune hashes one
/// dependency assembly once and compares it against every consumer's copy) does not pay
/// for it repeatedly, and so a test can drive the rule without touching a disk.
let verdict (hashFile: string -> string) (copy: string) (primaryOrigin: string) (otherOrigins: string list) =
    let copyHash = hashFile copy

    if not (ContentHash.isReadable copyHash) then
        CopyUnreadable copy
    else
        let hashed = primaryOrigin :: otherOrigins |> List.map (fun o -> o, hashFile o)

        if hashed |> List.exists (fun (_, h) -> h = copyHash) then
            MatchesAnOrigin
        else
            match hashed |> List.tryFind (fun (_, h) -> not (ContentHash.isReadable h)) with
            | Some(unreadable, _) -> OriginUnreadable unreadable
            | None -> DiffersFromOrigins primaryOrigin

/// Would MSBuild's own incremental copy still have work to do for this pair?
///
/// `Copy`'s `SkipUnchangedFiles` skips exactly when source and destination agree on size
/// AND last-write time, so a copy that agrees with NO origin on both is one the next real
/// build will re-emit — and a copy that agrees with one is one no build will ever touch
/// again. That is the whole reason this is the predicate a cache-replay gate may use: it
/// is the same question the build system asks itself, so refusing a replay over it costs
/// at most the build that clears it.
///
/// Pure `stat` — two per origin, none of the file bodies. It is deliberately NOT a
/// staleness claim: a pending copy may already hold the right bytes (a rebuild that
/// produced identical output). Over-reporting that way costs one build; under-reporting
/// is the wedge.
let isPending (pair: CopyPair) : bool =
    let copyInfo = FileInfo(pair.Copy)

    let matches (origin: string) =
        let originInfo = FileInfo(origin)

        originInfo.Exists
        && originInfo.Length = copyInfo.Length
        && originInfo.LastWriteTimeUtc = copyInfo.LastWriteTimeUtc

    copyInfo.Exists && not (pair.Origins |> List.exists matches)

/// The per-TFM outputs of a project, given the one output path the graph recorded.
///
/// Derived from the recorded path's SIBLING directories rather than a hard-coded
/// `bin/Debug/<tfm>` so it holds for a repo using `artifacts/`-style output layout too:
/// whatever directory the primary output sits in, its siblings are the other frameworks'.
/// Falls back to the recorded path alone when there is no parent to look in.
let private siblingOutputs (recordedDll: string) : string list =
    let fileName = Path.GetFileName recordedDll

    let parent = recordedDll |> Path.GetDirectoryName |> Path.GetDirectoryName

    let siblings =
        // `Directory.Exists` is documented to answer `false` for null, empty and
        // malformed paths, so it is also the guard for a recorded output path with no
        // grandparent directory to enumerate — no separate emptiness check to keep in
        // step with it.
        if not (Directory.Exists parent) then
            []
        else
            Directory.GetDirectories parent
            |> Array.toList
            |> List.map (fun d -> Path.Combine(d, fileName))
            |> List.filter File.Exists

    // The recorded path first when it exists, so a single-TFM project (the common case)
    // does not depend on directory enumeration order for which origin gets named.
    let recorded = if File.Exists recordedDll then [ recordedDll ] else []
    recorded @ (siblings |> List.filter (fun s -> s <> recordedDll))

/// Every project that transitively depends on `producer` — the consumers whose output
/// directories the build copies the producer's assembly into.
let private transitiveDependents (graph: ProjectGraph.IProjectGraphReader) (producer: AbsProjectPath) =
    let rec walk (seen: Set<AbsProjectPath>) (queue: AbsProjectPath list) =
        match queue with
        | [] -> seen
        | p :: rest when seen.Contains p -> walk seen rest
        | p :: rest -> walk (Set.add p seen) (graph.GetDependents p @ rest)

    walk Set.empty (graph.GetDependents producer)

/// Every dependency-assembly copy the graph implies and the disk confirms.
///
/// Scoped to the ProjectReference closure the graph already tracks — never "any file
/// whose name matches a project", which would condemn a NuGet package that happens to
/// share a project's assembly name. Scoped to copies that EXIST, because a copy that is
/// absent is the producer's own `DllMissing` finding (or a build still in flight), and
/// two gates reporting one file is how a diagnostic stops being read.
///
/// Content items are NOT covered: the graph tracks compile items only, so it cannot see
/// them. Every occurrence recorded against AUTOMATION-245 in the consuming repo was a
/// build-tooling ASSEMBLY copied into a test project, which is what this covers.
/// TestPrune's `ArtifactFreshness` remains the wider check, on its own on-disk walk.
let dependencyCopies (graph: ProjectGraph.IProjectGraphReader) : CopyPair list =
    let stem (p: AbsProjectPath) =
        Path.GetFileNameWithoutExtension(AbsProjectPath.value p)

    let outputDirOf (p: AbsProjectPath) =
        graph.GetCanonicalDllPath p
        |> Option.map Path.GetDirectoryName
        |> Option.filter (isNull >> not)

    graph.GetAllProjects()
    |> List.collect (fun producer ->
        match graph.GetCanonicalDllPath producer with
        | None -> []
        | Some recordedDll ->
            match siblingOutputs recordedDll with
            | [] -> [] // never built — `DllMissing`'s finding, not this one's
            | firstOrigin :: restOrigins ->
                let origins = firstOrigin :: restOrigins
                let fileName = Path.GetFileName recordedDll

                transitiveDependents graph producer
                |> Set.toList
                |> List.choose (fun consumer ->
                    match outputDirOf consumer with
                    | None -> None
                    | Some consumerDir ->
                        let copy = Path.Combine(consumerDir, fileName)

                        // A consumer sharing the producer's output directory holds the
                        // origin itself, not a copy of it.
                        if not (File.Exists copy) || List.contains copy origins then
                            None
                        else
                            // Name the origin built for the consumer's own framework when
                            // there is one — the file the reader is looking at.
                            let consumerTfm = Path.GetFileName consumerDir

                            let primary, others =
                                match
                                    origins
                                    |> List.partition (fun o ->
                                        System.String.Equals(
                                            Path.GetFileName(Path.GetDirectoryName o),
                                            consumerTfm,
                                            System.StringComparison.OrdinalIgnoreCase
                                        ))
                                with
                                | matching :: restMatching, rest -> matching, restMatching @ rest
                                // Nothing matched the consumer's framework, so the
                                // partition returned `origins` unchanged.
                                | [], _ -> firstOrigin, restOrigins

                            Some
                                { Producer = stem producer
                                  Consumer = stem consumer
                                  PrimaryOrigin = primary
                                  OtherOrigins = others
                                  Copy = copy }))
