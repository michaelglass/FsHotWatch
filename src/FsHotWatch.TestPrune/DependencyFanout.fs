/// Dependency-fingerprint project fanout for the daemon.
///
/// TestPrune's symbol diff is blind to a dependency/PackageReference change that
/// alters a project's BINARY behaviour without touching any F# symbol (e.g. a
/// CommandTree 0.6.3 → 0.7.0 bump): the dependent compiles against identical
/// symbols, the symbol diff is empty, and the zero-affected skip gate skips its
/// tests — letting a runtime-breaking bump enter the green baseline.
///
/// So on a `BuildCompleted`, every test project whose per-project DEPENDENCY
/// FINGERPRINT moved since the last build is force-run in full, UNIONED with the
/// symbol-precise selection. Source-symbol edits keep their precise selection; only
/// dependency/binary changes trigger this project-coarse fanout.
///
/// The fingerprint hashes, for every referenced project, exactly the inputs the symbol
/// diff cannot see: its restored package graph (`obj/project.assets.json`), every
/// non-compiled file in its directory (the `.fsproj`, embedded resources, content, C#
/// sources, props/targets) and the `Directory.*` build files above it — see
/// `projectInputsHash`. Its compiled F# sources, and the DLL they rebuild, are left out:
/// the rebuilt DLL's bytes move with ANY edit, so hashing them fanned a comment out to a
/// whole-project run. It also folds in the test project's OWN direct
/// `<PackageReference>` versions, so a bump on the test project itself is caught.
module FsHotWatch.TestPrune.DependencyFanout

open System
open System.Collections.Generic
open System.IO
open System.Xml.Linq
open FsHotWatch.PluginFramework

/// Direct `<PackageReference>` (name, version) pairs declared inline in a
/// `.fsproj`. CPM references (no inline `Version`) contribute their name with an
/// empty version — the referenced-DLL hashes already capture the resolved CPM
/// version transitively, so we don't re-resolve `Directory.Packages.props` here.
/// Sorted for stability. Unreadable project → empty.
let internal directPackageVersions (fsprojPath: string) : (string * string) list =
    try
        let doc = XDocument.Load(fsprojPath)

        doc.Descendants(XName.Get "PackageReference")
        |> Seq.choose (fun el ->
            let inc = el.Attribute(XName.Get "Include")

            if inc = null then
                None
            else
                let ver = el.Attribute(XName.Get "Version")
                Some(inc.Value, (if ver = null then "" else ver.Value)))
        |> Seq.distinct
        |> Seq.sortBy fst
        |> Seq.toList
    with _ ->
        []

/// MSBuild imports these from every directory above a project, so editing one changes
/// what the project compiles without touching anything inside it.
let private importedBuildFiles =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props" ]

let private isProjectFile (path: string) =
    match Path.GetExtension(path: string).ToLowerInvariant() with
    | ".fsproj"
    | ".csproj"
    | ".vbproj" -> true
    | _ -> false

let private isHidden (path: string) =
    Path.GetFileName(path: string).StartsWith "."

/// A directory the walk does not descend into: build output (the restored package graph
/// under `obj/` is read on its own), hidden directories, and symbolic links (`.devenv`
/// links into the nix store).
let private isWalkedPast (dir: string) =
    let name = Path.GetFileName(dir: string)

    name.Equals("bin", StringComparison.OrdinalIgnoreCase)
    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
    || isHidden dir
    || not (isNull (DirectoryInfo(dir).LinkTarget))

/// `(path relative to the project directory, content hash)` for every file under it that
/// is an input the symbol diff does not see: everything but the compiled F# sources
/// (`isCompiled`) and hidden files. A subdirectory holding its own project file belongs
/// to that project: in the closure in its own right if referenced, and no input
/// otherwise. A directory that cannot be listed contributes `UnhashableContent` under its
/// own path rather than nothing, so it can never read as unchanged — and only itself: its
/// siblings are still read, so one locked directory cannot hide an edit beside it.
///
/// Only an IO failure reads as unreadable. Anything else propagates: swallowed, a bug in
/// the walk would turn every project into the same sentinel and silently stop fanout.
/// `enumerateFiles` lists one directory's files (`Directory.EnumerateFiles` outside tests).
let internal projectDirectoryInputs
    (enumerateFiles: string -> string seq)
    (projectDir: string)
    (isCompiled: string -> bool)
    : (string * string) list =
    let rec walk (dir: string) : (string * string) list =
        let relative (path: string) = Path.GetRelativePath(projectDir, path)

        try
            let entries = enumerateFiles dir |> List.ofSeq

            if dir <> projectDir && List.exists isProjectFile entries then
                []
            else
                let files =
                    entries
                    |> List.filter (fun f -> not (isHidden f) && not (isCompiled f))
                    |> List.map (fun f -> relative f, FsHotWatch.ContentHash.ofFile f)

                let below =
                    Directory.EnumerateDirectories dir
                    |> Seq.filter (isWalkedPast >> not)
                    |> List.ofSeq
                    |> List.collect walk

                files @ below
        with
        | :? IOException
        | :? UnauthorizedAccessException -> [ relative dir + "/", FsHotWatch.ContentHash.UnhashableContent ]

    walk projectDir

/// The imported build files present in the directories above `projectDir`, by full path.
let private importedBuildInputs (projectDir: string) : (string * string) list =
    let rec ancestors (dir: string) =
        match Path.GetDirectoryName dir with
        | null -> []
        | parent -> parent :: ancestors parent

    ancestors projectDir
    |> List.collect (fun dir -> importedBuildFiles |> List.map (fun name -> Path.Combine(dir, name)))
    |> List.filter File.Exists
    |> List.map (fun path -> path, FsHotWatch.ContentHash.ofFile path)

/// One referenced project's inputs that the symbol diff cannot see, as one hash:
///   • its resolved package graph, `obj/project.assets.json` (which carries every
///     package's version and sha512), or — for a project with no restore output — its
///     built DLL's bytes, the only remaining witness to a dependency change;
///   • every non-compiled file under its directory: the `.fsproj` itself, embedded
///     resources, content files, C# sources, props/targets;
///   • the `Directory.Build.*` / `Directory.Packages.props` files above it.
/// Compiled F# sources are left out: the symbol diff selects precisely on those, and
/// hashing them (or the DLL they rebuild, whose bytes move with any edit) made a comment
/// force-run every test project in full.
let private projectInputsHash (graph: ProjectGraphAccessor) (isCompiled: string -> bool) (project: string) : string =
    let projectDir = Path.GetDirectoryName(project: string)
    let assets = FsHotWatch.DepsFreshness.assetsPath project

    let packages =
        if File.Exists assets then
            "assets:" + FsHotWatch.ContentHash.ofFile assets
        else
            // `UnhashableContent` for a DLL that will not resolve, never a dropped entry:
            // missing -> present then flips the fingerprint exactly once (the "rebuilt"
            // signal), and an unresolvable project still counts.
            match graph.GetCanonicalDllPath project with
            | Some dll -> "dll:" + FsHotWatch.ContentHash.ofFile dll
            | None -> "dll:" + FsHotWatch.ContentHash.UnhashableContent

    let files =
        projectDirectoryInputs Directory.EnumerateFiles projectDir isCompiled
        @ importedBuildInputs projectDir
        |> List.sort
        |> List.map (fun (path, hash) -> $"file:%s{path}=%s{hash}")

    FsHotWatch.ContentHash.ofText (String.concat "\n" (packages :: files))

/// Fingerprints test projects against ONE reading of the project model, hashing each
/// referenced project at most once however many test projects share it. Build one per
/// build: what it memoizes is that build's view of the disk.
///
/// A test project's fingerprint is a hash over
///   • `projectInputsHash` of every transitively-referenced project, and
///   • the test project's own direct PackageReference versions.
/// Deterministic and order-insensitive.
///
/// With no project model, nothing is known to be compiled, so every source counts as an
/// input: an edit then fans out where the symbol diff would have been precise — slower,
/// never less safe.
let internal fingerprinter (graph: ProjectGraphAccessor) : string -> string =
    let compiled =
        graph.ObserveCheckableFiles()
        |> Option.map (snd >> Set.map FsHotWatch.Events.AbsFilePath.value)

    let isCompiled (path: string) =
        match compiled with
        | Some files ->
            Set.contains (FsHotWatch.Events.AbsFilePath.create path |> FsHotWatch.Events.AbsFilePath.value) files
        | None -> false

    let projectHashes = Dictionary<string, string>()

    let projectHash (project: string) =
        match projectHashes.TryGetValue project with
        | true, known -> known
        | false, _ ->
            let hash = projectInputsHash graph isCompiled project
            projectHashes[project] <- hash
            hash

    fun testFsproj ->
        // Transitive ProjectReference closure of the test project. The graph exposes
        // "dependents" (reverse) and "direct references" (forward); walk forward refs
        // transitively to collect every project whose binary this test links against.
        let rec collectRefs (visited: Set<string>) (frontier: string list) : Set<string> =
            match frontier with
            | [] -> visited
            | p :: rest ->
                if Set.contains p visited then
                    collectRefs visited rest
                else
                    let visited = Set.add p visited
                    let refs = graph.GetProjectReferences p
                    collectRefs visited (refs @ rest)

        let referenced = collectRefs Set.empty (graph.GetProjectReferences testFsproj)

        let projectEntries =
            referenced
            |> Set.toList
            |> List.map projectHash
            |> List.sort
            |> List.map (fun h -> $"project:%s{h}")

        let pkgEntries =
            directPackageVersions testFsproj |> List.map (fun (n, v) -> $"pkg:%s{n}@%s{v}")

        FsHotWatch.ContentHash.ofText (String.concat "\n" (projectEntries @ pkgEntries))

/// One test project's fingerprint (`fingerprinter` for a single project).
let internal computeProjectFingerprint (graph: ProjectGraphAccessor) (testFsproj: string) : string =
    fingerprinter graph testFsproj

/// Given the prior per-project fingerprints and the current ones, the set of test
/// projects whose fingerprint CHANGED (a project with no prior fingerprint is NOT
/// reported — the first build establishes the baseline, and cold-start full runs
/// already cover it). These are the projects to force-run.
let internal changedProjects (prior: Map<string, string>) (current: Map<string, string>) : Set<string> =
    current
    |> Map.toList
    |> List.choose (fun (proj, fp) ->
        match Map.tryFind proj prior with
        | Some old when old <> fp -> Some proj
        | _ -> None)
    |> Set.ofList

/// The fingerprints the next build compares against: this build's, and the prior one for
/// every project this build could not fingerprint. A build whose graph lacks a project
/// (an empty graph while it is being rediscovered, or a project not yet registered) has
/// not observed it; forgetting its prior would make the next observation a "first build"
/// that `changedProjects` never reports, so a dependency changed across that gap would
/// never fan out.
let internal advance (prior: Map<string, string>) (current: Map<string, string>) : Map<string, string> =
    Map.fold (fun kept project fingerprint -> Map.add project fingerprint kept) prior current
