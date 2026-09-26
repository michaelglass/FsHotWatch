/// Long-lived threads for work that runs under a caller-held deadline.
///
/// A timed call (`ProcessHelper.runWithCancellableTimeout`) must run its work somewhere
/// other than the caller's thread, so that the caller can abandon work that ignores its
/// cancellation token when the deadline passes. A finished worker waits for the next job
/// instead of exiting, so a scan's thousands of timed calls reuse a few threads.
///
/// The set is elastic, never capped: a job that finds no idle worker gets a new one.
/// A cap would make an abandoned job that never unwinds hold a slot, and enough of them
/// would queue every later job behind its own deadline. So threads created are bounded
/// by the peak number of jobs in flight at once (abandoned ones included), not by the
/// number of jobs run. Idle workers are kept rather than retired, since a retirement is
/// the thread exit this module exists to avoid.
module FsHotWatch.DeadlineWorkers

open System.Collections.Generic
open System.Threading

/// The name every worker thread carries.
[<Literal>]
let ThreadName = "fshw-deadline-worker"

/// A job: it runs on a worker and returns the action that publishes its
/// outcome. The worker counts itself idle BEFORE publishing, so a caller that wakes on
/// the outcome and immediately submits again finds that worker instead of creating one.
type internal Job = unit -> (unit -> unit)

/// The elastic worker set. `Created` counts threads started over the pool's life.
type internal Pool() =
    let gate = obj ()
    let pending = Queue<Job>()
    // Workers waiting for a job, or about to: counted from the moment a worker has
    // finished a job's work, before it publishes that job's outcome.
    let mutable idle = 0
    let mutable created = 0

    let work (first: Job) =
        let mutable next = first

        while true do
            let publish =
                try
                    next ()
                with _ ->
                    ignore

            lock gate (fun () -> idle <- idle + 1)

            try
                publish ()
            with _ ->
                ()

            next <-
                lock gate (fun () ->
                    while pending.Count = 0 do
                        Monitor.Wait gate |> ignore

                    idle <- idle - 1
                    pending.Dequeue())

    member _.Created: int = lock gate (fun () -> created)

    /// Run `job` on an idle worker, or on a new one when none is free.
    member _.Post(job: Job) : unit =
        let spawn =
            lock gate (fun () ->
                if idle > pending.Count then
                    pending.Enqueue job
                    Monitor.Pulse gate
                    false
                else
                    created <- created + 1
                    true)

        if spawn then
            Thread((fun () -> work job), IsBackground = true, Name = ThreadName).Start()

/// The daemon-wide pool behind `ProcessHelper.runWithCancellableTimeout`.
let internal shared = Pool()
