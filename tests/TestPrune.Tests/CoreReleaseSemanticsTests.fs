module TestPrune.Tests.CoreReleaseSemanticsTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote

let private repoRoot () =
    let rec find (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: semantic-tagger.json is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "semantic-tagger.json")) then
            directory.FullName
        else
            find directory.Parent

    find (DirectoryInfo(AppContext.BaseDirectory))

let private read root path =
    File.ReadAllText(Path.Combine(root, path))

[<Fact>]
let ``Core 8 schema release is tagger-ready with library and CLI enrolled together`` () =
    let root = repoRoot ()
    let coreProject = read root "src/TestPrune.Core/TestPrune.Core.fsproj"
    let cliProject = read root "src/TestPrune/TestPrune.fsproj"
    let coreChangelog = read root "src/TestPrune.Core/CHANGELOG.md"
    let cliChangelog = read root "src/TestPrune/CHANGELOG.md"

    // The release tagger's version-bump commit must pass CI before its tag is pushed.
    // Accept both valid states: the last released version with Core 8 still pending, or
    // the synchronized Core 8 candidate with matching changelog headings.
    let taggerReady =
        coreProject.Contains("<Version>7.0.1</Version>")
        && cliProject.Contains("<Version>7.0.1</Version>")
        && not (coreChangelog.Contains("## 8.0.0"))
        && not (cliChangelog.Contains("## 8.0.0"))

    let taggerBumped =
        coreProject.Contains("<Version>8.0.0</Version>")
        && cliProject.Contains("<Version>8.0.0</Version>")
        && coreChangelog.Contains("## 8.0.0 -")
        && cliChangelog.Contains("## 8.0.0 -")

    test <@ taggerReady || taggerBumped @>

    use tagger = JsonDocument.Parse(read root "semantic-tagger.json")

    let core =
        tagger.RootElement.GetProperty("packages").EnumerateArray()
        |> Seq.find (fun package -> package.GetProperty("name").GetString() = "TestPrune.Core")

    let tagPrefix = core.GetProperty("tagPrefix").GetString()
    test <@ tagPrefix = "core-v" @>

    let sharedProjects =
        core.GetProperty("fsProjsSharingSameTag").EnumerateArray()
        |> Seq.map _.GetString()
        |> Set.ofSeq

    test <@ sharedProjects = set [ "src/TestPrune/TestPrune.fsproj" ] @>

    let workflow = read root ".github/workflows/release.yml"
    test <@ workflow.Contains("- 'core-v*'") @>
    test <@ workflow.Contains("extra-fsproj-paths: 'src/TestPrune/TestPrune.fsproj'") @>

    let unreleased = coreChangelog.Split("## 7.0.1", StringSplitOptions.None)[0]
    test <@ unreleased.Contains("## Unreleased") @>
    test <@ unreleased.Contains("- feat!:") @>
    test <@ coreChangelog.Contains("SchemaVersion` 11 -> 12") @>
    test <@ cliChangelog.Contains("SchemaVersion` 11 -> 12") @>
