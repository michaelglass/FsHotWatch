module FsHotWatch.Tests.ProcessHelperTests

open System
open System.Threading.Tasks
open Xunit
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

/// Bounds for the spawn tests that care about something OTHER than the bounds
/// (the child-env contract, the process registry): every one of them runs a
/// trivial `sh -c echo` that exits in milliseconds, so a plain finite timeout is
/// all they need. Named so a reader is never in doubt that the bounds are
/// incidental here — the bound-specific behaviour has its own tests below.
let private quick = ProcessBounds.silent (TimeSpan.FromSeconds 30.0)

/// Shadows `ProcessHelper.runProcess` with the 4-arg form those tests want.
let private runProcess command args workDir env =
    FsHotWatch.ProcessHelper.runProcess command args workDir env quick

/// The bound-specific tests: same spawn, explicit bounds, no env / workDir noise.
let private runProcessBounded command args bounds =
    FsHotWatch.ProcessHelper.runProcess command args "." [] bounds

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
let ``runProcess returns Succeeded when fast`` () =
    match runProcess "echo" "hi" "." [] with
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
let ``runProcess registers the child while running and unregisters on exit`` () =
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

// --- Launch-liveness watchdog (AUTOMATION-65 QA finding: the launch gap) ---
// The pure decision + the injectable loop are pinned deterministically here
// (no real process, no OS scheduling) — mirroring how AUTOMATION-66 tested
// `waitForDaemonReadyWith`. The real-process arms below are fast (sub-second)
// and self-contained (no global state).

// decideLaunchStep: (launchDeadlineReached, overallTimeoutReached, exited, sawOutput)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: exited wins over everything`` () =
    // Even racing the launch deadline with no output, a process that EXITED is a
    // natural completion to classify — never a stall.
    Assert.Equal(LaunchStep.Exited, decideLaunchStep true false true false)
    Assert.Equal(LaunchStep.Exited, decideLaunchStep false false true true)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: no life within the launch deadline is a stall`` () =
    // never-appears: not exited, no output, past the launch deadline.
    Assert.Equal(LaunchStep.Stalled, decideLaunchStep true false false false)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: a process that produced output is never launch-killed`` () =
    // slow-but-alive: streaming output past the launch deadline → keep waiting,
    // NOT Stalled. The launch deadline governs launch, not total duration.
    Assert.Equal(LaunchStep.KeepWaiting, decideLaunchStep true false false true)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: overall timeout ends even a progressing run`` () =
    // A hard cap the caller asked for still fires while progressing.
    Assert.Equal(LaunchStep.TimedOut, decideLaunchStep false true false true)
    // ...but a natural exit still wins over the overall timeout.
    Assert.Equal(LaunchStep.Exited, decideLaunchStep false true true false)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: still within the launch window keeps waiting`` () =
    Assert.Equal(LaunchStep.KeepWaiting, decideLaunchStep false false false false)

// resolveLaunchDeadline: override precedence (pure — no process env touched)

[<Fact(Timeout = 5000)>]
let ``resolveLaunchDeadline: absent override falls back to the default`` () =
    Assert.Equal(DefaultLaunchDeadline, resolveLaunchDeadline None)

[<Fact(Timeout = 5000)>]
let ``resolveLaunchDeadline: a positive integer override wins`` () =
    Assert.Equal(TimeSpan.FromSeconds 42.0, resolveLaunchDeadline (Some "42"))

[<Theory(Timeout = 5000)>]
[<InlineData("0")>]
[<InlineData("-5")>]
[<InlineData("not-a-number")>]
[<InlineData("")>]
let ``resolveLaunchDeadline: junk / non-positive override falls back to the default`` (value: string) =
    Assert.Equal(DefaultLaunchDeadline, resolveLaunchDeadline (Some value))

// launchWatchdogLoopWith: injected observe/clock/sleep — deterministic, no process.
// The fake clock advances by the slept ms on each poll so the deadline is
// reached after a bounded number of iterations.

let private fakeClock (start: DateTime) =
    let current = ref start
    let now () = current.Value

    let advance (ms: int) =
        current.Value <- current.Value.AddMilliseconds(float ms)

    now, advance

[<Fact(Timeout = 5000)>]
let ``launchWatchdogLoopWith: a child that never appears stalls at the launch deadline`` () =
    // observe always reports "not exited, no output" — the overloaded-spawn case.
    let now, advance = fakeClock DateTime.UtcNow

    let step =
        launchWatchdogLoopWith
            (fun () -> false, false)
            now
            advance
            250
            (TimeSpan.FromSeconds 1.0)
            System.Threading.Timeout.InfiniteTimeSpan

    Assert.Equal(LaunchOutcome.Stalled, step)

[<Fact(Timeout = 5000)>]
let ``launchWatchdogLoopWith: a child that dies silently settles as Exited`` () =
    // No output, then it's gone — the wrapper turns this into a launch-death
    // (raise). The loop's job is just to observe the exit promptly.
    let now, advance = fakeClock DateTime.UtcNow
    let calls = ref 0

    let observe () =
        incr calls
        if calls.Value >= 3 then true, false else false, false

    let step =
        launchWatchdogLoopWith
            observe
            now
            advance
            250
            (TimeSpan.FromSeconds 10.0)
            System.Threading.Timeout.InfiniteTimeSpan

    Assert.Equal(LaunchOutcome.Exited, step)

[<Fact(Timeout = 5000)>]
let ``launchWatchdogLoopWith: a slow-but-progressing run is NOT launch-killed`` () =
    // Streams output for far longer than the launch deadline, then exits. The
    // launch deadline must NOT trip — the result is Exited, never Stalled.
    let now, advance = fakeClock DateTime.UtcNow
    let calls = ref 0

    let observe () =
        incr calls
        // 20 polls of "alive, streaming output" (well past the 1 s launch
        // deadline at 250 ms/poll = ~5 s), then a natural exit.
        if calls.Value >= 20 then true, true else false, true

    let step =
        launchWatchdogLoopWith
            observe
            now
            advance
            250
            (TimeSpan.FromSeconds 1.0)
            System.Threading.Timeout.InfiniteTimeSpan

    Assert.Equal(LaunchOutcome.Exited, step)

[<Fact(Timeout = 5000)>]
let ``launchWatchdogLoopWith: overall timeout ends a progressing run`` () =
    let now, advance = fakeClock DateTime.UtcNow

    let step =
        launchWatchdogLoopWith
            (fun () -> false, true) // always alive & progressing, never exits
            now
            advance
            250
            (TimeSpan.FromSeconds 10.0) // launch deadline never relevant (has output)
            (TimeSpan.FromSeconds 1.0) // overall cap fires

    Assert.Equal(LaunchOutcome.TimedOut, step)

// runProcess under `ProcessBounds.streaming`: fast real-process arms (no global state).

[<Fact(Timeout = 20000)>]
let ``runProcess streaming: a fast normal process returns Succeeded`` () =
    // A progressing, quickly-exiting process is never launch-killed.
    match
        runProcessBounded
            "echo"
            "hi"
            (ProcessBounds.streaming System.Threading.Timeout.InfiniteTimeSpan (TimeSpan.FromSeconds 5.0))
    with
    | Succeeded out -> Assert.Equal("hi", out.Trim())
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

[<Fact(Timeout = 20000)>]
let ``runProcess streaming: a real failing test with output stays Failed`` () =
    // A genuine nonzero verdict is preserved, never converted to a stall.
    match
        runProcessBounded
            "sh"
            "-c \"echo boom; exit 3\""
            (ProcessBounds.streaming System.Threading.Timeout.InfiniteTimeSpan (TimeSpan.FromSeconds 5.0))
    with
    | Failed(3, out) -> Assert.Contains("boom", out)
    | other -> Assert.Fail $"expected Failed 3, got %A{other}"

[<Fact(Timeout = 20000)>]
let ``runProcess streaming: a nonzero exit with NO output is Failed, not a stall`` () =
    // Critical distinction (the regression that motivated dropping a silent-death
    // heuristic): a child that EXITS nonzero having produced nothing is a genuine
    // failing / zero-match test — a runner filtered to no tests exits nonzero with
    // no output — INDISTINGUISHABLE from a spawn-death at the process boundary. It
    // must be classified normally, NOT force-aborted, so it never masks a real
    // verdict. The machine-sleep case is covered by the poll observing the exit at
    // all (closing the wedge), not by guessing a death here.
    match
        runProcessBounded
            "sh"
            "-c \"exit 8\""
            (ProcessBounds.streaming System.Threading.Timeout.InfiniteTimeSpan (TimeSpan.FromSeconds 5.0))
    with
    | Failed(8, _) -> ()
    | other -> Assert.Fail $"expected Failed 8, got %A{other}"

[<Fact(Timeout = 20000)>]
let ``runProcess streaming: a child producing no output within the launch deadline raises`` () =
    // `sleep` produces no output and won't exit inside a 250 ms launch deadline —
    // the overloaded-spawn case. The tree is killed and a stall is raised.
    let ex =
        Assert.Throws<LaunchStalledException>(fun () ->
            runProcessBounded
                "sleep"
                "30"
                (ProcessBounds.streaming System.Threading.Timeout.InfiniteTimeSpan (TimeSpan.FromMilliseconds 250.0))
            |> ignore)

    Assert.Contains("no live process", ex.Data0)

[<Fact(Timeout = 20000)>]
let ``runProcess streaming: a progressing run that overruns the overall timeout is TimedOut`` () =
    // Progresses (emits "go") so the launch deadline never trips, but the overall
    // per-config timeout is a hard cap that still kills the tree → TimedOut. Proves
    // an alive run is bounded by the caller's timeout, never by the launch deadline.
    match
        runProcessBounded
            "sh"
            "-c \"echo go; sleep 30\""
            (ProcessBounds.streaming (TimeSpan.FromMilliseconds 300.0) (TimeSpan.FromSeconds 10.0))
    with
    | TimedOut(_, tail) -> Assert.Contains("go", tail)
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"

// ---------------------------------------------------------------------------
// AUTOMATION-98 finding 2 — the UNBOUNDED POST-EXIT DRAIN, on the SUCCESS path.
//
// The regression this pins: `runProcessWithTimeout`'s success arm ended in a bare
// `Task.WaitAll(stdoutTask, stderrTask)` with NO timeout. Those tasks complete at
// stream EOF, and EOF never arrives while ANY process holds the write end of the
// inherited stdout pipe — so a child that exits cleanly while a GRANDCHILD (an
// MSBuild node, a Playwright driver, a backgrounded `sleep`) still holds the pipe
// blocks that WaitAll FOREVER. The child is gone, the exit code is right there,
// and the daemon waits anyway. That is the 16 h wedge, and every hook / build /
// fileCommand spawn went through this path.
//
// `sh -c "( sleep 30 & ) ; echo done"` reproduces it exactly: `sh` exits at once
// (leaving `done` on the pipe), and the orphaned `sleep 30` inherits the pipe and
// holds it open for 30 s. Bounded drain → returns in ~PostExitDrainMs with the
// output it did capture. Unbounded drain → 30 s, blowing this test's 15 s budget.
//
// RED-BEFORE-GREEN: restore `Task.WaitAll(stdoutTask, stderrTask)` (no timeout) on
// the Exited arm and this test hangs until the xUnit timeout kills it.
// ---------------------------------------------------------------------------
[<Fact(Timeout = 15000)>]
let ``runProcess does not wait for a grandchild that inherited the stdout pipe`` () =
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let outcome =
        runProcessBounded "sh" "-c \"( sleep 30 & ) ; echo done\"" (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))

    sw.Stop()

    // Classified by the child's EXIT CODE, not by stream EOF.
    match outcome with
    | Succeeded out -> Assert.Contains("done", out)
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

    // The grandchild holds the pipe for 30 s; we must be long gone by then.
    Assert.True(
        sw.Elapsed < TimeSpan.FromSeconds 10.0,
        $"post-exit drain was not bounded: took %.1f{sw.Elapsed.TotalSeconds}s waiting on a \
          grandchild-held pipe for a child that had already exited"
    )
