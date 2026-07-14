// This class mutates the PROCESS ENVIRONMENT (`withEnv`) and then spawns children
// that snapshot it — two globals, interacting. Run in parallel with the other
// global-state classes it flaked under saturation: the child of
// `runProcess inherits the parent process environment` came up with the probe
// variable EMPTY (observed 2026-07-14 at load ~26, only inside `mise run ci`,
// which forks compile + lint + tests at once). Serialized with the rest of the
// process-global-state classes, per the pattern this repo already uses for the
// logging globals and the live file watchers.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ProcessHelperTests

open System
open Xunit
open Xunit.Sdk
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

// ---------------------------------------------------------------------------
// AUTOMATION-126 — THE DISEASE, IN OUR OWN TEST ASSERTIONS.
//
// Every stdout assertion in this file used to be shaped like
//
//     match runProcess "sh" (echoEnv key) "." [] with
//     | Succeeded out -> Assert.Equal(expected, out)
//
// and `out` was a plain `string`. On a loaded box the 2 s post-exit drain window
// expired before the thread-pool-scheduled reader ever ran, so `out` came back
// `""` — and the assertion compared against it AS IF IT HAD MEASURED AN EMPTY
// OUTPUT. Three sightings on 2026-07-14. When `expected` was `""` (the env-strip
// tests!) it PASSED, proving nothing; when it wasn't, it failed with a mismatch
// that named the wrong culprit.
//
// That is the same failure as a `top` that cannot sample reporting a healthy box,
// or a check that cannot run tests reporting no failures: a measurement that could
// not be taken, reported as a measurement of nothing. The cure is not a longer
// window — it is that "I never finished draining" and "the child printed nothing"
// are now DIFFERENT VALUES (`ProcessOutput.DrainTimedOut` vs `Drained ""`), and
// only the second can be asserted against.
//
// `expectStdout` below is the one door every stdout assertion goes through, and it
// slams on the first.
// ---------------------------------------------------------------------------

let private expectStdout (expected: string) (outcome: ProcessOutcome) =
    match outcome with
    | Succeeded(ProcessOutput.Drained text) -> Assert.Equal(expected, text)
    | Succeeded(ProcessOutput.DrainTimedOut(captured, window)) ->
        Assert.Fail
            $"THE DRAIN DID NOT FINISH within %d{int window.TotalSeconds}s, so the child's output was never \
              measured and CANNOT be compared to %A{expected}. What we caught before giving up: %A{captured} — \
              which is not the same fact as 'the child printed that'. Cause: the reader was starved, or a \
              grandchild is holding the pipe open. This test is not red because the child misbehaved; it is red \
              because we failed to listen."
    | other -> Assert.Fail $"expected Succeeded, got %A{other}"

// --- pumpReachedEof: did a stopped pump stop AT EOF? (internal, via InternalsVisibleTo) ---
// The live arms need a pipe torn down mid-read (exercised end-to-end in
// FsHotWatch.IntegrationTests); these pin the pure decision deterministically.

[<Fact(Timeout = 5000)>]
let ``pumpReachedEof: a read loop that ended with no failure reached EOF`` () = Assert.True(pumpReachedEof None)

[<Fact(Timeout = 5000)>]
let ``pumpReachedEof: an expected drain failure did NOT reach EOF`` () =
    // F18: the timeout-kill tears the pipe down under the pump. The read DIED, so
    // the capture is not provably complete — false, never "EOF with no more bytes".
    Assert.False(pumpReachedEof (Some(IO.IOException "pipe broken after kill")))
    Assert.False(pumpReachedEof (Some(ObjectDisposedException "stream")))

[<Fact(Timeout = 5000)>]
let ``pumpReachedEof: an UNEXPECTED failure is re-raised, never laundered into a verdict`` () =
    // A read that failed for a reason we do not understand must not be quietly
    // downgraded to "not drained" — that would hide a real bug inside an outcome
    // the caller is trained to tolerate. It escapes.
    Assert.Throws<NullReferenceException>(fun () -> pumpReachedEof (Some(NullReferenceException())) |> ignore)
    |> ignore

// --- classifyDrain: the capture is only complete if BOTH pumps ended at EOF ---

/// An EOF thunk that must never be forced: forcing it would mean reading
/// `Task.Result` on a pump that is still running — the block this design forbids.
let private mustNotBeForced () : bool =
    failwith "classifyDrain forced an EOF flag before the wait proved the pump had stopped — this would BLOCK"

[<Fact(Timeout = 5000)>]
let ``classifyDrain: a wait that did not return is a timed-out drain, and never touches Task.Result`` () =
    match classifyDrain false mustNotBeForced mustNotBeForced "partial" (TimeSpan.FromSeconds 2.0) with
    | ProcessOutput.DrainTimedOut(captured, window) ->
        Assert.Equal("partial", captured)
        Assert.Equal(TimeSpan.FromSeconds 2.0, window)
    | other -> Assert.Fail $"expected DrainTimedOut, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``classifyDrain: a stdout pump that died short of EOF is a timed-out drain`` () =
    match classifyDrain true (fun () -> false) mustNotBeForced "partial" (TimeSpan.FromSeconds 2.0) with
    | ProcessOutput.DrainTimedOut(captured, _) -> Assert.Equal("partial", captured)
    | other -> Assert.Fail $"expected DrainTimedOut, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``classifyDrain: a stderr pump that died short of EOF is a timed-out drain`` () =
    // stdout reached EOF but stderr did not — STILL not a complete measurement.
    match classifyDrain true (fun () -> true) (fun () -> false) "partial" (TimeSpan.FromSeconds 2.0) with
    | ProcessOutput.DrainTimedOut(captured, _) -> Assert.Equal("partial", captured)
    | other -> Assert.Fail $"expected DrainTimedOut, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``classifyDrain: both pumps at EOF inside the window is the child's complete output`` () =
    match classifyDrain true (fun () -> true) (fun () -> true) "all of it" (TimeSpan.FromSeconds 2.0) with
    | ProcessOutput.Drained text -> Assert.Equal("all of it", text)
    | other -> Assert.Fail $"expected Drained, got %A{other}"

// --- ProcessOutput.text / renderOutput: the two ways to read a capture ---

[<Fact(Timeout = 5000)>]
let ``ProcessOutput.text hands back the captured bytes, untagged, either way`` () =
    // For text-SEARCHING (a marker either present or not), where a short capture
    // can only cost a hit, never invent one.
    Assert.Equal("hi", ProcessOutput.text (ProcessOutput.Drained "hi"))
    Assert.Equal("hi", ProcessOutput.text (ProcessOutput.DrainTimedOut("hi", TimeSpan.FromSeconds 2.0)))

[<Fact(Timeout = 5000)>]
let ``renderOutput leaves a fully-drained capture exactly as the child said it`` () =
    Assert.Equal("hello", renderOutput (ProcessOutput.Drained "hello"))

[<Fact(Timeout = 5000)>]
let ``renderOutput NAMES an incomplete drain so no human reads the silence as the child's`` () =
    let rendered =
        renderOutput (ProcessOutput.DrainTimedOut("half a line", TimeSpan.FromSeconds 2.0))

    Assert.Contains("half a line", rendered)
    Assert.Contains("OUTPUT INCOMPLETE", rendered)
    Assert.Contains("2s", rendered)

[<Fact(Timeout = 5000)>]
let ``outputOf renders each outcome's capture, tagging the ones we could not finish`` () =
    Assert.Equal("ok", outputOf (Succeeded(ProcessOutput.Drained "ok")))
    Assert.Equal("boom", outputOf (Failed(2, ProcessOutput.Drained "boom")))

    let timedOut =
        outputOf (TimedOut(TimeSpan.FromSeconds 30.0, ProcessOutput.Drained "tail", KillOutcome.Killed))

    Assert.Contains("timed out after 30s", timedOut)
    Assert.Contains("tail", timedOut)
    // A tree we DID kill says nothing extra — the note is not noise on the happy path.
    Assert.DoesNotContain("KILL FAILED", timedOut)

    // An empty capture we never managed to take does NOT render as an empty output.
    let unmeasured =
        outputOf (Succeeded(ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0)))

    Assert.Contains("OUTPUT INCOMPLETE", unmeasured)

[<Fact(Timeout = 20000)>]
let ``runProcess returns Succeeded when fast`` () =
    runProcess "echo" "hi" "." [] |> expectStdout "hi"

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
    runProcess "echo" "hello" "." [] |> expectStdout "hello"

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
    Assert.True(isTimedOut (TimedOut(TimeSpan.FromSeconds 1.0, ProcessOutput.Drained "tail", KillOutcome.Killed)))
    Assert.False(isTimedOut (Succeeded(ProcessOutput.Drained "ok")))
    Assert.False(isTimedOut (Failed(1, ProcessOutput.Drained "boom")))

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

// NOTE every assertion below expects the EMPTY string — these are the strip tests,
// and an empty stdout is exactly what they are here to prove. They are therefore
// the tests a starved drain could silently fake: pre-AUTOMATION-126 a reader that
// never ran produced `""`, they all went green, and the strip contract they claim
// to guard was never actually exercised. `expectStdout` now demands a MEASURED
// empty output and rejects an unmeasured one.

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

// ---------------------------------------------------------------------------
// AUTOMATION-149 — `killTree` used to be `try proc.Kill(...) with _ -> ()`.
//
// The catch-all laundered an exception into a verdict: a Win32 permission failure
// and a clean kill left exactly the same trace (none), so "I could not kill the tree"
// was spelled identically to "I killed the tree" — and the caller was handed a
// `TimedOut` whose docstring PROMISED the tree was dead while it went on running.
// Same disease as AUTOMATION-126's drain: a failure to DO the work, rendered
// indistinguishable from the work succeeding.
//
// `isExpectedKillException` (F17) had said since the 2026-05-02 audit which
// exceptions are benign — and the call site that was supposed to consult it never
// did. `classifyKill` is that policy, finally wired up, and pure so the FAILURE arm
// is deterministic: live-firing it would need a process we are genuinely forbidden
// to kill, which is not something a test can conjure reliably on every OS.
//
// RED-BEFORE-GREEN: restore `with _ -> ()` (i.e. make `classifyKill` return
// `KillOutcome.Killed` for every input) and the two `KillFailed` tests below fail.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``classifyKill: a kill that returned killed the tree (AUTOMATION-149)`` () =
    Assert.Equal(KillOutcome.Killed, classifyKill None)

[<Fact(Timeout = 5000)>]
let ``classifyKill: the already-exited race is benign, not a failure (AUTOMATION-149)`` () =
    // The child exited in the gap between the timeout firing and the kill landing.
    // Nobody had to kill it; the tree is dead either way. This is the ONLY exception
    // the old catch-all was entitled to swallow (F17).
    Assert.Equal(KillOutcome.AlreadyExited, classifyKill (Some(System.InvalidOperationException())))

[<Fact(Timeout = 5000)>]
let ``classifyKill: a permission failure is a kill we did NOT perform (AUTOMATION-149)`` () =
    // THE regression. Win32Exception = we were refused. The tree is still running, and
    // the old `with _ -> ()` reported that as success.
    match classifyKill (Some(System.ComponentModel.Win32Exception())) with
    | KillOutcome.KillFailed reason -> Assert.IsType<System.ComponentModel.Win32Exception>(reason) |> ignore
    | other -> Assert.Fail $"a refused kill must not be spelled like a successful one, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``classifyKill: an unexpected exception is a kill we did NOT perform (AUTOMATION-149)`` () =
    // Anything outside the F17 benign class is, by construction, a kill we cannot
    // vouch for — it is NEVER quietly rounded down to "killed".
    match classifyKill (Some(System.NullReferenceException())) with
    | KillOutcome.KillFailed reason -> Assert.IsType<System.NullReferenceException>(reason) |> ignore
    | other -> Assert.Fail $"an unexplained kill failure must not be spelled like a successful one, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``a kill that failed is NAMED in the output a human reads (AUTOMATION-149)`` () =
    let leaked =
        outputOf (
            TimedOut(
                TimeSpan.FromSeconds 30.0,
                ProcessOutput.Drained "tail",
                KillOutcome.KillFailed(System.ComponentModel.Win32Exception())
            )
        )

    // The timeout is still reported...
    Assert.Contains("timed out after 30s", leaked)
    Assert.Contains("tail", leaked)
    // ...but it can no longer be mistaken for "and the runaway is over".
    Assert.Contains("KILL FAILED", leaked)
    Assert.Contains("STILL RUNNING", leaked)

[<Fact(Timeout = 5000)>]
let ``renderKillBrief marks a leaked tree on a one-line status, and is silent otherwise`` () =
    // A status line reading only "timed out after 30s" invites the wrong conclusion.
    Assert.Contains("KILL FAILED", renderKillBrief (KillOutcome.KillFailed(System.ComponentModel.Win32Exception())))
    Assert.Equal("", renderKillBrief KillOutcome.Killed)
    Assert.Equal("", renderKillBrief KillOutcome.AlreadyExited)

// `killTreeWith` takes the kill as a parameter precisely so these three arms are
// reachable without an unkillable process — the failure arm included. Coverage here is
// EARNED, not excluded: the branch that used to be `with _ -> ()` is now executed by a
// test that watches it produce a value the caller cannot ignore.

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a kill that returns reports Killed (AUTOMATION-149)`` () =
    Assert.Equal(KillOutcome.Killed, killTreeWith (fun () -> "child") id)

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a child that beat us to it reports AlreadyExited (AUTOMATION-149)`` () =
    let kill () =
        raise (System.InvalidOperationException())

    Assert.Equal(KillOutcome.AlreadyExited, killTreeWith (fun () -> "child") kill)

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a refused kill reports KillFailed, never Killed (AUTOMATION-149)`` () =
    let boom = System.ComponentModel.Win32Exception()
    let kill () = raise boom

    match killTreeWith (fun () -> "child") kill with
    | KillOutcome.KillFailed reason -> Assert.Same(boom, reason)
    | other -> Assert.Fail $"a kill the OS refused must not be reported as done, got %A{other}"

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
    runProcessBounded
        "echo"
        "hi"
        (ProcessBounds.streaming System.Threading.Timeout.InfiniteTimeSpan (TimeSpan.FromSeconds 5.0))
    |> expectStdout "hi"

[<Fact(Timeout = 20000)>]
let ``runProcess streaming: a real failing test with output stays Failed`` () =
    // A genuine nonzero verdict is preserved, never converted to a stall.
    match
        runProcessBounded
            "sh"
            "-c \"echo boom; exit 3\""
            (ProcessBounds.streaming System.Threading.Timeout.InfiniteTimeSpan (TimeSpan.FromSeconds 5.0))
    with
    // A child with no grandchild reaches EOF the instant it exits, so its failure
    // output is a MEASURED capture — `Drained`, not a partial we hope contains "boom".
    | Failed(3, ProcessOutput.Drained out) -> Assert.Contains("boom", out)
    | other -> Assert.Fail $"expected Failed 3 with a fully-drained output, got %A{other}"

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
    // The kill-path tail is EXPLICITLY a best-effort capture — the kill tears the
    // pipes down under the pumps, so whether they end at EOF or die mid-read is an
    // OS race. `ProcessOutput.text` is us SAYING SO: this is the one call site in
    // the file that reads a capture without demanding it be complete, and it is a
    // deliberate, greppable act rather than a `""` we mistook for an answer.
    | TimedOut(_, tail, _) -> Assert.Contains("go", ProcessOutput.text tail)
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
/// A child that exits at once while an orphaned grandchild keeps the inherited
/// stdout pipe open for 30 s. EOF therefore CANNOT arrive inside the 2 s drain
/// window — this is a real, fully deterministic drain that cannot finish, and it is
/// the fixture both tests below stand on.
let private spawnWithPipeHoldingGrandchild () =
    runProcessBounded "sh" "-c \"( sleep 30 & ) ; echo done\"" (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))

[<Fact(Timeout = 15000)>]
let ``runProcess does not wait for a grandchild that inherited the stdout pipe`` () =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let outcome = spawnWithPipeHoldingGrandchild ()
    sw.Stop()

    // Classified by the child's EXIT CODE, not by stream EOF — a grandchild holding
    // the pipe cannot turn a clean exit into a failure. But the capture is honestly
    // marked: we bailed on a stream that never reached EOF, so what we have is what
    // we caught, NOT a measurement of what the child said.
    match outcome with
    | Succeeded(ProcessOutput.DrainTimedOut(captured, window)) ->
        Assert.Contains("done", captured)
        Assert.Equal(TimeSpan.FromSeconds 2.0, window)
    | other -> Assert.Fail $"expected Succeeded with a DrainTimedOut capture, got %A{other}"

    // The grandchild holds the pipe for 30 s; we must be long gone by then.
    Assert.True(
        sw.Elapsed < TimeSpan.FromSeconds 10.0,
        $"post-exit drain was not bounded: took %.1f{sw.Elapsed.TotalSeconds}s waiting on a \
          grandchild-held pipe for a child that had already exited"
    )

// ---------------------------------------------------------------------------
// AUTOMATION-126, THE REGRESSION TEST: a drain that could not finish must FAIL an
// output assertion, loudly and by its true name — never be silently compared
// against as if it were the child's output.
//
// RED-BEFORE-GREEN: revert `ProcessOutput` to a plain `string` and this test cannot
// even be written — `expectStdout` would receive `"done"` (or, with a starved
// reader, `""`) and assert against it, exactly as it did on 2026-07-14.
// ---------------------------------------------------------------------------
[<Fact(Timeout = 15000)>]
let ``a drain that could not finish FAILS an output assertion by its true name`` () =
    let outcome = spawnWithPipeHoldingGrandchild ()

    let failure = Assert.Throws<FailException>(fun () -> expectStdout "done" outcome)

    // It is red for the RIGHT reason: it names the unfinished drain, and it does NOT
    // pretend to have compared two strings. (Note the child really did print "done":
    // even a capture that happens to hold what we wanted is not evidence we heard
    // all of it, so it is refused all the same.)
    Assert.Contains("THE DRAIN DID NOT FINISH", failure.Message)
    Assert.Contains("we failed to listen", failure.Message)
    Assert.DoesNotContain("Assert.Equal() Failure", failure.Message)

// ---------------------------------------------------------------------------
// AUTOMATION-149: the failed kill must be LOUD, not merely representable.
//
// The value on `TimedOut` is what a CALLER cannot ignore. This is the other half:
// what an OPERATOR — someone reading nothing but the daemon's stderr — is told when
// a runaway tree survives us. `with _ -> ()` told them nothing at all.
//
// Touches Logging.logLevel + Console.Error → joins the LogGlobal serialized
// collection (see TestHelpers).
// ---------------------------------------------------------------------------
[<Collection(LogGlobalCollectionName)>]
type KillFailureIsLoudTests() =

    [<Fact(Timeout = 5000)>]
    member _.``a kill we could not perform is logged at ERROR, naming what failed and why``() =
        let original = FsHotWatch.Logging.logLevel
        let sb = Text.StringBuilder()
        let writer = new IO.StringWriter(sb)
        let prevErr = Console.Error

        try
            Console.SetError(writer)
            FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error

            let boom = System.ComponentModel.Win32Exception(1, "Operation not permitted")
            let kill () = raise boom

            let outcome = killTreeWith (fun () -> "`sleep 10` (pid 4242)") kill

            match outcome with
            | KillOutcome.KillFailed _ -> ()
            | other -> Assert.Fail $"expected KillFailed, got %A{other}"

            let logged = sb.ToString()

            // WHAT we failed to kill...
            Assert.Contains("sleep 10", logged)
            Assert.Contains("4242", logged)
            // ...WHY...
            Assert.Contains("Win32Exception", logged)
            Assert.Contains("Operation not permitted", logged)
            // ...and what it MEANS, in words that cannot be read as success.
            Assert.Contains("FAILED to kill", logged)
            Assert.Contains("STILL RUNNING", logged)
        finally
            Console.SetError(prevErr)
            FsHotWatch.Logging.setLogLevel original

    [<Fact(Timeout = 5000)>]
    member _.``a kill that worked says nothing — the loud path is reserved for real failure``() =
        let original = FsHotWatch.Logging.logLevel
        let sb = Text.StringBuilder()
        let writer = new IO.StringWriter(sb)
        let prevErr = Console.Error

        try
            Console.SetError(writer)
            FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error

            killTreeWith (fun () -> "`sleep 10` (pid 4242)") id |> ignore
            // The already-exited race is benign: the tree is dead either way.
            killTreeWith (fun () -> "`sleep 10` (pid 4243)") (fun () -> raise (InvalidOperationException()))
            |> ignore

            Assert.Equal("", sb.ToString().Trim())
        finally
            Console.SetError(prevErr)
            FsHotWatch.Logging.setLogLevel original
