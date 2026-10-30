/// Project-dependency fanout for test selection.
///
/// TestPrune's primary impact analysis is SOURCE-SYMBOL precise: a `.fs` edit
/// changes a symbol's content hash, the reverse dependency walk finds the tests
/// that reach it, and only those run. That is exactly right for source edits.
///
/// It is BLIND, however, to changes that alter a project's BINARY behaviour
/// without touching any F# symbol — most importantly a NuGet/PackageReference
/// version bump (e.g. CommandTree 0.6.3 → 0.7.0). The dependent project compiles
/// against identical symbols, so the symbol diff is empty and its tests are
/// skipped — yet the new package's runtime behaviour may break them.
///
/// This module closes that gap with a coarser, PROJECT-scoped fanout that runs
/// ALONGSIDE (unioned with) the symbol graph: each project carries a
/// `DependencyFingerprint` (a hash of its resolved package versions + the
/// fingerprints of the projects it references). When a project P's fingerprint
/// changes, every test in every test project that TRANSITIVELY ProjectReferences
/// P is selected. Source-symbol edits stay symbol-precise; only dependency/binary
/// changes get this project-scoped fanout. It is a strict SUPERSET of the symbol
/// selection, never a `RunAll`.
module TestPrune.ProjectFanout

open TestPrune.AstAnalyzer

/// A project's identity for fanout: its name, the projects it directly
/// ProjectReferences (by name), and an opaque `DependencyFingerprint`.
///
/// The fingerprint is computed by the CALLER (CLI / daemon) from the resolved
/// dependency closure — package versions plus the fingerprints of referenced
/// projects — so this core module stays free of MSBuild/NuGet concerns and is
/// purely a graph computation over opaque strings.
type ProjectInfo =
    { ProjectName: string
      ProjectReferences: string list
      DependencyFingerprint: string }

/// Deterministic hash of a project's resolved dependency surface: its
/// `PackageReference` set (name → version, including versions resolved from
/// `Directory.Packages.props` under CPM) plus the dependency fingerprints of the
/// projects it references. Folding referenced projects' fingerprints in makes the
/// hash TRANSITIVE — a package bump deep in the reference chain flips every
/// downstream project's fingerprint — so the caller can compute fingerprints in
/// topological order and have transitivity for free.
///
/// Order-insensitive in the inputs (both lists are sorted before hashing) so a
/// reordering of `<PackageReference>` items or project refs does not spuriously
/// flip the fingerprint. A package VERSION change does flip it; that is the whole
/// point.
let computeDependencyFingerprint
    (packageReferences: (string * string) list)
    (referencedProjectFingerprints: string list)
    : string =
    let pkgEntries =
        packageReferences
        |> List.map (fun (name, version) -> $"pkg:%s{name}@%s{version}")
        |> List.sort

    let refEntries =
        referencedProjectFingerprints |> List.map (fun fp -> $"ref:%s{fp}") |> List.sort

    let combined = String.concat "\n" (pkgEntries @ refEntries)

    let bytes =
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined))

    System.Convert.ToHexStringLower(bytes)

/// Projects whose `DependencyFingerprint` DIFFERS from a known prior value.
///
/// A project absent from `previous` is NOT reported: a first-ever index has no
/// baseline to diff against, and "never indexed" is already handled by the
/// existing new-file / RunAll path. Reporting it here would force a fanout on
/// every cold start. We report only a project that was seen before AND whose
/// fingerprint moved.
let diffFingerprints (previous: Map<string, string>) (current: ProjectInfo list) : Set<string> =
    current
    |> List.choose (fun p ->
        match Map.tryFind p.ProjectName previous with
        | Some prior when prior <> p.DependencyFingerprint -> Some p.ProjectName
        | _ -> None)
    |> Set.ofList

/// Transitive closure of "projects that (directly or indirectly) ProjectReference
/// a changed project", INCLUDING the changed projects themselves.
///
/// We walk the REVERSE of the ProjectReference edges: if `Build` references `Ops`
/// and `Ops`'s fingerprint changed, `Build` is affected; if `Build.Tests`
/// references `Build`, it is affected too. The changed project is included so a
/// test project whose OWN fingerprint changed (e.g. it bumped a package directly)
/// selects its own tests.
let affectedTestProjects (projects: ProjectInfo list) (changedProjects: Set<string>) : Set<string> =
    // Reverse adjacency: referenced project -> set of projects that reference it.
    let dependents =
        projects
        |> List.collect (fun p -> p.ProjectReferences |> List.map (fun r -> r, p.ProjectName))
        |> List.groupBy fst
        |> List.map (fun (referenced, pairs) -> referenced, pairs |> List.map snd |> Set.ofList)
        |> Map.ofList

    let rec walk (visited: Set<string>) (frontier: string list) : Set<string> =
        match frontier with
        | [] -> visited
        | node :: rest ->
            if Set.contains node visited then
                walk visited rest
            else
                let visited = Set.add node visited

                let next =
                    dependents
                    |> Map.tryFind node
                    |> Option.defaultValue Set.empty
                    |> fun s -> Set.difference s visited

                walk visited (Set.toList next @ rest)

    walk Set.empty (Set.toList changedProjects)

/// Every test method whose `TestProject` is in the affected-project closure of
/// `changedProjects`. This is the fanout selection: when a dependency fingerprint
/// changes, run ALL tests in the dependent test projects (project-coarse), to be
/// unioned with the symbol-precise selection by the caller.
let selectTestsForChangedProjects
    (projects: ProjectInfo list)
    (testMethods: TestMethodInfo list)
    (changedProjects: Set<string>)
    : TestMethodInfo list =
    let affected = affectedTestProjects projects changedProjects

    testMethods |> List.filter (fun t -> Set.contains t.TestProject affected)
