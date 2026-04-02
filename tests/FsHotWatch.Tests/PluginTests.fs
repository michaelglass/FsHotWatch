module FsHotWatch.Tests.PluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.PluginFramework

[<Fact>]
let ``plugin has a name`` () =
    let handler =
        { Name = "fake"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None }

    Assert.Equal("fake", handler.Name)
