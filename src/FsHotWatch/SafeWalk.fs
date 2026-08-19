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
///     mounts).
///   * Never throws on a permission hole or a transient IO hiccup — one unreadable
///     subtree must not fault an otherwise good walk.
///   * A subtree it could not see is REPORTED, never silently dropped (AUTOMATION-164).
///     A walk that returns only files makes "I could not look inside this directory"
///     indistinguishable from "there is nothing in it", and every consumer downstream
///     reads an absence as clean: `TreeHash` hashes a tree it never fully saw, and the
///     freshness gate finds no source newer than the assembly and says FRESH. So the
///     walk is a stream of `WalkEntry`, and each caller states out loud what a
///     `Skipped` means for ITS question — the same shape `ContentHash` already uses for
///     an unreadable FILE, which this walker used to delete before the sentinel could
///     be reached.
module FsHotWatch.SafeWalk

open System.IO

/// Directories nested deeper than this are skipped (and reported). 64 levels
/// exceeds any sane repository layout by an order of magnitude; only a
/// filesystem cycle that somehow evades the symlink guard could reach it.
[<Literal>]
let MaxDepth = 64

/// Why a directory inside the walked scope contributed no entries.
type SkipReason =
    /// Its contents could not be listed — a permission hole, or an IO error.
    | Unreadable of message: string
    /// `MaxDepth` stopped the walk here; the subtree below was never entered.
    | DepthCapped

/// A part of the tree the walk could not see, and why. `Path` is absolute — the
/// caller relativises it against whatever root it asked about.
type SkippedDir = { Path: string; Reason: SkipReason }

/// One step of a walk: a file it saw, or a directory it could not look inside.
///
/// A DU rather than two collections because the walk is LAZY (callers that only
/// need existence stop at the first hit), and a `Skipped` list finalised only at
/// the end of enumeration is a list every early-stopping caller reads empty —
/// which is exactly the false "nothing to report" this type exists to prevent.
[<NoComparison>]
type WalkEntry =
    | Found of file: FileInfo
    | Skipped of skipped: SkippedDir

/// A walk run to completion: everything it saw, and everything it could not.
/// `Skipped` empty is the only proof the walk was total.
[<NoComparison>]
type WalkResult =
    { Files: FileInfo list
      Skipped: SkippedDir list }

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

/// One-line human phrasing of a skip, so every consumer that has to explain a
/// refusal says the same thing about the same hole.
let describeSkip (skipped: SkippedDir) : string =
    match skipped.Reason with
    | Unreadable message -> $"%s{skipped.Path} could not be read (%s{message}) — its contents were never seen"
    | DepthCapped ->
        $"%s{skipped.Path} is nested deeper than the depth cap (%d{MaxDepth}) — its subtree was never walked"

/// One directory read, with the two "I could not look" exceptions turned into
/// `Error`. Anything else is a bug in this walker or a broken runtime, and must
/// fault the caller rather than be laundered into an empty result.
///
/// ONE arm covering both types, not one arm each: they mean the same thing here and
/// get the same answer, and only `UnauthorizedAccessException` is forceable from a
/// test (a mode-000 directory). Split into two arms, the other gets a body line no
/// test can ever execute — a permanent coverage hole standing in for a distinction
/// this function does not make.
let private attemptRead (read: unit -> 'a[]) : Result<'a[], string> =
    try
        Ok(read ())
    with ex when (ex :? IOException) || (ex :? System.UnauthorizedAccessException) ->
        Error ex.Message

/// Lazily yields every entry under `root` (recursive, root included): each file matching
/// `searchPattern`, and each directory the walk could not see. Directories whose LEAF
/// NAME is in `excludedDirNames` are not walked and are not skips — they were never in
/// scope. Symlinked directories likewise. Empty for a missing root.
///
/// `searchPattern` carries the same glob semantics as `DirectoryInfo.GetFiles` but is
/// applied per-directory, since we own the recursion. Lazy, so callers that only need
/// existence (`Seq.exists`) can stop early.
let enumerateEntries (excludedDirNames: Set<string>) (searchPattern: string) (root: string) : seq<WalkEntry> =
    // A symlinked directory is a portal out of the tree, and possibly into a
    // cycle. Every caller wants the REAL tree under the root, so reparse points
    // are skipped wholesale — cheaper and more predictable than cycle-detecting
    // via an inode set.
    let inScope (d: DirectoryInfo) =
        not (excludedDirNames.Contains d.Name)
        && (d.Attributes &&& FileAttributes.ReparsePoint) = enum<FileAttributes> 0

    let rec walkDir (dir: DirectoryInfo) (depth: int) : seq<WalkEntry> =
        seq {
            // Both reads are attempted even when the first fails: a directory whose
            // files list but whose subdirectories do not still contributes its files.
            let files = attemptRead (fun () -> dir.GetFiles searchPattern)
            let subdirs = attemptRead (fun () -> dir.GetDirectories() |> Array.filter inScope)

            yield! files |> Result.defaultValue [||] |> Seq.map Found

            // ONE skip per unreadable directory, however many of its two reads failed:
            // the hole is the directory, and reporting it twice would let a consumer
            // counting holes disagree with one listing them.
            match files, subdirs with
            | Error message, _
            | _, Error message ->
                yield
                    Skipped
                        { Path = dir.FullName
                          Reason = Unreadable message }
            | Ok _, Ok _ -> ()

            for sub in subdirs |> Result.defaultValue [||] do
                if depth >= MaxDepth then
                    Logging.warn
                        "safewalk"
                        $"depth cap (%d{MaxDepth}) reached at %s{sub.FullName} — subtree skipped (filesystem cycle?)"

                    yield
                        Skipped
                            { Path = sub.FullName
                              Reason = DepthCapped }
                else
                    yield! walkDir sub (depth + 1)
        }

    let rootInfo = DirectoryInfo root

    if rootInfo.Exists then walkDir rootInfo 0 else Seq.empty

/// The COMPLETE answer for `root`: every file, and every directory the walk could not
/// see. For callers whose question is only sound over a tree they fully saw — the
/// content address of the tree (`TreeHash`), the `--no-build` freshness gate — and who
/// must therefore state a conclusion about `Skipped` rather than inherit an absence.
let walk (excludedDirNames: Set<string>) (searchPattern: string) (root: string) : WalkResult =
    let entries = enumerateEntries excludedDirNames searchPattern root |> List.ofSeq

    { Files =
        entries
        |> List.choose (function
            | Found f -> Some f
            | Skipped _ -> None)
      Skipped =
        entries
        |> List.choose (function
            | Skipped s -> Some s
            | Found _ -> None) }

/// Every file matching `searchPattern` under `root`, DISCARDING what the walk could not
/// see. Named for the choice it makes: best-effort is correct for enumeration that only
/// ever adds work (which files to watch, which projects to register) — a missed file
/// there is a file that goes unwatched, not a claim that it is clean. Any caller drawing
/// a CONCLUSION from the absence of files wants `walk` instead.
let bestEffortFilesMatching (excludedDirNames: Set<string>) (searchPattern: string) (root: string) : seq<FileInfo> =
    enumerateEntries excludedDirNames searchPattern root
    |> Seq.choose (function
        | Found f -> Some f
        | Skipped _ -> None)

/// `bestEffortFilesMatching` over every file (pattern `"*"`).
let bestEffortFiles (excludedDirNames: Set<string>) (root: string) : seq<FileInfo> =
    bestEffortFilesMatching excludedDirNames "*" root

/// Full paths of every file matching `searchPattern` under `root` — the
/// string-returning convenience for callers that only ever wanted paths
/// (project discovery, watch enumeration). Best-effort, as the name says.
let bestEffortFilePaths (excludedDirNames: Set<string>) (searchPattern: string) (root: string) : seq<string> =
    bestEffortFilesMatching excludedDirNames searchPattern root
    |> Seq.map (fun f -> f.FullName)
