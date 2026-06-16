/// Dependency-fingerprint project fanout for the daemon.
///
/// TestPrune's symbol diff is blind to a dependency/PackageReference change that
/// alters a project's BINARY behaviour without touching any F# symbol (e.g. a
/// CommandTree 0.6.3 → 0.7.0 bump): the dependent compiles against identical
/// symbols, the symbol diff is empty, and the zero-affected skip gate skips its
/// tests — letting a runtime-breaking bump enter the green baseline.
///
/// This module computes a per-test-project DEPENDENCY FINGERPRINT and, on a
/// `BuildCompleted`, fans out: every test project whose fingerprint moved since
/// the last build is force-run (all its tests), UNIONED with the symbol-precise
/// selection. Source-symbol edits keep their precise selection; only
/// dependency/binary changes trigger this project-coarse fanout.
///
/// The fingerprint is the HIGHER-FIDELITY variant: it hashes the CONTENT of the
/// resolved referenced-project DLLs (via the project graph's canonical DLL
/// paths), so a referenced project rebuilt against a bumped package flips the
/// fingerprint even though its .fsproj text is unchanged. It additionally folds
/// in the test project's OWN direct `<PackageReference>` versions so a package
/// bump on the test project itself is caught even before its DLL is observed.
module FsHotWatch.TestPrune.DependencyFanout

open System.IO
open System.Xml.Linq
open FsHotWatch.PluginFramework

/// Content hash of a file, or a stable sentinel when it can't be read (missing
/// DLL pre-first-build, locked, etc.). A missing→present transition flips the
/// hash exactly once, which is the desired "rebuilt" signal.
let private fileContentHash (path: string) : string =
    try
        if File.Exists path then
            FsHotWatch.CheckCache.sha256Hex (System.Convert.ToBase64String(File.ReadAllBytes path))
        else
            "missing"
    with _ ->
        "unreadable"

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

    let dllHashes =
        referenced
        |> Set.toList
        |> List.choose graph.GetCanonicalDllPath
        |> List.map fileContentHash
        |> List.sort

    let pkgEntries =
        directPackageVersions testFsproj |> List.map (fun (n, v) -> $"pkg:%s{n}@%s{v}")

    let combined =
        String.concat "\n" ((dllHashes |> List.map (fun h -> $"dll:%s{h}")) @ pkgEntries)

    FsHotWatch.CheckCache.sha256Hex combined

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
