/// `Locking.locked` — mutual exclusion that leaves no unreachable branch behind.
module FsHotWatch.Tests.LockingTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch

[<Fact>]
let ``returns the work's result and releases the gate`` () =
    let gate = Lock()

    let result = Locking.locked gate (fun () -> gate.IsHeldByCurrentThread, 42)

    test <@ result = (true, 42) @>
    test <@ not gate.IsHeldByCurrentThread @>

[<Fact>]
let ``releases the gate when the work throws`` () =
    let gate = Lock()

    let thrown =
        Assert.Throws<InvalidOperationException>(fun () ->
            Locking.locked gate (fun () -> raise (InvalidOperationException "boom"))
            |> ignore)

    test <@ thrown.Message = "boom" @>
    test <@ not gate.IsHeldByCurrentThread @>

[<Fact(Timeout = 20000)>]
let ``a second holder waits until the first releases`` () =
    let gate = Lock()
    use entered = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)

    let first =
        Task.Run(fun () ->
            Locking.locked gate (fun () ->
                entered.Set()
                release.Wait()))

    entered.Wait()
    let second = Task.Run(fun () -> Locking.locked gate (fun () -> ()))

    test <@ not (second.Wait(TimeSpan.FromMilliseconds 200.0)) @>
    release.Set()
    first.Wait()
    test <@ second.Wait(TimeSpan.FromSeconds 5.0) @>

[<Fact>]
let ``monitored returns the work's result and releases the monitor`` () =
    let gate = obj ()

    let result = Locking.monitored gate (fun () -> Monitor.IsEntered gate, 42)

    test <@ result = (true, 42) @>
    test <@ not (Monitor.IsEntered gate) @>

[<Fact>]
let ``monitored releases the monitor when the work throws`` () =
    let gate = obj ()

    let thrown =
        Assert.Throws<InvalidOperationException>(fun () ->
            Locking.monitored gate (fun () -> raise (InvalidOperationException "boom"))
            |> ignore)

    test <@ thrown.Message = "boom" @>
    test <@ not (Monitor.IsEntered gate) @>

[<Fact(Timeout = 20000)>]
let ``monitored holds the monitor Wait and Pulse need`` () =
    let gate = obj ()
    let mutable ready = false

    let waiter =
        Task.Run(fun () ->
            Locking.monitored gate (fun () ->
                while not ready do
                    Monitor.Wait gate |> ignore

                ready))

    Locking.monitored gate (fun () ->
        ready <- true
        Monitor.Pulse gate)

    test <@ waiter.Wait(TimeSpan.FromSeconds 5.0) && waiter.Result @>
