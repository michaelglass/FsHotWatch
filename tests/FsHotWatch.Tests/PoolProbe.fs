/// A probe of this test host's thread pool, for AUTOMATION-1753: tests that do nothing slow
/// (one builds and reads back a JSON verdict) were cancelled at 15 s and 30 s caps under a
/// loaded CI run, and a cancelled test reports nothing of why. xUnit runs a test that has a
/// Timeout on a pool task, so a pool that other tests keep busy can hold a trivial test past its
/// cap. Every 5 s this samples the pool, and writes a line to the test output whenever work has
/// waited across two samples: a long queue with a flat completed count is a starved pool; an
/// empty queue at a cancel points at the test's own work instead. It changes nothing it observes.
module FsHotWatch.Tests.PoolProbe

open System
open System.Threading

/// The pool at one instant.
type Sample =
    { At: DateTimeOffset
      Threads: int
      Pending: int64
      Completed: int64 }

let private sample () : Sample =
    { At = DateTimeOffset.UtcNow
      Threads = ThreadPool.ThreadCount
      Pending = ThreadPool.PendingWorkItemCount
      Completed = ThreadPool.CompletedWorkItemCount }

/// The line to write for `current`, when work waited in the pool at both `previous` and
/// `current`, naming what the pool completed in between.
let report (previous: Sample) (current: Sample) : string option =
    if previous.Pending > 0L && current.Pending > 0L then
        let seconds = int (current.At - previous.At).TotalSeconds

        Some
            $"""pool-probe %s{current.At.ToString "yyyy-MM-ddTHH:mm:ss"}Z threads=%d{current.Threads} pending=%d{current.Pending} completed+%d{current.Completed - previous.Completed}/%d{seconds}s"""
    else
        None

/// The sampler, for the life of the test assembly, on a thread of its own rather than pool work: a
/// starved pool cannot delay the sample that reports it. Disposing it ends the thread.
type Sampler() =
    let stop = new ManualResetEventSlim(false)

    let rec loop (previous: Sample) =
        // The wait is the sampling interval; a set event ends the probe.
        if not (stop.Wait(TimeSpan.FromSeconds 5.0)) then
            let current = sample ()
            report previous current |> Option.iter Console.Out.WriteLine
            loop current

    let thread =
        Thread((fun () -> loop (sample ())), IsBackground = true, Name = "pool-probe")

    do thread.Start()

    interface IDisposable with
        member _.Dispose() =
            stop.Set()
            thread.Join(TimeSpan.FromSeconds 10.0) |> ignore
            stop.Dispose()

[<assembly: Xunit.AssemblyFixture(typeof<Sampler>)>]
do ()
