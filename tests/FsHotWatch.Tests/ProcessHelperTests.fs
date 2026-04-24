module FsHotWatch.Tests.ProcessHelperTests

open System
open Xunit
open FsHotWatch.ProcessHelper

[<Fact(Timeout = 10000)>]
let ``runProcessWithTimeout kills child when exceeded`` () =
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let success, output =
        runProcessWithTimeout "sleep" "10" "." [] (TimeSpan.FromMilliseconds 200.0)

    sw.Stop()
    Assert.False success
    Assert.Contains("timed out", output)
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds 3.0, $"took {sw.Elapsed}")

[<Fact(Timeout = 10000)>]
let ``runProcessWithTimeout returns normally when fast`` () =
    let success, output =
        runProcessWithTimeout "echo" "hi" "." [] (TimeSpan.FromSeconds 5.0)

    Assert.True success
    Assert.Equal("hi", output.Trim())

[<Fact(Timeout = 10000)>]
let ``runWithTimeout returns Ok when work completes in time`` () =
    let result = runWithTimeout (TimeSpan.FromSeconds 1.0) (fun () -> 42)
    Assert.Equal(Ok 42, result)

[<Fact(Timeout = 10000)>]
let ``runWithTimeout returns Error TimedOut when work exceeds timeout`` () =
    let result =
        runWithTimeout (TimeSpan.FromMilliseconds 50.0) (fun () ->
            System.Threading.Thread.Sleep 1000
            42)

    match result with
    | Error msg -> Assert.Contains("timed out", msg.ToLowerInvariant())
    | Ok _ -> Assert.Fail "expected timeout"

[<Fact(Timeout = 10000)>]
let ``runWithTimeout with InfiniteTimeSpan never times out`` () =
    let result = runWithTimeout System.Threading.Timeout.InfiniteTimeSpan (fun () -> 7)
    Assert.Equal(Ok 7, result)
