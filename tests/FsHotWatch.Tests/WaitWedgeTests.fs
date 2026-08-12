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
