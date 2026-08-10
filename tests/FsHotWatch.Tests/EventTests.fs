module FsHotWatch.Tests.EventTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = 15000)>]
let ``FileChangeKind constructors work`` () =
    let source = SourceChanged [ "src/Lib.fs" ]
    let proj = ProjectChanged [ "src/Lib.fsproj" ]
    let sln = SolutionChanged

    test
        <@
            match source with
            | SourceChanged files -> files.Length = 1
            | _ -> false
        @>

    test
        <@
            match proj with
            | ProjectChanged _ -> true
            | _ -> false
        @>

    test
        <@
            match sln with
            | SolutionChanged -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``PluginStatus constructors work`` () =
    let idle = Idle
    let running = Running(since = System.DateTime.UtcNow)

    test
        <@
            match idle with
            | Idle -> true
            | _ -> false
        @>

    test
        <@
            match running with
            | Running _ -> true
            | _ -> false
        @>

// --- path/hash wrappers ---

[<Fact(Timeout = 15000)>]
let ``AbsFilePath round-trips and normalizes`` () =
    let p = AbsFilePath.create "foo.fs"
    test <@ System.IO.Path.IsPathRooted(AbsFilePath.value p) @>

[<Fact(Timeout = 15000)>]
let ``AbsProjectPath round-trips`` () =
    let p = AbsProjectPath.create "x.fsproj"
    test <@ AbsProjectPath.value p |> System.IO.Path.IsPathRooted @>

[<Fact(Timeout = 15000)>]
let ``ContentHash round-trips`` () =
    let h = ContentHash.create "abc123"
    test <@ ContentHash.value h = "abc123" @>

// --- PluginStatus predicates ---

[<Fact(Timeout = 15000)>]
let ``isTerminal is true for Completed and Failed, false for Idle and Running`` () =
    let now = System.DateTime.UtcNow
    test <@ PluginStatus.isTerminal (completedAt now) @>
    test <@ PluginStatus.isTerminal (failedAt "err" now) @>
    test <@ not (PluginStatus.isTerminal Idle) @>
    test <@ not (PluginStatus.isTerminal (Running(since = now))) @>

[<Fact(Timeout = 15000)>]
let ``isQuiescent is true for Idle, Completed and Failed, false for Running`` () =
    let now = System.DateTime.UtcNow
    test <@ PluginStatus.isQuiescent Idle @>
    test <@ PluginStatus.isQuiescent (completedAt now) @>
    test <@ PluginStatus.isQuiescent (failedAt "err" now) @>
    test <@ not (PluginStatus.isQuiescent (Running(since = now))) @>

// --- TestResult helpers ---

[<Fact(Timeout = 15000)>]
let ``TestResult.output returns the output string for both cases`` () =
    test <@ TestResult.output (TestsPassed("ok", false, TimeSpan.Zero)) = "ok" @>
    test <@ TestResult.output (TestsFailed("bad", true, TimeSpan.Zero)) = "bad" @>

[<Fact(Timeout = 15000)>]
let ``TestResult.wasFiltered reflects the filter flag`` () =
    test <@ not (TestResult.wasFiltered (TestsPassed("ok", false, TimeSpan.Zero))) @>
    test <@ TestResult.wasFiltered (TestsPassed("ok", true, TimeSpan.Zero)) @>
    test <@ TestResult.wasFiltered (TestsFailed("bad", true, TimeSpan.Zero)) @>

[<Fact(Timeout = 15000)>]
let ``TestResult.isPassed distinguishes Passed and Failed`` () =
    test <@ TestResult.isPassed (TestsPassed("ok", false, TimeSpan.Zero)) @>
    test <@ not (TestResult.isPassed (TestsFailed("bad", false, TimeSpan.Zero))) @>

// Issue 1: the dedicated TestsDeferred case (apphost missing → tests never
// ran). It must thread correctly through every TestResult helper.
[<Fact(Timeout = 15000)>]
let ``TestResult helpers handle the TestsDeferred case`` () =
    let deferred = TestsDeferred "apphost not produced; tests did not run"
    // output returns the reason.
    test <@ TestResult.output deferred = "apphost not produced; tests did not run" @>
    // A deferred project never ran — treat as filtered so it can't be classed
    // a full-suite run that lowers a coverage baseline.
    test <@ TestResult.wasFiltered deferred @>
    // No wall-clock duration.
    test <@ TestResult.elapsed deferred = TimeSpan.Zero @>
    // CRITICAL: must NEVER count as passed — a never-ran project cannot produce
    // a green verdict.
    test <@ not (TestResult.isPassed deferred) @>
    // It's deferred, not a timeout.
    test <@ not (TestResult.isTimedOut deferred) @>
    test <@ TestResult.isDeferred deferred @>
    // Real results are not deferred.
    test <@ not (TestResult.isDeferred (TestsPassed("ok", false, TimeSpan.Zero))) @>
    test <@ not (TestResult.isDeferred (TestsFailed("bad", false, TimeSpan.Zero))) @>

// --- RunVerification.ofResults: the ONE derivation, and the states it can reach
//
// This replaced `TestResult.ranFullSuite : _ -> bool` in AUTOMATION-282. The bool
// answered a question that has no honest yes/no for a run that executed nothing,
// so the cases below are what it could not say. Each asserts the WHOLE value, not
// a predicate over it — a test that only asks "is it full suite?" would pass
// equally on NoProjectsSelected and NothingExecuted, which is the confusion the
// type exists to end.

[<Fact(Timeout = 15000)>]
let ``ofResults: no projects selected — nothing was invoked`` () =
    // Formerly "ranFullSuite is true for an empty map", justified as "nothing was
    // filtered". That is `Map.forall`'s vacuity, not evidence, and there is now a
    // case that says what actually happened.
    test <@ RunVerification.ofResults Map.empty = NoProjectsSelected @>
    test <@ RunVerification.scope (RunVerification.ofResults Map.empty) = None @>

[<Fact(Timeout = 15000)>]
let ``ofResults: projects that all matched zero tests keep their count`` () =
    let results =
        Map.ofList [ "A", TestsNoMatch("", TimeSpan.Zero); "B", TestsNoMatch("", TimeSpan.Zero) ]

    test <@ RunVerification.ofResults results = AllZeroMatch 2 @>

[<Fact(Timeout = 15000)>]
let ``ofResults: projects reported but not one executed`` () =
    // The case that did not exist before 282: non-empty, not all-no-match, and
    // still nothing ran. It used to answer `Ran`, so `verifiedNothing` was false
    // for a run that verified nothing.
    let results =
        Map.ofList
            [ "A", TestsDeferred "apphost not produced"
              "B", TestsErrored "no parseable report" ]

    test <@ RunVerification.ofResults results = NothingExecuted @>
    test <@ RunVerification.verifiedNothing (RunVerification.ofResults results) @>

[<Fact(Timeout = 15000)>]
let ``ofResults: a deferred project alongside a real one is PARTIAL, never full`` () =
    // A project that never ran must not let the run claim the whole suite.
    let results =
        Map.ofList
            [ "A", TestsPassed("ok", false, TimeSpan.Zero)
              "B", TestsDeferred "apphost not produced" ]

    test <@ RunVerification.ofResults results = Ran Partial @>

[<Fact(Timeout = 15000)>]
let ``ofResults: every project executed unfiltered is the ONLY full suite`` () =
    // The positive control for all of the above: the guard is not a blanket. A
    // genuine unfiltered run must still reach FullSuite, or gating would be dead
    // everywhere rather than honest.
    let results =
        Map.ofList
            [ "A", TestsPassed("ok", false, TimeSpan.Zero)
              "B", TestsFailed("fail", false, TimeSpan.Zero) ]

    test <@ RunVerification.ofResults results = Ran FullSuite @>
    test <@ RunVerification.ranFullSuite (RunVerification.ofResults results) @>

[<Fact(Timeout = 15000)>]
let ``ofResults: one filtered project makes the run partial`` () =
    let results =
        Map.ofList
            [ "A", TestsPassed("ok", false, TimeSpan.Zero)
              "B", TestsPassed("ok", true, TimeSpan.Zero) ]

    test <@ RunVerification.ofResults results = Ran Partial @>
    test <@ not (RunVerification.ranFullSuite (RunVerification.ofResults results)) @>

[<Fact(Timeout = 15000)>]
let ``wire tokens round-trip, and scope is unreachable without having run`` () =
    // The contract both ends of the IPC match on. Every case must survive the
    // round trip, or a daemon and a CLI disagree silently.
    for v in
        [ NoProjectsSelected
          AllZeroMatch 0
          NothingExecuted
          Ran Partial
          Ran FullSuite ] do
        test <@ RunVerification.tryParse (RunVerification.token v) = Some v @>

    // The pre-282 token asserted "tests ran" while saying nothing about breadth.
    // It must not parse: the missing half used to be supplied by a bool that could
    // claim a full suite for a run that executed nothing.
    test <@ RunVerification.tryParse "ran" = None @>

// --- RunVerdict: a content-free ✓ is unconstructible (AUTOMATION-99) ---------
//
// The representation is private, so `RunVerdict.create` is the ONLY way to
// obtain a value — daemon, cache deserializer, CLI, test helper, example. These
// pin that it refuses a verdict that says nothing.

[<Fact(Timeout = 15000)>]
let ``RunVerdict.create rejects an empty summary`` () =
    raises<System.ArgumentException> <@ RunVerdict.create "" (TimeSpan.FromSeconds 1.0) @>

[<Fact(Timeout = 15000)>]
let ``RunVerdict.create rejects a whitespace-only summary`` () =
    raises<System.ArgumentException> <@ RunVerdict.create "   \t\n " TimeSpan.Zero @>

[<Fact(Timeout = 15000)>]
let ``RunVerdict.create keeps the summary and elapsed it was given`` () =
    let v = RunVerdict.create "6 passed, 0 failed" (TimeSpan.FromSeconds 12.5)
    test <@ v.Summary = "6 passed, 0 failed" @>
    test <@ v.Elapsed = TimeSpan.FromSeconds 12.5 @>

[<Fact(Timeout = 15000)>]
let ``a zero elapsed is honest (nothing measurable ran), an empty summary is not`` () =
    // Zero duration is a legitimate measurement (a cache replay, a no-op cycle);
    // "I have nothing to say about what I did" never is.
    let v = RunVerdict.create "0 files checked" TimeSpan.Zero
    test <@ v.Elapsed = TimeSpan.Zero @>

[<Fact(Timeout = 15000)>]
let ``PluginStatus.completedNow and failedNow carry their verdict`` () =
    match PluginStatus.completedNow "did a thing" (TimeSpan.FromSeconds 2.0) with
    | Completed(_, v) ->
        test <@ v.Summary = "did a thing" @>
        test <@ v.Elapsed = TimeSpan.FromSeconds 2.0 @>
    | other -> failwithf "expected Completed, got %A" other

    // Failed carries BOTH: the error is the diagnosis, the verdict is the
    // one-line human summary + the measured duration. That is what stops
    // RecordTerminal guessing a start time and rendering the "started: with no
    // elapsed:" signature this bug was diagnosed by.
    match PluginStatus.failedNow "stack trace here" "2 failed: A, B" (TimeSpan.FromSeconds 3.0) with
    | Failed(err, _, v) ->
        test <@ err = "stack trace here" @>
        test <@ v.Summary = "2 failed: A, B" @>
        test <@ v.Elapsed = TimeSpan.FromSeconds 3.0 @>
    | other -> failwithf "expected Failed, got %A" other
