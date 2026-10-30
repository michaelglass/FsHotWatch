module SampleWeb.IntegrationTests.MathApiTests

open Xunit
open SampleWeb.Handlers

/// Tests for the /api/math/{a}/{b}/add route
module AddRouteTests =

    [<Fact>]
    let ``GET /api/math/2/3/add returns 5`` () = Assert.Equal(5, handleAdd 2 3)

    [<Fact>]
    let ``GET /api/math/0/0/add returns 0`` () = Assert.Equal(0, handleAdd 0 0)

/// Tests for the /api/math/{a}/{b}/multiply route
module MultiplyRouteTests =

    [<Fact>]
    let ``GET /api/math/3/4/multiply returns 12`` () = Assert.Equal(12, handleMultiply 3 4)
