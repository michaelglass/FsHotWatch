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
                                                   Unverifiable = 1
                                                   SameKey = 0 }
        @>

    test
        <@ PluginComparison.mismatches comparison |> List.map (fun m -> m.Plugin, m.File) = [ "analyzers", "src/B.fs" ] @>

/// The nightly keyed the file by the very key reuse would have read, so its "fresh" result may be
/// that cached entry: comparing would compare the cache with itself. It is its own outcome,
/// counted as neither equal nor unverifiable, whatever the findings.
[<Fact>]
let ``a file the nightly keyed by the reused key is sameKey, neither equal nor unverifiable`` () =
    test <@ compareFile Reused.SameKey fresh = FileOutcome.SameKey @>
    test <@ compareFile Reused.SameKey [] = FileOutcome.SameKey @>

    let comparison =
        PluginComparison.ofFiles
            "lint"
            [ "src/A.fs", FileOutcome.SameKey
              "src/B.fs", FileOutcome.SameKey
              "src/C.fs", FileOutcome.Equal
              "src/D.fs", FileOutcome.Unverifiable UnverifiableReason.NoReusedEntry
              "src/E.fs", FileOutcome.Mismatch([], [ Finding.ofEntry (entry "X" 1 "x") ]) ]

    test
        <@
            PluginComparison.counts comparison = { Equal = 1
                                                   Mismatch = 1
                                                   Unverifiable = 1
                                                   SameKey = 2 }
        @>

    test <@ PluginComparison.mismatches comparison |> List.map _.File = [ "src/E.fs" ] @>
    test <@ PluginComparison.sameKeyFiles comparison = [ "src/A.fs"; "src/B.fs" ] @>

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

/// S and S' with `differing` analyses substituted and nothing copied.
let private selected (s: Set<string>) (sPrime: Set<string>) (differing: int) : Selections =
    { S = s
      SPrime = sPrime
      DifferingAnalyses = differing
      UnverifiableAnalyses = 0
      Copy = ImpactDbCopy.NotCopied }

[<Fact>]
let ``T in S but not S-prime that fails in the nightly is attributable`` () =
    let result =
        compareTests (selected s sPrime 1) [ row TestOutcome.Passed TestOutcome.Failed ]

    test <@ result.Attributable |> List.map _.Test = [ "T" ] @>
    test <@ List.isEmpty result.SelectorMiss @>
    test <@ result.OutsideSPrimeFailedOrChanged = 1 @>
    test <@ result.DifferingAnalyses = 1 @>

[<Fact>]
let ``T passing as it did in the watermark run counts nothing`` () =
    let result =
        compareTests (selected s sPrime 1) [ row TestOutcome.Passed TestOutcome.Passed ]

    test <@ List.isEmpty result.Attributable @>
    test <@ List.isEmpty result.SelectorMiss @>
    test <@ List.isEmpty result.FlakyChanged @>
    test <@ result.OutsideSPrimeFailedOrChanged = 0 @>

[<Fact>]
let ``a failing test outside S is a selector miss, and one inside S-prime is not counted`` () =
    let result =
        compareTests
            (selected s sPrime 0)
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

// --- the spot-check of sameKey files -------------------------------------------------------

let private noSpotChecks: SpotChecks = { Seed = 7; Analyzers = []; Lint = [] }

let private sampleSpotChecks: SpotChecks =
    { Seed = 42
      Analyzers = []
      Lint =
        [ "src/D.fs",
          SpotOutcome.Mismatch(
              [ Finding.ofEntry (entry "X" 1 "cached") ],
              [ Finding.ofEntry (entry "X" 1 "recomputed") ]
          )
          "src/E.fs", SpotOutcome.Equal
          "src/F.fs", SpotOutcome.NotRecomputed "the live check did not complete" ] }

let private manyFiles = [ for i in 1..120 -> $"src/F%03d{i}.fs" ]

/// The sample is a pure function of the seed and the files: the same seed draws the same files
/// whatever order they are named in, at most `size` of them, all of them when there are fewer,
/// never one twice, and in path order.
[<Fact>]
let ``the spot-check sample is drawn by its seed: fixed size, or every file when there are fewer`` () =
    let drawn = SpotCheck.sample 1234 SpotCheckSize manyFiles

    test <@ SpotCheckSize = 50 @>
    test <@ drawn.Length = 50 @>
    test <@ drawn = List.distinct drawn && drawn = List.sort drawn @>
    test <@ drawn |> List.forall (fun f -> List.contains f manyFiles) @>
    test <@ SpotCheck.sample 1234 SpotCheckSize (List.rev manyFiles) = drawn @>
    test <@ SpotCheck.sample 1235 SpotCheckSize manyFiles <> drawn @>

    let few = [ "src/B.fs"; "src/A.fs"; "src/C.fs" ]
    test <@ SpotCheck.sample 1234 SpotCheckSize few = [ "src/A.fs"; "src/B.fs"; "src/C.fs" ] @>
    test <@ List.isEmpty (SpotCheck.sample 1234 SpotCheckSize []) @>

/// The recomputed result against the cached one: equal in any order and whatever the detail; a
/// mismatch keeps both lists; a file whose result could not be recomputed says why.
[<Fact>]
let ``each sampled file's recomputed result is compared with the cached one`` () =
    let recomputed = fresh |> List.rev |> List.map (fun e -> { e with Detail = None })

    test <@ SpotCheck.compare fresh (Result.Ok recomputed) = SpotOutcome.Equal @>

    match SpotCheck.compare fresh (Result.Ok []) with
    | SpotOutcome.Mismatch(cached, again) ->
        test <@ cached = (fresh |> List.map Finding.ofEntry |> List.sort) @>
        test <@ List.isEmpty again @>
    | other -> failwith $"expected a mismatch, got %A{other}"

    test <@ SpotCheck.compare fresh (Result.Error "gone") = SpotOutcome.NotRecomputed "gone" @>

    let counts = SpotCheck.counts sampleSpotChecks.Lint

    test
        <@
            counts = { Sampled = 3
                       Equal = 1
                       Mismatch = 1
                       NotRecomputed = 1 }
        @>

    // The wire formats a plugin's spot-check command speaks round-trip.
    let request = [ "src/A.fs"; "src/B.fs" ]
    test <@ SpotCheckRequest.tryOfJson (SpotCheckRequest.toJson request) = Some request @>
    test <@ SpotCheckReply.tryOfJson (SpotCheckReply.toJson sampleSpotChecks.Lint) = Some sampleSpotChecks.Lint @>
    test <@ SpotCheckRequest.tryOfJson "{}" = None && SpotCheckReply.tryOfJson "[]" = None @>

    let unknownOutcome =
        (SpotCheckReply.toJson [ "src/A.fs", SpotOutcome.Equal ]).Replace("\"equal\"", "\"maybe\"")

    test <@ SpotCheckReply.tryOfJson unknownOutcome = None @>

// --- the land host -------------------------------------------------------------------------

[<Fact>]
let ``on any host but the land host the result is hostMismatch, with no per-file counts`` () =
    let compared () =
        Compared
            { Servable = 3
              Analyzers = PluginComparison.ofFiles "analyzers" []
              Lint = PluginComparison.ofFiles "lint" []
              SpotChecks = noSpotChecks
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
                    [ "src/C.fs", FileOutcome.Unverifiable UnverifiableReason.ConfigChanged
                      "src/D.fs", FileOutcome.SameKey ]
              SpotChecks = sampleSpotChecks
              TestPrune =
                TestPruneSection.Compared(
                    { compareTests (selected s sPrime 2) [ row TestOutcome.Passed TestOutcome.Failed ] with
                        ImpactDbCopy = ImpactDbCopy.Copied(4096L, 12L) }
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
                                     Unverifiable = 0
                                     SameKey = 0 }
            @>

        test
            <@
                counts.Lint = { Equal = 0
                                Mismatch = 0
                                Unverifiable = 1
                                SameKey = 1 }
            @>

        test <@ counts.SpotChecks.Seed = 42 @>
        test <@ counts.SpotChecks.Analyzers.Sampled = 0 @>

        test
            <@
                counts.SpotChecks.Lint = { Sampled = 3
                                           Equal = 1
                                           Mismatch = 1
                                           NotRecomputed = 1 }
            @>

        match counts.TestPrune with
        | TestPruneSummary.Compared tp ->
            test <@ tp.Attributable = 1 && tp.SelectorMiss = 0 && tp.DifferingAnalyses = 2 @>
            test <@ tp.ImpactDbCopy = ImpactDbCopy.Copied(4096L, 12L) @>
        | TestPruneSummary.NotCompared why
        | TestPruneSummary.Invalid why -> failwith $"expected test-prune counts, got %s{why}"
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
                      SpotChecks = noSpotChecks
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
                      SpotChecks = noSpotChecks
                      TestPrune =
                        TestPruneSection.Compared(
                            compareTests
                                (selected Set.empty Set.empty 0)
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

// --- the commands' wire formats ----------------------------------------------------------

[<Fact>]
let ``the servable files and a plugin's reply round-trip, and anything else reads as none`` () =
    let servable =
        [ { File = "src/A.fs"
            Project = "src/P.fsproj"
            FcsSignature = "fcs"
            UsedSignatures = UsedSignaturesKey.Described "used" }
          { File = "src/B.fs"
            Project = "src/P.fsproj"
            FcsSignature = "fcs2"
            UsedSignatures = UsedSignaturesKey.Undescribed } ]

    test <@ Servable.tryOfJson (Servable.toJson servable) = Some servable @>
    test <@ Servable.tryOfJson "{\"schema\":\"other\",\"files\":[]}" = None @>

    let comparison =
        PluginComparison.ofFiles
            "analyzers"
            [ "src/A.fs", FileOutcome.Equal
              "src/B.fs", FileOutcome.Unverifiable UnverifiableReason.NoReusedKey
              "src/D.fs", FileOutcome.Unverifiable UnverifiableReason.DisplacedKeyDiffers
              "src/C.fs",
              FileOutcome.Mismatch(
                  [ Finding.ofEntry (entry "X" 1 "reused") ],
                  [ Finding.ofEntry (entry "X" 1 "fresh") ]
              ) ]

    test <@ PluginReply.tryOfJson (PluginReply.toJson comparison) = Some comparison @>
    test <@ PluginReply.tryOfJson (Servable.toJson servable) = None @>

/// A refused S′ is an invalid test-prune comparison with its reason, in the report and the
/// summary line alike, and test-prune's refused answer reads back as the refusal it was.
[<Fact>]
let ``a refused S-prime is an invalid test-prune comparison, with its reason, through every file`` () =
    let reason = "the impact index is not sealed"

    let report =
        { sample with
            Result =
                Compared
                    { Servable = 0
                      Analyzers = PluginComparison.ofFiles "analyzers" []
                      Lint = PluginComparison.ofFiles "lint" []
                      SpotChecks = noSpotChecks
                      TestPrune = TestPruneSection.Invalid reason } }

    test <@ Report.tryOfJson (Report.toJson report) = Some report @>
    let summary = Summary.ofReport report
    test <@ Summary.tryOfJson (Summary.toJson summary) = Some summary @>

    match summary.Result with
    | SummaryResult.Compared counts -> test <@ counts.TestPrune = TestPruneSummary.Invalid reason @>
    | SummaryResult.HostMismatch _ -> failwith "expected counts"

    let refused = SelectionsAnswer.Refused reason
    test <@ SelectionsReply.tryOfJson (SelectionsReply.toJson refused) = Some refused @>

    let unknownResult =
        """{"schema":"fshw-references-shadow-selections-v2","result":"other"}"""

    test <@ SelectionsReply.tryOfJson unknownResult = None @>
