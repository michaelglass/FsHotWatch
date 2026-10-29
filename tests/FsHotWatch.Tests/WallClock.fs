namespace FsHotWatch.Tests

open System

/// The FSHW-WALL-001 allowlist: a test, member, type or module whose REAL time is the point.
///
/// FSHW-WALL-001 (analyzers/FsHotWatch.Rules/WallClockAnalyzer.fs) flags, in test sources,
/// a sleep (`Thread.Sleep` / `Task.Delay` / `Async.Sleep`), an assertion that reads a
/// stopwatch or elapsed time, and an asserted wait whose budget is a literal. Each measures
/// the box the suite runs on, and on a box at load 50–140 that fails tests on load rather
/// than on behaviour. The fix is an injected clock (`FakeClock.ManualClock`) or a fake host,
/// and an assertion on what happened.
///
/// Some tests cannot take that fix, and this attribute says so where the test is declared:
///
///   - a genuine integration test of a real host (a real child, a real signal, a real
///     filesystem event), whose claim is about the real OS;
///   - a negative window: nothing may happen within N ms (load only delays events, so it can
///     hide a defect on a slow box but cannot fail a correct one);
///   - a polling primitive's interval between polls, which is never the claim;
///   - simulated work whose duration only orders events the test then waits for.
///
/// The reason is REQUIRED and the rule checks it: `[<WallClock>]` or `[<WallClock("")>]`
/// allows nothing and is itself a finding. `rg 'WallClock\("'` lists every exception with
/// its justification; docs/wall-clock-bounds.md is the policy.
[<AttributeUsage(AttributeTargets.All, AllowMultiple = false)>]
type WallClockAttribute(reason: string) =
    inherit Attribute()

    /// Why real time is the point of the test this attribute marks.
    member _.Reason = reason
