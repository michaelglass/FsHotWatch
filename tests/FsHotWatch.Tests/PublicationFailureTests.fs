module FsHotWatch.Tests.PublicationFailureTests

open System.IO
open Xunit
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Cli.IpcOutput
open FsHotWatch.Tests.TestHelpers

[<Theory(Timeout = 15000)>]
[<InlineData("failed", 1)>]
[<InlineData("incomplete", 2)>]
[<InlineData("clean", 3)>]
let ``publication failure preserves established nonzero outcome beside declined evidence`` outcomeName expected =
    withTempDir "publication-failure" (fun root ->
        // A directory at the target forces the real atomic publisher's I/O path.
        Directory.CreateDirectory(Verdict.path root) |> ignore
        let parsed =
            parseStatuses
                """{"coverage-probe":{"status":{"tag":"completed","at":"2026-09-09T12:00:00Z"},"lastRun":{"startedAt":"2026-09-09T11:59:59Z","elapsedMs":1000,"outcome":{"tag":"notEvaluated","reason":"coverage unavailable"},"summary":"coverage unavailable","activityTail":[],"replayed":false}}}"""

        let parsed =
            if outcomeName = "failed" then
                let failed =
                    parseStatuses
                        """{"build":{"status":{"tag":"failed","at":"2026-09-09T12:00:00Z","error":"compiler failed"},"lastRun":{"startedAt":"2026-09-09T11:59:59Z","elapsedMs":1000,"outcome":{"tag":"failed","error":"compiler failed"},"summary":"compiler failed","activityTail":[],"replayed":false}}}"""
                Map.fold (fun combined name status -> Map.add name status combined) parsed failed
            else
                parsed

        let outcome =
            match outcomeName with
            | "failed" -> CheckVerdict.CheckOutcome.FailuresFound
            | "incomplete" -> CheckVerdict.CheckOutcome.Incomplete 1
            | "clean" -> CheckVerdict.CheckOutcome.Clean BaselineFixtures.baseline
            | other -> failwithf "unknown test outcome %s" other

        let code =
            publishVerdict
                root
                []
                CheckVerdict.InnerLoop
                CheckVerdict.VerificationCompleteness.NotRecorded
                false
                (BaselineFixtures.reportOf (FullSuite 1))
                Verdict.NoReading
                parsed
                []
                (SettledTree.capture root [])
                outcome

        Assert.Equal(expected, code)
        Assert.True(Directory.Exists(Verdict.path root))
        Assert.False(File.Exists(Verdict.path root)))
