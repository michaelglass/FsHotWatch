module Xunit4RunnerFixture.Tests

open System
open System.Threading
open Xunit

[<Fact>]
let ``xUnit 4 fixture passes`` () = Assert.True true

/// Quick tests that finish before `Hanging` is killed, so a host killed at its timeout
/// has printed completions to read (AUTOMATION-1300). One writes test output, which a
/// `--output Detailed` console must not turn into extra result lines.
type Quick(output: ITestOutputHelper) =
    [<Fact>]
    member _.``first quick``() =
        output.WriteLine "passed Not.A.Result (1ms)"

    [<Fact>]
    member _.``second quick``() = Assert.Equal(2, 1 + 1)

/// Hangs only when `FSHW_FIXTURE_HANG` is set: a real host for a test to kill at its
/// timeout. Unset, it passes at once and the fixture stays a fast green.
type Hanging() =
    [<Fact>]
    member _.``hangs when asked``() =
        if not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "FSHW_FIXTURE_HANG")) then
            Thread.Sleep Timeout.Infinite
