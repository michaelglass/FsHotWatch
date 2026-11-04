/// No wait in the suites measures its deadline on the wall clock (AUTOMATION-1461).
///
/// `waitUntil` read `DateTime.UtcNow` for its deadline, and a loaded gate reported "the
/// awaited condition did not happen within 30000 ms" for a test that took 729 ms. The two
/// clocks disagree when the box sleeps: the gate runs on a laptop that the kernel puts to
/// sleep in the middle of heavy work ("Dark Wake Thermal Emergency" in `pmset -g log`).
/// The wall clock keeps counting through a sleep, so a wait that began before it has run
/// out by the first poll after it, while the `Stopwatch` xUnit times the test with does
/// not count it (CLOCK_UPTIME_RAW on macOS, CLOCK_MONOTONIC on Linux).
///
/// A wait's budget is a hang cap: time the box spent running the work. A frozen process
/// does no work, so the budget is read from `Deadline`, which runs on the `Stopwatch`.
module FsHotWatch.Tests.MonotonicDeadlineTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.Tests.RepoTasks

/// The wall clock's now on the left of a comparison with a name: a loop or a check that
/// tests a deadline (`while … && DateTime.UtcNow < deadline do`, `if … >= deadline then`).
/// A timestamp read for data (`StartedAt <= DateTime.UtcNow`, `Now = fun () -> …`) has the
/// clock on the right, or no comparison, and is not a deadline.
let private wallClockDeadline =
    Regex(@"\bDateTime(Offset)?\.(Utc)?Now\s*(<=|>=|<|>)\s*[A-Za-z_]")

/// Whether `line` tests a deadline against the wall clock. A comment is not code.
let internal testsWallClockDeadline (line: string) =
    not (line.TrimStart().StartsWith "//") && wallClockDeadline.IsMatch line

/// Every wall-clock deadline in `tests/`, as `file:line: code`. This file is left out: its
/// cases spell the shape it looks for.
let private wallClockDeadlines (root: string) =
    filesUnder FsHotWatch.SafeWalk.SourceExcludedDirs "*.fs" (Path.Combine(root, "tests"))
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> not (relative.Contains "/obj/" || relative.Contains "/bin/"))
    |> Seq.filter (fun (relative, _) -> Path.GetFileName relative <> "MonotonicDeadlineTests.fs")
    |> Seq.collect (fun (relative, path) ->
        File.ReadAllLines path
        |> Seq.mapi (fun i line -> i + 1, line)
        |> Seq.filter (snd >> testsWallClockDeadline)
        |> Seq.map (fun (n, line) -> $"%s{relative}:%d{n}: %s{line.Trim()}"))
    |> List.ofSeq

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``no wait in the suites measures its deadline on the wall clock`` () =
    let found = wallClockDeadlines (repoRoot ())

    if not (List.isEmpty found) then
        Assert.Fail(
            "These waits read their deadline off the wall clock, which counts the time the box slept, so a wait \
             that spans a sleep expires at the first poll after it. Use TestHelpers.Deadline (the Stopwatch):\n"
            + String.Join("\n", found)
        )

[<Theory>]
[<InlineData("    while not ok && DateTime.UtcNow < deadline do")>]
[<InlineData("            if DateTime.UtcNow >= deadline then")>]
[<InlineData("        while scheduler.Queued < 8 && System.DateTime.UtcNow < deadline do")>]
[<InlineData("    while DateTimeOffset.Now <= until do")>]
let ``a deadline tested against the wall clock is found`` (line: string) = test <@ testsWallClockDeadline line @>

[<Theory>]
[<InlineData("    test <@ (liveProcessStart self).Value <= DateTime.UtcNow @>")>]
[<InlineData("            { Now = fun () -> DateTime.UtcNow")>]
[<InlineData("    let cutoff = DateTime.UtcNow - olderThan")>]
[<InlineData("    // while DateTime.UtcNow < deadline do")>]
[<InlineData("    while not ok && deadline.Remains do")>]
let ``a timestamp read for data is not a deadline`` (line: string) =
    test <@ not (testsWallClockDeadline line) @>

/// A clock the test moves by hand, as the `Stopwatch` a `Deadline` reads.
type private HandClock() =
    member val Now = TimeSpan.Zero with get, set

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a deadline runs out on the clock it was given, and only then`` () =
    let clock = HandClock()
    let deadline = Deadline.On((fun () -> clock.Now), TimeSpan.FromSeconds 30.0)
    test <@ deadline.Remains @>
    clock.Now <- TimeSpan.FromSeconds 29.9
    test <@ deadline.Remains @>
    clock.Now <- TimeSpan.FromSeconds 30.0
    test <@ not deadline.Remains @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``waitUntilTrue gives up only when its deadline's clock has run out`` () =
    // The poll count is the claim: with the clock held still, a wait whose condition never
    // holds keeps polling, whatever the wall clock does; once the clock passes the budget,
    // it gives up at the next poll.
    let clock = HandClock()
    let mutable polls = 0

    let held =
        waitUntilTrueOn
            (fun budget -> Deadline.On((fun () -> clock.Now), budget))
            (fun () ->
                polls <- polls + 1

                if polls = 5 then
                    clock.Now <- TimeSpan.FromMilliseconds 30_000.0

                false)
            30_000

    test <@ not held && polls = 5 @>
