/// The files a rewriting preprocessor may be handed: the repository's own (AUTOMATION-1680
/// follow-up). The format preprocessor rewrote a package's `_content` source in the shared
/// NuGet cache, which a project compiles in from outside the repository.
module FsHotWatch.Tests.RepoFilesTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Plugin
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

let private write (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private classify (repo: string) (file: string) =
    RepoFiles.parseWith (PathFilter.GitIgnores repo) repo file

[<Fact>]
let ``a file under the root is the repository's, as a relative spelling too`` () =
    withTempDir "repofile" (fun repo ->
        let own = Path.Combine(repo, "src", "A.fs")
        write own "module A"

        test <@ classify repo own |> Result.map RepoFiles.path = Ok own @>
        test <@ classify repo "src/A.fs" |> Result.map RepoFiles.path = Ok "src/A.fs" @>)

[<Fact>]
let ``a package source outside the root, and one behind a link out of the tree, are OutsideRoot`` () =
    withTempDir "repofile" (fun repo ->
        withTempDir "repofile-pkg" (fun nuget ->
            let package = Path.Combine(nuget, "_content", "DefaultResultWriters.fs")
            write package "module Pkg"
            Directory.CreateSymbolicLink(Path.Combine(repo, ".devenv"), nuget) |> ignore

            let throughLink =
                Path.Combine(repo, ".devenv", "_content", "DefaultResultWriters.fs")

            test <@ classify repo package = Error RepoFiles.Excluded.OutsideRoot @>
            test <@ classify repo throughLink = Error RepoFiles.Excluded.OutsideRoot @>))

[<Fact>]
let ``an ignored file and build output are not the repository's to rewrite`` () =
    withTempDir "repofile" (fun repo ->
        write (Path.Combine(repo, ".gitignore")) "generated/\n*.g.fs\n"
        let underIgnored = Path.Combine(repo, "generated", "Gen.fs")
        let ignoredName = Path.Combine(repo, "src", "Thing.g.fs")
        let output = Path.Combine(repo, "src", "obj", "Debug", "AssemblyInfo.fs")
        write underIgnored "module Gen"
        write ignoredName "module Thing"
        write output "module Info"

        test <@ classify repo underIgnored = Error RepoFiles.Excluded.Ignored @>
        test <@ classify repo ignoredName = Error RepoFiles.Excluded.Ignored @>
        test <@ classify repo output = Error RepoFiles.Excluded.BuildOutput @>)

/// A formatter double: rewrites every file it is handed whose bytes it would change.
let private rewriter (handed: ResizeArray<string>) =
    { new IFsHotWatchPreprocessor with
        member _.Name = "format"

        member _.Process (repoFiles: RepoFiles.RepoFile list) (_repoRoot: string) =
            let files = repoFiles |> List.map RepoFiles.path
            handed.AddRange files

            let modified =
                files
                |> List.filter (fun f -> File.Exists f && (File.ReadAllText f).Contains "let  x")

            for f in modified do
                File.WriteAllText(f, (File.ReadAllText f).Replace("let  x", "let x"))

            Ok
                { Modified = modified
                  Considered = files.Length
                  Failures = []
                  Evidence = "test rewriter" }

        member _.Dispose() = () }

[<Fact>]
let ``a scan over a project that compiles in an outside file leaves it untouched and the check green`` () =
    withTempDir "repofile-scan" (fun repo ->
        withTempDir "repofile-scan-pkg" (fun nuget ->
            let own = Path.Combine(repo, "src", "Lib.fs")
            write own "module Lib\nlet x = 1\n"
            let package = Path.Combine(nuget, "_content", "DefaultResultWriters.fs")
            let pristine = "module Pkg\nlet  x = 1\n"
            write package pristine
            write (Path.Combine(repo, ".gitignore")) "*.g.fs\n"
            let ignored = Path.Combine(repo, "src", "Thing.g.fs")
            write ignored pristine
            Directory.CreateSymbolicLink(Path.Combine(repo, "linked"), nuget) |> ignore

            let throughLink =
                Path.Combine(repo, "linked", "_content", "DefaultResultWriters.fs")

            let host = PluginHost.create (Unchecked.defaultof<_>) repo
            let handed = ResizeArray<string>()
            host.RegisterPreprocessor(rewriter handed)

            let run =
                host.RunPreprocessorsFor(PreprocessPass.Judge, [ own; package; ignored; throughLink ])

            test <@ List.ofSeq handed = [ own ] @>
            test <@ File.ReadAllText package = pristine @>
            test <@ File.ReadAllText ignored = pristine @>
            test <@ List.isEmpty run.Modified @>
            test <@ List.isEmpty run.Refused && List.isEmpty run.Failures @>

            match host.GetStatus "format" with
            | Some(Events.PluginStatus.Failed _) -> failwith "the scan failed on a file that is not the repository's"
            | _ -> ()))
