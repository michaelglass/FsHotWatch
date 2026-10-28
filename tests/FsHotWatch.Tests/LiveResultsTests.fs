/// How many check results a daemon's plugins hold at once (AUTOMATION-683, ADR-054). A
/// producer takes a place before it publishes a result, and the place is given back when
/// every subscriber has committed the result, failed it, or never subscribed; or when the
/// producer publishes nothing. Past the bound, producers wait. Every wait here is on a
/// completion the test controls: nothing sleeps.
module FsHotWatch.Tests.LiveResultsTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.LiveResults
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

/// Take a place on `bound` and hand it over to `committed`: the place is held until
/// `committed` completes. The hold has queued, or been granted, before this returns.
let private holdUntil (bound: Bound) (committed: Task) (ct: CancellationToken) : Task =
    Async.StartImmediateAsTask(bound.Hold(fun place -> place.ReleaseWhen [ committed ]), ct) :> Task

let private pending () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let private within = TimeSpan.FromSeconds 10.0

[<Fact(Timeout = 10000)>]
let ``a place under the bound is granted at once, and given back when the producer publishes nothing`` () =
    let bound = Bound 2

    bound.Hold ignore |> Async.RunSynchronously

    let status = bound.Status()
    test <@ status.Bound = 2 @>
    test <@ status.Live = 0 && status.Queued = 0 @>
    test <@ status.Admitted = 1L && status.Peak = 1 @>

[<Fact(Timeout = 10000)>]
let ``past the bound a producer waits, and each place given back admits exactly one`` () =
    let bound = Bound 2
    let commits = [ for _ in 1..3 -> pending () ]

    let holds =
        commits
        |> List.map (fun commit -> holdUntil bound commit.Task CancellationToken.None)

    test <@ holds[0].IsCompletedSuccessfully && holds[1].IsCompletedSuccessfully @>
    test <@ not holds[2].IsCompleted @>
    test <@ bound.Status().Live = 2 && bound.Status().Queued = 1 @>

    commits[0].SetResult()
    test <@ holds[2].Wait within @>

    let afterOne = bound.Status()
    test <@ afterOne.Live = 2 && afterOne.Queued = 0 @>
    test <@ afterOne.Peak = 2 && afterOne.Admitted = 3L @>

    commits[1].SetResult()
    commits[2].SetResult()
    waitUntil (fun () -> bound.Status().Live = 0) 10000

[<Fact(Timeout = 10000)>]
let ``a producer that gives up while waiting never takes a place`` () =
    let bound = Bound 1
    let first = pending ()
    holdUntil bound first.Task CancellationToken.None |> ignore
    use giveUp = new CancellationTokenSource()
    let second = holdUntil bound (Task.CompletedTask) giveUp.Token
    test <@ bound.Status().Queued = 1 @>

    giveUp.Cancel()
    Assert.ThrowsAny<Exception>(fun () -> second.Wait within |> ignore) |> ignore
    test <@ second.IsCanceled @>
    test <@ bound.Status().Queued = 0 && bound.Status().Abandoned = 1L @>

    first.SetResult()
    waitUntil (fun () -> bound.Status().Live = 0) 10000
    // The place the give-up never took is free: the next producer is granted at once.
    test <@ (holdUntil bound Task.CompletedTask CancellationToken.None).IsCompletedSuccessfully @>

[<Fact(Timeout = 10000)>]
let ``a publication that throws gives its place back`` () =
    let bound = Bound 1

    let failed =
        Assert.ThrowsAny<Exception>(fun () ->
            bound.Hold(fun _ -> failwith "publication failed") |> Async.RunSynchronously)

    test <@ failed.Message.Contains "publication failed" @>
    test <@ bound.Status().Live = 0 @>
    test <@ (holdUntil bound Task.CompletedTask CancellationToken.None).IsCompletedSuccessfully @>

[<Fact(Timeout = 10000)>]
let ``a place handed over to a commit that fails is given back`` () =
    let bound = Bound 1
    let commit = pending ()
    holdUntil bound commit.Task CancellationToken.None |> ignore
    test <@ bound.Status().Live = 1 @>

    commit.SetException(InvalidOperationException "the plugin failed the result")
    waitUntil (fun () -> bound.Status().Live = 0) 10000

[<Fact(Timeout = 10000)>]
let ``a place handed over to no commits is given back at once`` () =
    let bound = Bound 1
    bound.Hold(fun place -> place.ReleaseWhen []) |> Async.RunSynchronously
    test <@ bound.Status().Live = 0 @>

[<Fact(Timeout = 10000)>]
let ``giving a place back twice gives it back once`` () =
    let bound = Bound 2
    let commit = pending ()
    holdUntil bound commit.Task CancellationToken.None |> ignore

    bound.Hold(fun place ->
        place.Release()
        place.Release())
    |> Async.RunSynchronously

    // The other producer's place is still held: only one place was given back.
    test <@ bound.Status().Live = 1 @>
    commit.SetResult()
    waitUntil (fun () -> bound.Status().Live = 0) 10000

[<Theory>]
[<InlineData(null, 0)>]
[<InlineData("", 0)>]
[<InlineData("  ", 0)>]
[<InlineData("4", 4)>]
[<InlineData(" 12 ", 12)>]
let ``FSHW_LIVE_RESULTS holds a positive integer or nothing`` (raw: string, expected: int) =
    let parsed = parseOverride raw
    let wanted = if expected = 0 then None else Some expected
    test <@ parsed = Ok wanted @>

[<Theory>]
[<InlineData("0")>]
[<InlineData("-3")>]
[<InlineData("many")>]
[<InlineData("2.5")>]
let ``anything else in FSHW_LIVE_RESULTS is refused, naming the value`` (raw: string) =
    match parseOverride raw with
    | Error message -> test <@ message.Contains OverrideEnvVar && message.Contains raw @>
    | Ok parsed -> failwith $"%s{raw} was accepted as %A{parsed}"

[<Fact>]
let ``a daemon's bound is the override when it is one, else the default with a warning`` () =
    let env (value: string) : string -> string =
        fun name -> if name = OverrideEnvVar then value else null

    let three = resolveBound (env "3")
    let unset = resolveBound (env null)
    test <@ three = (3, None) @>
    test <@ unset = (DefaultBound, None) @>

    match resolveBound (env "none") with
    | bound, Some warning ->
        test <@ bound = DefaultBound @>
        test <@ warning.Contains OverrideEnvVar @>
    | _, None -> failwith "an invalid override gave no warning"

[<Fact>]
let ``the default bound is twice the processor count`` () =
    test <@ DefaultBound = 2 * Environment.ProcessorCount @>

[<Fact(Timeout = 10000)>]
let ``status --repository carries the live results, times in whole ms`` () =
    let payload =
        LiveResultsStatus.payload
            { Bound = 8
              Live = 3
              Peak = 8
              Queued = 2
              Admitted = 40L
              Abandoned = 1L
              Waited = TimeSpan.FromMilliseconds 1500.7
              LongestWait = TimeSpan.FromMilliseconds 250.2 }

    test <@ payload["bound"].GetValue<int>() = 8 @>
    test <@ payload["live"].GetValue<int>() = 3 @>
    test <@ payload["peak"].GetValue<int>() = 8 @>
    test <@ payload["queued"].GetValue<int>() = 2 @>
    test <@ payload["admitted"].GetValue<int64>() = 40L @>
    test <@ payload["abandoned"].GetValue<int64>() = 1L @>
    test <@ payload["waitedMs"].GetValue<int64>() = 1500L @>
    test <@ payload["longestWaitMs"].GetValue<int64>() = 250L @>

// --- the host: a result's place is held until its subscribers commit it ---

let private consumer (name: string) (onChecked: unit -> Async<unit>) : PluginHandler<unit, unit> =
    { Name = PluginName.create name
      Init = ()
      Update =
        fun _ state event ->
            async {
                match event with
                | FileChecked _ -> do! onChecked ()
                | _ -> ()

                return state
            }
      Commands = []
      Subscriptions = Set.ofList [ SubscribeFileChecked ]
      CacheKey = None
      PrepareCommit = None
      Teardown = None }

let private publishOn (host: PluginHost) (file: string) : Task =
    host.LiveResults.Hold(fun place -> host.EmitFileChecked(fakeFileCheckResult file, place))
    |> Async.StartImmediateAsTask
    :> Task

[<Fact(Timeout = 20000)>]
let ``a result holds its place until every subscriber has committed it`` () =
    let host = PluginHost(nullChecker, "/tmp/live-results", liveResultBound = 1)
    let gate = pending ()
    host.RegisterHandler(consumer "fast" async.Zero)
    host.RegisterHandler(consumer "slow" (fun () -> Async.AwaitTask gate.Task))

    let first = publishOn host "/tmp/live-results/A.fs"
    test <@ first.IsCompletedSuccessfully @>
    let second = publishOn host "/tmp/live-results/B.fs"
    // The slow subscriber has not committed A, so B waits for A's place.
    test <@ not second.IsCompleted @>
    test <@ host.LiveResults.Status().Live = 1 && host.LiveResults.Status().Queued = 1 @>

    gate.SetResult()
    test <@ second.Wait within @>
    waitUntil (fun () -> host.LiveResults.Status().Live = 0) 15000
    test <@ host.LiveResults.Status().Peak = 1 @>

[<Fact(Timeout = 20000)>]
let ``with no subscriber a result's place is given back at once`` () =
    let host = PluginHost(nullChecker, "/tmp/live-results", liveResultBound = 1)
    let published = publishOn host "/tmp/live-results/A.fs"
    test <@ published.IsCompletedSuccessfully @>
    test <@ host.LiveResults.Status().Live = 0 @>

[<Fact(Timeout = 20000)>]
let ``a subscriber that fails a result gives its place back`` () =
    let host = PluginHost(nullChecker, "/tmp/live-results", liveResultBound = 1)
    host.RegisterHandler(consumer "failing" (fun () -> failwith "this plugin fails every result"))

    let published = publishOn host "/tmp/live-results/A.fs"
    test <@ published.IsCompletedSuccessfully @>
    waitUntil (fun () -> host.LiveResults.Status().Live = 0) 15000

[<Fact>]
let ``a bound of less than one place is one place`` () =
    let bound = Bound 0
    test <@ bound.Capacity = 1 && bound.Status().Bound = 1 @>
