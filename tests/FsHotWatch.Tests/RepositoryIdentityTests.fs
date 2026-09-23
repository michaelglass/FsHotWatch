/// Repository, worktree and session identities for a repository host.
///
/// The load-bearing claims, each pinned in both directions: every worktree of ONE
/// repository shares a `RepositoryId` while keeping its own `WorktreeId`, and nothing
/// that is not the same repository — an independent clone, a sibling that merely lives
/// under a directory called `worktrees` — ever shares one. The layouts are built as
/// `jj` 0.45 / `git` 2.55 write them; the `real …` tests drive the tools themselves to
/// prove those shapes are still what the tools write.
module FsHotWatch.Tests.RepositoryIdentityTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.RepositoryIdentity
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// Layout builders — the exact shapes verified on disk
// ---------------------------------------------------------------------------

let private mkdir (path: string) =
    Directory.CreateDirectory path |> ignore
    path

/// A jj primary workspace: `.jj/repo` is a directory.
let private jjPrimary (root: string) =
    mkdir (Path.Combine(root, ".jj", "repo", "store")) |> ignore
    mkdir (Path.Combine(root, ".jj", "working_copy")) |> ignore
    root

/// A jj secondary workspace: `.jj/repo` is a FILE holding the primary's `.jj/repo`,
/// relative to the secondary's `.jj` directory — what `jj workspace add` writes.
let private jjSecondary (primary: string) (workspace: string) =
    let jjDir = mkdir (Path.Combine(workspace, ".jj"))
    let target = Path.GetRelativePath(jjDir, Path.Combine(primary, ".jj", "repo"))
    File.WriteAllText(Path.Combine(jjDir, "repo"), target)
    workspace

/// Colocate a jj primary with git: a `.git` directory beside `.jj`, and jj's
/// `store/git_target` pointing at it.
let private colocate (primary: string) =
    mkdir (Path.Combine(primary, ".git")) |> ignore
    File.WriteAllText(Path.Combine(primary, ".jj", "repo", "store", "git_target"), "../../../.git")
    primary

/// A git main checkout with an `origin` remote.
let private gitMain (origin: string) (root: string) =
    let gitDir = mkdir (Path.Combine(root, ".git"))
    File.WriteAllText(Path.Combine(gitDir, "config"), $"[remote \"origin\"]\n\turl = %s{origin}\n")
    root

/// A git linked worktree of `main`: `.git` is a FILE naming the per-worktree gitdir,
/// whose `commondir` names the common directory — what `git worktree add` writes.
let private gitWorktree (main: string) (name: string) (worktree: string) =
    let perWorktree = mkdir (Path.Combine(main, ".git", "worktrees", name))
    File.WriteAllText(Path.Combine(perWorktree, "commondir"), "../..\n")
    File.WriteAllText(Path.Combine(perWorktree, "gitdir"), Path.Combine(worktree, ".git") + "\n")
    mkdir worktree |> ignore
    File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: %s{perWorktree}\n")
    worktree

let private resolved (root: string) =
    match resolveWorktree root with
    | Ok r -> r
    | Error e -> failwith $"expected %s{root} to resolve, got %s{IdentityError.describe e}"

let private failure (root: string) =
    match resolveWorktree root with
    | Ok r -> failwith $"expected %s{root} to FAIL, but it resolved to %A{r}"
    | Error e -> e

// ---------------------------------------------------------------------------
// One repository, many worktrees
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``jj primary and secondary workspaces share a RepositoryId and keep distinct WorktreeIds`` () =
    withTempDir "rid-jj" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "repo")))

        let secondary =
            jjSecondary primary (mkdir (Path.Combine(primary, ".workspaces", "ws")))

        let p = resolved primary
        let s = resolved secondary

        test <@ p.Store.Provider = VcsProvider.Jujutsu @>
        test <@ s.Store = p.Store @>
        test <@ s.Repository = p.Repository @>
        test <@ s.Worktree <> p.Worktree @>)

[<Fact(Timeout = 15000)>]
let ``an absolute jj pointer resolves to the same repository as a relative one`` () =
    withTempDir "rid-jj-abs" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "repo")))
        let ws = mkdir (Path.Combine(dir, "ws", ".jj"))
        File.WriteAllText(Path.Combine(ws, "repo"), Path.Combine(primary, ".jj", "repo") + "\n")

        test <@ (resolved (Path.Combine(dir, "ws"))).Repository = (resolved primary).Repository @>)

[<Fact(Timeout = 15000)>]
let ``git main checkout and linked worktree share a RepositoryId and keep distinct WorktreeIds`` () =
    withTempDir "rid-git" (fun dir ->
        let main = gitMain "git@example.com:o/r.git" (mkdir (Path.Combine(dir, "main")))
        let wt = gitWorktree main "feature" (Path.Combine(dir, "feature"))

        let m = resolved main
        let w = resolved wt

        test <@ m.Store.Provider = VcsProvider.Git @>
        test <@ w.Store = m.Store @>
        test <@ w.Repository = m.Repository @>
        test <@ w.Worktree <> m.Worktree @>)

[<Fact(Timeout = 15000)>]
let ``a colocated jj repository and a git worktree made from it are ONE repository`` () =
    // `git worktree add` from a colocated repo is a worktree of the same repository as
    // its jj workspaces. The link is proven by jj's own `git_target` pointing back at
    // the git directory.
    withTempDir "rid-coloc" (fun dir ->
        let primary = colocate (jjPrimary (mkdir (Path.Combine(dir, "repo"))))
        let secondary = jjSecondary primary (mkdir (Path.Combine(dir, "ws")))
        let gitWt = gitWorktree primary "gwt" (Path.Combine(dir, "gwt"))

        let ids = [ primary; secondary; gitWt ] |> List.map resolved

        test <@ ids |> List.map _.Repository |> List.distinct |> List.length = 1 @>
        test <@ ids |> List.map _.Worktree |> List.distinct |> List.length = 3 @>
        test <@ ids |> List.forall (fun r -> r.Store.Provider = VcsProvider.Jujutsu) @>)

[<Fact(Timeout = 15000)>]
let ``a .jj directory merely beside a git directory does not adopt it`` () =
    // Without `git_target` pointing back, a stray `.jj` next to `.git` proves nothing.
    withTempDir "rid-stray-jj" (fun dir ->
        let main = gitMain "o" (mkdir (Path.Combine(dir, "main")))
        let wt = gitWorktree main "wt" (Path.Combine(dir, "wt"))
        mkdir (Path.Combine(main, ".jj", "repo", "store")) |> ignore
        File.WriteAllText(Path.Combine(main, ".jj", "repo", "store", "git_target"), "git")

        test <@ (resolved wt).Store.Provider = VcsProvider.Git @>)

// ---------------------------------------------------------------------------
// Independent repositories never collide
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``two independent git clones of the same origin have DIFFERENT RepositoryIds`` () =
    withTempDir "rid-clones" (fun dir ->
        let origin = "git@github.com:owner/same.git"
        let a = gitMain origin (mkdir (Path.Combine(dir, "a", "same")))
        let b = gitMain origin (mkdir (Path.Combine(dir, "b", "same")))

        test <@ (resolved a).Repository <> (resolved b).Repository @>)

[<Fact(Timeout = 15000)>]
let ``two independent jj clones have DIFFERENT RepositoryIds`` () =
    withTempDir "rid-jj-clones" (fun dir ->
        let a = colocate (jjPrimary (mkdir (Path.Combine(dir, "a", "same"))))
        let b = colocate (jjPrimary (mkdir (Path.Combine(dir, "b", "same"))))

        test <@ (resolved a).Repository <> (resolved b).Repository @>)

[<Fact(Timeout = 15000)>]
let ``repositories living under a directory named worktrees are not truncated into one`` () =
    // Guessing the common dir from a `/worktrees/` path segment would cut both of these
    // back to `<dir>` and merge two unrelated repositories. `commondir` is the truth.
    withTempDir "rid-worktrees-dir" (fun dir ->
        let a = gitMain "o" (mkdir (Path.Combine(dir, "worktrees", "a")))
        let b = gitMain "o" (mkdir (Path.Combine(dir, "worktrees", "b")))

        test <@ (resolved a).Repository <> (resolved b).Repository @>
        test <@ (resolved a).Store.Path.Value.EndsWith(Path.Combine("worktrees", "a", ".git")) @>)

[<Fact(Timeout = 15000)>]
let ``a directory under no VCS is a standalone repository of its own`` () =
    withTempDir "rid-standalone" (fun dir ->
        let a = mkdir (Path.Combine(dir, "a"))
        let b = mkdir (Path.Combine(dir, "b"))

        test <@ (resolved a).Store.Provider = VcsProvider.Standalone @>
        test <@ (resolved a).Store.Path = (resolved a).Root @>
        test <@ (resolved a).Repository <> (resolved b).Repository @>)

[<Fact(Timeout = 15000)>]
let ``the provider is part of the RepositoryId`` () =
    withTempDir "rid-provider" (fun dir ->
        let store = (resolved (mkdir (Path.Combine(dir, "x")))).Root

        let ids =
            [ VcsProvider.Jujutsu; VcsProvider.Git; VcsProvider.Standalone ]
            |> List.map (fun p -> RepositoryId.ofStore { Provider = p; Path = store })

        test <@ ids |> List.distinct |> List.length = 3 @>)

// ---------------------------------------------------------------------------
// Canonical roots: symlinks, case, `..`, recreation
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``a symlinked root resolves to exactly its target's identity`` () =
    withTempDir "rid-symlink" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "repo")))
        let link = Path.Combine(dir, "link")
        Directory.CreateSymbolicLink(link, primary) |> ignore

        let viaLink = resolved link
        let direct = resolved primary

        test <@ viaLink.Root = direct.Root @>
        test <@ viaLink.Repository = direct.Repository @>
        test <@ viaLink.Worktree = direct.Worktree @>)

[<Fact(Timeout = 15000)>]
let ``a symlink in the MIDDLE of the path, relative, is resolved too`` () =
    withTempDir "rid-mid-symlink" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "real", "repo")))
        Directory.CreateSymbolicLink(Path.Combine(dir, "alias"), "real") |> ignore

        test <@ (resolved (Path.Combine(dir, "alias", "repo"))).Worktree = (resolved primary).Worktree @>)

[<Fact(Timeout = 15000)>]
let ``.. after a symlink is the PHYSICAL parent, not the lexical one`` () =
    withTempDir "rid-dotdot" (fun dir ->
        let target = mkdir (Path.Combine(dir, "deep", "inner"))
        let sibling = jjPrimary (mkdir (Path.Combine(dir, "deep", "sibling")))
        Directory.CreateSymbolicLink(Path.Combine(dir, "shortcut"), target) |> ignore
        // Lexically `shortcut/../sibling` is `<dir>/sibling`, which does not exist;
        // physically it is `deep/sibling`.
        test <@ (resolved (dir + "/shortcut/../sibling")).Worktree = (resolved sibling).Worktree @>)

[<Fact(Timeout = 15000)>]
let ``. segments, a relative path, and .. above the filesystem root all canonicalize`` () =
    withTempDir "rid-segments" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "repo")))
        let expected = (resolved primary).Root

        test <@ (resolved (dir + "/./repo/.")).Root = expected @>
        // `..` at the root stays at the root, as POSIX has it.
        test <@ (resolved ("/.." + primary)).Root = expected @>
        test <@ (resolved (Path.GetRelativePath(Directory.GetCurrentDirectory(), primary))).Root = expected @>)

[<Fact(Timeout = 15000)>]
let ``a directory that cannot be listed keeps the name as given`` () =
    // Search permission without read permission: the entry is reachable, its stored
    // spelling just cannot be learned by listing.
    withTempDir "rid-unlistable" (fun dir ->
        let locked = mkdir (Path.Combine(dir, "locked"))
        let primary = jjPrimary (mkdir (Path.Combine(locked, "repo")))
        let before = (resolved primary).Worktree
        File.SetUnixFileMode(locked, UnixFileMode.UserExecute)

        try
            let r = resolved primary
            test <@ r.Worktree = before @>
            test <@ r.Root.Value.EndsWith(Path.Combine("locked", "repo")) @>
        finally
            File.SetUnixFileMode(locked, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute))

[<Fact(Timeout = 15000)>]
let ``a case-variant root is the same worktree on a case-insensitive volume and absent on a sensitive one`` () =
    withTempDir "rid-case" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "MyRepo")))
        // A sibling that does NOT answer to the variant, so the match is a choice.
        mkdir (Path.Combine(dir, "Other")) |> ignore
        let variant = Path.Combine(dir, "myrepo")

        if Directory.Exists variant then
            let v = resolved variant
            test <@ v.Root = (resolved primary).Root @>
            test <@ v.Worktree = (resolved primary).Worktree @>
            test <@ v.Root.Value.EndsWith "MyRepo" @>
        else
            test <@ failure variant = IdentityError.PathNotFound variant @>)

[<Fact(Timeout = 15000)>]
let ``a worktree deleted and recreated at the same path keeps its WorktreeId`` () =
    withTempDir "rid-recreate" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "repo")))
        let wsPath = Path.Combine(dir, "ws")
        let before = resolved (jjSecondary primary (mkdir wsPath))
        Directory.Delete(wsPath, true)
        test <@ failure wsPath = IdentityError.PathNotFound wsPath @>
        let after = resolved (jjSecondary primary (mkdir wsPath))

        test <@ after.Worktree = before.Worktree @>
        test <@ after.Repository = before.Repository @>)

// ---------------------------------------------------------------------------
// Malformed layouts fail loudly
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``an empty jj pointer is malformed, not a private repository`` () =
    withTempDir "rid-empty-ptr" (fun dir ->
        let jjDir = mkdir (Path.Combine(dir, ".jj"))
        File.WriteAllText(Path.Combine(jjDir, "repo"), "  \n")

        test
            <@
                match failure dir with
                | IdentityError.MalformedMetadata(file, _) -> file.EndsWith(Path.Combine(".jj", "repo"))
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a jj pointer of blank lines is malformed`` () =
    withTempDir "rid-blank-ptr" (fun dir ->
        let jjDir = mkdir (Path.Combine(dir, ".jj"))
        File.WriteAllText(Path.Combine(jjDir, "repo"), "\n\r\n")

        test
            <@
                match failure dir with
                | IdentityError.MalformedMetadata(_, reason) -> reason = "empty"
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``an unreadable jj pointer is malformed, not a private repository`` () =
    withTempDir "rid-unreadable-ptr" (fun dir ->
        let jjDir = mkdir (Path.Combine(dir, ".jj"))
        let pointer = Path.Combine(jjDir, "repo")
        File.WriteAllText(pointer, "/somewhere")
        File.SetUnixFileMode(pointer, UnixFileMode.None)

        try
            test
                <@
                    match failure dir with
                    | IdentityError.MalformedMetadata(_, reason) -> reason.StartsWith "unreadable"
                    | _ -> false
                @>
        finally
            File.SetUnixFileMode(pointer, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact(Timeout = 15000)>]
let ``a jj pointer into a symlink loop reports the loop`` () =
    withTempDir "rid-ptr-loop" (fun dir ->
        let loopA = Path.Combine(dir, "loopA")
        Directory.CreateSymbolicLink(loopA, Path.Combine(dir, "loopB")) |> ignore
        Directory.CreateSymbolicLink(Path.Combine(dir, "loopB"), loopA) |> ignore
        let ws = mkdir (Path.Combine(dir, "ws"))
        let jjDir = mkdir (Path.Combine(ws, ".jj"))
        File.WriteAllText(Path.Combine(jjDir, "repo"), loopA)

        test
            <@
                match failure ws with
                | IdentityError.SymlinkLoop _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a jj pointer at a directory that is not there is a dangling pointer`` () =
    withTempDir "rid-dangling" (fun dir ->
        let jjDir = mkdir (Path.Combine(dir, ".jj"))
        File.WriteAllText(Path.Combine(jjDir, "repo"), "../../gone/.jj/repo")

        test
            <@
                match failure dir with
                | IdentityError.DanglingPointer(_, target) -> target = "../../gone/.jj/repo"
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a jj pointer with a NUL in it is malformed`` () =
    withTempDir "rid-nul" (fun dir ->
        let jjDir = mkdir (Path.Combine(dir, ".jj"))
        File.WriteAllText(Path.Combine(jjDir, "repo"), "../\000/.jj/repo")

        test
            <@
                match failure dir with
                | IdentityError.MalformedMetadata _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a .jj directory with no repo entry is malformed`` () =
    withTempDir "rid-no-repo" (fun dir ->
        mkdir (Path.Combine(dir, ".jj")) |> ignore

        test
            <@
                match failure dir with
                | IdentityError.MalformedMetadata _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a .git file that is not a gitdir pointer is malformed`` () =
    withTempDir "rid-bad-git" (fun dir ->
        File.WriteAllText(Path.Combine(dir, ".git"), "something else\n")

        test
            <@
                match failure dir with
                | IdentityError.MalformedMetadata _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a gitdir pointer at a missing directory is a dangling pointer`` () =
    withTempDir "rid-bad-gitdir" (fun dir ->
        File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: /nonexistent/.git/worktrees/x\n")

        test
            <@
                match failure dir with
                | IdentityError.DanglingPointer _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a commondir pointing nowhere is a dangling pointer`` () =
    withTempDir "rid-bad-commondir" (fun dir ->
        let main = gitMain "o" (mkdir (Path.Combine(dir, "main")))
        let wt = gitWorktree main "wt" (Path.Combine(dir, "wt"))
        File.WriteAllText(Path.Combine(main, ".git", "worktrees", "wt", "commondir"), "../../../missing\n")

        test
            <@
                match failure wt with
                | IdentityError.DanglingPointer _ -> true
                | _ -> false
            @>)

[<Fact(Timeout = 15000)>]
let ``a root that is missing, a file, or a symlink loop fails with its own reason`` () =
    withTempDir "rid-bad-root" (fun dir ->
        let missing = Path.Combine(dir, "missing")
        test <@ failure missing = IdentityError.PathNotFound missing @>

        let file = Path.Combine(dir, "file")
        File.WriteAllText(file, "")

        test
            <@
                match failure file with
                | IdentityError.NotADirectory _ -> true
                | _ -> false
            @>

        let loopA = Path.Combine(dir, "loopA")
        Directory.CreateSymbolicLink(loopA, Path.Combine(dir, "loopB")) |> ignore
        Directory.CreateSymbolicLink(Path.Combine(dir, "loopB"), loopA) |> ignore
        test <@ failure loopA = IdentityError.SymlinkLoop loopA @>

        for unusable in [ ""; null ] do
            test
                <@
                    match failure unusable with
                    | IdentityError.InvalidPath(_, reason) -> reason = "empty"
                    | _ -> false
                @>)

[<Fact(Timeout = 15000)>]
let ``every identity error describes itself with the path it is about`` () =
    let errors =
        [ IdentityError.PathNotFound "/p"
          IdentityError.NotADirectory "/p"
          IdentityError.SymlinkLoop "/p"
          IdentityError.InvalidPath("/p", "why")
          IdentityError.MalformedMetadata("/p", "why")
          IdentityError.DanglingPointer("/p", "/t") ]

    test <@ errors |> List.forall (fun e -> (IdentityError.describe e).Contains "/p") @>

// ---------------------------------------------------------------------------
// Id formats
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``ids round-trip through their rendered form and reject anything else`` () =
    withTempDir "rid-parse" (fun dir ->
        let r = resolved dir
        test <@ RepositoryId.tryParse r.Repository.Value = Some r.Repository @>
        test <@ WorktreeId.tryParse r.Worktree.Value = Some r.Worktree @>
        test <@ r.Repository.Value.Length = IdLength @>

        let session =
            { Repository = r.Repository
              Worktree = r.Worktree
              Incarnation = SessionIncarnation.mint () }

        test <@ SessionId.tryParse (SessionId.render session) = Some session @>

        for bad in
            [ null
              ""
              r.Repository.Value.ToUpperInvariant()
              r.Repository.Value + "0"
              "g" + r.Repository.Value.Substring 1 ] do
            test <@ RepositoryId.tryParse bad = None @>
            test <@ WorktreeId.tryParse bad = None @>
            test <@ SessionIncarnation.tryParse bad = None @>

        test <@ RepositoryId.tryParse (String.replicate IdLength "-") = None @>
        test <@ SessionId.tryParse "x.y.z" = None @>
        test <@ SessionId.tryParse null = None @>
        test <@ SessionId.tryParse (r.Repository.Value + "." + r.Worktree.Value) = None @>)

[<Fact(Timeout = 15000)>]
let ``every identity renders as its value`` () =
    withTempDir "rid-render" (fun dir ->
        let r = resolved dir
        let incarnation = SessionIncarnation.mint ()
        test <@ string r.Root = r.Root.Value @>
        test <@ string r.Repository = r.Repository.Value @>
        test <@ string r.Worktree = r.Worktree.Value @>
        test <@ string incarnation = incarnation.Value @>)

[<Fact(Timeout = 15000)>]
let ``two minted incarnations never coincide`` () =
    test <@ SessionIncarnation.mint () <> SessionIncarnation.mint () @>

// ---------------------------------------------------------------------------
// Where repository control state lives
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``the state home prefers the override, then XDG, then ~/.local/state`` () =
    test <@ FsHwPaths.stateHomeFrom "/explicit" "/xdg" "/home/u" = "/explicit" @>
    test <@ FsHwPaths.stateHomeFrom "" "/xdg" "/home/u" = Path.Combine("/xdg", "fshw") @>
    test <@ FsHwPaths.stateHomeFrom null "" "/home/u" = Path.Combine("/home/u", ".local", "state", "fshw") @>

[<Fact(Timeout = 15000)>]
let ``the state home reads the process environment through the same precedence`` () =
    // Read-only: the environment is process-global, so this compares rather than sets.
    let expected =
        FsHwPaths.stateHomeFrom
            (Environment.GetEnvironmentVariable FsHwPaths.StateHomeEnvVar)
            (Environment.GetEnvironmentVariable "XDG_STATE_HOME")
            (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)

    test <@ FsHwPaths.stateHome () = expected @>

[<Fact(Timeout = 15000)>]
let ``control state is keyed by repository, shared by its worktrees, and outside every worktree`` () =
    withTempDir "rid-control" (fun dir ->
        let stateHome = Path.Combine(dir, "state")
        let primary = jjPrimary (mkdir (Path.Combine(dir, "repo")))
        let secondary = jjSecondary primary (mkdir (Path.Combine(dir, "ws")))
        let other = jjPrimary (mkdir (Path.Combine(dir, "other")))

        let paths root =
            repositoryControlPaths stateHome (resolved root).Repository

        test <@ paths primary = paths secondary @>
        test <@ paths primary <> paths other @>

        let control = paths primary

        for worktree in [ primary; secondary ] do
            let root = (resolved worktree).Root.Value
            test <@ not (control.Directory.StartsWith(root + string Path.DirectorySeparatorChar)) @>

        for file in [ control.LockFile; control.PidFile; control.IdentityFile; control.HostLog ] do
            test <@ Path.GetDirectoryName file = control.Directory @>

        // Short enough for a Unix-domain socket name once the runtime prefixes it.
        test <@ control.Endpoint.Length <= 32 @>
        test <@ control.Endpoint <> (paths other).Endpoint @>)

[<Fact(Timeout = 15000)>]
let ``control state survives the deletion of a worktree`` () =
    withTempDir "rid-survive" (fun dir ->
        let stateHome = Path.Combine(dir, "state")
        let main = gitMain "o" (mkdir (Path.Combine(dir, "main")))
        let wt = gitWorktree main "wt" (Path.Combine(dir, "wt"))
        let before = repositoryControlPaths stateHome (resolved wt).Repository

        Directory.Delete(wt, true)

        test <@ repositoryControlPaths stateHome (resolved main).Repository = before @>)

[<Fact(Timeout = 15000)>]
let ``shared cache is per repository and results stay in the worktree's own .fshw`` () =
    withTempDir "rid-result-root" (fun dir ->
        let primary = jjPrimary (mkdir (Path.Combine(dir, "repo")))
        let secondary = jjSecondary primary (mkdir (Path.Combine(dir, "ws")))
        let p = resolved primary
        let s = resolved secondary

        test <@ repositorySharedCacheDir "/c" p.Repository = repositorySharedCacheDir "/c" s.Repository @>
        test <@ worktreeResultRoot p = Path.Combine(p.Root.Value, ".fshw") @>
        test <@ worktreeResultRoot s = Path.Combine(s.Root.Value, ".fshw") @>)

// ---------------------------------------------------------------------------
// This checkout, and the real tools
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``this checkout resolves, and when it is a jj workspace it shares its primary's repository`` () =
    let here = resolved (RepoTasks.repoRoot ())

    match here.Store.Provider with
    | VcsProvider.Jujutsu ->
        // The primary is the directory holding the shared `.jj/repo`.
        let primary = Path.GetDirectoryName(Path.GetDirectoryName here.Store.Path.Value)
        test <@ (resolved primary).Repository = here.Repository @>
    | _ -> ()

let private onPath (tool: string) =
    (Environment.GetEnvironmentVariable "PATH").Split(Path.PathSeparator)
    |> Array.exists (fun d -> File.Exists(Path.Combine(d, tool)))

let private run (cwd: string) (exe: string) (args: string list) =
    let psi = ProcessStartInfo(exe, args, WorkingDirectory = cwd)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.Environment["GIT_CONFIG_GLOBAL"] <- "/dev/null"
    psi.Environment["JJ_CONFIG"] <- "/dev/null"
    psi.Environment["JJ_USER"] <- "t"
    psi.Environment["JJ_EMAIL"] <- "t@example.com"
    use p = Process.Start psi
    let stdout = p.StandardOutput.ReadToEndAsync()
    let stderr = p.StandardError.ReadToEndAsync()

    if not (p.WaitForExit 10000) then
        p.Kill true
        failwith $"%s{exe} %A{args} timed out"

    if p.ExitCode <> 0 then
        failwith $"%s{exe} %A{args} exited %d{p.ExitCode}: %s{stdout.Result}%s{stderr.Result}"

[<Fact(Timeout = 30000)>]
let ``real git: a main checkout and a git worktree share a repository`` () =
    if not (onPath "git") then
        Assert.Skip "git is not on PATH"

    withTempDir "rid-real-git" (fun dir ->
        let main = mkdir (Path.Combine(dir, "main"))
        run main "git" [ "init"; "-q" ]

        run
            main
            "git"
            [ "-c"
              "user.email=t@example.com"
              "-c"
              "user.name=t"
              "commit"
              "-q"
              "--allow-empty"
              "-m"
              "x" ]

        run main "git" [ "worktree"; "add"; "-q"; Path.Combine(dir, "wt") ]

        let m = resolved main
        let w = resolved (Path.Combine(dir, "wt"))
        test <@ m.Store.Provider = VcsProvider.Git @>
        test <@ w.Repository = m.Repository @>
        test <@ w.Worktree <> m.Worktree @>)

[<Fact(Timeout = 30000)>]
let ``real jj: colocated primary, jj workspace and git worktree are one repository`` () =
    if not (onPath "jj" && onPath "git") then
        Assert.Skip "jj and git are not both on PATH"

    withTempDir "rid-real-jj" (fun dir ->
        let primary = Path.Combine(dir, "repo")
        run dir "jj" [ "git"; "init"; "--colocate"; primary ]
        run primary "jj" [ "workspace"; "add"; Path.Combine(dir, "ws") ]
        run primary "jj" [ "commit"; "-m"; "x" ]
        run primary "git" [ "worktree"; "add"; "-q"; "--detach"; Path.Combine(dir, "gwt") ]

        let ids =
            [ primary; Path.Combine(dir, "ws"); Path.Combine(dir, "gwt") ]
            |> List.map resolved

        test <@ ids |> List.forall (fun r -> r.Store.Provider = VcsProvider.Jujutsu) @>
        test <@ ids |> List.map _.Repository |> List.distinct |> List.length = 1 @>
        test <@ ids |> List.map _.Worktree |> List.distinct |> List.length = 3 @>

        // And an independent jj clone is a different repository.
        let other = Path.Combine(dir, "other")
        run dir "jj" [ "git"; "init"; "--colocate"; other ]
        test <@ (resolved other).Repository <> ids.Head.Repository @>)
