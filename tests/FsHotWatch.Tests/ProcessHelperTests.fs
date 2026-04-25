module FsHotWatch.Tests.ProcessHelperTests

open System
open Xunit
open FsHotWatch.ProcessHelper

[<Fact(Timeout = 10000)>]
let ``runProcessWithTimeout kills child when exceeded`` () =
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let result =
        runProcessWithTimeout "sleep" "10" "." [] (TimeSpan.FromMilliseconds 200.0)

    sw.Stop()
    Assert.True(isTimedOut result)
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds 3.0, $"took {sw.Elapsed}")

[<Fact(Timeout = 10000)>]
let ``runProcessWithTimeout returns Succeeded when fast`` () =
    match runProcessWithTimeout "echo" "hi" "." [] (TimeSpan.FromSeconds 5.0) with
    | Succeeded out -> Assert.Equal("hi", out.Trim())
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

[<Fact(Timeout = 10000)>]
let ``runWithTimeout returns WorkCompleted when work completes in time`` () =
    Assert.Equal(WorkCompleted 42, runWithTimeout (TimeSpan.FromSeconds 1.0) (fun () -> 42))

[<Fact(Timeout = 10000)>]
let ``runWithTimeout returns WorkTimedOut when work exceeds timeout`` () =
    let result =
        runWithTimeout (TimeSpan.FromMilliseconds 50.0) (fun () ->
            System.Threading.Thread.Sleep 1000
            42)

    match result with
    | WorkTimedOut _ -> ()
    | WorkCompleted _ -> Assert.Fail "expected timeout"

[<Fact(Timeout = 10000)>]
let ``runWithTimeout with InfiniteTimeSpan never times out`` () =
    Assert.Equal(WorkCompleted 7, runWithTimeout System.Threading.Timeout.InfiniteTimeSpan (fun () -> 7))

[<Fact(Timeout = 10000)>]
let ``runProcess succeeds for echo`` () =
    match runProcess "echo" "hello" "." [] with
    | Succeeded out -> Assert.Equal("hello", out.Trim())
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

[<Fact(Timeout = 10000)>]
let ``runProcess reports nonzero exit as Failed`` () =
    match runProcess "sh" "-c \"exit 3\"" "." [] with
    | Failed(code, _) -> Assert.Equal(3, code)
    | other -> Assert.Fail $"expected Failed, got %A{other}"

[<Fact(Timeout = 10000)>]
let ``runProcessWithTimeout reports TimedOut on kill`` () =
    // The earlier variant asserted partial stdout reached the `tail` field, but
    // capturing pre-kill stdout races subprocess startup under load. The
    // contract worth pinning is just that we get the TimedOut tag — the tail is
    // best-effort drain.
    match runProcessWithTimeout "sh" "-c \"echo partial; sleep 10\"" "." [] (TimeSpan.FromMilliseconds 300.0) with
    | TimedOut _ -> ()
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``ProcessRegistry.killAll terminates tracked live processes`` () =
    // Per-scope registry isolates this test from concurrent ones — `install`
    // is AsyncLocal-scoped so killAll only affects this test's tracked PIDs.
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    FsHotWatch.Tests.TestHelpers.withTrackedSleep 60 (fun proc ->
        FsHotWatch.ProcessRegistry.track proc
        Assert.False(proc.HasExited)

        FsHotWatch.ProcessRegistry.killAll ()

        proc.WaitForExit(5000) |> ignore
        Assert.True(proc.HasExited))

[<Fact(Timeout = 15000)>]
let ``runProcessWithTimeout registers the child while running and unregisters on exit`` () =
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    Assert.Empty(FsHotWatch.ProcessRegistry.snapshot ())

    Assert.True(isSucceeded (runProcess "echo" "hi" "." []))

    // Post-exit, the registry has unregistered — exited PIDs the OS could later
    // recycle must not linger.
    Assert.Empty(FsHotWatch.ProcessRegistry.snapshot ())

[<Fact(Timeout = 15000)>]
let ``ProcessRegistry.killAll kills a child started via runProcessWithTimeout from another thread`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    // The Task captures the AsyncLocal context at start, so the spawned child
    // registers against this test's registry — not a process-wide global.
    let task =
        System.Threading.Tasks.Task.Run(fun () ->
            runProcessWithTimeout "sleep" "30" "." [] System.Threading.Timeout.InfiniteTimeSpan)

    // Wait for the child to register (Process.Start is fast; track is the next line).
    // 8s deadline tolerates parallel-test thread-pool contention — a Task.Run body
    // sometimes doesn't reach Process.Start for several seconds under load.
    let deadline = DateTime.UtcNow.AddSeconds 8.0

    while registry.Snapshot().IsEmpty && DateTime.UtcNow < deadline do
        System.Threading.Thread.Sleep 25

    Assert.NotEmpty(registry.Snapshot())

    registry.KillAll()

    let completed = task.Wait(5000)
    Assert.True(completed, "runProcessWithTimeout did not return after killAll")

[<Fact(Timeout = 5000)>]
let ``isDotnetCommand matches dotnet basename`` () =
    Assert.True(isDotnetCommand "dotnet")
    Assert.True(isDotnetCommand "dotnet.exe")
    Assert.True(isDotnetCommand "/usr/local/share/dotnet/dotnet")
    Assert.True(isDotnetCommand "/c/Program Files/dotnet/dotnet.exe")

[<Fact(Timeout = 5000)>]
let ``isDotnetCommand rejects non-dotnet commands`` () =
    Assert.False(isDotnetCommand "sh")
    Assert.False(isDotnetCommand "echo")
    Assert.False(isDotnetCommand "/bin/sh")
    Assert.False(isDotnetCommand "dotnet-coverage")

[<Fact(Timeout = 5000)>]
let ``mergeDotnetEnv injects MSBUILDDISABLENODEREUSE for dotnet`` () =
    let merged = mergeDotnetEnv "dotnet" []
    Assert.Contains(("MSBUILDDISABLENODEREUSE", "1"), merged)

[<Fact(Timeout = 5000)>]
let ``mergeDotnetEnv leaves non-dotnet commands untouched`` () =
    Assert.Empty(mergeDotnetEnv "sh" [])
    Assert.Equal<(string * string) list>([ "FOO", "bar" ], mergeDotnetEnv "sh" [ "FOO", "bar" ])

[<Fact(Timeout = 5000)>]
let ``mergeDotnetEnv preserves caller-supplied MSBUILDDISABLENODEREUSE`` () =
    let merged = mergeDotnetEnv "dotnet" [ "MSBUILDDISABLENODEREUSE", "0" ]
    Assert.Equal<(string * string) list>([ "MSBUILDDISABLENODEREUSE", "0" ], merged)
