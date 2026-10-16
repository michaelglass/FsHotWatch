module FsHotWatch.Tests.ScanHeartbeatTests

open System
open System.Collections.Concurrent
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch.Cli

/// Fast enough that a test sees several reports in well under a second.
let private every = TimeSpan.FromMilliseconds 20.0

let private scanning =
    "scanning: 3/10 files (30%), 5s elapsed, cache 0 hit / 3 miss"

/// A wait that blocks until `release` is set, then answers `result`.
let private blockingUntil (release: ManualResetEventSlim) (result: 'a) : unit -> 'a =
    fun () ->
        if not (release.Wait(TimeSpan.FromSeconds 10.0)) then
            failwith "the test never released the wait"

        result

// A cold scan: the wait blocks for minutes, and the reader hears its progress meanwhile.
[<Fact(Timeout = 15000)>]
let ``a scan that outlasts the interval reports its progress until it finishes`` () =
    use release = new ManualResetEventSlim(false)
    let emitted = ConcurrentQueue<string>()

    let emit line =
        emitted.Enqueue line

        if emitted.Count >= 2 then
            release.Set()

    let result =
        ScanHeartbeat.waitReporting every (fun () -> scanning) emit (blockingUntil release "scan done")

    test <@ result = "scan done" @>
    test <@ emitted.Count >= 2 @>
    test <@ emitted |> Seq.forall ((=) scanning) @>

// A warm scan answers from the cache before the first report is due: nothing is said.
[<Fact(Timeout = 15000)>]
let ``a scan that finishes inside the interval reports nothing`` () =
    let reads = ref 0
    let emitted = ConcurrentQueue<string>()

    let result =
        ScanHeartbeat.waitReporting
            (TimeSpan.FromSeconds 10.0)
            (fun () ->
                Interlocked.Increment reads |> ignore
                scanning)
            emitted.Enqueue
            (fun () -> 42)

    test <@ result = 42 @>
    test <@ reads.Value = 0 @>
    test <@ emitted.IsEmpty @>

// The daemon's answer between scans (`idle`, `complete: …`) or a wedge report is not
// scan progress, and must not be printed as if it were.
[<Fact(Timeout = 15000)>]
let ``an answer with no scanning line reports nothing`` () =
    use release = new ManualResetEventSlim(false)
    let reads = ref 0
    let emitted = ConcurrentQueue<string>()

    let read () =
        if Interlocked.Increment reads >= 2 then
            release.Set()

        "complete: 10 files checked in 1.0s"

    ScanHeartbeat.waitReporting every read emitted.Enqueue (blockingUntil release ())

    test <@ reads.Value >= 2 @>
    test <@ emitted.IsEmpty @>

// A progress read is a courtesy: one that fails must neither fail nor end the wait the
// verdict depends on.
[<Fact(Timeout = 15000)>]
let ``a failing progress read leaves the wait running`` () =
    use release = new ManualResetEventSlim(false)
    let reads = ref 0
    let emitted = ConcurrentQueue<string>()

    let read () : string =
        if Interlocked.Increment reads >= 2 then
            release.Set()

        raise (InvalidOperationException "pipe busy")

    let result =
        ScanHeartbeat.waitReporting every read emitted.Enqueue (blockingUntil release "scan done")

    test <@ result = "scan done" @>
    test <@ emitted.IsEmpty @>

[<Fact(Timeout = 15000)>]
let ``the wait's own failure reaches the caller unchanged`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            ScanHeartbeat.waitReporting every (fun () -> scanning) ignore (fun () ->
                raise (InvalidOperationException "daemon shut down"))
            |> ignore)

    test <@ ex.Message = "daemon shut down" @>

[<Fact(Timeout = 15000)>]
let ``check reports a running scan at least every 30 seconds`` () =
    test <@ ScanHeartbeat.interval <= TimeSpan.FromSeconds 30.0 @>

[<Fact(Timeout = 15000)>]
let ``a progress line is indented on its own line when output is not a terminal`` () =
    let written = ConcurrentQueue<string>()
    ScanHeartbeat.emitLineTo false written.Enqueue scanning
    test <@ List.ofSeq written = [ $"  %s{scanning}\n" ] @>

// On a terminal the spinner owns the current line: the progress line clears it first.
[<Fact(Timeout = 15000)>]
let ``a progress line clears the spinner's line first on a terminal`` () =
    let written = ConcurrentQueue<string>()
    ScanHeartbeat.emitLineTo true written.Enqueue scanning
    test <@ List.ofSeq written = [ $"\r\u001b[2K  %s{scanning}\n" ] @>
