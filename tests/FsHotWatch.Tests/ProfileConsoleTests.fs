/// `Profile`'s second source of class lines: the per-test result lines the host prints as
/// each test finishes (`passed <name> (<duration>)`), read off the output stream fshw
/// already tees into the run log. A host killed at its timeout writes no CTRF report, but
/// everything it printed before the kill was seen as it arrived (AUTOMATION-1300).
module FsHotWatch.Tests.ProfileConsoleTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune

let private at (seconds: float) =
    DateTime(2026, 10, 21, 3, 0, 0, DateTimeKind.Utc).AddSeconds seconds

let private tempRunDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fshw-profile-console-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

[<Theory>]
[<InlineData("passed Ns.Mod.fast one (4ms)", "passed", "Ns.Mod.fast one", 4L)>]
[<InlineData("failed Ns.Mod.slow (10s 112ms)", "failed", "Ns.Mod.slow", 10112L)>]
[<InlineData("failed (canceled) Ns.Mod.hung (2m 3s 4ms)", "failed", "Ns.Mod.hung", 123004L)>]
[<InlineData("skipped Ns.Mod.later (0ms)", "skipped", "Ns.Mod.later", 0L)>]
[<InlineData("passed Ns.Mod.trailing (1h 1ms)  \r", "passed", "Ns.Mod.trailing", 3600001L)>]
[<InlineData("\u001b[32mpassed\u001b[m Ns.Mod.coloured (7ms)", "passed", "Ns.Mod.coloured", 7L)>]
[<InlineData("passed Ns.Mod.theory(x: \"a (b)\") (1.5s)", "passed", "Ns.Mod.theory(x: \"a (b)\")", 1500L)>]
let ``parseCompletion reads the runner's per-test result line``
    (line: string, outcome: string, name: string, ms: int64)
    =
    test <@ Profile.parseCompletion line = Some(outcome, name, ms) @>

[<Theory>]
[<InlineData("")>]
[<InlineData("passed")>]
[<InlineData("passed the gate twice (and then some)")>]
[<InlineData("Test run summary: Passed! - x.dll (net10.0|arm64)")>]
[<InlineData("  succeeded: 3")>]
[<InlineData("failed to start the host")>]
// Indented: the "Standard output" block a Detailed console prints under a passed test.
[<InlineData("    passed Not.A.Result (1ms)")>]
let ``parseCompletion ignores everything that is not a result line`` (line: string) =
    test <@ Profile.parseCompletion line = None @>

[<Theory>]
[<InlineData("Ns.Mod.fast one", "Ns.Mod")>]
[<InlineData("Ns.Mod.method", "Ns.Mod")>]
[<InlineData("Ns.Mod.reads .fshw.json when it changes", "Ns.Mod")>]
[<InlineData("Ns.Outer+Inner.method(x: 1.5)", "Ns.Outer+Inner")>]
[<InlineData("NoDots", "NoDots")>]
let ``classOfTestName is the longest dotted prefix with no space or argument list in it``
    (name: string, expected: string)
    =
    test <@ Profile.classOfTestName name = expected @>

let private recorder (dir: string) =
    Profile.start dir "Killed.Tests" (Some 2) 4 (fun () -> Some 2.0) (fun _ -> None) Threading.Timeout.InfiniteTimeSpan

let private classLines (dir: string) =
    Profile.read (Profile.pathIn dir "Killed.Tests")
    |> List.choose (function
        | Profile.Class(t, _, source) -> Some(t, source)
        | _ -> None)

[<Fact>]
let ``a killed host's printed completions become class lines, each test ending when its line arrived`` () =
    let dir = tempRunDir ()
    let r = recorder dir

    // Chunks split anywhere, a line among them that is not a result, lines arriving
    // at different moments.
    Profile.observeAt r (at 10.0) "banner\n\npassed A.Slow.one (4s)\npass"

    Profile.observeAt
        r
        (at 16.0)
        "ed A.Slow.two (6s)\r\n  Standard output\n    passed Not.A.Result (1ms)\npassed B.Fast.one (20ms)\n"
    // Cut mid-line by the kill: never a completion.
    Profile.observeAt r (at 17.0) "passed B.Fast.two (3"

    Profile.finish r "timed-out" (Error "no report: the host was killed at its 2s timeout")

    let classes = classLines dir

    let slow, slowSource = classes |> List.find (fun (t, _) -> t.Class = "A.Slow")

    test <@ slowSource = Profile.ClassSource.Console @>
    test <@ slow.Tests = 2 @>
    test <@ slow.DurationMs = 10_000L @>
    // `one` ended at 10 s after 4 s; `two` arrived at 16 s.
    test <@ slow.Start = at 6.0 @>
    test <@ slow.Stop = at 16.0 @>

    let fast, _ = classes |> List.find (fun (t, _) -> t.Class = "B.Fast")
    test <@ fast.Tests = 1 @>
    test <@ fast.DurationMs = 20L @>

    // Slowest span first, as the report's lines are; the indented test output is no class.
    test <@ classes |> List.map (fun (t, _) -> t.Class) = [ "A.Slow"; "B.Fast" ] @>

    test
        <@
            Profile.read (Profile.pathIn dir "Killed.Tests")
            |> List.forall (function
                | Profile.ClassesUnavailable _ -> false
                | _ -> true)
        @>

[<Fact>]
let ``a report, when there is one, decides the class lines and the console is not consulted`` () =
    let dir = tempRunDir ()
    let r = recorder dir
    Profile.observeAt r (at 1.0) "passed Console.Only.one (5ms)\n"

    let timing: Profile.ClassTiming =
        { Class = "From.Report"
          Tests = 1
          DurationMs = 5L
          Start = at 0.0
          Stop = at 0.005 }

    Profile.finish r "passed" (Ok [ timing ])

    test <@ classLines dir |> List.map (fun (t, s) -> t.Class, s) = [ "From.Report", Profile.ClassSource.Report ] @>

[<Fact>]
let ``no report and no printed completion still says why there are no class lines`` () =
    let dir = tempRunDir ()
    let r = recorder dir
    Profile.observeAt r (at 1.0) "banner only, and a cut-off passed X.Y.z (1"
    Profile.finish r "timed-out" (Error "no report: the host was killed at its 2s timeout")

    let last = Profile.read (Profile.pathIn dir "Killed.Tests") |> List.last

    match last with
    | Profile.ClassesUnavailable reason ->
        test <@ reason.StartsWith "no report: the host was killed at its 2s timeout" @>
        test <@ reason.Contains "no per-test result line" @>
    | other -> failwith $"unexpected last line: %A{other}"

[<Fact>]
let ``output after finish is ignored, and a line with no end is never buffered without bound`` () =
    let dir = tempRunDir ()
    let r = recorder dir
    // Far past the partial-line cap: dropped, not held.
    Profile.observeAt r (at 1.0) (String('x', 1_000_000))
    Profile.observeAt r (at 2.0) "\npassed A.B.c (1ms)\n"
    Profile.finish r "timed-out" (Error "no report")
    Profile.observeAt r (at 3.0) "passed Late.Class.d (1ms)\n"

    test <@ classLines dir |> List.map (fun (t, _) -> t.Class) = [ "A.B" ] @>

[<Fact>]
let ``a console class line round-trips with its source`` () =
    let timing: Profile.ClassTiming =
        { Class = "A.B"
          Tests = 2
          DurationMs = 9L
          Start = at 0.0
          Stop = at 1.0 }

    let line =
        Profile.Class(timing, Profile.contentionOver 4 [] (timing.Start, timing.Stop), Profile.ClassSource.Console)

    test <@ Profile.tryParse (Profile.render line) = Some line @>
    test <@ (Profile.render line).Contains "\"source\":\"console\"" @>

// --- the test running at the kill: xUnit's long-running test reports ---

[<Theory>]
[<InlineData("[Long Running Test] 'Ns.Hung.stuck', Elapsed: 00:00:03", "Ns.Hung.stuck", 3000L)>]
[<InlineData("[Long Running Test] 'Ns.C.it''s quoted', Elapsed: 01:02:05\r", "Ns.C.it''s quoted", 3725000L)>]
let ``parseLongRunning reads xUnit's long-running test report`` (line: string, name: string, ms: int64) =
    test <@ Profile.parseLongRunning line = Some(name, ms) @>

[<Theory>]
[<InlineData("")>]
[<InlineData("  [Long Running Test] 'Ns.C.m', Elapsed: 00:00:03")>]
[<InlineData("[Long Running Test] 'Ns.C.m', Elapsed: soon")>]
[<InlineData("passed Ns.C.m (3ms)")>]
let ``parseLongRunning ignores every other line`` (line: string) =
    test <@ Profile.parseLongRunning line = None @>

let private runningLines (dir: string) =
    Profile.read (Profile.pathIn dir "Killed.Tests")
    |> List.choose (function
        | Profile.Running(name, since) -> Some(name, since)
        | _ -> None)

[<Fact>]
let ``a killed host's test that was reported long-running and never finished was running at the kill`` () =
    let dir = tempRunDir ()
    let r = recorder dir

    Profile.observeAt r (at 10.0) "[Long Running Test] 'Ns.Hung.stuck', Elapsed: 00:00:04\n"
    Profile.observeAt r (at 10.0) "[Long Running Test] 'Ns.Slow.done', Elapsed: 00:00:04\n"
    // The newest report of a test decides when it started.
    Profile.observeAt r (at 20.0) "[Long Running Test] 'Ns.Hung.stuck', Elapsed: 00:00:15\n"
    // Reported long-running, then finished: not running at the kill.
    Profile.observeAt r (at 21.0) "passed Ns.Slow.done (16s)\n"

    Profile.finish r "timed-out" (Error "no report: the host was killed at its 2s timeout")

    test <@ runningLines dir = [ "Ns.Hung.stuck", at 5.0 ] @>

[<Fact>]
let ``a run with a report has nothing running at its end`` () =
    let dir = tempRunDir ()
    let r = recorder dir
    Profile.observeAt r (at 10.0) "[Long Running Test] 'Ns.Slow.t', Elapsed: 00:00:04\n"
    Profile.finish r "passed" (Ok [])
    test <@ List.isEmpty (runningLines dir) @>

[<Fact>]
let ``a running line round-trips`` () =
    let line = Profile.Running("Ns.Hung.stuck", at 5.0)
    test <@ Profile.tryParse (Profile.render line) = Some line @>
    test <@ Profile.tryParse """{"event":"running","test":"x"}""" = None @>
