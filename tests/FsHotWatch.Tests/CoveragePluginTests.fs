module FsHotWatch.Tests.CoveragePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers
open CoverageRatchet.Thresholds

module CovPlugin = FsHotWatch.Coverage.CoveragePlugin

/// Minimal Cobertura XML with the given filename and line hits. All non-zero hits = covered.
let private coberturaXml (fileName: string) (lines: (int * int) list) =
    let lineEls =
        lines
        |> List.map (fun (num, hits) -> $"""<line number="{num}" hits="{hits}" />""")
        |> String.concat "\n            "

    $"""<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.5" branch-rate="1" version="1.9">
  <packages>
    <package name="pkg" line-rate="0.5" branch-rate="1">
      <classes>
        <class filename="{fileName}" name="{fileName}" line-rate="0.5" branch-rate="1">
          <lines>
            {lineEls}
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>"""

/// Minimal thresholds JSON — empty overrides, uses defaults (100%).
let private defaultThresholdsJson = "{}"

/// Thresholds JSON with a per-file override for fileName with given line/branch thresholds.
let private thresholdsJsonWithOverride (fileName: string) (line: int) (branch: int) =
    $"""{{ "overrides": {{ "{fileName}": {{ "line": {line}, "branch": {branch} }} }} }}"""

/// A run that actually EXECUTED a test, unfiltered.
///
/// This carried `Results = Map.empty` until AUTOMATION-280 — a shape the plugin
/// now declines to judge, so it exercised the skip path rather than the check.
/// Every driver below wants the ordinary case, so the ordinary case is what it
/// emits; the degenerate shape has its own helper and its own test.
let private emitRunCompleted (host: PluginHost) =
    host.EmitTestRunCompleted
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList [ "p1", TestsPassed("ok", false, TimeSpan.Zero) ]
          RanFullSuite = true }

/// Nothing executed, yet the run claims full-suite scope.
///
/// `RanFullSuite = true` is the HARSHER input, deliberately: TestPrune's own
/// degenerate lifecycles say `false` since AUTOMATION-281, but a replayed cache
/// entry or an external producer can still say `true`, and that is the case the
/// guard has to survive.
let private emitRunThatExecutedNothing (host: PluginHost) =
    host.EmitTestRunCompleted
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.empty
          RanFullSuite = true }

[<Fact(Timeout = 15000)>]
let ``plugin has correct name`` () =
    let handler = FsHotWatch.Coverage.CoveragePlugin.create "/tmp/ratchet.json" "/tmp"

    test <@ handler.Name = FsHotWatch.PluginFramework.PluginName.create "coverage" @>

[<Fact(Timeout = 15000)>]
let ``plugin subscribes to TestRunCompleted`` () =
    let handler = FsHotWatch.Coverage.CoveragePlugin.create "/tmp/ratchet.json" "/tmp"

    test
        <@
            handler.Subscriptions
            |> Set.contains FsHotWatch.PluginFramework.SubscribeTestRunCompleted
        @>

// ---------------------------------------------------------------------------
// Verdict reliability (2026-06-02 Issue B): an impact-filtered run must NOT gate
// red. Un-run source files read 0.0% (absent), indistinguishable from a genuine
// zero, so only a full-suite run may fail the gate; a filtered run is raise-only.
// ---------------------------------------------------------------------------

let private mkFileResult (fileName: string) (linePct: float) (lineThreshold: float) : FileResult =
    { File =
        { FileName = fileName
          LinePct = linePct
          BranchPct = 100.0
          BranchesCovered = 0
          BranchesTotal = 0 }
      LineThreshold = lineThreshold
      BranchThreshold = 0.0 }

[<Fact(Timeout = 5000)>]
let ``gateVerdict: full-suite shortfall gates (Failed)`` () =
    let below = [ mkFileResult "OutOfDiff.fs" 0.0 100.0 ]

    match CovPlugin.gateVerdict true (SomeFailed below) with
    | CovPlugin.Failed results -> test <@ results.Length = 1 @>
    | other -> Assert.Fail $"Expected Failed, got {other}"

[<Fact(Timeout = 5000)>]
let ``gateVerdict: filtered shortfall does NOT gate (NotGatedFiltered)`` () =
    let below =
        [ mkFileResult "OutOfDiff1.fs" 0.0 100.0
          mkFileResult "OutOfDiff2.fs" 0.0 100.0 ]

    match CovPlugin.gateVerdict false (SomeFailed below) with
    | CovPlugin.NotGatedFiltered count -> test <@ count = 2 @>
    | other -> Assert.Fail $"Expected NotGatedFiltered, got {other}"

[<Fact(Timeout = 5000)>]
let ``gateVerdict: AllPassed is Passed regardless of full-suite flag`` () =
    test
        <@
            match CovPlugin.gateVerdict false AllPassed with
            | CovPlugin.Passed -> true
            | _ -> false
        @>

    test
        <@
            match CovPlugin.gateVerdict true AllPassed with
            | CovPlugin.Passed -> true
            | _ -> false
        @>

[<Fact(Timeout = 15000)>]
let ``regression: impact-filtered run with stale baseline does not false-red on un-run file`` () =
    withTempDir "coverage" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")

        // An out-of-diff file reading 0.0% (no current coverage, no/stale baseline):
        // 0 of 2 lines hit. 100% default floor ⇒ a raw ratchet check would FAIL it.
        File.WriteAllText(xmlPath, coberturaXml "OutOfDiff.fs" [ (1, 0); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Impact-filtered run: a project that RAN, under a filter.
        //
        // This drove `Results = Map.empty` until AUTOMATION-280. That is not what a
        // filtered run looks like — a filter narrows which tests run, it does not
        // remove the project from the results — and the plugin now declines to judge
        // a run that executed nothing, so the empty map exercised the skip path
        // instead of the downgrade this test is about.
        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Normal
              Results = Map.ofList [ "p1", TestsPassed("ok", true, TimeSpan.Zero) ]
              RanFullSuite = false }

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            10000

        // No false red: verdict is ✓ and NO errors are reported for the un-run file.
        let status = host.GetStatus("coverage")

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>

        let errors = host.GetErrorsByPlugin("coverage")
        test <@ errors.IsEmpty @>)

[<Fact(Timeout = 10000)>]
let ``regression: repeated impact-filtered evaluations of an unchanged commit are stable`` () =
    // The downstream non-determinism was the GATE DECISION varying across
    // identical evaluations of one unchanged commit (✓ / 9 / 85 below-floor).
    // The gate is now a pure function of (ranFullSuite, CheckResult), so the
    // same below-floor set on a filtered run always yields the SAME non-gating
    // verdict — no flake. Evaluate repeatedly and assert a stable result.
    let belowFloor =
        [ mkFileResult "OutOfDiff1.fs" 0.0 100.0
          mkFileResult "OutOfDiff2.fs" 0.0 100.0
          mkFileResult "OutOfDiff3.fs" 0.0 100.0 ]

    let verdicts =
        [ for _ in 1..10 ->
              match CovPlugin.gateVerdict false (SomeFailed belowFloor) with
              | CovPlugin.NotGatedFiltered n -> Some n
              | CovPlugin.Failed _ -> None
              | CovPlugin.Passed -> Some 0 ]

    // Every evaluation must be identical: not gated, same count, never a red.
    test <@ verdicts |> List.forall (fun v -> v = Some 3) @>

[<Fact(Timeout = 15000)>]
let ``plugin reports errors when file is below threshold`` () =
    withTempDir "coverage" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")

        // File with 50% line coverage — will fail at 100% default threshold
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Failed _) -> true
                | _ -> false)
            10000

        let errors = host.GetErrorsByPlugin("coverage")
        test <@ not errors.IsEmpty @>

        let fileErrors = errors |> Map.tryFind "MyModule.fs"
        test <@ fileErrors.IsSome @>
        test <@ not fileErrors.Value.IsEmpty @>)

[<Fact(Timeout = 30000)>]
let ``a run that executed NOTHING reaches no coverage verdict — its full-suite claim is vacuous`` () =
    // AUTOMATION-280. The gate is `if ranFullSuite then Failed else NotGatedFiltered`,
    // and `RanFullSuite` is vacuously TRUE for an empty result map — so a run that
    // executed no test could decide whether a shortfall gates. Neither answer is
    // honest: `true` gates on coverage this run did not produce, `false` silently
    // downgrades a real shortfall to a non-gating notice. The only honest move is
    // to reach no verdict at all, which is what `Aborted` already does.
    withTempDir "coverage" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")

        // A shortfall that WOULD gate: 50% line coverage against a 100% default.
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        let isFailed () =
            match host.GetStatus("coverage") with
            | Some(Failed _) -> true
            | _ -> false

        emitRunThatExecutedNothing host
        // Quiescence, not a fixed wait. `waitUntil isFailed 2000` burns the FULL 2s
        // every run — the condition it polls for never becomes true, which is the
        // whole point — and proves only that 2s elapsed. This returns the instant
        // the plugin has drained the event, and proves the stronger thing: the
        // plugin CONSUMED the run and still reached no gating verdict.
        waitForQuiescent host 5000
        test <@ not (isFailed ()) @>

        // POSITIVE CONTROL, and the reason the assertion above means anything: the
        // same fixture, same host, same threshold — driven by a run that actually
        // executed — must gate. Without this, a broken fixture or a plugin that
        // never checks anything would pass the absence assertion just as happily.
        emitRunCompleted host
        waitUntil isFailed 10000
        test <@ isFailed () @>)

[<Fact(Timeout = 15000)>]
let ``plugin clears errors when all files pass`` () =
    withTempDir "coverage" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")

        // File with 100% line coverage
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        // Override to require only 50% so the file passes
        File.WriteAllText(configPath, thresholdsJsonWithOverride "MyModule.fs" 50 0)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            10000

        let status = host.GetStatus("coverage")

        test
            <@
                match status.Value with
                | Completed _ -> true
                | _ -> false
            @>

        let errors = host.GetErrorsByPlugin("coverage")
        test <@ errors.IsEmpty @>)

// The no-XML path costs a FIXED ~5s: `pollForFiles searchDir 50 100` walks the
// tree 50 times at 100ms apart before giving up. That floor is intrinsic to what
// this test covers — it cannot be seeded away here the way an incidental
// observer's can (cf. `run-tests emits TestRunCompleted so other plugins see the
// run`, which seeds a cobertura precisely so it never pays this) — so the bound
// has to be a MULTIPLE of 5s rather than the 2x it used to carry. Measured: 5122ms
// on an idle box; under `mise run ci`, which fans `test-direct` out alongside
// `compile` and `lint-project`, the same 5s of `Async.Sleep` dilated past 10s on a
// saturated thread pool. 30s is 6x the nominal floor.
[<Fact(Timeout = 45000)>]
let ``plugin skips check when no coverage XML found`` () =
    withTempDir "coverage" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Subscribe before emitting: polling `GetStatus` samples a value that lags
        // the transition, and — more importantly — a wait that merely EXPIRES used
        // to be indistinguishable from one that succeeded, because the only
        // assertion below (`errors.IsEmpty`) is vacuously true on the no-XML path
        // whether or not the plugin ever ran. The wait is now asserted.
        let completion = beginAwaitTerminal host "coverage"

        emitRunCompleted host

        let observed = completion.Wait(TimeSpan.FromSeconds 30.0)

        // Plugin should complete (no XML = no failures to report)
        if not observed then
            let last = host.GetStatus("coverage")
            failwithf "coverage never reached a terminal status on the no-XML path. Last status: %A" last

        test
            <@
                match completion.Result with
                | Completed _ -> true
                | _ -> false
            @>

        let errors = host.GetErrorsByPlugin("coverage")
        test <@ errors.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``plugin ignores aborted test runs`` () =
    withTempDir "coverage" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Emit an aborted run
        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Aborted "timeout"
              Results = Map.empty
              RanFullSuite = false }

        // Wait a bit — plugin should NOT transition (stays Idle or Running briefly then Idle)
        System.Threading.Thread.Sleep(500)

        let status = host.GetStatus("coverage")
        // Should not have Failed (no check was run on an aborted run)
        test
            <@
                match status with
                | Some(Failed _) -> false
                | _ -> true
            @>)

// ---------------------------------------------------------------------------
// The coverage check is VISIBLE while it runs (AUTOMATION-99 review, finding 4)
//
// CoveragePlugin claimed `RunExclusive "coverage-check"` with NO preceding
// `Running`. Two consequences, both live: its status rendered ✓ while it was
// still running, and — because the host's work-cycle generation only advances
// on a Running transition — coverage's generation NEVER moved, so
// `allPluginsAdvancedToTerminal()` could never be satisfied while coverage was
// registered and EVERY `WaitForComplete` fell back to the slower quiescence
// path. The framework now reports Running at the claim, so the gap is
// unrepresentable rather than merely fixed here.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``coverage advances its work-cycle generation — the fast terminal wait is not starved`` () =
    withTempDir "coverage-gen" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Never run ⇒ generation 0 (absent from the map).
        test <@ (host.WorkCycleGenerations() |> Map.tryFind "coverage") = None @>

        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            15000

        // The check ran, so the plugin passed THROUGH Running — which is the
        // only thing that advances the generation counter.
        let gen = host.WorkCycleGenerations() |> Map.tryFind "coverage"
        test <@ gen = Some 1L @>)

[<Fact(Timeout = 20000)>]
let ``a coverage failure carries a verdict with an honest elapsed and a UTC timestamp`` () =
    withTempDir "coverage-utc" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        // 50% line coverage against the 100% default floor ⇒ gated failure.
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let before = DateTime.UtcNow
        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Failed _) -> true
                | _ -> false)
            15000

        match host.GetStatus("coverage") with
        | Some(Failed(_, at, v)) ->
            // The failure states what it found …
            test <@ v.Summary.Contains "below threshold" @>
            // … and its timestamp is UTC. This site shipped `DateTime.Now` while
            // its own CHANGELOG asserted UTC; mixed into UTC arithmetic that
            // skews or NEGATES the elapsed a human reads when coverage gates
            // them. A local reading in a non-UTC zone lands outside this window.
            let after = DateTime.UtcNow
            test <@ at >= before && at <= after @>
        | other -> failwithf "expected Failed with a verdict, got %A" other)

// ---------------------------------------------------------------------------
// `coverage-ratchet` runs on the MAILBOX, under the same exclusive slot as the
// check (AUTOMATION-99 review, finding 7).
//
// The command used to rewrite the thresholds config on the IPC thread while a
// `RunExclusive "coverage-check"` run might be READING that very file — a
// second instance of the capability that caused this bug (an IPC command doing
// work outside the daemon's accounting). The command can now only `Post`; the
// handler claims the slot, so the rewrite and the check are serialised.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``coverage-ratchet rewrites the thresholds config through the mailbox`` () =
    withTempDir "coverage-ratchet" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        let reply = host.RunCommand("coverage-ratchet", [| "" |]) |> Async.RunSynchronously

        test <@ reply.IsSome @>
        test <@ reply.Value.Contains "thresholds updated" @>

        // The rewrite actually happened. (WHAT coverageratchet chooses to
        // tighten is its policy, not this plugin's — the subject here is that
        // the rewrite ran on the MAILBOX, under the slot, and reported back.)
        let written = File.ReadAllText(configPath)
        test <@ written <> defaultThresholdsJson @>

        // … and it went through the MAILBOX, so it is a run the daemon can see:
        // it reported Running at the claim (advancing the generation) and a
        // terminal verdict when it finished.
        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            10000

        match host.GetStatus("coverage") with
        | Some(Completed(_, v)) -> test <@ v.Summary.Contains "thresholds updated" @>
        | other -> failwithf "expected Completed carrying the ratchet verdict, got %A" other

        test <@ (host.WorkCycleGenerations() |> Map.tryFind "coverage") = Some 1L @>)

[<Fact(Timeout = 20000)>]
let ``coverage-ratchet with an explicit config path argument targets that file`` () =
    withTempDir "coverage-ratchet-arg" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let defaultConfig = Path.Combine(dir, "coverage-ratchet.json")
        let explicitConfig = Path.Combine(dir, "other-thresholds.json")
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        File.WriteAllText(defaultConfig, defaultThresholdsJson)
        File.WriteAllText(explicitConfig, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create defaultConfig dir)

        let reply =
            host.RunCommand("coverage-ratchet", [| explicitConfig |])
            |> Async.RunSynchronously

        test <@ reply.Value.Contains explicitConfig @>
        // The named file was rewritten; the default one was left alone.
        test <@ File.ReadAllText(explicitConfig) <> defaultThresholdsJson @>
        test <@ File.ReadAllText(defaultConfig) = defaultThresholdsJson @>)

[<Fact(Timeout = 20000)>]
let ``coverage-ratchet reports honestly when there is no coverage XML to ratchet from`` () =
    withTempDir "coverage-ratchet-noxml" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        let reply = host.RunCommand("coverage-ratchet", [| "" |]) |> Async.RunSynchronously

        test <@ reply.Value.Contains "no coverage.cobertura.xml found" @>
        // Nothing was rewritten on the "nothing to learn from" path.
        test <@ File.ReadAllText(configPath) = defaultThresholdsJson @>)

[<Fact(Timeout = 30000)>]
let ``coverage-ratchet REFUSES to race a check that is reading the file it rewrites`` () =
    // With no XML on disk, the check holds the "coverage-check" slot for its
    // full poll window — a deterministic way to have a run in flight. The
    // ratchet's claim is then refused (SlotBusy) and it says so, rather than
    // rewriting the config under a live reader.
    withTempDir "coverage-ratchet-race" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Start a check: it polls for coverage files (none exist) and so holds
        // the slot. The framework reports Running at the claim.
        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Running _) -> true
                | _ -> false)
            10000

        let reply = host.RunCommand("coverage-ratchet", [| "" |]) |> Async.RunSynchronously

        test <@ reply.Value.Contains "a coverage check is in flight" @>
        // The config was NOT rewritten under the live reader.
        test <@ File.ReadAllText(configPath) = defaultThresholdsJson @>

        // Let the check settle so the temp dir can be cleaned.
        waitUntil (fun () -> not (host.AnyPluginBusy())) 20000)

[<Fact(Timeout = 30000)>]
let ``a second TestRunCompleted while a check is in flight is skipped, not stacked`` () =
    // The `SlotBusy` arm of the check trigger: skipping is correct here (the
    // next completed run re-checks against its own fresh cobertura), but it is
    // now a decision the code STATES rather than a value the framework dropped.
    withTempDir "coverage-double-trigger" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Running _) -> true
                | _ -> false)
            10000

        // Second trigger while the first check holds the slot.
        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            20000

        // Exactly ONE check cycle ran (one Running→terminal transition).
        test <@ (host.WorkCycleGenerations() |> Map.tryFind "coverage") = Some 1L @>
        waitUntil (fun () -> not (host.AnyPluginBusy())) 20000)
