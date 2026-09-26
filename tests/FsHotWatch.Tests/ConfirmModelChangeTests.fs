/// `confirm` on a daemon whose project model moves under it: a real host, the real
/// TestPrune plugin running a real (gated) test host, the daemon's verdict wait, and the
/// CLI's receipt gate. Every ordering is a signal — a marker the test host writes when it
/// starts, a release file it waits on — never a sleep.
module FsHotWatch.Tests.ConfirmModelChangeTests

open System
open System.IO
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Tests.TestHelpers

/// A daemon over `<root>/repo` whose one test project waits for `<root>/release`. The
/// release file, the start marker and the impact database all sit OUTSIDE the repository,
/// so ending a run never reads as the tree moving under it.
type private GatedDaemon =
    { Host: FsHotWatch.PluginHost.PluginHost
      RepoRoot: string
      Release: string
      Started: string }

let private gatedDaemon (root: string) =
    let repoRoot = Path.Combine(root, "repo")
    Directory.CreateDirectory(Path.Combine(repoRoot, "src")) |> ignore
    File.WriteAllText(Path.Combine(repoRoot, "src", "Value.fs"), "module Value\nlet answer = 1\n")
    let release = Path.Combine(root, "release")
    let started = Path.Combine(root, "started")

    let host = FsHotWatch.PluginHost.PluginHost.create sharedChecker.Value repoRoot
    host.WorkStore.PublishProjectModelWithFiles(fixtureModelOf 1L, Set.empty)

    host.RegisterHandler(
        FsHotWatch.TestPrune.TestPrunePlugin.create
            (Path.Combine(root, "impact.db"))
            repoRoot
            (Some
                [ { FsHotWatch.TestPrune.TestPrunePlugin.TestConfig.Project = "GatedTests"
                    Command = "sh"
                    Args = $"-c \"touch '%s{started}'; " + gatedWait root release 1500 + "\""
                    Group = "default"
                    Environment = []
                    FilterTemplate = None
                    ClassJoin = " "
                    TimeoutSec = None
                    ReportVerificationFormat = FsHotWatch.TestPrune.TestPrunePlugin.AutoDetect } ])
            None
            None
            None
            None
            []
    )

    { Host = host
      RepoRoot = repoRoot
      Release = release
      Started = started }

/// What `confirm`'s escalation (and a rebuild's launch) does: run every project, and
/// return once the host has committed the result.
let private runSuite (daemon: GatedDaemon) =
    File.WriteAllText(daemon.Release, "")

    daemon.Host.RunCommand("run-tests", [| "{}" |])
    |> Async.RunSynchronously
    |> ignore

    waitForQuiescent daemon.Host 20000

/// The reading `confirm` grades: `test-scope`, parsed as the CLI parses it.
let private testScope (daemon: GatedDaemon) =
    daemon.Host.RunCommand("test-scope", [||])
    |> Async.RunSynchronously
    |> Option.get
    |> IpcParsing.parseTestRunReport

type private ConfirmReading =
    /// The reading is not a full suite: `confirm` runs one before it grades anything.
    | Escalates
    /// The reading was graded; the exit code the verdict file records, and its text.
    | Graded of exitCode: int * verdict: string

/// Grade the daemon's current reading the way `confirm` does after it settles: escalate a
/// scope that is not a full suite, otherwise publish through the receipt gate. Published
/// into a scratch directory, so the verdict file never lands in the repository.
let private gradeConfirm (daemon: GatedDaemon) (scratch: string) =
    let report = testScope daemon

    if CheckVerdict.confirmNeedsFullRun CheckVerdict.Confirmation report.Scope then
        Escalates
    else
        Directory.CreateDirectory scratch |> ignore

        let exitCode =
            publishVerdictForInvocation
                (Verdict.Invocation.start ())
                scratch
                []
                CheckVerdict.Confirmation
                false
                report
                Verdict.NoReading
                Map.empty
                (IpcParsing.DaemonEvidence.ofHost daemon.Host)
                []
                (IpcParsing.ProjectModelReading.Observed daemon.Host.WorkSnapshot.ProjectModel)
                (SettledTree.capture scratch [])
                (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

        Graded(exitCode, File.ReadAllText(Path.Combine(scratch, ".fshw", "verdict.json")))

/// The daemon's settle: resolves only once the host holds evidence for the current model.
let private settles (daemon: GatedDaemon) (seconds: float) =
    try
        FsHotWatch.Daemon.waitForVerdict daemon.Host (TimeSpan.FromSeconds seconds) CancellationToken.None
        |> fun waiting -> waiting.GetAwaiter().GetResult()

        true
    with :? TimeoutException ->
        false

/// A confirm that ends is green, or it runs the suite again. It is never graded red for
/// want of a receipt for a run no evidence names.
let private assertGreen (reading: ConfirmReading) =
    match reading with
    | Graded(exitCode, verdict) ->
        test <@ not (verdict.Contains "nothing vouches") @>
        test <@ exitCode = 0 @>
    | Escalates -> failwith "a full suite under the current model must grade, not escalate"

[<Fact(Timeout = 60000)>]
let ``a model change that lands while confirm's run is in flight is re-run under the new model, never graded stale``
    ()
    =
    withTempDir "confirm-model-mid-run" (fun root ->
        let daemon = gatedDaemon root

        // A daemon that has earned evidence: a full suite under generation 1, graded green.
        runSuite daemon
        assertGreen (gradeConfirm daemon (Path.Combine(root, "verdict-0")))

        // The confirm's run, held in flight.
        File.Delete daemon.Release
        File.Delete daemon.Started

        daemon.Host.RunCommand("set-scope", [| "{\"scope\":\"full\"}" |])
        |> Async.RunSynchronously
        |> ignore

        let inFlight = Async.StartAsTask(daemon.Host.RunCommand("run-tests", [| "{}" |]))

        waitUntil (fun () -> File.Exists daemon.Started) 20000

        // A late MSBuild re-evaluation replaces the model while the suite runs.
        daemon.Host.WorkStore.PublishProjectModelWithFiles(fixtureModelOf 2L, Set.empty)
        File.WriteAllText(daemon.Release, "")
        inFlight.GetAwaiter().GetResult() |> ignore
        waitForQuiescent daemon.Host 20000

        // The run verified generation 1: nothing vouches for 2, so the daemon does not
        // settle, and the reading grades no run — confirm escalates instead of ending red.
        test <@ not (settles daemon 1.0) @>
        test <@ gradeConfirm daemon (Path.Combine(root, "verdict-1")) = Escalates @>

        // The escalation runs the suite under generation 2, and that run is graded green.
        runSuite daemon
        test <@ settles daemon 5.0 @>
        assertGreen (gradeConfirm daemon (Path.Combine(root, "verdict-2"))))

[<Fact(Timeout = 60000)>]
let ``a long-lived daemon re-evaluated after its last confirm re-earns its evidence under the new model`` () =
    withTempDir "confirm-model-long-lived" (fun root ->
        let daemon = gatedDaemon root

        // The earlier confirm, green on generation 1.
        runSuite daemon
        assertGreen (gradeConfirm daemon (Path.Combine(root, "verdict-0")))

        // The workspace moved on: a large change batch, a forced scan and two
        // re-evaluations, ending at generation 3 — with no run since.
        daemon.Host.WorkStore.PublishProjectModelWithFiles(fixtureModelOf 2L, Set.empty)
        daemon.Host.WorkStore.PublishProjectModelWithFiles(fixtureModelOf 3L, Set.empty)

        // The generation-1 receipt still describes the tree, and nothing about the model
        // graded now: the reading is not a full suite, so confirm runs one.
        test <@ not (settles daemon 1.0) @>
        test <@ (testScope daemon).RunId = None @>
        test <@ gradeConfirm daemon (Path.Combine(root, "verdict-1")) = Escalates @>

        runSuite daemon
        test <@ settles daemon 5.0 @>
        assertGreen (gradeConfirm daemon (Path.Combine(root, "verdict-2"))))
