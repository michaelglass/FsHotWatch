module FsHotWatch.Tests.ProcessHelperTimeoutTests

open System
open Xunit
open FsHotWatch.ProcessHelper

// Moved here from FsHotWatch.Tests/ProcessHelperTests.fs. This test spawns a
// real long-running process to force the timeout-kill + drain path
// (ProcessHelper.fs ~lines 157-196: the Kill catch arm, the post-kill
// Task.WaitAll drain catch, and the stdoutTask/stderrTask
// IsCompletedSuccessfully else-branches). Those lines are SOMETIMES-covered
// depending on OS scheduling, which made the unit-suite line coverage of
// ProcessHelper.fs jitter run-to-run. Living in the integration suite (no
// coverage package) keeps the behavior tested but out of the metric, so the
// unit coverage of ProcessHelper.fs is deterministic.
[<Fact(Timeout = 15000)>]
let ``runProcess times out and kills long-running process (covers F17/F18 use sites)`` () =
    // Spawn a long-running process and exercise the timeout-kill + drain path
    // so the F17/F18 catch sites get coverage hits in the integration tree.
    use _ = FsHotWatch.ProcessRegistry.install (FsHotWatch.ProcessRegistry.Registry())

    match runProcess "sleep" "30" "." [] (ProcessBounds.silent (TimeSpan.FromMilliseconds 200.0)) with
    | TimedOut _ -> ()
    | other -> Assert.Fail $"expected TimedOut, got %A{other}"
