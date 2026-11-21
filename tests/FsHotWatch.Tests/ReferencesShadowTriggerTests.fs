/// AUTOMATION-1816 step 2b, design test 6: the report is made only after a fresh confirm that
/// kept no prior verdict and graded a run, with the nightly shadow on. A reused confirm, a kept
/// verdict and the switch off write nothing and ask the daemon nothing. (A filtered `check` never
/// reaches the trigger: only `confirm`'s run arm calls it.)
module FsHotWatch.Tests.ReferencesShadowTriggerTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ReferencesShadowReport
open FsHotWatch.Cli
open FsHotWatch.Cli.ReferencesShadowTrigger
open FsHotWatch.Tests.TestHelpers

let private runId = Guid.Parse "0123456789abcdef0123456789abcdef"

let private nothingBefore =
    { Changed = []
      QuarantinedClasses = Set.empty
      WatermarkRun = None }

/// A transport that fails the test if anything is asked.
let private askNothing: ScopeCommands.Send =
    fun name _ -> failwith $"the daemon was asked `%s{name}`"

/// test-prune's answer when the index held this run's analysis.
let private selectedReply (selections: Selections) =
    SelectionsReply.toJson (SelectionsAnswer.Selected selections)

let private onLandHost =
    FsHotWatch.InMemoryCheckCache.NightlyShadow.OnLandHost "land-box"

[<Fact>]
let ``only a fresh confirm that kept no verdict and graded a run is compared`` () =
    test <@ graded CheckVerdict.Freshness.RequireNewRun false (Some runId) = Graded.Compare runId @>

    test
        <@
            match graded CheckVerdict.Freshness.MayReuse false (Some runId) with
            | Graded.Skip _ -> true
            | Graded.Compare _ -> false
        @>

    test
        <@
            match graded CheckVerdict.Freshness.RequireNewRun true (Some runId) with
            | Graded.Skip _ -> true
            | Graded.Compare _ -> false
        @>

    test
        <@
            match graded CheckVerdict.Freshness.RequireNewRun false None with
            | Graded.Skip _ -> true
            | Graded.Compare _ -> false
        @>

/// No report, and no lookup: the switch off, a reused confirm, a kept verdict. The exit code is
/// the confirm's.
[<Fact>]
let ``the switch off, a reused confirm or a kept verdict writes nothing and asks nothing`` () =
    withTempDir "refshadow-trigger" (fun repoRoot ->
        let cases =
            [ FsHotWatch.InMemoryCheckCache.NightlyShadow.Off, CheckVerdict.Freshness.RequireNewRun, false
              onLandHost, CheckVerdict.Freshness.MayReuse, false
              onLandHost, CheckVerdict.Freshness.RequireNewRun, true ]

        for nightly, freshness, kept in cases do
            let exitCode =
                aroundConfirm repoRoot nightly "land-box" freshness askNothing (fun () -> kept) (fun () -> 7)

            test <@ exitCode = 7 @>

        test <@ not (File.Exists(summaryPath repoRoot)) @>
        test <@ not (Directory.Exists(reportsDirectory repoRoot)) @>)

[<Fact>]
let ``on another host than the land host the report says so before any lookup`` () =
    let report =
        compare
            "land-box"
            "laptop"
            askNothing
            nothingBefore
            (fun () -> failwith "no rows")
            runId
            7
            DateTimeOffset.UnixEpoch

    test <@ report.Result = HostMismatch("laptop", "land-box") @>

[<Fact>]
let ``on the land host each plugin is asked about the servable files and the report is filed`` () =
    withTempDir "refshadow-trigger-compare" (fun repoRoot ->
        let servable =
            [ { File = "src/A.fs"
                Project = "src/P.fsproj"
                FcsSignature = "fcs"
                UsedSignatures = UsedSignaturesKey.Described "used" } ]

        let asked = Collections.Generic.List<string>()

        let send: ScopeCommands.Send =
            fun name args ->
                asked.Add name

                match name with
                | n when n = ServableCommand -> Some(Servable.toJson servable)
                | n when n = AnalyzersCommand ->
                    test <@ Servable.tryOfJson args = Some servable @>
                    Some(PluginReply.toJson (PluginComparison.ofFiles "analyzers" [ "src/A.fs", FileOutcome.Equal ]))
                | n when n = TestPruneCommand ->
                    test <@ (TestPruneRequest.tryOfJson args |> Option.map _.Changed) = Some [ "Lib.f" ] @>

                    Some(
                        selectedReply
                            { S = set [ "T" ]
                              SPrime = Set.empty
                              DifferingAnalyses = 1
                              UnverifiableAnalyses = 0
                              Copy = ImpactDbCopy.Copied(4096L, 3L) }
                    )
                | _ -> None

        let rows () =
            [ { Test = "T"
                Watermark = TestOutcome.Passed
                Nightly = TestOutcome.Failed
                Quarantined = false
                Unstable = false } ]

        let report =
            compare
                "land-box.example"
                "LAND-BOX"
                send
                { nothingBefore with
                    Changed = [ "Lib.f" ] }
                rows
                runId
                7
                DateTimeOffset.UnixEpoch

        write repoRoot report

        match readSummaries repoRoot with
        | [ { Result = SummaryResult.Compared counts } ] ->
            test <@ counts.Servable = 1 @>
            test <@ counts.Analyzers.Equal = 1 @>
            // No lint command on this host: every file unverifiable, never equal.
            test <@ counts.Lint.Unverifiable = 1 && counts.Lint.Equal = 0 @>

            match counts.TestPrune with
            | TestPruneSummary.Compared tp ->
                test <@ tp.Attributable = 1 && tp.ImpactDbCopy = ImpactDbCopy.Copied(4096L, 3L) @>
            | TestPruneSummary.NotCompared why
            | TestPruneSummary.Invalid why -> failwith why
        | other -> failwith $"expected one compared summary, got %A{other}"

        test <@ asked |> Seq.contains LintCommand @>
        // No sameKey file anywhere: no spot-check is asked for.
        test <@ not (asked |> Seq.contains AnalyzersSpotCheckCommand) @>
        test <@ not (asked |> Seq.contains LintSpotCheckCommand) @>)

/// ITEM 3. Each plugin's `sameKey` files are sampled by the report's seed, at most
/// `SpotCheckSize` of them, and only the sample is sent to the plugin's spot-check command; the
/// report records the seed and each sampled file's outcome. A host without the command leaves
/// each sampled file not recomputed. Record and compare only: the plugin replies are all it reads.
[<Fact>]
let ``each plugin's sameKey files are sampled by the seed, spot-checked, and recorded with it`` () =
    let lintFiles = [ for i in 1..60 -> $"src/L%02d{i}.fs" ]
    let analyzerFiles = [ "src/A1.fs"; "src/A2.fs" ]

    let servable =
        (lintFiles @ analyzerFiles)
        |> List.map (fun file ->
            { File = file
              Project = "src/P.fsproj"
              FcsSignature = "fcs"
              UsedSignatures = UsedSignaturesKey.Described "used" })

    let sentToLint = Collections.Generic.List<string list>()

    let send: ScopeCommands.Send =
        fun name args ->
            match name with
            | n when n = ServableCommand -> Some(Servable.toJson servable)
            | n when n = LintCommand ->
                Some(
                    PluginReply.toJson (
                        PluginComparison.ofFiles
                            "lint"
                            ((lintFiles |> List.map (fun f -> f, FileOutcome.SameKey))
                             @ (analyzerFiles |> List.map (fun f -> f, FileOutcome.Equal)))
                    )
                )
            | n when n = AnalyzersCommand ->
                Some(
                    PluginReply.toJson (
                        PluginComparison.ofFiles
                            "analyzers"
                            ((analyzerFiles |> List.map (fun f -> f, FileOutcome.SameKey))
                             @ (lintFiles
                                |> List.map (fun f -> f, FileOutcome.Unverifiable UnverifiableReason.NoReusedEntry)))
                    )
                )
            | n when n = LintSpotCheckCommand ->
                let files = SpotCheckRequest.tryOfJson args |> Option.defaultValue []
                sentToLint.Add files

                Some(
                    SpotCheckReply.toJson (
                        files
                        |> List.mapi (fun i f ->
                            f,
                            if i = 0 then
                                SpotOutcome.Mismatch(
                                    [],
                                    [ Finding.ofEntry (FsHotWatch.ErrorLedger.ErrorEntry.error "now") ]
                                )
                            else
                                SpotOutcome.Equal)
                    )
                )
            | _ -> None

    let report =
        compare "land-box" "land-box" send nothingBefore (fun () -> []) runId 99 DateTimeOffset.UnixEpoch

    match report.Result with
    | Compared c ->
        let expected = SpotCheck.sample 99 SpotCheckSize lintFiles
        test <@ c.SpotChecks.Seed = 99 @>
        test <@ List.ofSeq sentToLint = [ expected ] @>
        test <@ c.SpotChecks.Lint |> List.map fst = expected @>
        test <@ (SpotCheck.counts c.SpotChecks.Lint).Mismatch = 1 @>
        test <@ (SpotCheck.counts c.SpotChecks.Lint).Equal = SpotCheckSize - 1 @>

        test
            <@
                c.SpotChecks.Analyzers = (analyzerFiles
                                          |> List.map (fun f ->
                                              f,
                                              SpotOutcome.NotRecomputed
                                                  $"the daemon has no `%s{AnalyzersSpotCheckCommand}` command"))
            @>

        test <@ (PluginComparison.counts c.Lint).SameKey = 60 @>
    | HostMismatch _ -> failwith "expected a comparison"

    // A spot-check reply that is not one is a failure, as every malformed reply is.
    let malformed: ScopeCommands.Send =
        fun name args ->
            if name = LintSpotCheckCommand then
                Some "{}"
            else
                send name args

    raises<InvalidOperationException>
        <@ compare "land-box" "land-box" malformed nothingBefore (fun () -> []) runId 99 DateTimeOffset.UnixEpoch @>

[<Fact>]
let ``the rows read each test's outcome in the nightly and the watermark run`` () =
    withTempDir "refshadow-trigger-rows" (fun repoRoot ->
        let watermark = Guid.Parse "fedcba9876543210fedcba9876543210"

        let ctrf (run: Guid) (tests: (string * string) list) =
            let dir = Ctrf.runDir repoRoot run
            Directory.CreateDirectory dir |> ignore

            let rows =
                tests
                |> List.map (fun (name, status) ->
                    $"{{\"name\":\"%s{name}\",\"status\":\"%s{status}\",\"duration\":1}}")
                |> String.concat ","

            File.WriteAllText(
                Path.Combine(dir, "P" + Ctrf.ReportSuffix),
                $"{{\"results\":{{\"summary\":{{}},\"tests\":[%s{rows}]}}}}"
            )

        ctrf runId [ "M.A", "passed"; "M.B", "failed"; "Q.C", "failed" ]
        ctrf watermark [ "M.A", "passed"; "M.B", "passed"; "M.D", "passed"; "Q.C", "failed" ]

        let rows =
            testRows
                repoRoot
                { nothingBefore with
                    WatermarkRun = Some watermark
                    QuarantinedClasses = set [ "Q" ] }
                runId
            |> List.map (fun r -> r.Test, (r.Watermark, r.Nightly, r.Quarantined))
            |> Map.ofList

        test <@ rows["M.A"] = (TestOutcome.Passed, TestOutcome.Passed, false) @>
        test <@ rows["M.B"] = (TestOutcome.Passed, TestOutcome.Failed, false) @>
        test <@ rows["M.D"] = (TestOutcome.Passed, TestOutcome.Absent, false) @>
        test <@ rows["Q.C"] = (TestOutcome.Failed, TestOutcome.Failed, true) @>)

[<Fact>]
let ``before the run it reads C, the quarantined classes and no watermark without a verdict`` () =
    withTempDir "refshadow-trigger-before" (fun repoRoot ->
        test <@ readBefore repoRoot = nothingBefore @>

        FsHotWatch.TestPrune.PendingVerification.save repoRoot (set [ "Lib.f"; "Lib.g" ])

        FsHotWatch.TestPrune.OutstandingFailures.OutstandingFailure.save
            repoRoot
            [ { Project = "Tests"
                Class = Some "Q"
                Method = None
                File = "tests/Q.fs"
                Entry = FsHotWatch.ErrorLedger.ErrorEntry.error "red" } ]

        test
            <@
                readBefore repoRoot = { Changed = [ "Lib.f"; "Lib.g" ]
                                        QuarantinedClasses = set [ "Q" ]
                                        WatermarkRun = None }
            @>

        // Unreadable ledgers read as empty, never as a failure of the confirm.
        File.WriteAllText(FsHotWatch.TestPrune.PendingVerification.sidecarPath repoRoot, "{ torn")

        File.WriteAllText(FsHotWatch.TestPrune.OutstandingFailures.OutstandingFailure.sidecarPath repoRoot, "{ torn")

        test <@ readBefore repoRoot = nothingBefore @>)

[<Fact>]
let ``on the land host a fresh confirm that graded no run writes nothing`` () =
    withTempDir "refshadow-trigger-norun" (fun repoRoot ->
        let exitCode =
            aroundConfirm
                repoRoot
                onLandHost
                "land-box"
                CheckVerdict.Freshness.RequireNewRun
                askNothing
                (fun () -> false)
                (fun () -> 1)

        test <@ exitCode = 1 @>
        test <@ not (File.Exists(summaryPath repoRoot)) @>)

[<Fact>]
let ``a daemon without the servable command or a malformed reply is a failure, and no test-prune is not compared`` () =
    let rows () = []

    let answer (replies: Map<string, string>) : ScopeCommands.Send = fun name _ -> Map.tryFind name replies

    let run send =
        compare "land-box" "land-box" send nothingBefore rows runId 7 DateTimeOffset.UnixEpoch

    raises<InvalidOperationException> <@ run (answer Map.empty) @>
    raises<InvalidOperationException> <@ run (answer (Map.ofList [ ServableCommand, "{}" ])) @>

    let servable = Servable.toJson []

    raises<InvalidOperationException>
        <@ run (answer (Map.ofList [ ServableCommand, servable; TestPruneCommand, "{}" ])) @>

    raises<InvalidOperationException>
        <@ run (answer (Map.ofList [ ServableCommand, servable; AnalyzersCommand, "{}" ])) @>

    match (run (answer (Map.ofList [ ServableCommand, servable ]))).Result with
    | Compared c -> test <@ c.TestPrune = TestPruneSection.NotCompared "no test-prune plugin" @>
    | HostMismatch _ -> failwith "expected a comparison"

/// A `confirm` verdict in `repoRoot` with `outcome`, graded from the run `graded`.
let private writeVerdict (repoRoot: string) (graded: Guid option) (outcome: Verdict.Outcome) =
    Verdict.create
        Verdict.Confirm
        { BaselineFixtures.reportOf (IpcParsing.FullSuite(1, None)) with
            RunId = graded }
        ({ Hash = "sha256:fixture"
           FileCount = 1
           SkippedCount = 0
           DeclaredCount = 0
           AbsentDeclarationCount = 0 }
        : TreeHash.Tree)
        (Some [])
        outcome
        0
        []
        []
        Verdict.CheckComparison.notRecorded
        []
        ProjectModelFixtures.available
        (fun _ -> Map.empty)
    |> Verdict.write repoRoot

/// The watermark is the run the prior verdict's full-suite green was earned by; a verdict that is
/// not a green, or cannot be read, names none.
[<Fact>]
let ``before the run the watermark is the run a full-suite green verdict was earned by`` () =
    withTempDir "refshadow-trigger-watermark" (fun repoRoot ->
        writeVerdict repoRoot (Some runId) (Verdict.Green BaselineFixtures.baseline)
        test <@ (readBefore repoRoot).WatermarkRun = Some BaselineFixtures.runId @>

        writeVerdict repoRoot (Some runId) (Verdict.Incomplete "fixture")
        test <@ (readBefore repoRoot).WatermarkRun = None @>

        // An unreadable verdict names no watermark either.
        File.WriteAllText(FsHwPaths.verdictFile repoRoot, "{ torn")
        test <@ (readBefore repoRoot).WatermarkRun = None @>)

/// A transport answering the servable file, the analyzers and test-prune, recording what it was asked.
let private answering (asked: Collections.Generic.List<string>) : ScopeCommands.Send =
    let servable =
        [ { File = "src/A.fs"
            Project = "src/P.fsproj"
            FcsSignature = "fcs"
            UsedSignatures = UsedSignaturesKey.Described "used" } ]

    fun name _ ->
        asked.Add name

        match name with
        | n when n = ServableCommand -> Some(Servable.toJson servable)
        | n when n = AnalyzersCommand ->
            Some(PluginReply.toJson (PluginComparison.ofFiles "analyzers" [ "src/A.fs", FileOutcome.Equal ]))
        | n when n = LintCommand ->
            Some(
                PluginReply.toJson (
                    PluginComparison.ofFiles
                        "lint"
                        [ "src/A.fs", FileOutcome.Unverifiable UnverifiableReason.DisplacedKeyDiffers ]
                )
            )
        | n when n = TestPruneCommand ->
            Some(
                selectedReply
                    { S = set [ "M.T" ]
                      SPrime = Set.empty
                      DifferingAnalyses = 1
                      UnverifiableAnalyses = 0
                      Copy = ImpactDbCopy.Copied(4096L, 3L) }
            )
        | _ -> None

/// The whole path on the land host: the confirm publishes a verdict naming the run it graded, and
/// the trigger reads C, the watermark from the prior green and the run's CTRF rows, asks each
/// plugin, and files the report for that run. The exit code is the run's.
[<Fact>]
let ``a fresh confirm that graded a run on the land host files that run's report`` () =
    withTempDir "refshadow-trigger-files" (fun repoRoot ->
        // The prior green, earned by the watermark run, and C.
        writeVerdict repoRoot (Some BaselineFixtures.runId) (Verdict.Green BaselineFixtures.baseline)
        FsHotWatch.TestPrune.PendingVerification.save repoRoot (set [ "Lib.f" ])

        let ctrf (run: Guid) (status: string) =
            let dir = Ctrf.runDir repoRoot run
            Directory.CreateDirectory dir |> ignore

            File.WriteAllText(
                Path.Combine(dir, "P" + Ctrf.ReportSuffix),
                $"{{\"results\":{{\"summary\":{{}},\"tests\":[{{\"name\":\"M.T\",\"status\":\"%s{status}\",\"duration\":1}}]}}}}"
            )

        ctrf BaselineFixtures.runId "passed"
        let asked = Collections.Generic.List<string>()

        let exitCode =
            aroundConfirm
                repoRoot
                onLandHost
                "land-box"
                CheckVerdict.Freshness.RequireNewRun
                (answering asked)
                (fun () -> false)
                (fun () ->
                    // The run: M.T fails, and the confirm publishes a verdict graded from it.
                    ctrf runId "failed"
                    writeVerdict repoRoot (Some runId) (Verdict.Incomplete "fixture")
                    1)

        test <@ exitCode = 1 @>
        test <@ List.ofSeq asked = [ ServableCommand; TestPruneCommand; AnalyzersCommand; LintCommand ] @>

        match readSummaries repoRoot with
        | [ summary ] ->
            test <@ summary.RunId = runId.ToString "N" @>

            match summary.Result with
            | SummaryResult.Compared counts ->
                test
                    <@
                        counts.Servable = 1
                        && counts.Analyzers.Equal = 1
                        && counts.Lint.Unverifiable = 1
                    @>

                match counts.TestPrune with
                // M.T passed in the watermark run and failed here, in S and not S′: attributable.
                | TestPruneSummary.Compared tp -> test <@ tp.Attributable = 1 && tp.SelectorMiss = 0 @>
                | TestPruneSummary.NotCompared why
                | TestPruneSummary.Invalid why -> failwith why
            | SummaryResult.HostMismatch _ -> failwith "expected a comparison"
        | other -> failwith $"expected one summary, got %A{other}")

/// The report is measurement: a comparison that fails is logged and dropped, never the confirm's.
[<Fact>]
let ``a comparison that fails files nothing and keeps the run's exit code`` () =
    withTempDir "refshadow-trigger-fails" (fun repoRoot ->
        let exitCode =
            aroundConfirm
                repoRoot
                onLandHost
                "land-box"
                CheckVerdict.Freshness.RequireNewRun
                (fun _ _ -> None)
                (fun () -> false)
                (fun () ->
                    writeVerdict repoRoot (Some runId) (Verdict.Incomplete "fixture")
                    1)

        test <@ exitCode = 1 @>
        test <@ not (File.Exists(summaryPath repoRoot)) @>)

/// A test is unstable when it timed out or was cancelled in either run, or when its last runs
/// flip; a skipped test is skipped; a watermark run with no reports reads every test as absent.
[<Fact>]
let ``the rows mark stopped and flaky tests unstable, and read a missing watermark run as absent`` () =
    withTempDir "refshadow-trigger-unstable" (fun repoRoot ->
        let dir = Ctrf.runDir repoRoot runId
        Directory.CreateDirectory dir |> ignore

        let row (name: string) (status: string) (exn: string option) =
            let extra =
                match exn with
                | Some e -> $",\"extra\":{{\"exception\":\"%s{e}\"}}"
                | None -> ""

            $"{{\"name\":\"%s{name}\",\"status\":\"%s{status}\",\"duration\":1%s{extra}}}"

        let rows =
            [ row "M.TimedOut" "failed" (Some "Xunit.Sdk.TestTimeoutException")
              row "M.Cancelled" "failed" (Some "System.OperationCanceledException")
              row "M.Skipped" "skipped" None
              row "M.Odd" "weird" None
              row "M.Flaky" "passed" None
              row "M.Steady" "passed" None ]
            |> String.concat ","

        File.WriteAllText(
            Path.Combine(dir, "P" + Ctrf.ReportSuffix),
            $"{{\"results\":{{\"summary\":{{}},\"tests\":[%s{rows}]}}}}"
        )

        let at (minutes: float) =
            DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes minutes

        FsHotWatch.TestPrune.Flakiness.appendRecords
            (FsHotWatch.TestPrune.Flakiness.historyPath repoRoot)
            20
            [ for i, outcome in
                  List.indexed
                      [ FsHotWatch.TestPrune.Flakiness.Passed
                        FsHotWatch.TestPrune.Flakiness.Failed
                        FsHotWatch.TestPrune.Flakiness.Passed ] do
                  { FsHotWatch.TestPrune.Flakiness.TestRunRecord.Name = "M.Flaky"
                    Outcome = outcome
                    DurationMs = 1
                    RunStartedAt = at (float i) }

                  { FsHotWatch.TestPrune.Flakiness.TestRunRecord.Name = "M.Steady"
                    Outcome = FsHotWatch.TestPrune.Flakiness.Passed
                    DurationMs = 1
                    RunStartedAt = at (float i) } ]

        let read =
            testRows
                repoRoot
                { nothingBefore with
                    WatermarkRun = Some(Guid.Parse "fedcba9876543210fedcba9876543210") }
                runId
            |> List.map (fun r -> r.Test, (r.Watermark, r.Nightly, r.Unstable))
            |> Map.ofList

        test <@ read["M.TimedOut"] = (TestOutcome.Absent, TestOutcome.Failed, true) @>
        test <@ read["M.Cancelled"] = (TestOutcome.Absent, TestOutcome.Failed, true) @>
        test <@ read["M.Skipped"] = (TestOutcome.Absent, TestOutcome.Skipped, false) @>
        test <@ read["M.Odd"] = (TestOutcome.Absent, TestOutcome.Failed, true) @>
        test <@ read["M.Flaky"] = (TestOutcome.Absent, TestOutcome.Passed, true) @>
        test <@ read["M.Steady"] = (TestOutcome.Absent, TestOutcome.Passed, false) @>)

/// TEST 7, the trigger's half (gates the land): with the nightly shadow on, the confirm runs the
/// same, answers the same exit code, and leaves the verdict and the ledgers the run reads
/// byte for byte as with it off.
[<Fact>]
let ``the nightly shadow on or off, the confirm's exit code, verdict and ledgers are the same`` () =
    let confirm (nightly: FsHotWatch.InMemoryCheckCache.NightlyShadow) =
        withTempDir "refshadow-gate-confirm" (fun repoRoot ->
            FsHotWatch.TestPrune.PendingVerification.save repoRoot (set [ "Lib.f" ])
            let runs = ref 0

            let send: ScopeCommands.Send =
                fun name _ ->
                    match name with
                    | n when n = ServableCommand -> Some(Servable.toJson [])
                    | _ -> None

            let exitCode =
                aroundConfirm
                    repoRoot
                    nightly
                    "land-box"
                    CheckVerdict.Freshness.RequireNewRun
                    send
                    (fun () -> false)
                    (fun () ->
                        runs.Value <- runs.Value + 1
                        Directory.CreateDirectory(FsHwPaths.root repoRoot) |> ignore
                        File.WriteAllText(FsHwPaths.verdictFile repoRoot, "{\"the\":\"verdict\"}")
                        3)

            exitCode,
            runs.Value,
            File.ReadAllText(FsHwPaths.verdictFile repoRoot),
            File.ReadAllText(FsHotWatch.TestPrune.PendingVerification.sidecarPath repoRoot))

    test <@ confirm onLandHost = confirm FsHotWatch.InMemoryCheckCache.NightlyShadow.Off @>

/// test-prune refusing S′ (its index did not hold this run's analysis) marks the comparison
/// invalid with the reason, loudly in the report; the confirm's exit code is untouched.
[<Fact>]
let ``a refused S-prime files an invalid test-prune comparison and keeps the run's exit code`` () =
    withTempDir "refshadow-trigger-refused" (fun repoRoot ->
        writeVerdict repoRoot (Some BaselineFixtures.runId) (Verdict.Green BaselineFixtures.baseline)
        let asked = Collections.Generic.List<string>()
        let reason = "the impact index is not sealed"

        let send: ScopeCommands.Send =
            fun name args ->
                match name with
                | n when n = TestPruneCommand ->
                    asked.Add name
                    Some(SelectionsReply.toJson (SelectionsAnswer.Refused reason))
                | _ -> answering asked name args

        let exitCode =
            aroundConfirm
                repoRoot
                onLandHost
                "land-box"
                CheckVerdict.Freshness.RequireNewRun
                send
                (fun () -> false)
                (fun () ->
                    writeVerdict repoRoot (Some runId) (Verdict.Incomplete "fixture")
                    1)

        test <@ exitCode = 1 @>

        match readSummaries repoRoot with
        | [ { Result = SummaryResult.Compared counts } ] ->
            test <@ counts.TestPrune = TestPruneSummary.Invalid reason @>
            // The other plugins are still compared.
            test <@ counts.Analyzers.Equal = 1 @>
        | other -> failwith $"expected one compared summary, got %A{other}")
