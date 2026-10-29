/// ADR-059 §1: one content key for every unit of verifiable work. Equal inputs give an equal
/// key in any order; any moved input moves the key and is NAMED; no encoding ambiguity lets
/// two different input sets share a key.
module FsHotWatch.Tests.ActionKeyTests

open Xunit
open Swensen.Unquote
open FsHotWatch

[<Fact>]
let ``a key is a function of its kind and labelled inputs only, not their order`` () =
    let a =
        ActionKey.create "test" [ "tree", "sha256:t"; "project", "tests/A/A.fsproj" ]

    let b =
        ActionKey.create "test" [ "project", "tests/A/A.fsproj"; "tree", "sha256:t" ]

    test <@ a.Value = b.Value @>
    test <@ a.Inputs = b.Inputs @>

[<Fact>]
let ``a key value is the documented prefix and a lowercase sha256`` () =
    let k = ActionKey.create "plugin" [ "name", "lint" ]
    test <@ k.Value.StartsWith ActionKey.Prefix @>
    test <@ k.Value.Length = ActionKey.Prefix.Length + 64 @>
    test <@ ActionKey.isValue k.Value @>
    test <@ not (ActionKey.isValue "ak1:xyz") @>
    test <@ not (ActionKey.isValue (k.Value.ToUpperInvariant())) @>

[<Fact>]
let ``the recipe is the documented byte stream`` () =
    let expected = "fshw-action-v1\nplugin\n4:name|4:lint|4:tree|8:sha256:t|"

    test <@ ActionKey.encode "plugin" [ "tree", "sha256:t"; "name", "lint" ] = expected @>

    test
        <@
            (ActionKey.create "plugin" [ "tree", "sha256:t"; "name", "lint" ]).Value = ActionKey.Prefix
                                                                                       + ContentHash.ofText expected
        @>

[<Fact>]
let ``the kind is part of the key`` () =
    let inputs = [ "tree", "sha256:t" ]

    test
        <@
            (ActionKey.create "plugin" inputs).Value
            <> (ActionKey.create "test" inputs).Value
        @>

[<Fact>]
let ``a moved input moves the key`` () =
    let a = ActionKey.create "test" [ "tree", "sha256:t1"; "scope", "full" ]
    let b = ActionKey.create "test" [ "tree", "sha256:t2"; "scope", "full" ]
    test <@ a.Value <> b.Value @>

[<Fact>]
let ``length prefixes keep a label-value split from colliding`` () =
    let a = ActionKey.create "k" [ "x", "ab"; "y", "" ]
    let b = ActionKey.create "k" [ "x", "a"; "y", "b" ]
    let c = ActionKey.create "k" [ "x|1:y", "" ]
    test <@ a.Value <> b.Value @>
    test <@ a.Value <> c.Value @>

[<Fact>]
let ``an added input moves the key`` () =
    let a = ActionKey.create "test" [ "tree", "sha256:t" ]
    let b = ActionKey.create "test" [ "tree", "sha256:t"; "scope", "" ]
    test <@ a.Value <> b.Value @>

[<Fact>]
let ``a duplicate label is refused, never silently merged`` () =
    raises<System.ArgumentException> <@ ActionKey.create "test" [ "tree", "a"; "tree", "b" ] @>

[<Fact>]
let ``an empty kind or a kind with a line break is refused`` () =
    raises<System.ArgumentException> <@ ActionKey.create "" [ "tree", "a" ] @>
    raises<System.ArgumentException> <@ ActionKey.create "a\nb" [ "tree", "a" ] @>

[<Fact>]
let ``moved names every label whose digest differs, including one present on one side only`` () =
    let stored = [ "tree", "1"; "scope", "full"; "gone", "x" ]
    let current = [ "tree", "2"; "scope", "full"; "new", "y" ]
    test <@ ActionKey.moved stored current = [ "new"; "tree"; "gone" ] @>
    test <@ List.isEmpty (ActionKey.moved current current) @>
