/// Seeded histories of `SupervisedWork.Queue` on the deterministic runtime (AUTOMATION-1695).
///
/// A history submits requests whose work completes, fails, hangs until cancelled, or takes a
/// while in virtual time (sometimes longer than the deadline), and whose `beginWork` may throw;
/// it closes the queue, lets ready work run, and advances virtual time to the next timer. The
/// seed decides every interleaving: when the store's writer runs, which ready item runs next,
/// and so when each worker, deadline timer, cancellation and continuation runs. After every
/// operation, and once more at quiescence:
///
///   * SERIAL: at most one request's work runs at a time, and work starts in admission order.
///   * HONEST REST: while an admitted request's receipt is unsettled, the host is busy.
///   * OUTCOME: a receipt settles once, and as its request's behaviour allows: success only for
///     work that returned within its deadline; `TimeoutException` only for work that overran;
///     the work's own failure only for work that failed; `ObjectDisposedException` only for a
///     request whose work never started; cancellation only after a close.
///   * SETTLEMENT (at quiescence): every admitted receipt has settled, and nothing is busy.
///   * STATE (at quiescence): the queue's state holds exactly the requests that succeeded, in
///     admission order.
///   * RUNTIME: no deadlock, no escape (every callback runs on the simulation's thread),
///     quiescence reached.
module FsHotWatch.Tests.SupervisedWorkSimulation

open System
open System.Threading
open System.Threading.Tasks
open FsHotWatch
open FsHotWatch.PluginWorkOwner
open FsHotWatch.Tests.SimRuntime

/// What a request's work does.
[<RequireQualifiedAccess>]
type Behaviour =
    | Completes
    | Fails
    /// Runs until its token is cancelled.
    | Hangs
    /// Returns after `seconds` of virtual time, unless cancelled first.
    | Slow of seconds: int
    /// Its `beginWork` throws, refusing its admission or stopping the queue.
    | BeginThrows

[<RequireQualifiedAccess>]
type Op =
    | Submit of value: int * behaviour: Behaviour * synchronous: bool
    | Close
    | Step of count: int
    | Advance

[<RequireQualifiedAccess>]
type Mutant =
    /// A runtime whose timers never fire: a deadline that never expires.
    | TimerLost
    /// A runtime whose cancellation requests do nothing.
    | CancelLost
    /// Two consecutive submissions admitted in the opposite order to their callers.
    | SubmitOrderSwapped

[<RequireQualifiedAccess>]
type Subject =
    | Current
    | Mutated of Mutant

[<RequireQualifiedAccess>]
type Theorem =
    | Serial
    | HonestRest
    | Outcome
    | Settlement
    | State
    | Runtime

type Violation =
    { Seed: int
      Step: int
      Theorem: Theorem
      Detail: string
      History: Op list
      Choices: Choice list }

/// How often each await arm the pool reaches only by chance was reached.
type Census =
    {
        /// An admission whose change published before `SubmitAsync` returned.
        AdmittedAlready: int
        /// An admission still pending when `SubmitAsync` returned.
        AdmittedLater: int
        /// A deadline that expired while its work ran.
        DeadlinesExpired: int
    }

module Census =
    let zero =
        { AdmittedAlready = 0
          AdmittedLater = 0
          DeadlinesExpired = 0 }

    let add (a: Census) (b: Census) =
        { AdmittedAlready = a.AdmittedAlready + b.AdmittedAlready
          AdmittedLater = a.AdmittedLater + b.AdmittedLater
          DeadlinesExpired = a.DeadlinesExpired + b.DeadlinesExpired }

exception WorkFailureException of value: int
exception BeginFailureException of value: int

[<Literal>]
let HistoryLength = 30

/// The queue's deadline, in virtual seconds.
[<Literal>]
let DeadlineSeconds = 10

let private behaviourText (b: Behaviour) =
    match b with
    | Behaviour.Completes -> "completes"
    | Behaviour.Fails -> "fails"
    | Behaviour.Hangs -> "hangs"
    | Behaviour.Slow s -> $"takes %d{s} s"
    | Behaviour.BeginThrows -> "begin throws"

let opText (op: Op) : string =
    match op with
    | Op.Submit(value, behaviour, synchronous) ->
        let how = if synchronous then "submit" else "submit async"
        $"%s{how} %d{value} (%s{behaviourText behaviour})"
    | Op.Close -> "close"
    | Op.Step count -> $"step %d{count}"
    | Op.Advance -> "advance to the next timer"

let generate (seed: int) (length: int) : Op list =
    let rng = Random seed
    let mutable value = 0

    let behaviour () =
        let roll = rng.NextDouble()

        if roll < 0.40 then
            Behaviour.Completes
        elif roll < 0.55 then
            Behaviour.Fails
        elif roll < 0.70 then
            Behaviour.Hangs
        elif roll < 0.90 then
            Behaviour.Slow(1 + rng.Next(2 * DeadlineSeconds))
        else
            Behaviour.BeginThrows

    [ for _ in 1..length do
          let roll = rng.NextDouble()

          if roll < 0.45 then
              value <- value + 1
              Op.Submit(value, behaviour (), rng.NextDouble() < 0.3)
          elif roll < 0.75 then
              Op.Step(1 + rng.Next 4)
          elif roll < 0.95 then
              Op.Advance
          else
              Op.Close ]

/// What became of one admitted request's receipt.
[<NoComparison; NoEquality>]
type private Admitted =
    { Value: int
      Behaviour: Behaviour
      Receipt: Task<unit> }

let private listText (values: int list) =
    "[" + (values |> List.map string |> String.concat "; ") + "]"

let execute (subject: Subject) (seed: int) (history: Op list) : Violation list * Census =
    let sim = Sim(seed ^^^ 0x5EED)

    let runtime =
        match subject with
        | Subject.Mutated Mutant.TimerLost ->
            { sim.Runtime with
                Schedule =
                    fun _ _ ->
                        { new IDisposable with
                            member _.Dispose() = () } }
        | Subject.Mutated Mutant.CancelLost ->
            { sim.Runtime with
                Cancel = fun _ -> Task.CompletedTask }
        | Subject.Current
        | Subject.Mutated Mutant.SubmitOrderSwapped -> sim.Runtime

    use _ = sim.Install()
    let store = Store runtime
    let behaviours = Collections.Generic.Dictionary<int, Behaviour>()
    let started = Collections.Generic.List<int>()
    // Requests whose work returned a result.
    let returned = Collections.Generic.List<int>()
    let admitted = Collections.Generic.List<Admitted>()
    let violations = Collections.Generic.List<Violation>()
    let mutable running = 0
    let mutable closed = false
    let mutable census = Census.zero
    let mutable step = 0
    let mutable held: (int * Behaviour) option = None
    // Asynchronous admissions not yet published.
    let mutable pending = 0

    let violate (theorem: Theorem) (detail: string) =
        violations.Add
            { Seed = seed
              Step = step
              Theorem = theorem
              Detail = detail
              History = history
              Choices = sim.Choices }

    let beginWork (state: int list) (value: int) =
        sim.CheckOwner "beginWork ran off the simulation's thread"

        match behaviours[value] with
        | Behaviour.BeginThrows -> raise (BeginFailureException value)
        | Behaviour.Completes
        | Behaviour.Fails
        | Behaviour.Hangs
        | Behaviour.Slow _ -> state

    // Returns once `token` is cancelled, or after `seconds` of virtual time when given.
    let waitFor (token: CancellationToken) (seconds: int option) : Async<bool> =
        async {
            let finished =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            use _ = token.Register(fun () -> finished.TrySetResult false |> ignore)

            use _ =
                match seconds with
                | Some s ->
                    runtime.Schedule (TimeSpan.FromSeconds(float s)) (fun () -> finished.TrySetResult true |> ignore)
                | None ->
                    { new IDisposable with
                        member _.Dispose() = () }

            return! Async.AwaitTask finished.Task
        }

    let work (state: int list) (value: int) (token: CancellationToken) (publish: int list -> unit) =
        async {
            sim.CheckOwner "work ran off the simulation's thread"
            started.Add value
            running <- running + 1

            if running > 1 then
                violate Theorem.Serial $"%d{value} started while another request's work ran"

            try
                match behaviours[value] with
                | Behaviour.Completes ->
                    returned.Add value
                    return value :: state
                | Behaviour.Fails -> return raise (WorkFailureException value)
                | Behaviour.Hangs ->
                    let! _ = waitFor token None
                    token.ThrowIfCancellationRequested()
                    return state
                | Behaviour.Slow seconds ->
                    publish state
                    let! finished = waitFor token (Some seconds)
                    token.ThrowIfCancellationRequested()

                    if not finished then
                        raise (OperationCanceledException token)

                    returned.Add value
                    return value :: state
                | Behaviour.BeginThrows -> return failwith "a request whose beginWork throws never runs"
            finally
                running <- running - 1
        }

    let queue =
        SupervisedWork.Queue(
            store,
            "simulated",
            ([]: int list),
            TimeSpan.FromSeconds(float DeadlineSeconds),
            beginWork,
            (fun state _ -> state),
            (fun _ -> sim.CheckOwner "completed ran off the simulation's thread"),
            work
        )

    let submit (value: int) (behaviour: Behaviour) (synchronous: bool) =
        // Any earlier beginWork that throws may have stopped the queue.
        let mayBeStopped =
            closed
            || behaviours
               |> Seq.exists (fun kv -> kv.Key <> value && kv.Value = Behaviour.BeginThrows)

        behaviours[value] <- behaviour

        let accepted (receipt: Task<unit>) =
            admitted.Add
                { Value = value
                  Behaviour = behaviour
                  Receipt = receipt }

        let refused (failure: exn) =
            match failure with
            | BeginFailureException v when v = value -> ()
            | :? ObjectDisposedException when mayBeStopped -> ()
            | other -> violate Theorem.Outcome $"the admission of %d{value} was refused with %O{other}"

        if synchronous then
            match
                (try
                    Ok(queue.Submit(value, CancellationToken.None))
                 with failure ->
                     Error failure)
            with
            | Ok receipt -> accepted receipt
            | Error failure -> refused failure
        else
            let admission = queue.SubmitAsync(value, CancellationToken.None)

            census <-
                if admission.IsCompleted then
                    { census with
                        AdmittedAlready = census.AdmittedAlready + 1 }
                else
                    { census with
                        AdmittedLater = census.AdmittedLater + 1 }

            pending <- pending + 1

            let settle () =
                pending <- pending - 1

                match admission.Result with
                | Ok receipt -> accepted receipt
                | Error failure -> refused failure

            if admission.IsCompleted then
                settle ()
            else
                admission.GetAwaiter().UnsafeOnCompleted(Action settle)

    let apply (op: Op) =
        match op with
        | Op.Submit(value, behaviour, synchronous) ->
            match subject, held with
            | Subject.Mutated Mutant.SubmitOrderSwapped, None ->
                // Recorded now, admitted after the next submission.
                held <- Some(value, behaviour)
                behaviours[value] <- behaviour
            | Subject.Mutated Mutant.SubmitOrderSwapped, Some(heldValue, heldBehaviour) ->
                held <- None
                submit value behaviour synchronous
                submit heldValue heldBehaviour synchronous
            | _ -> submit value behaviour synchronous
        | Op.Close ->
            closed <- true
            queue.Close()
        | Op.Step count ->
            for _ in 1..count do
                sim.Step() |> ignore
        | Op.Advance -> sim.AdvanceToNextTimer() |> ignore

    // The order the harness meant: submissions in history order, beginWork refusals aside.
    let intended () =
        history
        |> List.choose (fun op ->
            match op with
            | Op.Submit(value, _, _) -> Some value
            | Op.Close
            | Op.Step _
            | Op.Advance -> None)

    let checkSerialOrder () =
        let order = intended ()
        let ran = List.ofSeq started
        let positions = ran |> List.map (fun v -> List.findIndex ((=) v) order)

        if positions <> List.sort positions then
            violate Theorem.Serial $"work started in the order %s{listText ran}, not in admission order"

    let checkHonestRest () =
        let unsettled =
            admitted
            |> Seq.filter (fun a -> not a.Receipt.IsCompleted)
            |> Seq.map (fun a -> a.Value)
            |> List.ofSeq

        if not unsettled.IsEmpty && not store.Snapshot.IsBusy then
            violate Theorem.HonestRest $"the host is at rest while %s{listText unsettled} are unsettled"

    let checkOutcomes () =
        for a in admitted do
            if a.Receipt.IsCompleted then
                let ran = started.Contains a.Value

                let allowed, what =
                    match a.Receipt.Exception with
                    | null ->
                        let inTime =
                            match a.Behaviour with
                            | Behaviour.Completes -> true
                            // At exactly the deadline, the result and the deadline race: either may win.
                            | Behaviour.Slow s -> s <= DeadlineSeconds
                            | Behaviour.Fails
                            | Behaviour.Hangs
                            | Behaviour.BeginThrows -> false

                        ran && inTime && returned.Contains a.Value, "succeeded"
                    | failure ->
                        match failure.InnerException with
                        | WorkFailureException v ->
                            ran && v = a.Value && a.Behaviour = Behaviour.Fails, "failed with its work"
                        | :? TimeoutException ->
                            let overran =
                                match a.Behaviour with
                                | Behaviour.Hangs -> true
                                | Behaviour.Slow s -> s >= DeadlineSeconds
                                | Behaviour.Completes
                                | Behaviour.Fails
                                | Behaviour.BeginThrows -> false

                            ran && overran, "timed out"
                        | :? ObjectDisposedException -> not ran, "was refused as closed"
                        // A close may cancel an admitted request before its work begins.
                        | :? OperationCanceledException -> closed, "was cancelled"
                        | BeginFailureException b ->
                            behaviours[b] = Behaviour.BeginThrows, $"failed with %d{b}'s beginWork"
                        | other -> false, $"failed with %O{other}"

                if not allowed then
                    let how =
                        if ran then
                            "after its work started"
                        else
                            "with its work never started"

                    violate Theorem.Outcome $"%d{a.Value} (%s{behaviourText a.Behaviour}) %s{what} %s{how}"

    let checkRuntime () =
        match sim.Faults with
        | [] -> ()
        | faults -> violate Theorem.Runtime $"%A{faults}"

    let mutable remaining = history

    while violations.Count = 0 && not remaining.IsEmpty do
        step <- step + 1

        try
            apply remaining.Head
        with
        | SimDeadlockException what -> violate Theorem.Runtime what
        | failure -> violate Theorem.Runtime $"the step raised %O{failure}"

        if violations.Count = 0 then
            checkRuntime ()
            checkHonestRest ()
            checkOutcomes ()

        remaining <- remaining.Tail

    if violations.Count = 0 then
        step <- step + 1

        try
            held |> Option.iter (fun (value, behaviour) -> submit value behaviour false)
            sim.RunUntilQuiet()
        with
        | SimDeadlockException what -> violate Theorem.Runtime what
        | failure -> violate Theorem.Runtime $"quiescence raised %O{failure}"

        checkRuntime ()
        checkOutcomes ()
        checkSerialOrder ()

        let unsettled =
            admitted
            |> Seq.filter (fun a -> not a.Receipt.IsCompleted)
            |> Seq.map (fun a -> a.Value)
            |> List.ofSeq

        if not unsettled.IsEmpty || pending > 0 then
            violate
                Theorem.Settlement
                $"at rest %s{listText unsettled} are unsettled, and %d{pending} admissions unpublished"
        elif store.Snapshot.IsBusy then
            violate Theorem.Settlement "at rest, with every receipt settled, the host is busy"

        let order = intended ()

        // The state holds results only: each from a request whose work returned, in admission
        // order, and every request whose receipt succeeded. A returned result may be missing:
        // one that arrives as its deadline expires is discarded, whatever its receipt then says
        // (a successor's beginWork can stop the queue and fail it).
        let state = List.rev queue.State

        let succeeded =
            admitted
            |> Seq.filter (fun a -> a.Receipt.IsCompletedSuccessfully)
            |> Seq.map (fun a -> a.Value)
            |> List.ofSeq

        let positions = state |> List.map (fun v -> List.tryFindIndex ((=) v) order)

        if
            not (List.forall returned.Contains state)
            || positions <> List.sort positions
            || not (List.forall (fun v -> List.contains v state) succeeded)
        then
            violate
                Theorem.State
                $"the state is %s{listText state}; work returned for %s{listText (List.ofSeq returned)}, and %s{listText succeeded} succeeded"

        census <-
            { census with
                DeadlinesExpired =
                    census.DeadlinesExpired
                    + (admitted
                       |> Seq.filter (fun a ->
                           match a.Receipt.Exception with
                           | null -> false
                           | failure -> failure.InnerException :? TimeoutException)
                       |> Seq.length) }

        try
            queue.Close()
            sim.RunUntilQuiet()
        with failure ->
            violate Theorem.Runtime $"the final close raised %O{failure}"

    List.ofSeq violations, census

let explore (subject: Subject) (seeds: int list) (stopAtFirst: bool) : Violation list * Census =
    let mutable violations = []
    let mutable census = Census.zero
    let mutable remaining = seeds

    while not remaining.IsEmpty && not (stopAtFirst && not violations.IsEmpty) do
        let seed = remaining.Head
        let found, seen = execute subject seed (generate seed HistoryLength)
        violations <- violations @ found
        census <- Census.add census seen
        remaining <- remaining.Tail

    violations, census

let describe (v: Violation) : string =
    let lines =
        v.History
        |> List.mapi (fun i op ->
            let mark = if i + 1 = v.Step then " <-- here" else ""
            $"  %d{i + 1}. %s{opText op}%s{mark}")
        |> String.concat "\n"

    $"seed %d{v.Seed}, step %d{v.Step}: %A{v.Theorem}: %s{v.Detail}\n"
    + $"(%d{v.Choices.Length} runtime choices; replay with FSHW_SIM_SEED=%d{v.Seed} FSHW_SIM_HISTORIES=1)\n%s{lines}"
