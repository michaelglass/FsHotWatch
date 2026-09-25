/// A run declared `PluginWork.resultFirst` hands its result to the plugin's mailbox ahead
/// of the dispatched events queued before it: the result folds once the fold in flight
/// commits, not after every event behind that fold. The plugin's own messages keep their
/// order, dispatched events keep theirs, and an undeclared run's result still waits its
/// turn.
///
/// Every fold here records itself, and a `slow…` fold then blocks on a gate the test
/// holds, so "the result folded before the events behind the fold in flight" is decided by
/// the order folds ran in, never by timing.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.PluginFrameworkResultFirstTests

open System
open System.Collections.Concurrent
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Plugin
open FsHotWatch.PluginFramework
open FsHotWatch.Tests.PluginFrameworkFixtures

type private Msg =
    | Finished of run: int
    | Progress of run: int

[<NoComparison; NoEquality>]
type private Harness =
    {
        Registration: RegisteredPlugin
        /// Each fold's line, in the order the folds ran.
        Folds: ConcurrentQueue<string>
        Statuses: ConcurrentQueue<PluginStatus>
        /// Set as the named fold starts.
        Started: string -> ManualResetEventSlim
        /// Let the named `slow…` fold return.
        Release: string -> unit
        /// Let run `n` return its result.
        FinishRun: int -> unit
        /// Set once run `n`'s worker has retired: its result is in the mailbox.
        Retired: int -> ManualResetEventSlim
        ResultFolded: int -> ManualResetEventSlim
        ReleaseEverything: unit -> unit
    }

let private bound = TimeSpan.FromSeconds 10.0

/// A plugin whose `FileChanged` events are named by their one file. `launch` and
/// `claim…` try to claim run `n` under "work"; `slow…` blocks until released. A run
/// waits for the test, optionally posts `Progress` from the worker, and returns
/// `Finished`, declared `resultFirst` when `resultFirst` is set.
let private harness (resultFirst: bool) (postProgress: bool) : Harness =
    let signal () = new ManualResetEventSlim(false)
    let started = ConcurrentDictionary<string, ManualResetEventSlim>()
    let gates = ConcurrentDictionary<string, ManualResetEventSlim>()
    let runGates = ConcurrentDictionary<int, ManualResetEventSlim>()
    let retired = ConcurrentDictionary<int, ManualResetEventSlim>()
    let folded = ConcurrentDictionary<int, ManualResetEventSlim>()
    let folds = ConcurrentQueue<string>()
    let statuses = ConcurrentQueue<PluginStatus>()
    let get (table: ConcurrentDictionary<'K, ManualResetEventSlim>) key = table.GetOrAdd(key, fun _ -> signal ())
    // Moved only by folds, which the mailbox runs one at a time.
    let mutable runs = 0
    // The run a worker belongs to, read by the host's `StartAsync` wrapper.
    let launching = ConcurrentQueue<int>()

    let handler =
        { Name = PluginName.create "result-first"
          Init = ()
          Update =
            fun ctx state event ->
                async {
                    match event with
                    | FileChanged(SourceChanged [ name ]) ->
                        let claim =
                            if name = "launch" || name.StartsWith "claim" then
                                let run = runs + 1

                                let work =
                                    async {
                                        (get runGates run).Wait()

                                        if postProgress then
                                            ctx.Post(Progress run)

                                        return Finished run
                                    }

                                let work = if resultFirst then PluginWork.resultFirst work else work
                                launching.Enqueue run

                                match ctx.RunExclusive "work" work with
                                | Claimed ->
                                    runs <- run
                                    $" claimed run %d{run}"
                                | SlotBusy ->
                                    launching.TryDequeue() |> ignore
                                    " slot busy"
                            else
                                ""

                        folds.Enqueue(name + claim)
                        (get started name).Set()

                        if name.StartsWith "slow" then
                            (get gates name).Wait()
                    | Custom(Progress run) -> folds.Enqueue $"progress %d{run}"
                    | Custom(Finished run) ->
                        folds.Enqueue $"result %d{run}"

                        ctx.ReportStatus(
                            Completed(DateTime.UtcNow, RunVerdict.create $"run %d{run} folded" TimeSpan.Zero)
                        )

                        (get folded run).Set()
                    | _ -> ()

                    return state
                }
          Commands = []
          Subscriptions = Set.singleton SubscribeFileChanged
          CacheKey = None
          PrepareCommit = None
          Teardown = None }

    let services =
        { defaultServices with
            ReportStatus = fun _ status -> statuses.Enqueue status
            // The worker is the only thing the framework starts here. It retires by handing
            // its result to the mailbox, so once it returns the result is queued.
            StartAsync =
                fun work ->
                    let run =
                        match launching.TryDequeue() with
                        | true, run -> run
                        | _ -> failwith "a worker started with no run launching"

                    Async.Start(
                        async {
                            do! work
                            (get retired run).Set()
                        }
                    ) }

    { Registration = registerHandler services handler
      Folds = folds
      Statuses = statuses
      Started = get started
      Release = fun name -> (get gates name).Set()
      FinishRun = fun run -> (get runGates run).Set()
      Retired = get retired
      ResultFolded = get folded
      ReleaseEverything =
        fun () ->
            for gate in gates.Values do
                gate.Set()

            for gate in runGates.Values do
                gate.Set() }

let private changed name =
    DispatchFileChanged(SourceChanged [ name ])

/// Run 1 is live, `slow-1` holds the mailbox, and `behind` are queued after it. Run 1
/// then finishes, so its result is queued behind all of them.
let private resultQueuedBehind (h: Harness) (behind: string list) =
    dispatchAndAwait h.Registration (changed "launch")
    h.Registration.Dispatch(changed "slow-1")
    test <@ (h.Started "slow-1").Wait bound @>

    for name in behind do
        h.Registration.Dispatch(changed name)

    h.FinishRun 1
    test <@ (h.Retired 1).Wait bound @>

let private folds (h: Harness) = h.Folds.ToArray() |> List.ofArray

let private resultFoldsAheadOfTheQueue () =
    let h = harness true false

    try
        resultQueuedBehind h [ "slow-2"; "slow-3"; "slow-4"; "slow-5" ]
        h.Release "slow-1"

        // `slow-2` .. `slow-5` each block until released, and none is: a result that
        // waited for them would never fold.
        test <@ (h.ResultFolded 1).Wait bound @>
        test <@ List.truncate 3 (folds h) = [ "launch claimed run 1"; "slow-1"; "result 1" ] @>

        // `slow-2` folds next and holds the mailbox. The run's verdict is the status the
        // plugin shows, and the key is free: nothing is left of run 1 to wait for.
        test <@ (h.Started "slow-2").Wait bound @>

        test
            <@
                match Seq.tryLast h.Statuses with
                | Some(Completed(_, verdict)) -> verdict.Summary = "run 1 folded"
                | _ -> false
            @>

        test <@ not ((h.Started "slow-3").IsSet) @>
    finally
        h.ReleaseEverything()

[<Fact(Timeout = 30000)>]
let ``a result-first run's result folds after the fold in flight, not after the events queued behind it`` () =
    resultFoldsAheadOfTheQueue ()

[<Fact(Timeout = 60000)>]
let ``a result-first run's result folds ahead of the queue with every pool thread busy`` () =
    TestHelpers.withEveryPoolThreadBusy resultFoldsAheadOfTheQueue

/// The guarantee the reorder must keep: a claim of run N+1 is only ever made by a fold
/// that runs after run N's result has folded. The events that overtaken still claim, in
/// the order they were dispatched.
[<Fact(Timeout = 30000)>]
let ``no fold claims the next run before the previous run's result has folded`` () =
    let h = harness true false

    try
        resultQueuedBehind h [ "claim-a"; "claim-b"; "claim-c" ]
        h.Release "slow-1"
        test <@ (h.ResultFolded 1).Wait bound @>
        // Run 2 is result-first as well: finishing it before `claim-c` is taken would let
        // its result overtake `claim-c` too.
        test <@ (h.Started "claim-c").Wait bound @>
        h.FinishRun 2
        test <@ (h.ResultFolded 2).Wait bound @>

        let order = folds h

        test
            <@
                order = [ "launch claimed run 1"
                          "slow-1"
                          "result 1"
                          "claim-a claimed run 2"
                          "claim-b slot busy"
                          "claim-c slot busy"
                          "result 2" ]
            @>

        let firstClaimOfRun2 =
            order |> List.findIndex (fun line -> line.EndsWith "claimed run 2")

        test <@ List.findIndex ((=) "result 1") order < firstClaimOfRun2 @>
    finally
        h.ReleaseEverything()

/// A message the worker posted before returning was queued before its result, and folds
/// before it: the plugin's own messages move ahead of the dispatched events together.
[<Fact(Timeout = 30000)>]
let ``a message the run posted folds before its result, and both ahead of the dispatched events`` () =
    let h = harness true true

    try
        resultQueuedBehind h [ "slow-2"; "slow-3" ]
        h.Release "slow-1"
        test <@ (h.ResultFolded 1).Wait bound @>

        test <@ List.truncate 4 (folds h) = [ "launch claimed run 1"; "slow-1"; "progress 1"; "result 1" ] @>
    finally
        h.ReleaseEverything()

/// Reordering is declared, never assumed: a plugin whose result fold depends on the
/// events dispatched before it keeps its first-in, first-out mailbox.
[<Fact(Timeout = 30000)>]
let ``an undeclared run's result still waits behind the events queued before it`` () =
    let h = harness false false

    try
        resultQueuedBehind h [ "slow-2" ]
        h.Release "slow-1"
        test <@ (h.Started "slow-2").Wait bound @>
        test <@ not (h.ResultFolded 1).IsSet @>
        h.Release "slow-2"
        test <@ (h.ResultFolded 1).Wait bound @>
        test <@ folds h = [ "launch claimed run 1"; "slow-1"; "slow-2"; "result 1" ] @>
    finally
        h.ReleaseEverything()
