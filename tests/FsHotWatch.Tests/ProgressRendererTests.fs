module FsHotWatch.Tests.ProgressRendererTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcOutput
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
          LastRun = Some(completedRun (TimeSpan.FromSeconds 3.2) (TimeSpan.FromSeconds 3.2) (Some "built 4 projects")) }

    let lines = renderPlugin Compact now "Build" parsed |> stripMany
    test <@ lines.Length = 1 @>
    let line = lines.[0]
    test <@ line.Contains "Build" @>
    test <@ line.Contains "\u2713" @> // ✓
    test <@ line.Contains "3.2s" @>
    test <@ line.Contains "built 4 projects" @>

[<Fact(Timeout = 5000)>]
let ``compact Running with subtasks lists them`` () =
    let parsed: ParsedPluginStatus =
        { Status = Running(now - TimeSpan.FromSeconds(72.0))
          Subtasks =
            [ makeSubtask "A" "running A" 10.0
              makeSubtask "B" "running B" 8.0
              makeSubtask "C" "running C" 2.0 ]
          ActivityTail = [ "queued 3" ]
          LastRun = None }

    let lines = renderPlugin Compact now "TestPrune" parsed |> stripMany
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
          LastRun = None }

    let lines = renderPlugin Compact now "Lint" parsed |> stripMany
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
          LastRun = Some(failedRun (TimeSpan.FromSeconds 6.4) (TimeSpan.FromSeconds 6.4) multiline) }

    let lines = renderPlugin Compact now "Lint" parsed |> stripMany
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
          LastRun = Some(failedRun (TimeSpan.FromSeconds 1.0) (TimeSpan.FromSeconds 1.0) longErr) }

    let linesLong = renderPlugin Compact now "Lint" parsedLong |> stripMany
    test <@ linesLong.Length = 1 @>
    // The rendered line length (after stripping colors) should be bounded.
    test <@ linesLong.[0].Length < 200 @>

[<Fact(Timeout = 5000)>]
let ``compact Idle with history shows last-run recap`` () =
    let parsed: ParsedPluginStatus =
        { Status = Idle
          Subtasks = []
          ActivityTail = []
          LastRun = Some(completedRun (TimeSpan.FromSeconds 30.0) (TimeSpan.FromSeconds 4.1) (Some "no issues")) }

    let lines = renderPlugin Compact now "Analyzers" parsed |> stripMany
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
          LastRun = None }

    let lines = renderPlugin Compact now "Coverage" parsed |> stripMany
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
          LastRun = None }

    let lines = renderPlugin Verbose now "TestPrune" parsed |> stripMany
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
                  ActivityTail = [ "loading rules"; "linting FileA.fs" ] } }

    let lines = renderPlugin Verbose now "Lint" parsed |> stripMany
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
                  ActivityTail = [ "dotnet build sln" ] } }

    let lines = renderPlugin Verbose now "Build" parsed |> stripMany
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
                  ActivityTail = [] } }

    let lines = renderPlugin Verbose now "Build" parsed |> stripMany
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
                LastRun = Some(completedRun (TimeSpan.FromSeconds 3.0) (TimeSpan.FromSeconds 3.0) (Some "ok")) }
              "Lint",
              { Status = Idle
                Subtasks = []
                ActivityTail = []
                LastRun = None } ]

    let lines = renderAll Compact now statuses |> stripMany
    // Compact is exactly one line per plugin.
    test <@ lines.Length = 2 @>
    let joined = String.concat "\n" lines
    test <@ joined.Contains "Build" @>
    test <@ joined.Contains "Lint" @>
