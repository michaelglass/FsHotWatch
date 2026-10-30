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

let private analyzeFixture (since: DateTimeOffset option) =
    withLog shadowLines (fun path -> analyze since { Records = fixture; Unreadable = 1 } (readShadowLogs [ path ]))

[<Fact>]
let ``a shadow line is read back by its fields, and anything else is not a line`` () =
    let parsed = parseShadowLine (shadowLine t0 "check" "stricter" "missing")

    test
        <@
            parsed = Some
                { At = t0
                  Command = "check"
                  Direction = "stricter"
                  Lookup = "missing" }
        @>

    test <@ parseShadowLine "{" = None @>
    test <@ parseShadowLine """{"schema":"fshw-shadow-v0","at":"2026-10-27T00:00:00+00:00"}""" = None @>
    test <@ parseShadowLine """{"schema":"fshw-shadow-v1"}""" = None @>

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
                               Directions = [ "agree", 2; "stricter", 1 ] }
                             { Day = "2026-10-28"
                               Lines = 2
                               Agree = 1
                               Directions = [ "agree", 1; "stricter", 1 ] } ]
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
        render 10 (analyze None { Records = []; Unreadable = 0 } (readShadowLogs []))
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
