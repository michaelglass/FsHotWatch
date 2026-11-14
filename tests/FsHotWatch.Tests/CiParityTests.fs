/// `mise run ci` and GitHub Actions must run the same commands, and the integration
/// suite must be in both while staying out of `check` and `land-check`.
///
/// `.fshw.json` declares FsHotWatch.IntegrationTests out of the daemon's test slot, so
/// neither verb runs it and something else has to. For a long time that "something" was
/// claimed to be the reusable CI workflow's test step, which runs `*.Tests.fsproj` only
/// and never reached it: the suite was red on Linux, and nothing said so. These tests pin
/// where it runs now, to the files that run it.
module FsHotWatch.Tests.CiParityTests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

let private integrationCommand =
    "dotnet test --project tests/FsHotWatch.IntegrationTests/FsHotWatch.IntegrationTests.fsproj --no-build"

let private ciYml root =
    File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))

/// The single-quoted `lint-cmd:` the build job hands the reusable workflow.
let private lintCmd (yml: string) =
    let m = Regex.Match(yml, @"^\s*lint-cmd:\s*'([^']*)'", RegexOptions.Multiline)
    test <@ m.Success @>
    m.Groups[1].Value

/// The `run:` lines of the `integration` job, from its header to the next top-level job.
let private integrationJobRuns (yml: string) =
    let lines = yml.Split('\n')

    lines
    |> Array.skipWhile (fun line -> line <> "  integration:")
    |> Array.skip 1
    |> Array.takeWhile (fun line -> not (Regex.IsMatch(line, @"^  [A-Za-z-]+:\s*$")))
    |> Array.choose (fun line ->
        let m = Regex.Match(line, @"^\s*(?:- )?run:\s*(.+)$")
        if m.Success then Some(m.Groups[1].Value.Trim()) else None)
    |> Array.toList

/// The single-line `run = "..."` of a task.
let private runOf (mise: string) taskName =
    let m =
        Regex.Match(commandLines mise taskName, "^run\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline)

    test <@ m.Success @>
    m.Groups[1].Value

[<Fact>]
let ``ci runs the confirm CI runs, then the integration suite CI runs`` () =
    let root = repoRoot ()
    let yml = ciYml root

    // Through scripts/ci-halves.sh and not `confirm && integration`: chained with `&&`, a red
    // confirm skipped the integration half, and nothing said per half what ran or was reused
    // (AUTOMATION-1689). The script runs both halves always; CiHalvesTests pins its contract.
    test <@ runOf (miseToml root) "ci" = $"bash scripts/ci-halves.sh '%s{lintCmd yml}' '%s{integrationCommand}'" @>
    test <@ List.last (integrationJobRuns yml) = integrationCommand @>

[<Fact>]
let ``the inner loop never runs the integration suite`` () =
    let root = repoRoot ()
    let mise = miseToml root

    let running =
        // The landing gate (`land-check`) is the inner loop's command too; AUTOMATION-1614 keeps
        // the integration suite out of both, and the post-land spot check (`ci`) runs it.
        Set.union (dependencyClosure mise "check") (dependencyClosure mise "land-check")
        |> Set.filter (fun task -> (commandLines mise task).Contains "FsHotWatch.IntegrationTests")

    test <@ Set.isEmpty running @>

[<Fact>]
let ``the integration suite is declared out of the daemon's test slot, with a reason that says where it runs`` () =
    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot (), ".fshw.json")))

    let tests = config.RootElement.GetProperty("tests")

    let gated =
        tests.GetProperty("projects").EnumerateArray()
        |> Seq.map (fun p -> p.GetProperty("project").GetString())
        |> Seq.toList

    let reason =
        tests.GetProperty("excluded").EnumerateArray()
        |> Seq.tryFind (fun e -> e.GetProperty("project").GetString() = "tests/FsHotWatch.IntegrationTests")
        |> Option.map (fun e -> e.GetProperty("reason").GetString())

    test
        <@
            not (
                gated
                |> List.exists (fun p -> p.Contains("IntegrationTests", StringComparison.Ordinal))
            )
        @>

    test
        <@
            reason
            |> Option.exists (fun r -> r.Contains "`mise run ci`" && r.Contains "`integration` job")
        @>

/// The `tests.beforeRun` steps of this repository's `.fshw.json`, as written.
let private testBeforeRunSteps (tests: JsonElement) =
    match tests.TryGetProperty "beforeRun" with
    | true, step when step.ValueKind = JsonValueKind.String -> [ step.GetString() ]
    | true, steps when steps.ValueKind = JsonValueKind.Array ->
        steps.EnumerateArray() |> Seq.map (fun step -> step.GetString()) |> Seq.toList
    | _ -> []

[<Fact>]
let ``a test run does not repeat the build the build plugin ran before it`` () =
    // A test run starts on the build plugin's BuildSucceeded, so the tree it tests was
    // built by `build.command` already. A `tests.beforeRun` step that runs the same
    // command again re-evaluates every project of the solution before each run: 3.4 to
    // 6.9 s on every one-file edit, measured on this repository (AUTOMATION-943).
    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot (), ".fshw.json")))

    let build = config.RootElement.GetProperty("build")

    let buildLine =
        $"""%s{build.GetProperty("command").GetString()} %s{build.GetProperty("args").GetString()}"""

    let repeated =
        testBeforeRunSteps (config.RootElement.GetProperty("tests"))
        |> List.filter (fun step -> step.Trim() = buildLine.Trim())

    test <@ List.isEmpty repeated @>

// AUTOMATION-1025. RepositoryIdentityTests' real-jj case is the only end-to-end proof that
// a colocated primary, a jj workspace and a git worktree resolve to one repository, and
// it needs jj on PATH. The build job's runner has none, so on CI it never ran.

/// The single-quoted `pre-build-cmd:` the build job hands the reusable workflow, if any.
let private preBuildCmd (yml: string) =
    let m = Regex.Match(yml, @"^\s*pre-build-cmd:\s*'([^']*)'", RegexOptions.Multiline)
    if m.Success then Some m.Groups[1].Value else None

[<Fact>]
let ``the CI build job installs jj before it builds and tests`` () =
    let root = repoRoot ()
    let installer = Path.Combine(root, "scripts", "install-jj.sh")
    test <@ File.Exists installer @>
    test <@ preBuildCmd (ciYml root) = Some "bash scripts/install-jj.sh" @>

[<Fact>]
let ``the nightly replay installs jj with the same script`` () =
    let root = repoRoot ()

    let nightly =
        File.ReadAllText(Path.Combine(root, ".github", "workflows", "nightly-replay.yml"))

    test <@ nightly.Contains "run: bash scripts/install-jj.sh" @>

// AUTOMATION-1680. CI's build job runs `dotnet fantomas <fantomas-paths> --check`, and main went
// red on it three times (c12ac32f, d6457cae, 05d7e16c) over trees both local gates had passed:
// the daemon's `format: true` pass REWRITES an unformatted file during the check rather than
// failing it, so the check certified the rewritten working copy while the commit kept the old
// bytes. The local gates now run CI's own command, read from the workflow, so they cannot drift.

/// The single-quoted `fantomas-paths:` the build job hands the reusable workflow.
let private fantomasPaths (yml: string) =
    let m = Regex.Match(yml, @"^\s*fantomas-paths:\s*'([^']*)'", RegexOptions.Multiline)
    test <@ m.Success @>
    m.Groups[1].Value

[<Fact>]
let ``land-check and ci run CI's exact format check`` () =
    let root = repoRoot ()
    let mise = miseToml root
    // The reusable workflow's "Format check" step, as scripts/ci-linux/steps.sh transcribes it.
    let ciCommand = $"dotnet fantomas %s{fantomasPaths (ciYml root)} --check"

    test <@ runOf mise "fantomas-check" = ciCommand @>
    test <@ dependsOf mise "land-check" |> List.contains "fantomas-check" @>
    test <@ dependsOf mise "ci" |> List.contains "fantomas-check" @>
