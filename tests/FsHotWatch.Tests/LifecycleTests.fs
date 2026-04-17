module FsHotWatch.Tests.LifecycleTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Lifecycle

[<Fact(Timeout = 30000)>]
let ``Lifecycle transitions from Idle to Running`` () =
    let idle = Lifecycle.create "initial"
    let running = idle |> Lifecycle.start
    test <@ running |> Lifecycle.value = "initial" @>

[<Fact(Timeout = 30000)>]
let ``Lifecycle transitions from Running to Idle`` () =
    let idle = Lifecycle.create "v1"
    let running = idle |> Lifecycle.start
    let idle2 = running |> Lifecycle.complete "v2"
    test <@ idle2 |> Lifecycle.value = "v2" @>

[<Fact(Timeout = 30000)>]
let ``Lifecycle value is accessible in any phase`` () =
    let idle = Lifecycle.create 42
    test <@ idle |> Lifecycle.value = 42 @>
    let running = idle |> Lifecycle.start
    test <@ running |> Lifecycle.value = 42 @>
    let idle2 = running |> Lifecycle.complete 99
    test <@ idle2 |> Lifecycle.value = 99 @>
