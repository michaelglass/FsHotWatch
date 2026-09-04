/// A stable identity for the REPOSITORY a checkout belongs to — the namespace a
/// box-wide cache is partitioned by, so two workspaces of one repository share a
/// store and two unrelated repositories never do.
///
/// This is the one place a version control system is consulted, and it is consulted
/// exactly as the shape AUTOMATION-564 blesses: an ACCELERATOR with a fallback. jj
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

/// Strip the `worktrees/<name>` tail a git worktree's `gitdir:` pointer carries, so
/// every worktree of a repository resolves to the SAME `.git` directory. A pointer
/// that does not have that shape is already the shared directory.
let internal canonicalGitDir (gitDir: string) =
    let normalized = gitDir.Replace('\\', '/').TrimEnd('/')
    let marker = "/worktrees/"

    match normalized.LastIndexOf(marker, StringComparison.Ordinal) with
    | -1 -> normalized
    | index -> normalized.Substring(0, index)

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
        RepoIdentitySource.Jujutsu jjRepo
    else
        match tryReadPointer jjRepo with
        // A secondary jj workspace stores the absolute path of the shared repo
        // directory. Every workspace of the repository stores the SAME path.
        | Some pointer -> RepoIdentitySource.Jujutsu(pointer.Replace('\\', '/').TrimEnd('/'))
        | None ->
            if Directory.Exists gitPath then
                RepoIdentitySource.Git(canonicalGitDir gitPath)
            else
                match tryReadPointer gitPath with
                | Some pointer when pointer.StartsWith("gitdir:", StringComparison.Ordinal) ->
                    RepoIdentitySource.Git(canonicalGitDir (pointer.Substring("gitdir:".Length).Trim()))
                | _ -> RepoIdentitySource.CheckoutPath root

/// The string an identity is hashed from. Tagged by kind so a `.git` directory and a
/// checkout that merely happens to have the same path cannot collide.
let internal identitySource (source: RepoIdentitySource) =
    match source with
    | RepoIdentitySource.Jujutsu repoDir -> "jj:" + repoDir
    | RepoIdentitySource.Git gitDir -> "git:" + gitDir
    | RepoIdentitySource.CheckoutPath root -> "path:" + root

/// A filesystem-safe namespace for `repoRoot`'s repository: the checkout's directory
/// name (so a human can tell the directories apart) plus a digest of the identity
/// source (so telling them apart is not left to the name).
let namespaceOf (repoRoot: string) : string =
    let source = describe repoRoot

    let digest =
        (FsHotWatch.CheckCache.sha256Hex (identitySource source)).Substring(0, 16)

    let label =
        let name =
            try
                Path.GetFileName(Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar))
            with _ ->
                ""

        // A name is a convenience for whoever lists the cache directory, never part of
        // the identity — so anything that is not plainly safe is simply dropped.
        if
            not (String.IsNullOrWhiteSpace name)
            && name
               |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.')
        then
            name
        else
            "repo"

    $"%s{label}-%s{digest}"
