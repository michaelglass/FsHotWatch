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

/// Resolve a pointer file's target against the directory the pointer lives in, the
/// way the tool that wrote it will read it back. jj writes a RELATIVE `.jj/repo`
/// pointer (`../../../.jj/repo`) and git may write a relative `gitdir:`; hashing the
/// pointer TEXT gave every same-depth workspace one identity and the default checkout
/// another, so no workspace ever shared a namespace with the checkout that seeded it.
let private resolvePointer (pointerFile: string) (pointer: string) =
    let target = pointer.Replace('\\', '/').TrimEnd('/')

    let resolved =
        try
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName pointerFile, target))
        with _ ->
            target

    resolved.Replace('\\', '/').TrimEnd('/')

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
        match tryReadPointer jjRepo with
        // A secondary jj workspace stores where the shared repo directory is. Every
        // workspace of the repository resolves to the SAME directory.
        | Some pointer -> RepoIdentitySource.Jujutsu(resolvePointer jjRepo pointer)
        | None ->
            if Directory.Exists gitPath then
                RepoIdentitySource.Git(canonicalGitDir gitPath)
            else
                match tryReadPointer gitPath with
                | Some pointer when pointer.StartsWith("gitdir:", StringComparison.Ordinal) ->
                    RepoIdentitySource.Git(
                        canonicalGitDir (resolvePointer gitPath (pointer.Substring("gitdir:".Length).Trim()))
                    )
                | _ -> RepoIdentitySource.CheckoutPath root

/// Which checkout of a repository a directory is, read from the filesystem.
[<RequireQualifiedAccess>]
type CheckoutKind =
    /// `.jj/repo` is a directory: the workspace that owns the store.
    | JjDefaultWorkspace
    /// `.jj/repo` is a file pointing at another workspace's store (`jj workspace add`).
    | JjSecondaryWorkspace
    /// `.git` is a directory.
    | GitMainCheckout
    /// `.git` is a `gitdir:` file (`git worktree add`).
    | GitWorktree
    /// Neither `.jj` nor `.git` is recognised.
    | PlainDirectory

/// Classify `root`. jj is read first: a colocated repository carries `.git` too, and
/// only jj's pointer says which workspace this is. Config cannot answer this —
/// `.fshw.json` is tracked, so every workspace reads the same one.
let checkoutKind (root: string) : CheckoutKind =
    let jjRepo = Path.Combine(root, ".jj", "repo")
    let git = Path.Combine(root, ".git")

    if Directory.Exists jjRepo then
        CheckoutKind.JjDefaultWorkspace
    elif File.Exists jjRepo then
        CheckoutKind.JjSecondaryWorkspace
    elif Directory.Exists git then
        CheckoutKind.GitMainCheckout
    elif File.Exists git then
        CheckoutKind.GitWorktree
    else
        CheckoutKind.PlainDirectory

/// A secondary checkout: a jj workspace other than the default, or a git worktree.
let isSecondaryCheckout (kind: CheckoutKind) : bool =
    match kind with
    | CheckoutKind.JjSecondaryWorkspace
    | CheckoutKind.GitWorktree -> true
    | CheckoutKind.JjDefaultWorkspace
    | CheckoutKind.GitMainCheckout
    | CheckoutKind.PlainDirectory -> false

/// How a startup log names a checkout kind, with the evidence it was read from.
let describeCheckoutKind (kind: CheckoutKind) : string =
    match kind with
    | CheckoutKind.JjDefaultWorkspace -> "the jj default workspace (.jj/repo is a directory)"
    | CheckoutKind.JjSecondaryWorkspace -> "a secondary jj workspace (.jj/repo is a file)"
    | CheckoutKind.GitMainCheckout -> "a git main checkout (.git is a directory)"
    | CheckoutKind.GitWorktree -> "a git worktree (.git is a file)"
    | CheckoutKind.PlainDirectory -> "a plain directory (no .jj or .git)"

/// The string an identity is hashed from. Tagged by kind so a `.git` directory and a
/// checkout that merely happens to have the same path cannot collide.
let internal identitySource (source: RepoIdentitySource) =
    match source with
    | RepoIdentitySource.Jujutsu repoDir -> "jj:" + repoDir
    | RepoIdentitySource.Git gitDir -> "git:" + gitDir
    | RepoIdentitySource.CheckoutPath root -> "path:" + root

/// The repository's own directory name, read off the identity: the directory that
/// holds `.jj` or `.git`, or the checkout itself when nothing was recognised. The
/// same for every checkout of one repository, unlike the checkout's own name.
/// `GetDirectoryName`/`GetFileName` never throw: on a path with nothing above it
/// they return null, which `namespaceOf` treats as "no usable name".
let private repositoryName (source: RepoIdentitySource) =
    match source with
    | RepoIdentitySource.Jujutsu repoDir -> Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName repoDir))
    | RepoIdentitySource.Git gitDir -> Path.GetFileName(Path.GetDirectoryName gitDir)
    | RepoIdentitySource.CheckoutPath root -> Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))

/// A filesystem-safe namespace for `repoRoot`'s repository: the REPOSITORY's directory
/// name (so a human can tell the directories apart) plus a digest of the identity
/// source (so telling them apart is not left to the name). Both halves come from the
/// identity, never from the checkout: this string IS the shared store's directory, so
/// anything checkout-specific in it would give every workspace a private store — which
/// is what the checkout's own name did.
let namespaceOf (repoRoot: string) : string =
    let source = describe repoRoot

    let digest =
        (FsHotWatch.CheckCache.sha256Hex (identitySource source)).Substring(0, 16)

    let label =
        let name = repositoryName source

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
