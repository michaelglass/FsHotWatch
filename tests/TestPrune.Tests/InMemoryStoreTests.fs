module TestPrune.Tests.InMemoryStoreTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.InMemoryStore

/// Standard test graph: testA -> funcB -> TypeC
let private standardGraph =
    { Symbols =
        [ { FullName = "Tests.testA"
            Kind = Function
            SourceFile = "tests/Tests.fs"
            LineStart = 1
            LineEnd = 5
            ContentHash = "" }
          { FullName = "Lib.funcB"
            Kind = Function
            SourceFile = "src/Lib.fs"
            LineStart = 1
            LineEnd = 5
            ContentHash = "" }
          { FullName = "Domain.TypeC"
            Kind = Type
            SourceFile = "src/Domain.fs"
            LineStart = 1
            LineEnd = 3
            ContentHash = "" }
          { FullName = "Other.unrelated"
            Kind = Function
            SourceFile = "src/Other.fs"
            LineStart = 1
            LineEnd = 5
            ContentHash = "" } ]
      Dependencies =
        [ { FromSymbol = "Tests.testA"
            ToSymbol = "Lib.funcB"
            Kind = Calls }
          { FromSymbol = "Lib.funcB"
            ToSymbol = "Domain.TypeC"
            Kind = UsesType } ]
      TestMethods =
        [ { SymbolFullName = "Tests.testA"
            TestProject = "MyTests"
            TestClass = "Tests"
            TestMethod = "testA" } ] }

module ``InMemoryStore basics`` =

    [<Fact>]
    let ``GetSymbolsInFile returns symbols for that file`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let symbols = store.GetSymbolsInFile "src/Lib.fs"
        test <@ symbols.Length = 1 @>
        test <@ symbols[0].FullName = "Lib.funcB" @>

    [<Fact>]
    let ``QueryAffectedTests finds direct dependent test`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let tests = store.QueryAffectedTests [ "Lib.funcB" ]
        test <@ tests.Length = 1 @>
        test <@ tests[0].TestMethod = "testA" @>

    [<Fact>]
    let ``QueryAffectedTests finds transitive dependent test`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let tests = store.QueryAffectedTests [ "Domain.TypeC" ]
        test <@ tests.Length = 1 @>
        test <@ tests[0].TestMethod = "testA" @>

    [<Fact>]
    let ``GetReachableSymbols follows forward edges`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let reachable = store.GetReachableSymbols [ "Tests.testA" ]
        test <@ reachable.Contains "Lib.funcB" @>
        test <@ reachable.Contains "Domain.TypeC" @>

    [<Fact>]
    let ``GetAllSymbols returns all symbols`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let all = store.GetAllSymbols()
        test <@ all.Length = 4 @>

    [<Fact>]
    let ``QueryAffectedTests with unknown symbol returns empty`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let tests = store.QueryAffectedTests [ "NonExistent.func" ]
        test <@ tests |> List.isEmpty @>
