module TestPrune.Tests.DomainTests

open System
open Xunit
open Swensen.Unquote
open TestPrune.Domain

[<Fact>]
let ``AnalysisError.describe ParseFailed includes file and errors`` () =
    let err = ParseFailed("src/Foo.fs", [ "unexpected token"; "missing =" ])
    let msg = AnalysisError.describe err
    test <@ msg.Contains("src/Foo.fs") @>
    test <@ msg.Contains("unexpected token") @>
    test <@ msg.Contains("missing =") @>

[<Fact>]
let ``AnalysisError.describe CheckerAborted includes file`` () =
    let err = CheckerAborted "src/Bar.fs"
    let msg = AnalysisError.describe err
    test <@ msg.Contains("src/Bar.fs") @>

[<Fact>]
let ``AnalysisError.describe DiffProviderFailed includes reason`` () =
    let err = DiffProviderFailed "jj not found"
    let msg = AnalysisError.describe err
    test <@ msg.Contains("jj not found") @>

[<Fact>]
let ``AnalysisError.describe ProjectBuildFailed includes project and exit code`` () =
    let err = ProjectBuildFailed("MyProject.fsproj", 1)
    let msg = AnalysisError.describe err
    test <@ msg.Contains("MyProject.fsproj") @>
    test <@ msg.Contains("1") @>

[<Fact>]
let ``AnalysisError.describe DatabaseError includes operation`` () =
    let err = DatabaseError("insert", exn "disk full")
    let msg = AnalysisError.describe err
    test <@ msg.Contains("insert") @>
    test <@ msg.Contains("disk full") @>

[<Fact>]
let ``SelectionReason.describe SymbolChanged includes symbol and kind`` () =
    let reason = SymbolChanged("MyModule.myFunc", Modified)
    let msg = SelectionReason.describe reason
    test <@ msg.Contains("MyModule.myFunc") @>
    test <@ msg.Contains("Modified") @>

[<Fact>]
let ``SelectionReason.describe MultipleChanges includes symbol names`` () =
    let reason = MultipleChanges [ "A"; "B"; "C" ]
    let msg = SelectionReason.describe reason
    test <@ msg.Contains("A, B, C") @>

[<Fact>]
let ``SelectionReason.describe TransitiveDependency includes chain`` () =
    let reason = TransitiveDependency [ "A"; "B"; "C" ]
    let msg = SelectionReason.describe reason
    test <@ msg.Contains("A -> B -> C") @>

[<Fact>]
let ``SelectionReason.describe FsprojChanged includes file`` () =
    let reason = FsprojChanged "MyProject.fsproj"
    let msg = SelectionReason.describe reason
    test <@ msg.Contains("MyProject.fsproj") @>

[<Fact>]
let ``SelectionReason.describe NewFileNotIndexed includes file`` () =
    let reason = NewFileNotIndexed "src/New.fs"
    let msg = SelectionReason.describe reason
    test <@ msg.Contains("src/New.fs") @>

[<Fact>]
let ``SelectionReason.describe AnalysisFailedFallback includes file`` () =
    let reason = AnalysisFailedFallback "src/Broken.fs"
    let msg = SelectionReason.describe reason
    test <@ msg.Contains("src/Broken.fs") @>

[<Fact>]
let ``SelectionReason.describe FileDependencyChanged includes path and symbol`` () =
    let reason = FileDependencyChanged("data/cases.json", "Tests.snap")
    let msg = SelectionReason.describe reason
    test <@ msg.Contains("data/cases.json") @>
    test <@ msg.Contains("Tests.snap") @>

module ``AttributeArgs round-trip`` =

    open TestPrune.AttributeArgs

    [<Fact>]
    let ``every string reads back as itself`` () =
        let values: obj list =
            [ "data\\cases.json"; "a\"b"; "a,b"; "line\nbreak"; "café"; ""; "[\"x\"]" ]

        let expected = values |> List.map (fun v -> Text(v :?> string))
        test <@ parse (serialize values) = Ok expected @>

    [<Fact>]
    let ``non-string constants keep their JSON text and are not strings`` () =
        let values: obj list = [ box 42; box true; null; box 1.5; box 'c' ]
        let parsed = parse (serialize values)

        test
            <@
                parsed = Ok
                    [ Constant "42"
                      Constant "true"
                      Constant "null"
                      Constant "1.5"
                      Constant """{"value":"c"}""" ]
            @>

        test <@ parsed |> Result.map strings = Ok [] @>

    [<Fact>]
    let ``texts keeps every argument in order, strings unquoted`` () =
        let parsed = parse (serialize [ box "t"; box 7 ])
        test <@ parsed |> Result.map texts = Ok [ "t"; "7" ] @>

    [<Fact>]
    let ``a payload that is not a JSON array is an error naming it`` () =
        for payload in [ "not-json"; "{}"; "[\"unterminated" ] do
            match parse payload with
            | Error reason -> test <@ reason.Contains payload @>
            | Ok args -> failwith $"%s{payload} parsed as %A{args}"

    [<Fact>]
    let ``numbers of every width are JSON numbers; a non-finite one is spelled as a value`` () =
        let values: obj list = [ box 3L; box 7UL; box 2.5m; box 1.5f; box nan ]

        test
            <@
                parse (serialize values) = Ok
                    [ Constant "3"
                      Constant "7"
                      Constant "2.5"
                      Constant "1.5"
                      Constant """{"value":"NaN"}""" ]
            @>
