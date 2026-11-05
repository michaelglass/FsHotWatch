/// `shadow-report`: the phase-2 promotion bar (D5) evaluated over a project's shadow rows,
/// misses and mutation samples. Every bar is a threshold, and one failing bar fails the report.
module TestPrune.Trace.Tests.ShadowReportTests

open System
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.TraceStore

let private t0 = DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)

let private baseRow: ShadowRow =
    { RunId = "r"
      TestProject = "P"
      EnvFingerprint = "E"
      RecordedAt = t0
      Universe = 100
      CompleteShare = 1.0
      StaticTests = 50
      TraceTests = 5
      StaticClasses = 5
      TraceClasses = 1
      RuleCounts = Map.empty
      StaticOnly = []
      TraceOnly = []
      SelectMs = 100L
      Mode = ImpactRun
      RecordPolicy = "every-run"
      Eligible = Some true
      ActualFingerprint = Some "E"
      FingerprintOutcome = PredictionHit
      TraceRecall = "n/a: impact run"
      StaticClassTests = 50
      TraceClassTests = 10
      ChildUntracedTests = 0 }

/// 15 days, 120 impact rows, 12 full rows with measured recall 2/2, all green.
let greenRows =
    [ for i in 0..119 ->
          { baseRow with
              RunId = $"i%d{i}"
              RecordedAt = t0.AddHours(float i * 3.0) } ]
    @ [ for i in 0..11 ->
            { baseRow with
                RunId = $"f%d{i}"
                Mode = FullSuiteRun
                RecordedAt = t0.AddDays(float i + 0.5)
                TraceRecall = "2/2" } ]

let greenSamples =
    [ for i in 1..55 ->
          { TestProject = "P"
            Kind = (if i > 50 then UnionTypeMutant else SymbolMutant)
            Symbol = $"S%d{i}"
            TreeHash = "t"
            RecordedAt = t0
            Outcome = Measured
            Failing = 3
            Selected = 9
            Missed = [] } ]

let private bar name (results: ShadowReport.BarResult list) =
    results |> List.find (fun r -> r.Name = name)

let private evaluate rows misses samples =
    ShadowReport.evaluate ShadowReport.defaults rows misses samples

[<Fact>]
let ``a green window passes every bar`` () =
    let results = evaluate greenRows [] greenSamples
    test <@ ShadowReport.passes results @>

    test
        <@
            results |> List.map _.Name = [ "window"
                                           "verified-failure misses"
                                           "trace recall on full runs"
                                           "mutation"
                                           "eligibility"
                                           "fingerprint prediction"
                                           "latency"
                                           "payoff" ]
        @>

[<Fact>]
let ``no rows at all fails, it never passes vacuously`` () =
    let results = evaluate [] [] []
    test <@ not (ShadowReport.passes results) @>
    test <@ not (bar "window" results).Pass && not (bar "payoff" results).Pass @>
    test <@ not (bar "latency" results).Pass && not (bar "eligibility" results).Pass @>

[<Fact>]
let ``a verified-failure miss on an eligible row fails; one on an ineligible row is listed and does not`` () =
    let failing =
        bar "verified-failure misses" (evaluate greenRows [ "i3", "P|K|a" ] greenSamples)

    test <@ not failing.Pass && failing.Measured.Contains "P|K|a" @>

    let rows =
        greenRows
        |> List.map (fun r ->
            if r.RunId = "i3" then
                { r with Eligible = Some false }
            else
                r)

    let listed =
        bar "verified-failure misses" (evaluate rows [ "i3", "P|K|a" ] greenSamples)

    test <@ listed.Pass && listed.Measured.Contains "P|K|a" @>

[<Fact>]
let ``a miss of a run outside the window is not counted`` () =
    test <@ (bar "verified-failure misses" (evaluate greenRows [ "elsewhere", "P|K|a" ] greenSamples)).Pass @>

[<Fact>]
let ``a full run whose trace recall is below 1 fails; one that is not measurable is counted, not failed`` () =
    let below =
        greenRows
        |> List.map (fun r -> if r.RunId = "f3" then { r with TraceRecall = "1/2" } else r)

    test <@ not (bar "trace recall on full runs" (evaluate below [] greenSamples)).Pass @>

    let unmeasurable =
        greenRows
        |> List.map (fun r ->
            if r.RunId = "f3" then
                { r with
                    TraceRecall = "n/a: no complete report" }
            else
                r)

    let result = bar "trace recall on full runs" (evaluate unmeasurable [] greenSamples)
    test <@ result.Pass && result.Measured.Contains "1 not measurable" @>

[<Fact>]
let ``too few mutation samples, or any missed test, fails`` () =
    test <@ not (bar "mutation" (evaluate greenRows [] (List.truncate 49 greenSamples))).Pass @>

    let missed =
        { greenSamples.Head with
            Missed = [ "P|K|a" ] }
        :: greenSamples.Tail

    test <@ not (bar "mutation" (evaluate greenRows [] missed)).Pass @>
    // A sample that broke no test measures nothing and does not count.
    let quiet =
        greenSamples
        |> List.mapi (fun i s -> if i < 6 then { s with Outcome = NoFailure } else s)

    test <@ not (bar "mutation" (evaluate greenRows [] quiet)).Pass @>

[<Fact>]
let ``too few union-type samples fails, unless the project has no more union types to sample`` () =
    let symbolOnly = greenSamples |> List.filter (fun s -> s.Kind = SymbolMutant)
    test <@ not (bar "mutation" (evaluate greenRows [] symbolOnly)).Pass @>

    let noUnions =
        { ShadowReport.defaults with
            UnionTypeCandidates = Some 0 }

    test <@ (bar "mutation" (ShadowReport.evaluate noUnions greenRows [] symbolOnly)).Pass @>

[<Fact>]
let ``an unsafe fingerprint miss fails, and safe misses fail above 5 percent of traced rows`` () =
    let unsafe =
        { greenRows.Head with
            FingerprintOutcome = UnsafeMiss }
        :: greenRows.Tail

    test <@ not (bar "fingerprint prediction" (evaluate unsafe [] greenSamples)).Pass @>

    let safe n =
        greenRows
        |> List.mapi (fun i r ->
            if i < n then
                { r with FingerprintOutcome = SafeMiss }
            else
                r)

    // 132 traced rows: 6 is under 5 %, 7 is over.
    test <@ (bar "fingerprint prediction" (evaluate (safe 6) [] greenSamples)).Pass @>
    test <@ not (bar "fingerprint prediction" (evaluate (safe 7) [] greenSamples)).Pass @>

[<Fact>]
let ``a window shorter than 14 days, short of runs, or recorded under full-runs fails`` () =
    let short = greenRows |> List.filter (fun r -> r.RecordedAt < t0.AddDays 10.0)
    test <@ not (bar "window" (evaluate short [] greenSamples)).Pass @>

    let fullRuns =
        greenRows |> List.map (fun r -> { r with RecordPolicy = "full-runs" })

    test <@ not (bar "window" (evaluate fullRuns [] greenSamples)).Pass @>

    let fewFull =
        greenRows
        |> List.filter (fun r -> r.Mode = ImpactRun || r.RunId = "f0" || r.RunId = "f11")

    test <@ not (bar "window" (evaluate fewFull [] greenSamples)).Pass @>

    // Rows written before the run kind was recorded count toward neither side.
    let unknown = greenRows |> List.map (fun r -> { r with Mode = ModeNotRecorded })
    test <@ not (bar "window" (evaluate unknown [] greenSamples)).Pass @>

[<Fact>]
let ``eligibility fails under 95 percent of rows eligible`` () =
    let rows =
        greenRows
        |> List.mapi (fun i r -> if i < 7 then { r with Eligible = Some false } else r)

    test <@ not (bar "eligibility" (evaluate rows [] greenSamples)).Pass @>

[<Fact>]
let ``latency is the nearest-rank p95 of the selection time`` () =
    let slow =
        greenRows
        |> List.mapi (fun i r -> if i < 7 then { r with SelectMs = 5000L } else r)

    test <@ not (bar "latency" (evaluate slow [] greenSamples)).Pass @>

    let fewSlow =
        greenRows
        |> List.mapi (fun i r -> if i < 6 then { r with SelectMs = 5000L } else r)

    test <@ (bar "latency" (evaluate fewSlow [] greenSamples)).Pass @>

[<Fact>]
let ``the payoff bar is the median launch ratio of impact rows that launched something`` () =
    let heavy = greenRows |> List.map (fun r -> { r with TraceClassTests = 40 })
    test <@ not (bar "payoff" (evaluate heavy [] greenSamples)).Pass @>

    // Impact rows where static launched nothing say nothing about the payoff.
    let withEmpty =
        greenRows
        @ [ for i in 0..199 ->
                { baseRow with
                    RunId = $"z%d{i}"
                    StaticClassTests = 0
                    TraceClassTests = 40 } ]

    let payoff = bar "payoff" (evaluate withEmpty [] greenSamples)
    test <@ payoff.Pass && payoff.Measured.Contains "method-level" @>

[<Fact>]
let ``render prints the table, the listed misses and the untraced-children decision point`` () =
    let rows =
        greenRows
        |> List.map (fun r ->
            if r.Mode = ImpactRun then
                { r with ChildUntracedTests = 1 }
            else
                r)

    let results = evaluate rows [ "i3", "P|K|a" ] greenSamples
    let text = ShadowReport.render results rows

    test
        <@
            text.Contains "| window |"
            && text.Contains "| FAIL |"
            && text.Contains "| PASS |"
        @>

    test <@ text.Contains "untraced children" && text.Contains "allowlist task due" @>

[<Fact>]
let ``rows and samples before --since are left out`` () =
    let rows = ShadowReport.since (Some(t0.AddDays 10.0)) greenRows

    test
        <@
            rows |> List.forall (fun r -> r.RecordedAt >= t0.AddDays 10.0)
            && not rows.IsEmpty
        @>

    test <@ ShadowReport.since None greenRows = greenRows @>

[<Fact>]
let ``recall that is not r/t is not measurable, whatever its shape`` () =
    let rows =
        greenRows
        |> List.map (fun r ->
            match r.RunId with
            | "f1" -> { r with TraceRecall = "x" }
            | "f2" -> { r with TraceRecall = "1/x" }
            | "f3" -> { r with TraceRecall = "1/2/3" }
            | "f4" -> { r with TraceRecall = "x/2" }
            | _ -> r)

    let result = bar "trace recall on full runs" (evaluate rows [] greenSamples)
    test <@ result.Pass && result.Measured.Contains "4 not measurable" @>

[<Fact>]
let ``the payoff median of an odd number of rows is its middle row`` () =
    let rows =
        [ for i, ratio in [ 0, 10; 1, 40; 2, 20 ] ->
              { baseRow with
                  RunId = $"o%d{i}"
                  TraceClassTests = ratio } ]

    // Ratios 0.2, 0.8 and 0.4 over 50: the median is 0.4.
    test <@ (bar "payoff" (evaluate rows [] greenSamples)).Measured.StartsWith "median class-level ratio 0.4 " @>

[<Fact>]
let ``a known union-type candidate count above the bar keeps the bar`` () =
    let symbolOnly = greenSamples |> List.filter (fun s -> s.Kind = SymbolMutant)

    let many =
        { ShadowReport.defaults with
            UnionTypeCandidates = Some 10 }

    test <@ not (bar "mutation" (ShadowReport.evaluate many greenRows [] symbolOnly)).Pass @>

[<Fact>]
let ``render says when no impact row launched a traced test, and when no allowlist task is due`` () =
    test <@ (ShadowReport.render (evaluate [] [] []) []).Contains "no impact row launched a traced test" @>
    let green = ShadowReport.render (evaluate greenRows [] greenSamples) greenRows
    test <@ green.Contains "no allowlist task" && green.Contains "shadow-report: PASS" @>
