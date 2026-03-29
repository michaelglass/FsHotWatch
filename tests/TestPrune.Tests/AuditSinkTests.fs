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
