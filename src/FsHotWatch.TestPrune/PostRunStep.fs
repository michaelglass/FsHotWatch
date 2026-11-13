/// One log line where each step of a run's completion starts and one where it ends
/// (AUTOMATION-1660). Between the last test host's exit and "Tests complete" a run did
/// work that logged nothing — 35.6 s of it in one measured edit check — so where that time
/// went could only be bounded by file mtimes. Every step there now says when it began,
/// when it ended and how long it took, under one run's id.
module FsHotWatch.TestPrune.PostRunStep

open System.Diagnostics
open FsHotWatch

// A literal, not a module value: a module value is a static initializer, which one coverage
// run counted as executed and another did not.
[<Literal>]
let private Tag = "test-prune"

let private startLine (runId: string) (step: string) = $"post-run %s{runId} %s{step}: start"

let private elapsedMs (startedAt: int64) =
    int64 (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds)

let private endLine (runId: string) (step: string) (startedAt: int64) =
    $"post-run %s{runId} %s{step}: end after %d{elapsedMs startedAt}ms"

let private failedLine (runId: string) (step: string) (startedAt: int64) (ex: exn) =
    $"post-run %s{runId} %s{step}: failed after %d{elapsedMs startedAt}ms (%s{ex.GetType().Name}: %s{ex.Message})"

/// Run `work` as the step `step` of run `runId`, logging its start, and its end (or its
/// failure, which is re-raised unchanged) with how long it took.
let timed (runId: string) (step: string) (work: unit -> 'a) : 'a =
    Logging.info Tag (startLine runId step)
    let startedAt = Stopwatch.GetTimestamp()

    let result =
        try
            work ()
        with ex ->
            Logging.warn Tag (failedLine runId step startedAt ex)
            reraise ()

    Logging.info Tag (endLine runId step startedAt)
    result

/// `timed` for a step that is itself asynchronous.
let timedAsync (runId: string) (step: string) (work: unit -> Async<'a>) : Async<'a> =
    async {
        Logging.info Tag (startLine runId step)
        let startedAt = Stopwatch.GetTimestamp()
        let! outcome = work () |> Async.Catch

        match outcome with
        | Choice1Of2 result ->
            Logging.info Tag (endLine runId step startedAt)
            return result
        | Choice2Of2 ex ->
            Logging.warn Tag (failedLine runId step startedAt ex)
            return raise ex
    }
