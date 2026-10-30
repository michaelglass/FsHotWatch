module TestPrune.Tests.SyntheticRouteContractSkipped

open Xunit

// Stand-in for the ADR 0001 drift-alarm contract test (`SyntheticRouteContractTests.fs`), compiled
// ONLY when the Falco.UnionRoutes sibling checkout is absent — see `FalcoUnionRoutesAvailable` in
// TestPrune.Tests.fsproj. The alarm's oracle (`Route.enumerate`) is unreleased, so it needs a local
// checkout of that repo; requiring one would break a standalone clone of TestPrune, and silently
// dropping the test would let a green run overstate what it checked. This marker keeps the omission
// visible in the test report instead.

[<Fact(Skip = "Falco.UnionRoutes sibling checkout not present; clone it next to TestPrune to run the ADR 0001 drift alarm.")>]
let ``AST composition matches Falco.UnionRoutes canonical (drift alarm)`` () = ()
