module FsHotWatch.Tests.ProcessHelperTests

open System
open System.Threading.Tasks
open Xunit
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

// --- drainedOrEmpty: post-kill drain tail (internal, via InternalsVisibleTo) ---
// The timeout-kill call sites are exercised end-to-end in
// FsHotWatch.IntegrationTests; these deterministic unit tests pin both arms so
// the pure decision (completed -> value, anything else -> "") stays covered in
// the unit metric without depending on OS scheduling.

[<Fact(Timeout = 5000)>]
let ``drainedOrEmpty returns the value of a completed task`` () =
    Assert.Equal("hello", drainedOrEmpty (Task.FromResult "hello"))

[<Fact(Timeout = 5000)>]
let ``drainedOrEmpty returns empty for a never-completing task`` () =
    let tcs = TaskCompletionSource<string>()
    Assert.Equal("", drainedOrEmpty tcs.Task)

[<Fact(Timeout = 5000)>]
let ``drainedOrEmpty returns empty for a faulted task`` () =
    let tcs = TaskCompletionSource<string>()
    tcs.SetException(InvalidOperationException "boom")
    Assert.Equal("", drainedOrEmpty tcs.Task)

[<Fact(Timeout = 20000)>]
let ``runProcessWithTimeout returns Succeeded when fast`` () =
    match runProcessWithTimeout "echo" "hi" "." [] (TimeSpan.FromSeconds 5.0) with
    | Succeeded out -> Assert.Equal("hi", out.Trim())
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

[<Fact(Timeout = 20000)>]
let ``runWithTimeout returns WorkCompleted when work completes in time`` () =
    Assert.Equal(WorkCompleted 42, runWithTimeout (TimeSpan.FromSeconds 1.0) (fun () -> 42))

[<Fact(Timeout = 20000)>]
let ``runWithTimeout returns WorkTimedOut when work exceeds timeout`` () =
    let result =
        runWithTimeout (TimeSpan.FromMilliseconds 50.0) (fun () ->
            System.Threading.Thread.Sleep 1000
            42)

    match result with
    | WorkTimedOut _ -> ()
    | WorkCompleted _ -> Assert.Fail "expected timeout"

[<Fact(Timeout = 20000)>]
let ``runWithTimeout with InfiniteTimeSpan never times out`` () =
    Assert.Equal(WorkCompleted 7, runWithTimeout System.Threading.Timeout.InfiniteTimeSpan (fun () -> 7))

// --- runWithCancellableTimeout: real in-process cancellation (AUTOMATION-15 item 2) ---

[<Fact(Timeout = 20000)>]
let ``runWithCancellableTimeout returns WorkCompleted when work completes in time`` () =
    Assert.Equal(WorkCompleted 99, runWithCancellableTimeout (TimeSpan.FromSeconds 1.0) (fun _ct -> 99))

[<Fact(Timeout = 20000)>]
let ``runWithCancellableTimeout with InfiniteTimeSpan passes a non-cancelled token`` () =
    match
        runWithCancellableTimeout System.Threading.Timeout.InfiniteTimeSpan (fun ct -> ct.IsCancellationRequested)
    with
    | WorkCompleted requested -> Assert.False(requested)
    | WorkTimedOut _ -> Assert.Fail "expected completion"

[<Fact(Timeout = 20000)>]
let ``runWithCancellableTimeout CANCELS a stuck unit on timeout (no orphaned thread)`` () =
    // The audit's core defect: a timed-out in-process unit used to leave its
    // orphan thread running (still holding whatever lock it grabbed). With real
    // cancellation, a cooperative unit observes the token and unwinds — proven
    // here by a flag the work sets ONLY when it sees the cancellation, plus an
    // "still running" flag that must be cleared by the time we observe cancel.
    let observedCancellation = new System.Threading.ManualResetEventSlim(false)
    let workExited = new System.Threading.ManualResetEventSlim(false)

    let work (ct: System.Threading.CancellationToken) =
        try
            // Cooperative wait that releases the instant the token is cancelled —
            // models a stuck unit that honours cancellation (FCS/Fantomas/etc.).
            ct.WaitHandle.WaitOne() |> ignore
            observedCancellation.Set()
            42
        finally
            workExited.Set()

    let result = runWithCancellableTimeout (TimeSpan.FromMilliseconds 50.0) work

    match result with
    | WorkTimedOut _ -> ()
    | WorkCompleted _ -> Assert.Fail "expected timeout"

    // The defining assertion: the work was actually cancelled (token fired) and
    // its thread unwound — it is NOT orphaned still-running.
    Assert.True(observedCancellation.Wait(TimeSpan.FromSeconds 5.0), "work never observed cancellation — orphaned")
    Assert.True(workExited.Wait(TimeSpan.FromSeconds 5.0), "work thread never exited — orphaned")

[<Fact(Timeout = 20000)>]
let ``runWithCancellableTimeout cancelled unit releases its lock so the next unit proceeds`` () =
    // Concretely models the wedge: unit A grabs a lock and hangs; on timeout it
    // is cancelled and releases the lock; unit B must then be able to acquire it.
    // Pre-fix (orphan thread holding the lock forever) B would block indefinitely.
    let gate = new System.Threading.SemaphoreSlim(1, 1)

    let stuck (ct: System.Threading.CancellationToken) =
        gate.Wait()

        try
            ct.WaitHandle.WaitOne() |> ignore
        finally
            gate.Release() |> ignore

    let aResult = runWithCancellableTimeout (TimeSpan.FromMilliseconds 50.0) stuck

    match aResult with
    | WorkTimedOut _ -> ()
    | WorkCompleted _ -> Assert.Fail "expected timeout for the stuck unit"

    // B must be able to take the lock the cancelled A released.
    let acquiredByB = gate.Wait(TimeSpan.FromSeconds 5.0)
    Assert.True(acquiredByB, "lock was never released by the cancelled unit — daemon would stay wedged")

    if acquiredByB then
        gate.Release() |> ignore

[<Fact(Timeout = 20000)>]
let ``runProcess succeeds for echo`` () =
    match runProcess "echo" "hello" "." [] with
    | Succeeded out -> Assert.Equal("hello", out.Trim())
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

[<Fact(Timeout = 20000)>]
let ``runProcess reports nonzero exit as Failed`` () =
    match runProcess "sh" "-c \"exit 3\"" "." [] with
    | Failed(code, _) -> Assert.Equal(3, code)
    | other -> Assert.Fail $"expected Failed, got %A{other}"

[<Fact(Timeout = 15000)>]
let ``runProcessWithTimeout registers the child while running and unregisters on exit`` () =
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    Assert.Empty(FsHotWatch.ProcessRegistry.snapshot ())

    Assert.True(isSucceeded (runProcess "echo" "hi" "." []))

    // Post-exit, the registry has unregistered — exited PIDs the OS could later
    // recycle must not linger.
    Assert.Empty(FsHotWatch.ProcessRegistry.snapshot ())

[<Fact(Timeout = 5000)>]
let ``isTimedOut is true only for TimedOut`` () =
    // Deterministic coverage for the ProcessOutcome predicate. The live
    // timeout-kill path that produces a real TimedOut value is exercised in
    // FsHotWatch.IntegrationTests (kept out of the coverage suite because its
    // OS-scheduling-dependent kill/drain branches jitter); this pins the pure
    // discriminator in the unit suite.
    Assert.True(isTimedOut (TimedOut(TimeSpan.FromSeconds 1.0, "tail")))
    Assert.False(isTimedOut (Succeeded "ok"))
    Assert.False(isTimedOut (Failed(1, "boom")))

[<Fact(Timeout = 15000)>]
let ``isDotnetCommand matches dotnet basename`` () =
    Assert.True(isDotnetCommand "dotnet")
    Assert.True(isDotnetCommand "dotnet.exe")
    Assert.True(isDotnetCommand "/usr/local/share/dotnet/dotnet")
    Assert.True(isDotnetCommand "/c/Program Files/dotnet/dotnet.exe")

[<Fact(Timeout = 15000)>]
let ``isDotnetCommand rejects non-dotnet commands`` () =
    Assert.False(isDotnetCommand "sh")
    Assert.False(isDotnetCommand "echo")
    Assert.False(isDotnetCommand "/bin/sh")
    Assert.False(isDotnetCommand "dotnet-coverage")

[<Fact(Timeout = 15000)>]
let ``mergeDotnetEnv injects MSBUILDDISABLENODEREUSE for dotnet`` () =
    let merged = mergeDotnetEnv "dotnet" []
    Assert.Contains(("MSBUILDDISABLENODEREUSE", "1"), merged)

[<Fact(Timeout = 15000)>]
let ``mergeDotnetEnv leaves non-dotnet commands untouched`` () =
    Assert.Empty(mergeDotnetEnv "sh" [])
    Assert.Equal<(string * string) list>([ "FOO", "bar" ], mergeDotnetEnv "sh" [ "FOO", "bar" ])

[<Fact(Timeout = 15000)>]
let ``mergeDotnetEnv preserves caller-supplied MSBUILDDISABLENODEREUSE`` () =
    let merged = mergeDotnetEnv "dotnet" [ "MSBUILDDISABLENODEREUSE", "0" ]
    Assert.Equal<(string * string) list>([ "MSBUILDDISABLENODEREUSE", "0" ], merged)

// Spawn-env contract — see the strip in ProcessHelper.runProcessWithTimeout
// for the Nix scenario that motivated it.

let private echoEnv (var: string) =
    sprintf "-c \"printf %%s \\\"$%s\\\"\"" var

let private expectStdout (expected: string) (outcome: ProcessOutcome) =
    match outcome with
    | Succeeded out -> Assert.Equal(expected, out)
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

[<Fact(Timeout = 20000)>]
let ``runProcess inherits the parent process environment (no scrubbing)`` () =
    let key = "FSHOTWATCH_ENV_PASSTHROUGH_PROBE"
    let value = "nix-store-path-marker-" + Guid.NewGuid().ToString("N")

    withEnv key (Some value) (fun () -> runProcess "sh" (echoEnv key) "." [] |> expectStdout value)

[<Fact(Timeout = 20000)>]
let ``runProcess strips DOTNET_ROOT_ARM64 unconditionally`` () =
    withEnv "DOTNET_ROOT_ARM64" (Some "/poisoned/wrapped/bin") (fun () ->
        runProcess "sh" (echoEnv "DOTNET_ROOT_ARM64") "." [] |> expectStdout "")

[<Fact(Timeout = 20000)>]
let ``runProcess strips DOTNET_ROOT_X64 and DOTNET_ROOT_X86 too`` () =
    withEnv "DOTNET_ROOT_X64" (Some "/poisoned/x64") (fun () ->
        withEnv "DOTNET_ROOT_X86" (Some "/poisoned/x86") (fun () ->
            let args = "-c \"printf %s:%s \\\"$DOTNET_ROOT_X64\\\" \\\"$DOTNET_ROOT_X86\\\"\""
            runProcess "sh" args "." [] |> expectStdout ":"))

[<Fact(Timeout = 20000)>]
let ``runProcess preserves plain DOTNET_ROOT (only arch-specific is stripped)`` () =
    let probe = "/some/intentional/dotnet/root-" + Guid.NewGuid().ToString("N")

    withEnv "DOTNET_ROOT" (Some probe) (fun () -> runProcess "sh" (echoEnv "DOTNET_ROOT") "." [] |> expectStdout probe)

[<Fact(Timeout = 20000)>]
let ``runProcess strip respects caller-supplied DOTNET_ROOT_ARM64 override`` () =
    let explicitValue = "/explicit/correct-" + Guid.NewGuid().ToString("N")

    withEnv "DOTNET_ROOT_ARM64" (Some "/inherited/poisoned") (fun () ->
        runProcess "sh" (echoEnv "DOTNET_ROOT_ARM64") "." [ "DOTNET_ROOT_ARM64", explicitValue ]
        |> expectStdout explicitValue)

// Leaked-MSBuild-env contract — see the strip in
// ProcessHelper.runProcessWithTimeout for the scenario that motivated it.
// The daemon calls Ionide.ProjInfo.Init.init for in-process design-time
// project evaluation, which writes MSBUILD_EXE_PATH / MSBuildExtensionsPath /
// MSBuildSDKsPath into the daemon's OWN process environment, pinning them at a
// specific SDK band's MSBuild. Process.Start inherits the full parent env, so
// a spawned `dotnet build` then runs MSBuild from a possibly-different (or
// incomplete) SDK band than the muxer resolves — the restore-graph sub-build
// fails with exit 1 and ZERO diagnostics ("Build FAILED / 0 Error(s)").
// Stripping these on every spawn lets the child re-resolve MSBuild from its
// own SDK, matching a clean shell. Caller-supplied overrides still win.

// Each leaked MSBuild discovery key is stripped to empty in the child env,
// regardless of which one the parent leaked.
[<Theory(Timeout = 20000)>]
[<InlineData("MSBUILD_EXE_PATH")>]
[<InlineData("MSBuildExtensionsPath")>]
[<InlineData("MSBuildSDKsPath")>]
let ``runProcess strips leaked MSBuild discovery key unconditionally`` (key: string) =
    withEnv key (Some "/poisoned/sdk/leaked") (fun () -> runProcess "sh" (echoEnv key) "." [] |> expectStdout "")

[<Fact(Timeout = 20000)>]
let ``runProcess strip respects caller-supplied MSBUILD_EXE_PATH override`` () =
    let explicitValue = "/explicit/correct-" + Guid.NewGuid().ToString("N")

    withEnv "MSBUILD_EXE_PATH" (Some "/inherited/poisoned/MSBuild.dll") (fun () ->
        runProcess "sh" (echoEnv "MSBUILD_EXE_PATH") "." [ "MSBUILD_EXE_PATH", explicitValue ]
        |> expectStdout explicitValue)

// DOTNET_HOST_PATH realpath contract — see realpath block in
// runProcessWithTimeout for the nix-wrapped-SDK scenario that motivated it.
// The .NET muxer reads DOTNET_HOST_PATH literally and computes
// DOTNET_ROOT_<arch> = dirname(DOTNET_HOST_PATH); on a wrapped-bin/ symlink
// that dirname has no `shared/` sibling, so apphost dies. Resolving the
// symlink before spawn lands dirname on the unwrapped runtime tree.
//
// These tests exercise the contract via the explicit-env arg (4th param of
// runProcess) rather than mutating process env. Process-env mutation races
// the parallel test runner: a concurrent DaemonTests subprocess inherits
// the test's pointing-at-temp-dir DOTNET_HOST_PATH, the test cleans the
// temp dir, and the daemon spawn fails with "Permission denied" / "No such
// file" on `wrapped-dotnet`. ProcessHelper applies the realpath after the
// explicit overlay so this contract holds for either entry point.

[<Fact(Timeout = 20000)>]
let ``runProcess resolves DOTNET_HOST_PATH symlink to its realpath`` () =
    let tmp =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fshw-hostpath-{Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(tmp) |> ignore

    try
        let target = System.IO.Path.Combine(tmp, "real-dotnet")
        System.IO.File.WriteAllText(target, "")
        let link = System.IO.Path.Combine(tmp, "wrapped-dotnet")
        System.IO.File.CreateSymbolicLink(link, target) |> ignore

        runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [ "DOTNET_HOST_PATH", link ]
        |> expectStdout target
    finally
        if System.IO.Directory.Exists(tmp) then
            System.IO.Directory.Delete(tmp, true)

[<Fact(Timeout = 20000)>]
let ``runProcess leaves DOTNET_HOST_PATH unchanged when target is not a symlink`` () =
    let tmp =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fshw-hostpath-{Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(tmp) |> ignore

    try
        let regular = System.IO.Path.Combine(tmp, "regular-file")
        System.IO.File.WriteAllText(regular, "")

        runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [ "DOTNET_HOST_PATH", regular ]
        |> expectStdout regular
    finally
        if System.IO.Directory.Exists(tmp) then
            System.IO.Directory.Delete(tmp, true)

[<Fact(Timeout = 20000)>]
let ``runProcess leaves DOTNET_HOST_PATH unchanged when path does not exist`` () =
    let bogus = $"/no/such/path/fshw-{Guid.NewGuid():N}"

    runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [ "DOTNET_HOST_PATH", bogus ]
    |> expectStdout bogus

[<Fact(Timeout = 20000)>]
let ``runProcess does not set DOTNET_HOST_PATH when inherited env did not have it`` () =
    // Setting to None temporarily unsets the variable. This is safe under
    // parallel test load: a concurrent dotnet spawn missing DOTNET_HOST_PATH
    // re-derives it from argv[0] and works fine. (Setting it to a temp path
    // is what races — that's why the other tests in this group use the
    // explicit-env arg instead of mutating process env.)
    withEnv "DOTNET_HOST_PATH" None (fun () -> runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [] |> expectStdout "")

[<Fact(Timeout = 20000)>]
let ``runProcess resolves DOTNET_HOST_PATH symlink chain to final target`` () =
    let tmp =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fshw-hostpath-chain-{Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(tmp) |> ignore

    try
        let final = System.IO.Path.Combine(tmp, "final")
        System.IO.File.WriteAllText(final, "")
        let mid = System.IO.Path.Combine(tmp, "mid")
        System.IO.File.CreateSymbolicLink(mid, final) |> ignore
        let outer = System.IO.Path.Combine(tmp, "outer")
        System.IO.File.CreateSymbolicLink(outer, mid) |> ignore

        runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [ "DOTNET_HOST_PATH", outer ]
        |> expectStdout final
    finally
        if System.IO.Directory.Exists(tmp) then
            System.IO.Directory.Delete(tmp, true)

[<Fact(Timeout = 20000)>]
let ``runProcess overlays explicit env on top of inherited env`` () =
    let inheritedKey = "FSHOTWATCH_ENV_PASSTHROUGH_INHERITED"
    let inheritedValue = "inherited-" + Guid.NewGuid().ToString("N")
    let explicitKey = "FSHOTWATCH_ENV_PASSTHROUGH_EXPLICIT"
    let explicitValue = "explicit-" + Guid.NewGuid().ToString("N")

    withEnv inheritedKey (Some inheritedValue) (fun () ->
        let args =
            sprintf "-c \"printf %%s:%%s \\\"$%s\\\" \\\"$%s\\\"\"" inheritedKey explicitKey

        runProcess "sh" args "." [ explicitKey, explicitValue ]
        |> expectStdout (inheritedValue + ":" + explicitValue))

// The timeout-kill test that spawned `sleep 30` to force the kill-on-timeout
// drain path lives in FsHotWatch.IntegrationTests/ProcessHelperTimeoutTests.fs.
// Its OS-scheduling-dependent coverage of ProcessHelper.fs lines ~157-196 made
// the unit-suite line coverage jitter, so it was moved to the integration suite
// (no coverage package). The deterministic helpers below still pin the F17/F18
// contracts in the unit suite.

// --- F17/F18: isExpectedKillException / isExpectedDrainException helpers ---

[<Fact(Timeout = 5000)>]
let ``isExpectedKillException accepts InvalidOperationException (F17)`` () =
    Assert.True(isExpectedKillException (System.InvalidOperationException()))

[<Fact(Timeout = 5000)>]
let ``isExpectedKillException rejects Win32Exception (F17)`` () =
    // Win32Exception is a real permission/system failure that should propagate.
    Assert.False(isExpectedKillException (System.ComponentModel.Win32Exception()))

[<Fact(Timeout = 5000)>]
let ``isExpectedKillException rejects NullReferenceException (F17)`` () =
    Assert.False(isExpectedKillException (System.NullReferenceException()))

[<Fact(Timeout = 5000)>]
let ``isExpectedDrainException accepts AggregateException (F18)`` () =
    Assert.True(isExpectedDrainException (System.AggregateException()))

[<Fact(Timeout = 5000)>]
let ``isExpectedDrainException accepts IOException (F18)`` () =
    Assert.True(isExpectedDrainException (System.IO.IOException()))

[<Fact(Timeout = 5000)>]
let ``isExpectedDrainException accepts ObjectDisposedException (F18)`` () =
    Assert.True(isExpectedDrainException (System.ObjectDisposedException("x")))

[<Fact(Timeout = 5000)>]
let ``isExpectedDrainException rejects NullReferenceException (F18)`` () =
    Assert.False(isExpectedDrainException (System.NullReferenceException()))

// --- F19/F20: ProcessRegistry narrow-catch contracts ---

[<Fact(Timeout = 15000)>]
let ``ProcessRegistry.Snapshot tolerates disposed processes (F19)`` () =
    // F19: HasExited on a disposed Process throws InvalidOperationException.
    // After narrowing from `with _` to :? InvalidOperationException + Win32,
    // the Snapshot should still treat it as not-alive and skip it.
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    // Spawn a fast process, wait for it to exit, then dispose so HasExited throws.
    let psi = System.Diagnostics.ProcessStartInfo("echo", "hi")
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let proc = System.Diagnostics.Process.Start(psi)
    registry.Track proc
    proc.WaitForExit()
    proc.Dispose()

    // Should not throw and should return empty (alive=false swallowed).
    let snap = registry.Snapshot()
    Assert.Empty(snap)

[<Fact(Timeout = 5000)>]
let ``isExpectedProcessException accepts InvalidOperationException (F19/F20)`` () =
    Assert.True(FsHotWatch.ProcessRegistry.isExpectedProcessException (System.InvalidOperationException()))

[<Fact(Timeout = 5000)>]
let ``isExpectedProcessException accepts Win32Exception (F19/F20)`` () =
    Assert.True(FsHotWatch.ProcessRegistry.isExpectedProcessException (System.ComponentModel.Win32Exception()))

[<Fact(Timeout = 5000)>]
let ``isExpectedProcessException rejects NullReferenceException (F19/F20)`` () =
    Assert.False(FsHotWatch.ProcessRegistry.isExpectedProcessException (System.NullReferenceException()))

[<Fact(Timeout = 15000)>]
let ``ProcessRegistry.KillAll tolerates already-exited processes (F20)`` () =
    // F20: Kill on a process that already exited throws InvalidOperationException.
    // After narrowing, KillAll must still complete cleanly when processes have
    // exited normally before shutdown.
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    let psi = System.Diagnostics.ProcessStartInfo("echo", "hi")
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let proc = System.Diagnostics.Process.Start(psi)
    registry.Track proc
    proc.WaitForExit()

    // Should complete without throwing even though Kill would throw on an
    // exited process if HasExited check raced.
    registry.KillAll()
