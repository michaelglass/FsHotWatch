/// The freshness gate for a `--no-build` test run (AUTOMATION-122).
///
/// The question this module answers is exactly one: **would running
/// `dotnet run --project <test> --no-build` execute bits that do not match the
/// sources on disk?** A "yes" must block the run (that is the `--no-build`-
/// against-stale-binary hole the gate was added to close, cli alpha.40); a "no"
/// must let it through.
///
/// ## Why the old shape cried wolf
///
/// The predecessor (`newestSourceMtime` + `apphostStale`) compared the test
/// project's DLL against the newest source mtime found ANYWHERE IN THE REPO. So
/// an edit to any leaf project made EVERY project MSBuild then declined to
/// rebuild — i.e. every project outside that edit's dependency closure — look
/// stale. Worse, the accusation was unanswerable: an incremental `dotnet build`
/// is correctly a no-op for an unrelated project, so its DLL mtime never caught
/// up with the repo-wide watermark and the gate stayed red forever. The only
/// escape was `dotnet build -t:Rebuild` — forcing a relink purely to move a
/// timestamp. A guard that cannot be satisfied by the action it demands teaches
/// people to bypass it, and this guard is worth keeping.
///
/// ## The shape that cannot cry wolf
///
/// Freshness is decided per-project, over the test project's OWN transitive
/// `ProjectReference` closure, in terms of the two — and only two — things a
/// build does to produce a runnable output tree:
///
///   1. It COMPILES each project's sources into that project's own assembly.
///      ⇒ `AssemblyOlderThanSource`: a compile input newer than the assembly
///        built from it means the compile did not run since the edit.
///   2. It COPIES files into the test project's output dir — dependency
///      assemblies, and content/fixture items (transitively, from referenced
///      projects). MSBuild copies with `File.Copy`, which PRESERVES the source's
///      mtime, so after a copy the two are EQUAL.
///      ⇒ `CopyOlderThanOrigin`: a copy strictly older than its origin means the
///        copy did not happen since the edit.
///
/// Both are exactly what a plain `dotnet build` fixes, and NOTHING else is
/// asserted — so a file outside the closure cannot make the gate fire, and every
/// firing names a file a normal build will re-emit. Verified against real MSBuild
/// (2026-07-14): an out-of-closure edit leaves the test DLL untouched across
/// repeated incremental builds (the old false positive, now unrepresentable); an
/// in-closure edit restamps the dependency's DLL, its copy in the consumer's
/// output dir, and the consumer's own DLL; and a changed content item is
/// re-copied into the consumer's output dir with the source's mtime.
///
/// ## Content items — the fake green this also closes
///
/// The predecessor looked at `.fs`/`.cs` files only, so a changed test FIXTURE
/// copied in from a shared project was invisible to it: the tests ran
/// `--no-build` against the OLD copy still sitting in `bin/`, PASSED, and the red
/// only surfaced after a forced rebuild (intelligence, `dsa-scope-4.json`,
/// 2026-07-14 — a green merge that left main red for hours). `CopyOlderThanOrigin`
/// covers content and dependency assemblies alike, because it keys on the COPY:
/// a file in a project's directory is only ever compared against a destination
/// that the build actually produced. A file the build does not copy has no
/// destination, so it can never make the gate fire.
///
/// This module is deliberately self-contained (on-disk `.fsproj` parse rather
/// than `IProjectGraphReader`): `executeTests` is graph-free — it is shared with
/// the one-off `run-tests` command, which has no daemon and no discovered graph —
/// and the graph tracks `Compile` items only, so it could not see content items
/// even if it were reachable.
module FsHotWatch.TestPrune.ArtifactFreshness

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
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
/// Every case names the exact pair of files whose mtimes prove the build did not
/// run — so the gate's message is actionable, and a plain `dotnet build` (never
/// `-t:Rebuild`) is always the remedy.
type StaleInput =
    /// A compile input is newer than the assembly compiled from it: the compile
    /// did not run since the edit. `Project` is the owning project's directory
    /// leaf — it may be the test project itself or any project in its closure.
    | AssemblyOlderThanSource of project: string * source: string * sourceMtime: DateTime * assemblyMtime: DateTime
    /// A file the build copies into the test project's output dir (a dependency
    /// assembly, or a content/fixture item — its own, or one carried in from a
    /// referenced project) is strictly older than the file it is copied from: the
    /// copy did not happen since the edit, so the run would read the old bytes.
    | CopyOlderThanOrigin of origin: string * copy: string * originMtime: DateTime * copyMtime: DateTime
    /// The gate could not determine what this run's inputs ARE — an unreadable or
    /// unparseable project file, or a `ProjectReference` it cannot resolve. This is
    /// the FAIL-CLOSED case: a freshness gate that answers "up to date" because it
    /// could not look is precisely the bug this whole module exists to kill. Refuse
    /// the run and let the build report the real error.
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
    | CopyOlderThanOrigin(origin, copy, originMtime, copyMtime) ->
        sprintf
            "%s (edited %O) has not been copied into the test output since — %s is from %O"
            origin
            originMtime
            copy
            copyMtime
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

/// Per-run memo. The directory walks and `.fsproj` parses are the expensive part
/// of the gate, and every test config in a run shares the same closure prefixes —
/// so each project is walked at most ONCE per run, even when several test projects
/// depend on it. Thread-safe: test groups run in parallel.
type Cache() =
    let closures = ConcurrentDictionary<string, Result<string list, string>>()
    let files = ConcurrentDictionary<string, (string * DateTime) list>()
    let assemblies = ConcurrentDictionary<string, (string * DateTime) option>()

    /// Direct `ProjectReference` includes of a project file, absolute and
    /// resolved.
    ///
    /// FAILS CLOSED. A project file we cannot read, cannot parse, or whose
    /// `ProjectReference` we cannot resolve (no `Include`, or an `Include` naming
    /// a project that does not exist) means WE DO NOT KNOW WHAT THIS RUN'S INPUTS
    /// ARE. Swallowing that into "no references" would shrink the closure to
    /// nothing and let a stale dependency sail through as fresh — this gate's own
    /// bug, reborn inside its fix. "I could not look" is not "it is up to date":
    /// it is `Error`, and the caller refuses the run and lets the BUILD report the
    /// real error (which it will, loudly, on the same malformed file).
    let directReferences (projectFile: string) : Result<string list, string> =
        try
            let projDir = Path.GetDirectoryName projectFile

            let resolve (el: XElement) =
                match el.Attribute(XName.Get "Include") |> Option.ofObj with
                | None -> Error $"%s{projectFile} has a <ProjectReference> with no Include"
                | Some attr ->
                    let path = Path.GetFullPath(Path.Combine(projDir, attr.Value.Replace('\\', '/')))

                    if File.Exists path then
                        Ok path
                    else
                        Error $"%s{projectFile} references %s{path}, which does not exist"

            // The FIRST reference we cannot resolve ends it. There is no such thing
            // as a partially-known closure, so there is deliberately no arm here
            // that keeps the references it managed to resolve and drops the rest —
            // that arm IS the fail-open bug.
            let rec resolveAll acc elements =
                match elements with
                | [] -> Ok(acc |> List.rev |> List.distinct)
                | el :: rest ->
                    match resolve el with
                    | Error e -> Error e
                    | Ok path -> resolveAll (path :: acc) rest

            XDocument.Load(projectFile).Descendants()
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
    member _.FilesUnder(dir: string) : (string * DateTime) list =
        files.GetOrAdd(
            dir,
            fun d ->
                // No existence guard: SafeWalk yields nothing for a missing root.
                SafeWalk.enumerateFiles excludedDirs d
                |> Seq.map (fun f -> Path.GetRelativePath(d, f.FullName), f.LastWriteTimeUtc)
                |> List.ofSeq
        )

    /// The project's own most recently built `<assemblyName>.dll` — its path and
    /// mtime — across its per-TFM output dirs, or `None` when the project has not
    /// been built yet. `None` is NOT staleness: a missing artifact is the presence
    /// probe's business (a build in flight may still land it), and this gate only
    /// judges artifacts that exist. The newest is the right one to compare against:
    /// it is the one a rebuild would have just written.
    member _.OwnAssembly(projectDir: string, assemblyName: string) : (string * DateTime) option =
        assemblies.GetOrAdd(
            Path.Combine(projectDir, assemblyName),
            fun _ ->
                tfmOutputDirs (binDirOf projectDir)
                |> Array.choose (fun tfmDir ->
                    let dll = Path.Combine(tfmDir, assemblyName + ".dll")

                    if File.Exists dll then
                        Some(dll, File.GetLastWriteTimeUtc dll)
                    else
                        None)
                |> Array.toList
                |> function
                    | [] -> None
                    | built -> Some(built |> List.maxBy snd)
        )

/// The leaf name a project is known by (its directory name, which by convention
/// is also its assembly name).
let private projectLabel (projectDir: string) =
    Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

/// Is one project's contribution to `tfmDir` stale? `projectDir`/`assemblyName`
/// name a project in the test project's closure (possibly the test project
/// itself); `outputs` maps every file the build has placed under `tfmDir` to its
/// mtime, keyed by path relative to `tfmDir`.
let private staleContribution
    (cache: Cache)
    (tfmDir: string)
    (outputs: Map<string, DateTime>)
    (projectDir: string)
    (assemblyName: string)
    : StaleInput option =
    let sources = cache.FilesUnder projectDir

    // (1) COMPILE. The project's own assembly must be newer than every compile
    //     input it was built from. Judged against the project's OWN output, never
    //     the consumer's: a private-only edit to a dependency need not relink its
    //     consumers (reference assemblies exist precisely to avoid that), so
    //     comparing a dependency's source against the TEST project's DLL would be
    //     the same unanswerable accusation in a smaller costume.
    let staleCompile =
        match cache.OwnAssembly(projectDir, assemblyName) with
        | None -> None // not built yet — the presence probe's business, not ours
        | Some(_, assemblyMtime) ->
            sources
            |> List.filter (fun (rel, _) -> compileExtensions.Contains(Path.GetExtension(rel).ToLowerInvariant()))
            |> List.tryFind (fun (_, mtime) -> mtime > assemblyMtime)
            |> Option.map (fun (rel, mtime) ->
                AssemblyOlderThanSource(projectLabel projectDir, Path.Combine(projectDir, rel), mtime, assemblyMtime))

    // (2) COPY. Every file of this project that the build has copied into the test
    //     project's output dir must be no older than its origin. Keyed on the COPY
    //     existing, so a file the build does not copy is never asserted about —
    //     that is what makes a false positive unrepresentable here. Covers content
    //     and fixture items (its own, and those carried in transitively) …
    let staleCopy () =
        sources
        |> List.tryPick (fun (rel, originMtime) ->
            match outputs.TryFind rel with
            | Some copyMtime when copyMtime < originMtime ->
                Some(
                    CopyOlderThanOrigin(
                        Path.Combine(projectDir, rel),
                        Path.Combine(tfmDir, rel),
                        originMtime,
                        copyMtime
                    )
                )
            | _ -> None)

    // … and (3) the dependency's ASSEMBLY, which is copied into the consumer's
    //     output dir by the same mtime-preserving copy. Catches "only the
    //     dependency was rebuilt": its own DLL is fresh, but the copy the test run
    //     would actually load is not. (For the test project itself the copy IS the
    //     origin — same path, equal mtimes — so this is a no-op.)
    let staleAssemblyCopy () =
        let copy = Path.Combine(tfmDir, assemblyName + ".dll")

        let copyMtime =
            if File.Exists copy then
                Some(File.GetLastWriteTimeUtc copy)
            else
                None

        match cache.OwnAssembly(projectDir, assemblyName), copyMtime with
        | Some(origin, originMtime), Some copyMtime when copyMtime < originMtime ->
            Some(CopyOlderThanOrigin(origin, copy, originMtime, copyMtime))
        | _ -> None

    staleCompile
    |> Option.orElseWith staleCopy
    |> Option.orElseWith staleAssemblyCopy

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
/// answer is `InputsUndeterminable`, NOT `None`. A gate that reports "up to date"
/// because it could not look is the very bug it exists to prevent.
///
/// A multi-targeted project is stale only when EVERY per-TFM output dir is stale:
/// which TFM `dotnet run` selects is not knowable here, so a single fresh output
/// dir means there is a fresh way to run — conservative against false-stale, which
/// is the whole point.
let stale (cache: Cache) (target: RunnerTarget) : StaleInput option =
    let sw = Stopwatch.StartNew()

    // The closure: the test project itself, plus every project it transitively
    // references. Sources ANYWHERE ELSE in the repo are — by construction — not
    // inputs to this test binary and cannot make it stale.
    //
    // No project FILE (a `--project` naming a directory that holds none) is not
    // ignorance — there are no declared references to fail to read, and `dotnet
    // run` would itself fail on such a path. That directory's own files are the
    // inputs, and they are fully knowable.
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
        // FAIL CLOSED: we could not work out what this run's inputs are, so we
        // cannot certify it. Refuse, and let the build report the real error.
        | Error reason -> Some(InputsUndeterminable(target.AssemblyName, reason))
        | Ok _ when Array.isEmpty candidateTfmDirs -> None // nothing built to be stale — presence probe's business
        | Ok ordered ->
            // Stale iff NO output dir is fresh (see the multi-TFM note above).
            let perTfm =
                candidateTfmDirs
                |> Array.map (fun tfmDir ->
                    let outputs =
                        SafeWalk.enumerateFiles Set.empty tfmDir
                        |> Seq.map (fun f -> Path.GetRelativePath(tfmDir, f.FullName), f.LastWriteTimeUtc)
                        |> Map.ofSeq

                    ordered
                    |> List.tryPick (fun (projectDir, assemblyName) ->
                        staleContribution cache tfmDir outputs projectDir assemblyName))

            if Array.forall Option.isSome perTfm then
                Array.head perTfm
            else
                None

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
