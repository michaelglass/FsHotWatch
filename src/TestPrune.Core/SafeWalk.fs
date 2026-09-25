/// Symlink-safe, depth-bounded recursive file enumeration — THE one walker for
/// every "files under this root" job in TestPrune (test-project discovery,
/// test-source scanning, project-file discovery).
///
/// Why this exists: `SearchOption.AllDirectories` FOLLOWS DIRECTORY SYMLINKS. In a
/// devenv/nix repo the reachable tree contains self-loop symlinks (e.g.
/// ncurses-6.6-dev/include/{ncurses,ncursesw} -> `.`), and each such loop DOUBLES
/// the path count per level, so an `AllDirectories` walk that reaches one is
/// effectively non-terminating. TestPrune's route extension hit exactly this and hung
/// `fshw check` for 8h36m — silent, no timeout, no error, no test ever launched.
///
/// Scoping the walk to a narrower root is NOT a fix, and "only scan tests/ to avoid
/// .devenv symlinks" is exactly the trap: `tests/*/bin/` holds Playwright's
/// Nix-provisioned browser symlinks, so the walk escapes into /nix/store from inside
/// `tests/` regardless. A portal anywhere under the root defeats a narrower root; only
/// refusing to traverse symlinked directories terminates.
///
/// Guarantee: never descends a symlinked (reparse-point) directory. The real
/// filesystem tree is acyclic, so termination is structural, not heuristic.
module TestPrune.SafeWalk

open System.IO

/// Directories deeper than this are skipped (with a warning on stderr). 64
/// levels is an order of magnitude beyond any sane repo layout; only a cycle
/// that somehow evaded the symlink guard (a bind mount, say) could reach it.
[<Literal>]
let MaxDepth = 64

/// Build output and tooling dirs — never hold the sources TestPrune cares
/// about, and are where the portals out of the repo live (`bin/.playwright`
/// symlinks into the Nix store; `.devenv`/`.direnv` link into /nix/store).
/// Excluded by NAME so a walk never even reaches the symlink guard for them.
let ExcludedDirs =
    set
        [ "bin"
          "obj"
          ".git"
          ".jj"
          ".hg"
          ".svn"
          ".devenv"
          ".direnv"
          ".fshw"
          "node_modules" ]

/// True when `dir` is the root of ANOTHER working copy nested inside the one being
/// walked: a jj workspace (`.jj`), an independent clone (a `.git` directory), or a git
/// worktree (a `.git` file pointing into some repository's `worktrees/`). Its files are
/// a different tree, often a full copy of this one at another revision, so reading
/// them makes a result depend on what else happens to be checked out alongside. A
/// submodule (a `.git` file pointing into `modules/`) is part of this tree and is
/// walked, as is a directory whose `.git` file cannot be read: unproven, it stays in.
let isNestedWorkingCopy (dir: DirectoryInfo) : bool =
    let gitPath = Path.Combine(dir.FullName, ".git")

    Directory.Exists(Path.Combine(dir.FullName, ".jj"))
    || Directory.Exists gitPath
    || (File.Exists gitPath
        && (try
                File.ReadAllText(gitPath).Replace('\\', '/').Contains "/worktrees/"
            with _ ->
                false))

/// Full paths of every file matching `searchPattern` under `root` (recursive,
/// root included), skipping `ExcludedDirs` by leaf name, nested working copies
/// (`isNestedWorkingCopy`), and NEVER entering a symlinked directory. Empty for a missing root. `searchPattern` has the same
/// glob semantics as `Directory.GetFiles` but is applied per-directory, since we
/// own the recursion. Do not reintroduce `AllDirectories`.
let enumerateFiles (searchPattern: string) (root: string) : string list =
    let rec walk (dir: DirectoryInfo) (depth: int) : seq<string> =
        seq {
            let files =
                try
                    dir.GetFiles(searchPattern) |> Array.map (fun f -> f.FullName)
                with
                | :? IOException
                | :? System.UnauthorizedAccessException -> [||]

            yield! files

            let subdirs =
                try
                    dir.GetDirectories()
                    |> Array.filter (fun d ->
                        not (ExcludedDirs.Contains d.Name)
                        // A symlinked directory is a portal out of the tree, and
                        // possibly into a cycle. Every caller wants the REAL tree.
                        && (d.Attributes &&& FileAttributes.ReparsePoint) = enum<FileAttributes> 0
                        && not (isNestedWorkingCopy d))
                with
                | :? IOException
                | :? System.UnauthorizedAccessException -> [||]

            for sub in subdirs do
                if depth >= MaxDepth then
                    eprintfn
                        $"[safewalk] depth cap (%d{MaxDepth}) reached at %s{sub.FullName} — subtree skipped (filesystem cycle?)"
                else
                    yield! walk sub (depth + 1)
        }

    let rootInfo = DirectoryInfo root

    if rootInfo.Exists then
        walk rootInfo 0 |> List.ofSeq
    else
        []
