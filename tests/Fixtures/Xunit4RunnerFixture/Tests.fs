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

/// Fails, only when `FSHW_FIXTURE_POOL_PROBE` is set, while a thread-pool thread is held by
/// work other than this test's own: what a test that caps the pool relies on (a starvation
/// test caps it at two workers and needs both). A diagnostic fshw injects into a launch must
/// not hold one (AUTOMATION-1353: xUnit's `--long-running` monitor holds one for the whole
/// run). The monitor is queued at assembly start and may not have been picked up yet when
/// this test begins, so the probe first lets half a second pass; work merely passing through
/// (the runner's reporting, a sibling quick test) is not held, so the count is then
/// re-sampled for up to a second and the lowest reading wins.
type PoolProbe() =
    [<Fact>]
    member _.``no pool thread is held by other work``() =
        if not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "FSHW_FIXTURE_POOL_PROBE")) then
            let maxWorkers, _ = ThreadPool.GetMaxThreads()
            let own = if Thread.CurrentThread.IsThreadPoolThread then 1 else 0

            let held () =
                let available, _ = ThreadPool.GetAvailableThreads()
                maxWorkers - available - own

            let rec settle attemptsLeft lowest =
                if lowest <= 0 || attemptsLeft = 0 then
                    lowest
                else
                    Thread.Sleep 10
                    settle (attemptsLeft - 1) (min lowest (held ()))

            Thread.Sleep 500
            let lowest = settle 100 (held ())
            Assert.True(lowest <= 0, $"%d{lowest} pool thread(s) held by other work for a whole second")
