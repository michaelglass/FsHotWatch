module FsHotWatch.Tests.PluginWorkOwnerTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch
open FsHotWatch.PluginWorkOwner

/// Await a receipt that must already be settled or settle promptly, preserving its exception.
let private settled (receipt: Task<unit>) =
    let winner =
        Task.WhenAny([| receipt :> Task; Task.Delay(TimeSpan.FromSeconds 5.0) |]).GetAwaiter().GetResult()

    Assert.True(obj.ReferenceEquals(receipt, winner), "the receipt must settle within the observation bound")
    receipt.GetAwaiter().GetResult()

let private claim (owner: Owner<'State>) key =
    owner.TryClaim key
    |> Option.defaultWith (fun () -> failwith $"fixture expected {key} to be free")

let private complete (owner: Owner<'State>) run =
    owner.CompleteRun run
    |> Option.defaultWith (fun () -> failwith "fixture expected a live executor to fold the result")

let private idle () = RowStatus.ofWork false 0L None

let private assertResting (snapshot: Snapshot<'State>) =
    match snapshot.Phase with
    | Resting -> ()
    | Working _ -> Assert.Fail "the owner must be resting"

// ---------------------------------------------------------------------------------------
// Runs, folds and events
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``owner atomically transfers a run to its result fold and publishes the fold before rest`` () =
    let owner = Owner(0)
    Assert.False(owner.Snapshot.IsRunning "work")
    let identity = claim owner "work"
    let running = owner.Snapshot
    Assert.True running.IsBusy
    Assert.True(running.IsRunning "work")
    Assert.False(running.IsRunning "other")

    let completion = complete owner identity
    let folding = owner.Snapshot
    Assert.True folding.IsBusy
    Assert.False(folding.IsRunning "work")
    Assert.Equal(0, folding.State)
    // A prior observation is immutable even after its successor is published.
    Assert.True(running.IsRunning "work")

    owner.CommitEvent(completion, 1)
    let resting = owner.Snapshot
    assertResting resting
    Assert.False resting.IsBusy
    Assert.Equal(1, resting.State)
    Assert.Equal(1L, resting.CompletedEvents)

[<Fact>]
let ``duplicate and foreign completions cannot retire another obligation`` () =
    let owner = Owner(0)
    let first = owner.AdmitEvent()
    let second = owner.AdmitEvent()
    owner.CommitEvent(first, 1)

    Assert.Throws<InvalidOperationException>(fun () -> owner.CommitEvent(first, 99))
    |> ignore

    let other = Owner(0)
    let foreign = other.AdmitEvent()

    Assert.Throws<InvalidOperationException>(fun () -> owner.CommitEvent(foreign, 99))
    |> ignore

    Assert.True owner.Snapshot.IsBusy
    Assert.Equal(1, owner.Snapshot.State)
    Assert.Equal(1L, owner.Snapshot.CompletedEvents)

    owner.CommitEvent(second, 2)
    Assert.False owner.Snapshot.IsBusy
    Assert.Equal(2, owner.Snapshot.State)
    other.CommitEvent(foreign, 0)

[<Fact>]
let ``foreign and wrong-kind capabilities cannot retire another owners work`` () =
    let owner = Owner(0)
    let other = Owner(0)
    let active = claim owner "tests"
    let foreign = claim other "tests"
    let event = owner.AdmitEvent()

    let refuse (attempt: unit -> unit) =
        Assert.Throws<InvalidOperationException>(attempt) |> ignore

    let version = owner.Store.Snapshot.Version
    refuse (fun () -> owner.CompleteRun foreign |> ignore)
    refuse (fun () -> owner.CompleteRun event |> ignore)
    refuse (fun () -> owner.FailRun(foreign, InvalidOperationException "foreign"))
    refuse (fun () -> owner.CommitEvent(active, 1))
    refuse (fun () -> owner.PublishEventState(active, 1))
    refuse (fun () -> owner.SettleEvent active)
    refuse (fun () -> owner.FailEvent(foreign, UpdateFailed(InvalidOperationException "foreign")))
    Assert.Equal(version, owner.Store.Snapshot.Version)

    Assert.True owner.Snapshot.IsBusy
    Assert.Equal(0, owner.Snapshot.State)
    owner.CommitEvent(event, 1)
    let completion = complete owner active
    owner.CommitEvent(completion, 2)
    refuse (fun () -> owner.CommitEvent(completion, 3))

    Assert.Equal(2, owner.Snapshot.State)
    Assert.False owner.Snapshot.IsBusy
    other.CommitEvent(complete other foreign, 0)
    Assert.False other.Snapshot.IsBusy

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``failure delivery cannot retire a capability of a different kind`` failEvent =
    let owner = Owner(0)
    let event = owner.AdmitEvent()
    let run = claim owner "tests"
    let failure = InvalidOperationException("wrong failure capability")

    Assert.Throws<InvalidOperationException>(fun () ->
        if failEvent then
            owner.FailEvent(run, UpdateFailed failure)
        else
            owner.FailRun(event, failure))
    |> ignore

    Assert.True owner.Snapshot.IsBusy
    Assert.True(owner.Snapshot.IsRunning "tests")
    Assert.True owner.Snapshot.Fault.IsNone
    owner.CommitEvent(event, 1)
    owner.CommitEvent(complete owner run, 2)
    Assert.False owner.Snapshot.IsBusy
    Assert.Equal(2, owner.Snapshot.State)

[<Fact>]
let ``prepared publication retains its event until exact finalization`` () =
    let owner = Owner(4)
    let identity, receipt = owner.AdmitTrackedEvent()
    let other = owner.AdmitEvent()
    let prior = owner.Snapshot

    Assert.Throws<InvalidOperationException>(fun () -> owner.SettleEvent identity)
    |> ignore

    owner.PublishEventState(identity, 9)
    Assert.Equal(9, owner.Snapshot.State)
    Assert.Equal(4, prior.State)
    Assert.True owner.Snapshot.IsBusy
    Assert.False receipt.IsCompleted
    Assert.Equal(0L, owner.Snapshot.CompletedEvents)

    Assert.Throws<InvalidOperationException>(fun () -> owner.PublishEventState(identity, 99))
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> owner.CommitEvent(identity, 99))
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> owner.SettleEvent other)
    |> ignore

    owner.SettleEvent identity
    settled receipt
    Assert.Equal(9, owner.Snapshot.State)
    Assert.True(owner.Snapshot.IsBusy, "the unrelated event remains owned")
    owner.CommitEvent(other, 10)
    Assert.False owner.Snapshot.IsBusy
    Assert.Equal(2L, owner.Snapshot.CompletedEvents)

[<Fact>]
let ``a result fold keeps its key while a successor it launched finishes first`` () =
    let owner = Owner(0)
    let first = claim owner "tests"
    let firstFold = complete owner first
    let second = owner.TryClaim("tests", after = firstFold) |> Option.get

    Assert.True((owner.TryClaim("tests", after = firstFold)).IsNone, "a fold launches at most one successor")

    Assert.True((owner.TryClaim "tests").IsNone, "a live successor holds the key")
    Assert.True(owner.Snapshot.IsRunning "tests")
    let secondFold = complete owner second
    Assert.False(owner.Snapshot.IsRunning "tests")
    Assert.False owner.Snapshot.HasExclusiveRun

    Assert.True((owner.TryClaim("tests", after = secondFold)).IsNone, "an older uncommitted fold still holds the key")

    let delivered = ResizeArray<WorkId>()
    let queued = owner.EnqueueIntent("tests", None, delivered.Add)
    owner.PublishEventState(secondFold, 2)
    owner.SettleEvent(secondFold)
    Assert.Empty delivered
    Assert.True owner.Snapshot.IsBusy
    owner.CommitEvent(firstFold, 3)
    let intent = Assert.Single delivered
    Assert.False queued.IsCompleted
    owner.CommitEvent(intent, 4)
    settled queued
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``a failed successor leaves its predecessor fold holding the key`` () =
    let owner = Owner(0)
    let run = claim owner "tests"
    let fold = complete owner run
    let successor = owner.TryClaim("tests", after = fold) |> Option.get
    let failure = InvalidOperationException("successor failed")
    owner.FailRun(successor, failure)
    Assert.True owner.Snapshot.IsBusy
    Assert.False(owner.Snapshot.IsRunning "tests")
    Assert.True((owner.TryClaim "tests").IsNone)
    owner.CommitEvent(fold, 1)
    Assert.False owner.Snapshot.IsBusy

    Assert.Same(
        failure,
        owner.Snapshot.Fault
        |> Option.defaultWith (fun () -> failwith "an older result cannot clear its successor's failure")
    )

[<Fact>]
let ``capabilities resolve within their own key when several keys are held`` () =
    let owner = Owner(0)
    let alpha = claim owner "alpha"
    let beta = claim owner "beta"
    let gamma = claim owner "gamma"
    // Lanes are visited in key order: "alpha" is inspected, and skipped, first.
    let betaFold = complete owner beta
    let alphaFold = complete owner alpha
    Assert.True owner.Snapshot.HasExclusiveRun
    owner.CommitEvent(betaFold, 1)
    owner.FailRun(gamma, InvalidOperationException "gamma failed")
    Assert.False owner.Snapshot.HasExclusiveRun
    owner.CommitEvent(alphaFold, 2)
    Assert.False owner.Snapshot.IsBusy

// ---------------------------------------------------------------------------------------
// Intents
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``queued successor is owned before predecessor receipt completes`` () =
    let store = Store()
    let owner = Owner(0, store, "queued")
    let delivered = ResizeArray<WorkId>()
    let first = owner.EnqueueIntent("tests", None, delivered.Add)
    let second = owner.EnqueueIntent("tests", None, delivered.Add)

    let observed =
        first.ContinueWith(
            Func<Task<unit>, HostSnapshot>(fun _ -> store.Snapshot),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default
        )

    owner.CommitEvent(delivered[0], 1)
    Assert.True(observed.Wait(TimeSpan.FromSeconds 5.0), "the predecessor receipt must complete")
    let atReceipt = observed.Result
    // The successor intent is the only obligation left, so busy means it is owned.
    Assert.True atReceipt.IsBusy
    Assert.Equal<string list>([ "queued" ], atReceipt.BusyNames)
    Assert.Equal(2, delivered.Count)
    Assert.False second.IsCompleted
    owner.CommitEvent(delivered[1], 2)
    settled second
    Assert.False store.Snapshot.IsBusy
    Assert.Equal(2L, store.Snapshot.CompletedEvents)

[<Fact>]
let ``queued intents follow the exact run through prepared completion before FIFO delivery`` () =
    let store = Store()
    let owner = Owner(0, store, "queued")
    let received = ResizeArray<string * WorkId>()
    let post name identity = received.Add(name, identity)
    let active = claim owner "tests"
    let first = owner.EnqueueIntent("tests", None, post "command-1")
    let automatic = owner.EnqueueIntent("tests", Some "flush", post "old-flush")
    let replacement = owner.EnqueueIntent("tests", Some "flush", post "new-flush")
    Assert.Same(automatic, replacement)
    Assert.Empty received
    let completion = complete owner active
    owner.PublishEventState(completion, 1)
    let last = owner.EnqueueIntent("tests", None, post "command-2")
    Assert.Empty received
    Assert.False first.IsCompleted
    let before = store.Snapshot
    owner.SettleEvent(completion, preparedCommit = true)
    Assert.Equal<string list>([ "command-1" ], received |> Seq.map fst |> Seq.toList)
    Assert.True store.Snapshot.IsBusy
    Assert.True before.IsBusy
    Assert.Equal(1, owner.Snapshot.State)
    Assert.False first.IsCompleted

    for index in 0..2 do
        let _, identity = received[index]
        owner.CommitEvent(identity, owner.Snapshot.State + 1)

    Assert.Equal<string list>([ "command-1"; "new-flush"; "command-2" ], received |> Seq.map fst |> Seq.toList)
    Assert.False store.Snapshot.IsBusy
    Assert.True first.IsCompletedSuccessfully
    Assert.True automatic.IsCompletedSuccessfully
    Assert.True last.IsCompletedSuccessfully

[<Fact>]
let ``commands queued before a result fold stay ahead of later successor intents`` () =
    let owner = Owner(())
    let delivered = ResizeArray<string * WorkId>()
    let firstRun = claim owner "tests"

    let earlier =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("earlier", id))

    let fold = complete owner firstRun
    let nextRun = owner.TryClaim("tests", after = fold) |> Option.get
    let later = owner.EnqueueIntent("tests", None, fun id -> delivered.Add("later", id))
    owner.CommitEvent(fold, ())
    Assert.Empty delivered
    Assert.True(owner.Snapshot.IsRunning "tests")
    owner.CommitEvent(complete owner nextRun, ())
    Assert.Equal("earlier", fst (Assert.Single delivered))
    owner.CommitEvent(snd delivered[0], ())
    Assert.Equal("later", fst delivered[1])
    owner.CommitEvent(snd delivered[1], ())
    Assert.True earlier.IsCompletedSuccessfully
    Assert.True later.IsCompletedSuccessfully
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``only the exact result fold may claim its successor before commit`` () =
    let owner = Owner(())
    let active = claim owner "tests"
    let earlierEvent = owner.AdmitEvent()

    Assert.True(
        (owner.TryClaim("tests", after = earlierEvent)).IsNone,
        "a live worker holds the key against any predecessor"
    )

    let fold = complete owner active
    Assert.True((owner.TryClaim("tests", after = earlierEvent)).IsNone)
    Assert.True((owner.TryClaim "tests").IsNone)
    let next = owner.TryClaim("tests", after = fold) |> Option.get
    owner.CommitEvent(earlierEvent, ())
    owner.CommitEvent(fold, ())
    owner.CommitEvent(complete owner next, ())
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``idle intent reserves its key until its exact fold admits work`` () =
    let owner = Owner(())
    let delivered = ResizeArray<string * WorkId>()
    let first = owner.EnqueueIntent("tests", None, fun id -> delivered.Add("first", id))

    let second =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("second", id))

    Assert.Equal<string list>([ "first" ], delivered |> Seq.map fst |> Seq.toList)
    Assert.True((owner.TryClaim "tests").IsNone)
    let firstIdentity = snd delivered[0]
    let active = owner.TryClaim("tests", after = firstIdentity) |> Option.get
    owner.CommitEvent(firstIdentity, ())
    Assert.True first.IsCompletedSuccessfully
    Assert.False second.IsCompleted
    owner.CommitEvent(complete owner active, ())
    Assert.Equal<string list>([ "first"; "second" ], delivered |> Seq.map fst |> Seq.toList)
    owner.CommitEvent(snd delivered[1], ())
    Assert.True second.IsCompletedSuccessfully
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``coalescing spans the completing run and its already admitted successor`` () =
    let owner = Owner(())
    let delivered = ResizeArray<string * WorkId>()
    let firstRun = claim owner "tests"

    let original =
        owner.EnqueueIntent("tests", Some "flush", fun id -> delivered.Add("old-flush", id))

    let earlier =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("earlier-command", id))

    let fold = complete owner firstRun
    let nextRun = owner.TryClaim("tests", after = fold) |> Option.get

    let replacement =
        owner.EnqueueIntent("tests", Some "flush", fun id -> delivered.Add("new-flush", id))

    let later =
        owner.EnqueueIntent("tests", None, fun id -> delivered.Add("later-command", id))

    Assert.Same(original, replacement)
    owner.CommitEvent(fold, ())
    owner.CommitEvent(complete owner nextRun, ())

    for index in 0..2 do
        Assert.Equal(index + 1, delivered.Count)
        owner.CommitEvent(snd delivered[index], ())

    Assert.Equal<string list>(
        [ "new-flush"; "earlier-command"; "later-command" ],
        delivered |> Seq.map fst |> Seq.toList
    )

    Assert.True original.IsCompletedSuccessfully
    Assert.True earlier.IsCompletedSuccessfully
    Assert.True later.IsCompletedSuccessfully
    Assert.False owner.Snapshot.IsBusy

[<Theory>]
[<InlineData("commit")>]
[<InlineData("prepared")>]
[<InlineData("failure")>]
let ``successor delivery failure cannot strand the retired predecessor receipt`` mode =
    let owner = Owner(0)
    let delivered = ResizeArray<WorkId>()
    let active = claim owner "tests"
    let predecessor = owner.EnqueueIntent("tests", None, delivered.Add)
    let deliveryFailure = InvalidOperationException("successor delivery failed")
    let successor = owner.EnqueueIntent("tests", None, fun _ -> raise deliveryFailure)
    owner.CommitEvent(complete owner active, 1)
    let identity = Assert.Single delivered
    let originalFailure = InvalidOperationException("predecessor update failed")

    let settle () =
        match mode with
        | "commit" -> owner.CommitEvent(identity, 2)
        | "prepared" ->
            owner.PublishEventState(identity, 2)
            owner.SettleEvent(identity, preparedCommit = true)
        | _ -> owner.FailEvent(identity, UpdateFailed originalFailure)

    let thrown = Assert.Throws<InvalidOperationException>(settle)
    Assert.Same(deliveryFailure, thrown)
    Assert.True(predecessor.IsCompleted, "a retired predecessor receipt settles even when successor delivery throws")

    if mode = "failure" then
        Assert.Same(originalFailure, Assert.Throws<InvalidOperationException>(fun () -> settled predecessor))
    else
        Assert.True predecessor.IsCompletedSuccessfully
        Assert.Equal(2, owner.Snapshot.State)

    Assert.Same(deliveryFailure, Assert.Throws<InvalidOperationException>(fun () -> settled successor))
    Assert.Same(deliveryFailure, owner.Snapshot.ExecutorFault |> Option.toObj)
    Assert.False owner.Snapshot.IsBusy

// ---------------------------------------------------------------------------------------
// Failures
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``only a committed actual worker result recovers a previous worker failure`` () =
    let owner = Owner(0)
    let failed = claim owner "tests"
    owner.FailRun(failed, InvalidOperationException("first worker failed"))
    let delivered = ResizeArray<WorkId>()
    let queued = owner.EnqueueIntent("tests", None, delivered.Add)
    owner.PublishEventState(delivered[0], 1)
    owner.SettleEvent(delivered[0], preparedCommit = true)
    Assert.True queued.IsCompletedSuccessfully
    Assert.True owner.Snapshot.Fault.IsSome
    let recovered = claim owner "tests"
    let result = complete owner recovered
    owner.PublishEventState(result, 2)
    owner.SettleEvent result
    Assert.True owner.Snapshot.Fault.IsNone
    Assert.False owner.Snapshot.IsBusy
    Assert.Equal(2, owner.Snapshot.State)

[<Fact>]
let ``published update recovery restores rest while exclusive presence tracks only the actual run`` () =
    let owner = Owner(0)
    Assert.False owner.Snapshot.HasExclusiveRun
    let bad = owner.AdmitEvent()
    owner.FailEvent(bad, UpdateFailed(InvalidOperationException("update refused")))
    Assert.True owner.Snapshot.Fault.IsSome
    let recovery = owner.AdmitEvent()
    owner.PublishEventState(recovery, 1)
    owner.SettleEvent recovery
    Assert.True owner.Snapshot.Fault.IsNone
    let run = claim owner "tests"
    Assert.True owner.Snapshot.HasExclusiveRun
    let fold = complete owner run
    Assert.False owner.Snapshot.HasExclusiveRun
    owner.CommitEvent(fold, 2)
    Assert.False owner.Snapshot.HasExclusiveRun
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``an event admitted before a failure cannot disprove it`` () =
    let owner = Owner(0)
    let older = owner.AdmitEvent()
    let failing = owner.AdmitEvent()
    let failure = InvalidOperationException("newer update failed")
    owner.FailEvent(failing, UpdateFailed failure)
    owner.CommitEvent(older, 1)
    Assert.Same(failure, owner.Snapshot.Fault |> Option.toObj)
    let newer = owner.AdmitEvent()
    owner.CommitEvent(newer, 2)
    Assert.True owner.Snapshot.Failure.IsNone

[<Fact>]
let ``commit failure survives ordinary success without stranding a live worker`` () =
    let owner = Owner(1)
    let identity, receipt = owner.AdmitTrackedEvent()
    let worker = claim owner "worker"
    let failure = IO.IOException("durable finalization failed")
    owner.PublishEventState(identity, 2)
    owner.FailEvent(identity, CommitFailed failure)
    Assert.Same(failure, Assert.Throws<IO.IOException>(fun () -> settled receipt))
    Assert.Same(failure, owner.Snapshot.Fault |> Option.toObj)
    Assert.True(owner.Snapshot.IsRunning "worker")
    owner.CommitEvent(complete owner worker, 3)
    Assert.Same(failure, owner.Snapshot.Fault |> Option.toObj)
    Assert.Equal(3, owner.Snapshot.State)
    let ordinary = owner.AdmitEvent()
    owner.PublishEventState(ordinary, 4)
    owner.SettleEvent ordinary
    Assert.Same(failure, owner.Snapshot.Fault |> Option.toObj)
    let recovery = owner.AdmitEvent()
    owner.PublishEventState(recovery, 5)
    owner.SettleEvent(recovery, preparedCommit = true)
    Assert.True owner.Snapshot.Fault.IsNone
    Assert.Equal(5, owner.Snapshot.State)

[<Theory>]
[<InlineData("commit")>]
[<InlineData("run")>]
let ``an ordinary update cannot overwrite stronger failed verification`` kind =
    let owner = Owner(0)
    let first = InvalidOperationException("verification did not commit")

    if kind = "commit" then
        let event = owner.AdmitEvent()
        owner.FailEvent(event, CommitFailed first)
    else
        let run = claim owner "tests"
        owner.MarkRunFailure(run, first)
        owner.MarkRunFailure(run, InvalidOperationException("later deadline"))
        Assert.Same(first, owner.Snapshot.Fault |> Option.toObj)
        Assert.True owner.Snapshot.IsBusy
        owner.FailRun(run, first)
        owner.MarkRunFailure(run, InvalidOperationException("late callback"))

    let unrelated = owner.AdmitEvent()
    owner.FailEvent(unrelated, UpdateFailed(InvalidOperationException("ordinary event")))
    Assert.Same(first, owner.Snapshot.Fault |> Option.toObj)
    owner.CommitEvent(owner.AdmitEvent(), 1)
    Assert.Same(first, owner.Snapshot.Fault |> Option.toObj)

    if kind = "commit" then
        let successfulRun = claim owner "tests"
        owner.FailRun(successfulRun, InvalidOperationException("run cannot erase commit failure"))
        Assert.Same(first, owner.Snapshot.Fault |> Option.toObj)

    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``a missed run deadline outranks an ordinary update failure`` () =
    let owner = Owner(0)
    let run = claim owner "tests"
    let event = owner.AdmitEvent()
    owner.FailEvent(event, UpdateFailed(InvalidOperationException("ordinary update failed")))
    let deadline = TimeoutException("run missed its deadline")
    owner.MarkRunFailure(run, deadline)
    let tracked = owner.AdmitEvent()
    owner.MarkRunFailure(tracked, InvalidOperationException("an event has no deadline to miss"))

    match owner.Snapshot.Failure with
    | Some(RunFailure failure) -> Assert.Same(deadline, failure)
    | _ -> Assert.Fail "the deadline must be recorded as a run failure"

    owner.CommitEvent(tracked, 1)
    Assert.Same(deadline, owner.Snapshot.Fault |> Option.toObj)
    owner.CommitEvent(complete owner run, 2)
    Assert.True owner.Snapshot.Fault.IsNone
    Assert.False owner.Snapshot.IsBusy

[<Fact>]
let ``every owner failure exposes its original exception`` () =
    let failure = InvalidOperationException("original")

    for recorded in
        [ UpdateFailure failure
          CommitFailure failure
          RunFailure failure
          OperationFailure failure
          ExecutorFailure failure ] do
        Assert.Same(failure, recorded.Exception)

// ---------------------------------------------------------------------------------------
// Executor faults
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``executor fault fails events but preserves workers until their real completion`` () =
    let owner = Owner(0)
    let event, receipt = owner.AdmitTrackedEvent()
    let run = claim owner "work"
    let before = owner.Snapshot
    let failure = InvalidOperationException("executor failed")
    owner.FaultExecutor failure

    Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> settled receipt))
    Assert.True owner.Snapshot.IsBusy
    Assert.True(owner.Snapshot.IsRunning "work")
    Assert.True owner.Snapshot.Fault.IsSome
    Assert.True before.Fault.IsNone
    Assert.True before.ExecutorFault.IsNone
    Assert.Equal(0, owner.Snapshot.State)
    Assert.Equal(0L, owner.Snapshot.CompletedEvents)

    Assert.Throws<InvalidOperationException>(fun () -> owner.CommitEvent(event, 99))
    |> ignore

    let refused =
        Assert.Throws<InvalidOperationException>(fun () -> owner.AdmitEvent() |> ignore)

    Assert.Same(failure, refused.InnerException)

    for admit in
        [ fun () -> owner.AdmitTrackedEvent() |> ignore
          fun () -> owner.TryClaim "other" |> ignore
          fun () -> owner.EnqueueIntent("work", None, ignore) |> ignore ] do
        Assert.Throws<InvalidOperationException>(admit) |> ignore

    Assert.True((owner.CompleteRun run).IsNone, "a dead executor cannot accept a result fold")
    Assert.False owner.Snapshot.IsBusy
    Assert.True(owner.Snapshot.Fault.IsSome, "empty work does not erase failure evidence")
    Assert.True(before.IsRunning "work", "previously published snapshots remain immutable")

[<Fact>]
let ``executor fault fails queued receipts but retains the live exclusive worker`` () =
    let store = Store()
    let owner = Owner((), store, "faulted")
    let active = claim owner "tests"

    let queued =
        owner.EnqueueIntent("tests", None, fun _ -> failwith "must not deliver after executor fault")

    let fault = InvalidOperationException("executor stopped")
    owner.FaultExecutor fault
    Assert.Same(fault, Assert.Throws<InvalidOperationException>(fun () -> settled queued))
    Assert.True store.Snapshot.IsBusy
    owner.FailRun(active, InvalidOperationException("worker drained"))
    Assert.False store.Snapshot.IsBusy
    Assert.Same(fault, owner.Snapshot.ExecutorFault |> Option.toObj)

[<Fact>]
let ``executor failure is immutable while outstanding worker capabilities drain`` () =
    let store = Store()
    let owner = Owner(0, store, "failed-owner")
    let event, receipt = owner.AdmitTrackedEvent()
    owner.PublishEventState(event, 1)
    let run = claim owner "tests"
    let first = InvalidOperationException("executor failed first")
    owner.FaultExecutor first
    owner.FaultExecutor(InvalidOperationException("later report must not replace cause"))
    Assert.Same(first, Assert.Throws<InvalidOperationException>(fun () -> settled receipt))
    Assert.Same(first, owner.Snapshot.ExecutorFault |> Option.toObj)
    Assert.Same(first, snd (Assert.Single store.Snapshot.ExecutorFaults))
    Assert.True store.Snapshot.IsBusy
    Assert.True((owner.CompleteRun run).IsNone)
    Assert.False store.Snapshot.IsBusy
    Assert.Equal(1, owner.Snapshot.State)

[<Fact>]
let ``executor fault retires every fold and queued intent but keeps a launched successor`` () =
    let owner = Owner(0)
    let delivered = ResizeArray<WorkId>()
    let untracked = owner.AdmitEvent()
    let intent = owner.EnqueueIntent("commands", None, delivered.Add)
    let queued = owner.EnqueueIntent("commands", None, delivered.Add)
    let run = claim owner "tests"
    let fold = complete owner run
    let successor = owner.TryClaim("tests", after = fold) |> Option.get
    let idleRun = claim owner "idle"
    let idleFold = complete owner idleRun
    let failure = InvalidOperationException("executor stopped")
    owner.FaultExecutor failure

    for receipt in [ intent; queued ] do
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(fun () -> settled receipt))

    Assert.True(owner.Snapshot.IsRunning "tests")
    Assert.False(owner.Snapshot.IsRunning "commands")

    for retired in [ untracked; fold; idleFold; delivered[0] ] do
        Assert.Throws<InvalidOperationException>(fun () -> owner.CommitEvent(retired, 1))
        |> ignore

    owner.FailRun(successor, InvalidOperationException("successor drained"))
    Assert.False owner.Snapshot.IsBusy
    Assert.Same(failure, owner.Snapshot.ExecutorFault |> Option.toObj)

// ---------------------------------------------------------------------------------------
// Host publication
// ---------------------------------------------------------------------------------------

[<Fact>]
let ``shared host publication retains fanout ownership between recipient admissions`` () =
    let store = Store()
    let first = Owner(0, store, "first")
    let second = Owner("initial", store, "second")
    let dispatch = store.BeginOperation "dispatch"
    let firstEvent = first.AdmitEvent()
    let before = store.Snapshot
    first.CommitEvent(firstEvent, 1)
    Assert.True(store.Snapshot.IsBusy, "fanout still owns the next recipient admission")
    Assert.Equal<string list>([ "dispatch" ], store.Snapshot.BusyNames)
    let secondEvent = second.AdmitEvent()
    store.EndOperation dispatch
    Assert.Equal<string list>([ "second" ], store.Snapshot.BusyNames)
    second.CommitEvent(secondEvent, "finished")
    let after = store.Snapshot
    Assert.False after.IsBusy
    Assert.Empty after.BusyNames
    Assert.Equal(2L, after.CompletedEvents)
    Assert.Equal(1, first.Snapshot.State)
    Assert.Equal("finished", second.Snapshot.State)
    Assert.True before.IsBusy
    Assert.Contains("first", before.BusyNames)
    Assert.Equal(0L, before.CompletedEvents)
    Assert.True(after.Version > before.Version)
    Assert.Same(store, first.Store)

[<Fact>]
let ``host snapshot retains executor fault after the last live worker settles`` () =
    let store = Store()
    let owner = Owner(0, store, "faulted")
    let neighbour = Owner(0, store, "neighbour")
    neighbour.FailEvent(neighbour.AdmitEvent(), UpdateFailed(InvalidOperationException "neighbour update"))
    let run = claim owner "work"
    let failure = InvalidOperationException("controlled fault")
    owner.FaultExecutor failure
    let running = store.Snapshot
    Assert.True running.IsBusy
    Assert.Equal<string list>([ "neighbour"; "faulted" ], running.Faults |> List.map fst |> List.sort |> List.rev)
    Assert.Same(failure, snd (Assert.Single running.ExecutorFaults))
    Assert.Empty running.OperationFaults
    Assert.True((owner.CompleteRun run).IsNone)
    Assert.False store.Snapshot.IsBusy
    Assert.Same(failure, snd (Assert.Single store.Snapshot.ExecutorFaults))
    Assert.True running.IsBusy

[<Fact>]
let ``foreign and duplicate host completions leave admitted work owned`` () =
    let store = Store()
    let other = Store()
    let work = store.BeginOperation "scan"
    let foreign = other.BeginOperation "scan"

    Assert.Throws<InvalidOperationException>(fun () -> store.EndOperation foreign)
    |> ignore

    Assert.True store.Snapshot.IsBusy
    store.EndOperation work

    Assert.Throws<InvalidOperationException>(fun () -> store.EndOperation work)
    |> ignore

    Assert.False store.Snapshot.IsBusy
    Assert.True other.Snapshot.IsBusy
    other.EndOperation foreign

// The predicate the stall detector's carve-out turns on. It has to separate three
// states that a busy host cannot otherwise tell apart, so all three are driven here:
// an ordinary operation (a dispatch fan-out) is NOT a reason to keep waiting; a
// declared bounded one is, for exactly as long as its deadline has not spoken; and once
// that deadline records a failure it stops being one, so the detector names it.
[<Fact>]
let ``only live bounded work counts as supervised work in flight`` () =
    let store = Store()

    let unbounded = store.BeginOperation "dispatch"
    Assert.True store.Snapshot.IsBusy
    Assert.False store.Snapshot.SupervisedWorkInFlight

    let bounded = store.BeginOperation("test-prune: impact selection", true)
    Assert.True store.Snapshot.SupervisedWorkInFlight

    // An ordinary operation retiring changes nothing about the declaration.
    store.EndOperation unbounded
    Assert.True store.Snapshot.SupervisedWorkInFlight

    // The deadline speaking is what ends it — not the work returning.
    Assert.True(store.FailOperation(bounded, TimeoutException("deadline expired")))
    Assert.True(store.Snapshot.IsBusy, "the work is still running; only its bound has expired")
    Assert.False store.Snapshot.SupervisedWorkInFlight

    store.EndOperation bounded
    Assert.False store.Snapshot.SupervisedWorkInFlight

[<Fact>]
let ``failed host operation stays visible after cleanup until a new attempt`` () =
    let store = Store()
    let identity = store.BeginOperation "preprocessor"
    let failure = InvalidOperationException("refused input")
    Assert.True(store.FailOperation(identity, failure))
    Assert.True(store.FailOperation(identity, InvalidOperationException("second report keeps the first cause")))
    let active = store.Snapshot
    Assert.Same(failure, snd (Assert.Single active.Faults))
    store.EndOperation identity
    Assert.True active.IsBusy
    Assert.False store.Snapshot.IsBusy
    Assert.Same(failure, snd (Assert.Single store.Snapshot.OperationFaults))
    let retry = store.BeginOperation "preprocessor"
    Assert.Empty store.Snapshot.OperationFaults
    Assert.True store.Snapshot.IsBusy
    store.EndOperation retry
    Assert.Empty store.Snapshot.Faults

[<Fact>]
let ``a retry cannot clear the failure of an overlapping host operation`` () =
    let store = Store()
    let active = store.BeginOperation "scan"
    let failure = InvalidOperationException("first scan still live")
    Assert.True(store.FailOperation(active, failure))
    let retry = store.BeginOperation "scan"
    Assert.Same(failure, snd (Assert.Single store.Snapshot.OperationFaults))
    Assert.Equal<string list>([ "scan" ], store.Snapshot.BusyNames)
    store.EndOperation retry
    store.EndOperation active
    Assert.False(store.FailOperation(active, InvalidOperationException("late callback")))
    Assert.Same(failure, snd (Assert.Single store.Snapshot.OperationFaults))

    Assert.Throws<InvalidOperationException>(fun () -> store.EndOperation active)
    |> ignore

    let unrelated = store.BeginOperation "dispatch"
    Assert.Single store.Snapshot.OperationFaults |> ignore
    store.EndOperation unrelated
    let fresh = store.BeginOperation "scan"
    Assert.Empty store.Snapshot.OperationFaults
    store.EndOperation fresh

[<Fact>]
let ``a row publishes its projection with its value and a failed change publishes nothing`` () =
    let store = Store()

    let project (value: int) =
        RowStatus.ofWork
            (value > 0)
            (int64 value)
            (if value = 7 then
                 Some(OperationFailure(InvalidOperationException "seven"))
             else
                 None)

    let row = store.Register("queue", 0, project)
    let quiet = store.Register("quiet", (), fun () -> idle ())
    Assert.False store.Snapshot.IsBusy
    let pinned = store.Snapshot
    let label = store.Change(row, (fun _ value -> (value + 7, "published")))
    Assert.Equal("published", label)
    Assert.Equal(7, store.Read row)
    Assert.Equal(0, pinned.Row row)
    Assert.Equal<string list>([ "queue" ], store.Snapshot.BusyNames)
    Assert.Equal(7L, store.Snapshot.CompletedEvents)
    Assert.Equal("seven", (snd (Assert.Single store.Snapshot.OperationFaults)).Message)
    Assert.Empty store.Snapshot.ExecutorFaults
    let version = store.Snapshot.Version
    let failure = InvalidOperationException("transition refused")

    Assert.Same(
        failure,
        Assert.Throws<InvalidOperationException>(fun () ->
            store.Change(row, (fun _ (_: int) -> (raise failure: int * unit))))
    )

    Assert.Equal(version, store.Snapshot.Version)
    Assert.Equal(7, store.Read row)
    store.Change(quiet, (fun _ () -> ((), ())))
    Assert.Equal(version + 1L, store.Snapshot.Version)

    let foreign = Store().Register("foreign", 0, project)

    Assert.Throws<InvalidOperationException>(fun () -> store.Snapshot.Row foreign |> ignore)
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> store.Change(foreign, (fun _ value -> (value, ()))))
    |> ignore

[<Theory(Timeout = 30000)>]
[<InlineData("publish")>]
[<InlineData("settle")>]
[<InlineData("complete-run")>]
let ``owner transitions retain their result until actual store publication`` operation =
    task {
        let store = Store()
        let owner = Owner(0, store, "publication-boundary")
        let event, receipt = owner.AdmitTrackedEvent()

        if operation <> "publish" then
            owner.PublishEventState(event, 1)

        let run =
            if operation = "complete-run" then
                owner.SettleEvent event
                owner.TryClaim "tests"
            else
                None

        let blocker = store.Register("held publication writer", (), fun () -> idle ())
        // Continuations run off the writer: the test body must never resume inside the change it holds.
        let entered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        use release = new ManualResetEventSlim(false)

        let held =
            store.ChangeAsync(
                blocker,
                fun _ () ->
                    entered.TrySetResult(()) |> ignore

                    if not (release.Wait(TimeSpan.FromSeconds 15.0)) then
                        invalidOp "fixture never released the held publication"

                    ((), ())
            )

        try
            // Every wait below is for an ORDER, not a latency: under a starved pool the posting
            // thread can start arbitrarily late, and only the test's Timeout bounds a hang.
            do! entered.Task
            let before = store.Snapshot

            let transition =
                Task.Run(
                    Func<WorkId option>(fun () ->
                        match operation with
                        | "publish" ->
                            owner.PublishEventState(event, 1)
                            None
                        | "settle" ->
                            owner.SettleEvent event
                            None
                        | _ -> owner.CompleteRun(Option.get run))
                )

            // Wait until the transition is queued behind the held change, or has already
            // returned — the defect this test exists to catch. Then say which.
            while store.PendingChanges <> 1 && not transition.IsCompleted do
                do! Task.Delay 1

            Assert.False(transition.IsCompleted, "a queued transition cannot return before its publication")
            Assert.Equal(1, store.PendingChanges)
            Assert.Equal(before.Version, store.Snapshot.Version)
            release.Set()
            let! next = transition

            match operation, next with
            | "publish", _ -> owner.SettleEvent event
            | "complete-run", Some fold -> owner.CommitEvent(fold, 2)
            | _ -> ()

            settled receipt
            Assert.False store.Snapshot.IsBusy
            Assert.True owner.Snapshot.Fault.IsNone
            let expected = if operation = "complete-run" then 2 else 1
            Assert.Equal(expected, owner.Snapshot.State)
            Assert.Equal(int64 expected, store.Snapshot.CompletedEvents)
        finally
            release.Set()
            held.Wait(TimeSpan.FromSeconds 5.0) |> ignore
    }

[<Fact>]
let ``a finished run owes its verdict until its result fold commits, except to that fold`` () =
    let owner = Owner(0)
    Assert.False(owner.Snapshot.OwesRunVerdict None)

    let run = claim owner "tests"
    Assert.True(owner.Snapshot.OwesRunVerdict None)

    // The worker has finished; its result fold is queued. The run is not live, but its
    // verdict is still owed to every event except the fold that carries it.
    let fold = complete owner run
    Assert.False(owner.Snapshot.IsRunning "tests")
    Assert.True(owner.Snapshot.OwesRunVerdict None)
    Assert.True(owner.Snapshot.OwesRunVerdict(Some(owner.AdmitEvent())))
    Assert.False(owner.Snapshot.OwesRunVerdict(Some fold))

    owner.CommitEvent(fold, 1)
    Assert.False(owner.Snapshot.OwesRunVerdict None)

[<Fact>]
let ``a result fold that launched the next run owes that run's verdict, even to itself`` () =
    // The successor is live, so its Running stands against every report, including the
    // fold that launched it: that fold's terminal would describe the finished run.
    let owner = Owner(0)
    let fold = complete owner (claim owner "tests")
    let successor = owner.TryClaim("tests", after = fold) |> Option.get
    Assert.True(owner.Snapshot.IsRunning "tests")
    Assert.True(owner.Snapshot.OwesRunVerdict(Some fold))

    owner.CommitEvent(fold, 1)
    Assert.True(owner.Snapshot.OwesRunVerdict None)
    owner.CommitEvent(complete owner successor, 2)
    Assert.False(owner.Snapshot.OwesRunVerdict None)

[<Fact>]
let ``a delivered intent holds its key without owing a run verdict`` () =
    let owner = Owner(0)
    let delivered = ResizeArray<WorkId>()
    let _receipt = owner.EnqueueIntent("tests", None, delivered.Add)
    let intent = Assert.Single delivered
    Assert.True owner.Snapshot.IsBusy
    Assert.False(owner.Snapshot.OwesRunVerdict None)
    owner.CommitEvent(intent, 1)
