/// A project's checks wait for the projects it references, not for its whole dependency
/// tier (AUTOMATION-683, ADR-056). Every wait here is on a completion the test controls:
/// nothing sleeps.
module FsHotWatch.Tests.DependentChecksTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

let private file (name: string) = AbsFilePath.create $"/repo/%s{name}.fs"

let private node key after files : DependentWork<string> =
    { Key = key
      After = after
      Files = files |> List.map file }

let private pending () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let private within = TimeSpan.FromSeconds 10.0

let private run maxRetries maxInFlight check (work: DependentWork<string> list) (ct: CancellationToken) =
    Async.StartAsTask(
        runDependentChecks maxRetries maxInFlight check (fun (_: string) -> async.Zero()) work,
        cancellationToken = ct
    )

[<Fact(Timeout = 20000)>]
let ``a project starts when its references finish, not when its tier does`` () =
    // A and B are one tier; C references only A. B is held: C must not wait for it.
    let holdB = pending ()
    let cStarted = new ManualResetEventSlim(false)

    let check (key: string) (_: AbsFilePath) =
        async {
            match key with
            | "B" -> do! Async.AwaitTask holdB.Task
            | "C" -> cStarted.Set()
            | _ -> ()

            return Some key
        }

    let running =
        run 0 4 check [ node "A" [] [ "a" ]; node "B" [] [ "b" ]; node "C" [ "A" ] [ "c" ] ] CancellationToken.None

    try
        test <@ cStarted.Wait within @>
        test <@ not running.IsCompleted @>
    finally
        holdB.TrySetResult() |> ignore

    test <@ running.Wait within @>
    test <@ running.Result = ScanCheckOutcome.AllChecked 0 @>

[<Fact(Timeout = 20000)>]
let ``a project never starts before its references finish, retry rounds included`` () =
    // A's second file is cancelled once; its retry round must finish before C starts.
    let events = ConcurrentQueue<string>()
    let attempts = ConcurrentDictionary<AbsFilePath, int>()

    let check (key: string) (f: AbsFilePath) =
        async {
            let attempt = attempts.AddOrUpdate(f, 1, fun _ n -> n + 1)
            events.Enqueue $"start %s{key} %s{AbsFilePath.value f} #%d{attempt}"

            let result = if f = file "a2" && attempt = 1 then None else Some key

            events.Enqueue $"end %s{key} %s{AbsFilePath.value f} #%d{attempt}"
            return result
        }

    let outcome =
        (run 2 4 check [ node "A" [] [ "a1"; "a2" ]; node "C" [ "A" ] [ "c" ] ] CancellationToken.None).Result

    test <@ outcome = ScanCheckOutcome.AllChecked 1 @>
    let order = List.ofSeq events
    let lastA = order |> List.findIndexBack (fun e -> e.StartsWith "end A")
    let firstC = order |> List.findIndex (fun e -> e.StartsWith "start C")
    test <@ lastA < firstC @>
    test <@ attempts[file "a2"] = 2 @>

[<Fact(Timeout = 20000)>]
let ``at most maxInFlight checks are started and unfinished, across projects`` () =
    let release = pending ()
    let inFlight = ref 0
    let peak = ref 0
    let gate = obj ()

    let check (_: string) (_: AbsFilePath) =
        async {
            lock gate (fun () ->
                inFlight.Value <- inFlight.Value + 1
                peak.Value <- max peak.Value inFlight.Value)

            do! Async.AwaitTask release.Task
            lock gate (fun () -> inFlight.Value <- inFlight.Value - 1)
            return Some 1
        }

    let work =
        [ node "A" [] [ "a1"; "a2"; "a3" ]
          node "B" [] [ "b1"; "b2"; "b3" ]
          node "C" [] [ "c1"; "c2"; "c3" ] ]

    let running =
        Async.StartAsTask(runDependentChecks 0 2 check (fun (_: int) -> async.Zero()) work)

    waitUntil (fun () -> lock gate (fun () -> inFlight.Value = 2)) 10000
    test <@ lock gate (fun () -> peak.Value) = 2 @>
    release.SetResult()
    test <@ running.Wait within @>
    test <@ running.Result = ScanCheckOutcome.AllChecked 0 @>
    test <@ peak.Value = 2 @>

[<Fact(Timeout = 20000)>]
let ``a project whose budget runs out reports its files, and its dependents still run`` () =
    let calls = ConcurrentDictionary<string, int>()

    let check (key: string) (_: AbsFilePath) =
        async {
            calls.AddOrUpdate(key, 1, fun _ n -> n + 1) |> ignore
            return if key = "A" then None else Some key
        }

    let outcome =
        (run 2 4 check [ node "A" [] [ "a" ]; node "C" [ "A" ] [ "c" ] ] CancellationToken.None).Result

    test <@ outcome = ScanCheckOutcome.BudgetExhausted([ file "a" ], 2, 2) @>
    test <@ calls["A"] = 3 && calls["C"] = 1 @>

[<Fact(Timeout = 20000)>]
let ``extra rounds are summed across projects`` () =
    let attempts = ConcurrentDictionary<AbsFilePath, int>()

    let check (key: string) (f: AbsFilePath) =
        async {
            let attempt = attempts.AddOrUpdate(f, 1, fun _ n -> n + 1)
            return if attempt = 1 then None else Some key
        }

    let outcome =
        (run 3 4 check [ node "A" [] [ "a" ]; node "B" [] [ "b" ] ] CancellationToken.None).Result

    test <@ outcome = ScanCheckOutcome.AllChecked 2 @>

[<Fact(Timeout = 20000)>]
let ``a check that throws fails the run, and nothing after it starts`` () =
    let started = ConcurrentQueue<string>()

    let check (key: string) (_: AbsFilePath) =
        async {
            started.Enqueue key

            if key = "B" then
                failwith "the check of B failed"

            return Some key
        }

    let failed =
        Assert.ThrowsAny<Exception>(fun () ->
            (run 0 1 check [ node "B" [] [ "b" ]; node "C" [ "B" ] [ "c" ] ] CancellationToken.None).Wait within
            |> ignore)

    test <@ failed.GetBaseException().Message.Contains "the check of B failed" @>
    test <@ List.ofSeq started = [ "B" ] @>

[<Fact(Timeout = 20000)>]
let ``a cancelled run starts nothing more`` () =
    let holdA = pending ()
    let started = ConcurrentQueue<string>()
    let aStarted = new ManualResetEventSlim(false)

    let check (key: string) (_: AbsFilePath) =
        async {
            started.Enqueue key

            if key = "A" then
                aStarted.Set()
                do! Async.AwaitTask holdA.Task

            return Some key
        }

    use cancel = new CancellationTokenSource()

    let running =
        run 0 1 check [ node "A" [] [ "a" ]; node "C" [ "A" ] [ "c" ] ] cancel.Token

    test <@ aStarted.Wait within @>
    cancel.Cancel()
    holdA.SetResult()
    Assert.ThrowsAny<Exception>(fun () -> running.Wait within |> ignore) |> ignore
    test <@ running.IsCanceled || running.IsFaulted @>
    test <@ List.ofSeq started = [ "A" ] @>

[<Fact(Timeout = 20000)>]
let ``a reference outside the work is no reason to wait, and an empty project releases its dependents`` () =
    let checkedKeys = ConcurrentQueue<string>()

    let check (key: string) (_: AbsFilePath) =
        async {
            checkedKeys.Enqueue key
            return Some key
        }

    let outcome =
        (run 0 2 check [ node "Gated" [] []; node "A" [ "Gated"; "NotInThisRun" ] [ "a" ] ] CancellationToken.None)
            .Result

    test <@ outcome = ScanCheckOutcome.AllChecked 0 @>
    test <@ List.ofSeq checkedKeys = [ "A" ] @>

[<Fact(Timeout = 20000)>]
let ``no work is checked at once`` () =
    let outcome =
        (run 0 2 (fun (_: string) (_: AbsFilePath) -> async { return Some "x" }) [] CancellationToken.None).Result

    test <@ outcome = ScanCheckOutcome.AllChecked 0 @>

[<Fact(Timeout = 20000)>]
let ``a result is emitted as its own check returns, not when its project's round does`` () =
    // The scan's progress counter advances in `emit`: the slow file waits for the fast
    // one's emit, which only happens if emits do not wait for the round.
    let fastEmitted = new ManualResetEventSlim(false)

    let check (_: string) (f: AbsFilePath) =
        async {
            if f = file "slow" then
                return Some(fastEmitted.Wait within)
            else
                return Some true
        }

    let emitted = ConcurrentQueue<bool>()

    let emit (sawFast: bool) =
        async {
            emitted.Enqueue sawFast
            fastEmitted.Set()
        }

    let outcome =
        runDependentChecks 0 4 check emit [ node "A" [] [ "fast"; "slow" ] ]
        |> Async.RunSynchronously

    test <@ outcome = ScanCheckOutcome.AllChecked 0 @>
    test <@ emitted |> Seq.forall id @>
