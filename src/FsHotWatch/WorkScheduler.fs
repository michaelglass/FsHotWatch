/// Bounded, fair admission of work shared by several sessions.
///
/// A `Scheduler` has a fixed number of slots. Each session takes its own `Lane` and
/// acquires a slot through it before doing bounded work. While slots are free an
/// acquisition is granted at once. Once they are all taken, waiters queue on their
/// lane, and each freed slot is handed directly to the head of the next waiting lane
/// in turn: a lane with fifty queued jobs and a lane with one are served alternately,
/// so the one waits behind at most one of the fifty, not all of them. Within a lane,
/// jobs are served in arrival order.
module FsHotWatch.WorkScheduler

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// A fixed number of slots, handed out across lanes in turn. `handingOver` runs on the
/// releasing thread each time a freed slot passes to a waiter: after the waiter has
/// left the queue and before it is woken.
[<Sealed>]
type Scheduler internal (capacity: int, handingOver: unit -> unit) =
    do
        if capacity < 1 then
            raise (ArgumentOutOfRangeException(nameof capacity, capacity, "a scheduler needs at least one slot"))

    let gate = Lock()
    let mutable running = 0
    let mutable queued = 0
    /// The lanes with at least one waiter, in the order they are next served.
    let turns = LinkedList<Lane>()

    /// A scheduler of `capacity` slots.
    new(capacity: int) = Scheduler(capacity, ignore)

    /// How many slots there are.
    member _.Capacity: int = capacity

    /// How many slots are held now.
    member _.Running: int = Locking.locked gate (fun () -> running)

    /// How many acquisitions are waiting for a slot now.
    member _.Queued: int = Locking.locked gate (fun () -> queued)

    /// A new lane: one session's place in the turns.
    member this.Lane(name: string) : Lane = Lane(this, name)

    member internal _.Acquire(lane: Lane, ct: CancellationToken) : Async<bool> =
        if ct.IsCancellationRequested then
            async.Return false
        else
            let waiting =
                Locking.locked gate (fun () ->
                    if running < capacity then
                        running <- running + 1
                        None
                    else
                        let grant =
                            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

                        let node = lane.Waiters.AddLast grant
                        queued <- queued + 1

                        if lane.Waiters.Count = 1 then
                            turns.AddLast lane |> ignore

                        Some(grant, node))

            match waiting with
            | None -> async.Return true
            | Some(grant, node) ->
                // Gives up the place in the queue, unless a slot was handed over first.
                let leave () =
                    Locking.locked gate (fun () ->
                        if not (isNull node.List) then
                            lane.Waiters.Remove node
                            queued <- queued - 1

                            if lane.Waiters.Count = 0 then
                                turns.Remove lane |> ignore

                            grant.TrySetResult false |> ignore)

                async {
                    use _ = ct.Register(Action leave)
                    return! Async.AwaitTask grant.Task
                }

    member internal _.Release() : unit =
        let next =
            Locking.locked gate (fun () ->
                if running = 0 then
                    invalidOp "released a slot that nobody holds"

                match turns.First with
                | null ->
                    running <- running - 1
                    None
                | first ->
                    let lane = first.Value
                    turns.RemoveFirst()
                    let grant = lane.Waiters.First.Value
                    lane.Waiters.RemoveFirst()
                    queued <- queued - 1

                    // A lane with more waiting goes to the back of the turns.
                    if lane.Waiters.Count > 0 then
                        turns.AddLast lane |> ignore

                    Some grant)

        // The slot passes straight to the waiter; `running` is unchanged.
        next
        |> Option.iter (fun grant ->
            handingOver ()
            grant.TrySetResult true |> ignore)

/// One session's place in a scheduler's turns.
and [<Sealed>] Lane internal (scheduler: Scheduler, name: string) =
    /// Waiters on this lane, oldest first. Guarded by the scheduler.
    member val internal Waiters: LinkedList<TaskCompletionSource<bool>> = LinkedList<TaskCompletionSource<bool>>()

    /// What this lane is called in logs and tests.
    member _.Name: string = name

    /// The scheduler whose slots this lane takes.
    member _.Scheduler: Scheduler = scheduler

    /// Wait for a slot without holding a thread: true once one is held, false when
    /// `ct` gave up first. A held slot is given back with `Release`.
    member this.Acquire(ct: CancellationToken) : Async<bool> = scheduler.Acquire(this, ct)

    /// Give back a slot this lane's caller holds. It goes to the next lane in turn.
    member _.Release() : unit = scheduler.Release()
