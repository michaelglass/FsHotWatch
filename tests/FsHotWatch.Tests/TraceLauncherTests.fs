// The launcher fshw hands TestPrune.Trace for a traced run's JIT-verification child: the
// child joins the current process scope, so closing that scope or cancelling the run ends
// it. Every test that starts a child reaps it in a `finally`, through a handle acquired
// while the child was positively alive. Registry diagnostics are logged, so the class
// shares the serialized logging collection.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.TraceLauncherTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.TestPrune
open TestPrune.Trace

let private request exe args timeout : Launch.LaunchRequest =
    { Exe = exe
      Args = args
      Env = []
      WorkDir = Path.GetTempPath()
      Timeout = timeout }

/// A verify child that would run for 30 seconds.
let private slowVerify = request "/bin/sleep" [ "30" ] (TimeSpan.FromMinutes 1.0)

/// Launch `req` on a worker (the current process scope flows to it). The first task is
/// the child, resolved while it is running; the second is the launch.
let private launchObserved (req: Launch.LaunchRequest) (ct: CancellationToken) =
    let child =
        TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously)

    let launch =
        Task.Run(fun () ->
            TraceRun.launcherWith
                (fun pid ->
                    try
                        child.TrySetResult(Process.GetProcessById pid) |> ignore
                    with failure ->
                        child.TrySetException failure |> ignore)
                req
                ct)

    child.Task, launch

/// Reap the child this test started, if it started, and wait for evidence it is gone.
let private reap (child: Task<Process>) =
    if child.IsCompletedSuccessfully then
        let p = child.Result

        try
            try
                if not p.HasExited then
                    p.Kill(entireProcessTree = true)
            with :? InvalidOperationException ->
                ()

            Assert.True(p.WaitForExit 5000, "the test must reap the child it started")
        finally
            p.Dispose()

/// Whether `task` settles, faulted or not, within ten seconds.
let private settles (task: Task) =
    obj.ReferenceEquals(Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds 10.0)).Result, task)

[<Fact(Timeout = 30000)>]
let ``closing the owning process scope mid-verify kills the verify child`` () =
    let scope = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install scope
    let child, launch = launchObserved slowVerify CancellationToken.None

    try
        Assert.True(child.Wait(TimeSpan.FromSeconds 10.0), "the verify child must start")
        Assert.Contains(scope.Snapshot(), fun tracked -> tracked.Id = child.Result.Id)

        scope.KillAll()

        Assert.True(child.Result.WaitForExit 7000, "closing the scope must kill the verify child")
        Assert.True(settles launch, "the launch must end once its child is killed")
    finally
        reap child

[<Fact(Timeout = 30000)>]
let ``cancelling the run mid-verify kills the verify child and raises, leaving its scope open`` () =
    let scope = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install scope
    use cts = new CancellationTokenSource()
    let child, launch = launchObserved slowVerify cts.Token

    try
        Assert.True(child.Wait(TimeSpan.FromSeconds 10.0), "the verify child must start")

        cts.Cancel()

        Assert.True(child.Result.WaitForExit 7000, "cancelling the run must kill the verify child")
        Assert.True(settles launch, "the launch must end once its child is killed")
        Assert.True(launch.IsFaulted || launch.IsCanceled)

        Assert.IsAssignableFrom<OperationCanceledException>(launch.Exception.InnerException)
        |> ignore

        Assert.False(scope.IsClosed, "the run's cancellation reaches only the verify child's own scope")
    finally
        reap child

[<Fact(Timeout = 30000)>]
let ``a verify child's exit code and output come back, with its arguments quoted`` () =
    let scope = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install scope

    let failed =
        TraceRun.launcher
            (request "/bin/sh" [ "-c"; "echo two words; exit 3" ] (TimeSpan.FromMinutes 1.0))
            CancellationToken.None

    let passed =
        TraceRun.launcher (request "/bin/sh" [ "-c"; "echo ok" ] (TimeSpan.FromMinutes 1.0)) CancellationToken.None

    Assert.Equal((3, "two words"), failed)
    Assert.Equal((0, "ok"), passed)

[<Fact(Timeout = 30000)>]
let ``a verify child that overruns its timeout is killed and reported as timed out`` () =
    let scope = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install scope

    let code, output =
        TraceRun.launcher (request "/bin/sleep" [ "30" ] (TimeSpan.FromMilliseconds 300.0)) CancellationToken.None

    Assert.Equal(TraceRun.TimedOutExitCode, code)
    Assert.StartsWith("timed out after ", output)
