module FsHotWatch.Tests.SupervisedProcessTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch

let private awaitResult (work: Task<unit>) =
    let winner =
        Task.WhenAny([| work :> Task; Task.Delay(TimeSpan.FromSeconds 5.0) |]).GetAwaiter().GetResult()

    Assert.True(
        obj.ReferenceEquals(work, winner),
        "the original operation receipt must settle within the observation bound"
    )

    work.GetAwaiter().GetResult()

// Real OS termination belongs in the integration project, alongside
// ProcessHelperTimeoutTests, so kernel scheduling cannot perturb unit coverage.

[<Fact(Timeout = 30000)>]
[<Trait("A106Supervision", "OwnedProcessDeadline")>]
let ``deadline terminates its own process without killing a sibling operation`` () =
    let store = PluginWorkOwner.Store()
    let parent = ProcessRegistry.Registry()
    use scope = ProcessRegistry.install parent
    use entered = new ManualResetEventSlim(false)
    use childStarted = new ManualResetEventSlim(false)

    let scheduled =
        TaskCompletionSource<unit -> unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let startInfo () =
        let info = Diagnostics.ProcessStartInfo("/bin/sleep", "15")
        info.UseShellExecute <- false
        info

    use sibling = new Diagnostics.Process(StartInfo = startInfo ())
    let mutable siblingStarted = false
    // Keep the handle owned by the fixture until all observations and cleanup
    // finish; the worker must not dispose it before the death assertion reads it.
    use child = new Diagnostics.Process(StartInfo = startInfo ())

    let queue =
        SupervisedWork.Queue(
            store,
            "owned-process",
            0,
            TimeSpan.FromMinutes 1.0,
            (fun state (_: int) -> state),
            (fun state _ -> state),
            ignore,
            (fun state _ _ _ ->
                async {
                    Assert.True(child.Start())
                    childStarted.Set()
                    ProcessRegistry.track child
                    entered.Set()

                    try
                        // Deliberately ignores the operation token: cancellation
                        // alone cannot terminate an OS child at this boundary.
                        Assert.True(child.WaitForExit(15000), "fixture child has a natural lifetime bound")
                        return state + 1
                    finally
                        ProcessRegistry.untrack child
                }),
            scheduleDeadline =
                (fun _ expire ->
                    scheduled.SetResult expire

                    { new IDisposable with
                        member _.Dispose() = () })
        )

    let stopOwned (owned: Diagnostics.Process) =
        try
            if not owned.HasExited then
                owned.Kill(entireProcessTree = true)
        with :? InvalidOperationException when owned.HasExited ->
            ()

        Assert.True(owned.WaitForExit(5000), "fixture-owned process must be reaped")

    let mutable receipt: Task<unit> option = None

    try
        Assert.True(sibling.Start())
        siblingStarted <- true
        parent.Track sibling
        let active = queue.Submit(1, CancellationToken.None)
        receipt <- Some active
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0), "owned child must start before its deadline")
        Assert.False(child.HasExited)
        Assert.False(sibling.HasExited, "positive control: sibling is alive before deadline")
        Assert.Contains(child, parent.Snapshot())
        Assert.Contains(sibling, parent.Snapshot())

        let expire =
            scheduled.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        expire ()
        Assert.True(child.WaitForExit(7000), "deadline must terminate the supervised operation's child")
        Assert.False(sibling.HasExited, "deadline must preserve a sibling owned by the parent scope")
        Assert.Throws<TimeoutException>(fun () -> awaitResult active) |> ignore
        Assert.False(store.Snapshot.IsBusy)
        Assert.Equal(0, queue.State)
        Assert.DoesNotContain(child, parent.Snapshot())
        Assert.Contains(sibling, parent.Snapshot())
    finally
        // Close admission first. Reap the exact owned handles independently of
        // whether future operation scopes also register them with their parent.
        try
            queue.Close()
        finally
            try
                if childStarted.IsSet then
                    stopOwned child
            finally
                try
                    if siblingStarted then
                        stopOwned sibling
                finally
                    try
                        parent.KillAll()
                    finally
                        match receipt with
                        | Some active ->
                            try
                                awaitResult active
                            with
                            | :? TimeoutException
                            | :? OperationCanceledException -> ()
                        | None -> ()

[<Fact(Timeout = 30000)>]
[<Trait("A106Supervision", "DisposedChildHandle")>]
let ``disposing a tracked live child cannot earn successful operation retirement`` () =
    let store = PluginWorkOwner.Store()
    let parent = ProcessRegistry.Registry()
    use scope = ProcessRegistry.install parent
    let mutable observer: Diagnostics.Process option = None

    let queue =
        SupervisedWork.Queue(
            store,
            "disposed-child",
            0,
            TimeSpan.FromSeconds 20.0,
            (fun state (_: unit) -> state),
            (fun _ _ -> -1),
            ignore,
            (fun state _ _ _ ->
                async {
                    let info = Diagnostics.ProcessStartInfo("/bin/sleep", "15")
                    info.UseShellExecute <- false
                    use tracked = Diagnostics.Process.Start(info)
                    Assert.False(tracked.HasExited, "positive control: child is alive before tracking")
                    // Acquire this fixture-owned observer while the admitted child is
                    // known live. Cleanup never re-resolves a historical PID.
                    observer <- Some(Diagnostics.Process.GetProcessById tracked.Id)
                    ProcessRegistry.track tracked
                    return state + 1
                })
        )

    let mutable receipt: Task<unit> option = None

    try
        let active = queue.Submit((), CancellationToken.None)
        receipt <- Some active

        let winner =
            Task.WhenAny([| active :> Task; Task.Delay(5000) |]).GetAwaiter().GetResult()

        Assert.True(
            obj.ReferenceEquals(active, winner),
            "the original receipt must settle before inspecting its outcome"
        )

        let result =
            try
                active.GetAwaiter().GetResult()
                Ok()
            with failure ->
                Result.Error failure

        let child =
            observer
            |> Option.defaultWith (fun () -> failwith "fixture child never started")

        match result with
        | Ok() -> Assert.True(child.HasExited, "successful retirement must establish that the tracked child exited")
        | Result.Error _ ->
            Assert.Contains(parent.Leaks, fun leak -> leak.Pid = child.Id)
            Assert.False(store.Snapshot.IsBusy)
            Assert.Equal(-1, queue.State)
    finally
        try
            queue.Close()
        finally
            try
                match observer with
                | Some child ->
                    try
                        if not child.HasExited then
                            child.Kill(entireProcessTree = true)

                        Assert.True(child.WaitForExit(5000), "fixture observer must reap its own child")
                    finally
                        child.Dispose()
                | None -> ()
            finally
                parent.KillAll()

                match receipt with
                | Some active ->
                    // A real completed receipt, not an observation timeout.
                    let winner =
                        Task.WhenAny([| active :> Task; Task.Delay(5000) |]).GetAwaiter().GetResult()

                    Assert.True(obj.ReferenceEquals(active, winner), "original receipt must settle during cleanup")

                    if active.IsFaulted then
                        active.Exception |> ignore
                | None -> ()

[<Theory(Timeout = 30000)>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("A106Supervision", "ExclusiveChildRetirement")>]
let ``exclusive completion cannot retire before its tracked child exits`` (failWork: bool) =
    let parent = ProcessRegistry.Registry()
    use scope = ProcessRegistry.install parent
    let host = PluginHost.PluginHost(Unchecked.defaultof<_>, "/tmp")
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let folded =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let startInfo = Diagnostics.ProcessStartInfo("/bin/sleep", "20")
    startInfo.UseShellExecute <- false
    use child = new Diagnostics.Process(StartInfo = startInfo)
    let mutable started = false
    let workFailure = InvalidOperationException("exclusive fixture work failed")

    let handler: PluginFramework.PluginHandler<unit, unit> =
        { Name = PluginFramework.PluginName.create "exclusive-child"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | Events.FileChanged _ ->
                        let claim =
                            ctx.RunExclusive
                                "child"
                                (async {
                                    Assert.True(child.Start())
                                    started <- true
                                    ProcessRegistry.track child
                                    entered.Set()
                                    Assert.True(release.Wait(TimeSpan.FromSeconds 10.0))

                                    if failWork then
                                        raise workFailure

                                    return ()
                                })

                        Assert.Equal(PluginFramework.Claimed, claim)
                    | Events.Custom() -> folded.TrySetResult(()) |> ignore
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton PluginFramework.SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    host.RegisterHandler handler

    try
        host.EmitFileChanged(Events.SourceChanged [ "/tmp/exclusive-child.fs" ])
        Assert.True(entered.Wait(TimeSpan.FromSeconds 5.0), "exclusive work must launch its actual child")
        Assert.False(child.HasExited)
        Assert.Contains(child, parent.Snapshot())
        Assert.True(host.AnyPluginBusy())
        release.Set()

        if not failWork then
            folded.Task.WaitAsync(TimeSpan.FromSeconds 7.0).GetAwaiter().GetResult()

        Assert.True(
            SpinWait.SpinUntil((fun () -> not (host.AnyPluginBusy())), TimeSpan.FromSeconds 5.0),
            "completion fold must actually retire before child lifetime is inspected"
        )

        Assert.Equal(not failWork, folded.Task.IsCompletedSuccessfully)
        Assert.Empty(host.FaultedPlugins())

        if failWork then
            let failures = host.FailedWork()
            Assert.Single(failures) |> ignore
            Assert.Same(workFailure, snd failures.Head)
        else
            Assert.Empty(host.FailedWork())

        Assert.True(child.HasExited, "exclusive retirement must establish termination of its tracked child")
        Assert.DoesNotContain(child, parent.Snapshot())
    finally
        release.Set()

        if started then
            if not child.HasExited then
                child.Kill(entireProcessTree = true)

            Assert.True(child.WaitForExit(5000), "fixture must reap only its own child")

        parent.KillAll()
        host.Teardown()
