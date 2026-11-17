/// Debounced input that stays owned from the moment it is posted until the supervised
/// work it became has settled.
///
/// Input waiting out its debounce is one pending cohort, and cohorts whose debounce ended
/// wait, in order, for admission to the supervisor. Both are published in the host
/// `Store`, so an input between the watcher and the worker is never mistaken for rest.
/// `Post` returns after its input is published. Each cohort's receipts settle with the
/// outcome of the supervised request it became, and the cohort stays owned until they have:
/// rest implies every receipt handed out has settled (AUTOMATION-1780).
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
        /// Cohorts the supervisor owns whose receipts have not settled. Rest waits for them
        /// (AUTOMATION-1780): a cohort leaves this list in the transition that sets its receipts,
        /// so a host at rest has settled every receipt it handed out.
        Settling: WorkId list
        Closed: bool
        Failure: exn option
    }

/// What a debounce timer found when it fired.
[<NoComparison; NoEquality>]
type private Expiry<'Request> =
    | Admit
    /// Its timer failed: the cohort settled with that failure in the same transition.
    | Abandon
    /// Its cohort was already flushed or replaced by later input.
    | Obsolete

/// Coalesces input with `merge` and hands each cohort to `worker`.
///
/// A positive delay restarts the debounce with the longer of the pending and new delays.
/// A zero delay flushes: it merges into any pending input and queues the cohort for
/// admission at once, which makes it a barrier ordered after everything posted before it.
///
/// Its debounce timers (`Runtime.Delay`), the continuation chain that admits cohorts in order
/// (`Runtime.Scheduler`) and its bounded waits (`Runtime.Wait`) come from its store's runtime
/// (AUTOMATION-1695), so the simulation decides when each timer fires and each admission runs.
///
/// `awaitReceipt` wraps the supervised request's receipt as a cohort's input retires. Tests pass
/// an already-finished one to pin the arm a real one reaches only when the pool runs it first.
/// In production it is the receipt itself.
type Queue<'State, 'Request>
    (
        store: Store,
        name: string,
        worker: SupervisedWork.Queue<'State, 'Request>,
        merge: 'Request -> 'Request -> 'Request,
        ?awaitReceipt: Task<unit> -> Task<unit>
    ) =
    let runtime = store.Runtime
    let awaitReceipt = defaultArg awaitReceipt id

    let handle =
        store.Register(
            name,
            { Pending = None
              Admitting = []
              Settling = []
              Closed = false
              Failure = None },
            fun input ->
                RowStatus.ofWork
                    (input.Pending.IsSome
                     || not (List.isEmpty input.Admitting)
                     || not (List.isEmpty input.Settling))
                    0L
                    (input.Failure |> Option.map OperationFailure)
        )

    // A change whose effect runs on the writer right after its publication (`ChangeAsyncThen`).
    let changeThen transition =
        store.ChangeAsyncThen(handle, fun _ input -> transition input)

    // The effect of the change that takes the cohort out of what the row owns: run by the writer
    // right after that publication, before anything else runs (AUTOMATION-1780). A reader woken
    // by a receipt finds the cohort retired; a reader that sees the retirement finds the receipts
    // set, in the simulation exactly, and on the pool but for the statements of this loop
    // (ADR-078's atomic-step limit).
    let setReceipts (cohort: Cohort<'Request>) (failure: exn option) () =
        for receipt in cohort.Receipts do
            match failure with
            | None -> receipt.TrySetResult(()) |> ignore
            | Some failure -> receipt.TrySetException failure |> ignore

    // A cohort the supervisor owned settles with its request's outcome, leaving `Settling`.
    let settle (cohort: Cohort<'Request>) (failure: exn option) =
        changeThen (fun input ->
            { input with
                Settling = List.filter (fun id -> id <> cohort.Id) input.Settling },
            (),
            setReceipts cohort failure)
        |> ignore

    let settleWith (cohort: Cohort<'Request>) (receipt: Task<unit>) =
        task {
            try
                do! awaitReceipt receipt
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

            // The supervisor owns the cohort (or refused it) before its input retires. A refused
            // cohort settles with this publication; an owned one stays owned until its request does.
            do!
                changeThen (fun input ->
                    match admitted with
                    | Ok _ ->
                        { input with
                            Admitting = List.tail input.Admitting
                            Settling = head.Id :: input.Settling },
                        (),
                        ignore
                    | Error failure ->
                        { input with
                            Admitting = List.tail input.Admitting
                            Failure = Option.orElse input.Failure (Some failure) },
                        (),
                        setReceipts head (Some failure))

            match admitted with
            | Ok receipt -> settleWith head receipt
            | Error _ -> ()
        }

    let admissionGate = Lock()
    let mutable admissions = Task.CompletedTask

    let scheduleAdmission () =
        Locking.locked admissionGate (fun () ->
            admissions <-
                admissions
                    .ContinueWith(
                        (fun (_: Task) -> admitHead ()),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        runtime.Scheduler
                    )
                    .Unwrap())

    let expire (id: WorkId) (delay: TimeSpan) : Task =
        task {
            let! waited =
                task {
                    try
                        do! runtime.Delay delay
                        return None
                    with failure ->
                        return Some failure
                }

            let! expiry =
                changeThen (fun input ->
                    match input.Pending, waited with
                    | Some cohort, None when cohort.Id = id ->
                        { input with
                            Pending = None
                            Admitting = input.Admitting @ [ cohort ] },
                        Admit,
                        ignore
                    | Some cohort, Some failure when cohort.Id = id ->
                        { input with
                            Pending = None
                            Failure = Some failure },
                        Abandon,
                        setReceipts cohort (Some failure)
                    | _ -> input, Obsolete, ignore)

            // Logged after the transition, so the line is a witness that it happened.
            waited
            |> Option.iter (fun failure -> Logging.error name $"a debounce timer failed: %O{failure}")

            match expiry with
            | Admit -> scheduleAdmission ()
            | Abandon -> ()
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

        admission |> SupervisedWork.waitWithin runtime SupervisedWork.AdmissionBound

    /// Close input and the worker. Pending input settles with `ObjectDisposedException`.
    /// Cohorts already queued for admission keep their identity until the closed worker
    /// refuses them.
    member _.Close() =
        let closing =
            task {
                do!
                    changeThen (fun input ->
                        { input with
                            Closed = true
                            Pending = None },
                        (),
                        match input.Pending with
                        | Some cohort -> setReceipts cohort (Some(ObjectDisposedException name))
                        | None -> ignore)

                worker.Close()
            }

        closing |> SupervisedWork.waitWithin runtime SupervisedWork.AdmissionBound
