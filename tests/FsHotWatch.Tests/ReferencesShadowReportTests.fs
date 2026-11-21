/// AUTOMATION-1816 step 2b: what the nightly's output-level shadow concludes from what reuse
/// would have served each plugin and what the same nightly produced. Record and compare only.
module FsHotWatch.Tests.ReferencesShadowReportTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.ReferencesShadowReport
open FsHotWatch.Tests.TestHelpers

let private entry (code: string) (line: int) (message: string) : ErrorEntry =
    { Message = message
      Severity = DiagnosticSeverity.Warning
      Line = line
      Column = 1
      Code = Some code
      Detail = Some "detail is not compared" }

let private fresh = [ entry "FL0001" 3 "b"; entry "FL0002" 1 "a" ]

// --- test 2 / 3: analyzers and lint ------------------------------------------------------

[<Fact>]
let ``a stored record equal to the fresh findings is equal, in any order and whatever the detail`` () =
    let reused =
        Reused.Found(
            fresh
            |> List.rev
            |> List.map (fun e -> Finding.ofEntry { e with Detail = None })
        )

    test <@ compareFile reused fresh = FileOutcome.Equal @>

[<Fact>]
let ``an altered stored record is a mismatch that keeps both lists`` () =
    let altered =
        [ entry "FL0001" 3 "b"; entry "FL0002" 2 "a" ] |> List.map Finding.ofEntry

    match compareFile (Reused.Found altered) fresh with
    | FileOutcome.Mismatch(reused, freshFindings) ->
        test <@ reused = List.sort altered @>
        test <@ freshFindings = (fresh |> List.map Finding.ofEntry |> List.sort) @>
    | other -> failwith $"expected a mismatch, got %A{other}"

[<Fact>]
let ``a deleted record or a changed config is unverifiable, never equal`` () =
    test
        <@
            compareFile (Reused.Unverifiable UnverifiableReason.NoReusedEntry) fresh = FileOutcome.Unverifiable
                UnverifiableReason.NoReusedEntry
        @>

    test
        <@
            compareFile (Reused.Unverifiable UnverifiableReason.ConfigChanged) [] = FileOutcome.Unverifiable
                UnverifiableReason.ConfigChanged
        @>

[<Fact>]
let ``a plugin's comparison names the file and the plugin of each mismatch and counts each outcome`` () =
    let comparison =
        PluginComparison.ofFiles
            "analyzers"
            [ "src/A.fs", FileOutcome.Equal
              "src/B.fs", FileOutcome.Mismatch([ Finding.ofEntry (entry "X" 1 "x") ], [])
              "src/C.fs", FileOutcome.Unverifiable UnverifiableReason.NoReusedEntry ]

    test
        <@
            PluginComparison.counts comparison = { Equal = 1
                                                   Mismatch = 1
                                                   Unverifiable = 1 }
        @>

    test
        <@ PluginComparison.mismatches comparison |> List.map (fun m -> m.Plugin, m.File) = [ "analyzers", "src/B.fs" ] @>

// --- test 5: the changed-outcome table ---------------------------------------------------

let private row watermark nightly =
    { Test = "T"
      Watermark = watermark
      Nightly = nightly
      Quarantined = false
      Unstable = false }

[<Fact>]
let ``the changed-outcome table`` () =
    test <@ classify (row TestOutcome.Passed TestOutcome.Failed) = Some Counted.FailedOrChanged @>
    test <@ classify (row TestOutcome.Failed TestOutcome.Passed) = Some Counted.FailedOrChanged @>
    test <@ classify (row TestOutcome.Passed TestOutcome.Absent) = Some Counted.FailedOrChanged @>
    test <@ classify (row TestOutcome.Passed TestOutcome.Passed) = None @>
    // Red in both runs and not quarantined: it failed.
    test <@ classify (row TestOutcome.Failed TestOutcome.Failed) = Some Counted.FailedOrChanged @>
    // Quarantined and still red: not a change.
    test
        <@
            classify
                { row TestOutcome.Failed TestOutcome.Failed with
                    Quarantined = true } = None
        @>
    // A flaky flip, or a timeout or cancellation: listed, not counted.
    test
        <@
            classify
                { row TestOutcome.Passed TestOutcome.Failed with
                    Unstable = true } = Some Counted.FlakyChanged
        @>

    test
        <@
            classify
                { row TestOutcome.Passed TestOutcome.Passed with
                    Unstable = true } = None
        @>

// --- test 4: test-prune attribution --------------------------------------------------------

let private s = set [ "T"; "U" ]
let private sPrime = set [ "U" ] // the stale analysis dropped the edge to T

[<Fact>]
let ``T in S but not S-prime that fails in the nightly is attributable`` () =
    let result = compareTests s sPrime 1 [ row TestOutcome.Passed TestOutcome.Failed ]

    test <@ result.Attributable |> List.map _.Test = [ "T" ] @>
    test <@ List.isEmpty result.SelectorMiss @>
    test <@ result.OutsideSPrimeFailedOrChanged = 1 @>
    test <@ result.DifferingAnalyses = 1 @>

[<Fact>]
let ``T passing as it did in the watermark run counts nothing`` () =
    let result = compareTests s sPrime 1 [ row TestOutcome.Passed TestOutcome.Passed ]
    test <@ List.isEmpty result.Attributable @>
    test <@ List.isEmpty result.SelectorMiss @>
    test <@ List.isEmpty result.FlakyChanged @>
    test <@ result.OutsideSPrimeFailedOrChanged = 0 @>

[<Fact>]
let ``a failing test outside S is a selector miss, and one inside S-prime is not counted`` () =
    let result =
        compareTests
            s
            sPrime
            0
            [ { row TestOutcome.Passed TestOutcome.Failed with
                  Test = "V" }
              { row TestOutcome.Passed TestOutcome.Failed with
                  Test = "U" }
              { row TestOutcome.Passed TestOutcome.Failed with
                  Test = "T"
                  Unstable = true } ]

    test <@ result.SelectorMiss |> List.map _.Test = [ "V" ] @>
    test <@ List.isEmpty result.Attributable @>
    test <@ result.FlakyChanged |> List.map _.Test = [ "T" ] @>
    test <@ result.OutsideSPrimeFailedOrChanged = 1 @>

// --- the land host -------------------------------------------------------------------------

[<Fact>]
let ``on any host but the land host the result is hostMismatch, with no per-file counts`` () =
    let compared () =
        Compared
            { Servable = 3
              Analyzers = PluginComparison.ofFiles "analyzers" []
              Lint = PluginComparison.ofFiles "lint" []
              TestPrune = TestPruneSection.NotCompared "no watermark" }

    test <@ onHost "laptop" "land-host" compared = HostMismatch("laptop", "land-host") @>
    // The host name is compared without case or domain.
    test <@ onHost "Land-Host.local" "land-host" compared = compared () @>

    let summary =
        Summary.ofReport (
            { RunId = "r"
              WrittenAt = DateTimeOffset.UnixEpoch
              Result = HostMismatch("laptop", "land-host") }
            : Report
        )

    test <@ summary.Result = SummaryResult.HostMismatch("laptop", "land-host") @>

// --- test 8: round trip and retention ------------------------------------------------------

let private sample: Report =
    { RunId = "run-1"
      WrittenAt = DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero)
      Result =
        Compared
            { Servable = 4
              Analyzers =
                PluginComparison.ofFiles
                    "analyzers"
                    [ "src/A.fs", FileOutcome.Equal
                      "src/B.fs",
                      FileOutcome.Mismatch(
                          [ Finding.ofEntry (entry "X" 1 "x") ],
                          [ Finding.ofEntry { entry "X" 2 "x" with Code = None } ]
                      ) ]
              Lint =
                PluginComparison.ofFiles
                    "lint"
                    [ "src/C.fs", FileOutcome.Unverifiable UnverifiableReason.ConfigChanged ]
              TestPrune =
                TestPruneSection.Compared(
                    { compareTests s sPrime 2 [ row TestOutcome.Passed TestOutcome.Failed ] with
                        ImpactDbCopyBytes = 4096L }
                ) } }

[<Fact>]
let ``the report and its summary line round-trip`` () =
    test <@ Report.tryOfJson (Report.toJson sample) = Some sample @>

    let mismatch: Report =
        { sample with
            Result = HostMismatch("laptop", "land-host") }

    test <@ Report.tryOfJson (Report.toJson mismatch) = Some mismatch @>
    let mismatchSummary = Summary.ofReport mismatch
    test <@ Summary.tryOfJson (Summary.toJson mismatchSummary) = Some mismatchSummary @>

    let summary = Summary.ofReport sample
    test <@ Summary.tryOfJson (Summary.toJson summary) = Some summary @>

    match summary.Result with
    | SummaryResult.Compared counts ->
        test <@ counts.Servable = 4 @>

        test
            <@
                counts.Analyzers = { Equal = 1
                                     Mismatch = 1
                                     Unverifiable = 0 }
            @>

        test
            <@
                counts.Lint = { Equal = 0
                                Mismatch = 0
                                Unverifiable = 1 }
            @>

        match counts.TestPrune with
        | TestPruneSummary.Compared tp ->
            test <@ tp.Attributable = 1 && tp.SelectorMiss = 0 && tp.DifferingAnalyses = 2 @>
            test <@ tp.ImpactDbCopyBytes = 4096L @>
        | TestPruneSummary.NotCompared why -> failwith $"expected test-prune counts, got %s{why}"
    | SummaryResult.HostMismatch _ -> failwith "expected counts"

    for text in [ "{ not json"; "[]"; """{"schema":"other"}""" ] do
        test <@ Report.tryOfJson text = None @>
        test <@ Summary.tryOfJson text = None @>

[<Fact>]
let ``writing a report files the detail and one summary line, and keeps only the last 30 reports`` () =
    withTempDir "refshadow-write" (fun repoRoot ->
        for i in 1..32 do
            write
                repoRoot
                { sample with
                    RunId = $"run-%02d{i}"
                    WrittenAt = sample.WrittenAt.AddDays(float i) }

        let reports = Directory.GetFiles(reportsDirectory repoRoot, "*.json")
        test <@ reports.Length = RetainedReports @>
        test <@ RetainedReports = 30 @>
        test <@ not (File.Exists(Path.Combine(reportsDirectory repoRoot, "run-01.json"))) @>
        test <@ not (File.Exists(Path.Combine(reportsDirectory repoRoot, "run-02.json"))) @>

        test
            <@
                Report.tryOfJson (File.ReadAllText(Path.Combine(reportsDirectory repoRoot, "run-32.json")))
                |> Option.map _.RunId = Some "run-32"
            @>

        let lines = File.ReadAllLines(summaryPath repoRoot)
        test <@ lines.Length = 32 @>
        test <@ readSummaries repoRoot |> List.map _.RunId |> List.last = "run-32" @>)

[<Fact>]
let ``a report whose test-prune side was not compared, with every outcome token, round-trips`` () =
    let report: Report =
        { sample with
            Result =
                Compared
                    { Servable = 1
                      Analyzers =
                        PluginComparison.ofFiles
                            "analyzers"
                            [ "src/A.fs", FileOutcome.Unverifiable UnverifiableReason.NoReusedEntry ]
                      Lint = PluginComparison.ofFiles "lint" []
                      TestPrune = TestPruneSection.NotCompared "no watermark run" } }

    test <@ Report.tryOfJson (Report.toJson report) = Some report @>
    let summary = Summary.ofReport report
    test <@ Summary.tryOfJson (Summary.toJson summary) = Some summary @>

    let outcomes =
        { sample with
            Result =
                Compared
                    { Servable = 0
                      Analyzers = PluginComparison.ofFiles "analyzers" []
                      Lint = PluginComparison.ofFiles "lint" []
                      TestPrune =
                        TestPruneSection.Compared(
                            compareTests
                                Set.empty
                                Set.empty
                                0
                                [ { row TestOutcome.Skipped TestOutcome.Absent with
                                      Test = "A" }
                                  { row TestOutcome.Absent TestOutcome.Skipped with
                                      Test = "B" } ]
                        ) } }

    test <@ Report.tryOfJson (Report.toJson outcomes) = Some outcomes @>

[<Theory>]
[<InlineData("\"outcome\":\"equal\"", "\"outcome\":\"maybe\"")>]
[<InlineData("\"reason\":\"config-changed\"", "\"reason\":\"other\"")>]
[<InlineData("\"watermark\":\"passed\"", "\"watermark\":\"other\"")>]
[<InlineData("\"result\":\"compared\",\"servable\"", "\"result\":\"other\",\"servable\"")>]
[<InlineData("\"result\":\"compared\",\"differing", "\"result\":\"other\",\"differing")>]
[<InlineData("\"runId\":\"run-1\"", "\"runId\":5")>]
[<InlineData("\"runId\":\"run-1\"", "\"run\":\"run-1\"")>]
[<InlineData("\"lint\":{", "\"lint\":5,\"x\":{")>]
[<InlineData("\"servable\":4", "\"servable\":\"4\"")>]
[<InlineData("\"writtenAt\":\"", "\"writtenAt\":\"x")>]
let ``a report that is not exactly this shape reads as none`` (valid: string, damaged: string) =
    let text = Report.toJson sample
    test <@ text.Contains valid @>
    test <@ Report.tryOfJson (text.Replace(valid, damaged)) = None @>

[<Theory>]
[<InlineData("\"result\":\"compared\",\"servable\"", "\"result\":\"other\",\"servable\"")>]
[<InlineData("\"result\":\"compared\",\"differing", "\"result\":\"other\",\"differing")>]
let ``a summary line that is not exactly this shape reads as none`` (valid: string, damaged: string) =
    let text = Summary.toJson (Summary.ofReport sample)
    test <@ text.Contains valid @>
    test <@ Summary.tryOfJson (text.Replace(valid, damaged)) = None @>

[<Fact>]
let ``a report that cannot be written is logged and dropped, and no summary file reads as none`` () =
    withTempDir "refshadow-fail" (fun repoRoot ->
        // No .fshw directory yet, then one with no summary file.
        test <@ List.isEmpty (readSummaries repoRoot) @>
        Directory.CreateDirectory(FsHwPaths.root repoRoot) |> ignore
        test <@ List.isEmpty (readSummaries repoRoot) @>

        // The reports directory is a file: nothing can be written under it.
        File.WriteAllText(reportsDirectory repoRoot, "")
        write repoRoot sample
        test <@ List.isEmpty (readSummaries repoRoot) @>)
