/// Who a checkout IS, for a repository host that serves many worktrees at once.
///
/// Three identities, deliberately distinct:
///
/// - `RepositoryId` — the repository. A digest of the canonical COMMON metadata store
///   (the jj repo directory, or git's common directory) and its provider. Every jj
///   workspace and every git worktree of one repository resolves to the same store, so
///   to the same id; two independent clones each have their own store, so they never
///   collide — not even when they share an origin URL, because the remote is never
///   consulted.
/// - `WorktreeId` — one canonical physical root within that repository. Not a branch,
///   not a commit, not content: the same directory is the same worktree for as long as
///   it exists, and a worktree deleted and recreated at the same path is the same
///   worktree again.
/// - `SessionIncarnation` — a fresh nonce a host mints each time it attaches a
///   worktree, so a worktree that was detached and later reattached cannot accept a
///   completion addressed to its previous life.
///
/// Unlike `RepoIdentity` (the cache namespace, which degrades to a private namespace on
/// anything unrecognised), this module FAILS LOUDLY: a pointer that is unreadable,
/// malformed or dangling is an `IdentityError`, never a guess. A host keyed by the wrong
/// repository would route one repository's work into another's session registry.
///
/// Reading the layout is a directory question, not a `jj`/`git` invocation. The shapes
/// it reads, as `jj` 0.45 and `git` 2.55 write them:
///
/// - jj primary workspace: `<root>/.jj/repo` is a DIRECTORY (the shared repo).
/// - jj secondary workspace: `<root>/.jj/repo` is a FILE holding the path of the
///   primary's `.jj/repo`, relative to `<root>/.jj` (`../../../.jj/repo`).
/// - jj colocated with git: the primary also has a `.git` directory, and
///   `.jj/repo/store/git_target` points at it (`../../../.git`, relative to `store`).
/// - git main checkout: `<root>/.git` is a DIRECTORY.
/// - git linked worktree: `<root>/.git` is a FILE (`gitdir: <main>/.git/worktrees/<n>`),
///   and that directory's `commondir` file names the common directory (`../..`).
module FsHotWatch.RepositoryIdentity

open System
open System.IO
open System.Text

/// Why an identity could not be derived. Every case names the path it is about, so the
/// message a user reads says which file to look at.
[<RequireQualifiedAccess>]
type IdentityError =
    /// Nothing exists at this path (or a symlink on the way to it dangles).
    | PathNotFound of path: string
    /// The path exists but is not a directory, where a directory is required.
    | NotADirectory of path: string
    /// Resolving symlinks did not terminate within the hop budget.
    | SymlinkLoop of path: string
    /// The path cannot be made a path at all (an embedded NUL, say).
    | InvalidPath of path: string * reason: string
    /// A VCS metadata file exists but cannot be read or does not have its shape.
    | MalformedMetadata of file: string * reason: string
    /// A VCS pointer file names a directory that is not there.
    | DanglingPointer of file: string * target: string

module IdentityError =
    /// One human-readable line.
    let describe (error: IdentityError) : string =
        match error with
        | IdentityError.PathNotFound path -> $"no such file or directory: %s{path}"
        | IdentityError.NotADirectory path -> $"not a directory: %s{path}"
        | IdentityError.SymlinkLoop path -> $"too many levels of symbolic links resolving %s{path}"
        | IdentityError.InvalidPath(path, reason) -> $"not a usable path (%s{reason}): %A{path}"
        | IdentityError.MalformedMetadata(file, reason) -> $"malformed VCS metadata in %s{file}: %s{reason}"
        | IdentityError.DanglingPointer(file, target) -> $"%s{file} points at %s{target}, which is not a directory"

/// An absolute path in the ONE spelling the filesystem itself uses: every symlink on
/// the way resolved, `.`/`..` applied physically, and each component spelled as it is
/// stored on disk — so on a case-insensitive volume `/Users/x/Repo` and `/users/X/repo`
/// are the same value. Only `canonicalize` makes one.
type CanonicalPath =
    private
    | CanonicalPath of string

    member this.Value =
        let (CanonicalPath p) = this
        p

    override this.ToString() = this.Value

/// Symlink hops allowed while canonicalizing — the same budget POSIX `MAXSYMLINKS`
/// commonly gives, so a loop is an error rather than a hang.
[<Literal>]
let MaxSymlinkHops = 40

let private separators =
    [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]

/// Split an absolute path into its root and its components.
let private split (absolute: string) : string * string list =
    let root = Path.GetPathRoot absolute
    let rest = absolute.Substring(root.Length)
    root, rest.Split(separators, StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

let private nfc (s: string) = s.Normalize(NormalizationForm.FormC)

/// The target a symlink at `path` stores, or None when `path` is not a symlink. No
/// guard: `LinkTarget` answers null — it does not throw — for a missing entry, an
/// over-long name, a non-directory parent and an unsearchable parent alike.
let private linkTarget (path: string) : string option =
    FileInfo(path).LinkTarget |> Option.ofObj

/// Whether anything — file, directory or symlink, dangling or not — is at `path`.
let private entryExists (path: string) =
    File.Exists path || Directory.Exists path || (linkTarget path).IsSome

/// The name `name` is stored under inside the real directory `dir`, or None when
/// nothing answers to it. An exact match wins; otherwise the one entry equal to it
/// ignoring case and Unicode normalization (what a case-insensitive volume answered
/// to). A directory that cannot be listed keeps the name as given — the entry exists,
/// its stored spelling just cannot be learned.
let private storedName (dir: string) (name: string) : string option =
    let candidate = Path.Combine(dir, name)

    if not (entryExists candidate) then
        None
    else
        let entries =
            try
                Directory.EnumerateFileSystemEntries(dir)
                |> Seq.map Path.GetFileName
                |> Array.ofSeq
            with _ ->
                [||]

        // `Array.IndexOf` (ordinal for strings) and `.Length`, not `Array.contains` /
        // `Array.exists` and a `[| x |]` pattern: those compile a null-array guard that no
        // array here can reach.
        let answering =
            entries
            |> Array.filter (fun e -> String.Equals(nfc e, nfc name, StringComparison.OrdinalIgnoreCase))

        if Array.IndexOf(entries, name) >= 0 then Some name
        elif answering.Length = 1 then Some answering[0]
        else Some name

let private parentOf (path: string) =
    match Path.GetDirectoryName path with
    | null -> path
    | parent -> parent

/// Canonicalize `path` (relative paths are taken against the current directory). The
/// entry must exist: a canonical spelling of something that is not there is a guess.
let canonicalize (path: string) : Result<CanonicalPath, IdentityError> =
    // Past the empty and NUL checks nothing here throws on .NET: `IsPathRooted` and
    // `Combine` validate nothing else.
    let absolute =
        if String.IsNullOrEmpty path then
            Error(IdentityError.InvalidPath(string path, "empty"))
        elif path.Contains '\000' then
            Error(IdentityError.InvalidPath(path, "contains a NUL character"))
        elif Path.IsPathRooted path then
            Ok path
        else
            Ok(Path.Combine(Directory.GetCurrentDirectory(), path))

    // `current` is always a REAL directory (every component resolved), so `..` taken
    // against it is the physical parent — lexical `..` over a symlink would not be.
    let rec walk (current: string) (remaining: string list) (hops: int) =
        match remaining with
        | [] -> Ok(CanonicalPath current)
        | "." :: rest -> walk current rest hops
        | ".." :: rest -> walk (parentOf current) rest hops
        | name :: rest ->
            match storedName current name with
            | None -> Error(IdentityError.PathNotFound name)
            | Some stored ->
                let full = Path.Combine(current, stored)

                match linkTarget full with
                | Some _ when hops >= MaxSymlinkHops -> Error(IdentityError.SymlinkLoop path)
                | Some target ->
                    let root, components =
                        split (
                            if Path.IsPathRooted target then
                                target
                            else
                                Path.Combine(current, target)
                        )

                    walk root (components @ rest) (hops + 1)
                | None -> walk full rest hops

    absolute
    |> Result.bind (fun abs ->
        let root, components = split abs

        // Name the path the caller asked about, not the half-resolved spelling the walk
        // stopped at — that is the path the caller can recognise.
        match walk root components 0 with
        | Error(IdentityError.PathNotFound _) -> Error(IdentityError.PathNotFound path)
        | other -> other)

/// Which version control system owns a common store.
[<RequireQualifiedAccess>]
type VcsProvider =
    | Jujutsu
    | Git
    /// No recognised VCS: the checkout is its own repository.
    | Standalone

module VcsProvider =
    /// The stable tag hashed into a `RepositoryId`. Never change one: it would move
    /// every repository's control state.
    let tag (provider: VcsProvider) =
        match provider with
        | VcsProvider.Jujutsu -> "jj"
        | VcsProvider.Git -> "git"
        | VcsProvider.Standalone -> "standalone"

/// The directory a repository's metadata lives in, shared by all its worktrees.
type CommonStore =
    { Provider: VcsProvider
      Path: CanonicalPath }

/// Exactly `length` lowercase hex characters — the rendered form of every id here.
let internal isLowerHex (length: int) (s: string) =
    not (isNull s)
    && s.Length = length
    && s |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

/// Length, in hex characters, of a `RepositoryId` / `WorktreeId` (128 bits).
[<Literal>]
let IdLength = 32

let private digest (domain: string) (parts: string list) =
    (ContentHash.ofText (String.concat "\n" (domain :: parts))).Substring(0, IdLength)

/// The identity of a repository: a digest of its common store and provider.
type RepositoryId =
    private
    | RepositoryId of string

    member this.Value =
        let (RepositoryId v) = this
        v

    override this.ToString() = this.Value

module RepositoryId =
    let ofStore (store: CommonStore) : RepositoryId =
        RepositoryId(digest "fshw-repository/1" [ VcsProvider.tag store.Provider; store.Path.Value ])

    /// Strict parse of a rendered id: exactly `IdLength` lowercase hex characters.
    let tryParse (s: string) : RepositoryId option =
        if isLowerHex IdLength s then Some(RepositoryId s) else None

/// The identity of one worktree: its canonical physical root within its repository.
type WorktreeId =
    private
    | WorktreeId of string

    member this.Value =
        let (WorktreeId v) = this
        v

    override this.ToString() = this.Value

module WorktreeId =
    /// Scoped by the repository, so a path reused by a different repository is a
    /// different worktree.
    let ofRoot (repository: RepositoryId) (root: CanonicalPath) : WorktreeId =
        WorktreeId(digest "fshw-worktree/1" [ repository.Value; root.Value ])

    let tryParse (s: string) : WorktreeId option =
        if isLowerHex IdLength s then Some(WorktreeId s) else None

/// A host-minted nonce for one attachment of a worktree. Never derived from anything:
/// two attachments of the same worktree always differ.
type SessionIncarnation =
    private
    | SessionIncarnation of string

    member this.Value =
        let (SessionIncarnation v) = this
        v

    override this.ToString() = this.Value

module SessionIncarnation =
    let mint () : SessionIncarnation =
        SessionIncarnation(Guid.NewGuid().ToString("N"))

    let tryParse (s: string) : SessionIncarnation option =
        if isLowerHex 32 s then Some(SessionIncarnation s) else None

/// The handle a host issues for one attached worktree. Every RPC after the attach
/// carries it; a host answers only the incarnation it currently holds.
type SessionId =
    { Repository: RepositoryId
      Worktree: WorktreeId
      Incarnation: SessionIncarnation }

module SessionId =
    /// `<repository>.<worktree>.<incarnation>` — all three are hex, so `.` never
    /// appears inside a part.
    let render (id: SessionId) : string =
        $"%s{id.Repository.Value}.%s{id.Worktree.Value}.%s{id.Incarnation.Value}"

    let tryParse (s: string) : SessionId option =
        match (if isNull s then [||] else s.Split '.') with
        | [| r; w; i |] ->
            match RepositoryId.tryParse r, WorktreeId.tryParse w, SessionIncarnation.tryParse i with
            | Some r, Some w, Some i ->
                Some
                    { Repository = r
                      Worktree = w
                      Incarnation = i }
            | _ -> None
        | _ -> None

/// Which checkout of its repository a worktree is, read from the same entries that
/// locate its common store. jj is read first: a colocated repository carries `.git`
/// too, and only jj's pointer says which workspace this is.
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
    /// Neither `.jj` nor `.git`: a standalone repository of its own.
    | PlainDirectory

module CheckoutKind =
    /// A secondary checkout: a jj workspace other than the default, or a git worktree.
    let isSecondary (kind: CheckoutKind) : bool =
        match kind with
        | CheckoutKind.JjSecondaryWorkspace
        | CheckoutKind.GitWorktree -> true
        | CheckoutKind.JjDefaultWorkspace
        | CheckoutKind.GitMainCheckout
        | CheckoutKind.PlainDirectory -> false

    /// How a log line names a checkout kind, with the evidence it was read from.
    let describe (kind: CheckoutKind) : string =
        match kind with
        | CheckoutKind.JjDefaultWorkspace -> "the jj default workspace (.jj/repo is a directory)"
        | CheckoutKind.JjSecondaryWorkspace -> "a secondary jj workspace (.jj/repo is a file)"
        | CheckoutKind.GitMainCheckout -> "a git main checkout (.git is a directory)"
        | CheckoutKind.GitWorktree -> "a git worktree (.git is a file)"
        | CheckoutKind.PlainDirectory -> "a plain directory (no .jj or .git)"

/// A worktree root with everything derived from it.
type ResolvedWorktree =
    { Root: CanonicalPath
      Kind: CheckoutKind
      Store: CommonStore
      Repository: RepositoryId
      Worktree: WorktreeId }

/// Read a one-line pointer file (the first line, trimmed). Unreadable or empty is an
/// error: an unreadable pointer is not evidence of anything.
let private readPointer (file: string) : Result<string, IdentityError> =
    try
        let firstLine =
            File.ReadAllText(file).Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.map (fun l -> l.Trim())
            |> Option.defaultValue ""

        if String.IsNullOrEmpty firstLine then
            Error(IdentityError.MalformedMetadata(file, "empty"))
        else
            Ok firstLine
    with ex ->
        Error(IdentityError.MalformedMetadata(file, $"unreadable: %s{ex.GetType().Name}: %s{ex.Message}"))

/// Follow a pointer the way the tool that wrote it reads it back: relative targets are
/// taken against `baseDir`. The target must be an existing directory.
let private followPointer (file: string) (baseDir: string) (target: string) : Result<CanonicalPath, IdentityError> =
    let combined =
        if target.Contains '\000' then target
        elif Path.IsPathRooted target then target
        else Path.Combine(baseDir, target)

    match canonicalize combined with
    | Ok path when Directory.Exists path.Value -> Ok path
    | Ok _
    | Error(IdentityError.PathNotFound _) -> Error(IdentityError.DanglingPointer(file, target))
    | Error(IdentityError.InvalidPath(_, reason)) -> Error(IdentityError.MalformedMetadata(file, reason))
    | Error other -> Error other

/// Git's common directory for a (possibly per-worktree) git directory: the target of
/// its `commondir` file when it has one, else itself. Read from the file git writes,
/// not guessed from a `/worktrees/` segment in the path — a repository that merely
/// lives under a directory called `worktrees` must not be truncated to its parent.
let private gitCommonDir (gitDir: CanonicalPath) : Result<CanonicalPath, IdentityError> =
    let commondir = Path.Combine(gitDir.Value, "commondir")

    if File.Exists commondir then
        readPointer commondir |> Result.bind (followPointer commondir gitDir.Value)
    else
        Ok gitDir

/// When `gitCommon` is the `.git` of a jj repository colocated with it, that jj repo
/// directory — so a `git worktree add` made from a colocated repository is a worktree
/// of the SAME repository as its jj workspaces. Proven by jj's own `git_target`
/// pointing back at `gitCommon`, never by a `.jj` directory merely sitting beside it.
let private colocatedJjRepo (gitCommon: CanonicalPath) : CanonicalPath option =
    let jjRepo = Path.Combine(parentOf gitCommon.Value, ".jj", "repo")
    let store = Path.Combine(jjRepo, "store")
    let gitTarget = Path.Combine(store, "git_target")

    if Directory.Exists jjRepo && File.Exists gitTarget then
        match readPointer gitTarget |> Result.bind (followPointer gitTarget store) with
        | Ok target when target = gitCommon -> canonicalize jjRepo |> Result.toOption
        | _ -> None
    else
        None

/// The kind of the checkout rooted at the canonical directory `root`, and its common
/// store.
let private checkoutOf (root: CanonicalPath) : Result<CheckoutKind * CommonStore, IdentityError> =
    let jjDir = Path.Combine(root.Value, ".jj")
    let jjRepo = Path.Combine(jjDir, "repo")
    let dotGit = Path.Combine(root.Value, ".git")

    let jj path =
        { Provider = VcsProvider.Jujutsu
          Path = path }

    let git kind (gitDir: CanonicalPath) =
        gitCommonDir gitDir
        |> Result.map (fun common ->
            match colocatedJjRepo common with
            | Some jjRepo -> kind, jj jjRepo
            | None ->
                kind,
                { Provider = VcsProvider.Git
                  Path = common })

    if Directory.Exists jjRepo then
        canonicalize jjRepo
        |> Result.map (fun path -> CheckoutKind.JjDefaultWorkspace, jj path)
    elif File.Exists jjRepo then
        readPointer jjRepo
        |> Result.bind (followPointer jjRepo jjDir)
        |> Result.map (fun path -> CheckoutKind.JjSecondaryWorkspace, jj path)
    elif Directory.Exists jjDir then
        Error(IdentityError.MalformedMetadata(jjDir, "has no `repo` entry"))
    elif Directory.Exists dotGit then
        canonicalize dotGit |> Result.bind (git CheckoutKind.GitMainCheckout)
    elif File.Exists dotGit then
        readPointer dotGit
        |> Result.bind (fun line ->
            if line.StartsWith("gitdir:", StringComparison.Ordinal) then
                followPointer dotGit root.Value (line.Substring("gitdir:".Length).Trim())
            else
                Error(IdentityError.MalformedMetadata(dotGit, "a `.git` file must start with `gitdir:`")))
        |> Result.bind (git CheckoutKind.GitWorktree)
    else
        Ok(
            CheckoutKind.PlainDirectory,
            { Provider = VcsProvider.Standalone
              Path = root }
        )

/// Resolve the worktree rooted at `root` (the directory holding `.jj` / `.git`; a
/// directory with neither is a standalone repository of its own).
let resolveWorktree (root: string) : Result<ResolvedWorktree, IdentityError> =
    canonicalize root
    |> Result.bind (fun canonical ->
        if not (Directory.Exists canonical.Value) then
            Error(IdentityError.NotADirectory canonical.Value)
        else
            checkoutOf canonical
            |> Result.map (fun (kind, store) ->
                let repository = RepositoryId.ofStore store

                { Root = canonical
                  Kind = kind
                  Store = store
                  Repository = repository
                  Worktree = WorktreeId.ofRoot repository canonical }))

/// Where a repository host keeps its control state. A pure function of the state home
/// and the `RepositoryId`: never a path inside a worktree, so it outlives any one of
/// them and every worktree of the repository finds the same one.
type RepositoryControlPaths =
    {
        Directory: string
        LockFile: string
        PidFile: string
        IdentityFile: string
        HostLog: string
        /// The IPC endpoint (pipe) name. Short, because a Unix-domain socket path is
        /// length-limited and the runtime prefixes it.
        Endpoint: string
    }

/// Hex characters of the `RepositoryId` the endpoint name carries (64 bits — ample to
/// keep the repositories on one machine apart).
[<Literal>]
let EndpointIdLength = 16

let repositoryControlPaths (stateHome: string) (repository: RepositoryId) : RepositoryControlPaths =
    let dir = Path.Combine(stateHome, "repositories", repository.Value)

    { Directory = dir
      LockFile = Path.Combine(dir, "host.lock")
      PidFile = Path.Combine(dir, "host.pid")
      IdentityFile = Path.Combine(dir, "host.identity")
      HostLog = Path.Combine(dir, "host.log")
      Endpoint = $"fshw-repo-%s{repository.Value.Substring(0, EndpointIdLength)}" }

/// The repository's slice of the box-wide shared cache (immutable, content-keyed
/// artifacts shared by all its worktrees).
let repositorySharedCacheDir (cacheHome: string) (repository: RepositoryId) : string =
    Path.Combine(cacheHome, "repositories", repository.Value)

/// Where results for ONE worktree are materialized: that worktree's own `.fshw`, which
/// stays useful without a live host.
let worktreeResultRoot (worktree: ResolvedWorktree) : string = FsHwPaths.root worktree.Root.Value
