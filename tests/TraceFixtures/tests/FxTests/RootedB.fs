/// A test module whose module-level value `library` is computed from `SuiteRoot.root`. Its
/// test reads only this file's other value, `sum`: reading it runs this file's initializer,
/// which computes `library` too. So the test depends on SuiteRoot's probe only through that
/// initializer. RootedA and RootedB have the same shape, so at least one of them initializes
/// after `SuiteRoot` has: SuiteRoot's initializer does not run inside theirs.
module FxTests.RootedB

open System.IO
open Xunit

let private library = Path.Combine(SuiteRoot.root, "src", "FxLib")

let private sum = List.sum [ 2; 2 ]

[<Fact>]
let ``rooted b reads its own module value`` () = Assert.Equal(4, sum)
