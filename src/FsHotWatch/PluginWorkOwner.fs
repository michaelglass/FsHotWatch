/// Local plugin work ownership. Only the owner publishes domain state and obligations.
module internal FsHotWatch.PluginWorkOwner

open System
open System.Threading
open System.Threading.Tasks

[<Struct>]
type WorkId = private WorkId of Guid

[<NoComparison; NoEquality>]
type private WorkKind =
    | Event of TaskCompletionSource<unit> option
    | Exclusive of string

/// Working cannot contain zero obligations. Construction stays inside this module.
[<NoComparison; NoEquality>]
type private WorkPhase =
    | Resting
    | Working of first: (WorkId * WorkKind) * rest: Map<WorkId, WorkKind>

[<NoComparison; NoEquality>]
type Snapshot<'State> =
    private
        { Domain: 'State
          Phase: WorkPhase
          Completed: int64
          ExecutorFault: exn option }

    member this.Fault = this.ExecutorFault
    member this.State = this.Domain
    member this.CompletedEvents = this.Completed

    member this.IsBusy =
        match this.Phase with
        | Resting -> false
        | Working _ -> true

    member this.IsRunning key =
        let matches =
            function
            | Event _ -> false
            | Exclusive candidate -> candidate = key

        match this.Phase with
        | Resting -> false
        | Working((_, first), rest) -> matches first || (rest |> Map.exists (fun _ kind -> matches kind))

    member this.HasExclusiveRun =
        let isExclusive =
            function
            | Event _ -> false
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
      Fault: exn option }

[<NoComparison; NoEquality>]
type HostSnapshot =
    private
        { Rows: Map<Guid, Row>
          Operations: Map<WorkId, string> }

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
        this.Rows
        |> Map.toList
        |> List.choose (fun (_, row) -> row.Fault |> Option.map (fun ex -> row.Name, ex))

[<NoComparison; NoEquality>]
type private Mutation = Mutate of (HostSnapshot -> HostSnapshot * obj) * TaskCompletionSource<obj>

/// One writer publishes all plugin domains and host obligations. Transitions are
/// bounded in-memory operations; external work and receipt callbacks stay outside.
type Store() =
    let mutable published =
        { Rows = Map.empty
          Operations = Map.empty }

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
                Operations = Map.add id name snapshot.Operations },
            id)

    member _.EndOperation(id: WorkId) =
        mutate (fun snapshot ->
            if not (Map.containsKey id snapshot.Operations) then
                invalidOp "Host work identity is foreign or already completed"

            { snapshot with
                Operations = Map.remove id snapshot.Operations },
            ())

/// Typed capability into the shared publication. Standalone handlers get a private
/// store; a host supplies the same store to every handler it registers.
type Owner<'State>(initialState: 'State, ?store: Store, ?name: string) =
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
          Fault = snapshot.Fault }

    let id =
        store.Register(
            row
                { Domain = initialState
                  Phase = Resting
                  Completed = 0L
                  ExecutorFault = None }
        )

    let mutate (change: Snapshot<'State> -> Snapshot<'State> * 'Result) : 'Result =
        store.Change(
            id,
            fun current ->
                let next, result = change (unbox<Snapshot<'State>> current.Value)
                row next, result
        )

    member _.Snapshot = unbox<Snapshot<'State>> (store.Read(id).Value)

    member _.AdmitTrackedEvent() : WorkId * Task<unit> =
        mutate (fun snapshot ->
            snapshot.ExecutorFault |> Option.iter raise
            let id = WorkId(Guid.NewGuid())

            let receipt =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let work = entries snapshot.Phase |> Map.add id (Event(Some receipt))
            { snapshot with Phase = phase work }, (id, receipt.Task))

    member _.AdmitEvent() : WorkId =
        mutate (fun snapshot ->
            snapshot.ExecutorFault |> Option.iter raise
            let id = WorkId(Guid.NewGuid())
            let work = entries snapshot.Phase |> Map.add id (Event None)
            { snapshot with Phase = phase work }, id)

    member _.TryClaim(key: string) : (WorkId * DateTime) option =
        mutate (fun snapshot ->
            snapshot.ExecutorFault |> Option.iter raise

            if snapshot.IsRunning key then
                snapshot, None
            else
                let id = WorkId(Guid.NewGuid())
                let work = entries snapshot.Phase |> Map.add id (Exclusive key)
                { snapshot with Phase = phase work }, Some(id, DateTime.UtcNow))

    member _.CommitEvent(id: WorkId, state: 'State) =
        let receipt =
            mutate (fun snapshot ->
                requireKind
                    id
                    (function
                    | Event _ -> true
                    | _ -> false)
                    snapshot

                let receipt =
                    match Map.find id (entries snapshot.Phase) with
                    | Event receipt -> receipt
                    | _ -> invalidOp "Expected event obligation"

                let work = entries snapshot.Phase |> Map.remove id

                { snapshot with
                    Domain = state
                    Phase = phase work
                    Completed = snapshot.Completed + 1L },
                receipt)

        receipt |> Option.iter (fun completion -> completion.TrySetResult(()) |> ignore)

    /// Transfer to a result fold only while its executor can accept it. A stopped
    /// executor cannot consume a result; the worker still retires only after cleanup.
    member _.CompleteRun(id: WorkId) : WorkId option =
        mutate (fun snapshot ->
            requireKind
                id
                (function
                | Exclusive _ -> true
                | Event _ -> false)
                snapshot

            let remaining = entries snapshot.Phase |> Map.remove id

            match snapshot.ExecutorFault with
            | Some _ ->
                { snapshot with
                    Phase = phase remaining },
                None
            | None ->
                let completion = WorkId(Guid.NewGuid())
                let work = remaining |> Map.add completion (Event None)
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
                                | Exclusive _ -> Map.add id kind remaining, receipts
                                | Event None -> remaining, receipts
                                | Event(Some receipt) -> remaining, receipt :: receipts)
                            (Map.empty, [])

                    { snapshot with
                        Phase = phase remaining
                        ExecutorFault = Some failure },
                    (failure, receipts))

        for receipt in receipts do
            receipt.TrySetException(failure) |> ignore

    member _.FailRun(id: WorkId) =
        mutate (fun snapshot ->
            requireKind
                id
                (function
                | Exclusive _ -> true
                | Event _ -> false)
                snapshot

            { snapshot with
                Phase = entries snapshot.Phase |> Map.remove id |> phase },
            ())
