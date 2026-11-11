/// AUTOMATION-865. A directory that cannot be read is not an empty one. Each walk below
/// once read it as absent: the discovery roots and the declared verdict inputs (the tree
/// hash), the `dependsOn` salt of the test cache key, and the project lookup of a
/// runner's `--project` directory. Each must name the path it could not see instead,
/// and a readable twin of the same tree is the positive control.
module FsHotWatch.Tests.UnreadableWalkTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestPrune.ExternalInputs
open FsHotWatch.TestPrune.RunnerArgs
open FsHotWatch.Tests.TestHelpers

let private rwx =
    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// Run `body` with `dir` unreadable (mode 000), restoring it after so the scratch tree
/// can be deleted. Unix only: Windows has no mode bits to take away.
let private sealedDuring (dir: string) (body: unit -> unit) =
    if not (OperatingSystem.IsWindows()) then
        File.SetUnixFileMode(dir, UnixFileMode.None)

        try
            body ()
        finally
            File.SetUnixFileMode(dir, rwx)

// ---------------------------------------------------------------------------
// The discovery roots (TreeHash.files)
// ---------------------------------------------------------------------------

/// `src` is a link into a directory that cannot be traversed: `Directory.Exists` says
/// no, exactly as it does for a missing root.
let private withLinkedRoot (body: string -> string -> unit) =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "unreadable-root" (fun scratch ->
            let repo = Path.Combine(scratch, "repo")
            let vault = Path.Combine(scratch, "vault")
            write vault "real/Lib.fs" "module Lib\n"
            write repo ".fshw.json" "{}"
            write repo "tests/T.fs" "module T\n"

            File.CreateSymbolicLink(Path.Combine(repo, "src"), Path.Combine(vault, "real"))
            |> ignore

            body repo vault)

[<Fact>]
let ``a discovery root behind an untraversable directory is a hole in the tree hash, not an absent root`` () =
    withLinkedRoot (fun repo vault ->
        // POSITIVE CONTROL: readable, the linked root is walked like any other.
        let seen = TreeHash.files repo []
        test <@ seen.Files |> List.map fst |> List.contains "src/Lib.fs" @>
        test <@ List.isEmpty seen.Skipped @>

        sealedDuring vault (fun () ->
            let walked = TreeHash.files repo []
            test <@ walked.Skipped = [ "src" ] @>
            test <@ (TreeHash.ofWalked walked).Hash <> (TreeHash.ofWalked seen).Hash @>))

[<Fact>]
let ``a discovery root that is a dangling link is absent, not a hole`` () =
    withTempDir "dangling-root" (fun scratch ->
        if not (OperatingSystem.IsWindows()) then
            let repo = Path.Combine(scratch, "repo")
            write repo "tests/T.fs" "module T\n"

            File.CreateSymbolicLink(Path.Combine(repo, "src"), Path.Combine(scratch, "nowhere"))
            |> ignore

            test <@ List.isEmpty (TreeHash.files repo []).Skipped @>)

// ---------------------------------------------------------------------------
// Declared verdict inputs (VerdictInputs.resolve)
// ---------------------------------------------------------------------------

let private declaring (paths: string list) =
    { VerdictInputs.empty with
        Hashed =
            paths
            |> List.map (fun p ->
                { VerdictInputs.Declared.Path = p
                  VerdictInputs.Declared.Why = "test" }) }

let private withDeclaredInputs (body: string -> string -> unit) =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "unreadable-declared" (fun repo ->
            write repo "inputs/rules.json" "{}"
            write repo "inputs/sealed/more.json" "{}"
            body repo (Path.Combine(repo, "inputs/sealed")))

[<Fact>]
let ``an unreadable directory under a declared input is reported and hashed as a hole`` () =
    withDeclaredInputs (fun repo sealedDir ->
        for declared in [ "inputs"; "inputs/**/*.json" ] do
            let declaration = declaring [ declared ]
            // POSITIVE CONTROL: readable, both files resolve and nothing is unreadable.
            let readable = VerdictInputs.resolve repo declaration
            test <@ List.length readable.Files = 2 && List.isEmpty readable.Unreadable @>

            sealedDuring sealedDir (fun () ->
                let resolved = VerdictInputs.resolve repo declaration
                test <@ resolved.Unreadable = [ "inputs/sealed" ] @>
                test <@ List.isEmpty resolved.Absent @>))

[<Fact>]
let ``a declared file that cannot be looked up is unreadable, not absent`` () =
    withDeclaredInputs (fun repo sealedDir ->
        let declaration = declaring [ "inputs/sealed/more.json" ]
        test <@ (VerdictInputs.resolve repo declaration).Files.Length = 1 @>

        sealedDuring sealedDir (fun () ->
            let resolved = VerdictInputs.resolve repo declaration
            test <@ List.isEmpty resolved.Absent @>
            test <@ resolved.Unreadable = [ "inputs/sealed/more.json" ] @>))

[<Fact>]
let ``the tree hash folds in an unreadable declared input`` () =
    withDeclaredInputs (fun repo sealedDir ->
        write repo ".fshw.json" """{"verdictInputs":{"hashed":[{"path":"inputs","why":"test"}]}}"""
        test <@ List.isEmpty (TreeHash.files repo []).Skipped @>

        sealedDuring sealedDir (fun () -> test <@ (TreeHash.files repo []).Skipped = [ "inputs/sealed" ] @>))

// ---------------------------------------------------------------------------
// The dependsOn salt (ExternalInputs)
// ---------------------------------------------------------------------------

[<Fact>]
let ``an unreadable directory under a dependsOn glob moves the cache salt and is named`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "unreadable-dependson" (fun repo ->
            let sealedDir = Path.Combine(repo, "db/sealed")
            Directory.CreateDirectory sealedDir |> ignore
            // POSITIVE CONTROL: a readable, empty directory matches nothing: no salt.
            test <@ externalDependencyHash repo [ "db/**" ] = "" @>
            test <@ List.isEmpty (resolveDependsOnFiles repo [ "db/**" ]).Unreadable @>

            sealedDuring sealedDir (fun () ->
                test <@ (resolveDependsOnFiles repo [ "db/**" ]).Unreadable = [ "db/sealed" ] @>
                test <@ externalDependencyHash repo [ "db/**" ] <> "" @>))

// ---------------------------------------------------------------------------
// A runner's --project directory (RunnerArgs.deriveProjectBin)
// ---------------------------------------------------------------------------

[<Fact>]
let ``a --project directory inside one that cannot be traversed is named, not absent`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "untraversable-project" (fun repo ->
            write repo "tests/Vault/T/T.fsproj" "<Project />"
            let vault = Path.Combine(repo, "tests/Vault")
            let dir = Path.Combine(vault, "T")
            let args = "run --project tests/Vault/T --no-build"
            test <@ (deriveProjectBin args repo).Value.Unlisted = None @>

            // A directory that is not there at all holds no project, and is no hole.
            test <@ SafeWalk.filesIn (Path.Combine(repo, "tests/Nowhere")) "*.fsproj" = Ok [||] @>

            sealedDuring vault (fun () ->
                test
                    <@
                        (deriveProjectBin args repo).Value.Unlisted
                        |> Option.map (fun hole -> hole.Path) = Some dir
                    @>))

[<Fact>]
let ``a --project directory that cannot be listed is named, never thrown or read as holding no project`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "unreadable-project" (fun repo ->
            write repo "tests/T/T.fsproj" "<Project />"
            let dir = Path.Combine(repo, "tests/T")
            let args = "run --project tests/T --no-build"

            // POSITIVE CONTROL: readable, the project file is found.
            let readable = (deriveProjectBin args repo).Value

            test
                <@
                    readable.ProjectFile = Some(Path.Combine(dir, "T.fsproj"))
                    && readable.Unlisted = None
                @>

            sealedDuring dir (fun () ->
                let target = (deriveProjectBin args repo).Value
                test <@ target.ProjectFile = None @>
                test <@ target.Unlisted |> Option.map (fun hole -> hole.Path) = Some dir @>

                test
                    <@
                        match
                            FsHotWatch.TestPrune.ArtifactFreshness.stale
                                (FsHotWatch.TestPrune.ArtifactFreshness.Cache())
                                target
                        with
                        | Some(FsHotWatch.TestPrune.ArtifactFreshness.InputsUndeterminable(_, reason)) ->
                            reason.Contains dir
                        | _ -> false
                    @>))

// ---------------------------------------------------------------------------
// AUTOMATION-1563: what the tree hash could not see without saying so
// ---------------------------------------------------------------------------

[<Fact>]
let ``a checkout under a directory named bin still hashes its sources`` () =
    // A: `/bin/` was matched in the ABSOLUTE path, so a repository at `…/bin/repo`
    // hashed no source at all and an edit left the hash unchanged.
    withTempDir "treehash-bin" (fun scratch ->
        let repo = Path.Combine(scratch, "bin", "repo")
        write repo "src/A.fs" "module A\nlet x = 1\n"
        let before = TreeHash.compute repo []
        write repo "src/A.fs" "module A\nlet x = 2\n"
        let after = TreeHash.compute repo []
        test <@ before.FileCount > 0 @>
        test <@ before.Hash <> after.Hash @>)

[<Fact>]
let ``an edit under a symlinked source subdirectory that points inside the repository moves the tree hash`` () =
    // B: the walk skips every symlinked directory, and the tree hash recorded nothing for
    // one. `src/Shared -> ../shared/Shared` holding compile items could change freely
    // under a verdict that still applied.
    if not (OperatingSystem.IsWindows()) then
        withTempDir "treehash-link-inside" (fun repo ->
            write repo "src/App.fs" "module App\n"
            write repo "shared/Shared/a.fs" "module Shared\nlet y = 1\n"

            File.CreateSymbolicLink(Path.Combine(repo, "src", "Shared"), Path.Combine(repo, "shared", "Shared"))
            |> ignore

            let before = TreeHash.compute repo []
            write repo "shared/Shared/a.fs" "module Shared\nlet y = 2\n"
            let after = TreeHash.compute repo []

            test
                <@
                    (TreeHash.files repo []).Files
                    |> List.map fst
                    |> List.contains "src/Shared/a.fs"
                @>

            test <@ before.Hash <> after.Hash @>)

[<Fact>]
let ``a symlinked source subdirectory pointing outside the repository is a recorded hole, never followed`` () =
    // A link out of the tree is not walked (`.devenv/` links into a 160K-file nix store),
    // and it is not silence either: it is a hole, named in `Skipped`.
    if not (OperatingSystem.IsWindows()) then
        withTempDir "treehash-link-outside" (fun scratch ->
            let repo = Path.Combine(scratch, "repo")
            let common = Path.Combine(scratch, "common")
            write repo "src/App.fs" "module App\n"
            write common "Shared/a.fs" "module Shared\nlet y = 1\n"

            File.CreateSymbolicLink(Path.Combine(repo, "src", "Shared"), Path.Combine(common, "Shared"))
            |> ignore

            let walked = TreeHash.files repo []
            test <@ walked.Skipped = [ "src/Shared" ] @>

            test
                <@
                    walked.Files
                    |> List.map fst
                    |> List.forall (fun rel -> not (rel.StartsWith "src/Shared/"))
                @>

            test <@ (TreeHash.ofWalked walked).SkippedCount = 1 @>)

[<Fact>]
let ``a dangling symlinked source subdirectory is absent, and one under build output is never looked at`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "treehash-link-dangling" (fun scratch ->
            let repo = Path.Combine(scratch, "repo")
            write repo "src/App.fs" "module App\n"
            write repo "src/obj/Debug/App.AssemblyInfo.fs" "module Generated\n"

            File.CreateSymbolicLink(Path.Combine(repo, "src", "Gone"), Path.Combine(scratch, "nowhere"))
            |> ignore

            File.CreateSymbolicLink(Path.Combine(repo, "src", "obj", "Linked"), Path.Combine(scratch, "elsewhere"))
            |> ignore

            let walked = TreeHash.files repo []
            test <@ List.isEmpty walked.Skipped @>
            // Nothing under build output, and no link followed or holed there. (The walk lists
            // the dangling link itself as a file entry, as it always has.)
            test
                <@
                    walked.Files
                    |> List.map fst
                    |> List.forall (fun rel -> not (rel.StartsWith "src/obj/"))
                @>)
