module FsHotWatch.Bench.Tests.KeyCostTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

[<Fact>]
let ``a replacement splits at the first arrow`` () =
    test <@ KeyCost.parseReplacement "a => b=>c" = Ok("a ", " b=>c") @>

[<Fact>]
let ``a replacement without an arrow or with nothing to replace is refused`` () =
    test <@ Result.isError (KeyCost.parseReplacement "no arrow") @>
    test <@ Result.isError (KeyCost.parseReplacement "=>after") @>
