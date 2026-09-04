module FsHotWatch.Tests.ReleaseOrchestrationTests

open System
open System.IO
open System.Text.Json
open System.Xml.Linq
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

let private releaseProjects root =
    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "semantic-tagger.json")))

    config.RootElement.GetProperty("packages").EnumerateArray()
    |> Seq.map (fun package ->
        let name = package.GetProperty("name").GetString()
        let relativePath = package.GetProperty("fsproj").GetString()
        let path = Path.Combine(root, relativePath) |> Path.GetFullPath
        name, path)
    |> Map.ofSeq

let private dependencyGraph root =
    let projects = releaseProjects root

    let packageByPath =
        projects |> Map.toSeq |> Seq.map (fun (name, path) -> path, name) |> Map.ofSeq

    projects
    |> Map.map (fun _ projectPath ->
        let project = XDocument.Load projectPath

        project.Descendants(XName.Get "ProjectReference")
        |> Seq.choose (fun reference ->
            let relativePath = reference.Attribute(XName.Get "Include").Value

            let dependencyPath =
                Path.GetFullPath(relativePath, Path.GetDirectoryName(projectPath))

            Map.tryFind dependencyPath packageByPath)
        |> Set.ofSeq)

let private dependencyLevels (graph: Map<string, Set<string>>) =
    let rec build released remaining levels =
        if Set.isEmpty remaining then
            List.rev levels
        else
            let ready =
                remaining |> Set.filter (fun package -> Set.isSubset graph[package] released)

            test <@ not (Set.isEmpty ready) @>
            build (Set.union released ready) (Set.difference remaining ready) (ready :: levels)

    build Set.empty (graph |> Map.keys |> Set.ofSeq) []

[<Fact>]
let ``release emits every dependency lane before the CLI lane`` () =
    let root = repoRoot ()

    let release = taskBlock (miseToml root) "release"

    let graph = dependencyGraph root
    let expectedLevels = dependencyLevels graph

    let releaseLevels =
        release.Split('\n')
        |> Array.choose (fun line ->
            let marker = "fssemantictagger release --only "
            let markerAt = line.IndexOf(marker, StringComparison.Ordinal)

            if markerAt < 0 then
                None
            else
                line.Substring(markerAt + marker.Length).Split(',', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map _.Trim()
                |> Array.toList
                |> Some)
        |> Array.toList

    test <@ releaseLevels |> List.map Set.ofList = expectedLevels @>
    test <@ releaseLevels |> List.concat |> Set.ofList = (graph |> Map.keys |> Set.ofSeq) @>
    test <@ releaseLevels |> List.concat |> List.countBy id |> List.forall (snd >> (=) 1) @>

    let mutatingReleaseLines =
        release.Split('\n')
        |> Array.filter _.Contains("fssemantictagger release")
        |> Array.toList

    test <@ mutatingReleaseLines.Length = expectedLevels.Length @>
    test <@ mutatingReleaseLines |> List.forall _.Contains("release --only ") @>

    test
        <@
            mutatingReleaseLines
            |> List.forall (fun line -> not (line.Contains("--dry-run")))
        @>

    let coreRelease =
        release.IndexOf("release --only FsHotWatch\n", StringComparison.Ordinal)

    let pluginRelease =
        release.IndexOf("release --only FsHotWatch.TestPrune,", StringComparison.Ordinal)

    let cliRelease =
        release.IndexOf("release --only FsHotWatch.Cli\n", StringComparison.Ordinal)

    test <@ 0 <= coreRelease && coreRelease < pluginRelease && pluginRelease < cliRelease @>

    for package in graph |> Map.keys do
        let waitCommand = $"wait-for-nuget.fsx -- %s{package} "
        let waitAt = release.IndexOf(waitCommand, StringComparison.Ordinal)
        test <@ waitAt >= 0 @>
        test <@ release.IndexOf(waitCommand, waitAt + waitCommand.Length, StringComparison.Ordinal) < 0 @>

        if package = "FsHotWatch" then
            test <@ coreRelease < waitAt && waitAt < pluginRelease @>
        elif package = "FsHotWatch.Cli" then
            test <@ cliRelease < waitAt @>
        else
            test <@ pluginRelease < waitAt && waitAt < cliRelease @>

[<Fact>]
let ``release dry run remains one exact whole-release preview`` () =
    let root = repoRoot ()

    let dryRun = taskBlock (miseToml root) "release-dry-run"

    let taggerLines =
        dryRun.Split('\n') |> Array.filter _.Contains("fssemantictagger release")

    test <@ taggerLines = [| "run = \"dotnet tool run fssemantictagger release --dry-run\"" |] @>
    test <@ not (dryRun.Contains("--only")) @>
