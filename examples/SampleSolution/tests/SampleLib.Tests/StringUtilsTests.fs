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
