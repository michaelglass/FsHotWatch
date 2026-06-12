/// Single source of truth for where the daemon looks for F# projects.
/// `fingerprintFsprojFiles` (the scan-skip guard), `discoverAndRegisterProjects`
/// (MSBuild evaluation), and `FileWatcher.create` (watch roots) must all agree
/// on these roots: if they drift, the watcher can see files discovery never
/// registers, or the fingerprint can churn on projects that are never loaded.
module internal FsHotWatch.Discovery

open System.IO

/// The repo-relative directories that are searched for .fsproj files and
/// watched for source changes.
let discoveryRoots (repoRoot: string) : string list =
    [ Path.Combine(repoRoot, "src"); Path.Combine(repoRoot, "tests") ]

/// The discovery roots that exist on disk.
let existingDiscoveryRoots (repoRoot: string) : string list =
    discoveryRoots repoRoot |> List.filter Directory.Exists

/// Every .fsproj under the existing discovery roots. Unfiltered: callers apply
/// their own exclude semantics (`PathFilter.isExcludedPath`).
let findFsprojFiles (repoRoot: string) : string list =
    existingDiscoveryRoots repoRoot
    |> List.collect (fun dir -> Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories) |> Array.toList)
