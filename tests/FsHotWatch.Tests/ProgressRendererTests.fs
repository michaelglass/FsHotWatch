module FsHotWatch.Tests.ProgressRendererTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.ProgressRenderer

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

// ---------------- Compact mode ----------------

[<Fact(Timeout = 5000)>]
let ``compact Completed shows check glyph elapsed and summary`` () =
    let parsed: ParsedPluginStatus =
        { Status = Completed(now - TimeSpan.FromSeconds(3.2))
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

[<Fact(Timeout = 5000)>]
let ``compact Completed with ledger errors shows warn glyph and count`` () =
    let parsed: ParsedPluginStatus =
        { Status = Completed(now - TimeSpan.FromSeconds(3.2))
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

[<Fact(Timeout = 5000)>]
let ``compact Completed with only warnings respects warningsAreFailures flag`` () =
    let parsed: ParsedPluginStatus =
        { Status = Completed(now - TimeSpan.FromSeconds(1.0))
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) None)
          Diagnostics = { Errors = 0; Warnings = 3 } }

    // warningsAreFailures = true -> ⚠
    let strict = renderPlugin Compact true now "Lint" parsed |> stripMany
    test <@ strict.[0].Contains "⚠" @>

    // warningsAreFailures = false -> ✓ (warnings don't count)
    let lax = renderPlugin Compact false now "Lint" parsed |> stripMany
    test <@ lax.[0].Contains "✓" @>
    test <@ not (lax.[0].Contains "⚠") @>

[<Fact(Timeout = 5000)>]
let ``compact Running with subtasks lists them`` () =
    let parsed: ParsedPluginStatus =
        { Status = Running(now - TimeSpan.FromSeconds(72.0))
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

[<Fact(Timeout = 5000)>]
let ``compact Running with no subtasks shows last activity line`` () =
    let parsed: ParsedPluginStatus =
        { Status = Running(now - TimeSpan.FromSeconds(5.0))
          Subtasks = []
          ActivityTail = [ "loading rules"; "linting FileA.fs" ]
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "Lint" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "linting FileA.fs" @>

[<Fact(Timeout = 5000)>]
let ``compact Failed shows truncated error first line`` () =
    let longErr = String.replicate 120 "x"
    let multiline = "first line of error\nsecond line\nthird line"

    let parsed: ParsedPluginStatus =
        { Status = Failed(multiline, now - TimeSpan.FromSeconds 6.4)
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
        { Status = Failed(longErr, now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(failedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) longErr)
          Diagnostics = DiagnosticCounts.empty }

    let linesLong = renderPlugin Compact true now "Lint" parsedLong |> stripMany
    test <@ linesLong.Length = 1 @>
    // The rendered line length (after stripping colors) should be bounded.
    test <@ linesLong.[0].Length < 200 @>

[<Fact(Timeout = 5000)>]
let ``compact Idle with history shows last-run recap`` () =
    let parsed: ParsedPluginStatus =
        { Status = Idle
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

[<Fact(Timeout = 5000)>]
let ``compact Idle with no history is single line name`` () =
    let parsed: ParsedPluginStatus =
        { Status = Idle
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "Coverage" parsed |> stripMany
    test <@ lines.Length = 1 @>
    test <@ lines.[0].Contains "Coverage" @>

// ---------------- Verbose mode ----------------

[<Fact(Timeout = 5000)>]
let ``verbose Running emits header plus subtask tree plus recent`` () =
    let parsed: ParsedPluginStatus =
        { Status = Running(now - TimeSpan.FromSeconds(72.0))
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

[<Fact(Timeout = 5000)>]
let ``verbose Failed shows started, error detail, and recent`` () =
    let startedAt = now - TimeSpan.FromSeconds 6.4
    let err = "FileA.fs(12,4): FS0020: ...\nFileA.fs(33,1): FS0025: ..."

    let parsed: ParsedPluginStatus =
        { Status = Failed(err, now)
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

[<Fact(Timeout = 5000)>]
let ``verbose Completed shows header started elapsed summary`` () =
    let startedAt = now - TimeSpan.FromSeconds 3.2

    let parsed: ParsedPluginStatus =
        { Status = Completed now
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

[<Fact(Timeout = 5000)>]
let ``verbose Completed with empty activity tail hides recent section`` () =
    let startedAt = now - TimeSpan.FromSeconds 1.0

    let parsed: ParsedPluginStatus =
        { Status = Completed now
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

[<Fact(Timeout = 5000)>]
let ``renderAll concatenates per-plugin blocks`` () =
    let statuses =
        Map.ofList
            [ "Build",
              { Status = Completed now
                Subtasks = []
                ActivityTail = []
                LastRun = Some(completedRun (TimeSpan.FromSeconds 3.0) (TimeSpan.FromSeconds 3.0) (Some "ok"))
                Diagnostics = DiagnosticCounts.empty }
              "Lint",
              { Status = Idle
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
        { Status = Completed(now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) summary)
          Diagnostics = DiagnosticCounts.empty }

    let failStatus (err: string) : ParsedPluginStatus =
        { Status = Failed(err, now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(failedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) err)
          Diagnostics = DiagnosticCounts.empty }

    let runningStatus () : ParsedPluginStatus =
        { Status = Running(now - TimeSpan.FromSeconds 2.0)
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let warnStatus () : ParsedPluginStatus =
        { Status = Completed(now - TimeSpan.FromSeconds 1.0)
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) None)
          Diagnostics = { Errors = 0; Warnings = 3 } }

    let idleNoHistory () : ParsedPluginStatus =
        { Status = Idle
          Subtasks = []
          ActivityTail = []
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let idleCompleted (summary: string option) : ParsedPluginStatus =
        { Status = Idle
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 10.0) (TimeSpan.FromSeconds 2.0) summary)
          Diagnostics = DiagnosticCounts.empty }

    let idleFailed (err: string) : ParsedPluginStatus =
        { Status = Idle
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

open AgentFixtures

[<Fact(Timeout = 5000)>]
let ``agent renderAll emits banner as first line`` () =
    let lines = agentAll [ "build", okStatus None ]
    test <@ lines.Length >= 1 @>
    test <@ lines.[0].StartsWith "# fs-hot-watch agent mode" @>

[<Fact(Timeout = 5000)>]
let ``agent banner lists expected commands`` () =
    let lines = agentAll [ "build", okStatus None ]
    let banner = lines.[0]

    [ "check"
      "build"
      "test"
      "lint"
      "analyze"
      "format"
      "format-check"
      "errors"
      "status" ]
    |> List.iter (fun cmd -> test <@ banner.Contains cmd @>)

[<Fact(Timeout = 5000)>]
let ``agent renderAll omits Idle plugins with no LastRun`` () =
    let lines = agentAll [ "build", okStatus None; "coverage", idleNoHistory () ]

    let joined = String.concat "\n" lines
    test <@ joined.Contains "build:" @>
    test <@ not (joined.Contains "coverage") @>

[<Fact(Timeout = 5000)>]
let ``agent renderPlugin for Idle with no LastRun returns empty list`` () =
    test <@ List.isEmpty (agentLine "coverage" (idleNoHistory ())) @>

[<Fact(Timeout = 5000)>]
let ``agent Idle-with-completed-LastRun renders as ok`` () =
    let lines = agentLine "analyze" (idleCompleted (Some "clean"))
    test <@ lines = [ "analyze: ok" ] @>

[<Fact(Timeout = 5000)>]
let ``agent Idle-with-failed-LastRun renders as fail with summary`` () =
    let lines = agentLine "test" (idleFailed "2 failed in FsHotWatch.Tests")
    test <@ lines.Length = 1 @>
    test <@ lines.[0] = "test: fail summary=\"2 failed in FsHotWatch.Tests\"" @>

[<Fact(Timeout = 5000)>]
let ``agent ok line is plain "<name>: ok" with no summary`` () =
    let lines = agentLine "build" (okStatus (Some "built 4 projects"))
    test <@ lines = [ "build: ok" ] @>

[<Fact(Timeout = 5000)>]
let ``agent fail line includes summary`` () =
    let lines = agentLine "test" (failStatus "2 failed in FsHotWatch.Tests")
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.StartsWith "test: fail summary=\"" @>
    test <@ line.Contains "2 failed in FsHotWatch.Tests" @>
    test <@ line.EndsWith "\"" @>

[<Fact(Timeout = 5000)>]
let ``agent warn state fires when warnings present and warningsAreFailures=true`` () =
    let lines = agentLine "lint" (warnStatus ())
    test <@ lines.Length = 1 @>
    test <@ lines.[0].StartsWith "lint: warn" @>

[<Fact(Timeout = 5000)>]
let ``agent warn demotes to ok when warningsAreFailures=false`` () =
    let lines = renderPlugin Agent false now "lint" (warnStatus ())
    test <@ lines = [ "lint: ok" ] @>

[<Fact(Timeout = 5000)>]
let ``agent Running line has no summary`` () =
    let lines = agentLine "build" (runningStatus ())
    test <@ lines = [ "build: running" ] @>

[<Fact(Timeout = 5000)>]
let ``agent fail summary uses first non-empty line`` () =
    let err = "first line of error\nsecond line\nthird line"
    let lines = agentLine "lint" (failStatus err)
    test <@ lines.[0].Contains "first line of error" @>
    // Newlines collapsed to spaces — "second line" may still appear but
    // as part of a single-quoted summary. Ensure the line is not multi-line.
    test <@ not (lines.[0].Contains "\n") @>

[<Fact(Timeout = 5000)>]
let ``agent fail summary truncated to roughly 80 chars`` () =
    let long = String.replicate 200 "x"
    let lines = agentLine "lint" (failStatus long)
    // Extract the summary between the quotes.
    let line = lines.[0]
    let m = System.Text.RegularExpressions.Regex.Match(line, "summary=\"([^\"]*)\"")
    test <@ m.Success @>
    let summary = m.Groups.[1].Value
    test <@ summary.Length <= 80 @>
    test <@ summary.EndsWith "..." @>

[<Fact(Timeout = 5000)>]
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

[<Fact(Timeout = 5000)>]
let ``agent emits no ANSI escapes`` () =
    let statuses =
        [ "build", okStatus (Some "ok")
          "test", failStatus "boom"
          "lint", warnStatus ()
          "analyze", runningStatus () ]

    let lines = agentAll statuses
    let joined = String.concat "\n" lines
    // No ESC char anywhere.
    test <@ not (joined.Contains "\x1b") @>
    test <@ stripAnsi joined = joined @>

// ----- next-step rules -----

[<Fact(Timeout = 5000)>]
let ``agent next is errors --wait when any plugin is running`` () =
    let statuses = [ "build", failStatus "compile error"; "test", runningStatus () ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fs-hot-watch --agent errors --wait" @>

[<Fact(Timeout = 5000)>]
let ``agent next is build when build failed even if others also failed`` () =
    let statuses =
        [ "build", failStatus "compile error"
          "test", failStatus "2 failed"
          "lint", failStatus "warnings" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fs-hot-watch --agent build" @>

[<Fact(Timeout = 5000)>]
let ``agent next is test when build ok but test failed`` () =
    let statuses =
        [ "build", okStatus None
          "test", failStatus "boom"
          "lint", failStatus "warnings" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fs-hot-watch --agent test" @>

[<Fact(Timeout = 5000)>]
let ``agent next picks lint before analyze when both fail`` () =
    let statuses =
        [ "analyze", failStatus "bad"
          "lint", failStatus "warn"
          "coverage", failStatus "low" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fs-hot-watch --agent lint" @>

[<Fact(Timeout = 5000)>]
let ``agent next picks analyze before format-check and coverage`` () =
    let statuses =
        [ "coverage", failStatus "low"
          "format-check", failStatus "unformatted"
          "analyze", failStatus "bad" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fs-hot-watch --agent analyze" @>

[<Fact(Timeout = 5000)>]
let ``agent next picks format-check before coverage`` () =
    let statuses =
        [ "coverage", failStatus "low"; "format-check", failStatus "unformatted" ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fs-hot-watch --agent format-check" @>

[<Fact(Timeout = 5000)>]
let ``agent next is errors when only warnings and warningsAreFailures=true`` () =
    let statuses = [ "build", okStatus None; "lint", warnStatus () ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: fs-hot-watch --agent errors" @>

[<Fact(Timeout = 5000)>]
let ``agent next is done when warnings present but warningsAreFailures=false`` () =
    let statuses = [ "build", okStatus None; "lint", warnStatus () ]

    let lines = agentAllLax statuses
    test <@ List.last lines = "next: done" @>

[<Fact(Timeout = 5000)>]
let ``agent next is done when all clean`` () =
    let statuses =
        [ "build", okStatus None
          "test", okStatus None
          "lint", okStatus None
          "analyze", okStatus None ]

    let lines = agentAll statuses
    test <@ List.last lines = "next: done" @>

// ----- primary subtask rendering -----

[<Fact(Timeout = 5000)>]
let ``compact Running prefers primary subtask label over activity tail`` () =
    let parsed: ParsedPluginStatus =
        { Status = Running(now - TimeSpan.FromSeconds(1.5))
          Subtasks = [ makeSubtask "primary" "running 3 selected tests" 1.5 ]
          ActivityTail = [ "processing bar.fs" ]
          LastRun = None
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "test-prune" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "running 3 selected tests" @>
    // The primary label should win — activity-tail fallback is suppressed.
    test <@ not (line.Contains "processing bar.fs") @>

[<Fact(Timeout = 5000)>]
let ``compact Idle shows explicit summary not last log line`` () =
    let parsed: ParsedPluginStatus =
        { Status = Completed(now - TimeSpan.FromSeconds(2.0))
          Subtasks = []
          ActivityTail = [ "processing foo.fs"; "processing bar.fs" ]
          LastRun = Some(completedRun (TimeSpan.FromSeconds 2.0) (TimeSpan.FromSeconds 2.0) (Some "5 passed, 0 failed"))
          Diagnostics = DiagnosticCounts.empty }

    let lines = renderPlugin Compact true now "test-prune" parsed |> stripMany
    let line = lines.[0]
    test <@ line.Contains "5 passed, 0 failed" @>
    test <@ not (line.Contains "processing bar.fs") @>

// ----- regex roundtrip -----

[<Fact(Timeout = 5000)>]
let ``agent output lines match the parseable grammar`` () =
    let statuses =
        [ "build", okStatus (Some "built 4 projects")
          "test", failStatus "he said \"boom\"\nthen exited"
          "lint", warnStatus ()
          "analyze", runningStatus ()
          "coverage", idleCompleted (Some "covered") ]

    let lines = agentAll statuses

    let pattern =
        "^(?:# .*|[a-z-]+: (ok|fail|warn|running)(?: summary=\"(?:[^\"\\\\]|\\\\.)*\")?|next: .+)$"

    let rx = System.Text.RegularExpressions.Regex(pattern)

    for line in lines do
        test <@ rx.IsMatch line @>
