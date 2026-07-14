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
///        built from it means the compile did not run since the edit. A `.fs` and
///        the `.dll` compiled from it have no content relation, so an mtime is the
///        ONLY signal available here — and this half of the module is right to use
///        one.
///   2. It COPIES files into the test project's output dir — dependency
///      assemblies, and content/fixture items (transitively, from referenced
///      projects).
///      ⇒ `CopyDiffersFromOrigin`: a copy whose BYTES are not the bytes of any
///        output it could have been copied from means the copy did not happen
///        since the edit.
///
/// ## Why a copy is judged by CONTENT and never by an mtime (AUTOMATION-169)
///
/// The copy check used to ask `copyMtime < originMtime`. It cried wolf again, and
/// the second wolf-cry came through the TARGET FRAMEWORK.
///
/// A multi-targeted dependency (`netstandard2.0; net8.0; net9.0; net10.0` — a
/// vendored SqlHydra fork) was consumed by a test project on net10.0. MSBuild
/// copies the dependency's net10.0 output; this gate resolved the origin to
/// whichever per-TFM output was NEWEST — which was net8.0, built nine minutes
/// later. So it compared a net10.0 copy against a net8.0 origin and condemned a
/// PERFECT copy. The message indicted itself: the origin it printed ended
/// `/net8.0/`, the destination `/net10.0/`. And no plain `dotnet build` could
/// answer it — a correct rebuild re-copies net10.0, so the accusation re-fired
/// forever. Four of six test projects refused to run.
///
/// Different TFMs of one project build at DIFFERENT TIMES, so an mtime comparison
/// ACROSS TFMs is not a bad heuristic — it is a CATEGORY ERROR. Correcting the
/// resolution would leave the error expressible; so the mtimes are gone from the
/// copy verdict entirely, and with them the whole bug class:
///
///   * TFM confusion — the question "which TFM did MSBuild pick?" is one this
///     graph-free module cannot answer (nearest-compatible-framework, netstandard
///     fallback), and it no longer has to ASK. Content is TFM-agnostic.
///   * a `jj`/`git` working-copy restamp of an unchanged file;
///   * coarse filesystem timestamps, and a rebuild inside one timestamp tick.
///
/// The rule, and it is ONE rule with two applications: **a copy is current iff its
/// bytes are the bytes of one of the outputs the build could have copied it from**
/// — for a content item, the file at that relative path in a closure project; for
/// a dependency assembly, any of that project's per-TFM outputs. That question
/// never mentions a target framework, so it cannot get one wrong. (Confirmed
/// against the real consumer, 2026-07-14: the fork's four per-TFM DLLs hash to
/// four DIFFERENT digests, and every consumer's copy is byte-identical to
/// net10.0's — so a genuinely stale copy matches NONE of them and is still caught.)
///
/// This is the same doctrine `TreeHash` already states — *"The hash is over
/// CONTENT, never mtimes. mtime is precisely what lied in APPLIC-24"* — and the
/// two sibling modules no longer give opposite advice. It uses core's ONE hasher,
/// `ContentHash`, and so inherits its fail-closed sentinel: a file we cannot read
/// is `InputsUndeterminable`, never "fresh".
///
/// Both are exactly what a plain `dotnet build` fixes, and NOTHING else is
/// asserted — so a file outside the closure cannot make the gate fire, and every
/// firing names a file a normal build will re-emit. Verified against real MSBuild
/// (2026-07-14): an out-of-closure edit leaves the test DLL untouched across
/// repeated incremental builds (the old false positive, now unrepresentable); an
/// in-closure edit re-emits the dependency's DLL and re-copies it into the
/// consumer's output dir; and a changed content item is re-copied likewise.
///
/// ## Content items — the fake green this also closes
///
/// The predecessor looked at `.fs`/`.cs` files only, so a changed test FIXTURE
/// copied in from a shared project was invisible to it: the tests ran
/// `--no-build` against the OLD copy still sitting in `bin/`, PASSED, and the red
/// only surfaced after a forced rebuild (intelligence, `dsa-scope-4.json`,
/// 2026-07-14 — a green merge that left main red for hours). `CopyDiffersFromOrigin`
/// covers content and dependency assemblies alike, because it keys on the COPY:
/// a file in a project's directory is only ever compared against a destination
/// that the build actually produced. A file the build does not copy has no
/// destination, so it can never make the gate fire.
///
/// ## Shadowing — when two projects claim one destination
///
/// A relative path can be claimed by SEVERAL projects in one closure
/// (`xunit.runner.json` sits in five of them in the consumer above). MSBuild copies
/// them all to the same destination and the last writer wins, so one copy survives
/// and the others are shadowed. A copy is therefore checked against EVERY claimant
/// in the closure and is current if it matches ANY — otherwise the shadowed project
/// would be condemned for a build doing exactly what it means to do, and no build
/// could answer the charge. (Under the old mtime rule this misfired only when the
/// shadowed file happened to be newer; under a content rule it would have been
/// PERMANENT, so the fix for one bug had to bring the guard for the other.)
///
/// This module is deliberately self-contained (on-disk `.fsproj` parse rather
/// than `IProjectGraphReader`): `executeTests` is graph-free — it is shared with
/// the one-off `run-tests` command, which has no daemon and no discovered graph —
/// and the graph tracks `Compile` items only, so it could not see content items
/// even if it were reachable.
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
    /// leaf — it may be the test project itself or any project in its closure.
    ///
    /// The one MTIME judgement left in this module, and the only one that can be
    /// made: a `.fs` source and the `.dll` compiled from it share no bytes, so
    /// there is nothing to compare but the clock.
    | AssemblyOlderThanSource of project: string * source: string * sourceMtime: DateTime * assemblyMtime: DateTime
    /// A file the build copies into the test project's output dir (a dependency
    /// assembly, or a content/fixture item — its own, or one carried in from a
    /// referenced project) does not hold the BYTES of any output it could have
    /// been copied from: the copy did not happen since the edit, so the run would
    /// read the old bytes.
    ///
    /// Deliberately carries NO MTIMES. Two per-TFM outputs of one project are
    /// built minutes apart, so comparing a copy's mtime against an origin's is
    /// meaningless unless their frameworks match — and this module cannot know
    /// which framework MSBuild chose (AUTOMATION-169). A verdict that has no
    /// mtimes in it cannot compare two of them across a TFM boundary: the error is
    /// not corrected here, it is UNREPRESENTABLE.
    | CopyDiffersFromOrigin of origin: string * copy: string
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
    | CopyDiffersFromOrigin(origin, copy) ->
        sprintf "%s has not been copied into the test output since it changed — %s holds different bytes" origin copy
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
/// `ConcurrentDictionary.GetOrAdd(key, valueFactory)` does NOT give that, and the
/// difference is the whole point of a memo. It is free to invoke the factory
/// CONCURRENTLY on several threads for the same key and publish only one result;
/// the losing threads still did the work, and it is thrown away. For a memo whose
/// entire job is to eliminate duplicated directory walks and `XDocument.Load`
/// parses, that eliminates nothing — and the duplicate is the NORMAL case here,
/// not a rare race: test groups genuinely run in parallel and their
/// `ProjectReference` closures overlap heavily, so they collide on the same cold
/// key by construction.
///
/// A `Lazy` under `ExecutionAndPublication` IS the guarantee: exactly one
/// execution, and every other caller blocks on it and takes its result. (The
/// dictionary may still construct several `Lazy` objects for one key — but a
/// `Lazy` is a cheap wrapper, and only the published one is ever forced.)
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
/// so each project is walked at most ONCE per run, even when several test projects
/// depend on it. Thread-safe: test groups run in parallel.
///
/// "At most once" is the `OnceMemo` guarantee, and it has to be: a plain
/// `ConcurrentDictionary.GetOrAdd` would let the parallel test groups each run the
/// same walk and discard all but one result, which is the cost this type exists to
/// avoid.
type Cache() =
    let closures = OnceMemo<string, Result<string list, string>>()
    let files = OnceMemo<string, (string * DateTime) list>()
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

    /// EVERY `<assemblyName>.dll` this project has built, one per per-TFM output
    /// dir. These are the outputs a consumer's copy of this assembly could have
    /// come from — and the gate deliberately does NOT try to work out WHICH of them
    /// MSBuild chose. That question needs the nearest-compatible-framework rules
    /// (a net10.0 consumer takes a netstandard2.0 dependency's netstandard2.0
    /// output quite happily), and a graph-free on-disk parse cannot answer it. It
    /// does not need to: the copy is judged against these outputs by CONTENT, and
    /// content does not care which framework produced it. Guessing here — picking
    /// the newest — is exactly what condemned four test projects in AUTOMATION-169.
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
    /// Used ONLY by the compile check, where the comparison is against the
    /// project's OWN sources and the NEWEST output is the lenient — and so
    /// conservative-against-false-stale — choice: if any framework was compiled
    /// after the edit, the compile ran. It must never be used as the ORIGIN of a
    /// copy: across TFMs, "newest" and "the one that was copied" are different
    /// files (AUTOMATION-169).
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

/// THE copy rule, and the only one: **is `copy` — the file the `--no-build` run
/// will actually load — byte-identical to one of the `candidates` the build could
/// have copied it from?**
///
/// `None` = current. `Some` = the run would read bytes no current build produces.
///
/// There is deliberately no mtime anywhere in here, and no attempt to work out
/// WHICH candidate MSBuild picked. Both are the same mistake in different clothes:
/// the module cannot know the chosen target framework, and the moment it guesses
/// (AUTOMATION-169: it took the newest, which was net8.0, and condemned a perfect
/// net10.0 copy) it starts making accusations no build can answer. It does not
/// need to know. If the copy's bytes are the bytes of ANY current output of its
/// origin, then the run loads code that matches the sources on disk — which is the
/// only question this gate was ever asking. If they match NONE of them, the copy
/// is old bytes whichever framework produced it, and the run must not happen.
///
/// FAILS CLOSED on a file it cannot read (`ContentHash`'s sentinel matches
/// nothing, so an unreadable file could otherwise masquerade as a mismatch — or,
/// worse, a match): "I could not read it" is `InputsUndeterminable`, never a
/// verdict.
let private copyVerdict (cache: Cache) (project: string) (copy: string) (candidates: string list) : StaleInput option =
    let copyHash = cache.Hash copy

    if not (ContentHash.isReadable copyHash) then
        Some(InputsUndeterminable(project, $"could not read the build's copy at %s{copy}"))
    else
        let hashed = candidates |> List.map (fun c -> c, cache.Hash c)

        if hashed |> List.exists (fun (_, h) -> h = copyHash) then
            None // the copy IS one of the origin's current outputs
        else
            // No match. Before calling it stale, make sure we could actually READ
            // everything we compared against — a mismatch we could not fully check
            // is ignorance, not evidence.
            match hashed |> List.tryFind (fun (_, h) -> not (ContentHash.isReadable h)) with
            | Some(unreadable, _) -> Some(InputsUndeterminable(project, $"could not read %s{unreadable}"))
            | None -> Some(CopyDiffersFromOrigin(List.head candidates, copy))

/// The origin candidates, ordered so the one the consumer most likely consumes is
/// FIRST — purely so a stale message names the framework the reader is looking at.
/// The VERDICT does not depend on this order (it is content, over the whole set);
/// only the wording does. Naming a net8.0 origin to a reader staring at a net10.0
/// output dir is what made the AUTOMATION-169 message read as nonsense.
let private consumerTfmFirst (tfmDir: string) (candidates: string list) : string list =
    let consumerTfm =
        Path.GetFileName(tfmDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

    let tfmOf (candidate: string) =
        Path.GetFileName(Path.GetDirectoryName candidate)

    let matching, rest =
        candidates
        |> List.partition (fun c -> String.Equals(tfmOf c, consumerTfm, StringComparison.OrdinalIgnoreCase))

    matching @ rest

/// Is one project's contribution to `tfmDir` stale? `projectDir`/`assemblyName`
/// name a project in the test project's closure (possibly the test project
/// itself); `outputs` holds the path — relative to `tfmDir` — of every file the
/// build has placed there.
///
/// `outputs` is a SET, not a map to mtimes. All the copy check ever asks of it is
/// *"did the build put something here?"*, because a file the build does not copy
/// has no destination and is never asserted about — that is what keeps a false
/// positive unrepresentable. What is at that destination is then settled by
/// content, so its mtime was never wanted.
let private staleContribution
    (cache: Cache)
    (tfmDir: string)
    (outputs: IReadOnlySet<string>)
    (closure: (string * string) list)
    (projectDir: string)
    (assemblyName: string)
    : StaleInput option =
    let sources = cache.FilesUnder projectDir
    let project = projectLabel projectDir

    /// Every file in the CLOSURE that the build could have copied to `rel` — this
    /// project's first, so a stale message names the project being judged.
    ///
    /// It is not always this project's alone. Two projects in one closure may hold
    /// a file at the SAME relative path (`xunit.runner.json` sits in five of them in
    /// the consumer this gate was fixed against); MSBuild copies both to the same
    /// destination and the last writer wins. Compare the surviving copy against only
    /// ONE claimant and the other is condemned for being SHADOWED — an accusation no
    /// build can answer, because the build is doing exactly what it means to do.
    ///
    /// So the copy is checked against every claimant, and is current if it matches
    /// ANY of them. That is not a loosening: the copy the run loads came from one of
    /// these files, and if it matches one of them it holds bytes a current build
    /// produces — which is the only thing this gate asserts. If it matches NONE, it
    /// is stale whoever wrote it.
    let claimantsOf (rel: string) =
        let mine = Path.Combine(projectDir, rel)

        let others =
            closure
            |> List.map (fun (dir, _) -> Path.Combine(dir, rel))
            |> List.filter (fun candidate -> candidate <> mine && File.Exists candidate)

        mine :: others

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
                AssemblyOlderThanSource(project, Path.Combine(projectDir, rel), mtime, assemblyMtime))

    // (2) COPY. Every file of this project that the build has copied into the test
    //     project's output dir must hold the bytes it was copied from. Keyed on the
    //     COPY existing, so a file the build does not copy is never asserted about
    //     — that is what makes a false positive unrepresentable here. Covers content
    //     and fixture items (its own, and those carried in transitively) …
    let staleCopy () =
        sources
        |> List.tryPick (fun (rel, _) ->
            if outputs.Contains rel then
                copyVerdict cache project (Path.Combine(tfmDir, rel)) (claimantsOf rel)
            else
                None)

    // … and (3) the dependency's ASSEMBLY, which the same copy carries into the
    //     consumer's output dir. Catches "only the dependency was rebuilt": its own
    //     DLL is fresh, but the copy the test run would actually load is not.
    //
    //     The candidates are ALL of the dependency's per-TFM outputs, because which
    //     one MSBuild copied is not knowable here — and, judged by content, does
    //     not need to be. (For the test project itself the copy IS one of the
    //     candidates — the very same path — so this is a no-op, as it should be.)
    let staleAssemblyCopy () =
        let copy = Path.Combine(tfmDir, assemblyName + ".dll")

        if not (File.Exists copy) then
            None // not copied yet — the presence probe's business, not ours
        else
            match cache.OwnAssemblyOutputs(projectDir, assemblyName) with
            | [] -> None // not built yet — likewise
            | candidates -> copyVerdict cache project copy (consumerTfmFirst tfmDir candidates)

    staleCompile
    |> Option.orElseWith staleCopy
    |> Option.orElseWith staleAssemblyCopy

/// Is the test project's output at `tfmDir` stale — i.e. is ANY project in its
/// closure contributing something out of date to it? `ordered` is that closure,
/// test project first.
let private staleInTfmDir (cache: Cache) (ordered: (string * string) list) (tfmDir: string) : StaleInput option =
    // A hash SET, not an F# `Map` and not a map to mtimes. This is read exactly one
    // way — "is there a copy at `rel`?" — and it is built from a full walk of the
    // output dir (hundreds of DLLs here, before content and fixtures). A `Map`
    // would pay O(n log n) and one heap node per file to build an ordering nothing
    // ever asks for; a map to mtimes would carry a value no caller may use any
    // more. `HashSet` builds in O(n) and answers the one question asked.
    let outputs =
        SafeWalk.enumerateFiles Set.empty tfmDir
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
            let perTfm = candidateTfmDirs |> Array.map (staleInTfmDir cache ordered)

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
