/// Bounded, fair admission of work shared by several sessions.
///
/// A `Scheduler` has a fixed number of slots. Each session takes its own `Lane` and
/// acquires a slot through it before doing bounded work. While slots are free an
/// acquisition is granted at once. Once they are all taken, waiters queue on their
/// lane, and each freed slot is handed directly to a waiter in the next lane in turn:
/// a lane with fifty queued jobs and a lane with one are served alternately, so the one
/// waits behind at most one of the fifty, not all of them.
///
/// Each acquisition carries a `JobSpec` (AUTOMATION-682, AUTOMATION-681 slice 7). Its class
/// orders the waiters: an `Interactive` job (the checks of a save) is urgent, a `Bulk` job
/// (a scan's tiers) is not, until it has waited the aging bound. A freed slot goes to the
/// first lane in turn whose next waiter is urgent, and to the first lane in turn when none
/// is. Within a lane the oldest urgent waiter goes first, else the oldest. So a scan in one
/// session cannot keep another session's save waiting, and a run of saves cannot keep a
/// scan waiting more than the aging bound plus one turn.
///
/// Each lane counts what it was admitted, per class (`Lane.Status`), so `status
/// --repository` can say which session is waiting, and for how long.
module FsHotWatch.WorkScheduler

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

/// How a job ranks for a slot.
type JobClass =
    /// Someone is waiting on it now: the checks of a save.
    | Interactive
    /// Work in bulk that nobody waits on job by job: a scan's tiers. Urgent once it has
    /// waited the aging bound.
    | Bulk

/// What a job asks of the scheduler: its class, and its weight. A slot held for a job of
/// weight `w` charges its lane `w` times its slot-time (ADR-053), so a declared heavy job
/// is admitted proportionally less under contention. Exclusivity and dependencies are later
/// fields (AUTOMATION-682).
type JobSpec = { Class: JobClass; Weight: float }

module JobSpec =
    /// A job someone is waiting on now.
    let interactive: JobSpec = { Class = Interactive; Weight = 1.0 }

    /// Bulk work, urgent only once aged.
    let bulk: JobSpec = { Class = Bulk; Weight = 1.0 }

    /// `job`, weighing `weight`: a positive, finite number (AUTOMATION-1299, ADR-053).
    let weighted (weight: float) (job: JobSpec) : JobSpec =
        if not (weight > 0.0 && Double.IsFinite weight) then
            raise (ArgumentOutOfRangeException(nameof weight, weight, "a weight must be a positive finite number"))

        { job with Weight = weight }

/// How long a bulk job waits before it ranks as interactive.
let DefaultAgingBound = TimeSpan.FromSeconds 5.0

/// How far ahead of the least-served waiting session a session may be and still be served
/// in its turn (AUTOMATION-682 slice 5).
let FairnessQuantum = TimeSpan.FromMilliseconds 100.0

/// A scheduler's slots now: how many there are, how many are held, and how many
/// acquisitions wait for one.
type SchedulerStatus =
    { Capacity: int
      Running: int
      Queued: int }

/// What one lane admitted of one class: acquisitions granted a slot, and given up while
/// waiting for one; the total and the longest time a granted one waited.
type ClassStatus =
    { Granted: int64
      Abandoned: int64
      Waited: TimeSpan
      LongestWait: TimeSpan }

/// What one lane was admitted. `Granted` and `Abandoned` count acquisitions since the
/// lane was made: granted a slot, or given up while waiting for one. `Waited` sums,
/// and `LongestWait` is the most of, the time a granted acquisition spent queued. `Held`
/// is slot-time: the time each of the lane's slots was held, summed. `Charged` weighs
/// it: each slot's time times its job's weight, the service fairness is decided by
/// (ADR-051, ADR-053). `Interactive` and `Bulk` split the counts by class.
type LaneStatus =
    { Lane: string
      Queued: int
      Running: int
      Granted: int64
      Abandoned: int64
      Waited: TimeSpan
      LongestWait: TimeSpan
      Held: TimeSpan
      Charged: TimeSpan
      Interactive: ClassStatus
      Bulk: ClassStatus }

let private wholeMs (t: TimeSpan) = int64 t.TotalMilliseconds

module SchedulerStatus =
    /// The `scheduler` object of `status --repository`.
    let payload (status: SchedulerStatus) : JsonObject =
        let node = JsonObject()
        node["capacity"] <- JsonValue.Create status.Capacity
        node["running"] <- JsonValue.Create status.Running
        node["queued"] <- JsonValue.Create status.Queued
        node

module ClassStatus =
    let internal zero: ClassStatus =
        { Granted = 0L
          Abandoned = 0L
          Waited = TimeSpan.Zero
          LongestWait = TimeSpan.Zero }

    /// One class's counts in a session's `admission` object, times in whole ms.
    let payload (status: ClassStatus) : JsonObject =
        let node = JsonObject()
        node["granted"] <- JsonValue.Create status.Granted
        node["abandoned"] <- JsonValue.Create status.Abandoned
        node["waitedMs"] <- JsonValue.Create(wholeMs status.Waited)
        node["longestWaitMs"] <- JsonValue.Create(wholeMs status.LongestWait)
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
        node["chargedMs"] <- JsonValue.Create(wholeMs status.Charged)
        node["interactive"] <- ClassStatus.payload status.Interactive
        node["bulk"] <- ClassStatus.payload status.Bulk
        node

/// One queued acquisition: what wakes it, its class, and when it queued.
[<NoComparison; NoEquality>]
type internal Waiter =
    {
        Grant: TaskCompletionSource<bool>
        Class: JobClass
        Weight: float
        Since: TimeSpan
        /// Runs under the lock when a slot becomes this waiter's.
        OnGrant: unit -> unit
    }

/// A fixed number of slots, handed out across lanes in turn. `handingOver` runs on the
/// releasing thread each time a freed slot passes to a waiter: after the waiter has
/// left the queue and before it is woken. `clock` is the monotonic time waits and holds
/// are measured by; a bulk job that has waited `agingBound` ranks as interactive.
[<Sealed>]
type Scheduler internal (capacity: int, handingOver: unit -> unit, clock: unit -> TimeSpan, agingBound: TimeSpan) =
    do
        if capacity < 1 then
            raise (ArgumentOutOfRangeException(nameof capacity, capacity, "a scheduler needs at least one slot"))

    let gate = Lock()
    let mutable running = 0
    let mutable queued = 0
    /// The lanes with at least one waiter, in the order they are next served.
    let turns = LinkedList<Lane>()

    /// Which waiting lane a freed slot goes to (AUTOMATION-682 slices 2 and 5). The lanes
    /// whose next waiter ranks highest compete: an aged bulk job, then an interactive one,
    /// then bulk. Aged jobs are served in turn: aging is the starvation bound, and no
    /// accounting may delay it. Otherwise the slot goes to the first lane in turn whose
    /// service (slot-time held) is within `FairnessQuantum` of the least any competing lane
    /// has had, so a session with ten-times-longer jobs gets a tenth of the grants instead
    /// of ten times the slot-time, and sessions of similar cost still simply take turns.
    /// The lanes holding or waiting for a slot.
    let active = HashSet<Lane>(HashIdentity.Reference)

    /// `lane` is about to hold or wait for a slot. A lane that was idle catches up to the
    /// least service of the lanes that kept working, so it comes back level with them
    /// instead of with credit banked while it was away.
    let activate (lane: Lane) (now: TimeSpan) =
        if active.Add lane && active.Count > 1 then
            active
            |> Seq.filter (fun other -> not (obj.ReferenceEquals(other, lane)))
            |> Seq.map (fun other -> other.Service now)
            |> Seq.min
            |> fun floor -> lane.CatchUp(now, floor)

    /// `lane` went idle if it neither holds nor waits for a slot now.
    let settle (lane: Lane) =
        if not lane.Active then
            active.Remove lane |> ignore

    let choose (waiting: LinkedList<Lane>) (now: TimeSpan) : Lane =
        let lanes = List.ofSeq waiting
        let rankOf (lane: Lane) = lane.NextRank(now, agingBound)
        let best = lanes |> List.map rankOf |> List.min
        let competing = lanes |> List.filter (fun lane -> rankOf lane = best)

        if best = 0 then
            List.head competing
        else
            let least = competing |> List.map (fun lane -> lane.Service now) |> List.min
            competing |> List.find (fun lane -> lane.Service now <= least + FairnessQuantum)

    static let monotonic () = Stopwatch.GetElapsedTime 0L

    /// A scheduler of `capacity` slots.
    new(capacity: int) = Scheduler(capacity, ignore, monotonic, DefaultAgingBound)

    internal new(capacity: int, handingOver: unit -> unit) =
        Scheduler(capacity, handingOver, monotonic, DefaultAgingBound)

    internal new(capacity: int, handingOver: unit -> unit, clock: unit -> TimeSpan) =
        Scheduler(capacity, handingOver, clock, DefaultAgingBound)

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

    /// Wait for a slot for `job` on `lane`: true once one is held, false when `ct` gave up
    /// first. `onGrant` runs under the lock at the moment a slot becomes this
    /// acquisition's, so a caller can record that it holds one even if its own
    /// continuation is then cancelled (`Lane.Run`).
    ///
    /// Nothing happens until the returned async runs: an acquisition whose caller is
    /// cancelled before it starts never queues. Queueing when it was merely described left
    /// a waiter no one would remove, and the slot later handed to it was never released.
    member internal this.Acquire(lane: Lane, job: JobSpec, ct: CancellationToken, onGrant: unit -> unit) : Async<bool> =
        Async.FromContinuations(fun (granted, _, _) -> this.Admit(lane, job, ct, onGrant, granted))

    /// The body of an acquisition: grant at once, or queue and register the give-up, then
    /// answer `granted` with true (a slot is held) or false (`ct` gave up first).
    member internal _.Admit
        (lane: Lane, job: JobSpec, ct: CancellationToken, onGrant: unit -> unit, granted: bool -> unit)
        : unit =
        if ct.IsCancellationRequested then
            granted false
        else
            let waiting =
                Locking.locked gate (fun () ->
                    let now = clock ()

                    activate lane now

                    if running < capacity then
                        running <- running + 1
                        lane.Take(now, job.Class, job.Weight, TimeSpan.Zero)
                        onGrant ()
                        None
                    else
                        let waiter =
                            { Grant = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                              Class = job.Class
                              Weight = job.Weight
                              Since = now
                              OnGrant = onGrant }

                        let node = lane.Enqueue waiter
                        queued <- queued + 1

                        if lane.Waiting = 1 then
                            turns.AddLast lane |> ignore

                        Some node)

            match waiting with
            | None -> granted true
            | Some node ->
                let waiter = node.Value

                // Gives up the place in the queue, unless a slot was handed over first.
                let leave () =
                    Locking.locked gate (fun () ->
                        if not (isNull node.List) then
                            node.List.Remove node
                            queued <- queued - 1
                            lane.Abandon waiter.Class
                            settle lane

                            if lane.Waiting = 0 then
                                turns.Remove lane |> ignore

                            waiter.Grant.TrySetResult false |> ignore)

                let registration = ct.Register(Action leave)

                waiter.Grant.Task.ContinueWith(fun (answer: Task<bool>) ->
                    registration.Dispose()
                    granted answer.Result)
                |> ignore

    member internal _.Release(releasing: Lane, weight: float option) : unit =
        let next =
            Locking.locked gate (fun () ->
                if running = 0 then
                    invalidOp "released a slot that nobody holds"

                let now = clock ()

                if not (releasing.Give(now, weight)) then
                    invalidOp $"lane %s{releasing.Name} released a slot it does not hold"

                settle releasing

                match turns.First with
                | null ->
                    running <- running - 1
                    None
                | first ->
                    let lane = choose first.List now
                    turns.Remove lane |> ignore
                    let next = lane.Next(now, agingBound)
                    next.List.Remove next
                    queued <- queued - 1
                    lane.Take(now, next.Value.Class, next.Value.Weight, now - next.Value.Since)
                    next.Value.OnGrant()

                    // A lane with more waiting goes to the back of the turns.
                    if lane.Waiting > 0 then
                        turns.AddLast lane |> ignore

                    Some next.Value.Grant)

        // The slot passes straight to the waiter; `running` is unchanged.
        next
        |> Option.iter (fun grant ->
            handingOver ()
            grant.TrySetResult true |> ignore)

/// One session's place in a scheduler's turns, and its admission counts. Everything
/// mutable here is guarded by the scheduler's lock.
and [<Sealed>] Lane internal (scheduler: Scheduler, name: string) =
    let interactiveWaiters = LinkedList<Waiter>()
    let bulkWaiters = LinkedList<Waiter>()
    let mutable held = 0
    /// The weight of each slot held now, in the order they were granted.
    let heldWeights = ResizeArray<float>()
    let mutable interactive = ClassStatus.zero
    let mutable bulk = ClassStatus.zero
    /// Slot-time up to `heldSince`; the slots held now add `held × (now - heldSince)`.
    let mutable heldTime = TimeSpan.Zero
    let mutable heldSince = TimeSpan.Zero
    /// Weighted slot-time up to `heldSince`: each slot's time times its job's weight.
    let mutable chargedTime = TimeSpan.Zero
    /// Added to the slot-time held to give this lane's service; raised by `CatchUp`.
    let mutable serviceOffset = TimeSpan.Zero

    let heldUpTo (now: TimeSpan) =
        heldTime + TimeSpan.FromTicks(int64 held * (now - heldSince).Ticks)

    let chargedUpTo (now: TimeSpan) =
        chargedTime
        + TimeSpan.FromTicks(int64 (Seq.sum heldWeights * float (now - heldSince).Ticks))

    let accrue (now: TimeSpan) =
        heldTime <- heldUpTo now
        chargedTime <- chargedUpTo now
        heldSince <- now

    let update (cls: JobClass) (change: ClassStatus -> ClassStatus) =
        match cls with
        | Interactive -> interactive <- change interactive
        | Bulk -> bulk <- change bulk

    /// The oldest bulk waiter, when it has waited the aging bound.
    let agedBulk (now: TimeSpan) (agingBound: TimeSpan) =
        match bulkWaiters.First with
        | null -> None
        | b when now - b.Value.Since >= agingBound -> Some b
        | _ -> None

    /// How many waiters this lane has.
    member internal _.Waiting: int = interactiveWaiters.Count + bulkWaiters.Count

    /// Queue `waiter` under its class.
    member internal _.Enqueue(waiter: Waiter) : LinkedListNode<Waiter> =
        match waiter.Class with
        | Interactive -> interactiveWaiters.AddLast waiter
        | Bulk -> bulkWaiters.AddLast waiter

    /// How the waiter this lane would serve next ranks: 0 for a bulk job aged past the
    /// bound, 1 for an interactive job, 2 for bulk. Only asked of a lane with a waiter.
    member internal _.NextRank(now: TimeSpan, agingBound: TimeSpan) : int =
        if (agedBulk now agingBound).IsSome then 0
        elif interactiveWaiters.Count > 0 then 1
        else 2

    /// The slot-time this lane has been charged: what it has held, each slot weighed by its
    /// job's declared weight (AUTOMATION-1299, ADR-053), plus any catch-up it was given on
    /// waking. This is the one place a slot is charged.
    member internal _.Service(now: TimeSpan) : TimeSpan = chargedUpTo now + serviceOffset

    /// Raise this lane's service to at least `floor`, as it wakes.
    member internal this.CatchUp(now: TimeSpan, floor: TimeSpan) =
        let behind = floor - this.Service now

        if behind > TimeSpan.Zero then
            serviceOffset <- serviceOffset + behind

    /// Whether this lane holds or waits for a slot.
    member internal this.Active: bool = held > 0 || this.Waiting > 0

    /// The waiter this lane serves next: the oldest urgent one, else the oldest. Only
    /// asked of a lane with a waiter.
    member internal _.Next(now: TimeSpan, agingBound: TimeSpan) : LinkedListNode<Waiter> =
        match interactiveWaiters.First, agedBulk now agingBound with
        | null, Some aged -> aged
        | null, None -> bulkWaiters.First
        | first, Some aged when aged.Value.Since < first.Value.Since -> aged
        | first, _ -> first

    /// A slot granted to this lane at `now` for a job of `cls` weighing `weight`, after
    /// waiting `wait`.
    member internal _.Take(now: TimeSpan, cls: JobClass, weight: float, wait: TimeSpan) =
        accrue now
        held <- held + 1
        heldWeights.Add weight

        update cls (fun c ->
            { c with
                Granted = c.Granted + 1L
                Waited = c.Waited + wait
                LongestWait = max c.LongestWait wait })

    /// One of this lane's slots given back at `now`, held for a job of `weight`: false, and
    /// nothing changes, when the lane holds no such slot. With no weight named (an
    /// unweighted `Release`), a weight-1 slot is given back if the lane holds one, else the
    /// oldest it holds.
    member internal _.Give(now: TimeSpan, weight: float option) : bool =
        let index =
            match weight with
            | Some w -> heldWeights.IndexOf w
            | None when heldWeights.Count = 0 -> -1
            | None -> max 0 (heldWeights.IndexOf 1.0)

        if index < 0 then
            false
        else
            accrue now
            held <- held - 1
            heldWeights.RemoveAt index
            true

    /// One of this lane's waiters of `cls` gave up.
    member internal _.Abandon(cls: JobClass) =
        update cls (fun c -> { c with Abandoned = c.Abandoned + 1L })

    /// What this lane is called in logs and tests.
    member _.Name: string = name

    /// The scheduler whose slots this lane takes.
    member _.Scheduler: Scheduler = scheduler

    /// Wait for a slot for an interactive job, without holding a thread: true once one
    /// is held, false when `ct` gave up first. A held slot is given back with `Release`.
    member this.Acquire(ct: CancellationToken) : Async<bool> =
        scheduler.Acquire(this, JobSpec.interactive, ct, ignore)

    /// Wait for a slot for `job`, without holding a thread: true once one is held, false
    /// when `ct` gave up first. A held slot is given back with `Release`.
    member this.Acquire(job: JobSpec, ct: CancellationToken) : Async<bool> =
        scheduler.Acquire(this, job, ct, ignore)

    /// `Acquire`, with `onGrant` run under the scheduler's lock at the moment a slot becomes
    /// this acquisition's, so a caller can record that it holds one even if its own
    /// continuation is then cancelled.
    member internal this.Acquire(job: JobSpec, ct: CancellationToken, onGrant: unit -> unit) : Async<bool> =
        scheduler.Acquire(this, job, ct, onGrant)

    /// Give back a slot this lane holds. It goes to the next lane in turn. A lane that
    /// holds none is refused: its caller released a slot another lane holds.
    member this.Release() : unit = scheduler.Release(this, None)

    /// Give back a slot this lane holds for `job`, charged at its weight until now. A lane
    /// that holds no slot of that weight is refused.
    member this.Release(job: JobSpec) : unit =
        scheduler.Release(this, Some job.Weight)

    /// Run `work` holding one slot for `job`, and give the slot back however `work` ends:
    /// it returns, it throws, or the caller is cancelled, including in the moment between
    /// the slot being granted and the work starting. Cancelled before a slot is granted,
    /// the work never starts.
    member this.Run(job: JobSpec, work: Async<'T>) : Async<'T> =
        async {
            // 1 once a slot is this run's. Set under the scheduler's lock at the grant, so
            // the `finally` sees it even when the continuation after the grant is cancelled.
            let held = ref 0

            try
                let! ct = Async.CancellationToken
                // Given up while waiting is a cancellation, raised by the acquisition itself.
                do!
                    Async.FromContinuations(fun (admitted, _, cancelled) ->
                        scheduler.Admit(
                            this,
                            job,
                            ct,
                            (fun () -> held.Value <- 1),
                            fun granted ->
                                if granted then
                                    admitted ()
                                else
                                    cancelled (OperationCanceledException ct)
                        ))

                return! work
            finally
                if Interlocked.Exchange(&held.contents, 0) = 1 then
                    this.Release job
        }

    /// What this lane was admitted, read now.
    member this.Status() : LaneStatus =
        scheduler.Read(fun now ->
            { Lane = name
              Queued = this.Waiting
              Running = held
              Granted = interactive.Granted + bulk.Granted
              Abandoned = interactive.Abandoned + bulk.Abandoned
              Waited = interactive.Waited + bulk.Waited
              LongestWait = max interactive.LongestWait bulk.LongestWait
              Held = heldUpTo now
              Charged = chargedUpTo now
              Interactive = interactive
              Bulk = bulk })

/// Run `jobs` with at most `bound` (at least one) of them started and unfinished at
/// once, results in the order of `jobs`.
///
/// Unbounded, a fan-out over every file of a tier or batch starts every job at once,
/// each holding its closure and a place in the scheduler's queue, so what it holds
/// grows with the files. Bounded by the slots, the jobs not yet admitted stay unstarted
/// descriptions.
let parallelWithin (bound: int) (jobs: Async<'T> seq) : Async<'T array> =
    Async.Parallel(jobs, maxDegreeOfParallelism = max 1 bound)
