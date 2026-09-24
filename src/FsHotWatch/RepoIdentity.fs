/// The namespace a box-wide cache is partitioned by, so two workspaces of one
/// repository share a store and two unrelated repositories never do.
///
/// The layout is read by `RepositoryIdentity.resolveWorktree`, the one reader of a
/// checkout's `.jj` / `.git` entries: the namespace is keyed by the same canonical
/// common store as the `RepositoryId`. Where that reader fails loudly, this module
/// falls back: a checkout whose layout cannot be read gets a private namespace. The
/// failure mode is "no sharing", never "sharing with the wrong repository".
module FsHotWatch.RepoIdentity

open System
open System.IO
open FsHotWatch.RepositoryIdentity

/// Where a checkout's cache namespace comes from.
[<RequireQualifiedAccess>]
type RepoIdentitySource =
    /// The repository's common store, shared by every checkout of it.
    | Store of CommonStore
    /// The layout could not be read: the checkout (as given, made absolute) is its
    /// own namespace.
    | Unreadable of root: string * error: IdentityError

/// Identify the repository `repoRoot` is a checkout of.
let describe (repoRoot: string) : RepoIdentitySource =
    match resolveWorktree repoRoot with
    | Ok worktree -> RepoIdentitySource.Store worktree.Store
    | Error error ->
        let root =
            try
                Path.GetFullPath repoRoot
            with _ ->
                repoRoot

        RepoIdentitySource.Unreadable(root, error)

/// The string an identity is hashed from. Tagged by kind so a store and a checkout
/// that merely happens to have the same path cannot collide.
let internal identitySource (source: RepoIdentitySource) =
    match source with
    | RepoIdentitySource.Store store -> VcsProvider.tag store.Provider + ":" + store.Path.Value
    | RepoIdentitySource.Unreadable(root, _) -> "path:" + root

/// The repository's own directory name, read off the identity: the directory that
/// holds `.jj` or `.git`, or the checkout itself for a standalone or unreadable one.
/// The same for every checkout of one repository, unlike the checkout's own name.
/// `GetDirectoryName`/`GetFileName` never throw: on a path with nothing above it
/// they return null, which `namespaceOf` treats as "no usable name".
let private repositoryName (source: RepoIdentitySource) =
    match source with
    | RepoIdentitySource.Store store ->
        match store.Provider with
        | VcsProvider.Jujutsu -> Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName store.Path.Value))
        | VcsProvider.Git -> Path.GetFileName(Path.GetDirectoryName store.Path.Value)
        | VcsProvider.Standalone -> Path.GetFileName store.Path.Value
    | RepoIdentitySource.Unreadable(root, _) -> Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))

/// A filesystem-safe namespace for `repoRoot`'s repository: the REPOSITORY's directory
/// name (so a human can tell the directories apart) plus a digest of the identity
/// source (so telling them apart is not left to the name). Both halves come from the
/// identity, never from the checkout: this string IS the shared store's directory, so
/// anything checkout-specific in it would give every workspace a private store.
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
