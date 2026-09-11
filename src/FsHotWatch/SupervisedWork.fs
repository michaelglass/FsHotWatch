/// Serial external work whose admission, state and queued handoffs share the host publication.
module internal FsHotWatch.SupervisedWork

open System
open System.Threading
open System.Threading.Tasks

let defaultDeadline = TimeSpan.FromMinutes 60.0

let resolveDeadline (overrideSeconds: string option) =
    match overrideSeconds |> Option.bind (fun value -> match Int32.TryParse value with true, n when n > 0 -> Some n | _ -> None) with
    | Some seconds -> TimeSpan.FromSeconds(float seconds)
    | None -> defaultDeadline

let ambientDeadline () =
    Environment.GetEnvironmentVariable "FSHW_VERDICT_DEADLINE_SEC" |> Option.ofObj |> resolveDeadline

let defaultScheduler delay expire =
    new Timer((fun _ -> expire ()), null, delay, Timeout.InfiniteTimeSpan) :> IDisposable

/// One external execution boundary for scans, batches, preprocessors and exclusive
/// plugin workers. Deadline cancellation never retires the admitted identity;
/// only finish may transfer it, after actual work and child cleanup have returned.
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
        if deadline <= TimeSpan.Zero || deadline = TimeSpan.MaxValue then
            invalidArg (nameof deadline) "Supervised work needs a finite positive deadline"

        use cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct)
        let timer =
            try
                Ok(schedule deadline (fun () ->
                    try
                        onDeadline (TimeoutException($"{name} exceeded its {deadline} work deadline"))
                    with failure ->
                        Logging.error name $"deadline publication failed: {failure}"

                    try
                        cancellation.CancelAsync().ContinueWith(
                            (fun (result: Task) -> Logging.error name $"work cancellation callback failed: {result.Exception}"),
                            TaskContinuationOptions.OnlyOnFaulted) |> ignore
                    with :? ObjectDisposedException -> ()))
            with failure -> Result.Error failure

        match timer with
        | Result.Error failure -> return finish (Result.Error failure) ignore
        | Ok timer ->
            use timer = timer
            return!
                ProcessRegistry.withChildScopeAsync cancellation.Token (fun settleChildren ->
                    async {
                        let! outcome =
                            async {
                                try
                                    cancellation.Token.ThrowIfCancellationRequested()
                                    let! result = work cancellation.Token
                                    cancellation.Token.ThrowIfCancellationRequested()
                                    return Ok result
                                with failure -> return Result.Error failure
                            }
                        return finish outcome settleChildren
                    })
    }

[<NoComparison; NoEquality>]
type private Request<'Request> =
    { Id: Guid
      Value: 'Request
      Cancellation: CancellationTokenSource
      Receipt: TaskCompletionSource<unit> }

[<NoComparison; NoEquality>]
type private Phase<'Request> =
    | Resting
    | Running of Request<'Request> * Request<'Request> list
    | Finishing of Request<'Request> * Request<'Request> list

[<NoComparison; NoEquality>]
type private Core<'State, 'Request> =
    { State: 'State
      Phase: Phase<'Request>
      Closed: bool
      Completed: int64
      Failure: exn option }

/// Deadline requests cancellation and records failure; it never pretends a
/// noncooperative callback stopped. Only its real completion retires the work.
type Queue<'State, 'Request>
    (
        store: PluginWorkOwner.Store,
        name: string,
        initial: 'State,
        deadline: TimeSpan,
        beginWork: 'State -> 'Request -> 'State,
        failed: 'State -> exn -> 'State,
        completed: 'State -> unit,
        work: 'State -> 'Request -> CancellationToken -> ('State -> unit) -> Async<'State>,
        ?scheduleDeadline: (TimeSpan -> (unit -> unit) -> IDisposable)
    ) =
    do
        if deadline <= TimeSpan.Zero || deadline = TimeSpan.MaxValue then
            invalidArg (nameof deadline) "Supervised work needs a finite positive deadline"

    // The daemon installs its process registry before constructing owners.
    // Requests may arrive from unrelated RPC/test execution contexts: launches
    // must retain the owner's registry, not inherit the requesting caller's.
    let ownerContext = ExecutionContext.Capture()

    let launch (body: unit -> unit) : Task =
        if isNull ownerContext then
            Task.Run(Action body)
        else
            let mutable launched: Task option = None

            ExecutionContext.Run(
                ownerContext.CreateCopy(),
                ContextCallback(fun _ -> launched <- Some(Task.Run(Action body))),
                null
            )

            launched
            |> Option.defaultWith (fun () -> invalidOp "Owner context did not launch its worker")

    let scheduleDeadline = defaultArg scheduleDeadline defaultScheduler

    let project (core: Core<'State, 'Request>) : PluginWorkOwner.Row =
        { Name = name
          Value = box core
          Busy =
            match core.Phase with
            | Resting -> false
            | Running _
            | Finishing _ -> true
          Completed = core.Completed
          Failure = core.Failure |> Option.map PluginWorkOwner.OperationFailure
          Evidence = None }

    let rowId =
        store.Register(
            project
                { State = initial
                  Phase = Resting
                  Closed = false
                  Completed = 0L
                  Failure = None }
        )

    let read () =
        unbox<Core<'State, 'Request>> (store.Read(rowId).Value)

    let mutateAsync (change: Core<'State, 'Request> -> Core<'State, 'Request> * 'Result) : Task<'Result> =
        store.ChangeAsync(
            rowId,
            fun row ->
                let next, result = change (unbox<Core<'State, 'Request>> row.Value)
                project next, result
        )

    // Internal settlement stays attached to actual publication. A caller's
    // timeout must never discard a launch, receipt or cancellation effect.
    let mutate change =
        (mutateAsync change).GetAwaiter().GetResult()

    let cancel (request: Request<'Request>) =
        try
            request.Cancellation
                .CancelAsync()
                .ContinueWith(
                    (fun (result: Task) -> Logging.error name $"work cancellation callback failed: {result.Exception}"),
                    TaskContinuationOptions.OnlyOnFaulted
                )
            |> ignore
        with :? ObjectDisposedException ->
            // Completion can dispose the token after Close captured the active
            // request but before this post-publication cancellation effect runs.
            ()

    let publish id state =
        mutate (fun core ->
            match core.Phase with
            | Running(current, _) when current.Id = id -> { core with State = state }, ()
            | _ -> invalidOp "Work publication is foreign or already completed")

    let markDeadline (request: Request<'Request>) (failure: exn) =

        task {
            try
                let! active =
                    mutateAsync (fun core ->
                        match core.Phase with
                        | Running(current, _)
                        | Finishing(current, _) when current.Id = request.Id ->
                            { core with Failure = Some failure }, true
                        | _ -> core, false)

                if active then
                    Logging.error name failure.Message
                    cancel request
            with publicationFailure ->
                Logging.error name $"deadline publication failed: {publicationFailure}"
                cancel request
        }
        // Normal publication is immediate; retain the existing observable
        // deadline acknowledgement while a late publication keeps its effects.
        |> fun pending -> pending.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

    let failExecutor (request: Request<'Request>) (failure: exn) =
        Logging.error name $"work owner transition failed: {failure}"

        try
            let retained, retired =
                mutate (fun core ->
                    let phase, retained, retired =
                        match core.Phase with
                        | Resting -> Resting, None, []
                        | Running(current, queued)
                        | Finishing(current, queued) when current.Id = request.Id ->
                            // This worker task has already faulted: no callback is
                            // still running under its identity.
                            Resting, None, current :: queued
                        | Running(current, queued) -> Running(current, []), Some current, queued
                        | Finishing(current, queued) -> Finishing(current, []), Some current, queued

                    { core with
                        Closed = true
                        Failure = Some failure
                        Phase = phase },
                    (retained, retired))

            for pending in retired do
                pending.Receipt.TrySetException(failure) |> ignore
                pending.Cancellation.Dispose()

            retained |> Option.iter cancel
        with publicationFailure ->
            // A timed-out writer may still apply the queued failure transition.
            // Never infer that an unrelated live obligation can be cleared.
            Logging.error name $"work owner failure publication failed: {publicationFailure}"

        request.Receipt.TrySetException(failure) |> ignore

    let rec start (request: Request<'Request>, state: 'State) =
        let finish outcome beforeRetire =
            let delivered =
                mutate (fun core ->
                    match core.Phase with
                    | Running(current, queued) when current.Id = request.Id ->
                        let delivered =
                            match core.Failure, outcome with
                            | Some failure, _ -> Result.Error failure
                            | None, result -> result

                        let state =
                            match delivered with
                            | Ok state -> state
                            | Result.Error failure -> failed core.State failure

                        { core with
                            State = state
                            Phase = Finishing(current, queued) },
                        delivered
                    | _ -> invalidOp "Work completion is foreign or already completed")

            let notified =
                try
                    match delivered with
                    | Ok state ->
                        completed state
                        delivered
                    | Result.Error _ -> delivered
                with failure ->
                    Logging.error name $"work completion notification failed: {failure}"
                    Result.Error failure

            let notified =
                try
                    beforeRetire ()
                    notified
                with failure ->
                    Logging.error name $"work child teardown failed: {failure}"
                    Result.Error failure

            let next, delivered =
                mutate (fun core ->
                    match core.Phase with
                    | Finishing(current, queued) when current.Id = request.Id ->
                        let delivered =
                            match core.Failure, notified with
                            | Some failure, _ -> Result.Error failure
                            | None, Ok _ when core.Closed ->
                                Result.Error(OperationCanceledException(request.Cancellation.Token) :> exn)
                            | None, result -> result

                        let settled =
                            match delivered with
                            | Ok state -> state
                            | Result.Error failure -> failed core.State failure

                        let nextState, phase, next =
                            match queued with
                            | [] -> settled, Resting, None
                            | next :: rest -> beginWork settled next.Value, Running(next, rest), Some(next, settled)

                        { core with
                            State = nextState
                            Phase = phase
                            Completed = core.Completed + 1L
                            Failure =
                                match next, delivered with
                                | Some _, _
                                | _, Ok _ -> None
                                | None, Result.Error failure -> Some failure },
                        (next, delivered)
                    | _ -> invalidOp "Work retirement is foreign or already completed")
            // Notification and cleanup stay owned; successor admission and final
            // state publication precede release of the predecessor's receipt.
            match delivered with
            | Ok _ -> request.Receipt.TrySetResult(()) |> ignore
            | Result.Error failure -> request.Receipt.TrySetException(failure) |> ignore

            request.Cancellation.Dispose()
            next |> Option.iter start

        try
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
                    |> fun operation -> Async.RunSynchronously(operation, cancellationToken = CancellationToken.None))

            running.ContinueWith(
                (fun (task: Task) ->
                    let failure =
                        if task.IsCanceled then
                            TaskCanceledException(task) :> exn
                        else
                            task.Exception.GetBaseException()

                    failExecutor request failure),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion,
                TaskScheduler.Default
            )
            |> ignore
        with failure ->
            Logging.error name $"supervised work failed to start: {failure}"

            try
                finish (Result.Error failure) ignore
            with transitionFailure ->
                failExecutor request transitionFailure

    member _.State = (read ()).State

    member _.SubmitAsync(value: 'Request, ct: CancellationToken) : Task<Result<Task<unit>, exn>> =
        ct.ThrowIfCancellationRequested()

        let request =
            { Id = Guid.NewGuid()
              Value = value
              Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct)
              Receipt = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously) }

        let scheduled =
            task {
                try
                    let! launch =
                        mutateAsync (fun core ->
                            if core.Closed then
                                raise (ObjectDisposedException(name))

                            match core.Phase with
                            | Resting ->
                                { core with
                                    State = beginWork core.State value
                                    Phase = Running(request, [])
                                    Failure = None },
                                Some(request, core.State)
                            | Running(current, queued) ->
                                { core with
                                    Phase = Running(current, queued @ [ request ]) },
                                None
                            | Finishing(current, queued) ->
                                { core with
                                    Phase = Finishing(current, queued @ [ request ]) },
                                None)

                    launch |> Option.iter start
                    return Ok request.Receipt.Task
                with failure ->
                    request.Cancellation.Dispose()
                    return Result.Error failure
            }

        scheduled

    member this.Submit(value: 'Request, ct: CancellationToken) : Task<unit> =
        match this.SubmitAsync(value, ct).WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult() with
        | Ok receipt -> receipt
        | Result.Error failure -> raise failure

    member _.Close() =
        let closing =
            task {
                try
                    let! active, queued =
                        mutateAsync (fun core ->
                            match core.Phase with
                            | Resting -> { core with Closed = true }, (None, [])
                            | Running(current, queued) ->
                                { core with
                                    Closed = true
                                    Phase = Running(current, []) },
                                (Some current, queued)
                            | Finishing(current, queued) ->
                                { core with
                                    Closed = true
                                    Phase = Finishing(current, []) },
                                (Some current, queued))

                    for request in queued do
                        request.Receipt.TrySetException(ObjectDisposedException(name)) |> ignore
                        request.Cancellation.Dispose()

                    active |> Option.iter cancel
                    return Ok()
                with failure ->
                    Logging.error name $"closing work admission failed: {failure}"
                    return Result.Error failure
            }

        match closing.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult() with
        | Ok() -> ()
        | Result.Error failure -> raise failure
