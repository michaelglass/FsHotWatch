module FsHotWatch.Tests.TestPruneStormTests

open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

// Moved here from FsHotWatch.Tests/TestPrunePluginTests.fs.
//
// This is a genuine convergence-UNDER-LOAD stress test, not a fixed-window
// behavior test: it fires a burst of BuildCompleted events at a live test run
// to drive the plugin repeatedly through PendingRerun, then asserts only that
// the plugin EVENTUALLY settles on a terminal status once the storm subsides.
// "Eventually" is bounded by a generous poll-until-deadline window, but the
// exact number of rerun cycles and when the last TestsFinished fires terminal
// is OS-scheduler-dependent. Under CPU load in the parallel unit-suite runner
// the settle could exceed even a generous window, flaking the unit run with
// runner exit 2 (test failure, not coverage). Per the house rule, a test whose
// pass/fail turns on convergence-under-load timing belongs in the
// coverage-excluded integration suite — not papered over with a bigger sleep in
// the unit suite. The behavior it guards (no stuck-Running against a continuous
// BuildCompleted storm) is still exercised here on every integration run.

[<Fact(Timeout = 30000)>]
let ``PendingRerun storm: plugin reaches terminal state after BuildCompleted hammering subsides`` () =
    withTempDir "tp-rerun-storm" (fun tmpDir ->
        // Stress the PendingRerun loop: while a test run is in flight, fire
        // additional BuildCompleted events so the plugin sets PendingRerun on
        // each one. After we stop emitting builds, the plugin must eventually
        // drain its queued reruns and settle on a terminal status.
        //
        // Reproduces the user-reported "stuck Running" symptom against a
        // continuous BuildCompleted storm: if any code path leaves
        // PendingRerun set but never schedules a rerun, or schedules a rerun
        // whose TestsFinished never fires terminal, the plugin sits in
        // Running indefinitely.
        let configs =
            [ { Project = "FastTests"
                Command = "sh"
                Args = "-c \"sleep 0.3; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // Fire many BuildCompleteds in quick succession. The first transitions
        // Idle → Running and starts the test run. Subsequent ones land mid-run
        // and set PendingRerun (each new one re-sets it, idempotent). After the
        // initial run finishes the rerun branch fires; another batch of builds
        // will re-arm PendingRerun, and so on.
        for _ in 1..6 do
            host.EmitBuildCompleted(BuildSucceeded)
            // Tiny sleep so they don't all coalesce into the inbox before the
            // first one transitions to Running. Without this the first 6 land
            // before any RunExclusive starts, which trivially passes.
            Thread.Sleep(80)

        // Allow the rerun loop to keep cycling, then stop emitting. Within a
        // reasonable settle window (test run = 0.3s, leaving generous slack),
        // the plugin must reach a terminal status.
        waitForTerminalStatus host "test-prune" 20000

        let finalStatus = host.GetStatus("test-prune")

        let isTerminal =
            match finalStatus with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false

        test <@ isTerminal @>)
