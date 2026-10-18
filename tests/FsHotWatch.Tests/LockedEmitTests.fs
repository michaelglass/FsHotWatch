/// The scan emits each checked file while holding the discovery lock, and the scan's other
/// check callbacks wait for that lock, a pool thread each. An emit that needed a pool
/// thread to finish would wait behind those callbacks, and they wait for it: the pool
/// injects threads one at a time and hands every one to another waiting callback.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.LockedEmitTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Xunit
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

/// More callbacks than a starved pool injects threads for in many times `bound`.
let private queuedCallbacks = 200

/// Far longer than an emit that publishes on the caller's thread takes; far shorter than
/// a pool injecting a thread for each queued callback ahead of the emit's publication.
let private bound = TimeSpan.FromSeconds 5.0

let private checkedFile =
    { File = AbsFilePath.create "/tmp/locked-emit/A.fs"
      Source = ""
      ParseResults = Unchecked.defaultof<_>
      CheckResults = ParseOnly
      ProjectOptions = Unchecked.defaultof<_>
      Version = 0L
      ModelGeneration = None
      Frame = None
      Placed = Map.empty }

[<Fact(Timeout = 120000)>]
let ``an emit under the discovery lock finishes while the scan's other callbacks wait for that lock`` () =
    let host = PluginHost.create Unchecked.defaultof<_> "/tmp/locked-emit"
    use releaseFold = new ManualResetEventSlim(false)

    // A subscriber whose fold does not finish until the test ends: admitting the event
    // must not wait for it.
    host.RegisterHandler
        { Name = PluginName.create "slow-fold"
          Init = ()
          Update =
            fun _ state _ ->
                async {
                    releaseFold.Wait(TimeSpan.FromSeconds 60.0) |> ignore
                    return state
                }
          Commands = []
          Subscriptions = Set.ofList [ SubscribeFileChecked ]
          PrepareCommit = None
          CacheKey = None
          Teardown = None }

    let discovery = Lock()
    use holding = new ManualResetEventSlim(false)
    use queued = new ManualResetEventSlim(false)
    let emitted = Stopwatch()

    // The checked file's own callback, on a thread of its own so a hang is observed by
    // `Join` rather than hanging the test.
    let holder =
        Thread(
            (fun () ->
                discovery.Enter()

                try
                    holding.Set()
                    queued.Wait()
                    emitted.Start()
                    host.EmitFileChecked checkedFile
                    emitted.Stop()
                finally
                    discovery.Exit()),
            IsBackground = true,
            Name = "locked-emit holder"
        )

    let mutable callbacks: Task list = []

    try
        let finished =
            withEveryPoolThreadBusy (fun () ->
                holder.Start()
                Assert.True(holding.Wait(TimeSpan.FromSeconds 10.0), "the holder never took the lock")

                // On the pool's global queue, as continuations posted from outside the pool
                // are, and so ahead of anything the emit itself posts there. `Task.Run` from
                // this pool thread would use its local queue, which an injected thread
                // reads only after the global one.
                callbacks <-
                    [ for _ in 1..queuedCallbacks ->
                          Task.Factory.StartNew(
                              (fun () ->
                                  discovery.Enter()
                                  discovery.Exit()),
                              CancellationToken.None,
                              TaskCreationOptions.PreferFairness,
                              TaskScheduler.Default
                          ) ]

                queued.Set()
                holder.Join bound)

        Assert.True(
            finished,
            $"EmitFileChecked under the lock did not return within %O{bound}: it waits for a pool thread \
              that %d{queuedCallbacks} callbacks waiting on the same lock are queued ahead of"
        )

        Assert.True(emitted.Elapsed < bound, $"the emit took %O{emitted.Elapsed}")
    finally
        releaseFold.Set()
        // Unblocked pool: the holder's emit completes, then every callback gets the lock.
        holder.Join(TimeSpan.FromSeconds 60.0) |> ignore
        Task.WaitAll(callbacks |> Array.ofList, TimeSpan.FromSeconds 60.0) |> ignore

/// A change posted asynchronously while the pool has no thread waits for a pool writer.
/// A synchronous change posted after it publishes the queue itself when no writer runs,
/// the posted change first. The pool writer, when it finally runs, finds the writer role
/// taken and leaves the queue to its holder. Order, not liveness: the emit test above is
/// the one a writer that needs the pool fails.
[<Fact(Timeout = 120000)>]
let ``a synchronous change publishes an asynchronous one queued ahead of it first, and the pool writer leaves the queue to it``
    ()
    =
    let store = FsHotWatch.PluginWorkOwner.Store()

    let row =
        store.Register("order", List.empty<string>, fun _ -> FsHotWatch.PluginWorkOwner.RowStatus.ofWork false 0L None)

    use published = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let mutable posted: Task<unit> = null

    // Off the pool, so the pool writer it posts is queued on the global queue, behind the
    // held work.
    let poster =
        Thread(
            (fun () -> posted <- store.ChangeAsync(row, fun _ changes -> "async" :: changes, ())),
            IsBackground = true
        )

    // Holds the writer role inside its own change until the test has seen the pool writer run.
    let writer =
        Thread(
            (fun () ->
                store.Change(
                    row,
                    fun _ changes ->
                        published.Set()
                        release.Wait(TimeSpan.FromSeconds 60.0) |> ignore
                        "sync" :: changes, ()
                )),
            IsBackground = true
        )

    try
        withEveryPoolThreadBusy (fun () ->
            poster.Start()
            Assert.True(poster.Join bound, "posting an asynchronous change waited")
            writer.Start()
            Assert.True(published.Wait bound, "the synchronous change waited for a pool thread")
            // Published, though its caller cannot resume until the pool has a thread.
            Assert.Equal<string list>([ "async" ], store.Read row))

        // The pool writer was queued before this marker; once the marker has run, so has it.
        let marker =
            Task.Factory.StartNew(
                ignore,
                CancellationToken.None,
                TaskCreationOptions.PreferFairness,
                TaskScheduler.Default
            )

        Assert.True(marker.Wait(TimeSpan.FromSeconds 60.0), "the pool never ran the work queued on it")
        // No assertion depends on this sleep: it lets the dequeued pool writer reach its check
        // while the writer role is still held, the branch this test covers.
        // FSHW-WAIT-001 ok: deliberate window for coverage, not synchronisation
        Thread.Sleep 200
    finally
        release.Set()

    Assert.True(writer.Join(TimeSpan.FromSeconds 60.0), "the synchronous change never returned")
    Assert.True(posted.Wait(TimeSpan.FromSeconds 60.0), "the asynchronous caller never resumed")
    Assert.Equal<string list>([ "sync"; "async" ], store.Read row)
    Assert.Equal(0, store.PendingChanges)
