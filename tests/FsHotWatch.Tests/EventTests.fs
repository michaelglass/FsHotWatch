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

// AUTOMATION-278. The exhaustive table for THE per-project derivation.
//
// The FLOOR at the end is the point: without it, a seventh `TestResult` case could be
// added, be classified by `verdict`'s catch-all-free match (which would fail to
// compile — good) and then be omitted from this table (which would NOT), leaving a
// green test that never looked at it. The floor turns "I found nothing" into a claim
// this test is entitled to make.
[<Fact(Timeout = 15000)>]
let ``TestResult.verdict classifies every TestResult case`` () =
    let cases =
        [ TestsPassed("ok", false, TimeSpan.Zero), Verified
          TestsFailed("bad", false, TimeSpan.Zero), Refuted
          TestsTimedOut("stuck", TimeSpan.FromSeconds 1.0, false, TimeSpan.Zero), Refuted
          // The three that verify NOTHING. A zero match sits here with the other two —
          // that is the whole change: it used to be a sub-case of "passed".
          TestsNoMatch("Zero tests ran", TimeSpan.Zero), NothingVerified
          TestsDeferred "apphost not produced", NothingVerified
          TestsErrored "no parseable report", NothingVerified ]

    for (result, expected) in cases do
        test <@ TestResult.verdict result = expected @>

    let caseName (r: TestResult) =
        let (info, _) =
            Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(r, typeof<TestResult>)

        info.Name

    let declared =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<TestResult>)
        |> Array.map (fun c -> c.Name)
        |> Set.ofArray

    let covered = cases |> List.map (fst >> caseName) |> Set.ofList

    // FLOOR: every declared case appears above, and there are at least six of them —
    // so a scan that silently degraded to looking at nothing cannot pass.
    test <@ declared = covered @>
    test <@ Set.count declared >= 6 @>

[<Fact(Timeout = 15000)>]
let ``TestResult.verifiedGreen is TRUE only for a project that executed and passed`` () =
    test <@ TestResult.verifiedGreen (TestsPassed("ok", false, TimeSpan.Zero)) @>
    test <@ not (TestResult.verifiedGreen (TestsFailed("bad", false, TimeSpan.Zero))) @>
    // THE regression this ticket exists for. Its predecessor `TestResult.isPassed`
    // answered TRUE here, which is what let a run that executed nothing be summed into
    // a green by any aggregator that forgot to re-derive the fact from a string prefix.
    test <@ not (TestResult.verifiedGreen (TestsNoMatch("Zero tests ran", TimeSpan.Zero))) @>
    test <@ not (TestResult.verifiedGreen (TestsDeferred "apphost not produced")) @>
    test <@ not (TestResult.verifiedGreen (TestsErrored "no parseable report")) @>

// `verifiedGreen` is deliberately NOT the negation of "failed", and a fold that treats
// it as one over-reports rather than under-reports — the safe direction. Pinned so the
// next person to want a "not a failure" bool sees that the asymmetry is the design.
[<Fact(Timeout = 15000)>]
let ``a zero match is neither verified nor a failure`` () =
    let noMatch = TestsNoMatch("Zero tests ran", TimeSpan.Zero)
    test <@ not (TestResult.verifiedGreen noMatch) @>
    test <@ TestResult.verdict noMatch <> Refuted @>
    test <@ TestResult.isNoMatch noMatch @>

// TestsDeferred means the apphost was missing and the tests never ran. It must
// thread correctly through every TestResult helper.
[<Fact(Timeout = 15000)>]
let ``TestResult helpers handle the TestsDeferred case`` () =
    let deferred = TestsDeferred "apphost not produced; tests did not run"
    test <@ TestResult.output deferred = "apphost not produced; tests did not run" @>
    // A deferred project never ran — treat as filtered so it can't be classed
    // a full-suite run that lowers a coverage baseline.
    test <@ TestResult.wasFiltered deferred @>
    test <@ TestResult.elapsed deferred = TimeSpan.Zero @>
    // Must never count as passed: a never-ran project cannot produce a green verdict.
    test <@ not (TestResult.verifiedGreen deferred) @>
    test <@ not (TestResult.isTimedOut deferred) @>
    test <@ TestResult.isDeferred deferred @>
    test <@ not (TestResult.isDeferred (TestsPassed("ok", false, TimeSpan.Zero))) @>
    test <@ not (TestResult.isDeferred (TestsFailed("bad", false, TimeSpan.Zero))) @>

// --- RunVerification.ofResults: the ONE derivation, and the states it can reach
//
// Replaced `TestResult.ranFullSuite : _ -> bool` (AUTOMATION-282), a bool with no
// honest answer for a run that executed nothing. Each case below asserts the WHOLE
// value, not a predicate over it: "is it full suite?" alone passes equally on
// NoProjectsSelected and NothingExecuted — the confusion the type exists to end.

[<Fact(Timeout = 15000)>]
let ``ofResults: no projects selected — nothing was invoked`` () =
    // `Map.forall` over an empty map is vacuously true, so "nothing was filtered"
    // was never evidence that anything ran.
    test <@ RunVerification.ofResults Map.empty = NoProjectsSelected @>
    test <@ RunVerification.scope (RunVerification.ofResults Map.empty) = None @>

[<Fact(Timeout = 15000)>]
let ``ofResults: projects that all matched zero tests keep their count`` () =
    let results =
        Map.ofList [ "A", TestsNoMatch("", TimeSpan.Zero); "B", TestsNoMatch("", TimeSpan.Zero) ]

    test <@ RunVerification.ofResults results = AllZeroMatch 2 @>

[<Fact(Timeout = 15000)>]
let ``ofResults: projects reported but not one executed`` () =
    // Non-empty, not all-no-match, and still nothing ran. Answering `Ran` here made
    // `verifiedNothing` false for a run that verified nothing.
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
    // Positive control: the guard is not a blanket. A genuine unfiltered run must
    // still reach FullSuite, or gating is dead everywhere rather than honest.
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

    // The legacy "ran" token asserted tests ran while saying nothing about breadth,
    // so it must not parse.
    test <@ RunVerification.tryParse "ran" = None @>

// --- RunVerdict: a content-free ✓ is unconstructible (AUTOMATION-99) ---------
//
// The representation is private, so `RunVerdict.create` is the ONLY way to obtain a
// value — daemon, cache deserializer, CLI, test helper, example alike.

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
    // A cache replay or a no-op cycle genuinely takes zero time.
    let v = RunVerdict.create "0 files checked" TimeSpan.Zero
    test <@ v.Elapsed = TimeSpan.Zero @>

[<Fact(Timeout = 15000)>]
let ``PluginStatus.completedNow and failedNow carry their verdict`` () =
    match PluginStatus.completedNow "did a thing" (TimeSpan.FromSeconds 2.0) with
    | Completed(_, v) ->
        test <@ v.Summary = "did a thing" @>
        test <@ v.Elapsed = TimeSpan.FromSeconds 2.0 @>
    | other -> failwithf "expected Completed, got %A" other

    // Failed carries BOTH: the error is the diagnosis, the verdict is the one-line
    // summary plus the measured duration. Without it RecordTerminal guesses a start
    // time and renders the "started: with no elapsed:" signature.
    match PluginStatus.failedNow "stack trace here" "2 failed: A, B" (TimeSpan.FromSeconds 3.0) with
    | Failed(err, _, v) ->
        test <@ err = "stack trace here" @>
        test <@ v.Summary = "2 failed: A, B" @>
        test <@ v.Elapsed = TimeSpan.FromSeconds 3.0 @>
    | other -> failwithf "expected Failed, got %A" other
