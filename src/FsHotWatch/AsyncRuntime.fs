/// The host's timing sources, as ONE capability (AUTOMATION-1695).
///
/// Whether a `let!` in a task finds its awaited task already complete or suspends, and
/// whether a test observes work before or after it is delivered, is decided by whichever
/// thread runs first. Code that reaches the thread pool, a timer or a blocking wait directly
/// leaves that to the pool, so a test of it, and a coverage floor over it, flakes. Code that
/// does all of them through a `Runtime` leaves it to the runtime: `pool` in production, and
/// the deterministic simulation of the tests, which decides every interleaving from a seed.
module internal FsHotWatch.AsyncRuntime

open System
open System.Threading
open System.Threading.Tasks

[<NoComparison; NoEquality>]
type Runtime =
    {
        /// Run `work` later, off the caller's stack: a thread-pool work item.
        Dispatch: (unit -> unit) -> unit
        /// Block this thread until `task` settles or `bound` passes, whichever is first.
        /// True when it settled. Raises nothing: a failed task is settled like any other.
        Wait: TimeSpan -> Task -> bool
        /// Where tasks started with `Task.Factory.StartNew` and continuations attached with
        /// `ContinueWith` run: the thread pool's scheduler in production.
        Scheduler: TaskScheduler
        /// A one-shot timer that calls `expire` after `delay`; disposing it disarms it.
        Schedule: TimeSpan -> (unit -> unit) -> IDisposable
        /// A task that completes after `delay`: `Task.Delay`.
        Delay: TimeSpan -> Task
        /// Request cancellation of `source` without running its callbacks on this thread.
        /// The task settles once they have run, faulted with what they raised (or with
        /// `ObjectDisposedException` for a disposed source).
        Cancel: CancellationTokenSource -> Task
        /// Run `work` to completion on this thread, blocking it while `work` waits.
        RunSynchronously: Async<unit> -> unit
    }

/// The thread pool, its timers, and this thread's own wait.
let pool: Runtime =
    {
      // Not the caller's execution context: the work may serve every caller (a store's
      // writer publishes every caller's change).
      Dispatch =
        fun work ->
            ThreadPool.UnsafeQueueUserWorkItem(Action<obj>(fun _ -> work ()), null, true)
            |> ignore
      // Kept by this thread's own wait, not by a timer whose callback needs a pool thread.
      // A settled task needs no wait handle: most synchronous changes publish inline.
      Wait = fun bound task -> task.IsCompleted || (task :> IAsyncResult).AsyncWaitHandle.WaitOne bound
      Scheduler = TaskScheduler.Default
      Schedule =
        fun delay expire -> new Timer((fun _ -> expire ()), null, delay, Timeout.InfiniteTimeSpan) :> IDisposable
      Delay = fun delay -> Task.Delay delay
      Cancel = fun source -> source.CancelAsync()
      RunSynchronously = fun work -> Async.RunSynchronously work }
