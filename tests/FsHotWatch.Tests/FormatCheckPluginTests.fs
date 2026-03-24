module FsHotWatch.Tests.FormatCheckPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Plugin
open FsHotWatch.Fantomas.FormatCheckPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = FormatCheckPlugin() :> IFsHotWatchPlugin
    test <@ plugin.Name = "format-check" @>
