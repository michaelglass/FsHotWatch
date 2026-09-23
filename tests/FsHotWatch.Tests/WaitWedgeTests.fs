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
      PrepareCommit = None
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
      PrepareCommit = None
      CacheKey = Some(fun _ _ -> failwith "cache-key computation failed")
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
          PrepareCommit = None
          // Throws for FileChanged only, so the run can be established first.
          CacheKey =
            Some(fun _ event ->
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
        (TimeSpan.FromMilliseconds 300.0)
        CancellationToken.None
    |> fun t -> t.GetAwaiter().GetResult()

/// Finishes one event per permit the test releases, so the drain runs at the test's
/// pace rather than the scheduler's.
let private gatedDrainingHandler (name: string) (permits: SemaphoreSlim) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun _ctx state _event ->
            async {
                do! permits.WaitAsync() |> Async.AwaitTask
                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 60_000)>]
let ``a draining backlog holds the wait until its last event finishes`` () =
    // The claim is an ORDER: the backlog drains, THEN the wait resolves. The busy set is
    // ["draining-plugin"] throughout and nothing is ever Running — the shape a real check
    // of a large repo takes while one plugin drains thousands of FileChecked events.
    // Whether a drain LONGER than the stall threshold reads as a wedge is `StallWatch`'s
    // question, answered below without a clock.
    use permits = new SemaphoreSlim(0)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(gatedDrainingHandler "draining-plugin" permits)

    let backlog = 500

    for _ in 1..backlog do
        host.EmitBuildCompleted(BuildSucceeded)

    // No deadline, and the production stall threshold: only the drain can end this wait.
    let waiting =
        Daemon.waitForAllTerminalCore
            host
            TimeSpan.MaxValue
            Daemon.waitForAllTerminalBusyStallThreshold
            CancellationToken.None

    permits.Release(backlog - 1) |> ignore
    test <@ waitUntilTrue (fun () -> host.CompletedDispatches() = int64 (backlog - 1)) 30_000 @>

    // One event is still owned, so the wait cannot have resolved.
    test <@ host.AnyPluginBusy() @>
    test <@ not waiting.IsCompleted @>

    permits.Release() |> ignore
    waiting.GetAwaiter().GetResult()

    test <@ host.CompletedDispatches() = int64 backlog @>

[<Fact>]
let ``a drain however long is not a stall while each event finishes inside the threshold`` () =
    let threshold = TimeSpan.FromSeconds 5.0
    let step = TimeSpan.FromSeconds 4.0
    let start = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let events = 500L

    // Sampled as the detector samples: the last reading before each event finishes still
    // shows the previous count, one step after it last moved.
    let drained =
        [ 1L .. events ]
        |> List.fold
            (fun (watch: Daemon.StallWatch) finished ->
                let before = watch.Since + step
                let unmoved = Daemon.StallWatch.observe (finished - 1L) before watch
                test <@ not (Daemon.StallWatch.stalled threshold before unmoved) @>
                Daemon.StallWatch.observe finished before unmoved)
            (Daemon.StallWatch.start start |> Daemon.StallWatch.observe 0L start)

    // The drain spanned 400 thresholds and never once read as stalled.
    let span = TimeSpan.FromTicks(step.Ticks * events)
    test <@ drained.Since - start = span @>
    test <@ drained.Finished = events @>

[<Fact>]
let ``a count that has not moved for the threshold is a stall`` () =
    // The control for the drain above: the same watch, with the count held still.
    let threshold = TimeSpan.FromSeconds 5.0
    let start = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    let watch = Daemon.StallWatch.start start |> Daemon.StallWatch.observe 7L start
    let justBefore = start + threshold - TimeSpan.FromTicks 1L
    let at = start + threshold

    test <@ not (Daemon.StallWatch.stalled threshold justBefore (Daemon.StallWatch.observe 7L justBefore watch)) @>
    test <@ Daemon.StallWatch.stalled threshold at (Daemon.StallWatch.observe 7L at watch) @>

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
    use permits = new SemaphoreSlim(0)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(gatedDrainingHandler "draining-plugin" permits)

    for _ in 1..10 do
        host.EmitBuildCompleted(BuildSucceeded)

    // Each release finishes exactly one more event: the count advances step by step —
    // the property a single sample cannot establish.
    for released in 1L .. 10L do
        permits.Release() |> ignore
        test <@ waitUntilTrue (fun () -> host.CompletedDispatches() = released) 10_000 @>

    test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000 @>

// ---------------------------------------------------------------------------
// Slow is not stuck: the cold-start false positive.
// ---------------------------------------------------------------------------
//
// A workspace with no impact database makes test-prune do its largest single unit of
// work — every symbol in the tree is a seed, and the selection is the whole suite. That
// runs inside ONE event fold: the plugin owns work, nothing is `Running`, and no plugin
// event finishes anywhere in the host for as long as it takes. Byte for byte, that is
// the signature of an event whose handler never returned, and the detector called it
// WEDGED and failed the check — on work that completed seconds later.
//
// The two cases below are the whole distinction, and they differ in ONE thing: whether
// the fold said what it was doing. `stuckHandler` above is the undeclared stall and is
// still named in milliseconds. A fold that declares a bounded unit of work is waited on
// while it is inside that bound — and named the moment it outlives it.

/// One long unit of work, declared and bounded, exactly as a cold attribution does it.
let private declaringHandler
    (name: string)
    (deadline: TimeSpan)
    (entered: ManualResetEventSlim)
    (release: ManualResetEventSlim)
    =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun ctx state _event ->
            async {
                use _bound = ctx.DeclareBoundedWork "cold impact attribution" deadline
                entered.Set()
                release.Wait()
                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 60_000)>]
let ``one long declared unit of work with no host events is not a wedge`` () =
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        // A deadline far longer than this test: the work is inside its bound throughout.
        host.RegisterHandler(declaringHandler "cold-start-plugin" (TimeSpan.FromMinutes 5.0) entered release)
        host.EmitBuildCompleted(BuildSucceeded)

        Assert.True(entered.Wait(TimeSpan.FromSeconds 10.0), "the declared work must start")

        // The shape the detector used to get wrong, asserted rather than assumed: work
        // owned, and no plugin event finishing anywhere in the host.
        test <@ host.AnyPluginBusy() @>
        test <@ host.CompletedDispatches() = 0L @>

        // Stall threshold well under the overall timeout: if the detector still fired it
        // would do so at 300ms, long before the 2s timeout, and the message would say so.
        let failure =
            Assert.Throws<TimeoutException>(fun () ->
                Daemon.waitForAllTerminalCore
                    host
                    (TimeSpan.FromSeconds 2.0)
                    (TimeSpan.FromMilliseconds 300.0)
                    CancellationToken.None
                |> fun t -> t.GetAwaiter().GetResult())

        // Still waiting — the honest answer — not a diagnosis of a bug that is not there.
        test <@ not (failure.Message.Contains "WEDGED") @>
        test <@ failure.Message.Contains "timed out" @>
    finally
        release.Set()

[<Fact(Timeout = 60_000)>]
let ``declared work held past its own deadline still reads as wedged`` () =
    // The negative control, and the reason a declaration is not a loophole: it buys the
    // fold the bound it NAMED and not one second more. Outlive it and the detector says
    // so, naming the plugin, exactly as it does for work that declared nothing.
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        host.RegisterHandler(declaringHandler "overrunning-plugin" (TimeSpan.FromMilliseconds 200.0) entered release)
        host.EmitBuildCompleted(BuildSucceeded)

        Assert.True(entered.Wait(TimeSpan.FromSeconds 10.0), "the declared work must start")

        let failure =
            Assert.Throws<TimeoutException>(fun () ->
                Daemon.waitForAllTerminalCore
                    host
                    (TimeSpan.FromSeconds 30.0)
                    (TimeSpan.FromMilliseconds 400.0)
                    CancellationToken.None
                |> fun t -> t.GetAwaiter().GetResult())

        test <@ failure.Message.Contains "WEDGED" @>
        test <@ failure.Message.Contains "overrunning-plugin" @>
    finally
        release.Set()
