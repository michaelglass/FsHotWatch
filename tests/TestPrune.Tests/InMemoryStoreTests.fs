module TestPrune.Tests.InMemoryStoreTests

open Xunit
open Swensen.Unquote
open TestPrune.InMemoryStore
open TestPrune.Tests.TestHelpers

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

    [<Fact>]
    let ``GetSymbolsInFile returns empty for unknown file`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let symbols = store.GetSymbolsInFile "nonexistent.fs"
        test <@ symbols |> List.isEmpty @>

    [<Fact>]
    let ``GetDependenciesFromFile returns deps for file with deps`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let deps = store.GetDependenciesFromFile "tests/Tests.fs"
        test <@ deps.Length = 1 @>
        test <@ deps[0].ToSymbol = "Lib.funcB" @>

    [<Fact>]
    let ``GetDependenciesFromFile returns empty for file with no deps`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let deps = store.GetDependenciesFromFile "nonexistent.fs"
        test <@ deps |> List.isEmpty @>

    [<Fact>]
    let ``GetTestMethodsInFile returns tests for test file`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let tests = store.GetTestMethodsInFile "tests/Tests.fs"
        test <@ tests.Length = 1 @>
        test <@ tests[0].TestMethod = "testA" @>

    [<Fact>]
    let ``GetTestMethodsInFile returns empty for non-test file`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let tests = store.GetTestMethodsInFile "src/Lib.fs"
        test <@ tests |> List.isEmpty @>

    [<Fact>]
    let ``GetAllSymbolNames returns all symbol names`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let names = store.GetAllSymbolNames()
        test <@ names.Count = 4 @>
        test <@ names.Contains "Lib.funcB" @>
        test <@ names.Contains "Tests.testA" @>

    [<Fact>]
    let ``GetTestMethodSymbolNames returns test method names`` () =
        let store = fromAnalysisResults [ standardGraph ]
        let testNames = store.GetTestMethodSymbolNames()
        test <@ testNames.Count = 1 @>
        test <@ testNames.Contains "Tests.testA" @>

    [<Fact>]
    let ``empty analysis results produce empty store`` () =
        let store = fromAnalysisResults []
        test <@ store.GetAllSymbols() |> List.isEmpty @>
        test <@ store.GetAllSymbolNames().Count = 0 @>
        test <@ store.GetTestMethodSymbolNames().Count = 0 @>
        test <@ store.GetSymbolsInFile "any.fs" |> List.isEmpty @>
        test <@ store.GetDependenciesFromFile "any.fs" |> List.isEmpty @>
        test <@ store.GetTestMethodsInFile "any.fs" |> List.isEmpty @>
        test <@ store.QueryAffectedTests [ "Any.symbol" ] |> List.isEmpty @>
        test <@ store.GetReachableSymbols [] |> Set.isEmpty @>
