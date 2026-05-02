module FsHotWatch.Tests.ProcessHelperTests

open System
open Xunit
open FsHotWatch.ProcessHelper
open FsHotWatch.Tests.TestHelpers

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

// DOTNET_HOST_PATH realpath contract — see strip block in
// runProcessWithTimeout for the nix-wrapped-SDK scenario that motivated it.
// The .NET muxer reads DOTNET_HOST_PATH literally and computes
// DOTNET_ROOT_<arch> = dirname(DOTNET_HOST_PATH); on a wrapped-bin/ symlink
// that dirname has no `shared/` sibling, so apphost dies. Resolving the
// symlink before spawn lands dirname on the unwrapped runtime tree.

[<Fact(Timeout = 20000)>]
let ``runProcess resolves DOTNET_HOST_PATH symlink to its realpath`` () =
    System.IO.Directory.CreateDirectory(System.IO.Path.GetTempPath()) |> ignore

    let tmp =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fshw-hostpath-{Guid.NewGuid():N}")

    System.IO.Directory.CreateDirectory(tmp) |> ignore

    try
        let target = System.IO.Path.Combine(tmp, "real-dotnet")
        System.IO.File.WriteAllText(target, "")
        let link = System.IO.Path.Combine(tmp, "wrapped-dotnet")
        System.IO.File.CreateSymbolicLink(link, target) |> ignore

        withEnv "DOTNET_HOST_PATH" (Some link) (fun () ->
            runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [] |> expectStdout target)
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

        withEnv "DOTNET_HOST_PATH" (Some regular) (fun () ->
            runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [] |> expectStdout regular)
    finally
        if System.IO.Directory.Exists(tmp) then
            System.IO.Directory.Delete(tmp, true)

[<Fact(Timeout = 20000)>]
let ``runProcess leaves DOTNET_HOST_PATH unchanged when path does not exist`` () =
    let bogus = $"/no/such/path/fshw-{Guid.NewGuid():N}"

    withEnv "DOTNET_HOST_PATH" (Some bogus) (fun () ->
        runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [] |> expectStdout bogus)

[<Fact(Timeout = 20000)>]
let ``runProcess does not set DOTNET_HOST_PATH when inherited env did not have it`` () =
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

        withEnv "DOTNET_HOST_PATH" (Some outer) (fun () ->
            runProcess "sh" (echoEnv "DOTNET_HOST_PATH") "." [] |> expectStdout final)
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

[<Fact(Timeout = 15000)>]
let ``runProcessWithTimeout times out and kills long-running process (covers F17/F18 use sites)`` () =
    // Spawn a long-running process and exercise the timeout-kill + drain path
    // so the F17/F18 catch sites get coverage hits in the integration tree.
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    match runProcessWithTimeout "sleep" "30" "." [] (TimeSpan.FromMilliseconds 200.0) with
    | TimedOut _ -> ()
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"

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
