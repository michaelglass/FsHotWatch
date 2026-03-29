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
