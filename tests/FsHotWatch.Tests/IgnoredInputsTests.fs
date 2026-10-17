/// A directory or glob named in `verdictInputs` or `evidence.extra` resolves to the files
/// the repository TRACKS under it, not to whatever a build or a session left there. The
/// repository's `.gitignore` files decide which is which, read the way git reads them:
/// nested files apply below their own directory, later and deeper rules win, and a file
/// under an ignored directory cannot be re-included.
module FsHotWatch.Tests.IgnoredInputsTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

/// The repo-relative files `paths` resolve to.
let private resolved (root: string) (paths: string list) : string list =
    let declaration =
        { VerdictInputs.empty with
            Hashed =
                paths
                |> List.map (fun p ->
                    { VerdictInputs.Declared.Path = p
                      VerdictInputs.Declared.Why = "test" }) }

    (VerdictInputs.resolve root declaration).Files
    |> List.map (fun abs -> Path.GetRelativePath(root, abs).Replace('\\', '/'))

[<Fact>]
let ``a listed directory skips the ignored files and directories under it and keeps the tracked ones`` () =
    withTempDir "ignored-dir" (fun root ->
        write root ".gitignore" "infra/publish/\n*.env\n"
        write root "infra/template.yaml" "Resources: {}\n"
        write root "infra/publish/app.dll" "built"
        write root "infra/publish/deep/more.dll" "built"
        write root "infra/deploy.env" "SECRET=1\n"

        test <@ resolved root [ "infra" ] = [ "infra/template.yaml" ] @>)

[<Fact>]
let ``a glob walked from the repository root skips ignored files`` () =
    withTempDir "ignored-glob" (fun root ->
        write root ".gitignore" "metrics/\n"
        write root "docs/guide.md" "# guide\n"
        write root "docs/metrics/run.md" "generated\n"

        test <@ resolved root [ "**/*.md" ] = [ "docs/guide.md" ] @>)

[<Fact>]
let ``a nested gitignore applies only below its own directory`` () =
    withTempDir "ignored-nested" (fun root ->
        write root "sub/.gitignore" "*.log\n/build\n"
        write root "sub/a/x.log" "log"
        write root "sub/build/out.txt" "out"
        write root "sub/a/build/kept.txt" "kept: /build is anchored to sub/"
        write root "sub/src.txt" "src"
        write root "other/x.log" "kept: sub/.gitignore does not reach here"

        test
            <@
                resolved root [ "sub"; "other" ] = [ "other/x.log"
                                                     "sub/.gitignore"
                                                     "sub/a/build/kept.txt"
                                                     "sub/src.txt" ]
            @>)

[<Fact>]
let ``a gitignore between the repository root and the listed directory applies`` () =
    withTempDir "ignored-ancestor" (fun root ->
        write root "a/.gitignore" "*.tmp\n"
        write root "a/b/x.tmp" "scratch"
        write root "a/b/y.txt" "tracked"

        test <@ resolved root [ "a/b" ] = [ "a/b/y.txt" ] @>)

[<Fact>]
let ``a negation re-includes a file and a deeper gitignore overrides a shallower one`` () =
    withTempDir "ignored-negation" (fun root ->
        write root ".gitignore" "*.log\n!keep.log\n*.gen\n"
        write root "d/keep.log" "kept by negation"
        write root "d/drop.log" "ignored"
        write root "d/sub/.gitignore" "!wanted.gen\n"
        write root "d/sub/wanted.gen" "re-included below"
        write root "d/sub/other.gen" "still ignored"
        write root "d/x.gen" "ignored"

        test <@ resolved root [ "d" ] = [ "d/keep.log"; "d/sub/.gitignore"; "d/sub/wanted.gen" ] @>)

[<Fact>]
let ``a file under an ignored directory cannot be re-included, as in git`` () =
    withTempDir "ignored-parent" (fun root ->
        write root ".gitignore" "out/\n!out/keep.txt\n"
        write root "d/out/keep.txt" "git still ignores this: its directory is excluded"
        write root "d/src.txt" "tracked"

        test <@ resolved root [ "d" ] = [ "d/src.txt" ] @>)

[<Fact>]
let ``a listed directory the repository ignores is taken as named`` () =
    // Pointing AT ignored output is an explicit statement, as pointing at `bin/` is: the
    // ignore rules prune what a walk sweeps into, never the root it was handed.
    withTempDir "ignored-pointed" (fun root ->
        write root ".gitignore" "publish/\n"
        write root "infra/publish/app.dll" "built"
        write root "infra/publish/lib/dep.dll" "built"

        test <@ resolved root [ "infra/publish" ] = [ "infra/publish/app.dll"; "infra/publish/lib/dep.dll" ] @>)

[<Fact>]
let ``a listed file the repository ignores is still resolved`` () =
    withTempDir "ignored-file" (fun root ->
        write root ".gitignore" "*.env\n"
        write root "deploy.env" "A=1\n"

        test <@ resolved root [ "deploy.env" ] = [ "deploy.env" ] @>)

[<Fact>]
let ``without a gitignore every file under a listed directory resolves`` () =
    withTempDir "ignored-none" (fun root ->
        write root "infra/a.yaml" "a"
        write root "infra/publish/b.dll" "b"

        test <@ resolved root [ "infra" ] = [ "infra/a.yaml"; "infra/publish/b.dll" ] @>)

[<Fact>]
let ``a nested gitignore line is rewritten relative to the repository root`` () =
    let rebase = PathFilter.rebaseIgnoreLine

    test <@ rebase "sub" "" = None @>
    test <@ rebase "sub" "# comment" = None @>
    test <@ rebase "" "*.log  " = Some "*.log" @>
    test <@ rebase "sub" "*.log" = Some "sub/**/*.log" @>
    test <@ rebase "sub" "out/" = Some "sub/**/out/" @>
    test <@ rebase "sub" "/build" = Some "sub/build" @>
    test <@ rebase "sub" "a/b" = Some "sub/a/b" @>
    test <@ rebase "sub" "!keep.log" = Some "!sub/**/keep.log" @>
    test <@ rebase "sub" "!/keep.log" = Some "!sub/keep.log" @>
    // An escaped trailing space is part of the name, so it is not trimmed away.
    test <@ rebase "sub" "name\\ " = Some "sub/**/name\\ " @>

[<Fact>]
let ``a rule for a directory's contents does not ignore the directory itself`` () =
    // `packages/*` ignores what is in `packages`, not `packages`: a listed `packages` is
    // walked, and what a later rule re-includes in it is still found, from the directory
    // and from a glob walked from the repository root.
    withTempDir "ignored-contents" (fun root ->
        write root ".gitignore" "packages/*\n!packages/local/\ncache/**\n"
        write root "packages/restored/lib.dll" "restored"
        write root "packages/local/Rules.fs" "tracked"
        write root "packages/.keep" "ignored by packages/*"
        write root "cache/a/x.bin" "ignored by cache/**"

        test <@ resolved root [ "packages" ] = [ "packages/local/Rules.fs" ] @>
        test <@ List.isEmpty (resolved root [ "cache" ]) @>
        test <@ resolved root [ "**/*.fs" ] = [ "packages/local/Rules.fs" ] @>)

[<Fact>]
let ``the root and paths outside the repository are never ignored`` () =
    withTempDir "ignored-bounds" (fun root ->
        write root ".gitignore" "*\n"
        write root "a/x.txt" "x"
        let ignores = PathFilter.GitIgnores root
        let outside = Path.GetFullPath(Path.Combine(root, "..", "elsewhere"))

        test <@ not (ignores.IsIgnored(root, true)) @>
        test <@ not (ignores.IsIgnored(outside, true)) @>
        test <@ not (ignores.IsUnderIgnored root) @>
        test <@ not (ignores.IsUnderIgnored outside) @>
        test <@ ignores.IsIgnored(Path.Combine(root, "a", "x.txt"), false) @>
        test <@ ignores.IsUnderIgnored(Path.Combine(root, "a", "b")) @>)

[<Fact>]
let ``an unreadable gitignore ignores nothing, so the walk keys more rather than less`` () =
    if not (System.OperatingSystem.IsWindows()) then
        withTempDir "ignored-unreadable" (fun root ->
            write root "d/.gitignore" "*.log\n"
            write root "d/x.log" "kept"
            let gitignore = Path.Combine(root, "d", ".gitignore")
            File.SetUnixFileMode(gitignore, UnixFileMode.None)

            try
                test <@ resolved root [ "d" ] = [ "d/.gitignore"; "d/x.log" ] @>
                test <@ not ((PathFilter.GitIgnores root).IsIgnored(Path.Combine(root, "d", "x.log"), false)) @>
            finally
                File.SetUnixFileMode(gitignore, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
