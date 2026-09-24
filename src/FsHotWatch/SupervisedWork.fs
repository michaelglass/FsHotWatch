/// Serial external work (scans, change batches) owned in the host publication from
/// admission until its receipt settles.
///
/// A `Queue` is one row of a `PluginWorkOwner.Store`. Its phase, domain state, queued
/// requests, admission closure and last failure are published together, so a reader never
/// waits for the work and never sees rest between a finishing request and its successor.
/// Callbacks run on a worker task, never inside the store's writer.
///
/// Every request runs under `execute`: a finite deadline, a child-process scope of its
/// own, and cancellation. A deadline records a failure and requests cancellation. It does
/// not retire the request: only the callback's actual return does, after its completion
/// notification and deadline teardown. A caller that stops waiting changes nothing either.
module internal FsHotWatch.SupervisedWork

open System
open System.Threading
open System.Threading.Tasks
open FsHotWatch.PluginWorkOwner

/// How long a caller waits for its admission, or a close, to be published. The
/// transition keeps its effects if this bound expires: a launch, a receipt and a
/// cancellation stay attached to the publication, not to the caller.
let internal AdmissionBound = TimeSpan.FromSeconds 5.0

/// Block this thread until `task` settles, at most `bound`, then raise its failure
/// unwrapped, or `TimeoutException` once the bound passes.
///
/// The bound is kept by this thread's own wait. `task.WaitAsync(bound)` keeps it with a
/// timer whose callback runs on the thread pool, so with every pool thread busy the
/// caller waited until the pool freed one: a bounded wait that is not bounded.
let internal waitWithin (bound: TimeSpan) (task: Task<'T>) : 'T =
    let settled =
        try
            task.Wait bound
        with :? AggregateException ->
            true

    if settled then
        task.GetAwaiter().GetResult()
    else
        raise (TimeoutException($"gave up after %O{bound}"))

let private requireBounded (deadline: TimeSpan) =
    if deadline <= TimeSpan.Zero || deadline = TimeSpan.MaxValue then
        invalidArg (nameof deadline) "Supervised work needs a finite positive deadline"

/// A one-shot timer that calls `expire` after `delay`.
let defaultScheduler (delay: TimeSpan) (expire: unit -> unit) : IDisposable =
    new Timer((fun _ -> expire ()), null, delay, Timeout.InfiniteTimeSpan) :> IDisposable

/// Request cancellation without running registered callbacks on this thread: a callback
/// that blocks or throws must not stall a timer or a close. A source disposed before or
/// while its callbacks run belongs to work that has already retired, so that refusal is
/// not a failure; a callback that throws is.
let private requestCancellation (name: string) (source: CancellationTokenSource) =
    // A disposed source does not throw here; it returns a faulted task, filtered below.
    source
        .CancelAsync()
        .ContinueWith(
            (fun (cancelled: Task) ->
                let refusals =
                    cancelled.Exception.Flatten().InnerExceptions
                    |> Seq.filter (fun failure -> not (failure :? ObjectDisposedException))
                    |> List.ofSeq

                if not (List.isEmpty refusals) then
                    Logging.error name $"a work cancellation callback failed: %O{AggregateException refusals}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        )
    |> ignore

/// Declare that the caller is entering ONE long unit of work that will finish no plugin
/// event while it runs — a first-run impact attribution, a cold discovery — and bound it.
///
/// The stall detector cannot tell a fold that is merely slow from one that will never
/// return, because the two look identical: work owned, nothing `Running`, the host's
/// completed-event counter still. What separates them is not how they look but what will
/// happen next, and only the caller knows that. So it says so, and pays for saying it with
/// a deadline: while the declaration is live and inside `deadline` the work counts as
/// progress; when the deadline expires the operation's own failure is recorded, it stops
/// counting, and the detector names it exactly as it names an undeclared stall.
///
/// Declaring nothing is therefore the SAFE default, not a loophole: an undeclared fold is
/// still caught at the detector's own threshold. A declaration can only move the bound for
/// the region it wraps, and only to a bound it states out loud.
///
/// Dispose to end it. Disposal is idempotent.
let declare
    (store: PluginWorkOwner.Store)
    (schedule: TimeSpan -> (unit -> unit) -> IDisposable)
    (name: string)
    (deadline: TimeSpan)
    : IDisposable =
    requireBounded deadline
    let identity = store.BeginOperation(name, true)

    let timer =
        schedule deadline (fun () ->
            store.FailOperation(
                identity,
                TimeoutException(
                    $"declared bounded work '%s{name}' overran its %O{deadline} deadline without returning"
                )
            )
            |> ignore)

    let mutable ended = 0

    { new IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&ended, 1) = 0 then
                // Disarm first: a callback that fires after the operation is retired
                // cannot reopen it, and `FailOperation` refuses it.
                timer.Dispose()
                store.EndOperation identity }

/// Run one piece of external work under a finite deadline and a child-process scope.
///
/// - `schedule` arms the deadline before `work` is invoked, so a callback that blocks
///   before returning its computation is still covered. A scheduler that fails settles
///   the operation with that failure and never invokes `work`.
/// - When the deadline expires, `onDeadline` records it and the work's token is
///   cancelled. A failing `onDeadline` is logged and cancellation still happens.
/// - `finish` receives the outcome after the work and its children are torn down, while
///   the deadline is still armed. It is given the deadline's release, so completion
///   notification stays covered by the deadline until `finish` releases it.
/// - A work token cancelled for any reason refuses a successful outcome.
let execute
    (name: string)
    (deadline: TimeSpan)
    (schedule: TimeSpan -> (unit -> unit) -> IDisposable)
    (onDeadline: exn -> unit)
    (ct: CancellationToken)
    (work: CancellationToken -> Async<'Result>)
    (finish: Result<'Result, exn> -> (unit -> unit) -> 'Finished)
    : Async<'Finished> =
    async {
        requireBounded deadline
        use cancellation = CancellationTokenSource.CreateLinkedTokenSource ct

        let expire () =
            try
                onDeadline (TimeoutException $"%s{name} exceeded its %O{deadline} work deadline")
            with failure ->
                Logging.error name $"recording the work deadline failed: %O{failure}"

            requestCancellation name cancellation

        let armed =
            try
                Ok(schedule deadline expire)
            with failure ->
                Error failure

        match armed with
        | Error failure -> return finish (Error failure) ignore
        | Ok timer ->
            // `finish` may release the deadline and the `finally` releases it again.
            let released = ref 0

            let release () =
                if Interlocked.Exchange(&released.contents, 1) = 0 then
                    timer.Dispose()

            try
                let! outcome =
                    async {
                        try
                            let! result =
                                ProcessRegistry.withChildScopeAsync
                                    cancellation.Token
                                    (async {
                                        cancellation.Token.ThrowIfCancellationRequested()
                                        let! result = work cancellation.Token
                                        cancellation.Token.ThrowIfCancellationRequested()
                                        return result
                                    })

                            return Ok result
                        with failure ->
                            return Error failure
                    }

                return finish outcome release
            finally
                release ()
    }

[<NoComparison; NoEquality>]
type private Request<'Request> =
    { Id: WorkId
      Value: 'Request
      Cancellation: CancellationTokenSource
      Receipt: TaskCompletionSource<unit> }

/// Where a queue's work is. `queued` requests wait behind `active`, oldest first.
[<NoComparison; NoEquality>]
type private Phase<'Request> =
    /// Nothing is admitted.
    | Resting
    /// `active`'s callback is live.
    | Running of active: Request<'Request> * queued: Request<'Request> list
    /// `active`'s callback returned. Its result is published; its completion notification
    /// and deadline teardown are still owned.
    | Finishing of active: Request<'Request> * queued: Request<'Request> list

[<NoComparison; NoEquality>]
type private Core<'State, 'Request> =
    {
        State: 'State
        Phase: Phase<'Request>
        Closed: bool
        Completed: int64
        /// The last attempt's failure, or a deadline recorded against the active request.
        /// A new attempt clears it.
        Failure: exn option
    }

let private queueOf phase =
    match phase with
    | Resting -> None, []
    | Running(active, queued)
    | Finishing(active, queued) -> Some active, queued

let private withQueue queued phase =
    match phase with
    | Resting -> Resting
    | Running(active, _) -> Running(active, queued)
    | Finishing(active, _) -> Finishing(active, queued)

/// The request `id` and the requests queued behind it, while `id` is the active request.
/// Refused for any other identity, so a late or foreign callback cannot change ownership.
let private live (name: string) (id: WorkId) phase =
    match phase with
    | Running(active, queued)
    | Finishing(active, queued) when active.Id = id -> active, queued
    | _ -> invalidOp $"%s{name}: the work is not live under this identity; it is foreign or already retired"

/// A serial queue of external work.
///
/// - `beginWork` runs inside the store's writer when a request becomes active, and its
///   state is published with the admission. A `beginWork` that throws refuses the
///   admission; one that throws while handing the queue to a successor stops the queue.
/// - `work` receives the published state, the request, a token and a `publish` that
///   replaces the state while the request is live.
/// - `failed` folds a failure into the state.
/// - `completed` is notified after a successful result is published and before the
///   request retires. A failing notification fails the request.
type Queue<'State, 'Request>
    (
        store: Store,
        name: string,
        initial: 'State,
        deadline: TimeSpan,
        beginWork: 'State -> 'Request -> 'State,
        failed: 'State -> exn -> 'State,
        completed: 'State -> unit,
        work: 'State -> 'Request -> CancellationToken -> ('State -> unit) -> Async<'State>,
        ?scheduleDeadline: TimeSpan -> (unit -> unit) -> IDisposable
    ) =
    do requireBounded deadline

    // Requests arrive from RPC handlers, watcher threads and tests, each with its own
    // execution context. Workers run in the context the queue was constructed in, so a
    // child process a request spawns registers with the owner's process registry rather
    // than the caller's.
    let ownerContext = ExecutionContext.Capture()
    let scheduleDeadline = defaultArg scheduleDeadline defaultScheduler

    let handle =
        store.Register(
            name,
            { State = initial
              Phase = Resting
              Closed = false
              Completed = 0L
              Failure = None },
            fun core ->
                RowStatus.ofSupervisedWork
                    (fst (queueOf core.Phase)).IsSome
                    core.Completed
                    (core.Failure |> Option.map OperationFailure)
        )

    let change transition =
        store.Change(handle, fun _ core -> transition core)

    let launch (body: unit -> unit) : Task =
        match ownerContext with
        | null -> Task.Run(Action body)
        | context ->
            let launched = ref Task.CompletedTask

            ExecutionContext.Run(
                context.CreateCopy(),
                ContextCallback(fun _ -> launched.Value <- Task.Run(Action body)),
                null
            )

            launched.Value

    let publish (id: WorkId) (state: 'State) =
        change (fun core ->
            live name id core.Phase |> ignore
            { core with State = state }, ())

    // Runs on the deadline's timer. A writer slower than the bound keeps the transition;
    // the timeout it raises here is logged by `execute`, which still cancels the work.
    let markDeadline (request: Request<'Request>) (failure: exn) =
        task {
            let! marked =
                store.ChangeAsync(
                    handle,
                    fun _ core ->
                        match core.Phase with
                        | Running(active, _)
                        | Finishing(active, _) when active.Id = request.Id -> { core with Failure = Some failure }, true
                        | _ -> core, false
                )

            if marked then
                Logging.error name failure.Message
        }
        |> waitWithin AdmissionBound

    // A worker task can only fault inside `finish`, before its request retires: a
    // `failed` or successor `beginWork` that throws in the writer. Nothing runs under the
    // request any more, so it and everything queued behind it settle with the failure,
    // and the queue admits nothing further.
    let faultWorker (request: Request<'Request>) (failure: exn) =
        Logging.error name $"the supervised worker failed and closed its queue: %O{failure}"

        let retired =
            change (fun core ->
                let active, queued = live name request.Id core.Phase

                { core with
                    Phase = Resting
                    Closed = true
                    Failure = Some failure },
                active :: queued)

        for pending in retired do
            pending.Receipt.TrySetException failure |> ignore
            pending.Cancellation.Dispose()

    let rec start (request: Request<'Request>) (state: 'State) =
        let finish (outcome: Result<'State, exn>) (releaseDeadline: unit -> unit) =
            let delivered =
                change (fun core ->
                    let active, queued = live name request.Id core.Phase

                    let delivered =
                        match core.Failure with
                        | Some deadlineFailure -> Error deadlineFailure
                        | None -> outcome

                    let state =
                        match delivered with
                        | Ok state -> state
                        | Error failure -> failed core.State failure

                    { core with
                        State = state
                        Phase = Finishing(active, queued) },
                    delivered)

            let notified =
                delivered
                |> Result.bind (fun state ->
                    try
                        completed state
                        Ok state
                    with failure ->
                        Logging.error name $"the completion notification failed: %O{failure}"
                        Error failure)

            let settled =
                try
                    releaseDeadline ()
                    notified
                with failure ->
                    Logging.error name $"releasing the work deadline failed: %O{failure}"
                    notified |> Result.bind (fun _ -> Error failure)

            // Retirement and the successor's admission are one publication, and both
            // precede the predecessor's receipt.
            let successor, final =
                change (fun core ->
                    let _, queued = live name request.Id core.Phase

                    let final =
                        match core.Failure with
                        | Some deadlineFailure -> Error deadlineFailure
                        | None -> settled

                    let state =
                        match delivered, final with
                        | Ok _, Error failure -> failed core.State failure
                        | _ -> core.State

                    let retired =
                        { core with
                            State = state
                            Completed = core.Completed + 1L
                            Failure =
                                match final with
                                | Ok _ -> None
                                | Error failure -> Some failure }

                    match queued with
                    | [] -> { retired with Phase = Resting }, (None, final)
                    | next :: rest ->
                        let begun = beginWork state next.Value

                        { retired with
                            State = begun
                            Phase = Running(next, rest)
                            Failure = None },
                        (Some(next, begun), final))

            match final with
            | Ok _ -> request.Receipt.TrySetResult(()) |> ignore
            | Error failure -> request.Receipt.TrySetException failure |> ignore

            request.Cancellation.Dispose()
            successor |> Option.iter (fun (next, begun) -> start next begun)

        let running =
            launch (fun () ->
                execute
                    name
                    deadline
                    scheduleDeadline
                    (markDeadline request)
                    request.Cancellation.Token
                    (fun token -> work state request.Value token (publish request.Id))
                    finish
                |> Async.RunSynchronously)

        running.ContinueWith(
            (fun (faulted: Task) -> faultWorker request (faulted.Exception.GetBaseException())),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        )
        |> ignore

    /// The published state.
    member _.State: 'State = (store.Read handle).State

    /// Admit `value`. The result resolves once the admission is published: `Ok` carries
    /// the request's receipt, `Error` the refusal (a closed queue, a throwing `beginWork`).
    /// Cancelling `ct` cancels the request's work.
    member _.SubmitAsync(value: 'Request, ct: CancellationToken) : Task<Result<Task<unit>, exn>> =
        ct.ThrowIfCancellationRequested()
        let cancellation = CancellationTokenSource.CreateLinkedTokenSource ct

        let receipt =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        task {
            try
                let! launched =
                    store.ChangeAsync(
                        handle,
                        fun fresh core ->
                            if core.Closed then
                                raise (ObjectDisposedException name)

                            let request =
                                { Id = fresh
                                  Value = value
                                  Cancellation = cancellation
                                  Receipt = receipt }

                            match queueOf core.Phase with
                            | None, _ ->
                                let begun = beginWork core.State value

                                { core with
                                    State = begun
                                    Phase = Running(request, [])
                                    Failure = None },
                                Some(request, begun)
                            | Some _, queued ->
                                { core with
                                    Phase = withQueue (queued @ [ request ]) core.Phase },
                                None
                    )

                launched |> Option.iter (fun (request, begun) -> start request begun)
                return Ok receipt.Task
            with failure ->
                cancellation.Dispose()
                return Error failure
        }

    /// `SubmitAsync`, waiting at most `AdmissionBound` for the admission and raising its
    /// refusal. A timed-out caller does not withdraw the request.
    member this.Submit(value: 'Request, ct: CancellationToken) : Task<unit> =
        match this.SubmitAsync(value, ct) |> waitWithin AdmissionBound with
        | Ok receipt -> receipt
        | Error failure -> raise failure

    /// Close admission. Queued requests settle with `ObjectDisposedException`; the active
    /// callback is asked to cancel and stays owned until it actually returns.
    member _.Close() =
        let closing =
            task {
                let! active, queued =
                    store.ChangeAsync(
                        handle,
                        fun _ core ->
                            { core with
                                Closed = true
                                Phase = withQueue [] core.Phase },
                            queueOf core.Phase
                    )

                for request in queued do
                    request.Receipt.TrySetException(ObjectDisposedException name) |> ignore
                    request.Cancellation.Dispose()

                active
                |> Option.iter (fun request -> requestCancellation name request.Cancellation)
            }

        closing |> waitWithin AdmissionBound
