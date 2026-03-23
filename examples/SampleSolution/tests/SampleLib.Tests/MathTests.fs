module SampleLib.Tests.MathTests

open Xunit
open SampleLib.Math

[<Fact>]
let ``add returns sum`` () = Assert.Equal(5, add 2 3)

[<Fact>]
let ``multiply returns product`` () = Assert.Equal(12, multiply 3 4)

[<Fact>]
let ``factorial of 0 is 1`` () = Assert.Equal(1, factorial 0)

[<Fact>]
let ``factorial of 5 is 120`` () = Assert.Equal(120, factorial 5)
