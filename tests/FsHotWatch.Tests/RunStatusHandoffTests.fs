module FsHotWatch.Tests.RunStatusHandoffTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

// An exclusive run reports `Running` when it claims its key and owes the terminal its
// result fold reports. Between the worker finishing and that fold committing, the key is
// still held by the run: the fold waits in the plugin's mailbox behind whatever was queued
// before it. That hand-off normally takes milliseconds, but behind one long fold it can
// take minutes.
//
// A terminal reported by an UNRELATED fold in that window (a per-file analysis saying
// "Completed") must not replace the run's `Running`: both the verdict wait and the wedge
// monitor read `Running` to know the plugin is still answerable, and a plugin that owns
// work while reporting a terminal reads as a stuck inflight count.

type private HandoffMsg = | RunDone

/// The plugin the hand-off window is driven through. One event per step, distinguished by
/// the changed file's name:
///
///  * BuildCompleted — claim the run; the worker returns once `workerGo` is set.
///  * "hold"   — wait until the worker has retired into its result fold, so every event
///               queued after this one is processed while that fold is still owed.
///  * "report" — an unrelated per-file terminal, the shape of a symbol-analysis fold.
///  * "slow"   — one declared bounded unit of work, then an undeclared tail, each gated
///               by the test. The tail is the rest of a fold after its declaration ends.
let private handoffHandler
    (workerGo: ManualResetEventSlim)
    (slowEntered: ManualResetEventSlim)
    (slowRelease: ManualResetEventSlim)
    (tailEntered: ManualResetEventSlim)
    (tailRelease: ManualResetEventSlim)
    =
    { Name = PluginName.create "handoff"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | BuildCompleted _ ->
                    match
                        ctx.RunExclusive
                            "tests"
                            (async {
                                workerGo.Wait()
                                return RunDone
                            })
                    with
                    | Claimed -> ()
                    | SlotBusy -> failwith "test setup: expected to claim the tests key"
                | FileChanged(SourceChanged [ "hold" ]) ->
                    while ctx.SlotHolder "tests" = SlotHolder.LiveRun do
                        Thread.Sleep 5
                | FileChanged(SourceChanged [ "report" ]) ->
                    ctx.ReportStatus(PluginStatus.completedNow "symbol analysis: no run due" TimeSpan.Zero)
                | FileChanged(SourceChanged [ "slow" ]) ->
                    do
                        use _bound = ctx.DeclareBoundedWork "impact selection" (TimeSpan.FromHours 1.0)
                        slowEntered.Set()
                        slowRelease.Wait()

                    tailEntered.Set()
                    tailRelease.Wait()
                | Custom RunDone -> ctx.ReportStatus(PluginStatus.completedNow "run done" TimeSpan.Zero)
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted; SubscribeFileChanged ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

let private isRunning (host: PluginHost) =
    match host.GetStatus "handoff" with
    | Some(Running _) -> true
    | _ -> false

/// Claim the run, queue hold → report → slow, and only then let the worker finish. Its
/// result fold is posted when it finishes, so it queues behind all three, and "report" and
/// "slow" run while it is owed.
let private openHandoffWindow (host: PluginHost) (workerGo: ManualResetEventSlim) =
    host.EmitBuildCompleted(BuildSucceeded)
    host.EmitFileChanged(SourceChanged [ "hold" ])
    host.EmitFileChanged(SourceChanged [ "report" ])
    host.EmitFileChanged(SourceChanged [ "slow" ])
    workerGo.Set()

[<Fact(Timeout = 60_000)>]
let ``a terminal reported while a finished run's result fold is owed leaves the run Running`` () =
    use workerGo = new ManualResetEventSlim(false)
    use slowEntered = new ManualResetEventSlim(false)
    use slowRelease = new ManualResetEventSlim(false)
    use tailEntered = new ManualResetEventSlim(false)
    use tailRelease = new ManualResetEventSlim(false)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        host.RegisterHandler(handoffHandler workerGo slowEntered slowRelease tailEntered tailRelease)
        openHandoffWindow host workerGo

        // BuildCompleted, hold and report have committed; "slow" holds the mailbox, so the
        // run's result fold has not been processed.
        Assert.True(slowEntered.Wait(TimeSpan.FromSeconds 10.0), "the slow fold must start")
        test <@ host.CompletedDispatches() = 3L @>
        test <@ host.AnyPluginBusy() @>

        test <@ isRunning host @>

        slowRelease.Set()
        tailRelease.Set()

        // The fold's own terminal still lands: the run's verdict is not swallowed.
        test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000 @>

        test
            <@
                match host.GetStatus "handoff" with
                | Some(Completed(_, verdict)) -> verdict.Summary = "run done"
                | _ -> false
            @>
    finally
        workerGo.Set()
        slowRelease.Set()
        tailRelease.Set()

[<Fact(Timeout = 60_000)>]
let ``the verdict wait does not read an owed run result as a wedge once a declared fold ends`` () =
    // The production shape: a finished full-suite run's result fold is queued behind a
    // long, declared impact selection. The declaration keeps the stall detector quiet while
    // it lasts; the instant it ends, the fold is still finishing and nothing has committed
    // for longer than the stall threshold. The run still owns the plugin's status, so the
    // plugin is Running and this is not a wedge.
    use workerGo = new ManualResetEventSlim(false)
    use slowEntered = new ManualResetEventSlim(false)
    use slowRelease = new ManualResetEventSlim(false)
    use tailEntered = new ManualResetEventSlim(false)
    use tailRelease = new ManualResetEventSlim(false)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        host.RegisterHandler(handoffHandler workerGo slowEntered slowRelease tailEntered tailRelease)
        openHandoffWindow host workerGo

        Assert.True(slowEntered.Wait(TimeSpan.FromSeconds 10.0), "the slow fold must start")

        let wait =
            Daemon.waitForAllTerminalCore
                host
                (TimeSpan.FromSeconds 30.0)
                (TimeSpan.FromMilliseconds 300.0)
                CancellationToken.None

        // Longer than the stall threshold with no event committing, inside the declaration.
        // FSHW-WAIT-001 ok: the window must outlast the stall threshold; nothing is awaited.
        Thread.Sleep 800
        slowRelease.Set()
        Assert.True(tailEntered.Wait(TimeSpan.FromSeconds 10.0), "the tail must start")

        // The declaration has ended and the fold is still finishing. A wedge verdict here
        // would fault the wait within a few ticks.
        // FSHW-WAIT-001 ok: asserting a NEGATIVE (no wedge verdict) inside this window.
        Thread.Sleep 800
        test <@ not wait.IsFaulted @>

        tailRelease.Set()
        wait.GetAwaiter().GetResult()
    finally
        workerGo.Set()
        slowRelease.Set()
        tailRelease.Set()
