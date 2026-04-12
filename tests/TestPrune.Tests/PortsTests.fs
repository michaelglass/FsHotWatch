module TestPrune.Tests.PortsTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Ports
open TestPrune.Tests.TestHelpers

module ``SymbolStore from Database`` =

    [<Fact>]
    let ``store wraps database GetSymbolsInFile`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "Lib.func"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "abc"
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            db.RebuildProjects([ graph ])
            let store = toSymbolStore db
            let symbols = store.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].FullName = "Lib.func" @>)

    [<Fact>]
    let ``store wraps database QueryAffectedTests`` () =
        withDb (fun db ->
            let store = toSymbolStore db
            let tests = store.QueryAffectedTests [ "nonexistent" ]
            test <@ tests |> List.isEmpty @>)

module ``SymbolSink from Database`` =

    [<Fact>]
    let ``sink wraps database RebuildProjects`` () =
        withDb (fun db ->
            let sink = toSymbolSink db
            let store = toSymbolStore db

            let graph =
                { Symbols =
                    [ { FullName = "Lib.func"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "abc"
                        IsExtern = false } ]
                  Dependencies = []
                  TestMethods = []
                  Attributes = []
                  Diagnostics = AnalysisDiagnostics.Zero }

            sink.RebuildProjects [ graph ] [] []
            let symbols = store.GetSymbolsInFile "src/Lib.fs"
            test <@ symbols.Length = 1 @>)
