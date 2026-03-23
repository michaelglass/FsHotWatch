module TestPrune.Tests.DeadCodeTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.DeadCode

let private tempDbPath () =
    Path.Combine(Path.GetTempPath(), $"test-prune-%A{Guid.NewGuid()}.db")

let private withDb (f: Database -> unit) =
    let path = tempDbPath ()

    try
        let db = Database.create path
        f db
    finally
        if File.Exists path then
            File.Delete path

        let walPath = path + "-wal"
        let shmPath = path + "-shm"

        if File.Exists walPath then
            File.Delete walPath

        if File.Exists shmPath then
            File.Delete shmPath

module ``All symbols reachable`` =

    [<Fact>]
    let ``entry point reaches everything — empty unreachable list`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "App.Lib.helper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.helper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildForProject("App", graph)

            let result = findDeadCode db [ "*.Program.main" ]

            test <@ result.UnreachableSymbols = [] @>
            test <@ result.TotalSymbols = 2 @>
            test <@ result.ReachableSymbols = 2 @>)

module ``Unreachable function detected`` =

    [<Fact>]
    let ``function with no callers from entry point is unreachable`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "App.Lib.usedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "App.Lib.unusedHelper"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12 } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.usedHelper"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildForProject("App", graph)

            let result = findDeadCode db [ "*.Program.main" ]

            test <@ result.UnreachableSymbols.Length = 1 @>
            test <@ result.UnreachableSymbols[0].FullName = "App.Lib.unusedHelper" @>)

module ``Transitive reachability`` =

    [<Fact>]
    let ``entry to funcA to funcB to TypeC — all reachable`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "App.Lib.funcA"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "App.Lib.funcB"
                        Kind = Function
                        SourceFile = "src/App/Lib.fs"
                        LineStart = 7
                        LineEnd = 12 }
                      { FullName = "App.Domain.TypeC"
                        Kind = Type
                        SourceFile = "src/App/Domain.fs"
                        LineStart = 1
                        LineEnd = 3 } ]
                  Dependencies =
                    [ { FromSymbol = "App.Program.main"
                        ToSymbol = "App.Lib.funcA"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.funcA"
                        ToSymbol = "App.Lib.funcB"
                        Kind = Calls }
                      { FromSymbol = "App.Lib.funcB"
                        ToSymbol = "App.Domain.TypeC"
                        Kind = UsesType } ]
                  TestMethods = [] }

            db.RebuildForProject("App", graph)

            let result = findDeadCode db [ "*.Program.main" ]

            test <@ result.UnreachableSymbols = [] @>
            test <@ result.ReachableSymbols = 4 @>)

module ``Test methods excluded`` =

    [<Fact>]
    let ``test methods not reported as dead code`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "Tests.MyTests.testSomething"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTests.testSomething"
                        TestProject = "Tests"
                        TestClass = "MyTests"
                        TestMethod = "testSomething" } ] }

            db.RebuildForProject("App", graph)

            let result = findDeadCode db [ "*.Program.main" ]

            // Test method is unreachable from production entry but should be excluded
            test <@ result.UnreachableSymbols = [] @>)

module ``Module symbols excluded`` =

    [<Fact>]
    let ``modules are containers and not reported as dead code`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "App.OldModule"
                        Kind = Module
                        SourceFile = "src/App/OldModule.fs"
                        LineStart = 1
                        LineEnd = 20 }
                      { FullName = "App.OldModule.orphanFunc"
                        Kind = Function
                        SourceFile = "src/App/OldModule.fs"
                        LineStart = 3
                        LineEnd = 8 } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildForProject("App", graph)

            let result = findDeadCode db [ "*.Program.main" ]

            // Module excluded, but the orphan function should still be reported
            let names = result.UnreachableSymbols |> List.map (fun s -> s.FullName)
            test <@ names = [ "App.OldModule.orphanFunc" ] @>)

module ``Test file symbols excluded`` =

    [<Fact>]
    let ``anything in tests/ is excluded from dead code report`` () =
        withDb (fun db ->
            let graph =
                { Symbols =
                    [ { FullName = "App.Program.main"
                        Kind = Function
                        SourceFile = "src/App/Program.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "TestHelpers.setup"
                        Kind = Function
                        SourceFile = "tests/TestHelpers.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildForProject("App", graph)

            let result = findDeadCode db [ "*.Program.main" ]

            // TestHelpers.setup is in tests/ directory, should be excluded
            test <@ result.UnreachableSymbols = [] @>)
