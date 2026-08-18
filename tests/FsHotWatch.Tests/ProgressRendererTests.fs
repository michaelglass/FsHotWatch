module FsHotWatch.Tests.ProgressRendererTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.ProgressRenderer
open FsHotWatch.Tests.TestHelpers

/// Fixed "now" so elapsed calculations are deterministic across runs.
let private now = DateTime(2026, 4, 17, 14, 3, 20, DateTimeKind.Utc)

/// Strip ANSI colour escapes (ESC [ ... letter) so tests can assert on text shape.
let private stripAnsi (s: string) : string =
    System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[0-9;]*[A-Za-z]", "")

let private stripMany (xs: string list) : string list = xs |> List.map stripAnsi

let private makeSubtask (key: string) (label: string) (agoSec: float) : Subtask =
    { Key = key
      Label = label
      StartedAt = now - TimeSpan.FromSeconds(agoSec) }

let private completedRun (ago: TimeSpan) (elapsed: TimeSpan) (summary: string option) : RunRecord =
    { StartedAt = now - ago
      Elapsed = elapsed
      Outcome = CompletedRun
      Summary = summary
      ActivityTail = [] }

let private failedRun (ago: TimeSpan) (elapsed: TimeSpan) (error: string) : RunRecord =
    { StartedAt = now - ago
      Elapsed = elapsed
      Outcome = FailedRun error
      Summary = None
      ActivityTail = [] }

let private timedOutRun (ago: TimeSpan) (elapsed: TimeSpan) (reason: string) : RunRecord =
    { StartedAt = now - ago
      Elapsed = elapsed
      Outcome = TimedOut reason
      Summary = None
      ActivityTail = [] }

// ---------------- Compact mode ----------------

[<Fact(Timeout = 15000)>]
let ``compact Completed shows check glyph elapsed and summary`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(3.2))
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 3.2) (TimeSpan.FromSeconds 3.2) (Some "built 4 projects"))
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "Build" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "Build" @>
    test <@ line.Contains "\u2713" @> // ✓
    test <@ line.Contains "3.2s" @>
    test <@ line.Contains "built 4 projects" @>

[<Fact(Timeout = 15000)>]
let ``compact Completed with no LastRun does not display 0ms timing`` () =
    // A Completed status with no RunRecord (a cache-replay path that bypassed the
    // Running phase) must omit the timing rather than display a misleading "(0ms)".
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(2.0))
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "build" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "build" @>
    test <@ not (line.Contains "0ms") @>
    test <@ not (line.Contains "(0") @>

[<Fact(Timeout = 15000)>]
let ``compact Completed with zero-elapsed LastRun does not display 0ms timing`` () =
    // Same contract for an Elapsed of exactly zero: real builds take milliseconds,
    // so zero means "unknown" (the cache-replay synthetic record), not 0ms.
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(2.0))
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 0.0) TimeSpan.Zero (Some "built 19 projects"))
          Diagnostics = { Errors = 0; Warnings = 1 } }

    let lines = renderPlugin Compact true now "build" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "build" @>
    test <@ not (line.Contains "0ms") @>
    test <@ not (line.Contains "(0") @>

[<Fact(Timeout = 15000)>]
let ``verbose Completed with zero-elapsed LastRun does not display 0ms timing`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(2.0))
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 0.0) TimeSpan.Zero None)
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Verbose true now "build" parsed |> stripMany
    let header = lines.[0]
    test <@ header.Contains "build" @>
    test <@ not (header.Contains "0ms") @>
    test <@ not (header.Contains "(0") @>

[<Fact(Timeout = 15000)>]
let ``compact Completed with ledger errors shows warn glyph and count`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(3.2))
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 3.2) (TimeSpan.FromSeconds 3.2) None)
          Diagnostics = { Errors = 2; Warnings = 0 } }

    let lines = renderPlugin Compact true now "Lint" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "⚠" @> // ⚠
    test <@ not (line.Contains "✓") @> // not ✓
    test <@ line.Contains "2 error(s)" @>

[<Fact(Timeout = 15000)>]
let ``compact Completed with only warnings respects warningsAreFailures flag`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(1.0))
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) None)
          Diagnostics = { Errors = 0; Warnings = 3 } }

    let strict = renderPlugin Compact true now "Lint" parsed |> stripMany
    test <@ strict.[0].Contains "⚠" @>

    let lax = renderPlugin Compact false now "Lint" parsed |> stripMany
    test <@ lax.[0].Contains "✓" @>
    test <@ not (lax.[0].Contains "⚠") @>

[<Fact(Timeout = 15000)>]
let ``compact Running with subtasks lists them`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Running(now - TimeSpan.FromSeconds(72.0))
          Subtasks =
            [ makeSubtask "A" "running A" 10.0
              makeSubtask "B" "running B" 8.0
              makeSubtask "C" "running C" 2.0 ]
          ActivityTail = [ "queued 3" ]
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "TestPrune" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "\u2026" @> // ⋯ / …
    test <@ line.Contains "TestPrune" @>
    test <@ line.Contains "3 running" @>
    test <@ line.Contains "A" && line.Contains "B" && line.Contains "C" @>

[<Fact(Timeout = 15000)>]
let ``compact Running with no subtasks shows last activity line`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Running(now - TimeSpan.FromSeconds(5.0))
          Subtasks = []
          ActivityTail = [ "loading rules"; "linting FileA.fs" ]
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "Lint" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "linting FileA.fs" @>

[<Fact(Timeout = 15000)>]
let ``compact Failed shows truncated error first line`` () =
    let longErr = String.replicate 120 "x"
    let multiline = "first line of error\nsecond line\nthird line"

    let parsed: ParsedPluginStatus =
        { Status = StatusView.Failed(multiline, now - TimeSpan.FromSeconds 6.4)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(failedRun (TimeSpan.FromSeconds 6.4) (TimeSpan.FromSeconds 6.4) multiline)
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "Lint" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "\u2717" @> // ✗
    test <@ line.Contains "first line of error" @>
    test <@ not (line.Contains "second line") @>

    // Ultra-long single line is truncated to <= ~80 chars of error text.
    let parsedLong: ParsedPluginStatus =
        { Status = StatusView.Failed(longErr, now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(failedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) longErr)
          Diagnostics = DiagnosticCounts.empty }

    let linesLong = renderPlugin Compact true now "Lint" parsedLong |> stripMany
    test <@ linesLong.Length = 1 @>
    test <@ linesLong.[0].Length < 200 @>

[<Fact(Timeout = 15000)>]
let ``compact Idle with history shows last-run recap`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Idle
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 30.0) (TimeSpan.FromSeconds 4.1) (Some "no issues"))
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "Analyzers" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "Analyzers" @>
    test <@ line.Contains "last" @>
    test <@ line.Contains "4.1s" @>
    test <@ line.Contains "no issues" @>

[<Fact(Timeout = 15000)>]
let ``compact Idle with no history is single line name`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Idle
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "Coverage" parsed |> stripMany
    test <@ lines.Length = 1 @>
    test <@ lines.[0].Contains "Coverage" @>

// ---------------- Verbose mode ----------------

[<Fact(Timeout = 15000)>]
let ``verbose Running emits header plus subtask tree plus recent`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Running(now - TimeSpan.FromSeconds(72.0))
          Subtasks =
            [ makeSubtask "FooTests" "running FooTests" 48.0
              makeSubtask "BarTests" "compiling" 62.0
              makeSubtask "BazTests" "queued" 22.0 ]
          ActivityTail = [ "queued 3 projects"; "dotnet test FooTests" ]
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Verbose true now "TestPrune" parsed |> stripMany
    // Expected shape: header + 3 subtasks + "recent:" + 2 activity lines = 7 lines minimum.
    test <@ lines.Length >= 7 @>
    test <@ lines.[0].Contains "TestPrune" @>
    let joined = String.concat "\n" lines
    test <@ joined.Contains "FooTests" @>
    test <@ joined.Contains "BarTests" @>
    test <@ joined.Contains "BazTests" @>
    // Tree glyphs: all but last subtask use ├─, last uses └─.
    test <@ joined.Contains "\u251c\u2500" @> // ├─
    test <@ joined.Contains "\u2514\u2500" @> // └─
    test <@ joined.Contains "recent" @>
    test <@ joined.Contains "dotnet test FooTests" @>

[<Fact(Timeout = 15000)>]
let ``verbose Running preserves leading whitespace in activity tail entries (queued re-run nesting)`` () =
    // TestPrunePlugin emits "queued re-run (tests already running)" with a leading
    // "  ↳ " so the renderer's 8-space indent compounds to 10, nesting the entry
    // under the in-flight test-result lines. The renderer must not strip or
    // collapse embedded leading whitespace.
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Running(now - TimeSpan.FromSeconds 30.0)
          Subtasks = []
          ActivityTail =
            [ "Intelligence.Build.Dev.Tests: failed"
              "  ↳ queued re-run (tests already running)"
              "Intelligence.Tests.Database: passed" ]
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Verbose true now "test-prune" parsed |> stripMany
    let joined = String.concat "\n" lines
    // Sibling test-result lines render with exactly 8 spaces of indent.
    test
        <@
            lines
            |> List.exists (fun l -> l = "        Intelligence.Build.Dev.Tests: failed")
        @>

    test
        <@
            lines
            |> List.exists (fun l -> l = "        Intelligence.Tests.Database: passed")
        @>
    // 2 extra caller-supplied spaces → 10 total.
    test
        <@
            lines
            |> List.exists (fun l -> l = "          ↳ queued re-run (tests already running)")
        @>

    test <@ joined.Contains "↳ queued re-run" @>

[<Fact(Timeout = 15000)>]
let ``verbose Failed shows started, error detail, and recent`` () =
    let startedAt = now - TimeSpan.FromSeconds 6.4
    let err = "FileA.fs(12,4): FS0020: ...\nFileA.fs(33,1): FS0025: ..."

    let parsed: ParsedPluginStatus =
        { Status = StatusView.Failed(err, now)
          Subtasks = []
          ActivityTail = [ "loading rules"; "linting FileA.fs" ]
          LastRun =
            Some
                { StartedAt = startedAt
                  Elapsed = TimeSpan.FromSeconds 6.4
                  Outcome = FailedRun err
                  Summary = None
                  ActivityTail = [ "loading rules"; "linting FileA.fs" ] }
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Verbose true now "Lint" parsed |> stripMany
    let joined = String.concat "\n" lines
    test <@ joined.Contains "Lint" @>
    test <@ joined.Contains "started" @>
    test <@ joined.Contains "error detail" @>
    test <@ joined.Contains "FS0020" @>
    test <@ joined.Contains "FS0025" @>
    test <@ joined.Contains "recent" @>
    test <@ joined.Contains "linting FileA.fs" @>

[<Fact(Timeout = 15000)>]
let ``verbose Completed shows header started elapsed summary`` () =
    let startedAt = now - TimeSpan.FromSeconds 3.2

    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed now
          Subtasks = []
          ActivityTail = [ "dotnet build sln" ]
          LastRun =
            Some
                { StartedAt = startedAt
                  Elapsed = TimeSpan.FromSeconds 3.2
                  Outcome = CompletedRun
                  Summary = Some "built 4 projects"
                  ActivityTail = [ "dotnet build sln" ] }
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Verbose true now "Build" parsed |> stripMany
    let joined = String.concat "\n" lines
    test <@ joined.Contains "Build" @>
    test <@ joined.Contains "started" @>
    test <@ joined.Contains "3.2s" @>
    test <@ joined.Contains "built 4 projects" @>

[<Fact(Timeout = 15000)>]
let ``verbose Completed with empty activity tail hides recent section`` () =
    let startedAt = now - TimeSpan.FromSeconds 1.0

    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed now
          Subtasks = []
          ActivityTail = []
          LastRun =
            Some
                { StartedAt = startedAt
                  Elapsed = TimeSpan.FromSeconds 1.0
                  Outcome = CompletedRun
                  Summary = Some "ok"
                  ActivityTail = [] }
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Verbose true now "Build" parsed |> stripMany
    let joined = String.concat "\n" lines
    test <@ not (joined.Contains "recent") @>

// ---------------- renderAll ----------------

[<Fact(Timeout = 15000)>]
let ``renderAll concatenates per-plugin blocks`` () =
    let statuses =
        Map.ofList
            [ "Build",
              { Status = StatusView.Completed now
                Subtasks = []
                ActivityTail = []
                LastRun = Some(completedRun (TimeSpan.FromSeconds 3.0) (TimeSpan.FromSeconds 3.0) (Some "ok"))
                Diagnostics = DiagnosticCounts.empty }
              "Lint",
              { Status = StatusView.Idle
                Subtasks = []
                ActivityTail = []
                LastRun = None
                Diagnostics = DiagnosticCounts.empty } ]

    let lines = renderAll Compact true now statuses |> stripMany
    // Compact is exactly one line per plugin.
    test <@ lines.Length = 2 @>
    let joined = String.concat "\n" lines
    test <@ joined.Contains "Build" @>
    test <@ joined.Contains "Lint" @>

// ---------------- Agent mode ----------------

module private AgentFixtures =
    let okStatus (summary: string option) : ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) summary)
          Diagnostics = DiagnosticCounts.empty }

    let failStatus (err: string) : ParsedPluginStatus =
        { Status = StatusView.Failed(err, now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(failedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) err)
          Diagnostics = DiagnosticCounts.empty }

    let runningStatus () : ParsedPluginStatus =
        { Status = StatusView.Running(now - TimeSpan.FromSeconds 2.0)
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let warnStatus () : ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) None)
          Diagnostics = { Errors = 0; Warnings = 3 } }

    let idleNoHistory () : ParsedPluginStatus =
        { Status = StatusView.Idle
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let idleCompleted (summary: string option) : ParsedPluginStatus =
        { Status = StatusView.Idle
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 10.0) (TimeSpan.FromSeconds 2.0) summary)
          Diagnostics = DiagnosticCounts.empty }

    let idleFailed (err: string) : ParsedPluginStatus =
        { Status = StatusView.Idle
          Subtasks = []
          ActivityTail = []
          LastRun = Some(failedRun (TimeSpan.FromSeconds 10.0) (TimeSpan.FromSeconds 2.0) err)
          Diagnostics = DiagnosticCounts.empty }

    /// Render a single plugin in agent mode, return list (may be empty for omitted).
    let agentLine name parsed = renderPlugin Agent true now name parsed

    let agentAll (statuses: (string * ParsedPluginStatus) list) =
        renderAll Agent true now (Map.ofList statuses)

    let agentAllLax (statuses: (string * ParsedPluginStatus) list) =
        renderAll Agent false now (Map.ofList statuses)

    /// The COMPACT rendering of one plugin, joined — the fixed-width surface that keeps
    /// its 80-character budget (see the agent/compact pair of tests below).
    let compactLine name parsed =
        renderPlugin Compact true now name parsed |> String.concat "\n"

open AgentFixtures

[<Fact(Timeout = 15000)>]
let ``agent renderAll emits banner as first line`` () =
    let lines = agentAll [ "build", okStatus None ]
    test <@ lines.Length >= 1 @>
    test <@ lines.[0].StartsWith "# fshw agent mode" @>

[<Fact(Timeout = 15000)>]
let ``agent banner lists expected commands`` () =
    let lines = agentAll [ "build", okStatus None ]
    let banner = lines.[0]

    [ "check"; "status"; "format"; "scan"; "rerun" ]
    |> List.iter (fun cmd -> test <@ banner.Contains cmd @>)

[<Fact(Timeout = 15000)>]
let ``agent renderAll omits Idle plugins with no LastRun`` () =
    let lines = agentAll [ "build", okStatus None; "coverage", idleNoHistory () ]

    let joined = String.concat "\n" lines
    test <@ joined.Contains "build:" @>
    test <@ not (joined.Contains "coverage") @>

[<Fact(Timeout = 15000)>]
let ``agent renderPlugin for Idle with no LastRun returns empty list`` () =
    test <@ List.isEmpty (agentLine "coverage" (idleNoHistory ())) @>

[<Fact(Timeout = 15000)>]
let ``agent Idle-with-completed-LastRun renders as ok`` () =
    let lines = agentLine "analyze" (idleCompleted (Some "clean"))
    test <@ lines = [ "analyze: ok" ] @>

[<Fact(Timeout = 15000)>]
let ``agent Idle-with-failed-LastRun renders as fail with summary`` () =
    let lines = agentLine "test" (idleFailed "2 failed in FsHotWatch.Tests")
    test <@ lines.Length = 1 @>
    test <@ lines.[0] = "test: fail summary=\"2 failed in FsHotWatch.Tests\"" @>

[<Fact(Timeout = 15000)>]
let ``agent ok line is plain "<name>: ok" with no summary`` () =
    let lines = agentLine "build" (okStatus (Some "built 4 projects"))
    test <@ lines = [ "build: ok" ] @>

[<Fact(Timeout = 15000)>]
let ``agent fail line includes summary`` () =
    let lines = agentLine "test" (failStatus "2 failed in FsHotWatch.Tests")
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.StartsWith "test: fail summary=\"" @>
    test <@ line.Contains "2 failed in FsHotWatch.Tests" @>
    test <@ line.EndsWith "\"" @>

[<Fact(Timeout = 15000)>]
let ``agent warn state fires when warnings present and warningsAreFailures=true`` () =
    let lines = agentLine "lint" (warnStatus ())
    test <@ lines.Length = 1 @>
    test <@ lines.[0].StartsWith "lint: warn" @>

[<Fact(Timeout = 15000)>]
let ``agent warn demotes to ok when warningsAreFailures=false`` () =
    let lines = renderPlugin Agent false now "lint" (warnStatus ())
    test <@ lines = [ "lint: ok" ] @>

[<Fact(Timeout = 15000)>]
let ``agent Running line has no summary`` () =
    let lines = agentLine "build" (runningStatus ())
    test <@ lines = [ "build: running" ] @>

[<Fact(Timeout = 15000)>]
let ``agent fail summary uses first non-empty line`` () =
    let err = "first line of error\nsecond line\nthird line"
    let lines = agentLine "lint" (failStatus err)
    test <@ lines.[0].Contains "first line of error" @>
    // Newlines collapse to spaces, so "second line" may appear — inside the one
    // quoted summary. What matters is that the output stays a single line.
    test <@ not (lines.[0].Contains "\n") @>

/// AUTOMATION-201, reworked. The agent line has NO width budget, so it must not have a
/// truncator: it is line-oriented parseable output, and the redraw that the 80-character
/// budget existed for is guarded by `UI.isInteractive` — which is false exactly when
/// agent mode is what you get. The cap was a fixed-width constraint copied onto a
/// surface with no width, and what it cut was the payload: the reported symptom is a
/// list of affected projects severed mid-name.
///
/// AC2 wants "every affected project (no truncation)", and this is the surface an
/// automated caller reads.
[<Fact(Timeout = 15000)>]
let ``agent fail summary is NOT truncated — every affected project survives whole`` () =
    let projects = [ for i in 1..6 -> $"Intelligence.Build.Dev.Tests.Number%d{i}" ]

    let summary =
        "6 waiting on build (tests did not run): " + String.concat ", " projects

    test <@ summary.Length > 80 @> // the case the old cap destroyed

    let line = (agentLine "test-prune" (failStatus summary)).[0]

    let m = System.Text.RegularExpressions.Regex.Match(line, "summary=\"([^\"]*)\"")

    test <@ m.Success @>
    let rendered = m.Groups.[1].Value

    for p in projects do
        test <@ rendered.Contains p @>

    test <@ rendered = summary @>
    // Still ONE line: newlines collapse, so an untruncated summary cannot break the
    // one-line-per-plugin contract an agent parses.
    test <@ not (line.Contains "\n") @>

/// The COMPACT/VERBOSE budget is untouched, and must stay so. Those blocks are erased
/// and rewritten by counting the lines printed, so a summary wide enough to WRAP makes
/// the erase count wrong and smears the display. Without this, "remove the truncation"
/// would read as licence to remove it everywhere.
[<Fact(Timeout = 15000)>]
let ``compact fail summary still respects the fixed-width budget and names what it dropped`` () =
    let long = String.replicate 200 "x"
    let line = compactLine "lint" (failStatus long)
    test <@ line.Contains "… (+" @>
    test <@ line.Contains "more)" @>
    test <@ not (line.Contains(String.replicate 100 "x")) @>

/// A summary already inside the budget is passed through untouched — no marker, no
/// ellipsis. Without this, a truncator that mangled every string would pass the test
/// above just as well.
[<Fact(Timeout = 15000)>]
let ``agent fail summary under the budget is left exactly as-is`` () =
    let short = "lint failed on 2 files"
    let lines = agentLine "lint" (failStatus short)

    let m =
        System.Text.RegularExpressions.Regex.Match(lines.[0], "summary=\"([^\"]*)\"")

    test <@ m.Success @>
    test <@ m.Groups.[1].Value = short @>

[<Fact(Timeout = 15000)>]
let ``agent fail summary escapes embedded double quotes`` () =
    let err = "he said \"boom\" then exited"
    let lines = agentLine "test" (failStatus err)
    let line = lines.[0]
    test <@ line.Contains "\\\"boom\\\"" @>
    // Must remain a single well-formed summary="..." pair (exactly 2 unescaped quotes).
    let unescapedQuotes =
        // Count quotes not preceded by backslash.
        System.Text.RegularExpressions.Regex.Matches(line, "(?<!\\\\)\"").Count

    test <@ unescapedQuotes = 2 @>

[<Fact(Timeout = 15000)>]
let ``agent emits no ANSI escapes`` () =
    let statuses =
        [ "build", okStatus (Some "ok")
          "test", failStatus "boom"
          "lint", warnStatus ()
          "analyze", runningStatus () ]

    let lines = agentAll statuses
    let joined = String.concat "\n" lines
    test <@ not (joined.Contains "\x1b") @>
    test <@ stripAnsi joined = joined @>

// ----- next-step rules -----

[<Fact(Timeout = 15000)>]
let ``agent next is check when any plugin is running`` () =
    let statuses = [ "build", failStatus "compile error"; "test", runningStatus () ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fshw --agent check" @>

[<Fact(Timeout = 15000)>]
let ``agent next is build when build failed even if others also failed`` () =
    let statuses =
        [ "build", failStatus "compile error"
          "test", failStatus "2 failed"
          "lint", failStatus "warnings" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fshw --agent status build" @>

[<Fact(Timeout = 15000)>]
let ``agent next is test when build ok but test failed`` () =
    let statuses =
        [ "build", okStatus None
          "test", failStatus "boom"
          "lint", failStatus "warnings" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fshw --agent status test" @>

[<Fact(Timeout = 15000)>]
let ``agent next picks lint before analyze when both fail`` () =
    let statuses =
        [ "analyze", failStatus "bad"
          "lint", failStatus "warn"
          "coverage", failStatus "low" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fshw --agent status lint" @>

[<Fact(Timeout = 15000)>]
let ``agent next picks analyze before format-check and coverage`` () =
    let statuses =
        [ "coverage", failStatus "low"
          "format-check", failStatus "unformatted"
          "analyze", failStatus "bad" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fshw --agent status analyze" @>

[<Fact(Timeout = 15000)>]
let ``agent next picks format-check before coverage`` () =
    let statuses =
        [ "coverage", failStatus "low"; "format-check", failStatus "unformatted" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fshw --agent status format-check" @>

[<Fact(Timeout = 15000)>]
let ``agent next is status when only warnings and warningsAreFailures=true`` () =
    let statuses = [ "build", okStatus None; "lint", warnStatus () ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fshw --agent status" @>

[<Fact(Timeout = 15000)>]
let ``agent next is done when warnings present but warningsAreFailures=false`` () =
    let statuses = [ "build", okStatus None; "lint", warnStatus () ]

    let lines = agentAllLax statuses
    test <@ List.last lines = "next: done" @>

[<Fact(Timeout = 15000)>]
let ``agent next is done when all clean`` () =
    let statuses =
        [ "build", okStatus None
          "test", okStatus None
          "lint", okStatus None
          "analyze", okStatus None ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: done" @>

// ----- primary subtask rendering -----

[<Fact(Timeout = 15000)>]
let ``compact Running prefers primary subtask label over activity tail`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Running(now - TimeSpan.FromSeconds(1.5))
          Subtasks = [ makeSubtask "primary" "running 3 selected tests" 1.5 ]
          ActivityTail = [ "processing bar.fs" ]
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "test-prune" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "running 3 selected tests" @>
    test <@ not (line.Contains "processing bar.fs") @>

[<Fact(Timeout = 15000)>]
let ``compact Idle shows explicit summary not last log line`` () =
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(2.0))
          Subtasks = []
          ActivityTail = [ "processing foo.fs"; "processing bar.fs" ]
          LastRun = Some(completedRun (TimeSpan.FromSeconds 2.0) (TimeSpan.FromSeconds 2.0) (Some "5 passed, 0 failed"))
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "test-prune" parsed |> stripMany
    let line = lines.[0]
    test <@ line.Contains "5 passed, 0 failed" @>
    test <@ not (line.Contains "processing bar.fs") @>

// ----- regex roundtrip -----

[<Fact(Timeout = 15000)>]
let ``agent output lines match the parseable grammar`` () =
    let statuses =
        [ "build", okStatus (Some "built 4 projects")
          "test", failStatus "he said \"boom\"\nthen exited"
          "lint", warnStatus ()
          "analyze", runningStatus ()
          "coverage", idleCompleted (Some "covered") ]

    let lines = agentAll statuses

    let pattern =
        "^(?:# .*|[a-z-]+: (ok|fail|warn|running|timed-out)(?: summary=\"(?:[^\"\\\\]|\\\\.)*\")?|next: .+)$"

    let rx = System.Text.RegularExpressions.Regex(pattern)

    for line in lines do
        test <@ rx.IsMatch line @>

// ---------------- TimedOut rendering ----------------

let private timedOutStatus (reason: string) : ParsedPluginStatus =
    { Status = StatusView.Failed($"timed out: {reason}", now - TimeSpan.FromSeconds 1.0)
      Subtasks = []
      ActivityTail = []
      LastRun = Some(timedOutRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) reason)
      Diagnostics = DiagnosticCounts.empty }

[<Fact(Timeout = 15000)>]
let ``compact Failed with TimedOut outcome uses timeout glyph and label`` () =
    let parsed = timedOutStatus "exceeded 60s"

    let lines = renderPlugin Compact true now "Build" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "⏱" @> // ⏱
    test <@ not (line.Contains "✗") @> // no ✗
    test <@ line.Contains "timed out" @>

[<Fact(Timeout = 15000)>]
let ``verbose Failed with TimedOut outcome uses timeout glyph`` () =
    let parsed = timedOutStatus "exceeded 60s"

    let lines = renderPlugin Verbose true now "Build" parsed |> stripMany
    test <@ lines |> List.exists (fun l -> l.Contains "⏱") @>

[<Fact(Timeout = 15000)>]
let ``agent Failed with TimedOut outcome emits timed-out token`` () =
    let parsed = timedOutStatus "exceeded 60s"
    let line = renderPlugin Agent true now "build" parsed |> List.head |> stripAnsi
    test <@ line.Contains "build: timed-out" @>
    test <@ line.Contains "timed out" @>

// ---------------- Wedge + fail-closed rendering (AUTOMATION-147) ----------------
//
// A plugin that has not completed is NEVER rendered ✓, and a plugin Running past
// the wedge bound is NAMED as wedged, in words, in every mode — a fault must not
// have to be detected by noticing what isn't printed.

module private WedgeFixtures =
    /// Running long past the default wedge bound (verdict deadline 60m + 5m grace).
    let wedgedSince = now - TimeSpan.FromHours 2.0

    let running (since: DateTime) : ParsedPluginStatus =
        { Status = StatusView.Running since
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let completedNoRecord () : ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds 2.0)
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

open WedgeFixtures

[<Fact(Timeout = 15000)>]
let ``compact Running past the wedge bound is named WEDGED, not merely running`` () =
    let lines =
        renderPlugin Compact true now "analyzers" (running wedgedSince) |> stripMany

    test <@ lines.Length = 1 @>
    test <@ lines.[0].Contains "WEDGED" @>
    test <@ lines.[0].Contains "no completion in" @>
    test <@ lines.[0].Contains "⚠" @>
    test <@ not (lines.[0].Contains "…") @>

[<Fact(Timeout = 15000)>]
let ``compact Running under the bound is NOT declared wedged`` () =
    // A 12-minute run is "cannot tell yet", not a wedge — the daemon-side log
    // escalations carry the uncertainty.
    let lines =
        renderPlugin Compact true now "test-prune" (running (now - TimeSpan.FromMinutes 12.0))
        |> stripMany

    test <@ not (lines.[0].Contains "WEDGED") @>
    test <@ lines.[0].Contains "…" @>

[<Fact(Timeout = 15000)>]
let ``verbose Running past the wedge bound is named WEDGED in the header`` () =
    let lines =
        renderPlugin Verbose true now "analyzers" (running wedgedSince) |> stripMany

    test <@ lines.[0].Contains "WEDGED" @>
    test <@ lines.[0].Contains "⚠" @>

[<Fact(Timeout = 15000)>]
let ``agent Running past the wedge bound tokens as wedged with the wedge words`` () =
    let lines = agentLine "analyzers" (running wedgedSince)
    test <@ lines.Length = 1 @>
    test <@ lines.[0].StartsWith "analyzers: wedged" @>
    test <@ lines.[0].Contains "WEDGED: started" @>
    test <@ lines.[0].Contains "no completion in" @>

[<Fact(Timeout = 15000)>]
let ``agent nextStep for a wedged plugin points at status, never done`` () =
    let lines = agentAll [ "analyzers", running wedgedSince ]
    let next = lines |> List.last
    test <@ next.Contains "status" @>
    test <@ not (next.Contains "done") @>

[<Fact(Timeout = 15000)>]
let ``compact Completed with no run record renders a warn with words, never a bare check`` () =
    let lines =
        renderPlugin Compact true now "build" (completedNoRecord ()) |> stripMany

    test <@ lines.Length = 1 @>
    test <@ lines.[0].Contains "⚠" @>
    test <@ not (lines.[0].Contains "✓") @>
    test <@ lines.[0].Contains "no run record" @>

[<Fact(Timeout = 15000)>]
let ``verbose Completed with no run record renders a warn with words, never a bare check`` () =
    let lines =
        renderPlugin Verbose true now "build" (completedNoRecord ()) |> stripMany

    test <@ lines.[0].Contains "⚠" @>
    test <@ not (lines.[0].Contains "✓") @>
    test <@ lines.[0].Contains "no run record" @>

[<Fact(Timeout = 15000)>]
let ``agent Completed with no run record tokens as warn with the missing-record words`` () =
    let lines = agentLine "build" (completedNoRecord ())
    test <@ lines.Length = 1 @>
    test <@ lines.[0].StartsWith "build: warn" @>
    test <@ lines.[0].Contains "no run record" @>

[<Fact(Timeout = 15000)>]
let ``verbose Completed with zero elapsed states it in words instead of omitting the line`` () =
    // The `elapsed:` line is always present: zero elapsed is stated as a
    // replayed/synthetic record, never left to be inferred from its absence
    // (operators were grepping for the missing line).
    let parsed: ParsedPluginStatus =
        { Status = StatusView.Completed(now - TimeSpan.FromSeconds(2.0))
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 0.0) TimeSpan.Zero (Some "replayed"))
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Verbose true now "build" parsed |> stripMany
    test <@ lines |> List.exists (fun l -> l.Contains "elapsed: not measured") @>
    test <@ lines |> List.exists (fun l -> l.Contains "started:") @>

// ---------------- AUTOMATION-198: a run that verified nothing is not a success ----------------
//
// The observed defect: `fshw check` on a diff with four brand-new tests rendered
// `✓ test-prune — 0 passed, 0 failed in 0 projects` and then refused to certify (exit 3,
// NO VERDICT). A reader scanning plugin glyphs saw success; only the verdict disagreed.
//
// Every one of these has a PAIRED positive control on the same rendering path: "it isn't
// green" passes trivially if the renderer stopped emitting ✓ at all.

/// A terminal that carries the verified-nothing verdict — the summary the test-prune
/// plugin records for a run that executed no project. Built through `RunSummary`, the
/// producer's own constructor, so a change to the marker breaks the test rather than
/// silently making it assert nothing.
let private verifiedNothingStatus () : ParsedPluginStatus =
    okStatus (Some(RunSummary.nothingVerified "0 test project(s) ran, no test executed"))

/// The control: a run that DID execute, worded exactly as the healthy line is. Note it
/// carries `selected: no` too — that is a MODE flag, not a count, and no surface may
/// read a verdict out of it.
let private genuinePassStatus () : ParsedPluginStatus =
    okStatus (Some "6 passed, 0 failed in 6 projects (selected: no, slowest: Unit 12.0s)")

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: compact renders a verified-nothing run as warn, never a check`` () =
    let lines =
        renderPlugin Compact true now "test-prune" (verifiedNothingStatus ())
        |> stripMany

    test <@ lines.Length = 1 @>
    test <@ lines.[0].Contains "⚠" @>
    test <@ not (lines.[0].Contains "✓") @>
    test <@ lines.[0].Contains "NOTHING VERIFIED" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: compact still renders a run that DID execute as a check`` () =
    // Positive control for the test above.
    let lines =
        renderPlugin Compact true now "test-prune" (genuinePassStatus ()) |> stripMany

    test <@ lines.Length = 1 @>
    test <@ lines.[0].Contains "✓" @>
    test <@ not (lines.[0].Contains "⚠") @>
    test <@ lines.[0].Contains "6 passed, 0 failed in 6 projects" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: verbose renders a verified-nothing run as warn, never a check`` () =
    let lines =
        renderPlugin Verbose true now "test-prune" (verifiedNothingStatus ())
        |> stripMany

    test <@ lines.[0].Contains "⚠" @>
    test <@ not (lines.[0].Contains "✓") @>
    test <@ lines |> List.exists (fun l -> l.Contains "NOTHING VERIFIED") @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: verbose still renders a run that DID execute as a check`` () =
    // Positive control for the test above.
    let lines =
        renderPlugin Verbose true now "test-prune" (genuinePassStatus ()) |> stripMany

    test <@ lines.[0].Contains "✓" @>
    test <@ not (lines.[0].Contains "⚠") @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: agent tokens a verified-nothing run as warn, never ok`` () =
    let lines = agentLine "test-prune" (verifiedNothingStatus ())
    test <@ lines.Length = 1 @>
    test <@ lines.[0].StartsWith "test-prune: warn" @>
    test <@ lines.[0].Contains "NOTHING VERIFIED" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: agent still tokens a run that DID execute as ok`` () =
    // Positive control for the test above.
    let lines = agentLine "test-prune" (genuinePassStatus ())
    test <@ lines.Length = 1 @>
    test <@ lines.[0].StartsWith "test-prune: ok" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: a REPLAYED verified-nothing run is not a check either`` () =
    // The cache-replay path appends " (cached)" to the summary it replays. A replay of a
    // run that executed nothing verified exactly as much as the original did, so the
    // marker is matched on the PREFIX, not by equality.
    let replayed =
        okStatus (
            Some(
                RunSummary.nothingVerified "0 test project(s) ran, no test executed"
                + " (cached)"
            )
        )

    let lines = renderPlugin Compact true now "test-prune" replayed |> stripMany
    test <@ lines.[0].Contains "⚠" @>
    test <@ not (lines.[0].Contains "✓") @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: a verified-nothing run warns without reddening the verdict`` () =
    // `Warn`, not `Fail`: nothing BROKE. The scope layer already refuses this run its
    // exit 0 (`CheckOutcome.UnearnedScope NoTestsRun`, exit 3 — NO VERDICT); tokening it
    // `fail` here would convert that honest refusal into a claim that tests failed.
    let outcome =
        FsHotWatch.Cli.Verdict.pluginOutcomeOf true now (verifiedNothingStatus ())

    test <@ outcome = Some FsHotWatch.Cli.Verdict.PluginOutcome.Warn @>

    test <@ outcome |> Option.map FsHotWatch.Cli.Verdict.PluginOutcome.isFailing = Some false @>

    // Control: the executing run still tokens `Ok` on the same path.
    test
        <@
            FsHotWatch.Cli.Verdict.pluginOutcomeOf true now (genuinePassStatus ()) = Some
                FsHotWatch.Cli.Verdict.PluginOutcome.Ok
        @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-198: agent nextStep after a verified-nothing run points at status, never done`` () =
    // End of the same thread: an agent that reads `next: done` off a run that executed
    // no test has been told the check is finished and clean. It is neither.
    let lines = agentAll [ "test-prune", verifiedNothingStatus () ]
    let next = lines |> List.last
    test <@ next.Contains "status" @>
    test <@ not (next.Contains "done") @>

    // Control: the executing run still ends the loop.
    let done_ = agentAll [ "test-prune", genuinePassStatus () ] |> List.last
    test <@ done_.Contains "done" @>
