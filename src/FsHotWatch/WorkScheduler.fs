/// Bounded, fair admission of work shared by several sessions.
///
/// A `Scheduler` has a fixed number of slots. Each session takes its own `Lane` and
/// acquires a slot through it before doing bounded work. While slots are free an
/// acquisition is granted at once. Once they are all taken, waiters queue on their
/// lane, and each freed slot is handed directly to the head of the next waiting lane
/// in turn: a lane with fifty queued jobs and a lane with one are served alternately,
/// so the one waits behind at most one of the fifty, not all of them. Within a lane,
/// jobs are served in arrival order.
///
/// Each lane counts what it was admitted (`Lane.Status`), so `status --repository` can
/// say which session is waiting, and for how long (AUTOMATION-682).
module FsHotWatch.WorkScheduler

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

/// A scheduler's slots now: how many there are, how many are held, and how many
/// acquisitions wait for one.
type SchedulerStatus =
    { Capacity: int
      Running: int
      Queued: int }

/// What one lane was admitted. `Granted` and `Abandoned` count acquisitions since the
/// lane was made: granted a slot, or given up while waiting for one. `Waited` sums,
/// and `LongestWait` is the most of, the time a granted acquisition spent queued. `Held`
/// is slot-time: the time each of the lane's slots was held, summed.
type LaneStatus =
    { Lane: string
      Queued: int
      Running: int
      Granted: int64
      Abandoned: int64
      Waited: TimeSpan
      LongestWait: TimeSpan
      Held: TimeSpan }

let private wholeMs (t: TimeSpan) = int64 t.TotalMilliseconds

module SchedulerStatus =
    /// The `scheduler` object of `status --repository`.
    let payload (status: SchedulerStatus) : JsonObject =
        let node = JsonObject()
        node["capacity"] <- JsonValue.Create status.Capacity
        node["running"] <- JsonValue.Create status.Running
        node["queued"] <- JsonValue.Create status.Queued
        node

module LaneStatus =
    /// A session's `admission` object of `status --repository`, times in whole ms.
    let payload (status: LaneStatus) : JsonObject =
        let node = JsonObject()
        node["queued"] <- JsonValue.Create status.Queued
        node["running"] <- JsonValue.Create status.Running
        node["granted"] <- JsonValue.Create status.Granted
        node["abandoned"] <- JsonValue.Create status.Abandoned
        node["waitedMs"] <- JsonValue.Create(wholeMs status.Waited)
        node["longestWaitMs"] <- JsonValue.Create(wholeMs status.LongestWait)
        node["heldMs"] <- JsonValue.Create(wholeMs status.Held)
        node

/// One queued acquisition: what wakes it, and when it queued.
[<NoComparison; NoEquality>]
type internal Waiter =
    { Grant: TaskCompletionSource<bool>
      Since: TimeSpan }

/// A fixed number of slots, handed out across lanes in turn. `handingOver` runs on the
/// releasing thread each time a freed slot passes to a waiter: after the waiter has
/// left the queue and before it is woken. `clock` is the monotonic time waits and holds
/// are measured by.
[<Sealed>]
type Scheduler internal (capacity: int, handingOver: unit -> unit, clock: unit -> TimeSpan) =
    do
        if capacity < 1 then
            raise (ArgumentOutOfRangeException(nameof capacity, capacity, "a scheduler needs at least one slot"))

    let gate = Lock()
    let mutable running = 0
    let mutable queued = 0
    /// The lanes with at least one waiter, in the order they are next served.
    let turns = LinkedList<Lane>()

    /// A scheduler of `capacity` slots.
    new(capacity: int) = Scheduler(capacity, ignore, (fun () -> Stopwatch.GetElapsedTime 0L))

    internal new(capacity: int, handingOver: unit -> unit) =
        Scheduler(capacity, handingOver, (fun () -> Stopwatch.GetElapsedTime 0L))

    /// How many slots there are.
    member _.Capacity: int = capacity

    /// How many slots are held now.
    member _.Running: int = Locking.locked gate (fun () -> running)

    /// How many acquisitions are waiting for a slot now.
    member _.Queued: int = Locking.locked gate (fun () -> queued)

    /// The slots now, read together.
    member _.Status() : SchedulerStatus =
        Locking.locked gate (fun () ->
            { Capacity = capacity
              Running = running
              Queued = queued })

    /// A new lane: one session's place in the turns.
    member this.Lane(name: string) : Lane = Lane(this, name)

    /// `read` under the lock, given the time now.
    member internal _.Read(read: TimeSpan -> 'T) : 'T =
        Locking.locked gate (fun () -> read (clock ()))

    member internal _.Acquire(lane: Lane, ct: CancellationToken) : Async<bool> =
        if ct.IsCancellationRequested then
            async.Return false
        else
            let waiting =
                Locking.locked gate (fun () ->
                    let now = clock ()

                    if running < capacity then
                        running <- running + 1
                        lane.Take(now, TimeSpan.Zero)
                        None
                    else
                        let waiter =
                            { Grant = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                              Since = now }

                        let node = lane.Waiters.AddLast waiter
                        queued <- queued + 1

                        if lane.Waiters.Count = 1 then
                            turns.AddLast lane |> ignore

                        Some node)

            match waiting with
            | None -> async.Return true
            | Some node ->
                let grant = node.Value.Grant

                // Gives up the place in the queue, unless a slot was handed over first.
                let leave () =
                    Locking.locked gate (fun () ->
                        if not (isNull node.List) then
                            lane.Waiters.Remove node
                            queued <- queued - 1
                            lane.Abandon()

                            if lane.Waiters.Count = 0 then
                                turns.Remove lane |> ignore

                            grant.TrySetResult false |> ignore)

                async {
                    use _ = ct.Register(Action leave)
                    return! Async.AwaitTask grant.Task
                }

    member internal _.Release(releasing: Lane) : unit =
        let next =
            Locking.locked gate (fun () ->
                if running = 0 then
                    invalidOp "released a slot that nobody holds"

                let now = clock ()

                if not (releasing.Give now) then
                    invalidOp $"lane %s{releasing.Name} released a slot it does not hold"

                match turns.First with
                | null ->
                    running <- running - 1
                    None
                | first ->
                    let lane = first.Value
                    turns.RemoveFirst()
                    let waiter = lane.Waiters.First.Value
                    lane.Waiters.RemoveFirst()
                    queued <- queued - 1
                    lane.Take(now, now - waiter.Since)

                    // A lane with more waiting goes to the back of the turns.
                    if lane.Waiters.Count > 0 then
                        turns.AddLast lane |> ignore

                    Some waiter.Grant)

        // The slot passes straight to the waiter; `running` is unchanged.
        next
        |> Option.iter (fun grant ->
            handingOver ()
            grant.TrySetResult true |> ignore)

/// One session's place in a scheduler's turns, and its admission counts. Everything
/// mutable here is guarded by the scheduler's lock.
and [<Sealed>] Lane internal (scheduler: Scheduler, name: string) =
    let mutable held = 0
    let mutable granted = 0L
    let mutable abandoned = 0L
    let mutable waited = TimeSpan.Zero
    let mutable longestWait = TimeSpan.Zero
    /// Slot-time up to `heldSince`; the slots held now add `held × (now - heldSince)`.
    let mutable heldTime = TimeSpan.Zero
    let mutable heldSince = TimeSpan.Zero

    let heldUpTo (now: TimeSpan) =
        heldTime + TimeSpan.FromTicks(int64 held * (now - heldSince).Ticks)

    let accrue (now: TimeSpan) =
        heldTime <- heldUpTo now
        heldSince <- now

    /// Waiters on this lane, oldest first. Guarded by the scheduler.
    member val internal Waiters: LinkedList<Waiter> = LinkedList<Waiter>()

    /// A slot granted to this lane at `now`, after waiting `wait`.
    member internal _.Take(now: TimeSpan, wait: TimeSpan) =
        accrue now
        held <- held + 1
        granted <- granted + 1L
        waited <- waited + wait
        longestWait <- max longestWait wait

    /// One of this lane's slots given back at `now`: false, and nothing changes, when
    /// the lane holds none.
    member internal _.Give(now: TimeSpan) : bool =
        if held = 0 then
            false
        else
            accrue now
            held <- held - 1
            true

    /// One of this lane's waiters gave up.
    member internal _.Abandon() = abandoned <- abandoned + 1L

    /// What this lane is called in logs and tests.
    member _.Name: string = name

    /// The scheduler whose slots this lane takes.
    member _.Scheduler: Scheduler = scheduler

    /// Wait for a slot without holding a thread: true once one is held, false when
    /// `ct` gave up first. A held slot is given back with `Release`.
    member this.Acquire(ct: CancellationToken) : Async<bool> = scheduler.Acquire(this, ct)

    /// Give back a slot this lane holds. It goes to the next lane in turn. A lane that
    /// holds none is refused: its caller released a slot another lane holds.
    member this.Release() : unit = scheduler.Release this

    /// What this lane was admitted, read now.
    member this.Status() : LaneStatus =
        scheduler.Read(fun now ->
            { Lane = name
              Queued = this.Waiters.Count
              Running = held
              Granted = granted
              Abandoned = abandoned
              Waited = waited
              LongestWait = longestWait
              Held = heldUpTo now })

/// Run `jobs` with at most `bound` (at least one) of them started and unfinished at
/// once, results in the order of `jobs`.
///
/// Unbounded, a fan-out over every file of a tier or batch starts every job at once,
/// each holding its closure and a place in the scheduler's queue, so what it holds
/// grows with the files. Bounded by the slots, the jobs not yet admitted stay unstarted
/// descriptions.
let parallelWithin (bound: int) (jobs: Async<'T> seq) : Async<'T array> =
    Async.Parallel(jobs, maxDegreeOfParallelism = max 1 bound)
