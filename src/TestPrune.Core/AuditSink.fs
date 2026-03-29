module TestPrune.AuditSink

open TestPrune.Domain

/// Audit event sink — either an active MailboxProcessor or a no-op that discards events.
type AuditSink =
    | ActiveSink of MailboxProcessor<Timestamped<AnalysisEvent>>
    | NoopSink

    member this.Post(event) =
        match this with
        | ActiveSink mbp -> mbp.Post(event)
        | NoopSink -> ()

/// Create an audit sink that persists events using the given function.
let createAuditSink (persist: Timestamped<AnalysisEvent> -> Async<unit>) : AuditSink =
    ActiveSink(
        MailboxProcessor.Start(fun inbox ->
            let rec loop () =
                async {
                    let! event = inbox.Receive()
                    do! persist event
                    return! loop ()
                }

            loop ())
    )

/// Create a no-op audit sink that discards all events without starting a thread.
let createNoopSink () : AuditSink = NoopSink

/// Wrap an event with the current timestamp.
let timestamp (event: AnalysisEvent) : Timestamped<AnalysisEvent> =
    { Timestamp = System.DateTimeOffset.UtcNow
      Event = event }
