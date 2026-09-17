/// Work ownership for plugins and host operations.
///
/// A `Store` is the only writer. It owns one mailbox and publishes one immutable
/// `HostSnapshot` per accepted change. Readers take the published reference; no read
/// goes through the mailbox. A plugin `Owner` is a typed capability for one row of that
/// publication.
///
/// Owner transitions are pure: each takes a snapshot and returns the next snapshot, its
/// result, and the effects that follow publication (delivering a queued intent, settling
/// receipts). The store publishes; the owner runs the effects afterwards, outside the
/// writer. A receipt therefore never completes before the state it acknowledges is
/// visible, and a successor is owned before its predecessor's receipt completes.
///
/// `PluginFramework` and `PluginHost` publish through this module (ADR-029).
module internal FsHotWatch.PluginWorkOwner

open System
open System.Threading
open System.Threading.Tasks

/// A capability for one admitted piece of work. Only a store mints one, from its own
/// identity and a publication serial, so it cannot be forged and an identity from a
/// different store never matches.
[<Struct>]
type WorkId = private WorkId of store: Guid * serial: int64

[<NoComparison; NoEquality>]
type OwnerFailure =
    | UpdateFailure of exn
    | CommitFailure of exn
    | RunFailure of exn
    | OperationFailure of exn
    | ExecutorFailure of exn

    member this.Exception =
        match this with
        | UpdateFailure failure
        | CommitFailure failure
        | RunFailure failure
        | OperationFailure failure
        | ExecutorFailure failure -> failure

/// The failures an event's own fold can report. An executor failure is not one of them:
/// it retires every event at once, through `Owner.FaultExecutor`.
[<NoComparison; NoEquality>]
type EventFailure =
    | UpdateFailed of exn
    | CommitFailed of exn

/// What one row contributes to the host's rest, completion and fault answers. A row's
/// projection runs inside the same publication as its value, so the two cannot disagree.
[<NoComparison; NoEquality>]
type RowStatus =
    { Busy: bool
      Completed: int64
      Failure: OwnerFailure option }

[<NoComparison; NoEquality>]
type private Row =
    { Name: string
      Value: obj
      Status: RowStatus }

[<NoComparison; NoEquality>]
type private Operation = { Name: string; Failure: exn option }

/// A typed capability for one row. Only `Store.Register` creates one.
[<NoComparison; NoEquality>]
type RowHandle<'T> =
    private
        { StoreId: Guid
          Key: WorkId
          Project: 'T -> RowStatus }

[<NoComparison; NoEquality>]
type HostSnapshot =
    private
        { Identity: Guid
          Published: int64
          Rows: Map<WorkId, Row>
          Operations: Map<WorkId, Operation>
          SettledFailures: Map<WorkId, string * exn>
          Model: ProjectModel.Observation
          ModelFiles: (int64 * Set<Events.AbsFilePath>) option }

    /// Increases by one with every publication.
    member this.Version = this.Published

    /// The project model this publication was made under.
    member this.ProjectModel = this.Model

    /// The checkable files of the available model, paired with its generation. `None`
    /// whenever the model is not available: membership never outlives its model.
    member this.ProjectModelFiles = this.ModelFiles

    member this.IsBusy =
        not this.Operations.IsEmpty
        || (this.Rows |> Map.exists (fun _ row -> row.Status.Busy))

    member this.BusyNames =
        [ yield! this.Operations |> Map.toList |> List.map (fun (_, operation) -> operation.Name)
          yield!
              this.Rows
              |> Map.toList
              |> List.filter (fun (_, row) -> row.Status.Busy)
              |> List.map (fun (_, row) -> row.Name) ]
        |> List.distinct

    member this.CompletedEvents =
        this.Rows |> Map.toList |> List.sumBy (fun (_, row) -> row.Status.Completed)

    /// Events committed by the rows registered under `name`.
    member this.CompletedEventsOf(name: string) =
        this.Rows
        |> Map.toList
        |> List.sumBy (fun (_, row) -> if row.Name = name then row.Status.Completed else 0L)

    member private this.HostFailures =
        let live =
            this.Operations
            |> Map.toList
            |> List.choose (fun (_, operation) ->
                operation.Failure |> Option.map (fun failure -> operation.Name, failure))

        live @ (this.SettledFailures |> Map.toList |> List.map snd)

    member private this.RowFailures(select: OwnerFailure -> exn option) =
        this.Rows
        |> Map.toList
        |> List.choose (fun (_, row) ->
            row.Status.Failure
            |> Option.bind select
            |> Option.map (fun failure -> row.Name, failure))

    member this.Faults =
        this.HostFailures @ this.RowFailures(fun failure -> Some failure.Exception)

    member this.ExecutorFaults =
        this.RowFailures(fun failure ->
            match failure with
            | ExecutorFailure failure -> Some failure
            | _ -> None)

    member this.OperationFaults =
        this.HostFailures
        @ this.RowFailures(fun failure ->
            match failure with
            | OperationFailure failure -> Some failure
            | _ -> None)

    /// Read a row from this pinned publication.
    member this.Row(handle: RowHandle<'T>) : 'T =
        if handle.StoreId <> this.Identity then
            invalidOp "Row handle belongs to a different store"

        unbox<'T> (Map.find handle.Key this.Rows).Value

[<NoComparison; NoEquality>]
type private Mutation =
    | Mutation of change: (WorkId -> HostSnapshot -> HostSnapshot * obj) * reply: TaskCompletionSource<obj>

/// The single writer. Each change receives a freshly minted `WorkId` it may use for a
/// new obligation. A change that throws publishes nothing and fails only its own reply.
///
/// Changes run inside the mailbox, so they must be bounded in-memory transitions. A
/// change that waits on this store deadlocks it.
type Store() =
    let identity = Guid.NewGuid()

    let mutable published =
        { Identity = identity
          Published = 0L
          Rows = Map.empty
          Operations = Map.empty
          SettledFailures = Map.empty
          Model = ProjectModel.Observation.Unobserved
          ModelFiles = None }

    let agent =
        MailboxProcessor<Mutation>.Start(fun inbox ->
            let rec loop () =
                async {
                    let! (Mutation(change, reply)) = inbox.Receive()
                    let current = published
                    let version = current.Published + 1L

                    try
                        let next, result = change (WorkId(identity, version)) current
                        Volatile.Write(&published, { next with Published = version })
                        reply.TrySetResult(result) |> ignore
                    with failure ->
                        reply.TrySetException(failure) |> ignore

                    return! loop ()
                }

            loop ())

    let changeAsync (change: WorkId -> HostSnapshot -> HostSnapshot * 'Result) : Task<'Result> =
        let reply =
            TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)

        agent.Post(
            Mutation(
                (fun fresh snapshot ->
                    let next, result = change fresh snapshot
                    next, box result),
                reply
            )
        )

        task {
            let! result = reply.Task
            return unbox<'Result> result
        }

    // An internal transition waits for its actual publication. A caller that stopped
    // waiting could not roll the queued change back, and would lose its effects.
    let change transition =
        (changeAsync transition).GetAwaiter().GetResult()

    member _.Snapshot: HostSnapshot = Volatile.Read(&published)

    /// Publish a model observation whose checkable membership is not known.
    member _.PublishProjectModel(observation: ProjectModel.Observation) =
        change (fun _ snapshot ->
            { snapshot with
                Model = observation
                ModelFiles = None },
            ())

    /// Publish a model observation and, when it is available, its checkable files. Both
    /// change in one publication, so no reader sees the new model with the old membership.
    member _.PublishProjectModelWithFiles(observation: ProjectModel.Observation, files: Set<Events.AbsFilePath>) =
        let modelFiles =
            match observation with
            | ProjectModel.Observation.Available model -> Some(model.Generation, files)
            | ProjectModel.Observation.Unobserved
            | ProjectModel.Observation.Rediscovering _
            | ProjectModel.Observation.Unavailable _ -> None

        change (fun _ snapshot ->
            { snapshot with
                Model = observation
                ModelFiles = modelFiles },
            ())

    /// Changes posted but not yet published. A diagnostic, and a witness for tests that
    /// need to know a change is queued behind a held one.
    member _.PendingChanges = agent.CurrentQueueLength

    member _.Register<'T>(name: string, initial: 'T, project: 'T -> RowStatus) : RowHandle<'T> =
        change (fun fresh snapshot ->
            let row =
                { Name = name
                  Value = box initial
                  Status = project initial }

            { snapshot with
                Rows = Map.add fresh row snapshot.Rows },
            { StoreId = identity
              Key = fresh
              Project = project })

    member _.Read(handle: RowHandle<'T>) : 'T = Volatile.Read(&published).Row handle

    /// Resolves once the change is published, independent of any caller's wait bound.
    member _.ChangeAsync(handle: RowHandle<'T>, transition: WorkId -> 'T -> 'T * 'Result) : Task<'Result> =
        changeAsync (fun fresh snapshot ->
            let next, result = transition fresh (snapshot.Row handle)
            let row = Map.find handle.Key snapshot.Rows

            { snapshot with
                Rows =
                    Map.add
                        handle.Key
                        { row with
                            Value = box next
                            Status = handle.Project next }
                        snapshot.Rows },
            result)

    member this.Change(handle: RowHandle<'T>, transition: WorkId -> 'T -> 'T * 'Result) : 'Result =
        this.ChangeAsync(handle, transition).GetAwaiter().GetResult()

    /// Own a named host operation: a preprocessor pass, a dispatch fan-out, a scan.
    /// Starting one clears the retained failures of earlier, finished operations of the
    /// same name. A failure of an overlapping operation that is still live stays.
    member _.BeginOperation(name: string) : WorkId =
        change (fun fresh snapshot ->
            { snapshot with
                Operations = Map.add fresh { Name = name; Failure = None } snapshot.Operations
                SettledFailures = snapshot.SettledFailures |> Map.filter (fun _ (failed, _) -> failed <> name) },
            fresh)

    /// Record the first failure of a live operation. Returns false, and changes nothing,
    /// for an operation this store does not own: a late callback cannot reopen it.
    member _.FailOperation(id: WorkId, failure: exn) : bool =
        change (fun _ snapshot ->
            match Map.tryFind id snapshot.Operations with
            | Some operation ->
                { snapshot with
                    Operations =
                        Map.add
                            id
                            { operation with
                                Failure = operation.Failure |> Option.orElse (Some failure) }
                            snapshot.Operations },
                true
            | None -> snapshot, false)

    /// Retire a live operation. Its failure, if any, stays visible until the next
    /// operation of the same name begins.
    member _.EndOperation(id: WorkId) : unit =
        change (fun _ snapshot ->
            match Map.tryFind id snapshot.Operations with
            | None -> invalidOp "Host operation is foreign to this store or already ended"
            | Some operation ->
                { snapshot with
                    Operations = Map.remove id snapshot.Operations
                    SettledFailures =
                        match operation.Failure with
                        | Some failure -> Map.add id (operation.Name, failure) snapshot.SettledFailures
                        | None -> snapshot.SettledFailures },
                ())

// ---------------------------------------------------------------------------------------
// Plugin owner state
// ---------------------------------------------------------------------------------------

/// A command waiting for an exclusive key. It is owned from the moment it is accepted:
/// its receipt settles only after the fold it becomes has been published.
[<NoComparison; NoEquality>]
type Intent =
    private
        { Id: WorkId
          Coalescing: string option
          Deliver: WorkId -> unit
          Receipt: TaskCompletionSource<unit> }

/// The intents queued behind whatever holds an exclusive key, oldest first.
[<NoComparison; NoEquality>]
type ExclusiveSlot =
    | Running
    | RunningQueued of first: Intent * rest: Intent list

type EventStage =
    /// Admitted, and its update has not published a candidate.
    | Admitted
    /// Its candidate state is published; finalization has not settled it.
    | Prepared

[<NoComparison; NoEquality>]
type PendingEvent =
    private
        { Stage: EventStage
          Receipt: TaskCompletionSource<unit> option }

/// An event that folds a result into an exclusive key: a worker's result, or a delivered
/// intent. Only a worker's result can disprove that worker's earlier failure.
[<NoComparison; NoEquality>]
type Fold =
    private
        { Id: WorkId
          Event: PendingEvent
          IsWorkerResult: bool }

/// What holds an exclusive key. A fold may claim its successor worker before it commits,
/// and that worker may finish before the fold does, so a holder is a run of folds in
/// admission order followed by at most one live worker. Two workers for one key cannot be
/// represented.
[<NoComparison; NoEquality>]
type Holder =
    private
    | Worker of WorkId
    | Folding of first: Fold * later: Fold list * successor: WorkId option

[<NoComparison; NoEquality>]
type Lane =
    private
        { Holder: Holder
          Slot: ExclusiveSlot }

/// Never empty: `phaseOf` is the only constructor, and it answers `Resting` instead.
[<NoComparison; NoEquality>]
type Obligations =
    private
        { Events: Map<WorkId, PendingEvent>
          Lanes: Map<string, Lane> }

[<NoComparison; NoEquality>]
type Phase =
    | Resting
    | Working of Obligations

let private phaseOf events lanes =
    if Map.isEmpty events && Map.isEmpty lanes then
        Resting
    else
        Working { Events = events; Lanes = lanes }

let private obligations phase =
    match phase with
    | Resting -> Map.empty, Map.empty
    | Working work -> work.Events, work.Lanes

let private holdsWorker holder =
    match holder with
    | Worker _
    | Folding(_, _, Some _) -> true
    | Folding(_, _, None) -> false

let private queued slot =
    match slot with
    | Running -> []
    | RunningQueued(first, rest) -> first :: rest

let private slotOf intents =
    match intents with
    | [] -> Running
    | first :: rest -> RunningQueued(first, rest)

/// A failure and the publication that recorded it. Only work admitted after that
/// publication can disprove the failure: an older result folding late cannot.
[<NoComparison; NoEquality>]
type Recorded =
    private
        { Failure: OwnerFailure
          At: WorkId }

[<NoComparison; NoEquality>]
type Snapshot<'State> =
    private
        { Domain: 'State
          Work: Phase
          Committed: int64
          Failed: Recorded option }

    member this.State = this.Domain
    member this.Phase = this.Work
    member this.CompletedEvents = this.Committed
    member this.Failure = this.Failed |> Option.map (fun recorded -> recorded.Failure)
    member this.Fault = this.Failure |> Option.map (fun failure -> failure.Exception)

    member this.ExecutorFault =
        match this.Failure with
        | Some(ExecutorFailure failure) -> Some failure
        | _ -> None

    member this.IsBusy =
        match this.Work with
        | Resting -> false
        | Working _ -> true

    /// A live worker holds `key`. A fold of a finished run does not count.
    member this.IsRunning(key: string) =
        obligations this.Work
        |> snd
        |> Map.tryFind key
        |> Option.exists (fun lane -> holdsWorker lane.Holder)

    member this.HasExclusiveRun =
        obligations this.Work
        |> snd
        |> Map.exists (fun _ lane -> holdsWorker lane.Holder)

// ---------------------------------------------------------------------------------------
// Pure transitions
// ---------------------------------------------------------------------------------------

/// Why a capability was refused. A refused transition publishes nothing.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type private Refusal =
    /// Not owned here: another owner's work, or work already retired.
    | Foreign
    /// Owned here, but by a different kind or stage of work.
    | WrongKind
    /// The executor has failed; it admits no new work.
    | ExecutorStopped of exn

[<NoComparison; NoEquality>]
type private Settlement =
    | Succeed of TaskCompletionSource<unit>
    | Fail of TaskCompletionSource<unit> * exn

/// A transition's publication plus the effects that must follow it. At most one intent is
/// delivered per transition: only a key becoming free hands it on, and it hands it to one.
[<NoComparison; NoEquality>]
type private Step<'State, 'Result> =
    { Next: Snapshot<'State>
      Result: 'Result
      Delivery: Intent option
      Settlements: Settlement list }

let private publish next result =
    Ok
        { Next = next
          Result = result
          Delivery = None
          Settlements = [] }

/// Obligations after rewriting one site, and the intent a freed key hands on to.
type private Rewritten = Map<WorkId, PendingEvent> * Map<string, Lane> * Intent option

[<NoComparison; NoEquality>]
type private Site =
    /// An ordinary event or a fold. Rewrite with `Some` to replace it, `None` to retire it.
    | EventSite of PendingEvent * isWorkerResult: bool * rewrite: (PendingEvent option -> Rewritten)
    /// A live worker. Rewrite with `Some` to replace it by its result fold, `None` to retire it.
    | WorkerSite of rewrite: (Fold option -> Rewritten)

/// A delivered intent is its key's fold. Its receipt is the intent's own.
let private intentFold (intent: Intent) =
    { Id = intent.Id
      Event =
        { Stage = Admitted
          Receipt = Some intent.Receipt }
      IsWorkerResult = false }

/// Store a key's new holder. A key left with no holder passes to its oldest queued
/// intent, which becomes the key's fold in this same publication.
let private release key slot holder lanes =
    match holder, slot with
    | Some held, _ -> Map.add key { Holder = held; Slot = slot } lanes, None
    | None, Running -> Map.remove key lanes, None
    | None, RunningQueued(next, rest) ->
        Map.add
            key
            { Holder = Folding(intentFold next, [], None)
              Slot = slotOf rest }
            lanes,
        Some next

let private foldChain id first later successor (replacement: Fold option) =
    let chain =
        first :: later
        |> List.choose (fun fold -> if fold.Id = id then replacement else Some fold)

    match chain, successor with
    | head :: tail, _ -> Some(Folding(head, tail, successor))
    | [], Some worker -> Some(Worker worker)
    | [], None -> None

let private locate id snapshot : Site option =
    let events, lanes = obligations snapshot.Work

    match Map.tryFind id events with
    | Some pending ->
        let rewrite replacement =
            let events =
                match replacement with
                | Some next -> Map.add id next events
                | None -> Map.remove id events

            events, lanes, None

        Some(EventSite(pending, false, rewrite))
    | None ->
        lanes
        |> Map.toList
        |> List.tryPick (fun (key, lane) ->
            let rewriteHolder holder =
                let lanes, delivery = release key lane.Slot holder lanes
                events, lanes, delivery

            match lane.Holder with
            | Worker worker when worker = id ->
                Some(WorkerSite(fun fold -> rewriteHolder (fold |> Option.map (fun fold -> Folding(fold, [], None)))))
            | Worker _ -> None
            | Folding(first, later, successor) ->
                match first :: later |> List.tryFind (fun fold -> fold.Id = id) with
                | Some fold ->
                    let rewrite replacement =
                        replacement
                        |> Option.map (fun pending -> { fold with Event = pending })
                        |> foldChain id first later successor
                        |> rewriteHolder

                    Some(EventSite(fold.Event, fold.IsWorkerResult, rewrite))
                | None when successor = Some id ->
                    Some(
                        WorkerSite(fun result ->
                            rewriteHolder (Some(Folding(first, later @ Option.toList result, None))))
                    )
                | None -> None)

let private admission (snapshot: Snapshot<'State>) =
    match snapshot.ExecutorFault with
    | Some failure -> Error(Refusal.ExecutorStopped failure)
    | None -> Ok()

let private admitEvent receipt fresh snapshot =
    admission snapshot
    |> Result.bind (fun () ->
        let events, lanes = obligations snapshot.Work
        let pending = { Stage = Admitted; Receipt = receipt }

        publish
            { snapshot with
                Work = phaseOf (Map.add fresh pending events) lanes }
            fresh)

/// Claim `key` for a new worker. The key must be free, or held only by the fold named in
/// `after` with no successor yet: a result fold may launch the next run before it commits.
let private tryClaim key after fresh snapshot =
    admission snapshot
    |> Result.bind (fun () ->
        let events, lanes = obligations snapshot.Work

        let claim holder slot =
            publish
                { snapshot with
                    Work = phaseOf events (Map.add key { Holder = holder; Slot = slot } lanes) }
                (Some fresh)

        match Map.tryFind key lanes, after with
        | None, _ -> claim (Worker fresh) Running
        | Some({ Holder = Folding(fold, [], None) } as lane), Some predecessor when fold.Id = predecessor ->
            claim (Folding(fold, [], Some fresh)) lane.Slot
        | Some _, _ -> publish snapshot None)

/// Accept a command for `key`. A free key delivers it at once as the key's fold. A held key
/// queues it, or, when a queued intent has the same coalescing key, replaces that intent's
/// payload and keeps its place and receipt.
let private enqueueIntent key coalescing deliver receipt fresh snapshot =
    admission snapshot
    |> Result.bind (fun () ->
        let events, lanes = obligations snapshot.Work

        let intent =
            { Id = fresh
              Coalescing = coalescing
              Deliver = deliver
              Receipt = receipt }

        let withLane lane =
            { snapshot with
                Work = phaseOf events (Map.add key lane lanes) }

        match Map.tryFind key lanes with
        | None ->
            Ok
                { Next =
                    withLane
                        { Holder = Folding(intentFold intent, [], None)
                          Slot = Running }
                  Result = receipt.Task
                  Delivery = Some intent
                  Settlements = [] }
        | Some lane ->
            let pending = queued lane.Slot

            let existing =
                coalescing
                |> Option.bind (fun requested ->
                    pending |> List.tryFind (fun candidate -> candidate.Coalescing = Some requested))

            match existing with
            | Some prior ->
                let replaced =
                    pending
                    |> List.map (fun candidate ->
                        if candidate.Id = prior.Id then
                            { prior with Deliver = deliver }
                        else
                            candidate)

                publish (withLane { lane with Slot = slotOf replaced }) prior.Receipt.Task
            | None ->
                publish
                    (withLane
                        { lane with
                            Slot = slotOf (pending @ [ intent ]) })
                    receipt.Task)

let private rewritten snapshot (events, lanes, delivery) =
    { snapshot with
        Work = phaseOf events lanes },
    delivery

let private settledWith (receipt: TaskCompletionSource<unit> option) settlement =
    receipt |> Option.map settlement |> Option.toList

/// Success on an event clears only a failure that event disproves: one recorded before the
/// event was admitted, of a kind the event's success speaks to.
let private recovered (id: WorkId) isWorkerResult preparedCommit failed =
    match failed with
    | Some recorded when id > recorded.At ->
        match recorded.Failure with
        | UpdateFailure _ -> None
        | CommitFailure _ when preparedCommit -> None
        | RunFailure _ when isWorkerResult -> None
        | _ -> failed
    | _ -> failed

let private record failure fresh = Some { Failure = failure; At = fresh }

let private eventAt stage id snapshot =
    match locate id snapshot with
    | None -> Error Refusal.Foreign
    | Some(EventSite(pending, isWorkerResult, rewrite)) when pending.Stage = stage ->
        Ok(pending, isWorkerResult, rewrite)
    | Some _ -> Error Refusal.WrongKind

let private publishEventState id state snapshot =
    eventAt Admitted id snapshot
    |> Result.bind (fun (pending, _, rewrite) ->
        let next, _ = rewrite (Some { pending with Stage = Prepared }) |> rewritten snapshot

        publish { next with Domain = state } ())

let private completeEvent stage preparedCommit state id snapshot =
    eventAt stage id snapshot
    |> Result.map (fun (pending, isWorkerResult, rewrite) ->
        let next, delivery = rewrite None |> rewritten snapshot

        { Next =
            { next with
                Domain = defaultArg state snapshot.Domain
                Committed = snapshot.Committed + 1L
                Failed = recovered id isWorkerResult preparedCommit snapshot.Failed }
          Result = ()
          Delivery = delivery
          Settlements = settledWith pending.Receipt Succeed })

let private failEvent id failure fresh snapshot =
    match locate id snapshot with
    | None -> Error Refusal.Foreign
    | Some(WorkerSite _) -> Error Refusal.WrongKind
    | Some(EventSite(pending, _, rewrite)) ->
        let next, delivery = rewrite None |> rewritten snapshot

        let failed, cause =
            match failure, snapshot.Failed with
            | UpdateFailed cause, Some { Failure = CommitFailure _ | RunFailure _ } -> snapshot.Failed, cause
            | UpdateFailed cause, _ -> record (UpdateFailure cause) fresh, cause
            | CommitFailed cause, _ -> record (CommitFailure cause) fresh, cause

        Ok
            { Next = { next with Failed = failed }
              Result = ()
              Delivery = delivery
              Settlements = settledWith pending.Receipt (fun receipt -> Fail(receipt, cause)) }

let private workerAt id snapshot =
    match locate id snapshot with
    | None -> Error Refusal.Foreign
    | Some(EventSite _) -> Error Refusal.WrongKind
    | Some(WorkerSite rewrite) -> Ok rewrite

/// Hand a finished worker to its result fold. A failed executor cannot fold a result, so
/// the worker simply retires and the answer is `None`.
let private completeRun id fresh snapshot =
    workerAt id snapshot
    |> Result.map (fun rewrite ->
        let fold, result =
            match snapshot.ExecutorFault with
            | Some _ -> None, None
            | _ ->
                Some
                    { Id = fresh
                      Event = { Stage = Admitted; Receipt = None }
                      IsWorkerResult = true },
                Some fresh

        let next, delivery = rewrite fold |> rewritten snapshot

        { Next = next
          Result = result
          Delivery = delivery
          Settlements = [] })

let private failRun id failure fresh snapshot =
    workerAt id snapshot
    |> Result.map (fun rewrite ->
        let next, delivery = rewrite None |> rewritten snapshot

        { Next =
            { next with
                Failed =
                    match snapshot.Failed with
                    | Some { Failure = ExecutorFailure _ | CommitFailure _ } -> snapshot.Failed
                    | _ -> record (RunFailure failure) fresh }
          Result = ()
          Delivery = delivery
          Settlements = [] })

/// Record a missed deadline without retiring the still-live worker. It outranks an
/// ordinary update failure, and never replaces a stronger or earlier cause. A callback for
/// a worker that is no longer live changes nothing.
let private markRunFailure id failure fresh snapshot =
    match locate id snapshot, snapshot.Failure with
    | Some(WorkerSite _), (None | Some(UpdateFailure _)) ->
        publish
            { snapshot with
                Failed = record (RunFailure failure) fresh }
            ()
    | _ -> publish snapshot ()

/// Stop the executor. The first cause is kept. Every event and fold is retired and its
/// receipt fails, as does every queued intent's. Live workers stay owned until they
/// actually finish; the executor fault does not prove they stopped.
let private faultExecutor failure fresh (snapshot: Snapshot<'State>) =
    match snapshot.ExecutorFault with
    | Some _ -> publish snapshot ()
    | None ->
        let events, lanes = obligations snapshot.Work

        let eventReceipts =
            events |> Map.toList |> List.choose (fun (_, pending) -> pending.Receipt)

        let workers, laneReceipts =
            lanes
            |> Map.toList
            |> List.map (fun (key, lane) ->
                let queuedReceipts = queued lane.Slot |> List.map (fun intent -> intent.Receipt)

                match lane.Holder with
                | Worker worker -> Some(key, worker), queuedReceipts
                | Folding(first, later, successor) ->
                    successor |> Option.map (fun worker -> key, worker),
                    (first :: later |> List.choose (fun fold -> fold.Event.Receipt))
                    @ queuedReceipts)
            |> List.unzip

        let lanes =
            workers
            |> List.choose id
            |> List.map (fun (key, worker) ->
                key,
                { Holder = Worker worker
                  Slot = Running })
            |> Map.ofList

        Ok
            { Next =
                { snapshot with
                    Work = phaseOf Map.empty lanes
                    Failed = record (ExecutorFailure failure) fresh }
              Result = ()
              Delivery = None
              Settlements =
                eventReceipts @ List.concat laneReceipts
                |> List.map (fun receipt -> Fail(receipt, failure)) }

let private refused refusal : exn =
    match refusal with
    | Refusal.Foreign -> InvalidOperationException("Work identity is foreign to this owner or already retired")
    | Refusal.WrongKind -> InvalidOperationException("Work identity belongs to a different kind or stage of work")
    | Refusal.ExecutorStopped failure ->
        InvalidOperationException("The plugin executor has failed and admits no new work", failure)

let private settle settlement =
    match settlement with
    | Succeed receipt -> receipt.TrySetResult(()) |> ignore
    | Fail(receipt, failure) -> receipt.TrySetException(failure) |> ignore

let private receipt () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

// ---------------------------------------------------------------------------------------
// Owner
// ---------------------------------------------------------------------------------------

/// A typed capability into one row of a store. Standalone owners get a private store; a
/// host passes the same store to every owner so its publication answers for all of them.
///
/// Every method returns after its transition is published and its effects have run. The
/// effects run on the calling thread, so a method must never be called from inside a store
/// change: that change would wait on the writer it occupies.
type Owner<'State>(initialState: 'State, ?store: Store, ?name: string) =
    let store =
        match store with
        | Some shared -> shared
        | None -> Store()

    let handle =
        store.Register(
            defaultArg name "plugin",
            { Domain = initialState
              Work = Resting
              Committed = 0L
              Failed = None },
            fun snapshot ->
                { Busy = snapshot.IsBusy
                  Completed = snapshot.Committed
                  Failure = snapshot.Failure }
        )

    // A refusal raises inside the change, so the store publishes nothing for it.
    let apply (transition: WorkId -> Snapshot<'State> -> Result<Step<'State, 'Result>, Refusal>) =
        store.Change(
            handle,
            fun fresh snapshot ->
                match transition fresh snapshot with
                | Ok step -> step.Next, step
                | Error refusal -> raise (refused refusal)
        )

    let stopExecutor failure =
        let step = apply (faultExecutor failure)
        List.iter settle step.Settlements

    /// Publication has happened. Deliver the freed key's intent, then settle receipts
    /// whether or not delivery succeeded: a predecessor that committed stays committed. An
    /// intent that cannot be delivered can never fold, so the executor fails.
    let perform transition =
        let step = apply transition

        try
            match step.Delivery with
            | Some intent ->
                try
                    intent.Deliver intent.Id
                with failure ->
                    stopExecutor failure
                    reraise ()
            | None -> ()
        finally
            List.iter settle step.Settlements

        step.Result

    member _.Store = store

    member _.Snapshot: Snapshot<'State> = store.Read handle

    member _.AdmitEvent() : WorkId = perform (admitEvent None)

    member _.AdmitTrackedEvent() : WorkId * Task<unit> =
        let completion = receipt ()
        perform (admitEvent (Some completion)), completion.Task

    member _.TryClaim(key: string, ?after: WorkId) : WorkId option = perform (tryClaim key after)

    member _.EnqueueIntent(key: string, coalescingKey: string option, deliver: WorkId -> unit) : Task<unit> =
        perform (enqueueIntent key coalescingKey deliver (receipt ()))

    /// Publish a candidate state without settling the event.
    member _.PublishEventState(id: WorkId, state: 'State) =
        perform (fun _ snapshot -> publishEventState id state snapshot)

    /// Settle a prepared event after its finalization succeeded. `preparedCommit` says the
    /// durable commit itself succeeded, which disproves an earlier commit failure.
    member _.SettleEvent(id: WorkId, ?preparedCommit: bool) =
        perform (fun _ snapshot -> completeEvent Prepared (defaultArg preparedCommit false) None id snapshot)

    member _.CommitEvent(id: WorkId, state: 'State) =
        perform (fun _ snapshot -> completeEvent Admitted false (Some state) id snapshot)

    member _.FailEvent(id: WorkId, failure: EventFailure) = perform (failEvent id failure)

    member _.CompleteRun(id: WorkId) : WorkId option = perform (completeRun id)

    member _.FailRun(id: WorkId, failure: exn) = perform (failRun id failure)

    member _.MarkRunFailure(id: WorkId, failure: exn) = perform (markRunFailure id failure)

    member _.FaultExecutor(failure: exn) = stopExecutor failure
