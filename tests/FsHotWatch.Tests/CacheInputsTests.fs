/// The cache-key inputs that live outside the judged file: the config a
/// tool discovers by walking up from it.
module FsHotWatch.Tests.CacheInputsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.CacheInputs
open FsHotWatch.Tests.TestHelpers

// =============================================================================
// Config discovered by walking up from the file
// =============================================================================

[<Fact>]
let ``editorConfigInputs collects every editorconfig from the repo root down to the file`` () =
    withTempDir "fantomas-editorconfig" (fun dir ->
        let sub = Path.Combine(dir, "src", "deep")
        Directory.CreateDirectory sub |> ignore
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")
        File.WriteAllText(Path.Combine(dir, "src", ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")
        let file = Path.Combine(sub, "A.fs")
        File.WriteAllText(file, "module A\n")

        let inputs = editorConfigInputs dir [ file ]

        // the LABEL is repo-relative, so the same `.editorconfig` is
        // the same cache-key input in every checkout of the repository.
        test
            <@
                inputs = [ "editorconfig:repo:.editorconfig", "root = true\n"
                           "editorconfig:repo:src/.editorconfig", "[*.fs]\nmax_line_length = 80\n" ]
            @>

        // A file that does not exist yet still resolves its directories' configs.
        test <@ editorConfigInputs dir [ Path.Combine(dir, "src", "B.fs") ] |> List.length = 2 @>

        // No config files, no inputs.
        withTempDir "fantomas-noeditorconfig" (fun bare ->
            test <@ List.isEmpty (editorConfigInputs bare [ Path.Combine(bare, "A.fs") ]) @>)

        // A file OUTSIDE the root walks to the filesystem root and stops there, and
        // never reports this repository's configs.
        withTempDir "fantomas-outside" (fun elsewhere ->
            let outside = editorConfigInputs dir [ Path.Combine(elsewhere, "B.fs") ]
            test <@ outside |> List.forall (fun (label, _) -> not (label.Contains dir)) @>))

[<Fact>]
let ``configChainInputs finds any walked-up config file under its own label`` () =
    withTempDir "chain" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        File.WriteAllText(Path.Combine(dir, "fsharplint.json"), "{}")
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")

        let inputs =
            configChainInputs dir "fsharplint.json" "fsharplint" [ Path.Combine(dir, "src", "A.fs") ]

        test <@ inputs = [ "fsharplint:repo:fsharplint.json", "{}" ] @>)

[<Fact>]
let ``configChainInputs stops at the repository root`` () =
    // A config ABOVE the repository belongs to no checkout of it: keying it would split
    // two checkouts that sit under different parents.
    withTempDir "chain-parent" (fun parent ->
        let repo = Path.Combine(parent, "repo")
        Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore
        File.WriteAllText(Path.Combine(parent, ".editorconfig"), "root = true\n")
        File.WriteAllText(Path.Combine(repo, ".editorconfig"), "[*.fs]\n")

        let inputs = editorConfigInputs repo [ Path.Combine(repo, "src", "A.fs") ]

        test <@ inputs = [ "editorconfig:repo:.editorconfig", "[*.fs]\n" ] @>)

[<Fact>]
let ``configChainInputs reports a config shared by several files once`` () =
    withTempDir "chain-shared" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "src", "a")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "src", "b")) |> ignore
        File.WriteAllText(Path.Combine(dir, "fsharplint.json"), "{}")
        File.WriteAllText(Path.Combine(dir, "src", "b", "fsharplint.json"), "[]")

        let inputs =
            configChainInputs
                dir
                "fsharplint.json"
                "fsharplint"
                [ Path.Combine(dir, "src", "a", "A.fs"); Path.Combine(dir, "src", "b", "B.fs") ]

        test
            <@
                inputs = [ "fsharplint:repo:fsharplint.json", "{}"
                           "fsharplint:repo:src/b/fsharplint.json", "[]" ]
            @>)

[<Fact>]
let ``configChainInputs with no files has no inputs`` () =
    withTempDir "chain-nofiles" (fun dir ->
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")
        test <@ List.isEmpty (editorConfigInputs dir []) @>)

[<Fact>]
let ``an unreadable config throws rather than being keyed as absent`` () =
    withTempDir "chain-unreadable" (fun dir ->
        let config = Path.Combine(dir, ".editorconfig")
        File.WriteAllText(config, "root = true\n")

        let canSimulate =
            not (OperatingSystem.IsWindows())
            && (File.SetUnixFileMode(config, UnixFileMode.None)

                try
                    File.ReadAllText config |> ignore
                    false // running as root: permissions are not enforced
                with _ ->
                    true)

        try
            if canSimulate then
                let raised =
                    try
                        editorConfigInputs dir [ Path.Combine(dir, "A.fs") ] |> ignore
                        false
                    with
                    | :? UnauthorizedAccessException
                    | :? IOException -> true

                test <@ raised @>
        finally
            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(config, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
