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
/// The fingerprint hashes the CONTENT of the resolved referenced-project DLLs (via
/// the project graph's canonical DLL paths), so a referenced project rebuilt against
/// a bumped package flips it even though its .fsproj text is unchanged. It also
/// folds in the test project's OWN direct `<PackageReference>` versions, so a bump
/// on the test project itself is caught before its DLL is observed.
module FsHotWatch.TestPrune.DependencyFanout

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

/// Compute a test project's dependency fingerprint: a hash over
///   • the content hashes of every transitively-referenced project's compiled DLL
///     (captures a referenced project rebuilt against a bumped package), and
///   • the test project's own direct PackageReference versions.
/// `graph` resolves references + DLL paths. Deterministic and order-insensitive.
let internal computeProjectFingerprint (graph: ProjectGraphAccessor) (testFsproj: string) : string =
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

    // `List.map`, NOT `List.choose`. A project whose DLL path the graph cannot resolve
    // must NOT be dropped: a dropped project contributes nothing, so the fingerprint
    // never moves when it changes, so a dependency bump inside it never fans out —
    // UNDER-SELECTION, the exact failure this module exists to prevent. It hashes to
    // `UnhashableContent` instead, the repo-wide I-could-not-read-it value, which can
    // never collide with a real digest, so missing→present flips the fingerprint
    // exactly once: the "rebuilt" signal.
    let dllHashes =
        referenced
        |> Set.toList
        |> List.map (fun proj ->
            match graph.GetCanonicalDllPath proj with
            | Some dll -> FsHotWatch.ContentHash.ofFile dll
            | None -> FsHotWatch.ContentHash.UnhashableContent)
        |> List.sort

    let pkgEntries =
        directPackageVersions testFsproj |> List.map (fun (n, v) -> $"pkg:%s{n}@%s{v}")

    let combined =
        String.concat "\n" ((dllHashes |> List.map (fun h -> $"dll:%s{h}")) @ pkgEntries)

    FsHotWatch.ContentHash.ofText combined

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
