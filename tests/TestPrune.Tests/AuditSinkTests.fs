module TestPrune.Tests.AuditSinkTests

open System
open Xunit
open Swensen.Unquote
open TestPrune.Domain
open TestPrune.AuditSink

module ``AuditSink basics`` =

    [<Fact>]
    let ``posted events are persisted in order`` () =
        let received = System.Collections.Generic.List<Timestamped<AnalysisEvent>>()

        let persist event = async { received.Add(event) }

        let sink = createAuditSink persist

        let event1 =
            { Timestamp = DateTimeOffset.UtcNow
              Event = IndexStartedEvent 5 }

        let event2 =
            { Timestamp = DateTimeOffset.UtcNow
              Event = IndexCompletedEvent(100, 50, 10) }

        sink.Post(event1)
        sink.Post(event2)
        sink.Flush()

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
// NoopSink.Post is synchronous no-op, nothing to wait for

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

            sink.Flush()

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

            sink1.Flush()
            sink2.Flush()

            let eventsA = db.GetEvents("run-a")
            let eventsB = db.GetEvents("run-b")
            test <@ eventsA.Length = 1 @>
            test <@ eventsB.Length = 2 @>)

    [<Fact>]
    let ``ClearEvents removes events for a run`` () =
        withDb (fun db ->
            let sink = createSqliteSink db.InsertEvent "run-clear"

            sink.Post(timestamp (IndexStartedEvent 3))
            sink.Flush()

            test <@ db.GetEvents("run-clear").Length = 1 @>

            db.ClearEvents("run-clear")

            test <@ db.GetEvents("run-clear").Length = 0 @>)

    [<Fact>]
    let ``all event types are serialized correctly`` () =
        withDb (fun db ->
            let runId = "serialize-test"
            let sink = createSqliteSink db.InsertEvent runId

            let events =
                [ FileAnalyzedEvent("f.fs", 1, 2, 3)
                  FileCacheHitEvent("f.fs", "hash matched")
                  FileSkippedEvent("f.fs", "not found")
                  ProjectCacheHitEvent "MyProject"
                  ProjectIndexedEvent("MyProject", 5)
                  SymbolChangeDetectedEvent("f.fs", "Lib.func", Modified)
                  TestSelectedEvent("Tests.test1", SymbolChanged("Lib.func", Modified))
                  DiffParsedEvent [ "a.fs"; "b.fs" ]
                  IndexStartedEvent 3
                  IndexCompletedEvent(100, 50, 10)
                  ErrorEvent(ParseFailed("f.fs", [ "error" ]))
                  DeadCodeFoundEvent [ "Unused.func" ] ]

            for event in events do
                sink.Post(timestamp event)

            sink.Flush()

            let stored = db.GetEvents(runId)
            test <@ stored.Length = 12 @>

            let types = stored |> List.map (fun (_, t, _) -> t)
            test <@ types |> List.contains "FileAnalyzed" @>
            test <@ types |> List.contains "FileCacheHit" @>
            test <@ types |> List.contains "FileSkipped" @>
            test <@ types |> List.contains "ProjectCacheHit" @>
            test <@ types |> List.contains "ProjectIndexed" @>
            test <@ types |> List.contains "SymbolChangeDetected" @>
            test <@ types |> List.contains "TestSelected" @>
            test <@ types |> List.contains "DiffParsed" @>
            test <@ types |> List.contains "IndexStarted" @>
            test <@ types |> List.contains "IndexCompleted" @>
            test <@ types |> List.contains "Error" @>
            test <@ types |> List.contains "DeadCodeFound" @>)

    [<Fact>]
    let ``SymbolChangeDetected serializes Added change kind`` () =
        withDb (fun db ->
            let runId = "added-test"
            let sink = createSqliteSink db.InsertEvent runId

            sink.Post(timestamp (SymbolChangeDetectedEvent("f.fs", "Lib.newFunc", Added)))
            sink.Flush()

            let stored = db.GetEvents(runId)
            test <@ stored.Length = 1 @>
            let (_, eventType, data) = stored[0]
            test <@ eventType = "SymbolChangeDetected" @>
            test <@ data.Contains("Added") @>)

    [<Fact>]
    let ``SymbolChangeDetected serializes Removed change kind`` () =
        withDb (fun db ->
            let runId = "removed-test"
            let sink = createSqliteSink db.InsertEvent runId

            sink.Post(timestamp (SymbolChangeDetectedEvent("f.fs", "Lib.oldFunc", Removed)))
            sink.Flush()

            let stored = db.GetEvents(runId)
            test <@ stored.Length = 1 @>
            let (_, eventType, data) = stored[0]
            test <@ eventType = "SymbolChangeDetected" @>
            test <@ data.Contains("Removed") @>)

    [<Fact>]
    let ``createSqliteSink error handler does not crash on insert failure`` () =
        // Create a sink with an insertEvent that always throws
        let failingInsert (_runId: string, _ts: string, _eventType: string, _data: string) =
            failwith "Simulated insert failure"

        let sink = createSqliteSink failingInsert "fail-run"

        // Post an event — the error handler should catch the exception and eprintfn
        sink.Post(timestamp (IndexStartedEvent 1))
        sink.Flush()

        // If we reach here without an exception, the error handler worked.
        // Post another event to confirm the mailbox processor is still alive.
        sink.Post(timestamp (IndexCompletedEvent(1, 0, 0)))
        sink.Flush()
