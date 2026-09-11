/// Local plugin work ownership. Only the owner publishes domain state and obligations.
module internal FsHotWatch.PluginWorkOwner

open System
open System.Threading
open System.Threading.Tasks
open FsHotWatch.Events

[<Struct>]
type WorkId = private WorkId of Guid

[<NoComparison; NoEquality>]
type private QueuedIntent =
    { Id: WorkId
      CoalescingKey: string option
      Deliver: WorkId -> unit
      Receipt: TaskCompletionSource<unit> }

/// A slot owns its pending intent capabilities, not precomputed work.
[<NoComparison; NoEquality>]
type private ExclusiveSlot =
    | Running
    | RunningQueued of first: QueuedIntent * rest: QueuedIntent list

[<NoComparison; NoEquality>]
type private WorkKind =
    | Event of TaskCompletionSource<unit> option * (string * ExclusiveSlot) option
    | Committing of TaskCompletionSource<unit> option * (string * ExclusiveSlot) option
    | Exclusive of string * ExclusiveSlot

let private queued = function Running -> [] | RunningQueued(first, rest) -> first :: rest
let private slot = function [] -> Running | first :: rest -> RunningQueued(first, rest)

/// Working cannot contain zero obligations. Construction stays inside this module.
[<NoComparison; NoEquality>]
type private WorkPhase =
    | Resting
    | Working of first: (WorkId * WorkKind) * rest: Map<WorkId, WorkKind>

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

[<NoComparison; NoEquality>]
type Snapshot<'State> =
    private
        { Domain: 'State
          Phase: WorkPhase
          Completed: int64
          Failure: OwnerFailure option }

    member this.Fault = this.Failure |> Option.map (fun failure -> failure.Exception)

    member this.ExecutorFault =
        match this.Failure with
        | Some(ExecutorFailure failure) -> Some failure
        | _ -> None

    member this.State = this.Domain
    member this.CompletedEvents = this.Completed

    member this.IsBusy =
        match this.Phase with
        | Resting -> false
        | Working _ -> true

    member this.IsRunning key =
        let matches =
            function
            | Event _
            | Committing _ -> false
            | Exclusive(candidate, _) -> candidate = key

        match this.Phase with
        | Resting -> false
        | Working((_, first), rest) -> matches first || (rest |> Map.exists (fun _ kind -> matches kind))

    member this.HasExclusiveRun =
        let isExclusive =
            function
            | Event _
            | Committing _ -> false
            | Exclusive _ -> true

        match this.Phase with
        | Resting -> false
        | Working((_, first), rest) -> isExclusive first || (rest |> Map.exists (fun _ kind -> isExclusive kind))

let private entries =
    function
    | Resting -> Map.empty
    | Working((id, kind), rest) -> Map.add id kind rest

let private phase work =
    match Map.toSeq work |> Seq.tryHead with
    | None -> Resting
    | Some first -> Working(first, Map.remove (fst first) work)

let private requireKind id expected snapshot =
    match Map.tryFind id (entries snapshot.Phase) with
    | Some kind when expected kind -> ()
    | Some _ -> invalidOp "Work identity belongs to a different operation kind"
    | None -> invalidOp "Work identity is foreign or already completed"

/// Each row contains the typed immutable domain snapshot and its ownership projection.
/// Both are replaced in the same host publication, never sampled through callbacks.
[<NoComparison; NoEquality>]
type Row =
    { Name: string
      Value: obj
      Busy: bool
      Completed: int64
      Failure: OwnerFailure option
      Evidence: EarnedEvidence option }

    member this.Fault = this.Failure |> Option.map (fun failure -> failure.Exception)

    member this.ExecutorFault =
        match this.Failure with
        | Some(ExecutorFailure failure) -> Some failure
        | _ -> None

[<NoComparison; NoEquality>]
type HostSnapshot =
    private
        { Rows: Map<Guid, Row>
          Operations: Map<WorkId, string>
          HostFailures: Map<WorkId, string * exn>
          Observers: Set<WorkId>
          Model: ProjectModel.Observation }

    member this.ProjectModel = this.Model
    member this.ObserverCount = this.Observers.Count
    member this.Evidence =
        this.Rows |> Map.toList |> List.choose (fun (_, row) -> row.Evidence)

    member this.IsBusy =
        not this.Operations.IsEmpty || (this.Rows |> Map.exists (fun _ row -> row.Busy))

    member this.BusyNames =
        [ yield! this.Operations |> Map.toSeq |> Seq.map snd
          yield!
              this.Rows
              |> Map.toSeq
              |> Seq.choose (fun (_, row) -> if row.Busy then Some row.Name else None) ]
        |> List.distinct

    member this.CompletedEvents =
        this.Rows |> Map.toSeq |> Seq.sumBy (fun (_, row) -> row.Completed)

    member this.Faults =
        (this.HostFailures |> Map.toList |> List.map snd) @
        (this.Rows
        |> Map.toList
        |> List.choose (fun (_, row) -> row.Fault |> Option.map (fun ex -> row.Name, ex)))

    member this.ExecutorFaults =
        this.Rows
        |> Map.toList
        |> List.choose (fun (_, row) -> row.ExecutorFault |> Option.map (fun ex -> row.Name, ex))

    member this.OperationFaults =
        (this.HostFailures |> Map.toList |> List.map snd) @
        (this.Rows
        |> Map.toList
        |> List.choose (fun (_, row) ->
            match row.Failure with
            | Some(OperationFailure failure) -> Some(row.Name, failure)
            | _ -> None))

[<NoComparison; NoEquality>]
type private Mutation = Mutate of (HostSnapshot -> HostSnapshot * obj) * TaskCompletionSource<obj>

/// One writer publishes all plugin domains and host obligations. Transitions are
/// bounded in-memory operations; external work and receipt callbacks stay outside.
type Store() =
    let mutable published =
        { Rows = Map.empty
          Operations = Map.empty
          HostFailures = Map.empty
          Observers = Set.empty
          Model = ProjectModel.Observation.Unobserved }

    let agent =
        MailboxProcessor<Mutation>.Start(fun inbox ->
            let rec loop snapshot =
                async {
                    let! (Mutate(change, reply)) = inbox.Receive()

                    let next =
                        try
                            let next, result = change snapshot
                            Volatile.Write(&published, next)
                            reply.TrySetResult(result) |> ignore
                            next
                        with ex ->
                            reply.TrySetException(ex) |> ignore
                            snapshot

                    return! loop next
                }

            loop published)

    let mutateAsync (change: HostSnapshot -> HostSnapshot * 'Result) : Task<'Result> =
        let reply =
            TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)

        agent.Post(Mutate((fun state -> let next, result = change state in next, box result), reply))

        task {
            let! result = reply.Task
            return unbox<'Result> result
        }

    let mutate change =
        // Timeout bounds the caller, not the already posted transition.
        (mutateAsync change).WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

    member _.Snapshot = Volatile.Read(&published)

    member _.PublishProjectModel(observation: ProjectModel.Observation) =
        mutate (fun snapshot -> { snapshot with Model = observation }, ())

    /// Client observation is owned but does not keep the work it observes busy.
    /// Idle-exit reads the same aggregate instead of a separate mutable counter.
    member _.Observe() : IDisposable =
        let id =
            mutate (fun snapshot ->
                let id = WorkId(Guid.NewGuid())
                { snapshot with Observers = Set.add id snapshot.Observers }, id)

        { new IDisposable with
            member _.Dispose() =
                mutate (fun snapshot ->
                    { snapshot with Observers = Set.remove id snapshot.Observers }, ()) }

    member _.Register(row: Row) =
        mutate (fun snapshot ->
            let id = Guid.NewGuid()

            { snapshot with
                Rows = Map.add id row snapshot.Rows },
            id)

    member _.Read(id: Guid) =
        Map.find id (Volatile.Read(&published).Rows)

    member _.Change(id: Guid, change: Row -> Row * 'Result) : 'Result =
        mutate (fun snapshot ->
            let row, result = change (Map.find id snapshot.Rows)

            { snapshot with
                Rows = Map.add id row snapshot.Rows },
            result)

    /// Actual publication completion, independent of any caller's wait bound.
    member _.ChangeAsync(id: Guid, change: Row -> Row * 'Result) : Task<'Result> =
        mutateAsync (fun snapshot ->
            let row, result = change (Map.find id snapshot.Rows)

            { snapshot with
                Rows = Map.add id row snapshot.Rows },
            result)

    member _.BeginOperation(name: string) =
        mutate (fun snapshot ->
            let id = WorkId(Guid.NewGuid())

            { snapshot with
                Operations = Map.add id name snapshot.Operations
                HostFailures = snapshot.HostFailures |> Map.filter (fun failedId (failedName, _) -> failedName <> name || Map.containsKey failedId snapshot.Operations) },
            id)

    member _.FailOperation(id: WorkId, failure: exn) =
        mutate (fun snapshot ->
            match Map.tryFind id snapshot.Operations with
            | Some name ->
                { snapshot with HostFailures = Map.add id (name, failure) snapshot.HostFailures }, ()
            | None -> snapshot, ())

    member _.EndOperation(id: WorkId) =
        mutate (fun snapshot ->
            if not (Map.containsKey id snapshot.Operations) then
                invalidOp "Host work identity is foreign or already completed"

            { snapshot with
                Operations = Map.remove id snapshot.Operations },
            ())

/// Typed capability into the shared publication. Standalone handlers get a private
/// store; a host supplies the same store to every handler it registers.
type Owner<'State>(initialState: 'State, ?store: Store, ?name: string) as this =
    let store =
        match store with
        | Some shared -> shared
        | None -> Store()

    let name = defaultArg name "plugin"

    let row (snapshot: Snapshot<'State>) =
        { Name = name
          Value = box snapshot
          Busy = snapshot.IsBusy
          Completed = snapshot.CompletedEvents
          Failure = snapshot.Failure
          Evidence =
            match box snapshot.State with
            | :? IEarnedEvidenceState as domain -> domain.EarnedEvidence
            | _ -> None }

    let id =
        store.Register(
            row
                { Domain = initialState
                  Phase = Resting
                  Completed = 0L
                  Failure = None }
        )

    let mutate (change: Snapshot<'State> -> Snapshot<'State> * 'Result) : 'Result =
        store.Change(
            id,
            fun current ->
                let next, result = change (unbox<Snapshot<'State>> current.Value)
                row next, result
        )

    let admitSuccessor key pending work =
        match pending with
        | [] -> work, []
        | first :: rest ->
            let active =
                work |> Map.toList |> List.tryPick (fun (identity, kind) ->
                    match kind with
                    | Exclusive(candidate, alreadyQueued) when candidate = key -> Some(identity, alreadyQueued)
                    | _ -> None)
            match active with
            | Some(identity, alreadyQueued) ->
                // A result fold may already have launched its automatic successor.
                // Older queued commands precede intents admitted during that fold.
                Map.add identity (Exclusive(key, slot (pending @ queued alreadyQueued))) work, []
            | None ->
                // Release one intent. Its committed transition may launch work;
                // the remaining FIFO stays owned through that exact fold too.
                Map.add first.Id (Event(Some first.Receipt, Some(key, slot rest))) work, [ first ]

    let retireEvent id snapshot =
        let receipt, continuation =
            match Map.find id (entries snapshot.Phase) with
            | Event(receipt, continuation)
            | Committing(receipt, continuation) -> receipt, continuation
            | _ -> invalidOp "Expected event obligation"
        let remaining = entries snapshot.Phase |> Map.remove id
        let work, delivered =
            match continuation with
            | None -> remaining, []
            | Some(key, pending) -> admitSuccessor key (queued pending) remaining
        work, receipt, delivered

    let deliverIntents pending =
        try
            for intent in pending do
                intent.Deliver intent.Id
        with failure ->
            this.FaultExecutor failure
            raise failure

    member _.Snapshot = unbox<Snapshot<'State>> (store.Read(id).Value)

    member _.AdmitTrackedEvent() : WorkId * Task<unit> =
        mutate (fun snapshot ->
            snapshot.ExecutorFault |> Option.iter raise
            let id = WorkId(Guid.NewGuid())

            let receipt =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let work = entries snapshot.Phase |> Map.add id (Event(Some receipt, None))
            { snapshot with Phase = phase work }, (id, receipt.Task))

    member _.AdmitEvent() : WorkId =
        mutate (fun snapshot ->
            snapshot.ExecutorFault |> Option.iter raise
            let id = WorkId(Guid.NewGuid())
            let work = entries snapshot.Phase |> Map.add id (Event(None, None))
            { snapshot with Phase = phase work }, id)

    member _.TryClaim(key: string, ?after: WorkId) : (WorkId * DateTime) option =
        mutate (fun snapshot ->
            snapshot.ExecutorFault |> Option.iter raise

            let occupied =
                entries snapshot.Phase |> Map.exists (fun identity kind ->
                    match kind with
                    | Exclusive(candidate, _) -> candidate = key
                    | Event(_, Some(candidate, _))
                    | Committing(_, Some(candidate, _)) -> candidate = key && Some identity <> after
                    | _ -> false)

            if occupied then
                snapshot, None
            else
                let id = WorkId(Guid.NewGuid())
                let work = entries snapshot.Phase |> Map.add id (Exclusive(key, Running))
                { snapshot with Phase = phase work }, Some(id, DateTime.UtcNow))

    /// Admit intent under a live slot or its result fold. Coalescing replaces
    /// only the payload of an equal pending intent, retaining its exact receipt.
    member _.EnqueueIntent(key: string, coalescingKey: string option, deliver: WorkId -> unit) : Task<unit> =
        let immediate, receipt =
            mutate (fun snapshot ->
                snapshot.ExecutorFault |> Option.iter raise
                let work = entries snapshot.Phase
                let candidate =
                    work |> Map.toList |> List.tryPick (fun (id, kind) ->
                        match kind with
                        | Exclusive(candidate, pending) when candidate = key -> Some(id, kind, pending)
                        | _ -> None)
                    |> Option.orElseWith (fun () ->
                        work |> Map.toList |> List.tryPick (fun (id, kind) ->
                            match kind with
                            | Event(_, Some(candidate, pending))
                            | Committing(_, Some(candidate, pending)) when candidate = key -> Some(id, kind, pending)
                            | _ -> None))

                let existing =
                    candidate |> Option.bind (fun (_, _, pending) ->
                        coalescingKey |> Option.bind (fun requested ->
                            queued pending |> List.tryFind (fun intent -> intent.CoalescingKey = Some requested)))
                let intent =
                    match existing with
                    | Some prior -> { prior with Deliver = deliver }
                    | None ->
                        { Id = WorkId(Guid.NewGuid())
                          CoalescingKey = coalescingKey
                          Deliver = deliver
                          Receipt = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously) }

                match candidate with
                | None ->
                    { snapshot with Phase = Map.add intent.Id (Event(Some intent.Receipt, None)) work |> phase },
                    (Some intent, intent.Receipt.Task)
                | Some(id, kind, pending) ->
                    let next =
                        (match existing with
                         | None -> queued pending @ [ intent ]
                         | Some prior -> queued pending |> List.map (fun item -> if item.Id = prior.Id then intent else item))
                        |> slot
                    let kind =
                        match kind with
                        | Exclusive(key, _) -> Exclusive(key, next)
                        | Event(receipt, _) -> Event(receipt, Some(key, next))
                        | Committing(receipt, _) -> Committing(receipt, Some(key, next))
                    { snapshot with Phase = Map.add id kind work |> phase }, (None, intent.Receipt.Task))

        immediate |> Option.iter (fun intent -> deliverIntents [ intent ])
        receipt

    member _.CommitEvent(id: WorkId, state: 'State) =
        let receipt, pending =
            mutate (fun snapshot ->
                requireKind id (function Event _ -> true | _ -> false) snapshot
                let work, receipt, pending = retireEvent id snapshot
                { snapshot with
                    Domain = state
                    Phase = phase work
                    Completed = snapshot.Completed + 1L
                    Failure =
                        match snapshot.Failure with
                        | Some(UpdateFailure _) -> None
                        | other -> other },
                (receipt, pending))
        deliverIntents pending
        receipt |> Option.iter (fun completion -> completion.TrySetResult(()) |> ignore)

    /// Publish a prepared candidate without settling its original event obligation.
    member _.PublishEventState(id: WorkId, state: 'State) =
        mutate (fun snapshot ->
            requireKind
                id
                (function
                | Event _ -> true
                | _ -> false)
                snapshot

            let receipt, continuation =
                match Map.find id (entries snapshot.Phase) with
                | Event(receipt, continuation) -> receipt, continuation
                | _ -> invalidOp "Expected event obligation"

            { snapshot with
                Domain = state
                Phase = entries snapshot.Phase |> Map.add id (Committing(receipt, continuation)) |> phase },
            ())

    /// Only the exact prepared event can acknowledge successful finalization.
    member _.SettleEvent(id: WorkId, ?preparedCommit: bool) =
        let preparedCommit = defaultArg preparedCommit false

        let receipt, pending =
            mutate (fun snapshot ->
                requireKind
                    id
                    (function
                    | Committing _ -> true
                    | _ -> false)
                    snapshot

                let work, receipt, pending = retireEvent id snapshot

                { snapshot with
                    Phase = phase work
                    Completed = snapshot.Completed + 1L
                    Failure =
                        match snapshot.Failure with
                        | Some(UpdateFailure _) -> None
                        | Some(CommitFailure _)
                        | Some(RunFailure _) when preparedCommit -> None
                        | other -> other },
                (receipt, pending))

        deliverIntents pending
        receipt |> Option.iter (fun completion -> completion.TrySetResult(()) |> ignore)

    /// A failed fold cannot acknowledge success or discard unrelated live workers.
    member _.FailEvent(id: WorkId, failure: OwnerFailure) =
        match failure with
        | ExecutorFailure _ -> invalidArg "failure" "Use FaultExecutor for executor termination"
        | _ -> ()

        let receipt, pending =
            mutate (fun snapshot ->
                requireKind
                    id
                    (function
                    | Event _
                    | Committing _ -> true
                    | _ -> false)
                    snapshot

                let work, receipt, pending = retireEvent id snapshot

                let retainedFailure =
                    match snapshot.Failure, failure with
                    | Some(CommitFailure _ as prior), UpdateFailure _
                    | Some(RunFailure _ as prior), UpdateFailure _ -> prior
                    | _ -> failure

                { snapshot with
                    Phase = phase work
                    Failure = Some retainedFailure },
                (receipt, pending))

        deliverIntents pending

        receipt
        |> Option.iter (fun completion -> completion.TrySetException(failure.Exception) |> ignore)

    /// Transfer to a result fold only while its executor can accept it. A stopped
    /// executor cannot consume a result; the worker still retires only after cleanup.
    member _.CompleteRun(id: WorkId) : WorkId option =
        mutate (fun snapshot ->
            requireKind
                id
                (function
                | Exclusive _ -> true
                | Event _
                | Committing _ -> false)
                snapshot

            let remaining = entries snapshot.Phase |> Map.remove id

            match snapshot.ExecutorFault with
            | Some _ ->
                { snapshot with
                    Phase = phase remaining },
                None
            | None ->
                let completion = WorkId(Guid.NewGuid())
                let continuation =
                    match Map.find id (entries snapshot.Phase) with
                    | Exclusive(key, pending) -> Some(key, pending)
                    | _ -> invalidOp "Expected exclusive operation"
                let work = remaining |> Map.add completion (Event(None, continuation))
                { snapshot with Phase = phase work }, Some completion)

    member this.TransferToCompletion(id: WorkId) : WorkId =
        this.CompleteRun(id)
        |> Option.defaultWith (fun () -> invalidOp "Plugin executor has failed")

    member _.FaultExecutor(failure: exn) =
        let failure, receipts =
            mutate (fun snapshot ->
                match snapshot.ExecutorFault with
                | Some original -> snapshot, (original, [])
                | None ->
                    let remaining, receipts =
                        entries snapshot.Phase
                        |> Map.fold
                            (fun (remaining, receipts) id kind ->
                                match kind with
                                | Exclusive(key, pending) ->
                                    let queuedReceipts = queued pending |> List.map (fun intent -> intent.Receipt)
                                    Map.add id (Exclusive(key, Running)) remaining, queuedReceipts @ receipts
                                | Event(receipt, pending)
                                | Committing(receipt, pending) ->
                                    let queuedReceipts = pending |> Option.map (snd >> queued >> List.map (fun intent -> intent.Receipt)) |> Option.defaultValue []
                                    remaining, (Option.toList receipt) @ queuedReceipts @ receipts)
                            (Map.empty, [])

                    { snapshot with
                        Phase = phase remaining
                        Failure = Some(ExecutorFailure failure) },
                    (failure, receipts))

        for receipt in receipts do
            receipt.TrySetException(failure) |> ignore

    /// Record a worker deadline without releasing its still-live capability.
    member _.MarkRunFailure(id: WorkId, failure: exn) =
        mutate (fun snapshot ->
            match Map.tryFind id (entries snapshot.Phase) with
            | Some(Exclusive _) ->
                { snapshot with
                    Failure =
                        match snapshot.Failure with
                        | Some prior -> Some prior
                        | None -> Some(RunFailure failure) }, ()
            | _ -> snapshot, ())

    member _.FailRun(id: WorkId, failure: exn) =
        let pending =
            mutate (fun snapshot ->
                requireKind id (function Exclusive _ -> true | _ -> false) snapshot
                let key, pending =
                    match Map.find id (entries snapshot.Phase) with
                    | Exclusive(key, pending) -> key, queued pending
                    | _ -> invalidOp "Expected exclusive operation"
                let work, delivered = admitSuccessor key pending (entries snapshot.Phase |> Map.remove id)
                { snapshot with
                    Phase = phase work
                    Failure =
                        match snapshot.Failure with
                        | Some(ExecutorFailure _ as prior)
                        | Some(CommitFailure _ as prior) -> Some prior
                        | _ -> Some(RunFailure failure) }, delivered)
        deliverIntents pending
