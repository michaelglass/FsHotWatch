module SampleLib.Tests.StringUtilsTests

open Xunit
open SampleLib.StringUtils

[<Fact>]
let ``reverse reverses a string`` () = Assert.Equal("olleh", reverse "hello")

[<Fact>]
let ``isPalindrome detects palindromes`` () = Assert.True(isPalindrome "racecar")

[<Fact>]
let ``isPalindrome rejects non-palindromes`` () = Assert.False(isPalindrome "hello")

[<Fact>]
let ``wordCount counts words`` () =
    Assert.Equal(3, wordCount "hello world foo")

[<Fact>]
let ``wordCount returns 0 for empty string`` () = Assert.Equal(0, wordCount "")

/// Class-based tests (type members) — exercises the type member tracking path
/// in TestPrune's impact analysis. Changes to `reverse` or `isPalindrome`
/// should select these tests via the transitive dependency graph.
type PalindromeTests() =
    [<Fact>]
    member _.``reverse of single char is itself``() = Assert.Equal("a", reverse "a")

    [<Fact>]
    member _.``isPalindrome handles single char``() = Assert.True(isPalindrome "a")
