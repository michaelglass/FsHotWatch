/// A deterministic runtime for the host's async work (AUTOMATION-1695).
///
/// Everything a simulated component would hand to the thread pool, and every continuation
/// a `task {}` posts back, runs on ONE thread, the one that created the `Sim`, in an order
/// drawn from the seed:
///
/// - `Runtime.Dispatch` either runs its work inside the call or queues it, by the seed. A
///   `let!` on what that work settles therefore finds it already complete, or suspends,
///   because the seed said so, never because a pool thread won a race.
/// - While installed, the simulation is this thread's `SynchronizationContext`, so a task
///   continuation captured here is posted into the same queue.
/// - The pump runs one ready item at a time, choosing which by the seed.
/// - Time is virtual: it advances only when nothing is ready, to the next armed timer.
/// - `Runtime.Wait` pumps until its task settles. A wait that nothing left to run can
///   settle is a DEADLOCK, raised at once instead of hanging the test.
///
/// Work that reaches the simulation from any other thread is an ESCAPE: a timing source the
/// component did not route through its `Runtime`. It is recorded, never silently run.
module FsHotWatch.Tests.SimRuntime

open System
open System.Threading
open System.Threading.Tasks
open FsHotWatch

/// What the pump ran.
[<RequireQualifiedAccess>]
type Ran =
    /// Work queued by `Runtime.Dispatch`.
    | Dispatched
    /// A continuation posted to the simulation's synchronization context.
    | Posted
    /// A task started, or a continuation attached, on the runtime's scheduler.
    | Scheduled
    /// A virtual timer's callback.
    | Timer

/// A choice the simulation made, in the order it made them.
[<RequireQualifiedAccess>]
type Choice =
    /// A dispatch ran its work inside the call.
    | Inline
    /// A dispatch queued its work.
    | Queued
    /// The pump ran ready item `index` of `count`.
    | Ran of index: int * count: int
    /// Nothing was ready, so virtual time advanced to the next timer.
    | Advanced of now: TimeSpan

/// What no history may do, whatever the interleaving.
[<RequireQualifiedAccess>]
type Fault =
    /// A wait that nothing left to run could settle.
    | Deadlock of what: string
    /// Work reached the simulation from a thread other than its own.
    | Escape of what: string
    /// Work kept arriving past the step bound of one run to quiescence.
    | Unquiet of steps: int

/// Raised by a wait that can never settle, so the history ends there with its fault.
exception SimDeadlockException of string

[<NoComparison; NoEquality>]
type private Item =
    | DispatchedWork of (unit -> unit)
    | PostedWork of SendOrPostCallback * obj
    | ScheduledTask of (unit -> unit)
    | FiredTimer of (unit -> unit)

/// A virtual timer: armed until it fires or is disposed.
[<NoComparison; NoEquality>]
type private VirtualTimer =
    { Due: TimeSpan
      Order: int64
      Expire: unit -> unit
      mutable Armed: bool }

type private Context(post: SendOrPostCallback -> obj -> unit) =
    inherit SynchronizationContext()
    override _.Post(callback, state) = post callback state
    override _.Send(callback, state) = callback.Invoke state
    override this.CreateCopy() = this :> SynchronizationContext

/// The tasks a component starts and the continuations it attaches with a scheduler.
type private SimScheduler(queue: (unit -> unit) -> unit) as this =
    inherit TaskScheduler()
    let run (task: Task) = this.Run task |> ignore
    override _.QueueTask(task) = queue (fun () -> run task)
    // Never inline: every scheduled task goes through the queue, so the seed orders it.
    override _.TryExecuteTaskInline(_, _) = false
    override _.GetScheduledTasks() = Seq.empty
    member _.Run(task: Task) = base.TryExecuteTask task

/// The bound on one run to quiescence: far beyond what any history here schedules.
[<Literal>]
let QuietBound = 100_000

/// One simulated world. `inlineShare` is the chance that a dispatch runs its work inside the
/// call rather than queueing it.
///
/// Time is virtual: it advances only when nothing is ready, straight to the next armed timer,
/// and a fired timer's callback is a ready item like any other.
type internal Sim(seed: int, inlineShare: float) =
    let rng = Random seed
    let owner = Environment.CurrentManagedThreadId
    let gate = Lock()
    let ready = Collections.Generic.List<Item>()
    let timers = Collections.Generic.List<VirtualTimer>()
    let choices = Collections.Generic.List<Choice>()
    let faults = Collections.Generic.List<Fault>()
    let mutable now = TimeSpan.Zero
    let mutable armedSoFar = 0L

    let fault (f: Fault) =
        Locking.locked gate (fun () -> faults.Add f)

    // Work from another thread is recorded and dropped: running it here would hide the
    // escape behind a deterministic-looking result.
    let fromOwner (what: string) =
        if Environment.CurrentManagedThreadId = owner then
            true
        else
            fault (Fault.Escape what)
            false

    let enqueue (item: Item) =
        Locking.locked gate (fun () -> ready.Add item)

    let context =
        Context(fun callback state ->
            if fromOwner "a continuation posted from another thread" then
                enqueue (PostedWork(callback, state)))

    let scheduler =
        SimScheduler(fun run ->
            if fromOwner "a task scheduled from another thread" then
                enqueue (ScheduledTask run))

    let step () : Ran option =
        let picked =
            Locking.locked gate (fun () ->
                if ready.Count = 0 then
                    None
                else
                    let index = rng.Next ready.Count
                    choices.Add(Choice.Ran(index, ready.Count))
                    let item = ready[index]
                    ready.RemoveAt index
                    Some item)

        match picked with
        | None -> None
        | Some(DispatchedWork work) ->
            work ()
            Some Ran.Dispatched
        | Some(PostedWork(callback, state)) ->
            callback.Invoke state
            Some Ran.Posted
        | Some(ScheduledTask run) ->
            run ()
            Some Ran.Scheduled
        | Some(FiredTimer expire) ->
            expire ()
            Some Ran.Timer

    // Advance to the next armed timer due no later than `limit`, and make every timer due by
    // then ready. False when none is due by `limit`.
    let advance (limit: TimeSpan option) =
        Locking.locked gate (fun () ->
            timers.RemoveAll(fun t -> not t.Armed) |> ignore

            if timers.Count = 0 then
                false
            else
                let next = timers |> Seq.minBy (fun t -> t.Due, t.Order)

                match limit with
                | Some limit when next.Due > limit -> false
                | Some _
                | None ->
                    now <- max now next.Due
                    choices.Add(Choice.Advanced now)

                    let due =
                        timers
                        |> Seq.filter (fun t -> t.Due <= now)
                        |> Seq.sortBy (fun t -> t.Due, t.Order)
                        |> List.ofSeq

                    for t in due do
                        t.Armed <- false
                        timers.Remove t |> ignore
                        ready.Add(FiredTimer t.Expire)

                    true)

    let schedule (delay: TimeSpan) (expire: unit -> unit) : IDisposable =
        if fromOwner "a timer armed from another thread" then
            let timer =
                Locking.locked gate (fun () ->
                    armedSoFar <- armedSoFar + 1L

                    let t =
                        { Due = now + delay
                          Order = armedSoFar
                          Expire = expire
                          Armed = true }

                    timers.Add t
                    t)

            { new IDisposable with
                member _.Dispose() =
                    Locking.locked gate (fun () -> timer.Armed <- false) }
        else
            { new IDisposable with
                member _.Dispose() = () }

    let dispatch (work: unit -> unit) =
        if fromOwner "work dispatched from another thread" then
            if rng.NextDouble() < inlineShare then
                Locking.locked gate (fun () -> choices.Add Choice.Inline)
                work ()
            else
                Locking.locked gate (fun () -> choices.Add Choice.Queued)
                enqueue (DispatchedWork work)

    // Pump until `task` settles. With nothing ready, time advances to the next timer due
    // within `bound` (any timer, unbounded); a bounded wait with none due times out, its bound
    // passing; an unbounded one with none at all is a deadlock.
    let wait (bound: TimeSpan) (task: Task) =
        let limit =
            if bound = Timeout.InfiniteTimeSpan then
                None
            else
                Some(now + bound)

        let mutable progressed = true

        while not task.IsCompleted && progressed do
            progressed <- (step ()).IsSome || advance limit

        if task.IsCompleted then
            true
        else
            match limit with
            | None ->
                let what = "an unbounded wait on a task nothing left to run can settle"
                fault (Fault.Deadlock what)
                raise (SimDeadlockException what)
            | Some limit ->
                Locking.locked gate (fun () -> now <- max now limit)
                false

    // `Task.Delay` on the virtual clock: a zero delay is already complete, as there.
    let delay (span: TimeSpan) : Task =
        if span <= TimeSpan.Zero then
            Task.CompletedTask
        else
            let elapsed =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            schedule span (fun () -> elapsed.TrySetResult() |> ignore) |> ignore
            elapsed.Task

    let cancel (source: CancellationTokenSource) : Task =
        let cancelled =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        dispatch (fun () ->
            try
                source.Cancel()
                cancelled.SetResult()
            with failure ->
                cancelled.SetException failure)

        cancelled.Task

    // Started as a task on the simulation's scheduler, so the scheduler is current while
    // `work` runs: an `Async.AwaitTask` inside it continues from the queue, not the pool.
    let runSynchronously (work: Async<unit>) =
        let running =
            Task.Factory
                .StartNew(
                    (fun () -> Async.StartImmediateAsTask work),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    scheduler
                )
                .Unwrap()

        wait Timeout.InfiniteTimeSpan running |> ignore
        running.GetAwaiter().GetResult()

    let runtime: AsyncRuntime.Runtime =
        { Dispatch = dispatch
          Wait = wait
          Scheduler = scheduler
          Schedule = schedule
          Delay = delay
          Cancel = cancel
          RunSynchronously = runSynchronously }

    new(seed: int) = Sim(seed, 0.5)

    member _.Seed = seed

    /// The capability a simulated component takes in place of `AsyncRuntime.pool`.
    member _.Runtime = runtime

    /// Virtual time since the world began.
    member _.Now = Locking.locked gate (fun () -> now)

    /// Make the simulation this thread's synchronization context until disposed.
    member _.Install() : IDisposable =
        let previous = SynchronizationContext.Current
        SynchronizationContext.SetSynchronizationContext context

        { new IDisposable with
            member _.Dispose() =
                SynchronizationContext.SetSynchronizationContext previous }

    /// Run one ready item, chosen by the seed: what it was, or `None` when nothing was ready.
    member _.Step() : Ran option = step ()

    /// Advance virtual time to the next armed timer and make it ready. False when none is armed.
    member _.AdvanceToNextTimer() : bool = advance None

    /// Run until nothing is ready and no timer is armed, at most `QuietBound` items.
    member _.RunUntilQuiet() =
        let mutable steps = 0
        let mutable running = true

        while running do
            if steps >= QuietBound then
                fault (Fault.Unquiet steps)
                running <- false
            else
                match step () with
                | Some _ -> steps <- steps + 1
                | None -> running <- advance None

    /// Queue `work` as a ready item, never inline: a harness's own source of later completions
    /// (a scripted stream's data arriving after the read that asked for it).
    member _.Later(work: unit -> unit) =
        if fromOwner "work queued from another thread" then
            enqueue (DispatchedWork work)

    /// Record an ESCAPE unless this runs on the simulation's thread: a harness callback calls it,
    /// so work a component sent to the real pool behind the runtime's back is still named.
    member _.CheckOwner(what: string) = fromOwner what |> ignore

    member _.ReadyCount = Locking.locked gate (fun () -> ready.Count)

    /// How many choices the simulation has made: a dispatch that made none did not dispatch.
    member _.ChoiceCount = Locking.locked gate (fun () -> choices.Count)

    member _.Choices: Choice list = Locking.locked gate (fun () -> List.ofSeq choices)

    member _.Faults: Fault list = Locking.locked gate (fun () -> List.ofSeq faults)
