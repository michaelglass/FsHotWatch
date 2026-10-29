/// The verdict of a run with a timeout red points at that project's insights: its long
/// pole and share of the wall, its headroom, and whether the run was contended — the
/// same record `fshw insights --json` prints for the same run directory
/// (AUTOMATION-1300, slice 4).
module FsHotWatch.Tests.VerdictTimeoutInsightsTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// The run directory a test run leaves when one host prints a long class's results, hangs,
/// and is killed at its 2 s timeout, beside a project that passes: each project's profile,
/// in the lines the runner writes (`Profile.render`). Returns the root and the run id.
///
/// Written, not produced by a real run (AUTOMATION-1349). A long pole is a class that
/// spans at least `Insights.LongPoleShare` of the wall, and a killed run's wall is its
/// timeout plus a teardown that load stretches: in a real 2 s run the 1.9 s class stopped
/// being the long pole once the wall passed 3.8 s, which a loaded CI runner reached. Here
/// the wall is a fact of the fixture, so the claim below no longer depends on how fast the
/// box tore a host down. That a killed host's printed results become these class and
/// running lines is `TestPruneProfileTests`' claim, made over real hosts.
let private timeoutRun () : string * Guid =
    let root = isolatedRoot ()
    let runId = Guid.NewGuid()
    let runDir = Ctrf.runDir root runId
    Directory.CreateDirectory runDir |> ignore

    let t0 = DateTime(2026, 10, 28, 6, 0, 0, DateTimeKind.Utc)
    let at (ms: int) = t0.AddMilliseconds(float ms)

    let quiet: Profile.Contention =
        { Samples = 1
          LoadPerCoreMean = Some 0.25
          LoadPerCoreMax = Some 0.25
          Contended = Some false }

    let consoleClass cls startMs stopMs : Profile.Line =
        Profile.Class(
            { Class = cls
              Tests = 1
              DurationMs = int64 (stopMs - startMs)
              Start = at startMs
              Stop = at stopMs },
            quiet,
            Profile.ClassSource.Console
        )

    let write project (lines: Profile.Line list) =
        File.WriteAllLines(Profile.pathIn runDir project, lines |> List.map Profile.render)

    let killedAfter = TimeSpan.FromSeconds 2.0

    // The host printed `passed Hung.Pole.a (1900ms)`, `passed Hung.Quick.b (5ms)` and xUnit's
    // long-running report for `Hung.stuck forever`, then hung until the 2 s timeout killed it.
    write
        "Hung.Tests"
        [ Profile.Start("Hung.Tests", at 0, 4, Some 2, Some 1.0)
          Profile.Spawn(at 50, 4242)
          Profile.End(
              at 2_400,
              2_400L,
              Some 12.0,
              Some 1.0,
              Profile.outcomeOf (Events.TestsTimedOut("", killedAfter, false, killedAfter))
          )
          consoleClass "Hung.Pole" 100 2_000
          consoleClass "Hung.Quick" 1_995 2_000
          Profile.Running("Hung.stuck forever", at 1_000) ]

    write
        "Fine.Tests"
        [ Profile.Start("Fine.Tests", at 0, 4, None, Some 1.0)
          Profile.Spawn(at 50, 4243)
          Profile.End(
              at 100,
              100L,
              Some 3.0,
              Some 1.0,
              Profile.outcomeOf (Events.TestsPassed("", false, TimeSpan.FromMilliseconds 100.0))
          )
          Profile.ClassesUnavailable "no report: this runner was not asked for CTRF" ]

    root, runId

let private redVerdict (root: string) (runId: Guid) : Verdict.Verdict =
    let runs: Verdict.RunSuites list =
        [ { RunId = Some runId
            Suites = Verdict.suiteVerdicts root (Some runId) } ]

    Verdict.create
        Verdict.Check
        { Scope = IpcParsing.TestScope.FullSuite(2, None)
          RunId = Some runId
          SessionRuns = []
          CheckRuns = [ runId ]
          Seeds = []
          SeedCount = 0
          Baseline = BaselineFixtures.reading
          NotSelected = Some []
          SelectedBy = None
          SeedFanout = None
          GradedEvidence = IpcParsing.GradedEvidenceReading.NotReported
          OutcomeKeys = None }
        { Hash = "sha256:t"
          FileCount = 1
          SkippedCount = 0
          DeclaredCount = 0
          AbsentDeclarationCount = 0 }
        (Some [])
        Verdict.Red
        1
        [ { Name = "test-prune"
            Outcome = Verdict.PluginOutcome.Fail
            ElapsedMs = Some 2_500L
            Summary = Some "1 passed, 1 failed in 2 projects" } ]
        runs
        Verdict.CheckComparison.notRecorded
        []
        ProjectModelFixtures.available
    |> Verdict.withTimedOut (Verdict.timedOutInsights root runs)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a timeout red's verdict carries the long pole and its share of the wall, as insights reports them`` () =
    let root, runId = timeoutRun ()
    let v = redVerdict root runId

    // Only the project that timed out; the passing one is not a timeout.
    let entry = v.TimedOut |> List.exactlyOne
    let id = runId.ToString "N"
    test <@ entry.Project = "Hung.Tests" && entry.Outcome = Some "timed-out" @>
    test <@ entry.RunId = id @>
    let pole = entry.LongPole |> Option.map (fun p -> p.Class)
    test <@ pole = Some "Hung.Pole" @>
    // The class running at the kill, from xUnit's long-running report the host printed.
    let running = entry.RunningAtKill |> List.map (fun r -> r.Class, r.Test)
    test <@ running = [ "Hung", "Hung.stuck forever" ] @>
    let over = entry.HeadroomMs |> Option.exists (fun h -> h <= 0.0)
    test <@ entry.TimeoutSec = Some 2 && over @>

    // The same record `fshw insights --json --run <id>` prints for that run directory.
    let insights =
        JsonNode.Parse(
            Insights.reportJson
                { Insights.Options.defaults with
                    Run = Some(runId.ToString "N") }
                (Insights.loadRuns (Ctrf.reportsDir root))
        )

    let printed =
        insights["projects"].AsArray()
        |> Seq.choose Insights.tryReadInsight
        |> Seq.find (fun i -> i.Project = "Hung.Tests")

    test <@ printed = entry @>

    // On the wire: the long pole, its share, and where to look.
    let wire = JsonNode.Parse(Verdict.serialize v)
    let timedOut = wire["timedOut"].AsArray()
    test <@ timedOut.Count = 1 @>
    test <@ (timedOut[0].Item("longPole").Item("class").GetValue<string>()) = "Hung.Pole" @>

    test
        <@
            (timedOut[0].Item("longPole").Item("share").GetValue<float>()) = (entry.LongPole
                                                                              |> Option.bind (fun p -> p.Share)
                                                                              |> Option.get)
        @>

    let see = $"fshw insights --run %s{id}"
    test <@ (timedOut[0].Item("see").GetValue<string>()) = see @>

    // And it reads back.
    Verdict.write root v

    match Verdict.read root with
    | Verdict.Reading.Found back -> test <@ back.TimedOut = v.TimedOut @>
    | other -> failwith $"the verdict did not read back: %A{other}"

[<Fact(Timeout = 30_000)>]
let ``a verdict with no timeout says so with an empty list, and an old verdict without the field reads as none`` () =
    let root = isolatedRoot ()
    let runId = Guid.NewGuid()
    Directory.CreateDirectory(Ctrf.runDir root runId) |> ignore
    let v = redVerdict root runId

    test <@ List.isEmpty v.TimedOut @>
    let wire = JsonNode.Parse(Verdict.serialize v)
    test <@ wire["timedOut"].AsArray().Count = 0 @>

    let withoutField =
        let o = JsonNode.Parse(Verdict.serialize v).AsObject()
        o.Remove "timedOut" |> ignore
        o.ToJsonString()

    File.WriteAllText(Verdict.path root, withoutField)

    match Verdict.read root with
    | Verdict.Reading.Found back -> test <@ List.isEmpty back.TimedOut @>
    | other -> failwith $"an old verdict did not read: %A{other}"
