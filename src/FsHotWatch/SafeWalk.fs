/// Symlink-safe, depth-bounded recursive directory walking — the one walker for every
/// "files under this root" job (project discovery, watch enumeration, freshness scans,
/// dependsOn globs).
///
/// Naive recursive walks follow SYMLINKED DIRECTORIES, and in a devenv/nix repo
/// `.devenv/profile` links into /nix/store, whose reachable tree contains self-loop
/// symlinks (e.g. ncurses `include/{ncurses,ncursesw} -> .`). Those branch — each level
/// doubles the path count — so within the kernel's ~32-symlink ELOOP envelope there are
/// ~2^32 distinct paths and the walk never terminates; it silently wedged `fshw check`.
///
/// `SearchOption.AllDirectories` has the SAME defect: those overloads use
/// `EnumerationOptions.Compatible` (`AttributesToSkip = 0`), so they descend hidden
/// dirs (`.devenv` included) and follow directory symlinks — measured at 800k+ files in
/// 52s and still climbing. `AllDirectories` and `Directory.GetDirectories`-plus-
/// recursion are both banned; route every repo-scale walk through this module.
///
/// Guarantees:
///   * Never descends into a symlinked (reparse-point) directory — the real filesystem
///     tree is acyclic, so termination is structural, not heuristic.
///   * Depth-capped as a belt for cycles that could evade the symlink guard (e.g. bind
///     mounts); a skipped subtree is logged, never silently dropped.
///   * Per-subtree IO errors are swallowed (best-effort enumeration) — a permission
///     hole or transient hiccup must not fault the caller.
module FsHotWatch.SafeWalk

open System.IO

/// Directories nested deeper than this are skipped (with a warning). 64 levels
/// exceeds any sane repository layout by an order of magnitude; only a
/// filesystem cycle that somehow evades the symlink guard could reach it.
[<Literal>]
let MaxDepth = 64

/// Tooling/VCS directories that never hold repo sources, and that include the
/// known portals OUT of the repo tree: `.devenv`/`.direnv` symlink into
/// /nix/store, `.workspaces` holds sibling jj checkouts of the same repo.
/// Excluded by NAME so a walk never even reaches the symlink guard for them.
let ToolingExcludedDirs =
    set
        [ ".git"
          ".jj"
          ".hg"
          ".svn"
          ".fshw"
          ".vs"
          ".idea"
          "node_modules"
          ".devenv"
          ".direnv"
          ".workspaces" ]

/// `ToolingExcludedDirs` plus build output. Used where a generated artifact must
/// never masquerade as a source — the test-prune freshness scan, where a
/// regenerated `obj/` file would otherwise read as a newer "source" and pin
/// every test project permanently stale.
let SourceExcludedDirs = Set.union ToolingExcludedDirs (set [ "bin"; "obj" ])

/// Lazily yields every file matching `searchPattern` under `root` (recursive, root
/// included), skipping directories whose LEAF NAME is in `excludedDirNames`, and never
/// entering symlinked directories. Empty for a missing root. `searchPattern` carries
/// the same glob semantics as `DirectoryInfo.GetFiles` but is applied per-directory,
/// since we own the recursion. Lazy, so callers that only need existence
/// (`Seq.exists`) can stop early.
let enumerateFilesMatching (excludedDirNames: Set<string>) (searchPattern: string) (root: string) : seq<FileInfo> =
    let rec walk (dir: DirectoryInfo) (depth: int) : seq<FileInfo> =
        seq {
            let files =
                try
                    dir.GetFiles(searchPattern)
                with
                | :? IOException
                | :? System.UnauthorizedAccessException -> [||]

            yield! files

            let subdirs =
                try
                    dir.GetDirectories()
                    |> Array.filter (fun d ->
                        not (excludedDirNames.Contains d.Name)
                        // A symlinked directory is a portal out of the tree, and
                        // possibly into a cycle. Every caller wants the REAL tree under
                        // the root, so reparse points are skipped wholesale — cheaper
                        // and more predictable than cycle-detecting via an inode set.
                        && (d.Attributes &&& FileAttributes.ReparsePoint) = enum<FileAttributes> 0)
                with
                | :? IOException
                | :? System.UnauthorizedAccessException -> [||]

            for sub in subdirs do
                if depth >= MaxDepth then
                    Logging.warn
                        "safewalk"
                        $"depth cap (%d{MaxDepth}) reached at %s{sub.FullName} — subtree skipped (filesystem cycle?)"
                else
                    yield! walk sub (depth + 1)
        }

    let rootInfo = DirectoryInfo root

    if rootInfo.Exists then walk rootInfo 0 else Seq.empty

/// `enumerateFilesMatching` over every file (pattern `"*"`).
let enumerateFiles (excludedDirNames: Set<string>) (root: string) : seq<FileInfo> =
    enumerateFilesMatching excludedDirNames "*" root

/// Full paths of every file matching `searchPattern` under `root` — the
/// string-returning convenience for callers that only ever wanted paths
/// (project discovery, watch enumeration).
let enumerateFilePaths (excludedDirNames: Set<string>) (searchPattern: string) (root: string) : seq<string> =
    enumerateFilesMatching excludedDirNames searchPattern root
    |> Seq.map (fun f -> f.FullName)
