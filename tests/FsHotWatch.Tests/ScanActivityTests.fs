module FsHotWatch.Tests.ScanActivityTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ScanActivity

// AUTOMATION-609. The lease is the daemon's only honest answer to "is a scan
// running?", and every property below is one the idle-exit scheduler leans on:
// it must be true for the whole span, false again afterwards, and — the part
// that turns a fix into a hang if it is wrong — false again after a FAILURE.

[<Fact>]
let ``a fresh lease set has nothing in flight`` () =
    let leases = ScanLeases.create ()

    test <@ List.isEmpty (ScanLeases.inFlight leases) @>
    test <@ ScanLeases.anyInFlight leases = false @>

[<Fact>]
let ``an acquired lease reports its kind in flight`` () =
    let leases = ScanLeases.create ()
    use _cold = ScanLeases.acquire leases ScanKind.Cold

    test <@ ScanLeases.inFlight leases = [ ScanKind.Cold ] @>
    test <@ ScanLeases.anyInFlight leases @>

[<Fact>]
let ``disposing a lease clears it`` () =
    let leases = ScanLeases.create ()
    let lease = ScanLeases.acquire leases ScanKind.Forced

    test <@ ScanLeases.inFlight leases = [ ScanKind.Forced ] @>

    lease.Dispose()

    test <@ List.isEmpty (ScanLeases.inFlight leases) @>

[<Fact>]
let ``disposing twice releases once`` () =
    // A `use` inside a body that also disposes explicitly must not drive the
    // count negative — a negative count would read as "no scan" while one runs.
    let leases = ScanLeases.create ()
    let outer = ScanLeases.acquire leases ScanKind.Cold
    let inner = ScanLeases.acquire leases ScanKind.Cold

    inner.Dispose()
    inner.Dispose()

    test <@ ScanLeases.inFlight leases = [ ScanKind.Cold ] @>

    outer.Dispose()

    test <@ List.isEmpty (ScanLeases.inFlight leases) @>

[<Fact>]
let ``concurrent leases are all released before the set reads idle`` () =
    let leases = ScanLeases.create ()

    let held = [| for _ in 1..64 -> ScanLeases.acquire leases ScanKind.Cold |]

    test <@ ScanLeases.anyInFlight leases @>

    Parallel.For(0, held.Length, fun i -> held[i].Dispose()) |> ignore

    test <@ List.isEmpty (ScanLeases.inFlight leases) @>

[<Fact>]
let ``withLease holds for the span of the work and releases after`` () =
    let leases = ScanLeases.create ()
    let observedDuringWork = ref []

    let work =
        async {
            observedDuringWork.Value <- ScanLeases.inFlight leases
            return 42
        }

    let result = withLease leases ScanKind.Cold work |> Async.RunSynchronously

    test <@ result = 42 @>
    test <@ observedDuringWork.Value = [ ScanKind.Cold ] @>
    test <@ List.isEmpty (ScanLeases.inFlight leases) @>

[<Fact>]
let ``withLease releases when the work throws`` () =
    // Acceptance criterion: "exceptional exits release the active-work lease so
    // a failed scan cannot keep the daemon alive forever." A scan that dies in
    // discovery must not pin an idle-exit-eligible daemon up indefinitely.
    let leases = ScanLeases.create ()

    let work = async { return failwith "scan blew up" }

    raises<exn> <@ withLease leases ScanKind.Cold work |> Async.RunSynchronously @>

    test <@ List.isEmpty (ScanLeases.inFlight leases) @>

[<Fact>]
let ``withLease releases when the work is cancelled`` () =
    // The same criterion for the OTHER terminal path: `fshw stop` cancels the
    // daemon lifetime mid-scan, and the lease must not survive it.
    let leases = ScanLeases.create ()
    use cts = new CancellationTokenSource()

    let work =
        async {
            cts.Cancel()
            do! Async.Sleep 5_000
            return ()
        }

    let run () =
        Async.RunSynchronously(withLease leases ScanKind.Cold work, cancellationToken = cts.Token)

    raises<OperationCanceledException> <@ run () @>

    test <@ List.isEmpty (ScanLeases.inFlight leases) @>

[<Fact>]
let ``describe names each kind`` () =
    test <@ ScanKind.describe ScanKind.Cold = "cold" @>
    test <@ ScanKind.describe ScanKind.Forced = "forced" @>
