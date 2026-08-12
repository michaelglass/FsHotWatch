module FsHotWatch.Tests.ProcessHelperTimeoutTests

open System
open Xunit
open FsHotWatch.ProcessHelper

// Spawns a real long-running process to force ProcessHelper's timeout-kill + drain
// path (the Kill catch arm, the post-kill Task.WaitAll drain catch, and the
// stdoutTask/stderrTask IsCompletedSuccessfully else-branches). Whether those lines
// are hit depends on OS scheduling, so in the unit suite they made ProcessHelper.fs
// coverage jitter run-to-run. It lives here (no coverage package) to keep that
// metric deterministic while still testing the behavior.
[<Fact(Timeout = 15000)>]
let ``runProcess times out and kills long-running process (covers F17/F18 use sites)`` () =
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    match runProcess "sleep" "30" "." [] (ProcessBounds.silent (TimeSpan.FromMilliseconds 200.0)) with
    | TimedOut _ -> ()
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"
