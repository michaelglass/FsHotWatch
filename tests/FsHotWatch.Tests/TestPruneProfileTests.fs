/// The plugin end of `Profile`: every project of every run leaves a
/// `<Project>.profile.jsonl` in the run directory — including a project whose host was
/// killed at its timeout and wrote no report (AUTOMATION-1300).
module FsHotWatch.Tests.TestPruneProfileTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

let private projectOf (command: string) (args: string) (timeoutSec: int option) : TestConfig =
    { Project = "Profiled.Tests"
      Command = command
      Args = args
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = timeoutSec
      ReportVerificationFormat = Disabled
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      AlwaysClasses = [] }

let private project (args: string) (timeoutSec: int option) : TestConfig = projectOf "sh" args timeoutSec

let private runOnce (config: TestConfig) : string * Profile.Line list =
    let root = isolatedRoot ()
    let host = PluginHost.create (Unchecked.defaultof<_>) root
    let handler = create ":memory:" root (Some [ config ]) None None None None []
    host.RegisterHandler(handler)

    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsSome @>

    use doc = JsonDocument.Parse(result.Value)

    let status =
        doc.RootElement.GetProperty("projects").[0].GetProperty("status").GetString()

    let runDir =
        Directory.GetDirectories(FsHotWatch.Ctrf.reportsDir root) |> Array.exactlyOne

    status, Profile.read (Profile.pathIn runDir config.Project)

[<Fact(Timeout = 120_000)>]
let ``a project killed at its timeout still leaves a profile with its start, host pid, end and why no classes`` () =
    let status, lines = runOnce (project "-c \"echo started; exec sleep 60\"" (Some 2))

    test <@ status = "timed-out" @>

    match lines with
    | Profile.Start("Profiled.Tests", _, cores, Some 2, _) :: _ -> test <@ cores >= 1 @>
    | other -> failwith $"no start line: %A{other}"

    test
        <@
            lines
            |> List.exists (function
                | Profile.Spawn(_, pid) -> pid > 0
                | _ -> false)
        @>

    let ends =
        lines
        |> List.choose (function
            | Profile.End(_, wall, _, _, outcome) -> Some(wall, outcome)
            | _ -> None)

    match ends with
    | [ (wall, "timed-out") ] -> test <@ wall >= 2000L @>
    | other -> failwith $"unexpected end lines: %A{other}"

    test
        <@
            lines
            |> List.exists (function
                | Profile.ClassesUnavailable _ -> true
                | _ -> false)
        @>

[<Fact(Timeout = 120_000)>]
let ``a passing project's profile ends passed`` () =
    let status, lines = runOnce (project "-c \"exit 0\"" None)

    test <@ status = "passed" @>

    test
        <@
            lines
            |> List.exists (function
                | Profile.End(_, _, _, _, "passed") -> true
                | _ -> false)
        @>

let private classLines (lines: Profile.Line list) =
    lines
    |> List.choose (function
        | Profile.Class(t, _, source) -> Some(t.Class, t.Tests, source)
        | _ -> None)

[<Fact(Timeout = 120_000)>]
let ``a host killed at its timeout still gets class lines from the results it printed before the kill`` () =
    let status, lines =
        runOnce (
            project
                "-c \"echo 'passed A.Slow.one (40ms)'; echo 'passed A.Slow.two (60ms)'; echo 'passed B.Fast.one (1ms)'; exec sleep 60\""
                (Some 2)
        )

    test <@ status = "timed-out" @>

    test
        <@
            classLines lines |> List.sortBy (fun (c, _, _) -> c) = [ ("A.Slow", 2, Profile.ClassSource.Console)
                                                                     ("B.Fast", 1, Profile.ClassSource.Console) ]
        @>

// THE DONE-CRITERION, on a real host: the xUnit v4 fixture's test host, two of whose classes
// finish while a third hangs, killed at a short timeout. It writes no CTRF report; the class
// lines come from the per-test results fshw asked it to print (`--output Detailed`).
//
// The timeout counts from the spawn, so the host is started from its apphost, not through
// `dotnet run`, whose MSBuild evaluation a loaded box has stretched toward 30 s
// (docs/wall-clock-bounds.md). `sh` drops the project file it is handed, which is there
// only so runner detection reads the fixture's restored xUnit major and injects the
// switches; everything fshw appends reaches the host. The quick tests finish milliseconds
// after the start, which is the one child this timeout must outlast: `HangWaitMs`, the
// bound for one trivial child on a loaded box (docs/wall-clock-bounds.md, rule 2). It is
// also this test's whole cost, once per case: the per-class lines need no opt-in, and the
// name of the hung test comes only with `nameHungTests` (AUTOMATION-1353).
[<Theory(Timeout = 300_000)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``a real xUnit host killed at its timeout still gets per-class lines`` (nameHungTests: bool) =
    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let fixtureDir = Path.Combine(repoRoot, "tests", "Fixtures", "Xunit4RunnerFixture")
    let fixtureProject = Path.Combine(fixtureDir, "Xunit4RunnerFixture.fsproj")

    let apphost =
        Path.Combine(fixtureDir, "bin", "Debug", "net10.0", "Xunit4RunnerFixture")

    let config =
        { projectOf
              "sh"
              $"-c \"shift; exec '%s{apphost}' \\\"$@\\\"\" sh \"%s{fixtureProject}\""
              (Some(HangWaitMs / 1000)) with
            // `CI`: the host colours its output, as it does on every CI runner (GitHub sets it), so
            // this test reads what CI reads on every platform. Uncoloured output is pinned by
            // the parser tests.
            Environment = [ "FSHW_FIXTURE_HANG", "1"; "CI", "true" ]
            ReportVerificationFormat = AutoDetect
            NameHungTests = nameHungTests }

    let status, lines = runOnce config

    test <@ status = "timed-out" @>

    let classes = classLines lines

    // `Quick` and the module's own fact finished and printed; `Hanging` never did, and the
    // test output `Quick` wrote (an indented `passed …` line) is no class.
    test
        <@
            classes
            |> List.contains ("Xunit4RunnerFixture.Tests+Quick", 2, Profile.ClassSource.Console)
        @>

    test
        <@
            classes
            |> List.contains ("Xunit4RunnerFixture.Tests", 1, Profile.ClassSource.Console)
        @>

    test
        <@
            classes
            |> List.forall (fun (c, _, _) -> c <> "Xunit4RunnerFixture.Tests+Hanging" && c <> "Not.A")
        @>

    // And, for a project that asks, the test that was running when the host was killed, from
    // xUnit's long-running report (`--long-running` and diagnostic messages on). A project
    // that does not ask gets no monitor, so nothing is named.
    let running =
        lines
        |> List.choose (function
            | Profile.Running(name, _) -> Some name
            | _ -> None)

    let expected =
        if nameHungTests then
            [ "Xunit4RunnerFixture.Tests+Hanging.hangs when asked" ]
        else
            []

    test <@ running = expected @>

// AUTOMATION-1353: a diagnostic must not change what the host runs with. xUnit's
// `--long-running` monitor holds one thread-pool thread for the whole run, which failed every
// test that caps the pool (intelligence's starvation tests, 3/3 on an idle box). The fixture's
// `PoolProbe` fails while any pool thread is held by other work; fshw's default launch of a
// project that declares a timeout (as every intelligence project does) must leave it green.
// The host passes in well under a second; the timeout only ends a hang.
[<Fact(Timeout = 300_000)>]
let ``fshw's default launch of a real xUnit host holds none of its thread-pool threads`` () =
    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let fixtureDir = Path.Combine(repoRoot, "tests", "Fixtures", "Xunit4RunnerFixture")
    let fixtureProject = Path.Combine(fixtureDir, "Xunit4RunnerFixture.fsproj")

    let apphost =
        Path.Combine(fixtureDir, "bin", "Debug", "net10.0", "Xunit4RunnerFixture")

    let config =
        { projectOf "sh" $"-c \"shift; exec '%s{apphost}' \\\"$@\\\"\" sh \"%s{fixtureProject}\"" (Some 240) with
            Environment = [ "FSHW_FIXTURE_POOL_PROBE", "1" ]
            ReportVerificationFormat = AutoDetect }

    let status, _ = runOnce config

    test <@ status = "passed" @>

// Slice 5: the host's child processes, by command. The first sample (at 5 s) reads the
// process table, so a host that runs past it has its children seen.
[<Fact(Timeout = 120_000)>]
let ``a project's profile lists the child processes its host ran`` () =
    let status, lines = runOnce (project "-c \"sleep 7; exit 0\"" None)

    test <@ status = "passed" @>

    let commands =
        lines
        |> List.choose (function
            | Profile.Children c -> Some c.Command
            | _ -> None)

    test <@ commands |> List.contains "sleep" @>
    test <@ commands |> List.contains "sh" @>
