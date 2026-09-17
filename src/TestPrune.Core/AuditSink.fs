module TestPrune.AuditSink

open System.Threading.Tasks
open TestPrune.Domain

/// Bound on waiting for an audit sink to confirm a flush, in milliseconds.
///
/// This is a WEDGE DETECTOR, not a perf knob. The sink's agent replies to a flush only after
/// persisting every event queued ahead of it, so a healthy flush normally returns as soon as
/// that backlog is written. It never replies when the agent is wedged (a persist that never
/// returns) or has stopped, and an unbounded wait there hangs the caller silently.
[<Literal>]
let flushTimeoutMs = 120_000

/// Outcome of waiting for an audit sink to process the events posted before a flush.
type FlushOutcome =
    /// Every event posted before the flush was processed.
    | Flushed
    /// The sink did not confirm within the bound (in milliseconds). Events posted before
    /// the flush may not have been persisted.
    | FlushTimedOut of timeoutMs: int

/// Audit event sink that receives timestamped analysis events.
type AuditSink internal (post: Timestamped<AnalysisEvent> -> unit, flushWithin: int -> FlushOutcome) =
    member _.Post(event) = post event

    /// Wait, bounded by `flushTimeoutMs`, until all previously posted events have been processed.
    /// A timeout is reported on stderr and returned as `FlushTimedOut`, never as `Flushed`.
    member _.Flush() : FlushOutcome = flushWithin flushTimeoutMs

    /// `Flush` with an explicit bound, so tests can exercise the timeout without waiting out
    /// the production window.
    member internal _.FlushWithin(timeoutMs: int) : FlushOutcome = flushWithin timeoutMs

type private SinkMessage =
    | Event of Timestamped<AnalysisEvent>
    | Flush of AsyncReplyChannel<unit>

/// Create an audit sink that persists events using the given function.
///
/// A persist that throws is reported on stderr and that event is dropped; the sink keeps
/// processing later events. An exception escaping the agent's loop would end it silently,
/// losing every later event and timing out every later flush.
let createAuditSink (persist: Timestamped<AnalysisEvent> -> Async<unit>) : AuditSink =
    let mbp =
        MailboxProcessor.Start(fun inbox ->
            let rec loop () =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Event event ->
                        try
                            do! persist event
                        with ex ->
                            eprintfn $"AuditSink: failed to persist event: %s{ex.Message}"
                    | Flush reply -> reply.Reply()

                    return! loop ()
                }

            loop ())

    let flushWithin (timeoutMs: int) =
        let confirmed = mbp.PostAndAsyncReply(Flush) |> Async.StartAsTask

        if Task.WaitAny([| (confirmed :> Task) |], timeoutMs) >= 0 then
            Flushed
        else
            eprintfn
                $"AuditSink: flush exceeded %d{timeoutMs}ms; the audit agent did not confirm (it has stopped or is wedged); events posted before the flush may not have been persisted"

            FlushTimedOut timeoutMs

    AuditSink((fun event -> mbp.Post(Event event)), flushWithin)

/// Create a no-op audit sink that discards all events without starting a thread.
let createNoopSink () : AuditSink = AuditSink(ignore, (fun _ -> Flushed))

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
    | ProjectSelectedByRuntimeCoverageEvent(project, changedFile) ->
        "ProjectSelectedByRuntimeCoverage", $"%s{project}|%s{changedFile}"
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
