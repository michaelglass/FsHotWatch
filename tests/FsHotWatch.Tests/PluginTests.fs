module FsHotWatch.Tests.PluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Plugin

type FakePlugin() =
    let mutable initialized = false

    interface IFsHotWatchPlugin with
        member _.Name = "fake"
        member _.Initialize(_ctx) = initialized <- true
        member _.Dispose() = ()

    member _.IsInitialized = initialized

[<Fact>]
let ``plugin has a name`` () =
    let plugin = FakePlugin() :> IFsHotWatchPlugin
    test <@ plugin.Name = "fake" @>
