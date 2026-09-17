// Child processes are owned by the operation that spawned them.
//
// Every test here starts real children, so every test reaps what it started in a
// `finally`, through a handle it acquired itself while the child was positively alive.
// None of them resolves a historical pid during cleanup. The registry diagnostics these
// tests provoke are logged, so the class shares the serialized logging collection.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ProcessOwnershipTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

/// Reap a fixture-owned handle and wait for positive evidence that it is gone.
let private reap (child: Process) =
    try
        try
            if not child.HasExited then
                child.Kill(entireProcessTree = true)
        with :? InvalidOperationException ->
            ()

        Assert.True(child.WaitForExit(5000), "the fixture must reap the child it owns")
    finally
        child.Dispose()

/// Collects a child's first output line, a pid, and resolves it into an observer
/// handle while the announcing child is still blocked, hence positively alive.
type private PidAnnouncement() =
    let text = Text.StringBuilder()

    let observed =
        TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Observer = observed.Task

    member _.Sink(chunk: string) =
        if not observed.Task.IsCompleted then
            text.Append(chunk) |> ignore
            let seen = text.ToString()

            match seen.IndexOf('\n') with
            | -1 -> ()
            | newline ->
                try
                    observed.TrySetResult(Process.GetProcessById(Int32.Parse(seen.Substring(0, newline).Trim())))
                    |> ignore
                with failure ->
                    observed.TrySetException(failure) |> ignore

    /// Reap the announced child, if it was ever announced.
    member _.Reap() =
        if observed.Task.IsCompletedSuccessfully then
            reap observed.Task.Result

// ---------------------------------------------------------------------------
// Admission closes with shutdown
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``a process registering after shutdown cannot escape its owning registry`` () =
    let registry = ProcessRegistry.Registry()
    registry.KillAll()

    withTrackedSleep 30 (fun child ->
        Assert.False(child.HasExited)
        registry.Track child

        Assert.True(child.WaitForExit(5000), "a registry that has shut down must reap a child registering afterwards")

        Assert.Empty(registry.Snapshot()))

[<Fact(Timeout = 30000)>]
let ``closed process scope refuses launch before target side effects`` () =
    withTempDir "closed-admission" (fun directory ->
        let registry = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install registry
        registry.KillAll()

        Assert.Throws<OperationCanceledException>(fun () ->
            runProcess
                "/bin/sh"
                "-c \"echo target > executed\""
                directory
                []
                (ProcessBounds.silent (TimeSpan.FromSeconds 5.0))
            |> ignore)
        |> ignore

        Assert.False(File.Exists(Path.Combine(directory, "executed")), "the refused target must never have run")
        Assert.Empty(registry.Snapshot())
        Assert.Empty(registry.Leaks))

[<Fact(Timeout = 30000)>]
let ``a child that launches while its scope shuts down is refused, not reported`` () =
    let registry = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install registry

    withTrackedSleep 30 (fun child ->
        // The scope shuts down after the pre-launch check passed, before admission.
        ProcessRegistry.ensureAdmitting "the fixture child"
        registry.KillAll()

        let refusal =
            Assert.Throws<OperationCanceledException>(fun () ->
                ProcessRegistry.admitOrRefuse child "the fixture child")

        Assert.Contains("terminated at launch", refusal.Message)
        Assert.True(child.HasExited, "the refusing scope must have reaped the child")
        Assert.Empty(registry.Snapshot()))

// ---------------------------------------------------------------------------
// A disposed handle is not evidence of exit
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``shutdown records a disposed live handle as uncertain termination`` () =
    let registry = ProcessRegistry.Registry()
    let child = startSleep 30
    let observer = Process.GetProcessById child.Id

    try
        Assert.False(observer.HasExited, "positive control: the child is alive when its handle is disposed")
        registry.Track child
        child.Dispose()
        registry.KillAll()

        match registry.Leaks with
        | [ leak ] -> Assert.Equal(observer.Id, leak.Pid)
        | other -> Assert.Fail $"a disposed live handle must be recorded as a leak, got %A{other}"
    finally
        child.Dispose()
        reap observer

[<Fact(Timeout = 30000)>]
let ``disposing a tracked live child cannot earn successful operation retirement`` () =
    let parent = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install parent
    let mutable observer: Process option = None

    try
        let result =
            try
                runWithCancellableTimeout (TimeSpan.FromSeconds 20.0) (fun _ ->
                    use tracked = startSleep 30
                    // Acquired while the tracked child is positively live.
                    observer <- Some(Process.GetProcessById tracked.Id)
                    ProcessRegistry.track tracked
                    1)
                |> Ok
            with failure ->
                Error failure

        let child =
            observer
            |> Option.defaultWith (fun () -> failwith "the fixture child never started")

        match result with
        | Ok _ -> Assert.True(child.HasExited, "successful retirement must establish that the tracked child exited")
        | Error _ -> Assert.Contains(parent.Leaks, fun leak -> leak.Pid = child.Id)
    finally
        observer |> Option.iter reap
        parent.KillAll()

// ---------------------------------------------------------------------------
// An operation's deadline reaches its own children, and only those
// ---------------------------------------------------------------------------

/// Wait for an operation's completion task to settle, without letting a faulted
/// completion throw from a `finally`.
let private settles (completion: Task) =
    let winner =
        Task.WhenAny([| completion; Task.Delay(TimeSpan.FromSeconds 10.0) |]).GetAwaiter().GetResult()

    obj.ReferenceEquals(completion, winner)

[<Fact(Timeout = 30000)>]
let ``deadline terminates its own process without killing a sibling operation`` () =
    let parent = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install parent
    let sibling = startSleep 30
    let announcement = PidAnnouncement()
    let mutable completion = Task.CompletedTask

    try
        parent.Track sibling

        // The deadline fires at an exact point: once the operation's child has
        // announced itself, never before it could have started.
        let expireOnceAnnounced (work: Task) =
            Task.WaitAny([| work; announcement.Observer :> Task |]) = 0

        let outcome, settled =
            runWithCancellableDeadline expireOnceAnnounced (TimeSpan.FromSeconds 3.0) (fun _ignoresToken ->
                runProcessTo
                    (Some announcement.Sink)
                    "/bin/sh"
                    "-c \"echo $$; exec sleep 15\""
                    "."
                    []
                    (ProcessBounds.silent (TimeSpan.FromSeconds 30.0)))

        completion <- settled
        Assert.Equal(WorkTimedOut(TimeSpan.FromSeconds 3.0), outcome)
        let child = announcement.Observer.Result

        Assert.True(child.WaitForExit(7000), "the deadline must terminate the operation's own child")
        Assert.False(sibling.HasExited, "the deadline must not reach a sibling owned by the parent")
        Assert.Contains(sibling, parent.Snapshot())
        Assert.DoesNotContain(parent.Snapshot(), fun tracked -> tracked.Id = child.Id)
        Assert.True(settles completion, "the timed-out operation must settle once its child is gone")
    finally
        try
            announcement.Reap()
            reap sibling
        finally
            parent.KillAll()
            Assert.True(settles completion, "the timed-out operation must settle during cleanup")

[<Fact(Timeout = 30000)>]
let ``a timed-out operation cannot launch a child after its deadline`` () =
    withTempDir "late-launch" (fun directory ->
        let parent = ProcessRegistry.Registry()
        use _ = ProcessRegistry.install parent
        use expired = new ManualResetEventSlim(false)

        let outcome, completion =
            runWithCancellableDeadline (fun _ -> false) (TimeSpan.FromSeconds 1.0) (fun _ignoresToken ->
                // Deliberately waits for the EXPIRY, not the token: it launches after it.
                Assert.True(expired.Wait(TimeSpan.FromSeconds 10.0), "the fixture must be released")

                runProcess
                    "/bin/sh"
                    "-c \"echo late > executed\""
                    directory
                    []
                    (ProcessBounds.silent (TimeSpan.FromSeconds 5.0)))

        expired.Set()
        Assert.Equal(WorkTimedOut(TimeSpan.FromSeconds 1.0), outcome)
        Assert.True(settles completion, "the timed-out operation must settle")

        let refusal = Assert.Throws<AggregateException>(fun () -> completion.Wait())
        Assert.IsType<OperationCanceledException>(refusal.InnerException) |> ignore
        Assert.False(File.Exists(Path.Combine(directory, "executed")), "the late launch must never have run")
        Assert.False(parent.IsClosed, "expiring one operation must not shut its parent scope"))

// ---------------------------------------------------------------------------
// Child scopes
// ---------------------------------------------------------------------------

[<Fact(Timeout = 30000)>]
let ``synchronous child scope settles and restores its parent registry`` () =
    let parent = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install parent
    let child = startSleep 30

    try
        let result =
            ProcessRegistry.withChildScope CancellationToken.None (fun () ->
                ProcessRegistry.track child
                Assert.Contains(child, parent.Snapshot())
                42)

        Assert.Equal(42, result)
        Assert.True(child.HasExited, "settling a scope must reap what its work left tracked")
        Assert.Empty(parent.Snapshot())
        Assert.Empty(parent.Leaks)
        Assert.False(parent.IsClosed, "settling a scope must not shut its parent")

        ProcessRegistry.reportLeak 0 "restored parent" "post-scope marker"
        Assert.Contains(parent.Leaks, fun leak -> leak.Description = "restored parent")
    finally
        reap child

[<Fact(Timeout = 15000)>]
let ``child scope refuses retirement when retained ownership reports uncertainty`` () =
    let parent = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install parent

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ProcessRegistry.withChildScope CancellationToken.None (fun () ->
                ProcessRegistry.reportLeak 4242 "`dotnet test` (pid 4242)" "the kill did not return"
                1)
            |> ignore)

    Assert.Contains("could not establish termination", error.Message)
    Assert.Contains("the kill did not return", error.Message)
    Assert.Equal(4242, (Assert.Single parent.Leaks).Pid)

    ProcessRegistry.reportLeak 0 "restored parent" "post-scope marker"
    Assert.Contains(parent.Leaks, fun leak -> leak.Description = "restored parent")

[<Fact(Timeout = 15000)>]
let ``an operation's own failure survives its scope's uncertain teardown`` () =
    let parent = ProcessRegistry.Registry()
    use _ = ProcessRegistry.install parent
    let failure = IOException("the operation failed on its own")

    let raised =
        Assert.Throws<IOException>(fun () ->
            ProcessRegistry.withChildScope CancellationToken.None (fun () ->
                ProcessRegistry.reportLeak 4242 "child" "the kill was refused"
                raise failure)
            |> ignore)

    Assert.Same(failure, raised)
    Assert.Single(parent.Leaks) |> ignore

[<Fact(Timeout = 30000)>]
let ``a child registry refuses and reaps a child when its parent has shut down`` () =
    let parent = ProcessRegistry.Registry()
    let scope = ProcessRegistry.Registry(Some parent)
    parent.KillAll()

    withTrackedSleep 30 (fun child ->
        Assert.True(scope.IsClosed, "a scope whose parent shut down is closed too")
        Assert.False(scope.Admit child)
        Assert.True(child.HasExited, "the refusing parent must have reaped the child")
        Assert.Empty(scope.Snapshot())
        Assert.Empty(parent.Snapshot()))

[<Fact(Timeout = 30000)>]
let ``a closed child registry reaps a late child without keeping it in its parent`` () =
    let parent = ProcessRegistry.Registry()
    let scope = ProcessRegistry.Registry(Some parent)
    scope.KillAll()

    withTrackedSleep 30 (fun child ->
        Assert.False(parent.IsClosed)
        Assert.False(scope.Admit child)
        Assert.True(child.HasExited, "the closed scope must have reaped the child")
        Assert.Empty(parent.Snapshot())
        parent.KillAll()
        Assert.Empty(parent.Leaks))

// ---------------------------------------------------------------------------
// Pure decisions
// ---------------------------------------------------------------------------

[<Fact>]
let ``only positive exit evidence establishes termination`` () =
    let refused = ComponentModel.Win32Exception(1, "Operation not permitted")

    let disposed =
        InvalidOperationException("No process is associated with this object.")

    Assert.Equal(
        ProcessRegistry.Termination.Established,
        ProcessRegistry.classifyTermination ProcessRegistry.ExitObservation.Exited (Some refused)
    )

    let reasonOf termination =
        match termination with
        | ProcessRegistry.Termination.Uncertain reason -> reason
        | ProcessRegistry.Termination.Established ->
            Assert.Fail "expected uncertainty"
            ""

    Assert.Contains(
        "Operation not permitted",
        reasonOf (ProcessRegistry.classifyTermination ProcessRegistry.ExitObservation.Running (Some refused))
    )

    Assert.Contains(
        "still running",
        reasonOf (ProcessRegistry.classifyTermination ProcessRegistry.ExitObservation.Running None)
    )

    Assert.Contains(
        "can no longer be observed",
        reasonOf (ProcessRegistry.classifyTermination (ProcessRegistry.ExitObservation.Unobservable disposed) None)
    )

[<Fact>]
let ``a scope with no uncertain teardown retires normally`` () =
    Assert.True((ProcessRegistry.uncertainRetirement []).IsNone)

[<Fact>]
let ``a failure before the watchdog decides tears the child down first`` () =
    let events = ResizeArray<string>()
    let failure = IOException("the pump could not start")

    let raised =
        Assert.Throws<IOException>(fun () ->
            killIfUndecided (fun () -> events.Add "killed") (fun () -> raise failure)
            |> ignore)

    Assert.Same(failure, raised)
    Assert.Equal<string list>([ "killed" ], List.ofSeq events)
    Assert.Equal(7, killIfUndecided (fun () -> events.Add "unexpected") (fun () -> 7))
    Assert.Equal(1, events.Count)

// ---------------------------------------------------------------------------
// Output pumps do not depend on the caller's scheduler
// ---------------------------------------------------------------------------

/// A scheduler that runs nothing until released: stands in for a caller whose
/// context is blocked inside the very call that queued the work.
type private HeldScheduler() =
    inherit TaskScheduler()
    let gate = obj ()
    let queued = Queue<Task>()
    let mutable released = false

    member private this.Execute(task: Task) = this.TryExecuteTask task |> ignore

    override _.GetScheduledTasks() =
        lock gate (fun () -> List.ofSeq queued) :> seq<Task>

    override this.QueueTask(task: Task) =
        let runNow =
            lock gate (fun () ->
                if released then
                    true
                else
                    queued.Enqueue task
                    false)

        if runNow then
            ThreadPool.QueueUserWorkItem(fun _ -> this.Execute task) |> ignore

    override _.TryExecuteTaskInline(_, _) = false

    /// Run the first queued task on a thread of its own.
    member this.RunFirstOnOwnThread() =
        let first = lock gate (fun () -> queued.Dequeue())
        let thread = Thread(fun () -> this.Execute first)
        thread.IsBackground <- true
        thread.Start()
        thread

    member this.Release() =
        let pending =
            lock gate (fun () ->
                released <- true
                let pending = List.ofSeq queued
                queued.Clear()
                pending)

        for task in pending do
            ThreadPool.QueueUserWorkItem(fun _ -> this.Execute task) |> ignore

[<Fact(Timeout = 40000)>]
let ``runProcess preserves target exit and output without pumping the caller context`` () =
    withTempDir "process-caller-context" (fun root ->
        let scheduler = HeldScheduler()
        let registry = ProcessRegistry.Registry()
        let output = Text.StringBuilder()
        let marker = Path.Combine(root, "release-target")

        let sink (chunk: string) =
            lock output (fun () ->
                output.Append(chunk) |> ignore

                if output.ToString().Contains("target-ready") && not (File.Exists marker) then
                    File.WriteAllText(marker, "release"))

        let caller =
            Task.Factory.StartNew(
                (fun () ->
                    use _ = ProcessRegistry.install registry

                    runProcessTo
                        (Some sink)
                        "/bin/sh"
                        "-c \"echo target-ready; i=0; while [ ! -f release-target ] && [ $i -lt 100 ]; do sleep 0.05; i=$((i+1)); done; [ -f release-target ] || exit 91; echo target-finished; exit 7\""
                        root
                        []
                        (ProcessBounds.silent (TimeSpan.FromSeconds 20.0))),
                CancellationToken.None,
                TaskCreationOptions.None,
                scheduler
            )

        let thread = scheduler.RunFirstOnOwnThread()

        try
            Assert.True(caller.Wait(TimeSpan.FromSeconds 20.0), "the caller did not settle")
            Assert.Empty(registry.Snapshot())
            Assert.Empty(registry.Leaks)

            match caller.Result with
            | Failed(7, ProcessOutput.Drained text) ->
                Assert.Contains("target-ready", text)
                Assert.Contains("target-finished", text)
            | other -> Assert.Fail $"expected target exit 7 with its complete output, got %A{other}"
        finally
            scheduler.Release()
            File.WriteAllText(marker, "release")
            registry.KillAll()
            Assert.True(thread.Join(TimeSpan.FromSeconds 10.0), "the caller thread did not exit"))
