module FsHotWatch.Tests.WaitWedgeTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework

// A plugin whose handler never returns keeps its inflight count above zero
// forever, so `AnyPluginBusy` stays true and BOTH satisfaction paths in
// `waitForAllTerminalCore` are blocked — nothing is Running, yet the wait can
// never resolve.
//
// Before the stall detector that state was indistinguishable from "still
// working": the wait sat there for the full hour-long timeout emitting
// "All plugins terminal, waiting for quiescence..." and then reported "all
// terminal but quiescence check failed". Three of those cost an hour each and
// blocked every deploy behind them, because `check` gates the deploy preflight.
//
// The hand-off this state legitimately covers — a run finishing and its
// completion message being handled — takes milliseconds. A plugin doing real
// work is `Running`, which is a different branch. So a busy set that does not
// change for the threshold is a stuck plugin, and saying so beats hanging.

/// A handler subscribed to BuildCompleted whose Update blocks until the test
/// releases it. Models the real shape: an event whose handler never returns.
let private stuckHandler (name: string) (release: ManualResetEventSlim) =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun _ctx state _event ->
            async {
                // Blocks the plugin's mailbox: the event is counted in flight
                // until this returns, which the test controls.
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

        // Dispatch an event the handler subscribes to; its handler will block,
        // so the plugin reports busy from here until the test releases it.
        host.EmitBuildCompleted(BuildSucceeded)

        // Let the mailbox pick the event up so the inflight count is non-zero.
        Thread.Sleep 200

        test <@ host.AnyPluginBusy() @>
        test <@ host.BusyPluginNames() |> List.contains "wedged-plugin" @>

        // A generous overall timeout with a TINY stall threshold: if the wait
        // resolved on the timeout rather than the stall detector, this test
        // would take 60s and the message would not name the plugin.
        let ex =
            Assert.Throws<TimeoutException>(fun () ->
                Daemon.waitForAllTerminalCore
                    host
                    (TimeSpan.FromSeconds 30.0)
                    false
                    (TimeSpan.FromMilliseconds 300.0)
                    CancellationToken.None
                |> fun t -> t.GetAwaiter().GetResult())

        // The message must identify the culprit — the whole point is that a
        // human reading it knows where to look without reading the source.
        test <@ ex.Message.Contains "WEDGED" @>
        test <@ ex.Message.Contains "wedged-plugin" @>
    finally
        // Release the handler so the blocked mailbox thread can unwind.
        release.Set()

/// Poll until `cond` holds, up to `timeout`. Returns whether it held.
///
/// A FIXED sleep cannot express what these tests assert. The bug is that the
/// count never returns to zero — not that it is non-zero at some instant — and
/// under parallel test load a plugin that is merely still working is non-zero
/// for a good reason. A sleep long enough to be reliable would also be long
/// enough to hide a slow leak; this distinguishes "transiently busy" from
/// "busy forever" without either race. Pre-fix the count only rises, so no
/// timeout makes this pass.
let private waitUntil (timeout: TimeSpan) (cond: unit -> bool) =
    let deadline = DateTime.UtcNow + timeout

    let mutable ok = cond ()

    while not ok && DateTime.UtcNow < deadline do
        Thread.Sleep 25
        ok <- cond ()

    ok

// A handler whose CACHE-KEY function throws. This is not a contrived fault: the
// dispatch loop computes `handler.CacheKey event` BEFORE entering the
// `try/finally` that decrements the inflight count (PluginFramework, dispatch
// loop). TestPrune's `dependsOnHash` hashes every file matched by the
// `dependsOn` globs, and its per-file arm runs `fcsCheckSignature` over raw FCS
// results — I/O and third-party data shapes, on the dispatch thread. A throw
// there escapes the loop entirely.
//
// Field evidence (thellma-intelligence daemon.log): test-prune logged
// "TestPrune DB was recreated (schema change) — cleared 0 FCS check-cache
// entries so every file re-indexes on this scan", then went COMPLETELY silent
// for 61.6 minutes while the host reported it busy and `WaitForComplete` ran to
// 3584s. A plugin doing work logs; a plugin draining a backlog logs as it
// drains. An hour of silence while counted busy is a dead agent. The recreate
// forces every file to re-index, so the per-file key arm is dispatched over the
// whole repo at once — which is why the wedge tracks schema bumps rather than
// trees.
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
//
// `ErrorLedger` and the scan-signal agent both subscribe to `agent.Error` for
// exactly this reason ("a programming bug ... log loudly"). The plugin agent —
// the one that owns the counter the wait consults — is the one that does not.
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
    let settled =
        waitUntil (TimeSpan.FromSeconds 10.0) (fun () -> not (host.AnyPluginBusy()))

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
    // The leak is MONOTONIC, which is what makes it unrecoverable in the field:
    // once the agent is dead, every later dispatch adds another phantom unit of
    // in-flight work. This pins the property the counter must keep — a plugin
    // that faulted once must not accumulate busy-ness from later events.
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")
    host.RegisterHandler(throwingCacheKeyHandler "faulting-plugin")

    host.EmitBuildCompleted(BuildSucceeded)

    test <@ waitUntil (TimeSpan.FromSeconds 10.0) (fun () -> not (host.AnyPluginBusy())) @>

    host.EmitBuildCompleted(BuildSucceeded)

    test <@ waitUntil (TimeSpan.FromSeconds 10.0) (fun () -> not (host.AnyPluginBusy())) @>

[<Fact(Timeout = 60_000)>]
let ``a dispatch fault must not stomp the status of a live exclusive run`` () =
    // The forced `Failed` a dispatch fault reports follows the SAME ownership
    // rule as `safeUpdate`'s: a live exclusive run reported `Running` when it
    // claimed, and its completion path is guaranteed to deliver a terminal. If
    // an unrelated dispatch fault could overwrite that with `Failed`, the
    // framework would manufacture a terminal verdict for a run still executing
    // — which is exactly how a crashing per-file handler once produced a
    // terminal mid-test-run. So the fault is logged and accounted for, but the
    // status is left to its owner.
    use release = new ManualResetEventSlim(false)

    let handler =
        { Name = PluginName.create "run-owns-status"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | BuildCompleted _ ->
                        // Claim the slot: the framework reports Running and holds
                        // the work token until this returns. The claim must be
                        // MATCHED — a dropped `SlotBusy` is dropped work, and here
                        // it would also mean the test quietly stopped testing what
                        // it claims to (no live run left to own the status).
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

        // Wait until the run has actually claimed and published Running.
        let running =
            waitUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                match host.GetStatus "run-owns-status" with
                | Some(Running _) -> true
                | _ -> false)

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
    // The positive control for the detector above: same wait, same tiny stall
    // threshold, over a host where nothing is stuck. If this ALSO raised, the
    // detector would be firing on healthy hosts and the test above would prove
    // nothing.
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
