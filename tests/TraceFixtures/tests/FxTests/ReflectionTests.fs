module FxTests.ReflectionTests

open Xunit
open FxLib.Shapes

[<Fact>]
let ``the helper names every colour`` () =
    Assert.Equal<string[]>([| "Red"; "Green" |], Colour.names ())

[<Fact>]
let ``a value's cases are named`` () =
    Assert.Equal<string[]>([| "Red"; "Green" |], caseNamesOf (box Red))

[<Fact>]
let ``no type in FxLib is forbidden (sweep)`` () =
    Assert.DoesNotContain(typeof<Colour>.Assembly.GetTypes(), fun t -> t.Name.StartsWith "Forbidden")

[<Fact>]
let ``the audit finds nothing forbidden`` () = Assert.Equal(0, audit ())

[<Fact>]
let ``a test that never reflects`` () = Assert.Equal(2, 1 + 1)
