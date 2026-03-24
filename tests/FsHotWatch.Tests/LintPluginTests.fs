module FsHotWatch.Tests.LintPluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Plugin
open FsHotWatch.Lint.LintPlugin

[<Fact>]
let ``plugin has correct name`` () =
    let plugin = LintPlugin() :> IFsHotWatchPlugin
    test <@ plugin.Name = "lint" @>
