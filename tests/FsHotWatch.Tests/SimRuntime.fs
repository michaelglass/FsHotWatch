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

/// A choice the simulation made, in the order it made them.
[<RequireQualifiedAccess>]
type Choice =
    /// A dispatch ran its work inside the call.
    | Inline
    /// A dispatch queued its work.
    | Queued
    /// The pump ran ready item `index` of `count`.
    | Ran of index: int * count: int

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
exception SimDeadlock of string

[<NoComparison; NoEquality>]
type private Item =
    | DispatchedWork of (unit -> unit)
    | PostedWork of SendOrPostCallback * obj

type private Context(post: SendOrPostCallback -> obj -> unit) =
    inherit SynchronizationContext()
    override _.Post(callback, state) = post callback state
    override _.Send(callback, state) = callback.Invoke state
    override this.CreateCopy() = this :> SynchronizationContext

/// The bound on one run to quiescence: far beyond what any history here schedules.
[<Literal>]
let QuietBound = 100_000

/// One simulated world. `inlineShare` is the chance that a dispatch runs its work inside the
/// call rather than queueing it.
type internal Sim(seed: int, inlineShare: float) =
    let rng = Random seed
    let owner = Environment.CurrentManagedThreadId
    let gate = Lock()
    let ready = Collections.Generic.List<Item>()
    let choices = Collections.Generic.List<Choice>()
    let faults = Collections.Generic.List<Fault>()

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

    let dispatch (work: unit -> unit) =
        if fromOwner "work dispatched from another thread" then
            if rng.NextDouble() < inlineShare then
                Locking.locked gate (fun () -> choices.Add Choice.Inline)
                work ()
            else
                Locking.locked gate (fun () -> choices.Add Choice.Queued)
                enqueue (DispatchedWork work)

    let wait (bound: TimeSpan) (task: Task) =
        let mutable progressed = true

        while not task.IsCompleted && progressed do
            progressed <- (step ()).IsSome

        if task.IsCompleted then
            true
        elif bound = Timeout.InfiniteTimeSpan then
            let what = "an unbounded wait on a task nothing left to run can settle"
            fault (Fault.Deadlock what)
            raise (SimDeadlock what)
        else
            // No virtual time passes here, so a bounded wait with nothing to run times out.
            false

    let runtime: AsyncRuntime.Runtime = { Dispatch = dispatch; Wait = wait }

    new(seed: int) = Sim(seed, 0.5)

    member _.Seed = seed

    /// The capability a simulated component takes in place of `AsyncRuntime.pool`.
    member _.Runtime = runtime

    /// Make the simulation this thread's synchronization context until disposed.
    member _.Install() : IDisposable =
        let previous = SynchronizationContext.Current
        SynchronizationContext.SetSynchronizationContext context

        { new IDisposable with
            member _.Dispose() =
                SynchronizationContext.SetSynchronizationContext previous }

    /// Run one ready item, chosen by the seed: what it was, or `None` when nothing was ready.
    member _.Step() : Ran option = step ()

    /// Run ready items until none is left, at most `QuietBound`.
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
                | None -> running <- false

    member _.ReadyCount = Locking.locked gate (fun () -> ready.Count)

    /// How many choices the simulation has made: a dispatch that made none did not dispatch.
    member _.ChoiceCount = Locking.locked gate (fun () -> choices.Count)

    member _.Choices: Choice list = Locking.locked gate (fun () -> List.ofSeq choices)

    member _.Faults: Fault list = Locking.locked gate (fun () -> List.ofSeq faults)
