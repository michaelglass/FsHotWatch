module TestPrune.AuditSink

open TestPrune.Domain

/// Type alias for the audit event sink.
type AuditSink = MailboxProcessor<Timestamped<AnalysisEvent>>

/// Create an audit sink that persists events using the given function.
let createAuditSink (persist: Timestamped<AnalysisEvent> -> Async<unit>) : AuditSink =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () =
            async {
                let! event = inbox.Receive()
                do! persist event
                return! loop ()
            }

        loop ())

/// Create a no-op audit sink that discards all events.
let createNoopSink () : AuditSink =
    createAuditSink (fun _ -> async { return () })

/// Wrap an event with the current timestamp.
let timestamp (event: AnalysisEvent) : Timestamped<AnalysisEvent> =
    { Timestamp = System.DateTimeOffset.UtcNow
      Event = event }
