module FsHotWatch.Tests.WatcherTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Watcher

[<Fact>]
let ``hello returns project name`` () =
    test <@ hello () = "FsHotWatch" @>
