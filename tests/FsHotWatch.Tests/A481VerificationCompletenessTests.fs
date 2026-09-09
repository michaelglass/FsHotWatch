module FsHotWatch.Tests.A481VerificationCompletenessTests

open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.RunOnceOutput
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Tests.TestHelpers

// Uses the same real settle/convergence/publish transport as IpcOutputTests'
// "daemon command retains executed evidence across a same-tree quiet convergence read".
// The failure stays RED: this separate field states whether verification finished.
let private coverageFailure gap =
    let uncheckedField =
        match gap with
        | "fcs-unknown" -> ""
        | "fcs-unchecked" -> ",\"unchecked\":2"
        | _ -> ",\"unchecked\":0"

    let deferredEntry =
        match gap with
        | "build-wait" ->
            """"Tests.fs":[{"plugin":"test-prune","message":"build artifact was not produced","severity":"deferred","line":0,"column":0}]"""
        | "runner-abort" ->
            """"Tests.fs":[{"plugin":"test-prune","message":"test host was killed","severity":"aborted","line":0,"column":0}]"""
        | _ -> ""

    sprintf
        """{"count":0,"files":{%s},"statuses":{"coverage-count-gate":{"status":{"tag":"failed","error":"coverage floor fell","at":"2026-09-09T12:00:00Z"},"subtasks":[],"activityTail":[],"lastRun":{"startedAt":"2026-09-09T11:59:59Z","elapsedMs":1000,"outcome":{"tag":"failed","error":"coverage floor fell"},"summary":"coverage floor fell","activityTail":[],"replayed":false}}}%s}"""
        deferredEntry
        uncheckedField

[<Theory(Timeout = 15000)>]
[<InlineData("complete", false)>]
[<InlineData("complete", true)>]
[<InlineData("fcs-unknown", false)>]
[<InlineData("fcs-unknown", true)>]
[<InlineData("fcs-unchecked", false)>]
[<InlineData("fcs-unchecked", true)>]
[<InlineData("build-wait", false)>]
[<InlineData("build-wait", true)>]
[<InlineData("runner-abort", false)>]
[<InlineData("runner-abort", true)>]
[<InlineData("missing-baseline", false)>]
[<InlineData("missing-baseline", true)>]
[<InlineData("unknown-scope", false)>]
[<InlineData("unknown-scope", true)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``coverage failure preserves independent completeness from the final transport reading`` (gap: string) reread =
    withTempDir "a481-verification-completeness" (fun repoRoot ->
        let mutable errorReads = 0
        let mutable rescans = 0

        let getErrors () =
            errorReads <- errorReads + 1

            if reread && errorReads = 1 then
                // Incomplete but clean causes the actual convergence loop to reread.
                """{"count":0,"files":{},"statuses":{},"unchecked":1}"""
            else
                coverageFailure gap

        let getTestRun () =
            let report = BaselineFixtures.reportOf (FullSuite 1)
            // The initial clean-but-incomplete snapshot has valid scope/baseline;
            // only the final failure snapshot carries the selected independent gap.
            if reread && errorReads < 2 then
                report
            else
                match gap with
                | "missing-baseline" -> { report with Baseline = BaselineReading.NotReported }
                | "unknown-scope" -> { report with Scope = ScopeUnreadable "scope reply was unavailable" }
                | _ -> report

        let exitCode =
            pollAndRender
                ProgressRenderer.Agent
                CheckVerdict.InnerLoop
                repoRoot
                []
                (fun _ -> [])
                false
                (fun () -> "idle")
                (fun () -> "idle")
                (fun () -> "{}")
                getErrors
                getTestRun
                (fun () -> ReachUnavailable "this test does not offer an impact projection")
                ignore
                (fun () ->
                    rescans <- rescans + 1
                    "idle")

        test <@ errorReads = (if reread then 2 else 1) @>
        test <@ rescans = (if reread then 1 else 0) @>
        test <@ exitCode = 1 @>

        match Verdict.read repoRoot with
        | Verdict.Reading.Found verdict ->
            test <@ verdict.Outcome = Verdict.Red @>
            test <@ verdict.ExitCode = exitCode @>
            let plugin = Assert.Single verdict.Plugins
            test <@ plugin.Name = "coverage-count-gate" @>
            test <@ plugin.Outcome = Verdict.PluginOutcome.Fail @>
        | other -> failwithf "expected unchanged readable RED verdict, got %A" other

        use document = JsonDocument.Parse(File.ReadAllText(Verdict.path repoRoot))
        let evidence = document.RootElement.GetProperty("verificationCompleteness")
        let kind = evidence.GetProperty("kind").GetString()

        if gap = "complete" then
            test <@ kind = "complete" @>
        else
            test <@ kind = "incomplete" || kind = "not-recorded" @>

            if kind = "incomplete" then
                let reason = evidence.GetProperty("reason").GetString()
                test <@ not (System.String.IsNullOrWhiteSpace reason) @>)

let private publishCompleteFailure root settled =
    let response = coverageFailure "complete" |> parseDiagnosticsResponse
    let report = BaselineFixtures.reportOf (FullSuite 1)
    let completeness =
        checkInputs false report response
        |> CheckVerdict.VerificationCompleteness.ofInputs CheckVerdict.InnerLoop

    publishVerdict
        root
        []
        CheckVerdict.InnerLoop
        completeness
        false
        report
        Verdict.NoReading
        response.Statuses
        []
        settled
        CheckVerdict.CheckOutcome.FailuresFound

[<Theory(Timeout = 15000)>]
[<InlineData("")>]
[<InlineData("null")>]
[<InlineData("false")>]
[<InlineData("[]")>]
[<InlineData("{\"kind\":\"future\"}")>]
[<InlineData("{\"kind\":\"incomplete\"}")>]
[<InlineData("{\"kind\":\"incomplete\",\"reason\":\" \"}")>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``legacy and malformed completeness never rehydrate as complete`` replacement =
    withTempDir "a481-completeness-legacy" (fun root ->
        let code = publishCompleteFailure root (SettledTree.capture root [])
        test <@ code = 1 @>
        let path = Verdict.path root
        let document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText path).AsObject()

        if replacement = "" then
            document.Remove("verificationCompleteness") |> ignore
        else
            document["verificationCompleteness"] <- System.Text.Json.Nodes.JsonNode.Parse replacement

        File.WriteAllText(path, document.ToJsonString())

        match Verdict.read root with
        | Verdict.Reading.Found verdict ->
            test <@ verdict.Outcome = Verdict.Red @>
            test <@ verdict.VerificationCompleteness = CheckVerdict.VerificationCompleteness.NotRecorded @>
        | other -> failwithf "legacy metadata should remain readable without inventing completeness: %A" other)

[<Theory(Timeout = 15000)>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Issue", "AUTOMATION-481")>]
let ``completeness survives readback only while its settled tree holds`` moveTree =
    withTempDir "a481-completeness-tree" (fun root ->
        let source = Path.Combine(root, "src")
        Directory.CreateDirectory source |> ignore
        let tracked = Path.Combine(source, "Tracked.fs")
        File.WriteAllText(tracked, "module Tracked\nlet value = 1\n")
        let settled = SettledTree.capture root []

        if moveTree then
            File.WriteAllText(tracked, "module Tracked\nlet value = 2\n")

        let code = publishCompleteFailure root settled

        match Verdict.read root with
        | Verdict.Reading.Found verdict ->
            test <@ verdict.ExitCode = code @>

            if moveTree then
                test <@ code = 2 @>
                test <@ verdict.VerificationCompleteness = CheckVerdict.VerificationCompleteness.NotRecorded @>
                match verdict.Outcome with
                | Verdict.Incomplete _ -> ()
                | other -> failwithf "a changed tree must invalidate the claim: %A" other
            else
                test <@ code = 1 @>
                test <@ verdict.Outcome = Verdict.Red @>
                test <@ verdict.VerificationCompleteness = CheckVerdict.VerificationCompleteness.Complete @>
        | other -> failwithf "expected readable publication: %A" other)
