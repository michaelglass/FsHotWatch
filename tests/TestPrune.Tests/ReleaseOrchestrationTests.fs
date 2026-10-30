module TestPrune.Tests.ReleaseOrchestrationTests

open System
open System.IO
open Xunit
open Swensen.Unquote

// TestPrune's own repository released by hand-ordered `fssemantictagger --only` levels with a
// restore barrier after every package, and two tests here derived those levels from the
// ProjectReference DAG and failed when mise.toml disagreed. TestPrune now releases from the
// FsHotWatch repository (AUTOMATION-1367), whose `release` task is one unscoped tagger run: the
// tagger derives the publication waves from the same DAG and waits for each wave on NuGet before
// tagging the next, so the hand ordering, and the tests that pinned it, are gone. What stays
// true is that the preview is one whole-release, non-mutating run.

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: mise.toml is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "mise.toml")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let private taskBlock (mise: string) taskName =
    let marker = $"[tasks.%s{taskName}]"

    mise.Split('\n')
    |> Array.skipWhile ((<>) marker)
    |> Array.takeWhile (fun line -> line = marker || not (line.StartsWith("[tasks.", StringComparison.Ordinal)))
    |> String.concat "\n"

[<Fact>]
let ``release dry run remains one whole-release non-mutating preview`` () =
    let root = repoRoot ()

    let dryRun =
        File.ReadAllText(Path.Combine(root, "mise.toml"))
        |> fun mise -> taskBlock mise "release-dry-run"

    let lines =
        dryRun.Split('\n') |> Array.filter _.Contains("fssemantictagger release")

    test <@ lines = [| "run = \"dotnet tool run fssemantictagger release --dry-run\"" |] @>
    test <@ not (dryRun.Contains("--only")) @>

[<Fact>]
let ``release is one unscoped tagger run that orders the waves itself`` () =
    let root = repoRoot ()

    let release =
        File.ReadAllText(Path.Combine(root, "mise.toml"))
        |> fun mise -> taskBlock mise "release"

    let taggerLines =
        release.Split('\n')
        |> Array.filter (fun line -> line.Contains("fssemantictagger") && not (line.TrimStart().StartsWith("#")))

    test <@ taggerLines = [| "dotnet tool run fssemantictagger release --skip-nuget-wait" |] @>
    test <@ not (release.Contains("--only")) @>
