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

let private emitRunCompleted (host: PluginHost) =
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

        // Impact-filtered run: RanFullSuite = false.
        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Normal
              Results = Map.empty
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

[<Fact(Timeout = 15000)>]
let ``plugin skips check when no coverage XML found`` () =
    withTempDir "coverage" (fun dir ->
        let configPath = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(configPath, defaultThresholdsJson)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        emitRunCompleted host

        // Plugin should complete (no XML = no failures to report)
        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            10000

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

[<Fact(Timeout = 15000)>]
let ``plugin refreshes baseline after passing full-suite run`` () =
    withTempDir "coverage" (fun dir ->
        let projectDir = Path.Combine(dir, "MyProject")
        Directory.CreateDirectory(projectDir) |> ignore

        let xmlPath = Path.Combine(projectDir, "coverage.cobertura.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")

        // 100% coverage — will pass
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        File.WriteAllText(configPath, thresholdsJsonWithOverride "MyModule.fs" 50 0)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        // Full-suite run
        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Normal
              Results = Map.empty
              RanFullSuite = true }

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            10000

        // baseline.xml should have been written (refreshBaselines after passing full-suite run)
        let baselinePath = Path.Combine(projectDir, "coverage.baseline.xml")
        test <@ File.Exists(baselinePath) @>

        let errors = host.GetErrorsByPlugin("coverage")
        test <@ errors.IsEmpty @>)

[<Fact(Timeout = 15000)>]
let ``plugin does not refresh baseline after partial run`` () =
    withTempDir "coverage" (fun dir ->
        let projectDir = Path.Combine(dir, "MyProject")
        Directory.CreateDirectory(projectDir) |> ignore

        let xmlPath = Path.Combine(projectDir, "coverage.cobertura.xml")
        let baselinePath = Path.Combine(projectDir, "coverage.baseline.xml")
        let configPath = Path.Combine(dir, "coverage-ratchet.json")

        // Baseline has line 2 with hits=99 (high-watermark).
        // Current run has line 2 with hits=1.
        // mergeIntoBaselines preserves max hits (baseline stays at 99).
        // refreshBaselines would replace baseline with current XML (line 2 = hits=1).
        // After a partial run, the baseline's high hit count must survive.
        File.WriteAllText(baselinePath, coberturaXml "MyModule.fs" [ (1, 1); (2, 99) ])
        File.WriteAllText(xmlPath, coberturaXml "MyModule.fs" [ (1, 1); (2, 1) ])
        File.WriteAllText(configPath, thresholdsJsonWithOverride "MyModule.fs" 50 0)

        let host = PluginHost.create (Unchecked.defaultof<_>) dir
        host.RegisterHandler(FsHotWatch.Coverage.CoveragePlugin.create configPath dir)

        host.EmitTestRunCompleted
            { RunId = Guid.NewGuid()
              TotalElapsed = TimeSpan.Zero
              Outcome = Normal
              Results = Map.empty
              RanFullSuite = false }

        waitUntil
            (fun () ->
                match host.GetStatus("coverage") with
                | Some(Completed _) -> true
                | _ -> false)
            10000

        // If refreshBaselines ran, the baseline would have hits="1" (current run).
        // merge preserves the high-watermark, so hits="99" must still be present.
        let baseline = File.ReadAllText(baselinePath)
        test <@ baseline.Contains("hits=\"99\"") @>)
