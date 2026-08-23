// This class mutates the PROCESS ENVIRONMENT (`withEnv`) and then spawns children that
// snapshot it — two globals interacting. Run in parallel with the other global-state
// classes it flaked under saturation: the child of `runProcess inherits the parent process
// environment` came up with the probe variable EMPTY. Hence the serialized collection the
// repo already uses for the logging globals and the live file watchers.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ProcessHelperTests

open System
open Xunit
open Xunit.Sdk
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

/// Bounds for the spawn tests that care about something OTHER than the bounds (the child-env
/// contract, the process registry). Named so the bounds read as incidental — the
/// bound-specific behaviour has its own tests below.
let private quick = ProcessBounds.silent (TimeSpan.FromSeconds 30.0)

/// Shadows `ProcessHelper.runProcess` with the 4-arg form those tests want.
let private runProcess command args workDir env =
    FsHotWatch.ProcessHelper.runProcess command args workDir env quick

/// The bound-specific tests: same spawn, explicit bounds, no env / workDir noise.
let private runProcessBounded command args bounds =
    FsHotWatch.ProcessHelper.runProcess command args "." [] bounds

// ---------------------------------------------------------------------------
// AUTOMATION-126 — a drain that never finished, asserted against as if it had.
//
// Stdout assertions here used to compare against a plain `string`. On a loaded box the 2s
// post-exit drain window expired before the thread-pool-scheduled reader ever ran, so `out`
// came back `""` and the assertion compared against it AS IF IT HAD MEASURED AN EMPTY
// OUTPUT. Where `expected` was `""` (the env-strip tests below!) it PASSED, proving nothing.
//
// The cure is not a longer window: "I never finished draining" and "the child printed
// nothing" are now DIFFERENT VALUES (`ProcessOutput.DrainTimedOut` vs `Drained ""`), and
// only the second may be asserted against. `expectStdout` is the one door every stdout
// assertion goes through, and it slams on the first.
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
    // A read that failed for a reason we do not understand must not be downgraded to "not
    // drained" — that hides a real bug inside an outcome the caller is trained to tolerate.
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
    // For text-SEARCHING (a marker either present or not), where a short capture can only
    // cost a hit, never invent one.
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
    // A timed-out in-process unit used to leave its orphan thread running, still holding
    // whatever lock it grabbed. The flags below prove the token actually fired and the
    // thread actually unwound, rather than the call merely returning.
    let observedCancellation = new System.Threading.ManualResetEventSlim(false)
    let workExited = new System.Threading.ManualResetEventSlim(false)

    let work (ct: System.Threading.CancellationToken) =
        try
            // Models a stuck unit that honours cancellation (FCS/Fantomas/etc.).
            ct.WaitHandle.WaitOne() |> ignore
            observedCancellation.Set()
            42
        finally
            workExited.Set()

    let result = runWithCancellableTimeout (TimeSpan.FromMilliseconds 50.0) work

    match result with
    | WorkTimedOut _ -> ()
    | WorkCompleted _ -> Assert.Fail "expected timeout"

    Assert.True(observedCancellation.Wait(TimeSpan.FromSeconds 5.0), "work never observed cancellation — orphaned")
    Assert.True(workExited.Wait(TimeSpan.FromSeconds 5.0), "work thread never exited — orphaned")

[<Fact(Timeout = 20000)>]
let ``runWithCancellableTimeout cancelled unit releases its lock so the next unit proceeds`` () =
    // The wedge, concretely: A grabs a lock and hangs, times out, and must release it so B
    // can acquire it. With an orphan thread holding the lock forever, B blocks indefinitely.
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

    // Exited PIDs the OS could later recycle must not linger in the registry.
    Assert.Empty(FsHotWatch.ProcessRegistry.snapshot ())

[<Fact(Timeout = 5000)>]
let ``isTimedOut is true only for TimedOut`` () =
    // The live timeout-kill path that produces a real TimedOut lives in
    // FsHotWatch.IntegrationTests — its OS-scheduling-dependent kill/drain branches jitter
    // coverage — so the pure discriminator is pinned here instead.
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

// The strip tests below expect the EMPTY string, which makes them the ones a starved drain
// could silently fake: a reader that never ran also produced `""`, so they went green
// without exercising the strip contract at all. `expectStdout` demands a MEASURED empty
// output and rejects an unmeasured one.

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

// Leaked-MSBuild-env contract. `Ionide.ProjInfo.Init.init` writes MSBUILD_EXE_PATH /
// MSBuildExtensionsPath / MSBuildSDKsPath into the daemon's OWN process environment, pinning
// them at one SDK band's MSBuild. Process.Start inherits the full parent env, so a spawned
// `dotnet build` then runs MSBuild from a possibly-different band than the muxer resolves —
// the restore-graph sub-build fails with exit 1 and ZERO diagnostics ("Build FAILED /
// 0 Error(s)"). Stripping these on every spawn lets the child re-resolve from its own SDK.
// Caller-supplied overrides still win.
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

// DOTNET_HOST_PATH realpath contract (the nix-wrapped-SDK case). The .NET muxer reads
// DOTNET_HOST_PATH literally and computes DOTNET_ROOT_<arch> = dirname(DOTNET_HOST_PATH);
// on a wrapped-bin/ symlink that dirname has no `shared/` sibling, so apphost dies.
// Resolving the symlink before spawn lands dirname on the unwrapped runtime tree.
//
// These use the explicit-env arg rather than mutating process env, which races the parallel
// runner: a concurrent DaemonTests subprocess inherits the temp-dir DOTNET_HOST_PATH, the
// test cleans the temp dir, and the daemon spawn dies on `wrapped-dotnet`. ProcessHelper
// applies the realpath after the explicit overlay, so the contract holds for either entry.

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
    // Unsetting is safe under parallel load: a concurrent dotnet spawn missing
    // DOTNET_HOST_PATH re-derives it from argv[0]. Setting it to a temp path is what races,
    // which is why the rest of this group uses the explicit-env arg.
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

// The timeout-kill test that forces the kill-on-timeout drain path lives in
// FsHotWatch.IntegrationTests/ProcessHelperTimeoutTests.fs — its OS-scheduling-dependent
// coverage made the unit-suite line coverage jitter. The deterministic helpers below still
// pin the F17/F18 contracts here.

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
// A Win32 permission failure and a clean kill left exactly the same trace (none), so "I
// could not kill the tree" was spelled identically to "I killed the tree" — and the caller
// got a `TimedOut` whose docstring PROMISED the tree was dead while it went on running.
//
// `classifyKill` is `isExpectedKillException`'s policy finally wired up, and it is pure so
// the FAILURE arm is deterministic: live-firing it needs a process we are genuinely
// forbidden to kill, which no test can conjure reliably on every OS.
//
// RED-BEFORE-GREEN: make `classifyKill` return `Killed` for every input and the two
// `KillFailed` tests below fail.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``classifyKill: a kill that returned killed the tree (AUTOMATION-149)`` () =
    Assert.Equal(KillOutcome.Killed, classifyKill KillCall.Returned)

[<Fact(Timeout = 5000)>]
let ``classifyKill: the already-exited race is benign, not a failure (AUTOMATION-149)`` () =
    // The child exited between the timeout firing and the kill landing: the tree is dead
    // either way. The ONLY exception the old catch-all was entitled to swallow (F17).
    Assert.Equal(KillOutcome.AlreadyExited, classifyKill (KillCall.Threw(System.InvalidOperationException())))

[<Fact(Timeout = 5000)>]
let ``classifyKill: a permission failure is a kill we did NOT perform (AUTOMATION-149)`` () =
    // Win32Exception = we were refused. The tree is still running, and the old
    // `with _ -> ()` reported that as success.
    match classifyKill (KillCall.Threw(System.ComponentModel.Win32Exception())) with
    | KillOutcome.KillFailed reason -> Assert.IsType<System.ComponentModel.Win32Exception>(reason) |> ignore
    | other -> Assert.Fail $"a refused kill must not be spelled like a successful one, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``classifyKill: an unexpected exception is a kill we did NOT perform (AUTOMATION-149)`` () =
    // Anything outside the F17 benign class is a kill we cannot vouch for, and is never
    // rounded down to "killed".
    match classifyKill (KillCall.Threw(System.NullReferenceException())) with
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

// `killTreeWith` takes the kill as a parameter so these three arms — the failure arm
// included — are reachable without an unkillable process.

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a kill that returns reports Killed (AUTOMATION-149)`` () =
    Assert.Equal(KillOutcome.Killed, killTreeWith TeardownBudget 4242 (fun () -> "child") id)

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a child that beat us to it reports AlreadyExited (AUTOMATION-149)`` () =
    let kill () =
        raise (System.InvalidOperationException())

    Assert.Equal(KillOutcome.AlreadyExited, killTreeWith TeardownBudget 4242 (fun () -> "child") kill)

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a refused kill reports KillFailed, never Killed (AUTOMATION-149)`` () =
    let boom = System.ComponentModel.Win32Exception()
    let kill () = raise boom

    match killTreeWith TeardownBudget 4242 (fun () -> "child") kill with
    | KillOutcome.KillFailed reason -> Assert.Same(boom, reason)
    | other -> Assert.Fail $"a kill the OS refused must not be reported as done, got %A{other}"

// ---------------------------------------------------------------------------
// AUTOMATION-454 — THE OTHER HALF OF AUTOMATION-149: a kill that never ANSWERS.
//
// A149 fixed the kill that says no. This is the kill that says nothing. On 2026-08-21 a
// `check` timed out five test projects at their configured caps, and then TestPrune never
// emitted `TestRunCompleted` at all: no CTRF, no coverage, no history, no verdict. The
// plugin stayed Running for another 72 minutes until the daemon's wedge watchdog restarted
// it, and the waiting check exited 2 with mass 0ms results — a run that could not report,
// which reads exactly like a run that had nothing to say.
//
// Every phase after the per-project timeout was already bounded (the drain window is 2s)
// EXCEPT the teardown itself: `killTreeWith` called `proc.Kill(entireProcessTree = true)`
// and waited for it forever. `Kill(entireProcessTree = true)` is not a signal, it is a WALK
// of the process table, and a walk on a starved box can block. One blocked project task is
// enough: `Async.Parallel` never completes, so the run never reaches the serial
// coverage/history/verdict work.
//
// The bound is on the CALL, so the injected regression is a kill that BLOCKS — the direct
// analogue of A149's kill that THROWS, and equally unconjurable from a real process.
//
// RED-BEFORE-GREEN: give `callKillWithin` back its unbounded `kill ()` and the blocking
// tests below hang until xUnit's 5s Timeout fires.
// ---------------------------------------------------------------------------

/// A budget short enough to keep these tests fast, long enough that a machine hiccup
/// cannot expire it under a kill that returns immediately.
let private shortBudget = TimeSpan.FromMilliseconds 250.0

/// A kill that BLOCKS until released — the injectable regression. Returns the kill and the
/// gate that frees the abandoned thread, so no test leaves a thread parked for the rest of
/// the run.
let private blockingKill () =
    let gate = new Threading.ManualResetEventSlim(false)
    let kill () = gate.Wait()
    kill, gate

[<Fact(Timeout = 5000)>]
let ``classifyKill: a kill that never answered is neither killed nor failed (AUTOMATION-454)`` () =
    // Not `Killed` — we established nothing. Not `KillFailed` — nobody refused us, and a
    // `KillFailed` carrying a fabricated exception would put words in the OS's mouth.
    let budget = TimeSpan.FromSeconds 10.0
    Assert.Equal(KillOutcome.KillTimedOut budget, classifyKill (KillCall.DidNotReturn budget))

[<Fact(Timeout = 5000)>]
let ``callKillWithin: a kill that returns is Returned — the positive control (AUTOMATION-454)`` () =
    Assert.Equal(KillCall.Returned, callKillWithin shortBudget id)

[<Fact(Timeout = 5000)>]
let ``callKillWithin: a kill that throws reports the throw, not a timeout (AUTOMATION-454)`` () =
    // The budget must not swallow the exception: A149's classification still has to see it.
    let boom = System.ComponentModel.Win32Exception()

    match callKillWithin shortBudget (fun () -> raise boom) with
    | KillCall.Threw ex -> Assert.Same(boom, ex)
    | other -> Assert.Fail $"a throwing kill must be reported as a throw, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``callKillWithin: a BLOCKING kill is cut off at the budget (AUTOMATION-454)`` () =
    let kill, gate = blockingKill ()

    try
        let clock = Diagnostics.Stopwatch.StartNew()
        let call = callKillWithin shortBudget kill
        let elapsed = clock.Elapsed

        Assert.Equal(KillCall.DidNotReturn shortBudget, call)
        // It waited for the budget...
        Assert.True(elapsed >= shortBudget, $"gave up after %A{elapsed}, before the %A{shortBudget} budget")
        // ...and then it STOPPED waiting. Without the bound this line is never reached.
        Assert.True(elapsed < TimeSpan.FromSeconds 3.0, $"the budget did not cut the wait off: %A{elapsed}")
    finally
        gate.Set()

[<Fact(Timeout = 5000)>]
let ``callKillWithin: the budget is not spent on a kill that returns (AUTOMATION-454)`` () =
    // The other direction: a healthy teardown is untouched by the bound. A tree really dies
    // in milliseconds, and a bound that made every kill wait out its budget would turn a
    // five-project run into an extra minute of nothing.
    let clock = Diagnostics.Stopwatch.StartNew()
    Assert.Equal(KillCall.Returned, callKillWithin (TimeSpan.FromSeconds 30.0) id)
    Assert.True(clock.Elapsed < TimeSpan.FromSeconds 3.0, $"a kill that returned still waited %A{clock.Elapsed}")

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a blocked teardown ends in KillTimedOut, not in a wedge (AUTOMATION-454)`` () =
    let kill, gate = blockingKill ()

    try
        // THE regression. Before the bound, this call does not return and the enclosing
        // project task never produces a result.
        match killTreeWith shortBudget 4242 (fun () -> "`dotnet test` (pid 4242)") kill with
        | KillOutcome.KillTimedOut budget -> Assert.Equal(shortBudget, budget)
        | other -> Assert.Fail $"a teardown that never answered must be named, got %A{other}"
    finally
        gate.Set()

[<Fact(Timeout = 5000)>]
let ``killTreeWith: a normal teardown still completes untouched (AUTOMATION-454)`` () =
    // Both positive controls, against the same budget the blocked case fails: bounding the
    // teardown may not change what a teardown that WORKS reports.
    Assert.Equal(KillOutcome.Killed, killTreeWith shortBudget 4242 (fun () -> "child") id)

    Assert.Equal(
        KillOutcome.AlreadyExited,
        killTreeWith shortBudget 4243 (fun () -> "child") (fun () -> raise (InvalidOperationException()))
    )

[<Fact(Timeout = 5000)>]
let ``a teardown that never answered is NAMED in the output a human reads (AUTOMATION-454)`` () =
    let unaccounted =
        outputOf (
            TimedOut(
                TimeSpan.FromSeconds 30.0,
                ProcessOutput.Drained "tail",
                KillOutcome.KillTimedOut(TimeSpan.FromSeconds 10.0)
            )
        )

    // The timeout is still reported...
    Assert.Contains("timed out after 30s", unaccounted)
    Assert.Contains("tail", unaccounted)
    // ...and so is the fact that we walked away without knowing.
    Assert.Contains("KILL TIMED OUT", unaccounted)
    Assert.Contains("STILL RUNNING", unaccounted)
    // It is NOT spelled like a refusal: "the OS said no" and "the OS said nothing" are
    // different things to go looking for.
    Assert.DoesNotContain("KILL FAILED", unaccounted)

[<Fact(Timeout = 5000)>]
let ``renderKillBrief marks an unaccounted-for tree on a one-line status (AUTOMATION-454)`` () =
    let brief = renderKillBrief (KillOutcome.KillTimedOut(TimeSpan.FromSeconds 10.0))
    Assert.Contains("KILL TIMED OUT", brief)
    Assert.Contains("UNACCOUNTED FOR", brief)
    // Still silent for the two outcomes that mean the tree is dead.
    Assert.Equal("", renderKillBrief KillOutcome.Killed)
    Assert.Equal("", renderKillBrief KillOutcome.AlreadyExited)

// ---------------------------------------------------------------------------
// AUTOMATION-454 — a tree we could not account for is REGISTERED, not only logged.
//
// The log line reaches whoever is reading stderr at that moment. Shutdown happens later,
// possibly hours later, possibly after a watchdog restart, and by then the `Process` handle
// is disposed and the pid survives only if somebody wrote it down.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 5000)>]
let ``a teardown that never answered registers the leaked pid (AUTOMATION-454)`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry
    let kill, gate = blockingKill ()

    try
        killTreeWith shortBudget 4242 (fun () -> "`dotnet test` (pid 4242)") kill
        |> ignore
    finally
        gate.Set()

    match FsHotWatch.ProcessRegistry.leaked () with
    | [ leak ] ->
        Assert.Equal(4242, leak.Pid)
        Assert.Contains("dotnet test", leak.Description)
        Assert.Contains("did not return", leak.Reason)
    | other -> Assert.Fail $"expected exactly one leaked tree, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``a refused teardown registers the leaked pid too (AUTOMATION-454)`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    killTreeWith shortBudget 4243 (fun () -> "`dotnet test` (pid 4243)") (fun () ->
        raise (System.ComponentModel.Win32Exception(1, "Operation not permitted")))
    |> ignore

    match FsHotWatch.ProcessRegistry.leaked () with
    | [ leak ] ->
        Assert.Equal(4243, leak.Pid)
        Assert.Contains("refused", leak.Reason)
    | other -> Assert.Fail $"expected exactly one leaked tree, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``a teardown that worked registers nothing — the ledger is not a spawn log (AUTOMATION-454)`` () =
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    killTreeWith shortBudget 4244 (fun () -> "child") id |> ignore

    killTreeWith shortBudget 4245 (fun () -> "child") (fun () -> raise (InvalidOperationException()))
    |> ignore

    Assert.Empty(FsHotWatch.ProcessRegistry.leaked ())

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
    // F19: HasExited on a disposed Process throws InvalidOperationException. After narrowing
    // from `with _` to :? InvalidOperationException + Win32, Snapshot must still treat it as
    // not-alive and skip it.
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    let psi = System.Diagnostics.ProcessStartInfo("echo", "hi")
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let proc = System.Diagnostics.Process.Start(psi)
    registry.Track proc
    proc.WaitForExit()
    proc.Dispose()

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
    // F20: Kill on an already-exited process throws InvalidOperationException. After
    // narrowing, KillAll must still complete cleanly when processes exited before shutdown.
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    let psi = System.Diagnostics.ProcessStartInfo("echo", "hi")
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    let proc = System.Diagnostics.Process.Start(psi)
    registry.Track proc
    proc.WaitForExit()

    registry.KillAll()

// AUTOMATION-454 — shutdown must NAME what it is walking away from.
//
// The error at the moment of the failed teardown reaches whoever happens to be reading
// stderr right then. Shutdown can be hours later, after a watchdog restart, and by then
// the `Process` handle is long disposed — the pid survives only because it was written
// down. `KillAll` is where the daemon stops looking, so it is where the ledger is read.

[<Fact(Timeout = 5000)>]
let ``ProcessRegistry: shutdown names a tree it could not account for (AUTOMATION-454)`` () =
    let original = FsHotWatch.Logging.logLevel
    let sb = Text.StringBuilder()
    let writer = new IO.StringWriter(sb)
    let prevErr = Console.Error
    let registry = FsHotWatch.ProcessRegistry.Registry()
    use _ = FsHotWatch.ProcessRegistry.install registry

    try
        Console.SetError(writer)
        FsHotWatch.Logging.setLogLevel FsHotWatch.Logging.LogLevel.Error

        FsHotWatch.ProcessRegistry.reportLeak 4242 "`dotnet test Integration` (pid 4242)" "the kill did not return"
        registry.KillAll()
    finally
        Console.SetError(prevErr)
        FsHotWatch.Logging.setLogLevel original

    let logged = sb.ToString()
    Assert.Contains("LEAKED process tree", logged)
    Assert.Contains("4242", logged)
    Assert.Contains("dotnet test Integration", logged)
    // It says what shutdown did NOT do, rather than implying the leak is handled.
    Assert.Contains("NOT reaping it", logged)

    // The ledger outlives the live set: `KillAll` clears what it killed, never what it
    // could not. A record that vanished at shutdown would be a record nobody could read.
    match registry.Leaks with
    | [ _ ] -> ()
    | other -> Assert.Fail $"KillAll must not clear the leak ledger, got %A{other}"

[<Fact(Timeout = 5000)>]
let ``ProcessRegistry: the leak ledger is scoped, not a process-wide global (AUTOMATION-454)`` () =
    // Same reason `Registry` itself is AsyncLocal-scoped: two daemons (or two parallel
    // test runs) must not read each other's unaccounted-for trees.
    let registry = FsHotWatch.ProcessRegistry.Registry()

    let insideScope =
        use _ = FsHotWatch.ProcessRegistry.install registry
        FsHotWatch.ProcessRegistry.reportLeak 4242 "child" "the kill did not return"
        FsHotWatch.ProcessRegistry.leaked ()

    match insideScope with
    | [ leak ] -> Assert.Equal(4242, leak.Pid)
    | other -> Assert.Fail $"expected the leak inside its own scope, got %A{other}"

    Assert.Empty(FsHotWatch.ProcessRegistry.leaked ())

// --- Launch-liveness watchdog (AUTOMATION-65 QA finding: the launch gap) ---
// The pure decision and the injectable loop are pinned deterministically here — no real
// process, no OS scheduling. The real-process arms below are sub-second and hold no global
// state.

// decideLaunchStep: (launchDeadlineReached, overallTimeoutReached, exited, sawOutput)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: exited wins over everything`` () =
    // Even racing the launch deadline with no output, a process that EXITED is a natural
    // completion to classify, never a stall.
    Assert.Equal(LaunchStep.Exited, decideLaunchStep true false true false)
    Assert.Equal(LaunchStep.Exited, decideLaunchStep false false true true)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: no life within the launch deadline is a stall`` () =
    // never-appears: not exited, no output, past the launch deadline.
    Assert.Equal(LaunchStep.Stalled, decideLaunchStep true false false false)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: a process that produced output is never launch-killed`` () =
    // Slow-but-alive: the launch deadline governs launch, not total duration.
    Assert.Equal(LaunchStep.KeepWaiting, decideLaunchStep true false false true)

[<Fact(Timeout = 5000)>]
let ``decideLaunchStep: overall timeout ends even a progressing run`` () =
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

// launchWatchdogLoopWith: injected observe/clock/sleep — deterministic, no process. The
// fake clock advances by the slept ms on each poll, so the deadline is reached after a
// bounded number of iterations.

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
    // No output, then it's gone. The wrapper turns this into a launch-death (raise); the
    // loop's job is only to observe the exit promptly.
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
    // Streams output for far longer than the launch deadline, then exits: the deadline must
    // NOT trip.
    let now, advance = fakeClock DateTime.UtcNow
    let calls = ref 0

    let observe () =
        incr calls
        // 20 polls of "alive, streaming output" — ~5s at 250ms/poll, well past the 1s
        // launch deadline — then a natural exit.
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
    // Why there is no silent-death heuristic: a child that EXITS nonzero having produced
    // nothing is a genuine failing / zero-match run (a runner filtered to no tests looks
    // exactly like this), and is INDISTINGUISHABLE from a spawn-death at the process
    // boundary. Classifying it normally rather than force-aborting keeps it from masking a
    // real verdict; the machine-sleep case is closed by the poll observing the exit at all.
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
    // Progresses (emits "go") so the launch deadline never trips, but the per-config timeout
    // is a hard cap that still kills the tree — an alive run is bounded by the caller's
    // timeout, never by the launch deadline.
    match
        runProcessBounded
            "sh"
            "-c \"echo go; sleep 30\""
            (ProcessBounds.streaming (TimeSpan.FromMilliseconds 300.0) (TimeSpan.FromSeconds 10.0))
    with
    // The kill-path tail is EXPLICITLY best-effort: the kill tears the pipes down under the
    // pumps, so whether they end at EOF or die mid-read is an OS race. `ProcessOutput.text`
    // is the one call site here that reads a capture without demanding it be complete, and
    // it is deliberate and greppable rather than a `""` mistaken for an answer.
    | TimedOut(_, tail, _) -> Assert.Contains("go", ProcessOutput.text tail)
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"

// ---------------------------------------------------------------------------
// AUTOMATION-98 finding 2 — the UNBOUNDED POST-EXIT DRAIN, on the SUCCESS path.
//
// The success arm ended in a bare `Task.WaitAll(stdoutTask, stderrTask)` with NO timeout.
// Those tasks complete at stream EOF, and EOF never arrives while ANY process holds the
// write end of the inherited stdout pipe — so a child that exits cleanly while a GRANDCHILD
// (an MSBuild node, a Playwright driver, a backgrounded `sleep`) still holds the pipe blocks
// FOREVER. That is the 16h wedge, and every hook / build / fileCommand spawn took this path.
//
// RED-BEFORE-GREEN: restore the untimed `Task.WaitAll` on the Exited arm and this test hangs
// until the xUnit timeout kills it.
// ---------------------------------------------------------------------------
/// A child that exits at once while an orphaned grandchild keeps the inherited stdout pipe
/// open for 30s, so EOF CANNOT arrive inside the 2s drain window — a fully deterministic
/// drain that cannot finish, and the fixture both tests below stand on.
let private spawnWithPipeHoldingGrandchild () =
    runProcessBounded "sh" "-c \"( sleep 30 & ) ; echo done\"" (ProcessBounds.silent (TimeSpan.FromSeconds 60.0))

[<Fact(Timeout = 15000)>]
let ``runProcess does not wait for a grandchild that inherited the stdout pipe`` () =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let outcome = spawnWithPipeHoldingGrandchild ()
    sw.Stop()

    // Classified by the child's EXIT CODE, not by stream EOF — a grandchild holding the pipe
    // cannot turn a clean exit into a failure. The capture is still marked honestly: we
    // bailed on a stream that never reached EOF, so it is what we caught, not what the child
    // said.
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
// AUTOMATION-126's regression test: a drain that could not finish must FAIL an output
// assertion by its true name, never be compared against as if it were the child's output.
//
// RED-BEFORE-GREEN: revert `ProcessOutput` to a plain `string` and this test cannot even be
// written — `expectStdout` would receive `"done"` (or `""`) and assert against it.
// ---------------------------------------------------------------------------
[<Fact(Timeout = 15000)>]
let ``a drain that could not finish FAILS an output assertion by its true name`` () =
    let outcome = spawnWithPipeHoldingGrandchild ()

    let failure = Assert.Throws<FailException>(fun () -> expectStdout "done" outcome)

    // Red for the RIGHT reason: it names the unfinished drain and does not pretend to have
    // compared two strings. The child really did print "done" — a capture that happens to
    // hold what we wanted is still not evidence we heard all of it.
    Assert.Contains("THE DRAIN DID NOT FINISH", failure.Message)
    Assert.Contains("we failed to listen", failure.Message)
    Assert.DoesNotContain("Assert.Equal() Failure", failure.Message)

// ---------------------------------------------------------------------------
// AUTOMATION-149: the failed kill must be LOUD, not merely representable. The value on
// `TimedOut` is what a CALLER cannot ignore; this is the other half — what an OPERATOR
// reading nothing but the daemon's stderr is told when a runaway tree survives us.
//
// Touches Logging.logLevel + Console.Error, hence the LogGlobal serialized collection.
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

            let outcome =
                killTreeWith TeardownBudget 4242 (fun () -> "`sleep 10` (pid 4242)") kill

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

            killTreeWith TeardownBudget 4242 (fun () -> "`sleep 10` (pid 4242)") id
            |> ignore
            // The already-exited race is benign: the tree is dead either way.
            killTreeWith TeardownBudget 4243 (fun () -> "`sleep 10` (pid 4243)") (fun () ->
                raise (InvalidOperationException()))
            |> ignore

            Assert.Equal("", sb.ToString().Trim())
        finally
            Console.SetError(prevErr)
            FsHotWatch.Logging.setLogLevel original

    // -----------------------------------------------------------------------
    // AUTOMATION-454 — the teardown must also be loud when it WORKS.
    //
    // The 2026-08-21 evidence recorded that five projects hit their caps and then
    // recorded nothing else. From that log it is impossible to say whether the run
    // stopped before the teardown or inside it, or which of the five projects was
    // the one still holding `Async.Parallel` open. Begin/end/duration for every
    // kill is what turns "TestPrune stopped" into "TestPrune stopped killing THIS".
    // -----------------------------------------------------------------------

    /// Capture whatever the daemon writes to stderr at `level` while `body` runs.
    member private _.CapturingStderr(level, body: unit -> unit) : string =
        let original = FsHotWatch.Logging.logLevel
        let sb = Text.StringBuilder()
        let writer = new IO.StringWriter(sb)
        let prevErr = Console.Error

        try
            Console.SetError(writer)
            FsHotWatch.Logging.setLogLevel level
            body ()
            sb.ToString()
        finally
            Console.SetError(prevErr)
            FsHotWatch.Logging.setLogLevel original

    [<Fact(Timeout = 5000)>]
    member this.``a successful kill records begin, end, duration and outcome (AUTOMATION-454)``() =
        let logged =
            this.CapturingStderr(
                FsHotWatch.Logging.LogLevel.Info,
                fun () ->
                    killTreeWith (TimeSpan.FromSeconds 7.0) 4242 (fun () -> "`dotnet test Unit` (pid 4242)") id
                    |> ignore
            )

        // BEGIN — which tree, and what it is being given.
        Assert.Contains("killing process tree", logged)
        Assert.Contains("dotnet test Unit", logged)
        Assert.Contains("4242", logged)
        Assert.Contains("teardown budget 7s", logged)
        // END — the outcome, and how long it took to get there.
        Assert.Contains("killed the process tree", logged)
        Assert.Contains("ms", logged)

    [<Fact(Timeout = 5000)>]
    member this.``an already-exited kill is recorded as such, not as a kill we performed (AUTOMATION-454)``() =
        let logged =
            this.CapturingStderr(
                FsHotWatch.Logging.LogLevel.Info,
                fun () ->
                    killTreeWith (TimeSpan.FromSeconds 7.0) 4243 (fun () -> "`dotnet test Db` (pid 4243)") (fun () ->
                        raise (InvalidOperationException()))
                    |> ignore
            )

        Assert.Contains("killing process tree", logged)
        Assert.Contains("had already exited", logged)
        Assert.DoesNotContain("FAILED", logged)

    [<Fact(Timeout = 5000)>]
    member this.``a teardown that never answered is logged at ERROR, naming the tree and the budget (AUTOMATION-454)``
        ()
        =
        let gate = new Threading.ManualResetEventSlim(false)

        let logged =
            this.CapturingStderr(
                FsHotWatch.Logging.LogLevel.Error,
                fun () ->
                    try
                        killTreeWith
                            (TimeSpan.FromMilliseconds 250.0)
                            4244
                            (fun () -> "`dotnet test Integration` (pid 4244)")
                            (fun () -> gate.Wait())
                        |> ignore
                    finally
                        gate.Set()
            )

        // WHAT we could not account for...
        Assert.Contains("dotnet test Integration", logged)
        Assert.Contains("4244", logged)
        // ...WHY we stopped waiting...
        Assert.Contains("TIMED OUT killing", logged)
        Assert.Contains("did not return", logged)
        // ...and what it MEANS, in words that cannot be read as "the runaway is over".
        Assert.Contains("STILL RUNNING", logged)

// ---------------------------------------------------------------------------
// AUTOMATION-279 — THE OUTPUT SINK, AND WHY IT MUST BE FED AS BYTES ARRIVE.
//
// TestPrune used to keep a project's output only in memory and echo a 40-line TAIL of it on
// failure. An integration suite hit its 900s cap four runs running, and all forty lines were
// identical repeated startup logging; the cause ("test shard pool ... already in use by PID
// 18024") had been printed at the HEAD, in the first seconds.
//
// "As output arrives" is the entire contract, not a performance note: the case that most
// needs the evidence is the child SIGKILLed at its timeout, which never reaches any
// end-of-run writer and whose in-memory capture the kill itself truncates. A sink fed from
// the final capture would rebuild the bug.
// ---------------------------------------------------------------------------

/// A sink over a REAL FILE — the production shape (`RunLog.openFor`), not a StringBuilder.
/// A StringBuilder sink would pass the kill test below while an unflushed `StreamWriter`
/// buffer silently lost the same bytes in production, so this writes, flushes and reads back
/// through the filesystem exactly as the daemon does.
let private withFileSink (dir: string) (body: (string -> unit) -> string -> unit) =
    let path = IO.Path.Combine(dir, "child.output.log")

    use stream =
        new IO.FileStream(path, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.Read)

    use writer = new IO.StreamWriter(stream)
    writer.AutoFlush <- true
    body (fun chunk -> writer.Write(chunk: string)) path

[<Fact(Timeout = 20000)>]
let ``runProcessTo keeps what a SIGKILLed child said BEFORE the kill`` () =
    // The child announces its cause and then hangs forever, so the timeout kills its tree
    // and nothing downstream of the process ever runs. A sink fed at the end leaves this
    // file empty — the production bug exactly.
    withTempDir "sink-kill" (fun dir ->
        withFileSink dir (fun sink path ->
            let outcome =
                runProcessTo
                    (Some sink)
                    "sh"
                    "-c \"echo SHARD-POOL-IN-USE-BY-PID-18024; sleep 30\""
                    "."
                    []
                    (ProcessBounds.streaming (TimeSpan.FromMilliseconds 600.0) (TimeSpan.FromSeconds 10.0))

            match outcome with
            | TimedOut _ -> ()
            | other -> Assert.Fail $"expected TimedOut (the kill path is the whole point), got %A{other}"

            Assert.True(IO.File.Exists path, $"no log at %s{path} — a killed run left no evidence at all")
            Assert.Contains("SHARD-POOL-IN-USE-BY-PID-18024", IO.File.ReadAllText path)))

[<Fact(Timeout = 20000)>]
let ``runProcessTo sees the head of a run whose 40-line TAIL has buried it`` () =
    // The incident, reduced: the cause is line 1 and the next 59 are the indistinguishable
    // noise a tail would show instead. Without this, the change could "pass" while surfacing
    // only what the console already surfaced.
    withTempDir "sink-head" (fun dir ->
        withFileSink dir (fun sink path ->
            let script =
                "-c \"echo REAL-CAUSE-AT-HEAD; i=0; while [ $i -lt 59 ]; do echo migration-noise; i=$((i+1)); done\""

            match runProcessTo (Some sink) "sh" script "." [] quick with
            | Succeeded _ -> ()
            | other -> Assert.Fail $"expected Succeeded, got %A{other}"

            let logged = IO.File.ReadAllText path

            Assert.Contains("REAL-CAUSE-AT-HEAD", logged)

            // Positive control: the last 40 non-blank lines — what the console shows — do
            // NOT contain it. If the child's output ever shrank, the assertion above would
            // be passing trivially, and this fails first and says so.
            let tail =
                logged.Split('\n')
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> fun ls -> ls.[max 0 (ls.Length - 40) ..]
                |> String.concat "\n"

            Assert.DoesNotContain("REAL-CAUSE-AT-HEAD", tail)))

[<Fact(Timeout = 20000)>]
let ``runProcessTo streams DURING the run — on disk, mid-flight`` () =
    // The discriminating test for "as it arrives": every other sink test here passes just as
    // happily against a `runProcessTo` that hands over the whole capture at exit. Two things
    // do not survive that mutation:
    //
    //   * WHEN the first chunk reaches the sink (immediately, vs. at exit), and
    //   * whether the bytes are READABLE BY ANOTHER READER while the child still runs —
    //     the difference between `tail -f`-ing a wedged 900s suite and waiting fifteen
    //     minutes to learn nothing.
    //
    // The second also pins `AutoFlush`: without it the bytes sit in a StreamWriter buffer
    // and the on-disk read below comes back empty.
    withTempDir "sink-during" (fun dir ->
        withFileSink dir (fun sink path ->
            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable firstChunkAt = TimeSpan.Zero
            let mutable onDiskAtFirstChunk = ""
            let mutable chunks = 0

            let observing (chunk: string) =
                sink chunk

                if chunks = 0 then
                    firstChunkAt <- sw.Elapsed
                    // A SEPARATE handle, read while the child is still sleeping —
                    // FileShare.Read is what permits it.
                    onDiskAtFirstChunk <-
                        use fs =
                            new IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)

                        use reader = new IO.StreamReader(fs)
                        reader.ReadToEnd()

                chunks <- chunks + 1

            // Prints at once, then lives another 3 s doing nothing.
            let outcome =
                runProcessTo (Some observing) "sh" "-c \"echo early; sleep 3\"" "." [] quick

            let elapsedAtEnd = sw.Elapsed

            match outcome with
            | Succeeded _ -> ()
            | other -> Assert.Fail $"expected Succeeded, got %A{other}"

            Assert.True(chunks > 0, "the sink was never called")

            // Fixture control FIRST: if the child did not actually live ~3s, "the chunk
            // arrived early" is a claim about nothing and everything below is vacuous.
            Assert.True(
                elapsedAtEnd > TimeSpan.FromSeconds 2.5,
                $"fixture broken: the child was meant to live ~3s, ran %.1f{elapsedAtEnd.TotalSeconds}s"
            )

            // Arrived at the START of the run, not at its end.
            Assert.True(
                firstChunkAt < TimeSpan.FromSeconds 1.5,
                $"the first chunk reached the sink after %.1f{firstChunkAt.TotalSeconds}s of a \
                  %.1f{elapsedAtEnd.TotalSeconds}s run — that is buffer-then-write, not streaming"
            )

            // And it was on disk, readable, while the child was still alive.
            Assert.Contains("early", onDiskAtFirstChunk)))

[<Fact(Timeout = 15000)>]
let ``a throwing sink is disabled, never fatal, and never corrupts the capture`` () =
    // A full disk may not fail a test run, and — subtler — a sink failure may not land in
    // the pump's own `failure` latch, which means "the STREAM died" and would downgrade a
    // complete capture to `DrainTimedOut`.
    let mutable calls = 0

    let alwaysThrows _ =
        calls <- calls + 1
        raise (IO.IOException "disk full")

    match runProcessTo (Some alwaysThrows) "sh" "-c \"echo still-captured\"" "." [] quick with
    | Succeeded(ProcessOutput.Drained text) -> Assert.Equal("still-captured", text)
    | other -> Assert.Fail $"expected Succeeded with a COMPLETE (Drained) capture, got %A{other}"

    // Disabled after the first throw rather than retried per chunk — one warning,
    // not one per 4 KB.
    Assert.Equal(1, calls)

[<Fact(Timeout = 15000)>]
let ``runProcess is runProcessTo with no sink`` () =
    // The 5-arg form every other caller uses must stay exactly what it was.
    let withoutSink = runProcessBounded "echo" "same" quick
    let withNoneSink = runProcessTo None "echo" "same" "." [] quick

    match withoutSink, withNoneSink with
    | Succeeded(ProcessOutput.Drained a), Succeeded(ProcessOutput.Drained b) ->
        Assert.Equal("same", a)
        Assert.Equal(a, b)
    | a, b -> Assert.Fail $"expected two identical Succeeded captures, got %A{a} and %A{b}"
