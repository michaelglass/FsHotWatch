/// A red run is the answer for the inputs it ran (AUTOMATION-1373).
///
/// Under CPU starvation a gate could not converge: while a run was in flight, the symbols
/// it had been launched to verify were still outstanding, so each cohort and build queued
/// another run. The run came back red (starved projects time out), its symbols stayed
/// owed, and the queued run launched the same selection over the same tree again. At about
/// 80 minutes per run under load, the gate's 140-minute verdict deadline expired before
/// the daemon stopped retrying (intelligence land entry 234).
///
/// These pin the decision, not a duration: whether a run launches is counted from the
/// test host's own marker file. Nothing here reads or asserts on a clock; the waits are
/// hang caps (`HangWaitMs`) on event commits, and a gate file orders the mid-run cohort.
module FsHotWatch.Tests.TestPruneRedRetryTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.TestPrune.TestPrunePlugin
open TestPrune.Database
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestPrunePluginTestSupport

/// One project, P1, covering `Lib.foo`. Every run appends a line to `runs` and marks
/// `started`, waits for `release`, then exits with `exitCode`: 1 is a red run.
let private runner (tmpDir: string) (release: string) (exitCode: int) : TestConfig =
    let runs = Path.Combine(tmpDir, "runs")
    let started = Path.Combine(tmpDir, "started")

    { Project = "P1"
      Command = "sh"
      // 3000 polls at 20 ms is about 60 s, past every hang cap below. See `gatedWait`.
      Args =
        "-c \"printf 'run\\n' >> '"
        + runs
        + "'; touch '"
        + started
        + "'; "
        + gatedWait tmpDir release 3000
        + $"; exit %d{exitCode}\""
      Group = "default"
      Environment = []
      FilterTemplate = None
      ClassJoin = " "
      TimeoutSec = None
      ReportVerificationFormat = AutoDetect
      Starvation = None
      CheckSkip = None
      NameHungTests = false
      Weight = 1.0
      AlwaysClasses = [] }

let private runCount (tmpDir: string) =
    let runs = Path.Combine(tmpDir, "runs")

    if File.Exists runs then
        File.ReadAllLines(runs).Length
    else
        0

/// A host whose queue owes `Lib.foo`, covered only by P1, with a source file under a
/// discovery root so the receipt input tree has an entry an edit can move.
let private withRedScenario name (body: string -> string -> FsHotWatch.PluginHost.PluginHost -> unit) =
    withTempDir name (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "src/Lib.fs" "P1" "P1Tests" "fooTest"
        PendingVerification.save tmpDir (Set.singleton "Lib.foo")
        let sourceDir = Directory.CreateDirectory(Path.Combine(tmpDir, "src")).FullName
        File.WriteAllText(Path.Combine(sourceDir, "Lib.fs"), "module Lib\nlet foo x = x + 1\n")

        withReleaseGate tmpDir name (fun release ->
            let host = createModelHost (Unchecked.defaultof<_>) tmpDir

            host.RegisterHandler(create dbPath tmpDir (Some [ runner tmpDir release 1 ]) None None None None [])

            body tmpDir release host))

/// Launch the red run, and let it conclude.
let private concludeRedRun (tmpDir: string) (release: string) (host: FsHotWatch.PluginHost.PluginHost) =
    File.WriteAllText(release, "")
    host.EmitBuildCompleted(BuildSucceeded)
    waitUntil (fun () -> File.Exists(Path.Combine(tmpDir, "started"))) HangWaitMs
    waitForQuiescent host HangWaitMs

let private expectRed (host: FsHotWatch.PluginHost.PluginHost) =
    match host.GetStatus("test-prune") with
    | Some(Failed _) -> ()
    | other -> Assert.Fail($"expected the red run's result to stand, got %A{other}")

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a re-run queued during a red run does not retry it over the same tree`` () =
    // The land-234 shape. The run holds the "tests" key; a cohort folds while it does,
    // finds `Lib.foo` still owed, and queues a re-run. The run launched against `Lib.foo`
    // and concludes red, so the queued re-run would launch the same selection over the
    // same tree: a retry, not new evidence. Under load it costs as long as the run did.
    withRedScenario "tp-red-queued" (fun tmpDir release host ->
        host.EmitBuildCompleted(BuildSucceeded)
        waitUntil (fun () -> File.Exists(Path.Combine(tmpDir, "started"))) HangWaitMs

        // The run is held at its gate, so this cohort folds while it is in flight. Its
        // own commit is the witness that the re-run was queued before the run concluded.
        let committedBefore = committedBy host "test-prune"

        host.EmitBatchChecked(
            { fakeBatchChecked [ "src/Lib.fs" ] with
                Trigger = InSessionBatch [ SourceChanged [ "src/Lib.fs" ] ] }
        )

        test <@ waitForCommitted host "test-prune" committedBefore 1L HangWaitMs @>

        File.WriteAllText(release, "")
        waitForQuiescent host HangWaitMs

        test <@ runCount tmpDir = 1 @>
        test <@ (PendingQueueHelpers.loadQueue tmpDir).Contains "Lib.foo" @>
        expectRed host)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a cohort that folds after a red run over the same tree does not retry it`` () =
    // The forced scan of the next gate invocation folds its cohorts with the slot free.
    // Nothing is owed that the red run did not launch against, and the tree has not
    // moved, so the drain would only run the same selection again.
    withRedScenario "tp-red-cohort" (fun tmpDir release host ->
        concludeRedRun tmpDir release host
        test <@ runCount tmpDir = 1 @>

        let committedBefore = committedBy host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "src/Lib.fs" ])
        test <@ waitForCommitted host "test-prune" committedBefore 1L HangWaitMs @>
        waitForQuiescent host HangWaitMs

        test <@ runCount tmpDir = 1 @>
        test <@ (PendingQueueHelpers.loadQueue tmpDir).Contains "Lib.foo" @>
        expectRed host)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an edit after a red run still launches the run that verifies it`` () =
    // The control: the skip is for a retry only. Once the tree moves, the red speaks for
    // bytes that are gone, and the owed symbol must be verified again.
    withRedScenario "tp-red-edited" (fun tmpDir release host ->
        concludeRedRun tmpDir release host
        test <@ runCount tmpDir = 1 @>

        File.WriteAllText(Path.Combine(tmpDir, "src", "Lib.fs"), "module Lib\nlet foo x = x + 2\n")
        let committedBefore = committedBy host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "src/Lib.fs" ])
        test <@ waitForCommitted host "test-prune" committedBefore 1L HangWaitMs @>
        waitForQuiescent host HangWaitMs

        test <@ runCount tmpDir = 2 @>)

/// `project` appends to `runs-<project>` and exits `exitCode`, ungated.
let private quickRunner (tmpDir: string) (project: string) (exitCode: int) : TestConfig =
    let runs = Path.Combine(tmpDir, $"runs-%s{project}")

    { runner tmpDir (Path.Combine(tmpDir, "unused")) exitCode with
        Project = project
        Args = $"-c \"printf 'run\\n' >> '%s{runs}'; exit %d{exitCode}\"" }

let private runsOf (tmpDir: string) (project: string) =
    let runs = Path.Combine(tmpDir, $"runs-%s{project}")

    if File.Exists runs then
        File.ReadAllLines(runs).Length
    else
        0

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a red full-suite run is not followed by a second full pass that re-runs its green projects`` () =
    // AUTOMATION-1392: a check that went red re-ran projects that were already green, in
    // a second full pass. A cold daemon (no full-suite baseline) widens its first run to
    // every project: P1 (owing `Lib.foo`) goes red, P2 passes. The cohort the next gate
    // invocation folds over the same tree must not launch that full pass again.
    withTempDir "tp-red-full" (fun tmpDir ->
        let dbPath = Path.Combine(tmpDir, "tp.db")
        let db = Database.create dbPath
        PendingQueueHelpers.seedCoveredSymbol db "Lib.foo" "src/Lib.fs" "P1" "P1Tests" "fooTest"
        PendingVerification.save tmpDir (Set.singleton "Lib.foo")
        let sourceDir = Directory.CreateDirectory(Path.Combine(tmpDir, "src")).FullName
        File.WriteAllText(Path.Combine(sourceDir, "Lib.fs"), "module Lib\nlet foo x = x + 1\n")
        let host = createModelHost (Unchecked.defaultof<_>) tmpDir

        host.RegisterHandler(
            create dbPath tmpDir (Some [ quickRunner tmpDir "P1" 1; quickRunner tmpDir "P2" 0 ]) None None None None []
        )

        host.EmitBuildCompleted(BuildSucceeded)
        waitUntil (fun () -> runsOf tmpDir "P1" >= 1 && runsOf tmpDir "P2" >= 1) HangWaitMs
        waitForQuiescent host HangWaitMs
        let p2First = runsOf tmpDir "P2"

        let committedBefore = committedBy host "test-prune"
        host.EmitBatchChecked(fakeBatchChecked [ "src/Lib.fs" ])
        test <@ waitForCommitted host "test-prune" committedBefore 1L HangWaitMs @>
        waitForQuiescent host HangWaitMs

        test <@ runsOf tmpDir "P1" = 1 @>
        test <@ runsOf tmpDir "P2" = p2First @>
        expectRed host)
