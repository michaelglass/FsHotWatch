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

let private serializeEvent (event: AnalysisEvent) : string * string =
    match event with
    | FileAnalyzedEvent(file, symbols, deps, tests) -> "FileAnalyzed", $"%s{file}|%d{symbols}|%d{deps}|%d{tests}"
    | FileCacheHitEvent(file, reason) -> "FileCacheHit", $"%s{file}|%s{reason}"
    | FileSkippedEvent(file, reason) -> "FileSkipped", $"%s{file}|%s{reason}"
    | ProjectCacheHitEvent project -> "ProjectCacheHit", project
    | ProjectIndexedEvent(project, fileCount) -> "ProjectIndexed", $"%s{project}|%d{fileCount}"
    | SymbolChangeDetectedEvent(file, name, change) -> "SymbolChangeDetected", $"%s{file}|%s{name}|%A{change}"
    | TestSelectedEvent(testMethod, reason) -> "TestSelected", $"%s{testMethod}|%s{SelectionReason.describe reason}"
    | DiffParsedEvent files -> "DiffParsed", (files |> String.concat "|")
    | IndexStartedEvent count -> "IndexStarted", $"%d{count}"
    | IndexCompletedEvent(symbols, deps, tests) -> "IndexCompleted", $"%d{symbols}|%d{deps}|%d{tests}"
    | ErrorEvent error -> "Error", AnalysisError.describe error
    | DeadCodeFoundEvent names -> "DeadCodeFound", (names |> String.concat "|")

/// Create an audit sink that persists events to SQLite via the given insert function.
/// The insertEvent function takes (runId, timestamp, eventType, eventData).
let createSqliteSink (insertEvent: string * string * string * string -> unit) (runId: string) : AuditSink =
    createAuditSink (fun event ->
        async {
            let ts = event.Timestamp.ToString("o")
            let eventType, eventData = serializeEvent event.Event
            insertEvent (runId, ts, eventType, eventData)
        })

/// Wrap an event with the current timestamp.
let timestamp (event: AnalysisEvent) : Timestamped<AnalysisEvent> =
    { Timestamp = System.DateTimeOffset.UtcNow
      Event = event }
