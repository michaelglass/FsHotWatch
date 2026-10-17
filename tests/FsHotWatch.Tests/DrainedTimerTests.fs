/// `DrainedTimer` — once its disposal returns, no tick is running and none starts.
module FsHotWatch.Tests.DrainedTimerTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch

[<Fact(Timeout = 15000)>]
let ``a tick that starts after the ticks stopped does nothing`` () =
    // A tick the timer queued before its disposal can start after it.
    let ran = ref 0

    let ticks =
        DrainedTimer.Ticks(fun () -> Interlocked.Increment &ran.contents |> ignore)

    ticks.Run()
    ticks.Stop()
    ticks.Run()
    test <@ ran.Value = 1 @>

[<Fact(Timeout = 15000)>]
let ``stopping waits for the tick running now`` () =
    use entered = new ManualResetEventSlim()
    use release = new ManualResetEventSlim()
    let finished = ref false

    let ticks =
        DrainedTimer.Ticks(fun () ->
            entered.Set()
            release.Wait(TimeSpan.FromSeconds 10.0) |> ignore
            Volatile.Write(&finished.contents, true))

    let running = Task.Run(fun () -> ticks.Run())
    test <@ entered.Wait(TimeSpan.FromSeconds 10.0) @>

    let stopping = Task.Run(fun () -> ticks.Stop())
    test <@ not (stopping.Wait(TimeSpan.FromMilliseconds 200.0)) @>

    release.Set()
    test <@ stopping.Wait(TimeSpan.FromSeconds 10.0) @>
    // Stop returned only after the tick had finished.
    let tickFinished = Volatile.Read &finished.contents
    test <@ tickFinished @>
    test <@ running.Wait(TimeSpan.FromSeconds 10.0) @>

[<Fact(Timeout = 15000)>]
let ``a tick that stops its own timer does not wait for itself`` () =
    let ticks = ref Unchecked.defaultof<DrainedTimer.Ticks>
    let ran = ref 0

    ticks.Value <-
        DrainedTimer.Ticks(fun () ->
            Interlocked.Increment &ran.contents |> ignore
            ticks.Value.Stop())

    let first = Task.Run(fun () -> ticks.Value.Run())
    test <@ first.Wait(TimeSpan.FromSeconds 10.0) @>
    ticks.Value.Run()
    test <@ ran.Value = 1 @>

[<Fact(Timeout = 15000)>]
let ``a timer ticks until it is disposed, and not after`` () =
    let ran = ref 0

    let timer =
        DrainedTimer.every (TimeSpan.FromMilliseconds 10.0) (fun () -> Interlocked.Increment &ran.contents |> ignore)

    let deadline = DateTime.UtcNow.AddSeconds 10.0

    while Volatile.Read &ran.contents < 2 && DateTime.UtcNow < deadline do
        Thread.Sleep 10

    timer.Dispose()
    let atDisposal = Volatile.Read &ran.contents
    test <@ atDisposal >= 2 @>
    Thread.Sleep 100
    let afterDisposal = Volatile.Read &ran.contents
    test <@ afterDisposal = atDisposal @>
