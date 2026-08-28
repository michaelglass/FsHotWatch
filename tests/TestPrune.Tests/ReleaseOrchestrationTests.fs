module TestPrune.Tests.ReleaseOrchestrationTests

open System
open System.IO
open System.Text.Json
open System.Xml.Linq
open Xunit
open Swensen.Unquote

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

let private releaseNodes root =
    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "semantic-tagger.json")))

    config.RootElement.GetProperty("packages").EnumerateArray()
    |> Seq.map (fun package ->
        let name = package.GetProperty("name").GetString()

        let paths =
            seq {
                yield package.GetProperty("fsproj").GetString()

                match package.TryGetProperty("fsProjsSharingSameTag") with
                | true, shared -> yield! shared.EnumerateArray() |> Seq.map _.GetString()
                | false, _ -> ()
            }
            |> Seq.map (fun relativePath -> relativePath, Path.Combine(root, relativePath) |> Path.GetFullPath)
            |> Seq.toList

        name, paths)
    |> Map.ofSeq

let private releaseArtifacts root =
    releaseNodes root
    |> Map.map (fun _ projectPaths ->
        let artifacts =
            projectPaths
            |> List.map (fun (relativePath, fullPath) ->
                let project = XDocument.Load fullPath

                let packageId =
                    project.Descendants(XName.Get "PackageId") |> Seq.exactlyOne |> _.Value

                packageId, relativePath)

        artifacts)

let private dependencyGraph root =
    let projects = releaseNodes root

    let packageByPath =
        projects
        |> Map.toSeq
        |> Seq.collect (fun (name, paths) -> paths |> Seq.map (fun (_, path) -> path, name))
        |> Map.ofSeq

    projects
    |> Map.map (fun owner projectPaths ->
        projectPaths
        |> Seq.collect (fun (_, projectPath) ->
            XDocument.Load(projectPath).Descendants(XName.Get "ProjectReference")
            |> Seq.choose (fun reference ->
                let relativePath = reference.Attribute(XName.Get "Include").Value

                Path.GetFullPath(relativePath, Path.GetDirectoryName(projectPath))
                |> fun path -> Map.tryFind path packageByPath))
        |> Set.ofSeq
        |> Set.remove owner)

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

let private assertOrderedRelease root taskName verb =
    let task =
        File.ReadAllText(Path.Combine(root, "mise.toml"))
        |> fun mise -> taskBlock mise taskName

    let graph = dependencyGraph root
    let expectedLevels = dependencyLevels graph
    let artifacts = releaseArtifacts root
    let marker = $"fssemantictagger %s{verb} --only "

    let actualLevels =
        task.Split('\n')
        |> Array.choose (fun line ->
            let markerAt = line.IndexOf(marker, StringComparison.Ordinal)

            if markerAt < 0 then
                None
            else
                line.Substring(markerAt + marker.Length).Split(',', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map _.Trim()
                |> Set.ofArray
                |> Some)
        |> Array.toList

    test <@ actualLevels = expectedLevels @>

    let mutatingTaggerLines =
        task.Split('\n')
        |> Array.filter (fun line -> line.Contains("fssemantictagger") && not (line.TrimStart().StartsWith("#")))

    test <@ mutatingTaggerLines.Length = expectedLevels.Length @>
    test <@ mutatingTaggerLines |> Array.forall _.Contains(marker) @>

    test
        <@
            mutatingTaggerLines
            |> Array.forall (fun line -> not (line.Contains("--dry-run")))
        @>

    let releases =
        actualLevels
        |> List.map (fun level -> level, task.IndexOf(marker + String.concat "," level))

    for level, releaseAt in releases do
        test <@ releaseAt >= 0 @>

        for package in level do
            for packageId, projectPath in artifacts[package] do
                let command = $"wait-for-nuget.fsx -- %s{packageId} %s{projectPath}"
                let waitAt = task.IndexOf(command, releaseAt, StringComparison.Ordinal)
                test <@ waitAt > releaseAt @>
                test <@ task.IndexOf(command, waitAt + command.Length, StringComparison.Ordinal) < 0 @>

        match releases |> List.tryFindIndex (fst >> (=) level) with
        | Some index when index + 1 < releases.Length ->
            let _, nextReleaseAt = releases[index + 1]

            for package in level do
                for packageId, _ in artifacts[package] do
                    test
                        <@
                            task.IndexOf($"wait-for-nuget.fsx -- %s{packageId} ", releaseAt, StringComparison.Ordinal) < nextReleaseAt
                        @>
        | _ -> ()

[<Fact>]
let ``stable release follows the semantic project DAG with an exact barrier after every package`` () =
    assertOrderedRelease (repoRoot ()) "release" "release"

[<Fact>]
let ``alpha release follows the semantic project DAG with an exact barrier after every package`` () =
    assertOrderedRelease (repoRoot ()) "release-alpha" "alpha"

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
