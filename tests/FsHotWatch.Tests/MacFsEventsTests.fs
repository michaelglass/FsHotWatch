module FsHotWatch.Tests.MacFsEventsTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.TestHelpers
open FsHotWatch.MacFsEvents

// ─── Pure function tests (run on all platforms) ────────────────────

module ``classifyEvent pure tests`` =

    [<Fact(Timeout = 15000)>]
    let ``file created is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemCreated
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 15000)>]
    let ``file removed is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemRemoved
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 15000)>]
    let ``file renamed is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemRenamed
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 15000)>]
    let ``file modified is FileChange`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemModified
        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 15000)>]
    let ``file with multiple change flags is FileChange`` () =
        let flags =
            EventFlags.ItemIsFile ||| EventFlags.ItemCreated ||| EventFlags.ItemModified

        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 15000)>]
    let ``all four change types with ItemIsFile are FileChange`` () =
        let flags =
            EventFlags.ItemIsFile
            ||| EventFlags.ItemCreated
            ||| EventFlags.ItemRemoved
            ||| EventFlags.ItemRenamed
            ||| EventFlags.ItemModified

        test <@ classifyEvent flags = EventClassification.FileChange @>

    [<Fact(Timeout = 15000)>]
    let ``MustScanSubDirs alone is CoalescedScan`` () =
        let flags = EventFlags.MustScanSubDirs
        test <@ classifyEvent flags = EventClassification.CoalescedScan @>

    [<Fact(Timeout = 15000)>]
    let ``MustScanSubDirs with other non-file flags is CoalescedScan`` () =
        let flags = EventFlags.MustScanSubDirs ||| 0x00000004u
        test <@ classifyEvent flags = EventClassification.CoalescedScan @>

    [<Fact(Timeout = 15000)>]
    let ``directory created without ItemIsFile is Ignored`` () =
        let flags = EventFlags.ItemCreated
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 15000)>]
    let ``directory modified without ItemIsFile is Ignored`` () =
        let flags = EventFlags.ItemModified
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 15000)>]
    let ``ItemIsFile without change flags is Ignored`` () =
        let flags = EventFlags.ItemIsFile
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 15000)>]
    let ``zero flags is Ignored`` () =
        test <@ classifyEvent 0u = EventClassification.Ignored @>

    [<Fact(Timeout = 15000)>]
    let ``ItemIsFile with unrelated high bits but no change flags is Ignored`` () =
        let flags = EventFlags.ItemIsFile ||| 0x00100000u
        test <@ classifyEvent flags = EventClassification.Ignored @>

    [<Fact(Timeout = 15000)>]
    let ``MustScanSubDirs takes priority when no file change flags present`` () =
        // ItemIsFile without a change flag fails the first branch, so it falls through.
        let flags = EventFlags.MustScanSubDirs ||| EventFlags.ItemIsFile
        test <@ classifyEvent flags = EventClassification.CoalescedScan @>

    [<Fact(Timeout = 15000)>]
    let ``file change takes priority over MustScanSubDirs when both present`` () =
        let flags =
            EventFlags.ItemIsFile ||| EventFlags.ItemCreated ||| EventFlags.MustScanSubDirs

        test <@ classifyEvent flags = EventClassification.FileChange @>

module ``isFileChangeEvent pure tests`` =

    [<Fact(Timeout = 15000)>]
    let ``returns true for file create`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemCreated
        test <@ isFileChangeEvent flags = true @>

    [<Fact(Timeout = 15000)>]
    let ``returns true for file modify`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemModified
        test <@ isFileChangeEvent flags = true @>

    [<Fact(Timeout = 15000)>]
    let ``returns false for directory create`` () =
        let flags = EventFlags.ItemCreated
        test <@ isFileChangeEvent flags = false @>

    [<Fact(Timeout = 15000)>]
    let ``returns false for MustScanSubDirs`` () =
        let flags = EventFlags.MustScanSubDirs
        test <@ isFileChangeEvent flags = false @>

    [<Fact(Timeout = 15000)>]
    let ``returns false for zero`` () = test <@ isFileChangeEvent 0u = false @>

    [<Fact(Timeout = 15000)>]
    let ``returns false for ItemIsFile alone`` () =
        test <@ isFileChangeEvent EventFlags.ItemIsFile = false @>

module ``isMustScanEvent pure tests`` =

    [<Fact(Timeout = 15000)>]
    let ``returns true for MustScanSubDirs`` () =
        test <@ isMustScanEvent EventFlags.MustScanSubDirs = true @>

    [<Fact(Timeout = 15000)>]
    let ``returns true for MustScanSubDirs with extra bits`` () =
        let flags = EventFlags.MustScanSubDirs ||| 0x00000080u
        test <@ isMustScanEvent flags = true @>

    [<Fact(Timeout = 15000)>]
    let ``returns false for file change`` () =
        let flags = EventFlags.ItemIsFile ||| EventFlags.ItemModified
        test <@ isMustScanEvent flags = false @>

    [<Fact(Timeout = 15000)>]
    let ``returns false for zero`` () = test <@ isMustScanEvent 0u = false @>

    [<Fact(Timeout = 15000)>]
    let ``returns false when file change and MustScanSubDirs both present`` () =
        // FileChange takes priority
        let flags =
            EventFlags.ItemIsFile ||| EventFlags.ItemRemoved ||| EventFlags.MustScanSubDirs

        test <@ isMustScanEvent flags = false @>

module ``describeFlags pure tests`` =

    [<Fact(Timeout = 15000)>]
    let ``names a must-scan's trigger`` () =
        test <@ describeFlags 0x00000007u = "0x00000007 [MustScanSubDirs UserDropped KernelDropped]" @>
        test <@ describeFlags 0x00000021u = "0x00000021 [MustScanSubDirs RootChanged]" @>

    [<Fact(Timeout = 15000)>]
    let ``keeps bits it has no name for`` () =
        test <@ describeFlags 0x80000001u = "0x80000001 [MustScanSubDirs 0x80000000]" @>

    [<Fact(Timeout = 15000)>]
    let ``an empty flag set names nothing`` () =
        test <@ describeFlags 0u = "0x00000000 []" @>

// ─── Integration tests (macOS only) ─────────────────────────��─────

// Force sequential execution — FSEvents startup latency (4-10s for cold dirs) causes
// spurious failures when multiple streams compete for fseventsd resources in parallel.
module ``native cleanup lifecycle tests`` =

    let private startedThread (body: unit -> unit) =
        let thread = Thread(ThreadStart(body))
        thread.IsBackground <- true
        thread.Start()
        thread

    [<Fact(Timeout = 15000)>]
    let ``failed start invalidates and joins before release without calling stop`` () =
        let order = ResizeArray<string>()
        let stopLoop = new ManualResetEventSlim(false)
        let thread = startedThread stopLoop.Wait

        let cleanup =
            NativeCleanupCoordinator(
                (fun () -> order.Add("stop")),
                (fun () -> order.Add("invalidate")),
                (fun () ->
                    order.Add("stop-loop")
                    stopLoop.Set()),
                (fun () -> thread),
                (fun () -> order.Add("release"))
            )

        cleanup.Cleanup()

        test <@ not thread.IsAlive @>
        test <@ order |> Seq.toList = [ "invalidate"; "stop-loop"; "release" ] @>

    [<Fact(Timeout = 15000)>]
    let ``concurrent successful cleanup stops and releases exactly once`` () =
        let mutable stops = 0
        let mutable invalidates = 0
        let mutable releases = 0
        let stopLoop = new ManualResetEventSlim(false)
        let thread = startedThread stopLoop.Wait

        let cleanup =
            NativeCleanupCoordinator(
                (fun () -> Interlocked.Increment(&stops) |> ignore),
                (fun () -> Interlocked.Increment(&invalidates) |> ignore),
                stopLoop.Set,
                (fun () -> thread),
                (fun () -> Interlocked.Increment(&releases) |> ignore)
            )

        cleanup.MarkStarted()

        [| for _ in 1..8 -> System.Threading.Tasks.Task.Run(cleanup.Cleanup) |]
        |> System.Threading.Tasks.Task.WhenAll
        |> fun task -> task.GetAwaiter().GetResult()

        test <@ stops = 1 @>
        test <@ invalidates = 1 @>
        test <@ releases = 1 @>

    [<Fact(Timeout = 15000)>]
    let ``cleanup requested on run-loop thread releases only after that loop exits`` () =
        let order = ResizeArray<string>()
        let mutable cleanup = Unchecked.defaultof<NativeCleanupCoordinator>
        let mutable releasedBeforeExit = false

        let thread =
            Thread(
                ThreadStart(fun () ->
                    cleanup.Cleanup()
                    releasedBeforeExit <- order |> Seq.contains "release"
                    cleanup.RunLoopExited())
            )

        cleanup <-
            NativeCleanupCoordinator(
                (fun () -> order.Add("stop")),
                (fun () -> order.Add("invalidate")),
                (fun () -> order.Add("stop-loop")),
                (fun () -> thread),
                (fun () -> order.Add("release"))
            )

        cleanup.MarkStarted()
        thread.Start()
        test <@ thread.Join(2000) @>
        test <@ not releasedBeforeExit @>
        test <@ order |> Seq.toList = [ "stop"; "invalidate"; "stop-loop"; "release" ] @>

type MacFsEventsFixture() = class end

[<CollectionDefinition("MacFsEvents", DisableParallelization = true)>]
type MacFsEventsCollection() =
    interface ICollectionFixture<MacFsEventsFixture>

[<Collection("MacFsEvents")>]
type MacFsEventsTests() =
    let isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)

    [<Fact(Timeout = 15000)>]
    member _.``FsEventStream constructor rejects empty directory list``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            // Constructor guard only — no FSEvents callbacks, so it stabilises
            // module coverage without fseventsd timing flake.
            let ex =
                Assert.Throws<System.ArgumentException>(fun () ->
                    use _stream = FsHotWatch.MacFsEvents.create [] ignore 0.05
                    ())

            test <@ ex.Message.Contains("At least one directory") @>

    [<Fact(Timeout = SyncHangBackstopMs)>]
    member _.``FsEventStream rejects more exclusion paths than FSEvents accepts``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents-excl" (fun tmpDir ->
                let tooMany =
                    [ for i in 0 .. FsHotWatch.MacFsEvents.MaxExclusionPaths -> Path.Combine(tmpDir, $"x%d{i}") ]

                let ex =
                    Assert.Throws<System.ArgumentException>(fun () ->
                        use _stream =
                            FsHotWatch.MacFsEvents.createExcluding [ tmpDir ] tooMany ignore ignore 0.05

                        ())

                test <@ ex.Message.Contains("at most") @>)

    [<WallClock("real host: a real timer, process, signal, clock or filesystem event is the subject, so it runs on real time; it asserts outcomes, not durations")>]
    [<Fact(Timeout = 150000)>]
    member _.``FsEventStream never delivers an event under an excluded path``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents-excl" (fun tmpDir ->
                let kept = Path.Combine(tmpDir, "kept")
                let excluded = Path.Combine(tmpDir, "excluded")
                Directory.CreateDirectory kept |> ignore
                Directory.CreateDirectory excluded |> ignore
                let seen = System.Collections.Concurrent.ConcurrentQueue<string>()

                use _stream =
                    FsHotWatch.MacFsEvents.createExcluding [ tmpDir ] [ excluded ] seen.Enqueue ignore 0.05

                // Write to both on every probe; the kept write arriving proves the
                // stream is live, and by then the excluded writes were dispatched too.
                probeLoop
                    (fun n ->
                        File.WriteAllText(Path.Combine(excluded, $"E%d{n}.fs"), "e")
                        File.WriteAllText(Path.Combine(kept, $"K%d{n}.fs"), "k"))
                    (fun () -> seen |> Seq.exists (fun p -> p.Contains("/kept/")))
                    60000

                Thread.Sleep 500
                test <@ seen |> Seq.exists (fun p -> p.Contains("/kept/")) @>
                test <@ seen |> Seq.forall (fun p -> not (p.Contains("/excluded/"))) @>)

    [<Fact(Timeout = SyncHangBackstopMs)>]
    member _.``FsEventStream create and dispose without crash``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents" (fun tmpDir ->
                use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] ignore 0.05
                test <@ _stream.IsRunning @>)

    [<Fact(Timeout = 150000)>]
    member _.``FsEventStream detects file creation``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents" (fun tmpDir ->
                let mutable detectedPaths: string list = []
                let lockObj = obj ()

                let onFile path =
                    lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

                use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile 0.05

                // Probe until the stream is live: FSEvents cold-start is an unbounded window for a new dir.
                probeUntilEvent tmpDir (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 60000

                // Probe-write until the event fires: delivery can lag
                // for an unbounded window after a large cold-start batch.
                let testFile = Path.Combine(tmpDir, "Test.fs")

                probeLoop
                    (fun _ -> File.WriteAllText(testFile, "module Test\nlet x = 1\n"))
                    (fun () -> lock lockObj (fun () -> detectedPaths |> List.exists (fun p -> p.EndsWith("Test.fs"))))
                    60000

                let paths = lock lockObj (fun () -> detectedPaths)
                test <@ paths |> List.exists (fun p -> p.EndsWith("Test.fs")) @>)

    [<WallClock("real host: a real timer, process, signal, clock or filesystem event is the subject, so it runs on real time; it asserts outcomes, not durations")>]
    [<Fact(Timeout = 150000)>]
    member _.``FsEventStream detects file modification``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents" (fun tmpDir ->
                let testFile = Path.Combine(tmpDir, "Lib.fs")
                File.WriteAllText(testFile, "module Lib\nlet x = 1\n")

                let mutable detectedCount = 0
                let lockObj = obj ()

                let onFile _path =
                    lock lockObj (fun () -> detectedCount <- detectedCount + 1)

                use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile 0.05

                probeUntilEvent tmpDir (fun () -> lock lockObj (fun () -> detectedCount > 0)) 60000

                // Let in-flight probe events drain before capturing baseline.
                Thread.Sleep(200)
                let countAfterProbe = lock lockObj (fun () -> detectedCount)

                probeLoop
                    (fun n -> File.WriteAllText(testFile, $"module Lib\nlet x = {n + 2}\n"))
                    (fun () -> lock lockObj (fun () -> detectedCount > countAfterProbe))
                    60000

                test <@ lock lockObj (fun () -> detectedCount > countAfterProbe) @>)

    [<Fact(Timeout = 150000)>]
    member _.``FsEventStream detects file deletion``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents" (fun tmpDir ->
                let testFile = Path.Combine(tmpDir, "Del.fs")
                File.WriteAllText(testFile, "module Del")

                let mutable detectedPaths: string list = []
                let lockObj = obj ()

                let onFile path =
                    lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

                use _stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile 0.05

                probeUntilEvent tmpDir (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 60000

                // Each iteration deletes (and if needed re-creates) Del.fs, because
                // Delivery of the delete event can lag.
                probeLoop
                    (fun _ ->
                        if not (File.Exists testFile) then
                            File.WriteAllText(testFile, "module Del")

                        File.Delete(testFile))
                    (fun () -> lock lockObj (fun () -> detectedPaths |> List.exists (fun p -> p.EndsWith("Del.fs"))))
                    60000

                let paths = lock lockObj (fun () -> detectedPaths)
                test <@ paths |> List.exists (fun p -> p.EndsWith("Del.fs")) @>)

    [<Fact(Timeout = 150000)>]
    member _.``FsEventStream watches multiple directories``() =
        if not isMacOS then
            Assert.Skip("macOS only")
        else
            let tmpRoot =
                Path.Combine(Path.GetTempPath(), $"fshw-fsevents-multi-{Guid.NewGuid():N}")

            let srcDir = Path.Combine(tmpRoot, "src")
            let testDir = Path.Combine(tmpRoot, "tests")
            Directory.CreateDirectory(srcDir) |> ignore
            Directory.CreateDirectory(testDir) |> ignore

            let mutable detectedPaths: string list = []
            let lockObj = obj ()

            let onFile path =
                lock lockObj (fun () -> detectedPaths <- path :: detectedPaths)

            try
                use _stream = FsHotWatch.MacFsEvents.create [ srcDir; testDir ] onFile 0.05

                probeUntilEvent srcDir (fun () -> lock lockObj (fun () -> detectedPaths.Length > 0)) 60000

                probeLoop
                    (fun n ->
                        File.WriteAllText(Path.Combine(srcDir, "A.fs"), $"module A // v{n}")
                        File.WriteAllText(Path.Combine(testDir, "B.fs"), $"module B // v{n}"))
                    (fun () ->
                        lock lockObj (fun () ->
                            detectedPaths |> List.exists (fun p -> p.EndsWith("A.fs"))
                            && detectedPaths |> List.exists (fun p -> p.EndsWith("B.fs"))))
                    60000

                let paths = lock lockObj (fun () -> detectedPaths)
                test <@ paths |> List.exists (fun p -> p.EndsWith("A.fs")) @>
                test <@ paths |> List.exists (fun p -> p.EndsWith("B.fs")) @>
            finally
                Directory.Delete(tmpRoot, true)

// ─── Event drain (AUTOMATION-1543) ──────────────────────────────────
//
// fseventsd keeps every event it has queued for a stream until that stream's callback
// returns. The callback used to run the daemon's handler itself: hash the file, then
// wait up to five seconds for the daemon's store to admit the change, which is exactly
// when FCS is busy. The drain takes events off the callback in O(1) and runs the handler
// on its own thread, so a busy daemon holds its backlog in-process, deduplicated by
// path, instead of in fseventsd.

module ``event drain tests`` =
    let private file (path: string) = DrainItem.File path

    [<Fact(Timeout = SyncHangBackstopMs)>]
    let ``posting returns while the handler is blocked`` () =
        use gate = new ManualResetEventSlim(false)
        let handled = System.Collections.Concurrent.ConcurrentQueue<DrainItem>()

        use drain =
            new EventDrain(
                (fun item ->
                    gate.Wait()
                    handled.Enqueue item),
                "test-drain"
            )

        // Every post completes with the handler stuck on the first item.
        for i in 0..9_999 do
            drain.Post(file $"/r/f%d{i}.fs")

        test <@ (drain.Stats()).Received = 10_000L @>
        test <@ handled.IsEmpty @>

        gate.Set()
        waitUntilOr "all 10,000 items delivered" (fun () -> (drain.Stats()).Delivered = 10_000L) 30_000
        test <@ handled.Count = 10_000 @>

    [<Fact(Timeout = SyncHangBackstopMs)>]
    let ``a path posted again while it is still pending is delivered once`` () =
        use gate = new ManualResetEventSlim(false)
        let handled = System.Collections.Concurrent.ConcurrentQueue<DrainItem>()

        use drain =
            new EventDrain(
                (fun item ->
                    gate.Wait()
                    handled.Enqueue item),
                "test-drain"
            )

        // The first post is taken by the dispatch thread and blocks there; wait for that
        // so the rest are all pending together.
        drain.Post(file "/r/first.fs")
        waitUntilOr "the first item is being handled" (fun () -> (drain.Stats()).Pending = 0) 10_000

        for _ in 1..100 do
            drain.Post(file "/r/A.fs")
            drain.Post(file "/r/B.fs")

        test <@ (drain.Stats()).Pending = 2 @>
        test <@ (drain.Stats()).Coalesced = 198L @>

        gate.Set()
        waitUntilOr "three deliveries" (fun () -> handled.Count = 3) 10_000

        test <@ List.ofSeq handled = [ file "/r/first.fs"; file "/r/A.fs"; file "/r/B.fs" ] @>

    [<Fact(Timeout = SyncHangBackstopMs)>]
    let ``a path posted again after it was taken is delivered again`` () =
        // The handler reads the file when it runs, so a change after it started must
        // reach it a second time, not be folded into the delivery already under way.
        use started = new ManualResetEventSlim(false)
        use gate = new ManualResetEventSlim(false)
        let handled = System.Collections.Concurrent.ConcurrentQueue<DrainItem>()

        use drain =
            new EventDrain(
                (fun item ->
                    started.Set()
                    gate.Wait()
                    handled.Enqueue item),
                "test-drain"
            )

        drain.Post(file "/r/A.fs")
        test <@ started.Wait(HangWait) @>
        drain.Post(file "/r/A.fs")
        gate.Set()

        waitUntilOr "two deliveries" (fun () -> handled.Count = 2) 10_000
        test <@ List.ofSeq handled = [ file "/r/A.fs"; file "/r/A.fs" ] @>

    [<Fact(Timeout = SyncHangBackstopMs)>]
    let ``a rescan and a file of the same path are distinct items`` () =
        use gate = new ManualResetEventSlim(false)
        let handled = System.Collections.Concurrent.ConcurrentQueue<DrainItem>()

        use drain =
            new EventDrain(
                (fun item ->
                    gate.Wait()
                    handled.Enqueue item),
                "test-drain"
            )

        drain.Post(file "/r/first.fs")
        waitUntilOr "the first item is being handled" (fun () -> (drain.Stats()).Pending = 0) 10_000
        drain.Post(DrainItem.Rescan "/r/src")
        drain.Post(file "/r/src")
        gate.Set()

        waitUntilOr "three deliveries" (fun () -> handled.Count = 3) 10_000
        test <@ List.ofSeq handled = [ file "/r/first.fs"; DrainItem.Rescan "/r/src"; file "/r/src" ] @>

    [<Fact(Timeout = SyncHangBackstopMs)>]
    let ``a handler that throws does not stop delivery`` () =
        let handled = System.Collections.Concurrent.ConcurrentQueue<DrainItem>()

        use drain =
            new EventDrain(
                (fun item ->
                    if item = file "/r/bad.fs" then
                        invalidOp "handler failed"

                    handled.Enqueue item),
                "test-drain"
            )

        drain.Post(file "/r/bad.fs")
        drain.Post(file "/r/good.fs")

        waitUntilOr "the item after the failure" (fun () -> handled.Count = 1) 10_000
        test <@ List.ofSeq handled = [ file "/r/good.fs" ] @>

    [<Fact(Timeout = SyncHangBackstopMs)>]
    let ``nothing is delivered after dispose`` () =
        use gate = new ManualResetEventSlim(false)
        let handled = System.Collections.Concurrent.ConcurrentQueue<DrainItem>()

        let drain =
            new EventDrain(
                (fun item ->
                    gate.Wait()
                    handled.Enqueue item),
                "test-drain"
            )

        drain.Post(file "/r/first.fs")
        waitUntilOr "the first item is being handled" (fun () -> (drain.Stats()).Pending = 0) 10_000
        drain.Post(file "/r/pending.fs")

        // Dispose waits for the item under way, so release it from another thread.
        let release =
            System.Threading.Tasks.Task.Run(fun () ->
                waitUntilOr "dispose to begin" (fun () -> drain.Closing) 10_000
                gate.Set())

        (drain :> IDisposable).Dispose()
        release.Wait()
        drain.Post(file "/r/late.fs")

        test <@ List.ofSeq handled = [ file "/r/first.fs" ] @>

    [<Fact(Timeout = SyncHangBackstopMs)>]
    let ``disposing from inside the handler does not deadlock`` () =
        let mutable drainRef: EventDrain option = None
        use disposed = new ManualResetEventSlim(false)

        let drain =
            new EventDrain(
                (fun _ ->
                    drainRef |> Option.iter (fun d -> (d :> IDisposable).Dispose())
                    disposed.Set()),
                "test-drain"
            )

        drainRef <- Some drain
        drain.Post(file "/r/A.fs")
        test <@ disposed.Wait(HangWait) @>

/// The drain on a real stream: with the handler blocked, the native callback still takes
/// every event fseventsd delivers, so its queue for this client stays drained.
[<Collection("MacFsEvents")>]
type MacFsEventsDrainTests() =
    [<Fact(Timeout = 150000)>]
    member _.``a native stream keeps taking events while its handler is blocked``() =
        if not (RuntimeInformation.IsOSPlatform OSPlatform.OSX) then
            Assert.Skip("macOS only")
        else
            withTempDir "fsevents-drain" (fun tmpDir ->
                use gate = new ManualResetEventSlim(false)
                let handled = System.Collections.Concurrent.ConcurrentQueue<string>()

                let onFile path =
                    gate.Wait()
                    handled.Enqueue path

                use stream = FsHotWatch.MacFsEvents.create [ tmpDir ] onFile 0.05

                // Writes 200 new files per probe; the stream must take all of one probe's
                // files from fseventsd while the handler is still stuck on its first.
                let mutable written = 0

                probeLoop
                    (fun n ->
                        for i in 0..199 do
                            File.WriteAllText(Path.Combine(tmpDir, $"p%d{n}-%d{i}.fs"), "x")

                        written <- written + 200)
                    (fun () -> (stream.DrainStats()).Received >= 200L)
                    60000

                let taken = (stream.DrainStats()).Received
                test <@ taken >= 200L @>
                // Nothing reached the handler past the one it is blocked on.
                test <@ handled.IsEmpty @>

                gate.Set()
                waitUntilOr "the handler catches up" (fun () -> handled.Count >= 200) 60000
                test <@ handled.Count >= 200 @>)
