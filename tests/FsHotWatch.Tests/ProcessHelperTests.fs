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
