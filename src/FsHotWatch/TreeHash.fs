/// The content address of the tree fshw verifies.
///
/// A verdict is a claim about a PARTICULAR tree, so it carries the hash of the tree it
/// verified and the consumer's rule is total: if `treeHash ≠ hash(current tree)`, the
/// verdict does not apply — never reuse it.
///
/// WHAT IS HASHED — everything under the discovery roots (`src/`, `tests/`) that is not
/// build output, tooling state, or excluded by config, PLUS `.fshw.json` itself. That
/// includes CONTENT/FIXTURE files, deliberately: a changed fixture MSBuild declines to
/// re-copy can otherwise let a suite run green against the OLD fixture.
///
/// The hash is over CONTENT, never mtimes: a checkout, a `touch`, or a filesystem with
/// coarse timestamps all move the mtime without changing what the compiler sees, and
/// MSBuild's up-to-date check trusts exactly that mtime.
module FsHotWatch.TreeHash

open System
open System.IO
open System.Text

/// Identifies the hashing scheme, so a consumer can tell a hash it understands
/// from one it doesn't rather than comparing two incomparable strings. A change
/// to the recipe below MUST bump this.
[<Literal>]
let Algorithm = "fshw-tree-sha256-v1"

/// The content address of a tree, with the number of files that went into it
/// (a bare hash is unfalsifiable; a count makes an empty/misrooted walk visible).
type Tree = { Hash: string; FileCount: int }

/// Every file that makes up "the tree fshw verifies", as (repo-relative path,
/// absolute path) pairs sorted by the relative path (ordinal). Deterministic —
/// the sort is what makes the hash reproducible across machines and filesystems.
///
/// Uses the SAME primitives the daemon uses to decide what it watches
/// (`Discovery.existingDiscoveryRoots`, `SafeWalk`, `PathFilter.isExcludedPath`),
/// so the hashed set cannot drift away from the checked set.
let files (repoRoot: string) (excludePatterns: string list) : (string * string) list =
    let isExcluded = PathFilter.isExcludedPath repoRoot excludePatterns

    let walked =
        Discovery.existingDiscoveryRoots repoRoot
        |> List.collect (fun root -> SafeWalk.enumerateFilePaths SafeWalk.SourceExcludedDirs "*" root |> List.ofSeq)
        |> List.filter (isExcluded >> not)

    let config = FsHwPaths.configFile repoRoot

    let all = if File.Exists config then config :: walked else walked

    all
    |> List.map (fun abs ->
        let rel = Path.GetRelativePath(repoRoot, abs).Replace('\\', '/')
        rel, abs)
    |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

/// The content address of `entries` — the pure half, so the recipe is testable
/// without a filesystem.
///
/// RECIPE (this is a documented contract; consumers may reimplement it):
///   for each (relPath, contentHash), ordered by relPath (ordinal):
///       relPath + NUL + contentHash + LF
///   treeHash = "sha256:" + lowerhex(sha256(utf8(that concatenation)))
///
/// The separator is NUL, not a space: a path may contain spaces, and a separator
/// that can occur inside a field lets two different trees produce one byte stream.
let hashEntries (entries: (string * string) list) : string =
    let sb = StringBuilder()

    for (rel, contentHash) in entries do
        sb.Append(rel).Append('\000').Append(contentHash).Append('\n') |> ignore

    "sha256:" + ContentHash.ofText (sb.ToString())

/// The content address of the repo's current tree.
let compute (repoRoot: string) (excludePatterns: string list) : Tree =
    let entries = files repoRoot excludePatterns

    // `ContentHash` owns the sentinel policy for an unreadable file.
    let hashed = entries |> List.map (fun (rel, abs) -> rel, ContentHash.ofFile abs)

    { Hash = hashEntries hashed
      FileCount = List.length hashed }
