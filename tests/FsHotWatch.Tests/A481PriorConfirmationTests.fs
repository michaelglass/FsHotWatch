module FsHotWatch.Tests.A481PriorConfirmationTests

open System
open System.IO
open System.Text.Json
open Xunit
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Tests.TestHelpers

let private statuses outcome replayField =
    sprintf
        """{"coverage-probe":{"status":{"tag":"completed","at":"2026-09-09T12:00:00Z"},"lastRun":{"startedAt":"2026-09-09T11:59:59Z","elapsedMs":1000,"outcome":%s,"summary":"deliberately misleading success (cached)","activityTail":[]%s}}}"""
        outcome
        replayField
    |> parseStatuses

let private publish root mode scope parsed =
    publishVerdict
        (modelEvidence [ BaselineFixtures.runId ])
        root
        []
        mode
        true
        (BaselineFixtures.reportOf scope)
        Verdict.NoReading
        parsed
        []
        (SettledTree.capture root [])
        (CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline)

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``check with already verified tests preserves prior confirmation only without a new decline`` declined =
    withTempDir "a481-prior-refusal" (fun root ->
        let runDir = Ctrf.runDir root BaselineFixtures.runId
        Directory.CreateDirectory runDir |> ignore

        let report =
            """{"reportFormat":"CTRF","specVersion":"0.0.0","reportId":"a481-policy-fixture","results":{"tool":{"name":"xUnit.net v3"},"summary":{"tests":1,"passed":1,"failed":0,"pending":0,"skipped":0,"other":0,"suites":1,"start":1,"stop":2},"tests":[{"name":"Fixture.measured","status":"passed"}]}}"""

        File.WriteAllText(Path.Combine(runDir, "Fixture" + Ctrf.ReportSuffix), report)

        Assert.Equal(
            0,
            publish
                root
                CheckVerdict.Confirmation
                (FullSuite 1)
                (statuses """{"tag":"completed"}""" ",\"replayed\":false")
        )

        match Verdict.priorConfirmation root [] with
        | Verdict.PriorConfirmation.StillApplies previous -> Assert.Single(previous.Suites) |> ignore
        | other -> failwithf "positive control must earn reusable full evidence: %A" other

        let priorBytes = File.ReadAllBytes(Verdict.path root)
        let parsed =
            if declined then
                statuses
                    """{"tag":"notEvaluated","reason":"reports cannot describe the current run"}"""
                    ",\"replayed\":false"
            else
                statuses """{"tag":"completed"}""" ",\"replayed\":false"

        let code =
            publishVerdict
                (modelEvidence [ BaselineFixtures.runId ])
                root
                []
                CheckVerdict.InnerLoop
                true
                (BaselineFixtures.reportOf (NoTestsRun NoTestsReason.AlreadyVerified))
                Verdict.NoReading
                parsed
                []
                (SettledTree.capture root [])
                (CheckVerdict.CheckOutcome.UnearnedScope(NoTestsRun NoTestsReason.AlreadyVerified))

        // A no-tests check still refuses an unearned scope. The new measurement
        // changes which evidence may remain on disk, not that invocation policy.
        Assert.Equal(3, code)
        if declined then
            Assert.Equal(Verdict.PriorConfirmation.MustEarn, Verdict.priorConfirmation root [])
            match Verdict.read root with
            | Verdict.Reading.Found current ->
                Assert.Equal(3, current.ExitCode)
                Assert.False(Verdict.isFullSuiteGreen current)
                let probe = Assert.Single current.Plugins
                match probe.Outcome with
                | Verdict.PluginOutcome.NotEvaluated reason -> Assert.Contains("current run", reason)
                | other -> failwithf "new decline must remain visible: %A" other
            | other -> failwithf "current evidence should be readable: %A" other
        else
            Assert.Equal<byte>(priorBytes, File.ReadAllBytes(Verdict.path root))
            match Verdict.priorConfirmation root [] with
            | Verdict.PriorConfirmation.StillApplies previous -> Assert.True(Verdict.isFullSuiteGreen previous)
            | other -> failwithf "unchanged evidence should preserve the earned confirmation: %A" other)
