/// Consumer leases on exclusive work. A client that asks for a run holds a lease on it for
/// as long as it is connected; the daemon's own wants (the watcher, a plugin's own event)
/// hold a lease no client can release. When the last lease goes, a queued intent is
/// withdrawn and a running, cooperative-safe run is cancelled without publishing anything.
module FsHotWatch.Tests.PluginFrameworkLeaseTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.PluginFrameworkFixtures

type LeaseMsg =
    /// Asked for by a client command or by the daemon's own event.
    | Wanted
    /// The launched run finished and its result folds.
    | RunDone
    /// The daemon-owned run holding the key finished.
    | HoldDone

let private signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let private bound = TimeSpan.FromSeconds 10.0

/// Everything a test observes about one plugin.
type private Probe =
    {
        /// Holds the key with a daemon-owned run until released, so intents queue.
        HoldRelease: TaskCompletionSource<unit>
        /// Released to let a launched `Wanted` run finish on its own.
        RunRelease: TaskCompletionSource<unit>
        RunStarted: TaskCompletionSource<unit>
        RunCancelled: TaskCompletionSource<unit>
        /// What happened, in order: "hold-claimed", "hold-done", "wanted", "run-done".
        Folds: Collections.Concurrent.ConcurrentQueue<string>
        Statuses: Collections.Concurrent.ConcurrentQueue<PluginStatus>
        /// When true, the run starts a long-lived child in its process scope first.
        SpawnChild: bool
        Child: Diagnostics.Process option ref
        /// Client requests whose intent has been enqueued.
        Requested: int ref
    }

let private newProbe () =
    { HoldRelease = signal ()
      RunRelease = signal ()
      RunStarted = signal ()
      RunCancelled = signal ()
      Folds = Collections.Concurrent.ConcurrentQueue<string>()
      Statuses = Collections.Concurrent.ConcurrentQueue<PluginStatus>()
      SpawnChild = false
      Child = ref None
      Requested = ref 0 }

/// Wait on `gate` in a way cancellation interrupts: `Async.AwaitTask` alone does not.
let private awaitCancellably (gate: Task<unit>) =
    async {
        let! ct = Async.CancellationToken
        do! gate.WaitAsync(ct) |> Async.AwaitTask
    }

/// A plugin whose `FileChanged` holds the "work" key with a daemon-owned run, and whose
/// `want` command enqueues a coalescing `Wanted` intent on that key. A `Wanted` fold
/// launches a run — cooperative-safe when `safe` — that waits on `RunRelease`.
/// `SolutionChanged` enqueues the same intent from the daemon's own event. With `shared`,
/// the run also takes the host-wide "artifacts" resource.
let private leasePluginWith (probe: Probe) (safe: bool) (shared: bool) : PluginHandler<unit, LeaseMsg> =
    let wantedRun =
        async {
            use! _cancel = Async.OnCancel(fun () -> probe.RunCancelled.TrySetResult(()) |> ignore)

            if probe.SpawnChild then
                // Started and tracked the way `ProcessHelper.runProcess` does.
                let psi = Diagnostics.ProcessStartInfo("sleep", "120")
                psi.UseShellExecute <- false
                let child = Diagnostics.Process.Start psi
                ProcessRegistry.track child
                probe.Child.Value <- Some child

            probe.RunStarted.TrySetResult(()) |> ignore
            do! awaitCancellably probe.RunRelease.Task
            return RunDone
        }

    { Name = PluginName.create "leased"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | FileChanged(SourceChanged _) ->
                    let holding =
                        ctx.RunExclusive
                            "work"
                            (async {
                                do! probe.HoldRelease.Task |> Async.AwaitTask
                                return HoldDone
                            })

                    test <@ holding = Claimed @>
                    probe.Folds.Enqueue "hold-claimed"
                    return state
                | FileChanged SolutionChanged ->
                    ctx.EnqueueExclusiveIntent "work" (Some "wanted") Wanted |> ignore
                    return state
                | Custom HoldDone ->
                    probe.Folds.Enqueue "hold-done"
                    return state
                | Custom Wanted ->
                    probe.Folds.Enqueue "wanted"

                    let work =
                        if safe then
                            PluginWork.cooperativeSafe wantedRun
                        else
                            wantedRun

                    if shared then
                        match
                            ctx.RunExclusiveShared "work" "artifacts" (fun _ -> work) (fun _ -> Ready) (fun _ ->
                                RunDone)
                        with
                        | SharedClaimed
                        | SharedQueued -> ()
                        | LocalSlotBusy -> failwith "a delivered intent holds the key it launches under"
                    else
                        match ctx.RunExclusive "work" work with
                        | Claimed -> ()
                        | SlotBusy -> failwith "a delivered intent holds the key it launches under"

                    return state
                | Custom RunDone ->
                    probe.Folds.Enqueue "run-done"
                    return state
                | _ -> return state
            }
      Commands =
        [ "want",
          PluginCommand.Request(fun ctx _ ->
              async {
                  let receipt = ctx.EnqueueExclusiveIntent "work" (Some "wanted") Wanted
                  Interlocked.Increment(&probe.Requested.contents) |> ignore
                  do! receipt |> Async.AwaitTask
                  return "admitted"
              }) ]
      Subscriptions = Set.singleton SubscribeFileChanged
      CacheKey = None
      PrepareCommit = None
      Teardown = None }

type private Harness =
    { Registration: RegisteredPlugin
      Want: CancellationToken -> Task<string> }

let private registerWithServices (services: PluginHostServices) (probe: Probe) (safe: bool) (shared: bool) =
    let mutable command: CommandHandler option = None

    let registration =
        registerHandler
            { services with
                RegisterCommand = fun (_, handler) -> command <- Some handler
                ReportStatus = fun _ status -> probe.Statuses.Enqueue status }
            (leasePluginWith probe safe shared)

    { Registration = registration
      // A client's command runs under that client's connection token, as the IPC
      // server runs it.
      Want = fun client -> Async.StartAsTask(command.Value [||], cancellationToken = client) }

let private register probe safe =
    registerWithServices defaultServices probe safe false

let private hold (harness: Harness) =
    dispatchAndAwait harness.Registration (DispatchFileChanged(SourceChanged [ "/tmp/repo/a.fs" ]))

let private observe (task: Task<'T>) =
    task.ContinueWith(fun (t: Task<'T>) -> t.Exception |> ignore) |> ignore
    task

let private settled (task: Task) =
    try
        task.Wait bound |> ignore
    with _ ->
        ()

    task.IsCompleted

/// Wait until `count` client requests have enqueued, so a disconnect cannot race them.
let private enqueued (probe: Probe) (count: int) =
    waitUntilTrue (fun () -> Volatile.Read(&probe.Requested.contents) = count) 10000

let private idle (harness: Harness) =
    waitUntilTrue (fun () -> not (harness.Registration.IsBusy())) 10000

[<Fact(Timeout = 30000)>]
let ``a queued run only a client wanted is withdrawn when that client goes away`` () =
    let probe = newProbe ()
    let harness = register probe true
    hold harness
    use client = new CancellationTokenSource()
    let admitted = harness.Want client.Token |> observe
    test <@ enqueued probe 1 @>
    test <@ not admitted.IsCompleted @>

    client.Cancel()
    probe.HoldRelease.TrySetResult(()) |> ignore

    test <@ idle harness @>
    // The withdrawn intent never folded, so nothing ran on its behalf.
    test <@ List.ofSeq probe.Folds = [ "hold-claimed"; "hold-done" ] @>
    test <@ not probe.RunStarted.Task.IsCompleted @>

[<Fact(Timeout = 30000)>]
let ``a running cooperative-safe run only a client wanted is cancelled and publishes nothing`` () =
    let probe = newProbe ()
    let harness = register probe true
    use client = new CancellationTokenSource()
    harness.Want client.Token |> observe |> ignore
    test <@ probe.RunStarted.Task.Wait bound @>
    let before = probe.Statuses.ToArray()

    client.Cancel()

    test <@ probe.RunCancelled.Task.Wait bound @>
    test <@ idle harness @>
    test <@ List.ofSeq probe.Folds = [ "wanted" ] @>
    // The status the claim displaced is reported back: a cancelled run is not a verdict.
    let after = probe.Statuses.ToArray() |> Array.skip before.Length
    test <@ after = [| Idle |] @>

[<Fact(Timeout = 30000)>]
let ``a running run not declared cooperative-safe runs to completion after its client goes`` () =
    let probe = newProbe ()
    let harness = register probe false
    use client = new CancellationTokenSource()
    harness.Want client.Token |> observe |> ignore
    test <@ probe.RunStarted.Task.Wait bound @>

    client.Cancel()
    test <@ not (probe.RunCancelled.Task.Wait 300) @>
    probe.RunRelease.TrySetResult(()) |> ignore

    test <@ waitUntilTrue (fun () -> Seq.contains "run-done" probe.Folds) 10000 @>
    test <@ not probe.RunCancelled.Task.IsCompleted @>

[<Fact(Timeout = 30000)>]
let ``a run the daemon also wants survives the client that asked for it`` () =
    let probe = newProbe ()
    let harness = register probe true
    hold harness
    use client = new CancellationTokenSource()
    let admitted = harness.Want client.Token |> observe
    test <@ enqueued probe 1 @>
    // The daemon's own event wants the same work, and coalesces onto the queued intent.
    dispatchAndAwait harness.Registration (DispatchFileChanged SolutionChanged)

    client.Cancel()
    probe.HoldRelease.TrySetResult(()) |> ignore

    test <@ probe.RunStarted.Task.Wait bound @>
    test <@ not (probe.RunCancelled.Task.Wait 300) @>
    probe.RunRelease.TrySetResult(()) |> ignore
    test <@ waitUntilTrue (fun () -> Seq.contains "run-done" probe.Folds) 10000 @>
    test <@ settled admitted @>

[<Fact(Timeout = 30000)>]
let ``a run two clients wanted continues until the second one goes`` () =
    let probe = newProbe ()
    let harness = register probe true
    hold harness
    use first = new CancellationTokenSource()
    use second = new CancellationTokenSource()
    harness.Want first.Token |> observe |> ignore
    harness.Want second.Token |> observe |> ignore
    test <@ enqueued probe 2 @>

    // One client leaves while the run is still queued: the other still holds it.
    first.Cancel()
    probe.HoldRelease.TrySetResult(()) |> ignore
    test <@ probe.RunStarted.Task.Wait bound @>
    test <@ not (probe.RunCancelled.Task.Wait 300) @>

    // The last lease goes: the running run is cancelled.
    second.Cancel()
    test <@ probe.RunCancelled.Task.Wait bound @>
    test <@ idle harness @>
    test <@ not (Seq.contains "run-done" probe.Folds) @>

[<Fact(Timeout = 30000)>]
let ``a cancelled shared run hands the resource back in the state it was handed`` () =
    let probe = newProbe ()
    let releases = Collections.Concurrent.ConcurrentQueue<SharedResourceState>()

    let services =
        { defaultServices with
            ClaimOrQueueSharedRun = fun _ _ -> Some(Invalid "stale before the run")
            ReleaseSharedRun = fun _ state -> releases.Enqueue state }

    let harness = registerWithServices services probe true true
    use client = new CancellationTokenSource()
    harness.Want client.Token |> observe |> ignore
    test <@ probe.RunStarted.Task.Wait bound @>

    client.Cancel()

    test <@ probe.RunCancelled.Task.Wait bound @>
    test <@ idle harness @>
    // Not `Ready` (a verdict it never earned) and not "shared work faulted" (a failure
    // it never had): the cancelled run learned nothing about the resource.
    test <@ List.ofSeq releases = [ Invalid "stale before the run" ] @>

[<Fact(Timeout = 30000)>]
let ``a cancelled shared run whose resource cannot be handed back fails instead of vanishing`` () =
    // The run is abandoned, but the host-wide resource it held is still owed back. When
    // handing it back fails, that failure is the run's verdict — a silent abandonment
    // would leave the resource held with nothing saying why.
    let probe = newProbe ()

    let services =
        { defaultServices with
            ReleaseSharedRun = fun _ _ -> failwith "release refused" }

    let harness = registerWithServices services probe true true
    use client = new CancellationTokenSource()
    harness.Want client.Token |> observe |> ignore
    test <@ probe.RunStarted.Task.Wait bound @>

    client.Cancel()

    test <@ probe.RunCancelled.Task.Wait bound @>
    test <@ idle harness @>

    test
        <@
            probe.Statuses
            |> Seq.exists (function
                | Failed(message, _, _) -> message.Contains "shared release after cancellation failed"
                | _ -> false)
        @>

[<Fact(Timeout = 30000)>]
let ``a shared run whose client goes while it waits for the resource never starts`` () =
    let probe = newProbe ()
    let releases = Collections.Concurrent.ConcurrentQueue<SharedResourceState>()
    let queuedStart = ref None

    let services =
        { defaultServices with
            // Another plugin holds the resource: the run is claimed but queued for it.
            ClaimOrQueueSharedRun =
                fun _ start ->
                    queuedStart.Value <- Some start
                    None
            ReleaseSharedRun = fun _ state -> releases.Enqueue state }

    let harness = registerWithServices services probe true true
    use client = new CancellationTokenSource()
    harness.Want client.Token |> observe |> ignore
    test <@ waitUntilTrue (fun () -> queuedStart.Value.IsSome) 10000 @>

    client.Cancel()
    // The resource is handed over after the client has gone.
    let started =
        match queuedStart.Value.Value Ready with
        | SharedStarted -> true
        | SharedStartFailed _ -> false

    test <@ started @>

    test <@ idle harness @>
    test <@ not probe.RunStarted.Task.IsCompleted @>
    test <@ List.ofSeq releases = [ Ready ] @>
    test <@ not (Seq.contains "run-done" probe.Folds) @>

[<Fact(Timeout = 30000)>]
let ``a cancelled run's child processes are reaped`` () =
    let probe = { newProbe () with SpawnChild = true }
    let harness = register probe true
    use client = new CancellationTokenSource()
    harness.Want client.Token |> observe |> ignore
    test <@ probe.RunStarted.Task.Wait bound @>
    let child = probe.Child.Value.Value

    try
        test <@ not child.HasExited @>

        client.Cancel()

        test <@ child.WaitForExit(10000) @>
        test <@ idle harness @>
    finally
        if not child.HasExited then
            child.Kill true

type private FunnelMsg =
    | FunnelWanted
    | FunnelRunDone

let private summaryOf (status: PluginStatus) =
    match status with
    | Completed(_, verdict) -> Some verdict.Summary
    | _ -> None

/// Both halves of the status funnel at once. A cancelled cooperative-safe run puts back
/// the status its `Running` displaced — through the same funnel every report takes — and
/// that funnel still drops an unrelated terminal while a FINISHED run's result fold is
/// owed, letting only the run's own verdict land.
[<Fact(Timeout = 30000)>]
let ``a cancelled run restores its displaced status and an owed run verdict still gates terminals`` () =
    let statuses = Collections.Concurrent.ConcurrentQueue<PluginStatus>()
    let wantedStarted = signal ()
    let wantedCancelled = signal ()
    use workerGo = new ManualResetEventSlim(false)
    let mutable command: CommandHandler option = None

    let handler: PluginHandler<unit, FunnelMsg> =
        { Name = PluginName.create "funnel"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged [ "before" ]) ->
                        ctx.ReportStatus(PluginStatus.completedNow "before the run" TimeSpan.Zero)
                    | Custom FunnelWanted ->
                        let wanted =
                            PluginWork.cooperativeSafe (
                                async {
                                    use! _cancel = Async.OnCancel(fun () -> wantedCancelled.TrySetResult(()) |> ignore)

                                    wantedStarted.TrySetResult(()) |> ignore
                                    do! Async.Sleep Timeout.Infinite
                                    return FunnelRunDone
                                }
                            )

                        match ctx.RunExclusive "work" wanted with
                        | Claimed -> ()
                        | SlotBusy -> failwith "the delivered intent holds the key"
                    | FileChanged(SourceChanged [ "claim" ]) ->
                        match
                            ctx.RunExclusive
                                "work"
                                (async {
                                    workerGo.Wait()
                                    return FunnelRunDone
                                })
                        with
                        | Claimed -> ()
                        | SlotBusy -> failwith "the key is free"
                    | FileChanged(SourceChanged [ "hold" ]) ->
                        // Until the worker has retired into its result fold, which then
                        // queues behind "report": that fold is owed while "report" runs.
                        while ctx.SlotHolder "work" = SlotHolder.LiveRun do
                            Thread.Sleep 5
                    | FileChanged(SourceChanged [ "report" ]) ->
                        ctx.ReportStatus(PluginStatus.completedNow "unrelated terminal" TimeSpan.Zero)
                    | Custom FunnelRunDone -> ctx.ReportStatus(PluginStatus.completedNow "run done" TimeSpan.Zero)
                    | _ -> ()

                    return state
                }
          Commands =
            [ "want",
              PluginCommand.Request(fun ctx _ ->
                  async {
                      do! ctx.EnqueueExclusiveIntent "work" None FunnelWanted |> Async.AwaitTask
                      return "admitted"
                  }) ]
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let registration =
        registerHandler
            { defaultServices with
                RegisterCommand = fun (_, handler) -> command <- Some handler
                ReportStatus = fun _ status -> statuses.Enqueue status }
            handler

    /// Dispatch without waiting; the answer completes when the event has committed.
    let dispatch file : Task<unit> =
        match registration.DispatchTracked(DispatchFileChanged(SourceChanged [ file ])) with
        | Some receipt -> receipt.Wait(TimeSpan.FromSeconds 30.0)
        | None -> failwith "subscribed"

    // A terminal the plugin reported before any run: what a run's `Running` displaces.
    test <@ (dispatch "before").Wait bound @>
    test <@ statuses.ToArray() |> Array.last |> summaryOf = Some "before the run" @>

    // A client's run, cancelled when the client goes.
    use client = new CancellationTokenSource()

    Async.StartAsTask(command.Value [||], cancellationToken = client.Token)
    |> observe
    |> ignore

    test <@ wantedStarted.Task.Wait bound @>

    test
        <@
            match statuses.ToArray() |> Array.last with
            | Running _ -> true
            | _ -> false
        @>

    client.Cancel()
    test <@ wantedCancelled.Task.Wait bound @>
    test <@ waitUntilTrue (fun () -> not (registration.IsBusy())) 10000 @>
    // Restored through the funnel: exactly the displaced terminal, and nothing after it.
    test <@ statuses.ToArray() |> Array.last |> summaryOf = Some "before the run" @>
    let afterRestore = statuses.Count

    // A daemon run whose result fold is owed while an unrelated terminal is reported.
    dispatch "claim" |> ignore
    dispatch "hold" |> ignore
    let reported = dispatch "report"
    workerGo.Set()

    test <@ reported.Wait bound @>
    test <@ waitUntilTrue (fun () -> not (registration.IsBusy())) 10000 @>

    let window = statuses.ToArray() |> Array.skip afterRestore |> Array.choose summaryOf
    // The unrelated terminal was dropped; the run's own verdict landed, and last.
    test <@ window = [| "run done" |] @>
