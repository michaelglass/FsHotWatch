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
