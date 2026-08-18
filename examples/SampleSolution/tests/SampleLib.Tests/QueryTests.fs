module SampleLib.Tests.QueryTests

open Xunit
open SampleLib.Query

[<Fact>]
let ``doubleEvens keeps the even items and doubles them`` () =
    let actual = doubleEvens [ 1; 2; 3; 4; 5 ]
    Assert.Equal<int list>([ 4; 8 ], actual)

[<Fact>]
let ``doubleEvens on an empty list is empty`` () =
    let actual = doubleEvens []
    Assert.Equal<int list>([], actual)
