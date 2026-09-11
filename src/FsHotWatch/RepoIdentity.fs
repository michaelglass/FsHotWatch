/// A stable identity for the REPOSITORY a checkout belongs to — the namespace a
/// box-wide cache is partitioned by, so two workspaces of one repository share a
/// store and two unrelated repositories never do.
///
/// This is the one place a version control system is consulted, and it is consulted
/// exactly as the shape blesses: an ACCELERATOR with a fallback. jj
/// and git both record, in the checkout, where the shared repository lives — a
/// `.jj/repo` file in a secondary workspace, a `gitdir:` pointer in a git worktree.
/// Reading that pointer is a directory-layout question, not a `jj`/`git` invocation,
/// so it costs a `File.ReadAllText` and works with no VCS binary on PATH.
///
/// When nothing is recognised the identity falls back to the checkout's own path, so
/// an unknown layout gets a private namespace. The failure mode is "no sharing",
/// never "sharing with the wrong repository".
module FsHotWatch.RepoIdentity

open System
open System.IO

/// How a checkout's repository was identified. Carried out of `describe` so the
/// daemon can say, in one log line, whether sharing is actually going to happen.
[<RequireQualifiedAccess>]
type RepoIdentitySource =
    /// A jj workspace pointing at a shared repo directory, or a colocated `.jj/repo`.
    | Jujutsu of repoDir: string
    /// A git worktree's `gitdir:` pointer, or a plain `.git` directory.
    | Git of gitDir: string
    /// Nothing recognised: the checkout is its own repository as far as we can tell.
    | CheckoutPath of root: string

/// Read a one-line pointer file, returning None for anything unreadable or empty.
/// Never throws: an unreadable pointer means "not recognised", which the caller
/// turns into a private namespace.
let private tryReadPointer (path: string) =
    try
        if not (File.Exists path) then
            None
        else
            let text = File.ReadAllText(path).Trim()
            if String.IsNullOrEmpty text then None else Some text
    with _ ->
        None

/// Resolve pointer contents from the directory containing the pointer file.
/// Both jj and git may write relative paths; hashing the unresolved text aliases
/// identically laid-out workspaces in unrelated repositories.
let private resolvePointer (pointerFile: string) (pointer: string) =
    try
        Some(Path.GetFullPath(pointer, Path.GetDirectoryName pointerFile).Replace('\\', '/').TrimEnd('/'))
    with _ ->
        None

/// Git records a linked worktree's shared repository in `commondir`, relative
/// to its administrative directory (or absolute). A directory-name suffix is not
/// evidence: unrelated --separate-git-dir repositories may have that same shape.
/// Missing or unusable metadata retains a private identity for the original gitdir.
let internal canonicalGitDir (gitDir: string) =
    let normalized = gitDir.Replace('\\', '/').TrimEnd('/')
    let commonDirFile = Path.Combine(gitDir, "commondir")

    tryReadPointer commonDirFile
    |> Option.bind (resolvePointer commonDirFile)
    |> Option.defaultValue normalized

/// Identify the repository `repoRoot` is a checkout of.
let describe (repoRoot: string) : RepoIdentitySource =
    let root =
        try
            Path.GetFullPath repoRoot
        with _ ->
            repoRoot

    let jjRepo = Path.Combine(root, ".jj", "repo")
    let gitPath = Path.Combine(root, ".git")

    if Directory.Exists jjRepo then
        RepoIdentitySource.Jujutsu(jjRepo.Replace('\\', '/').TrimEnd('/'))
    else
        match tryReadPointer jjRepo |> Option.bind (resolvePointer jjRepo) with
        | Some repoDir -> RepoIdentitySource.Jujutsu repoDir
        | None ->
            if Directory.Exists gitPath then
                RepoIdentitySource.Git(canonicalGitDir gitPath)
            else
                match tryReadPointer gitPath with
                | Some pointer when pointer.StartsWith("gitdir:", StringComparison.Ordinal) ->
                    match resolvePointer gitPath (pointer.Substring("gitdir:".Length).Trim()) with
                    | Some gitDir -> RepoIdentitySource.Git(canonicalGitDir gitDir)
                    | None -> RepoIdentitySource.CheckoutPath root
                | _ -> RepoIdentitySource.CheckoutPath root

/// The string an identity is hashed from. Tagged by kind so a `.git` directory and a
/// checkout that merely happens to have the same path cannot collide.
let internal identitySource (source: RepoIdentitySource) =
    match source with
    | RepoIdentitySource.Jujutsu repoDir -> "jj:" + repoDir
    | RepoIdentitySource.Git gitDir -> "git:" + gitDir
    | RepoIdentitySource.CheckoutPath root -> "path:" + root

/// A filesystem-safe namespace derived only from repository identity. A checkout
/// label must not participate: differently named workspaces use this whole string
/// as their cache directory, not only its digest suffix.
let namespaceOf (repoRoot: string) : string =
    let digest = describe repoRoot |> identitySource |> FsHotWatch.CheckCache.sha256Hex

    $"repo-%s{digest}"
