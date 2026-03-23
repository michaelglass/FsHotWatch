module TestPrune.Tests.DatabaseTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Database

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

        // SQLite WAL/SHM files
        let walPath = path + "-wal"
        let shmPath = path + "-shm"

        if File.Exists walPath then
            File.Delete walPath

        if File.Exists shmPath then
            File.Delete shmPath

module ``Create initializes schema`` =

    [<Fact>]
    let ``create db and query without error`` () =
        withDb (fun db ->
            let symbols = db.GetSymbolsInFile "nonexistent.fs"
            test <@ symbols = [] @>

            let names = db.GetAllSymbolNames()
            test <@ names = Set.empty @>

            let affected = db.QueryAffectedTests []
            test <@ affected = [] @>)

module ``Store and retrieve symbols`` =

    [<Fact>]
    let ``insert via RebuildForProject and query back via GetSymbolsInFile`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "MyModule.myFunc"
                        Kind = Function
                        SourceFile = "src/MyModule.fs"
                        LineStart = 5
                        LineEnd = 10 }
                      { FullName = "MyModule.MyType"
                        Kind = Type
                        SourceFile = "src/MyModule.fs"
                        LineStart = 12
                        LineEnd = 20 } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildForProject("MyProject", result)

            let symbols = db.GetSymbolsInFile "src/MyModule.fs"
            test <@ symbols.Length = 2 @>
            test <@ symbols[0].FullName = "MyModule.myFunc" @>
            test <@ symbols[0].Kind = Function @>
            test <@ symbols[0].LineStart = 5 @>
            test <@ symbols[1].FullName = "MyModule.MyType" @>
            test <@ symbols[1].Kind = Type @>)

module ``Transitive dependency query`` =

    [<Fact>]
    let ``testA depends on funcB depends on TypeC — changing TypeC returns testA`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "Domain.TypeC"
                        Kind = Type
                        SourceFile = "src/Domain.fs"
                        LineStart = 1
                        LineEnd = 3 } ]
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

            db.RebuildForProject("MyProject", result)

            let affected = db.QueryAffectedTests [ "Domain.TypeC" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>
            test <@ affected[0].TestClass = "Tests" @>
            test <@ affected[0].TestProject = "MyTests" @>)

module ``Direct dependency`` =

    [<Fact>]
    let ``testA depends on funcB — changing funcB returns testA`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ] }

            db.RebuildForProject("MyProject", result)

            let affected = db.QueryAffectedTests [ "Lib.funcB" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>)

module ``No dependency`` =

    [<Fact>]
    let ``change symbol that no test depends on returns empty`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.testA"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5 }
                      { FullName = "Other.unrelated"
                        Kind = Function
                        SourceFile = "src/Other.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ] }

            db.RebuildForProject("MyProject", result)

            let affected = db.QueryAffectedTests [ "Other.unrelated" ]
            test <@ affected = [] @>)

module ``RebuildForProject replaces old data`` =

    [<Fact>]
    let ``rebuild twice only latest data present`` () =
        withDb (fun db ->
            let result1 =
                { Symbols =
                    [ { FullName = "Mod.oldFunc"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildForProject("MyProject", result1)

            let result2 =
                { Symbols =
                    [ { FullName = "Mod.newFunc"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildForProject("MyProject", result2)

            let symbols = db.GetSymbolsInFile "src/Mod.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].FullName = "Mod.newFunc" @>

            let allNames = db.GetAllSymbolNames()
            test <@ allNames |> Set.contains "Mod.oldFunc" |> not @>
            test <@ allNames |> Set.contains "Mod.newFunc" @>)

module ``Multiple tests depending on same symbol`` =

    [<Fact>]
    let ``returns all tests`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Tests.test1"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 1
                        LineEnd = 3 }
                      { FullName = "Tests.test2"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 5
                        LineEnd = 7 }
                      { FullName = "Lib.sharedFunc"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5 } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.test1"
                        ToSymbol = "Lib.sharedFunc"
                        Kind = Calls }
                      { FromSymbol = "Tests.test2"
                        ToSymbol = "Lib.sharedFunc"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.test1"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test1" }
                      { SymbolFullName = "Tests.test2"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "test2" } ] }

            db.RebuildForProject("MyProject", result)

            let affected = db.QueryAffectedTests [ "Lib.sharedFunc" ]
            test <@ affected.Length = 2 @>

            let methods = affected |> List.map (fun t -> t.TestMethod) |> Set.ofList
            test <@ methods = set [ "test1"; "test2" ] @>)

module ``GetAllSymbolNames`` =

    [<Fact>]
    let ``returns the full set of names`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "A.one"
                        Kind = Function
                        SourceFile = "src/A.fs"
                        LineStart = 1
                        LineEnd = 3 }
                      { FullName = "B.two"
                        Kind = Type
                        SourceFile = "src/B.fs"
                        LineStart = 1
                        LineEnd = 3 }
                      { FullName = "C.three"
                        Kind = Value
                        SourceFile = "src/C.fs"
                        LineStart = 1
                        LineEnd = 3 } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildForProject("MyProject", result)

            let names = db.GetAllSymbolNames()
            test <@ names = set [ "A.one"; "B.two"; "C.three" ] @>)
