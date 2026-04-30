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
