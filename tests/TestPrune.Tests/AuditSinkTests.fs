module TestPrune.Tests.AuditSinkTests

open System
open System.Threading
open Xunit
open Swensen.Unquote
open TestPrune.Domain
open TestPrune.AuditSink

module ``AuditSink basics`` =

    [<Fact>]
    let ``posted events are persisted in order`` () =
        let received = System.Collections.Generic.List<Timestamped<AnalysisEvent>>()
        let gate = new ManualResetEventSlim(false)

        let persist event =
            async {
                received.Add(event)

                if received.Count = 2 then
                    gate.Set()
            }

        let sink = createAuditSink persist

        let event1 =
            { Timestamp = DateTimeOffset.UtcNow
              Event = IndexStartedEvent 5 }

        let event2 =
            { Timestamp = DateTimeOffset.UtcNow
              Event = IndexCompletedEvent(100, 50, 10) }

        sink.Post(event1)
        sink.Post(event2)

        test <@ gate.Wait(TimeSpan.FromSeconds(5.0)) @>
        test <@ received.Count = 2 @>
        test <@ received[0].Event = IndexStartedEvent 5 @>
        test <@ received[1].Event = IndexCompletedEvent(100, 50, 10) @>

module ``noopSink`` =

    [<Fact>]
    let ``noop sink does not throw`` () =
        let sink = createNoopSink ()

        sink.Post(
            { Timestamp = DateTimeOffset.UtcNow
              Event = IndexStartedEvent 1 }
        )
        // Allow time for processing
        Thread.Sleep(100)

module ``timestamp helper`` =

    [<Fact>]
    let ``timestamp wraps event with current time`` () =
        let before = DateTimeOffset.UtcNow
        let result = timestamp (IndexStartedEvent 3)
        let after = DateTimeOffset.UtcNow
        test <@ result.Event = IndexStartedEvent 3 @>
        test <@ result.Timestamp >= before @>
        test <@ result.Timestamp <= after @>

module ``SQLite persistence`` =
    open TestPrune.Tests.TestHelpers

    [<Fact>]
    let ``events are persisted to database`` () =
        withDb (fun db ->
            let runId = "test-run-1"
            let sink = createSqliteSink db.InsertEvent runId

            sink.Post(timestamp (IndexStartedEvent 5))
            sink.Post(timestamp (IndexCompletedEvent(100, 50, 10)))

            // Wait for MailboxProcessor to process
            Thread.Sleep(500)

            let events = db.GetEvents(runId)
            test <@ events.Length = 2 @>
            let (_, type1, _) = events[0]
            let (_, type2, _) = events[1]
            test <@ type1 = "IndexStarted" @>
            test <@ type2 = "IndexCompleted" @>)

    [<Fact>]
    let ``events are isolated by run ID`` () =
        withDb (fun db ->
            let sink1 = createSqliteSink db.InsertEvent "run-a"
            let sink2 = createSqliteSink db.InsertEvent "run-b"

            sink1.Post(timestamp (IndexStartedEvent 1))
            sink2.Post(timestamp (IndexStartedEvent 2))
            sink2.Post(timestamp (IndexCompletedEvent(10, 5, 3)))

            Thread.Sleep(500)

            let eventsA = db.GetEvents("run-a")
            let eventsB = db.GetEvents("run-b")
            test <@ eventsA.Length = 1 @>
            test <@ eventsB.Length = 2 @>)

    [<Fact>]
    let ``ClearEvents removes events for a run`` () =
        withDb (fun db ->
            let sink = createSqliteSink db.InsertEvent "run-clear"

            sink.Post(timestamp (IndexStartedEvent 3))
            Thread.Sleep(500)

            test <@ db.GetEvents("run-clear").Length = 1 @>

            db.ClearEvents("run-clear")

            test <@ db.GetEvents("run-clear").Length = 0 @>)
