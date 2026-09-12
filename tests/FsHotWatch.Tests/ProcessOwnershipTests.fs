module FsHotWatch.Tests.ProcessOwnershipTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.ProcessOwnership

let private hostPath =
    Path.Combine(
        Path.GetDirectoryName(typeof<ProcessHelper.ProcessBounds>.Assembly.Location),
        "fshw-process-host",
        "FsHotWatch.ProcessHost.dll"
    )

let private target () =
    ProcessStartInfo("/bin/sh", "-c \"exit 0\"", UseShellExecute = false)

let private noAdmission = Action<OwnedChild>(ignore)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``ownership rejects shell execution and argument lists before spawning`` shell =
    let start = target ()
    start.UseShellExecute <- shell

    if not shell then
        start.ArgumentList.Add "ambiguous"

    Assert.Throws<ArgumentException>(fun () -> OwnedChild.Start(start, hostPath, noAdmission) |> ignore)
    |> ignore

[<Fact>]
let ``missing packaged helper is a named startup failure`` () =
    let missing =
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll")

    let error =
        Assert.Throws<FileNotFoundException>(fun () -> OwnedChild.Start(target (), missing, noAdmission) |> ignore)

    Assert.Equal(missing, error.FileName)

[<Fact>]
let ``ownership requires target and admission callback`` () =
    Assert.Throws<ArgumentNullException>(fun () -> OwnedChild.Start(null, hostPath, noAdmission) |> ignore)
    |> ignore

    Assert.Throws<ArgumentNullException>(fun () -> OwnedChild.Start(target (), hostPath, null) |> ignore)
    |> ignore

[<Theory(Timeout = 15000)>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``admission revocation cannot execute target and retains original refusal`` revoke =
    let directory = Directory.CreateTempSubdirectory("fshw-admission-")
    let marker = Path.Combine(directory.FullName, "executed")

    let start =
        ProcessStartInfo(
            "/bin/sh",
            "-c \"echo target > executed\"",
            UseShellExecute = false,
            WorkingDirectory = directory.FullName
        )

    let mutable captured = None

    let admission =
        Action<OwnedChild>(fun child ->
            captured <- Some child

            if revoke then
                child.Terminate()
            else
                raise (InvalidOperationException("admission refused by test")))

    try
        let error =
            Record.Exception(fun () -> OwnedChild.Start(start, hostPath, admission) |> ignore)

        Assert.NotNull error

        if revoke then
            Assert.IsType<OperationCanceledException>(error) |> ignore
        else
            Assert.Equal("admission refused by test", error.Message)

        Assert.False(File.Exists marker, "target must never run before successful admission")
        let child = captured.Value
        Assert.True(child.Process.HasExited, "failed admission must finish helper cleanup")
        child.Terminate()
        child.Dispose()
        child.Dispose()
    finally
        captured
        |> Option.iter (fun child ->
            try
                child.Terminate()
                child.Dispose()
            with :? ObjectDisposedException ->
                ())

        directory.Delete(true)

[<Fact(Timeout = 15000)>]
let ``ownership cannot be disposed or claim target exit before an admitted target finishes`` () =
    let start = ProcessStartInfo("/bin/sleep", "30", UseShellExecute = false)
    let child = OwnedChild.Start(start, hostPath, noAdmission)

    try
        Assert.Throws<InvalidOperationException>(fun () -> child.Dispose()) |> ignore
        Assert.False(child.Process.HasExited)
        // This identity belongs to the live helper created above, not a historical PID.
        use ready =
            JsonDocument.Parse(sprintf "{\"processGroup\":%d,\"jobName\":null}" child.Process.Id)

        use boundary =
            Containment.Open(ready.RootElement, child.Process.Id, "unused-on-unix")

        Assert.False(boundary.IsEmpty(), "the admitted helper and target still own the group")
        child.Terminate()
        Assert.True(boundary.IsEmpty(), "verified termination must leave the owned group empty")
        Assert.True(child.Process.HasExited)
        // Cancellation supplies no target receipt. Helper cleanup is not a target success.
        Assert.ThrowsAny<IOException>(fun () -> child.TargetExitCode |> ignore)
        |> ignore
    finally
        child.Terminate()
        child.Dispose()

    Assert.Throws<ObjectDisposedException>(fun () -> child.Terminate()) |> ignore

    Assert.Throws<ObjectDisposedException>(fun () -> child.TargetExitCode |> ignore)
    |> ignore

[<Fact(Timeout = 15000)>]
let ``closed process scope refuses launch before target side effects`` () =
    let directory = Directory.CreateTempSubdirectory("fshw-closed-admission-")
    let registry = ProcessRegistry.Registry()
    use scope = ProcessRegistry.install registry
    registry.KillAll()

    try
        Assert.Throws<OperationCanceledException>(fun () ->
            ProcessHelper.runProcess
                "/bin/sh"
                "-c \"echo target > executed\""
                directory.FullName
                []
                (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 5.))
            |> ignore)
        |> ignore

        Assert.False(File.Exists(Path.Combine(directory.FullName, "executed")))
        Assert.Empty(registry.Snapshot())
        Assert.Empty registry.Leaks
    finally
        directory.Delete(true)

[<Theory>]
[<InlineData(0, "null")>]
[<InlineData(1, "null")>]
[<InlineData(3, "null")>]
[<InlineData(2, "\"unexpected-job\"")>]
let ``Unix containment refuses a foreign or unsafe identity`` group job =
    use message =
        JsonDocument.Parse(sprintf "{\"processGroup\":%d,\"jobName\":%s}" group job)

    Assert.Throws<IOException>(fun () -> Containment.Open(message.RootElement, 2, "expected") |> ignore)
    |> ignore

[<Fact>]
let ``registry uses owned capability even when leader already exited`` () =
    use leader =
        Process.Start(ProcessStartInfo("/bin/sh", "-c \"exit 0\"", UseShellExecute = false))

    Assert.True(leader.WaitForExit(5000))
    let parent = ProcessRegistry.Registry()
    let registry = ProcessRegistry.Registry(parent)
    let mutable terminated = 0
    registry.Track(leader, terminateOwned = (fun () -> Interlocked.Increment(&terminated) |> ignore))
    Assert.Single(registry.Snapshot()) |> ignore
    Assert.Single(parent.Snapshot()) |> ignore
    registry.KillAll()
    Assert.Equal(1, terminated)
    Assert.Empty(parent.Snapshot())
    Assert.Empty(registry.Leaks)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``failed ownership capability stays an explicit leak even after leader exit`` hasParent =
    use leader =
        Process.Start(ProcessStartInfo("/bin/sh", "-c \"exit 0\"", UseShellExecute = false))

    Assert.True(leader.WaitForExit(5000))
    let parent = ProcessRegistry.Registry()

    let registry =
        if hasParent then
            ProcessRegistry.Registry(parent)
        else
            ProcessRegistry.Registry()

    registry.Track(leader, terminateOwned = (fun () -> raise (IOException("retained boundary refused termination"))))
    registry.KillAll()
    let leak = Assert.Single registry.Leaks
    Assert.Equal(leader.Id, leak.Pid)
    Assert.Contains("retained boundary refused termination", leak.Reason)

    if hasParent then
        Assert.Single(parent.Leaks) |> ignore
    else
        Assert.Empty(parent.Leaks)

    registry.KillAll()
    Assert.Single(registry.Leaks) |> ignore

[<Fact(Timeout = 15000)>]
let ``registry deadline reports uncertainty and observes late capability failure`` () =
    use leader =
        Process.Start(ProcessStartInfo("/bin/sh", "-c \"exit 0\"", UseShellExecute = false))

    Assert.True(leader.WaitForExit(5000))
    use entered = new ManualResetEventSlim()
    use release = new ManualResetEventSlim()
    use finished = new ManualResetEventSlim()
    let registry = ProcessRegistry.Registry()

    registry.Track(
        leader,
        terminateOwned =
            fun () ->
                try
                    entered.Set()

                    if not (release.Wait(TimeSpan.FromSeconds 10.)) then
                        failwith "test failed to release capability"

                    raise (IOException("late termination failure"))
                finally
                    finished.Set()
    )

    let shutdown = Task.Run(fun () -> registry.KillAll())

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 2.))
        Assert.True(shutdown.Wait(TimeSpan.FromSeconds 7.), "shutdown must return when ownership stays uncertain")
        let leak = Assert.Single registry.Leaks
        Assert.Contains("exceeded", leak.Reason)
    finally
        release.Set()
        Assert.True(finished.Wait(TimeSpan.FromSeconds 2.))

[<Fact>]
let ``disposed raw handle cannot become a successful teardown witness`` () =
    let leader =
        Process.Start(ProcessStartInfo("/bin/sh", "-c \"exit 0\"", UseShellExecute = false))

    Assert.True(leader.WaitForExit(5000))
    let registry = ProcessRegistry.Registry()
    registry.Track leader
    leader.Dispose()
    registry.KillAll()
    Assert.Single(registry.Leaks) |> ignore

[<Fact>]
let ``synchronous child scope settles and restores its parent registry`` () =
    let parent = ProcessRegistry.Registry()
    use installed = ProcessRegistry.install parent

    let result =
        ProcessRegistry.withChildScope CancellationToken.None (fun settle ->
            settle ()
            42)

    Assert.Equal(42, result)
    ProcessRegistry.killAll ()
    Assert.Empty(ProcessRegistry.snapshot ())

[<Fact>]
let ``detached helper does not intercept ordinary or malformed CLI arguments`` () =
    for args in
        [ [||]
          [| "status" |]
          [| "--internal-detached-launch" |]
          [| "other"; "exit 0" |]
          [| "--internal-detached-launch"; "exit 0"; "extra" |] ] do
        Assert.Equal(None, FsHotWatch.Cli.DetachedLaunch.tryRun args)

[<Fact(Timeout = 15000)>]
let ``detached launch reports original failed command`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FsHotWatch.Cli.DetachedLaunch.launch (Path.GetTempPath()) "exit 7")

    Assert.Contains("exited 7", error.Message)

[<Fact(Timeout = 15000)>]
let ``detached launch bounds and reaps a stuck helper`` () =
    let error =
        Assert.Throws<TimeoutException>(fun () -> FsHotWatch.Cli.DetachedLaunch.launch (Path.GetTempPath()) "sleep 30")

    Assert.Contains("exceeded five seconds", error.Message)


[<Theory>]
[<InlineData("", "without the required receipt")>]
[<InlineData("{\"kind\":\"ready\"}\n", "Unexpected process host protocol")>]
[<InlineData("{\"kind\":\"error\",\"message\":\"target failed\"}\n", "target failed")>]
[<InlineData("{\"kind\":\"exit\",\"exitCode\":7}\nextra\n", "Unexpected data after")>]
let ``process receipt protocol refuses missing malformed and duplicate terminal records``
    (payload: string)
    (expected: string)
    =
    use bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes payload)
    use reader = new StreamReader(bytes)

    let error =
        Assert.Throws<IOException>(fun () -> ChildProtocol.receipt(reader).GetAwaiter().GetResult() |> ignore)

    Assert.Contains(expected, error.Message)

[<Fact>]
let ``process receipt preserves original nonzero exit independently from helper status`` () =
    use bytes =
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes "{\"kind\":\"exit\",\"exitCode\":7}\n")

    use reader = new StreamReader(bytes)
    Assert.Equal(7, ChildProtocol.receipt(reader).GetAwaiter().GetResult())

[<Fact(Timeout = 15000)>]
let ``ownership retry never waits forever behind an outstanding cleanup`` () =
    let gate = obj ()
    use entered = new ManualResetEventSlim()
    use release = new ManualResetEventSlim()

    let holder =
        Task.Run(fun () ->
            lock gate (fun () ->
                entered.Set()
                Assert.True(release.Wait(TimeSpan.FromSeconds 10.))))

    try
        Assert.True(entered.Wait(TimeSpan.FromSeconds 2.))

        let error =
            Assert.Throws<TimeoutException>(fun () -> ChildProtocol.withLock gate ignore)

        Assert.Contains("cleanup remains unconfirmed", error.Message)
    finally
        release.Set()
        Assert.True(holder.Wait(TimeSpan.FromSeconds 2.))

    Assert.Equal(42, ChildProtocol.withLock gate (fun () -> 42))

[<Fact>]
let ``ownership monitor is released when protected operation throws`` () =
    let gate = obj ()

    Assert.Throws<IOException>(fun () -> ChildProtocol.withLock gate (fun () -> raise (IOException("original")): unit))
    |> ignore

    Assert.Equal(42, ChildProtocol.withLock gate (fun () -> 42))

[<Fact(Timeout = 15000)>]
let ``missing working directory preserves startup failure without admitting a process`` () =
    let missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let registry = ProcessRegistry.Registry()
    use scope = ProcessRegistry.install registry

    let error =
        Assert.Throws<System.ComponentModel.Win32Exception>(fun () ->
            ProcessHelper.runProcess
                "/bin/sh"
                "-c \"exit 0\""
                missing
                []
                (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 5.))
            |> ignore)

    Assert.Contains(missing, error.Message)
    Assert.Empty(registry.Snapshot())
    Assert.Empty(registry.Leaks)

[<Fact(Timeout = 15000)>]
let ``real admitted target retains its output and exit separately from containment helper`` () =
    let start =
        ProcessStartInfo(
            "/bin/sh",
            "-c \"printf original-output; exit 7\"",
            UseShellExecute = false,
            RedirectStandardOutput = true
        )

    let child = OwnedChild.Start(start, hostPath, noAdmission)

    try
        Assert.Equal(7, child.TargetExitCode)
        Assert.Equal(137, child.Process.ExitCode)
        Assert.Equal("original-output", child.Process.StandardOutput.ReadToEnd())
    finally
        child.Terminate()
        child.Dispose()

[<Fact(Timeout = 15000)>]
let ``invalid helper image cannot admit target and startup remains bounded`` () =
    let directory = Directory.CreateTempSubdirectory("fshw-invalid-host-")
    let image = Path.Combine(directory.FullName, "invalid.dll")
    File.WriteAllText(image, "This is not a managed process host.")
    let mutable admitted = false
    let clock = Stopwatch.StartNew()

    try
        Assert.ThrowsAny<OperationCanceledException>(fun () ->
            OwnedChild.Start(target (), image, Action<OwnedChild>(fun _ -> admitted <- true))
            |> ignore)
        |> ignore

        Assert.False(admitted, "a helper without a ready receipt cannot release the target")
        Assert.InRange(clock.Elapsed.TotalSeconds, 4., 12.)
    finally
        directory.Delete(true)

[<Fact(Timeout = 15000)>]
let ``containment wait spends its existing deadline while retaining live ownership`` () =
    let child =
        OwnedChild.Start(ProcessStartInfo("/bin/sleep", "30", UseShellExecute = false), hostPath, noAdmission)

    try
        use ready =
            JsonDocument.Parse(sprintf "{\"processGroup\":%d,\"jobName\":null}" child.Process.Id)

        use boundary =
            Containment.Open(ready.RootElement, child.Process.Id, "unused-on-unix")

        let elapsed = Stopwatch.StartNew()

        let error =
            Assert.Throws<TimeoutException>(fun () -> ChildProtocol.waitForContainment boundary elapsed)

        Assert.Contains("within five seconds", error.Message)
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 4., 10.)
        Assert.False(boundary.IsEmpty(), "waiting cannot turn a live group into positive cleanup evidence")
        child.Terminate()
        Assert.True(boundary.IsEmpty())
    finally
        child.Terminate()
        child.Dispose()

[<Fact(Timeout = 15000)>]
let ``child scope refuses retirement when retained ownership reports uncertainty`` () =
    let parent = ProcessRegistry.Registry()
    use installed = ProcessRegistry.install parent

    use leader =
        Process.Start(ProcessStartInfo("/bin/sh", "-c \"exit 0\"", UseShellExecute = false))

    Assert.True(leader.WaitForExit(5000))

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ProcessRegistry.withChildScope CancellationToken.None (fun settle ->
                ProcessRegistry.trackOwned leader (fun () -> raise (IOException("owned descendants unconfirmed")))
                settle ()))

    Assert.Contains("could not establish termination", error.Message)
    Assert.Contains("owned descendants unconfirmed", (Assert.Single parent.Leaks).Reason)
    ProcessRegistry.reportLeak 0 "restored parent" "post-scope marker"
    Assert.Contains(parent.Leaks, fun leak -> leak.Description = "restored parent")

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``uncertain cleanup preserves failures and keeps ownership retained`` operationFailed =
    let primary = IOException("original operation failed")
    let cleanup = TimeoutException("native containment unconfirmed")
    let mutable reported = None
    let mutable released = false

    let error =
        Record.Exception(fun () ->
            ProcessHelper.settleOwnedProcess
                (if operationFailed then Some primary else None)
                (fun () -> raise cleanup)
                (fun failure -> reported <- Some failure)
                (fun () -> released <- true))

    Assert.False(released, "uncertain containment must retain its stable ownership capability")
    Assert.Same(cleanup, reported.Value)

    if operationFailed then
        let combined = Assert.IsType<AggregateException> error
        Assert.Equal(2, combined.InnerExceptions.Count)
        Assert.Same(primary, combined.InnerExceptions[0])
        Assert.Same(cleanup, combined.InnerExceptions[1])
    else
        Assert.Same(cleanup, error)

[<Fact>]
let ``verified cleanup releases ownership in order without reporting a leak`` () =
    let events = ResizeArray<string>()

    ProcessHelper.settleOwnedProcess None (fun () -> events.Add "terminated") (fun _ -> events.Add "leak") (fun () ->
        events.Add "released")

    Assert.Equal<string list>([ "terminated"; "released" ], List.ofSeq events)

[<Fact>]
let ``successful cleanup leaves the original operation exception intact`` () =
    let primary = IOException("original operation failed")
    let mutable released = false
    let mutable pending = None

    let error =
        Record.Exception(fun () ->
            try
                try
                    (raise primary: unit)
                with failure ->
                    pending <- Some failure
                    reraise ()
            finally
                ProcessHelper.settleOwnedProcess pending ignore ignore (fun () -> released <- true))

    Assert.Same(primary, error)
    Assert.True(released)

[<Fact>]
let ``detached session failure never attempts exec and retains native errno`` () =
    let mutable execAttempted = false
    let messages = ResizeArray<string>()

    let result =
        FsHotWatch.Cli.DetachedLaunch.runHelperWith
            (fun () -> -1)
            (fun _ ->
                execAttempted <- true
                -1)
            (fun () -> 13)
            messages.Add
            "must not execute"

    Assert.Equal(1, result)
    Assert.False(execAttempted)
    Assert.Contains("setsid failed (errno 13)", Assert.Single messages)

[<Fact>]
let ``detached exec receives complete null terminated UTF8 shell arguments and reports failure`` () =
    let command = "printf 'héllo 🌍'"
    let messages = ResizeArray<string>()
    let mutable inspected = false

    let inspect (path: string, argv: nativeint) =
        Assert.Equal("/bin/sh", path)

        let read index =
            let pointer =
                System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, index * IntPtr.Size)

            System.Runtime.InteropServices.Marshal.PtrToStringUTF8 pointer

        Assert.Equal("/bin/sh", read 0)
        Assert.Equal("-c", read 1)
        Assert.Equal(command, read 2)
        Assert.Equal(IntPtr.Zero, System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, 3 * IntPtr.Size))
        inspected <- true
        -1

    let result =
        FsHotWatch.Cli.DetachedLaunch.runHelperWith (fun () -> 123) inspect (fun () -> 2) messages.Add command

    Assert.True(inspected)
    Assert.Equal(1, result)
    Assert.Contains("execv failed (errno 2)", Assert.Single messages)

[<Fact(Timeout = 15000)>]
let ``disabled output sink is never retried across multiple read buffers`` () =
    let mutable attempts = 0

    let sink _ =
        attempts <- attempts + 1
        raise (IOException("streamed log is unavailable"))

    let outcome =
        ProcessHelper.runProcessTo
            (Some sink)
            "/bin/sh"
            "-c \"i=0; while [ $i -lt 10000 ]; do printf x; i=$((i+1)); done\""
            "."
            []
            (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 10.))

    Assert.Equal(1, attempts)

    match outcome with
    | ProcessHelper.Succeeded(ProcessHelper.ProcessOutput.Drained output) -> Assert.Equal(String('x', 10000), output)
    | other -> failwithf "Expected complete successful capture despite disabled sink, got %A" other

[<Fact>]
let ``failed boundary cleanup preserves original and teardown causes in order`` () =
    let original = IOException("original admission failed")
    let teardown = TimeoutException("cleanup unconfirmed")
    let mutable invoked = false

    let error =
        Assert.Throws<AggregateException>(fun () ->
            ChildProtocol.cleanupAfterFailure "admission context" original (fun () ->
                invoked <- true
                raise teardown))

    Assert.True(invoked)
    Assert.StartsWith("admission context", error.Message)
    Assert.Equal(2, error.InnerExceptions.Count)
    Assert.Same(original, error.InnerExceptions[0])
    Assert.Same(teardown, error.InnerExceptions[1])

[<Fact>]
let ``successful boundary cleanup leaves original error propagation with caller`` () =
    let original = IOException("original startup failed")
    let mutable cleaned = false

    let error =
        Record.Exception(fun () ->
            try
                (raise original: unit)
            with failure ->
                ChildProtocol.cleanupAfterFailure "unused failure context" failure (fun () -> cleaned <- true)
                reraise ())

    Assert.True(cleaned)
    Assert.Same(original, error)

[<Fact>]
let ``ownership failure diagnostics omit message and payload while retaining original stack`` () =
    let original =
        InvalidOperationException("secret-argument secret-environment secret-payload")

    original.Data["command"] <- "secret-command"
    let mutable diagnostic = ""
    let mutable throwingStack = ""

    let observed =
        Assert.Throws<InvalidOperationException>(
            Action(fun () ->
                try
                    raise original
                with error ->
                    throwingStack <- error.StackTrace

                    ChildProtocol.reportFailureWith
                        (fun text -> diagnostic <- text)
                        "exit-receipt"
                        5000L
                        123
                        "WaitingForActivation"
                        "not-applicable"
                        error

                    reraise ())
        )

    Assert.Same(original, observed)
    Assert.Contains("phase=exit-receipt", diagnostic)
    Assert.Contains("elapsedMs=5000", diagnostic)
    Assert.Contains("helperPid=123", diagnostic)
    Assert.Contains("receipt=WaitingForActivation", diagnostic)
    Assert.Contains("exceptionType=System.InvalidOperationException", diagnostic)
    Assert.False(String.IsNullOrEmpty throwingStack)
    Assert.Contains(throwingStack, diagnostic)
    Assert.DoesNotContain("secret-", diagnostic)

[<Fact>]
let ``ownership diagnostic writer failure cannot replace the original cancellation`` () =
    let original = OperationCanceledException("sensitive cancellation context")

    let observed =
        Assert.Throws<OperationCanceledException>(
            Action(fun () ->
                try
                    raise original
                with error ->
                    ChildProtocol.reportFailureWith
                        (fun _ -> raise (IOException("diagnostic sink failed")))
                        "connect"
                        5000L
                        123
                        "not-observed"
                        "True"
                        error

                    reraise ())
        )

    Assert.Same(original, observed)
