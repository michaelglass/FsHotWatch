module FsHotWatch.Tests.SuspectRecheckTests

// `SuspectRecheck.run` drops the answers the daemon could not trust and checks those
// files again, in this process, so the test evidence the process holds survives. These
// pin the order of its steps and the one condition that stops it.

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.PluginFramework
open FsHotWatch.SuspectRecheck
open FsHotWatch.Tests.TestHelpers

/// Seams that record every call, in order, into `calls`. `suspectBefore` answers the
/// first read of the suspect files and `suspectAfter` every later one.
let private recording
    (calls: ResizeArray<string>)
    (holders: string list)
    (suspectBefore: string list)
    (suspectAfter: string list)
    =
    let reads = ref 0

    { ExclusiveHolders =
        fun () ->
            calls.Add "holders"
            holders
      SuspectFiles =
        fun () ->
            calls.Add "suspects"
            reads.Value <- reads.Value + 1
            if reads.Value = 1 then suspectBefore else suspectAfter
      Drop = fun file -> calls.Add $"drop %s{file}"
      Recheck =
        fun () ->
            async {
                calls.Add "recheck"
                return ()
            } }

[<Fact>]
let ``every suspect file is dropped before the one re-check, and a clean re-check leaves no survivor`` () =
    let calls = ResizeArray()

    let outcome =
        run (recording calls [] [ "/r/A.fs"; "/r/B.fs" ] []) |> Async.RunSynchronously

    test <@ outcome = Outcome.Rechecked([ "/r/A.fs"; "/r/B.fs" ], []) @>

    test <@ List.ofSeq calls = [ "holders"; "suspects"; "drop /r/A.fs"; "drop /r/B.fs"; "recheck"; "suspects" ] @>

[<Fact>]
let ``a file still suspect after the re-check is reported as a survivor`` () =
    let calls = ResizeArray()

    let outcome =
        run (recording calls [] [ "/r/A.fs"; "/r/B.fs" ] [ "/r/B.fs" ])
        |> Async.RunSynchronously

    test <@ outcome = Outcome.Rechecked([ "/r/A.fs"; "/r/B.fs" ], [ "/r/B.fs" ]) @>

[<Fact>]
let ``a suspect answer that first appears during the re-check is not one it dropped, so it is not its survivor`` () =
    let calls = ResizeArray()

    let outcome =
        run (recording calls [] [ "/r/A.fs" ] [ "/r/C.fs" ]) |> Async.RunSynchronously

    test <@ outcome = Outcome.Rechecked([ "/r/A.fs" ], []) @>

[<Fact>]
let ``while an exclusive run is held nothing is dropped and nothing is re-checked`` () =
    let calls = ResizeArray()

    let outcome =
        run (recording calls [ "test-prune" ] [ "/r/A.fs" ] [])
        |> Async.RunSynchronously

    test <@ outcome = Outcome.Refused [ "test-prune" ] @>
    test <@ List.ofSeq calls = [ "holders" ] @>

[<Fact>]
let ``with no suspect file there is nothing to drop and no re-check is started`` () =
    let calls = ResizeArray()

    let outcome = run (recording calls [] [] []) |> Async.RunSynchronously

    test <@ outcome = Outcome.Rechecked([], []) @>
    test <@ List.ofSeq calls = [ "holders"; "suspects" ] @>

// --- a suspect answer never enters the check-result cache ----------------------

let private doesNotMatch (left: string) (right: string) =
    $"The type '%s{left}' does not match the type '%s{right}'"

[<Fact>]
let ``an answer that declares a type incompatible with itself is not cached, so an unchanged file is asked again`` () =
    test <@ not (CheckPipeline.cacheableAnswer [ doesNotMatch "Lib.Domain.Order" "Lib.Domain.Order" ]) @>

    test
        <@
            not (
                CheckPipeline.cacheableAnswer
                    [ "The value or constructor 'x' is not defined."
                      doesNotMatch "Lib.Domain.Order" "Lib.Domain.Order" ]
            )
        @>

[<Fact>]
let ``a clean answer and a genuine mismatch are still cached`` () =
    // POSITIVE CONTROL: refusing to cache every answer would make every scan cold.
    test <@ CheckPipeline.cacheableAnswer [] @>
    test <@ CheckPipeline.cacheableAnswer [ doesNotMatch "int" "string" ] @>

// --- the host names what holds an exclusive run ----------------------------------

type private HolderMsg = | HolderDone

/// A plugin that claims the "tests" key on a build and holds it until `workerGo` is set.
let private holdingHandler (workerGo: ManualResetEventSlim) (claimed: ManualResetEventSlim) =
    { Name = PluginName.create "holder"
      Init = ()
      Update =
        fun ctx state event ->
            async {
                match event with
                | BuildCompleted _ ->
                    match
                        ctx.RunExclusive
                            "tests"
                            (async {
                                claimed.Set()
                                workerGo.Wait()
                                return HolderDone
                            })
                    with
                    | Claimed -> ()
                    | SlotBusy -> failwith "test setup: expected to claim the tests key"
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeBuildCompleted ]
      PrepareCommit = None
      CacheKey = None
      Teardown = None }

[<Fact(Timeout = 60_000)>]
let ``the host names a plugin holding an exclusive run until its result fold commits`` () =
    use workerGo = new ManualResetEventSlim(false)
    use claimed = new ManualResetEventSlim(false)
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp")

    try
        host.RegisterHandler(holdingHandler workerGo claimed)
        test <@ List.isEmpty (host.ExclusiveHolders()) @>

        host.EmitBuildCompleted(BuildSucceeded)
        Assert.True(claimed.Wait(TimeSpan.FromSeconds 10.0), "the run must start")
        test <@ host.ExclusiveHolders() = [ "holder" ] @>

        workerGo.Set()
        test <@ waitUntilTrue (fun () -> not (host.AnyPluginBusy())) 10_000 @>
        test <@ List.isEmpty (host.ExclusiveHolders()) @>
    finally
        workerGo.Set()

// --- the automatic re-check a settling check makes -------------------------------

/// Settle seams over a recording `run`: `holders` answers each exclusive-holder read in
/// turn (the last answer repeats), `suspectsAfter` is what a re-check leaves suspect.
let private settleSeams
    (calls: ResizeArray<string>)
    (tree: string option)
    (holders: string list list)
    (suspectBefore: string list)
    (suspectAfter: string list)
    =
    let holderReads = ref holders
    let rechecked = ref false

    { Recheck =
        { ExclusiveHolders =
            fun () ->
                match holderReads.Value with
                | [ last ] -> last
                | next :: rest ->
                    holderReads.Value <- rest
                    next
                | [] -> []
          SuspectFiles = fun () -> if rechecked.Value then suspectAfter else suspectBefore
          Drop = fun file -> calls.Add $"drop %s{file}"
          Recheck =
            fun () ->
                async {
                    calls.Add "recheck"
                    rechecked.Value <- true
                } }
      TreeHash = fun () -> tree
      AwaitQuiet = fun () -> async { calls.Add "await quiet" }
      Log = fun line -> calls.Add $"log %s{line}" }

[<Fact>]
let ``a settling check holding suspect entries re-checks them once, settles again, and logs the outcome`` () =
    let calls = ResizeArray()
    let retried = RetriedTrees()

    let outcome =
        settle retried (settleSeams calls (Some "tree-1") [ [] ] [ "/r/A.fs" ] [])
        |> Async.RunSynchronously

    let cleared = Outcome.Rechecked([ "/r/A.fs" ], [])
    let logged = "log " + logLine cleared
    test <@ outcome = Some cleared @>
    test <@ List.ofSeq calls = [ "drop /r/A.fs"; "recheck"; "await quiet"; logged ] @>

[<Fact>]
let ``a tree gets one automatic re-check, so an answer that stays suspect is not retried again`` () =
    let calls = ResizeArray()
    let retried = RetriedTrees()
    let seams = settleSeams calls (Some "tree-1") [ [] ] [ "/r/A.fs" ] [ "/r/A.fs" ]

    let first = settle retried seams |> Async.RunSynchronously
    let second = settle retried seams |> Async.RunSynchronously

    test <@ first = Some(Outcome.Rechecked([ "/r/A.fs" ], [ "/r/A.fs" ])) @>
    test <@ second = None @>
    test <@ calls |> Seq.filter ((=) "recheck") |> Seq.length = 1 @>

[<Fact>]
let ``a different tree gets its own re-check`` () =
    let calls = ResizeArray()
    let retried = RetriedTrees()

    settle retried (settleSeams calls (Some "tree-1") [ [] ] [ "/r/A.fs" ] [ "/r/A.fs" ])
    |> Async.RunSynchronously
    |> ignore

    let other =
        settle retried (settleSeams calls (Some "tree-2") [ [] ] [ "/r/A.fs" ] [])
        |> Async.RunSynchronously

    test <@ other = Some(Outcome.Rechecked([ "/r/A.fs" ], [])) @>
    test <@ calls |> Seq.filter ((=) "recheck") |> Seq.length = 2 @>

[<Fact>]
let ``with nothing suspect no tree is claimed and nothing is scanned`` () =
    let calls = ResizeArray()
    let retried = RetriedTrees()

    let outcome =
        settle retried (settleSeams calls (Some "tree-1") [ [] ] [] [])
        |> Async.RunSynchronously

    test <@ outcome = None @>
    test <@ calls.Count = 0 @>
    // The tree's one re-check is still available for a later settle that needs it.
    test <@ retried.TryClaim "tree-1" @>

[<Fact>]
let ``a tree that cannot be read is not re-checked, since its one re-check could not be counted`` () =
    let calls = ResizeArray()

    let outcome =
        settle (RetriedTrees()) (settleSeams calls None [ [] ] [ "/r/A.fs" ] [])
        |> Async.RunSynchronously

    test <@ outcome = None @>
    test <@ not (calls.Contains "recheck") @>

[<Fact>]
let ``a held exclusive run is waited for, never interrupted, and the re-check runs after it`` () =
    let calls = ResizeArray()

    let outcome =
        settle (RetriedTrees()) (settleSeams calls (Some "tree-1") [ [ "test-prune" ]; [] ] [ "/r/A.fs" ] [])
        |> Async.RunSynchronously

    test <@ outcome = Some(Outcome.Rechecked([ "/r/A.fs" ], [])) @>
    // The wait comes first; nothing is dropped while the run holds its key.
    test <@ List.ofSeq calls |> List.take 3 = [ "await quiet"; "drop /r/A.fs"; "recheck" ] @>

[<Fact>]
let ``a host that never releases its exclusive runs is waited for a bounded number of times`` () =
    let calls = ResizeArray()

    let outcome =
        settle (RetriedTrees()) (settleSeams calls (Some "tree-1") [ [ "test-prune" ] ] [ "/r/A.fs" ] [])
        |> Async.RunSynchronously

    test <@ outcome = Some(Outcome.Refused [ "test-prune" ]) @>
    test <@ calls |> Seq.filter ((=) "await quiet") |> Seq.length = MaxExclusiveWaits @>
    test <@ not (calls.Contains "recheck") @>

[<Fact>]
let ``the log line names every file re-checked and every survivor`` () =
    let clean = logLine (Outcome.Rechecked([ "/r/A.fs"; "/r/B.fs" ], []))

    test
        <@
            clean.Contains "/r/A.fs"
            && clean.Contains "/r/B.fs"
            && clean.Contains "no survivor"
        @>

    let survived = logLine (Outcome.Rechecked([ "/r/A.fs"; "/r/B.fs" ], [ "/r/B.fs" ]))
    test <@ survived.Contains "survivor" && survived.Contains "/r/B.fs" @>

    let refused = logLine (Outcome.Refused [ "test-prune" ])
    test <@ refused.Contains "test-prune" @>
