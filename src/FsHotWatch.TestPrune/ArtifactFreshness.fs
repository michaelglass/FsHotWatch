/// The freshness gate for a `--no-build` test run: would running
/// `dotnet run --project <test> --no-build` execute bits that do not match the
/// sources on disk? A "yes" blocks the run.
///
/// Decided per-project over the test project's own transitive `ProjectReference`
/// closure, over the three things a build does: it COMPILES each project's sources
/// into that project's own assembly (`AssemblyOlderThanSource`), it COPIES
/// dependency assemblies and content/fixture items into the test project's output
/// dir (`CopyDiffersFromOrigin`), and it GENERATES the runtime dependency manifest
/// the host resolves assemblies through (`DepsManifestOlderThanRestore`). Nothing
/// outside the closure is asserted about, and all three checks are exactly what a
/// plain `dotnet build` fixes.
///
/// A copy is judged by CONTENT, never by an mtime — current iff its bytes are the
/// bytes of one of the outputs the build could have copied it from — so a
/// multi-targeted dependency, a working-copy restamp and a coarse-timestamp
/// rebuild-within-one-tick cannot fool it. Hashing goes through core's `ContentHash`
/// and inherits its fail-closed sentinel: a file we cannot read is
/// `InputsUndeterminable`, never "fresh". The WALK is fail-closed on the same terms
/// (`SafeWalk.walk`, not the best-effort enumeration): a directory we could not look
/// inside is `InputsUndeterminable` too, because a source that never arrives is a
/// source nothing is newer than.
///
/// Self-contained (on-disk `.fsproj` parse rather than `IProjectGraphReader`)
/// because `executeTests` is graph-free — shared with the one-off `run-tests`
/// command, which has no daemon and no discovered graph — and the graph tracks
/// `Compile` items only, so it could not see content items even if reachable.
module FsHotWatch.TestPrune.ArtifactFreshness

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Xml.Linq
open FsHotWatch

/// The build outputs one `dotnet run --project <p> --no-build` will load.
/// Derived from the runner's command line by the caller (`deriveProjectBin`),
/// which owns the `--project`-may-be-a-file-or-a-directory parsing.
type RunnerTarget =
    {
        /// The `.fsproj`/`.csproj`, when one could be located. `None` when
        /// `--project` named a directory holding no project file: the reference
        /// closure is then unknowable and freshness is judged on that directory
        /// alone (never on the repo).
        ProjectFile: string option
        /// The directory holding the project's sources and content items.
        ProjectDir: string
        /// The assembly (and so DLL) name — `<AssemblyName>.dll`.
        AssemblyName: string
        /// `<projectDir>/bin/Debug`; the per-TFM output dirs live under it.
        BinDir: string
    }

/// Why a test project's build output cannot be trusted for a `--no-build` run.
/// Every case names the exact pair of files that prove the build did not run — so
/// the gate's message is actionable, and a plain `dotnet build` (never
/// `-t:Rebuild`) is always the remedy.
type StaleInput =
    /// A compile input is newer than the assembly compiled from it: the compile
    /// did not run since the edit. `Project` is the owning project's directory
    /// leaf — the test project itself, or any project in its closure.
    ///
    /// The only mtime judgement in this module, and the only one that can be made:
    /// a `.fs` source and the `.dll` compiled from it share no bytes, so there is
    /// nothing to compare but the clock.
    | AssemblyOlderThanSource of project: string * source: string * sourceMtime: DateTime * assemblyMtime: DateTime
    /// A file the build copies into the test project's output dir (a dependency
    /// assembly, or a content/fixture item — its own, or one carried in from a
    /// referenced project) does not hold the BYTES of any output it could have
    /// been copied from: the copy did not happen since the edit, so the run would
    /// read the old bytes.
    ///
    /// Carries NO MTIMES, deliberately. Two per-TFM outputs of one project are built
    /// minutes apart and this module cannot know which framework MSBuild chose, so a
    /// verdict holding no mtimes cannot compare two across a TFM boundary.
    | CopyDiffersFromOrigin of origin: string * copy: string
    /// The runtime dependency manifest the host reads — `bin/<tfm>/<Assembly>.deps.json`
    /// — is OLDER than the restore it is generated from, `obj/project.assets.json`. The
    /// manifest therefore lists the reference closure of a SUPERSEDED restore.
    ///
    /// Nothing about the compile says so, which is why the other three cases cannot see
    /// it: every assembly can be newer than every source and every copy can hold exactly
    /// its origin's bytes while this is true. But the host resolves assemblies from the
    /// manifest, not from the directory, so a dependency sitting in the output folder and
    /// missing from the manifest is a `FileNotFoundException` on precisely the code paths
    /// that touch it — a green build and a red run, on specific routes only, which reads
    /// like an application bug (AUTOMATION-528).
    ///
    /// An mtime judgement, and — as with `AssemblyOlderThanSource` — the only one
    /// available: a restore and the manifest generated from it share no bytes.
    | DepsManifestOlderThanRestore of
        project: string *
        assets: string *
        manifest: string *
        assetsMtime: DateTime *
        manifestMtime: DateTime
    /// The gate could not determine what this run's inputs ARE — an unreadable or
    /// unparseable project file, or a `ProjectReference` it cannot resolve. FAIL
    /// CLOSED: a freshness gate that answers "up to date" because it could not look
    /// is the bug this module exists to kill. Refuse the run and let the build
    /// report the real error.
    | InputsUndeterminable of project: string * reason: string

/// Compile inputs — the files whose edit must force a recompile before a verdict
/// from the resulting binary can be believed.
let private compileExtensions = set [ ".fs"; ".fsi"; ".fsx"; ".cs" ]

/// Directory names never walked: build output (`bin`/`obj` — where a copied file
/// could masquerade as its own origin), VCS, and tooling state. Shared with every
/// other repo-scale walk so the exclusions cannot drift per-caller.
let private excludedDirs = SafeWalk.SourceExcludedDirs

/// One-line human phrasing of a stale verdict, naming the offending file pair.
let describe (stale: StaleInput) : string =
    match stale with
    | AssemblyOlderThanSource(project, source, sourceMtime, assemblyMtime) ->
        sprintf
            "%s was compiled at %O but its source %s was edited at %O — the build has not run since the edit"
            project
            assemblyMtime
            source
            sourceMtime
    | CopyDiffersFromOrigin(origin, copy) ->
        sprintf "%s has not been copied into the test output since it changed — %s holds different bytes" origin copy
    | DepsManifestOlderThanRestore(project, assets, manifest, assetsMtime, manifestMtime) ->
        sprintf
            "%s's runtime dependency manifest %s was generated at %O from a restore that %s superseded at %O — it lists a stale reference closure, so an assembly present in the output folder can still fail to load"
            project
            manifest
            manifestMtime
            assets
            assetsMtime
    | InputsUndeterminable(project, reason) ->
        sprintf
            "%s: cannot determine what this test run's inputs are (%s) — refusing to call it fresh; build it and let the build report the error"
            project
            reason

/// The per-TFM output directories under a project's `bin/Debug` (one per target
/// framework), or empty when no build output exists yet. The apphost and the
/// managed DLL both live under a `<tfm>/` subdir whose name we cannot know
/// without the project graph, so both the presence probe (`tryApphostPresent`)
/// and this gate scan across these.
let tfmOutputDirs (binDir: string) : string[] =
    if Directory.Exists binDir then
        Directory.GetDirectories binDir
    else
        [||]

/// A project's own `bin/Debug`, derived the same way `RunnerTarget.BinDir` is.
let private binDirOf (projectDir: string) =
    Path.Combine(projectDir, "bin", "Debug")

/// A memo whose value factory runs AT MOST ONCE per key, however many threads ask
/// for that key at once.
///
/// `ConcurrentDictionary.GetOrAdd(key, valueFactory)` does NOT give that: it may
/// invoke the factory concurrently on several threads for one key and publish only
/// one result, so the losers still did the work. Here that duplicate is the NORMAL
/// case — test groups run in parallel and their `ProjectReference` closures overlap
/// heavily. A `Lazy` under `ExecutionAndPublication` IS the guarantee: one
/// execution, every other caller blocks on it.
type internal OnceMemo<'K, 'V when 'K: equality>() =
    let entries = ConcurrentDictionary<'K, Lazy<'V>>()

    /// The memoised value for `key`, computing it with `factory` if this is the
    /// first ask. `factory` runs at most once per key, ever.
    member _.GetOrAdd(key: 'K, factory: 'K -> 'V) : 'V =
        entries
            .GetOrAdd(key, (fun k -> Lazy<'V>((fun () -> factory k), LazyThreadSafetyMode.ExecutionAndPublication)))
            .Value

/// Per-run memo. The directory walks and `.fsproj` parses are the expensive part
/// of the gate, and every test config in a run shares the same closure prefixes —
/// so each project is walked at most ONCE per run (the `OnceMemo` guarantee), even
/// when several test projects depend on it. Thread-safe: test groups run in parallel.
type Cache() =
    let closures = OnceMemo<string, Result<string list, string>>()
    let files = OnceMemo<string, Result<(string * DateTime) list, string>>()
    let assemblies = OnceMemo<string, (string * DateTime) option>()
    let outputs = OnceMemo<string, string list>()
    let hashes = OnceMemo<string, string>()

    /// Direct `ProjectReference` includes of a project file, absolute and
    /// resolved.
    ///
    /// FAILS CLOSED. A project file we cannot read, cannot parse, or whose
    /// `ProjectReference` we cannot resolve (no `Include`, or an `Include` naming
    /// a project that does not exist) means WE DO NOT KNOW WHAT THIS RUN'S INPUTS
    /// ARE. Swallowing that into "no references" would shrink the closure to
    /// nothing and let a stale dependency sail through as fresh. "I could not look"
    /// is `Error`: the caller refuses the run and lets the BUILD report the real
    /// error, which it will, loudly, on the same malformed file.
    let directReferences (projectFile: string) : Result<string list, string> =
        try
            let projDir = Path.GetDirectoryName projectFile
            let doc = XDocument.Load(projectFile)

            // An `Include` may be COMPUTED — `Include="$(SomeProject)"` (TestPrune's
            // own test project computes its optional Falco.UnionRoutes reference this
            // way). Read textually that is a search for a file literally named
            // `$(SomeProject)`, which never exists, so the gate answers
            // `InputsUndeterminable` and defers every run on a perfectly fresh tree.
            //
            // Deliberately a SMALL expansion, not an MSBuild evaluation: this file's
            // own declared properties plus the two well-known directory ones, which
            // covers the shape that occurs in practice. Anything it cannot expand
            // stays unexpanded, so the path does not exist and the fail-closed arm
            // below still runs — never a false "fresh".
            let properties =
                let declared =
                    doc.Descendants()
                    |> Seq.filter (fun el -> not (isNull el.Parent) && el.Parent.Name.LocalName = "PropertyGroup")
                    |> Seq.map (fun el -> el.Name.LocalName, el.Value)
                    |> List.ofSeq

                // Well-known ones LAST, so a project cannot redefine them out from under us.
                declared
                @ [ "MSBuildThisFileDirectory", projDir + string Path.DirectorySeparatorChar
                    "MSBuildProjectDirectory", projDir ]
                |> Map.ofList

            let rec expand depth (s: string) =
                // Bounded: a property defined in terms of itself must not spin.
                if depth > 10 || not (s.Contains "$(") then
                    s
                else
                    let replaced =
                        Text.RegularExpressions.Regex.Replace(
                            s,
                            @"\$\(([A-Za-z_][A-Za-z0-9_]*)\)",
                            (fun m ->
                                match properties.TryFind m.Groups[1].Value with
                                | Some v -> v
                                | None -> m.Value)
                        )

                    if replaced = s then s else expand (depth + 1) replaced

            let resolve (el: XElement) =
                match el.Attribute(XName.Get "Include") |> Option.ofObj with
                | None -> Error $"%s{projectFile} has a <ProjectReference> with no Include"
                | Some attr ->
                    let path =
                        Path.GetFullPath(Path.Combine(projDir, expand 0 (attr.Value.Replace('\\', '/'))))

                    if File.Exists path then
                        Ok(Some path)
                    elif not (isNull (el.Attribute(XName.Get "Condition"))) then
                        // OPTIONAL BY CONSTRUCTION. The project itself guards this
                        // reference with a `Condition`, which is how an optional sibling
                        // checkout is expressed — TestPrune's Falco reference is
                        // `Condition="'$(FalcoUnionRoutesAvailable)' == 'true'"` and its
                        // absence is a supported, documented state. We do not evaluate
                        // MSBuild conditions, so on a machine without that sibling the
                        // alternative is deferring every run forever over a reference the
                        // build correctly ignores.
                        //
                        // The trade: a REQUIRED reference wrongly carrying an always-true
                        // `Condition` is skipped rather than refused. The build runs
                        // before this gate and fails loudly on a missing reference
                        // anyway. An UNCONDITIONAL missing reference is still an error.
                        Ok None
                    else
                        Error $"%s{projectFile} references %s{path}, which does not exist"

            // The FIRST reference we cannot resolve ends it: there is no such thing
            // as a partially-known closure, so there is deliberately no arm that
            // keeps what resolved and drops the rest — that arm IS the fail-open bug.
            let rec resolveAll acc elements =
                match elements with
                | [] -> Ok(acc |> List.rev |> List.distinct)
                | el :: rest ->
                    match resolve el with
                    | Error e -> Error e
                    | Ok(Some path) -> resolveAll (path :: acc) rest
                    | Ok None -> resolveAll acc rest

            doc.Descendants()
            |> Seq.filter (fun el -> el.Name.LocalName = "ProjectReference")
            |> List.ofSeq
            |> resolveAll []
        with ex ->
            Error $"%s{projectFile} could not be read: %s{ex.Message}"

    /// The transitive `ProjectReference` closure of `projectFile`, INCLUDING
    /// itself — the complete set of projects whose sources and content the test
    /// binary is built from and runs against. Cycle-guarded (an fsproj cycle is
    /// MSBuild's error to report, not a hang here).
    ///
    /// `Error` when any project file in the closure could not be read or resolved:
    /// the inputs are then unknown, and an unknown input is a REBUILD, never a
    /// pass (see `directReferences`).
    member _.Closure(projectFile: string) : Result<string list, string> =
        let rec walk (visited: Set<string>) (queue: string list) =
            match queue with
            | [] -> Ok(Set.toList visited)
            | p :: rest when visited.Contains p -> walk visited rest
            | p :: rest ->
                match closures.GetOrAdd(p, directReferences) with
                | Error e -> Error e
                | Ok refs -> walk (Set.add p visited) (refs @ rest)

        walk Set.empty [ Path.GetFullPath projectFile ]

    /// Every file under `dir` (build output, VCS and tooling dirs excluded), as
    /// `(path relative to dir, mtime)`. The relative path is the key the build
    /// copies content by — `<outDir>/<tfm>/<relative path>` — so it is what a
    /// copy is looked up under.
    ///
    /// `Error` when the walk could not see all of `dir` — an unreadable
    /// subdirectory, or one past `SafeWalk.MaxDepth`. NOT a shorter list: a list is
    /// read here as "these are the inputs", and a short one says every input is
    /// accounted for. Same fail-closed rule as `directReferences`: an input we could
    /// not look at is unknown, and an unknown input is a REBUILD, never a pass.
    member _.FilesUnder(dir: string) : Result<(string * DateTime) list, string> =
        files.GetOrAdd(
            dir,
            fun d ->
                // No existence guard: SafeWalk yields nothing for a missing root.
                let walked = SafeWalk.walk excludedDirs "*" d

                match walked.Skipped with
                | [] ->
                    walked.Files
                    |> List.map (fun f -> Path.GetRelativePath(d, f.FullName), f.LastWriteTimeUtc)
                    |> Ok
                | skipped -> skipped |> List.map SafeWalk.describeSkip |> String.concat "; " |> Error
        )

    /// EVERY `<assemblyName>.dll` this project has built, one per per-TFM output
    /// dir — the full set of outputs a consumer's copy could have come from, since
    /// which one MSBuild chose is not knowable here (see `copyVerdict`).
    ///
    /// Empty when the project has not been built yet. That is NOT staleness: a
    /// missing artifact is the presence probe's business (a build in flight may
    /// still land it), and this gate only judges artifacts that exist.
    member _.OwnAssemblyOutputs(projectDir: string, assemblyName: string) : string list =
        outputs.GetOrAdd(
            Path.Combine(projectDir, assemblyName),
            fun _ ->
                tfmOutputDirs (binDirOf projectDir)
                |> Array.choose (fun tfmDir ->
                    let dll = Path.Combine(tfmDir, assemblyName + ".dll")

                    if File.Exists dll then Some dll else None)
                |> Array.toList
        )

    /// The project's own most recently built `<assemblyName>.dll` — its path and
    /// mtime — across its per-TFM output dirs, or `None` when the project has not
    /// been built yet. `None` is NOT staleness (see `OwnAssemblyOutputs`).
    ///
    /// Used ONLY by the compile check, where the comparison is against the project's
    /// OWN sources and the NEWEST output is the conservative-against-false-stale
    /// choice: if any framework was compiled after the edit, the compile ran. It must
    /// never be used as the ORIGIN of a copy — across TFMs, "newest" and "the one
    /// that was copied" are different files.
    member this.OwnAssembly(projectDir: string, assemblyName: string) : (string * DateTime) option =
        assemblies.GetOrAdd(
            Path.Combine(projectDir, assemblyName),
            fun _ ->
                this.OwnAssemblyOutputs(projectDir, assemblyName)
                |> List.map (fun dll -> dll, File.GetLastWriteTimeUtc dll)
                |> function
                    | [] -> None
                    | built -> Some(built |> List.maxBy snd)
        )

    /// The content hash of a file, memoised — the gate hashes a dependency
    /// assembly once and then compares it against every consumer's copy of it.
    /// `ContentHash.ofFile` never throws: an unreadable file hashes to the
    /// `UnhashableContent` sentinel, which matches nothing, and callers turn that
    /// into `InputsUndeterminable` rather than a pass.
    member _.Hash(path: string) : string =
        hashes.GetOrAdd(path, ContentHash.ofFile)

/// The leaf name a project is known by (its directory name, which by convention
/// is also its assembly name).
let private projectLabel (projectDir: string) =
    Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

/// THE copy rule, phrased in this module's verdict type. The rule ITSELF lives in
/// core (`OutputCopyFreshness.verdict`) — is `copy` byte-identical to one of the
/// origins the build could have copied it from? `None` = current, `Some` = the run
/// would read bytes no current build produces.
///
/// It moved to core for AUTOMATION-245: `FsHotWatch.Build` and `FsHotWatch.TestPrune`
/// are siblings over core, so the plugin that owns the build cache could not see this
/// rule at all, and the ticket named that as the reason its acceptance was unmet.
/// Restating it there would have been two implementations of a rule that has to agree,
/// which is how a gate degrades without anyone noticing — so there is one, here
/// through it.
///
/// `primary`/`others` rather than a candidate list because "there is at least one
/// origin to compare against" is a fact of both call sites below, and a list makes it
/// an unreachable arm every caller has to carry.
///
/// FAILS CLOSED on a file it cannot read: "I could not read it" is
/// `InputsUndeterminable`, never a verdict.
let private copyVerdict
    (cache: Cache)
    (project: string)
    (copy: string)
    (primary: string)
    (others: string list)
    : StaleInput option =
    match OutputCopyFreshness.verdict cache.Hash copy primary others with
    | OutputCopyFreshness.MatchesAnOrigin -> None
    | OutputCopyFreshness.DiffersFromOrigins origin -> Some(CopyDiffersFromOrigin(origin, copy))
    | OutputCopyFreshness.CopyUnreadable path ->
        Some(InputsUndeterminable(project, $"could not read the build's copy at %s{path}"))
    | OutputCopyFreshness.OriginUnreadable path -> Some(InputsUndeterminable(project, $"could not read %s{path}"))

/// The origin candidates, ordered so the one the consumer most likely consumes is
/// FIRST — purely so a stale message names the framework the reader is looking at.
/// The VERDICT is content over the whole set and does not depend on this order.
///
/// Takes and returns the head separately so the non-emptiness the caller already
/// established survives the reordering.
let private consumerTfmFirst (tfmDir: string) (first: string) (rest: string list) : string * string list =
    let consumerTfm =
        Path.GetFileName(tfmDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

    let tfmOf (candidate: string) =
        Path.GetFileName(Path.GetDirectoryName candidate)

    let matching, others =
        first :: rest
        |> List.partition (fun c -> String.Equals(tfmOf c, consumerTfm, StringComparison.OrdinalIgnoreCase))

    match matching with
    | m :: moreMatching -> m, moreMatching @ others
    // Nothing was built for the consumer's framework, so the partition left the
    // candidates exactly as they came in.
    | [] -> first, rest

/// Is one project's contribution to `tfmDir` stale? `projectDir`/`assemblyName`
/// name a project in the test project's closure (possibly the test project
/// itself); `outputs` holds the path — relative to `tfmDir` — of every file the
/// build has placed there.
///
/// `outputs` is a SET, not a map to mtimes. All the copy check ever asks of it is
/// *"did the build put something here?"* — a file the build does not copy has no
/// destination and is never asserted about, which is what keeps a false positive
/// unrepresentable. What is at that destination is then settled by content.
let private staleContribution
    (cache: Cache)
    (tfmDir: string)
    (outputs: IReadOnlySet<string>)
    (closure: (string * string) list)
    (projectDir: string)
    (assemblyName: string)
    : StaleInput option =
    let project = projectLabel projectDir

    // FAIL CLOSED on a walk that could not see the whole project. A directory
    // SafeWalk could not enumerate used to contribute NOTHING here, and this check
    // asks "is any source newer than the assembly?" — so an unreadable source dir
    // produced no source, no source was newer, and the gate said FRESH about bits it
    // had never looked at (AUTOMATION-164). "I could not look" is the same answer as
    // an unreadable project file: refuse, and let the build report the real error.
    match cache.FilesUnder projectDir with
    | Error reason -> Some(InputsUndeterminable(project, reason))
    | Ok sources ->

        /// Every file in the CLOSURE that the build could have copied to `rel` — this
        /// project's returned separately as the primary, so a stale message names the
        /// project being judged, and so the caller's non-emptiness survives into
        /// `copyVerdict` rather than becoming an unreachable arm there. Not
        /// always this project's alone: two closure projects may hold a file at the SAME
        /// relative path (`xunit.runner.json` sits in five of them here), MSBuild copies
        /// both to one destination and the last writer wins. Checked against every
        /// claimant, and current if it matches ANY — otherwise the shadowed project is
        /// condemned for a build doing exactly what it means to do.
        let claimantsOf (rel: string) =
            let mine = Path.Combine(projectDir, rel)

            let others =
                closure
                |> List.map (fun (dir, _) -> Path.Combine(dir, rel))
                |> List.filter (fun candidate -> candidate <> mine && File.Exists candidate)

            mine, others

        // (1) COMPILE. The project's own assembly must be newer than every compile
        //     input it was built from. Judged against the project's OWN output, never
        //     the consumer's: a private-only edit to a dependency need not relink its
        //     consumers (that is what reference assemblies are for), so comparing a
        //     dependency's source against the TEST project's DLL would be an
        //     accusation no build can answer.
        let staleCompile =
            match cache.OwnAssembly(projectDir, assemblyName) with
            | None -> None // not built yet — the presence probe's business, not ours
            | Some(_, assemblyMtime) ->
                sources
                |> List.filter (fun (rel, _) -> compileExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant()))
                |> List.tryFind (fun (_, mtime) -> mtime > assemblyMtime)
                |> Option.map (fun (rel, mtime) ->
                    AssemblyOlderThanSource(project, Path.Combine(projectDir, rel), mtime, assemblyMtime))

        // (2) COPY. Every file of this project that the build has copied into the test
        //     project's output dir must hold the bytes it was copied from — content and
        //     fixture items included, its own and those carried in transitively …
        let staleCopy () =
            sources
            |> List.tryPick (fun (rel, _) ->
                if outputs.Contains rel then
                    let primary, others = claimantsOf rel
                    copyVerdict cache project (Path.Combine(tfmDir, rel)) primary others
                else
                    None)

        // … and (3) the dependency's ASSEMBLY, which the same copy carries into the
        //     consumer's output dir. Catches "only the dependency was rebuilt": its own
        //     DLL is fresh, but the copy the test run would actually load is not. (For
        //     the test project itself the copy IS one of the candidates, so it no-ops.)
        let staleAssemblyCopy () =
            let copy = Path.Combine(tfmDir, assemblyName + ".dll")

            if not (File.Exists copy) then
                None // not copied yet — the presence probe's business, not ours
            else
                match cache.OwnAssemblyOutputs(projectDir, assemblyName) with
                | [] -> None // not built yet — likewise
                | first :: rest ->
                    let primary, others = consumerTfmFirst tfmDir first rest
                    copyVerdict cache project copy primary others

        staleCompile
        |> Option.orElseWith staleCopy
        |> Option.orElseWith staleAssemblyCopy

/// Is the runtime dependency manifest in `tfmDir` older than the restore it is
/// generated from?
///
/// Asked ONLY of the project that is about to run, never of its closure: the host
/// resolves through the entry assembly's manifest, and a library's own `deps.json` is
/// not read at all. So the question has exactly one subject, and asking it of the
/// closure would manufacture refusals nothing can act on.
///
/// `dotnet restore` writes `obj/project.assets.json`; only a BUILD writes
/// `bin/<tfm>/<Assembly>.deps.json` from it. That asymmetry is the whole defect: the
/// deps-freshness gate's automatic restore repairs the compile and leaves the manifest
/// behind, so the compile error looks transient while the load failure survives.
let private staleDepsManifest (projectDir: string) (assemblyName: string) (tfmDir: string) : StaleInput option =
    let assets = Path.Combine(projectDir, "obj", "project.assets.json")
    let manifest = Path.Combine(tfmDir, assemblyName + ".deps.json")

    // Absence is never staleness here, the same rule the rest of this module follows: a
    // project with no restore output (an old-style project, a cleaned `obj/`) and one
    // that generates no manifest are both legitimate, and there is nothing to compare.
    if not (File.Exists assets) || not (File.Exists manifest) then
        None
    else
        let assetsMtime = File.GetLastWriteTimeUtc assets
        let manifestMtime = File.GetLastWriteTimeUtc manifest

        // STRICT `>`. A build writes the restore first and the manifest after it, so
        // equal timestamps — a coarse filesystem, or both inside one tick — are the
        // NORMAL healthy shape and must not read as stale.
        if assetsMtime > manifestMtime then
            Some(DepsManifestOlderThanRestore(projectLabel projectDir, assets, manifest, assetsMtime, manifestMtime))
        else
            None

/// Is the test project's output at `tfmDir` stale — i.e. is ANY project in its
/// closure contributing something out of date to it? `ordered` is that closure,
/// test project first.
let private staleInTfmDir
    (cache: Cache)
    (label: string)
    (ordered: (string * string) list)
    (tfmDir: string)
    : StaleInput option =
    let walked = SafeWalk.walk Set.empty "*" tfmDir

    match walked.Skipped with
    // FAIL CLOSED. `outputs` is read as "did the build put something here?", and a
    // file missing from it is never asserted about — so a directory the walk could
    // not see turns every copy under it into a destination nobody checks. An absence
    // we cannot distinguish from a hole is not evidence (AUTOMATION-164).
    | _ :: _ ->
        Some(InputsUndeterminable(label, walked.Skipped |> List.map SafeWalk.describeSkip |> String.concat "; "))
    | [] ->
        // A hash SET, not an F# `Map`: read exactly one way ("is there a copy at
        // `rel`?") over a full walk of the output dir (hundreds of DLLs before content
        // and fixtures), so paying O(n log n) for an ordering nothing asks for is waste.
        let outputs =
            walked.Files
            |> Seq.map (fun f -> Path.GetRelativePath(tfmDir, f.FullName))
            |> HashSet

        ordered
        |> List.tryPick (fun (projectDir, assemblyName) ->
            staleContribution cache tfmDir outputs ordered projectDir assemblyName)

/// Would a `--no-build` run of this test project execute bits that do not match
/// the sources? `Some reason` blocks the run (and names the file pair proving
/// it); `None` lets it through.
///
/// `None` — runnable — also when the project has no build output at all: absence
/// is the presence probe's (`tryApphostPresent`) retry-friendly business, since a
/// build in flight may still land the apphost. This gate speaks only about
/// artifacts that EXIST and are out of date; those will not refresh on their own.
///
/// It FAILS CLOSED on ignorance. If the closure cannot be determined — an
/// unreadable/unparseable project file, an unresolvable `ProjectReference` — the
/// answer is `InputsUndeterminable`, NOT `None`.
///
/// A multi-targeted project is stale only when EVERY per-TFM output dir is stale:
/// which TFM `dotnet run` selects is not knowable here, so a single fresh output
/// dir means there is a fresh way to run — conservative against false-stale.
let stale (cache: Cache) (target: RunnerTarget) : StaleInput option =
    let sw = Stopwatch.StartNew()

    // No project FILE (a `--project` naming a directory that holds none) is NOT
    // ignorance — there are no declared references to fail to read, and `dotnet run`
    // would itself fail on such a path. That directory's own files are the inputs,
    // and they are fully knowable.
    let closure =
        match target.ProjectFile with
        | Some projectFile ->
            cache.Closure projectFile
            |> Result.map (List.map (fun p -> Path.GetDirectoryName p, Path.GetFileNameWithoutExtension p))
        | None -> Ok [ target.ProjectDir, target.AssemblyName ]

    // Order: the test project first (its own staleness is the likeliest and most
    // legible finding), then its dependencies by path — so the reported reason is
    // deterministic rather than filesystem-order.
    let ordered =
        closure
        |> Result.map (fun projects ->
            let isSelf (dir: string, _) = dir = target.ProjectDir
            let self, deps = projects |> List.partition isSelf
            self @ List.sortBy fst deps)

    let candidateTfmDirs =
        tfmOutputDirs target.BinDir
        |> Array.filter (fun tfmDir -> File.Exists(Path.Combine(tfmDir, target.AssemblyName + ".dll")))

    let verdict =
        match ordered with
        // FAIL CLOSED: inputs unknown, so we cannot certify.
        | Error reason -> Some(InputsUndeterminable(target.AssemblyName, reason))
        | Ok _ when Array.isEmpty candidateTfmDirs -> None // nothing built to be stale — presence probe's business
        | Ok ordered ->
            // Stale iff NO output dir is fresh (see the multi-TFM note above).
            //
            // The manifest check runs FIRST per dir: it is two `stat`s against the walk's
            // hundreds, and when it fires it is the more specific finding — a tree whose
            // manifest predates its restore is one no compile or copy verdict can explain.
            let judgeTfmDir (tfmDir: string) : StaleInput option =
                match staleDepsManifest target.ProjectDir target.AssemblyName tfmDir with
                | Some _ as manifestStale -> manifestStale
                | None -> staleInTfmDir cache target.AssemblyName ordered tfmDir

            let perTfm = candidateTfmDirs |> Array.map judgeTfmDir

            // …except that IGNORANCE does not get the multi-TFM benefit of the doubt.
            // "Some other TFM is fresh, so there is a fresh way to run" is only sound
            // when this one was actually judged; if we could not see it, we do not know
            // which of the two `dotnet run` would pick. Refuse rather than average.
            let undeterminable =
                perTfm
                |> Array.tryPick (function
                    | Some(InputsUndeterminable _ as u) -> Some u
                    | _ -> None)

            match undeterminable with
            | Some u -> Some u
            | None when Array.forall Option.isSome perTfm -> Array.head perTfm
            | None -> None

    let outcome =
        match verdict with
        | Some s -> describe s
        | None -> "fresh"

    let closureSize =
        match closure with
        | Ok projects -> projects.Length
        | Error _ -> 0

    Logging.info
        "test-prune"
        $"freshness gate for %s{target.AssemblyName}: %d{closureSize} projects in closure, scanned in %d{sw.ElapsedMilliseconds}ms — %s{outcome}"

    verdict
