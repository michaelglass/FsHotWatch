/// Seeded histories of `DebouncedWork.Queue` on the deterministic runtime (AUTOMATION-1695).
///
/// A history posts values with a debounce delay of 1..5 virtual seconds, or zero (a flush, a
/// barrier ordered after everything posted before it); it closes the input, lets ready work run,
/// and advances virtual time. Input coalesces by list append, so the cohorts the supervised
/// worker receives show exactly which posts were merged, in which order. The worker fails a
/// cohort holding a multiple of 7. The seed decides every interleaving: when the store's writer
/// runs, when each debounce timer fires, and when each admission runs. Checked after every
/// operation, and once more at quiescence:
///
///   * ORDER: the cohorts the worker received, concatenated, are the posted values in posting
///     order, each at most once.
///   * BARRIER: a flushed value is the last of its cohort: nothing posted after a flush joins it.
///   * OUTCOME: a receipt succeeds only for a value delivered in a cohort the worker completed,
///     fails with the worker's failure only for one delivered in a cohort it failed, and is
///     refused as closed only after a close.
///   * HONEST REST: while a post's value is neither delivered to the worker nor refused as
///     closed, the host is busy.
///   * SETTLEMENT (at quiescence): every receipt has settled, every value whose receipt
///     succeeded or failed with the worker was delivered exactly once, and nothing is busy.
///   * RUNTIME: no deadlock, no escape, quiescence reached.
module FsHotWatch.Tests.DebouncedWorkSimulation

open System
open System.Threading
open System.Threading.Tasks
open FsHotWatch
open FsHotWatch.PluginWorkOwner
open FsHotWatch.Tests.SimRuntime

[<RequireQualifiedAccess>]
type Op =
    /// Post `value` with a debounce of `seconds` (zero: a flush).
    | Post of value: int * seconds: int
    | Close
    | Step of count: int
    | Advance

[<RequireQualifiedAccess>]
type Mutant =
    /// Debounce timers that never fire.
    | TimerLost
    /// Two consecutive posts sent in the opposite order to their callers.
    | PostOrderSwapped
    /// A value posted twice, as a retry that did not know it landed.
    | DuplicatePost

[<RequireQualifiedAccess>]
type Subject =
    | Current
    | Mutated of Mutant

[<RequireQualifiedAccess>]
type Theorem =
    | Order
    | Barrier
    | Outcome
    | HonestRest
    | Settlement
    | Runtime

type Violation =
    { Seed: int
      Step: int
      Theorem: Theorem
      Detail: string
      History: Op list
      Choices: Choice list }

/// How often the queue's interesting paths were taken.
type Census =
    {
        /// Times the host was at rest while a receipt was still unsettled, its value already
        /// handed to the worker (or refused as closed): a cohort's receipts settle one
        /// continuation after the request they became retires. Counted, not a violation: the
        /// documented contract is that INPUT is never mistaken for rest (AUTOMATION-1695 reports it).
        RestBeforeReceipt: int
        /// Cohorts that merged more than one post.
        Coalesced: int
        /// Cohorts a debounce timer admitted (they end in a value posted with a delay).
        TimerAdmitted: int
        /// Cohorts a flush admitted.
        Flushed: int
    }

module Census =
    let zero =
        { RestBeforeReceipt = 0
          Coalesced = 0
          TimerAdmitted = 0
          Flushed = 0 }

    let add (a: Census) (b: Census) =
        { RestBeforeReceipt = a.RestBeforeReceipt + b.RestBeforeReceipt
          Coalesced = a.Coalesced + b.Coalesced
          TimerAdmitted = a.TimerAdmitted + b.TimerAdmitted
          Flushed = a.Flushed + b.Flushed }

exception CohortFailureException of cohort: int list

[<Literal>]
let HistoryLength = 30

/// The worker fails a cohort holding a multiple of this.
[<Literal>]
let FailingMultiple = 7

let opText (op: Op) : string =
    match op with
    | Op.Post(value, 0) -> $"flush %d{value}"
    | Op.Post(value, seconds) -> $"post %d{value} with a %d{seconds} s debounce"
    | Op.Close -> "close"
    | Op.Step count -> $"step %d{count}"
    | Op.Advance -> "advance to the next timer"

let generate (seed: int) (length: int) : Op list =
    let rng = Random seed
    let mutable value = 0

    [ for _ in 1..length do
          let roll = rng.NextDouble()

          if roll < 0.50 then
              value <- value + 1
              Op.Post(value, (if rng.NextDouble() < 0.25 then 0 else 1 + rng.Next 5))
          elif roll < 0.75 then
              Op.Step(1 + rng.Next 4)
          elif roll < 0.97 then
              Op.Advance
          else
              Op.Close ]

let private listText (values: int list) =
    "[" + (values |> List.map string |> String.concat "; ") + "]"

let private fails (cohort: int list) =
    cohort |> List.exists (fun v -> v % FailingMultiple = 0)

let execute (subject: Subject) (seed: int) (history: Op list) : Violation list * Census =
    let sim = Sim(seed ^^^ 0xDEB0)

    let runtime =
        match subject with
        | Subject.Mutated Mutant.TimerLost ->
            { sim.Runtime with
                Delay = fun _ -> TaskCompletionSource<unit>().Task }
        | Subject.Current
        | Subject.Mutated _ -> sim.Runtime

    use _ = sim.Install()
    let store = Store runtime
    let cohorts = Collections.Generic.List<int list>()
    let posted = Collections.Generic.List<int>()
    let flushed = Collections.Generic.HashSet<int>()
    let receipts = Collections.Generic.List<int * Task<unit>>()
    let violations = Collections.Generic.List<Violation>()
    let mutable closed = false
    let mutable step = 0
    let mutable held: (int * int) option = None

    let violate (theorem: Theorem) (detail: string) =
        violations.Add
            { Seed = seed
              Step = step
              Theorem = theorem
              Detail = detail
              History = history
              Choices = sim.Choices }

    let worker =
        SupervisedWork.Queue(
            store,
            "changes",
            0,
            TimeSpan.FromMinutes 10.0,
            (fun state (_: int list) -> state),
            (fun state _ -> state),
            (fun _ -> sim.CheckOwner "completed ran off the simulation's thread"),
            (fun state cohort _ _ ->
                async {
                    sim.CheckOwner "the worker ran off the simulation's thread"
                    cohorts.Add cohort

                    if fails cohort then
                        raise (CohortFailureException cohort)

                    return state + 1
                })
        )

    let input = DebouncedWork.Queue(store, "changes", worker, (@))

    let send (value: int) (seconds: int) =
        match
            (try
                Ok(input.Post([ value ], TimeSpan.FromSeconds(float seconds)))
             with failure ->
                 Error failure)
        with
        | Ok receipt -> receipts.Add(value, receipt)
        | Error(:? ObjectDisposedException) when closed -> ()
        | Error failure -> violate Theorem.Outcome $"the post of %d{value} was refused with %O{failure}"

    let post (value: int) (seconds: int) =
        posted.Add value

        if seconds = 0 then
            flushed.Add value |> ignore

        match subject, held with
        | Subject.Mutated Mutant.PostOrderSwapped, None -> held <- Some(value, seconds)
        | Subject.Mutated Mutant.PostOrderSwapped, Some(heldValue, heldSeconds) ->
            held <- None
            send value seconds
            send heldValue heldSeconds
        | Subject.Mutated Mutant.DuplicatePost, _ ->
            send value seconds
            send value seconds
        | Subject.Current, _
        | Subject.Mutated Mutant.TimerLost, _ -> send value seconds

    let apply (op: Op) =
        match op with
        | Op.Post(value, seconds) -> post value seconds
        | Op.Close ->
            closed <- true
            input.Close()
        | Op.Step count ->
            for _ in 1..count do
                sim.Step() |> ignore
        | Op.Advance -> sim.AdvanceToNextTimer() |> ignore

    let delivered () = cohorts |> Seq.concat |> List.ofSeq

    let checkOrder () =
        let got = delivered ()
        let order = List.ofSeq posted
        let positions = got |> List.map (fun v -> List.tryFindIndex ((=) v) order)

        if
            List.distinct got <> got
            || positions <> List.sort positions
            || List.contains None positions
        then
            violate
                Theorem.Order
                $"the worker received %s{listText got}, not the posts %s{listText order} in order, each once"

        for cohort in cohorts do
            for value in cohort do
                if flushed.Contains value && List.last cohort <> value then
                    violate Theorem.Barrier $"flushed %d{value} is not the last of its cohort %s{listText cohort}"

    let checkOutcomes () =
        for value, receipt in receipts do
            if receipt.IsCompleted then
                let cohort = cohorts |> Seq.tryFind (List.contains value)

                let allowed, what =
                    match receipt.Exception, cohort with
                    | null, Some c -> not (fails c), "succeeded"
                    | null, None -> false, "succeeded"
                    | failure, _ ->
                        match failure.InnerException, cohort with
                        | CohortFailureException failed, Some c -> failed = c && fails c, "failed with the worker"
                        | :? ObjectDisposedException, _ -> closed, "was refused as closed"
                        // A close cancels the request a cohort became, even before its work began.
                        | :? OperationCanceledException, _ -> closed, "was cancelled"
                        | other, _ -> false, $"failed with %O{other}"

                if not allowed then
                    let where =
                        match cohort with
                        | Some c -> $"delivered in %s{listText c}"
                        | None -> "never delivered"

                    violate Theorem.Outcome $"%d{value} %s{what}, %s{where}"

    // Rest while a receipt is unsettled, counted (see `Census.RestBeforeReceipt`).
    let mutable restBeforeReceipt = 0

    let checkHonestRest () =
        let unsettled =
            receipts
            |> Seq.filter (fun (_, r) -> not r.IsCompleted)
            |> Seq.map fst
            |> List.ofSeq

        if not store.Snapshot.IsBusy then
            let delivered = delivered ()

            match unsettled |> List.filter (fun v -> not (List.contains v delivered)) with
            | [] when unsettled.IsEmpty -> ()
            | [] -> restBeforeReceipt <- restBeforeReceipt + 1
            | _ when closed -> restBeforeReceipt <- restBeforeReceipt + 1
            | waiting ->
                violate
                    Theorem.HonestRest
                    $"the host is at rest while %s{listText waiting} are neither delivered nor refused"

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
            checkOrder ()
            checkOutcomes ()
            checkHonestRest ()

        remaining <- remaining.Tail

    let mutable census = Census.zero

    if violations.Count = 0 then
        step <- step + 1

        try
            held |> Option.iter (fun (value, seconds) -> send value seconds)
            sim.RunUntilQuiet()
        with
        | SimDeadlockException what -> violate Theorem.Runtime what
        | failure -> violate Theorem.Runtime $"quiescence raised %O{failure}"

        checkRuntime ()
        checkOrder ()
        checkOutcomes ()

        let unsettled =
            receipts
            |> Seq.filter (fun (_, r) -> not r.IsCompleted)
            |> Seq.map fst
            |> List.ofSeq

        if not unsettled.IsEmpty then
            violate Theorem.Settlement $"at rest %s{listText unsettled} are unsettled"
        elif store.Snapshot.IsBusy then
            violate Theorem.Settlement "at rest, with every receipt settled, the host is busy"

        census <-
            { RestBeforeReceipt = restBeforeReceipt
              Coalesced = cohorts |> Seq.filter (fun c -> c.Length > 1) |> Seq.length
              TimerAdmitted =
                cohorts
                |> Seq.filter (fun c -> not (flushed.Contains(List.last c)))
                |> Seq.length
              Flushed = cohorts |> Seq.filter (fun c -> flushed.Contains(List.last c)) |> Seq.length }

        try
            input.Close()
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
