/// `mise run ci` and GitHub Actions must run the same commands, and the integration
/// suite must be in both while staying out of `check`.
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

    test <@ runOf (miseToml root) "ci" = lintCmd yml + " && " + integrationCommand @>
    test <@ List.last (integrationJobRuns yml) = integrationCommand @>

[<Fact>]
let ``the inner loop never runs the integration suite`` () =
    let root = repoRoot ()
    let mise = miseToml root

    let running =
        dependencyClosure mise "check"
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
