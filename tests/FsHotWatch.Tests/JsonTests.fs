module FsHotWatch.Tests.JsonTests

open System.Text.Json
open Xunit
open Swensen.Unquote

let private parse (json: string) = JsonDocument.Parse(json).RootElement

[<Fact(Timeout = 5000)>]
let ``asking a non-object for a field answers that it has none`` () =
    // `JsonElement.TryGetProperty` throws on these instead.
    for json in [ "\"text\""; "42"; "[1,2]"; "null" ] do
        let el = parse json
        test <@ FsHotWatch.Json.tryProp el "x" = None @>
        test <@ FsHotWatch.Json.tryString el "x" = None @>
        test <@ FsHotWatch.Json.tryInt el "x" = None @>
        test <@ FsHotWatch.Json.tryInt64 el "x" = None @>

[<Fact(Timeout = 5000)>]
let ``a field of the wrong kind or out of range reads as absent`` () =
    let el = parse """{"s":"a","n":7,"big":9999999999,"frac":1.5}"""

    test <@ FsHotWatch.Json.tryString el "s" = Some "a" @>
    test <@ FsHotWatch.Json.tryString el "n" = None @>
    test <@ FsHotWatch.Json.tryInt el "n" = Some 7 @>
    test <@ FsHotWatch.Json.tryInt el "s" = None @>
    test <@ FsHotWatch.Json.tryInt el "big" = None @>
    test <@ FsHotWatch.Json.tryInt64 el "big" = Some 9999999999L @>
    test <@ FsHotWatch.Json.tryInt el "frac" = None @>
    test <@ FsHotWatch.Json.tryInt64 el "frac" = None @>
    test <@ FsHotWatch.Json.tryInt el "missing" = None @>

[<Fact(Timeout = 5000)>]
let ``a boolean field reads as its value, and any other kind as absent`` () =
    let el = parse """{"t":true,"f":false,"s":"true","n":1}"""

    test <@ FsHotWatch.Json.tryBool el "t" = Some true @>
    test <@ FsHotWatch.Json.tryBool el "f" = Some false @>
    test <@ FsHotWatch.Json.tryBool el "s" = None @>
    test <@ FsHotWatch.Json.tryBool el "n" = None @>
    test <@ FsHotWatch.Json.tryBool el "missing" = None @>

[<Fact(Timeout = 5000)>]
let ``a document is read when it parses, and is absent when it does not`` () =
    let readS =
        FsHotWatch.Json.tryReadDocument (fun root -> FsHotWatch.Json.tryString root "s")

    test <@ readS """{"s":"a"}""" = Some "a" @>
    test <@ readS """{"n":1}""" = None @>
    test <@ readS "not json" = None @>

[<Fact(Timeout = 5000)>]
let ``an exception other than a parse failure propagates from a document read`` () =
    let readS =
        FsHotWatch.Json.tryReadDocument (fun root -> FsHotWatch.Json.tryString root "s")

    raises<System.ArgumentNullException> <@ readS null @>

    raises<System.InvalidOperationException>
        <@ FsHotWatch.Json.tryReadDocument (fun _ -> invalidOp "a reader's own failure") "{}" @>
