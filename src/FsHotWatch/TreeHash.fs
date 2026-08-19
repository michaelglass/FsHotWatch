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
/// to the recipe below — including a change to WHAT IS HASHED — MUST bump this.
///
/// v2 (AUTOMATION-164): a directory the walk could not see is now an entry of its
/// own. Under v1 it contributed nothing, so a tree with a permission hole in it
/// hashed identically to the same tree fully readable, and a verdict earned over
/// the part we could see applied to the part we could not.
[<Literal>]
let Algorithm = "fshw-tree-sha256-v2"

/// The content address of a tree, with the number of files that went into it and
/// the number of directories the walk could not see (a bare hash is unfalsifiable;
/// the counts make an empty/misrooted walk — and a TRUNCATED one — visible).
type Tree =
    { Hash: string
      FileCount: int
      SkippedCount: int }

/// What a walk of the verified tree found: its files as (repo-relative path,
/// absolute path) pairs, and the repo-relative path of every directory it could
/// NOT see. Both sorted by relative path (ordinal) — the sort is what makes the
/// hash reproducible across machines and filesystems.
type Walked =
    { Files: (string * string) list
      Skipped: string list }

/// Everything that makes up "the tree fshw verifies", INCLUDING the holes in it.
///
/// A hole is a directory `SafeWalk` could not enumerate, or one past its depth cap.
/// It is carried here rather than dropped because dropping it is what made a
/// partially-seen tree hash like a fully-seen one; `compute` folds it into the hash.
/// Excluded paths are filtered out of the holes too: a directory the config says we
/// do not verify is not a hole in what we do.
///
/// Uses the SAME primitives the daemon uses to decide what it watches
/// (`Discovery.existingDiscoveryRoots`, `SafeWalk`, `PathFilter.isExcludedPath`),
/// so the hashed set cannot drift away from the checked set.
let files (repoRoot: string) (excludePatterns: string list) : Walked =
    let isExcluded = PathFilter.isExcludedPath repoRoot excludePatterns

    let relativeTo (abs: string) =
        Path.GetRelativePath(repoRoot, abs).Replace('\\', '/')

    let sorted paths =
        paths |> List.sortWith (fun a b -> String.CompareOrdinal(fst a, fst b))

    let walked =
        Discovery.existingDiscoveryRoots repoRoot
        |> List.map (fun root -> SafeWalk.walk SafeWalk.SourceExcludedDirs "*" root)

    let filePaths =
        walked
        |> List.collect (fun w -> w.Files |> List.map (fun f -> f.FullName))
        |> List.filter (isExcluded >> not)

    let config = FsHwPaths.configFile repoRoot

    let all =
        if File.Exists config then
            config :: filePaths
        else
            filePaths

    { Files = all |> List.map (fun abs -> relativeTo abs, abs) |> sorted
      Skipped =
        walked
        |> List.collect (fun w -> w.Skipped |> List.map (fun s -> s.Path))
        |> List.filter (isExcluded >> not)
        |> List.map relativeTo
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)) }

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
///
/// A directory the walk could not see is an ENTRY, hashed to
/// `ContentHash.UnhashableContent` under its own path with a trailing `/` (a
/// relative path a FILE can never have, so a hole and a file never collide).
/// That is the same fail-closed answer `ContentHash` gives for an unreadable
/// FILE, applied one level up: a tree we could not fully see must not hash like
/// the tree we could, or a verdict earned over the visible part silently covers
/// the invisible part.
let compute (repoRoot: string) (excludePatterns: string list) : Tree =
    let walked = files repoRoot excludePatterns

    // `ContentHash` owns the sentinel policy for an unreadable file.
    let hashedFiles =
        walked.Files |> List.map (fun (rel, abs) -> rel, ContentHash.ofFile abs)

    let hashedHoles =
        walked.Skipped |> List.map (fun rel -> rel + "/", ContentHash.UnhashableContent)

    let entries =
        hashedFiles @ hashedHoles
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    { Hash = hashEntries entries
      FileCount = List.length hashedFiles
      SkippedCount = List.length hashedHoles }
