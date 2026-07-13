/// Symlink-safe, depth-bounded recursive directory walking — THE one walker for
/// every "files under this root" job (project discovery, watch enumeration,
/// freshness scans, dependsOn globs).
///
/// Why this exists (2026-07-13 RCA): a hand-rolled recursive walk in the
/// test-prune freshness gate followed SYMLINKED DIRECTORIES. In a devenv/nix
/// repo `.devenv/profile` links into /nix/store, whose reachable tree contains
/// TWO SELF-LOOP symlinks in ONE directory (ncurses-6.6-dev/include/{ncurses,
/// ncursesw} -> `.`). That BRANCHES: each level doubles the path count, so
/// within the kernel's ~32-symlink ELOOP envelope there are ~2^32 distinct
/// paths to enumerate. The walk was effectively non-terminating and silently
/// wedged every `fshw check` (observed: 8h36m, no output, no timeout, no error).
///
/// `SearchOption.AllDirectories` has the SAME defect and is NOT a safe
/// alternative: the `SearchOption` overloads use `EnumerationOptions.Compatible`
/// (`AttributesToSkip = 0`), so they descend hidden dirs — `.devenv` included —
/// and they follow directory symlinks. Measured on the two-self-loop shape:
/// 800k+ files in 52s and still climbing. So `AllDirectories` and
/// `Directory.GetDirectories`-plus-recursion are both banned here; route every
/// repo-scale walk through this module.
///
/// Guarantees:
///   * NEVER descends into a symlinked (reparse-point) directory — the real
///     filesystem tree is acyclic, so termination is structural, not heuristic.
///   * Depth-capped as a belt for cycles that could evade the symlink guard
///     (e.g. bind mounts); a skipped subtree is LOGGED, never silently dropped.
///   * Per-subtree IO errors are swallowed (best-effort enumeration) — a
///     permission hole or transient hiccup must not fault the caller.
module FsHotWatch.SafeWalk

open System.IO

/// Directories nested deeper than this are skipped (with a warning). 64 levels
/// exceeds any sane repository layout by an order of magnitude; only a
/// filesystem cycle that somehow evades the symlink guard could reach it.
[<Literal>]
let MaxDepth = 64

/// Tooling/VCS directories that never hold repo sources, and that include the
/// known portals OUT of the repo tree: `.devenv`/`.direnv` symlink into
/// /nix/store (the 2026-07-13 wedge), `.workspaces` holds sibling jj checkouts
/// of the same repo. Excluded by NAME so a walk never even reaches the symlink
/// guard for them.
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

/// Lazily yields every file matching `searchPattern` under `root` (recursive,
/// root included), skipping directories whose LEAF NAME is in
/// `excludedDirNames`, and never entering symlinked directories. Empty for a
/// missing root. `searchPattern` carries the same glob semantics as
/// `DirectoryInfo.GetFiles` (`"*"`, `"*.fsproj"`, ...) but is applied
/// PER-DIRECTORY — the pattern behaves exactly as before while WE own the
/// recursion. Lazy, so callers that only need existence (`Seq.exists`) can stop
/// early without paying for the whole tree.
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
                        // The load-bearing guard: a symlinked directory is a
                        // portal out of the tree (and possibly into a cycle).
                        // Every caller wants the REAL tree under the root, so
                        // reparse points are skipped wholesale — cheaper and
                        // far more predictable than cycle-detecting via an
                        // inode set.
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
