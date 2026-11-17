module FsHotWatch.Tests.AsyncRuntimeTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the pool's wait answers a settled task without waiting`` () =
    test <@ AsyncRuntime.pool.Wait TimeSpan.Zero Task.CompletedTask @>
    test <@ AsyncRuntime.pool.Wait TimeSpan.Zero (Task.FromException(InvalidOperationException "settled")) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the pool's wait gives up on an unsettled task once its bound passes`` () =
    let never = TaskCompletionSource<unit>()
    test <@ not (AsyncRuntime.pool.Wait TimeSpan.Zero never.Task) @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the pool runs dispatched work, and its wait sees what that work settles`` () =
    let settled =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let ran = TaskCompletionSource<int>()

    AsyncRuntime.pool.Dispatch(fun () ->
        ran.SetResult Environment.CurrentManagedThreadId
        settled.SetResult())

    test <@ AsyncRuntime.pool.Wait (TimeSpan.FromMilliseconds(float SyncHangBackstopMs)) settled.Task @>
    test <@ ran.Task.IsCompletedSuccessfully @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the pool's timer fires its callback, and the pool's scheduler runs started tasks`` () =
    let fired =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    use _ =
        AsyncRuntime.pool.Schedule TimeSpan.Zero (fun () -> fired.TrySetResult() |> ignore)

    test <@ AsyncRuntime.pool.Wait (TimeSpan.FromMilliseconds(float SyncHangBackstopMs)) fired.Task @>

    let started =
        Task.Factory.StartNew(
            (fun () -> 42),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            AsyncRuntime.pool.Scheduler
        )

    test <@ started.GetAwaiter().GetResult() = 42 @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the pool cancels a source, and runs an async computation to completion`` () =
    use source = new CancellationTokenSource()
    let cancelling = AsyncRuntime.pool.Cancel source
    test <@ source.IsCancellationRequested @>
    test <@ AsyncRuntime.pool.Wait (TimeSpan.FromMilliseconds(float SyncHangBackstopMs)) cancelling @>

    let mutable ran = false
    AsyncRuntime.pool.RunSynchronously(async { ran <- true })
    test <@ ran @>
