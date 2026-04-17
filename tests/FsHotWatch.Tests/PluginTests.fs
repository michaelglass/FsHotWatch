module FsHotWatch.Tests.PluginTests

open Xunit
open Swensen.Unquote
open FsHotWatch.PluginFramework

[<Fact(Timeout = 5000)>]
let ``plugin has a name`` () =
    let handler =
        { Name = PluginName.create "fake"
          Init = ()
          Update = fun _ctx state _event -> async { return state }
          Commands = []
          Subscriptions = PluginSubscriptions.none
          CacheKey = None
          Teardown = None }

    Assert.Equal(PluginName.create "fake", handler.Name)
