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

let private declarationOrder root =
    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "semantic-tagger.json")))

    config.RootElement.GetProperty("packages").EnumerateArray()
    |> Seq.map _.GetProperty("name").GetString()
    |> List.ofSeq

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

/// The release used to hand-order itself: one scoped `--only` invocation per dependency
/// level, with an exact-version barrier after each. It had to, because the tagger pushed
/// every tag at once in CONFIG order — and config order is wrong here, which the second
/// half of this test keeps proving. The tagger now derives the publication order from the
/// project references and waits between waves, so hand-ordering is no longer a safeguard;
/// it is a second, hand-maintained copy of an order that already exists, free to drift
/// from the one that actually governs the push.
[<Fact>]
let ``release hands publication order to the tagger and keeps only the end-to-end tool proof`` () =
    let root = repoRoot ()

    let release = taskBlock (miseToml root) "release"

    let taggerLines =
        release.Split('\n')
        |> Array.filter _.Contains("fssemantictagger release")
        |> Array.toList

    // Exactly one, unscoped: `--only` would be this file deciding the order again.
    test <@ taggerLines.Length = 1 @>
    test <@ taggerLines |> List.forall (fun line -> not (line.Contains "--only")) @>
    // And mutating — a release task that previews is a release task that releases nothing.
    test <@ taggerLines |> List.forall (fun line -> not (line.Contains "--dry-run")) @>
    // The tagger must NOT confirm the last wave itself: the barrier below does that, with
    // a real install rather than a feed lookup. Without this flag the final wave is waited
    // for twice, and the release fails on the weaker probe before the stronger one runs.
    test <@ taggerLines |> List.forall _.Contains("--skip-nuget-wait") @>

    let barriers =
        release.Split('\n')
        |> Array.filter _.Contains("wait-for-nuget.fsx")
        |> Array.toList

    // One barrier, on the CLI, after the tagger. It orders nothing — the CLI is last —
    // it proves the published tool installs and runs, which a feed presence check does not.
    test <@ barriers.Length = 1 @>
    test <@ barriers |> List.forall _.Contains("-- FsHotWatch.Cli ") @>

    let taggerAt = release.IndexOf("fssemantictagger release", StringComparison.Ordinal)

    let barrierAt = release.IndexOf("wait-for-nuget.fsx", StringComparison.Ordinal)
    test <@ 0 <= taggerAt && taggerAt < barrierAt @>

    // Why none of the above may become "just follow the config order": in this repository
    // the config order is not a legal publication order. The CLI is declared before every
    // plugin it bundles, so anything that publishes in declaration order publishes a
    // dependent before its dependencies.
    let graph = dependencyGraph root
    let declared = declarationOrder root
    let position = declared |> List.mapi (fun index name -> name, index) |> Map.ofList

    let declaredBeforeItsDependency =
        declared
        |> List.collect (fun package ->
            graph[package]
            |> Set.toList
            |> List.filter (fun dependency -> position[package] < position[dependency])
            |> List.map (fun dependency -> package, dependency))

    test <@ not (List.isEmpty declaredBeforeItsDependency) @>
    test <@ declaredBeforeItsDependency |> List.forall (fst >> (=) "FsHotWatch.Cli") @>

[<Fact>]
let ``release dry run remains one exact whole-release preview`` () =
    let root = repoRoot ()

    let dryRun = taskBlock (miseToml root) "release-dry-run"

    let taggerLines =
        dryRun.Split('\n') |> Array.filter _.Contains("fssemantictagger release")

    test <@ taggerLines = [| "run = \"dotnet tool run fssemantictagger release --dry-run\"" |] @>
    test <@ not (dryRun.Contains("--only")) @>
