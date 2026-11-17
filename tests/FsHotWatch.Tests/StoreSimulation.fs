/// Seeded histories of `PluginWorkOwner.Store` on the deterministic runtime (AUTOMATION-1695).
///
/// A history is a list of operations from several logical callers: asynchronous and
/// synchronous changes (a synchronous change's transition may post further changes, or let
/// queued work run while it holds the writer, as another thread would), changes that throw,
/// observation leases taken and released, and pump steps. The runtime decides, from the
/// seed, whether each posted writer runs inside its post or later, and which ready item runs
/// next. After every operation, and once more at quiescence, the store's contract is checked:
///
///   * PUBLICATION BEFORE RECEIPT: a change's reply never settles before the change is
///     visible in the published snapshot.
///   * ORDER: each row holds a prefix of the changes posted to it, in posting order, each once.
///   * CONSERVATION (at quiescence): each row holds exactly the changes posted to it.
///   * SETTLEMENT (at quiescence): every reply has settled, no change is pending, and the
///     observer count is the leases still held.
///   * FAILURE ISOLATION: a change that throws fails its own reply with its own failure and
///     publishes nothing.
///   * VERSION (at quiescence): one publication per accepted change, no more.
///   * RUNTIME: no deadlock, no escape, quiescence reached.
///
/// Planted defects (`Mutant`) wrap the store the way a defective caller or runtime would; each
/// must be caught within the histories the gate runs.
module FsHotWatch.Tests.StoreSimulation

open System
open System.Threading.Tasks
open FsHotWatch
open FsHotWatch.PluginWorkOwner
open FsHotWatch.Tests.SimRuntime

/// What a synchronous change's transition does while it holds the writer.
[<RequireQualifiedAccess>]
type Nested =
    /// Post an asynchronous change: it queues behind the running writer.
    | PostAsync of row: int * value: int
    /// Let up to `count` ready items run, as other threads would while this one writes.
    | RunReady of count: int

/// One operation of a history.
[<RequireQualifiedAccess>]
type Op =
    | Async of row: int * value: int
    | Sync of row: int * value: int * nested: Nested list
    | Throwing of row: int * synchronous: bool
    | Observe
    /// Dispose a lease taken earlier (taken modulo how many there were; disposing one twice
    /// releases it once).
    | Release of pick: int
    /// Run up to `count` ready items.
    | Step of count: int

/// A planted defect.
[<RequireQualifiedAccess>]
type Mutant =
    /// A reply that settles as soon as its change is posted.
    | ReceiptBeforePublication
    /// The first queued writer is never run: a lost wakeup.
    | LostWakeup
    /// Two consecutive asynchronous changes are posted in the opposite order to their callers.
    | PostOrderSwapped
    /// An asynchronous change is delivered twice, as a retry that did not know it landed.
    | DuplicateDelivery

[<RequireQualifiedAccess>]
type Subject =
    | Current
    | Mutated of Mutant

[<RequireQualifiedAccess>]
type Theorem =
    | PublicationBeforeReceipt
    | Order
    | Conservation
    | Settlement
    | FailureIsolation
    | Version
    | Runtime

type Violation =
    { Seed: int
      Step: int
      Theorem: Theorem
      Detail: string
      History: Op list
      Choices: Choice list }

/// How often each interleaving of the store's writer was reached: the arms a pool reaches only
/// by chance, each reached here by the seed.
type Census =
    {
        /// An asynchronous change whose writer published inside its post: the await's
        /// already-complete arm.
        AlreadyPublished: int
        /// An asynchronous change still pending when its post returned: the await's suspend arm.
        Suspended: int
        /// An asynchronous change that found a writer posted and not started, and only queued.
        JoinedPostedWriter: int
        /// A posted writer that started while a synchronous caller was writing, and claimed
        /// nothing.
        WriterFoundWriting: int
    }

module Census =
    let zero =
        { AlreadyPublished = 0
          Suspended = 0
          JoinedPostedWriter = 0
          WriterFoundWriting = 0 }

    let add (a: Census) (b: Census) =
        { AlreadyPublished = a.AlreadyPublished + b.AlreadyPublished
          Suspended = a.Suspended + b.Suspended
          JoinedPostedWriter = a.JoinedPostedWriter + b.JoinedPostedWriter
          WriterFoundWriting = a.WriterFoundWriting + b.WriterFoundWriting }

/// The failure a throwing change raises, so isolation can tell it from any other.
exception SimulatedFailure of value: int

[<Literal>]
let Rows = 2

[<Literal>]
let HistoryLength = 40

let private nestedText (n: Nested) =
    match n with
    | Nested.PostAsync(row, value) -> $"post async row %d{row} <- %d{value}"
    | Nested.RunReady count -> $"run %d{count} ready"

/// An operation as one line, for a violation's history.
let opText (op: Op) : string =
    match op with
    | Op.Async(row, value) -> $"async row %d{row} <- %d{value}"
    | Op.Sync(row, value, nested) ->
        let inner =
            match nested with
            | [] -> ""
            | _ -> " { " + (nested |> List.map nestedText |> String.concat "; ") + " }"

        $"sync row %d{row} <- %d{value}%s{inner}"
    | Op.Throwing(row, synchronous) ->
        let how = if synchronous then "sync" else "async"
        $"%s{how} throwing change on row %d{row}"
    | Op.Observe -> "observe"
    | Op.Release pick -> $"release lease %d{pick}"
    | Op.Step count -> $"step %d{count}"

/// The operations of a history of `length` from `seed`.
let generate (seed: int) (length: int) : Op list =
    let rng = Random seed
    let mutable value = 0

    let next () =
        value <- value + 1
        value

    let row () = rng.Next Rows

    [ for _ in 1..length do
          let roll = rng.NextDouble()

          if roll < 0.30 then
              Op.Async(row (), next ())
          elif roll < 0.50 then
              let r = row ()
              let v = next ()

              let nested =
                  [ for _ in 1 .. rng.Next 3 do
                        if rng.NextDouble() < 0.6 then
                            Nested.PostAsync(row (), next ())
                        else
                            Nested.RunReady(1 + rng.Next 3) ]

              Op.Sync(r, v, nested)
          elif roll < 0.55 then
              Op.Throwing(row (), rng.NextDouble() < 0.5)
          elif roll < 0.62 then
              Op.Observe
          elif roll < 0.69 then
              Op.Release(rng.Next 1000)
          else
              Op.Step(1 + rng.Next 4) ]


/// A runtime whose first dispatch is lost: the writer it carried never runs.
let private losingFirst (inner: AsyncRuntime.Runtime) : AsyncRuntime.Runtime =
    let mutable lost = false

    { inner with
        Dispatch = fun work -> if lost then inner.Dispatch work else lost <- true }

let private isPrefix (actual: int list) (expected: int list) =
    actual.Length <= expected.Length
    && List.truncate actual.Length expected = actual

let private listText (values: int list) =
    "[" + (values |> List.map string |> String.concat "; ") + "]"

/// Run `history` against a fresh store, on a runtime seeded from `seed`.
let execute (subject: Subject) (seed: int) (history: Op list) : Violation list * Census =
    let sim = Sim(seed ^^^ 0x51ED)

    let runtime =
        match subject with
        | Subject.Mutated Mutant.LostWakeup -> losingFirst sim.Runtime
        | Subject.Current
        | Subject.Mutated _ -> sim.Runtime

    use _ = sim.Install()
    let store = Store runtime

    let rows =
        Array.init Rows (fun i -> store.Register($"row %d{i}", ([]: int list), fun _ -> RowStatus.ofWork false 0L None))

    let baseVersion = store.Snapshot.Version
    let posted = Array.init Rows (fun _ -> Collections.Generic.List<int>())
    let replies = Collections.Generic.List<Task<int>>()
    let throwing = Collections.Generic.List<int * Task<int>>()
    let leases = Collections.Generic.List<IDisposable>()
    let released = Collections.Generic.HashSet<int>()
    let violations = Collections.Generic.List<Violation>()
    let mutable accepted = 0
    let mutable failures = 0
    let mutable census = Census.zero
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

    let read (row: int) = store.Read rows[row] |> List.rev

    let append (value: int) =
        fun (_: WorkId) (seen: int list) -> value :: seen, value

    // Check a reply when it settles: the change it acknowledges is visible by then.
    let watch (row: int) (value: int) (reply: Task<int>) =
        let check () =
            if reply.IsCompletedSuccessfully then
                if not (List.contains value (store.Read rows[row])) then
                    violate
                        Theorem.PublicationBeforeReceipt
                        $"the reply for %d{value} settled while row %d{row} was %s{listText (read row)}"
                elif reply.Result <> value then
                    violate Theorem.PublicationBeforeReceipt $"the reply for %d{value} carried %d{reply.Result}"

        if reply.IsCompleted then
            check ()
        else
            reply.GetAwaiter().UnsafeOnCompleted(Action check)

        replies.Add reply

    // Post one asynchronous change, as the subject's caller posts it.
    let send (row: int) (value: int) : Task<int> =
        posted[row].Add value
        accepted <- accepted + 1

        match subject with
        | Subject.Mutated Mutant.ReceiptBeforePublication ->
            store.ChangeAsync(rows[row], append value) |> ignore
            Task.FromResult value
        | Subject.Mutated Mutant.DuplicateDelivery ->
            store.ChangeAsync(rows[row], append value) |> ignore
            store.ChangeAsync(rows[row], append value)
        | Subject.Current
        | Subject.Mutated _ -> store.ChangeAsync(rows[row], append value)

    // A top-level asynchronous change, counted in the census by the arm it took.
    let sendCounted (row: int) (value: int) =
        let choicesBefore = sim.ChoiceCount
        let reply = send row value

        census <-
            { census with
                AlreadyPublished = census.AlreadyPublished + (if reply.IsCompleted then 1 else 0)
                Suspended = census.Suspended + (if reply.IsCompleted then 0 else 1)
                // Outside a transition no writer is running, so a post that dispatched
                // nothing found a writer posted and not started.
                JoinedPostedWriter = census.JoinedPostedWriter + (if sim.ChoiceCount = choicesBefore then 1 else 0) }

        watch row value reply

    let runReady (count: int) (insideWriter: bool) =
        for _ in 1..count do
            match sim.Step() with
            | Some Ran.Dispatched when insideWriter ->
                census <-
                    { census with
                        WriterFoundWriting = census.WriterFoundWriting + 1 }
            | Some _
            | None -> ()

    let apply (op: Op) =
        match op with
        | Op.Async(row, value) ->
            match subject, held with
            | Subject.Mutated Mutant.PostOrderSwapped, None ->
                // Held, so the next asynchronous change is posted ahead of it.
                posted[row].Add value
                accepted <- accepted + 1
                held <- Some(row, value)
            | Subject.Mutated Mutant.PostOrderSwapped, Some(heldRow, heldValue) ->
                held <- None
                sendCounted row value
                watch heldRow heldValue (store.ChangeAsync(rows[heldRow], append heldValue))
            | _ -> sendCounted row value
        | Op.Sync(row, value, nested) ->
            posted[row].Add value
            accepted <- accepted + 1

            let result =
                store.Change(
                    rows[row],
                    fun _ seen ->
                        for n in nested do
                            match n with
                            | Nested.PostAsync(r, v) -> watch r v (send r v)
                            | Nested.RunReady count -> runReady count true

                        value :: seen, value
                )

            if result <> value || not (List.contains value (store.Read rows[row])) then
                violate
                    Theorem.PublicationBeforeReceipt
                    $"the synchronous change %d{value} returned %d{result} with row %d{row} %s{listText (read row)}"
        | Op.Throwing(row, synchronous) ->
            failures <- failures + 1
            let id = failures
            let throws = fun (_: WorkId) (_: int list) -> raise (SimulatedFailure id)

            if synchronous then
                match
                    (try
                        Ok(store.Change(rows[row], throws))
                     with failure ->
                         Error failure)
                with
                | Error(SimulatedFailure raised) when raised = id -> ()
                | Error failure ->
                    violate Theorem.FailureIsolation $"synchronous throwing change %d{id} raised %s{failure.Message}"
                | Ok result ->
                    violate Theorem.FailureIsolation $"synchronous throwing change %d{id} returned %d{result}"
            else
                throwing.Add(id, store.ChangeAsync(rows[row], throws))
        | Op.Observe ->
            leases.Add(store.Observe())
            accepted <- accepted + 1
        | Op.Release pick ->
            if leases.Count > 0 then
                let index = pick % leases.Count
                leases[index].Dispose()

                if released.Add index then
                    accepted <- accepted + 1
        | Op.Step count -> runReady count false

    let checkOrder () =
        for row in 0 .. Rows - 1 do
            let actual = read row
            let expected = List.ofSeq posted[row]

            if not (isPrefix actual expected) then
                violate
                    Theorem.Order
                    $"row %d{row} is %s{listText actual}, not a prefix of what was posted to it, %s{listText expected}"

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
        | SimDeadlock what -> violate Theorem.Runtime what
        | failure -> violate Theorem.Runtime $"the step raised %O{failure}"

        if violations.Count = 0 then
            checkOrder ()
            checkRuntime ()

        remaining <- remaining.Tail

    if violations.Count = 0 then
        step <- step + 1

        held
        |> Option.iter (fun (row, value) -> watch row value (store.ChangeAsync(rows[row], append value)))

        sim.RunUntilQuiet()
        checkRuntime ()

        for row in 0 .. Rows - 1 do
            let actual = read row
            let expected = List.ofSeq posted[row]

            if actual <> expected then
                violate
                    Theorem.Conservation
                    $"at rest row %d{row} is %s{listText actual}; what was posted to it is %s{listText expected}"

        let unsettled = replies |> Seq.filter (fun r -> not r.IsCompleted) |> Seq.length

        if unsettled > 0 || store.PendingChanges <> 0 then
            violate
                Theorem.Settlement
                $"at rest %d{unsettled} replies are unsettled and %d{store.PendingChanges} changes pending"

        if store.Snapshot.ObserverCount <> leases.Count - released.Count then
            violate
                Theorem.Settlement
                $"%d{store.Snapshot.ObserverCount} observers, with %d{leases.Count - released.Count} leases held"

        for id, reply in throwing do
            match reply.Exception with
            | null -> violate Theorem.FailureIsolation $"throwing change %d{id} ended %A{reply.Status}"
            | failure ->
                match failure.InnerException with
                | SimulatedFailure raised when raised = id -> ()
                | other -> violate Theorem.FailureIsolation $"throwing change %d{id} failed with %O{other}"

        let publications = int (store.Snapshot.Version - baseVersion)

        if publications <> accepted then
            violate Theorem.Version $"%d{publications} publications for %d{accepted} accepted changes"

    List.ofSeq violations, census

/// The histories of `seeds`, in order; with `stopAtFirst`, until the first violation.
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


/// A violation, with everything needed to replay it: the seed alone regenerates the history
/// and every choice the runtime made.
let describe (v: Violation) : string =
    let lines =
        v.History
        |> List.mapi (fun i op ->
            let mark = if i + 1 = v.Step then " <-- here" else ""
            $"  %d{i + 1}. %s{opText op}%s{mark}")
        |> String.concat "\n"

    $"seed %d{v.Seed}, step %d{v.Step}: %A{v.Theorem}: %s{v.Detail}\n"
    + $"(%d{v.Choices.Length} runtime choices; replay with FSHW_SIM_SEED=%d{v.Seed} FSHW_SIM_HISTORIES=1)\n%s{lines}"
