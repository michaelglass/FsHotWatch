module TestPrune.Tests.ImpactAnalysisTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.ImpactAnalysis
open TestPrune.InMemoryStore
open TestPrune.Tests.TestHelpers

module ``Changed symbol with dependent test`` =

    [<Fact>]
    let ``direct dependency returns the test`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // funcB changed
        let currentSymbols =
            Map.ofList
                [ "src/Lib.fs",
                  [ { FullName = "Lib.funcB"
                      Kind = Function
                      SourceFile = "src/Lib.fs"
                      LineStart = 1
                      LineEnd = 10
                      ContentHash = "changed"
                      IsExtern = false } ] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols

        match result with
        | RunSubset tests ->
            test <@ tests.Length = 1 @>
            test <@ tests[0].TestMethod = "testA" @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``Changed symbol with transitive dependent test`` =

    [<Fact>]
    let ``transitive dependency returns the test`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // TypeC changed
        let currentSymbols =
            Map.ofList
                [ "src/Domain.fs",
                  [ { FullName = "Domain.TypeC"
                      Kind = Type
                      SourceFile = "src/Domain.fs"
                      LineStart = 1
                      LineEnd = 8
                      ContentHash = "changed"
                      IsExtern = false } ] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Domain.fs" ] currentSymbols

        match result with
        | RunSubset tests ->
            test <@ tests.Length = 1 @>
            test <@ tests[0].TestMethod = "testA" @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``Changed symbol with no dependent tests`` =

    [<Fact>]
    let ``production-only code returns empty subset`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // unrelated changed
        let currentSymbols =
            Map.ofList
                [ "src/Other.fs",
                  [ { FullName = "Other.unrelated"
                      Kind = Function
                      SourceFile = "src/Other.fs"
                      LineStart = 1
                      LineEnd = 10
                      ContentHash = "changed"
                      IsExtern = false } ] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Other.fs" ] currentSymbols

        match result with
        | RunSubset tests -> test <@ tests |> List.isEmpty @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``Multiple changed symbols`` =

    [<Fact>]
    let ``union of all affected tests`` () =
        let graph =
            { Symbols =
                [ { FullName = "Tests.test1"
                    Kind = Function
                    SourceFile = "tests/Tests.fs"
                    LineStart = 1
                    LineEnd = 3
                    ContentHash = ""
                    IsExtern = false }
                  { FullName = "Tests.test2"
                    Kind = Function
                    SourceFile = "tests/Tests.fs"
                    LineStart = 5
                    LineEnd = 8
                    ContentHash = ""
                    IsExtern = false }
                  { FullName = "Lib.funcA"
                    Kind = Function
                    SourceFile = "src/Lib.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = ""
                    IsExtern = false }
                  { FullName = "Lib.funcB"
                    Kind = Function
                    SourceFile = "src/Lib.fs"
                    LineStart = 7
                    LineEnd = 12
                    ContentHash = ""
                    IsExtern = false } ]
              Dependencies =
                [ { FromSymbol = "Tests.test1"
                    ToSymbol = "Lib.funcA"
                    Kind = Calls }
                  { FromSymbol = "Tests.test2"
                    ToSymbol = "Lib.funcB"
                    Kind = Calls } ]
              TestMethods =
                [ { SymbolFullName = "Tests.test1"
                    TestProject = "MyTests"
                    TestClass = "Tests"
                    TestMethod = "test1" }
                  { SymbolFullName = "Tests.test2"
                    TestProject = "MyTests"
                    TestClass = "Tests"
                    TestMethod = "test2" } ]
              Diagnostics = AnalysisDiagnostics.Zero }

        let store = fromAnalysisResults [ graph ]

        // Both funcA and funcB changed
        let currentSymbols =
            Map.ofList
                [ "src/Lib.fs",
                  [ { FullName = "Lib.funcA"
                      Kind = Function
                      SourceFile = "src/Lib.fs"
                      LineStart = 1
                      LineEnd = 8
                      ContentHash = "changed-a"
                      IsExtern = false }
                    { FullName = "Lib.funcB"
                      Kind = Function
                      SourceFile = "src/Lib.fs"
                      LineStart = 10
                      LineEnd = 18
                      ContentHash = "changed-b"
                      IsExtern = false } ] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols

        match result with
        | RunSubset tests ->
            test <@ tests.Length = 2 @>

            let methods = tests |> List.map (fun t -> t.TestMethod) |> Set.ofList

            test <@ methods = set [ "test1"; "test2" ] @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``No changes`` =

    [<Fact>]
    let ``empty changed files returns empty subset`` () =
        let store = fromAnalysisResults [ standardGraph ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [] Map.empty

        match result with
        | RunSubset tests -> test <@ tests |> List.isEmpty @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``New file not indexed`` =

    [<Fact>]
    let ``new file triggers RunAll`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // brand new file with symbols, not in DB
        let currentSymbols =
            Map.ofList
                [ "src/NewModule.fs",
                  [ { FullName = "NewModule.newFunc"
                      Kind = Function
                      SourceFile = "src/NewModule.fs"
                      LineStart = 1
                      LineEnd = 5
                      ContentHash = ""
                      IsExtern = false } ] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/NewModule.fs" ] currentSymbols

        match result with
        | RunAll _ -> ()
        | RunSubset _ -> failwith "Expected RunAll for new file"

module ``fsproj changed`` =

    [<Fact>]
    let ``fsproj change triggers RunAll`` () =
        let store = fromAnalysisResults [ standardGraph ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/MyProject.fsproj" ] Map.empty

        match result with
        | RunAll(FsprojChanged _) -> ()
        | other -> failwith $"Expected RunAll(FsprojChanged _), got %A{other}"

module ``Empty changed files`` =

    [<Fact>]
    let ``empty list returns empty subset`` () =
        let store = fromAnalysisResults [ standardGraph ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [] Map.empty

        match result with
        | RunSubset tests -> test <@ tests |> List.isEmpty @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``File with no stored symbols and no current symbols`` =

    [<Fact>]
    let ``both stored and current symbols empty returns empty subset`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // "src/Empty.fs" was never indexed (no stored symbols) and has no current symbols either
        let currentSymbols = Map.ofList [ "src/Empty.fs", [] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Empty.fs" ] currentSymbols

        match result with
        | RunSubset tests -> test <@ tests |> List.isEmpty @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``File that had symbols but now has none`` =

    [<Fact>]
    let ``all symbols removed from file detects removals and returns affected tests`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // src/Lib.fs has stored symbols (Lib.funcB) but current symbols list is empty — all removed
        let currentSymbols = Map.ofList [ "src/Lib.fs", [] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols

        match result with
        | RunSubset tests ->
            test <@ tests.Length = 1 @>
            test <@ tests[0].TestMethod = "testA" @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``Unchanged file in changed list`` =

    [<Fact>]
    let ``file with unchanged symbol hashes returns empty subset`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // src/Lib.fs is in changedFiles but symbols have identical hashes (empty → empty)
        let currentSymbols =
            Map.ofList
                [ "src/Lib.fs",
                  [ { FullName = "Lib.funcB"
                      Kind = Function
                      SourceFile = "src/Lib.fs"
                      LineStart = 1
                      LineEnd = 5
                      ContentHash = ""
                      IsExtern = false } ] ]

        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols

        match result with
        | RunSubset tests -> test <@ tests |> List.isEmpty @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``File absent from current symbols map`` =

    [<Fact>]
    let ``changed file not in currentSymbolsByFile and not stored returns empty subset`` () =
        let store = fromAnalysisResults [ standardGraph ]

        // "src/Unknown.fs" is in changedFiles but absent from currentSymbolsByFile AND not in store
        let result, _events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Unknown.fs" ] Map.empty

        match result with
        | RunSubset tests -> test <@ tests |> List.isEmpty @>
        | RunAll reason -> failwith $"Expected RunSubset, got RunAll: %s{SelectionReason.describe reason}"

module ``Event emission`` =

    [<Fact>]
    let ``symbol change emits SymbolChangeDetectedEvent`` () =
        let store = fromAnalysisResults [ standardGraph ]

        let currentSymbols =
            Map.ofList
                [ "src/Lib.fs",
                  [ { FullName = "Lib.funcB"
                      Kind = Function
                      SourceFile = "src/Lib.fs"
                      LineStart = 1
                      LineEnd = 10
                      ContentHash = "changed"
                      IsExtern = false } ] ]

        let _result, events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/Lib.fs" ] currentSymbols

        let symbolChangeEvents =
            events
            |> List.choose (fun e ->
                match e with
                | SymbolChangeDetectedEvent(file, name, kind) -> Some(file, name, kind)
                | _ -> None)

        test <@ symbolChangeEvents.Length >= 1 @>

        test
            <@
                symbolChangeEvents
                |> List.exists (fun (file, name, _kind) -> file = "src/Lib.fs" && name = "Lib.funcB")
            @>

    [<Fact>]
    let ``fsproj change emits no symbol events`` () =
        let store = fromAnalysisResults [ standardGraph ]

        let _result, events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [ "src/MyProject.fsproj" ] Map.empty

        let symbolChangeEvents =
            events
            |> List.choose (fun e ->
                match e with
                | SymbolChangeDetectedEvent _ -> Some e
                | _ -> None)

        test <@ symbolChangeEvents |> List.isEmpty @>

    [<Fact>]
    let ``empty changes emits no events`` () =
        let store = fromAnalysisResults [ standardGraph ]

        let _result, events =
            selectTests store.GetSymbolsInFile store.QueryAffectedTests [] Map.empty

        test <@ events |> List.isEmpty @>
