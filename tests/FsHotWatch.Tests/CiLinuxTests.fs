/// `mise run ci-linux` runs GitHub Actions' CI jobs in a local Linux container, and it is
/// only worth running if what it runs IS what GitHub runs (AUTOMATION-1363).
///
/// scripts/ci-linux/steps.sh derives the plan from `.github/workflows/ci.yml` instead of
/// restating it. These tests read the plan it writes (`--plan`, which needs neither Docker
/// nor dotnet) and pin it to the workflow file: the build job's Lint step is the workflow's
/// `lint-cmd`, its test step measures coverage per project, the local `integration` job's commands
/// are its `run:` lines, and a workflow shape it does not model is refused, not skipped.
module FsHotWatch.Tests.CiLinuxTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.RepoTasks
open FsHotWatch.Tests.TestHelpers

/// `steps.sh --plan <yml> <plan-file>`: the exit outcome and, on success, the plan text.
let private plan (yml: string) =
    withTempDir "ci-linux-plan" (fun dir ->
        let planFile = Path.Combine(dir, "plan.txt")
        let script = Path.Combine(repoRoot (), "scripts", "ci-linux", "steps.sh")

        let outcome =
            runProcess
                "bash"
                $"\"%s{script}\" --plan \"%s{yml}\" \"%s{planFile}\""
                dir
                []
                (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))

        let text =
            if File.Exists planFile then
                Some(File.ReadAllText planFile)
            else
                None

        outcome, text)

/// The planned steps as (job / name, command) pairs, in order.
let private steps (planText: string) =
    Regex.Matches(planText, @"^## (.+)\n((?:.+\n)*)", RegexOptions.Multiline)
    |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value.TrimEnd('\n'))
    |> Seq.toList

let private ciYml () =
    Path.Combine(repoRoot (), ".github", "workflows", "ci.yml")

/// A single-quoted `key:` value of the workflow, as GitHub hands it to the reusable workflow.
let private quotedInput key =
    let m =
        Regex.Match(File.ReadAllText(ciYml ()), $@"^\s*%s{key}:\s*'([^']*)'", RegexOptions.Multiline)

    test <@ m.Success @>
    m.Groups[1].Value

let private repoPlan () =
    match plan (ciYml ()) with
    | Succeeded _, Some text -> steps text
    | outcome, _ -> failwith $"steps.sh --plan failed: %s{outputOf outcome}"

[<Fact>]
let ``the build job's Lint step is the workflow's lint-cmd, verbatim`` () =
    let lint = repoPlan () |> List.find (fun (name, _) -> name = "build / Lint")

    test <@ snd lint = quotedInput "lint-cmd" @>

[<Fact>]
let ``the build job tests every project with coverage, and no shared coverage settings`` () =
    let _, command =
        repoPlan () |> List.find (fun (name, _) -> name = "build / Test with coverage")

    let testLines =
        command.Split('\n') |> Array.filter (fun l -> l.Contains "dotnet test")

    test <@ testLines.Length = 2 @>

    // The test projects need different module filters (FsHotWatch.Tests excludes the TestPrune
    // assemblies TestPrune.Tests measures), so each carries its own testconfig.json and the
    // workflow hands them none: one `--coverage-settings` would apply to every project.
    test <@ testLines |> Array.forall (fun l -> not (l.Contains "--coverage-settings")) @>

    test <@ not (Regex.IsMatch(File.ReadAllText(ciYml ()), @"^\s*test-extra-args:", RegexOptions.Multiline)) @>

    test <@ command.Contains "--coverage-output-format cobertura" @>

[<Fact>]
let ``every test project the build job measures carries its own coverage filters`` () =
    let testsDir = Path.Combine(repoRoot (), "tests")

    let missing =
        FsHotWatch.Tests.TestHelpers.filesUnder FsHotWatch.SafeWalk.SourceExcludedDirs "*.Tests.fsproj" testsDir
        |> List.filter (fun proj -> not (File.Exists(Path.Combine(Path.GetDirectoryName proj, "testconfig.json"))))
        |> List.map (fun proj -> Path.GetRelativePath(testsDir, proj))

    test <@ List.isEmpty missing @>

[<Fact>]
let ``the build job checks the coverage floors, format and docs as the reusable workflow does`` () =
    let names = repoPlan () |> List.map fst

    let required =
        [ "build / Format check"
          "build / Sync docs check"
          "build / Build"
          "build / Build (Release)"
          "build / Coverage ratchet check"
          "build / Build examples"
          "lint-project / Lint project" ]

    test <@ required |> List.filter (fun r -> not (List.contains r names)) |> List.isEmpty @>

[<Fact>]
let ``the integration job runs the workflow's own run lines, ending in the integration suite`` () =
    let integration =
        repoPlan ()
        |> List.filter (fun (name, _) -> name.StartsWith("integration / ", StringComparison.Ordinal))
        |> List.map snd

    test
        <@
            List.last integration = "dotnet test --project tests/FsHotWatch.IntegrationTests/FsHotWatch.IntegrationTests.fsproj --no-build"
        @>

[<Fact>]
let ``a workflow input ci-linux does not model is refused, not skipped`` () =
    withTempDir "ci-linux-unmodelled" (fun dir ->
        let yml = Path.Combine(dir, "ci.yml")

        File.WriteAllText(
            yml,
            "jobs:\n  build:\n    uses: michaelglass/MichaelsWackyFsPackageTools/.github/workflows/michaels-wacky-build.yml@main\n    with:\n      solution: X.slnx\n      brand-new-input: 'yes'\n"
        )

        match plan yml with
        | Failed(code, _), text -> test <@ code = 2 && text.IsNone @>
        | outcome, _ -> failwith $"expected a refusal, got %s{outputOf outcome}")

[<Fact>]
let ``a step that uses an action ci-linux does not emulate is refused`` () =
    withTempDir "ci-linux-action" (fun dir ->
        let yml = Path.Combine(dir, "ci.yml")

        File.WriteAllText(
            yml,
            "jobs:\n  other:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v6\n      - uses: some/action@v1\n      - run: echo hi\n"
        )

        match plan yml with
        | Failed(code, _), _ -> test <@ code = 2 @>
        | outcome, _ -> failwith $"expected a refusal, got %s{outputOf outcome}")

[<Fact>]
let ``mise run ci-linux runs the script, and its output directory is ignored`` () =
    let root = repoRoot ()

    test <@ (commandLines (miseToml root) "ci-linux").Contains "run = \"bash scripts/ci-linux.sh\"" @>

    test
        <@
            File.ReadAllLines(Path.Combine(root, ".gitignore"))
            |> Array.exists (fun l -> l.Trim() = ".ci-linux/")
        @>
