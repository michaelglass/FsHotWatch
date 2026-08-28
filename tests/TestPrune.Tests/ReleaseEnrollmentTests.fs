module TestPrune.Tests.ReleaseEnrollmentTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote

let private repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: no semantic-tagger.json in any ancestor of the test binary"
        elif File.Exists(Path.Combine(directory.FullName, "semantic-tagger.json")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let private read root path =
    File.ReadAllText(Path.Combine(root, path))

let private configuredPackages root =
    use document = JsonDocument.Parse(read root "semantic-tagger.json")

    document.RootElement.GetProperty("packages").EnumerateArray()
    |> Seq.map (fun package ->
        package.GetProperty("name").GetString(),
        package.GetProperty("fsproj").GetString(),
        package.GetProperty("tagPrefix").GetString())
    |> Set.ofSeq

let private yamlJob (workflow: string) (jobName: string) =
    let marker = $"  {jobName}:"

    workflow.Split('\n')
    |> Array.skipWhile (fun line -> line <> marker)
    |> Array.skip 1
    |> Array.takeWhile (fun line -> not (line.StartsWith("  ") && not (line.StartsWith("    "))))
    |> String.concat "\n"

[<Fact>]
let ``SQL packages are enrolled in every release authority`` () =
    let root = repoRoot ()

    let sqlPackages =
        set
            [ "TestPrune.Sql", "src/TestPrune.Sql/TestPrune.Sql.fsproj", "sql-v"
              "TestPrune.SqlHydra", "src/TestPrune.SqlHydra/TestPrune.SqlHydra.fsproj", "sqlhydra-v" ]

    test <@ Set.isSubset sqlPackages (configuredPackages root) @>

    let packTask = read root "mise.toml"
    test <@ packTask.Contains("dotnet pack src/TestPrune.Sql/TestPrune.Sql.fsproj") @>
    test <@ packTask.Contains("dotnet pack src/TestPrune.SqlHydra/TestPrune.SqlHydra.fsproj") @>

    for _, projectPath, _ in sqlPackages do
        let project = read root projectPath
        test <@ project.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>") @>
        test <@ project.Contains("<None Include=\"README.md\" Pack=\"true\" PackagePath=\"\\\"") @>

        let packageReadme =
            read root (Path.Combine(Path.GetDirectoryName(projectPath), "README.md"))

        test <@ packageReadme.Contains("dotnet add package") @>
        test <@ packageReadme.Contains("ITestPruneExtension") @>

        let packageDocs =
            read root (Path.Combine("docs", Path.GetFileName(Path.GetDirectoryName(projectPath)), "index.md"))

        test <@ packageDocs.Contains("dotnet add package") @>
        test <@ packageDocs.Contains("ITestPruneExtension") @>

    let releaseWorkflow = read root ".github/workflows/release.yml"

    for packageName, projectPath, tagPrefix in sqlPackages do
        let laneName = tagPrefix.Replace("-v", "")
        let releaseJob = yamlJob releaseWorkflow $"release-{laneName}"
        let publishJob = yamlJob releaseWorkflow $"publish-{laneName}"
        test <@ releaseWorkflow.Contains($"- '{tagPrefix}*'") @>
        test <@ releaseJob.Contains($"package-name: {packageName}") @>
        test <@ releaseJob.Contains($"tag-prefix: {tagPrefix}") @>
        test <@ releaseJob.Contains($"fsproj-path: {projectPath}") @>
        test <@ publishJob.Contains($"needs: release-{laneName}") @>

    let releaseTask = read root "mise.toml"

    // Dependency ordering is derived from the project graph and enforced by
    // ReleaseOrchestrationTests; enrollment only owns presence in this test.
    test <@ releaseTask.Contains("TestPrune.Core") @>
    test <@ releaseTask.Contains("TestPrune.Sql") @>
    test <@ releaseTask.Contains("TestPrune.SqlHydra") @>

    let readme = read root "README.md"
    test <@ readme.Contains("[`TestPrune.Sql`](https://www.nuget.org/packages/TestPrune.Sql)") @>
    test <@ readme.Contains("[`TestPrune.SqlHydra`](https://www.nuget.org/packages/TestPrune.SqlHydra)") @>
