/// Debounced input that stays owned from the moment it is posted until the supervised
/// work it became has settled.
///
/// Input waiting out its debounce is one pending cohort, and cohorts whose debounce ended
/// wait, in order, for admission to the supervisor. Both are published in the host
/// `Store`, so an input between the watcher and the worker is never mistaken for rest.
/// `Post` returns after its input is published. Each cohort's receipts settle with the
/// outcome of the supervised request it became.
module internal FsHotWatch.DebouncedWork

open System
open System.Threading
open System.Threading.Tasks
open FsHotWatch.PluginWorkOwner

[<NoComparison; NoEquality>]
type private Cohort<'Request> =
    { Id: WorkId
      Value: 'Request
      Delay: TimeSpan
      Receipts: TaskCompletionSource<unit> list }

[<NoComparison; NoEquality>]
type private Input<'Request> =
    {
        /// Input still inside its debounce window.
        Pending: Cohort<'Request> option
        /// Cohorts whose debounce ended, oldest first. The head is the next to enter the
        /// supervisor, and it leaves this list only once the supervisor owns it.
        Admitting: Cohort<'Request> list
        Closed: bool
        Failure: exn option
    }

/// What a debounce timer found when it fired.
[<NoComparison; NoEquality>]
type private Expiry<'Request> =
    | Admit
    | Abandon of Cohort<'Request> * exn
    /// Its cohort was already flushed or replaced by later input.
    | Obsolete

/// Coalesces input with `merge` and hands each cohort to `worker`.
///
/// A positive delay restarts the debounce with the longer of the pending and new delays.
/// A zero delay flushes: it merges into any pending input and queues the cohort for
/// admission at once, which makes it a barrier ordered after everything posted before it.
type Queue<'State, 'Request>
    (
        store: Store,
        name: string,
        worker: SupervisedWork.Queue<'State, 'Request>,
        merge: 'Request -> 'Request -> 'Request,
        ?delayTask: TimeSpan -> Task
    ) =
    let delayTask = defaultArg delayTask (fun delay -> Task.Delay(delay))

    let handle =
        store.Register(
            name,
            { Pending = None
              Admitting = []
              Closed = false
              Failure = None },
            fun input ->
                RowStatus.ofWork
                    (input.Pending.IsSome || not (List.isEmpty input.Admitting))
                    0L
                    (input.Failure |> Option.map OperationFailure)
        )

    let change transition =
        store.ChangeAsync(handle, fun _ input -> transition input)

    let settle (cohort: Cohort<'Request>) (failure: exn option) =
        for receipt in cohort.Receipts do
            match failure with
            | None -> receipt.TrySetResult(()) |> ignore
            | Some failure -> receipt.TrySetException failure |> ignore

    let settleWith (cohort: Cohort<'Request>) (receipt: Task<unit>) =
        task {
            try
                do! receipt
                settle cohort None
            with failure ->
                settle cohort (Some failure)
        }
        |> ignore

    // Admit the oldest cohort. One admission runs per cohort queued, one at a time, and
    // each takes the list's head, so cohorts enter the supervisor in the order they were
    // published no matter which thread scheduled them.
    let admitHead () : Task =
        task {
            let head = List.head (store.Read handle).Admitting
            let! admitted = worker.SubmitAsync(head.Value, CancellationToken.None)

            let refusal =
                match admitted with
                | Ok _ -> None
                | Error failure -> Some failure

            // The supervisor owns the cohort (or refused it) before its input retires.
            do!
                change (fun input ->
                    { input with
                        Admitting = List.tail input.Admitting
                        Failure = Option.orElse input.Failure refusal },
                    ())

            match admitted with
            | Ok receipt -> settleWith head receipt
            | Error failure -> settle head (Some failure)
        }

    let admissionGate = obj ()
    let mutable admissions = Task.CompletedTask

    let scheduleAdmission () =
        lock admissionGate (fun () ->
            admissions <-
                admissions
                    .ContinueWith(
                        (fun (_: Task) -> admitHead ()),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default
                    )
                    .Unwrap())

    let expire (id: WorkId) (delay: TimeSpan) : Task =
        task {
            let! waited =
                task {
                    try
                        do! delayTask delay
                        return None
                    with failure ->
                        return Some failure
                }

            let! expiry =
                change (fun input ->
                    match input.Pending, waited with
                    | Some cohort, None when cohort.Id = id ->
                        { input with
                            Pending = None
                            Admitting = input.Admitting @ [ cohort ] },
                        Admit
                    | Some cohort, Some failure when cohort.Id = id ->
                        { input with
                            Pending = None
                            Failure = Some failure },
                        Abandon(cohort, failure)
                    | _ -> input, Obsolete)

            // Logged after the transition, so the line is a witness that it happened.
            waited
            |> Option.iter (fun failure -> Logging.error name $"a debounce timer failed: %O{failure}")

            match expiry with
            | Admit -> scheduleAdmission ()
            | Abandon(cohort, failure) -> settle cohort (Some failure)
            | Obsolete -> ()
        }

    /// Publish `value` as input, waiting at most `SupervisedWork.AdmissionBound`. The
    /// returned receipt settles with the outcome of the supervised request it becomes.
    /// Raises the refusal: a closed queue, or a `merge` that throws (which leaves the
    /// pending input as it was).
    member _.Post(value: 'Request, delay: TimeSpan) : Task<unit> =
        if delay < TimeSpan.Zero || delay = TimeSpan.MaxValue then
            invalidArg (nameof delay) "A debounce delay must be finite and nonnegative"

        let receipt =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let admission =
            task {
                let! cohort =
                    store.ChangeAsync(
                        handle,
                        fun fresh input ->
                            if input.Closed then
                                raise (ObjectDisposedException name)

                            let cohort =
                                match input.Pending with
                                | None ->
                                    { Id = fresh
                                      Value = value
                                      Delay = delay
                                      Receipts = [ receipt ] }
                                | Some pending ->
                                    { Id = fresh
                                      Value = merge pending.Value value
                                      Delay = max pending.Delay delay
                                      Receipts = receipt :: pending.Receipts }

                            if delay = TimeSpan.Zero then
                                { input with
                                    Pending = None
                                    Admitting = input.Admitting @ [ cohort ]
                                    Failure = None },
                                cohort
                            else
                                { input with
                                    Pending = Some cohort
                                    Failure = None },
                                cohort
                    )

                if delay = TimeSpan.Zero then
                    scheduleAdmission ()
                else
                    expire cohort.Id cohort.Delay |> ignore

                return receipt.Task
            }

        admission |> SupervisedWork.waitWithin SupervisedWork.AdmissionBound

    /// Close input and the worker. Pending input settles with `ObjectDisposedException`.
    /// Cohorts already queued for admission keep their identity until the closed worker
    /// refuses them.
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
                |> Option.iter (fun cohort -> settle cohort (Some(ObjectDisposedException name)))

                worker.Close()
            }

        closing |> SupervisedWork.waitWithin SupervisedWork.AdmissionBound
