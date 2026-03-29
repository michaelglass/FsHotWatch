module TestPrune.AuditSink

open TestPrune.Domain

/// Audit event sink that receives timestamped analysis events.
type AuditSink internal (post: Timestamped<AnalysisEvent> -> unit, flush: unit -> unit) =
    member _.Post(event) = post event
    /// Wait until all previously posted events have been processed.
    member _.Flush() = flush ()

type private SinkMessage =
    | Event of Timestamped<AnalysisEvent>
    | Flush of AsyncReplyChannel<unit>

/// Create an audit sink that persists events using the given function.
let createAuditSink (persist: Timestamped<AnalysisEvent> -> Async<unit>) : AuditSink =
    let mbp =
        MailboxProcessor.Start(fun inbox ->
            let rec loop () =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Event event -> do! persist event
                    | Flush reply -> reply.Reply()

                    return! loop ()
                }

            loop ())

    AuditSink((fun event -> mbp.Post(Event event)), (fun () -> mbp.PostAndAsyncReply(Flush) |> Async.RunSynchronously))

/// Create a no-op audit sink that discards all events without starting a thread.
let createNoopSink () : AuditSink = AuditSink(ignore, ignore)

let private serializeEvent (event: AnalysisEvent) : string * string =
    match event with
    | FileAnalyzedEvent(file, symbols, deps, tests) -> "FileAnalyzed", $"%s{file}|%d{symbols}|%d{deps}|%d{tests}"
    | FileCacheHitEvent(file, reason) -> "FileCacheHit", $"%s{file}|%s{reason}"
    | FileSkippedEvent(file, reason) -> "FileSkipped", $"%s{file}|%s{reason}"
    | ProjectCacheHitEvent project -> "ProjectCacheHit", project
    | ProjectIndexedEvent(project, fileCount) -> "ProjectIndexed", $"%s{project}|%d{fileCount}"
    | SymbolChangeDetectedEvent(file, name, change) ->
        let kind =
            match change with
            | Modified -> "Modified"
            | Added -> "Added"
            | Removed -> "Removed"

        "SymbolChangeDetected", $"%s{file}|%s{name}|%s{kind}"
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
            try
                let ts = event.Timestamp.ToString("o")
                let eventType, eventData = serializeEvent event.Event
                insertEvent (runId, ts, eventType, eventData)
            with ex ->
                eprintfn $"AuditSink: failed to persist event: %s{ex.Message}"
        })

/// Wrap an event with the current timestamp.
let timestamp (event: AnalysisEvent) : Timestamped<AnalysisEvent> =
    { Timestamp = System.DateTimeOffset.UtcNow
      Event = event }
