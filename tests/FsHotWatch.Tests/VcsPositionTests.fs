module FsHotWatch.Tests.VcsPositionTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.VcsPosition
open FsHotWatch.Tests.TestHelpers

// ---- protobuf, as jj writes it -------------------------------------------------------

let private varint (value: uint64) : byte[] =
    let rec go (v: uint64) acc =
        if v < 0x80UL then
            List.rev (byte v :: acc)
        else
            go (v >>> 7) (byte ((v &&& 0x7FUL) ||| 0x80UL) :: acc)

    go value [] |> Array.ofList

let private lengthField (number: int) (payload: byte[]) =
    Array.concat
        [ varint (uint64 ((number <<< 3) ||| 2))
          varint (uint64 payload.Length)
          payload ]

let private varintField (number: int) (value: uint64) =
    Array.concat [ varint (uint64 (number <<< 3)); varint value ]

let private opId (seed: byte) = Array.init 64 (fun i -> seed + byte i)

let private hex (bytes: byte[]) =
    Convert.ToHexString(bytes).ToLowerInvariant()

/// `.jj/working_copy/checkout`: the operation id (field 2), the workspace name (field 3).
let private checkoutBytes (op: byte[]) =
    Array.concat [ lengthField 2 op; lengthField 3 "a1705"B ]

/// An operation: its view id (field 1) and metadata (field 3), whose field 3 is the
/// description and field 7 `is_snapshot`. The description is long enough that its
/// length takes two varint bytes, as a real one with `args` tags does.
let private operationBytes (description: string) (isSnapshot: bool) =
    let metadata =
        Array.concat
            [ lengthField 1 (Array.concat [ varintField 1 1700000000000UL; varintField 2 60UL ])
              lengthField 3 (Text.Encoding.UTF8.GetBytes(description + String(' ', 150)))
              (if isSnapshot then varintField 7 1UL else [||]) ]

    Array.concat [ lengthField 1 (opId 9uy); lengthField 3 metadata ]

/// A jj default workspace at `root`: `.jj/repo` a directory holding the op store.
let private jjWorkspace (root: string) =
    Directory.CreateDirectory(Path.Combine(root, ".jj", "repo", "op_store", "operations"))
    |> ignore

    Directory.CreateDirectory(Path.Combine(root, ".jj", "working_copy")) |> ignore

let private writeOperation (root: string) (op: byte[]) (bytes: byte[]) =
    File.WriteAllBytes(Path.Combine(root, ".jj", "repo", "op_store", "operations", hex op), bytes)

let private checkOut (root: string) (op: byte[]) =
    File.WriteAllBytes(Path.Combine(root, ".jj", "working_copy", "checkout"), checkoutBytes op)

let private readerOf (root: string) =
    match probeFor root with
    | Probe.Tracked read -> read
    | Probe.Untracked reason -> failwith $"expected a tracked checkout, got: {reason}"

let private positionOf (root: string) =
    match readerOf root () with
    | Ok position -> position
    | Error reason -> failwith $"expected a position, got: {reason}"

let private errorOf (root: string) =
    match readerOf root () with
    | Ok position -> failwith $"expected an error, got {position}"
    | Error reason -> reason

let private isVcsOperation origin =
    match origin with
    | BatchOrigin.VcsOperation _ -> true
    | BatchOrigin.Edit _ -> false

// ---- jj ------------------------------------------------------------------------------

[<Fact>]
let ``jj: reads the operation that last updated this workspace's working copy`` () =
    withTempDir "vcs-jj" (fun root ->
        jjWorkspace root
        writeOperation root (opId 1uy) (operationBytes "rebase" false)
        checkOut root (opId 1uy)

        test <@ positionOf root = Position.JjWorkingCopy(hex (opId 1uy), JjOperationKind.Rewrite) @>)

[<Fact>]
let ``jj: a workspace whose working copy a rewriting operation moved is a VCS operation, a still one an edit`` () =
    withTempDir "vcs-jj-moved" (fun root ->
        jjWorkspace root
        writeOperation root (opId 1uy) (operationBytes "new empty commit" false)
        writeOperation root (opId 2uy) (operationBytes "rebase commit" false)
        checkOut root (opId 1uy)
        let before = positionOf root

        test <@ classify before (positionOf root) = BatchOrigin.Edit before @>

        checkOut root (opId 2uy)
        let after = positionOf root

        test <@ classify before after = BatchOrigin.VcsOperation(before, after) @>)

[<Fact>]
let ``jj: a working copy moved only by a snapshot is an edit, because jj wrote no file`` () =
    withTempDir "vcs-jj-snapshot" (fun root ->
        jjWorkspace root
        writeOperation root (opId 1uy) (operationBytes "rebase" false)
        writeOperation root (opId 2uy) (operationBytes "snapshot working copy" true)
        checkOut root (opId 1uy)
        let before = positionOf root
        checkOut root (opId 2uy)
        let after = positionOf root

        test <@ after = Position.JjWorkingCopy(hex (opId 2uy), JjOperationKind.Snapshot) @>
        test <@ classify before after = BatchOrigin.Edit after @>)

[<Fact>]
let ``jj: a secondary workspace reads its own working copy and the shared op store`` () =
    withTempDir "vcs-jj-secondary" (fun dir ->
        let primary = Path.Combine(dir, "primary")
        let secondary = Path.Combine(dir, "secondary")
        jjWorkspace primary

        Directory.CreateDirectory(Path.Combine(secondary, ".jj", "working_copy"))
        |> ignore

        File.WriteAllText(Path.Combine(secondary, ".jj", "repo"), "../../primary/.jj/repo")
        writeOperation primary (opId 3uy) (operationBytes "edit commit" false)
        checkOut secondary (opId 3uy)

        test <@ positionOf secondary = Position.JjWorkingCopy(hex (opId 3uy), JjOperationKind.Rewrite) @>)

[<Fact>]
let ``jj: unreadable records are errors, never a position`` () =
    withTempDir "vcs-jj-errors" (fun root ->
        jjWorkspace root

        // No checkout file yet.
        test <@ (errorOf root).Contains "checkout" @>

        // A checkout naming no operation.
        File.WriteAllBytes(Path.Combine(root, ".jj", "working_copy", "checkout"), lengthField 3 "a1705"B)
        test <@ (errorOf root).Contains "no operation id" @>

        // A field cut short.
        File.WriteAllBytes(Path.Combine(root, ".jj", "working_copy", "checkout"), [| 0x12uy; 0x40uy; 1uy |])
        test <@ (errorOf root).Contains "truncated" @>

        // A varint that never ends.
        File.WriteAllBytes(Path.Combine(root, ".jj", "working_copy", "checkout"), [| 0x08uy; 0x80uy |])
        test <@ (errorOf root).Contains "truncated" @>

        // A wire type jj does not write here (fixed64).
        File.WriteAllBytes(Path.Combine(root, ".jj", "working_copy", "checkout"), [| 0x09uy |])
        test <@ (errorOf root).Contains "wire type 1" @>

        // An operation the op store does not hold.
        checkOut root (opId 4uy)
        test <@ (errorOf root).Contains(hex (opId 4uy)) @>)

[<Fact>]
let ``jj: an operation with no metadata rewrote, as proto3 reads an absent is_snapshot`` () =
    withTempDir "vcs-jj-bare-op" (fun root ->
        jjWorkspace root
        writeOperation root (opId 5uy) (lengthField 1 (opId 9uy))
        checkOut root (opId 5uy)

        test <@ positionOf root = Position.JjWorkingCopy(hex (opId 5uy), JjOperationKind.Rewrite) @>)

// ---- git -----------------------------------------------------------------------------

let private sha (c: char) = String(c, 40)

/// A git main checkout at `root` whose HEAD file holds `head`.
let private gitCheckout (root: string) (head: string) =
    Directory.CreateDirectory(Path.Combine(root, ".git", "refs", "heads")) |> ignore
    File.WriteAllText(Path.Combine(root, ".git", "HEAD"), head + "\n")

let private setRef (gitDir: string) (name: string) (commit: string) =
    File.WriteAllText(Path.Combine(gitDir, name), commit + "\n")

[<Fact>]
let ``git: HEAD on a branch reads the commit the branch names; a moved branch is a VCS operation`` () =
    withTempDir "vcs-git" (fun root ->
        gitCheckout root "ref: refs/heads/main"
        let gitDir = Path.Combine(root, ".git")
        setRef gitDir "refs/heads/main" (sha 'a')
        let before = positionOf root

        test <@ before = Position.GitBranch("refs/heads/main", GitRef.Points(sha 'a')) @>
        test <@ classify before (positionOf root) = BatchOrigin.Edit before @>

        setRef gitDir "refs/heads/main" (sha 'b')
        test <@ isVcsOperation (classify before (positionOf root)) @>)

[<Fact>]
let ``git: a switch to another branch is a VCS operation even at the same commit`` () =
    withTempDir "vcs-git-switch" (fun root ->
        gitCheckout root "ref: refs/heads/main"
        let gitDir = Path.Combine(root, ".git")
        setRef gitDir "refs/heads/main" (sha 'a')
        setRef gitDir "refs/heads/topic" (sha 'a')
        let before = positionOf root
        gitCheckout root "ref: refs/heads/topic"

        test <@ isVcsOperation (classify before (positionOf root)) @>)

[<Fact>]
let ``git: a packed ref, an unborn branch and a detached HEAD each read`` () =
    withTempDir "vcs-git-shapes" (fun root ->
        gitCheckout root "ref: refs/heads/main"
        let gitDir = Path.Combine(root, ".git")

        test <@ positionOf root = Position.GitBranch("refs/heads/main", GitRef.Unborn) @>

        File.WriteAllText(
            Path.Combine(gitDir, "packed-refs"),
            $"# pack-refs with: peeled fully-peeled sorted \n{sha 'c'} refs/heads/other\n{sha 'd'} refs/heads/main\n^{sha 'e'}\n"
        )

        test <@ positionOf root = Position.GitBranch("refs/heads/main", GitRef.Points(sha 'd')) @>

        gitCheckout root (sha 'f')
        test <@ positionOf root = Position.GitDetached(sha 'f') @>)

[<Fact>]
let ``git: a HEAD that is neither a ref nor a commit is an error`` () =
    withTempDir "vcs-git-bad-head" (fun root ->
        gitCheckout root "ref: refs/heads/main"
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "not a head\n")

        test <@ (errorOf root).Contains "HEAD" @>

        File.Delete(Path.Combine(root, ".git", "HEAD"))
        test <@ (errorOf root).Contains "HEAD" @>)

[<Fact>]
let ``git: a linked worktree reads its own HEAD and the common directory's refs`` () =
    withTempDir "vcs-git-worktree" (fun dir ->
        let main = Path.Combine(dir, "main")
        let linked = Path.Combine(dir, "linked")
        gitCheckout main "ref: refs/heads/main"
        let common = Path.Combine(main, ".git")
        let perWorktree = Path.Combine(common, "worktrees", "linked")
        Directory.CreateDirectory(perWorktree) |> ignore
        Directory.CreateDirectory(linked) |> ignore
        File.WriteAllText(Path.Combine(perWorktree, "HEAD"), "ref: refs/heads/topic\n")
        File.WriteAllText(Path.Combine(perWorktree, "commondir"), "../..\n")
        File.WriteAllText(Path.Combine(linked, ".git"), $"gitdir: {perWorktree}\n")
        setRef common "refs/heads/topic" (sha '7')

        test <@ positionOf linked = Position.GitBranch("refs/heads/topic", GitRef.Points(sha '7')) @>)

[<Fact>]
let ``git: a linked worktree whose commondir dangles is untracked`` () =
    withTempDir "vcs-git-worktree-dangling" (fun dir ->
        let perWorktree = Path.Combine(dir, "gitdir")
        let linked = Path.Combine(dir, "linked")
        Directory.CreateDirectory(perWorktree) |> ignore
        Directory.CreateDirectory(linked) |> ignore
        File.WriteAllText(Path.Combine(perWorktree, "HEAD"), "ref: refs/heads/topic\n")
        File.WriteAllText(Path.Combine(perWorktree, "commondir"), "../nowhere\n")
        File.WriteAllText(Path.Combine(linked, ".git"), $"gitdir: {perWorktree}\n")

        // The worktree itself does not resolve, so `probeFor` never reaches git.
        test
            <@
                match probeFor linked with
                | Probe.Untracked _ -> true
                | Probe.Tracked _ -> false
            @>)

// ---- no VCS --------------------------------------------------------------------------

[<Fact>]
let ``a plain directory and an unresolvable checkout are untracked`` () =
    withTempDir "vcs-none" (fun root ->
        let untracked dir =
            match probeFor dir with
            | Probe.Untracked reason -> reason
            | Probe.Tracked _ -> failwith "expected untracked"

        test <@ (untracked root).Contains "no .jj or .git" @>

        Directory.CreateDirectory(Path.Combine(root, ".jj")) |> ignore
        test <@ (untracked root).Contains "repo" @>)

[<Fact>]
let ``describe names the position a log line reports`` () =
    let op = String('a', 128)

    test <@ Position.describe (Position.JjWorkingCopy(op, JjOperationKind.Rewrite)) = "jj op aaaaaaaaaaaa" @>

    test
        <@ Position.describe (Position.JjWorkingCopy(op, JjOperationKind.Snapshot)) = "jj op aaaaaaaaaaaa (snapshot)" @>

    test <@ Position.describe (Position.GitDetached(sha 'b')) = "git HEAD bbbbbbbbbbbb (detached)" @>

    test
        <@
            Position.describe (Position.GitBranch("refs/heads/main", GitRef.Points(sha 'c'))) = "git refs/heads/main at cccccccccccc"
        @>

    test <@ Position.describe (Position.GitBranch("refs/heads/main", GitRef.Unborn)) = "git refs/heads/main (unborn)" @>

[<Fact>]
let ``jj: a malformed operation or metadata is an error naming the operation file`` () =
    withTempDir "vcs-jj-bad-op" (fun root ->
        jjWorkspace root
        writeOperation root (opId 6uy) [| 0x0auy; 0x05uy |]
        checkOut root (opId 6uy)

        test <@ (errorOf root).Contains(hex (opId 6uy)) @>

        writeOperation root (opId 6uy) (lengthField 3 [| 0x0auy; 0x05uy |])
        test <@ (errorOf root).Contains "metadata" @>)

[<Fact>]
let ``a read after the layout changed reports what it found`` () =
    withTempDir "vcs-layout-changed" (fun dir ->
        let jj = Path.Combine(dir, "jj")
        jjWorkspace jj
        let readJj = readerOf jj
        Directory.Delete(Path.Combine(jj, ".jj", "repo"), true)

        test <@ Result.isError (readJj ()) @>

        let git = Path.Combine(dir, "git")
        gitCheckout git "ref: refs/heads/main"
        let readGit = readerOf git
        Directory.Delete(Path.Combine(git, ".git"), true)

        test
            <@
                match readGit () with
                | Error reason -> reason.Contains ".git"
                | Ok _ -> false
            @>)

[<Fact>]
let ``git: a forty-character HEAD that is not hex is not a commit`` () =
    withTempDir "vcs-git-not-hex" (fun root ->
        gitCheckout root (String('z', 40))

        test <@ (errorOf root).Contains "neither a ref nor a commit" @>)

[<Fact>]
let ``git: packed-refs without the branch leaves it unborn`` () =
    withTempDir "vcs-git-packed-miss" (fun root ->
        gitCheckout root "ref: refs/heads/main"
        File.WriteAllText(Path.Combine(root, ".git", "packed-refs"), $"{sha 'c'} refs/heads/other\n")

        test <@ positionOf root = Position.GitBranch("refs/heads/main", GitRef.Unborn) @>)

[<Fact>]
let ``git: a detached HEAD that moves is a VCS operation`` () =
    withTempDir "vcs-git-detached-moved" (fun root ->
        gitCheckout root (sha '1')
        let before = positionOf root
        gitCheckout root (sha '2')

        test <@ classify before (positionOf root) = BatchOrigin.VcsOperation(before, Position.GitDetached(sha '2')) @>)

[<Fact>]
let ``jj: fields of other types around the operation id are skipped`` () =
    withTempDir "vcs-jj-varint" (fun root ->
        jjWorkspace root
        writeOperation root (opId 7uy) (operationBytes "abandon" false)

        File.WriteAllBytes(
            Path.Combine(root, ".jj", "working_copy", "checkout"),
            Array.concat [ varintField 4 300UL; checkoutBytes (opId 7uy) ]
        )

        test <@ positionOf root = Position.JjWorkingCopy(hex (opId 7uy), JjOperationKind.Rewrite) @>)

[<Fact>]
let ``jj: a checkout that is a directory is an error, not a position`` () =
    withTempDir "vcs-jj-dir" (fun root ->
        jjWorkspace root

        Directory.CreateDirectory(Path.Combine(root, ".jj", "working_copy", "checkout"))
        |> ignore

        test <@ (errorOf root).Contains "cannot read" @>)

[<Fact>]
let ``describe keeps an id shorter than its abbreviation whole`` () =
    test <@ Position.describe (Position.GitDetached "abc") = "git HEAD abc (detached)" @>

// ---- real jj and git, colocated --------------------------------------------------------

/// A colocated jj+git repository driven by the real tools: the jj detector reads it, a jj
/// operation is recognised, and an edit (even one jj has snapshotted) is not misread.
/// Also pins two known limits: a git operation is invisible to the jj detector until the
/// next jj command, and a jj operation that rewrites no file still moves the position.
[<Fact(Timeout = 9 * RealCommandMs)>]
let ``real colocated jj and git: the jj detector reads the checkout and tells an operation from an edit`` () =
    FsHotWatch.Tests.RealTools.requireTools [ "jj"; "git" ]
    let run = FsHotWatch.Tests.RealTools.run

    withTempDir "vcs-colocated" (fun parent ->
        let dir = Path.Combine(parent, "repo")
        run parent "jj" [ "git"; "init"; "--colocate"; dir ]
        let jj args = run dir "jj" ([ "-R"; dir ] @ args)

        let git args =
            run
                dir
                "git"
                ([ "-c"
                   "user.email=t@example.com"
                   "-c"
                   "user.name=t"
                   "-c"
                   "commit.gpgsign=false" ]
                 @ args)

        let gitHead () =
            File.ReadAllText(Path.Combine(dir, ".git", "HEAD")).Trim()

        let started = positionOf dir

        test
            <@
                match started with
                | Position.JjWorkingCopy(_, JjOperationKind.Rewrite) -> true
                | _ -> false
            @>

        // An edit no jj command has seen: the position stands still.
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n")
        let edited = positionOf dir
        test <@ classify started edited = BatchOrigin.Edit edited @>

        // `jj commit`: a jj operation, recognised. It moves git HEAD too.
        let headBefore = gitHead ()
        jj [ "commit"; "-m"; "x" ]
        let committed = positionOf dir
        test <@ isVcsOperation (classify edited committed) @>
        test <@ gitHead () <> headBefore @>

        // An edit jj snapshotted: the op moved, but it only recorded what was on disk.
        File.AppendAllText(Path.Combine(dir, "a.txt"), "two\n")
        jj [ "status" ]
        let snapshotted = positionOf dir
        test <@ classify committed snapshotted = BatchOrigin.Edit snapshotted @>

        // Known limit: a git commit moves git HEAD and nothing the jj detector reads.
        let headBeforeGit = gitHead ()
        git [ "commit"; "-q"; "-am"; "y" ]
        let afterGit = positionOf dir
        test <@ gitHead () <> headBeforeGit @>
        test <@ classify snapshotted afterGit = BatchOrigin.Edit afterGit @>

        // The next jj command imports it as an operation that rewrites no file, and the
        // position moves: the next save is classified as a VCS operation.
        jj [ "status" ]
        let imported = positionOf dir
        test <@ isVcsOperation (classify afterGit imported) @>

        // `jj new`: a jj operation, recognised.
        jj [ "new" ]
        test <@ isVcsOperation (classify imported (positionOf dir)) @>)
