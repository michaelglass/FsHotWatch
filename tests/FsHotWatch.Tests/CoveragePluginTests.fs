module FsHotWatch.Tests.CoveragePluginTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

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
