module FsHotWatch.Tests.TestPruneStormTests

open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TestPrune.TestPrunePlugin
open FsHotWatch.Tests.TestHelpers

// A convergence-UNDER-LOAD stress test, not a fixed-window behavior test: it
// asserts only that the plugin EVENTUALLY settles on a terminal status once the
// storm subsides. The number of rerun cycles and when the last TestsFinished fires
// terminal are OS-scheduler-dependent, so under CPU load in the parallel unit-suite
// runner the settle can exceed even a generous window and fail the run. It belongs
// in the integration suite for that reason — do not move it back and widen the
// sleep instead.

[<Fact(Timeout = 30000)>]
let ``PendingRerun storm: plugin reaches terminal state after BuildCompleted hammering subsides`` () =
    withTempDir "tp-rerun-storm" (fun tmpDir ->
        // Reproduces the reported "stuck Running": if any path leaves PendingRerun
        // set without scheduling a rerun, or schedules one whose TestsFinished never
        // fires terminal, the plugin sits in Running forever.
        let configs =
            [ { Project = "FastTests"
                Command = "sh"
                Args = "-c \"sleep 0.3; exit 0\""
                Group = "default"
                Environment = []
                FilterTemplate = None
                ClassJoin = " "
                TimeoutSec = None
                ReportVerificationFormat = AutoDetect } ]

        let host = PluginHost.create (Unchecked.defaultof<_>) tmpDir
        let handler = create ":memory:" tmpDir (Some configs) None None None None []
        host.RegisterHandler(handler)

        // The first BuildCompleted transitions Idle → Running and starts the test
        // run; later ones land mid-run and (idempotently) set PendingRerun.
        for _ in 1..6 do
            host.EmitBuildCompleted(BuildSucceeded)
            // Tiny sleep so they don't all coalesce into the inbox before the
            // first one transitions to Running. Without this the first 6 land
            // before any RunExclusive starts, which trivially passes.
            Thread.Sleep(80)

        // Settle window: the test run itself is 0.3s, so 20s is generous slack.
        waitForTerminalStatus host "test-prune" 20000

        let finalStatus = host.GetStatus("test-prune")

        let isTerminal =
            match finalStatus with
            | Some(Completed _)
            | Some(Failed _) -> true
            | _ -> false

        test <@ isTerminal @>)
