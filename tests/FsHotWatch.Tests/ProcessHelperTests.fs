module FsHotWatch.Tests.ProcessHelperTests

open System
open Xunit
open FsHotWatch.ProcessHelper

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

[<Fact(Timeout = 15000)>]
let ``runProcessWithTimeout registers the child while running and unregisters on exit`` () =
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    Assert.Empty(FsHotWatch.ProcessRegistry.snapshot ())

    Assert.True(isSucceeded (runProcess "echo" "hi" "." []))

    // Post-exit, the registry has unregistered — exited PIDs the OS could later
    // recycle must not linger.
    Assert.Empty(FsHotWatch.ProcessRegistry.snapshot ())

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

// Regression: a daemon-spawned child must inherit the daemon process's
// environment. Reported under a Nix toolchain where DOTNET_ROOT and
// NIX_PROFILES are needed by `dotnet run` invoked from a beforeRun hook.
// FsHotWatch sets specific keys via psi.Environment[k] <- v on top of the
// inherited dictionary; it never clears or replaces it. This test pins
// that contract so a future "scrub the env" change can't silently break
// Nix users.
[<Fact(Timeout = 10000)>]
let ``runProcess inherits the parent process environment (no scrubbing)`` () =
    let key = "FSHOTWATCH_ENV_PASSTHROUGH_PROBE"
    let value = "nix-store-path-marker-" + Guid.NewGuid().ToString("N")
    Environment.SetEnvironmentVariable(key, value)

    try
        // sh -c "printf %s \"$VAR\"" — printf avoids trailing newline noise.
        let args = sprintf "-c \"printf %%s \\\"$%s\\\"\"" key

        match runProcess "sh" args "." [] with
        | Succeeded out -> Assert.Equal(value, out)
        | other -> Assert.Fail $"expected Succeeded, got %A{other}"
    finally
        Environment.SetEnvironmentVariable(key, null)

// On Nix, the .NET host writes DOTNET_ROOT_<ARCH> into the daemon's env
// pointing at argv[0]'s dir. With a wrapped SDK, that dir lacks
// shared/Microsoft.NETCore.App, so any later `dotnet run` spawned from a
// hook trusts the inherited DOTNET_ROOT_ARM64 and aborts with
// "App host version: X.Y.Z … .NET location: Not found". Interactive shells
// dodge this because each dotnet invocation re-resolves; the daemon's
// long life pins the bad value.
//
// Fix: strip DOTNET_ROOT_<ARCH> from the spawn env when the child will
// invoke dotnet (directly, or via `sh -c "dotnet …"`). The host re-resolves
// from argv[0], so dropping even valid values is safe.

[<Fact(Timeout = 5000)>]
let ``invokesDotnet matches dotnet and shell-wrapped dotnet`` () =
    Assert.True(invokesDotnet "dotnet" "build")
    Assert.True(invokesDotnet "/usr/local/share/dotnet/dotnet" "--info")
    Assert.True(invokesDotnet "sh" "-c \"dotnet run --project foo\"")
    Assert.True(invokesDotnet "/bin/sh" "-c \"cd x && dotnet test\"")
    Assert.True(invokesDotnet "bash" "-c \"dotnet build\"")

[<Fact(Timeout = 5000)>]
let ``invokesDotnet rejects non-dotnet commands`` () =
    Assert.False(invokesDotnet "sh" "-c \"echo hi\"")
    Assert.False(invokesDotnet "sh" "-c \"make build\"")
    Assert.False(invokesDotnet "cat" "/etc/hosts")
    Assert.False(invokesDotnet "echo" "dotnet")

[<Fact(Timeout = 10000)>]
let ``runProcess strips DOTNET_ROOT_ARM64 when child invokes dotnet via sh -c`` () =
    Environment.SetEnvironmentVariable("DOTNET_ROOT_ARM64", "/poisoned/wrapped/bin")

    try
        // sh -c "dotnet --version >/dev/null 2>&1 || true; printf %s \"$DOTNET_ROOT_ARM64\""
        // We don't care if dotnet actually runs; we only need the predicate to fire on
        // the args. The `printf` reports what the child received in its env.
        let args =
            "-c \"dotnet --version >/dev/null 2>&1 || true; printf %s \\\"$DOTNET_ROOT_ARM64\\\"\""

        match runProcess "sh" args "." [] with
        | Succeeded out -> Assert.Equal("", out)
        | other -> Assert.Fail $"expected Succeeded, got %A{other}"
    finally
        Environment.SetEnvironmentVariable("DOTNET_ROOT_ARM64", null)

[<Fact(Timeout = 10000)>]
let ``runProcess strips DOTNET_ROOT_X64 and DOTNET_ROOT_X86 too`` () =
    Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", "/poisoned/x64")
    Environment.SetEnvironmentVariable("DOTNET_ROOT_X86", "/poisoned/x86")

    try
        let args =
            "-c \"dotnet --version >/dev/null 2>&1 || true; printf %s:%s \\\"$DOTNET_ROOT_X64\\\" \\\"$DOTNET_ROOT_X86\\\"\""

        match runProcess "sh" args "." [] with
        | Succeeded out -> Assert.Equal(":", out)
        | other -> Assert.Fail $"expected Succeeded, got %A{other}"
    finally
        Environment.SetEnvironmentVariable("DOTNET_ROOT_X64", null)
        Environment.SetEnvironmentVariable("DOTNET_ROOT_X86", null)

[<Fact(Timeout = 10000)>]
let ``runProcess preserves plain DOTNET_ROOT (only arch-specific is stripped)`` () =
    let probe = "/some/intentional/dotnet/root-" + Guid.NewGuid().ToString("N")
    Environment.SetEnvironmentVariable("DOTNET_ROOT", probe)

    try
        let args = "-c \"dotnet --version >/dev/null 2>&1 || true; printf %s \\\"$DOTNET_ROOT\\\"\""

        match runProcess "sh" args "." [] with
        | Succeeded out -> Assert.Equal(probe, out)
        | other -> Assert.Fail $"expected Succeeded, got %A{other}"
    finally
        Environment.SetEnvironmentVariable("DOTNET_ROOT", null)

[<Fact(Timeout = 10000)>]
let ``runProcess does NOT strip DOTNET_ROOT_ARM64 for non-dotnet children`` () =
    let probe = "/inherited/arm64-" + Guid.NewGuid().ToString("N")
    Environment.SetEnvironmentVariable("DOTNET_ROOT_ARM64", probe)

    try
        // No dotnet token — predicate must not fire.
        let args = "-c \"printf %s \\\"$DOTNET_ROOT_ARM64\\\"\""

        match runProcess "sh" args "." [] with
        | Succeeded out -> Assert.Equal(probe, out)
        | other -> Assert.Fail $"expected Succeeded, got %A{other}"
    finally
        Environment.SetEnvironmentVariable("DOTNET_ROOT_ARM64", null)

// Companion: explicit env entries must overlay (not replace) the inherited env.
// If a future refactor switches to psi.Environment.Clear() + setters, this test
// fails because the inherited probe disappears.
[<Fact(Timeout = 10000)>]
let ``runProcess overlays explicit env on top of inherited env`` () =
    let inheritedKey = "FSHOTWATCH_ENV_PASSTHROUGH_INHERITED"
    let inheritedValue = "inherited-" + Guid.NewGuid().ToString("N")
    let explicitKey = "FSHOTWATCH_ENV_PASSTHROUGH_EXPLICIT"
    let explicitValue = "explicit-" + Guid.NewGuid().ToString("N")

    Environment.SetEnvironmentVariable(inheritedKey, inheritedValue)

    try
        let args =
            sprintf "-c \"printf %%s:%%s \\\"$%s\\\" \\\"$%s\\\"\"" inheritedKey explicitKey

        match runProcess "sh" args "." [ explicitKey, explicitValue ] with
        | Succeeded out -> Assert.Equal(inheritedValue + ":" + explicitValue, out)
        | other -> Assert.Fail $"expected Succeeded, got %A{other}"
    finally
        Environment.SetEnvironmentVariable(inheritedKey, null)
