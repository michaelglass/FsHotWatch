/// AUTOMATION-1368: `fshw analytics` — flaky, slow and trust, as queries over the one
/// outcome store and the shadow ledger. Every test runs over a fixture store in a temp
/// directory and fixture shadow lines; no test reads the wall clock or the user's cache.
module FsHotWatch.Tests.OutcomeAnalyticsTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.OutcomeStore
open FsHotWatch.Cli
open FsHotWatch.Cli.OutcomeAnalytics
open FsHotWatch.Tests.TestHelpers

let private t0 = DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero)

let private key (project: string) (inputs: string) =
    ActionKey.create "test" [ "kp", inputs; "project", project; "run-scope", "full" ]

let private record
    (k: ActionKey.ActionKey)
    (subject: string)
    (outcome: Outcome)
    (at: DateTimeOffset)
    (duration: int64 option)
    (worktree: string)
    =
    { Key = k
      Subject = subject
      Outcome = outcome
      Counts = None
      DurationMs = duration
      Producer =
        { Binary = "fshw-1"
          Worktree = worktree
          RunId = Some "run" }
      RecordedAt = at }

let private kA = key "A" "a-inputs-1"
let private kA2 = key "A" "a-inputs-2"
let private kB = key "B" "b-inputs-1"

/// A.Tests flaked on one input set (pass, fail, pass); passed once on another. B.Tests
/// failed twice on one input set, a day apart, and was never green there.
let private fixture =
    [ record kA "A.Tests" Outcome.Pass t0 (Some 1000L) "/w1"
      record kA "A.Tests" Outcome.Fail (t0.AddHours 1.0) (Some 3000L) "/w2"
      record kA "A.Tests" Outcome.Pass (t0.AddHours 2.0) (Some 2000L) "/w1"
      record kA2 "A.Tests" Outcome.Pass t0 (Some 4000L) "/w1"
      record kB "B.Tests" Outcome.Fail (t0.AddMinutes 30.0) None "/w1"
      record kB "B.Tests" Outcome.Fail (t0.AddDays 1.0) None "/w1" ]

let private shadowLine (at: DateTimeOffset) (command: string) (direction: string) (lookup: string) =
    $"""{{"schema":"fshw-shadow-v1","at":"%s{at.ToString("o")}","command":"%s{command}","tree":"sha256:t","producer":"p","today":{{"outcome":"green","class":"green","exit":0}},"lookup":{{"outcome":"%s{lookup}","missing":[],"failing":[]}},"agree":%s{(if direction = "agree" then "true" else "false")},"direction":"%s{direction}","recorded":3}}"""

let private shadowLines =
    [ shadowLine t0 "confirm" "agree" "green"
      shadowLine (t0.AddHours 1.0) "check" "agree" "green"
      shadowLine (t0.AddHours 2.0) "check" "stricter" "missing"
      shadowLine (t0.AddDays 1.0) "verdict" "agree" "green"
      shadowLine (t0.AddDays(1.0).AddMinutes 5.0) "verdict" "stricter" "missing"
      "not json at all"
      """{"schema":"fshw-shadow-v0","at":"2026-10-27T00:00:00+00:00"}""" ]

let private withLog (lines: string list) (body: string -> 'a) =
    withTempDir "analytics" (fun dir ->
        let path = Path.Combine(dir, "lookup-verdict.jsonl")
        File.WriteAllLines(path, lines)
        body path)

let private noRows: TestRowsScan = { Indexes = []; Unreadable = 0 }

let private noLog: TestRowsLog.LogRead =
    { Runs = []
      UnreadableFiles = 0
      UnreadableLines = 0 }

let private analyzeFixture (since: DateTimeOffset option) =
    withLog shadowLines (fun path ->
        analyze since { Records = fixture; Unreadable = 1 } noRows noLog (readShadowLogs [ path ]))

[<Fact>]
let ``a shadow line is read back by its fields, and anything else is not a line`` () =
    let parsed = parseShadowLine (shadowLine t0 "check" "stricter" "missing")

    test
        <@
            parsed = Some
                { At = t0
                  Command = "check"
                  Direction = ShadowVerdict.Direction.Stricter
                  Lookup = ShadowVerdict.Class.Missing }
        @>

    test <@ parseShadowLine "{" = None @>
    test <@ parseShadowLine """{"schema":"fshw-shadow-v0","at":"2026-10-27T00:00:00+00:00"}""" = None @>
    test <@ parseShadowLine """{"schema":"fshw-shadow-v1"}""" = None @>
    test <@ parseShadowLine (shadowLine t0 "check" "sideways" "missing") = None @>
    test <@ parseShadowLine (shadowLine t0 "check" "agree" "amber") = None @>

[<Fact>]
let ``shadow logs are read line by line; a missing log is skipped and an unreadable line counted`` () =
    withLog shadowLines (fun path ->
        let read = readShadowLogs [ path; path + ".absent" ]
        test <@ read.Lines.Length = 5 @>
        test <@ read.UnreadableLines = 2 @>
        test <@ read.Logs = [ path ] @>)

[<Fact>]
let ``flaky: an input set recorded both green and red is named, with its counts and when it was last seen`` () =
    let r = analyzeFixture None

    test
        <@
            r.Flaky = [ { Kind = "test"
                          Subject = "A.Tests"
                          FlakyKeys = 1
                          Keys = 2
                          Passes = 2
                          Fails = 1
                          Worktrees = 2
                          LastSeen = t0.AddHours 2.0 } ]
        @>

[<Fact>]
let ``slow: percentiles of the recorded durations per subject, and how many records carry one`` () =
    let r = analyzeFixture None

    test
        <@
            r.Slow = [ { Kind = "test"
                         Subject = "A.Tests"
                         Samples = 4
                         P50Ms = 2000L
                         P90Ms = 4000L
                         MaxMs = 4000L } ]
        @>

    test <@ r.DurationsRecorded = 4 && r.RecordsRead = 6 && r.UnreadableRecords = 1 @>

[<Fact>]
let ``trust: reds later green on identical inputs, and runs a lookup would have answered`` () =
    let r = analyzeFixture None
    test <@ r.Reds = { Fails = 3; LaterGreen = 1 } @>

    test
        <@
            r.Reuse = [ { Kind = "test"
                          Keys = 3
                          Records = 6
                          RedundantRuns = 2 } ]
        @>

[<Fact>]
let ``trust over time: the store and the shadow ledger by UTC day`` () =
    let r = analyzeFixture None

    test
        <@
            r.StoreDays = [ { Day = "2026-10-27"
                              Records = 5
                              Fails = 2
                              LaterGreen = 1 }
                            { Day = "2026-10-28"
                              Records = 1
                              Fails = 1
                              LaterGreen = 0 } ]
        @>

    test
        <@
            r.ShadowDays = [ { Day = "2026-10-27"
                               Lines = 3
                               Agree = 2
                               Directions = [ ShadowVerdict.Direction.Agree, 2; ShadowVerdict.Direction.Stricter, 1 ] }
                             { Day = "2026-10-28"
                               Lines = 2
                               Agree = 1
                               Directions = [ ShadowVerdict.Direction.Agree, 1; ShadowVerdict.Direction.Stricter, 1 ] } ]
        @>

    test <@ r.ShadowUnreadable = 2 @>

[<Fact>]
let ``cache hits: a shadowed verdict read is satisfied by the store or missing`` () =
    let r = analyzeFixture None

    test
        <@
            r.LookupReads = { Reads = 2
                              Satisfied = 1
                              Missing = 1 }
        @>

[<Fact>]
let ``--since leaves out every record and shadow line older than it`` () =
    let r = analyzeFixture (Some(t0.AddMinutes 90.0))
    test <@ r.RecordsRead = 2 @>
    test <@ List.isEmpty r.Flaky @>
    test <@ r.Reds = { Fails = 1; LaterGreen = 0 } @>
    test <@ r.ShadowDays |> List.sumBy _.Lines = 3 @>

[<Fact>]
let ``what the store does not record is reported as a gap, never as a zero`` () =
    let noDurations = fixture |> List.map (fun r -> { r with DurationMs = None })

    let r =
        analyze
            None
            { Records = noDurations
              Unreadable = 0 }
            noRows
            noLog
            (readShadowLogs [])

    test <@ List.isEmpty r.Slow @>
    test <@ r.Gaps |> List.exists (fun g -> g.Contains "durationMs") @>
    test <@ r.Gaps |> List.exists (fun g -> g.Contains "per test") @>
    test <@ r.Gaps |> List.exists (fun g -> g.Contains "shadow") @>

    let withData = analyzeFixture None
    test <@ not (withData.Gaps |> List.exists (fun g -> g.Contains "durationMs")) @>
    test <@ withData.Gaps |> List.exists (fun g -> g.Contains "per test") @>

[<Fact>]
let ``the human report names each section and its numbers`` () =
    let text = render 10 (analyzeFixture None) |> String.concat "\n"
    test <@ text.Contains "Flaky" && text.Contains "A.Tests" @>
    test <@ text.Contains "Slow" && text.Contains "p50 2.0s" && text.Contains "p90 4.0s" @>
    test <@ text.Contains "reds later green on identical inputs: 1 of 3" @>
    test <@ text.Contains "runs a lookup would have answered: 2 of 6" @>
    test <@ text.Contains "2026-10-27" && text.Contains "agree 2/3" @>
    test <@ text.Contains "verdict reads satisfied by the store: 1 of 2" @>

[<Fact>]
let ``an empty store renders as empty, not as a failure`` () =
    let text =
        render 10 (analyze None { Records = []; Unreadable = 0 } noRows noLog (readShadowLogs []))
        |> String.concat "\n"

    test <@ text.Contains "0 records" @>
    test <@ text.Contains "no input set recorded both outcomes" @>

[<Fact>]
let ``the JSON report carries every section under a versioned schema`` () =
    use doc = JsonDocument.Parse(toJson (analyzeFixture None))
    let root = doc.RootElement
    let flaky = root.GetProperty("flaky")[0]
    let trust = root.GetProperty("trust")
    // JsonElement is a struct, which a quotation cannot call into: read the values first.
    let schema = root.GetProperty("schema").GetString()
    let records = root.GetProperty("records").GetInt32()
    let subject = flaky.GetProperty("subject").GetString()
    let lastSeen = flaky.GetProperty("lastSeen").GetString()
    let expectedLastSeen = (t0.AddHours 2.0).ToString("o")
    let p90 = (root.GetProperty("slow")[0]).GetProperty("p90Ms").GetInt64()
    let laterGreen = trust.GetProperty("reds").GetProperty("laterGreen").GetInt32()

    let redundant =
        (trust.GetProperty("reuse")[0]).GetProperty("redundantRuns").GetInt32()

    let satisfied = trust.GetProperty("lookupReads").GetProperty("satisfied").GetInt32()

    let stricter =
        (trust.GetProperty("shadowDays")[0]).GetProperty("directions").GetProperty("stricter").GetInt32()

    let storeDays = trust.GetProperty("storeDays").GetArrayLength()
    let gaps = root.GetProperty("gaps").GetArrayLength()
    test <@ schema = "fshw-analytics-v1" && records = 6 @>
    test <@ subject = "A.Tests" && lastSeen = expectedLastSeen @>
    test <@ p90 = 4000L && laterGreen = 1 && redundant = 2 && satisfied = 1 @>
    test <@ stricter = 1 && storeDays = 2 && gaps >= 1 @>

[<Fact>]
let ``the shadow logs read by default are this checkout's and those of every worktree the store names`` () =
    withTempDir "analytics-logs" (fun dir ->
        let here = Path.Combine(dir, "here")
        let other = Path.Combine(dir, "other")
        let silent = Path.Combine(dir, "silent")

        for root in [ here; other ] do
            Directory.CreateDirectory(Path.Combine(root, ".fshw", "shadow")) |> ignore
            File.WriteAllText(Path.Combine(root, ".fshw", "shadow", "lookup-verdict.jsonl"), "")

        let records =
            [ record kA "A.Tests" Outcome.Pass t0 None other
              record kB "B.Tests" Outcome.Pass t0 None silent
              record kA2 "A.Tests" Outcome.Pass t0 None here ]

        test
            <@
                shadowLogsFor here records = [ Path.Combine(here, ".fshw", "shadow", "lookup-verdict.jsonl")
                                               Path.Combine(other, ".fshw", "shadow", "lookup-verdict.jsonl") ]
            @>)

// --- AUTOMATION-1744: flaky TESTS, from the per-test rows filed beside each test record ---

let private runOf (project: string) (runId: string) (tree: string) (tool: string) (at: DateTimeOffset) rows =
    ({ Run =
        { Key = (key project tree).Value
          Project = project
          RunId = runId
          Tree = tree
          Tool = tool
          Machine = "box"
          RecordedAt = at }
       Rows =
         (rows
          |> List.map (fun (name, outcome) ->
              ({ Name = name
                 Outcome = outcome
                 DurationMs = 1L }
              : TestRows.Row))
          |> FsHotWatch.Core.NonEmpty.ofList)
             .Value }
    : TestRows.Index)

let private indexOnly =
    FsHotWatch.Core.NonEmpty.ofList [ RowSource.Index ] |> Option.get

/// A.flaky failed, passed and timed out on one tree and tool; A.skippy was skipped once and
/// passed once; A.split failed on one tree and passed on another, and passed under another
/// tool; B.c was cancelled once and passed once.
let private rowRuns =
    [ runOf "A.Tests" "r1" "t1" "f1" t0 [ "A.flaky", TestRows.Outcome.Failed; "A.skippy", TestRows.Outcome.Skipped ]
      runOf
          "A.Tests"
          "r2"
          "t1"
          "f1"
          (t0.AddHours 1.0)
          [ "A.flaky", TestRows.Outcome.Passed; "A.skippy", TestRows.Outcome.Passed ]
      runOf "A.Tests" "r3" "t1" "f1" (t0.AddHours 2.0) [ "A.flaky", TestRows.Outcome.TimedOut ]
      runOf "A.Tests" "r4" "t2" "f1" (t0.AddHours 3.0) [ "A.split", TestRows.Outcome.Failed ]
      runOf "A.Tests" "r5" "t1" "f2" (t0.AddHours 3.0) [ "A.split", TestRows.Outcome.Passed ]
      runOf "A.Tests" "r6" "t1" "f1" (t0.AddHours 3.0) [ "A.split", TestRows.Outcome.Passed ]
      runOf "B.Tests" "r7" "t2" "f1" (t0.AddMinutes 30.0) [ "B.c", TestRows.Outcome.Cancelled ]
      runOf "B.Tests" "r8" "t2" "f1" (t0.AddHours 4.0) [ "B.c", TestRows.Outcome.Passed ] ]

let private analyzeRows (since: DateTimeOffset option) =
    analyze since { Records = fixture; Unreadable = 0 } { Indexes = rowRuns; Unreadable = 2 } noLog (readShadowLogs [])

[<Fact>]
let ``flaky test: one test on one tree and tool recorded passed and red, with each outcome counted and both runs`` () =
    let r = analyzeRows None

    test
        <@
            r.FlakyTests = [ { Project = "B.Tests"
                               Test = "B.c"
                               Tree = "t2"
                               Tool = "f1"
                               Outcomes = [ TestRows.Outcome.Passed, 1; TestRows.Outcome.Cancelled, 1 ]
                               RedRun = "r7"
                               GreenRun = "r8"
                               LastSeen = t0.AddHours 4.0
                               Sources = indexOnly }
                             { Project = "A.Tests"
                               Test = "A.flaky"
                               Tree = "t1"
                               Tool = "f1"
                               Outcomes =
                                 [ TestRows.Outcome.Passed, 1
                                   TestRows.Outcome.Failed, 1
                                   TestRows.Outcome.TimedOut, 1 ]
                               RedRun = "r3"
                               GreenRun = "r2"
                               LastSeen = t0.AddHours 2.0
                               Sources = indexOnly } ]
        @>

    test <@ r.TestRowRuns = 8 && r.UnreadableTestRows = 2 @>

[<Fact>]
let ``flaky test: --since leaves out the rows of runs older than it`` () =
    let r = analyzeRows (Some(t0.AddHours 1.0))
    test <@ r.FlakyTests |> List.map _.Test = [ "A.flaky" ] @>
    test <@ (List.exactlyOne r.FlakyTests).Outcomes = [ TestRows.Outcome.Passed, 1; TestRows.Outcome.TimedOut, 1 ] @>
    test <@ r.TestRowRuns = 6 @>

[<Fact>]
let ``flaky test: the human and JSON reports name each flaky test, its counts and both runs`` () =
    let r = analyzeRows None
    let text = render 10 r |> String.concat "\n"

    test
        <@
            text.Contains "Flaky tests"
            && text.Contains "8 run(s) of per test rows, 2 unreadable"
        @>

    test
        <@
            text.Contains
                "A.flaky [A.Tests]: 1 passed, 1 failed, 1 timedOut; red in run r3, passed in run r2; tree t1, tool f1"
        @>

    test <@ not (r.Gaps |> List.exists (fun g -> g.StartsWith "flaky tests:")) @>

    use doc = JsonDocument.Parse(toJson r)
    let first = doc.RootElement.GetProperty("flakyTests")[1]
    let testName = first.GetProperty("test").GetString()
    let timedOut = first.GetProperty("outcomes").GetProperty("timedOut").GetInt32()
    let red = first.GetProperty("redRun").GetString()
    let green = first.GetProperty("greenRun").GetString()
    let runs = doc.RootElement.GetProperty("testRowRuns").GetInt32()
    test <@ testName = "A.flaky" && timedOut = 1 && red = "r3" && green = "r2" && runs = 8 @>

[<Fact>]
let ``flaky test: no rows is a gap and an empty section, never a zero rate`` () =
    let r =
        analyze None { Records = []; Unreadable = 0 } noRows noLog (readShadowLogs [])

    test <@ List.isEmpty r.FlakyTests @>
    test <@ r.Gaps |> List.exists (fun g -> g.StartsWith "flaky tests:") @>
    test <@ (render 10 r |> String.concat "\n").Contains "no test recorded both outcomes" @>

// --- AUTOMATION-1744: the daily test-row log, a second source of rows that outlives its records ---

let private sourcesOf (f: FlakyTest) =
    FsHotWatch.Core.NonEmpty.toList f.Sources

let private analyzeSources (indexed: TestRows.Index list) (logged: TestRows.Index list) =
    analyze
        None
        { Records = []; Unreadable = 0 }
        { Indexes = indexed; Unreadable = 0 }
        { Runs = logged
          UnreadableFiles = 1
          UnreadableLines = 2 }
        (readShadowLogs [])

let private red =
    runOf "A.Tests" "r1" "t1" "f1" t0 [ "A.flaky", TestRows.Outcome.Failed ]

let private green =
    runOf "A.Tests" "r2" "t1" "f1" (t0.AddHours 1.0) [ "A.flaky", TestRows.Outcome.Passed ]

[<Fact>]
let ``flaky test: a run in both the index and the log counts once, and the finding names both sources`` () =
    let r = analyzeSources [ red; green ] [ red; green ]
    let flake = List.exactlyOne r.FlakyTests
    test <@ flake.Outcomes = [ TestRows.Outcome.Passed, 1; TestRows.Outcome.Failed, 1 ] @>
    test <@ sourcesOf flake = [ RowSource.Index; RowSource.DailyLog ] @>

    test
        <@
            r.TestRowRuns = 2
            && r.LogRuns = 2
            && r.UnreadableLogFiles = 1
            && r.UnreadableLogLines = 2
        @>

[<Fact>]
let ``flaky test: a run of the same id in another project is another run`` () =
    let other = runOf "B.Tests" "r1" "t1" "f1" t0 [ "A.flaky", TestRows.Outcome.Failed ]

    let r = analyzeSources [ red ] [ other ]
    test <@ r.TestRowRuns = 2 @>

[<Fact>]
let ``flaky test: a finding from the log alone names the log, and one from the index alone the index`` () =
    test <@ (analyzeSources [] [ red; green ]).FlakyTests |> List.map sourcesOf = [ [ RowSource.DailyLog ] ] @>
    test <@ (analyzeSources [ red; green ] []).FlakyTests |> List.map sourcesOf = [ [ RowSource.Index ] ] @>

[<Fact>]
let ``flaky test: --since leaves out logged runs older than it`` () =
    let r =
        analyze
            (Some(t0.AddMinutes 30.0))
            { Records = []; Unreadable = 0 }
            noRows
            { Runs = [ red; green ]
              UnreadableFiles = 0
              UnreadableLines = 0 }
            (readShadowLogs [])

    test <@ List.isEmpty r.FlakyTests && r.LogRuns = 1 && r.TestRowRuns = 1 @>

[<Fact>]
let ``flaky test: the human and JSON reports name each finding's sources and the log's counts`` () =
    let r = analyzeSources [ green ] [ red; green ]
    let text = render 10 r |> String.concat "\n"

    test
        <@
            text.Contains
                "2 run(s) of per test rows, 0 unreadable; 1 beside records, 2 in the daily log, 1 unreadable log file(s), 2 unreadable log line(s)"
        @>

    test <@ text.Contains "tree t1, tool f1; from index and daily log; last" @>

    let logOnly = render 10 (analyzeSources [] [ red; green ]) |> String.concat "\n"
    test <@ logOnly.Contains "; from daily log; last" @>

    use doc = JsonDocument.Parse(toJson r)
    let flake = doc.RootElement.GetProperty("flakyTests")[0]

    let sources =
        flake.GetProperty("sources").EnumerateArray()
        |> Seq.map (fun s -> s.GetString())
        |> List.ofSeq

    test <@ sources = [ "index"; "log" ] @>
    test <@ doc.RootElement.GetProperty("testRowLogRuns").GetInt32() = 2 @>
    test <@ doc.RootElement.GetProperty("unreadableTestRowLogLines").GetInt32() = 2 @>
    test <@ doc.RootElement.GetProperty("unreadableTestRowLogFiles").GetInt32() = 1 @>
