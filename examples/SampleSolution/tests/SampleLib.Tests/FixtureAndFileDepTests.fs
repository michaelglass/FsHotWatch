module SampleLib.Tests.FixtureAndFileDepTests

open System
open System.IO
open Xunit
open TestPrune
open SampleLib.StringUtils

/// Shared state for class-based tests. With TestPrune's aggregate-type
/// invalidation, editing any member of this fixture (including `setup`
/// or `sampleInput` that specific tests don't reference) invalidates
/// every test in classes that take it as a ctor parameter.
type StringFixture() =
    let cached = "hello"

    member _.sampleInput = cached
    member _.setup() = ()
    member _.expectedReverse = "olleh"

/// Class-based tests receiving the fixture via the primary constructor.
/// TestPrune emits direct `testMethod -> StringFixture` edges so the tests
/// are selected even when their body doesn't read the fixture directly.
type FixtureConsumingTests(fixture: StringFixture) =

    [<Fact>]
    member _.``reverse matches fixture expectation``() =
        Assert.Equal(fixture.expectedReverse, reverse fixture.sampleInput)

    [<Fact>]
    member _.``independent of fixture body``() =
        // This test never touches `fixture.*`. Aggregate-type invalidation plus
        // the direct ctor-param edge still ensures an edit to `StringFixture`
        // pulls this test back in.
        Assert.True(isPalindrome "racecar")

/// Declarative dependency on a non-F# file. Editing `data/golden.json`
/// invalidates this test even though no F# symbol changed.
[<DependsOnFile("examples/SampleSolution/tests/SampleLib.Tests/data/golden.json")>]
[<Fact>]
let ``reverse matches golden snapshot`` () =
    let goldenPath = Path.Combine(AppContext.BaseDirectory, "data", "golden.json")

    let json = File.ReadAllText(goldenPath)
    // Minimal parse — real code would use a JSON library.
    Assert.Contains("\"olleh\"", json)
    Assert.Equal("olleh", reverse "hello")
