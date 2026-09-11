/// Watcher input stays owned while coalescing and while entering the supervisor.
module internal FsHotWatch.DebouncedWork

open System
open System.Threading
open System.Threading.Tasks

[<NoComparison; NoEquality>]
type private Group<'Request> =
    { Id: Guid
      Value: 'Request
      Delay: TimeSpan
      Receipts: TaskCompletionSource<unit> list }

[<NoComparison; NoEquality>]
type private Input<'Request> =
    { Pending: Group<'Request> option
      Moving: Group<'Request> list
      Closed: bool
      Failure: exn option }

/// Coalesces input without running external callbacks in the publication writer.
/// Zero-delay input flushes the preceding cohort atomically (a command barrier).
type Queue<'State, 'Request>
    (
        store: PluginWorkOwner.Store,
        name: string,
        worker: SupervisedWork.Queue<'State, 'Request>,
        merge: 'Request -> 'Request -> 'Request,
        ?delayTask: (TimeSpan -> Task)
    ) =
    let delayTask = defaultArg delayTask (fun delay -> Task.Delay(delay))

    let row (input: Input<'Request>) : PluginWorkOwner.Row =
        { Name = name
          Value = box input
          Busy = input.Pending.IsSome || not input.Moving.IsEmpty
          Completed = 0L
          Failure = input.Failure |> Option.map PluginWorkOwner.OperationFailure
          Evidence = None
          AnalysisEvidence = None }

    let id =
        store.Register(
            row
                { Pending = None
                  Moving = []
                  Closed = false
                  Failure = None }
        )

    let change transition =
        store.ChangeAsync(
            id,
            fun current ->
                let next, result = transition (unbox<Input<'Request>> current.Value)
                row next, result
        )

    let settle (group: Group<'Request>) (outcome: Result<unit, exn>) =
        for receipt in group.Receipts do
            match outcome with
            | Ok() -> receipt.TrySetResult(()) |> ignore
            | Result.Error failure -> receipt.TrySetException(failure) |> ignore

    let observe (pending: Task) =
        pending.ContinueWith(
            (fun (failed: Task) -> Logging.error name $"change admission task failed: {failed.Exception}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        )
        |> ignore

    let retire movingId failure =
        change (fun input ->
            match input.Moving with
            | current :: rest when current.Id = movingId ->
                { input with
                    Moving = rest
                    Failure = failure |> Option.orElse input.Failure },
                List.tryHead rest
            | _ -> invalidOp "Change cohort admission is foreign or out of order")

    let rec dispatch (group: Group<'Request>) =
        task {
            let! admitted = worker.SubmitAsync(group.Value, CancellationToken.None)

            let failure =
                match admitted with
                | Ok _ -> None
                | Result.Error failure -> Some failure

            let! next = retire group.Id failure
            // Only the queue head may enter the supervisor. Launch its successor
            // after actual admission, without waiting for external work to finish.
            next |> Option.iter (fun pending -> dispatch pending |> observe)

            match admitted with
            | Ok receipt ->
                try
                    do! receipt
                    settle group (Ok())
                with failure ->
                    Logging.error name $"change cohort failed: {failure}"
                    settle group (Result.Error failure)
            | Result.Error failure -> settle group (Result.Error failure)
        }

    let expire groupId delay =
        task {
            let! waited =
                task {
                    try
                        do! delayTask delay
                        return Ok()
                    with failure ->
                        return Result.Error failure
                }

            match waited with
            | Result.Error failure ->
                let! abandoned =
                    change (fun input ->
                        match input.Pending with
                        | Some group when group.Id = groupId ->
                            { input with
                                Pending = None
                                Failure = Some failure },
                            Some group
                        | _ -> input, None)
                // A superseding cohort owns its own scheduler and all merged
                // receipts. An obsolete timer cannot fail that newer work.
                abandoned |> Option.iter (fun group -> settle group (Result.Error failure))
                Logging.error name $"debounce scheduling failed: {failure}"
            | Ok() ->
                let! ready =
                    change (fun input ->
                        match input.Pending with
                        | Some group when group.Id = groupId ->
                            { input with
                                Pending = None
                                Moving = input.Moving @ [ group ] },
                            (if input.Moving.IsEmpty then Some group else None)
                        | _ -> input, None)

                match ready with
                | Some group -> do! dispatch group
                | None -> ()
        }

    member _.Post(value: 'Request, delay: TimeSpan) : Task<unit> =
        if delay < TimeSpan.Zero || delay = TimeSpan.MaxValue then
            invalidArg (nameof delay) "Debounce must be finite and nonnegative"

        let receipt =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let admission =
            task {
                try
                    let! group, startNow =
                        change (fun input ->
                            if input.Closed then
                                raise (ObjectDisposedException(name))

                            let combined =
                                match input.Pending with
                                | None ->
                                    { Id = Guid.NewGuid()
                                      Value = value
                                      Delay = delay
                                      Receipts = [ receipt ] }
                                | Some previous ->
                                    { Id = Guid.NewGuid()
                                      Value = merge previous.Value value
                                      Delay =
                                        if delay = TimeSpan.Zero then
                                            delay
                                        else
                                            max previous.Delay delay
                                      Receipts = receipt :: previous.Receipts }

                            if delay = TimeSpan.Zero then
                                { input with
                                    Pending = None
                                    Moving = input.Moving @ [ combined ]
                                    Failure = None },
                                (combined, input.Moving.IsEmpty)
                            else
                                { input with
                                    Pending = Some combined
                                    Failure = None },
                                (combined, false))

                    if startNow then
                        dispatch group |> observe
                    elif group.Delay > TimeSpan.Zero then
                        expire group.Id group.Delay |> observe

                    return Ok receipt.Task
                with failure ->
                    return Result.Error failure
            }
        // Scheduling remains attached if this caller's bound expires.
        match admission.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult() with
        | Ok receipt -> receipt
        | Result.Error failure -> raise failure

    member _.Close() =
        let closing =
            task {
                let! pending =
                    change (fun input ->
                        { input with
                            Closed = true
                            Pending = None },
                        input.Pending)

                pending
                |> Option.iter (fun group -> settle group (Result.Error(ObjectDisposedException(name))))
                // Moving cohorts retain their identities until real admission/rejection.
                worker.Close()
            }

        observe closing
        closing.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
