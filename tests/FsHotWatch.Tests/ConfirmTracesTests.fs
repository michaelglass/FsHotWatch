/// `confirm`'s check that the run its verdict graded recorded the traces `tests.traces`
/// asks for: silent when it did or when none were asked for, a named warning otherwise.
module FsHotWatch.Tests.ConfirmTracesTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli
open FsHotWatch.TestPrune
open TestPrune.Trace

let private runId = "0123456789abcdef0123456789abcdef"

let private row (id: string) status reason : TraceStore.TraceRun =
    { RunId = id
      TestProject = "T"
      TreeHash = "t"
      EnvFingerprint = ""
      RecordedAt = DateTimeOffset.UtcNow
      Kind = TraceStore.FullRun
      Status = status
      Reason = reason
      StatsJson = "{}" }

let private settings record : TraceSettings =
    { Record = record
      DbPath = ".fshw/test-traces.db"
      WeaveTests = WeaveTestSites
      FingerprintInputs = []
      FingerprintEnv = []
      VerifyTimeoutSec = 300 }

[<Fact>]
let ``a graded run that recorded a project owes nothing`` () =
    let rows =
        [ "A", [ row runId TraceStore.Recorded "" ]
          "B", [ row runId TraceStore.Refused "no-ctrf-report" ] ]

    test <@ ConfirmTraces.unrecordedWarning RecordFullRuns runId rows = None @>

[<Fact>]
let ``a graded run that stored nothing is named as that`` () =
    // The shape the full-suite scope bug left: rows from an earlier run, none from this one.
    let rows = [ "A", [ row "an-earlier-run" TraceStore.Recorded "" ] ]

    let warning = ConfirmTraces.unrecordedWarning RecordFullRuns runId rows

    test <@ warning.IsSome @>

    test
        <@
            warning.Value.Contains runId
            && warning.Value.Contains "stored no trace and no reason"
        @>

[<Fact>]
let ``a graded run whose every project was refused names each reason`` () =
    let rows =
        [ "A", [ row runId TraceStore.Refused "not-recorded: full-runs" ]
          "B", [ row runId TraceStore.FailedToRecord "recorder-no-output" ] ]

    let warning = (ConfirmTraces.unrecordedWarning RecordEveryRun runId rows).Value

    test
        <@
            warning.Contains "A: not-recorded: full-runs"
            && warning.Contains "B: recorder-no-output"
        @>

[<Fact>]
let ``nothing is owed when recording is off or no project takes part`` () =
    test <@ ConfirmTraces.unrecordedWarning RecordOff runId [ "A", [] ] = None @>
    test <@ ConfirmTraces.unrecordedWarning RecordFullRuns runId [] = None @>

[<Fact>]
let ``check reads a missing trace database as a run that stored nothing`` () =
    let root = Directory.CreateTempSubdirectory("fshw-confirm-traces-").FullName

    try
        let warning =
            ConfirmTraces.check root (Some(settings RecordFullRuns)) [ "A" ] (Some(Guid.Parse runId))

        test <@ warning.IsSome && warning.Value.Contains "stored no trace" @>
        test <@ not (File.Exists(Path.Combine(root, ".fshw", "test-traces.db"))) @>
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``check reads the graded run's rows from the trace database`` () =
    let root = Directory.CreateTempSubdirectory("fshw-confirm-traces-").FullName
    let dbPath = Path.Combine(root, ".fshw", "test-traces.db")
    Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore

    try
        do
            let store = TraceStore.Store.Open dbPath

            try
                store.RecordRunWithoutTraces(
                    { row runId TraceStore.Refused "not-recorded: full-runs" with
                        TestProject = "A" }
                )
            finally
                (store :> IDisposable).Dispose()

        let warning =
            ConfirmTraces.check root (Some(settings RecordFullRuns)) [ "A" ] (Some(Guid.Parse runId))

        test <@ warning.IsSome && warning.Value.Contains "A: not-recorded: full-runs" @>
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        Directory.Delete(root, true)

[<Fact>]
let ``check owes nothing without traces configured or without a graded run`` () =
    test <@ ConfirmTraces.check "/nowhere" None [ "A" ] (Some(Guid.Parse runId)) = None @>
    test <@ ConfirmTraces.check "/nowhere" (Some(settings RecordFullRuns)) [ "A" ] None = None @>
