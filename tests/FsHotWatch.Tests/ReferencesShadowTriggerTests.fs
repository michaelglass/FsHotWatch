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
                        SelectionsReply.toJson
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
            | TestPruneSummary.NotCompared why -> failwith why
        | other -> failwith $"expected one compared summary, got %A{other}"

        test <@ asked |> Seq.contains LintCommand @>)

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
        compare "land-box" "land-box" send nothingBefore rows runId DateTimeOffset.UnixEpoch

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
