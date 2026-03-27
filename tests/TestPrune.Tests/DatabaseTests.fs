module TestPrune.Tests.DatabaseTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
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

let private openRawConnection (dbPath: string) =
    let conn = new SqliteConnection($"Data Source=%s{dbPath}")
    conn.Open()
    conn

module ``Create initializes schema`` =

    [<Fact>]
    let ``create db and query without error`` () =
        withDb (fun db ->
            let symbols = db.GetSymbolsInFile "nonexistent.fs"
            test <@ symbols |> List.isEmpty @>

            let names = db.GetAllSymbolNames()
            test <@ names = Set.empty @>

            let affected = db.QueryAffectedTests []
            test <@ affected |> List.isEmpty @>)

module ``Store and retrieve symbols`` =

    [<Fact>]
    let ``insert via RebuildProjects and query back via GetSymbolsInFile`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "MyModule.myFunc"
                        Kind = Function
                        SourceFile = "src/MyModule.fs"
                        LineStart = 5
                        LineEnd = 10
                        ContentHash = "" }
                      { FullName = "MyModule.MyType"
                        Kind = Type
                        SourceFile = "src/MyModule.fs"
                        LineStart = 12
                        LineEnd = 20
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "MyProject", result ])

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

            db.RebuildProjects([ "MyProject", result ])

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
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ] }

            db.RebuildProjects([ "MyProject", result ])

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
                        LineEnd = 5
                        ContentHash = "" }
                      { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
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
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ] }

            db.RebuildProjects([ "MyProject", result ])

            let affected = db.QueryAffectedTests [ "Other.unrelated" ]
            test <@ affected |> List.isEmpty @>)

module ``RebuildProjects replaces old data`` =

    [<Fact>]
    let ``rebuild twice only latest data present`` () =
        withDb (fun db ->
            let result1 =
                { Symbols =
                    [ { FullName = "Mod.oldFunc"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "MyProject", result1 ])

            let result2 =
                { Symbols =
                    [ { FullName = "Mod.newFunc"
                        Kind = Function
                        SourceFile = "src/Mod.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "MyProject", result2 ])

            let symbols = db.GetSymbolsInFile "src/Mod.fs"
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].FullName = "Mod.newFunc" @>

            let allNames = db.GetAllSymbolNames()
            test <@ allNames |> Set.contains "Mod.oldFunc" |> not @>
            test <@ allNames |> Set.contains "Mod.newFunc" @>)

module ``Cross-project dependencies`` =

    [<Fact>]
    let ``cross-project dep edges survive regardless of list order`` () =
        withDb (fun db ->
            // Project A defines the symbol
            let projectA =
                "ProjectA",
                { Symbols =
                    [ { FullName = "LibModule.helper"
                        Kind = Function
                        SourceFile = "src/Lib/Helper.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            // Project B has a test that depends on A's symbol
            let projectB =
                "ProjectB",
                { Symbols =
                    [ { FullName = "Tests.MyTests.test1"
                        Kind = Function
                        SourceFile = "tests/MyTests.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.MyTests.test1"
                        ToSymbol = "LibModule.helper"
                        Kind = Calls } ]
                  TestMethods =
                    [ { SymbolFullName = "Tests.MyTests.test1"
                        TestProject = "ProjectB"
                        TestClass = "Tests.MyTests"
                        TestMethod = "test1" } ] }

            // Pass B before A — the old API would silently drop the edge
            db.RebuildProjects([ projectB; projectA ])

            let affected = db.QueryAffectedTests [ "LibModule.helper" ]
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "test1" @>)

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
                        LineEnd = 3
                        ContentHash = "" }
                      { FullName = "Tests.test2"
                        Kind = Function
                        SourceFile = "tests/Tests.fs"
                        LineStart = 5
                        LineEnd = 7
                        ContentHash = "" }
                      { FullName = "Lib.sharedFunc"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
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

            db.RebuildProjects([ "MyProject", result ])

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
                        LineEnd = 3
                        ContentHash = "" }
                      { FullName = "B.two"
                        Kind = Type
                        SourceFile = "src/B.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = "" }
                      { FullName = "C.three"
                        Kind = Value
                        SourceFile = "src/C.fs"
                        LineStart = 1
                        LineEnd = 3
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "MyProject", result ])

            let names = db.GetAllSymbolNames()
            test <@ names = set [ "A.one"; "B.two"; "C.three" ] @>)

module ``Route handler round-trip`` =

    [<Fact>]
    let ``RebuildRouteHandlers and GetAllRouteHandlers returns inserted entries`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)

            let all = db.GetAllRouteHandlers()
            test <@ all.Length = 3 @>

            let patterns = all |> List.map (fun e -> e.UrlPattern) |> Set.ofList
            test <@ patterns = set [ "/api/users"; "/api/orders" ] @>

            let methods = all |> List.map (fun e -> e.HttpMethod) |> Set.ofList
            test <@ methods = set [ "GET"; "POST" ] @>)

    [<Fact>]
    let ``GetUrlPatternsForSourceFile returns patterns for a given source file`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)

            let patterns = db.GetUrlPatternsForSourceFile("src/UsersHandler.fs")
            test <@ patterns.Length = 2 @>
            test <@ patterns |> List.contains "/api/users" @>

            let ordersPatterns = db.GetUrlPatternsForSourceFile("src/OrdersHandler.fs")
            test <@ ordersPatterns = [ "/api/orders" ] @>

            let none = db.GetUrlPatternsForSourceFile("src/NotAHandler.fs")
            test <@ none |> List.isEmpty @>)

    [<Fact>]
    let ``GetAllHandlerSourceFiles returns distinct source files`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/users"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/UsersHandler.fs" }
                  { UrlPattern = "/api/orders"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OrdersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)

            let files = db.GetAllHandlerSourceFiles()
            test <@ files = set [ "src/UsersHandler.fs"; "src/OrdersHandler.fs" ] @>)

    [<Fact>]
    let ``RebuildRouteHandlers replaces all previous entries`` () =
        withDb (fun db ->
            let first =
                [ { UrlPattern = "/old/route"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/OldHandler.fs" } ]

            db.RebuildRouteHandlers(first)

            let second =
                [ { UrlPattern = "/new/route"
                    HttpMethod = "POST"
                    HandlerSourceFile = "src/NewHandler.fs" } ]

            db.RebuildRouteHandlers(second)

            let all = db.GetAllRouteHandlers()
            test <@ all.Length = 1 @>
            test <@ all[0].UrlPattern = "/new/route" @>
            test <@ all[0].HandlerSourceFile = "src/NewHandler.fs" @>

            let files = db.GetAllHandlerSourceFiles()
            test <@ files |> Set.contains "src/OldHandler.fs" |> not @>
            test <@ files |> Set.contains "src/NewHandler.fs" @>)

    [<Fact>]
    let ``RebuildRouteHandlers with empty list clears all entries`` () =
        withDb (fun db ->
            let entries =
                [ { UrlPattern = "/api/users"
                    HttpMethod = "GET"
                    HandlerSourceFile = "src/UsersHandler.fs" } ]

            db.RebuildRouteHandlers(entries)
            db.RebuildRouteHandlers([])

            let all = db.GetAllRouteHandlers()
            test <@ all |> List.isEmpty @>

            let files = db.GetAllHandlerSourceFiles()
            test <@ files = Set.empty @>)

module ``Empty database queries`` =

    [<Fact>]
    let ``GetAllSymbols returns empty on fresh database`` () =
        withDb (fun db ->
            let symbols = db.GetAllSymbols()
            test <@ symbols |> List.isEmpty @>)

    [<Fact>]
    let ``GetTestMethodSymbolNames returns empty on fresh database`` () =
        withDb (fun db ->
            let names = db.GetTestMethodSymbolNames()
            test <@ names = Set.empty @>)

    [<Fact>]
    let ``GetReachableSymbols with non-empty roots on empty database returns empty`` () =
        withDb (fun db ->
            let reachable = db.GetReachableSymbols([ "nonexistent" ])
            test <@ reachable = Set.empty @>)

    [<Fact>]
    let ``GetAllRouteHandlers returns empty on fresh database`` () =
        withDb (fun db ->
            let handlers = db.GetAllRouteHandlers()
            test <@ handlers |> List.isEmpty @>)

    [<Fact>]
    let ``GetAllHandlerSourceFiles returns empty on fresh database`` () =
        withDb (fun db ->
            let files = db.GetAllHandlerSourceFiles()
            test <@ files = Set.empty @>)

module ``stringToSymbolKind fallback`` =

    [<Fact>]
    let ``unknown kind string falls back to Value`` () =
        let path = tempDbPath ()

        try
            let db = Database.create path

            // Insert a symbol row with an unknown kind string directly via raw SQL
            use conn = openRawConnection path

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                INSERT INTO symbols (full_name, kind, source_file, line_start, line_end, indexed_at)
                VALUES ('Foo.bar', 'UnknownKind', 'src/Foo.fs', 1, 5, '2024-01-01T00:00:00Z')
                """

            cmd.ExecuteNonQuery() |> ignore

            let symbols = db.GetSymbolsInFile("src/Foo.fs")
            test <@ symbols.Length = 1 @>
            test <@ symbols[0].Kind = Value @>
        finally
            if File.Exists path then
                File.Delete path

            if File.Exists(path + "-wal") then
                File.Delete(path + "-wal")

            if File.Exists(path + "-shm") then
                File.Delete(path + "-shm")

module ``stringToDepKind fallback`` =

    [<Fact>]
    let ``unknown dep kind string falls back to References`` () =
        let path = tempDbPath ()

        try
            let db = Database.create path

            // Seed two symbols so we can insert a dependency between them
            let result =
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
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ] }

            db.RebuildProjects([ "MyProject", result ])

            // Insert a dependency with an unknown dep_kind directly via raw SQL
            use conn = openRawConnection path

            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                INSERT INTO dependencies (from_symbol_id, to_symbol_id, dep_kind)
                SELECT f.id, t.id, 'unknown_dep_kind'
                FROM symbols f, symbols t
                WHERE f.full_name = 'Tests.testA' AND t.full_name = 'Lib.funcB'
                """

            cmd.ExecuteNonQuery() |> ignore

            // QueryAffectedTests traverses dependencies; the unknown kind should fall back to
            // References, meaning testA is still returned as affected by funcB
            let affected = db.QueryAffectedTests([ "Lib.funcB" ])
            test <@ affected.Length = 1 @>
            test <@ affected[0].TestMethod = "testA" @>
        finally
            if File.Exists path then
                File.Delete path

            if File.Exists(path + "-wal") then
                File.Delete(path + "-wal")

            if File.Exists(path + "-shm") then
                File.Delete(path + "-shm")

module ``Project key storage`` =

    [<Fact>]
    let ``GetProjectKey returns None when no hash stored`` () =
        withDb (fun db ->
            let hash = db.GetProjectKey "MyProject"
            test <@ hash = None @>)

    [<Fact>]
    let ``SetProjectKey then GetProjectKey round-trips`` () =
        withDb (fun db ->
            db.SetProjectKey("MyProject", "abc123")
            let hash = db.GetProjectKey "MyProject"
            test <@ hash = Some "abc123" @>)

    [<Fact>]
    let ``SetProjectKey overwrites previous hash`` () =
        withDb (fun db ->
            db.SetProjectKey("MyProject", "old-hash")
            db.SetProjectKey("MyProject", "new-hash")
            let hash = db.GetProjectKey "MyProject"
            test <@ hash = Some "new-hash" @>)

    [<Fact>]
    let ``hashes are per-project`` () =
        withDb (fun db ->
            db.SetProjectKey("ProjectA", "hash-a")
            db.SetProjectKey("ProjectB", "hash-b")
            test <@ db.GetProjectKey "ProjectA" = Some "hash-a" @>
            test <@ db.GetProjectKey "ProjectB" = Some "hash-b" @>)

module ``File key storage`` =

    [<Fact>]
    let ``GetFileKey returns None when no key stored`` () =
        withDb (fun db ->
            let key = db.GetFileKey "src/Mod.fs"
            test <@ key = None @>)

    [<Fact>]
    let ``SetFileKey then GetFileKey round-trips`` () =
        withDb (fun db ->
            db.SetFileKey("src/Mod.fs", "abc123")
            test <@ db.GetFileKey "src/Mod.fs" = Some "abc123" @>)

    [<Fact>]
    let ``SetFileKey overwrites previous key`` () =
        withDb (fun db ->
            db.SetFileKey("src/Mod.fs", "old")
            db.SetFileKey("src/Mod.fs", "new")
            test <@ db.GetFileKey "src/Mod.fs" = Some "new" @>)

    [<Fact>]
    let ``keys are per-file`` () =
        withDb (fun db ->
            db.SetFileKey("src/A.fs", "key-a")
            db.SetFileKey("src/B.fs", "key-b")
            test <@ db.GetFileKey "src/A.fs" = Some "key-a" @>
            test <@ db.GetFileKey "src/B.fs" = Some "key-b" @>)

module ``GetDependenciesFromFile`` =

    [<Fact>]
    let ``returns dependencies originating from the given file`` () =
        withDb (fun db ->
            let result =
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
                        ContentHash = "" } ]
                  Dependencies =
                    [ { FromSymbol = "Tests.testA"
                        ToSymbol = "Lib.funcB"
                        Kind = Calls } ]
                  TestMethods = [] }

            db.RebuildProjects([ "MyProject", result ])

            let deps = db.GetDependenciesFromFile "tests/Tests.fs"
            test <@ deps.Length = 1 @>
            test <@ deps[0].FromSymbol = "Tests.testA" @>
            test <@ deps[0].ToSymbol = "Lib.funcB" @>
            test <@ deps[0].Kind = Calls @>)

    [<Fact>]
    let ``returns empty for file with no outgoing dependencies`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "MyProject", result ])

            let deps = db.GetDependenciesFromFile "src/Lib.fs"
            test <@ deps |> List.isEmpty @>)

module ``GetTestMethodsInFile`` =

    [<Fact>]
    let ``returns test methods defined in the given file`` () =
        withDb (fun db ->
            let result =
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
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods =
                    [ { SymbolFullName = "Tests.testA"
                        TestProject = "MyTests"
                        TestClass = "Tests"
                        TestMethod = "testA" } ] }

            db.RebuildProjects([ "MyProject", result ])

            let tests = db.GetTestMethodsInFile "tests/Tests.fs"
            test <@ tests.Length = 1 @>
            test <@ tests[0].TestMethod = "testA" @>
            test <@ tests[0].TestClass = "Tests" @>
            test <@ tests[0].TestProject = "MyTests" @>)

    [<Fact>]
    let ``returns empty for non-test file`` () =
        withDb (fun db ->
            let result =
                { Symbols =
                    [ { FullName = "Lib.funcB"
                        Kind = Function
                        SourceFile = "src/Lib.fs"
                        LineStart = 1
                        LineEnd = 5
                        ContentHash = "" } ]
                  Dependencies = []
                  TestMethods = [] }

            db.RebuildProjects([ "MyProject", result ])

            let tests = db.GetTestMethodsInFile "src/Lib.fs"
            test <@ tests |> List.isEmpty @>)
