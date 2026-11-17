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
