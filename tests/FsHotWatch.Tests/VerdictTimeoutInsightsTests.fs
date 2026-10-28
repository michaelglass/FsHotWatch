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
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// A real run of one `sh` host that prints a long class's results, then hangs and is
/// killed at its 2 s timeout, beside a project that passes. Returns the root and the run id.
let private timeoutRun () : string * Guid =
    let root = isolatedRoot ()

    let project name args timeout : TestConfig =
        { Project = name
          Command = "sh"
          Args = args
          Group = name
          Environment = []
          FilterTemplate = None
          ClassJoin = " "
          TimeoutSec = timeout
          ReportVerificationFormat = Disabled
          Starvation = None
          CheckSkip = None
          AlwaysClasses = [] }

    let configs =
        [ project
              "Hung.Tests"
              "-c \"echo 'passed Hung.Pole.a (1900ms)'; echo 'passed Hung.Quick.b (5ms)'; echo \\\"[Long Running Test] 'Hung.stuck forever', Elapsed: 00:00:01\\\"; exec sleep 60\""
              (Some 2)
          project "Fine.Tests" "-c \"exit 0\"" None ]

    let host = PluginHost.create (Unchecked.defaultof<_>) root
    let handler = create ":memory:" root (Some configs) None None None None []
    host.RegisterHandler(handler)
    let result = host.RunCommand("run-tests", [| "{}" |]) |> Async.RunSynchronously
    test <@ result.IsSome @>

    let runDir = Directory.GetDirectories(Ctrf.reportsDir root) |> Array.exactlyOne
    root, Guid.ParseExact(Path.GetFileName runDir, "N")

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
          GradedEvidence = IpcParsing.GradedEvidenceReading.NotReported }
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

[<Fact(Timeout = 120_000)>]
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
