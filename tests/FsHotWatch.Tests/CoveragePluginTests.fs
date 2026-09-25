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
/// `Results` must be non-empty: the plugin declines to judge a run that executed
/// nothing, so `Map.empty` here would silently exercise the skip path instead of
/// the check. The degenerate shape has its own helper below.
let private emitRunCompleted (host: PluginHost) =
    host.EmitTestRunCompleted
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList [ "p1", TestsPassed("ok", false, TimeSpan.Zero) ]
          Verification = Ran RunScope.FullSuite }

/// Nothing executed, yet the run claims full-suite scope.
///
/// `RanFullSuite = true` is deliberately the HARSHER input: TestPrune's own
/// degenerate lifecycles say `false`, but a replayed cache entry or an external
/// producer can still say `true`, and that is the case the guard has to survive.
let private emitRunThatExecutedNothing (host: PluginHost) =
    host.EmitTestRunCompleted
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.empty
          Verification = NoProjectsSelected }

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
// Verdict reliability: an impact-filtered run must NOT gate red. Un-run source
// files read 0.0% (absent), indistinguishable from a genuine zero, so only a
// full-suite run may fail the gate; a filtered run is raise-only.
// ---------------------------------------------------------------------------

let private mkFileResult (fileName: string) (linePct: float) (lineThreshold: float) : FileResult =
    { File =
        { FileName = fileName
          LinePct = linePct
          BranchPct = 100.0
          // CoverageRatchet.Core 0.1.0-alpha.6 carries the covered-line COUNT beside the
          // percentage (it ratchets on counts, which stay stable when the JIT-dependent
          // denominator drifts). A 100-line denominator keeps the two coherent: covered
          // IS the percentage. These tests gate on percentages, so the counts only have
          // to agree with LinePct, not be realistic.
          LinesCovered = int linePct
          LinesTotal = 100
          BranchesCovered = 0
          BranchesTotal = 0 }
      LineThreshold = lineThreshold
      BranchThreshold = 0.0 }

[<Fact(Timeout = 5000)>]
let ``gateVerdict: full-suite shortfall gates (Failed)`` () =
    let below = [ mkFileResult "OutOfDiff.fs" 0.0 100.0 ]

    match CovPlugin.gateVerdict RunScope.FullSuite (SomeFailed below) with
    | CovPlugin.Failed results -> test <@ results.Length = 1 @>
    | other -> Assert.Fail $"Expected Failed, got {other}"

[<Fact(Timeout = 5000)>]
let ``gateVerdict: filtered shortfall does NOT gate (NotGatedFiltered)`` () =
    let below =
        [ mkFileResult "OutOfDiff1.fs" 0.0 100.0
          mkFileResult "OutOfDiff2.fs" 0.0 100.0 ]

    match CovPlugin.gateVerdict RunScope.Partial (SomeFailed below) with
    | CovPlugin.NotGatedFiltered count -> test <@ count = 2 @>
    | other -> Assert.Fail $"Expected NotGatedFiltered, got {other}"

[<Fact(Timeout = 5000)>]
let ``gateVerdict: AllPassed is Passed regardless of full-suite flag`` () =
    test
        <@
            match CovPlugin.gateVerdict RunScope.Partial AllPassed with
            | CovPlugin.Passed -> true
            | _ -> false
        @>

    test
        <@
            match CovPlugin.gateVerdict RunScope.FullSuite AllPassed with
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

        // Impact-filtered run: a project that RAN, under a filter. A filter narrows
        // which tests run; it does not remove the project from the results, so an
        // empty `Results` here would exercise the skip path, not the downgrade.
        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Normal
              Results = Map.ofList [ "p1", TestsPassed("ok", true, TimeSpan.Zero) ]
              Verification = Ran RunScope.Partial }

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

[<Fact(Timeout = 10000)>]
let ``regression: repeated impact-filtered evaluations of an unchanged commit are stable`` () =
    // The flake was the GATE DECISION varying across identical evaluations of one
    // unchanged commit (✓ / 9 / 85 below-floor). The gate is now a pure function
    // of (ranFullSuite, CheckResult), so repeated evaluation gives one answer.
    let belowFloor =
        [ mkFileResult "OutOfDiff1.fs" 0.0 100.0
          mkFileResult "OutOfDiff2.fs" 0.0 100.0
          mkFileResult "OutOfDiff3.fs" 0.0 100.0 ]

    let verdicts =
        [ for _ in 1..10 ->
              match CovPlugin.gateVerdict RunScope.Partial (SomeFailed belowFloor) with
              | CovPlugin.NotGatedFiltered n -> Some n
              | CovPlugin.Failed _ -> None
              | CovPlugin.Passed -> Some 0 ]

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
    // The gate is `if ranFullSuite then Failed else NotGatedFiltered`, and
    // `RanFullSuite` is vacuously TRUE for an empty result map — so a run that
    // executed no test could decide whether a shortfall gates. Neither answer is
    // honest: `true` gates on coverage this run did not produce, `false` silently
    // downgrades a real shortfall to a non-gating notice. It must reach no verdict
    // at all, as `Aborted` already does.
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
        // Quiescence, not a fixed wait. `waitUntil isFailed 2000` would burn the full
        // 2s every run (its condition never becomes true) and prove only that 2s
        // elapsed. This returns the instant the event is drained, and so proves the
        // stronger thing: the plugin CONSUMED the run and still reached no verdict.
        waitForQuiescent host 5000
        test <@ not (isFailed ()) @>

        // POSITIVE CONTROL, and the reason the assertion above means anything: the
        // same fixture, host and threshold, driven by a run that actually executed,
        // must gate. Without it a broken fixture or a plugin that never checks
        // anything would pass the absence assertion just as happily.
        emitRunCompleted host
        waitUntil isFailed 10000
        test <@ isFailed () @>)

[<Fact(Timeout = 15000)>]
let ``plugin clears errors when all files pass`` () =
    withTempDir "coverage" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")

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

// The no-XML path costs a FIXED ~5s: `pollForFiles searchDir 50 100` walks the tree
// 50 times at 100ms apart before giving up, and this test cannot seed a cobertura to
// skip it — that floor IS the path under test. Measured 5122ms idle; under `mise run
// ci`, which fans `test-direct` out alongside `compile` and `lint-project`, the same
// 5s of `Async.Sleep` dilated past 10s on a saturated thread pool. Hence a bound that
// is a multiple of the floor, not 2x it: 30s is 6x.
[<Fact(Timeout = 45000)>]
let ``plugin skips check when no coverage XML found`` () =
    withTempDir "coverage" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Subscribe before emitting, and assert the wait: `errors.IsEmpty` below is
        // vacuously true on the no-XML path whether or not the plugin ever ran, so a
        // wait that merely EXPIRES would be indistinguishable from one that succeeded.
        let completion = beginAwaitTerminal host "coverage"

        emitRunCompleted host

        let observed = completion.Wait(TimeSpan.FromSeconds 30.0)

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

        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Aborted "timeout"
              Results = Map.empty
              Verification = NoProjectsSelected }

        // Quiescence rather than a fixed sleep: proves the event WAS drained.
        waitForQuiescent host 5000

        let status = host.GetStatus("coverage")

        test
            <@
                match status with
                | Some(Failed _) -> false
                | _ -> true
            @>)

[<Fact(Timeout = 15000)>]
let ``an aborted run is ignored even when it executed and covered the whole suite`` () =
    // The abort is what disqualifies it, NOT the absence of a verification: a run
    // cancelled after some projects reported carries real results and a real
    // `Ran FullSuite`, so it is the one aborted shape that would reach the gate if
    // the `Aborted` arm were ever narrowed to "and nothing ran". Its coverage is a
    // partial artefact of an interrupted run and must not gate a shortfall.
    withTempDir "coverage" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        // A shortfall that WOULD gate if this run were judged.
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        let results = Map.ofList [ "p1", TestsPassed("ok", false, TimeSpan.Zero) ]

        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Aborted "cancelled after the first project"
              Results = results
              Verification = RunVerification.ofResults results }

        waitForQuiescent host 5000

        test
            <@
                match host.GetStatus("coverage") with
                | Some(Failed _) -> false
                | _ -> true
            @>)

// ---------------------------------------------------------------------------
// The coverage check is VISIBLE while it runs.
//
// Claiming the exclusive slot with NO preceding `Running` rendered coverage's
// status as ✓ while it was still running. The framework now reports `Running` at
// the claim, and the run's terminal lands in coverage's activity history.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 20000)>]
let ``coverage records the completed run in its activity history`` () =
    withTempDir "coverage-gen" (fun dir ->
        let xmlPath = Path.Combine(dir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Never run ⇒ no run record.
        test <@ host.GetHistory("coverage").IsEmpty @>

        emitRunCompleted host

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            15000

        test <@ host.GetHistory("coverage").Length = 1 @>)

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
            test <@ v.Summary.Contains "below threshold" @>
            // The timestamp is UTC. A `DateTime.Now` here mixes into UTC arithmetic
            // and skews or NEGATES the elapsed a human reads when coverage gates
            // them; a local reading in a non-UTC zone lands outside this window.
            let after = DateTime.UtcNow
            test <@ at >= before && at <= after @>
        | other -> failwithf "expected Failed with a verdict, got %A" other)

// ---------------------------------------------------------------------------
// `coverage-ratchet` runs on the MAILBOX, under the same exclusive slot as the check.
//
// Rewriting the thresholds config from the IPC thread can race a check that is
// READING that very file — an IPC command doing work outside the daemon's
// accounting. The command only `Post`s; the handler claims the slot, so the
// rewrite and the check are serialised.
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

        // WHAT coverageratchet chooses to tighten is its policy, not this plugin's;
        // all this asserts is that the rewrite happened.
        let written = File.ReadAllText(configPath)
        test <@ written <> defaultThresholdsJson @>

        // … and that it went through the MAILBOX, so the daemon can see it: Running
        // at the claim, then a terminal verdict in the run history.
        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            10000

        match host.GetStatus("coverage") with
        | Some(Completed(_, v)) -> test <@ v.Summary.Contains "thresholds updated" @>
        | other -> failwithf "expected Completed carrying the ratchet verdict, got %A" other

        test <@ host.GetHistory("coverage").Length = 1 @>)

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

[<Fact(Timeout = 60000)>]
let ``triggers refused while a check is in flight are coalesced into one later check`` () =
    // The `SlotBusy` arm of the check trigger. A refused trigger is KEPT: the runs it
    // speaks for produced coverage nothing has judged, and no later event re-requests
    // it. So the holder's result fold runs it — once, however many triggers were
    // refused, because they all describe the same cobertura on disk.
    withTempDir "coverage-double-trigger" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        emitRunCompleted host

        // With no coverage XML on disk the check holds the slot for its full poll
        // window, so the triggers below are refused without racing anything.
        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Running _) -> true
                | _ -> false)
            10000

        // Three more triggers while the first check holds the slot.
        emitRunCompleted host
        emitRunCompleted host
        emitRunCompleted host

        let settled = waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 45000
        test <@ settled @>

        // The first check, plus ONE for everything refused behind it — not three, and
        // not the zero the dropped trigger used to leave.
        test <@ host.GetHistory("coverage").Length = 2 @>)

// ---------------------------------------------------------------------------
// `coverage-status` — the human-facing answer to "what did coverage decide?".
//
// Each of its three states is a distinct claim, and "no check has run yet" is the
// one that must not be confusable with "OK" — the same absence-read-as-success
// shape this area exists to prevent, one surface over.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``coverage-status distinguishes never-run from OK from FAILED`` () =
    withTempDir "coverage" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        let status () =
            host.RunCommand("coverage-status", [||]) |> Async.RunSynchronously

        // Nothing has run: it must say so, not report OK.
        test <@ status () = Some "coverage: no check run yet" @>

        // A passing check. 100% line coverage against a 50% override.
        File.WriteAllText(Path.Combine(dir, "coverage.cobertura.xml"), coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        File.WriteAllText(configPath, thresholdsJsonWithOverride "MyModule.fs" 50 0)
        emitRunCompleted host
        waitUntil (fun () -> status () = Some "coverage: OK") 10000
        test <@ status () = Some "coverage: OK" @>

        // A shortfall against the default 100% floor flips it, and the message
        // points at where the detail lives.
        File.WriteAllText(Path.Combine(dir, "coverage.cobertura.xml"), coberturaXml "MyModule.fs" [ (1, 1); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)
        emitRunCompleted host

        waitUntil (fun () -> status () = Some "coverage: FAILED (run `fshw errors` for details)") 10000
        test <@ status () = Some "coverage: FAILED (run `fshw errors` for details)" @>)

// ---------------------------------------------------------------------------
// COVERAGE's honest control: it never participates in the cache
// ---------------------------------------------------------------------------
//
// The other three plugins get a cold-vs-cached ledger comparison. Coverage
// cannot: it is deliberately non-cacheable (`CacheKey = None`), because its
// result is a function of the Cobertura XML the preceding test run just wrote —
// files no key it could compute covers. Handing it a synthetic cache entry would
// prove something about a fixture rather than about production.
//
// So the property to pin is the ABSENCE, and the absence has to be pinned where
// it bites: `handler.CacheKey = None` makes the dispatch loop compute no key at
// all, which is what makes replay AND store impossible. Asserting the field
// alone would leave "and therefore the framework never touches the cache" as an
// unchecked inference — so the cache itself is instrumented and asked.

/// A task cache that counts every framework lookup and store. Delegates
/// everything; the counters are the whole point.
type private CountingTaskCache(inner: FsHotWatch.TaskCache.ITaskCache) =
    let mutable gets = 0
    let mutable sets = 0

    member _.Gets = System.Threading.Volatile.Read(&gets)
    member _.Sets = System.Threading.Volatile.Read(&sets)

    interface FsHotWatch.TaskCache.ITaskCache with
        member _.TryGet compositeKey cacheKey =
            System.Threading.Interlocked.Increment(&gets) |> ignore
            inner.TryGet compositeKey cacheKey

        // Counted too, and this is the member the framework actually calls: counting
        // only `TryGet` would let the "never reads the cache" claim pass vacuously.
        member _.Lookup compositeKey cacheKey =
            System.Threading.Interlocked.Increment(&gets) |> ignore
            inner.Lookup compositeKey cacheKey

        member _.Set compositeKey cacheKey result =
            System.Threading.Interlocked.Increment(&sets) |> ignore
            inner.Set compositeKey cacheKey result

        member _.Clear() = inner.Clear()
        member _.ClearPlugin plugin = inner.ClearPlugin plugin
        member _.ClearFile file = inner.ClearFile file
        member _.ClearPluginFile plugin file = inner.ClearPluginFile plugin file

[<Fact(Timeout = 30000)>]
let ``the coverage plugin never reads or writes the task cache`` () =
    withTempDir "coverage-noncacheable" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, thresholdsJsonWithOverride "MyModule.fs" 50 0)
        File.WriteAllText(Path.Combine(dir, "coverage.cobertura.xml"), coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])

        let counting = CountingTaskCache(FsHotWatch.TaskCache.InMemoryTaskCache())

        let host =
            PluginHost(Unchecked.defaultof<_>, dir, taskCache = (counting :> FsHotWatch.TaskCache.ITaskCache))

        let handler = CovPlugin.create configPath dir

        // The declaration, pinned so a future "let's cache coverage too" has to
        // come back through this test and its reasoning.
        test <@ handler.CacheKey.IsNone @>

        host.RegisterHandler(handler)

        // NO out-of-batch sentinel here, deliberately. The other three controls seed
        // one because their question is what a REPLAY does to the ledger. Coverage has
        // no replay — and it clears wholesale on every check anyway (`ClearAllErrors`
        // before re-reporting the shortfalls), so a surviving-sentinel assertion would
        // be false and a cleared-sentinel assertion would be about `ClearAllErrors`,
        // not about the cache. The cache key's ABSENCE is the whole property.

        let status () =
            host.RunCommand("coverage-status", [||]) |> Async.RunSynchronously

        // Two identical runs, the second dispatched only once the first has landed
        // — for any cacheable plugin that second one is a hit.
        //
        // POSITIVE CONTROL, and the reason the wait is on `coverage: OK` rather than
        // on a terminal status: the plugin starts Idle, so a terminal-status poll can
        // return before the first check even begins, and "the cache was never touched"
        // would then be a true statement about a plugin that never ran.
        emitRunCompleted host
        let ranCold = waitUntilTrue (fun () -> status () = Some "coverage: OK") 15000
        test <@ ranCold @>

        emitRunCompleted host
        let settled = waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 15000
        test <@ settled @>
        let afterSecond = status ()
        test <@ afterSecond = Some "coverage: OK" @>

        // Never replayed ...
        let summary = terminalSummaryOf host "coverage"
        test <@ not (summary.Contains "(cached)") @>
        // ... and never even consulted. `None` short-circuits before the lookup, so
        // both counters stay at zero rather than merely missing.
        test <@ counting.Gets = 0 @>
        test <@ counting.Sets = 0 @>)

// ---------------------------------------------------------------------------
// A refused claim on "coverage-check" must not DROP the trigger.
//
// The key is held from the claim until the run's RESULT FOLD commits, not until
// the worker finishes: `PluginWorkOwner.completeRun` turns the lane's holder from
// `Worker` into `Folding`, and `tryClaim` refuses any lane that exists. So a
// `TestRunCompleted` folded between a check finishing and its `CheckDone`
// committing is refused while `IsRunning "coverage-check"` reads false — and a
// refused trigger that is only logged is coverage for the newest test run that is
// never judged, with nothing left to re-request it.
//
// These drive that window directly — the refusal is the stub's answer, not a
// race to be slept on.
// ---------------------------------------------------------------------------

module PF = FsHotWatch.PluginFramework

/// A context whose `RunExclusive` answers `claim` and records what it was handed.
/// `IsRunning` is false throughout: that IS the fold window — no worker is live,
/// yet the key is held.
let private stubCoverageCtx
    (claim: string -> Async<CovPlugin.CoverageMsg> -> PF.RunClaim)
    : PF.PluginCtx<CovPlugin.CoverageMsg> =
    { ReportStatus = ignore
      ReportErrors = fun _ _ -> ()
      ClearErrors = ignore
      ClearAllErrors = ignore
      EmitBuildCompleted = ignore
      EmitTestRunStarted = ignore
      EmitTestProgress = ignore
      EmitTestRunCompleted = ignore
      EmitCommandCompleted = ignore
      Checker = Unchecked.defaultof<_>
      RepoRoot = "/tmp"
      Post = ignore
      EnqueueExclusiveIntent = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(())
      StartSubtask = fun _ _ -> ()
      UpdateSubtask = fun _ _ -> ()
      EndSubtask = ignore
      Log = ignore
      CompleteWithTimeout = ignore
      RunExclusive = claim
      RunExclusiveShared = fun _ _ _ _ _ -> failwith "coverage takes no shared lease"
      SlotHolder = fun _ -> PF.SlotHolder.Free
      DeclareBoundedWork = PF.BoundedWork.undeclared
      FcsSuppressedCodes = Set.empty
      ProjectGraph = PF.ProjectGraphAccessor.none }

/// A completed run that PROVED `scope` — the input the check trigger reads.
let private runCompleted (scope: RunScope) =
    let wasFiltered = (scope = RunScope.Partial)

    TestRunCompleted
        { RunId = Guid.NewGuid()
          TotalElapsed = TimeSpan.Zero
          Outcome = Normal
          Results = Map.ofList [ "p1", TestsPassed("ok", wasFiltered, TimeSpan.Zero) ]
          Verification = Ran scope }

[<Fact(Timeout = 15000)>]
let ``a trigger refused while a finished check's fold holds the key is retained`` () =
    let handler = CovPlugin.create "/tmp/ratchet.json" "/tmp"
    let refused = stubCoverageCtx (fun _ _ -> PF.SlotBusy)

    let owed =
        handler.Update refused handler.Init (runCompleted RunScope.Partial)
        |> Async.RunSynchronously

    test <@ owed.Owed = Some RunScope.Partial @>

    // Positive control on the same path: an accepted claim owes nothing, so the
    // retention above is the refusal's doing and not an unconditional write.
    let accepted =
        handler.Update (stubCoverageCtx (fun _ _ -> PF.Claimed)) handler.Init (runCompleted RunScope.Partial)
        |> Async.RunSynchronously

    test <@ accepted.Owed = None @>

[<Fact(Timeout = 15000)>]
let ``the trigger retained behind a finished check is run by that check's result fold`` () =
    withTempDir "coverage-owed-drained" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        // One file below the default 100% floor: the drained check has something to
        // judge, so its verdict names the scope it ran under.
        File.WriteAllText(Path.Combine(dir, "coverage.cobertura.xml"), coberturaXml "MyModule.fs" [ (1, 1); (2, 0) ])
        File.WriteAllText(configPath, defaultThresholdsJson)

        let handler = CovPlugin.create configPath dir

        // The newer test run arrives while the key is held: impact-FILTERED, unlike
        // the full-suite run whose fold holds it.
        let retained =
            handler.Update (stubCoverageCtx (fun _ _ -> PF.SlotBusy)) handler.Init (runCompleted RunScope.Partial)
            |> Async.RunSynchronously

        test <@ retained.Owed = Some RunScope.Partial @>

        let mutable scheduled: (string * Async<CovPlugin.CoverageMsg>) option = None

        let holderFold =
            stubCoverageCtx (fun key work ->
                scheduled <- Some(key, work)
                PF.Claimed)

        // The finished check's own result fold: it holds the key, so its claim is the
        // one that can start the retained trigger.
        let drained =
            handler.Update holderFold retained (Custom(CovPlugin.CheckDone(CovPlugin.Passed, TimeSpan.Zero)))
            |> Async.RunSynchronously

        test <@ drained.Owed = None @>
        // The holder's own verdict still lands — draining does not eat it.
        test <@ drained.LastCheckPassed = Some true @>

        match scheduled with
        | None -> Assert.Fail "the retained trigger must reach a coverage check"
        | Some(key, work) ->
            test <@ key = "coverage-check" @>

            // And it checks for the NEWER run: the shortfall is gated by the retained
            // `Partial` scope (a notice), not by the full-suite holder's (a red).
            match work |> Async.RunSynchronously with
            | CovPlugin.CheckDone(CovPlugin.NotGatedFiltered count, _) -> test <@ count = 1 @>
            | other -> Assert.Fail $"expected the retained Partial trigger's verdict, got {other}")
