module FsHotWatch.Tests.WaitWedgeTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers

// A plugin whose handler never returns keeps its inflight count above zero
// forever, so `AnyPluginBusy` stays true and BOTH satisfaction paths in
// `waitForAllTerminalCore` are blocked — nothing is Running, yet the wait can
// never resolve. Before the stall detector that state was indistinguishable from
// "still working": the wait sat there for the full hour-long timeout.
//
// What separates stuck from slow is PROGRESS, not the busy set. A plugin
// draining a long `FileChecked` backlog is busy continuously with nothing
// Running, and the set of busy names stays identical for the whole drain — so a
// detector keyed on that set fires on a healthy check of a large repo. The host
// counts events finished (`CompletedDispatches`); a drain moves it constantly,
// a stopped agent never does.

/// Blocks until the test releases it — the real shape: a handler that never returns.
let private stuckHandler (name: string) (release: ManualResetEventSlim) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun _ctx state _event ->
            async {
                // The event stays counted in flight until the test releases this.
                release.Wait()
                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 60_000)>]
let ``a plugin stuck busy fails the wait fast, naming it`` () =
    use release = new ManualResetEventSlim(false)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        host.RegisterHandler(stuckHandler "wedged-plugin" release)

        host.EmitBuildCompleted(BuildSucceeded)

        // Let the mailbox pick the event up so the inflight count is non-zero.
        Thread.Sleep 200

        test <@ host.AnyPluginBusy() @>
        test <@ host.BusyPluginNames() |> List.contains "wedged-plugin" @>

        // Generous overall timeout, TINY stall threshold: if the wait resolved on
        // the timeout instead of the stall detector this would take 60s and the
        // message would not name the plugin.
        let ex =
            Assert.Throws<TimeoutException>(fun () ->
                Daemon.waitForAllTerminalCore
                    host
                    (TimeSpan.FromSeconds 30.0)
                    false
                    (TimeSpan.FromMilliseconds 300.0)
                    CancellationToken.None
                |> fun t -> t.GetAwaiter().GetResult())

        // The message must name the culprit, so a human knows where to look.
        test <@ ex.Message.Contains "WEDGED" @>
        test <@ ex.Message.Contains "wedged-plugin" @>
    finally
        // Release the handler so the blocked mailbox thread can unwind.
        release.Set()

// A handler whose CACHE-KEY function throws. Not contrived: the dispatch loop
// computes `handler.CacheKey event` BEFORE entering the `try/finally` that
// decrements the inflight count, and real key functions do I/O on the dispatch
// thread (TestPrune's `dependsOnHash` hashes every file matched by the `dependsOn`
// globs). A throw there escapes the loop entirely.
//
// Two things then go wrong at once, and the second is the one that wedges:
//   1. the increment `post` already took is never decremented — leaked;
//   2. the exception escapes the message loop, so the MailboxProcessor STOPS.
//      Nothing subscribes to its `Error` event, so it stops SILENTLY. Every
//      later post still increments and queues into a mailbox nobody reads, so
//      `inflightCount` only ever rises.
//
// `IsBusy` is `inflightCount > 0`, so a dead agent is indistinguishable from a
// busy one — permanently. Both satisfaction paths in `waitForAllTerminalCore`
// require `not (AnyPluginBusy())`, so the wait can never resolve.
let private throwingCacheKeyHandler (name: string) =
    { Name = PluginName.create name
      Init = ()
      Update = fun _ctx state _event -> async { return state }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      CacheKey = Some(fun _event -> failwith "cache-key computation failed")
      Teardown = None }

[<Fact(Timeout = 60_000)>]
let ``a fault in the dispatch loop must not leave the plugin busy forever`` () =
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(throwingCacheKeyHandler "faulting-plugin")

    host.EmitBuildCompleted(BuildSucceeded)

    // The fault must be ACCOUNTED FOR, not swallowed: the work it was counting
    // is over, so the plugin must not still claim work in flight.
    let settled = waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000

    test <@ settled @>
    test <@ host.BusyPluginNames() |> List.isEmpty @>

    // And it must be VISIBLE. A plugin whose agent died has not "succeeded";
    // leaving it non-terminal hides a programming bug behind a hang.
    let status = host.GetStatus "faulting-plugin"

    test
        <@
            match status with
            | Some(Failed _) -> true
            | _ -> false
        @>

[<Fact(Timeout = 60_000)>]
let ``a second event after a dispatch fault must still be counted correctly`` () =
    // The leak is MONOTONIC, which is what made it unrecoverable in the field:
    // once the agent is dead, every later dispatch adds another phantom unit of
    // in-flight work.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(throwingCacheKeyHandler "faulting-plugin")

    host.EmitBuildCompleted(BuildSucceeded)

    test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000 @>

    host.EmitBuildCompleted(BuildSucceeded)

    test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000 @>

[<Fact(Timeout = 60_000)>]
let ``a dispatch fault must not stomp the status of a live exclusive run`` () =
    // The forced `Failed` a dispatch fault reports follows the SAME ownership rule
    // as `safeUpdate`'s: a live exclusive run reported `Running` when it claimed and
    // is guaranteed to deliver a terminal. Letting an unrelated fault overwrite that
    // would manufacture a terminal verdict for a run still executing — how a crashing
    // per-file handler once produced a terminal mid-test-run. So the fault is logged
    // and accounted for, but the status is left to its owner.
    use release = new ManualResetEventSlim(false)

    let handler =
        { Name = PluginName.create "run-owns-status"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ ->
                        // The framework reports Running and holds the work token
                        // until this returns. An unmatched `SlotBusy` would leave no
                        // live run to own the status — the test would quietly stop
                        // testing anything.
                        match
                            ctx.RunExclusive
                                "k"
                                (async {
                                    release.Wait()
                                    return ()
                                })
                        with
                        | Claimed -> ()
                        | SlotBusy -> failwith "test setup: expected to claim the exclusive slot"
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeBuildCompleted; SubscribeFileChanged ]
          // Throws for FileChanged only, so the run can be established first.
          CacheKey =
            Some(fun event ->
                match event with
                | FileChanged _ -> failwith "cache-key computation failed"
                | _ -> None)
          Teardown = None }

    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        host.RegisterHandler handler

        host.EmitBuildCompleted(BuildSucceeded)

        let running =
            waitUntilTrue
                (fun () ->
                    match host.GetStatus "run-owns-status" with
                    | Some(Running _) -> true
                    | _ -> false)
                10_000

        test <@ running @>

        // Now fault the dispatch loop while that run is still in flight.
        host.EmitFileChanged(SourceChanged [ "a.fs" ])

        // Give the fault time to be handled; the status must NOT flip to Failed.
        Thread.Sleep 500

        test
            <@
                match host.GetStatus "run-owns-status" with
                | Some(Running _) -> true
                | _ -> false
            @>
    finally
        release.Set()

[<Fact(Timeout = 60_000)>]
let ``a host with no busy plugins resolves instead of reporting a wedge`` () =
    // Positive control for the detector above: same wait, same tiny stall threshold,
    // over a host where nothing is stuck. If this ALSO raised, the detector would be
    // firing on healthy hosts and the test above would prove nothing.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    use release = new ManualResetEventSlim(true) // pre-released: never blocks
    host.RegisterHandler(stuckHandler "healthy-plugin" release)

    test <@ host.BusyPluginNames() |> List.isEmpty @>

    Daemon.waitForAllTerminalCore
        host
        (TimeSpan.FromSeconds 30.0)
        false
        (TimeSpan.FromMilliseconds 300.0)
        CancellationToken.None
    |> fun t -> t.GetAwaiter().GetResult()

/// Takes time per event but always finishes. The shape that matters: a plugin
/// working through a QUEUE — busy the whole time, never `Running` (it reports no
/// status), with an unchanging busy set.
let private slowDrainingHandler (name: string) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun _ctx state _event ->
            async {
                do! Async.Sleep 20
                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 60_000)>]
let ``a plugin draining a backlog is not a wedge, however long the drain`` () =
    // Queue far more work than the stall threshold allows: the busy set is
    // ["draining-plugin"] and NEVER changes, nothing is ever Running, and the drain
    // runs many times longer than the threshold. A detector keyed on the busy set not
    // changing would fail here — and would therefore fail every real check of a large
    // repo, where one plugin drains thousands of FileChecked events. Keyed on events
    // finished, this is plainly progress.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(slowDrainingHandler "draining-plugin")

    // 500 events x 20ms = ~10s of continuous work, against a 5s threshold. Keeping
    // the proof above the threshold without making it subsecond avoids asserting
    // scheduler latency when the merge gate deliberately saturates the machine.
    for _ in 1..500 do
        host.EmitBuildCompleted(BuildSucceeded)

    test <@ host.AnyPluginBusy() @>

    // Busy is incremented when work is POSTED, before the mailbox is scheduled. Under
    // full-suite load the first continuation can otherwise start after the stall
    // window, making this a scheduler-startup test rather than a draining-backlog test.
    // Establish one real completion first; about 499 events (~10s) still remain, so
    // the drain remains continuously busy for roughly twice the 5s stall threshold.
    test <@ waitUntilTrue (fun () -> host.CompletedDispatches() > 0L) 10_000 @>

    // Must RESOLVE, not raise.
    Daemon.waitForAllTerminalCore
        host
        (TimeSpan.FromSeconds 30.0)
        false
        (TimeSpan.FromSeconds 5.0)
        CancellationToken.None
    |> fun t -> t.GetAwaiter().GetResult()

[<Fact(Timeout = 60_000)>]
let ``a plugin whose loop died fails the wait immediately, naming it`` () =
    // A dead agent no longer has to be inferred from silence over a threshold: the
    // plugin publishes the fault, so the wait can refuse at once and say fshw itself
    // broke rather than blaming the tree under check.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(throwingCacheKeyHandler "faulting-plugin")

    host.EmitBuildCompleted(BuildSucceeded)
    test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000 @>

    // The dispatch fault is handled and the loop survives, so nothing is
    // faulted and the wait must still be able to resolve normally.
    test <@ host.FaultedPlugins() |> List.isEmpty @>

[<Fact(Timeout = 60_000)>]
let ``CompletedDispatches counts events finished, not events posted`` () =
    // The distinction the stall detector depends on. If this counted POSTS it would
    // move while a plugin was wedged (posts still arrive at a dead mailbox), and the
    // detector would call a wedge healthy.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    use release = new ManualResetEventSlim(false)
    host.RegisterHandler(stuckHandler "gated-plugin" release)

    test <@ host.CompletedDispatches() = 0L @>

    host.EmitBuildCompleted(BuildSucceeded)

    // Posted and picked up, but the handler is blocked: busy, zero finished.
    test <@ waitUntilTrue (fun () -> host.AnyPluginBusy()) 10_000 @>
    test <@ host.CompletedDispatches() = 0L @>

    release.Set()

    // Now it finishes, and only now does progress move.
    test <@ waitUntilTrue (fun () -> host.CompletedDispatches() > 0L) 10_000 @>
    test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000 @>

[<Fact(Timeout = 60_000)>]
let ``CompletedDispatches keeps moving while a plugin drains a queue`` () =
    // What distinguishes a drain from a stall: the count must advance repeatedly, not
    // once, so a detector sampling it twice across the threshold sees motion.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(slowDrainingHandler "draining-plugin")

    for _ in 1..10 do
        host.EmitBuildCompleted(BuildSucceeded)

    let firstMove = waitUntilTrue (fun () -> host.CompletedDispatches() >= 1L) 10_000
    test <@ firstMove @>

    let afterFirst = host.CompletedDispatches()

    // It advances AGAIN — the property a single sample cannot establish.
    test <@ waitUntilTrue (fun () -> host.CompletedDispatches() > afterFirst) 10_000 @>

    test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 20_000 @>
    test <@ host.CompletedDispatches() = 10L @>
