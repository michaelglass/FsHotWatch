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

        let expire =
            scheduled.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

        expire ()
        Assert.True(child.WaitForExit(7000), "deadline must terminate the supervised operation's child")
        Assert.False(sibling.HasExited, "deadline must preserve a sibling owned by the parent scope")
        Assert.Throws<TimeoutException>(fun () -> awaitResult active) |> ignore
        Assert.False(store.Snapshot.IsBusy)
        Assert.Equal(0, queue.State)
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
