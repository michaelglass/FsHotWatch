module FsHotWatch.Tests.TestPrunePluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Plugin
open FsHotWatch.TestPrune.TestPrunePlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = TestPrunePlugin(":memory:", "/tmp") :> IFsHotWatchPlugin
    test <@ plugin.Name = "test-prune" @>
