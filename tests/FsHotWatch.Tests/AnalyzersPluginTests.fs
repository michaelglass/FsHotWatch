module FsHotWatch.Tests.AnalyzersPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Plugin
open FsHotWatch.Analyzers.AnalyzersPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = AnalyzersPlugin([]) :> IFsHotWatchPlugin
    test <@ plugin.Name = "analyzers" @>
