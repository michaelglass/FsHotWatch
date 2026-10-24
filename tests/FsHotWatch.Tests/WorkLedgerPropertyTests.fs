/// Conservation of work, as a checked property (AUTOMATION-107, item 2).
///
/// The law: every unit of work the owner accepts — an event, a worker, a result fold, a
/// queued command — reaches exactly one terminal outcome, and the owner rests exactly when
/// nothing it accepted is still owed. A receipt settles once, when its unit retires, and
/// says which way it retired.
///
/// FsCheck generates arbitrary interleavings of every `Owner` transition (admit, publish,
/// commit, settle, fail, claim, complete, fail-run, abandon, mark, enqueue, coalesce,
/// lease release, executor fault), including transitions aimed at retired and foreign
/// identities. A small independent model predicts which ones the owner must refuse, what
/// it owes, and which queued command a freed key must hand on to. After every step the
/// owner's published answers are compared with the model; after the sequence, everything
/// still owed is driven to completion and the owner must come to rest with every receipt
/// settled.
module FsHotWatch.Tests.WorkLedgerPropertyTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FsCheck
open FsCheck.FSharp
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.PluginWorkOwner

/// One generated transition. Picks are resolved against every identity seen so far,
/// retired and foreign ones included, so refusals are exercised as often as successes.
type Cmd =
    | Admit of tracked: bool
    | Publish of pick: int
    | Commit of pick: int
    | Settle of pick: int * preparedCommit: bool
    | FailEvent of pick: int * commitFailed: bool
    | Claim of secondKey: bool * after: int option
    | CompleteRun of pick: int
    | FailRun of pick: int
    | Abandon of pick: int
    | MarkFailure of pick: int
    | Enqueue of secondKey: bool * coalesce: bool option * byClient: bool * poisoned: bool
    | Release of pick: int
    | Fault

type private Kind =
    | KEvent
    | KFold
    | KWorker
    | KQueued

exception PoisonedDeliveryException

type private Outcome =
    | Succeeded
    | Refused

[<NoComparison; NoEquality>]
type private Tracked =
    {
        mutable Kind: Kind
        Key: string option
        mutable Stage: EventStage
        Receipt: Task<unit> option
        Coalesce: bool option
        /// `None`: the daemon holds it. Otherwise the client sources whose release it waits on.
        mutable Clients: CancellationTokenSource list option
        /// Every client source that ever asked for it, the daemon-held ones included.
        mutable Sources: CancellationTokenSource list
        /// Its delivery raises: the owner must fault its executor and still settle what committed.
        mutable Poisoned: bool
        mutable Retired: Outcome option
    }

[<NoComparison; NoEquality>]
type private Lane =
    { mutable Folds: WorkId list
      mutable Worker: WorkId option
      mutable Queue: WorkId list }

let private keys = [ "a"; "b" ]
let private keyOf secondKey = if secondKey then "b" else "a"

/// The owner under test plus an independent account of what it owes.
type private Harness() =
    let owner = Owner<int>(0)
    let units = Dictionary<WorkId, Tracked>()
    let lanes = Dictionary<string, Lane>()
    let known = ResizeArray<WorkId>()
    let delivered = ResizeArray<WorkId>()
    let expectedDelivered = ResizeArray<WorkId>()
    let mutable committed = 0L
    let mutable faulted = false
    let mutable state = 0
    let mutable poisonExpected = false
    let mutable poisonRaised = false

    do
        // A foreign identity: minted by another owner, so this one must refuse it.
        known.Add((Owner<int>(0)).AdmitEvent())

    let fail message =
        raise (Xunit.Sdk.XunitException message)

    /// A non-negative pick names a unit still owed, when there is one; a negative pick names any
    /// identity ever seen, retired and foreign ones included.
    let pick index =
        let owedIds =
            known
            |> Seq.filter (fun id ->
                match units.TryGetValue id with
                | true, tracked -> tracked.Retired.IsNone
                | false, _ -> false)
            |> Array.ofSeq

        let from: WorkId array =
            if index >= 0 && owedIds.Length > 0 then
                owedIds
            else
                known.ToArray()

        from[((index % from.Length) + from.Length) % from.Length]

    let live id =
        match units.TryGetValue id with
        | true, tracked when tracked.Retired.IsNone -> Some tracked
        | _ -> None

    let fresh kind key stage receipt coalesce clients (id: WorkId) =
        if units.ContainsKey id then
            fail "the owner minted an identity it had already minted"

        units[id] <-
            { Kind = kind
              Key = key
              Stage = stage
              Receipt = receipt
              Coalesce = coalesce
              Clients = clients
              Sources = Option.defaultValue [] clients
              Poisoned = false
              Retired = None }

        known.Add id

    /// A key with no holder hands on to its oldest queued command, in the same step.
    let release key =
        let lane = lanes[key]

        if lane.Folds.IsEmpty && lane.Worker.IsNone then
            match lane.Queue with
            | [] -> lanes.Remove key |> ignore
            | next :: rest ->
                lane.Folds <- [ next ]
                lane.Queue <- rest
                units[next].Kind <- KFold
                units[next].Stage <- Admitted
                expectedDelivered.Add next

                if units[next].Poisoned then
                    poisonExpected <- true

    let retire id outcome =
        let tracked = units[id]
        tracked.Retired <- Some outcome

        match tracked.Kind, tracked.Key with
        | KFold, Some key ->
            let lane = lanes[key]
            lane.Folds <- lane.Folds |> List.filter ((<>) id)
            release key
        | KWorker, Some key ->
            lanes[key].Worker <- None
            release key
        | KQueued, Some key ->
            let lane = lanes[key]
            lane.Queue <- lane.Queue |> List.filter ((<>) id)
        | _ -> ()

    /// Run `act` where the model says the owner must refuse: it raises, and publishes nothing.
    let refused (label: string) (act: unit -> unit) =
        let before = owner.Store.Snapshot.Version

        let raised =
            try
                act ()
                false
            with :? InvalidOperationException ->
                true

        if not raised then
            fail $"%s{label}: the owner accepted a transition the ledger says it must refuse"

        if owner.Store.Snapshot.Version <> before then
            fail $"%s{label}: a refused transition published a new snapshot"

    /// Run an accepted transition whose publication may hand a freed key to a poisoned command.
    let guarded (act: unit -> unit) =
        try
            act ()
        with PoisonedDeliveryException ->
            poisonRaised <- true

    let isEventLike (tracked: Tracked) =
        tracked.Kind = KEvent || tracked.Kind = KFold

    let commit id =
        match live id with
        | Some tracked when isEventLike tracked && tracked.Stage = Admitted ->
            state <- state + 1
            guarded (fun () -> owner.CommitEvent(id, state))
            committed <- committed + 1L
            retire id Succeeded
        | _ -> refused "commit" (fun () -> owner.CommitEvent(id, -1))

    let settle id preparedCommit =
        match live id with
        | Some tracked when isEventLike tracked && tracked.Stage = Prepared ->
            guarded (fun () -> owner.SettleEvent(id, preparedCommit))
            committed <- committed + 1L
            retire id Succeeded
        | _ -> refused "settle" (fun () -> owner.SettleEvent(id, preparedCommit))

    let completeRun id =
        match live id with
        | Some tracked when tracked.Kind = KWorker ->
            let key = tracked.Key.Value

            match owner.CompleteRun id with
            | Some _ when faulted -> fail "a faulted executor folded a run's result"
            | None when not faulted -> fail "a live executor did not fold a run's result"
            | None -> retire id Succeeded
            | Some fold ->
                tracked.Retired <- Some Succeeded
                let lane = lanes[key]
                lane.Worker <- None
                lane.Folds <- lane.Folds @ [ fold ]
                fresh KFold (Some key) Admitted None None None fold
        | _ -> refused "complete-run" (fun () -> owner.CompleteRun id |> ignore)

    /// Every event, fold and queued command retires as a failure. Live workers stay owned
    /// until they actually finish: the fault does not prove they stopped.
    let applyFault () =
        if not faulted then
            faulted <- true

            for tracked in List.ofSeq units.Values do
                if tracked.Retired.IsNone && tracked.Kind <> KWorker then
                    tracked.Retired <- Some Refused

            for key in List.ofSeq lanes.Keys do
                let lane = lanes[key]
                lane.Folds <- []
                lane.Queue <- []

                if lane.Worker.IsNone then
                    lanes.Remove key |> ignore

    let faultExecutor () =
        owner.FaultExecutor(exn "executor failed")
        applyFault ()

    let step command =
        match command with
        | Admit tracked when faulted ->
            refused "admit" (fun () ->
                if tracked then
                    owner.AdmitTrackedEvent() |> ignore
                else
                    owner.AdmitEvent() |> ignore)
        | Admit true ->
            let id, receipt = owner.AdmitTrackedEvent()
            fresh KEvent None Admitted (Some receipt) None None id
        | Admit false -> fresh KEvent None Admitted None None None (owner.AdmitEvent())
        | Publish index ->
            let id = pick index

            match live id with
            | Some tracked when isEventLike tracked && tracked.Stage = Admitted ->
                state <- state + 1
                owner.PublishEventState(id, state)
                tracked.Stage <- Prepared
            | _ -> refused "publish" (fun () -> owner.PublishEventState(id, -1))
        | Commit index -> commit (pick index)
        | Settle(index, preparedCommit) -> settle (pick index) preparedCommit
        | FailEvent(index, commitFailed) ->
            let id = pick index

            let failure =
                if commitFailed then
                    CommitFailed(exn "commit failed")
                else
                    UpdateFailed(exn "update failed")

            match live id with
            | Some tracked when isEventLike tracked ->
                guarded (fun () -> owner.FailEvent(id, failure))
                retire id Refused
            | _ -> refused "fail-event" (fun () -> owner.FailEvent(id, failure))
        | Claim(secondKey, after) ->
            let key = keyOf secondKey
            // Half the even picks name the key's oldest fold: the only predecessor a claim can follow.
            let after =
                after
                |> Option.map (fun index ->
                    match lanes.TryGetValue key with
                    | true, { Folds = oldest :: _ } when index >= 0 && index % 2 = 0 -> oldest
                    | _ -> pick index)

            if faulted then
                refused "claim" (fun () ->
                    match after with
                    | Some predecessor -> owner.TryClaim(key, predecessor) |> ignore
                    | None -> owner.TryClaim key |> ignore)
            else
                let claimed =
                    match after with
                    | Some predecessor -> owner.TryClaim(key, predecessor)
                    | None -> owner.TryClaim key

                let expected =
                    match lanes.TryGetValue key with
                    | false, _ -> true
                    | true, lane ->
                        match lane.Folds, lane.Worker, after with
                        | [ only ], None, Some predecessor -> only = predecessor
                        | _ -> false

                match claimed with
                | Some worker when expected ->
                    if not (lanes.ContainsKey key) then
                        lanes[key] <-
                            { Folds = []
                              Worker = None
                              Queue = [] }

                    lanes[key].Worker <- Some worker
                    fresh KWorker (Some key) Admitted None None None worker
                | None when not expected -> ()
                | Some _ -> fail $"claim on %s{key}: the owner granted a key the ledger says is held"
                | None -> fail $"claim on %s{key}: the owner refused a key the ledger says is free"
        | CompleteRun index -> completeRun (pick index)
        | FailRun index ->
            let id = pick index

            match live id with
            | Some tracked when tracked.Kind = KWorker ->
                guarded (fun () -> owner.FailRun(id, exn "run failed"))
                retire id Refused
            | _ -> refused "fail-run" (fun () -> owner.FailRun(id, exn "run failed"))
        | Abandon index ->
            let id = pick index

            match live id with
            | Some tracked when tracked.Kind = KWorker ->
                guarded (fun () -> owner.AbandonRun id)
                retire id Succeeded
            | _ -> refused "abandon" (fun () -> owner.AbandonRun id)
        // A missed deadline records a failure and retires nothing, live worker or not.
        | MarkFailure index -> owner.MarkRunFailure(pick index, exn "deadline missed")
        | Enqueue(secondKey, coalesce, byClient, poisoned) ->
            let key = keyOf secondKey
            let coalescing = coalesce |> Option.map string

            let client =
                if byClient then
                    Some(new CancellationTokenSource())
                else
                    None

            let leases =
                match client with
                | Some source -> ConsumerLeases.ofClient source.Token
                | None -> HeldByDaemon

            let deliver id _ =
                delivered.Add id

                if poisoned then
                    raise PoisonedDeliveryException

            let enqueue () =
                owner.EnqueueLeasedIntent(key, coalescing, leases, deliver)

            if faulted then
                refused "enqueue" (enqueue >> ignore)
            else
                let prior =
                    match lanes.TryGetValue key, coalesce with
                    | (true, lane), Some _ ->
                        lane.Queue |> List.tryFind (fun queued -> units[queued].Coalesce = coalesce)
                    | _ -> None

                // A poisoned command delivered at once raises out of the enqueue, after its
                // publication: its identity is the one the delivery was handed.
                let accepted =
                    try
                        let id, receipt = enqueue ()
                        Some receipt, id
                    with PoisonedDeliveryException ->
                        poisonRaised <- true
                        None, delivered[delivered.Count - 1]

                match prior, accepted with
                | Some prior, (receipt, id) ->
                    if id <> prior then
                        fail "a coalescing command did not join the queued command it matches"

                    match units[prior].Receipt, receipt with
                    | Some shared, Some receipt when obj.ReferenceEquals(shared, receipt) -> ()
                    | _ -> fail "a coalesced command did not share its predecessor's receipt"

                    let merged = units[prior]
                    // The newest command's payload replaces the queued one's.
                    merged.Poisoned <- poisoned

                    merged.Sources <- merged.Sources @ Option.toList client

                    merged.Clients <-
                        match merged.Clients, client with
                        | Some mine, Some theirs -> Some(mine @ [ theirs ])
                        | _ -> None
                | None, (receipt, id) ->
                    let clients = client |> Option.map List.singleton

                    match lanes.TryGetValue key with
                    | true, lane ->
                        lane.Queue <- lane.Queue @ [ id ]
                        fresh KQueued (Some key) Admitted receipt coalesce clients id
                        units[id].Poisoned <- poisoned
                    | false, _ ->
                        lanes[key] <-
                            { Folds = [ id ]
                              Worker = None
                              Queue = [] }

                        fresh KFold (Some key) Admitted receipt coalesce clients id
                        expectedDelivered.Add id

                        if poisoned then
                            poisonExpected <- true
        | Release index ->
            // Aimed at a queued command when there is one: only those can be withdrawn.
            let queued =
                known
                |> Seq.filter (fun id ->
                    match units.TryGetValue id with
                    | true, tracked -> tracked.Retired.IsNone && tracked.Kind = KQueued
                    | false, _ -> false)
                |> Array.ofSeq

            let id =
                if index >= 0 && queued.Length > 0 then
                    queued[index % queued.Length]
                else
                    pick index

            match units.TryGetValue id with
            | true, tracked ->
                tracked.Sources
                |> List.tryFind (fun source -> not source.IsCancellationRequested)
                |> Option.iter (fun source -> source.Cancel())
            | false, _ -> ()

            let expected =
                match live id with
                | Some({ Kind = KQueued
                         Clients = Some sources }) ->
                    sources |> List.forall (fun source -> source.IsCancellationRequested)
                | _ -> false

            let withdrawn = owner.WithdrawReleased id

            if withdrawn <> expected then
                fail $"withdraw: the owner answered %b{withdrawn}, the ledger says %b{expected}"

            if withdrawn then
                retire id Refused
        | Fault -> faultExecutor ()

    let owed () =
        units.Values |> Seq.filter (fun tracked -> tracked.Retired.IsNone) |> Seq.length

    /// Everything the owner publishes must agree with the ledger, after every step.
    let check (after: unit -> string) =
        let snapshot = owner.Snapshot
        let owed = owed ()

        if snapshot.IsBusy <> (owed > 0) then
            fail $"after %s{after ()}: owner busy=%b{snapshot.IsBusy} while the ledger owes %d{owed} unit(s)"

        if owner.Store.Snapshot.IsBusy <> snapshot.IsBusy then
            fail $"after %s{after ()}: the host's rest answer disagrees with its owner's"

        if snapshot.CompletedEvents <> committed then
            fail $"after %s{after ()}: %d{snapshot.CompletedEvents} commit(s) published, %d{committed} made"

        if snapshot.ExecutorFault.IsSome <> faulted then
            fail $"after %s{after ()}: executor fault published=%b{snapshot.ExecutorFault.IsSome}, made=%b{faulted}"

        if List.ofSeq delivered <> List.ofSeq expectedDelivered then
            fail
                $"after %s{after ()}: delivered %A{List.ofSeq delivered}, the ledger expected %A{List.ofSeq expectedDelivered}"

        for key in keys do
            let held, running =
                match lanes.TryGetValue key with
                | true, lane -> true, lane.Worker.IsSome
                | false, _ -> false, false

            if snapshot.IsHeld key <> held || snapshot.IsRunning key <> running then
                fail
                    $"after %s{after ()}: key %s{key} held=%b{snapshot.IsHeld key} running=%b{snapshot.IsRunning key}, ledger held=%b{held} running=%b{running}"

        // Exactly one terminal outcome per receipt, settled with its unit and not before.
        for tracked in units.Values do
            match tracked.Receipt, tracked.Retired with
            | Some receipt, None when receipt.IsCompleted ->
                fail $"after %s{after ()}: a receipt settled while its unit is still owed"
            | Some receipt, Some _ when not receipt.IsCompleted ->
                fail $"after %s{after ()}: a unit retired and its receipt never settled"
            | Some receipt, Some Succeeded when not receipt.IsCompletedSuccessfully ->
                fail $"after %s{after ()}: a unit that committed settled its receipt as a failure"
            | Some receipt, Some Refused when receipt.IsCompletedSuccessfully ->
                fail $"after %s{after ()}: a unit that failed settled its receipt as a success"
            | _ -> ()

    /// One transition, then the delivery it handed on: a poisoned delivery must raise out of
    /// exactly the transition that freed its key, and leave the executor faulted. The label
    /// is formatted only for a failure message: `sprintf "%A"` on every generated command
    /// was most of this property's run time (15-27 s of a solo class run, against 1-3 s
    /// without it), which pushed it past its cap on a loaded box.
    let transition (label: unit -> string) (act: unit -> unit) =
        poisonExpected <- false
        poisonRaised <- false
        act ()

        if poisonExpected <> poisonRaised then
            fail $"%s{label ()}: a poisoned delivery was expected=%b{poisonExpected}, raised=%b{poisonRaised}"

        if poisonExpected then
            applyFault ()

        check label

    /// Drive every owed unit to a successful end, as a cooperative host would.
    let drain () =
        let mutable rounds = 0

        while owed () > 0 do
            rounds <- rounds + 1

            if rounds > 10_000 then
                fail $"drain: %d{owed ()} unit(s) can never retire"

            let next =
                units
                |> Seq.tryFind (fun (KeyValue(_, tracked)) -> tracked.Retired.IsNone && isEventLike tracked)
                |> Option.orElse (
                    units
                    |> Seq.tryFind (fun (KeyValue(_, tracked)) -> tracked.Retired.IsNone && tracked.Kind = KWorker)
                )

            match next with
            | Some(KeyValue(id, tracked)) when tracked.Kind = KWorker ->
                transition (fun () -> "drain") (fun () -> completeRun id)
            | Some(KeyValue(id, tracked)) when tracked.Stage = Prepared ->
                transition (fun () -> "drain") (fun () -> settle id false)
            | Some(KeyValue(id, _)) -> transition (fun () -> "drain") (fun () -> commit id)
            | None -> fail $"drain: %d{owed ()} queued unit(s) wait on a key nothing holds"

        match owner.Snapshot.Phase with
        | Resting -> ()
        | Working _ -> fail "drain: nothing is owed and the owner is not resting"

    member _.Run(commands: Cmd list) =
        check (fun () -> "start")

        for command in commands do
            transition (fun () -> sprintf "%A" command) (fun () -> step command)

        drain ()

let private pickGen = Gen.choose (-12, 40)

let private cmdGen =
    Gen.frequency
        [ 6, Gen.elements [ true; false ] |> Gen.map Admit
          3, pickGen |> Gen.map Publish
          4, pickGen |> Gen.map Commit
          3, Gen.zip pickGen (Gen.elements [ true; false ]) |> Gen.map Settle
          2, Gen.zip pickGen (Gen.elements [ true; false ]) |> Gen.map FailEvent
          5, Gen.zip (Gen.elements [ true; false ]) (Gen.optionOf pickGen) |> Gen.map Claim
          4, pickGen |> Gen.map CompleteRun
          2, pickGen |> Gen.map FailRun
          1, pickGen |> Gen.map Abandon
          1, pickGen |> Gen.map MarkFailure
          5,
          gen {
              let! secondKey = Gen.elements [ true; false ]
              let! coalesce = Gen.optionOf (Gen.elements [ true; false ])
              let! byClient = Gen.elements [ true; false ]
              // Rare, for the same reason as the fault: it is one.
              let! poisoned = Gen.frequency [ 15, Gen.constant false; 1, Gen.constant true ]
              return Enqueue(secondKey, coalesce, byClient, poisoned)
          }
          4, pickGen |> Gen.map Release
          // Rare: after it, most work is refused, so a frequent fault starves the rest.
          1, Gen.constant Fault ]

let private commands =
    Arb.fromGenShrink (Gen.listOf cmdGen, (ArbMap.defaults |> ArbMap.arbitrary<Cmd list>).Shrinker)

[<Fact(Timeout = PropertyTestCapMs)>]
let ``every accepted unit of work reaches exactly one terminal outcome, and the owner rests only when none is owed``
    ()
    =
    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(600).WithEndSize(120),
        Prop.forAll commands (fun commands -> Harness().Run commands)
    )

/// The executor fault is the transition that retires the most at once, so a run that
/// faults in every sequence keeps that path from being sampled only rarely.
[<Fact(Timeout = PropertyTestCapMs)>]
let ``an executor fault retires every non-worker unit once, and the workers it leaves still retire`` () =
    let faulting =
        Arb.fromGen (Gen.map2 (fun before after -> before @ [ Fault ] @ after) (Gen.listOf cmdGen) (Gen.listOf cmdGen))

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(300).WithEndSize(60),
        Prop.forAll faulting (fun commands -> Harness().Run commands)
    )
